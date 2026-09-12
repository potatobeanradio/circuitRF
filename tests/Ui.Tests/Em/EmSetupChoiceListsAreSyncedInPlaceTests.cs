// ================================================================
//  EmSetupChoiceListsAreSyncedInPlaceTests.cs
//
//  Owner report, 2026-09-12: changing a .cem from "Full-wave planar" to "Uniform transmission
//  line" made the EM Setup panel's Conductors section fail and render red text under the Signal
//  conductor combo — "Cannot change source while update is in progress.", out of
//  Avalonia.Controls.Selection.SelectionModel.SetSource.
//
//  THE MECHANISM, because the fix looks like a tidy-up and is not:
//
//    Refresh() rebuilt ConductorLayerChoices and ReturnPlaneChoices as BRAND NEW
//    ObservableCollections and assigned them. Each assignment hands the bound ComboBox a new
//    SelectionModel.Source; Avalonia clears the selection when the source is replaced; the two-way
//    SelectedItem binding writes that cleared value back into the view model; the setter commits it
//    as an edit and calls Refresh(); and Refresh() replaces the collection again. An unbounded
//    recursion THROUGH THE CONTROL, which Avalonia's own re-entrancy guard turns into the message
//    above, rendered by the ComboBox's DataValidationErrors.
//
//    It only fires when the selection is not already the first row, because that is the row the
//    reset lands on — which is why "(infer from the drawn geometry)" looked fine and a named
//    conductor did not. Verified both ways against the reported board before the fix.
//
//  Two SILENT losses shared the same cause and are gated here too: picking a signal conductor did
//  not stick (the write-back cleared it straight back to "(infer …)"), and a named return plane was
//  discarded by any analysis-kind change — the one setting R-rp1-2 exists to keep visible.
//
//  These tests assert the STRUCTURAL property that prevents all of it — the collections are synced
//  in place and their instances outlive every Refresh — rather than the control behaviour, because
//  this suite may not call Avalonia runtime APIs (CircuitRF.Ui.Tests.csproj states the rule). The
//  loop itself was reproduced in a headless Avalonia host driving the real EmSetupEditorView, on
//  both the reported board and testdata/antenna; that host is not something this project can carry.
// ================================================================

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public sealed class EmSetupChoiceListsAreSyncedInPlaceTests : IDisposable
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "crf-emchoices-" + Guid.NewGuid().ToString("N")[..12]);

    public EmSetupChoiceListsAreSyncedInPlaceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    /// <summary>Four layers, so that the signal-conductor list has a SECOND row to select — the
    /// whole fault is invisible while the selection sits on the first one. Top Copper (1 oz) and
    /// Inner 2 are the two non-ground conductors.</summary>
    private static Technology Tech() => ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");

    /// <summary>A trace on Inner 2 (drawing layer 3/0) with a port label at each end.</summary>
    private static LayoutView Layout()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        var layer = new LayerKey(3, 0);
        view.Shapes.Add(new RectShape { Layer = layer, X1 = 0, Y1 = 0, X2 = 20_000_000, Y2 = 600_000 });
        view.Shapes.Add(new LabelShape
        {
            Layer = layer, X = 0, Y = 300_000, Text = "P1", Height = 400_000, IsPort = true,
        });
        view.Shapes.Add(new LabelShape
        {
            Layer = layer, X = 20_000_000, Y = 300_000, Text = "P2", Height = 400_000, IsPort = true,
        });
        return view;
    }

    private EmSetupEditorViewModel Editor(Action<EmSetup>? seed = null)
    {
        string path = Path.Combine(_dir, "panel.cem");
        var setup = new EmSetup
        {
            Name = "panel", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
        };
        seed?.Invoke(setup);
        EmSetupPersistence.SaveToFile(path, setup);

        var vm = new EmSetupEditorViewModel(path, setup)
        {
            ResolveLayout = _ => new EmLayoutSource(
                Path.Combine(_dir, "a.clay"), Layout(), Tech(), Dbu),
        };
        vm.Refresh();
        return vm;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  The structural property: the instances outlive every Refresh
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TheChoiceCollectionInstancesSurviveEveryRefresh_IncludingAnAnalysisKindChange()
    {
        var vm = Editor();

        var conductors = vm.ConductorLayerChoices;
        var returns    = vm.ReturnPlaneChoices;

        // A plain re-extraction, the mesh-invalidating settings, and the change that was reported.
        vm.Refresh();
        vm.DispersionCorrection = !vm.DispersionCorrection;
        vm.AnalysisKind = EmAnalysisKind.CrossSection;
        vm.AnalysisKind = EmAnalysisKind.Planar;
        vm.AnalysisKind = EmAnalysisKind.Auto;
        vm.Refresh();

        Assert.Same(conductors, vm.ConductorLayerChoices);
        Assert.Same(returns,    vm.ReturnPlaneChoices);

        // Synced in place is not the same claim as never built: the rows still have to be right.
        Assert.Equal(
            [EmSetupEditorViewModel.InferSignalLayer, "Top Copper (1 oz)", "Inner 2"],
            vm.ConductorLayerChoices);
        Assert.Equal(
            [
                EmSetupEditorViewModel.AutomaticReturnPlane,
                "Top Copper (1 oz)",
                "Inner 1 (Ground Plane)  — ground reference",
                "Inner 2",
                "Bottom Copper (1 oz)  — ground reference",
            ],
            vm.ReturnPlaneChoices.Select(c => c.Display));
    }

    /// <summary>A technology that genuinely gained a conductor still reaches the panel — the sync
    /// is "leave it alone when it matches", not "never write to it".</summary>
    [Fact]
    public void AChangedTechnologyStillUpdatesTheRows_InTheSameInstance()
    {
        string path = Path.Combine(_dir, "grow.cem");
        var setup = new EmSetup { Name = "grow", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar };
        EmSetupPersistence.SaveToFile(path, setup);

        var tech = Tech();
        var vm = new EmSetupEditorViewModel(path, setup)
        {
            ResolveLayout = _ => new EmLayoutSource(Path.Combine(_dir, "a.clay"), Layout(), tech, Dbu),
        };
        vm.Refresh();

        var conductors = vm.ConductorLayerChoices;
        Assert.DoesNotContain("Inner 3", vm.ConductorLayerChoices);

        tech.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "Inner 3", ThicknessDbu = 35_000, SigmaSm = 5.8e7,
        });
        vm.Refresh();

        Assert.Same(conductors, vm.ConductorLayerChoices);
        Assert.Contains("Inner 3", vm.ConductorLayerChoices);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  The two settings the loop was silently destroying
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ANamedSignalConductorSurvivesTheAnalysisKindChangeThatWasReported()
    {
        var vm = Editor(s => s.SignalStackupLayerName = "Inner 2");
        Assert.Equal("Inner 2", vm.SignalLayerChoice);

        vm.AnalysisKind = EmAnalysisKind.CrossSection;

        Assert.Equal("Inner 2", vm.SignalLayerChoice);
        Assert.Equal("Inner 2", vm.Working.SignalStackupLayerName);
    }

    [Fact]
    public void ANamedReturnPlaneSurvivesTheSameChange()
    {
        var vm = Editor(s => s.GroundStackupLayerName = "Inner 1 (Ground Plane)");
        Assert.Equal("Inner 1 (Ground Plane)", vm.ReturnPlaneChoice?.Name);

        vm.AnalysisKind = EmAnalysisKind.CrossSection;

        Assert.Equal("Inner 1 (Ground Plane)", vm.Working.GroundStackupLayerName);
        Assert.Equal("Inner 1 (Ground Plane)", vm.ReturnPlaneChoice?.Name);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  The belt to the braces: a cleared selection is the control talking, not an edit
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ANullFromTheControlCommitsNothing_OnEitherCombo()
    {
        var vm = Editor(s =>
        {
            s.SignalStackupLayerName = "Inner 2";
            s.GroundStackupLayerName = "Inner 1 (Ground Plane)";
        });

        bool dirtyBefore = vm.IsDirty;

        // Exactly what a ComboBox writes back when its selection is cleared.
        vm.SignalLayerChoice = null!;
        vm.ReturnPlaneChoice = null;

        Assert.Equal("Inner 2", vm.Working.SignalStackupLayerName);
        Assert.Equal("Inner 1 (Ground Plane)", vm.Working.GroundStackupLayerName);
        Assert.Equal(dirtyBefore, vm.IsDirty);
        Assert.False(vm.UndoRedo.CanUndo);
    }
}

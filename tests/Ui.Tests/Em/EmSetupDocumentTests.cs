// Tier D — the .cem document (brief-L6-L7-em-ui.md §7).
//
// R-em-9: a .cem is workspace-scoped and NEVER scratch, so there is no materialize/offer-a-target
// path to test — the whole shape mirrors TechDocument, including snapshot undo.

using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class EmSetupDocumentTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "crf-em-" + Guid.NewGuid().ToString("N")[..12]);

    public EmSetupDocumentTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private string Path_(string name) => System.IO.Path.Combine(_dir, name);

    /// <summary>Every setting non-default — the round trip is only worth anything if nothing is
    /// left sitting on its own default and passing by accident.</summary>
    private static EmSetup FullyNonDefault() => new()
    {
        Name                   = "MyEmSetup",
        LayoutRef              = "Amp/layout/Amp.clay",
        SignalStackupLayerName = "Metal1",
        Frequency              = new FrequencySpec("2", "18", "250", SweepKind.Log, "GHz", "GHz", "MHz"),
        Port1Z0                = new Complex(75, -12.5),
        Port2Z0                = new Complex(40, 3),
        Mesh                   = new EmMeshSettings(9, 5, 0.017, 1.85, 33.5, 17),
        DispersionCorrection   = true,
        SnpOutputPathOverride  = "results/custom-name",
    };

    [Fact]
    public void EverySettingRoundTripsThroughCem()
    {
        var path = Path_("full.cem");
        var original = FullyNonDefault();
        EmSetupPersistence.SaveToFile(path, original);

        var loaded = EmSetupPersistence.LoadFromFile(path);

        Assert.Equal(original.Name, loaded.Name);
        Assert.Equal(original.LayoutRef, loaded.LayoutRef);
        Assert.Equal(original.SignalStackupLayerName, loaded.SignalStackupLayerName);
        Assert.Equal(original.Port1Z0, loaded.Port1Z0);
        Assert.Equal(original.Port2Z0, loaded.Port2Z0);
        Assert.Equal(original.Mesh, loaded.Mesh);
        Assert.Equal(original.DispersionCorrection, loaded.DispersionCorrection);
        Assert.Equal(original.SnpOutputPathOverride, loaded.SnpOutputPathOverride);

        Assert.Equal(original.Frequency.StartExpr, loaded.Frequency.StartExpr);
        Assert.Equal(original.Frequency.StopExpr,  loaded.Frequency.StopExpr);
        Assert.Equal(original.Frequency.StepExpr,  loaded.Frequency.StepExpr);
        Assert.Equal(original.Frequency.Mode,      loaded.Frequency.Mode);
        Assert.Equal(original.Frequency.Kind,      loaded.Frequency.Kind);
        Assert.Equal(original.Frequency.StartUnit, loaded.Frequency.StartUnit);
        Assert.Equal(original.Frequency.StepUnit,  loaded.Frequency.StepUnit);
    }

    [Fact]
    public void SerializingTwice_IsByteIdentical()
    {
        var setup = FullyNonDefault();
        Assert.Equal(EmSetupPersistence.Serialize(setup), EmSetupPersistence.Serialize(setup));
    }

    [Fact]
    public void ANewerFormatVersion_IsRefused_RatherThanPartiallyRead()
    {
        string json = EmSetupPersistence.Serialize(FullyNonDefault())
            .Replace("\"FormatVersion\": 1", "\"FormatVersion\": 99", StringComparison.Ordinal);
        var ex = Assert.Throws<InvalidDataException>(() => EmSetupPersistence.Deserialize(json));
        Assert.Contains("newer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── The editor VM: dirty/save/undo, mirroring the .ctech editor ───────────────────────────

    private (EmSetupEditorViewModel Vm, string Path) NewEditor(EmSetup? seed = null)
    {
        var path = Path_("edit.cem");
        var setup = seed ?? new EmSetup { Name = "edit" };
        EmSetupPersistence.SaveToFile(path, setup);
        return (new EmSetupEditorViewModel(path, setup), path);
    }

    [Fact]
    public void AFreshlyOpenedSetup_IsClean()
    {
        var (vm, _) = NewEditor();
        Assert.False(vm.IsDirty);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void EditingAMeshSetting_DirtiesUndoablyAndSavingClearsIt()
    {
        var (vm, path) = NewEditor();

        vm.MinCellsAcrossWidthText = "11";
        vm.CommitMeshField(nameof(EmMeshSettings.MinCellsAcrossWidth));

        Assert.True(vm.IsDirty);
        Assert.Equal(11, vm.Working.Mesh.MinCellsAcrossWidth);

        vm.UndoCommand.Execute(null);
        Assert.Equal(EmMeshSettings.Default.MinCellsAcrossWidth, vm.Working.Mesh.MinCellsAcrossWidth);
        Assert.False(vm.IsDirty);   // undo back to the saved baseline clears it

        vm.RedoCommand.Execute(null);
        Assert.Equal(11, vm.Working.Mesh.MinCellsAcrossWidth);

        vm.SaveCommand.Execute(null);
        Assert.False(vm.IsDirty);
        Assert.Equal(11, EmSetupPersistence.LoadFromFile(path).Mesh.MinCellsAcrossWidth);
    }

    [Fact]
    public void ANoOpEdit_PushesNoUndoEntry()
    {
        var (vm, _) = NewEditor();
        vm.MinCellsAcrossWidthText = EmMeshSettings.Default.MinCellsAcrossWidth.ToString();
        vm.CommitMeshField(nameof(EmMeshSettings.MinCellsAcrossWidth));
        Assert.False(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void APortImpedanceAcceptsAComplexValue_AndRefusesNonsenseByName()
    {
        var (vm, _) = NewEditor();

        vm.Port1Z0Text = "75-10j";
        vm.CommitPortZ0(1);
        Assert.Null(vm.Port1Z0Error);
        Assert.Equal(new Complex(75, -10), vm.Working.Port1Z0);

        vm.Port2Z0Text = "not a number";
        vm.CommitPortZ0(2);
        Assert.NotNull(vm.Port2Z0Error);
        Assert.Equal(new Complex(50, 0), vm.Working.Port2Z0);   // model untouched
    }

    [Theory]
    [InlineData("50", 50, 0)]
    [InlineData("50+10j", 50, 10)]
    [InlineData("50 - 10j", 50, -10)]
    [InlineData("75Ω", 75, 0)]
    [InlineData("-5e1+2.5j", -50, 2.5)]
    public void ComplexOhmParsing(string text, double re, double im)
    {
        Assert.True(EmSetupEditorViewModel.TryParseComplexOhms(text, out var z));
        Assert.Equal(re, z.Real, 9);
        Assert.Equal(im, z.Imaginary, 9);
    }

    // ── R-em-10: a .cem references its layout by path and degrades when it is gone ────────────

    [Fact]
    public void ASetupPointingAtAMissingLayout_SaysSoSpecifically_RatherThanThrowing()
    {
        var (vm, _) = NewEditor(new EmSetup { Name = "x", LayoutRef = "gone/layout/gone.clay" });
        vm.ResolveLayout = _ => null;
        vm.Refresh();

        Assert.Null(vm.Problem);
        Assert.Contains("gone/layout/gone.clay", vm.LayoutStatus, StringComparison.Ordinal);
        Assert.Contains("could not be found", vm.LayoutStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASetupWithNoLayoutSelectedYet_SaysSo_AndSimulateIsBlocked()
    {
        var (vm, _) = NewEditor();
        vm.Refresh();
        Assert.False(vm.CanRun);
        Assert.Contains("No layout selected", vm.LayoutStatus, StringComparison.OrdinalIgnoreCase);
    }

    // ── R-em-13: CanSolve is asked on every settings change, and its reason is live ───────────

    [Fact]
    public void TheKernelsOwnRefusal_IsShownLive_NotAtSimulateTime()
    {
        var (vm, _) = NewEditor(new EmSetup { Name = "x", LayoutRef = "a.clay" });

        // Seventeen parallel strips — one past R-gen-9's conductor ceiling. Extraction succeeds (the
        // geometry is perfectly extractable); the kernel is what refuses it.
        //
        // L7b-b changed WHAT is refused here, not that a kernel refusal shows live: an ASYMMETRIC
        // pair — this test's previous fixture — is exactly what the general modal decomposition
        // exists for and now solves, so the fixture moved to the case that is still unsupported.
        int n = QuasiStaticKernel.MaxSignalConductors + 1;
        var view = new LayoutView { DbuPerMicron = LayoutUnits.DefaultDbuPerMicron };
        for (int k = 0; k < n; k++)
        {
            long y0 = k * 800_000;
            view.Shapes.Add(new RectShape
            { Layer = new(1, 0), X1 = 0, Y1 = y0, X2 = 20_000_000, Y2 = y0 + 400_000 });
        }

        vm.ResolveLayout = _ => new EmLayoutSource(
            "/x/a.clay", view, StarterTechnologies.Pcb2Layer(), LayoutUnits.DefaultDbuPerMicron);
        vm.Refresh();

        Assert.NotNull(vm.Problem);                 // extraction succeeded
        Assert.Null(vm.ExtractionRefusal);
        Assert.NotNull(vm.KernelRefusal);           // the kernel is the one refusing
        Assert.Contains(n.ToString(), vm.KernelRefusal!, StringComparison.Ordinal);
        Assert.Contains("dense boundary-element solve", vm.KernelRefusal!, StringComparison.Ordinal);
        Assert.False(vm.CanRun);
        Assert.Equal(vm.KernelRefusal, vm.BlockingReason);
    }

    [Fact]
    public void AResolvableSingleLine_ProducesAReadbackAndAllowsSimulate()
    {
        var (vm, _) = NewEditor(new EmSetup { Name = "x", LayoutRef = "a.clay" });
        vm.ResolveLayout = _ => HeroSource();
        vm.Refresh();

        Assert.True(vm.CanRun);
        Assert.Null(vm.BlockingReason);
        Assert.NotNull(vm.Readback);
        Assert.Equal("Top Copper (1 oz)", vm.Readback!.SignalLayerName);
        Assert.NotEmpty(vm.StackupRows);
        Assert.Contains(vm.ConductorLayerChoices, c => c == "Top Copper (1 oz)");
        Assert.DoesNotContain(vm.ConductorLayerChoices, c => c == "Bottom Copper (1 oz)");   // ground
    }

    [Fact]
    public void TheDispersionOptIn_IsDisabledWithAStatedReason_WhenItDoesNotApply()
    {
        var (vm, _) = NewEditor(new EmSetup { Name = "x", LayoutRef = "a.clay" });

        vm.ResolveLayout = _ => HeroSource();
        vm.Refresh();
        Assert.Null(vm.DispersionDisabledReason);   // a single microstrip: it applies

        var coupled = new LayoutView { DbuPerMicron = LayoutUnits.DefaultDbuPerMicron };
        coupled.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 0,         X2 = 20_000_000, Y2 = 1_000_000 });
        coupled.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 1_500_000, X2 = 20_000_000, Y2 = 2_500_000 });
        vm.ResolveLayout = _ => new EmLayoutSource(
            "/x/a.clay", coupled, StarterTechnologies.Pcb2Layer(), LayoutUnits.DefaultDbuPerMicron);
        vm.Refresh();

        Assert.NotNull(vm.DispersionDisabledReason);
        Assert.Contains("single microstrip", vm.DispersionDisabledReason!, StringComparison.OrdinalIgnoreCase);
    }

    internal static EmLayoutSource HeroSource()
    {
        var view = new LayoutView { DbuPerMicron = LayoutUnits.DefaultDbuPerMicron };
        view.Shapes.Add(new RectShape
        { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = 20_000_000, Y2 = 2_900_000 });
        return new EmLayoutSource("/x/a.clay", view, StarterTechnologies.Pcb2Layer(),
                                  LayoutUnits.DefaultDbuPerMicron);
    }
}

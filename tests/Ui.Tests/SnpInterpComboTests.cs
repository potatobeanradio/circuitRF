using System.Linq;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// SnP "Interp Mode" (Linear / Cubic Spline / Makima) and "Interp Domain" (RI / MA) comboboxes in
/// the Parameter Editor's SnP custom panel — mirrors the existing PinConfig/Pitch combo wiring in
/// <see cref="ParameterEditorViewModel"/> (there was previously no UI surface for InterpMode at
/// all: the SnP panel is a hand-built custom panel, not the generic parameter-row list, so a
/// parameter absent from that custom panel has no combo/text box anywhere).
/// </summary>
public class SnpInterpComboTests
{
    private static (SchematicViewModel Vm, EditableComponent Comp, ParameterEditorViewModel Editor) MakeSnp(
        string interpMode = "CubicSpline", string interpDomain = "RI")
    {
        var model = new SchematicEditModel();
        var comp = new EditableComponent { Symbol = SymbolKind.Snp, InstanceName = "S1", X = 0, Y = 0 };
        comp.Parameters.Add(new EditableParameter { Name = "NumPorts",    Expression = "2" });
        comp.Parameters.Add(new EditableParameter { Name = "File",       Expression = "test.s2p" });
        comp.Parameters.Add(new EditableParameter { Name = "RefNode",    Expression = "false" });
        comp.Parameters.Add(new EditableParameter { Name = "PinConfig",  Expression = "Standard" });
        comp.Parameters.Add(new EditableParameter { Name = "Pitch",      Expression = "Loose" });
        comp.Parameters.Add(new EditableParameter { Name = "InterpMode", Expression = interpMode });
        comp.Parameters.Add(new EditableParameter { Name = "InterpDomain", Expression = interpDomain });
        comp.Parameters.Add(new EditableParameter { Name = "ExtrapMode", Expression = "NearestEdge" });
        model.Components.Add(comp);

        var vm = new SchematicViewModel(model);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: false);
        return (vm, comp, editor);
    }

    // ── Options ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void InterpModeOptions_AreLinearCubicSplineMakima_InThatOrder()
    {
        Assert.Equal(new[] { "Linear", "CubicSpline", "Makima" }, ParameterEditorViewModel.SnpInterpModeOptions);
    }

    [Fact]
    public void InterpDomainOptions_AreRIAndMA_InThatOrder()
    {
        Assert.Equal(new[] { "RI", "MA" }, ParameterEditorViewModel.SnpInterpDomainOptions);
    }

    // ── Refresh: stored value -> combo index ────────────────────────────────────────────────────

    [Theory]
    [InlineData("Linear", 0)]
    [InlineData("CubicSpline", 1)]
    [InlineData("Makima", 2)]
    public void SnpInterpModeIndex_ReflectsStoredValue(string stored, int expectedIndex)
    {
        var (_, _, editor) = MakeSnp(interpMode: stored);
        Assert.Equal(expectedIndex, editor.SnpInterpModeIndex);
    }

    [Fact]
    public void SnpInterpModeIndex_LegacyCubicValue_FallsBackToCubicSplineIndex()
    {
        // "Cubic" was the stored value before this brief; ComponentModelFactory still resolves it
        // to cubic spline (its default fallback), so the combo must land on the same option rather
        // than silently defaulting to Linear (index 0).
        var (_, _, editor) = MakeSnp(interpMode: "Cubic");
        Assert.Equal(1, editor.SnpInterpModeIndex); // CubicSpline
    }

    [Theory]
    [InlineData("RI", 0)]
    [InlineData("MA", 1)]
    public void SnpInterpDomainIndex_ReflectsStoredValue(string stored, int expectedIndex)
    {
        var (_, _, editor) = MakeSnp(interpDomain: stored);
        Assert.Equal(expectedIndex, editor.SnpInterpDomainIndex);
    }

    // ── Selecting a combo option commits to the model ───────────────────────────────────────────

    [Fact]
    public void SelectingMakima_CommitsInterpModeExpressionEqualsMakima()
    {
        var (_, comp, editor) = MakeSnp();
        editor.SnpInterpModeIndex = 2; // Makima
        Assert.Equal("Makima", comp.Parameters.Single(p => p.Name == "InterpMode").Expression);
    }

    [Fact]
    public void SelectingLinear_CommitsInterpModeExpressionEqualsLinear()
    {
        var (_, comp, editor) = MakeSnp();
        editor.SnpInterpModeIndex = 0; // Linear
        Assert.Equal("Linear", comp.Parameters.Single(p => p.Name == "InterpMode").Expression);
    }

    [Fact]
    public void SelectingMA_CommitsInterpDomainExpressionEqualsMA()
    {
        var (_, comp, editor) = MakeSnp();
        editor.SnpInterpDomainIndex = 1; // MA
        Assert.Equal("MA", comp.Parameters.Single(p => p.Name == "InterpDomain").Expression);
    }

    [Fact]
    public void SelectingAnOption_IsUndoable()
    {
        var (vm, comp, editor) = MakeSnp(interpMode: "Linear");
        editor.SnpInterpModeIndex = 2; // -> Makima
        Assert.Equal("Makima", comp.Parameters.Single(p => p.Name == "InterpMode").Expression);

        vm.UndoRedo.Undo();
        Assert.Equal("Linear", comp.Parameters.Single(p => p.Name == "InterpMode").Expression);
    }

    // ── NumPorts/File/other SnP params untouched by the new combos ─────────────────────────────

    [Fact]
    public void ChangingInterpDomain_LeavesInterpModeAndOtherParamsUntouched()
    {
        var (_, comp, editor) = MakeSnp(interpMode: "Makima", interpDomain: "RI");
        editor.SnpInterpDomainIndex = 1; // MA

        Assert.Equal("Makima", comp.Parameters.Single(p => p.Name == "InterpMode").Expression);
        Assert.Equal("NearestEdge", comp.Parameters.Single(p => p.Name == "ExtrapMode").Expression);
    }

    // ── Regression: an SnP placed/saved BEFORE InterpDomain existed (missing the param row) ──────
    //
    // Owner-reported: setting Interp Domain to a non-default value reverted on dialog reopen, and
    // "Domain" never showed up in the extracted netlist. Root cause: ApplySnpParam only SETS an
    // existing param row's Expression — on an instance whose Parameters never had an "InterpDomain"
    // row (any SnP placed/saved before this feature), it silently found nothing to set. The combo
    // LOOKED like it changed (SnpInterpDomainIndex is its own observable property), but nothing was
    // ever written to the component, so a fresh dialog/VM instance read the fallback default again,
    // and NetExtractor — which only emits rows that exist on comp.Parameters — had nothing to emit.
    //
    // Default is MA (owner's explicit preference, 2026-08-13) — was RI when this regression was
    // first fixed; only the default value changed, not the shape of the bug or its fix.

    private static EditableComponent MakePreExistingSnp_MissingInterpDomain(out SchematicEditModel model)
    {
        model = new SchematicEditModel();
        var comp = new EditableComponent { Symbol = SymbolKind.Snp, InstanceName = "S1", X = 0, Y = 0 };
        comp.Parameters.Add(new EditableParameter { Name = "NumPorts",   Expression = "2" });
        comp.Parameters.Add(new EditableParameter { Name = "File",       Expression = "test.s2p" });
        comp.Parameters.Add(new EditableParameter { Name = "RefNode",    Expression = "false" });
        comp.Parameters.Add(new EditableParameter { Name = "PinConfig",  Expression = "Standard" });
        comp.Parameters.Add(new EditableParameter { Name = "Pitch",      Expression = "Loose" });
        comp.Parameters.Add(new EditableParameter { Name = "InterpMode", Expression = "Cubic" });
        comp.Parameters.Add(new EditableParameter { Name = "ExtrapMode", Expression = "NearestEdge" });
        // Deliberately NO "InterpDomain" row — simulates an instance placed/saved before this
        // parameter existed.
        model.Components.Add(comp);
        return comp;
    }

    [Fact]
    public void PreExistingSnp_MissingInterpDomainRow_IsToppedUpOnDialogOpen_AtMADefault()
    {
        var comp = MakePreExistingSnp_MissingInterpDomain(out var model);
        var vm = new SchematicViewModel(model);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: false);

        var row = comp.Parameters.SingleOrDefault(p => p.Name == "InterpDomain");
        Assert.NotNull(row);
        Assert.Equal("MA", row!.Expression);
        Assert.Equal(1, editor.SnpInterpDomainIndex); // MA
    }

    [Fact]
    public void PreExistingSnp_SelectingRI_PersistsAcrossDialogReopen_AndReachesTheExtractedNetlist()
    {
        // MA is now the default the missing row gets topped up at, so this test picks the OTHER
        // option (RI) to actually exercise the write/persist path rather than a no-op.
        var comp = MakePreExistingSnp_MissingInterpDomain(out var model);
        model.Wires.Add(Wire((-200, 0), (-400, 0)));
        model.Wires.Add(Wire(( 200, 0), ( 400, 0)));
        var vm = new SchematicViewModel(model);

        // First "open": top-up runs (seeding MA), then the user picks RI.
        var editor1 = new ParameterEditorViewModel();
        editor1.SetTargetDirect(vm, comp, showClose: false);
        Assert.Equal("MA", comp.Parameters.Single(p => p.Name == "InterpDomain").Expression);
        editor1.SnpInterpDomainIndex = 0; // RI
        Assert.Equal("RI", comp.Parameters.Single(p => p.Name == "InterpDomain").Expression);

        // "Close and reopen": a brand-new VM instance targeting the SAME live component.
        var editor2 = new ParameterEditorViewModel();
        editor2.SetTargetDirect(vm, comp, showClose: false);
        Assert.Equal(0, editor2.SnpInterpDomainIndex); // still RI — not reverted to MA

        // Reaches the extracted netlist (this is what the engine actually reads).
        var cell = NetExtractor.Extract(model);
        var inst = cell.TestBench.Instances.Single(i => i.Reference == "SnP");
        Assert.Contains(inst.Overrides, o => o.Name == "InterpDomain" && o.Expression == "RI");
    }

    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }
}

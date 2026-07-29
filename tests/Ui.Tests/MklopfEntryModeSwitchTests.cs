using System.Globalization;
using System.Linq;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// MKlopf's Z1/Z2 &lt;-&gt; W1/W2 and L &lt;-&gt; F3db entry-mode switch — the missing UI
/// mechanism reported after the taper-family brief shipped ("how does the user change...?"):
/// R-klp-3/R-klp-3a's alternate entry routes were already resolved correctly by
/// <c>ComponentModelFactory</c>, but nothing let a user actually reach the alternate route through
/// the Parameter Editor. These gates exercise the real <see cref="ParameterEditorViewModel"/> wired
/// to a real <see cref="SchematicViewModel"/> (both construct headlessly — no Avalonia app host
/// needed, per src/Ui/CLAUDE.md's own note) so the parameter-list mutation, undo, and the
/// no-longer-count-only <c>OnModelChanged</c> staleness check are all exercised together, not just
/// the pure conversion math.
/// </summary>
public class MklopfEntryModeSwitchTests
{
    private static (SchematicViewModel Vm, EditableComponent Comp, ParameterEditorViewModel Editor) MakeMklopf(
        params (string Name, string Expr, string Unit)[] extraParams)
    {
        var model = new SchematicEditModel();
        var comp = new EditableComponent { Symbol = SymbolKind.Mklopf, InstanceName = "MK1", X = 0, Y = 0 };
        foreach (var (name, expr, unit) in extraParams)
            comp.Parameters.Add(new EditableParameter { Name = name, Expression = expr, Unit = unit, ShowOnSchematic = true });
        model.Components.Add(comp);

        var vm = new SchematicViewModel(model);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: false);
        return (vm, comp, editor);
    }

    private static double Val(EditableComponent comp, string name)
    {
        var p = comp.Parameters.First(x => x.Name == name);
        return double.Parse(p.Expression, CultureInfo.InvariantCulture);
    }

    private static bool Has(EditableComponent comp, string name) => comp.Parameters.Any(p => p.Name == name);

    // ── Gate: the affordance exists and is gated correctly ──────────────────────────────────────

    [Fact]
    public void IsMklopfTarget_TrueOnlyForMklopf()
    {
        var (_, _, editor) = MakeMklopf(("Z1", "50", "Ω"), ("Z2", "100", "Ω"), ("L", "20", "mm"));
        Assert.True(editor.IsMklopfTarget);

        var model = new SchematicEditModel();
        var r = new EditableComponent { Symbol = SymbolKind.Resistor, InstanceName = "R1" };
        model.Components.Add(r);
        var vm2 = new SchematicViewModel(model);
        var editor2 = new ParameterEditorViewModel();
        editor2.SetTargetDirect(vm2, r, showClose: false);
        Assert.False(editor2.IsMklopfTarget);
    }

    [Fact]
    public void DefaultPlacement_UsesImpedanceAndLengthEntry_NotWidthOrF3db()
    {
        var (_, _, editor) = MakeMklopf(("Z1", "50", "Ω"), ("Z2", "100", "Ω"), ("L", "20", "mm"));
        Assert.False(editor.MklopfUsesWidthEntry);
        Assert.False(editor.MklopfUsesF3dbEntry);
        Assert.Equal("Use W1/W2", editor.MklopfImpedanceToggleLabel);
        Assert.Equal("Use F3db", editor.MklopfLengthToggleLabel);
    }

    // ── Gate: Z1/Z2 <-> W1/W2 actually swaps the parameter set ──────────────────────────────────

    [Fact]
    public void ToggleImpedanceEntry_Z1Z2_SwitchesToW1W2_RemovingZ1Z2()
    {
        var (_, comp, editor) = MakeMklopf(("Z1", "50", "Ω"), ("Z2", "100", "Ω"), ("L", "20", "mm"));

        editor.ToggleMklopfImpedanceEntryCommand.Execute(null);

        Assert.False(Has(comp, "Z1"));
        Assert.False(Has(comp, "Z2"));
        Assert.True(Has(comp, "W1"));
        Assert.True(Has(comp, "W2"));
        Assert.True(editor.MklopfUsesWidthEntry);
        Assert.Equal("Use Z1/Z2", editor.MklopfImpedanceToggleLabel);

        // W1 (50 Ohm) must be wider than W2 (100 Ohm) on the same substrate.
        Assert.True(Val(comp, "W1") > Val(comp, "W2"));
    }

    [Fact]
    public void ToggleImpedanceEntry_RoundTrip_Z1Z2_ToW1W2_AndBack_RecoversOriginalValues()
    {
        var (_, comp, editor) = MakeMklopf(("Z1", "50", "Ω"), ("Z2", "100", "Ω"), ("L", "20", "mm"));

        editor.ToggleMklopfImpedanceEntryCommand.Execute(null); // -> W1/W2
        editor.ToggleMklopfImpedanceEntryCommand.Execute(null); // -> Z1/Z2 again

        Assert.True(Has(comp, "Z1"));
        Assert.True(Has(comp, "Z2"));
        Assert.False(Has(comp, "W1"));
        Assert.False(Has(comp, "W2"));
        Assert.Equal(50.0, Val(comp, "Z1"), 1);
        Assert.Equal(100.0, Val(comp, "Z2"), 1);
    }

    [Fact]
    public void ToggleImpedanceEntry_W1W2ToZ1Z2_ProducesTheImpedanceThoseWidthsPresent()
    {
        var (_, comp, editor) = MakeMklopf(("W1", "2.9", "mm"), ("W2", "1.0", "mm"), ("L", "20", "mm"));
        Assert.True(editor.MklopfUsesWidthEntry);

        editor.ToggleMklopfImpedanceEntryCommand.Execute(null);

        Assert.True(Has(comp, "Z1"));
        Assert.True(Has(comp, "Z2"));
        Assert.False(Has(comp, "W1"));
        // The wider trace (2.9mm) must present the LOWER impedance.
        Assert.True(Val(comp, "Z1") < Val(comp, "Z2"));
    }

    // ── Gate: L <-> F3db actually swaps the parameter set ───────────────────────────────────────

    [Fact]
    public void ToggleLengthEntry_LToF3db_RemovesL_AddsF3db()
    {
        var (_, comp, editor) = MakeMklopf(("Z1", "50", "Ω"), ("Z2", "100", "Ω"), ("L", "20", "mm"));

        editor.ToggleMklopfLengthEntryCommand.Execute(null);

        Assert.False(Has(comp, "L"));
        Assert.True(Has(comp, "F3db"));
        Assert.True(editor.MklopfUsesF3dbEntry);
        Assert.Equal("Use L", editor.MklopfLengthToggleLabel);
        Assert.True(Val(comp, "F3db") > 0);
    }

    [Fact]
    public void ToggleLengthEntry_RoundTrip_LToF3db_AndBack_RecoversApproximatelyTheOriginalLength()
    {
        var (_, comp, editor) = MakeMklopf(("Z1", "50", "Ω"), ("Z2", "100", "Ω"), ("L", "20", "mm"));

        editor.ToggleMklopfLengthEntryCommand.Execute(null); // -> F3db
        editor.ToggleMklopfLengthEntryCommand.Execute(null); // -> L again

        Assert.True(Has(comp, "L"));
        Assert.False(Has(comp, "F3db"));
        Assert.Equal(20.0, Val(comp, "L"), 1);
    }

    [Fact]
    public void ToggleLengthEntry_UsesCurrentlyActiveImpedanceRoute_EvenWhenItIsWidthEntry()
    {
        // The length toggle must resolve Z1/Z2 from whichever impedance route is ACTIVE (here,
        // W1/W2) rather than assuming Z1/Z2 always exists.
        var (_, comp, editor) = MakeMklopf(("W1", "2.9", "mm"), ("W2", "1.0", "mm"), ("L", "20", "mm"));

        editor.ToggleMklopfLengthEntryCommand.Execute(null);

        Assert.True(Has(comp, "F3db"));
        Assert.False(Has(comp, "L"));
        Assert.True(Has(comp, "W1")); // impedance route itself is untouched by the length toggle
        Assert.True(Val(comp, "F3db") > 0);
    }

    // ── Gate: undo restores the original parameter set exactly ──────────────────────────────────

    [Fact]
    public void ToggleImpedanceEntry_IsUndoable()
    {
        var (vm, comp, editor) = MakeMklopf(("Z1", "50", "Ω"), ("Z2", "100", "Ω"), ("L", "20", "mm"));

        editor.ToggleMklopfImpedanceEntryCommand.Execute(null);
        Assert.True(Has(comp, "W1"));

        vm.UndoRedo.Undo();

        Assert.False(Has(comp, "W1"));
        Assert.True(Has(comp, "Z1"));
        Assert.Equal(50.0, Val(comp, "Z1"), 1);
    }

    // ── Gate: the factory's own resolution agrees with what the switch produced ──────────────────

    [Fact]
    public void AfterSwitchingToWidthEntry_TheProducedW1W2ParametersAreConsumableByTheRealFactory()
    {
        var (_, comp, editor) = MakeMklopf(("Z1", "50", "Ω"), ("Z2", "100", "Ω"), ("GammaMax", "0.05", ""), ("L", "20", "mm"));
        editor.ToggleMklopfImpedanceEntryCommand.Execute(null); // Z1/Z2 -> W1/W2

        var parameters = comp.Parameters.ToDictionary(
            p => p.Name,
            p => new Value(double.Parse(p.Expression, CultureInfo.InvariantCulture)));

        var model = ComponentModelFactory.TryCreate("MKLOPF", parameters);
        Assert.IsType<MicrostripKlopfModel>(model);
    }
}

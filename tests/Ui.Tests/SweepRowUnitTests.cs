using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for brief-sweep-range-units: SweepAxisRowViewModel unit default + round-trip.
/// </summary>
public class SweepRowUnitTests
{
    private static SchematicEditModel ModelWithVar(string varName, string expr, string unit)
    {
        var model = new SchematicEditModel();
        var comp  = new EditableComponent { Symbol = SymbolKind.Var, X = 0, Y = 0, InstanceName = "VAR1" };
        comp.Parameters.Add(new EditableParameter { Name = varName, Expression = expr, Unit = unit });
        model.Components.Add(comp);
        return model;
    }

    private static SchematicEditModel ModelWithVars((string name, string expr, string unit)[] vars)
    {
        var model = new SchematicEditModel();
        var comp  = new EditableComponent { Symbol = SymbolKind.Var, X = 0, Y = 0, InstanceName = "VAR1" };
        foreach (var (n, e, u) in vars)
            comp.Parameters.Add(new EditableParameter { Name = n, Expression = e, Unit = u });
        model.Components.Add(comp);
        return model;
    }

    // ── Bug: stale inherited unit when re-pointing a sweep at a new variable ──────
    // Repro: a Loadpull-Pursuit freq sweep (VarName="RFfreq", inherited Unit="GHz") is edited and the
    // variable is changed to a drain-voltage var "VDD" (unit "V"). The old "GHz" unit must NOT stick and
    // scale VDD by 1e9 — units come only from the CURRENT swept variable.
    [Fact]
    public void SweepRow_ChangingVariable_DropsStaleInheritedUnit()
    {
        var model = ModelWithVars([("RFfreq", "2", "GHz"), ("VDD", "28", "V")]);

        // Restore a frequency sweep as the editor would (VarName then Unit), Unit inherited GHz.
        var vm = new SweepAxisRowViewModel(model)
        {
            VarName         = "RFfreq",
            Mode            = SweepAxisMode.StepSize,
            StartExpr       = "20",
            StopExpr        = "30",
            StepOrCountExpr = "2",
            Unit            = "GHz",   // sticky unit carried in from the prior spec / restore
        };
        Assert.Equal("GHz", vm.EffectiveUnit);

        // User re-points the row at the drain-voltage variable.
        vm.VarName = "VDD";

        // EffectiveUnit must now follow VDD (volts → no linear scale), NOT the stale GHz.
        Assert.Equal("V", vm.EffectiveUnit);
        var pts = vm.BuildValues();
        Assert.NotNull(pts);
        Assert.Equal(20.0, pts![0],  1e-9);   // not 2e10
        Assert.Equal(30.0, pts![^1], 1e-9);   // not 3e10
    }

    // A fresh sweep over a unitless variable applies no scaling at all.
    [Fact]
    public void SweepRow_UnitlessVariable_NoScaling()
    {
        var model = ModelWithVar("VDD", "28", "");   // no declared unit
        var vm = new SweepAxisRowViewModel(model)
        {
            VarName = "VDD", Mode = SweepAxisMode.StepSize,
            StartExpr = "20", StopExpr = "30", StepOrCountExpr = "2",
        };
        Assert.Equal("", vm.EffectiveUnit);
        var pts = vm.BuildValues();
        Assert.Equal(20.0, pts![0],  1e-9);
        Assert.Equal(30.0, pts![^1], 1e-9);
    }

    // ── T6: SweepRow_DefaultsUnitFromVar ─────────────────────────────────────

    [Fact]
    public void SweepRow_DefaultsUnitFromVar()
    {
        var model = ModelWithVar("RFfreq", "2", "GHz");
        var vm    = new SweepAxisRowViewModel(model)
        {
            VarName        = "RFfreq",
            Mode           = SweepAxisMode.StepSize,
            StartExpr      = "1",
            StopExpr       = "5",
            StepOrCountExpr = "1",
            // Unit left blank → should inherit GHz from VAR declaration.
        };

        Assert.Equal("GHz", vm.EffectiveUnit);

        // BuildValues must return base-unit (Hz) values.
        var pts = vm.BuildValues();
        Assert.NotNull(pts);
        Assert.Equal(5, pts!.Length);
        Assert.Equal(1e9, pts[0],  1e-3);
        Assert.Equal(5e9, pts[^1], 1e-3);

        // Explicit override: set unit to MHz → EffectiveUnit = MHz; values scale by 1e6.
        vm.Unit = "MHz";
        Assert.Equal("MHz", vm.EffectiveUnit);
        var ptsMhz = vm.BuildValues();
        Assert.NotNull(ptsMhz);
        Assert.Equal(1e6, ptsMhz![0],  1e-3);
        Assert.Equal(5e6, ptsMhz![^1], 1e-3);
    }

    // ── T7: SweepRow_RoundTrip_Unit ───────────────────────────────────────────

    [Fact]
    public void SweepRow_RoundTrip_Unit()
    {
        var model = ModelWithVar("RFfreq", "2", "GHz");
        var vm    = new SweepAxisRowViewModel(model)
        {
            VarName         = "RFfreq",
            Mode            = SweepAxisMode.StepSize,
            StartExpr       = "1",
            StopExpr        = "5",
            StepOrCountExpr = "1",
            // Unit blank → inherits GHz
        };

        // BuildSpec stores coefficients (1, 5, 1) plus EffectiveUnit="GHz".
        var spec = vm.BuildSpec();
        Assert.NotNull(spec);
        Assert.Equal("GHz", spec!.Unit);
        Assert.Equal(1.0, spec.Start,       precision: 9);
        Assert.Equal(5.0, spec.Stop,        precision: 9);
        Assert.Equal(1.0, spec.StepOrCount, precision: 9);

        // Materialize a PSA from the spec; values must be base-unit.
        var psa = new ParametricSweepAnalysis("SW1", "RFfreq", spec, "HB1");
        Assert.Equal(5, psa.SweepValues.Length);
        Assert.Equal(1e9, psa.SweepValues[0],  1e-3);
        Assert.Equal(5e9, psa.SweepValues[^1], 1e-3);

        // FromPsa restores the Unit field; displayed coefficients are the originals (not 1e9).
        var vm2 = SweepAxisRowViewModel.FromPsa(psa, model);
        Assert.Equal("GHz", vm2.Unit);
        Assert.Equal("1",   vm2.StartExpr);
        Assert.Equal("5",   vm2.StopExpr);
        Assert.Equal("1",   vm2.StepOrCountExpr);
    }
}

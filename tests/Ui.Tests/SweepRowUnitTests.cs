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

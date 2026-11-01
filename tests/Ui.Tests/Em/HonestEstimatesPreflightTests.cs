// EMB-4 — what `check` (EmRunService.Preflight) now says before a planar run starts.
//
//   R-emb4-4  the via-footprint ρ/λ refusal the solve raises after meshing and setup is raised by the preflight.
//   R-emb4-5  the accelerated solve's near-field factor is estimated and reported.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public class HonestEstimatesPreflightTests(ITestOutputHelper output)
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Metal1 = new(1, 0);
    private static readonly LayerKey BacksideVia = new(8, 0);   // the MMIC starter's "Backside Via"

    private static long Um(double v) => (long)Math.Round(v * Dbu);

    /// <summary>A 2 mm Metal1 line on the GaAs starter with a backside via under each end — two via footprints
    /// 1.9 mm apart, ρ/λ = 0.127 at 20 GHz and 0.063 at 10 GHz against the 0.1 G_A^zz is validated to.</summary>
    private static EmLayoutSource TwoVias()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape { Layer = Metal1, X1 = 0, Y1 = 0, X2 = Um(2000), Y2 = Um(20) });
        view.Shapes.Add(new RectShape { Layer = BacksideVia, X1 = Um(40), Y1 = 0, X2 = Um(60), Y2 = Um(20) });
        view.Shapes.Add(new RectShape { Layer = BacksideVia, X1 = Um(1940), Y1 = 0, X2 = Um(1960), Y2 = Um(20) });
        view.Shapes.Add(new LabelShape { Layer = Metal1, X = 0, Y = Um(10), Text = "P1", Height = Um(4), IsPort = true });
        view.Shapes.Add(new LabelShape { Layer = Metal1, X = Um(2000), Y = Um(10), Text = "P2", Height = Um(4), IsPort = true });
        return new EmLayoutSource("vias.clay", view, StarterTechnologies.MmicGaAs(), Dbu);
    }

    private static EmSetup Setup(string topGHz, bool accelerated = false) => new()
    {
        Name = "vias", LayoutRef = "vias.clay", AnalysisKind = EmAnalysisKind.Planar, AcceleratedSolve = accelerated,
        Frequency = new Core.Design.FrequencySpec("1", topGHz, 3, Core.Design.SweepKind.Linear, "GHz", "GHz"),
    };

    [Fact]
    public void TwoViasPastTheValidatedRhoOverLambda_AreRefusedByThePreflight_AndInsideItAreNot()
    {
        var past = EmRunService.Preflight(Setup("20"), TwoVias());
        output.WriteLine(past.Refusal ?? "(not refused)");
        Assert.False(past.Ok);
        Assert.Contains("VIA FOOTPRINT", past.Refusal!, StringComparison.Ordinal);
        Assert.Contains("ρ/λ = 0.12", past.Refusal!, StringComparison.Ordinal);
        Assert.Contains("Bringing the vias closer together, or lowering the sweep's top", past.Refusal!,
                        StringComparison.Ordinal);

        var inside = EmRunService.Preflight(Setup("10"), TwoVias());
        Assert.True(inside.Ok, inside.Refusal);
    }

    [Fact]
    public void AcceleratedSolve_ThePreflightReportsTheNearFieldFactor()
    {
        var pre = EmRunService.Preflight(Setup("10", accelerated: true), TwoVias());
        Assert.True(pre.Ok, pre.Refusal);
        var near = Assert.Single(pre.Findings, f => f.Text.StartsWith("Accelerated solve: the near field holds",
                                                                      StringComparison.Ordinal));
        output.WriteLine(near.Text);
        Assert.Contains("exact sparse factor", near.Text, StringComparison.Ordinal);
        Assert.False(near.IsWarning);
        // The mesh has vias, so it is judged on the dense ceiling, and the budget says what the accelerator holds.
        Assert.NotNull(pre.PlanarMesh!.AcceleratedEstimate);
        Assert.Contains("the accelerated solve this run uses holds about", pre.PlanarMesh.Budget, StringComparison.Ordinal);
    }
}

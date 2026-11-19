using System.Diagnostics;
using System.Globalization;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Layout.Em;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Synthesis;

/// <summary>
/// §6b's benches on Planar MoM against the circuit model (docs/design/filter-synthesis.md §6b), for the tapped hairpin
/// of a 0.1 dB Chebyshev of order 5, 2.2 to 2.6 GHz, on 20 mil RO4350B, lossless as <c>FromCouplings</c> measures: the
/// k(1,2) pair at its circuit gap and the tapped resonator at its circuit tap, 21 points each on the coarse mesh.
/// </summary>
public class CouplingCurveEmTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Benchmark")]   // two 21-point sweeps on the coarse mesh, ~6.5 min
    public void TheHairpinBenches_OnEm_ReadTheCircuitModelsKAndQe()
    {
        var s = new MicrostripSubstrate(0.508e-3, 35e-6, 3.66, 1e20, 0.0);
        var spec = new FilterSpec(FilterResponse.Chebyshev, FilterBand.Bandpass, 5, 2.2e9, 2.6e9, 0.1);
        double f0 = spec.CentreHz, fbw = spec.FractionalBandwidth, w = CoupledResonatorDesign.Width(spec.Z0, f0, s);
        var g = LowpassPrototype.GValues(spec.Response, spec.Order, spec.RippleDb);
        var form = new Dictionary<string, object>
            { ["W"] = w, ["Shape"] = "Hairpin", ["Feed"] = "Tapped", ["Profile"] = "Uniform", ["Sa"] = PassivePlan.OnGrid(2.0 * w) };
        var v = CoupledResonatorDesign.FromCouplings(form, CoupledResonatorDesign.Couplings(g, fbw),
            CoupledResonatorDesign.ExternalQ(g, fbw), f0, s);
        // The lone resonator's length, as FromCouplings' first step sizes it.
        double l = (double)v["L1"];
        for (int i = 0; i < 4; i++)
            l = PassivePlan.OnGrid(l * CoupledResonatorDesign.Peaks(CoupledResonatorDesign.Bench(form, s, 1, l, ("Feed", "Coupled"),
                ("S1", 3.0 * s.HMeters), ("S2", 3.0 * s.HMeters)), s, 1, 0.5 * f0, 1.5 * f0)[0] / f0);

        var em = new CouplingCurvePlanarSolver();
        var clock = Stopwatch.StartNew();
        var pair = CoupledResonatorDesign.LightPair(form, l, (double)v["S2"], s);
        var f = CoupledResonatorDesign.Peaks(pair, s, 2, 0.5 * f0, 1.5 * f0);
        double kCircuit = CouplingCurves.K(f[0], f[1]), kEm = CouplingCurves.CouplingOf(em, pair, s, Math.Sqrt(f[0] * f[1]));
        var one = CoupledResonatorDesign.Bench(form, s, 1, l, ("Tap", v["Tap"]));
        double fe = CoupledResonatorDesign.Peaks(one, s, 1, 0.5 * f0, 1.5 * f0)[0];
        double qeCircuit = CoupledResonatorDesign.ExternalQOf(one, s, fe), qeEm = CouplingCurves.ExternalQOf(em, one, s, fe);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"S2 {(double)v["S2"] * 1e3:0.###} mm: k EM {kEm:0.#####}, circuit {kCircuit:0.#####}; Tap {(double)v["Tap"] * 1e3:0.###} mm: "
          + $"Qe EM {qeEm:0.###}, circuit {qeCircuit:0.###}; EM took {clock.Elapsed.TotalSeconds:0.0} s"));
        // Measured: k 0.101 against 0.133, 24 % low with or without grounded ports; Qe 7.20 against 6.86, where the series
        // gap's end cell barely loaded the tap and read 7957.
        Assert.InRange(kEm / kCircuit, 0.7, 1.3);
        Assert.InRange(qeEm / qeCircuit, 0.9, 1.1);
    }
}

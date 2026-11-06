// brief-em3d-31 — the radiation pattern of a 3D run, driven through the SAME far-field stage the planar kernel
// uses (src/Engine/Mom/FarFieldStage.cs), so the "farfield" group a 3D .npy carries is the planar one cube for
// cube and the Data Display, the metrics and the CLI need nothing new.
//
// EACH PORT'S PATTERN IS THAT PORT'S OWN EXCITATION. Palace and EMerge excite every port in turn, so port k's pattern
// comes from the solve that drove port k, with every other port terminated in its own Z0. openEMS runs once per port
// (R-em3d9-3a), and its other ports reflect a little as its probes see them, so its port k adds up every run
// (brief-cosim-pattern-3). It is normalised to a 1 V voltage across the driven port — the planar kernel's
// "port driven at 1 V" — and the accepted power is ½·Re(V·I*) at that port under the same excitation, which
// is R-ant-5's denominator; the mismatch factor is 1 − |S_kk|² of the published S (R-ant-6). So the powers
// are this run's own S-parameters' rule, not a re-derivation (R-em3d31-1).

using System.Globalization;
using System.Numerics;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using RfCore.Data;

namespace CircuitRF.Design.Em3d;

/// <summary>One (port, frequency)'s pattern and the two port quantities its metrics need.</summary>
internal sealed record Em3dPatternPoint(PlanarFarFieldPattern Pattern, double AcceptedW, Complex Reflection);

internal static class Em3dRadiation
{
    /// <summary>
    /// The efficiency tolerance a 3D solve states for itself — how far past 1 its radiated-over-accepted may
    /// read before it is an error. Its two powers come from different integrals (a surface transform and a
    /// port DFT), and the lossless half-wave dipole measured their balance (OpenEmsRadiationTests); this is set
    /// comfortably outside that.
    /// </summary>
    public const double EfficiencyTolerance = 0.03;

    /// <summary>
    /// brief-cosim-pattern-3 — metadata in the far-field group: this result's patterns are the field its S describes, referred to the voltage its S states.
    /// Palace's always were. An older openEMS or EMerge result lacks it, and a fields analysis asks for that run again rather than recombining it.
    /// </summary>
    public const string PortsTerminatedCube = "__PortsTerminated";

    public static FarFieldExternalTerms Terms(string solver, double acceptedW) => new(
        solver, acceptedW,
        "Front-to-back cannot be computed: the pattern's θ axis stops at 90° because the air box's floor is a " +
        "conducting plane, and that plane runs through the absorber on every side — an INFINITE plane in the " +
        "model, so the field below it is identically zero by construction, not small. Set the AirBox ZMin face " +
        "to Absorbing (and draw the ground plane, if the design has one) to get the whole sphere and a " +
        "front-to-back ratio.",
        EmSuitability.No(
            $"{solver} does not itemise a surface-wave term: the 3D problem has no laterally infinite substrate for " +
            "a guided mode to be launched into and lost, so whatever the substrate guides either reaches a board " +
            "edge and radiates or is absorbed at the air box, and is in the other two numbers. PowerAccepted minus " +
            "PowerRadiated is every loss together, and both are published."),
        EmSuitability.No(
            $"{solver} does not itemise its conductor loss beside its radiation: PowerAccepted minus PowerRadiated is " +
            "the whole of what the structure dissipated — metal and dielectric together — and both are published."),
        EfficiencyTolerance);

    /// <summary>
    /// brief-cosim-pattern-7 R-cp7-3 — a recombined planar pattern's terms. The radiated power sums, as the integral of the summed pattern. The surface-wave and conductor powers are
    /// quadratic in the currents, so each port's stored number leaves out the cross terms between ports (9.4 dB off on a measured two-gap strip), and the currents are not kept. Each is
    /// omitted by name, and the dielectric residual goes with them through the registry's own predicate.
    /// </summary>
    public static FarFieldExternalTerms PlanarSumTerms(string solver, double acceptedW) => new(
        solver, acceptedW,
        "Front-to-back cannot be computed: " + PlanarFarFieldPattern.ThetaRangeNote,
        EmSuitability.No(
            "A recombined planar pattern does not itemise a surface-wave term: that power is quadratic in the currents, so the run's number for each port cannot be added up for a " +
            "circuit, and the currents are not kept. PowerAccepted minus PowerRadiated is every loss together, and both are published."),
        EmSuitability.No(
            "A recombined planar pattern does not itemise its conductor or film loss, for the same reason: each is quadratic in the currents, and the currents are not kept. " +
            "PowerAccepted minus PowerRadiated is every loss together, and both are published."));

    /// <summary>What one point's metrics are read from, so a metric asked for alone (brief-cosim-pattern-4) is the cube's number.</summary>
    public static PlanarMetricContext Context(string solver, Em3dPatternPoint point, double referenceInputPowerDbm, bool planarSum = false)
        => new(point.Pattern, planarSum ? PlanarSumTerms(solver, point.AcceptedW) : Terms(solver, point.AcceptedW), point.Reflection,
               PlanarMetricSettings.Default with { ReferenceInputPowerDbm = referenceInputPowerDbm });

    /// <summary>
    /// Assembles the points — row-major <c>[freq, port]</c>, frequencies ascending, ports as
    /// <paramref name="portNumbers"/> — through the stage, publishes the group into <paramref name="data"/>
    /// and returns the sentences the run carries. <paramref name="frequencies"/> says which frequencies the pattern is at, when
    /// they are not every frequency of the sweep (brief-emi-7: EMerge's are its field frequencies). <paramref name="origin"/> says how a
    /// port's pattern was formed, when it is not simply the run that excited it.
    /// </summary>
    public static List<string> Publish(DataSet data, string solver, PlanarFarFieldGrid grid, IReadOnlyList<int> portNumbers,
                                       IReadOnlyList<double> freqs, IReadOnlyList<Em3dPatternPoint> points,
                                       double referenceInputPowerDbm, bool hemisphere, string? frequencies = null, string? origin = null, bool planarSum = false)
    {
        var slices = new List<FarFieldSlice>(freqs.Count);
        int n = portNumbers.Count;
        for (int i = 0; i < freqs.Count; i++)
        {
            var pats = new PlanarFarFieldPattern[n];
            var mets = new PlanarMetricReport[n];
            var pols = new PlanarPolarizationPattern[n];
            for (int k = 0; k < n; k++)
            {
                var pt = points[i * n + k];
                pats[k] = pt.Pattern;
                (mets[k], pols[k]) = FarFieldStage.Evaluate(Context(solver, pt, referenceInputPowerDbm, planarSum));
            }
            slices.Add(new FarFieldSlice(freqs[i], pats, mets, pols));
        }
        var sets = FarFieldStage.Assemble(grid, portNumbers, slices);
        FarFieldStage.Publish(data, sets.FarField, sets.Metrics, sets.Polarization);

        var notes = new List<string>();
        var first = points[0];
        var m0 = sets.Metrics.At(0, 0);
        notes.Add($"Far field ({solver}), port {first.Pattern.DrivenPort} driven at 1 V with every other port terminated in its " +
                  $"own Z0, {G(first.Pattern.FrequencyHz / 1e9)} GHz: E_θ and E_φ are r-normalised patterns (r·E with e^{{−jk₀r}} " +
                  $"removed), peak {Eng(first.Pattern.PeakFieldV)}V; U peaks at " +
                  $"{Eng(first.Pattern.PeakIntensityWPerSr)}W/sr and integrates to " +
                  $"{Eng(first.Pattern.RadiatedPowerW)}W over the {(hemisphere ? "upper hemisphere" : "whole sphere")} " +
                  $"against {Eng(first.AcceptedW)}W accepted at the port (radiation efficiency " +
                  $"{m0.Budget.RadiationEfficiency:P2}). {origin ?? "Each port's pattern is the run that excited that port."} " +
                  $"{frequencies ?? $"A pattern at each of the sweep's {freqs.Count} frequencies"}, on a 1° grid.");
        foreach (string r in sets.Metrics.Refusals) notes.Add(r);
        var pol = sets.Polarization.At(0, 0);
        notes.Add(pol.ScaleCaption);
        if (pol.Reference is { } reference) notes.Add(reference.Note);
        foreach (string r in sets.Polarization.Refusals) notes.Add(r);
        return notes;
    }

    private static string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

    /// <summary>A value with an SI prefix and four significant digits, followed directly by the unit.</summary>
    private static string Eng(double v)
    {
        if (v == 0 || !double.IsFinite(v)) return v.ToString("G4", CultureInfo.InvariantCulture) + " ";
        string[] p = ["f", "p", "n", "µ", "m", "", "k", "M", "G"];
        int e = Math.Clamp((int)Math.Floor(Math.Log10(Math.Abs(v)) / 3), -5, 3);
        return (v / Math.Pow(1000, e)).ToString("G4", CultureInfo.InvariantCulture) + " " + p[e + 5];
    }
}

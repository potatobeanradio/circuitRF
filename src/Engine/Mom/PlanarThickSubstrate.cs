// ICF-4 Part B — what a run says on a substrate that is electrically thick: R-icf4-3 (the tall Uncalibrated port) and
// R-icf4-5 (the non-passive warning says which port carries the excess, and how thick the substrate is).
//
// Both are WARNINGS, not fixes. src/Engine/Mom/RESOLVED.md §ICF-4 Part A is the measurement behind them: on 750 µm of
// silicon a grounded Uncalibrated edge port — a lumped source across the whole height, with no vertical current in it —
// made a passive spiral non-passive from between 0.26 and 0.28 wavelengths of electrical height, and returning the same
// leads through a drawn via chain made it passive again. A vertical current term for the port is later work.

using System.Globalization;
using System.Numerics;
using NumFlat;

namespace CircuitRF.Engine.Mom;

internal static class PlanarThickSubstrate
{
    /// <summary>
    /// <b>R-icf4-3 — the electrical height past which a grounded Uncalibrated port is warned.</b> A quarter wavelength:
    /// just under where ICF-4 Part A measured the error starting (0.261 λ passive, 0.278 λ not). It is also where the
    /// first TE surface wave of a grounded slab cuts on, which is why the onset frequency alone could not separate the
    /// two causes; the via-chain return did.
    /// </summary>
    public const double TallPortFraction = 0.25;

    /// <summary><b>R-icf4-5 — a substrate past a tenth of a wavelength is said to be thick</b> in the non-passive warning.</summary>
    public const double ThickSubstrateFraction = 0.1;

    /// <summary>
    /// The optical path from the ground plane up to <paramref name="zM"/> — Σ tᵢ·√εᵣᵢ over the finite layers below it —
    /// in metres. Multiply by f/c for wavelengths. A conducting layer counts its εᵣ alone: σ makes it lossy, not longer.
    /// </summary>
    public static double OpticalHeightM(LayerStack stack, double zM)
    {
        double path = 0, z = stack.InterfaceZ[0];
        foreach (var layer in stack.Layers)
        {
            if (z >= zM) break;
            double t = Math.Min(layer.ThicknessM, zM - z);
            path += t * Math.Sqrt(Math.Max(layer.Material.EpsR * layer.Material.MuR, 0.0));
            z += layer.ThicknessM;
        }
        return path;
    }

    /// <summary>
    /// <b>R-icf4-3.</b> The warning for every grounded Uncalibrated port whose height passes
    /// <see cref="TallPortFraction"/> of a wavelength somewhere in the sweep, or null when none does. Each entry is the
    /// port's number and its level's height above the ground plane.
    /// </summary>
    public static string? TallPortWarning(IReadOnlyList<(int Number, double HeightM, double OpticalM)> ports,
                                          IReadOnlyList<double> freqsHz, SurfaceMesher.PlanarLengthFormat fmt)
    {
        var parts = new List<string>();
        var above = new SortedSet<double>();
        foreach (var (number, heightM, opticalM) in ports)
        {
            if (!(opticalM > 0)) continue;
            double fFrom = TallPortFraction * EmConstants.C0 / opticalM;
            var hit = freqsHz.Where(f => f >= fFrom).ToList();
            if (hit.Count == 0) continue;
            foreach (var f in hit) above.Add(f);
            parts.Add($"port {number} spans {fmt(heightM)} to the ground plane, a quarter wavelength in the substrate " +
                      $"from {SurfaceMesher.Eng(fFrom)}Hz");
        }
        if (parts.Count == 0) return null;

        return $"Uncalibrated Port height: {string.Join("; ", parts)}. Its source is lumped across that whole height " +
               $"with no vertical current in it, and past about {TallPortFraction:0.##} of a wavelength that stops " +
               "describing the launch: on a thick silicon substrate it was measured to make a passive part read " +
               $"non-passive. Treat the answer at {PlanarSolve.FrequencyList([.. above])} with that in mind; a drawn return " +
               "to the ground plane, or a lower top frequency, avoids it.";
    }

    /// <summary>
    /// <b>R-icf4-5 — which ports carry the gain.</b> The right singular vector of S for σ_max — the incident wave that comes
    /// back largest — read as each port's share of its power, largest first, until three quarters of it is named. Port
    /// numbers come from <paramref name="numbers"/>, in S's own order.
    /// </summary>
    public static IReadOnlyList<(int Number, double Share)> CarryingPorts(Mat<Complex> s, IReadOnlyList<int> numbers)
    {
        int n = s.RowCount;
        if (n == 0) return [];
        var v = new Complex[n];
        Array.Fill(v, new Complex(1.0 / Math.Sqrt(n), 0));
        var w = new Complex[n];
        var u = new Complex[n];
        // Power iteration on SᴴS: n is the port count, so this is a handful of multiply-adds.
        for (int it = 0; it < 200; it++)
        {
            for (int i = 0; i < n; i++) { u[i] = 0; for (int j = 0; j < n; j++) u[i] += s[i, j] * v[j]; }
            for (int j = 0; j < n; j++) { w[j] = 0; for (int i = 0; i < n; i++) w[j] += Complex.Conjugate(s[i, j]) * u[i]; }
            double norm = Math.Sqrt(w.Sum(c => c.Magnitude * c.Magnitude));
            if (!(norm > 0)) return [];
            for (int j = 0; j < n; j++) v[j] = w[j] / norm;
        }

        var shares = Enumerable.Range(0, n)
            .Select(i => (Number: numbers[i], Share: v[i].Magnitude * v[i].Magnitude))
            .OrderByDescending(p => p.Share).ThenBy(p => p.Number).ToList();
        var named = new List<(int, double)>();
        double sum = 0;
        foreach (var p in shares)
        {
            named.Add(p);
            sum += p.Share;
            if (sum >= 0.75) break;
        }
        return named;
    }

    /// <summary>The clause <see cref="CarryingPorts"/> gives the NOT PASSIVE warning.</summary>
    public static string CarryingClause(IReadOnlyList<(int Number, double Share)> ports)
    {
        if (ports.Count == 0) return "";
        string pct(double x) => (100 * x).ToString("0", CultureInfo.InvariantCulture) + "%";
        return ports.Count == 1
            ? $" The excess is carried by port {ports[0].Number} ({pct(ports[0].Share)} of the wave that gains most)."
            : $" The excess is shared by ports {string.Join(", ", ports.Select(p => $"{p.Number} ({pct(p.Share)})"))} " +
              "of the wave that gains most.";
    }

    /// <summary>
    /// <b>R-icf4-5 — the thick-substrate sentence</b>, in place of "not identified", or null below
    /// <see cref="ThickSubstrateFraction"/>. ASCII, because it reaches the Touchstone caveat (see
    /// <see cref="PlanarSolve.NonPassivityCause"/>).
    /// </summary>
    public static string? ThickSubstrateSentence(double heightM, double opticalM, double atHz)
    {
        double lambdas = opticalM * atHz / EmConstants.C0;
        if (!(lambdas > ThickSubstrateFraction)) return null;
        string um = (heightM * 1e6).ToString("G4", CultureInfo.InvariantCulture);
        string l = lambdas.ToString("0.###", CultureInfo.InvariantCulture);
        return $"The substrate is electrically thick there: {um} um under the metal is {l} wavelengths at " +
               $"{SurfaceMesher.Eng(atHz)}Hz, past the {ThickSubstrateFraction:0.#} below which a port's path to the " +
               "ground plane is short. On a substrate this thick a grounded edge port's lumped source, which carries " +
               "no vertical current, was measured to produce gain like this; a drawn return to the ground plane did not.";
    }
}

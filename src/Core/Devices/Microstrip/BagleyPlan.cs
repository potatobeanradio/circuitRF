using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>What a Bagley polygon divider comes out to on one substrate: the derived numbers, and the plan the model
/// stamps and the PCell draws.</summary>
/// <param name="Ways">N, the number of outputs.</param>
/// <param name="W0Meters">Width of the Z0 line every pin leaves on.</param>
/// <param name="Lines">The ring's two lines, each with its λg/4: the input arms (λ/4) and the sides (λ/2, two of
/// them).</param>
public sealed record BagleyDesign(double Z0, double FreqHz, int Ways, double W0Meters, IReadOnlyList<WilkinsonLine> Lines,
    RingShape Ring, PassivePlan Plan);

/// <summary>
/// The Bagley polygon divider: an odd N-way, in-phase-or-antiphase, lossless divider with no resistors. G. Bagley's
/// design was never published; its analysis is in I. Sakagami, T. Wuren, M. Fujii and M. Tahara, "Compact Multi-Way
/// Power Dividers Similar to the Bagley Polygon," IEEE MTT-S IMS 2007. Pure: specs and a substrate in,
/// <see cref="BagleyDesign"/> out.
///
/// <para><b>The polygon.</b> N outputs v1 … vN sit at the corners of a polygon whose sides are half-wave lines, and the
/// input feeds the middle of the one side that closes it, a quarter wave from v1 and from vN. A half-wave line repeats
/// its voltage inverted, so every corner has the same amplitude and adjacent corners are opposite: v1, v3, v5 … leave a
/// quarter period behind the input and v2, v4 … half a period from those. N must be odd so the two ways round from the
/// input agree at every corner (they differ by a whole number of wavelengths). Equal voltages into equal loads take
/// equal power, so the input arms, Z_a, only have to match: the corners' currents add to N·V/(2·Z0) at v1 and at vN,
/// which a quarter wave of Z_a turns into Z_a²·N/(4·Z0) at the input, Z0 when Z_a = 2·Z0/√N. The sides' impedance
/// does not enter at F; it sets the band, and Z0 gives the widest input match on ideal lines at N = 3 (|S11| under
/// −20 dB over ±17 % of F, against ±13 % at Z0/√2 and ±15 % at √2·Z0). Only the input is matched: the outputs see
/// each other and are not isolated.</para>
///
/// <para><b>The ring</b> is laid out as the rat-race's (<see cref="RacetrackRing"/>): the input on the upper run with
/// v1 … vj to its right and vN … vN−j+1 to its left, j = ⌊(m + 1)/2⌋ for N = 2m + 1, and the rest of the corners on the
/// lower run, so the runs come out nearly equal; the side between vj and vj+1 (and its mirror) goes round a turn.
/// Pins: 1 the input, k + 1 the corner vk. Widths are MLIN's at F, each λg/4 in MLIN's own dispersive εeff at F
/// between the reference planes of the tees it joins.</para>
/// </summary>
public static class BagleyPlan
{
    /// <summary>The most outputs the plan builds.</summary>
    public const int MaxWays = 9;

    /// <summary>The pins of an <paramref name="ways"/>-way divider: the input, then each output in turn round the
    /// polygon.</summary>
    public static IReadOnlyList<string> PinsFor(int ways) => [.. Enumerable.Range(1, ways + 1).Select(k => $"{k}")];

    /// <summary>The ways a pin count belongs to (N + 1 pins); 3 for any count that is not one.</summary>
    public static int WaysForPinCount(int pins) => pins - 1 is var n and >= 3 and <= MaxWays && n % 2 == 1 ? n : 3;

    public static BagleyDesign Build(double z0, double fHz, double ways, MicrostripSubstrate s,
        PlanCorner corner = PlanCorner.Curve)
    {
        foreach (var (name, value) in new[] { ("Z0", z0), ("F", fHz) })
            if (!(value > 0))
                throw new ParameterRefusalException(Diagnostic.Create("device.bagley.spec-not-positive",
                    DiagnosticSeverity.Error, "Bagley divider: {name} = {value} must be positive.",
                    ("name", name), ("value", value)));
        if (!(ways >= 3 && ways <= MaxWays && ways == Math.Round(ways) && ways % 2 == 1))
            throw new ParameterRefusalException(Diagnostic.Create("device.bagley.ways-unsupported",
                DiagnosticSeverity.Error, "Bagley divider: N = {n}; the polygon needs an odd number of outputs, 3 to {max}.",
                ("n", ways), ("max", MaxWays)));

        int n = (int)ways, m = (n - 1) / 2, j = (m + 1) / 2;
        const string part = "Bagley divider", width = "device.bagley.width-unreachable";
        double w0 = PassivePlan.OnGrid(BranchLinePlan.Width(z0, fHz, s, part, width));
        var arm = RacetrackRing.Quarter("input arms", 2.0 * z0 / Math.Sqrt(n), fHz, s, part, width);
        var side = RacetrackRing.Quarter("sides", z0, fHz, s, part, width);
        var a = new RingLine(arm.WMeters, arm.QuarterWaveMeters);
        var b = new RingLine(side.WMeters, 2.0 * side.QuarterWaveMeters);

        // Corner vk is pin k + 1. The upper run: vN-j+1 … vN, the input, v1 … vj; the lower: vN-j down to vj+1.
        List<RingStop> Corners(IEnumerable<int> ks)
        {
            var stops = new List<RingStop>();
            foreach (int k in ks)
            {
                if (stops.Count > 0) stops.Add(b);
                stops.Add(new RingTee(k + 1));
            }
            return stops;
        }
        List<RingStop> top = [.. Corners(Enumerable.Range(n - j + 1, j)), a, new RingTee(1), a,
                              .. Corners(Enumerable.Range(1, j))];
        var bottom = Corners(Enumerable.Range(j + 1, n - 2 * j).Reverse());
        var pins = PinsFor(n);
        var (sections, nodes, ring) = RacetrackRing.Build(top, bottom, w0,
            (side.WMeters, side.Z, side.QuarterWaveMeters, 2.0 * side.QuarterWaveMeters), corner, s, fHz,
            pins.Count + 1, part, "device.bagley.ring-too-small");
        return new BagleyDesign(z0, fHz, n, w0, [arm, side], ring, new PassivePlan(pins, sections, nodes));
    }
}

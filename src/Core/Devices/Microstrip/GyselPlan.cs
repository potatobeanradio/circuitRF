using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>What a Gysel divider comes out to on one substrate: the derived numbers, and the plan the model stamps and
/// the PCell draws.</summary>
/// <param name="W0Meters">Width of the Z0 line every pin leaves on.</param>
/// <param name="Lines">The ring's three lines, each with its λg/4: the input arms, the isolation arms, and the half-wave
/// line, which is two of its quarter waves long.</param>
/// <param name="IsolationOhms">The resistor that belongs from each of R2 and R3 to ground.</param>
public sealed record GyselDesign(double Z0, double FreqHz, double W0Meters, IReadOnlyList<WilkinsonLine> Lines,
    double IsolationOhms, RingShape Ring, PassivePlan Plan);

/// <summary>
/// The two-way Gysel divider (U. H. Gysel, "A New N-Way Power Divider/Combiner Suitable for High-Power Applications,"
/// MTT-S International Microwave Symposium Digest, 1975), planned as one microstrip ring. Pure: specs and a substrate in,
/// <see cref="GyselDesign"/> out.
///
/// <para><b>The ring.</b> The input (1) is a quarter wave from each output (2, 3); each output is a quarter wave from an
/// isolation node (R2, R3); and a half-wave line joins the two isolation nodes. Each isolation node goes to ground through
/// a resistor, which is NOT part of this part: an ordinary resistor from R2 and from R3 to a ground via, so the layout,
/// LVS, the BOM and EM see it — and, the reason for the topology, a resistor that can be as large, or as far from the
/// ring, as the power it has to take asks. Laid out as the rat-race is (<see cref="RacetrackRing"/>): 2, 1 and 3 on the
/// upper run with their lines leaving upward, R2 and R3 on the lower run with theirs downward, the half-wave line between
/// them, and each isolation arm round a turn.</para>
///
/// <para><b>The numbers</b>, from the even/odd analysis about the line through 1 and the half-wave line's middle. Even
/// (2 and 3 driven alike): the middle of the half-wave line is an open, so each isolation node sits a quarter wave from
/// an open — a short — and the isolation arm a quarter wave from that, an open at the output: the resistors carry
/// nothing and each output sees only its input arm, which must turn the input's 2·Z0 half into Z0: √2·Z0. Odd (driven
/// opposite): the input and the line's middle are virtual grounds, so the input arm and the half line are shorted
/// quarter waves, opens at the output and at the isolation node; the output then sees its resistor through the isolation
/// arm, Z2²/R, which is Z0 when R = Z2²/Z0. Matched in both halves, every port is matched and the outputs isolated at F.
/// Z2 is free: Z0, so the resistor is Z0 too and the Z0 line it lands on is matched by it. The half-wave line's
/// impedance does not enter at F; lower widens the band (on ideal lines |S11|, |S22| and |S23| stay under −20 dB over
/// ±9 % of F with Z0, ±11 % with Z0/√2 and ±12 % with Z0/2), and the plan takes Z0/√2, whose width stays within about
/// twice the port line's. Widths are MLIN's at F, each λg/4 in MLIN's own dispersive εeff at F between the reference
/// planes of the tees it joins.</para>
/// </summary>
public static class GyselPlan
{
    /// <summary>The part's pins, in terminal order: the input, the two outputs, and the isolation nodes beside 2 and 3.</summary>
    public static readonly IReadOnlyList<string> Pins = ["1", "2", "3", "R2", "R3"];

    public static GyselDesign Build(double z0, double fHz, MicrostripSubstrate s, PlanCorner corner = PlanCorner.Curve)
    {
        foreach (var (name, value) in new[] { ("Z0", z0), ("F", fHz) })
            if (!(value > 0))
                throw new ParameterRefusalException(Diagnostic.Create("device.gysel.spec-not-positive",
                    DiagnosticSeverity.Error, "Gysel divider: {name} = {value} must be positive.",
                    ("name", name), ("value", value)));

        const string part = "Gysel divider", width = "device.gysel.width-unreachable";
        double w0 = PassivePlan.OnGrid(BranchLinePlan.Width(z0, fHz, s, part, width));
        var input = RacetrackRing.Quarter("input arms", Math.Sqrt(2.0) * z0, fHz, s, part, width);
        var isolation = RacetrackRing.Quarter("isolation arms", z0, fHz, s, part, width);
        var half = RacetrackRing.Quarter("half-wave line", z0 / Math.Sqrt(2.0), fHz, s, part, width);

        // Nodes: 1-5 the pins (1, 2, 3, R2, R3); the ring numbers its own from 6.
        var (sections, nodes, ring) = RacetrackRing.Build(
            [new RingTee(2), new RingLine(input.WMeters, input.QuarterWaveMeters), new RingTee(1),
             new RingLine(input.WMeters, input.QuarterWaveMeters), new RingTee(3)],
            [new RingTee(4), new RingLine(half.WMeters, 2.0 * half.QuarterWaveMeters), new RingTee(5)],
            w0, (isolation.WMeters, isolation.Z, isolation.QuarterWaveMeters, isolation.QuarterWaveMeters), corner, s, fHz,
            Pins.Count + 1, part, "device.gysel.ring-too-small");
        return new GyselDesign(z0, fHz, w0, [input, isolation, half], isolation.Z * isolation.Z / z0, ring,
            new PassivePlan(Pins, sections, nodes));
    }
}

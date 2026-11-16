using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>What a coupled-line balun comes out to on one substrate.</summary>
/// <param name="Coupling">The voltage coupling c each section needs, 1/√(1 + 2·ZL/Z0).</param>
/// <param name="Ze">Even-mode impedance asked for, Z0·√((1 + c)/(1 − c)).</param>
/// <param name="Zo">Odd-mode impedance asked for, Z0·√((1 − c)/(1 + c)).</param>
/// <param name="Modes">MCLIN's modes at F of each section as drawn.</param>
public sealed record MarchandBalunDesign(
    double Z0, double ZL, double FreqHz, double GapMeters, double Coupling, double Ze, double Zo,
    double WMeters, double SMeters, double LengthMeters, KirschningJansenCoupled.Modes Modes, PassivePlan Plan);

/// <summary>
/// The compensated coupled-line balun (N. Marchand, "Transmission-line conversion transformers," Electronics,
/// vol. 17, no. 12, pp. 142-145, Dec. 1944, in coaxial form; planar: K. S. Ang and I. D. Robertson, "Analysis and
/// design of impedance-transforming planar Marchand baluns," IEEE Trans. MTT, vol. 49, no. 2, pp. 402-406, Feb. 2001).
/// Pure: Z0, ZL and F plus a substrate and a layout Gap in, <see cref="MarchandBalunDesign"/> out.
///
/// <para><b>The structure.</b> Two identical quarter-wave coupled sections in a row. The unbalanced input runs along
/// strip A of the first, across a short line of length Gap, and along strip A of the second to an open end: a half-wave
/// open stub. Strip B of each section is grounded at its outer end and is a balanced output at its inner end, so the
/// outputs 2 and 3 face each other across Gap. The grounds are NOT part of this part: G2 and G3 are the outer ends of
/// the strips that carry outputs 2 and 3, for vias to ground, so layout, LVS and EM see the vias.</para>
///
/// <para><b>The numbers.</b> At the quarter wave the coupled sections' open-circuit impedances reduce to their same-
/// strip and cross-strip transfer terms, Zp = (Ze + Zo)/2 and Zm = (Ze − Zo)/2, and solving the two sections with the
/// stub's open end and the grounds gives V3 = −V2 for any loads alike, and an input impedance
/// 2(Ze·Zo)²/(ZL·(Ze − Zo)²) with ZL on each output. Matching that to Z0 with lines of √(Ze·Zo) = Z0 needs a coupling
/// c = 1/√(1 + 2·ZL/Z0) — the coupling-to-impedance-ratio relation Ang and Robertson derive, worked here from the
/// sections' impedance matrix for √(Ze·Zo) = Z0. <see cref="CoupledLineSynthesis"/> then finds the pair, λg/4 at its
/// mean εeff. ZL is the load on EACH output, half the balanced load.</para>
///
/// <para><b>Limits.</b> Equal loads of 50 Ω need c = 1/√3 (−4.8 dB), far tighter than edge-coupled microstrip
/// reaches: on 20 mil RO4350B it needs a gap under 0.1·h and is refused. The match fixes the mutual admittance
/// 1/Zo − 1/Ze = √(2/(ZL·Z0)), which edge coupling at a buildable gap only reaches for high output loads; there 200 Ω
/// on each output (c = −9.5 dB) needs 102 µm (docs/design/rf-passives.md). The balance above is exact only for TEM
/// lines: in microstrip the odd mode runs faster than the even mode, neither section is a quarter wave for both, and
/// the outputs come out about 16° short of 180° on that board (amplitude within 0.2 dB). A longer Gap moves the match
/// off F.</para>
/// </summary>
public static class MarchandBalunPlan
{
    /// <summary>The part's pins, in terminal order: the unbalanced input, the two balanced outputs, and the grounded
    /// ends of the strips that carry them.</summary>
    public static readonly IReadOnlyList<string> Pins = ["1", "2", "3", "G2", "G3"];

    /// <summary>The coupling each section needs to match ZL on each output to Z0, with √(Ze·Zo) = Z0.</summary>
    public static double CouplingFor(double z0, double zl) => 1.0 / Math.Sqrt(1.0 + 2.0 * zl / z0);

    public static MarchandBalunDesign Build(double z0, double zl, double fHz, double gapMeters, MicrostripSubstrate s)
    {
        foreach (var (name, value) in new[] { ("Z0", z0), ("ZL", zl), ("F", fHz), ("Gap", gapMeters) })
            if (!(value > 0))
                throw new ParameterRefusalException(Diagnostic.Create("device.marchand.spec-not-positive",
                    DiagnosticSeverity.Error, "Marchand balun: {name} = {value} must be positive.",
                    ("name", name), ("value", value)));

        double c = CouplingFor(z0, zl);
        double ze = z0 * Math.Sqrt((1.0 + c) / (1.0 - c)), zo = z0 * Math.Sqrt((1.0 - c) / (1.0 + c));
        var g = CoupledLineSynthesis.Synthesize(ze, zo, fHz, s, "Marchand balun");
        double w = PassivePlan.OnGrid(g.WMeters), gap = PassivePlan.OnGrid(g.SMeters), l = PassivePlan.OnGrid(g.QuarterWaveMeters);
        double join = PassivePlan.OnGrid(gapMeters);

        // Nodes: 1-5 the pins (1, 2, 3, G2, G3); 6 the open end, 7/8 the joining line's ends. The open end is numbered
        // first of the internal nodes so it is eliminated last, when its pivot is no longer a quarter-wave null.
        var sections = new PlanSection[]
        {
            new PlanCoupledLine([1, 7, 4, 2], 0.0, 0.0, 0.0, w, gap, l),
            new PlanLine([7, 8], l, 0.0, 0.0, w, join),
            new PlanCoupledLine([8, 6, 3, 5], l + join, 0.0, 0.0, w, gap, l),
        };
        // A via to ground on each grounded end, and the balanced load across the outputs, which face each other across Gap.
        return new MarchandBalunDesign(z0, zl, fHz, join, c, ze, zo, w, gap, l,
            CoupledLineCouplerPlan.Modes(w, gap, l, fHz, s), new PassivePlan(Pins, sections, NodeCount: 8)
            {
                Sites =
                [
                    new(PlanSiteRole.Via, PlanSiteKind.Shunt, "G2", null, null, null),
                    new(PlanSiteRole.Via, PlanSiteKind.Shunt, "G3", null, null, null),
                    new(PlanSiteRole.Load, PlanSiteKind.Series, "2", "3", join, 2.0 * zl),
                ],
            });
    }
}

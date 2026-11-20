// brief-emi-13 R-emi13-1/2 — the effective index a single-terminal wave port's mode is aimed at: the line's own quasi-TEM mode, from
// kernel A's 2D electrostatic solve of the port face's cross-section (the wall ports' section and medium, Em3dWallCalibration), at
// the sweep's top, where EMerge fixes a wave port's modes.
//
// WHY. EMerge's wave port takes the mode with the largest β² at the sweep's top, which is not always the line's: ground metal on the
// top layer at the face carries a parallel-plate mode between it and the floor at β² = εr·k0², above a microstrip's or a grounded
// coplanar line's quasi-TEM mode, and a wave port took it (src/Design/RESOLVED.md, EMI-13). The line's own mode is the one whose
// index is closest to what the cross-section predicts with every other metal at ground potential.
//
// WHAT. n_eff = c0·Re√(L′C′), with C′ the line's self-capacitance from kernel A in complex permittivity (a conducting layer as
// εr − jσ/(ωε0), so a line over conducting silicon is priced in its slow-wave regime) and L′ = μ0ε0/C′₀ from the same section with
// every dielectric replaced by vacuum. Z_c = Re√(L′/C′) is reported beside it. A face kernel A cannot solve (no ground beneath, a
// dielectric that does not span the face, a z face, metal that is not horizontal) has no target, and its port keeps EMerge's own rule.

using System.Numerics;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

/// <summary>What a single-terminal wave port's mode is aimed at, or why it is not.</summary>
/// <param name="Neff">The line's quasi-TEM effective index at <see cref="FrequencyHz"/>; null with <see cref="Refusal"/> set.</param>
/// <param name="Zc">The line's characteristic impedance from the same section, Ω.</param>
/// <param name="FrequencyHz">The frequency the index is for: the sweep's top.</param>
/// <param name="Refusal">Why there is no target, as a clause: "the xmin face's cross-section has no ground plane beneath it".</param>
public sealed record Em3dWavePortTarget(double? Neff, double? Zc, double FrequencyHz, string? Refusal)
{
    /// <summary>brief-emi-13 R-emi13-2 — the share by which the mode a run took may differ from the target before the run warns,
    /// and how near a candidate must be before the driver's first mode search stops widening (its TARGET_FRACTION). The brief
    /// started at 10 %; measured, the line's own mode came within 0.4 % of its target on the top-ground line and 3 % on the silicon
    /// line, and the parallel-plate mode EMerge's own rule took sat 6.4 % above it, inside 10 % (src/Design/RESOLVED.md, EMI-13).</summary>
    public const double WarnFraction = 0.05;

    /// <summary>Whether <paramref name="port"/> is a port whose mode is aimed: a wave port of one terminal on a line (not a hollow
    /// guide, not a terminal group, not a Floquet mode).</summary>
    public static bool Applies(Em3dPort port)
        => port.Kind == Em3dPortKind.Wave && !port.Hollow && port.FaceGroup is null && port.Floquet is null && port.HollowMode is null;

    /// <summary>The target for <paramref name="port"/> on <paramref name="problem"/>, at the sweep's top. Null when the port is not
    /// one whose mode is aimed (<see cref="Applies"/>).</summary>
    public static Em3dWavePortTarget? For(Em3dProblem problem, Em3dPort port)
    {
        if (!Applies(port)) return null;
        double f = problem.Frequency.StopHz;
        Em3dWavePortTarget No(string why) => new(null, null, f, why);
        string? face = problem.FaceOf(port.Min, port.Max);
        if (face is null) return No("the port is not on a face of the air box");
        if (face is "zmin" or "zmax") return No($"the {face} face's cross-section is not a stack of horizontal layers, which kernel A solves");
        int axis = face[0] == 'x' ? 0 : 1;
        bool low = face.EndsWith("min", StringComparison.Ordinal);
        var b = problem.Boundary;
        double at = axis == 0 ? (low ? b.Min.X : b.Max.X) : (low ? b.Min.Y : b.Max.Y);
        string line = port.PositiveObject;
        if (!problem.Solids.Any(s => s.Name == line) && !problem.Sheets.Any(s => s.Name == line))
            return No($"its positive end ('{line}') is not a drawn conductor kernel A can take as the line");
        // A slab one micrometre deep at the face: the cross-section the port's mode solve sees. Its side faces are taken open: every
        // other metal is grounded already, side walls several substrate heights from the line barely move its quasi-TEM index, and
        // kernel A meshes a PEC wall the box's height tall into tens of thousands of segments (Em3dWallCalibration.Section).
        var open = axis == 0
            ? b.Faces with { YMin = Em3dBoundaryKind.Pmc, YMax = Em3dBoundaryKind.Pmc }
            : b.Faces with { XMin = Em3dBoundaryKind.Pmc, XMax = Em3dBoundaryKind.Pmc };
        var sectionProblem = problem with { Boundary = b with { Faces = open } };
        var section = Em3dWallCalibration.Section.Take(sectionProblem, face, axis, at, low ? 1 : -1, 0, Depth, [port], null, [line]);
        if (section is null || section.Refusal is { } refused)
            return No(LowerFirst(section?.Refusal ?? "the face's cross-section could not be taken"));
        // Metal standing on a PEC floor (a via on the face) is at ground potential already, as kernel A takes every metal but the
        // line to be: left in, it is a conductor touching two others, which kernel A cannot solve.
        if (b.Faces.ZMin == Em3dBoundaryKind.Pec)
            section.Pieces.RemoveAll(q => q.Role == Em3dRole.Conductor && q.Line < 0 && q.Z0 <= b.Min.Z + 1e-9);
        Em3dWallCalibration.Section.ModalMedium? medium;
        try { medium = section.Medium(f); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or OverflowException or ArithmeticException)
        {
            return No($"kernel A could not solve the face's cross-section ({e.Message.TrimEnd('.')})");
        }
        if (medium is null)
            return No(LowerFirst(section.MediumRefusal ?? "kernel A could not solve the face's cross-section"));
        Complex c = medium.CMode[0];
        if (!(c.Real > 0) || !(medium.Lambda[0] > 0)) return No("kernel A found no capacitance for the line on the face");
        double l = medium.Lambda[0] / c.Real;                   // λ = L′·Re C′ for one line (Em3dWallCalibration.Section.Medium)
        double neff = C0 * Complex.Sqrt(l * c).Real;
        double zc = Complex.Sqrt(l / c).Real;
        return neff > 0 && double.IsFinite(neff) ? new(neff, zc, f, null) : No("kernel A's index for the line was not a positive number");
    }

    private const double C0 = 299792458.0;

    /// <summary>The depth of the slab the cross-section is taken from, inward of the face.</summary>
    private const double Depth = 1e-6;

    /// <summary>The section's sentence as a clause: lower-cased first letter, no full stop, and without what it goes on to say about
    /// a wall port's own standards, which a wave port has none of.</summary>
    private static string LowerFirst(string s)
    {
        foreach (string tail in new[] { "; its C′", "; a wall port", ", which a wall port", "; the line standards" })
            if (s.IndexOf(tail, StringComparison.Ordinal) is var k and > 0) s = s[..k];
        return s.Length > 0 ? char.ToLowerInvariant(s[0]) + s[1..].TrimEnd('.') : s;
    }
}

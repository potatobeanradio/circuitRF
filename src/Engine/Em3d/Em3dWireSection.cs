// brief-em3d-4 R-em3d4-2a — a bond wire's cross-section, from its diameter d (em-3d.md §6.6).
//
// THE ONLY PLACE THESE NUMBERS APPEAR. §6.6's table, decided as perimeter-matched: above a few
// hundred megahertz the current is confined to a skin a few microns deep, so resistance is set by the
// PERIMETER it flows around, and the hexagon is sized so its perimeter equals the round wire's, πd.
//
//   | Quantity                 | Round, diameter d | Perimeter-matched hexagon  |
//   |--------------------------|-------------------|----------------------------|
//   | side                     | —                 | πd/6        ≈ 0.524 d      |
//   | height, flat to flat     | d                 | (√3π/6) d   ≈ 0.907 d      |
//   | width, corner to corner  | d                 | (π/3) d     ≈ 1.047 d      |
//   | perimeter                | πd                | πd                         |
//   | area                     | 0.785 d²          | 0.712 d² (−9.3 %)          |
//
// The cost, stated rather than hidden: 9.3 % less area, so DC resistance reads ~10 % high below
// ~200 MHz for a 1 mil gold wire, and a polygon crowds skin current into its corners. F0's Q3 measured
// that crowding (docs/design/em-3d-f0-findings.md); nothing here corrects for it (R-em3d4-2e).

namespace CircuitRF.Engine.Em3d;

/// <summary>A cross-section's dimensions, metres. <see cref="Height"/> is flat to flat for a
/// hexagon and is NOT the diameter — which is why loop heights are measured on surfaces, never on d.</summary>
public readonly record struct Em3dSectionSize(double Side, double Height, double Width, double Perimeter);

public static class Em3dWireSection
{
    /// <summary>How many facets a <see cref="Em3dSection.Circle"/> ring has. A multiple of four, so a
    /// vertex sits exactly at the bottom and at the top of the section — the surfaces a loop height and
    /// a foot are measured on. A backend that can sweep a true circle uses the path and the diameter.</summary>
    public const int CircleFacets = 16;

    /// <summary>The perimeter-matched hexagon for a round wire of diameter <paramref name="d"/>.</summary>
    public static Em3dSectionSize Hexagon(double d)
    {
        double side = Math.PI * d / 6;
        return new Em3dSectionSize(side, Math.Sqrt(3) * side, 2 * side, 6 * side);
    }

    /// <summary>A circle of diameter <paramref name="d"/>.</summary>
    public static Em3dSectionSize Circle(double d) => new(0, d, d, Math.PI * d);

    public static Em3dSectionSize Of(Em3dSection section, double d)
        => section == Em3dSection.Hexagon ? Hexagon(d) : Circle(d);

    /// <summary>
    /// The section's outline in its own plane, as (across, up) offsets from the axis, in order around
    /// the section. The hexagon starts with its two BOTTOM corners (indices 0 and 1),
    /// so its bottom face is the edge 0–1; the circle starts at its bottom vertex.
    /// </summary>
    public static IReadOnlyList<(double Across, double Up)> Outline(Em3dSection section, double d)
    {
        if (section == Em3dSection.Hexagon)
        {
            var s = Hexagon(d);
            double a = s.Side / 2, w = s.Width / 2, h = s.Height / 2;
            return [(-a, -h), (a, -h), (w, 0), (a, h), (-a, h), (-w, 0)];
        }
        var ring = new (double, double)[CircleFacets];
        double r = d / 2;
        for (int k = 0; k < CircleFacets; k++)
        {
            double t = -Math.PI / 2 + 2 * Math.PI * k / CircleFacets;
            ring[k] = (r * Math.Cos(t), r * Math.Sin(t));
        }
        return ring;
    }
}

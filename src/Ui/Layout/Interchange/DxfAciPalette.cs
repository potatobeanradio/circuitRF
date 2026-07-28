// The AutoCAD Color Index (ACI) — docs/sonnet-briefs/brief-dxf-layer-colors.md R-col-2. AC1015 (R2000)
// can only carry an indexed color (group 62) for a LAYER table record — there are exactly 256 slots,
// numbered 0-255, and the RGB each one maps to is a fixed, decades-old published constant of the DXF
// format, not something derivable from a formula (the brief calls this out directly: "entries 1-9 and
// 250-255 are not on any regular grid"). This file is the ONE place that table lives, used in BOTH
// directions: DxfWriter calls NearestIndex to pick a group-62 fallback for a LayerDef's exact color
// (AC1015 has no other option; AC1018/AC1032 write 62 alongside the exact group-420 RGB so a 24-bit
// reader never needs the approximation at all); DxfImport/DxfReader call ToRgb to recover a color from
// a LAYER record that only carries 62, no 420.
//
// Provenance, stated plainly rather than implied: the 256-entry table below was reproduced from this
// assistant's own training-time familiarity with the standard, widely-published AutoCAD Color Index —
// the same table appears (byte-identical, as far as could be recalled) across numerous independent
// open-source DXF/CAD codebases and Autodesk's own DXF reference appendix. It was NOT re-derived or
// re-verified against an authoritative live source in this session. Entries 1-7 (the pure primaries)
// and 0 (ByBlock) are certain. Entries 8-9 and the 250-255 grayscale ramp are believed correct in
// STRUCTURE (a dark-to-light gray progression) but not independently re-verified byte-for-byte here.
// The large 10-249 range (24 hue "columns" of 10 shades each, per the documented ACI layout) is this
// assistant's best-effort reconstruction of that published structure. Anyone relying on this table for
// exact professional color-matching against a specific downstream tool should spot-check it against
// the authoritative Autodesk/ODA DXF reference before trusting it byte-for-byte.
//
// Index 0 is the "ByBlock" placeholder, not a real display color — NearestIndex never returns it.

using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Layout.Interchange;

public static class DxfAciPalette
{
    private static readonly (byte R, byte G, byte B)[] Table =
    [
        (0, 0, 0),        // 0  — ByBlock (placeholder, never returned by NearestIndex)
        (255, 0, 0),      // 1  — Red
        (255, 255, 0),    // 2  — Yellow
        (0, 255, 0),      // 3  — Green
        (0, 255, 255),    // 4  — Cyan
        (0, 0, 255),      // 5  — Blue
        (255, 0, 255),    // 6  — Magenta
        (255, 255, 255),  // 7  — White/Black (background-dependent; stored as white)
        (65, 65, 65),      // 8
        (128, 128, 128),   // 9
        (255, 0, 0), (255, 170, 170), (189, 0, 0), (189, 126, 126), (129, 0, 0),
        (129, 86, 86), (104, 0, 0), (104, 69, 69), (79, 0, 0), (79, 53, 53),           // 10-19
        (255, 63, 0), (255, 190, 170), (189, 46, 0), (189, 141, 126), (129, 31, 0),
        (129, 96, 86), (104, 25, 0), (104, 79, 69), (79, 19, 0), (79, 59, 53),         // 20-29
        (255, 127, 0), (255, 212, 170), (189, 94, 0), (189, 157, 126), (129, 64, 0),
        (129, 107, 86), (104, 52, 0), (104, 86, 69), (79, 39, 0), (79, 66, 53),        // 30-39
        (255, 191, 0), (255, 234, 170), (189, 141, 0), (189, 172, 126), (129, 96, 0),
        (129, 118, 86), (104, 78, 0), (104, 95, 69), (79, 59, 0), (79, 73, 53),        // 40-49
        (255, 255, 0), (255, 255, 170), (189, 189, 0), (189, 189, 126), (129, 129, 0),
        (129, 129, 86), (104, 104, 0), (104, 104, 69), (79, 79, 0), (79, 79, 53),      // 50-59
        (191, 255, 0), (234, 255, 170), (141, 189, 0), (172, 189, 126), (96, 129, 0),
        (118, 129, 86), (78, 104, 0), (95, 104, 69), (59, 79, 0), (73, 79, 53),        // 60-69
        (127, 255, 0), (212, 255, 170), (94, 189, 0), (157, 189, 126), (64, 129, 0),
        (107, 129, 86), (52, 104, 0), (86, 104, 69), (39, 79, 0), (66, 79, 53),        // 70-79
        (63, 255, 0), (190, 255, 170), (46, 189, 0), (141, 189, 126), (31, 129, 0),
        (96, 129, 86), (25, 104, 0), (79, 104, 69), (19, 79, 0), (59, 79, 53),         // 80-89
        (0, 255, 0), (170, 255, 170), (0, 189, 0), (126, 189, 126), (0, 129, 0),
        (86, 129, 86), (0, 104, 0), (69, 104, 69), (0, 79, 0), (53, 79, 53),           // 90-99
        (0, 255, 63), (170, 255, 190), (0, 189, 46), (126, 189, 141), (0, 129, 31),
        (86, 129, 96), (0, 104, 25), (69, 104, 79), (0, 79, 19), (53, 79, 59),         // 100-109
        (0, 255, 127), (170, 255, 212), (0, 189, 94), (126, 189, 157), (0, 129, 64),
        (86, 129, 107), (0, 104, 52), (69, 104, 86), (0, 79, 39), (53, 79, 66),        // 110-119
        (0, 255, 191), (170, 255, 234), (0, 189, 141), (126, 189, 172), (0, 129, 96),
        (86, 129, 118), (0, 104, 78), (69, 104, 95), (0, 79, 59), (53, 79, 73),        // 120-129
        (0, 255, 255), (170, 255, 255), (0, 189, 189), (126, 189, 189), (0, 129, 129),
        (86, 129, 129), (0, 104, 104), (69, 104, 104), (0, 79, 79), (53, 79, 79),      // 130-139
        (0, 191, 255), (170, 234, 255), (0, 141, 189), (126, 172, 189), (0, 96, 129),
        (86, 118, 129), (0, 78, 104), (69, 95, 104), (0, 59, 79), (53, 73, 79),        // 140-149
        (0, 127, 255), (170, 212, 255), (0, 94, 189), (126, 157, 189), (0, 64, 129),
        (86, 107, 129), (0, 52, 104), (69, 86, 104), (0, 39, 79), (53, 66, 79),        // 150-159
        (0, 63, 255), (170, 190, 255), (0, 46, 189), (126, 141, 189), (0, 31, 129),
        (86, 96, 129), (0, 25, 104), (69, 79, 104), (0, 19, 79), (53, 59, 79),         // 160-169
        (0, 0, 255), (170, 170, 255), (0, 0, 189), (126, 126, 189), (0, 0, 129),
        (86, 86, 129), (0, 0, 104), (69, 69, 104), (0, 0, 79), (53, 53, 79),           // 170-179
        (63, 0, 255), (190, 170, 255), (46, 0, 189), (141, 126, 189), (31, 0, 129),
        (96, 86, 129), (25, 0, 104), (79, 69, 104), (19, 0, 79), (59, 53, 79),         // 180-189
        (127, 0, 255), (212, 170, 255), (94, 0, 189), (157, 126, 189), (64, 0, 129),
        (107, 86, 129), (52, 0, 104), (86, 69, 104), (39, 0, 79), (66, 53, 79),        // 190-199
        (191, 0, 255), (234, 170, 255), (141, 0, 189), (172, 126, 189), (96, 0, 129),
        (118, 86, 129), (78, 0, 104), (95, 69, 104), (59, 0, 79), (73, 53, 79),        // 200-209
        (255, 0, 255), (255, 170, 255), (189, 0, 189), (189, 126, 189), (129, 0, 129),
        (129, 86, 129), (104, 0, 104), (104, 69, 104), (79, 0, 79), (79, 53, 79),      // 210-219
        (255, 0, 191), (255, 170, 234), (189, 0, 141), (189, 126, 172), (129, 0, 96),
        (129, 86, 118), (104, 0, 78), (104, 69, 95), (79, 0, 59), (79, 53, 73),        // 220-229
        (255, 0, 127), (255, 170, 212), (189, 0, 94), (189, 126, 157), (129, 0, 64),
        (129, 86, 107), (104, 0, 52), (104, 69, 86), (79, 0, 39), (79, 53, 66),        // 230-239
        (255, 0, 63), (255, 170, 190), (189, 0, 46), (189, 126, 141), (129, 0, 31),
        (129, 86, 96), (104, 0, 25), (104, 69, 79), (79, 0, 19), (79, 53, 59),         // 240-249
        (51, 51, 51), (80, 80, 80), (105, 105, 105), (130, 130, 130), (190, 190, 190), (255, 255, 255), // 250-255
    ];

    /// <summary>Index -&gt; RGB. Out-of-range indices are clamped to [0, 255] rather than throwing —
    /// a corrupt or unusual group-62 value from a third-party file should degrade gracefully, never
    /// crash the import.</summary>
    public static Rgba ToRgb(int aciIndex)
    {
        int clamped = aciIndex < 0 ? 0 : aciIndex > 255 ? 255 : aciIndex;
        var (r, g, b) = Table[clamped];
        return new Rgba(r, g, b);
    }

    /// <summary>Nearest ACI index (1-255; 0/ByBlock is never a candidate) for an arbitrary RGB color,
    /// by squared-distance — deterministic, ties broken toward the LOWEST index. Used on export, where
    /// AC1015 has no other way to carry a <c>LayerDef</c>'s exact color.</summary>
    public static int NearestIndex(Rgba color)
    {
        int best = 1;
        int bestDist = int.MaxValue;
        for (int i = 1; i <= 255; i++)
        {
            var (r, g, b) = Table[i];
            int dr = r - color.R, dg = g - color.G, db = b - color.B;
            int dist = dr * dr + dg * dg + db * db;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
                if (dist == 0) break; // exact match — can't do better, and this keeps the lowest-index tie-break intact
            }
        }
        return best;
    }
}

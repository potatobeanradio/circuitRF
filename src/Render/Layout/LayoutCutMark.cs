// brief-icp-7 R-icp7-3 — a cell instance the open .cem cuts out of its EM problem, drawn on the layout: what leaves the problem
// (hatched: the instance's own metal in Pin mode, its box in Cell mode) and each pin's verdict as a small glyph at its face. Like a
// component port's mark it is not in the .clay: the .cem builds it (ICP-2's cut, read from the cached build) and pushes it, and only
// the interactive canvas sets LayoutRenderOptions.CutMarks, so no render, picture or export draws one.

namespace CircuitRF.Render;

/// <summary>What a cut pin is, as its glyph shows it.</summary>
public enum LayoutCutPinKind
{
    /// <summary>A port at its face: the port's own arrow is drawn (a component-port mark), and nothing more here.</summary>
    Routed,
    /// <summary>Tied to ground at its face: a ground mark.</summary>
    Grounded,
    /// <summary>It touches another cut instance's pin: a join mark to that pin.</summary>
    Abutting,
    /// <summary>Nothing meets it: a warning marker.</summary>
    Floating,
    /// <summary>Only the return of other pins' ports: a small ring.</summary>
    Return,
    /// <summary>Left out of the run: a small cross, its tooltip the reason.</summary>
    LeftOut,
}

/// <summary>One pin's glyph, at its face's middle (DBU). <paramref name="ToX"/>/<paramref name="ToY"/> are the pin an abutting one
/// joins.</summary>
public sealed record LayoutCutPinMark(long X, long Y, LayoutCutPinKind Kind, string Tip, long? ToX = null, long? ToY = null);

/// <summary>One cut instance: its path (the first segment names the placement on this layout that a press selects), its left-out
/// outline, whether that is a box, its pins' glyphs, and the hover line over its outline.</summary>
public sealed record LayoutCutMark(string Path, IReadOnlyList<PolygonShape> Outline, bool Box, IReadOnlyList<LayoutCutPinMark> Pins, string Tip)
{
    /// <summary>What the outline covers.</summary>
    public Bbox Region
    {
        get
        {
            long x0 = long.MaxValue, y0 = long.MaxValue, x1 = long.MinValue, y1 = long.MinValue;
            foreach (var p in Outline)
                for (int i = 0; i + 1 < p.Xy.Length; i += 2)
                {
                    x0 = Math.Min(x0, p.Xy[i]); x1 = Math.Max(x1, p.Xy[i]);
                    y0 = Math.Min(y0, p.Xy[i + 1]); y1 = Math.Max(y1, p.Xy[i + 1]);
                }
            return x0 > x1 ? Bbox.Empty : new Bbox(x0, y0, x1, y1);
        }
    }

    /// <summary>The placement on the open layout this instance is, or lies inside: its path's first segment.</summary>
    public string TopSegment => Path.Split('/')[0];
}

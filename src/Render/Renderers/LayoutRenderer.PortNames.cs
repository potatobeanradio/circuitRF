// brief-dfr32-2 R-dfr32-2-4 / R-dfr32-2-5 — where a port's NAME is drawn, and how big.
//
// A port's name is an annotation, as a schematic pin's name is: drawn at a fixed screen size (the size a ruler's
// readout takes by default), beside the port's mark on its OUTWARD side — past the tail of an edge port's arrow, or
// past an internal port's ring — at every zoom. The label's stored Height is not read here; it still governs what an
// export (GDSII, DXF) writes.
//
// Names do not overlap. Ports are placed in document order, and a name whose box meets an earlier name's moves
// outward along its own port's direction, a step at a time, until it is clear; one that moved further than its own
// height gets a thin leader back to its mark. Nothing here reads the pan or the order tiles were gathered in, so the
// same layout at the same zoom always places its names the same way, and `render` (which draws through this same
// pass) makes the picture the canvas shows.
//
// brief-dfr33-1 R-dfr33-1-2 — a name that cannot be placed NEAR its port is shortened, then left out. Moving a name
// without limit turned nine ports along one line into a row of leaders longer than the feature they label. So a name
// moves at most PortNameMaxTravelHeights of its own heights; past that the port's NUMBER alone ("4" for "P4") is
// tried the same way, and past that no name is drawn. The arrow stays, the canvas shows the name on hover, and a
// name left out at one zoom comes back when zooming in makes room — placement is a function of the zoom alone.

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.Layout.Text;
using SkiaSharp;

namespace CircuitRF.Render;

public static partial class LayoutRenderer
{
    /// <summary>A port name's size, in points: a ruler's default readout size (<c>RulerAnnotation.TextSizePt</c>).</summary>
    internal const double PortNameTextPt = 11.0;

    /// <summary>The clear space between a port's mark and its name, in device pixels.</summary>
    internal const double PortNameGapDevicePixels = 3.0;

    /// <summary>The clear space between two names, in device pixels (R-dfr33-1-2): at 3 px, "P5" and "P4" side by side
    /// read as one word, "P5P4".</summary>
    internal const double PortNameSpacingDevicePixels = 7.0;

    /// <summary>How far a name may move outward looking for clear space, in its own heights (R-dfr33-1-2).</summary>
    internal const int PortNameMaxTravelHeights = 3;

    /// <summary>One port's name as drawn: the label to draw (the stored one moved and resized, never the model's), its
    /// box, and — when it moved more than its own height to clear another name — the leader from the name back to the
    /// port's mark. <see cref="Shortened"/> when the port's number stands in for its full name.</summary>
    internal readonly record struct PortNamePlacement(int Index, LabelShape Drawn, Bbox Box,
                                                      (long X0, long Y0, long X1, long Y1)? Leader,
                                                      bool Shortened = false);

    /// <summary>A port name's height in DBU at <paramref name="devicePxPerDbu"/>: <see cref="PortNameTextPt"/> on screen.</summary>
    internal static long PortNameHeightDbu(double devicePxPerDbu)
        => Math.Max(1, (long)Math.Round(PortNameTextPt * DevicePixelsPerPoint / devicePxPerDbu));

    /// <summary>
    /// Every port name of a frame, placed. <paramref name="devicePxPerDbu"/> zero or less (no viewport) draws each name
    /// where it always was: at its anchor, at its stored height. A port whose name (and number) found no clear space
    /// within reach is absent from the result and its index added to <paramref name="omitted"/>.
    /// </summary>
    internal static List<PortNamePlacement> PlacePortNames(IEnumerable<(int Index, LabelShape Label)> ports,
        LayoutPortDirection.ConductorLookup? conductorAt, double devicePxPerDbu, Technology? tech, int dbuPerMicron,
        List<int>? omitted = null)
    {
        var placed = new List<PortNamePlacement>();
        var ordered = ports.Where(p => !string.IsNullOrEmpty(p.Label.Text)).OrderBy(p => p.Index).ToList();
        if (devicePxPerDbu <= 0)
        {
            foreach (var (index, label) in ordered)
                placed.Add(new(index, label, new Bbox(label.X, label.Y, label.X, label.Y), null));
            return placed;
        }

        long height = PortNameHeightDbu(devicePxPerDbu);
        long gap = Math.Max(1, (long)Math.Round(PortNameGapDevicePixels / devicePxPerDbu));
        long spacing = Math.Max(1, (long)Math.Round(PortNameSpacingDevicePixels / devicePxPerDbu));
        foreach (var (index, label) in ordered)
        {
            // The mark: its box, the point its name is pushed from, and the outward direction (against the current).
            int ox = 0, oy = 1;
            Bbox mark = new(label.X, label.Y, label.X, label.Y);
            if (LayoutPortDirection.Resolve(conductorAt, label) is { } hint)
            {
                var kind = label.PortKind ?? LayoutPortDirection.InferredKind(hint);
                bool atAnchor = LayoutPortDirection.MarkAtAnchor(kind, hint);
                var drawnHint = atAnchor ? hint : EdgePortSheet.Bar(hint, label, tech, dbuPerMicron);
                mark = LayoutPortDirection.MarkerBbox(label, drawnHint, atAnchor, 0);
                var (ux, uy) = LayoutPortDirection.UnitVector(hint.Direction);
                (ox, oy) = (-ux, -uy);
            }

            // Centre of the mark's outward face, then the name's box just beyond it.
            long cx = (mark.MinX + mark.MaxX) / 2, cy = (mark.MinY + mark.MaxY) / 2;
            long faceX = ox > 0 ? mark.MaxX : ox < 0 ? mark.MinX : cx;
            long faceY = oy > 0 ? mark.MaxY : oy < 0 ? mark.MinY : cy;

            // The full name, then the number alone where it says something shorter, then nothing.
            string? number = EmPortExtraction.TryParseNumber(label.Text, out int n)
                             && n.ToString(System.Globalization.CultureInfo.InvariantCulture) is var digits
                             && digits != label.Text.Trim() ? digits : null;
            bool done = false;
            foreach (var text in number is null ? new[] { label.Text } : new[] { label.Text, number })
            {
                var drawn = NameLabel(label, 0, 0, height, text);
                var ink = NameInk(drawn, tech);
                long w = ink.MaxX - ink.MinX, h = ink.MaxY - ink.MinY;
                long ax = faceX + ox * (gap + w / 2), ay = faceY + oy * (gap + h / 2);
                long startX = ax, startY = ay;

                Bbox BoxAt(long x, long y) => new(x + ink.MinX, y + ink.MinY, x + ink.MaxX, y + ink.MaxY);
                bool Clear(Bbox b) => placed.All(q => !Grown(q.Box, spacing).Intersects(b));
                long step = Math.Max(1, height / 2);
                long reach = PortNameMaxTravelHeights * Math.Max(h, height);
                bool clear = Clear(BoxAt(ax, ay));
                while (!clear && Math.Abs(ax - startX) + Math.Abs(ay - startY) + step <= reach)
                {
                    ax += ox * step;
                    ay += oy * step;
                    clear = Clear(BoxAt(ax, ay));
                }
                if (!clear) continue;

                var box = BoxAt(ax, ay);
                long moved = Math.Abs(ax - startX) + Math.Abs(ay - startY);
                (long, long, long, long)? leader = moved > h
                    ? (ox != 0 ? (ox > 0 ? box.MinX : box.MaxX) : ax, oy != 0 ? (oy > 0 ? box.MinY : box.MaxY) : ay, faceX, faceY)
                    : null;
                placed.Add(new(index, NameLabel(label, ax, ay, height, text), box, leader, Shortened: !ReferenceEquals(text, label.Text)));
                done = true;
                break;
            }
            if (!done) omitted?.Add(index);
        }
        return placed;

        static Bbox Grown(Bbox b, long by) => new(b.MinX - by, b.MinY - by, b.MaxX + by, b.MaxY + by);
    }

    /// <summary>The label a port's name is drawn as: the port's text and font, upright, at the given anchor and height,
    /// at the font's own pen.</summary>
    private static LabelShape NameLabel(LabelShape port, long x, long y, long height, string? text = null) => new()
    {
        Layer = port.Layer, X = x, Y = y, Text = text ?? port.Text, Height = height, IsPort = true,
        Style = port.Style, Font = port.Font,
    };

    /// <summary>The box a name's ink covers about its own anchor (centred, as every port name is).</summary>
    private static Bbox NameInk(LabelShape drawn, Technology? tech)
    {
        if (drawn.Font == LabelFont.Stroke)
            return StrokeText.Bounds(drawn, centred: true, tech) ?? new Bbox(0, 0, 0, 0);
        // Measured at a reference size and scaled: a DBU height can be far outside a font's comfortable range.
        const float reference = 100f;
        double k = drawn.Height / (double)reference;
        using var font = new SKFont(LayoutTextOutline.ResolveTypeface(drawn.Style), reference);
        var (align, dy) = LayoutTextOutline.ResolveLabelAnchor(drawn, font, centred: true);
        float width = font.MeasureText(drawn.Text);
        float left = align switch { SKTextAlign.Center => -width / 2f, SKTextAlign.Right => -width, _ => 0f };
        // Path space is y-down: the glyphs sit from dy + ascent (negative) to dy + descent there, so y-up negates.
        var m = font.Metrics;
        return new Bbox((long)Math.Floor(left * k), (long)Math.Floor(-(dy + m.Descent) * k),
                        (long)Math.Ceiling((left + width) * k), (long)Math.Ceiling(-(dy + m.Ascent) * k));
    }
}

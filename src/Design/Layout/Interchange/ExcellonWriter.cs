// Excellon drill writer (docs/sonnet-briefs/brief-L4c-gerber-export.md §5). Separate format, separate
// file(s): M48 header, METRIC units, tool definitions (T1C0.300), a body of T<n> tool selections and
// X…Y… hits, M30. A Via contributes exactly one hit here (its pad flash is the Gerber copper layer's
// concern — GerberWriter — neither alone is a via). §5's own text gives a plated/non-plated split "by
// convention, taken from the .ctech mapping if it carries one" — InterchangeMapping has no such field
// today (only GdsiiLayer/GdsiiDatatype/DxfLayerName/GerberSuffix/GerberFileFunction), so this always
// emits a single plated file and the caller reports that choice explicitly, per the brief's own
// fallback instruction, rather than silently guessing a split that isn't there to read.
//
// R-via-5 (docs/sonnet-briefs/brief-via-primitive-and-stackup.md §4.2): a bare Circle on a
// drill-function layer is how MMIC genuinely draws a via, and refusing to emit a hole for it would let
// a design that looks correct ship a board with no holes — so `unpairedCircles` contributes hits here
// too, sharing the SAME tool-dedup table as `vias`. The caller (GerberExport) is what counts and
// reports these as unpaired; this file only drills the holes.

namespace CircuitRF.Design.Layout.Interchange;

public static class ExcellonWriter
{
    public sealed record DrillWriteResult(int ToolsDefined, int HitsWritten);

    /// <summary>Writes every <paramref name="vias"/> hit — plus, per R-via-5, one hit per
    /// <paramref name="unpairedCircles"/> entry (diameter = its own 2×R) — into one plated Excellon
    /// file. Tools are deduped by diameter (§5: "exactly as apertures are deduped") across BOTH sources
    /// together, numbered T1, T2, … in first-use order (vias first, then unpaired circles). Coordinates
    /// use <see cref="GerberFormat.FormatDecimalMm"/> — an explicit decimal point at the SAME digit
    /// resolution the sibling Gerber files use (§5: "consistent with the Gerber files"), sidestepping
    /// classic Excellon's zero-suppression ambiguity while staying exact (pure integer-string
    /// manipulation, never a <c>double</c> conversion).</summary>
    public static DrillWriteResult Write(
        Stream stream, IReadOnlyList<ViaShape> vias, GerberFormat format,
        IReadOnlyList<CircleShape>? unpairedCircles = null)
    {
        var hits = new List<(long X, long Y, long DiameterDbu)>(vias.Count + (unpairedCircles?.Count ?? 0));
        foreach (var v in vias) hits.Add((v.X, v.Y, Math.Max(v.DrillSize, 1)));
        if (unpairedCircles is not null)
            foreach (var c in unpairedCircles) hits.Add((c.Cx, c.Cy, Math.Max(c.R * 2, 1)));

        var toolByDiameter = new Dictionary<long, int>();
        var ordered = new List<(int Tool, long DiameterDbu)>();
        int next = 1;
        foreach (var (_, _, d) in hits)
        {
            if (toolByDiameter.ContainsKey(d)) continue;
            toolByDiameter[d] = next;
            ordered.Add((next, d));
            next++;
        }

        using var w = new StreamWriter(stream, System.Text.Encoding.ASCII, -1, leaveOpen: true) { NewLine = "\n" };

        w.WriteLine("M48");
        w.WriteLine("METRIC");
        foreach (var (tool, diameter) in ordered)
            w.WriteLine($"T{tool}C{format.FormatDecimalMm(diameter)}");
        w.WriteLine("%");
        w.WriteLine("G90"); // absolute coordinates
        w.WriteLine("G05"); // drill mode

        int currentTool = -1;
        foreach (var (x, y, d) in hits)
        {
            int tool = toolByDiameter[d];
            if (tool != currentTool) { w.WriteLine($"T{tool}"); currentTool = tool; }
            w.WriteLine($"X{format.FormatDecimalMm(x)}Y{format.FormatDecimalMm(y)}");
        }

        w.WriteLine("M30");
        w.Flush();

        return new DrillWriteResult(ordered.Count, hits.Count);
    }
}

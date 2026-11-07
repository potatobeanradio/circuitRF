// brief-lcp-4 — a component port drawn on the layout. Component ports are not in the .clay: the open .cem builds them (LCP-1's
// strip and delta gap) and pushes them here, as it pushes its mesh. The layout holds no opinion about them, edits nothing with
// them and never writes them: the interactive canvas is the only caller that sets LayoutRenderOptions.ComponentPortMarks, so a
// render, a picture and every export draw none, by the rule the mesh overlay follows.

namespace CircuitRF.Render;

/// <summary>One built component port, as the layout canvas draws it.</summary>
/// <param name="Number">The port's number in the run.</param>
/// <param name="Name">The port's name: the refdes, or refdes.pin for a part with several signal pads.</param>
/// <param name="Refdes">The part.</param>
/// <param name="Gap">The delta-gap label the run solves with: its point is the cut, its direction the port's polarity.</param>
/// <param name="Strip">The strip the run adds across the part's gap. Not drawn: it is not in the layout. Its width is the
/// marker's.</param>
public sealed record LayoutComponentPortMark(int Number, string Name, string Refdes, LabelShape Gap, RectShape Strip)
{
    /// <summary>The text the mark carries: the number, spelled as a drawn port's name is.</summary>
    public string Text => "P" + Number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>What the mark covers, for a hover: the strip.</summary>
    public Bbox Region => LayoutGeometry.BboxOf(Strip);

    /// <summary>The hover line: the port and the part, and that it is the setup's, not the layout's.</summary>
    public string HoverText(string? setupName)
        => $"Port {Number} · {Name}: a component port of {(setupName is { Length: > 0 } s ? s : "the EM setup")}. " +
           "It is not in the layout; the part is what moves it.";
}

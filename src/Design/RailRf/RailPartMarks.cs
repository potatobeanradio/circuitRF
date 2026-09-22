// Where a part is on the board — the ONE resolution, for every surface that draws one.
//
// ── WHY THIS IS NOT IN THE VIEW MODEL, AND NOT IN THE RENDERER ─────────────────────────────────
//
// Two marks are drawn on railRF's board and both are the same question: the part SELECTED in the
// parts table, and every part the table says is NOT FITTED. And there are four surfaces that draw
// them — the window's board panel, Copy, the window's Export report and `circuitrf rail -o`.
//
// The renderer may not answer it. `RailPartHighlight`'s own header states the rule: which pads
// belong to C7 is a question about the board netlist, and answering it below the firewall would put
// a second copy of PdnAttachments' resolution in the drawing code. The renderer is handed geometry.
//
// The view model may not own it either, because `src/Cli` cannot reference `src/Ui` — and a headless
// report whose marks were resolved by a second implementation is the divergence the CLI chapter's
// whole "no second route" rule exists against. So the resolution lives here, beside the rest of
// railRF's document-layer logic, and each surface projects the result into the renderer's own
// draw argument.

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Pdn;

namespace CircuitRF.Design.RailRf;

/// <summary>
/// One part's place on the board: every pad it names, and the box its body covers where anything
/// states one.
/// </summary>
/// <param name="Label">What is written beside the mark — the refdes, as the table spells it.</param>
/// <param name="Pads">Each pad, DBU. <b>Every one, not a centroid</b> — a decoupling capacitor's two
/// pads are what the mounting loop is measured between, and the middle of the part is the one place
/// on it that no answer is about.</param>
/// <param name="Body">The body box where a placement states one, else null.</param>
public readonly record struct RailPartMark(
    string Label,
    IReadOnlyList<(long X, long Y)> Pads,
    Bbox? Body);

/// <summary>Where railRF's parts are on the board. <b>Resolves; draws nothing.</b></summary>
public static class RailPartMarks
{
    /// <summary>
    /// How far past the pads a body box reaches when it is built from a placement centroid, DBU.
    /// </summary>
    /// <remarks>
    /// A pad is a COORDINATE here and not a shape, so a box drawn through two of them has no width
    /// of its own. <c>RailPartHighlight.PadReachDbu</c> is this constant — the renderer's fallback
    /// outline and this body box have to agree, or one part is marked at two different sizes
    /// depending on which surface asked.
    /// </remarks>
    public const long PadReachDbu = 300_000;   // 0.3 mm at the default 1000 DBU/µm

    /// <summary>
    /// Where one anchor is, or null where the board places it nowhere.
    /// </summary>
    /// <remarks>
    /// <b>Null rather than an empty mark</b>, and the caller is what decides whether to draw: a mark
    /// at the placement centroid of a part with no pads — or at the origin — says something that is
    /// not true, and the parts table already prints <i>not placed</i> on that row.
    /// </remarks>
    public static RailPartMark? For(
        RailPortAnchor anchor, string label,
        IReadOnlyList<PlacedPin> pads, PlacementTable? placement = null)
    {
        ArgumentNullException.ThrowIfNull(pads);

        var found = PdnAttachments.Resolve(anchor, pads);

        // The body box where a placement states a centroid — it is what the part actually COVERS,
        // and on a bulk capacitor that is several millimetres wider than its two pads. A coordinate
        // anchor names no component, so it has none.
        Bbox? body = null;
        if (anchor.Refdes is { Length: > 0 } refdes &&
            placement is { Refusal: null } table &&
            table.Rows.FirstOrDefault(r =>
                string.Equals(r.Refdes, refdes, StringComparison.OrdinalIgnoreCase)) is
                { Refdes: { Length: > 0 } } placed)
        {
            var box = new Bbox(placed.X, placed.Y, placed.X, placed.Y);
            foreach (var (x, y) in found) box = box.Union(new Bbox(x, y, x, y));
            body = new Bbox(box.MinX - PadReachDbu, box.MinY - PadReachDbu,
                            box.MaxX + PadReachDbu, box.MaxY + PadReachDbu);
        }

        if (found.Count == 0 && body is null) return null;

        return new RailPartMark(label, [.. found], body);
    }

    /// <summary>
    /// Every part of <paramref name="rail"/> the document says is NOT FITTED, where the board places
    /// it — <b>the mark that says a row is in the table and not in the answer</b>.
    /// </summary>
    /// <remarks>
    /// <b>The land pattern is never removed, and nothing here could remove it.</b> What is drawn
    /// under a part is its footprint — copper, mask and silkscreen — and all of it is etched and
    /// printed whether or not a component is soldered on top; unmounting says what is fitted TO the
    /// geometry, never what the geometry is (R-rail23-1c). So the pads stay and this marks them,
    /// exactly as <c>RailMarkerKind.Observation</c> is drawn rather than omitted: <i>not there</i>
    /// and <i>there and contributing nothing</i> must not look the same.
    ///
    /// <para>Through <see cref="For"/>, which is the selection's own call, so an unmounted part is
    /// marked at exactly the pads the same part is marked at when it is picked in the table.</para>
    /// </remarks>
    public static IReadOnlyList<RailPartMark> NotFitted(
        RailSpec? rail, IReadOnlyList<PlacedPin> pads, PlacementTable? placement = null)
    {
        if (rail is null) return [];
        ArgumentNullException.ThrowIfNull(pads);

        var marks = new List<RailPartMark>();
        foreach (var part in rail.Parts)
        {
            if (part.Mounted || part.Refdes is not { Length: > 0 } refdes) continue;
            if (For(new RailPortAnchor { Refdes = refdes }, refdes, pads, placement) is { } mark)
                marks.Add(mark);
        }
        return marks;
    }
}

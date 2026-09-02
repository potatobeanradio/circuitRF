using System;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using SkiaSharp;

namespace CircuitRF.Ui.Matching;

/// <summary>One component label found under a point, in WORLD units.</summary>
/// <param name="ComponentId">The projected component's id — an element's ladder name, or "Termination 1".</param>
/// <param name="Row">Which label row: 0 type, 1 instance name, 2 the value.</param>
/// <param name="Text">The EDITABLE part of the row — "1.53 nH", not "L = 1.53 nH".</param>
/// <param name="BaseX">The row's own left edge, world units.</param>
/// <param name="BaselineY">The row's Skia baseline, world units.</param>
/// <param name="PrefixWidth">
/// Width of the "L = " / "Z = " part the editor opens PAST, world units; zero for a row with no
/// name-and-equals prefix.
/// </param>
public sealed record MatchLabelHit(
    string ComponentId, int Row, string Text, double BaseX, double BaselineY, double PrefixWidth);

/// <summary>
/// Where the Designer's network pane draws each component label, and which one is under a point.
/// </summary>
/// <remarks>
/// <b>Pure geometry, deliberately separated from the canvas that uses it.</b> A hit-test that only
/// exists inside a <c>Control</c> can only be checked by clicking, and "the double-click does
/// nothing" is a report with no way to tell a wrong band from a pointer that never arrived. Every
/// number here is a function of the projected <see cref="SchematicModel"/> alone, so a test can ask
/// it directly.
///
/// <para><b>It reads the renderer's own row geometry, with the renderer's own arguments.</b>
/// <c>SchematicRenderer.DrawLabels</c> passes <c>c.Ports.Count / 2</c> and
/// <c>c.GlyphBbMaxY - c.Y</c> to <see cref="SchematicComponent.LabelRowGeometry"/>; anything else
/// here would put the clickable zone somewhere the text is not.
/// <c>SchematicHitTest.TestComponentLabels</c> cannot be reused instead — it walks an
/// <c>EditableSchematic</c>, and this pane's drawing is a projection with no edit model behind it.</para>
/// </remarks>
public static class MatchSchematicLabels
{
    /// <summary>How far outside a row's measured text still counts as a hit, world units.</summary>
    /// <remarks>
    /// The same 10 units <c>SchematicHitTest</c> allows on a schematic page — at the pane's own scale
    /// that is a fraction of a character, and without it the last glyph's right edge is a miss.
    /// </remarks>
    public const double Slack = 10.0;

    /// <summary>
    /// The label under one world point, or null. <paramref name="tolerance"/> widens every row's band
    /// and text extent, in world units — see the remarks on why the caller supplies it.
    /// </summary>
    /// <remarks>
    /// <b>Owner-reported, 2026-08-20: "cannot double click on TermG value to get the inline text
    /// editor."</b> Two separate things were wrong and each on its own is enough to make the gesture
    /// fail.
    ///
    /// <para><b>1. The rows OVERLAP, and first-match order gave the overlap to the wrong one.</b>
    /// <see cref="SchematicComponent.LabelRowGeometry"/>'s band runs from 76 world units above the
    /// baseline to 25.6 below — 101.6 tall — on a row pitch of 72. Consecutive bands therefore share
    /// 29.6 units, and a loop that returns the first containing row hands all of it to the row ABOVE.
    /// A label's glyphs sit above their own baseline, so <b>the top 34% of the value text resolved to
    /// the instance-name row</b> — which is not editable, so the double-click did nothing at all,
    /// silently, about a third of the time. Resolution is now by <b>nearest band CENTRE</b>, not first
    /// match: the split between two rows lands halfway between their centres, in the middle of the
    /// shared strip. A row's own GLYPHS are what decides it — those abut without overlapping, so
    /// "is the point on this row's text?" has exactly one answer, and only a point that is on no row's
    /// text at all falls back to the nearest.</para>
    ///
    /// <para><b>2. A world-unit hit band is the wrong size in a pane that frames a whole network.</b>
    /// A schematic page is read at a zoom where a component is 100-300 px wide and a label row is
    /// comfortably tall. Measured off the pane's own captured figure, this one runs at a zoom of
    /// 0.0674: the row pitch is <b>4.85 screen pixels</b> and the text 4.7. That is not a target anyone
    /// can hit repeatedly. The caller therefore passes a tolerance derived from the CURRENT zoom, so
    /// the pick area has a floor in screen pixels and shrinks back to nothing as the user zooms in.</para>
    ///
    /// <para><b>3. And even then, a 4.7-pixel strip with two dead rows on top of it is not usable.</b>
    /// Only the value row is editable in this pane — the type is what the synthesis produced and the
    /// name is the key every stored transform resolves through — so the type and name rows were two
    /// thirds of the label block doing nothing while sitting directly over the third that does. The
    /// caller resolves ANY row of a component to that component's editable value (see
    /// <c>MatchDesignerViewModel.ResolveInlineEdit</c>), and <see cref="HitGlyph"/> adds the symbol
    /// itself. That turns a 4.7-pixel target into a 16-pixel one, or a 32-pixel one on a
    /// <c>TermG</c>.</para>
    /// </remarks>
    public static MatchLabelHit? HitTest(SchematicModel? model, double wx, double wy, double tolerance = 0.0)
    {
        if (model is null) return null;
        double pad = Math.Max(0.0, tolerance);

        using var font = new SKFont(SkiaFonts.PlexRegular, (float)SchematicComponent.LabelWorldHeight);

        SchematicComponent? bestComp = null;
        int bestRow = -1;
        double bestBaseX = 0, bestBaselineY = 0, bestScore = double.PositiveInfinity;

        foreach (var c in model.Components)
        {
            for (int row = 0; row < c.Labels.Count; row++)
            {
                string label = c.Labels[row];
                if (string.IsNullOrEmpty(label)) continue;

                var (baseX, baselineY, bandTop, bandBot) = RowGeometry(c, row);
                if (wy < bandTop - pad || wy > bandBot + pad) continue;
                if (wx < baseX - Slack - pad || wx > baseX + font.MeasureText(label) + Slack + pad) continue;

                double score = DistanceToText(wy, baselineY);
                if (score >= bestScore) continue;

                bestScore = score;
                bestComp = c;
                bestRow = row;
                bestBaseX = baseX;
                bestBaselineY = baselineY;
            }
        }

        if (bestComp is not null)
            return Describe(bestComp.Id, bestRow, bestComp.Labels[bestRow], bestBaseX, bestBaselineY, font);

        return HitGlyph(model, wx, wy, pad, font);
    }

    /// <summary>
    /// The component whose GLYPH is under the point, reported as its own value row.
    /// </summary>
    /// <remarks>
    /// <b>Double-clicking a component's body opens its value — the schematic page's own convention</b>
    /// (<c>SchematicView.OnComponentDoubleTapped</c> opens the parameter editor for the component that
    /// was hit). It matters far more here than there. At the zoom this pane frames a whole ladder at,
    /// a label row is under five screen pixels tall while a <c>TermG</c>'s glyph is about thirty — so
    /// the symbol is the target a user can actually hit, and it is the one they reach for.
    ///
    /// <para>Nothing is given up: this pane has no selection and nothing can be moved, so a
    /// double-click on a glyph had no other meaning. Empty space still re-frames.</para>
    /// </remarks>
    private static MatchLabelHit? HitGlyph(SchematicModel model, double wx, double wy, double pad, SKFont font)
    {
        foreach (var c in model.Components)
        {
            if (c.Labels.Count == 0) continue;                       // a shunt arm's own GND
            if (wx < c.GlyphBbMinX - pad || wx > c.GlyphBbMaxX + pad) continue;
            if (wy < c.GlyphBbMinY - pad || wy > c.GlyphBbMaxY + pad) continue;

            int row = c.Labels.Count - 1;                            // the value row — see ValueRow
            var (baseX, baselineY, _, _) = RowGeometry(c, row);
            return Describe(c.Id, row, c.Labels[row], baseX, baselineY, font);
        }
        return null;
    }

    /// <summary>
    /// How far <paramref name="wy"/> is from a row's own GLYPHS — zero when it is on them.
    /// </summary>
    /// <remarks>
    /// <b>This is what disambiguates two overlapping bands, and it has to be the text rather than the
    /// band.</b> A row's glyphs run from its baseline up one cap height, so on a 72-unit pitch with a
    /// 70-unit cap the text intervals ABUT and never overlap — which makes "is the point on this
    /// row's text?" an exact question with exactly one yes. The bands do overlap, so anything derived
    /// from them (first match, nearest centre) has to split the shared strip somewhere arbitrary, and
    /// wherever that lands it takes a slice of one row's real text with it.
    /// </remarks>
    private static double DistanceToText(double wy, double baselineY)
    {
        double top = baselineY - SchematicComponent.LabelWorldHeight;
        return wy < top ? top - wy : wy > baselineY ? wy - baselineY : 0.0;
    }

    /// <summary>One named row's geometry, or null when the drawing no longer has it.</summary>
    public static MatchLabelHit? Locate(SchematicModel? model, string componentId, int row)
    {
        if (model is null) return null;

        foreach (var c in model.Components)
        {
            if (!string.Equals(c.Id, componentId, StringComparison.Ordinal)) continue;
            if (row < 0 || row >= c.Labels.Count) return null;

            using var font = new SKFont(SkiaFonts.PlexRegular, (float)SchematicComponent.LabelWorldHeight);
            var (baseX, baselineY, _, _) = RowGeometry(c, row);
            return Describe(c.Id, row, c.Labels[row], baseX, baselineY, font);
        }
        return null;
    }

    /// <summary>
    /// One row's anchor and hit band — <see cref="SchematicComponent.LabelRowGeometry"/> called
    /// exactly as <c>SchematicRenderer.DrawLabels</c> calls it.
    /// </summary>
    public static (double BaseX, double BaselineY, double BandTop, double BandBot) RowGeometry(
        SchematicComponent c, int row)
    {
        ArgumentNullException.ThrowIfNull(c);
        var (oDx, oDy) = SchematicComponent.LabelOffsetAt(c.LabelOffsets, row);
        return SchematicComponent.LabelRowGeometry(
            c.X, c.Y, row, oDx, oDy, c.Symbol, c.Ports.Count / 2, c.GlyphBbMaxY - c.Y);
    }

    /// <summary>
    /// Splits a row into the part the editor opens OVER and the part it opens PAST.
    /// </summary>
    /// <remarks>
    /// A value row reads "<c>L = 1.53 nH</c>" and only "1.53 nH" is editable — the same split the
    /// schematic page's own inline edit makes with its <c>PrefixWorldUnits</c>. The prefix is measured
    /// at the renderer's reference size, so the result is zoom-independent and the caller scales it.
    /// </remarks>
    private static MatchLabelHit Describe(
        string id, int row, string label, double baseX, double baselineY, SKFont font)
    {
        int eq = label.IndexOf('=', StringComparison.Ordinal);
        if (eq < 0) return new MatchLabelHit(id, row, label, baseX, baselineY, 0.0);

        string prefix = label[..(eq + 1)];
        string value = label[(eq + 1)..].TrimStart();

        // The prefix ends at the equals sign, and whatever whitespace followed it belongs to the
        // MEASUREMENT rather than to the string: measuring "L =" and starting the box there would
        // put it hard against the glyph it is meant to sit after.
        double width = font.MeasureText(label[..(label.Length - value.Length)]);
        return new MatchLabelHit(id, row, value, baseX, baselineY, width);
    }
}

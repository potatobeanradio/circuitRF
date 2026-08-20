using System;
using System.Collections.Generic;
using System.IO;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using SkiaSharp;

namespace CircuitRF.Ui.Matching;

/// <summary>
/// Where a SHUNT element's three label rows go — beside the symbol, or underneath its ground.
/// </summary>
/// <remarks>
/// <b>Owner, 2026-08-20:</b> <i>"if a shunt component instance name gets too long, its text rendering
/// bleeds into the component to its right. Make it so that if the instance name does overlap with its
/// adjacent component, all of the component text gets rendered underneath the GND component below it.
/// Only do this for shunt components. The flatten to cell should also do this."</i>
///
/// <para>A shunt arm's labels sit to the RIGHT of its column (<see cref="MatchSchematicModel.ShuntLabelDx"/>)
/// because BELOW is where its own ground glyph is. That works until the text is wider than the gap to
/// the next column, at which point it runs straight into the neighbouring symbol. The fallback is not
/// to shrink the text or widen the ladder — it is to move the whole three-row block down past the
/// ground, where there is unlimited room and nothing to collide with.</para>
///
/// <para><b>It lives in one place because two drawings have to agree.</b> The Designer's pane
/// (<see cref="MatchSchematicModel"/>) and the flattened cell (<c>MatchFlatten</c>) place the same
/// labels for the same elements, and the owner asked for the rule in both. A second copy of the
/// arithmetic is a second place for it to stop being the same drawing.</para>
/// </remarks>
public static class MatchShuntLabels
{
    /// <summary>
    /// Half the width of a VERTICAL two-terminal glyph — an inductor's coil or a capacitor's plates
    /// measured across the leads. It is what the next column's symbol occupies, and therefore where
    /// this column's label text has to stop.
    /// </summary>
    /// <remarks>
    /// A grounded termination's body is 70 wide on its left side, so 65 is very nearly the same
    /// boundary for the LAST column too — which is why the rule needs no special case for it.
    /// </remarks>
    public const double GlyphHalfWidth = 65.0;

    /// <summary>Gap left between the end of the text and the next column's glyph.</summary>
    public const double Clearance = 25.0;

    /// <summary>
    /// World width of one character of label text — the FALLBACK, used only when the label typeface
    /// cannot be loaded.
    /// </summary>
    /// <remarks>
    /// <b>Not <see cref="SchematicComponent.LabelCharWidth"/>, and the difference is the point.</b>
    /// That constant is a deliberately GENEROUS estimate floored at 500 units, correct for its own job
    /// — inflating a culling box, where over-estimating costs nothing. Used here it would report an
    /// ordinary <c>"C = 1.23 pF"</c> as too wide and push every label under the ground.
    ///
    /// <para>40 is the measured mean advance of IBM Plex Sans at
    /// <see cref="SchematicComponent.LabelWorldHeight"/> over the strings these rows actually contain
    /// (names run ~42/char, value rows ~34 because of the spaces around the <c>=</c>). It is close
    /// enough to be a safe fallback and far enough out — a 12-character value row measures 407 and
    /// estimates 480 — that <see cref="EstimateWidth"/> measures for real when it can.</para>
    /// </remarks>
    public const double CharWorldWidth = 40.0;

    /// <summary>Gap between the bottom of the ground glyph and the top of the first label row.</summary>
    public const double UnderGroundGap = 60.0;

    /// <summary>How far below its own origin a <c>Ground</c> glyph's bars run.</summary>
    public const double GroundGlyphDepth = 70.0;

    /// <summary>
    /// The widest label block that still clears the next column, for a ladder at
    /// <paramref name="pitch"/>.
    /// </summary>
    /// <remarks>
    /// The block's left edge is <c>LabelBaseOffsetX + ShuntLabelDx</c> from the column centre and the
    /// obstacle's left edge is <c>pitch - GlyphHalfWidth</c>; the budget is the distance between them
    /// less <see cref="Clearance"/>. Every term is read from the constant that produces it, so
    /// widening the ladder or moving the labels changes the budget with it.
    /// </remarks>
    public static double WidthBudget(double pitch) =>
        pitch - GlyphHalfWidth - Clearance
        - (SchematicComponent.LabelBaseOffsetX + MatchSchematicModel.ShuntLabelDx);

    /// <summary>
    /// The world width of the widest row in <paramref name="labels"/>, <b>measured in the typeface the
    /// renderer actually draws them in</b>.
    /// </summary>
    /// <remarks>
    /// <b>Measured, not counted.</b> "Does this text run into that symbol" is a text-measurement
    /// question, and a per-character estimate answers it 20 % wrong in the direction that matters: an
    /// ordinary <c>"C = 0.435 pF"</c> counts as 12 characters (480 estimated) and measures 407, which
    /// is the difference between the fallback firing on a normal design and firing on a long name.
    /// <c>SchematicRenderer</c> draws these rows with <c>SkiaFonts.PlexRegular</c> at
    /// <see cref="SchematicComponent.LabelWorldHeight"/>, so that is what is measured.
    ///
    /// <para><b>The fallback matters for the flattened cell, not for the pane.</b> The pane recomputes
    /// this every rebuild; a flattened cell PERSISTS the resulting offsets, so a machine where the
    /// typeface will not load would write slightly different ones. They are ordinary draggable label
    /// offsets either way, and <see cref="CharWorldWidth"/> is calibrated against the real advances,
    /// so the two answers differ only for a row near the boundary.</para>
    /// </remarks>
    public static double EstimateWidth(IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        double widest = 0;
        foreach (string label in labels)
        {
            if (string.IsNullOrEmpty(label)) continue;
            double w = MeasureRow(label);
            if (w > widest) widest = w;
        }
        return widest;
    }

    private static readonly Lazy<SKFont?> LabelFont = new(() =>
    {
        try { return new SKFont(SkiaFonts.PlexRegular, (float)SchematicComponent.LabelWorldHeight); }
        catch (Exception e) when (e is InvalidOperationException or IOException or NotSupportedException)
        {
            return null;   // headless without the bundled font — fall back to the per-character rate
        }
    });

    private static double MeasureRow(string label) =>
        LabelFont.Value is { } font ? font.MeasureText(label) : label.Length * CharWorldWidth;

    /// <summary>True when this label block cannot sit beside its symbol without hitting the next column.</summary>
    public static bool Overflows(IReadOnlyList<string> labels, double pitch) =>
        EstimateWidth(labels) > WidthBudget(pitch);

    /// <summary>
    /// The per-row offset for one shunt element's labels: beside the symbol when they fit, and
    /// centred underneath the ground when they do not.
    /// </summary>
    /// <param name="labels">The three rows, as they will be drawn.</param>
    /// <param name="pitch">Column-to-column spacing of the drawing this element is in.</param>
    /// <param name="groundOffsetY">
    /// Where the arm's own <c>Ground</c> sits, as a y offset from the ELEMENT's centre. The pane and
    /// the flattened cell space their grounds differently, and the block goes under whichever one this
    /// drawing has.
    /// </param>
    public static (double Dx, double Dy) Offsets(
        IReadOnlyList<string> labels, double pitch, double groundOffsetY)
    {
        if (!Overflows(labels, pitch))
            return (MatchSchematicModel.ShuntLabelDx, MatchSchematicModel.ShuntLabelDy);

        // Centred on the column: the rows are drawn LEFT-ALIGNED from one shared anchor, so the block
        // is centred by placing that anchor half the widest row to the left of the column centre.
        double dx = -SchematicComponent.LabelBaseOffsetX - EstimateWidth(labels) / 2.0;

        // First baseline one cap-height below the gap under the ground bars. LabelBaseY rather than
        // LabelBaseYFor: a vertical L or C runs to ±200, which is inside the default offset, so the
        // two agree — and stating the constant keeps this arithmetic readable.
        double dy = groundOffsetY + GroundGlyphDepth + UnderGroundGap
                    + SchematicComponent.LabelWorldHeight - SchematicComponent.LabelBaseY;

        return (dx, dy);
    }
}

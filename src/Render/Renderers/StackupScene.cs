using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SkiaSharp;

namespace CircuitRF.Render;

/// <summary>What one thing the pointer can be over IS.</summary>
public enum StackupHitKind
{
    /// <summary>A conductor or dielectric band of the sandwich.</summary>
    Band,
    /// <summary>A via's barrel, drawn across the bands it spans.</summary>
    ViaBarrel,
    /// <summary>The grab target at the TOP end of a via barrel (brief 5 drags it).</summary>
    ViaGripTop,
    /// <summary>The grab target at the BOTTOM end of a via barrel.</summary>
    ViaGripBottom,
    /// <summary>A piece of text. <see cref="StackupHit.Field"/> says which value editing it would
    /// change, and is <see cref="StackupField.None"/> for a static word or a unit.</summary>
    Label,
    /// <summary>Nothing — the value a caller uses for "the pointer is over the background".
    /// <see cref="StackupScene.HitTest"/> itself returns <c>null</c> rather than this.</summary>
    Empty,
}

// Which value a Label hit would edit is CircuitRF.Design.Layout.StackupField — the enum
// StackupFieldReadiness already keys its per-field checks on, widened there with None, Name and Span
// rather than duplicated here. See that file for why a second enum of the same name could not exist.
//
// StackupField.Span is deliberately carried and deliberately never typed: a via's span is a pair of
// conductor NAMES, chosen from the card's combo boxes and dragged on the canvas (brief 5), so the
// label carries the field for selection and reporting while brief 4's inline editor refuses it.

/// <summary>How a piece of text is drawn. The scene decides; the renderer only maps this onto a
/// face, a size and a colour, so the two cannot disagree about which text is which.</summary>
public enum StackupLabelStyle
{
    /// <summary>A band's own name, sitting ON the band in fixed dark ink.</summary>
    BandName,
    /// <summary>The same name after it MOVED OFF its band — same face, but the theme's ink, because
    /// it is now on the pane's background rather than on the technology's metal. Drawing it in
    /// <see cref="BandName"/>'s fixed dark would make it all but invisible in the dark variant, which
    /// is the same bug that rule exists to prevent, one step along.</summary>
    ColumnName,
    /// <summary>A spec piece in the label column.</summary>
    Spec,
    /// <summary>A spec piece that names something structural — the ground reference, a patterned
    /// dielectric's tie.</summary>
    Accent,
    /// <summary>A boundary condition, above or below the stack.</summary>
    Note,
    /// <summary>A via the drawing could not place. Never silence: see R-stk1-6.</summary>
    Refusal,
    /// <summary>A via's own name, beside its barrel.</summary>
    ViaName,
}

/// <summary>The three drawn via states (R-stk1-7). Two INDEPENDENT model fields produce them —
/// <c>StackupLayer.Plated</c> (is this hole metal at all) and <c>StackupLayer.Fill</c> (the fill
/// model, both of whose values are conductive).</summary>
public enum StackupViaLook
{
    /// <summary>Two metal walls with the hole between them. <see cref="StackupBarrel.WallPx"/> is
    /// the WALL, not the hole radius.</summary>
    PlatedBarrel,
    /// <summary>Solid metal, edge to edge.</summary>
    SolidFill,
    /// <summary>An outline with no metal fill — a hole, drawn as one.</summary>
    UnplatedHole,
}

/// <summary>One thing the pointer can be over, and what editing it would mean.</summary>
public sealed record StackupHit(
    StackupHitKind Kind,
    string         LayerName,   // StackupLayer.Name — the key everything else resolves by
    StackupField   Field,       // which value a Label hit would edit; None otherwise
    SKRect         Rect);

/// <summary>A conductor or dielectric band, placed.</summary>
/// <param name="Fill">The TECHNOLOGY's colour, not the theme's: a conductor takes the colour of the
/// drawing layer it is bound to (R-stk1-5) so the cross-section and the layout editor agree about
/// which metal is which. A dielectric's is <see cref="StackupRenderTheme.DielectricFill"/> instead
/// and this is <c>null</c> — a dielectric is not something anyone draws on.</param>
public sealed record StackupBand(
    string       Name,
    StackupKind  Kind,
    SKRect       Rect,
    Rgba?        Fill,
    bool         IsGroundReference,
    long         ThicknessDbu);

/// <summary>A via, drawn ACROSS the bands it spans at its own x. It is not a band of the sandwich
/// and has no z band of its own.</summary>
/// <param name="WallPx">The metal wall's drawn width in pixels, for
/// <see cref="StackupViaLook.PlatedBarrel"/> only; 0 otherwise.</param>
/// <param name="GripTop">The HIT rect of the top gripper — larger than the glyph the renderer draws
/// by <see cref="StackupScene.GripHitSlop"/> on each side. A 4-pixel target that is exactly 4 pixels
/// to hit is a target nobody hits.</param>
public sealed record StackupBarrel(
    string         Name,
    SKRect         Rect,
    StackupViaLook Look,
    float          WallPx,
    Rgba?          Fill,
    SKRect         GripTop,
    SKRect         GripBottom,
    string?        SpanFromLayer,
    string?        SpanToLayer);

/// <summary>
/// One measured, padded piece of text.
///
/// <para><b>A band's spec is SEVERAL of these</b>, not one sentence (R-stk1-10): a static word, a
/// value, a unit. Only the value pieces carry a <see cref="Field"/> other than
/// <see cref="StackupField.None"/>, which is what lets brief 4 double-click a number rather than the
/// sentence it sits in.</para>
/// </summary>
/// <param name="Rect">The measured rect INFLATED by <see cref="StackupScene.LabelPadX"/> /
/// <see cref="StackupScene.LabelPadY"/>. This is the rect the no-overlap guarantee (R-stk1-9) is
/// stated over and the rect that goes into <see cref="StackupScene.Hits"/>.</param>
public sealed record StackupLabel(
    string            Text,
    string            LayerName,
    StackupField      Field,
    StackupLabelStyle Style,
    SKRect            Rect,
    float             TextX,
    float             Baseline);

// There is no leader-line type, deliberately.
//
// R-stk1-9's rule 3 joins a pushed-apart label back to its band with one, and that is what this
// originally drew. The owner looked at it and asked for the callout lines to go (2026-09-13). What
// carries the correspondence instead is already there and costs no ink: a pushed label is still the
// nearest one to its band, the label column is in stack order, and every group that is not beside
// its own band leads with that layer's NAME. See src/Render/RESOLVED.md.

/// <summary>Caller-supplied layout choices. Everything else is a pure function of the technology
/// and the width.</summary>
public sealed record StackupSceneOptions
{
    public static readonly StackupSceneOptions Default = new();

    /// <summary>
    /// Per-via lateral lane, keyed by <c>StackupLayer.Name</c>, as a fraction of the band column
    /// (0 = its left edge, 1 = its right edge) at which the barrel's CENTRE sits. A via not named
    /// here takes the default spread.
    ///
    /// <para><b>Brief 5 makes this user-settable and persists it</b> — a field and a drag and nothing
    /// else, because the honouring is already here. It is COSMETIC and must stay cosmetic: nothing
    /// downstream of the drawing may read it (series overview §3e).</para>
    /// </summary>
    public IReadOnlyDictionary<string, float>? ViaLanes { get; init; }

    /// <summary>
    /// Whether the two boundary-condition notes — "Top: Open — free space above" above the stack and
    /// "Bottom: Ground" below it — are laid out at all.
    ///
    /// <para>True everywhere the drawing shows a WHOLE stackup, which is the tab, the clipboard copy
    /// and two of the three documentation figures. It is false for the one caller that draws a
    /// <b>window</b> on a stack rather than the stack: the MIM module figure shows five of the MMIC
    /// process's seven bands, and a slice of a sandwich has no terminations of its own — printing the
    /// stack's would say something the picture does not show.</para>
    ///
    /// <para>It is here rather than in the renderer because it changes the LAYOUT: the notes occupy
    /// vertical space above and below the bands, and everything the scene places is measured from
    /// them.</para>
    /// </summary>
    public bool ShowBoundaryConditions { get; init; } = true;
}

/// <summary>
/// <b>The layout of a stackup cross-section: where every band, barrel, label and gripper lands, as
/// rectangles in scene coordinates.</b> A pure function of <c>(Technology, width, options)</c>.
///
/// <h3>R-stk1-1 — computed ONCE, read TWICE</h3>
/// <para>The control that draws this scene also has to answer "what is under this pointer?" for
/// clicks, drags, grippers, context menus and double-click-to-edit, and <b>it answers it by reading
/// the same scene the renderer drew</b> — never by re-deriving band positions. A second copy of the
/// placement arithmetic is the defect that does not appear until the geometry changes: the picture
/// moves, the hit-test does not, the user clicks a band and selects its neighbour, and nothing
/// throws.</para>
///
/// <h3>The z order, which is the whole disambiguation rule</h3>
/// <para><see cref="Hits"/> is in DRAW order, topmost LAST, and <see cref="HitTest"/> walks it
/// backwards. The order is <b>bands, then barrels, then labels, then grippers</b>: a gripper beats a
/// label beats a barrel beats a band. It is invisible from the call site, which is why it is stated
/// here.</para>
///
/// <h3>What the picture never says</h3>
/// <para>Band heights are relative WITHIN a kind and never across kinds (R-stk1-3), and when a kind's
/// own dynamic range does not fit its height budget the mapping compresses — monotonically, so the
/// ordering stays truthful. <see cref="ConductorsCompressed"/> / <see cref="DielectricsCompressed"/>
/// record which branch was taken and <b>nothing draws them</b> (R-stk1-4, owner 2026-09-13): no note,
/// no asterisk, no "not to scale" caption. What carries the honesty instead costs no extra ink — the
/// real thickness is printed on every band.</para>
/// </summary>
public sealed class StackupScene
{
    // ── Column geometry ───────────────────────────────────────────────────────────────────────────

    /// <summary>Left and right margin.</summary>
    public const float Gutter = 24f;
    /// <summary>Between the band column and the label column.</summary>
    public const float ColumnGap = 18f;
    /// <summary>The band column's share of the usable width. Both columns FLEX — this lives in a
    /// dockable pane a user resizes, unlike <c>DocStackupFixtures</c>'s fixed-width figures.</summary>
    public const float BandColumnFraction = 0.55f;
    /// <summary>The band column never narrows past this, so a narrow pane degrades predictably
    /// rather than collapsing (R-stk1-2).</summary>
    public const float MinBandColumnWidth = 180f;

    /// <summary>Below this width the label column is DROPPED and the specs move onto the band.</summary>
    public const float LabelColumnDropWidth = 380f;
    /// <summary>Below this width the scene is a single line saying the pane is too narrow.</summary>
    public const float MinRenderableWidth = 150f;

    /// <summary>Inset of on-band text from the band's left edge.</summary>
    public const float InnerPad = 6f;

    // ── Band heights (R-stk1-3): a separate, deliberately OVERLAPPING range per kind ──────────────

    public const float ConductorMinHeight = 16f;
    public const float ConductorMaxHeight = 36f;
    public const float DielectricMinHeight = 20f;
    public const float DielectricMaxHeight = 110f;

    // ── Vias ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A barrel's drawn width, and the range its metal WALL scales over.
    ///
    /// <para>Wider than <c>DocStackupFixtures</c>' 16 px, and the walls are a large fraction of it,
    /// because a plated barrel has to read as ONE object with a bore rather than as two thin lines
    /// with a gap (owner, 2026-09-13). It is not to scale against the bands and never could be — a
    /// 0.3 mm drill through a 1.6 mm board is a tenth of the picture's own vertical compression — so
    /// the width is chosen to be legible, and the real wall thickness is printed beside it.</para>
    /// </summary>
    public const float BarrelWidth  = 22f;
    public const float ViaWallMin   = 3f;
    public const float ViaWallMax   = 6f;
    /// <summary>Lane 0's centre, as a fraction of the band column. Deliberately on the RIGHT-hand
    /// side, exactly as <c>DocStackupFixtures</c> puts it, so a barrel never runs through the band
    /// names on the left.</summary>
    public const float LaneStartFraction = 0.68f;
    /// <summary>The tightest two barrels are ever packed — what the spread falls back to when the
    /// column cannot hold every via's name as well as every via.</summary>
    public const float LaneMinStep = BarrelWidth + 2f;
    /// <summary>Half-width of a gripper's drawn glyph.</summary>
    public const float GripGlyphHalf = 3.5f;
    /// <summary>How much larger than the glyph the gripper's HIT rect is, on every side.</summary>
    public const float GripHitSlop = 4f;
    /// <summary>Clearance kept between the leftmost barrel and a band's on-band name.</summary>
    public const float NameZoneGap = 8f;

    // ── Text (R-stk1-9) ───────────────────────────────────────────────────────────────────────────

    public const float BandNameSize = 13f;
    public const float SpecSize     = 11.5f;
    public const float NoteSize     = 13f;

    /// <summary>
    /// The point size a label of this style is measured — and therefore drawn — at.
    ///
    /// <para>Here rather than in the renderer because it is a LAYOUT fact: it is what the scene
    /// measured the piece with, and <c>StackupRenderer.FontFor</c> now reads it back instead of
    /// keeping a second copy. brief 4's inline editor is the caller that made the difference visible
    /// — a box opened over a label has to be the size of the text it covers, and a size table that
    /// had drifted from the measurement would be wrong with nothing saying so.</para>
    /// </summary>
    public static float FontSizeFor(StackupLabelStyle style) => style switch
    {
        StackupLabelStyle.BandName   => BandNameSize,
        StackupLabelStyle.ColumnName => BandNameSize,
        StackupLabelStyle.Note       => NoteSize,
        _                            => SpecSize,
    };

    /// <summary>Horizontal padding around every measured label. Two abutting pieces of one sentence
    /// are therefore separated by <c>2 × LabelPadX + PieceGap</c>, which is what makes R-stk1-9's
    /// "no two label rects intersect" hold for a sentence built out of several of them.</summary>
    public const float LabelPadX = 1.5f;

    /// <summary>
    /// Vertical padding around a label in the COLUMN — and therefore, indirectly, <b>how well the
    /// spec rows line up with the bands they describe</b>.
    ///
    /// <para>It was 3, which is what made it worth changing (owner, 2026-09-13: "we're allowing too
    /// much vertical padding in the text on the right side"). Every line of every group carried 6 px
    /// it did not need, so every CLUSTER of groups was that much taller, and <see cref="Separate"/>
    /// re-centres a cluster about the mean of its members' ideal centres — a taller cluster displaces
    /// each of its members further from its own band. Measured over the shipped technologies plus a
    /// 20-hairline stack, at 900 px and 620 px: the mean distance from a thickness label's centre to
    /// its band's centre went from <b>39.4 px to 14.8 px</b>, and the worst case from 116 to 62.</para>
    ///
    /// <para><b>It is not what keeps two labels apart</b>, which is why it can be this small:
    /// <see cref="LabelGap"/> separates two GROUPS and <see cref="LineGap"/> two wrapped lines of one,
    /// and both are strictly positive on their own account. What the padding still buys is a pixel of
    /// slop around the glyphs for brief 4's double-click, which is why it is not zero.</para>
    /// </summary>
    public const float LabelPadY = 1f;

    /// <summary>
    /// The vertical padding an ON-BAND name is measured and drawn with, instead of
    /// <see cref="LabelPadY"/> — and therefore <b>the whole of the rule that decides whether a band
    /// keeps its own name</b>: it keeps it when its height admits the text's own face box plus this
    /// much above and below.
    ///
    /// <para>Owner, 2026-09-13: on the shipped 4-layer board the 8 mil prepreg gave its name up to the
    /// label column, and there is plainly room for it. There was: the band draws 20.95 px tall, the
    /// text's face box is 16.90, and the test it failed was against the 22.90 px rect that face box
    /// sits in when it is padded like a label in the COLUMN. That padding is there to keep two
    /// separately-placed labels from touching (R-stk1-9); a name on its own band has no neighbour to
    /// be kept from, only two band edges to stay clear of.</para>
    ///
    /// <para><b>It is the padding AND the threshold, deliberately.</b> The band is admitted only if
    /// the padded text fits it, and the text is then emitted with exactly that padding — so an on-band
    /// name's rect is inside its band BY CONSTRUCTION, which is what keeps R-stk1-9 true of a rect
    /// that no separator ever sees. Two numbers here would be two chances to make a name that overlaps
    /// the band above it.</para>
    ///
    /// <para>Not smaller: it has to clear the ground reference's heavy edge, which is
    /// <c>StackupRenderer.GroundEdgeWidth</c> centred on the band's own edge and therefore reaches
    /// half of that inward.</para>
    /// </summary>
    public const float OnBandPadY = 1.5f;
    /// <summary>Strictly positive, so adjacent pieces' padded rects are disjoint rather than merely
    /// touching — a guarantee that survives a future change to the intersection test.</summary>
    public const float PieceGap    = 1f;
    /// <summary>Extra space before a piece that starts a new QUANTITY, so the sentence reads as
    /// quantities rather than as words.</summary>
    public const float QuantityGap = 5f;
    /// <summary>Minimum vertical clearance between two pushed-apart labels.</summary>
    public const float LabelGap = 2f;
    /// <summary>Clearance between two WRAPPED lines of one label group. Strictly positive for the
    /// same reason <see cref="PieceGap"/> is.</summary>
    public const float LineGap = 1f;

    public const float TopPad    = 10f;
    public const float NoteGap   = 8f;
    public const float FooterPad = 12f;
    /// <summary>Between a via's barrel and the name drawn beside it.</summary>
    public const float ViaNameGap = 5f;

    /// <summary>
    /// What the ground reference's spec piece says.
    ///
    /// <para>"gnd", not "ground ref" (owner, 2026-09-13). It is printed inside a label column that
    /// WRAPS, and the two words it used to be took a whole wrapped line on a narrow pane for a piece
    /// of information the heavy edge on the band has already given. The abbreviation is the one every
    /// schematic in this application already uses for the same net.</para>
    /// </summary>
    public const string GroundReferenceText = "gnd";

    /// <summary>
    /// How much of a conductor's name a via's SPAN keeps before a SPACE may end it (owner,
    /// 2026-09-13).
    ///
    /// <para>A span is two conductor names and an arrow on one row, and process layers are not called
    /// short things — "Inner 1 (Ground Plane)" and "Bottom Copper (1 oz)" together are most of a
    /// narrow pane's label column, and the wrap then spends three lines on a via that has four things
    /// to say.</para>
    ///
    /// <para><b>A BRACKET is not held to it</b>, and deliberately: see
    /// <see cref="ElideSpanName"/>. Nothing under this length is ever shortened at all.</para>
    /// </summary>
    public const int SpanNameMinChars = 10;

    /// <summary>The characters that open a qualifier — everything after one is a parenthetical, and a
    /// span may cut at it wherever it falls.</summary>
    private static readonly char[] QualifierOpeners = ['(', '['];

    /// <summary>
    /// A conductor name as a via's span shows it: whole if it is short, otherwise cut with an
    /// ellipsis at whichever comes FIRST — an opening bracket, or a space at or after
    /// <see cref="SpanNameMinChars"/>.
    ///
    /// <para><b>At a space and never mid-word.</b> "Bottom Copper (1 oz)" becomes "Bottom Copper…"
    /// rather than "Bottom Cop…", because the first still names a layer a reader recognises and the
    /// second is a string that could belong to two of them. A name with no space past the budget and
    /// no bracket is therefore returned WHOLE: there is nowhere to cut it that leaves it readable, and
    /// the label column wraps, which is a worse look but not a wrong one.</para>
    ///
    /// <para><b>A bracket cuts wherever it falls, even before the budget</b> (owner, 2026-09-13).
    /// What follows one is a qualifier rather than part of the identity — "(1 oz)", "(Ground Plane)"
    /// — so "Inner 1 (Ground Plane)" reads better as "Inner 1…" than as "Inner 1 (Ground…", which
    /// spends six more characters and leaves a bracket hanging open. It cannot empty a name: the
    /// length guard above means this only ever runs on a name longer than the budget, and a cut at
    /// index 0 is refused for the name that IS a bracket.</para>
    ///
    /// <para>It is display only, and safe to be: brief 4 refuses to type a span
    /// (<see cref="StackupField.Span"/> is a pair chosen from the card's combo boxes), so no elided
    /// string is ever parsed back. <see cref="UnresolvedSpanText"/> deliberately does NOT use it — a
    /// refusal has to quote the exact name that failed to resolve.</para>
    /// </summary>
    internal static string ElideSpanName(string? name)
    {
        if (name is not { Length: > SpanNameMinChars }) return name ?? "";

        int bracket = name.IndexOfAny(QualifierOpeners);
        int space   = name.IndexOf(' ', SpanNameMinChars);

        int cut = bracket > 0 && (space < 0 || bracket < space) ? bracket : space;
        if (cut <= 0) return name;

        string elided = name[..cut].TrimEnd() + "…";
        return elided.Length < name.Length ? elided : name;
    }

    /// <summary>The fallback conductor colour, for a conductor bound to no drawing layer.</summary>
    public static readonly Rgba MetalFallback = new(190, 150, 90);

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly Dictionary<string, SKRect> _rectByName = new(StringComparer.Ordinal);

    private StackupScene() { }

    // ── What a caller reads ───────────────────────────────────────────────────────────────────────

    public float Width  { get; private init; }

    /// <summary>The INTRINSIC height — what brief 2's control reports as its desired height and a
    /// <c>ScrollViewer</c> scrolls. Nothing in this series compresses the drawing to fit a
    /// viewport.</summary>
    public float Height { get; private init; }

    /// <summary>Topmost LAST; <see cref="HitTest"/> walks it backwards.</summary>
    public IReadOnlyList<StackupHit>    Hits    { get; private init; } = [];
    public IReadOnlyList<StackupBand>   Bands   { get; private init; } = [];
    public IReadOnlyList<StackupBarrel> Barrels { get; private init; } = [];
    public IReadOnlyList<StackupLabel>  Labels  { get; private init; } = [];

    /// <summary>Scene METADATA, read by tests and by a caller outside the picture (a documentation
    /// figure's own caption). <b>Nothing draws it</b> — R-stk1-4.</summary>
    public bool ConductorsCompressed  { get; private init; }
    /// <inheritdoc cref="ConductorsCompressed"/>
    public bool DielectricsCompressed { get; private init; }

    /// <summary>The pane was too narrow to draw anything; the scene is one line of text.</summary>
    public bool IsTooNarrow { get; private init; }
    /// <summary>The pane was too narrow for a label column, so the specs moved onto the bands.</summary>
    public bool LabelColumnDropped { get; private init; }

    /// <summary>The width the label column actually got.</summary>
    public float LabelColumnWidth { get; private init; }

    /// <summary>The width the widest label group would need to be drawn on ONE line — its pieces,
    /// their gaps and their padding, laid out without wrapping.</summary>
    public float WidestLabelGroup { get; private init; }

    /// <summary>
    /// Whether any label group had to WRAP at this width.
    ///
    /// <para>Derived from the two numbers above rather than recorded as a flag, so it is a statement
    /// about the layout that a caller can also read the terms of. <see cref="WidthThatFitsLabels"/>
    /// is what a caller that wants it false does about it.</para>
    /// </summary>
    public bool LabelsWrap => WidestLabelGroup > LabelColumnWidth + 0.01f;

    /// <summary>
    /// <b>The width at which nothing in the label column wraps</b> — at least
    /// <paramref name="minWidth"/>, never more than <paramref name="maxWidth"/>.
    ///
    /// <para>brief 7's clipboard copy is the caller (owner, 2026-09-13: the copied picture must not
    /// break a spec onto a second line). It belongs HERE and not there for R-stk1-1's reason: the
    /// relationship between a scene's total width and its label column's is this class's arithmetic —
    /// the band column takes a fraction, yields to the widest unbreakable token, and has a floor — and
    /// a caller that solved for it would be keeping a second copy of all three.</para>
    ///
    /// <para><b>It iterates rather than solving.</b> Build is a pure function and cheap, and each
    /// round adds the deficit the last one measured; because the label column takes a fixed FRACTION
    /// of what is added, each round closes about half of the remaining gap and a handful of rounds
    /// settles it. Solving in closed form would mean naming <see cref="BandColumnFraction"/> in the
    /// arithmetic, which is exactly the coupling the iteration avoids.</para>
    ///
    /// <para>The cap is a refusal to loop, not a layout choice: a stackup whose labels cannot fit any
    /// reasonable page gets the widest page tried and wraps, which is what it did before.</para>
    /// </summary>
    public static float WidthThatFitsLabels(Technology tech, float minWidth, float maxWidth)
    {
        ArgumentNullException.ThrowIfNull(tech);

        float w = Math.Clamp(minWidth, MinRenderableWidth, maxWidth);
        for (int round = 0; round < MaxWidenRounds; round++)
        {
            var scene = Build(tech, w);
            if (!scene.LabelsWrap) return w;

            float deficit = scene.WidestLabelGroup - scene.LabelColumnWidth;
            if (deficit <= 0f || w >= maxWidth) break;

            w = Math.Min(maxWidth, w + deficit + ColumnGap);
        }
        return w;
    }

    /// <summary>How many times <see cref="WidthThatFitsLabels"/> will widen before giving up. Each
    /// round closes about half the remaining gap, so this is far more than convergence needs — it is
    /// there so a pathological technology cannot spin.</summary>
    public const int MaxWidenRounds = 24;

    /// <summary>
    /// The column the bands and the barrels share — every band's rect spans it exactly, and a via's
    /// lane fraction is measured across it.
    ///
    /// <para>Here rather than re-derived by a caller, for R-stk1-1's reason: brief 5's lateral drag
    /// turns a pointer x into the fraction it stores, and a second copy of the column arithmetic
    /// would write a number that does not put the barrel back under the pointer. Empty
    /// (<c>SKRect.Empty</c>) when the scene drew no bands.</para>
    /// </summary>
    public SKRect BandColumn { get; private init; }

    /// <summary>The topmost thing at <paramref name="x"/>, <paramref name="y"/>, or <c>null</c> for
    /// the background.</summary>
    public StackupHit? HitTest(float x, float y)
    {
        for (int i = Hits.Count - 1; i >= 0; i--)
            if (Hits[i].Rect.Contains(x, y)) return Hits[i];
        return null;
    }

    /// <summary>The band's rect, or a via's barrel rect — what brief 3's selection outline frames.
    /// Null for a name that is not in the stackup, or for a via whose barrel could not be placed.</summary>
    public SKRect? RectOf(string layerName)
        => layerName is not null && _rectByName.TryGetValue(layerName, out var r) ? r : null;

    // ── Build ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Lays the whole cross-section out. Deterministic: called twice on the same technology
    /// and width it produces element-wise equal scenes (R-stk1-11), which brief 7's clipboard export
    /// and brief 8's doc figures both depend on.</summary>
    public static StackupScene Build(Technology tech, float width, StackupSceneOptions options)
    {
        ArgumentNullException.ThrowIfNull(tech);
        options ??= StackupSceneOptions.Default;

        using var nameFont = new SKFont(SkiaFonts.PlexSemiBold, BandNameSize);
        using var specFont = new SKFont(SkiaFonts.PlexRegular,  SpecSize);
        using var noteFont = new SKFont(SkiaFonts.PlexSemiBold, NoteSize);

        if (width < MinRenderableWidth) return TooNarrowScene(width, noteFont);

        // The widest UNBREAKABLE token the scene can emit — see WidestToken. The label column has to
        // hold it, because a piece wider than its column is a piece that runs off the right edge: a
        // value the reader cannot read and brief 4 cannot edit. So the band column YIELDS to it, down
        // to its own floor, and if even the floor will not do the label column is dropped. The width
        // decides the layout; the layout never decides to overflow the width.
        float usable    = width - 2 * Gutter;
        float bandLeft  = Gutter;
        float widest    = WidestToken(tech, nameFont, specFont);
        float bandWidth = Math.Max(MinBandColumnWidth, usable * BandColumnFraction);
        if (usable - bandWidth - ColumnGap < widest)
            bandWidth = Math.Max(MinBandColumnWidth, usable - ColumnGap - widest);

        bool dropLabels = width < LabelColumnDropWidth || usable - bandWidth - ColumnGap < widest;
        if (dropLabels) bandWidth = usable;
        float bandRight = bandLeft + bandWidth;
        // With no label column the specs move out of the columns entirely and stack BENEATH the
        // drawing, in stack order, each joined to its band by a leader — and a band's name leads its
        // own group rather than sitting on the band, which is R-stk1-9's rule 1 with the label column
        // standing where it can.
        //
        // The brief's narrow mode puts the specs ON the bands, and that was written before the text
        // was measured: a dielectric's full spec wraps to three lines in a 290-pixel column, against
        // a band that is 21 pixels tall, so every spec lands over a band that is not its own and the
        // picture asserts a correspondence that is false. Below the stack costs vertical space in a
        // pane that scrolls anyway, and says nothing untrue. See src/Render/RESOLVED.md.
        float labelLeft  = dropLabels ? bandLeft + InnerPad : bandRight + ColumnGap;
        float labelWidth = width - Gutter - labelLeft;

        var layers     = tech.Stackup.Layers;
        var bandLayers = layers.Where(l => l.Kind != StackupKind.Via).ToList();
        var viaLayers  = layers.Where(l => l.Kind == StackupKind.Via).ToList();

        var conductorScale  = HeightScale.For(
            bandLayers.Where(l => l.Kind == StackupKind.Conductor).Select(l => l.ThicknessDbu),
            ConductorMinHeight, ConductorMaxHeight);
        var dielectricScale = HeightScale.For(
            bandLayers.Where(l => l.Kind == StackupKind.Dielectric).Select(l => l.ThicknessDbu),
            DielectricMinHeight, DielectricMaxHeight);

        var labels  = new List<StackupLabel>();
        var hits    = new List<StackupHit>();

        // ── The stack itself ─────────────────────────────────────────────────────────────────────
        float y = TopPad;

        // Two pieces, so a narrow pane WRAPS the note rather than running it off the edge. A caller
        // drawing a WINDOW on a stack asks for neither note — see ShowBoundaryConditions — and then
        // the space they occupy is not reserved either.
        bool showBoundaries = options.ShowBoundaryConditions;
        var topNote = new PieceRun(bandLeft, width - Gutter - bandLeft);
        if (showBoundaries)
        {
            topNote.Add($"Top: {tech.Stackup.Top}", StackupField.None, StackupLabelStyle.Note, noteFont, 0f);
            if (tech.Stackup.Top == BoundaryCondition.Open)
                topNote.Add("— free space above", StackupField.None, StackupLabelStyle.Note, noteFont, PieceGap);
            y += topNote.Height + NoteGap;
        }

        float stackTop = y;
        var   bands    = new List<StackupBand>(bandLayers.Count);
        foreach (var band in bandLayers)
        {
            float h = band.Kind == StackupKind.Conductor
                ? conductorScale.Map(band.ThicknessDbu)
                : dielectricScale.Map(band.ThicknessDbu);
            var rect = new SKRect(bandLeft, y, bandRight, y + h);
            bands.Add(new StackupBand(
                band.Name, band.Kind, rect,
                band.Kind == StackupKind.Conductor ? MetalOf(band, tech) : null,
                band is { Kind: StackupKind.Conductor, IsGroundReference: true },
                band.ThicknessDbu));
            y += h;
        }
        float stackBottom = y;

        // ── Barrels, and the lane spread they occupy ─────────────────────────────────────────────
        var barrels     = new List<StackupBarrel>();
        var barrelVias  = new List<StackupLayer>();   // parallel to barrels — two vias may share a name
        var unresolved  = new List<StackupLayer>();
        var wallScale   = HeightScale.For(
            viaLayers.Where(v => LookOf(v) == StackupViaLook.PlatedBarrel && v.WallThicknessDbu is > 0)
                     .Select(v => v.WallThicknessDbu!.Value),
            ViaWallMin, ViaWallMax);

        // Which vias can be drawn at all, decided BEFORE any of them is placed: the default spread
        // leaves room between one barrel and the next for the first one's NAME, and it cannot size
        // that gap without knowing how many barrels share the column and what each is called.
        var placed = new List<StackupLayer>();
        foreach (var via in viaLayers)
        {
            if (FindBand(bands, via.SpanFromLayer) is null || FindBand(bands, via.SpanToLayer) is null)
                unresolved.Add(via);
            else
                placed.Add(via);
        }
        var defaultLanes = DefaultLaneCentres(placed, specFont, bandLeft, bandWidth, dropLabels);

        for (int slot = 0; slot < placed.Count; slot++)
        {
            var via = placed[slot];
            var a = FindBand(bands, via.SpanFromLayer)!;
            var b = FindBand(bands, via.SpanToLayer)!;

            float cx    = LaneCentre(via, defaultLanes[slot], bandLeft, bandWidth, options);
            float y0    = Math.Min(a.Rect.Top,    b.Rect.Top);
            float y1    = Math.Max(a.Rect.Bottom, b.Rect.Bottom);
            var   look  = LookOf(via);
            float wall  = look == StackupViaLook.PlatedBarrel
                ? wallScale.Map(via.WallThicknessDbu ?? 0L)
                : 0f;
            // Never let the wall close the hole: a "plated" barrel drawn solid says the wrong thing
            // about the one field this look exists to show.
            wall = Math.Min(wall, (BarrelWidth - 2f) * 0.5f);

            var rect = new SKRect(cx - BarrelWidth * 0.5f, y0, cx + BarrelWidth * 0.5f, y1);
            float g  = GripGlyphHalf + GripHitSlop;
            barrels.Add(new StackupBarrel(
                via.Name, rect, look, wall, MetalOf(via, tech),
                new SKRect(cx - g, y0 - g, cx + g, y0 + g),
                new SKRect(cx - g, y1 - g, cx + g, y1 + g),
                via.SpanFromLayer, via.SpanToLayer));
            barrelVias.Add(via);
        }

        // A band's own name shares the band column with the barrels, so the zone it may occupy stops
        // short of the leftmost one. DocStackupFixtures states the same constraint as a comment; here
        // it is arithmetic, because the lanes move with the pane's width.
        float nameZoneRight = barrels.Count > 0
            ? barrels.Min(v => v.Rect.Left) - NameZoneGap
            : bandRight - InnerPad;
        float nameZoneWidth = Math.Max(0f, nameZoneRight - (bandLeft + InnerPad));

        // ── Labels ───────────────────────────────────────────────────────────────────────────────
        var groups = new List<LabelGroup>();

        for (int i = 0; i < bandLayers.Count; i++)
        {
            var layer = bandLayers[i];
            var band  = bands[i];

            var group = new LabelGroup(labelLeft, labelWidth)
            {
                LayerName = layer.Name,
                Index     = groups.Count,
                AnchorY   = band.Rect.MidY,
            };

            // Rule 1: the name goes ON the band when the band is tall enough for the padded text AND
            // the name zone is wide enough for it. Otherwise it moves to the label column AHEAD of
            // the spec — which is the same rule in both modes, because in the narrow mode the "label
            // column" is the band column's own right-hand part.
            float nameW = nameFont.MeasureText(layer.Name) + 2 * LabelPadX;
            float nameH = FaceHeight(nameFont) + 2 * OnBandPadY;
            bool  onBand = !dropLabels && layer.Name.Length > 0
                        && nameH <= band.Rect.Height && nameW <= nameZoneWidth;

            if (onBand)
            {
                var run = new PieceRun(bandLeft + InnerPad, padY: OnBandPadY);
                run.Add(layer.Name, StackupField.Name, StackupLabelStyle.BandName, nameFont, 0f);
                run.Place(band.Rect.MidY - run.Height * 0.5f, layer.Name, labels, hits);
            }
            else if (layer.Name.Length > 0)
            {
                group.Add(layer.Name, StackupField.Name, StackupLabelStyle.ColumnName, nameFont, 0f);
            }

            AddSpecPieces(group, layer, tech, specFont);
            if (group.PieceCount > 0) groups.Add(group);
        }

        for (int i = 0; i < barrels.Count; i++)
        {
            var barrel = barrels[i];
            var via    = barrelVias[i];

            // Rule 4: the via's name goes beside its barrel — unless it would run out of the band
            // column, in which case it joins the spec group, exactly as an over-wide band name does.
            //
            // Its ideal y is a BAND's centre rather than the barrel's, so the text lands inside one
            // band instead of straddling a boundary — see ViaNameAnchorY, which is also where the
            // dielectric-then-conductor preference is stated.
            float nameW = specFont.MeasureText(via.Name) + 2 * LabelPadX;
            float nameH = FaceHeight(specFont) + 2 * LabelPadY;
            var   nameGroup = new LabelGroup(barrel.Rect.Right + ViaNameGap,
                                             bandRight - barrel.Rect.Right - ViaNameGap)
            {
                LayerName = via.Name,
                Index     = groups.Count,
                AnchorY   = ViaNameAnchorY(bands, barrel.Rect, nameH),
            };
            bool besideBarrel = !dropLabels && nameGroup.Left + nameW <= bandRight;
            if (besideBarrel)
            {
                nameGroup.Add(via.Name, StackupField.Name, StackupLabelStyle.ViaName, specFont, 0f);
                groups.Add(nameGroup);
            }

            var group = new LabelGroup(labelLeft, labelWidth)
            {
                LayerName = via.Name,
                Index     = groups.Count,
                AnchorY   = barrel.Rect.MidY,
            };
            if (!besideBarrel)
                group.Add(via.Name, StackupField.Name, StackupLabelStyle.ColumnName, nameFont, 0f);
            AddViaSpecPieces(group, via, barrel, tech, specFont);
            if (group.PieceCount > 0) groups.Add(group);
        }

        Separate(groups, dropLabels ? stackBottom + NoteGap : stackTop, LabelGap);

        foreach (var group in groups) group.Emit(labels, hits);

        // ── The two boundary conditions, which belong to the STACK rather than to any band ───────
        float footerY;
        if (showBoundaries)
        {
            string bottomText   = $"Bottom: {tech.Stackup.Bottom}";
            float  bottomWidth  = noteFont.MeasureText(bottomText) + 2 * LabelPadX;
            float  bottomTop    = Math.Max(stackBottom, BottomOfColumn(labels, bandLeft, bandLeft + bottomWidth)) + NoteGap;

            topNote.Place(TopPad, "", labels, hits);

            var bottomNote = new PieceRun(bandLeft, width - Gutter - bandLeft);
            bottomNote.Add(bottomText, StackupField.None, StackupLabelStyle.Note, noteFont, 0f);
            bottomNote.Place(bottomTop, "", labels, hits);
            footerY = bottomTop + bottomNote.Height + LabelGap;
        }
        else
        {
            footerY = Math.Max(stackBottom, BottomOfColumn(labels, bandLeft, width - Gutter)) + NoteGap;
        }

        // R-stk1-6: an unresolvable via is drawn as a marker, never omitted. A via that simply does
        // not appear is a stackup the user cannot tell is broken from the picture, and the card list
        // already flags it — the drawing must not disagree by staying silent.
        foreach (var via in unresolved)
        {
            var run = new PieceRun(labelLeft, labelWidth);
            bool isHole = via.Plated == false;
            run.Add(via.Name, StackupField.Name,
                    isHole ? StackupLabelStyle.ViaName : StackupLabelStyle.Refusal, specFont, 0f);
            run.Add(isHole ? "unplated hole — no span" : UnresolvedSpanText(via, tech),
                    StackupField.Span,
                    isHole ? StackupLabelStyle.Spec : StackupLabelStyle.Refusal, specFont, QuantityGap);
            run.Place(footerY, via.Name, labels, hits);
            footerY += run.Height + LabelGap;
        }

        // ── Hit order: bands, barrels, labels, grippers. Topmost LAST. ───────────────────────────
        var ordered = new List<StackupHit>(hits.Count + bands.Count + barrels.Count * 3);
        foreach (var band in bands)
            ordered.Add(new StackupHit(StackupHitKind.Band, band.Name, StackupField.None, band.Rect));
        foreach (var barrel in barrels)
            ordered.Add(new StackupHit(StackupHitKind.ViaBarrel, barrel.Name, StackupField.None, barrel.Rect));
        ordered.AddRange(hits);
        foreach (var barrel in barrels)
        {
            ordered.Add(new StackupHit(StackupHitKind.ViaGripTop,    barrel.Name, StackupField.None, barrel.GripTop));
            ordered.Add(new StackupHit(StackupHitKind.ViaGripBottom, barrel.Name, StackupField.None, barrel.GripBottom));
        }

        float bottom = footerY;
        foreach (var l in labels)  bottom = Math.Max(bottom, l.Rect.Bottom);
        foreach (var v in barrels) bottom = Math.Max(bottom, v.GripBottom.Bottom);

        var scene = new StackupScene
        {
            Width                 = width,
            Height                = bottom + FooterPad,
            Bands                 = bands,
            Barrels               = barrels,
            Labels                = labels,
            Hits                  = ordered,
            ConductorsCompressed  = conductorScale.Compressed,
            DielectricsCompressed = dielectricScale.Compressed,
            LabelColumnDropped    = dropLabels,
            LabelColumnWidth      = labelWidth,
            WidestLabelGroup      = groups.Count == 0 ? 0f : groups.Max(g => g.NaturalWidth),
            BandColumn            = bands.Count == 0
                ? SKRect.Empty
                : new SKRect(bandLeft, stackTop, bandRight, stackBottom),
        };
        foreach (var band in bands)     scene._rectByName.TryAdd(band.Name,   band.Rect);
        foreach (var barrel in barrels) scene._rectByName.TryAdd(barrel.Name, barrel.Rect);
        return scene;
    }

    /// <inheritdoc cref="Build(Technology, float, StackupSceneOptions)"/>
    public static StackupScene Build(Technology tech, float width)
        => Build(tech, width, StackupSceneOptions.Default);

    // ── Pieces ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A band's spec, as SEVERAL labels (R-stk1-10). Every number is formatted <b>exactly as the card
    /// formats it</b> — <c>LayoutUnits.Format</c> in the technology's display unit for a thickness,
    /// <c>StackupLayerRowViewModel</c>'s own <c>"0.###e+0"</c> for σ, invariant culture throughout —
    /// so opening an editor over a value seeds the same string the card shows. A drawing that rounded
    /// differently from the field it edits would be an edit that changed a value the user did not
    /// touch.
    /// </summary>
    private static void AddSpecPieces(LabelGroup g, StackupLayer layer, Technology tech, SKFont font)
    {
        // Short, because the picture is the point and every word here is drawn on every band
        // (owner, 2026-09-13). "thick" is gone: a length with a unit on a band of a cross-section is
        // its thickness, and the word said so nine times on a seven-band stack.
        g.Add(ThicknessText(layer.ThicknessDbu, tech), StackupField.Thickness, StackupLabelStyle.Spec, font, QuantityGap);
        g.Add(UnitText(tech), StackupField.None, StackupLabelStyle.Spec, font, PieceGap);

        if (layer.Kind == StackupKind.Conductor)
        {
            // In mil — the unit a board is spoken of in — a copper thickness is also read in ounces,
            // so the weight rides beside the length rather than making the reader convert.
            if (CopperWeightText(layer.ThicknessDbu, tech) is { } oz)
                g.Add(oz, StackupField.None, StackupLabelStyle.Spec, font, QuantityGap);

            g.Add("σ =", StackupField.None,  StackupLabelStyle.Spec, font, QuantityGap);
            g.Add(layer.SigmaSm.ToString("0.###e+0", Inv), StackupField.Sigma, StackupLabelStyle.Spec, font, PieceGap);
            g.Add("S/m",      StackupField.None,  StackupLabelStyle.Spec, font, PieceGap);

            // The ground reference is the one band a reader has to be able to FIND. The heavy edge
            // says it in the picture; this says it in words, on the same line — a second line would
            // be a second thing to keep from overlapping for no extra information.
            if (layer.IsGroundReference)
                g.Add(GroundReferenceText, StackupField.None, StackupLabelStyle.Accent, font, QuantityGap);
            return;
        }

        g.Add("εr =", StackupField.None, StackupLabelStyle.Spec, font, QuantityGap);
        g.Add(layer.Epsr.ToString("0.####", Inv), StackupField.Epsr, StackupLabelStyle.Spec, font, PieceGap);
        g.Add("tanδ =", StackupField.None, StackupLabelStyle.Spec, font, QuantityGap);
        g.Add(layer.TanD.ToString("0.######", Inv), StackupField.TanD, StackupLabelStyle.Spec, font, PieceGap);
        // µr is printed only when it is NOT 1, which is the engineering convention and saves a whole
        // quantity on very nearly every dielectric anyone draws. The consequence is real and is the
        // trade: a non-magnetic dielectric has no µr label, so it cannot be edited FROM THE DRAWING
        // (brief 4) — only from the card, which always shows the field. A magnetic one has both.
        if (Math.Abs(layer.Mur - 1.0) > 1e-9)
        {
            g.Add("µr =", StackupField.None, StackupLabelStyle.Spec, font, QuantityGap);
            g.Add(layer.Mur.ToString("0.####", Inv), StackupField.Mur, StackupLabelStyle.Spec, font, PieceGap);
        }

        // MIM-7's tie: the one thing about this band a reader cannot infer from the picture, because
        // it is drawn as a layer of the sandwich like any other and is the only one that is not
        // always there.
        if (layer.PresentWithLayer is { Length: > 0 } plate)
            g.Add($"patterned: {plate}", StackupField.None, StackupLabelStyle.Accent, font, QuantityGap);
    }

    private static void AddViaSpecPieces(
        LabelGroup g, StackupLayer via, StackupBarrel barrel, Technology tech, SKFont font)
    {
        // Three pieces rather than one sentence, so a narrow label column can break BETWEEN the two
        // conductor names instead of running the pair off the edge. All three carry Span: a hit
        // anywhere in it means the same thing, and brief 5 drags it as one.
        // Both are non-null here by construction — this runs only for a via whose span RESOLVED to
        // two bands — but Add ignores an empty piece anyway, so the guard costs nothing.
        g.Add(ElideSpanName(via.SpanFromLayer), StackupField.Span, StackupLabelStyle.Spec, font, QuantityGap);
        g.Add("→",                              StackupField.Span, StackupLabelStyle.Spec, font, PieceGap);
        g.Add(ElideSpanName(via.SpanToLayer),   StackupField.Span, StackupLabelStyle.Spec, font, PieceGap);

        // WallThicknessDbu is the WALL, not the hole radius — the confusion the model field's own
        // doc comment already warns about, and one a drawing that got it backwards would make
        // permanent. It is editable from the drawing on a plated-FILL via only, which is the only
        // state in which it means anything.
        //
        // "plated=25" and not "plated wall 25" (owner, 2026-09-13): the word was the only one on this
        // row that named a field rather than said something about the via, and on a barrel whose look
        // is already drawn as two walls with a bore between them it was telling the reader what they
        // are looking at. The "=" carries the same sense the σ, εr and tanδ rows already use, and the
        // value stays its own piece so brief 4 can still double-click it.
        if (barrel.Look != StackupViaLook.PlatedBarrel)
        {
            g.Add(barrel.Look == StackupViaLook.SolidFill ? "solid" : "unplated",
                  StackupField.None, StackupLabelStyle.Spec, font, QuantityGap);
            return;
        }

        g.Add("plated =", StackupField.None, StackupLabelStyle.Spec, font, QuantityGap);
        g.Add(ThicknessText(via.WallThicknessDbu ?? 0L, tech), StackupField.WallThickness, StackupLabelStyle.Spec, font, PieceGap);
        g.Add(UnitText(tech), StackupField.None, StackupLabelStyle.Spec, font, PieceGap);
    }

    private static string UnresolvedSpanText(StackupLayer via, Technology tech)
    {
        // With fewer than two conductors no span COULD resolve, so naming the unset ends would
        // report a symptom; what the user can act on is the missing metal.
        if (tech.Stackup.Layers.Count(l => l.Kind == StackupKind.Conductor) < 2)
            return "first add conductors to the stackup for this via to span";

        string from = via.SpanFromLayer is { Length: > 0 } f ? $"\"{f}\"" : "(unset)";
        string to   = via.SpanToLayer   is { Length: > 0 } t ? $"\"{t}\"" : "(unset)";
        return $"span does not resolve: {from} → {to}";
    }

    internal static string ThicknessText(long dbu, Technology tech)
        => LayoutUnits.Format(dbu, tech.DefaultDisplayUnit, LayoutUnits.DefaultDbuPerMicron);

    internal static string UnitText(Technology tech) => LayoutUnits.Suffix(tech.DefaultDisplayUnit);

    /// <summary>The copper thickness of one ounce per square foot, as the trade rounds it (34.79 µm
    /// exactly; every fabricator's table says 35).</summary>
    internal const double MicronsPerOunce = 35.0;

    // The weights a fabricator actually sells. A thickness within a few percent of one is that
    // weight — 18 µm is "½ oz" on every data sheet, though 18/35 is 0.514.
    private static readonly double[] StandardOunces = [0.25, 1.0 / 3, 0.5, 0.75, 1, 1.5, 2, 2.5, 3, 4, 5, 6];

    /// <summary>
    /// "(1 oz)" for a conductor band, shown only when the display unit is mil; null otherwise or for
    /// a zero thickness. A standard weight within 8 % prints as that weight; anything else prints
    /// "≈" and its ratio to <see cref="MicronsPerOunce"/>.
    /// </summary>
    internal static string? CopperWeightText(long thicknessDbu, Technology tech)
    {
        if (tech.DefaultDisplayUnit != LayoutUnit.Mil || thicknessDbu <= 0) return null;
        double oz = (double)LayoutUnits.FromDbu(thicknessDbu, LayoutUnit.Um, LayoutUnits.DefaultDbuPerMicron)
                    / MicronsPerOunce;
        foreach (var w in StandardOunces)
            if (Math.Abs(oz - w) <= 0.08 * w)
                return $"({w.ToString("0.##", Inv)} oz)";
        return $"(≈{oz.ToString("0.##", Inv)} oz)";
    }

    // ── Colour, span and lane resolution ──────────────────────────────────────────────────────────

    /// <summary>R-stk1-5: a conductor (and a via barrel) takes its fill from the first drawing layer
    /// it is bound to that resolves against the technology, so the cross-section and the layout
    /// editor agree about which metal is which.</summary>
    private static Rgba MetalOf(StackupLayer layer, Technology tech)
    {
        foreach (var key in layer.DrawingLayers)
            foreach (var def in tech.Layers)
                if (def.Key.Equals(key)) return def.Color;
        return MetalFallback;
    }

    /// <summary>
    /// The three drawn via states, from two independent model fields (R-stk1-7).
    ///
    /// <para><b>A null <c>Fill</c> draws SOLID</b>, per the brief's own table — a hollow barrel needs
    /// a wall thickness, and an entry with no fill model stated has none, so drawing one would mean
    /// inventing the number the look exists to show. Note that <c>StackupLayerRowViewModel</c>'s
    /// combo box defaults a null <c>Fill</c> to <c>Plated</c> instead; see
    /// <c>src/Render/RESOLVED.md</c>.</para>
    /// </summary>
    private static StackupViaLook LookOf(StackupLayer via)
        => via.Plated == false               ? StackupViaLook.UnplatedHole
         : via.Fill == ViaFillKind.Plated    ? StackupViaLook.PlatedBarrel
         :                                     StackupViaLook.SolidFill;

    private static StackupBand? FindBand(List<StackupBand> bands, string? name)
    {
        if (name is not { Length: > 0 }) return null;
        foreach (var band in bands)
            if (band.Kind == StackupKind.Conductor && string.Equals(band.Name, name, StringComparison.Ordinal))
                return band;
        return null;
    }

    /// <summary>
    /// R-stk1-8. An explicit per-via lane wins over everything: the caller's
    /// <see cref="StackupSceneOptions.ViaLanes"/> first — that is a drag IN FLIGHT, which has to win
    /// over what the technology still says until the release commits it — then the entry's own
    /// persisted lane (R-stk5-7), so every reader of a technology draws the barrel where the user put
    /// it with no plumbing of its own: the tab, the clipboard export and the documentation figures.
    /// Failing both, <paramref name="defaultCentre"/> — <see cref="DefaultLaneCentres"/>' spread.
    /// </summary>
    private static float LaneCentre(
        StackupLayer via, float defaultCentre, float bandLeft, float bandWidth,
        StackupSceneOptions options)
    {
        float half = BarrelWidth * 0.5f;
        float lo   = bandLeft + half + 1f;
        float hi   = bandLeft + bandWidth - half - 1f;

        if (options.ViaLanes is { } lanes && lanes.TryGetValue(via.Name, out float lane))
            return Math.Clamp(bandLeft + bandWidth * Math.Clamp(lane, 0f, 1f), lo, hi);

        if (via.DrawLaneFraction is { } stored && !double.IsNaN(stored))
            return Math.Clamp(bandLeft + bandWidth * (float)Math.Clamp(stored, 0d, 1d), lo, hi);

        return Math.Clamp(defaultCentre, lo, hi);
    }

    /// <summary>
    /// Where the barrels go when nothing has placed them by hand — right to left, <b>with room left
    /// between each one and the next for its own NAME</b>.
    ///
    /// <para>A via's name is drawn immediately to the RIGHT of its barrel, so the gap between barrel
    /// <i>i</i> and barrel <i>i-1</i> is the space that name has to live in. The spread used to be
    /// three fixed fractions of the column wrapping to a second pass, which took no account of how
    /// long anything was called: on a board with several vias the name of one was printed across the
    /// metal of the next (owner, 2026-09-13). Each step is now measured from the name it has to
    /// clear.</para>
    ///
    /// <para><b>When they do not all fit, the steps are compressed uniformly and the names overlap</b>
    /// — which is the stated fallback. A barrel pushed outside the band column would be worse: it
    /// could not be seen, hovered or dragged back, and R-stk1-8 exists to stop exactly that.</para>
    ///
    /// <para>With the label column dropped there is no name beside a barrel at all
    /// (<c>besideBarrel</c> is false), so nothing has to be kept clear and the barrels crowd into the
    /// right-hand gutter exactly as they did.</para>
    /// </summary>
    private static List<float> DefaultLaneCentres(
        List<StackupLayer> vias, SKFont specFont, float bandLeft, float bandWidth, bool dropLabels)
    {
        var centres = new List<float>(vias.Count);
        if (vias.Count == 0) return centres;

        float half = BarrelWidth * 0.5f;
        float lo   = bandLeft + half + 1f;
        float hi   = bandLeft + bandWidth - half - 1f;

        if (dropLabels)
        {
            for (int i = 0; i < vias.Count; i++)
                centres.Add(Math.Clamp(hi - i * LaneMinStep, lo, hi));
            return centres;
        }

        // Slot 0 sits where it always has, which is what leaves the right-hand part of the column
        // free for its own name.
        float first = Math.Clamp(bandLeft + bandWidth * LaneStartFraction, lo, hi);

        var   steps = new float[vias.Count];
        float total = 0f;
        for (int i = 1; i < vias.Count; i++)
        {
            float nameW = specFont.MeasureText(vias[i].Name) + 2 * LabelPadX;
            steps[i] = Math.Max(LaneMinStep, BarrelWidth + ViaNameGap + nameW + NameZoneGap);
            total   += steps[i];
        }

        float room  = Math.Max(0f, first - lo);
        float scale = total > room && total > 0f ? room / total : 1f;

        float x = first;
        centres.Add(x);
        for (int i = 1; i < vias.Count; i++)
        {
            x -= Math.Max(LaneMinStep, steps[i] * scale);
            centres.Add(Math.Clamp(x, lo, hi));
        }
        return centres;
    }

    /// <summary>
    /// Where a via's own name sits vertically: <b>centred in ONE band of the sandwich</b> wherever a
    /// band the barrel crosses is tall enough to hold it (owner, 2026-09-13). A name straddling a
    /// boundary is half on metal and half on dielectric and reads as neither.
    ///
    /// <para>A DIELECTRIC is preferred over a conductor — it is the taller band on very nearly every
    /// stack, and it carries no on-band name of its own — and among the candidates of one kind the
    /// one whose centre is nearest the barrel's own middle wins, so the name stays where the eye
    /// looks for it. With no band tall enough the barrel's middle stands: mixed is the LAST
    /// preference, not a refusal.</para>
    ///
    /// <para>It is the group's ideal anchor and not its final y. <see cref="Separate"/> still runs,
    /// and two names whose x intervals overlap — which the spread above avoids unless the column is
    /// too narrow to hold them all — are still pushed apart, because R-stk1-9 outranks this.</para>
    /// </summary>
    private static float ViaNameAnchorY(List<StackupBand> bands, SKRect barrel, float textHeight)
    {
        float best     = barrel.MidY;
        float bestNear = float.PositiveInfinity;
        int   bestRank = int.MaxValue;

        foreach (var band in bands)
        {
            // Only a band the barrel actually crosses. A name floated onto some other band would put
            // the via somewhere it is not.
            if (band.Rect.Bottom <= barrel.Top || band.Rect.Top >= barrel.Bottom) continue;
            if (band.Rect.Height < textHeight) continue;

            int   rank = band.Kind == StackupKind.Dielectric ? 0 : 1;
            float near = Math.Abs(band.Rect.MidY - barrel.MidY);
            if (rank > bestRank || (rank == bestRank && near >= bestNear)) continue;

            bestRank = rank;
            bestNear = near;
            best     = band.Rect.MidY;
        }
        return best;
    }

    /// <summary>
    /// The widest piece of text the scene can emit that it is not allowed to break.
    ///
    /// <para><b>It measures NAMES and formatted VALUES, and deliberately not the static words.</b>
    /// Those two are what can be arbitrarily long — a layer called "Inner 1 (Ground Plane)", a
    /// thickness printed in mil to four places — while every static word here is under a dozen
    /// characters, and no column wide enough for a name will fail to hold "tan&#948; =". A static
    /// word that ever grew long enough to matter shows up as a label past the right edge in
    /// <c>StackupSceneTests</c>' own extent gate rather than as a silent overflow.</para>
    ///
    /// <para><b>It is not an absolute guarantee, and cannot be</b>: a layer name longer than the
    /// whole pane has nowhere to go, and the alternative — truncating it — would print a value that
    /// is not the value. It holds for every shipped technology at every width the gate covers.</para>
    /// </summary>
    private static float WidestToken(Technology tech, SKFont nameFont, SKFont specFont)
    {
        float widest = 0f;
        foreach (var layer in tech.Stackup.Layers)
        {
            Token(layer.Name, nameFont);
            Token(layer.Name, specFont);
            Token(ThicknessText(layer.ThicknessDbu, tech), specFont);
            Token(UnitText(tech), specFont);
            if (layer.Kind == StackupKind.Conductor && CopperWeightText(layer.ThicknessDbu, tech) is { } oz)
                Token(oz, specFont);
            Token(layer.SigmaSm.ToString("0.###e+0", Inv), specFont);
            Token(layer.Epsr.ToString("0.####",   Inv), specFont);
            Token(layer.TanD.ToString("0.######", Inv), specFont);
            Token(layer.Mur .ToString("0.####",   Inv), specFont);
            // The ELIDED spellings, because those are what get drawn — sizing the column against the
            // full names would yield width to a token the scene never emits, which is the whole of
            // what the elision was for.
            Token(ElideSpanName(layer.SpanFromLayer), specFont);
            Token(ElideSpanName(layer.SpanToLayer),   specFont);
            if (layer.WallThicknessDbu is { } w) Token(ThicknessText(w, tech), specFont);
            if (layer.PresentWithLayer is { Length: > 0 } plate)
                Token($"patterned: {plate}", specFont);
        }
        return widest;

        void Token(string? text, SKFont font)
        {
            if (text is not { Length: > 0 }) return;
            widest = Math.Max(widest, font.MeasureText(text) + 2 * LabelPadX);
        }
    }

    private static float BottomOfColumn(List<StackupLabel> labels, float x0, float x1)
    {
        float bottom = float.NegativeInfinity;
        foreach (var l in labels)
            if (l.Rect.Left < x1 && x0 < l.Rect.Right) bottom = Math.Max(bottom, l.Rect.Bottom);
        return float.IsNegativeInfinity(bottom) ? 0f : bottom;
    }

    private static float FaceHeight(SKFont font) => font.Metrics.Descent - font.Metrics.Ascent;

    private static StackupScene TooNarrowScene(float width, SKFont noteFont)
    {
        var run = new PieceRun(4f);
        run.Add("Pane too narrow to draw the stackup.", StackupField.None, StackupLabelStyle.Note, noteFont, 0f);
        var labels = new List<StackupLabel>();
        var hits   = new List<StackupHit>();
        run.Place(TopPad, "", labels, hits);
        return new StackupScene
        {
            Width       = width,
            Height      = TopPad + run.Height + FooterPad,
            Labels      = labels,
            IsTooNarrow = true,
        };
    }

    // ── R-stk1-3's height function ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a thickness within ONE kind onto that kind's own band-height range.
    ///
    /// <para><b>Proportional while the kind's dynamic range fits the budget</b>, so two conductors at
    /// a 2:1 thickness ratio draw at exactly 2:1 — which is what was asked for. <b>Monotonically
    /// compressed when it does not</b>: the thickest still draws tallest, the thinnest still draws
    /// shortest, every ordering is preserved and nothing is a hairline.</para>
    ///
    /// <para><b>The proportional branch is anchored at the THICKEST</b>, <c>h = hMax·t/tMax</c>, not
    /// at the thinnest. Both anchors are exactly proportional and both satisfy the range; this one
    /// uses the whole budget, so the commonest stack in the application — a two-layer board whose two
    /// copper layers are the same weight and whose one substrate has nothing to be proportional
    /// against — draws its substrate at full height instead of at the floor. Anchoring at the
    /// thinnest would have drawn 1.6 mm of FR-4 four pixels taller than 35 µm of copper.</para>
    /// </summary>
    private readonly struct HeightScale
    {
        private readonly float _hMin, _hMax, _tMin, _tMax;
        public  readonly bool  Compressed;

        private HeightScale(float hMin, float hMax, float tMin, float tMax, bool compressed)
        { _hMin = hMin; _hMax = hMax; _tMin = tMin; _tMax = tMax; Compressed = compressed; }

        public static HeightScale For(IEnumerable<long> thicknesses, float hMin, float hMax)
        {
            float tMin = float.PositiveInfinity, tMax = 0f;
            foreach (long dbu in thicknesses)
            {
                float t = Math.Max(dbu, 1L);
                tMin = Math.Min(tMin, t);
                tMax = Math.Max(tMax, t);
            }
            if (tMax <= 0f) return new HeightScale(hMin, hMax, 1f, 1f, false);
            bool compressed = tMax / tMin > hMax / hMin + 1e-4f;
            return new HeightScale(hMin, hMax, tMin, tMax, compressed);
        }

        public float Map(long dbu)
        {
            float t = Math.Max(dbu, 1L);
            if (!Compressed) return Math.Clamp(_hMax * t / _tMax, _hMin, _hMax);
            double span = Math.Log(_tMax / _tMin);
            double f    = span <= 0 ? 0 : Math.Log(t / _tMin) / span;
            return (float)Math.Clamp(_hMin + (_hMax - _hMin) * f, _hMin, _hMax);
        }
    }

    // ── Text runs and the no-overlap guarantee (R-stk1-9) ─────────────────────────────────────────

    private sealed record PieceSpec(
        string Text, StackupField Field, StackupLabelStyle Style, float Width, float Ascent,
        float Descent, float GapBefore);

    /// <summary>
    /// Measured, padded pieces at a fixed x, wrapped to a stated width.
    ///
    /// <para><b>The wrap is not cosmetic.</b> A dielectric's spec is nine pieces long and a via's
    /// names two conductors; on a 560-pixel pane either runs past the right margin, and a label that
    /// runs off the edge is a value the user cannot read and brief 4 cannot edit. Wrapping keeps
    /// every piece inside the scene, and because successive lines are separated by
    /// <see cref="LineGap"/> it keeps R-stk1-9 true as well.</para>
    /// </summary>
    private class PieceRun
    {
        private readonly List<PieceSpec> _pieces = [];
        private readonly List<int>       _lineStart = [];
        private readonly float           _maxWidth;
        private readonly float           _padY;
        private float _width;
        private float _natural;
        private float _ascent  = float.PositiveInfinity;
        private float _descent;

        /// <param name="padY">Above and below the text's face box. <see cref="LabelPadY"/> for a run
        /// in the label column, where the padding is what keeps two separately-placed labels from
        /// touching; <see cref="OnBandPadY"/> for a name drawn ON its band, which has no neighbour to
        /// be kept from and two band edges to stay inside.</param>
        public PieceRun(float left, float maxWidth = float.PositiveInfinity, float padY = LabelPadY)
        { Left = left; _maxWidth = maxWidth; _padY = padY; }

        public float Left  { get; set; }
        public float Width => _width;

        /// <summary>What this run would need to be drawn on ONE line — every piece, its gap and its
        /// padding, with no wrap. <see cref="Width"/> is the width it actually occupies, which is the
        /// widest of its wrapped lines and therefore never more than the maximum it was given.</summary>
        public float NaturalWidth => _natural;
        public float LineHeight => _pieces.Count == 0 ? 0f : (_descent - _ascent) + 2 * _padY;
        public int   PieceCount => _pieces.Count;
        public float Height =>
            _lineStart.Count == 0 ? 0f : _lineStart.Count * LineHeight + (_lineStart.Count - 1) * LineGap;

        public void Add(string text, StackupField field, StackupLabelStyle style, SKFont font, float gapBefore)
        {
            if (text is null or "") return;
            var m = font.Metrics;
            _pieces.Add(new PieceSpec(text, field, style, font.MeasureText(text), m.Ascent, m.Descent, gapBefore));
            _ascent  = Math.Min(_ascent,  m.Ascent);
            _descent = Math.Max(_descent, m.Descent);
            Reflow();
        }

        /// <summary>Greedy: a piece that would cross <c>_maxWidth</c> starts a new line. The FIRST
        /// piece of a line never carries a leading gap — <see cref="Left"/> is what the leader lines
        /// and the x-overlap partition are stated in terms of, so it has to be where the ink starts —
        /// and a piece wider than the whole width still gets its own line rather than none.</summary>
        private void Reflow()
        {
            _lineStart.Clear();
            _width = 0f;
            _natural = 0f;
            float cursor = 0f;
            for (int i = 0; i < _pieces.Count; i++)
            {
                var p = _pieces[i];
                bool first = _lineStart.Count == 0 || cursor <= 0f;
                float advance = first ? 0f : PieceGap + p.GapBefore;
                float right   = cursor + advance + p.Width + 2 * LabelPadX;

                // The unwrapped width, accumulated alongside: what the run would need on one line.
                _natural += (i == 0 ? 0f : PieceGap + p.GapBefore) + p.Width + 2 * LabelPadX;

                if (!first && right > _maxWidth)
                {
                    _lineStart.Add(i);
                    cursor = p.Width + 2 * LabelPadX;
                }
                else
                {
                    if (_lineStart.Count == 0) _lineStart.Add(0);
                    cursor = right;
                }
                _width = Math.Max(_width, cursor);
            }
        }

        /// <summary>Emits every piece at <paramref name="top"/>. A piece's rect is its measured width
        /// inflated by the padding, and consecutive pieces are separated by <see cref="PieceGap"/> on
        /// top of that, so no two of them intersect.</summary>
        public void Place(float top, string layerName, List<StackupLabel> labels, List<StackupHit> hits)
        {
            float lineH = LineHeight;
            for (int line = 0; line < _lineStart.Count; line++)
            {
                int from = _lineStart[line];
                int to   = line + 1 < _lineStart.Count ? _lineStart[line + 1] : _pieces.Count;

                float lineTop  = top + line * (lineH + LineGap);
                float baseline = lineTop + _padY - _ascent;
                float cursor   = Left;

                for (int i = from; i < to; i++)
                {
                    var p = _pieces[i];
                    if (i > from) cursor += p.GapBefore;
                    var rect = new SKRect(cursor, lineTop, cursor + p.Width + 2 * LabelPadX, lineTop + lineH);
                    labels.Add(new StackupLabel(p.Text, layerName, p.Field, p.Style, rect, cursor + LabelPadX, baseline));
                    hits.Add(new StackupHit(StackupHitKind.Label, layerName, p.Field, rect));
                    cursor = rect.Right + PieceGap;
                }
            }
        }
    }

    /// <summary>A <see cref="PieceRun"/> that has not been placed yet: it knows where it WANTS to be
    /// (its band's vertical centre) and the separator decides where it lands.</summary>
    private sealed class LabelGroup : PieceRun
    {
        public LabelGroup(float left, float maxWidth) : base(left, maxWidth) { }

        public string LayerName { get; init; } = "";
        public int    Index     { get; init; }
        public float  AnchorY   { get; init; }
        public float  Top       { get; set; }

        public void Emit(List<StackupLabel> labels, List<StackupHit> hits) => Place(Top, LayerName, labels, hits);
    }

    /// <summary>
    /// <b>R-stk1-9's guarantee, made structural.</b> Every group wants its band's centre line; when
    /// two would collide — which happens the moment two thin bands are adjacent — they are pushed
    /// apart SYMMETRICALLY about their midpoint, which is what keeps each one the nearest label to
    /// its own band with no callout line needed to say so.
    ///
    /// <para>Groups that cannot overlap horizontally are never pushed against each other: the list is
    /// partitioned into components by x-interval overlap first, which is what keeps a via's name in
    /// the band column from displacing a spec in the label column. Within a component the classic
    /// cluster merge runs — place each group at its ideal centre, merge any two that touch, re-centre
    /// the merged cluster about the MEAN of its members' ideal centres, and repeat — then one
    /// downward-only pass keeps the whole thing below <paramref name="minY"/>.</para>
    ///
    /// <para><b>The test is the property, not this algorithm</b> (R-stk1-9): over every shipped
    /// technology and over a generated stack of the thinnest bands the unit can express, no two
    /// entries in <see cref="Labels"/> intersect. That is what makes any future change to the
    /// placement safe.</para>
    /// </summary>
    private static void Separate(List<LabelGroup> groups, float minY, float gap)
    {
        if (groups.Count == 0) return;
        foreach (var g in groups) g.Top = g.AnchorY - g.Height * 0.5f;

        var byLeft = groups.OrderBy(g => g.Left).ThenBy(g => g.Index).ToList();
        int i = 0;
        while (i < byLeft.Count)
        {
            var component = new List<LabelGroup> { byLeft[i] };
            float right = byLeft[i].Left + byLeft[i].Width;
            int j = i + 1;
            while (j < byLeft.Count && byLeft[j].Left < right)
            {
                component.Add(byLeft[j]);
                right = Math.Max(right, byLeft[j].Left + byLeft[j].Width);
                j++;
            }
            SeparateComponent(component, minY, gap);
            i = j;
        }
    }

    private static void SeparateComponent(List<LabelGroup> component, float minY, float gap)
    {
        if (component.Count < 2)
        {
            if (component.Count == 1) component[0].Top = Math.Max(component[0].Top, minY);
            return;
        }

        var ordered = component.OrderBy(g => g.AnchorY).ThenBy(g => g.Index).ToList();
        var clusters = new List<List<LabelGroup>>();
        foreach (var g in ordered)
        {
            clusters.Add([g]);
            Reposition(clusters[^1], gap);
            while (clusters.Count >= 2 && Top(clusters[^2]) + TotalHeight(clusters[^2], gap) + gap > Top(clusters[^1]))
            {
                var merged = clusters[^2];
                merged.AddRange(clusters[^1]);
                clusters.RemoveAt(clusters.Count - 1);
                Reposition(merged, gap);
            }
        }

        // Downward-only, so it cannot re-introduce an overlap it has just resolved.
        float cursor = minY;
        foreach (var cluster in clusters)
        {
            float top = Top(cluster);
            if (top < cursor) Shift(cluster, cursor - top);
            cursor = Top(cluster) + TotalHeight(cluster, gap) + gap;
        }
    }

    private static float TotalHeight(List<LabelGroup> cluster, float gap)
        => cluster.Sum(g => g.Height) + gap * (cluster.Count - 1);

    private static float Top(List<LabelGroup> cluster) => cluster[0].Top;

    private static void Reposition(List<LabelGroup> cluster, float gap)
    {
        float centre = 0f;
        foreach (var g in cluster) centre += g.AnchorY;
        centre /= cluster.Count;

        float top = centre - TotalHeight(cluster, gap) * 0.5f;
        foreach (var g in cluster) { g.Top = top; top += g.Height + gap; }
    }

    private static void Shift(List<LabelGroup> cluster, float dy)
    {
        foreach (var g in cluster) g.Top += dy;
    }
}

// The layer EXPRESSION a rule measures (docs/design/layout-view.md §9A.5).
//
// DRC v1 assumed a rule names one drawing layer. Real process rule decks do not work that way:
// a rule measures a DERIVED layer built from boolean algebra, sizing, and topological selection
// over the drawing layers, and only then applies a width/spacing/enclosure check to the result.
// A rule that says "minimum metal width, excluding the keep-out region, where it meets a pad" is
// three region operations before any measurement happens. v1 could not read those rules
// at all — not because the check was missing, but because there was nowhere to put the operand.
//
// <para><b>The vocabulary here is deliberately the one real decks use</b>, so translating a deck
// statement into this model is mechanical rather than interpretive. Adding an operation that no
// deck actually invokes buys nothing; leaving one out means a rule cannot be expressed and must be
// reported as unsupported instead — which is the honest outcome, but a worse one.</para>
//
// Serialized as TEXT, not as polymorphic JSON — see <see cref="DrcLayerExprParser"/> for why.

namespace CircuitRF.Design.Layout.Drc;

/// <summary>
/// A region-valued expression over drawing layers. Evaluated by <see cref="DrcRegionEval"/>.
///
/// <para>Every case is an immutable record, so an expression is safe to share across rules and
/// across threads, and two structurally identical expressions compare equal — which is what lets
/// the evaluator cache sub-expression results within one run without a hand-written key.</para>
/// </summary>
public abstract record DrcLayerExpr
{
    /// <summary>A drawing layer, by its own <see cref="LayerKey"/>. The only leaf.</summary>
    public sealed record Layer(LayerKey Key) : DrcLayerExpr;

    /// <summary>Intersection — the region covered by BOTH operands.</summary>
    public sealed record And(DrcLayerExpr A, DrcLayerExpr B) : DrcLayerExpr;

    /// <summary>Union — the region covered by EITHER operand.</summary>
    public sealed record Or(DrcLayerExpr A, DrcLayerExpr B) : DrcLayerExpr;

    /// <summary>Difference — <paramref name="A"/> with <paramref name="B"/> removed.
    /// Named <c>Not</c> to match deck vocabulary (<c>a.not(b)</c>), NOT a unary complement:
    /// the complement of a region is unbounded and has no representation here.</summary>
    public sealed record Not(DrcLayerExpr A, DrcLayerExpr B) : DrcLayerExpr;

    /// <summary>Symmetric difference — covered by exactly one operand.</summary>
    public sealed record Xor(DrcLayerExpr A, DrcLayerExpr B) : DrcLayerExpr;

    /// <summary>
    /// Grow (positive) or shrink (negative) by <paramref name="ByDbu"/>.
    ///
    /// <para><b>Join style is MITER, matching the width check's own erosion</b> — a derived layer
    /// built by sizing and then measured for width must not disagree with the measurement about
    /// what a corner is. Round joins would round every convex corner by the sizing distance and
    /// make the derived layer's own corners read as narrow.</para>
    /// </summary>
    public sealed record Sized(DrcLayerExpr A, long ByDbu) : DrcLayerExpr;

    /// <summary>
    /// Whole-polygon topological selection: keep the polygons of <paramref name="A"/> that stand in
    /// <paramref name="Op"/> relation to <paramref name="B"/>.
    ///
    /// <para><b>This selects WHOLE polygons, never partial area</b> — that is the entire difference
    /// between <c>a.interacting(b)</c> and <c>a.and(b)</c>, and getting it wrong produces a region
    /// that looks plausible and measures differently. A polygon either survives intact or is
    /// dropped.</para>
    /// </summary>
    public sealed record Select(DrcLayerExpr A, DrcLayerExpr B, DrcSelectOp Op) : DrcLayerExpr;

    /// <summary>The holes of <paramref name="A"/>, as solid regions in their own right.</summary>
    public sealed record Holes(DrcLayerExpr A) : DrcLayerExpr;

    /// <summary>
    /// Union of <paramref name="A"/> with itself — merges abutting and overlapping polygons into
    /// single regions.
    ///
    /// <para>Not redundant: every other operation here already returns a merged result, but a deck
    /// states <c>merged</c> explicitly where the merge is the POINT of the step (two abutting
    /// rectangles must be measured as one shape, not two), and carrying that through keeps a
    /// translated rule readable against its source.</para>
    /// </summary>
    public sealed record Merged(DrcLayerExpr A) : DrcLayerExpr;

    /// <summary>
    /// Keeps the polygons of <paramref name="A"/> whose AREA falls in
    /// [<paramref name="MinDbu2"/>, <paramref name="MaxDbu2"/>].
    ///
    /// <para>Area is in square DBU, and the bound is inclusive at both ends. A null bound is
    /// open — <c>with_area(1/0, 100, )</c> is "at least 100". Selecting by area is how a deck
    /// isolates fill shapes, stray slivers and minimum-area candidates before measuring them.</para>
    /// </summary>
    public sealed record WithArea(DrcLayerExpr A, long? MinDbu2, long? MaxDbu2) : DrcLayerExpr;

    /// <summary>
    /// Keeps the polygons of <paramref name="A"/> whose PERIMETER falls in
    /// [<paramref name="MinDbu"/>, <paramref name="MaxDbu"/>], in DBU.
    ///
    /// <para>Perimeter rather than "edge length" deliberately: a deck's <c>with_length</c> selects
    /// on an EDGE collection, which this model has no type for, and approximating it by the
    /// polygon's own perimeter would silently select different shapes. This is the polygon-level
    /// question that IS expressible; an edge-level one is reported as unsupported instead.</para>
    /// </summary>
    public sealed record WithPerimeter(DrcLayerExpr A, long? MinDbu, long? MaxDbu) : DrcLayerExpr;

    /// <summary>Renders the expression in the parser's own syntax. Round-trips through
    /// <see cref="DrcLayerExprParser.Parse"/>.</summary>
    public sealed override string ToString() => DrcLayerExprParser.Format(this);

    /// <summary>Every drawing layer this expression reads, deduplicated. Used to decide which
    /// layers a run must build regions for, and to report a rule that names a layer the technology
    /// does not define.</summary>
    public IReadOnlyCollection<LayerKey> ReferencedLayers()
    {
        var acc = new HashSet<LayerKey>();
        Walk(this, acc);
        return acc;

        static void Walk(DrcLayerExpr e, HashSet<LayerKey> into)
        {
            switch (e)
            {
                case Layer l:                   into.Add(l.Key); break;
                case And a:                     Walk(a.A, into); Walk(a.B, into); break;
                case Or o:                      Walk(o.A, into); Walk(o.B, into); break;
                case Not n:                     Walk(n.A, into); Walk(n.B, into); break;
                case Xor x:                     Walk(x.A, into); Walk(x.B, into); break;
                case Sized s:                   Walk(s.A, into); break;
                case Select sel:                Walk(sel.A, into); Walk(sel.B, into); break;
                case Holes h:                   Walk(h.A, into); break;
                case Merged m:                  Walk(m.A, into); break;
                case WithArea wa:               Walk(wa.A, into); break;
                case WithPerimeter wp:          Walk(wp.A, into); break;
            }
        }
    }
}

/// <summary>
/// The whole-polygon selection relations a deck uses. Each keeps polygons of A; they differ only in
/// which ones.
/// </summary>
public enum DrcSelectOp
{
    /// <summary>A polygon of A that touches or overlaps B anywhere.</summary>
    Interacting,

    /// <summary>The complement of <see cref="Interacting"/> within A.</summary>
    NotInteracting,

    /// <summary>A polygon of A entirely contained within B.</summary>
    Inside,

    /// <summary>A polygon of A entirely outside B (no touch, no overlap).</summary>
    Outside,

    /// <summary>A polygon of A that entirely contains at least one polygon of B.</summary>
    Covering,

    /// <summary>The complement of <see cref="Covering"/> within A.</summary>
    NotCovering,
}

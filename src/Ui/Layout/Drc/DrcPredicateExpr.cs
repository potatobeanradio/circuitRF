// The PREDICATE half of the rule language (docs/design/wbond.md §8.1, WB32a).
//
// ── What this adds, and what it deliberately does not replace ────────────────────────────────────
//
// `DrcLayerExpr` is REGION-valued: every node returns a set of polygons, and the rule that owns it
// supplies the measurement (a `DrcRuleKind`) and the threshold (a `ValueDbu`). That shape is exactly
// right for a die-side rule, where the rules really are a small set of measurements applied to
// different regions at different values.
//
// An assembly rule is not that shape. "Loop height must stay under a curve of span" is a COMPARISON
// between two computed scalars, and there is no measurement kind that expresses it — which is why
// §8.1's own widening table adds functions and a new kind of value rather than more rule kinds. So
// this file adds a scalar/predicate layer ON TOP of the existing language: new operands (wire sets),
// new functions, and one genuinely new value kind (the piecewise-linear `envelope`). Region operands
// are still `DrcLayerExpr`, parsed by the untouched `DrcLayerExprParser` — `wire_to_layer(G1, 8/0)`
// hands its second argument straight to it.
//
// The 2D layer language is not modified in any way. That is checked, not assumed: a pinned
// regression parses and re-formats a corpus of pre-existing expressions and compares the text
// byte-for-byte.
//
// ── The iteration domain, which is the part that is easy to get wrong ────────────────────────────
//
// `loop_height(G1) <= envelope(max_loop, span(G1))` has to hold for EVERY wire in G1, and
// `wire_spacing(G1, G2) >= 4mil` for every PAIR. So a predicate is not evaluated once — it is
// evaluated once per candidate, and a candidate is either one wire or one pair of wires. Which one
// is decided by the functions the predicate uses (see <see cref="WasmDomain"/>), never guessed per
// evaluation, so a rule cannot mean different things on two different designs.

using CircuitRF.Ui.Layout.Assembly;

namespace CircuitRF.Ui.Layout.Drc;

/// <summary>
/// What a value is measured in. Carried so a comparison between two different kinds of quantity is
/// refused at PARSE time rather than producing a plausible number.
///
/// <para><b>The failure this exists to prevent is specific.</b> Lengths are stored in nanometres, so
/// a bare <c>30</c> compared against <c>angle_change(G1)</c> would be a perfectly reasonable 30
/// degrees, while a bare <c>30</c> compared against <c>span(G1)</c> would be thirty NANOMETRES — a
/// rule that can never fire, written by someone who meant 30 mil. Quantities make the second one a
/// parse error that says "state a unit".</para>
/// </summary>
public enum WasmQuantity
{
    /// <summary>A distance, in nanometres.</summary>
    Length,

    /// <summary>An angle, in degrees.</summary>
    Angle,

    /// <summary>A plain number — a count or a ratio.</summary>
    Number,
}

/// <summary>What one evaluation of a predicate is ABOUT — see this file's header.</summary>
public enum WasmDomain
{
    /// <summary>One wire at a time.</summary>
    Wire,

    /// <summary>One ordered pair of wires at a time, drawn from the two sets a pair function names.</summary>
    Pair,
}

/// <summary>Functions measured over a PAIR of wires.</summary>
public enum WasmPairFunction
{
    /// <summary>
    /// Minimum SURFACE-to-surface distance between the two wires in 3D — each wire's polyline
    /// treated as a chain of capsules of its own radius. This is §8's "wire-to-wire 3D clearance",
    /// and it is the quantity that cannot be expressed as a 2D polygon spacing at all.
    /// </summary>
    WireSpacing,

    /// <summary>
    /// Minimum foot-to-foot distance between the two wires, in the layout plane.
    ///
    /// <para><b>This is not the same quantity as <see cref="WireSpacing"/>, and §8's rule table needs
    /// both.</b> "Minimum wire pitch" is a bond-pad spacing: how far apart the bonder has to place
    /// two feet, measured between the feet regardless of where the loops go. Clearance is how close
    /// the wires come anywhere along their length. A house states both, at different values, and
    /// expressing pitch as clearance would pass a pair of feet on top of each other whose loops
    /// happen to diverge.</para>
    /// </summary>
    FootPitch,
}

/// <summary>Functions measured over ONE wire.</summary>
public enum WasmWireFunction
{
    /// <summary>Maximum z minus minimum z — <c>Wire.LoopHeightNm</c>, the one definition (§3.0).</summary>
    LoopHeight,

    /// <summary>The wire's span — the <b>XY</b> distance from the input foot to the output foot,
    /// <c>Wire.SpanMetres</c>, the one definition (owner, 2026-08-19: there is no z in a span). It
    /// used to be the 3-D distance, so a rule written against the number the panel showed was checking
    /// something else on any wire that dropped.</summary>
    Span,

    /// <summary>Largest turn angle, in degrees, between consecutive segments of the polyline.</summary>
    AngleChange,

    /// <summary>Minimum 3D distance from the wire to the boundary of the reference layout's extent —
    /// §8's "wire-to-die-edge clearance".</summary>
    DistToEdge,

    /// <summary>Minimum 3D distance from the wire to artwork in a named region, at that region's own
    /// stackup height — §8's "wire-to-pad-edge" and "wire-to-lead-edge" clearances.</summary>
    WireToLayer,
}

public enum WasmCompareOp { Lt, Le, Gt, Ge, Eq, Ne }

/// <summary>
/// A scalar-valued node. Immutable records, so two structurally identical values compare equal and a
/// resolved rule is safe to share across threads — the same property that lets
/// <see cref="DrcRegionEval"/> memoize sub-expressions without a hand-written key.
/// </summary>
public abstract record WasmValue
{
    /// <summary>A literal, already converted to its quantity's storage unit (nanometres / degrees).
    /// The positional member is <c>Kind</c> rather than <c>Quantity</c> only because the base's
    /// computed <see cref="WasmValue.Quantity"/> owns that name for every node.</summary>
    public sealed record Literal(double Value, WasmQuantity Kind) : WasmValue;

    /// <summary>A pair function over two wire sets. <c>SetB</c> equals <c>SetA</c> for the one-argument
    /// form, which measures within a single set.</summary>
    public sealed record PairCall(WasmPairFunction Fn, string SetA, string SetB) : WasmValue;

    /// <summary>A per-wire function over one wire set. <paramref name="Region"/> is non-null only for
    /// <see cref="WasmWireFunction.WireToLayer"/>.</summary>
    public sealed record WireCall(WasmWireFunction Fn, string Set, DrcLayerExpr? Region) : WasmValue;

    /// <summary>A piecewise-linear lookup: the named table evaluated at <paramref name="Arg"/>.</summary>
    public sealed record EnvelopeCall(string Table, WasmValue Arg) : WasmValue;

    /// <summary>What this value is measured in.</summary>
    public WasmQuantity Quantity => this switch
    {
        Literal l      => l.Kind,
        PairCall       => WasmQuantity.Length,
        WireCall w     => w.Fn == WasmWireFunction.AngleChange ? WasmQuantity.Angle : WasmQuantity.Length,

        // An envelope's Y values are nanometres — the tables §8.1 exists for are loop-height-vs-span
        // curves. A ratio table would need a declared quantity on the table itself; it is not offered
        // rather than being silently allowed to mean either.
        EnvelopeCall   => WasmQuantity.Length,
        _ => WasmQuantity.Number,
    };

    /// <summary>Wire domain unless something in the tree needs a pair.</summary>
    public WasmDomain Domain => this switch
    {
        PairCall            => WasmDomain.Pair,
        EnvelopeCall e      => e.Arg.Domain,
        _                   => WasmDomain.Wire,
    };

    /// <summary>Every wire-set name this value reads, in first-seen order.</summary>
    public void CollectSets(List<string> into)
    {
        switch (this)
        {
            case PairCall p:
                Add(into, p.SetA);
                Add(into, p.SetB);
                break;
            case WireCall w:
                Add(into, w.Set);
                break;
            case EnvelopeCall e:
                e.Arg.CollectSets(into);
                break;
        }

        static void Add(List<string> list, string name)
        {
            if (!list.Contains(name, StringComparer.OrdinalIgnoreCase)) list.Add(name);
        }
    }

    /// <summary>Every envelope table this value looks up, in first-seen order.</summary>
    public void CollectEnvelopes(List<string> into)
    {
        if (this is not EnvelopeCall e) return;
        if (!into.Contains(e.Table, StringComparer.OrdinalIgnoreCase)) into.Add(e.Table);
        e.Arg.CollectEnvelopes(into);
    }

    public sealed override string ToString() => DrcPredicateParser.Format(this);
}

/// <summary>A boolean node. The top of a `.wasm` rule's expression.</summary>
public abstract record WasmPredicate
{
    public sealed record Compare(WasmValue Left, WasmCompareOp Op, WasmValue Right) : WasmPredicate;
    public sealed record And(WasmPredicate A, WasmPredicate B) : WasmPredicate;
    public sealed record Or(WasmPredicate A, WasmPredicate B) : WasmPredicate;
    public sealed record Not(WasmPredicate A) : WasmPredicate;

    /// <summary>Pair domain if ANY comparison in the tree needs a pair — see this file's header.</summary>
    public WasmDomain Domain => this switch
    {
        Compare c => c.Left.Domain == WasmDomain.Pair || c.Right.Domain == WasmDomain.Pair
                        ? WasmDomain.Pair : WasmDomain.Wire,
        And a     => a.A.Domain == WasmDomain.Pair || a.B.Domain == WasmDomain.Pair
                        ? WasmDomain.Pair : WasmDomain.Wire,
        Or o      => o.A.Domain == WasmDomain.Pair || o.B.Domain == WasmDomain.Pair
                        ? WasmDomain.Pair : WasmDomain.Wire,
        Not n     => n.A.Domain,
        _         => WasmDomain.Wire,
    };

    /// <summary>Every wire-set name the predicate reads, in first-seen order.</summary>
    public IReadOnlyList<string> ReferencedSets()
    {
        var acc = new List<string>();
        Walk(this, v => v.CollectSets(acc));
        return acc;
    }

    /// <summary>Every envelope table the predicate looks up, in first-seen order.</summary>
    public IReadOnlyList<string> ReferencedEnvelopes()
    {
        var acc = new List<string>();
        Walk(this, v => v.CollectEnvelopes(acc));
        return acc;
    }

    /// <summary>
    /// The two sets a pair-domain predicate draws its pair from, or null when it is not pair-domain.
    /// Taken from the FIRST pair call in the tree — a predicate naming two different pairings is
    /// refused at resolve time rather than silently choosing one.
    /// </summary>
    public (string A, string B)? PairSets()
    {
        (string, string)? found = null;
        Walk(this, v => { if (found is null && v is WasmValue.PairCall p) found = (p.SetA, p.SetB); });
        return found;
    }

    /// <summary>Every pair call in the tree — so a resolver can check they all name the same pairing.</summary>
    public IReadOnlyList<WasmValue.PairCall> PairCalls()
    {
        var acc = new List<WasmValue.PairCall>();
        Walk(this, v => { if (v is WasmValue.PairCall p) acc.Add(p); });
        return acc;
    }

    private static void Walk(WasmPredicate p, Action<WasmValue> onValue)
    {
        switch (p)
        {
            case Compare c: WalkValue(c.Left, onValue); WalkValue(c.Right, onValue); break;
            case And a:     Walk(a.A, onValue); Walk(a.B, onValue); break;
            case Or o:      Walk(o.A, onValue); Walk(o.B, onValue); break;
            case Not n:     Walk(n.A, onValue); break;
        }
    }

    private static void WalkValue(WasmValue v, Action<WasmValue> onValue)
    {
        onValue(v);
        if (v is WasmValue.EnvelopeCall e) WalkValue(e.Arg, onValue);
    }

    /// <summary>Evaluates against one candidate. See <see cref="IWasmMeasurements"/>.</summary>
    public bool Evaluate(IWasmMeasurements m) => this switch
    {
        Compare c => Apply(c.Op, Eval(c.Left, m), Eval(c.Right, m)),
        And a     => a.A.Evaluate(m) && a.B.Evaluate(m),
        Or o      => o.A.Evaluate(m) || o.B.Evaluate(m),
        Not n     => !n.A.Evaluate(m),
        _         => true,
    };

    /// <summary>Evaluates one scalar node against a candidate.</summary>
    public static double Eval(WasmValue v, IWasmMeasurements m) => v switch
    {
        WasmValue.Literal l      => l.Value,
        WasmValue.PairCall p     => m.Pair(p.Fn, p.SetA, p.SetB),
        WasmValue.WireCall w     => m.Wire(w.Fn, w.Set, w.Region),
        WasmValue.EnvelopeCall e => m.Envelope(e.Table, Eval(e.Arg, m)),
        _                        => 0.0,
    };

    /// <summary>
    /// Equality on doubles uses a relative tolerance rather than <c>==</c>. A rule stating
    /// <c>diameter == 1mil</c> against a value that round-tripped through mil and back would
    /// otherwise fail on the last bit, which is not a design rule anyone wrote.
    /// </summary>
    private static bool Apply(WasmCompareOp op, double l, double r)
    {
        double tol = 1e-9 * Math.Max(1.0, Math.Max(Math.Abs(l), Math.Abs(r)));
        return op switch
        {
            WasmCompareOp.Lt => l <  r - tol,
            WasmCompareOp.Le => l <= r + tol,
            WasmCompareOp.Gt => l >  r + tol,
            WasmCompareOp.Ge => l >= r - tol,
            WasmCompareOp.Eq => Math.Abs(l - r) <= tol,
            WasmCompareOp.Ne => Math.Abs(l - r) >  tol,
            _                => true,
        };
    }

    public sealed override string ToString() => DrcPredicateParser.Format(this);
}

/// <summary>
/// What a predicate asks of the world for one candidate. The geometry lives behind this interface so
/// the AST stays framework-free and testable with a hand-written stub — which is how every gate in
/// <c>WasmPredicateParserTests</c> exercises the language without building a design.
/// </summary>
public interface IWasmMeasurements
{
    /// <summary>The pair function's value for the candidate PAIR, in nanometres.</summary>
    double Pair(WasmPairFunction fn, string setA, string setB);

    /// <summary>The per-wire function's value for the candidate WIRE (nanometres, or degrees for
    /// <see cref="WasmWireFunction.AngleChange"/>).</summary>
    double Wire(WasmWireFunction fn, string set, DrcLayerExpr? region);

    /// <summary>The named table evaluated at <paramref name="x"/> — see <see cref="WasmEnvelope"/>.</summary>
    double Envelope(string table, double x);
}

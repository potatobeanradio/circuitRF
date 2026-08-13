namespace CircuitRF.Ui.Layout.PCells;

/// <summary>
/// MTee artwork (brief-L5a-pcell-contract-and-microstrip.md §3): two collinear through arms
/// (widths <c>W1</c>, <c>W2</c> — may differ, "a tee whose through line steps width is entirely
/// ordinary") plus a perpendicular branch (<c>W3</c>), unioned at the junction. R-pc-3 + the
/// brief's own MTee convention: pin 1 at the origin, the through line running along +X to pin 2,
/// the branch along -Y to pin 3.
///
/// <b>Each arm carries its own drawn length (<c>L1</c>/<c>L2</c>/<c>L3</c>), independent of its
/// width.</b> These arrived after an owner report that dragging W1's width gripper moved the far end
/// of the component: the arm length used to be <c>2.5 ×</c> that arm's own width, so a width edit
/// relocated the junction and both other pins along the perpendicular axis. A cell that declares no
/// length still gets the old derivation, so nothing already drawn moves — see
/// <see cref="PCellGeometryHelpers.StubLengthFactor"/> and <see cref="PCellGeometryHelpers.ResolveArmLength"/>.
/// The arms remain ARTWORK: <c>MicrostripTeeModel</c> has no length term, so a longer arm draws
/// longer and simulates identically. Real line length is a separate, user-placed MLIN.
///
/// <b>docs/sonnet-briefs/brief-L5-followups.md §2 (R-L5f-5): the branch direction is DEFINED BY
/// THE SYMBOL, not by a coordinate sign.</b> The MTee symbol's own port 3 sits at schematic-canvas
/// (0, +200) — "down," since the schematic canvas is Y-DOWN (<c>EditableSchematic.cs</c>'s own port
/// table: "+Y is down in this codebase"). Layout is Y-UP (<c>LayoutInstanceTransform</c>'s own
/// convention), so the SAME physical "down" is -Y here — the earlier "branch along +Y" wording (also
/// in pcell-contract.md R4) was a literal reading of "+Y" that silently crossed from the symbol's
/// Y-down world into layout's Y-up one without flipping the sign, producing an upside-down T the
/// symbol had already been corrected once before. State the rule as "match the symbol," because a
/// coordinate sign is exactly what drifts back the next time someone reads "+Y" out of context.
/// </summary>
public static class MTeePCell
{
    public const string GeneratorId = "MTEE";

    public static PCellResult Generate(
        IReadOnlyDictionary<string, PCellValue> parameters,
        Technology? technology,
        PCellLayerSelection layerSelection)
    {
        var diagnostics = new List<string>();
        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;

        long w1 = PCellUnits.MetresToDbu(parameters.Real("W1", 0.0), dbuPerMicron);
        long w2 = PCellUnits.MetresToDbu(parameters.Real("W2", 0.0), dbuPerMicron);
        long w3 = PCellUnits.MetresToDbu(parameters.Real("W3", 0.0), dbuPerMicron);

        // L1/L2/L3 — each arm's own drawn length, independent of its width. NOT DECLARED at all (an
        // older cell) falls back to the legacy StubLengthFactor×width derivation, so nothing already
        // on disk moves. See PCellGeometryHelpers.StubLengthFactor for why the derived rule was
        // retired for this cell: it made a WIDTH gripper move the junction along the other axis.
        double? l1Meters = PCellGeometryHelpers.Declared(parameters, "L1");
        double? l2Meters = PCellGeometryHelpers.Declared(parameters, "L2");
        double? l3Meters = PCellGeometryHelpers.Declared(parameters, "L3");

        long stub1 = PCellGeometryHelpers.ResolveArmLength(l1Meters, w1, dbuPerMicron);
        long stub2 = PCellGeometryHelpers.ResolveArmLength(l2Meters, w2, dbuPerMicron);
        long stub3 = PCellGeometryHelpers.ResolveArmLength(l3Meters, w3, dbuPerMicron);

        // Gated on the stub ALREADY being positive. A length grip cannot reach a negative value any
        // more — its own Min below is exactly this minimum — so what this guards is the paths that do
        // not go through the solver at all: a hand-typed number, a script, an older file. Those keep
        // this editor's standing "report a bad parameter rather than forbid one" behaviour and render
        // as asked, rather than being yanked to half the crossing width without explanation.
        if (l1Meters is not null && stub1 > 0)
            stub1 = PCellGeometryHelpers.ClampArmLength(stub1, w3 / 2, GeneratorId, "L1", "W3", diagnostics);
        if (l2Meters is not null && stub2 > 0)
            stub2 = PCellGeometryHelpers.ClampArmLength(stub2, w3 / 2, GeneratorId, "L2", "W3", diagnostics);
        if (l3Meters is not null && stub3 > 0)
            stub3 = PCellGeometryHelpers.ClampArmLength(
                stub3, Math.Max(w1, w2) / 2, GeneratorId, "L3", "the through line's width", diagnostics);

        var signalLayer = SubstrateResolver.ResolveSignalLayerKey(technology, layerSelection, out _);

        // Junction sits at the origin's own X — pin 1 at the far end of the first through arm, so
        // the junction (where the branch meets the through line) is at (stub1, 0); pin 2 sits
        // stub2 further along +X from there.
        long junctionX = stub1;

        var throughArm1 = PCellGeometryHelpers.BuildArmRect(0, 0, 0.0, stub1, w1, signalLayer);
        var throughArm2 = PCellGeometryHelpers.BuildArmRect(junctionX, 0, 0.0, stub2, w2, signalLayer);
        // R-L5f-5: -Y (270°), matching the symbol's own downward port 3 — see the type doc comment.
        var branch = PCellGeometryHelpers.BuildArmRect(junctionX, 0, 270.0, stub3, w3, signalLayer);

        var merged = PCellGeometryHelpers.UnionArms([throughArm1, throughArm2, branch], signalLayer, technology);

        var pins = new[]
        {
            new PCellPin("1", 0, 0, signalLayer, w1, 180.0),
            new PCellPin("2", junctionX + stub2, 0, signalLayer, w2, 0.0),
            new PCellPin("3", junctionX, -stub3, signalLayer, w3, 270.0),
        };

        // pcell-parameter-handles.md — a tee's outline has exactly six outward corners: the two at
        // each through-arm's own end cap, and the two at the branch tip. (The two where the branch
        // meets the through line are re-entrant — they are where the metal folds inward, not a place
        // anything is grabbed by.)
        //
        // SIX grips, ONE AT EACH CORNER OF THE METAL — and each drives BOTH of its own arm's
        // parameters, by which way it is dragged: across the arm sets that arm's WIDTH, along the arm
        // sets its LENGTH (owner request, 2026-08-12). That is R-pch-4a's orthogonal decomposition,
        // not R-pch-4's forbidden apportioning: along-axis and across-axis are independent scalars
        // with one parameter each, so the split is unique and nothing is guessed. It also keeps the
        // grip count exactly what it was — a second, length-only grip per arm would double the
        // clutter to say something the corner already says.
        //
        // The anchor moves from the arm's own end-cap centre to the JUNCTION, which is what makes the
        // along-axis projection read as that arm's length. The across-axis projection is unchanged by
        // that move: every anchor keeps the coordinate the width is measured on (y = 0 for the through
        // arms, x = junctionX for the branch), so a width drag measures exactly what it always did.
        //
        // KeepAnchorFixed because arm 1's anchor genuinely travels: R4 pins pin 1 at the cell origin,
        // so growing arm 1 can only push the junction along +X. The host translates the instance so
        // the junction keeps its world position and pin 1 moves instead — "drag this end, keep the
        // other end still", the gesture MLIN's own left-edge grip already established. Arms 2 and 3
        // do not move their anchor, where the flag is a documented no-op; set anyway so a reader does
        // not have to work out which of three sibling arms is the special one.
        //
        // Keyed on DECLARED, never on positive: a length is allowed to pass through zero and go
        // negative mid-drag (PCellDimensionSign normalises it at mouse up), and a grip that lost its
        // cross axis and its pinning halfway through a gesture would change what the drag even means.
        //
        // A cell that does NOT declare all three lengths keeps exactly the grips it always had: the
        // old end-cap anchors, no cross axis, no pinning. R2's one list is what a handle may name and
        // PCellHandleSolver.Validate refuses one that names anything else, so a cross axis naming an
        // absent L would be dropped and reported on every generate. The pinning matters just as much:
        // on the derived path the junction moves WITH W1, so holding it fixed would make a plain width
        // drag translate the instance — a behaviour change for artwork that already exists.
        long throughEnd = junctionX + stub2;
        const PCellHandleQuantity len = PCellHandleQuantity.Length;
        bool twoAxis = l1Meters is not null && l2Meters is not null && l3Meters is not null;

        // The LENGTH axis is BOUNDED and the width axis deliberately is not (owner's call,
        // 2026-08-12: "clamp the L parameters so they never go negative during a drag; keep W
        // untouched, I like how they are currently working"). A width that overshoots recovers
        // exactly at mouse up — the same rectangle, wound the right way — so there is nothing to stop
        // it doing. A length that overshoots cannot be recovered exactly at all, and while it is
        // negative it draws its arm back over the one that belongs on that side.
        //
        // The bound is the SAME minimum the crossing-width clamp above enforces, derived from the very
        // same integer and converted down. That agreement is the point: a floor lower than the clamp
        // would let the solver propose a value the generator then silently overrides, so the grip
        // would keep chasing a position the geometry had already stopped moving to.
        //
        // Min on the HANDLE is safe where a clamp inside the generator is not: PCellHandleSolver
        // measures its sensitivity ONCE, at the drag's starting value, and Propose clamps only the
        // candidate it derives from it — so the map it measured stays intact and the grip simply
        // stops. Clamping in the generator would flatten that map and the grip would stop following
        // the cursor at all.
        double MinLength(long halfCrossDbu) =>
            PCellUnits.DbuToMetres(Math.Max(halfCrossDbu, 1), dbuPerMicron);

        PCellHandleCrossAxis? Length(string name, long halfCrossDbu) =>
            twoAxis ? new PCellHandleCrossAxis(name, Min: MinLength(halfCrossDbu), Quantity: len) : null;

        // Through arm 1's end cap (pin 1) — top and bottom corners.
        (long a1x, long a1y) = twoAxis ? (junctionX, 0L) : (0L, 0L);
        // Through arm 2's end cap (pin 2).
        (long a2x, long a2y) = twoAxis ? (junctionX, 0L) : (throughEnd, 0L);
        // The branch tip. The branch runs along -Y, so its width is measured across X.
        (long a3x, long a3y) = twoAxis ? (junctionX, 0L) : (junctionX, -stub3);

        var handles = new[]
        {
            new PCellHandle("W1", a1x, a1y, 0,  w1 / 2, AxisDeg: 90,  Cross: Length("L1", w3 / 2), KeepAnchorFixed: twoAxis, Quantity: len),
            new PCellHandle("W1", a1x, a1y, 0, -w1 / 2, AxisDeg: 270, Cross: Length("L1", w3 / 2), KeepAnchorFixed: twoAxis, Quantity: len),

            new PCellHandle("W2", a2x, a2y, throughEnd,  w2 / 2, AxisDeg: 90,  Cross: Length("L2", w3 / 2), KeepAnchorFixed: twoAxis, Quantity: len),
            new PCellHandle("W2", a2x, a2y, throughEnd, -w2 / 2, AxisDeg: 270, Cross: Length("L2", w3 / 2), KeepAnchorFixed: twoAxis, Quantity: len),

            new PCellHandle("W3", a3x, a3y, junctionX + w3 / 2, -stub3, AxisDeg: 0,   Cross: Length("L3", Math.Max(w1, w2) / 2), KeepAnchorFixed: twoAxis, Quantity: len),
            new PCellHandle("W3", a3x, a3y, junctionX - w3 / 2, -stub3, AxisDeg: 180, Cross: Length("L3", Math.Max(w1, w2) / 2), KeepAnchorFixed: twoAxis, Quantity: len),
        };

        return new PCellResult([merged], pins,
            Diagnostics: diagnostics.Count > 0 ? diagnostics : null, Handles: handles);
    }
}

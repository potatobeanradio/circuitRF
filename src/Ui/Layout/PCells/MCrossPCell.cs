namespace CircuitRF.Ui.Layout.PCells;

/// <summary>
/// MCross artwork (brief-L5a-pcell-contract-and-microstrip.md §3): four arms (<c>W1</c>-<c>W4</c>)
/// unioned at the centre. R-pc-3: for a symmetric junction the origin is the centre, arm 1 along
/// +X — arms 2/3/4 follow at 90/180/270° (CCW), the standard 4-way cross. Geometric drawing does
/// not require <c>W1==W3</c>/<c>W2==W4</c> — that constraint (microstrip-models.md R11: the
/// published cross-junction ELECTRICAL models require opposing arms equal, so an asymmetric cross
/// is handled by reporting the opposing-mean approximation) belongs to <c>MicrostripCrossModel</c>
/// (<c>src/Core/Devices/</c>), not to drawing the artwork.
///
/// <b>Each arm carries its own drawn length (<c>L1</c>-<c>L4</c>), independent of its width</b> —
/// same owner report and same reasoning as <see cref="MTeePCell"/>: the old <c>2.5 ×</c> width
/// derivation made a width gripper move that arm's own pin along the arm. A cell that declares no
/// length still gets the old derivation, so nothing already drawn moves. The arms are ARTWORK;
/// <c>MicrostripCrossModel</c> has no length term.
/// </summary>
public static class MCrossPCell
{
    public const string GeneratorId = "MCROSS";

    public static PCellResult Generate(
        IReadOnlyDictionary<string, PCellValue> parameters,
        Technology? technology,
        PCellLayerSelection layerSelection)
    {
        double w1Meters = parameters.Real("W1", 0.0);
        double w2Meters = parameters.Real("W2", 0.0);
        double w3Meters = parameters.Real("W3", 0.0);
        double w4Meters = parameters.Real("W4", 0.0);

        // L1-L4 — each arm's own drawn length, independent of its width. NOT DECLARED at all (an older
        // cell) falls back to the legacy StubLengthFactor×width derivation, so nothing already on disk
        // moves. Same owner report as MTee: the derived rule made a WIDTH gripper walk that arm's own
        // pin along the arm, i.e. perpendicular to what was being dragged.
        double? l1Meters = PCellGeometryHelpers.Declared(parameters, "L1");
        double? l2Meters = PCellGeometryHelpers.Declared(parameters, "L2");
        double? l3Meters = PCellGeometryHelpers.Declared(parameters, "L3");
        double? l4Meters = PCellGeometryHelpers.Declared(parameters, "L4");

        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        long w1 = PCellUnits.MetresToDbu(w1Meters, dbuPerMicron);
        long w2 = PCellUnits.MetresToDbu(w2Meters, dbuPerMicron);
        long w3 = PCellUnits.MetresToDbu(w3Meters, dbuPerMicron);
        long w4 = PCellUnits.MetresToDbu(w4Meters, dbuPerMicron);

        long stub1 = PCellGeometryHelpers.ResolveArmLength(l1Meters, w1, dbuPerMicron);
        long stub2 = PCellGeometryHelpers.ResolveArmLength(l2Meters, w2, dbuPerMicron);
        long stub3 = PCellGeometryHelpers.ResolveArmLength(l3Meters, w3, dbuPerMicron);
        long stub4 = PCellGeometryHelpers.ResolveArmLength(l4Meters, w4, dbuPerMicron);

        // An explicit arm length shorter than half the PERPENDICULAR pair's width leaves that pair
        // overhanging this arm's own end cap. Clamped and reported; the derived path is deliberately
        // left alone so an older cell reproduces exactly, and so is a NEGATIVE one — a length grip
        // cannot produce one (its Min below is exactly this minimum), so the only ways to reach it
        // are a hand-typed number, a script or an older file, where reporting beats forbidding.
        var diagnostics = new List<string>();
        long halfCrossX = Math.Max(w2, w4) / 2;   // the ±Y arms cross the ±X arms
        long halfCrossY = Math.Max(w1, w3) / 2;   // and vice versa
        if (l1Meters is not null && stub1 > 0) stub1 = PCellGeometryHelpers.ClampArmLength(stub1, halfCrossX, GeneratorId, "L1", "the ±Y arms' width", diagnostics);
        if (l2Meters is not null && stub2 > 0) stub2 = PCellGeometryHelpers.ClampArmLength(stub2, halfCrossY, GeneratorId, "L2", "the ±X arms' width", diagnostics);
        if (l3Meters is not null && stub3 > 0) stub3 = PCellGeometryHelpers.ClampArmLength(stub3, halfCrossX, GeneratorId, "L3", "the ±Y arms' width", diagnostics);
        if (l4Meters is not null && stub4 > 0) stub4 = PCellGeometryHelpers.ClampArmLength(stub4, halfCrossY, GeneratorId, "L4", "the ±X arms' width", diagnostics);

        var signalLayer = SubstrateResolver.ResolveSignalLayerKey(technology, layerSelection, out _);

        var arm1 = PCellGeometryHelpers.BuildArmRect(0, 0,   0.0, stub1, w1, signalLayer);
        var arm2 = PCellGeometryHelpers.BuildArmRect(0, 0,  90.0, stub2, w2, signalLayer);
        var arm3 = PCellGeometryHelpers.BuildArmRect(0, 0, 180.0, stub3, w3, signalLayer);
        var arm4 = PCellGeometryHelpers.BuildArmRect(0, 0, 270.0, stub4, w4, signalLayer);

        var merged = PCellGeometryHelpers.UnionArms([arm1, arm2, arm3, arm4], signalLayer, technology);

        var pins = new[]
        {
            new PCellPin("1",  stub1,      0, signalLayer, w1,   0.0),
            new PCellPin("2",      0,  stub2, signalLayer, w2,  90.0),
            new PCellPin("3", -stub3,      0, signalLayer, w3, 180.0),
            new PCellPin("4",      0, -stub4, signalLayer, w4, 270.0),
        };

        // pcell-parameter-handles.md — EIGHT grips, ONE AT EACH CORNER OF THE METAL. A cross's outline
        // has eight outward corners: two at each arm's own end cap. (The four where adjacent arms meet
        // are re-entrant — inward folds, not something to grab.)
        //
        // Each grip drives BOTH of its own arm's parameters, by which way it is dragged: across the
        // arm sets that arm's WIDTH (the ±X arms are edited vertically, the ±Y arms horizontally),
        // along the arm sets its LENGTH. R-pch-4a's orthogonal decomposition — see MTeePCell for the
        // full reasoning, which is identical here.
        //
        // The anchor moves from each arm's own end-cap centre to the cross's CENTRE, which is what
        // makes the along-axis projection read as that arm's length. Every anchor keeps the coordinate
        // its width is measured on (y = 0 for the ±X arms, x = 0 for the ±Y arms), so a width drag
        // measures exactly what it always did. The centre IS the cell origin and therefore never moves,
        // so KeepAnchorFixed is a documented no-op here — unlike MTee, whose pin 1 is the origin and
        // whose junction consequently travels; it is set anyway for uniformity across the junction
        // cells, per PCellHandle's own note.
        //
        // A cell that does not declare all four lengths keeps exactly the grips it always had — see
        // MTeePCell for why that matters for both the cross axis and the pinning, and for why the gate
        // is DECLARED rather than positive.
        const PCellHandleQuantity len = PCellHandleQuantity.Length;
        bool twoAxis = l1Meters is not null && l2Meters is not null
                       && l3Meters is not null && l4Meters is not null;

        // Bounded on the LENGTH axis only, at exactly the minimum the crossing-width clamp above
        // enforces — see MTeePCell for the full reasoning, including why the width axis is
        // deliberately left free and why Min on the handle is safe where a generator clamp is not.
        double MinLength(long halfCrossDbu) =>
            PCellUnits.DbuToMetres(Math.Max(halfCrossDbu, 1), dbuPerMicron);

        PCellHandleCrossAxis? Length(string name, long halfCrossDbu) =>
            twoAxis ? new PCellHandleCrossAxis(name, Min: MinLength(halfCrossDbu), Quantity: len) : null;

        (long a1x, long a1y) = twoAxis ? (0L, 0L) : (stub1, 0L);
        (long a2x, long a2y) = twoAxis ? (0L, 0L) : (0L, stub2);
        (long a3x, long a3y) = twoAxis ? (0L, 0L) : (-stub3, 0L);
        (long a4x, long a4y) = twoAxis ? (0L, 0L) : (0L, -stub4);

        var handles = new[]
        {
            // Arm 1 (+X) end cap.
            new PCellHandle("W1", a1x, a1y,  stub1,  w1 / 2, AxisDeg: 90,  Cross: Length("L1", halfCrossX), KeepAnchorFixed: twoAxis, Quantity: len),
            new PCellHandle("W1", a1x, a1y,  stub1, -w1 / 2, AxisDeg: 270, Cross: Length("L1", halfCrossX), KeepAnchorFixed: twoAxis, Quantity: len),
            // Arm 2 (+Y) end cap.
            new PCellHandle("W2", a2x, a2y,  w2 / 2,  stub2, AxisDeg: 0,   Cross: Length("L2", halfCrossY), KeepAnchorFixed: twoAxis, Quantity: len),
            new PCellHandle("W2", a2x, a2y, -w2 / 2,  stub2, AxisDeg: 180, Cross: Length("L2", halfCrossY), KeepAnchorFixed: twoAxis, Quantity: len),
            // Arm 3 (-X) end cap.
            new PCellHandle("W3", a3x, a3y, -stub3,  w3 / 2, AxisDeg: 90,  Cross: Length("L3", halfCrossX), KeepAnchorFixed: twoAxis, Quantity: len),
            new PCellHandle("W3", a3x, a3y, -stub3, -w3 / 2, AxisDeg: 270, Cross: Length("L3", halfCrossX), KeepAnchorFixed: twoAxis, Quantity: len),
            // Arm 4 (-Y) end cap.
            new PCellHandle("W4", a4x, a4y,  w4 / 2, -stub4, AxisDeg: 0,   Cross: Length("L4", halfCrossY), KeepAnchorFixed: twoAxis, Quantity: len),
            new PCellHandle("W4", a4x, a4y, -w4 / 2, -stub4, AxisDeg: 180, Cross: Length("L4", halfCrossY), KeepAnchorFixed: twoAxis, Quantity: len),
        };

        return new PCellResult([merged], pins,
            Diagnostics: diagnostics.Count > 0 ? diagnostics : null, Handles: handles);
    }
}

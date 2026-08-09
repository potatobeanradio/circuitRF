namespace CircuitRF.Ui.Layout.PCells;

/// <summary>
/// MTaper artwork (brief-mtaper-mklopf.md §1): a trapezoid whose width varies linearly from
/// <c>W1</c> at pin 1 to <c>W2</c> at pin 2 over length <c>L</c>. R-pc-3: pin 1 at the origin, the
/// taper running to +X.
///
/// <b>R-tap-2: no tessellation parameter exists here, deliberately.</b> A linear width profile's
/// outline is an EXACT 4-vertex polygon (two parallel end-caps joined by two straight slanted
/// edges) — there is no curve to approximate, so there is nothing to couple to the electrical
/// section count (<c>MicrostripTaperModel</c>/<c>MicrostripCascadeSectioning</c>, in
/// <c>src/Core/Devices/</c>) even by accident.
/// </summary>
public static class MTaperPCell
{
    public const string GeneratorId = "MTAPER";

    public static PCellResult Generate(
        IReadOnlyDictionary<string, PCellValue> parameters,
        Technology? technology,
        PCellLayerSelection layerSelection)
    {
        double w1Meters = parameters.Real("W1", 0.0);
        double w2Meters = parameters.Real("W2", 0.0);
        double lMeters = parameters.Real("L", 0.0);

        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        long w1 = PCellUnits.MetresToDbu(w1Meters, dbuPerMicron);
        long w2 = PCellUnits.MetresToDbu(w2Meters, dbuPerMicron);
        long l = PCellUnits.MetresToDbu(lMeters, dbuPerMicron);

        var signalLayer = SubstrateResolver.ResolveSignalLayerKey(technology, layerSelection, out _);

        var trapezoid = new PolygonShape
        {
            Layer = signalLayer,
            Xy = [0, -w1 / 2, 0, w1 / 2, l, w2 / 2, l, -w2 / 2],
        };

        var pins = new[]
        {
            new PCellPin("1", 0, 0, signalLayer, w1, 180.0),
            new PCellPin("2", l, 0, signalLayer, w2,   0.0),
        };

        // pcell-parameter-handles.md §2.2 — SIX grips: the length from either end, and each of the
        // two INDEPENDENT widths from either side of its own end cap.
        //
        // Both width grips at an end anchor on the CENTRELINE there (the trapezoid is centred on
        // y = 0), so each measures its own half-width, neither moves when the other end is dragged,
        // and dragging top or bottom grows the taper symmetrically about its axis — which is what
        // a centred trapezoid can actually do. (MLIN's edge grips anchor on the OPPOSITE edge
        // instead, because a straight line CAN hold one edge still by translating the instance; a
        // taper cannot, since holding one end's edge would drag the other end with it.)
        //
        // The left-hand length grip is the R-pch-4b case: `L` can only grow toward +X (R4 pins pin 1
        // at the origin), so dragging the near end leftward is expressed as "grow, and hold the far
        // end where it is" — the host translates the instance to keep the anchor put.
        const PCellHandleQuantity len = PCellHandleQuantity.Length;
        var handles = new[]
        {
            // Length, from the far end (anchor = pin 1, which never moves) and from the near end.
            new PCellHandle("L",  0, 0, l, 0, AxisDeg: 0,   Quantity: len),
            new PCellHandle("L",  l, 0, 0, 0, AxisDeg: 180, KeepAnchorFixed: true, Quantity: len),
            // W1 — the near end cap, both sides.
            new PCellHandle("W1", 0, 0, 0,  w1 / 2, AxisDeg: 90,  Quantity: len),
            new PCellHandle("W1", 0, 0, 0, -w1 / 2, AxisDeg: 270, Quantity: len),
            // W2 — the far end cap, both sides.
            new PCellHandle("W2", l, 0, l,  w2 / 2, AxisDeg: 90,  Quantity: len),
            new PCellHandle("W2", l, 0, l, -w2 / 2, AxisDeg: 270, Quantity: len),
        };

        return new PCellResult([trapezoid], pins, Handles: handles);
    }
}

namespace CircuitRF.Ui.Layout.PCells;

/// <summary>
/// MLIN artwork (brief-L5a-pcell-contract-and-microstrip.md §3): a straight microstrip line of
/// width <c>W</c> and length <c>L</c>, both in SI metres per R-pc-6. R-pc-3: pin 1 at the origin,
/// the line running to +X.
/// </summary>
public static class MlinPCell
{
    public const string GeneratorId = "MLIN";

    public static PCellResult Generate(
        IReadOnlyDictionary<string, PCellValue> parameters,
        Technology? technology,
        PCellLayerSelection layerSelection)
    {
        double wMeters = parameters.Real("W", 0.0);
        double lMeters = parameters.Real("L", 0.0);

        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        long w = PCellUnits.MetresToDbu(wMeters, dbuPerMicron);
        long l = PCellUnits.MetresToDbu(lMeters, dbuPerMicron);

        var signalLayer = SubstrateResolver.ResolveSignalLayerKey(technology, layerSelection, out _);

        var line = new RectShape
        {
            Layer = signalLayer,
            X1 = 0, Y1 = -w / 2,
            X2 = l,  Y2 =  w / 2,
        };

        var pins = new[]
        {
            new PCellPin("1", 0, 0, signalLayer, w, 180.0),
            new PCellPin("2", l, 0, signalLayer, w,   0.0),
        };

        // pcell-parameter-handles.md — ONE GRIP PER EDGE MIDPOINT, each anchored on the OPPOSITE
        // edge with KeepAnchorFixed. That pairing is the whole design: the projection from the far
        // edge to the near one IS the dimension (so dragging the top edge by 1 mm changes W by
        // 1 mm, not 2), and pinning the anchor makes the opposite edge hold still in world space
        // while this one moves — which is what grabbing an edge means everywhere else in the
        // editor.
        //
        // Without the pin, dragging the LEFT edge would grow the line to the RIGHT: a generator
        // cannot move its own origin (R4 puts pin 1 at (0,0)), so the host translates the instance
        // instead. On the right and top edges the anchor happens not to move, so the flag is a
        // no-op there — set anyway, because a set of four edge grips that behaved as four different
        // rules would be worse than one that reads uniformly.
        long midX = l / 2, halfW = w / 2;
        const PCellHandleQuantity len = PCellHandleQuantity.Length;
        var handles = new[]
        {
            // Right edge — anchored on the left edge (pin 1), which never moves anyway.
            new PCellHandle("L", 0, 0, l, 0, AxisDeg: 0, KeepAnchorFixed: true, Quantity: len),
            // Left edge — anchored on the right edge, which the host holds still.
            new PCellHandle("L", l, 0, 0, 0, AxisDeg: 180, KeepAnchorFixed: true, Quantity: len),
            // Top edge — anchored on the bottom edge.
            new PCellHandle("W", midX, -halfW, midX, halfW, AxisDeg: 90, KeepAnchorFixed: true, Quantity: len),
            // Bottom edge — anchored on the top edge.
            new PCellHandle("W", midX, halfW, midX, -halfW, AxisDeg: 270, KeepAnchorFixed: true, Quantity: len),
        };

        return new PCellResult([line], pins, Handles: handles);
    }
}

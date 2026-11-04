namespace CircuitRF.Design.Layout.PCells;

/// <summary>
/// MGap artwork (brief-sdl-9 D2): the end-to-end gap between two lines of width <c>W</c>, <c>S</c> apart.
/// <b>It draws no copper.</b> Its layout is its two pins and nothing else — pin 1 at the origin facing −X,
/// pin 2 at <c>(S, 0)</c> facing +X, each <c>W</c> wide on the line's layer. The lines either side are the
/// metal: placement puts each line's end on a pin, so the gap between the two ends is exactly <c>S</c> by
/// construction, with nothing in the placer that knows a gap exists. The pins are on different nets, so
/// LVS and the connectivity signature read a designed gap, not a broken joint.
///
/// <para><c>S ≤ 0</c> is drawn as nothing at all apart from the two pins at the origin and reported:
/// the two lines would touch, and the simulation refuses it.</para>
/// </summary>
public static class MGapPCell
{
    public const string GeneratorId = "MGAP";

    public static PCellResult Generate(
        IReadOnlyDictionary<string, PCellValue> parameters,
        Technology? technology,
        PCellLayerSelection layerSelection)
    {
        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        long w = PCellUnits.MetresToDbu(parameters.Real("W", 0.0), dbuPerMicron);
        long s = PCellUnits.MetresToDbu(parameters.Real("S", 0.0), dbuPerMicron);
        var signalLayer = SubstrateResolver.ResolveSignalLayerKey(technology, layerSelection, out _);
        List<string>? diagnostics = null;

        if (s <= 0)
        {
            (diagnostics ??= []).Add("MGap: S must be greater than zero — the two lines would touch, which is not a gap, "
                                   + "and the simulation refuses it.");
            s = 0;
        }
        if (w <= 0) (diagnostics ??= []).Add("MGap: W must be positive.");

        var pins = new[]
        {
            new PCellPin("1", 0, 0, signalLayer, w, 180.0),
            new PCellPin("2", s, 0, signalLayer, w, 0.0),
        };

        // S at pin 2, along the line (the gesture that widens or closes the gap); W across pin 1's end.
        var handles = new[]
        {
            new PCellHandle("S", 0, 0, s, 0, AxisDeg: 0, KeepAnchorFixed: true, Quantity: PCellHandleQuantity.Length),
            new PCellHandle("W", 0, 0, 0, w / 2, AxisDeg: 90, Quantity: PCellHandleQuantity.Length),
        };

        return new PCellResult([], pins, diagnostics, Handles: handles);
    }
}

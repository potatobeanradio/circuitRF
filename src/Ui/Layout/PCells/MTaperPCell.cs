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
        IReadOnlyDictionary<string, double> parameters,
        Technology? technology,
        PCellLayerSelection layerSelection)
    {
        double w1Meters = parameters.GetValueOrDefault("W1", 0.0);
        double w2Meters = parameters.GetValueOrDefault("W2", 0.0);
        double lMeters = parameters.GetValueOrDefault("L", 0.0);

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

        return new PCellResult([trapezoid], pins);
    }
}

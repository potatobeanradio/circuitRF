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
        IReadOnlyDictionary<string, double> parameters,
        Technology? technology,
        PCellLayerSelection layerSelection)
    {
        double wMeters = parameters.GetValueOrDefault("W", 0.0);
        double lMeters = parameters.GetValueOrDefault("L", 0.0);

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

        return new PCellResult([line], pins);
    }
}

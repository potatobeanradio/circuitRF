namespace CircuitRF.Ui.Layout.PCells;

/// <summary>
/// MCross artwork (brief-L5a-pcell-contract-and-microstrip.md §3): four arms (<c>W1</c>-<c>W4</c>)
/// unioned at the centre. R-pc-3: for a symmetric junction the origin is the centre, arm 1 along
/// +X — arms 2/3/4 follow at 90/180/270° (CCW), the standard 4-way cross. Geometric drawing does
/// not require <c>W1==W3</c>/<c>W2==W4</c> — that constraint (microstrip-models.md R11: the
/// published cross-junction ELECTRICAL models require opposing arms equal, so an asymmetric cross
/// is handled by reporting the opposing-mean approximation) belongs to <c>MicrostripCrossModel</c>
/// (<c>src/Core/Devices/</c>), not to drawing the artwork.
/// </summary>
public static class MCrossPCell
{
    public const string GeneratorId = "MCROSS";

    public static PCellResult Generate(
        IReadOnlyDictionary<string, double> parameters,
        Technology? technology,
        PCellLayerSelection layerSelection)
    {
        double w1Meters = parameters.GetValueOrDefault("W1", 0.0);
        double w2Meters = parameters.GetValueOrDefault("W2", 0.0);
        double w3Meters = parameters.GetValueOrDefault("W3", 0.0);
        double w4Meters = parameters.GetValueOrDefault("W4", 0.0);

        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        long w1 = PCellUnits.MetresToDbu(w1Meters, dbuPerMicron);
        long w2 = PCellUnits.MetresToDbu(w2Meters, dbuPerMicron);
        long w3 = PCellUnits.MetresToDbu(w3Meters, dbuPerMicron);
        long w4 = PCellUnits.MetresToDbu(w4Meters, dbuPerMicron);

        long stub1 = (long)Math.Round(PCellGeometryHelpers.StubLengthFactor * w1, MidpointRounding.AwayFromZero);
        long stub2 = (long)Math.Round(PCellGeometryHelpers.StubLengthFactor * w2, MidpointRounding.AwayFromZero);
        long stub3 = (long)Math.Round(PCellGeometryHelpers.StubLengthFactor * w3, MidpointRounding.AwayFromZero);
        long stub4 = (long)Math.Round(PCellGeometryHelpers.StubLengthFactor * w4, MidpointRounding.AwayFromZero);

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

        return new PCellResult([merged], pins);
    }
}

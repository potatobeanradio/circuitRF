namespace CircuitRF.Ui.Layout.PCells;

/// <summary>
/// MTee artwork (brief-L5a-pcell-contract-and-microstrip.md §3): two collinear through arms
/// (widths <c>W1</c>, <c>W2</c> — may differ, "a tee whose through line steps width is entirely
/// ordinary") plus a perpendicular branch (<c>W3</c>), unioned at the junction. R-pc-3 + the
/// brief's own MTee convention: pin 1 at the origin, the through line running along +X to pin 2,
/// the branch along +Y to pin 3.
/// </summary>
public static class MTeePCell
{
    public const string GeneratorId = "MTEE";

    public static PCellResult Generate(
        IReadOnlyDictionary<string, double> parameters,
        Technology? technology,
        PCellLayerSelection layerSelection)
    {
        double w1Meters = parameters.GetValueOrDefault("W1", 0.0);
        double w2Meters = parameters.GetValueOrDefault("W2", 0.0);
        double w3Meters = parameters.GetValueOrDefault("W3", 0.0);

        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        long w1 = PCellUnits.MetresToDbu(w1Meters, dbuPerMicron);
        long w2 = PCellUnits.MetresToDbu(w2Meters, dbuPerMicron);
        long w3 = PCellUnits.MetresToDbu(w3Meters, dbuPerMicron);

        long stub1 = (long)Math.Round(PCellGeometryHelpers.StubLengthFactor * w1, MidpointRounding.AwayFromZero);
        long stub2 = (long)Math.Round(PCellGeometryHelpers.StubLengthFactor * w2, MidpointRounding.AwayFromZero);
        long stub3 = (long)Math.Round(PCellGeometryHelpers.StubLengthFactor * w3, MidpointRounding.AwayFromZero);

        var signalLayer = SubstrateResolver.ResolveSignalLayerKey(technology, layerSelection, out _);

        // Junction sits at the origin's own X — pin 1 at the far end of the first through arm, so
        // the junction (where the branch meets the through line) is at (stub1, 0); pin 2 sits
        // stub2 further along +X from there.
        long junctionX = stub1;

        var throughArm1 = PCellGeometryHelpers.BuildArmRect(0, 0, 0.0, stub1, w1, signalLayer);
        var throughArm2 = PCellGeometryHelpers.BuildArmRect(junctionX, 0, 0.0, stub2, w2, signalLayer);
        var branch = PCellGeometryHelpers.BuildArmRect(junctionX, 0, 90.0, stub3, w3, signalLayer);

        var merged = PCellGeometryHelpers.UnionArms([throughArm1, throughArm2, branch], signalLayer, technology);

        var pins = new[]
        {
            new PCellPin("1", 0, 0, signalLayer, w1, 180.0),
            new PCellPin("2", junctionX + stub2, 0, signalLayer, w2, 0.0),
            new PCellPin("3", junctionX, stub3, signalLayer, w3, 90.0),
        };

        return new PCellResult([merged], pins);
    }
}

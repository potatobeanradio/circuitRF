// Owner: PCD-3 (brief-pcd-3 R-pcd3-2). Framework-free.

namespace CircuitRF.Design.Layout.PCells;

/// <summary>
/// MSHAPE artwork (brief-pcd-0 §4.3): the outline its own parameters state, in the part's own frame. Form "polygon" is a
/// closed outline through X[i]/Y[i]; "path" is a centreline through them, W wide, flush at its ends. Pin k is at
/// PinX[k]/PinY[k], PinW[k] wide, facing PinDir[k] degrees. The shape is on the signal layer (SignalLayer, else the
/// technology's top conductor). A form it cannot draw is said, with the pins still placed.
/// </summary>
public static class MShapePCell
{
    public const string GeneratorId = DrawnMetal.GeneratorId;

    /// <summary>The placed defaults of brief-pcd-0 §4.3 in SI: a 100 um square right of the origin, pin 1 at its left edge's
    /// middle, as wide as that edge, facing -X (ComponentTypeRegistry.DefaultParameters).</summary>
    private static readonly Dictionary<string, PCellValue> PlacedDefaults = new(StringComparer.Ordinal)
    {
        ["X[1]"] = 0.0, ["Y[1]"] = -50e-6, ["X[2]"] = 100e-6, ["Y[2]"] = -50e-6,
        ["X[3]"] = 100e-6, ["Y[3]"] = 50e-6, ["X[4]"] = 0.0, ["Y[4]"] = 50e-6,
        ["PinX[1]"] = 0.0, ["PinY[1]"] = 0.0, ["PinW[1]"] = 100e-6, ["PinDir[1]"] = 180.0,
    };

    public static PCellResult Generate(
        IReadOnlyDictionary<string, PCellValue> parameters,
        Technology? technology,
        PCellLayerSelection layerSelection)
    {
        // Handed no vertex and no pin rows at all (a caller with no instance: a palette tile, a gate over the registry), it
        // draws the placed default part, so its pins agree with the symbol's default count.
        if (!parameters.ContainsKey(DrawnMetal.X(1)) && !parameters.ContainsKey(DrawnMetal.PinX(1)))
            parameters = new Dictionary<string, PCellValue>(PlacedDefaults.Concat(parameters.Where(kv => !PlacedDefaults.ContainsKey(kv.Key))),
                                                            StringComparer.Ordinal);
        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        long Dbu(string name) => PCellUnits.MetresToDbu(parameters.Real(name), dbuPerMicron);
        var layer = SubstrateResolver.ResolveSignalLayerKey(technology, layerSelection, out _);

        var pins = new List<PCellPin>();
        for (int k = 1; parameters.ContainsKey(DrawnMetal.PinX(k)); k++)
            pins.Add(new PCellPin($"{k}", Dbu(DrawnMetal.PinX(k)), Dbu(DrawnMetal.PinY(k)), layer,
                                  Dbu(DrawnMetal.PinW(k)), parameters.Real(DrawnMetal.PinDir(k), 180.0)));

        var xy = new List<long>();
        for (int i = 1; parameters.ContainsKey(DrawnMetal.X(i)) && parameters.ContainsKey(DrawnMetal.Y(i)); i++)
        {
            xy.Add(Dbu(DrawnMetal.X(i)));
            xy.Add(Dbu(DrawnMetal.Y(i)));
        }
        int n = xy.Count / 2;

        string form = parameters.Text(DrawnMetal.Form, DrawnMetal.FormPolygon).Trim().Trim('"').Trim();
        if (form.Equals(DrawnMetal.FormPath, StringComparison.OrdinalIgnoreCase))
        {
            long w = Dbu(DrawnMetal.Width);
            if (n < 2 || w <= 0)
                return new PCellResult([], pins, [$"A path needs two vertices and a width above zero; it has {n} and W = {w} dbu."]);
            return new PCellResult([new PathShape { Layer = layer, Xy = [.. xy], Width = w }], pins);
        }
        if (!form.Equals(DrawnMetal.FormPolygon, StringComparison.OrdinalIgnoreCase))
            return new PCellResult([], pins, [$"Form is \"{form}\"; it is \"{DrawnMetal.FormPolygon}\" or \"{DrawnMetal.FormPath}\"."]);
        if (n < 3)
            return new PCellResult([], pins, [$"A polygon needs three vertices; it has {n}."]);
        return new PCellResult([new PolygonShape { Layer = layer, Xy = [.. xy] }], pins);
    }
}

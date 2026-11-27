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

    public static PCellResult Generate(
        IReadOnlyDictionary<string, PCellValue> parameters,
        Technology? technology,
        PCellLayerSelection layerSelection)
    {
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

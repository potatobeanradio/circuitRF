using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;

namespace CircuitRF.Design.Layout.PCells;

/// <summary>
/// The planned passives' artwork (docs/design/rf-passives.md). A generator builds its part's plan in Core —
/// the plan its model stamps — and <see cref="Draw"/> draws every section through the EXISTING part's own
/// generator (<see cref="MTeePCell"/>, <see cref="MCurvePCell"/>), turned and moved to the section's pose. The
/// copper is therefore the copper those parts draw on their own, piece for piece, and the pins are those
/// parts' own pins wherever a section ends on one of the passive's pins — never a second rounding of where
/// they should be.
///
/// <para>The pieces abut, as the separate instances of a hand-drawn divider do, rather than being merged into
/// one polygon: the curves keep their true arcs (every consumer flattens them to its own tolerance), and a
/// ring merged into one outline would need a hole.</para>
/// </summary>
public static class PassivePlanPCells
{
    /// <summary>The equal-split Wilkinson divider (MWPD): <see cref="WilkinsonPlan"/>'s ring.</summary>
    public const string WilkinsonId = ComponentModelFactory.WilkinsonTypeName;

    public static PCellResult GenerateWilkinson(
        IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology, PCellLayerSelection layerSelection)
    {
        WilkinsonDesign design;
        try
        {
            design = WilkinsonPlan.Build(parameters.Real("Z0", 50.0), parameters.Real("F", 2.4e9),
                parameters.Real("Gap", 1.35e-3), SubstrateOf(technology, layerSelection));
        }
        catch (ParameterRefusalException ex)
        {
            return new PCellResult([], [], [ex.Message]);
        }
        return Draw(design.Plan, technology, layerSelection);
    }

    /// <summary>The substrate the model is given for the same part: the stackup's, or the factory's fallbacks.</summary>
    private static MicrostripSubstrate SubstrateOf(Technology? technology, PCellLayerSelection layerSelection)
    {
        var (s, _, _) = SubstrateResolver.ResolveElectrical(technology, layerSelection);
        return s is null
            ? new(ComponentModelFactory.DefaultSubstrateHMeters, ComponentModelFactory.DefaultSubstrateTMeters,
                  ComponentModelFactory.DefaultSubstrateEpsR, ComponentModelFactory.DefaultSubstrateSigmaSPerM,
                  ComponentModelFactory.DefaultSubstrateTanD)
            : new(s.HeightMeters, s.ThicknessMeters, s.RelativePermittivity, s.ConductivitySPerM, s.LossTangent,
                  s.RoughnessRmsMeters);
    }

    /// <summary>Draws <paramref name="plan"/>: each section by its own part's generator, posed.</summary>
    public static PCellResult Draw(PassivePlan plan, Technology? technology, PCellLayerSelection layerSelection)
    {
        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        var shapes = new List<LayoutShape>();
        var pins = new List<PCellPin>();
        List<string>? diagnostics = null;

        foreach (var section in plan.Sections)
        {
            var part = section switch
            {
                PlanTee t => MTeePCell.Generate(new Dictionary<string, PCellValue>
                {
                    ["W1"] = t.W1, ["W2"] = t.W2, ["W3"] = t.W3, ["L1"] = t.L1, ["L2"] = t.L2, ["L3"] = t.L3,
                }, technology, layerSelection),
                PlanCurve c => MCurvePCell.Generate(new Dictionary<string, PCellValue>
                {
                    ["W"] = c.W, ["Angle"] = c.AngleDeg, ["Radius"] = c.Radius,
                }, technology, layerSelection),
                _ => throw new NotSupportedException(section.GetType().Name),
            };

            int quarter = (int)Math.Round(section.RotationDeg / 90.0) & 3;
            long dx = PCellUnits.MetresToDbu(section.X, dbuPerMicron), dy = PCellUnits.MetresToDbu(section.Y, dbuPerMicron);
            (long X, long Y) Pose(long x, long y)
            {
                var (rx, ry) = quarter switch { 1 => (-y, x), 2 => (-x, -y), 3 => (y, -x), _ => (x, y) };
                return (rx + dx, ry + dy);
            }

            var walk = new LayoutCoordinateTransform(Pose, m => m);
            foreach (var shape in part.Shapes)
            {
                LayoutCoordinateWalk.Transform(shape, walk);
                shapes.Add(shape);
            }
            for (int j = 0; j < part.Pins.Count && j < section.Nodes.Length; j++)
            {
                int node = section.Nodes[j];
                if (node < 1 || node > plan.Pins.Count) continue;
                var p = part.Pins[j];
                var (x, y) = Pose(p.X, p.Y);
                pins.Add(p with { Name = plan.Pins[node - 1], X = x, Y = y,
                                  OutwardDirectionDeg = (p.OutwardDirectionDeg + 90.0 * quarter) % 360.0 });
            }
            if (part.Diagnostics is { Count: > 0 } d) (diagnostics ??= []).AddRange(d);
        }

        // The pins in the plan's own order, which is the part's terminal order.
        var order = plan.Pins.ToList();
        return new PCellResult(shapes, [.. pins.OrderBy(p => order.IndexOf(p.Name))], diagnostics);
    }
}

using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;

namespace CircuitRF.Design.Layout.PCells;

/// <summary>
/// The planned passives' artwork (docs/design/rf-passives.md). A generator builds its part's plan in Core —
/// the plan its model stamps — and <see cref="Draw"/> draws every section through the EXISTING part's own
/// generator (<see cref="MTeePCell"/>, <see cref="MCurvePCell"/>, <see cref="MlinPCell"/>,
/// <see cref="CoupledLinePCells"/>, <see cref="MMiterPCell"/>, <see cref="MBendPCell"/>; an interdigitated section's
/// strips each by <see cref="MlinPCell"/>), turned and
/// moved to the section's pose. The
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
    /// <summary>The Wilkinson divider (MWPD): <see cref="WilkinsonPlan"/>'s ring, and its sections and transformers.</summary>
    public const string WilkinsonId = ComponentModelFactory.WilkinsonTypeName;

    public static PCellResult GenerateWilkinson(
        IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology, PCellLayerSelection layerSelection)
    {
        WilkinsonDesign design;
        try
        {
            design = WilkinsonPlan.Build(parameters.Real("Z0", 50.0), parameters.Real("F", 2.4e9),
                parameters.Real("Gap", 1.35e-3), SubstrateOf(technology, layerSelection),
                parameters.Real("K", 1.0), parameters.Real("N", 1.0), parameters.Real("Band", WilkinsonPlan.DefaultBand),
                CornerOf(parameters));
        }
        catch (ParameterRefusalException ex)
        {
            return new PCellResult([], [], [ex.Message]);
        }
        return Draw(design.Plan, technology, layerSelection);
    }

    /// <summary>The 3 dB branch-line hybrid (MBLC): <see cref="BranchLinePlan"/>'s one or two sections.</summary>
    public const string BranchLineId = ComponentModelFactory.BranchLineTypeName;

    public static PCellResult GenerateBranchLine(
        IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology, PCellLayerSelection layerSelection)
        => DrawOrRefuse(() => BranchLinePlan.Build(parameters.Real("Z0", 50.0), parameters.Real("F", 2.4e9),
            parameters.Real("N", 1.0), SubstrateOf(technology, layerSelection)).Plan, technology, layerSelection);

    /// <summary>The rat-race hybrid (MRRC): <see cref="RatRacePlan"/>'s ring.</summary>
    public const string RatRaceId = ComponentModelFactory.RatRaceTypeName;

    public static PCellResult GenerateRatRace(
        IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology, PCellLayerSelection layerSelection)
        => DrawOrRefuse(() => RatRacePlan.Build(parameters.Real("Z0", 50.0), parameters.Real("F", 2.4e9),
            SubstrateOf(technology, layerSelection), CornerOf(parameters)).Plan, technology, layerSelection);

    /// <summary>A ring's <c>Corner</c>, a name; Curve when it is absent or not one of the names.</summary>
    private static PlanCorner CornerOf(IReadOnlyDictionary<string, PCellValue> parameters)
        => Enum.TryParse(parameters.Text("Corner"), ignoreCase: true, out PlanCorner c) && Enum.IsDefined(c) ? c : PlanCorner.Curve;

    /// <summary>The coupled-line coupler (MCLC): <see cref="CoupledLineCouplerPlan"/>'s one MCLIN.</summary>
    public const string CoupledLineCouplerId = ComponentModelFactory.CoupledLineCouplerTypeName;

    /// <summary>The four-finger interdigitated coupler (MIDC): <see cref="LangePlan"/>'s fingers.</summary>
    public const string LangeId = ComponentModelFactory.LangeTypeName;

    /// <summary>The coupled-line balun (MCBL): <see cref="MarchandBalunPlan"/>'s two sections and their joining line.</summary>
    public const string MarchandId = ComponentModelFactory.MarchandTypeName;

    public static PCellResult GenerateCoupledLineCoupler(
        IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology, PCellLayerSelection layerSelection)
    {
        double broadside = parameters.Real("Broadside", 0.0);
        var stack = broadside != 0.0
            ? SubstrateResolver.ResolveBroadsideStack(technology, layerSelection.SignalLayerNameOverride).Stack
            : null;
        return DrawOrRefuse(() => CoupledLineCouplerPlan.Build(parameters.Real("Coupling", 10.0), parameters.Real("Z0", 50.0),
            parameters.Real("F", 2.4e9), SubstrateOf(technology, layerSelection), parameters.Real("Sections", 1.0),
            parameters.Real("Band", CoupledLineCouplerPlan.DefaultBand), parameters.Real("Tandem", 0.0),
            EnumOf(parameters, "Compensation", CouplerCompensation.None), broadside, stack).Plan, technology, layerSelection);
    }

    /// <summary>An enum-named parameter, a name; <paramref name="fallback"/> when absent or not one of the names.</summary>
    private static T EnumOf<T>(IReadOnlyDictionary<string, PCellValue> parameters, string name, T fallback) where T : struct, Enum
        => Enum.TryParse(parameters.Text(name), ignoreCase: true, out T v) && Enum.IsDefined(v) ? v : fallback;

    public static PCellResult GenerateLange(
        IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology, PCellLayerSelection layerSelection)
        => DrawOrRefuse(() => LangePlan.Build(parameters.Real("Coupling", 4.0), parameters.Real("Z0", 50.0),
            parameters.Real("F", 2.4e9), SubstrateOf(technology, layerSelection),
            parameters.Real("Fingers", LangePlan.Fingers)).Plan, technology, layerSelection);

    public static PCellResult GenerateMarchand(
        IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology, PCellLayerSelection layerSelection)
        => DrawOrRefuse(() => MarchandBalunPlan.Build(parameters.Real("Z0", 50.0), parameters.Real("ZL", 200.0),
            parameters.Real("F", 2.4e9), parameters.Real("Gap", 1e-3), SubstrateOf(technology, layerSelection)).Plan,
            technology, layerSelection);

    /// <summary>The Schiffman phase shifter (MSPS): <see cref="SchiffmanPlan"/>'s C-section and reference line.</summary>
    public const string SchiffmanId = ComponentModelFactory.SchiffmanTypeName;

    public static PCellResult GenerateSchiffman(
        IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology, PCellLayerSelection layerSelection)
        => DrawOrRefuse(() => SchiffmanPlan.Build(parameters.Real("Z0", 50.0), parameters.Real("F", 2.4e9),
            parameters.Real("Phase", 90.0), parameters.Real("Band", SchiffmanPlan.DefaultBand),
            SubstrateOf(technology, layerSelection), EnumOf(parameters, "Reference", SchiffmanReference.Straight)).Plan,
            technology, layerSelection);

    /// <summary>The plan drawn, or its refusal as the cell's one diagnostic.</summary>
    private static PCellResult DrawOrRefuse(Func<PassivePlan> plan, Technology? technology, PCellLayerSelection layerSelection)
    {
        try { return Draw(plan(), technology, layerSelection); }
        catch (ParameterRefusalException ex) { return new PCellResult([], [], [ex.Message]); }
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
                PlanLine l => MlinPCell.Generate(new Dictionary<string, PCellValue> { ["W"] = l.W, ["L"] = l.L },
                    technology, layerSelection),
                PlanCoupledLine { Fingers: 2, Wiggly: true } c => Wiggly(c, technology, layerSelection),
                PlanCoupledLine { Fingers: 2 } c => CoupledLinePCells.GenerateMclin(new Dictionary<string, PCellValue>
                {
                    ["W"] = c.W, ["S"] = c.S, ["L"] = c.L,
                }, technology, layerSelection),
                PlanBroadsideLine b => (b.Stack.Stripline ? (Func<IReadOnlyDictionary<string, PCellValue>, Technology?,
                        PCellLayerSelection, PCellResult>)CoupledLinePCells.GenerateBclin : CoupledLinePCells.GenerateMbclin)(
                    new Dictionary<string, PCellValue>
                    {
                        ["W"] = b.W, ["O"] = b.O, ["L"] = b.L,
                        ["LayerA"] = PCellValue.Text(b.Stack.LayerA ?? ""), ["LayerB"] = PCellValue.Text(b.Stack.LayerB ?? ""),
                    }, technology, layerSelection),
                PlanCoupledLine c => Fingers(c, technology, layerSelection),
                PlanBend b => b.M is { } m
                    ? MMiterPCell.Generate(new Dictionary<string, PCellValue>
                    {
                        ["W"] = b.W, ["Angle"] = b.AngleDeg, ["M"] = m,
                    }, technology, layerSelection)
                    : MBendPCell.Generate(new Dictionary<string, PCellValue>
                    {
                        ["W"] = b.W, ["Angle"] = b.AngleDeg, ["Miter"] = (double)MicrostripBendMiter.None,
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

    /// <summary>An interdigitated section: each strip an MLIN, strip k centred k·(W + S) below the first as MCLIN places
    /// its second, with the strips' pins in the section's node order (each strip's near end, then its far end).</summary>
    private static PCellResult Fingers(PlanCoupledLine c, Technology? technology, PCellLayerSelection layerSelection)
    {
        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        long pitch = PCellUnits.MetresToDbu(c.W, dbuPerMicron) + PCellUnits.MetresToDbu(c.S, dbuPerMicron);
        var shapes = new List<LayoutShape>();
        var pins = new List<PCellPin>();
        List<string>? diagnostics = null;
        for (int k = 0; k < c.Fingers; k++)
        {
            var strip = MlinPCell.Generate(new Dictionary<string, PCellValue> { ["W"] = c.W, ["L"] = c.L },
                technology, layerSelection);
            long dy = -k * pitch;
            var down = new LayoutCoordinateTransform((x, y) => (x, y + dy), m => m);
            foreach (var shape in strip.Shapes)
            {
                LayoutCoordinateWalk.Transform(shape, down);
                shapes.Add(shape);
            }
            pins.AddRange(strip.Pins.Select(p => p with { Y = p.Y + dy }));
            if (k == 0 && strip.Diagnostics is { Count: > 0 } d) diagnostics = [.. d];
        }
        return new PCellResult(shapes, pins, diagnostics);
    }

    /// <summary>A wiggly pair (A. Podell, 1970): strip A from y = W/2 down to a triangle-wave edge about y = −W/2, strip B's
    /// matching saw-tooth <see cref="PlanCoupledLine.WiggleGapOffset"/> lower and its straight edge W below that, each tooth
    /// <see cref="PlanCoupledLine.WigglePitch"/> long and <see cref="PlanCoupledLine.WiggleDepth"/> deep; pins as MCLIN's,
    /// at each strip's mean centre line.</summary>
    private static PCellResult Wiggly(PlanCoupledLine c, Technology? technology, PCellLayerSelection layerSelection)
    {
        int dbu = LayoutUnits.DefaultDbuPerMicron;
        long M(double m) => PCellUnits.MetresToDbu(m, dbu);
        var layer = SubstrateResolver.ResolveSignalLayerKey(technology, layerSelection, out _);
        long w = M(c.W), l = M(c.L), off = M(c.WiggleGapOffset), half = M(c.WiggleDepth / 2.0);
        int teeth = (int)Math.Round(c.L / c.WigglePitch);
        long hw = w / 2, yb = -(w + off);
        // The saw-tooth about y = 0, from x = 0 to l: on the mean edge at each end and every half tooth, up at each first
        // quarter and down at each third, so the strips' end faces are centred on their pins as MCLIN's are.
        var edge = Enumerable.Range(0, 4 * teeth + 1)
            .Select(i => (X: (long)Math.Round((double)l * i / (4 * teeth)), Y: (i % 4) switch { 1 => half, 3 => -half, _ => 0L }))
            .ToList();
        IEnumerable<long> Flat(IEnumerable<(long X, long Y)> pts) => pts.SelectMany(p => new[] { p.X, p.Y });
        var a = new PolygonShape
        {
            Layer = layer,
            Xy = [0, w - hw, .. Flat(edge.Select(p => (p.X, p.Y - hw))), l, w - hw],
        };
        // Strip B: its toothed top edge right to left, then its straight bottom edge.
        var b = new PolygonShape
        {
            Layer = layer,
            Xy = [.. Flat(Enumerable.Reverse(edge).Select(p => (p.X, p.Y - hw - off))), 0, yb - hw, l, yb - hw],
        };
        var pins = new[]
        {
            new PCellPin("1", 0, 0, layer, w, 180.0),
            new PCellPin("2", l, 0, layer, w, 0.0),
            new PCellPin("3", 0, yb, layer, w, 180.0),
            new PCellPin("4", l, yb, layer, w, 0.0),
        };
        return new PCellResult([a, b], pins);
    }
}

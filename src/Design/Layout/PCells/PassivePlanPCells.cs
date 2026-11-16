using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;

namespace CircuitRF.Design.Layout.PCells;

/// <summary>
/// The planned passives' artwork (docs/design/rf-passives.md). A generator builds its part's plan in Core —
/// the plan its model stamps — and <see cref="Draw"/> draws every section through the EXISTING part's own
/// generator (<see cref="MTeePCell"/>, <see cref="MCurvePCell"/>, <see cref="MlinPCell"/>,
/// <see cref="CoupledLinePCells"/>, <see cref="MMiterPCell"/>, <see cref="MBendPCell"/>, <see cref="MGapPCell"/>, <see cref="MCrossPCell"/>,
/// <see cref="MTaperPCell"/>, <see cref="MRadialPCell"/>; an interdigitated section's strips, and an N-line section's,
/// each by <see cref="MlinPCell"/>; an open end draws nothing), turned and
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
        => DrawOrRefuse(() => Lumped(parameters)
            ? LumpedPlan.Wilkinson(parameters.Real("Z0", 50.0), parameters.Real("F", 2.4e9), parameters.Real("Gap", 1.35e-3),
                SubstrateOf(technology, layerSelection), Word(parameters, "Arm", LumpedArm.Pi), parameters.Real("K", 1.0),
                parameters.Real("N", 1.0)).Plan
            : WilkinsonPlan.Build(parameters.Real("Z0", 50.0), parameters.Real("F", 2.4e9),
                parameters.Real("Gap", 1.35e-3), SubstrateOf(technology, layerSelection),
                parameters.Real("K", 1.0), parameters.Real("N", 1.0), parameters.Real("Band", WilkinsonPlan.DefaultBand),
                CornerOf(parameters)).Plan, technology, layerSelection);

    /// <summary>The 3 dB branch-line hybrid (MBLC): <see cref="BranchLinePlan"/>'s one or two sections.</summary>
    public const string BranchLineId = ComponentModelFactory.BranchLineTypeName;

    public static PCellResult GenerateBranchLine(
        IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology, PCellLayerSelection layerSelection)
        => DrawOrRefuse(() => Lumped(parameters)
            ? LumpedPlan.BranchLine(parameters.Real("Z0", 50.0), parameters.Real("F", 2.4e9), parameters.Real("Gap", 1.35e-3),
                SubstrateOf(technology, layerSelection), parameters.Real("N", 1.0)).Plan
            : BranchLinePlan.Build(parameters.Real("Z0", 50.0), parameters.Real("F", 2.4e9),
            parameters.Real("N", 1.0), SubstrateOf(technology, layerSelection)).Plan, technology, layerSelection);

    /// <summary>The rat-race hybrid (MRRC): <see cref="RatRacePlan"/>'s ring.</summary>
    public const string RatRaceId = ComponentModelFactory.RatRaceTypeName;

    public static PCellResult GenerateRatRace(
        IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology, PCellLayerSelection layerSelection)
        => DrawOrRefuse(() => Lumped(parameters)
            ? LumpedPlan.RatRace(parameters.Real("Z0", 50.0), parameters.Real("F", 2.4e9), parameters.Real("Gap", 1.35e-3),
                SubstrateOf(technology, layerSelection)).Plan
            : RatRacePlan.Build(parameters.Real("Z0", 50.0), parameters.Real("F", 2.4e9),
            SubstrateOf(technology, layerSelection), CornerOf(parameters)).Plan, technology, layerSelection);

    /// <summary>A ring's <c>Corner</c>, a name; Curve when it is absent or not one of the names.</summary>
    private static PlanCorner CornerOf(IReadOnlyDictionary<string, PCellValue> parameters) => Word(parameters, "Corner", PlanCorner.Curve);

    /// <summary>A word-valued parameter, <paramref name="fallback"/> when it is absent or not one of the names.</summary>
    private static T Word<T>(IReadOnlyDictionary<string, PCellValue> parameters, string name, T fallback) where T : struct, Enum
        => Enum.TryParse(parameters.Text(name), ignoreCase: true, out T c) && Enum.IsDefined(c) ? c : fallback;

    /// <summary>A spec-driven passive's <c>Realization</c> is Lumped (<see cref="LumpedPlan"/>).</summary>
    private static bool Lumped(IReadOnlyDictionary<string, PCellValue> parameters)
        => Word(parameters, "Realization", PlanRealization.Distributed) == PlanRealization.Lumped;

    /// <summary>The coupled-line coupler (MCLC): <see cref="CoupledLineCouplerPlan"/>'s one MCLIN.</summary>
    public const string CoupledLineCouplerId = ComponentModelFactory.CoupledLineCouplerTypeName;

    /// <summary>The four-finger interdigitated coupler (MIDC): <see cref="LangePlan"/>'s fingers.</summary>
    public const string LangeId = ComponentModelFactory.LangeTypeName;

    /// <summary>The coupled-line balun (MCBL): <see cref="MarchandBalunPlan"/>'s two sections and their joining line.</summary>
    public const string MarchandId = ComponentModelFactory.MarchandTypeName;

    public static PCellResult GenerateCoupledLineCoupler(
        IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology, PCellLayerSelection layerSelection)
        => DrawOrRefuse(() => CoupledLineCouplerPlan.Build(parameters.Real("Coupling", 10.0), parameters.Real("Z0", 50.0),
            parameters.Real("F", 2.4e9), SubstrateOf(technology, layerSelection)).Plan, technology, layerSelection);

    public static PCellResult GenerateLange(
        IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology, PCellLayerSelection layerSelection)
        => DrawOrRefuse(() => LangePlan.Build(parameters.Real("Coupling", 4.0), parameters.Real("Z0", 50.0),
            parameters.Real("F", 2.4e9), SubstrateOf(technology, layerSelection)).Plan, technology, layerSelection);

    public static PCellResult GenerateMarchand(
        IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology, PCellLayerSelection layerSelection)
        => DrawOrRefuse(() => Lumped(parameters)
            ? LumpedPlan.LatticeBalun(parameters.Real("Z0", 50.0), parameters.Real("ZL", 200.0), parameters.Real("F", 2.4e9),
                parameters.Real("Gap", 1e-3), SubstrateOf(technology, layerSelection)).Plan
            : MarchandBalunPlan.Build(parameters.Real("Z0", 50.0), parameters.Real("ZL", 200.0),
            parameters.Real("F", 2.4e9), parameters.Real("Gap", 1e-3), SubstrateOf(technology, layerSelection)).Plan,
            technology, layerSelection);

    /// <summary>The resistive attenuator and splitter (MRPAD): <see cref="LumpedPlan.ResistivePad"/>'s interconnect.</summary>
    public const string ResistivePadId = ComponentModelFactory.ResistivePadTypeName;

    public static PCellResult GenerateResistivePad(
        IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology, PCellLayerSelection layerSelection)
        => DrawOrRefuse(() => LumpedPlan.ResistivePad(Word(parameters, "Form", ResistivePadForm.Pi), parameters.Real("Z0", 50.0),
            parameters.Real("Attenuation", 6.0), parameters.Real("Gap", 1.35e-3), SubstrateOf(technology, layerSelection)).Plan,
            technology, layerSelection);

    /// <summary>N coupled microstrip lines (MCLINN): one <see cref="PlanCoupledStrips"/> section, its pins the strips'
    /// near ends 1..N and far ends N + 1..2N, as <see cref="MicrostripMultiLineModel"/> numbers them.</summary>
    public const string CoupledStripsId = MicrostripMultiLineModel.TypeName;

    public static PCellResult GenerateCoupledStrips(
        IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology, PCellLayerSelection layerSelection)
    {
        int n = (int)Math.Round(parameters.Real("N", 3.0));
        var (w, s) = MicrostripMultiLineModel.StripsOf(n, parameters.Real, ComponentModelFactory.DefaultCoupledWidthMeters,
            ComponentModelFactory.DefaultCoupledGapMeters);
        double l = parameters.Real("L", 10e-3);
        if (MicrostripMultiLineModel.Refusal(w, s, l, SubstrateOf(technology, layerSelection)) is { } why)
            return new PCellResult([], [], [why.Render()]);
        var plan = new PassivePlan([.. Enumerable.Range(1, 2 * n).Select(k => $"{k}")],
            [new PlanCoupledStrips([.. Enumerable.Range(1, n).SelectMany(k => new[] { k, n + k })], 0.0, 0.0, 0.0, w, s, l)],
            2 * n);
        return Draw(plan, technology, layerSelection);
    }

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
                PlanCross x => MCrossPCell.Generate(new Dictionary<string, PCellValue>
                {
                    ["W1"] = x.W, ["W2"] = x.W, ["W3"] = x.W, ["W4"] = x.W, ["L1"] = x.L, ["L2"] = x.L, ["L3"] = x.L, ["L4"] = x.L,
                }, technology, layerSelection),
                PlanCurve c => MCurvePCell.Generate(new Dictionary<string, PCellValue>
                {
                    ["W"] = c.W, ["Angle"] = c.AngleDeg, ["Radius"] = c.Radius,
                }, technology, layerSelection),
                PlanLine l => MlinPCell.Generate(new Dictionary<string, PCellValue> { ["W"] = l.W, ["L"] = l.L },
                    technology, layerSelection),
                PlanCoupledLine { Fingers: 2 } c => CoupledLinePCells.GenerateMclin(new Dictionary<string, PCellValue>
                {
                    ["W"] = c.W, ["S"] = c.S, ["L"] = c.L,
                }, technology, layerSelection),
                PlanCoupledLine c => Strips([.. Enumerable.Repeat(c.W, c.Fingers)], [.. Enumerable.Repeat(c.S, c.Fingers - 1)],
                    c.L, technology, layerSelection),
                PlanCoupledStrips m => Strips(m.W, m.S, m.L, technology, layerSelection),
                PlanBend b => b.M is { } m
                    ? MMiterPCell.Generate(new Dictionary<string, PCellValue>
                    {
                        ["W"] = b.W, ["Angle"] = b.AngleDeg, ["M"] = m,
                    }, technology, layerSelection)
                    : MBendPCell.Generate(new Dictionary<string, PCellValue>
                    {
                        ["W"] = b.W, ["Angle"] = b.AngleDeg, ["Miter"] = (double)MicrostripBendMiter.None,
                    }, technology, layerSelection),
                PlanGap g => MGapPCell.Generate(new Dictionary<string, PCellValue> { ["W"] = g.W, ["S"] = g.S },
                    technology, layerSelection),
                PlanTaper t => MTaperPCell.Generate(new Dictionary<string, PCellValue>
                {
                    ["W1"] = t.W1, ["W2"] = t.W2, ["L"] = t.L,
                }, technology, layerSelection),
                PlanOpen => new PCellResult([], []),
                PlanRadialStub r => MRadialPCell.Generate(new Dictionary<string, PCellValue>
                {
                    ["W"] = r.W, ["Ro"] = r.Ro, ["Angle"] = r.AngleDeg, ["Sides"] = (double)r.Sides,
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

    /// <summary>A section of coupled strips (an interdigitated <see cref="PlanCoupledLine"/>, or <see cref="PlanCoupledStrips"/>):
    /// each strip an MLIN of its own width, strip k's centre below strip k − 1's by their half widths and the gap between
    /// them (for equal strips k·(W + S) below the first, as MCLIN places its second), with the strips' pins in the
    /// section's node order (each strip's near end, then its far end).</summary>
    private static PCellResult Strips(IReadOnlyList<double> w, IReadOnlyList<double> s, double l, Technology? technology,
                                      PCellLayerSelection layerSelection)
    {
        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        var shapes = new List<LayoutShape>();
        var pins = new List<PCellPin>();
        List<string>? diagnostics = null;
        long dy = 0;
        for (int k = 0; k < w.Count; k++)
        {
            if (k > 0)
                dy -= (PCellUnits.MetresToDbu(w[k - 1], dbuPerMicron) + PCellUnits.MetresToDbu(w[k], dbuPerMicron)) / 2
                    + PCellUnits.MetresToDbu(s[k - 1], dbuPerMicron);
            var strip = MlinPCell.Generate(new Dictionary<string, PCellValue> { ["W"] = w[k], ["L"] = l },
                technology, layerSelection);
            long at = dy;
            var down = new LayoutCoordinateTransform((x, y) => (x, y + at), m => m);
            foreach (var shape in strip.Shapes)
            {
                LayoutCoordinateWalk.Transform(shape, down);
                shapes.Add(shape);
            }
            pins.AddRange(strip.Pins.Select(p => p with { Y = p.Y + at }));
            if (k == 0 && strip.Diagnostics is { Count: > 0 } d) diagnostics = [.. d];
        }
        return new PCellResult(shapes, pins, diagnostics);
    }
}

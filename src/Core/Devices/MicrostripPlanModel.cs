using System.Numerics;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices;

/// <summary>
/// A planned microstrip passive (docs/design/rf-passives.md): MWPD, the equal-split Wilkinson divider
/// (<see cref="WilkinsonPlan"/>), and the coupled-line parts (<see cref="CoupledLineCouplerPlan"/>,
/// <see cref="LangePlan"/>). It has no line physics of its own: each section of the
/// plan is the EXISTING part's model (<see cref="MicrostripTeeModel"/>, <see cref="MicrostripCurveModel"/>,
/// <see cref="MicrostripLineModel"/>, <see cref="MicrostripCoupledLineModel"/>), stamped into a
/// <see cref="LocalAdmittanceBlock"/> on the plan's nodes, and the internal nodes are Kron-reduced away so the
/// instance stamps one block on its pins — the way MTEE stamps the arms it draws (<see cref="JunctionArms"/>).
/// The same network built from explicit instances of those parts is therefore the same matrix.
///
/// <para>DC: every section is lossless there and joins the ends of each of its strips, so the pins fall into the
/// plan's copper islands — each island's pins declared as shorts for the DC engines (<see cref="IDcShortedTerminals"/>),
/// and tied as MTEE ties its own by an ω = 0 stamp. A Wilkinson is one island; a coupler's two strips are two.</para>
/// </summary>
public sealed class MicrostripPlanModel : ComponentModel, IReportsWarnings, IDcShortedTerminals
{
    private readonly PassivePlan _plan;
    private readonly (ComponentModel Model, ElaboratedComponent At)[] _sections;

    public override int PortCount => _plan.Pins.Count;
    public override ModelKind Kind => ModelKind.Linear;
    public override string[] TerminalNames => [.. _plan.Pins];
    public IReadOnlyList<(int A, int B)> DcShortedTerminals { get; }

    public MicrostripPlanModel(PassivePlan plan, MicrostripSubstrate s, string instancePath)
    {
        _plan = plan;
        _sections = [.. plan.Sections.Select(section => Build(section, s, instancePath))];
        DcShortedTerminals = Islands(plan);
    }

    /// <summary>Each pin paired with the first pin on its copper island: a tree, so the DC shorts form no loop.</summary>
    private static (int A, int B)[] Islands(PassivePlan plan)
    {
        var root = Enumerable.Range(0, plan.NodeCount + 1).ToArray();
        int Find(int n) => root[n] == n ? n : root[n] = Find(root[n]);
        foreach (var section in plan.Sections)
        {
            var n = section.Nodes;
            var joins = section is PlanCoupledLine c
                ? Enumerable.Range(0, c.Fingers).Select(k => (n[2 * k], n[2 * k + 1]))
                : n.Skip(1).Select(x => (n[0], x));
            foreach (var (a, b) in joins) root[Find(a)] = Find(b);
        }
        int pins = plan.Pins.Count;
        return [.. Enumerable.Range(1, pins - 1)
            .Select(k => (A: Enumerable.Range(0, k + 1).First(j => Find(j + 1) == Find(k + 1)), B: k))
            .Where(p => p.A != p.B)];
    }

    private static (ComponentModel, ElaboratedComponent) Build(PlanSection section, MicrostripSubstrate s, string path)
    {
        ComponentModel model = section switch
        {
            PlanTee t => new MicrostripTeeModel(t.W1, t.W2, t.W3, s.HMeters, s.TMeters, s.EpsR, s.SigmaSPerM, s.TanD,
                path, t.L1, t.L2, t.L3, s.RoughnessMeters),
            PlanCurve c => new MicrostripCurveModel(c.W, c.AngleDeg, c.Radius, s.HMeters, s.TMeters, s.EpsR,
                s.SigmaSPerM, s.TanD, path, s.RoughnessMeters),
            PlanLine l => new MicrostripLineModel(l.W, l.L, s.HMeters, s.TMeters, s.EpsR, s.SigmaSPerM, s.TanD, path,
                s.RoughnessMeters),
            PlanCoupledLine { Fingers: 2 } p => new MicrostripCoupledLineModel(p.W, p.S, p.L, s.HMeters, s.TMeters,
                s.EpsR, s.SigmaSPerM, s.TanD, path, s.RoughnessMeters),
            PlanCoupledLine p => new InterdigitatedLineModel(p.W, p.S, p.L, p.Fingers, s, path),
            _ => throw new NotSupportedException(section.GetType().Name),
        };
        return (model, new ElaboratedComponent(model.GetType().Name, path, section.Nodes,
            new Dictionary<string, Value>(), model));
    }

    public IReadOnlyList<(string Key, string Message)> DrainWarnings()
        => [.. _sections.Select(x => x.Model).OfType<IReportsWarnings>().SelectMany(m => m.DrainWarnings())
                        .DistinctBy(w => w.Key)];

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        if (omega <= 0.0)
        {
            var g = new Complex(1.0e9, 0.0);
            foreach (var (a, b) in DcShortedTerminals) mna.AddAdmittance(c.Nodes[a], c.Nodes[b], g);
            return;
        }
        var block = new LocalAdmittanceBlock(_plan.NodeCount);
        foreach (var (model, at) in _sections) model.Stamp(block, at, omega);
        block.ReduceOnto(mna, c.Nodes);
    }
}

/// <summary>
/// An interdigitated section of N (even) edge-coupled strips, alternate strips forming two conductors (a
/// <see cref="PlanCoupledLine"/> with more than two fingers). Ou's equations (<see cref="LangePlan"/>) built from the
/// pair of two adjacent strips, which is MCLIN at the same W and S: <see cref="MicrostripCoupledLineModel.ModesAt"/>
/// gives that pair's Ze, Zo and modal εeff, and <see cref="MicrostripCoupledLineModel.Gamma"/> each mode's γ.
///
/// <para>The strips of a conductor are joined only by parts on the passive's pins, so each strip keeps its own ends.
/// With every strip of a conductor at one potential, as Ou assumes, each strip carries its own share of the
/// conductor's modal admittances: an outer strip Ye to ground and Yo with its neighbour (it IS the pair's edge), a strip
/// between two Ye·Ye/(Ye + Ym) to ground plus 2·(Yo − Ye) to its two neighbours, with Ym = (Yo − Ye)/2. Each
/// conductor-A strip and its mirror image in conductor B carry equal shares, so each such pair stamps as a symmetric
/// coupled pair of those modal impedances (<see cref="CLineModel.StampModalPair"/>). Joined at their ends by ideal
/// jumpers the shares add back to Ou's Ze,N and Zo,N exactly; a real jumper carries its strip's share of the current.
/// Loss: each mode's conductor loss is Wheeler's Rs/(Z·W) over the conductor's N/2 strips.</para>
/// </summary>
internal sealed class InterdigitatedLineModel : ComponentModel, IReportsWarnings, IDcShortedTerminals
{
    private readonly MicrostripCoupledLineModel _pair;
    private readonly int _fingers;
    private readonly double _l;

    public override int PortCount => 2 * _fingers;
    public override ModelKind Kind => ModelKind.Linear;
    public IReadOnlyList<(int A, int B)> DcShortedTerminals { get; }

    public InterdigitatedLineModel(double w, double s, double l, int fingers, MicrostripSubstrate sub, string path)
    {
        if (fingers < 4 || fingers % 2 != 0)
            throw new ArgumentException($"An interdigitated section has an even number of strips, at least 4; {fingers} given.");
        _pair = new MicrostripCoupledLineModel(w, s, l, sub.HMeters, sub.TMeters, sub.EpsR, sub.SigmaSPerM, sub.TanD, path,
            sub.RoughnessMeters);
        _fingers = fingers;
        _l = l;
        DcShortedTerminals = [.. Enumerable.Range(0, fingers).Select(k => (2 * k, 2 * k + 1))];
    }

    public IReadOnlyList<(string Key, string Message)> DrainWarnings() => _pair.DrainWarnings();

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        double f = omega / (2.0 * Math.PI);
        var m = _pair.ModesAt(f);
        var (zeN, zoN) = LangePlan.Equivalent(m.Ze, m.Zo, _fingers);
        double half = _fingers / 2.0;
        var (ge, go) = _pair.Gamma(f, m with { Ze = zeN * half, Zo = zoN * half });
        double ye = 1.0 / m.Ze, yo = 1.0 / m.Zo, ym = (yo - ye) / 2.0;
        double yIn = ye * ye / (ye + ym);
        var n = c.Nodes;
        // Strip k (0-based, top first) of conductor A (even k) pairs with its mirror N-1-k in conductor B.
        for (int k = 0; k < _fingers; k += 2)
        {
            int b = _fingers - 1 - k;
            bool outer = k == 0;
            double yEven = outer ? ye : yIn, yOdd = outer ? yo : yIn + 2.0 * (yo - ye);
            CLineModel.StampModalPair(mna, n[2 * k], n[2 * k + 1], n[2 * b], n[2 * b + 1],
                1.0 / yEven, ge * _l, 1.0 / yOdd, go * _l);
        }
    }
}

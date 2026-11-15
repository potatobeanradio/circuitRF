using System.Numerics;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices;

/// <summary>
/// A planned microstrip passive (docs/design/rf-passives.md) — today MWPD, the equal-split Wilkinson divider
/// (<see cref="WilkinsonPlan"/>). It has no line physics of its own: each section of the plan is the EXISTING
/// part's model (<see cref="MicrostripTeeModel"/>, <see cref="MicrostripCurveModel"/>), stamped into a
/// <see cref="LocalAdmittanceBlock"/> on the plan's nodes, and the internal nodes are Kron-reduced away so the
/// instance stamps one block on its pins — the way MTEE stamps the arms it draws (<see cref="JunctionArms"/>).
/// The same network built from explicit MTEE and MCURVE instances is therefore the same matrix.
///
/// <para>DC: every section is lossless there and joins its pins, so all the pins are one node — declared as
/// shorts for the DC engines (<see cref="IDcShortedTerminals"/>), and tied as MTEE ties its own by an
/// ω = 0 stamp.</para>
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
        DcShortedTerminals = [.. Enumerable.Range(1, plan.Pins.Count - 1).Select(k => (0, k))];
    }

    private static (ComponentModel, ElaboratedComponent) Build(PlanSection section, MicrostripSubstrate s, string path)
    {
        ComponentModel model = section switch
        {
            PlanTee t => new MicrostripTeeModel(t.W1, t.W2, t.W3, s.HMeters, s.TMeters, s.EpsR, s.SigmaSPerM, s.TanD,
                path, t.L1, t.L2, t.L3, s.RoughnessMeters),
            PlanCurve c => new MicrostripCurveModel(c.W, c.AngleDeg, c.Radius, s.HMeters, s.TMeters, s.EpsR,
                s.SigmaSPerM, s.TanD, path, s.RoughnessMeters),
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
            for (int k = 1; k < c.Nodes.Length; k++) mna.AddAdmittance(c.Nodes[0], c.Nodes[k], g);
            return;
        }
        var block = new LocalAdmittanceBlock(_plan.NodeCount);
        foreach (var (model, at) in _sections) model.Stamp(block, at, omega);
        block.ReduceOnto(mna, c.Nodes);
    }
}

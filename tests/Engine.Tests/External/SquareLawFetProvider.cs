using System.Globalization;
using CircuitRF.Core.Devices.External;

namespace CircuitRF.Engine.Tests.External;

/// <summary>
/// The synthetic external-device provider: a textbook square-law FET with parasitic access
/// resistances and self-heating, used as the test oracle for the whole external-device path.
///
/// <para>This is the test strategy for external devices, not a toy. A real provider's model has no
/// closed form, so a test written against one can only check that nothing crashed. This fixture has
/// <b>known exact answers</b> — an analytic Jacobian and a DC operating point obtainable from a
/// scalar solve that never touches circuitRF's matrix — so it can assert the operating point, the
/// internal node voltages, and the Jacobian entry by entry. It also mirrors the real topology it
/// stands in for: access resistances that create genuine internal nodes, and a thermal node that is
/// a solved unknown rather than a fixed input.</para>
///
/// <para>Node layout (external pins first, then internal, matching descriptor order):</para>
/// <code>
///   0 gate      ──Rg──┐
///   1 drain     ──────┼── channel ──┐
///   2 source    ──Rs──┼─────────────┤
///   3 thermal         │             │        4 gateInternal
///                     └─ 4 ─────────┘        5 sourceInternal
/// </code>
///
/// <para>Behaviour: <c>Id = β·(Vgs_int − Vth(T))²·(1 + λ·Vds_int)</c> in saturation, zero below
/// threshold. Dissipated power <c>P = Id·Vds_int</c> is delivered to the thermal node, which has its
/// own internal <c>Rth</c> to a fixed reference — so the thermal node is never floating and the
/// self-heating loop closes through the solver: power out, temperature back, threshold shifts.</para>
/// </summary>
public sealed class SquareLawFetProvider : IExternalDeviceProvider
{
    public const string TypeName = "SquareLawFet";

    public string Name { get; }

    public SquareLawFetProvider(string name = "synthetic") => Name = name;

    // Node indices — the single place the layout is written down.
    public const int Gate = 0, Drain = 1, Source = 2, Thermal = 3, GateInt = 4, SourceInt = 5;
    public const int NodeCount = 6;

    public static readonly ExternalDeviceDescriptor TypeDescriptor = new(
        TypeId:            TypeName,
        DisplayName:       "Square-law FET (synthetic)",
        ExternalPinCount:  4,
        InternalNodeCount: 2,
        Parameters:
        [
            new ExternalParamDescriptor("Beta",   ExternalParamKind.Double, "0.05", "A/V^2"),
            new ExternalParamDescriptor("Vth0",   ExternalParamKind.Double, "1.0",  "V"),
            new ExternalParamDescriptor("Lambda", ExternalParamKind.Double, "0.02", "1/V"),
            new ExternalParamDescriptor("Rg",     ExternalParamKind.Double, "10.0", "Ohm"),
            new ExternalParamDescriptor("Rs",     ExternalParamKind.Double, "1.0",  "Ohm"),
            new ExternalParamDescriptor("Rth",    ExternalParamKind.Double, "5.0",  "degC/W"),
            new ExternalParamDescriptor("Ktv",    ExternalParamKind.Double, "0.0",  "V/degC"),
        ],
        Nodes:
        [
            new ExternalNodeDescriptor(Gate,      External: true,  NodeQuantityKind.Electrical, "gate"),
            new ExternalNodeDescriptor(Drain,     External: true,  NodeQuantityKind.Electrical, "drain"),
            new ExternalNodeDescriptor(Source,    External: true,  NodeQuantityKind.Electrical, "source"),
            new ExternalNodeDescriptor(Thermal,   External: true,  NodeQuantityKind.Thermal,    "thermal"),
            new ExternalNodeDescriptor(GateInt,   External: false, NodeQuantityKind.Electrical, "gateInt"),
            new ExternalNodeDescriptor(SourceInt, External: false, NodeQuantityKind.Electrical, "sourceInt"),
        ],
        OpVars:
        [
            // Every one of these has a CLOSED FORM in Channel() below, which is what makes a
            // read-back checkable against arithmetic instead of against itself. `Region` is an int
            // and `Regime` a string, so the two non-real cases are exercised too: an int is a real
            // once it is a number in a cube, and a string has nowhere to land in one and must be
            // DECLARED here and absent from every read-back.
            new ExternalOpVarDescriptor("Id",     ExternalParamKind.Double, "A",    "drain current"),
            new ExternalOpVarDescriptor("Gm",     ExternalParamKind.Double, "S",    "transconductance"),
            new ExternalOpVarDescriptor("Gds",    ExternalParamKind.Double, "S",    "output conductance"),
            new ExternalOpVarDescriptor("Tj",     ExternalParamKind.Double, "degC", "junction temperature"),
            new ExternalOpVarDescriptor("Region", ExternalParamKind.Int,    "",     "0 = cut off, 1 = on"),
            new ExternalOpVarDescriptor("Regime", ExternalParamKind.String, "",     "the region, in words"),
        ]);

    public IReadOnlyList<ExternalDeviceDescriptor> Describe() => [TypeDescriptor];

    /// <summary>
    /// How many instances have been asked for, and how many have not been given back.
    ///
    /// <para>Counted because an instance a real provider makes lives in ANOTHER PROCESS, where no
    /// garbage collector reaches it and where a worker's own table is finite. A sweep re-elaborates
    /// per point by design, so "did anything hand them back" is a property worth asserting and one
    /// nothing else here would notice.</para>
    /// </summary>
    public int Created { get; private set; }
    public int Live    { get; private set; }

    /// <summary>
    /// How many evaluation POINTS this provider has been asked for, across every instance.
    ///
    /// <para>Counted because the structural property a read-back has to keep is a count, not a
    /// duration: one evaluation per device per converged point. A timing test would measure the
    /// machine; this measures the thing that would actually regress — a read-back accidentally
    /// wired into the Newton loop rather than taken once at the answer.</para>
    /// </summary>
    public int PointsEvaluated { get; private set; }

    /// <summary>Round trips spent on the standalone operating-point read, across every instance.</summary>
    public int OperatingPointReads { get; private set; }

    public void ResetCounters() { PointsEvaluated = 0; OperatingPointReads = 0; }

    public IExternalDeviceInstance Create(string typeId, IReadOnlyDictionary<string, string> parameters)
    {
        if (!string.Equals(typeId, TypeName, StringComparison.Ordinal))
            throw new ExternalDeviceException($"Provider '{Name}' does not expose a type '{typeId}'.");

        Created++;
        Live++;
        return new Instance(parameters, this);
    }

    /// <summary>Parameters as this provider resolves them — public so tests can build the oracle.</summary>
    public sealed record Params(double Beta, double Vth0, double Lambda,
                                double Rg, double Rs, double Rth, double Ktv);

    public static Params ReadParams(IReadOnlyDictionary<string, string> p)
    {
        double Get(string key, double fallback) =>
            p.TryGetValue(key, out var s) &&
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? d : fallback;

        var v = new Params(
            Beta:   Get("Beta",   0.05),
            Vth0:   Get("Vth0",   1.0),
            Lambda: Get("Lambda", 0.02),
            Rg:     Get("Rg",     10.0),
            Rs:     Get("Rs",     1.0),
            Rth:    Get("Rth",    5.0),
            Ktv:    Get("Ktv",    0.0));

        if (v.Rg <= 0 || v.Rs <= 0 || v.Rth <= 0)
            throw new ExternalDeviceException(
                $"{TypeName}: Rg, Rs and Rth must all be positive (got Rg={v.Rg}, Rs={v.Rs}, Rth={v.Rth}).");
        return v;
    }

    /// <summary>Drain current and its three partial derivatives, shared by the model and the oracle.</summary>
    public static (double Id, double Gm, double Gds, double GT) Channel(
        Params p, double vgsInt, double vdsInt, double temp)
    {
        double vth = p.Vth0 + p.Ktv * temp;
        double ov  = vgsInt - vth;
        if (ov <= 0.0) return (0.0, 0.0, 0.0, 0.0);        // subthreshold: hard cutoff, C0 at ov=0

        double lam = 1.0 + p.Lambda * vdsInt;
        double id  = p.Beta * ov * ov * lam;
        double gm  = 2.0 * p.Beta * ov * lam;              // ∂Id/∂Vgs_int
        double gds = p.Beta * ov * ov * p.Lambda;          // ∂Id/∂Vds_int
        double gT  = -gm * p.Ktv;                          // ∂Id/∂T, through Vth(T)
        return (id, gm, gds, gT);
    }

    private sealed class Instance(IReadOnlyDictionary<string, string> parameters, SquareLawFetProvider owner)
        : IExternalDeviceInstance
    {
        private readonly Params _p = ReadParams(parameters);

        public ExternalDeviceDescriptor Descriptor => TypeDescriptor;

        public ExternalDeviceEvaluation Evaluate(IReadOnlyList<double> v)
        {
            if (v.Count != NodeCount)
                throw new ExternalDeviceException(
                    $"{TypeName}: expected {NodeCount} node voltages, got {v.Count}.");

            double vg = v[Gate], vd = v[Drain], vs = v[Source];
            double t  = v[Thermal], vgi = v[GateInt], vsi = v[SourceInt];

            owner.PointsEvaluated++;

            double vgsInt = vgi - vsi;
            double vdsInt = vd  - vsi;
            var (id, gm, gds, gT) = Channel(_p, vgsInt, vdsInt, t);

            // Written during the LOAD, exactly as a compiled model writes its own — which is what
            // makes the whole of a read-back a question of position in time.
            _last = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["Id"]     = id,
                ["Gm"]     = gm,
                ["Gds"]    = gds,
                ["Tj"]     = t,
                ["Region"] = id > 0.0 ? 1.0 : 0.0,
            };

            double gG = 1.0 / _p.Rg, gS = 1.0 / _p.Rs, gTh = 1.0 / _p.Rth;
            double power = id * vdsInt;

            // Passive convention throughout: I[k] is the current flowing INTO the device at node k.
            var i = new double[NodeCount];
            i[Gate]      =  (vg - vgi) * gG;
            i[Drain]     =  id;
            i[Source]    =  (vs - vsi) * gS;
            i[Thermal]   = -power + t * gTh;     // device delivers P out; internal Rth pins the node
            i[GateInt]   =  (vgi - vg) * gG;     // gate is DC-open beyond the access resistor
            i[SourceInt] = -id + (vsi - vs) * gS;

            // Analytic Jacobian. Vgs_int = v[GateInt] − v[SourceInt]; Vds_int = v[Drain] − v[SourceInt].
            var g = new double[NodeCount, NodeCount];
            g[Gate, Gate]       =  gG;   g[Gate, GateInt]     = -gG;
            g[GateInt, GateInt] =  gG;   g[GateInt, Gate]     = -gG;

            g[Source, Source]       =  gS;   g[Source, SourceInt] = -gS;
            g[SourceInt, SourceInt] =  gS;   g[SourceInt, Source] = -gS;

            g[Drain, Drain]     =  gds;
            g[Drain, Thermal]   =  gT;
            g[Drain, GateInt]   =  gm;
            g[Drain, SourceInt] = -gm - gds;

            g[SourceInt, Drain]     -=  gds;
            g[SourceInt, Thermal]   -=  gT;
            g[SourceInt, GateInt]   -=  gm;
            g[SourceInt, SourceInt] -= -gm - gds;

            // Thermal row: I = −Id·Vds_int + T/Rth, so every channel derivative appears twice —
            // once through Id and once through Vds_int itself.
            g[Thermal, Drain]     = -(gds * vdsInt + id);
            g[Thermal, Thermal]   = -(gT * vdsInt) + gTh;
            g[Thermal, GateInt]   = -(gm * vdsInt);
            g[Thermal, SourceInt] = -((-gm - gds) * vdsInt - id);

            return new ExternalDeviceEvaluation(i, new double[NodeCount], g, new double[NodeCount, NodeCount]);
        }

        /// <summary>
        /// The op-vars of the LAST bias this instance evaluated — a read, never an evaluation, so it
        /// carries the same timing hazard a real provider's does: read a call too late and it
        /// describes the previous point.
        /// </summary>
        public IReadOnlyDictionary<string, double>? ReadOperatingPoint()
        {
            owner.OperatingPointReads++;
            // The string-valued `Regime` is declared and is NOT here: a single-kind numeric cube has
            // nowhere to put it. Its absence is the property under test, not an omission.
            return _last;
        }

        public ExternalOperatingPoint? EvaluateOperatingPoint(IReadOnlyList<IReadOnlyList<double>> nodeVoltages)
        {
            var names = new[] { "Id", "Gm", "Gds", "Tj", "Region" };
            var rows  = new double[nodeVoltages.Count][];
            for (int k = 0; k < nodeVoltages.Count; k++)
            {
                Evaluate(nodeVoltages[k]);
                rows[k] = [.. names.Select(n => _last![n])];
            }
            return new ExternalOperatingPoint(names, rows);
        }

        private Dictionary<string, double>? _last;

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.Live--;
        }
    }
}

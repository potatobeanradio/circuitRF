using System.Globalization;
using CircuitRF.Core.Devices.Composed.Traps;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices.Composed;

/// <summary>
/// The constituents of one <c>.cmodel</c>, compiled ONCE when the file is loaded and shared by every
/// <see cref="ComposedCoreModel"/> built on it (R-mc1-3, R-mc2-1/2/3).
///
/// <para><b>Inputs are seeds.</b> <c>vgs</c> and <c>vds</c> always, <c>T</c> when the model is
/// over a temperature range, and the enabled trap states (MC-10) occupy the gradient lanes in that order, so one evaluation returns
/// a constituent's value and its derivative by each. An equation runs through the SDD's own
/// dual-number arithmetic (<see cref="CompiledSddExpr.CompileNamed"/>); a table or an ANN runs through
/// its evaluator inside its <see cref="GuidedExtrapolation"/>, its inputs gathered from the seeds and
/// its gradient scattered back into their lanes.</para>
/// </summary>
public sealed class ComposedCoreProgram
{
    /// <summary>The intrinsic gate–source voltage, V.</summary>
    public const string Vgs = "vgs";
    /// <summary>The intrinsic drain–source voltage, V.</summary>
    public const string Vds = "vds";
    /// <summary>The device temperature, °C — an input only of a <see cref="TemperatureMode.Range"/> model.</summary>
    public const string T   = "T";

    /// <summary>The seed names in lane order.</summary>
    public IReadOnlyList<string> Seeds { get; }

    /// <summary>Gradient width: 2, 3 with <c>T</c>, plus one per enabled trap state.</summary>
    public int Width => Seeds.Count;

    /// <summary>Whether <c>T</c> is a lane (a <see cref="TemperatureMode.Range"/> model).</summary>
    public bool HasT { get; }

    /// <summary>The trap states' dynamics (MC-10), or null for a model without traps.</summary>
    public TrapDynamics? Traps { get; }

    /// <summary>The threshold-shift coupling (MC-19), or null when <c>Id</c> takes the states as inputs or there are none.</summary>
    public TrapShift? Shift { get; }

    /// <summary>The lane of the first trap state: 2, or 3 with <c>T</c>.</summary>
    public int TrapLane => HasT ? 3 : 2;

    /// <summary>The drain–source symmetry option (R-mc2-4).</summary>
    public DrainSourceSymmetry Symmetry { get; }

    /// <summary>[P1] eq. 6.52's time constant, seconds, when the model carries <c>IdRF</c>; else null.</summary>
    public double? DispersionTau { get; }

    // One compiled function per payload entry, in a fixed order the core reads by field. IdRF is null
    // unless the model is dispersive, and then Id is the DC current.
    internal readonly ComposedFunction Id, IgGs, IgGd, Qg, Qd;
    internal readonly ComposedFunction? IdRF;

    /// <summary>The drift decomposition when <c>Qg</c> and <c>Qd</c> are <c>Drift</c> (MC-8); the core then takes the
    /// charges from it, and <see cref="Qg"/>/<see cref="Qd"/> are zero.</summary>
    public DriftCharges? Drift { get; }

    private readonly string _label;
    private readonly double _t0;   // the temperature a shift's DC states are read at when T is not a lane

    private ComposedCoreProgram(IReadOnlyList<string> seeds, bool hasT, TrapDynamics? traps, TrapShift? shift, double t0, string label,
                                DrainSourceSymmetry symmetry,
                                ComposedFunction id, ComposedFunction igGs, ComposedFunction igGd,
                                ComposedFunction qg, ComposedFunction qd, ComposedFunction? idRf, double? tau,
                                DriftCharges? drift)
    {
        Seeds = seeds; HasT = hasT; Traps = traps; Shift = shift; _t0 = t0; _label = label; Symmetry = symmetry;
        Id = id; IgGs = igGs; IgGd = igGd; Qg = qg; Qd = qd; IdRF = idRf; DispersionTau = tau; Drift = drift;
    }

    /// <summary>Compiles a model already checked by <see cref="CmodelFile.Validate"/>.</summary>
    public static ComposedCoreProgram Compile(ComposedModel model, string label)
    {
        bool hasT = model.Temperature.Mode == TemperatureMode.Range;
        var traps = model.Traps is { } tb ? TrapModels.Build(tb, label) : null;
        IReadOnlyList<string> seeds = [Vgs, Vds, .. (hasT ? (string[])[T] : []), .. traps?.States ?? []];

        ComposedFunction One(Constituent c, string name, string key, double[]? ownBranch)
        {
            if (c.Method == Constituent.DriftMethod) return ZeroFunction.Instance;
            if (c.Method == Constituent.EquationMethod)
            {
                var parameters = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var p in c.Parameters) parameters[p.Key] = p.Value;
                var ast = Parser.Parse(c.Expression(key)!);
                return new EquationFunction(CompiledSddExpr.CompileNamed(ast, seeds, parameters, label), ast, seeds, parameters, label);
            }

            var evaluator = RuntimePayload.Build(model, c, key, $"Core.Constituents.{name}.{key}");
            double gx = ownBranch is null ? 0.0 : model.Extrapolation!.Gx[GxKey(name, key)];
            return new RuntimeFunction(
                new GuidedExtrapolation(evaluator, c.Domain!, c.Inputs, gx, ownBranch, model.Extrapolation?.Mode ?? ExtrapolationMode.Global),
                c.Inputs, seeds);
        }

        var k = model.Core;
        ComposedFunction? Temp(NoiseValue? v, string key) => v?.Function is { } f ? One(f, $"Noise.{key}", f.PayloadKey, null) : null;
        var noise = k.Noise;
        var program = new ComposedCoreProgram(seeds, hasT, traps, traps is null ? null : TrapShift.Build(model.Traps!, traps),
            hasT ? double.NaN : model.Temperature.T0, label, k.Symmetry,
            One(k.Id, "Id", k.Id.PayloadKey, Branch(k.Id.Inputs, vgs: 0, vds: 1)),
            One(k.Ig, "Ig", "IgGs", Branch(k.Ig.Inputs, vgs: 1, vds: 0)),
            One(k.Ig, "Ig", "IgGd", Branch(k.Ig.Inputs, vgs: 1, vds: -1)),
            One(k.Qg, "Qg", k.Qg.PayloadKey, null),
            One(k.Qd, "Qd", k.Qd.PayloadKey, null),
            k.IdRF is { } rf ? One(rf, "IdRF", rf.PayloadKey, Branch(rf.Inputs, vgs: 0, vds: 1)) : null,
            k.Dispersion?.Tau,
            k.Qg.Method == Constituent.DriftMethod ? DriftCharges.Build(model) : null);
        if (noise is not null)
        {
            program.NoiseTg = Temp(noise.Tg, "Tg");
            program.NoiseTd = Temp(noise.Td, "Td");
        }
        return program;
    }

    /// <summary>The <c>Core.Noise</c> temperatures that are constituents, compiled as the currents are (brief NZ-4); null for
    /// a constant or an absent one.</summary>
    internal ComposedFunction? NoiseTg { get; private set; }
    internal ComposedFunction? NoiseTd { get; private set; }

    /// <summary>
    /// The seeds of a DC operating point: <paramref name="vgs"/>, <paramref name="vds"/>, <paramref name="t"/> (°C) when
    /// it is a lane, and every trap state at its DC value — what a caller outside the core evaluates a constituent at.
    /// </summary>
    public void DcSeeds(double vgs, double vds, double t, Span<double> seeds)
    {
        seeds[0] = vgs;
        seeds[1] = vds;
        if (HasT) seeds[2] = t;
        if (Traps is { } traps)
            for (int k = 0; k < traps.States.Count; k++) seeds[TrapLane + k] = traps.DcValue(k, vgs, vds, t);
    }

    /// <summary>
    /// The channel current the core evaluates at an operating point — <c>IdRF</c> for a dispersive model, else
    /// <c>Id</c> — and its gradient over the seed lanes: what a drift charge reads (MC-8 R-mc8-3), and what the
    /// composer trains one against.
    /// </summary>
    public double Channel(ReadOnlySpan<double> seeds, Span<double> grad)
        => IdRF is { } rf ? Eval(rf, "IdRF", seeds, grad) : Drain(seeds, grad);

    /// <summary>
    /// <c>Id</c> as the core conducts it: under a shift coupling (MC-19 R-mc19-1) <c>Id₀(vgs − ΔV, vds, T)</c>, its gradient
    /// by the chain rule through <c>ΔV</c> — <c>∂Id/∂x = ∂Id₀/∂x − gm₀·∂ΔV/∂x</c> over every lane, the states' included;
    /// otherwise <c>Id</c> itself.
    /// </summary>
    internal double Drain(ReadOnlySpan<double> seeds, Span<double> grad)
    {
        if (Shift is not { } shift) return Eval(Id, "Id", seeds, grad);
        int n = Traps!.States.Count;
        double t = HasT ? seeds[2] : _t0;
        Span<double> dBias = stackalloc double[3], dPhi = stackalloc double[TrapDynamics.MaxStates];
        double dv = shift.Delta(seeds.Slice(TrapLane, n), seeds[0], seeds[1], t, dBias, dPhi);
        Span<double> x = stackalloc double[RuntimeEvaluator.MaxInputs];
        seeds.CopyTo(x);
        x[0] -= dv;
        double id = Eval(Id, "Id", x[..Width], grad);
        double gm = grad[0];
        grad[0] -= gm * dBias[0];
        grad[1] -= gm * dBias[1];
        if (HasT) grad[2] -= gm * dBias[2];
        for (int k = 0; k < n; k++) grad[TrapLane + k] -= gm * dPhi[k];
        return id;
    }

    /// <summary>
    /// Both charges and their gradients over the seed lanes at one operating point, unscaled and before any
    /// drain–source image — what the composer's report and conservation checks read (MC-8).
    /// </summary>
    public (double Qg, double Qd) Charges(ReadOnlySpan<double> seeds, Span<double> gQg, Span<double> gQd)
    {
        if (Drift is not { } drift) return (Eval(Qg, "Qg", seeds, gQg), Eval(Qd, "Qd", seeds, gQd));
        Span<double> g = stackalloc double[RuntimeEvaluator.MaxInputs];
        double id = DriftChannel(seeds, g);
        drift.Evaluate(seeds[0], seeds[1], id, g, Width, out double qg, gQg, out double qd, gQd);
        return (qg, qd);
    }

    /// <summary>
    /// Both charges' Hessians over <c>(vgs, vds)</c>, <c>[h00, h01, h11]</c> each, the other lanes held: what the branch
    /// linearisation (brief-modelcomposer-22 R-mc22-2) reads. False where a payload forms none exactly — a drift charge, or a
    /// table or ANN outside its training domain — and the caller differences the gradients instead.
    /// </summary>
    internal bool ChargeHessians(ReadOnlySpan<double> seeds, Span<double> hQg, Span<double> hQd)
        => Drift is null && Qg.Hessian(seeds, hQg) && Qd.Hessian(seeds, hQd);

    /// <summary>The current a drift charge reads: <see cref="Channel"/> without the shift, which no charge takes (R-mc19-1).</summary>
    internal double DriftChannel(ReadOnlySpan<double> seeds, Span<double> grad)
        => IdRF is { } rf ? Eval(rf, "IdRF", seeds, grad) : Eval(Id, "Id", seeds, grad);

    /// <summary>The <c>Extrapolation.Gx</c> key of a current payload: <c>Id</c>, <c>IgGs</c>, <c>IgGd</c>.</summary>
    internal static string GxKey(string constituent, string key) => constituent == "Ig" ? key : constituent;

    /// <summary>A branch voltage <c>a·vgs + b·vds</c> as a vector over <paramref name="inputs"/>.</summary>
    private static double[] Branch(IReadOnlyList<string> inputs, double vgs, double vds)
    {
        var own = new double[inputs.Count];
        for (int k = 0; k < own.Length; k++)
            own[k] = inputs[k] == Vgs ? vgs : inputs[k] == Vds ? vds : 0.0;
        return own;
    }

    /// <summary>
    /// One constituent at one operating point: its value, and its gradient written into
    /// <paramref name="grad"/>. A NaN or infinite value or derivative is a named failure — never a
    /// silent zero, which would be a device that conducts nothing exactly where its equation broke.
    /// </summary>
    internal double Eval(ComposedFunction f, string name, ReadOnlySpan<double> seeds, Span<double> grad)
    {
        double v = f.Evaluate(seeds, grad);
        bool finite = double.IsFinite(v);
        for (int i = 0; i < Width && finite; i++) finite = double.IsFinite(grad[i]);
        if (!finite) throw new ComposedEvaluationException(Describe(name, v, seeds));
        return v;
    }

    private string Describe(string name, double v, ReadOnlySpan<double> seeds)
    {
        var at = new List<string>(Width);
        for (int i = 0; i < Width; i++)
            at.Add($"{Seeds[i]}={seeds[i].ToString("G6", CultureInfo.InvariantCulture)}");
        string what = double.IsFinite(v) ? "a derivative that is not finite" : v.ToString(CultureInfo.InvariantCulture);
        return $"'{_label}': constituent {name} evaluated to {what} at {string.Join(", ", at)}.";
    }
}

/// <summary>One compiled constituent payload: value and gradient over the core's seed lanes.</summary>
internal abstract class ComposedFunction
{
    public abstract double Evaluate(ReadOnlySpan<double> seeds, Span<double> grad);

    /// <summary>The Hessian over lanes 0 and 1 (<c>vgs</c>, <c>vds</c>) as <c>[h00, h01, h11]</c>, or false when this
    /// function forms none exactly at <paramref name="seeds"/>.</summary>
    public virtual bool Hessian(ReadOnlySpan<double> seeds, Span<double> h) => false;
}

/// <summary>A charge the drift decomposition supplies instead.</summary>
internal sealed class ZeroFunction : ComposedFunction
{
    public static readonly ZeroFunction Instance = new();

    public override double Evaluate(ReadOnlySpan<double> seeds, Span<double> grad)
    {
        grad.Clear();
        return 0.0;
    }

    public override bool Hessian(ReadOnlySpan<double> seeds, Span<double> h)
    {
        h[..3].Clear();
        return true;
    }
}

/// <summary>An equation: <see cref="CompiledSddExpr"/>'s register program for the value and gradient, and the same AST's
/// node tree in <see cref="SecondOrderDual"/> arithmetic for the Hessian, built on first use.</summary>
internal sealed class EquationFunction(CompiledSddExpr expr, Expr ast, IReadOnlyList<string> seeds,
                                       IReadOnlyDictionary<string, double> parameters, string label) : ComposedFunction
{
    private (CNode Root, double[] Constants)? _second;

    public override double Evaluate(ReadOnlySpan<double> seeds, Span<double> grad)
        => expr.EvalDualInto(seeds, grad, label);

    public override bool Hessian(ReadOnlySpan<double> at, Span<double> h)
    {
        var (root, constants) = _second ??= Compile();
        int n = seeds.Count;
        int total = n + constants.Length;
        Span<SecondOrderDual> slots = total <= 64 ? stackalloc SecondOrderDual[total] : new SecondOrderDual[total];
        for (int i = 0; i < n; i++) slots[i] = i < 2 ? SecondOrderDual.Seed(at[i], i) : SecondOrderDual.Constant(at[i]);
        for (int i = 0; i < constants.Length; i++) slots[n + i] = SecondOrderDual.Constant(constants[i]);
        var r = SddCompiler.Eval<SecondOrderDual>(root, slots);
        h[0] = r.H00; h[1] = r.H01; h[2] = r.H11;
        return double.IsFinite(r.H00) && double.IsFinite(r.H01) && double.IsFinite(r.H11);
    }

    private (CNode, double[]) Compile()
    {
        // CompileNamed's slot order: the seeds, then each parameter not shadowed by one.
        var slotOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < seeds.Count; i++) slotOf[seeds[i]] = i;
        var constants = new double[parameters.Count];
        int k = 0;
        foreach (var kv in parameters)
        {
            if (!slotOf.ContainsKey(kv.Key)) slotOf[kv.Key] = seeds.Count + k;
            constants[k++] = kv.Value;
        }
        return (SddCompiler.CompileNode(ast, slotOf, label), constants);
    }
}

/// <summary>A table or ANN: its inputs gathered from the seeds, its gradient scattered back.</summary>
internal sealed class RuntimeFunction : ComposedFunction
{
    private readonly GuidedExtrapolation _f;
    private readonly int[] _lane;      // input k reads seed lane _lane[k]
    private readonly int _width;

    public RuntimeFunction(GuidedExtrapolation f, IReadOnlyList<string> inputs, IReadOnlyList<string> seeds)
    {
        _f = f;
        _width = seeds.Count;
        _lane = new int[inputs.Count];
        for (int k = 0; k < inputs.Count; k++)
        {
            int lane = -1;
            for (int s = 0; s < seeds.Count; s++) if (seeds[s] == inputs[k]) lane = s;
            _lane[k] = lane >= 0 ? lane
                : throw new InvalidOperationException($"input '{inputs[k]}' has no seed in this model.");
        }
    }

    public override double Evaluate(ReadOnlySpan<double> seeds, Span<double> grad)
    {
        int d = _lane.Length;
        Span<double> x = stackalloc double[RuntimeEvaluator.MaxInputs];
        Span<double> g = stackalloc double[RuntimeEvaluator.MaxInputs];
        for (int k = 0; k < d; k++) x[k] = seeds[_lane[k]];
        double v = _f.Evaluate(x[..d], g);
        grad[.._width].Clear();
        for (int k = 0; k < d; k++) grad[_lane[k]] = g[k];
        return v;
    }

    /// <summary>Inside the training domain, the evaluator's own Hessian along <c>vgs</c> and then <c>vds</c>; outside it the
    /// continuation forms none.</summary>
    public override bool Hessian(ReadOnlySpan<double> seeds, Span<double> h)
    {
        int d = _lane.Length;
        Span<double> x = stackalloc double[RuntimeEvaluator.MaxInputs];
        Span<double> w = stackalloc double[RuntimeEvaluator.MaxInputs];
        Span<double> hw = stackalloc double[RuntimeEvaluator.MaxInputs];
        for (int k = 0; k < d; k++) x[k] = seeds[_lane[k]];
        h[..3].Clear();
        for (int lane = 0; lane < 2; lane++)
        {
            w[..d].Clear();
            bool reads = false;
            for (int k = 0; k < d; k++) if (_lane[k] == lane) { w[k] = 1; reads = true; }
            if (!reads) continue;
            if (!_f.HessianAlong(x[..d], w[..d], hw[..d])) return false;
            for (int k = 0; k < d; k++)
            {
                if (_lane[k] == 0 && lane == 0) h[0] = hw[k];
                if (_lane[k] == 1) h[lane == 0 ? 1 : 2] = hw[k];
            }
        }
        return true;
    }
}

/// <summary>A composed model's equation was not finite at an operating point. The message names the
/// model, the constituent and the point.</summary>
public sealed class ComposedEvaluationException(string message) : Exception(message);

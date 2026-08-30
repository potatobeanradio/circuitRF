using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core;

/// <summary>
/// HB partition: a component is entirely linear or entirely nonlinear — never both.
/// </summary>
public enum ModelKind { Linear, Nonlinear }

/// <summary>
/// Base for every component's electrical behaviour (passive and active alike).
/// "Device" is reserved for its RF meaning (an active part); ComponentModel is the type name.
/// Stamp and Evaluate bodies live in Phase 2+; the base and shape are defined here.
/// </summary>
public abstract class ComponentModel
{
    public abstract int       PortCount { get; }
    public abstract ModelKind Kind      { get; }

    /// <summary>
    /// Terminal names for each port, used to form branch-current cube keys "instancePath:terminalName".
    /// Default: 1-based numeric strings ("1", "2", …). Override in derived types for semantic names.
    /// </summary>
    public virtual string[] TerminalNames
        => Enumerable.Range(1, PortCount).Select(i => i.ToString()).ToArray();

    /// <summary>
    /// Linear contribution — the model contributes stamps; the engine owns the matrix.
    /// Called once per frequency point during analysis assembly.
    /// </summary>
    public virtual void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
        => throw new NotImplementedException($"{GetType().Name}.Stamp is not implemented");

    /// <summary>
    /// Nonlinear contribution — Phase 3 (HB). Not called in Phase 1.
    /// Base default: not a nonlinear model.
    /// </summary>
    public virtual NonlinearResult Evaluate(in PortVoltages v)
        => throw new NotSupportedException($"{GetType().Name} is not a nonlinear model");

    /// <summary>
    /// Nonlinear contribution with control currents. Engines call this form, passing
    /// ControlCurrents.Empty when they don't yet supply control currents.
    /// Base default: forward to the 1-arg form (ignores control currents — correct for every
    /// built-in device; only SddModel overrides this).
    /// </summary>
    public virtual NonlinearResult Evaluate(in PortVoltages v, in ControlCurrents c)
        => Evaluate(v);

    /// <summary>
    /// Whether an engine should gather a whole set of operating points and ask for them at once
    /// rather than calling <see cref="Evaluate(in PortVoltages)"/> per point.
    ///
    /// <para><b>False for every built-in model, deliberately.</b> A built-in evaluation is a direct
    /// call, so gathering would buy nothing and cost an allocation per point — and, more importantly,
    /// an engine that reads this as false takes exactly the code path it took before batching
    /// existed, which is what keeps a built-in device's result bit-identical. It is true only where
    /// an evaluation carries real transport cost (an out-of-process model), which is the case the
    /// batch exists for.</para>
    /// </summary>
    public virtual bool PrefersBatchEvaluate => false;

    /// <summary>
    /// Evaluate a whole set of port-voltage vectors, one result per vector, in declaration order.
    ///
    /// <para>The default is the scalar path applied once per point, so a model that has no cheaper
    /// answer needs no code. Override only where one call for many points is genuinely cheaper than
    /// many calls, and set <see cref="PrefersBatchEvaluate"/> alongside it.</para>
    /// </summary>
    public virtual IReadOnlyList<NonlinearResult> EvaluateBatch(double[][] portVoltages)
    {
        var results = new NonlinearResult[portVoltages.Length];
        for (int k = 0; k < results.Length; k++)
            results[k] = Evaluate(new PortVoltages(portVoltages[k]));
        return results;
    }

    /// <summary>
    /// HB-P4 — whether an engine should hand this model the WHOLE time grid at once
    /// (<see cref="EvaluateGrid"/>) rather than calling <see cref="Evaluate(in PortVoltages)"/> once
    /// per sample.
    ///
    /// <para>Distinct from <see cref="PrefersBatchEvaluate"/>, which exists for models whose cost is
    /// TRANSPORT (an out-of-process worker) and which answers with a list of per-sample results. This
    /// one exists for models whose cost is ARITHMETIC, and it answers into caller-owned
    /// structure-of-arrays buffers: the SDD runs its compiled register program once for the grid with
    /// vectorised operands instead of once per sample through 136-byte duals, and the closed-form
    /// built-ins stop allocating six arrays a sample.</para>
    ///
    /// <para>False by default, so a model that does not opt in takes exactly the path it took before
    /// this existed — which is what keeps its results bit-identical.</para>
    /// </summary>
    public virtual bool PrefersGridEvaluate => false;

    /// <summary>
    /// HB-P4 M4 — whether this model implements <see cref="EvaluateInto"/>, letting the default
    /// <see cref="EvaluateGrid"/> reuse four buffers for the whole grid rather than allocating six
    /// arrays per sample. A closed-form built-in sets this; the SDD does not (it overrides
    /// <see cref="EvaluateGrid"/> outright, with a far larger win).
    /// </summary>
    protected virtual bool HasEvaluateInto => false;

    /// <summary>
    /// The allocation-free form of <see cref="Evaluate(in PortVoltages)"/>: the same arithmetic,
    /// written into CALLER-OWNED buffers. Implementations must write (or clear) every entry — the
    /// buffers are reused across samples, so anything left unwritten is the previous sample's value.
    /// <paramref name="i"/> and <paramref name="q"/> are length <see cref="PortCount"/>;
    /// <paramref name="dg"/> and <paramref name="dc"/> are <c>PortCount × PortCount</c>.
    /// </summary>
    protected virtual void EvaluateInto(in PortVoltages v, double[] i, double[] q, double[,] dg, double[,] dc)
        => throw new NotSupportedException($"{GetType().Name} does not implement EvaluateInto");

    /// <summary>
    /// Evaluate every sample of a time grid in one call, writing into <paramref name="into"/>.
    ///
    /// <para><paramref name="portVoltages"/> is <c>[port][t]</c> and <paramref name="controlCurrents"/>
    /// is <c>[control][t]</c>, both row-major with stride <paramref name="sampleCount"/>; pass an
    /// empty span for controls when the device has none. The result buffers are the caller's and are
    /// reused across iterations — this method shapes and fills them, and allocates nothing per
    /// sample when the model overrides it.</para>
    ///
    /// <para>The default is the scalar path applied once per sample and copied in, so every model
    /// supports the call whether or not it sets <see cref="PrefersGridEvaluate"/>.</para>
    /// </summary>
    public virtual void EvaluateGrid(
        ReadOnlySpan<double> portVoltages, ReadOnlySpan<double> controlCurrents,
        int sampleCount, GridResult into)
    {
        int P = PortCount;
        int C = sampleCount > 0 ? controlCurrents.Length / sampleCount : 0;
        into.EnsureShape(P, C, sampleCount);
        into.ClearBlocks();
        into.ResetTerms();

        // A model with a closed-form Evaluate says so by overriding EvaluateInto, and then the
        // grid loop writes through four buffers allocated ONCE for the whole grid instead of six
        // arrays per sample. There is no control-current form of it — only the SDD has controls,
        // and it overrides EvaluateGrid outright.
        if (HasEvaluateInto && C == 0)
        {
            var pv = new double[P];
            var bi = new double[P];
            var bq = new double[P];
            var bdg = new double[P, P];
            var bdc = new double[P, P];
            for (int t = 0; t < sampleCount; t++)
            {
                for (int p = 0; p < P; p++) pv[p] = portVoltages[p * sampleCount + t];
                EvaluateInto(new PortVoltages(pv), bi, bq, bdg, bdc);
                for (int p = 0; p < P; p++)
                {
                    into.I[into.PortBase(p) + t] = bi[p];
                    into.Q[into.PortBase(p) + t] = bq[p];
                    for (int q = 0; q < P; q++)
                    {
                        into.Dg[into.JacBase(p, q) + t] = bdg[p, q];
                        into.Dc[into.JacBase(p, q) + t] = bdc[p, q];
                    }
                }
            }
            return;
        }

        var portV = new double[P];
        var ctrlV = new double[C];
        for (int t = 0; t < sampleCount; t++)
        {
            for (int p = 0; p < P; p++) portV[p] = portVoltages[p * sampleCount + t];
            for (int c = 0; c < C; c++) ctrlV[c] = controlCurrents[c * sampleCount + t];

            var r = C > 0
                ? Evaluate(new PortVoltages(portV), new ControlCurrents(ctrlV))
                : Evaluate(new PortVoltages(portV));

            for (int p = 0; p < P; p++)
            {
                into.I[into.PortBase(p) + t] = r.I[p];
                into.Q[into.PortBase(p) + t] = r.Q[p];
                for (int q = 0; q < P; q++)
                {
                    into.Dg[into.JacBase(p, q) + t] = r.Dg[p, q];
                    into.Dc[into.JacBase(p, q) + t] = r.Dc[p, q];
                }
                for (int c = 0; c < C; c++)
                {
                    if (r.DControl is not null) into.DControl[into.CtrlBase(p, c) + t] = r.DControl[p, c];
                    if (r.DControlCharge is not null) into.DControlCharge[into.CtrlBase(p, c) + t] = r.DControlCharge[p, c];
                }
            }

            // Buckets are discovered on the first sample: which w values a device produces is a
            // property of its equations, not of the operating point.
            if (t == 0)
                foreach (var term in r.Terms) into.AddTerm(term.W);
            var live = into.LiveTerms;
            for (int b = 0; b < live.Length && b < r.Terms.Count; b++)
            {
                var src = r.Terms[b];
                var dst = live[b];
                for (int p = 0; p < P; p++)
                {
                    dst.Value[into.PortBase(p) + t] = src.Value[p];
                    for (int q = 0; q < P; q++) dst.Jac[into.JacBase(p, q) + t] = src.Jac[p, q];
                    if (src.JacCtrl is not null)
                        for (int c = 0; c < C; c++) dst.JacCtrl[into.CtrlBase(p, c) + t] = src.JacCtrl[p, c];
                }
            }
        }
    }

    /// <summary>
    /// Frequency-domain weighting function H[w](ω).
    /// w=0 → 1 (identity/conductance), w=1 → jω (charge/capacitance).
    /// SDD overrides this for w≥2 (user-defined H[w] expressions, brief #3).
    /// Every other device inherits the built-ins and never sees w≥2.
    /// </summary>
    public virtual Complex Weight(int w, double omega) => w switch
    {
        0 => Complex.One,
        1 => new Complex(0, omega),
        _ => throw new NotSupportedException($"{GetType().Name}: H[{w}] is not defined")
    };

    /// <summary>
    /// Small-signal linear contribution of a nonlinear device, linearized at the supplied bias
    /// operating point. Stamps Y[p,q] = Σ_w H[w](ω)·∂I[p,w]/∂V_q|_bias as an N-port
    /// admittance block, using the same port→node-pair convention as NonlinearDcEngine
    /// (port p spans Nodes[2p],Nodes[2p+1]). Linear-only engines (S-parameter, future linear-AC)
    /// call this for Kind==Nonlinear devices instead of Stamp(); HB/DC never call it.
    /// Base default suits every nonlinear device (NonlinearC, SDD); override only for special cases.
    /// </summary>
    public virtual void StampLinearized(IMnaContext mna, ElaboratedComponent c, double omega, in PortVoltages bias)
    {
        var r = Evaluate(bias);
        int P = PortCount;
        for (int p = 0; p < P; p++)
        {
            int np = c.Nodes.Length > 2 * p     ? c.Nodes[2 * p]     : 0;
            int nm = c.Nodes.Length > 2 * p + 1 ? c.Nodes[2 * p + 1] : 0;
            for (int q = 0; q < P; q++)
            {
                int qp = c.Nodes.Length > 2 * q     ? c.Nodes[2 * q]     : 0;
                int qm = c.Nodes.Length > 2 * q + 1 ? c.Nodes[2 * q + 1] : 0;
                // Y[p,q] = Σ_w H[w](ω) · ∂I[p,w]/∂V_q; w=0,1 are the fast path.
                Complex y = new Complex(r.Dg[p, q], omega * r.Dc[p, q]);
                foreach (var term in r.Terms)
                    y += Weight(term.W, omega) * term.Jac[p, q];
                if (y == Complex.Zero) continue;
                mna.AddBlockAdmittance(np, qp,  y);
                mna.AddBlockAdmittance(np, qm, -y);
                mna.AddBlockAdmittance(nm, qp, -y);
                mna.AddBlockAdmittance(nm, qm,  y);
            }
        }
    }
}

/// <summary>Port voltage vector passed to Evaluate (Phase 3+).</summary>
public readonly struct PortVoltages(double[] voltages)
{
    public double[] Voltages { get; } = voltages;
    public double this[int i] => Voltages[i];
}

/// <summary>Control currents _c1.._cm seeded for the SDD (empty for every other device).</summary>
public readonly struct ControlCurrents(double[] values)
{
    public double[] Values { get; } = values;
    public int Count => Values.Length;
    public double this[int i] => Values[i];
    public static readonly ControlCurrents Empty = new([]);
}

/// <summary>
/// One higher-weighting-function (w≥2) bucket returned by ComponentModel.Evaluate.
/// The port current contribution is: H[w](ω) · FT{Value[p](v(t))}.
/// </summary>
public readonly struct WeightedTerm
{
    public int       W     { get; }     // weighting index ≥ 2
    public double[]  Value { get; }     // per-port time-domain value of I[p,w]
    public double[,] Jac   { get; }     // ∂Value[p]/∂v[q]
    /// <summary>∂Value[p]/∂_cn (port × control-index); null when no control currents (brief #3 §2).</summary>
    public double[,]? JacCtrl { get; }

    public WeightedTerm(int w, double[] value, double[,] jac, double[,]? jacCtrl = null)
    {
        W = w; Value = value; Jac = jac; JacCtrl = jacCtrl;
    }
}

/// <summary>
/// Result returned by ComponentModel.Evaluate (Phase 3+).
/// i=port currents (w=0), q=port charges (w=1), dg=di/dv, dc=dq/dv.
/// Terms carries optional higher-w (w≥2) buckets; empty for all built-in devices.
/// DControl carries ∂I[p,0]/∂_cn (port × control-index); null when no control currents.
/// </summary>
public readonly struct NonlinearResult
{
    public double[]  I     { get; }
    public double[]  Q     { get; }
    public double[,] Dg    { get; }
    public double[,] Dc    { get; }
    public IReadOnlyList<WeightedTerm> Terms { get; }
    public double[,]? DControl { get; }
    /// <summary>∂Q[p]/∂_cn (port × control-index), charge/w=1 path; null when no control currents (brief #3 §2).</summary>
    public double[,]? DControlCharge { get; }

    // Existing 4-arg ctor — Terms = [], DControl = null (fast path, unchanged).
    public NonlinearResult(double[] i, double[] q, double[,] dg, double[,] dc)
    {
        I = i; Q = q; Dg = dg; Dc = dc; Terms = []; DControl = null; DControlCharge = null;
    }

    // Extended 5-arg ctor — carries higher-w buckets; DControl = null.
    public NonlinearResult(double[] i, double[] q, double[,] dg, double[,] dc,
        IReadOnlyList<WeightedTerm>? terms)
    {
        I = i; Q = q; Dg = dg; Dc = dc; Terms = terms ?? []; DControl = null; DControlCharge = null;
    }

    // 6-arg ctor — carries higher-w buckets and control-current (w=0) sensitivity.
    public NonlinearResult(double[] i, double[] q, double[,] dg, double[,] dc,
        IReadOnlyList<WeightedTerm>? terms, double[,]? dControl)
    {
        I = i; Q = q; Dg = dg; Dc = dc; Terms = terms ?? []; DControl = dControl; DControlCharge = null;
    }

    // 7-arg ctor — also carries the charge (w=1) control-current sensitivity (brief #3 §2).
    public NonlinearResult(double[] i, double[] q, double[,] dg, double[,] dc,
        IReadOnlyList<WeightedTerm>? terms, double[,]? dControl, double[,]? dControlCharge)
    {
        I = i; Q = q; Dg = dg; Dc = dc; Terms = terms ?? []; DControl = dControl; DControlCharge = dControlCharge;
    }
}

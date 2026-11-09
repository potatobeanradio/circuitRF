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
    /// <summary>
    /// How many BRANCH equations this model owns — voltage relations it constrains, each costing a
    /// branch-current unknown and a row of its own in the Newton system.
    ///
    /// <para>Zero for every model but the behavioural voltage source. It is asked because a NONLINEAR
    /// model that owns a branch has to have its <see cref="Stamp"/> called during the engines'
    /// linear pass — that is where a branch unknown is allocated, and where the constant half of its
    /// constraint row is written — while every other nonlinear model contributes nothing there and
    /// is skipped.</para>
    /// </summary>
    public virtual int BranchEquationCount => 0;

    /// <summary>
    /// The matrix branch index of each branch equation, in the order
    /// <see cref="NonlinearResult.BranchResidual"/> states them, as allocated by the most recent
    /// <see cref="Stamp"/> or <see cref="StampLinearized"/>.
    ///
    /// <para><b>Per-run, never carried between engines.</b> Branch numbering is a property of one
    /// assembly: the DC engine, the S-parameter engine and the harmonic-balance extractor each build
    /// their own, so an index resolved for one is not an index in another. This is the same rule the
    /// SDD's control-branch indices already follow.</para>
    /// </summary>
    public virtual IReadOnlyList<int> BranchIndices => [];

    public virtual void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
        => throw new NotImplementedException($"{GetType().Name}.Stamp is not implemented");

    /// <summary>
    /// Nonlinear contribution — Phase 3 (HB). Not called in Phase 1.
    /// Base default: not a nonlinear model.
    /// </summary>
    public virtual NonlinearResult Evaluate(in PortVoltages v)
        => throw new NotSupportedException($"{GetType().Name} is not a nonlinear model");

    // ── Noise (brief NZ-4) ────────────────────────────────────────────────────

    /// <summary>
    /// The noise sources this model declares: each a current between two of its terminals, internal nodes included
    /// (<see cref="NoiseSourceDecl"/>). Empty — the default — is a model with no noise of its own, which the noise analysis
    /// names. A model declares only the sources its parameters turn on, so a device with every noise parameter at its
    /// default is named exactly as it was before it had any.
    /// </summary>
    public virtual IReadOnlyList<NoiseSourceDecl> NoiseSources => [];

    /// <summary>
    /// Each of <see cref="NoiseSources"/>' densities at the INSTANTANEOUS state <paramref name="v"/> — the same port
    /// voltages <see cref="Evaluate(in PortVoltages)"/> takes — and the device temperature <paramref name="kelvin"/>, for
    /// one device (the caller applies the multiplier). In the S-parameter analysis the state is the DC operating point; in
    /// a harmonic-balance noise analysis it is each sample of the large-signal waveform, which is what makes the densities
    /// cyclostationary without a second model. A model with a thermal node reads its rise from <paramref name="v"/>, as
    /// <c>Evaluate</c> does.
    /// </summary>
    public virtual void EvaluateNoise(in PortVoltages v, double kelvin, Span<NoiseDensity> into) { }

    /// <summary>
    /// The densities of <see cref="NoiseSources"/> at the bias <paramref name="bias"/> and each of
    /// <paramref name="frequenciesHz"/>: <c>[k, s]</c>, single-sided A²/Hz, for one device. The default evaluates
    /// <see cref="EvaluateNoise"/> once and applies each density's frequency law; a model whose densities come from
    /// elsewhere (an external device's worker) answers here instead. Null when the model declares no sources.
    /// </summary>
    public virtual double[,]? NoiseDensitiesAt(
        ElaboratedComponent c, in PortVoltages bias, double kelvin, IReadOnlyList<double> frequenciesHz)
    {
        var decl = NoiseSources;
        if (decl.Count == 0) return null;
        var d = new NoiseDensity[decl.Count];
        EvaluateNoise(bias, kelvin, d);
        var psd = new double[frequenciesHz.Count, decl.Count];
        for (int k = 0; k < frequenciesHz.Count; k++)
            for (int s = 0; s < decl.Count; s++)
                psd[k, s] = d[s].At(frequenciesHz[k]);
        return psd;
    }

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
        => StampAdmittanceBlock(mna, c, omega, Evaluate(bias));

    /// <summary>
    /// The stamping half of <see cref="StampLinearized"/>, over an ALREADY-EVALUATED result — so the
    /// passivated path (<see cref="StampLinearizedPassive"/>) writes its masked Jacobian through the
    /// same six lines the ordinary path uses, rather than through a second copy of them that could
    /// drift.
    /// </summary>
    protected void StampAdmittanceBlock(
        IMnaContext mna, ElaboratedComponent c, double omega, in NonlinearResult r)
    {
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

    // ── The NDF passivation contract (brief-wsprobe-6 R-wsp6-3) ───────────────

    /// <summary>
    /// Whether this model carries a dependent source, and if so whether circuitRF can render it
    /// passive exactly — see <see cref="Core.Activity"/> for what each answer means and
    /// <c>docs/design/stability-wsprobe.md</c> §11 for the table every model in this repository
    /// appears in.
    ///
    /// <para><b>Answered by TYPE, and conservatively.</b> A model whose activity depends on DATA it
    /// has not read yet answers <see cref="Core.Activity.BlackBox"/> here and refines it in
    /// <see cref="ActivityFor"/>, where the component and the run's frequency grid are available:
    /// default-deny, so a model that never looks is refused rather than assumed harmless.</para>
    /// </summary>
    public virtual Activity Activity => Activity.Passive;

    /// <summary>
    /// <see cref="Activity"/> for one placed instance over the frequencies a run will visit — the
    /// hook a DATA-dependent model (an <c>SnP</c> file, a <c>Chain</c>'s ABCD expressions) uses to
    /// upgrade its conservative type-level answer to <see cref="Core.Activity.Passive"/> once it has
    /// measured itself. Everything else inherits the type-level answer unchanged.
    /// </summary>
    public virtual Activity ActivityFor(ElaboratedComponent c, IReadOnlyList<double> freqsHz) => Activity;

    /// <summary>
    /// What a refusal or an <c>explain --analysis</c> listing should add to the bare enum value —
    /// the measured σ_max of an S-parameter block, say. Null on every model whose answer speaks for
    /// itself. Set by <see cref="ActivityFor"/> where the answer is measured.
    /// </summary>
    public virtual string? PassivationNote => null;

    /// <summary>
    /// The LINEAR contribution with every dependent source in it rendered passive — the stamp that
    /// builds <c>Δ0</c> (Eq. 181). The default forwards to <see cref="Stamp"/>, which is correct by
    /// definition for a <see cref="Core.Activity.Passive"/> model and for one whose passivation is
    /// applied by re-elaborating the netlist (<see cref="Core.Activity.ActiveUserScaled"/>).
    ///
    /// <para><b>The branch count must not change.</b> The active and passive assemblies are
    /// subtracted entry by entry, so a passivated model that allocates a different number of branch
    /// unknowns shifts every later row and column. A controlled voltage source passivates to a
    /// <i>zero-gain</i> source — a 0 V branch, i.e. a short — not to no branch at all.</para>
    /// </summary>
    public virtual void StampPassive(IMnaContext mna, ElaboratedComponent c, double omega)
        => Stamp(mna, c, omega);

    /// <summary>
    /// The linearised contribution of a nonlinear device with its controlled sources removed. The
    /// default forwards to <see cref="StampLinearized"/>; an <see cref="Core.Activity.ActiveExact"/>
    /// nonlinear family overrides <see cref="ControlledConductances"/> instead of this, and gets the
    /// masking below for free.
    /// </summary>
    public virtual void StampLinearizedPassive(
        IMnaContext mna, ElaboratedComponent c, double omega, in PortVoltages bias)
    {
        var ctl = ControlledConductances;
        if (ctl.Count == 0 && !PassivatesTranscapacitance)
        {
            StampLinearized(mna, c, omega, bias);
            return;
        }
        StampAdmittanceBlock(mna, c, omega, PassivateBlock(Evaluate(bias)));
    }

    /// <summary>
    /// The <c>(p, q)</c> entries of the linearised conductance block <c>∂I_p/∂V_q</c> that ARE the
    /// model's dependent sources — a FET's <c>gm = ∂I_d/∂V_gs</c>, a BJT's transport current, a
    /// MOSFET's <c>gm</c> and <c>gmbs</c>. Zeroed by <see cref="PassivateBlock"/>; everything else
    /// (every junction conductance, every <c>gds</c>) is kept, because a two-terminal nonlinearity
    /// linearises to a conductance and a conductance is passive.
    ///
    /// <para>Empty on every model that has none, which is the default.</para>
    /// </summary>
    public virtual IReadOnlyList<(int P, int Q)> ControlledConductances => [];

    /// <summary>
    /// The ports whose voltage is a STATE in a unit of its own rather than a circuit voltage — a temperature rise, a
    /// trap state, a dispersion current — each referenced to ground. Their KCL rows are not in amperes (a thermal row is
    /// in watts), which harmonic balance's line search has to know to weigh them against the circuit's rows: it judges
    /// a norm scaled by each row's self-admittance when any interface node is one (MC-25). Empty for every model whose
    /// ports are all circuit voltages.
    /// </summary>
    public virtual IReadOnlyList<int> StatePorts => [];

    /// <summary>
    /// The longest time constant of a state this model holds that is slower than its own RF behaviour — a trap's
    /// emission, a thermal pole — in seconds at the model's operating temperature, with what holds it ("drain-lag
    /// state"). An envelope run's parallel segments must warm up past it. Declared rather than linearised: at an
    /// operating point where a trap is capturing, the linearisation reports the nanosecond capture rate, not the
    /// emission that sets how long the state remembers. Null, the default, for a model that holds no such state.
    /// </summary>
    public virtual (double Seconds, string State)? SlowestTimeConstant() => null;

    /// <summary>
    /// Whether <see cref="PassivateBlock"/> should also remove the ANTISYMMETRIC part of the charge
    /// Jacobian <c>∂Q_p/∂V_q</c> — the transcapacitance.
    ///
    /// <para><b>Why the antisymmetric part is exactly the transcapacitance.</b> A two-terminal
    /// <c>C(V)</c> between the port-<c>p</c> pair and the port-<c>q</c> pair contributes
    /// <c>dc[p,q] = dc[q,p]</c>: it is reciprocal, and it is the "capacitances evaluated at bias"
    /// the document says to keep. A charge at <c>p</c> that responds to <c>V_q</c> WITHOUT a matching
    /// response of <c>Q_q</c> to <c>V_p</c> is not a capacitor at all — it is a controlled source,
    /// and it is the half of <c>dc</c> that survives <c>dc − dcᵀ</c>. Dropping it is what makes
    /// <c>Y + Yᴴ ⪰ 0</c> attainable at every ω: the Hermitian part of <c>Dg + jω·Dc</c> is
    /// <c>(Dg + Dgᵀ) + jω(Dc − Dcᵀ)</c>, whose imaginary half grows without bound while a passive
    /// block's does not.</para>
    ///
    /// <para>True wherever <see cref="ControlledConductances"/> is non-empty by default, because the
    /// two travel together; the built-in FET family happens to have a symmetric <c>dc</c> already,
    /// so for it this changes nothing at all.</para>
    /// </summary>
    public virtual bool PassivatesTranscapacitance => ControlledConductances.Count > 0;

    /// <summary>
    /// <paramref name="r"/> with <see cref="ControlledConductances"/> zeroed and, when
    /// <see cref="PassivatesTranscapacitance"/>, the charge Jacobian symmetrised. Currents and
    /// charges are carried through untouched — the passive assembly has every independent source off
    /// and reads only the Jacobian.
    /// </summary>
    /// <summary>
    /// The device's own linearised port-admittance block <c>Y_dev(ω)</c> at a bias — <c>P × P</c>,
    /// in the model's own port coordinates — with (<paramref name="passivated"/>) or without its
    /// dependent sources. <b>Null when the model has no port-admittance form at all</b>, which is
    /// every linear model but the two that override this.
    ///
    /// <para>R-wsp6-5's passivity guard is taken of this: the passivated block must satisfy
    /// <c>Y + Yᴴ ⪰ 0</c> at every frequency, and the ACTIVE block of a biased transistor must NOT —
    /// otherwise the check distinguishes nothing. The eigenvalue arithmetic lives in the engine,
    /// which has the dense linear algebra; this only supplies the block.</para>
    /// </summary>
    public virtual Complex[,]? LinearisedPortAdmittance(
        ElaboratedComponent c, double omega, in PortVoltages bias, bool passivated)
    {
        if (Kind != ModelKind.Nonlinear) return null;

        var r = Evaluate(bias);
        if (passivated) r = PassivateBlock(r);

        int P = PortCount;
        var y = new Complex[P, P];
        for (int p = 0; p < P; p++)
            for (int q = 0; q < P; q++)
            {
                Complex v = new Complex(r.Dg[p, q], omega * r.Dc[p, q]);
                foreach (var term in r.Terms) v += Weight(term.W, omega) * term.Jac[p, q];
                y[p, q] = v;
            }
        return y;
    }

    protected NonlinearResult PassivateBlock(in NonlinearResult r)
    {
        int P = PortCount;
        var dg = (double[,])r.Dg.Clone();
        foreach (var (p, q) in ControlledConductances)
            if (p >= 0 && p < P && q >= 0 && q < P) dg[p, q] = 0.0;

        var dc = r.Dc;
        if (PassivatesTranscapacitance)
        {
            dc = (double[,])r.Dc.Clone();
            for (int p = 0; p < P; p++)
                for (int q = p + 1; q < P; q++)
                {
                    double m = 0.5 * (dc[p, q] + dc[q, p]);
                    dc[p, q] = m;
                    dc[q, p] = m;
                }
        }

        return new NonlinearResult(r.I, r.Q, dg, dc, r.Terms, r.DControl, r.DControlCharge,
                                   r.BranchResidual, r.DBranchV, r.DBranchC);
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

    /// <summary>
    /// The right-hand side of each BRANCH equation the model owns — the value <c>g(v, i)</c> that
    /// the constrained port's voltage is held at. Null for every device that constrains no voltage,
    /// which is all of them but the behavioural voltage source.
    ///
    /// <para><b>Why this is a separate block rather than another port current.</b> A device that
    /// states <c>V(a) − V(b) = g(…)</c> is a Group-2 element: the relation is between node voltages,
    /// and no combination of currents expresses it. It needs a branch-current unknown of its own and
    /// a row in the Newton system that is not a KCL row — which is what these three arrays fill in.
    /// The engine reads them at the branch rows the model allocated during its stamp.</para>
    ///
    /// <para><b>There is deliberately no charge counterpart.</b> The residual is algebraic in
    /// <c>v</c> and <c>i</c>; a nonlinear charge driven through such a source lives entirely in the
    /// linear capacitor the source drives, where <c>jkω·C·V</c> applied to the harmonics of the
    /// stored charge is already exactly its time derivative. A <c>Q</c> term here would count it
    /// twice.</para>
    /// </summary>
    public double[]? BranchResidual { get; }

    /// <summary>∂g_k/∂v_q — branch equation × port. Null when <see cref="BranchResidual"/> is.</summary>
    public double[,]? DBranchV { get; }

    /// <summary>
    /// ∂g_k/∂_cn — branch equation × control-index. Null when the equation reads no branch current.
    ///
    /// <para><b>Not an edge case.</b> A behavioural voltage source whose expression is a function of
    /// another source's branch current is an ordinary thing to write, and the first real device that
    /// works at all leans on it twice.</para>
    /// </summary>
    public double[,]? DBranchC { get; }

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

    // 10-arg ctor — also carries the BRANCH block, for a device that constrains a port voltage.
    public NonlinearResult(double[] i, double[] q, double[,] dg, double[,] dc,
        IReadOnlyList<WeightedTerm>? terms, double[,]? dControl, double[,]? dControlCharge,
        double[]? branchResidual, double[,]? dBranchV, double[,]? dBranchC)
    {
        I = i; Q = q; Dg = dg; Dc = dc; Terms = terms ?? []; DControl = dControl; DControlCharge = dControlCharge;
        BranchResidual = branchResidual; DBranchV = dBranchV; DBranchC = dBranchC;
    }
}

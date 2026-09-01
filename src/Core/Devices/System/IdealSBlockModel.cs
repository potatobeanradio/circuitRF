using System.Numerics;
using CircuitRF.Core.Elaboration;

// NAMESPACE, deliberately: the FOLDER is `System` (brief-sys-2) but the namespace is the flat
// `CircuitRF.Core.Devices` the sibling models already use. A namespace segment literally spelled
// `System` shadows the BCL root from every file inside it — `System.Numerics.Complex` would be
// looked up as `CircuitRF.Core.Devices.System.Numerics.Complex` and fail — and the only cures are
// a `global::` prefix on every BCL reference or an alias. Neither is worth a directory name.
namespace CircuitRF.Core.Devices;

/// <summary>
/// The shared stamp for an ideal N-port given by its S-matrix. Every block in the system-level
/// series except the amplifier is one of these; a subclass supplies only its own
/// <see cref="FillS"/>.
///
/// <para><b>The stamp.</b> One branch-current unknown <c>i_p</c> per port, with <c>v_p</c> the port
/// voltage across that port's own ± pair — the 2N-net convention <see cref="ZPortModel"/> and
/// <see cref="MixerModel"/> already use, so <c>Nodes[2p]</c> is port p+ and <c>Nodes[2p+1]</c> is
/// port p−. For per-port reference impedances <c>Z0_p</c>:</para>
/// <code>
///   constraint row p:   (v_p − conj(Z0_p)·i_p)/√Re(Z0_p)
///                         −  Σ_q S_pq·(v_q + Z0_q·i_q)/√Re(Z0_q)  =  0
///   KCL:                i_p flows Nodes[2p] → Nodes[2p+1]   (into the device, as everywhere here)
/// </code>
/// <para>which is <c>b = S·a</c> written out, with the common factor of 2 in the wave definitions
/// cancelled off the row.</para>
///
/// <para><b>The reference impedances may be COMPLEX</b>, and the rows above are already the general
/// form: <c>a_p = (v_p + Z0_p·i_p)/2√Re(Z0_p)</c>, <c>b_p = (v_p − conj(Z0_p)·i_p)/2√Re(Z0_p)</c> —
/// Kurokawa POWER waves, the same definition <c>SParameterEngine</c> extracts its own S with, so a
/// block and the ports measuring it agree by construction. The conjugate on <c>b</c> is the whole of
/// the generalisation; with a real <c>Z0</c> every term reduces to what it was.</para>
///
/// <para><b>Reference impedance and PRESENTED impedance differ by a conjugate, and the PARAMETER
/// NAME says which one it is.</b> Kurokawa's <c>S_pp = 0</c> is <c>Z_seen = conj(Z0_p)</c>, so one
/// of the two has to be the number a user types, and the repository's rule is the obvious one:</para>
/// <list type="bullet">
/// <item><description>A parameter spelled <c>Z0</c> — the attenuator's, the switch's, the
/// circulator's, the coupler's — IS the reference impedance, and reaches this constructor
/// unchanged.</description></item>
/// <item><description>A parameter spelled <c>Zin</c>/<c>Zout</c> is the impedance that port
/// PRESENTS, because that is what the name says and what a designer knows about the part they are
/// modelling. <see cref="FilterModel"/>, <see cref="AmplifierModel"/> and <c>DuplexerModel</c>
/// conjugate it on the way in; that is the ONLY place in the family a conjugate appears, and the
/// stamp below is the textbook form in the reference impedance either way.</description></item>
/// </list>
///
/// <para>So a filter stated at <c>Zin = 5 + j100</c> PRESENTS <c>5 + j100</c>, and is
/// conjugate-matched — maximum power transfer — by a <c>Term</c> at <c>Z = 5 − j100</c>. Every block
/// with a real port impedance is unaffected by any of this, a real number being its own
/// conjugate.</para>
///
/// <para>The NONLINEAR half of this family is real-valued and cannot take a complex reference — see
/// <see cref="RefuseComplexZ0"/>, which is called before either mechanism is built.</para>
///
/// <para><b>Why this and not Z or Y.</b> It is the DEFINITION of S rather than a transformation of
/// it, so it has no singular case, and the singular cases are the ones a system block diagram is
/// made of. The ideal circulator <c>S = [[0,0,1],[1,0,0],[0,1,0]]</c> has <c>det(I−S) = 0</c>
/// exactly and therefore no Z matrix at all. The ideal through <c>S = [[0,1],[1,0]]</c> — a closed
/// switch, a lowpass at DC — has no Y. Both fall out of the rows above with nothing special done:
/// the through reduces to <c>v1 = v2</c>, <c>i1 = −i2</c>, an ideal wire, and the ideal open
/// <c>S = I</c> reduces to <c>i_p = 0</c>. Those are states a switch is actually PLACED in, which is
/// why <see cref="ChainModel"/>'s argument — stamp the form that does not degenerate — is repeated
/// here one level up.</para>
///
/// <para><b>Three rules this class owns, so no subclass can get them wrong.</b></para>
/// <list type="number">
/// <item><description><b><c>S(−ω) = conj(S(ω))</c>.</b> <see cref="FillS"/> is always asked at
/// <c>|ω|</c> and the result conjugated when the caller passed a negative one, so a subclass never
/// writes the rule and cannot forget it. A block with a real S never notices; the quadrature coupler
/// (SYS-3/SYS-4) is wrong without it. <i>Measured, not assumed:</i> the HB linear extractor does NOT
/// currently hand a model a negative ω — <c>HbEngine.ExtractMix</c> extracts at <c>|ω|</c> and
/// conjugates the whole <c>Y</c> and Norton vector itself (HbEngine.cs:738, :1015), and
/// <c>HbLinearExtractor</c> keys its caches on the ω it is given, including an explicit DC entry. So
/// this rule is inert on today's engine paths and correct on any that stop conjugating for the
/// model. It costs one line.</description></item>
/// <item><description><b><c>Re(Z0_p) &gt; 0</c>.</b> A reference impedance with no positive
/// resistive part is not a port: <c>√Re(Z0)</c> would be NaN and the failure would surface as a
/// non-convergence with nothing attached to it. It falls back to 50 Ω instead, exactly as
/// <see cref="MixerModel"/>'s constructor does with its port impedances. The REACTIVE part is
/// unrestricted — see the complex-Z0 paragraph below.</description></item>
/// <item><description><b>The net count</b> is validated in the <c>Elaborator</c>, naming the
/// instance, for every model in this family at once — see its <c>ExpectedNetCount</c>.</description>
/// </item>
/// </list>
///
/// <para><b>Ideal means the entry is ABSENT, not small.</b> The buffer handed to
/// <see cref="FillS"/> is cleared first, and <see cref="Stamp"/> skips a zero S entry entirely, so a
/// freshly placed block puts no leakage term into the matrix rather than a 1e-10 one — the same
/// standard <see cref="MixerModel"/>'s <c>IsolationOff</c> sets. <see cref="SuppressedAmplitude"/>
/// is where a stated "off" number becomes an exact zero.</para>
/// </summary>
public abstract class IdealSBlockModel : ComponentModel
{
    /// <summary>
    /// At or above this many dB, a SUPPRESSION (a return loss, an isolation) means the term is not
    /// there at all and <see cref="SuppressedAmplitude"/> returns exactly zero. The same 150 dB
    /// <see cref="MixerModel"/> uses, and for the same reason: far below where the number could
    /// matter to any result, and far above anything a real part claims.
    ///
    /// <para>A LOSS is not a suppression and never snaps — a 200 dB pad is a 200 dB pad, because
    /// attenuating is what the part is for.</para>
    /// </summary>
    protected const double SuppressionOffDb = 150.0;

    private readonly Complex[] _z0;
    private Complex[]? _z0Conj;                 // rule 1's Z0 half, built on first negative-omega stamp
    private readonly Complex[,] _s;

    private double _cachedOmega;
    private bool   _cacheValid;

    private PimOverlay? _pim;

    /// <param name="z0PerPort">
    /// REFERENCE impedance per port, in ohms, real or complex — what S is defined against. Its
    /// LENGTH is the port count.
    ///
    /// <para><b>Not necessarily the number a user typed.</b> A parameter named <c>Z0</c> IS this;
    /// one named <c>Zin</c>/<c>Zout</c> is the impedance the port PRESENTS, which is this
    /// conjugated — see the class remarks, and <see cref="FilterModel"/>, which is where that one
    /// <c>Complex.Conjugate</c> lives.</para>
    /// </param>
    protected IdealSBlockModel(IReadOnlyList<Complex> z0PerPort)
    {
        int n = z0PerPort.Count;
        _z0 = new Complex[n];
        for (int p = 0; p < n; p++)
            _z0[p] = z0PerPort[p].Real > 0 ? z0PerPort[p] : new Complex(50.0, 0);   // rule 2
        _s = new Complex[n, n];
        PortBranchIndices = new int[n];
        for (int p = 0; p < n; p++) PortBranchIndices[p] = -1;
    }

    public sealed override int PortCount => _z0.Length;

    /// <summary>
    /// <see cref="ModelKind.Linear"/> with passive intermod off — the exact wave-constraint stamp
    /// below, and zero cost in the HB nonlinear partition — and <see cref="ModelKind.Nonlinear"/>
    /// with it on.
    ///
    /// <para>This is legal because every engine reads <c>Kind</c> off the model INSTANCE, not off
    /// the type (<c>SParameterEngine</c>, <c>NonlinearDcEngine</c>,
    /// <c>ElaboratedComponent.IsNonlinear</c>), and because a LINEAR model may already use the
    /// 2N-net ± pair convention a nonlinear one requires — <see cref="ZPortModel"/> is the proof, so
    /// <b>the net contract does not change when a user turns PIM on</b>.</para>
    ///
    /// <para>One consequence, documented rather than hidden: <c>SParameterEngine</c> runs a
    /// nonlinear DC solve for the WHOLE netlist as soon as any component is nonlinear. Turning PIM
    /// on therefore changes what an S-parameter run does — it must not change what it reports, and
    /// <c>PassiveIntermodSParamTests</c> is where that is held.</para>
    /// </summary>
    public sealed override ModelKind Kind =>
        _pim is null && !HasOwnNonlinearity ? ModelKind.Linear : ModelKind.Nonlinear;

    /// <summary>
    /// Whether the subclass carries a nonlinearity of its OWN, separate from the passive-intermod
    /// overlay — <see cref="AmplifierModel"/>'s compression is the only one (brief-sys-5), and a
    /// block that has an intercept of its own is exactly the block the factory refuses to also give
    /// a PIM to.
    ///
    /// <para>It exists so <see cref="Kind"/> can stay sealed. The rule this class actually owns is
    /// "a block is Linear unless SOMETHING in it is not", and the whole of the family's routing —
    /// <see cref="Stamp"/> standing down, the linear engines going through
    /// <c>StampLinearized</c> — hangs off that one answer rather than off which mechanism supplied
    /// it.</para>
    /// </summary>
    protected virtual bool HasOwnNonlinearity => false;

    /// <summary>
    /// The passive-intermod overlay, or null when PIM is off — which is the default and is what
    /// every block in brief-sys-2 and brief-sys-3 stays at unless a user asks otherwise.
    /// </summary>
    public PimOverlay? Pim => _pim;

    /// <summary>
    /// Turns on the passive-intermod overlay. A subclass calls this as the LAST statement of its
    /// constructor — the overlay is derived from the block's own S, so it cannot be built before
    /// <see cref="FillS"/> can answer.
    ///
    /// <para>A stated level at or below <see cref="PimOverlay.PimOffDbm"/> builds nothing at all and
    /// leaves the block exactly <see cref="ModelKind.Linear"/>, which is the "ideal means the term
    /// is ABSENT" standard the rest of this family keeps. Every refusal
    /// (<see cref="PimOverlay.Build"/>) is raised HERE, at construction, where the block can be
    /// named — never inside a Newton iteration, where nothing on the stack can say which instance
    /// it was.</para>
    /// </summary>
    /// <param name="pimDbm">The stated third-order product level, dBm, at <paramref name="outPort"/>.</param>
    /// <param name="pimPcDbm">Power per carrier that level was stated at, dBm.</param>
    /// <param name="inPort">Port the two carriers enter, 0-based.</param>
    /// <param name="outPort">Port the product level is stated at, 0-based.</param>
    protected void EnablePim(double pimDbm, double pimPcDbm, int inPort, int outPort)
    {
        if (PimOverlay.IsOff(pimDbm)) return;
        RefuseComplexZ0("passive intermod");
        _pim = PimOverlay.Build(SAt(0.0), RealZ0(), pimDbm, pimPcDbm, inPort, outPort, GetType().Name);
    }

    /// <summary>
    /// Branch indices per port, set during each <see cref="Stamp"/>; -1 before the first.
    /// The same field <see cref="ZPortModel"/> and <see cref="SnpModel"/> publish, and it has the
    /// same consequence: one model instance was never stampable from two threads at once.
    /// </summary>
    public int[] PortBranchIndices { get; }

    /// <summary>
    /// Whether this block's own impedance PARAMETERS name what each port presents (<c>Zin</c>,
    /// <c>Zout</c>) rather than the reference (<c>Z0</c>) — which is the same thing as whether the
    /// subclass conjugated on the way into the constructor. Read only when a refusal has to quote a
    /// number back to the user in the spelling they typed it.
    /// </summary>
    protected virtual bool PortParameterIsPresentedImpedance => false;

    /// <summary>The REFERENCE impedance of port <paramref name="port"/>, in ohms, after rule 2 —
    /// what S is defined against. Complex whenever the block was given one.</summary>
    public Complex Z0Of(int port) => _z0[port];

    /// <summary>The impedance port <paramref name="port"/> PRESENTS when its <c>S_pp</c> is zero —
    /// <c>conj(Z0)</c>, and the same number on every block whose reference is real.</summary>
    public Complex PortZOf(int port) => Complex.Conjugate(_z0[port]);

    /// <summary>
    /// The per-port reference impedances as plain resistances — the real parts, which are the whole
    /// value on every block that has not been given a complex one. For the nonlinear half, which is
    /// real-valued by construction and is guarded by
    /// <see cref="RefuseComplexZ0"/> before it is ever reached.
    /// </summary>
    private double[] RealZ0()
    {
        var r = new double[_z0.Length];
        for (int p = 0; p < _z0.Length; p++) r[p] = _z0[p].Real;
        return r;
    }

    /// <summary>
    /// Refuses a complex reference impedance on a path that cannot carry one, NAMING the component
    /// and the remedy.
    ///
    /// <para>The linear wave stamp takes any complex <c>Z0</c> (Kurokawa power waves — see
    /// <see cref="StampWaveConstraints"/>). The NONLINEAR half of this family does not: both
    /// <see cref="PimOverlay"/> and <see cref="AmplifierModel"/>'s compression are written as a real
    /// <c>i = f(v)</c> built from a real admittance matrix, and a complex <c>Z0</c> makes that
    /// admittance complex — which is a second quadrature bucket and a second calibration, not a
    /// wider type. Refused at CONSTRUCTION, where the instance can be named, rather than as a NaN
    /// inside a Newton iteration.</para>
    /// </summary>
    /// <param name="what">The feature being asked for, named in the message.</param>
    protected void RefuseComplexZ0(string what)
    {
        for (int p = 0; p < _z0.Length; p++)
        {
            if (_z0[p].Imaginary == 0.0) continue;

            // The message quotes what the USER typed, which is the reference on a Z0-parameter block
            // and its conjugate on a Zin/Zout one. Quoting the stored reference on the latter would
            // print a number with the wrong sign of reactance beside the one they can see on screen.
            var z = PortParameterIsPresentedImpedance ? PortZOf(p) : Z0Of(p);
            throw new InvalidOperationException(
                $"{GetType().Name}: a complex port impedance ({z.Real:G6}"
              + $"{(z.Imaginary < 0 ? " - j" : " + j")}{Math.Abs(z.Imaginary):G6} ohm at port "
              + $"{p + 1}) cannot be combined with {what}. The nonlinearity is a real, memoryless "
              + $"i = f(v) built from this block's admittance matrix, and a complex port impedance "
              + $"makes that admittance complex. Use a real impedance here, or leave the block "
              + $"linear and put the reactance on the component that terminates it.");
        }
    }

    /// <summary>
    /// The block's own S-matrix at <paramref name="omega"/> ≥ 0, written IN PLACE into a buffer this
    /// class owns and has already cleared. A frequency-flat block computes its S once in its
    /// constructor and copies it in; a frequency-dependent one (SYS-6) evaluates here.
    ///
    /// <para>Never called with a negative ω — see rule 1 above.</para>
    /// </summary>
    protected abstract void FillS(double omega, Complex[,] s);

    /// <summary>
    /// The S-matrix this block stamps at <paramref name="omega"/>, with rule 1 applied. Public so a
    /// test can gate the stamp against the S the parameters state without going through a solve.
    /// The returned buffer is the model's own and is overwritten by the next call.
    /// </summary>
    public Complex[,] SAt(double omega)
    {
        if (_cacheValid && _cachedOmega.Equals(omega)) return _s;

        Array.Clear(_s);
        FillS(Math.Abs(omega), _s);

        // Rule 1: S(−ω) = conj(S(ω)).
        if (omega < 0)
            for (int p = 0; p < PortCount; p++)
            for (int q = 0; q < PortCount; q++)
                _s[p, q] = Complex.Conjugate(_s[p, q]);

        _cachedOmega = omega;
        _cacheValid  = true;
        return _s;
    }

    /// <summary>
    /// Invalidates the cached S. Only needed by a subclass whose S can change after construction;
    /// nothing in SYS-2 does, and a frequency-dependent block keys on ω rather than mutating.
    /// </summary>
    protected void InvalidateS() => _cacheValid = false;

    /// <summary>
    /// The reference impedances this block stamps at <paramref name="omega"/>. Rule 1 applies to
    /// them too: a physical reference impedance is conjugate-symmetric, <c>Z0(-w) = conj(Z0(w))</c>,
    /// exactly as <c>S(-w) = conj(S(w))</c> is. Inert on a real Z0, and inert on today's engine
    /// paths for the reason rule 1 already records — one array, built once, so it cannot be the
    /// thing a complex-Z0 block gets wrong later.
    /// </summary>
    public IReadOnlyList<Complex> Z0At(double omega)
    {
        if (omega >= 0) return _z0;
        if (_z0Conj is null)
        {
            _z0Conj = new Complex[_z0.Length];
            for (int p = 0; p < _z0.Length; p++) _z0Conj[p] = Complex.Conjugate(_z0[p]);
        }
        return _z0Conj;
    }

    /// <summary>
    /// The wave-constraint stamp — the linear path, and the ONLY path when passive intermod is off.
    ///
    /// <para>With PIM on — or on a block with a nonlinearity of its own, which is the amplifier —
    /// the block is <see cref="ModelKind.Nonlinear"/> and the linear engines call
    /// <see cref="ComponentModel.StampLinearized"/> instead; this body then does nothing, exactly as
    /// <see cref="MixerModel"/>'s does, so a mis-routed call cannot stamp the same block twice.</para>
    /// </summary>
    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        if (Kind is ModelKind.Nonlinear) return;
        StampWaveConstraints(mna, c.Nodes, SAt(omega), Z0At(omega), PortBranchIndices);
    }

    /// <summary>
    /// The wave-constraint stamp itself, as a free function over an EXPLICIT node list rather than
    /// over a component's own.
    ///
    /// <para><b>Why it is separable at all.</b> A component that is one S-matrix stamps this once,
    /// which is <see cref="Stamp"/> above. A component that is SEVERAL S-matrices sharing nets
    /// stamps it several times — that is exactly what the duplexer is (brief-sys-6: two filters and
    /// one antenna node, four branch currents, no internal node, and the junction interaction, the
    /// TX-to-RX isolation and each arm's out-of-band reflection all falling out of the shared node
    /// rather than being separate parameters). Sharing the code rather than a second copy of it is
    /// what makes "no new mathematics at all" literally true.</para>
    ///
    /// <para>The branches are allocated by this call and written back into
    /// <paramref name="branchOut"/>; every one of them is allocated before ANY row is stamped,
    /// because a constraint row references the other ports' branch columns.</para>
    /// </summary>
    /// <param name="mna">The matrix being assembled.</param>
    /// <param name="nodes">2N node indices, <c>[p0+, p0−, p1+, p1−, …]</c>.</param>
    /// <param name="s">The N×N scattering matrix to stamp.</param>
    /// <param name="z0">Per-port reference impedance, ohms, real or complex, all with Re &gt; 0.</param>
    /// <param name="branchOut">Receives the N branch indices; length N.</param>
    /// <param name="nodeOffset">
    /// Index into <paramref name="nodes"/> of this block's first port pair, so one component's node
    /// list can carry several blocks. Zero for a component that is one matrix.
    /// </param>
    /// <param name="portNodes">
    /// Which entry of <paramref name="nodes"/> each port's ± pair sits at, or null for the ordinary
    /// consecutive layout. The duplexer's second arm is ports {ANT, RX} out of {ANT, TX, RX}, which
    /// is not consecutive and is the whole reason this exists.
    /// </param>
    public static void StampWaveConstraints(
        IMnaContext mna, int[] nodes, Complex[,] s, IReadOnlyList<Complex> z0, int[] branchOut,
        int nodeOffset = 0, IReadOnlyList<int>? portNodes = null)
    {
        int n = branchOut.Length;
        int NodeOf(int port, int side) =>
            nodes[nodeOffset + 2 * (portNodes is null ? port : portNodes[port]) + side];

        for (int p = 0; p < n; p++) branchOut[p] = mna.AddBranch();

        for (int p = 0; p < n; p++)
            mna.AddBranchCurrent(branchOut[p], NodeOf(p, 0), NodeOf(p, 1));

        // The two current coefficients each row needs, per port, computed once.
        //
        // For a REAL reference both are √Z0, and they are computed AS √Z0 rather than as the
        // algebraically identical Z0/√Z0 — which differs in the last bit and would have moved every
        // existing block's stamp by an ulp for no reason. The general forms are what a complex
        // reference needs: conj(Z0)/√Re(Z0) on the row's OWN branch (the b-wave's conjugate) and
        // Z0/√Re(Z0) on the incident side (the a-wave's).
        var root = new double[n];
        var back = new Complex[n];
        var fwd  = new Complex[n];
        for (int p = 0; p < n; p++)
        {
            root[p] = Math.Sqrt(z0[p].Real);
            bool real = z0[p].Imaginary == 0.0;
            back[p] = real ? new Complex(root[p], 0) : Complex.Conjugate(z0[p]) / root[p];
            fwd[p]  = real ? new Complex(root[p], 0) : z0[p] / root[p];
        }

        for (int p = 0; p < n; p++)
        {
            int    br = branchOut[p];
            int    np = NodeOf(p, 0), nm = NodeOf(p, 1);
            double rp = root[p];

            // (v_p − conj(Z0_p)·i_p)/√Re(Z0_p)
            mna.AddConstraint(br, np, new Complex(1.0 / rp, 0));
            if (nm > 0) mna.AddConstraint(br, nm, new Complex(-1.0 / rp, 0));
            mna.AddBranchConstraint(br, br, -back[p]);

            // − Σ_q S_pq·(v_q + Z0_q·i_q)/√Re(Z0_q)
            for (int q = 0; q < n; q++)
            {
                Complex spq = s[p, q];
                if (spq == Complex.Zero) continue;   // an ideal term is absent, not 1e-10 of one

                int    qp = NodeOf(q, 0), qm = NodeOf(q, 1);
                double rq = root[q];

                mna.AddConstraint(br, qp, -spq / rq);
                if (qm > 0) mna.AddConstraint(br, qm, spq / rq);
                mna.AddBranchConstraint(br, branchOut[q], -spq * fwd[q]);
            }
        }
    }

    // ── The passive-intermod half (brief-sys-4) ───────────────────────────────

    /// <summary>
    /// <c>i = Re(Y)·v + Re(N)·ψ(R·v)</c>, plus the <c>H[2]</c> bucket carrying the imaginary halves
    /// when the block's S is complex. See <see cref="PimOverlay"/> for the derivation, for why the
    /// limiter is applied to the wave INCIDENT on each port rather than to the port voltage, and for
    /// the closed form the calibration inverts.
    ///
    /// <para>Charge is identically zero — an ideal passive block stores none, so the device is
    /// frequency-flat at every harmonic the solver retains apart from the quadrature bucket, which
    /// is a sign and not a slope.</para>
    /// </summary>
    public override NonlinearResult Evaluate(in PortVoltages v)
    {
        var pim = _pim ?? throw new NotSupportedException(
            $"{GetType().Name} has no passive intermod, so it is not a nonlinear model");

        int n = PortCount;
        var i  = new double[n];
        var q  = new double[n];
        var dg = new double[n, n];
        var dc = new double[n, n];

        // stackalloc rather than two arrays a sample: EvaluateInto is called once per time-grid
        // point per Newton iteration, and PortCount is 2, 3 or 4 for every block in this family.
        Span<double> psi  = stackalloc double[n];
        Span<double> dpsi = stackalloc double[n];
        pim.EvaluateInto(v, i, dg, psi, dpsi);

        if (!pim.HasQuadrature)
            return new NonlinearResult(i, q, dg, dc);

        var value = new double[n];
        var jac   = new double[n, n];
        pim.EvaluateQuadratureInto(v, psi, dpsi, value, jac);
        return new NonlinearResult(i, q, dg, dc, [new WeightedTerm(QuadratureW, value, jac)]);
    }

    /// <inheritdoc/>
    public override bool PrefersGridEvaluate => _pim is not null;

    /// <summary>
    /// The allocation-free grid path — but ONLY for a block with no quadrature bucket.
    ///
    /// <para>The base <see cref="ComponentModel.EvaluateGrid"/>'s <c>EvaluateInto</c> shortcut fills
    /// I/Q/Dg/Dc and nothing else: it carries no <c>Terms</c>, because no model that took it had
    /// any. A block whose S is complex does, so it stays on the scalar path where the bucket
    /// survives. Saying so here rather than overriding <c>EvaluateGrid</c> keeps the one behaviour
    /// that matters — the bucket reaching the solver — impossible to lose to an optimisation.</para>
    /// </summary>
    protected override bool HasEvaluateInto => _pim is { HasQuadrature: false };

    /// <inheritdoc/>
    protected override void EvaluateInto(in PortVoltages v, double[] i, double[] q, double[,] dg, double[,] dc)
    {
        int n = PortCount;
        for (int p = 0; p < n; p++)
        {
            q[p] = 0.0;
            for (int r = 0; r < n; r++) dc[p, r] = 0.0;
        }

        Span<double> psi  = stackalloc double[n];
        Span<double> dpsi = stackalloc double[n];
        _pim!.EvaluateInto(v, i, dg, psi, dpsi);
    }

    /// <summary>
    /// The weighting index this family uses for the quadrature bucket. 2 is the first index above
    /// the two built-ins (<c>H[0] = 1</c>, <c>H[1] = jω</c>), and nothing else in a netlist can
    /// collide with it — a bucket is looked up on the model that produced it.
    /// </summary>
    private const int QuadratureW = 2;

    /// <summary>
    /// <c>H[2](ω) = j·sign(ω)</c> — the frequency-domain factor that lets a block with a genuinely
    /// complex S host a memoryless nonlinearity.
    ///
    /// <para>It is <c>+j·sign(ω)</c> and not <c>−j·sign(ω)</c> because the bucket it weights is
    /// <c>Im(Y)</c> and <c>Im(N)</c>: for ω &gt; 0 the two halves recombine as
    /// <c>Re(Y) + j·Im(Y) = Y</c>, and for ω &lt; 0 as <c>conj(Y)</c>, which is the same
    /// <c>S(−ω) = conj(S(ω))</c> rule this class already keeps for the linear stamp. (brief-sys-4
    /// writes <c>+j·sign(ω)</c> in its mechanism and the series brief writes <c>−j·sign(ω)</c> in
    /// its overview; the sign is fixed by which matrix the bucket carries, and a wrong one passes
    /// every amplitude test and fails only the quadrature gate.)</para>
    ///
    /// <para>At ω = 0 it is exactly zero, so the whole bucket drops out of a DC solve — a
    /// frequency-domain factor has to.</para>
    /// </summary>
    public override Complex Weight(int w, double omega)
        => w == QuadratureW && _pim is not null
         ? new Complex(0.0, Math.Sign(omega))
         : base.Weight(w, omega);

    // ── dB helpers, shared by every block in the series ────────────────────────

    /// <summary>
    /// The amplitude ratio a positive number of dB names: <c>10^(−db/20)</c>. Taken literally at
    /// every value — this is for a LOSS or a GAIN, which is the number the part exists to have.
    /// </summary>
    protected static double AmplitudeFromDb(double db) => Math.Pow(10.0, -db / 20.0);

    /// <summary>
    /// The amplitude ratio a positive number of dB of SUPPRESSION names — a return loss, an
    /// isolation — snapped to EXACTLY zero at or above <see cref="SuppressionOffDb"/>, so a
    /// default-ideal block stamps no entry there at all.
    /// </summary>
    protected static double SuppressedAmplitude(double db)
        => db >= SuppressionOffDb ? 0.0 : AmplitudeFromDb(db);
}

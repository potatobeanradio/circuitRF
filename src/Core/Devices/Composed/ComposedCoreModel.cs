using CircuitRF.Core.Devices.Composed.Traps;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices.Composed;

/// <summary>
/// The nonlinear core of a composed FET (D3, R-mc1-6): <c>Ig</c>, <c>Id</c>, <c>Qg</c> and <c>Qd</c> of the
/// intrinsic device on the nets <c>gi di si</c>, scaled by <c>N/N0</c>.
///
/// <para><b>Ports.</b> Two, sharing the source: port 0 = (gate, source), port 1 = (drain, source), so
/// <c>v[0]</c> is <c>vgs</c> and <c>v[1]</c> is <c>vds</c> — <see cref="Fet.FetModelBase"/>'s
/// arrangement, and the elaborator expands the three nets the same way.</para>
///
/// <para><b>Currents.</b> The gate current is <c>IgGs + IgGd</c>, the two branches of [P1] Fig. 6.30;
/// the gate–drain branch leaves through the drain, so the drain terminal carries
/// <c>Id − IgGd</c>.</para>
///
/// <para><b>Charges.</b> <c>q = (Qg, Qd)</c> and <c>dc = ∂q/∂v</c> come from the same evaluation, so the
/// model is terminal-charge-conserving by construction (D7) — <c>Qs = −Qg − Qd</c> is the KCL the
/// engine already writes. A transcapacitance (<c>∂Qd/∂vgs ≠ ∂Qg/∂vds</c>) is kept, not symmetrised. A drift
/// decomposition (MC-8, <see cref="DriftCharges"/>) reads the channel current of the same evaluation.</para>
///
/// <para><b>The internal-node seam</b> used by the thermal, trap and dispersion phases:
/// <see cref="InternalStateNodeCount"/> ground-referenced nodes, minted by the elaborator after the two
/// intrinsic ports as ordinary global unknowns — never eliminated locally, because their harmonic
/// content matters in HB.</para>
///
/// <para><b>DC/RF dispersion</b> (R-mc7-4, [P1] eq. 6.52) is the first user: one state node <c>x</c>, port
/// 2, whose voltage is a current in amps. The drain carries <c>IdRF + x</c>, and the node's own port
/// carries <c>i = x − (IdDC − IdRF)</c> with <c>q = τ·x</c>, so KCL there is
/// <c>τ dx/dt + x = IdDC − IdRF</c>. At DC <c>x = IdDC − IdRF</c> and the drain carries <c>IdDC</c>; far
/// above 1/τ, <c>x</c> cannot follow and the small-signal drain current is <c>IdRF</c>'s. Both currents
/// are taken in the terminal frame (after the drain–source image), so <c>x</c> means the same on either
/// side of <c>vds = 0</c>. The node is unscaled — <c>N/N0</c> multiplies the drain current it feeds.</para>
///
/// <para><b>Traps</b> (MC-10 R-mc10-1, [X10] eqs. 1.1–1.5) are the second user: one state node per enabled trap state,
/// ports 2 … in the states' order, whose voltage is the state in volts. The trap model supplies each node's current and
/// charge (<see cref="TrapDynamics"/>) from the intrinsic <c>vgs</c>, <c>vds</c> — never their drain–source image, which a
/// model with traps does not have — and <c>T</c>; the states are inputs of <c>Id</c> and <c>Qd</c>, so their lanes'
/// gradients land in those ports' columns. Under a <c>Shift</c> coupling (MC-19) <c>Id</c> takes none itself and is
/// evaluated at a gate voltage the states shift (<see cref="TrapShift"/>), its state columns from the chain rule; the
/// charges, a drift charge's reading of the current included, take no shift. The nodes are unscaled, like the dispersion
/// node.</para>
///
/// <para><b>Self-heating</b> (MC-9 R-mc9-2, [P1] §6.6.2) adds a fourth net, the thermal node, as the last port
/// <c>(t, 0)</c>: its voltage is the temperature rise ΔT and every constituent is evaluated at
/// <c>T = temp + ΔT</c>. The port carries <c>−P</c>, the dissipated power <c>Id·vds + IgGs·vgs + IgGd·vgd</c>
/// ([P1] eq. 6.67) in its terminal form <c>ig·vgs + id·vds</c>, so the power flows INTO the node, where the
/// synthesised cell's <c>Rth ∥ Cth</c> to ground closes [P1] eq. 6.67. It is not a minted unknown: the cell owns the
/// net, which is what lets an instance's fourth pin be it. <c>P</c> is the scaled (whole-device) power, and
/// <c>Rth</c> scales by <c>N0/N</c>, so ΔT does not depend on <c>N</c>.</para>
///
/// <para><b>Instance switches</b> (brief-modelcomposer-21, <see cref="ComposedSwitches"/>) change how THIS instance evaluates
/// the model, never the model. <c>Charge=false</c> zeroes both intrinsic charges. <c>LinearC=true</c> replaces them by
/// their tangent at the bias point the engine froze (<see cref="IBiasFrozen"/>): <c>q(v) = q(v_Q) + C(v_Q)·(v − v_Q)</c>, the
/// temperature and trap inputs held at the frozen values; unfrozen, the core refuses to return a charge outside a DC
/// solve (<see cref="ChargeReading"/>). <c>GateLag=false</c> / <c>DrainLag=false</c> remove the states of that capture
/// direction (<see cref="TrapDynamics.CaptureSign"/>): each takes its DC value at the instantaneous voltages, its lane's
/// gradient folds through <see cref="TrapDynamics.DcValue(int, double, double, double, Span{double})"/>, and its node is
/// not minted. <c>SelfHeating=false</c> evaluates at the ambient and drives no power into the thermal port, which stays,
/// so a bound pin 4 still connects. <c>Dispersion=false</c> conducts <c>IdDC</c> and mints no dispersion node.</para>
///
/// <para><b>Branch switches</b> (brief-modelcomposer-22, <see cref="BranchLinearisation"/>): <c>LinearCgs</c>, <c>LinearCdg</c>,
/// <c>LinearCds</c> and <c>LinearCm</c> hold one circuit-set capacitance at its bias-point value and let the others follow
/// the model. The whole charge is then read with its temperature and trap inputs at the frozen point's values — the
/// surface <c>Q̃(vgs, vds)</c> the construction acts on, in the terminal frame, so a line across <c>vds = 0</c> under
/// <c>Exchange</c> needs nothing special.</para>
/// </summary>
public sealed class ComposedCoreModel : ComponentModel, IBiasFrozen
{
    private readonly ComposedCoreProgram _p;
    private readonly double _scale;
    private readonly double _temperatureC;
    private readonly bool   _hasT;
    private readonly bool   _exchange;
    private readonly bool   _dispersive;
    private readonly double _tau;
    private readonly bool   _thermal;     // the thermal port exists (the model self-heats)
    private readonly bool   _heats;       // ... and this instance lets it (SelfHeating)
    private readonly TrapDynamics? _traps;
    private readonly int    _allTraps;    // the model's enabled states
    private readonly int    _trapCount;   // the states this instance keeps as nodes
    private readonly int[]  _kept;        // node j holds state _kept[j]
    private readonly int[]  _nodeOf;      // state k's node, or −1 when its lag is switched off
    private readonly bool   _dcGradient;  // the dynamics supply DcValue's gradient overload
    private readonly ChargeBranches _branches;   // the branches held at the bias point, none under LinearC
    private Frozen? _frozen;
    private readonly double _rth, _cth;   // the instance's thermal overrides, NaN for the model's own

    /// <summary>The model this core evaluates.</summary>
    public LoadedComposedModel Loaded { get; }

    /// <summary>The instance's finger count.</summary>
    public int N { get; }

    /// <summary>The switches this instance evaluates the model with.</summary>
    public ComposedSwitches Switches { get; }

    /// <summary>The ComposedFET instance this core belongs to, for a refusal; set by the elaborator.</summary>
    public string Label { get; internal set; } = "";

    /// <summary>The temperature the constituents are evaluated at, °C — the device's (its <c>Temp</c>, else the run's
    /// ambient) clamped to the model's range, or the model's own <c>T0</c> for a single-temperature model.</summary>
    public double TemperatureC => _temperatureC;

    /// <summary>The temperature this device was asked to run at, °C, before the model's range held it.</summary>
    public double RequestedC { get; }

    /// <param name="rth">The instance's own <c>Rth</c>, °C/W, or NaN for the model's; with <paramref name="cth"/>, only what
    /// <see cref="SlowestTimeConstant"/> reports, since the cell's thermal network is stamped by the cell's R and C.</param>
    public ComposedCoreModel(LoadedComposedModel loaded, int n, double ambientC, ComposedSwitches? switches = null,
                             double rth = double.NaN, double cth = double.NaN)
    {
        _rth = rth;
        _cth = cth;
        Loaded   = loaded;
        N        = n;
        Switches = switches ?? ComposedSwitches.Default;
        _branches = Switches.LinearC ? ChargeBranches.None : Switches.FrozenBranches;
        _p     = loaded.Program;
        _scale = (double)n / loaded.N0;
        _exchange = _p.Symmetry == DrainSourceSymmetry.Exchange;
        _tau = _p.DispersionTau ?? 0.0;
        _dispersive = _p.IdRF is not null && Switches.Dispersion;
        _thermal = loaded.Model.Thermal is not null;
        _heats = _thermal && Switches.SelfHeating;
        _traps = _p.Traps;
        _allTraps = _traps?.States.Count ?? 0;

        _nodeOf = new int[_allTraps];
        var kept = new List<int>(_allTraps);
        for (int k = 0; k < _allTraps; k++)
        {
            bool gate = _traps!.CaptureSign(k) < 0;
            bool keep = gate ? Switches.GateLag : Switches.DrainLag;
            _nodeOf[k] = keep ? kept.Count : -1;
            if (keep) kept.Add(k);
        }
        _kept = [.. kept];
        _trapCount = _kept.Length;
        _dcGradient = _trapCount < _allTraps && SuppliesDcGradient(_traps!);

        var t = loaded.Model.Temperature;
        _hasT = t.Mode == TemperatureMode.Range;
        RequestedC = ambientC;
        _temperatureC = _hasT ? Math.Clamp(ambientC, t.Tmin, t.Tmax) : t.T0;
    }

    // ── Noise (brief NZ-4) ────────────────────────────────────────────────────

    /// <summary>
    /// The core's noise sources, from the model's <c>Core.Noise</c>: <c>4k·Td·gds</c> across the intrinsic drain–source
    /// when <c>Td</c> is stated, and flicker <c>Kf·|Id|^Af / f</c> there when <c>Kf</c> is. <c>Tg</c> is not a source of
    /// the core: it is the temperature of the network's <c>Ri</c>, which the noise analysis takes thermal at it
    /// (<see cref="GateNoiseKelvin"/>). No block, or one with neither, declares none.
    /// </summary>
    public override IReadOnlyList<NoiseSourceDecl> NoiseSources => _noiseDecl ??= BuildNoiseSources();
    private IReadOnlyList<NoiseSourceDecl>? _noiseDecl;

    private IReadOnlyList<NoiseSourceDecl> BuildNoiseSources()
    {
        var list = new List<NoiseSourceDecl>();
        if (Loaded.Model.Core.Noise is not { } n) return list;
        if (n.Td is not null) list.Add(NoiseSourceDecl.OnPort("thermal_gds", 1));
        if (n.Kf != 0) list.Add(NoiseSourceDecl.OnPort("flicker", 1));
        return list;
    }

    /// <summary>The device's own temperature at a state, kelvin: the temperature it was asked to run at plus the thermal
    /// node's rise when it heats itself. What the network's parasitics are thermal at.</summary>
    public double DeviceKelvin(in PortVoltages v)
        => Temperature.ToKelvin(RequestedC + (_heats && v.Voltages.Length > ThermalPort ? v[ThermalPort] : 0.0));

    /// <summary><c>Core.Noise.Tg</c> at a state, kelvin, or null when the model states none.</summary>
    public double? GateNoiseKelvin(in PortVoltages v)
        => Loaded.Model.Core.Noise?.Tg is { } tg ? NoiseTemperature(tg, _p.NoiseTg, v) : null;

    /// <summary>A Noise temperature at a state: the constant, or the constituent at the core's vgs, vds and temperature.</summary>
    private double NoiseTemperature(NoiseValue value, ComposedFunction? compiled, in PortVoltages v)
    {
        if (value.Constant is { } k) return k;
        Span<double> seeds = stackalloc double[_p.Width];
        Span<double> grad  = stackalloc double[_p.Width];
        _p.DcSeeds(v[0], v[1], _temperatureC + (_heats ? v[ThermalPort] : 0.0), seeds);
        return _p.Eval(compiled!, "Noise", seeds, grad);
    }

    /// <inheritdoc/>
    public override void EvaluateNoise(in PortVoltages v, double kelvin, Span<NoiseDensity> into)
    {
        var n = Loaded.Model.Core.Noise!;
        int P = PortCount;
        var i = new double[P];
        var dg = new double[P, P];
        EvaluateInto(v, i, new double[P], dg, new double[P, P]);
        int k = 0;
        if (n.Td is { } td) into[k++] = NoiseDensity.Thermal(dg[1, 1], NoiseTemperature(td, _p.NoiseTd, v));
        if (n.Kf != 0) into[k] = NoiseDensity.Flicker(n.Kf, n.Af, i[1]);
    }

    public override int       PortCount => 2 + InternalStateNodeCount + (_thermal ? 1 : 0);
    public override ModelKind Kind      => ModelKind.Nonlinear;

    /// <summary>Internal state nodes the elaborator mints: one, <c>x</c>, for a dispersive model; one per trap state the
    /// instance keeps for a model with traps (the two are never combined). A switched-off part mints none.</summary>
    public int InternalStateNodeCount => _dispersive ? 1 : _trapCount;

    /// <summary>Whether the model self-heats: the core then takes a fourth net, the thermal node, as its last port —
    /// under <c>SelfHeating=false</c> too, where it drives no power into it.</summary>
    public bool HasThermalNode => _thermal;

    /// <summary>The thermal node's port, or −1.</summary>
    public int ThermalPort => _thermal ? 2 + InternalStateNodeCount : -1;

    /// <summary>The dispersion or trap nodes, then the thermal node: none of them is a circuit voltage.</summary>
    public override IReadOnlyList<int> StatePorts =>
        [.. Enumerable.Range(2, InternalStateNodeCount), .. (_thermal ? (int[])[ThermalPort] : [])];

    public override string[] TerminalNames =>
        [ "gi", "si", "di", "si", .. (_dispersive ? (string[])["x", "0"] : []),
          .. _kept.SelectMany(k => (string[])[_traps!.States[k], "0"]), .. (_thermal ? (string[])["t", "0"] : []) ];

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega) { }

    /// <summary>The slowest of the states this instance keeps: each trap state's emission time at its temperature, the
    /// dispersion τ, and each thermal pole's <c>R·C</c> as the cell stamps it (<see cref="ComposedNetworkTemplate"/>'s
    /// factors; the periphery scalings cancel). A switched-off lag, dispersion or self-heating holds nothing.</summary>
    public override (double Seconds, string State)? SlowestTimeConstant()
    {
        (double Seconds, string State)? slowest = null;
        void Take(double tau, string state)
        {
            if (double.IsFinite(tau) && tau > 0 && (slowest is null || tau > slowest.Value.Seconds)) slowest = (tau, state);
        }
        for (int j = 0; j < _trapCount; j++)
            if (_traps!.EmissionTime(_kept[j], _temperatureC) is { } emit)
                Take(emit, _traps.CaptureSign(_kept[j]) < 0 ? "gate-lag state" : "drain-lag state");
        if (_dispersive) Take(_tau, "dispersion");
        if (_heats && Loaded.Model.Thermal is { } th)
        {
            double ambient = _hasT ? _temperatureC : RequestedC;
            double rFactor = double.IsFinite(_rth) ? _rth / th.Rth : th.RthAt(ambient) / th.Rth;
            double cTotal = th.Poles.Sum(p => p.Cth);
            double cFactor = double.IsFinite(_cth) ? (cTotal > 0 ? _cth / cTotal : 0) : 1.0;
            foreach (var pole in th.Poles)
            {
                double c = double.IsFinite(_cth) && cTotal <= 0 ? _cth * pole.Rth / th.Rth : pole.Cth * cFactor;
                Take(pole.Rth * rFactor * c, "thermal pole");
            }
        }
        return slowest;
    }

    /// <summary>The channel current's <c>gm</c> is a dependent source circuitRF can switch off exactly,
    /// as for the built-in FETs.</summary>
    public override Activity Activity => Activity.ActiveExact;

    /// <summary>With dispersion the drain's dependence on <c>x</c> is the other controlled source: zeroing
    /// both leaves the drain a conductance. A trap node's dependence on the gate and drain voltages, and the drain's on the
    /// trap state, are controlled sources too: passivated, the node is a conductance of its own.
    /// The thermal coupling is a controlled source both ways — the currents' dependence on ΔT, and the power
    /// the voltages drive into the thermal node — so passivated, the thermal node is decoupled.</summary>
    public override IReadOnlyList<(int P, int Q)> ControlledConductances
    {
        get
        {
            var list = new List<(int, int)> { (1, 0) };
            if (_dispersive) list.Add((1, 2));
            for (int k = 0; k < _trapCount; k++) { list.Add((1, 2 + k)); list.Add((2 + k, 0)); list.Add((2 + k, 1)); }
            if (_thermal)
            {
                int th = ThermalPort;
                for (int p = 0; p < PortCount; p++) { list.Add((p, th)); if (p != th) list.Add((th, p)); }
            }
            return list;
        }
    }

    // ── The bias point (R-mc21-3) ────────────────────────────────────────────

    /// <summary>The intrinsic charge at the frozen point and its 2×2 capacitance, scaled, plus the port voltages it was
    /// frozen at — one immutable object, swapped in whole. <see cref="Held"/> is the seed vector there, whose lanes past
    /// the voltages a branch-frozen charge is read at; <see cref="Branch"/> the four branch capacitances of that surface at
    /// the point, unscaled.</summary>
    private sealed record Frozen(double[] At, double Qg, double Qd, double Cgg, double Cgd, double Cdg, double Cdd,
                                 double[] Held, double[] Branch);

    /// <summary>The intrinsic charge with its inputs past <c>vgs</c>, <c>vds</c> held at <paramref name="held"/>'s, in the
    /// terminal frame and unscaled — what <see cref="BranchLinearisation"/> integrates along.</summary>
    private readonly struct HeldSurface(ComposedCoreModel core, double[] held) : IChargeSurface
    {
        public bool Evaluate(double vgs, double vds, out double qg, out double qd, Span<double> c, Span<double> dC)
        {
            var p = core._p;
            int width = p.Width;
            bool image = core._exchange && vds < 0;
            Span<double> s = stackalloc double[RuntimeEvaluator.MaxInputs];
            held.CopyTo(s);
            s[0] = image ? vgs - vds : vgs;
            s[1] = image ? -vds : vds;
            Span<double> gQg = stackalloc double[RuntimeEvaluator.MaxInputs], gQd = stackalloc double[RuntimeEvaluator.MaxInputs];
            (qg, qd) = p.Charges(s[..width], gQg, gQd);

            Span<double> hg = stackalloc double[3], hd = stackalloc double[3];
            bool exact = !dC.IsEmpty && p.ChargeHessians(s[..width], hg, hd);
            if (image)
            {
                // As Core maps the image back: ∂/∂vds = −∂′/∂vgs′ − ∂′/∂vds′, Qd = −Qg′ − Qd′; a Hessian through the same
                // linear map is JᵀHJ.
                FromImage(gQg); FromImage(gQd);
                for (int k = 0; k < 2; k++) gQd[k] = -gQg[k] - gQd[k];
                qd = -qg - qd;
                if (exact)
                {
                    ImageHessian(hg); ImageHessian(hd);
                    for (int k = 0; k < 3; k++) hd[k] = -hg[k] - hd[k];
                }
            }
            c[0] = gQg[0]; c[1] = gQg[1]; c[2] = gQd[0]; c[3] = gQd[1];
            if (!exact) return false;
            // ∂C/∂vgs, then ∂C/∂vds, each in C's order (C11 C12 C21 C22).
            dC[0] = hg[0]; dC[1] = hg[1]; dC[2] = hd[0]; dC[3] = hd[1];
            dC[4] = hg[1]; dC[5] = hg[2]; dC[6] = hd[1]; dC[7] = hd[2];
            return true;
        }

        private static void ImageHessian(Span<double> h)
        {
            double h00 = h[0], h01 = h[1], h11 = h[2];
            h[1] = -h00 - h01;
            h[2] = h00 + 2 * h01 + h11;
        }
    }

    /// <summary>Only a linearised charge needs one.</summary>
    public bool NeedsBiasPoint => Switches.LinearCharge;

    /// <summary>Whether the charge has been frozen (a linear-charge instance only).</summary>
    public bool IsFrozen => _frozen is not null;

    /// <summary>The port voltages the charge was frozen at — <c>vgs</c>, <c>vds</c>, then the state nodes and the thermal
    /// node's rise, in port order — or null while unfrozen. What a Verilog-A export's bias-point parameters are read from.</summary>
    public IReadOnlyList<double>? FrozenAt => _frozen?.At;

    public bool FreezeAt(ReadOnlySpan<double> portVoltages)
    {
        if (!NeedsBiasPoint) return false;
        if (_frozen is { } f && portVoltages.SequenceEqual(f.At)) return false;

        int n = PortCount;
        Span<double> ri = stackalloc double[MaxPorts], rq = stackalloc double[MaxPorts],
                     rg = stackalloc double[MaxPorts * MaxPorts], rc = stackalloc double[MaxPorts * MaxPorts];
        Span<double> state = stackalloc double[TrapDynamics.MaxStates];
        for (int k = 0; k < _trapCount; k++) state[k] = portVoltages[2 + k];
        double dT = _thermal ? portVoltages[ThermalPort] : 0.0;
        Core(portVoltages[0], portVoltages[1], _dispersive ? portVoltages[2] : 0.0, state, dT, ri, rq, rg, rc, frozenCharge: false);

        var held = new double[_p.Width];
        Span<double> dcGrad = stackalloc double[3 * TrapDynamics.MaxStates];
        Inputs(portVoltages[0], portVoltages[1], state, dT, held, dcGrad);
        var branch = new double[4];
        if (_branches != ChargeBranches.None)
        {
            Span<double> c = stackalloc double[4];
            new HeldSurface(this, held).Evaluate(portVoltages[0], portVoltages[1], out _, out _, c, []);
            BranchLinearisation.Capacitances(c, branch);
        }
        _frozen = new Frozen(portVoltages.ToArray(), rq[0], rq[1], rc[0], rc[1], rc[n], rc[n + 1], held, branch);
        return true;
    }

    // ── Evaluation ───────────────────────────────────────────────────────────

    public override NonlinearResult Evaluate(in PortVoltages v)
    {
        int n = PortCount;
        var i  = new double[n];
        var q  = new double[n];
        var dg = new double[n, n];
        var dc = new double[n, n];
        EvaluateInto(v, i, q, dg, dc);
        return new NonlinearResult(i, q, dg, dc);
    }

    public override bool PrefersGridEvaluate => !NonlinearEvalDiagnostics.DisableGridEvaluate;

    protected override bool HasEvaluateInto => true;

    protected override void EvaluateInto(in PortVoltages v, double[] i, double[] q, double[,] dg, double[,] dc)
    {
        int n = PortCount;
        Span<double> ri = stackalloc double[MaxPorts], rq = stackalloc double[MaxPorts],
                     rg = stackalloc double[MaxPorts * MaxPorts], rc = stackalloc double[MaxPorts * MaxPorts];
        Span<double> state = stackalloc double[TrapDynamics.MaxStates];
        for (int k = 0; k < _trapCount; k++) state[k] = v[2 + k];
        Core(v[0], v[1], _dispersive ? v[2] : 0.0, state, _thermal ? v[ThermalPort] : 0.0, ri, rq, rg, rc);
        for (int p = 0; p < n; p++)
        {
            i[p] = ri[p];
            q[p] = rq[p];
            for (int col = 0; col < n; col++) { dg[p, col] = rg[p * n + col]; dc[p, col] = rc[p * n + col]; }
        }
    }

    /// <summary>
    /// The whole HB time grid in one pass, written straight into the caller's buffers (R-mc2-5): no
    /// per-sample arrays, no per-grid arrays — the base class's <see cref="EvaluateInto"/> loop still
    /// allocates its five scratch buffers once per call, and HB calls once per Newton iteration.
    /// </summary>
    public override void EvaluateGrid(ReadOnlySpan<double> portVoltages, ReadOnlySpan<double> controlCurrents,
                                      int sampleCount, GridResult into)
    {
        int n = PortCount;
        into.EnsureShape(n, 0, sampleCount);
        into.ResetTerms();
        Span<double> ri = stackalloc double[MaxPorts], rq = stackalloc double[MaxPorts],
                     rg = stackalloc double[MaxPorts * MaxPorts], rc = stackalloc double[MaxPorts * MaxPorts];
        Span<double> state = stackalloc double[TrapDynamics.MaxStates];
        int th = ThermalPort;
        for (int t = 0; t < sampleCount; t++)
        {
            for (int k = 0; k < _trapCount; k++) state[k] = portVoltages[(2 + k) * sampleCount + t];
            Core(portVoltages[t], portVoltages[sampleCount + t], _dispersive ? portVoltages[2 * sampleCount + t] : 0.0, state,
                 _thermal ? portVoltages[th * sampleCount + t] : 0.0, ri, rq, rg, rc);
            for (int p = 0; p < n; p++)
            {
                into.I[into.PortBase(p) + t] = ri[p];
                into.Q[into.PortBase(p) + t] = rq[p];
                for (int col = 0; col < n; col++)
                {
                    into.Dg[into.JacBase(p, col) + t] = rg[p * n + col];
                    into.Dc[into.JacBase(p, col) + t] = rc[p * n + col];
                }
            }
        }
    }

    /// <summary>
    /// One operating point: port currents <c>i</c>, charges <c>q</c>, and their Jacobians row-major. <paramref name="state"/>
    /// holds the KEPT trap states, in node order.
    ///
    /// <para><b>Drain–source exchange</b> (R-mc2-4). With <see cref="DrainSourceSymmetry.Exchange"/> and
    /// <c>vds &lt; 0</c>, the constituents are evaluated at the image point <c>(vgs − vds, −vds)</c>
    /// ([P1] eq. 6.61) and transformed back: the channel current changes sign, the two gate branches
    /// exchange ([P1] eq. 6.64), <c>Qg</c> carries over and <c>Qd = −Qg′ − Qd′</c> (the image's source
    /// charge, [P1] eq. 6.63 in charge form). Each gradient maps through the image's Jacobian:
    /// <c>∂/∂vgs = ∂′/∂vgs′</c>, <c>∂/∂vds = −∂′/∂vgs′ − ∂′/∂vds′</c>.</para>
    ///
    /// <para><paramref name="frozenCharge"/> false evaluates the charge as fitted whatever <c>LinearC</c> says — the freeze
    /// itself reads it so.</para>
    /// </summary>
    private void Core(double vgs, double vds, double x, ReadOnlySpan<double> state, double dT,
                      Span<double> i, Span<double> q, Span<double> dg, Span<double> dc, bool frozenCharge = true)
    {
        int n = PortCount;
        i[..n].Clear(); q[..n].Clear(); dg[..(n * n)].Clear(); dc[..(n * n)].Clear();

        bool image = _exchange && vds < 0;
        int width = _p.Width;
        Span<double> seeds = stackalloc double[RuntimeEvaluator.MaxInputs];
        Span<double> dcGrad = stackalloc double[3 * TrapDynamics.MaxStates];
        double temperature = Inputs(vgs, vds, state, dT, seeds, dcGrad);
        seeds[0] = image ? vgs - vds : vgs;
        seeds[1] = image ? -vds : vds;
        int trapLane = _p.TrapLane;
        var s = seeds[..width];

        const int W = RuntimeEvaluator.MaxInputs;
        Span<double> gId = stackalloc double[W], gGs = stackalloc double[W], gGd = stackalloc double[W],
                     gQg = stackalloc double[W], gQd = stackalloc double[W], gRf = stackalloc double[W];

        double id   = _p.Drain(s, gId);
        double igGs = _p.Eval(_p.IgGs, "IgGs", s, gGs);
        double igGd = _p.Eval(_p.IgGd, "IgGd", s, gGd);
        double qg   = _p.Eval(_p.Qg,   "Qg",   s, gQg);
        double qd   = _p.Eval(_p.Qd,   "Qd",   s, gQd);
        double idRf = _dispersive ? _p.Eval(_p.IdRF!, "IdRF", s, gRf) : 0.0;
        if (_p.Drift is { } drift)
        {
            // The drift charge reads the unshifted current: no charge takes a shift coupling (MC-19 R-mc19-1). It reads the
            // model's own channel whatever Dispersion says — the switch changes the drain, not the charge.
            Span<double> gD = stackalloc double[W];
            bool own = _p.IdRF is not null || _p.Shift is not null;
            double idD = own ? _p.DriftChannel(s, gD) : id;
            if (!own) gId.CopyTo(gD);
            drift.Evaluate(s[0], s[1], idD, gD, _p.Width, out qg, gQg, out qd, gQd);
        }

        if (_trapCount < _allTraps)
            for (int k = 0; k < _allTraps; k++)
                if (_nodeOf[k] < 0)
                {
                    var d = dcGrad.Slice(3 * k, 3);
                    int lane = trapLane + k;
                    Fold(gId, lane, d); Fold(gGs, lane, d); Fold(gGd, lane, d); Fold(gQg, lane, d); Fold(gQd, lane, d);
                }

        if (image)
        {
            FromImage(gId); FromImage(gGs); FromImage(gGd); FromImage(gQg); FromImage(gQd); FromImage(gRf);
            id = -id;
            idRf = -idRf;
            for (int k = 0; k < width; k++)   // the T lane maps as the voltages do: the image changes no temperature
            {
                gId[k] = -gId[k];
                gRf[k] = -gRf[k];
                (gGs[k], gGd[k]) = (gGd[k], gGs[k]);
                gQd[k] = -gQg[k] - gQd[k];
            }
            (igGs, igGd) = (igGd, igGs);
            qd = -qg - qd;
        }

        double k0 = _scale;
        // The voltage lanes are columns 0 and 1; the T lane is the thermal port's column when the model self-heats and
        // the instance lets it, and no column otherwise (T is then the run's constant); a kept trap state's lane is its
        // node's port, a removed one's has been folded away.
        int th = ThermalPort;
        int Col(int lane) => lane < 2 ? lane
                           : lane >= trapLane && _allTraps > 0 ? (_nodeOf[lane - trapLane] is >= 0 and var j ? 2 + j : -1)
                           : _heats ? th : -1;
        int lanes = width;

        i[0] = k0 * (igGs + igGd);
        q[0] = k0 * qg;
        q[1] = k0 * qd;
        for (int lane = 0; lane < lanes; lane++)
        {
            int col = Col(lane);
            if (col < 0) continue;
            dg[col]     = k0 * (gGs[lane] + gGd[lane]);
            dc[col]     = k0 * gQg[lane];
            dc[n + col] = k0 * gQd[lane];
        }

        if (!_dispersive)
        {
            i[1] = k0 * (id - igGd);
            for (int lane = 0; lane < lanes; lane++)
                if (Col(lane) is >= 0 and var col) dg[n + col] = k0 * (gId[lane] - gGd[lane]);
        }
        else
        {
            // [P1] eq. 6.52 through the state node: the drain follows IdRF + x, and x relaxes to IdDC − IdRF with τ.
            i[1] = k0 * (idRf + x - igGd);
            i[2] = x - (id - idRf);
            q[2] = _tau * x;
            for (int lane = 0; lane < lanes; lane++)
            {
                int col = Col(lane);
                if (col < 0) continue;
                dg[n + col]     = k0 * (gRf[lane] - gGd[lane]);
                dg[2 * n + col] = -(gId[lane] - gRf[lane]);
            }
            dg[n + 2] = k0;
            dg[2 * n + 2] = 1.0;
            dc[2 * n + 2] = _tau;
        }

        // The trap nodes (MC-10): each kept state's own current and charge, read at the intrinsic voltages and the device
        // temperature.
        if (_traps is { } traps)
        {
            Span<double> gi = stackalloc double[4], gq = stackalloc double[4];
            for (int j = 0; j < _trapCount; j++)
            {
                int k = _kept[j];
                int sp = 2 + j, row = sp * n;
                i[sp] = traps.Evaluate(k, state[j], vgs, vds, temperature, gi, out q[sp], gq);
                if (!double.IsFinite(i[sp]) || !double.IsFinite(q[sp]) || !double.IsFinite(gi[0]))
                    throw new ComposedEvaluationException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"'{Loaded.Model.Traps!.Model}' trap state {traps.States[k]} is not finite at vgs={vgs:G6}, vds={vds:G6}, {traps.States[k]}={state[j]:G6}."));
                dg[row + sp] = gi[0]; dg[row] = gi[1]; dg[row + 1] = gi[2];
                dc[row + sp] = gq[0]; dc[row] = gq[1]; dc[row + 1] = gq[2];
                if (_heats) { dg[row + th] = gi[3]; dc[row + th] = gq[3]; }
            }
        }

        // The intrinsic charge as the instance's switches have it (R-mc21-1, R-mc21-3).
        if (!Switches.Charge)
        {
            q[0] = q[1] = 0;
            dc[..(2 * n)].Clear();
        }
        else if (Switches.LinearCharge && frozenCharge)
        {
            if (_frozen is not { } f)
            {
                if (!ChargeReading.Unread)
                    throw new ComposedEvaluationException(
                        $"'{Label}': {LinearisedWhat()} at the bias point, and the bias point was never set — nothing froze " +
                        "the quiescent DC operating point before the charge was read.");
            }
            else if (Switches.LinearC)
            {
                double d0 = vgs - f.At[0], d1 = vds - f.At[1];
                q[0] = f.Qg + f.Cgg * d0 + f.Cgd * d1;
                q[1] = f.Qd + f.Cdg * d0 + f.Cdd * d1;
                dc[..(2 * n)].Clear();
                dc[0] = f.Cgg; dc[1] = f.Cgd; dc[n] = f.Cdg; dc[n + 1] = f.Cdd;
            }
            else
            {
                // R-mc22-1: the held surface, each frozen branch's residual along the line from the bias point removed.
                Span<double> c = stackalloc double[4];
                BranchLinearisation.Evaluate(new HeldSurface(this, f.Held), _branches, f.At[0], f.At[1], f.Branch,
                                             vgs, vds, out double bqg, out double bqd, c);
                q[0] = k0 * bqg;
                q[1] = k0 * bqd;
                dc[..(2 * n)].Clear();
                dc[0] = k0 * c[0]; dc[1] = k0 * c[1]; dc[n] = k0 * c[2]; dc[n + 1] = k0 * c[3];
            }
        }

        if (!_thermal) return;
        if (!_heats) return;   // isothermal: the port stays, carrying no power

        // [P1] eq. 6.67: the dissipated power, in its terminal form, flows into the thermal node; its port carries −P.
        double p = i[0] * vgs + i[1] * vds;
        i[th] = -p;
        for (int col = 0; col < n; col++)
        {
            double dp = dg[col] * vgs + dg[n + col] * vds + (col == 0 ? i[0] : col == 1 ? i[1] : 0.0);
            dg[th * n + col] = -dp;
        }
    }

    /// <summary>
    /// Every seed lane past the voltages: <c>T</c> when it is one, then each trap state — a kept one from its node, one whose
    /// lag is switched off at its DC value at the instantaneous voltages (R-mc21-4), its gradient by <c>(vgs, vds, T)</c>
    /// into <paramref name="dcGrad"/> for its lane to fold through. Returns the device temperature.
    /// </summary>
    private double Inputs(double vgs, double vds, ReadOnlySpan<double> state, double dT, Span<double> seeds, Span<double> dcGrad)
    {
        double temperature = _temperatureC + (_heats ? dT : 0.0);
        if (_hasT) seeds[2] = temperature;
        int trapLane = _p.TrapLane;
        for (int k = 0; k < _allTraps; k++)
            seeds[trapLane + k] = _nodeOf[k] >= 0 ? state[_nodeOf[k]] : DcState(k, vgs, vds, temperature, dcGrad.Slice(3 * k, 3));
        return temperature;
    }

    /// <summary>The switches that need the bias point, for a refusal.</summary>
    private string LinearisedWhat()
    {
        if (Switches.LinearC) return "LinearC=true linearises the intrinsic charge";
        var names = BranchLinearisation.Names(_branches).ToArray();
        string set = string.Join(", ", names.Select(b => $"Linear{b}=true"));
        return names.Length == 1 ? $"{set} holds that capacitance" : $"{set} hold those capacitances";
    }

    /// <summary>A removed state's DC value at the instantaneous point, its gradient by <c>(vgs, vds, T)</c> into
    /// <paramref name="grad"/>: the dynamics' own overload, or a central difference of the value when they supply none.</summary>
    private double DcState(int k, double vgs, double vds, double t, Span<double> grad)
    {
        if (_dcGradient) return _traps!.DcValue(k, vgs, vds, t, grad);
        double v = _traps!.DcValue(k, vgs, vds, t);
        double hg = 1e-6 * Math.Max(1, Math.Abs(vgs)), hd = 1e-6 * Math.Max(1, Math.Abs(vds)), ht = 1e-4 * Math.Max(1, Math.Abs(t));
        grad[0] = (_traps.DcValue(k, vgs + hg, vds, t) - _traps.DcValue(k, vgs - hg, vds, t)) / (2 * hg);
        grad[1] = (_traps.DcValue(k, vgs, vds + hd, t) - _traps.DcValue(k, vgs, vds - hd, t)) / (2 * hd);
        grad[2] = (_traps.DcValue(k, vgs, vds, t + ht) - _traps.DcValue(k, vgs, vds, t - ht)) / (2 * ht);
        return v;
    }

    /// <summary>The chain rule for a removed lane: <c>∂/∂vgs += ∂/∂φ·∂φ/∂vgs</c>, likewise vds and T, then the lane is gone.</summary>
    private void Fold(Span<double> g, int lane, ReadOnlySpan<double> d)
    {
        g[0] += g[lane] * d[0];
        g[1] += g[lane] * d[1];
        if (_hasT) g[2] += g[lane] * d[2];
        g[lane] = 0;
    }

    private static bool SuppliesDcGradient(TrapDynamics traps)
    {
        Span<double> g = stackalloc double[3];
        try { traps.DcValue(0, 0, 0, 25, g); return true; }
        catch (NotSupportedException) { return false; }
    }

    /// <summary>The most ports a core has: two intrinsic, the state nodes (one dispersion node, or up to four trap states),
    /// the thermal node.</summary>
    private const int MaxPorts = 2 + TrapDynamics.MaxStates + 1;

    private static void FromImage(Span<double> g) => g[1] = -g[0] - g[1];
}

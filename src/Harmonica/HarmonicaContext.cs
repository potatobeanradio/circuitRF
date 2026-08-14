using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;

namespace CircuitRF.Harmonica;

/// <summary>One converged harmonicaRF operating point.</summary>
/// <param name="V">Interface node spectra, [node, harmonic].</param>
/// <param name="INl">Nonlinear device currents at the interface, [node, harmonic].</param>
/// <param name="Converged">Whether the Newton loop reached tolerance.</param>
/// <param name="Iterations">Newton iterations taken.</param>
/// <param name="PavlDbm">Available power this point was solved at.</param>
public sealed record OperatingPoint(
    Complex[,] V,
    Complex[,] INl,
    bool       Converged,
    int        Iterations,
    double     PavlDbm)
{
    /// <summary>The interface admittance and excitation this point was solved with, per harmonic.</summary>
    public required Complex[][,] YNN  { get; init; }
    public required Complex[][]  ISrc { get; init; }

    /// <summary>‖F‖ at the point the Newton loop stopped. Reported whether or not it converged.</summary>
    public required double Residual { get; init; }

    /// <summary>
    /// The TOTAL nonlinear injection at the interface, <c>i + jωq + Σ H[w]·W</c> — what the HB
    /// residual actually balances, as against <see cref="INl"/>, which is the conduction half alone.
    ///
    /// <para>Both are needed and they are not interchangeable. D1's intrinsic quantities are the
    /// conduction half by definition; anything that has to obey KCL at a node — recovering a plane
    /// voltage, or the true delivered current R-hrf-4 rests on — needs the total. Using the
    /// conduction half for the second was a real defect here, caught by the closed-form check in
    /// <c>R4_ZinIsTheDeliveredCurrentNotTheDevicesOwn</c> rather than by inspection.</para>
    /// </summary>
    public required Complex[,] INlTotal { get; init; }
}

/// <summary>
/// R-hrf-5 — one context owns one elaborated netlist and one <see cref="InterfaceNetwork"/>.
///
/// <para><b>The boundary this class exists to hold.</b> A STRUCTURAL change (the DUT, the embedding
/// stack, K, the frequency) rebuilds; a VALUE change (a termination, the drive, the bias) does not.
/// Going through a global-variable override would force a re-elaboration and is roughly a thousand
/// times the cost of the thing being changed, so it is forbidden — <see cref="SetBias"/> reaches the
/// supply models directly instead.</para>
///
/// <para><b>Terminations do not touch the netlist at all.</b> They are closed algebraically against
/// the pre-extracted interface (§6.2), so a marker move costs one 2×2 inverse per harmonic and no MNA
/// work whatsoever. This is the claim the whole product's liveness rests on.</para>
///
/// <para><b>Re-entrant-READY, not concurrent.</b> There is no static mutable state here and nothing
/// is shared between contexts, so H5's worker pool can hold one context each. The pool itself is not
/// built, deliberately (§6 of the brief).</para>
/// </summary>
public sealed class HarmonicaContext
{
    private readonly AnalysisSettings _settings;

    private CircuitModel      _model;
    private ElaboratedNetlist _netlist = null!;
    private InterfaceNetwork  _interface = null!;
    private VdcModel?         _gateSupply;
    private VdcModel?         _drainSupply;

    private HarmonicaContext(CircuitModel model, AnalysisSettings settings)
    {
        _model    = model;
        _settings = settings;
        Rebuild();
    }

    /// <summary>
    /// Builds a context for a model. <paramref name="settings"/> defaults to the loadpull engine's:
    /// inductance regularisation ALWAYS, because the ideal choke through an ideal supply is a
    /// zero-impedance DC path at the termination plane and that is what regularisation is for.
    /// </summary>
    public static HarmonicaContext Create(CircuitModel model, AnalysisSettings? settings = null)
        => new(model, settings ?? DefaultSettings(model));

    public static AnalysisSettings DefaultSettings(CircuitModel model) => new()
    {
        InductanceRegularization = RegularizationMode.Always,
        HbMaxIter                = model.Settings.MaxIter,
    };

    public CircuitModel      Model     => _model;
    public ElaboratedNetlist Netlist   => _netlist;
    public InterfaceNetwork  Interface => _interface;

    /// <summary>The generated netlist text — also what <i>Export testbench</i> (§7.8) writes.</summary>
    public string NetlistText { get; private set; } = "";

    /// <summary>How many times the netlist has been rebuilt. A value change must never move it.</summary>
    public int RebuildCount { get; private set; }

    // ── structural vs value change (R-hrf-5) ─────────────────────────────────

    /// <summary>
    /// Applies a new model. Rebuilds only if <see cref="CircuitModel.StructuralKey"/> moved; a bias
    /// or drive change mutates in place. Returns whether a rebuild happened, so a caller (and a
    /// test) can assert the boundary rather than trust it.
    ///
    /// <para><b>Idq-driven bias, R3C follow-up (2026-08-13) — this used to be entirely unimplemented.</b>
    /// <c>model.Bias.Idq</c> was round-tripped and persisted but never once read here; the applied
    /// gate voltage was always <c>model.Bias.Vgs ?? 0.0</c>, so an Idq-only document silently biased at
    /// 0 V. Now: whichever of Vgs/Vds/Idq moved re-resolves the actual gate voltage — via
    /// <see cref="SolveVgsForIdq"/> when <c>Idq</c> is set (a Vds change alone still has to re-solve,
    /// since the same Idq target needs a different Vgs at a different Vds), else <c>Vgs ?? 0.0</c> as
    /// before. <see cref="SetBias"/>'s own write leaves <c>_model.Bias.Idq</c> exactly as the top-level
    /// <c>_model = model</c> assignment above already set it — so after this returns, <c>Vgs</c> is
    /// always the real (solved-or-given) bias and <c>Idq</c>, when non-null, is the TARGET that
    /// produced it. Both fields are populated together now; "Vgs xor Idq" is no longer the invariant a
    /// caller should assume.</para>
    /// </summary>
    public bool Apply(CircuitModel model)
    {
        bool structural = model.StructuralKey != _model.StructuralKey;
        var  previous   = _model;
        _model = model;

        if (structural) { Rebuild(); return true; }

        bool biasMoved = model.Bias.Vgs != previous.Bias.Vgs
                       || model.Bias.Vds != previous.Bias.Vds
                       || model.Bias.Idq != previous.Bias.Idq;
        if (biasMoved) ResolveBias(model);
        return false;
    }

    /// <summary>Applies whichever of Vgs/Idq the model states — via <see cref="SolveVgsForIdq"/> when
    /// Idq drives, else Vgs directly — through <see cref="SetBias"/>. Shared by <see cref="Apply"/>'s
    /// value-change branch and <see cref="Rebuild"/>'s own end: a FRESH context (construction, or any
    /// structural rebuild) needs this exactly as much as a later value edit does — an Idq-driven model
    /// hand to <see cref="Create"/> for the first time must not sit at the netlist's raw default gate
    /// voltage until some later unrelated value edit happens to trigger a resolve.</summary>
    private void ResolveBias(CircuitModel model)
    {
        double vgs = model.Bias.Idq is { } idq
            ? SolveVgsForIdq(idq, model.Bias.Vds)
            : model.Bias.Vgs ?? 0.0;
        SetBias(vgs, model.Bias.Vds);
    }

    /// <summary>
    /// R-h9c-12 (R1C §6) — Refresh DUT: re-elaborates unconditionally, even though
    /// <see cref="CircuitModel.StructuralKey"/> has not moved. A DUT can change on disk between Set
    /// and Refresh (an external <c>.osdi</c> that was recompiled, a kit manifest that was edited) with
    /// none of its OWN fields moving — <see cref="Apply"/>'s key comparison is exactly the wrong tool
    /// for that, since it exists to AVOID a rebuild when nothing meaningful changed, and here the user
    /// is explicitly asserting that something did. <see cref="RebuildCount"/> increments again, so the
    /// same counter that gates "elaboration happens exactly once on Set" also gates "exactly once more
    /// on Refresh" — one shape, two user actions.
    /// </summary>
    public void ForceRebuild() => Rebuild();

    /// <summary>
    /// Moves the bias supplies in place. Their values are ordinary resolved parameters, so this is
    /// the one value change that has to reach a model object rather than an algebraic step — and the
    /// alternative, a global-variable override, re-elaborates the whole netlist.
    /// </summary>
    public void SetBias(double vgs, double vds)
    {
        _gateSupply?.SetVdc(vgs);
        _drainSupply?.SetVdc(vds);
        _model = _model with { Bias = _model.Bias with { Vgs = vgs, Vds = vds } };

        // The bias supplies are the excitation the open-port extraction captured, so the extraction
        // — and ONLY the extraction — has to be redone. That is a linear solve per harmonic, not an
        // elaboration, and it is why bias is not part of the structural key.
        ReExtract();

        // §3.3 item 1 (brief-harmonicarf-r3b) — the DC seed is a function of (structure, bias); a
        // bias move invalidates it exactly like ReExtract, not one moment later.
        _dcSeedVoltages = null;
    }

    private void Rebuild()
    {
        var generated = HarmonicaNetlist.Build(_model);
        NetlistText = generated.Text;

        var (lib, tb) = new CnlReader().Read(NetlistText);
        _netlist = new Elaborator(lib).Elaborate(tb);

        _gateSupply  = FindSupply(HarmonicaNetlist.GateSupply);
        _drainSupply = FindSupply(HarmonicaNetlist.DrainSupply);

        IntrinsicPorts = IntrinsicPortMap.For(
            _model.Dut, DutComponent.Model, _model.Embedding.Package);

        RebuildCount++;
        ReExtract();

        // §3.3 item 1 — a structural rebuild (Apply's structural branch, or ForceRebuild) always
        // invalidates too; the netlist itself may now be a different circuit.
        _dcSeedVoltages = null;

        // R3C follow-up — an Idq-driven model handed to Create() for the very first time (or a
        // structural rebuild of one — a new DUT, a new K) must not sit at the netlist's raw default
        // gate voltage until some later, unrelated value edit happens to trigger a resolve. Also
        // covers plain Vgs: harmless (ResolveBias is a no-op re-application of the same Vgs) and one
        // path is simpler than two.
        ResolveBias(_model);
    }

    private void ReExtract()
        => _interface = InterfaceNetwork.Extract(
               _netlist, _settings,
               Node(HarmonicaNetlist.SourcePlane), Node(HarmonicaNetlist.LoadPlane),
               _model.Settings.HarmonicCount, _model.Settings.FrequencyHz);

    private VdcModel? FindSupply(string instanceName)
        => _netlist.Components
                   .FirstOrDefault(c => c.InstancePath == instanceName)?.Model as VdcModel;

    /// <summary>Circuit node number for a generated net name.</summary>
    public int Node(string netName) => _netlist.Nodes.GetOrAssign(netName);

    /// <summary>Index into the HB unknown vector for a generated net name, or −1 if it is not one.</summary>
    public int InterfaceIndex(string netName) => Array.IndexOf(_interface.DeviceNodes, Node(netName));

    /// <summary>The one DUT. The topology holds exactly one nonlinear device (§1.1).</summary>
    public ElaboratedComponent DutComponent
        => _netlist.Components[_netlist.NonlinearComponents.Single()];

    /// <summary>
    /// Which ports §4.5's intrinsic quantities are read at, resolved once per REBUILD (the DUT and the
    /// package are both structural, so nothing below a rebuild can move it). An external model with no
    /// <see cref="DutSpec.IntrinsicMapping"/> reports itself unavailable here, which is what makes the
    /// intrinsic panels draw empty rather than at a guessed plane — R-h8-3.
    /// </summary>
    public IntrinsicPortMap IntrinsicPorts { get; private set; } = IntrinsicPortMap.TwoPort;

    // ── solving ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The Thévenin drive amplitude for an available power, against the fundamental source
    /// termination: <c>|Vs| = √(8·P_avl·Re Z_S(1))</c>. Same rule as <c>TunerModel.SetSourceDrive</c>.
    /// </summary>
    public static double DriveVolts(TerminationSet terminations, double pavlDbm)
    {
        double pavlW = Math.Pow(10.0, (pavlDbm - 30.0) / 10.0);
        double reZ   = terminations.Z(TerminationSide.Source, 1).Real;
        return reZ > 0 ? Math.Sqrt(8.0 * pavlW * reZ) : 0.0;
    }

    /// <summary>
    /// Solves one operating point. The terminations are closed algebraically — no MNA solve, no
    /// re-elaboration, no netlist mutation — and the Newton loop is the engine's own.
    /// </summary>
    public OperatingPoint Solve(TerminationSet terminations, double pavlDbm,
                                Complex[,]? warmStart = null)
    {
        var (yNN, iSrc) = _interface.Close(terminations, DriveVolts(terminations, pavlDbm),
                                           _model.Settings.DcBlockFarads);

        int K     = _model.Settings.HarmonicCount;
        int N     = _interface.InterfaceCount;
        int gridN = HbFft.GridSize(K, _model.Settings.FftOverSample);

        Complex[,] v = new Complex[N, K + 1];
        if (warmStart is not null && warmStart.GetLength(0) == N && warmStart.GetLength(1) == K + 1)
            Array.Copy(warmStart, v, warmStart.Length);
        else
            SeedFromRealDc(v, N);

        var result = HbNewton.Solve(v, yNN, iSrc, _model.Settings.FrequencyHz, K, N,
                                    _netlist, _interface.DeviceNodes, gridN, _settings,
                                    _model.Settings.Tol, _model.Settings.Lambda,
                                    _model.Settings.GuardHarmonic);

        var (iNl, qNl, _, _, buckets) = HbNewton.EvaluateNonlinear(
            v, N, K, gridN, _netlist, _interface.DeviceNodes);

        double omega0 = 2.0 * Math.PI * _model.Settings.FrequencyHz;
        var total = new Complex[N, K + 1];
        for (int n = 0; n < N; n++)
            for (int k = 0; k <= K; k++)
            {
                Complex t = iNl[n, k];
                if (k > 0) t += new Complex(0, k * omega0) * qNl[n, k];
                foreach (var b in buckets) t += b.Model.Weight(b.W, k * omega0) * b.WNl[n, k];
                total[n, k] = t;
            }

        return new OperatingPoint(v, iNl, result.Converged, result.Iterations, pavlDbm)
        {
            YNN      = yNN,
            ISrc     = iSrc,
            INlTotal = total,
            Residual = result.IterTrace.Count > 0 ? result.IterTrace[^1].ResidualNorm : double.NaN,
        };
    }

    // ── §3.3 item 1 (brief-harmonicarf-r3b) — the REAL nonlinear DC seed ────────

    /// <summary>
    /// Cached once per (structure, bias) — the owner's own ask: "the DC solve can be performed as
    /// soon as the DUT is loaded into memory and can always be reused throughout all harmonica
    /// calculations." Invalidated in <see cref="Rebuild"/> and <see cref="SetBias"/>. Null means "not
    /// computed yet, or invalidated" — computed lazily on first use, not eagerly, so a document that
    /// never needs a cold seed (every solve is warm-started) never pays for one.
    /// </summary>
    private double[]? _dcSeedVoltages;

    /// <summary>How many times the true nonlinear DC operating point has actually been solved.
    /// The cache's own gate — a counter, not a clock.</summary>
    public int DcSeedComputeCount { get; private set; }

    /// <summary>
    /// Seeds a cold solve from the DUT's own converged nonlinear DC operating point — the quiescent
    /// bias point, harmonics zero — rather than the linear network's DC point WITH THE DEVICE ABSENT
    /// (what this used to do: <c>V = −Y(0)⁻¹·I_src(0)</c>). The old seed was not "seeded from DC" in
    /// any sense that involves the device's own nonlinearity; it was the network's open-circuit point,
    /// which for a device biased well away from pinch-off/breakdown can be dB-scale wrong as a guess
    /// for where the real operating point sits.
    /// </summary>
    private void SeedFromRealDc(Complex[,] v, int n)
    {
        var dcV = _dcSeedVoltages ??= ComputeDcSeed();
        for (int i = 0; i < n; i++)
        {
            int nid = _interface.DeviceNodes[i];               // 1-based; 0 = ground
            v[i, 0] = nid >= 1 && nid <= dcV.Length ? new Complex(dcV[nid - 1], 0.0) : Complex.Zero;
        }
        // Harmonics 1..K stay zero — the same "zero at every harmonic" rule the old seed used.
    }

    /// <summary>
    /// The real nonlinear DC solve, against harmonicaRF's own OPEN-port netlist (the bias tees are
    /// already stamped into it — §4.4 — so this is the device's genuine quiescent point, not a guess
    /// requiring any termination). A non-convergent DC solve still returns its best-effort voltages
    /// (never null) — using them is strictly no worse than the old zero/linear seed, and the HB
    /// Newton loop's own convergence is what actually matters downstream.
    /// </summary>
    private double[] ComputeDcSeed()
    {
        DcSeedComputeCount++;
        try
        {
            var result = CircuitRF.Engine.NonlinearDcEngine.Run(_netlist);
            return result.NodeVoltages;
        }
        catch (Exception)
        {
            // A DC solve that cannot converge even under continuation stepping (NonlinearDcEngine's
            // own last resort) leaves the seed at zero — never worse than the old linear-network
            // seed's own failure mode (InterfaceNetwork.SolveDense throwing on a singular Y(0)), and
            // the HB Newton loop downstream is what actually has to converge either way.
            return [];
        }
    }

    // ── R3C follow-up — Idq ⇄ Vgs, the "1-D secant on the DC solve" the tooltips always promised ────

    /// <summary>The DUT's own DC drain current AT THE CURRENT BIAS, amps — what the strip shows when
    /// Vgs is the driver (Idq is then informational: "here is what this Vgs actually draws"). Reuses
    /// <see cref="_dcSeedVoltages"/> — the SAME cached DC operating point <see cref="SeedFromRealDc"/>
    /// warm-starts a cold HB solve from — so reading this costs nothing extra once anything has
    /// already triggered a cold solve at this bias, and exactly one DC solve otherwise. NaN when the
    /// drain port is unavailable (an external DUT with no intrinsic mapping, §4.5.5) or the cached
    /// solve is empty (DC did not converge at all).</summary>
    public double DcDrainCurrentAmps => DrainCurrentAt(_dcSeedVoltages ??= ComputeDcSeed());

    /// <summary>
    /// Solves Vgs for a target quiescent drain current, by bracket-then-secant on the DUT's own DC
    /// operating point — the SAME shape <c>PinSearch.Run</c> already uses for (Pin, compression),
    /// applied here to (Vgs, Ids). <paramref name="idqTargetAmps"/> and the return value are both
    /// AMPS/VOLTS — unit conversion for a mA-displaying UI is the caller's own job, not this method's.
    ///
    /// <para><b>Never throws, never returns null</b> — matches <see cref="ComputeDcSeed"/>'s own
    /// "best-effort, always leaves a real bias behind" philosophy: a target this DUT genuinely cannot
    /// reach (outside its Vgs range, or a DC solve that stops converging while searching) still has to
    /// leave SOME Vgs applied, and the closest one found beats an exception taking the whole edit down.
    /// </para>
    ///
    /// <para><b>Mutates the bias supplies as a side effect of searching</b> — each trial has to be a
    /// real DC solve against the real netlist, so there is no way to probe Ids(Vgs) without moving
    /// <c>_gateSupply</c> there first. The CALLER (<see cref="Apply"/>) always finishes by calling
    /// <see cref="SetBias"/> with the result, which re-applies the converged Vgs/Vds pair for real
    /// (including <see cref="ReExtract"/> and invalidating the DC seed cache) — so a caller of THIS
    /// method alone, mid-search, must not assume the supplies are left at any particular value.</para>
    /// </summary>
    public double SolveVgsForIdq(double idqTargetAmps, double vds)
    {
        double vgs0 = _model.Bias.Vgs ?? 0.0;
        double i0 = TryDcIds(vgs0, vds, out bool ok0);
        if (!ok0) return vgs0;

        double tol = Math.Max(1e-12, Math.Abs(idqTargetAmps) * 1e-4);
        if (Math.Abs(i0 - idqTargetAmps) <= tol) return vgs0;

        // Probe gm's SIGN rather than assuming one — a depletion-mode and an enhancement-mode device
        // (or a user-supplied external model of either) can disagree about which way Vgs has to move.
        const double probeStep = 0.01;
        double i1 = TryDcIds(vgs0 + probeStep, vds, out bool ok1);
        double gmSign = ok1 && Math.Abs(i1 - i0) > 1e-15 ? Math.Sign(i1 - i0) : 1.0;
        double dir = Math.Sign(idqTargetAmps - i0) * gmSign;
        if (dir == 0) dir = 1.0;

        // ── bracket: step outward, doubling, until Ids crosses the target ──────
        double vgsLo = vgs0, iLo = i0;
        double vgsHi = vgs0, iHi = i0;
        double stride = probeStep;
        bool bracketed = false;

        for (int i = 0; i < MaxBiasBracketSteps; i++)
        {
            double vgsNext = vgsHi + dir * stride;
            double iNext = TryDcIds(vgsNext, vds, out bool ok);
            if (!ok) break;   // DC stopped converging out here — work with whatever was bracketed

            bool crossed = (iLo <= idqTargetAmps) != (iNext <= idqTargetAmps);
            vgsLo = vgsHi; iLo = iHi;
            vgsHi = vgsNext; iHi = iNext;
            if (crossed) { bracketed = true; break; }

            stride *= 2.0;
        }

        if (!bracketed)
            return Math.Abs(iHi - idqTargetAmps) < Math.Abs(i0 - idqTargetAmps) ? vgsHi : vgs0;

        // ── secant within [vgsLo, vgsHi], bisection fallback on a flat/overshoot step ──
        double best = Math.Abs(iHi - idqTargetAmps) < Math.Abs(iLo - idqTargetAmps) ? vgsHi : vgsLo;
        double bestErr = Math.Min(Math.Abs(iHi - idqTargetAmps), Math.Abs(iLo - idqTargetAmps));

        for (int it = 0; it < MaxBiasSecantSteps && bestErr > tol; it++)
        {
            double denom = iHi - iLo;
            double next = Math.Abs(denom) < 1e-15
                ? 0.5 * (vgsLo + vgsHi)
                : vgsLo + (idqTargetAmps - iLo) * (vgsHi - vgsLo) / denom;

            if (!(next > Math.Min(vgsLo, vgsHi) && next < Math.Max(vgsLo, vgsHi)))
                next = 0.5 * (vgsLo + vgsHi);

            double iNext = TryDcIds(next, vds, out bool ok);
            if (!ok) break;

            double err = Math.Abs(iNext - idqTargetAmps);
            if (err < bestErr) { best = next; bestErr = err; }

            if ((iNext <= idqTargetAmps) == (iLo <= idqTargetAmps)) { vgsLo = next; iLo = iNext; }
            else                                                    { vgsHi = next; iHi = iNext; }
        }

        return best;
    }

    private const int MaxBiasBracketSteps = 20;
    private const int MaxBiasSecantSteps  = 30;

    /// <summary>One trial: apply (vgs, vds) to the real supplies, run a real DC solve, read Ids back.
    /// <paramref name="ok"/> is false for a solve that threw or produced no node voltages at all — the
    /// caller decides what "give up here" means for its own search, this just reports the fact.
    /// </summary>
    private double TryDcIds(double vgs, double vds, out bool ok)
    {
        _gateSupply?.SetVdc(vgs);
        _drainSupply?.SetVdc(vds);
        try
        {
            var result = CircuitRF.Engine.NonlinearDcEngine.Run(_netlist);
            double ids = DrainCurrentAt(result.NodeVoltages);
            ok = !double.IsNaN(ids) && double.IsFinite(ids);
            return ids;
        }
        catch (Exception)
        {
            ok = false;
            return double.NaN;
        }
    }

    /// <summary>
    /// The DUT's own drain current at a converged DC node-voltage vector — direct device evaluation
    /// (<see cref="ComponentModel.Evaluate"/>) at the terminal voltages, the SAME
    /// "read the model's own I directly, no ratio" rule §4.5.1 states for the load side. Built from
    /// <c>dut.Nodes</c> and indexed with the SAME 1-based/0-is-ground convention
    /// <see cref="SeedFromRealDc"/> already uses for this exact array, so the two can never disagree
    /// about what "node 0" means. NaN when the drain port is unknown (§4.5.5 refuses to guess) or
    /// <paramref name="dcV"/> is empty (a DC solve that did not converge at all).
    /// </summary>
    private double DrainCurrentAt(double[] dcV)
    {
        int drainPort = IntrinsicPorts.DrainPort;
        if (drainPort < 0 || dcV.Length == 0) return double.NaN;

        var dut = DutComponent;
        int p = dut.Model.PortCount;
        var pv = new double[p];
        for (int q = 0; q < p; q++)
        {
            int np = dut.Nodes.Length > 2 * q     ? dut.Nodes[2 * q]     : 0;
            int nm = dut.Nodes.Length > 2 * q + 1 ? dut.Nodes[2 * q + 1] : 0;
            double vp = np >= 1 && np <= dcV.Length ? dcV[np - 1] : 0.0;
            double vm = nm >= 1 && nm <= dcV.Length ? dcV[nm - 1] : 0.0;
            pv[q] = vp - vm;
        }

        if (drainPort >= p) return double.NaN;
        return dut.Model.Evaluate(new PortVoltages(pv)).I[drainPort];
    }
}

using System.Numerics;
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
    /// </summary>
    public bool Apply(CircuitModel model)
    {
        bool structural = model.StructuralKey != _model.StructuralKey;
        var  previous   = _model;
        _model = model;

        if (structural) { Rebuild(); return true; }

        if (model.Bias.Vgs != previous.Bias.Vgs || model.Bias.Vds != previous.Bias.Vds)
            SetBias(model.Bias.Vgs ?? 0.0, model.Bias.Vds);
        return false;
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
            SeedFromDc(v, yNN, iSrc, N, K);

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

    /// <summary>
    /// Cold seed: the linear network's own DC operating point with the devices absent
    /// (<c>V = −Y(0)⁻¹·I_src(0)</c>), and zero at every harmonic. A warm start supersedes it, which
    /// is the case that matters — 0.94 ms warm against 2.45 ms cold (§2).
    /// </summary>
    private static void SeedFromDc(Complex[,] v, Complex[][,] yNN, Complex[][] iSrc, int n, int k)
    {
        var rhs = new Complex[n, 1];
        for (int i = 0; i < n; i++) rhs[i, 0] = -iSrc[0][i];

        Complex[,] dc;
        try { dc = InterfaceNetwork.SolveDense(yNN[0], rhs); }
        catch (InvalidOperationException) { return; }   // leave the seed at zero

        for (int i = 0; i < n; i++) v[i, 0] = dc[i, 0];
        _ = k;
    }
}

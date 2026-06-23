using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Programmable RF termination — the user-facing Tuner component (loadpull.md §1).
///
/// Declared role-neutrally; the LoadpullEngine assigns the role by calling SetRole()
/// before any HB runs. Outside HB (S-parameter sims) the Tuner presents Z[1] flat
/// over all frequencies (no harmonic band structure).
///
/// Node layout (5 entries in ElaboratedComponent.Nodes). Both roles declare the same two nets
/// — [0] DUT-facing, [1] reference — and the engine mints the rest (loadpull.md §1.1):
///   [0] = n_dut   (DUT-facing net; the single schematic pin)
///   [1] = n_ref   (reference net; ground "0" by default)
///   [2] = __tuner_&lt;inst&gt;_block  (internal: between DC-block cap and RF termination)
///   [3] = __tuner_&lt;inst&gt;_bias   (internal: between RF choke and bias supply)
///   [4] = __tuner_&lt;inst&gt;_outer  (internal: SourceTuner RF-drive node; unused by LoadTuner)
///
/// Internal bias-tee topology (BiasTee=on, loadpull.md §1.1):
///
///   LoadTuner  (n_dut, n_ref):
///     n_dut  --[C=1F]--   n_block --[Z_Port per-harmonic]-- n_ref
///     n_dut  --[L=1H]--   n_bias  --[V=Vbias@DC]----------  n_ref
///
///   SourceTuner (n_dut, n_ref; n_outer minted internally):
///     n_outer --[V_1Tone drive at f0, |Vs|]-- n_ref
///     n_outer --[Z_Port per-harmonic]-- n_block --[C=1F]-- n_dut
///     n_dut   --[L=1H]--  n_bias --[V=Vbias@DC]-- n_ref
///
/// Matches the Hero-2 explicit bias-tee topology (hero2.cnl) exactly.
/// C = 1 F (ideal: open at DC, short at RF), L = 1 H (ideal: short at DC, high-Z at RF).
///
/// InductanceRegularization: the LoadpullEngine sets InductanceRegularization=Always so that
/// the ideal choke branch diagonal gets R_reg added before DC extraction (linear-engine §4.3.1).
/// The Stamp() method for the choke is identical to InductorModel (R=0 case) so the extractor
/// can identify and regularize it automatically.
///
/// Sign convention for power measurements (verified against Hero-2 golden data):
///   Pout          = −½·Re(V[load_dut_node, k=1] · conj(I_nl[load_dut_idx, k=1]))
///   Pin_delivered = +½·Re(V[src_dut_node,  k=1] · conj(I_nl[src_dut_idx,  k=1]))
/// </summary>
public sealed class TunerModel : ComponentModel
{
    // ── Constants ────────────────────────────────────────────────────────────
    private const double IdealC      = 1.0;   // Farads — open at DC, short at RF
    private const double IdealL      = 1.0;   // Henries — short at DC, high-Z at RF
    private const double OmegaTolRad = 1.0;   // 1 rad/s harmonic-matching tolerance

    // ── ComponentModel overrides ─────────────────────────────────────────────
    public override int       PortCount => 1;
    public override ModelKind Kind      => ModelKind.Linear;

    // ── Per-harmonic termination ─────────────────────────────────────────────
    // Values are impedances (Γ entries are pre-converted at construction).
    private readonly Dictionary<int, Complex> _harmonicZ;   // k → Z, k ≥ 1
    private readonly Complex                  _zDefault;     // Zdefault catch-all

    // ── Bias-tee ─────────────────────────────────────────────────────────────
    private readonly bool   _hasBiasTee;
    private readonly double _vbias;    // DC bias voltage (V)

    // ── Role (set by LoadpullEngine before any HB run) ────────────────────────
    public TunerRole Role { get; private set; } = TunerRole.Load;
    public void SetRole(TunerRole role) => Role = role;

    // ── Source-role drive parameters ─────────────────────────────────────────
    // _toneFreqHz == 0 means S-param mode (no tone, no drive stamped).
    private double _toneFreqHz;
    private double _vsMagnitude;   // |Vs| = sqrt(8·Pavl·Re(Z1_eff)), updated each Pin step

    /// <summary>
    /// Set by the LoadpullEngine at setup and updated each Pin step.
    /// Computes |Vs| = sqrt(8·Pavl·Re(Z1_eff)) where Z1_eff is the effective fundamental
    /// impedance — the harmonic-override value if one is set, else the declared Z[1].
    /// This keeps Pavl and the presented source impedance in agreement at all times.
    /// </summary>
    public void SetSourceDrive(double toneFreqHz, double pavlWatts)
    {
        _toneFreqHz = toneFreqHz;
        double omega0 = 2.0 * Math.PI * toneFreqHz;
        double reZ1   = GetZ(omega0).Real;   // respects harmonic override if set
        _vsMagnitude  = reZ1 > 0 ? Math.Sqrt(8.0 * pavlWatts * reZ1) : 0.0;
    }

    /// <summary>Set tone for non-source-role HB (e.g. unit tests); no drive stamped.</summary>
    public void SetTone(double toneFreqHz) => _toneFreqHz = toneFreqHz;

    // ── Swept-harmonic override (set by LoadpullEngine per grid point) ────────
    private int?    _overrideHarmonic;
    private Complex _overrideZ;

    public void SetHarmonicOverride(int harmonic, Complex z)
    {
        _overrideHarmonic = harmonic;
        _overrideZ        = z;
    }

    public void ClearHarmonicOverride() => _overrideHarmonic = null;

    // ── Exposed handles for regularization and Pdc readback ──────────────────
    // Set during each Stamp() call.

    /// <summary>
    /// Branch index of the internal RF choke (ideal L = IdealL H).
    /// Set by Stamp() at each MNA build. Used by HbLinearExtractor.ApplyInductanceReg()
    /// so the TunerModel's choke is regularized exactly like InductorModel branches when
    /// InductanceRegularization = Always (loadpull.md §2.1, linear-engine §4.3.1).
    /// </summary>
    public int ChokeBranchIndex { get; private set; } = -1;

    /// <summary>Branch index of the internal bias supply (DC voltage source).</summary>
    public int BiasSupplyBranchIndex { get; private set; } = -1;

    // ── Constructor ───────────────────────────────────────────────────────────

    public TunerModel(
        string                   instanceName,
        Dictionary<int, Complex> harmonicZ,
        Complex                  zDefault,
        bool                     hasBiasTee,
        double                   vbias)
    {
        _ = instanceName;  // stored for diagnostics if needed later
        _harmonicZ = harmonicZ;
        _zDefault  = zDefault;
        _hasBiasTee = hasBiasTee;
        _vbias      = vbias;
    }

    // ── Stamp (called once per frequency by the MNA assembly) ─────────────────

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        // Node layout (see class doc):
        //   [0] = n_dut, [1] = n_ref, [2] = nBlock, [3] = nBias, [4] = nOuter
        int nDut   = c.Nodes.Length > 0 ? c.Nodes[0] : 0;
        int nRef   = c.Nodes.Length > 1 ? c.Nodes[1] : 0;
        int nBlock = c.Nodes.Length > 2 ? c.Nodes[2] : 0;
        int nBias  = c.Nodes.Length > 3 ? c.Nodes[3] : 0;
        int nOuter = c.Nodes.Length > 4 ? c.Nodes[4] : 0;
        bool isDC  = Math.Abs(omega) < OmegaTolRad;

        ChokeBranchIndex      = -1;  // reset each stamp pass
        BiasSupplyBranchIndex = -1;

        if (Role == TunerRole.Load)
            StampLoad(mna, nDut, nRef, nBlock, nBias, omega, isDC);
        else
            StampSource(mna, nDut, nRef, nBlock, nBias, nOuter, omega, isDC);
    }

    // ── LoadTuner ─────────────────────────────────────────────────────────────

    private void StampLoad(IMnaContext mna,
        int nDut, int nRef, int nBlock, int nBias,
        double omega, bool isDC)
    {
        // 1. DC-block cap C=1F: admittance jωC between nDut and nBlock.
        //    Open at DC (no stamp); transparent at RF.
        if (!isDC)
            mna.AddAdmittance(nDut, nBlock, new Complex(0, omega * IdealC));

        // 2. Z_Port (load impedance): between nBlock and nRef.
        StampZPort(mna, nBlock, nRef, GetZ(omega));

        // 3. RF choke L=1H: Group-2 branch between nDut and nBias.
        //    ChokeBranchIndex is captured so HbLinearExtractor.ApplyInductanceReg()
        //    can regularize it the same way it regularizes InductorModel branches.
        ChokeBranchIndex = StampInductor(mna, nDut, nBias, omega, isDC);

        // 4. Bias supply: Group-2 branch between nBias and nRef.
        if (_hasBiasTee)
            BiasSupplyBranchIndex = StampBiasSupply(mna, nBias, nRef, isDC);
    }

    // ── SourceTuner ───────────────────────────────────────────────────────────

    private void StampSource(IMnaContext mna,
        int nDut, int nRef, int nBlock, int nBias, int nOuter,
        double omega, bool isDC)
    {
        // 1. V_1Tone drive: Group-2 branch between nOuter and nRef.
        //    Drives at the analysis fundamental; shorts all other frequencies.
        //    Only stamped when tone is set (HB mode; not S-param).
        if (_toneFreqHz > 0)
        {
            double omegaTone = 2.0 * Math.PI * _toneFreqHz;
            bool   isTone    = Math.Abs(omega - omegaTone) < OmegaTolRad;
            var    driveV    = isTone ? new Complex(_vsMagnitude, 0) : Complex.Zero;
            int brDrive = mna.AddBranch();
            mna.AddBranchCurrent(brDrive, nOuter, nRef);
            mna.AddConstraint(brDrive, nOuter, new Complex(+1, 0));
            if (nRef > 0) mna.AddConstraint(brDrive, nRef, new Complex(-1, 0));
            mna.AddSourceValue(brDrive, driveV);
        }

        // 2. Z_Port (source impedance): between nOuter and nBlock.
        StampZPort(mna, nOuter, nBlock, GetZ(omega));

        // 3. DC-block cap C=1F: admittance jωC between nBlock and nDut.
        if (!isDC)
            mna.AddAdmittance(nBlock, nDut, new Complex(0, omega * IdealC));

        // 4. RF choke L=1H between nDut and nBias.
        ChokeBranchIndex = StampInductor(mna, nDut, nBias, omega, isDC);

        // 5. Bias supply between nBias and nRef.
        if (_hasBiasTee)
            BiasSupplyBranchIndex = StampBiasSupply(mna, nBias, nRef, isDC);
    }

    // ── Shared stamp primitives ───────────────────────────────────────────────

    /// <summary>
    /// Stamps a 1-port Z element as a Group-2 branch (linear-engine §2, Group-2).
    /// Constraint: V(na) − V(nb) − Z·I = 0; KCL: I from na to nb.
    /// </summary>
    private static void StampZPort(IMnaContext mna, int na, int nb, Complex z)
    {
        int br = mna.AddBranch();
        mna.AddBranchCurrent(br, na, nb);
        mna.AddConstraint(br, na, new Complex(+1, 0));
        if (nb > 0) mna.AddConstraint(br, nb, new Complex(-1, 0));
        mna.AddBranchConstraint(br, br, -z);
    }

    /// <summary>
    /// Stamps an ideal inductor L=IdealL as a Group-2 branch and returns its branch index.
    /// Constraint: V(na) − V(nb) − jωL·I = 0 at AC; V(na) − V(nb) = 0 at DC (exact short).
    /// Identical to InductorModel.Stamp(R=0) so the returned branch index can be used by
    /// HbLinearExtractor.ApplyInductanceReg() to regularize the TunerModel's choke exactly
    /// like an InductorModel choke (linear-engine §4.3.1, HB CLAUDE.md).
    /// </summary>
    private static int StampInductor(IMnaContext mna, int na, int nb, double omega, bool isDC)
    {
        int br = mna.AddBranch();
        mna.AddBranchCurrent(br, na, nb);
        mna.AddConstraint(br, na, new Complex(+1, 0));
        if (nb > 0) mna.AddConstraint(br, nb, new Complex(-1, 0));
        // Diagonal: −jωL at AC; 0 at DC (exact short).
        if (!isDC)
            mna.AddBranchConstraint(br, br, new Complex(0, -omega * IdealL));
        // At DC, diagonal stays 0 → constraint is Va − Vb = 0 (inductor = short).
        // InductanceRegularization adds R_reg to this branch when mode=Always.
        return br;
    }

    /// <summary>
    /// Stamps the DC bias supply as a Group-2 branch.
    /// Constraint: V(nBias) − V(nRef) = Vbias at DC; 0 at RF (choke blocks RF).
    /// Returns the branch index (for bias-current readback in the post-processor).
    /// </summary>
    private int StampBiasSupply(IMnaContext mna, int nBias, int nRef, bool isDC)
    {
        int br = mna.AddBranch();
        mna.AddBranchCurrent(br, nBias, nRef);
        mna.AddConstraint(br, nBias, new Complex(+1, 0));
        if (nRef > 0) mna.AddConstraint(br, nRef, new Complex(-1, 0));
        // Source value: Vbias at DC; 0 at RF (DC supply is a short at RF).
        mna.AddSourceValue(br, isDC ? new Complex(_vbias, 0) : Complex.Zero);
        return br;
    }

    // ── Z(ω) evaluation ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns the impedance to present at angular frequency omega.
    ///
    /// HB mode (ToneFreqHz > 0): maps omega to harmonic k = round(omega/omega0).
    ///   DC (k=0): returns Zdefault (near-short for DC bias path through cap-blocked port).
    ///   k ≥ 1: checks override first, then declared Z[k], then Zdefault.
    ///
    /// S-param mode (ToneFreqHz = 0): returns Z[1] for all omega ≠ 0; Zdefault at DC.
    /// (loadpull.md §1.1: "presents Z[1]/G[1] constant over all frequencies outside HB".)
    /// </summary>
    private Complex GetZ(double omega)
    {
        bool isDC = Math.Abs(omega) < OmegaTolRad;
        if (isDC) return _zDefault;

        if (_toneFreqHz <= 0)
            return GetDeclaredZ(1);   // S-param mode: flat Z[1]

        // HB mode: identify the harmonic.
        double omega0 = 2.0 * Math.PI * _toneFreqHz;
        double ratio  = omega / omega0;
        int    k      = (int)Math.Round(ratio);
        if (k < 1 || Math.Abs(ratio - k) * omega0 > OmegaTolRad)
            return _zDefault;   // off-grid → catch-all

        // Loadpull override (set per grid point) takes precedence.
        if (_overrideHarmonic.HasValue && _overrideHarmonic.Value == k)
            return _overrideZ;

        return GetDeclaredZ(k);
    }

    /// <summary>Returns the declared Z for harmonic k (falls back to Zdefault).</summary>
    public Complex GetDeclaredZ(int k)
        => _harmonicZ.TryGetValue(k, out var z) ? z : _zDefault;
}

/// <summary>Role of a Tuner within a Loadpull analysis (assigned by the LoadpullEngine).</summary>
public enum TunerRole
{
    /// <summary>Passive load termination (Z_Port + bias-tee; no RF drive).</summary>
    Load,
    /// <summary>Source termination + internal RF drive (Z_Port + V_1Tone + bias-tee).</summary>
    Source,
}

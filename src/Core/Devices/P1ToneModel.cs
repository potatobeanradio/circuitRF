using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Single-tone RF power source specified by available power (Pavl, dBm) behind an internal
/// reference impedance (Zdefault, default 50 Ω).  Supports per-harmonic-band terminations
/// Z[1], Z[2], … using the band-assignment rule n = roundHalfUp(|f|/f_c).
///
/// Node layout (3 entries in ElaboratedComponent.Nodes):
///   [0] = first declared net  (DUT/external-facing)
///   [1] = second declared net (reference, typically ground)
///   [2] = __p1tone_&lt;inst&gt;_drv  (internal: junction between V-source and Z_Port)
///
/// Stamp topology (HB mode):
///   nRef --[V_drive = |Vs|∠φ at driveFreqHz, 0 otherwise]-- nDrv --[Z_Port GetZ(ω)]-- nExt
///
/// Stamp topology (S-param mode, _fc ≤ 0):
///   Z_Port(nExt, nRef, Z[1]) only — no drive branch, nDrv unused.
///
/// |Vs| = sqrt(8 · Re(Z_at_fundamental) · Pavl_W)  (matched-load maximum power transfer).
///
/// SetToneContext must be called by HbEngine before any Stamp() in HB mode.
/// </summary>
public sealed class P1ToneModel : ComponentModel, IDriveScalable
{
    private const double OmegaTolRad = 1.0;  // 1 rad/s harmonic-matching tolerance

    /// <inheritdoc/>
    public double DriveScale { get; set; } = 1.0;

    public override int       PortCount => 1;
    public override ModelKind Kind      => ModelKind.Linear;

    private readonly Dictionary<int, Complex> _harmonicZ;
    private readonly Complex                  _zDefault;
    private readonly double                   _pavlDbm;
    private readonly double                   _freqHz;
    private readonly double                   _phaseDeg;

    // Set by SetToneContext() before each HB run.
    private double _fc;           // band-center frequency (Hz); 0 = S-param mode
    private double _driveFreqHz;  // frequency at which to stamp the V-drive

    // |Vs| = sqrt(8·Re(Z1_eff)·Pavl_W); recomputed in SetToneContext().
    private double _vsMagnitude;

    public P1ToneModel(
        string                   instanceName,
        Dictionary<int, Complex> harmonicZ,
        Complex                  zDefault,
        double                   pavlDbm,
        double                   freqHz,
        double                   phaseDeg)
    {
        _ = instanceName;
        _harmonicZ = harmonicZ;
        _zDefault  = zDefault;
        _pavlDbm   = pavlDbm;
        _freqHz    = freqHz;
        _phaseDeg  = phaseDeg;

        // Pre-compute |Vs| using S-param-mode Z[1] (before SetToneContext is called).
        // SetToneContext will recompute with the correct band-mapped impedance.
        double pavlW = Math.Pow(10.0, (pavlDbm - 30.0) / 10.0);
        double reZ1  = GetZ(2.0 * Math.PI * freqHz).Real;  // _fc=0 → S-param flat Z[1]
        _vsMagnitude = reZ1 > 0 ? Math.Sqrt(8.0 * pavlW * reZ1) : 0.0;
    }

    /// <summary>
    /// Called by HbEngine before extraction to inject the tone context.
    ///   fc          — band-center frequency in Hz (f0 for single-tone; (f1+f2)/2 for two-tone).
    ///   driveFreqHz — actual drive frequency (= P1Tone's declared Freq parameter).
    /// Also recomputes |Vs| with the band-mapped impedance at the fundamental.
    /// </summary>
    public void SetToneContext(double fc, double driveFreqHz)
    {
        _fc          = fc;
        _driveFreqHz = driveFreqHz;

        // Recompute |Vs| with the band-mapped Z at the drive frequency.
        double pavlW = Math.Pow(10.0, (_pavlDbm - 30.0) / 10.0);
        double reZ1  = GetZ(2.0 * Math.PI * driveFreqHz).Real;
        _vsMagnitude = reZ1 > 0 ? Math.Sqrt(8.0 * pavlW * reZ1) : 0.0;
    }

    /// <summary>No-op: ParametricSweepEngine re-elaborates per point, so all params are fresh.</summary>
    public void ReevaluateFromGlobals(IReadOnlyDictionary<string, Value> globals) { }

    /// <summary>Declared tone frequency (Hz) — used by HbEngine for commensurability and context.</summary>
    public double FreqHz => _freqHz;

    /// <summary>
    /// Branch index set by StampAsSParamPort; used by SParameterEngine legacy path (Re(Z0) ≤ 0).
    /// Stable across frequencies because branch allocation order is deterministic.
    /// </summary>
    public int LastBranchIndex { get; private set; } = -1;

    /// <summary>
    /// Stamps a 0 V source branch between Nodes[0] and Nodes[1] (mirroring TermModel.Stamp),
    /// for SParameterEngine's legacy path. The engine drives unit voltage on this branch and
    /// reads the resulting current to extract the Y column.
    /// </summary>
    public void StampAsSParamPort(IMnaContext mna, ElaboratedComponent c)
    {
        LastBranchIndex = mna.AddBranch();
        mna.AddBranchCurrent(LastBranchIndex, c.Nodes[0], c.Nodes[1]);
        mna.AddConstraint(LastBranchIndex, c.Nodes[0], +Complex.One);
        mna.AddConstraint(LastBranchIndex, c.Nodes[1], -Complex.One);
        mna.AddSourceValue(LastBranchIndex, Complex.Zero);
    }

    /// <summary>
    /// S-parameter mode: the internal drive node (Nodes[2], the "__p1tone_..._drv" junction) hosts
    /// the HB V-source and is unused here — no drive branch, and no Z stamp touches it — so it would
    /// be a floating MNA unknown (zero row/col, hence singular). Tie it to the reference node
    /// (Nodes[1]) with a unit conductance. The drive node is otherwise isolated in S-param mode, so
    /// this carries no current and does not affect the port at the external terminals; the engine
    /// realizes Z0 via the port conductance (wave path) or renormalization (legacy path).
    /// </summary>
    public void StampSParamDriveTie(IMnaContext mna, ElaboratedComponent c)
    {
        int nRef = c.Nodes.Length > 1 ? c.Nodes[1] : 0;
        int nDrv = c.Nodes.Length > 2 ? c.Nodes[2] : 0;
        if (nDrv <= 0) return;   // no internal drive node → nothing to tie
        mna.AddAdmittance(nDrv, nRef, Complex.One);
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        int nExt = c.Nodes.Length > 0 ? c.Nodes[0] : 0;
        int nRef = c.Nodes.Length > 1 ? c.Nodes[1] : 0;
        int nDrv = c.Nodes.Length > 2 ? c.Nodes[2] : 0;

        if (_fc <= 0)
        {
            // S-param mode: present Z[1] flat across all frequencies (no drive).
            StampZPort(mna, nExt, nRef, GetZ(omega));
            return;
        }

        // HB mode: V-drive at nDrv→nRef, Z_Port from nExt→nDrv.

        // Drive branch: pins nDrv to Vs at driveFreqHz, 0 at all other harmonics.
        double omegaDrv = 2.0 * Math.PI * _driveFreqHz;
        bool   isTone   = Math.Abs(omega - omegaDrv) < OmegaTolRad;
        var    driveV   = isTone
            ? Complex.FromPolarCoordinates(DriveScale * _vsMagnitude, _phaseDeg * Math.PI / 180.0)
            : Complex.Zero;

        int brDrive = mna.AddBranch();
        mna.AddBranchCurrent(brDrive, nDrv, nRef);
        mna.AddConstraint(brDrive, nDrv, new Complex(+1, 0));
        if (nRef > 0) mna.AddConstraint(brDrive, nRef, new Complex(-1, 0));
        mna.AddSourceValue(brDrive, driveV);

        // Z_Port: harmonic-band impedance between nExt and nDrv.
        StampZPort(mna, nExt, nDrv, GetZ(omega));
    }

    // ── Impedance lookup ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the impedance to present at angular frequency omega.
    ///
    /// S-param mode (_fc ≤ 0): returns Z[1] flat for all non-DC frequencies.
    /// HB mode: maps |f| to band n = roundHalfUp(|f|/f_c); returns Z[n] or Zdefault.
    /// DC (|ω| &lt; OmegaTolRad): returns Zdefault.
    /// </summary>
    private Complex GetZ(double omega)
    {
        bool isDC = Math.Abs(omega) < OmegaTolRad;
        if (isDC) return _zDefault;

        if (_fc <= 0)
            return GetDeclaredZ(1);   // S-param mode: flat Z[1]

        double freqHz = Math.Abs(omega) / (2.0 * Math.PI);
        int    n      = (int)Math.Floor(freqHz / _fc + 0.5);  // roundHalfUp
        if (n < 0) n = 0;

        return GetDeclaredZ(n);
    }

    /// <summary>Returns the declared Z for harmonic band n; falls back to Zdefault.</summary>
    public Complex GetDeclaredZ(int n)
        => _harmonicZ.TryGetValue(n, out var z) ? z : _zDefault;

    // ── Shared stamp primitive ────────────────────────────────────────────────────

    private static void StampZPort(IMnaContext mna, int na, int nb, Complex z)
    {
        int br = mna.AddBranch();
        mna.AddBranchCurrent(br, na, nb);
        mna.AddConstraint(br, na, new Complex(+1, 0));
        if (nb > 0) mna.AddConstraint(br, nb, new Complex(-1, 0));
        mna.AddBranchConstraint(br, br, -z);
    }
}

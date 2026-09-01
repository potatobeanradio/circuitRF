using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Multi-tone RF power source — the convenient authoring counterpart of <see cref="P1ToneModel"/>
/// for two-tone (and higher) harmonic-balance. Each tone i carries its own available power
/// <c>Pavl[i]</c> (dBm), frequency <c>Freq[i]</c>, and phase <c>Phase[i]</c>; all tones share one
/// harmonic-terminated reference impedance (the scalar <c>Z</c> = Zdefault, plus optional per-band
/// <c>Z[k]</c>), using the same band-assignment rule as P1Tone (n = roundHalfUp(|f|/f_c),
/// docs/design/p1tone-harmonic-terminations.md).
///
/// Topology (identical to P1Tone, but the drive injects a SUM of tones):
///   nRef --[V_drive(ω)]-- nDrv --[Z_Port GetZ(ω)]-- nExt
/// where V_drive(ω) = the matching tone's phasor when ω lands on a declared tone, else 0.
///   |Vs_i| = sqrt(8 · Re(Z_at_Freq_i) · Pavl_i_W)  (matched-load maximum power transfer, per tone).
///
/// Node layout (3 entries in ElaboratedComponent.Nodes), same as P1Tone:
///   [0] = first declared net (DUT/external-facing)
///   [1] = second declared net (reference, typically ground)
///   [2] = __pntone_&lt;inst&gt;_drv (internal: V-source ↔ Z_Port junction)
///
/// HbEngine calls <see cref="SetToneContext"/> before extraction (single-tone f_c = f0; two-tone
/// f_c = (f1+f2)/2). In S-parameter mode (_fc ≤ 0) the source is passive: it presents Z[1] between
/// the external and reference nodes and ties off its (undriven) internal node — no port role.
///
/// <para><b>Phase[i] reaches this model in RADIANS</b>, for the same reason and by the same route as
/// <see cref="P1ToneModel"/>'s: the Elaborator applies the parameter's angle unit before the factory
/// runs. An authored <c>Phase[1]=45 deg</c> arrives as 0.7854; a bare <c>Phase[1]=45</c> is 45
/// radians.</para>
/// </summary>
public sealed class PnToneModel : ComponentModel, IDriveScalable
{
    private const double OmegaTolRad = 1.0;  // 1 rad/s harmonic-matching tolerance

    /// <inheritdoc/>
    public double DriveScale { get; set; } = 1.0;

    public override int       PortCount => 1;
    public override ModelKind Kind      => ModelKind.Linear;

    /// <summary>
    /// One declared tone: available power (dBm), frequency (Hz), phase (<b>RADIANS</b> — see the
    /// class doc).
    /// </summary>
    public readonly record struct Tone(double PavlDbm, double FreqHz, double PhaseRad);

    private readonly Tone[]                    _tones;
    private readonly Dictionary<int, Complex>  _harmonicZ;
    private readonly Complex                   _zDefault;

    // Set by SetToneContext() before each HB run. 0 ⇒ S-param mode.
    private double _fc;

    // |Vs_i| per tone; recomputed in SetToneContext() with the band-mapped impedance at each tone.
    private readonly double[] _vsMagnitude;

    public PnToneModel(string instanceName, Tone[] tones,
        Dictionary<int, Complex> harmonicZ, Complex zDefault)
    {
        _ = instanceName;
        _tones       = tones;
        _harmonicZ   = harmonicZ;
        _zDefault    = zDefault;
        _vsMagnitude = new double[tones.Length];

        // Pre-compute |Vs_i| using S-param-mode Z[1] (before SetToneContext); recomputed there.
        for (int i = 0; i < tones.Length; i++)
        {
            double pavlW = Math.Pow(10.0, (tones[i].PavlDbm - 30.0) / 10.0);
            double reZ   = GetZ(2.0 * Math.PI * tones[i].FreqHz).Real;
            _vsMagnitude[i] = reZ > 0 ? Math.Sqrt(8.0 * pavlW * reZ) : 0.0;
        }
    }

    /// <summary>The declared tone frequencies (Hz) — used by HbEngine for commensurability.</summary>
    public IReadOnlyList<double> ToneFreqsHz => Array.ConvertAll(_tones, t => t.FreqHz);

    /// <summary>
    /// Inject the band-center frequency f_c (Hz) before extraction (single-tone f0; two-tone
    /// (f1+f2)/2). Each tone still drives at its own Freq[i]; f_c only sets the Z[k] band ruler.
    /// Recomputes |Vs_i| with the band-mapped impedance at each tone's frequency.
    /// </summary>
    public void SetToneContext(double fc)
    {
        _fc = fc;
        for (int i = 0; i < _tones.Length; i++)
        {
            double pavlW = Math.Pow(10.0, (_tones[i].PavlDbm - 30.0) / 10.0);
            double reZ   = GetZ(2.0 * Math.PI * _tones[i].FreqHz).Real;
            _vsMagnitude[i] = reZ > 0 ? Math.Sqrt(8.0 * pavlW * reZ) : 0.0;
        }
    }

    /// <summary>No-op: ParametricSweepEngine re-elaborates per point, so all params are fresh.</summary>
    public void ReevaluateFromGlobals(IReadOnlyDictionary<string, Value> globals) { }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        int nExt = c.Nodes.Length > 0 ? c.Nodes[0] : 0;
        int nRef = c.Nodes.Length > 1 ? c.Nodes[1] : 0;
        int nDrv = c.Nodes.Length > 2 ? c.Nodes[2] : 0;

        if (_fc <= 0)
        {
            // S-param mode: present Z[1] passively between nExt and nRef (no drive). Tie the otherwise
            // floating internal drive node to the reference so the MNA stays non-singular; it carries
            // no current and does not perturb the external terminals.
            StampZPort(mna, nExt, nRef, GetZ(omega));
            if (nDrv > 0) mna.AddAdmittance(nDrv, nRef, Complex.One);
            return;
        }

        // HB mode: at this spectral line ω, the drive equals the matching tone's phasor (at most one
        // tone matches, since tones are distinct frequencies), else 0.
        Complex driveV = Complex.Zero;
        for (int i = 0; i < _tones.Length; i++)
        {
            double omegaTone = 2.0 * Math.PI * _tones[i].FreqHz;
            if (Math.Abs(omega - omegaTone) < OmegaTolRad)
            {
                driveV = Complex.FromPolarCoordinates(
                    DriveScale * _vsMagnitude[i], _tones[i].PhaseRad);
                break;
            }
        }

        int brDrive = mna.AddBranch();
        mna.AddBranchCurrent(brDrive, nDrv, nRef);
        mna.AddConstraint(brDrive, nDrv, new Complex(+1, 0));
        if (nRef > 0) mna.AddConstraint(brDrive, nRef, new Complex(-1, 0));
        mna.AddSourceValue(brDrive, driveV);

        // Z_Port: harmonic-band impedance between nExt and nDrv.
        StampZPort(mna, nExt, nDrv, GetZ(omega));
    }

    // ── Impedance lookup (identical band rule to P1Tone) ──────────────────────────

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

    private static void StampZPort(IMnaContext mna, int na, int nb, Complex z)
    {
        int br = mna.AddBranch();
        mna.AddBranchCurrent(br, na, nb);
        mna.AddConstraint(br, na, new Complex(+1, 0));
        if (nb > 0) mna.AddConstraint(br, nb, new Complex(-1, 0));
        mna.AddBranchConstraint(br, br, -z);
    }
}

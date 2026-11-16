using System.Numerics;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Ideal (lossless) transmission line — 2-port, ground-referenced (linear-engine §4.x).
/// Nodal-admittance stamp (Group 1 — no branch-current unknowns).
///
/// Nodes (2 signal terminals, common reference = ground "0"):
///   Nodes[0] = port-1 signal, Nodes[1] = port-2 signal.
/// Both ports are referenced to ground; the reference net is implicit (never a terminal),
/// so the netlister emits only the two signal nets.
///
/// Parameters (units already applied by the elaborator):
///   Z = characteristic impedance Z₀ (Ω, real).
///   E = electrical length at the reference frequency F (RADIANS, real — the elaborator has
///       already applied the deg→rad unit scale, so authored "E=90 deg" arrives as π/2).
///   F = reference frequency (Hz).
///   A = total attenuation at F, in dB (OPTIONAL, additive — brief-L5a-pcell-contract-and-
///       microstrip.md R-pc-11 / microstrip-models.md R3: MLIN reuses this ideal-line stamp with
///       its own dispersive, per-frequency loss; a bare "TLIN:" block gets this simpler,
///       always-available knob for a lossy ideal line). Default 0 → byte-identical lossless
///       behavior for every pre-existing "TLIN:" instance (no `.cnl`/test regression).
///       Scales with frequency the SAME way E does (θ ∝ f, so a matching αl ∝ f is the
///       consistent "ideal line" convention — a real per-unit-length loss is NOT linear in f in
///       general (skin effect ~ √f), but this block has no physical length to derive a true
///       frequency-dependent α from; a component wanting real dispersive loss (MLIN) computes
///       its own αl per frequency and calls <see cref="StampUniformLine"/> directly instead of
///       going through this parameter).
///
/// The electrical length is proportional to frequency (θ = βl, β ∝ f for an ideal line),
/// so at an arbitrary stamping frequency f = ω/2π:
///   θ(f) = E·(f / F)   [radians, with E already in radians]
///   αl(f) = (A/8.686)·(f / F)   [nepers, dB→Np via /8.686; 0 when A is unset]
///
/// Lossy-line Y-parameters (referenced to ground), with γl = αl + jθ:
///   Y11 = Y22 = coth(γl) / Z₀
///   Y12 = Y21 = −1 / (Z₀·sinh(γl))
/// which reduce EXACTLY to the lossless cot/csc forms when αl = 0 (coth(jθ) = −j·cot θ,
/// −1/sinh(jθ) = −1/(j·sin θ) = j/sin θ) — the shared <see cref="StampUniformLine"/> helper
/// stamps this general form; this class is one of its two callers.
/// stamped as the 2×2 nodal block on (Nodes[0], Nodes[1]) with ground as the common return.
///
/// <b>The physical form</b> (brief-artsch-2): <c>L</c> (metres) and <c>Eeff</c> instead of <c>E</c>, so a line
/// read off artwork is stated as what it is. θ(f) = 2π·f·L·√Eeff / c₀ — exact for a non-dispersive TEM
/// line, with no reference frequency in it. Its loss is per length and scales physically:
///   αl(f) = (Ac·√(f/F) + Ad·(f/F))·L / 8.686   [Ac conductor, Ad dielectric, both dB/m at F]
/// <see cref="ComponentModelFactory"/> decides the form and refuses a mixture; this class only stamps it.
///
/// Resonance guard: at θ = kπ with αl≈0 (sinh(γl) → 0) the open/short Y-parameters diverge.
/// <see cref="StampUniformLine"/> clamps |sinh γl| to a small floor; this class additionally
/// warns once per instance (research-tool philosophy: warn-and-continue, matching
/// MutualInductanceModel) using its own sin(θ) check so the warning text names the degenerate
/// frequency exactly as before. DC (ω = 0 → θ = 0) is the same degeneracy and is clamped
/// identically.
/// </summary>
public sealed class TLineModel : ComponentModel, IDcShortedTerminals, IUniformLine
{
    /// <summary>Lossless at DC (every loss term here vanishes at 0 Hz): a short from port 1 to port 2.</summary>
    public IReadOnlyList<(int A, int B)> DcShortedTerminals { get; } = [(0, 1)];

    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;

    private readonly double _z0;        // characteristic impedance Z₀ (Ω)
    private readonly double _eRad;      // electrical length at F (RADIANS — elaborator already applied deg→rad)
    private readonly double _fRefHz;    // reference frequency F (Hz)
    private readonly double _aDb;       // total attenuation at F, in dB (0 = lossless, the pre-existing behavior)

    // The physical form (brief-artsch-2). _lengthM is NaN in the angle form, which is what IsPhysical reads.
    private readonly double _lengthM = double.NaN;
    private readonly double _eeff = 1.0;
    private readonly double _acDbPerM;  // conductor attenuation at F, dB/m, ∝ √f
    private readonly double _adDbPerM;  // dielectric attenuation at F, dB/m, ∝ f

    /// <summary>True when the line was stated by <c>L</c>/<c>Eeff</c> rather than <c>E</c>/<c>F</c>.</summary>
    public bool IsPhysical => !double.IsNaN(_lengthM);

    /// <summary>The physical length in metres; NaN in the angle form.</summary>
    public double LengthMeters => _lengthM;

    /// <summary>The effective permittivity of the physical form; 1 in the angle form.</summary>
    public double Eeff => _eeff;

    /// <summary>The reference frequency in Hz, or 0 where none applies (a lossless physical line
    /// that states no <c>F</c>).</summary>
    public double ReferenceFrequencyHz => _fRefHz;

    // Warn once per instance (not once per frequency point).
    private bool _warnedDegenerate;

    /// <summary>Floor for |sin θ| to keep the open/short Y-parameters finite at θ = kπ (lossless case).</summary>
    private const double SinFloor = 1e-9;

    /// <summary>dB → Np conversion: Np = dB / (20·log10(e)) = dB / 8.685889638...</summary>
    private const double DbPerNp = 8.685889638065035;

    /// <param name="electricalLengthRad">Electrical length at F, in RADIANS. The elaborator has
    /// already applied the parameter's angle unit (deg→rad via Units.Scale), so an authored
    /// "E=90 deg" arrives here as π/2. Do NOT re-apply π/180.</param>
    /// <param name="attenuationDb">Total attenuation at F, in dB. 0 (default) is the original
    /// lossless behavior, byte-for-byte.</param>
    public TLineModel(double z0Ohms, double electricalLengthRad, double refFreqHz, string name,
        double attenuationDb = 0.0)
    {
        _z0     = z0Ohms;
        _eRad   = electricalLengthRad;
        _fRefHz = refFreqHz;
        _aDb    = attenuationDb;
        _ = name;   // reserved for future diagnostics; instance path is used for warnings
    }

    /// <summary>The physical form: a line of <paramref name="lengthMeters"/> in a medium of effective
    /// permittivity <paramref name="eeff"/>, with conductor and dielectric attenuation stated in dB/m at
    /// <paramref name="refFreqHz"/> (which may be 0 when both are 0). Validation is the factory's.</summary>
    public static TLineModel Physical(double z0Ohms, double lengthMeters, double eeff, double refFreqHz,
        double conductorDbPerM, double dielectricDbPerM, string name)
        => new(z0Ohms, lengthMeters, eeff, refFreqHz, conductorDbPerM, dielectricDbPerM, name);

    private TLineModel(double z0Ohms, double lengthMeters, double eeff, double refFreqHz,
        double conductorDbPerM, double dielectricDbPerM, string name)
        : this(z0Ohms, 0.0, refFreqHz, name)
    {
        _lengthM  = lengthMeters;
        _eeff     = eeff;
        _acDbPerM = conductorDbPerM;
        _adDbPerM = dielectricDbPerM;
    }

    /// <summary>The electrical length θ in radians at <paramref name="freqHz"/>, in either form.</summary>
    public double ElectricalLengthRad(double freqHz)
        => IsPhysical ? 2.0 * Math.PI * freqHz * _lengthM * Math.Sqrt(_eeff) / MicrostripLoss.SpeedOfLight
         : _eRad * (_fRefHz != 0.0 ? freqHz / _fRefHz : 0.0);

    /// <summary>The total attenuation αl in nepers at <paramref name="freqHz"/>, in either form.</summary>
    private double AttenuationNp(double freqHz)
    {
        double freqRatio = _fRefHz != 0.0 ? freqHz / _fRefHz : 0.0;
        if (!IsPhysical) return (_aDb / DbPerNp) * freqRatio;
        if (_acDbPerM == 0.0 && _adDbPerM == 0.0) return 0.0;
        return (_acDbPerM * Math.Sqrt(freqRatio) + _adDbPerM * freqRatio) * _lengthM / DbPerNp;
    }

    /// <summary>
    /// brief-trn-4b — the phase (rad) the line's loss comes with (<see cref="Planar.CausalLoss"/>): the physical form's
    /// conductor term (∝ √f) adds its own attenuation; the electrical form's <c>A</c> and the physical form's dielectric
    /// term (both ∝ f) add the dielectric phase pinned at <c>F</c>, where they are stated. 0 for a lossless line.
    /// </summary>
    private double LossPhaseRad(double freqHz)
    {
        if (!(freqHz > 0) || !(_fRefHz > 0)) return 0;
        double ratio = freqHz / _fRefHz;
        if (!IsPhysical) return _aDb == 0 ? 0 : Planar.CausalLoss.Phase(freqHz, 0, 0, _aDb / DbPerNp * ratio, _fRefHz);
        if (_acDbPerM == 0.0 && _adDbPerM == 0.0) return 0;
        return Planar.CausalLoss.Phase(freqHz, _acDbPerM * Math.Sqrt(ratio) * _lengthM / DbPerNp, 0,
                                       _adDbPerM * ratio * _lengthM / DbPerNp, _fRefHz);
    }

    public (double Z0, Complex GammaLength) LineAt(double freqHz) =>
        (_z0, new Complex(AttenuationNp(freqHz), ElectricalLengthRad(freqHz) + LossPhaseRad(freqHz)));

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        double freqHz = omega / (2.0 * Math.PI);

        // Electrical length at this frequency: θ = E·(f / F), with E already in radians (the
        // elaborator applied the deg→rad unit). For an ideal line β ∝ f, so the reference (E, F)
        // pair fixes the delay and θ scales linearly with frequency.
        // Guard a zero/unset reference frequency (avoid divide-by-zero): treat as θ = 0, αl = 0.
        // The physical form needs no F for θ: 2π·f·L·√Eeff / c₀.
        double theta = ElectricalLengthRad(freqHz);
        double alphaLNp = AttenuationNp(freqHz);

        // Resonance / DC degeneracy warning: only meaningful in the lossless case (a real αl > 0
        // already keeps sinh(γl) away from zero on its own).
        if (alphaLNp == 0.0 && Math.Abs(Math.Sin(theta)) < SinFloor && !_warnedDegenerate)
        {
            Console.Error.WriteLine(
                $"[circuitRF] TLIN:{c.InstancePath}: sin(θ)≈0 at f={freqHz:G6} Hz " +
                $"(θ={theta * 180.0 / Math.PI:G6}°, a quarter-wave open/short resonance); " +
                $"clamping |sinh γl| to a floor and proceeding.");
            _warnedDegenerate = true;
        }

        var gammaLength = new Complex(alphaLNp, theta + LossPhaseRad(freqHz));
        StampUniformLine(mna, c.Nodes[0], c.Nodes[1], new Complex(_z0, 0.0), gammaLength);
    }

    /// <summary>Floor for |sinh γl| to keep the open/short Y-parameters finite at a resonance
    /// (γl = jkπ for some integer k, the lossless quarter-wave-multiple degeneracy).</summary>
    private const double SinhFloor = 1e-9;

    /// <summary>
    /// The canonical uniform-transmission-line nodal-admittance stamp, shared by every model that
    /// is electrically an ideal line at a given (Z₀, γl) — <see cref="TLineModel"/> itself
    /// (lossless or the simple linear-in-frequency lossy case above) and, per brief-L5a-pcell-
    /// contract-and-microstrip.md R-pc-11 / microstrip-models.md R3, the microstrip line PCell
    /// (<c>MicrostripLineModel</c>), which computes a genuinely dispersive Z₀(f)/γ(f) per
    /// frequency (Hammerstad-Jensen + Kirschning-Jansen + loss) and calls this SAME helper rather
    /// than re-deriving the stamp — "one implementation," never a second copy that could disagree.
    /// </summary>
    /// <param name="z0">Characteristic impedance at this frequency (real for the low-loss
    /// quasi-TEM approximation every caller in this codebase uses; the type is Complex only so a
    /// future caller with a genuinely complex Z₀ is not blocked).</param>
    /// <param name="gammaLength">γ·l = αl + jβl — total attenuation (Np) and electrical length
    /// (rad) over the line's full length, at this frequency.</param>
    public static void StampUniformLine(IMnaContext mna, int n1, int n2, Complex z0, Complex gammaLength)
    {
        Complex sinhGl = Complex.Sinh(gammaLength);
        Complex coshGl = Complex.Cosh(gammaLength);

        if (sinhGl.Magnitude < SinhFloor)
        {
            // Preserve direction (the lossless θ=kπ case is purely imaginary); an exact zero
            // (γl = 0, i.e. a zero-length line) has no defined direction — default to +j, matching
            // the lossless model's θ→0⁺ convention.
            var dir = sinhGl.Magnitude > 0.0 ? sinhGl / sinhGl.Magnitude : Complex.ImaginaryOne;
            sinhGl = dir * SinhFloor;
        }

        Complex yDiag = coshGl / (z0 * sinhGl);        // Y11 = Y22 =  coth(γl) / Z₀
        Complex yOff  = -Complex.One / (z0 * sinhGl);  // Y12 = Y21 = −1 / (Z₀·sinh(γl))

        // 2×2 nodal admittance block (ground = common return, node 0 entries auto-dropped):
        //   [ Y11  Y12 ] [V1]   so KCL row n1 gets +Y11·V1 + Y12·V2, row n2 gets +Y21·V1 + Y22·V2.
        //   [ Y21  Y22 ] [V2]
        mna.AddBlockAdmittance(n1, n1, yDiag);
        mna.AddBlockAdmittance(n2, n2, yDiag);
        mna.AddBlockAdmittance(n1, n2, yOff);
        mna.AddBlockAdmittance(n2, n1, yOff);
    }
}

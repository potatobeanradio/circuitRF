using System.Numerics;
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
///   Z = characteristic impedance Z₀ (Ω, real — the line is lossless).
///   E = electrical length at the reference frequency F (RADIANS, real — the elaborator has
///       already applied the deg→rad unit scale, so authored "E=90 deg" arrives as π/2).
///   F = reference frequency (Hz).
///
/// The electrical length is proportional to frequency (θ = βl, β ∝ f for an ideal line),
/// so at an arbitrary stamping frequency f = ω/2π:
///   θ(f) = E·(f / F)   [radians, with E already in radians]
///
/// Lossless-line Y-parameters (referenced to ground):
///   Y11 = Y22 = −j·cot(θ) / Z₀
///   Y12 = Y21 = +j / (Z₀·sin(θ))
/// stamped as the 2×2 nodal block on (Nodes[0], Nodes[1]) with ground as the common return.
///
/// Resonance guard: at θ = kπ (sin θ → 0) the open/short Y-parameters diverge. The stamp
/// clamps |sin θ| to a small floor and warns once per instance (research-tool philosophy:
/// warn-and-continue, matching MutualInductanceModel). DC (ω = 0 → θ = 0) is the same
/// degeneracy and is clamped identically.
/// </summary>
public sealed class TLineModel : ComponentModel
{
    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;

    private readonly double _z0;        // characteristic impedance Z₀ (Ω)
    private readonly double _eRad;      // electrical length at F (RADIANS — elaborator already applied deg→rad)
    private readonly double _fRefHz;    // reference frequency F (Hz)

    // Warn once per instance (not once per frequency point).
    private bool _warnedDegenerate;

    /// <summary>Floor for |sin θ| to keep the open/short Y-parameters finite at θ = kπ.</summary>
    private const double SinFloor = 1e-9;

    /// <param name="electricalLengthRad">Electrical length at F, in RADIANS. The elaborator has
    /// already applied the parameter's angle unit (deg→rad via Units.Scale), so an authored
    /// "E=90 deg" arrives here as π/2. Do NOT re-apply π/180.</param>
    public TLineModel(double z0Ohms, double electricalLengthRad, double refFreqHz, string name)
    {
        _z0     = z0Ohms;
        _eRad   = electricalLengthRad;
        _fRefHz = refFreqHz;
        _ = name;   // reserved for future diagnostics; instance path is used for warnings
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        double freqHz = omega / (2.0 * Math.PI);

        // Electrical length at this frequency: θ = E·(f / F), with E already in radians (the
        // elaborator applied the deg→rad unit). For an ideal line β ∝ f, so the reference (E, F)
        // pair fixes the delay and θ scales linearly with frequency.
        // Guard a zero/unset reference frequency (avoid divide-by-zero): treat as θ = 0.
        double theta = _fRefHz != 0.0
            ? _eRad * (freqHz / _fRefHz)
            : 0.0;

        double sin = Math.Sin(theta);
        double cos = Math.Cos(theta);

        // Resonance / DC degeneracy: clamp |sin θ| away from zero so cot(θ) and csc(θ) stay finite.
        if (Math.Abs(sin) < SinFloor)
        {
            if (!_warnedDegenerate)
            {
                Console.Error.WriteLine(
                    $"[circuitRF] TLIN:{c.InstancePath}: sin(θ)≈0 at f={freqHz:G6} Hz " +
                    $"(θ={theta * 180.0 / Math.PI:G6}°, a quarter-wave open/short resonance); " +
                    $"clamping |sin θ| to {SinFloor:G1} and proceeding.");
                _warnedDegenerate = true;
            }
            sin = sin < 0.0 ? -SinFloor : SinFloor;
        }

        // Lossless-line nodal Y-parameters (referenced to ground).
        //   Y_series (port↔port) = −Y12 ;  Y_self (port↔ground) = Y11 + Y12
        // We stamp the raw 2×2 nodal block directly; ground rows/cols are dropped by the engine.
        var yDiag = new Complex(0.0, -cos / (_z0 * sin));   // Y11 = Y22 = −j·cot θ / Z₀
        var yOff  = new Complex(0.0,  1.0 / (_z0 * sin));   // Y12 = Y21 = +j·csc θ / Z₀

        int n1 = c.Nodes[0];
        int n2 = c.Nodes[1];

        // 2×2 nodal admittance block (ground = common return, node 0 entries auto-dropped):
        //   [ Y11  Y12 ] [V1]   so KCL row n1 gets +Y11·V1 + Y12·V2, row n2 gets +Y21·V1 + Y22·V2.
        //   [ Y21  Y22 ] [V2]
        mna.AddBlockAdmittance(n1, n1, yDiag);
        mna.AddBlockAdmittance(n2, n2, yDiag);
        mna.AddBlockAdmittance(n1, n2, yOff);
        mna.AddBlockAdmittance(n2, n1, yOff);
    }
}

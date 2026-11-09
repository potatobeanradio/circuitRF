using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices;

/// <summary>
/// The oscillator analysis's marker (brief NZ-9). A two-terminal part placed in series in the oscillator's loop
/// (<c>Mode=series</c>, the default) or across its resonator (<c>Mode=shunt</c>), where the oscillation is strong.
///
/// <para><b>In every other analysis it does nothing:</b> a short in series (a 0 V branch, like an IProbe), an open in
/// shunt (no stamp at all).</para>
///
/// <para><b>Under the oscillator analysis</b> the engine arms it with a generator of amplitude <see cref="DriveAmplitude"/>
/// at the angular frequency <see cref="DriveOmega"/>, and it is that generator at that one frequency and the idle part at
/// every other:</para>
/// <list type="bullet">
/// <item><b>shunt</b> — a voltage generator <c>A</c> behind a conductance <see cref="DriveConductance"/>, in Norton form:
/// <c>G·A</c> injected into the first net and out of the second, with <c>G</c> across them. Open elsewhere. The generator
/// delivers <c>G·(A − V)</c>, so it does no work exactly when the voltage across the part is <c>A</c>.</item>
/// <item><b>series</b> — a current generator <c>A</c> flowing first net → second net through the part, with
/// <see cref="DriveConductance"/> across it. Short elsewhere. It does no work exactly when the voltage across it is zero,
/// that is when the loop current is <c>A</c> with the loop closed.</item>
/// </list>
/// <para>The conductance makes the driven harmonic-balance problem well posed and changes nothing at the oscillation, where
/// the generator carries no current (shunt) or holds no voltage (series).</para>
///
/// .cnl syntax: <c>OscProbe:&lt;label&gt;  n1  n2  [Mode=series|shunt]</c>.
/// </summary>
public sealed class OscProbeModel : ComponentModel
{
    public override int       PortCount     => 1;
    public override ModelKind Kind          => ModelKind.Linear;
    public override string[]  TerminalNames => ["+", "-"];

    /// <summary>The <c>Mode</c> key's two spellings.</summary>
    public const string ModeSeries = "series", ModeShunt = "shunt";

    /// <summary>True when the part's <c>Mode</c> is <c>shunt</c>; anything else is series.</summary>
    public static bool IsShunt(ElaboratedComponent c)
        => c.Parameters.TryGetValue("Mode", out var m) && m.Kind == ValueKind.String
           && m.AsString().Trim().Equals(ModeShunt, StringComparison.OrdinalIgnoreCase);

    /// <summary>The generator's angular frequency, or 0 when the part is idle (every analysis but the oscillator's).</summary>
    public double DriveOmega { get; set; }

    /// <summary>The generator's phasor: volts in shunt, amperes in series.</summary>
    public Complex DriveAmplitude { get; set; }

    /// <summary>The generator's own conductance, siemens.</summary>
    public double DriveConductance { get; set; }

    /// <summary>The branch index of the series short (or the series generator) on the most recent stamp, or −1.</summary>
    public int LastBranchIndex { get; private set; } = -1;

    /// <summary>Disarms the generator: the part is idle again.</summary>
    public void Disarm() { DriveOmega = 0; DriveAmplitude = Complex.Zero; DriveConductance = 0; }

    private bool AtDrive(double omega)
        => DriveOmega > 0 && Math.Abs(omega - DriveOmega) <= 1e-9 * DriveOmega;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        if (c.Nodes.Length < 2) return;
        int a = c.Nodes[0], b = c.Nodes[1];
        bool armed = AtDrive(omega);

        if (IsShunt(c))
        {
            LastBranchIndex = -1;
            if (!armed) return;                      // open at every other frequency
            var g = new Complex(DriveConductance, 0);
            mna.AddAdmittance(a, b, g);
            mna.AddCurrentInjection(a, g * DriveAmplitude);
            mna.AddCurrentInjection(b, -g * DriveAmplitude);
            return;
        }

        // Series: one branch at every frequency, so the system has the same unknowns whichever frequency is stamped.
        int br = mna.AddBranch();
        LastBranchIndex = br;
        mna.AddBranchCurrent(br, a, b);
        if (!armed)
        {
            mna.AddConstraint(br, a, Complex.One);   // a short: V(a) − V(b) = 0
            mna.AddConstraint(br, b, -Complex.One);
            mna.AddSourceValue(br, Complex.Zero);
            return;
        }
        mna.AddBranchConstraint(br, br, Complex.One); // the generator: its branch current is A
        mna.AddSourceValue(br, DriveAmplitude);
        mna.AddAdmittance(a, b, new Complex(DriveConductance, 0));
    }
}

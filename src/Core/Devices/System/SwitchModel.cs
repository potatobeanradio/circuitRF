using System.Numerics;

namespace CircuitRF.Core.Devices;

/// <summary>What an OPEN throw looks like from its own port.</summary>
public enum SwitchOffState
{
    /// <summary>An open circuit: <c>S = 1</c> at that port. What a series-FET or PIN switch does.</summary>
    Reflective,

    /// <summary>Terminated in its own reference impedance: <c>S = 0</c> at that port.</summary>
    Absorptive,
}

/// <summary>
/// The ideal RF switch — SPST, SPDT, or any throw count — as a frequency-flat S-matrix.
///
/// <para><b>One component, two tiles.</b> <c>Switch</c> (SPST, 2 ports, 4 nets) and <c>SwitchD</c>
/// (SPDT, 3 ports, 6 nets) are the same engine component with a different <c>Throws</c>, seeded per
/// tile by the registry — the <c>Mixer</c>/<c>MixerD</c> arrangement, for the same reason: nothing
/// electrical distinguishes them beyond how many throws exist, so there must not be two models.
/// Port 0 is the common port; ports 1…<c>Throws</c> are the throws, in the order the glyph numbers
/// them.</para>
///
/// <para><b><c>State</c> is a plain number, and that is the feature.</b> It names which throw is
/// closed — <c>0</c> means none of them is, so an SPST reads <c>0</c> as open and an SPDT reads it
/// as both throws open. Being an ordinary swept parameter, a parametric sweep over <c>State</c>
/// gives every switch position in one run, and the schematic glyph follows it (SYS-1), so the
/// position reads off the drawing rather than out of the sweep definition. A <c>State</c> naming a
/// throw that does not exist closes nothing, which is the same answer by the same rule rather than
/// a special case.</para>
///
/// <para><b>The S-matrix, derived in full.</b> Write <c>ρ = 10^(−RL/20)</c> (0 exactly at ≥ 150 dB),
/// <c>ι = 10^(−IL/20)</c>, <c>σ = 10^(−Isolation/20)</c> (0 exactly at ≥ 150 dB), and give each
/// throw <c>p</c> its own transmission to the common port:</para>
/// <code>
///   t_p = ι   if p is the closed throw          (the path the switch is making)
///   t_p = σ   otherwise                          (what leaks past an open one)
///
///   S[0,p] = S[p,0] = t_p                        common ↔ throw p
///   S[p,q] = S[q,p] = t_p·t_q     (p ≠ q)        throw ↔ throw, through the common node
///   S[p,p] = ρ                    if p is on the closed path (the common port and the closed throw)
///   S[p,p] = 1                    otherwise, Reflective   — an open throw is an open circuit
///   S[p,p] = 0                    otherwise, Absorptive   — an open throw is Z0 to its reference
/// </code>
/// <para><b>The two throws of an SPDT are NOT symmetric</b>, which is the whole content of that
/// table: at <c>State = 1</c> the closed throw carries <c>ι</c> and the open one <c>σ</c>, and the
/// two throws see each other only through the product of the paths the state leaves them —
/// <c>ι·σ</c>, a signal that reaches the far throw by leaking to the common node and being carried
/// on from there. That is why the throw-to-throw term vanishes exactly when the isolation is off:
/// there is nothing for it to be a product WITH. At <c>State = 0</c> both throws carry <c>σ</c> and
/// see each other through <c>σ²</c>.</para>
///
/// <para>So the default SPDT (<c>State</c> 1, <c>IL</c> 0 dB, <c>Isolation</c> and <c>RL</c> at 200,
/// <c>Reflective</c>) stamps exactly</para>
/// <code>
///   S = [[0, 1, 0],
///        [1, 0, 0],
///        [0, 0, 1]]
/// </code>
/// <para>— an ideal wire from common to throw 1, and an ideal open at throw 2. Neither of those has
/// a Z matrix; both are stamped without a special case, which is why this family exists.</para>
///
/// <para><b>An open reflective switch is not passive-by-construction, and is not refused.</b> With a
/// finite isolation, <c>S = [[1, σ],[σ, 1]]</c> has a singular value above one. A user is allowed to
/// type numbers a real part could not have; this model refuses only what cannot be stamped.</para>
/// </summary>
public sealed class SwitchModel : IdealSBlockModel
{
    private readonly Complex[,] _sFlat;
    private readonly int        _throws;

    /// <param name="throws">Throw count: 1 is SPST, 2 is SPDT. Below 1 falls back to 1.</param>
    /// <param name="state">Which throw is closed; 0 (or any number that is not a throw) closes none.</param>
    /// <param name="ilDb">Insertion loss of the closed path, dB.</param>
    /// <param name="isolationDb">Open-path leakage, dB; ≥ 150 means none.</param>
    /// <param name="offState">What an open throw looks like from its own port.</param>
    /// <param name="z0">Reference impedance of every port, ohms; may be complex.</param>
    /// <param name="returnLossDb">Return loss of the closed path; ≥ 150 means exactly matched.</param>
    public SwitchModel(int throws, int state, double ilDb, double isolationDb,
                       SwitchOffState offState, Complex z0, double returnLossDb)
        : base(Z0For(throws, z0))
    {
        _throws = throws >= 1 ? throws : 1;

        double thru = AmplitudeFromDb(ilDb);
        double iso  = SuppressedAmplitude(isolationDb);
        double refl = SuppressedAmplitude(returnLossDb);
        double open = offState == SwitchOffState.Reflective ? 1.0 : 0.0;

        int n = 1 + _throws;
        bool anythingClosed = state >= 1 && state <= _throws;

        // t[p] for the throws; t[0] is unused (the common port is not a throw).
        var t = new double[n];
        for (int p = 1; p < n; p++) t[p] = p == state ? thru : iso;

        _sFlat = new Complex[n, n];

        // Diagonal: a port on the closed path is matched to its stated return loss; every other
        // port is whatever the off state says an open throw is.
        _sFlat[0, 0] = new Complex(anythingClosed ? refl : open, 0);
        for (int p = 1; p < n; p++)
            _sFlat[p, p] = new Complex(p == state ? refl : open, 0);

        // Common ↔ throw, and throw ↔ throw through the common node.
        for (int p = 1; p < n; p++)
        {
            _sFlat[0, p] = _sFlat[p, 0] = new Complex(t[p], 0);
            for (int q = p + 1; q < n; q++)
                _sFlat[p, q] = _sFlat[q, p] = new Complex(t[p] * t[q], 0);
        }
    }

    private static Complex[] Z0For(int throws, Complex z0)
    {
        int n = 1 + (throws >= 1 ? throws : 1);
        var z = new Complex[n];
        Array.Fill(z, z0);
        return z;
    }

    /// <summary>
    /// <c>"1"</c>/<c>"2"</c> for the SPST, whose two pins are interchangeable and carry numbers only;
    /// <c>"com"</c> then the throws' own numerals for every larger throw count, matching the glyph.
    /// </summary>
    public override string[] TerminalNames
    {
        get
        {
            if (_throws == 1) return ["1", "2"];
            var names = new string[1 + _throws];
            names[0] = "com";
            for (int p = 1; p <= _throws; p++) names[p] = p.ToString();
            return names;
        }
    }

    protected override void FillS(double omega, Complex[,] s)
        => Array.Copy(_sFlat, s, _sFlat.Length);
}

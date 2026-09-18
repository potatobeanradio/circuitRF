using System.Numerics;
using NumFlat;

// NAMESPACE, deliberately flat — see IdealSBlockModel.cs's own header for why the `System` FOLDER
// does not become a namespace segment.
namespace CircuitRF.Core.Devices;

/// <summary>
/// Is a system block's own S-matrix passive, and by how much is it not?
///
/// <para><b>Why this exists.</b> Every block in the System family except the amplifier declares
/// <see cref="Core.Activity.Passive"/>, and that declaration is a CLAIM about numbers the user
/// typed rather than a property of the code. The family writes its S-matrix entries as REAL,
/// IN-PHASE amplitudes — an insertion loss, a return loss and an isolation are each converted
/// straight from dB with no phase — so the suppression terms add coherently with the through path
/// in a way no real part's do. A 0.5 dB switch with 18 dB of return loss has
/// <c>|S11 + S21| = 1.07</c>: passive per port, and not passive as a matrix. Nothing refused it,
/// nothing reported it, and it showed up two components away as a return loss that read +1.2 dB.</para>
///
/// <para><b>The test is σ_max(S) ≤ 1</b>, which is <c>I − SᴴS ⪰ 0</c> written as a singular value —
/// the same test <see cref="SnpModel"/> applies to a Touchstone file and <see cref="ChainModel"/>
/// to its evaluated ABCD, through the same <see cref="RfCore.RFNetwork.Passivity(Mat{Complex})"/>.
/// One passivity routine in this repository, not three.</para>
///
/// <para><b>Complex and per-port reference impedances are fine here</b>, and that is worth stating
/// because <c>RFNetwork.Passivity</c>'s own remarks warn about them. Its caution is about
/// TOUCHSTONE data, whose normalization is not guaranteed to be power waves. This family's S is
/// defined by <see cref="IdealSBlockModel.StampWaveConstraints"/> against Kurokawa power waves, for
/// which <c>P = |a|² − |b|²</c> holds per port at any complex reference — so <c>σ_max ≤ 1</c> is
/// exactly "this block delivers no more power than it absorbs", whatever the ports are referenced
/// to.</para>
/// </summary>
internal static class SystemBlockPassivity
{
    /// <summary>
    /// How far above 1 σ_max may sit before the block is reported. The same <c>1e-6</c>
    /// <see cref="SnpModel"/> and <see cref="ChainModel"/> use, and for the same reason: an ideal
    /// lossless block — a closed switch, a through, an ideal circulator — sits at σ_max = 1 exactly
    /// and must never be reported, so the tolerance has to clear the arithmetic's own noise and
    /// nothing more.
    /// </summary>
    internal const double Tolerance = 1e-6;

    /// <summary>
    /// True when the block is passive and it can be shown without a singular-value decomposition.
    ///
    /// <para><c>σ_max ≤ √(‖S‖₁·‖S‖∞)</c> — the largest column sum times the largest row sum, both
    /// of absolute values. It is one pass over N² entries against an O(N³) factorization, and it
    /// accepts the common cases outright: a filter in its passband, an attenuator, a matched
    /// through. It never reports a block as passive that is not, because it is an UPPER bound; when
    /// it fails to prove anything, <see cref="SigmaMax"/> answers.</para>
    /// </summary>
    internal static bool ProvablyPassive(Complex[,] s, int n)
    {
        double worstRow = 0.0, worstCol = 0.0;
        for (int p = 0; p < n; p++)
        {
            double row = 0.0, col = 0.0;
            for (int q = 0; q < n; q++)
            {
                row += s[p, q].Magnitude;
                col += s[q, p].Magnitude;
            }
            if (row > worstRow) worstRow = row;
            if (col > worstCol) worstCol = col;
        }
        return Math.Sqrt(worstRow * worstCol) <= 1.0 + Tolerance;
    }

    /// <summary>σ_max(S) — the number the report quotes, and the exact test when the bound above
    /// could not settle it.</summary>
    internal static double SigmaMax(Complex[,] s, int n)
    {
        var m = new Mat<Complex>(n, n);
        for (int p = 0; p < n; p++)
            for (int q = 0; q < n; q++)
                m[p, q] = s[p, q];
        return RfCore.RFNetwork.Passivity(m);
    }

    /// <summary>
    /// σ_max when the block is not passive, and null when it is — the whole check in one call, with
    /// the cheap bound tried first.
    /// </summary>
    internal static double? ExcessOf(Complex[,] s, int n)
    {
        if (n <= 0 || ProvablyPassive(s, n)) return null;
        double sigma = SigmaMax(s, n);
        return sigma > 1.0 + Tolerance ? sigma : null;
    }

    /// <summary>
    /// The message. It names the instance, says how much power the block manufactures and where,
    /// and — because this is almost never a typo but a combination of three separately reasonable
    /// datasheet numbers — says what the combination is.
    /// </summary>
    /// <param name="instancePath">The placed instance, by its full downward path.</param>
    /// <param name="typeName">The block's own type, so the message reads without the schematic.</param>
    /// <param name="sigmaMax">σ_max(S) as measured.</param>
    /// <param name="omega">
    /// Where it was measured, rad/s — or <b>null</b> when the block's S does not depend on
    /// frequency, in which case the message names none. A switch is not passive at 2 GHz in the
    /// same sense that it is not passive at every other frequency, and quoting the first one a run
    /// happened to stamp it at sends the reader looking for something frequency-dependent.
    /// </param>
    internal static (string Key, string Message) Report(
        string instancePath, string typeName, double sigmaMax, double? omega)
    {
        string at = omega switch
        {
            null       => "",
            { } w when Math.Abs(w) > 0 => $" at {Math.Abs(w) / (2.0 * Math.PI) / 1e9:G6} GHz",
            _          => " at DC",
        };
        double excessDb = 20.0 * Math.Log10(sigmaMax);

        return ($"system.block-not-passive:{instancePath}",
            $"system.block-not-passive: '{instancePath}' ({typeName}) is declared passive and is not" +
            $"{at} — σ_max(S) = {sigmaMax:G6}, so it delivers up to {excessDb:F2} dB more power than " +
            "it absorbs. The System blocks build their S-matrix from real, in-phase amplitudes, so an " +
            "insertion loss, a return loss and an isolation that are each individually plausible can " +
            "still sum above unity; raise the suppressions (RL, Isolation, Directivity) or accept more " +
            "loss until they do not. Results downstream of this block are affected: a reflective load " +
            "behind it can read a return loss above 0 dB.");
    }
}

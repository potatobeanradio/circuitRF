using System.Numerics;

namespace CircuitRF.Core.Devices;

/// <summary>
/// The ideal N-way combiner, which is also the ideal N-way splitter: a matched, reciprocal,
/// frequency-flat (N+1)-port with a common port S and N paths into it. Port 0 is S; ports 1…N are
/// the paths, in the order the glyph numbers them. 2(N+1) nets, <c>[S+, S−, 1+, 1−, …, N+, N−]</c>;
/// the single-ended tile ties each port's − net to ground at extraction.
///
/// <para><b>Its S-matrix, in full.</b> Path k has its own loss <c>IL_k</c> beyond the ideal split and
/// its own transmission phase <c>φ_k</c>:</para>
/// <code>
///   S[0,k] = S[k,0] = 10^(−IL_k/20) · exp(j·φ_k) / √N      S ↔ path k
///   every other entry = 0                                  matched, and the paths isolated
/// </code>
/// <para>That is what an N-way Wilkinson is at its centre frequency, without the lines: driven at S
/// it splits into N equal parts, and N equal inputs in phase add in amplitude at S. Lossless, it
/// sends all of N coherent equal inputs to S and none of anything else, because
/// <c>I − SᴴS</c> is then the projection away from the in-phase input — the part the isolation
/// resistors of a real Wilkinson absorb.</para>
///
/// <para><b>Its noise is its S.</b> It is passive, so the shared stamp's thermal noise from S
/// (<see cref="IdealSBlockModel"/>) is all there is: from S into path k the noise figure is the
/// path's whole loss, <c>10·log10 N + IL_k</c>; and N branches whose noise is uncorrelated, combined
/// at S, keep their noise while their coherent signal gains <c>10·log10 N</c>.</para>
///
/// <para><b>A phase per path, not a phase for the block.</b> A common phase on every path is
/// invisible to anything the block connects, so there is none; <c>φ_k</c> is what a steered array
/// or an arriving plane wave puts on each element, and an ordinary swept expression.</para>
/// </summary>
public sealed class CombinerModel : IdealSBlockModel
{
    private readonly Complex[] _t;

    /// <param name="ways">N, the number of paths; below 2 falls back to 2.</param>
    /// <param name="pathLossDb">Each path's loss beyond the ideal split, dB, one entry per path.</param>
    /// <param name="pathPhaseRad">Each path's transmission phase, radians, one entry per path.</param>
    /// <param name="z0">Reference impedance of every port, ohms; may be complex.</param>
    public CombinerModel(int ways, IReadOnlyList<double> pathLossDb, IReadOnlyList<double> pathPhaseRad, Complex z0)
        : base(Enumerable.Repeat(z0, 1 + Math.Max(2, ways)).ToArray())
    {
        int n = Math.Max(2, ways);
        _t = new Complex[n];
        for (int k = 0; k < n; k++)
            _t[k] = Complex.FromPolarCoordinates(
                AmplitudeFromDb(k < pathLossDb.Count ? pathLossDb[k] : 0) / Math.Sqrt(n),
                k < pathPhaseRad.Count ? pathPhaseRad[k] : 0);
    }

    /// <summary>The common port, then the paths' own numerals, matching the glyph.</summary>
    public override string[] TerminalNames => ["S", .. Enumerable.Range(1, _t.Length).Select(k => k.ToString())];

    protected override void FillS(double omega, Complex[,] s)
    {
        for (int k = 0; k < _t.Length; k++)
            s[0, k + 1] = s[k + 1, 0] = _t[k];
    }
}

using System.Numerics;

namespace CircuitRF.WBond.Mom;

/// <summary>
/// <b>D(ω)</b> — the per-segment internal impedance, the <b>only</b> frequency-dependent quantity in
/// kernel W1, and diagonal.
///
/// <code>
/// (rPerMetre, lIntPerMetre) = InternalImpedance.PerMetre(f, radius, sigma)
/// D[k](ω) = rPerMetre · l_k  +  j·ω·lIntPerMetre · l_k
/// </code>
///
/// <para>Identical to <c>ImpedanceReduction.WireInternalImpedance</c> with the segment's length in
/// place of the wire's path length. <b>Because the scaling is by length and lengths add, D summed over
/// a wire's segments equals that wire's own D exactly</b> — which is half of the identity gate against
/// the analytic model; the other half is that partial inductance is additive under subdivision.</para>
///
/// <h3>The cache is per (radius, sigma), not per segment</h3>
/// <para>The Bessel evaluation behind <see cref="InternalImpedance.PerMetre"/> is the only
/// transcendental work in a frequency step. An array of identical wires has <b>one</b> distinct
/// (radius, sigma) pair however many thousand segments it meshes to, so grouping turns a per-segment
/// Bessel call into a single one.</para>
/// </summary>
public sealed class SegmentInternalZ
{
    private readonly double[] _radius;    // per group
    private readonly double[] _sigma;     // per group
    private readonly int[] _group;        // per segment
    private readonly double[] _length;    // per segment, metres

    private SegmentInternalZ(double[] radius, double[] sigma, int[] group, double[] length)
    {
        _radius = radius;
        _sigma = sigma;
        _group = group;
        _length = length;
    }

    /// <summary>N_s.</summary>
    public int SegmentCount => _group.Length;

    /// <summary>How many distinct (radius, sigma) pairs the design has — one Bessel evaluation each.</summary>
    public int GroupCount => _radius.Length;

    public static SegmentInternalZ Create(WireMomMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        int n = mesh.SegmentCount;
        var group = new int[n];
        var radii = new List<double>();
        var sigmas = new List<double>();

        for (int k = 0; k < n; k++)
        {
            double r = mesh.SegmentRadius[k], s = mesh.SegmentSigma[k];

            int found = -1;
            for (int g = 0; g < radii.Count; g++)
                if (radii[g] == r && sigmas[g] == s) { found = g; break; }

            if (found < 0)
            {
                found = radii.Count;
                radii.Add(r);
                sigmas.Add(s);
            }
            group[k] = found;
        }

        return new SegmentInternalZ([.. radii], [.. sigmas], group, (double[])mesh.SegmentLength.Clone());
    }

    /// <summary>
    /// The diagonal of <b>D</b> at one frequency, in ohms. One Bessel evaluation per
    /// <see cref="GroupCount"/>, then a multiply per segment.
    /// </summary>
    public Complex[] Diagonal(double frequencyHz)
    {
        var d = new Complex[_group.Length];
        FillDiagonal(frequencyHz, d);
        return d;
    }

    /// <summary>The same, into a caller-owned buffer — the sweep path allocates once and refills.</summary>
    public void FillDiagonal(double frequencyHz, Complex[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Length < _group.Length)
            throw new ArgumentException(
                $"Need room for {_group.Length} segments, got {destination.Length}.", nameof(destination));

        double omega = 2.0 * Math.PI * frequencyHz;

        var r = new double[_radius.Length];
        var li = new double[_radius.Length];
        for (int g = 0; g < _radius.Length; g++)
        {
            var (rPerMetre, lIntPerMetre) = InternalImpedance.PerMetre(frequencyHz, _radius[g], _sigma[g]);
            r[g] = rPerMetre;
            li[g] = omega * lIntPerMetre;
        }

        for (int k = 0; k < _group.Length; k++)
        {
            int g = _group[k];
            destination[k] = new Complex(r[g] * _length[k], li[g] * _length[k]);
        }
    }
}

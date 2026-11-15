// brief-paa-7 — a Phased Antenna Array's S held sparse across its record's frequencies: one pattern of stored entries (the coupling extension's,
// which depends on the lattices and not on the values) and its values at each frequency, interpolated entry by entry exactly as an EM Field Data
// block's SnP is (SnpFit: a cubic spline in magnitude and unwrapped phase, linear below three frequencies, clamped and warned outside), so a
// sparse array and the dense S it replaced agree at every frequency.

using System.Numerics;
using RfCore;

namespace CircuitRF.Core.Devices.Arrays;

/// <summary>brief-paa-7 — an array's S, sparse, over its record's frequencies.</summary>
public sealed class SparseNetwork
{
    private readonly ArraySparseMatrix _pattern;
    private readonly Complex[][] _values;
    private RFNetwork.Spline1D[]? _mag, _phase;
    private bool _warned;
    private readonly object _gate = new();

    public SparseNetwork(double[] frequencies, ArraySparseMatrix pattern, Complex[][] values)
    {
        Frequencies = frequencies;
        _pattern = pattern;
        _values = values;
    }

    public double[] Frequencies { get; }

    /// <summary>The S at record frequency <paramref name="f"/> (an index).</summary>
    public ArraySparseMatrix AtIndex(int f) => _pattern.WithValues(_values[f]);

    /// <summary>The S at <paramref name="hz"/>.</summary>
    public ArraySparseMatrix At(double hz)
    {
        int nf = Frequencies.Length, nz = _pattern.NonZeros;
        lock (_gate)
        {
            if (_mag is null)
            {
                bool linear = nf < 3;
                _mag = new RFNetwork.Spline1D[nz];
                _phase = new RFNetwork.Spline1D[nz];
                for (int k = 0; k < nz; k++)
                {
                    var m = new double[nf];
                    var ph = new double[nf];
                    for (int f = 0; f < nf; f++) { m[f] = _values[f][k].Magnitude; ph[f] = _values[f][k].Phase; }
                    RFNetwork.PhaseUnwrap(ph);
                    _mag[k] = new RFNetwork.Spline1D(Frequencies, m, InterpolationMethod.CubicSpline, linear);
                    _phase[k] = new RFNetwork.Spline1D(Frequencies, ph, InterpolationMethod.CubicSpline, linear);
                }
            }
            if (!_warned && (hz < Frequencies[0] || hz > Frequencies[^1]))
            {
                _warned = true;
                string side = hz < Frequencies[0] ? "below" : "above";
                RFNetwork.Warn($"Interpolation target(s) extend {side} the stored range [{Frequencies[0] / 1e9:G4}–{Frequencies[^1] / 1e9:G4} GHz]. " +
                               "Out-of-range values will be clamped to the nearest endpoint.");
            }
        }
        var v = new Complex[nz];
        for (int k = 0; k < nz; k++) v[k] = Complex.FromPolarCoordinates(_mag[k].Eval(hz), _phase![k].Eval(hz));
        return _pattern.WithValues(v);
    }
}

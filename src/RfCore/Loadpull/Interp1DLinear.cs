// ================================================================
//  Interp1DLinear.cs — 1-D linear interpolator
//
//  Matches scipy.interpolate.interp1d(x, y, kind='linear',
//  bounds_error=False), which returns NaN for out-of-range queries.
//
//  SPLData.py relies on the NaN fill to drop unsupported points.
// ================================================================

using System;

namespace RfCore.Loadpull;

public sealed class Interp1DLinear
{
    private readonly double[] _x;
    private readonly double[] _y;
    private readonly int      _n;

    /// <param name="x">Ascending x coordinates (throws if not sorted).</param>
    /// <param name="y">Corresponding y values.</param>
    public Interp1DLinear(ReadOnlySpan<double> x, ReadOnlySpan<double> y)
    {
        if (x.Length != y.Length)
            throw new ArgumentException("x and y must have the same length.");
        if (x.Length < 2)
            throw new ArgumentException("At least 2 points are required for linear interpolation.");

        for (int i = 1; i < x.Length; i++)
        {
            if (x[i] <= x[i - 1])
                throw new ArgumentException(
                    $"x must be strictly ascending; x[{i - 1}]={x[i - 1]}, x[{i}]={x[i]}.");
        }

        _x = x.ToArray();
        _y = y.ToArray();
        _n = x.Length;
    }

    /// <summary>
    /// Linear interpolation at <paramref name="xq"/>.
    /// Returns NaN for out-of-range inputs (scipy bounds_error=False behaviour).
    /// </summary>
    public double Eval(double xq)
    {
        if (xq < _x[0] || xq > _x[_n - 1]) return double.NaN;

        // Binary search for the interval [_x[lo], _x[lo+1]] containing xq
        int lo = 0, hi = _n - 2;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_x[mid] <= xq) lo = mid; else hi = mid - 1;
        }

        double t = (xq - _x[lo]) / (_x[lo + 1] - _x[lo]);
        return _y[lo] + t * (_y[lo + 1] - _y[lo]);
    }

    /// <summary>
    /// Evaluate at many points into a caller-supplied buffer.
    /// result.Length must equal xs.Length.
    /// </summary>
    public void Eval(ReadOnlySpan<double> xs, Span<double> result)
    {
        for (int i = 0; i < xs.Length; i++)
            result[i] = Eval(xs[i]);
    }
}

// ================================================================
//  AffineStamp.cs  —  a linear network's G and C from its frequency-domain stamps
//
//  Every lumped model stamps Y(w) = G + jw C: a resistor G, a capacitor jwC, an
//  inductor's branch row -R - jwL. Two stamps a hair apart recover both exactly,
//  with no per-model work, for any stamp that is affine in jw:
//
//    around DC:        C = (A(2d) - A(d)) / (jd),          G = A(d) - jd C
//    around w (> 0):   C = (A(w+d) - A(w-d)) / (2jd),      A0 = A(w)
//
//  The envelope engine steps each harmonic's product with these (its k = 0 and k > 0
//  products, the multi-fundamental lattice and the memory estimate); the transient
//  engine steps the baseband pair, real, from DC up (brief-trn-1 R-trn1-3). One
//  copy, so the two engines cannot recover a different network from one netlist.
// ================================================================

using System.Numerics;
using CircuitRF.Engine.HarmonicBalance;
using CSparse;
using CSparse.Complex;
using CSparse.Storage;

namespace CircuitRF.Engine;

internal static class AffineStamp
{
    /// <summary>The stamp spacing around <paramref name="omega"/> (rad/s) every recovery uses: a part in a million of the
    /// frequency, at least 2π rad/s.</summary>
    internal static double Delta(double omega) => Math.Max(1e-6 * Math.Max(omega, 2 * Math.PI * 1e6), 2 * Math.PI * 1.0);

    /// <summary>The linear partition of <paramref name="ex"/> stamped at <paramref name="omega"/>, sources active.</summary>
    internal static CompressedColumnStorage<Complex> Stamp(HbLinearExtractor ex, MnaSystem mna, double omega)
    {
        ex.StampSmallSignal(mna, omega);
        return mna.BuildCsc();
    }

    /// <summary>G and C around DC, from stamps at <paramref name="dlt"/> and 2·<paramref name="dlt"/>.</summary>
    internal static (CompressedColumnStorage<Complex> G, CompressedColumnStorage<Complex> C) Baseband(
        HbLinearExtractor ex, MnaSystem mna, double dlt)
    {
        var a1 = Stamp(ex, mna, dlt);
        var a2 = Stamp(ex, mna, 2 * dlt);
        return Baseband(a1, a2, dlt);
    }

    /// <summary>G and C around DC from the two stamps <paramref name="a1"/> (at dlt) and <paramref name="a2"/> (at 2·dlt).</summary>
    internal static (CompressedColumnStorage<Complex> G, CompressedColumnStorage<Complex> C) Baseband(
        CompressedColumnStorage<Complex> a1, CompressedColumnStorage<Complex> a2, double dlt)
    {
        var c = Combine(a2, a1, 1.0 / new Complex(0, dlt), -1.0 / new Complex(0, dlt));
        var g = Combine(a1, c, 1.0, -new Complex(0, dlt));
        return (g, c);
    }

    /// <summary>The stamp at <paramref name="w"/> and C from stamps either side of it, <paramref name="dlt"/> away.</summary>
    internal static (CompressedColumnStorage<Complex> A0, CompressedColumnStorage<Complex> C) AtHarmonic(
        HbLinearExtractor ex, MnaSystem mna, double w, double dlt)
    {
        var ap = Stamp(ex, mna, w + dlt);
        var am = Stamp(ex, mna, w - dlt);
        var c = Combine(ap, am, 1.0 / new Complex(0, 2 * dlt), -1.0 / new Complex(0, 2 * dlt));
        var a0 = Stamp(ex, mna, w);
        return (a0, c);
    }

    /// <summary>a·A + b·B over the union pattern.</summary>
    internal static CompressedColumnStorage<Complex> Combine(CompressedColumnStorage<Complex> A, CompressedColumnStorage<Complex> B, Complex a, Complex b)
    {
        int n = A.RowCount;
        if (B.RowCount != n || A.ColumnCount != B.ColumnCount)
            throw new InvalidOperationException("the linear network's size changes with frequency (a model adds unknowns only at some frequencies)");
        var dict = new Dictionary<long, Complex>();
        void Add(CompressedColumnStorage<Complex> M, Complex s)
        {
            for (int j = 0; j < M.ColumnCount; j++)
                for (int p = M.ColumnPointers[j]; p < M.ColumnPointers[j + 1]; p++)
                {
                    long key = (long)M.RowIndices[p] * n + j;
                    dict[key] = (dict.TryGetValue(key, out var v) ? v : 0) + s * M.Values[p];
                }
        }
        Add(A, a); Add(B, b);
        var tri = new CoordinateStorage<Complex>(n, A.ColumnCount, dict.Count);
        foreach (var kv in dict) tri.At((int)(kv.Key / n), (int)(kv.Key % n), kv.Value);
        return SparseMatrix.OfIndexed(tri);
    }
}

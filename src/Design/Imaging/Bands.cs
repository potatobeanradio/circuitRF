// Deterministic parallelism for the raster core — brief-img-1-raster-core.md R-im1-9 (D17).
//
// A loop over rows (or columns) is cut into bands of a FIXED size that does not depend on the thread count, and each
// band writes only its own slice of the output. Nothing is reduced across bands in completion order, so a result is the
// same bytes on one thread or on sixteen. A loop that does need a reduction (a sum, a histogram) asks for the per-band
// partials and combines them itself, in band order.

namespace CircuitRF.Design.Imaging;

internal static class Bands
{
    /// <summary>Rows per band. Fixed — a band boundary that moved with the thread count would be a result that did.</summary>
    public const int Size = 64;

    /// <summary>Runs <paramref name="body"/>(start, end) over [0, <paramref name="count"/>) in fixed bands.
    /// <paramref name="maxThreads"/> 0 means the runtime's choice, 1 a plain loop.</summary>
    public static void For(int count, int maxThreads, Action<int, int> body)
    {
        int bands = (count + Size - 1) / Size;
        if (bands <= 1 || maxThreads == 1)
        {
            for (int b = 0; b < bands; b++) body(b * Size, Math.Min(count, (b + 1) * Size));
            return;
        }
        var po = new ParallelOptions { MaxDegreeOfParallelism = maxThreads <= 0 ? -1 : maxThreads };
        Parallel.For(0, bands, po, b => body(b * Size, Math.Min(count, (b + 1) * Size)));
    }
}

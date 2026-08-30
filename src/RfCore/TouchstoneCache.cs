// ================================================================
//  TouchstoneCache.cs  —  parse a Touchstone file once per process
//
//  ParametricSweepEngine re-elaborates at every sweep point by design,
//  which news every ComponentModel, so without this cache an SnP's
//  TouchstoneIO.ReadFile ran again at every point (2.6 ms for a
//  2001-point .s2p, 0.06 ms for an 84-point one) and its spline fit
//  was rebuilt with it. Keyed by full path + last-write time + length,
//  so a file the user re-saves between runs IS re-read.
//
//  No eviction: the cache holds one parsed SNP per distinct file the
//  design references, which is bounded by what the user opened, not by
//  the length of the sweep.
// ================================================================

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace RfCore
{
    /// <summary>
    /// Process-wide cache of parsed Touchstone files and their fitted interpolators.
    /// Thread-safe. The returned <see cref="SNP"/> is SHARED — treat it as immutable.
    /// </summary>
    public static class TouchstoneCache
    {
        private readonly record struct FitKey(
            InterpolationMethod Method,
            InterpolationFormat Format,
            MatrixType          InterpolateIn);

        private sealed class Entry
        {
            internal readonly long Ticks;
            internal readonly long Length;
            internal readonly SNP  Snp;

            private readonly ConcurrentDictionary<FitKey, Lazy<SnpFit>> _fits = new();

            internal Entry(string path, long ticks, long length)
            {
                Ticks  = ticks;
                Length = length;
                Snp    = TouchstoneIO.ReadFile(path);
                Interlocked.Increment(ref _parseCount);
            }

            internal SnpFit GetFit(InterpolationMethod method,
                                   InterpolationFormat format,
                                   MatrixType          interpolateIn)
                => _fits.GetOrAdd(new FitKey(method, format, interpolateIn),
                       k => new Lazy<SnpFit>(
                           () =>
                           {
                               Interlocked.Increment(ref _fitCount);
                               return new SnpFit(Snp, k.Method, k.Format, k.InterpolateIn);
                           },
                           LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        private static readonly ConcurrentDictionary<string, Lazy<Entry>> _cache = new();

        private static long _parseCount;
        private static long _fitCount;

        /// <summary>Test/diagnostic instrumentation: Touchstone parses actually performed
        /// (cache MISSES) process-wide. A small, bounded count across a many-point sweep is
        /// the direct proof that the sweep is not re-reading its files.</summary>
        public static long ParseCount => Interlocked.Read(ref _parseCount);

        /// <summary>Test/diagnostic instrumentation: spline fits actually performed (cache
        /// MISSES) process-wide.</summary>
        public static long FitCount => Interlocked.Read(ref _fitCount);

        /// <summary>Test-only: drop every cached parse and fit, and zero both counters, so a
        /// test can measure cache behaviour without pollution from whichever test ran first.</summary>
        public static void ResetForTesting()
        {
            _cache.Clear();
            Interlocked.Exchange(ref _parseCount, 0);
            Interlocked.Exchange(ref _fitCount, 0);
        }

        /// <summary>
        /// The parsed contents of <paramref name="fullPath"/>, re-reading only when the file's
        /// last-write time or length has changed since it was cached.
        /// The instance is SHARED across callers — do not mutate it.
        /// </summary>
        public static SNP Get(string fullPath) => GetEntry(fullPath).Snp;

        /// <summary>
        /// An interpolator over <paramref name="fullPath"/>, reusing the cached spline fit for
        /// these settings. A FRESH wrapper is returned each call, so the out-of-range warning is
        /// per consumer (once per model per run), not once per process.
        /// </summary>
        public static SnpInterpolator GetInterpolator(
            string              fullPath,
            InterpolationMethod method        = InterpolationMethod.CubicSpline,
            InterpolationFormat format        = InterpolationFormat.RealImag,
            MatrixType          interpolateIn = MatrixType.S,
            OutOfRangePolicy    outOfRange    = OutOfRangePolicy.WarnClamp)
            => new SnpInterpolator(
                   GetEntry(fullPath).GetFit(method, format, interpolateIn), outOfRange);

        private static Entry GetEntry(string fullPath)
        {
            string key = Path.GetFullPath(fullPath);
            var    fi  = new FileInfo(key);
            long   ticks  = fi.LastWriteTimeUtc.Ticks;
            long   length = fi.Length;

            while (true)
            {
                var lazy = _cache.GetOrAdd(key, MakeLazy(key, ticks, length));
                Entry entry = lazy.Value;
                if (entry.Ticks == ticks && entry.Length == length) return entry;

                // Stale — the file changed on disk since it was cached. Replace the entry, and
                // lose the race gracefully: whoever wins, the next loop re-validates its stamp.
                var fresh = MakeLazy(key, ticks, length);
                if (_cache.TryUpdate(key, fresh, lazy)) return fresh.Value;
            }
        }

        private static Lazy<Entry> MakeLazy(string key, long ticks, long length)
            => new Lazy<Entry>(() => new Entry(key, ticks, length),
                               LazyThreadSafetyMode.ExecutionAndPublication);
    }
}

using Xunit;

namespace CircuitRF.Harmonica.Tests;

/// <summary>
/// Every timing measurement in this project belongs to this collection, so xUnit runs them one at a
/// time rather than in parallel with each other.
///
/// <para><b>This is the L8d finding applied a third time.</b> A benchmark sharing a run with others
/// reads more than twice as slow — L9d's 71.9 s was first mis-measured at 16.79 s that way — and once
/// there were six timing methods here they contended enough to INVERT one of the comparisons: the
/// batched external solve read 7.58 ms against the unbatched 6.39 ms, where alone they are 0.50 and
/// 1.42 ms. Serialising is necessary and not sufficient, which is why each measurement is also a
/// best-of-N: another test PROJECT running concurrently is still outside this collection's reach, and
/// the minimum over a few repetitions is the estimator that survives it.</para>
///
/// <para>The reported numbers are still only trustworthy when the measurement is taken ALONE, per the
/// brief. The collection and the best-of-N exist so the ASSERTIONS do not go red on a shared run.</para>
/// </summary>
[CollectionDefinition("HarmonicaBenchmarks", DisableParallelization = true)]
public sealed class HarmonicaBenchmarkCollection;

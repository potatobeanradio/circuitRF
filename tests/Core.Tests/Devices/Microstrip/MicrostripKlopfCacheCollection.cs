namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>
/// xUnit runs test CLASSES in parallel by default. <c>MicrostripKlopfModel</c> keeps its resolved
/// geometry and section tables in a PROCESS-WIDE cache, with process-wide
/// <c>GeometryBuildCount</c>/<c>SectionTableBuildCount</c> counters beside it — that sharing is the
/// point of the design (R-mk-1: one build per distinct parameter set, reused across every analysis),
/// not an accident to be refactored away.
///
/// <para>It does mean any two classes that build MKlopf models race: one class's
/// <c>ResetCachesForTesting()</c> wipes the cache another is mid-way through populating, and its
/// models bump counters a third is asserting exact values on. That is exactly how
/// <c>MklopfPerformanceAndMessagesTests.Gate4_CurvatureScan_RunsOnce_ForGeometryThatNeverWarns</c>
/// intermittently failed with a build count of 0 or 2 — never in isolation, only under full-suite CPU
/// contention.</para>
///
/// <para>Every class that builds an MKlopf model declares <c>[Collection(Name)]</c> so xUnit
/// serializes them relative to each other. Three classes, so the cost is negligible; xUnit still
/// parallelizes across every OTHER collection, which is why this is not a blanket
/// <c>DisableTestParallelization</c>.</para>
///
/// <para><c>MicrostripKlopfEntryConversionTests</c> is included even though it only creates models
/// through <c>ComponentModelFactory.TryCreate</c> and does not stamp them today: construction is one
/// edit away from touching the same cache, and the cost of being wrong is a heisenbug that only shows
/// up in someone else's unrelated CI run.</para>
/// </summary>
[CollectionDefinition(Name)]
public class MicrostripKlopfCacheCollection
{
    public const string Name = "MicrostripKlopfModel.ProcessWideCache";
}

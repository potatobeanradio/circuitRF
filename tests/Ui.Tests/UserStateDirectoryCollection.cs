using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The test classes that move circuitRF's PER-USER STATE DIRECTORY — <c>AppDataRoot.RedirectTo</c>,
/// which is a process-global with one slot.
///
/// <para><b>Why <c>DisableParallelization</c> and not merely a shared collection.</b> A shared
/// collection would serialise its own members against each other and nothing else, and the directory
/// is read by far more classes than write it: anything that constructs the New Workspace dialog,
/// reads a preference, or asks <c>TechnologyCatalog</c> what is installed answers from wherever the
/// pointer happens to be at that instant. Listing every reader is not maintainable — a class becomes
/// a reader by touching a preference, which is invisible from its name — so the classes that WRITE
/// the pointer run alone instead.</para>
///
/// <para><b>Measured, not assumed.</b> <c>TechnologyCatalogTests</c> and
/// <c>AuthoringCliVerbTests</c> each pass in isolation (11 and 25 tests) and produce three failures
/// when run together: a technology one of them had just installed reported as "not installed", and a
/// CLI refusal listing a different installation's technologies than the in-process call it is
/// compared against. That is a statement about the scheduler, not about either test.</para>
///
/// <para><b><c>AuthoringCliVerbTests</c> moved here OUT of <c>CellStatGlobalsCollection</c>, and is
/// more protected rather than less</b> — <c>DisableParallelization</c> means nothing else in the
/// assembly runs alongside it, which subsumes that collection's promise of not overlapping the other
/// <c>CellStat</c> classes.</para>
///
/// <para>The cost is small and was measured: the two classes together are about two seconds of the
/// assembly's ~27, and they are the only members. Add a class here the moment it calls
/// <c>AppDataRoot.RedirectTo</c> or <c>UserStateDirectory.RedirectTo</c>.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UserStateDirectoryCollection
{
    public const string Name = "Per-user state directory";
}

using Xunit;

namespace CircuitRF.Ui.Tests.Lvs;

/// <summary>
/// The test classes that drive <c>CliEntry.Run</c> IN PROCESS and read what it wrote run in one
/// collection, so they never run at the same moment as each other.
/// </summary>
/// <remarks>
/// <b>Why they cannot run concurrently.</b> Capturing the verb's output means
/// <c>Console.SetOut</c>, and <c>Console.Out</c> is one process-wide writer — as is
/// <c>JsonRun</c>'s own state, which <c>--json</c> swaps the writer out through and
/// <c>JsonRun.Reset()</c> clears. xUnit runs distinct test classes in parallel by default, so two
/// classes doing this land both verbs' documents in whichever buffer was installed last: the
/// symptom is a <c>JsonReaderException</c> — <i>"'{' is invalid after a single JSON value"</i> —
/// in whichever test parsed second, and it is a statement about the scheduler rather than about
/// the verb.
///
/// <para><b>Not <c>DisableParallelization</c>.</b> These two still run in parallel with every
/// other class in the assembly; they simply run one at a time relative to each other, which is
/// the whole of what they need. Every other CLI gate in this repository launches a real process
/// and is unaffected.</para>
///
/// <para>Add a class here the moment it calls <c>CliEntry.Run</c> and reads stdout.</para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class LvsCliConsoleCollection
{
    public const string Name = "CLI stdout, in process";
}

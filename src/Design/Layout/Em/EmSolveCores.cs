// M1 (brief-em-sweep-performance) — the EM solver's core cap: where it is STORED, what the panel
// offers, and what a stored value means.
//
// R-emp-6: STORED in AppPreferences, SHOWN in the EM Setup panel. Core count is a property of the
// machine and not of the design — a `.cem` travels with the workspace, and opening a colleague's EM
// setup must not pin your core count to theirs.
//
// R-emp-7: it enters NO provenance hash, and it cannot, because it is not part of the model any hash
// is taken over. That is asserted rather than merely arranged (EmCoreCountTests).
//
// **THE STORE IS NOT HERE ANY MORE** (brief-cli-em-verb.md R-emcli-3). What lives in this file is the
// pure part: how many cores the machine has, what the panel may offer, how a stored value is
// sanitised, and what each choice reads as. The AppPreferences read/write is
// CircuitRF.Ui.Layout.Em.EmSolveCorePreference, and the cap reaches the solve as an ARGUMENT —
// EmRunService.Run's `maxCores`, which the GUI fills from that preference and a headless run leaves
// null. Headless there is no preferences file to read and no user to have set one, and a run service
// that reached for a GUI preference could not have crossed the firewall at all.

namespace CircuitRF.Design.Layout.Em;

/// <summary>
/// The core-count preference and the panel's own choice list. <b>Null means Automatic</b>, which maps
/// to <c>PlanarSolveSettings.MaxDegreeOfParallelism = null</c> — the unbounded behaviour every run
/// had before this control existed.
/// </summary>
public static class EmSolveCores
{
    /// <summary>How many cores this machine has, and therefore what the choice list is built from.</summary>
    public static int ProcessorCount => Environment.ProcessorCount;

    /// <summary>
    /// A stored value is clamped rather than trusted: a preferences file copied from a bigger machine
    /// would otherwise ask for more cores than exist, and a hand-edited 0 or −1 would reach
    /// <c>Parallel.For</c> as a framework exception with no mention of a core count in it. Anything
    /// unusable reads as Automatic, which is always a working answer.
    /// </summary>
    public static int? Sanitise(int? stored)
        => stored is { } c && c >= 1 ? Math.Min(c, ProcessorCount) : null;

    /// <summary>
    /// The panel's choices, in order: Automatic, then powers of two up to the machine's count, plus
    /// the count itself when it is not already one of them (10 cores → 1, 2, 4, 8, 10).
    /// </summary>
    public static IReadOnlyList<int?> Choices(int processorCount)
    {
        var list = new List<int?> { null };
        for (int n = 1; n <= processorCount; n *= 2) list.Add(n);
        if (processorCount >= 1 && list[^1] != processorCount) list.Add(processorCount);
        return list;
    }

    /// <inheritdoc cref="Choices(int)"/>
    public static IReadOnlyList<int?> Choices() => Choices(ProcessorCount);

    /// <summary>What a choice reads as. Automatic names the count it resolves to, so the default is
    /// not a word with no number behind it.</summary>
    public static string Label(int? cap, int processorCount)
        => cap is null   ? $"Automatic ({processorCount} core{(processorCount == 1 ? "" : "s")})"
         : cap == 1      ? "1 core"
         :                 $"{cap} cores";

    /// <inheritdoc cref="Label(int?, int)"/>
    public static string Label(int? cap) => Label(cap, ProcessorCount);

    /// <summary>The choice list the panel binds to, label and value together so the view needs no
    /// converter and the two can never drift.</summary>
    public static IReadOnlyList<EmSolveCoreChoice> ChoiceRows(int processorCount)
        => [.. Choices(processorCount).Select(c => new EmSolveCoreChoice(c, Label(c, processorCount)))];

    /// <inheritdoc cref="ChoiceRows(int)"/>
    public static IReadOnlyList<EmSolveCoreChoice> ChoiceRows() => ChoiceRows(ProcessorCount);
}

/// <summary>One entry in the panel's core-count combo. <see cref="Cap"/> null is Automatic.</summary>
public sealed record EmSolveCoreChoice(int? Cap, string Label);

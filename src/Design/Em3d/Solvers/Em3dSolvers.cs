// brief-emi-1 R-emi1-1c — THE list of 3D solvers.
//
// Every place that offers or enumerates the solvers — the Solver picker, `em --solver`, `solver list`, the MCP tool's
// option text, the readiness check — reads this list, so adding a solver is a line here and a folder beside this file.
// The order is fixed: it is the order a comparison runs them in and the order every list shows them.

using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

public static class Em3dSolvers
{
    /// <summary>Every 3D solver, in order.</summary>
    public static IReadOnlyList<Em3dSolverDescriptor> All { get; } = [PalaceSolver.Descriptor, OpenEmsSolver.Descriptor, EmergeSolver.Descriptor];

    /// <summary>The solver a new 3D setup starts on (overview D4: the FEM default).</summary>
    public static Em3dSolverChoice Default => PalaceSolver.Id;

    /// <summary>What the format's <c>Both</c> means, and always will: Palace then openEMS, compared against Palace.</summary>
    public static Em3dSolverChoice Both { get; } = Em3dSolverChoice.Compare([PalaceSolver.Id, OpenEmsSolver.Id], PalaceSolver.Id);

    private const string NoneSpelling = "None";
    private const string BothSpelling = "Both";

    /// <summary>brief-emi-6 R-emi6-1 — the key <c>Solver3D</c>'s spelling of any comparison but <see cref="Both"/>; its set and
    /// reference are the setup's <c>Compare</c> block.</summary>
    public const string CompareSpelling = "Compare";

    /// <summary>O4 — a comparison's reference when none is named: Palace when it is chosen, else the first chosen.</summary>
    public static Em3dSolverId DefaultReference(IReadOnlyCollection<Em3dSolverId> chosen)
        => chosen.Contains(PalaceSolver.Id) ? PalaceSolver.Id : InOrder(chosen)[0];

    /// <summary><paramref name="solvers"/> in the registry's order, each once.</summary>
    public static IReadOnlyList<Em3dSolverId> InOrder(IEnumerable<Em3dSolverId> solvers)
    {
        var set = solvers.ToHashSet();
        return [.. All.Select(d => d.Id).Where(set.Contains)];
    }

    /// <summary>The descriptor of <paramref name="id"/>, offered or still integrating.</summary>
    public static Em3dSolverDescriptor Get(Em3dSolverId id)
        => All.FirstOrDefault(d => d.Id == id)
           ?? throw new ArgumentOutOfRangeException(nameof(id), id, "No such 3D solver.");

    /// <summary>The descriptors of the solvers <paramref name="choice"/> runs, in the order it runs them.</summary>
    public static IReadOnlyList<Em3dSolverDescriptor> Of(Em3dSolverChoice choice) => [.. choice.Solvers.Select(Get)];

    /// <summary>Whether any solver <paramref name="choice"/> runs has <paramref name="capability"/>.</summary>
    public static bool Any(Em3dSolverChoice choice, Func<Em3dSolverCapabilities, bool> capability)
        => choice.Solvers.Any(id => capability(Get(id).Capabilities));

    /// <summary>The first solver <paramref name="choice"/> runs that has <paramref name="capability"/>, or null.</summary>
    public static Em3dSolverDescriptor? FirstWith(Em3dSolverChoice choice, Func<Em3dSolverCapabilities, bool> capability)
        => Of(choice).FirstOrDefault(d => capability(d.Capabilities));

    /// <summary>Every solver (installed or not) that has <paramref name="capability"/>, in order.</summary>
    public static IReadOnlyList<Em3dSolverDescriptor> With(Func<Em3dSolverCapabilities, bool> capability)
        => [.. All.Where(d => capability(d.Capabilities))];

    /// <summary>The solvers whose method is <paramref name="method"/>, by name: <c>Palace</c>, or <c>A and B</c>.</summary>
    public static string NamesOf(Em3dSolverFit method) => Names(All.Where(d => d.Method == method));

    /// <summary><paramref name="solvers"/>' names joined as a sentence lists them: <c>A</c>, <c>A and B</c>, <c>A, B and C</c>.</summary>
    public static string Names(IEnumerable<Em3dSolverDescriptor> solvers)
    {
        var names = solvers.Select(d => d.DisplayName).ToList();
        return names.Count <= 1 ? string.Concat(names) : string.Join(", ", names[..^1]) + " and " + names[^1];
    }

    /// <summary>The programs every solver needs, each once, in order — what <c>solver list</c> lists and Settings ▸
    /// Solvers shows.</summary>
    public static IReadOnlyList<SolverTool> Tools => [.. All.SelectMany(d => d.Tools).Distinct()];

    /// <summary>
    /// brief-emi-6 R-emi6-2 — which solvers are installed: every program each needs found, and one circuitRF runs. Each
    /// program is looked for once and costs one short version probe, so a caller with a window calls this off its UI thread.
    /// </summary>
    public static IReadOnlyDictionary<Em3dSolverId, bool> Installed()
    {
        var found = Tools.ToDictionary(t => t, t => SolverDiscovery.For(t) is var d && d.Find(out _) is { } i && d.Runs(i));
        return All.ToDictionary(d => d.Id, d => d.Tools.All(t => found[t]));
    }

    /// <summary>
    /// brief-icp-10 — the solver a new 3D setup takes when nobody has chosen one: <see cref="Default"/> when it is installed, else
    /// the first installed of EMerge and openEMS, else <see cref="Default"/> (a setup with nothing installed says so when run).
    /// A user who installed only EMerge was handed a Palace setup that refused to run. Probes each program once, so a caller
    /// with a window calls it off its UI thread.
    /// </summary>
    public static Em3dSolverChoice DefaultInstalled()
    {
        var installed = Installed();
        foreach (var id in new[] { PalaceSolver.Id, EmergeSolver.Id, OpenEmsSolver.Id })
            if (installed.TryGetValue(id, out bool yes) && yes) return id;
        return Default;
    }

    /// <summary>The solver whose <see cref="Em3dSolverDescriptor.ResultToken"/> is <paramref name="token"/>, any case.</summary>
    public static Em3dSolverDescriptor? ByToken(string token)
        => All.FirstOrDefault(d => string.Equals(d.ResultToken, token, StringComparison.OrdinalIgnoreCase));

    /// <summary>The solver named <paramref name="displayName"/> (<c>Palace</c>, <c>openEMS</c>), or null.</summary>
    public static Em3dSolverDescriptor? ByName(string? displayName)
        => All.FirstOrDefault(d => d.DisplayName == displayName);

    /// <summary><c>em --solver</c>'s words: each solver's token, then <c>both</c> and <c>compare</c>.</summary>
    public static IReadOnlyList<string> CliWords
        => [.. All.Select(d => d.ResultToken), BothSpelling.ToLowerInvariant(), CompareSpelling.ToLowerInvariant()];

    /// <summary><see cref="CliWords"/> as a sentence lists them: <c>palace, openems, emerge, both or compare</c>, and that a
    /// comma list compares those solvers.</summary>
    public static string CliWordList
        => string.Join(", ", CliWords.SkipLast(1)) + " or " + CliWords[^1] + ", or a comma list of solvers to compare (" +
           string.Join(",", All.Take(2).Select(d => d.ResultToken)) + ")";

    /// <summary>The one choice <c>em --solver &lt;word&gt;</c> names — a solver's token or <c>both</c> — or null. A list and
    /// <c>compare</c> are <see cref="ParseCliSolvers"/>'s.</summary>
    public static Em3dSolverChoice? FromCliWord(string word)
        => string.Equals(word, BothSpelling, StringComparison.OrdinalIgnoreCase) ? Both
         : ByToken(word) is { } d ? d.Id : null;

    /// <summary>
    /// brief-emi-6 R-emi6-5 — <c>em --solver</c>'s value and <c>--reference</c>, as a choice: a solver's token, <c>both</c>
    /// (Palace and openEMS), <c>compare</c> (<paramref name="setupChoice"/>'s own set, which must be a comparison), or a comma
    /// list of tokens (registry order, each once). <paramref name="reference"/> names the comparison's reference; omitted, it
    /// is the setup's own when the set is the setup's, else <see cref="DefaultReference"/>. Returns the refusal's sentence
    /// when the words name no choice.
    /// </summary>
    public static (Em3dSolverChoice? Choice, string? Refusal) ParseCliSolvers(string value, string? reference, Em3dSolverChoice setupChoice)
    {
        IReadOnlyList<Em3dSolverId> set;
        Em3dSolverId? setupReference = null;
        if (string.Equals(value, CompareSpelling, StringComparison.OrdinalIgnoreCase))
        {
            if (!setupChoice.IsCompare)
                return (null, $"--solver compare runs the setup's own comparison, and this setup's Solver3D is {setupChoice}; name the " +
                              $"solvers to compare instead (--solver {string.Join(",", All.Take(2).Select(d => d.ResultToken))}).");
            set = setupChoice.Solvers;
            setupReference = setupChoice.Reference;
        }
        else if (!value.Contains(','))
        {
            if (FromCliWord(value) is not { } one) return (null, $"--solver takes {CliWordList}, not '{value}'.");
            if (reference is null) return (one, null);
            if (!one.IsCompare) return (null, "--reference names a comparison's reference; --solver names one solver.");
            set = one.Solvers;
        }
        else
        {
            var ids = new List<Em3dSolverId>();
            foreach (string word in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (ByToken(word) is not { } d)
                    return (null, $"--solver lists '{word}', which names no 3D solver; a list takes {string.Join(", ", All.Select(x => x.ResultToken))}.");
                if (ids.Contains(d.Id)) return (null, $"--solver lists {d.ResultToken} twice.");
                ids.Add(d.Id);
            }
            if (ids.Count < 2) return (null, "--solver's list compares two or more solvers; name one alone without a comma.");
            set = InOrder(ids);
        }

        Em3dSolverId chosenReference;
        if (reference is null) chosenReference = setupReference ?? DefaultReference([.. set]);
        else if (ByToken(reference) is not { } r) return (null, $"--reference names no 3D solver: it takes {string.Join(", ", set.Select(i => Get(i).ResultToken))}, not '{reference}'.");
        else if (!set.Contains(r.Id)) return (null, $"--reference {r.ResultToken} is not one of the solvers compared ({string.Join(", ", set.Select(i => Get(i).ResultToken))}).");
        else chosenReference = r.Id;
        return (Em3dSolverChoice.Compare(set, chosenReference), null);
    }

    // ── R-emi1-3 — the format's spellings ─────────────────────────────────────────────────────────

    /// <summary>The spellings the key <c>Solver3D</c> accepts, as a refusal lists them.</summary>
    public static IReadOnlyList<string> Spellings => [.. All.Select(d => d.Spelling), BothSpelling, CompareSpelling];

    /// <summary>The key <c>Solver3D</c>'s spelling of <paramref name="choice"/>: <c>None</c>, a solver's own, <c>Both</c> (Palace
    /// and openEMS against Palace, exactly), or <c>Compare</c> for any other comparison.</summary>
    public static string Spelling(Em3dSolverChoice choice)
        => choice.IsNone ? NoneSpelling
         : !choice.IsCompare ? Get(choice.Reference).Spelling
         : choice == Both ? BothSpelling
         : CompareSpelling;

    /// <summary>Whether <paramref name="spelling"/> is <c>Compare</c>, whose set is the <c>Compare</c> block's.</summary>
    public static bool IsCompareSpelling(string? spelling) => string.Equals(spelling, CompareSpelling, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// brief-emi-6 R-emi6-1 — the comparison a <c>Compare</c> block names: <paramref name="solvers"/> by their <c>Solver3D</c>
    /// spellings (any case, any order; held in registry order) and <paramref name="reference"/> among them (omitted: O4's
    /// default). Returns the refusal's sentence when the block names no comparison.
    /// </summary>
    public static (Em3dSolverChoice? Choice, string? Refusal) FromCompareBlock(IReadOnlyList<string>? solvers, string? reference)
    {
        string Known() => string.Join(", ", All.SkipLast(1).Select(d => d.Spelling)) + " or " + All[^1].Spelling;
        if (solvers is null) return (null, "Solver3D is Compare, and the setup has no Compare block naming its Solvers.");
        var ids = new List<Em3dSolverId>();
        foreach (string name in solvers)
        {
            if (BySpelling(name) is not { } d)
                return (null, $"Compare names '{name}', which no 3D solver this version of circuitRF answers to; its Solvers take {Known()}.");
            if (ids.Contains(d.Id)) return (null, $"Compare names {d.Spelling} twice.");
            ids.Add(d.Id);
        }
        if (ids.Count < 2) return (null, $"Compare names {ids.Count} solver{(ids.Count == 1 ? "" : "s")}; a comparison takes two or more.");
        var set = InOrder(ids);
        if (reference is null) return (Em3dSolverChoice.Compare(set, DefaultReference(set)), null);
        if (BySpelling(reference) is not { } r || !set.Contains(r.Id))
            return (null, $"Compare's Reference '{reference}' is not one of its Solvers ({string.Join(", ", set.Select(i => Get(i).Spelling))}).");
        return (Em3dSolverChoice.Compare(set, r.Id), null);
    }

    private static Em3dSolverDescriptor? BySpelling(string spelling)
        => All.FirstOrDefault(d => string.Equals(d.Spelling, spelling, StringComparison.OrdinalIgnoreCase));

    /// <summary>The choice spelt <paramref name="spelling"/> (any case, as the format has always read it), or null. <c>Compare</c>
    /// is null here: its set is the block's (<see cref="FromCompareBlock"/>).</summary>
    public static Em3dSolverChoice? FromSpelling(string spelling)
        => string.Equals(spelling, NoneSpelling, StringComparison.OrdinalIgnoreCase) ? Em3dSolverChoice.None
         : string.Equals(spelling, BothSpelling, StringComparison.OrdinalIgnoreCase) ? Both
         : All.FirstOrDefault(d => string.Equals(d.Spelling, spelling, StringComparison.OrdinalIgnoreCase)) is { } d
             ? d.Id
         : null;

    /// <summary>A <c>Solver3D</c> spelling no solver answers to, refused with the ones that do.</summary>
    public static string UnknownSpelling(string spelling)
        => $"Solver3D '{spelling}' names no 3D solver this version of circuitRF knows; it takes {string.Join(", ", Spellings.SkipLast(1))} " +
           $"or {Spellings[^1]}.";
}

using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout.Em.ParametricEm;

namespace CircuitRF.Cli;

/// <summary>
/// brief-sdl-6 R-sdl6-1 / D5 — the run verbs' <c>--em on|off</c> and <c>--em-max-solves N</c>, and what they do before a
/// verb elaborates: the chain the verb dispatches is switched to the bench <see cref="EmModelSwitch"/> builds when it runs
/// by EM, its EM evaluations are counted at every point it will run, and a run needing more uncached solves than the
/// limit (default 20) stops there, before anything is solved. The CLI asks nobody: the count is printed and the limit is
/// the answer a dialog would have given. Nothing here decides anything <c>src/Design</c> does not already decide.
/// </summary>
internal sealed class EmModelRun
{
    /// <summary>The default <c>--em-max-solves</c>.</summary>
    public const int DefaultMaxSolves = 20;

    /// <summary><c>--em on</c> (true), <c>--em off</c> (false), or neither (null: as each analysis says).</summary>
    public bool? Em { get; private set; }

    public int MaxSolves { get; private set; } = DefaultMaxSolves;

    /// <summary>
    /// Takes <c>--em</c> / <c>--em-max-solves</c> at <paramref name="i"/> when it is one; true when consumed (with
    /// <paramref name="i"/> moved past its value). A malformed value is a refusal in <paramref name="refusal"/>.
    /// </summary>
    public bool TryTake(string verb, string[] args, ref int i, out int? refusal)
    {
        refusal = null;
        switch (args[i])
        {
            case "--em" when i + 1 < args.Length:
                string v = args[++i].Trim().ToLowerInvariant();
                if (v is "on") Em = true;
                else if (v is "off") Em = false;
                else refusal = JsonRun.Fail(CliDiagnostics.EmSwitchMalformed(verb, args[i]));
                return true;
            case "--em-max-solves" when i + 1 < args.Length:
                if (int.TryParse(args[++i], out int n) && n >= 0) MaxSolves = n;
                else refusal = JsonRun.Fail(CliDiagnostics.EmSwitchMalformed(verb, args[i]));
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Applies the switch to <paramref name="lib"/>/<paramref name="tb"/> for the chain the verb will dispatch: the one
    /// <see cref="ChainSelector"/> selects for <paramref name="analysisName"/> (the verb's <c>--analysis</c>, or null) among
    /// chains whose base satisfies <paramref name="isBase"/> — the same selection the verb's own dispatch makes, so a named
    /// sweep or a name in another case is switched exactly as it is run. Returns the scope the verb runs in (dispose it when
    /// the run ends), or null with <paramref name="exit"/> set when the run stops here.
    /// </summary>
    public IDisposable? Prepare(string input, string? refBase, ref Library lib, ref TestBench tb, Func<Analysis, bool> isBase,
                                string? analysisName, out int exit)
    {
        exit = 0;
        _sweepTop = false;
        ParametricEmProvider.Register();
        if (Em == false) (tb, lib) = EmModelSwitch.CircuitOnly(tb, lib);

        var top = ChainSelector.Select(tb, analysisName, isBase, "", "").Selected;
        if (top is null || Em == false || ChainSelector.BaseOfChain(top, tb) is not { } chainBase || !isBase(chainBase))
            return EmEvaluationScope.Begin(control: RunHost.Control);   // the verb refuses a chain of another kind itself

        bool whole = EmModelSwitch.IsEm(top, tb, Em);
        if (!whole && !EmModelSwitch.HasInstancesSetToEm(tb, lib)) return EmEvaluationScope.Begin(control: RunHost.Control);

        var switched = EmModelSwitch.Apply(tb, lib, Path.GetFullPath(input), EmModelSwitch.SetupOf(top, tb), refBase,
                                           out var refusal, whole);
        if (switched is null)
        {
            exit = JsonRun.Fail(CliDiagnostics.EmModelRefused(top.Name, refusal ?? ""));
            return null;
        }
        if (switched.PathA is not null) Console.Error.WriteLine(switched.Describe(top.Name));

        var count = EmSolveCount.Count(switched.Tb, switched.Lib, top, refBase, RunHost.Control);
        if (count.Any)
        {
            Console.Error.WriteLine("EM: " + count.Sentence);
            JsonRun.Note(CliDiagnostics.EmSolveCount(count.Sentence));
        }
        foreach (var line in count.Unreached)
        {
            Console.Error.WriteLine("Warning: " + line);
            JsonRun.Note(CliDiagnostics.EmUnreached(line));
        }
        foreach (var line in count.Notes)
        {
            Console.Error.WriteLine("EM: " + line);
            JsonRun.Note(CliDiagnostics.EmThreeDNote(line));
        }
        if (CircuitEvaluation.EmLimitRefusal(count, MaxSolves) is { } over)
        {
            exit = count.Refusals.Count > 0
                ? JsonRun.Fail(CliDiagnostics.EmModelRefused(top.Name, over))
                : JsonRun.Fail(CliDiagnostics.EmTooManySolves(over));
            return null;
        }

        lib = switched.Lib;
        tb = switched.Tb;
        _sweepTop = top is ParametricSweepAnalysis;
        return EmEvaluationScope.Begin(control: RunHost.Control);
    }

    private bool _sweepTop;

    /// <summary>
    /// D5 — wrap the verb's one elaboration of the whole bench in this. When the chain is a sweep run with EM evaluations,
    /// every point elaborates (and solves) for itself and that netlist only collects diagnostics, so it is elaborated in
    /// counting mode: else the bench's own values would be solved once more than the count said, even when no sweep point
    /// is at them. Null — nothing to wrap — otherwise.
    /// </summary>
    public IDisposable? BaseElaboration() =>
        _sweepTop ? EmEvaluationScope.Begin(countOnly: true, control: RunHost.Control, estimate: false) : null;
}

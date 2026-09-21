using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf netlist &lt;path.clay&gt; --ipc out.ipc --placement out.csv --bom out.csv</c> — the
/// board half of the verb (brief-authored-board-3-companion-writers.md R-ab3-2).
///
/// <para><b>It owns no projection and no writer.</b> Every byte comes from
/// <see cref="BoardCompanions"/>, which is what the layout editor's File ▸ Export rows call —
/// byte identity between the CLI as a process and the in-process call is the gate, and that test
/// exists because the two drifting is invisible (R-ab3-3b). What is here is argument parsing,
/// target resolution, refusals and reporting, on <c>src/Cli/Authoring.cs</c>' terms.</para>
///
/// <para><b>Nothing is written on a refusal</b> (R-ab3-2e). The projection is computed first and
/// carries its own refusal sentence; a half-written <c>.ipc</c> is worse than none, because it
/// reads as a complete statement about a board and is one about part of it.</para>
/// </summary>
internal static class Board
{
    public static int Run(
        string path, DocumentKind kind, string? ipc, string? placement, string? bom, string? output)
    {
        // R-ab3-2b. Two of the three tables are `.csv` and the extension cannot say which, so `-o`
        // on a board is the one question a dialog would have asked and a flag has to answer. The
        // same refusal covers a `.clay` given with no destination at all: there is no picture on
        // stdout here either, because three tables cannot share one stream.
        if (ipc is null && placement is null && bom is null)
            return JsonRun.Fail(CliDiagnostics.NetlistBoardWhichTable(path, output));

        var (clay, refusal) = ResolveLayout(path, kind);
        if (refusal is { } r) return r;

        LayoutView view;
        try { view = LayoutPersistence.LoadFromFile(clay!); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.NetlistBoardUnreadable(clay!, ex.Message)); }

        // The technology walk `render` and `em` both take — the layout's own reference, then its
        // ancestor workspace. A board with none still projects: what it costs is the via access
        // codes, which only a stackup can state, and an absent field is written as absent.
        var cache = new TechnologyCache();
        var (resolved, _) = TechnologyResolver.ResolveForDocument(view.TechRef, clay!, null, cache);
        foreach (string d in resolved.Diagnostics) Console.Error.WriteLine("warning: " + d);
        if (resolved.Tech is null)
            Console.Error.WriteLine(
                "note: no technology resolved for this board, so no via record states the layers it "
              + "spans. Every other field is unaffected.");

        var projection = BoardCompanions.Project(view, clay!, resolved.Tech);

        foreach (string note in projection.Notes)
        {
            Console.Error.WriteLine("warning: " + note);
            JsonRun.Note(CliDiagnostics.NetlistBoardNote(note));
        }

        if (projection.Refusal is { Length: > 0 } why)
        {
            Console.Error.WriteLine("error: " + why);
            return JsonRun.Fail(CliDiagnostics.NetlistBoardRefused(clay!, why));
        }

        // R-ab3-3c's sentence, headless. It does not refuse: a placement file with no nets in it is
        // a perfectly useful placement file, and a thin table that says it is thin is worth more
        // than a refusal that leaves the caller with nothing.
        if (projection.ThinnessSummary is { Length: > 0 } thin)
        {
            Console.Error.WriteLine("note: " + thin);
            JsonRun.Note(CliDiagnostics.NetlistBoardThin(thin));
        }

        try { BoardCompanions.Write(projection, ipc, placement, bom); }
        catch (Exception ex)
        {
            return JsonRun.Fail(CliDiagnostics.NetlistWriteFailed(
                ipc ?? placement ?? bom ?? clay!, ex.Message));
        }

        if (ipc is not null)       Report("ipc", ipc, $"{projection.Pads.Count:N0} pad(s)");
        if (placement is not null) Report("placement", placement, $"{projection.Placements.Count:N0} placement(s)");
        if (bom is not null)       Report("bom", bom, $"{projection.Parts.Count:N0} part(s)");

        Console.Error.WriteLine($"[circuitRF] projected {Path.GetFileName(clay!)}");
        return 0;
    }

    private static void Report(string key, string path, string what)
    {
        JsonRun.AddOutput(key, path);
        Console.WriteLine(path);
        Console.Error.WriteLine($"[circuitRF] {what} -> {path}");
    }

    /// <summary>
    /// Which layout this run projects. A cell folder resolves through
    /// <c>CellFolder.ResolvePrimary</c> — the same function <c>render</c> and the schematic half of
    /// this verb use, so the view this projects is the view those would have drawn.
    /// </summary>
    private static (string? Clay, int? Refusal) ResolveLayout(string path, DocumentKind kind)
    {
        if (kind == DocumentKind.Layout) return (path, null);

        var primary = CellFolder.ResolvePrimary(path, ViewType.Layout);
        if (primary.State is PrimaryState.NoView)
            return (null, JsonRun.Fail(CliDiagnostics.NetlistNoLayoutView(path)));

        if (primary.ResolvedName is not { Length: > 0 } name)
            return (null, JsonRun.Fail(CliDiagnostics.NetlistNoPrimary(path, primary.State.ToString())));

        return (Path.Combine(CellFolder.SubFolderPath(path, ViewType.Layout), name), null);
    }
}

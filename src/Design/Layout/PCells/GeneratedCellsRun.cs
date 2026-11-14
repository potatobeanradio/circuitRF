using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.Layout.PCells;

/// <summary>
/// What a headless run needs before it reads a layout's geometry: every generated cell the layout
/// PLACES, present — rebuilt where missing or stale, or named as a cell that cannot be
/// (brief-generated-cells-2 R-gc2-2/R-gc2-3).
///
/// <para><b>Why this exists.</b> A placed PCell's artwork lives in <c>.generated-cells/</c>, which is a
/// cache: an unpacked archive, a <c>history clone</c>, a CI checkout (the workspace <c>.gitignore</c>
/// excludes it) or a workspace nobody has opened since a generator changed does not have it. The
/// flatten drops such an instance with a warning, and every verb after it gives a complete, plausible
/// answer for a board with parts missing. This rebuilds them first, through the same step the
/// application's open uses (<see cref="GeneratedCellsLifecycle.Rebuild"/>), and hands back the ones it
/// could not, so the caller can refuse.</para>
///
/// <para><b>Only what the layout PLACES, and the hierarchy under it.</b> The application's open walks
/// every <c>.clay</c> in the workspace; a run over one board must not start a kit's interpreter — or
/// be refused — over a cell some other layout uses.</para>
///
/// <para><b>It edits no document.</b> Where the application repoints a stale instance and saves the
/// layout, this redirects the stale folder to the rebuilt one in <see cref="GeneratedCellOverlay"/>
/// for the run — the artwork is the same, and a CLI run has no business rewriting a <c>.clay</c> that
/// another process may have open. For the same reason it never prunes.</para>
///
/// <para><b>Dispose it when the run is over</b>: that removes the run's in-memory cells and redirects
/// and ends any kit interpreter it started.</para>
/// </summary>
public sealed class GeneratedCellsRun : IDisposable
{
    private readonly GeneratedCellRunOptions _options;
    private readonly Func<string?, Technology?> _resolveTech;
    private readonly List<string> _overlayKeys = [];
    private readonly Dictionary<string, PCellWorkerResolver> _kitResolvers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _visitedCells = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _settled = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UnbuildableGeneratedCell> _unbuildable = [];
    private readonly List<string> _notes = [];
    private readonly List<string> _warnings = [];
    private readonly List<KitNotAllowed> _kitsNotAllowed = [];
    private readonly HashSet<string> _asked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _allowedByAsking = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private GeneratedCellsRun(GeneratedCellRunOptions options)
    {
        _options = options;
        _resolveTech = options.ResolveTech ?? MemoizedTechLoader();
    }

    /// <summary>The placed generated cells that are not on disk and could not be rebuilt. Empty is
    /// the ordinary answer.</summary>
    public IReadOnlyList<UnbuildableGeneratedCell> Unbuildable => _unbuildable;

    /// <summary>What was said on the way that is not a failure — a kit's own report, or a cell used
    /// from disk because its generator could not be asked whether it is current.</summary>
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>brief-icp-10 — what a generator said when it ran: a kit cell that drew nothing on a layer the technology lacks says
    /// so here. A warning, since the cell it built is not what the kit draws.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>The kits this run needed and was not allowed to run, with what is recorded for each.</summary>
    public IReadOnlyList<KitNotAllowed> KitsNotAllowed => _kitsNotAllowed;

    /// <summary>How many generated cells this run built (on disk or in memory).</summary>
    public int CellsBuilt { get; private set; }

    /// <summary>
    /// Makes every generated cell <paramref name="view"/> places, directly or through the cells it
    /// places, resolvable — or reports it. <paramref name="clayPath"/> is the file
    /// <paramref name="view"/> was read from; relative cell references resolve against its folder.
    /// </summary>
    public static GeneratedCellsRun Prepare(LayoutView view, string clayPath, GeneratedCellRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        var run = new GeneratedCellsRun(options ?? new GeneratedCellRunOptions());
        run.Visit(view, CellHierarchy.BaseDirOfDocument(Path.GetFullPath(clayPath)), Path.GetFullPath(clayPath));
        return run;
    }

    /// <summary><see cref="Prepare(LayoutView, string, GeneratedCellRunOptions?)"/> for a caller that
    /// has not read the layout itself. An unreadable file prepares nothing: the caller's own read
    /// reports it.</summary>
    public static GeneratedCellsRun Prepare(string clayPath, GeneratedCellRunOptions? options = null)
    {
        LayoutView view;
        try { view = LayoutPersistence.LoadFromFile(clayPath); }
        catch { return new GeneratedCellsRun(options ?? new GeneratedCellRunOptions()); }
        return Prepare(view, clayPath, options);
    }

    private void Visit(LayoutView view, string layoutDir, string referencedFrom)
    {
        foreach (var inst in view.Instances)
        {
            if (ExternalCellRef.ResolveCellDir(inst.CellRef, layoutDir) is not { } cellDir) continue;
            cellDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cellDir));

            if (GeneratedRootOf(cellDir) is { } workspaceRoot)
            {
                if (_settled.Add(cellDir)) Settle(view, cellDir, workspaceRoot, referencedFrom);
                continue;
            }

            // An authored cell: its own placements are the run's too.
            if (!_visitedCells.Add(cellDir)) continue;
            // Which cells a run settles does not depend on a placement's side (brief-bsp-1 R-bsp1-7).
            var res = CellLayoutResolver.Resolve(inst, layoutDir, tech: null);
            if (res is { State: CellLayoutState.Resolved, View: { } sub, ResolvedCellDir: { } subDir })
                Visit(sub, CellHierarchy.LayoutBaseDirOf(subDir), ClayOf(subDir) ?? subDir);
        }
    }

    /// <summary>One placed generated cell: rebuilt from the placing layout's snapshot, or reported.</summary>
    private void Settle(LayoutView placing, string cellDir, string workspaceRoot, string referencedFrom)
    {
        string name = Path.GetFileName(cellDir);
        bool onDisk = GeneratedCellsLifecycle.IsOnDisk(cellDir);

        if (SnapshotFor(placing, name) is not { } recorded)
        {
            if (!onDisk)
                _unbuildable.Add(new UnbuildableGeneratedCell(name, null, referencedFrom,
                    "the layout that places it records no snapshot to rebuild it from."));
            return;
        }

        var snap = GeneratedCellsLifecycle.Canonical(workspaceRoot, recorded, referencedFrom, placing);
        if (!PCellRegistry.IsBuiltIn(snap.GeneratorId)) EnsureKitResolver(workspaceRoot);

        // SL2: a read-only workspace is never written, whatever the verb would have liked — its cells
        // are held for the run like a read-only verb's.
        var target = _options.Target == GeneratedCellTarget.Disk && WorkspaceWritability.IsReadOnly(workspaceRoot)
            ? GeneratedCellTarget.Memory
            : _options.Target;

        if (TryRebuild(workspaceRoot, snap, target, out string? rebuilt, out var failure) is false
            // A missing kit cell, and a kit nobody on this machine has decided about: the one case where
            // asking the person helps. Never for a cell on disk — that one is used as it is, and a
            // prompt to redraw what is already there would train exactly the reflexive "Allow" consent
            // exists to prevent.
            && !onDisk && !PCellRegistry.IsBuiltIn(snap.GeneratorId) && AskForTrust(workspaceRoot))
            TryRebuild(workspaceRoot, snap, target, out rebuilt, out failure);

        if (rebuilt is null)
        {
            if (onDisk)
            {
                // What the application's open does with the same failure: the cell on disk stays in
                // use. It is only a refusal when there is nothing on disk to use (R-gc2-3).
                _notes.Add($"The generated cell '{name}' could not be checked against its generator " +
                           $"'{snap.GeneratorId}' ({failure!.Message}); the copy on disk was used.");
                return;
            }
            _unbuildable.Add(new UnbuildableGeneratedCell(name, snap.GeneratorId, referencedFrom, Reason(snap.GeneratorId, failure!)));
            return;
        }

        if (!string.Equals(rebuilt, cellDir, StringComparison.OrdinalIgnoreCase))
        {
            GeneratedCellOverlay.Redirect(cellDir, rebuilt);
            _overlayKeys.Add(cellDir);
            CellLayoutResolver.Invalidate(cellDir);
        }
    }

    private bool TryRebuild(string workspaceRoot, PCellSnapshot snap, GeneratedCellTarget target,
                            out string? rebuilt, out Exception? failure)
    {
        rebuilt = null;
        failure = null;
        try
        {
            int written = GeneratedCellStore.CellsWrittenUnder(workspaceRoot);
            string dir = GeneratedCellsLifecycle.Rebuild(workspaceRoot, snap, _resolveTech, target, out var said);
            // A kit's (a built-in's notes are design-time, said where the cell is placed, and would repeat on every rebuild).
            if (said is { Count: > 0 } && !PCellRegistry.IsBuiltIn(snap.GeneratorId))
                foreach (string line in said) _warnings.Add($"The generated cell '{Path.GetFileName(dir)}' (generator '{snap.GeneratorId}'): {line}");
            rebuilt = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

            if (target == GeneratedCellTarget.Disk)
                CellsBuilt += GeneratedCellStore.CellsWrittenUnder(workspaceRoot) - written;
            else if (!GeneratedCellsLifecycle.IsOnDisk(rebuilt) && !_overlayKeys.Contains(rebuilt, StringComparer.OrdinalIgnoreCase))
            {
                CellsBuilt++;
                _overlayKeys.Add(rebuilt);
            }
            return true;
        }
        catch (Exception ex)
        {
            failure = ex;
            return false;
        }
    }

    /// <summary>
    /// Puts <see cref="GeneratedCellRunOptions.AskTrust"/>'s question for every kit in
    /// <paramref name="workspaceRoot"/> nobody has decided about, once per kit per run. True when at
    /// least one was allowed — the resolver then forgets its earlier "not allowed", as the
    /// application's own grant does, so the retry can start it.
    ///
    /// <para><b>Only UNKNOWN is asked about.</b> A kit this machine recorded as denied was refused by
    /// the person at the keyboard; a headless question must not be a way round that.</para>
    /// </summary>
    private bool AskForTrust(string workspaceRoot)
    {
        if (_options.AskTrust is not { } ask || !_kitResolvers.TryGetValue(workspaceRoot, out var resolver)) return false;

        bool any = false;
        foreach (var kit in _kitsNotAllowed.ToList())
        {
            if (kit.Recorded != PCellTrustDecision.Unknown || !_asked.Add(kit.Directory)) continue;
            if (!resolver.Kits.Any(k => string.Equals(PCellTrustStore.Normalize(k.TrustKey), kit.Directory, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (ask(kit.Directory) != true) continue;
            _allowedByAsking.Add(kit.Directory);
            _kitsNotAllowed.Remove(kit);
            any = true;
        }

        if (any)
        {
            resolver.StopProviders();
            PCellRegistry.InvalidateResolved();
        }
        return any;
    }

    private string Reason(string generatorId, Exception ex)
    {
        if (!PCellRegistry.IsBuiltIn(generatorId) && _kitsNotAllowed.Count > 0)
            return $"its generator is not built in, and {Plural(_kitsNotAllowed.Count, "kit")} that could draw it " +
                   $"{(_kitsNotAllowed.Count == 1 ? "has" : "have")} not been allowed to run here: " +
                   string.Join(", ", _kitsNotAllowed.Select(k => $"'{k.Directory}'")) + ".";
        return ex.Message;
    }

    private static PCellSnapshot? SnapshotFor(LayoutView view, string name)
    {
        if (view.PCellSnapshots.TryGetValue(name, out var exact)) return exact;
        foreach (var (key, snap) in view.PCellSnapshots)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return snap;
        return null;
    }

    /// <summary>The workspace root a generated cell folder belongs to, or null when
    /// <paramref name="cellDir"/> is not one.</summary>
    private static string? GeneratedRootOf(string cellDir)
    {
        string? parent = Path.GetDirectoryName(cellDir);
        if (parent is null || !string.Equals(Path.GetFileName(parent), GeneratedCellStore.ReservedFolderName,
                                              StringComparison.OrdinalIgnoreCase)) return null;
        return Path.GetDirectoryName(parent);
    }

    private static string? ClayOf(string cellDir)
    {
        try
        {
            var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);
            return primary.ResolvedName is { Length: > 0 } n
                ? Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), n)
                : null;
        }
        catch { return null; }
    }

    // ── Kits ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The workspace's kit generators, made available to the registry for this run — the resolver the
    /// application registers on open, with this run's trust instead of a prompt. A kit nobody allowed
    /// is never started: the refusal happens inside the resolver, at the one point that would launch
    /// an interpreter.
    /// </summary>
    private void EnsureKitResolver(string workspaceRoot)
    {
        if (_kitResolvers.ContainsKey(workspaceRoot)) return;

        var given = _options.Trust ?? (_ => PCellTrustDecision.Unknown);
        PCellTrustDecision trust(string dir)
            => _allowedByAsking.Contains(PCellTrustStore.Normalize(dir)) ? PCellTrustDecision.Allowed : given(dir);
        var resolver = new PCellWorkerResolver(workspaceRoot, findInterpreter: null, report: Note, trust: trust,
                                               moduleInterpreter: _options.ModuleInterpreter)
        {
            // Replayed, never written back: the application records the interpreter it settles on,
            // and a headless run must not edit the .cws.
            Recorded = RecordedInterpreter(workspaceRoot),
        };
        _kitResolvers[workspaceRoot] = resolver;
        PCellRegistry.AddResolver(resolver);

        // Keyed as the resolver keys it: a gdsfactory kit's consent belongs to its installed directory.
        foreach (var kit in resolver.Kits)
            if (trust(kit.TrustKey) is var decision && decision != PCellTrustDecision.Allowed)
                _kitsNotAllowed.Add(new KitNotAllowed(PCellTrustStore.Normalize(kit.TrustKey), decision));
    }

    private void Note(string message)
    {
        // The resolver's own refusal of an untrusted kit is what KitsNotAllowed reports, structured;
        // repeating its prose (which names the application's Settings tab) would say it twice.
        if (message.Contains("allowed to run", StringComparison.Ordinal)) return;
        if (!_notes.Contains(message)) _notes.Add(message);
    }

    private static string? RecordedInterpreter(string workspaceRoot)
    {
        try
        {
            string cws = Path.Combine(workspaceRoot, ".cws");
            return File.Exists(cws) ? WorkspacePersistence.LoadFromFile(cws).PythonInterpreter : null;
        }
        catch { return null; }
    }

    private static Func<string?, Technology?> MemoizedTechLoader()
    {
        var cache = new Dictionary<string, Technology?>(StringComparer.OrdinalIgnoreCase);
        return identity =>
        {
            if (string.IsNullOrEmpty(identity)) return null;
            if (cache.TryGetValue(identity, out var hit)) return hit;
            Technology? tech = null;
            // The application's own loader, and its own best-effort rule: a missing .ctech rebuilds on
            // the fallback palette rather than refusing.
            try { if (File.Exists(identity)) tech = TechPersistence.LoadFromFile(identity); }
            catch { }
            return cache[identity] = tech;
        };
    }

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        GeneratedCellOverlay.Remove(_overlayKeys);
        foreach (string dir in _overlayKeys) CellLayoutResolver.Invalidate(dir);

        foreach (var resolver in _kitResolvers.Values)
        {
            PCellRegistry.RemoveResolver(resolver);
            try { resolver.Dispose(); } catch { /* teardown must not fail the run that is ending */ }
        }
        _kitResolvers.Clear();
    }
}

/// <summary>How a <see cref="GeneratedCellsRun"/> builds what is missing.</summary>
public sealed record GeneratedCellRunOptions
{
    /// <summary><see cref="GeneratedCellTarget.Memory"/> for a verb that promises to write nothing;
    /// <see cref="GeneratedCellTarget.Disk"/> for one that may write the folder as the application
    /// would, so the next run is free. The default is the one that cannot surprise anybody.</summary>
    public GeneratedCellTarget Target { get; init; } = GeneratedCellTarget.Memory;

    /// <summary>Whether a kit's generator scripts may run, by the kit's manifest directory. Null is
    /// "nobody has been asked" for every kit, so no kit code runs.</summary>
    public Func<string, PCellTrustDecision>? Trust { get; init; }

    /// <summary>
    /// Asks the person whether a kit nobody has decided about may run, by its manifest directory:
    /// true allows it for this run, false or null does not. Asked only when a MISSING cell needs it.
    /// Null never asks — the command line, where <c>--trust-kit</c> says it up front instead.
    /// </summary>
    public Func<string, bool?>? AskTrust { get; init; }

    /// <summary>Which interpreter runs a gdsfactory kit, by module name. Null reads the choices this
    /// installation recorded; a headless caller puts its <c>--python</c> first (brief-gdf-2 R-gdf2-2b).</summary>
    public Func<string, string?>? ModuleInterpreter { get; init; }

    /// <summary>A snapshot's resolved <c>.ctech</c> path to a technology. Null uses a memoized
    /// <see cref="TechPersistence.LoadFromFile(string)"/>, the application's own loader.</summary>
    public Func<string?, Technology?>? ResolveTech { get; init; }
}

/// <summary>A placed generated cell that is not on disk and could not be rebuilt.</summary>
/// <param name="CellName">The cell folder's name, as the instance names it.</param>
/// <param name="GeneratorId">The generator its snapshot records, or null when there is no snapshot.</param>
/// <param name="ReferencedFrom">The layout file that places it.</param>
/// <param name="Reason">Why it could not be rebuilt.</param>
public sealed record UnbuildableGeneratedCell(string CellName, string? GeneratorId, string ReferencedFrom, string Reason)
{
    /// <summary>The sentence the application's Messages line says for the same cell.</summary>
    public string Sentence => GeneratedCellsLifecycle.CouldNotRebuild(CellName, GeneratorId, Reason);
}

/// <summary>A kit a run needed and was not allowed to run, and what this installation records for it.</summary>
public sealed record KitNotAllowed(string Directory, PCellTrustDecision Recorded);

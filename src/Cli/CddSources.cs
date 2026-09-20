using CircuitRF.Design.Results;
using CircuitRF.Design.Workspace;
using CircuitRF.Render.DataDisplay;
using RfCore;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// The data behind a headless <c>.cdd</c> render: <see cref="IPlotDataSources"/> over the files a
/// caller named with <c>--data</c> plus the ones the document resolves beside itself
/// (brief-render-4-data-display.md R-rnd4-4).
///
/// <para><b>It reads through the readers the GUI reads through</b> — <c>DataSetImporter</c> for an
/// <c>.npy</c>, <c>TouchstoneIO</c> plus <c>DataSetBuilder.FromSnp</c> for a Touchstone. That is
/// exactly the pair <c>circuitrf read</c> uses and exactly the pair
/// <c>DataSourceEntryViewModel</c> uses; a third loader here would be a file the CLI and the GUI
/// could disagree about. <b>It is now literally one function</b> — <see cref="PlotSourceFile"/>, below
/// the firewall — because the Smith Chart tool's own document sources would have been the third.</para>
///
/// <para><b>Every source is resolved before anything is drawn, and an unresolved one is a
/// refusal.</b> R-rnd4-4 — and the reason is worth repeating at the point where it is enforced: an
/// empty plot is a valid picture that exports cleanly and looks exactly like a measurement that
/// came back empty. The sentinel <c>run.npy</c> means "whatever this document has SELECTED", and
/// headlessly there is no selection and no window to make one in, so a display bound to it with no
/// <c>--data</c> is refused NAMING the sentinel and the flag.</para>
/// </summary>
internal sealed class CddSources : IPlotDataSources
{
    private readonly Dictionary<string, Loaded> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _refToPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private          string? _selected;

    private sealed class Loaded
    {
        public required string   Path;
        public required string   Reference;
        public required bool     FromDataFlag;
        public          DataSet? Data;
        public          SNP?     Snp;
    }

    // ── binding ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves every source the chosen pages' traces reference, and refuses if any cannot be.
    /// </summary>
    /// <param name="cddPath">The document, whose folder and whose workspace's <c>results/</c> are
    /// the two places a relative reference is looked for.</param>
    public static (CddSources? Sources, int? Refusal) Bind(
        string                       cddPath,
        DataDisplayConfig            config,
        IReadOnlyList<TabConfig>     tabs,
        IReadOnlyList<int>           pages,
        IReadOnlyList<string>        data)
    {
        var s = new CddSources();

        foreach (var kv in config.SourceAliases) s._aliases[kv.Key] = kv.Value;

        // ── the files the caller named ───────────────────────────────────────
        //
        // Loaded eagerly and in order: an unreadable --data is the caller's own mistake and must be
        // reported as itself, not later as "this display reads something that is not here".
        var named = new List<Loaded>();
        foreach (string raw in data)
        {
            string abs = Path.GetFullPath(raw);
            if (!File.Exists(abs))
                return (null, JsonRun.Fail(CliDiagnostics.RenderDataNotFound(raw)));

            var loaded = new Loaded { Path = abs, Reference = Path.GetFileName(abs), FromDataFlag = true };
            if (Read(loaded) is { } why)
                return (null, JsonRun.Fail(CliDiagnostics.RenderDataUnreadable(raw, why)));

            named.Add(loaded);
            s._byPath[abs] = loaded;
        }

        // ── what the document asks for ───────────────────────────────────────

        var wanted = new List<string>();
        void Want(string? sref)
        {
            if (sref is null) return;
            if (!wanted.Contains(sref, StringComparer.Ordinal)) wanted.Add(sref);
        }

        foreach (int ti in pages)
        foreach (var pc in tabs[ti].Plots)
        foreach (var tc in pc.Traces)
        {
            Want(tc.SourcePath);
            if (!string.IsNullOrEmpty(tc.XSourcePath)) Want(tc.XSourcePath);
        }
        // The selected source is wanted even when no trace names the sentinel: a loadpull SUMMARY
        // column has no per-trace source of its own and reads it.
        bool needsSelected = wanted.Contains(DataSourceRef.Selected, StringComparer.Ordinal)
                          || pages.Any(ti => tabs[ti].Plots.Any(p => p.Traces.Any(t => t.SummaryColumn is not null)));
        if (needsSelected) Want(DataSourceRef.Selected);

        // ── where a reference can be found ───────────────────────────────────

        string cddDir      = Path.GetDirectoryName(Path.GetFullPath(cddPath)) ?? ".";
        string? wsDir      = WorkspaceRootFinder.FindAncestorCws(cddDir) is { } cws
                           ? Path.GetDirectoryName(cws)
                           : null;
        string? resultsDir = wsDir is not null ? ResultsWriter.ResultsDirectory(wsDir) : null;

        var searched = new List<string> { cddDir };
        if (resultsDir is not null) searched.Add(resultsDir);
        string tried = string.Join(", ", searched);

        int nextNamed = 0;

        foreach (string sref in wanted)
        {
            // The sentinel: the document's own SelectedDataSource first, then --data. A caller that
            // named a file has said which run to draw, and that overrides a stale selection.
            if (sref == DataSourceRef.Selected)
            {
                // config.SelectedDataSource is a FILE NAME, and the flat results/ convention means
                // the most-recent run is literally called "run.npy" — the same string as the
                // sentinel. So it is located as a name, never short-circuited as the sentinel; a
                // display whose selection is that file must resolve to that file.
                string? abs = named.Count > 0 ? named[0].Path
                            : LocateFile(config.SelectedDataSource, cddDir, resultsDir);
                if (abs is null)
                    return (null, JsonRun.Fail(CliDiagnostics.RenderCddSelectedUnbound(DataSourceRef.Selected)));

                if (!s._byPath.TryGetValue(abs, out var sel))
                {
                    sel = new Loaded { Path = abs, Reference = sref, FromDataFlag = false };
                    // R-aut9-7: the SOURCE could not be read, which is not the same problem as the
                    // display being unreadable — and reporting it as one sent a caller off to
                    // rewrite a `.cdd` that was never wrong. The selection is reported by the name
                    // the document holds, since that is what a caller would have to change.
                    if (Read(sel) is { } why)
                        return (null, JsonRun.Fail(CliDiagnostics.RenderCddSourceUnreadable(
                            config.SelectedDataSource ?? sref, abs, cddPath, why)));
                    s._byPath[abs] = sel;
                }
                if (named.Count > 0) nextNamed = Math.Max(nextNamed, 1);
                s._refToPath[sref] = abs;
                s._selected        = abs;
                continue;
            }

            // A concrete reference: beside the `.cdd`, then under the workspace's results/, then a
            // --data file whose NAME matches, then the next unclaimed --data in order.
            string? found = LocateOnDisk(sref, cddDir, resultsDir)
                         ?? named.FirstOrDefault(n => string.Equals(
                                Path.GetFileName(n.Path), Path.GetFileName(sref),
                                StringComparison.OrdinalIgnoreCase))?.Path;

            if (found is null && nextNamed < named.Count) found = named[nextNamed++].Path;

            if (found is null)
                return (null, JsonRun.Fail(CliDiagnostics.RenderCddSourceUnresolved(sref, tried)));

            if (!s._byPath.TryGetValue(found, out var entry))
            {
                entry = new Loaded { Path = found, Reference = sref, FromDataFlag = false };
                if (Read(entry) is { } why)
                    return (null, JsonRun.Fail(CliDiagnostics.RenderCddSourceUnreadable(
                        sref, found, cddPath, why)));
                s._byPath[found] = entry;
            }
            s._refToPath[sref] = found;
        }

        s._selected ??= s._refToPath.Count > 0 ? s._refToPath.Values.First() : null;

        // R-rnd4-4's last clause: a --data that binds nothing is a refusal, not a shrug. A caller
        // that misspelled a path, or handed over last week's run, must not get a picture drawn from
        // whatever happened to be lying beside the document.
        foreach (var n in named)
        {
            if (s._refToPath.ContainsValue(n.Path)) continue;
            return (null, JsonRun.Fail(CliDiagnostics.RenderDataBindsNothing(
                n.Path, wanted.Count == 0 ? "nothing" : string.Join(", ", wanted.Select(w => $"'{w}'")))));
        }

        return (s, null);
    }

    // ── describing, without binding ──────────────────────────────────────────

    /// <summary>
    /// Every result file a display asks for, and where each one is — or is not — on disk.
    ///
    /// <para><b>Why this lives here rather than in <c>check</c>.</b> Which references a document
    /// carries, and the two directories a relative one is looked for in, are decisions
    /// <see cref="Bind"/> already makes; a second copy in the checker would answer a different
    /// question the first time either changed. This is the same collection and the same locator with
    /// the loading left out, because <c>check</c> reads no results and refuses nothing over
    /// them — a display whose run has simply not been made yet is the ordinary state of a shipped
    /// example.</para>
    /// </summary>
    /// <returns>
    /// One entry per distinct reference, in the order the document names them, each with the
    /// absolute path it resolves to or null; plus the directories that were searched.
    /// </returns>
    public static (IReadOnlyList<(string Reference, string? Path)> Sources,
                   IReadOnlyList<string> Searched) Describe(
        string cddPath, DataDisplayConfig config, IReadOnlyList<TabConfig> tabs)
    {
        var wanted = new List<string>();
        void Want(string? sref)
        {
            if (string.IsNullOrEmpty(sref)) return;
            if (!wanted.Contains(sref, StringComparer.Ordinal)) wanted.Add(sref);
        }

        foreach (var tab in tabs)
        foreach (var pc in tab.Plots)
        foreach (var tc in pc.Traces)
        {
            Want(tc.SourcePath);
            Want(tc.XSourcePath);
        }
        if (wanted.Contains(DataSourceRef.Selected, StringComparer.Ordinal)
            || tabs.Any(tb => tb.Plots.Any(p => p.Traces.Any(tc => tc.SummaryColumn is not null))))
            Want(DataSourceRef.Selected);

        string  cddDir     = Path.GetDirectoryName(Path.GetFullPath(cddPath)) ?? ".";
        string? wsDir      = WorkspaceRootFinder.FindAncestorCws(cddDir) is { } cws
                           ? Path.GetDirectoryName(cws)
                           : null;
        string? resultsDir = wsDir is not null ? ResultsWriter.ResultsDirectory(wsDir) : null;

        var searched = new List<string> { cddDir };
        if (resultsDir is not null) searched.Add(resultsDir);

        var found = new List<(string, string?)>();
        foreach (string sref in wanted)
            found.Add((sref, sref == DataSourceRef.Selected
                ? LocateFile(config.SelectedDataSource, cddDir, resultsDir)
                : LocateFile(sref, cddDir, resultsDir)));

        return (found, searched);
    }

    /// <summary>
    /// A reference as a path on disk: rooted as itself, otherwise beside the `.cdd` and then under
    /// the workspace's <c>results/</c> — which is the flat, shared directory the application's own
    /// library resolves a bare <c>&lt;name&gt;.npy</c> against.
    /// </summary>
    private static string? LocateOnDisk(string? sref, string cddDir, string? resultsDir)
        => sref == DataSourceRef.Selected ? null : LocateFile(sref, cddDir, resultsDir);

    /// <summary>The same walk, with no sentinel short-circuit — see the caller for why that matters.</summary>
    private static string? LocateFile(string? sref, string cddDir, string? resultsDir)
    {
        if (string.IsNullOrEmpty(sref)) return null;
        if (Path.IsPathRooted(sref)) return File.Exists(sref) ? Path.GetFullPath(sref) : null;

        string beside = CircuitRF.Core.RefPath.Resolve(cddDir, sref);
        if (File.Exists(beside)) return beside;

        if (resultsDir is not null)
        {
            string inResults = CircuitRF.Core.RefPath.Resolve(resultsDir, sref);
            if (File.Exists(inResults)) return inResults;
        }
        return null;
    }

    /// <summary>Loads one file. Returns null on success, or why it could not be read.</summary>
    private static string? Read(Loaded l)
    {
        var (data, snp, why) = LoadResult(l.Path);
        l.Data = data;
        l.Snp  = snp;
        return why;
    }

    /// <summary>
    /// One result file as the Data Display sees it — the <c>DataSet</c>, and the narrow network view
    /// that stands in for an SNP. Public so <c>plot</c> can read a result BEFORE it authors a
    /// document about it (R-aut11-2): a cube spec has to be checked against the cubes that are
    /// actually there, and a spec checked against a second loader would be checked against a
    /// different file.
    /// </summary>
    public static (DataSet? Data, SNP? Snp, string? Error) LoadResult(string path)
        => PlotSourceFile.Load(path);

    // ── IPlotDataSources ─────────────────────────────────────────────────────

    public string? ResolveAbs(string? sourceRef)
    {
        if (string.IsNullOrEmpty(sourceRef) || sourceRef == DataSourceRef.Selected) return _selected;
        return _refToPath.TryGetValue(sourceRef, out var p) ? p
             : Path.IsPathRooted(sourceRef) && _byPath.ContainsKey(sourceRef) ? sourceRef
             : null;
    }

    public bool     Contains(string absPath)       => _byPath.ContainsKey(absPath);
    public SNP?     NetworkFor(string absPath)     => _byPath.TryGetValue(absPath, out var e) ? e.Snp  : null;
    public DataSet? DataFor(string absPath)        => _byPath.TryGetValue(absPath, out var e) ? e.Data : null;
    public string?  DisplayNameFor(string absPath) => _byPath.ContainsKey(absPath) ? Path.GetFileName(absPath) : null;

    public string? AliasFor(string absPath)
    {
        if (!_byPath.TryGetValue(absPath, out var e)) return null;
        // Aliases are stored in the `.cdd` keyed the way a trace's own reference is — a bare file
        // name relative to the results root. The reference this entry answered to is that key.
        if (_aliases.TryGetValue(e.Reference, out var a) && a.Length > 0) return a;
        if (_aliases.TryGetValue(Path.GetFileName(absPath), out var b) && b.Length > 0) return b;
        return null;
    }

    /// <summary>
    /// True when more than one source carrying a NETWORK is loaded — the same test the application
    /// makes, and the reason it is that test rather than a plain count: a trace label carries its
    /// source's name to tell two S-parameter curves apart.
    /// </summary>
    public bool HasMultipleSources => _byPath.Values.Count(e => e.Snp is { IsEmpty: false }) > 1;

    public DataSet? SelectedData => _selected is not null ? DataFor(_selected) : null;

    // ── reporting ────────────────────────────────────────────────────────────

    public IReadOnlyList<RenderSourceJson> Report()
        => _refToPath
           .OrderBy(kv => kv.Key, StringComparer.Ordinal)
           .Select(kv => new RenderSourceJson(
               kv.Key, kv.Value,
               _byPath.TryGetValue(kv.Value, out var e) && e.FromDataFlag ? "--data" : "document"))
           .ToList();
}

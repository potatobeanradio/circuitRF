// brief-em3d-98 — the shell's half of "is this 3D view solved?": the workspace tree's .c3d rows, a floating window's title, and
// the one-line question before a run would replace a complete, current result. Every answer is C3dSolveStatus's (src/Design);
// nothing here decides whether a result is current.
//
// NO CHECK DELAYS AN OPEN (R-em3d98-6). The tree is shown first; then one background pass, at low priority, fills in the rows
// it has no answer for, each as it finishes — no spinner, no placeholder glyph. An open document's row shows its editor's
// in-memory answer; a closed one's comes from its file, cached per (path, time, size) for the session. A save of anything a
// 3D view can be solved from (a layout, a technology, a material library, a schematic) clears that cache, and the hash cache
// under the check means only the changed files are re-read.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Dock.Model.Controls;
using Dock.Model.Core;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.ViewModels;

public partial class WorkspaceViewModel
{
    private readonly Dictionary<string, (DateTime Time, long Length, SolveBadgeSet Badges)> _treeSolveCache =
        new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _treeSolveCts;
    private FileSystemWatcher? _solveWatcher;
    private string? _solveWatcherRoot;
    private DispatcherTimer? _solveDebounce;

    /// <summary>The files whose save can change whether a 3D view's result is current, beside the ones its editor watches.</summary>
    private static readonly string[] SolveInputs = [".c3d", ".clay", ".ctech", ".wbond", ".cmat", ".csch"];

    /// <summary>Once, from the constructor: the tree's rescans and the active document's changes.</summary>
    private void HookSolveBadges()
    {
        Controls.WindowMotion.Install();      // the checks below hold off while a window is resized or moved
        Dock.ProjectTreeTool.RowsRebuilt += tool =>
        {
            if (ReferenceEquals(tool, _factory.ProjectTreeTool))
                Dispatcher.UIThread.Post(RefreshTreeSolveBadges, DispatcherPriority.Background);
        };
        _factory.ActiveDockableChanged += (_, _) => RefreshFloatingTitles();
        _factory.DockableDocked        += (_, _) => PostRefreshFloatingTitles();
        _factory.DockableMoved         += (_, _) => PostRefreshFloatingTitles();
        _factory.DockableRemoved       += (_, _) => PostRefreshFloatingTitles();
        _factory.DockableClosed        += (_, _) => PostRefreshFloatingTitles();
        _factory.WindowAdded           += (_, _) => PostRefreshFloatingTitles();
    }

    /// <summary>The open 3D editor showing <paramref name="path"/> (as its top document), or null.</summary>
    private C3dEditorViewModel? OpenC3dEditorFor(string path)
        => _openDocsByPath.TryGetValue(C3dEditorDocument.KeyFor(path), out var d) && d is C3dEditorDocument c ? c.ViewModel : null;

    /// <summary>An open editor answered: its row shows the in-memory answer, and a floating window showing it follows.</summary>
    private void OnC3dSolveStatusesChecked(C3dEditorViewModel vm)
    {
        _factory.ProjectTreeTool?.SetSolveBadges(vm.TopFilePath, vm.SolveBadges);
        RefreshFloatingTitles();
    }

    /// <summary>
    /// Every <c>.c3d</c> row of the tree: an open document's from its editor; a closed one's from the cache when its file has
    /// not moved, else checked in the background (one pass, below-normal priority, rows filled in as each finishes).
    /// </summary>
    internal void RefreshTreeSolveBadges()
    {
        if (_factory.ProjectTreeTool is not { } tree || CurrentWorkspacePath is null) return;
        EnsureSolveWatcher();
        string root = EmResultsRoot();
        var todo = new List<string>();
        foreach (string path in tree.C3dFilePaths())
        {
            if (OpenC3dEditorFor(path) is { } vm) { tree.SetSolveBadges(path, vm.SolveBadges); continue; }
            if (Stamp(path) is { } st && _treeSolveCache.TryGetValue(path, out var c) && c.Time == st.Time && c.Length == st.Length)
            { tree.SetSolveBadges(path, c.Badges); continue; }
            todo.Add(path);
        }
        if (todo.Count == 0) return;
        _treeSolveCts?.Cancel();
        var cts = _treeSolveCts = new CancellationTokenSource();
        var ct = cts.Token;
        var worker = new Thread(() =>
        {
            foreach (string path in todo)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    Controls.WindowMotion.WaitForQuiet(ct);         // never mid-resize: each answer redraws a tree row
                    if (Stamp(path) is not { } stamp) continue;
                    var doc = C3dPersistence.LoadFromFile(path);
                    var badges = new SolveBadgeSet(C3dSolveStatus.Of(doc, path, root, ct),
                                                   doc.ActiveSetup ?? C3dSetups.Read(doc).FirstOrDefault(s => s.Refusal is null)?.Name);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        _treeSolveCache[path] = (stamp.Time, stamp.Length, badges);
                        if (OpenC3dEditorFor(path) is null) _factory.ProjectTreeTool?.SetSolveBadges(path, badges);
                    }, DispatcherPriority.Background);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e) when (e is not OutOfMemoryException) { /* unreadable: no glyphs, as `check` would say */ }
            }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "circuitRF solved glyphs" };
        worker.Start();
    }

    /// <summary>Forgets the tree's answer for <paramref name="path"/> (null: every one) and asks again.</summary>
    internal void InvalidateTreeSolve(string? path = null)
    {
        if (path is null) _treeSolveCache.Clear();
        else _treeSolveCache.Remove(Path.GetFullPath(path));
        RefreshTreeSolveBadges();
    }

    private static (DateTime Time, long Length)? Stamp(string path)
    {
        try { var f = new FileInfo(path); return f.Exists ? (f.LastWriteTimeUtc, f.Length) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// One watcher on the open workspace for what a save can change: a file a 3D view is solved from, or a run's
    /// <c>status.json</c> (a run elsewhere — <c>circuitrf em</c> — finishing). Debounced a second, then the tree's cache is
    /// cleared and every open 3D view re-checks.
    /// </summary>
    private void EnsureSolveWatcher()
    {
        string? root = WorkspaceRootDir;
        if (root == _solveWatcherRoot) return;
        _solveWatcher?.Dispose();
        _solveWatcher = null;
        _solveWatcherRoot = root;
        if (root is null || !Directory.Exists(root)) return;
        try
        {
            var w = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            };
            void OnChange(object? _, FileSystemEventArgs e)
            {
                string name = Path.GetFileName(e.FullPath);
                if (!name.Equals(C3dRunStatus.FileName, StringComparison.OrdinalIgnoreCase) &&
                    !SolveInputs.Contains(Path.GetExtension(name).ToLowerInvariant())) return;
                Dispatcher.UIThread.Post(SolveInputsChanged);
            }
            w.Changed += OnChange;
            w.Created += OnChange;
            w.Deleted += OnChange;
            w.Renamed += (s, e) => OnChange(s, e);
            w.EnableRaisingEvents = true;
            _solveWatcher = w;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { /* the next open re-checks */ }
    }

    private void SolveInputsChanged()
    {
        _solveDebounce ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (Controls.WindowMotion.SinceLast < Controls.WindowMotion.Quiet) return;     // still resizing: the timer ticks again
            _solveDebounce!.Stop();
            foreach (var doc in _openDocsByPath.Values.OfType<C3dEditorDocument>()) doc.ViewModel.ScheduleSolveStatus();
            InvalidateTreeSolve();
        });
        _solveDebounce.Stop();
        _solveDebounce.Start();
    }

    // ── a floating window's title (R-em3d98-5 item 4) ─────────────────────────────────────────────

    /// <summary>The longest a torn-off window's title is allowed to grow before the rest of its
    /// documents are counted rather than named.</summary>
    internal const int MaxFloatTitleLength = 80;

    /// <summary>
    /// A torn-off document window's OS title names every document docked into it, the active one first:
    /// <c>Amp.clay, Bias.csch</c>. Dock sets the title once, from the first document, when the window is
    /// presented, so a document docked into the window later was never named. A 3D view carries its glyphs
    /// as TEXT — the title bar is the operating system's: <c>Connector.c3d — solved (FEM, thermal)</c>.
    /// Windows holding a tool, and the main one (it names the workspace), keep their titles.
    /// </summary>
    private void RefreshFloatingTitles()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        foreach (var window in desktop.Windows.OfType<Dock.CrfHostWindow>()
                     .Where(w => w.PlatformImpl is not null && ReferenceEquals(w.OwningWorkspace, this) && !WindowFloatsATool(w)))
        {
            var docs = DockablesIn(window.Window?.Layout).OfType<IDocument>().Where(d => d is not ITool).Cast<IDockable>().ToList();
            if (docs.Count == 0) continue;
            var active = FindAnyDocumentInWindow(window);
            if (active is not null && docs.Remove(active)) docs.Insert(0, active);
            string title = FloatTitleFor(docs.Select(FloatTitleName).ToList());
            if (window.Title != title) window.Title = title;
        }
    }

    private static string FloatTitleName(IDockable doc) => doc switch
    {
        C3dEditorDocument c3d => c3d.SolveTitleSuffix is { } suffix
            ? $"{Path.GetFileName(c3d.FilePath)} — {suffix}"
            : Path.GetFileName(c3d.FilePath),
        _ => (doc.Title ?? "").TrimStart('•', ' '),
    };

    /// <summary>
    /// The names joined with commas while they fit in <see cref="MaxFloatTitleLength"/>; the rest are
    /// counted — <c>Amp.clay, Bias.csch + 3 more</c>. The first name is always given whole.
    /// </summary>
    internal static string FloatTitleFor(IReadOnlyList<string> names)
    {
        var shown = names.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (shown.Count == 0) return "";
        var title = new System.Text.StringBuilder(shown[0]);
        int i = 1;
        for (; i < shown.Count; i++)
        {
            string more = $" + {shown.Count - i - 1} more";
            int need = 2 + shown[i].Length + (i < shown.Count - 1 ? more.Length : 0);
            if (title.Length + need > MaxFloatTitleLength) break;
            title.Append(", ").Append(shown[i]);
        }
        if (i < shown.Count) title.Append($" + {shown.Count - i} more");
        return title.ToString();
    }

    /// <summary>A dock change settles before its window is re-titled: Dock presents a new float, and sets
    /// its own title, inside the gesture.</summary>
    private void PostRefreshFloatingTitles() => Dispatcher.UIThread.Post(RefreshFloatingTitles, DispatcherPriority.Background);

    // ── the question before a re-run (R-em3d98-7) ─────────────────────────────────────────────────

    /// <summary>
    /// True when the run may go ahead: the setup has no complete, current result to replace, or the user said Run Again. A
    /// partial or out-of-date result runs with no question. When the background check has not answered, this setup alone is
    /// checked now — the question must not be skipped because the glyphs were slow.
    /// </summary>
    private async Task<bool> ConfirmRerunAsync(C3dEditorViewModel c3d, string setupName)
    {
        var legs = c3d.SolveStatusOf(setupName);
        if (!C3dSolveStatus.AllCurrent(legs)) return true;
        var doc = _openDocsByPath.Values.OfType<C3dEditorDocument>().FirstOrDefault(d => ReferenceEquals(d.ViewModel, c3d));
        if (doc is null || HostWindowOf(doc) is not { } window) return true;
        var dlg = new SaveChangesDialog(SolveBadgeRules.RerunQuestion(setupName, legs),
                                        saveLabel: "Run Again", dontSaveLabel: null, cancelLabel: "Cancel", title: "Result Is Current");
        await dlg.ShowDialog(window);
        return dlg.Result == SaveChangesResult.Save;
    }
}

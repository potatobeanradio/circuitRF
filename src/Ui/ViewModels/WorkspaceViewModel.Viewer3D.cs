// brief-em3d-28 R-em3d28-5 — where the 3D view opens, and what it follows.
//
//   * Show 3D on a 3D .cem's panel opens it as a document tab beside the setup (one view per .cem);
//   * an edit to the setup, a change to its layout, or a save of its technology or .wBond regenerates
//     it in the background (R-em3d28-2d) — the edit through the panel's own SetupChanged, the layout
//     through NotifyEmSetupsLayoutChanged, the files on disk through a watcher on the workspace;
//   * a finished 3D run refreshes its mesh overlay;
//   * its camera is kept in the workspace's window state (the .cwsuser), never in the .cem.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Threading;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Workspace;
using CircuitRF.Render;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.ViewModels;

public partial class WorkspaceViewModel
{
    private Dictionary<string, CwsCamera3D>? _viewer3DCameras;
    private readonly Dictionary<Viewer3DDocument, FileSystemWatcher> _viewer3DWatchers = [];

    /// <summary>Extensions whose save regenerates an open 3D view.</summary>
    private static readonly string[] Viewer3DInputs = [".cem", ".clay", ".ctech", ".wbond"];

    /// <summary>Opens (or focuses) the 3D view of the setup at <paramref name="cemPath"/>.</summary>
    public void OpenOrActivate3DView(string cemPath)
    {
        string full = Path.GetFullPath(cemPath);
        string key = Viewer3DDocument.KeyFor(full);
        if (ActivateIfOpen(key)) return;

        try
        {
            var vm = new Viewer3DViewModel(full, () => Snapshot3DInputs(full), Viewer3DBackends.Create,
                                           () => GetResultsRoot(), a => Dispatcher.UIThread.Post(a));
            vm.RestoreCamera(StoredCamera(full));
            var doc = new Viewer3DDocument(vm);
            _factory.OpenDocument(doc);
            _openDocsByPath[key] = doc;
            Watch3DInputs(doc);
            vm.Regenerate();
        }
        catch (Exception ex)
        {
            Messages.Error($"Could not open the 3D view: {ex.Message}");
        }
    }

    /// <summary>
    /// The UI thread's snapshot for one regeneration: the setup as the panel holds it (unsaved edits
    /// included) or as the file says when no panel is open, and its layout resolved exactly as Simulate
    /// resolves it — the LIVE model of an open .clay, through ResolveEmLayout.
    /// </summary>
    private Viewer3DInputs Snapshot3DInputs(string cemPath)
    {
        EmSetup setup;
        try
        {
            setup = _openDocsByPath.TryGetValue(cemPath, out var d) && d is EmSetupDocument open
                ? open.ViewModel.Working.Clone()
                : EmSetupPersistence.LoadFromFile(cemPath);
        }
        catch (Exception ex)
        {
            return new Viewer3DInputs(new EmSetup(), null, $"the setup could not be read: {ex.Message}",
                                      ThemeService.Active, ThemeService.CurrentVariant);
        }
        string? refusal = setup.Is3D ? null
            : "this setup has no 3D solver. Choose Palace or openEMS as its 3D solver to see it in 3D.";
        var source = refusal is null ? ResolveEmLayout(cemPath, setup.LayoutRef) : null;
        if (refusal is null && source is null) refusal = "its layout reference resolves to nothing.";
        return new Viewer3DInputs(setup, source, refusal, ThemeService.Active, ThemeService.CurrentVariant);
    }

    /// <summary>The .clay a .cem names, resolved as the panel resolves it — or null.</summary>
    private string? EmSetupLayoutPathOf(string cemPath)
    {
        try
        {
            var setup = _openDocsByPath.TryGetValue(cemPath, out var d) && d is EmSetupDocument open
                ? open.ViewModel.Working : EmSetupPersistence.LoadFromFile(cemPath);
            return ResolveEmLayoutPath(cemPath, setup.LayoutRef);
        }
        catch (Exception) { return null; }
    }

    private void Invalidate3DViews(string cemPath)
    {
        if (_openDocsByPath.TryGetValue(Viewer3DDocument.KeyFor(cemPath), out var d) && d is Viewer3DDocument v)
            v.ViewModel.Invalidate();
    }

    private void Refresh3DViewOverlays(string cemPath)
    {
        if (_openDocsByPath.TryGetValue(Viewer3DDocument.KeyFor(cemPath), out var d) && d is Viewer3DDocument v)
            v.ViewModel.RefreshSolverOverlays();
    }

    /// <summary>A save of the technology, the layout, the .wBond or the .cem itself — anywhere under the
    /// workspace, since a technology may sit anywhere in it — regenerates the view. Debounced by the
    /// view model, so a burst of saves is one build.</summary>
    private void Watch3DInputs(Viewer3DDocument doc)
    {
        string root = WorkspaceRootDir ?? Path.GetDirectoryName(doc.CemPath)!;
        try
        {
            var w = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            };
            void OnChange(object? _, FileSystemEventArgs e)
            {
                if (Viewer3DInputs.Contains(Path.GetExtension(e.FullPath).ToLowerInvariant()))
                    doc.ViewModel.Invalidate();
            }
            w.Changed += OnChange;
            w.Created += OnChange;
            w.Renamed += (s, e) => OnChange(s, e);
            w.EnableRaisingEvents = true;
            _viewer3DWatchers[doc] = w;
        }
        catch (Exception ex)
        {
            Messages.Warning($"The 3D view will not follow saves of its technology or .wBond: {ex.Message}");
        }
    }

    private void Closed3DView(Viewer3DDocument doc)
    {
        if (_viewer3DWatchers.Remove(doc, out var w)) w.Dispose();
        if (CameraKey(doc.CemPath) is { } k && doc.ViewModel.CameraToPersist() is { } cam)
        {
            _viewer3DCameras ??= LoadStoredCameras();
            _viewer3DCameras[k] = cam;
        }
        doc.ViewModel.Dispose();
    }

    /// <summary>
    /// A 3D run directory — <c>&lt;result key&gt;.palace</c> (or <c>.palace_es</c>, <c>_ms</c>, <c>_eig</c>) or
    /// <c>.openems</c> — opens the 3D view of the workspace <c>.cem</c> whose run it is, with the mesh
    /// shown when there is one. The directory holds the solver's lowering, not the problem, so the
    /// problem comes from the setup, exactly as Show 3D builds it. False for any other folder.
    /// </summary>
    private bool TryOpenRunDirectory3D(string dir)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"\.(palace(_es|_ms|_eig)?|openems)$")) return false;
        if (GetResultsRoot() is not { } results || WorkspaceRootDir is not { } root) return false;

        string full = Path.GetFullPath(dir);
        foreach (string cem in Directory.EnumerateFiles(root, "*.cem", SearchOption.AllDirectories))
        {
            EmSetup setup;
            try { setup = EmSetupPersistence.LoadFromFile(cem); }
            catch (Exception) { continue; }
            if (!setup.Is3D) continue;
            foreach (var solver in new[] { Em3dSolver.Palace, Em3dSolver.OpenEms })
            {
                if (!string.Equals(Path.GetFullPath(Design.Em3d.Em3dRunService.RunDirectory(results, setup, solver)), full,
                                   StringComparison.OrdinalIgnoreCase))
                    continue;
                OpenOrActivate3DView(cem);
                if (_openDocsByPath.TryGetValue(Viewer3DDocument.KeyFor(cem), out var d) && d is Viewer3DDocument v)
                    v.ViewModel.ShowMeshWhenAvailable = solver == Em3dSolver.Palace;
                return true;
            }
        }
        Messages.Warning($"'{name}' looks like a 3D run directory, but no .cem in this workspace names it " +
                         "as its run — open the setup and use Show 3D instead.");
        return true;
    }

    /// <summary>The key a camera is stored under: the .cem relative to the workspace root, forward
    /// slashes. Null outside a workspace — there is no window state to keep it in.</summary>
    private string? CameraKey(string cemPath)
        => WorkspaceRootDir is { } root ? Path.GetRelativePath(root, cemPath).Replace('\\', '/') : null;

    private Dictionary<string, CwsCamera3D> LoadStoredCameras()
        => CurrentWorkspacePath is { } cws && TryLoadCws(cws).Viewer3DCameras is { } stored
            ? new Dictionary<string, CwsCamera3D>(stored, StringComparer.Ordinal)
            : new Dictionary<string, CwsCamera3D>(StringComparer.Ordinal);

    private CwsCamera3D? StoredCamera(string cemPath)
    {
        _viewer3DCameras ??= LoadStoredCameras();
        return CameraKey(cemPath) is { } k && _viewer3DCameras.TryGetValue(k, out var c) ? c : null;
    }

    /// <summary>Every 3D view's camera — the open ones as they are now, the closed ones as they were
    /// left. Null when there is none, so a designer who never opened a 3D view gets no row.</summary>
    private Dictionary<string, CwsCamera3D>? Viewer3DCamerasToPersist()
    {
        _viewer3DCameras ??= LoadStoredCameras();
        foreach (var doc in _openDocsByPath.Values.OfType<Viewer3DDocument>())
            if (CameraKey(doc.CemPath) is { } k && doc.ViewModel.CameraToPersist() is { } cam) _viewer3DCameras[k] = cam;
        return _viewer3DCameras.Count > 0 ? _viewer3DCameras : null;
    }
}

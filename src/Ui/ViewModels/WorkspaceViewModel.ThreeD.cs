// brief-em3d-43 — where the 3D editor opens, what it follows, and how it is saved.
//
//   * a double-click on a .c3d in the Project Tree opens it (R-em3d43-1a), one tab per file;
//   * a placed cell's .c3d or .clay (or a technology) changing on disk re-elaborates — the elaborator's
//     child cache is keyed by the file's stamp, so only that instance is rebuilt — with no prompt;
//   * the open document itself changing on disk reloads it when it is clean, and asks when it is dirty,
//     as the layout editor does (R-em3d43-1d);
//   * Save, Save As, Save All, the close prompt and the quit prompt reach it as every other editor.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Render.Scene3D.Export;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ViewModels.ProjectTree;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Viewer3D;
using CircuitRF.Ui.Views.Dialogs;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

public partial class WorkspaceViewModel
{
    private readonly Dictionary<C3dEditorDocument, FileSystemWatcher> _c3dWatchers = [];

    /// <summary>Extensions whose change on disk re-elaborates an open 3D editor (a placed cell, a technology).</summary>
    private static readonly string[] C3dInputs = [".c3d", ".clay", ".ctech", ".wbond", ".cmat"];

    /// <summary>Opens (or focuses) the 3D editor on <paramref name="path"/>. It starts orthographic, as a setup's
    /// 3D view does: a view is drawn on a plane, and perspective foreshortens the grid being drawn on. A camera
    /// this workspace stored for it — projection included — is put back over that.</summary>
    public void OpenOrActivateC3dEditor(string path)
    {
        string full = Path.GetFullPath(path);
        string key = C3dEditorDocument.KeyFor(full);
        if (ActivateIfOpen(key)) return;

        C3dDocument document;
        try { document = C3dPersistence.LoadFromFile(full); }
        catch (Exception ex) when (ex is C3dReadException or IOException or UnauthorizedAccessException)
        {
            Messages.Error($"Could not open the 3D view '{Path.GetFileName(full)}': {ex.Message}");
            return;
        }

        // brief-em3d-64 R-em3d64-5b (D3) — a document holding a kernel object, enabled or not, is refused on open when this
        // installation has no geometry kernel: nothing is opened, and the dialog and the Messages line say why and what
        // fixes it. A document with none opens exactly as before and never asks the kernel anything.
        if (C3dKernelUse.RefusalOnOpen(document, KernelCapability) is { } why)
        {
            Messages.Error($"{C3dKernelUse.CannotOpen} {why}");
            _ = KernelRefusalDialog(Path.GetFileName(full), why);
            return;
        }
        OpenC3dEditor(full, key, document, scratch: false);
    }

    /// <summary>The geometry kernel's capability as the refusal on open reads it; a test substitutes an absent one.</summary>
    internal Func<CircuitRF.Design.ThreeD.Occ.GeometryKernelCapability> KernelCapability { get; set; } =
        () => CircuitRF.Design.ThreeD.Occ.GeometryKernel.Shared.Capability;

    /// <summary>The refusal's dialog (file name, the sentence); a test substitutes a recorder.</summary>
    internal Func<string, string, Task> KernelRefusalDialog { get; set; }

    /// <summary>[Open Settings ▸ Solvers] [Close] over the sentence — Settings' Solvers tab carries the kernel's row.</summary>
    private async Task ShowKernelRefusalAsync(string file, string why)
    {
        if (ResolveOwner(null) is not { } owner) return;
        if (await TextConfirmDialog.AskAsync(owner, file, C3dKernelUse.CannotOpen, why, "Open Settings ▸ Solvers"))
        {
            var settings = new SettingsView(CurrentWorkspacePath is { } ws ? Path.GetDirectoryName(ws) : null);
            settings.SelectTab("Solvers");
            settings.Show(owner);
        }
    }

    // ── On Launch ▸ New 3D Design (3D editor bugs round 2) ────────────────────────────────────

    /// <summary>
    /// A scratch 3D design to draw in at once — what Settings ▸ On Launch ▸ New 3D Design opens. Nothing is written for
    /// it: its path is where it WOULD be, so its technology and relative references resolve as a saved one's will, and
    /// its first save is a Save As. With a workspace open that is the workspace's root, so it draws in the workspace's
    /// technology; with none it is a workspace of its own in the temporary folder, made by the same
    /// <see cref="WorkspaceCreate.Create"/> New Workspace calls, on the default technology the New Workspace dialog opens
    /// on — without one no material would resolve and nothing drawn could be simulated once saved.
    /// </summary>
    internal void OpenScratchC3d()
    {
        string? folder = WorkspaceRootDir ?? ScratchC3dWorkspace();
        if (folder is null) return;
        string path = Path.Combine(folder, NextScratchC3dName(folder) + C3dPersistence.Extension);
        var (tech, _) = TechnologyResolver.ResolveForDocument(null, path, CurrentWorkspacePath, _techCache);
        var document = CircuitRF.Design.Cells.CellCreate.NewThreeDView(folder, tech.Tech);
        string full = Path.GetFullPath(path);
        OpenC3dEditor(full, C3dEditorDocument.KeyFor(full), document, scratch: true);
    }

    /// <summary>The session's own scratch workspace, made on first use in the scratch folder (Settings ▸ Solvers); null
    /// (and said) when it cannot be. A run of an unsaved 3D view writes its results here, so it follows that setting.</summary>
    private string? ScratchC3dWorkspace()
    {
        if (CircuitRF.Design.ScratchFolder.Refusal() is { } noScratch)
        {
            Messages.Error($"Could not make a scratch 3D design: {noScratch}");
            return null;
        }
        string parent = Path.Combine(CircuitRF.Design.ScratchFolder.Root, "circuitRF-scratch", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string dir = Path.Combine(parent, "3d");
        if (File.Exists(Path.Combine(dir, ".cws"))) return dir;
        try
        {
            Directory.CreateDirectory(parent);
            return WorkspaceCreate.Create(parent, "3d", WorkspaceCreate.DefaultTechnologyId).WorkspaceDir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Messages.Error($"Could not make a scratch 3D design: {ex.Message}");
            return null;
        }
    }

    /// <summary>The lowest free <c>Untitled-3D-N</c>: no file of that name in <paramref name="folder"/>, and no open tab.</summary>
    private string NextScratchC3dName(string folder)
    {
        for (int n = 1; ; n++)
        {
            string name = $"Untitled-3D-{n}";
            string path = Path.GetFullPath(Path.Combine(folder, name + C3dPersistence.Extension));
            if (!File.Exists(path) && !_openDocsByPath.ContainsKey(C3dEditorDocument.KeyFor(path))) return name;
        }
    }

    private void OpenC3dEditor(string full, string key, C3dDocument document, bool scratch)
    {
        try
        {
            var vm = new C3dEditorViewModel(full, document, Viewer3DBackends.Create, () => CurrentWorkspacePath,
                                            a => Dispatcher.UIThread.Post(a), _techCache, scratch);
            var doc = new C3dEditorDocument(vm);
            vm.Viewer.IsPerspective = false;
            // 3D editor round 5 — the camera (projection included) is window state, as a .cem's 3D view's always was: put
            // back as it was left, from the .cwsuser — never the .c3d, which stays the design.
            if (StoredCamera(full) is { } camera) vm.Viewer.RestoreCamera(camera);
            // brief-em3d-45 R-em3d45-1a — the drawing plane is window state: put back where it was left.
            if (StoredDrawingPlane(full) is { } plane) vm.SetPlane(plane);
            vm.DrawingPlaneChanged += () => RememberDrawingPlane(vm);
            // brief-em3d-49 — the active setup is per-user editor state; runs land in this workspace's results.
            vm.ResultsRootProvider = () => EmResultsRoot();
            vm.RestoreActiveSetup(StoredActiveSetup(full));
            vm.ActiveSetupChanged += () => RememberActiveSetup(vm);
            vm.SolveStatusesChecked += () => OnC3dSolveStatusesChecked(vm);     // brief-em3d-98 — the tree row and a float's title
            vm.ActiveSetupChanged += () => OnC3dSolveStatusesChecked(vm);
            vm.RunRequested = (c3d, setupName) => RunC3dSetupAsync(c3d, setupName);
            vm.MeshRequested = (c3d, setupName) => RunC3dSetupAsync(c3d, setupName, meshOnly: true);
            vm.SetupAnalysesRequested = c3d => _ = ShowC3dSetupAnalysesAsync(c3d, null);
            // brief-sar-12 — Place Against Phantom… is modal; Band Sweep… is not, since its runs go on for as long as they take.
            vm.ShowPlacementDialog = async dvm =>
            {
                if ((HostWindowOf(doc) ?? ResolveOwner(null)) is { } owner) await new Views.ThreeD.DevicePlacementDialog(dvm).ShowDialog<bool>(owner);
            };
            vm.ShowBandSweepDialog = dvm =>
            {
                var window = new Views.ThreeD.BandSweepDialog(dvm);
                if ((HostWindowOf(doc) ?? ResolveOwner(null)) is { } owner) window.Show(owner); else window.Show();
                return Task.CompletedTask;
            };
            vm.EditMaterialRequested = EditMaterial;    // brief-em3d-94
            // brief-em3d-101 — an image's notes and warnings go to Messages; Replace Image… and Resolve Path… open the picker.
            vm.PostMessage = (text, warning) => { if (warning) Messages.Warning(text, vm.FilePath); else Messages.Info(text, vm.FilePath); };
            vm.Viewer.PostMessage = vm.PostMessage;    // brief-dfr31-5 R-dfr31-5-3 — a build that failed says so once
            vm.UnsavedSetupFiles = () => [.. UnsavedSetupsNaming(clay => vm.Elaboration?.FilesRead.Contains(Path.GetFullPath(clay), StringComparer.OrdinalIgnoreCase) == true)
                                                 .Select(d => Path.GetFileName(d.FilePath))];
            vm.PickImageFile = async title =>
                (await CircuitRF.Ui.Views.ThreeD.ImageFilePicker.PickAsync(HostWindowOf(doc) ?? ResolveOwner(null), title, multiple: false)).FirstOrDefault();
            // brief-em3d-68 R-em3d68-5d — a reload's new parts are offered in the import table, unchecked.
            vm.OfferStepParts = (source, parts) => ShowStepImportAsync(vm, source, null, parts.ToHashSet(StringComparer.Ordinal));
            vm.ExternalChangeWhileDirty += () => _ = AskReloadC3dAsync(doc);
            // 3D editor round 1 — the selection's fields are the application's Properties Inspector: Properties (the
            // toolbar button, the menus) brings it forward, opening it when it was closed.
            // 3D menu cleanup — the 3D menu's Modify items follow this editor's selection while it is the active document.
            vm.MenuStateChanged += () => { if (ReferenceEquals(ActiveC3dEditor(), vm)) RaiseThreeDSelectionChanged(); };
            vm.PropertiesRequested += _ =>
            {
                ShowToolPanel(CircuitRF.Ui.Docking.DockPanelIds.Properties);
                _factory.PropertiesTool?.SetActiveC3d(vm);
                _propertiesShownFor = doc;
            };
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(C3dEditorViewModel.CanPopOut)) NotifyHierarchyCanExecuteChanged();
                if (e.PropertyName is not nameof(C3dEditorViewModel.IsDirty)) return;
                _factory.ProjectTreeTool?.SetFileDirty(vm.TopFilePath, vm.IsDirty);
                RaiseFileMenuEnablementChanged();
            };
            WireC3dHierarchy(doc);
            HookC3dMaterials(doc);
            HookC3dPaste(doc);
            _factory.OpenDocument(doc);
            _openDocsByPath[key] = doc;
            WatchC3d(doc);
            vm.Start();
            // Settings ▸ General ▸ 3D Designs — per user, never the .c3d: as if the toolbar button had been pressed.
            if (Realistic3DPreference.OnOpen) vm.Viewer.IsRealistic = true;
            RaiseThreeDMenuChanged();
        }
        catch (Exception ex)
        {
            Messages.Error($"Could not open the 3D editor: {ex.Message}");
        }
    }

    private void WatchC3d(C3dEditorDocument doc)
    {
        string root = WorkspaceRootDir ?? Path.GetDirectoryName(doc.FilePath)!;
        try
        {
            var w = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            };
            void OnChange(object? _, FileSystemEventArgs e)
            {
                if (!C3dInputs.Contains(Path.GetExtension(e.FullPath).ToLowerInvariant())) return;
                bool own = string.Equals(Path.GetFullPath(e.FullPath), doc.FilePath, StringComparison.OrdinalIgnoreCase);
                Dispatcher.UIThread.Post(own ? doc.ViewModel.OnFileChangedOnDisk : doc.ViewModel.OnChildChanged);
            }
            w.Changed += OnChange;
            w.Created += OnChange;
            w.Renamed += (s, e) => OnChange(s, e);
            // Designer round 32 — a watcher over a workspace a solver is writing field files into (and one on a synced cloud
            // folder) can overflow and drop the very save it exists to report; the scene then stayed on the old layout until the
            // .c3d was reopened. A bigger buffer makes that rarer, and an overflow re-checks every file the scene was built from.
            w.InternalBufferSize = 64 * 1024;
            w.Error += (_, _) => Dispatcher.UIThread.Post(doc.ViewModel.RecheckInputs);
            w.EnableRaisingEvents = true;
            _c3dWatchers[doc] = w;
        }
        catch (Exception ex)
        {
            Messages.Warning($"The 3D editor will not follow changes to its placed cells on disk: {ex.Message}");
        }
    }

    /// <summary>The open document changed on disk while it has unsaved edits: ask, as the layout editor does.</summary>
    private async Task AskReloadC3dAsync(C3dEditorDocument doc)
    {
        if (HostWindowOf(doc) is not { } window) return;
        var dlg = new Views.Dialogs.SaveChangesDialog(
            $"'{Path.GetFileName(doc.FilePath)}' changed on disk. Reload it and discard your unsaved changes?",
            saveLabel: "Keep Mine", dontSaveLabel: "Reload", cancelLabel: "Cancel", title: "File Changed on Disk");
        await dlg.ShowDialog(window);
        if (dlg.Result == SaveChangesResult.DontSave)
        {
            if (doc.ViewModel.Reload() is { } why) Messages.Error($"Could not reload '{doc.FilePath}': {why}");
        }
    }

    /// <summary>Writes a 3D view; reports either way. True when it was written.</summary>
    private bool SaveC3d(C3dEditorDocument doc)
    {
        if (doc.ViewModel.Save() is { } why)
        {
            Messages.Error($"Failed to save '{doc.FilePath}': {why}");
            return false;
        }
        Messages.Success("Saved", doc.FilePath);
        return true;
    }

    /// <summary>Save for a 3D view wherever a window can be asked: a scratch design's first save is a Save As (3D editor bugs
    /// round 2), anything else is <see cref="SaveC3d"/>. True when it was written; a cancelled picker is false.</summary>
    private async Task<bool> SaveC3dAsync(C3dEditorDocument doc, Window owner)
    {
        if (!doc.IsScratch) return SaveC3d(doc);
        await SaveC3dAs(doc, owner);
        return !doc.IsScratch;
    }

    /// <summary>Save As for a 3D view: a new .c3d beside the old one by default, which the tab then follows. A scratch
    /// design starts in the open workspace (its own folder is a temporary one), else wherever the picker opens.</summary>
    internal async Task SaveC3dAs(C3dEditorDocument doc, Window owner)
    {
        string? startDir = doc.IsScratch ? WorkspaceRootDir : Path.GetDirectoryName(doc.FilePath);
        var start = startDir is null ? null : await owner.StorageProvider.TryGetFolderFromPathAsync(new Uri(startDir));
        var file = await owner.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Save 3D View As",
            SuggestedFileName = Path.GetFileNameWithoutExtension(doc.FilePath),
            SuggestedStartLocation = start,
            DefaultExtension = "c3d",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("3D view") { Patterns = ["*.c3d"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        string oldKey = C3dEditorDocument.KeyFor(doc.FilePath);
        bool wasScratch = doc.IsScratch;
        if (doc.ViewModel.SaveAs(path) is { } why) { Messages.Error($"Failed to save '{path}': {why}"); return; }
        _openDocsByPath.Remove(oldKey);
        _openDocsByPath[C3dEditorDocument.KeyFor(path)] = doc;
        doc.FollowSavedAs();
        Messages.Success("Saved", path);
        // A scratch design's first file: the tree shows it when it landed in the workspace, and it is now a tab a .cws reopens.
        if (wasScratch) { _factory.ProjectTreeTool?.Refresh(); RaiseFileMenuEnablementChanged(); }
    }

    // ── hierarchy (brief-em3d-48) ─────────────────────────────────────────────────────────────

    /// <summary>What the 3D editor's hierarchy asks of the shell: open a layout (Push In on a layout child), refresh the
    /// tree (Group into Cell), a yes/no and a refusal (Flatten), Save / Discard / Cancel (Pop Out of a dirty child), a name, and whether
    /// a child is open in its own tab.</summary>
    private void WireC3dHierarchy(C3dEditorDocument doc)
    {
        var vm = doc.ViewModel;
        vm.OpenLayoutRequested += path => _ = OpenOrActivateLayoutAsync(path);
        vm.CellCreated += _ => _factory.ProjectTreeTool?.Refresh();
        vm.OpenElsewhere = path => _openDocsByPath.TryGetValue(C3dEditorDocument.KeyFor(path), out var other) && !ReferenceEquals(other, doc);
        vm.Confirm = async question =>
            HostWindowOf(doc) is { } w && await TextConfirmDialog.AskAsync(w, "3D Editor", "Confirm", question, "Continue");
        vm.Inform = async refusal =>
        {
            if (HostWindowOf(doc) is { } w) await TextConfirmDialog.AskAsync(w, "3D Editor", "Nothing was changed", refusal, null);
        };
        vm.AddMaterials = materials => Task.FromResult(AddC3dMaterials(doc, materials));
        vm.PopOutQuestion = async question =>
        {
            if (HostWindowOf(doc) is not { } w) return C3dPopOutChoice.Save;
            var dlg = new SaveChangesDialog(question, saveLabel: "Save", dontSaveLabel: "Discard", cancelLabel: "Cancel", title: "Pop Out");
            await dlg.ShowDialog(w);
            return dlg.Result switch { SaveChangesResult.Save => C3dPopOutChoice.Save, SaveChangesResult.DontSave => C3dPopOutChoice.Discard, _ => C3dPopOutChoice.Cancel };
        };
        vm.AskName = async (prompt, suggestion) =>
            HostWindowOf(doc) is { } w ? await new InputNameDialog("3D Editor", prompt, suggestion).ShowDialog<string?>(w) : null;
    }

    /// <summary>brief-em3d-48 R-em3d48-1a — Design ▸ Place Cell Instance… into a 3D view: the cell picker in its 3D mode
    /// (a View choice, no technology gate), the Reference Cell… escape hatch as the layout editor has it, then the
    /// placement armed — or refused at the pick with its reason.</summary>
    private async Task PlaceCellInstanceIn3DAsync(C3dEditorDocument doc)
    {
        if (HostWindowOf(doc) is not { } owner) return;
        var vm = doc.ViewModel;
        string baseDir = Path.GetDirectoryName(vm.FilePath)!;
        while (true)
        {
            var dialog = new InstanceCellPickerDialog(WorkspaceRootDir, baseDir, C3dHierarchy.CellDirOf(vm.FilePath),
                                                      CircuitRF.Design.Cells.ViewType.ThreeD, CanReferenceExternalCell);
            var pick = await dialog.ShowDialog<CircuitRF.Ui.Layout.CellPickResult?>(owner);
            if (pick is null) return;
            if (pick.ReferenceRequested)
            {
                string? broughtIn = await ReferenceExternalCellAsync();
                if (broughtIn is null) continue;
                pick = new CircuitRF.Ui.Layout.CellPickResult(ExternalCellRef.MakeCellRef(baseDir, broughtIn), broughtIn);
            }
            if (pick.CellRef.Length == 0) return;
            var view = pick.View switch
            {
                CircuitRF.Design.Cells.ViewType.Layout => C3dInstanceView.Layout,
                CircuitRF.Design.Cells.ViewType.ThreeD => C3dInstanceView.ThreeD,
                _ => C3dHierarchy.ViewFile(pick.AbsoluteCellDir, C3dInstanceView.ThreeD) is null ? C3dInstanceView.Layout : C3dInstanceView.ThreeD,
            };
            if (vm.BeginInstancePlacement(pick.CellRef, pick.AbsoluteCellDir, view) is { } why) Messages.Warning(why);
            return;
        }
    }

    /// <inheritdoc/>
    public async Task New3DViewFromLayoutAsync(ProjectTreeNodeViewModel cellNode) => await NewThreeDViewFromLayoutAsync(cellNode.AbsolutePath);

    /// <summary>
    /// brief-em3d-48 R-em3d48-7 — a cell's 3D view from its own layout: the name asked, the document made by
    /// C3dHierarchy.NewFromLayout (the one place it is made), written with CellCreate's writer, and opened. What could
    /// not be carried across — a port that is not one rectangle — is said, never dropped in silence.
    /// </summary>
    private async Task NewThreeDViewFromLayoutAsync(string cellDir)
    {
        if (ResolveOwner(null) is not { } window) return;
        string cellName = Path.GetFileName(cellDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var suggested = CircuitRF.Ui.Schematic.ViewFileNameSuggestion.Suggest(cellDir, cellName, CircuitRF.Design.Cells.ViewType.ThreeD);
        var name = await new InputNameDialog("New 3D View from Layout", "3D view file name (without extension):", suggested).ShowDialog<string?>(window);
        if (name is null) return;
        if (CircuitRF.Design.Cells.NameValidator.Validate(name) is { } reason) { Messages.Error($"Invalid 3D view name: {reason}"); return; }
        var path = Path.Combine(CircuitRF.Design.Cells.CellFolder.SubFolderPath(cellDir, CircuitRF.Design.Cells.ViewType.ThreeD), name + C3dPersistence.Extension);
        if (File.Exists(path)) { Messages.Error($"A file named '{name}{C3dPersistence.Extension}' already exists."); return; }
        if (!await SaveCellLayoutsBeforeThreeD(cellDir, window)) return;
        var made = C3dHierarchy.NewFromLayout(cellDir, path, CurrentWorkspacePath, _techCache);
        if (made.Document is not { } c3d) { Messages.Error($"New 3D View from Layout: {made.Refusal}"); return; }
        try { CircuitRF.Design.Cells.CellCreate.WriteThreeDView(cellDir, name, c3d); }
        catch (Exception ex) { Messages.Error($"Failed to create the 3D view: {ex.Message}"); return; }
        _factory.ProjectTreeTool?.Refresh();
        RefreshCellEditorFileLists(cellDir);
        Messages.Success("Created", path);
        if (made.SetupsCopied.Count > 0) Messages.Info($"Copied {made.SetupsCopied.Count} 3D setup(s) as embedded setups: {string.Join(", ", made.SetupsCopied)}.");
        if (made.PortsNote is { } portsNote) Messages.Info(portsNote);
        foreach (string moved in made.PortsMoved) Messages.Info(moved);
        if (made.PortsLeftOut.Count > 0) Messages.Warning(C3dHierarchy.PortsLeftOutSentence(made.PortsLeftOut));
        OpenOrActivateC3dEditor(path);
    }

    /// <summary>
    /// Designer feedback round 34 — a 3D view is made from the cell's layout AS SAVED, so a via deleted in an open, unsaved
    /// layout came back in the new view. An open layout of the cell with unsaved edits is offered Save / Use Saved / Cancel
    /// first. False when the user cancelled (or a save was cancelled).
    /// </summary>
    private async Task<bool> SaveCellLayoutsBeforeThreeD(string cellDir, Avalonia.Controls.Window owner)
    {
        string layoutDir = Path.GetFullPath(CircuitRF.Design.Cells.CellFolder.SubFolderPath(cellDir, CircuitRF.Design.Cells.ViewType.Layout))
                           + Path.DirectorySeparatorChar;
        var dirty = _openDocsByPath.Values.OfType<Layout.LayoutDocument>()
            .Where(d => d.IsDirty && d.FilePath is { } p && Path.GetFullPath(p).StartsWith(layoutDir, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var tab in dirty)
        {
            var dlg = new Views.Dialogs.SaveChangesDialog(
                $"'{Path.GetFileName(tab.FilePath)}' has unsaved changes, and a 3D view is made from the layout as saved. " +
                "Save them first?",
                dontSaveLabel: "Use Saved", title: "Unsaved Layout");
            await dlg.ShowDialog(owner);
            switch (dlg.Result)
            {
                case Views.Dialogs.SaveChangesResult.Save:
                    await SaveSingleLayoutDocument(tab, owner);
                    if (tab.IsDirty) return false;   // the save was cancelled
                    break;
                case Views.Dialogs.SaveChangesResult.DontSave:
                    break;
                default:
                    return false;
            }
        }
        foreach (var setup in UnsavedSetupsNaming(p => Path.GetFullPath(p).StartsWith(layoutDir, StringComparison.OrdinalIgnoreCase)))
        {
            var dlg = new Views.Dialogs.SaveChangesDialog(
                $"'{Path.GetFileName(setup.FilePath)}' has unsaved changes, and a 3D view takes its ports from the setup as saved. " +
                "Save them first?",
                dontSaveLabel: "Use Saved", title: "Unsaved EM Setup");
            await dlg.ShowDialog(owner);
            switch (dlg.Result)
            {
                case Views.Dialogs.SaveChangesResult.Save:
                    setup.ViewModel.SaveCommand.Execute(null);
                    if (setup.IsDirty) return false;   // the save failed, and the panel has said why
                    break;
                case Views.Dialogs.SaveChangesResult.DontSave:
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Designer round 36 — the open EM setups with unsaved edits whose layout <paramref name="layout"/> accepts. A 3D view's ports are
    /// translated with the .cem as saved, so a Return changed in the panel and not saved left a port out of every new 3D view until a
    /// restart's save prompt happened to write it.
    /// </summary>
    private List<EmSetupDocument> UnsavedSetupsNaming(Func<string, bool> layout)
        => [.. _openDocsByPath.Values.OfType<EmSetupDocument>()
                .Where(d => d.IsDirty && EmSetupLayoutPathOf(d.FilePath) is { } clay && layout(clay))];

    /// <summary>
    /// brief-dfr26-1 R-dfr26-1-6 — New 3D View from Layout for a layout FILE: the layout editor's toolbar and the EM setup
    /// panel, beside its Show 3D. The cell is the folder its layout subfolder sits in; a layout in no cell is said.
    /// </summary>
    public Task NewThreeDViewFromLayoutForLayoutAsync(string clayPath)
    {
        string? cellDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(clayPath)));
        if (cellDir is null || !File.Exists(Path.Combine(cellDir, CircuitRF.Design.Cells.CellFolder.CcellFileName)))
        {
            Messages.Error($"New 3D View from Layout: '{Path.GetFileName(clayPath)}' is not saved in a cell, and a 3D view is made in its layout's cell.");
            return Task.CompletedTask;
        }
        return NewThreeDViewFromLayoutAsync(cellDir);
    }

    /// <summary>Design ▸ New 3D View from Layout — the active layout's cell.</summary>
    [RelayCommand(CanExecute = nameof(CanNewThreeDViewFromLayout))]
    private async Task NewThreeDViewFromLayout()
    {
        if (ActiveLayoutDocument?.ActiveViewModel.CurrentCellDir is { Length: > 0 } cellDir) await NewThreeDViewFromLayoutAsync(cellDir);
    }

    private bool CanNewThreeDViewFromLayout() => ActiveLayoutDocument?.ActiveViewModel.CurrentCellDir is { Length: > 0 };

    /// <summary>brief-em3d-48 R-em3d48-3c — 3D ▸ Triangle Budget…: the frame's budget before array elements draw as boxes.</summary>
    [RelayCommand]
    private async Task ThreeDTriangleBudget()
    {
        if (ResolveOwner(null) is not { } window) return;
        string current = Lod3DPreference.Budget.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var text = await new InputNameDialog("Triangle Budget",
            $"Triangles a 3D frame draws before array elements, farthest first, are drawn as boxes (at least {Lod3DPreference.Minimum:N0}):",
            current).ShowDialog<string?>(window);
        if (text is null) return;
        if (!long.TryParse(text.Replace(",", "").Replace("_", "").Trim(), System.Globalization.NumberStyles.Integer,
                           System.Globalization.CultureInfo.InvariantCulture, out long budget) || budget < Lod3DPreference.Minimum)
        {
            Messages.Error($"'{text}' is not a whole number of at least {Lod3DPreference.Minimum:N0}.");
            return;
        }
        Lod3DPreference.Budget = budget;
        foreach (var d in _openDocsByPath.Values)
        {
            if (d is C3dEditorDocument e) { e.ViewModel.Viewer.TriangleBudget = budget; e.ViewModel.Viewer.RequestFrame(); }
            else if (d is Viewer3DDocument v) { v.ViewModel.TriangleBudget = budget; v.ViewModel.RequestFrame(); }
        }
    }

    // ── the drawing plane in the window state (brief-em3d-45 R-em3d45-1a) ─────────────────────

    private Dictionary<string, CwsDrawingPlane>? _drawingPlanes;

    private Dictionary<string, CwsDrawingPlane> LoadStoredDrawingPlanes()
        => CurrentWorkspacePath is { } cws && TryLoadCws(cws).C3dDrawingPlanes is { } stored
            ? new Dictionary<string, CwsDrawingPlane>(stored, StringComparer.Ordinal)
            : new Dictionary<string, CwsDrawingPlane>(StringComparer.Ordinal);

    private DrawingPlane? StoredDrawingPlane(string c3dPath)
    {
        _drawingPlanes ??= LoadStoredDrawingPlanes();
        return CameraKey(c3dPath) is { } k && _drawingPlanes.TryGetValue(k, out var p) && Enum.TryParse<C3dPlane>(p.Plane, out var plane)
            ? new DrawingPlane(plane, p.OffsetDbu) : null;
    }

    /// <summary>The plane as it is now; the default plane is no row at all.</summary>
    private void RememberDrawingPlane(C3dEditorViewModel vm)
    {
        if (CameraKey(vm.TopFilePath) is not { } k) return;
        _drawingPlanes ??= LoadStoredDrawingPlanes();
        if (vm.Plane == DrawingPlane.Default) _drawingPlanes.Remove(k);
        else _drawingPlanes[k] = new CwsDrawingPlane { Plane = vm.Plane.Plane.ToString(), OffsetDbu = vm.Plane.OffsetDbu };
    }

    /// <summary>Every 3D editor's drawing plane for the <c>.cwsuser</c> — null when none is off the default.</summary>
    // ── the active setup in the window state (brief-em3d-49 R-em3d49-1a) ──────────────────────

    private Dictionary<string, string>? _activeSetups;

    private string? StoredActiveSetup(string c3dPath)
    {
        _activeSetups ??= CurrentWorkspacePath is { } cws && TryLoadCws(cws).C3dActiveSetups is { } stored
            ? new Dictionary<string, string>(stored, StringComparer.Ordinal) : new Dictionary<string, string>(StringComparer.Ordinal);
        return CameraKey(c3dPath) is { } k && _activeSetups.TryGetValue(k, out var name) ? name : null;
    }

    private void RememberActiveSetup(C3dEditorViewModel vm)
    {
        if (CameraKey(vm.TopFilePath) is not { } k) return;
        _activeSetups ??= new Dictionary<string, string>(StringComparer.Ordinal);
        if (vm.ActiveSetupName is { } name) _activeSetups[k] = name; else _activeSetups.Remove(k);
    }

    private Dictionary<string, string>? ActiveSetupsToPersist()
    {
        foreach (var doc in _openDocsByPath.Values.OfType<C3dEditorDocument>()) RememberActiveSetup(doc.ViewModel);
        return _activeSetups is { Count: > 0 } a ? a : null;
    }

    /// <summary>Where an EM run from this window lands: the workspace's results folder, or the session's.</summary>
    private string EmResultsRoot()
        => Path.Combine(CurrentWorkspacePath is { } cws ? Path.GetDirectoryName(cws)! : _recovery.SessionDir, "results");

    /// <summary>
    /// brief-em3d-49 R-em3d49-5a — a 3D view's setup run: the same path a .cem's run takes (discovery, progress, cancel,
    /// refusals, the Data Display), with the geometry the document itself — elaborated and assembled with the setup.
    /// </summary>
    /// <param name="meshOnly">brief-dfr36-1 R-dfr36-1-1 — make the setup's mesh and stop: the same folder, ceiling, cancel and progress
    /// row, no result and no question about replacing one. When it is the active setup's, the view turns the mesh on.</param>
    internal async Task RunC3dSetupAsync(C3dEditorViewModel c3d, string? setupName, bool meshOnly = false)
    {
        // 3D editor bugs round 2 — a scratch design is saved before it runs (Simulate offers the Save As): a run belongs to a file on disk.
        if (c3d.IsScratch)
        {
            var doc = _openDocsByPath.Values.OfType<C3dEditorDocument>().FirstOrDefault(d => ReferenceEquals(d.ViewModel, c3d));
            if (doc is null || HostWindowOf(doc) is not { } window || !await SaveC3dAsync(doc, window))
            {
                Messages.Warning($"'{Path.GetFileName(c3d.FilePath)}' has not been saved: save it (File ▸ Save As…) to simulate it.");
                return;
            }
        }
        var (setup, fromCem, refusal) = c3d.RunSetupFor(setupName);
        if (setup is null) { Messages.Warning(refusal ?? "Nothing to simulate."); return; }
        if (meshOnly && CircuitRF.Design.Em3d.Em3dRunService.MeshButtonRefusal(setup) is { } noMesh) { Messages.Warning(noMesh); return; }
        // brief-em3d-98 R-em3d98-7 — one line before replacing a complete, current result (the setup run, active or not).
        if (!meshOnly && !fromCem && (setupName ?? c3d.ActiveSetupName) is { } embedded && !await ConfirmRerunAsync(c3d, embedded)) return;
        var document = c3d.RunDocument();
        string path = c3d.TopFilePath;
        string? cws = CurrentWorkspacePath;
        // The panel whose Simulate/Cancel reflect the run: the setup editor when it shows this setup, else a transient one.
        var panel = c3d.SetupEditor?.ViewModel is { IsEmbedded: true } shown && !fromCem &&
                    C3dSetups.ForRun(shown.Working, path).Name == setup.Name
            ? shown : new CircuitRF.Ui.Layout.Em.EmSetupEditorViewModel(path, setup.Clone(), embedded: true);
        bool ok = await RunEmSetupAsync(panel, setup, (s, control, root) => EmRunService.RunThreeDView(
            s, document, path, cws, root, default, control, CircuitRF.Ui.Layout.Em.EmSolveCorePreference.Preferred, ConfirmEmMemory, fromCem,
            device: CircuitRF.Ui.Layout.Em.EmPalaceDevicePreference.Preferred,
            memoryCeilingBytes: CircuitRF.Ui.Layout.Em.EmMemoryLimitPreference.CeilingBytes,
            emergeDevice: CircuitRF.Ui.Layout.Em.EmEmergeDevicePreference.Preferred,
            target: fromCem ? null : new EmRunTarget { SetupPath = path, EmbeddedSetup = setupName ?? c3d.ActiveSetupName }), meshOnly);
        if (ok && meshOnly && (setupName is null || setupName == c3d.ActiveSetupName)) c3d.Viewer.ShowMeshWhenAvailable = true;
        // brief-em3d-98 R-em3d98-6 — whichever setup ran, and however it ended (a cancelled run's status changed too).
        if (ok) c3d.RunFinished();
        else c3d.RefreshSolveStatusNow();
    }

    /// <summary>Simulate ▸ Mesh Only: the 3D view's active setup. Refused with its reason, in Messages, on a setup with no Gmsh mesh.</summary>
    [RelayCommand(CanExecute = nameof(CanMeshOnly))]
    private Task MeshOnly()
        => _factory.DocumentDock?.ActiveDockable is CircuitRF.Ui.ThreeD.C3dEditorDocument c3d
            ? RunC3dSetupAsync(c3d.ViewModel, null, meshOnly: true) : Task.CompletedTask;

    private bool CanMeshOnly() => _factory.DocumentDock?.ActiveDockable is CircuitRF.Ui.ThreeD.C3dEditorDocument;

    /// <summary>
    /// Simulate ▸ Setup Analyses… with a 3D view active (and the editor's tune button): the analysis list a schematic
    /// gets, on the 3D view's embedded setups. Every change in it is an edit of the 3D view — its undo, its dirty mark.
    /// </summary>
    internal async Task ShowC3dSetupAnalysesAsync(C3dEditorViewModel c3d, Window? owner)
    {
        if (ResolveOwner(owner) is not { } window) return;
        await new Views.Dialogs.C3dSetupAnalysesDialog(c3d).ShowDialog(window);
    }

    private Dictionary<string, CwsDrawingPlane>? DrawingPlanesToPersist()
    {
        _drawingPlanes ??= LoadStoredDrawingPlanes();
        foreach (var doc in _openDocsByPath.Values.OfType<C3dEditorDocument>()) RememberDrawingPlane(doc.ViewModel);
        return _drawingPlanes.Count > 0 ? _drawingPlanes : null;
    }

    private void ClosedC3dEditor(C3dEditorDocument doc)
    {
        RememberDrawingPlane(doc.ViewModel);
        // 3D editor round 5 — the Analyses panel lets go of a closed .c3d's setups (and falls back to the schematic's list).
        if (_factory.AnalysesTool is { } tool && ReferenceEquals(tool.C3dEditor, doc.ViewModel)) tool.C3dEditor = null;
        // Not after a workspace switch has dropped the table: the outgoing session was persisted with this camera in it.
        if (_viewer3DCameras is not null && !doc.IsScratch && CameraKey(doc.FilePath) is { } k && doc.ViewModel.Viewer.CameraToPersist() is { } cam)
            _viewer3DCameras[k] = cam;
        if (_c3dWatchers.Remove(doc, out var w)) w.Dispose();
        _factory.ProjectTreeTool?.SetFileDirty(doc.FilePath, false);
        if (!doc.IsScratch) InvalidateTreeSolve(doc.FilePath);     // brief-em3d-98 — the row goes back to the file's answer
        NotifyHierarchyCanExecuteChanged();
        doc.ViewModel.Dispose();
        RaiseThreeDMenuChanged();
    }

    // ── the 3D menu (owner decision D9, overview §1m) ─────────────────────────────────────────
    //
    // ONE top-level menu. Every item acts on the active 3D pane — the editor's or the read-only viewer's,
    // which share their view model — and is enabled only when there is one. Nothing here is a second
    // implementation: each item calls the command the pane's own toolbar and keys call.
    //
    // 3D editor bugs round 4 — the menu is SHOWN only while a 3D pane is the active document; with any other
    // document active it is hidden, not merely disabled. On macOS a window's NativeMenu is fixed for its
    // lifetime, so the menu is never added or removed: its item's IsVisible is bound (IsThreeDMenuVisible),
    // on the NativeMenuItem and on the in-window MenuItem alike, and re-raised from both enablement fan-outs.

    /// <summary>The active 3D pane's view model, or null.</summary>
    private Viewer3DViewModel? Active3DPane() => ResolveActiveDocumentForCommands() switch
    {
        C3dEditorDocument e => e.ViewModel.Viewer,
        Viewer3DDocument v  => v.ViewModel,
        _                   => null,
    };

    private bool HasActive3DPane() => Active3DPane() is not null;

    /// <summary>3D editor bugs round 4 — whether the top-level 3D menu is shown: a 3D document (the editor or the
    /// read-only viewer) is the one the shell's commands act on.</summary>
    public bool IsThreeDMenuVisible => ShowsThreeDMenu(ResolveActiveDocumentForCommands());

    /// <summary>The 3D menu's rule, on the dockable the shell's commands resolve to.</summary>
    internal static bool ShowsThreeDMenu(object? activeDocument) => activeDocument is C3dEditorDocument or Viewer3DDocument;

    /// <summary>brief-em3d-66 R-em3d66-7 — 3D ▸ Boolean: shown always, disabled without the kernel.</summary>
    public bool ThreeDBooleanAvailable => CircuitRF.Ui.ThreeD.GeometryKernelAvailability.IsAvailable;

    /// <summary>3D ▸ Boolean's tooltip: the capability's own sentence when that is why it is disabled, then what every 3D ▸
    /// item says.</summary>
    public string ThreeDBooleanTip
        => (CircuitRF.Ui.ThreeD.GeometryKernelAvailability.DisabledReason("Boolean") is { } why ? why + " " : "") + "Requires an active 3D editor.";

    /// <summary>brief-em3d-67 — 3D ▸ Modify ▸ Edge ▸ Fillet… and Chamfer…: what they do, after the capability's own sentence
    /// when that is why they are disabled.</summary>
    public string ThreeDFilletTip => EdgeTip("Fillet", "Round the selected edges of one solid with a radius; previewed before OK.");
    public string ThreeDChamferTip => EdgeTip("Chamfer", "Bevel the selected edges of one solid, at one distance or two; previewed before OK.");

    private static string EdgeTip(string what, string does)
        => (CircuitRF.Ui.ThreeD.GeometryKernelAvailability.DisabledReason(what) is { } why ? why + " " : does + " ") + "Requires an active 3D editor.";

    // ── Import STEP (brief-em3d-68) ────────────────────────────────────────────────────────────

    /// <summary>File ▸ Import ▸ STEP…: shown always, disabled without the kernel or a 3D editor.</summary>
    public bool ThreeDImportStepAvailable => CircuitRF.Ui.ThreeD.GeometryKernelAvailability.IsAvailable && HasActiveC3dEditor();

    /// <summary>The Import STEP items' tooltip: the capability's own sentence when that is why they are disabled.</summary>
    public string ThreeDImportStepTip
        => (CircuitRF.Ui.ThreeD.GeometryKernelAvailability.DisabledReason("Import STEP") is { } why ? why + " "
            : "A STEP file's parts in this 3D view, one object per part, each with a material you choose. ") + "Requires an active 3D editor.";

    private bool CanImportStep() => ThreeDImportStepAvailable;

    [RelayCommand(CanExecute = nameof(CanImportStep))]
    private async Task ImportStep(Window? owner)
    {
        if (ActiveC3dEditor() is not { } editor) return;
        if (editor.ImportStepRefusal() is { } refusal) { Messages.Error(refusal); return; }
        if (ResolveOwner(owner) is not { } window) return;
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import STEP",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("STEP") { Patterns = ["*.step", "*.stp", "*.STEP", "*.STP"] },
                new FilePickerFileType("All Files") { Patterns = ["*.*"] },
            ],
        });
        if (files.Count == 0) return;
        await ShowStepImportAsync(editor, files[0].Path.LocalPath, window, null);
    }

    /// <summary>The Import STEP dialog on <paramref name="source"/> — reading, then the table — and the editor's commit on OK.
    /// <paramref name="onlyParts"/> limits the table to a reload's new parts, unchecked.</summary>
    private async Task ShowStepImportAsync(C3dEditorViewModel editor, string source, Window? owner, IReadOnlySet<string>? onlyParts)
    {
        if (ResolveOwner(owner) is not { } window) return;
        var dialogVm = new StepImportDialogViewModel(source, control => editor.ReadStep(source, control),
                                                     plan => CircuitRF.Design.ThreeD.Step.StepImport.NamesRefusal(plan, editor.Document), onlyParts,
                                                     plan => CircuitRF.Design.ThreeD.Step.StepImport.GroupRefusal(plan, editor.Document));
        bool ok = await new CircuitRF.Ui.Views.ThreeD.StepImportDialog(dialogVm).ShowDialog<bool>(window);
        if (!ok || dialogVm.Plan is not { } plan) return;
        if (editor.AcceptStepImport(plan) is { } why) { Messages.Error(why); return; }
        foreach (string note in editor.LastStepNotes) Messages.Info(note, editor.FilePath);
    }

    // ── Import anatomy (brief-sar-11) ──────────────────────────────────────────────────────────

    /// <summary>File ▸ Import ▸ Voxel Model… and Surface Meshes…: enabled with an active 3D editor (no geometry kernel needed).</summary>
    public bool ThreeDImportAnatomyAvailable => HasActiveC3dEditor();

    public string ThreeDImportAnatomyTip
        => "A segmented voxel model (NIfTI, raw, a DiscMaterial .h5, or a tetrahedral .node/.ele voxelised), or one closed surface " +
           "per tissue (STL, OBJ, PLY), in this 3D view, each label or surface given a material. openEMS runs it. Requires an active 3D editor.";

    private bool CanImportAnatomy() => ThreeDImportAnatomyAvailable;

    [RelayCommand(CanExecute = nameof(CanImportAnatomy))]
    private async Task ImportVoxelModel(Window? owner)
    {
        if (ActiveC3dEditor() is not { } editor) return;
        if (editor.ImportAnatomyRefusal() is { } refusal) { Messages.Error(refusal); return; }
        if (ResolveOwner(owner) is not { } window) return;
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Voxel Model",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Label volumes") { Patterns = ["*.nii", "*.nii.gz", "*.gz", "*.h5", "*.hdf5", "*.raw", "*.bin", "*.vol", "*.node", "*.ele"] },
                new FilePickerFileType("All Files") { Patterns = ["*.*"] },
            ],
        });
        if (files.Count == 0) return;
        string source = files[0].Path.LocalPath;
        var vm = new VoxelImportDialogViewModel(source, editor.AnatomyMaterials(), editor.Document.DbuPerMicron,
                                                options => editor.PlanVoxelImport(source, options));
        bool ok = await new CircuitRF.Ui.Views.ThreeD.VoxelImportDialog(vm).ShowDialog<bool>(window);
        if (!ok || vm.Plan is not { } plan) return;
        if (editor.AcceptVoxelImport(plan) is { } why) { Messages.Error(why); return; }
        foreach (string line in plan.Report.Lines(plan.Rows)) Messages.Info(line, editor.FilePath);
        foreach (string note in editor.LastAnatomyNotes) Messages.Info(note, editor.FilePath);
        if (plan.Report.Warning is { } w) Messages.Warning(w, editor.FilePath);
    }

    [RelayCommand(CanExecute = nameof(CanImportAnatomy))]
    private async Task ImportSurfaceMeshes(Window? owner)
    {
        if (ActiveC3dEditor() is not { } editor) return;
        if (editor.ImportAnatomyRefusal() is { } refusal) { Messages.Error(refusal); return; }
        if (ResolveOwner(owner) is not { } window) return;
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Surface Meshes (one closed surface per tissue)",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Surface meshes") { Patterns = ["*.stl", "*.obj", "*.ply", "*.STL", "*.OBJ", "*.PLY"] },
                new FilePickerFileType("All Files") { Patterns = ["*.*"] },
            ],
        });
        if (files.Count == 0) return;
        var paths = files.Select(f => f.Path.LocalPath).ToList();
        var vm = new SurfaceImportDialogViewModel(paths, editor.AnatomyMaterials(), unit => editor.PlanSurfaceImport(paths, unit));
        bool ok = await new CircuitRF.Ui.Views.ThreeD.SurfaceImportDialog(vm).ShowDialog<bool>(window);
        if (!ok || vm.Plan is not { } plan) return;
        if (editor.AcceptSurfaceImport(plan) is { } why) { Messages.Error(why); return; }
        foreach (string note in editor.LastAnatomyNotes) Messages.Info(note, editor.FilePath);
    }

    // ── Export STEP (brief-em3d-69) ────────────────────────────────────────────────────────────

    /// <summary>File ▸ Export ▸ STEP…: shown always, enabled with the kernel and an active 3D or layout
    /// document (R-em3d69-4b).</summary>
    public bool ThreeDExportStepAvailable => CircuitRF.Ui.ThreeD.GeometryKernelAvailability.IsAvailable && StepExportSource() is not null;

    /// <summary>The Export STEP items' tooltip: the capability's own sentence when that is why they are disabled.</summary>
    public string ThreeDExportStepTip
        => (CircuitRF.Ui.ThreeD.GeometryKernelAvailability.DisabledReason("Export STEP") is { } why ? why + " "
            : "The elaborated model — the solids the solver gets, named and coloured — as one STEP file. ") +
           "Requires an active 3D or layout document.";

    private bool CanExportStep() => ThreeDExportStepAvailable;

    /// <summary>What an export of the active document reads: its top file, and its model as it stands when that is what the
    /// tab shows (a 3D view pushed into a placed cell exports the top document from disk).</summary>
    private (string Path, CircuitRF.Design.ThreeD.Step.StepExportOptions Source)? StepExportSource()
    {
        switch (ResolveActiveDocumentForCommands())
        {
            case C3dEditorDocument c:
            {
                var vm = c.ViewModel;
                bool top = string.Equals(Path.GetFullPath(vm.FilePath), Path.GetFullPath(vm.TopFilePath), StringComparison.OrdinalIgnoreCase);
                return (vm.TopFilePath, new CircuitRF.Design.ThreeD.Step.StepExportOptions
                {
                    Document = top ? vm.Document : null, WorkspaceCws = CurrentWorkspacePath,
                });
            }
            case LayoutDocument l when l.NavDepth == 0 && l.ActiveViewModel.CurrentLayoutPath is { Length: > 0 } clay:
                return (clay, new CircuitRF.Design.ThreeD.Step.StepExportOptions
                {
                    Layout = l.ActiveViewModel.Model, WorkspaceCws = CurrentWorkspacePath,
                });
            default:
                return null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportStep))]
    private async Task ExportStep(Window? owner)
    {
        if (StepExportSource() is not { } src) return;
        if (ResolveOwner(owner) is not { } window) return;
        var start = await window.StorageProvider.TryGetFolderFromPathAsync(new Uri(Path.GetDirectoryName(src.Path)!));
        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export STEP",
            // NO extension on the suggested name: Avalonia's storage provider appends DefaultExtension itself, so
            // supplying both spelled it twice ("x.step.step"). src/Ui/RESOLVED.md, SuggestedFileNameGateTests.
            SuggestedFileName = Path.GetFileNameWithoutExtension(src.Path),
            SuggestedStartLocation = start,
            DefaultExtension = "step",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("STEP") { Patterns = ["*.step", "*.stp"] }],
        });
        if (file?.TryGetLocalPath() is not { } target) return;

        var kernel = CircuitRF.Design.ThreeD.Occ.GeometryKernel.Shared;
        var vm = new StepExportDialogViewModel(target, src.Source,
            options => CircuitRF.Design.ThreeD.Step.StepExport.Plan(src.Path, options, kernel, _techCache),
            (plan, control) => CircuitRF.Design.ThreeD.Step.StepExport.Write(plan, target, kernel, control));
        bool ok = await new CircuitRF.Ui.Views.ThreeD.StepExportDialog(vm).ShowDialog<bool>(window);
        if (!ok || vm.Result is not { } result) return;
        foreach (string note in result.Notes) Messages.Info(note, src.Path);
        Messages.Success($"Exported STEP: {result.Plan.Summary}", result.Path);
    }

    // ── Export glTF (brief-em3d-111) ───────────────────────────────────────────────────────────

    /// <summary>File ▸ Export ▸ glTF…'s tooltip: what it writes, then what it needs.</summary>
    public string ThreeDExportGltfTip
        => "The model as the view draws it, with its appearances and smooth normals, as one binary glTF (.glb) for another renderer — " +
           "a path tracer gives the refraction and caustics the realistic view approximates. Requires an active 3D document " +
           "or an EM setup's 3D view.";

    /// <summary>What an export of the active document reads: the file it is named after and the view's scene, camera and plots —
    /// a .c3d editor's (with the elaboration behind it) or a setup's Show 3D (a scene with no document).</summary>
    private (string Path, Func<GltfExportSource> Source, Func<GltfCamera> Camera, Func<IReadOnlyList<GltfField>> Fields)? GltfExportTarget()
        => ResolveActiveDocumentForCommands() switch
        {
            C3dEditorDocument e => (e.ViewModel.FilePath, e.ViewModel.GltfSource, e.ViewModel.GltfCamera, e.ViewModel.GltfFields),
            Viewer3DDocument v when v.ViewModel.Scene.Objects.Length > 0 =>
                (v.CemPath, () => v.ViewModel.GltfSource(Path.GetFileNameWithoutExtension(v.CemPath)), v.ViewModel.GltfCamera,
                 v.ViewModel.GltfFields),
            _ => null,
        };

    private bool CanExportGltf() => GltfExportTarget() is not null;

    /// <summary>R-em3d111-3a — needs no geometry kernel: it writes the view's own triangles.</summary>
    [RelayCommand(CanExecute = nameof(CanExportGltf))]
    private async Task ExportGltf(Window? owner)
    {
        if (GltfExportTarget() is not { } src) return;
        if (ResolveOwner(owner) is not { } window) return;
        var start = await window.StorageProvider.TryGetFolderFromPathAsync(new Uri(Path.GetDirectoryName(src.Path)!));
        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export glTF",
            // NO extension on the suggested name: the storage provider appends DefaultExtension itself (SuggestedFileNameGateTests).
            SuggestedFileName = Path.GetFileNameWithoutExtension(src.Path),
            SuggestedStartLocation = start,
            DefaultExtension = "glb",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("glTF binary") { Patterns = ["*.glb"] }],
        });
        if (file?.TryGetLocalPath() is not { } target) return;

        var vm = new GltfExportDialogViewModel(target, src.Source(), src.Camera(), src.Fields());
        bool ok = await new CircuitRF.Ui.Views.ThreeD.GltfExportDialog(vm).ShowDialog<bool>(window);
        if (!ok || vm.Current is not { } result || vm.Written is not { } written) return;
        foreach (string note in result.Notes) Messages.Info(note, src.Path);
        Messages.Success($"Exported glTF: {result.Summary}", written);
    }

    /// <summary>The 3D menu's items again — when the geometry kernel's probe answers.</summary>
    internal void RefreshThreeDMenu() => RaiseThreeDMenuChanged();

    /// <summary>
    /// 3D editor bugs round 5 — raised once the 3D menu has actually been shown or hidden (its binding has already run),
    /// never on the fan-outs' many re-raises of an unchanged answer. The macOS shell repaints the menu bar on it: AppKit
    /// draws a top-level item's hidden flag late, so the 3D menu reached the bar about half a second after the menus
    /// beside it.
    /// </summary>
    public event Action? ThreeDMenuVisibilityChanged;

    private bool _threeDMenuShown;

    /// <summary>Re-evaluates every 3D menu item — called from both of the shell's enablement fan-outs.</summary>
    private void RaiseThreeDMenuChanged()
    {
        OnPropertyChanged(nameof(IsThreeDMenuVisible));
        if (IsThreeDMenuVisible != _threeDMenuShown)
        {
            _threeDMenuShown = !_threeDMenuShown;
            ThreeDMenuVisibilityChanged?.Invoke();
        }
        OnPropertyChanged(nameof(ThreeDBooleanAvailable));
        OnPropertyChanged(nameof(ThreeDBooleanTip));
        OnPropertyChanged(nameof(ThreeDFilletTip));
        OnPropertyChanged(nameof(ThreeDChamferTip));
        OnPropertyChanged(nameof(ThreeDImportStepAvailable));
        OnPropertyChanged(nameof(ThreeDImportStepTip));
        ImportStepCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ThreeDImportAnatomyAvailable));
        ImportVoxelModelCommand.NotifyCanExecuteChanged();
        ImportSurfaceMeshesCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ThreeDExportStepAvailable));
        OnPropertyChanged(nameof(ThreeDExportStepTip));
        ExportStepCommand.NotifyCanExecuteChanged();
        ExportGltfCommand.NotifyCanExecuteChanged();
        ExportDrawingCommand.NotifyCanExecuteChanged();
        ThreeDSelectModeCommand.NotifyCanExecuteChanged();
        ThreeDFitCommand.NotifyCanExecuteChanged();
        ThreeDStandardViewCommand.NotifyCanExecuteChanged();
        ThreeDPerspectiveCommand.NotifyCanExecuteChanged();
        ToggleThreeDRealisticCommand.NotifyCanExecuteChanged();
        ThreeDLookCommand.NotifyCanExecuteChanged();
        FollowRealisticPane();
        ThreeDClipPlaneCommand.NotifyCanExecuteChanged();
        ThreeDAxisIndicatorCommand.NotifyCanExecuteChanged();
        ThreeDScaleLegendCommand.NotifyCanExecuteChanged();
        ThreeDShowAllCommand.NotifyCanExecuteChanged();
        ThreeDSnapCommand.NotifyCanExecuteChanged();
        ThreeDDrawCommand.NotifyCanExecuteChanged();
        ThreeDAddPhantomCommand.NotifyCanExecuteChanged();
        ThreeDPlaceAgainstPhantomCommand.NotifyCanExecuteChanged();
        ThreeDBandSweepCommand.NotifyCanExecuteChanged();
        ThreeDInsertImageCommand.NotifyCanExecuteChanged();
        ThreeDTemperatureCommand.NotifyCanExecuteChanged();
        ThreeDDrawingPlaneCommand.NotifyCanExecuteChanged();
        ThreeDExtrudeCommand.NotifyCanExecuteChanged();
        ThreeDSelectAllCommand.NotifyCanExecuteChanged();
        ThreeDMeasureCommand.NotifyCanExecuteChanged();
        ThreeDMaterialsCommand.NotifyCanExecuteChanged();
        RunAnalysisCommand.NotifyCanExecuteChanged();
        MeshOnlyCommand.NotifyCanExecuteChanged();
        RaiseThreeDSelectionChanged();
    }

    // ── 3D menu cleanup (2026-09-27): what the selection can do ──────────────────────────────
    //
    // Each Modify / Boolean item is enabled exactly when the active editor's CanRunModify says its operation would act — the
    // predicates the canvas context menu is built from. A submenu is enabled when at least its reason to exist holds.

    /// <summary>3D ▸ Modify: something is selected (in the view, or a polyline or wire in the tree).</summary>
    public bool ThreeDModifyMenuEnabled => ActiveC3dEditor()?.HasModifySelection == true;

    /// <summary>Move Along, Rotate 90° and Mirror: an object or instance is selected (Object mode).</summary>
    public bool ThreeDTransformMenuEnabled => ActiveC3dEditor()?.CanRunModify("MoveX") == true;

    /// <summary>Align: two or more selected — the others line up with the last.</summary>
    public bool ThreeDAlignMenuEnabled => ActiveC3dEditor()?.CanRunModify("AlignXMin") == true;

    /// <summary>Order: an object (not an instance) is selected.</summary>
    public bool ThreeDOrderMenuEnabled => ActiveC3dEditor()?.CanRunModify("Front") == true;

    /// <summary>Face: one face of this document's own objects is selected (Face mode).</summary>
    public bool ThreeDFaceMenuEnabled => ActiveC3dEditor()?.CanRunModify("MeasureFace") == true;

    /// <summary>Edge: an edge is selected (Edge mode).</summary>
    public bool ThreeDEdgeMenuEnabled => ActiveC3dEditor()?.CanRunModify("TangentChain") == true;

    /// <summary>Vertex: one vertex is selected (Vertex mode).</summary>
    public bool ThreeDVertexMenuEnabled => ActiveC3dEditor()?.CanRunModify("MeasureFrom") == true;

    /// <summary>3D ▸ Boolean: the kernel, and an object selected in Object mode — Subtract… and the rest say why not.</summary>
    public bool ThreeDBooleanMenuEnabled
        => ThreeDBooleanAvailable && ActiveC3dEditor() is { } e && e.Viewer.SelectMode == Scene3DSelectMode.Object && e.Targets().Count > 0;

    /// <summary>brief-em3d-75 gate 2 — 3D ▸ View ▸ Temperature: enabled only with a current thermal result for the active
    /// setup; greyed, with the reason, when there is none or it is stale.</summary>
    public bool ThreeDTemperatureMenuEnabled => ActiveC3dEditor()?.PlotTemperatureRefusal() is null && ActiveC3dEditor() is not null;

    /// <summary>The Temperature submenu opens with any 3D editor: Clear, the Probe Table and Mirror work on a stale or absent result,
    /// and the items that plot say why they cannot (<see cref="ThreeDTemperatureTip"/>).</summary>
    public bool ThreeDTemperatureSubmenuEnabled => ActiveC3dEditor() is not null;

    public string ThreeDTemperatureTip => ActiveC3dEditor() is { } e
        ? e.PlotTemperatureRefusal() ?? "Temperature from the active thermal setup's run."
        : "Requires an active 3D editor.";

    /// <summary>brief-em3d-129 — 3D ▸ STEP ▸ Reload from Source: what it does, or why it cannot.</summary>
    public string ThreeDStepReloadTip => ActiveC3dEditor() is not { } e ? "Requires an active 3D editor."
        : e.Targets() is [{ Instance: false } t] && !C3dEditorViewModel.IsOperandIndex(t.Index)
            ? e.ReloadRefusal(e.StepAt(t.Index)) ?? "Read the source file again and move every part of it there, re-checking each face a port, boundary or fillet uses."
            : "Select one imported STEP object.";

    /// <summary>brief-em3d-129 R-em3d129-3a — 3D ▸ STEP ▸ Split into Solids: what it does, or which condition failed.</summary>
    public string ThreeDStepSplitTip => ActiveC3dEditor() is not { } e ? "Requires an active 3D editor."
        : e.SplitSelectionRefusal() ?? "One object per solid of this STEP part, gathered in a group named after it; every face reference moves to the piece whose face it is.";

    /// <summary>brief-em3d-130 — 3D ▸ STEP ▸ Replace with Prism / Replace with Box… / Convert to Polyhedron: what each does, or why
    /// it cannot.</summary>
    public string ThreeDStepPrismTip => ConvertTip(CircuitRF.Design.ThreeD.Step.StepConvertKind.Prism);
    public string ThreeDStepBoxTip => ConvertTip(CircuitRF.Design.ThreeD.Step.StepConvertKind.Box);
    public string ThreeDStepPolyhedronTip => ConvertTip(CircuitRF.Design.ThreeD.Step.StepConvertKind.Polyhedron);

    private string ConvertTip(CircuitRF.Design.ThreeD.Step.StepConvertKind kind) => ActiveC3dEditor() is not { } e ? "Requires an active 3D editor."
        : e.ConvertSelectionRefusal(kind) ?? C3dEditorViewModel.ConvertTip(kind);

    private static readonly string[] ThreeDSelectionProperties =
    [
        nameof(ThreeDModifyMenuEnabled), nameof(ThreeDTransformMenuEnabled), nameof(ThreeDAlignMenuEnabled),
        nameof(ThreeDOrderMenuEnabled), nameof(ThreeDFaceMenuEnabled), nameof(ThreeDEdgeMenuEnabled),
        nameof(ThreeDVertexMenuEnabled), nameof(ThreeDBooleanMenuEnabled),
        nameof(ThreeDTemperatureMenuEnabled), nameof(ThreeDTemperatureSubmenuEnabled), nameof(ThreeDTemperatureTip),
        nameof(ThreeDStepReloadTip), nameof(ThreeDStepSplitTip),
        nameof(ThreeDStepPrismTip), nameof(ThreeDStepBoxTip), nameof(ThreeDStepPolyhedronTip),
    ];

    /// <summary>Re-asks every item that depends on the active editor's selection.</summary>
    private void RaiseThreeDSelectionChanged()
    {
        foreach (string p in ThreeDSelectionProperties) OnPropertyChanged(p);
        ThreeDModifyCommand.NotifyCanExecuteChanged();
        ThreeDExtrudeCommand.NotifyCanExecuteChanged();
        ThreeDHideShowSelectionCommand.NotifyCanExecuteChanged();
    }

    // brief-em3d-45 — drawing needs the editor, not the read-only viewer.
    private C3dEditorViewModel? ActiveC3dEditor() => ResolveActiveDocumentForCommands() is C3dEditorDocument e ? e.ViewModel : null;

    private bool HasActiveC3dEditor() => ActiveC3dEditor() is not null;

    /// <summary>3D ▸ Draw ▸ Box … Cylinder, Sphere — the same arming the toolbar and the Shift+A popup do.</summary>
    [RelayCommand(CanExecute = nameof(HasActiveC3dEditor))]
    private void ThreeDDraw(string kind)
    {
        if (ActiveC3dEditor() is { } e && Enum.TryParse<CircuitRF.Ui.ThreeD.Tools.C3dToolKind>(kind, out var k)) e.Arm(k);
    }

    /// <summary>brief-sar-3 R-sar3-2 — 3D ▸ Draw ▸ Phantom ▸ …: a layered phantom of that shape, one undo entry.</summary>
    [RelayCommand(CanExecute = nameof(HasActiveC3dEditor))]
    private void ThreeDAddPhantom(string shape)
    {
        if (ActiveC3dEditor() is { } e && Enum.TryParse<CircuitRF.Design.ThreeD.C3dPhantomShape>(shape, out var s)) e.AddPhantom(s);
    }

    /// <summary>brief-sar-12 R-sar12-2 — 3D ▸ Draw ▸ Phantom ▸ Place Against Phantom…, on the selection.</summary>
    [RelayCommand(CanExecute = nameof(HasActiveC3dEditor))]
    private Task ThreeDPlaceAgainstPhantom() => ActiveC3dEditor()?.OpenPlacementAsync() ?? Task.CompletedTask;

    /// <summary>brief-sar-12 R-sar12-3 — 3D ▸ Draw ▸ Phantom ▸ Band Sweep….</summary>
    [RelayCommand(CanExecute = nameof(HasActiveC3dEditor))]
    private Task ThreeDBandSweep() => ActiveC3dEditor()?.OpenBandSweepAsync() ?? Task.CompletedTask;

    /// <summary>brief-em3d-101 R-em3d101-2b — 3D ▸ Draw ▸ Image…: the same picker and the same placement as the toolbar button.</summary>
    [RelayCommand(CanExecute = nameof(HasActiveC3dEditor))]
    private async Task ThreeDInsertImage()
    {
        if (ActiveC3dEditor() is not { } e) return;
        if (e.InsertImageRefusal() is { } why) { Messages.Error(why); return; }
        var owner = ResolveActiveDocumentForCommands() is C3dEditorDocument d ? HostWindowOf(d) : null;
        var files = await CircuitRF.Ui.Views.ThreeD.ImageFilePicker.PickAsync(owner ?? ResolveOwner(null), "Insert Image", multiple: true);
        if (files.Count > 0) e.InsertImages(files);
    }

    /// <summary>brief-em3d-75 — 3D ▸ View ▸ Temperature's items and the thermal places' visibility switches, by name
    /// (C3dEditorViewModel.RunTemperature).</summary>
    [RelayCommand(CanExecute = nameof(HasActiveC3dEditor))]
    private void ThreeDTemperature(string which) => ActiveC3dEditor()?.RunTemperature(which);

    /// <summary>3D ▸ Drawing Plane ▸ XY / YZ / XZ (keeping the offset), and Show Grid.</summary>
    [RelayCommand(CanExecute = nameof(HasActiveC3dEditor))]
    private void ThreeDDrawingPlane(string which)
    {
        if (ActiveC3dEditor() is not { } e) return;
        if (which == "Grid") { e.ShowDrawingGrid = !e.ShowDrawingGrid; return; }
        if (Enum.TryParse<CircuitRF.Design.ThreeD.C3dPlane>(which, out var p)) e.SetPlane(e.Plane with { Plane = p });
    }

    /// <summary>3D ▸ Modify ▸ Extrude — enabled on a sheet (or a tree-selected polyline) it can extrude.</summary>
    [RelayCommand(CanExecute = nameof(CanThreeDExtrude))]
    private void ThreeDExtrude() => ActiveC3dEditor()?.Extrude();

    private bool CanThreeDExtrude() => ActiveC3dEditor()?.CanExtrude == true;

    /// <summary>brief-em3d-46 — 3D ▸ Modify's object operations, by name: the same functions the context menu and the
    /// keys call (C3dEditorViewModel.RunModify). 3D menu cleanup — each enabled only when it can act on the selection.</summary>
    [RelayCommand(CanExecute = nameof(CanThreeDModify))]
    private void ThreeDModify(string which) => ActiveC3dEditor()?.RunModify(which);

    private bool CanThreeDModify(string? which) => ActiveC3dEditor()?.CanRunModify(which) == true;

    /// <summary>3D menu cleanup — 3D ▸ Select All Objects: every shown object, in the editor and the read-only viewer alike
    /// (Ctrl/Cmd+A on the pane is the same call).</summary>
    [RelayCommand(CanExecute = nameof(HasActive3DPane))]
    private void ThreeDSelectAll() => Active3DPane()?.SelectAllObjects();

    /// <summary>brief-em3d-91 — 3D ▸ Hide / Show Selection: the editor's H (C3dEditorViewModel.HideOrShowSelection), enabled
    /// while something is selected.</summary>
    [RelayCommand(CanExecute = nameof(CanThreeDHideShowSelection))]
    private void ThreeDHideShowSelection() => ActiveC3dEditor()?.HideOrShowSelection();

    private bool CanThreeDHideShowSelection() => ActiveC3dEditor()?.SelectionVisibility is { } v && v != C3dSelectionVisibility.None;

    /// <summary>brief-em3d-46 R-em3d46-6 — 3D ▸ Measure, in the editor and the read-only viewer alike.</summary>
    [RelayCommand(CanExecute = nameof(HasActive3DPane))]
    private void ThreeDMeasure() => Active3DPane()?.ToggleMeasure();

    /// <summary>3D ▸ Select Mode ▸ Object / Face / Edge / Vertex.</summary>
    [RelayCommand(CanExecute = nameof(HasActive3DPane))]
    private void ThreeDSelectMode(string mode)
    {
        if (Active3DPane() is { } p && Enum.TryParse<Scene3DSelectMode>(mode, out var m)) p.SelectMode = m;
    }

    [RelayCommand(CanExecute = nameof(HasActive3DPane))]
    private void ThreeDFit() => Active3DPane()?.FitCommand.Execute(null);

    [RelayCommand(CanExecute = nameof(HasActive3DPane))]
    private void ThreeDStandardView(StandardView3D view) => Active3DPane()?.StandardViewCommand.Execute(view);

    [RelayCommand(CanExecute = nameof(HasActive3DPane))]
    private void ThreeDPerspective() { if (Active3DPane() is { } p) p.IsPerspective = !p.IsPerspective; }

    [RelayCommand(CanExecute = nameof(HasActive3DPane))]
    private void ThreeDClipPlane() { if (Active3DPane() is { } p) p.ClipEnabled = !p.ClipEnabled; }

    /// <summary>brief-em3d-106 R-em3d106-1c — 3D ▸ View ▸ Realistic View: the toolbar's toggle.</summary>
    [RelayCommand(CanExecute = nameof(HasActive3DPane))]
    private void ToggleThreeDRealistic() { if (Active3DPane() is { } p) p.IsRealistic = !p.IsRealistic; }

    /// <summary>brief-em3d-108 R-em3d108-3a — 3D ▸ View ▸ Look…: the active 3D design's Look panel, the flyout beside the Realistic
    /// toggle. Opening it turns the realistic view on.</summary>
    [RelayCommand(CanExecute = nameof(HasActiveC3dEditor))]
    private void ThreeDLook() => ActiveC3dEditor()?.RequestLookPanel();

    /// <summary>Whether the active 3D view is realistic — both menus' check mark. Followed through the pane's own property, so the
    /// toolbar's toggle moves the mark too.</summary>
    public bool ThreeDRealistic => Active3DPane()?.IsRealistic == true;

    private Viewer3DViewModel? _realisticPane;

    /// <summary>Follows the active pane's realistic flag (re-pointed whenever the active document changes).</summary>
    private void FollowRealisticPane()
    {
        var pane = Active3DPane();
        if (!ReferenceEquals(pane, _realisticPane))
        {
            if (_realisticPane is not null) _realisticPane.PropertyChanged -= OnRealisticPaneChanged;
            _realisticPane = pane;
            if (pane is not null) pane.PropertyChanged += OnRealisticPaneChanged;
        }
        OnPropertyChanged(nameof(ThreeDRealistic));
    }

    private void OnRealisticPaneChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Viewer3DViewModel.IsRealistic)) OnPropertyChanged(nameof(ThreeDRealistic));
    }

    [RelayCommand(CanExecute = nameof(HasActive3DPane))]
    private void ThreeDAxisIndicator() { if (Active3DPane() is { } p) p.ShowAxisIndicator = !p.ShowAxisIndicator; }

    /// <summary>3D editor bugs round 6 — 3D ▸ View ▸ Scale Legend: the toolbar's toggle for the scale bar.</summary>
    [RelayCommand(CanExecute = nameof(HasActive3DPane))]
    private void ThreeDScaleLegend() { if (Active3DPane() is { } p) p.ShowScaleLegend = !p.ShowScaleLegend; }

    [RelayCommand(CanExecute = nameof(HasActive3DPane))]
    private void ThreeDShowAll() => Active3DPane()?.ShowAll();

    /// <summary>brief-em3d-44 R-em3d44-5 — 3D ▸ Snap: <c>All</c> is the master switch, any other a kind's toggle —
    /// the same properties the editor's toolbar toggles bind to.</summary>
    [RelayCommand(CanExecute = nameof(HasActive3DPane))]
    private void ThreeDSnap(string kind)
    {
        if (Active3DPane() is not { } p) return;
        switch (kind)
        {
            case "All":        p.SnapEnabled = !p.SnapEnabled; break;
            case "Vertex":     p.SnapVertex = !p.SnapVertex; break;
            case "Midpoint":   p.SnapMidpoint = !p.SnapMidpoint; break;
            case "Edge":       p.SnapEdge = !p.SnapEdge; break;
            case "FaceCentre": p.SnapFaceCentre = !p.SnapFaceCentre; break;
            case "Grid":       p.SnapGridOn = !p.SnapGridOn; break;
        }
    }

    /// <summary>A workspace switch drops the dock tree without closing documents one by one.</summary>
    private void ReleaseC3dEditorsOfOutgoingWorkspace()
    {
        foreach (var doc in _openDocsByPath.Values.OfType<C3dEditorDocument>().ToList()) ClosedC3dEditor(doc);
        _drawingPlanes = null;
    }
}

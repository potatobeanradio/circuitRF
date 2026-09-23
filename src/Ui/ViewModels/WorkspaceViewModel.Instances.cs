using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.Dock;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace CircuitRF.Ui.ViewModels;

// ─────────────────────────────────────────────────────────────────────────────
//  The Instances panel and Design ▸ Find Instance… (brief-find-instance-panel.md).
//
//  The panel lists what the FOCUSED document's canvas shows — the active frame of a schematic or a
//  layout, so a push-in re-points it — and nothing for any other kind of document. With "Include
//  sub-cells" on it lists the tab from its TOP frame down instead. The list lives in
//  InstanceListViewModel; this file only decides which document that is, hands the sub-cell walk a
//  copy of every open session, puts the panel on screen for the command, and answers a double-click
//  by bringing the ROW'S document forward — pushing down to the row's cell first when it is in one.
// ─────────────────────────────────────────────────────────────────────────────

public partial class WorkspaceViewModel
{
    // The tool whose double-click this view model answers. A fresh layout (a workspace switch) builds
    // a new tool, so the subscription is checked on every route rather than made once.
    private InstancesTool? _wiredInstancesTool;

    // The document whose push-in / pop-out re-points the panel.
    private IDockable? _instancesFrameDoc;

    /// <summary>
    /// Points the Instances panel at <paramref name="document"/>'s active frame, or empties it for
    /// anything that is not a schematic or a layout — one document's list beside another is worse than
    /// none (R-fi-3). Cheap when nothing changed, which the list itself checks.
    /// </summary>
    private void RouteInstancesPanel(IDockable? document)
    {
        WireInstancesTool();
        FollowInstancesFrames(document);

        switch (document)
        {
            case SchematicDocument sd:
                _factory.InstancesTool?.SetActiveDocument(sd.ActiveViewModel, InstancesHeaderOf(sd),
                                                          sd.NavFrames[0].Session, InstancesRootHeaderOf(sd));
                break;
            case LayoutDocument ld:
                _factory.InstancesTool?.SetActiveDocument(ld.ActiveViewModel, InstancesHeaderOf(ld),
                                                          ld.NavFrames[0].Session, InstancesRootHeaderOf(ld));
                break;
            default:
                _factory.InstancesTool?.SetActiveDocument(null, null);
                break;
        }
    }

    /// <summary>The file the listed frame came from — <c>Amp.csch</c>, or the sub-cell's own file
    /// after a push-in, since that is what is being listed.</summary>
    private static string InstancesHeaderOf(SchematicDocument sd)
    {
        if (sd.ActiveViewModel.DocumentName is { Length: > 0 } name) return name;
        if (sd.NavDepth == 0 && sd.FilePath is { Length: > 0 } path) return Path.GetFileName(path);
        return sd.NavFrames[^1].Label;
    }

    private static string InstancesHeaderOf(LayoutDocument ld)
        => ld.ActiveViewModel.CurrentLayoutPath is { Length: > 0 } path
            ? Path.GetFileName(path)
            : ld.NavFrames[^1].Label;

    /// <summary>The tab's own file — what "Include sub-cells" lists from, wherever the canvas is.</summary>
    private static string InstancesRootHeaderOf(SchematicDocument sd)
    {
        if (sd.NavFrames[0].Session.DocumentName is { Length: > 0 } name) return name;
        if (sd.FilePath is { Length: > 0 } path) return Path.GetFileName(path);
        return sd.NavFrames[0].Label;
    }

    private static string InstancesRootHeaderOf(LayoutDocument ld)
        => ld.NavFrames[0].Session.CurrentLayoutPath is { Length: > 0 } path
            ? Path.GetFileName(path)
            : ld.NavFrames[0].Label;

    private void WireInstancesTool()
    {
        var tool = _factory.InstancesTool;
        if (ReferenceEquals(tool, _wiredInstancesTool)) return;

        if (_wiredInstancesTool is not null)
        {
            _wiredInstancesTool.ListVm.InstanceActivated -= OnInstanceActivated;
            _wiredInstancesTool.ListVm.LiveLevels = null;
        }
        _wiredInstancesTool = tool;
        if (tool is not null)
        {
            tool.ListVm.InstanceActivated += OnInstanceActivated;
            tool.ListVm.LiveLevels = SnapshotOpenCellLevels;
        }
    }

    /// <summary>
    /// A copy of every open schematic and layout session's placements, by absolute path — taken on the
    /// UI thread when a sub-cell walk starts, because the walk runs on another and a live model is the
    /// UI thread's alone. An unsaved edit in an open cell is therefore what the search finds, and a
    /// cell nobody has open is read from disk. Names and references only; nothing touches the disk here.
    /// </summary>
    private IReadOnlyDictionary<string, InstanceHierarchyWalk.Level> SnapshotOpenCellLevels()
    {
        var levels = new Dictionary<string, InstanceHierarchyWalk.Level>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in _registry.AllPaths.ToList())
            if (_registry.TryGet(path, out var vm) && vm is not null)
                levels[path] = InstanceHierarchyWalk.Of(vm.EditModel);

        foreach (var path in _layoutRegistry.AllPaths.ToList())
            if (_layoutRegistry.TryGet(path, out var vm) && vm is not null)
                levels[path] = InstanceHierarchyWalk.Of(vm.Model, Path.GetDirectoryName(path));

        return levels;
    }

    // A push-in swaps which view model the canvas shows without the document ever leaving the tab
    // strip, so nothing else re-routes the panel — the Properties panel's 2026-08-25 bug, avoided here.
    private void FollowInstancesFrames(IDockable? document)
    {
        if (ReferenceEquals(document, _instancesFrameDoc)) return;

        switch (_instancesFrameDoc)
        {
            case SchematicDocument sd: sd.ActiveViewModelChanged -= OnInstancesFrameChanged; break;
            case LayoutDocument ld:    ld.ActiveViewModelChanged -= OnInstancesFrameChanged; break;
        }

        _instancesFrameDoc = document as SchematicDocument ?? (IDockable?)(document as LayoutDocument);

        switch (_instancesFrameDoc)
        {
            case SchematicDocument sd: sd.ActiveViewModelChanged += OnInstancesFrameChanged; break;
            case LayoutDocument ld:    ld.ActiveViewModelChanged += OnInstancesFrameChanged; break;
        }
    }

    private void OnInstancesFrameChanged(object? sender, EventArgs e) => RouteInstancesPanel(_instancesFrameDoc);

    // ── Design ▸ Find Instance… (Ctrl/⌘+F) ───────────────────────────────────

    /// <summary>
    /// Shows the Instances panel for the focused document — docking it if it is closed, flying it out
    /// if it is auto-hidden, bringing its tab to the front if it is behind another — and puts the caret
    /// in its search box with the last search selected, so typing replaces it (R-fi-12..14).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFindInstance))]
    private void FindInstance()
    {
        // The command's own target, which is also right for a document in a torn-off window.
        RouteInstancesPanel(ResolveActiveDocumentForCommands());
        ShowInstancesPanel();
        _factory.InstancesTool?.RequestSearchFocus();
    }

    /// <summary>A schematic or a layout, and greyed out for everything else — never hidden (R-fi-13).</summary>
    private bool CanFindInstance() => CanFindInstanceIn(ResolveActiveDocumentForCommands());

    /// <summary><see cref="CanFindInstance"/>'s rule on its own — the same answer as
    /// <c>IsSchematicDocumentActive() || IsLayoutDocumentActive()</c>, testable without a shell.</summary>
    internal static bool CanFindInstanceIn(IDockable? focused) => focused is SchematicDocument or LayoutDocument;

    private void ShowInstancesPanel()
    {
        if (_factory.InstancesTool is not { } tool) return;

        try
        {
            // On screen, behind a tab, or collapsed to a strip: ShowToolPanel's own answers.
            if (_factory.IsToolAutoHidden(tool) || _factory.TryFindTool(tool, out _, out _))
            {
                ShowToolPanelCore(DockPanelIds.Instances);
                return;
            }

            // Closed: back where it was, if anywhere is remembered; otherwise DOCKED beside a panel of
            // its kind rather than floated over the canvas the user is about to search.
            if (RestorePanelToItsHome(DockPanelIds.Instances, tool)) return;
            if (DockInstancesPanelBesideSibling(tool)) return;

            ShowToolPanelCore(DockPanelIds.Instances);
        }
        finally
        {
            RaiseToolPanelVisibilityChanged();
        }
    }

    /// <summary>
    /// Tabs the panel in front of the Analyses (or failing that the Properties, or the Project) panel
    /// in the shell. An insert into a live dock, never a layout rebuild — a rebuild re-realises every
    /// open canvas, and a keystroke must not cost that.
    /// </summary>
    private bool DockInstancesPanelBesideSibling(InstancesTool tool)
    {
        foreach (var sibling in new ITool?[] { _factory.AnalysesTool, _factory.PropertiesTool, _factory.ProjectTreeTool })
        {
            if (sibling is null) continue;
            if (!_factory.TryFindTool(sibling, out var parent, out var window) || parent is null || window is not null)
                continue;

            try
            {
                _factory.InsertDockable(parent, tool, parent.VisibleDockables?.Count ?? 0);
                parent.ActiveDockable = tool;
                _factory.SetActiveDockable(tool);
                ShellWindow()?.Activate();
                return true;
            }
            catch (Exception ex)
            {
                Messages.Warning($"Could not dock the {tool.Title} panel: {ex.Message}");
                return false;
            }
        }

        return false;
    }

    // ── Double-click / Enter on a row ─────────────────────────────────────────

    /// <summary>
    /// Brings the ROW'S document forward — never "the active document": the user may have clicked
    /// another tab since the list was built — and then selects and frames the instance there. A row
    /// listed from the tab's top frame while the canvas is pushed in pops back up to it; a row inside a
    /// placed cell pushes down to that cell first (<see cref="DescendTo(SchematicDocument, int, SubCellInstance)"/>).
    /// </summary>
    private void OnInstanceActivated(InstanceRow row)
    {
        switch (row.SourceViewModel, row.Source)
        {
            case (SchematicViewModel vm, EditableComponent comp):
                if (FindSchematicDocumentShowing(vm) is { } sd)
                    RevealAfterActivating(sd, () => InstanceReveal.Reveal(sd, comp));
                else if (FindSchematicDocumentWithFrame(vm, out var root, out int k))
                    RevealAfterActivating(root, () =>
                    {
                        PopToLevel(root, k);
                        InstanceReveal.Reveal(root, comp);
                    });
                break;

            case (LayoutEditorViewModel vm, LayoutInstance inst):
                if (FindLayoutDocumentShowing(vm) is { } ld)
                    RevealAfterActivating(ld, () => InstanceReveal.Reveal(vm, inst));
                else if (FindLayoutDocumentWithFrame(vm, out var root, out int k))
                    RevealAfterActivating(root, () =>
                    {
                        PopToLevel(root, k);
                        InstanceReveal.Reveal(vm, inst);
                    });
                break;

            case (SchematicViewModel vm, SubCellInstance path):
                if (FindSchematicDocumentWithFrame(vm, out var sdoc, out int sk))
                    RevealAfterActivating(sdoc, () => DescendTo(sdoc, sk, path));
                break;

            case (LayoutEditorViewModel vm, SubCellInstance path):
                if (FindLayoutDocumentWithFrame(vm, out var ldoc, out int lk))
                    RevealAfterActivating(ldoc, () => DescendTo(ldoc, lk, path));
                break;
        }
    }

    // ── Down to a sub-cell row ────────────────────────────────────────────────
    //
    // The walk read those cells off the UI thread, so the row carries NAMES; each is found again in the
    // session a push-in shows. Frames already on the path are kept — stepping through the results in one
    // sub-cell must not pop out and push back in on every row — and the reveal waits a dispatcher pass,
    // because the canvas re-points at the pushed frame on its own schedule and a zoom asked of the old
    // frame would be lost.

    /// <summary>Pushes <paramref name="doc"/> from frame <paramref name="frameIndex"/> down
    /// <paramref name="path"/>'s chain, then selects and frames its instance.</summary>
    private void DescendTo(SchematicDocument doc, int frameIndex, SubCellInstance path)
    {
        int kept = 0;
        while (kept < path.Chain.Count && frameIndex + 1 + kept <= doc.NavDepth
               && doc.NavFrames[frameIndex + 1 + kept].Label == path.Chain[kept].Name)
            kept++;
        if (doc.NavDepth > frameIndex + kept) PopToLevel(doc, frameIndex + kept);

        for (int i = kept; i < path.Chain.Count; i++)
        {
            var parent = doc.ActiveViewModel.EditModel;
            var comp   = FindStep(parent.Components, path.Chain[i], InstanceHierarchyWalk.NameOf);
            if (comp is null || !HierarchyResolver.CanPushInto(comp, parent, out _))
            {
                SubCellRowGone(path);
                return;
            }
            PushIntoCell(doc, comp);
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (FindStep(doc.ActiveViewModel.EditModel.Components, path.Leaf, InstanceHierarchyWalk.NameOf) is { } leaf)
                InstanceReveal.Reveal(doc, leaf);
            else
                SubCellRowGone(path);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <inheritdoc cref="DescendTo(SchematicDocument, int, SubCellInstance)"/>
    private void DescendTo(LayoutDocument doc, int frameIndex, SubCellInstance path)
    {
        // A layout frame's label is its cell, not the placement — the descent chain names those, and it
        // is only trusted when every frame recorded one.
        int kept = 0;
        if (doc.DescentChainIsComplete)
        {
            var descent = doc.DescentChain;
            while (kept < path.Chain.Count && frameIndex + kept < descent.Count
                   && InstanceHierarchyWalk.NameOf(descent[frameIndex + kept].Instance) == path.Chain[kept].Name)
                kept++;
        }
        if (doc.NavDepth > frameIndex + kept) PopToLevel(doc, frameIndex + kept);

        for (int i = kept; i < path.Chain.Count; i++)
        {
            var parent = doc.ActiveViewModel;
            var inst   = FindStep(parent.Model.Instances, path.Chain[i], InstanceHierarchyWalk.NameOf);
            if (inst is null || !CanPushInto(inst, parent, out _))
            {
                SubCellRowGone(path);
                return;
            }
            PushIntoCell(doc, inst);
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var vm = doc.ActiveViewModel;
            if (FindStep(vm.Model.Instances, path.Leaf, InstanceHierarchyWalk.NameOf) is { } leaf)
                InstanceReveal.Reveal(vm, leaf);
            else
                SubCellRowGone(path);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>The placement a step names: the one at its index when the name still matches there,
    /// else the first of that name — an insert above it shifts the index, not the name.</summary>
    internal static T? FindStep<T>(IReadOnlyList<T> items, InstancePathStep step, Func<T, string> nameOf) where T : class
    {
        if (step.Index >= 0 && step.Index < items.Count && nameOf(items[step.Index]) == step.Name)
            return items[step.Index];
        foreach (var item in items)
            if (nameOf(item) == step.Name) return item;
        return null;
    }

    /// <summary>The row was out of date — said once, and the list rebuilt so it stops offering it.</summary>
    private void SubCellRowGone(SubCellInstance path)
    {
        Messages.Info($"'{path.DottedPath}' is no longer there — the Instances list has been refreshed.");
        _factory.InstancesTool?.ListVm.Refresh();
    }

    /// <summary>
    /// Runs <paramref name="reveal"/> against <paramref name="document"/>, activating it first when it
    /// is not the focused one. An activated tab's view may bind and lay out only on the next pass, and
    /// a zoom asked of a canvas with no size does nothing — so the reveal waits for that pass. A
    /// document already in front is revealed at once and keyboard focus stays in the list, so Enter
    /// can walk it.
    /// </summary>
    private void RevealAfterActivating(IDockable document, Action reveal)
    {
        if (ReferenceEquals(ResolveActiveDocumentForCommands(), document))
        {
            reveal();
            return;
        }

        ActivateOpenDocument(document);
        Avalonia.Threading.Dispatcher.UIThread.Post(reveal, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Escape in the panel's search box (R-fi-15): the keyboard goes back to the canvas of the document
    /// being listed, bringing its window forward if it is a torn-off one.
    /// </summary>
    internal void ReturnFocusFromInstancesPanel()
    {
        if ((_instancesFrameDoc ?? ResolveActiveDocumentForCommands()) is { } document)
            BringDockableWindowToFront(document);
    }

    private SchematicDocument? FindSchematicDocumentShowing(SchematicViewModel vm)
        => _openDocsByPath.Values.OfType<SchematicDocument>().Concat(_scratchDocs)
                          .FirstOrDefault(d => ReferenceEquals(d.ActiveViewModel, vm));

    private LayoutDocument? FindLayoutDocumentShowing(LayoutEditorViewModel vm)
        => _openDocsByPath.Values.OfType<LayoutDocument>().Concat(_scratchLayouts)
                          .FirstOrDefault(d => ReferenceEquals(d.ActiveViewModel, vm));

    /// <summary>The open tab with <paramref name="vm"/> anywhere in its frame stack, and where — the
    /// frame a row listed from the tab's top keeps pointing at while the canvas is pushed in.</summary>
    private bool FindSchematicDocumentWithFrame(SchematicViewModel vm, out SchematicDocument doc, out int frameIndex)
    {
        foreach (var d in _openDocsByPath.Values.OfType<SchematicDocument>().Concat(_scratchDocs))
            for (int i = 0; i < d.NavFrames.Count; i++)
                if (ReferenceEquals(d.NavFrames[i].Session, vm)) { doc = d; frameIndex = i; return true; }

        doc = null!;
        frameIndex = -1;
        return false;
    }

    /// <inheritdoc cref="FindSchematicDocumentWithFrame"/>
    private bool FindLayoutDocumentWithFrame(LayoutEditorViewModel vm, out LayoutDocument doc, out int frameIndex)
    {
        foreach (var d in _openDocsByPath.Values.OfType<LayoutDocument>().Concat(_scratchLayouts))
            for (int i = 0; i < d.NavFrames.Count; i++)
                if (ReferenceEquals(d.NavFrames[i].Session, vm)) { doc = d; frameIndex = i; return true; }

        doc = null!;
        frameIndex = -1;
        return false;
    }
}

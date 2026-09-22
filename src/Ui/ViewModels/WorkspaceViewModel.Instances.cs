using System;
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
//  layout, so a push-in re-points it — and nothing for any other kind of document. The list lives in
//  InstanceListViewModel; this file only decides which document that is, puts the panel on screen
//  for the command, and answers a double-click by bringing the ROW'S document forward.
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
                _factory.InstancesTool?.SetActiveDocument(sd.ActiveViewModel, InstancesHeaderOf(sd));
                break;
            case LayoutDocument ld:
                _factory.InstancesTool?.SetActiveDocument(ld.ActiveViewModel, InstancesHeaderOf(ld));
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

    private void WireInstancesTool()
    {
        var tool = _factory.InstancesTool;
        if (ReferenceEquals(tool, _wiredInstancesTool)) return;

        if (_wiredInstancesTool is not null) _wiredInstancesTool.ListVm.InstanceActivated -= OnInstanceActivated;
        _wiredInstancesTool = tool;
        if (tool is not null) tool.ListVm.InstanceActivated += OnInstanceActivated;
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
    /// another tab since the list was built — and then selects and frames the instance there.
    /// </summary>
    private void OnInstanceActivated(InstanceRow row)
    {
        switch (row.SourceViewModel, row.Source)
        {
            case (SchematicViewModel vm, EditableComponent comp):
                if (FindSchematicDocumentShowing(vm) is { } sd)
                    RevealAfterActivating(sd, () => InstanceReveal.Reveal(sd, comp));
                break;

            case (LayoutEditorViewModel vm, LayoutInstance inst):
                if (FindLayoutDocumentShowing(vm) is { } ld)
                    RevealAfterActivating(ld, () => InstanceReveal.Reveal(vm, inst));
                break;
        }
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
}

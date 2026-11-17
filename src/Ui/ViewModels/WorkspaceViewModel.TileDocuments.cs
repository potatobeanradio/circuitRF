using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Window ▸ Tile Side by Side / Tile Stacked / Tile Grid — arranging the DOCKED document area into
/// panes in one step, so a schematic and the display of its results can be seen together — and
/// Window ▸ Merge All Documents, which undoes any of them and also gathers every torn-off document.
///
/// <para><b>The three Tile commands arrange docked documents only.</b> A torn-off document is in a
/// window of its own that the user placed deliberately, and the operating system already arranges
/// top-level windows (snap on Windows, tiling on macOS and most Linux desktops); resizing those would
/// be a second window manager fighting the first. <b>Merge All Documents is the exception on purpose</b>:
/// it is the clean-up for a scattered arrangement, so it brings every torn-off DOCUMENT back as a tab
/// of the main strip and closes the windows that leaves empty. Floating TOOL panels stay where they
/// are — they are not documents, and Reset Layout is their clean-up.</para>
///
/// <para>Where each document goes is <see cref="DocumentTiling"/>'s decision — kind first (schematic
/// left, results right), then cell, with a Data Display counted as part of the cell whose results it
/// plots. See that class for the pane limits.</para>
/// </summary>
public partial class WorkspaceViewModel
{
    /// <summary>True when the docked area holds at least two documents — one has nothing to tile with.</summary>
    public bool CanTileDocuments => DockedDocumentCount() >= 2;

    /// <summary>True when the docked area is split, or any document is in a window of its own.</summary>
    public bool CanMergeAllDocuments => EnumerateDocumentPanes().Count() > 1 || FloatingTileCandidates().Count > 0;

    [RelayCommand] private void TileSideBySide() => TileDocuments(TileArrangement.SideBySide);
    [RelayCommand] private void TileStacked()    => TileDocuments(TileArrangement.Stacked);
    [RelayCommand] private void TileGrid()       => TileDocuments(TileArrangement.Grid);
    [RelayCommand] private void MergeAllDocuments() => TileDocuments(TileArrangement.MergeAll);

    /// <summary>
    /// Arranges every docked document per <paramref name="arrangement"/>. Returns false when there was
    /// nothing to arrange or the arrangement could not be applied (the tree is then unchanged, and the
    /// reason is in Messages).
    /// </summary>
    public bool TileDocuments(TileArrangement arrangement)
    {
        if (Layout is not IRootDock root) return false;

        bool merge = arrangement == TileArrangement.MergeAll;
        var candidates = merge ? [.. DockedTileCandidates(), .. FloatingTileCandidates()] : DockedTileCandidates();
        if (!merge && candidates.Count < 2) return false;
        if (merge && !CanMergeAllDocuments) return false;

        // A torn-off document the user was working in keeps the focus when Merge brings it home.
        var focus = ResolveActiveDocumentForCommands();
        if (focus is not null && !candidates.Any(c => ReferenceEquals(c.Document, focus))) focus = null;

        if (DocumentTiling.Plan(candidates, arrangement, focus) is not { } plan) return false;

        try
        {
            if (!_factory.ArrangeDocuments(root, plan))
            {
                Messages.Warning("The documents could not be tiled: the main document area is not docked in this window.");
                return false;
            }
        }
        catch (Exception ex)
        {
            Messages.Warning($"The documents could not be tiled: {ex.Message}");
            return false;
        }

        SubscribeToDocumentPanes();

        if (focus is not null && focus.Owner is IDock pane)
        {
            _factory.SetActiveDockable(focus);
            _factory.SetFocusedDockable(pane, focus);
            MarkActiveDocumentPane(focus);
        }

        // With the dockers hidden, Show Dockers re-applies the arrangement captured when they were
        // hidden — which would quietly put the documents back the way they were before this tile.
        if (DockersCollapsed && _preCollapseLayout is { } pre && CaptureDockLayout() is { } live)
        {
            pre.DocumentRegion = live.DocumentRegion;
            pre.DocumentOrder  = live.DocumentOrder;
            pre.ActiveDocument = live.ActiveDocument;
        }

        OnDockArrangementChanged();
        return true;
    }

    /// <summary>Every document in this workspace's floating windows, window by window.</summary>
    internal IReadOnlyList<TileCandidate> FloatingTileCandidates()
    {
        var list = new List<TileCandidate>();
        if (Layout is not IRootDock { Windows: { } windows }) return list;
        foreach (var w in windows)
        {
            if (w?.Layout is not { } wl) continue;
            foreach (var pane in Docking.DockLayoutCapture.EnumerateDocumentPanes(wl))
                foreach (var d in pane.VisibleDockables ?? Enumerable.Empty<IDockable>())
                    if (IsTileable(d))
                        list.Add(new TileCandidate(d!, TileRoleOf(d!), TileCellOf(d!), false));
        }
        return list;
    }

    private int DockedDocumentCount() =>
        EnumerateDocumentPanes().Sum(p => p.VisibleDockables?.Count(IsTileable) ?? 0);

    private static bool IsTileable(IDockable? d) => d is IDocument and not ITool;

    /// <summary>Every docked document, pane by pane in tree order and tab order within a pane.</summary>
    internal IReadOnlyList<TileCandidate> DockedTileCandidates()
    {
        var list = new List<TileCandidate>();
        foreach (var pane in EnumerateDocumentPanes())
            foreach (var d in pane.VisibleDockables ?? Enumerable.Empty<IDockable>())
                if (IsTileable(d))
                    list.Add(new TileCandidate(d!, TileRoleOf(d!), TileCellOf(d!), ReferenceEquals(pane.ActiveDockable, d)));
        return list;
    }

    internal static TileRole TileRoleOf(IDockable d) => d switch
    {
        SchematicDocument            => TileRole.Schematic,
        SymbolEditorDocument         => TileRole.Symbol,
        CellParameterEditorDocument  => TileRole.CellParameters,
        LayoutDocument               => TileRole.Layout,
        ThreeD.C3dEditorDocument     => TileRole.ThreeD,
        TechDocument                 => TileRole.Technology,
        DataDisplayDocument          => TileRole.DataDisplay,
        _                            => TileRole.Other,
    };

    /// <summary>
    /// The cell a document belongs to, by NAME — the same name a run files its results under
    /// (<c>ResultsWriter.SchematicKey</c>: <c>&lt;cell&gt;</c> or <c>&lt;cell&gt;.&lt;view&gt;</c>), which
    /// is what lets a Data Display be matched to the schematic it plots. A view inside a cell folder
    /// (<c>&lt;cell&gt;/schematic/x.csch</c>) is that cell; a loose file is its own stem; a Data Display
    /// is the cell of its data source, else of its own <c>.cdd</c> name.
    /// </summary>
    internal static string? TileCellOf(IDockable d) => d switch
    {
        DataDisplayDocument dd => ResultCell(dd.ViewModel.Window.DataSourceLibrary.SelectedDataSourceAbs)
                                  ?? ResultCell(dd.FilePath),
        CellParameterEditorDocument cpd => Path.GetFileName(Path.GetDirectoryName(cpd.ViewModel.EditModel.CcellPath)),
        TechDocument => null,
        Commands.IFileBackedDocument { FilePath: { } path } => ViewCell(path),
        _ => null,
    };

    /// <summary>The cell of a view file: the cell folder's name when it sits in a view sub-folder,
    /// else the file's own stem.</summary>
    internal static string? ViewCell(string path)
    {
        var dir = Path.GetDirectoryName(path);
        var sub = dir is null ? null : Path.GetFileName(dir);
        if (sub is not null
            && (sub.Equals(CellFolder.SchematicSubFolder, StringComparison.OrdinalIgnoreCase)
             || sub.Equals(CellFolder.SymbolSubFolder,    StringComparison.OrdinalIgnoreCase)
             || sub.Equals(CellFolder.LayoutSubFolder,    StringComparison.OrdinalIgnoreCase)
             || sub.Equals(CellFolder.ThreeDSubFolder,    StringComparison.OrdinalIgnoreCase))
            && Path.GetDirectoryName(dir) is { Length: > 0 } cellDir)
            return Path.GetFileName(cellDir);

        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>The cell a result or display file names: its stem up to the first dot, so
    /// <c>Amp.npy</c>, <c>Amp.tb.npy</c> and <c>Amp.corners.npy</c> all name <c>Amp</c>.</summary>
    internal static string? ResultCell(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var stem = Path.GetFileName(path);
        int dot = stem.IndexOf('.');
        stem = dot > 0 ? stem[..dot] : stem;
        return stem.Length > 0 ? stem : null;
    }
}

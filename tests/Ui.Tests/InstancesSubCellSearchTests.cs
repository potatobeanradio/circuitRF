using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The Instances panel's "Include sub-cells": the walk below the listed frame, run off the UI thread.
/// The thread hops are driven by hand — a queue both the background hop and the post back land in —
/// so "the top level is listed before the walk finishes" is an observable state, not a race.
/// </summary>
public sealed class InstancesSubCellSearchTests : IDisposable
{
    private readonly string _ws;

    public InstancesSubCellSearchTests()
    {
        _ws = Path.Combine(Path.GetTempPath(), "crfSubCellSearch_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_ws);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_ws);
        if (Directory.Exists(_ws)) Directory.Delete(_ws, recursive: true);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static EditableComponent Part(string name, SymbolKind kind = SymbolKind.Resistor, string? cellRef = null)
        => new() { InstanceName = name, Symbol = kind, CellRef = cellRef };

    /// <summary>A cell with one schematic; a sub-cell is referenced as <c>../../Name</c>, from the
    /// schematic's own folder.</summary>
    private string SchematicCell(string name, params EditableComponent[] parts)
    {
        var cellDir = CellFolder.CreateCellFolder(_ws, name);
        var model = new SchematicEditModel();
        foreach (var p in parts) model.Components.Add(p);
        string path = Path.Combine(cellDir, CellFolder.SchematicSubFolder, name + ".csch");
        SchematicPersistence.SaveToFile(path, model, name);
        return path;
    }

    private string LayoutCell(string name, params LayoutInstance[] instances)
    {
        var cellDir = CellFolder.CreateCellFolder(_ws, name);
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        foreach (var i in instances) view.Instances.Add(i);
        string path = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "main.clay");
        LayoutPersistence.SaveToFile(path, view);
        return path;
    }

    private static SchematicViewModel OpenSchematic(string path)
        => new(SchematicPersistence.LoadFromFile(path).model);

    /// <summary>Both thread hops, queued until <see cref="RunAll"/>.</summary>
    private sealed class ManualThreads
    {
        private readonly Queue<Action> _work = new();
        public void Enqueue(Action a) => _work.Enqueue(a);
        public void RunAll() { while (_work.Count > 0) _work.Dequeue()(); }
    }

    private static InstanceListViewModel SubCellList(ManualThreads threads)
    {
        var list = new InstanceListViewModel
        {
            RunInBackground = threads.Enqueue,
            PostToUi        = threads.Enqueue,
            IncludeSubCells = true,
        };
        list.SetShown(true);
        return list;
    }

    // ── The walk ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The top level is listed at once and the rows below arrive with the walk, by dotted path and in
    /// natural order among them. A cell that contains itself is listed and not walked again, and a
    /// rebuild after that re-walks from memory rather than re-reading any cell.
    /// </summary>
    [Fact]
    public void SubCells_ArriveAfterTheTopLevel_ByDottedPath_AndACellContainingItselfStops()
    {
        SchematicCell("Leaf", Part("R5"));
        SchematicCell("Mid", Part("C1", SymbolKind.Capacitor), Part("X2", SymbolKind.Generic, "../../Leaf"),
                             Part("X9", SymbolKind.Generic, "../../Mid"));
        var top = SchematicCell("Top", Part("R1"), Part("X1", SymbolKind.Generic, "../../Mid"),
                                       Part("GND1", SymbolKind.Ground));

        var threads = new ManualThreads();
        var list = SubCellList(threads);
        list.SetActiveDocument(OpenSchematic(top), "Top.csch");

        Assert.Equal(["R1", "X1"], list.VisibleRows.Select(r => r.Name));
        Assert.True(list.IsSearchingSubCells);

        threads.RunAll();

        Assert.False(list.IsSearchingSubCells);
        Assert.Equal(["R1", "X1", "X1.C1", "X1.X2", "X1.X2.R5", "X1.X9"], list.VisibleRows.Select(r => r.Name));

        var r5 = (SubCellInstance)list.VisibleRows.Single(r => r.Name == "X1.X2.R5").Source;
        Assert.Equal(["X1", "X2"], r5.Chain.Select(s => s.Name));
        Assert.Equal(new InstancePathStep("R5", 0), r5.Leaf);

        list.FilterText = "r5";   // a path segment is searchable like a name
        Assert.Equal(["X1.X2.R5"], list.VisibleRows.Select(r => r.Name));

        int reads = list.DiskCache.Reads;
        list.Refresh();
        threads.RunAll();
        Assert.Equal(reads, list.DiskCache.Reads);
        Assert.Equal(["X1.X2.R5"], list.VisibleRows.Select(r => r.Name));
    }

    /// <summary>An open cell's unsaved edit is what the walk finds — the workspace hands it a copy of
    /// every open session, and the disk copy of that cell is never read.</summary>
    [Fact]
    public void AnOpenCellsUnsavedEdit_IsWhatTheWalkFinds()
    {
        var mid = SchematicCell("Mid", Part("C1", SymbolKind.Capacitor));
        var top = SchematicCell("Top", Part("X1", SymbolKind.Generic, "../../Mid"));

        var open = OpenSchematic(mid);
        open.EditModel.Components.Add(Part("L7", SymbolKind.Inductor));

        var threads = new ManualThreads();
        var list = SubCellList(threads);
        list.LiveLevels = () => new Dictionary<string, InstanceHierarchyWalk.Level>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.GetFullPath(mid)] = InstanceHierarchyWalk.Of(open.EditModel),
        };
        list.SetActiveDocument(OpenSchematic(top), "Top.csch");
        threads.RunAll();

        Assert.Equal(["X1", "X1.C1", "X1.L7"], list.VisibleRows.Select(r => r.Name));
        Assert.Equal(0, list.DiskCache.Reads);
    }

    /// <summary>The layout walk, on the same terms, and the tie-break a double-click finds a step by.</summary>
    [Fact]
    public void ALayoutSubCell_IsWalked_AndAStepIsFoundByNameWhenItsIndexMoved()
    {
        LayoutCell("Leaf");
        LayoutCell("Mid", new LayoutInstance { CellRef = "../../Leaf", RefDes = "U2" });
        var top = LayoutCell("Top", new LayoutInstance { CellRef = "../../Mid", RefDes = "U1" });

        var threads = new ManualThreads();
        var list = SubCellList(threads);
        list.SetActiveDocument(new LayoutEditorViewModel(LayoutPersistence.LoadFromFile(top), top), "Top.clay");
        threads.RunAll();

        Assert.Equal(["U1", "U1.U2"], list.VisibleRows.Select(r => r.Name));

        string[] names = ["A", "B", "U2"];
        Assert.Equal("U2", WorkspaceViewModel.FindStep(names, new InstancePathStep("U2", 2), n => n));
        Assert.Equal("U2", WorkspaceViewModel.FindStep(names, new InstancePathStep("U2", 0), n => n));
        Assert.Null(WorkspaceViewModel.FindStep(names, new InstancePathStep("U9", 0), n => n));
    }

    // ── What is listed, and when a result is dropped ─────────────────────────

    /// <summary>With sub-cells on the panel lists the tab's TOP frame, so a push-in rebuilds nothing;
    /// turned off, it follows the canvas's frame again, and a walk already running is dropped.</summary>
    [Fact]
    public void SubCellsOn_ListsTheTabsTopFrame_AndTurningItOffDropsARunningWalk()
    {
        SchematicCell("Mid", Part("C1", SymbolKind.Capacitor));
        var top = SchematicCell("Top", Part("X1", SymbolKind.Generic, "../../Mid"));
        var rootVm = OpenSchematic(top);
        var midVm  = OpenSchematic(Path.Combine(_ws, "Mid", CellFolder.SchematicSubFolder, "Mid.csch"));

        var threads = new ManualThreads();
        var list = SubCellList(threads);
        list.SetActiveDocument(rootVm, "Top.csch");
        threads.RunAll();
        int rebuilds = list.RebuildCount;

        list.SetActiveDocument(midVm, "Mid.csch", rootVm, "Top.csch");   // pushed into X1
        Assert.Equal("Top.csch", list.HeaderLabel);
        Assert.Equal(rebuilds, list.RebuildCount);

        list.Refresh();                        // a walk starts ...
        Assert.True(list.IsSearchingSubCells);
        list.IncludeSubCells = false;          // ... and is superseded before it reports
        threads.RunAll();

        Assert.False(list.IsSearchingSubCells);
        Assert.Equal("Mid.csch", list.HeaderLabel);
        Assert.Equal(["C1"], list.VisibleRows.Select(r => r.Name));
    }
}

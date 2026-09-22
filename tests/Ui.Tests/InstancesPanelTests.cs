using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// brief-find-instance-panel.md §3 — the Instances panel and Design ▸ Find Instance…, one test per
/// claim. Everything that can be is driven headlessly on the list view model; what lives only in
/// <c>WorkspaceViewModel</c> (which cannot be constructed without a shell) is a source scan, the
/// fallback this suite already uses for that class.
/// </summary>
public class InstancesPanelTests
{
    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static SchematicViewModel Schematic(params (string Name, SymbolKind Kind)[] parts)
    {
        var model = new SchematicEditModel();
        double x = 0;
        foreach (var (name, kind) in parts)
            model.Components.Add(new EditableComponent { InstanceName = name, Symbol = kind, X = x += 400, Y = 0 });
        return new SchematicViewModel(model);
    }

    private static LayoutEditorViewModel Layout(int count)
    {
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        for (int i = 1; i <= count; i++)
            model.Instances.Add(new LayoutInstance { CellRef = "../Pad", RefDes = $"U{i}", X = i * 10_000, Y = 0 });
        return new LayoutEditorViewModel(model);
    }

    /// <summary>A debounce driven by hand: the latest scheduled callback, and how many were started.</summary>
    private sealed class ManualDebounce
    {
        public Action? Pending;
        public int Started;

        public IDisposable Schedule(TimeSpan _, Action action)
        {
            Started++;
            Pending = action;
            return new Canceller(this, action);
        }

        public void Fire() { var a = Pending; Pending = null; a?.Invoke(); }

        private sealed class Canceller(ManualDebounce owner, Action action) : IDisposable
        {
            public void Dispose() { if (ReferenceEquals(owner.Pending, action)) owner.Pending = null; }
        }
    }

    private static InstanceListViewModel ShownList(object documentVm, ManualDebounce? debounce = null)
    {
        var list = new InstanceListViewModel();
        if (debounce is not null) list.Schedule = debounce.Schedule;
        list.SetShown(true);
        list.SetActiveDocument(documentVm, "doc");
        return list;
    }

    private static string RepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = System.IO.Path.GetDirectoryName(here);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "CLAUDE.md")))
            dir = System.IO.Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return System.IO.File.ReadAllText(System.IO.Path.Combine(dir!, relativePath));
    }

    // ── R-fi-4/R-fi-5: the rows and the two filters ──────────────────────────

    /// <summary>Name AND type, rows in natural order (R10 after R2), and Ground/VAR left out.</summary>
    [Fact]
    public void Filters_ByNameAndByType_AreAndCombined_InNaturalOrder()
    {
        var list = ShownList(Schematic(
            ("R10", SymbolKind.Resistor), ("C1", SymbolKind.Capacitor), ("R2", SymbolKind.Resistor),
            ("R1", SymbolKind.Resistor), ("C12", SymbolKind.Capacitor),
            ("GND1", SymbolKind.Ground), ("VAR1", SymbolKind.Var)));

        string r = ComponentTypeRegistry.DisplayName(SymbolKind.Resistor);
        string c = ComponentTypeRegistry.DisplayName(SymbolKind.Capacitor);

        Assert.Equal(["C1", "C12", "R1", "R2", "R10"], list.VisibleRows.Select(x => x.Name));
        Assert.Equal([InstanceListViewModel.AllTypes, c, r], list.TypeOptions);

        list.FilterText = "1";
        Assert.Equal(["C1", "C12", "R1", "R10"], list.VisibleRows.Select(x => x.Name));

        list.SelectedType = r;
        Assert.Equal(["R1", "R10"], list.VisibleRows.Select(x => x.Name));

        list.FilterText = "r1";   // case-insensitive
        Assert.Equal(["R1", "R10"], list.VisibleRows.Select(x => x.Name));
    }

    /// <summary>
    /// R-fi-8, on the brief's 10,000-instance layout: a keystroke that narrows the text scans only the
    /// rows the last one kept, and never goes back to the model. Gated on the counter, not a clock.
    /// </summary>
    [Fact]
    public void ANarrowingKeystroke_ScansOnlyTheSurvivors_OfA10kLayout()
    {
        var list = ShownList(Layout(10_000));
        Assert.Equal(10_000, list.TotalRows);

        list.FilterText = "u1";
        Assert.Equal(10_000, list.LastFilterScanned);
        int kept = list.VisibleRows.Count;          // U1, U10..U19, U100.., U1000.., U10000
        Assert.Equal(1_112, kept);

        list.FilterText = "u12";
        Assert.Equal(kept, list.LastFilterScanned);
        Assert.Equal(111, list.VisibleRows.Count);

        list.FilterText = "u1";                      // a backspace widens: back to every row
        Assert.Equal(10_000, list.LastFilterScanned);
        Assert.Equal(1, list.RebuildCount);          // and no keystroke rebuilt from the model
    }

    // ── R-fi-7: rebuilt from the model, debounced, and never for shapes ──────

    [Fact]
    public void Rebuild_IsDebounced_SkippedForShapesOnly_AndDeferredWhileHidden()
    {
        var vm = Layout(3);
        var debounce = new ManualDebounce();
        var list = ShownList(vm, debounce);
        Assert.Equal(1, list.RebuildCount);          // the document switch rebuilds at once

        // A shapes-only change cannot add, remove or rename an instance.
        vm.Model.Shapes.Add(new RectShape { X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 });
        vm.Model.NotifyChanged(LayoutChangeInfo.Appended(0, 1));
        vm.Model.NotifyChanged(LayoutChangeInfo.Updated([0]));
        Assert.Equal(0, debounce.Started);

        // A burst of instance changes — a drag — is one rebuild, after the model goes quiet.
        vm.Model.Instances.Add(new LayoutInstance { CellRef = "../Pad", RefDes = "U4" });
        for (int i = 0; i < 5; i++) vm.Model.NotifyChanged(LayoutChangeInfo.InstancesOnly);
        Assert.Equal(1, list.RebuildCount);
        debounce.Fire();
        Assert.Equal(2, list.RebuildCount);
        Assert.Equal(4, list.TotalRows);

        // Hidden: nothing is scheduled at all, and showing the panel again rebuilds at once.
        list.SetShown(false);
        vm.Model.NotifyChanged(LayoutChangeInfo.InstancesOnly);
        Assert.Null(debounce.Pending);
        Assert.Equal(2, list.RebuildCount);
        list.SetShown(true);
        Assert.Equal(3, list.RebuildCount);
    }

    // ── R-fi-9/R-fi-10: double-click selects and frames ──────────────────────

    /// <summary>
    /// Also the §2 trap: the row holds the INSTANCE, and its index is looked up at the gesture —
    /// the delete here shifts U2 from index 1 to 0 after the list was built.
    /// </summary>
    [Fact]
    public void ALayoutRow_SelectsItsInstance_AndFramesARegionContainingItsBox()
    {
        var vm = Layout(2);
        var list = ShownList(vm);
        var row = list.VisibleRows.Single(r => r.Name == "U2");
        var inst = (LayoutInstance)row.Source;

        vm.Model.Instances.RemoveAt(0);              // not notified: the debounce has not fired yet

        Bbox? asked = null;
        vm.ZoomToRegionRequested += b => asked = b;

        Assert.True(InstanceReveal.Reveal(vm, inst));

        Assert.Equal([0], vm.SelectedInstanceIndices);
        var box = CellHierarchy.InstanceBbox(inst, vm.InstanceBaseDir);
        Assert.NotNull(asked);
        Assert.True(asked!.Value.MinX <= box.MinX && asked.Value.MaxX >= box.MaxX
                 && asked.Value.MinY <= box.MinY && asked.Value.MaxY >= box.MaxY);
    }

    /// <summary>A row whose instance has gone rebuilds the list and does nothing else.</summary>
    [Fact]
    public void AStaleRow_RebuildsTheList_AndActivatesNothing()
    {
        var vm = Layout(2);
        var list = ShownList(vm);
        var row = list.VisibleRows[0];
        int activated = 0;
        list.InstanceActivated += _ => activated++;

        vm.Model.Instances.Remove((LayoutInstance)row.Source);

        Assert.False(list.Activate(row));
        Assert.Equal(0, activated);
        Assert.Equal(2, list.RebuildCount);
        Assert.Equal(1, list.TotalRows);
    }

    [Fact]
    public void ASchematicRow_SelectsItsComponent_AndAsksToFrameItsFullBox()
    {
        var vm = Schematic(("R1", SymbolKind.Resistor), ("R2", SymbolKind.Resistor));
        var doc = new SchematicDocument("Amp", vm);
        var comp = vm.EditModel.Components[1];

        (double MinX, double MinY, double MaxX, double MaxY)? asked = null;
        doc.ZoomToWorldRectRequested += (a, b, c, d) => asked = (a, b, c, d);

        Assert.True(InstanceReveal.Reveal(doc, comp));

        Assert.Equal([comp.Id], vm.Selection.Ids);
        var rc = vm.RenderModel!.Components.Single(c => c.Id == comp.Id);
        Assert.NotNull(asked);
        Assert.True(asked!.Value.MinX <= rc.FullBbMinX && asked.Value.MaxX >= rc.FullBbMaxX
                 && asked.Value.MinY <= rc.FullBbMinY && asked.Value.MaxY >= rc.FullBbMaxY);
    }

    // ── R-fi-13: the command's enablement ────────────────────────────────────

    [Fact]
    public void FindInstance_IsEnabledForASchematicOrALayout_AndNothingElse()
    {
        var layout = new LayoutDocument("Amp", Layout(0));
        var schematic = new SchematicDocument("Amp", Schematic());
        var display = new DataDisplayDocument("Untitled-Display-1", new DataDisplayDocumentViewModel());

        Assert.True(WorkspaceViewModel.CanFindInstanceIn(layout));
        Assert.True(WorkspaceViewModel.CanFindInstanceIn(schematic));
        Assert.False(WorkspaceViewModel.CanFindInstanceIn(display));
        Assert.False(WorkspaceViewModel.CanFindInstanceIn(null));

        // Re-asked from BOTH fan-outs, or it stays stuck at whatever it was on construction.
        var src = RepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs");
        Assert.Equal(2, src.Split("FindInstanceCommand.NotifyCanExecuteChanged()").Length - 1);
    }

    // ── R-fi-2/R-fi-3: following the focused document ───────────────────────

    [Fact]
    public void ThePanel_FollowsDocumentSwitches_AndEmptiesWhenNoneIsListable()
    {
        var tool = new InstancesTool();
        tool.ListVm.SetShown(true);

        tool.SetActiveDocument(Schematic(("R1", SymbolKind.Resistor)), "Amp.csch");
        Assert.Equal("Amp.csch", tool.ListVm.HeaderLabel);
        Assert.Equal(["R1"], tool.ListVm.VisibleRows.Select(r => r.Name));

        tool.SetActiveDocument(Layout(2), "Amp.clay");
        Assert.Equal("Amp.clay", tool.ListVm.HeaderLabel);
        Assert.Equal(["U1", "U2"], tool.ListVm.VisibleRows.Select(r => r.Name));

        tool.SetActiveDocument(null, null);          // a Data Display focused, or the last tab closed
        Assert.Equal("Instances", tool.ListVm.HeaderLabel);
        Assert.Empty(tool.ListVm.VisibleRows);
        Assert.True(tool.ListVm.ShowEmptyState);
        Assert.Equal(InstanceListViewModel.NoDocumentText, tool.ListVm.EmptyStateText);

        // The routes that drive it: every activation (cleared for any other kind of document), and
        // the close of the document it is following.
        var src = RepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs");
        Assert.Contains("RouteInstancesPanel(activeDockable);", src);
        Assert.Contains("if (ReferenceEquals(dockable, _instancesFrameDoc)) RouteInstancesPanel(null);", src);
    }

    /// <summary>R-fi-1: it appears when asked for, and in no shipped Window Layout.</summary>
    [Fact]
    public void ThePanel_IsInNoShippedDefaultLayout()
    {
        foreach (var layout in Enum.GetValues<CircuitRF.Ui.Theming.WindowLayout>().Select(DockLayoutDefaults.For))
            Assert.DoesNotContain(layout.Panels, p => p.Id == DockPanelIds.Instances && p.Open);
    }
}

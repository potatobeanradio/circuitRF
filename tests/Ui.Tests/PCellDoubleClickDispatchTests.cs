using System.IO;
using System.Runtime.CompilerServices;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-L5-followups-3.md §1 (R-L5h-1/2): the THIRD attempt at "double-click on
/// a PCell must open its parameter editor, not push in." The first two rounds each added a guard
/// INSIDE <c>DoPushInto</c> — which is why the user kept seeing push-in's own polite refusal message
/// ("Can't push into cell: ...") instead of anything opening: push-in was still being CALLED. This
/// round changes the DISPATCH itself, in <c>LayoutEditorView.OnInstanceDoubleTapped</c> — the new
/// <see cref="LayoutHierarchyResolver.IsPCellInstance"/> predicate is checked FIRST, before
/// <c>DoPushInto</c> is ever reached.
///
/// <c>LayoutEditorView</c> is a <c>UserControl</c> and this project's tests must not call any
/// Avalonia runtime API, so the dispatch method itself cannot be driven directly (matching every
/// prior Layout Editor phase's note on this). Correctness rests on two things: (1) the predicate the
/// dispatch calls, tested directly here at the VM level (the same public methods the view's
/// code-behind calls); and (2) a structural source-scan proving the dispatch actually checks the
/// predicate BEFORE calling <c>DoPushInto</c> — the exact ordering bug all three rounds have been
/// about, and the one thing a VM-level test alone cannot prove.
/// </summary>
public sealed class PCellDoubleClickDispatchTests : IDisposable
{
    private readonly string _root;

    public PCellDoubleClickDispatchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-pcell-dispatch-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        CellLayoutResolver.InvalidateUnder(_root);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_root);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private LayoutEditorViewModel MakeVm(string cellName = "Doc") =>
        new(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 }, Path.Combine(_root, cellName, "layout", "main.clay"));

    // ── LayoutHierarchyResolver.IsPCellInstance (the predicate the dispatch calls) ──────────────

    [Fact]
    public void IsPCellInstance_TrueForAResolvedPCellInstance()
    {
        var vm = MakeVm();
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(_root, "MLIN", defaults, null, null, PCellLayerSelection.Default);
        var inst = new LayoutInstance { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 0, Y = 0, Mag = 1.0 };

        Assert.True(LayoutHierarchyResolver.IsPCellInstance(inst, vm));
    }

    [Fact]
    public void IsPCellInstance_FalseForAnOrdinaryCellInstance()
    {
        var vm = MakeVm();
        string cellDir = CellFolder.CreateCellFolder(_root, "MyAmp");
        var clayPath = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "MyAmp.clay");
        Directory.CreateDirectory(Path.GetDirectoryName(clayPath)!);
        LayoutPersistence.SaveToFile(clayPath, new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 });

        var inst = new LayoutInstance { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 0, Y = 0, Mag = 1.0 };

        Assert.False(LayoutHierarchyResolver.IsPCellInstance(inst, vm));
        // And an ordinary instance is still push-in-able — the predicate does not clobber that path.
        Assert.True(LayoutHierarchyResolver.CanPushInto(inst, vm, out _));
    }

    [Fact]
    public void IsPCellInstance_FalseForAnUnresolvableInstance_FallsThroughToPushInsOwnRefusal()
    {
        var vm = MakeVm();
        var inst = new LayoutInstance { CellRef = "../../nowhere", X = 0, Y = 0, Mag = 1.0 };

        Assert.False(LayoutHierarchyResolver.IsPCellInstance(inst, vm));
        Assert.False(LayoutHierarchyResolver.CanPushInto(inst, vm, out var reason));
        Assert.Equal("cell reference not found", reason);
    }

    [Fact]
    public void IsPCellInstance_FalseForNullInstanceOrNullVm()
    {
        var vm = MakeVm();
        Assert.False(LayoutHierarchyResolver.IsPCellInstance(null, vm));
        Assert.False(LayoutHierarchyResolver.IsPCellInstance(new LayoutInstance { CellRef = "x" }, null));
    }

    // ── LayoutEditorViewModel.SelectInstance (what the dispatch calls instead of push-in) ───────

    [Fact]
    public void SelectInstance_SelectsExactlyThatIndex()
    {
        var vm = MakeVm();
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(_root, "MLIN", defaults, null, null, PCellLayerSelection.Default);
        vm.Model.Instances.Add(new LayoutInstance { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 0, Y = 0, Mag = 1.0 });
        vm.Model.Instances.Add(new LayoutInstance { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 20_000_000, Y = 0, Mag = 1.0 });

        vm.SelectInstance(1);

        Assert.Single(vm.SelectedInstanceIndices);
        Assert.Equal(1, vm.SelectedInstanceIndices[0]);
    }

    // ── Structural proof of dispatch ORDER — the actual bug across all three rounds ─────────────

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    [Fact]
    public void OnInstanceDoubleTapped_ChecksIsPCellInstance_BeforeDoPushIntoIsEverReached()
    {
        string src = ReadRepoFile(Path.Combine("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs"));

        int methodStart = src.IndexOf("private void OnInstanceDoubleTapped(", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "OnInstanceDoubleTapped not found");
        int methodEnd = src.IndexOf("\n    }", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart, "could not find the end of OnInstanceDoubleTapped");
        string body = src[methodStart..methodEnd];

        int predicateAt = body.IndexOf("IsPCellInstance(", StringComparison.Ordinal);
        int pushInAt = body.IndexOf("DoPushInto(doc, instance)", StringComparison.Ordinal);
        Assert.True(predicateAt >= 0, "OnInstanceDoubleTapped no longer checks IsPCellInstance");
        Assert.True(pushInAt >= 0, "OnInstanceDoubleTapped no longer calls DoPushInto for the fallthrough case");
        Assert.True(predicateAt < pushInAt,
            "IsPCellInstance must be checked BEFORE DoPushInto is reached — this is the exact ordering bug " +
            "every previous round left in place (a guard added INSIDE DoPushInto still means DoPushInto was called).");

        // The PCell branch must return before falling through — DoPushInto's call in the body must
        // appear only once, in the non-PCell path (not duplicated/also called in the PCell branch).
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, "DoPushInto\\("));
    }

    [Fact]
    public void UpdateHierarchyButtonStates_SetsTheDisabledReasonOnThePushInTooltip()
    {
        string src = ReadRepoFile(Path.Combine("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs"));

        int methodStart = src.IndexOf("private void UpdateHierarchyButtonStates(", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "UpdateHierarchyButtonStates not found");
        int methodEnd = src.IndexOf("\n    }", methodStart, StringComparison.Ordinal);
        string body = src[methodStart..methodEnd];

        Assert.Contains("ToolTip.SetTip(PushInBtn", body);
        Assert.Contains("reason", body);
    }
}

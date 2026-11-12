// ================================================================
//  CopyPasteObjectsTests.cs — brief-em3d-95's view-model gates (pixels were not seen: Avalonia cannot start from this
//  machine's shell, so the menus are read as the items the view fills them with):
//    1  Paste is ONE undo entry, and the pasted rows are then the tree's selection and the scene's
//    2  the tree's empty area offers Paste: disabled with its reason until a 3D copy is on the clipboard
//    3  Copy on a field plot is disabled with its reason; on a port and on a boundary it is enabled
//    4  a paste that must ask raises the dialog request instead of pasting
// ================================================================

using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class CopyPasteObjectsTests : IDisposable
{
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-em95vm-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public CopyPasteObjectsTests()
    {
        Snap3DPreference.TestOverrideActive = true;
        Lod3DPreference.TestOverrideActive = true;
    }

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public void Gate1_PasteIsOneEntry_AndThePastedRowsAreSelected()
    {
        var vm = Open();
        Assert.Null(vm.CopyRows([Row(vm, "a")]));
        Assert.NotNull(vm.ClipboardText);
        int entries = vm.UndoEntries;

        Assert.Null(vm.Paste(vm.ClipboardText));
        Settle(vm);
        Assert.Equal(["a", "b", "a_2"], vm.Document.Objects.Select(o => o.Name));
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Equal(["a_2"], vm.SelectedTreeItems.Select(r => r.Name));
        Assert.Equal(["a_2"], vm.Viewer.SelectedObjects().Select(o => o.Name));
        Assert.StartsWith("Pasted 1 object; renamed a → a_2", vm.StatusMessage);
        // The copy lies on its original, so its row's menu moves it: the canvas's operations, the row's own Duplicate once.
        var headers = vm.TreeMenuItems(Row(vm, "a_2")).Select(i => i.Header).ToList();
        Assert.Equal(["Move", "Move Along", "Rotate", "Rotate 90°", "Mirror", "Duplicate", "Array…", "Align", "Order"],
                     headers.SkipWhile(h => h != "Move").Take(9));
        Assert.Single(headers, h => h == "Duplicate");

        vm.UndoRedo.Undo();
        Settle(vm);
        Assert.Equal(["a", "b"], vm.Document.Objects.Select(o => o.Name));
    }

    [Fact]
    public void Gate2_TheEmptyArea_OffersPaste_DisabledUntilA3DCopyIsThere()
    {
        var vm = Open();
        vm.ClipboardText = "some text from elsewhere";
        var paste = Assert.Single(vm.TreeEmptyMenuItems());
        Assert.Equal((C3dEditorViewModel.PasteHeader, false, C3dEditorViewModel.NothingCopied), (paste.Header, paste.Enabled, paste.Tip));

        vm.CopyRows([Row(vm, "b")]);
        Assert.True(Assert.Single(vm.TreeEmptyMenuItems()).Enabled);
        Assert.Contains(vm.TreeMenuItems(Row(vm, "a")), i => i.Header == C3dEditorViewModel.PasteHeader && i.Enabled);   // on a row too
    }

    [Fact]
    public void Gate3_CopyOnAFieldPlotIsDisabledWithItsReason_OnAPortAndABoundaryEnabled()
    {
        var vm = Open(records: true);
        var plot = vm.CopyItem([new C3dTreeItem(vm, "plot", C3dEditorViewModel.FieldPlotKind, null, -1, -1, true)]);
        Assert.False(plot.Enabled);
        Assert.Contains("field plot", plot.Tip);

        Assert.True(vm.CopyItem([new C3dTreeItem(vm, C3dPorts.ProblemName(1), "Port", null, -1, -1, true)]).Enabled);
        Assert.True(vm.CopyItem([new C3dTreeItem(vm, Scene3DBuilder.FaceTintPrefix + "a/zmax", C3dEditorViewModel.EmBoundaryKind, null, -1, -1, true)]).Enabled);
        Assert.DoesNotContain(vm.Viewer.ContextMenuItems(), i => i.Header == C3dEditorViewModel.CopyObjectsHeader);   // nothing selected
        vm.TreeSelectionChanged([Row(vm, "a"), Row(vm, "b")]);
        Assert.Contains(vm.Viewer.ContextMenuItems(), i => i.Header == C3dEditorViewModel.CopyObjectsHeader && i.Enabled);
    }

    [Fact]
    public void Gate4_APasteThatMustAsk_RaisesTheDialogRequest_AndPastesNothing()
    {
        var vm = Open();
        vm.Document.Variables.Add(new C3dVariable { Name = "h", Expression = "5", Unit = "Um" });
        var source = new C3dDocument { Objects = [new C3dBox { Name = "z", Size = new(1, 1, 1) }], Variables = [new C3dVariable { Name = "h", Expression = "7", Unit = "Um" }] };
        C3dBindings.SetExpr(source.Objects[0], C3dBindings.SpecOf(typeof(C3dBox), nameof(C3dBox.Size))!, 2, new C3dExpr("h"));
        string text = C3dFragment.Serialize(C3dFragment.Build(source, null, new C3dCopySelection { Objects = [0] }, new C3dCopyContext("", null, C3dCell.None, null)));
        C3dPasteRequest? asked = null;
        vm.PasteDialogRequested += r => asked = r;
        int entries = vm.UndoEntries;

        vm.Paste(text);
        Assert.Equal("h", Assert.Single(asked!.Plan.Variables).Name);
        Assert.Equal(entries, vm.UndoEntries);

        var choices = asked.Plan.Defaults();
        choices.Variables["h"] = "h_2";
        Assert.Null(vm.CommitPaste(asked.Payload, choices));
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Contains(vm.Document.Variables, v => v.Name == "h_2");
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Two 20 µm Gold boxes a and b; <paramref name="records"/> adds a port and an EM face boundary on a's top.</summary>
    private C3dEditorViewModel Open(bool records = false)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cell.c3d");
        C3dBox Box(string name, int i) => new()
        {
            Name = name, Material = "Gold", Min = new C3dPoint3(i * 30 * Um, 0, 0), Size = new C3dPoint3(20 * Um, 20 * Um, 20 * Um),
        };
        var doc = new C3dDocument { SnapDbu = 1 * Um, Objects = [Box("a", 0), Box("b", 1)] };
        if (records)
        {
            doc.Ports = [new C3dPort { Number = 1, Plane = C3dPlane.XY, Rect = new C3dRect { Min = new(0, 0), Size = new(10 * Um, 10 * Um) } }];
            doc.FaceBoundaries = [new C3dFaceBoundary { Object = "a", Face = "zmax", Kind = Em3dFaceBoundaryKind.Pec }];
        }
        C3dPersistence.SaveToFile(path, doc);
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue)
        {
            ResultsRootProvider = () => Path.Combine(_root, "results"),
        };
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(400, 300);
        vm.Start();
        Settle(vm);
        return vm;
    }

    private static C3dTreeItem Row(C3dEditorViewModel vm, string name)
    {
        static IEnumerable<C3dTreeItem> Walk(C3dTreeItem i) => i.Children.SelectMany(Walk).Prepend(i);
        return vm.Tree.SelectMany(g => g.Items).SelectMany(Walk).First(r => r.Name == name && !r.IsGroup);
    }

    private void Until(Func<bool> done, string what)
        => Assert.True(SpinWait.SpinUntil(() => { while (_posted.TryDequeue(out var a)) a(); return done(); }, TimeSpan.FromSeconds(30)), what);

    private void Settle(C3dEditorViewModel vm) => Until(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, "the scene never settled");
}

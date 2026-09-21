using CircuitRF.Design;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Footprints;

/// <summary>
/// Three owner reports, 2026-09-20, and ONE cause: Escape did not deselect a footprint designator,
/// clicking empty canvas did not deselect one, and Reset in the Properties Inspector — pressed with
/// a MLIN selected — moved a capacitor's designator instead.
///
/// <para>The designator is the FOURTH selection channel. <c>SetDesignatorSelection</c> was taught to
/// clear the other three when it takes the selection; the other three were never taught about it, so
/// a designator selection survived every replace that followed and nothing but another designator
/// click could end it. The Reset button is the damaging symptom because the Properties Inspector
/// follows the INSTANCE selection while <c>ResetSelectedDesignatorPositions</c> prefers the
/// DESIGNATOR one — two channels, two different parts, one button.</para>
/// </summary>
public sealed class DesignatorSelectionEndsTests : IDisposable
{
    private readonly string _root;

    public DesignatorSelectionEndsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-desig-sel-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".cws"), "{}");
        CellLayoutResolver.InvalidateUnder(_root);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_root);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Two land patterns, both with a designator, both with a MANUAL label offset so Reset
    /// has something to undo on either of them.</summary>
    private LayoutEditorViewModel Setup()
    {
        var entry = ShippedTechnologies.All.First(e =>
            LandPatternLayers.Resolve(ShippedTechnologies.Load(e), PCellLayerSelection.Default, []).Silkscreen is not null);
        var tech = ShippedTechnologies.Load(entry);
        var vm = new LayoutEditorViewModel(
            new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 },
            Path.Combine(_root, "Board", "layout", "main.clay"))
            { ActiveTool = LayoutEditorViewModel.Tool.Select, Technology = tech };

        string cellDir = GeneratedCellStore.GetOrCreate(
            _root, FootprintRef.For(SmtCaseTable.All[0]).ToString(), new Dictionary<string, PCellValue>(),
            tech, entry.Id, PCellLayerSelection.Default);
        string cellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir);

        vm.Model.Instances.Add(new LayoutInstance   // index 0 — "the capacitor"
        { CellRef = cellRef, X = 0, Y = 0, Mag = 1.0, RefDes = "C1", LabelDx = 500_000, LabelDy = 500_000 });
        vm.Model.Instances.Add(new LayoutInstance   // index 1 — "the other part"
        { CellRef = cellRef, X = 20_000_000, Y = 0, Mag = 1.0, RefDes = "L1", LabelDx = 700_000, LabelDy = 700_000 });
        return vm;
    }

    /// <summary>Clicks the designator of instance <paramref name="index"/> the way a user does —
    /// through the pointer path, so the hit test and <c>TryBeginDesignatorDrag</c> are what put the
    /// selection there, rather than a test-only entry point that could diverge from the gesture. Both
    /// placements store a manual offset, so the label sits at the instance origin plus that offset.
    /// </summary>
    private static void ClickDesignator(LayoutEditorViewModel vm, int index)
    {
        var inst = vm.Model.Instances[index];
        long x = inst.X + inst.LabelDx!.Value, y = inst.Y + inst.LabelDy!.Value;
        vm.OnPointerPressed(x, y, Avalonia.Input.KeyModifiers.None, hitTolDbu: 1000);
        vm.OnPointerReleased(x, y, Avalonia.Input.KeyModifiers.None);
        Assert.Equal([index], vm.SelectedDesignatorIndices);
    }

    /// <summary>Selecting an instance ends a designator selection, so the Reset button under that
    /// instance's properties resets THAT instance. This is the report: the capacitor moved.</summary>
    [Fact]
    public void ResetActsOnThePartThePropertiesInspectorIsShowing()
    {
        var vm = Setup();
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);

        ClickDesignator(vm, 0);                       // the capacitor's designator was clicked earlier

        vm.SelectInstance(1);                         // ...then the other part was selected
        Assert.Empty(vm.SelectedDesignatorIndices);

        props.ResetInstanceDesignatorPosition();

        Assert.Null(vm.Model.Instances[1].LabelDx);    // the part the panel was showing
        Assert.Equal(500_000, vm.Model.Instances[0].LabelDx);   // the capacitor, untouched
    }

    /// <summary>Escape with nothing else going on clears the whole selection — all four channels.</summary>
    [Fact]
    public void EscapeDeselectsADesignator()
    {
        var vm = Setup();
        ClickDesignator(vm, 0);
        vm.OnKeyDown(Avalonia.Input.Key.Escape, Avalonia.Input.KeyModifiers.None);
        Assert.Empty(vm.SelectedDesignatorIndices);
    }

    /// <summary>A press on empty canvas is a replace with nothing in it, so it ends a designator
    /// selection the same way it ends a shape or instance one.</summary>
    [Fact]
    public void ClickingEmptyCanvasDeselectsADesignator()
    {
        var vm = Setup();
        ClickDesignator(vm, 0);
        vm.OnPointerPressed(-50_000_000, -50_000_000, Avalonia.Input.KeyModifiers.None);
        Assert.Empty(vm.SelectedDesignatorIndices);
    }
}

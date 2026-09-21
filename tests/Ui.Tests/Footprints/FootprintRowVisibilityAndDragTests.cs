using Avalonia.Input;
using CircuitRF.Design;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Footprints;

/// <summary>
/// Owner report, 2026-09-20, on the Layout Editor's Properties Inspector — two faults that happened
/// to land on the same two controls.
///
/// <para>The Footprint row offered a case size and an IPC-7351B density level for a PCell that draws
/// its own artwork and is not a land pattern at all (a microstrip). And during an ordinary move drag
/// the row read "(no cell)" from press to release, reverting on commit.</para>
/// </summary>
public sealed class FootprintRowVisibilityAndDragTests : IDisposable
{
    private readonly string _root;

    public FootprintRowVisibilityAndDragTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-fp-row-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".cws"), "{}");
        CellLayoutResolver.InvalidateUnder(_root);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_root);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>A land pattern's case and density ARE its identity, so the row belongs to it. A
    /// microstrip's outline comes from its width and its length — there is no case size to name and
    /// nothing for land protrusion to protrude from, so both controls go away.</summary>
    [Fact]
    public void TheFootprintRowIsShownForALandPatternAndHiddenForAMicrostrip()
    {
        var (vm, props) = PanelOver(LandPatternCell(out var reference));
        Assert.True(props.ShowInstanceFootprintRow);
        Assert.Equal(reference.Case.Code, vm.SelectedInstanceFootprint?.Case.Code);

        (_, props) = PanelOver(GeneratedCellStore.GetOrCreate(
            _root, "MLIN", SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0),
            null, null, PCellLayerSelection.Default));
        Assert.False(props.ShowInstanceFootprintRow);
    }

    /// <summary>The footprint is a property of the CELL, which a move drag does not touch — so the
    /// row keeps naming the placement's own case for the whole gesture. It used to read "(no cell)":
    /// the panel refreshes on every drag frame, and the accessor it read the instance from refuses
    /// to answer while a drag preview is live.</summary>
    [Fact]
    public void AMoveDragDoesNotBlankTheFootprintValue()
    {
        var (vm, props) = PanelOver(LandPatternCell(out _));
        string before = props.InstanceFootprintOptions[props.InstanceFootprintIndex];

        // Press on a pad, not on the cell origin — for a two-pad land pattern that is the gap
        // between them.
        var (px, py) = PadPoint(vm);
        vm.OnPointerPressed(px, py, KeyModifiers.None, hitTolDbu: 1_000);
        vm.OnPointerMoved(px + 2_000_000, py, leftDown: true, KeyModifiers.None, hitTolDbu: 1_000);
        Assert.NotEmpty(vm.Overlay.InstanceDragOverrides);   // the gesture really is a live drag

        Assert.DoesNotContain("(no cell)", props.InstanceFootprintOptions);
        Assert.Equal(before, props.InstanceFootprintOptions[props.InstanceFootprintIndex]);
        Assert.True(props.ShowInstanceFootprintRow);

        vm.OnPointerReleased(px + 2_000_000, py, KeyModifiers.None);
        Assert.Equal(before, props.InstanceFootprintOptions[props.InstanceFootprintIndex]);
    }

    /// <summary>A generated land-pattern cell, on a PCB technology — the MMIC one has none of the
    /// layers a land pattern draws on and generates an empty cell, which there is nothing to press.</summary>
    private string LandPatternCell(out FootprintRef reference)
    {
        reference = FootprintRef.For(SmtCaseTable.All[0]);
        var tech = ShippedTechnologies.All.First(t => t.Id.StartsWith("pcb-", StringComparison.Ordinal));
        return GeneratedCellStore.GetOrCreate(
            _root, reference.ToString(), new Dictionary<string, PCellValue>(),
            ShippedTechnologies.Load(tech), tech.Id, PCellLayerSelection.Default);
    }

    /// <summary>The centre of the placed cell's first pad, in the layout's own coordinates.</summary>
    private static (long X, long Y) PadPoint(LayoutEditorViewModel vm)
    {
        var view = CellLayoutResolver.Resolve(vm.Model.Instances[0].CellRef, vm.InstanceBaseDir).View!;
        return view.Shapes[0] switch
        {
            RectShape r    => ((r.X1 + r.X2) / 2, (r.Y1 + r.Y2) / 2),
            PolygonShape p => (Mid(p.Xy, 0), Mid(p.Xy, 1)),
            var other      => throw new InvalidOperationException($"unexpected pad shape {other.GetType().Name}"),
        };

        static long Mid(long[] xy, int offset)
        {
            long lo = xy[offset], hi = xy[offset];
            for (int i = offset; i < xy.Length; i += 2) { lo = Math.Min(lo, xy[i]); hi = Math.Max(hi, xy[i]); }
            return (lo + hi) / 2;
        }
    }

    private (LayoutEditorViewModel Vm, LayoutShapePropertiesViewModel Props) PanelOver(string cellDir)
    {
        var vm = new LayoutEditorViewModel(
            new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 },
            Path.Combine(_root, "Board", "layout", "main.clay"))
            { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.Model.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 0, Y = 0, Mag = 1.0,
        });
        vm.SelectInstance(0);

        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);
        return (vm, props);
    }
}

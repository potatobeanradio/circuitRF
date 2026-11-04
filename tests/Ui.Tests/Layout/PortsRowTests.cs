// brief-dfr33-1 R-dfr33-1-1 — the Layers list's Ports row. A port label lives on the conductor layer it drives, so
// turning that layer's Sel box off took the traces with it and nothing set ports aside on their own. One test per
// claim: Sel off takes ports out of a click, Select All and a marquee while the Port tool still places them; Vis off
// draws none; and the row is stored with the rest of the view's layer state.

using System.IO;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Render;
using CircuitRF.Ui.Layout;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

public sealed class PortsRowTests : IDisposable
{
    private static readonly LayerKey Copper = new(1, 0);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-ports-row-" + Guid.NewGuid().ToString("N"));

    public PortsRowTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static Technology Tech() => new()
    {
        Name = "One copper",
        Layers = { new LayerDef { Key = Copper, Name = "Top Copper", Color = new Rgba(200, 120, 40) } },
    };

    private static LabelShape Port(long x, string text, LayoutRotation direction) => new()
    {
        Layer = Copper, PortLayer = Copper, X = x, Y = 0, Text = text, Height = 1_000, IsPort = true,
        PortDirection = direction,
    };

    /// <summary>A 50 µm trace with a port on its left end.</summary>
    private static LayoutView TraceWithPort(bool withPort = true)
    {
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        view.Shapes.Add(new RectShape { Layer = Copper, X1 = 0, Y1 = -1_000, X2 = 50_000, Y2 = 1_000 });
        if (withPort) view.Shapes.Add(Port(0, "P1", LayoutRotation.R0));
        return view;
    }

    private static LayoutEditorViewModel Editor(LayoutView view)
    {
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.ApplyTechResolution(new TechResolution(Tech(), "/ws/tech/t.ctech", TechResolutionSource.WorkspaceDefault, []));
        return vm;
    }

    private static bool IsPort(LayoutEditorViewModel vm, int i) => vm.Model.Shapes[i] is LabelShape { IsPort: true };

    [Fact]
    public void SelectableOff_LeavesPortsOutOfAClickSelectAllAndAMarquee_AndThePortToolStillPlacesThem()
    {
        var vm = Editor(TraceWithPort());
        Assert.True(IsPort(vm, vm.PickStackForTest(0, 0, 0)[0].Index));   // ports rank first

        vm.PortsSelectable = false;
        Assert.Equal("Ports not selectable", vm.PortsRowStatusText);
        Assert.True(vm.HasLayerOverrides);
        Assert.DoesNotContain(vm.PickStackForTest(0, 0, 0), p => p.Kind == LayoutPickKind.Shape && IsPort(vm, p.Index));
        Assert.NotEmpty(vm.PickStackForTest(0, 0, 0));                     // the trace under it is now the top

        vm.SelectAllCommand.Execute(null);
        Assert.Equal([0], vm.SelectedIndices);

        vm.DeselectAllCommand.Execute(null);
        vm.OnPointerPressed(-10_000, -10_000, KeyModifiers.None);
        vm.OnPointerMoved(60_000, 10_000, leftDown: true, KeyModifiers.None);
        vm.OnPointerReleased(60_000, 10_000, KeyModifiers.None);
        Assert.Equal([0], vm.SelectedIndices);

        vm.ActiveTool = LayoutEditorViewModel.Tool.Port;
        vm.OnPointerPressed(50_000, 0, KeyModifiers.None);
        Assert.Equal(2, vm.Model.Shapes.OfType<LabelShape>().Count(l => l.IsPort));
    }

    [Fact]
    public void VisibleOff_DrawsNoPortGlyph()
    {
        var vp = LayoutViewport.ZoomToFit(new Bbox(-10_000, -5_000, 60_000, 5_000), 600, 200, 0.05);
        var theme = LayoutRenderTheme.Light;

        byte[] Draw(LayoutView view, bool hidePorts)
        {
            using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
            LayoutRenderer.Draw(surface.Canvas, view, Tech(), vp, new LayoutRenderOptions { Theme = theme, HidePorts = hidePorts });
            using var image = surface.Snapshot();
            return image.PeekPixels().GetPixelSpan().ToArray();
        }

        var bare = Draw(TraceWithPort(withPort: false), hidePorts: false);
        Assert.NotEqual(bare, Draw(TraceWithPort(), hidePorts: false));
        Assert.Equal(bare, Draw(TraceWithPort(), hidePorts: true));
    }

    /// <summary>Designer feedback round 34 — with the port's copper layer hidden, the editor canvas still draws the port
    /// (the Ports row alone decides), a click still picks it; an export (no flag) still hides it with its layer.</summary>
    [Fact]
    public void AHiddenCopperLayer_LeavesItsPortsDrawnAndPickable_OnTheEditorCanvasOnly()
    {
        var vp = LayoutViewport.ZoomToFit(new Bbox(-10_000, -5_000, 60_000, 5_000), 600, 200, 0.05);
        var hidden = Tech();
        hidden.Layers[0].Visible = false;

        byte[] Draw(LayoutView view, bool editor)
        {
            using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
            LayoutRenderer.Draw(surface.Canvas, view, hidden, vp,
                                new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, PortsIgnoreLayerVisibility = editor });
            using var image = surface.Snapshot();
            return image.PeekPixels().GetPixelSpan().ToArray();
        }

        var bare = Draw(TraceWithPort(withPort: false), editor: true);
        Assert.NotEqual(bare, Draw(TraceWithPort(), editor: true));
        Assert.Equal(bare, Draw(TraceWithPort(), editor: false));

        var vm = new LayoutEditorViewModel(TraceWithPort()) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.ApplyTechResolution(new TechResolution(hidden, "/ws/tech/t.ctech", TechResolutionSource.WorkspaceDefault, []));
        Assert.Contains(vm.PickStackForTest(0, 0, 0), p => p.Kind == LayoutPickKind.Shape && IsPort(vm, p.Index));
    }

    [Fact]
    public void TheRowRoundTripsThroughTheCwsuser_AndShowAllClearsIt()
    {
        var vm = Editor(TraceWithPort());
        vm.PortsVisible = false;
        vm.PortsSelectable = false;

        string cws = Path.Combine(_dir, ".cws");
        WorkspacePersistence.SaveToFile(cws, new CwsFile
        {
            LayoutLayerViews = new() { ["cells/a/layout/a.clay"] = vm.LayerViewToPersist()! },
        });
        var reopened = Editor(TraceWithPort());
        reopened.ApplyLayerView(WorkspacePersistence.LoadFromFile(cws).LayoutLayerViews!["cells/a/layout/a.clay"]);
        Assert.False(reopened.PortsVisible);
        Assert.False(reopened.PortsSelectable);

        reopened.ShowAllLayersCommand.Execute(null);
        Assert.True(reopened.PortsVisible && reopened.PortsSelectable);
        Assert.Null(reopened.LayerViewToPersist());
    }
}

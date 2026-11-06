// The Nets panel and `explain --nets` (brief-ipx-1): one function behind both, a highlight that is overlay state and never reaches
// a picture of the layout, and a move through the rows that scans no shape and draws per layer rather than per shape.

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Theming;
using CircuitRF.Render;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Nets;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.Nets;

public sealed class NetsPanelTests : IDisposable
{
    private static readonly LayerKey Top = new(1, 0), Bottom = new(2, 0), Drill = new(3, 0);

    private readonly string _dir = Directory.CreateTempSubdirectory("nets-panel-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Technology Tech() => new()
    {
        Layers =
        [
            new LayerDef { Key = Top, Name = "Top", Color = new Rgba(200, 60, 60), ZOrder = 3, FillOpacity = 1 },
            new LayerDef { Key = Drill, Name = "Drill", Color = new Rgba(90, 90, 90), ZOrder = 2 },
            new LayerDef { Key = Bottom, Name = "Bottom", Color = new Rgba(60, 90, 200), ZOrder = 1 },
        ],
        Stackup = new Stackup
        {
            Layers =
            [
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = 35, SigmaSm = 5.8e7, DrawingLayers = [Top] },
                new StackupLayer { Kind = StackupKind.Via, Name = "Via", DrawingLayers = [Drill], SpanFromLayer = "Top", SpanToLayer = "Bottom" },
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Bottom", ThicknessDbu = 35, SigmaSm = 5.8e7, DrawingLayers = [Bottom] },
            ],
        },
    };

    private static RectShape Rect(string? net, LayerKey layer, long x) =>
        new() { Layer = layer, X1 = x, Y1 = 0, X2 = x + 1000, Y2 = 1000, Net = net };

    private static LayoutView Board() => new()
    {
        DisplayUnit = LayoutUnit.Mm,
        Shapes =
        {
            Rect("N10", Top, 0),
            Rect("N2", Top, 2000),
            Rect("N2", Top, 4000),
            new ViaShape { Layer = Drill, X = 6000, Y = 500, PadSize = 600, DrillSize = 300, Net = "N10" },
            Rect(null, Top, 8000),
            new LabelShape { Layer = Top, X = 0, Y = 0, Text = "not copper", Net = "ONLY_A_LABEL" },
        },
    };

    private static (LayoutEditorViewModel Vm, NetsPanelViewModel Panel) Open(LayoutView view) => Open(view, Tech());

    private static (LayoutEditorViewModel Vm, NetsPanelViewModel Panel) Open(LayoutView view, Technology? tech)
    {
        var vm = new LayoutEditorViewModel(view) { Technology = tech };
        var panel = new NetsPanelViewModel { PostBuild = job => job() };
        panel.SetEditor(vm);
        panel.IsShown = true;
        return (vm, panel);
    }

    private static byte[] Draw(LayoutView view, Technology tech, LayoutOverlay? overlay, ColorVariant variant = ColorVariant.Light)
    {
        using var bitmap = new SKBitmap(200, 100);
        using var canvas = new SKCanvas(bitmap);
        LayoutRenderer.Draw(canvas, view, tech, new LayoutViewport(-1000, -1000, 0.01, 200, 100),
                            new LayoutRenderOptions { Theme = LayoutRenderTheme.FromTheme(ColorTheme.BuiltIn, variant), Overlay = overlay });
        return bitmap.Bytes;
    }

    [Fact]
    public void Rows_CountTheCellsShapes_InNaturalOrder_WithTheLayersEachReaches()
    {
        var list = LayoutNets.Of(Board(), Tech());

        // N2 before N10; the label's name is no net, and the via reaches both conductors its stackup entry joins.
        Assert.Equal(["N2", "N10"], list.Rows.Select(r => r.Name));
        Assert.Equal(2, list.Rows[0].ShapeCount);
        Assert.Equal(["Top"], list.Rows[0].Layers);
        Assert.Equal(2, list.Rows[1].ShapeCount);
        Assert.Equal(["Top", "Bottom", "Drill"], list.Rows[1].Layers);
        Assert.Equal([1, 2], list.ShapesOf("N2"));
        Assert.Null(list.Scope);
    }

    [Fact]
    public void ALayoutPlacingParts_SaysItsCountsLeaveThemOut()
    {
        var view = Board();
        view.Instances.Add(new LayoutInstance { CellRef = "Part" });

        var (_, panel) = Open(view);

        Assert.Contains("its placed part", panel.HeaderText);
    }

    [Fact]
    public void NoNamedNets_IsOneLineSayingSo()
    {
        var (_, panel) = Open(new LayoutView { Shapes = { Rect(null, Top, 0) } });

        Assert.Empty(panel.Rows);
        Assert.Equal(LayoutNets.NoNamedNets, panel.EmptyText);
        Assert.False(panel.ZoomToNetCommand.CanExecute(null));
    }

    [Fact]
    public void ThePanelsRows_AreWhatExplainNetsPrints()
    {
        string clay = Path.Combine(_dir, "board.clay");
        LayoutPersistence.SaveToFile(clay, Board());
        var (_, panel) = Open(LayoutPersistence.LoadFromFile(clay), tech: null);   // it belongs to no workspace, so no technology resolves

        var nets = JsonDocument.Parse(Cli("explain", clay, "--nets", "--json").Out).RootElement
            .GetProperty("result").GetProperty("explain").GetProperty("nets").EnumerateArray().ToList();

        Assert.Equal(panel.Rows.Select(r => r.Name), nets.Select(n => n.GetProperty("name").GetString()));
        Assert.Equal(panel.Rows.Select(r => r.ShapeCount), nets.Select(n => n.GetProperty("shapes").GetInt32()));
        Assert.Equal(panel.Rows.Select(r => r.LayersText),
                     nets.Select(n => string.Join(", ", n.GetProperty("layers").EnumerateArray().Select(l => l.GetString()))));
    }

    [Fact]
    public void ADocumentKindWithNoLayout_IsRefusedByName()
    {
        string cnl = Path.Combine(_dir, "a.cnl");
        File.WriteAllText(cnl, "R:R1 a 0 R=50 Ohm\n");

        var run = Cli("explain", cnl, "--nets", "--json");

        Assert.Equal(1, run.Exit);
        Assert.Contains("explain.option.wrong-kind", run.Out);
    }

    [Fact]
    public void Highlighting_IsOverlayState_NotTheSelection_AndNoPictureOfTheLayoutCarriesIt()
    {
        var (vm, panel) = Open(Board());
        var tech = vm.Technology!;
        byte[] before = Draw(vm.Model, tech, null);

        panel.SetSelectedRows([panel.Rows[0]]);

        Assert.Equal("N2", Assert.Single(vm.Overlay!.NetHighlights).Net);
        Assert.Empty(vm.SelectedIndices);
        Assert.False(vm.UndoRedo.CanUndo);
        Assert.False(vm.IsDirty);
        Assert.Equal(before, Draw(vm.Model, tech, null));
        Assert.NotEqual(before, Draw(vm.Model, tech, vm.Overlay));
        Assert.NotEqual(ColorTheme.BuiltIn.Resolve(ColorRole.LayoutNetHighlight, ColorVariant.Light),
                        ColorTheme.BuiltIn.Resolve(ColorRole.LayoutNetHighlight, ColorVariant.Dark));

        panel.IsShown = false;
        Assert.Empty(vm.Overlay!.NetHighlights);
    }

    [Fact]
    public void MovingThroughRows_ScansNoShape_AndAFrameDrawsPerLayerNotPerShape()
    {
        var view = new LayoutView();
        for (int i = 0; i < 3000; i++) view.Shapes.Add(Rect("BIG", Top, i * 2000));
        view.Shapes.Add(Rect("SMALL", Top, -5000));
        view.NotifyChanged();
        var (vm, panel) = Open(view);
        var big = panel.Rows.Single(r => r.Name == "BIG");
        var small = panel.Rows.Single(r => r.Name == "SMALL");
        int builds = panel.ListBuilds;

        using var bitmap = new SKBitmap(200, 100);
        using var canvas = new SKCanvas(bitmap);
        var vp = new LayoutViewport(-1000, -1000, 200.0 / 6_000_000, 200, 100);
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.FromTheme(ColorTheme.BuiltIn, ColorVariant.Light) };
        LayoutRenderResult Frame() => LayoutRenderer.Draw(canvas, view, vm.Technology, vp, opts with { Overlay = vm.Overlay });

        panel.SetSelectedRows([big]);
        Assert.Equal(1, Frame().NetHighlightDrawCalls);
        var geometry = Assert.Single(vm.Overlay!.NetHighlights);

        panel.DimOtherCopper = true;
        Assert.Equal(2, Frame().NetHighlightDrawCalls);    // the fill and the outline, not 3,000 of either
        panel.DimOtherCopper = false;

        panel.SetSelectedRows([small]);
        Frame();
        panel.SetSelectedRows([big]);
        Frame();

        Assert.Same(geometry, Assert.Single(vm.Overlay!.NetHighlights));
        Assert.Equal(1, geometry.GeometryBuilds);
        Assert.Equal(builds, panel.ListBuilds);
    }

    [Fact]
    public void AHiddenLayer_IsNotOutlined()
    {
        var (vm, panel) = Open(new LayoutView { Shapes = { Rect("N", Top, 0), Rect("N", Bottom, 2000) } });
        panel.SetSelectedRows([panel.Rows[0]]);
        vm.LayerRows.Single(r => r.Name == "Bottom").Visible = false;

        using var bitmap = new SKBitmap(200, 100);
        using var canvas = new SKCanvas(bitmap);
        var frame = LayoutRenderer.Draw(canvas, vm.Model, vm.DrawTechnology, new LayoutViewport(-1000, -1000, 0.01, 200, 100),
            new LayoutRenderOptions { Theme = LayoutRenderTheme.FromTheme(ColorTheme.BuiltIn, ColorVariant.Light), Overlay = vm.Overlay });

        Assert.Equal(1, frame.NetHighlightDrawCalls);
    }

    [Fact]
    public void ADocumentChange_BuildsTheListOnce_AndKeepsTheSelectionByName()
    {
        var (vm, panel) = Open(Board());
        var posted = new List<Action>();
        panel.PostBuild = posted.Add;
        panel.SetSelectedRows([panel.Rows[0]]);
        int builds = panel.ListBuilds;

        vm.SetNetOnShapes([vm.Model.Shapes[4]], "N2");
        vm.Model.NotifyChanged();
        var job = Assert.Single(posted);
        job();

        Assert.Equal(builds + 1, panel.ListBuilds);
        Assert.Equal(3, panel.Rows[0].ShapeCount);
        Assert.Equal("N2", Assert.Single(panel.SelectedRows).Name);
        Assert.Equal(3, Assert.Single(vm.Overlay!.NetHighlights).Shapes.Count);
    }

    [Fact]
    public void AFilterNarrowsBySubstring_AndAHiddenRowLeavesTheSelection()
    {
        var (_, panel) = Open(Board());
        panel.SetSelectedRows([panel.Rows[0]]);

        panel.FilterText = "n1";

        Assert.Equal("N10", Assert.Single(panel.Rows).Name);
        Assert.Empty(panel.SelectedRows);
        Assert.Contains("1 of 2", panel.HeaderText);
    }

    [Fact]
    public void ZoomFramesTheNet_AndSelectShapesLeavesOutWhatAClickCouldNotSelect()
    {
        var view = new LayoutView { Shapes = { Rect("N", Top, 0), Rect("N", Top, 4000), Rect("N", Bottom, 90_000) } };
        var (vm, panel) = Open(view);
        vm.LayerRows.Single(r => r.Name == "Bottom").Visible = false;
        var framed = Bbox.Empty;
        vm.ZoomToRegionRequested += box => framed = box;
        panel.SetSelectedRows([panel.Rows[0]]);

        panel.ZoomToNetCommand.Execute(null);
        panel.SelectShapesCommand.Execute(null);

        Assert.Equal(new Bbox(0, 0, 5000, 1000), framed);
        Assert.Equal([0, 1], vm.SelectedIndices);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void DimmedCopper_StaysSolidWhereTwoShapesOfTheNetOverlap()
    {
        // One ring each way round. Under a winding fill the two cancel where they overlap unless each is added with one winding.
        var view = new LayoutView
        {
            Shapes =
            {
                new PolygonShape { Layer = Top, Net = "N", Xy = [0, 0, 6000, 0, 6000, 6000, 0, 6000] },
                new PolygonShape { Layer = Top, Net = "N", Xy = [3000, 0, 3000, 6000, 9000, 6000, 9000, 0] },
            },
        };
        var (vm, panel) = Open(view);
        panel.DimOtherCopper = true;
        panel.SetSelectedRows([panel.Rows[0]]);

        using var bitmap = new SKBitmap(200, 100);
        using var canvas = new SKCanvas(bitmap);
        var vp = new LayoutViewport(0, 0, 0.02, 200, 100);
        LayoutRenderer.Draw(canvas, view, vm.Technology, vp, new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.FromTheme(ColorTheme.BuiltIn, ColorVariant.Light), Overlay = vm.Overlay,
        });

        var only = bitmap.GetPixel(30, 50);
        var both = bitmap.GetPixel(90, 50);
        Assert.Equal(only, both);
    }

    private static (int Exit, string Out) Cli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        string cliDir = typeof(NetsPanelTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        stderr.GetAwaiter().GetResult();
        return (proc.ExitCode, stdout.GetAwaiter().GetResult());
    }
}

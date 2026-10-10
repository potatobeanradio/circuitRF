// brief-img-6-entry-points.md §4 — a placed picture, right-clicked: a layout bitmap offers Trace Image into This
// Layout… with As placed and this layout's technology; an unresolved bitmap offers neither row; a schematic bitmap's
// layout row offers no As placed.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Recognition;
using CircuitRF.Ui.Tests.Imaging;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

[Collection(ImageEntryPointsCollection.Name)]
public sealed class BitmapContextRowsTests : IDisposable
{
    private const long DbuPerPixel = 25_400;   // 25.4 µm a pixel at the default 1000 DBU/µm
    private readonly string _ws = Directory.CreateTempSubdirectory("crf-im6-rows-").FullName;
    private readonly string _cell;

    public BitmapContextRowsTests()
    {
        _cell = Directory.CreateDirectory(Path.Combine(_ws, "board")).FullName;
        File.WriteAllBytes(Path.Combine(_cell, "board.png"), Pictures.Png(TracePictures.TwoLayerBoard()));
    }

    public void Dispose()
    {
        try { Directory.Delete(_ws, true); } catch { /* best effort */ }
    }

    private static BitmapShape Bitmap(string reference) => new()
    {
        ImagePathRef = reference, X = 0, Y = 0, W = 400 * DbuPerPixel, H = 300 * DbuPerPixel, Locked = true,
    };

    [Fact]
    public async Task ALayoutBitmap_OffersTraceIntoThisLayout_AsPlaced_WithThisLayoutsTechnology()
    {
        // Two technologies; the workspace names none, so the first would be chosen — the layout names the other.
        var entry = ShippedTechnologies.All.First(e => e.Id == TracePictures.TechId);
        File.WriteAllText(Path.Combine(_ws, "aaa.ctech"), ShippedTechnologies.LoadRawJson(entry));
        File.WriteAllText(Path.Combine(_ws, "board.ctech"), ShippedTechnologies.LoadRawJson(entry));
        File.WriteAllText(Path.Combine(_ws, ".cws"), "{}");
        string clay = Path.Combine(_cell, "board.clay");
        var bitmap = Bitmap("board.png");   // locked: locking stops dragging, not reading
        LayoutPersistence.SaveToFile(clay, new LayoutView { TechRef = "../board.ctech", Shapes = { bitmap } });

        var rows = ImageEntryPoints.LayoutBitmapRows(clay, bitmap);
        Assert.Equal([ImageEntryPoints.TraceIntoLayoutHeader, ImageEntryPoints.CreateSchematicHeader], rows.Select(r => r.Header));
        var trace = rows[0];
        Assert.True(trace.IntoPlaced);
        Assert.False(trace.MakeSchematic);

        var workspace = new WorkspaceViewModel { CurrentWorkspacePath = Path.Combine(_ws, ".cws") };
        var source = ImageEntryPoints.SourceOf(clay, bitmap).Source!;
        var (choices, preferred) = workspace.ImageTechnologies(_ws, source);
        Assert.Equal(Path.Combine(_ws, "board.ctech"), choices[preferred].Path);

        var vm = new ImageSourceViewModel(_ws, choices, preferred, source, trace.MakeSchematic,
                                          new RecordingImageRunner(DrawingKind.Layout), debounce: TimeSpan.Zero,
                                          presetDirectory: Path.Combine(_ws, "presets"));
        await vm.Recognition;

        Assert.True(vm.TargetOwn);   // this layout, over the picture
        Assert.Contains(vm.Scale.Choices, c => c.Label == "As placed");
        Assert.Equal("As placed", vm.Scale.SelectedChoice?.Label);
    }

    [Fact]
    public void AnUnresolvedBitmap_OffersNeitherRow()
    {
        string clay = Path.Combine(_cell, "board.clay");
        Assert.Empty(ImageEntryPoints.LayoutBitmapRows(clay, Bitmap("moved-away.png")));
        Assert.Empty(ImageEntryPoints.LayoutBitmapRows(clay, Bitmap("")));
        Assert.Empty(ImageEntryPoints.SchematicBitmapRows(Path.Combine(_cell, "board.csch"), new EditableBitmap { ImagePath = "moved-away.png" }));
    }

    [Fact]
    public async Task ASchematicBitmapsLayoutRow_OffersNoAsPlaced()
    {
        string csch = Path.Combine(_cell, "board.csch");
        var bitmap = new EditableBitmap { ImagePath = "board.png", Width = 400, Height = 300 };

        var rows = ImageEntryPoints.SchematicBitmapRows(csch, bitmap);
        Assert.Equal([ImageEntryPoints.CreateSchematicHeader, ImageEntryPoints.CreateLayoutHeader], rows.Select(r => r.Header));
        var layout = rows.Single(r => !r.MakeSchematic);
        Assert.False(layout.IntoPlaced);

        var vm = ImageDialog.Open(_ws, new RecordingImageRunner(DrawingKind.Layout), ImageEntryPoints.SourceOf(csch, bitmap).Source!,
                                  layout.MakeSchematic);
        await vm.Recognition;

        Assert.False(vm.OwnTargetOffered);
        Assert.DoesNotContain(vm.Scale.Choices, c => c.Label == "As placed");
    }
}

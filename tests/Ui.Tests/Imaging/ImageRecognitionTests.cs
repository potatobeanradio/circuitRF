// brief-img-4-layout-image-to-schematic.md §4 — a layout picture made into a schematic through the unchanged artwork
// recognition (R-im4-1 … R-im4-6).
//
// The picture is drawn in memory from AS-9's round-trip board as a fabricator receives it (ArtworkRoundTripBoard: the
// Gerber set imported into a fresh workspace) — its top copper and its drills only, at 20 µm/px, over the board's extent,
// so the Bottom plane is in no pixel. Read with the board's own technology, it must come back as the same circuit the
// board's artwork reads as — with the plane implied rather than drawn.
//
// Like for like: the artwork is read from what the picture states — its copper, its vias and its plane, but not its
// solder mask. AS-4 reads a land pattern from the mask's openings where the technology has a mask, and from the copper
// where it has none; and a part standing ON its line (L1, its first pad centred on the trace) is a pad in a line, which
// only an opening shows. A picture of the copper alone states no such pad, so neither reading finds that part, and the
// board's via to ground under it stays a stray (src/Design/Layout/Recognition/RESOLVED.md, IM-4).

using System;
using System.IO;
using System.Linq;
using Clipper2Lib;
using CircuitRF.Core.Design;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Tests.Recognition;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class ImageRecognitionTests(ArtworkRoundTripBoard board) : IClassFixture<ArtworkRoundTripBoard>, IDisposable
{
    private const double MetresPerPixel = 20e-6;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-imgrec-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>The board's top copper (via pads included) and its drill holes, drawn at <see cref="MetresPerPixel"/>.</summary>
    private static RasterImage TopAndDrills(TraceImpedanceAnalysis.LayoutSource board)
    {
        var tech = board.Technology!;
        var top = Conductors.Of(tech)[0].DrawingLayers.ToHashSet();
        double dbuPerPx = MetresPerPixel * 1e6 * board.View.DbuPerMicron;
        var copper = new Paths64();
        var everything = new Paths64();
        var drills = board.Shapes.OfType<ViaShape>().ToList();
        foreach (var s in board.Shapes.Where(s => s is not (ViaShape or LabelShape or BitmapShape)))
        {
            var paths = LayoutClipper.ToClipperPaths(s, 1000);
            everything.AddRange(paths);
            if (top.Contains(s.Layer)) copper.AddRange(paths);
        }
        copper = Clipper.Union(copper, FillRule.NonZero);
        var box = Clipper.GetBounds(everything);
        const int margin = 0;
        int w = (int)Math.Ceiling(box.Width / dbuPerPx) + 2 * margin, h = (int)Math.Ceiling(box.Height / dbuPerPx) + 2 * margin;
        float X(double x) => (float)((x - box.left) / dbuPerPx + margin);
        float Y(double y) => (float)((box.bottom - y) / dbuPerPx + margin);

        return Pictures.Draw(w, h, c =>
        {
            using var cu = Pictures.Fill(new SKColor(0xc8, 0x75, 0x33));
            using var hole = Pictures.Fill(SKColors.Black);
            using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
            foreach (var ring in copper)
            {
                path.MoveTo(X(ring[0].X), Y(ring[0].Y));
                foreach (var p in ring.Skip(1)) path.LineTo(X(p.X), Y(p.Y));
                path.Close();
            }
            c.DrawPath(path, cu);
            foreach (var v in drills) c.DrawCircle(X(v.X), Y(v.Y), (float)(v.PadSize / 2.0 / dbuPerPx), cu);
            foreach (var v in drills) c.DrawCircle(X(v.X), Y(v.Y), (float)(v.DrillSize / 2.0 / dbuPerPx), hole);
        });
    }

    private static string Signature(PartRow r) => $"{r.Kind}/{r.Case?.Code}/{r.Connection}";

    [Fact]
    public void ThePicture_ReadsAsTheBoardsArtworkDoes_WithThePlaneImplied_AndTheCellHoldsBothViewsOverThePicture()
    {
        var source = TraceImpedanceAnalysis.LoadLayout(board.ImportedClay);
        var stated = TechPersistence.Clone(source.Technology!);
        var stackup = stated.Stackup.Layers.SelectMany(l => l.DrawingLayers).ToHashSet();
        stated.Layers = [.. stated.Layers.Where(l => stackup.Contains(l.Key))];
        var artwork = ArtworkRecognition.Circuit(RecognitionInput.FromFile(board.ImportedClay) with { Technology = stated });
        Assert.True(artwork.Circuit is not null, artwork.Result.Refusal);

        var picture = ImageSource.FromBytes(Pictures.Png(TopAndDrills(source)), "board.png").Source!;
        Directory.CreateDirectory(_dir);
        var run = ImageRecognition.Run(new ImageRecognitionInput
        {
            Trace = new ImageTraceInput
            {
                Source = picture, Technology = source.Technology, TechnologyPath = source.TechnologyPath,
                Scale = ImageScaleStatement.Stated(MetresPerPixel),
            },
        }, ImageRecognitionTarget.NewCell(_dir, "Board_pic"));
        Assert.True(run.Ok, run.Refusal + "\n" + string.Join("\n", run.Report.Lines()));

        // The same circuit: every element by type, every part by kind, case and connection.
        static string Counts(TestBench tb) =>
            string.Join(" ", tb.Instances.GroupBy(i => i.Reference).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}×{g.Count()}"));
        Assert.Equal(Counts(artwork.Circuit!.TestBench), Counts(run.Reading.Circuit!.TestBench));
        Assert.Equal(artwork.Result.Parts.Rows.Select(Signature).Order(), run.Reading.Recognition!.Parts.Rows.Select(Signature).Order());

        // Ground: the stackup's plane, which the picture does not draw — implied, and said so once.
        Assert.Equal(GroundSource.ReferenceConductor, artwork.Result.Board!.Ground.Source);
        Assert.Equal(GroundSource.UndrawnReference, run.Reading.Recognition.Board!.Ground.Source);
        Assert.Equal(1, run.Report.Count(RecognitionFindingClass.ImageReferenceImplied));
        // The drill circle is a via to the implied plane — AS D7's VIAGND, as the drawn plane's via is.
        Assert.Equal(artwork.Result.Board.Vias.Select(v => v.Element), run.Reading.Recognition.Board.Vias.Select(v => v.Element));
        Assert.Contains(run.Reading.Recognition.Board.Vias, v => v.Element == ViaElement.ViaGnd);
        // One report: the trace's lines, then the recognition's.
        var classes = run.Report.Findings.Select(f => f.Class).ToList();
        Assert.True(classes.IndexOf(RecognitionFindingClass.ImageScaleChosen) < classes.IndexOf(RecognitionFindingClass.GroundChosen));

        // One cell, both views, both on the picture; each carries the ImageSource block, and the schematic names the
        // traced layout as its artwork.
        string cell = Path.Combine(_dir, "Board_pic");
        Assert.Equal(cell, run.CellDir);
        Assert.StartsWith(cell, run.LayoutPath);
        Assert.StartsWith(cell, run.SchematicPath);
        var clay = LayoutPersistence.LoadFromFile(run.LayoutPath!);
        var under = Assert.Single(clay.Shapes.OfType<BitmapShape>());
        Assert.True(under.Locked);
        Assert.Equal(ImageTrace.UnderlayOpacity, under.Opacity, 6);
        Assert.NotNull(clay.ImageSource);

        var (csch, _, _) = SchematicPersistence.LoadFromFile(run.SchematicPath!);
        var behind = Assert.Single(csch.CanvasObjects.OfType<EditableBitmap>());
        Assert.True(behind.IsLocked);
        Assert.Equal(1 - ImageTrace.UnderlayOpacity, behind.Transparency, 6);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(run.SchematicPath)!, behind.ImagePath)));
        Assert.Equal(clay.ImageSource!.Sha256, csch.ImageSource!.Sha256);
        Assert.Equal(Path.GetFullPath(run.LayoutPath!),
                     Path.GetFullPath(Path.Combine(Path.GetDirectoryName(run.SchematicPath)!, csch.ArtworkSource!.Layout)));
    }
}

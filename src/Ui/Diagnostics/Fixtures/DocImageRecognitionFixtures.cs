using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Clipper2Lib;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Recognition;
using SkiaSharp;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// Create Schematic / Layout from Image (brief-img-5-dialog.md) — the dialog empty, reading a layout picture, waiting
/// for its scale, and showing a picture that holds no drawing.
/// </summary>
/// <remarks>
/// <b>The picture is drawn in memory</b> from the shipped <c>Artwork to Schematic</c> example's board — its top copper
/// and its drill holes, at <see cref="MetresPerPixel"/> — so no picture file is committed and every platform reads the
/// same pixels. <b>The reading is the real one</b> (<see cref="ImageRecognitionRunner"/>), awaited before the capture,
/// and the scale is stated the way a user states it: Two points…, two clicks across the picture, and its width typed.
///
/// <para>The window's <c>Styles</c> are carried across onto its detached content, as <see cref="DocRecognitionFixtures"/>
/// does.</para>
/// </remarks>
public static class DocImageRecognitionFixtures
{
    private const string ExampleFolder = "Artwork to Schematic";
    private const double MetresPerPixel = 20e-6;

    public static FigureScene Empty() => Scene(Open(null, makeSchematic: true, out _));

    /// <summary>The board's picture, made into a schematic: the overlay and the parts table filled.</summary>
    public static FigureScene LayoutPicture()
    {
        var vm = Open(BoardPicture(out int width), makeSchematic: true, out _);
        vm.Recognition.GetAwaiter().GetResult();
        // A sparse board of one trace width reads to Auto as a schematic drawing (IM-2's vote: little ink, one stroke
        // width); the figure shows the user's one click that answers it.
        vm.KindIndex = 2;
        vm.Recognition.GetAwaiter().GetResult();
        StateWidth(vm, width);
        if (vm.Rows.Count == 0)
            throw new InvalidOperationException($"Reading the board's picture produced no parts ({vm.Status}), so the figure would show an empty table.");
        vm.SelectedRow = vm.Rows.FirstOrDefault(r => r.Refdes.StartsWith("C", StringComparison.Ordinal)) ?? vm.Rows[0];
        return Scene(vm);
    }

    /// <summary>The same picture, Make Layout, before any scale is stated: the scale row asks.</summary>
    public static FigureScene NeedsScale()
    {
        var vm = Open(BoardPicture(out _), makeSchematic: false, out _);
        vm.Recognition.GetAwaiter().GetResult();
        vm.KindIndex = 2;
        vm.Recognition.GetAwaiter().GetResult();
        if (!vm.Scale.NeedsScale)
            throw new InvalidOperationException("The board's picture found a scale on its own, so the figure would not show the scale row asking.");
        vm.AdvancedExpanded = true;
        return Scene(vm);
    }

    /// <summary>A photograph-like picture: dimmed, one sentence, the two links.</summary>
    public static FigureScene NoDrawing()
    {
        var vm = Open(Photograph(), makeSchematic: true, out _);
        vm.Recognition.GetAwaiter().GetResult();
        if (!vm.HasFailure)
            throw new InvalidOperationException($"The photograph was read as a drawing ({vm.ReadKind?.Kind}), so the figure would not show the failure.");
        return Scene(vm);
    }

    private static ImageSourceViewModel Open(ImageSource? source, bool makeSchematic, out string clay)
    {
        string root = ExampleWorkspaces.ResolveRoot()
            ?? throw new InvalidOperationException("No examples/ tree beside the generator or above it, so the Create from Image figures have no technology.");
        string ws = Path.Combine(root, ExampleFolder);
        clay = Path.Combine(ws, "Board", "Board", "layout", "Board.clay");
        string tech = RecognitionInput.FromFile(clay).TechnologyPath
            ?? throw new InvalidOperationException("The example board names no technology.");
        // The presets are the generator's own empty folder: the user's would show in a committed figure.
        string presets = Path.Combine(Path.GetTempPath(), "crf-docgen-image-presets");
        return new ImageSourceViewModel(ws, [new ImageTechnologyChoice(Path.GetFileNameWithoutExtension(tech), tech)], 0,
                                        source, makeSchematic, ImageRecognitionRunner.Instance, debounce: TimeSpan.Zero,
                                        presetDirectory: presets);
    }

    /// <summary>Two points… across the picture's full width, and that width typed — the user's way of stating a scale.</summary>
    private static void StateWidth(ImageSourceViewModel vm, int widthPx)
    {
        var scale = vm.Scale;
        scale.SelectedChoice = scale.Choices.First(c => c.Kind == ImageScaleChoiceKind.TwoPoints);
        scale.Click(new PixelPoint(0, 0));
        scale.Click(new PixelPoint(widthPx, 0));
        scale.DistanceText = $"{(widthPx * MetresPerPixel * 1e3).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} mm";
        scale.ApplyDistanceCommand.Execute(null);
        vm.Recognition.GetAwaiter().GetResult();
    }

    private static FigureScene Scene(ImageSourceViewModel vm)
    {
        var window = new CreateSchematicFromArtworkDialog(vm);
        var content = window.Content as Control
            ?? throw new InvalidOperationException("CreateSchematicFromArtworkDialog has no content control to capture.");
        var styles = window.Styles.ToList();
        window.Content = null;
        window.Styles.Clear();
        foreach (var style in styles) content.Styles.Add(style);
        content.DataContext = vm;
        return new FigureScene(new Panel { Children = { content } });
    }

    // ── pictures ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The example board's top copper (via pads included) and its drill holes, drawn on a green board as a board
    /// viewer draws them.</summary>
    private static ImageSource BoardPicture(out int width)
    {
        string root = ExampleWorkspaces.ResolveRoot()!;
        var input = RecognitionInput.FromFile(Path.Combine(root, ExampleFolder, "Board", "Board", "layout", "Board.clay"));
        var tech = input.Technology!;
        var top = Conductors.Of(tech)[0].DrawingLayers.ToHashSet();
        double dbuPerPx = MetresPerPixel * 1e6 * input.View.DbuPerMicron;
        var copper = new Paths64();
        var everything = new Paths64();
        var drills = input.Shapes.OfType<ViaShape>().ToList();
        foreach (var s in input.Shapes.Where(s => s is not (ViaShape or LabelShape or BitmapShape)))
        {
            var paths = LayoutClipper.ToClipperPaths(s, 1000);
            everything.AddRange(paths);
            if (top.Contains(s.Layer)) copper.AddRange(paths);
        }
        copper = Clipper.Union(copper, FillRule.NonZero);
        var box = Clipper.GetBounds(everything);
        const int margin = 12;
        int w = (int)Math.Ceiling(box.Width / dbuPerPx) + 2 * margin, h = (int)Math.Ceiling(box.Height / dbuPerPx) + 2 * margin;
        float X(double x) => (float)((x - box.left) / dbuPerPx + margin);
        float Y(double y) => (float)((box.bottom - y) / dbuPerPx + margin);
        width = w;

        return Png(w, h, c =>
        {
            c.Clear(new SKColor(0x1f, 0x4e, 0x36));
            using var cu = Fill(new SKColor(0xd8, 0x8a, 0x48));
            using var hole = Fill(SKColors.Black);
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
        }, "board.png");
    }

    /// <summary>Graded colour with grain and no line work — what a photograph looks like to the kind reader. The grain is
    /// a fixed sequence, so every run draws the same pixels.</summary>
    private static ImageSource Photograph() => Png(480, 320, c =>
    {
        using var shader = SKShader.CreateRadialGradient(new SKPoint(170, 130), 300,
            [new SKColor(0xf2, 0xc1, 0x8c), new SKColor(0x8a, 0x5a, 0x7c), new SKColor(0x2d, 0x3e, 0x63)], SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader, IsAntialias = true };
        c.DrawRect(0, 0, 480, 320, paint);
        uint seed = 0x2545F491;
        using var grain = new SKPaint { IsAntialias = false };
        for (int y = 0; y < 320; y++)
            for (int x = 0; x < 480; x++)
            {
                seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                grain.Color = new SKColor(0, 0, 0, (byte)(seed % 48));
                c.DrawPoint(x, y, grain);
            }
    }, "photo.png");

    private static SKPaint Fill(SKColor colour) => new() { Color = colour, IsAntialias = true, Style = SKPaintStyle.Fill };

    private static ImageSource Png(int w, int h, Action<SKCanvas> draw, string name)
    {
        using var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(SKColors.White);
            draw(c);
        }
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return ImageSource.FromBytes(data.ToArray(), name).Source
               ?? throw new InvalidOperationException($"The fixture picture '{name}' could not be read back.");
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Diagnostics;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Theming;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// <b>SVG that LEAVES the application carries the same Skia defect the documentation figures did</b>
/// (owner-reported 2026-08-21; see <c>src/Ui/RESOLVED.md</c>). Skia's SVG device writes each text
/// run's per-glyph <c>x</c>/<c>y</c> list with a separator after the last entry, which is invalid;
/// Gecko drops the whole attribute and draws every run at the element origin, a line above its
/// baseline, where the clip removes it. Illustrator, Inkscape, Chrome and Safari accept it — so an
/// exported plot or a pasted schematic looks right everywhere except Firefox.
///
/// <para>These drive the REAL export seams, not the repair function, and each one first proves that
/// raw Skia does emit the defect — otherwise a test asserting its absence passes vacuously the day
/// Skia changes, and stops guarding anything.</para>
/// </summary>
public class SvgExportPositionListTests(ITestOutputHelper output)
{
    /// <summary>An <c>x</c> or <c>y</c> attribute whose value ends in a separator.</summary>
    private static readonly Regex TrailingSeparator = new(@"\s(?:x|y)\s*=\s*""[^""]*[,\s]""");

    private static void AssertRepaired(string svg, string what)
    {
        Assert.Contains("<text", svg, StringComparison.Ordinal);
        var offender = TrailingSeparator.Match(svg);
        Assert.False(offender.Success,
            $"{what} still carries Skia's trailing separator on a per-glyph position list "
          + $"(\"{offender.Value.Trim()}\"), so its text is unreadable in Firefox.");
    }

    // ── The vacuity guard ─────────────────────────────────────────────────────

    /// <summary>
    /// Raw <see cref="SKSvgCanvas"/>, with nothing of ours in the way, DOES write the trailing
    /// separator. Every assertion below is meaningless without this: they all assert an absence.
    /// </summary>
    [Fact]
    public void RawSkiaStillEmitsTheDefect_SoTheAssertionsBelowAreNotVacuous()
    {
        string raw = RawSkiaSvg();
        output.WriteLine(FirstTextRun(raw));

        Assert.Contains("<text", raw, StringComparison.Ordinal);
        Assert.True(TrailingSeparator.IsMatch(raw),
            "Skia no longer writes a trailing separator on its position lists. That is good news, but "
          + "every test in this file now asserts an absence that is true for the wrong reason — "
          + "re-point them, or retire the repair.");
    }

    // ── The export seams ──────────────────────────────────────────────────────

    /// <summary>Data Display's SVG export (the "Export SVG" command's own builder).</summary>
    [Fact]
    public void PlotExportSvg_HasNoTrailingSeparator()
    {
        string svg = PlotExporter.BuildSvgString(DrawSomeText);
        output.WriteLine(FirstTextRun(svg));
        AssertRepaired(svg, "An exported plot");
    }

    /// <summary>The layout clipboard's SVG flavour, driven through its real render helper.</summary>
    [Fact]
    public void LayoutClipboardSvg_HasNoTrailingSeparator()
    {
        var tech = new Technology
        {
            Name   = "Test",
            Layers = [new LayerDef { Key = new LayerKey(1, 0), Name = "L1",
                                     Color = new Rgba(200, 30, 30), FillOpacity = 0.5, Visible = true }],
        };

        List<LayoutShape> shapes =
        [
            new RectShape  { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 50_000 },
            new LabelShape { Layer = new LayerKey(1, 0), X = 10_000, Y = 25_000, Height = 12_000, Text = "PORT1" },
        ];

        var result = LayoutClipboard.TryRenderToSvg(shapes, tech, LayoutRenderTheme.Light, transparent: true);

        Assert.NotNull(result);
        output.WriteLine(FirstTextRun(result!.Value.Svg));
        AssertRepaired(result.Value.Svg, "A layout copied as SVG");
    }

    // ── The guard for export paths that do not exist yet ──────────────────────

    /// <summary>
    /// <b>Every place in <c>src/Ui</c> that creates an <see cref="SKSvgCanvas"/> must route its output
    /// through the repair</b> — either <c>SvgFontNormalizer.RepairPositionLists</c> (what leaves the
    /// application) or <c>SvgPostPass.Run</c> (what the documentation generator writes).
    ///
    /// <para>This is the part that protects the future. The two tests above cover the two seams a
    /// headless test can drive; the schematic, symbol and wire-bond writers need a live document to
    /// render, and a seam added next year needs nothing at all to be forgotten. Comments are stripped
    /// first, because one file discusses <c>SKSvgCanvas</c> without creating one.</para>
    /// </summary>
    [Fact]
    public void EverySvgCanvasInTheUiRoutesThroughTheRepair()
    {
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(UiRoot(), "*.cs", SearchOption.AllDirectories))
        {
            string code = StripComments(File.ReadAllText(path));
            if (!code.Contains("SKSvgCanvas.Create", StringComparison.Ordinal)) continue;

            bool repaired = code.Contains("RepairPositionLists", StringComparison.Ordinal)
                         || code.Contains("SvgPostPass.Run", StringComparison.Ordinal);

            output.WriteLine($"{(repaired ? "ok  " : "MISS")} {Path.GetFileName(path)}");
            if (!repaired) offenders.Add(Path.GetFileName(path));
        }

        Assert.NotEmpty(Directory.EnumerateFiles(UiRoot(), "*.cs", SearchOption.AllDirectories));
        Assert.True(offenders.Count == 0,
            "These write SVG with Skia but never repair its per-glyph position lists, so their text "
          + "renders unreadably in Firefox: " + string.Join(", ", offenders)
          + ". Pass the document through SvgFontNormalizer.RepairPositionLists before returning it.");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static void DrawSomeText(SKCanvas canvas)
    {
        canvas.Clear(SKColors.White);
        using var font  = new SKFont(SKTypeface.Default, 12.5f);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        canvas.DrawText("Setup Analyses", 20f, 40f, font, paint);
    }

    private static string RawSkiaSvg()
    {
        using var stream = new SKDynamicMemoryWStream();
        using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, 300, 100), stream))
            DrawSomeText(canvas);
        return Encoding.UTF8.GetString(stream.DetachAsData().ToArray());
    }

    private static string FirstTextRun(string svg)
    {
        var m = Regex.Match(svg, @"<text[^>]*>", RegexOptions.Singleline);
        return m.Success ? m.Value : "(no <text> in the document)";
    }

    private static string StripComments(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline),
                         @"//[^\n]*", "");

    private static string UiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "Could not locate the repository root from the test output directory.");
        return Path.Combine(dir!.FullName, "src", "Ui");
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CircuitRF.Render;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Render;

/// <summary>
/// <b>A dashed rectangle used to export as a SOLID one</b>, and the only sign of it was a line of
/// native console noise — <c>Unsupported path effect in addPaint.</c>, repeated once per shape, with
/// no newline and no indication of which shape or which file (owner-reported, 2026-09-21).
///
/// <para>Skia's SVG device writes <c>drawRect</c>/<c>drawRRect</c>/<c>drawOval</c>/<c>drawCircle</c>
/// out as <c>&lt;rect&gt;</c>/<c>&lt;ellipse&gt;</c>/<c>&lt;circle&gt;</c> elements and then calls
/// <c>addPaint</c> to attach the stroke. A path effect cannot be expressed on a primitive element, so
/// <c>addPaint</c> prints that line and emits the element WITHOUT the effect. <c>drawPath</c> has no
/// such branch — the device flattens the effect into the path's own geometry — which is what
/// <see cref="DashSafeShapes"/> routes through.</para>
///
/// <para>Raster and PDF never had the defect: both devices convert the primitive to a path
/// internally before applying the effect. This is a vector-export repair only.</para>
/// </summary>
public class DashSafeShapesTests(ITestOutputHelper output)
{
    /// <summary>
    /// <b>The dash reaches the SVG through the helper, and is silently lost without it.</b>
    ///
    /// <para>Both halves are asserted for each of the four closed primitives. Proving only the
    /// helper's half would pass vacuously the day Skia starts emitting <c>stroke-dasharray</c> on a
    /// primitive element, and would stop guarding anything.</para>
    /// </summary>
    [Theory]
    [InlineData("rect")]
    [InlineData("roundrect")]
    [InlineData("oval")]
    [InlineData("circle")]
    public void DashSurvivesSvgExportOnlyThroughTheHelper(string shape)
    {
        string bare   = Svg(shape, dashSafe: false);
        string safe   = Svg(shape, dashSafe: true);

        output.WriteLine($"── {shape} — bare ──\n{bare}\n── {shape} — DashSafe ──\n{safe}");

        // The SUBPATH COUNT is the discriminator, not the element name. A bare rect, oval or circle
        // comes out as <rect>/<ellipse>/<circle>; a bare ROUNDED rect comes out as a <path> too,
        // because Skia has no rounded-rect element — but it is the single solid outline, emitted
        // through the same addPaint that drops the effect. Only the flattened form is many disjoint
        // dashes.
        int bareDashes = Subpaths(bare), safeDashes = Subpaths(safe);

        Assert.DoesNotContain("dasharray", bare, StringComparison.Ordinal);
        Assert.True(bareDashes <= 1,
            $"{shape}: the bare primitive kept its dash ({bareDashes} subpaths), so this test no "
          + "longer proves the defect it guards and would pass vacuously.");

        Assert.Contains("<path", safe, StringComparison.Ordinal);
        Assert.True(safeDashes >= 8,
            $"{shape}: expected the dash to be flattened into many subpaths, found {safeDashes}. "
          + "The effect was dropped on the way into the SVG.");
    }

    /// <summary>
    /// <b>No renderer may hand a paint that carries a path effect to a bare closed primitive.</b>
    ///
    /// <para>This is the part that protects the future. The test above proves the helper works; it
    /// cannot notice a call site added next year that does not use it, and the symptom — a stroke
    /// exported solid instead of dashed — is one nobody reads a build log closely enough to
    /// catch.</para>
    ///
    /// <para>The scan is scope-aware: a paint name is live only from its declaration until the brace
    /// depth falls back below it. Without that, five solid paints sharing a common name
    /// (<c>stroke</c>, <c>paint</c>, <c>edge</c>) with a dashed paint elsewhere in the same file read
    /// as offenders. Comments are stripped first, since several of these files DISCUSS the dash they
    /// do not draw.</para>
    /// </summary>
    [Fact]
    public void NoEffectCarryingPaintReachesABareClosedPrimitive()
    {
        var offenders = new List<string>();
        int scanned = 0;

        foreach (string dir in new[] { "Render", "Ui", "Cli", "Design" })
        foreach (string path in Directory.EnumerateFiles(Path.Combine(SrcRoot(), dir), "*.cs",
                                                         SearchOption.AllDirectories))
        {
            string raw = File.ReadAllText(path);
            if (!raw.Contains("PathEffect", StringComparison.Ordinal)) continue;
            scanned++;
            offenders.AddRange(Offenders(StripComments(raw), Path.GetFileName(path)));
        }

        output.WriteLine($"scanned {scanned} files that mention PathEffect");
        Assert.True(scanned > 0, "The scan matched no files at all — it has stopped guarding anything.");
        Assert.True(offenders.Count == 0,
            "These pass a paint carrying an SKPathEffect to a bare DrawRect/DrawRoundRect/DrawOval/"
          + "DrawCircle, so the effect is dropped when the canvas is an SVG one and Skia prints "
          + "\"Unsupported path effect in addPaint.\": " + string.Join("; ", offenders)
          + ". Use the matching DashSafeShapes.Draw*DashSafe extension instead.");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static readonly Regex Primitive = new(@"\.Draw(Rect|Oval|RoundRect|Circle|Region)\s*\(");
    private static readonly Regex DeclaredVar = new(@"\bvar\s+(\w+)\s*=");
    private static readonly Regex DeclaredField = new(@"\bSKPaint\s+(\w+)\s*=");
    private static readonly Regex AssignedEffect = new(@"(\w+)\.PathEffect\s*=\s*(?!null)");

    /// <summary>Every line in <paramref name="code"/> that draws a closed primitive with a paint
    /// that is in scope and is known to carry a path effect.</summary>
    private static IEnumerable<string> Offenders(string code, string file)
    {
        string[] lines = code.Split('\n');
        var live = new Dictionary<string, int>(StringComparer.Ordinal);   // paint name → brace depth
        int depth = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            foreach (string gone in live.Where(kv => depth < kv.Value).Select(kv => kv.Key).ToList())
                live.Remove(gone);

            var decl = DeclaredVar.Match(line);
            if (!decl.Success) decl = DeclaredField.Match(line);
            if (decl.Success)
            {
                // The initializer runs to its closing brace; a paint declared over several lines is
                // the norm here, so the effect is looked for across the whole of it.
                string blob = string.Join('\n', lines.Skip(i).Take(14));
                int end = blob.IndexOf("};", StringComparison.Ordinal);
                if (end >= 0 && blob[..end].Contains("PathEffect", StringComparison.Ordinal)
                             && (blob[..end].Contains("SKPaint", StringComparison.Ordinal)
                              || blob[..end].Contains("new()", StringComparison.Ordinal)))
                    live[decl.Groups[1].Value] = depth;
            }

            var assigned = AssignedEffect.Match(line);
            if (assigned.Success) live[assigned.Groups[1].Value] = depth;

            // A draw call wrapped across lines puts the PAINT on a later one, so the whole call is
            // rejoined before the paint is looked for. Without this the scan's own guarantee is only
            // "no offender that fits on one line", which is not a rule anyone writing code knows.
            if (Primitive.IsMatch(line))
            {
                string call = WholeCall(lines, i);
                foreach (string name in live.Keys)
                    if (Regex.IsMatch(call, @"[,(]\s*(?:\w+\.)?" + Regex.Escape(name) + @"\s*\)"))
                        yield return $"{file}:{i + 1} [{name}]";
            }

            depth += line.Count(c => c == '{') - line.Count(c => c == '}');
        }
    }

    /// <summary>The draw call starting at <paramref name="start"/>, joined across as many following
    /// lines as it takes for its parentheses to balance.</summary>
    private static string WholeCall(string[] lines, int start)
    {
        var sb = new StringBuilder();
        int depth = 0;
        for (int i = start; i < lines.Length && i < start + 6; i++)
        {
            sb.Append(lines[i]);
            depth += lines[i].Count(c => c == '(') - lines[i].Count(c => c == ')');
            if (depth <= 0) break;   // the call's own opening paren is on the first line
        }
        return sb.ToString();
    }

    private static string Svg(string shape, bool dashSafe)
    {
        var rect = new SKRect(10, 10, 190, 190);
        using var stream = new SKDynamicMemoryWStream();
        using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, 200, 200), stream))
        {
            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = SKColors.Red, IsAntialias = true,
                PathEffect = SKPathEffect.CreateDash([6f, 4f], 0f),
            };
            switch (shape)
            {
                case "rect" when dashSafe:      canvas.DrawRectDashSafe(rect, paint); break;
                case "rect":                    canvas.DrawRect(rect, paint); break;
                case "roundrect" when dashSafe: canvas.DrawRoundRectDashSafe(rect, 8f, 8f, paint); break;
                case "roundrect":               canvas.DrawRoundRect(rect, 8f, 8f, paint); break;
                case "oval" when dashSafe:      canvas.DrawOvalDashSafe(rect, paint); break;
                case "oval":                    canvas.DrawOval(rect, paint); break;
                case "circle" when dashSafe:    canvas.DrawCircleDashSafe(100f, 100f, 90f, paint); break;
                case "circle":                  canvas.DrawCircle(100f, 100f, 90f, paint); break;
                default: throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
            }
            paint.PathEffect?.Dispose();
        }
        return Encoding.UTF8.GetString(stream.DetachAsData().ToArray());
    }

    /// <summary>How many disjoint subpaths the SVG's path data holds — one per dash.</summary>
    private static int Subpaths(string svg) => PathData(svg).Count(ch => ch == 'M');

    private static string PathData(string svg)
        => string.Concat(Regex.Matches(svg, @"\sd\s*=\s*""([^""]*)""").Select(m => m.Groups[1].Value));

    private static string StripComments(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    private static string SrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "Could not locate the repository root from the test output directory.");
        return Path.Combine(dir!.FullName, "src");
    }
}

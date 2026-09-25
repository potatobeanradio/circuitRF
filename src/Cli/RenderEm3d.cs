using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Theming;
using CircuitRF.Diagnostics;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using RfCore.Export;
using SkiaSharp;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf render amp.cem -o top.svg --section z=35um</c> — a 3D EM setup as a section or an
/// isometric outline, with no solver installed and no window (brief-em3d-5 R-em3d5-2).
///
/// <para><b>This file draws nothing</b>, on <see cref="Render"/>'s terms: the cut is
/// <c>Em3dSectionScene</c>'s and every pixel is <c>Em3dSectionRenderer</c>'s, both in
/// <c>CircuitRF.Render</c>, written onto the same SVG/PDF/PNG surfaces every other picture goes
/// through (<see cref="VectorPage"/>). What is here is the argument rule, the refusals and the
/// report.</para>
///
/// <para><b>It starts no process</b> (R-em3d5-3d): the problem is generated in this process from the
/// documents, and nothing is meshed.</para>
/// </summary>
internal static class RenderEm3d
{
    /// <summary>What <see cref="Render"/> hands over: the options a 3D picture reads, the first one
    /// typed that it does not, and the verb's own theme resolution and encoder, so neither is
    /// repeated here.</summary>
    internal sealed record Request(
        string Output, string Format, IReadOnlyList<string> Sections, bool Iso, string? Inapplicable,
        int Width, int Height, double Scale, double Margin, bool Transparent, ColorVariant Variant,
        Func<string, (ColorTheme Theme, string Name, string From, int? Refusal)> themeOf,
        Func<int, int, Action<SKCanvas>, byte[]> emit);

    public static int Draw(string path, Request req)
    {
        if (req.Inapplicable is { } option) return JsonRun.Fail(CliDiagnostics.RenderEm3dNotApplicable(option));

        // R-em3d5-2b: exactly one view, refused together rather than ordered — and decided before the
        // file is read, so two incompatible questions get the same answer whether or not it parses.
        var asked = req.Sections.Select(s => $"--section {s}").ToList();
        if (req.Iso) asked.Add("--iso");
        if (asked.Count > 1) return JsonRun.Fail(CliDiagnostics.RenderEm3dMultipleViews(string.Join(" and ", asked)));

        Em3dView? view = null;
        if (req.Sections.Count == 1)
        {
            var (parsed, refusal) = ParseSection(req.Sections[0]);
            if (refusal is { } r) return r;
            view = parsed;
        }
        else if (req.Iso) view = Em3dView.Iso;

        Em3dSetupSource loaded;
        try { loaded = Em3dSetupSource.Load(path); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RenderDocumentUnreadable(path, ex.Message)); }

        // R-em3d5-2a: a planar setup's picture is its layout, so the refusal names it.
        if (!loaded.Setup.Is3D)
            return JsonRun.Fail(CliDiagnostics.RenderEm3dPlanar(
                path, loaded.Resolution.LayoutPath ?? loaded.Setup.LayoutRef));
        if (view is null) return JsonRun.Fail(CliDiagnostics.RenderEm3dViewRequired(path));
        if (loaded.Refusal is { } why) return JsonRun.Fail(CliDiagnostics.RenderEm3dUnbuildable(path, why));

        var generated = loaded.Generated!;
        var problem = generated.Problem!;
        RunHost.Cancellation.ThrowIfCancellationRequested();

        int structural = problem.Validate().Count;
        if (structural > 0)
        {
            var d = CliDiagnostics.RenderEm3dProblems(path, structural);
            Console.Error.WriteLine("note: " + d.Render());
            JsonRun.Note(d);
        }

        var (theme, themeName, themeFrom, themeRefusal) = req.themeOf(loaded.Path);
        if (themeRefusal is { } tr) return tr;

        RunHost.Control?.BeginStage("draw");
        Console.Error.WriteLine("[circuitRF] draw...");

        var scene = Em3dSectionScene.Build(problem, view.Value);
        var style = new Em3dRenderStyle(
            Em3dSectionRenderer.ObjectColours(problem, generated.Origins, loaded.Resolution.Source?.Technology,
                                              theme, req.Variant),
            theme, req.Variant, req.Margin, req.Transparent);

        int pxW = (int)Math.Round(req.Width  * req.Scale);
        int pxH = (int)Math.Round(req.Height * req.Scale);
        byte[] bytes = req.emit(pxW, pxH, canvas => Em3dSectionRenderer.Draw(canvas, pxW, pxH, scene, style));

        // ── write and report ─────────────────────────────────────────────────
        RunHost.Cancellation.ThrowIfCancellationRequested();
        RunHost.Control?.BeginStage("encode");
        Console.Error.WriteLine("[circuitRF] encode...");

        if (bytes.Length == 0)
            return JsonRun.Fail(CliDiagnostics.RenderWriteFailed(req.Output, "the encoder produced no bytes"));
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(req.Output));
            if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
            File.WriteAllBytes(req.Output, bytes);
        }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RenderWriteFailed(req.Output, ex.Message)); }

        var page = Em3dSectionRenderer.Layout(pxW, pxH, scene, req.Margin);
        var (boxLo, boxHi) = Projected(problem.Boundary, view.Value);
        string unitKind = req.Format == "png" ? "device-pixels" : "points";
        bool isIso = view.Value.Kind == Em3dViewKind.Iso;

        JsonRun.AddOutput(req.Format, req.Output);
        JsonRun.Render = new RenderReportJson(
            path, DocumentKinds.Name(DocumentKind.EmSetup), View: null, req.Format,
            new RenderViewportJson(isIso ? "iso" : "section",
                                   scene.FrameMin.U, scene.FrameMin.V, scene.FrameMax.U, scene.FrameMax.V,
                                   "m", 1.0, page.Scale, Letterboxed: false),
            new RenderExtentsJson(boxLo.U, boxLo.V, boxHi.U, boxHi.V, "m", 1.0),
            new RenderSizeJson(pxW, pxH, unitKind, req.Scale),
            new RenderThemeJson(themeName, req.Variant == ColorVariant.Dark ? "dark" : "light", themeFrom),
            Layers: null, Detail: null, Counters: null, bytes.Length,
            Em3d: new RenderEm3dJson(isIso ? "iso" : "section", view.Value.Plane, view.Value.Axis,
                                     isIso ? null : scene.At, "m", 1.0,
                                     [.. scene.Objects], [.. scene.Ports.Select(p => p.Number)]));

        Console.WriteLine($"Wrote {req.Output} ({pxW}x{pxH} {unitKind}, {bytes.Length:N0} bytes)");
        Console.WriteLine($"  {Em3dSectionRenderer.Title(scene)}: {scene.Objects.Count()} object(s), " +
                          $"{scene.Ports.Count} port(s)");
        return 0;
    }

    /// <summary>
    /// <c>z=35um</c>, <c>xz@y=1.2mm</c> or <c>yz@x=…</c>. <b>The length carries an SI unit and a bare
    /// number is a refusal</b> (R-em3d5-2b): nanometres, micrometres and millimetres are three
    /// plausible planes on identical text, exactly as on a layout.
    /// </summary>
    internal static (Em3dView View, int? Refusal) ParseSection(string text)
    {
        int eq = text.IndexOf('=');
        if (eq <= 0) return (default, JsonRun.Fail(CliDiagnostics.RenderEm3dSectionMalformed(text)));

        Em3dViewKind? kind = text[..eq].Trim().ToLowerInvariant() switch
        {
            "z"    => Em3dViewKind.SectionZ,
            "xz@y" => Em3dViewKind.SectionY,
            "yz@x" => Em3dViewKind.SectionX,
            _      => null,
        };
        if (kind is null) return (default, JsonRun.Fail(CliDiagnostics.RenderEm3dSectionMalformed(text)));

        string value = text[(eq + 1)..].Trim();
        if (value.Length == 0 || !char.IsLetter(value[^1]))
            return (default, JsonRun.Fail(CliDiagnostics.RenderEm3dUnitRequired(
                text, $"'{text[..eq]}={value}um' or '{text[..eq]}={value}mm'")));

        // Picometre resolution: the parse is exact decimal arithmetic into an integer, and a
        // picometre is far below anything a section can resolve.
        const int PicometresPerMicron = 1_000_000;
        if (!LayoutUnits.TryParse(value, LayoutUnit.Um, PicometresPerMicron, out long pm))
            return (default, JsonRun.Fail(CliDiagnostics.RenderEm3dSectionMalformed(text)));
        return (new Em3dView(kind.Value, pm * 1e-12), null);
    }

    /// <summary>The air box in the picture's own plane.</summary>
    private static (Uv Lo, Uv Hi) Projected(Em3dAirBox box, Em3dView view)
    {
        switch (view.Kind)
        {
            case Em3dViewKind.SectionZ: return (new Uv(box.Min.X, box.Min.Y), new Uv(box.Max.X, box.Max.Y));
            case Em3dViewKind.SectionY: return (new Uv(box.Min.X, box.Min.Z), new Uv(box.Max.X, box.Max.Z));
            case Em3dViewKind.SectionX: return (new Uv(box.Min.Y, box.Min.Z), new Uv(box.Max.Y, box.Max.Z));
        }
        double u0 = double.PositiveInfinity, v0 = u0, u1 = double.NegativeInfinity, v1 = u1;
        for (int k = 0; k < 8; k++)
        {
            var q = Em3dSectionScene.Project(new Point3((k & 1) == 0 ? box.Min.X : box.Max.X,
                                                        (k & 2) == 0 ? box.Min.Y : box.Max.Y,
                                                        (k & 4) == 0 ? box.Min.Z : box.Max.Z));
            u0 = Math.Min(u0, q.U); v0 = Math.Min(v0, q.V); u1 = Math.Max(u1, q.U); v1 = Math.Max(v1, q.V);
        }
        return (new Uv(u0, v0), new Uv(u1, v1));
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Avalonia.Platform;

namespace CircuitRF.DocGen.Pipeline;

/// <summary>
/// Copies the application's OWN typefaces into <c>docs/user/assets/fonts/</c> on every run.
///
/// <para><b>Why the generator does this rather than a human doing it once.</b> An
/// <see cref="SkiaSharp.SKSvgCanvas"/> emits <c>&lt;text&gt;</c> with a <c>font-family</c> reference,
/// not outlines — so a reader without those faces installed sees substituted letterforms. Extracting
/// the TTFs through the same asset loader the application draws with means the docs' webfonts are
/// literally the same bytes, and cannot drift: there is no copy to remember to refresh.</para>
///
/// <para>(Mitigating detail worth knowing when a substitution does slip through: Skia writes a
/// per-glyph <c>x</c> list, so glyphs stay pinned to their laid-out positions. A substitution changes
/// letterforms, not layout — it looks wrong, it does not collapse.)</para>
///
/// <para>All three families are redistributable and their licences are copied alongside them.</para>
/// </summary>
public static class FontExtractor
{
    /// <summary>One extracted face: where it came from, what it is called, and what it is used for.</summary>
    /// <param name="Asset">The <c>avares://</c> source.</param>
    /// <param name="File">The file written under <c>assets/fonts/</c>.</param>
    /// <param name="Family">The CSS <c>font-family</c> name.</param>
    /// <param name="Weight">CSS <c>font-weight</c>.</param>
    /// <param name="Style">CSS <c>font-style</c>.</param>
    public readonly record struct Face(string Asset, string File, string Family, int Weight, string Style);

    /// <summary>
    /// Exactly the faces circuitRF renders with — Avalonia's chrome font, our Skia canvases' font,
    /// and the plot/scientific font. Adding one here and nowhere else is enough: the CSS is
    /// generated from this list.
    /// </summary>
    public static readonly IReadOnlyList<Face> Faces =
    [
        // Avalonia control chrome. Embedded in the Avalonia.Fonts.Inter package, which is what
        // .WithInterFont() registers — so this is the very font the captured controls drew with.
        new("avares://Avalonia.Fonts.Inter/Assets/Inter-Regular.ttf",  "Inter-Regular.ttf",  "Inter", 400, "normal"),
        new("avares://Avalonia.Fonts.Inter/Assets/Inter-Medium.ttf",   "Inter-Medium.ttf",   "Inter", 500, "normal"),
        new("avares://Avalonia.Fonts.Inter/Assets/Inter-SemiBold.ttf", "Inter-SemiBold.ttf", "Inter", 600, "normal"),
        new("avares://Avalonia.Fonts.Inter/Assets/Inter-Bold.ttf",     "Inter-Bold.ttf",     "Inter", 700, "normal"),

        // Our own Skia canvases: schematic, layout, Smith chart, wBond profile.
        new("avares://CircuitRF.Ui/Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-Regular.ttf",
            "IBMPlexSans-Regular.ttf",  "IBM Plex Sans", 400, "normal"),
        new("avares://CircuitRF.Ui/Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-Italic.ttf",
            "IBMPlexSans-Italic.ttf",   "IBM Plex Sans", 400, "italic"),
        new("avares://CircuitRF.Ui/Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-SemiBold.ttf",
            "IBMPlexSans-SemiBold.ttf", "IBM Plex Sans", 600, "normal"),
        new("avares://CircuitRF.Ui/Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-Bold.ttf",
            "IBMPlexSans-Bold.ttf",     "IBM Plex Sans", 700, "normal"),

        // Plot / scientific text.
        new("avares://CircuitRF.Ui/Assets/Fonts/DejaVuSans.ttf",      "DejaVuSans.ttf",      "DejaVu Sans", 400, "normal"),
        new("avares://CircuitRF.Ui/Assets/Fonts/DejaVuSans-Bold.ttf", "DejaVuSans-Bold.ttf", "DejaVu Sans", 700, "normal"),
        new("avares://CircuitRF.Ui/Assets/Fonts/DejaVuSans-Oblique.ttf",
            "DejaVuSans-Oblique.ttf", "DejaVu Sans", 400, "italic"),
    ];

    /// <summary>The licence files that must travel with the faces above.</summary>
    private static readonly (string Asset, string File)[] Licences =
    [
        ("avares://CircuitRF.Ui/Assets/Fonts/IBM_Plex_Sans/OFL.txt",        "OFL.txt"),
        ("avares://CircuitRF.Ui/Assets/Fonts/DejaVu Fonts License.txt",     "DejaVu Fonts License.txt"),
    ];

    /// <summary>
    /// The families the documentation ALWAYS needs, whatever the figures happen to reference: the
    /// stylesheet sets body text in IBM Plex Sans, so those faces ship even if no figure uses them.
    /// </summary>
    private static readonly string[] AlwaysShip = ["IBM Plex Sans"];

    /// <summary>
    /// Write the needed faces and their licences into <paramref name="fontsDir"/>; returns bytes written.
    ///
    /// <para><b>Only the families the output actually references are shipped</b>, plus the body font.
    /// This is not tidiness: <c>CircuitRF.Ui.csproj</c> copies <c>docs/user/**</c> into the
    /// application output, so every font here is added to the installed size — on top of the same
    /// bytes the app already carries as an Avalonia resource. Shipping all eleven faces
    /// unconditionally costs 4.25 MB, most of it DejaVu, for faces no page may cite.</para>
    /// </summary>
    /// <param name="familiesUsed">Font families named by the emitted figures.</param>
    public static long Extract(string fontsDir, IReadOnlySet<string> familiesUsed, out IReadOnlyList<string> missing)
    {
        Directory.CreateDirectory(fontsDir);
        var absent = new List<string>();
        long bytes = 0;

        var needed = Needed(familiesUsed);
        foreach (var face in needed)
            bytes += Copy(face.Asset, Path.Combine(fontsDir, face.File), absent);

        // A face that used to be needed and is not any more must go, or the directory only ever grows.
        var keep = needed.Select(f => f.File)
                         .Concat(Licences.Select(l => l.File))
                         .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in Directory.EnumerateFiles(fontsDir, "*.ttf"))
            if (!keep.Contains(Path.GetFileName(stale))) File.Delete(stale);

        foreach (var (asset, file) in Licences)
            bytes += Copy(asset, Path.Combine(fontsDir, file), absent);

        missing = absent;
        return bytes;
    }

    /// <summary>The faces to ship: every family a figure cited, plus the body font.</summary>
    public static IReadOnlyList<Face> Needed(IReadOnlySet<string> familiesUsed)
        => Faces.Where(f => AlwaysShip.Contains(f.Family)
                         || familiesUsed.Any(u => u.Equals(f.Family, StringComparison.OrdinalIgnoreCase)
                                               || u.StartsWith(f.Family + " ", StringComparison.OrdinalIgnoreCase)))
                .ToList();

    private static long Copy(string asset, string dest, List<string> absent)
    {
        var uri = new Uri(asset);
        if (!AssetLoader.Exists(uri)) { absent.Add(asset); return 0; }

        using var src = AssetLoader.Open(uri);
        using var dst = File.Create(dest);
        src.CopyTo(dst);
        return dst.Length;
    }

    /// <summary>
    /// The <c>@font-face</c> block the docs stylesheet needs, generated from <see cref="Faces"/> so
    /// a face added above cannot be missing below.
    /// </summary>
    public static string FontFaceCss(IReadOnlySet<string>? familiesUsed = null)
    {
        var faces = familiesUsed is null ? Faces : Needed(familiesUsed);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("/* ---------------------------------------------------------------- [FONTS] --");
        sb.AppendLine("   GENERATED BLOCK - do not hand-edit. Rewritten by tools/DocGen on every run;");
        sb.AppendLine("   see FontExtractor.cs. The .ttf files beside it are extracted from the");
        sb.AppendLine("   application's own assets, so a doc page renders with the exact typefaces the");
        sb.AppendLine("   captured figures were drawn with rather than a browser substitution.");
        sb.AppendLine("   Licences: assets/fonts/OFL.txt (Inter, IBM Plex Sans) and");
        sb.AppendLine("   assets/fonts/DejaVu Fonts License.txt (DejaVu Sans).");
        sb.AppendLine("   ------------------------------------------------------------------------- */");
        foreach (var f in faces)
        {
            sb.AppendLine("@font-face {");
            sb.AppendLine($"  font-family: \"{f.Family}\";");
            sb.AppendLine($"  src: url(\"../fonts/{Uri.EscapeDataString(f.File)}\") format(\"truetype\");");
            sb.AppendLine($"  font-weight: {f.Weight};");
            sb.AppendLine($"  font-style: {f.Style};");
            sb.AppendLine("  font-display: swap;");
            sb.AppendLine("}");
        }
        return sb.ToString();
    }
}

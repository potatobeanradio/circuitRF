using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.DocGen.Pipeline;

namespace CircuitRF.DocGen;

/// <summary>
/// The circuitRF User-Docs Factory: one command regenerates every user-doc figure and every
/// user-doc page from the live application.
///
/// <code>
///   dotnet run --project tools/DocGen -- --out docs/user          figures + fonts + HTML
///   dotnet run --project tools/DocGen -- --slides docs/slides     landscape PDF decks
/// </code>
///
/// <para>Decks are selectable (<c>--deck overview</c>) and themed (<c>--theme dark</c>); both
/// default to everything, because the routine act is regenerating the set and a partial regeneration
/// is the deliberate one.</para>
///
/// <para><b>Never wired into <c>dotnet build</c></b>, on purpose. It stays a deliberate command:
/// it opens a headless application, drives real views and writes into the repository.</para>
/// </summary>
public static class Program
{
    private const string Usage = """
        circuitRF User-Docs Factory

          dotnet run --project tools/DocGen -- --out <docs-dir>        regenerate figures, fonts and pages
          dotnet run --project tools/DocGen -- --slides <out-dir>      regenerate the landscape PDF decks
          dotnet run --project tools/DocGen -- --out <d> --slides <s>  both

        Deck options (with --slides)
          --deck <id>[,<id>]  build only these decks. Default: all of them.
                                overview     Why adopt circuitRF - for an audience deciding whether to.
                                new-user     From first principles, for someone new to circuit simulation.
                                quick-start  The fast path, for an engineer who already uses simulators.
                                reference    The Reference Guide in outline: every chapter, what is in it.
          --theme <t>[,<t>]   light, dark, or both. Default: both.
                              A light deck carries light screenshots and a dark deck dark ones, so
                              this picks the CAPTURES as well as the page colour.

        Options
          --lint-diagnostic   write figures even when the dropped-paint lint fires, so the offending
                              file can be opened. Never use for a real regeneration: the lint is
                              blocking precisely because a wrong figure does not announce itself.

        Examples
          --slides docs/slides                          all four decks, light and dark  (8 PDFs)
          --slides docs/slides --deck overview          the adoption deck, both themes
          --slides docs/slides --deck overview,new-user --theme dark
        """;

    public static int Main(string[] args)
    {
        string? outDir = null, slidesDir = null;
        bool lintDiag = false;
        HashSet<string>? decks = null;
        List<CircuitRF.Ui.Theming.ColorVariant>? variants = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out"    when i + 1 < args.Length: outDir    = args[++i]; break;
                case "--slides" when i + 1 < args.Length: slidesDir = args[++i]; break;
                case "--deck"   when i + 1 < args.Length:
                    decks ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var d in Split(args[++i])) decks.Add(d);
                    break;
                case "--theme"  when i + 1 < args.Length:
                    variants ??= [];
                    foreach (var t in Split(args[++i]))
                    {
                        switch (t.ToLowerInvariant())
                        {
                            case "light": variants.Add(CircuitRF.Ui.Theming.ColorVariant.Light); break;
                            case "dark":  variants.Add(CircuitRF.Ui.Theming.ColorVariant.Dark);  break;
                            case "both":
                                variants.Add(CircuitRF.Ui.Theming.ColorVariant.Light);
                                variants.Add(CircuitRF.Ui.Theming.ColorVariant.Dark);
                                break;
                            default:
                                Console.Error.WriteLine($"Unknown theme '{t}'. Known: light, dark, both.\n");
                                return 2;
                        }
                    }
                    break;
                case "--lint-diagnostic": lintDiag = true; break;
                case "-h" or "--help": Console.WriteLine(Usage); return 0;
                default:
                    Console.Error.WriteLine($"Unrecognised argument '{args[i]}'.\n");
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }

        if (outDir is null && slidesDir is null) { Console.WriteLine(Usage); return 2; }

        // --deck and --theme narrow a deck run. Accepting them on a run that builds no decks would
        // silently do nothing, which is the failure this whole tool is built to refuse.
        if (slidesDir is null && (decks is not null || variants is not null))
        {
            Console.Error.WriteLine("--deck and --theme only mean something with --slides <out-dir>.\n");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        variants = variants?.Distinct().ToList();

        // A figure must not depend on whose machine generated it. WorkspaceViewModel's constructor
        // reads the real preferences file and restores the PDKs installed from it, so the workspace
        // capture carried the generating developer's launch window layout (visibly: the Library panel
        // changed columns), colour scheme and installed kits. Point the per-user state directory at a
        // throwaway one, and every run sees a first-launch installation.
        //
        // FIRST, before anything constructs a view-model or reads a preference.
        string state = Path.Combine(Path.GetTempPath(), "circuitRF-docgen-state");
        if (Directory.Exists(state)) Directory.Delete(state, recursive: true);
        CircuitRF.Ui.AppDataRoot.RedirectTo(state);

        HeadlessHost.Start();
        CircuitRF.Ui.Diagnostics.UiArtworkGenerator.LintDiagnosticMode = lintDiag;

        // Every capture in this process is a figure, so controls that rasterise for live-frame speed
        // draw as geometry instead. See UiArtworkGenerator.HeadlessCapture.
        CircuitRF.Ui.Diagnostics.UiArtworkGenerator.HeadlessCapture = true;

        try
        {
            string docs = outDir ?? DefaultDocsRoot();
            var run = new DocGenRun(docs);
            run.Run(slidesOnly: outDir is null, slidesOut: slidesDir, decks: decks, variants: variants);
            Console.WriteLine(run.Report);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Documentation generation FAILED.\n");
            Console.Error.WriteLine(ex.Message);
            if (ex.InnerException is { } inner && !ReferenceEquals(inner, ex))
                Console.Error.WriteLine("  ---> " + inner.Message);
            return 1;
        }
    }

    /// <summary>A comma- or space-separated list value, e.g. <c>--deck overview,new-user</c>.</summary>
    private static IEnumerable<string> Split(string value)
        => value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Slides-only runs still need the docs root — the Markdown sources and the captured figures both
    /// live under it — so walk up for it rather than making the caller repeat it.
    ///
    /// <para>The marker is <c>circuitRF.slnx</c>, NOT the existence of a <c>docs/user</c> directory.
    /// This project references <c>CircuitRF.Ui</c>, which copies <c>docs/user</c> into its own build
    /// output, so looking for the directory finds
    /// <c>tools/DocGen/bin/Debug/net10.0/docs/user</c> — a bundle copy with no <c>src/</c> in it,
    /// which produced a slides run that reported success and wrote nothing.</para>
    /// </summary>
    private static string DefaultDocsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
                return Path.Combine(dir.FullName, "docs", "user");
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not find the repository root (no circuitRF.slnx walking up from the build output). "
          + "Pass --out explicitly.");
    }
}

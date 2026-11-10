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
///   dotnet run --project tools/DocGen -- --out docs/user          HTML + fonts (figures as committed)
///   dotnet run --project tools/DocGen -- --out docs/user --figures      + figures of pages whose .md changed
///   dotnet run --project tools/DocGen -- --out docs/user --all-figures  + every figure (the CI check)
///   dotnet run --project tools/DocGen -- --slides docs/slides     landscape PDF decks
///   dotnet run --project tools/DocGen -- --page docs/user/src/reference/wbond.md   one page, seconds
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

          dotnet run --project tools/DocGen -- --out <docs-dir>        regenerate pages and fonts (no figures)
          dotnet run --project tools/DocGen -- --out <d> --figures     ...and the figures of pages whose .md changed
          dotnet run --project tools/DocGen -- --out <d> --figure <id> ...and these figures, by name
          dotnet run --project tools/DocGen -- --out <d> --all-figures ...and every figure (minutes; the CI check)
          dotnet run --project tools/DocGen -- --slides <out-dir>      regenerate the landscape PDF decks
          dotnet run --project tools/DocGen -- --out <d> --slides <s>  both
          dotnet run --project tools/DocGen -- --page <src.md>[,...]   only these pages (seconds)

        Figure options (with --out)
          A run captures NO figures unless asked: the pages inline the SVGs already on disk, so
          symbols, figures and toolbars stay byte-for-byte as committed. Capturing drives the live
          application headlessly for every figure and is what makes a full run take minutes.
          --figures           recapture the figures, symbols, glyphs and toolbars cited by a page
                              whose .md changed since the last capture (docs/user/src/
                              _figure-stamps.txt records each page's hash), plus any cited figure
                              missing on disk. A figure only unchanged pages cite is left alone.
          --figure <id>[,<id>] recapture these too, by name, light and dark. An id is a FigureCatalog
                              id (layout-editor), a toolbar as toolbar-<id> (toolbar-layout) or a
                              component symbol by its file stem (atten); repeatable. An unknown id
                              is an error that lists the real ones.
          --all-figures       recapture everything. The only mode that catches a UI change moving a
                              figure whose page did not change, so check-docs-current.sh uses it.
          --mark-figures-current  capture nothing; record every page as captured, for after a full
                              capture made some other way.

        Page options
          --page <p>[,<p>]    regenerate ONLY these pages' HTML, plus the search index. Each is a
                              Markdown source (docs/user/src/reference/wbond.md, or relative to
                              docs/user/src) or an output slug (reference/wbond.html); repeatable.
                              Captures nothing: figures, symbols, toolbars and fonts stay as
                              committed, so a change that moves a figure still needs the full run.
                              Other pages whose output would change too are NAMED, not written.
                              Writes into --out if given, else docs/user.

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
          --rebuild-static    with --figures, also rebuild the figures the catalog marks Static - the antenna
                              patterns, which are committed pictures of a simulation the docs
                              build does not re-run. Each says what it needs if it cannot.

        Examples
          --slides docs/slides                          all four decks, light and dark  (8 PDFs)
          --slides docs/slides --deck overview          the adoption deck, both themes
          --slides docs/slides --deck overview,new-user --theme dark
        """;

    public static int Main(string[] args)
    {
        string? outDir = null, slidesDir = null;
        List<string>? pages = null;
        bool lintDiag = false, rebuildStatic = false, changedFigures = false, allFigures = false, markCurrent = false;
        HashSet<string>? figureIds = null;
        HashSet<string>? decks = null;
        List<CircuitRF.Render.ColorVariant>? variants = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out"    when i + 1 < args.Length: outDir    = args[++i]; break;
                case "--slides" when i + 1 < args.Length: slidesDir = args[++i]; break;
                case "--page"   when i + 1 < args.Length:
                    pages ??= [];
                    pages.AddRange(Split(args[++i]));
                    break;
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
                            case "light": variants.Add(CircuitRF.Render.ColorVariant.Light); break;
                            case "dark":  variants.Add(CircuitRF.Render.ColorVariant.Dark);  break;
                            case "both":
                                variants.Add(CircuitRF.Render.ColorVariant.Light);
                                variants.Add(CircuitRF.Render.ColorVariant.Dark);
                                break;
                            default:
                                Console.Error.WriteLine($"Unknown theme '{t}'. Known: light, dark, both.\n");
                                return 2;
                        }
                    }
                    break;
                case "--figures":     changedFigures = true; break;
                case "--all-figures": allFigures = true; break;
                case "--mark-figures-current": markCurrent = true; break;
                case "--figure" when i + 1 < args.Length:
                    figureIds ??= new HashSet<string>(StringComparer.Ordinal);
                    foreach (var f in Split(args[++i])) figureIds.Add(f);
                    break;
                case "--lint-diagnostic": lintDiag = true; break;
                case "--rebuild-static":  rebuildStatic = true; break;
                case "-h" or "--help": Console.WriteLine(Usage); return 0;
                default:
                    Console.Error.WriteLine($"Unrecognised argument '{args[i]}'.\n");
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }

        if (outDir is null && slidesDir is null && pages is null) { Console.WriteLine(Usage); return 2; }

        // A page run captures nothing, so pairing it with a deck build or any figure capture would
        // have it silently skip what was asked for.
        bool capturing = changedFigures || allFigures || figureIds is not null;
        if (pages is not null && (slidesDir is not null || rebuildStatic || capturing || markCurrent))
        {
            Console.Error.WriteLine("--page regenerates pages only; it cannot be combined with --slides, any figure flag or --rebuild-static.\n");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        // Figures are captured into --out; asking for them on a run that writes no docs, or for the
        // static ones on a run that captures nothing, would silently do nothing.
        if ((capturing || markCurrent) && outDir is null)
        {
            Console.Error.WriteLine("The figure flags work on the docs tree; pass --out <docs-dir>.\n");
            Console.Error.WriteLine(Usage);
            return 2;
        }
        if (allFigures && (figureIds is not null || changedFigures))
        {
            Console.Error.WriteLine("--all-figures already recaptures everything; drop it, or drop --figures/--figure.\n");
            Console.Error.WriteLine(Usage);
            return 2;
        }
        if (markCurrent && capturing)
        {
            Console.Error.WriteLine("--mark-figures-current captures nothing; it cannot be combined with a capture.\n");
            Console.Error.WriteLine(Usage);
            return 2;
        }
        if (rebuildStatic && !capturing)
        {
            Console.Error.WriteLine("--rebuild-static rebuilds figures; it needs --figures or --figure.\n");
            Console.Error.WriteLine(Usage);
            return 2;
        }

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
            var run = new DocGenRun(docs)
            {
                RebuildStatic = rebuildStatic, OnlyPages = pages,
                Capture = allFigures ? FigureMode.All : changedFigures ? FigureMode.ChangedPages : FigureMode.None,
                OnlyFigures = figureIds, MarkFiguresCurrent = markCurrent,
            };
            run.Run(slidesOnly: outDir is null && pages is null, slidesOut: slidesDir, decks: decks, variants: variants);
            Console.WriteLine(run.Report);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Documentation generation FAILED.\n");
            Console.Error.WriteLine(ex.Message);
            if (ex.InnerException is { } inner && !ReferenceEquals(inner, ex))
                Console.Error.WriteLine("  ---> " + inner.Message);

            // And then the whole thing, stack included. A figure is built by a fixture that drives
            // real view models over real engine results, so a failure can arrive from anywhere in
            // the application — "the matrix is singular." names neither the figure nor the call that
            // raised it, and finding out otherwise means editing this file and running the 40-second
            // generation again, which is exactly what happened (brief-wsprobe-7).
            Console.Error.WriteLine();
            Console.Error.WriteLine(ex.ToString());
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

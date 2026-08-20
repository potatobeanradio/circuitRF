using System;
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

        Options
          --lint-diagnostic   write figures even when the dropped-paint lint fires, so the offending
                              file can be opened. Never use for a real regeneration: the lint is
                              blocking precisely because a wrong figure does not announce itself.
        """;

    public static int Main(string[] args)
    {
        string? outDir = null, slidesDir = null;
        bool lintDiag = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out"    when i + 1 < args.Length: outDir    = args[++i]; break;
                case "--slides" when i + 1 < args.Length: slidesDir = args[++i]; break;
                case "--lint-diagnostic": lintDiag = true; break;
                case "-h" or "--help": Console.WriteLine(Usage); return 0;
                default:
                    Console.Error.WriteLine($"Unrecognised argument '{args[i]}'.\n");
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }

        if (outDir is null && slidesDir is null) { Console.WriteLine(Usage); return 2; }

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

        try
        {
            string docs = outDir ?? DefaultDocsRoot();
            var run = new DocGenRun(docs);
            run.Run(slidesOnly: outDir is null, slidesOut: slidesDir);
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

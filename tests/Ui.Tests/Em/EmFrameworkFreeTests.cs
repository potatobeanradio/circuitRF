// R-em-1 — the extractor (and everything else under the EM folders that is not a view) is
// framework-free: no Avalonia, no SkiaSharp. That is the single structural decision which made the
// engine half tractable, and it is what lets every Tier E test run without constructing a document,
// a canvas or a workspace.
//
// A source scan rather than a reflection check, because the point is that these files must not even
// REFERENCE the framework — a type that is merely unused would still couple the file to it.
//
// **The EM setup pipeline moved to src/Design/Layout/Em in 2026-08** (brief-cli-em-verb.md), and R-em-1
// is now enforced there by something STRICTER than this scan: tests/Firewall.Tests asserts the whole
// CircuitRF.Design assembly references no Avalonia, transitively, which a per-file grep cannot do.
// This file keeps scanning what stayed in src/Ui — the view models, which are framework-free by the
// same rule and live in an assembly the firewall test can say nothing about.

namespace CircuitRF.Ui.Tests.Em;

public class EmFrameworkFreeTests
{
    private static string EmDir()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "src", "Ui", "Layout", "Em");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("could not locate src/Ui/Layout/Em from the test output dir");
    }

    [Fact]
    public void NothingUnderLayoutEm_ReferencesAvaloniaOrSkia()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(EmDir(), "*.cs", SearchOption.AllDirectories))
        {
            // Comments are stripped first: several of these files SAY "framework-free (no Avalonia /
            // Skia)" in their own headers, which is the rule being enforced, not a violation of it.
            string code = StripComments(File.ReadAllText(file));
            if (code.Contains("Avalonia", StringComparison.Ordinal) ||
                code.Contains("SkiaSharp", StringComparison.Ordinal) ||
                code.Contains("SKCanvas", StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(file));
        }

        Assert.True(offenders.Count == 0,
            "src/Ui/Layout/Em must stay framework-free (R-em-1). Offending file(s): " +
            string.Join(", ", offenders));
    }

    /// <summary>Removes <c>//</c>-to-end-of-line and <c>/* … */</c> spans. Deliberately simple: it
    /// does not know about string literals, which only makes it stricter (a framework name inside a
    /// string would still be caught), never more permissive.</summary>
    private static string StripComments(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        for (int i = 0; i < src.Length; i++)
        {
            if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') i++;
                sb.Append('\n');
                continue;
            }
            if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/')) i++;
                i++;
                continue;
            }
            sb.Append(src[i]);
        }
        return sb.ToString();
    }

    /// <summary>The design-layer half of the EM folder — src/Design/Layout/Em, where the `.cem`
    /// model, the extractors and the run service live since brief-cli-em-verb.md.</summary>
    private static string DesignEmDir() => RepoSubdir("src", "Design", "Layout", "Em");

    private static string RepoSubdir(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Combine([dir, .. parts]);
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException(
            $"could not locate {Path.Combine(parts)} from the test output dir");
    }

    [Fact]
    public void TheExtractorFileExists_AndIsWhereTheBriefSaysItIs()
    {
        // The file map is part of the contract: a future reader looking for cross-section extraction
        // should find it at the documented path rather than wherever it drifted to. That path is
        // src/Design/Layout/Em since the pipeline crossed the UI firewall — and asserting it HERE is
        // what makes the move visible to anyone who goes looking in the old place.
        Assert.True(File.Exists(Path.Combine(DesignEmDir(), "CrossSectionExtractor.cs")));
        Assert.True(File.Exists(Path.Combine(DesignEmDir(), "EmExtractionResult.cs")));
        Assert.True(File.Exists(Path.Combine(DesignEmDir(), "EmSetupModel.cs")));
        Assert.True(File.Exists(Path.Combine(DesignEmDir(), "EmSetupPersistence.cs")));
        Assert.True(File.Exists(Path.Combine(DesignEmDir(), "EmRunService.cs")));
        Assert.True(File.Exists(Path.Combine(DesignEmDir(), "EmSnpProvenance.cs")));

        // The `.cem` EDITOR stayed behind, and a reader who cannot find it there has found a
        // different kind of problem than a reader who cannot find the extractor.
        Assert.True(File.Exists(Path.Combine(EmDir(), "EmSetupEditorViewModel.cs")));
    }

    [Fact]
    public void NothingUnderTheDesignEmFolder_ReferencesAvaloniaOrSkia()
    {
        // Belt to tests/Firewall.Tests' braces. The assembly check is strictly stronger (it sees
        // transitive references a grep cannot), but it names the ASSEMBLY; this names the FILE, which
        // is the difference between "something in CircuitRF.Design pulled in Avalonia" and "this one
        // did". Cheap enough to keep both.
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(DesignEmDir(), "*.cs", SearchOption.AllDirectories))
        {
            string code = StripComments(File.ReadAllText(file));
            if (code.Contains("Avalonia", StringComparison.Ordinal) ||
                code.Contains("SkiaSharp", StringComparison.Ordinal) ||
                code.Contains("SKCanvas", StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(file));
        }

        Assert.True(offenders.Count == 0,
            "src/Design/Layout/Em must stay framework-free (R-em-1). Offending file(s): " +
            string.Join(", ", offenders));
    }
}

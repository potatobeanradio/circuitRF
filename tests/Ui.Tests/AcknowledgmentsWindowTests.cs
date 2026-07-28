using System.Runtime.CompilerServices;

namespace CircuitRF.Ui.Tests;

// ── docs/sonnet-briefs/brief-layout-testing-fixes.md item 9 ─────────────────────────────────────
// Clipper2 (Boost Software License) is already in README.md's own Acknowledgments section but was
// missing from the in-app Acknowledgments dialog. AcknowledgmentsWindow is a real Window subclass and
// cannot be constructed headlessly (this project's test suite must not call any Avalonia runtime API)
// — matching every prior dialog-content fix in this codebase, this is a source-text-scan test.

public class AcknowledgmentsWindowTests
{
    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    [Fact]
    public void Dialog_ListsClipper2_WithBoostLicense()
    {
        var xaml = ReadRepoFile("src/Ui/Views/Dialogs/AcknowledgmentsWindow.axaml");
        Assert.Contains("Clipper2", xaml);
        Assert.Contains("Boost Software License", xaml);
    }

    [Fact]
    public void Dialog_ListsEveryLibraryReadmeCites()
    {
        // README.md's own Acknowledgments section names these six by name; the in-app dialog must
        // name all of them too, so the two never silently drift apart again.
        var xaml = ReadRepoFile("src/Ui/Views/Dialogs/AcknowledgmentsWindow.axaml");
        foreach (var name in new[] { "Avalonia", "SkiaSharp", "CSparse.NET", "NumFlat", "Clipper2", "CommunityToolkit.Mvvm" })
            Assert.Contains(name, xaml);
    }

    [Fact]
    public void Readme_AcknowledgmentsSection_NamesClipper2WithBoostLicense()
    {
        // The other half of the cross-check: confirm README.md itself still states what this test's
        // sibling assumes it does, so a future README edit that drops Clipper2 fails loudly here too.
        var readme = ReadRepoFile("README.md");
        var idx = readme.IndexOf("## Acknowledgments", StringComparison.Ordinal);
        Assert.True(idx >= 0, "README.md has no '## Acknowledgments' section");
        // Normalize whitespace — the README hard-wraps prose at ~100 columns, so "Boost Software
        // License" itself can straddle a line break in the raw source.
        var section = string.Join(' ', readme[idx..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("Clipper2", section);
        Assert.Contains("Boost Software License", section);
    }
}

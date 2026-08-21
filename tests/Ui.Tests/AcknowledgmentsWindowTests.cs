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
        // README.md's own Acknowledgments section names these by name; the in-app dialog must
        // name all of them too, so the two never silently drift apart again.
        var xaml = ReadRepoFile("src/Ui/Views/Dialogs/AcknowledgmentsWindow.axaml");
        foreach (var name in new[] { "Avalonia", "SkiaSharp", "CSparse.NET", "NumFlat", "Clipper2", "CommunityToolkit.Mvvm" })
            Assert.Contains(name, xaml);
    }

    // ── Licence-obligation coverage ─────────────────────────────────────────────────────────────
    // circuitRF ships two copyleft components inside an otherwise MIT distribution. Both carry
    // notice obligations that travel with any redistributed binary, and a notice that silently
    // disappears is the failure mode these guard. Source-text scans for the same reason as the
    // tests above: AcknowledgmentsWindow is a real Window and cannot be constructed headlessly.

    [Fact]
    public void ThirdPartyNotices_ExistsAndCoversBothCopyleftComponents()
    {
        var notices = ReadRepoFile("THIRD-PARTY-NOTICES.md");

        // CSparse.NET is LGPL-2.1-ONLY. The "only" matters: there is no "or later" clause, so an
        // upgrade path to LGPL-3 does not exist and must not be assumed.
        Assert.Contains("CSparse.NET", notices);
        Assert.Contains("LGPL-2.1-only", notices);

        // osdi.h is MPL-2.0 and copyleft at FILE scope: it may live inside an MIT project but may
        // never be relicensed.
        Assert.Contains("osdi.h", notices);
        Assert.Contains("MPL-2.0", notices);
    }

    [Fact]
    public void CopyleftLicenceTexts_AreShippedInFull()
    {
        // LGPL §6 and MPL §3 both require the licence text to accompany the distribution. A link is
        // not a copy; these files must be in the tree.
        Assert.Contains("GNU LESSER GENERAL PUBLIC LICENSE", ReadRepoFile("licenses/LGPL-2.1.txt"));
        Assert.Contains("Mozilla Public License Version 2.0", ReadRepoFile("licenses/MPL-2.0.txt"));
    }

    [Fact]
    public void Dialog_StatesTheLgplRelinkRight_NotJustTheLicenceName()
    {
        // Naming "LGPL v2.1" alone does not discharge §6. A recipient of a STATICALLY LINKED build
        // has to be told they may modify CSparse.NET and relink it, and where the source that lets
        // them do so lives. The packaged installers are SelfContained + PublishSingleFile, which is
        // what makes the obligation live rather than theoretical.
        var xaml = ReadRepoFile("src/Ui/Views/Dialogs/AcknowledgmentsWindow.axaml");
        Assert.Contains("relink", xaml);
        Assert.Contains("THIRD-PARTY-NOTICES.md", xaml);
    }

    [Fact]
    public void Readme_DoesNotClaimTheDistributionIsCopyleftFree()
    {
        // The engine links LGPL CSparse.NET and tools/osdi-worker carries an MPL header, so the
        // README must not assert the distribution is free of copyleft — a reader relies on that
        // claim when deciding whether they may redistribute a build.
        var readme = ReadRepoFile("README.md");
        Assert.DoesNotContain("No GPL/copyleft code is ingested", readme);
        Assert.Contains("THIRD-PARTY-NOTICES.md", readme);
        Assert.Contains("LGPL-2.1-only", readme);
    }

    [Fact]
    public void Readme_DoesNotOfferToRedistributeTheProprietaryLoadpullFixtures()
    {
        // The loadpull/contour fixtures are third-party measured data that cannot be redistributed,
        // so the README must not offer to supply them on request.
        var readme = ReadRepoFile("README.md");
        var normalized = string.Join(' ', readme.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.DoesNotContain("Contact the repo owner if you need the files", normalized);
        Assert.Contains("do not permit redistribution", normalized);
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

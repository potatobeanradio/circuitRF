using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls.Primitives;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  A scrollbar does not sit on top of the scrollbar inside it.
//
//  Owner report, 2026-09-20 (railRF's Results ▸ Breakdown, long text and a short window): the inner
//  scrollbar is hard to grab because the outer one renders over it. Fluent's ScrollViewer theme
//  makes an auto-hiding bar an OVERLAY — the content presenter spans the bar's own grid column — so
//  the outer bar's transparent border owns the last ScrollBarSize pixels of content, which is
//  exactly where a card-inset inner bar draws its resting thumb. Measured in a headless Avalonia
//  12.0.3 harness on railRF's geometry: the two bars overlapped by 7 px, and an InputHitTest at the
//  middle of the inner thumb returned the OUTER bar's page button.
//
//  The fix is one style in the shared style file, which stops the presenter spanning so a visible
//  bar reserves its own column. It is XAML rendered by the real Fluent theme and this project has no
//  headless Avalonia, so it is source-scanned here — the behaviour itself was measured out of tree.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class NestedScrollBarStylesTests
{
    /// <summary>
    /// The rule exists AND still carries its <c>[AllowAutoHide=True]</c> qualifier. Both halves
    /// matter: without the qualifier the selector still matches and the setters still parse, but the
    /// span stays at 2 and nothing changes — the theme's own rule is activated by that same
    /// condition and an unconditional style at the same priority does not displace it. Measured both
    /// ways in the harness (322 px content presenter qualified, 338 px unqualified), which is why
    /// this test reads the condition rather than just the selector's shape.
    /// </summary>
    [Fact]
    public void TheScrollViewerRule_StopsTheContentPresenterSpanningTheScrollBarColumns()
    {
        var styles = ReadRepo("src", "Ui", "Styles", "CircuitRfStyles.axaml");

        var m = Regex.Match(styles,
            @"<Style Selector=""ScrollViewer\[AllowAutoHide=True\] /template/ ScrollContentPresenter#PART_ContentPresenter"">(.*?)</Style>",
            RegexOptions.Singleline);

        Assert.True(m.Success, "The nested-scrollbar overlay override is gone from CircuitRfStyles.axaml.");
        Assert.Contains(@"Property=""Grid.ColumnSpan"" Value=""1""", Regex.Replace(m.Groups[1].Value, @"\s+", " "), StringComparison.Ordinal);
        Assert.Contains(@"Property=""Grid.RowSpan"" Value=""1""",    Regex.Replace(m.Groups[1].Value, @"\s+", " "), StringComparison.Ordinal);
    }

    /// <summary>
    /// The Library palette opts back out, and the rule that lets it must stay BELOW the one it
    /// reverses — both are activated styles at the same priority, so the later one takes the
    /// property and reordering them silently restores the gutter. The palette reflows on measured
    /// width (60 px tiles in a 62 px slot), so 12 px of it is a whole column.
    /// </summary>
    [Fact]
    public void TheLibraryPalette_KeepsItsFloatingBar_ThroughAnOptOutDeclaredAfterTheRule()
    {
        var styles = ReadRepo("src", "Ui", "Styles", "CircuitRfStyles.axaml");

        int rule   = styles.IndexOf(@"Selector=""ScrollViewer[AllowAutoHide=True] /template/", StringComparison.Ordinal);
        int optOut = styles.IndexOf(@"Selector=""ScrollViewer.overlay-scroll[AllowAutoHide=True] /template/", StringComparison.Ordinal);

        Assert.True(rule >= 0 && optOut > rule, "The overlay-scroll opt-out is missing, or no longer follows the rule it reverses.");

        var palette = ReadRepo("src", "Ui", "Views", "Palette", "PaletteToolView.axaml");
        Assert.Matches(@"<ScrollViewer x:Name=""TileScroll"" Classes=""overlay-scroll""", palette);
    }

    /// <summary>
    /// The ground the rule stands on, through the real package: a ScrollViewer this application does
    /// not configure auto-hides, so the qualified selector above reaches it. A package upgrade that
    /// flipped this default would leave the rule matching nothing, silently.
    /// </summary>
    [Fact]
    public void AvaloniasOwnDefault_IsStillTheAutoHidingOverlayBar()
    {
        Assert.True(new ScrollBar().AllowAutoHide);
    }

    private static string ReadRepo(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

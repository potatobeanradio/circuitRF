using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner report: opening a `.ctech` imported from a real Gerber set greeted the user with a wall of
/// validation messages — 22 of them on one 21-layer board — repeated over every tab and again in the
/// Messages panel on every load. Two of those 22 were facts; the other 20 were the same two facts
/// restated once per layer and once per via end.
///
/// <para>These gates hold the three answers: the import stops minting an alias that cannot BE an
/// alias, the validator reports one problem per cause, and the editor shows a tab only what that tab
/// can fix.</para>
/// </summary>
public class TechValidationNoiseTests
{
    private static string TempPath() => System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"technoise-{System.Guid.NewGuid():N}.ctech");

    private static LayerDef Layer(int n, string name, InterchangeMapping? map = null) =>
        new() { Key = new LayerKey(n, 0), Name = name, Interchange = map };

    private static InterchangeMapping Suffix(string s) => new(null, null, null, s, null);

    // ── One message per shared alias, not one per additional claimant ─────────

    [Fact]
    public void TwentyLayersSharingOneGerberSuffix_AreOneMessage_NotNineteen()
    {
        // The real shape of it: a board written as twenty `.art` files, where the extension says
        // "artwork" and the STEM says which layer. Pairwise reporting made this 19 lines.
        var tech = new Technology
        {
            Layers = [.. Enumerable.Range(1, 20).Select(n => Layer(n, $"L{n}", Suffix("art")))],
        };

        var suffixProblems = TechValidation.Analyze(tech)
            .Where(p => p.Message.Contains("Gerber suffix"))
            .ToList();

        var one = Assert.Single(suffixProblems);
        Assert.Equal(TechProblemArea.Interchange, one.Area);
        Assert.Contains("20 layers", one.Message);
        Assert.Contains("and 17 more", one.Message);   // three are named, the rest counted
    }

    [Fact]
    public void APairSharingASuffix_StillNamesBothLayers()
    {
        // The count only replaces names once there are more names than anyone reads. A pair — the
        // case someone actually mistyped — must still say WHICH two.
        var tech = new Technology { Layers = [Layer(1, "Signal", Suffix("GTL")), Layer(2, "Ground", Suffix("gtl"))] };

        var one = Assert.Single(TechValidation.Analyze(tech), p => p.Message.Contains("Gerber suffix"));
        Assert.Contains("\"Signal\"", one.Message);
        Assert.Contains("\"Ground\"", one.Message);
    }

    // ── A stackup with no conductors is ONE problem, not three per via ────────

    [Fact]
    public void AViaInAStackupWithNoConductors_IsReportedOnce_NotAsThreeUnanswerableChecks()
    {
        // What a Gerber import without a job file produces: a drill layer, and no substrate anywhere
        // in the files to describe. The span ends and the wall thickness cannot be answered until the
        // conductors exist, so naming them is three restatements of the one thing that is wrong.
        var tech = new Technology
        {
            Layers = [Layer(7, "Drill")],
            Stackup = new Stackup
            {
                Layers =
                [
                    new StackupLayer
                    {
                        Kind = StackupKind.Via, Name = "Drill", Fill = ViaFillKind.Plated,
                        DrawingLayers = [new LayerKey(7, 0)],
                    },
                ],
            },
        };

        var problems = TechValidation.Analyze(tech);

        var one = Assert.Single(problems);
        Assert.Equal(TechProblemArea.Stackup, one.Area);
        Assert.Contains("no conductor layers", one.Message);
        Assert.DoesNotContain(problems, p => p.Message.Contains("Plated"));
        Assert.DoesNotContain(problems, p => p.Message.Contains("spans an unknown"));
    }

    [Fact]
    public void OnceConductorsExist_ABadViaSpanIsStillReportedInFull()
    {
        // The collapse above is scoped to "there is nothing to check against". A technology that HAS
        // conductors and names the wrong one is a typo, and must still be caught per end.
        var tech = new Technology
        {
            Stackup = new Stackup
            {
                Layers =
                [
                    new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = 1, SigmaSm = 1, IsGroundReference = true },
                    new StackupLayer { Kind = StackupKind.Via, Name = "V", SpanFromLayer = "Top", SpanToLayer = "Typo", Fill = ViaFillKind.Plated },
                ],
            },
        };

        var problems = TechValidation.Analyze(tech);

        Assert.Contains(problems, p => p.Message.Contains("spans an unknown conductor layer \"Typo\""));
        Assert.Contains(problems, p => p.Message.Contains("Plated"));
    }

    // ── The editor shows a tab only what that tab can fix ─────────────────────

    [Fact]
    public void TheBanner_ListsOnlyTheVisibleTabsProblems_AndTheOtherTabsCarryTheCount()
    {
        var tech = new Technology
        {
            Layers = [Layer(1, "A", Suffix("art")), Layer(2, "B", Suffix("art"))],
            Stackup = new Stackup
            {
                Layers = [new StackupLayer { Kind = StackupKind.Conductor, Name = "M", SigmaSm = 0, ThicknessDbu = 1, IsGroundReference = true }],
            },
        };
        var vm = new TechEditorViewModel(TempPath(), tech);

        // Tab 0 is Layers, and neither problem belongs to it — nothing to show, and the two tabs that
        // DO own one say so in their own headers.
        Assert.Equal(0, vm.SelectedTabIndex);
        Assert.Empty(vm.ActiveTabIssues);
        Assert.False(vm.HasActiveTabIssues);
        Assert.Equal("Layers", vm.LayersTabHeader);
        Assert.Equal("Stackup (1)", vm.StackupTabHeader);
        Assert.Equal("Interchange (1)", vm.InterchangeTabHeader);

        // ...but the technology is still known to be wrong, which is a different question.
        Assert.True(vm.HasValidationIssues);
        Assert.Equal(2, vm.ValidationIssues.Count);

        vm.SelectedTabIndex = 3;   // Interchange

        Assert.True(vm.HasActiveTabIssues);
        Assert.Contains("Gerber suffix", Assert.Single(vm.ActiveTabIssues));

        // Every header reads EXACTLY as it did before the click. Selecting a tab must not change the
        // width of its own label: that slides the headers to its right sideways, under the pointer
        // that just clicked one — the horizontal twin of the banner moving the header row vertically,
        // which is why the banner is docked below the tabs.
        Assert.Equal("Layers", vm.LayersTabHeader);
        Assert.Equal("Stackup (1)", vm.StackupTabHeader);
        Assert.Equal("DRC Rules", vm.DrcTabHeader);
        Assert.Equal("Interchange (1)", vm.InterchangeTabHeader);
    }

    // ── The banner's own markup: three properties that fail SILENTLY ──────────

    /// <summary>The validation banner's markup, from the comment that opens it to its closing tag.</summary>
    private static string BannerMarkup()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string axaml = File.ReadAllText(
            Path.Combine(dir!.FullName, "src", "Ui", "Views", "Layout", "TechEditorView.axaml"));

        int start = axaml.IndexOf("<!-- ── Validation banner", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "the validation banner block is gone");
        int end = axaml.IndexOf("<!-- ── Sections", start, System.StringComparison.Ordinal);
        Assert.True(end > start, "the validation banner is no longer followed by the Sections block");
        return axaml[start..end];
    }

    [Fact]
    public void TheBannerIsDockedBelowTheTabs_SoAppearingNeverMovesTheTabHeaders()
    {
        // Owner report: docked to the Top it sat between the title bar and the tab header strip, so a
        // per-tab banner appearing and disappearing moved the very header row being clicked.
        string banner = BannerMarkup();

        Assert.Contains("DockPanel.Dock=\"Bottom\"", banner);
        Assert.DoesNotContain("DockPanel.Dock=\"Top\"", banner);

        // ...and it must still be declared BEFORE the TabControl, which a DockPanel gives its
        // remaining space to as the last child.
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx"))) dir = dir.Parent;
        string axaml = File.ReadAllText(
            Path.Combine(dir!.FullName, "src", "Ui", "Views", "Layout", "TechEditorView.axaml"));
        Assert.True(
            axaml.IndexOf("<!-- ── Validation banner", System.StringComparison.Ordinal)
                < axaml.IndexOf("<TabControl x:Name=\"SectionTabs\"", System.StringComparison.Ordinal),
            "the banner must be declared before the TabControl, or the TabControl claims the space first");
    }

    [Fact]
    public void TheBannerRowIsNotAHorizontalStackPanel_SoTextWrappingCanActuallyFire()
    {
        // Owner report: the messages did not wrap and ran off the right edge, unreadable — even though
        // TextWrapping="Wrap" was set. A horizontal StackPanel measures its children with UNBOUNDED
        // width on its own axis, so the wrap had no width to happen at and the property was inert.
        // This is the whole class of bug: the markup LOOKS correct and does nothing.
        string banner = BannerMarkup();

        Assert.DoesNotContain("Orientation=\"Horizontal\"", banner);
        Assert.Contains("ColumnDefinitions=\"Auto,*\"", banner);
        Assert.Contains("TextWrapping=\"Wrap\"", banner);
        // The scroller must not offer horizontal scrolling either, or the row is measured unbounded
        // again and the wrap goes inert for the same reason.
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", banner);
    }

    [Fact]
    public void TheBannerMessagesAreSelectable_SoTheyCanBeCopied()
    {
        // They name layers, suffixes and aliases the user then goes and searches for.
        string banner = BannerMarkup();

        Assert.Contains("<SelectableTextBlock", banner);
        Assert.DoesNotContain("<TextBlock ", banner);
    }
}

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout;
using CircuitRF.Ui.Converters;
using CircuitRF.Ui.Diagnostics;
using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Technology Editor — the Help button, and the Stackup tab's own toolbar row.
//
//  Owner request (2026-09-09): a Help button in the top-right corner; tooltips on the two labels
//  whose wording is not self-explanatory ("EM sheet at", "Patterned with"); the Add buttons moved
//  off the bottom of the pane and onto the top row; and the stack height joined by what the stack
//  is MADE of, as selectable text that cannot overlap when the window narrows.
//
//  Layout assertions are source scans, for the reason the file beside this one already states: an
//  .axaml change has no headlessly assertable rendered output. The view-model half is asserted
//  against the real shipped technology, not a fixture, because the counts are the whole point.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class TechEditorHelpAndStackupSummaryTests
{
    private static string Axaml() =>
        RepoFile(Path.Combine("src", "Ui", "Views", "Layout", "TechEditorView.axaml"));

    private static string CodeBehind() =>
        RepoFile(Path.Combine("src", "Ui", "Views", "Layout", "TechEditorView.axaml.cs"));

    // ── Help ──────────────────────────────────────────────────────────────────

    [Fact]
    public void HelpButton_SitsInTheHeadersLastColumn_OnTheSameGlyphEveryOtherHelpButtonUses()
    {
        var axaml = Axaml();

        var i = axaml.IndexOf("Name=\"HelpButton\"", StringComparison.Ordinal);
        Assert.True(i >= 0, "the Technology Editor header must carry a Help button");

        // Column 5 is the last of the header Grid's five Auto columns — the top-right corner, after
        // Undo/Redo/Save. Anywhere else and it is not where the request put it.
        var button = axaml[(i - 200)..(i + 400)];
        Assert.Contains("Grid.Column=\"5\"", button);

        // The ? glyph, not a word and not a different icon: one meaning, one icon across the app.
        Assert.Contains("Kind=\"HelpCircleOutline\"", button);
    }

    /// <summary>
    /// The destination follows the visible tab. A button that always opened one chapter would be
    /// right a quarter of the time — this window edits four unrelated things.
    /// </summary>
    [Fact]
    public void HelpDestination_FollowsTheVisibleTab()
    {
        Assert.Equal(("reference/layout-editor.html", "technology"),  TechEditorViewModel.HelpDestinationFor(0));
        Assert.Equal(("reference/stackup.html",       ""),            TechEditorViewModel.HelpDestinationFor(1));
        Assert.Equal(("reference/layout-editor.html", "drc"),         TechEditorViewModel.HelpDestinationFor(2));
        Assert.Equal(("reference/layout-editor.html", "interchange"), TechEditorViewModel.HelpDestinationFor(3));
        Assert.Equal(("reference/materials.html",     ""),            TechEditorViewModel.HelpDestinationFor(TechEditorViewModel.MaterialsTabIndex));
    }

    /// <summary>
    /// Every destination the button can emit must be in <see cref="DocAnchors"/>, which is what makes
    /// the docs build fail if one of those sections stops being emitted — the failure mode of a
    /// broken deep link is that the browser opens the page at the top and nobody notices.
    /// </summary>
    [Fact]
    public void EveryHelpDestination_IsRegisteredInDocAnchors()
    {
        foreach (var index in new[] { 0, 1, 2, 3, TechEditorViewModel.MaterialsTabIndex })
        {
            var (page, anchor) = TechEditorViewModel.HelpDestinationFor(index);
            Assert.Contains(DocAnchors.TechEditorLinks, l => l.Page == page && l.Anchor == anchor);
        }

        Assert.All(DocAnchors.TechEditorLinks, l => Assert.Contains(DocAnchors.All(), a => a == l));
    }

    [Fact]
    public void HelpButton_OpensThroughDocLauncher_AndNothingElseInThisViewDoes()
    {
        var cs = CodeBehind();
        Assert.Contains("HelpButton.Click", cs);
        Assert.Contains("DocLauncher.Open(", cs);
        Assert.Contains("HelpDestination", cs);
    }

    // ── The two labels whose wording is not self-explanatory ──────────────────

    /// <summary>
    /// The tooltip has to be on the LABEL as well as the control: the words are what a reader hovers
    /// when they do not know what the control is for, and hovering the control assumes they do.
    ///
    /// <para><b>The wording moved out of the .axaml and into <see cref="StackupCardText"/></b>
    /// (R-stk6-8, 2026-09-13), because brief 6 puts these same fields on the cross-section's context
    /// menu and requires the menu's labels and tooltips to be the CARD's, verbatim. So what this
    /// test now checks is stronger than what it checked before: rather than two typed copies that
    /// happen to match today, the label, the control beside it and the menu item all read one
    /// constant, and drift is not expressible. The assertions are the same three facts — the row
    /// exists, the label carries the explanation, and so does the control.</para>
    /// </summary>
    [Theory]
    [InlineData("SheetAt",     "Metal thickness goes to:", "has no thickness for metal")]
    [InlineData("PresentWith", "Patterned with:",          "what says where it is")]
    public void TheLabel_CarriesTheSameExplanationAsTheControlBesideIt(
        string constant, string label, string fragment)
    {
        // The words themselves are still exactly these words.
        Assert.Equal(label, (string)typeof(StackupCardText).GetField(constant)!.GetRawConstantValue()!);
        Assert.Contains(fragment, (string)typeof(StackupCardText).GetField(constant + "Tip")!.GetRawConstantValue()!);

        var axaml = Axaml();

        var i = axaml.IndexOf("Text=\"{x:Static lay:StackupCardText." + constant + "}\"", StringComparison.Ordinal);
        Assert.True(i >= 0, $"the Stackup tab must still carry a \"{label}\" label");

        var block = axaml[i..Math.Min(axaml.Length, i + 900)];
        Assert.Contains("ToolTip.Tip=\"{x:Static lay:StackupCardText." + constant + "Tip}\"", block);

        // On the label AND on the combo, so the two cannot drift into saying different things.
        Assert.True(
            Regex.Matches(axaml, Regex.Escape("{x:Static lay:StackupCardText." + constant + "Tip}")).Count >= 2,
            $"the {label} explanation should be on both the label and the control beside it");
    }

    // ── The relabelled thickness-absorption row ───────────────────────────────

    /// <summary>
    /// Owner, 2026-09-09: "EM sheet at: Bottom / Top" could not be answered without knowing what a
    /// conductor's z <i>band</i> is — an internal construct of the extractor that the control never
    /// defined. The row now names the CONSEQUENCE. <b>Display only</b>: the enum and the
    /// <c>.ctech</c> are untouched, so every technology ever written reads back identically and no
    /// extraction changes.
    /// </summary>
    [Fact]
    public void TheThicknessRow_NamesWhatItDoes_AndTheStoredValueIsUnchanged()
    {
        var axaml = Axaml();

        // The words live in StackupCardText since R-stk6-8 — see the theory above for why.
        Assert.Equal("Metal thickness goes to:", StackupCardText.SheetAt);
        Assert.Contains("Text=\"{x:Static lay:StackupCardText.SheetAt}\"", axaml);
        Assert.DoesNotContain("EM sheet at", axaml);

        // The combo still binds the enum itself; only its ItemTemplate is converted.
        Assert.Contains("SelectedItem=\"{Binding SelectedSheetAt, Mode=TwoWay}\"", axaml);
        Assert.Contains("StaticResource SheetAtLabel", axaml);

        Assert.Equal("the layer above (default)", SheetAtLabelConverter.Label(ConductorSheetSurface.Bottom));
        Assert.Equal("the substrate below",       SheetAtLabelConverter.Label(ConductorSheetSurface.Top));

        // The default is still Bottom and the file still says "Bottom" — the label is not the value.
        Assert.Equal(ConductorSheetSurface.Bottom, new StackupLayer { Kind = StackupKind.Conductor }.SheetAt
                                                   ?? ConductorSheetSurface.Bottom);
    }

    /// <summary>
    /// There are exactly two choices, and the third one a reader reaches for does not exist. Half to
    /// each side is representable — the sheet would land on the interface between the two grown
    /// regions — but it would sit every ordinary line half a metal thickness away from the
    /// closed-form models circuitRF validates against, forever. Declined on purpose (owner,
    /// 2026-09-09), and the chapter says so rather than leaving the next reader to ask again.
    /// </summary>
    [Fact]
    public void ThereIsNoSymmetricChoice_AndTheChapterSaysWhy()
    {
        Assert.Equal(2, Enum.GetValues<ConductorSheetSurface>().Length);

        var md = RepoFile(Path.Combine("docs", "user", "src", "reference", "stackup.md"));
        Assert.Contains("This is not \"expand up / expand down / symmetric\"", md);
        Assert.Contains("There is\ndeliberately no \"symmetric\"", md);
    }

    /// <summary>The word "band" was used thirteen times in this chapter and defined nowhere.</summary>
    [Fact]
    public void TheChapter_NoLongerUsesTheUndefinedWordBand()
    {
        // Word-boundary, not a substring: "bandwidth" is a perfectly good word and this chapter may
        // legitimately grow one. What must not come back is the bare noun.
        var md = RepoFile(Path.Combine("docs", "user", "src", "reference", "stackup.md"));
        var hit = Regex.Match(md, @"\bbands?\b", RegexOptions.IgnoreCase);
        Assert.False(hit.Success,
            "The Stackup chapter used \"band\" thirteen times and defined it nowhere (owner, "
          + "2026-09-09). It is the extractor's name for the slice of z a stackup row occupies; say "
          + "that, or say what the setting does instead. Found at offset " + hit.Index + ".");
    }

    // ── The Stackup tab's toolbar ─────────────────────────────────────────────

    [Fact]
    public void AddButtons_AreOnTheTopRow_NotDockedToTheBottomOfThePane()
    {
        var axaml = Axaml();

        var i = axaml.IndexOf("AddDielectricLayerCommand", StringComparison.Ordinal);
        Assert.True(i >= 0, "the Stackup tab must still offer an Add Dielectric button");

        // The three buttons used to live in a DockPanel.Dock="Bottom" strip — a full stackup's
        // scroll away from the row being added to. The row they are on now is the WrapPanel that holds
        // them, so the window starts there: a fixed reach back from the first button stopped reaching it
        // once the boundary combos and a comment were put ahead of the buttons in the same row.
        int row = axaml.LastIndexOf("<WrapPanel", i, StringComparison.Ordinal);
        Assert.True(row >= 0, "the Add buttons must sit in the toolbar's WrapPanel");
        var panel = axaml[row..(i + 700)];
        Assert.DoesNotContain("DockPanel.Dock=\"Bottom\"", panel);
        Assert.StartsWith("<WrapPanel Grid.Row=\"0\"", panel, StringComparison.Ordinal);
        // No longer right-aligned: they flow in the WrapPanel after the boundary combos, which is what
        // keeps them from painting over the combos on a narrow pane (the test below).

        // All three together, or the row is half moved.
        Assert.Contains("AddConductorLayerCommand", panel);
        Assert.Contains("AddViaLayerCommand", panel);
    }

    /// <summary>
    /// A horizontal StackPanel measures its children with UNBOUNDED width on its own axis, so it does
    /// not shorten as the window narrows — it runs out of its cell and paints over whatever is beside
    /// it. That is the overlap this row had to be immune to, and a WrapPanel is what makes it so.
    /// </summary>
    [Fact]
    public void SummaryReadouts_Wrap_RatherThanOverflowTheRow()
    {
        var axaml = Axaml();

        var i = axaml.IndexOf("ViewModel.StackTotalText", StringComparison.Ordinal);
        Assert.True(i >= 0, "the Stackup tab must still show the stack height");

        var open = axaml.LastIndexOf("<WrapPanel", i, StringComparison.Ordinal);
        var stack = axaml.LastIndexOf("<StackPanel", i, StringComparison.Ordinal);
        Assert.True(open > stack, "the summary readouts must live in a WrapPanel, not a StackPanel");

        // Row 1: the height moved down a row when the Add buttons took row 0.
        Assert.Contains("Grid.Row=\"1\"", axaml[open..i]);
    }

    [Fact]
    public void EveryReadoutOnThatRow_IsSelectable_IncludingTheStackHeight()
    {
        var axaml = Axaml();

        foreach (var binding in new[]
        {
            "ViewModel.StackTotalText", "ViewModel.ConductorCountText",
            "ViewModel.DielectricCountText", "ViewModel.ViaCountText",
            "ViewModel.BoardThicknessText",
        })
        {
            var i = axaml.IndexOf(binding, StringComparison.Ordinal);
            Assert.True(i >= 0, $"the summary row must still bind {binding}");

            // These are numbers people copy — into a fab note, a spreadsheet, a message asking
            // whether the board is the one they think it is.
            var element = axaml.LastIndexOf('<', i);
            Assert.StartsWith("<SelectableTextBlock", axaml[element..]);
        }
    }

    // ── The counts themselves, against the real shipped technology ────────────

    [Fact]
    public void TheCounts_AreTerse_AndAgreeWithTheShippedFourLayerTechnology()
    {
        var tech = ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");
        var vm   = new TechEditorViewModel(Path.Combine(Path.GetTempPath(), "x.ctech"), tech);

        // Four coppers, three dielectrics between them, two via entries — and the figure in the
        // Stackup chapter is a picture of exactly this file, so a change here changes that page.
        Assert.Equal("conductors: 4",  vm.ConductorCountText);
        Assert.Equal("dielectrics: 3", vm.DielectricCountText);
        Assert.Equal("vias: 2",        vm.ViaCountText);

        // "Number of conductors: 4" does not fit beside the height and the board comparison, which
        // is the whole reason the wording is this short.
        Assert.DoesNotContain("Number of", vm.ConductorCountText);
    }

    [Fact]
    public void TheCounts_FollowAnEdit()
    {
        var tech = ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");
        var vm   = new TechEditorViewModel(Path.Combine(Path.GetTempPath(), "x.ctech"), tech);

        vm.AddViaLayerCommand.Execute(null);
        Assert.Equal("vias: 3", vm.ViaCountText);

        // And an undo puts it back — the summary is recomputed from Working on every rebuild, which
        // is what makes that free rather than something to remember.
        vm.UndoCommand.Execute(null);
        Assert.Equal("vias: 2", vm.ViaCountText);
    }

    // ── The chapter and its figure ────────────────────────────────────────────

    [Fact]
    public void TheStackupChapter_CitesTheTechnologyEditorFigure_AndTheCatalogHasIt()
    {
        var md = RepoFile(Path.Combine("docs", "user", "src", "reference", "stackup.md"));
        Assert.Contains("{{ui: tech-editor-stackup}}", md);

        var row = FigureCatalog.Catalog.Single(r => r.Id == "tech-editor-stackup");

        // Tall on purpose: the stackup is a scrolling list, and a reader cannot scroll a picture.
        // The shipped four-layer technology is nine entries, and all nine have to be in the frame.
        Assert.True(row.Height > 1500,
            "the Stackup-tab figure must be tall enough to show every entry without a scrollbar; "
          + $"it is {row.Height} px.");
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static string RepoFile(string rel)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, rel));
    }
}

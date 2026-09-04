using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Diagnostics;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The Security &amp; Permissions de-cluttering, and the Help button that replaced it (owner,
/// 2026-09-03).
///
/// <para>Four paragraphs of standing helper text under four controls made that tab read as prose
/// with controls in it. Each moved onto its own control as a <c>ToolTip.Tip</c>, and the long-form
/// explanation moved to a User-Documentation chapter the dialog now links to from a Help button at
/// the leading edge of its footer — which is where every platform puts Help, and where Revert used
/// to sit.</para>
///
/// <para>Asserted against the SOURCE, for the reason the rest of <c>Ui.Tests</c> is: this project
/// deliberately calls no Avalonia runtime API. What is being pinned is structural — the text is a
/// tooltip and not a standing block, the button exists and points at the page the anchor contract
/// declares — not a rendered pixel.</para>
/// </summary>
public class SettingsDialogHelpAndTooltipsTests
{
    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!;
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine([RepoRoot().FullName, .. parts]));

    private static string Dialog(string file) => Read("src", "Ui", "Views", "Dialogs", file);

    /// <summary>
    /// Each of the four sentences still exists — nothing was DELETED, it was moved — and each is now
    /// inside a <c>ToolTip.Tip</c>. The opening words are quoted rather than the whole paragraph so
    /// that rewording the explanation does not fail this test; where it lives is the property.
    /// </summary>
    [Theory]
    [InlineData("ExternalWorkerSettingsView.axaml",   "A kit can ship its own program")]
    [InlineData("UpdateSettingsView.axaml",           "Downloads new versions in the background")]
    [InlineData("UpdateSettingsView.axaml",           "Opens the release notes once")]
    [InlineData("VerilogACompilerSettingsView.axaml", "circuitRF loads COMPILED models")]
    public void TheHelperTextIsATooltipAndNotAStandingBlock(string file, string opening)
    {
        string xaml = Dialog(file);

        int text = xaml.IndexOf(opening, StringComparison.Ordinal);
        Assert.True(text >= 0,
            $"{file} no longer says \"{opening}…\" anywhere. The tooltip pass MOVED this explanation "
          + "onto its control; it was not meant to be dropped.");

        // The nearest enclosing element start before the sentence must be inside an open ToolTip.Tip.
        int tip   = xaml.LastIndexOf("<ToolTip.Tip>",  text, StringComparison.Ordinal);
        int close = xaml.LastIndexOf("</ToolTip.Tip>", text, StringComparison.Ordinal);
        Assert.True(tip >= 0 && tip > close,
            $"\"{opening}…\" in {file} is not inside a ToolTip.Tip. Standing helper text under a "
          + "control is exactly the clutter this pass removed.");
    }

    /// <summary>
    /// A tooltip carrying a paragraph has to wrap. Avalonia hands a bare string to a ContentPresenter
    /// that does not, so a 400-character tip would render as one line running off the screen edge —
    /// which is why the repo's own idiom is an explicit TextBlock with a MaxWidth.
    /// </summary>
    [Theory]
    [InlineData("ExternalWorkerSettingsView.axaml")]
    [InlineData("UpdateSettingsView.axaml")]
    [InlineData("VerilogACompilerSettingsView.axaml")]
    public void EveryParagraphTooltipWrapsAndIsBounded(string file)
    {
        foreach (var block in Between(Dialog(file), "<ToolTip.Tip>", "</ToolTip.Tip>"))
        {
            Assert.Contains("TextWrapping=\"Wrap\"", block);
            Assert.Contains("MaxWidth=", block);
        }
    }

    /// <summary>
    /// Help at the leading edge, everything that ACTS on the dialog at the trailing one. Revert moved
    /// across to join Cancel and Close: it throws the colour edits away, and the bottom-left corner is
    /// where a reader looks for documentation rather than for a destructive button.
    /// </summary>
    [Fact]
    public void HelpIsLeftAndRevertMovedToTheRightGroup()
    {
        // The FOOTER only. "DockPanel.Dock" also appears in the Color Theme tab's variant toggle,
        // and comparing offsets across the whole file would compare two unrelated panels.
        string xaml   = Dialog("SettingsView.axaml");
        int    start  = xaml.LastIndexOf("<DockPanel Grid.Row=\"1\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "The Settings dialog no longer has a footer DockPanel in Grid.Row 1.");
        string footer = xaml[start..];

        int help   = footer.IndexOf("Name=\"HelpButton\"", StringComparison.Ordinal);
        int right  = footer.IndexOf("DockPanel.Dock=\"Right\"", StringComparison.Ordinal);
        int revert = footer.IndexOf("Content=\"Revert\"", StringComparison.Ordinal);
        int cancel = footer.IndexOf("Content=\"Cancel\"", StringComparison.Ordinal);

        Assert.True(help >= 0, "The Settings dialog has no Help button in its footer.");
        Assert.Contains("DockPanel.Dock=\"Left\"", footer[..help]);
        Assert.True(right > help,   "Help must be declared at the leading edge, before the trailing group.");
        Assert.True(revert > right, "Revert must sit inside the trailing button group, not on the left.");
        Assert.True(revert < cancel, "Revert leads the trailing group: Revert, Cancel, Close.");
    }

    /// <summary>
    /// The Help button opens the page the anchor contract declares, and that page is one
    /// <c>DocAnchors</c> knows about — which is what makes the generator fail if it stops existing.
    /// </summary>
    [Fact]
    public void HelpOpensTheSettingsChapterAndTheAnchorContractKnowsIt()
    {
        string code = Dialog("SettingsView.axaml.cs");

        Assert.Contains("OnHelpClick", code);
        Assert.Contains("DocLauncher.Open(SettingsDocPage)", code);
        Assert.Contains("\"reference/settings.html\"", code);

        Assert.Contains(DocAnchors.WholePages, p => p == "reference/settings.html");
    }

    /// <summary>
    /// One figure per tab, and every tab has one. A page whose prose walks four tabs while its
    /// figures show three is the drift the docs factory exists to prevent.
    /// </summary>
    [Fact]
    public void EveryTabOfTheDialogHasItsOwnFigureAndThePageCitesThemAll()
    {
        string page = Read("docs", "user", "src", "reference", "settings.md");

        foreach (var id in new[] { "settings-general", "settings-security",
                                   "settings-color-theme", "settings-wirebonds" })
        {
            Assert.Contains(FigureCatalog.Catalog, r => r.Id == id);
            Assert.Contains("{{ui: " + id + "}}", page);
        }

        int tabs = Occurrences(Dialog("SettingsView.axaml"), "<TabItem Header=");
        Assert.Equal(4, tabs);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static System.Collections.Generic.IEnumerable<string> Between(string text, string open, string close)
    {
        for (int i = text.IndexOf(open, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(open, i + 1, StringComparison.Ordinal))
        {
            int end = text.IndexOf(close, i, StringComparison.Ordinal);
            if (end < 0) yield break;
            yield return text[i..end];
        }
    }

    private static int Occurrences(string text, string needle)
        => text.Split(needle).Length - 1;
}

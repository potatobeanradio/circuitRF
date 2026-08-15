// ================================================================
//  HarmonicaSettingsDialogTextAndTabSplitTests.cs — R8A §2/§3's own gate
//
//  §2  every user-visible "§n" / internal brief code disappears from the harmonicaRF settings
//      dialog's XAML (comments — C# and XAML alike — are the repo's own numbering convention and
//      stay).
//  §3  the fade sliders, the iso-line-labels checkbox and the tickle-default controls moved from
//      Appearance to Advanced, markup and all.
//
//  Source/text scans, in the shape brief-harmonicarf-h8's own HarmonicaStandaloneTests uses — this
//  repo's long-standing fallback for a XAML control an Avalonia-instantiation-banned test project
//  cannot otherwise assert about (tests/Ui.Tests may not instantiate an Avalonia control).
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaSettingsDialogTextAndTabSplitTests
{
    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        Assert.True(dir.Length > 0, "could not locate the repository root");
        return dir;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));

    /// <summary>XML/AXAML text with <c>&lt;!-- --&gt;</c> comments removed — a previous round's scan
    /// test failed on its own comments before this strip was added.</summary>
    private static string XmlCodeOnly(string source) =>
        Regex.Replace(source, "<!--.*?-->", "", RegexOptions.Singleline);

    private const string SettingsDialogDir = "src/Ui/Views/Dialogs";

    private static readonly string[] DialogAxamlFiles =
    [
        "HarmonicaAppearanceSettingsView.axaml",
        "HarmonicaAdvancedSettingsView.axaml",
        "HarmonicaSettingsDialog.axaml",
    ];

    // ══ R8A §2 — no "§" and no internal brief code, anywhere user-visible ═══

    [Fact]
    public void NoHarmonicaDialogAxaml_ContainsASectionSignOrABriefCode_OutsideAComment()
    {
        // The brief's own verification command, run as a test rather than by hand so a future
        // dialog can't reintroduce this: `grep -n "§\|R-h[0-9]\|R7[A-D]\|R8[A-C]"
        // src/Ui/Views/Dialogs/Harmonica*.axaml` must return only lines inside <!-- --> blocks. Scoped
        // to the WHOLE Harmonica*.axaml glob, not just the three settings-dialog files — this is a
        // list built by grep, and a future one must not reappear anywhere in the family.
        var pattern = new Regex(@"§|R-h[0-9]|R7[A-D]|R8[A-C]", RegexOptions.Compiled);
        string dir = Path.Combine(RepoRoot(), SettingsDialogDir);
        var files = Directory.GetFiles(dir, "Harmonica*.axaml");
        Assert.True(files.Length >= DialogAxamlFiles.Length, "the glob found suspiciously few files");

        foreach (var path in files)
        {
            string code = XmlCodeOnly(File.ReadAllText(path));
            var offenders = code.Split('\n')
                .Select((line, i) => (Line: line, Number: i + 1))
                .Where(l => pattern.IsMatch(l.Line))
                .ToArray();

            Assert.True(offenders.Length == 0,
                $"{Path.GetFileName(path)}: non-comment '§'/brief-code text found: " +
                string.Join("; ", offenders.Select(o => $"L{o.Number}: {o.Line.Trim()}")));
        }
    }

    [Fact]
    public void AppearanceView_NoLongerNamesR9r2_18aOrAnySectionInUserVisibleText()
    {
        // §2's own named example (R-h9r2-18a) had no "§" in it but was the same defect: an internal
        // brief code leaking into a UI string. Pinned by name too, not just by the general pattern.
        string code = XmlCodeOnly(Read($"{SettingsDialogDir}/HarmonicaAppearanceSettingsView.axaml"));
        Assert.DoesNotContain("R-h9r2-18a", code, StringComparison.Ordinal);

        string advanced = XmlCodeOnly(Read($"{SettingsDialogDir}/HarmonicaAdvancedSettingsView.axaml"));
        Assert.DoesNotContain("R-h9r2-18a", advanced, StringComparison.Ordinal);
        Assert.DoesNotContain("§3", advanced, StringComparison.Ordinal);
    }

    // ══ R8A §3 — the tab split ════════════════════════════════════════════

    [Fact]
    public void AppearanceView_NoLongerContainsTheMovedControls()
    {
        string axaml = Read($"{SettingsDialogDir}/HarmonicaAppearanceSettingsView.axaml");
        foreach (string name in new[]
                 {
                     "AlphaFloorSlider", "AlphaExpSlider", "IsoLabelsCheck",
                     "TickleDefaultEnabledCheck", "TickleDefaultDbmBox",
                 })
            Assert.DoesNotContain(name, axaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AdvancedView_ContainsAllFiveMovedControls()
    {
        string axaml = Read($"{SettingsDialogDir}/HarmonicaAdvancedSettingsView.axaml");
        foreach (string name in new[]
                 {
                     "AlphaFloorSlider", "AlphaExpSlider", "IsoLabelsCheck",
                     "TickleDefaultEnabledCheck", "TickleDefaultDbmBox",
                 })
            Assert.Contains(name, axaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AdvancedViewCodeBehind_OwnsTheMovedHandlers_AndAttachTakesTheColorEditor()
    {
        string code = Read($"{SettingsDialogDir}/HarmonicaAdvancedSettingsView.axaml.cs");
        foreach (string member in new[]
                 {
                     "OnFadeChanged", "OnIsoLabelsChanged", "OnTickleDefaultChanged",
                     "OnTickleDefaultDbmKeyDown", "OnTickleDefaultDbmLostFocus",
                 })
            Assert.Contains(member, code, StringComparison.Ordinal);

        // The one thing that breaks silently if markup moves without state (§3's own warning): both
        // controls write through the SAME HarmonicaColorEditor the Appearance tab gets.
        Assert.Contains("HarmonicaColorEditor editor", code, StringComparison.Ordinal);

        string appearanceCode = Read($"{SettingsDialogDir}/HarmonicaAppearanceSettingsView.axaml.cs");
        foreach (string member in new[]
                 {
                     "OnFadeChanged", "OnIsoLabelsChanged", "OnTickleDefaultChanged",
                     "OnTickleDefaultDbmKeyDown", "OnTickleDefaultDbmLostFocus",
                 })
            Assert.DoesNotContain(member, appearanceCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsDialog_HandsTheSameColorEditorInstanceToBothTabs()
    {
        string code = Read($"{SettingsDialogDir}/HarmonicaSettingsDialog.axaml.cs");
        Assert.Contains("AdvancedTab.Attach(vm, vm.ColorEditor)", code, StringComparison.Ordinal);
    }
}

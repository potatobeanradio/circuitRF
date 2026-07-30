using System.IO;
using System.Runtime.CompilerServices;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Views.Dialogs;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-misc-termg-units-technologies.md §4 (R-misc-11/12): the New Workspace
/// dialog's technology picker is a combobox over the four shipped technologies + "None," defaulting
/// to <see cref="ShippedTechnologies.DefaultId"/>. <see cref="NewWorkspaceDialog"/> and
/// <see cref="WorkspaceViewModel"/> are both <c>Window</c>/un-constructable-headlessly types
/// (matching every prior phase's note on this) — correctness rests on the plain-record shape below
/// plus structural source-scans proving the actual wiring, the same fallback this codebase already
/// established for exactly this class of untestable UI code (see <c>PCellDoubleClickDispatchTests.cs</c>).
/// </summary>
public sealed class NewWorkspaceTechnologyPickerTests
{
    [Fact]
    public void NewWorkspaceTechItem_ToString_ReturnsDisplayName()
    {
        var item = new NewWorkspaceTechItem("pcb-2layer_RO4350B_20mil_1oz", "PCB 2-Layer RO4350B (20mil, 1oz)");
        Assert.Equal("PCB 2-Layer RO4350B (20mil, 1oz)", item.ToString());
    }

    [Fact]
    public void NewWorkspaceTechItem_NoneEntry_HasNullId()
    {
        var none = new NewWorkspaceTechItem(null, "None");
        Assert.Null(none.Id);
        Assert.Equal("None", none.ToString());
    }

    [Fact]
    public void NewWorkspaceResult_CarriesNullableTechnologyId()
    {
        var withTech = new NewWorkspaceResult("/parent", "MyWorkspace", "pcb-2layer_RO4350B_20mil_1oz");
        Assert.Equal("pcb-2layer_RO4350B_20mil_1oz", withTech.TechnologyId);

        var none = new NewWorkspaceResult("/parent", "MyWorkspace", null);
        Assert.Null(none.TechnologyId);
    }

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    [Fact]
    public void NewWorkspaceDialog_PopulatesComboFromShippedTechnologies_DefaultsToDefaultId()
    {
        string src = ReadRepoFile(Path.Combine("src", "Ui", "Views", "Dialogs", "NewWorkspaceDialog.axaml.cs"));
        Assert.Contains("ShippedTechnologies.All", src);
        Assert.Contains("ShippedTechnologies.DefaultId", src);
        Assert.Contains("TechCombo.ItemsSource", src);
        // No more radio-button trio for technology choice.
        Assert.DoesNotContain("NewWorkspaceTechChoice", src);
        Assert.DoesNotContain("StarterTechnologies", src);
    }

    [Fact]
    public void NewWorkspaceDialog_Axaml_HasComboAndNoneHint_NoRadioTrio()
    {
        string axaml = ReadRepoFile(Path.Combine("src", "Ui", "Views", "Dialogs", "NewWorkspaceDialog.axaml"));
        Assert.Contains("TechCombo", axaml);
        Assert.Contains("TechNoneHint", axaml);
        Assert.DoesNotContain("TechPcbRadio", axaml);
        Assert.DoesNotContain("TechMmicRadio", axaml);
        Assert.DoesNotContain("TechNoneRadio", axaml);
    }

    // R-misc-8: WorkspaceViewModel.NewWorkspace writes the shipped entry's OWN raw bytes into
    // tech/<id>.ctech — never re-serializes through TechPersistence.Serialize (which would be a
    // harmless but pointless round trip) and never StarterTechnologies.
    [Fact]
    public void WorkspaceViewModel_NewWorkspace_WritesShippedRawJson_NeverStarterTechnologies()
    {
        string src = ReadRepoFile(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));

        int methodStart = src.IndexOf("private async Task NewWorkspace(Window? owner)", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "NewWorkspace not found");
        int methodEnd = src.IndexOf("\n    private", methodStart + 1, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart, "could not find the end of NewWorkspace");
        string body = src[methodStart..methodEnd];

        Assert.Contains("ShippedTechnologies.LoadRawJson", body);
        Assert.Contains("result.TechnologyId", body);
        Assert.DoesNotContain("StarterTechnologies", body);
        Assert.DoesNotContain("NewWorkspaceTechChoice", body);
    }

    // §3.3/R-misc-10: OrphanTechnologyDialog's PCB/MMIC starter routes now come from the same
    // shipped set, not StarterTechnologies' own possibly-diverged in-memory content — "Empty" (a
    // genuinely blank technology, not a curated default) is orthogonal and deliberately unchanged.
    [Fact]
    public void OrphanTechnologyDialog_PcbAndMmicRoutes_UseShippedTechnologies_EmptyUnchanged()
    {
        string src = ReadRepoFile(Path.Combine("src", "Ui", "Views", "Dialogs", "OrphanTechnologyDialog.axaml.cs"));
        Assert.Contains("ShippedTechnologies.Load(\"mmic-GaAs_2LM_100um\")", src);
        Assert.Contains("ShippedTechnologies.Load(ShippedTechnologies.DefaultId)", src);
        Assert.Contains("StarterTechnologies.Empty()", src); // deliberately unchanged
    }
}

// Gate for docs/sonnet-briefs/brief-gerber-import-target-technology.md §6 — the Layer Mapping dialog's
// Technology row (R-gt-8, D2-D4). LayerMappingDialog is a Window and is not constructed here (this
// suite calls no Avalonia runtime API); its rules live in ImportTechnologyChoices and LayerMappingRows,
// which the dialog calls, and the one thing only the dialog decides — that no other caller shows the
// row — is held by a source scan, as LayerMappingDialogSourceTests holds that dialog's other rules.

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Diagnostics.Fixtures;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Tests;

public sealed class LayerMappingDialogTechnologyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("layer-mapping-tech-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static IReadOnlyList<TechnologyCatalogEntry> Shipped() => ShippedTechnologies.All
        .Select(e => new TechnologyCatalogEntry(e.Id, ShippedTechnologies.Load(e).Name, TechnologyOrigin.Shipped, e.ResourceName, null))
        .ToList();

    private string Install(string id) => WorkspaceCreate.InstallTechnology(_root, id);

    [Fact]
    public void TheTechnologyRow_IsShownOnlyByTheGerberConstructor()
    {
        string xaml = ReadRepoFile("src/Ui/Views/Dialogs/LayerMappingDialog.axaml");
        Assert.Matches(new Regex(@"x:Name=""TechnologyRow""[^>]*IsVisible=""False"""), xaml);

        string code = ReadRepoFile("src/Ui/Views/Dialogs/LayerMappingDialog.axaml.cs");
        int shown = code.IndexOf("TechnologyRow.IsVisible = true", StringComparison.Ordinal);
        Assert.Equal(shown, code.LastIndexOf("TechnologyRow.IsVisible = true", StringComparison.Ordinal));

        int gerberCtor = code.IndexOf("ImportMappingRequest request,", StringComparison.Ordinal);
        int nextMember = code.IndexOf("private ImportTechnologyChoice? SelectedChoice", StringComparison.Ordinal);
        Assert.InRange(shown, gerberCtor, nextMember);
    }

    [Fact]
    public void TheDefault_IsTheWorkspaceTechnologyWhenItsCopperMatches_AndNewWhenItDoesNot()
    {
        string six = Install(DocGerberFixtures.SixLayerId);
        string four = Install(DocGerberFixtures.FourLayerId);
        var choices = ImportTechnologyChoices.Build(_root, copperCount: 6, Shipped());

        var matched = choices[ImportTechnologyChoices.DefaultIndex(choices, six)];
        Assert.Equal(ImportTechnologyChoiceKind.Workspace, matched.Kind);
        Assert.Equal(Path.GetFullPath(six), Path.GetFullPath(matched.Path!));

        Assert.Equal(0, ImportTechnologyChoices.DefaultIndex(choices, four));
        Assert.Equal(ImportTechnologyChoiceKind.New, choices[0].Kind);
    }

    [Fact]
    public void ATechnologyWithADifferentCopperCount_IsListedDisabled_AndSaysItsCount()
    {
        string four = Install(DocGerberFixtures.FourLayerId);
        var choices = ImportTechnologyChoices.Build(_root, copperCount: 6, Shipped());

        var row = choices.Single(c => c.Kind == ImportTechnologyChoiceKind.Workspace && c.Path == four);
        Assert.False(row.IsEnabled);
        Assert.EndsWith("4 copper", row.Label);
    }

    /// <summary>The targets are in the .ctech's order, not paint order (designer round 21).</summary>
    [Fact]
    public void TheTargets_AreInTheTechnologysOrder()
    {
        var tech = new Technology
        {
            Layers =
            {
                new LayerDef { Key = new LayerKey(1, 0), Name = "Top",    ZOrder = 20 },
                new LayerDef { Key = new LayerKey(2, 0), Name = "Bottom", ZOrder = 0 },
            },
        };

        Assert.Equal(["Top", "Bottom"], LayerMappingRows.Targets(tech).Select(t => t.Name));
    }

    [Fact]
    public void UsingATechnology_OffersNoAddToTechnology()
    {
        Install(DocGerberFixtures.SixLayerId);
        var choices = ImportTechnologyChoices.Build(_root, copperCount: 6, Shipped());
        var use = choices.First(c => c.Kind == ImportTechnologyChoiceKind.Workspace);
        var row = new LayerMappingRow(new LayerKey(900, 0), "Mystery", 3, null, LayerMatchKind.NoMatch,
            new LayoutFragment.LayerReconciliationChoice(LayoutFragment.LayerReconciliationAction.KeepUnknown));

        var vms = LayerMappingRows.Build([row], use.Technology, use.AllowsAddToTechnology);

        Assert.DoesNotContain(vms[0].Actions, a => a.Action == LayoutFragment.LayerReconciliationAction.AddToTechnology);
    }

    [Fact]
    public void ACatalogChoice_IsCopiedOnlyOnContinue_AndAnIdenticalCopyIsReused()
    {
        var catalogRow = ImportTechnologyChoices.Build(_root, copperCount: 6, Shipped())
            .Single(c => c.Kind == ImportTechnologyChoiceKind.Catalog && c.CatalogId == DocGerberFixtures.SixLayerId);
        string copy = Path.Combine(_root, "tech", DocGerberFixtures.SixLayerId + ".ctech");
        Assert.False(File.Exists(copy));

        var first = ImportTechnologyChoices.Commit(catalogRow, _root);
        Assert.Equal(Path.GetFullPath(copy), first.UsePath);
        var written = File.GetLastWriteTimeUtc(copy);
        File.SetLastWriteTimeUtc(copy, written.AddMinutes(-5));

        var second = ImportTechnologyChoices.Commit(catalogRow, _root);
        Assert.Equal(first, second);
        Assert.Equal(written.AddMinutes(-5), File.GetLastWriteTimeUtc(copy));
    }

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }
}

using System.IO;
using System.Linq;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The schematic templates circuitRF ships (owner request): "New Cell" — and the tree's own "New
/// Schematic" — offer a list of them, and the created schematic is pre-populated.
///
/// These are the "ship" gates: they load the templates through the SAME reader the schematic editor
/// uses, so a template that circuitRF could not itself open fails here rather than in front of a
/// user. Every one of them reads the EMBEDDED resource, never the file on disk — that is what
/// actually proves the templates are inside the compiled assembly and will survive
/// <c>dotnet publish</c>.
/// </summary>
public sealed class ShippedSchematicTemplatesTests
{
    [Fact]
    public void EveryAuthoredTemplateFile_IsEmbeddedInTheAssembly()
    {
        // Derived from the folder rather than a hand-written list, so dropping a new template in
        // (the whole point of the file-per-template design) cannot silently fail to ship: the file
        // would be on disk and absent from All, and this test says so by name.
        var dir = Path.Combine(RepoRoot(), "src", "Ui", "resources", "schematic-templates");
        var onDisk = Directory.GetFiles(dir, "*.csch")
                              .Select(Path.GetFileNameWithoutExtension)
                              .OrderBy(x => x, System.StringComparer.Ordinal)
                              .ToArray();

        Assert.NotEmpty(onDisk);
        Assert.Equal(onDisk, ShippedSchematicTemplates.All.Select(t => t.Id).ToArray());
    }

    [Fact]
    public void TheThreeOwnerAuthoredTemplates_AreThere()
    {
        var ids = ShippedSchematicTemplates.All.Select(t => t.Id).ToArray();

        Assert.Contains("FET_Curve_Tracer", ids);
        Assert.Contains("FET_Loadpull_Pursuit", ids);
        Assert.Contains("FET_S-Parameters", ids);
    }

    [Theory]
    [InlineData("FET_Curve_Tracer")]
    [InlineData("FET_Loadpull_Pursuit")]
    [InlineData("FET_S-Parameters")]
    public void EveryTemplate_OpensThroughTheOrdinarySchematicReader_AndCarriesRealContent(string id)
    {
        var model = ShippedSchematicTemplates.Load(id);

        // A template that parsed but produced an empty schematic would be worse than no template —
        // it would look like the feature did nothing.
        Assert.NotEmpty(model.Components);
        Assert.NotEmpty(model.Analyses);
    }

    [Fact]
    public void ATemplatesModel_IsIndependentPerLoad_SoOneNewCellCannotDisturbAnother()
    {
        var a = ShippedSchematicTemplates.Load("FET_Curve_Tracer");
        var b = ShippedSchematicTemplates.Load("FET_Curve_Tracer");

        Assert.NotSame(a, b);
        Assert.NotSame(a.Components[0], b.Components[0]);

        a.Components.RemoveAt(0);
        Assert.NotEqual(a.Components.Count, b.Components.Count);
    }

    [Fact]
    public void TheDestinationDirectory_IsCarriedIntoTheModel_SoCellRefsResolveBeforeTheFirstSave()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-tpl-" + System.Guid.NewGuid().ToString("N")[..8]);

        var model = ShippedSchematicTemplates.Load("FET_S-Parameters", dir);

        Assert.Equal(dir, model.SchematicDirectory);
    }

    [Fact]
    public void DisplayNames_ReadAsWordsWhileIdsStayFilesystemSafe()
    {
        Assert.Equal("FET S-Parameters", ShippedSchematicTemplates.DisplayNameFor("FET_S-Parameters"));
        Assert.Equal("FET Curve Tracer", ShippedSchematicTemplates.DisplayNameFor("FET_Curve_Tracer"));

        // Hyphens and capitalisation are the author's own — nothing but '_' is rewritten.
        Assert.All(ShippedSchematicTemplates.All,
            t => Assert.DoesNotContain('_', t.DisplayName));
    }

    [Fact]
    public void ATemplateSurvivesTheWriteTheCreationPathPerforms()
    {
        // The creation path is Load → SchematicPersistence.SaveToFile → (the tab reads the same
        // model). This drives that write and reads the file back, so "the schematic is
        // pre-populated" is checked against what actually lands on disk, not just the in-memory
        // parse. WorkspaceViewModel itself cannot be constructed headlessly (it stands up Dock),
        // hence the two source scans below rather than driving it directly.
        string dir = Path.Combine(Path.GetTempPath(), "crf-tpl-" + System.Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var model = ShippedSchematicTemplates.Load("FET_S-Parameters", dir);
            string path = Path.Combine(dir, "Amp.csch");
            SchematicPersistence.SaveToFile(path, model, cellName: "Amp");

            var (reloaded, _, cellName) = SchematicPersistence.LoadFromFile(path);

            Assert.Equal("Amp", cellName);   // the new cell's name, not the template's
            Assert.Equal(model.Components.Count, reloaded.Components.Count);
            Assert.Equal(model.Analyses.Count,   reloaded.Analyses.Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ThePickerIsOffered_OnlyWhereANewSchematicIsActuallyCreated()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/ViewModels/WorkspaceViewModel.cs"));

        // Both New Cell paths (each creates the cell's primary schematic) plus the tree's own
        // New Schematic — three, and no more. A picker on New Symbol / New Layout / Duplicate Cell
        // would offer a choice that does nothing.
        Assert.Equal(3, CountOccurrences(src, "OfferSchematicTemplates("));

        foreach (var prompt in new[]
                 {
                     "new InputNameDialog(\"New Symbol\"",
                     "new InputNameDialog(\"New Layout\"",
                     "new InputNameDialog(\"Duplicate Cell\"",
                 })
        {
            int at = src.IndexOf(prompt, System.StringComparison.Ordinal);
            Assert.True(at > 0, $"expected to find the prompt {prompt}");
            Assert.DoesNotContain("OfferSchematicTemplates", src[at..(at + 400)]);
        }
    }

    [Fact]
    public void TheChosenTemplate_IsThreadedIntoTheCreationPath()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/ViewModels/WorkspaceViewModel.cs"));

        // Offering the picker but dropping its answer would look exactly like the feature working
        // and produce an empty schematic every time — the one failure a source scan can still catch.
        Assert.Equal(3, CountOccurrences(src, "dialog.SelectedTemplate"));
        Assert.Contains("ShippedSchematicTemplates.Load(template, schematicDir)", src);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    [Fact]
    public void AnUnknownTemplateName_ThrowsRatherThanSilentlyProducingAnEmptySchematic()
        => Assert.Throws<System.InvalidOperationException>(
            () => ShippedSchematicTemplates.Load("no-such-template"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

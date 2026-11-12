using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Tests.ThreeD;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Hzn;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// HZN-3 R-hzn3-2 — a design held in a Hertzon project is the 3D editor's document through its document source: the
/// document is HznToC3d.Translate's, the source is read-only, the project's materials are the document's own, and a design
/// that is refused opens nothing and lists every item.
/// </summary>
public sealed class HznDocumentSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf_hznsrc_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<C3dEditorViewModel> _open = [];

    public HznDocumentSourceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Copy(string fixture)
    {
        string path = Path.Combine(_root, fixture);
        File.Copy(Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), fixture), path);
        return path;
    }

    [Fact]
    public void ADesignOpens_AsTheTranslationsDocument_WithTheProjectsMaterials()
    {
        string path = Copy("box-and-lumped-port.hzn");
        var cache = new TechnologyCache();
        var source = new HznDesignSource(path, "Block", cache);

        var document = source.Load();

        var expected = HznToC3d.Translate(HznFile.Read(path).File!.Project, "Block");
        Assert.True(expected.Ok);
        // The two differences: the document names the held materials as its technology, and it opens with every chrome row of the
        // realistic view shown (Edges, Grid, Overlays, Air box, Ports, Boundaries, Images), which a .hzn has no Look to say.
        Assert.NotNull(document.TechRef);
        document.TechRef = null;
        var look = document.Look!;
        Assert.All(new[] { look.ShowEdges, look.ShowGrid, look.ShowOverlays, look.ShowAirBox, look.ShowPorts, look.ShowBoundaries, look.ShowImages },
                   shown => Assert.True(shown));
        document.Look = expected.Document!.Look;
        Assert.Equal(C3dPersistence.Serialize(expected.Document!), C3dPersistence.Serialize(document));
        Assert.Contains("laminate_35", cache.Get(source.TechnologyPath)!.ResolvedMaterials.Select(m => m.Name));

        Assert.Equal("Block — box-and-lumped-port.hzn", source.Title);
        Assert.Null(source.ReadOnlyReason);
        Assert.Null(source.Save(document));       // HZN-5: an unedited document saves, and writes nothing
        Assert.True(File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(Path.Combine(
            FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), "box-and-lumped-port.hzn"))));       // nothing was written
    }

    [Fact]
    public void TheEditorOnTheSource_ElaboratesWithTheProjectsMaterials_AndSaveAsWritesNoCopyItself()
    {
        string path = Copy("box-and-lumped-port.hzn");
        var cache = new TechnologyCache();
        var source = new HznDesignSource(path, "Block", cache);
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, source.Load(), () => fake, () => null, a => a(), cache, source: source);
        _open.Add(vm);
        vm.Viewer.Resized(400, 300);
        vm.Start();
        Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(60)), "the scene never settled");

        Assert.True(vm.Elaboration!.Ok, string.Join(" ", vm.Elaboration.Refusals));
        Assert.Contains("laminate_35", vm.Elaboration.Materials.Select(m => m.Name));
        Assert.Equal(source.Title, vm.Viewer.Title);
        Assert.False(vm.IsDirty);

        Assert.Null(vm.Save());                   // nothing was edited: nothing is written
        string copy = Path.Combine(_root, "copy.c3d");
        Assert.NotNull(vm.SaveAs(copy));          // a copy of a held design is Convert to .c3d's to make
        Assert.False(File.Exists(copy));
        Assert.Same(source, vm.Source);
    }

    [Fact]
    public void ARefusedDesign_OpensNothing_AndListsEveryItem()
    {
        string path = Copy("unsupported.hzn");
        var cache = new TechnologyCache();
        var all = HznToC3d.Translate(HznFile.Read(path).File!.Project, "Bad").Refusals;
        Assert.True(all.Count > 1);

        var source = new HznDesignSource(path, "Bad", cache);
        var thrown = Assert.Throws<HznOpenException>(() => source.Load());
        Assert.Equal(all.Count, thrown.Items.Count);
        Assert.False(cache.IsHeld(source.TechnologyPath));

        // …and the workspace shows it as an unopenable document is shown: a dialog over the whole list, no tab.
        var vm = new WorkspaceViewModel();
        var shown = new List<(string Title, string Text)>();
        vm.HznRefusalDialog = (title, text) => { shown.Add((title, text)); return Task.CompletedTask; };
        vm.OpenHznDesign(path, "Bad");

        var (title, text) = Assert.Single(shown);
        Assert.Equal("Bad — unsupported.hzn", title);
        foreach (var item in all) Assert.Contains(item.What, text);
        Assert.Null(vm.FindOpenDocument(HznDesignSource.KeyFor(path, "Bad")));
    }
}

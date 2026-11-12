using System.Text.RegularExpressions;
using CircuitRF.Cli;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Tests.ThreeD;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Hzn;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// HZN integration — the seams between the pieces the series built apart: an opened design runs through the editor's Simulate
/// with the same names and the same project materials as <c>em design.hzn</c>, and the shell's connections to the pieces exist.
/// </summary>
public sealed class HznIntegrationTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "crf_hznint_" + Guid.NewGuid().ToString("N")[..8])).FullName;


    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void AnOpenedDesign_RunsUnderTheCommandLinesNames_OnTheProjectsMaterials_AndReleasesThemOnClose()
    {
        string path = Path.Combine(_root, "microstrip-line.hzn");
        File.Copy(Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), "microstrip-line.hzn"), path);
        var (plan, refusal) = HznEmergeRun.Plan(path, null, null);
        Assert.True(plan is not null, refusal?.Render());

        var cache = new TechnologyCache();
        var source = new HznDesignSource(path, plan.Design, cache);
        var vm = new C3dEditorViewModel(path, source.Load(), () => new PatchRecordingBackend(), () => null, a => a(), cache, source: source);
        try
        {
            // The run is filed where the command line files it, so the editor and `em` agree on the folder, the setup's name and the record.
            Assert.Equal(plan.DocumentPath, vm.RunPath);
            var (setup, fromCem, why) = vm.RunSetupFor(plan.C3dSetup);
            Assert.True(setup is not null, why);
            Assert.False(fromCem);
            Assert.Equal(plan.Setup.Name, setup.Name);

            // The document names the held materials by a relative reference; from the run's folder it finds the same ones.
            var made = C3dProblemAssembly.Assemble(setup, vm.RunDocument(), vm.RunPath, null, vm.NewRunElaborator());
            Assert.True(made.Ok, made.Refusal);
            Assert.Equal(3.55, made.Problem!.Materials.Single(m => m.Name == "sub").Epsr);

            // The setups are written for EMerge: the panel offers no other solver, and no thermal one.
            vm.RestoreActiveSetup(null);
            vm.SelectedSetupItem = vm.SetupItems.First(i => i.Name == plan.C3dSetup);
            var panel = vm.SetupEditor?.ViewModel;
            Assert.NotNull(panel);
            Assert.Equal([CircuitRF.Design.Em3d.EmergeSolver.Id], panel.Solver3DChoiceList.Select(r => r.Value.Reference));
        }
        finally { vm.Dispose(); }

        source.Release();
        Assert.False(cache.IsHeld(source.TechnologyPath));
        Assert.False(cache.IsHeld(source.RunTechnologyPath));
    }

    [Fact]
    public async Task ExportingAnOpenedDesign_WritesItWithTheProjectsMaterials_UnderItsOwnName()
    {
        string path = Path.Combine(_root, "microstrip-line.hzn");
        File.Copy(Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), "microstrip-line.hzn"), path);
        var cache = new TechnologyCache();
        var source = new HznDesignSource(path, "Line", cache);
        var document = source.Load();
        document.TechRef = null;

        // Without the project's materials the same document names a material nothing resolves; with them it exports.
        var bare = new HznExportDialogViewModel(C3dPersistence.Deserialize(C3dPersistence.Serialize(document)), path, null, _root);
        await bare.PlanAsync();
        Assert.False(bare.Design!.Ok);
        var held = new HznExportDialogViewModel(document, path, null, _root, source.ProjectTechnology, source.Design);
        await held.PlanAsync();
        Assert.True(held.Design!.Ok, held.Report);
        Assert.Equal("Line", held.DesignName);
        Assert.Equal(Path.Combine(_root, "Line.hzn"), held.TargetPath);
        Assert.Contains("sub", held.Design.Materials.Select(m => m.GetString("name")));
        source.Release();
    }

    /// <summary>A damaged project is a refusal at every door a person reaches it by, never an exception: the tree's listing, the
    /// open, the run's plan, the instance picker and a conversion, over a fixture cut at several places and over bytes that are not a project.</summary>
    [Fact]
    public void ADamagedProject_IsARefusalAtEveryEntryPoint_NeverAnException()
    {
        var damaged = new List<(string Name, byte[] Bytes)> { ("empty", []), ("garbage", [.. Enumerable.Range(0, 2000).Select(i => (byte)(i * 31))]), ("deep", [.. Enumerable.Repeat((byte)'[', 4000)]) };
        foreach (string fixture in new[] { "microstrip-line.hzn", "mapping.hzn", "faces.hzn", "instance.hzn" })
        {
            byte[] good = File.ReadAllBytes(Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), fixture));
            foreach (double cut in new[] { 0.05, 0.25, 0.5, 0.75, 0.95, 0.999 })
                damaged.Add(($"{fixture}@{cut}", good[..(int)(good.Length * cut)]));
        }

        foreach (var (name, bytes) in damaged)
        {
            string path = Path.Combine(_root, name.Replace('@', '_') + ".hzn");
            File.WriteAllBytes(path, bytes);
            Assert.Equal(DocumentKind.Hertzon, DocumentKinds.Classify(path));     // a damaged project is reported as one, not called somebody else's file
            void Door(string door, Action act)
            {
                try { act(); }
                catch (Exception e) { Assert.Fail($"{door} threw {e.GetType().Name} on {name}: {e.Message}"); }
            }
            Door("listing", () => HznDesignList.Of(path));
            Door("plan", () => HznEmergeRun.Plan(path, null, null));
            Door("discover", () => HznInstances.Discover(_root));
            Door("open", () =>
            {
                var source = new HznDesignSource(path, "Line", new TechnologyCache());
                try { source.Load(); } catch (HznOpenException) { }
                source.Release();
            });
            Door("convert", () => HznConvert.Convert(new HznConvertRequest(path, null, Path.Combine(_root, "cell_" + Guid.NewGuid().ToString("N")[..6]), false, false)));
        }
    }

    [Fact]
    public void TheShell_ConnectsTheDesignRow_TheInstanceAndTheEditorsSimulate()
    {
        string root = RepoRoot();
        string Src(string rel) => StripComments(File.ReadAllText(Path.Combine(root, rel)));
        // Open Design on a Hertzon instance opens the design in its own tab.
        Assert.Contains("OpenHertzonDesignRequested +=", Src("src/Ui/ViewModels/WorkspaceViewModel.ThreeD.cs"));
        // A design row is a drag source whose payload a 3D view reads.
        Assert.Contains("new HznDesignDragPayload(", Src("src/Ui/Views/ProjectTree/ProjectTreeView.axaml.cs"));
        // The row's Simulate is the editor's Simulate, and an unsaved tab is asked about before Hertzon or a conversion reads the file.
        string hzn = Src("src/Ui/ViewModels/WorkspaceViewModel.Hzn.cs");
        Assert.Matches(@"SimulateHznDesignAsync\(.*?RunC3dSetupAsync\(doc\.ViewModel, null\)", Regex.Replace(hzn, @"\s+", " "));
        Assert.Contains("SettleUnsavedHznDesignAsync(", Src("src/Ui/ViewModels/WorkspaceViewModel.HertzonRun.cs"));
        Assert.Contains("SettleUnsavedHznDesignAsync(", hzn);
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitRF.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    private static string StripComments(string src) =>
        Regex.Replace(Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");
}

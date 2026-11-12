using System.Diagnostics;
using System.Text.RegularExpressions;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// HZN-6 — a Hertzon design converted into a cell's native 3D view. The fixtures are hand-written <c>.hzn</c> files; the
/// workspace technologies are made here, with exactly the materials each claim needs.
/// </summary>
public sealed class HznConvertTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-hzn6-" + Guid.NewGuid().ToString("N")[..10]);

    public HznConvertTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string Fixture(string name) => Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), name);

    /// <summary>A workspace whose technology defines <paramref name="materials"/> and nothing else a material lookup could find.</summary>
    private (string Dir, string Tech) Workspace(string name, params TechMaterial[] materials)
    {
        var made = WorkspaceCreate.Create(_root, name, WorkspaceCreate.DefaultTechnologyId);
        var tech = TechPersistence.LoadFromFile(made.TechPath!);
        tech.Materials = [.. materials];
        tech.MaterialLibraries = null;
        TechPersistence.SaveToFile(made.TechPath!, tech);
        return (made.WorkspaceDir, made.TechPath!);
    }

    private static C3dTranslation Translation()
    {
        var read = HznFile.Read(Fixture("mapping.hzn"));
        var t = HznToC3d.Translate(read.File!.Project, "Mapping");
        Assert.True(t.Ok, t.Refusal?.Render());
        return t;
    }

    private static HznConvertResult DoConvert(string cell, bool add = false, bool replace = false, string? design = "Mapping", string file = "mapping.hzn")
        => HznConvert.Convert(new HznConvertRequest(Fixture(file), design, cell, add, replace));

    [Fact]
    public void ANewCell_HoldsAViewEqualToTheTranslation_AndTheSourceIsUntouched()
    {
        var (ws, _) = Workspace("ws");
        string source = Fixture("mapping.hzn");
        string before = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source)));
        var stamp = File.GetLastWriteTimeUtc(source);

        var result = DoConvert(Path.Combine(ws, "Mapping"));
        Assert.True(result.Ok, result.Refusal?.Render());
        Assert.True(result.CellCreated);
        Assert.True(File.Exists(Path.Combine(ws, "Mapping", ".ccell")));
        Assert.Equal(Path.Combine(ws, "Mapping", "3d", "Mapping.c3d"), result.ViewPath);
        Assert.Equal(C3dPersistence.Serialize(Translation().Document!), C3dPersistence.Serialize(C3dPersistence.LoadFromFile(result.ViewPath!)));

        Assert.Equal(before, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source))));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(source));
    }

    [Fact]
    public void MissingMaterials_AreReportedAndNotWritten_UntilTheFlagSaysSo()
    {
        var (ws, techPath) = Workspace("ws");
        byte[] tech = File.ReadAllBytes(techPath);
        var expected = Translation().Technology!.Materials.Select(m => m.Name).Order().ToList();
        Assert.NotEmpty(expected);

        var plain = DoConvert(Path.Combine(ws, "A"));
        Assert.True(plain.Ok, plain.Refusal?.Render());
        Assert.Equal(expected, plain.MissingMaterials.Order().ToList());
        Assert.Contains(plain.Findings, d => d.Id == "hzn.convert.materials-missing");
        Assert.Equal(tech, File.ReadAllBytes(techPath));

        var added = DoConvert(Path.Combine(ws, "B"), add: true);
        Assert.True(added.Ok, added.Refusal?.Render());
        Assert.Equal(expected, added.AddedMaterials.Order().ToList());
        var reloaded = TechPersistence.LoadFromFile(techPath);
        foreach (string m in expected) Assert.NotNull(reloaded.FindMaterial(m));

        // Now the technology has them all and an identical copy is a match, not a second addition.
        var again = DoConvert(Path.Combine(ws, "C"), add: true);
        Assert.True(again.Ok, again.Refusal?.Render());
        Assert.Empty(again.MissingMaterials);
        Assert.Empty(again.AddedMaterials);
    }

    [Fact]
    public void AMaterialOfTheSameNameWithOtherProperties_IsRefusedListingBoth_AndNothingIsWritten()
    {
        var lam = Translation().Technology!.Materials.Single(m => m.Name == "lam");
        var (ws, techPath) = Workspace("ws", new TechMaterial { Name = "lam", Epsr = (lam.Epsr ?? 1) + 1, TanD = lam.TanD });
        byte[] tech = File.ReadAllBytes(techPath);

        var result = DoConvert(Path.Combine(ws, "Mapping"), add: true);
        Assert.False(result.Ok);
        Assert.Equal("hzn.convert.material-conflict", result.Refusal!.Id);
        string text = result.Refusal.Render();
        Assert.Contains($"epsr {lam.Epsr!.Value + 1}", text);
        Assert.Contains($"epsr {lam.Epsr.Value}", text);
        Assert.Contains("relative permittivity", text);
        Assert.False(Directory.Exists(Path.Combine(ws, "Mapping")));
        Assert.Equal(tech, File.ReadAllBytes(techPath));
    }

    [Fact]
    public void AnExistingView_IsRefusedUnlessReplacingIsChosen()
    {
        var (ws, _) = Workspace("ws");
        string cell = Path.Combine(ws, "Mapping");
        Assert.True(DoConvert(cell).Ok);
        string view = Path.Combine(cell, "3d", "Mapping.c3d");
        File.WriteAllText(view, "{}");

        var refused = DoConvert(cell);
        Assert.Equal("hzn.convert.view-exists", refused.Refusal!.Id);
        Assert.Equal("{}", File.ReadAllText(view));

        var replaced = DoConvert(cell, replace: true);
        Assert.True(replaced.Ok, replaced.Refusal?.Render());
        Assert.True(replaced.ViewReplaced);
        Assert.False(replaced.CellCreated);
        Assert.Equal(C3dPersistence.Serialize(Translation().Document!), C3dPersistence.Serialize(C3dPersistence.LoadFromFile(view)));
    }

    [Fact]
    public void SeveralDesigns_AreRefusedListingThem_WhenNoneIsNamed_AndARefusedDesignWritesNoCell()
    {
        var (ws, _) = Workspace("ws");
        var several = DoConvert(Path.Combine(ws, "X"), design: null, file: "two-designs.hzn");
        Assert.Equal("hzn.convert.design-required", several.Refusal!.Id);
        Assert.Contains("First", several.Refusal.Render());
        Assert.Contains("Second", several.Refusal.Render());

        var refused = DoConvert(Path.Combine(ws, "Y"), design: null, file: "unsupported.hzn");
        Assert.Equal("hzn.translate.refused", refused.Refusal!.Id);
        Assert.False(Directory.Exists(Path.Combine(ws, "X")));
        Assert.False(Directory.Exists(Path.Combine(ws, "Y")));
    }

    [Fact]
    public void AStepFileAnObjectNames_IsCopiedIntoTheCellsThreeDFolder()
    {
        string hznDir = Path.Combine(_root, "src");
        Directory.CreateDirectory(hznDir);
        File.WriteAllText(Path.Combine(hznDir, "part.step"), "ISO-10303-21;");
        var doc = new C3dDocument();
        var step = new C3dStep { Name = "Imp", File = "part.step" };
        doc.Objects.Add(step);
        string threeD = Path.Combine(_root, "cell", "3d");

        var copied = HznConvert.CopyStepFiles(doc, hznDir, threeD, out var refusal);
        Assert.Null(refusal);
        Assert.Equal([Path.Combine(threeD, "part.step")], copied);
        Assert.Equal("ISO-10303-21;", File.ReadAllText(Path.Combine(threeD, "part.step")));
        Assert.Equal("part.step", step.File);

        File.WriteAllText(Path.Combine(hznDir, "part.step"), "different");
        HznConvert.CopyStepFiles(doc, hznDir, threeD, out var differs);
        Assert.Equal("hzn.convert.step-differs", differs!.Id);
        Assert.Equal("ISO-10303-21;", File.ReadAllText(Path.Combine(threeD, "part.step")));
    }

    [Fact]
    public void TheVerbAsAProcess_WritesTheBytesTheInProcessCallWrites()
    {
        var (wsA, _) = Workspace("wsA");
        var (wsB, _) = Workspace("wsB");

        var run = RunCli("convert", Fixture("mapping.hzn"), "--design", "Mapping", "-o", Path.Combine(wsA, "Mapping"));
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);
        var inProcess = DoConvert(Path.Combine(wsB, "Mapping"));
        Assert.True(inProcess.Ok, inProcess.Refusal?.Render());

        Assert.Equal(File.ReadAllBytes(inProcess.ViewPath!), File.ReadAllBytes(Path.Combine(wsA, "Mapping", "3d", "Mapping.c3d")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(wsB, "Mapping", ".ccell")), File.ReadAllBytes(Path.Combine(wsA, "Mapping", ".ccell")));
        Assert.Contains(Path.Combine(wsA, "Mapping", "3d", "Mapping.c3d"), run.StdOut);
        Assert.Contains("does not define", run.StdErr);

        // A project of several designs and no --design is the same refusal the function returns.
        var several = RunCli("convert", Fixture("two-designs.hzn"), "-o", Path.Combine(wsA, "Two"));
        Assert.Equal(1, several.ExitCode);
        Assert.Contains("--design", several.StdErr);
        Assert.False(Directory.Exists(Path.Combine(wsA, "Two")));
    }

    [Fact]
    public void TheVerbAndTheDialogsFlow_HoldNoSecondCopyOfTheConversion()
    {
        foreach (string file in new[] { "src/Cli/HznConvertVerb.cs", "src/Ui/ViewModels/WorkspaceViewModel.HznConvert.cs", "src/Ui/Views/Dialogs/HznConvertDialog.cs" })
        {
            string code = StripComments(File.ReadAllText(FixturePaths.Require(file)));
            foreach (string own in new[] { "HznToC3d.Translate", "CellCreate.", "TechPersistence.", "C3dPersistence." })
                Assert.DoesNotContain(own, code);
        }
        Assert.Contains("HznConvert.Convert(", StripComments(File.ReadAllText(FixturePaths.Require("src/Cli/HznConvertVerb.cs"))));
        Assert.Contains("HznConvert.Convert(", StripComments(File.ReadAllText(FixturePaths.Require("src/Ui/ViewModels/WorkspaceViewModel.HznConvert.cs"))));
    }

    private static string StripComments(string src) =>
        Regex.Replace(Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(HznConvertTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }
}

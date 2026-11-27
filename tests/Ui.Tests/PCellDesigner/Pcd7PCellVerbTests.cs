// Owner: PCD-7 (brief-pcd-7). The gate for `circuitrf pcell`, as AuthoringCliVerbTests gates `new`: the CLI as a
// process against the in-process call the window makes, byte for byte, and a comment-stripped scan that the verb file
// holds no logic of its own.

using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CircuitRF.Cli.Serve;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.PCellDesigner;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;

namespace CircuitRF.Ui.Tests.PCellDesigner;

[Collection(UserStateDirectoryCollection.Name)]
public sealed class Pcd7PCellVerbTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-pcd7-" + Guid.NewGuid().ToString("N")[..12]);
    private string StateDir => Path.Combine(_root, "state");

    public Pcd7PCellVerbTests()
    {
        Directory.CreateDirectory(StateDir);
        CircuitRF.Ui.AppDataRoot.RedirectTo(StateDir);
    }

    public void Dispose()
    {
        CircuitRF.Ui.AppDataRoot.RedirectTo(null);
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>R-pcd7-3: <c>pcell render --set</c> draws the bytes <c>render</c> draws of the layout
    /// <see cref="DesignedCellLayout.At"/> makes at those values, saved where the cell's layout goes; and the value
    /// changed the picture.</summary>
    [Fact]
    public void Render_DrawsWhatRenderDrawsOfTheLayoutAtTheValues()
    {
        string cell = DesignedLine(Path.Combine(_root, "r"));
        string viaVerb = Path.Combine(_root, "verb.svg"), atDefaults = Path.Combine(_root, "defaults.svg");
        var run = RunCli("pcell", "render", cell, "-o", viaVerb, "--set", "L1=2mm", "--size", "400x300");
        Assert.True(run.ExitCode == 0, run.StdErr);
        Assert.Equal(0, RunCli("pcell", "render", cell, "-o", atDefaults, "--size", "400x300").ExitCode);
        Assert.False(Directory.Exists(Path.Combine(cell, "layout")) && Directory.GetFiles(Path.Combine(cell, "layout"), "*.clay").Length > 0,
                     "pcell render wrote a .clay");

        string ws = Path.GetDirectoryName(cell)!;
        string clay = Path.Combine(cell, "layout", "Line.clay");
        var (tech, _) = TechnologyResolver.ResolveForDocument(null, clay, Path.Combine(ws, ".cws"), new TechnologyCache());
        var at = DesignedCellLayout.At(cell, new Dictionary<string, Value> { ["L1"] = new Value(0.002) }, ws, tech.Tech!, tech.ResolvedPath);
        Assert.Null(at.Refusal);
        LayoutPersistence.SaveToFile(clay, at.Layout!);
        string viaRender = Path.Combine(_root, "render.svg");
        Assert.Equal(0, RunCli("render", clay, "-o", viaRender, "--size", "400x300").ExitCode);

        Assert.Equal(File.ReadAllBytes(viaRender), File.ReadAllBytes(viaVerb));
        Assert.NotEqual(File.ReadAllBytes(atDefaults), File.ReadAllBytes(viaVerb));
    }

    /// <summary>R-pcd7-1: <c>pcell new</c> writes the cell folder <see cref="PCellDesign.Create"/> writes.</summary>
    [Fact]
    public void New_WritesTheCellFolderCreateWrites()
    {
        string cliWs = Workspace(Path.Combine(_root, "n-cli")), guiWs = Workspace(Path.Combine(_root, "n-gui"));
        var run = RunCli("pcell", "new", cliWs, "Pad");
        Assert.True(run.ExitCode == 0, run.StdErr);
        PCellDesign.Create(guiWs, "Pad");
        AssertSameTree(Path.Combine(guiWs, "Pad"), Path.Combine(cliWs, "Pad"));
    }

    /// <summary>R-pcd7-2: <c>pcell check</c> reports <see cref="PCellDesignCheck.Run"/>'s findings and exits as
    /// <c>check</c>: 1 on an error, else 0, and 1 on a warning under <c>--severity warning</c>.</summary>
    [Fact]
    public void Check_ReportsTheFindingsAndExitsAsCheck()
    {
        string cell = DesignedLine(Path.Combine(_root, "c"));
        var findings = PCellDesignCheck.Run(cell);
        int errors = findings.Count(f => f.Severity == DiagnosticSeverity.Error);
        int warnings = findings.Count(f => f.Severity == DiagnosticSeverity.Warning);

        var run = RunCli("pcell", "check", cell);
        Assert.Equal(errors > 0 ? 1 : 0, run.ExitCode);
        Assert.Contains($"{errors} error(s), {warnings} warning(s)", run.StdOut, StringComparison.Ordinal);
        foreach (var f in findings) Assert.Contains(f.Render(), run.StdErr, StringComparison.Ordinal);
        Assert.Equal(errors + warnings > 0 ? 1 : 0, RunCli("pcell", "check", cell, "--severity", "warning").ExitCode);
    }

    /// <summary>R-pcd7-4: <c>pcell export</c> writes <see cref="PCellPackage.Export"/>'s bytes, and <c>pcell import</c>
    /// writes the cell folder <see cref="PCellPackage.Import"/> writes.</summary>
    [Fact]
    public void ExportAndImport_WriteWhatThePackageCallsWrite()
    {
        string cell = DesignedLine(Path.Combine(_root, "e"));
        string viaCli = Path.Combine(_root, "cli.cpcell"), viaCall = Path.Combine(_root, "call.cpcell");
        var run = RunCli("pcell", "export", cell, "-o", viaCli);
        Assert.True(run.ExitCode == 0, run.StdErr);
        Assert.NotNull(PCellPackage.Export(cell, viaCall).Path);
        Assert.Equal(File.ReadAllBytes(viaCall), File.ReadAllBytes(viaCli));

        string cliWs = Workspace(Path.Combine(_root, "i-cli")), guiWs = Workspace(Path.Combine(_root, "i-gui"));
        run = RunCli("pcell", "import", viaCli, "--into", cliWs);
        Assert.True(run.ExitCode == 0, run.StdErr);
        string? imported = PCellPackage.Import(viaCli, guiWs).Path;
        Assert.NotNull(imported);
        AssertSameTree(imported!, Path.Combine(cliWs, "Line"));
    }

    /// <summary>R-pcd7-5: the MCP tool's render call is the verb's argv.</summary>
    [Fact]
    public void TheMcpTool_TranslatesToTheVerbsArgv()
    {
        Directory.CreateDirectory(_root);
        // As a client sends it: parsed JSON, which is what the adapter reads.
        var call = JsonNode.Parse(new JsonObject
        {
            ["what"] = "render", ["cell"] = Path.Combine(_root, "C"), ["output"] = Path.Combine(_root, "c.svg"),
            ["set"] = new JsonArray("Lpad=200um"), ["window"] = "0um,0um,1mm,1mm",
        }.ToJsonString())!.AsObject();
        var argv = ToolCatalog.ToArgv("pcell", call, PathRoot.Open(_root, out _)!, out var refusal);
        Assert.Null(refusal);
        Assert.Equal(["pcell", "render", Path.Combine(_root, "C"), "-o", Path.Combine(_root, "c.svg"),
                      "--set", "Lpad=200um", "--window", "0um,0um,1mm,1mm", "--json"], argv!);
    }

    /// <summary>R-pcd7-6: <c>pcell --help</c> names every noun and the options section.</summary>
    [Fact]
    public void Help_NamesEveryNoun()
    {
        var run = RunCli("pcell", "--help");
        Assert.Equal(0, run.ExitCode);
        foreach (string s in new[] { "pcell options:", "pcell new", "pcell check", "pcell render", "pcell export", "pcell import" })
            Assert.Contains(s, run.StdOut, StringComparison.Ordinal);
    }

    /// <summary>The verb calls the five functions the window calls and none of their insides (brief-pcd-7 §0).</summary>
    [Fact]
    public void TheVerbHoldsNoLogic()
    {
        string code = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cli", "PCellVerb.cs")));
        foreach (string call in new[] { "PCellDesign.Create(", "PCellDesignCheck.Run(", "DesignedCellLayout.At(",
                                        "PCellPackage.Export(", "PCellPackage.Import(", "Render.RunLayout(" })
            Assert.Contains(call, code, StringComparison.Ordinal);
        foreach (string inside in new[] { "ZipArchive", "ZipFile", "SHA256", "SchematicLayoutPlanner.Plan", "EmEvaluation",
                                          "CellCreate.", "CellPersistence.", "SchematicPersistence.", "LayoutRenderer",
                                          "LayoutPersistence.Save", "ContentKey" })
            Assert.DoesNotContain(inside, code, StringComparison.Ordinal);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    /// <summary>A workspace holding a designed cell <c>Line</c>: SDL's three-line through line, its first length the
    /// VAR <c>L1</c> (5 mm).</summary>
    private static string Workspace(string parent)
    {
        WorkspaceCreate.Create(parent, "Ws", WorkspaceCreate.DefaultTechnologyId);
        return Path.Combine(parent, "Ws");
    }

    private string DesignedLine(string parent)
    {
        string ws = Workspace(parent);
        var made = CellCreate.Create(ws, "Line", CellViews.Schematic | CellViews.Symbol);

        var (model, _, _) = SchematicPersistence.LoadFromFile(Path.Combine(
            RepoRoot(), "testdata", "schematic-to-layout", "C1-through-line", "schematic", "C1-through-line.csch"));
        model.Components.Single(c => c.InstanceName == "TL1").Parameters.Single(p => p.Name == "L").Expression = "L1";
        model.Components.Add(new EditableComponent
        {
            InstanceName = "VAR1", Symbol = SymbolKind.Var, X = 0, Y = -600,
            Parameters = { new EditableParameter { Name = "L1", Expression = "5", Unit = "mm" } },
        });
        SchematicPersistence.SaveToFile(made.SchematicPath!, model, "Line");

        string ccell = Path.Combine(made.CellDir, CellFolder.CcellFileName);
        var cc = CellPersistence.LoadFromFile(ccell);
        cc.PCellDesign = new CcellPCellDesign();
        cc.LayoutGenerator = "cpcell:Line";
        cc.EmCopper = true;
        CellPersistence.SaveToFile(ccell, cc);
        return made.CellDir;
    }

    private static void AssertSameTree(string expectedRoot, string actualRoot)
    {
        string[] Rel(string root) =>
            [.. Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal)];
        Assert.Equal(Rel(expectedRoot), Rel(actualRoot));
        foreach (string rel in Rel(expectedRoot).Where(r => File.Exists(Path.Combine(expectedRoot, r))))
            Assert.True(File.ReadAllBytes(Path.Combine(expectedRoot, rel)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(actualRoot, rel))), rel);
    }

    private static string StripComments(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    private (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment[CircuitRF.Design.UserStateDirectory.EnvironmentVariable] = StateDir;
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(Pcd7PCellVerbTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Cli;
using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.Cli;

/// <summary>
/// <c>circuitrf import pdk</c> (brief-gdf-1 R-gdf1-4): File ▸ Import PDK with no window. It owns no
/// import logic — <see cref="PdkWorkspaceImport"/> does, and the view model calls the same function.
///
/// <para>The kit is synthetic: one record-format symbol, one subcircuit, and a parametric-cell package
/// written against the cell API, so the import has a declaration to write and generators to start.</para>
/// </summary>
[Collection(UserStateDirectoryCollection.Name)]
public sealed class ImportPdkCliVerbTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-importpdk-" + Guid.NewGuid().ToString("N")[..12]);

    private string StateDir => Path.Combine(_root, "state");

    public ImportPdkCliVerbTests()
    {
        Directory.CreateDirectory(StateDir);
        CircuitRF.Ui.AppDataRoot.RedirectTo(StateDir);
        PdkKitRegistry.ResetAllForTests();
    }

    public void Dispose()
    {
        ImportPdk.InterpreterFinder = null;
        PdkKitRegistry.ResetAllForTests();
        CircuitRF.Ui.AppDataRoot.RedirectTo(null);
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private string Kit()
    {
        string kit = Path.Combine(_root, "synthkit");
        void Write(string rel, string text)
        {
            string p = Path.Combine(kit, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }

        Write("libs.sym/widget.sym", """
            v {version=1.2}
            K {type=res
            template="name=X1 model=widget w=1u l=2u"}
            L 4 -20 -20 20 20 {}
            B 5 -2.5 -2.5 2.5 2.5 {name=a dir=inout}
            B 5 17.5 -2.5 22.5 2.5 {name=b dir=inout}
            """);
        Write("libs.spice/devices.lib", """
            .subckt widget a b w=1u l=2u
            R1 a b {w*l*1e12}
            .ends widget
            """);
        Write("libs.python/synth_cells/__init__.py", "");
        Write("libs.python/synth_cells/widget_code.py", "from cni.dlo import *\nclass widget(DloGen): pass\n");
        return kit;
    }

    private string Workspace(string name)
    {
        WorkspaceCreate.Create(_root, name, technologyId: null);
        return Path.Combine(_root, name);
    }

    // ── byte identity, and no second copy ────────────────────────────────────────────────────────

    /// <summary>The verb, run as a PROCESS, records the same <c>.cws</c> reference and writes the same
    /// cell declaration as the in-process function the GUI's import calls.</summary>
    [Fact]
    public void TheVerbWritesWhatTheGuisImportWrites()
    {
        string kit = Kit();
        string viaCli = Workspace("ws_cli"), inProcess = Workspace("ws_fn");

        var (exit, _, err) = RunCli("import", "pdk", kit, "--into", viaCli);
        Assert.True(exit == 0, err);
        PdkWorkspaceImport.Import(kit, Path.Combine(inProcess, WorkspacePersistence.FileName));

        string Refs(string ws) => JsonSerializer.Serialize(
            WorkspacePersistence.LoadFromFile(Path.Combine(ws, WorkspacePersistence.FileName)).PdkRefs);
        Assert.Equal(Refs(inProcess), Refs(viaCli));
        Assert.Contains("synthkit", Refs(viaCli), StringComparison.Ordinal);

        string folder = KitPCellLibrary.FolderNameFor("synthkit");
        foreach (string file in new[] { PCellGeneratorManifest.FileName, KitPCellLibrary.EntryScriptName })
            Assert.Equal(File.ReadAllBytes(Path.Combine(inProcess, folder, file)),
                         File.ReadAllBytes(Path.Combine(viaCli, folder, file)));
    }

    /// <summary>The view model calls the ONE import function and none of its steps (brief-gdf-6 R-gdf6-2):
    /// calling <c>Read</c>, <c>Install</c> and <c>Commit</c> itself is how the two sequences drifted apart.</summary>
    [Fact]
    public void TheViewModelKeptNoSecondCopy()
    {
        string code = StripComments(File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Ui", "ViewModels", "WorkspaceViewModel.cs")));

        Assert.Contains("PdkWorkspaceImport.Import(", code, StringComparison.Ordinal);
        Assert.Contains("PdkWorkspaceImport.ReadReferenced(", code, StringComparison.Ordinal);
        foreach (string gone in new[] { "PdkWorkspaceImport.Read(", "PdkWorkspaceImport.Install(",
                                        "PdkWorkspaceImport.Commit(", "FindSourceTree(",
                                        "PdkImporter.Import(", "PdkPartInstaller.Install(",
                                        "KitPCellLibrary.EnsureDeclared(", "new CwsPdkRef" })
            Assert.DoesNotContain(gone, code, StringComparison.Ordinal);
    }

    /// <summary>The GUI's command path, driven with no window, writes the same <c>.cws</c> and the same
    /// declaration as <c>circuitrf import pdk</c> run as a process — compared against what the GUI DOES, not
    /// against the function it is supposed to call.</summary>
    [Fact]
    public async System.Threading.Tasks.Task TheGuisCommandPathWritesWhatTheVerbWrites()
    {
        string kit = Kit();
        string viaCli = Workspace("ws_cli2"), viaGui = Workspace("ws_gui");

        var (exit, _, err) = RunCli("import", "pdk", kit, "--into", viaCli);
        Assert.True(exit == 0, err);

        var vm = new CircuitRF.Ui.ViewModels.WorkspaceViewModel
        {
            CurrentWorkspacePath = Path.Combine(viaGui, WorkspacePersistence.FileName),
        };
        var imported = await vm.ImportPdkPathAsync(kit);
        Assert.NotNull(imported?.Result.Outcome);

        Assert.Equal(RefsOf(viaCli), RefsOf(viaGui));
        string folder = KitPCellLibrary.FolderNameFor("synthkit");
        foreach (string file in new[] { PCellGeneratorManifest.FileName, KitPCellLibrary.EntryScriptName })
            Assert.Equal(File.ReadAllBytes(Path.Combine(viaCli, folder, file)),
                         File.ReadAllBytes(Path.Combine(viaGui, folder, file)));
    }

    // ── one folder, two kits; an archive (R-gdf6-2) ──────────────────────────────────────────────

    /// <summary>A kit tree that also carries a gdsfactory package imports BOTH (D2): the ordinary half is
    /// recorded — it used to be skipped with nothing said — and the gdsfactory half is attempted after it.
    /// With no interpreter chosen that half is refused by name, which is the routine tier's evidence that it
    /// was reached; tier B runs it.</summary>
    [Fact]
    public void AKitTreeHoldingAGdsfactoryPackage_ImportsBoth()
    {
        string kit = Kit();
        string pkg = Path.Combine(kit, "python", "synth_gf_kit");
        Directory.CreateDirectory(pkg);
        File.WriteAllText(Path.Combine(pkg, "__init__.py"),
            "import gdsfactory as gf\nfrom gdsfactory.pdk import Pdk\nPDK = Pdk(name='synth_gf_kit')\n");
        string ws = Workspace("ws_both");

        var (exit, err) = Run("import", "pdk", kit, "--into", ws);

        Assert.Contains("synthkit", RefsOf(ws) ?? "", StringComparison.Ordinal);
        Assert.Contains("holds two kits", err, StringComparison.Ordinal);
        Assert.Contains("synth_gf_kit", err, StringComparison.Ordinal);
        Assert.Equal(1, exit);   // the gdsfactory half: no interpreter is chosen for it on this machine
        Assert.Contains("No Python environment is chosen", err, StringComparison.Ordinal);
    }

    /// <summary>Tier B of the same: with an interpreter that has gdsfactory, the gdsfactory half runs too, and
    /// the workspace ends up holding both kits — the ordinary one referenced, the gdsfactory one declared.</summary>
    [CircuitRF.Ui.Tests.Gdsfactory.GdsfactoryFact]
    public void AKitTreeHoldingAGdsfactoryPackage_ImportsBoth_TierB()
    {
        // Named apart from the gdsfactory module, and without the cell package of its own, so the two kits'
        // declarations cannot be mistaken for each other.
        string kit = Path.Combine(_root, "twokit");
        Directory.Move(Kit(), kit);
        Directory.Delete(Path.Combine(kit, "libs.python"), recursive: true);
        CopyTree(FixturePaths.Require("tests/Ui.Tests/Gdsfactory/Fixtures/synthkit"), Path.Combine(kit, "python"));
        string ws = Workspace("ws_both_b");

        var (exit, err) = Run("import", "pdk", kit, "--into", ws, "--allow-code",
                              "--python", CircuitRF.Ui.Tests.Gdsfactory.GdsfactoryFactAttribute.Interpreter!);

        Assert.True(exit == 0, err);
        Assert.Contains("twokit", RefsOf(ws) ?? "", StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(ws, KitPCellLibrary.FolderNameFor("synthkit"), PCellGeneratorManifest.FileName)), err);
    }

    private static void CopyTree(string from, string to)
    {
        foreach (string dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            if (!file.Contains("__pycache__", StringComparison.Ordinal))
                File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }

    /// <summary>An archive of a kit imports from the command line through the code the GUI unpacks with,
    /// and records what the unpacked folder records — apart from where it lives.</summary>
    [Fact]
    public void AnArchiveImportsAsTheFolderItHolds()
    {
        string kit = Kit();
        string zip = Path.Combine(_root, "synthkit.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(kit, zip, System.IO.Compression.CompressionLevel.Fastest,
                                                          includeBaseDirectory: true);
        string fromZip = Workspace("ws_zip"), fromFolder = Workspace("ws_folder");

        var (exit, err) = Run("import", "pdk", zip, "--into", fromZip);
        Assert.True(exit == 0, err);
        Assert.Equal(0, Run("import", "pdk", kit, "--into", fromFolder).Exit);

        string unpacked = Path.Combine(fromZip, KitArchive.KitsFolderName, "synthkit", "synthkit");
        Assert.True(Directory.Exists(unpacked));
        static string WithoutPath(string? refs) => Regex.Replace(refs ?? "", "\"Path\":\"[^\"]*\"", "");
        Assert.Equal(WithoutPath(RefsOf(fromFolder)), WithoutPath(RefsOf(fromZip)));
        Assert.Contains("kits/synthkit", RefsOf(fromZip), StringComparison.Ordinal);
    }

    /// <summary>A name taken from the FOLDER is checked as a given one is (R-gdf6-5), and the refusal says
    /// how to give one.</summary>
    [Fact]
    public void AFolderNameThatWouldSplitItsPartReferences_IsRefused()
    {
        string bad = Path.Combine(_root, "syn|kit");
        Directory.Move(Kit(), bad);
        string ws = Workspace("ws_badfolder");

        var (exit, err) = Run("import", "pdk", bad, "--into", ws);

        Assert.Equal(1, exit);
        Assert.Contains("--name", err, StringComparison.Ordinal);
        Assert.Null(RefsOf(ws));
    }

    // ── what is never recorded ───────────────────────────────────────────────────────────────────

    private static string? RefsOf(string ws)
    {
        var refs = WorkspacePersistence.LoadFromFile(Path.Combine(ws, WorkspacePersistence.FileName)).PdkRefs;
        return refs is { Count: > 0 } ? JsonSerializer.Serialize(refs) : null;
    }

    /// <summary>A name holding a character that splits a part reference is refused before anything is
    /// written: <c>pdk://a/b/widget</c> reads back as kit 'a', and no placed part would resolve.</summary>
    [Fact]
    public void ANameThatWouldSplitItsPartReferences_IsRefused()
    {
        string ws = Workspace("ws_badname");

        var (exit, err) = Run("import", "pdk", Kit(), "--into", ws, "--name", "a/b");

        Assert.Equal(1, exit);
        Assert.Contains("--name", err, StringComparison.Ordinal);
        Assert.Null(RefsOf(ws));
    }

    /// <summary>A folder in which circuitRF recognises nothing usable is refused and not recorded — it
    /// used to be referenced from the workspace and re-read on every open, with exit 0.</summary>
    [Fact]
    public void AFolderHoldingNoKit_IsRefusedAndNotRecorded()
    {
        string notKit = Path.Combine(_root, "notakit");
        Directory.CreateDirectory(notKit);
        File.WriteAllBytes(Path.Combine(notKit, "blob.bin"), [0, 1, 2, 3, 0, 255]);
        string ws = Workspace("ws_notkit");

        var (exit, err) = Run("import", "pdk", notKit, "--into", ws);

        Assert.True(exit == 1, err);
        Assert.Contains("recognised nothing it can use", err, StringComparison.Ordinal);
        Assert.Null(RefsOf(ws));
    }

    // ── consent ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Without consent no interpreter is even looked for, so no kit code can run; the import
    /// still succeeds and says why. <c>--allow-code</c> starts the kit's generators for this one run.</summary>
    [Fact]
    public void WithoutConsentNoInterpreterIsStarted()
    {
        string kit = Kit();
        int asked = 0;
        ImportPdk.InterpreterFinder = (_, _) => { asked++; return null; };

        var (exit, err) = Run("import", "pdk", kit, "--into", Workspace("ws_no"));
        Assert.Equal(0, exit);
        Assert.Equal(0, asked);
        Assert.Contains("were not started", err, StringComparison.Ordinal);

        (exit, _) = Run("import", "pdk", kit, "--into", Workspace("ws_yes"), "--allow-code");
        Assert.Equal(0, exit);
        Assert.Equal(1, asked);
    }

    // ── a placed kit part, headless ──────────────────────────────────────────────────────────────

    /// <summary>A schematic holding a part placed from an imported kit extracts with no window: the
    /// workspace's kits are loaded for the run, as a workspace open loads them.</summary>
    [Fact]
    public void APlacedKitPartExtractsHeadlessly()
    {
        string ws = Workspace("ws_run");
        Assert.Equal(0, Run("import", "pdk", Kit(), "--into", ws).Exit);
        PdkKitRegistry.ResetAllForTests();   // nothing in memory: what a fresh process has

        string csch = Path.Combine(ws, "bench.csch");
        var model = new SchematicEditModel { SchematicDirectory = ws };
        var comp = new EditableComponent
        {
            InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = PdkKitRegistry.RefFor("synthkit", "widget"),
        };
        comp.Parameters.Add(new EditableParameter { Name = "w", Expression = "3E-06" });
        model.Components.Add(comp);
        SchematicPersistence.SaveToFile(csch, model, "bench");

        string cnl = Path.Combine(ws, "bench.cnl");
        var (exit, err) = Run("netlist", csch, "-o", cnl);
        Assert.True(exit == 0, err);
        // Resolved through the kit — without the load it is an unresolved reference and no instance.
        Assert.Matches(@"(?m)^ExtDevice:X1\s.*Provider=synthkit\s.*w=3E-06", File.ReadAllText(cnl));
    }

    /// <summary>
    /// <c>opt</c>, <c>yield</c> and <c>fields</c> read a schematic as a prepared circuit, and what its
    /// extraction could not build is said there too — it used to reach only <c>netlist</c> and the run
    /// verbs, so a skipped or half-wired kit part vanished from an optimisation without a word.
    /// </summary>
    [Fact]
    public void APreparedSchematicSaysWhatItsExtractionCouldNotBuild()
    {
        string kit = Path.Combine(_root, "bodykit");
        Directory.CreateDirectory(Path.Combine(kit, "libs.sym"));
        Directory.CreateDirectory(Path.Combine(kit, "libs.spice"));
        File.WriteAllText(Path.Combine(kit, "libs.sym", "widget.sym"), """
            v {version=1.2}
            K {type=res
            format="@name @pinlist @body widget w=@w"
            template="name=X1 body=sub! w=1u"}
            B 5 -2.5 -2.5 2.5 2.5 {name=a dir=inout}
            B 5 17.5 -2.5 22.5 2.5 {name=b dir=inout}
            """);
        File.WriteAllText(Path.Combine(kit, "libs.spice", "devices.lib"), """
            .subckt widget a b bn w=1u
            R1 a b {w*1e6}
            R2 b bn 1e9
            .ends widget
            """);
        string ws = Workspace("ws_prepared");
        Assert.Equal(0, Run("import", "pdk", kit, "--into", ws).Exit);
        PdkKitRegistry.ResetAllForTests();

        string csch = Path.Combine(ws, "bench.csch");
        var model = new SchematicEditModel { SchematicDirectory = ws };
        model.Components.Add(new EditableComponent
        {
            InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = PdkKitRegistry.RefFor("bodykit", "widget"),
        });
        SchematicPersistence.SaveToFile(csch, model, "bench");

        var (realErr, err) = (Console.Error, new StringWriter());
        try
        {
            Console.SetError(err);
            JsonRun.Reset();
            CircuitSource.PrepareSchematic(csch);
        }
        finally { Console.SetError(realErr); }

        Assert.Matches(@"(?m)^warning: .*X1.*'body'", err.ToString());
    }

    /// <summary>A per-module <c>--python</c> for a module no kit in the run uses is a warning, never a
    /// refusal (R-gdf6-4): a script passing it for a kit it only sometimes uses must not fail.</summary>
    [Fact]
    public void APythonFlagForAModuleNoKitUses_IsAWarning()
    {
        var (exit, err) = Run("find", Workspace("ws_python"), "--python", "unused_mod=/envs/x/bin/python");

        Assert.Equal(0, exit);
        Assert.Contains("--python unused_mod=/envs/x/bin/python was not used", err, StringComparison.Ordinal);
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────────

    private static (int Exit, string Err) Run(params string[] args)
    {
        var (realOut, realErr) = (Console.Out, Console.Error);
        var err = new StringWriter();
        try
        {
            Console.SetOut(new StringWriter());
            Console.SetError(err);
            JsonRun.Reset();
            return (CliEntry.Run(args), err.ToString());
        }
        finally { Console.SetOut(realOut); Console.SetError(realErr); }
    }

    private (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
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

    private static string StripComments(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(ImportPdkCliVerbTests).Assembly)
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

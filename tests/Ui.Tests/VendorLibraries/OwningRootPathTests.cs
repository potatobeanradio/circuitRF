using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Core.Elaboration;
using CircuitRF.Design.Workspace;
using Xunit;

namespace CircuitRF.Ui.Tests.VendorLibraries;

/// <summary>
/// Brief vendorlib-8 §4: a relative file reference resolves where its DOCUMENT lives — a cell from a
/// referenced workspace in that workspace, a model folder's schematic in the model folder — in the editor
/// and in the run alike.
/// </summary>
[Collection(CellStatGlobalsCollection.Name)]
public sealed class OwningRootPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-vl8-" + Guid.NewGuid().ToString("N")[..12]);

    public OwningRootPathTests()
    {
        Directory.CreateDirectory(_root);
        WorkspaceRootFinder.InvalidateCache();
        CellSymbolResolver.InvalidateAll();
    }

    public void Dispose()
    {
        WorkspaceRootFinder.InvalidateCache();
        CellSymbolResolver.InvalidateAll();
        try { Directory.Delete(_root, true); } catch { }
    }

    // ── 1. the referenced-workspace cell ──────────────────────────────────────

    [Fact]
    public void AReferencedWorkspacesCell_ReadsItsOwnTouchstone_AndADecoyInTheConsumerChangesNothing()
    {
        string wsA = Path.Combine(_root, "A"), wsB = Path.Combine(_root, "B");
        Directory.CreateDirectory(wsA);
        Directory.CreateDirectory(wsB);
        WorkspacePersistence.SaveToFile(Path.Combine(wsA, ".cws"), new CwsFile());
        WorkspacePersistence.SaveToFile(Path.Combine(wsB, ".cws"), new CwsFile
        {
            ReferencedWorkspaces = [new CwsWorkspaceRef { Alias = "A", Path = Path.GetRelativePath(wsB, Path.Combine(wsA, ".cws")) }],
        });
        WorkspaceRootFinder.InvalidateCache();

        string own = WriteTouchstone(Path.Combine(wsA, "data", "line.s2p"), thru: true);
        WriteSnpCell(wsA, "Line", "data/line.s2p");

        string csch = WriteTop(wsB, "Board", ExternalCellRef.RefFor("A", "Line"));

        Assert.Equal(own, ElaboratedSnpFile(csch));

        WriteTouchstone(Path.Combine(wsB, "data", "line.s2p"), thru: false);
        Assert.Equal(own, ElaboratedSnpFile(csch));
    }

    // ── 2 + 3. a model folder inside a workspace tree ─────────────────────────

    [Fact]
    public void AModelFolderInsideAWorkspace_OwnsItsReferences_InTheEditorAndTheRunAlike()
    {
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile());
        string model = WriteSnpCell(Path.Combine(ws, "lib"), "Part1", "models/x.s2p");
        File.WriteAllText(Path.Combine(model, "part.cpart"), "{}");
        string modelSchematic = CellFolder.SubFolderPath(model, ViewType.Schematic);
        string x = WriteTouchstone(Path.Combine(model, "models", "x.s2p"), thru: true);
        WriteTouchstone(Path.Combine(ws, "models", "x.s2p"), thru: false);   // the workspace-root reading's file
        WorkspaceRootFinder.InvalidateCache();

        var found = OwningRoot.Find(modelSchematic);
        Assert.Equal(OwningRootKind.ModelFolder, found!.Kind);
        Assert.Equal(model, found.Root);
        Assert.Equal(OwningRootKind.Workspace, OwningRoot.Find(Path.Combine(ws, "lib"))!.Kind);

        // The editor reads it in the model folder…
        Assert.Equal(x, SnpPathPolicy.Resolve("models/x.s2p", ws, modelSchematic));
        Assert.Equal("models/x.s2p", SnpPathPolicy.ToStored(x, ws, modelSchematic));

        // …and so does a run of a design in that workspace placing it.
        string csch = WriteTop(ws, "Board", "../../lib/Part1");
        Assert.Equal(x, ElaboratedSnpFile(csch));

        // The model's own schematic, run as itself, resolves against the model folder too…
        string own = Path.Combine(modelSchematic, "Part1.csch");
        Assert.Equal(model, SchematicCircuit.ReferenceBaseOf(own));

        // …and explain --ref says so, naming the walk (R-vl8-3).
        var (exit, stdout, stderr) = RunCli("explain", own, "--ref", "models/x.s2p", "--json");
        Assert.True(exit == 0, stderr);
        var reference = System.Text.Json.Nodes.JsonNode.Parse(stdout)!["result"]!["explain"]!["reference"]!;
        Assert.Equal(x, (string?)reference["resolvedPath"]);
        Assert.Equal(model, (string?)reference["owningRoot"]);
        Assert.Equal("model-folder", (string?)reference["owningRootWalk"]);
    }

    private static (int Exit, string StdOut, string StdErr) RunCli(params string[] args)
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(OwningRootPathTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    // ── 4. the predicate covers every resolution site ─────────────────────────

    /// <summary>
    /// Every call of <c>ResolveSnpFilePath</c> / <c>ResolveFileReference</c> in the elaborator is in a method whose
    /// parameter <see cref="FileReferences"/> names, and so is every caller elsewhere. A new site fails here until the
    /// predicate (and this table) learns its parameter — which is what the extractor's rebasing reads.
    /// </summary>
    [Fact]
    public void TheFileReferencePredicate_CoversEveryResolutionSite()
    {
        var bySite = new Dictionary<string, (string Reference, string Param)[]>
        {
            ["ResolveExtDeviceParameters"] = [("VerilogA", "File")],
            ["ResolveSnpParameters"]       = [("SnP", "File"), ("EmFieldData", "File")],
            ["BindEmFieldData"]            = [("EmFieldData", "File")],
            ["ComposedFetCell"]            = [("ComposedFET", "File")],
            ["ResolveComposedParameters"]  = [("ComposedCore", "File")],
            ["ResolveWBondParameters"]     = [("wBond", "File"), ("wBond", "MaterialLibrary")],
            ["ResolveModSourceParameters"] = [("MOD_Source", "Carrier[1]")],
            ["TunableCatalog"]             = [("SnP", "File")],
        };
        foreach (var (site, entries) in bySite)
            foreach (var (reference, param) in entries)
                Assert.True(FileReferences.IsFileValued(reference, param), $"{site}: {reference}.{param} is not in FileReferences");

        var found = new HashSet<string>();
        string src = Path.Combine(RepoRoot(), "src");
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            // The predicate itself, and explain --ref, which reports the rule rather than resolving a parameter.
            if (name is "FileReferences" or "Explain") continue;
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string code = StripComment(lines[i]);
                // A call, or the method group handed to SignalSpec.MapFilePath.
                if (!Regex.IsMatch(code, @"\b(ResolveSnpFilePath|ResolveFileReference)\b")) continue;
                if (Regex.IsMatch(code, @"\b(private|public)\b.*\b(ResolveSnpFilePath|ResolveFileReference)\(string")) continue;   // the definitions
                string site = name == "Elaborator" ? EnclosingMethod(lines, i) : name;
                found.Add(site);
                Assert.True(bySite.ContainsKey(site),
                    $"{Path.GetRelativePath(src, file)}:{i + 1} resolves a file reference in '{site}'; add its parameter to " +
                    "FileReferences and to this table.");
            }
        }
        Assert.Equal(bySite.Keys.Order(), found.Order());
    }

    // ── fixture ───────────────────────────────────────────────────────────────

    /// <summary>A two-port cell holding one SnP whose File is <paramref name="file"/>, stored as written.</summary>
    private static string WriteSnpCell(string parent, string name, string file)
    {
        Directory.CreateDirectory(parent);
        string cell = CellFolder.CreateCellFolder(parent, name);
        SymbolPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(cell, ViewType.Symbol), name + ".csym"), TwoPinSymbol());

        var model = new SchematicEditModel();
        model.Components.Add(Pin("P1", 1, 0, 0));
        model.Components.Add(Pin("P2", 2, 0, 800));
        var snp = new EditableComponent { InstanceName = "S1", Symbol = SymbolKind.Snp, X = 400, Y = 400 };
        snp.Parameters.Add(new EditableParameter { Name = "File", Expression = file });
        snp.Parameters.Add(new EditableParameter { Name = "NumPorts", Expression = "2" });
        model.Components.Add(snp);
        string schDir = CellFolder.SubFolderPath(cell, ViewType.Schematic);
        Directory.CreateDirectory(schDir);
        SchematicPersistence.SaveToFile(Path.Combine(schDir, name + ".csch"), model);
        return cell;
    }

    private static string WriteTop(string ws, string name, string cellRef)
    {
        string cell = CellFolder.CreateCellFolder(ws, name);
        var model = new SchematicEditModel();
        model.Components.Add(new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = cellRef });
        string schDir = CellFolder.SubFolderPath(cell, ViewType.Schematic);
        Directory.CreateDirectory(schDir);
        string csch = Path.Combine(schDir, name + ".csch");
        SchematicPersistence.SaveToFile(csch, model);
        WorkspaceRootFinder.InvalidateCache();
        return csch;
    }

    /// <summary>The File the elaborated SnP holds, through the run's own path: extract, round-trip the .cnl text,
    /// elaborate against the run's base.</summary>
    private static string ElaboratedSnpFile(string csch)
    {
        var (lib, tb) = SchematicCircuit.FromSchematic(csch);
        using var nl = new Elaborator(lib) { BaseDirectory = SchematicCircuit.ReferenceBaseOf(csch) }.Elaborate(tb);
        return nl.Components.Single(c => c.InstancePath.EndsWith("S1", StringComparison.Ordinal)).Parameters["File"].AsString();
    }

    private static string WriteTouchstone(string path, bool thru)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string row = thru ? "0 0 1 0 1 0 0 0" : "1 0 0 0 0 0 1 0";
        File.WriteAllText(path, $"# GHz S RI R 50\n1 {row}\n2 {row}\n");
        return Path.GetFullPath(path);
    }

    private static EditableComponent Pin(string name, int num, double x, double y)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Pin, X = x, Y = y };
        c.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString() });
        return c;
    }

    private static Symbol TwoPinSymbol() => new(
        primitives: [new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, -100, 0, 100, 0)],
        pins:       [new SymbolPin(0, -200, 1, "1"), new SymbolPin(0, 200, 2, "2")],
        portCount:  2);

    private static string StripComment(string line)
    {
        int c = line.IndexOf("//", StringComparison.Ordinal);
        return c < 0 ? line : line[..c];
    }

    /// <summary>The member a line sits in: the nearest earlier declaration at member indentation.</summary>
    private static string EnclosingMethod(string[] lines, int index)
    {
        var decl = new Regex(@"^    (?:private|public|internal|protected)\b[^=;]*?\b(\w+)\s*\(");
        for (int i = index; i >= 0; i--)
            if (decl.Match(lines[i]) is { Success: true } m) return m.Groups[1].Value;
        return "?";
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

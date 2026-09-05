using System.Diagnostics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  MW2 §8 / §9.7 — a `.cnl` and a headless run behave identically to the GUI.
//
//  THE FINDING THIS TEST RECORDS, because it corrects the brief's own premise:
//  `Cli elab` reads a `.cnl`, and a `.cnl` carries every cell as an inline
//  `define … end` block (CnlWriter.Write(tb, library)). Extraction is where a cell
//  reference — external or not — is RESOLVED and absorbed; by the time anything is
//  written for the CLI to read, there is no cell reference left in the file and no
//  workspace for the CLI to walk up to. So R-mw2-18's "a kit-bearing external cell
//  must resolve from its own workspace's .cws on disk, or refuse" is satisfied
//  structurally rather than by new code: either extraction resolved the kit (and the
//  definition is in the .cnl) or it did not (and extraction reported the conflict,
//  in the GUI and headlessly alike). The gate below is that the two agree.
// ──────────────────────────────────────────────────────────────────────────────

[Collection(CellStatGlobalsCollection.Name)]
public sealed class ExternalCellRefCliTests : IDisposable
{
    private readonly string _root;
    private readonly string _wsA;
    private readonly string _wsB;

    public ExternalCellRefCliTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf_mw2cli_" + Guid.NewGuid().ToString("N")[..8]);
        _wsA  = Path.Combine(_root, "workspaceA");
        _wsB  = Path.Combine(_root, "workspaceB");
        Directory.CreateDirectory(_wsA);
        Directory.CreateDirectory(_wsB);
        CellSymbolResolver.InvalidateAll();
    }

    public void Dispose()
    {
        CellSymbolResolver.InvalidateAll();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// The resolver the GUI uses, in the form a test can construct: <c>WorkspaceViewModel.Resolve</c>
    /// is exactly <c>HierarchyResolver.ResolvePrimaryPath</c> plus a load of that schematic and the
    /// cell's <c>.ccell</c> interface. Reproduced here rather than stubbed on a dictionary, because
    /// the whole question is whether the DISK-backed resolution crosses the workspace boundary — a
    /// stub keyed on the reference string would answer yes without ever asking.
    /// </summary>
    private sealed class DiskCellResolver : ICellResolver
    {
        public CellResolution? Resolve(EditableComponent comp, SchematicEditModel containing)
        {
            if (HierarchyResolver.ResolvePrimaryPath(comp, containing) is not { } primaryPath) return null;

            var (model, _, _) = SchematicPersistence.LoadFromFile(primaryPath);
            model.SchematicDirectory = Path.GetDirectoryName(primaryPath);

            string cellDir  = Path.GetDirectoryName(Path.GetDirectoryName(primaryPath))!;
            string cellName = Path.GetFileName(cellDir);

            IReadOnlyList<ParameterDeclaration> parameters = [];
            string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
            if (File.Exists(ccellPath))
                parameters = [.. CellPersistence.LoadFromFile(ccellPath).Parameters.Select(p =>
                    new ParameterDeclaration(p.Name, p.DefaultExpression,
                        string.IsNullOrEmpty(p.Unit) ? null : p.Unit, hidden: !p.ShowOnSchematic))];

            return new CellResolution(cellName, model, parameters);
        }
    }

    [Fact]
    public void CliElab_OnANetlistExtractedThroughAnExternalReference_MatchesTheGui()
    {
        BuildTwoWorkspaces();

        // 1. Extract exactly as the GUI does — the same NetExtractor, the same disk-backed resolver.
        string schDir = Path.Combine(_wsB, "Board", "schematic");
        var (top, _, _) = SchematicPersistence.LoadFromFile(Path.Combine(schDir, "Board.csch"));
        top.SchematicDirectory = schDir;

        var extracted = NetExtractor.Extract(top, "tb", new DiskCellResolver());

        Assert.Empty(extracted.Conflicts);
        Assert.Contains(extracted.Library.Cells, c => c.Name == "SubBlock");   // the EXTERNAL cell's definition

        // 2. The GUI's own elaborated netlist.
        var expected = new Elaborator(extracted.Library).Elaborate(extracted.TestBench);

        // 3. The same design, through the file the CLI actually reads.
        string cnl = Path.Combine(_root, "board.cnl");
        File.WriteAllText(cnl, CnlWriter.Write(extracted.TestBench, extracted.Library));

        var (exit, stdout, stderr) = RunCli("elab", cnl);

        Assert.True(exit == 0, $"elab exited {exit}: {stderr}");
        Assert.Contains($"{expected.Components.Count} component(s)", stdout);
        foreach (var comp in expected.Components)
            Assert.Contains(comp.InstancePath, stdout);

        // The external cell's own content is what has to have travelled: the resistor inside Amp,
        // reached only through ws://A/SubBlock.
        Assert.Contains(expected.Components, c => c.InstancePath.Contains("X1", StringComparison.Ordinal));
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    private void BuildTwoWorkspaces()
    {
        WorkspacePersistence.SaveToFile(Path.Combine(_wsA, ".cws"), new CwsFile());
        WorkspacePersistence.SaveToFile(Path.Combine(_wsB, ".cws"), new CwsFile
        {
            ReferencedWorkspaces =
                [new CwsWorkspaceRef { Alias = "A", Path = Path.GetRelativePath(_wsB, Path.Combine(_wsA, ".cws")) }],
        });
        WorkspaceRootFinder.InvalidateCache();

        // Workspace A: a two-port cell with a resistor across it.
        string amp = CellFolder.CreateCellFolder(_wsA, "SubBlock");
        SymbolPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(amp, ViewType.Symbol), "SubBlock.csym"), TwoPinSymbol());

        var inner = new SchematicEditModel();
        inner.Components.Add(Pin("P1", 1, 0, 0));
        inner.Components.Add(Pin("P2", 2, 0, 800));
        inner.Components.Add(new EditableComponent
        { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 0, Y = 400 });
        inner.Components[2].Parameters.Add(new EditableParameter { Name = "R", Expression = "50" });
        SchematicPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(amp, ViewType.Schematic), "SubBlock.csch"), inner);

        // Workspace B: a board that instances it BY REFERENCE.
        string board = CellFolder.CreateCellFolder(_wsB, "Board");
        var topModel = new SchematicEditModel();
        topModel.Components.Add(new EditableComponent
        {
            InstanceName = "X1",
            Symbol       = SymbolKind.Generic,
            CellRef      = ExternalCellRef.RefFor("A", "SubBlock"),
            X = 0, Y = 0,
        });
        SchematicPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(board, ViewType.Schematic), "Board.csch"), topModel);
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

    // ── CLI plumbing — the pattern EmCliVerbTests established ─────────────────

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
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
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(ExternalCellRefCliTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }
}

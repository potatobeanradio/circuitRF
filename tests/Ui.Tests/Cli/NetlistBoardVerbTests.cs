// ================================================================
//  NetlistBoardVerbTests.cs — brief-authored-board-3-companion-writers.md §5, gates 5, 6, 8 and 10.
//
//  ── WHY BYTE IDENTITY, AND NOT "BOTH LOOK RIGHT" ──────────────────────────────────────────────
//
//  The layout editor's File ▸ Export rows and `circuitrf netlist` write the same three tables, and
//  the two DRIFTING IS INVISIBLE: a board exported from the window and one exported on a build
//  machine both look like board netlists, and nothing on either says they disagree. So the gate is
//  ConvertCliVerbTests' own — the verb run as a PROCESS against the in-process call, compared byte
//  for byte, over the shipped example rather than a fixture, because the shipped example is the
//  board whose answers must not move.
// ================================================================

using System.Diagnostics;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Theming;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Tests.Examples;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Cli;

public sealed class NetlistBoardVerbTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _out = Path.Combine(
        Path.GetTempPath(), "crf-ab3cli-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_out, true); } catch { /* best effort */ } }

    private static string ExampleClay() => Path.Combine(
        PowerRailFootprintCells.ExampleRoot(), "Sensor board", "layout", "Board.clay");

    // ══ 5. The CLI as a process against the in-process call ═══════════════════════════════════

    /// <summary>
    /// <b>Gate 5, R-ab3-3b.</b> All three tables, byte for byte, against what File ▸ Export writes.
    /// </summary>
    [Fact]
    public void TheVerbWritesExactlyWhatTheInProcessCallWrites()
    {
        Directory.CreateDirectory(_out);
        string clay = ExampleClay();

        string ipc = Path.Combine(_out, "cli.ipc");
        string place = Path.Combine(_out, "cli.placement.csv");
        string bom = Path.Combine(_out, "cli.bom.csv");

        var run = RunCli("netlist", clay, "--ipc", ipc, "--placement", place, "--bom", bom);
        Assert.Equal(0, run.ExitCode);

        // The in-process call, which is the one the layout editor's export rows make.
        var view = LayoutPersistence.LoadFromFile(clay);
        var (tech, _) = TechnologyResolver.ResolveForDocument(
            view.TechRef, clay, null, new TechnologyCache());
        var projection = BoardCompanions.Project(view, clay, tech.Tech);
        Assert.Null(projection.Refusal);

        Assert.Equal(BoardCompanions.BoardNetlistTextOf(projection), File.ReadAllText(ipc));
        Assert.Equal(BoardCompanions.PlacementTextOf(projection), File.ReadAllText(place));
        Assert.Equal(BoardCompanions.BomTextOf(projection), File.ReadAllText(bom));

        output.WriteLine($"{new FileInfo(ipc).Length:N0} + {new FileInfo(place).Length:N0} + " +
                         $"{new FileInfo(bom).Length:N0} bytes, identical both ways");
    }

    // ══ 6. -o alone on a board, and a schematic unchanged ═════════════════════════════════════

    /// <summary>
    /// <b>Gate 6, R-ab3-2b.</b> Two of the three tables are <c>.csv</c>, so the extension cannot
    /// say which — and "which table" is exactly what a dialog would ask. The refusal names all
    /// three flags, which is the whole point of it.
    /// </summary>
    [Fact]
    public void OutputAloneOnABoardIsARefusalNamingAllThreeFlags()
    {
        Directory.CreateDirectory(_out);
        string wrote = Path.Combine(_out, "nothing.cnl");

        var run = RunCli("netlist", ExampleClay(), "-o", wrote);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("--ipc", run.StdErr, StringComparison.Ordinal);
        Assert.Contains("--placement", run.StdErr, StringComparison.Ordinal);
        Assert.Contains("--bom", run.StdErr, StringComparison.Ordinal);
        Assert.False(File.Exists(wrote));

        output.WriteLine(run.StdErr.Trim());
    }

    /// <summary>
    /// <b>Gate 6's other half.</b> A <c>.csch</c> behaves exactly as it does at HEAD — nothing
    /// about the existing path moves (R-ab3-2a), and the board flags on one are a refusal BY KIND
    /// rather than a table projected out of a schematic.
    /// </summary>
    [Fact]
    public void ASchematicStillExtractsAndRefusesTheBoardFlags()
    {
        Directory.CreateDirectory(_out);
        string csch = Schematic();

        var extract = RunCli("netlist", csch, "-o", Path.Combine(_out, "out.cnl"));
        Assert.Equal(0, extract.ExitCode);
        Assert.Contains("Port:", File.ReadAllText(Path.Combine(_out, "out.cnl")), StringComparison.Ordinal);

        string never = Path.Combine(_out, "never.ipc");
        var refused = RunCli("netlist", csch, "--ipc", never);
        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("--ipc", refused.StdErr, StringComparison.Ordinal);
        Assert.False(File.Exists(never));

        output.WriteLine(refused.StdErr.Trim());
    }

    // ══ 8. Nothing is written on a refusal ════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 8, R-ab3-2e.</b> Over the flatten ceiling the pads were never read, so a netlist
    /// over that board would be a confident statement about geometry the run never saw. Nothing is
    /// written — asserted by the files' absence, because a half-written <c>.ipc</c> reads as a
    /// complete statement about a board and is one about part of it.
    /// </summary>
    [Fact]
    public void OverTheFlattenCeilingNothingIsWritten()
    {
        Directory.CreateDirectory(_out);
        string clay = OverTheCeilingBoard();

        string ipc = Path.Combine(_out, "never.ipc");
        string place = Path.Combine(_out, "never.csv");
        var run = RunCli("netlist", clay, "--ipc", ipc, "--placement", place);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("ceiling", run.StdErr, StringComparison.Ordinal);
        Assert.False(File.Exists(ipc));
        Assert.False(File.Exists(place));

        output.WriteLine(run.StdErr.Trim());
    }

    // ══ 10. The shipped example's committed companions ════════════════════════════════════════

    /// <summary>
    /// <b>Gate 10, R-ab3-4b.</b> The two files committed beside the Power Rail example are what
    /// this verb projects from its <c>.clay</c> — which is what makes the README's one command the
    /// truth rather than a description. <c>Board.gen.py</c> writes neither any more;
    /// <c>PowerRailExampleTests</c> holds that, and the example's own answers.
    /// </summary>
    [Fact]
    public void TheCommittedCompanionsAreWhatTheVerbProjects()
    {
        Directory.CreateDirectory(_out);
        string root = PowerRailFootprintCells.ExampleRoot();
        string ipc = Path.Combine(_out, "Board.ipc");
        string place = Path.Combine(_out, "Board.placement.csv");

        var run = RunCli("netlist", ExampleClay(), "--ipc", ipc, "--placement", place);
        Assert.Equal(0, run.ExitCode);

        Assert.Equal(
            File.ReadAllText(Path.Combine(root, "Sensor board", "layout", "Board.ipc")),
            File.ReadAllText(ipc));
        Assert.Equal(
            File.ReadAllText(Path.Combine(root, "Sensor board", "layout", "Board.placement.csv")),
            File.ReadAllText(place));
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A board whose one placement is a 1,000 × 1,000 array — past the flatten's own
    /// ceiling, which is the predicate <c>PlacedPins</c> already refuses on.</summary>
    private string OverTheCeilingBoard()
    {
        const int dbu = LayoutUnits.DefaultDbuPerMicron;
        var top = new LayerKey(1, 0);
        long mm = 1000L * dbu;

        string root = Path.Combine(_out, "ws");
        Directory.CreateDirectory(root);

        var tech = new Technology { Name = "Board" };
        tech.Layers = [new LayerDef { Key = top, Name = "TOP", ZOrder = 0, Color = new Rgba(200, 80, 40, 255) }];
        tech.Stackup.Layers =
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "TOP",
                ThicknessDbu = 35L * dbu, SigmaSm = 5.8e7, DrawingLayers = [top],
            },
        ];
        Directory.CreateDirectory(Path.Combine(root, "tech"));
        TechPersistence.SaveToFile(Path.Combine(root, "tech", "Board.ctech"), tech);
        WorkspacePersistence.SaveToFile(
            Path.Combine(root, ".cws"), new CwsFile { DefaultTechRef = Path.Combine("tech", "Board.ctech") });

        string landDir = CellFolder.CreateCellFolder(root, "Land");
        string landLayout = CellFolder.SubFolderPath(landDir, ViewType.Layout);
        Directory.CreateDirectory(landLayout);
        var land = new LayoutView { DbuPerMicron = dbu };
        land.Pins.Add(new LayoutPin { Name = "1", X = 0, Y = 0, WidthDbu = 250L * dbu, Layer = top });
        land.Shapes.Add(new RectShape { Layer = top, Pin = "1", X1 = -100L * dbu, Y1 = -100L * dbu, X2 = 100L * dbu, Y2 = 100L * dbu });
        LayoutPersistence.SaveToFile(Path.Combine(landLayout, "Land.clay"), land);

        string boardDir = CellFolder.CreateCellFolder(root, "Board");
        string boardLayout = CellFolder.SubFolderPath(boardDir, ViewType.Layout);
        Directory.CreateDirectory(boardLayout);
        var view = new LayoutView { DbuPerMicron = dbu, TechRef = Path.Combine("..", "..", "tech", "Board.ctech") };
        view.Instances.Add(new LayoutInstance
        {
            CellRef = Path.Combine("..", "..", "Land"),
            X = 0, Y = 0, Mag = 1.0, RefDes = "F1",
            Rows = 1_000, Cols = 1_000, PitchX = mm, PitchY = mm,
        });
        string clay = Path.Combine(boardLayout, "Board.clay");
        LayoutPersistence.SaveToFile(clay, view);
        return clay;
    }

    /// <summary>The smallest schematic that extracts — gate 6's "exactly as at HEAD".</summary>
    private string Schematic()
    {
        string root = Path.Combine(_out, "sch");
        Directory.CreateDirectory(root);
        WorkspacePersistence.SaveToFile(Path.Combine(root, ".cws"), new CwsFile());

        string cellDir = CellFolder.CreateCellFolder(root, "Pad");
        string schDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        Directory.CreateDirectory(schDir);

        var model = new CircuitRF.Design.Schematic.SchematicEditModel();
        model.Components.Add(new CircuitRF.Design.Schematic.EditableComponent
        {
            InstanceName = "P1", Symbol = CircuitRF.Design.Schematic.SymbolKind.Term, X = 0, Y = 0,
        });
        model.Components.Add(new CircuitRF.Design.Schematic.EditableComponent
        {
            InstanceName = "R1", Symbol = CircuitRF.Design.Schematic.SymbolKind.Resistor, X = 200, Y = 0,
        });

        string csch = Path.Combine(schDir, "Pad.csch");
        CircuitRF.Design.Schematic.SchematicPersistence.SaveToFile(csch, model);
        return csch;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
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
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(
                typeof(NetlistBoardVerbTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }
}

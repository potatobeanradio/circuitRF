// ================================================================
//  TurnedPartsTests.cs
//
//  brief-lvs-16-parts-placed-end-for-end.md — a two-pin part placed end for end, read by railRF's
//  own reader, reported once per part, and turned from either window. One test per claim (§6).
//
//  Every board but the shipped Attenuator is built here, in a temp folder: N parts standing across
//  a supply strip and a return strip, some of them placed turned. A part whose pin 1 is the
//  schematic's supply side stands at R90; turned, at R270 — the same two lands, the other way round.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.RailRf;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Theming;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Tests.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.Lvs;

public sealed class TurnedPartsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-lvs16-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bot = new(2, 0);
    private static long Mm(double v) => (long)Math.Round(v * 1e3 * Dbu);

    // ══ 1 — gate 1: §0's measurement, inverted ═══════════════════════════════════════════════════

    /// <summary>
    /// <b>The shipped Attenuator with all three resistors turned: no error, and exactly three
    /// warnings naming them.</b> Before this brief it was nine findings — a contradicted anchor and
    /// two unmatched devices per part — and not one of them said the word "turned".
    /// </summary>
    [Fact]
    public void ThreeTurnedResistorsAreThreeWarningsAndNoError()
    {
        string cell = Path.Combine(RepoRoot(), "examples", "LVS", "Attenuator");
        string clay = Path.Combine(cell, "layout", "Attenuator.clay");
        string csch = Path.Combine(cell, "schematic", "Attenuator.csch");

        var view = LayoutPersistence.LoadFromFile(clay);
        var (resolution, _) = TechnologyResolver.ResolveForDocument(view.TechRef, clay, null, new TechnologyCache());
        foreach (string r in new[] { "R1", "R2", "R3" })
        {
            int i = view.Instances.FindIndex(inst => inst.SchematicId == r);
            view.Instances[i] = TurnedParts.HalfTurn(
                view.Instances[i], TurnedParts.LandingHalfTurn(view, clay, resolution.Tech, i)!);
        }

        var (model, _, _) = SchematicPersistence.LoadFromFile(csch);
        var result = LvsRun.Run(view, clay, cell, resolution.Tech, model, csch);

        Assert.Equal(0, result.ErrorCount);
        var turned = result.Findings.Where(f => f.Id == "lvs.device.turned").ToList();
        Assert.All(turned, f => Assert.Equal(DiagnosticSeverity.Warning, f.Severity));
        Assert.Equal(["R1", "R2", "R3"], turned.Select(f => (string)f.Diagnostic.Arguments["refdes"]!).Order());
        Assert.Equal(3, result.WarningCount);
        Assert.Equal(["R1", "R2", "R3"], result.Turned.Select(t => t.Refdes).Order());
    }

    // ══ 2 — a tie is no statement, and no finding ═════════════════════════════════════════════════

    /// <summary>
    /// <b>A turned part the reading leaves straight on a tie is neither a warning nor an error.</b>
    /// Two capacitors, nothing stamped, one of them turned: the copper cannot say which strip is
    /// the supply, so nothing is called turned — and the comparison, which accepts a symmetric part
    /// either way round, finds nothing wrong either. Before the tie voted, its straight vote put both
    /// schematic nets on one strip and called it a short.
    /// </summary>
    [Fact]
    public void ATieLeftStraightIsNeitherAWarningNorAnError()
    {
        var fx = Board(parts: 2, turned: [1], stamped: false);

        var result = LvsRun.Run(fx.CellDir);

        Assert.Empty(result.Turned);
        Assert.True(!result.Findings.Any(f => f.Severity >= DiagnosticSeverity.Warning),
            string.Join("\n", result.Findings.Select(f => f.Render())));
    }

    // ══ 3 — gate 3: the cap does not reach the fix ════════════════════════════════════════════════

    /// <summary>
    /// <b>25 turned parts: all 25 in the result's list and in the panel's count, although the
    /// findings stop at 20.</b> A bulk Turn built on the findings would silently leave five parts
    /// the wrong way round.
    /// </summary>
    [Fact]
    public void TwentyFiveTurnedPartsAreAllListedDespiteTheCap()
    {
        var fx = Board(parts: 51, turned: [.. Enumerable.Range(0, 51).Where(i => i % 2 == 1).Take(25)], stamped: true);

        var vm = new LayoutEditorViewModel(LayoutPersistence.LoadFromFile(fx.Clay), fx.Clay);
        var result = vm.RunLvs()!;

        Assert.Equal(25, result.Turned.Count);
        Assert.Equal(LvsReport.MaxPerId, result.Findings.Count(f => f.Id == "lvs.device.turned"));
        Assert.Equal(25, vm.LvsTurnedParts.Count);
        Assert.Equal("Turn these 25 in the layout", vm.LvsTurnButtonText);
        Assert.Equal(0, result.ErrorCount);
    }

    // ══ 4 — gate 4: reduction does not hide one ═══════════════════════════════════════════════════

    /// <summary>
    /// <b>Three capacitors in parallel, one turned: that one is named</b> — with reduction on, which
    /// merges all three into one device on each side. The reading is taken before the merge.
    /// </summary>
    [Fact]
    public void ReductionDoesNotHideATurnedMember()
    {
        var fx = Board(parts: 3, turned: [2], stamped: true);

        var result = LvsRun.Run(fx.CellDir);

        Assert.Equal(ReductionMode.On, result.Reduction);
        Assert.Single(result.Layout.Devices);
        var finding = Assert.Single(result.Findings, f => f.Id == "lvs.device.turned");
        Assert.Equal("C3", finding.Diagnostic.Arguments["refdes"]);
        Assert.True(finding.HasMarker);
        Assert.Equal(0, result.ErrorCount);
    }

    // ══ 5 — gate 5: one reader, and the two windows agree ═════════════════════════════════════════

    /// <summary>
    /// <b>Nothing under <c>src/Design/Layout/Lvs</c> decides which parts are turned</b> except by its
    /// one call to <c>TurnedParts.Read</c> — and the one place a device's orientation is set is
    /// beside it, from its answer. Comment-stripped, because the files' own prose names the reader.
    /// </summary>
    [Fact]
    public void LvsHasOneCallToTheReaderAndNoDetectorOfItsOwn()
    {
        var calls = new List<string>();
        var setters = new List<string>();
        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot(), "src", "Design", "Layout", "Lvs"), "*.cs"))
        {
            string source = StripComments(File.ReadAllText(file));
            calls.AddRange(Enumerable.Repeat(Path.GetFileName(file),
                Regex.Matches(source, @"\bTurnedParts\.Read\(").Count));
            setters.AddRange(Enumerable.Repeat(Path.GetFileName(file),
                Regex.Matches(source, @"\bCrossedMembers\s*=(?!=)").Count));
        }

        Assert.Equal(["LayoutRead.cs"], calls);

        // LayoutRead puts the reading's answer on the device; the reduction only carries it across
        // a merge, member by member.
        Assert.Equal(["LayoutRead.cs", "LvsReduce.cs", "LvsReduce.cs"], setters.Order(StringComparer.Ordinal));
        string reduce = StripComments(File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Design", "Layout", "Lvs", "LvsReduce.cs")));
        Assert.Equal(2, Regex.Matches(reduce, @"\bCrossedMembers\s*=\s*\[\.\.").Count);
    }

    /// <summary>
    /// <b>Either window's Turn clears the other.</b> After the LVS panel's Turn, railRF's reading of
    /// the same layout turns nothing; after railRF's Turn, LVS reports no part turned.
    /// </summary>
    [Fact]
    public void ATurnInEitherWindowClearsTheOther()
    {
        // The LVS panel turns; railRF reads the document the panel edited.
        var first = Board(parts: 3, turned: [2], stamped: true, name: "First");
        var panel = new LayoutEditorViewModel(LayoutPersistence.LoadFromFile(first.Clay), first.Clay);
        panel.RunLvs();
        Assert.NotNull(panel.TurnInLayout(panel.SelectedLvsTurnedParts));

        var shapes = RailArtwork.FlattenedShapes(panel.Model, first.Clay, first.Tech);
        Assert.Empty(RailArtwork.PadsFor(panel.Model, first.Clay, first.Tech, null, null, shapes).Turned);

        // railRF turns (no layout window open, so into the file); LVS reads the file.
        var second = Board(parts: 3, turned: [2], stamped: true, name: "Second");
        var rail = RailRfFieldReport4Tests.OpenWindowOn(
            new RailRfFieldReport4Tests.Fx(second.View, second.Clay, second.Tech, second.CellDir));
        Assert.True(rail.HasPartsReadAsTurned);
        rail.TurnPartsCommand.Execute(null);

        var after = LvsRun.Run(second.CellDir);
        Assert.Empty(after.Turned);
        Assert.DoesNotContain(after.Findings, f => f.Id == "lvs.device.turned");
    }

    // ══ 6 — gate 6: a reversed diode ═════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A diode placed end for end is ONE <c>lvs.device.reversed</c> error and nothing else</b> —
    /// the anchor kept, the pair still paired, no unmatched device and no contradicted name. Its
    /// reversal is a different circuit, so it is an error and is never read as turned.
    /// </summary>
    [Fact]
    public void AReversedDiodeIsOneErrorAndThePairIsKept()
    {
        var fx = Board(parts: 3, turned: [2], stamped: true,
                       kind: i => i == 2 ? SymbolKind.Diode : SymbolKind.Capacitor);

        var result = LvsRun.Run(fx.CellDir);

        var finding = Assert.Single(result.Findings, f => f.Severity >= DiagnosticSeverity.Warning);
        Assert.Equal("lvs.device.reversed", finding.Id);
        Assert.Equal(DiagnosticSeverity.Error, finding.Severity);
        Assert.Empty(result.Turned);

        var pair = Assert.Single(result.Comparison.Devices, p => p.SchematicName == "C3");
        Assert.Equal(LvsPairedBy.Anchor, pair.By);
    }

    // ══ 7 — the panel's Turn ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The panel's Turn is one undo step, undo restores every instance, and the re-run is
    /// clean</b> — the re-run compares the document in hand, which is what the Turn edited.
    /// </summary>
    [Fact]
    public void ThePanelsTurnIsOneUndoStepAndTheRerunIsClean()
    {
        var fx = Board(parts: 5, turned: [1, 3], stamped: true);
        var vm = new LayoutEditorViewModel(LayoutPersistence.LoadFromFile(fx.Clay), fx.Clay);
        vm.RunLvs();
        Assert.Equal("Turn these 2 in the layout", vm.LvsTurnButtonText);

        var before = vm.Model.Instances.Select(Placement).ToList();

        var rerun = vm.TurnInLayout(vm.SelectedLvsTurnedParts);

        Assert.NotNull(rerun);
        Assert.True(rerun!.IsClean);
        Assert.Empty(rerun.Turned);
        Assert.False(vm.IsLvsStale);
        Assert.NotEqual(before, vm.Model.Instances.Select(Placement));

        vm.UndoCommand.Execute(null);
        Assert.Equal(before, vm.Model.Instances.Select(Placement));
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static (long X, long Y, double Rot) Placement(LayoutInstance i) => (i.X, i.Y, i.RotationDegrees);

    private sealed record Fx(LayoutView View, string Clay, Technology Tech, string CellDir);

    /// <summary>
    /// <paramref name="parts"/> two-pin parts at 1.5 mm pitch, each standing across a supply strip
    /// (below) and a return strip (above). The schematic puts every part's pin 1 on <c>VDD</c> and
    /// pin 2 on <c>VSS</c>; the parts in <paramref name="turned"/> are placed with pin 1 on the
    /// return strip instead. <paramref name="stamped"/> names the two strips on the copper, which is
    /// the evidence that lets the reader decide a part is turned.
    /// </summary>
    private Fx Board(
        int parts, IReadOnlyCollection<int> turned, bool stamped,
        Func<int, SymbolKind>? kind = null, string name = "Board")
    {
        string ws = Path.Combine(_root, name);
        var tech = TechFixture();
        Directory.CreateDirectory(Path.Combine(ws, "tech"));
        TechPersistence.SaveToFile(Path.Combine(ws, "tech", "Board.ctech"), tech);
        WorkspacePersistence.SaveToFile(
            Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = Path.Combine("tech", "Board.ctech") });
        WriteLandCell(ws);

        string cellDir = CellFolder.CreateCellFolder(ws, "Board");
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);

        var view = new LayoutView { DbuPerMicron = Dbu, TechRef = Path.Combine("..", "..", "tech", "Board.ctech") };
        long right = Mm(1.5 * (parts - 1) + 0.5);
        view.Shapes.Add(new RectShape
        {
            Layer = Top, X1 = -Mm(0.5), Y1 = -Mm(0.7), X2 = right, Y2 = -Mm(0.1), Net = stamped ? "VDD" : null,
        });
        view.Shapes.Add(new RectShape
        {
            Layer = Top, X1 = -Mm(0.5), Y1 = Mm(0.1), X2 = right, Y2 = Mm(0.7), Net = stamped ? "VSS" : null,
        });

        for (int i = 0; i < parts; i++)
            view.Instances.Add(new LayoutInstance
            {
                CellRef = Path.Combine("..", "..", "Land"),
                X = Mm(1.5 * i), Y = 0, Mag = 1.0, SchematicId = $"C{i + 1}",
                RotationDegrees = turned.Contains(i) ? 270 : 90,
            });

        string clay = Path.Combine(layoutDir, "Board.clay");
        LayoutPersistence.SaveToFile(clay, view);

        var model = new SchematicEditModel();
        for (int i = 0; i < parts; i++)
            model.Components.Add(new EditableComponent
            {
                InstanceName = $"C{i + 1}", Symbol = kind?.Invoke(i) ?? SymbolKind.Capacitor, X = 400 * i, Y = 0,
            });
        var top = new EditableWire(); top.Points.AddRange([(0.0, -200.0), (400.0 * (parts - 1), -200.0)]);
        var bot = new EditableWire(); bot.Points.AddRange([(0.0, 200.0), (400.0 * (parts - 1), 200.0)]);
        model.Wires.Add(top);
        model.Wires.Add(bot);
        model.NetLabels.Add(new EditableNetLabel { X = 200, Y = -200, Name = "VDD" });
        model.NetLabels.Add(new EditableNetLabel { X = 200, Y = 200, Name = "VSS" });
        SchematicPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Schematic), "Board.csch"), model, "Board");

        return new Fx(view, clay, tech, cellDir);
    }

    /// <summary>A two-pin land pattern with a terminal map — pin 1 on the left, pin 2 on the right.</summary>
    private static void WriteLandCell(string ws)
    {
        string cellDir = CellFolder.CreateCellFolder(ws, "Land");
        var cell = new LayoutView { DbuPerMicron = Dbu };
        foreach (var (pin, x) in new[] { ("1", -Mm(0.4)), ("2", Mm(0.4)) })
        {
            cell.Pins.Add(new LayoutPin { Name = pin, X = x, Y = 0, WidthDbu = Mm(0.3), Layer = Top });
            cell.Shapes.Add(new RectShape
            {
                Layer = Top, Pin = pin, X1 = x - Mm(0.15), Y1 = -Mm(0.15), X2 = x + Mm(0.15), Y2 = Mm(0.15),
            });
        }
        LayoutPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "Land.clay"), cell);

        CellPersistence.SaveToFile(Path.Combine(cellDir, CellFolder.CcellFileName), new CcellFile
        {
            NumPorts = 2,
            Terminals =
            [
                new CcellTerminal { Port = 1, Name = "1", LayoutPin = ["1"] },
                new CcellTerminal { Port = 2, Name = "2", LayoutPin = ["2"] },
            ],
        });
    }

    private static Technology TechFixture()
    {
        var tech = new Technology { Name = "Board" };
        tech.Layers =
        [
            new LayerDef { Key = Top, Name = "TOP", ZOrder = 0, Color = new Rgba(200, 80, 40, 255) },
            new LayerDef { Key = Bot, Name = "BOT", ZOrder = 1, Color = new Rgba(40, 90, 200, 255) },
        ];
        tech.Stackup.Layers =
        [
            new StackupLayer { Kind = StackupKind.Conductor, Name = "TOP", ThicknessDbu = Mm(0.035), SigmaSm = 5.8e7, DrawingLayers = [Top] },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "PP", ThicknessDbu = Mm(0.2), Epsr = 4.3, TanD = 0.02 },
            new StackupLayer { Kind = StackupKind.Conductor, Name = "BOT", ThicknessDbu = Mm(0.035), SigmaSm = 5.8e7, DrawingLayers = [Bot], IsGroundReference = true },
        ];
        return tech;
    }

    private static string StripComments(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(src, @"//[^\n]*", "");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}

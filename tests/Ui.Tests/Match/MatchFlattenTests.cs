using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Matching;
using CircuitRF.Engine;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Match;

/// <summary>
/// MN-5's gates (brief §5). Flatten writes FILES and replaces an instance, so these run against a
/// real cell folder on disk and a real extraction of it — the two things a view-model test could not
/// see are exactly the two the brief cares about.
/// </summary>
public sealed class MatchFlattenTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-match-flatten-" + Guid.NewGuid().ToString("N")[..10]);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a temp folder is not worth failing a test over */ }
    }

    // ── fixtures ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The digit count the tests below ask a flatten for when what they are measuring is EXACTNESS.
    /// </summary>
    /// <remarks>
    /// <b>The shipping default is the Designer's readout setting — three digits</b> (owner,
    /// 2026-08-28: the cell must show the number the pane showed), so a flattened cell is normally a
    /// ROUNDED copy of the design and agrees with the <c>Match</c> component to about one part in a
    /// thousand rather than 1e-12. Asking for 15 digits here takes that rounding off the table so the
    /// agreement gates keep measuring what they were written to measure — that the writer put the
    /// right elements, in the right arms, on the right nets. The two failures are orders of magnitude
    /// apart: a misplaced element moves |ΔS| by O(1), the rounding by 10^−digits.
    /// <see cref="TheFlattenedCell_WritesItsValues_AtTheDesignersOwnSignificantDigits"/> is what
    /// pins the default.
    /// </remarks>
    private const int ExactDigits = 15;

    /// <summary>match.md §4.9's interstage problem — two absorbed ends, one series and one parallel.</summary>
    private static MatchDesign Golden() => new()
    {
        F1 = 3.3e9,
        F2 = 5.0e9,
        Order = 4,
        Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(200.0, ReactanceKind.C, TerminationTopology.Parallel, 0.125e-12),
        Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 10e-12),
    };

    private static readonly double[] Band =
        [1e9, 2e9, 3.3e9, 3.8e9, 4.06202e9, 4.5e9, 5.0e9, 7e9, 12e9];

    /// <summary>A workspace holding one cell whose schematic instantiates the Match.</summary>
    private (SchematicViewModel Vm, EditableComponent Match) Workspace(MatchDesign design)
    {
        string cellDir = CellFolder.CreateCellFolder(_root, "top");
        string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);

        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        var match = new EditableComponent
        {
            InstanceName = "MN1", Symbol = SymbolKind.Match, X = 0, Y = 0,
        };
        match.Parameters.Add(new EditableParameter
        {
            Name = MatchEmbedding.DesignParameter, Expression = MatchEmbedding.Encode(design),
            ShowOnSchematic = false,
        });
        model.Components.Add(match);

        var vm = new SchematicViewModel(model) { WorkspaceRootProvider = () => _root };
        return (vm, match);
    }

    /// <summary>Reads cells off disk, exactly as <c>WorkspaceViewModel.Resolve</c> does.</summary>
    private sealed class DiskResolver : ICellResolver
    {
        public CellResolution? Resolve(EditableComponent comp, SchematicEditModel containing)
        {
            if (comp.CellRef is null || containing.SchematicDirectory is null) return null;

            string cellDir = Path.GetFullPath(Path.Combine(containing.SchematicDirectory, comp.CellRef));
            var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
            if (primary.ResolvedName is null) return null;

            string path = Path.Combine(cellDir, CellFolder.SchematicSubFolder, primary.ResolvedName);
            var (model, _, _) = SchematicPersistence.LoadFromFile(path);
            model.SchematicDirectory = Path.GetDirectoryName(path);

            IReadOnlyList<ParameterDeclaration> declared = [];
            string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
            if (File.Exists(ccellPath))
                declared = [.. CellPersistence.LoadFromFile(ccellPath).Parameters
                    .Select(p => new ParameterDeclaration(
                        p.Name, p.DefaultExpression,
                        string.IsNullOrEmpty(p.Unit) ? null : p.Unit, hidden: !p.ShowOnSchematic))];

            return new CellResolution(Path.GetFileName(cellDir), model, declared);
        }
    }

    /// <summary>A 2-port sweep of a schematic wired between two 50 Ω Terms.</summary>
    private static Complex[,][] Sweep(SchematicEditModel model, double[] frequencies)
    {
        var extracted = NetExtractor.Extract(model, "tb", new DiskResolver());
        Assert.DoesNotContain(extracted.Conflicts, c => !c.Contains("MEAS", StringComparison.Ordinal));

        var netlist = new Elaborator(extracted.Library).Elaborate(extracted.TestBench);
        var ds = SParameterEngine.Run(netlist, frequencies);
        var s = ds["S"];

        var result = new Complex[2, 2][];
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                result[i, j] = new Complex[frequencies.Length];
                for (int f = 0; f < frequencies.Length; f++)
                    result[i, j][f] = (Complex)s[f, i, j];
            }
        return result;
    }

    private static double WorstDifference(Complex[,][] a, Complex[,][] b)
    {
        double worst = 0.0;
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                for (int f = 0; f < a[i, j].Length; f++)
                    worst = Math.Max(worst, (a[i, j][f] - b[i, j][f]).Magnitude);
        return worst;
    }

    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    private static EditableComponent Term(int num, double x, double y)
    {
        var c = new EditableComponent { InstanceName = $"T{num}", Symbol = SymbolKind.Term, X = x, Y = y };
        c.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString() });
        c.Parameters.Add(new EditableParameter { Name = "Z", Expression = "50", Unit = "Ω" });
        return c;
    }

    /// <summary>Wraps whatever sits between (−200,0) and (+200,0) in a 50 Ω two-port testbench.</summary>
    private static void AddTestBenchAround(SchematicEditModel model)
    {
        model.Components.Add(Term(1, -600, -200));   // "+" at (−600,−400), "−" at (−600,0)
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Ground, X = -600, Y = 100 });
        model.Wires.Add(Wire((-600, 0), (-600, 100)));
        model.Wires.Add(Wire((-600, -400), (-200, -400), (-200, 0)));

        model.Components.Add(Term(2, 600, -200));
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Ground, X = 600, Y = 100 });
        model.Wires.Add(Wire((600, 0), (600, 100)));
        model.Wires.Add(Wire((600, -400), (200, -400), (200, 0)));
    }

    // ── §5: the whole point ───────────────────────────────────────────────────

    /// <summary>
    /// <b>A Match and the cell its Flatten produces give identical S-parameters.</b> Not "similar" —
    /// the same elements on the same nets, so the only difference is that one stamps them from a
    /// design blob and the other from placed components.
    /// </summary>
    [Fact]
    public void AMatch_AndTheCellItsFlattenProduces_AgreeToOnePartInATrillion()
    {
        var (vm, match) = Workspace(Golden());
        AddTestBenchAround(vm.EditModel);

        var before = Sweep(vm.EditModel, Band);

        var run = MatchFlattenService.Run(
            vm, match, _root, "MN1_match", replaceInPlace: true, significantDigits: ExactDigits);
        Assert.True(run.Ok, run.Message);
        output.WriteLine(run.Message);

        var after = Sweep(vm.EditModel, Band);

        double worst = WorstDifference(before, after);
        output.WriteLine($"worst |ΔS| over {Band.Length} frequencies: {worst:E3}");
        Assert.True(worst < 1e-12, $"the component and the flattened cell differ by {worst:E3}");
    }

    /// <summary>
    /// <b>The same equivalence in lowpass and highpass form</b> (match.md §16).
    /// </summary>
    /// <remarks>
    /// The arms are SINGLE elements here, so the flatten writer's "a series L+C arm is two
    /// components" placement rule has half as much to place and the ladder alternates on every
    /// position rather than every arm. Nothing in the writer, the stamp or the extraction was changed
    /// for it; this is the test that says so.
    /// </remarks>
    [Theory]
    [InlineData(NetworkForm.Lowpass)]
    [InlineData(NetworkForm.Highpass)]
    public void AMatchInEitherNewForm_AndItsFlattenedCell_AlsoAgree(NetworkForm form)
    {
        // Mixed the way each form absorbs: lowpass takes the shunt C at the HIGH-impedance end and
        // the series L at the low one; highpass takes their duals (match.md §16.4, as corrected —
        // the impedance ratio decides which end takes which).
        var design = new MatchDesign
        {
            F1 = 1.8e9, F2 = 2.2e9, Order = 4, Form = form,
            Response = ResponseShape.ChebyshevFano,
            Term1 = form == NetworkForm.Lowpass
                ? new Termination(50.0, ReactanceKind.C, TerminationTopology.Parallel, 0.5e-12)
                : new Termination(50.0, ReactanceKind.L, TerminationTopology.Parallel, 15e-9),
            Term2 = form == NetworkForm.Lowpass
                ? new Termination(25.0, ReactanceKind.L, TerminationTopology.Series, 0.1e-9)
                : new Termination(25.0, ReactanceKind.C, TerminationTopology.Series, 80e-12),
        };

        var (vm, match) = Workspace(design);
        AddTestBenchAround(vm.EditModel);

        var before = Sweep(vm.EditModel, Band);
        var run = MatchFlattenService.Run(
            vm, match, _root, "MN1_match", replaceInPlace: true, significantDigits: ExactDigits);
        Assert.True(run.Ok, run.Message);
        var after = Sweep(vm.EditModel, Band);

        double worst = WorstDifference(before, after);
        output.WriteLine($"{form}: worst |ΔS| over {Band.Length} frequencies: {worst:E3}");
        Assert.True(worst < 1e-12, $"the component and the flattened cell differ by {worst:E3}");
    }

    /// <summary>
    /// <b>The same equivalence for a DUAL-BAND design</b> (match.md §18).
    /// </summary>
    /// <remarks>
    /// Eight elements in four resonant arms rather than four in two — an ordinary bandpass ladder,
    /// twice as long. Nothing in the flatten writer, the stamp or the extraction was changed for it,
    /// and that they all handle it is §18.2's structural claim rather than a detail; this is the test
    /// that says so end to end.
    /// </remarks>
    [Fact]
    public void ADualBandMatch_AndItsFlattenedCell_AlsoAgree()
    {
        var design = new MatchDesign
        {
            BandCount = 2,
            F1 = 2.4e9, F2 = 2.5e9, F3 = 5.15e9, F4 = 5.85e9,
            Order = 2,
            Response = ResponseShape.ChebyshevFano,
            Term1 = new Termination(20.0, ReactanceKind.C, TerminationTopology.Parallel, 2.5e-12),
            Term2 = Termination.Resistive(50.0),
            AnalysisEnd = AnalysisEndChoice.Term1,
        };

        var (vm, match) = Workspace(design);
        AddTestBenchAround(vm.EditModel);

        var before = Sweep(vm.EditModel, Band);
        var run = MatchFlattenService.Run(
            vm, match, _root, "MN1_match", replaceInPlace: true, significantDigits: ExactDigits);
        Assert.True(run.Ok, run.Message);
        var after = Sweep(vm.EditModel, Band);

        double worst = WorstDifference(before, after);
        output.WriteLine($"dual-band: worst |ΔS| over {Band.Length} frequencies: {worst:E3}");
        Assert.True(worst < 1e-12, $"the component and the flattened cell differ by {worst:E3}");
    }

    /// <summary>
    /// <b>The terminations are disabled</b>: the netlist ignores them, and enabling both Terms and
    /// running the CELL ALONE reproduces the Designer's own response.
    /// </summary>
    [Fact]
    public void TheTerminations_AreDisabled_AndEnablingThemReproducesTheDesignersResponse()
    {
        var design = Golden();
        var (vm, match) = Workspace(design);

        var run = MatchFlattenService.Run(
            vm, match, _root, "MN1_match", replaceInPlace: false, significantDigits: ExactDigits);
        Assert.True(run.Ok, run.Message);

        var (cell, _, _) = SchematicPersistence.LoadFromFile(PrimarySchematicPath(run.CellDir!));

        // Every Term, every Ground beside one, and every absorbed reactance: Open.
        var terms = cell.Components.Where(c => c.Symbol == SymbolKind.Term).ToList();
        Assert.Equal(2, terms.Count);
        Assert.All(terms, t => Assert.Equal(DisableState.Open, t.Disable));

        var network = MatchRebuild.Rebuild(design).Network!;
        foreach (var absorbed in network.Elements.Where(e => e.IsAbsorbed))
        {
            var placed = cell.Components.First(c => c.InstanceName == absorbed.Name);
            Assert.Equal(DisableState.Open, placed.Disable);
        }

        // Disabled → the cell's own netlist has no ports at all: the two Terms are the only ones.
        cell.SchematicDirectory = Path.GetDirectoryName(PrimarySchematicPath(run.CellDir!));
        var asWritten = NetExtractor.Extract(cell, "tb", new DiskResolver());
        Assert.DoesNotContain(asWritten.TestBench.Instances, i => i.Reference == "Port");

        // Enable them, and the cell alone reproduces the plot the Designer drew.
        foreach (var t in cell.Components.Where(c =>
                     c.Symbol is SymbolKind.Term or SymbolKind.Ground
                     || network.Elements.Any(e => e.IsAbsorbed && e.Name == c.InstanceName)))
            t.Disable = DisableState.None;

        var extracted = NetExtractor.Extract(cell, "tb", new DiskResolver());
        var netlist = new Elaborator(extracted.Library).Elaborate(extracted.TestBench);
        var ds = SParameterEngine.Run(netlist, Band);

        double worst = 0.0;
        for (int f = 0; f < Band.Length; f++)
        {
            var s11 = (Complex)ds["S"][f, 0, 0];
            var s21 = (Complex)ds["S"][f, 1, 0];
            var (rs11, rs21) = MatchResponse.At(network, Band[f]);
            worst = Math.Max(worst, Math.Max((s11 - rs11).Magnitude, (s21 - rs21).Magnitude));
        }
        output.WriteLine($"worst |ΔS| vs the Designer's own response: {worst:E3}");
        Assert.True(worst < 1e-9,
            $"the enabled cell must reproduce the Designer's response; it differs by {worst:E3}");
    }

    /// <summary>
    /// <b>The flattened cell carries the numbers the Designer was showing</b> (owner, 2026-08-28: an
    /// inductor that reads 1.201 pH in the Designer must not land in the cell as 1.20099999999 pH).
    /// Every element value is the design's own value rounded to the Designer's <c>Significant
    /// digits</c> setting — no more digits than that, and the correctly-rounded number rather than a
    /// truncation of it.
    /// </summary>
    /// <remarks>
    /// The check is on the WRITTEN TEXT and on its distance from the exact value, not against a
    /// second call to the formatter: re-deriving the expected string from the same helper the writer
    /// uses would pass however many digits it emitted. Four digits, not the default three, so the
    /// assertion cannot be satisfied by a coincidence of the default.
    /// </remarks>
    [Fact]
    public void TheFlattenedCell_WritesItsValues_AtTheDesignersOwnSignificantDigits()
    {
        const int digits = 4;

        var design = Golden();
        var (vm, match) = Workspace(design);

        var designer = new MatchDesignerViewModel();
        designer.SetTarget(vm, match);
        designer.Settings.SignificantDigits = digits;

        var run = designer.Flatten(_root, "MN1_match", replaceInPlace: false);
        Assert.True(run.Ok, run.Message);

        var (cell, _, _) = SchematicPersistence.LoadFromFile(PrimarySchematicPath(run.CellDir!));
        var network = MatchRebuild.Rebuild(design).Network!;

        var placed = cell.Components
            .Where(c => c.Symbol is SymbolKind.Inductor or SymbolKind.Capacitor)
            .ToList();
        Assert.Equal(network.Elements.Count, placed.Count);

        foreach (var c in placed)
        {
            var element = network.Elements.Single(e => e.Name == c.InstanceName);
            var p = c.Parameters.Single(x => x.Name is "L" or "C");

            int written = SignificantDigitsIn(p.Expression);
            double si = double.Parse(p.Expression, System.Globalization.CultureInfo.InvariantCulture)
                        * MatchValueFormat.Scale(p.Unit);
            double relative = Math.Abs(si - element.Value) / Math.Abs(element.Value);

            output.WriteLine(
                $"{c.InstanceName}: wrote '{p.Expression} {p.Unit}' ({written} digits) for "
                + $"{element.Value:E15}; relative {relative:E2}");

            Assert.True(written <= digits,
                $"{c.InstanceName} was written as '{p.Expression}' — {written} significant digits, "
                + $"but the Designer is set to {digits}");
            // …and it is that value ROUNDED, not truncated or otherwise a different number: half a
            // unit in the last significant place is 5e-1 x 10^-(digits-1).
            Assert.True(relative <= 5e-4,
                $"{c.InstanceName}: '{p.Expression} {p.Unit}' is {relative:E2} away from the "
                + "design's own value, which is more than rounding to 4 digits can explain");
        }
    }

    /// <summary>
    /// How many significant digits a plain decimal string carries — leading zeros are placeholders
    /// and trailing zeros after rounding are padding, so "0.1250" and "125" both carry three.
    /// </summary>
    private static int SignificantDigitsIn(string number)
    {
        string d = new string([.. number.Where(char.IsDigit)]).TrimStart('0');
        if (number.Contains('.', StringComparison.Ordinal)) d = d.TrimEnd('0');
        return Math.Max(1, d.Length);
    }

    // ── §3: replacing in place ────────────────────────────────────────────────

    /// <summary>
    /// <b>Every net that touched the Match touches the new instance.</b> The symbol is a copy, so
    /// the pins are in the same places and no wire has to move — which is the whole reason §1 copies
    /// the glyph instead of auto-generating one.
    /// </summary>
    [Fact]
    public void InPlaceReplacement_KeepsEveryWire()
    {
        var (vm, match) = Workspace(Golden());
        AddTestBenchAround(vm.EditModel);

        var wiresBefore = vm.EditModel.Wires.Select(w => (w.Id, Points: w.Points.ToList())).ToList();
        var netsBefore = NetsAtPins(vm.EditModel, match);
        Assert.Equal(2, netsBefore.Count);

        var run = MatchFlattenService.Run(vm, match, _root, "MN1_match", replaceInPlace: true);
        Assert.True(run.Ok, run.Message);
        Assert.NotNull(run.Replacement);

        Assert.DoesNotContain(match, vm.EditModel.Components);
        Assert.Contains(run.Replacement!, vm.EditModel.Components);

        // Not one wire vertex moved.
        foreach (var (id, points) in wiresBefore)
        {
            var now = vm.EditModel.Wires.First(w => w.Id == id);
            Assert.Equal(points, now.Points);
        }

        var netsAfter = NetsAtPins(vm.EditModel, run.Replacement!);
        Assert.Equal(netsBefore, netsAfter);
        output.WriteLine($"pins on nets {string.Join(", ", netsAfter)} before and after");
    }

    /// <summary>The nets a component's pins sit on, in pin order, from a real extraction.</summary>
    private static List<string> NetsAtPins(SchematicEditModel model, EditableComponent comp)
    {
        var extracted = NetExtractor.Extract(model, "tb", new DiskResolver());
        var instance = extracted.TestBench.Instances
            .FirstOrDefault(i => i.InstanceName == comp.InstanceName);
        Assert.True(instance is not null,
            $"'{comp.InstanceName}' is not in the netlist: "
            + string.Join(", ", extracted.TestBench.Instances.Select(i => i.InstanceName)));
        return [.. instance!.NetBindings];
    }

    /// <summary>
    /// <b>One undo reverses everything</b> — the instance, the cell reference and the files. That is
    /// deliberately stronger than Layout's Group into Cell, which keeps its folder; see
    /// <c>FlattenMatchCommand</c> for why the two differ.
    /// </summary>
    [Fact]
    public void OneUndo_ReversesTheInstance_TheCellReference_AndTheFiles()
    {
        var (vm, match) = Workspace(Golden());
        AddTestBenchAround(vm.EditModel);

        var run = MatchFlattenService.Run(vm, match, _root, "MN1_match", replaceInPlace: true);
        Assert.True(run.Ok, run.Message);
        Assert.True(Directory.Exists(run.CellDir));

        vm.UndoRedo.Undo();

        Assert.Contains(match, vm.EditModel.Components);
        Assert.DoesNotContain(run.Replacement!, vm.EditModel.Components);
        Assert.False(Directory.Exists(run.CellDir), "undo must remove the cell folder it created");

        // …and redo puts all three back.
        vm.UndoRedo.Redo();
        Assert.Contains(run.Replacement!, vm.EditModel.Components);
        Assert.True(Directory.Exists(run.CellDir));
        Assert.True(File.Exists(PrimarySchematicPath(run.CellDir!)));
    }

    /// <summary>
    /// An undo does <b>not</b> delete a folder somebody has edited since. An undo stack may reverse
    /// what it did; it may not destroy work it did not do.
    /// </summary>
    [Fact]
    public void Undo_LeavesACellSomebodyHasEditedSince_Alone()
    {
        var (vm, match) = Workspace(Golden());

        var run = MatchFlattenService.Run(vm, match, _root, "MN1_match", replaceInPlace: true);
        Assert.True(run.Ok, run.Message);

        File.WriteAllText(Path.Combine(run.CellDir!, "notes.txt"), "mine now");

        vm.UndoRedo.Undo();
        Assert.True(Directory.Exists(run.CellDir), "a folder with somebody else's file in it must survive undo");
        Assert.Contains(match, vm.EditModel.Components);
    }

    // ── §1: what the cell contains ────────────────────────────────────────────

    /// <summary>
    /// <b>Series arms are two components</b> — an L and a C — never one L carrying a C= parameter.
    /// Someone will "simplify" this, because <c>InductorModel</c> stamps the combined form
    /// identically; the user's next action is to edit or sweep one of the two.
    /// </summary>
    [Fact]
    public void SeriesArms_AreWrittenAsTwoComponents()
    {
        var design = Golden();
        var (vm, match) = Workspace(design);

        var run = MatchFlattenService.Run(vm, match, _root, "MN1_match", replaceInPlace: false);
        Assert.True(run.Ok, run.Message);

        var (cell, _, _) = SchematicPersistence.LoadFromFile(PrimarySchematicPath(run.CellDir!));
        var network = MatchRebuild.Rebuild(design).Network!;

        // One placed component per stamped element, named exactly as MN-1 named it.
        foreach (var e in network.Elements.Where(e => !e.IsAbsorbed))
        {
            var placed = cell.Components.SingleOrDefault(c => c.InstanceName == e.Name);
            Assert.True(placed is not null, $"'{e.Name}' is not in the flattened cell");
            Assert.Equal(e.Type == ElementType.L ? SymbolKind.Inductor : SymbolKind.Capacitor,
                         placed!.Symbol);
            Assert.Equal(DisableState.None, placed.Disable);
        }

        // No inductor anywhere carries a C= parameter — that is the shape being refused.
        foreach (var l in cell.Components.Where(c => c.Symbol == SymbolKind.Inductor))
            Assert.DoesNotContain(l.Parameters, p => p.Name == "C");

        int seriesElements = network.Elements.Count(e => !e.IsAbsorbed && !e.IsShunt);
        output.WriteLine($"{seriesElements} through-path elements written as {seriesElements} components");
        Assert.True(seriesElements >= 2, "the golden design has series ARMS of two elements each");
    }

    /// <summary>
    /// <b>The symbol is copied, not referenced.</b> <c>BuiltInSymbols</c> hands out ONE cached
    /// instance per kind whose primitives are mutable, so a shared reference would leave the cell's
    /// symbol aliased to the application's own glyph.
    /// </summary>
    [Fact]
    public void TheSymbol_IsCopied_NotReferenced()
    {
        var (vm, match) = Workspace(Golden());
        var run = MatchFlattenService.Run(vm, match, _root, "MN1_match", replaceInPlace: false);
        Assert.True(run.Ok, run.Message);

        var builtIn = BuiltInSymbols.Primitives(SymbolKind.Match);
        var onDisk = SymbolPersistence.LoadFromFile(PrimarySymbolPath(run.CellDir!));

        // Same glyph…
        Assert.Equal(builtIn.Primitives.Count, onDisk.Primitives.Count);
        Assert.Equal(
            builtIn.Primitives.Select(p => p.GetType().Name),
            onDisk.Primitives.Select(p => p.GetType().Name));

        // …same two pins, in the same places, which is what keeps the wires.
        Assert.Equal(
            builtIn.Pins.OrderBy(p => p.PortIndex).Select(p => (p.PortIndex, p.LocalX, p.LocalY)),
            onDisk.Pins.OrderBy(p => p.PortIndex).Select(p => (p.PortIndex, p.LocalX, p.LocalY)));

        // …and not one shared object between them.
        foreach (var p in onDisk.Primitives)
            Assert.DoesNotContain(builtIn.Primitives, b => ReferenceEquals(b, p));

        // Editing the application's own glyph afterwards cannot reach the file.
        var body = (RoundedRectPrimitive)builtIn.Primitives.First(p => p is RoundedRectPrimitive);
        double original = body.W;
        try
        {
            body.W = 9999.0;
            var reloaded = SymbolPersistence.LoadFromFile(PrimarySymbolPath(run.CellDir!));
            Assert.Equal(original, ((RoundedRectPrimitive)reloaded.Primitives
                .First(p => p is RoundedRectPrimitive)).W);
        }
        finally { body.W = original; }
    }

    /// <summary>
    /// <b>The design blob travels.</b> Re-opening the generated cell reconstructs the original
    /// design: the same ladder, the same transforms and the same N's. A flattened cell that has
    /// forgotten what it was is a dead end six months later.
    /// </summary>
    [Fact]
    public void ReopeningTheGeneratedCell_ReconstructsTheOriginalDesign()
    {
        var design = Golden();
        var (vm, match) = Workspace(design);

        // A design with real transform state, so "the same N's" is a claim about something.
        var designer = new MatchDesignerViewModel();
        designer.SetTarget(vm, match);
        var pair = designer.AvailablePairs().First();
        designer.AddTransform(pair);
        designer.Transforms[0].N = designer.Transforms[0].NMin
            + 0.5 * (designer.Transforms[0].NMax - designer.Transforms[0].NMin);
        var edited = designer.Design.Clone();
        Assert.Single(edited.Transforms);

        var run = MatchFlattenService.Run(vm, match, _root, "MN1_match", replaceInPlace: true);
        Assert.True(run.Ok, run.Message);

        var reopened = MatchFlatten.TryReadDesign(run.CellDir!);
        Assert.NotNull(reopened);

        Assert.Equal(edited.F1, reopened!.F1);
        Assert.Equal(edited.F2, reopened.F2);
        Assert.Equal(edited.Order, reopened.Order);
        Assert.Equal(edited.Response, reopened.Response);
        Assert.Equal(edited.Transforms.Count, reopened.Transforms.Count);
        for (int i = 0; i < edited.Transforms.Count; i++)
        {
            Assert.Equal(edited.Transforms[i].ElementA, reopened.Transforms[i].ElementA);
            Assert.Equal(edited.Transforms[i].ElementB, reopened.Transforms[i].ElementB);
            Assert.Equal(edited.Transforms[i].Form, reopened.Transforms[i].Form);
            Assert.Equal(edited.Transforms[i].N, reopened.Transforms[i].N, 12);
        }

        // The ladder it rebuilds to is the ladder that was written.
        var original = MatchRebuild.Rebuild(edited).Network!;
        var restored = MatchRebuild.Rebuild(reopened).Network!;
        Assert.Equal(original.Elements.Count, restored.Elements.Count);
        for (int i = 0; i < original.Elements.Count; i++)
        {
            Assert.Equal(original.Elements[i].Name, restored.Elements[i].Name);
            Assert.Equal(original.Elements[i].Value, restored.Elements[i].Value, 15);
        }
        designer.Dispose();
    }

    /// <summary>The annotation says what the network is, and that the Terms can be switched on.</summary>
    [Fact]
    public void TheAnnotation_RecordsTheDesign_AndOffersTheTermsBack()
    {
        var design = Golden();
        var (vm, match) = Workspace(design);
        var run = MatchFlattenService.Run(vm, match, _root, "MN1_match", replaceInPlace: false,
                                          stampedUtc: new DateTime(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc));
        Assert.True(run.Ok, run.Message);

        var (cell, _, _) = SchematicPersistence.LoadFromFile(PrimarySchematicPath(run.CellDir!));
        var text = cell.CanvasObjects.OfType<EditableText>().Single().Text;
        output.WriteLine(text);

        Assert.Contains("3.3 – 5 GHz", text, StringComparison.Ordinal);
        Assert.Contains("order 4", text, StringComparison.Ordinal);
        Assert.Contains("single-match", text, StringComparison.Ordinal);   // match.md §6.9
        Assert.Contains("bandpass", text, StringComparison.Ordinal);       // match.md §16.7
        Assert.Contains("200 Ω parallel", text, StringComparison.Ordinal);
        Assert.Contains("1.25 Ω series", text, StringComparison.Ordinal);
        Assert.Contains("return loss", text, StringComparison.Ordinal);
        Assert.Contains("ripple", text, StringComparison.Ordinal);
        Assert.Contains("Π N²", text, StringComparison.Ordinal);
        Assert.Contains("2026-08-19", text, StringComparison.Ordinal);
        Assert.Contains("Enable both Terms", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The generated cell opens: it builds a render model, nothing sits on top of anything else, and
    /// every element pin is on the connection grid. A cell that simulates correctly and cannot be
    /// read is half a deliverable.
    /// </summary>
    [Fact]
    public void TheGeneratedCell_OpensAndIsLegible()
    {
        var (vm, match) = Workspace(Golden());
        var run = MatchFlattenService.Run(vm, match, _root, "MN1_match", replaceInPlace: false);
        Assert.True(run.Ok, run.Message);

        var (cell, _, _) = SchematicPersistence.LoadFromFile(PrimarySchematicPath(run.CellDir!));
        var (rendered, _) = cell.BuildRenderModel();
        Assert.Equal(cell.Components.Count, rendered.Components.Count);
        Assert.Single(cell.CanvasObjects.OfType<EditableText>());

        // No two components share a centre — the one way a generated layout hides half of itself.
        var positions = cell.Components.Select(c => (c.X, c.Y)).ToList();
        Assert.Equal(positions.Count, positions.Distinct().Count());

        // Every pin lands on the 100-unit connection grid, so a user can wire to it.
        foreach (var c in cell.Components)
            for (int i = 0; i < c.PortCount; i++)
            {
                var (px, py) = c.GetPortWorldCoord(i);
                Assert.Equal(0.0, px % 100.0, 9);
                Assert.Equal(0.0, py % 100.0, 9);
            }

        output.WriteLine(
            $"{rendered.Components.Count} components, bounding box "
            + $"{rendered.BbMinX:0}..{rendered.BbMaxX:0} x {rendered.BbMinY:0}..{rendered.BbMaxY:0}");
    }

    /// <summary>
    /// A design whose transforms have not reached their target says so, and says which resistances
    /// its Terms are carrying — the number beside a 200 Ω termination would otherwise read as a bug.
    /// </summary>
    [Fact]
    public void AnUnfinishedDesign_SaysWhichReferenceItsTermsCarry()
    {
        var design = Golden();
        var (vm, match) = Workspace(design);
        var run = MatchFlattenService.Run(
            vm, match, _root, "MN1_match", replaceInPlace: false, significantDigits: ExactDigits);
        Assert.True(run.Ok, run.Message);

        var (cell, _, _) = SchematicPersistence.LoadFromFile(PrimarySchematicPath(run.CellDir!));
        string text = cell.CanvasObjects.OfType<EditableText>().Single().Text;
        Assert.Contains("not reached", text, StringComparison.Ordinal);
        Assert.Contains("the ladder's own reference", text, StringComparison.Ordinal);

        // And the Terms really do carry the ladder's own ends, which is what reproduces the plot.
        var network = MatchRebuild.Rebuild(design).Network!;
        var t1 = cell.Components.Single(c => c.InstanceName == "T1");
        Assert.Equal(network.R1,
            double.Parse(t1.Parameters.Single(p => p.Name == "Z").Expression,
                         System.Globalization.CultureInfo.InvariantCulture)
            * MatchValueFormat.Scale(t1.Parameters.Single(p => p.Name == "Z").Unit), 12);
        output.WriteLine(text);
    }

    // ── ordinary care ─────────────────────────────────────────────────────────

    /// <summary>Flattening twice refuses the name rather than writing over the first cell.</summary>
    [Fact]
    public void FlatteningTwice_Refuses_RatherThanOverwriting()
    {
        var (vm, match) = Workspace(Golden());

        var first = MatchFlattenService.Run(vm, match, _root, "MN1_match", replaceInPlace: false);
        Assert.True(first.Ok, first.Message);
        string marker = Path.Combine(first.CellDir!, "marker.txt");
        File.WriteAllText(marker, "the first cell");

        var second = MatchFlattenService.Run(vm, match, _root, "MN1_match", replaceInPlace: false);
        Assert.False(second.Ok);
        Assert.Contains("already exists", second.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(marker), "the first cell must be untouched");
        output.WriteLine(second.Message);

        // And the dialog's own suggestion moves on rather than repeating the refusal.
        var availability = MatchFlattenService.Availability(vm, match);
        Assert.True(availability.CanRun);
        Assert.Equal("MN1_match_2", availability.DefaultName);
    }

    /// <summary>
    /// <b>A write failure part-way leaves no partial cell folder.</b> Half a cell is worse than
    /// none: the workspace scanner lists it and a user places it. The failure is injected AFTER the
    /// schematic is on disk, because a failure on the first step would only be testing the argument
    /// check — see <c>MatchFlatten.Write</c>'s own note on the seam.
    /// </summary>
    [Fact]
    public void AFailedWrite_LeavesNothingBehind()
    {
        var design = Golden();
        var rebuild = MatchRebuild.Rebuild(design);
        var schematic = MatchFlatten.BuildSchematic(rebuild, design, "MN1");

        Directory.CreateDirectory(_root);
        string cellDir = Path.Combine(_root, "half");

        var thrown = Record.Exception(() => MatchFlatten.Write(
            _root, "half", schematic, design,
            faultAfterSchematic: () => throw new IOException("the disk filled up")));

        Assert.IsType<IOException>(thrown);
        Assert.True(File.Exists(Path.Combine(_root, "half", CellFolder.SchematicSubFolder)) is false);
        Assert.False(Directory.Exists(cellDir), "a failed write must leave no cell folder behind");
        output.WriteLine($"refused with: {thrown!.Message}; '{cellDir}' does not exist");

        // …and the name is free again, so retrying is the obvious next action.
        var second = MatchFlatten.Write(_root, "half", schematic, design);
        Assert.True(Directory.Exists(second.CellDir));
        Assert.Equal(3, second.Files.Count);
    }

    // ── availability ──────────────────────────────────────────────────────────

    /// <summary>A scratch schematic cannot be flattened into, and the reason says which step to take.</summary>
    [Fact]
    public void AnUnsavedSchematic_IsRefusedByName()
    {
        var model = new SchematicEditModel();   // no SchematicDirectory — a scratch document
        var match = new EditableComponent { InstanceName = "MN1", Symbol = SymbolKind.Match };
        match.Parameters.Add(new EditableParameter
        {
            Name = MatchEmbedding.DesignParameter, Expression = MatchEmbedding.Encode(Golden()),
        });
        model.Components.Add(match);
        var vm = new SchematicViewModel(model) { WorkspaceRootProvider = () => _root };

        var availability = MatchFlattenService.Availability(vm, match);
        Assert.False(availability.CanRun);
        Assert.Contains("Save this schematic", availability.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The context menu shows <c>Flatten to Cell…</c> for a <c>Match</c> and for nothing else — the
    /// view reads exactly this, so the rule is testable without a window.
    /// </summary>
    [Fact]
    public void FlattenIsOffered_ForAMatchAndForNothingElse()
    {
        var (vm, match) = Workspace(Golden());
        var resistor = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor };
        vm.EditModel.Components.Add(resistor);

        Assert.True(MatchFlattenService.Availability(vm, match).CanRun);

        var forResistor = MatchFlattenService.Availability(vm, resistor);
        Assert.False(forResistor.CanRun);
        Assert.Contains("acts on a Match component", forResistor.Reason, StringComparison.Ordinal);
        Assert.False(MatchFlattenService.Availability(vm, null).CanRun);
    }

    /// <summary>
    /// The Designer's footer button is live and does the same thing — MN-3 wired it and left it
    /// disabled with a tooltip naming this brief; both must have changed.
    /// </summary>
    [Fact]
    public void TheDesignersFooterButton_IsLive_AndFlattensThroughTheSameService()
    {
        var (vm, match) = Workspace(Golden());
        var designer = new MatchDesignerViewModel();
        designer.SetTarget(vm, match);

        Assert.True(designer.CanFlatten);
        Assert.DoesNotContain("MN-5", designer.FlattenTooltip, StringComparison.Ordinal);
        Assert.Equal("MN1_match", designer.FlattenAvailability.DefaultName);

        var run = designer.Flatten(designer.FlattenAvailability.ParentDir!, "from_the_designer",
                                   replaceInPlace: true);
        Assert.True(run.Ok, run.Message);
        Assert.True(File.Exists(PrimarySchematicPath(run.CellDir!)));
        Assert.True(File.Exists(PrimarySymbolPath(run.CellDir!)));
        Assert.True(File.Exists(Path.Combine(run.CellDir!, CellFolder.CcellFileName)));
        designer.Dispose();
    }

    /// <summary>
    /// The design blob is on the CELL, never on a declared parameter — a declared parameter is
    /// seeded onto every placement as an override, and an override is evaluated as an expression at
    /// elaboration, which a base64 blob is not. Every placed instance of a flattened cell would
    /// otherwise refuse to elaborate.
    /// </summary>
    [Fact]
    public void TheDesignBlob_IsCellMetadata_NotADeclaredParameter()
    {
        var (vm, match) = Workspace(Golden());
        var run = MatchFlattenService.Run(vm, match, _root, "MN1_match", replaceInPlace: true);
        Assert.True(run.Ok, run.Message);

        var ccell = CellPersistence.LoadFromFile(Path.Combine(run.CellDir!, CellFolder.CcellFileName));
        Assert.False(string.IsNullOrEmpty(ccell.MatchDesign));
        Assert.Empty(ccell.Parameters);
        Assert.Equal(2, ccell.NumPorts);

        // The replacement instance carries no expression-shaped landmine either, and elaborates.
        Assert.Empty(run.Replacement!.Parameters);
        AddTestBenchAround(vm.EditModel);
        var extracted = NetExtractor.Extract(vm.EditModel, "tb", new DiskResolver());
        var netlist = new Elaborator(extracted.Library).Elaborate(extracted.TestBench);
        Assert.NotEmpty(netlist.Components);
    }

    // ── paths ─────────────────────────────────────────────────────────────────

    private static string PrimarySchematicPath(string cellDir)
    {
        var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
        Assert.NotNull(primary.ResolvedName);
        return Path.Combine(cellDir, CellFolder.SchematicSubFolder, primary.ResolvedName!);
    }

    private static string PrimarySymbolPath(string cellDir)
    {
        var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Symbol);
        Assert.NotNull(primary.ResolvedName);
        return Path.Combine(cellDir, CellFolder.SymbolSubFolder, primary.ResolvedName!);
    }
}

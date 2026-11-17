// brief-paa-9 — the Phased Array tool window's gates 2-6 (gate 1 is UiSnapshot PNGs, looked at; R-paa9-7's frame time is recorded in
// src/Ui/RESOLVED.md, never asserted). Counters, not timing. Fixtures: testdata/phased-array.

using System.Runtime.CompilerServices;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine.AntennaArray;
using CircuitRF.Ui.PhasedArray;
using CircuitRF.Ui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.PhasedArray;

[Collection(Sar.FieldCosimulationRunsCollection.Name)]
public sealed class PhasedArrayToolTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-paa9-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string Record(string name, [CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "phased-array", "results", name + ".cemr"));

    private static string E5 => $"Record=\"{Record("e5")}\" RecordRows=5 RecordCols=5 RecordDx=25.86 mm RecordDy=25.86 mm dx=25.86 mm dy=25.86 mm";

    private static string Bench(int side, string channel = "", string extra = "")
        => (channel.Length > 0 ? "define chan ( in out )\nAmp:A1 in 0 mid 0 Gain=20 IP3=200\nAtten:T1 mid 0 out 0 Loss=3\nC:C1 out 0 C=0.3pF\nend\n" : "") +
           "Port:P1 f 0 Num=1 Z=50 Ohm\ntheta0 = 0 deg\nphi0 = 0 deg\nf0 = 5.9 GHz\n" +
           $"PhasedArray:PA1 f {E5} Mode=feed Rows={side} Cols={side} Phase=steer(theta0,phi0,f0) Amp=cosq(1) {channel} {extra}\n" +
           "analysis F1 type=fields block=PA1 freq=5.9 freqUnit=GHz drive=1\n";

    /// <summary>The bench drawn as a schematic (what netlist --to-schematic draws), in a session with its own undo stack.</summary>
    private SchematicViewModel Drawn(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl, "bench", _dir);
        var drawn = NetlistSchematic.Build(lib, tb, _dir);
        Assert.Empty(drawn.Refusals);
        drawn.Schematic!.SchematicDirectory = _dir;
        return new SchematicViewModel(drawn.Schematic);
    }

    private Func<SchematicViewModel, PreparedCircuit?> FromSchematic
        => vm => PreparedCircuit.FromText(SchematicCircuit.CnlTextOf(vm.EditModel, "bench"), _dir, _dir);

    private static PhasedArrayToolViewModel Open(SchematicViewModel vm, Func<SchematicViewModel, PreparedCircuit?> prepare)
    {
        var instance = vm.EditModel.Components.Single(c => c.Symbol == SymbolKind.PhasedArray);
        var tool = new PhasedArrayToolViewModel(vm, instance, prepare, new ImmediatePhasedArrayHost());
        Assert.True(tool.Frame is not null, tool.Status);
        return tool;
    }

    private static double VarDegrees(SchematicViewModel vm, string name)
        => SchematicNominal.Of(vm.EditModel.Components).Real(name, null)!.Value * 180 / Math.PI;

    /// <summary>Gate 2 — a beam drag from boresight to (30°, 0°) writes theta0 = 30 to the VAR the steer() names, as one undo step.</summary>
    [Fact]
    public void Gate2_ABeamDrag_WritesTheSteeringVar_AsOneUndoStep()
    {
        var vm = Drawn(Bench(8));
        using var tool = Open(vm, FromSchematic);
        Assert.True(tool.Steering.Draggable, tool.Steering.Refusal);
        Assert.Equal("theta0", tool.Steering.Theta!.Var);
        int undo = vm.UndoRedo.Entries.Count();

        Assert.True(tool.BeginDrag());
        foreach (double t in new[] { 5.0, 12, 21, 30 }) tool.DragToAngles(t, 0);
        Assert.Equal(0, VarDegrees(vm, "theta0"), 9);   // nothing written while the drag moves
        tool.EndDrag();

        Assert.Equal(30, VarDegrees(vm, "theta0"), 9);
        Assert.Equal(0, VarDegrees(vm, "phi0"), 9);
        Assert.Equal(undo + 1, vm.UndoRedo.Entries.Count());
        Assert.Equal(30, tool.Frame!.Readouts.PeakThetaDeg, 0);
        vm.UndoRedo.Undo();
        Assert.Equal(0, VarDegrees(vm, "theta0"), 9);
    }

    /// <summary>brief-pch-9 — a drag on literal angles rewrites them glued (30deg), so the field still parses and the beam moves.</summary>
    [Fact]
    public void ABeamDrag_OnLiterals_RewritesThemSoTheFieldStillParses()
    {
        var vm = Drawn(Bench(8).Replace("Phase=steer(theta0,phi0,f0)", "Phase=steer(10deg,0deg,f0)"));
        using var tool = Open(vm, FromSchematic);
        Assert.True(tool.BeginDrag(), tool.Steering.Refusal);
        tool.DragToAngles(30, 0);
        tool.EndDrag();
        Assert.Equal("steer(30deg,0deg,f0)", tool.Instance.Parameters.Single(p => p.Name == "Phase").Expression);
        Assert.True(tool.Frame is not null, tool.Status);
        Assert.Equal(30, tool.Frame!.Readouts.PeakThetaDeg, 0);
    }

    /// <summary>Gate 2's other half — weights that are not a steer() show the marker and do not drag, saying why.</summary>
    [Fact]
    public void WeightsThatAreNotASteer_DoNotDrag()
    {
        var vm = Drawn(Bench(4).Replace("Phase=steer(theta0,phi0,f0)", "Phase=steer(theta0+0.1,phi0,f0)"));
        using var tool = Open(vm, FromSchematic);
        Assert.False(tool.Steering.Draggable);
        Assert.Contains("θ0", tool.Steering.Refusal);
        Assert.False(tool.BeginDrag());
    }

    /// <summary>Gate 3 — an element clicked off appears in Off, the schematic's symbol draws it hollow, and undo restores both.</summary>
    [Fact]
    public void Gate3_AnElementClickedOff_IsInOff_DrawnHollow_AndUndoRestoresIt()
    {
        var vm = Drawn(Bench(8));
        using var tool = Open(vm, FromSchematic);
        var pa = tool.Instance;
        PhasedArrayGlyph Glyph() => PhasedArrayGlyph.Of(pa.Parameters, SchematicNominal.Of(vm.EditModel.Components));

        tool.ToggleElement(10);
        Assert.Equal("10", pa.Parameters.Single(p => p.Name == "Off").Expression);
        Assert.Contains(10, Glyph().OffSet);
        Assert.False(tool.Frame!.State.Lattice.IsOn(9));   // the window followed the schematic

        vm.UndoRedo.Undo();
        Assert.DoesNotContain(10, Glyph().OffSet);
        Assert.True(tool.Frame!.State.Lattice.IsOn(9));
    }

    /// <summary>Q switches the selected elements off, and on again once every one of them is off — each one undo step, as Q disables a
    /// component in the schematic; with nothing selected it switches the element under the pointer.</summary>
    [Fact]
    public void Q_TogglesTheSelection_ElseTheHoveredElement_OneUndoStepEach()
    {
        var vm = Drawn(Bench(4));
        using var tool = Open(vm, FromSchematic);
        int undo = vm.UndoRedo.Entries.Count();
        tool.Selected.Add(2);
        tool.Selected.Add(5);

        Assert.True(tool.ToggleSelectedOrHovered());
        Assert.Equal([2, 5], tool.OffSet());
        Assert.True(tool.ToggleSelectedOrHovered());
        Assert.Empty(tool.OffSet());
        Assert.Equal(undo + 2, vm.UndoRedo.Entries.Count());

        Assert.True(tool.ClearSelection());
        Assert.False(tool.ToggleSelectedOrHovered());   // nothing selected, nothing hovered
        tool.HoverElement(7);
        Assert.True(tool.ToggleSelectedOrHovered());
        Assert.Equal([7], tool.OffSet());
    }

    /// <summary>The window is bound before the first frame arrives from a worker, so "Colour by" must already list what it shows: a
    /// selection with no item to match opened the picker blank.</summary>
    [Fact]
    public void ColourBy_ListsItsSelection_BeforeTheFirstFrame()
    {
        var vm = Drawn(Bench(4));
        var instance = vm.EditModel.Components.Single(c => c.Symbol == SymbolKind.PhasedArray);
        using var tool = new PhasedArrayToolViewModel(vm, instance, FromSchematic, new NeverRunsHost());
        Assert.Null(tool.Frame);
        Assert.Contains(tool.Quantities, c => c.Value == tool.Quantity);
    }

    /// <summary>A host that never gets round to the work: the moment between the window being bound and the first frame.</summary>
    private sealed class NeverRunsHost : IPhasedArrayHost
    {
        public void Post(Action action) { }
        public void Run(Action work) { }
    }

    /// <summary>Gate 4 — File ▸ New ▸ Phased Array creates a cell that check passes and that simulates.</summary>
    [Fact]
    public void Gate4_ANewPhasedArrayCell_Checks_AndSimulates()
    {
        string ws = Path.Combine(_dir, "ws");
        CircuitRF.Design.Workspace.WorkspaceCreate.Create(_dir, "ws", technologyId: null);
        var cell = CellCreate.PhasedArray(ws, "Array", Record("e1"));
        Assert.NotNull(cell.SchematicPath);

        Assert.Equal(0, CircuitRF.Cli.CliEntry.Run(["check", cell.SchematicPath!]));
        var circuit = PreparedCircuit.FromSchematic(cell.SchematicPath!);
        Assert.Null(circuit.ReadError);
        var run = CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest { Fields = FieldsOutput.Local });
        Assert.True(run.Status == RunStatus.Success && run.Errors.Count == 0, run.StatusMessage);
        var state = run.ArrayStates["F1"];
        Assert.Equal(64, state.Lattice.Count);
        Assert.Equal(0.5 * Engine.Mom.EmConstants.C0 / 5.9e9, state.Lattice.Dx, 9);
    }

    /// <summary>Gate 5 — the scan map is cancelled by an edit (a counter), never starts on a bench off the fast path, and N directions at 4096
    /// elements make one elaboration and one closure factorisation.</summary>
    [Fact]
    public void Gate5_TheScanMap_ReweighsOneElaboration_IsCancelledByAnEdit_AndRefusedOffTheFastPath()
    {
        PhasedArrayFastPath.Clear();
        var circuit = PreparedCircuit.FromText(Bench(64, "Channel=chan"), _dir, _dir);
        var session = new PhasedArraySession(circuit, "PA1");
        var frame = session.Evaluate(new Dictionary<string, string>(), interactive: false, (0, 0)).Frame!;
        long factors = PhasedArrayFastPath.ClosureFactorisations;
        var steering = new PhasedArraySteering("Phase", "steer", new SteerArgument("theta0", 0, 0, "theta0"), new SteerArgument("phi0", 0, 0, "phi0"), null);
        var (points, refusal) = session.ScanMap(frame, (t, p) => steering.Tunables("PA1", [], t, p), 20, 10, 90, null, CancellationToken.None);
        Assert.Null(refusal);
        output.WriteLine($"{points!.Count} directions: {circuit.Elaborations} elaboration, {circuit.Reweighs} re-weighs, " +
                         $"{PhasedArrayFastPath.ClosureFactorisations - factors} factorisations after the first frame");
        Assert.Equal(9, points.Count);
        Assert.Equal(1, circuit.Elaborations);
        Assert.Equal(9, circuit.Reweighs);
        Assert.Equal(0, PhasedArrayFastPath.ClosureFactorisations - factors);

        // an edit cancels it: the token an edit cancels stops the map before its next point
        long cancelled = PhasedArraySession.ScanMapsCancelled;
        using var cts = new CancellationTokenSource();
        var (stopped, why) = session.ScanMap(frame, (t, p) => steering.Tunables("PA1", [], t, p), 20, 10, 90,
                                             _ => cts.Cancel(), cts.Token);
        Assert.Null(stopped);
        Assert.Equal("Cancelled.", why);
        Assert.Equal(cancelled + 1, PhasedArraySession.ScanMapsCancelled);

        // a channel that reads the element is off the fast path: the map never starts
        var other = new PhasedArraySession(PreparedCircuit.FromText(Bench(4, "Channel=chan").Replace("Loss=3", "Loss=n"), _dir, _dir), "PA1");
        var off = other.Evaluate(new Dictionary<string, string>(), interactive: false, (0, 0)).Frame!;
        Assert.False(off.State.Path!.Fast);
        long started = PhasedArraySession.ScanMapsStarted;
        var (none, reason) = other.ScanMap(off, (t, p) => steering.Tunables("PA1", [], t, p), 20, 10, 90, null, CancellationToken.None);
        Assert.Null(none);
        Assert.StartsWith("No scan map", reason);
        Assert.Equal(started, PhasedArraySession.ScanMapsStarted);
    }

    /// <summary>Gate 5's first half through the window's own path — an edit in the schematic cancels the running map.</summary>
    [Fact]
    public void AnEditCancelsTheRunningScanMap()
    {
        var vm = Drawn(Bench(4));
        // the host runs the map's work only when told to, so the edit lands while it is "running"
        var host = new DeferredRunHost();
        var instance = vm.EditModel.Components.Single(c => c.Symbol == SymbolKind.PhasedArray);
        using var tool = new PhasedArrayToolViewModel(vm, instance, FromSchematic, host);
        host.Drain();
        Assert.NotNull(tool.Frame);
        long cancelled = PhasedArraySession.ScanMapsCancelled;
        tool.StartScan();
        tool.ToggleElement(3);   // the edit
        host.Drain();
        Assert.Equal(cancelled + 1, PhasedArraySession.ScanMapsCancelled);
    }

    /// <summary>Gate 6 — a 20-step beam drag at 4096 elements with an identical channel: no elaboration and 20 re-weighs, no closure
    /// factorisation and 20 back-substitutions, and no phase table built for the cuts, the map or the overview. The schematic is the bench
    /// drawn without its channel's definition (a drawing defines no cells); the circuit the tool evaluates is the bench's netlist, which
    /// has it.</summary>
    [Fact]
    public void Gate6_ATwentyStepDrag_CostsWhatPaa8AndPaa8bSay()
    {
        PhasedArrayFastPath.Clear();
        var vm = Drawn(Bench(64));
        PreparedCircuit? circuit = null;
        using var tool = Open(vm, _ => circuit = PreparedCircuit.FromText(Bench(64, "Channel=chan"), _dir, _dir));
        Assert.True(tool.Frame!.State.Path!.Fast, tool.Frame.State.Path.Sentence);
        long elaborations = circuit!.Elaborations, reweighs = circuit.Reweighs;
        long factors = PhasedArrayFastPath.ClosureFactorisations, backs = PhasedArrayFastPath.BackSubstitutions, tables = ArrayPattern.TablesBuilt;
        var times = new List<double>();

        Assert.True(tool.BeginDrag());
        for (int i = 1; i <= 20; i++)
        {
            tool.DragToAngles(1.5 * i, 0);
            times.Add(tool.Frame!.EvaluationMs + tool.Frame.FormingMs);
            output.WriteLine($"step {i}: evaluation {tool.Frame.EvaluationMs:0} ms, forming {tool.Frame.FormingMs:0} ms ({tool.Frame.Breakdown})");
        }
        output.WriteLine($"20 steps: {circuit.Elaborations - elaborations} elaborations, {circuit.Reweighs - reweighs} re-weighs, " +
                         $"{PhasedArrayFastPath.ClosureFactorisations - factors} factorisations, {PhasedArrayFastPath.BackSubstitutions - backs} back-substitutions, " +
                         $"{ArrayPattern.TablesBuilt - tables} tables; frame median {times.Order().ElementAt(10):0} ms");
        Assert.Equal(0, circuit.Elaborations - elaborations);
        Assert.Equal(20, circuit.Reweighs - reweighs);
        Assert.Equal(0, PhasedArrayFastPath.ClosureFactorisations - factors);
        Assert.Equal(20, PhasedArrayFastPath.BackSubstitutions - backs);
        Assert.Equal(0, ArrayPattern.TablesBuilt - tables);
        Assert.Equal(30, tool.Frame!.Readouts.PeakThetaDeg, 0);
    }

    /// <summary>R-paa9-7 — off the fast path (a channel that reads the element) a drag's step is PAA-8's estimate and the frame after the
    /// release is solved.</summary>
    [Fact]
    public void OffTheFastPath_ADragIsEstimated_AndTheReleaseSolves()
    {
        var vm = Drawn(Bench(4));
        string cnl = Bench(4, "Channel=chan").Replace("Loss=3", "Loss=n");
        using var tool = Open(vm, _ => PreparedCircuit.FromText(cnl, _dir, _dir));
        Assert.False(tool.Frame!.State.Path!.Fast);
        Assert.False(tool.Frame.State.Estimated);
        Assert.True(tool.BeginDrag());
        tool.DragToAngles(20, 0);
        Assert.True(tool.Frame!.State.Estimated);
        Assert.EndsWith("· estimated", tool.Status);
        tool.EndDrag();
        Assert.False(tool.Frame!.State.Estimated);
    }

    /// <summary>Runs the UI-thread work at once and keeps the background work until <see cref="Drain"/>.</summary>
    private sealed class DeferredRunHost : IPhasedArrayHost
    {
        private readonly Queue<Action> _work = new();
        public void Post(Action action) => action();
        public void Run(Action work) => _work.Enqueue(work);
        public void Drain() { while (_work.TryDequeue(out var w)) w(); }
    }
}

// brief-pch-9 — picking the beam through a Propagation Channel between two Feed-mode Phased Antenna Arrays steered by steer(). The design
// layer is loaded (the arrays read their patterns through it), so these live beside ChannelArrayEndTests. Fixture: testdata/phased-array e1
// (one isolated element, 5.9 GHz); the rays are written per test (Multipath=rays, RayFrame=ends: each direction in its end's own frame).

using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Arrays;
using CircuitRF.Core.Elaboration;
using CircuitRF.Design.Circuit;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine.AntennaArray;
using CircuitRF.Ui.PhasedArray;
using CircuitRF.Ui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.PhasedArray;

[Collection(Sar.FieldCosimulationRunsCollection.Name)]
public sealed class BeamSelectionTests(ITestOutputHelper output) : IDisposable
{
    private const double F = 5.9e9;
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-pch9-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string Record(string name, [CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "phased-array", "results", name + ".cemr"));

    private static readonly string E1 = $"Record=\"{Record("e1")}\" dx=25.86 mm dy=25.86 mm";

    private const string Header = "delay (ns), power (dB), theta_d (deg), phi_d (deg), theta_a (deg), phi_a (deg)";

    /// <summary>Two arrays of <paramref name="size"/>², each steered by its own VARs, linked by CH1 with <paramref name="channel"/>.</summary>
    private string Bench(string? channel, int size = 8, string? pa1Phase = null) => $"""
        f0 = 5.9 GHz
        t1 = 0 deg
        p1 = 0 deg
        t2 = 0 deg
        p2 = 0 deg
        P1Tone:S1 f1 0 Num=1 Pavl=10 Z=50 Freq=5.9 GHz
        PhasedArray:PA1 f1 {E1} Mode=feed Rows={size} Cols={size} {pa1Phase ?? "Phase=steer(t1,p1,f0)"}
        PhasedArray:PA2 f2 {E1} Mode=feed Rows={size} Cols={size} Phase=steer(t2,p2,f0)
        {(channel is null ? "" : "PropagationChannel:CH1 Tx=PA1 Rx=PA2 Distance=100 TxHeight=1.5 RxHeight=1.5 f0=5.9 GHz " + channel)}
        R:RL f2 0 R=50 Ohm
        """;

    private string Rays(string name, params string[] rows)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllLines(path, [Header, .. rows]);
        return $"Multipath=rays File=\"{path}\" RayFrame=ends";
    }

    private ElaboratedNetlist Elaborate(string text)
    {
        var circuit = PreparedCircuit.FromText(text, _dir, _dir);
        return new Elaborator(circuit.Lib!).Elaborate(circuit.Tb!);
    }

    private static PropagationChannelModel Channel(ElaboratedNetlist nl) => (PropagationChannelModel)nl.Components.Single(c => c.InstancePath == "CH1").Model;

    private static PhasedArrayModel Array(ElaboratedNetlist nl, string name) => (PhasedArrayModel)nl.Components.Single(c => c.InstancePath == name).Model;

    private static BeamTable Select(ElaboratedNetlist nl)
    {
        BeamSelection.Apply(nl, F, setCarrier: true);
        return Assert.IsType<BeamTable>(Channel(nl).BeamTable);
    }

    private static (double U, double V) Uv(double thetaDeg, double phiDeg)
    {
        double t = thetaDeg * Math.PI / 180, p = phiDeg * Math.PI / 180;
        return (Math.Sin(t) * Math.Cos(p), Math.Sin(t) * Math.Sin(p));
    }

    private static int Nearest(IReadOnlyList<BeamEntry> beams, double thetaDeg, double phiDeg)
    {
        var (u, v) = Uv(thetaDeg, phiDeg);
        return Enumerable.Range(0, beams.Count).MinBy(i => Math.Pow(beams[i].U - u, 2) + Math.Pow(beams[i].V - v, 2));
    }

    /// <summary>Gate 1 — a single-ray channel picks, at each end, the codebook entry nearest the ray.</summary>
    [Fact]
    public void Gate1_OneRay_PicksTheEntryNearestTheRayAtBothEnds()
    {
        var nl = Elaborate(Bench(Rays("one.csv", "0, 0, 23, 33, 37, -58")));
        var t = Select(nl);
        int tx = Nearest(t.TxBeams, 23, 33), rx = Nearest(t.RxBeams, 37, -58);
        output.WriteLine($"{t.TxBeams.Count} × {t.RxBeams.Count} beams; chosen {t.Label(t.Chosen)}; nearest tx {t.TxBeams[tx].Label}, rx {t.RxBeams[rx].Label}");
        Assert.Equal(tx, t.Chosen.Tx);
        Assert.Equal(rx, t.Chosen.Rx);
        Assert.True(t.CodebookLossDb >= -1e-9, $"the bound {t.BoundDb} dB is below the chosen pair's {t.Chosen.MetricDb} dB");
    }

    /// <summary>Gate 2 — two clusters: rank 1 is the stronger cluster, rank 2 the other one, not a beam beside rank 1; BeamSelect=rank
    /// BeamRank=2 takes it.</summary>
    [Fact]
    public void Gate2_TwoClusters_RankTwoIsTheOtherCluster()
    {
        string rays = Rays("two.csv",
            "0, 0, 12, 20, 18, 100", "10, -3, 13, 21, 17, 101", "20, -3, 11, 19, 19, 99",
            "100, -5, 42, 200, 35, -80", "110, -8, 43, 201, 34, -79", "120, -8, 41, 199, 36, -81");
        var t = Select(Elaborate(Bench(rays)));
        var r1 = t.Pairs.Single(p => p.Rank == 1);
        var r2 = t.Pairs.Single(p => p.Rank == 2);
        output.WriteLine($"rank 1 {t.Label(r1)} {r1.MetricDb:F2} dB; rank 2 {t.Label(r2)} {r2.MetricDb:F2} dB; next non-distinct " +
                         $"{t.Label(t.Pairs.First(p => p.Rank is null))}");
        Assert.Equal((Nearest(t.TxBeams, 12, 20), Nearest(t.RxBeams, 18, 100)), (r1.Tx, r1.Rx));
        Assert.Equal((Nearest(t.TxBeams, 42, 200), Nearest(t.RxBeams, 35, -80)), (r2.Tx, r2.Rx));
        // the second-strongest pair overall is a neighbour of rank 1, which is why "distinct" is needed
        Assert.Null(t.Pairs[1].Rank);

        var ranked = Select(Elaborate(Bench(rays + " BeamSelect=rank BeamRank=2")));
        Assert.Equal((r2.Tx, r2.Rx), (ranked.Chosen.Tx, ranked.Chosen.Rx));
    }

    /// <summary>Gate 3 — index with a direction uses that direction (the weights of a bench whose steer VARs say it); manual leaves every
    /// weight bit-identical to a bench with no channel.</summary>
    [Fact]
    public void Gate3_IndexUsesTheDirection_ManualLeavesTheWeights()
    {
        string rays = Rays("one.csv", "0, 0, 23, 33, 37, -58");
        var nl = Elaborate(Bench(rays + " BeamSelect=index TxBeam=\"(25, 45)\" RxBeam=\"(10, -30)\""));
        var t = Select(nl);
        Assert.Equal(25, t.TxBeams[t.Chosen.Tx].ThetaDeg);
        var want = Elaborate(Bench("").Replace("t1 = 0 deg", "t1 = 25 deg").Replace("p1 = 0 deg", "p1 = 45 deg")
                                      .Replace("t2 = 0 deg", "t2 = 10 deg").Replace("p2 = 0 deg", "p2 = -30 deg"));
        foreach (string pa in new[] { "PA1", "PA2" })
        {
            var got = Array(nl, pa).Feed!;
            var exp = Array(want, pa).Feed!;
            double worst = Enumerable.Range(0, got.Elements).Max(e => (got.Quantised(e, F) - exp.Quantised(e, F)).Magnitude);
            output.WriteLine($"{pa}: worst weight difference from the steered bench {worst:E2}");
            Assert.True(worst < 1e-14, $"{pa} differs from the steered bench by {worst}");
        }

        var manual = Elaborate(Bench(rays + " BeamSelect=manual"));
        BeamSelection.Apply(manual, F, setCarrier: true);
        Assert.Null(Channel(manual).BeamTable);
        var none = Elaborate(Bench(null));
        foreach (string pa in new[] { "PA1", "PA2" })
            for (int e = 0; e < Array(none, pa).Feed!.Elements; e++)
                Assert.Equal(Array(none, pa).Feed!.Quantised(e, F), Array(manual, pa).Feed!.Quantised(e, F));
    }

    /// <summary>Gate 4 — 64 × 64 pairs scored and the chosen one run through harmonic balance: one elaboration; each end's 64 entries formed in
    /// closed form, with two exact weighings (the one the closed form starts from, and the chosen entry, which is what is applied); 4096 pairs
    /// and one weight change per array.</summary>
    [Fact]
    public void Gate4_ScoringAndRunning_ElaboratesOnce()
    {
        string book = Path.Combine(_dir, "book.csv");
        var rows = new List<string> { "theta, phi" };
        for (int i = 0; i < 64; i++) rows.Add(string.Create(CultureInfo.InvariantCulture, $"{5 + 40.0 * (i / 8) / 7:0.###}, {i % 8 * 45}"));
        File.WriteAllLines(book, rows);
        string text = Bench(Rays("one.csv", "0, 0, 23, 33, 37, -58") + $" Codebook=\"{book}\"") +
                      "\nanalysis HB1 type=hb Tone=\"5.9\" ToneUnit=GHz MaxHarm=1 Tol=1e-13\n";
        BeamSelection.ForgetKept();
        long elab = Elaborator.Elaborations, pairs = BeamSelection.PairsScored, weighed = ArraySteering.Weighings, taken = PhasedArrayFeedModel.WeightsTaken;
        long fast = ArraySteering.FastWeighings;
        var run = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(text, _dir, _dir), new CircuitEvaluationRequest { Expressions = ["mag(HB1.V(\"f2\",1))"] });
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage + " " + string.Join(" ", run.Warnings));
        output.WriteLine($"elaborations {Elaborator.Elaborations - elab}, weighings {ArraySteering.Weighings - weighed}, pairs {BeamSelection.PairsScored - pairs}, " +
                         $"weights taken {PhasedArrayFeedModel.WeightsTaken - taken}; |V(f2)| {run.Expressions[0].Value}");
        Assert.Equal(1, Elaborator.Elaborations - elab);
        Assert.Equal(2 * 2, ArraySteering.Weighings - weighed);
        Assert.Equal(2 * 64, ArraySteering.FastWeighings - fast);
        Assert.Equal(64 * 64, BeamSelection.PairsScored - pairs);
        Assert.Equal(2, PhasedArrayFeedModel.WeightsTaken - taken);
        // the beam table in the run's result (R-pch9-6)
        var data = run.Results.Single(r => r.Name == "HB1").Data;
        Assert.Equal(64 * 64, data["link:CH1:Beam_dB"].BufferLength);
        Assert.Equal(64, data["link:CH1:TxCodebook_deg"].Axes[0].Values.Length);
        Assert.Equal(0, data["link:CH1:BeamChosen"].RealValues[0]);
        Assert.True(data["link:CH1:BeamCodebookLoss_dB"].RealValues[0] >= -1e-9);
    }

    /// <summary>Gate 5 — BeamSelect=auto on an array whose weights are explicit per-element phases refuses, naming manual.</summary>
    [Fact]
    public void Gate5_AutoOnExplicitPhases_RefusesNamingManual()
    {
        var nl = Elaborate(Bench(Rays("one.csv", "0, 0, 23, 33, 37, -58") + " BeamSelect=auto", pa1Phase: "Phase[1]=10 Phase[2]=30 Phase[3]=50"));
        var ex = Assert.Throws<ParameterRefusalException>(() => BeamSelection.Apply(nl, F, setCarrier: true));
        output.WriteLine(ex.Message);
        Assert.Contains("BeamSelect=manual", ex.Message);
        Assert.Contains("PA1", ex.Message);
    }

    /// <summary>Gate 6 — on three CDL-C drops between 16 × 16 arrays the alternating search finds the exhaustive search's best pair, and
    /// scores fewer pairs.</summary>
    [Fact]
    public void Gate6_Alternating_FindsTheExhaustiveBest_OnThreeCdlDrops()
    {
        foreach (int drop in new[] { 1, 2, 3 })
        {
            string cdl = $"Multipath=cdl Profile=CDL-C DelaySpread=100e-9 Seed=7 Drop={drop}";
            var ex = Select(Elaborate(Bench(cdl + " BeamSearch=exhaustive", 16)));
            var alt = Select(Elaborate(Bench(cdl + " BeamSearch=alternating", 16)));
            output.WriteLine($"drop {drop}: exhaustive {ex.Label(ex.Chosen)} {ex.Chosen.MetricDb:F3} dB of {ex.PairsScored}; alternating " +
                             $"{alt.Label(alt.Chosen)} {alt.Chosen.MetricDb:F3} dB of {alt.PairsScored}; bound {ex.BoundDb:F3} dB");
            Assert.Equal((ex.Chosen.Tx, ex.Chosen.Rx), (alt.Chosen.Tx, alt.Chosen.Rx));
            Assert.True(alt.PairsScored < ex.PairsScored / 10, $"alternating scored {alt.PairsScored} of {ex.PairsScored}");
        }
    }

    /// <summary>
    /// Gate 7 — the Phased Array tool on the transmit array of a two-cluster channel: a click on rank 2 writes BeamSelect=index with its
    /// TxBeam and RxBeam as one undo step, and the tool then shows rank 2 chosen; undo restores the channel; Apply writes each array's steer
    /// VARs to the chosen directions and sets BeamSelect=manual, as one undo step.
    /// </summary>
    [Fact]
    public void Gate7_TheTool_SelectsRankTwo_AsOneUndoStep_AndApplyWritesTheSteerVars()
    {
        string rays = Rays("two.csv",
            "0, 0, 12, 20, 18, 100", "10, -3, 13, 21, 17, 101", "20, -3, 11, 19, 19, 99",
            "100, -5, 42, 200, 35, -80", "110, -8, 43, 201, 34, -79", "120, -8, 41, 199, 36, -81");
        string cnl = Bench(rays).Replace("P1Tone:S1 f1 0 Num=1 Pavl=10 Z=50 Freq=5.9 GHz", "Port:P1 f1 0 Num=1 Z=50 Ohm") +
                     "\nanalysis F1 type=fields block=PA1 freq=5.9 freqUnit=GHz drive=1\n";
        var (lib, tb) = new CnlReader().Read(cnl, "bench", _dir);
        var drawn = NetlistSchematic.Build(lib, tb, _dir);
        Assert.Empty(drawn.Refusals);
        drawn.Schematic!.SchematicDirectory = _dir;
        var vm = new SchematicViewModel(drawn.Schematic);
        PreparedCircuit? Prepare(SchematicViewModel s) => PreparedCircuit.FromText(SchematicCircuit.CnlTextOf(s.EditModel, "bench"), _dir, _dir);
        var pa1 = vm.EditModel.Components.Single(c => c.InstanceName == "PA1");
        var ch = vm.EditModel.Components.Single(c => c.InstanceName == "CH1");
        using var tool = new PhasedArrayToolViewModel(vm, pa1, Prepare, new ImmediatePhasedArrayHost());
        Assert.True(tool.Frame is not null, tool.Status);
        Assert.True(tool.HasChannel);
        Assert.True(tool.Channel?.Table is not null, tool.ChannelStatus);
        Assert.True(tool.MapRays().Count == 6, $"{tool.MapRays().Count} rays on the map");
        string? Key(string name) => ch.Parameters.FirstOrDefault(p => p.Name == name)?.Expression;
        double Var(string name) => SchematicNominal.Of(vm.EditModel.Components).Real(name, null)!.Value * 180 / Math.PI;

        var r2 = tool.BeamRows.Single(r => r.Rank == 2);
        int undo = vm.UndoRedo.Entries.Count();
        tool.SelectPair(r2);
        output.WriteLine($"rank 2 {r2.Label}: BeamSelect={Key("BeamSelect")} TxBeam={Key("TxBeam")} RxBeam={Key("RxBeam")}; {tool.ChannelStatus}");
        Assert.Equal("index", Key("BeamSelect"));
        Assert.Equal((r2.Tx + 1).ToString(CultureInfo.InvariantCulture), Key("TxBeam"));
        Assert.Equal((r2.Rx + 1).ToString(CultureInfo.InvariantCulture), Key("RxBeam"));
        Assert.Equal(undo + 1, vm.UndoRedo.Entries.Count());
        Assert.Equal((r2.Tx, r2.Rx), (tool.Channel!.Table!.Chosen.Tx, tool.Channel.Table.Chosen.Rx));

        vm.UndoRedo.Undo();
        Assert.Null(Key("BeamSelect"));
        Assert.Null(Key("TxBeam"));
        Assert.Null(Key("RxBeam"));

        tool.SelectPair(tool.BeamRows.Single(r => r.Rank == 2));
        var t = tool.Channel!.Table!;
        var (txBeam, rxBeam) = (t.TxBeams[t.Chosen.Tx], t.RxBeams[t.Chosen.Rx]);
        int before = vm.UndoRedo.Entries.Count();
        tool.ApplyBeam();
        output.WriteLine($"applied: t1 {Var("t1"):F2}° p1 {Var("p1"):F2}° t2 {Var("t2"):F2}° p2 {Var("p2"):F2}°; BeamSelect={Key("BeamSelect")}");
        Assert.Equal(before + 1, vm.UndoRedo.Entries.Count());
        Assert.Equal(Math.Round(txBeam.ThetaDeg, 2), Var("t1"), 9);
        Assert.Equal(Math.Round(txBeam.PhiDeg, 2), Var("p1"), 9);
        Assert.Equal(Math.Round(rxBeam.ThetaDeg, 2), Var("t2"), 9);
        Assert.Equal(Math.Round(rxBeam.PhiDeg, 2), Var("p2"), 9);
        Assert.Equal("manual", Key("BeamSelect"));
        Assert.Null(Key("TxBeam"));
        Assert.Null(Key("RxBeam"));
    }

    /// <summary>
    /// Brief PCH-13 — a beam drag on a channel end sticks. The channel picks the beams (BeamSelect unstated), and the run steers the
    /// array to its choice whatever steer() says, so the drag moved the beam and the release's solve put it back. Released, the drag
    /// writes the dragged steering, the other end's chosen direction and BeamSelect=manual as one undo step, and the settled frame
    /// peaks where the beam was dragged.
    /// </summary>
    [Fact]
    public void ADragOnAChannelEnd_SteersByHand_AndTheBeamStaysWhereItWasDragged()
    {
        string rays = Rays("drag.csv",
            "0, 0, 12, 20, 18, 100", "10, -3, 13, 21, 17, 101", "20, -3, 11, 19, 19, 99",
            "100, -5, 42, 200, 35, -80", "110, -8, 43, 201, 34, -79", "120, -8, 41, 199, 36, -81");
        string cnl = Bench(rays).Replace("P1Tone:S1 f1 0 Num=1 Pavl=10 Z=50 Freq=5.9 GHz", "Port:P1 f1 0 Num=1 Z=50 Ohm") +
                     "\nanalysis F1 type=fields block=PA1 freq=5.9 freqUnit=GHz drive=1\n";
        var (lib, tb) = new CnlReader().Read(cnl, "bench", _dir);
        var drawn = NetlistSchematic.Build(lib, tb, _dir);
        drawn.Schematic!.SchematicDirectory = _dir;
        var vm = new SchematicViewModel(drawn.Schematic);
        PreparedCircuit? Prepare(SchematicViewModel s) => PreparedCircuit.FromText(SchematicCircuit.CnlTextOf(s.EditModel, "bench"), _dir, _dir);
        var pa1 = vm.EditModel.Components.Single(c => c.InstanceName == "PA1");
        var ch = vm.EditModel.Components.Single(c => c.InstanceName == "CH1");
        using var tool = new PhasedArrayToolViewModel(vm, pa1, Prepare, new ImmediatePhasedArrayHost());
        Assert.True(tool.Channel?.Table is not null, tool.ChannelStatus);
        var t = tool.Channel!.Table!;
        var rxBeam = t.RxBeams[t.Chosen.Rx];
        double Var(string name) => SchematicNominal.Of(vm.EditModel.Components).Real(name, null)!.Value * 180 / Math.PI;
        int before = vm.UndoRedo.Entries.Count();

        Assert.True(tool.BeginDrag());
        tool.DragToAngles(25, 60);
        tool.EndDrag();

        output.WriteLine($"t1 {Var("t1"):F2}° p1 {Var("p1"):F2}° t2 {Var("t2"):F2}° p2 {Var("p2"):F2}°; peak θ {tool.Frame?.Readouts.PeakThetaDeg:F2}°, φ {tool.Frame?.Readouts.PeakPhiDeg:F2}°");
        Assert.Equal(before + 1, vm.UndoRedo.Entries.Count());
        Assert.Equal("manual", ch.Parameters.Single(p => p.Name == "BeamSelect").Expression);
        Assert.Equal(25.0, Var("t1"), 9);
        Assert.Equal(60.0, Var("p1"), 9);
        Assert.Equal(Math.Round(rxBeam.ThetaDeg, 2), Var("t2"), 9);
        Assert.Equal(Math.Round(rxBeam.PhiDeg, 2), Var("p2"), 9);
        Assert.True(tool.Frame is not null, tool.Status);
        Assert.InRange(tool.Frame!.Readouts.PeakThetaDeg, 22, 28);
        Assert.InRange(tool.Frame.Readouts.PeakPhiDeg, 55, 65);
    }

    /// <summary>
    /// A re-weighed evaluation (a steer VAR tuned, as a beam drag tunes it) on new beamformers takes the previous choice again — one
    /// weighing per end, no pairs scored — since every candidate replaces θ0 and φ0 and so none of them moved.
    /// </summary>
    [Fact]
    public void AReweighedRun_TakesThePreviousChoice_WithoutSearchingAgain()
    {
        string text = Bench(Rays("one.csv", "0, 0, 23, 33, 37, -58")) + "\nanalysis HB1 type=hb Tone=\"5.9\" ToneUnit=GHz MaxHarm=1 Tol=1e-13\n";
        var circuit = PreparedCircuit.FromText(text, _dir, _dir);
        RunResult Eval(string t1) => CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest
        {
            Tunables = new Dictionary<string, string> { ["t1"] = t1 }, Expressions = ["mag(HB1.V(\"f2\",1))"],
        });
        var first = Eval("0 deg");
        Assert.True(first.Status == RunStatus.Success, first.StatusMessage);
        long pairs = BeamSelection.PairsScored, reused = BeamSelection.Reused, elab = Elaborator.Elaborations;
        var second = Eval("10 deg");
        Assert.True(second.Status == RunStatus.Success, second.StatusMessage);
        output.WriteLine($"elaborations {Elaborator.Elaborations - elab}, pairs {BeamSelection.PairsScored - pairs}, reused {BeamSelection.Reused - reused}; " +
                         $"|V| {first.Expressions[0].Value} then {second.Expressions[0].Value}");
        Assert.Equal(0, Elaborator.Elaborations - elab);   // re-weighed, not elaborated
        Assert.Equal(0, BeamSelection.PairsScored - pairs);
        Assert.Equal(1, BeamSelection.Reused - reused);
        Assert.Equal(first.Expressions[0].Value!.Value.AsReal(), second.Expressions[0].Value!.Value.AsReal(), 12);   // auto overrides the authored steer
    }

    /// <summary>The scoring reports its stages to the control it is given (the codebooks weighed, beam by beam) and stops on its token.</summary>
    [Fact]
    public void Scoring_ReportsItsStages_AndStopsWhenCancelled()
    {
        var nl = Elaborate(Bench(Rays("one.csv", "0, 0, 23, 33, 37, -58")));
        var seen = new List<CircuitRF.Engine.RunProgress>();
        var control = new CircuitRF.Engine.RunControl { Progress = new Recorder(seen), MinReportIntervalMs = 0 };
        var t = BeamSelection.Score(Channel(nl), "CH1", F, control);
        Assert.NotNull(t);
        var weighing = seen.Where(p => p.Stage.Contains("weighing the codebooks")).ToList();
        Assert.Equal(t!.TxBeams.Count + t.RxBeams.Count, weighing.Max(p => p.StageTotal));
        Assert.Equal(weighing.Max(p => p.StageTotal), weighing.Max(p => p.StageCompleted));
        Assert.Contains(seen, p => p.Stage.Contains("scoring every pair"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => BeamSelection.Score(Channel(Elaborate(Bench(Rays("one.csv", "0, 0, 23, 33, 37, -58")))), "CH1", F,
                                                                              new CircuitRF.Engine.RunControl { Token = cts.Token }));
    }

    private sealed class Recorder(List<CircuitRF.Engine.RunProgress> into) : IProgress<CircuitRF.Engine.RunProgress>
    {
        public void Report(CircuitRF.Engine.RunProgress value) => into.Add(value);
    }

    /// <summary>
    /// The closed form a codebook is scored with forms the weights the exact weighing (the array's own FeedWeights) does: continuous, quantised
    /// with a taper and an attenuator step, and as a true-time delay; it is absent for a field that is not the bare call.
    /// </summary>
    [Theory]
    [InlineData("Phase=steer(t1,p1,f0)")]
    [InlineData("Phase=steer(t1,p1,f0) Amp=taylor(-25,3) PhaseBits=5 AttenStep=0.5")]
    [InlineData("Delay=steerDelay(t1,p1) Amp=cosq(1) Phase=15")]
    public void TheClosedForm_IsTheExactWeighing(string weights)
    {
        var nl = Elaborate(Bench("", 16, weights).Replace("PropagationChannel:CH1 Tx=PA1 Rx=PA2 Distance=100 TxHeight=1.5 RxHeight=1.5 f0=5.9 GHz ", ""));
        var steering = Array(nl, "PA1").Steering!;
        Assert.NotNull(steering.Fast);
        double worst = 0;
        int flips = 0;
        foreach (var (t, p) in new[] { (0.0, 0.0), (12.5, 33.0), (41.0, -127.0), (58.9, 180.0) })
        {
            var exact = steering.Weigh(t, p);
            var fast = steering.Fast!(t, p);
            for (int e = 0; e < exact.Elements; e++)
            {
                double d = (exact.Quantised(e, F) - fast.Quantised(e, F)).Magnitude;
                if (d > 1e-9) flips++; else worst = Math.Max(worst, d);
                worst = Math.Max(worst, (exact.Requested(e, F) - fast.Requested(e, F)).Magnitude);
            }
        }
        output.WriteLine($"{weights}: worst difference {worst:E2}, quantisation boundaries crossed {flips}");
        Assert.True(worst < 1e-12, $"the closed form differs by {worst}");
        Assert.Equal(0, flips);

        var offset = Elaborate(Bench("", 8, "Phase=steer(t1,p1,f0)+10").Replace("PropagationChannel:CH1 Tx=PA1 Rx=PA2 Distance=100 TxHeight=1.5 RxHeight=1.5 f0=5.9 GHz ", ""));
        Assert.Null(Array(offset, "PA1").Steering!.Fast);
        Assert.Null(Array(offset, "PA1").Steering!.Refusal);   // still steerable, scored through the exact weighing
    }

    /// <summary>
    /// A bench elaborated again (an envelope segment, a sweep point) takes the kept selection — no search, the same weights — and one whose
    /// taper changed searches again.
    /// </summary>
    [Fact]
    public void ASecondElaboration_TakesTheKeptSelection_AndAChangedTaperSearchesAgain()
    {
        BeamSelection.ForgetKept();
        string rays = Rays("one.csv", "0, 0, 23, 33, 37, -58");
        var first = Elaborate(Bench(rays));
        BeamSelection.Apply(first, F, setCarrier: true);
        long searches = BeamSelection.Selections, hits = BeamSelection.CacheHits;
        var second = Elaborate(Bench(rays));
        BeamSelection.Apply(second, F, setCarrier: true);
        Assert.Equal(searches, BeamSelection.Selections);
        Assert.Equal(hits + 1, BeamSelection.CacheHits);
        foreach (string pa in new[] { "PA1", "PA2" })
            for (int e = 0; e < Array(first, pa).Feed!.Elements; e++)
                Assert.Equal(Array(first, pa).Feed!.Quantised(e, F), Array(second, pa).Feed!.Quantised(e, F));

        var tapered = Elaborate(Bench(rays, pa1Phase: "Phase=steer(t1,p1,f0) Amp=taylor(-25,3)"));
        BeamSelection.Apply(tapered, F, setCarrier: true);
        Assert.Equal(searches + 1, BeamSelection.Selections);
    }

    /// <summary>Netlists of one bench selecting at once (an envelope run's parallel segments): one searches, the others wait and take it.</summary>
    [Fact]
    public void ParallelSegments_SearchOnce()
    {
        BeamSelection.ForgetKept();
        string text = Bench(Rays("one.csv", "0, 0, 23, 33, 37, -58"));
        var nets = Enumerable.Range(0, 4).Select(_ => Elaborate(text)).ToArray();
        long searches = BeamSelection.Selections, hits = BeamSelection.CacheHits;
        Parallel.For(0, nets.Length, i => BeamSelection.Apply(nets[i], F, setCarrier: true));
        Assert.Equal(searches + 1, BeamSelection.Selections);
        Assert.Equal(hits + 3, BeamSelection.CacheHits);
        for (int k = 1; k < nets.Length; k++)
            for (int e = 0; e < Array(nets[0], "PA1").Feed!.Elements; e++)
                Assert.Equal(Array(nets[0], "PA1").Feed!.Quantised(e, F), Array(nets[k], "PA1").Feed!.Quantised(e, F));
    }
}

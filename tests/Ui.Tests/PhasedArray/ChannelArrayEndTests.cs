// brief-pch-8 — the Propagation Channel with a Phased Antenna Array end, through the schematic path (CircuitEvaluation on .cnl text, which
// loads the design layer the arrays read their patterns through). Fixtures: testdata/phased-array (5.9 GHz, 5° grid).

using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using CircuitRF.Core.Devices;
using CircuitRF.Design.Circuit;
using RfCore.Propagation;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.PhasedArray;

[Collection(Sar.FieldCosimulationRunsCollection.Name)]
public sealed class ChannelArrayEndTests(ITestOutputHelper output) : IDisposable
{
    private const double F = 5.9e9;
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-pch8-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string Record(string name, [CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "phased-array", "results", name + ".cemr"));

    private static readonly string E5 = $"Record=\"{Record("e5")}\" RecordRows=5 RecordCols=5 RecordDx=25.86 mm RecordDy=25.86 mm dx=25.86 mm dy=25.86 mm";
    private static string Inv(double x) => x.ToString("R", CultureInfo.InvariantCulture);

    private RunResult Run(string text, params string[] expressions)
    {
        var run = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(text, _dir, _dir), new CircuitEvaluationRequest { Expressions = expressions });
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage + " " + string.Join(" ", run.Warnings));
        return run;
    }

    /// <summary>The feed's complex voltage (harmonic 1) of a linear harmonic-balance run.</summary>
    private Complex Feed(string body)
    {
        var run = Run(body + "\nR:RL f 0 R=50 Ohm\nanalysis HB1 type=hb Tone=\"5.9\" ToneUnit=GHz MaxHarm=1 Tol=1e-13\n",
                      "real(HB1.V(\"f\",1))", "imag(HB1.V(\"f\",1))");
        double V(int k) => run.Expressions[k].Value?.AsReal() ?? throw new Xunit.Sdk.XunitException(run.Expressions[k].Error);
        return new Complex(V(0), V(1));
    }

    /// <summary>
    /// Gate 1 — reciprocity through the channel: a transmit pin of known EIRP (P1Tone of 10 dBm into a matched pin, TxGain 6 dBi, φ̂ in its
    /// own frame, which is global ẑ) 40 m from an 8 × 8 Feed-mode receive array turned so the line of sight arrives off boresight, through one
    /// line-of-sight ray in free space, puts at the feed the voltage PAA-7's incident wave from the same direction does at the free-space power
    /// density EIRP/(4πd²) in the same polarization, to 1e-10.
    /// </summary>
    [Fact]
    public void Gate1_OneRayFromAPinOfKnownEirp_IsPaa7sIncidentWave()
    {
        const double d = 40, pavlDbm = 10, gainDbi = 6, rxAz = 165, rxEl = 12;
        string array = $"PhasedArray:PA1 f {E5} Mode=feed Rows=8 Cols=8 Amp=taylor(-20,3)";
        var viaChannel = Feed($"""
            P1Tone:S1 tx 0 Num=1 Pavl={Inv(pavlDbm)} Z=50 Freq=5.9 GHz
            PropagationChannel:CH1 tx Rx=PA1 TxPos="0,0,10" RxPos="{Inv(d)},0,10" TxGain={Inv(gainDbi)} TxPol=phi RxAz={Inv(rxAz)} RxEl={Inv(rxEl)}
            {array}
            """);

        // the same arrival in the array's own frame, and the polarization (global ẑ) in its (θ̂, φ̂) there
        var frame = AntennaFrame.Of(rxAz, rxEl, 0);
        var arrival = new Vec3(-1, 0, 0);
        var (th, ph) = frame.Angles(arrival);
        var (tg, pg) = (frame.ToGlobal(th, ph, 1, 0), frame.ToGlobal(th, ph, 0, 1));
        double pt = tg.Z.Real, pp = pg.Z.Real;                    // ẑ·θ̂, ẑ·φ̂ (both real: ẑ is transverse to the arrival)
        double psi = Math.Atan2(pp, pt) * 180 / Math.PI;
        double eirpW = Math.Pow(10, (pavlDbm + gainDbi - 30) / 10), s = eirpW / (4 * Math.PI * d * d);
        double pdDbm = 10 * Math.Log10(s * 1000);
        var viaWave = Feed($"{array} RxTheta[1]={Inv(th * 180 / Math.PI)} RxPhi[1]={Inv(ph * 180 / Math.PI)} RxPd[1]={Inv(pdDbm)} RxFreq[1]=5.9 GHz RxPol[1]={Inv(psi)}");
        double rel = (viaChannel - viaWave).Magnitude / viaWave.Magnitude;
        output.WriteLine($"arrival θ {th * 180 / Math.PI:F3}° φ {ph * 180 / Math.PI:F3}°, pol {psi:F3}°; feed via the channel {viaChannel}, via PAA-7's wave {viaWave} ({rel:E2})");
        Assert.True(rel <= 1e-10, $"relative {rel:E2}");
    }

    /// <summary>An MNA context that only counts what is stamped into it.</summary>
    private sealed class CountingMna : CircuitRF.Core.IMnaContext
    {
        public long Entries, Branches;
        public void AddAdmittance(int a, int b, Complex y) => Entries++;
        public void AddBlockAdmittance(int r, int c, Complex y) => Entries++;
        public int AddBranch() => (int)Branches++;
        public void AddBranchCurrent(int br, int a, int b) => Entries += 2;
        public void AddConstraint(int br, int node, Complex v) => Entries++;
        public void AddNodeBranchCoupling(int node, int br, Complex v) => Entries++;
        public void AddBranchConstraint(int br, int other, Complex v) => Entries++;
        public void AddCurrentInjection(int node, Complex j) { }
        public void AddSourceValue(int br, Complex v) { }
    }

    /// <summary>
    /// Gate 2 — ray unknowns are sparse: a 64 × 64 Feed-mode array to a 64 × 64 one through CDL-A (460 rays) stamps two unknowns a ray and
    /// 2K·(2N_t + N_r) coupling entries, and the channel allocates nothing longer than one end's element count — never N_r × N_t.
    /// </summary>
    [Fact]
    public void Gate2_ALargeLinkThroughCdlA_HoldsNothingOfNrByNt()
    {
        var circuit = PreparedCircuit.FromText($"""
            Port:P1 f1 0 Num=1 Z=50 Ohm
            Port:P2 f2 0 Num=2 Z=50 Ohm
            PhasedArray:PA1 f1 {E5} Mode=feed Rows=64 Cols=64
            PhasedArray:PA2 f2 {E5} Mode=feed Rows=64 Cols=64
            PropagationChannel:CH1 Tx=PA1 Rx=PA2 Distance=500 TxHeight=25 RxHeight=1.5 Multipath=cdl Profile=CDL-A DelaySpread=100e-9 f0=5.9 GHz
            """, _dir, _dir);
        using var nl = new CircuitRF.Core.Elaboration.Elaborator(circuit.Lib!).Elaborate(circuit.Tb!);
        var ec = nl.Components.Single(c => c.Model is PropagationChannelModel);
        Assert.Same(ec, nl.Components[^1]);                                   // stamped after both arrays
        var m = (PropagationChannelModel)ec.Model;
        PropagationChannelModel.ResetCounters();
        var mna = new CountingMna();
        m.Stamp(mna, ec, 2 * Math.PI * F);
        int k = m.At(F).Rays.Count, nt = 4096, nr = 4096;
        _ = m.InstanceCouplings(F);
        long bound = 2L * k * (2L * nt + nr);
        output.WriteLine($"{k} rays: {mna.Branches} branches, {mna.Entries:N0} entries (bound 2K(2Nt+Nr) = {bound:N0}; Nr×Nt = {(long)nr * nt:N0}); " +
                         $"longest coefficient array {PropagationChannelModel.LargestCoefficientArray}");
        Assert.Equal(460, k);
        Assert.Equal(2 * k, mna.Branches);
        Assert.True(mna.Entries <= bound + 2L * k, $"{mna.Entries} entries");
        Assert.True(PropagationChannelModel.LargestCoefficientArray <= Math.Max(nt, nr));
    }

    /// <summary>Gate 4, through the channel: CDL-D's line-of-sight ray leaves along the ends' line and arrives from it, to 1e-12 rad.</summary>
    [Fact]
    public void Gate4_CdlD_LineOfSightRay_LiesOnTheEndsLine()
    {
        var circuit = PreparedCircuit.FromText("""
            PropagationChannel:CH1 a b TxPos="0,0,30" RxPos="180,-75,1.5" Multipath=cdl Profile=CDL-D DelaySpread=30e-9 f0=28 GHz
            R:R1 b 0 R=50 Ohm
            """, _dir, _dir);
        using var nl = new CircuitRF.Core.Elaboration.Elaborator(circuit.Lib!).Elaborate(circuit.Tb!);
        var r = ((PropagationChannelModel)nl.Components.Single(c => c.Model is PropagationChannelModel).Model).At(28e9);
        var los = r.Geometry.LineOfSight(28e9);
        var ray = r.Rays.Single(x => x.Polarization == PolarizationMatrix.LineOfSight);
        double Angle(Vec3 a, Vec3 b) => Math.Acos(Math.Clamp(a.Unit.Dot(b.Unit), -1, 1));
        double dep = Angle(ray.Departure, los.Departure), arr = Angle(ray.ArrivalFrom, los.ArrivalFrom);
        output.WriteLine($"departure off the line by {dep:E2} rad, arrival by {arr:E2} rad");
        Assert.True(dep < 1e-12 && arr < 1e-12);
    }

    /// <summary>
    /// R-pch8-3 — the envelope's explicit coupling is the frozen one: a CW from a pin through two directional rays (a ray list, different
    /// directions, delays and polarization) into a 4 × 4 receive array reads, once settled, the feed-to-transmit-pin voltage ratio the harmonic-balance
    /// run's ray unknowns give, to 2e-8 (the envelope solved to Tol 1e-12; the residual follows Tol: 6e-8 at draft accuracy); and the run counts its multiply-adds.
    /// </summary>
    [Fact]
    public void Envelope_ThroughTwoDirectionalRays_SettlesOnTheFrozenCoupling()
    {
        string rays = Path.Combine(_dir, "rays.csv");
        File.WriteAllText(rays, "delay (ns), power (dB), theta_d (deg), phi_d (deg), theta_a (deg), phi_a (deg), xpr (dB)\n"
                                + "0, 0, 90, 0, 90, 180, 20\n35, -4, 80, 25, 95, 150, 8\n");
        string Bench(string source, string analysis) => $"""
            {source}
            IProbe:Itx s tx
            PropagationChannel:CH1 tx Rx=PA1 Distance=30 TxHeight=10 RxHeight=10 Multipath=rays File="{rays}" TxGain=3 TxPol=45 Seed=4
            PhasedArray:PA1 f {E5} Mode=feed Rows=4 Cols=4
            IProbe:Iout f fo
            R:RL fo 0 R=50 Ohm
            {analysis}
            """;
        var hb = Run(Bench("P1Tone:S1 s 0 Num=1 Pavl=0 Z=50 Freq=5.9 GHz", "analysis HB1 type=hb Tone=\"5.9\" ToneUnit=GHz MaxHarm=1 Tol=1e-13"),
                     "mag(HB1.V(\"f\",1))/mag(HB1.V(\"tx\",1))");
        double vHb = hb.Expressions[0].Value!.Value.AsReal();

        var circuit = PreparedCircuit.FromText(Bench("MOD_Source:S1 s 0 Fc=5.9e9 Pavg=0 Z=50 Ohm Record=20e-6 Carrier[1]=\"CW;Off=0;P=0\"",
            "analysis ENV1 type=env MaxHarm=1 Segments=1 Warmup=1e-6 Stop=4e-6 Tol=1e-12"), _dir, _dir);
        var (lib, tb) = (circuit.Lib!, circuit.Tb!);
        var nl = new CircuitRF.Core.Elaboration.Elaborator(lib).Elaborate(tb);
        var plan = CircuitRF.Engine.Envelope.EnvelopeRunner.Resolve(tb.Analyses.OfType<CircuitRF.Core.Design.EnvelopeAnalysis>().Single(), nl.ResolvedGlobals);
        bool used = false;
        CircuitRF.Core.Elaboration.ElaboratedNetlist Elab() { if (used) return new CircuitRF.Core.Elaboration.Elaborator(lib).Elaborate(tb); used = true; return nl; }
        var (r, _, _) = CircuitRF.Engine.Envelope.EnvelopeRunner.Record(plan, Elab);
        int b = Array.IndexOf(r.IProbeNames, "Iout");
        double vEnv = r.IProbeVoltage[^1][b][1].Magnitude / r.IProbeVoltage[^1][Array.IndexOf(r.IProbeNames, "Itx")][1].Magnitude;
        var cost = CircuitRF.Engine.Envelope.EnvelopeChannel.LastInstanceCost;
        output.WriteLine($"|V(feed)/V(tx)|: envelope {vEnv:G12}, harmonic balance {vHb:G12} ({vEnv / vHb - 1:E2}); {cost.Rays} rays, {cost.TxElements} → {cost.RxElements} elements, " +
                         $"{cost.MultiplyAddsPerStep} multiply-adds a step");
        Assert.True(Math.Abs(vEnv / vHb - 1) < 2e-8, $"{vEnv} against {vHb}");
        Assert.Equal(2, cost.Rays);
        // brief-pch-8b: the 4 × 4 array is on the fast path, so the receive collapse projects each ray on its feed, not its 16 elements
        Assert.Equal(1, cost.RxElements);
        Assert.Equal(16, CircuitRF.Engine.Envelope.EnvelopeChannel.CollapsedElements);
    }

    private static string Testdata(string name, [CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "propagation", name));

    private static readonly string E1 = $"Record=\"{Record("e1")}\" dx=25.86 mm dy=25.86 mm";

    /// <summary>The 16 × 16 isolated-element array of gates 5 and 6, uniform weights, at the origin facing +x.</summary>
    private static string Array16 => $"PhasedArray:PA1 f {E1} Mode=feed Rows=16 Cols=16";

    /// <summary>One case of array_link_rays.json: what the script reads, from the channel's own realization.</summary>
    private static System.Text.Json.Nodes.JsonObject CaseOf(PropagationChannelModel m, double hz, double pavlDbm)
    {
        var rays = new System.Text.Json.Nodes.JsonArray();
        foreach (var e in m.DescribeRays(hz))
        {
            static System.Text.Json.Nodes.JsonArray Arr(params double[] v) => [.. v.Select(x => (System.Text.Json.Nodes.JsonNode)x)];
            rays.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["power"] = e.Ray.Gain.Magnitude * e.Ray.Gain.Magnitude, ["phase_rad"] = e.Ray.Gain.Phase, ["delay_s"] = e.Ray.ExcessDelayS,
                ["m"] = Arr(e.M.Tt.Real, e.M.Tt.Imaginary, e.M.Tp.Real, e.M.Tp.Imaginary, e.M.Pt.Real, e.M.Pt.Imaginary, e.M.Pp.Real, e.M.Pp.Imaginary),
                ["tx"] = Arr(e.Tx.Theta.Real, e.Tx.Theta.Imaginary, e.Tx.Phi.Real, e.Tx.Phi.Imaginary),
                ["rx"] = Arr(e.Rx.Theta.Real, e.Rx.Theta.Imaginary, e.Rx.Phi.Real, e.Rx.Phi.Imaginary),
                ["theta_rad"] = e.RxTheta, ["phi_rad"] = e.RxPhi,
            });
        }
        var r = m.At(m.ReferenceHz(hz));
        return new System.Text.Json.Nodes.JsonObject
        {
            ["freq_hz"] = hz, ["distance_m"] = r.DistanceM, ["pavl_dbm"] = pavlDbm, ["rows"] = 16, ["cols"] = 16, ["dx_m"] = 0.02586, ["dy_m"] = 0.02586,
            ["rays"] = rays,
        };
    }

    private static PropagationChannelModel Channel(CircuitRF.Core.Elaboration.ElaboratedNetlist nl, string name)
        => (PropagationChannelModel)nl.Components.Single(c => c.InstancePath == name).Model;

    private const string Ch1 = "PropagationChannel:CH1 t1 Rx=PA1 TxPos=\"80,0,1.5\" RxPos=\"0,0,1.5\" RxAz=0 RxEl=0 TxGain=6 TxPol=phi f0=5.9 GHz";
    private const string Ch2 = "PropagationChannel:CH2 t2 Rx=PA1 TxPos=\"50,30,1.5\" RxPos=\"0,0,1.5\" RxAz=0 RxEl=0 TxGain=3 TxPol=phi f0=5.95 GHz";
    /// <summary>brief-pch-8b — the interferer on the signal's frequency.</summary>
    private const string Ch2s = "PropagationChannel:CH2 t2 Rx=PA1 TxPos=\"50,30,1.5\" RxPos=\"0,0,1.5\" RxAz=0 RxEl=0 TxGain=3 TxPol=phi f0=5.9 GHz";
    private const string Cdl = "Multipath=cdl Profile=CDL-C DelaySpread=100e-9 Seed=1 Drop=1";

    /// <summary>
    /// The rays the script reads, recomputed from the channel's realization: equal to the committed array_link_rays.json to 1e-12, so a
    /// change to the realization fails here rather than silently drifting from the prediction. CRF_WRITE_ARRAY_LINK=1 writes the file
    /// instead (then run array_link_reference.py).
    /// </summary>
    private void CheckRays()
    {
        var cases = new System.Text.Json.Nodes.JsonObject();
        foreach (var (name, ch, extra, hz, pavl) in new[] { ("los", Ch1, "", 5.9e9, 10.0), ("cdlc", Ch1, Cdl, 5.9e9, 10.0), ("sig", Ch1, "", 5.9e9, 10.0), ("int", Ch2, "", 5.95e9, 0.0), ("int59", Ch2s, "", 5.9e9, 0.0) })
        {
            var circuit = PreparedCircuit.FromText($"{ch} {extra}\n{Array16}\nR:RL f 0 R=50 Ohm\nR:RT t1 0 R=50 Ohm\nR:RT2 t2 0 R=50 Ohm\n", _dir, _dir);
            using var nl = new CircuitRF.Core.Elaboration.Elaborator(circuit.Lib!).Elaborate(circuit.Tb!);
            cases[name] = CaseOf(Channel(nl, ch[19..22]), hz, pavl);
        }
        var now = new System.Text.Json.Nodes.JsonObject { ["cases"] = cases };
        string path = Testdata("array_link_rays.json");
        if (Environment.GetEnvironmentVariable("CRF_WRITE_ARRAY_LINK") == "1")
        {
            File.WriteAllText(path, now.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false }));
            return;
        }
        var was = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        double worst = 0;
        void Walk(System.Text.Json.Nodes.JsonNode? a, System.Text.Json.Nodes.JsonNode? b)
        {
            switch (a)
            {
                case System.Text.Json.Nodes.JsonObject o: foreach (var (k, v) in o) Walk(v, b![k]); break;
                case System.Text.Json.Nodes.JsonArray arr: Assert.Equal(arr.Count, b!.AsArray().Count); for (int i = 0; i < arr.Count; i++) Walk(arr[i], b[i]); break;
                default:
                    double x = double.Parse(a!.ToJsonString(), CultureInfo.InvariantCulture), y = double.Parse(b!.ToJsonString(), CultureInfo.InvariantCulture);
                    worst = Math.Max(worst, Math.Abs(x - y) / Math.Max(1, Math.Abs(y)));
                    break;
            }
        }
        Walk(was, now);
        Assert.True(worst < 1e-12, $"the channel's rays moved from array_link_rays.json by {worst:E2}: regenerate it (CRF_WRITE_ARRAY_LINK=1) and the prediction");
    }

    private static double Prediction(string name)
        => System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Testdata("array_link_reference.json")))!["feed_power_dbm"]![name]!.GetValue<double>();

    private double FeedDbm(string body, string at = "1")
    {
        var run = Run(body + "\nR:RL f 0 R=50 Ohm\nanalysis HB1 type=hb Tone=\"5.9\" ToneUnit=GHz MaxHarm=1 Tol=1e-13\n", $"mag(HB1.V(\"f\",{at}))");
        double v = run.Expressions[0].Value?.AsReal() ?? throw new Xunit.Sdk.XunitException(run.Expressions[0].Error);
        return 10 * Math.Log10(v * v / 100 * 1000);
    }

    /// <summary>
    /// Gate 5 — beam versus spread: a fixed boresight beam on a 16 × 16 array loses array gain in CDL-C (480 rays from every direction, each
    /// with its own polarization) against the line of sight by what array_link_reference.py predicts from the same rays, to 0.01 dB.
    /// </summary>
    [Fact]
    public void Gate5_ABoresightBeamInCdlC_LosesWhatTheScriptPredicts()
    {
        CheckRays();
        double los = FeedDbm($"P1Tone:S1 t1 0 Num=1 Pavl=10 Z=50 Freq=5.9 GHz\n{Ch1}\n{Array16}");
        double cdl = FeedDbm($"P1Tone:S1 t1 0 Num=1 Pavl=10 Z=50 Freq=5.9 GHz\n{Ch1} {Cdl}\n{Array16}");
        double loss = los - cdl, want = Prediction("los") - Prediction("cdlc");
        output.WriteLine($"feed: line of sight {los:F6} dBm (script {Prediction("los"):F6}), CDL-C {cdl:F6} dBm (script {Prediction("cdlc"):F6}); " +
                         $"loss {loss:F6} dB against {want:F6} dB");
        Assert.True(Math.Abs(loss - want) < 0.01, $"loss {loss} dB against {want} dB");
        Assert.True(Math.Abs(los - Prediction("los")) < 0.01);
    }

    /// <summary>
    /// Gate 6 — interference: two transmitters through two channels into one array, at two tones. Each tone's power at the feed is what that
    /// channel alone delivers (the contributions add), and the signal-to-interference ratio is the script's to 0.01 dB.
    /// </summary>
    [Fact]
    public void Gate6_TwoTransmittersIntoOneArray_AddAndHaveTheScriptsSinr()
    {
        CheckRays();
        string sources = "P1Tone:S1 t1 0 Num=1 Pavl=10 Z=50 Freq=5.9 GHz\nP1Tone:S2 t2 0 Num=2 Pavl=0 Z=50 Freq=5.95 GHz";
        var run = Run($"{sources}\n{Ch1}\n{Ch2}\n{Array16}\nR:RL f 0 R=50 Ohm\n" +
                      "analysis HB1 type=hb NumFreqs=2 Tone[1]=5.9e9 Tone[2]=5.95e9 MaxMixOrder=1 MaxHarm=1 Tol=1e-13\n",
                      "mag(HB1.V(\"f\",\"(1,0)\"))", "mag(HB1.V(\"f\",\"(0,1)\"))");
        double Dbm(int k) { double v = run.Expressions[k].Value?.AsReal() ?? throw new Xunit.Sdk.XunitException(run.Expressions[k].Error); return 10 * Math.Log10(v * v / 100 * 1000); }
        double p1 = Dbm(0), p2 = Dbm(1);
        double alone = FeedDbm($"P1Tone:S1 t1 0 Num=1 Pavl=10 Z=50 Freq=5.9 GHz\nR:RT2 t2 0 R=50 Ohm\n{Ch1}\n{Array16}");
        double sinr = p1 - p2, want = Prediction("sig") - Prediction("int");
        output.WriteLine($"feed: signal {p1:F6} dBm (alone {alone:F6}, script {Prediction("sig"):F6}), interferer {p2:F6} dBm (script {Prediction("int"):F6}); " +
                         $"SINR {sinr:F6} dB against {want:F6} dB");
        Assert.True(Math.Abs(p1 - alone) < 1e-9, $"the signal moved by {p1 - alone} dB with the interferer present");
        Assert.True(Math.Abs(sinr - want) < 0.01, $"SINR {sinr} dB against {want} dB");
    }

    // ── brief-pch-8b ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A 16 × 16 Feed-mode receive array on the 5 × 5 record (embedded, coupling on), each element through an identical, mismatched
    /// linear channel (an attenuator with 12 dB return loss), reached from a pin through CDL-C.</summary>
    private static string CollapseBench(string channelCell = "Atten:ATT el 0 bf 0 Loss=3 RL=12", string multipath = Cdl, int size = 16) => $"""
        define rxch ( bf el )
        {channelCell}
        end
        PropagationChannel:CH1 tx Rx=PA1 TxPos="80,0,1.5" RxPos="0,0,1.5" RxAz=0 RxEl=0 TxGain=6 TxPol=phi f0=5.9 GHz {multipath}
        PhasedArray:PA1 f {E5} Mode=feed Rows={size} Cols={size} Channel=rxch
        """;

    private (Complex Collapsed, Complex Elements) BothWaysS21(string body)
    {
        Complex Eval()
        {
            var d = Run(body).Results.Single(r => r.Name == "SP1").Data;
            var cube = d.CubesIn(d.Groups.First(g => d.CubesIn(g).ContainsKey("S")))["S"];
            return cube.ComplexValues[(0 * 2 + 1) * 2 + 0];
        }
        try
        {
            PropagationChannelModel.CollapseEnabled = true;
            var c = Eval();
            PropagationChannelModel.CollapseEnabled = false;
            return (c, Eval());
        }
        finally { PropagationChannelModel.CollapseEnabled = true; }
    }

    private (Complex Collapsed, Complex Elements) BothWays(string body, string expr)
    {
        Complex Eval()
        {
            var run = Run(body, $"real({expr})", $"imag({expr})");
            double V(int k) => run.Expressions[k].Value?.AsReal() ?? throw new Xunit.Sdk.XunitException(run.Expressions[k].Error);
            return new Complex(V(0), V(1));
        }
        try
        {
            PropagationChannelModel.CollapseEnabled = true;
            var c = Eval();
            PropagationChannelModel.CollapseEnabled = false;
            return (c, Eval());
        }
        finally { PropagationChannelModel.CollapseEnabled = true; }
    }

    /// <summary>8b gate 1 (S-parameters) — through CDL-C into the 16 × 16 array with identical mismatched channels and coupling on, S21 from
    /// the transmit pin to the feed with the receive collapse is the element-by-element stamp's to 1e-12.</summary>
    [Fact]
    public void Gate8b1_SParameters_TheCollapseEqualsTheElementStamp()
    {
        PropagationChannelModel.ResetCounters();
        var sp = BothWaysS21("Port:P1 tx 0 Num=1 Z=50 Ohm\nPort:P2 f 0 Num=2 Z=50 Ohm\n" + CollapseBench() +
                             "\nanalysis SP1 type=sparam start=5.9 stop=5.9 npts=1 Unit=GHz\n");
        double rs = (sp.Collapsed - sp.Elements).Magnitude / sp.Elements.Magnitude;
        output.WriteLine($"S21 collapsed {sp.Collapsed} element {sp.Elements} ({rs:E2}); {PropagationChannelModel.CollapsedStamps} collapsed stamps, " +
                         $"{PropagationChannelModel.ElementsNotStamped} elements not stamped");
        Assert.True(rs <= 1e-12, $"S21 {rs:E2}");
        Assert.True(PropagationChannelModel.CollapsedStamps > 0 && PropagationChannelModel.ElementsNotStamped >= 256);
    }

    /// <summary>8b gate 1 (harmonic balance) — the same bench's feed voltage, collapsed against element by element, to 1e-10: harmonic
    /// balance's own floor on this bench (2.7e-11 measured; the S-parameter run, the same stamps through one sparse solve, agrees to 2.5e-15).</summary>
    [Fact]
    public void Gate8b1_HarmonicBalance_TheCollapseEqualsTheElementStamp()
    {
        var hb = BothWays("P1Tone:S1 tx 0 Num=1 Pavl=0 Z=50 Freq=5.9 GHz\nR:RL f 0 R=50 Ohm\n" + CollapseBench() +
                          "\nanalysis HB1 type=hb Tone=\"5.9\" ToneUnit=GHz MaxHarm=1 Tol=1e-13\n", "HB1.V(\"f\",1)");
        double rh = (hb.Collapsed - hb.Elements).Magnitude / hb.Elements.Magnitude;
        output.WriteLine($"HB V(f) collapsed {hb.Collapsed}, element {hb.Elements} ({rh:E2})");
        Assert.True(rh <= 1e-10, $"HB {rh:E2}");
    }

    /// <summary>8b gate 1 (envelope) — on a 4 × 4 array with the same identical mismatched channels (an envelope run of the 16 × 16 one
    /// with its 256 channel cells takes minutes whatever the rays), a CW through two directional rays settles on a feed/transmit ratio equal
    /// to the element-by-element run's to the envelope's own residual, projected on one node instead of 16.</summary>
    [Fact]
    public void Gate8b1_Envelope_TheCollapseEqualsTheElementStamp()
    {
        string rays = Path.Combine(_dir, "rays8b.csv");
        File.WriteAllText(rays, "delay (ns), power (dB), theta_d (deg), phi_d (deg), theta_a (deg), phi_a (deg), xpr (dB)\n"
                                + "0, 0, 90, 180, 90, 0, 20\n12, -3, 92, 170, 85, 12, 8\n");
        string envRays = $"Multipath=rays File=\"{rays}\" Seed=2";
        double Env(bool collapse)
        {
            PropagationChannelModel.CollapseEnabled = collapse;
            try
            {
                var circuit = PreparedCircuit.FromText("MOD_Source:S1 s 0 Fc=5.9e9 Pavg=0 Z=50 Ohm Record=20e-6 Carrier[1]=\"CW;Off=0;P=0\"\nIProbe:Itx s tx\n" +
                    CollapseBench(multipath: envRays, size: 4) + "\nIProbe:Iout f fo\nR:RL fo 0 R=50 Ohm\nanalysis ENV1 type=env MaxHarm=1 Segments=1 Warmup=1e-6 Stop=3e-6 Tol=1e-12\n", _dir, _dir);
                var (lib, tb) = (circuit.Lib!, circuit.Tb!);
                var nl = new CircuitRF.Core.Elaboration.Elaborator(lib).Elaborate(tb);
                var plan = CircuitRF.Engine.Envelope.EnvelopeRunner.Resolve(tb.Analyses.OfType<CircuitRF.Core.Design.EnvelopeAnalysis>().Single(), nl.ResolvedGlobals);
                bool used = false;
                CircuitRF.Core.Elaboration.ElaboratedNetlist Elab() { if (used) return new CircuitRF.Core.Elaboration.Elaborator(lib).Elaborate(tb); used = true; return nl; }
                var (r, _, _) = CircuitRF.Engine.Envelope.EnvelopeRunner.Record(plan, Elab);
                return r.IProbeVoltage[^1][Array.IndexOf(r.IProbeNames, "Iout")][1].Magnitude / r.IProbeVoltage[^1][Array.IndexOf(r.IProbeNames, "Itx")][1].Magnitude;
            }
            finally { PropagationChannelModel.CollapseEnabled = true; }
        }
        double ec = Env(true);
        int collapsedElements = CircuitRF.Engine.Envelope.EnvelopeChannel.CollapsedElements;
        double ee = Env(false);
        output.WriteLine($"envelope |V(f)/V(tx)| collapsed {ec:G12}, element {ee:G12} ({ec / ee - 1:E2}); projected on 1 node instead of {collapsedElements}");
        Assert.True(Math.Abs(ec / ee - 1) < 2e-8, $"envelope {ec / ee - 1:E2}");
        Assert.Equal(16, collapsedElements);
    }

    /// <summary>8b gate 2 — the collapse is refused, with the fast path's sentence, for a channel cell that reads n and for a nonlinear
    /// channel; the element stamp is then used.</summary>
    [Fact]
    public void Gate8b2_TheCollapse_IsRefusedWhereTheFastPathIs()
    {
        foreach (var (cell, word) in new[] { ("Atten:ATT el 0 bf 0 Loss=3+0.01*n RL=12", "differs per element"), ("Amp:A1 el 0 bf 0 Gain=20", "nonlinear") })
        {
            PropagationChannelModel.ResetCounters();
            var circuit = PreparedCircuit.FromText("Port:P1 tx 0 Num=1 Z=50 Ohm\nPort:P2 f 0 Num=2 Z=50 Ohm\n" + CollapseBench(cell), _dir, _dir);
            using var nl = new CircuitRF.Core.Elaboration.Elaborator(circuit.Lib!).Elaborate(circuit.Tb!);
            var ec = nl.Components.Single(c => c.Model is PropagationChannelModel);
            ((PropagationChannelModel)ec.Model).Stamp(new CountingMna(), ec, 2 * Math.PI * F);
            string sentence = ((PropagationChannelModel)ec.Model).CollapseSentence ?? "";
            output.WriteLine($"{cell}: {sentence}");
            Assert.Equal(0, PropagationChannelModel.CollapsedStamps);
            Assert.Equal(1, PropagationChannelModel.CollapseRefusals);
            Assert.Contains(word, sentence);
        }
    }

    /// <summary>
    /// 8b gate 3 — shares: a signal and an interferer on one frequency through two channels into the 16 × 16 array. Each channel's
    /// FeedPower_dBm is the feed power of a run with only that channel to 1e-9 dB, and SIR_dB is array_link_reference.py's to 0.01 dB.
    /// </summary>
    [Fact]
    public void Gate8b3_EachChannelsShare_IsItsRunAlone_AndTheSirIsTheScripts()
    {
        CheckRays();
        string s1 = "P1Tone:S1 t1 0 Num=1 Pavl=10 Z=50 Freq=5.9 GHz", s2 = "P1Tone:S2 t2 0 Num=2 Pavl=0 Z=50 Freq=5.9 GHz";
        string hb = "\nR:RL f 0 R=50 Ohm\nanalysis HB1 type=hb Tone=\"5.9\" ToneUnit=GHz MaxHarm=1 Tol=1e-13\n";
        var both = Run($"{s1}\n{s2}\n{Ch1}\n{Ch2s}\n{Array16}{hb}");
        var d = both.Results.Single(r => r.Name == "HB1").Data;
        double Cube(string name) => d.CubesIn(d.Groups.First(g => d.CubesIn(g).ContainsKey(name)))[name].RealValues[0];
        double p1 = Cube("link:CH1:FeedPower_dBm"), p2 = Cube("link:CH2:FeedPower_dBm");
        double a1 = FeedDbm($"{s1}\nR:RT2 t2 0 R=50 Ohm\n{Ch1}\n{Array16}"), a2 = FeedDbm($"R:RT1 t1 0 R=50 Ohm\n{s2}\n{Ch2s}\n{Array16}");
        var sirCube = d.CubesIn(d.Groups.First(g => d.CubesIn(g).ContainsKey("link:PA1:SIR_dB")))["link:PA1:SIR_dB"];
        double sir = sirCube.RealValues[Array.IndexOf(sirCube.Axes[0].Labels!, "CH1")], want = Prediction("sig") - Prediction("int59");
        output.WriteLine($"shares: CH1 {p1:F9} dBm (alone {a1:F9}), CH2 {p2:F9} dBm (alone {a2:F9}); SIR {sir:F6} dB against {want:F6} dB");
        Assert.True(Math.Abs(p1 - a1) < 1e-9 && Math.Abs(p2 - a2) < 1e-9);
        Assert.True(Math.Abs(sir - want) < 0.01, $"SIR {sir} against {want}");
    }

    /// <summary>
    /// 8b gate 4 — an EM Field Data block as an end: the one-port block on the e1 record receives through a ray from boresight exactly the
    /// wave a 1 × 1 Phased Antenna Array (Elements mode) on the same record does, to 1e-11 (the block stamps its Z, so its source passes
    /// through 2√Z0·(I − S)⁻¹, 1.0e-12 measured), and transmits as it does, to 1e-12.
    /// </summary>
    [Fact]
    public void Gate8b4_AFieldDataEnd_IsTheOneElementArray()
    {
        string efd = $"EmFieldData:EF1 p File=\"{Record("e1")}\"", pa = $"PhasedArray:EF1 p {E1} Mode=elements Rows=1 Cols=1";
        string hb = "analysis HB1 type=hb Tone=\"5.9\" ToneUnit=GHz MaxHarm=1 Tol=1e-13\n";
        string Rx(string end) => $"P1Tone:S1 tx 0 Num=1 Pavl=10 Z=50 Freq=5.9 GHz\nPropagationChannel:CH1 tx Rx=EF1 Distance=60 TxGain=6 TxPol=phi\n{end}\nR:RL p 0 R=50 Ohm\n{hb}";
        string Tx(string end) => $"P1Tone:S1 p 0 Num=1 Pavl=10 Z=50 Freq=5.9 GHz\n{end}\nPropagationChannel:CH1 rx Tx=EF1 Distance=60 RxGain=6 RxPol=phi\nR:RL rx 0 R=50 Ohm\n{hb}";
        Complex V(string body, string net)
        {
            var run = Run(body, $"real(HB1.V(\"{net}\",1))", $"imag(HB1.V(\"{net}\",1))");
            return new Complex(run.Expressions[0].Value!.Value.AsReal(), run.Expressions[1].Value!.Value.AsReal());
        }
        var rxBlock = V(Rx(efd), "p"); var rxArray = V(Rx(pa), "p");
        var txBlock = V(Tx(efd), "rx"); var txArray = V(Tx(pa), "rx");
        double er = (rxBlock - rxArray).Magnitude / rxArray.Magnitude, et = (txBlock - txArray).Magnitude / txArray.Magnitude;
        output.WriteLine($"receive: block {rxBlock}, array {rxArray} ({er:E2}); transmit: block {txBlock}, array {txArray} ({et:E2})");
        Assert.True(er <= 1e-11 && et <= 1e-12);
    }
}

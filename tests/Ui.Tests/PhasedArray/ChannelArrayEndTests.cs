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
        Assert.Equal(16, cost.RxElements);
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
    private const string Cdl = "Multipath=cdl Profile=CDL-C DelaySpread=100e-9 Seed=1 Drop=1";

    /// <summary>
    /// The rays the script reads, recomputed from the channel's realization: equal to the committed array_link_rays.json to 1e-12, so a
    /// change to the realization fails here rather than silently drifting from the prediction. CRF_WRITE_ARRAY_LINK=1 writes the file
    /// instead (then run array_link_reference.py).
    /// </summary>
    private void CheckRays()
    {
        var cases = new System.Text.Json.Nodes.JsonObject();
        foreach (var (name, ch, extra, hz, pavl) in new[] { ("los", Ch1, "", 5.9e9, 10.0), ("cdlc", Ch1, Cdl, 5.9e9, 10.0), ("sig", Ch1, "", 5.9e9, 10.0), ("int", Ch2, "", 5.95e9, 0.0) })
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
}

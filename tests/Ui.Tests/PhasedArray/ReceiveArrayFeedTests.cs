// brief-pch-10b — a receive array on the fast path replaced by its feed in an envelope run, and the per-ray couplings counted:
//   1  a 16 × 16 receive array with identical linear channel cells, fed through two directional rays and through CDL-C: every cube
//      of the run with the array replaced equals the run with it stepped, to the envelope's own floor;
//   2  the replaced run steps none of the array's parts (a counter), and a noisy run keeps the array (its cells' noise is part of it);
//   3  InstanceCouplings evaluates each pattern class once per ray.

using System.Runtime.CompilerServices;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Design.Circuit;
using CircuitRF.Engine.Envelope;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.PhasedArray;

[Collection("ReceiveCollapse")]   // ReplaceReceiveArrays is process-wide
public sealed class ReceiveArrayFeedTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-pch10b-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string Record(string name, [CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "phased-array", "results", name + ".cemr"));

    private static readonly string E5 = $"Record=\"{Record("e5")}\" RecordRows=5 RecordCols=5 RecordDx=25.86 mm RecordDy=25.86 mm dx=25.86 mm dy=25.86 mm";

    private string TwoRays()
    {
        string path = Path.Combine(_dir, "rays.csv");
        File.WriteAllText(path, "delay (ns), power (dB), theta_d (deg), phi_d (deg), theta_a (deg), phi_a (deg), xpr (dB)\n"
                                + "0, 0, 90, 180, 90, 0, 20\n7.3, -4, 80, 155, 95, 25, 8\n");
        return $"Multipath=rays File=\"{path}\"";
    }

    private string Bench(string multipath, string extra = "") => $"""
        MOD_Source:S1 tx 0 Fc=5.9e9 Pavg=0 Z=50 Ohm Record=20e-6 Carrier[1]="CW;Off=0;P=0" {extra}
        define rxch ( bf el )
        Atten:ATT el 0 bf 0 Loss=3 RL=12
        end
        PropagationChannel:CH1 tx Rx=PA1 TxPos="80,0,1.5" RxPos="0,0,1.5" RxAz=0 RxEl=0 TxGain=6 f0=5.9 GHz {multipath}
        PhasedArray:PA1 f {E5} Mode=feed Rows=16 Cols=16 Channel=rxch
        IProbe:Iout f fo
        R:RL fo 0 R=50 Ohm
        analysis ENV1 type=env MaxHarm=1 Segments=1 Warmup=0.2e-6 Stop=1e-6 Tol=1e-12
        """;

    private DataSet Run(string text, bool replace)
    {
        var circuit = PreparedCircuit.FromText(text, _dir, _dir);
        Assert.True(circuit.ReadError is null, circuit.ReadError);
        var nl = new Elaborator(circuit.Lib!).Elaborate(circuit.Tb!);
        var plan = EnvelopeRunner.Resolve(circuit.Tb!.Analyses.OfType<CircuitRF.Core.Design.EnvelopeAnalysis>().Single(), nl.ResolvedGlobals);
        bool used = false;
        ElaboratedNetlist Elab() { if (used) return new Elaborator(circuit.Lib!).Elaborate(circuit.Tb!); used = true; return nl; }
        try
        {
            EnvelopeChannel.ReplaceReceiveArrays = replace;
            return EnvelopeRunner.Run(plan, Elab);
        }
        finally { EnvelopeChannel.ReplaceReceiveArrays = true; }
    }

    private static (double Worst, string Name) Compare(DataSet a, DataSet b)
    {
        double worst = 0; string name = "";
        foreach (var (key, x) in a.Cubes)
        {
            if (key.StartsWith("__", StringComparison.Ordinal) || !b.Contains(key)) continue;
            var y = b[key];
            if (x.DataKind != y.DataKind || x.Axes.Count != y.Axes.Count) continue;
            double peak = 0, diff = 0;
            // a PSD in dB: bins far below its peak are round-off (−400 dB), whose relative difference means nothing
            double floor = key == "PSD" ? x.RealValues.Where(double.IsFinite).DefaultIfEmpty(0).Max() - 150 : double.NegativeInfinity;
            if (x.DataKind == DataKind.Complex)
            {
                var u = x.ComplexValues; var v = y.ComplexValues;
                for (int i = 0; i < u.Length; i++) { if (double.IsNaN(u[i].Real)) continue; peak = Math.Max(peak, u[i].Magnitude); diff = Math.Max(diff, (u[i] - v[i]).Magnitude); }
            }
            else
            {
                var u = x.RealValues; var v = y.RealValues;
                for (int i = 0; i < u.Length; i++)
                {
                    if (double.IsNaN(u[i]) && double.IsNaN(v[i]) || double.IsInfinity(u[i]) && u[i] == v[i] || u[i] < floor) continue;
                    peak = Math.Max(peak, Math.Abs(u[i])); diff = Math.Max(diff, Math.Abs(u[i] - v[i]));
                }
            }
            // a phase in degrees that is zero to round-off (a CW's AM-PM, 8e-10°) is held to 1e-9° absolute, not to its own peak
            if (key is "AMPM" || key.EndsWith("_deg", StringComparison.Ordinal)) peak = Math.Max(peak, 1);
            double rel = peak > 0 ? diff / peak : diff;
            if (rel > worst) { worst = rel; name = key; }
        }
        return (worst, name);
    }


    [Theory]
    [InlineData("rays")]
    [InlineData("cdl")]
    public void Gate1And2_TheReplacedArrayEqualsTheSteppedOne_AndStepsNoneOfItsParts(string kind)
    {
        string multipath = kind == "rays" ? TwoRays() : "Multipath=cdl Profile=CDL-C DelaySpread=100e-9 Seed=1 Drop=1";
        var stepped = Run(Bench(multipath), replace: false);
        Assert.Equal(0, EnvelopeChannel.ReplacedParts);
        var replaced = Run(Bench(multipath), replace: true);
        int parts = EnvelopeChannel.ReplacedParts;
        var (worst, name) = Compare(stepped, replaced);
        var iout = replaced["Pavg_dBm"].RealValues[0];
        output.WriteLine($"{kind}: {parts} parts replaced by the feed; worst relative difference {worst:G3} ('{name}'); Pavg {iout:G8} dBm");
        Assert.True(iout > -150, $"nothing reaches the feed ({iout} dBm)");
        Assert.True(parts > 256, $"{parts} parts replaced");                 // the array, its feed and 256 channel cells
        Assert.True(worst <= 1e-9, $"'{name}': {worst:G3}");
    }

    /// <summary>Gate 2 — a reusing drop sweep into a 32 × 32 receive array: the transmit pin solved once, every drop's receive side the
    /// array's feed alone (the array, its feed and its 1024 channel cells out of the run). 64 × 64 does the same in 43 s, too long for
    /// the routine tier; its cost is in src/Engine/RESOLVED.md.</summary>
    [Fact]
    public void Gate2_ADropSweepInto32x32_SolvesTheReceiveSideAsAOnePort()
    {
        string text = "drop = 1\n" + Bench("Fading=rayleigh fD=50 Seed=3 Drop=drop").Replace("Rows=16 Cols=16", "Rows=32 Cols=32")
                      + "\nanalysis MC type=parametric_sweep Var=drop Start=1 Stop=3 Step=1 Inner=ENV1\n";
        var circuit = PreparedCircuit.FromText(text, _dir, _dir);
        var ds = CircuitRF.Engine.ParametricSweepEngine.Run(circuit.Tb!.Analyses.OfType<CircuitRF.Core.Design.ParametricSweepAnalysis>().Single(),
                                                            circuit.Lib!, circuit.Tb!, null, _dir);
        output.WriteLine($"counters {EnvelopeDropReuse.LastCounters}; parts replaced in the last run {EnvelopeChannel.ReplacedParts}");
        Assert.Equal((1, 2, 0), EnvelopeDropReuse.LastCounters);
        Assert.Equal(1024 + 2, EnvelopeChannel.ReplacedParts);
        Assert.True(ds["Pavg_dBm"].RealValues.All(double.IsFinite));
    }

    /// <summary>Gate 3 — the couplings evaluate each pattern class once per ray: a 2 × 2 array of one isolated element through CDL-C's rays
    /// evaluates its pattern exactly once per ray, whatever the element count — and once more toward the line of sight, which the pin end's
    /// polarization is matched along.</summary>
    [Fact]
    public void Gate3_CouplingsEvaluateEachPatternClassOncePerRay()
    {
        var circuit = PreparedCircuit.FromText($"""
            MOD_Source:S1 f1 0 Fc=5.9e9 Pavg=0 Z=50 Ohm Record=20e-6 Carrier[1]="CW;Off=0;P=0"
            PhasedArray:PA1 f1 Record="{Record("e1")}" dx=25.86 mm dy=25.86 mm Mode=feed Rows=4 Cols=4
            PropagationChannel:CH1 rx Tx=PA1 Distance=100 TxHeight=1.5 RxHeight=1.5 f0=5.9 GHz Multipath=cdl Profile=CDL-C DelaySpread=100e-9 Seed=7
            R:RL rx 0 R=50 Ohm
            """, _dir, _dir);
        var nl = new Elaborator(circuit.Lib!).Elaborate(circuit.Tb!);
        var ch = (PropagationChannelModel)nl.Components.Single(c => c.InstancePath == "CH1").Model;
        int rays = ch.At(5.9e9).Rays.Count;
        long before = PropagationChannelModel.PatternEvaluations;
        var couplings = ch.InstanceCouplings(5.9e9);
        long evaluated = PropagationChannelModel.PatternEvaluations - before;
        output.WriteLine($"{rays} rays, 16 elements: {evaluated} pattern evaluations");
        Assert.Equal(rays, couplings.Length);
        Assert.Equal(rays + 1, evaluated);
    }

    [Fact]
    public void Gate2_ANoisyRun_KeepsTheArray()
    {
        _ = Run(Bench(TwoRays()).Replace("Tol=1e-12", "Tol=1e-12 Noise=yes"), replace: true);
        Assert.Equal(0, EnvelopeChannel.ReplacedParts);
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CircuitRF.Core;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist.Spice;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine.Transient;
using Xunit;

namespace CircuitRF.Ui.Tests.Transient;

/// <summary>
/// brief TRN-6 gates: the SPICE deck writer round-trips through circuitRF's own reader, refuses by parameter and lists every
/// refusal at once, names nets apart after case folding, writes the same bytes as a process as in process, is the only writer
/// of the dialect, fits TXL and CPL exactly at their frequency with an independently recomputed |ΔS|, and classifies every
/// discontinuity. Synthetic benches only.
/// </summary>
public sealed class SpiceDeckWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("trn6-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private sealed record Written(SpiceDeck Deck, ElaboratedNetlist Netlist, PreparedCircuit Circuit, string Path);

    private Written Deck(string cnl, string name = "b.cnl")
    {
        string path = System.IO.Path.Combine(_dir, name);
        File.WriteAllText(path, cnl);
        var c = PreparedCircuit.FromFile(path, _dir);
        Assert.True(c.ReadError is null, c.ReadError);
        var nl = new Elaborator(c.Lib!) { BaseDirectory = _dir }.Elaborate(c.Tb!);
        var deck = SpiceDeckWriter.Write(new SpiceDeckRequest
        {
            Netlist = nl, Analysis = c.Tb!.Analyses.OfType<TransientAnalysis>().First(), BenchName = "b", Version = "test",
            Bench = c.Tb, Library = c.Lib, Fits = new TransientSpiceFits(),
        });
        return new Written(deck, nl, c, path);
    }

    private static ElaboratedNetlist ReadBack(SpiceDeck deck)
    {
        Assert.True(deck.Ok, string.Join("\n", deck.Refusals));
        var read = SpiceNetlistReader.ReadDeck(deck.Text!, "deck");
        var (lib, tb, why) = SpiceModelNetlist.DeckBench(read, "deck");
        Assert.True(why is null, why + "\n" + string.Join("\n", read.Notes));
        return new Elaborator(lib).Elaborate(tb!);
    }

    // ── gate 1 ───────────────────────────────────────────────────────────

    private const string EveryExactEntry = """
        Vdc:V1 a 0 Vdc=1
        R:R1 a b R=50
        L:L1 b c L=1n
        L:L2 c 0 L=2n
        Mutual:M1 Inductor1="L1" Inductor2="L2" M=0.5n
        C:C1 c 0 C=1p IC=0.2
        VCCS:G1 k 0 a 0 G=1m
        VCVS:E1 m 0 a 0 E=2
        R:Rm m 0 R=1k
        IProbe:IP1 f g
        R:R2 g 0 R=1
        Port:PT1 e 0 Num=1 Z=75
        TLIN:TL1 a e Z=50 E=90 deg F=1 GHz
        Idc:I1 k 0 Idc=1m
        R:R3 k 0 R=1k
        V_Pulse:VP1 p 0 V1=0 V2=1 Delay=1n Rise=1n Fall=2n Width=3n Period=20n
        R:Rp p 0 R=1k
        I_Pulse:IP2 q 0 I1=0 I2=1m Delay=0 Rise=1n Fall=1n Width=5n Period=20n
        R:Rq q 0 R=1k
        V_PWL:VW w 0 Points="0 0, 1n 1, 2n 0.5"
        R:Rw w 0 R=1k
        V_Exp:VE x 0 V1=0 V2=1 Delay1=1n Tau1=2n Delay2=5n Tau2=3n
        R:Rx x 0 R=1k
        V_SFFM:VS y 0 Offset=0 Amplitude=1 Carrier=1 GHz ModIndex=2 Signal=10 MHz
        R:Ry y 0 R=1k
        V_Expr:VX z 0 Expr="sin(2*pi*1e8*time)"
        R:Rz z 0 R=1k
        V_1Tone:V2 f 0 Freq=1 GHz V=1 Vdc=0.5 Delay=1n Damping=1e7
        SDD:D1 f 0 I[1,0]=1e-12*(exp(_v1/0.025)-1)
        Diode:D2 f 0 Is=2e-14 N=1.1 Cj0=1p
        Transformer:T1 a 0 n 0 N=2
        R:Rn n 0 R=50
        P1Tone:P1 s 0 Pavl=0 dBm Z=50 Freq=1 GHz
        R:Rs s 0 R=50
        VSwitch:S1 s 0 a 0 Ron=1 Roff=1e6 Von=1 Voff=0
        analysis TR1 type=tran Stop=10n Step=10p InitialState=zero
        """;

    [Fact]
    public void Gate1_EveryExactEntryWrittenAndReadBack_ElaboratesToTheSameNetlist()
    {
        var w = Deck(EveryExactEntry);
        var back = ReadBack(w.Deck);
        var names = w.Deck.Names;
        var deckOf = names.Instances.ToDictionary(x => x.Path, x => x.Deck);
        ElaboratedComponent Back(string path) => back.Components.Single(c => c.InstancePath == deckOf[path]);
        string NodeName(ElaboratedNetlist nl, int i) => i == 0 ? "0" : nl.Nodes.NameOf(i);
        double P(ElaboratedComponent c, string k) => c.Parameters[k].Kind == ValueKind.Real ? c.Parameters[k].AsReal() : c.Parameters[k].AsComplex().Real;
        static void Near(double a, double b, string what) =>
            Assert.True(Math.Abs(a - b) <= 1e-12 * Math.Max(Math.Abs(a), Math.Abs(b)) + 1e-300, $"{what}: {a:R} vs {b:R}");

        // the entries the reader turns back into one component: the type the table says, its nets, its values
        string[] idioms = ["T1", "P1", "S1"];
        foreach (var o in w.Netlist.Components.Where(c => !idioms.Contains(c.InstancePath)))
        {
            var b = Back(o.InstancePath);
            Assert.Equal(SpiceElementTable.For(o.ComponentType)?.ReadsBackAs ?? o.ComponentType, b.ComponentType);
            int nets = o.ComponentType == "Mutual" ? 0 : o.Model is DiodeModel ? 2 : o.Nodes.Length;
            for (int k = 0; k < nets; k++)
                Assert.Equal(names.Net(NodeName(w.Netlist, o.Nodes[k])), NodeName(back, b.Nodes[k]));

            switch (o.Model)
            {
                case ResistorModel: Near(P(o, "R"), P(b, "R"), o.InstancePath); break;
                case PortModel: Near(P(o, "Z"), P(b, "R"), o.InstancePath); break;
                case CapacitorModel: Near(P(o, "C"), P(b, "C"), o.InstancePath); Near(P(o, "IC"), P(b, "IC"), o.InstancePath + " IC"); break;
                case InductorModel: Near(P(o, "L"), P(b, "L"), o.InstancePath); break;
                case MutualInductanceModel:
                    Near(P(o, "M"), P(b, "M"), o.InstancePath);
                    Assert.Equal(deckOf["L1"], b.Parameters["Inductor1"].AsString().Trim('"'));
                    break;
                case VccsModel: Near(P(o, "G"), P(b, "G"), o.InstancePath); break;
                case VcvsModel: Near(P(o, "E"), P(b, "E"), o.InstancePath); break;
                case VdcModel: Near(P(o, "Vdc"), P(b, "Vdc"), o.InstancePath); break;
                case IdcModel: Near(P(o, "Idc"), P(b, "Idc"), o.InstancePath); break;
                case WaveformSourceModel ow:
                {
                    var bw = (WaveformSourceModel)b.Model;
                    for (int k = 0; k <= 400; k++) { double t = 10e-9 * k / 400; Near(ow.ValueAt(t), bw.ValueAt(t), $"{o.InstancePath} at {t}"); }
                    break;
                }
                case ToneSourceModelBase ot:
                {
                    var bt = (ToneSourceModelBase)b.Model;
                    Near(ot.DcValue, bt.DcValue, o.InstancePath + " dc");
                    Assert.True((ot.Tones[0].Phasor - bt.Tones[0].Phasor).Magnitude <= 1e-12, o.InstancePath + " phasor");
                    Near(P(o, "Delay"), P(b, "Delay"), o.InstancePath + " Delay");
                    Near(P(o, "Damping"), P(b, "Damping"), o.InstancePath + " Damping");
                    break;
                }
                case IUniformLine ol:
                    foreach (double f in new[] { 1e8, 1e9, 7e9 })
                    {
                        var (za, ga) = ol.LineAt(f);
                        var (zb, gb) = ((IUniformLine)b.Model).LineAt(f);
                        Near(za, zb, o.InstancePath + " Z0");
                        Assert.True((ga - gb).Magnitude <= 1e-12 * ga.Magnitude, o.InstancePath + " γl");
                    }
                    break;
                case SddModel or DiodeModel:
                    foreach (double v in new[] { -0.5, 0.3, 0.6 })
                    {
                        var ro = o.Evaluate(new PortVoltages([v]));
                        var rb = b.Evaluate(new PortVoltages([v]));
                        Near(ro.I[0], rb.I[0], $"{o.InstancePath} I at {v}");
                        Near(ro.Q[0], rb.Q[0], $"{o.InstancePath} Q at {v}");
                    }
                    break;
            }
        }

        // the idioms read back as what they were written as: the transformer as a VCVS, a sensor and a current-controlled
        // source; the P1Tone as a tone behind its Z; the switch as a behavioural conductance on the same four nets
        string listing = string.Join(", ", back.Components.Select(c => $"{c.InstancePath}:{c.ComponentType}"));
        Assert.True(back.Components.Any(c => c.ComponentType == "VCVS" && c.InstancePath == deckOf["T1"]), $"{deckOf["T1"]} in {listing}");
        Assert.Contains(back.Components, c => c.Model is ToneSourceModelBase && c.InstancePath == deckOf["P1"]);
        var sw = back.Components.Single(c => c.InstancePath == deckOf["S1"]);
        Assert.Equal("SDD", sw.ComponentType);
    }

    // ── gate 2 ───────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_ADiodeParameterWithNoCounterpart_IsRefusedByName_AndWrittenAtItsDefault()
    {
        var off = Deck("""
            Vdc:V1 a 0 Vdc=0.7
            Diode:D1 a 0 Is=1e-14 Isr=1e-12
            analysis TR1 type=tran Stop=1n
            """);
        Assert.False(off.Deck.Ok);
        Assert.Contains(off.Deck.Refusals, r => r.Message.StartsWith("D1: Isr has no counterpart in the SPICE dialect's diode", StringComparison.Ordinal));

        var atDefault = Deck("""
            Vdc:V1 a 0 Vdc=0.7
            Diode:D1 a 0 Is=1e-14 Isr=0
            analysis TR1 type=tran Stop=1n
            """, "d.cnl");
        Assert.True(atDefault.Deck.Ok, string.Join("\n", atDefault.Deck.Refusals));
        Assert.DoesNotContain("ISR", atDefault.Deck.Text!);
    }

    // ── gate 3 ───────────────────────────────────────────────────────────

    private const string ThreeUntranslatable = """
        V_1Tone:V1 a 0 Freq=1 GHz V=1
        MLIN:ML1 a b W=400e-6 L=5e-3 H=200e-6 T=35e-6 Er=3.66 TanD=0.004 Sigma=5.8e7
        SnP:X1 b c NumPorts=2 File="two.s2p"
        FET_Curtice:Q1 c d 0 Vto=-2 Beta=0.002
        R:R1 d 0 R=50

        """;

    [Fact]
    public void Gate3_AMicrostripAnSnpAndACurticeFet_AreAllListedAndNothingIsWritten()
    {
        File.WriteAllText(System.IO.Path.Combine(_dir, "two.s2p"), "# GHz S RI R 50\n1 0.1 0 0.9 0 0.9 0 0.1 0\n2 0.1 0 0.9 0 0.9 0 0.1 0\n");
        var refused = Deck(ThreeUntranslatable + "analysis TR1 type=tran Stop=10n\n");
        Assert.Null(refused.Deck.Text);
        Assert.Equal(["ML1", "X1", "Q1"], refused.Deck.Refusals.Select(r => r.Instance));

        var rlgc = Deck(ThreeUntranslatable + "analysis TR1 type=tran Stop=10n SpiceLines=rlgc\n", "rlgc.cnl");
        Assert.Null(rlgc.Deck.Text);
        Assert.Equal(["X1", "Q1"], rlgc.Deck.Refusals.Select(r => r.Instance));
    }

    // ── gate 4 ───────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_OutAndOutAndADottedNet_MapToDistinctLegalNamesAndBack()
    {
        var w = Deck("""
            Vdc:V1 Out 0 Vdc=1
            R:R1 Out out R=1
            R:R2 out a.b R=1
            R:R3 a.b gnd R=1
            R:R4 gnd 0 R=1
            analysis TR1 type=tran Stop=1n
            """);
        Assert.True(w.Deck.Ok);
        var names = w.Deck.Names;
        string[] nets = ["Out", "out", "a.b", "gnd"];
        var deck = nets.Select(names.Net).ToArray();
        Assert.Equal(deck.Length, deck.Select(d => d.ToUpperInvariant()).Distinct().Count());
        Assert.All(deck, d => Assert.Matches("^[A-Za-z0-9_]+$", d));
        Assert.DoesNotContain(deck, d => d.Equals("gnd", StringComparison.OrdinalIgnoreCase) || d == "0");
        for (int i = 0; i < nets.Length; i++) Assert.Equal(nets[i], names.NetOf(deck[i]));
        foreach (var d in deck) Assert.Contains($"*   net {d} = ", w.Deck.Text!);
    }

    // ── gate 5 ───────────────────────────────────────────────────────────

    [Fact]
    public void Gate5_NetlistToCir_AsAProcess_IsTheInProcessWriterByteForByte()
    {
        string cnl = System.IO.Path.Combine(_dir, "p.cnl");
        File.WriteAllText(cnl, EveryExactEntry);
        string cir = System.IO.Path.Combine(_dir, "p.cir");
        var run = RunCli("netlist", cnl, "-o", cir);
        Assert.True(run.ExitCode == 0, run.StdErr);

        string full = System.IO.Path.GetFullPath(cnl);
        var circuit = CircuitRF.Cli.SpiceDeckVerb.Prepare(full, CircuitRF.Cli.DocumentKind.Netlist);
        var (deck, why) = CircuitRF.Cli.SpiceDeckVerb.Deck(circuit.Lib!, circuit.Tb!, full, null);
        Assert.Null(why);
        Assert.Equal(new UTF8Encoding(false).GetBytes(deck!.Text!), File.ReadAllBytes(cir));

        // a refusal exits 1 and writes nothing
        File.WriteAllText(cnl, "Diode:D1 a 0 Isr=1e-12\nVdc:V1 a 0 Vdc=1\nanalysis TR1 type=tran Stop=1n\n");
        File.Delete(cir);
        var refused = RunCli("netlist", cnl, "-o", cir);
        Assert.Equal(1, refused.ExitCode);
        Assert.False(File.Exists(cir));
        Assert.Contains("Isr has no counterpart", refused.StdErr + refused.StdOut);
    }

    // ── gate 6 ───────────────────────────────────────────────────────────

    [Fact]
    public void Gate6_TheDeckIsWrittenOnlyBySpiceDeckWriter()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(System.IO.Path.Combine(root, "circuitRF.slnx"))) root = Directory.GetParent(root)!.FullName;
        var writers = new List<string>();
        foreach (var file in Directory.EnumerateFiles(System.IO.Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}") ||
                file.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}")) continue;
            string code = Regex.Replace(File.ReadAllText(file), @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);
            if (Regex.IsMatch(code, "\"\\.tran |\"\\.options |pre_osdi")) writers.Add(System.IO.Path.GetFileName(file));
        }
        Assert.Equal(["SpiceDeckWriter.cs"], writers.Distinct());
    }

    // ── gate 7 ───────────────────────────────────────────────────────────

    /// <summary>A line's S11 and S21 between 50 Ω ports, from its Z matrix (Z11 = Zc coth γl, Z21 = Zc csch γl) — written here
    /// independently of SpiceLineFit's ABCD form.</summary>
    private static (Complex S11, Complex S21) IndependentS(Complex zc, Complex gl)
    {
        Complex z11 = zc / Complex.Tanh(gl), z21 = zc / Complex.Sinh(gl);
        const double z0 = 50;
        Complex a = z11 + z0, d = a * a - z21 * z21;
        return (((z11 - z0) * a - z21 * z21) / d, 2 * z21 * z0 / d);
    }

    [Fact]
    public void Gate7_TxlReproducesTheMicrostripAtItsFrequency_AndItsLargestDeltaSIsRecomputedIndependently()
    {
        var w = Deck("""
            V_1Tone:V1 a 0 Freq=2 GHz V=1
            R:R0 a b R=50
            MLIN:ML1 b c W=400e-6 L=20e-3 H=200e-6 T=35e-6 Er=3.66 TanD=0.004 Sigma=5.8e7
            R:RL c 0 R=50
            analysis TR1 type=tran Stop=10n SpiceLines=rlgc
            """);
        Assert.True(w.Deck.Ok, string.Join("\n", w.Deck.Refusals));
        var rep = Assert.Single(w.Deck.Lines);
        Assert.Equal("TXL", rep.Element);
        Assert.Equal(2e9, rep.FitHz);
        var line = (IUniformLine)w.Netlist.Components.Single(c => c.InstancePath == "ML1").Model;
        var (zc, gl) = line.LineAt(rep.FitHz);
        var (zt, gt) = SpiceLineFit.Of(rep.Rlgc, rep.FitHz);
        Assert.True((zt - zc).Magnitude <= 1e-9 * zc, $"Zc {zt} vs {zc}");
        Assert.True((gt - gl).Magnitude <= 1e-9 * gl.Magnitude, $"γl {gt} vs {gl}");

        double worst = 0;
        foreach (double f in SpiceLineFit.Grid(rep.LoHz, rep.HiHz))
        {
            var (zm, gm) = line.LineAt(f);
            var (m11, m21) = IndependentS(zm, gm);
            var (zx, gx) = SpiceLineFit.Of(rep.Rlgc, f);
            var (t11, t21) = IndependentS(zx, gx);
            worst = Math.Max(worst, Math.Max((m11 - t11).Magnitude, (m21 - t21).Magnitude));
        }
        Assert.True(Math.Abs(worst - rep.MaxDeltaS) <= 1e-12 + 1e-9 * worst, $"{worst} vs {rep.MaxDeltaS}");
        Assert.Contains("txl R=", w.Deck.Text!);
    }

    [Fact]
    public void Gate7_CplReproducesBothModesOfACoupledPairAtItsFrequency()
    {
        var w = Deck("""
            V_1Tone:V1 a 0 Freq=1 GHz V=1
            R:R0 a p1 R=50
            CLIN:TC p1 p2 p3 p4 Ze=70 Ohm Zo=35 Ohm E=90 deg Eo=80 deg F=1 GHz
            R:R2 p2 0 R=50
            R:R3 p3 0 R=50
            R:R4 p4 0 R=50
            analysis TR1 type=tran Stop=10n SpiceLines=rlgc
            """);
        Assert.True(w.Deck.Ok, string.Join("\n", w.Deck.Refusals));
        var rep = Assert.Single(w.Deck.Lines);
        Assert.Equal("CPL", rep.Element);
        var pair = (ICoupledPair)w.Netlist.Components.Single(c => c.InstancePath == "TC").Model;
        var m = pair.PairAt(rep.FitHz);
        var s = rep.Rlgc; var mu = rep.Mutual!.Value;
        var even = new SpiceRlgc(s.R + mu.R, s.L + mu.L, s.G + mu.G, s.C + mu.C);
        var odd = new SpiceRlgc(s.R - mu.R, s.L - mu.L, s.G - mu.G, s.C - mu.C);
        foreach (var (mode, z, g) in new[] { (even, m.Ze, m.GammaLengthEven), (odd, m.Zo, m.GammaLengthOdd) })
        {
            var (zt, gt) = SpiceLineFit.Of(mode, rep.FitHz);
            Assert.True((zt - z).Magnitude <= 1e-9 * z, $"Z {zt} vs {z}");
            Assert.True((gt - g).Magnitude <= 1e-9 * g.Magnitude, $"γl {gt} vs {g}");
        }
        Assert.Contains("CPL length=1", w.Deck.Text!);
    }

    // ── gate 8 ───────────────────────────────────────────────────────────

    [Fact]
    public void Gate8_EveryDiscontinuityIsClassified_AGapRoundTrips_AndATeeWithArmsIsRefused()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(System.IO.Path.Combine(root, "circuitRF.slnx"))) root = Directory.GetParent(root)!.FullName;
        string doc = File.ReadAllText(System.IO.Path.Combine(root, "docs", "design", "spice-models.md"));
        int table = doc.IndexOf("### 15.6", StringComparison.Ordinal);
        Assert.True(table > 0, "spice-models.md §15.6 is the discontinuity table");
        string section = doc[table..];
        foreach (var t in new[] { "MBEND", "MGAP", "MTEE", "MCROSS", "MMITER", "MCURVE" })
            Assert.Contains($"| `{t}`", section);

        var gap = Deck("""
            Vdc:V1 a 0 Vdc=1
            R:R1 a b R=50
            MGAP:G1 b c W=0.5e-3 S=0.1e-3 H=0.508e-3 Er=9.9
            R:R2 c 0 R=50
            analysis TR1 type=tran Stop=1n
            """);
        var back = ReadBack(gap.Deck);
        var lumped = ((ILumpedNetwork)gap.Netlist.Components.Single(c => c.InstancePath == "G1").Model).Lumped!;
        var caps = back.Components.Where(c => c.Model is CapacitorModel).Select(c => c.Parameters["C"].AsReal()).Order().ToArray();
        Assert.Equal(lumped.Elements.Select(e => e.Value).Order().ToArray(), caps);

        var tee = Deck("""
            Vdc:V1 a 0 Vdc=1
            MTEE:T1 a b c W1=0.5e-3 W2=0.5e-3 W3=0.5e-3 H=0.508e-3 T=5e-6 Er=9.9 TanD=0 Sigma=5.8e7 L1=2e-3 L2=2e-3 L3=2e-3
            R:R2 b 0 R=50
            R:R3 c 0 R=50
            analysis TR1 type=tran Stop=1n
            """, "tee.cnl");
        var r = Assert.Single(tee.Deck.Refusals);
        Assert.Equal("T1", r.Instance);
        Assert.Contains("varies with frequency", r.Reason);
    }

    // ── SpiceFit (R-trn6-8) ──────────────────────────────────────────────

    [Fact]
    public void SpiceFit_WritesAnSnpAsCircuitRfsFit_AndLabelsIt()
    {
        File.WriteAllText(System.IO.Path.Combine(_dir, "two.s2p"), "# GHz S RI R 50\n1 0.1 0 0.9 0 0.9 0 0.1 0\n2 0.1 0 0.9 0 0.9 0 0.1 0\n");
        var w = Deck("""
            V_1Tone:V1 a 0 Freq=1 GHz V=1
            R:R0 a b R=50
            SnP:X1 b c NumPorts=2 File="two.s2p"
            R:R1 c 0 R=50
            analysis TR1 type=tran Stop=10n SpiceFit=yes
            """);
        Assert.True(w.Deck.Ok, string.Join("\n", w.Deck.Refusals));
        Assert.Contains(w.Deck.Fits, f => f.Instance == "X1" && f.Sentence.Contains("circuitRF's fit of X1"));
        Assert.Contains("X1: circuitRF's rational fit, not the element itself", w.Deck.Text!);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        string cliDir = typeof(SpiceDeckWriterTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}

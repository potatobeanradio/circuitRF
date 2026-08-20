using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// MN-2's stamp, through the real engines (match.md §8.2, §8.3).
///
/// <para><b>The oracle is a hand-built netlist of R/L/C primitives</b>, generated from the very
/// element list the component is built from and wired with its own explicit intermediate nodes. That
/// is deliberately a DIFFERENT node structure from the one the component stamps — the component
/// carries one branch per series ARM, the hand-built one carries a node per series ELEMENT — so
/// agreement between them is a statement about the topology and the DC/AC limits, not a tautology.
/// It is also MN-5's precondition: flatten writes exactly this netlist.</para>
/// </summary>
public class MatchStampTests(ITestOutputHelper output)
{
    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    // ── the two netlists under comparison ─────────────────────────────────────

    /// <summary>One <c>Match</c> between <c>p1</c> and <c>p2</c>.</summary>
    private static string MatchInstance(MatchDesign design, string name = "MN1",
                                        string p1 = "p1", string p2 = "p2")
        => $"Match:{name}  {p1} {p2}  Design={MatchEmbedding.Encode(design)}";

    /// <summary>
    /// The same ladder as ordinary primitives, one component per element and one node per series
    /// element.
    /// </summary>
    /// <param name="includeAbsorbed">
    /// When true the two termination-supplied reactances are written out TOO — the exact mistake
    /// §0.1 warns about, kept here as a fixture so a test can show the two answers differ.
    /// </param>
    private static string HandBuilt(MatchNetwork network, bool includeAbsorbed = false,
                                    string tag = "H", string p1 = "p1", string p2 = "p2")
    {
        var elements = network.Elements.Where(e => includeAbsorbed || !e.IsAbsorbed).ToList();
        int seriesCount = elements.Count(e => !e.IsShunt);

        var sb = new StringBuilder();
        string current = p1;
        int seen = 0, mint = 0;
        foreach (var e in elements)
        {
            string type = e.Type == ElementType.L ? "L" : "C";
            if (e.IsShunt)
            {
                sb.AppendLine($"{type}:{tag}{e.Name}  {current} 0  {type}={N(e.Value)}");
                continue;
            }

            string next = ++seen == seriesCount ? p2 : $"__{tag}n{++mint}";
            sb.AppendLine($"{type}:{tag}{e.Name}  {current} {next}  {type}={N(e.Value)}");
            current = next;
        }
        return sb.ToString();
    }

    /// <summary>match.md §4.9's interstage problem — the acceptance anchor, restated here rather than
    /// shared with <c>Core.Tests</c> (which this project does not reference).</summary>
    private static MatchDesign GoldenDesign() => new()
    {
        F1 = 3.3e9,
        F2 = 5.0e9,
        Order = 4,
        Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(200.0, ReactanceKind.C, TerminationTopology.Parallel, 0.125e-12),
        Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 10e-12),
    };

    private static MatchNetwork Ladder(MatchDesign design)
    {
        var rebuilt = MatchRebuild.Rebuild(design);
        Assert.Null(rebuilt.Refusal);
        return rebuilt.Network!;
    }

    /// <summary>A 2-port S-parameter sweep of a body wired between two 50 ohm Terms.</summary>
    private static Complex[,][] SweepS(string body, double[] frequencies)
    {
        string cnl = $"""
            Term:T1  p1 0  Num=1 Z=50
            Term:T2  p2 0  Num=2 Z=50
            {body}
            """;
        var ds = SParameterEngine.Run(Elaborate(cnl), frequencies);
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

    private static readonly double[] Band =
        [1e9, 2e9, 3.3e9, 3.8e9, 4.06202e9, 4.5e9, 5.0e9, 7e9, 12e9];

    // ── §0.2: elementwise, not an ABCD block ──────────────────────────────────

    /// <summary>
    /// A <c>Match</c> and the equivalent hand-built ladder give the same S-parameters to <b>1e-12</b>
    /// — the gate MN-5's flatten has to keep meeting, and the reason the stamp is elementwise rather
    /// than a cascaded 2x2 block.
    /// </summary>
    [Theory]
    [InlineData(false)]   // the golden §4.9 interstage design — two absorbed ends, a CFano, 2 series arms
    [InlineData(true)]    // the shipped default — 50/50 resistive, nothing absorbed, 1 series arm
    public void AMatch_AndTheHandBuiltLadder_AgreeToOnePartInATrillion(bool useDefault)
    {
        var design = useDefault ? MatchEmbedding.DefaultDesign() : GoldenDesign();
        var ladder = Ladder(design);

        var component = SweepS(MatchInstance(design), Band);
        var handBuilt = SweepS(HandBuilt(ladder), Band);

        double worst = WorstDifference(component, handBuilt);
        output.WriteLine($"worst |ΔS| over {Band.Length} frequencies: {worst:E3}");
        Assert.True(worst < 1e-12, $"component and hand-built ladder differ by {worst:E3}");
    }

    // ── §0.1: the absorbed elements are NOT in the component ──────────────────

    /// <summary>
    /// The invertible mistake, pinned from both sides: the component matches the ladder WITHOUT the
    /// two termination reactances, and does not match the ladder WITH them.
    ///
    /// <para>The second half is what makes this a test. Stamping the absorbed elements produces a
    /// component that looks perfect in the Designer's preview — the preview draws the whole ladder —
    /// and is a different circuit the moment it is placed, with no error anywhere.</para>
    /// </summary>
    [Fact]
    public void AMatch_OmitsTheAbsorbedTerminationReactances()
    {
        var design = GoldenDesign();
        var ladder = Ladder(design);
        Assert.Equal(2, ladder.Elements.Count(e => e.IsAbsorbed));

        var component = SweepS(MatchInstance(design), Band);
        var without = SweepS(HandBuilt(ladder), Band);
        var with = SweepS(HandBuilt(ladder, includeAbsorbed: true, tag: "W"), Band);

        double matches = WorstDifference(component, without);
        double differs = WorstDifference(component, with);
        output.WriteLine($"vs ladder WITHOUT the absorbed reactances: {matches:E3}");
        output.WriteLine($"vs ladder WITH    the absorbed reactances: {differs:E3}");

        Assert.True(matches < 1e-12, $"the component must BE the ladder minus them ({matches:E3})");
        Assert.True(differs > 1e-3,
            "stamping the absorbed reactances must be measurably a different circuit " +
            $"(worst |ΔS| only {differs:E3})");
    }

    // ── §0.2 reason 1: DC ─────────────────────────────────────────────────────

    /// <summary>
    /// <b>Series arms are DC opens.</b> Both ends of this design are SERIES-topology, so the ladder
    /// runs series-shunt-series and each pin's only path to anywhere is through a series arm — which
    /// contains a capacitor. Nothing can flow: the source sees no load at all, so there is no drop
    /// across its own resistor, and the far end sits at zero. A cascaded ABCD block would have
    /// diverged here instead.
    /// </summary>
    [Fact]
    public void AtDc_SeriesArmsAreOpens()
    {
        var design = new MatchDesign
        {
            F1 = 1e9,
            F2 = 2e9,
            Order = 3,
            Term1 = Termination.Resistive(50.0, TerminationTopology.Series),
            Term2 = Termination.Resistive(50.0, TerminationTopology.Series),
        };
        var ladder = Ladder(design);
        Assert.False(ladder.Elements[0].IsShunt);
        Assert.False(ladder.Elements[^1].IsShunt);

        string cnl = $"""
            Vdc:VS  in 0  Vdc=1
            R:Rs    in p1  R=50
            {MatchInstance(design)}
            R:RL    p2 0  R=50
            """;
        var netlist = Elaborate(cnl);
        var result = NonlinearDcEngine.Run(netlist);
        Assert.True(result.Converged, $"DC did not converge (residual {result.FinalResidual:G3})");

        double V(string net)
        {
            int n = netlist.Nodes.GetOrAssign(net);
            return n == 0 ? 0.0 : result.NodeVoltages[n - 1];
        }

        output.WriteLine($"V(p1)={V("p1"):G6}  V(p2)={V("p2"):G6}");
        // 1e-9, not 1e-12: the DC engine's own gmin leaks ~1e-12 S across every open branch, which
        // is 5e-11 of the source here. The claim being made is "open", not "open to machine epsilon".
        Assert.Equal(1.0, V("p1"), 1e-9);    // no current, so no drop across Rs
        Assert.Equal(0.0, V("p2"), 1e-9);    // and nothing arrives at the far side
    }

    /// <summary>
    /// <b>The shipped default DC-solves, and the reason it does is a choice.</b>
    /// </summary>
    /// <remarks>
    /// A Norton transform replaces one element with a pi of three of its own KIND. Applied to an
    /// inductor pair, the products are three ideal inductors in a loop — a loop of ideal shorts, and
    /// therefore a singular MNA system: the DC solve returns a residual of 1 and never converges,
    /// while the S-parameter sweep runs perfectly. The default's own FIRST-ranked solution (L1/L2) is
    /// exactly that shape, which is why <c>MatchEmbedding.DefaultDesign</c> prefers a CAPACITIVE
    /// transform — three capacitors put a series capacitor in the middle branch, a DC open, and the
    /// network stays solvable.
    ///
    /// <para>This is a claim about the DEFAULT and not about transforms: an inductor Norton transform
    /// is a legitimate thing to apply and the solutions list still offers every one. What a shipped
    /// default may not be is a circuit that refuses one of the analyses it is placed to run.</para>
    /// </remarks>
    [Fact]
    public void TheShippedDefault_IsNotAnInductorLoop_AndDcSolves()
    {
        var design = MatchEmbedding.DefaultDesign();
        Assert.NotEmpty(design.Transforms);

        var network = MatchRebuild.Rebuild(design).Network;
        Assert.NotNull(network);
        var products = network!.Elements.Where(e => e.Name.Contains("_N", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(products);
        Assert.All(products, e => Assert.Equal(ElementType.C, e.Type));

        var netlist = Elaborate($"""
            Vdc:VS  in 0  Vdc=1
            R:Rs    in p1  R=50
            {MatchInstance(design)}
            R:RL    p2 0  R=10
            """);
        var result = NonlinearDcEngine.Run(netlist);
        Assert.True(result.Converged, $"the shipped default did not DC-solve (residual {result.FinalResidual:G3})");
    }

    /// <summary>
    /// <b>Shunt arms are DC shorts</b>, exactly — a bare inductor to ground. The shipped default
    /// starts and ends with a shunt arm, so both pins are pinned to zero and the source's whole
    /// current flows through the component.
    /// </summary>
    [Fact]
    public void AtDc_ShuntArmsAreShorts()
    {
        var design = MatchEmbedding.DefaultDesign();
        string cnl = $"""
            Vdc:VS  in 0  Vdc=1
            R:Rs    in p1  R=50
            {MatchInstance(design)}
            R:RL    p2 0  R=50
            """;
        var netlist = Elaborate(cnl);
        var result = NonlinearDcEngine.Run(netlist);
        Assert.True(result.Converged, $"DC did not converge (residual {result.FinalResidual:G3})");

        double V(string net)
        {
            int n = netlist.Nodes.GetOrAssign(net);
            return n == 0 ? 0.0 : result.NodeVoltages[n - 1];
        }

        output.WriteLine($"V(p1)={V("p1"):E3}  V(p2)={V("p2"):E3}");
        Assert.Equal(0.0, V("p1"), 1e-12);
        Assert.Equal(0.0, V("p2"), 1e-12);
    }

    /// <summary>The DC solution agrees with the hand-built ladder's, which is the same claim stated
    /// without a hand-derived expected value.</summary>
    [Fact]
    public void AtDc_TheComponentAndTheHandBuiltLadderAgree()
    {
        var design = GoldenDesign();
        var ladder = Ladder(design);

        double Solve(string body)
        {
            string cnl = $"""
                Vdc:VS  in 0  Vdc=1
                R:Rs    in p1  R=50
                {body}
                R:RL    p2 0  R=1e3
                """;
            var netlist = Elaborate(cnl);
            var r = NonlinearDcEngine.Run(netlist);
            Assert.True(r.Converged, $"DC did not converge (residual {r.FinalResidual:G3})");
            int n = netlist.Nodes.GetOrAssign("p2");
            return n == 0 ? 0.0 : r.NodeVoltages[n - 1];
        }

        Assert.Equal(Solve(HandBuilt(ladder)), Solve(MatchInstance(design)), 1e-12);
    }

    // ── §0.2 reason 2: HB ─────────────────────────────────────────────────────

    /// <summary>
    /// An HB run <b>including the DC harmonic</b>, with a <c>Match</c> in the linear part, converges
    /// and reproduces the hand-built ladder's whole spectrum.
    ///
    /// <para>This is what an internal node that carries its own harmonic content buys. Eliminating it
    /// locally would be exact at DC and wrong here — the documented reason <c>DiodeModel</c>'s
    /// internal node is not collapsed either.</para>
    /// </summary>
    [Fact]
    public void InHb_AMatchInTheLinearPart_MatchesTheHandBuiltLadder()
    {
        // NOT the golden design: its basis ladder presents 1.68 ohms, so a source with any real
        // output impedance sees it as a short and the whole comparison collapses to two zero
        // spectra agreeing perfectly. This one keeps the shape that matters here — TWO series arms,
        // so there IS an internal node carrying its own harmonic content, and one ABSORBED element
        // so the skip runs in HB too — at an impedance a driver can actually swing into.
        var design = new MatchDesign
        {
            F1 = 3e9,
            F2 = 5e9,
            Order = 4,
            Term1 = Termination.Resistive(50.0),
            Term2 = new Termination(50.0, ReactanceKind.C, TerminationTopology.Parallel, 0.4e-12),
        };
        var ladder = Ladder(design);
        output.WriteLine($"R1={ladder.R1:G6} R2={ladder.R2:G6}");
        foreach (var e in ladder.Elements)
            output.WriteLine($"  {e.Name,-6} {e.Type} shunt={e.IsShunt} absorbed={e.IsAbsorbed} {e.Value:G6}");

        static string Circuit(string body) => $"""
            V_1Tone:Vs   in 0  Vdc=0  Freq=4e9  V=2  Phase=0
            R:Rs         in a  R=5
            Diode:D1     a 0
            {body}
            R:Rload  p2 0  R=50

            analysis HB1  type=hb  Tone=4e9  MaxHarm=3  Tol=1e-10
            """;

        DataCube Run(string body)
        {
            var (lib, tb) = new CnlReader().Read(Circuit(body));
            var netlist = new Elaborator(lib).Elaborate(tb);
            var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
            var result = new HbEngine(netlist, tb).Run(HbEngine.Resolve(hba, netlist.ResolvedGlobals));
            Assert.True(result.Converged, "HB did not converge");
            return ((DataSet)result)["V"];
        }

        var a = Run(MatchInstance(design, p1: "a"));
        var b = Run(HandBuilt(ladder, p1: "a"));

        int ia = Array.FindIndex(a.Axes[0].Labels!, n => n == "p2");
        int ib = Array.FindIndex(b.Axes[0].Labels!, n => n == "p2");
        Assert.True(ia >= 0 && ib >= 0, "p2 must appear in both node axes");

        double worst = 0.0;
        for (int k = 0; k < a.Axes[1].Values!.Length; k++)
        {
            var va = (Complex)a[ia, k];
            var vb = (Complex)b[ib, k];
            output.WriteLine($"k={k}: component {va:G6}   hand-built {vb:G6}");
            worst = Math.Max(worst, (va - vb).Magnitude);
        }

        // The DC harmonic is the one this test exists for — assert it is really in the spectrum, and
        // that it is DECIDED by the component. The diode rectifies, so node `a` carries a real DC
        // offset; p2 reads exactly zero because the ladder's last arm is a shunt inductor and a shunt
        // inductor is an exact DC short. Get that limit wrong — treat the shunt arm as an open — and
        // p2 would follow `a` instead.
        Assert.Equal(0.0, a.Axes[1].Values![0]);
        int iaNode = Array.FindIndex(a.Axes[0].Labels!, n => n == "a");
        Assert.True(((Complex)a[iaNode, 0]).Magnitude > 0.05,
            "the diode must actually rectify, or the DC harmonic proves nothing");
        Assert.Equal(0.0, ((Complex)a[ia, 0]).Magnitude, 1e-9);

        // And that there is something to compare: two all-zero spectra agree perfectly and say
        // nothing at all.
        Assert.True(((Complex)a[ia, 1]).Magnitude > 0.1,
            $"the fundamental at p2 is only {((Complex)a[ia, 1]).Magnitude:E3} V — the circuit is not driven");
        Assert.True(((Complex)a[ia, 2]).Magnitude > 1e-3,
            "the second harmonic must be non-trivial, or the nonlinearity is not being exercised");
        Assert.True(worst < 1e-9, $"spectra differ by {worst:E3} V");
    }

    // ── the internal-net mint ─────────────────────────────────────────────────

    /// <summary>
    /// Two cascaded instances of ONE design behave as two independent networks. Sharing an internal
    /// net would connect their middles together and still solve, so the check is electrical as well
    /// as by name: cascading two must not equal one.
    /// </summary>
    [Fact]
    public void TwoCascadedInstances_DoNotShareAnInternalNet()
    {
        var design = GoldenDesign();
        var ladder = Ladder(design);

        string cnl = $"""
            Term:T1  p1 0  Num=1 Z=50
            Term:T2  p3 0  Num=2 Z=50
            {MatchInstance(design, "MN1", "p1", "p2")}
            {MatchInstance(design, "MN2", "p2", "p3")}
            """;
        var netlist = Elaborate(cnl);
        Assert.NotEqual(netlist.Nodes.GetOrAssign("__match_MN1_0"),
                        netlist.Nodes.GetOrAssign("__match_MN2_0"));

        var cascade = SParameterEngine.Run(Elaborate(cnl), Band);
        var handBuilt = SParameterEngine.Run(Elaborate($"""
            Term:T1  p1 0  Num=1 Z=50
            Term:T2  p3 0  Num=2 Z=50
            {HandBuilt(ladder, tag: "A", p1: "p1", p2: "p2")}
            {HandBuilt(ladder, tag: "B", p1: "p2", p2: "p3")}
            """), Band);

        double worst = 0.0;
        for (int f = 0; f < Band.Length; f++)
            worst = Math.Max(worst,
                ((Complex)cascade["S"][f, 1, 0] - (Complex)handBuilt["S"][f, 1, 0]).Magnitude);
        output.WriteLine($"worst |ΔS21| over the cascade: {worst:E3}");
        Assert.True(worst < 1e-12, $"the cascade differs from two hand-built ladders by {worst:E3}");
    }

    // ── the headless promise ──────────────────────────────────────────────────

    /// <summary>
    /// A <c>.cnl</c> containing a <c>Match</c> runs headless under the real <c>Cli sparam</c> verb
    /// (match.md §2.1), and produces the bandpass its design describes.
    ///
    /// <para><b>A separate process on purpose.</b> Every other test here calls the engine directly,
    /// which cannot tell whether the base64 payload survives the netlist FILE — and it very nearly
    /// does not: <c>CnlReader</c>'s spaced-assignment merge glues the next token onto any value
    /// ending in <c>=</c>, which is exactly why <c>MatchEmbedding</c> strips the base64 padding. This
    /// is the only test that would notice it coming back.</para>
    ///
    /// <para><b>The DLL is exec'd directly rather than run through <c>dotnet run</c>.</b> A nested
    /// MSBuild inside a <c>dotnet test</c> that already holds this repository's build locks does not
    /// finish — measured, not assumed: the first version of this test hung past three minutes and had
    /// to be killed. A <c>ProjectReference</c> with <c>ReferenceOutputAssembly="false"</c> is what
    /// guarantees the CLI is already built here, so there is nothing left to build — which also makes
    /// this cheap: <b>173 ms measured</b>, against ~3 s for the same run through <c>dotnet run</c>.</para>
    /// </summary>
    [Fact]
    public void ACnlContainingAMatch_RunsHeadlessUnderCliSparam()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(MatchStampTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string cliDll = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(cliDll), $"the CLI was not built beside these tests: {cliDll}");

        string path = Path.Combine(Path.GetTempPath(), $"match-cli-{Guid.NewGuid():N}.cnl");
        // Port 2 is terminated in 10 ohms, matching the shipped default's own far end (2026-08-19).
        // Measuring a 50-to-10 transformer into 50 ohms measures the mismatch it exists to remove.
        File.WriteAllText(path, $"""
            Term:T1  p1 0  Num=1 Z=50
            Term:T2  p2 0  Num=2 Z=10
            {MatchInstance(MatchEmbedding.DefaultDesign())}

            analysis SP1  type=sparam  start=1.0 GHz  stop=3.0 GHz  step=0.5 GHz
            """);

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string arg in new[] { cliDll, "sparam", path })
                psi.ArgumentList.Add(arg);

            using var proc = System.Diagnostics.Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            output.WriteLine(stdout);
            if (stderr.Length > 0) output.WriteLine("stderr: " + stderr);
            Assert.Equal(0, proc.ExitCode);

            string touchstone = Path.ChangeExtension(path, ".s2p");
            Assert.True(File.Exists(touchstone), "Cli sparam wrote no Touchstone file");

            // In band it passes; an octave below it does not. Reading the numbers back is what makes
            // this an end-to-end check rather than an exit-code check.
            var rows = File.ReadAllLines(touchstone)
                           .Where(l => l.Length > 0 && (char.IsDigit(l[0]) || l[0] == '-'))
                           .Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                           .ToList();
            Assert.Equal(5, rows.Count);

            double S21Db(string[] r) => 10.0 * Math.Log10(
                double.Parse(r[3], CultureInfo.InvariantCulture) * double.Parse(r[3], CultureInfo.InvariantCulture) +
                double.Parse(r[4], CultureInfo.InvariantCulture) * double.Parse(r[4], CultureInfo.InvariantCulture));

            output.WriteLine(string.Join("  ", rows.Select(r => $"{r[0]}GHz {S21Db(r):F2}dB")));
            // 1.0, 1.5, 2.0, 2.5, 3.0 GHz — the band is 1.8-2.2.
            Assert.True(S21Db(rows[2]) > -0.2, "2 GHz is mid-band");
            Assert.True(S21Db(rows[0]) < -20.0, "1.0 GHz is well below the band");
            Assert.True(S21Db(rows[4]) < -20.0, "3.0 GHz is well above the band");
            File.Delete(touchstone);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}

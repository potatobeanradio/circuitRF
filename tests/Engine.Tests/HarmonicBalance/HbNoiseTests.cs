using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// Brief NZ-5: noise in harmonic balance, on the conversion matrix the small-signal sweep builds. Each gate against a
/// number from outside this code path: the S-parameter noise analysis in the small-signal limit, an ideal multiplier's
/// closed form, an ideal switch's, and the run with no key.
/// </summary>
public sealed class HbNoiseTests(ITestOutputHelper output)
{
    private static string N(double x) => x.ToString("R", CultureInfo.InvariantCulture);

    private static (ElaboratedNetlist Nl, TestBench Tb) Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl, "tb");
        return (new Elaborator(lib).Elaborate(tb), tb);
    }

    private static DataSet Hb(string cnl)
    {
        var (nl, tb) = Elaborate(cnl);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single();
        var p = HbEngine.Resolve(hba, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
        return new HbEngine(nl, tb).Run(p).DataSet;
    }

    /// <summary>The S-parameter noise run, its DC pre-pass converged to 1e-14 A: the default 1e-6 A leaves a 150 kΩ base
    /// bias ~2.5e-7 V short, which is 1e-5 of gm — ten times the gate's own tolerance, and none of it noise.</summary>
    private static DataSet Sp(string cnl)
    {
        var (nl, tb) = Elaborate(cnl);
        var spa = tb.Analyses.OfType<SParameterAnalysis>().Single();
        return SParameterEngine.Run(nl, spa.Expand(nl.ResolvedGlobals),
            new AnalysisSettings { NonlinearAbsTol = 1e-14 }.WithNoise(true));
    }

    // ── gate 1: the small-signal limit ───────────────────────────────────────

    /// <summary>A one-BJT LNA: base and collector shot noise, flicker, and Rb's thermal noise, with a lossy collector load
    /// and a base bias resistor. The HB drive is the source at <paramref name="pavl"/> dBm and <paramref name="toneHz"/>.</summary>
    private static string Lna(double pavl, double toneHz, string analysis) => $"""
        P1Tone:P1  in 0  Num=1 Z=50 Ohm Pavl={N(pavl)} dBm Freq={N(toneHz)}
        C:C1  in b  C=20e-12
        R:RB  vcc b  R=150e3
        BJT_NPN:Q1  c b 0  Is=1e-15 Bf=100 Rb=20 Kf=1e-14 Cje=0.5e-12 Cjc=0.2e-12
        L:LC  vcc c  L=100e-9
        R:RC  vcc c  R=400
        C:C2  c out  C=20e-12
        Port:P2  out 0  Num=2 Z=50 Ohm
        Vdc:V1  vcc 0  Vdc=3
        {analysis}
        """;

    private const string LnaSs = "SSStart=1.13 SSStop=2.33 SSNpts=4 SSUnit=GHz";

    /// <summary>With the drive at −200 dBm nothing converts between sidebands and every device density is its DC value, so
    /// NF is the S-parameter noise analysis's, at the same frequencies, to 1e-6 dB.</summary>
    [Fact]
    public void Gate1_SmallSignalLimit_EqualsSParameterNoise()
    {
        var hb = Hb(Lna(-200, 1e9, $"analysis HB1 type=hb Tone=1e9 MaxHarm=3 Tol=1e-9 {LnaSs} Noise=yes"));
        var sp = Sp(Lna(-200, 1e9, "analysis SP1 type=sparam start=1.13 stop=2.33 npts=4 Unit=GHz Noise=yes"));
        var a = hb["NF"].RealValues;
        var b = sp["NF"].RealValues;
        for (int k = 0; k < a.Length; k++)
        {
            output.WriteLine($"{hb["NF"].Axes[0].Values[k] / 1e9:G4} GHz: HB NF {a[k]:R} dB, S-parameter NF {b[k]:R} dB");
            Assert.Equal(b[k], a[k], 1e-6);
        }
        Assert.False(hb.Contains("NFdsb"));   // same frequency in and out: one figure
    }

    // ── gate 2: an ideal multiplier ──────────────────────────────────────────

    /// <summary>
    /// An SDD multiplier <c>i_IF = g·v_RF·v_LO</c> drawing nothing from the RF node, pumped by an ideal LO of amplitude A,
    /// RF and IF terminated in 50 Ω ports, everything at T0. With the RF node open, the IF tone is <c>(gA/2)·V_RF</c> from
    /// each of the two sidebands f_IF ± f_LO, so <c>Gc = (gA/2)²·4·R_L·R_s</c>, the image brings exactly as much noise as
    /// the signal (F_SSB = 2, 3.01 dB; F_DSB = 1) and NFsb is 50 % at each. A matched shunt R at the RF node adds its own
    /// noise at both sidebands as well: F_SSB = 2·(1 + R_s/R) and Gc falls by (R/(R + R_s))².
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gate2_IdealMultiplier_MeetsTheClosedForm(bool shunt)
    {
        const double g = 0.02, A = 1.0, R = 50;
        string cnl = $"""
            temp = 16.85
            Port:P1  rf 0  Num=1 Z=50 Ohm
            {(shunt ? "R:RS  rf 0  R=50" : "")}
            V_1Tone:VLO  lo 0  Freq=1e9 V={N(A)}
            SDD:X1  rf 0  lo 0  if 0  I[1,0]=0*_v1  I[2,0]=0*_v2  I[3,0]={N(g)}*_v1*_v2
            Port:P2  if 0  Num=2 Z=50 Ohm
            analysis HB1 type=hb Tone=1e9 MaxHarm=3 Tol=1e-12 SSStart=0.1 SSStop=0.3 SSNpts=3 SSUnit=GHz Noise=yes NoiseSideband=1
            """;
        var ds = Hb(cnl);
        double rp = shunt ? R * R / (2 * R) : R;
        double gc = Math.Pow(g * A / 2, 2) * 4 * R * R * Math.Pow(rp / R, 2);
        double fSsb = shunt ? 4 : 2;
        var sbAxis = ds["NFsb"].Axes[1];
        int up = Array.IndexOf(sbAxis.Values, 1.0), dn = Array.IndexOf(sbAxis.Values, -1.0);
        int nsb = sbAxis.Values.Length;
        for (int k = 0; k < 3; k++)
        {
            output.WriteLine($"NF {ds["NF"].RealValues[k]:R} dB, NFdsb {ds["NFdsb"].RealValues[k]:R} dB, Gc {ds["Gc"].RealValues[k]:R} dB, " +
                             $"NFsb(+1) {ds["NFsb"].RealValues[k * nsb + up]:R} %");
            Assert.Equal(10 * Math.Log10(fSsb), ds["NF"].RealValues[k], 1e-9);
            Assert.Equal(10 * Math.Log10(fSsb / 2), ds["NFdsb"].RealValues[k], 1e-9);
            Assert.Equal(10 * Math.Log10(gc), ds["Gc"].RealValues[k], 1e-9);
            Assert.Equal(50.0, ds["NFsb"].RealValues[k * nsb + up], 1e-9);
            Assert.Equal(50.0, ds["NFsb"].RealValues[k * nsb + dn], 1e-9);
            // F = Nout / (kT0·Gc): the two cubes check each other.
            Assert.Equal(ds["Fn"].RealValues[k],
                ds["Nout"].RealValues[k] / (NoiseMetrics.KT0 * Math.Pow(10, ds["Gc"].RealValues[k] / 10)), 1e-12);
        }
    }

    // ── gate 3: a switching diode ────────────────────────────────────────────

    /// <summary>
    /// One diode in series between a 50 Ω RF port and a 50 Ω IF port, switched by a 20 V LO in series with the RF side.
    /// As an ideal switch of duty d it passes <c>v_IF = s(t)·e_s/2</c>, with s(t)'s two-sided coefficients
    /// <c>c_0 = d</c>, <c>c_k = sin(πkd)/(πk)</c>: the conversion gain is <c>c_1²</c> and, every sideband of the source
    /// resistance reaching the IF through its own <c>c_k</c>, <c>F_SSB = Σ c_k² / c_1²</c> over the retained sidebands
    /// (d = ½: −9.94 dB and 6.86 dB at K = 11).
    ///
    /// <para><b>The closed form's own error.</b> The diode conducts only above its turn-on voltage, ~0.8 V of the 20 V LO, so
    /// its duty is ≈ acos(0.04)/π = 0.487 rather than ½ (−0.1 dB in NF, −0.01 dB in Gc); its on resistance of a few tenths
    /// of an ohm against 100 Ω costs Gc ~0.03 dB; and its own shot noise, which an ideal switch has none of, adds a few per
    /// cent of F − 1 (+0.1 dB). Hence 0.05 dB on Gc and 0.2 dB on NF, and the diode's own share is asserted to be that small
    /// positive amount.</para>
    /// </summary>
    [Fact]
    public void Gate3_SwitchingDiode_MeetsTheIdealSwitch()
    {
        const int K = 11;
        string cnl = $"""
            temp = 16.85
            Port:P1  rf 0  Num=1 Z=50 Ohm
            V_1Tone:VLO  a rf  Freq=1e9 V=20
            Diode:D1  a if  Is=1e-12 N=1
            Port:P2  if 0  Num=2 Z=50 Ohm
            analysis HB1 type=hb Tone=1e9 MaxHarm={K} Tol=1e-9 SSStart=0.1 SSStop=0.1 SSNpts=1 SSUnit=GHz Noise=yes NoiseSideband=1 NoiseContrib=yes
            """;
        var ds = Hb(cnl);
        double c1 = 1 / Math.PI, sum = 0.25;
        for (int k = 1; k <= K; k++) sum += 2 * Math.Pow(Math.Sin(Math.PI * k / 2) / (Math.PI * k), 2);
        double gc = 10 * Math.Log10(c1 * c1), nf = 10 * Math.Log10(sum / (c1 * c1));
        output.WriteLine($"Gc {ds["Gc"].RealValues[0]:F4} dB (switch {gc:F4}), NF {ds["NF"].RealValues[0]:F4} dB (switch {nf:F4})");
        Assert.Equal(gc, ds["Gc"].RealValues[0], 0.05);
        Assert.Equal(nf, ds["NF"].RealValues[0], 0.2);

        var nfc = ds["NFpct"];
        double diode = nfc.RealValues[Array.IndexOf(nfc.Axes[1].Labels!, "D1")];
        output.WriteLine($"D1's share of F − 1: {diode:F3} %");
        Assert.InRange(diode, 0.1, 5);
    }

    // ── gate 4: a blocker ────────────────────────────────────────────────────

    /// <summary>The LNA's gain at the tone at <paramref name="pavl"/> dBm, dB: Pout across the 50 Ω port over Pavl.</summary>
    private static double ToneGainDb(double pavl)
    {
        var ds = Hb(Lna(pavl, 1e9, "analysis HB1 type=hb Tone=1e9 MaxHarm=5 Tol=1e-9"));
        var v = ds["V"];
        int node = Array.IndexOf(v.Axes[0].Labels!, "out"), nh = v.Axes[1].Values.Length;
        var vo = v.ComplexValues[node * nh + 1];
        double pout = vo.Magnitude * vo.Magnitude / (2 * 50);
        return 10 * Math.Log10(pout) + 30 - pavl;
    }

    /// <summary>
    /// The LNA with a blocker: the HB tone is the blocker at 1 GHz, and NF is read at 1.13–2.33 GHz. Its input 1 dB
    /// compression point is found first. NF rises with the blocker, and 30 dB below P1dB it is gate 1's small-signal NF to
    /// 0.01 dB.
    /// </summary>
    [Fact]
    public void Gate4_Blocker_RaisesNf_AndIsSmallSignalWellBelowP1dB()
    {
        double g0 = ToneGainDb(-60), lo = -60, hi = 10;
        for (int i = 0; i < 20; i++)
        {
            double mid = (lo + hi) / 2;
            if (g0 - ToneGainDb(mid) < 1) lo = mid; else hi = mid;
        }
        double p1db = (lo + hi) / 2;
        output.WriteLine($"small-signal gain {g0:F3} dB, input P1dB {p1db:F2} dBm");

        double[] Nf(double pavl) =>
            Hb(Lna(pavl, 1e9, $"analysis HB1 type=hb Tone=1e9 MaxHarm=5 Tol=1e-9 {LnaSs} Noise=yes"))["NF"].RealValues;
        var small = Nf(-200);
        var levels = new[] { p1db - 30, p1db - 10, p1db };
        var nf = levels.Select(Nf).ToArray();
        for (int k = 0; k < small.Length; k++)
        {
            output.WriteLine($"f{k}: small-signal {small[k]:F4} dB; blocker at P1dB−30 {nf[0][k]:F4}, −10 {nf[1][k]:F4}, 0 {nf[2][k]:F4}");
            Assert.Equal(small[k], nf[0][k], 0.01);
            Assert.True(nf[1][k] > nf[0][k] && nf[2][k] > nf[1][k], "NF rises with the blocker");
        }
    }

    // ── two tones ────────────────────────────────────────────────────────────

    /// <summary>Gate 2's multiplier on the two-tone lattice, the second tone a source of zero amplitude: NoiseSideband=1,0
    /// gives the one-tone answer, and the sideband axis is spelled by mixing vector.</summary>
    [Fact]
    public void TwoTones_IdleSecondTone_GivesTheOneToneAnswer()
    {
        var ds = Hb("""
            temp = 16.85
            Port:P1  rf 0  Num=1 Z=50 Ohm
            V_1Tone:VLO  lo 0  Freq=1e9 V=1
            V_1Tone:VB  b 0  Freq=1.3711e9 V=0
            R:RB  b 0  R=50
            SDD:X1  rf 0  lo 0  if 0  I[1,0]=0*_v1  I[2,0]=0*_v2  I[3,0]=0.02*_v1*_v2
            Port:P2  if 0  Num=2 Z=50 Ohm
            analysis HB1 type=hb NumFreqs=2 Tone[1]=1e9 Tone[2]=1.3711e9 MaxMixOrder=3 MaxHarm=3 Tol=1e-12 SSStart=0.1 SSStop=0.3 SSNpts=3 SSUnit=GHz Noise=yes NoiseSideband=1,0
            """);
        for (int k = 0; k < 3; k++)
        {
            Assert.Equal(10 * Math.Log10(2), ds["NF"].RealValues[k], 1e-9);
            Assert.Equal(0.0, ds["NFdsb"].RealValues[k], 1e-9);
            Assert.Equal(0.0, ds["Gc"].RealValues[k], 1e-6);
        }
        var labels = ds["NFsb"].Axes[1].Labels!;
        Assert.Contains("1,0", labels);
        Assert.Contains("-1,0", labels);
    }

    // ── gate 6: no key, no change ────────────────────────────────────────────

    private const string Probed = """
        V_1Tone:Vin  n_s 0  Freq=2e9 V=0.45 Vdc=0.3
        R:Rs  n_s n_g  R=50
        WSProbe:GATE  n_g n_p1
        SDD:D1  n_p1 0  n_p2 0  I[1,0]=0.02*_v1+0.05*_v1^2  I[2,0]=0.03*_v2+0.04*_v1*_v2
        WSProbe:DRAIN  n_p2 n_d
        R:Rd  n_d n_sup  R=200
        V_1Tone:Vsup  n_sup 0  Vdc=3
        Port:P1  n_s 0  Num=1 Z=50 Ohm
        Port:P2  n_d 0  Num=2 Z=50 Ohm
        """;

    /// <summary>With Noise absent, or no, a probed HB run carries no noise cube and every other cube — the WSProbe's
    /// small-signal outputs on the same conversion matrix included — is identical, value for value, to the other.</summary>
    [Fact]
    public void Gate6_NoKey_NoChange()
    {
        const string hb = "analysis HB1 type=hb Tone=2e9 MaxHarm=4 Tol=1e-10 SSStart=0.37 SSStop=3.37 SSNpts=7 SSUnit=GHz";
        var absent = Hb($"{Probed}\n{hb}");
        var no = Hb($"{Probed}\n{hb} Noise=no NoiseIn=1 NoiseOut=2");
        var yes = Hb($"{Probed}\n{hb} Noise=yes");
        string[] noise = ["NF", "Fn", "NFdsb", "Fndsb", "Te", "Nout", "Gc", "NFsb", "NFc", "NFpct"];
        static string[] Text(DataCube c) => c.DataKind == DataKind.Complex
            ? [.. c.ComplexValues.Select(x => x.Real.ToString("R") + "," + x.Imaginary.ToString("R"))]
            : [.. c.RealValues.Select(x => x.ToString("R"))];
        Assert.DoesNotContain(absent.Cubes.Keys, noise.Contains);
        Assert.Equal(absent.Cubes.Keys.OrderBy(k => k), no.Cubes.Keys.OrderBy(k => k));
        Assert.Contains("wsp", absent.Cubes.Keys);
        Assert.Contains("NF", yes.Cubes.Keys);
        foreach (var (key, cube) in absent.Cubes)
        {
            Assert.Equal(Text(cube), Text(no[key]));
            Assert.Equal(Text(cube), Text(yes[key]));   // Noise=yes adds cubes and touches none of these
        }
    }

    /// <summary>The keys survive the .cnl round trip, and a line without them writes none.</summary>
    [Fact]
    public void Keys_RoundTrip_AndAreAbsentByDefault()
    {
        var (_, tb) = new CnlReader().Read(
            "R:R1 a 0 R=50\nanalysis HB1 type=hb Tone=1e9 SSStart=1 SSStop=2 SSNpts=3 SSUnit=GHz Noise=yes NoiseIn=3 NoiseOut=4 NoiseSideband=-1 NoiseContrib=yes", "tb");
        var hb = tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single();
        Assert.True(hb.Noise);
        Assert.True(hb.NoiseContrib);
        Assert.Equal(("3", "4", "-1"), (hb.NoiseInExpr, hb.NoiseOutExpr, hb.NoiseSidebandExpr));
        string written = CnlWriter.Write(tb);
        var (_, back) = new CnlReader().Read(written, "tb");
        var hb2 = back.Analyses.OfType<HarmonicBalanceAnalysis>().Single();
        Assert.Equal((hb.NoiseExpr, hb.NoiseInExpr, hb.NoiseOutExpr, hb.NoiseSidebandExpr, hb.NoiseContribExpr),
                     (hb2.NoiseExpr, hb2.NoiseInExpr, hb2.NoiseOutExpr, hb2.NoiseSidebandExpr, hb2.NoiseContribExpr));

        var (_, plain) = new CnlReader().Read("R:R1 a 0 R=50\nanalysis HB1 type=hb Tone=1e9", "tb");
        Assert.DoesNotContain("Noise", CnlWriter.Write(plain));
    }

    /// <summary>Noise=yes without the small-signal sweep is a refusal naming the keys it needs.</summary>
    [Fact]
    public void NoiseWithoutSsSweep_IsRefused()
    {
        var (nl, tb) = Elaborate("R:R1 a 0 R=50\nanalysis HB1 type=hb Tone=1e9 Noise=yes");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            HbEngine.Resolve(tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single(), nl.ResolvedGlobals));
        Assert.Contains("SSStart", ex.Message);
    }
}

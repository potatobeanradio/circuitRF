using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using RfCore.Data;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// Brief NZ-1: each part's share of the S-parameter noise (<c>NCs</c>, <c>NFc</c>, <c>NFpct</c>), a part's own
/// <c>Noise=no</c>, <c>Temp=</c> on an S-parameter block, a resistive <c>Z_Port</c> thermal, and the noise model table
/// <c>explain</c> prints. The references are the run's own total (the shares must sum to it) and closed forms.
/// </summary>
public sealed class NoiseContributionTests(ITestOutputHelper output) : IDisposable
{
    private const double K = NoiseMetrics.Boltzmann, T0 = NoiseMetrics.T0;

    private readonly string _dir = Directory.CreateTempSubdirectory("noise-contrib-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static string NoiseDir()
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(Path.Combine(dir, "testdata", "noise"))) return Path.Combine(dir, "testdata", "noise");
        throw new DirectoryNotFoundException("testdata/noise not found above " + AppContext.BaseDirectory);
    }

    /// <summary>A testdata/noise bench with <paramref name="keys"/> appended to its analysis line.</summary>
    private static string Bench(string name, string keys = "Noise=yes NoiseContrib=yes") => string.Join('\n',
        File.ReadAllText(Path.Combine(NoiseDir(), name + ".cnl")).Split('\n')
            .Select(l => l.TrimStart().StartsWith("analysis SP1", StringComparison.Ordinal) ? l.TrimEnd() + " " + keys : l));

    private sealed record Run(DataSet Ds, double[] Freqs, ElaboratedNetlist Nl);

    private Run RunCnl(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl, sourceDirectory: NoiseDir());
        var nl    = new Elaborator(lib) { BaseDirectory = _dir }.Elaborate(tb);
        var spa   = tb.Analyses.OfType<SParameterAnalysis>().First();
        var freqs = spa.Expand(nl.ResolvedGlobals);
        return new Run(SParameterEngine.Run(nl, freqs, AnalysisSettings.Default.WithNoiseOf(spa)), freqs, nl);
    }

    private static string[] Parts(DataSet ds, string cube) => ds[cube].Axes[1].Labels!;

    // ── gate 1: the shares sum to the total ───────────────────────────────────────

    /// <summary>Σ NCs = Cs and Σ NFc = F − 1 to 1e-12 relative, on mixed temperatures, a coupled group, a controlled
    /// source and the Friis cascade (two file-data blocks between a resistive pad). Σ NFpct is 100.</summary>
    [Theory]
    [InlineData("ngspice_mixed_temp")]
    [InlineData("ngspice_coupled_l")]
    [InlineData("ngspice_vccs_amp")]
    [InlineData("friis_matched")]
    public void Shares_SumToTheTotal(string bench)
    {
        var r = RunCnl(Bench(bench));
        var cs = r.Ds["Cs"];
        var ncs = r.Ds["NCs"];
        int np = ncs.Axes[1].Length;
        double worstCs = 0, worstF = 0;
        for (int fi = 0; fi < r.Freqs.Length; fi++)
        {
            double scale = 0;
            for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) scale = Math.Max(scale, ((Complex)cs[fi, i, j]).Magnitude);
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                {
                    Complex sum = 0;
                    for (int p = 0; p < np; p++) sum += (Complex)ncs[fi, p, i, j];
                    worstCs = Math.Max(worstCs, (sum - (Complex)cs[fi, i, j]).Magnitude / scale);
                }

            double fm1 = r.Ds["Fn"].RealValues[fi] - 1, nfc = 0, pct = 0;
            for (int p = 0; p < np; p++) { nfc += (double)r.Ds["NFc"][fi, p]; pct += (double)r.Ds["NFpct"][fi, p]; }
            worstF = Math.Max(worstF, Math.Abs(nfc - fm1) / fm1);
            Assert.Equal(100.0, pct, 1e-9);
        }
        output.WriteLine($"{bench}: {np} parts ({string.Join(", ", Parts(r.Ds, "NCs"))}); Σ NCs vs Cs {worstCs:E2}, Σ NFc vs F − 1 {worstF:E2}");
        Assert.True(worstCs < 1e-12, $"Σ NCs vs Cs: {worstCs:E2}");
        Assert.True(worstF < 1e-12, $"Σ NFc vs F − 1: {worstF:E2}");
        Assert.Equal(Parts(r.Ds, "NCs"), Parts(r.Ds, "NFc"));
    }

    /// <summary>A Mutual and its inductors are ONE entry on the part axis, labelled with the members joined by '+'.</summary>
    [Fact]
    public void CoupledGroup_IsOneEntry()
    {
        var r = RunCnl(Bench("ngspice_coupled_l"));
        Assert.Equal(["R1", "L1+L2+K1", "R2", "C1"], Parts(r.Ds, "NCs"));
    }

    // ── gate 2: a known split ─────────────────────────────────────────────────────

    /// <summary>
    /// The matched 6 dB π pad at T: each resistor's share of F − 1 in closed form, with H = (Rsh2‖Z0)/(Rse + Rsh2‖Z0) the
    /// pad's voltage transfer from its input node (the source delivers kT0·H² to the load), and both end nodes seeing
    /// Z0/2. The input shunt gives (T/T0)·Z0/Rsh1; the output shunt (T/T0)·(Z0/Rsh2)/H²; the series arm, a noise voltage
    /// in the loop Zl + Rse + Zr with Zl = Z0‖Rsh1 and Zr = Z0‖Rsh2, gives (T/T0)·4·Rse·Zr²/((Zl + Rse + Zr)²·Z0·H²).
    /// </summary>
    [Theory]
    [InlineData("pad6_290K", 290.0)]
    [InlineData("pad6_400K", 400.0)]
    public void MatchedPad_SharesMeetTheClosedForm(string bench, double kelvin)
    {
        const double Z0 = 50, Rsh = 150.47602375372455, Rse = 37.35187703354018;
        static double Par(double a, double b) => a * b / (a + b);
        double zr = Par(Rsh, Z0), zl = Par(Rsh, Z0), h = zr / (Rse + zr), t = kelvin / T0;
        double[] expected =
        [
            t * Z0 / Rsh,
            t * 4 * Rse * zr * zr / ((zl + Rse + zr) * (zl + Rse + zr) * Z0 * h * h),
            t * (Z0 / Rsh) / (h * h),
        ];

        var r = RunCnl(Bench(bench, "Noise=yes"));
        Assert.Equal(["Rsh1", "Rse", "Rsh2"], Parts(r.Ds, "NFc"));
        for (int fi = 0; fi < r.Freqs.Length; fi++)
            for (int p = 0; p < 3; p++)
                Assert.Equal(expected[p], (double)r.Ds["NFc"][fi, p], 1e-12);
        output.WriteLine($"{bench}: NFc = {string.Join(", ", expected.Select(x => x.ToString("G6", CultureInfo.InvariantCulture)))}; " +
                         $"series/shunt = {expected[1] / expected[0]:G6}");
    }

    // ── gate 3: Noise=no ──────────────────────────────────────────────────────────

    private static string QuietPad(params string[] quiet) => $"""
        temp = 16.85
        Port:P1  p1 0  Num=1 Z=50.0 Ohm
        Port:P2  p2 0  Num=2 Z=50.0 Ohm
        R:Rsh1  p1  0  R=150.47602375372455 Ohm{(quiet.Contains("Rsh1") ? " Noise=no" : "")}
        R:Rse  p1  p2  R=37.35187703354018 Ohm{(quiet.Contains("Rse") ? " Noise=no" : "")}
        R:Rsh2  p2  0  R=150.47602375372455 Ohm{(quiet.Contains("Rsh2") ? " Noise=no" : "")}
        analysis SP1 type=sparam start=1.0 stop=3.0 npts=3 Unit=GHz Noise=yes NoiseContrib=yes
        """;

    /// <summary>Noise=no on every resistor: NF is 0 dB exactly, Cs is exactly zero, there is no part axis, and the
    /// noiseless warning names nobody — the switch is the user's statement, not a gap.</summary>
    [Fact]
    public void NoiseOff_OnEveryResistor_IsSilentAndExact()
    {
        var r = RunCnl(QuietPad("Rsh1", "Rse", "Rsh2"));
        Assert.All(r.Ds["NF"].RealValues.ToArray(), v => Assert.Equal(0.0, v));
        Assert.All(r.Ds["Cs"].ComplexValues.ToArray(), v => Assert.Equal(Complex.Zero, v));
        Assert.False(r.Ds.Contains("NCs"));
        Assert.False(r.Ds.Contains("NFc"));
        Assert.DoesNotContain(r.Nl.Warnings, w => w.StartsWith("Noise:", StringComparison.Ordinal));
    }

    /// <summary>Noise=no on one resistor equals the full run with that resistor's share taken away.</summary>
    [Fact]
    public void NoiseOff_OnOneResistor_RemovesExactlyItsShare()
    {
        var all = RunCnl(QuietPad());
        var one = RunCnl(QuietPad("Rse"));
        Assert.Equal(["Rsh1", "Rsh2"], Parts(one.Ds, "NCs"));
        int rse = Array.IndexOf(Parts(all.Ds, "NCs"), "Rse");
        for (int fi = 0; fi < all.Freqs.Length; fi++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                {
                    var expected = (Complex)all.Ds["Cs"][fi, i, j] - (Complex)all.Ds["NCs"][fi, rse, i, j];
                    var got = (Complex)one.Ds["Cs"][fi, i, j];
                    Assert.True((got - expected).Magnitude <= 1e-12 * ((Complex)all.Ds["Cs"][fi, 0, 0]).Magnitude,
                                $"f{fi} ({i},{j}): {got} vs {expected}");
                }
    }

    /// <summary>The switch is a closed list: a value that is neither yes nor no is refused naming the part.</summary>
    [Fact]
    public void NoiseSwitch_RefusesAnythingButYesOrNo()
    {
        var (lib, tb) = new CnlReader().Read(QuietPad().Replace("R=37.35187703354018 Ohm", "R=37.35187703354018 Ohm Noise=maybe"));
        var ex = Assert.Throws<InvalidOperationException>(() => new Elaborator(lib).Elaborate(tb));
        Assert.Contains("'Rse': Noise=maybe is not yes or no", ex.Message);
    }

    // ── gate 4: Temp= on a passive S-parameter block ──────────────────────────────

    /// <summary>A matched 6 dB attenuator file with no noise data, at its own Temp=: thermal at that temperature, so
    /// Te = T·(L − 1) — at 20 K and at 400 K, against the 300 K ambient it took before Temp= was read.</summary>
    [Theory]
    [InlineData(20.0)]
    [InlineData(400.0)]
    public void SnpTemp_PassiveBlock_IsThermalAtItsOwnTemperature(double kelvin)
    {
        File.WriteAllText(Path.Combine(_dir, "pad6.s2p"), """
            # GHz S RI R 50
            1 0 0 0.5 0 0.5 0 0 0
            5 0 0 0.5 0 0.5 0 0 0
            """);
        string tempC = (kelvin - 273.15).ToString("R", CultureInfo.InvariantCulture);
        var r = RunCnl($"""
            Port:P1  p1 0  Num=1 Z=50 Ohm
            Port:P2  p2 0  Num=2 Z=50 Ohm
            SnP:X1  p1  p2  NumPorts=2 File="{Path.Combine(_dir, "pad6.s2p")}" Temp={tempC}
            analysis SP1 type=sparam start=1 stop=5 npts=3 Unit=GHz Noise=yes
            """);
        Assert.All(r.Ds["Te"].RealValues.ToArray(), te => Assert.Equal(kelvin * (4 - 1), te, 1e-9 * kelvin));
    }

    // ── a resistive Z_Port is thermal ─────────────────────────────────────────────

    /// <summary>A Z_Port whose Z has Re Z ⪰ 0 at the run's frequencies is thermal like the resistor it equals; one with a
    /// negative real part stays named as noiseless.</summary>
    [Fact]
    public void ZPort_ResistiveIsThermal_NegativeStaysNamed()
    {
        const string Head = """
            Port:P1  p1 0  Num=1 Z=50 Ohm
            Port:P2  p2 0  Num=2 Z=50 Ohm
            R:R1  p1 p2  R=20 Ohm
            """;
        const string Tail = "\nanalysis SP1 type=sparam start=1 stop=3 npts=3 Unit=GHz Noise=yes";
        var asR    = RunCnl(Head + "\nR:X1  p2 0  R=100 Ohm" + Tail);
        var asZ    = RunCnl(Head + "\nZ_Port:X1  p2 0  Z[1,1]=100" + Tail);
        var active = RunCnl(Head + "\nZ_Port:X1  p2 0  Z[1,1]=-100" + Tail);

        for (int fi = 0; fi < 3; fi++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    Assert.True(((Complex)asR.Ds["Cs"][fi, i, j] - (Complex)asZ.Ds["Cs"][fi, i, j]).Magnitude
                                <= 1e-12 * ((Complex)asR.Ds["Cs"][fi, 1, 1]).Magnitude, $"f{fi} ({i},{j})");
        Assert.DoesNotContain(asZ.Nl.Warnings, w => w.StartsWith("Noise:", StringComparison.Ordinal));
        Assert.Contains(active.Nl.Warnings, w => w.StartsWith("Noise:", StringComparison.Ordinal) && w.Contains("Z_Port:X1"));
    }

    // ── gate 5: no key, no change ─────────────────────────────────────────────────

    /// <summary>Without NoiseContrib there is no NCs, and a two-port's only additions to PR #50's cubes are NFc and
    /// NFpct; without Noise there is no noise cube at all.</summary>
    [Fact]
    public void NoKey_AddsOnlyTheTwoPortShares()
    {
        string[] pr50 = ["Cs", "NF", "Fn", "NFmin", "Fmin", "Te", "Tmin", "Rn", "Sopt", "Zopt"];
        var off = RunCnl(Bench("pad6_290K", ""));
        var on  = RunCnl(Bench("pad6_290K", "Noise=yes"));
        Assert.DoesNotContain(pr50.Concat(["NCs", "NFc", "NFpct"]), off.Ds.Contains);
        Assert.All(pr50, n => Assert.True(on.Ds.Contains(n), n));
        Assert.False(on.Ds.Contains("NCs"));
        Assert.True(on.Ds.Contains("NFc") && on.Ds.Contains("NFpct"));
    }

    // ── gate 6 (engine half): the table explain prints ────────────────────────────

    /// <summary>One of each row kind but device sources (OsdiNoiseTests holds that one, through the real worker): every
    /// part listed once, thermal at its own resolved temperature, a file block with its file and band, a switched-off
    /// part, a coupled group quieted by one member, a controlled source and a port with their reasons.</summary>
    [Fact]
    public void Table_ListsEveryPartOnceWithItsModel()
    {
        var (lib, tb) = new CnlReader().Read("""
            temp = 16.85
            Port:P1  p1 0  Num=1 Z=50 Ohm
            Port:P2  p2 0  Num=2 Z=50 Ohm
            R:R1  p1 a  R=10 Ohm Temp=126.85
            SnP:X1  a b  NumPorts=2 File="amp1.s2p"
            R:R2  b 0  R=1000 Ohm Noise=no
            L:L1  b 0  L=5 nH R=3 Ohm
            L:L2  p2 0  L=8 nH R=4 Ohm Noise=no
            Mutual:K1  Inductor1="L1" Inductor2="L2" M=4 nH
            VCCS:G1  p2 0 b 0  G=10 mS
            analysis SP1 type=sparam start=1 stop=4 npts=4 Unit=GHz Noise=yes
            """, sourceDirectory: NoiseDir());
        var nl = new Elaborator(lib).Elaborate(tb);
        var rows = SParameterNoise.Table(nl, [1e9, 4e9]).ToDictionary(r => r.Label);

        Assert.Equal(["P1", "P2", "R1", "X1", "R2", "L1+L2+K1", "G1"], rows.Keys);
        Assert.Equal(NoiseModelKind.Thermal, rows["R1"].Kind);
        Assert.Equal(400.0, rows["R1"].Kelvin, 1e-9);
        Assert.Equal(NoiseModelKind.FileData, rows["X1"].Kind);
        Assert.Equal("amp1.s2p", Path.GetFileName(rows["X1"].File));
        Assert.True(rows["X1"].BandLoHz > 0 && rows["X1"].BandHiHz > rows["X1"].BandLoHz);
        Assert.Equal(NoiseModelKind.SwitchedOff, rows["R2"].Kind);
        Assert.Equal(NoiseModelKind.SwitchedOff, rows["L1+L2+K1"].Kind);
        Assert.Contains("L2", rows["L1+L2+K1"].Reason);
        Assert.Equal(NoiseModelKind.Noiseless, rows["G1"].Kind);
        Assert.False(rows["G1"].ByDefinition);
        Assert.Contains("dependent source", rows["G1"].Reason);
        Assert.Equal(NoiseModelKind.Noiseless, rows["P1"].Kind);
        Assert.True(rows["P1"].ByDefinition);
    }
}

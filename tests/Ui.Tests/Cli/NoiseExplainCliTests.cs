using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Cli;

/// <summary>
/// Brief NZ-1, through the CLI as a process: <c>explain --analysis</c> lists the noise model of every part of a
/// <c>Noise=yes</c> analysis, in text and in <c>--json</c>, with the size of <c>NCs</c>; and <c>check</c> reports
/// <c>Noise=no</c> on a part that can never be noisy as INFO. One bench holds one part of each kind but device sources,
/// which need a compiled model's worker (<c>OsdiNoiseTests.Table_NamesTheDeviceSources_AndNoiseNoQuietsTheDevice</c>
/// holds that row against the same table).
/// </summary>
public sealed class NoiseExplainCliTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-noise-explain-" + Guid.NewGuid().ToString("N")[..12]);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private string Bench()
    {
        Directory.CreateDirectory(_root);
        File.Copy(Path.Combine(RepoRoot(), "testdata", "noise", "amp1.s2p"), Path.Combine(_root, "amp1.s2p"), overwrite: true);
        string path = Path.Combine(_root, "bench.cnl");
        File.WriteAllText(path, """
            temp = 16.85
            Port:P1  p1 0  Num=1 Z=50 Ohm Noise=no
            Port:P2  p2 0  Num=2 Z=50 Ohm
            R:R1  p1 a  R=10 Ohm Temp=126.85
            SnP:X1  a b  NumPorts=2 File="amp1.s2p"
            R:R2  b 0  R=1000 Ohm Noise=no
            VCCS:G1  p2 0 b 0  G=10 mS
            R:R3  b p2  R=5 Ohm
            analysis SP1 type=sparam start=1 stop=4 npts=4 Unit=GHz Noise=yes
            """);
        return path;
    }

    private JsonElement Table()
    {
        var run = RunCli("explain", Bench(), "--analysis", "--json");
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);
        using var doc = JsonDocument.Parse(run.StdOut);
        return doc.RootElement.GetProperty("result").GetProperty("explain").GetProperty("analyses")[0]
                  .GetProperty("noiseModel").Clone();
    }

    private static JsonElement Row(JsonElement table, string part)
        => table.GetProperty("parts").EnumerateArray().Single(p => p.GetProperty("part").GetString() == part);

    [Fact]
    public void Thermal_CarriesItsResolvedTemperature()
    {
        var r = Row(Table(), "R1");
        Assert.Equal("thermal", r.GetProperty("model").GetString());
        Assert.Equal(400.0, r.GetProperty("kelvin").GetDouble(), 1e-9);
    }

    [Fact]
    public void FileData_CarriesTheFileAndItsNoiseBand()
    {
        var r = Row(Table(), "X1");
        Assert.Equal("fileData", r.GetProperty("model").GetString());
        Assert.Equal("amp1.s2p", Path.GetFileName(r.GetProperty("file").GetString()));
        Assert.True(r.GetProperty("bandHiHz").GetDouble() > r.GetProperty("bandLoHz").GetDouble());
    }

    [Fact]
    public void SwitchedOff_IsListedNotWarned()
    {
        var r = Row(Table(), "R2");
        Assert.Equal("switchedOff", r.GetProperty("model").GetString());
        Assert.Equal("Noise=no", r.GetProperty("reason").GetString());
    }

    [Fact]
    public void Noiseless_CarriesItsReason()
    {
        var table = Table();
        Assert.Contains("dependent source", Row(table, "G1").GetProperty("reason").GetString());
        Assert.Contains("port", Row(table, "P2").GetProperty("reason").GetString());
        // Every part once; NCs' size stated for the three noisy entries on the part axis (R1, R3 thermal; X1 file data).
        Assert.Equal(7, table.GetProperty("parts").GetArrayLength());
        Assert.Equal(3, table.GetProperty("noisyParts").GetInt32());
        Assert.Equal(16L * 4 * 3 * 4, table.GetProperty("ncsBytes").GetInt64());
        Assert.False(table.GetProperty("noiseContrib").GetBoolean());
    }

    [Fact]
    public void Text_ListsEveryPart_AndCheckSaysTheSwitchOnAPortIsInert()
    {
        string path = Bench();
        var text = RunCli("explain", path, "--analysis");
        Assert.True(text.ExitCode == 0, text.StdErr + text.StdOut);
        output.WriteLine(text.StdOut);
        Assert.Contains("Noise: 3 noisy part(s) on the part axis; NCs 768 B", text.StdOut);
        Assert.Contains("noise R1 (R): thermal at 400 K", text.StdOut);
        Assert.Contains("noise R2 (R): switched off (Noise=no)", text.StdOut);

        var check = RunCli("check", path, "--json");
        Assert.Equal(0, check.ExitCode);
        Assert.Contains("check.noise.inert-switch", check.StdOut);
        Assert.Contains("'P1' has Noise=no and has no noise to switch off", check.StdOut + check.StdErr);
        Assert.DoesNotContain("'R2' has Noise=no", check.StdOut + check.StdErr);
    }

    /// <summary>Brief NZ-3: a system amplifier with NF= is listed as a specified NF, with the value, in JSON and text.</summary>
    [Fact]
    public void Amplifier_IsListedAsSpecifiedNf()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "amp.cnl");
        File.WriteAllText(path, """
            Port:P1  a 0  Num=1 Z=50 Ohm
            Port:P2  b 0  Num=2 Z=50 Ohm
            Amp:A1  a 0 b 0  Gain=20 IP3=200 NF=3
            analysis SP1 type=sparam start=1 stop=2 npts=2 Unit=GHz Noise=yes
            """);
        var json = RunCli("explain", path, "--analysis", "--json");
        Assert.True(json.ExitCode == 0, json.StdErr + json.StdOut);
        using var doc = JsonDocument.Parse(json.StdOut);
        var row = Row(doc.RootElement.GetProperty("result").GetProperty("explain").GetProperty("analyses")[0]
                         .GetProperty("noiseModel"), "A1");
        Assert.Equal("specified", row.GetProperty("model").GetString());
        Assert.Equal(3.0, row.GetProperty("nfDb").GetDouble(), 1e-12);

        var text = RunCli("explain", path, "--analysis");
        output.WriteLine(text.StdOut);
        Assert.Contains("noise A1 (Amp): specified NF 3 dB", text.StdOut);
    }

    /// <summary>
    /// Brief NZ-7: a source's phase-noise mask is simulated by an envelope run only. <c>check</c> reports it under a
    /// steady-state analysis as INFO and exits 0, and a mask that does not read as an error; <c>explain</c> names the
    /// envelope run's seed and the mask.
    /// </summary>
    [Fact]
    public void PhaseNoise_IsInfoUnderHb_AnErrorWhenItDoesNotRead_AndExplainedOnTheEnvelope()
    {
        Directory.CreateDirectory(_root);
        string Write(string name, string mask)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllText(path, $"""
                MOD_Source:S1 s 0 Fc=1e9 Pavg=0 Z=50 Record=0.5e-3 Carrier[1]="CW;BW=0.2MHz;Off=0;P=0" PhaseNoise="{mask}"
                R:RL s 0 R=50 Ohm
                analysis HB1 type=hb Tone=1 ToneUnit=GHz MaxHarm=3
                analysis ENV1 type=env Stop=1e-5 Noise=yes Seed=5
                """);
            return path;
        }
        var good = RunCli("check", Write("good.cnl", "1kHz:-80, 1MHz:-140"), "--json");
        Assert.True(good.ExitCode == 0, good.StdErr + good.StdOut);
        Assert.Contains("check.noise.phase-steady-state", good.StdOut);
        Assert.Contains("HB1 runs at steady state and ignores it", good.StdOut);

        var bad = RunCli("check", Write("bad.cnl", "1kHz-80"), "--json");
        Assert.Equal(1, bad.ExitCode);
        Assert.Contains("check.noise.phase-mask", bad.StdOut);

        var text = RunCli("explain", Path.Combine(_root, "good.cnl"), "--analysis");
        Assert.True(text.ExitCode == 0, text.StdErr + text.StdOut);
        output.WriteLine(text.StdOut);
        Assert.Contains("thermal noise on, noise seed 5, the same at every sweep point", text.StdOut);
        Assert.Contains("phase noise S1: 1kHz:-80, 1MHz:-140 (dBc/Hz)", text.StdOut);
    }

    /// <summary>
    /// The NZ-7 follow-up: Noise=yes on a source. <c>check</c> says a noisy MOD_Source stays noiseless in an S-parameter
    /// noise run, and that an ideal source has no impedance to be noisy, both as INFO; <c>explain</c> names the source and
    /// its temperature on the envelope run.
    /// </summary>
    [Fact]
    public void NoisySource_IsExplained_AndCheckSaysWhereItIsNot()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "src.cnl");
        File.WriteAllText(path, """
            MOD_Source:S1 s 0 Fc=1e9 Pavg=0 Z=50 Record=0.5e-3 Carrier[1]="CW;BW=0.2MHz;Off=0;P=0" Noise=yes Temp=16.85
            Vdc:V1 d 0 Vdc=1 V Noise=yes
            R:RD d 0 R=50 Ohm
            Port:P1 s 0 Num=1 Z=50 Ohm
            Port:P2 d 0 Num=2 Z=50 Ohm
            analysis SP1 type=sparam start=1 stop=2 npts=2 Unit=GHz Noise=yes
            analysis ENV1 type=env Stop=1e-5 Noise=yes
            """);
        var check = RunCli("check", path, "--json");
        Assert.True(check.ExitCode == 0, check.StdErr + check.StdOut);
        Assert.Contains("check.noise.source-steady-state", check.StdOut);
        Assert.Contains("SP1 keeps it noiseless", check.StdOut);
        Assert.Contains("check.noise.source-ideal", check.StdOut);
        Assert.Contains("V1", check.StdOut);
        Assert.Contains("has Noise=yes and no impedance to be noisy", check.StdOut);

        var text = RunCli("explain", path, "--analysis");
        Assert.True(text.ExitCode == 0, text.StdErr + text.StdOut);
        output.WriteLine(text.StdOut);
        Assert.Contains("source impedance thermal: S1 at 290 K", text.StdOut);
    }

    /// <summary>Briefs NZ-5 and NZ-9: what a run would refuse, check refuses first — an HB noise end naming no port, and an
    /// oscillator whose start-up span leaves out its own Fguess. Both passed check before, and the noise one surfaced only
    /// after a solve.</summary>
    [Fact]
    public void Check_RefusesAMissingNoisePort_AndAnOscillatorSpanWithoutItsGuess()
    {
        Directory.CreateDirectory(_root);
        string hb = Path.Combine(_root, "hb.cnl");
        File.WriteAllText(hb, """
            Port:P1  a 0  Num=1 Z=50 Ohm
            Port:P2  b 0  Num=2 Z=50 Ohm
            R:R1  a b  R=10 Ohm
            analysis HB1 type=hb Tone=1 ToneUnit=GHz MaxHarm=3 SSStart=1.01 SSStop=1.02 SSNpts=2 SSUnit=GHz Noise=yes NoiseOut=7
            """);
        var check = RunCli("check", hb, "--json");
        output.WriteLine(check.StdOut);
        Assert.Equal(1, check.ExitCode);
        Assert.Contains("check.noise.keys", check.StdOut);
        Assert.Contains("Num=7", check.StdOut);

        string osc = Path.Combine(_root, "osc.cnl");
        File.WriteAllText(osc, """
            L:L1  n 0  L=10 nH
            C:C1  n 0  C=10 pF
            R:RL  n 0  R=1000 Ohm
            SDD:D1 n 0  Ports=1  I[1,0]=-2e-3*_v1+1e-3*_v1^3
            OscProbe:X1 n 0 Mode=shunt
            analysis OSC1 type=osc Fguess=0.45 Fstart=0.6 Unit=GHz MaxHarm=1
            """);
        check = RunCli("check", osc, "--json");
        output.WriteLine(check.StdOut);
        Assert.Equal(1, check.ExitCode);
        Assert.Contains("check.osc.keys", check.StdOut);
    }

    /// <summary>An HB noise run's parts are listed as an S-parameter run's are, with what HB noise does differently said once.</summary>
    [Fact]
    public void Explain_ListsTheNoisyParts_ForAnHbNoiseRun()
    {
        Directory.CreateDirectory(_root);
        string hb = Path.Combine(_root, "hbparts.cnl");
        File.WriteAllText(hb, """
            Port:P1  a 0  Num=1 Z=50 Ohm
            Port:P2  b 0  Num=2 Z=50 Ohm
            R:R1  a b  R=10 Ohm Temp=126.85
            Diode:D1  b 0  Is=1e-14
            analysis HB1 type=hb Tone=1 ToneUnit=GHz MaxHarm=3 SSStart=1.01 SSStop=1.02 SSNpts=2 SSUnit=GHz Noise=yes
            """);
        var text = RunCli("explain", hb, "--analysis");
        output.WriteLine(text.StdOut);
        Assert.True(text.ExitCode == 0, text.StdErr + text.StdOut);
        Assert.Contains("large-signal waveform", text.StdOut);
        Assert.Contains("noise R1 (R): thermal at 400 K", text.StdOut);
        Assert.Contains("noise D1 (Diode): device sources", text.StdOut);
    }

    // ── The CLI as a process ────────────────────────────────────────────────────

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(NoiseExplainCliTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir.Length > 0 ? dir : AppContext.BaseDirectory;
    }
}

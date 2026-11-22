// ================================================================
//  ThermalExamplesTests.cs — brief-em3d-81's gates for the shipped thermal examples, Thermal Die to Heatsink and Thermal
//  Channel vs Surface, and brief-em3d-85's for the third, Thermal Output Wires.
//
//  Brief 70's discipline: each example's `expected-numbers.json` is the ONE source of every number its README and the Thermal
//  user page print — each value's Readme text must appear verbatim in both — and the Benchmark-tier gate re-runs the shipped
//  setups and holds their recorded values. `check` must be clean as a process; the vendor scan covers every file and the page.
// ================================================================

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class ThermalExamplesTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-th81-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { /* best effort */ } }

    private const string Heatsink = "Thermal Die to Heatsink", Channel = "Thermal Channel vs Surface", Wires = "Thermal Output Wires";
    private const string Page = "docs/user/src/reference/thermal.md";

    // ── gate 1: check is clean ──────────────────────────────────────────────────────────────────────

    /// <summary><c>check --json</c> on each workspace, as a process: no error and no warning.</summary>
    [Theory]
    [InlineData(Heatsink)]
    [InlineData(Channel)]
    [InlineData(Wires)]
    public async Task Gate1_CheckIsClean(string example)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        string cliDir = typeof(ThermalExamplesTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in new[] { "check", Root(example), "--json" }) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        _ = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        using var doc = JsonDocument.Parse(await outTask);
        var found = doc.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Where(d => d.GetProperty("severity").GetString() is "error" or "warning")
            .Select(d => d.GetProperty("message").GetString()!).ToList();
        Assert.Empty(found);
    }

    // ── gate 2: one source ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Heatsink, "README.md")]
    [InlineData(Heatsink, Page)]
    [InlineData(Channel, "README.md")]
    [InlineData(Channel, Page)]
    [InlineData(Wires, "README.md")]
    [InlineData(Wires, Page)]
    public void Gate2_TheReadmeAndThePageQuoteEveryNumberInTheFile(string example, string page)
    {
        string path = page == Page ? Path.Combine(PalaceBackendTests.RepoRoot(), Page) : Path.Combine(Root(example), page);
        string text = File.ReadAllText(path);
        var quoted = Readmes(JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(example), "expected-numbers.json"))).RootElement).ToList();
        Assert.NotEmpty(quoted);
        Assert.All(quoted, q => Assert.Contains(q, text));
    }

    /// <summary>Every string under a <c>Readme</c> key, at any depth.</summary>
    private static IEnumerable<string> Readmes(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
            {
                if (p.Name == "Readme")
                {
                    if (p.Value.ValueKind == JsonValueKind.String) yield return p.Value.GetString()!;
                    else foreach (var s in p.Value.EnumerateArray()) yield return s.GetString()!;
                }
                else foreach (string s in Readmes(p.Value)) yield return s;
            }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var x in e.EnumerateArray()) foreach (string s in Readmes(x)) yield return s;
    }

    /// <summary>The external check the page states: the interface material in one dimension.</summary>
    [Fact]
    public void TheInterfaceMaterialsClosedForm_IsTheRecordedValue()
    {
        double t = 85 + 11 * 100e-6 / (3 * 10e-3 * 8e-3);
        var n = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(Heatsink), "expected-numbers.json"))).RootElement;
        Assert.Equal(t, n.GetProperty("External")[0].GetProperty("Expected").GetDouble(), 5);
        Assert.Equal(t, n.GetProperty("Shipped").GetProperty("Recorded")[2].GetProperty("T_interface").GetDouble(), 4);
    }

    /// <summary>brief-em3d-85 — the external check the Output Wires page states: brief 72's W1 closed form for one isolated wire
    /// (air, linear ρ, constant k, ends at the solved heels' mean, 98.78 °C) at the hottest wire's 2.3088 A.</summary>
    [Fact]
    public void TheIsolatedWiresClosedForm_IsTheRecordedValue()
    {
        const double s20 = 4.1e7, a20 = 0.0034, k = 318, d = 25.4e-6, l = 0.9724e-3, te = 98.78, i = 2.3088;
        double a = Math.PI * d * d / 4, rhoE = (1 / s20) * (1 + a20 * (te - 20)), aE = a20 / (1 + a20 * (te - 20));
        double beta = i * Math.Sqrt(rhoE * aE / k) / a;
        double centre = te + (1 / aE) * (1 / Math.Cos(beta * l / 2) - 1), iStar = Math.PI / l * a * Math.Sqrt(k / (rhoE * aE));
        var n = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(Wires), "expected-numbers.json"))).RootElement;
        Assert.Equal(n.GetProperty("External")[0].GetProperty("Expected").GetDouble(), centre, 0.05);
        Assert.Equal(3.21, iStar, 0.005);
    }

    // ── gate 3: no vendor names ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_NoVendorNameIsInTheExamplesOrThePage()
    {
        var files = new[] { Heatsink, Channel, Wires }.SelectMany(x => Directory.EnumerateFiles(Root(x), "*", SearchOption.AllDirectories))
            .Append(Path.Combine(PalaceBackendTests.RepoRoot(), Page));
        foreach (string file in files)
        {
            var words = Regex.Matches(File.ReadAllText(file).ToLowerInvariant(), @"[a-z][a-z0-9]*").Select(w => w.Value).ToList();
            for (int i = 0; i < words.Count; i++)
            {
                Assert.DoesNotContain(Docs.WsProbeDocsTests.Digest(words[i]), Docs.WsProbeDocsTests.BannedDigests);
                if (i + 1 < words.Count)
                    Assert.DoesNotContain(Docs.WsProbeDocsTests.Digest(words[i] + " " + words[i + 1]), Docs.WsProbeDocsTests.BannedDigests);
            }
        }
    }

    // ── the runs: a regression test of the recorded numbers ─────────────────────────────────────────

    /// <summary>
    /// The shipped setups re-run in process — Die to Heatsink's three power points (about a minute), One Finger (minutes, order 2)
    /// and Eight Fingers — each holding its recorded values to the file's tolerance (K, or K/W). A deliberate change re-records
    /// the file and the pages quoting it.
    /// </summary>
    [GmshFact]
    [Trait("Category", "Benchmark")]
    public void TheShippedSetups_ReproduceTheirRecordedNumbers()
    {
        var moved = new List<string>();
        void Hold(string what, double got, double want, double tol)
        {
            output.WriteLine($"{what}: {got:F4} (recorded {want:F4})");
            if (Math.Abs(got - want) > tol) moved.Add($"{what}: {got:F4}, recorded {want:F4} (± {tol})");
        }

        var hs = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(Heatsink), "expected-numbers.json"))).RootElement.GetProperty("Shipped");
        var r = Run(Heatsink, "Die to Heatsink", "Steady");
        double tol = hs.GetProperty("Tolerance").GetDouble();
        int k = 0;
        foreach (var p in hs.GetProperty("Recorded").EnumerateArray())
        {
            Hold($"die top at {p.GetProperty("Pdiss")} W", r.Data!["thermal.T:die_top:max"].RealValues[k], p.GetProperty("DieTop").GetDouble(), tol);
            Hold($"Rth_jc at {p.GetProperty("Pdiss")} W", r.Data["Rth_jc"].RealValues[k], p.GetProperty("Rth_jc").GetDouble(), tol / 10);
            Hold($"Rth_ja at {p.GetProperty("Pdiss")} W", r.Data["Rth_ja"].RealValues[k], p.GetProperty("Rth_ja").GetDouble(), tol / 10);
            k++;
        }

        var ch = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(Channel), "expected-numbers.json"))).RootElement;
        var one = ch.GetProperty("OneFinger");
        r = Run(Channel, "One Finger", "Steady");
        Hold("channel", r.Data!["thermal.T:channel_T:max"].RealValues[0], one.GetProperty("Recorded").GetProperty("ChannelMax").GetDouble(), one.GetProperty("Tolerance").GetDouble());
        Hold("offset", r.Data["dT_ch_ir"].RealValues[0], one.GetProperty("Recorded").GetProperty("dT_ch_ir").GetDouble(), one.GetProperty("Tolerance").GetDouble());
        var eight = ch.GetProperty("EightFingers");
        r = Run(Channel, "Eight Fingers", "Array");
        Hold("centre finger", r.Data!["T_centre"].RealValues[0], eight.GetProperty("Recorded").GetProperty("T_centre").GetDouble(), eight.GetProperty("Tolerance").GetDouble());
        Hold("edge finger", r.Data["T_edge"].RealValues[0], eight.GetProperty("Recorded").GetProperty("T_edge").GetDouble(), eight.GetProperty("Tolerance").GetDouble());

        Assert.True(moved.Count == 0, "thermal results moved from the recorded ones:\n" + string.Join("\n", moved));
    }

    /// <summary>
    /// brief-em3d-85 — Thermal Output Wires' thermal setups re-run in process (about a minute in all): DcSweep's hottest and edge
    /// wires and its runaway, RfHarmonics' edge and centre wires, and Drawn Wires against the recorded DcSweep.
    /// </summary>
    [GmshFact]
    [Trait("Category", "Benchmark")]
    public void TheOutputWiresSetups_ReproduceTheirRecordedNumbers()
    {
        var n = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(Wires), "expected-numbers.json"))).RootElement;
        var moved = new List<string>();
        void Hold(string what, double got, double want, double tol)
        {
            output.WriteLine($"{what}: {got:F4} (recorded {want:F4})");
            if (!(Math.Abs(got - want) <= tol)) moved.Add($"{what}: {got:F4}, recorded {want:F4} (± {tol})");
        }
        static string Max(int wire) => $"thermal.Twire:U1/wire/Out/{wire}:max";

        var dc = n.GetProperty("DcSweep");
        double tol = dc.GetProperty("Tolerance").GetDouble();
        var r = Run(Wires, "Output", "DcSweep");
        int k = 0;
        foreach (var p in dc.GetProperty("Recorded").EnumerateArray())
        {
            if (p.TryGetProperty("Runaway", out _)) { Assert.Equal(1, r.Data!["thermal.Runaway"].RealValues[k]); k++; continue; }
            Hold($"hottest at {p.GetProperty("Idc")} A", r.Data!["measurements.Thot"].RealValues[k], p.GetProperty("Hottest").GetDouble(), tol);
            Hold($"edge at {p.GetProperty("Idc")} A", r.Data[Max(1)].RealValues[k], p.GetProperty("Edge").GetDouble(), tol);
            k++;
        }
        Assert.Contains(r.Warnings, w => w.Contains("No steady state above about 16.79 A"));

        var rf = n.GetProperty("RfHarmonics");
        r = Run(Wires, "Output", "RfHarmonics");
        k = 0;
        foreach (var p in rf.GetProperty("Recorded").EnumerateArray())
        {
            Hold($"RF edge at {p.GetProperty("I1")} A", r.Data![Max(1)].RealValues[k], p.GetProperty("Edge").GetDouble(), tol);
            Hold($"RF centre at {p.GetProperty("I1")} A", r.Data[Max(3)].RealValues[k], p.GetProperty("Centre").GetDouble(), tol);
            k++;
        }

        var drawn = n.GetProperty("DrawnWires").GetProperty("Recorded");
        r = Run(Wires, "Drawn Wires", "DcSweep");
        Hold("drawn wires' hottest at 14 A", r.Data!["measurements.Thot"].RealValues[2], drawn.GetProperty("Hottest").GetDouble(), tol);

        Assert.True(moved.Count == 0, "thermal results moved from the recorded ones:\n" + string.Join("\n", moved));
    }

    /// <summary>
    /// brief-wbond-wire-temperature G11 — the Amplifier Wires bench, the quick check with no 3D run: its drive sweep in process (well
    /// under a second, so routine), WireTemp at the three recorded drive levels, every point solved, and the circuit's own Pout and
    /// drain current at 28 dBm.
    /// </summary>
    [Fact]
    public void TheAmplifierWiresBench_ReproducesItsRecordedNumbers()
    {
        var aw = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(Wires), "expected-numbers.json"))).RootElement.GetProperty("AmplifierWires");
        double tol = aw.GetProperty("Tolerance").GetDouble();
        string schematic = Path.Combine(Root(Wires), "Amplifier Wires", "schematic", "Amplifier Wires.csch");
        var (model, _, _) = CircuitRF.Design.Schematic.SchematicPersistence.LoadFromFile(schematic);
        var extracted = CircuitRF.Design.Schematic.NetExtractor.Extract(model, "Amplifier Wires");
        Directory.CreateDirectory(_tmp);
        string cnl = Path.Combine(_tmp, "amplifier-wires.cnl");
        File.WriteAllText(cnl, CircuitRF.Core.Netlist.CnlWriter.Write(extracted.TestBench, extracted.Library));
        var run = CircuitRF.Ui.Schematic.SchematicRunService.RunNetlist(cnl, baseDirectory: Path.GetDirectoryName(schematic));
        Assert.True(run.Status == CircuitRF.Design.Circuit.RunStatus.Success, run.StatusMessage);
        var ds = Assert.Single(run.DataSets);

        var t = ds["WireTemp"];
        var pins = t.Axes[0].Values;
        Assert.All(ds["WireTempState"].RealValues, v => Assert.Equal(1.0, v));
        foreach (var p in aw.GetProperty("Recorded").EnumerateArray())
        {
            int i = Array.IndexOf(pins, p.GetProperty("Pin").GetDouble());
            output.WriteLine($"WireTemp at {pins[i]} dBm {t.RealValues[i]:F3} °C");
            Assert.Equal(p.GetProperty("WireTemp").GetDouble(), t.RealValues[i], tol);
        }
        // the bench's own measure lines, at 28 dBm: Pout_dBm = 10·log10(Re(½·V(out,1)·I(IOUT,1)*)·1000), Id_A = Re I(IDD,0)
        System.Numerics.Complex At(string cube, string name, int k)
        {
            var c = ds[cube];
            int row = Array.IndexOf(c.Axes[1].Labels!, name), nRow = c.Axes[1].Length, nH = c.Axes[2].Length;
            return c.ComplexValues[((pins.Length - 1) * nRow + row) * nH + k];
        }
        var at28 = aw.GetProperty("At28");
        double pout = 10 * Math.Log10((0.5 * At("V", "out", 1) * System.Numerics.Complex.Conjugate(At("I", "IOUT", 1))).Real * 1000);
        Assert.Equal(at28.GetProperty("Pout_dBm").GetDouble(), pout, 0.01);
        Assert.Equal(at28.GetProperty("Id_A").GetDouble(), At("I", "IDD", 0).Real, 0.01);
    }

    /// <summary>brief-em3d-85 — the EM setup in Palace (Draft, about 1.5 min), then FromHB on its result: the edge wire at 28 dBm
    /// and the drive at which it passes its limit.</summary>
    [PalaceFact]
    [Trait("Category", "Benchmark")]
    public void TheOutputWiresFromHB_ReproducesItsRecordedNumbers()
    {
        var hb = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(Wires), "expected-numbers.json"))).RootElement.GetProperty("FromHB");
        // On a COPY of the workspace, writing to its own results/ — where the Amplifier's XOUT reads the EM result from. Run's
        // scratch results root is a folder no circuit names, so FromHB refused with "No instance of the circuit uses this 3D
        // view's EM result" before solving anything.
        string copy = Path.Combine(_tmp, "copy", Wires);
        CopyTree(Root(Wires), copy);
        Run(Wires, "Output", "EM", copy);
        var r = Run(Wires, "Output", "FromHB", copy);
        var rec = hb.GetProperty("Recorded");
        double tol = hb.GetProperty("Tolerance").GetDouble();
        double edge = r.Data!["thermal.Twire:U1/wire/Out/1:max"].RealValues[^1], limit = r.Data["circuit.LimitAt:w1"].RealValues.Single();
        output.WriteLine($"edge at 28 dBm {edge:F4}, w1's limit at {limit:F4} dBm");
        Assert.Equal(rec.GetProperty("Edge").GetDouble(), edge, tol);
        Assert.Equal(rec.GetProperty("LimitAtW1").GetDouble(), limit, 0.01);
    }

    /// <param name="workspace">A copy of the example to run in, writing to its own <c>results/</c>; null runs the shipped
    /// example in place, writing to a scratch folder.</param>
    private EmRunResult Run(string example, string cell, string setup, string? workspace = null)
    {
        string root = workspace ?? Root(example);
        string path = Path.Combine(root, cell, "3d", cell + ".c3d");
        var doc = C3dPersistence.LoadFromFile(path);
        var run = EmRunService.RunThreeDView(C3dSetups.ForRun(C3dSetups.Select(doc, setup).Setup!, path), doc, path,
                                             Path.Combine(root, ".cws"),
                                             workspace is null ? Path.Combine(_tmp, example) : Path.Combine(workspace, "results"));
        Assert.True(run.Status == EmRunStatus.Ok, $"{cell} {setup}: {run.Error}");
        return run;
    }

    private static void CopyTree(string from, string to)
    {
        foreach (string dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }

    private static string Root(string example)
        => Path.Combine(ExampleWorkspaces.ResolveRoot(PalaceBackendTests.RepoRoot()) ?? throw new InvalidOperationException("no examples/"), example);
}

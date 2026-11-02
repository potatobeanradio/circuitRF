using System.Globalization;
using System.Text;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.WBond;
using Xunit;
using WPoint3 = CircuitRF.WBond.Point3;

namespace CircuitRF.Ui.Tests.ThreeD;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-em3d-50 R-em3d50-1a (gate 1) — the wire build's output, as text, for every existing wire
//  fixture and for the Bond wire and Package examples. The dumps under testdata/em3d/wire-dumps/ were
//  written BEFORE Em3dWires was split into resolution and .wBond reading, from the code as it stood;
//  the split, and anything after it, must reproduce them byte for byte. Beyond the generator dump
//  (Em3dGeneratorDumpTests) each wire's whole report is written — both loop heights and every
//  process value with its source — so the split cannot move a number the problem does not carry.
//  Set CRF_WRITE_WIRE_DUMPS=1 to (re)write them; nothing else does. Every case states its air box (GoldenAirBoxes), so a
//  change to the default padding never moves a dump.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class WireSplitDumpTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in Fixtures()) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Gate1_TheWireBuild_IsByteIdenticalToItsPreSplitDump(string name)
    {
        var build = Fixtures().Single(f => f.Name == name).Build;
        string text = Dump(build(GoldenAirBoxes.For(name)));
        string path = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "wire-dumps", FileName(name));
        if (Environment.GetEnvironmentVariable("CRF_WRITE_WIRE_DUMPS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Assert.True(File.Exists(path), $"no pre-split dump for '{name}' at {path}");
        Assert.Equal(File.ReadAllText(path), text);
    }

    private static string FileName(string name)
        => new string([.. name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_')]) + ".txt";

    internal static IEnumerable<(string Name, Func<EmAirBox?, Em3dGenerationResult> Build)> Fixtures()
    {
        // The examples: every .cem whose layout carries a .wBond.
        foreach (var (name, build) in Em3dGeneratorDumpTests.Fixtures())
            if (name.Contains("3D EM/Bond wire", StringComparison.Ordinal) || name.Contains("3D EM/Package", StringComparison.Ordinal))
                yield return (name, build);

        string caseA = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "f0", "A-bondwire", "kernelw", "case.wBond");
        yield return ("f0 case A", box => Em3dWireTests.Generate(WBondIo.ReadFile(caseA), airBox: box));
        yield return ("f0 case A round", box =>
        {
            var d = WBondIo.ReadFile(caseA);
            foreach (var w in d.AllWires()) w.CrossSection = WireCrossSection.Round;
            return Em3dWireTests.Generate(d, airBox: box);
        });

        WPoint3[] asym = [new(-475_000, 0, 112_700), new(-400_000, 30_000, 250_000), new(300_000, 0, 250_000), new(475_000, 0, 112_700)];
        yield return ("asymmetric array foot", box =>
        {
            var d = OneWire(asym);
            d.Arrays[0].FootLengthNm = 40_000;
            return Em3dWireTests.Generate(d, airBox: box);
        });
        yield return ("asymmetric overhang", box =>
        {
            var d = OneWire(asym);
            d.Arrays[0].FootLengthNm = 40_000;
            d.Arrays[0].Wires[0].FootLengthNm = 200_000;
            return Em3dWireTests.Generate(d, airBox: box);
        });
        foreach (bool vertical in new[] { false, true })
            yield return ($"ball start vertical={vertical}", box =>
            {
                var d = OneWire(new(-475_000, 0, 112_700),
                                vertical ? new WPoint3(-475_000, 0, 250_000) : new WPoint3(-325_000, 0, 262_700),
                                new(325_000, 0, 262_700), new(475_000, 0, 112_700));
                d.Arrays[0].Wires[0].StartBond = BondStyle.Ball;
                return Em3dWireTests.Generate(d, airBox: box);
            });
        yield return ("ball end round", box =>
        {
            var d = OneWire(new(-475_000, 0, 112_700), new(-325_000, 0, 262_700), new(475_000, 0, 112_700));
            d.Arrays[0].Wires[0].EndBond = BondStyle.Ball;
            d.Arrays[0].Wires[0].CrossSection = WireCrossSection.Round;
            return Em3dWireTests.Generate(d, airBox: box);
        });
        yield return ("through vertical", box => Em3dWireTests.Generate(OneWire(
            new(-475_000, 0, 112_700), new(-375_000, 0, 212_700), new(-370_000, 0, 312_700), new(-370_000, 0, 412_700),
            new(-375_000, 0, 512_700), new(-200_000, 0, 600_000), new(475_000, 0, 112_700)), airBox: box));
        yield return ("plan dogleg", box => Em3dWireTests.Generate(OneWire(
            new(-475_000, 0, 112_700), new(-375_000, 0, 300_000), new(0, 0, 300_000), new(-100_000, 173_205, 300_000),
            new(300_000, 300_000, 300_000), new(475_000, 0, 112_700)), airBox: box));
        yield return ("two arrays", box =>
        {
            var d = OneWire(new(-475_000, 0, 112_700), new(0, 0, 262_700), new(475_000, 0, 112_700));
            var second = new Wire { DiameterNm = 17_780, Material = "Gold" };
            second.Points.AddRange([new(-470_000, 20_000, 112_700), new(0, 20_000, 200_000), new(470_000, 20_000, 112_700)]);
            d.Arrays.Add(new WireArray { Name = "G2", Wires = { second } });
            return Em3dWireTests.Generate(d, airBox: box);
        });
        yield return ("refusal unknown metal", box =>
        {
            var d = OneWire(new(-475_000, 0, 112_700), new(475_000, 0, 112_700));
            d.Arrays[0].Wires[0].Material = "Unobtainium";
            return Em3dWireTests.Generate(d, airBox: box);
        });
        yield return ("refusal no pad", box =>
            Em3dWireTests.Generate(OneWire(new(-475_000, 0, 112_700), new(0, 0, 262_700), new(900_000, 0, 112_700)), airBox: box));
        yield return ("refusal sharp turn", box => Em3dWireTests.Generate(OneWire(
            new(-475_000, 0, 112_700), new(0, 0, 262_700), new(-300_000, 0, 262_800), new(475_000, 0, 112_700)), airBox: box));
    }

    private static WBondDesign OneWire(params WPoint3[] points)
    {
        var design = new WBondDesign { OperatingTempC = 20 };
        var wire = new Wire { DiameterNm = 25_400 };
        wire.Points.AddRange(points);
        design.Arrays.Add(new WireArray { Name = "G1", Wires = { wire } });
        return design;
    }

    internal static string Dump(Em3dGenerationResult r)
    {
        var sb = new StringBuilder(Em3dGeneratorDumpTests.Dump(r));
        foreach (var w in r.Wires)
            sb.Append($"report {w.Name} assembly {R(w.AssemblyLoopHeightM)} wbond {R(w.WBondLoopHeightM)} " +
                      $"foot {V(w.Process.FootLength)} ball {V(w.Process.BallDiameter)} {V(w.Process.BallHeight)} " +
                      $"rules {w.Process.AssemblyRules.ResolvedPath ?? "-"} size {R(w.Size.Width)},{R(w.Size.Height)} " +
                      $"start {End(w.Start)} end {End(w.End)}\n");
        return sb.ToString();

        static string V(WireBondValue v) => $"{v.Nm}:{v.Source}";
        static string End(Em3dWireEnd e) => $"{e.Style},{e.Pad},{R(e.PadTopM)},{e.Neck},{(e.FootLengthM is { } f ? R(f) : "-")},{R(e.OverhangM)}";
    }

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}

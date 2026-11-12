// ================================================================
//  C3dHollowWavePortTests.cs — brief-hzn-11's routine gates: a wave port on a hollow rectangular guide resolves with no
//  positive or negative conductor and its polarity line where R-hzn11-2 puts it; a sweep point below the guide's cutoff is
//  refused naming the cutoff; "mode" on a lumped port is refused; a two-conductor wave port on the box's faces reads as
//  before; the box's PEC faces alone, not closed round the region, are refused without the Terminals advice; EMerge and
//  Palace lower a hollow guide with nothing refused, openEMS refuses it; and a port normalised to its mode keeps the
//  solver's S as it is. The fixture (wr90-empty.c3d) is an empty WR90 section, 22.86 × 10.16 × 40 mm, every air-box face
//  PEC, a wave port with Z0 "mode" on each end.
//
//  The closed-form oracle (|S21| = 1, phase −βL) is a solve, run by hand and recorded in src/Design/RESOLVED.md (HZN-11).
// ================================================================

using System.Numerics;
using System.Text.Json;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class C3dHollowWavePortTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-hzn11-" + Guid.NewGuid().ToString("N")[..12]);

    public C3dHollowWavePortTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private static string Fixture => Path.Combine(FixturePaths.Require("tests/Ui.Tests/ThreeD/Fixtures"), "wr90-empty.c3d");

    /// <summary>The fixture, its text edited, written beside the test and loaded.</summary>
    private (C3dDocument Doc, string Path) Load(Func<string, string>? edit = null)
    {
        string text = File.ReadAllText(Fixture);
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N")[..6] + ".c3d");
        File.WriteAllText(path, edit is null ? text : edit(text));
        return (C3dPersistence.LoadFromFile(path), path);
    }

    /// <summary>The problem the setup's run builds, through the run's own assembly.</summary>
    private static Em3dProblem Problem(C3dDocument doc, string path, string setup = "EMerge")
    {
        var embedded = C3dSetups.Read(doc).Single(s => s.Name == setup).Setup!;
        var built = C3dProblemAssembly.Assemble(C3dSetups.ForRun(embedded, path), doc, path, null);
        Assert.True(built.Ok, built.Refusal);
        return built.Problem!;
    }

    private static C3dPortResult Resolve(C3dDocument doc, string path, string setup = "EMerge")
    {
        var e = C3dElaborator.ElaborateOnce(doc, path, null);
        var s = C3dSetups.Read(doc).Single(x => x.Name == setup).Setup!;
        var ctx = C3dProblemAssembly.PortContext(s, doc, e, C3dProblemAssembly.AirBox(s, e, out _));
        return C3dPorts.Resolve(doc, ctx)[0];
    }

    /// <summary>R-hzn11-2 — no positive or negative conductor; the polarity line crosses the narrow side at the broad side's centre,
    /// from the lower wall to the higher. Broad along x (WR90 lying flat) the line runs in z at x = a/2; stood on its side (the
    /// broad side along z) it runs in x at z = a/2.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AHollowGuidePort_HasNoEnds_AndItsPolarityLineCrossesTheNarrowSideAtTheBroadSidesCentre(bool standing)
    {
        var (doc, path) = Load();
        if (standing)
        {
            doc.Variables.Single(x => x.Name == "a").Expression = "10.16";
            doc.Variables.Single(x => x.Name == "b").Expression = "22.86";
            foreach (var port in doc.Ports) port.Rect = new C3dRect { Min = new C3dPoint2(0, 0), Size = new C3dPoint2(10_160_000, 22_860_000) };
        }
        var p = Problem(doc, path).Ports.Single(q => q.Number == 1);
        Assert.True(p.Hollow);
        Assert.Equal(("", ""), (p.PositiveObject, p.NegativeObject));
        Assert.Equal(22.86e-3, p.HollowBroadM!.Value, 9);
        var v = p.VoltagePath!.Value;
        var (from, to) = standing ? (new Point3(0, 0, 11.43e-3), new Point3(10.16e-3, 0, 11.43e-3))
                                  : (new Point3(11.43e-3, 0, 0), new Point3(11.43e-3, 0, 10.16e-3));
        Assert.Equal(from, Round(v.From));
        Assert.Equal(to, Round(v.To));
        Assert.True(p.ModeNormalised);
    }

    /// <summary>R-hzn11-2 — WR90's TE10 cutoff is c/(2a) = 6.557 GHz: a sweep from 5 GHz is refused before meshing, naming it.</summary>
    [Fact]
    public void ASweepPointBelowCutoff_IsRefused_NamingTheCutoff()
    {
        var (doc, path) = Load();
        var problem = Problem(doc, path);
        Assert.Null(Em3dModePorts.BelowCutoffRefusal(problem));
        var low = problem with { Frequency = problem.Frequency with { StartHz = 5e9 } };
        Assert.StartsWith("Port 'P1' is a hollow guide 22.86 mm across, whose lowest mode is cut off below 6.557 GHz in Air; the sweep " +
                          "starts at 5 GHz, and 1 of its points lies below the cutoff", Em3dModePorts.BelowCutoffRefusal(low));
    }

    /// <summary>R-hzn11-1 — a gap has no mode: "mode" on a lumped port is refused, naming the port.</summary>
    [Fact]
    public void ModeOnALumpedPort_IsRefused()
    {
        var (doc, path) = Load();
        doc.Ports[0].Kind = Em3dPortKind.Lumped;
        Assert.StartsWith("P1's Z0 is 'mode', and P1 is a lumped port", Resolve(doc, path).Refusal);
    }

    /// <summary>R-hzn11-2 — the two-conductor reading is unchanged: with the side faces magnetic walls, only the floor and the lid
    /// meet the region, and the port runs between them (a parallel-plate line), not as a hollow guide.</summary>
    [Fact]
    public void ATwoConductorWavePort_ReadsAsBefore()
    {
        var (doc, path) = Load(t => ReplaceFaces(ReplaceFaces(t, "XMin", "Pmc"), "XMax", "Pmc"));
        var r = Resolve(doc, path);
        Assert.Null(r.Refusal);
        Assert.False(r.Resolved!.Hollow);
        Assert.Equal(["airbox/zmax", "airbox/zmin"], new[] { r.Resolved.PositiveObject, r.Resolved.NegativeObject }.Order());
    }

    /// <summary>R-hzn11-2 — three PEC faces of the box and nothing else meet the region: refused, and never with the Terminals advice
    /// (there is no signal conductor to give one).</summary>
    [Fact]
    public void BoxFacesNotClosingRoundTheRegion_AreRefused_WithoutTheTerminalsAdvice()
    {
        var (doc, path) = Load(t => ReplaceFaces(t, "ZMax", "Absorbing"));
        var r = Resolve(doc, path);
        Assert.StartsWith("P1 is a wave port on the air box's ymin face, and only the air box's PEC faces xmin, xmax and zmin meet its " +
                          "region, without closing round it.", r.Refusal);
        Assert.DoesNotContain("Terminals", r.Refusal);
    }

    /// <summary>R-hzn11-3 — the dry run's lowerings: EMerge writes the hollow guide's entry and refuses nothing, Palace's
    /// configuration is written, openEMS refuses it naming the port.</summary>
    [Fact]
    public void AHollowGuide_IsLoweredForEMergeAndPalace_AndRefusedByOpenEms()
    {
        var (doc, path) = Load();
        var problem = Problem(doc, path);
        Assert.Null(EmergeLoweringRules.Refusal(problem, EmergeSettings.Default));
        using var json = JsonDocument.Parse(EmergeScriptWriter.Write(problem, EmergeSettings.Default).ProblemJson!);
        var wave = json.RootElement.GetProperty("wavePorts")[0];
        Assert.True(wave.GetProperty("hollow").GetBoolean());
        Assert.True(wave.GetProperty("modeNormalised").GetBoolean());

        var palace = Problem(doc, path, "Palace");
        var settings = PalaceSettings.Default;
        Assert.Null(PalaceConfigWriter.Write(palace, GmshGeoWriter.Write(palace, settings).Groups, settings).Refusal);
        Assert.Null(Em3dModePorts.SolverRefusal(palace, PalaceSolver.Id));
        Assert.StartsWith("Port 1 is a wave port met by one conductor (a hollow waveguide).", Em3dModePorts.SolverRefusal(palace, OpenEmsSolver.Id));
    }

    /// <summary>R-hzn11-4 — a port normalised to its mode keeps the solver's S as it is, whatever its mode's impedance, and the file
    /// states R 1 with the line saying it is not an impedance.</summary>
    [Fact]
    public void APortNormalisedToItsMode_KeepsTheSolversS()
    {
        var (doc, path) = Load();
        var ports = Problem(doc, path).Ports;
        var s = new Complex[,] { { new(0.01, 0.02), new(0.6, -0.8) }, { new(0.6, -0.8), new(0.02, 0.01) } };
        var sweep = new EmergeSweep([10e9], [s], [[new Complex(443.5, 0), new Complex(443.5, 0)]], [[Complex.Zero, Complex.Zero]]);
        var kept = EmergeResults.AtPortReferences(sweep, ports, out bool renormalised);
        Assert.False(renormalised);
        Assert.Equal(s, kept.S[0]);
        Assert.Equal([Complex.One, Complex.One], kept.Z0[0]);
        Assert.StartsWith(Em3dModePorts.SnpPrefix + "1, 2.", Em3dModePorts.SnpLine(ports));
    }

    private static string ReplaceFaces(string text, string face, string kind)
        => text.Replace($"\"{face}\": {{\n\t\t\t\t\t\"PaddingUm\": 0,\n\t\t\t\t\t\"Boundary\": \"Pec\"",
                        $"\"{face}\": {{\n\t\t\t\t\t\"PaddingUm\": 0,\n\t\t\t\t\t\"Boundary\": \"{kind}\"");

    private static Point3 Round(Point3 p) => new(Math.Round(p.X, 9), Math.Round(p.Y, 9), Math.Round(p.Z, 9));
}

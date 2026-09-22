// PCAL5 — a calibration GROUP whose ports sit on PADS, so the solver grows every member a feed lead.
//
// PCAL4 declined this by name (R-pcal4-6): "a lead is peeled as a matched length of the port's OWN
// line, which is one propagation constant; a group's port region has one per mode". That is right
// about the algebra and wrong about the geometry, and the case it refuses is what real boards are
// made of — a port lands on a pad, the pad is shorter than the calibration's end run, and R-fed-1
// grows a lead on EVERY member of the group, because they share a reference plane and therefore the
// same cross-section question. The leads are then collinear, equal in length and side by side at the
// group's own separation: together they ARE a uniform N-conductor section of exactly the
// cross-section the group's standard reproduces, so the peel is a matched length of the GROUP's
// modes. `PlanarFeedExtension.Peel` already ran in the modal basis; all it was missing was γ_m.
//
// Every PCAL4 fixture is a straight line with its ports at the drawn ends, so not one of them ever
// grows a lead and not one of them could reach this. `pad-coupled-pair` is the shape that does:
// 254 µm lines 812.8 µm apart, each ending in a 558.8 µm square pad, the pads 508 µm apart —
// 0.56 substrate heights, well inside the 5 a driven neighbour needs. At HEAD before PCAL5 it was a
// REFUSAL, with "would form one calibration group, but the solver had to grow a feed lead" carried
// along as the reason.
//
// THE OTHER HALF OF THE GATE — that every member peels the SAME length — is unit-tested rather than
// fixtured, and the absence is deliberate: it is not reachable by drawing. A group already requires
// its members to share a reference plane, the plane is one cell in from the lead's outer end, and
// the lead's length is quantised by the uniformity scan's own step, so two members with different
// drawn edges come out at outer ends a few µm apart and are declined by the PLANE test first
// (measured while trying to build a fixture for it: pads 300 µm apart in length gave leads of
// 2151.563 µm and 1856.25 µm, i.e. outer ends 4.69 µm apart). It is a guard on
// `PlanarFeedExtension.Peel`'s own precondition, and it is asserted exactly where it can be —
// `Engine.Tests/Mom/PlanarFeedExtensionTests`, the CommonPeelLength group.
//
// COST. One solve, one frequency, the same "coarsest mesh that still resolves the ports" choice
// ModalErrorBoxTests makes and for the same reason: everything gated here is a DECISION, and none of
// it is a number the mesh moves.

using CircuitRF.Design.Layout.Em;
using CircuitRF.Ui.Layout.Em;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public sealed class PadTerminatedGroupTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _results = Path.Combine(
        Path.GetTempPath(), "crf-pcal5-" + Guid.NewGuid().ToString("N")[..12], "results");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_results)!, true); } catch { /* best effort */ }
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }

    [Fact]
    public void APadTerminatedPairRuns_AsTwoGroups_WithEveryLeadPeeledModally()
    {
        string portcal = Path.Combine(RepoRoot(), "testdata", "portcal");
        string cem     = Path.Combine(portcal, "pad-coupled-pair", "em", "pad-coupled-pair.cem");

        var setup = EmSetupPersistence.LoadFromFile(cem);
        var res   = EmSetupResolver.Resolve(
            cem, setup.LayoutRef, Path.Combine(portcal, ".cws"), new TechnologyCache());
        Assert.NotNull(res.Source);

        setup = setup.Clone();
        setup.Frequency = new CircuitRF.Core.Design.FrequencySpec(
            "1", "1", 1, CircuitRF.Core.Design.SweepKind.Linear, "GHz", "GHz");
        setup.PlanarMesh = setup.PlanarMesh with { CellsPerWavelength = 2, EdgeMesh = false };

        var r = EmRunService.Run(setup, res.Source!, _results);
        output.WriteLine(r.Error ?? "(no error)");
        Assert.Equal(EmRunStatus.Ok, r.Status);
        Assert.NotNull(r.SnpPath);

        // ── Both planes are a group, and the pad end is one of them ────────────────────────────
        var groups = r.Notes!.Where(n => n.Contains("CALIBRATION GROUP", StringComparison.Ordinal)).ToList();
        foreach (string g in groups) output.WriteLine(g);
        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, g => g.Contains("Ports 1, 3", StringComparison.Ordinal));
        Assert.Contains(groups, g => g.Contains("Ports 2, 4", StringComparison.Ordinal));

        // ── …and the decline that used to stop the pad end is not reached ──────────────────────
        //
        // Asserted by NAME rather than by "the run is green": a group that formed for some other
        // reason would satisfy the two lines above and this one would still catch it.
        Assert.DoesNotContain(r.Notes!,
            n => n.Contains("would form one calibration group", StringComparison.Ordinal));

        // ── Both members grew a lead, and both got it back off ─────────────────────────────────
        string grown = Assert.Single(r.Notes!, n => n.Contains("UNIFORM LEAD", StringComparison.Ordinal));
        output.WriteLine(grown);
        Assert.Contains("port 2", grown, StringComparison.Ordinal);
        Assert.Contains("port 4", grown, StringComparison.Ordinal);

        string peeled = Assert.Single(r.Notes!, n => n.Contains("peeled back off", StringComparison.Ordinal));
        output.WriteLine(peeled);
        Assert.Contains("port 2", peeled, StringComparison.Ordinal);
        Assert.Contains("port 4", peeled, StringComparison.Ordinal);

        // ── Nothing was published outside the calibration's validity ───────────────────────────
        //
        // Which is the whole difference between this and the override a user had to reach for: the
        // file carries no caveat because none is owed.
        Assert.DoesNotContain(r.Notes!, n => n.Contains("OUTSIDE the condition", StringComparison.Ordinal));
        Assert.Empty(EmSnpProvenance.ReadCaveats(r.SnpPath!));
    }
}

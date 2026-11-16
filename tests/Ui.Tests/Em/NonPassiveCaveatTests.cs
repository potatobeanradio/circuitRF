// R-pcal7-4 — a row that is not a passive network is named on the FILE's own face.
//
// PCAL2's finding one case further on. A `.sNp` on disk carries no notes, and the run's NOT PASSIVE
// sentence is the last of thirty; on the shipped MMIC spiral the four rows below ~0.8 GHz read
// 110 nH against a real 2.8 nH with sigma_max up to 1.09, and a user opening that file in the Data
// Display six months later sees a smooth, plausible curve with nothing on it to say so.
//
// Gated against `ValidityCaveats` directly rather than through a run: the predicate is the whole of
// the change, and a full-wave sweep that happens to be non-passive costs minutes to reproduce and
// would be gating the sweep rather than the sentence.

using System.Numerics;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Mom;
using NumFlat;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public sealed class NonPassiveCaveatTests(ITestOutputHelper output)
{
    private static PlanarSolveResult Result(params PlanarPassivityExcess[] nonPassive) =>
        Result(Cause, nonPassive);

    private static PlanarSolveResult Result(string cause, params PlanarPassivityExcess[] nonPassive) =>
        new()
        {
            Points            = [],
            CoreFillCount     = 0,
            UnknownCount      = 0,
            StandardCount     = 0,
            CoreBuildMs       = 0,
            Findings          = [],
            NonPassivePoints  = nonPassive,
            NonPassivityCause = nonPassive.Length == 0 ? "" : cause,
        };

    /// <summary>A stand-in for whatever <c>PlanarSolve.NonPassivityCause</c> decided. The point of
    /// MIM-9 item 4 is that this file does not get to invent one, so the fixture supplies it.</summary>
    private const string Cause = "It is NOT the de-embedding: DeembedErrorFloor reads 0.0016.";

    [Fact]
    public void APassiveSweepDeclaresNothing()
        => Assert.Empty(EmSnpProvenance.ValidityCaveats(Result()));

    /// <summary>
    /// <b>The frequencies, not just the count.</b> "4 of 50 rows" tells a reader that some of their
    /// plot is wrong and not which of it — and the action it implies, raising the sweep's lower
    /// edge, cannot be taken from a count.
    /// </summary>
    [Fact]
    public void ANonPassiveSweepNamesTheBandAndTheWorstValue()
    {
        var caveats = EmSnpProvenance.ValidityCaveats(Result(
            new PlanarPassivityExcess(160e6, 1.0919),
            new PlanarPassivityExcess(320e6, 1.0384),
            new PlanarPassivityExcess(480e6, 1.0152),
            new PlanarPassivityExcess(640e6, 1.0046)));

        string caveat = Assert.Single(caveats);
        output.WriteLine(caveat);

        Assert.Contains("NOT A PASSIVE NETWORK", caveat, StringComparison.Ordinal);
        Assert.Contains("4 of these rows", caveat, StringComparison.Ordinal);
        Assert.Contains("160 MHz", caveat, StringComparison.Ordinal);
        Assert.Contains("640 MHz", caveat, StringComparison.Ordinal);
        Assert.Contains("1.0919", caveat, StringComparison.Ordinal);

        // ── MIM-9 item 4 — THE CAUSE IS CARRIED, NOT RE-GUESSED ────────────────────────────────
        //
        // What used to follow "the excess is this analysis rather than the design" was a second,
        // independently written sentence blaming the de-embedding's peel — a copy of the one in
        // PlanarSolve's panel note. MIM-9 corrected the panel; this copy would have gone on saying
        // the old thing in every .sNp ever written, which is the copy that outlives the session.
        Assert.Contains(Cause, caveat, StringComparison.Ordinal);
        Assert.DoesNotContain("peel divides by", caveat, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>ASCII only, and that is not a style rule.</b> Touchstone is written in an encoding this
    /// writer transliterates to: the first version of this line read "worst ?_max(S) = 1.0919" and
    /// "divides by a???" on the file. A caveat the file cannot spell is one nobody can act on.
    /// </summary>
    [Fact]
    public void TheCaveatSurvivesTheTouchstonesOwnEncoding()
    {
        // The attribution half of this line now comes from the engine, and its own ASCII gate is
        // MimThinLayerTests.M9_3 — this one still holds the half written here.
        string caveat = Assert.Single(EmSnpProvenance.ValidityCaveats(
            Result(new PlanarPassivityExcess(1.6e8, 1.09))));

        foreach (char c in caveat)
            Assert.True(c < 128, $"the caveat carries U+{(int)c:X4} ('{c}'), which the .sNp writes as '?'.");
    }

    /// <summary>ICF-4 R-icf4-5 — the file names the ports carrying the excess at the worst point, as the panel note does, in
    /// ASCII; a point that measured none adds nothing.</summary>
    [Fact]
    public void TheCaveatNamesTheCarryingPorts()
    {
        string caveat = Assert.Single(EmSnpProvenance.ValidityCaveats(Result(
            new PlanarPassivityExcess(31e9, 1.01),
            new PlanarPassivityExcess(40e9, 1.03) { CarriedBy = [(2, 0.9)] })));
        output.WriteLine(caveat);
        Assert.Contains("The excess is carried by port 2 (90% of the wave that gains most).", caveat, StringComparison.Ordinal);
        Assert.All(caveat, c => Assert.True(c < 128));
        Assert.DoesNotContain("carried by", Assert.Single(EmSnpProvenance.ValidityCaveats(Result(new PlanarPassivityExcess(1.6e8, 1.09)))),
                              StringComparison.Ordinal);
    }

    /// <summary>A clearance breach and a non-passive sweep are two different facts and the file
    /// carries both — the clearance one first, because it is the cause where there is one.</summary>
    [Fact]
    public void BothDeclarationsAreCarried()
    {
        var mesh = SurfaceMesher.Mesh(
            PlanarLineForCaveat(), new PlanarMeshSettings(Auto: false, CellsPerWavelength: 10,
                                                          EdgeMesh: false)).Mesh;
        var ports = PlanarPorts.ResolveAll(mesh,
        [
            new PlanarPort(1, new EmPoint(0,      5e-6), PlanarPortSide.MinX, 50.0) { LayerIndex = 0 },
            new PlanarPort(2, new EmPoint(400e-6, 5e-6), PlanarPortSide.MaxX, 50.0) { LayerIndex = 0 },
        ]);

        var r = new PlanarSolveResult
        {
            Points = [], CoreFillCount = 0, UnknownCount = 0, StandardCount = 0, CoreBuildMs = 0,
            Findings = [],
            FeedClearances = [new PlanarFeedClearance(ports[0].Number, PlanarNeighbourClass.Driven,
                                                      50e-6, 500e-6, 100e-6, 300e-6)],
            NonPassivePoints = [new PlanarPassivityExcess(160e6, 1.09)],
        };

        var caveats = EmSnpProvenance.ValidityCaveats(r);
        Assert.Equal(2, caveats.Count);
        Assert.Contains("OUTSIDE", caveats[0], StringComparison.Ordinal);
        Assert.Contains("NOT A PASSIVE NETWORK", caveats[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>MIM-9 item 4 — the guess was written TWICE, in two files, independently, and this is what
    /// stops a third.</b>
    ///
    /// <para><c>PlanarSolve</c> built the panel sentence and <c>EmSnpProvenance</c> built its own for
    /// the <c>.sNp</c> header. Both said the de-embedding's peel was the cause; MIM-12's controls
    /// showed it was not. Correcting one leaves the other wrong in every file already on disk — and
    /// the file is the copy that outlives the session. On <c>Authoring.cs</c>' terms: a scan of the
    /// comment-stripped sources, because a rule stated in prose is a rule that gets copied past.</para>
    /// </summary>
    [Fact]
    public void TheAttributionIsWrittenInExactlyOnePlace()
    {
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            string code = StripComments(File.ReadAllText(file));
            if (code.Contains("de-embedding rather than the", StringComparison.Ordinal))
                offenders.Add(Path.GetRelativePath(RepoRoot(), file));
        }

        string only = Assert.Single(offenders);
        Assert.Equal(Path.Combine("src", "Engine", "Mom", "PlanarSolve.cs"), only);

        // And the file that used to carry the second copy reads the decision instead of taking one.
        string prov = StripComments(File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Design", "Layout", "Em", "EmSnpProvenance.cs")));
        Assert.Contains("solve.NonPassivityCause", prov, StringComparison.Ordinal);
        Assert.DoesNotContain("peel", prov, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>Line and block comments removed, so a source scan cannot be satisfied by prose —
    /// and the comments in these very files quote the sentence being scanned for.</summary>
    private static string StripComments(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline),
                         @"//[^\n]*", "");

    private static PlanarProblem PlanarLineForCaveat() =>
        new([new PlanarConductorLayer("Metal1",
                [new PlanarPolygon([new EmPoint(0, 0), new EmPoint(400e-6, 0),
                                    new EmPoint(400e-6, 10e-6), new EmPoint(0, 10e-6)])],
                4.1e7, 3e-6)],
            GroundedSlab.GaAsStarter, 2e9);
}

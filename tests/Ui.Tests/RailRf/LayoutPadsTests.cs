// ================================================================
//  LayoutPadsTests.cs — brief-authored-board-1-layout-pads.md §7, the gate for a board the user DREW.
//
//  ── WHAT WAS BROKEN, AND WHY IT HAD NEVER BEEN NOTICED ────────────────────────────────────────
//
//  RailBoardInputs.Pads had exactly one producer — a companion `.ipc` through PdnBoardPads — so a
//  `.clay` somebody authored opened with Pads = [] and degraded in four places at once: no pick
//  list, every refdes anchor falling back to a coordinate, every mounting inductance a typed one,
//  and no parts-table row. None of the four FAILS. All four degrade to the path a board with no
//  companion files takes, so every answer stayed plausible and nothing said which half of the model
//  had never been reached.
//
//  ── THE ORACLE IS HAND ARITHMETIC, NOT ANOTHER circuitRF PATH ─────────────────────────────────
//
//  §7.1 and §7.2 ask for the exact DBU coordinates, written out. A test that compared PdnLayoutPads
//  against LayoutInstanceTransform would pass for as long as the two agreed with each other and
//  would say nothing about whether either is right. The fixture's pins are off BOTH axes for the
//  same reason: a mirror that is a no-op on a symmetric land proves nothing (the WB-C trap, in its
//  second form).
// ================================================================

using System.Diagnostics;
using System.Text.Json;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Design.Theming;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Tests.Examples;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class LayoutPadsTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-ab1-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bot = new(2, 0);

    private static long Um(double v) => (long)Math.Round(v * Dbu);
    private static long Mm(double v) => (long)Math.Round(v * 1e3 * Dbu);

    // The land pattern every test places. OFF BOTH AXES, deliberately — see this file's header.
    private static readonly (string Name, long X, long Y)[] LandPins =
        [("1", Um(-400), Um(250)), ("2", Um(400), Um(-150))];

    // ══ 1. A synthetic board with instances and no `.ipc` resolves pads ═════════════════════════

    /// <summary>
    /// <b>R-ab1-1c, R-ab1-1d.</b> Two footprint cells, known pin positions, a known transform — the
    /// exact DBU coordinates, against arithmetic written out here.
    /// </summary>
    [Fact]
    public void ABoardOfInstancesWithNoNetlistResolvesItsOwnPads()
    {
        var fx = Board(
            Place("Land", "C1", Mm(5), Mm(3)),
            Place("Land", "C2", Mm(9), Mm(7)));

        var pads = PdnLayoutPads.PadsOf(fx.View, fx.Clay, fx.Tech);

        Assert.Equal(4, pads.Count);
        Assert.All(pads, p => Assert.Equal(PdnPadSource.Artwork, p.Source));

        // R-ab1-1's scope: the nets arrive in brief 2, and null is already a representable state.
        Assert.All(pads, p => Assert.Null(p.Net));

        AssertPad(pads, "C1", "1", Mm(5) + Um(-400), Mm(3) + Um(250));
        AssertPad(pads, "C1", "2", Mm(5) + Um(400),  Mm(3) + Um(-150));
        AssertPad(pads, "C2", "1", Mm(9) + Um(-400), Mm(7) + Um(250));
        AssertPad(pads, "C2", "2", Mm(9) + Um(400),  Mm(7) + Um(-150));
    }

    // ══ 2. Rotation and mirror ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>§7.2.</b> The same board at 90°, at 217° (non-cardinal) and mirrored. The expected point is
    /// computed here from the rotation itself — <c>x cosθ − y sinθ</c>, <c>x sinθ + y cosθ</c>, with
    /// mirror negating local X first — rather than from <c>LayoutInstanceTransform</c>.
    /// </summary>
    [Theory]
    [InlineData(90.0, false)]
    [InlineData(217.0, false)]
    [InlineData(0.0, true)]
    [InlineData(90.0, true)]
    public void PadsLandWhereTheRotationAndTheMirrorPutThem(double deg, bool mirror)
    {
        var inst = Place("Land", "C1", Mm(5), Mm(3));
        inst.RotationDegrees = deg;
        inst.MirrorX = mirror;
        var fx = Board(inst);

        var pads = PdnLayoutPads.PadsOf(fx.View, fx.Clay, fx.Tech);
        Assert.Equal(2, pads.Count);

        double rad = deg * Math.PI / 180.0;
        // At the cardinals the cosine/sine are exact literals in the model, so they are here too —
        // Math.Cos(PI/2) is 6.1e-17 and rounding it would shift the answer by a DBU.
        double c = deg == 90.0 ? 0.0 : deg == 0.0 ? 1.0 : Math.Cos(rad);
        double s = deg == 90.0 ? 1.0 : deg == 0.0 ? 0.0 : Math.Sin(rad);

        foreach (var (name, lx, ly) in LandPins)
        {
            double mx = (mirror ? -1.0 : 1.0) * lx, my = ly;
            long x = Mm(5) + (long)Math.Round(mx * c - my * s);
            long y = Mm(3) + (long)Math.Round(mx * s + my * c);
            AssertPad(pads, "C1", name, x, y);
        }
    }

    // ══ 3. An array placement ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>R-ab1-1d.</b> One pad per pin per cell, the refdes the same on all of them. Worth stating
    /// because a via fence placed as a 1×N array is the shape that makes it look wrong.
    /// </summary>
    [Fact]
    public void AnArrayPlacementProducesOnePadPerPinPerCell()
    {
        var inst = Place("Land", "F1", Mm(2), Mm(2));
        inst.Rows = 2;
        inst.Cols = 3;
        inst.PitchX = Mm(1);
        inst.PitchY = Mm(4);
        var fx = Board(inst);

        var pads = PdnLayoutPads.PadsOf(fx.View, fx.Clay, fx.Tech);

        Assert.Equal(12, pads.Count);
        Assert.All(pads, p => Assert.Equal("F1", p.Refdes));

        // The array cell origin is the parent's own UNROTATED frame — pitch is not rotated with the
        // instance (LayoutInstanceTransform.ArrayCellOrigin's deliberate L3a simplification).
        Assert.Contains(pads, p => p.Pin == "1"
                                && p.X == Mm(2) + Mm(2) + Um(-400)
                                && p.Y == Mm(2) + Mm(4) + Um(250));
        Assert.Equal(6, pads.Count(p => p.Pin == "1"));
    }

    // ══ 4. What contributes nothing ═════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>R-ab1-1b, R-ab1-1c.</b> No designator is silence — a fabricated one would be worse than no
    /// pad, because an anchor would then resolve to it. An unresolved cell is silence PLUS the
    /// flatten's own sentence, not a second wording of it.
    /// </summary>
    [Fact]
    public void AnInstanceWithNoDesignatorOrNoCellContributesNothing()
    {
        var anonymous = Place("Land", null, Mm(1), Mm(1));
        var missing = Place("Land", "C9", Mm(2), Mm(2));
        missing.CellRef = "../../NoSuchCell";

        var fx = Board(anonymous, missing, Place("Land", "C1", Mm(5), Mm(3)));

        var notes = new List<string>();
        var pads = PdnLayoutPads.PadsOf(fx.View, fx.Clay, fx.Tech, null, notes);

        Assert.Equal(2, pads.Count);
        Assert.All(pads, p => Assert.Equal("C1", p.Refdes));

        // The anonymous placement is not reported at all: having no identity to draw is an ordinary
        // state, not a fault.
        string note = Assert.Single(notes);
        Assert.Equal(LayoutDesignFlatten.UnresolvedNote("../../NoSuchCell"), note);
    }

    // ══ 5. Which pad is which pin ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>R-ab1-4a / R-ab1-4b / R-ab1-4c.</b> Name first; index only when EVERY name fails; and a
    /// PARTIAL match is a refusal, never a fill-in — the Excellon-suppression class of decision,
    /// where the two readings differ by a swap nothing downstream can detect.
    /// </summary>
    [Fact]
    public void ThePinToPortJoinIsByNameThenByIndexAndRefusesAPartialMatch()
    {
        var fx = Board(
            Named("ByName", "U1", ["2", "1"]),   // pins named after the ports, in the other order
            Named("ByIndex", "U2", ["1", "2"]),  // generated chip land against named ports — index
            Named("Partial", "U3", ["A", "K"])); // one name matches its ports, one does not

        var ports = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["U1"] = ["1", "2"],
            ["U2"] = ["A", "K"],
            ["U3"] = ["A", "anode"],
        };

        var notes = new List<string>();
        var pads = PdnLayoutPads.PadsOf(
            fx.View, fx.Clay, fx.Tech, id => ports.TryGetValue(id, out var p) ? p : [], notes);

        // By NAME: pin "2" is port "2" wherever it sits in the list.
        Assert.Equal(["2", "1"], pads.Where(p => p.Refdes == "U1").Select(p => p.Pin));

        // By INDEX: pad i is port i, which is safe because the counts were already gated.
        Assert.Equal(["A", "K"], pads.Where(p => p.Refdes == "U2").Select(p => p.Pin));

        // The refusal is the row that matters, and the sentence names BOTH lists.
        Assert.DoesNotContain(pads, p => p.Refdes == "U3");
        string why = Assert.Single(notes);
        Assert.Contains("U3", why, StringComparison.Ordinal);
        Assert.Contains("[A, K]", why, StringComparison.Ordinal);       // its pins
        Assert.Contains("[A, anode]", why, StringComparison.Ordinal);   // its ports
        output.WriteLine(why);
    }

    /// <summary><b>R-ab1-4d.</b> No <c>SchematicId</c>, so no ports to join to — the pad is named by
    /// its own pin. <c>U1.1</c> resolves and <c>U1.VDD</c> does not, and that is honest: nothing on
    /// that board ever said VDD.</summary>
    [Fact]
    public void WithNoSchematicIdThePadIsNamedByItsPin()
    {
        var fx = Board(Place("Land", "C1", Mm(5), Mm(3)));
        var pads = PdnLayoutPads.PadsOf(fx.View, fx.Clay, fx.Tech, _ => ["VDD", "GND"]);

        Assert.Equal(["1", "2"], pads.Select(p => p.Pin));
    }

    // ══ 6 & 7. Precedence ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>R-ab1-3b, R-ab1-6c.</b> Per REFDES, not per board: a netlist naming two of three parts
    /// contributes those two and the artwork contributes the third. Whole-file precedence would
    /// throw away a correct pad because a DIFFERENT part was missing from a different file.
    /// </summary>
    [Fact]
    public void PrecedenceIsPerRefdesAndTheSummarySaysSo()
    {
        var fx = Board(
            Place("Land", "C1", Mm(5), Mm(3)),
            Place("Land", "C2", Mm(9), Mm(3)),
            Place("Land", "C3", Mm(13), Mm(3)));

        var netlist = Netlist(("C1", "1", "+3V3"), ("C1", "2", "GND"), ("C2", "1", "+3V3"), ("C2", "2", "GND"));

        var resolved = RailArtwork.PadsFor(fx.View, fx.Clay, fx.Tech, netlist);

        Assert.Equal(4, resolved.FromBoardNetlist);
        Assert.Equal(2, resolved.FromArtwork);

        // The netlist's own coordinates survive for the parts it names — the artwork's reading of C1
        // is DISCARDED, which is what "the netlist is the statement of record" means.
        var c1 = resolved.Pads.Where(p => p.Refdes == "C1").ToList();
        Assert.All(c1, p => Assert.Equal(PdnPadSource.BoardNetlist, p.Source));
        Assert.All(c1, p => Assert.Equal(Mm(20), p.X));

        Assert.All(resolved.Pads.Where(p => p.Refdes == "C3"),
                   p => Assert.Equal(PdnPadSource.Artwork, p.Source));

        Assert.Equal("6 pads: 4 from the board netlist, 2 from the artwork",
                     PdnPadSummary.Describe(resolved.Pads));
    }

    /// <summary><b>R-ab1-3c.</b> A refused netlist contributes NOTHING — the contract
    /// <see cref="BoardNetlist.Refusal"/> states, kept intact — and the artwork supplies
    /// everything.</summary>
    [Fact]
    public void ARefusedNetlistYieldsAllArtworkPads()
    {
        var fx = Board(Place("Land", "C1", Mm(5), Mm(3)));
        var refused = Netlist() with { Refusal = "its units are unstated" };

        var resolved = RailArtwork.PadsFor(fx.View, fx.Clay, fx.Tech, refused);

        Assert.Equal(0, resolved.FromBoardNetlist);
        Assert.Equal(2, resolved.FromArtwork);
        Assert.Equal("2 pads, from the artwork", PdnPadSummary.Describe(resolved.Pads));
    }

    // ══ 8. The mounting loop, computed from geometry ════════════════════════════════════════════

    /// <summary>
    /// <b>R-ab1-2d.</b> The pads this brief produces reach <c>PdnMountingLoopExtractor</c>, and the
    /// loop it computes is the one the ARTWORK's own coordinates give.
    /// </summary>
    /// <remarks>
    /// <b>The two nets are applied to the artwork's pads by this test, and that half is brief 2's.</b>
    /// Brief 1 scopes nets out (§8) and its pads come out with <c>Net</c> null, while
    /// <c>PdnMountingLoop</c> tells a power pad from a return pad BY NET — so until brief 2 names
    /// them, a board with no `.ipc` gets its pads and still no computed loop.
    ///
    /// <para><b>What is asserted against hand arithmetic is the quantity brief 1 owns: WHERE each
    /// pad is.</b> The via separation is the two pins' own spacing through the instance transform,
    /// written out here; and §4.3's whole physics is the −2M term, so a land whose pins are further
    /// apart must give a bigger loop. A wrong transform, a swapped pin/port join or a dropped array
    /// cell moves that separation, and it is the one term a part's placement actually changes.</para>
    /// </remarks>
    [Fact]
    public void TheArtworksOwnPadsComputeTheMountingLoopFromGeometry()
    {
        var narrow = LoopOf("Land", LandPins);
        var wide = LoopOf("WideLand", [("1", Um(-1200), Um(250)), ("2", Um(1200), Um(-150))]);

        // The separation the artwork itself states — dx and dy between the two pins, unrotated.
        Assert.Equal(Math.Sqrt(0.8 * 0.8 + 0.4 * 0.4) * 1e-3, narrow.Terms!.SeparationMetres, 9);
        Assert.Equal(Math.Sqrt(2.4 * 2.4 + 0.4 * 0.4) * 1e-3, wide.Terms!.SeparationMetres, 9);

        // §4.3's minus-two-M: further apart is a bigger loop. Nothing but the geometry moved.
        Assert.True(wide.Henries!.Value > narrow.Henries!.Value,
            $"the loop must RISE with the pins' spacing — {wide.Henries!.Value * 1e12:0.#} pH " +
            $"against {narrow.Henries!.Value * 1e12:0.#} pH");

        // §2.2's sanity band, which is the only check a user has on a number like this.
        Assert.InRange(narrow.Henries!.Value, 0.3e-9, 1.5e-9);

        // And the basis a reader sees on the parts table — the member this series exists to give a
        // producer.
        var model = new RailPartResolver(new PartLibrary()).Resolve(
            new RailPart { Refdes = "C1", PartNumber = "X" }, 3.3,
            new Dictionary<string, double> { ["C1"] = narrow.Henries!.Value });

        Assert.Equal(RailMountingBasis.ComputedFromGeometry, model.MountingBasis);
        output.WriteLine($"C1: {narrow.Henries!.Value * 1e12:0.#} pH narrow, " +
                         $"{wide.Henries!.Value * 1e12:0.#} pH wide");
    }

    /// <summary>One part's computed loop, off a board whose only statement about it is its
    /// artwork.</summary>
    private PdnMountingLoop LoopOf(string cell, (string Name, long X, long Y)[] pins)
    {
        Land(cell, pins);
        var fx = Board(Place(cell, "C1", Mm(5), Mm(3)));

        var pads = PdnLayoutPads.PadsOf(fx.View, fx.Clay, fx.Tech)
            .Select(p => p with { Net = p.Pin == "1" ? "VDD" : "GND" })   // brief 2's half, stated
            .ToList();

        // A via sitting exactly on each pad, so the pad-to-via trace is zero length and the loop is
        // the three via terms alone.
        var shapes = new List<LayoutShape>(fx.View.Shapes);
        foreach (var p in pads)
            shapes.Add(new ViaShape { Layer = Top, X = p.X, Y = p.Y, DrillSize = Um(300), PadSize = Um(600) });

        var loop = PdnMountingLoopExtractor.Compute(new PdnMountingLoopRequest
        {
            Rail = new RailSpec { Name = "VDD", NetName = "VDD", ReferenceLayer = Bot },
            Shapes = shapes,
            Technology = TechFixture(withPowerPlane: true),
            DbuPerMicron = Dbu,
            ReferenceNet = "GND",
            SearchRadiusMetres = 1e-6,
            Pads = pads,
        }, "C1");

        Assert.Null(loop.Unresolved);
        return loop;
    }

    // ══ 6b. The window's own row-to-board selection ═════════════════════════════════════════════

    /// <summary>
    /// <b>R-ab1-6b.</b> Clicking a part row marks that part ON the board, through
    /// <c>PdnAttachments.Resolve</c> over <c>RailBoardInputs.Pads</c> — so on a board with no
    /// companion files at all it used to mark nothing, silently, because there were no pads for the
    /// refdes to resolve against. Asserted rather than assumed.
    /// </summary>
    [Fact]
    public void APartRowMarksItsPadsOnAnAuthoredBoardWithNoCompanionFiles()
    {
        var fx = Board(Place("Land", "C1", Mm(5), Mm(3)));

        var document = new RailDocument { Name = "Authored" };
        var rail = new RailSpec { Name = "VDD", ReferenceLayer = Bot };
        rail.Parts.Add(new RailPart { Refdes = "C1", PartNumber = "CAP-100N" });
        document.Rails.Add(rail);

        var vm = new Ui.RailRf.RailRfViewModel(document, Path.Combine(_root, "Authored.crail"))
        {
            PostToUi = a => a(),
            RunOffThread = (work, _) => System.Threading.Tasks.Task.FromResult(work()),
        };

        vm.ApplyImport(
            new Ui.RailRf.RailImportOptions(),
            new Ui.RailRf.RailBoardInputs
            {
                Shapes = RailArtwork.FlattenedShapes(fx.View, fx.Clay, fx.Tech),
                View = fx.View,
                Technology = fx.Tech,
                DbuPerMicron = Dbu,
                ArtworkCellRef = fx.Clay,
            });

        Assert.Equal(2, vm.Board!.Pads.Count);

        vm.SelectedRailName = "VDD";
        vm.SelectedPart = vm.Parts.Single(p => p.Refdes == "C1");

        var mark = vm.PartHighlight;
        Assert.NotNull(mark);
        Assert.Equal(2, mark!.Pads.Count);
        Assert.Contains((Mm(5) + Um(-400), Mm(3) + Um(250)), mark.Pads);
    }

    // ══ 9. The CLI, as a process ════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>R-ab1-5c.</b> <c>circuitrf rail board.clay --load U1.VDD=120mA</c> was refused before this
    /// brief — nothing headless could resolve a refdes on a board nobody had exported an `.ipc` for.
    /// It runs now, with no display at any step, and its <c>--json</c> pad count is the in-process
    /// one.
    /// </summary>
    [Fact]
    public void TheVerbRunsAnAuthoredBoardAndItsJsonPadCountIsTheInProcessOne()
    {
        var fx = CliFixture();

        var (exit, stdout, stderr) = RunCli(
            "rail", fx.Clay, "--load", "U1.VDD=120mA", "--json");
        output.WriteLine(stderr);
        Assert.Equal(0, exit);

        using var doc = JsonDocument.Parse(stdout);
        var rail = doc.RootElement.GetProperty("result").GetProperty("rail");

        int inProcess = RailArtwork.PadsFor(fx.View, fx.Clay, fx.Tech, null).Pads.Count;
        Assert.Equal(inProcess, rail.GetProperty("pads").GetInt32());
        Assert.Equal(inProcess, rail.GetProperty("padsFromArtwork").GetInt32());
        Assert.Equal(0, rail.GetProperty("padsFromBoardNetlist").GetInt32());

        // The load RESOLVED TO COPPER rather than refusing, which is the whole point of the run:
        // `U1.VDD` names a pin the footprint itself names, on a board with no netlist anywhere.
        var ports = rail.GetProperty("rails")[0].GetProperty("ports");
        output.WriteLine(ports.ToString());
        Assert.Contains("U1", ports.ToString(), StringComparison.Ordinal);
    }

    // ══ 10. Over the flatten ceiling ════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>R-ab1-5d.</b> The flatten's outcomes gate the pads: a board whose geometry was never read
    /// must not come back with a confident pad list over it.
    /// </summary>
    [Fact]
    public void OverTheFlattenCeilingThereAreNoPadsAndTheNoteSaysSo()
    {
        var huge = Place("Land", "F1", 0, 0);
        huge.Rows = 1_000;
        huge.Cols = 1_000;
        huge.PitchX = Mm(1);
        huge.PitchY = Mm(1);
        var fx = Board(huge);

        var notes = new List<string>();
        var pads = PdnLayoutPads.PadsOf(fx.View, fx.Clay, fx.Tech, null, notes);

        Assert.Empty(pads);
        Assert.Contains("flatten", Assert.Single(notes), StringComparison.Ordinal);
    }

    // ══ 11. The shipped example is unchanged ════════════════════════════════════════════════════

    /// <summary>
    /// <b>§7.11.</b> The Power Rail example ships an `.ipc` naming every part on its board, so
    /// R-ab1-3a gives the netlist precedence over all fourteen and the pad set that reaches the
    /// solver is byte-for-byte the one <c>PdnBoardPads</c> produced before this brief. <b>This is the
    /// gate that catches a precedence rule implemented backwards</b> — reversed, the artwork's own
    /// reading would win, every coordinate would move, and every mounting loop with it.
    /// </summary>
    [Fact]
    public void TheShippedPowerRailExampleAnswersFromItsNetlistExactlyAsBefore()
    {
        string root = PowerRailFootprintCells.ExampleRoot();
        string clay = Path.Combine(root, "Sensor board", "layout", "Board.clay");
        string crail = Path.Combine(root, "Sensor board", "Sensor board.crail");

        var document = RailDocumentIo.LoadFromFile(crail);
        var view = LayoutPersistence.LoadFromFile(clay);
        var tech = PowerRailFootprintCells.ExampleTechnology();
        var netlist = RailArtwork.ResolveBoardNetlist(document, crail, view.DbuPerMicron, out _, out _);

        var before = PdnBoardPads.PadsOf(netlist);
        var after = RailArtwork.PadsFor(view, clay, tech, netlist);

        Assert.Equal(before, after.Pads);
        Assert.Equal(0, after.FromArtwork);
        // DISTINCT, and the difference is PadsFor's own documented one rather than a change here:
        // it de-duplicates, because both sources legitimately describe the same stitching via and
        // two identical seeds are redundant rather than wrong. The example's netlist states a via
        // IN each land, so a pad's point and its via's point are the same point said twice — what
        // this asserts is that the ARTWORK contributed no point the netlist had not already named.
        Assert.Equal(PdnBoardPads.NetPointsOf(netlist).Distinct(), after.NetPoints);
        Assert.Equal($"{before.Count} pads, from the board netlist", PdnPadSummary.Describe(after.Pads));

        output.WriteLine($"{before.Count} pads, all from the netlist; artwork contributed {after.FromArtwork}");
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private sealed record Fx(LayoutView View, string Clay, Technology Tech);

    private static void AssertPad(IReadOnlyList<PdnPad> pads, string refdes, string pin, long x, long y)
    {
        var pad = Assert.Single(pads, p => p.Refdes == refdes && p.Pin == pin);
        Assert.Equal((x, y), (pad.X, pad.Y));
    }

    /// <summary>A board cell holding <paramref name="instances"/>, with its technology on disk.</summary>
    private Fx Board(params LayoutInstance[] instances)
    {
        var tech = TechFixture();
        Directory.CreateDirectory(Path.Combine(_root, "tech"));
        string techPath = Path.Combine(_root, "tech", "Board.ctech");
        if (!File.Exists(techPath)) TechPersistence.SaveToFile(techPath, tech);
        if (!File.Exists(Path.Combine(_root, ".cws")))
            WorkspacePersistence.SaveToFile(
                Path.Combine(_root, ".cws"),
                new CwsFile { DefaultTechRef = Path.Combine("tech", "Board.ctech") });

        Land("Land", LandPins);

        string cellDir = CellFolder.CreateCellFolder(_root, "Board");
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layoutDir);

        var view = new LayoutView { DbuPerMicron = Dbu, TechRef = Path.Combine("..", "..", "tech", "Board.ctech") };
        view.Shapes.Add(new RectShape { Layer = Top, X1 = 0, Y1 = 0, X2 = Mm(30), Y2 = Mm(30) });
        view.Shapes.Add(new RectShape { Layer = Bot, X1 = 0, Y1 = 0, X2 = Mm(30), Y2 = Mm(30) });
        foreach (var i in instances) view.Instances.Add(i);

        string clay = Path.Combine(layoutDir, "Board.clay");
        LayoutPersistence.SaveToFile(clay, view);
        return new Fx(view, clay, tech);
    }

    /// <summary>A land pattern cell — pins plus a copper pad under each, which is what makes it a
    /// land rather than a list of coordinates.</summary>
    private void Land(string name, (string Name, long X, long Y)[] pins)
    {
        string cellDir = CellFolder.CreateCellFolder(_root, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layoutDir);

        var cell = new LayoutView { DbuPerMicron = Dbu };
        foreach (var (pinName, x, y) in pins)
        {
            cell.Pins.Add(new LayoutPin { Name = pinName, X = x, Y = y, WidthDbu = Um(250), Layer = Top });
            cell.Shapes.Add(new RectShape
            {
                Layer = Top, Pin = pinName,
                X1 = x - Um(125), Y1 = y - Um(125), X2 = x + Um(125), Y2 = y + Um(125),
            });
        }
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, name + ".clay"), cell);
    }

    private LayoutInstance Place(string cell, string? refdes, long x, long y) => new()
    {
        CellRef = Path.Combine("..", "..", cell),
        X = x, Y = y, Mag = 1.0, RefDes = refdes,
    };

    /// <summary>A placement whose footprint's pins are <paramref name="pinNames"/> and whose
    /// <c>SchematicId</c> is set, so R-ab1-4's port join applies to it.</summary>
    private LayoutInstance Named(string cell, string schematicId, string[] pinNames)
    {
        Land(cell, [.. pinNames.Select((n, i) => (n, Um(-400 + 800 * i), Um(250 - 400 * i)))]);
        var inst = Place(cell, null, Mm(5), Mm(3 * (schematicId[^1] - '0')));
        inst.SchematicId = schematicId;
        return inst;
    }

    /// <summary>A board netlist standing every part at one point, so a pad that came from it is told
    /// from a pad that came from the artwork by its COORDINATE and not only by its stamp.</summary>
    private static BoardNetlist Netlist(params (string Refdes, string Pin, string Net)[] rows)
        => new("Board.ipc", null, BoardNetlistUnits.MillimetreThousandth, BoardNetlistUnitsEvidence.Declared,
               "BOARD",
               [.. rows.Select((r, i) => new BoardNetlistRecord(
                   317, r.Net, r.Refdes, r.Pin, false, null, null, 1, Mm(20), Mm(20), i + 1))],
               rows.Length, 0, 0, default, []);

    /// <param name="withPowerPlane">Adds an inner conductor between the surface and the reference.
    /// <b>The mounting loop needs one</b>: with only the mounting surface and the reference in the
    /// stackup, <c>PdnMountingLoop</c> has no third conductor to call the rail's plane, takes its
    /// "the part sits on the rail's own copper" branch, and the loop loses the −2M term the test is
    /// about.</param>
    private static Technology TechFixture(bool withPowerPlane = false)
    {
        var mid = new LayerKey(3, 0);
        var tech = new Technology { Name = "Board" };
        tech.Layers =
        [
            new LayerDef { Key = Top, Name = "TOP", ZOrder = 0, Color = new Rgba(200, 80, 40, 255) },
            new LayerDef { Key = Bot, Name = "BOT", ZOrder = 1, Color = new Rgba(40, 90, 200, 255) },
            .. withPowerPlane
                ? new[] { new LayerDef { Key = mid, Name = "MID", ZOrder = 2, Color = new Rgba(90, 160, 90, 255) } }
                : [],
        ];
        tech.Stackup.Layers =
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "TOP",
                ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Top],
            },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "PP", ThicknessDbu = Um(200), Epsr = 4.3, TanD = 0.02 },
            .. withPowerPlane
                ? new StackupLayer[]
                {
                    new()
                    {
                        Kind = StackupKind.Conductor, Name = "MID",
                        ThicknessDbu = Um(18), SigmaSm = 5.8e7, DrawingLayers = [mid],
                    },
                    new() { Kind = StackupKind.Dielectric, Name = "CORE", ThicknessDbu = Mm(1.2), Epsr = 4.3, TanD = 0.02 },
                }
                : [],
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "BOT",
                ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Bot], IsGroundReference = true,
            },
        ];
        return tech;
    }

    // ── the CLI's own fixture: a board, a `.crail` beside it, and no companion files ─────────────

    private Fx CliFixture()
    {
        var tech = TechFixture();
        Directory.CreateDirectory(Path.Combine(_root, "tech"));
        TechPersistence.SaveToFile(Path.Combine(_root, "tech", "Board.ctech"), tech);
        WorkspacePersistence.SaveToFile(
            Path.Combine(_root, ".cws"),
            new CwsFile { DefaultTechRef = Path.Combine("tech", "Board.ctech") });

        // The load's pin is named VDD on the footprint itself — which is exactly R-ab1-4d's point:
        // `U1.VDD` resolves because the board says VDD, with no schematic and no netlist anywhere.
        Land("Chip", [("VDD", 0, 0), ("GND", Um(300), 0)]);

        string cellDir = CellFolder.CreateCellFolder(_root, "Panel");
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layoutDir);

        var view = new LayoutView { DbuPerMicron = Dbu, TechRef = Path.Combine("..", "..", "tech", "Board.ctech") };
        view.Shapes.Add(new RectShape { Layer = Top, X1 = 0, Y1 = 0, X2 = Mm(30), Y2 = Mm(0.4) });
        view.Shapes.Add(new RectShape { Layer = Bot, X1 = 0, Y1 = 0, X2 = Mm(30), Y2 = Mm(0.4) });
        view.Instances.Add(Place("Chip", "U1", Mm(29.8), Mm(0.2)));

        string clay = Path.Combine(layoutDir, "Panel.clay");
        LayoutPersistence.SaveToFile(clay, view);

        var doc = new RailDocument
        {
            Name = "Panel",
            ArtworkCellRef = Path.GetRelativePath(cellDir, clay),
        };
        var vdd = new RailSpec { Name = "VDD", ReferenceLayer = Bot };
        vdd.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Point = (Mm(0.2), Mm(0.2)) },
            OpenCircuitVoltageV = 3.7,
            SeriesResistanceOhms = 0.05,
        });
        doc.Rails.Add(vdd);
        RailDocumentIo.SaveToFile(Path.Combine(cellDir, "Panel.crail"), doc);

        return new Fx(view, clay, tech);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
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
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(LayoutPadsTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }
}

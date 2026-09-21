// ================================================================
//  GerberSetToACurveTests.cs — docs/sonnet-briefs/brief-railrf-27-gerber-set-to-a-curve.md §6
//
//  THE SCENARIO THIS FILE IS MEASURED AGAINST. A designer with a fabrication output set and no
//  circuitRF experience imports the Gerber set into a new workspace, places their own footprints in
//  the `.clay`, opens the board in railRF, picks the rail, confirms the reference, and gets a |Z|
//  curve with their decoupling in it. Brief 26 delivered the parts rows; on the reported board the
//  path still broke at the reference for two independent reasons and at the curve for a third.
//
//  ── ONE TEST PER CLAIM, AND ONE THAT IS THE POINT OF THE BRIEF ────────────────────────────────
//
//  The end-to-end gate is the first test. Everything before its last assertion is a step somebody
//  reported being unable to take, and the last one is the deliverable: a curve, from a fabrication
//  output set, without anybody editing JSON.
//
//  ── THE FIXTURE, AND THE ONE THING IT HAD TO GET RIGHT ────────────────────────────────────────
//
//  A hand-authored set, following L4e/L4f/L4g/GI1's precedent — worth less than a real set as a
//  dialect test, costs nothing to redistribute, names no tool or product. Its inner plane file is
//  named for the NET it carries, which is exactly what the reported board's was and is what matches
//  no copper pattern in `GerberLayerIdentity`.
//
//  THE GROUND POUR THE USER CLICKS IS ON THE BOTTOM LAYER, not on the inner plane. That is not
//  decoration: `PdnRailRegions.Walk` seeds the rail off every layer EXCEPT the reference layer, so a
//  click on the reference layer's own copper resolves to nothing and comes back as the ordinary
//  "resolves to no copper" refusal. The reported board's rail was a pour galvanically joined to the
//  plane by stitching vias — one piece of copper, reached from a layer the seeding does look at —
//  and that is the only shape R-rail27-2 is about.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Clipper2Lib;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class GerberSetToACurveTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-r27-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static long Mm(double v) => (long)Math.Round(v * 1e3 * Dbu);
    private static long Um(double v) => (long)Math.Round(v * Dbu);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  1 — THE END-TO-END GATE
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A fabrication output set to a |Z| curve, with no display and no hand-written JSON.</b>
    /// </summary>
    /// <remarks>
    /// Every numbered step below is one the brief says a designer reported being unable to take.
    /// Step 8 is the deliverable: the curve is not the bare-copper one, and it dips where the part
    /// the designer named resonates.
    /// </remarks>
    [Fact]
    public void TheWholeScenario_FromAFabricationOutputSet_ToACurveWithDecouplingInIt()
    {
        string set = WriteGerberSet();

        // ── 1. Import it. The inner plane is NAMED as unclassified, with its shape count ───────
        var asArtwork = GerberImport.Import(FilesIn(set), Folder("import-1"), "board", null, Dbu);
        output.WriteLine(string.Join("\n", asArtwork.Messages));

        string unclassified = Assert.Single(
            asArtwork.Messages, m => m.Contains("NOT CLASSIFIED AT ALL", StringComparison.Ordinal));
        Assert.Contains("gnd", unclassified, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"\d+ shape\(s\)", unclassified);

        // Two conductors, because the plane became artwork — which is the defect, stated.
        Assert.Equal(2, Conductors(asArtwork.Technology!).Count);

        // ── 2. Answer the layer question: copper, after the top conductor ──────────────────────
        var imported = GerberImport.Import(
            FilesIn(set), Folder("import-2"), "board", null, Dbu,
            resolveLayerMapping: rows => AnswerCopperAfterTheTop(rows, "gnd"));

        var conductors = Conductors(imported.Technology!);
        Assert.Equal(3, conductors.Count);
        Assert.All(conductors, c => Assert.Single(c.DrawingLayers));

        // In the stated ORDER — the whole reason the position is asked rather than derived.
        Assert.Contains("gnd", conductors[1].Name, StringComparison.OrdinalIgnoreCase);

        // ── 3. The two capacitors, stated by the set's own netlist companion ───────────────────
        //     (the placed footprints' pads, in the form a fabrication output set carries them).

        // ── 4. Open it in railRF: three conductors offered, none disabled ──────────────────────
        var vm = OpenInRailRf(imported);

        Assert.Equal(3, vm.ReferenceLayerOptions.Count);
        Assert.All(vm.ReferenceLayerOptions, o => Assert.True(o.IsSelectable));

        // R-rail27-1d: and NOTHING is said about unclaimed copper, because every conductor is bound.
        Assert.Equal("", vm.UnclaimedCopperNote);

        var plane = vm.ReferenceLayerOptions.Single(
            o => o.Name.Contains("gnd", StringComparison.OrdinalIgnoreCase));

        // ── 5. Pick the GROUND pour: refused BY NAME. Pick the supply pour: a rail ─────────────
        var onTheGround = vm.PickRailAt(Mm(10), Mm(9), "ground pour");
        onTheGround.ReferenceLayer = plane.Key;
        vm.RunCommand.Execute(null);

        output.WriteLine(vm.Refusal?.Sentence ?? "(no refusal)");
        Assert.Contains("anchored on the copper of its own reference return",
                        vm.Refusal!.Sentence, StringComparison.Ordinal);
        Assert.Equal(RailRefusalControl.ReferenceLayer, vm.Refusal!.Control);

        vm.RemoveRailCommand.Execute(null);

        var rail = vm.PickRailAt(Mm(10), Mm(5), "VDD pour");

        // R-rail27-4: the pour pick SEEDS its source, like every other add gesture.
        Assert.Equal(RailRfViewModel.SeededSourceVoltageV,
                     Assert.Single(rail.Sources).OpenCircuitVoltageV);
        Assert.Equal(1, vm.SeededRowCount);

        // The observation port is the USER's — §2.3 step 4, and no file in a Gerber set says where
        // the IC that matters is. So is the regulator's own output impedance: a source stating
        // neither shorts the rail to its reference at every frequency, which the sweep refuses BY
        // NAME rather than answering with zeros.
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Point = (Mm(15), Mm(5)) } });
        rail.Sources[0] = rail.Sources[0] with { SeriesResistanceOhms = 0.05, SeriesInductanceHenries = 2e-9 };

        // ── 6. Confirm the reference, solve, and be offered exactly the two capacitors ─────────
        vm.SelectedReferenceLayer = plane;
        vm.ConfirmReferenceCommand.Execute(null);
        vm.RunCommand.Execute(null);

        Assert.Null(vm.Refusal);

        // The bare-copper curve: the board and nothing on it, which is what brief 26's own §10
        // leaves a designer with.
        double[] bareCopper = [.. vm.Sweep!.Ports[0].MagnitudeOhms];
        double[] hz = [.. vm.Sweep!.FrequenciesHz];

        output.WriteLine(string.Join("\n", vm.PartOffer.Lines));
        Assert.Equal(["C1", "C2"], vm.PartOffer.Offered.Select(c => c.Refdes));
        Assert.Equal(2, vm.AddDiscoveredParts());

        // Every row is UNRESOLVED — the state this brief starts from.
        Assert.All(vm.Parts, p => Assert.Equal(RailPartRowViewModel.UnresolvedText, p.PartNumber));

        // ── 7. Say which part they are, and give that part a capacitance and an f0 ─────────────
        Assert.Equal(2, vm.AssignPartNumber(["C1", "C2"], "PN-100N"));
        Assert.All(vm.Parts, p => Assert.Equal("PN-100N", p.PartNumber));

        vm.PartLibrary = OneHundredNanofarads("PN-100N");
        vm.RunCommand.Execute(null);

        Assert.All(vm.Parts, p => Assert.False(p.IsUnresolved));

        // ── 8. THE DELIVERABLE: a curve with the decoupling in it ─────────────────────────────
        double[] withParts = [.. vm.Sweep!.Ports[0].MagnitudeOhms];
        double[] withHz    = [.. vm.Sweep!.FrequenciesHz];

        // The two curves are not even on the same axis: the resonance search ADDS points where it
        // finds one, which is R-rail14-4 and is itself evidence that there is now something on this
        // rail to resonate.
        Assert.True(withHz.Length > hz.Length);
        Assert.NotEmpty(vm.Sweep!.AddedHz);

        // The series resonance is the part's own: |Z| at f0 is far below the bare-copper curve,
        // which rises with frequency because copper and a source inductance alone are an inductor.
        int bare = NearestIndex(hz, SelfResonanceHz);
        int with = NearestIndex(withHz, SelfResonanceHz);

        output.WriteLine($"f0 = {SelfResonanceHz:0.###e+0} Hz · bare {bareCopper[bare]:0.#####} Ω " +
                         $"at {hz[bare]:0.###e+0} · with parts {withParts[with]:0.#####} Ω " +
                         $"at {withHz[with]:0.###e+0}");

        Assert.True(withParts[with] < bareCopper[bare],
                    $"the decoupling did not lower |Z| at its own resonance " +
                    $"({withParts[with]} vs {bareCopper[bare]} Ω)");

        // And it is a MINIMUM of the new curve, not merely a lower number — which is what makes it
        // the part's own series resonance rather than a curve that simply moved.
        Assert.Equal(withParts.Min(), withParts.Skip(Math.Max(0, with - 3)).Take(7).Min());
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  2 — R-rail27-1a: the import names what it left out, and is SILENT when it left out nothing
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The unclassified line is present with its shape count, and ABSENT when every file
    /// classified</b> — a message that is always there is one nobody reads.
    /// </summary>
    /// <remarks>
    /// The shape count is the actionable half. "Unclassified" says nothing; <i>328 shapes</i> is what
    /// distinguishes a plane from a stray drawing, and it is free because the import already holds
    /// the artwork.
    /// </remarks>
    [Fact]
    public void TheImportNamesEveryLayerLeftOutOfTheStackup_AndSaysNothingWhenThereAreNone()
    {
        var withPlane = GerberImport.Import(
            FilesIn(WriteGerberSet()), Folder("named"), "board", null, Dbu);

        string line = Assert.Single(
            withPlane.Messages, m => m.Contains("NOT CLASSIFIED AT ALL", StringComparison.Ordinal));
        Assert.Contains("gnd", line, StringComparison.OrdinalIgnoreCase);

        // The mask and the legend are the OTHER half of the same message and mean something
        // different — a stated decision, with its own paragraph above it.
        Assert.Contains("Imported as artwork and not in the stackup", line, StringComparison.Ordinal);
        Assert.Contains("Soldermask", line, StringComparison.Ordinal);

        // ── every file classified ─────────────────────────────────────────────────────────────
        string clean = Folder("all-classified");
        File.WriteAllText(Path.Combine(clean, "board.gtl"), Pour("Copper,L1,Top,Signal", 0, 0, 20, 10));
        File.WriteAllText(Path.Combine(clean, "board.gbl"), Pour("Copper,L2,Bot,Signal", 0, 0, 20, 10));

        var settled = GerberImport.Import(FilesIn(clean), Folder("clean"), "board", null, Dbu);
        Assert.DoesNotContain(
            settled.Messages, m => m.Contains("NOT CLASSIFIED AT ALL", StringComparison.Ordinal));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  3 — R-rail27-1c: the technology says it too, as a WARNING
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>An INNER conductor with no drawing layer is a warning; an outermost one is not.</b>
    /// </summary>
    /// <remarks>
    /// <b>The exemption is physics, not convenience.</b> An outermost conductor with no drawing layer
    /// is BLANKET metal — the shipped <c>mmic-GaAs_2LM_100um</c>'s backside ground is exactly that,
    /// the whole die backside, unpatterned, with no artwork to point at and nothing missing. An inner
    /// one cannot be blanket whatever the process: unpatterned metal in the middle of a stack shorts
    /// every via that passes through it. So an inner conductor claiming no drawing layer is always
    /// artwork somebody forgot to attach.
    ///
    /// <para>A WARNING and never an error, because a stackup skeleton legitimately has such entries
    /// before the artwork arrives — every <c>TechProblem</c> reaches <c>circuitrf check</c> at
    /// warning severity, so it still exits 0.</para>
    /// </remarks>
    [Fact]
    public void AnInnerConductorWithNoDrawingLayer_IsReported_AndABlanketOuterOneIsNot()
    {
        var tech = ThreeConductors();
        tech.Stackup.Layers.Single(l => l.Name == "GND").DrawingLayers.Clear();

        string problem = Assert.Single(
            TechValidation.Validate(tech), p => p.Contains("claims no drawing layer", StringComparison.Ordinal));
        output.WriteLine(problem);
        Assert.Contains("GND", problem, StringComparison.Ordinal);
        Assert.Contains("cannot be named as a reference return", problem, StringComparison.Ordinal);

        // Blanket backside metal — the outermost conductor, unpatterned. Not a defect.
        var blanket = ThreeConductors();
        blanket.Stackup.Layers.Single(l => l.Name == "BOT").DrawingLayers.Clear();
        Assert.DoesNotContain(
            TechValidation.Validate(blanket), p => p.Contains("claims no drawing layer", StringComparison.Ordinal));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  4 — R-rail27-1d: railRF says it at OPEN, with no run performed
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The copper that belongs to no conductor is named on the specification panel before any
    /// run.</b>
    /// </summary>
    /// <remarks>
    /// The extraction already reports this and it is useless here: that report needs a run, a run
    /// needs a confirmed reference, and the missing conductor is precisely why there is no reference
    /// to confirm. Circular, with the user inside the circle.
    /// </remarks>
    [Fact]
    public void RailRfNamesTheUnclaimedCopper_AtOpen_WithNoRunPerformed()
    {
        var tech = ThreeConductors();
        tech.Stackup.Layers.Single(l => l.Name == "GND").DrawingLayers.Clear();

        var vm = Bare();
        vm.Board = new RailBoardInputs
        {
            Technology   = tech,
            DbuPerMicron = Dbu,
            Shapes       = [Rect(Inner, 0, 0, 20, 10), Rect(Top, 2, 2, 18, 8)],
        };

        Assert.True(vm.HasUnclaimedCopperNote);
        output.WriteLine(vm.UnclaimedCopperNote);
        Assert.Contains("no Conductor entry of the stackup claims", vm.UnclaimedCopperNote, StringComparison.Ordinal);
        Assert.Contains("1 shape(s)", vm.UnclaimedCopperNote, StringComparison.Ordinal);

        // Nothing was run to produce it.
        Assert.Null(vm.Sweep);

        // Attach the layer and the note goes away, which is what makes it progressive. A WHOLE new
        // board, because `RailBoardInputs` is a record and mutating the technology it already holds
        // is structurally the same value — the setter would not fire.
        vm.Board = new RailBoardInputs
        {
            Technology   = ThreeConductors(),
            DbuPerMicron = Dbu,
            Shapes       = [Rect(Inner, 0, 0, 20, 10), Rect(Top, 2, 2, 18, 8)],
        };
        Assert.False(vm.HasUnclaimedCopperNote);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  5 — R-rail27-2: the refusal, and the case it must NOT fire on
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A rail anchored on its own return is refused; a rail that merely REACHES the reference
    /// layer is not.</b>
    /// </summary>
    /// <remarks>
    /// <b>The second half is the one that would be missed</b>, and it is the difference between a
    /// refusal and a tool that refuses every real board. A rail with copper on the reference layer —
    /// a mixed plane carrying a power pour, a barrel through an antipad — is an ordinary board, and
    /// both extractors already report it BY NAME as a diagnostic. It must stay a diagnostic.
    ///
    /// <para>What is refused is the reported board's shape: the pick landed on a ground pour, so the
    /// rail's own copper IS the return and there is nothing left on the reference layer once it is
    /// taken out.</para>
    /// </remarks>
    [Fact]
    public void ARailAnchoredOnItsOwnReturn_IsRefused_AndOneThatMerelyReachesTheReferenceLayerIsNot()
    {
        // ── the reported board: a bottom-side ground pour, stitched to the inner plane ────────
        var onItsOwnReturn = PdnRailRegions.Walk(
            new Dictionary<LayerKey, Paths64>
            {
                [Top]   = [Box(Mm(2), Mm(12), Mm(18), Mm(18))],    // the supply pour, clear of the stitch
                [Inner] = [Box(0, 0, Mm(20), Mm(10))],             // the plane — the reference
                [Bot]   = [Box(0, 0, Mm(20), Mm(10))],             // the ground pour, same net
                [Via]   = [Box(Mm(9), Mm(4), Mm(11), Mm(6))],      // the stitch, plane to pour
            },
            StitchedTech(), [], railNet: null,
            referenceLayer: Inner, referenceNet: null,
            extraRailSeeds: [(Mm(10), Mm(5))],                     // the click, on the ground pour
            bareCoordinateSeeds: [(Mm(10), Mm(5))]);

        output.WriteLine(onItsOwnReturn.OwnReturnRefusal ?? "(none)");
        Assert.NotNull(onItsOwnReturn.OwnReturnRefusal);
        Assert.Contains("nothing for current to return through",
                        onItsOwnReturn.OwnReturnRefusal!, StringComparison.Ordinal);

        // ── and the case that must NOT fire: a rail whose copper reaches the reference LAYER ───
        //     A power pour on the mixed inner layer, stitched up to the supply pour on top. The
        //     plane's own piece survives, so a return still exists.
        var reachesTheLayer = PdnRailRegions.Walk(
            new Dictionary<LayerKey, Paths64>
            {
                [Top]   = [Box(Mm(2), Mm(12), Mm(18), Mm(18))],    // the supply pour
                [Inner] =
                [
                    Box(0, 0, Mm(20), Mm(10)),                     // the ground plane
                    Box(Mm(2), Mm(14), Mm(18), Mm(18)),            // a supply pour ON the same layer
                ],
                [Via]   = [Box(Mm(9), Mm(15), Mm(11), Mm(17))],    // joining the two supply pours
            },
            StitchedTech(), [], railNet: null,
            referenceLayer: Inner, referenceNet: null,
            extraRailSeeds: [(Mm(10), Mm(16))],
            bareCoordinateSeeds: [(Mm(10), Mm(16))]);

        Assert.Null(reachesTheLayer.OwnReturnRefusal);
        Assert.NotEmpty(reachesTheLayer.Power);
        Assert.NotEmpty(reachesTheLayer.Reference);

        // And it IS on the reference layer — which is the thing the extractors report by name as a
        // diagnostic, and the whole reason this half of the test exists.
        Assert.Contains(reachesTheLayer.Power.SelectMany(i => i.Copper), c => c.Layer == Inner);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  6 — R-rail27-3a: assigning writes the part number and nothing else
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Assigning writes <c>PartNumber</c> on exactly the named rows, and every electrical column
    /// stays the library's.</b>
    /// </summary>
    /// <remarks>
    /// This is not "making the parts table editable". <c>RailPartRowViewModel</c> is read-only about
    /// the MODEL — capacitance, ESR, f₀ — which is entered once in the part library and used many
    /// times. A part NUMBER is the row's own field on the document and is what the library is keyed
    /// by: choosing it is choosing which library row applies.
    /// </remarks>
    [Fact]
    public void AssigningWritesThePartNumberOnExactlyTheSelectedRows_AndNoElectricalColumn()
    {
        var vm = Bare();
        var rail = new RailSpec { Name = "VDD", ReferenceLayer = Inner };
        rail.Parts.Add(new RailPart { Refdes = "C1" });
        rail.Parts.Add(new RailPart { Refdes = "C2" });
        rail.Parts.Add(new RailPart { Refdes = "C3" });
        vm.Document.Rails.Add(rail);
        vm.SelectedRailName = rail.Name;
        vm.PartLibrary = OneHundredNanofarads("PN-100N");

        Assert.Equal(2, vm.AssignPartNumber(["C1", "C3"], "PN-100N"));

        Assert.Equal("PN-100N", vm.Parts.Single(p => p.Refdes == "C1").PartNumber);
        Assert.Equal("PN-100N", vm.Parts.Single(p => p.Refdes == "C3").PartNumber);
        Assert.Equal(RailPartRowViewModel.UnresolvedText,
                     vm.Parts.Single(p => p.Refdes == "C2").PartNumber);

        // The electrical columns came from the LIBRARY, not from the assignment — the two rows that
        // now resolve read its capacitance and the third reads nothing.
        Assert.False(vm.Parts.Single(p => p.Refdes == "C1").IsUnresolved);
        Assert.True(vm.Parts.Single(p => p.Refdes == "C2").IsUnresolved);

        // Idempotent, and an empty answer clears it — the state a discovered row starts in.
        Assert.Equal(0, vm.AssignPartNumber(["C1", "C3"], "PN-100N"));
        Assert.Equal(2, vm.AssignPartNumber(["C1", "C3"], ""));
        Assert.All(vm.Parts, p => Assert.Equal(RailPartRowViewModel.UnresolvedText, p.PartNumber));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  7 — R-rail27-3b: the artwork fills the footprint column only where nothing else speaks
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The board's land pattern fills the footprint column, and the bill of materials still wins
    /// where it speaks.</b>
    /// </summary>
    [Fact]
    public void TheFootprintColumnFallsBackToTheArtwork_AndTheBomStillWinsWhereItSpeaks()
    {
        var part = new RailPart { Refdes = "C1" };

        var fromBoard = new RailPartRowViewModel(part, null, null, null, null, null, "C0402");
        Assert.Contains("0402", fromBoard.FootprintText, StringComparison.Ordinal);
        Assert.True(fromBoard.IsFootprintFromTheBoard);

        var bom = new BomRow("C1", "PN-100N", "100n", "0603", "X7R 100nF 0603");
        var fromBom = new RailPartRowViewModel(part, bom, null, null, null, null, "C0402");
        Assert.Contains("0603", fromBom.FootprintText, StringComparison.Ordinal);
        Assert.DoesNotContain("0402", fromBom.FootprintText, StringComparison.Ordinal);
        Assert.False(fromBom.IsFootprintFromTheBoard);

        // Nothing anywhere: the word this table already has for it.
        var silent = new RailPartRowViewModel(part, null, null, null, null);
        Assert.Equal(RailPartRowViewModel.UnresolvedText, silent.FootprintText);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  the fixtures
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static readonly LayerKey Top   = new(1, 0);
    private static readonly LayerKey Inner = new(2, 0);
    private static readonly LayerKey Bot   = new(3, 0);
    private static readonly LayerKey Via   = new(4, 0);

    private const double CopperSigma = 5.8e7;

    /// <summary>The part the end-to-end names: 100 nF, resonating where its mount puts it.</summary>
    private const double SelfResonanceHz = 20e6;

    private static Path64 Box(long x1, long y1, long x2, long y2) =>
        [new Point64(x1, y1), new Point64(x2, y1), new Point64(x2, y2), new Point64(x1, y2)];

    private static LayoutShape Rect(LayerKey layer, double x1, double y1, double x2, double y2) =>
        new RectShape { Layer = layer, X1 = Mm(x1), Y1 = Mm(y1), X2 = Mm(x2), Y2 = Mm(y2) };

    private static StackupLayer Conductor(string name, LayerKey? layer, bool reference = false) =>
        new()
        {
            Kind = StackupKind.Conductor, Name = name,
            ThicknessDbu = Um(35), SigmaSm = CopperSigma,
            DrawingLayers = layer is { } k ? [k] : [],
            IsGroundReference = reference,
        };

    private static StackupLayer Core(string name) =>
        new() { Kind = StackupKind.Dielectric, Name = name, ThicknessDbu = Mm(0.5), Epsr = 4.3, TanD = 0.02, Mur = 1 };

    /// <summary>Three conductors, top to bottom, each bound to its own drawing layer.</summary>
    private static Technology ThreeConductors()
    {
        var tech = new Technology { Name = "board" };
        tech.Layers.Add(new LayerDef { Key = Top,   Name = "Top Copper" });
        tech.Layers.Add(new LayerDef { Key = Inner, Name = "gnd" });
        tech.Layers.Add(new LayerDef { Key = Bot,   Name = "Bottom Copper" });
        tech.Stackup.Layers =
        [
            Conductor("TOP", Top),
            Core("core 1"),
            Conductor("GND", Inner, reference: true),
            Core("core 2"),
            Conductor("BOT", Bot),
        ];
        return tech;
    }

    /// <summary>The same, plus the via entry that makes <c>DrcConnectivity</c> join the layers.</summary>
    private static Technology StitchedTech()
    {
        var tech = ThreeConductors();
        tech.Layers.Add(new LayerDef { Key = Via, Name = "Via" });
        tech.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [Via],
            Fill = ViaFillKind.Plated, Plated = true, WallThicknessDbu = Um(25),
            SpanFromLayer = "TOP", SpanToLayer = "BOT",
        });
        return tech;
    }

    private static List<StackupLayer> Conductors(Technology tech) =>
        [.. tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor)];

    private static int NearestIndex(double[] hz, double target)
    {
        int best = 0;
        for (int i = 1; i < hz.Length; i++)
            if (Math.Abs(hz[i] - target) < Math.Abs(hz[best] - target)) best = i;
        return best;
    }

    private static PartLibrary OneHundredNanofarads(string partNumber)
    {
        var library = new PartLibrary { Name = "fixture" };
        library.Rows.Add(new PartLibraryRow
        {
            PartNumber          = partNumber,
            CapacitanceFarads       = 100e-9,
            SelfResonantFrequencyHz = SelfResonanceHz,
            EsrOhms             = 0.02,
            Footprint           = "0402",
        });
        return library;
    }

    private RailRfViewModel Bare() =>
        new(new RailDocument { Name = "board" }, null)
        {
            PostToUi = a => a(),
            RunOffThread = (work, _) => Task.FromResult(work()),
        };

    // ── the Gerber set ───────────────────────────────────────────────────────────────────────

    private const string MmHeader = "%FSLAX46Y46*%\n%MOMM*%\n";

    private static string Coord(double mm) => ((long)Math.Round(mm * 1_000_000)).ToString();

    /// <summary>A filled rectangle, optionally declaring what the file IS.</summary>
    private static string Pour(string? fileFunction, double x1, double y1, double x2, double y2)
    {
        string attribute = fileFunction is { Length: > 0 } fn ? $"%TF.FileFunction,{fn}*%\n" : "";
        return MmHeader + attribute + "G01*\nG36*\n" +
               $"X{Coord(x1)}Y{Coord(y1)}D02*\nX{Coord(x2)}Y{Coord(y1)}D01*\n" +
               $"X{Coord(x2)}Y{Coord(y2)}D01*\nX{Coord(x1)}Y{Coord(y2)}D01*\n" +
               $"X{Coord(x1)}Y{Coord(y1)}D01*\nG37*\nM02*\n";
    }

    private static string Flashes(string? fileFunction, params (double X, double Y)[] at)
    {
        string attribute = fileFunction is { Length: > 0 } fn ? $"%TF.FileFunction,{fn}*%\n" : "";
        return MmHeader + attribute + "%ADD10C,0.600*%\nD10*\n" +
               string.Concat(at.Select(p => $"X{Coord(p.X)}Y{Coord(p.Y)}D03*\n")) + "M02*\n";
    }

    private static string Drill(params (double X, double Y)[] hits) =>
        "M48\nMETRIC\nT1C0.300000\n%\nG90\nG05\nT1\n" +
        string.Concat(hits.Select(h => $"X{h.X:0.000000}Y{h.Y:0.000000}\n")) + "M30\n";

    /// <summary>
    /// The set. <b>The inner plane's file is named for the NET it carries</b>, which is what the
    /// reported board's was and is what matches no copper pattern in <c>GerberLayerIdentity</c>.
    /// </summary>
    private string WriteGerberSet()
    {
        string dir = Folder("set");

        // Top: the supply RAIL, and the capacitors' supply-side lands inside it. A long strip rather
        // than a square pour, because the fast model refuses copper the current FANS OUT in — 2
        // squares is below its own spreading floor — and §2.9's refusal is not what this gate is
        // about. 18 mm by 1 mm is 18 squares and reads as a trace.
        File.WriteAllText(Path.Combine(dir, "board.gtl"), Pour("Copper,L1,Top,Signal", 1, 4.5, 19, 5.5));

        // The inner plane. NO FileFunction, and a name nothing recognises — rung 4.
        File.WriteAllText(Path.Combine(dir, "board-gnd.gbr"), Pour(null, 0, 0, 20, 10));

        // Bottom: the ground pour the designer clicks by mistake, stitched to the plane above it.
        // CLEAR OF THE SUPPLY RAIL, because a seed is a coordinate with no layer on it: a ground pour
        // under the rail would make the pick itself ambiguous, which is a different question and is
        // `PdnRailRegions`' own (its seeding note records the same fact from the other side).
        File.WriteAllText(Path.Combine(dir, "board.gbl"), Pour("Copper,L3,Bot,Signal", 0, 7, 20, 10));

        // The stitching vias, at the capacitors' return lands — clear of the supply pour, so they
        // join the bottom pour to the plane and NOT to the rail.
        File.WriteAllText(Path.Combine(dir, "board.drl"), Drill((5, 9), (10, 9)));

        // Mask and legend: the stated-decision half of R-rail27-1a's message.
        File.WriteAllText(Path.Combine(dir, "board.gts"), Flashes("Soldermask,Top", (5, 5), (10, 5)));
        File.WriteAllText(Path.Combine(dir, "board.gto"), Flashes("Legend,Top", (5, 5)));

        return dir;
    }

    /// <summary>Answers the one row named <paramref name="layer"/> with "copper, above the top".</summary>
    private static IReadOnlyList<LayerMappingRow> AnswerCopperAfterTheTop(
        IReadOnlyList<LayerMappingRow> rows, string layer) =>
        [.. rows.Select(r =>
            r.StackupConductors is not null &&
            (r.SourceName ?? "").Contains(layer, StringComparison.OrdinalIgnoreCase)
                ? r with { Stackup = new LayerStackupChoice(true, 0) }
                : r)];

    private static IReadOnlyList<string> FilesIn(string dir) =>
        [.. Directory.EnumerateFiles(dir).OrderBy(p => p, StringComparer.Ordinal)];

    private string Folder(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── the railRF document over the imported board ──────────────────────────────────────────

    /// <summary>
    /// A <c>.crail</c> naming the imported cell, its netlist companion, and the window opened on it.
    /// </summary>
    /// <remarks>
    /// <b>The pads come from a board netlist beside the artwork</b>, which is how a fabrication
    /// output set carries its placed parts' terminals — the same route
    /// <c>RailArtwork.ResolveBoardNetlist</c> takes for any imported board, and the one thing a
    /// hand-authored Gerber fixture cannot state in the artwork alone.
    /// </remarks>
    private RailRfViewModel OpenInRailRf(GerberImport.ImportResult imported)
    {
        string clay = Path.Combine(
            CellFolder.SubFolderPath(imported.CellDir!, ViewType.Layout),
            Directory.GetFiles(CellFolder.SubFolderPath(imported.CellDir!, ViewType.Layout), "*.clay")
                     .Select(Path.GetFileName).First()!);

        string ipc = Path.Combine(imported.ImportDir!, "board.ipc");
        File.WriteAllText(ipc, Netlist(
            Record("VDD", (5_000, 5_000), "C1", "1"),
            Record("GND", (5_000, 9_000), "C1", "2"),
            Record("VDD", (10_000, 5_000), "C2", "1"),
            Record("GND", (10_000, 9_000), "C2", "2")));

        string crail = Path.Combine(imported.ImportDir!, "board.crail");
        var doc = new RailDocument { Name = "board" };
        RailDocumentIo.SaveToFile(crail, doc);

        doc.ArtworkCellRef   = clay;
        doc.BoardNetlistRef  = ipc;
        RailArtwork.RebaseReferences(doc, null, crail);
        RailDocumentIo.SaveToFile(crail, doc);

        var vm = new RailRfViewModel(RailDocumentIo.LoadFromFile(crail), crail)
        {
            PostToUi = a => a(),
            RunOffThread = (work, _) => Task.FromResult(work()),
        };

        foreach (string note in vm.LoadDocumentReferences()) output.WriteLine(note);
        Assert.NotNull(vm.Board);
        return vm;
    }

    /// <summary>One IPC-D-356 feature record, in the format's own columns.</summary>
    private static string Record(string net, (int X, int Y) microns, string component, string pin)
    {
        string reference = $"{component}-{pin}";
        return $"327{net.PadRight(14)}  {reference.PadRight(11)}" +
               $"X{Sign(microns.X)}Y{Sign(microns.Y)}\n";

        static string Sign(int v) => (v < 0 ? "-" : "+") + Math.Abs(v).ToString("000000");
    }

    /// <summary>Units code 1 — 0.001 mm, so a record's counts are microns.</summary>
    private static string Netlist(params string[] records) =>
        "C  hand-authored fixture\n" +
        "P  JOB       r27\n" +
        "P  UNITS CUST 1\n" +
        string.Concat(records) +
        "999\n";
}

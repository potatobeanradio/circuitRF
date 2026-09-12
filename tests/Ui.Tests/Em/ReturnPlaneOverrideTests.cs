// ================================================================
//  ReturnPlaneOverrideTests.cs — RP-1: an EXPLICIT return plane for a run.
//
//  R-em-4 resolves the return plane as the top surface of the highest ground-designated conductor
//  below the lowest analysis level. That is the correct rule, and it is the only vocabulary the
//  design had for the answer — which is wrong for two ordinary situations: a board with two
//  designated planes below the structure whose trace is genuinely referenced to the LOWER one, and
//  the routine "what if" of running one structure against two references. The only other way to say
//  either was to un-tick a plane's "Ground reference" in the TECHNOLOGY, which every other design
//  using it shares, and which also turns that plane into meshed signal metal everywhere.
//
//  `EmSetup.GroundStackupLayerName` says it per run instead. **Empty means R-em-4**, and the first
//  test here is the one that matters most: an existing multi-level fixture must produce the same
//  medium down to the level z's whether the field is absent or empty.
//
//  The refusals are the rest of the substance. An override that silently produced a
//  different-LOOKING answer would be the exact failure this area is written against, so a named
//  conductor that does not exist, that is also an analysis level, or that does not lie below the
//  lowest level is refused BY NAME with the remedy — never quietly ignored, and never quietly
//  honoured. A conductor the technology does not designate as ground is ACCEPTED, because that is
//  the point of the field, and said so in a note, because otherwise the `.ctech` and the run
//  disagree with nothing on screen to say which won.
//
//  The 2026-09-10 inner-layer-pad report is deliberately NOT what this field fixes: that was a
//  level-selection defect, fixed in the incidental trim (PortSeededLevelsTests). An override that
//  papered over a bad level set would leave the levels just as wrong.
// ================================================================

using System.Diagnostics;
using CircuitRF.Design.Layout;
using CircuitRF.Core.Design;
using CircuitRF.Engine.Mom;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests.Em;

public sealed class ReturnPlaneOverrideTests : IDisposable
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-rp1-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private static Technology Tech() => ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");

    /// <summary>Top Copper, Inner 1 (Ground Plane), Inner 2, Bottom Copper — in that order.</summary>
    private static List<StackupLayer> Conductors(Technology t) =>
        [.. t.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor)];

    private static long Um(double v) => (long)Math.Round(v * Dbu);

    private static RectShape Patch(LayerKey layer) =>
        new() { Layer = layer, X1 = 0, Y1 = 0, X2 = Um(30000), Y2 = Um(30000) };

    private static LabelShape Port(LayerKey layer, double x, double y) =>
        new() { Layer = layer, X = Um(x), Y = Um(y), Text = "P1", Height = Um(600), IsPort = true };

    private static EmExtractionSettings Ground(string? name, params string[] levels) =>
        new(AnalysisLevelNames: levels.Length > 0 ? levels : null, GroundStackupLayerName: name);

    private static string? ReturnNote(PlanarExtractionResult r) =>
        r.Notes.FirstOrDefault(n => n.StartsWith("Every port returns through", StringComparison.Ordinal));

    /// <summary>Metres per DBU for a STACKUP thickness — always the default resolution, never the
    /// layout's own (see PlanarExtractor's header; conflating the two rescales every height).</summary>
    private static double M(double thicknessDbu) => thicknessDbu / (LayoutUnits.DefaultDbuPerMicron * 1e6);

    /// <summary>
    /// Everything about the extracted medium a caller could observe, as one string. Used for gate 1
    /// because <c>PlanarProblem</c> is a record over ARRAYS — its own <c>Equals</c> is reference
    /// equality on the level list and would pass on two entirely different problems.
    /// </summary>
    private static string Signature(PlanarProblem p)
    {
        var s = p.EffectiveStack;
        return string.Join(" | ",
            [
                $"slab={p.Slab.HeightM:R}/{p.Slab.Material.EpsR:R}/{p.Slab.Material.TanD:R}",
                $"levels={p.Layers.Count}",
                .. Enumerable.Range(0, p.Layers.Count).Select(i => $"z{i}={p.LevelZ(i):R}"),
                $"medium={s}",
                $"regions={s.RegionCount}",
                .. Enumerable.Range(0, s.RegionCount).Select(i => $"m{i}={s.MaterialOfRegion(i).EpsR:R}"),
                .. Enumerable.Range(0, s.InterfaceZ.Count).Select(i => $"i{i}={s.InterfaceZ[i]:R}"),
                $"vias={p.ViaList.Count}",
            ]);
    }

    // ── Gate 1 — the field's ABSENCE must change nothing at all ────────────────────────────────

    /// <summary>
    /// <b>The gate that matters most.</b> A two-level fixture, extracted three ways: with no
    /// settings at all, with the settings record present but the return plane null, and with it set
    /// to the empty string. All three are R-em-4's inferred answer and must produce the same medium
    /// — the same slab, the same level z's, the same interfaces. R-rp1-1: empty means R-em-4, not
    /// "no ground".
    /// </summary>
    [Fact]
    public void AnEmptyReturnPlane_IsBitIdenticalToTheInferredRule()
    {
        var tech   = Tech();
        var conds  = Conductors(tech);
        var top    = conds[0].DrawingLayers[0];
        var inner2 = conds[2].DrawingLayers[0];

        LayoutShape[] art =
            [Patch(top), Patch(inner2), Port(top, 0, 15000), Port(inner2, 30000, 15000)];

        var bare    = PlanarExtractor.Extract(art, tech, Dbu, 10e9);
        var nulled  = PlanarExtractor.Extract(art, tech, Dbu, 10e9, Ground(null));
        var emptied = PlanarExtractor.Extract(art, tech, Dbu, 10e9, Ground(""));

        Assert.True(bare.Ok, bare.Refusal);
        Assert.True(nulled.Ok, nulled.Refusal);
        Assert.True(emptied.Ok, emptied.Refusal);

        // Two levels, so this fixture really does exercise the general medium rather than L8's
        // one-slab path — a single-level fixture would compare two trivially identical problems.
        Assert.Equal(2, bare.Problem!.Layers.Count);
        Assert.NotNull(bare.Problem.MediumStack);

        Assert.Equal(Signature(bare.Problem), Signature(nulled.Problem!));
        Assert.Equal(Signature(bare.Problem), Signature(emptied.Problem!));

        // …and the run says the same thing about it, in the un-overridden spelling.
        Assert.Equal(ReturnNote(bare), ReturnNote(emptied));
        Assert.DoesNotContain("THIS EM SETUP", ReturnNote(emptied)!, StringComparison.Ordinal);

        // Inner 2 is the LOWEST level here, and R-em-4 asks its question of the lowest — so the
        // inferred plane is Bottom Copper, not the Inner 1 that sits above Inner 2.
        Assert.False(emptied.ReturnPlane!.Overridden);
        Assert.Equal(conds[3].Name, emptied.ReturnPlane.ConductorName);
    }

    /// <summary>The same rule one level up: a <c>.cem</c> that leaves the field empty hands the
    /// extractor a null, so nothing downstream can tell the difference between "absent" and
    /// "empty" either.</summary>
    [Fact]
    public void AnEmptyFieldOnTheDocument_ReachesTheExtractorAsNull()
    {
        Assert.Null(new EmSetup().ToExtractionSettings().GroundStackupLayerName);
        Assert.Equal("Inner 2",
            new EmSetup { GroundStackupLayerName = "Inner 2" }
                .ToExtractionSettings().GroundStackupLayerName);
    }

    // ── Gate 2 — the override actually changes the medium, and by the right amount ─────────────

    /// <summary>
    /// The shipped 4-layer starter: a trace on Top Copper is referenced to Inner 1 by R-em-4 — 8 mil
    /// of prepreg — and to Bottom Copper by the override, which is the whole 62 mil board. The slab
    /// is asserted as a NUMBER computed from the stackup, because a note assertion alone passes with
    /// the medium built wrong, and that is exactly the failure mode here: both answers look ordinary.
    /// </summary>
    [Fact]
    public void TheOverrideMovesTheReturnPlane_AndTheSlabIsTheWholeBoard()
    {
        var tech  = Tech();
        var conds = Conductors(tech);
        var top   = conds[0].DrawingLayers[0];

        LayoutShape[] art = [Patch(top), Port(top, 0, 15000)];

        var inferred = PlanarExtractor.Extract(art, tech, Dbu, 10e9);
        Assert.True(inferred.Ok, inferred.Refusal);

        var over = PlanarExtractor.Extract(art, tech, Dbu, 10e9, Ground(conds[3].Name));
        Assert.True(over.Ok, over.Refusal);

        // The stackup, bottom-up: Bottom Copper | Prepreg | Inner 2 | Core | Inner 1 | Prepreg | Top.
        var dielectrics = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Dielectric).ToList();
        double prepregTop = M(dielectrics[0].ThicknessDbu);       // the one Inner 1 gives the trace
        double topSheet   = M(conds[3].ThicknessDbu) + M(dielectrics[2].ThicknessDbu)
                          + M(conds[2].ThicknessDbu) + M(dielectrics[1].ThicknessDbu)
                          + M(conds[1].ThicknessDbu) + M(dielectrics[0].ThicknessDbu);
        double wholeBoard = topSheet - M(conds[3].ThicknessDbu);  // down to Bottom Copper's TOP face

        Assert.Equal(prepregTop, inferred.Problem!.Slab.HeightM, 12);
        Assert.Equal(wholeBoard, over.Problem!.Slab.HeightM, 12);
        Assert.True(wholeBoard > 7 * prepregTop, "the override must not be a rounding difference");

        // The medium spans the whole distance, and it is FR-4 all the way down — the two inner
        // coppers are absorbed into the surrounding dielectric, which is exactly what the R-rp1-6
        // warning says happens to them.
        Assert.Equal(dielectrics[0].Epsr, over.Problem.Slab.Material.EpsR, 12);
        Assert.Equal(wholeBoard, over.Problem.EffectiveStack.TopZ, 12);

        // On THIS starter all three dielectrics are the same FR-4, so the medium legitimately merges
        // to one region and stays on L8's shipped one-slab path. Give the core a different εr and the
        // intervening layers become real interfaces the general kernel has to carry — which is the
        // half of "the LayerStack carries the intervening layers" a homogeneous board cannot show.
        var mixed = Tech();
        mixed.Stackup.Layers.First(l => l.Kind == StackupKind.Dielectric && l.Name == "Core").Epsr = 3.0;
        var stratified = PlanarExtractor.Extract(art, mixed, Dbu, 10e9, Ground(conds[3].Name));
        Assert.True(stratified.Ok, stratified.Refusal);
        Assert.NotNull(stratified.Problem!.MediumStack);
        Assert.True(stratified.Problem.MediumStack!.LayerCount > 1,
            $"expected a stratified medium, got {stratified.Problem.MediumStack}");
        Assert.Equal(wholeBoard, stratified.Problem.MediumStack.TopZ, 12);

        Assert.Equal(conds[3].Name, over.ReturnPlane!.ConductorName);
        Assert.True(over.ReturnPlane.Overridden);
        Assert.Equal(M(conds[3].ThicknessDbu), over.ReturnPlane.TopM, 12);
    }

    /// <summary>R-rp1-5: the note that is the ONLY place the return plane is visible must read
    /// differently when the document chose — and must keep naming the height, which is the number
    /// the whole 2%-scale trap lives in. It also names what R-em-4 would have chosen, because the
    /// interesting thing about an override is the difference it made.</summary>
    [Fact]
    public void TheReturnNote_SaysWhenTheAnswerWasOverridden()
    {
        var tech  = Tech();
        var conds = Conductors(tech);
        var top   = conds[0].DrawingLayers[0];

        var over = PlanarExtractor.Extract(
            [Patch(top), Port(top, 0, 15000)], tech, Dbu, 10e9, Ground(conds[3].Name));

        string note = ReturnNote(over)!;
        Assert.Contains($"'{conds[3].Name}'", note, StringComparison.Ordinal);
        Assert.Contains("THIS EM SETUP names it as the return plane", note, StringComparison.Ordinal);
        Assert.Contains($"'{conds[1].Name}'", note, StringComparison.Ordinal);   // what R-em-4 wanted
        Assert.Contains("µm", note, StringComparison.Ordinal);
        Assert.Contains("laterally infinite", note, StringComparison.Ordinal);
    }

    // ── Gate 3 — the three refusals, each naming the conductor and the remedy ──────────────────

    /// <summary>R-rp1-2. A name the technology does not have is a typo or a technology that has
    /// moved on; falling back to R-em-4 would answer a question nobody asked.</summary>
    [Fact]
    public void ANamedConductorThatDoesNotExist_IsRefusedByName()
    {
        var tech  = Tech();
        var conds = Conductors(tech);

        var r = PlanarExtractor.Extract(
            [Patch(conds[0].DrawingLayers[0]), Port(conds[0].DrawingLayers[0], 0, 15000)],
            tech, Dbu, 10e9, Ground("Middle Copper"));

        Assert.False(r.Ok);
        Assert.Contains("'Middle Copper'", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains($"'{tech.Name}'", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("return plane", r.Refusal!, StringComparison.Ordinal);
        // The remedy lists what it COULD have been — including the entries the technology does not
        // designate, since R-rp1-4 permits those and a list that hid them teaches the wrong rule.
        Assert.Contains($"'{conds[2].Name}'", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains($"'{conds[1].Name}'", r.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>R-rp1-4's refusal half. A conductor cannot be both the meshed metal and the
    /// laterally infinite plane that metal returns to — there is no reading of "both" the kernel
    /// could act on, so this is a refusal rather than a note.</summary>
    [Fact]
    public void AConductorThatIsAlsoAnAnalysisLevel_IsRefused()
    {
        var tech   = Tech();
        var conds  = Conductors(tech);
        var top    = conds[0].DrawingLayers[0];
        var inner2 = conds[2].DrawingLayers[0];

        var r = PlanarExtractor.Extract(
            [Patch(top), Patch(inner2), Port(top, 0, 15000)], tech, Dbu, 10e9,
            Ground(conds[2].Name, conds[0].Name, conds[2].Name));

        Assert.False(r.Ok);
        Assert.Contains($"'{conds[2].Name}'", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("analysis level", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("untick it in this setup's analysis levels", r.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>
    /// R-rp1-3 — R-em-4's own physics, not a limitation of the override. The message says which
    /// level and BOTH heights, because the user is looking at a stackup table and cannot see the
    /// analysis levels from there.
    ///
    /// <para><b>RP-3 narrowed what reaches this refusal, and the fixture moved with it.</b> A plane
    /// above EVERY level is no longer refused — it is the case the flipped-stack path now solves,
    /// and an override refusing what the automatic rule does silently would make the explicit
    /// spelling strictly weaker than the inferred one. What survives is the case no orientation of
    /// the stack can express: a plane BETWEEN the levels, which is two decoupled structures rather
    /// than one problem. Named here as Inner 1, with metal above it and below it.</para>
    /// </summary>
    [Fact]
    public void AConductorBetweenTheLevels_IsRefusedWithBothHeights()
    {
        var tech   = Tech();
        var conds  = Conductors(tech);
        var top    = conds[0].DrawingLayers[0];
        var inner2 = conds[2].DrawingLayers[0];

        var r = PlanarExtractor.Extract(
            [Patch(top), Patch(inner2), Port(inner2, 0, 15000)], tech, Dbu, 10e9, Ground(conds[1].Name));

        Assert.False(r.Ok);
        Assert.Contains($"'{conds[1].Name}'", r.Refusal!, StringComparison.Ordinal);   // Inner 1
        Assert.Contains($"'{conds[2].Name}'", r.Refusal!, StringComparison.Ordinal);   // Inner 2
        Assert.Contains("BENEATH the conductor it feeds", r.Refusal!, StringComparison.Ordinal);

        // Both heights, in µm, as numbers the stackup table can be read against.
        double innerSheet = M(conds[3].ThicknessDbu)
                          + M(tech.Stackup.Layers.First(l => l.Kind == StackupKind.Dielectric
                                                          && l.Name.Contains("bottom", StringComparison.OrdinalIgnoreCase)).ThicknessDbu);
        Assert.Contains($"{innerSheet * 1e6:G4} µm", r.Refusal!, StringComparison.Ordinal);
    }

    // ── Gate 4 — a conductor the technology does not designate is ACCEPTED, and said so ────────

    /// <summary>R-rp1-4's note half, and the reason the field exists at all: the alternative is
    /// un-ticking "Ground reference" on a technology every other design shares. The medium must
    /// actually terminate on it — asserted as the slab NUMBER, not just the name.</summary>
    [Fact]
    public void ANonDesignatedConductor_IsAcceptedWithANoteAndActuallyTerminatesTheMedium()
    {
        var tech  = Tech();
        var conds = Conductors(tech);
        var top   = conds[0].DrawingLayers[0];

        Assert.False(conds[2].IsGroundReference, "Inner 2 is the starter's non-designated inner layer");

        var r = PlanarExtractor.Extract(
            [Patch(top), Port(top, 0, 15000)], tech, Dbu, 10e9, Ground(conds[2].Name));

        Assert.True(r.Ok, r.Refusal);

        var note = r.Notes.FirstOrDefault(
            n => n.Contains("NOT marked as a ground reference", StringComparison.Ordinal));
        Assert.NotNull(note);
        Assert.Contains($"'{conds[2].Name}'", note, StringComparison.Ordinal);
        Assert.Contains($"'{tech.Name}'", note, StringComparison.Ordinal);
        Assert.Contains("this run only", note, StringComparison.Ordinal);

        var dielectrics = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Dielectric).ToList();
        double expected = M(dielectrics[0].ThicknessDbu) + M(conds[1].ThicknessDbu)
                        + M(dielectrics[1].ThicknessDbu);
        Assert.Equal(expected, r.Problem!.Slab.HeightM, 12);
        Assert.Equal(conds[2].Name, r.ReturnPlane!.ConductorName);
        Assert.True(r.ReturnPlane.Overridden);
    }

    // ── Gate 5 — the skipped-plane warning is NOT suppressed ───────────────────────────────────

    /// <summary>R-rp1-6. A designated plane between the levels and the chosen return is absorbed
    /// into the surrounding dielectric — its metal is modelled as substrate — and that is as true
    /// when the user chose the return as when R-em-4 did. If anything it matters more here, because
    /// the override is how someone reaches that state on purpose.</summary>
    [Fact]
    public void ASkippedDesignatedPlane_StillWarns_WhenTheReturnWasOverridden()
    {
        var tech  = Tech();
        var conds = Conductors(tech);
        var top   = conds[0].DrawingLayers[0];

        var r = PlanarExtractor.Extract(
            [Patch(top), Port(top, 0, 15000)], tech, Dbu, 10e9, Ground(conds[3].Name));

        Assert.True(r.Ok, r.Refusal);
        var warning = r.Notes.FirstOrDefault(n => n.StartsWith("WARNING:", StringComparison.Ordinal));
        Assert.NotNull(warning);
        Assert.Contains($"'{conds[1].Name}'", warning, StringComparison.Ordinal);   // Inner 1
        Assert.Contains("modelled as substrate", warning, StringComparison.Ordinal);
    }

    // ── Gate 7 — round trip ────────────────────────────────────────────────────────────────────

    /// <summary>A <c>.cem</c> with the field set survives a write and a read unchanged; one without
    /// it gains no key, so every document written before RP-1 re-serialises byte-identically and no
    /// <c>FormatVersion</c> bump was needed.</summary>
    [Fact]
    public void TheFieldRoundTrips_AndAnUnsetOneAddsNoKey()
    {
        var with = new EmSetup
        {
            Name = "rp", LayoutRef = "L/layout/L.clay", GroundStackupLayerName = "Inner 2",
        };
        string json = EmSetupPersistence.Serialize(with);
        Assert.Contains("\"GroundStackupLayerName\": \"Inner 2\"", json, StringComparison.Ordinal);
        Assert.Equal("Inner 2", EmSetupPersistence.Deserialize(json).GroundStackupLayerName);

        var without = new EmSetup { Name = "rp", LayoutRef = "L/layout/L.clay" };
        string bare = EmSetupPersistence.Serialize(without);
        Assert.DoesNotContain("GroundStackupLayerName", bare, StringComparison.Ordinal);
        Assert.Equal("", EmSetupPersistence.Deserialize(bare).GroundStackupLayerName);

        // Clone is the panel's own undo snapshot — a field it forgets is a field an undo silently
        // reverts to whatever the clone left behind.
        Assert.Equal("Inner 2", with.Clone().GroundStackupLayerName);
    }

    // ── Gate 6 — `explain` agrees with the run ─────────────────────────────────────────────────

    /// <summary>
    /// R-rp1-8. The plane <c>circuitrf explain</c> reports is asserted against the EXTRACTION, never
    /// against a transcription of R-em-4: a rule restated in the CLI is a rule that can disagree
    /// with the run, which is precisely what a caller asks this verb to rule out. Both spellings are
    /// checked — the same workspace read twice, once with the field set and once without.
    /// </summary>
    [Fact]
    public void ExplainReportsThePlaneTheExtractionUsed_BothInferredAndOverridden()
    {
        var tech  = Tech();
        var conds = Conductors(tech);
        string cemPath = BuildWorkspace(tech, conds[0].DrawingLayers[0]);

        foreach (string? chosen in new[] { null, conds[3].Name })
        {
            var setup = EmSetupPersistence.LoadFromFile(cemPath);
            setup.GroundStackupLayerName = chosen ?? "";
            EmSetupPersistence.SaveToFile(cemPath, setup);

            // What the RUN would resolve, in process, through the same resolver `explain` uses.
            var resolution = EmSetupResolver.Resolve(
                cemPath, setup.LayoutRef, Path.Combine(_root, ".cws"), new TechnologyCache());
            Assert.NotNull(resolution.Source?.Technology);

            var geometry = EmGeometry.Flatten(resolution.Source!.View, resolution.Source.AbsolutePath);
            var planar = PlanarExtractor.Extract(
                geometry.Shapes, resolution.Source.Technology!, resolution.Source.DbuPerMicron, 0,
                setup.ToExtractionSettings(setup.LayoutRef), geometry.GeneratorIds);
            Assert.True(planar.Ok, planar.Refusal);

            var rp = planar.ReturnPlane!;
            Assert.Equal(chosen is not null, rp.Overridden);

            var (exit, stdout, stderr) = RunCli("explain", cemPath);
            Assert.Equal(0, exit);
            Assert.Contains("return plane", stdout, StringComparison.Ordinal);
            Assert.Contains($"{rp.ConductorName} at {rp.TopM * 1e6:G4} µm", stdout, StringComparison.Ordinal);
            Assert.Contains(
                rp.Overridden ? "named by this EM setup" : "R-em-4",
                stdout, StringComparison.Ordinal);
            Assert.True(stderr.Length == 0 || !stderr.Contains("error", StringComparison.OrdinalIgnoreCase),
                stderr);
        }
    }

    // ── R-rp1-7 — the panel's own combobox ─────────────────────────────────────────────────────

    /// <summary>
    /// Every conductor stackup entry is listed, ground-designated or not, behind an "(automatic)"
    /// first row that writes the empty string. Nothing is FILTERED — R-rp1-4 permits a
    /// non-designated conductor, and a list that hid the legal choices would teach the wrong rule —
    /// so the designated ones are marked in the row text instead. Selecting one commits undoably,
    /// like every other control on this panel.
    /// </summary>
    [Fact]
    public void ThePanelListsEveryConductor_MarksTheDesignatedOnes_AndCommitsUndoably()
    {
        var tech = Tech();
        var vm = new CircuitRF.Ui.Layout.Em.EmSetupEditorViewModel(
            Path.Combine(Path.GetTempPath(), "unused-rp1.cem"),
            new EmSetup { Name = "x", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar })
        {
            ResolveLayout = _ => new EmLayoutSource(
                "a.clay", new LayoutView { DbuPerMicron = Dbu }, tech, Dbu),
        };
        vm.Refresh();

        var conds = Conductors(tech);
        Assert.Equal(
            CircuitRF.Ui.Layout.Em.EmSetupEditorViewModel.AutomaticReturnPlane,
            vm.ReturnPlaneChoices[0].Display);
        Assert.Equal("", vm.ReturnPlaneChoices[0].Name);
        Assert.Equal(
            conds.Select(c => c.Name),
            vm.ReturnPlaneChoices.Skip(1).Select(c => c.Name));

        // The two designated planes are marked; the two signal conductors are not.
        Assert.Contains("ground reference",
            vm.ReturnPlaneChoices.First(c => c.Name == conds[1].Name).Display, StringComparison.Ordinal);
        Assert.DoesNotContain("ground reference",
            vm.ReturnPlaneChoices.First(c => c.Name == conds[2].Name).Display, StringComparison.Ordinal);

        Assert.Same(vm.ReturnPlaneChoices[0], vm.ReturnPlaneChoice);   // "(automatic)" by default

        vm.ReturnPlaneChoice = vm.ReturnPlaneChoices.First(c => c.Name == conds[3].Name);
        Assert.Equal(conds[3].Name, vm.Working.GroundStackupLayerName);
        Assert.True(vm.IsDirty);

        vm.UndoCommand.Execute(null);
        Assert.Equal("", vm.Working.GroundStackupLayerName);

        vm.RedoCommand.Execute(null);
        Assert.Equal(conds[3].Name, vm.Working.GroundStackupLayerName);
    }

    /// <summary>A name the technology no longer has is NOT quietly cleared back to "(automatic)":
    /// the run refuses it by name (R-rp1-2), and a panel that dropped the setting would hide the
    /// very disagreement the refusal exists to report.</summary>
    [Fact]
    public void ANameTheTechnologyLacks_StaysSelectedAndIsMarkedAsMissing()
    {
        var tech = Tech();
        var vm = new CircuitRF.Ui.Layout.Em.EmSetupEditorViewModel(
            Path.Combine(Path.GetTempPath(), "unused-rp1b.cem"),
            new EmSetup
            {
                Name = "x", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
                GroundStackupLayerName = "Middle Copper",
            })
        {
            ResolveLayout = _ => new EmLayoutSource(
                "a.clay", new LayoutView { DbuPerMicron = Dbu }, tech, Dbu),
        };
        vm.Refresh();

        Assert.Equal("Middle Copper", vm.ReturnPlaneChoice!.Name);
        Assert.Contains("not in this technology", vm.ReturnPlaneChoice.Display, StringComparison.Ordinal);
        Assert.Equal("Middle Copper", vm.Working.GroundStackupLayerName);
        Assert.False(vm.IsDirty);      // projecting the document is not an edit
    }

    /// <summary>A workspace the way the GUI lays one out — the `.cem` names its layout
    /// WORKSPACE-relative, so the walk-up R-emcli-5 specifies is actually exercised.</summary>
    private string BuildWorkspace(Technology tech, LayerKey signalLayer)
    {
        string cellLayoutDir = Path.Combine(_root, "Patch", "layout");
        Directory.CreateDirectory(cellLayoutDir);

        TechPersistence.SaveToFile(Path.Combine(_root, "pcb.ctech"), tech);

        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(Patch(signalLayer));
        view.Shapes.Add(Port(signalLayer, 0, 15000));
        LayoutPersistence.SaveToFile(Path.Combine(cellLayoutDir, "Patch.clay"), view);

        WorkspacePersistence.SaveToFile(
            Path.Combine(_root, ".cws"), new CwsFile { DefaultTechRef = "pcb.ctech" });

        string cemPath = Path.Combine(_root, "patch.cem");
        EmSetupPersistence.SaveToFile(cemPath, new EmSetup
        {
            Name      = "patch",
            LayoutRef = Path.Combine("Patch", "layout", "Patch.clay"),
            Frequency = new FrequencySpec("1", "10", 3, SweepKind.Linear, "GHz", "GHz"),
        });
        return cemPath;
    }

    /// <summary>Launches the ALREADY-BUILT CLI, never `dotnet run --project src/Cli` — a nested
    /// MSBuild inside a `dotnet test` that already holds this repository's build locks does not
    /// finish. EmCliVerbTests' own header records that in full; this is the same pattern.</summary>
    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(
                typeof(ReturnPlaneOverrideTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string dll = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(dll), $"the CLI was not built beside these tests: {dll}");

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(dll);
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}

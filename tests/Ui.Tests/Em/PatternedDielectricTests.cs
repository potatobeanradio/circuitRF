// MIM-7 — a dielectric that is PATTERNED with a conductor: it enters a run's medium only when that
// conductor is in the run.
//
// WHY THE FIELD EXISTS. From MIM-2 to MIM-7 circuitRF shipped two MMIC technologies that differed
// only by a capacitor module, and the split was not filing. A capacitor dielectric between the two
// interconnect metals is a real layer of the laterally-infinite medium, so it was present in every
// run — including runs with no capacitor in them — and that cost two measured things:
//   1. every Metal1-Metal2 airbridge post crossed a dielectric interface, which PlanarKernel refuses
//      by name and for the WHOLE RUN, not as a dropped shape;
//   2. a line on the metal below it moved, because the film sat on that metal as superstrate.
// Neither may land silently on the technology every existing MMIC workspace copied.
//
// THE OBSERVATION. Physically the film is patterned: it exists under its plate and nowhere else. The
// 2.5D premise forces "laterally infinite per RUN"; it does not force "present in every run". So the
// tie names the plate, and the honest per-run proxy for "this run has capacitors in it" is one the
// extractors already compute — is the plate conductor in the run?
//
// WHAT THIS FILE HOLDS SHUT: the MECHANISM, on a probe technology built here so the assertions are
// about the rule rather than about the shipped MMIC numbers (those live in MimCapacitorTests, next
// to the capacitor fixtures they are about) — the two extractors that read the tie, the deactivation
// note that must never go silent, the broken tie that must NOT deactivate, and the schema half:
// validation, .ctech round trip, merge, editor row.

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class PatternedDielectricTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static readonly LayerKey Lower = new(1, 0);
    private static readonly LayerKey Plate = new(2, 0);

    private static long Um(double v) => (long)Math.Round(v * Dbu);

    private static RectShape Rect(LayerKey layer, double x0, double y0, double x1, double y1) =>
        new() { Layer = layer, X1 = Um(x0), Y1 = Um(y0), X2 = Um(x1), Y2 = Um(y1) };

    private static LabelShape Port(LayerKey layer, double x, double y, string name) =>
        new() { Layer = layer, X = Um(x), Y = Um(y), Text = name, Height = Um(4), IsPort = true };

    /// <summary>
    /// Ground / 100 µm εᵣ 10 / 3 µm "Lower" (sheet on its TOP, as a bottom plate wants) / 1 µm
    /// εᵣ 7 film tied to "Plate" / 2 µm "Plate" / 10 µm air. Deliberately generic and deliberately
    /// NOT the shipped MMIC numbers: every quantity below is identifiable by material, and a change
    /// to what circuitRF ships must not be able to make this file pass for the wrong reason.
    /// </summary>
    private static Technology ProbeTech(bool tied = true) => new()
    {
        Name = "patterned-film probe",
        DefaultDisplayUnit = LayoutUnit.Um,
        DefaultSnapDbu = Um(1),
        DefaultFlattenTolDbu = Um(1),
        Layers =
        [
            new LayerDef { Key = Lower, Name = "Lower", ZOrder = 1, Purpose = "drawing" },
            new LayerDef { Key = Plate, Name = "Plate", ZOrder = 2, Purpose = "drawing" },
        ],
        Stackup = new Stackup
        {
            Top = BoundaryCondition.Open,
            Bottom = BoundaryCondition.Ground,
            Layers =
            [
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Air", ThicknessDbu = Um(10), Epsr = 1.0 },
                new StackupLayer { Kind = StackupKind.Conductor,  Name = "Plate", ThicknessDbu = Um(2),
                                   SigmaSm = 4.1e7, DrawingLayers = [Plate] },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Film", ThicknessDbu = Um(1),
                                   Epsr = 7.0, TanD = 0.004,
                                   PresentWithLayer = tied ? "Plate" : null },
                new StackupLayer { Kind = StackupKind.Conductor,  Name = "Lower", ThicknessDbu = Um(3),
                                   SigmaSm = 4.1e7, DrawingLayers = [Lower],
                                   SheetAt = ConductorSheetSurface.Top },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Substrate", ThicknessDbu = Um(100), Epsr = 10.0, TanD = 0.002 },
                new StackupLayer { Kind = StackupKind.Conductor,  Name = "Ground", ThicknessDbu = Um(3),
                                   SigmaSm = 4.1e7, IsGroundReference = true },
            ],
        },
    };

    /// <summary>A line on the lower metal only — no plate artwork, so the plate is not an analysis
    /// level and the tie deactivates.</summary>
    private static List<LayoutShape> LowerLineOnly() =>
    [
        Rect(Lower, 0, 0, 400, 60),
        Port(Lower, 0, 30, "P1"),
        Port(Lower, 400, 30, "P2"),
    ];

    /// <summary>The same line with a plate over it — the plate carries artwork, so it IS an analysis
    /// level and the film is real.</summary>
    private static List<LayoutShape> WithAPlate() =>
    [
        Rect(Lower, 0, 0, 400, 60),
        Rect(Plate, 100, 10, 200, 50),
        Port(Lower, 0, 30, "P1"),
        Port(Lower, 400, 30, "P2"),
    ];

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The rule, in the planar extractor
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The whole claim, as an identity rather than a tolerance: with the plate absent, the tied
    /// technology extracts EXACTLY as the same technology with no film in it at all.</b>
    ///
    /// <para>The comparison technology is the same object with the film's own material set to air
    /// and the sheet-surface choice cleared — i.e. what the stack would say if the module had never
    /// been added, at the same band positions. Every number the solver reads is compared, because
    /// "nothing changed" is the claim and a spot check of one z would not be it.</para>
    /// </summary>
    [Fact]
    public void WithNoPlateArtwork_TheTiedFilmExtractsAsIfItWereNotThere()
    {
        var tied = ProbeTech();

        var moduleFree = ProbeTech(tied: false);
        var film = moduleFree.Stackup.Layers.Single(l => l.Name == "Film");
        film.Epsr = 1.0; film.TanD = 0;
        moduleFree.Stackup.Layers.Single(l => l.Name == "Lower").SheetAt = null;

        var a = PlanarExtractor.Extract(LowerLineOnly(), tied,       Dbu, 20e9);
        var b = PlanarExtractor.Extract(LowerLineOnly(), moduleFree, Dbu, 20e9);
        Assert.True(a.Ok, a.Refusal);
        Assert.True(b.Ok, b.Refusal);

        var pa = a.Problem!;
        var pb = b.Problem!;
        Assert.Equal(pb.Layers.Select(l => l.Name), pa.Layers.Select(l => l.Name));
        for (int i = 0; i < pb.Layers.Count; i++)
        {
            Assert.Equal(pb.LevelZ(i), pa.LevelZ(i));
            Assert.Equal(pb.Layers[i].ThicknessM, pa.Layers[i].ThicknessM);
        }
        Assert.Equal(pb.Slab.HeightM, pa.Slab.HeightM);
        Assert.Equal(pb.Slab.Material.EpsR, pa.Slab.Material.EpsR);
        Assert.Equal(pb.EffectiveStack.LayerCount, pa.EffectiveStack.LayerCount);
        for (int i = 0; i < pb.EffectiveStack.LayerCount; i++)
        {
            Assert.Equal(pb.EffectiveStack.Layers[i].ThicknessM, pa.EffectiveStack.Layers[i].ThicknessM);
            Assert.Equal(pb.EffectiveStack.Layers[i].Material.EpsR, pa.EffectiveStack.Layers[i].Material.EpsR);
            Assert.Equal(pb.EffectiveStack.Layers[i].Material.TanD, pa.EffectiveStack.Layers[i].Material.TanD);
        }

        // The sheet went back to the BOTTOM of the Lower band, which is the pre-MIM-6 placement —
        // 100 µm of substrate, not 103. That is the half of the rule that makes the identity exact
        // instead of close.
        Assert.Equal(100.0, pa.LevelZ(0) * 1e6, 12);
        Assert.DoesNotContain(pa.EffectiveStack.Layers, l => Math.Abs(l.Material.EpsR - 7.0) < 1e-9);
    }

    /// <summary>With the plate in the analysis, nothing is deactivated: the film is a region of the
    /// medium at its stated εᵣ, the lower conductor's sheet stays on the top of its band, and the
    /// plate gap is the film alone.</summary>
    [Fact]
    public void WithPlateArtwork_TheFilmIsRealAndTheSheetSurfaceStands()
    {
        var r = PlanarExtractor.Extract(WithAPlate(), ProbeTech(), Dbu, 20e9);
        Assert.True(r.Ok, r.Refusal);
        var p = r.Problem!;

        Assert.Equal(["Lower", "Plate"], p.Layers.Select(l => l.Name));
        Assert.Equal(103.0, p.LevelZ(0) * 1e6, 12);        // top of the Lower band (MIM-6)
        Assert.Equal(104.0, p.LevelZ(1) * 1e6, 12);        // bottom of the Plate band
        var gap = p.EffectiveStack.Layers.Single(l => Math.Abs(l.Material.EpsR - 7.0) < 1e-9);
        Assert.Equal(1e-6, gap.ThicknessM, 12);
        Assert.Equal(0.004, gap.Material.TanD, 9);
        Assert.DoesNotContain(r.Notes, n => n.Contains("patterned thin film", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>A deactivated tie is REPORTED, never silent.</b> Same discipline as the extractor's
    /// dropped-artwork note, and for the same reason: a medium the user did not author and cannot
    /// see is exactly the kind of change that produces a complete, believable answer to a question
    /// nobody asked. One note, naming both halves of the rule and what would switch it back on.
    /// </summary>
    [Fact]
    public void ADeactivatedTie_IsNamedInTheRunNotes_WithBothHalvesOfTheRule()
    {
        var r = PlanarExtractor.Extract(LowerLineOnly(), ProbeTech(), Dbu, 20e9);
        Assert.True(r.Ok, r.Refusal);

        var note = Assert.Single(r.Notes, n => n.Contains("patterned thin film", StringComparison.Ordinal));
        Assert.Contains("'Film'", note, StringComparison.Ordinal);
        Assert.Contains("'Plate'", note, StringComparison.Ordinal);
        Assert.Contains("as AIR", note, StringComparison.Ordinal);
        Assert.Contains("'Lower's analysis sheet is put back on the BOTTOM", note, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A tie naming a conductor the stackup does not have leaves the film ACTIVE.</b> The other
    /// choice — deactivate whenever the name does not resolve — would make a typo silently thin the
    /// medium, which is the failure the whole mechanism exists to prevent. It is a note, not a
    /// refusal, because the extraction is still a valid one; <c>TechValidation</c> is where the typo
    /// is called an error.
    /// </summary>
    [Fact]
    public void ATieNamingAnUnknownConductor_LeavesTheFilmInPlace_AndSaysSo()
    {
        var tech = ProbeTech();
        tech.Stackup.Layers.Single(l => l.Name == "Film").PresentWithLayer = "NoSuchPlate";

        var r = PlanarExtractor.Extract(LowerLineOnly(), tech, Dbu, 20e9);
        Assert.True(r.Ok, r.Refusal);
        // Nothing was deactivated, so the sheet-surface half of the rule did not fire either: the
        // level is at 103 µm (the TOP of the Lower band) exactly as the technology authored it,
        // where a working tie would have put it back at 100.
        Assert.Equal(103.0, r.Problem!.LevelZ(0) * 1e6, 12);
        Assert.DoesNotContain(r.Notes, n => n.Contains("patterned thin film", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.Contains("NoSuchPlate", StringComparison.Ordinal) &&
                                      n.Contains("no stackup entry", StringComparison.Ordinal));

        Assert.Contains(TechValidation.Validate(tech),
                        p => p.Contains("patterned with an unknown conductor", StringComparison.Ordinal));
    }

    /// <summary>Naming the plate as an analysis level activates the film even with no plate artwork
    /// drawn — the tie asks about the RUN's levels, which is the setup's to state, not about what
    /// happens to be drawn.</summary>
    [Fact]
    public void NamingThePlateAsAnAnalysisLevel_ActivatesTheFilm()
    {
        var r = PlanarExtractor.Extract(WithAPlate(), ProbeTech(), Dbu, 20e9,
            new EmExtractionSettings(AnalysisLevelNames: ["Lower"]));
        Assert.True(r.Ok, r.Refusal);
        Assert.Single(r.Problem!.Layers);
        Assert.Equal(100.0, r.Problem!.LevelZ(0) * 1e6, 12);        // deactivated: sheet at the bottom
        Assert.Contains(r.Notes, n => n.Contains("patterned thin film", StringComparison.Ordinal));

        var both = PlanarExtractor.Extract(LowerLineOnly(), ProbeTech(), Dbu, 20e9,
            new EmExtractionSettings(AnalysisLevelNames: ["Lower", "Plate"]));
        Assert.True(both.Ok, both.Refusal);
        Assert.Equal(103.0, both.Problem!.LevelZ(0) * 1e6, 12);     // active: sheet on the top
        Assert.Contains(both.Problem!.EffectiveStack.Layers, l => Math.Abs(l.Material.EpsR - 7.0) < 1e-9);
        Assert.DoesNotContain(both.Notes, n => n.Contains("patterned thin film", StringComparison.Ordinal));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The rule, in the cross-section (uniform-line) extractor
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The closed-form path reads the tie too, and it has to.</b> The cross-section kernel builds
    /// its own layered medium from the same stackup, so a film left switched on there sits as
    /// superstrate on every line drawn on the metal below it — the second of MIM-2's two measured
    /// costs, and the one that moves Z₀. There is no sheet surface to revert: this kernel models real
    /// metal of real thickness and never reads <c>SheetAt</c> (MIM-6's own decision).
    ///
    /// <para>Asserted as the numbers, both ways round, so the size of what the tie removes is on the
    /// record rather than described.</para>
    /// </summary>
    [Fact]
    public void TheCrossSectionExtractorHonoursTheTie_SoALineOnTheLowerMetalDoesNotMove()
    {
        var tied  = CrossSectionExtractor.Extract(LowerLineOnly(), ProbeTech(),          Dbu);
        var stuck = CrossSectionExtractor.Extract(LowerLineOnly(), ProbeTech(tied: false), Dbu);
        Assert.True(tied.Ok, tied.Refusal);
        Assert.True(stuck.Ok, stuck.Refusal);

        // With the tie honoured the film is air, so the only εᵣ above 1 in the problem is the
        // substrate's — the line is an ordinary microstrip.
        Assert.DoesNotContain(tied.Problem!.Regions, d => Math.Abs(d.Material.EpsR - 7.0) < 1e-9);
        Assert.Contains(stuck.Problem!.Regions,      d => Math.Abs(d.Material.EpsR - 7.0) < 1e-9);
        Assert.Contains(tied.Notes, n => n.Contains("patterned thin film", StringComparison.Ordinal));

        // …and a line drawn on the PLATE is a line under nothing: the film is beneath it either way,
        // so this is the case where the tie is active and the extraction is the same both ways.
        var onPlate = new List<LayoutShape>
        {
            Rect(Plate, 0, 0, 400, 60), Port(Plate, 0, 30, "P1"), Port(Plate, 400, 30, "P2"),
        };
        var plateTied  = CrossSectionExtractor.Extract(onPlate, ProbeTech(),            Dbu);
        var plateStuck = CrossSectionExtractor.Extract(onPlate, ProbeTech(tied: false), Dbu);
        Assert.True(plateTied.Ok, plateTied.Refusal);
        Assert.Equal(plateStuck.Problem!.Regions.Count, plateTied.Problem!.Regions.Count);
        Assert.DoesNotContain(plateTied.Notes, n => n.Contains("patterned thin film", StringComparison.Ordinal));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The schema half — validation, persistence, merge, editor
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The two hard validation rules. The third thing worth saying — name the conductor
    /// directly ABOVE the film — is a RECOMMENDATION, stated in the field's documentation and the
    /// editor's tooltip, and deliberately not failed here: a tie to a conductor further away is
    /// legal and honoured, only harder to read.</summary>
    [Fact]
    public void Validation_RequiresAnExistingNonGroundConductor_OnADielectricEntry()
    {
        var ok = ProbeTech();
        Assert.Empty(TechValidation.Validate(ok));

        var unknown = ProbeTech();
        unknown.Stackup.Layers.Single(l => l.Name == "Film").PresentWithLayer = "Nope";
        Assert.Contains(TechValidation.Validate(unknown),
                        p => p.Contains("patterned with an unknown conductor", StringComparison.Ordinal));

        var toGround = ProbeTech();
        toGround.Stackup.Layers.Single(l => l.Name == "Film").PresentWithLayer = "Ground";
        Assert.Contains(TechValidation.Validate(toGround),
                        p => p.Contains("which is the ground reference", StringComparison.Ordinal));

        var onAConductor = ProbeTech();
        onAConductor.Stackup.Layers.Single(l => l.Name == "Plate").PresentWithLayer = "Lower";
        Assert.Contains(TechValidation.Validate(onAConductor),
                        p => p.Contains("Only a Dielectric entry can be a patterned thin film",
                                        StringComparison.Ordinal));

        // A tie to a conductor that is not the one directly above is legal — and works.
        var faraway = ProbeTech();
        faraway.Stackup.Layers.Single(l => l.Name == "Film").PresentWithLayer = "Lower";
        Assert.Empty(TechValidation.Validate(faraway));
    }

    /// <summary>Round-trips through <c>.ctech</c>, and is ABSENT from the file when unset — an
    /// additive field that wrote itself into every dielectric row would be churn in a hand-edited
    /// format and would claim a decision nobody made.</summary>
    [Fact]
    public void PresentWithLayer_RoundTripsThroughCtech_AndIsAbsentWhenUnset()
    {
        string json = TechPersistence.Serialize(ProbeTech());
        Assert.Equal(1, CountOccurrences(json, "\"PresentWithLayer\""));
        Assert.Contains("\"PresentWithLayer\": \"Plate\"", json, StringComparison.Ordinal);

        var back = TechPersistence.Deserialize(json);
        Assert.Equal("Plate", back.Stackup.Layers.Single(l => l.Name == "Film").PresentWithLayer);
        Assert.All(back.Stackup.Layers.Where(l => l.Name != "Film"), l => Assert.Null(l.PresentWithLayer));

        Assert.DoesNotContain("\"PresentWithLayer\"",
                              TechPersistence.Serialize(ProbeTech(tied: false)), StringComparison.Ordinal);
    }

    /// <summary>The merge carries it. A stackup entry that differs ONLY here is a real conflict —
    /// one is a continuous layer of the medium and the other is not — so it is named in the conflict
    /// description as well.</summary>
    [Fact]
    public void TechnologyMerge_CarriesTheTie_AndNamesItInAConflict()
    {
        var target = ProbeTech(tied: false);
        var source = ProbeTech();

        var conflicts = TechnologyMerge.FindConflicts(target, source, TechSection.Stackup);
        var film = Assert.Single(conflicts, c => c.Key == "Stackup|Film");
        Assert.Contains("patterned with Plate", film.Theirs, StringComparison.Ordinal);
        Assert.DoesNotContain("patterned with", film.Mine, StringComparison.Ordinal);

        TechnologyMerge.Merge(target, source, TechSection.Stackup, TechMergeMode.Replace);
        Assert.Equal("Plate", target.Stackup.Layers.Single(l => l.Name == "Film").PresentWithLayer);
    }

    /// <summary>The editor row: a ComboBox of conductor names with "(none)", committing immediately
    /// and undoably — the same behaviour as the via Spans row it is modelled on. Showing "(none)"
    /// for a technology that says nothing is not an edit.</summary>
    [Fact]
    public void DielectricRow_ChoosingAPlate_CommitsUndoably()
    {
        var tech = ProbeTech(tied: false);
        var vm   = new TechEditorViewModel(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tie-{Guid.NewGuid():N}.ctech"), tech);
        var row  = vm.StackupLayers.Single(r => r.Layer.Name == "Film");

        Assert.Equal("(none)", row.SelectedPresentWith);
        Assert.Contains("Plate", row.PresentWithChoices);
        Assert.DoesNotContain("Ground", row.PresentWithChoices);   // a plate is never the ground plane
        Assert.False(vm.UndoRedo.CanUndo);

        row.SelectedPresentWith = "Plate";
        Assert.Equal("Plate", vm.Working.Stackup.Layers.Single(l => l.Name == "Film").PresentWithLayer);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();
        Assert.Null(vm.Working.Stackup.Layers.Single(l => l.Name == "Film").PresentWithLayer);

        vm.UndoRedo.Redo();
        Assert.Equal("Plate", vm.Working.Stackup.Layers.Single(l => l.Name == "Film").PresentWithLayer);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}

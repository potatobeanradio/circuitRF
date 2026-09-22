// Gate for docs/sonnet-briefs/brief-gi2-stackup-skeleton.md, as the owner amended it on 2026-09-08.
//
// An import that resolved six copper layers, worked out their order, bound each to a drawing layer and
// reported all of it — and then wrote a stackup with two via entries and nothing else. GI2 emits the
// STRUCTURE it had already computed.
//
// WHAT THIS FILE ASSERTED UNTIL 2026-09-08, AND WHY IT NO LONGER DOES. The load-bearing assertion was
// Epsr == 0 on every dielectric: zero is this codebase's spelling of "nobody said", it is outside every
// extractor's guard and outside TechValidation's `Epsr < 1`, so the skeleton was UNSIMULATABLE by
// construction. That was aimed at StackupLayer.Epsr's own C# default of 1.0 — AIR, a perfectly valid
// substrate that RUNS and answers a different question with nothing downstream to question it.
//
// The owner's decision is that the zeros were being paid for on every Gerber import, and that a
// default which is NAMED, REPORTED and plausible is not the silent-air failure that rule guarded
// against. So the structure is completed with one ordinary FR-4 board (SubstrateDefaults) and the
// import says which values it supplied. The tests below assert the FR-4 values and — still, in the same
// place and for the same reason — that a dielectric is never left at 1.0. Air remains the wrong answer;
// what changed is that "no answer" is no longer the alternative.
//
// The rule this file still holds shut, unchanged: NOTHING is inferred from the material NAMES in the
// files. TheDielectricsAreNamedPositionally_AndNameNoLaminate is that gate.
//
// Fixtures are hand-authored, following L4e/L4f/L4g/GI1's precedent: worth less than a real set as a
// dialect test, costs nothing to redistribute, names no tool or product.
//
// COUNTERS AND CONTENT ONLY. No wall-clock assertion anywhere in this file.

using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

public class Gi2StackupSkeletonTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gi2-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────────

    private const string MmHeader = "%FSLAX46Y46*%\n%MOMM*%\n";

    private static string Artwork(string? fileFunction = null, double xMm = 1.0, double yMm = 1.0)
    {
        string attribute = fileFunction is { Length: > 0 } fn ? $"%TF.FileFunction,{fn}*%\n" : "";
        long x = (long)Math.Round(xMm * 1_000_000);
        long y = (long)Math.Round(yMm * 1_000_000);
        return MmHeader + attribute + "%ADD10C,0.400*%\nD10*\n" + $"X{x}Y{y}D03*\n" + "M02*\n";
    }

    private static string Drill(double xMm = 1.0, double yMm = 1.0) =>
        "M48\nMETRIC\nT1C0.300000\n%\nG90\nG05\nT1\n" + $"X{xMm:0.000000}Y{yMm:0.000000}\n" + "M30\n";

    private string Folder(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string dir, string fileName, string content) =>
        File.WriteAllText(Path.Combine(dir, fileName), content);

    private GerberImport.ImportResult Import(string sourceDir, string name) =>
        GerberImport.Import(
            [.. Directory.EnumerateFiles(sourceDir).OrderBy(p => p, StringComparer.Ordinal)],
            _root, name, null, 1000, null, null);

    private static Technology TechOf(GerberImport.ImportResult r) =>
        TechPersistence.LoadFromFile(r.TechPath!);

    /// <summary>
    /// Six copper layers, top to bottom, every one of them DECLARED — so the stack order is not itself
    /// under test here — plus soldermask on both sides and one plated drill file. This is the shape the
    /// brief's opening paragraph describes: eleven stackup rows the importer had already computed and
    /// then discarded.
    /// </summary>
    private string SixLayerSet(bool withMask = true, bool withDrill = true, string? jobFile = null)
    {
        var dir = Folder(Guid.NewGuid().ToString("N")[..8]);
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.g2",  Artwork("Copper,L2,Inr,Plane",  xMm: 2.0));
        Write(dir, "board.g3",  Artwork("Copper,L3,Inr,Signal", xMm: 3.0));
        Write(dir, "board.g4",  Artwork("Copper,L4,Inr,Signal", xMm: 4.0));
        Write(dir, "board.g5",  Artwork("Copper,L5,Inr,Plane",  xMm: 5.0));
        Write(dir, "board.gbl", Artwork("Copper,L6,Bot,Signal", xMm: 6.0));
        if (withMask)
        {
            Write(dir, "board.gts", Artwork("Soldermask,Top", xMm: 1.5));
            Write(dir, "board.gbs", Artwork("Soldermask,Bot", xMm: 1.5));
        }
        if (withDrill) Write(dir, "board.drl", Drill());
        if (jobFile is not null) Write(dir, "board.gbrjob", jobFile);
        return dir;
    }

    private static List<StackupLayer> Electrical(Technology tech) =>
        [.. tech.Stackup.Layers.Where(l => l.Kind != StackupKind.Via)];

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 1 — structure without values
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ASixCopperSetWithNoJobFile_YieldsSixConductorsAndFiveDielectrics_TopToBottom()
    {
        var result = Import(SixLayerSet(), "skeleton_six");
        Assert.False(result.Cancelled);

        var tech = TechOf(result);
        var electrical = Electrical(tech);

        Assert.Equal(11, electrical.Count);
        Assert.Equal(6, electrical.Count(l => l.Kind == StackupKind.Conductor));
        Assert.Equal(5, electrical.Count(l => l.Kind == StackupKind.Dielectric));

        // Conductor, dielectric, conductor, … — a sandwich, never two conductors touching.
        Assert.Equal(
            [StackupKind.Conductor, StackupKind.Dielectric, StackupKind.Conductor, StackupKind.Dielectric,
             StackupKind.Conductor, StackupKind.Dielectric, StackupKind.Conductor, StackupKind.Dielectric,
             StackupKind.Conductor, StackupKind.Dielectric, StackupKind.Conductor],
            electrical.Select(l => l.Kind));

        // In the resolved top-to-bottom order, named as their drawing layers are named (R-gi2-2), so
        // the Stackup tab and the layer table read as one document.
        Assert.Equal(
            ["Top Copper", "Inner 1", "Inner 2", "Inner 3", "Inner 4", "Bottom Copper"],
            electrical.Where(l => l.Kind == StackupKind.Conductor).Select(l => l.Name));

        // Each conductor binds its OWN drawing layer, and the six are distinct.
        var bound = electrical.Where(l => l.Kind == StackupKind.Conductor)
                              .Select(l => Assert.Single(l.DrawingLayers)).ToList();
        Assert.Equal(6, bound.Distinct().Count());

        // ...and every one of those keys is a layer the technology actually defines.
        var known = tech.Layers.Select(l => l.Key).ToHashSet();
        Assert.All(bound, k => Assert.Contains(k, known));

        // Physics, not a guess about this board — through the ONE constant, never a second copy.
        Assert.All(electrical.Where(l => l.Kind == StackupKind.Conductor),
                   l => Assert.Equal(PcbStackupMapping.DefaultCopperConductivitySm, l.SigmaSm));

        // Every quantity that describes the SUBSTRATE is FILLED IN, from the one generic board
        // (owner, 2026-09-08). Outer copper is the first and last conductor in the top-to-bottom
        // order; the four between them are inner foil.
        var copper = electrical.Where(l => l.Kind == StackupKind.Conductor).ToList();
        Assert.Equal(35_000, copper[0].ThicknessDbu);
        Assert.Equal(35_000, copper[^1].ThicknessDbu);
        Assert.All(copper[1..^1], l => Assert.Equal(18_000, l.ThicknessDbu));

        // The five dielectrics share the default 1.778 mm board, because no file in this set states an
        // overall thickness. A six-layer board that came out 9 mm thick would be the arithmetic
        // nobody checks.
        Assert.All(electrical.Where(l => l.Kind == StackupKind.Dielectric), l =>
        {
            Assert.Equal(355_600, l.ThicknessDbu);       // 1778 um / 5
            Assert.Equal(4.4, l.Epsr, 12);
            Assert.Equal(0.02, l.TanD, 12);
            Assert.Equal(1.0, l.Mur, 12);
        });
    }

    /// <summary>R-gi2-5 — positional and neutral. The number and construction of the layers between two
    /// copper sheets is a fabrication decision that no artwork file states, and a plausible name is the
    /// thing that stops someone checking. (It would also be a third-party product name in this repo.)</summary>
    [Fact]
    public void TheDielectricsAreNamedPositionally_AndNameNoLaminate()
    {
        var tech = TechOf(Import(SixLayerSet(), "skeleton_names"));
        var dielectrics = Electrical(tech).Where(l => l.Kind == StackupKind.Dielectric).ToList();

        // Asserted in the POSITIVE, exhaustively — a list of forbidden material names here would be
        // the very glossary root CLAUDE.md §"Commercial Vendor References" forbids the repo to carry,
        // and "the name is exactly this" excludes every other name anyway.
        Assert.Equal(["Dielectric 1", "Dielectric 2", "Dielectric 3", "Dielectric 4", "Dielectric 5"],
                     dielectrics.Select(l => l.Name));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 3 — NOT AIR. Read this file's header before changing anything below.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>StackupLayer.Epsr</c> defaults to <c>1.0</c>, which is AIR: a valid, entirely simulatable
    /// substrate that would be indistinguishable from a measured one. That is still the wrong answer
    /// and this is still the test that says so — what changed on 2026-09-08 is the RIGHT answer.
    /// Until then it was <c>0</c>, "nobody said", which made the stack unusable on purpose; it is now
    /// one ordinary FR-4 board, supplied by <c>SubstrateDefaults</c> and named in the import's own
    /// message.
    ///
    /// <para><b>The <c>NotEqual(1.0)</c> below is the assertion most likely to be lost in a later
    /// tidy-up. Keep it.</b> A refactor that stopped writing Epsr explicitly in the skeleton branch,
    /// or that let Fill treat 1.0 as "unset", would put air back with no other symptom.</para>
    /// </summary>
    [Fact]
    public void TheDielectricsAreOrdinaryFr4_AndNeverAir()
    {
        var tech = TechOf(Import(SixLayerSet(), "skeleton_notair"));

        foreach (var d in Electrical(tech).Where(l => l.Kind == StackupKind.Dielectric))
        {
            Assert.Equal(SubstrateDefaults.Epsr, d.Epsr, 12);
            Assert.NotEqual(1.0, d.Epsr);   // 1.0 is air, and air runs. Spelled out on purpose.
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 2 — a skeleton must be UNSIMULATABLE, and the refusal must say what is missing
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static IReadOnlyList<LayoutShape> ArtworkOf(GerberImport.ImportResult result) =>
        LayoutPersistence.LoadFromFile(
            Directory.EnumerateFiles(result.CellDir!, "*.clay", SearchOption.AllDirectories).First()).Shapes;

    /// <summary>
    /// R-gi2-4, INVERTED by the owner's 2026-09-08 decision, on the two-conductor set.
    ///
    /// <para>Until then this asserted that both extractors refused a fresh import and that each named
    /// the missing substrate — which was the point of the zeros. With the substrate supplied, <b>no
    /// refusal from either kernel may mention a missing substrate value any more</b>: that is what the
    /// change bought, and a refusal still saying "zero thickness" would mean the fill never ran.
    ///
    /// <para>Both are still checked, because neither one's refusal covers the other's path. What each
    /// says now is about something else entirely and both are legitimate: the cross-section kernel
    /// refuses a circular pad as a non-uniform cross-section, and the planar kernel refuses because
    /// nothing has named a GROUND REFERENCE — a real, separate decision no artwork file can make
    /// (R-gi2-10), and the one thing this fill deliberately does not guess at.</para>
    /// </summary>
    [Fact]
    public void AFreshImportIsNoLongerRefusedForItsSubstrate_AndWhatIsLeftIsNotASubstrateQuestion()
    {
        // Two copper layers and one through hole — the pad on top is claimed by the drill as a via,
        // which leaves exactly one signal level and so takes the kernel past its own level check to
        // the substrate question this gate is about.
        var dir = Folder("twolayer");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.gbl", Artwork("Copper,L2,Bot,Signal", xMm: 2.0));
        Write(dir, "board.drl", Drill());

        var result = Import(dir, "twolayer_import");
        var tech = TechOf(result);
        var shapes = ArtworkOf(result);

        var cross = CrossSectionExtractor.Extract(shapes, tech, 1000);
        if (!cross.Ok) Assert.DoesNotContain("zero thickness", cross.Refusal!, StringComparison.Ordinal);

        // The planar path used to reach the SLAB-HEIGHT check first and answer "the signal sits at or
        // below the ground plane — check the stackup order", which is a wrong diagnosis of a stack
        // whose order is fine and whose thicknesses were never entered. It now names the real cause.
        var planar = PlanarExtractor.Extract(shapes, tech, 1000, 1e9);
        if (!planar.Ok)
        {
            Assert.DoesNotContain("zero thickness", planar.Refusal!, StringComparison.Ordinal);
            // What IS left: which copper is ground. Nothing in a Gerber set says it, and this fill
            // does not pretend to know — so the message names the decision rather than a value.
            Assert.Contains("ground reference", planar.Refusal!, StringComparison.Ordinal);
        }

        // Deliberately not asserted here: that marking a ground plane makes this particular set run.
        // This fixture's only remaining signal shape IS the bottom conductor's (the top pad was
        // claimed by the drill as a via), so marking it ground leaves nothing to solve for — a
        // property of a two-file fixture, not of the substrate. What this gate is about is that no
        // refusal names a substrate value any more.
    }

    /// <summary>The six-layer set too. The cross-section kernel's own multi-level refusal legitimately
    /// comes first there (a six-layer board is not one cross-section, whatever its substrate says) —
    /// and neither kernel may refuse it for a substrate value any more.</summary>
    [Fact]
    public void TheSixLayerImportIsNotRefusedForItsSubstrateEither()
    {
        var result = Import(SixLayerSet(), "skeleton_refused");
        var tech = TechOf(result);
        var shapes = ArtworkOf(result);

        var cross = CrossSectionExtractor.Extract(shapes, tech, 1000);
        Assert.False(cross.Ok);                      // six levels is not one cross-section
        Assert.DoesNotContain("zero thickness", cross.Refusal!, StringComparison.Ordinal);

        var planar = PlanarExtractor.Extract(shapes, tech, 1000, 1e9);
        if (!planar.Ok) Assert.DoesNotContain("zero thickness", planar.Refusal!, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 4 / 5 — one message, not twenty-two; and it shrinks as the work is done
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A skeleton has conductors, so every per-row check that <c>stackupIsSubstrateless</c> used to
    /// suppress re-engaged at once: eleven "non-positive thickness" and five "εr &lt; 1". That was the
    /// 22-message wall arriving by a different door, and R-gi2-9 replaced it with one summary.
    ///
    /// <para><b>Since 2026-09-08 there is nothing for that summary to say</b> — the values are
    /// supplied, so nothing is unset, so the wall it was suppressing cannot form either. The COUNT is
    /// still asserted, because that is what this gate is about: an import must not hand anybody a list
    /// of problems it created itself.</para>
    /// </summary>
    [Fact]
    public void AFreshImportReportsOneStackupProblem_AndItIsTheOneNoFileCouldAnswer()
    {
        var tech = TechOf(Import(SixLayerSet(), "skeleton_validator"));
        var stackupProblems = TechValidation.Analyze(tech)
            .Where(p => p.Area == TechProblemArea.Stackup).ToList();

        // Exactly one, and it is the decision no artwork file can make: which copper is ground
        // (R-gi2-10). The substrate summary is gone because there is no gap left to summarise, and
        // the plated via's wall thickness went the same way at GI3 R-gi3-4 — an import writes the
        // same 25 um every shipped technology writes and names it as a default.
        var only = Assert.Single(stackupProblems);
        Assert.Contains("ground reference", only.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(stackupProblems,
            p => p.Message.Contains("Plated with no wall thickness", StringComparison.Ordinal));
        Assert.DoesNotContain(stackupProblems,
            p => p.Message.Contains("substrate values are not", StringComparison.Ordinal));

        // The per-row walls are gone, and now for a second reason as well.
        Assert.DoesNotContain(stackupProblems, p => p.Message.Contains("non-positive thickness", StringComparison.Ordinal));
        Assert.DoesNotContain(stackupProblems, p => p.Message.Contains("εr < 1", StringComparison.Ordinal));
    }

    /// <summary>Gate 5, in the form that survives the fill: somebody typing their fabricator's real
    /// numbers over the defaulted ones must pick up no new problems for having started.</summary>
    [Fact]
    public void TypingRealNumbersOverTheDefaults_RaisesNothingNew()
    {
        var tech = TechOf(Import(SixLayerSet(), "skeleton_progressive"));
        int before = TechValidation.Analyze(tech).Count(p => p.Area == TechProblemArea.Stackup);

        var first = Electrical(tech).First(l => l.Kind == StackupKind.Dielectric);
        first.ThicknessDbu = 1_500_000;   // 1.5 mm
        first.Epsr = 3.66;
        first.TanD = 0.004;

        var after = TechValidation.Analyze(tech).Where(p => p.Area == TechProblemArea.Stackup).ToList();
        Assert.Equal(before, after.Count);           // the same one fact, none added

        // And the row that was edited is not reported at all — not as unset, not as wrong.
        Assert.DoesNotContain(after, p => p.Message.Contains($"\"{first.Name}\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// R-gi2-11. The distinction that matters is UNSET versus WRONG, and the fill turns on the same
    /// one: a freshly imported row's zero is "nobody said" and gets a default, while a negative
    /// thickness or a permittivity of 0.5 is a value somebody typed and is left alone AND reported.
    /// A pass that "tidied" those would hide a real mistake behind a plausible number.
    /// </summary>
    [Fact]
    public void AValueSomebodyTypedWrongly_KeepsItsOwnRow_EvenInsideASkeleton()
    {
        var tech = TechOf(Import(SixLayerSet(), "skeleton_wrong"));
        var dielectrics = Electrical(tech).Where(l => l.Kind == StackupKind.Dielectric).ToList();

        dielectrics[0].ThicknessDbu = -5;    // wrong, not unset
        dielectrics[1].ThicknessDbu = 100;
        dielectrics[1].Epsr = 0.5;           // wrong, not unset

        var stackup = TechValidation.Analyze(tech).Where(p => p.Area == TechProblemArea.Stackup).ToList();

        Assert.Single(stackup, p => p.Message.Contains("non-positive thickness (-5 DBU)", StringComparison.Ordinal));
        Assert.Single(stackup, p => p.Message.Contains("εr < 1 (0.5)", StringComparison.Ordinal));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 7 — the via entries now have conductors to name
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TheDrillLayerSpansTheTopmostAndBottommostConductors_AndTheValidatorAgrees()
    {
        var tech = TechOf(Import(SixLayerSet(), "skeleton_via"));

        var via = Assert.Single(tech.Stackup.Layers, l => l.Kind == StackupKind.Via);
        Assert.Equal("Top Copper", via.SpanFromLayer);
        Assert.Equal("Bottom Copper", via.SpanToLayer);

        // Before GI2 there were no conductor entries to name, so this same set produced two "spans an
        // unknown conductor layer" problems on top of the missing-stackup one.
        Assert.DoesNotContain(TechValidation.Analyze(tech),
            p => p.Message.Contains("spans an unknown conductor layer", StringComparison.Ordinal));
        Assert.DoesNotContain(TechValidation.Analyze(tech),
            p => p.Message.Contains("names a via layer but no conductor layers", StringComparison.Ordinal));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 8 — mask, paste and legend are artwork, not stackup entries
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// R-gi2-6. Mask IS a dielectric in the physical stack — which is exactly why leaving it out has to
    /// be a stated decision rather than an omission. Its ARTWORK states neither a thickness nor a
    /// permittivity, and adding it silently changes the conductor-to-conductor geometry a solver sees.
    /// </summary>
    [Fact]
    public void SoldermaskAndPasteAreImportedAsArtwork_AndSaidOnceToBeOutOfTheStackup()
    {
        var result = Import(SixLayerSet(withMask: true), "skeleton_mask");
        var tech = TechOf(result);

        // The mask layers ARE in the layer table…
        Assert.Contains(tech.Layers, l => l.Name == "Soldermask Top");
        Assert.Contains(tech.Layers, l => l.Name == "Soldermask Bottom");

        // …and are in the stackup nowhere, under any kind.
        var maskKeys = tech.Layers.Where(l => l.Name.StartsWith("Soldermask", StringComparison.Ordinal))
                                  .Select(l => l.Key).ToHashSet();
        Assert.DoesNotContain(tech.Stackup.Layers, sl => sl.DrawingLayers.Any(maskKeys.Contains));
        Assert.DoesNotContain(tech.Stackup.Layers,
            sl => sl.Name.Contains("mask", StringComparison.OrdinalIgnoreCase));

        // One message, once.
        var said = Assert.Single(result.Messages,
            m => m.Contains("are imported as artwork and are NOT in the stackup", StringComparison.Ordinal)
              || m.Contains("were imported as artwork and are NOT in the stackup", StringComparison.Ordinal));
        Assert.Contains("Soldermask Top", said, StringComparison.Ordinal);
        Assert.Contains("Soldermask Bottom", said, StringComparison.Ordinal);
    }

    /// <summary>The control: a set with no mask files says nothing about mask at all.</summary>
    [Fact]
    public void ASetWithNoMask_SaysNothingAboutMask()
    {
        var result = Import(SixLayerSet(withMask: false), "skeleton_nomask");
        Assert.DoesNotContain(result.Messages,
            m => m.Contains("NOT in the stackup", StringComparison.Ordinal));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 6 — the job-file branch is untouched
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private const string JobFileWithStackup =
        """
        {
          "Header": { "GenerationSoftware": { "Vendor": "n/a", "Application": "n/a" } },
          "GeneralSpecs": { "ProjectId": { "Name": "board" }, "Size": { "X": 10, "Y": 10 },
                            "LayerNumber": 2, "BoardThickness": 1.57 },
          "MaterialStackup": [
            { "Type": "Legend",     "Notes": "top legend" },
            { "Type": "SolderMask", "Thickness": 0.01 },
            { "Type": "Copper",     "Name": "Top Copper",    "Thickness": 0.035 },
            { "Type": "Dielectric", "Name": "Substrate", "Thickness": 1.5,
              "DielectricConstant": 4.4, "LossTangent": 0.02 },
            { "Type": "Copper",     "Name": "Bottom Copper", "Thickness": 0.035 },
            { "Type": "SolderMask", "Thickness": 0.01 }
          ]
        }
        """;

    /// <summary>Every field of every electrical entry, in order, as one string — a SNAPSHOT, so a
    /// regression anywhere in the job-file branch shows up as a diff rather than surviving a spot
    /// check that happened not to look at the field that moved.</summary>
    private static string Snapshot(Stackup stackup) =>
        string.Join("\n", stackup.Layers.Select(l =>
            $"{l.Kind}|{l.Name}|t={l.ThicknessDbu}|er={l.Epsr:R}|tand={l.TanD:R}|mur={l.Mur:R}|" +
            $"sigma={l.SigmaSm:R}|draw={l.DrawingLayers.Count}|from={l.SpanFromLayer}|to={l.SpanToLayer}|" +
            $"fill={l.Fill}|plated={l.Plated}|wall={l.WallThicknessDbu}"));

    /// <summary>
    /// R-gi2-8. A job file that carries a stackup is a STATEMENT about the board; the skeleton is not,
    /// and a partial job-file stackup must never be topped up from it. Nothing about this set changed.
    /// </summary>
    [Fact]
    public void TheSameSetPlusAJobFile_StillTakesTheJobFilesStackupWhole()
    {
        var dir = Folder("jobfile");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.gbl", Artwork("Copper,L2,Bot,Signal", xMm: 2.0));
        Write(dir, "board.gbrjob", JobFileWithStackup);

        var result = Import(dir, "jobfile_import");
        var stackup = TechOf(result).Stackup;

        Assert.Equal(
            """
            Conductor|Top Copper|t=35000|er=1|tand=0|mur=1|sigma=58000000|draw=1|from=|to=|fill=|plated=|wall=
            Dielectric|Substrate|t=1500000|er=4.4|tand=0.02|mur=1|sigma=0|draw=0|from=|to=|fill=|plated=|wall=
            Conductor|Bottom Copper|t=35000|er=1|tand=0|mur=1|sigma=58000000|draw=1|from=|to=|fill=|plated=|wall=
            """,
            Snapshot(stackup));

        // The job file's own thicknesses and permittivity — nothing here is a skeleton zero.
        Assert.DoesNotContain(stackup.Layers, l => l.Kind != StackupKind.Via && l.ThicknessDbu == 0);
        Assert.DoesNotContain(result.Messages,
            m => m.Contains("were created from the artwork", StringComparison.Ordinal));

        // Two copper files but a job file that declares three conductor-and-dielectric rows: the
        // skeleton must not fill the gap, and the two-conductor stackup above is the proof.
        Assert.Equal(2, stackup.Layers.Count(l => l.Kind == StackupKind.Conductor));
    }

    /// <summary>A job file with no <c>MaterialStackup</c> at all is the skeleton branch, and its stated
    /// board thickness is still reported — the one substrate fact such a set carries.</summary>
    [Fact]
    public void AJobFileWithNoStackup_TakesTheSkeleton_AndStillReportsTheBoardThickness()
    {
        var dir = Folder("jobnostack");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.gbl", Artwork("Copper,L2,Bot,Signal", xMm: 2.0));
        Write(dir, "board.gbrjob",
            """
            {
              "Header": { "GenerationSoftware": { "Vendor": "n/a", "Application": "n/a" } },
              "GeneralSpecs": { "ProjectId": { "Name": "board" }, "Size": { "X": 10, "Y": 10 },
                                "LayerNumber": 2, "BoardThickness": 1.57 }
            }
            """);

        var result = Import(dir, "jobnostack_import");
        var electrical = Electrical(TechOf(result));

        Assert.Equal(3, electrical.Count);                   // 2 conductors, 1 dielectric

        // The stated board thickness is now SPENT, not just reported (owner, 2026-09-08): 1.57 mm
        // less the two 35 um outer coppers leaves 1.5 mm for the single dielectric. A number a file
        // actually states always beats SubstrateDefaults' own 1.778 mm board.
        Assert.Equal(1_500_000, electrical.Single(l => l.Kind == StackupKind.Dielectric).ThicknessDbu);
        Assert.All(electrical.Where(l => l.Kind == StackupKind.Conductor),
                   l => Assert.Equal(35_000, l.ThicknessDbu));

        var said = Assert.Single(result.Messages,
            m => m.Contains("were created from the artwork", StringComparison.Ordinal));
        Assert.Contains("overall board thickness of 1.57 mm", said, StringComparison.Ordinal);

        var supplied = Assert.Single(result.Messages,
            m => m.Contains("NOT STATED BY ANY FILE IN THIS SET", StringComparison.Ordinal));
        Assert.Contains("the overall board thickness the files state", supplied, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 9 — round trip
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Including the substrate. A persistence layer that dropped one of these fields and let
    /// <c>Epsr</c>'s C# default of 1.0 come back would turn every reloaded import into air — the same
    /// failure the zeros used to guard against, by the same door.</summary>
    [Fact]
    public void TheImportedTechnologyRoundTripsThroughCtech_WithItsSubstrateIntact()
    {
        var result = Import(SixLayerSet(), "skeleton_roundtrip");

        // The import already wrote the .ctech; reload it, write it again, reload again.
        var first = TechPersistence.LoadFromFile(result.TechPath!);
        string second = Path.Combine(_root, "again.ctech");
        TechPersistence.SaveToFile(second, first);
        var reloaded = TechPersistence.LoadFromFile(second);

        Assert.Equal(Snapshot(first.Stackup), Snapshot(reloaded.Stackup));
        Assert.All(Electrical(reloaded), l => Assert.True(l.ThicknessDbu > 0));
        Assert.All(Electrical(reloaded).Where(l => l.Kind == StackupKind.Dielectric), l =>
        {
            Assert.Equal(SubstrateDefaults.Epsr, l.Epsr, 12);
            Assert.NotEqual(1.0, l.Epsr);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The message the import prints (R-gi2-12)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>TWO paragraphs, and they must stay two.</b> One reports what the FILES said; the other
    /// reports what nothing said and circuitRF supplied anyway. The whole safety of defaulting rests
    /// on somebody being able to tell those apart at a glance, so a single merged paragraph — read
    /// values and guesses in one sentence — is the regression this asserts against.
    /// </summary>
    [Fact]
    public void TheImportSaysWhatItBuilt_AndSeparatelyWhatItGuessed()
    {
        var result = Import(SixLayerSet(), "skeleton_message");

        var built = Assert.Single(result.Messages,
            m => m.Contains("were created from the artwork", StringComparison.Ordinal));

        Assert.Contains("6 conductor layer(s) and 5 dielectric layer(s)", built, StringComparison.Ordinal);
        Assert.Contains("states nothing about the substrate at all", built, StringComparison.Ordinal);
        Assert.Contains("5.8e+7 S/m", built, StringComparison.Ordinal);
        Assert.Contains("named here as a default", built, StringComparison.Ordinal);

        // The claim that made this paragraph honest before the fill would now be false in it.
        Assert.DoesNotContain("NO SUBSTRATE WAS INVENTED", built, StringComparison.Ordinal);

        var guessed = Assert.Single(result.Messages,
            m => m.Contains("NOT STATED BY ANY FILE IN THIS SET", StringComparison.Ordinal));

        // Every quantity supplied is NAMED, with its number, and the paragraph says plainly that
        // these describe a board circuitRF has not seen.
        Assert.Contains("35 um outer", guessed, StringComparison.Ordinal);
        Assert.Contains("18 um inner", guessed, StringComparison.Ordinal);
        Assert.Contains("355.6 um each", guessed, StringComparison.Ordinal);
        Assert.Contains("relative permittivity 4.4", guessed, StringComparison.Ordinal);
        Assert.Contains("loss tangent 0.02", guessed, StringComparison.Ordinal);
        Assert.Contains("guesses", guessed, StringComparison.Ordinal);
        Assert.Contains("Stackup tab", guessed, StringComparison.Ordinal);

        // And the rule that did NOT change is restated where somebody reading the message will see it.
        Assert.Contains("was inferred from the material names", guessed, StringComparison.Ordinal);

        // The old "left EMPTY" sentence is gone — it would now be false.
        Assert.DoesNotContain(result.Messages,
            m => m.Contains("stackup was left EMPTY", StringComparison.Ordinal));
    }

    /// <summary>A set whose job file states a complete stackup is a STATEMENT about the board, so
    /// there is nothing to supply and nothing is said. The note must never appear over values a file
    /// actually carried.</summary>
    [Fact]
    public void ACompleteJobFileStackup_GetsNoDefaultsNote()
    {
        var dir = Folder("jobcomplete");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.gbl", Artwork("Copper,L2,Bot,Signal", xMm: 2.0));
        Write(dir, "board.gbrjob", JobFileWithStackup);

        var result = Import(dir, "jobcomplete_import");
        Assert.DoesNotContain(result.Messages,
            m => m.Contains("NOT STATED BY ANY FILE IN THIS SET", StringComparison.Ordinal));
    }

    /// <summary>The one case that still leaves the stackup genuinely empty: no copper anywhere, so
    /// there is no structure to emit either.</summary>
    [Fact]
    public void ASetWithNoCopperAtAll_StillLeavesTheStackupEmpty_AndSaysSo()
    {
        var dir = Folder("nocopper");
        Write(dir, "board.gts", Artwork("Soldermask,Top"));
        Write(dir, "board.drl", Drill());

        var result = Import(dir, "nocopper_import");
        var tech = TechOf(result);

        Assert.Empty(Electrical(tech));
        Assert.Contains(result.Messages, m =>
            m.Contains("no copper artwork", StringComparison.Ordinal) &&
            m.Contains("left EMPTY and no substrate was invented", StringComparison.Ordinal));
    }
}

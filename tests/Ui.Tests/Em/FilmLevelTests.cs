// TFR-V — a via-contacted film on a level of its own: the technology's rules, what the planar extractor makes of it,
// metal crossing the film, and the gates that read the committed runs in testdata/em-tfr/zigzag-via.

using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Core.Design;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Tests.Em3d;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

public sealed class FilmLevelTests
{
    private static readonly LayerKey Metal1 = new(1, 0), Film = new(4, 0), Contact = new(11, 0);
    private const int Dbu = 1000;

    private static string Fixture(params string[] parts)
        => Path.Combine([PalaceBackendTests.RepoRoot(), "testdata", "em-tfr", "zigzag-via", .. parts]);

    private static string Overlap(params string[] parts)
        => Path.Combine([PalaceBackendTests.RepoRoot(), "testdata", "em-tfr", "zigzag", .. parts]);

    private static Technology Tech() => TechPersistence.LoadFromFile(Fixture("tech", "zigzag-via-GaAs.ctech"));

    private static RectShape Rect(LayerKey layer, double x1, double y1, double x2, double y2) => new()
    {
        Layer = layer, X1 = (long)(x1 * Dbu), Y1 = (long)(y1 * Dbu), X2 = (long)(x2 * Dbu), Y2 = (long)(y2 * Dbu),
    };

    private static LabelShape Port(string name, double x) => new()
    {
        Layer = Metal1, X = (long)(x * Dbu), Y = 0, Text = name, Height = 5000, IsPort = true,
    };

    private static StackupLayer Level(Technology tech) => tech.Stackup.Layers.Single(l => l.Name == "TFR Level");

    private static IEnumerable<string> FilmIds(Technology tech)
        => TechValidation.Analyze(tech).Select(p => p.Id ?? "").Where(id => id.StartsWith("tech.film.", StringComparison.Ordinal));

    // ── R-tfrv-1 — the model ────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnEntryWithNoMetalAndAFilmIsAFilmLevelAndValidatesClean()
    {
        var tech = Tech();
        Assert.True(ResistiveFilms.IsFilmLevel(Level(tech)));
        Assert.False(ResistiveFilms.IsFilmLevel(tech.Stackup.Layers.Single(l => l.Name == "Metal1")));
        Assert.DoesNotContain(TechValidation.Analyze(tech), p => p.Message.Contains("TFR Level"));
    }

    [Fact]
    public void AFilmLevelNoViaReachesIsNoContact()
    {
        var tech = Tech();
        tech.Stackup.Layers.RemoveAll(l => l.Name == "TFR Contact");
        Assert.Contains(ResistorFilmBinding.Ids.NoContact, FilmIds(tech));
    }

    [Fact]
    public void AFilmLevelTouchingAConductorIsAnErrorAndNotADropTarget()
    {
        var tech = Tech();
        tech.Stackup.Layers.RemoveAll(l => l.Name == "TFR Isolation");
        Assert.Contains(TechValidation.Analyze(tech), p => p.Id == ResistorFilmBinding.Ids.TouchingMetal
                                                         && p.Severity == CircuitRF.Diagnostics.DiagnosticSeverity.Error);
        Assert.False(ResistorFilmBinding.CanPlaceOn(tech, Film, "TFR Level", out _));
    }

    [Fact]
    public void AFaceOrAThicknessOnAFilmLevelMeansNothingAndSaysSo()
    {
        var tech = Tech();
        var film = Level(tech).Films![0];
        film.At = ConductorSheetSurface.Top;
        film.ThicknessDbu = 60;
        var ids = FilmIds(tech).ToList();
        Assert.Contains(ResistorFilmBinding.Ids.FaceOnLevel, ids);
        Assert.Contains(ResistorFilmBinding.Ids.ThicknessOnLevel, ids);
    }

    // ── R-tfrv-2 — the extractor ────────────────────────────────────────────────────────────────

    private static LayoutShape[] Straight(params LayoutShape[] more) =>
    [
        Rect(Metal1, 0, -20, 100, 20), Rect(Metal1, 220, -20, 320, 20),
        Rect(Film, 90, -20, 230, 20),
        Rect(Contact, 90, -20, 100, 20), Rect(Contact, 220, -20, 230, 20),
        Port("1", 0), Port("2", 320), .. more,
    ];

    private static EmExtractionSettings Settings => new(SignalStackupLayerName: "Metal1");

    [Fact]
    public void ADrawnFilmsLevelJoinsTheRunAndSaysSo()
    {
        var r = PlanarExtractor.Extract(Straight(), Tech(), Dbu, 10e9, Settings);
        Assert.True(r.Problem is not null, r.Refusal);
        Assert.Equal(2, r.Problem!.Layers.Count);
        Assert.Contains(r.Notes, n => n.Contains("'TFR Level' joined"));
    }

    [Fact]
    public void ALevelListLeavingOutADrawnFilmIsRefusedByName()
    {
        var r = PlanarExtractor.Extract(Straight(), Tech(), Dbu, 10e9, new EmExtractionSettings(AnalysisLevelNames: ["Metal1"]));
        Assert.Null(r.Problem);
        Assert.Contains("'TFR Level'", r.Refusal);
    }

    [Fact]
    public void MetalOverAFilmOnItsOwnLevelClipsNothing()
    {
        var bare = PlanarExtractor.Extract(Straight(), Tech(), Dbu, 10e9, Settings).Problem!;
        var strapped = PlanarExtractor.Extract(Straight(Rect(Metal1, 150, -30, 170, 30)), Tech(), Dbu, 10e9, Settings).Problem!;
        Assert.Equal(1.0, FilmArea(strapped) / FilmArea(bare), 12);
    }

    private static double FilmArea(PlanarProblem p)
        => p.Layers.SelectMany(l => l.Films).SelectMany(f => f.Polygons).Sum(q => { var (x0, y0, x1, y1) = q.Bounds(); return (x1 - x0) * (y1 - y0); });

    /// <summary>Gate 5 — the thickness correction declines on a level carrying a film because the growth would short it.
    /// Moved to its own level the film no longer stops Metal1 being corrected, and the film itself is not grown.</summary>
    [Fact]
    public void TheThicknessCorrectionRunsOnMetalOverAFilmLevel()
    {
        var plain = PlanarExtractor.Extract(Straight(), Tech(), Dbu, 10e9, Settings).Problem!;
        var r = PlanarExtractor.Extract(Straight(), Tech(), Dbu, 10e9, Settings with { ConductorThicknessCorrection = true });
        Assert.Contains(r.Notes, n => n.Contains("Conductor thickness on 'Metal1'"));
        Assert.DoesNotContain(r.Notes, n => n.Contains("NOT applied"));
        Assert.Equal(1.0, FilmArea(r.Problem!) / FilmArea(plain), 12);
    }

    // ── R-tfrv-3 — metal over a film, at DC, where the network is exact for the mesh and costs nothing ─────────────

    /// <summary>The series resistance a 0 Hz run reads, or null when the ports do not connect.</summary>
    private static double? DcSeriesR(IEnumerable<LayoutShape> art, Technology tech, int cellsAcross = 0)
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.AddRange(art);
        string scratch = Path.Combine(Path.GetTempPath(), "crf-filmlevel-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var setup = new EmSetup
            {
                Name = "dc", LayoutRef = "a.clay", SignalStackupLayerName = "Metal1", AnalysisKind = EmAnalysisKind.Planar,
                Frequency = new FrequencySpec("0", "0", 1),
            };
            if (cellsAcross > 0) setup.PlanarMesh = setup.PlanarMesh with { MinCellsAcrossConductor = cellsAcross };
            var run = EmRunService.Run(setup, new EmLayoutSource(Path.Combine(scratch, "a.clay"), view, tech, Dbu), scratch);
            Assert.True(run.Status == EmRunStatus.Ok, run.Error);
            Complex s21 = run.Data!.Cubes["S"].ComplexValues[2];
            return s21.Magnitude < 1e-6 ? null : 100.0 * (1.0 / s21.Real - 1.0);
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }

    private static IEnumerable<LayoutShape> ZigZag(string fixtureRoot)
        => LayoutPersistence.LoadFromFile(Path.Combine(fixtureRoot, "zigzag", "layout", "zigzag.clay")).Shapes;

    /// <summary>Gate 3 — a strap across one leg of the zig-zag, joined to nothing. On the film's own level it passes over
    /// and the film reads as if it were not there. As overlap contact the same artwork shorts that part of the leg.</summary>
    [Fact]
    public void AStrapOverTheFilmDoesNotShortIt()
    {
        var strap = Rect(Metal1, 330, 30, 390, 50);

        double bare = DcSeriesR(ZigZag(Fixture()), Tech())!.Value;
        double strapped = DcSeriesR([.. ZigZag(Fixture()), strap], Tech())!.Value;
        Assert.InRange(strapped / bare - 1, -0.01, 0.01);

        var overlap = TechPersistence.LoadFromFile(Overlap("tech", "mmic-GaAs_2LM_100um.ctech"));
        double overlapBare = DcSeriesR(ZigZag(Overlap()), overlap)!.Value;
        double overlapStrapped = DcSeriesR([.. ZigZag(Overlap()), strap], overlap)!.Value;
        Assert.True(overlapStrapped < 0.95 * overlapBare, $"{overlapStrapped} Ω against {overlapBare} Ω");
    }

    /// <summary>Gate 4 — metal heads run 30 µm past each via, over the film, and the length is still the distance between
    /// the vias' inner edges: 180 µm of a 40 µm film is 4.5 squares, 225 Ω, where the heads' own edges would give 150 Ω. The
    /// fixture's two methods spread 2.3 %. A via contacts the film at its cells' centres, so the network reads long by up to
    /// half a cell each end: 233.3, 231.9, 230.9 and 228.6 Ω at 4, 8, 16 and 32 cells across. The shipping mesh is therefore
    /// 3.7 % high on a film this short, and the gate is asked of the refined one.</summary>
    [Fact]
    public void TheViaOpeningsSetTheLength()
        => Assert.InRange(DcSeriesR(Straight60(), Tech(), 32)!.Value / (50.0 * 180.0 / 40.0) - 1, -0.023, 0.023);

    private static LayoutShape[] Straight60() =>
    [
        Rect(Metal1, 0, -20, 100, 20), Rect(Metal1, 220, -20, 320, 20),
        Rect(Film, 60, -20, 260, 20),
        Rect(Contact, 60, -20, 70, 20), Rect(Contact, 250, -20, 260, 20),
        Port("1", 0), Port("2", 320),
    ];

    /// <summary>Gate 6's planar half — with a contact via missing nothing joins the film to the metal, so the run reads an open.</summary>
    [Fact]
    public void AMissingContactViaIsAnOpen()
        => Assert.Null(DcSeriesR(Straight60().Where(x => !(x is RectShape { X1: 250000 } r && r.Layer == Contact)), Tech()));

    // ── The committed runs (testdata/em-tfr/zigzag-via/README.md) ─────────────────────────────────

    private static Dictionary<double, Complex[]> ReadS2p(string path)
    {
        var rows = new Dictionary<double, Complex[]>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] is '!' or '#') continue;
            var v = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            rows[v[0]] = [new(v[1], v[2]), new(v[3], v[4]), new(v[5], v[6]), new(v[7], v[8])];
        }
        return rows;
    }

    private static double WorstDelta(Dictionary<double, Complex[]> a, Dictionary<double, Complex[]> b, double f)
        => Enumerable.Range(0, 4).Max(e => (a[f][e] - b[f][e]).Magnitude);

    /// <summary>Gate 1 — a level with nothing drawn on it leaves a line as it was, at every frequency, against the same stack
    /// and total height without the band. It was 0.18 at 1 GHz (src/Engine/Mom/RESOLVED.md, TFR-V).</summary>
    [Fact]
    public void AnUndrawnFilmLevelLeavesALineAlone()
    {
        var with = ReadS2p(Fixture("refs", "p_thru.s2p"));
        var without = ReadS2p(Fixture("refs", "p_thru_noband.s2p"));
        Assert.Equal(6, with.Count);
        foreach (double f in with.Keys) Assert.InRange(WorstDelta(with, without, f), 0, 0.002);
    }

    /// <summary>Gate 2 — the same resistor on its own level against the overlap run. Its length is the same, by construction.</summary>
    [Fact]
    public void TheZigZagOnAFilmLevelMatchesTheOverlapRun()
    {
        var via = ReadS2p(Fixture("refs", "p_dut.s2p"));
        var overlap = ReadS2p(Overlap("refs", "p_dut.s2p"));
        Assert.Equal(6, via.Count);
        foreach (double f in via.Keys) Assert.InRange(WorstDelta(via, overlap, f), 0, 0.01);
    }
}

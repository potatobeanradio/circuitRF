// TFR-V — a via-contacted film on a level of its own: the technology's rules, what the planar extractor makes of it,
// metal crossing the film, and the gates that read the committed runs in testdata/em-tfr/zigzag-via.

using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
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

    // ── R-tfrv-4 — 3D ──────────────────────────────────────────────────────────────────────────────

    /// <summary>New 3D View from Layout carries the film as a sheet and the contact as a solid.</summary>
    [Fact]
    public void A3DViewFromTheLayoutCarriesTheViaAndTheFilm()
    {
        string cell = Fixture("zigzag");
        var made = CircuitRF.Design.ThreeD.C3dHierarchy.NewFromLayout(cell, Path.Combine(cell, "3d", "z.c3d"), Fixture(".cws"));
        Assert.True(made.Document is not null, made.Refusal);
        Assert.Empty(made.PortsLeftOut);
        var e = CircuitRF.Design.ThreeD.C3dElaborator.ElaborateOnce(made.Document!, Path.Combine(cell, "3d", "z.c3d"), null);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        Assert.Contains(e.Sheets, s => s.Material == "Film Resistor");
        Assert.Equal(2, e.Solids.Count(s => s.Name.StartsWith("U1/via/", StringComparison.Ordinal)));
    }

    /// <summary>A placed TFR's artwork is a generated cell, a cache a checkout does not have. A workspace's open rebuilds it and
    /// repoints the layout, and then New 3D View finds the metal.</summary>
    [Fact]
    public void A3DViewOfAPlacedTfrFindsItsMetalAfterTheWorkspaceOpens()
    {
        string root = Path.Combine(Path.GetTempPath(), "crf-filmlevel-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            string example = Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "PDK PCells");
            Directory.CreateDirectory(root);
            File.Copy(Path.Combine(example, ".cws"), Path.Combine(root, ".cws"));
            foreach (string sub in new[] { "tech", "ThinFilmResistor" })
                foreach (string f in Directory.EnumerateFiles(Path.Combine(example, sub), "*", SearchOption.AllDirectories))
                {
                    string to = Path.Combine(root, Path.GetRelativePath(example, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(f, to);
                }
            string cws = Path.Combine(root, ".cws");
            CircuitRF.Design.Layout.PCells.GeneratedCellsLifecycle.RegenerateAll(root, r => r is not null && File.Exists(r) ? TechPersistence.LoadFromFile(r) : null);

            string cell = Path.Combine(root, "ThinFilmResistor");
            var made = CircuitRF.Design.ThreeD.C3dHierarchy.NewFromLayout(cell, Path.Combine(cell, "3d", "t.c3d"), cws);
            Assert.True(made.Document is not null, made.Refusal);
            Assert.Empty(made.PortsLeftOut);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static Em3dProblem ThreeD(params LayoutShape[] more)
    {
        var view = LayoutPersistence.LoadFromFile(Fixture("zigzag", "layout", "zigzag.clay"));
        view.Shapes.AddRange(more);
        var tech = Tech();
        var setup = EmSetupPersistence.LoadFromFile(Fixture("zigzag", "em", "sheets.cem"));
        var made = Em3dGenerator.Generate(setup, new EmLayoutSource(Fixture("zigzag", "layout", "zigzag.clay"), view, tech, Dbu), tech);
        Assert.True(made.Problem is not null, made.Refusal);
        return made.Problem!;
    }

    /// <summary>The 3D via ends on the metal's sheet. Past it, that sheet would lie inside the barrel and Palace refuses the mesh.</summary>
    [Fact]
    public void AViaEndsOnTheSheetsItJoins()
    {
        var p = ThreeD();
        var via = p.Solids.First(s => s.Name.StartsWith("via/", StringComparison.Ordinal));
        var (_, _, z0, _, _, z1) = Em3dProblem.Bounds(via.Primitive);
        Assert.Equal(p.Sheets.Single(s => s.Name.StartsWith("Metal1", StringComparison.Ordinal) && s.Name.EndsWith("/1", StringComparison.Ordinal)).Z, z1, 12);
        Assert.Equal(p.Sheets.Single(s => s.Material == "Film Resistor").Z, z0, 12);
    }

    /// <summary>EMerge's film is a 3 µm volume, and rising through Metal1 it contacted whatever crossed it: a strap took the zig-zag from
    /// 295 Ω to 225 Ω at 1 GHz. It goes below the film, keeping the dielectric's permittivity.</summary>
    [Fact]
    public void EMergesVolumeKeepsClearOfMetalOverTheFilm()
    {
        var p = ThreeD(Rect(Metal1, 330, 30, 390, 50));
        double film = p.Sheets.Single(s => s.Material == "Film Resistor").Z;
        var lowered = EmergeLoweringRules.ResistiveSheetsAsVolumes(p, out var notes);
        var volume = lowered.Solids.Single(s => s.Name.Contains("Resistor", StringComparison.Ordinal));
        var (_, _, z0, _, _, z1) = Em3dProblem.Bounds(volume.Primitive);
        Assert.Equal(film, z1, 12);
        Assert.True(z0 < film);
        Assert.Equal(12.9, lowered.Materials.Single(m => m.Name == volume.Material).Epsr);
        Assert.Contains(notes, n => n.Contains("below the film", StringComparison.Ordinal));
    }

    /// <summary>With metal on both sides of the film inside that span, EMerge has nowhere to put it, and says so by name.</summary>
    [Fact]
    public void EMergeRefusesAFilmWithMetalCloseOnBothSides()
    {
        var p = ThreeD();
        Assert.Null(EmergeLoweringRules.ResistiveVolumeRefusal(p));
        double film = p.Sheets.Single(s => s.Material == "Film Resistor").Z;
        var under = p.Sheets.First(s => s.Name.StartsWith("Metal1", StringComparison.Ordinal)) with { Name = "Under", Z = film - 0.4e-6, Outline = p.Sheets.Single(s => s.Material == "Film Resistor").Outline };
        string why = EmergeLoweringRules.ResistiveVolumeRefusal(p with { Sheets = [.. p.Sheets, under] })!;
        Assert.Contains("Under", why);
        Assert.Contains("Palace", why);
    }

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

    /// <summary>Gate 7 — the planar film section against Palace's, de-embedded by each solver's own thru. The owner accepted the
    /// worst of them, 0.0287 at 40 GHz, so the gate is 0.03 where EM-TFR's is 0.02 (README). It is met at 0.02 through 10 GHz.</summary>
    [Theory]
    [InlineData("pa")]
    [InlineData("pb")]
    public void TheFilmSectionAgreesWithPalace(string run)
    {
        static (Complex A, Complex B, Complex C, Complex D) Abcd(Complex[] s)
        {
            Complex s11 = s[0], s21 = s[1], s12 = s[2], s22 = s[3], d = 2 * s21;
            const double z = 50;
            return (((1 + s11) * (1 - s22) + s12 * s21) / d, z * ((1 + s11) * (1 + s22) - s12 * s21) / d,
                    ((1 - s11) * (1 - s22) - s12 * s21) / (d * z), ((1 - s11) * (1 + s22) + s12 * s21) / d);
        }
        // The section between two half-thrus, and its S: the ABCD square root deembed.py takes.
        static Complex[] Section(Complex[] dut, Complex[] thru)
        {
            var (a, b, c, d) = Abcd(dut);
            var (ta, tb, tc, td) = Abcd(thru);
            Complex sd = Complex.Sqrt(ta * td - tb * tc), st = Complex.Sqrt(ta + td + 2 * sd);
            Complex ha = (ta + sd) / st, hb = tb / st, hc = tc / st, hd = (td + sd) / st;
            if (ha.Real < 0) (ha, hb, hc, hd) = (-ha, -hb, -hc, -hd);
            Complex det = ha * hd - hb * hc;
            (Complex ia, Complex ib, Complex ic, Complex id) = (hd / det, -hb / det, -hc / det, ha / det);
            Complex ma = ia * a + ib * c, mb = ia * b + ib * d, mc = ic * a + id * c, md = ic * b + id * d;
            Complex ra = ma * ia + mb * ic, rb = ma * ib + mb * id, rc = mc * ia + md * ic, rd = mc * ib + md * id;
            const double z = 50;
            Complex den = ra + rb / z + rc * z + rd;
            return [(ra + rb / z - rc * z - rd) / den, 2 / den, 2 * (ra * rd - rb * rc) / den, (-ra + rb / z - rc * z + rd) / den];
        }
        var pd = ReadS2p(Fixture("refs", "p_dut.s2p"));
        var pt = ReadS2p(Fixture("refs", "p_thru.s2p"));
        var rd = ReadS2p(Fixture("refs", run + "_dut.s2p"));
        var rt = ReadS2p(Fixture("refs", run + "_thru.s2p"));
        Assert.Equal(6, rd.Count);
        foreach (double f in rd.Keys)
        {
            var p = Section(pd[f], pt[f]);
            var r = Section(rd[f], rt[f]);
            for (int e = 0; e < 4; e++) Assert.InRange((p[e] - r[e]).Magnitude, 0, f <= 10 ? 0.02 : 0.03);
        }
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

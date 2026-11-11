// EM-TFR — a resistive film bound to a conductor level: the technology's rules, what the planar extractor and the 3D
// generator make of it, and the gates that read the campaign's committed references (testdata/em-tfr/zigzag/README.md).

using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Tests.Em3d;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

public sealed class ResistiveFilmTests
{
    private const string GaAs = "mmic-GaAs_2LM_100um";
    private static readonly LayerKey Metal1 = new(1, 0), Resistor = new(4, 0);
    private const int Dbu = 1000;

    private static string Fixture(params string[] parts)
        => Path.Combine([PalaceBackendTests.RepoRoot(), "testdata", "em-tfr", "zigzag", .. parts]);

    private static RectShape Rect(LayerKey layer, double x1, double y1, double x2, double y2) => new()
    {
        Layer = layer, X1 = (long)(x1 * Dbu), Y1 = (long)(y1 * Dbu), X2 = (long)(x2 * Dbu), Y2 = (long)(y2 * Dbu),
    };

    private static EmExtractionSettings Settings => new(SignalStackupLayerName: "Metal1");

    private static (EmSetup Setup, EmLayoutSource Source) LoadCem(string cell, string cem)
    {
        string path = Fixture(cell, "em", cem + ".cem");
        var setup = EmSetupPersistence.LoadFromFile(path);
        var resolved = EmSetupResolver.Resolve(path, setup.LayoutRef, Fixture(".cws"), new TechnologyCache());
        Assert.True(resolved.Source is not null, string.Join(" ", resolved.Diagnostics));
        return (setup, resolved.Source!);
    }

    private static Em3dProblem Generated(string cell, Em3dConductorMode mode)
    {
        var (setup, source) = LoadCem(cell, "sheets");
        setup.Conductors = mode;
        var r = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(r.Ok, r.Refusal);
        return r.Problem!;
    }

    // ── §1 The technology ───────────────────────────────────────────────────────────────────────

    /// <summary>Gate 8 — each binding a film may not have is an error, and the shipped one has none.</summary>
    [Theory]
    [InlineData("two-levels")]
    [InlineData("on-via")]
    [InlineData("on-ground")]
    [InlineData("also-metal")]
    public void AWrongBindingIsAnError(string rule)
    {
        var tech = ShippedTechnologies.Load(GaAs);
        Assert.DoesNotContain(TechValidation.Analyze(tech), p => p.Id?.StartsWith("tech.film.", StringComparison.Ordinal) == true);
        var entry = (string name) => tech.Stackup.Layers.First(l => l.Name == name);
        switch (rule)
        {
            case "two-levels": entry("Metal2").Films = [new StackupFilm { Layer = Resistor, SheetResistanceOhmPerSq = 50 }]; break;
            case "on-via":     ResistorFilmBinding.Remove(tech, Resistor); entry("Backside Via").Films = [new StackupFilm { Layer = Resistor, SheetResistanceOhmPerSq = 50 }]; break;
            case "on-ground":  ResistorFilmBinding.Remove(tech, Resistor); entry("Backside Metal").Films = [new StackupFilm { Layer = Resistor, SheetResistanceOhmPerSq = 50 }]; break;
            case "also-metal": entry("Metal1").DrawingLayers.Add(Resistor); break;
        }
        var problem = TechValidation.Analyze(tech).Single(p => p.Id == "tech.film." + rule);
        Assert.Equal(CircuitRF.Diagnostics.DiagnosticSeverity.Error, problem.Severity);
        Assert.Contains("Resistor", problem.Message);
    }

    /// <summary>A bound film is not copper: read as copper it would short the resistor it patterns.</summary>
    [Fact]
    public void ABoundFilmIsNotCopper()
    {
        var tech = ShippedTechnologies.Load(GaAs);
        Assert.Equal("Metal1", ResistiveFilms.ConductorOf(tech, Resistor)!.Name);
        Assert.DoesNotContain(Resistor, tech.Stackup.Layers.First(l => l.Name == "Metal1").DrawingLayers);
        Assert.DoesNotContain(Resistor, Conductors.Of(tech).First(c => c.StackupName == "Metal1").DrawingLayers);
    }

    // ── §2 The extractor ────────────────────────────────────────────────────────────────────────

    /// <summary>R-stk9-0c — a resistor drawn on a layer with no film entry says it is an open, rather than being counted with stray shapes.</summary>
    [Fact]
    public void ALayerWithNoFilmEntryIsNamedAsAnOpen()
    {
        var tech = ShippedTechnologies.Load(GaAs);
        ResistorFilmBinding.Remove(tech, Resistor);
        var result = PlanarExtractor.Extract(
            [Rect(Metal1, 0, -20, 100, 20), Rect(Resistor, 100, -20, 220, 20), Rect(Metal1, 220, -20, 320, 20)], tech, Dbu, 10e9, Settings);
        Assert.True(result.Problem is not null, result.Refusal);
        Assert.Empty(result.Problem!.Layers[0].Films);
        Assert.Contains(result.Notes, n => n.Contains("'Resistor'") && n.Contains("an open between its contacts"));
    }

    /// <summary>Gate 4 — metal wins: a film wholly under metal leaves the run the metal's alone, to the bit.</summary>
    [Fact]
    public void AFilmUnderMetalChangesNothing()
    {
        var tech = ShippedTechnologies.Load(GaAs);
        var metal = Rect(Metal1, 0, -20, 400, 20);
        var alone = PlanarExtractor.Extract([metal], tech, Dbu, 10e9, Settings).Problem!;
        var under = PlanarExtractor.Extract([metal, Rect(Resistor, 100, -10, 300, 10)], tech, Dbu, 10e9, Settings).Problem!;
        Assert.Empty(under.Layers[0].Films);
        Assert.Equal(EmSnpProvenance.GeometryHash(alone), EmSnpProvenance.GeometryHash(under));
    }

    /// <summary>§1 and §6 — the film left over is the part between the contacts' edges, and it is in the geometry hash.</summary>
    [Fact]
    public void TheFilmIsClippedToTheContactsAndStampsTheResult()
    {
        var tech = ShippedTechnologies.Load(GaAs);
        var contacts = new LayoutShape[] { Rect(Metal1, 0, -20, 100, 20), Rect(Metal1, 220, -20, 320, 20) };
        var film = PlanarExtractor.Extract([.. contacts, Rect(Resistor, 95, -20, 225, 20)], tech, Dbu, 10e9, Settings).Problem!;
        var (x0, _, x1, _) = Assert.Single(Assert.Single(film.Layers[0].Films).Polygons).Bounds();
        Assert.Equal(100e-6, x0, 12);
        Assert.Equal(220e-6, x1, 12);

        ResistiveFilms.Of(tech, Resistor)!.SheetResistanceOhmPerSq = 25;
        var other = PlanarExtractor.Extract([.. contacts, Rect(Resistor, 95, -20, 225, 20)], tech, Dbu, 10e9, Settings).Problem!;
        Assert.NotEqual(EmSnpProvenance.GeometryHash(film), EmSnpProvenance.GeometryHash(other));
    }

    /// <summary>The thickness correction grows a level's outline and not its film's region, and the growth is metal along
    /// the film's edges: a shorted resistor, 0.9 Ω where 1224 Ω is right on the PDK example's serpentine. It declines instead.</summary>
    [Fact]
    public void TheThicknessCorrectionDeclinesOnALevelCarryingAFilm()
    {
        var tech = ShippedTechnologies.Load(GaAs);
        LayoutShape[] art = [Rect(Metal1, 0, -20, 100, 20), Rect(Resistor, 95, -20, 225, 20), Rect(Metal1, 220, -20, 320, 20)];
        var plain = PlanarExtractor.Extract(art, tech, Dbu, 10e9, Settings).Problem!;
        var result = PlanarExtractor.Extract(art, tech, Dbu, 10e9, Settings with { ConductorThicknessCorrection = true });
        Assert.Equal(EmSnpProvenance.GeometryHash(plain), EmSnpProvenance.GeometryHash(result.Problem!));
        Assert.Contains(result.Notes, n => n.Contains("NOT applied on 'Metal1'") && n.Contains("resistive film"));
    }

    /// <summary>Gate 6's hash half — a run with no film keeps the geometry hash HEAD gave it, so every existing .snp stays
    /// current. The value was read from the build before this brief; its S and DC bits were compared there too (README).</summary>
    [Fact]
    public void NoFilmKeepsTheGeometryHash()
    {
        PlanarPolygon R(double x0, double y0, double x1, double y1)
            => new([new EmPoint(x0, y0), new EmPoint(x1, y0), new EmPoint(x1, y1), new EmPoint(x0, y1)]);
        var single = new PlanarProblem([new PlanarConductorLayer("Metal1", [R(0, -36.5e-6, 600e-6, 36.5e-6)], 4.1e7, 3e-6)],
                                       GroundedSlab.GaAsStarter, 5e9);
        Assert.Equal("7af0df44ed51c674", EmSnpProvenance.GeometryHash(single));

        var stack = new LayerStack(Termination.Pec,
            [new MediumLayer(100e-6, new EmMaterial(12.90, 0.0020)), new MediumLayer(3e-6, new EmMaterial(2.70, 0.0020))], Termination.Air);
        var two = new PlanarProblem(
            [new PlanarConductorLayer("M1", [R(0, 0, 400e-6, 100e-6)], 4.1e7, 2e-6, stack.InterfaceZ[1]),
             new PlanarConductorLayer("M2", [R(0, 0, 400e-6, 100e-6)], 4.1e7, 3e-6, stack.TopZ)],
            GroundedSlab.GaAsStarter, 10e9, null, stack, [new PlanarVia(0, 1, [R(180e-6, 30e-6, 220e-6, 70e-6)], 4.1e7)]);
        Assert.Equal("3dd2b9198a71db17", EmSnpProvenance.GeometryHash(two));
    }

    // ── §8 The 3D generator ─────────────────────────────────────────────────────────────────────

    /// <summary>Gate 9 — one sheet per clipped film region with σ·t = 1/R_s, in every mode, and the metal is what the mode names.</summary>
    [Theory]
    [InlineData(Em3dConductorMode.Sheets)]
    [InlineData(Em3dConductorMode.Solids)]
    public void TheFilmIsASheetInEveryModeAndTheMetalFollowsTheMode(Em3dConductorMode mode)
    {
        var p = Generated("zigzag", mode);
        var film = Assert.Single(p.Sheets, s => s.Material.StartsWith("Film ", StringComparison.Ordinal));
        double sigma = p.Materials.Single(m => m.Name == film.Material).SigmaSm;
        Assert.Equal(1.0 / 50.0, sigma * film.ThicknessM, 9);

        var metal = p.Sheets.Where(s => s != film).Select(s => s.Name).ToList();
        if (mode == Em3dConductorMode.Sheets)
        {
            Assert.Equal(2, metal.Count);
            Assert.DoesNotContain(p.Solids, s => s.Role == Em3dRole.Conductor);
            // The planar model's plane: the film on the metal's own sheet, on the substrate's surface.
            Assert.All(p.Sheets, s => Assert.Equal(film.Z, s.Z, 12));
        }
        else
        {
            Assert.Empty(metal);
            Assert.Equal(2, p.Solids.Count(s => s.Role == Em3dRole.Conductor));
        }
    }

    /// <summary>§8 — a stated thickness and face are used, and survive the file: σ follows so that σ·t stays 1/R_s.</summary>
    [Fact]
    public void AStatedThicknessAndFaceAreTheFilms()
    {
        var (setup, source) = LoadCem("zigzag", "sheets");
        setup.Conductors = Em3dConductorMode.Solids;
        var def = ResistiveFilms.Of(source.Technology!, Resistor)!;
        long? thickness = def.ThicknessDbu;
        var face = def.At;
        try
        {
            def.ThicknessDbu = 100;
            def.At = ConductorSheetSurface.Bottom;
            var p = Em3dGenerator.Generate(setup, source, source.Technology!).Problem!;
            var film = Assert.Single(p.Sheets);
            Assert.Equal(100e-9, film.ThicknessM, 15);
            Assert.Equal(2e5, p.Materials.Single(m => m.Name == film.Material).SigmaSm, 3);
            // The bottom of Metal1's band: 3 µm of backside metal and 100 µm of GaAs up.
            Assert.Equal(103e-6, film.Z, 12);

            var copy = ResistiveFilms.Of(TechPersistence.Clone(source.Technology!), Resistor)!;
            Assert.Equal(100, copy.ThicknessDbu);
            Assert.Equal(ConductorSheetSurface.Bottom, copy.At);
        }
        finally { def.ThicknessDbu = thickness; def.At = face; }
    }

    /// <summary>Gate 9 — with no film and no statement, a 3D problem has nothing new in it: goldens hold the bytes.</summary>
    [Fact]
    public void AutoWithNoFilmStatesNothingNew()
    {
        var (setup, source) = LoadCem("thru", "sheets");
        setup.Conductors = Em3dConductorMode.Auto;
        var p = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(p.Ok, p.Refusal);
        Assert.DoesNotContain(p.Problem!.Materials, m => m.Name.StartsWith("Film ", StringComparison.Ordinal));

        // Omitted at Auto, so every setup written before the key reads as it always did.
        string path = Path.Combine(Path.GetTempPath(), "crf-film-" + Guid.NewGuid().ToString("N")[..12] + ".cem");
        try
        {
            EmSetupPersistence.SaveToFile(path, setup);
            Assert.DoesNotContain("Conductors", File.ReadAllText(path));
            setup.Conductors = Em3dConductorMode.Solids;
            EmSetupPersistence.SaveToFile(path, setup);
            Assert.Equal(Em3dConductorMode.Solids, EmSetupPersistence.LoadFromFile(path).Conductors);
        }
        finally { File.Delete(path); }
    }

    /// <summary>§8's goldens: the film lowered for each solver with its σ·t, byte for byte.</summary>
    [Fact]
    public void TheLoweringGoldensHold()
    {
        var (setup, source) = LoadCem("zigzag", "sheets");
        var problem = Em3dGenerator.Generate(setup, source, source.Technology!).Problem!;
        bool write = Environment.GetEnvironmentVariable("CRF_WRITE_FILM_GOLDENS") == "1";
        void Check(string solver, string file, string text)
        {
            string path = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", solver, "film-sheet", file);
            if (write)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
            }
            Assert.Equal(File.ReadAllText(path), text);
        }

        var settings = PalaceSettings.Resolve(setup.Palace);
        var low = GmshGeoWriter.Write(problem, settings);
        Assert.True(low.Ok, low.Refusal);
        var cfg = PalaceConfigWriter.Write(problem, low.Groups, settings);
        Assert.True(cfg.Ok, cfg.Refusal);
        Check("palace-goldens", PalaceConfigWriter.ConfigFile, cfg.Json!);
        Check("palace-goldens", GmshGeoWriter.GroupsFile, low.GroupsJson!);
        Assert.Contains("\"Conductivity\": 400000", cfg.Json);
        Assert.Contains("\"Thickness\": 0.05", cfg.Json);

        var gridSettings = CemOpenEms.ResolveGrid(setup.OpenEms);
        var grid = FdtdGrid.Build(problem, gridSettings, long.MaxValue);
        var xml = CsxcadWriter.Write(problem, grid, gridSettings, CemOpenEms.ResolveRun(setup.OpenEms));
        Assert.True(xml.Ok, xml.Refusal);
        Check("openems-goldens", CsxcadWriter.ModelFile, xml.Model!);
        // Its own (σ, t), and the same σ·t: 1/R_s, whatever the sheet model needed the thickness to be.
        var film = Regex.Match(xml.Model!, "Name=\"Metal1/Resistor/1\" Conductivity=\"([^\"]+)\" Thickness=\"([^\"]+)\"");
        Assert.True(film.Success);
        double sigma = double.Parse(film.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        double thick = double.Parse(film.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(1.0 / 50.0, sigma * thick, 9);
        Assert.True(thick > 1e-6, $"a thin statement lets the sheet model bypass the film: {thick} m");

        var emerge = EmergeScriptWriter.Write(problem, EmergeSettings.Resolve(setup.Emerge));
        Assert.True(emerge.Ok, emerge.Refusal);
        string normalised = Regex.Replace(Regex.Replace(emerge.ProblemJson!, "kernel-[0-9a-f]{16}\\.brep", "kernel-HASH.brep"), "\"[0-9a-f]{64}\"", "\"SHA256\"");
        Check("emerge-goldens", EmergeScriptWriter.ProblemFile, normalised);
        // A volume of the same sheet resistance: EMerge's thin-conductor boundary reads a film low.
        Assert.Contains("Film Resistor (volume)", normalised);
    }

    /// <summary>EM-TFR's openEMS rule: a film is written thick enough that the sheet model's own capacitor stays out of the
    /// band, at the same σ·t, and metal keeps its numbers to the bit.</summary>
    [Fact]
    public void OpenEmsWritesAFilmThickerAndMetalAsItWas()
    {
        var notes = new List<string>();
        var gold = CsxcadWriter.ConductingSheetValues(4.1e7, 3e-6, 2e-14, 40e9, "metal", notes);
        Assert.Equal((4.1e7, 3e-6), gold);
        Assert.Empty(notes);

        var (sigma, thickness) = CsxcadWriter.ConductingSheetValues(4e5, 50e-9, 2e-14, 40e9, "film", notes);
        Assert.Equal(1.0 / 50.0, sigma * thickness, 12);
        Assert.InRange(thickness, 1e-6, 20e-6);
        Assert.Contains(notes, n => n.Contains("'film'") && n.Contains("50 Ω/sq"));
    }

    /// <summary>EM-TFR's EMerge rule: a film becomes a volume of the same sheet resistance at or below the limit where EMerge
    /// solves its current; metal is untouched, and a problem with no film comes back as the same object.</summary>
    [Fact]
    public void EmergeTakesAFilmAsAVolume()
    {
        var problem = Generated("zigzag", Em3dConductorMode.Sheets);
        var lowered = EmergeLoweringRules.ResistiveSheetsAsVolumes(problem, out var notes);
        var volume = Assert.Single(lowered.Solids, s => s.Name == "Metal1/Resistor/1");
        var extruded = Assert.IsType<Em3dExtrudedPolygon>(volume.Primitive);
        double sigma = lowered.Materials.Single(m => m.Name == volume.Material).SigmaSm;
        Assert.Equal(1.0 / 50.0, sigma * (extruded.ZTop - extruded.ZBottom), 9);
        Assert.True(sigma <= EmergeLoweringRules.SurfaceImpedanceLimitSm);
        Assert.Equal(2, lowered.Sheets.Count);
        Assert.Single(notes);

        var metalOnly = Generated("thru", Em3dConductorMode.Sheets);
        Assert.Same(metalOnly, EmergeLoweringRules.ResistiveSheetsAsVolumes(metalOnly, out _));
    }

    /// <summary>EM-TFR's grid rule: a film has at least six cells across its width on both axes, because its corners' share of
    /// the resistance is the current's distribution. Two across read 278 Ω where 306 Ω is right.</summary>
    [Fact]
    public void OpenEmsResolvesAFilmAcrossItsWidth()
    {
        var (setup, source) = LoadCem("zigzag", "sheets");
        var problem = Em3dGenerator.Generate(setup, source, source.Technology!).Problem!;
        var grid = FdtdGrid.Build(problem, CemOpenEms.ResolveGrid(setup.OpenEms), long.MaxValue);
        // The vertical leg, 40 µm wide: x 340..380 µm, with the generator's origin.
        Assert.True(grid.X.Lines.Count(x => x >= 340e-6 - 1e-9 && x <= 380e-6 + 1e-9) >= 7);
        Assert.True(grid.Y.Lines.Count(y => y >= 60e-6 - 1e-9 && y <= 100e-6 + 1e-9) >= 7);
    }

    // ── The campaign's committed references (testdata/em-tfr/zigzag/README.md) ───────────────────

    /// <summary>A two-port Touchstone as (GHz → S as [S11, S12, S21, S22]). The fixture's own RI files, nothing more.</summary>
    private static Dictionary<double, Complex[]> ReadS2p(string name)
    {
        var rows = new Dictionary<double, Complex[]>();
        foreach (string line in File.ReadLines(Fixture("refs", name)))
        {
            if (line.Length == 0 || line[0] is '!' or '#') continue;
            var v = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            rows[v[0]] = [new(v[1], v[2]), new(v[5], v[6]), new(v[3], v[4]), new(v[7], v[8])];
        }
        return rows;
    }

    /// <summary>The series resistance a DC row states: S21 = 2Z₀/(2Z₀ + R).</summary>
    private static double SeriesR(Complex s21) => 100.0 * (1.0 / s21.Real - 1.0);

    /// <summary>The film section between the two feeds: H⁻¹·A·H⁻¹ with H the half of the structure's own 2×-thru, as ABCD.</summary>
    private static Complex[] FilmSection(Complex[] dut, Complex[] thru)
    {
        static Complex[] ToAbcd(Complex[] s)
        {
            Complex s11 = s[0], s12 = s[1], s21 = s[2], s22 = s[3], d = 2 * s21;
            return [((1 + s11) * (1 - s22) + s12 * s21) / d, 50 * ((1 + s11) * (1 + s22) - s12 * s21) / d,
                    ((1 - s11) * (1 - s22) - s12 * s21) / (d * 50), ((1 - s11) * (1 + s22) + s12 * s21) / d];
        }
        static Complex[] Mul(Complex[] a, Complex[] b) =>
            [a[0] * b[0] + a[1] * b[2], a[0] * b[1] + a[1] * b[3], a[2] * b[0] + a[3] * b[2], a[2] * b[1] + a[3] * b[3]];
        static Complex[] Inv(Complex[] m)
        {
            Complex det = m[0] * m[3] - m[1] * m[2];
            return [m[3] / det, -m[1] / det, -m[2] / det, m[0] / det];
        }
        var t = ToAbcd(thru);
        Complex root = Complex.Sqrt(t[0] * t[3] - t[1] * t[2]);
        Complex scale = Complex.Sqrt(t[0] + t[3] + 2 * root);
        Complex[] h = [(t[0] + root) / scale, t[1] / scale, t[2] / scale, (t[3] + root) / scale];
        if (h[0].Real < 0) h = [.. h.Select(x => -x)];
        var m = Mul(Mul(Inv(h), ToAbcd(dut)), Inv(h));
        Complex den = m[0] + m[1] / 50 + m[2] * 50 + m[3];
        return [(m[0] + m[1] / 50 - m[2] * 50 - m[3]) / den, 2 * (m[0] * m[3] - m[1] * m[2]) / den, 2 / den, (-m[0] + m[1] / 50 - m[2] * 50 + m[3]) / den];
    }

    /// <summary>The film section of a planar run of this fixture, at the frequencies its .cem states.</summary>
    private static Dictionary<double, Complex[]> SolveP(string cell, string[] ghz, string scratch)
    {
        var (setup, source) = LoadCem(cell, cell);
        setup.Frequency = new FrequencySpec(ghz[0] + "e9", ghz[0] + "e9", 1);
        setup.AdditionalFrequencies = [.. ghz.Skip(1).Select(f => new FrequencySpec(f + "e9", f + "e9", 1))];
        var run = EmRunService.Run(setup, source, scratch);
        Assert.True(run.Status == EmRunStatus.Ok, run.Error);
        var s = run.Data!.Cubes["S"];
        var rows = new Dictionary<double, Complex[]>();
        for (int k = 0; k < s.Axes[0].Values.Length; k++)
            rows[Math.Round(s.Axes[0].Values[k] / 1e9, 9)] = [s.ComplexValues[k * 4], s.ComplexValues[k * 4 + 1], s.ComplexValues[k * 4 + 2], s.ComplexValues[k * 4 + 3]];
        return rows;
    }

    /// <summary>Gate 10 — the planar DC network against the 3D conduction solve of the hand-built reference, on one zig-zag.
    /// Measured 315.3 Ω against 308.3 Ω, and they bracket the converged value (README): the network reads high and falls to
    /// 312.9 Ω as the mesh is refined, the conforming FEM reads low.</summary>
    [Fact]
    public void PlanarDcAgreesWithTheHandBuiltConductionSolve()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "crf-film-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var p = SolveP("zigzag", ["0"], scratch);
            double planar = SeriesR(p[0.0][2]);
            double reference = SeriesR(ReadS2p("ha_dut.s2p")[0.0][2]);
            Assert.InRange(planar / reference - 1, 0, 0.03);
            // …and 311.8 Ω, four squares of straight film and four 0.559-square corners, lies between them.
            Assert.InRange(311.8, reference, planar);
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }

    /// <summary>Gate 10 — the generated .c3d against the hand-built HA, as geometry: outlines, heights above the floor, σ·t.</summary>
    [Fact]
    public void TheGeneratedSheetsAreTheHandBuiltOnes()
    {
        var generated = Generated("zigzag", Em3dConductorMode.Sheets);
        string path = Fixture("hand", "ha_dut.c3d");
        var doc = C3dPersistence.LoadFromFile(path);
        var setup = C3dSetups.ForRun(C3dSetups.Read(doc).Single().Setup!, path);
        var assembled = C3dProblemAssembly.Assemble(setup, doc, path, Fixture(".cws"));
        Assert.True(assembled.Ok, assembled.Refusal);
        var hand = assembled.Problem!;

        static double Area(IReadOnlyList<Point2> ring)
        {
            double twice = 0;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++) twice += ring[j].X * ring[i].Y - ring[i].X * ring[j].Y;
            return Math.Abs(twice) / 2;
        }
        static (double X0, double Y0, double X1, double Y1, double Area) Shape(Em3dSheet s)
            => (s.Outline.Min(p => p.X), s.Outline.Min(p => p.Y), s.Outline.Max(p => p.X), s.Outline.Max(p => p.Y), Area(s.Outline));
        double SigmaT(Em3dProblem p, Em3dSheet s) => p.Materials.Single(m => m.Name == s.Material).SigmaSm * s.ThicknessM;

        var g = generated.Sheets.OrderBy(s => s.Outline.Max(q => q.X)).ThenBy(s => s.Outline.Min(q => q.X)).ToList();
        var h = hand.Sheets.OrderBy(s => s.Outline.Max(q => q.X)).ThenBy(s => s.Outline.Min(q => q.X)).ToList();
        Assert.Equal(h.Count, g.Count);
        for (int i = 0; i < h.Count; i++)
        {
            var (a, b) = (Shape(g[i]), Shape(h[i]));
            Assert.Equal(b.X0, a.X0, 10); Assert.Equal(b.Y0, a.Y0, 10); Assert.Equal(b.X1, a.X1, 10); Assert.Equal(b.Y1, a.Y1, 10);
            Assert.Equal(b.Area, a.Area, 14);
            Assert.Equal(SigmaT(hand, h[i]), SigmaT(generated, g[i]), 6);
            // The generated stack counts from under the 3 µm backside metal, whose top is its floor.
            Assert.Equal(h[i].Z, g[i].Z - 3e-6, 10);
        }
    }

    /// <summary>
    /// Gate 10 — the planar solve of the film section against Palace's solve of a document that shares no code with it,
    /// to within what HA's own refinement moved (README). A de-embedded full-wave sweep, so it is in the accuracy tier.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void PlanarAgreesWithTheHandBuiltReference()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "crf-film-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            string[] ghz = ["5", "40"];      // the low band and the top: the README holds all five, and a Debug run of all five is over 8 min
            var dut = SolveP("zigzag", ghz, Path.Combine(scratch, "dut"));
            var thru = SolveP("thru", ghz, Path.Combine(scratch, "thru"));
            var hand = ReadS2p("ha_refined_dut.s2p");
            var handThru = ReadS2p("ha_refined_thru.s2p");
            foreach (string f in ghz)
            {
                double key = double.Parse(f, System.Globalization.CultureInfo.InvariantCulture);
                var p = FilmSection(dut[key], thru[key]);
                var r = FilmSection(hand[key], handThru[key]);
                for (int e = 0; e < 4; e++) Assert.InRange((p[e] - r[e]).Magnitude, 0, 0.02);
            }
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }
}

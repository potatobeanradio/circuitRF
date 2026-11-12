using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Ui.Tests.Em3d;

/// <summary>
/// brief-hzn-12 — a scattering setup: a driven 3D setup driven by an incident plane wave, run on EMerge, whose result is a radar
/// cross-section. One test per claim; nothing here solves (the oracle run against the exact sphere series and the comparison with
/// Hertzon are run by hand, and their numbers are in src/Design/RESOLVED.md §HZN).
/// </summary>
public sealed class C3dPlaneWaveTests
{
    private static EmSetup Scattering(Em3dSolverId solver = Em3dSolverId.Emerge) => new()
    {
        Problem3D = Em3dProblemType.Driven,
        Solver3D = solver,
        PlaneWave = new EmPlaneWave3D(new(0, 0, 1), new(1, 0, 0)),
    };

    /// <summary>A view's waves are records of the document: every key reads back as written, and a view with none writes no
    /// PlaneWaves key. A setup stores none.</summary>
    [Fact]
    public void TheViewsWaves_RoundTripThroughTheC3d_AndASetupStoresNone()
    {
        var doc = new CircuitRF.Design.ThreeD.C3dDocument();
        Assert.DoesNotContain("PlaneWaves", CircuitRF.Design.ThreeD.C3dPersistence.Serialize(doc));
        doc.PlaneWaves.Add(new CircuitRF.Design.ThreeD.C3dPlaneWave
        {
            Name = "Nose on", Enabled = false, Direction = [0.75, 0.433, 0.5], EField = [0.0103, 0.748, -0.663], E0 = 2.5,
            PhaseOrigin = new(10, -20, 30), ThetaStepDeg = 5, PhiStepDeg = 3,
        });
        var back = CircuitRF.Design.ThreeD.C3dPersistence.Deserialize(CircuitRF.Design.ThreeD.C3dPersistence.Serialize(doc)).PlaneWaves.Single();
        Assert.Equal(("Nose on", false, 2.5, new CircuitRF.Design.ThreeD.C3dPoint3(10, -20, 30), 5.0, 3.0),
                     (back.Name, back.Enabled, back.E0!.Value, back.PhaseOrigin, back.ThetaStepDeg!.Value, back.PhiStepDeg!.Value));
        Assert.Equal([0.75, 0.433, 0.5], back.Direction);

        Assert.DoesNotContain("Excitation", EmSetupPersistence.Serialize(Scattering()));
    }

    /// <summary>The enabled wave drives every Driven setup of the view, as read and as assembled; a disabled one drives none, and
    /// two enabled are an error naming the rule.</summary>
    [Fact]
    public void TheEnabledWave_DrivesTheViewsDrivenSetups_AndTwoEnabledAreAnError()
    {
        var doc = new CircuitRF.Design.ThreeD.C3dDocument
        {
            Setups = [EmSetupPersistence.ToEmbedded(new EmSetup { Name = "A", Problem3D = Em3dProblemType.Driven, Solver3D = Em3dSolverChoice.One(Em3dSolverId.Emerge) }),
                      EmSetupPersistence.ToEmbedded(new EmSetup { Name = "E", Problem3D = Em3dProblemType.Eigenmode, Solver3D = Em3dSolverChoice.One(Em3dSolverId.Emerge) })],
            PlaneWaves = [new() { Name = "W1", Enabled = false }, new() { Name = "W2", Direction = [1, 0, 0], EField = [0, 0, 1] }],
        };
        var setups = CircuitRF.Design.ThreeD.C3dSetups.Read(doc);
        Assert.Equal(("W2", new Point3(1, 0, 0)), (setups.Single(s => s.Name == "A").Setup!.PlaneWave!.Name, setups.Single(s => s.Name == "A").Setup!.PlaneWave!.Direction));
        Assert.Null(setups.Single(s => s.Name == "E").Setup!.PlaneWave);

        doc.PlaneWaves[1].Enabled = false;
        Assert.Null(CircuitRF.Design.ThreeD.C3dSetups.Read(doc).Single(s => s.Name == "A").Setup!.PlaneWave);

        doc.PlaneWaves[0].Enabled = doc.PlaneWaves[1].Enabled = true;
        Assert.Contains(CircuitRF.Design.ThreeD.C3dValidation.Validate(doc), d => d.Render().Contains("2 plane waves are enabled"));
    }

    /// <summary>R-hzn12-1 — a zero vector, an E-field not at right angles to the direction of travel, and an amplitude that is not
    /// positive are each refused with what is wrong.</summary>
    [Theory]
    [InlineData(0, 0, 0, 1, 0, 0, 1, "direction of travel")]
    [InlineData(0, 0, 1, 0, 0, 0, 1, "E-field is not a vector")]
    [InlineData(0, 0, 1, 1, 0, 1, 1, "not at right angles")]
    [InlineData(0, 0, 1, 1, 0, 0, 0, "is not positive")]
    public void AWaveThatCannotBeSolved_IsRefusedSayingWhy(double kx, double ky, double kz, double ex, double ey, double ez, double e0, string why)
    {
        var w = new EmPlaneWave3D(new(kx, ky, kz), new(ex, ey, ez), e0);
        Assert.Null(w.Resolve(out string? refused));
        Assert.Contains(why, refused);
    }

    /// <summary>R-hzn12-1 — a plane wave with ports beside it is refused, naming the setup.</summary>
    [Fact]
    public void APlaneWaveBesidePorts_IsRefusedNamingTheSetup()
    {
        string? refused = Em3dPlaneWaves.SetupRefusal(Scattering(), ports: 2, setupName: "RCS");
        Assert.Contains("Setup 'RCS'", refused);
        Assert.Contains("2 port(s)", refused);
        Assert.Null(Em3dPlaneWaves.SetupRefusal(Scattering(), ports: 0, setupName: "RCS"));
    }

    /// <summary>R-hzn12-1 — a scattering run's air box absorbs on every face; a conducting face is refused by name.</summary>
    [Fact]
    public void AnAirBoxFaceThatDoesNotAbsorb_IsRefusedByName()
    {
        var faces = new Em3dFaces(Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing,
                                  Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Pec, Em3dBoundaryKind.Absorbing);
        string? refused = Em3dPlaneWaves.BoxRefusal(new Em3dAirBox(new(-1, -1, -1), new(1, 1, 1), faces));
        Assert.Contains("zmin (Pec)", refused);
    }

    /// <summary>R-hzn12-3 — Palace refuses a plane-wave setup by name, saying why and naming the solvers that run one; EMerge and
    /// openEMS refuse nothing.</summary>
    [Fact]
    public void Palace_RefusesAPlaneWaveSayingWhy_AndEmergeAndOpenEmsRunIt()
    {
        string? palace = Em3dRunService.SolverRefusal(Scattering(Em3dSolverId.Palace));
        Assert.Contains("Palace does not", palace);
        Assert.Contains("no incident-field source", palace);
        Assert.Contains("openEMS", palace);
        Assert.Null(Em3dRunService.SolverRefusal(Scattering(Em3dSolverId.OpenEms)));
        Assert.Null(Em3dRunService.SolverRefusal(Scattering()));
    }

    /// <summary>openEMS: the plane wave's box encloses the model with the far-field surface outside it, on grid lines; its phase origin
    /// is the grid line one cell outside the box's low face along a positive direction; and the run's one file excites it (type 10,
    /// the wave's vectors) with no port.</summary>
    [Fact]
    public void OpenEms_PlacesTheWavesBox_BetweenTheModelAndTheSurface_AndExcitesItInItsOneFile()
    {
        var problem = Sphere(0.1);
        var grid = FdtdGrid.Build(problem, OpenEmsGridSettings.Default);
        var lowering = CsxcadWriter.Write(problem, grid, OpenEmsGridSettings.Default, OpenEmsRunSettings.Default, [1e9]);
        Assert.True(lowering.Ok, lowering.Refusal);
        var box = lowering.PlaneWave!;
        var surface = lowering.FarField!;
        Assert.True(box.Min.X < -0.035 && box.Max.X > 0.035, "the box encloses the sphere");
        Assert.True(surface.Min.X < box.Min.X && surface.Max.X > box.Max.X, "the surface is outside the box");
        Assert.Contains(box.Min.X, grid.X.Lines);
        int i = grid.Z.Lines.ToList().IndexOf(box.Min.Z);
        Assert.Equal(grid.Z.Lines[i - 1], box.PhaseOrigin.Z);
        Assert.Equal([0], lowering.Ports);
        Assert.Contains("Type=\"10\" Excite=\"1,0,0\" PropDir=\"0,0,1\"", lowering.PortFiles.Single());
    }

    /// <summary>The setup panel edits no wave: it names the view's wave that drives the setup, and a switch to Palace stops showing it
    /// (the 3D view disables the wave in the same edit).</summary>
    [Fact]
    public void ThePanel_NamesTheWaveThatDrivesIt_AndDropsItOnASwitchToPalace()
    {
        var setup = Scattering();
        setup.PlaneWave = setup.PlaneWave! with { Name = "Nose on" };
        var vm = new CircuitRF.Ui.Layout.Em.EmSetupEditorViewModel("x.c3d", setup, embedded: true);
        Assert.True(vm.IsPlaneWaveSetup);
        Assert.Contains("'Nose on'", vm.ExcitationNote);
        vm.Solver3DChoice = CircuitRF.Ui.Layout.Em.EmSetupEditorViewModel.Solver3DChoices.Single(c => c.Value == Em3dSolverId.Palace);
        Assert.False(vm.IsPlaneWaveSetup);
        Assert.Null(vm.ExcitationNote);
    }

    public static TheoryData<double, double, double, double, double, double> Waves => new()
    {
        { 0, 0, 1, 1, 0, 0 },          // along +z, E along x (Hertzon's sphere examples)
        { 0, 0, -1, 0, 1, 0 },         // along −z
        { 1, 0, 0, 0, 0, 1 },          // along +x, E along z
        { 0.75, 0.43301270189221924, 0.5, 0.010313169241199738, 0.7481815100061638, -0.6634139481689384 },   // θ 60°, φ 30°, ψ 40°
        { -0.3, 0.2, -0.9, 0.2, 0.9, 0 },
    };

    /// <summary>R-hzn12-2 — the angles handed to EMerge reproduce the wave's own vectors through EMerge's spherical formula.</summary>
    [Theory]
    [MemberData(nameof(Waves))]
    public void EmergesAngles_ReproduceTheVectors(double kx, double ky, double kz, double ex, double ey, double ez)
    {
        var wave = Em3dPlaneWave.From(new(kx, ky, kz), Perpendicular(new(kx, ky, kz), new(ex, ey, ez)), 1, default, out _)!;
        var (theta, phi, psi) = Em3dPlaneWaves.SphAngles(wave);
        var (k, e) = Em3dPlaneWaves.FromSphAngles(theta, phi, psi);
        Near(wave.Direction, k);
        Near(wave.EField, e);
    }

    /// <summary>R-hzn12-2 — the same, through EMerge's own background field: its k and E at the origin for the angles circuitRF gives
    /// it are the wave's vectors. Imports EMerge; solves nothing.</summary>
    [EmergeFact]
    public void EmergesOwnBackgroundField_IsTheWaveAskedFor()
    {
        var python = SolverDiscovery.ReadinessFor(EmergeSolver.Id).First(r => r.Proceeds).Installation!.Path;
        foreach (var row in Waves)
        {
            var v = row.Select(o => (double)o!).ToArray();
            var wave = Em3dPlaneWave.From(new(v[0], v[1], v[2]), Perpendicular(new(v[0], v[1], v[2]), new(v[3], v[4], v[5])), 1, default, out _)!;
            var (theta, phi, psi) = Em3dPlaneWaves.SphAngles(wave);
            static string R(double x) => x.ToString("R", CultureInfo.InvariantCulture);
            string script =
                "from emerge._emerge.physics.microwave.bcs.background_field import BackgroundField as B\n" +
                $"import numpy as np\nf = B(1.0, {R(theta)}, {R(phi)}, {R(psi)}, (0,0,0), 1.0, 'SPH')\n" +
                "z = np.zeros(1)\nk = np.real(f.k(z, z, z)).ravel(); e = np.real(f.E(z, z, z)).ravel()\n" +
                "print(' '.join(repr(float(x)) for x in list(k) + list(e)))\n";
            var psi_ = new ProcessStartInfo(python, ["-I", "-c", script]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi_)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            var n = output.Trim().Split('\n')[^1].Split(' ').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            Near(wave.Direction, new Point3(n[0], n[1], n[2]));
            Near(wave.EField, new Point3(n[3], n[4], n[5]));
        }
    }

    /// <summary>R-hzn12-4 — σ = 4π |E_far|² / E0², in m² and dBsm, on the grid and at the monostatic direction; the result is one group
    /// with no gain, directivity or efficiency, and it carries the wave it was solved for.</summary>
    [Fact]
    public void TheResult_IsTheCrossSectionAlone_AndCarriesItsWave()
    {
        var wave = new Em3dPlaneWave(new(0, 0, 1), new(1, 0, 0), 2, default);
        double[] f = [1e9], theta = [0, 90, 180], phi = [0, 90];
        var far = Enumerable.Repeat(new Complex(1, 0), 2 * 3 * 2).ToArray();   // |Eθ| = |Eφ| = 1 V everywhere
        var mono = new[] { new Complex(0, 2), Complex.Zero };
        var data = Em3dScatteringResult.Build(f, theta, phi, far, mono, wave);

        Assert.Equal([Em3dScatteringResult.Group], data.Groups.ToArray());
        string G(string c) => $"{Em3dScatteringResult.Group}.{c}";
        Assert.Equal(4 * Math.PI * 2 / 4, data[G(Em3dScatteringResult.BistaticCube)].RealValues[0], 12);
        Assert.Equal(4 * Math.PI, data[G(Em3dScatteringResult.MonostaticCube)].RealValues[0], 12);
        Assert.Equal(10 * Math.Log10(4 * Math.PI), data[G(Em3dScatteringResult.MonostaticDbsmCube)].RealValues[0], 12);
        Assert.Equal("dBsm", data[G(Em3dScatteringResult.BistaticDbsmCube)].Unit);
        Assert.DoesNotContain(data.CubesIn(Em3dScatteringResult.Group).Keys,
                              k => k.Contains("Gain", StringComparison.OrdinalIgnoreCase) || k.Contains("Directivity", StringComparison.OrdinalIgnoreCase) ||
                                   k.Contains("Efficiency", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, data[G(Em3dScatteringResult.E0Cube)].RealValues[0]);
        Assert.Equal([0, 0, 1], data[G(Em3dScatteringResult.DirectionCube)].RealValues);
    }

    private static Em3dProblem Sphere(double padM)
    {
        var faces = new Em3dFaces(Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing,
                                  Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing);
        double r = 0.035, b = r + padM;
        return new Em3dProblem(
            [new Em3dSolid("ball", "PEC", Em3dRole.Conductor, new Em3dSphere(default, r), 0)], [],
            [new Em3dMaterial("Air", 1, null, 0, 1, 0), new Em3dMaterial("PEC", 1, null, 0, 1, double.PositiveInfinity)], [],
            new Em3dAirBox(new(-b, -b, -b), new(b, b, b), faces) { Material = "Air" }, new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), 25)
        {
            PlaneWave = new Em3dPlaneWave(new(0, 0, 1), new(1, 0, 0), 1, default),
        };
    }

    /// <summary>R-hzn12-2 — EMerge's input carries the wave in its angles beside the vectors, the grid, and the integration box halfway
    /// between the model and the air box; a run with no port is not refused for having none.</summary>
    [Fact]
    public void TheLowering_CarriesTheWave_AndTheIntegrationBoxHalfwayToTheAirBox()
    {
        var lowered = EmergeScriptWriter.Write(Sphere(0.1), EmergeSettings.Default);
        Assert.True(lowered.Ok, lowered.Refusal);
        using var json = JsonDocument.Parse(lowered.ProblemJson!);
        var w = json.RootElement.GetProperty("planeWave");
        Assert.Equal(0, w.GetProperty("theta").GetDouble());
        Assert.Equal(2, w.GetProperty("thetaStepDeg").GetDouble());
        Assert.Equal(0.035 + 0.05, w.GetProperty("surface").GetProperty("max")[0].GetDouble(), 12);
    }

    /// <summary>R-hzn12-2 — a model that reaches the air box leaves no closed surface in the air to read the far field on: refused,
    /// naming the face.</summary>
    [Fact]
    public void AModelReachingTheAirBox_IsRefusedForWantOfASurface()
    {
        var lowered = EmergeScriptWriter.Write(Sphere(0), EmergeSettings.Default);
        Assert.False(lowered.Ok);
        Assert.Contains("reaches the air box's xmin face", lowered.Refusal);
    }

    private static Point3 Perpendicular(Point3 k, Point3 e)
    {
        double kk = k.X * k.X + k.Y * k.Y + k.Z * k.Z, ke = (k.X * e.X + k.Y * e.Y + k.Z * e.Z) / kk;
        return new(e.X - ke * k.X, e.Y - ke * k.Y, e.Z - ke * k.Z);
    }

    private static void Near(Point3 want, Point3 got)
    {
        Assert.Equal(want.X, got.X, 9);
        Assert.Equal(want.Y, got.Y, 9);
        Assert.Equal(want.Z, got.Z, 9);
    }
}

using System.Numerics;
using System.Text;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using CircuitRF.Render.Scene3D.Fields;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-emi-7 §5 — EMerge's fields and radiation pattern. Nothing here needs EMerge but the one [EmergeFact]:
//    1  a step in the driver's layout (written here, no Python) reads through VtuReader and FieldRun unchanged, its drive from
//       the run's own port voltages; the lowering carries the request
//    2  with EMerge: the Cavity's Modes run writes mode 1's field, `render --field` draws it, and its peak is mid-cavity
//    3  participation and each port's external Q, from canned per-material energies and port voltages
//    4  the radiation DataSet is the group Palace's path publishes, each pattern referred to 1 V across its port
//    5  brief-emi-11 — J_s: a driver-shaped boundary step pairs with its volume step and offers J_s on the conductor its
//       groups name (no Python); the driver's n × [H] orientation on a canned mesh (EMerge's Python); with EMerge, the
//       Cavity's strip carries J_s, rendered, peaking on its edge; with Palace too, the two peaks against their S agreement
// ══════════════════════════════════════════════════════════════════════════════════════════════

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class EmergeFieldsTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-emerge-fields-" + Guid.NewGuid().ToString("N")[..12]);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ── 1. The layout ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADriverShapedStep_ReadsThroughVtuReaderUnchanged_WithItsDriveFromTheRunsOwnVoltages()
    {
        // One second-order tetrahedron, its ten nodes in VTK's order, E = (k, 2k, −k) + j(0, k, 0) at node k.
        double[][] corners = [[0, 0, 0], [1e-3, 0, 0], [0, 1e-3, 0], [0, 0, 1e-3]];
        var nodes = corners.Concat(new[] { (0, 1), (1, 2), (0, 2), (0, 3), (1, 3), (2, 3) }
                           .Select(e => corners[e.Item1].Zip(corners[e.Item2], (a, b) => (a + b) / 2).ToArray())).ToArray();
        var e = Enumerable.Range(0, 10).Select(k => new Complex[] { k, new(2 * k, k), -k }).ToArray();
        var b = Enumerable.Range(0, 10).Select(k => new Complex[] { 1e-9 * k, 0, 0 }).ToArray();
        WriteDriverStep(Path.Combine(_root, "postpro", "paraview", "driven", "excitation_1", "Cycle000001"), nodes, e, b, attribute: 2);
        File.WriteAllText(Path.Combine(_root, EmergeResults.RunFile), """
            {"fieldSteps": [{"port": 1, "frequencyHz": 9955000000.0, "scale": 0.7071, "v": [3.0, 1.0], "vinc": [5.0, 0.0],
              "pvtu": "postpro/paraview/driven/excitation_1/Cycle000001/data.pvtu"}], "fieldBytes": 1}
            """);
        File.WriteAllText(Path.Combine(_root, EmergeScriptWriter.ProblemFile), """{"ports": [{"number": 1, "z0": 50}]}""");
        EmergeFields.WriteCollections(_root, EmergeFields.ReadSteps(_root), null);

        var run = FieldRun.OpenEmerge(_root)!;
        Assert.Equal(("EMerge", 1.0, FieldProblemKind.Driven), (run.Solver, run.ToMetres, run.Kind));
        var s = Assert.Single(run.Solutions);
        Assert.Equal((1, 9.955e9), (s.Excitation, s.FrequencyHz));
        Assert.Equal(new Complex(-2, 1) / 5, s.Drive!.Reflection);           // (V − V_inc)/V_inc of the step's own solve
        Assert.Equal(new Complex(50, 0), s.Drive.Z0);

        var step = FieldStep.Open(s.VolumePvtu!, run.ToMetres);
        Assert.Equal((10, 1, 2), (step.Mesh.NodeCount, step.Mesh.CellCount, step.Mesh.Order));
        Assert.Equal(2, step.Mesh.Attribute[0]);
        Assert.Equal(["B", "E"], step.Arrays.Select(a => a.Name).Order());
        var loaded = step.Load("E")!;
        for (int k = 0; k < 10; k++)
            for (int c = 0; c < 3; c++)
            {
                Assert.Equal((float)e[k][c].Real, loaded.Re[3 * k + c]);
                Assert.Equal((float)e[k][c].Imaginary, loaded.Im![3 * k + c]);
                Assert.Equal((float)nodes[k][c], step.Mesh.Points[3 * k + c]);
            }
    }

    [Fact]
    public void TheRequest_SavesTheSweepCentreByDefault_NoFileForAnEmptyList_AndTheLoweringCarriesIt()
    {
        var (problem, setup) = Cavity("Driven");
        var request = EmergeFields.Request(setup, problem)!;
        Assert.Equal([10e9], request.FrequenciesHz);                         // 8–12 GHz: the centre, as Palace's default
        Assert.True(request.Write);
        string json = EmergeScriptWriter.Write(problem, EmergeSettings.Resolve(setup.Emerge), request).ProblemJson!;
        var block = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("fields");
        Assert.True(block.GetProperty("write").GetBoolean());
        Assert.Equal([10e9], block.GetProperty("frequenciesHz").EnumerateArray().Select(x => x.GetDouble()));
        Assert.Equal(PalaceDrive.IncidentPowerW, block.GetProperty("incidentPowerW").GetDouble());
        Assert.False(block.TryGetProperty("farField", out _));
        // The goldens' input: no request, no block.
        Assert.DoesNotContain("\"fields\"", EmergeScriptWriter.Write(problem, EmergeSettings.Resolve(setup.Emerge)).ProblemJson!, StringComparison.Ordinal);

        setup.Emerge = new CemEmerge { SaveFieldsGHz = [] };
        Assert.Null(EmergeFields.Request(setup, problem));
        // The cavity's walls are conducting: no far field, and the run is told why.
        setup.RadiationPattern = true;
        Assert.Contains("xmin face is Pec", EmergeScriptWriter.RadiationPatternNote(setup, problem), StringComparison.Ordinal);
    }

    // ── 2. With EMerge ─────────────────────────────────────────────────────────────────────────

    /// <summary>The Cavity's Modes setup on EMerge at Draft: mode 1's field is written, read back through the field picker and
    /// drawn by <c>render --field</c>, and its |E| peaks in the middle of the cavity, as Palace's TE-like mode does. One mode,
    /// searched from 9.9 GHz: from the setup's 8 GHz EMerge's first solution is a 7.8 GHz non-resonance (Q ≈ 1.5), and its
    /// lumped ports are assembled at the search frequency (src/Design/RESOLVED.md, EMI-7). ~2.5 min: one-core eigen solve.</summary>
    [EmergeFact]
    [Trait("Category", "Benchmark")]
    public void TheCavityModes_OnEMerge_WriteMode1sField_RenderDrawsIt_AndItsPeakIsMidCavity()
    {
        string ws = Path.Combine(_root, "ws");
        EmergeRunTests.CopyDirectory(Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "3D Eigenmode"), ws);
        string path = Path.Combine(ws, "Cavity", "3d", "Cavity.c3d");
        // The Modes setup on EMerge at Draft, one mode: written into the document, so `render --field` reads the same run.
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        var modes = json["Setups"]!.AsArray().Single(s => (string?)s!["Name"] == "Modes")!;
        modes["Solver3D"] = "Emerge";
        modes["Emerge"] = new System.Text.Json.Nodes.JsonObject { ["Quality"] = "Draft" };
        modes["Eigenmode"]!["Count"] = 1;
        modes["Eigenmode"]!["TargetGHz"] = 9.9;
        File.WriteAllText(path, json.ToJsonString());
        var doc = C3dPersistence.LoadFromFile(path);
        var setup = C3dSetups.ForRun(C3dSetups.Read(doc).Single(s => s.Name == "Modes").Setup!, path);
        string results = Path.Combine(ws, "results");

        var run = EmRunService.RunThreeDView(setup, doc, path, Path.Combine(ws, ".cws"), results);
        foreach (string n in run.Notes ?? []) output.WriteLine("note: " + n);
        Assert.True(run.Status == EmRunStatus.Ok, run.Error);
        Assert.True(run.Data!.Contains(Em3dEigenResult.ParticipationCube));
        Assert.True(run.Data.Contains(Em3dEigenResult.UnloadedQCube));

        var found = FieldPlotResolver.Discover(setup, results, null);
        var mode1 = found.Items.First(i => i.Solution.Kind == FieldProblemKind.Eigenmode && i.Solution.Index == 0);
        output.WriteLine(mode1.Label);
        Assert.StartsWith("Mode 1: 9.9", mode1.Label, StringComparison.Ordinal);
        var step = FieldStep.Open(mode1.Solution.VolumePvtu!, mode1.Run.ToMetres);
        var eField = step.Load("E")!;
        int peak = Enumerable.Range(0, step.Mesh.NodeCount).MaxBy(k => Enumerable.Range(0, 3).Sum(c =>
            eField.Re[3 * k + c] * eField.Re[3 * k + c] + eField.Im![3 * k + c] * eField.Im[3 * k + c]));
        float[] p = step.Mesh.Points;
        var (xs, ys) = (Enumerable.Range(0, step.Mesh.NodeCount).Select(k => p[3 * k]).ToArray(),
                        Enumerable.Range(0, step.Mesh.NodeCount).Select(k => p[3 * k + 1]).ToArray());
        double fx = (p[3 * peak] - xs.Min()) / (xs.Max() - xs.Min()), fy = (p[3 * peak + 1] - ys.Min()) / (ys.Max() - ys.Min());
        output.WriteLine($"peak |E| at ({fx:F2}, {fy:F2}) of the box");
        Assert.InRange(fx, 0.3, 0.7);
        Assert.InRange(fy, 0.3, 0.7);

        string png = Path.Combine(_root, "mode.png");
        var (code, stdout, stderr) = EmergeRunTests.RunCli(ws, "render", path, "-o", png, "--field", "Cavity mode");
        output.WriteLine(stderr);
        Assert.True(code == 0, stdout + stderr);
        Assert.True(new FileInfo(png).Length > 0);
    }

    // ── 3. Eigenmode extras ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Participation_FromCannedEnergies_SumsToOne_AndQext_IsOmegaRTimesEnergyOverV2()
    {
        // modes.npy held three solutions; the run reports raw 2 then raw 0 (ascending frequency), and drops raw 1.
        var modes = new EmergeModes([new Em3dMode(1, 9e9, 500), new Em3dMode(2, 11e9, 400)], 1, 7.6e9, [2, 0]);
        var facts = new EmergeFieldFacts(
        [
            new EmergeFieldStep(null, double.NaN, 0, null, null, null, [1e-11, 3e-11, 0], [new Complex(2, 0)]),
            new EmergeFieldStep(null, double.NaN, 1, null, null, null, [9e-11, 0, 0], [new Complex(1, 0)]),
            new EmergeFieldStep(null, double.NaN, 2, null, null, null, [6e-11, 2e-11, 0], [new Complex(0, 4)]),
        ], 0, [1], [50], ["Air", "PTFE", "Perfect conductor"]);

        var (ports, qExt, domains, participation) = Em3dRunService.EmergeEigenExtras(modes, facts);
        Assert.Equal(["Air", "PTFE"], domains);                             // a material with no energy in any mode has no column
        for (int i = 0; i < 2; i++) Assert.Equal(1, participation![i, 0] + participation[i, 1], 12);
        Assert.Equal(0.75, participation![0, 0], 12);                       // mode 1 is raw 2: 6 of 8
        Assert.Equal(0.25, participation[1, 0], 12);                        // mode 2 is raw 0: 1 of 4
        Assert.Equal([1], ports);
        Assert.Equal(2 * Math.PI * 9e9 * 50 * 8e-11 / 16, qExt![0, 0], 9);
        Assert.Equal(2 * Math.PI * 11e9 * 50 * 4e-11 / 4, qExt[1, 0], 9);
    }

    // ── 4. The radiation pattern ───────────────────────────────────────────────────────────────

    [Fact]
    public void ThePattern_IsTheGroupPalacesPathPublishes_ReferredTo1VAcrossThePort()
    {
        Directory.CreateDirectory(_root);
        var grid = FarFieldStage.Sphere();
        int nt = grid.ThetaDeg.Count, np = grid.PhiDeg.Count;
        double f = 3e9;
        // A z-directed short dipole's r·E: E_θ = sin θ (at the step's own drive), E_φ = 0.
        var e = new Complex[2 * nt * np];
        for (int it = 0; it < nt; it++)
            for (int ip = 0; ip < np; ip++) e[it * np + ip] = Math.Sin(grid.ThetaDeg[it] * Math.PI / 180);
        WriteNpy(Path.Combine(_root, EmergeFields.FarFieldFile), e, [1, 1, 2, nt, np]);
        WriteNpy(Path.Combine(_root, EmergeFields.ThetaFile), [.. grid.ThetaDeg], [nt]);
        WriteNpy(Path.Combine(_root, EmergeFields.PhiFile), [.. grid.PhiDeg], [np]);
        WriteNpy(Path.Combine(_root, EmergeFields.FrequencyFile), [f], [1]);
        Complex v = new(2, 0), vi = new(4, 0);
        var facts = new EmergeFieldFacts([new EmergeFieldStep(1, f, null, null, v, vi, null, null)], 0, [], [], []);
        var ports = OpenEmsRadiationTests.Dipole(0.01, 1e-3, 1e-3, 0.025, 2.5e9, 3.5e9, 3).Ports;
        double r = ports[0].Z0.Real;

        var data = new DataSet();
        Assert.Null(EmergeFields.PublishPattern(_root, facts, ports, new EmSetup(), data, []));

        // Palace's path publishes through the same stage: the same points, built from 1 V across the port, give the same group.
        var eth = new Complex[nt * np];
        for (int it = 0; it < nt; it++)
            for (int ip = 0; ip < np; ip++) eth[grid.IndexOf(it, ip)] = e[it * np + ip] / v;
        double accepted = (16 - 4) / (2 * r * 4.0);
        var palace = new DataSet();
        Em3dRadiation.Publish(palace, "Palace (FEM)", grid, [1], [f],
                              [new Em3dPatternPoint(FarFieldStage.Pattern(grid, eth, new Complex[nt * np], 1, f), accepted, (v - vi) / vi)],
                              new EmSetup().ReferenceInputPowerDbm, hemisphere: false);
        Assert.Equal(palace.Groups.Order(), data.Groups.Order());
        foreach (string g in palace.Groups)
        {
            var mine = data.CubesIn(g);
            foreach (var (name, cube) in palace.CubesIn(g))
            {
                Assert.True(mine.ContainsKey(name), $"{g}/{name}");
                Assert.Equal(cube.Axes.Select(a => (a.Name, a.Values.Length)), mine[name].Axes.Select(a => (a.Name, a.Values.Length)));
            }
        }
        var directivity = data.CubesIn(PlanarFarField.Group)["DirectivityDbi"].RealValues;
        Assert.Equal(10 * Math.Log10(1.5), directivity[0], 2);              // a sin θ pattern is a short dipole's, 1.76 dBi
        Assert.Equal(accepted, data.CubesIn(PlanarFarField.Group)["PowerAccepted"].RealValues[0], 12);
    }

    /// <summary>brief-cosim-pattern-3 — with its S in the result, a lumped port's pattern is referred to the voltage S states, not the driver's reading of it.</summary>
    [Fact]
    public void ALumpedPortsPattern_IsReferredToTheVoltageItsSStates_AndTheResultIsMarked()
    {
        Directory.CreateDirectory(_root);
        const double f = 3e9;
        var grid = FarFieldStage.Sphere(EmergeFields.FarFieldStepDeg, EmergeFields.FarFieldStepDeg);
        int nt = grid.ThetaDeg.Count, np = grid.PhiDeg.Count;
        var e = new Complex[nt * np * 2];
        for (int it = 0; it < nt; it++)
            for (int ip = 0; ip < np; ip++) e[it * np + ip] = Math.Sin(grid.ThetaDeg[it] * Math.PI / 180);
        WriteNpy(Path.Combine(_root, EmergeFields.FarFieldFile), e, [1, 1, 2, nt, np]);
        WriteNpy(Path.Combine(_root, EmergeFields.ThetaFile), [.. grid.ThetaDeg], [nt]);
        WriteNpy(Path.Combine(_root, EmergeFields.PhiFile), [.. grid.PhiDeg], [np]);
        WriteNpy(Path.Combine(_root, EmergeFields.FrequencyFile), [f], [1]);
        Complex misread = new(2, 0), vi = new(4, 0), s11 = new(0.25, 0.5);
        var facts = new EmergeFieldFacts([new EmergeFieldStep(1, f, null, null, misread, vi, null, null)], 0, [], [], []);
        var ports = OpenEmsRadiationTests.Dipole(0.01, 1e-3, 1e-3, 0.025, 2.5e9, 3.5e9, 3).Ports;
        double r = ports[0].Z0.Real;

        var data = new DataSet();
        data.Add("S", new DataCube([new Axis("freq", [f], "Hz"), new Axis("i", [1.0], ""), new Axis("j", [1.0], "")], [s11]));
        Assert.Null(EmergeFields.PublishPattern(_root, facts, ports, new EmSetup(), data, []));

        var far = data.CubesIn(PlanarFarField.Group);
        double want = (1 - s11.Magnitude * s11.Magnitude) / (2 * r * (1 + s11).Magnitude * (1 + s11).Magnitude);
        Assert.Equal(want, far["PowerAccepted"].RealValues[0], 12);
        Assert.True(far.ContainsKey(Em3dRadiation.PortsTerminatedCube));
    }

    // ── 5. brief-emi-11: J_s on the conductors and sheets ─────────────────────────────────────

    /// <summary>A driver-shaped boundary step (two triangles of one conductor, a known J_s) beside its volume step, driven and
    /// eigenmode: the collections pair the two by timestep, J_s and Q_s are offered on the surfaces, and groups.json's
    /// conductor face group is what the surface plot draws. The layout and the groups, not EMerge.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ABoundaryStep_IsPairedWithItsVolumeStep_AndOffersJs_OnTheConductorItsGroupsName(bool driven)
    {
        double[][] corners = [[0, 0, 0], [1e-3, 0, 0], [0, 1e-3, 0], [0, 0, 1e-3]];
        var zero = Enumerable.Range(0, 4).Select(_ => new Complex[3]).ToArray();
        string vol = driven ? "postpro/paraview/driven/excitation_1/Cycle000001" : "postpro/paraview/eigenmode/Cycle000001";
        string bnd = driven ? "postpro/paraview/driven_boundary/excitation_1/Cycle000001" : "postpro/paraview/eigenmode_boundary/Cycle000001";
        WriteDriverStep(Path.Combine(_root, vol), corners, zero, zero, attribute: 1);
        // Two first-order triangles of conductor 'pad' (attribute 2): J_s = (0, k, 0) + j(k, 0, 0) at node k, Q_s = k.
        double[][] tris = [[0, 0, 0], [1e-3, 0, 0], [0, 1e-3, 0], [1e-3, 0, 0], [1e-3, 1e-3, 0], [0, 1e-3, 0]];
        var k6 = Enumerable.Range(0, 6).Select(k => (double)k).ToArray();
        WriteDriverPiece(Path.Combine(_root, bnd), tris, 69, 3, [2, 2],
            [("J_s_imag", 3, [.. k6.SelectMany(k => new[] { k, 0, 0 })]), ("J_s_real", 3, [.. k6.SelectMany(k => new[] { 0, k, 0 })]),
             ("Q_s_imag", 1, new double[6]), ("Q_s_real", 1, k6)]);
        string step = driven ? "\"port\": 1, \"frequencyHz\": 1e10, \"v\": [1.0, 0.0], \"vinc\": [1.0, 0.0]" : "\"mode\": 0";
        File.WriteAllText(Path.Combine(_root, EmergeResults.RunFile),
            $$"""{"fieldSteps": [{{{step}}, "pvtu": "{{vol}}/data.pvtu", "boundaryPvtu": "{{bnd}}/data.pvtu"}], "fieldBytes": 1}""");
        File.WriteAllText(Path.Combine(_root, EmergeScriptWriter.ProblemFile), """{"ports": [{"number": 1, "z0": 50}]}""");
        File.WriteAllText(Path.Combine(_root, GmshGeoWriter.GroupsFile), """
            {"Groups": [{"Attribute": 1, "Name": "background", "Dimension": 3, "Kind": "Background", "Material": "Air"},
                        {"Attribute": 2, "Name": "pad", "Dimension": 2, "Kind": "Conductor", "Material": "Copper"}]}
            """);
        var facts = EmergeFields.ReadSteps(_root);
        Assert.Equal($"{bnd}/data.pvtu", Assert.Single(facts.Steps).BoundaryPvtu);
        EmergeFields.WriteCollections(_root, facts, driven ? null : [0]);

        var run = FieldRun.OpenEmerge(_root)!;
        var s = Assert.Single(run.Solutions);
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, bnd, "data.pvtu")), Path.GetFullPath(s.BoundaryPvtu!));
        var volume = FieldStep.Open(s.VolumePvtu!, run.ToMetres);
        var boundary = FieldStep.Open(s.BoundaryPvtu!, run.ToMetres);
        Assert.Equal((FieldCellShape.Triangle, 1, 2), (boundary.Mesh.Shape, boundary.Mesh.Order, boundary.Mesh.CellCount));
        var offered = FieldQuantity.Offered(volume.Arrays, boundary.Arrays);
        Assert.Contains(offered, q => q.Array.Name == "J_s" && q.OnBoundary && q.IsVector && q.Array.IsComplex);
        Assert.Contains(offered, q => q.Array.Name == "Q_s" && q.OnBoundary && !q.IsVector);
        var js = boundary.Load("J_s")!;
        Assert.Equal([0f, 5f, 0f], js.Re[15..18]);
        Assert.Equal([5f, 0f, 0f], js.Im![15..18]);

        var pad = Assert.Single(FieldGroups.Read(_root), g => g.Dimension == 2);
        Assert.Equal(new FieldGroup("pad", 2, 2, "Conductor"), pad);
        Assert.Equal(2, FieldSurfaces.Boundary(boundary.Mesh, js, new HashSet<int> { pad.Attribute }, (0, 0, 0)).TriangleCount);
    }

    /// <summary>The driver's own orientation, on a canned mesh with a canned H (EMerge's Python for numpy; no solve): one
    /// triangle at z = 0 wound against +z. Over a conductor below it, H = x̂ in the tetrahedron above gives n = +ẑ and
    /// J_s = ẑ × x̂ = ŷ; as a sheet of +n = ẑ with H₊ = x̂ above and H₋ = −x̂ below (the tetrahedron below listed first), 2ŷ.
    /// Q_s likewise, from a canned D = ±ẑ.
    /// Both rewind the triangle to face n; at order 2 its midpoints are VTK's (0,1) (1,2) (2,0).</summary>
    [EmergeFact]
    public void TheDriversJs_IsNCrossTheJumpInH_WithNIntoTheMeshOrAlongTheSheet()
    {
        string python = SolverDiscovery.ReadinessFor(EmergeSolver.Id).Single(r => r.Tool == SolverTool.Emerge).Installation!.Path;
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, EmergeScriptWriter.DriverFile), EmergeScriptWriter.Driver);
        const string script = """
            import json, sys, types
            import numpy as np
            sys.path.insert(0, sys.argv[1])
            import circuitrf_emerge as d
            nodes = np.array([[0, 0, 0], [1, 0, 0], [0, 1, 0], [0, 0, 1], [0, 0, -1]], dtype=float).T
            H = {0: [1, 0, 0], 1: [-1, 0, 0]}
            D = {0: [0, 0, 1], 1: [0, 0, -1]}
            def run(tri_to_tet, surface, order):
                m = types.SimpleNamespace(nodes=nodes, tris=np.array([[0, 2, 1]]).T, tets=np.array([[0, 1, 2, 3], [0, 1, 2, 4]]).T,
                                          tri_to_tet=np.array(tri_to_tet), ftag_to_tri={7: [0]})
                m.get_triangles = lambda tags: [i for t in tags for i in m.ftag_to_tri[t]]
                b = d.boundary_nodes(m, {"names": ["background"], "surfaces": [dict(surface, tags=lambda: [7])]}, order)
                side = lambda cells, F=H: np.array([F[c] if c >= 0 else [0, 0, 0] for c in cells], dtype=float).T
                p = b["points"]
                return {"n": b["n"][:, 0].tolist(), "J": d.surface_current(b["n"], side(b["a"]), side(b["b"]))[:, 0].tolist(),
                        "wound": np.cross(p[:, 1] - p[:, 0], p[:, 2] - p[:, 0]).tolist(), "per": b["per"],
                        "attribute": int(b["attribute"][0]), "points": p.T.tolist(),
                        "Q": float(d.surface_charge(b["n"], side(b["a"], D), side(b["b"], D))[0])}
            print(json.dumps({
                "conductor": run([[0], [-1234]], {"kind": "Conductor", "name": "c", "material": 0, "normal": None}, 1),
                "sheet": run([[1], [0]], {"kind": "Sheet", "name": "s", "material": 0, "normal": [0, 0, 1]}, 2)}))
            """;
        var psi = new System.Diagnostics.ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-I", "-c", script, _root }) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr.Result);
        output.WriteLine(stdout);
        using var json = System.Text.Json.JsonDocument.Parse(stdout);
        double[] V(string c, string k) => [.. json.RootElement.GetProperty(c).GetProperty(k).EnumerateArray().Select(x => x.GetDouble())];

        Assert.Equal([0, 0, 1], V("conductor", "n"));
        Assert.Equal([0, 1, 0], V("conductor", "J"));
        Assert.True(V("conductor", "wound")[2] > 0);
        Assert.Equal(2, json.RootElement.GetProperty("conductor").GetProperty("attribute").GetInt32());   // after the one volume

        Assert.Equal([0, 0, 1], V("sheet", "n"));
        Assert.Equal([0, 2, 0], V("sheet", "J"));
        Assert.Equal(1, json.RootElement.GetProperty("conductor").GetProperty("Q").GetDouble());       // ẑ · D₊, D₊ = ẑ
        Assert.Equal(2, json.RootElement.GetProperty("sheet").GetProperty("Q").GetDouble());           // ẑ · (ẑ − (−ẑ))
        Assert.True(V("sheet", "wound")[2] > 0);
        var pts = json.RootElement.GetProperty("sheet").GetProperty("points").EnumerateArray()
                      .Select(r => r.EnumerateArray().Select(x => x.GetDouble()).ToArray()).ToArray();
        Assert.Equal(6, json.RootElement.GetProperty("sheet").GetProperty("per").GetInt32());
        foreach (var (node, (i, j)) in new[] { (3, (0, 1)), (4, (1, 2)), (5, (2, 0)) })
            for (int c = 0; c < 3; c++) Assert.Equal((pts[i][c] + pts[j][c]) / 2, pts[node][c], 12);
    }

    /// <summary>The Cavity's driven setup on EMerge at Draft, three frequencies (so the 10 GHz field is the sweep's own solve):
    /// the run writes a boundary step, <c>render --field</c> draws a J_s Surfaces plot, and the peak |J_s| on the strip lies on
    /// its outline, where current crowds.</summary>
    [EmergeFact]
    [Trait("Category", "Benchmark")]
    public void TheCavityDriven_OnEMerge_WritesJsOnTheStrip_RenderDrawsIt_AndItsPeakIsOnTheStripsEdge()
    {
        var (ws, path) = CavityWorkspace("Driven (EMerge)");
        var (setup, results, _) = RunCavity(ws, path, "Driven (EMerge)");
        var (peak, at) = PeakJs(setup, results);
        output.WriteLine($"peak |J_s| {peak:G4} A/m at ({at.X * 1e3:F3}, {at.Y * 1e3:F3}, {at.Z * 1e3:F3}) mm");
        // The strip is 21.6 mm × 0.78 mm: its outline is |y| = 0.39 mm or |x| = 10.8 mm.
        Assert.True(Math.Abs(Math.Abs(at.Y) - 0.39e-3) < 1e-6 || Math.Abs(Math.Abs(at.X) - 10.8e-3) < 1e-6, $"peak at ({at.X}, {at.Y})");

        string png = Path.Combine(_root, "js.png");
        var (code, stdout, stderr) = EmergeRunTests.RunCli(ws, "render", path, "-o", png, "--field", "Strip current", "--view-dir", "top");
        output.WriteLine(stderr);
        Assert.True(code == 0, stdout + stderr);
        Assert.True(new FileInfo(png).Length > 0);
    }

    /// <summary>The Cavity's driven problem on Palace and on EMerge, the same ports and the 10 GHz field: the two peak |J_s| on
    /// the strip agree within the two solvers' S-parameter agreement on that problem (the largest |ΔS| over the three
    /// frequencies). Both numbers are reported.</summary>
    [PalaceAndEmergeFact]
    [Trait("Category", "Benchmark")]
    public void TheCavityDriven_PeakJs_OnPalaceAndEMerge_AgreeWithinTheirSAgreement()
    {
        var (ws, path) = CavityWorkspace("Driven (EMerge)");
        var (emerge, emergeResults, se) = RunCavity(ws, path, "Driven (EMerge)");
        var (palace, palaceResults, sp) = RunCavity(ws, path, "Driven");
        var (je, _) = PeakJs(emerge, emergeResults);
        var (jp, _) = PeakJs(palace, palaceResults);
        var a = RfCore.TouchstoneIO.ReadFile(se);
        var b = RfCore.TouchstoneIO.ReadFile(sp);
        Assert.Equal(a.Frequencies, b.Frequencies);
        double ds = 0;
        for (int f = 0; f < a.Frequencies.Length; f++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++) ds = Math.Max(ds, (a[f][i, j] - b[f][i, j]).Magnitude);
        double dj = Math.Abs(je - jp) / jp;
        output.WriteLine($"peak |J_s|: EMerge {je:G5} A/m, Palace {jp:G5} A/m, relative difference {dj:P2}; largest |ΔS| {ds:G3}");
        Assert.True(dj <= ds, $"peak |J_s| differs by {dj:P2}, beyond the S agreement {ds:G3}");
    }

    /// <summary>The Cavity example in a workspace of its own, with a J_s Surfaces plot ("Strip current") reading
    /// <paramref name="setup"/>, and every driven setup cut to three frequencies.</summary>
    private (string Ws, string Path) CavityWorkspace(string setup)
    {
        string ws = Path.Combine(_root, "ws");
        if (!Directory.Exists(ws)) EmergeRunTests.CopyDirectory(Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "3D Eigenmode"), ws);
        string path = Path.Combine(ws, "Cavity", "3d", "Cavity.c3d");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var s in json["Setups"]!.AsArray().Where(s => s!["Problem3D"] is null)) s!["Frequency"]!["NumPoints"] = 3;
        json["FieldPlots"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
        {
            ["Name"] = "Strip current", ["Setup"] = setup, ["Quantity"] = "J_s", ["On"] = "Surfaces",
        });
        File.WriteAllText(path, json.ToJsonString());
        return (ws, path);
    }

    private static (EmSetup Setup, string Results, string Snp) RunCavity(string ws, string path, string name)
    {
        var doc = C3dPersistence.LoadFromFile(path);
        var setup = C3dSetups.ForRun(C3dSetups.Read(doc).Single(s => s.Name == name).Setup!, path);
        string results = Path.Combine(ws, "results");             // where render looks; each setup has its own folder under it
        var run = EmRunService.RunThreeDView(setup, doc, path, Path.Combine(ws, ".cws"), results);
        Assert.True(run.Status == EmRunStatus.Ok, run.Error);
        return (setup, results, run.SnpPath!);
    }

    /// <summary>The largest |J_s| on the strip's sheet group in the run's 10 GHz boundary step with port 1 driven, and where it is
    /// (metres).</summary>
    private static (double Peak, (double X, double Y, double Z) At) PeakJs(EmSetup setup, string results)
    {
        var found = FieldPlotResolver.Discover(setup, results, null);
        var item = found.Items.Single(i => i.Solution.Kind == FieldProblemKind.Driven && i.Solution.Excitation == 1 &&
                                                Math.Abs(i.Solution.FrequencyHz - 10e9) < 1);
        var step = FieldStep.Open(item.Solution.BoundaryPvtu!, item.Run.ToMetres);
        var strip = found.Groups.Where(g => g.Name == "line" && g.Kind == "Sheet").Select(g => g.Attribute).ToHashSet();
        Assert.NotEmpty(strip);
        var js = step.Load("J_s")!;
        var mesh = step.Mesh;
        double peak = -1;
        int best = -1;
        for (int c = 0; c < mesh.CellCount; c++)
        {
            if (!strip.Contains(mesh.Attribute[c])) continue;
            for (int l = 0; l < mesh.NodesPerCell; l++)
            {
                int k = mesh.Cells[c * mesh.NodesPerCell + l];
                double m = Math.Sqrt(Enumerable.Range(0, 3).Sum(d => js.Re[3 * k + d] * js.Re[3 * k + d] + js.Im![3 * k + d] * js.Im[3 * k + d]));
                if (m > peak) (peak, best) = (m, k);
            }
        }
        float[] p = mesh.Points;
        double u = item.Run.ToMetres;
        return (peak, (p[3 * best] * u, p[3 * best + 1] * u, p[3 * best + 2] * u));
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The Cavity example's setup <paramref name="name"/> on EMerge, and its built problem.</summary>
    private static (Em3dProblem Problem, EmSetup Setup) Cavity(string name)
    {
        string path = Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "3D Eigenmode", "Cavity", "3d", "Cavity.c3d");
        var doc = C3dPersistence.LoadFromFile(path);
        var setup = C3dSetups.ForRun(C3dSetups.Read(doc).Single(s => s.Name == name).Setup!, path);
        setup.Solver3D = EmergeSolver.Id;
        var assembled = C3dProblemAssembly.Assemble(setup, doc, path, null);
        Assert.True(assembled.Ok, assembled.Refusal);
        return (assembled.Problem!, setup);
    }

    /// <summary>One step as the driver writes it (circuitrf_emerge.py write_step): inline-base64 geometry with a UInt32 count
    /// encoded on its own, the field arrays appended raw, B and E as _real/_imag pairs, the attribute per cell.</summary>
    private static void WriteDriverStep(string dir, double[][] nodes, Complex[][] e, Complex[][] b, int attribute)
        => WriteDriverPiece(dir, nodes, 71, nodes.Length, [attribute],
           [("B_imag", 3, [.. b.SelectMany(x => x.Select(c => c.Imaginary))]), ("B_real", 3, [.. b.SelectMany(x => x.Select(c => c.Real))]),
            ("E_imag", 3, [.. e.SelectMany(x => x.Select(c => c.Imaginary))]), ("E_real", 3, [.. e.SelectMany(x => x.Select(c => c.Real))])]);

    /// <summary>write_step's piece: cells of one VTK type with <paramref name="per"/> nodes each, every cell its own nodes in
    /// order, each array (point data) of its own component count.</summary>
    private static void WriteDriverPiece(string dir, double[][] nodes, byte type, int per, int[] attributes,
                                         (string Name, int Components, double[] Values)[] values)
    {
        Directory.CreateDirectory(dir);
        static string Inline(byte[] data) => Convert.ToBase64String(BitConverter.GetBytes((uint)data.Length)) + Convert.ToBase64String(data);
        static byte[] Floats(IEnumerable<double> v) => [.. v.SelectMany(x => BitConverter.GetBytes((float)x))];
        static byte[] Ints(IEnumerable<int> v) => [.. v.SelectMany(BitConverter.GetBytes)];
        int n = nodes.Length, cells = attributes.Length;
        var arrays = values.Select(v => (v.Name, v.Components, Data: Floats(v.Values))).ToArray();
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\"?>\n<VTKFile type=\"UnstructuredGrid\" version=\"2.2\" byte_order=\"LittleEndian\" header_type=\"UInt32\">\n")
           .Append($"<UnstructuredGrid>\n<Piece NumberOfPoints=\"{n}\" NumberOfCells=\"{cells}\">\n<Points>\n")
           .Append($"<DataArray type=\"Float32\" NumberOfComponents=\"3\" format=\"binary\">{Inline(Floats(nodes.SelectMany(x => x)))}</DataArray>\n</Points>\n<Cells>\n")
           .Append($"<DataArray type=\"Int32\" Name=\"connectivity\" NumberOfComponents=\"1\" format=\"binary\">{Inline(Ints(Enumerable.Range(0, n)))}</DataArray>\n")
           .Append($"<DataArray type=\"Int32\" Name=\"offsets\" NumberOfComponents=\"1\" format=\"binary\">{Inline(Ints(Enumerable.Range(1, cells).Select(c => c * per)))}</DataArray>\n")
           .Append($"<DataArray type=\"UInt8\" Name=\"types\" NumberOfComponents=\"1\" format=\"binary\">{Inline([.. Enumerable.Repeat(type, cells)])}</DataArray>\n</Cells>\n<PointData>\n");
        long offset = 0;
        foreach (var (name, comps, data) in arrays)
        {
            xml.Append($"<DataArray type=\"Float32\" Name=\"{name}\" NumberOfComponents=\"{comps}\" format=\"appended\" offset=\"{offset}\"/>\n");
            offset += 4 + data.Length;
        }
        xml.Append("</PointData>\n<CellData>\n")
           .Append($"<DataArray type=\"Int32\" Name=\"attribute\" NumberOfComponents=\"1\" format=\"binary\">{Inline(Ints(attributes))}</DataArray>\n")
           .Append("</CellData>\n</Piece>\n</UnstructuredGrid>\n<AppendedData encoding=\"raw\">\n_");
        using (var f = File.Create(Path.Combine(dir, "proc000000.vtu")))
        {
            f.Write(Encoding.ASCII.GetBytes(xml.ToString()));
            foreach (var (_, _, data) in arrays) { f.Write(BitConverter.GetBytes((uint)data.Length)); f.Write(data); }
            f.Write(Encoding.ASCII.GetBytes("\n</AppendedData>\n</VTKFile>\n"));
        }
        string pdata = string.Join("\n", arrays.Select(a => $"<PDataArray type=\"Float32\" Name=\"{a.Name}\" NumberOfComponents=\"{a.Components}\" format=\"appended\"/>"));
        File.WriteAllText(Path.Combine(dir, "data.pvtu"),
            "<?xml version=\"1.0\"?>\n<VTKFile type=\"PUnstructuredGrid\" version=\"2.2\" byte_order=\"LittleEndian\">\n<PUnstructuredGrid GhostLevel=\"0\">\n" +
            "<PPoints>\n<PDataArray type=\"Float32\" Name=\"Points\" NumberOfComponents=\"3\" format=\"binary\"/>\n</PPoints>\n" +
            "<PCells>\n<PDataArray type=\"Int32\" Name=\"connectivity\" NumberOfComponents=\"1\" format=\"binary\"/>\n" +
            "<PDataArray type=\"Int32\" Name=\"offsets\" NumberOfComponents=\"1\" format=\"binary\"/>\n" +
            "<PDataArray type=\"UInt8\" Name=\"types\" NumberOfComponents=\"1\" format=\"binary\"/>\n</PCells>\n" +
            $"<PPointData>\n{pdata}\n</PPointData>\n" +
            "<PCellData>\n<PDataArray type=\"Int32\" Name=\"attribute\" NumberOfComponents=\"1\" format=\"binary\"/>\n</PCellData>\n" +
            "<Piece Source=\"proc000000.vtu\"/>\n</PUnstructuredGrid>\n</VTKFile>\n");
    }

    /// <summary>A plain NumPy array as <c>np.save</c> writes one: version 1, C order, little-endian.</summary>
    private static void WriteNpy(string path, Complex[] values, int[] shape)
        => WriteNpy(path, "<c16", shape, [.. values.SelectMany(c => BitConverter.GetBytes(c.Real).Concat(BitConverter.GetBytes(c.Imaginary)))]);

    private static void WriteNpy(string path, double[] values, int[] shape)
        => WriteNpy(path, "<f8", shape, [.. values.SelectMany(BitConverter.GetBytes)]);

    private static void WriteNpy(string path, string descr, int[] shape, byte[] data)
    {
        string dims = shape.Length == 1 ? $"{shape[0]}," : string.Join(", ", shape);
        string header = $"{{'descr': '{descr}', 'fortran_order': False, 'shape': ({dims}), }}";
        header = header.PadRight((header.Length + 10 + 64) / 64 * 64 - 10 - 1) + "\n";
        using var f = File.Create(path);
        f.Write([0x93, .. "NUMPY"u8, 1, 0]);
        f.Write(BitConverter.GetBytes((ushort)header.Length));
        f.Write(Encoding.ASCII.GetBytes(header));
        f.Write(data);
    }
}

/// <summary>A fact that needs both Palace (with Gmsh) and an EMerge, naming what is missing otherwise.</summary>
public sealed class PalaceAndEmergeFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Reason = new(() =>
        new[] { Em3dSolverId.Palace, EmergeSolver.Id }.SelectMany(s => SolverDiscovery.ReadinessFor(s)).FirstOrDefault(r => !r.Proceeds) is { } r
            ? $"needs Palace, Gmsh and EMerge: {r.Refusal}" : null);

    public PalaceAndEmergeFactAttribute()
    {
        if (Reason.Value is { } why) Skip = why;
    }
}

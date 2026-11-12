using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-emi-4 §5 — the lowering: an Em3dProblem as EMerge's input. None of these needs EMerge; the driver test runs
//  only when an EMerge is found (CIRCUITRF_EMERGE may name one) and reports Skipped otherwise.
//
//  Goldens: testdata/em3d/emerge-goldens/<case>/problem.json. To rewrite them after a DELIBERATE writer change, run the
//  golden tests with CRF_WRITE_EMERGE_GOLDENS=1 and review the diff; nothing rewrites them otherwise.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class EmergeLoweringTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-emerge-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ── 1. Goldens ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The cases: an example's 3D view and one of its setups, run on EMerge. The Cavity's driven setup, the
    /// Cavity with post's eigenmode one, and the connector launch (kernel solids, a layout instance, absorbing faces).</summary>
    private static readonly (string Case, string Path, string Setup)[] Cases =
    [
        ("cavity", "examples/3D Eigenmode/Cavity/3d/Cavity.c3d", "Driven"),
        ("cavity-with-post", "examples/3D Eigenmode/Cavity with post/3d/Cavity with post.c3d", "Modes"),
        ("connector-launch", "examples/3D Connector/Launch/3d/Launch.c3d", "Palace"),
        // brief-emw-2 — the 3D Wave Ports Launch: wave port P1 on the xmin face, lumped port P2 on the board
        ("wave-launch", "examples/3D Wave Ports/Launch/3d/Launch.c3d", "Palace"),
        // brief-emw-3 — the 3D Wave Ports Coupled Microstrip: two terminal groups, one entry each with its `terminals`
        ("coupled-microstrip", "examples/3D Wave Ports/Coupled Microstrip/3d/Coupled Microstrip.c3d", "Palace"),
    ];

    [Theory]
    [InlineData("cavity")]
    [InlineData("cavity-with-post")]
    public void Golden_TheCavities_LowerByteForByte(string name) => Golden(name);

    /// <summary>The launch's housing and pin are kernel solids: their B-rep files are named by the bytes the geometry kernel
    /// wrote, which this machine's kernel decides, so the comparison reads both names as placeholders and checks instead that
    /// each file the input names is the one the lowering carries.</summary>
    [KernelFact]
    public void Golden_TheConnectorLaunch_LowersByteForByte_ApartFromItsKernelFilesNames() => Golden("connector-launch");

    /// <summary>brief-emw-2 R-emw2-1 — the Launch's wave port lowers as a <c>wavePorts</c> entry, its lumped port stays in
    /// <c>ports</c>. Its flange is a kernel solid, as the connector launch's housing is.</summary>
    [KernelFact]
    public void Golden_TheWavePortLaunch_LowersByteForByte() => Golden("wave-launch");

    /// <summary>brief-emw-3 R-emw3-3 — a terminal group lowers as ONE <c>wavePorts</c> entry, its leader's, named by the port's
    /// label, with a <c>terminals</c> list (number, name, path) in terminal order; its face holds two materials, so no
    /// presolve shift.</summary>
    [Fact]
    public void Golden_TheCoupledMicrostrip_LowersByteForByte() => Golden("coupled-microstrip");

    private void Golden(string name)
    {
        var low = Lowered(name, out _);
        Assert.True(low.Ok, low.Refusal);
        string json = low.ProblemJson!;
        Assert.Equal(json, Lowered(name, out _).ProblemJson);
        Assert.DoesNotContain('\r', json);

        foreach (var f in low.Files)
        {
            Assert.Contains(EmergeScriptWriter.PyStr(f.FileName), json, StringComparison.Ordinal);
            Assert.Contains(EmergeScriptWriter.PyStr(f.Sha256), json, StringComparison.Ordinal);
        }
        string normalised = Regex.Replace(Regex.Replace(json, "kernel-[0-9a-f]{16}\\.brep", "kernel-HASH.brep"), "\"[0-9a-f]{64}\"", "\"SHA256\"");

        string path = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "emerge-goldens", name, EmergeScriptWriter.ProblemFile);
        if (Environment.GetEnvironmentVariable("CRF_WRITE_EMERGE_GOLDENS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(normalised));
        }
        Assert.Equal(File.ReadAllText(path), normalised);
    }

    /// <summary>One case's problem, lowered for EMerge at its setup's own EMerge settings.</summary>
    private static EmergeLowering Lowered(string name, out Em3dProblem problem)
    {
        var (_, rel, setupName) = Cases.Single(c => c.Case == name);
        string path = Path.Combine(PalaceBackendTests.RepoRoot(), rel);
        var doc = C3dPersistence.LoadFromFile(path);
        var setup = C3dSetups.ForRun(C3dSetups.Read(doc).Single(s => s.Name == setupName).Setup!, path);
        setup.Solver3D = EmergeSolver.Id;
        var assembled = C3dProblemAssembly.Assemble(setup, doc, path, WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(path)));
        Assert.True(assembled.Ok, assembled.Refusal);
        problem = assembled.Problem!;
        return EmergeScriptWriter.Write(problem, EmergeSettings.Resolve(setup.Emerge));
    }

    // ── 2. PyStr, and the scans that hold R-emi4-0a ─────────────────────────────────────────────────

    [Theory]
    [InlineData("plain")]
    [InlineData("a \"quoted\" name")]
    [InlineData("back\\slash and \\u0041 written out")]
    [InlineData("line one\nline two\r\ttabbed")]
    [InlineData("µ-strip Ω 端口 😀")]
    [InlineData("\u0000\u001f\u007f")]
    [InlineData("\"); import os; os.system(\"x\") #")]
    public void PyStr_ReadsBackToExactlyItsText_AndIsPrintableAscii(string text)
    {
        string literal = EmergeScriptWriter.PyStr(text);
        Assert.All(literal, c => Assert.InRange(c, ' ', '~'));
        Assert.Equal(text, ReadLiteral(literal));
        Assert.Equal(text, JsonSerializer.Deserialize<string>(literal));
    }

    /// <summary>The string-literal grammar Python's <c>json</c> module reads, for the escapes <c>PyStr</c> may write: a
    /// quote, a backslash and <c>\uXXXX</c>, a surrogate pair joining into one character. Anything else is refused.</summary>
    private static string ReadLiteral(string literal)
    {
        Assert.True(literal.Length >= 2 && literal[0] == '"' && literal[^1] == '"', literal);
        var b = new StringBuilder();
        for (int i = 1; i < literal.Length - 1; i++)
        {
            char c = literal[i];
            Assert.NotEqual('"', c);
            if (c != '\\') { b.Append(c); continue; }
            char e = literal[++i];
            if (e is '"' or '\\') b.Append(e);
            else if (e == 'u') { b.Append((char)Convert.ToInt32(literal.Substring(i + 1, 4), 16)); i += 4; }
            else Assert.Fail($"an escape PyStr never writes: \\{e}");
        }
        return b.ToString();
    }

    /// <summary>R-emi4-0a — the writer's files compose no text: no interpolated or formatted string, and the emitter's raw
    /// output only ever a literal, a number or PyStr's.</summary>
    [Fact]
    public void TheWriter_ComposesNoText_AndTheEmitterWritesOnlyLiteralsNumbersAndPyStr()
    {
        string dir = Path.Combine(PalaceBackendTests.RepoRoot(), "src", "Design", "Em3d", "Solvers", "EMerge");
        foreach (string file in new[] { "EmergeScriptWriter.cs", "EmergeScriptWriter.Emit.cs" })
        {
            string code = StripComments(File.ReadAllText(Path.Combine(dir, file)));
            Assert.DoesNotMatch(new Regex(@"\$@?""|@\$""|\bFormat\("), code);
            foreach (System.Text.RegularExpressions.Match raw in Regex.Matches(code, @"(?<!void )\bRaw\((.*?)\);"))
                Assert.Matches(new Regex(@"^(""[^""]*""|'.'|'\\.'|PyStr\(\w+\)|Num\(\w+\)|value \? ""true"" : ""false""|value\.ToString\(CultureInfo\.InvariantCulture\)|close|token)$"),
                               raw.Groups[1].Value);
        }
    }

    /// <summary>R-emi4-0a — the driver executes nothing it reads: no eval, exec, compile, dynamic import or process.</summary>
    [Fact]
    public void TheDriver_EvaluatesNothing_AndIsWrittenIntoTheRunDirectoryUnchanged()
    {
        string driver = EmergeScriptWriter.Driver;
        foreach (string banned in new[] { "eval(", "exec(", "compile(", "__import__", "importlib", "subprocess", "os.system", "pickle", "marshal" })
            Assert.DoesNotContain(banned, driver, StringComparison.Ordinal);
        Assert.All(Regex.Matches(driver, @"getattr\(([^)]*)\)").Select(m => m.Groups[1].Value), a => Assert.Matches(@"^\w+, ""\w+"", None$", a));

        var low = EmergeScriptWriter.Write(Microstrip(), EmergeSettings.Default);
        Assert.True(low.Ok, low.Refusal);
        EmergeScriptWriter.WriteInto(low, _root);
        Assert.Equal(driver, File.ReadAllText(Path.Combine(_root, EmergeScriptWriter.DriverFile)));
        Assert.Equal(low.ProblemJson, File.ReadAllText(Path.Combine(_root, EmergeScriptWriter.ProblemFile)));
    }

    private static string StripComments(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    // ── 3. Port polarity ─────────────────────────────────────────────────────────────────────────

    /// <summary>A port's direction is circuitRF's own, unchanged: EMerge's lumped port drives its voltage along it, and a
    /// thru with both ports pointing from ground to strip gave S21 = −57.7° against an expected −54° (a flipped port gave
    /// +122°; src/Design/RESOLVED.md, EMI-4) — the phase Palace's convention gives.</summary>
    [Fact]
    public void APortsDirection_IsWrittenAsCircuitRfStatesIt()
    {
        var problem = Microstrip();
        using var doc = JsonDocument.Parse(EmergeScriptWriter.Write(problem, EmergeSettings.Default).ProblemJson!);
        var ports = doc.RootElement.GetProperty("ports").EnumerateArray().ToList();
        Assert.Equal(problem.Ports.Count, ports.Count);
        for (int k = 0; k < ports.Count; k++)
        {
            var d = ports[k].GetProperty("direction").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            Assert.Equal([problem.Ports[k].Direction.X, problem.Ports[k].Direction.Y, problem.Ports[k].Direction.Z], d);
        }
    }

    // ── 4. Refusals, one per row EMerge cannot state ───────────────────────────────────────────────

    [Theory]
    [InlineData("eigenmode-is-lowered", null)]
    [InlineData("thermal", "thermal problem")]
    [InlineData("static", "electrostatic problem cannot be lowered for EMerge")]
    [InlineData("settings", "EMerge.ElementOrder is 3")]
    [InlineData("terminal-group-moved", "Port 'Left' has its reference plane moved 1 mm off its face; EMerge runs terminal ports with the reference plane on the port face")]
    [InlineData("hollow-waveguide", "names one conductor at both ends, or two of the air box's faces, and the port reader did not find a hollow guide")]
    [InlineData("coaxial-port", "is a coaxial lumped port")]
    [InlineData("complex-z0", "terminated in a real resistance")]
    [InlineData("voxel", "is a voxelized phantom")]
    [InlineData("face-boundary", "is a face boundary")]
    [InlineData("symmetry", "is a Symmetry face")]
    [InlineData("no-ports", "nothing for EMerge to excite")]
    [InlineData("short-element", "a short circuit, which EMerge's surface impedance cannot be")]
    [InlineData("low-sigma-conductor", "at or below EMerge's surface-impedance limit")]
    public void EachThingEMergeCannotBeTold_IsRefusedByName(string row, string? expected)
    {
        var p = Microstrip();
        var settings = EmergeSettings.Default;
        var port = p.Ports[0];
        p = row switch
        {
            "eigenmode-is-lowered" => p with { Type = Em3dProblemType.Eigenmode, EigenmodeTargetHz = 1e9 },
            "thermal" => p with { Type = Em3dProblemType.Thermal },
            "static" => p with { Type = Em3dProblemType.Electrostatic },
            "terminal-group-moved" => p with
            {
                Ports = [.. p.Ports.Select(q => q with { Kind = Em3dPortKind.Wave, FaceGroup = 1, FaceGroupLabel = "Left",
                                                         ReferencePlane = q.ReferencePlane with { ShiftM = 1e-3 } })],
            },
            "hollow-waveguide" => p with { Ports = [port with { Kind = Em3dPortKind.Wave, NegativeObject = port.PositiveObject }, .. p.Ports.Skip(1)] },
            "coaxial-port" => p with { Ports = [port with { Annulus = new Em3dAnnulus(1e-4, 3e-4, true) }, .. p.Ports.Skip(1)] },
            "complex-z0" => p with { Ports = [port with { Z0 = new Complex(50, 5) }, .. p.Ports.Skip(1)] },
            "voxel" => p with { Solids = [p.Solids[0] with { Voxel = new Em3dVoxelLattice("Head", new Point3(0, 0, 0), 1e-3) }, .. p.Solids.Skip(1)] },
            "face-boundary" => p with { FaceBoundaries = [new Em3dFaceBoundary(p.Solids[0].Name, "zmax", Em3dFaceBoundaryKind.Pec)] },
            "symmetry" => p with { Boundary = p.Boundary with { Faces = p.Boundary.Faces with { XMin = Em3dBoundaryKind.Symmetry } } },
            "no-ports" => p with { Ports = [] },
            "short-element" => p with
            {
                LumpedElements = [new Em3dLumpedElement("Rshort", port.Min, port.Max, port.Direction, 0, null, null, Em3dRlcTopology.Parallel)],
            },
            "low-sigma-conductor" => p with
            {
                Materials = [.. p.Materials.Select(m => m.Name == p.Solids.First(s => s.Role == Em3dRole.Conductor).Material
                    ? m with { SigmaSm = 100 } : m)],
            },
            _ => p,
        };
        if (row == "settings") settings = settings with { ElementOrder = 3 };

        var low = EmergeScriptWriter.Write(p, settings);
        if (expected is null) { Assert.True(low.Ok, low.Refusal); return; }
        Assert.False(low.Ok);
        output.WriteLine(low.Refusal);
        Assert.Contains(expected, low.Refusal, StringComparison.Ordinal);
    }

    /// <summary>brief-emw-2 R-emw2-6 — a wave port of one terminal is lowered, and the same port with a complex Z0 is refused:
    /// the renormalisation's target must be real and positive.</summary>
    [KernelFact]
    public void AOneTerminalWavePort_IsLowered_AndOneWithAComplexZ0_IsRefused()
    {
        Assert.True(Lowered("wave-launch", out var problem).Ok);
        var wave = problem.Ports.Single(q => q.Kind == Em3dPortKind.Wave);
        var complex = problem with { Ports = [.. problem.Ports.Select(q => q == wave ? q with { Z0 = new Complex(50, 5) } : q)] };
        var low = EmergeScriptWriter.Write(complex, EmergeSettings.Default);
        Assert.False(low.Ok);
        Assert.Contains("EMerge's wave port refers its S-parameters to the mode", low.Refusal, StringComparison.Ordinal);
    }

    /// <summary>The setup-level refusal (the descriptor's, before anything is looked for) names the solver that does it.</summary>
    [Fact]
    public void AStaticSetupOnEMerge_IsRefusedBeforeAnythingIsLookedFor_NamingPalace()
    {
        var setup = new EmSetup { Name = "S", Solver3D = EmergeSolver.Id, Problem3D = Em3dProblemType.Magnetostatic };
        string? why = Em3dRunService.SolverRefusal(setup);
        Assert.NotNull(why);
        Assert.Contains("magnetostatic solve on EMerge", why, StringComparison.Ordinal);
        Assert.Contains("--solver palace", why, StringComparison.Ordinal);
    }

    // ── 5. The settings section ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSection_RoundTrips_AnAbsentOneIsTheDefaults_AndAnEmptyOneIsNeverWritten()
    {
        var plain = new EmSetup { Name = "S", Solver3D = EmergeSolver.Id };
        string text = EmSetupPersistence.Serialize(plain);
        Assert.Contains("\"Solver3D\": \"Emerge\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("EMerge\"", text, StringComparison.Ordinal);
        Assert.Equal(EmergeSettings.Preset(PalaceQuality.Standard), EmergeSettings.Resolve(EmSetupPersistence.Deserialize(text).Emerge));

        plain.Emerge = new CemEmerge();
        Assert.Equal(text, EmSetupPersistence.Serialize(plain));

        var set = new EmSetup { Name = "S", Solver3D = EmergeSolver.Id, Emerge = new CemEmerge { Quality = PalaceQuality.Draft, MaxSamples = 40 } };
        string written = EmSetupPersistence.Serialize(set);
        Assert.Contains("\"EMerge\": {", written, StringComparison.Ordinal);
        var read = EmSetupPersistence.Deserialize(written);
        Assert.Equal(EmergeSettings.Preset(PalaceQuality.Draft) with { MaxSamples = 40 }, EmergeSettings.Resolve(read.Emerge));
        Assert.Equal(written, EmSetupPersistence.Serialize(read));
        Assert.Equal(written, EmSetupPersistence.Serialize(read.Clone()));
        var embedded = EmSetupPersistence.FromEmbedded(EmSetupPersistence.ToEmbedded(read));
        Assert.Equal(EmergeSettings.Resolve(read.Emerge), EmergeSettings.Resolve(embedded.Emerge));
        Assert.DoesNotContain("IsEmpty", EmSetupPersistence.ToEmbedded(read).GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>brief-emi-5 — with its run in, EMerge is offered wherever a solver is: the registry's list, <c>em --solver</c>,
    /// a <c>Solver3D</c> spelling, and the programs Settings lists.</summary>
    [Fact]
    public void EMerge_IsOffered_ByTheRegistry_TheCliAndTheFormat()
    {
        Assert.Equal(EmergeSolver.Id, Em3dSolvers.FromSpelling("emerge"));
        Assert.Contains(Em3dSolvers.All, d => d.Id == EmergeSolver.Id);
        Assert.Equal(EmergeSolver.Id, Em3dSolvers.FromCliWord("emerge"));
        Assert.Contains(SolverTool.Emerge, Em3dSolvers.Tools);
    }

    // ── 6. The driver, against a real EMerge (Skipped without one) ───────────────────────────────

    /// <summary>The driver reads each golden's input and builds and meshes the model in EMerge, so a mapping EMerge rejects
    /// is caught. No solve. The launch's case needs the geometry kernel as well, and is left out without it.</summary>
    [EmergeFact]
    public void TheDriver_BuildsAndMeshesEachGolden_InEMerge()
    {
        string python = SolverDiscovery.ReadinessFor(EmergeSolver.Id).Single(r => r.Tool == SolverTool.Emerge).Installation!.Path;
        foreach (var (name, _, _) in Cases)
        {
            if (name == "connector-launch" && KernelForTests.SkipReason is not null) continue;
            if (name == "wave-launch") continue;     // its own test below reads what the driver says of its wave port
            var low = Lowered(name, out _);
            string dir = Path.Combine(_root, name);
            EmergeScriptWriter.WriteInto(low, dir);
            var psi = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
            foreach (string a in new[] { "-I", EmergeScriptWriter.DriverFile, EmergeScriptWriter.ProblemFile, "mesh" }) psi.ArgumentList.Add(a);
            psi.Environment["MPLBACKEND"] = "Agg";
            using var p = Process.Start(psi)!;
            var stderr = p.StandardError.ReadToEndAsync();
            string stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            string last = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(l => l.StartsWith('{')) ?? "";   // EMerge may print after it
            output.WriteLine($"{name}: {last}");
            Assert.True(p.ExitCode == 0, $"{name}: exit {p.ExitCode}: {last}\n{stderr.Result[^Math.Min(2000, stderr.Result.Length)..]}");
            using var line = JsonDocument.Parse(last);
            Assert.True(line.RootElement.GetProperty("tets").GetInt64() > 0);
        }
    }

    /// <summary>brief-emw-2 §7 test 5 — the driver builds the wave-port Launch: its region is EMerge's full wave-port condition
    /// (<c>WavePort</c>, before EMerge 3.0 <c>WavePortIH</c>) with its mode solved at every frequency though one material (PTFE) meets it, and no PEC claims any of its faces, although
    /// the region lies on the xmin face, which is PEC.</summary>
    [EmergeFact]
    public void TheDriver_BuildsTheWavePortLaunch_ItsRegionAWavePort_WithNoPec()
    {
        if (KernelForTests.SkipReason is { } noKernel) { output.WriteLine("not run: " + noKernel); return; }
        var low = Lowered("wave-launch", out _);
        string dir = Path.Combine(_root, "wave-launch");
        EmergeScriptWriter.WriteInto(low, dir);
        using var line = JsonDocument.Parse(MeshInEmerge(dir));
        var wave = line.RootElement.GetProperty("wavePorts").EnumerateArray().Single();
        Assert.Equal(1, wave.GetProperty("number").GetInt32());
        Assert.StartsWith("WavePort", wave.GetProperty("condition").GetString());
        Assert.False(wave.GetProperty("mixed").GetBoolean());
        Assert.True(wave.GetProperty("modePerFrequency").GetBoolean());
        Assert.True(wave.GetProperty("regionFaces").GetInt32() > 0);
        Assert.Equal(0, wave.GetProperty("pecFaces").GetInt32());
    }

    /// <summary>brief-dfr27-4 R-dfr27-4-3 — a copper plane in the air box's corner, its outline counter-clockwise and its bottom
    /// edge on the box's ymin face, with an edge 0.39 µm long leaving that face at an angle (testdata/em3d/emerge-fragment, the
    /// ten-layer board's failure cut down to one solid and rebuilt synthetically). In metres that edge sits a few of
    /// OpenCASCADE's absolute tolerances (1e-7 in model units) long: the fragment left the plane unsplit, Gmsh's
    /// remove_all_duplicates split it after EMerge had taken its map, and the build stopped on "Unknown model region with tag
    /// 2". The driver builds and meshes in micrometres now, so it meshes.</summary>
    [EmergeFact]
    public void TheDriver_MeshesASubMicronEdgeAtTheBoardEdge()
    {
        string dir = FragmentFixture(null);
        using var line = JsonDocument.Parse(MeshInEmerge(dir));
        Assert.True(line.RootElement.GetProperty("tets").GetInt64() > 0);
        Assert.Equal(5, line.RootElement.GetProperty("conductorFaces").GetInt32());
    }

    /// <summary>brief-dfr27-4 R-dfr27-4-2 — a model the kernel cannot fragment cleanly is refused NAMING the solid: its name
    /// (a layout solid's starts with its layer), its material and its bounding box, never the bare tag. The same plane with
    /// that edge a million times shorter, so in micrometres it sits as near the tolerance as the original did in metres.</summary>
    [EmergeFact]
    public void AModelTheKernelCannotFragment_IsRefusedNamingTheSolid()
    {
        string dir = FragmentFixture(ring => { ring[1]![0] = ring[2]![0]!.GetValue<double>() + 0.266e-12; ring[1]![1] = 0.283e-12; });
        var (exit, last) = DriverMesh(dir);
        Assert.Equal(2, exit);
        string error = JsonDocument.Parse(last).RootElement.GetProperty("error").GetString()!;
        Assert.StartsWith("GeometryNotFragmented: ", error, StringComparison.Ordinal);
        Assert.Contains("'Inner 1/GND/1' (Copper, x 4612 to 5000 µm, y 0 to 48 µm, z 500 to 518 µm)", error, StringComparison.Ordinal);
        Assert.DoesNotContain("tag", error, StringComparison.Ordinal);
    }

    /// <summary>The fragment fixture and the driver in a directory of their own, its plane's ring changed by
    /// <paramref name="edit"/> first.</summary>
    private string FragmentFixture(Action<System.Text.Json.Nodes.JsonArray>? edit)
    {
        string dir = Path.Combine(_root, "fragment");
        Directory.CreateDirectory(dir);
        var problem = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(
            Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "emerge-fragment", EmergeScriptWriter.ProblemFile)))!;
        edit?.Invoke(problem["solids"]![0]!["shape"]!["rings"]![0]!.AsArray());
        File.WriteAllText(Path.Combine(dir, EmergeScriptWriter.ProblemFile), problem.ToJsonString());
        File.WriteAllText(Path.Combine(dir, EmergeScriptWriter.DriverFile), EmergeScriptWriter.Driver);
        return dir;
    }

    /// <summary>The driver's mesh mode in <paramref name="dir"/>: its last stdout line, after asserting it succeeded.</summary>
    private string MeshInEmerge(string dir)
    {
        var (exit, last) = DriverMesh(dir, out string stderr);
        Assert.True(exit == 0, $"exit {exit}: {last}\n{stderr[^Math.Min(2000, stderr.Length)..]}");
        return last;
    }

    private (int Exit, string Last) DriverMesh(string dir) => DriverMesh(dir, out _);

    /// <summary>The driver's mesh mode in <paramref name="dir"/>: its exit code and its last stdout line.</summary>
    private (int Exit, string Last) DriverMesh(string dir, out string stderr)
    {
        string python = SolverDiscovery.ReadinessFor(EmergeSolver.Id).Single(r => r.Tool == SolverTool.Emerge).Installation!.Path;
        var psi = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
        foreach (string a in new[] { "-I", EmergeScriptWriter.DriverFile, EmergeScriptWriter.ProblemFile, "mesh" }) psi.ArgumentList.Add(a);
        psi.Environment["MPLBACKEND"] = "Agg";
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        string last = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(l => l.StartsWith('{')) ?? "";   // EMerge may print after it
        output.WriteLine(last);
        stderr = err.Result;
        return (p.ExitCode, last);
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The generator's microstrip, as Palace's writer gates use it.</summary>
    private static Em3dProblem Microstrip()
    {
        var (setup, source) = Em3dGeneratorTests.Microstrip();
        var r = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(r.Ok, r.Refusal);
        return r.Problem!;
    }
}

/// <summary>A fact that needs an EMerge discovery finds and validates (<c>CIRCUITRF_EMERGE</c> may name one).</summary>
public sealed class EmergeFactAttribute : FactAttribute
{
    public EmergeFactAttribute()
    {
        if (SolverDiscovery.ReadinessFor(EmergeSolver.Id).FirstOrDefault(r => !r.Proceeds) is { } r)
            Skip = $"needs an EMerge: {r.Refusal}";
    }
}

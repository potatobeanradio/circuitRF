using System.Globalization;
using System.Text;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-em3d-42 R-em3d42-2b (gate 2) — the generator's output, as text, for every .cem in the
//  repository and every generator fixture the Em3d tests build. The dumps under
//  testdata/em3d/generator-dumps/ were written BEFORE Em3dLayoutSolids was split out of the
//  generator, from the generator as it stood; the split, and anything after it, must reproduce them
//  byte for byte. Everything the generator returns is in the text — the problem, the notes, the
//  warnings, the origins, the material sources, the wire reports — so a refactor cannot move any of
//  it unseen. Set CRF_WRITE_GENERATOR_DUMPS=1 to (re)write them; nothing else does.
//  Every case states its air box (GoldenAirBoxes), so a change to the default padding never moves a dump.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class Em3dGeneratorDumpTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in Fixtures()) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Gate2_TheGeneratorsOutput_IsByteIdenticalToItsPreSplitDump(string name)
    {
        var build = Fixtures().Single(f => f.Name == name).Build;
        string text = Dump(build(GoldenAirBoxes.For(name)));
        string path = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "generator-dumps", FileName(name));
        if (Environment.GetEnvironmentVariable("CRF_WRITE_GENERATOR_DUMPS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Assert.True(File.Exists(path), $"no pre-split dump for '{name}' at {path}");
        Assert.Equal(File.ReadAllText(path), text);
    }

    private static string FileName(string name)
        => new string([.. name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_')]) + ".txt";

    /// <summary>Every case: the repo's .cem files (a planar one made 3D in memory, so every one
    /// exercises the generator), and the fixtures of the Em3d tests.</summary>
    /// <summary>Each build takes the air box it states (<see cref="GoldenAirBoxes"/>); a face its own setup states wins.</summary>
    internal static IEnumerable<(string Name, Func<EmAirBox?, Em3dGenerationResult> Build)> Fixtures()
    {
        string root = PalaceBackendTests.RepoRoot();
        foreach (string cem in new[] { "examples", "testdata" }
                     .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.cem", SearchOption.AllDirectories))
                     .Order(StringComparer.Ordinal))
        {
            string rel = Path.GetRelativePath(root, cem).Replace('\\', '/');
            yield return ("cem " + rel, box =>
            {
                var setup = EmSetupPersistence.LoadFromFile(cem);
                if (!setup.Is3D) setup.Solver3D = Em3dSolverId.Palace;
                setup.AirBox = GoldenAirBoxes.Merge(setup.AirBox, box);
                var resolution = EmSetupResolver.Resolve(cem, setup.LayoutRef,
                                                         WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(cem)),
                                                         new TechnologyCache());
                if (resolution.Source?.Technology is not { } tech)
                    return new Em3dGenerationResult(null, "unresolved: " + string.Join(" ", resolution.Diagnostics), []);
                return Em3dGenerator.Generate(setup, resolution.Source, tech);
            });
        }

        yield return ("fixture microstrip", box => Gen(Em3dGeneratorTests.Microstrip(), box));
        yield return ("fixture caseB-plated", box => Gen(Em3dGeneratorTests.CaseB(plated: true), box));
        yield return ("fixture caseB-solid", box => Gen(Em3dGeneratorTests.CaseB(plated: false), box));
        yield return ("fixture via-padded", box =>
        {
            var (s, src) = Em3dGeneratorTests.CaseB(plated: true);
            s.AirBox = PalaceBackendTests.Padded(1500);
            return Gen((s, src), box);
        });
        yield return ("fixture stripline", box => Gen(PalaceBackendTests.Stripline(800e-6, 1000e-6, 5e-3, 2.2), box));
        yield return ("fixture wave-microstrip", box => Gen(PalaceEigenTests.WaveMicrostrip(wave: true), box));
        yield return ("fixture wave-microstrip-outline", box => Gen(PalaceEigenTests.WaveMicrostrip(wave: true, outlineBeyond: true), box));
        yield return ("fixture lumped-microstrip", box => Gen(PalaceEigenTests.WaveMicrostrip(wave: false), box));
        foreach (var kind in new[] { Em3dProblemType.Electrostatic, Em3dProblemType.Eigenmode })
            yield return ($"fixture microstrip-{kind}", box =>
            {
                var (s, src) = Em3dGeneratorTests.Microstrip();
                s.Problem3D = kind;
                if (kind == Em3dProblemType.Electrostatic) s.Terminals3D = [new EmTerminal3D("T", "none")];
                return Gen((s, src), box);
            });
    }

    private static Em3dGenerationResult Gen((EmSetup Setup, EmLayoutSource Source) f, EmAirBox? box)
    {
        f.Setup.AirBox = GoldenAirBoxes.Merge(f.Setup.AirBox, box);
        return Em3dGenerator.Generate(f.Setup, f.Source, f.Source.Technology!);
    }

    // ── the dump ────────────────────────────────────────────────────────────────────────────────

    internal static string Dump(Em3dGenerationResult r)
    {
        var sb = new StringBuilder();
        sb.Append("refusal ").Append(r.Refusal ?? "(none)").Append('\n');
        foreach (string n in r.Notes) sb.Append("note ").Append(n).Append('\n');
        foreach (string w in r.Warnings) sb.Append("warning ").Append(w).Append('\n');
        if (r.Problem is { } p) Problem(sb, p);
        foreach (var (k, v) in r.Origins.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append($"origin {k} {v.Kind} {v.StackupEntry ?? "-"} {v.DrawingLayer?.ToString() ?? "-"} {v.SheetReason ?? "-"}\n");
        foreach (var (k, v) in r.MaterialSources.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append($"material-source {k} {v}\n");
        sb.Append("no-alpha ").Append(string.Join(",", r.NoAlpha)).Append('\n');
        sb.Append("unknown-temperature ").Append(string.Join(",", r.UnknownTemperature)).Append('\n');
        foreach (var w in r.Wires)
            sb.Append($"wire {w.Name} {w.Array} {w.Member} {w.Material} {w.Section} {R(w.DiameterM)} {w.Size} {w.Start} {w.End}\n");
        return sb.ToString();
    }

    internal static void Problem(StringBuilder sb, Em3dProblem p)
    {
        foreach (var s in p.Solids)
            sb.Append($"solid {s.Name} {s.Material} {s.Role} {s.Order} {Prim(s.Primitive)}\n");
        foreach (var s in p.Sheets)
            sb.Append($"sheet {s.Name} {s.Material} {s.Order} {R(s.Z)} {R(s.ThicknessM)} [{Ring(s.Outline)}]" +
                      string.Concat(s.Holes.Select(h => $"[{Ring(h)}]")) + "\n");
        foreach (var m in p.Materials)
            sb.Append($"material {m.Name} {R(m.Epsr)} {(m.EpsrTensor is { } t ? string.Join(",", t.Select(R)) : "-")} " +
                      $"{R(m.TanD)} {R(m.Mur)} {R(m.SigmaSm)}\n");
        foreach (var q in p.Ports)
            sb.Append($"port {q.Number} {q.Name} {q.Kind} {q.PositiveObject} {q.NegativeObject} {P3(q.Min)} {P3(q.Max)} " +
                      $"{P3(q.Direction)} {R(q.Z0.Real)} {R(q.Z0.Imaginary)} {P3(q.ReferencePlane.Origin)} " +
                      $"{P3(q.ReferencePlane.Normal)} {R(q.ReferencePlane.ShiftM)} " +
                      $"{(q.Annulus is { } a ? $"{R(a.InnerRadiusM)},{R(a.OuterRadiusM)},{a.Outward}" : "-")} " +
                      $"{(q.VoltagePath is { } v ? $"{P3(v.From)}->{P3(v.To)}" : "-")}\n");
        sb.Append($"box {P3(p.Boundary.Min)} {P3(p.Boundary.Max)} {p.Boundary.Faces}\n");
        sb.Append($"freq {R(p.Frequency.StartHz)} {R(p.Frequency.StopHz)} {p.Frequency.Points} {p.Frequency.Kind} " +
                  $"{R(p.OperatingTempC)}\n");
        sb.Append($"type {p.Type} eigen {p.EigenmodeCount} {R(p.EigenmodeTargetHz)}\n");
        foreach (var t in p.Terminals)
            sb.Append($"terminal {t.Name} {t.SourcePort ?? "-"} {string.Join(",", t.Objects)}\n");
        sb.Append("ground ").Append(string.Join(",", p.GroundObjects)).Append('\n');
    }

    internal static string Prim(Em3dPrimitive prim) => prim switch
    {
        Em3dExtrudedPolygon e => $"extrude {R(e.ZBottom)} {R(e.ZTop)} [{Ring(e.Outline)}]" +
                                 string.Concat(e.Holes.Select(h => $"[{Ring(h)}]")),
        Em3dBox b => $"box {P3(b.Min)} {P3(b.Max)}",
        Em3dCylinder c => $"cylinder {P3(c.AxisStart)} {P3(c.AxisEnd)} {R(c.Radius)}",
        Em3dSphere s => $"sphere {P3(s.Center)} {R(s.Radius)}",
        Em3dTruncatedSphere t => $"tsphere {P3(t.Center)} {R(t.Radius)} {R(t.ZMin)} {R(t.ZMax)}",
        Em3dSweep w => $"sweep {w.Section} {R(w.Diameter)} path[{string.Join(" ", w.Path.Select(P3))}] rings" +
                       string.Concat(w.Rings.Select(r => $"[{string.Join(" ", r.Select(P3))}]")),
        var other => other.GetType().Name,
    };

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static string P3(Point3 q) => $"({R(q.X)},{R(q.Y)},{R(q.Z)})";
    private static string Ring(IReadOnlyList<Point2> r) => string.Join(" ", r.Select(q => $"{R(q.X)},{R(q.Y)}"));
}

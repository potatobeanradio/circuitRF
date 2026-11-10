// ================================================================
//  NetlistToSchematicTests.cs — brief-agent-authoring-overview.md AA-6's gate.
//
//  `netlist x.cnl --to-schematic` draws a netlist. The drawing is worth nothing unless it IS the
//  netlist, so the gate is the round trip: every committed `.cnl` either draws and extracts back —
//  through SchematicCircuit, the extraction Simulate performs — to the same instances, nets and
//  values, or is refused for a reason this file names. A second test holds the drawing to being
//  readable in the two ways a machine can check (no two symbols on top of each other, every wire
//  orthogonal), and a third drives the verb as a process, byte for byte against the in-process call.
// ================================================================

using System.Diagnostics;
using System.Text.Json.Nodes;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Cli;

public sealed class NetlistToSchematicTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-aa6-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>
    /// The committed netlists that cannot be drawn, and why. Pinned EXACTLY: a netlist that starts
    /// being refused is a regression, and one that stops being refused should be moved into the
    /// round trip on purpose rather than by accident.
    /// </summary>
    private static readonly Dictionary<string, string> Refused = new()
    {
        ["pi_network.cnl"]                         = "defines 1 cell(s)",
        ["Hero1B/hero1b.cnl"]                      = "defines 2 cell(s)",
        ["A3/semi_capacitor.cnl"]                  = "'SemiC' has no schematic symbol",
        ["em3d/f0/A-bondwire/kernelw/case.cnl"]    = "a wirebond's pins come from the design file",
    };

    /// <summary>
    /// The gate. Every `.cnl` under testdata/ — the committed netlists, since examples/ ships
    /// schematics only — is drawn, written, read back as a FILE and extracted, and the extraction is
    /// compared with the netlist as the reader reads it: per instance its type, its nets in order
    /// and its parameters; the globals; the measurements; the analyses.
    /// </summary>
    [Fact]
    public void EveryCommittedNetlist_DrawsAndExtractsBackToItself_OrIsRefusedForAStatedReason()
    {
        string testdata = Path.Combine(RepoRoot(), "testdata");
        var failures = new List<string>();
        int drawn = 0;

        foreach (string file in Directory.EnumerateFiles(testdata, "*.cnl", SearchOption.AllDirectories).Order())
        {
            string rel = Path.GetRelativePath(testdata, file).Replace('\\', '/');
            var (lib, tb) = CnlReader.ReadFile(file);
            string dir = Path.Combine(_root, "rt", rel.Replace('/', '_'));
            var result = NetlistSchematic.Build(lib, tb, dir);

            if (Refused.TryGetValue(rel, out string? reason))
            {
                if (result.Schematic is not null) failures.Add($"{rel}: drew, but is pinned as refused");
                else if (!result.Refusals.Any(r => r.Contains(reason, StringComparison.Ordinal)))
                    failures.Add($"{rel}: refused for {string.Join(" | ", result.Refusals)}");
                continue;
            }
            if (result.Schematic is null) { failures.Add($"{rel}: refused — {string.Join(" | ", result.Refusals)}"); continue; }

            string csch = Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + ".csch");
            Directory.CreateDirectory(dir);
            SchematicPersistence.SaveToFile(csch, result.Schematic, Path.GetFileNameWithoutExtension(file));
            var (_, back) = SchematicCircuit.FromSchematic(csch);

            failures.AddRange(Differences(tb, back).Select(d => $"{rel}: {d}"));
            drawn++;
            if (result.Notes.Count > 0) output.WriteLine($"{rel}: {string.Join(" | ", result.Notes)}");
        }

        output.WriteLine($"{drawn} drawn and round-tripped, {Refused.Count} refused");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// brief-artsch-1: a netlist holding a CPWG and a SLIN draws them as their own symbols and extracts
    /// back to itself, gap and all.
    /// </summary>
    [Fact]
    public void ACpwgAndASlin_DrawAndRoundTrip()
    {
        string dir = Path.Combine(_root, "planar");
        Directory.CreateDirectory(dir);
        string cnl = Path.Combine(dir, "planar.cnl");
        File.WriteAllText(cnl,
            "Port:P1 a 0 Num=1 Z=50 Ohm\nPort:P2 c 0 Num=2 Z=50 Ohm\n" +
            "CPWG:C1 a b W=1.4 mm G=0.3 mm L=10 mm\n" +
            "SLIN:S1 b c W=0.65 mm L=10 mm\n" +
            "analysis SP1 type=sparam start=1 stop=6 npts=11 Unit=GHz\n");
        var (lib, tb) = CnlReader.ReadFile(cnl);

        var result = NetlistSchematic.Build(lib, tb, dir);
        Assert.True(result.Schematic is not null, string.Join(" | ", result.Refusals));
        Assert.Contains(result.Schematic!.Components, c => c.Symbol == SymbolKind.Cpwg && c.InstanceName == "C1");
        Assert.Contains(result.Schematic.Components, c => c.Symbol == SymbolKind.Slin && c.InstanceName == "S1");

        string csch = Path.Combine(dir, "planar.csch");
        SchematicPersistence.SaveToFile(csch, result.Schematic, "planar");
        var (_, back) = SchematicCircuit.FromSchematic(csch);
        var differences = Differences(tb, back).ToList();
        Assert.True(differences.Count == 0, string.Join("\n", differences));
    }

    /// <summary>
    /// brief-artsch-7 R-as7-8: a layer name the .cnl quotes because it holds a space is stored BARE in the drawn
    /// schematic, as the schematic stores every layer name — kept quoted, the extraction looked up a conductor
    /// named with the quotes and fell back to the default layer.
    /// </summary>
    [Fact]
    public void AQuotedLayerName_IsStoredBare()
    {
        string dir = Path.Combine(_root, "layers");
        Directory.CreateDirectory(dir);
        string cnl = Path.Combine(dir, "layers.cnl");
        File.WriteAllText(cnl,
            "Port:P1 a 0 Num=1 Z=50 Ohm\nPort:P2 b 0 Num=2 Z=50 Ohm\n" +
            "MLIN:T1 a b W=1 mm L=5 mm SignalLayer=\"Top Copper (1 oz)\" GroundReference=\"Bottom Copper\"\n" +
            "analysis SP1 type=sparam start=1 stop=6 npts=11 Unit=GHz\n");
        var (lib, tb) = CnlReader.ReadFile(cnl);

        var line = NetlistSchematic.Build(lib, tb, dir).Schematic!.Components.Single(c => c.InstanceName == "T1");
        Assert.Equal("Top Copper (1 oz)", line.Parameters.Single(p => p.Name == "SignalLayer").Expression);
        Assert.Equal("Bottom Copper", line.Parameters.Single(p => p.Name == "GroundReference").Expression);
    }

    /// <summary>
    /// A Touchstone drawn into a cell of a workspace is stored against the WORKSPACE ROOT, the base a
    /// schematic's <c>File</c> is read against, and still resolves to the same file; the technology stays
    /// relative to the drawing's own folder. Drawn into a cell, the drawing used to store
    /// <c>../../results/…</c>, relative to its own folder, which resolved above the workspace.
    /// </summary>
    [Fact]
    public void ATouchstoneDrawnIntoACell_IsStoredAgainstTheWorkspaceRoot()
    {
        string ws = Path.Combine(_root, "ws");
        string schDir = Path.Combine(ws, "Bench", "schematic");
        Directory.CreateDirectory(schDir);
        Directory.CreateDirectory(Path.Combine(ws, "results"));
        File.WriteAllText(Path.Combine(ws, ".cws"), "{\n  \"FormatVersion\": 2,\n  \"LibraryRefs\": [],\n  \"KnownFiles\": []\n}");
        string snp = Path.Combine(ws, "results", "block.s2p");
        File.WriteAllText(snp, "# GHz S RI R 50\n1 0 0 1 0 1 0 0 0\n2 0 0 1 0 1 0 0 0\n");
        string cnl = Path.Combine(ws, "bench.cnl");
        File.WriteAllText(cnl,
            "Port:P1 a 0 Num=1 Z=50 Ohm\nPort:P2 b 0 Num=2 Z=50 Ohm\n" +
            "SnP:EM1 a b 0 NumPorts=2 File=\"results/block.s2p\"\n" +
            "analysis SP1 type=sparam start=1 stop=2 npts=2 Unit=GHz\n");
        var (lib, tb) = CnlReader.ReadFile(cnl);

        var model = NetlistSchematic.Build(lib, tb, schDir).Schematic!;
        string stored = model.Components.Single(c => c.InstanceName == "EM1").Parameters.Single(p => p.Name == "File").Expression;
        Assert.Equal("\"results/block.s2p\"", stored);

        string csch = Path.Combine(schDir, "Bench.csch");
        File.WriteAllText(csch, SchematicPersistence.Serialize(model, "Bench"));
        string extracted = SchematicCircuit.CnlTextOf(csch);
        Assert.Contains("File=\"results/block.s2p\"", extracted);
        Assert.True(File.Exists(SnpPathPolicy.Resolve("results/block.s2p", SchematicCircuit.ReferenceBaseOf(csch), null)));
    }

    /// <summary>A loadpull's grid is stored against the workspace root as a part's File is: the reader hands it back
    /// absolute, and the drawing used to keep that, naming a folder of the machine it was drawn on.</summary>
    [Fact]
    public void ALoadpullGridDrawnIntoACell_IsStoredAgainstTheWorkspaceRoot()
    {
        string ws = Path.Combine(_root, "ws");
        string schDir = Path.Combine(ws, "Bench", "schematic");
        Directory.CreateDirectory(schDir);
        File.WriteAllText(Path.Combine(ws, ".cws"), "{\n  \"FormatVersion\": 2,\n  \"LibraryRefs\": [],\n  \"KnownFiles\": []\n}");
        File.WriteAllText(Path.Combine(schDir, "grid.gam"), "# gamma Z0=50 mag_ang\n0 0\n");
        string cnl = Path.Combine(ws, "bench.cnl");
        File.WriteAllText(cnl,
            "Tuner:TS a 0 Zdefault=50 Z0=50 BiasTee=on Vbias=0 Z[1]=50\nR:R1 a b R=10\n" +
            "Tuner:TL b 0 Zdefault=50 Z0=50 BiasTee=on Vbias=0 Z[1]=50\n" +
            "analysis LP1 type=loadpull Tone=1e9 LoadTuner=TL SourceTuner=TS Grid=\"Bench/schematic/grid.gam\" Sweep=Source\n");
        var (lib, tb) = CnlReader.ReadFile(cnl);

        var model = NetlistSchematic.Build(lib, tb, schDir).Schematic!;
        Assert.Equal("Bench/schematic/grid.gam", model.Analyses.OfType<LoadpullAnalysis>().Single().GridPath);
    }

    /// <summary>
    /// Readable in the two ways a machine can check: no symbol drawn over another, and no wire that
    /// is not horizontal or vertical. Over every netlist the gate draws.
    /// </summary>
    [Fact]
    public void EveryDrawing_HasNoOverlappingSymbols_AndOnlyOrthogonalWires()
    {
        string testdata = Path.Combine(RepoRoot(), "testdata");
        var failures = new List<string>();

        foreach (string file in Directory.EnumerateFiles(testdata, "*.cnl", SearchOption.AllDirectories).Order())
        {
            string rel = Path.GetRelativePath(testdata, file).Replace('\\', '/');
            if (Refused.ContainsKey(rel)) continue;
            var (lib, tb) = CnlReader.ReadFile(file);
            var model = NetlistSchematic.Build(lib, tb).Schematic!;

            foreach (var w in model.Wires)
                for (int i = 0; i + 1 < w.Points.Count; i++)
                    if (w.Points[i].X != w.Points[i + 1].X && w.Points[i].Y != w.Points[i + 1].Y)
                        failures.Add($"{rel}: a diagonal wire segment {w.Points[i]} → {w.Points[i + 1]}");

            // A glyph may poke a few units past its own pin, so two boxes meeting at a pin are not
            // an overlap; anything deeper than a quarter of a grid square on both axes is. A ground
            // sits ON the terminal it grounds and its box reaches back over that lead a little
            // further, so half a grid square is its allowance.
            var comps = model.BuildRenderModel().Model.Components;
            for (int a = 0; a < comps.Count; a++)
                for (int b = a + 1; b < comps.Count; b++)
                {
                    double ox = Math.Min(comps[a].GlyphBbMaxX, comps[b].GlyphBbMaxX) - Math.Max(comps[a].GlyphBbMinX, comps[b].GlyphBbMinX);
                    double oy = Math.Min(comps[a].GlyphBbMaxY, comps[b].GlyphBbMaxY) - Math.Max(comps[a].GlyphBbMinY, comps[b].GlyphBbMinY);
                    double allowance = comps[a].Symbol == SymbolKind.Ground || comps[b].Symbol == SymbolKind.Ground ? 50 : 25;
                    if (ox > allowance && oy > allowance)
                        failures.Add($"{rel}: {Name(comps[a])} and {Name(comps[b])} overlap ({ox:0}×{oy:0})");
                }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));

        static string Name(SchematicComponent c) => c.InstanceName.Length > 0 ? c.InstanceName : c.Symbol.ToString();
    }

    /// <summary>
    /// The verb as a PROCESS writes the same bytes the in-process call serialises — it owns no
    /// placement of its own — and a netlist it cannot draw is refused by id, with nothing written.
    /// </summary>
    [Fact]
    public void TheVerb_WritesTheInProcessDrawing_AndRefusesWhatCannotBeDrawn()
    {
        string cnl = Path.Combine(RepoRoot(), "testdata", "Hero1", "hero1.cnl");
        string outPath = Path.Combine(_root, "verb", "hero1.csch");

        var run = RunCli("netlist", cnl, "--to-schematic", "-o", outPath);
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);

        var (lib, tb) = CnlReader.ReadFile(cnl);
        string expected = SchematicPersistence.Serialize(
            NetlistSchematic.Build(lib, tb, Path.GetDirectoryName(outPath)).Schematic!, "hero1");
        Assert.Equal(expected, File.ReadAllText(outPath));

        string refusedOut = Path.Combine(_root, "verb", "pi.csch");
        var refused = RunCli("netlist", Path.Combine(RepoRoot(), "testdata", "pi_network.cnl"),
                             "--to-schematic", "-o", refusedOut, "--json");
        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("netlist.to-schematic.refused", Ids(refused.StdOut));
        Assert.False(File.Exists(refusedOut));
    }

    // ── the comparison ───────────────────────────────────────────────────────

    /// <summary>
    /// Where the extraction of the drawing differs from the netlist. A type is compared through the
    /// registry's own short codes, so <c>Term</c> and <c>Port</c> — one component, two spellings the
    /// reader accepts — are equal. The one parameter a drawing may ADD is the port count a variadic
    /// symbol needs to know how many pins to draw, when the line left it to its nets.
    /// </summary>
    private static IEnumerable<string> Differences(TestBench a, TestBench b)
    {
        if (a.Instances.Count != b.Instances.Count)
            yield return $"{a.Instances.Count} instance(s) became {b.Instances.Count}";

        var pool = b.Instances.ToList();
        foreach (var x in a.Instances)
        {
            var y = pool.FirstOrDefault(i => i.InstanceName == x.InstanceName && Canon(i.Reference) == Canon(x.Reference));
            if (y is null) { yield return $"{x.InstanceName} ({x.Reference}) is missing"; continue; }
            pool.Remove(y);

            if (!x.NetBindings.SequenceEqual(y.NetBindings))
                yield return $"{x.InstanceName} nets [{string.Join(" ", x.NetBindings)}] became [{string.Join(" ", y.NetBindings)}]";
            if (x.RefNetBinding != y.RefNetBinding)
                yield return $"{x.InstanceName} reference net {x.RefNetBinding} became {y.RefNetBinding}";

            var pa = x.Overrides.Select(P).ToHashSet();
            var pb = y.Overrides.Where(p => !(Added(p.Name) && !x.Overrides.Any(q => q.Name == p.Name))).Select(P).ToHashSet();
            if (!pa.SetEquals(pb))
                yield return $"{x.InstanceName} parameters lost [{string.Join(", ", pa.Except(pb))}] gained [{string.Join(", ", pb.Except(pa))}]";
        }

        var va = a.GlobalVariables.Select(v => $"{v.Name}={v.Expression} {v.Unit}").ToHashSet();
        var vb = b.GlobalVariables.Select(v => $"{v.Name}={v.Expression} {v.Unit}").ToHashSet();
        if (!va.SetEquals(vb)) yield return $"globals lost [{string.Join(", ", va.Except(vb))}] gained [{string.Join(", ", vb.Except(va))}]";

        var ma = a.Measurements.Select(m => $"{m.Name}={m.Expression} {m.Unit}").ToHashSet();
        var mb = b.Measurements.Select(m => $"{m.Name}={m.Expression} {m.Unit}").ToHashSet();
        if (!ma.SetEquals(mb)) yield return $"measurements lost [{string.Join(", ", ma.Except(mb))}] gained [{string.Join(", ", mb.Except(ma))}]";

        if (!a.Analyses.Select(x => x.Name).SequenceEqual(b.Analyses.Select(x => x.Name)))
            yield return "the analyses differ";

        static string P(ParameterAssignment p) => $"{p.Name}={p.Expression} {p.Unit}";
        static bool Added(string name) => name is "NumPorts" or "Pins" or "RefNode";
        static string Canon(string r)
            => (ComponentTypeRegistry.TryParseCode(r, out var k, out _) ? ComponentTypeRegistry.EngineReference(k) : r)
               .ToUpperInvariant();
    }

    // ── driving the verb ─────────────────────────────────────────────────────

    private static string[] Ids(string document)
    {
        try
        {
            return [.. (JsonNode.Parse(document)?["diagnostics"]?.AsArray() ?? [])
                       .Select(d => d?["id"]?.GetValue<string>() ?? "")];
        }
        catch { return []; }
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(NetlistToSchematicTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir.Length > 0 ? dir : AppContext.BaseDirectory;
    }
}

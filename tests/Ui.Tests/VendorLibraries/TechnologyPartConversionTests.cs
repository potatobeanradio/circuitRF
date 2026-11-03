using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.VendorLibraries.ModelLibrary;
using CircuitRF.Design.Workspace;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests.VendorLibraries;

/// <summary>
/// Brief vendorlib-11 §6: a technology-bound part becomes an SnP sampled on the author's technology at export. The
/// converted files carry the plan with a DC row first; the extended self-check passes and drives current through the
/// converted parts; the DC row is what that check sees; the exported schematic keeps every pin where it was; the
/// predicate covers every seam; and no technology, or a plan with nothing to sample, is a refusal. Every part and
/// technology here is synthetic or shipped.
/// </summary>
[Collection(CellStatGlobalsCollection.Name)]
public sealed class TechnologyPartConversionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-vl11-" + Guid.NewGuid().ToString("N")[..12]);

    public TechnologyPartConversionTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // 1 — the default plan: every converted file is 1001 points, 0 Hz first, and the self-check passes.
    [Fact]
    public void ACellOfRMlinMteeAndViagnd_Exports_EachConvertedFileIsThePlanWithDcFirst()
    {
        string cell = WriteBoardCell(ShippedTechnologies.DefaultId, out _);
        var plan = ModelLibraryExport.Plan(cell, new ExportOptions { LibraryDir = Path.Combine(_dir, "lib") });
        Assert.Equal("4 components use the technology and will be converted: TL1 (MLIN), T1 (MTEE), TL2 (MLIN), V1 (VIAGND).",
                     ModelLibraryExport.ConversionLine(plan));

        var result = ModelLibraryExport.Export(cell, new ExportOptions { LibraryDir = Path.Combine(_dir, "lib") });
        Assert.True(result.Succeeded, result.Refusal);
        Assert.Contains("4 parts converted to S-parameters, 1001 points; between samples, within", result.Message);
        // The tee's own note about its calibration reaches the export once — not once per internal run, on stderr.
        Assert.Single(result.Warnings, w => w.StartsWith("T1 (MTEE): ", StringComparison.Ordinal) && w.Contains("calibrated at a single point"));

        foreach (var (file, ports) in new[] { ("TL1.s2p", 2), ("TL2.s2p", 2), ("T1.s3p", 3), ("V1.s1p", 1) })
        {
            var snp = TouchstoneIO.ReadFile(Path.Combine(result.ModelDir!, "models", file));
            Assert.Equal(ports, snp.Matrices[0].RowCount);
            Assert.Equal(1001, snp.Frequencies.Length);
            Assert.Equal(0, snp.Frequencies[0]);
            Assert.Equal(20e9, snp.Frequencies[^1], 3);
        }

        var (part, _) = ModelPartManifest.Read(Path.Combine(result.ModelDir!, ModelFolder.PartFileName));
        Assert.Equal(["TL1", "T1", "TL2", "V1"], part!.Converted.Select(c => c.Instance));
        var tl1 = part.Converted[0];
        Assert.Equal("MLIN", tl1.Type);
        Assert.Equal("models/TL1.s2p", tl1.File);
        Assert.Contains("W=1.1 mm", tl1.Parameters);
        Assert.False(string.IsNullOrWhiteSpace(tl1.Technology));
        Assert.DoesNotContain(Path.DirectorySeparatorChar, tl1.Technology);
        Assert.Equal(new ModelPartFrequency { Low = "0 Hz", High = "20 GHz" }, part.Frequency);
        Assert.Contains("Technology: " + tl1.Technology, File.ReadAllText(Path.Combine(result.ModelDir!, "models", "TL1.s2p")));
    }

    // 2 — a plan that starts above DC still writes the DC row; delete it by hand and the driven DC comparison fails.
    [Fact]
    public void APlanFrom1To20GHz_StillWritesADcRow_AndTheDrivenComparisonNeedsIt()
    {
        string cell = WriteBoardCell(ShippedTechnologies.DefaultId, out _);
        var options = new ExportOptions
        {
            LibraryDir = Path.Combine(_dir, "lib"),
            Sweep = [new FrequencySpec("1", "20", 96, SweepKind.Linear, "GHz", "GHz")],
        };
        var result = ModelLibraryExport.Export(cell, options);
        Assert.True(result.Succeeded, result.Refusal);

        string tl1 = Path.Combine(result.ModelDir!, "models", "TL1.s2p");
        var snp = TouchstoneIO.ReadFile(tl1);
        Assert.Equal(97, snp.Frequencies.Length);
        Assert.Equal(0, snp.Frequencies[0]);
        Assert.Equal(1e9, snp.Frequencies[1], 3);

        var (manifest, _) = ModelPartManifest.Read(Path.Combine(result.ModelDir!, ModelFolder.PartFileName));
        Assert.Null(ModelLibraryExport.SelfCheck(cell, result.ModelDir!, manifest!, out _, snp.Frequencies));

        // Every line from the DC row to the next frequency's is the DC row; without it the SnP clamps to 1 GHz at DC.
        var lines = File.ReadAllLines(tl1).ToList();
        int first = lines.FindIndex(l => l.Length > 0 && char.IsDigit(l[0]));
        Assert.StartsWith("0 ", lines[first]);
        lines.RemoveAt(first);
        File.WriteAllLines(tl1, lines);
        File.SetLastWriteTimeUtc(tl1, DateTime.UtcNow.AddSeconds(5));

        string? mismatch = ModelLibraryExport.SelfCheck(cell, result.ModelDir!, manifest!, out _, snp.Frequencies);
        Assert.NotNull(mismatch);
        Assert.Contains("the DC voltage at pin", mismatch);
    }

    // 3 — the exported schematic keeps each converted part's pins where they were, and extracts to the same nets.
    [Fact]
    public void TheExportedSchematic_KeepsEveryConvertedPinWhereItWas_AndExtractsToTheSameNets()
    {
        string cell = WriteBoardCell(ShippedTechnologies.DefaultId, out _);
        var result = ModelLibraryExport.Export(cell, new ExportOptions
        {
            LibraryDir = Path.Combine(_dir, "lib"),
            Sweep = [new FrequencySpec("1", "10", 11, SweepKind.Linear, "GHz", "GHz")],
        });
        Assert.True(result.Succeeded, result.Refusal);

        var source = SchematicPersistence.LoadFromFile(Path.Combine(cell, "schematic", "TSTBRD.csch")).model;
        var exported = SchematicPersistence.LoadFromFile(Path.Combine(result.ModelDir!, "schematic", "TSTBRD.csch")).model;
        foreach (string name in new[] { "TL1", "T1", "TL2", "V1" })
        {
            var a = source.Components.Single(c => c.InstanceName == name);
            var b = exported.Components.Single(c => c.InstanceName == name);
            Assert.Equal(SymbolKind.Snp, b.Symbol);
            Assert.Equal(a.Symbol, SnpGlyph.Of(b));
            Assert.Equal((a.X, a.Y, a.Rotation), (b.X, b.Y, b.Rotation));
            Assert.Equal(a.EffectivePortCount, b.EffectivePortCount);
            for (int k = 0; k < a.EffectivePortCount; k++)
                Assert.Equal(a.GetPortWorldCoord(k), b.GetPortWorldCoord(k));
        }

        var src = NetExtractor.ExtractCell(cell, out _)!.Value;
        var dst = NetExtractor.ExtractCell(result.ModelDir!, out _)!.Value;
        var srcCell = src.Library.Find(src.CellName)!;
        var dstCell = dst.Library.Find(dst.CellName)!;
        Assert.Equal(srcCell.Ports, dstCell.Ports);
        foreach (var inst in srcCell.Instances)
        {
            var other = dstCell.Instances.Single(i => i.InstanceName == inst.InstanceName);
            int n = other.NetBindings.Count;
            Assert.Equal(inst.NetBindings.Take(n), other.NetBindings);
            if (inst.InstanceName is "TL1" or "T1" or "TL2" or "V1")
            {
                Assert.Equal("SnP", other.Reference);
                Assert.DoesNotContain(other.Overrides, o => o.Name == SnpGlyph.SymbolParameter);
            }
        }
    }

    // 4 — the one predicate is every seam's kinds: each *Injection class's own Is…Kind, found by reflection.
    [Fact]
    public void TechnologyBound_CoversEverySeamsKinds()
    {
        // A seam is a *SubstrateInjection / *Injection class of the schematic namespace that names the kinds it injects.
        var seams = typeof(TechnologyBound).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(TechnologyBound).Namespace && t.Name.EndsWith("Injection", StringComparison.Ordinal))
            .Select(t => (Type: t, Predicates: t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name.StartsWith("Is", StringComparison.Ordinal) && m.Name.EndsWith("Kind", StringComparison.Ordinal)
                            && m.ReturnType == typeof(bool) && m.GetParameters() is [{ ParameterType: var p }] && p == typeof(SymbolKind))
                .ToList()))
            .Where(s => s.Predicates.Count > 0)
            .ToList();
        Assert.Equal(["BroadsideSubstrateInjection", "MicrostripSubstrateInjection", "MmicPassiveInjection",
                      "PlanarLineSubstrateInjection", "TestPadInjection", "ViaSubstrateInjection"],
                     seams.Select(s => s.Type.Name).Order(StringComparer.Ordinal));
        foreach (var (seam, predicates) in seams)
        {
            foreach (var kind in Enum.GetValues<SymbolKind>())
                foreach (var m in predicates)
                    if ((bool)m.Invoke(null, [kind])!)
                        Assert.True(TechnologyBound.Is(kind), $"{seam.Name}.{m.Name} takes {kind}, which TechnologyBound does not.");
        }
    }

    // 5 — no technology is a refusal; no technology-bound part greys the group and refuses --sweep.
    [Fact]
    public async System.Threading.Tasks.Task NoTechnology_IsARefusal_AndNothingToConvert_RefusesASweep()
    {
        string cell = WriteBoardCell(technologyId: null, out _);
        var plan = ModelLibraryExport.Plan(cell, new ExportOptions { LibraryDir = Path.Combine(_dir, "lib") });
        Assert.Contains("TL1 (MLIN) needs a technology to be converted, and this schematic has none.", plan.Refusals);

        string plain = WriteResistorCell();
        var vm = new CircuitRF.Ui.ViewModels.ExportToLibraryViewModel(plain, [], Path.Combine(_dir, "lib2"));
        await vm.Replanned;
        Assert.False(vm.IsFrequencyPlanEnabled);
        Assert.Equal("No component uses the technology.", vm.ConversionLine);

        var cli = ModelLibraryExportTests.RunCliPublic("export", "part", plain, "--library", Path.Combine(_dir, "lib2"),
                                                       "--sweep", "start=1", "stop=2", "npts=3", "unit=GHz");
        Assert.Equal(1, cli.Exit);
        Assert.Contains("No component uses the technology", cli.StdErr);
        Assert.False(Directory.Exists(Path.Combine(_dir, "lib2", "TSTR")));

        // Over MCP, each sweep item is one --sweep segment.
        var call = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new
        {
            what = "libraryPart", cell = plain, library = Path.Combine(_dir, "lib2"),
            sweep = new[] { "start=1 stop=10 npts=10 unit=GHz", "start=12 stop=12 npts=1 unit=GHz" },
        }))!.AsObject();
        var argv = CircuitRF.Cli.Serve.ToolCatalog.ToArgv("create", call, CircuitRF.Cli.Serve.PathRoot.Open(_dir, out _)!, out var refusal);
        Assert.Null(refusal);
        Assert.Equal(["--sweep", "start=1 stop=10 npts=10 unit=GHz", "--sweep", "start=12 stop=12 npts=1 unit=GHz"],
                     argv!.SkipWhile(a => a != "--sweep").Take(4));
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>in — R1 — TL1 — T1 — TL2 — out, with T1's third arm to ground through V1: a line on the bias path.</summary>
    private string WriteBoardCell(string? technologyId, out string ws)
    {
        ws = WorkspaceCreate.Create(_dir, "ws-" + (technologyId ?? "none"), technologyId).WorkspaceDir;
        var sch = new SchematicEditModel();
        var p1 = Pin("P1", 1, "in", 0, 0);
        var r1 = Part("R1", SymbolKind.Resistor, 2000, 0, ("R", "20", "Ohm"));
        var tl1 = Part("TL1", SymbolKind.Mlin, 6000, 0, ("W", "1.1", "mm"), ("L", "8", "mm"));
        var t1 = Part("T1", SymbolKind.MTee, 10000, 0, ("W1", "1.1", "mm"), ("W2", "1.1", "mm"), ("W3", "0.5", "mm"));
        var tl2 = Part("TL2", SymbolKind.Mlin, 14000, 0, ("W", "1.1", "mm"), ("L", "5", "mm"));
        var v1 = Part("V1", SymbolKind.ViaGnd, 10000, 4000);
        var p2 = Pin("P2", 2, "out", 18000, 0);
        p2.MirrorX = true;   // an output pin faces left, toward its wire
        foreach (var c in new[] { p1, r1, tl1, t1, tl2, v1, p2 }) sch.Components.Add(c);
        Wire(sch, p1, 0, r1, 0);
        Wire(sch, r1, 1, tl1, 0);
        Wire(sch, tl1, 1, t1, 0);
        Wire(sch, t1, 1, tl2, 0);
        Wire(sch, tl2, 1, p2, 0);
        Wire(sch, t1, 2, v1, 0);
        return WriteCell(ws, "TSTBRD", sch);
    }

    private string WriteResistorCell()
    {
        string ws = WorkspaceCreate.Create(_dir, "ws-r", ShippedTechnologies.DefaultId).WorkspaceDir;
        var sch = new SchematicEditModel();
        var p1 = Pin("P1", 1, "a", 0, 0);
        var r = Part("R1", SymbolKind.Resistor, 2000, 2000, ("R", "50", "Ohm"));
        var p2 = Pin("P2", 2, "b", 4000, 0);
        p2.MirrorX = true;
        foreach (var c in new[] { p1, r, p2 }) sch.Components.Add(c);
        Wire(sch, p1, 0, r, 0);
        Wire(sch, r, 1, p2, 0);
        return WriteCell(ws, "TSTR", sch);
    }

    private static string WriteCell(string parent, string name, SchematicEditModel sch)
    {
        string cell = CellFolder.CreateCellFolder(parent, name);
        string schDir = CellFolder.SubFolderPath(cell, ViewType.Schematic);
        Directory.CreateDirectory(schDir);
        sch.SchematicDirectory = schDir;
        SchematicPersistence.SaveToFile(Path.Combine(schDir, name + ".csch"), sch);
        WorkspaceRootFinder.InvalidateCache();
        return cell;
    }

    private static EditableComponent Pin(string name, int num, string label, double x, double y)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Pin, X = x, Y = y };
        c.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString() });
        c.Parameters.Add(new EditableParameter { Name = "Name", Expression = label });
        return c;
    }

    /// <summary>A part with the registry's own defaults, the given values written over them.</summary>
    private static EditableComponent Part(string name, SymbolKind kind, double x, double y, params (string Name, string Value, string Unit)[] set)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = kind, X = x, Y = y };
        foreach (var d in ComponentTypeRegistry.DefaultParameters(kind, 2))
            c.Parameters.Add(new EditableParameter { Name = d.Name, Expression = d.Expression, Unit = d.Unit, ShowOnSchematic = d.ShowOnSchematic });
        foreach (var (n, v, u) in set)
        {
            var p = c.Parameters.FirstOrDefault(q => q.Name == n);
            if (p is null) c.Parameters.Add(p = new EditableParameter { Name = n });
            p.Expression = v;
            p.Unit = u;
        }
        return c;
    }

    private static void Wire(SchematicEditModel sch, EditableComponent a, int ka, EditableComponent b, int kb)
    {
        var pa = a.GetPortWorldCoord(ka);
        var pb = b.GetPortWorldCoord(kb);
        var w = new EditableWire();
        w.Points.Add((pa.X, pa.Y));
        if (pa.X != pb.X && pa.Y != pb.Y) w.Points.Add((pb.X, pa.Y));
        w.Points.Add((pb.X, pb.Y));
        sch.Wires.Add(w);
    }
}

using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using CircuitRF.Core.Design;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Diagnostics;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.WBond;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-em3d-2 — named materials and 3D bodies in the technology. §6's eight gates, in order,
//  plus the owner's temperature-table placeholders (SigmaVsTemp / ThermalKVsTemp).
//
//  The important one is gate 4: adding a Materials list and an overmold body to every example's
//  technology leaves every planar extraction identical, field by field. It compares the EXTRACTION
//  rather than a solve — the kernels are deterministic given their input — and solves one small
//  example end to end so the comparison cannot be missing a path.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class TechMaterialsTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _tmp = Path.Combine(
        Path.GetTempPath(), "crf-em3d2-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { /* best effort */ } }

    // ── 1. Round-trip: omit-when-empty ─────────────────────────────────────────────────────────

    /// <summary>
    /// Every shipped, example and test-data <c>.ctech</c>: the new keys appear on write only when
    /// the file stated them, and load∘save is a fixed point.
    ///
    /// <para>Serializer-to-serializer, not against the bytes on disk: no <c>.ctech</c> in the repo is
    /// in this writer's exact spelling (they were hand-written or written by an importer before the
    /// current writer's formatting), so the file itself was never the byte-identical half. What the
    /// new code could change is what the writer EMITS, and the only way it can is by emitting a key
    /// the file did not state — which is what this asserts cannot happen.</para>
    /// </summary>
    [Fact]
    public void Gate1_EveryCtechRoundTrips_AndGainsNoKeyItDidNotState()
    {
        string root = RepoRoot();
        var files = new[] { "src/Design/resources/technologies", "examples", "testdata" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.ctech", SearchOption.AllDirectories))
            .ToList();
        Assert.True(files.Count >= 10, $"only {files.Count} .ctech files found");

        foreach (string file in files)
        {
            string onDisk = File.ReadAllText(file);
            string once   = TechPersistence.Serialize(TechPersistence.Deserialize(onDisk));
            Assert.Equal(once, TechPersistence.Serialize(TechPersistence.Deserialize(once)));

            foreach (string key in new[] { "\"Materials\"", "\"Bodies\"", "\"Material\"" })
                if (!onDisk.Contains(key, StringComparison.Ordinal))
                    Assert.DoesNotContain(key, once, StringComparison.Ordinal);
        }
    }

    // ── 2. Resolve on read ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_AnEntryNamingAMaterialLoadsWithTheMaterialsValues_AndTheExtractorSeesThem()
    {
        var (setup, source) = ResolveExample("Klopfenstein Taper", "Taper-MoM.cem");
        var tech = TechPersistence.Deserialize(NamedSubstrateJson(source.Technology!));

        var sub = tech.Stackup.Layers.Single(l => l.Kind == StackupKind.Dielectric);
        Assert.Equal(4.4, sub.Epsr);

        var geometry = EmGeometry.ForSetup(setup, source with { Technology = tech });
        var planar = PlanarExtractor.Extract(geometry.Shapes, tech, source.DbuPerMicron,
            setup.Frequency.Expand().Max(), setup.ToExtractionSettings(setup.LayoutRef), geometry.GeneratorIds);
        Assert.True(planar.Ok, planar.Refusal);

        var epsr = new List<double> { planar.Problem!.Slab.Material.EpsR };
        if (planar.Problem.MediumStack is { } stack) epsr.AddRange(stack.Layers.Select(l => l.Material.EpsR));
        Assert.Contains(4.4, epsr);
        Assert.DoesNotContain(3.0, epsr);
    }

    // ── 3. tech.material.disagrees, through check ───────────────────────────────────────────────

    [Fact]
    public void Gate3_DisagreesFiresOnTheHandEditedFile_AndNotAfterAGuiPathSave()
    {
        var (_, source) = ResolveExample("Klopfenstein Taper", "Taper-MoM.cem");
        Directory.CreateDirectory(_tmp);
        string path = Path.Combine(_tmp, "named.ctech");
        File.WriteAllText(path, NamedSubstrateJson(source.Technology!));

        var before = RunCheck(path);
        var hit = Assert.Single(Rules(before), r => r.Rule == TechValidation.Ids.MaterialDisagrees);
        Assert.Equal("warning", hit.Severity);

        // The editor's Save is TechPersistence.SaveToFile(FilePath, Working), and Working was loaded
        // through the resolving reader — so the numbers it writes are the material's.
        TechPersistence.SaveToFile(path, TechPersistence.LoadFromFile(path));
        Assert.DoesNotContain(Rules(RunCheck(path)), r => r.Rule == TechValidation.Ids.MaterialDisagrees);
    }

    // ── 4. The planar gate ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// For every <c>.cem</c> under <c>examples/</c>, both extractors' output with the example's own
    /// technology, and with the same technology plus a Materials list and an overmold body (through
    /// the writer and the reader), are identical in every public field.
    /// </summary>
    [Fact]
    public void Gate4_MaterialsAndAnOvermoldBodyChangeNoPlanarExtraction()
    {
        var cems = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "examples"), "*.cem", SearchOption.AllDirectories)
                            .ToList();
        Assert.NotEmpty(cems);

        foreach (string cem in cems)
        {
            var (setup, source) = Resolve(cem);
            var plus = WithMaterialsAndOvermold(source.Technology!);
            Assert.NotEmpty(plus.Bodies);

            string a = DumpExtraction(setup, source);
            string b = DumpExtraction(setup, source with { Technology = plus });
            output.WriteLine($"{Path.GetFileName(cem)}: {a.Length} chars of extraction compared");
            Assert.True(a == b, $"{cem}: extraction changed when materials and a body were added");
        }
    }

    /// <summary>
    /// The same comparison, solved end to end by the planar kernel and compared as written
    /// <c>.sNp</c> bytes (all but the provenance write-timestamp), on the Klopfenstein example's
    /// technology. The geometry is a 2 mm line at one frequency rather than the taper itself: the
    /// question is whether any path from the technology to the answer differs, and the taper's own
    /// solve is 16 s of the same code.
    ///
    /// <para><b>Benchmark tier</b>: two planar runs measured 6-8 s in a Debug build whatever the
    /// line's length or the point count — per-run fixed cost, not geometry. The routine half of this
    /// gate is the extraction comparison above, which covers every example.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void Gate4_OneSmallPlanarSolveEndToEnd_WritesTheSameSnp()
    {
        var (_, example) = ResolveExample("Klopfenstein Taper", "Taper-MoM.cem");
        var view = new LayoutView { DbuPerMicron = example.DbuPerMicron };
        view.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = -850_000, X2 = 2_000_000, Y2 = 850_000 });
        foreach (var (x, text, dir) in new[] { (0L, "1", LayoutRotation.R0), (2_000_000L, "2", LayoutRotation.R180) })
            view.Shapes.Add(new LabelShape { Layer = new(1, 0), X = x, Y = 0, Text = text, Height = 300_000,
                                             IsPort = true, PortDirection = dir });
        var source = new EmLayoutSource(Path.Combine(_tmp, "Line.clay"), view, example.Technology, example.DbuPerMicron);
        var setup  = new EmSetup
        {
            Name = "line", LayoutRef = "Line.clay", AnalysisKind = EmAnalysisKind.Planar,
            Frequency = new FrequencySpec("1", "1", 1, SweepKind.Linear, "GHz", "GHz"),
        };

        var plain = EmRunService.Run(setup, source, Path.Combine(_tmp, "a"));
        var plus  = EmRunService.Run(setup, source with { Technology = WithMaterialsAndOvermold(example.Technology!) },
                                     Path.Combine(_tmp, "b"));
        Assert.True(plain.Status == EmRunStatus.Ok, plain.Error);
        Assert.Equal(EmRunStatus.Ok, plus.Status);

        const string stamp = "circuitRF-EM written:";
        string[] A = File.ReadAllLines(plain.SnpPath!), B = File.ReadAllLines(plus.SnpPath!);
        Assert.Contains(A, l => l.Contains(stamp, StringComparison.Ordinal));
        Assert.Equal(A.Where(l => !l.Contains(stamp, StringComparison.Ordinal)),
                     B.Where(l => !l.Contains(stamp, StringComparison.Ordinal)));
    }

    // ── 5. One door ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// No compiled method outside the files §2b lists calls <c>StackupLayer.get_Material</c>. Read
    /// from the IL rather than the source text: ~30 unrelated types have a property called
    /// <c>Material</c>, and a text scan could not tell them apart.
    /// </summary>
    [Fact]
    public void Gate5_OnlyTheListedFilesReadStackupLayerMaterial()
    {
        string[] allowed =
        [
            "CircuitRF.Design.Layout.TechPersistence",
            "CircuitRF.Design.Layout.TechValidation",
            "CircuitRF.Ui.Layout.StackupLayerRowViewModel",
            "CircuitRF.Ui.Controls.StackupInlineEditor",
        ];

        var readers = new List<string>();
        foreach (string dll in Directory.EnumerateFiles(AppContext.BaseDirectory, "CircuitRF.*.dll"))
        {
            if (Path.GetFileName(dll).Contains(".Tests", StringComparison.Ordinal)) continue;
            readers.AddRange(MaterialGetterCallers(dll));
        }

        // Not vacuous: the loader's own read is found.
        Assert.Contains(readers, r => r.StartsWith("CircuitRF.Design.Layout.TechPersistence", StringComparison.Ordinal));
        var outside = readers.Where(r => !allowed.Any(a => r.StartsWith(a, StringComparison.Ordinal))).ToList();
        Assert.True(outside.Count == 0, "StackupLayer.Material read outside the one door: " + string.Join(", ", outside));
    }

    // ── 6. Every check id ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(TechValidation.Ids.MaterialUnknown,         DiagnosticSeverity.Error)]
    [InlineData(TechValidation.Ids.MaterialDuplicate,       DiagnosticSeverity.Error)]
    [InlineData(TechValidation.Ids.MaterialMissingProperty, DiagnosticSeverity.Error)]
    [InlineData(TechValidation.Ids.MaterialPartial,         DiagnosticSeverity.Info)]
    [InlineData(TechValidation.Ids.MaterialInvalid,         DiagnosticSeverity.Error)]
    [InlineData(TechValidation.Ids.MaterialNotReadYet,      DiagnosticSeverity.Info)]
    [InlineData(TechValidation.Ids.BodySitsOnUnknown,       DiagnosticSeverity.Error)]
    [InlineData(TechValidation.Ids.BodyNameClash,           DiagnosticSeverity.Error)]
    [InlineData(TechValidation.Ids.BodyOutlineLayerUnknown, DiagnosticSeverity.Error)]
    public void Gate6_EachRuleFires_AtItsSeverity_AndAShippedTechnologyRaisesNone(string id, DiagnosticSeverity severity)
    {
        var clean = Shipped();
        Assert.DoesNotContain(TechValidation.Analyze(clean), p => p.Id is not null);

        var tech = Shipped();
        var diel = tech.Stackup.Layers.First(l => l.Kind == StackupKind.Dielectric);
        switch (id)
        {
            case TechValidation.Ids.MaterialUnknown:
                diel.Material = "Unobtainium"; break;
            case TechValidation.Ids.MaterialDuplicate:
                tech.Materials.Add(new TechMaterial { Name = "gold", Sigma20 = 1 }); break;
            case TechValidation.Ids.MaterialMissingProperty:
                diel.Material = "Gold"; break;                             // a metal has no εr
            case TechValidation.Ids.MaterialPartial:
                tech.Materials.Add(new TechMaterial { Name = "Epsr only", Epsr = 3 });
                diel.Material = "Epsr only"; break;
            case TechValidation.Ids.MaterialInvalid:
                tech.FindMaterial("Gold")!.SigmaVsTemp =
                    [new() { TempC = 85, Value = 3.3e7 }, new() { TempC = 20, Value = 4.1e7 }]; break;
            case TechValidation.Ids.MaterialNotReadYet:
                tech.FindMaterial("Gold")!.ThermalKVsTemp =
                    [new() { TempC = 20, Value = 317 }, new() { TempC = 200, Value = 309 }]; break;
            case TechValidation.Ids.BodySitsOnUnknown:
                tech.Bodies.Add(new TechBody { Name = "Lid", Material = "Gold", SitsOn = "Nowhere", ThicknessDbu = 1 }); break;
            case TechValidation.Ids.BodyNameClash:
                tech.Bodies.Add(new TechBody { Name = diel.Name, Material = "Gold", SitsOn = diel.Name, ThicknessDbu = 1 }); break;
            case TechValidation.Ids.BodyOutlineLayerUnknown:
                tech.Bodies.Add(new TechBody { Name = "Lid", Material = "Gold", SitsOn = diel.Name, ThicknessDbu = 1,
                                               OutlineLayers = [new(999, 99)] }); break;
        }

        var hit = Assert.Single(TechValidation.Analyze(tech), p => p.Id == id);
        Assert.Equal(severity, hit.Severity);
        output.WriteLine(hit.Message);
    }

    /// <summary>R-em3d2-4a: disagrees is computed from the file as written — so on a loaded
    /// technology it never fires, and on the raw one it does.</summary>
    [Fact]
    public void Gate6_DisagreesIsARawRule()
    {
        var (_, source) = ResolveExample("Klopfenstein Taper", "Taper-MoM.cem");
        string json = NamedSubstrateJson(source.Technology!);

        Assert.DoesNotContain(TechValidation.AnalyzeRaw(TechPersistence.Deserialize(json)), p => p.Id is not null);
        var hit = Assert.Single(TechValidation.AnalyzeRaw(TechPersistence.DeserializeUnresolved(json)));
        Assert.Equal(TechValidation.Ids.MaterialDisagrees, hit.Id);
        Assert.Equal(DiagnosticSeverity.Warning, hit.Severity);
    }

    // ── 7. Code and technology agree on the four wire metals ────────────────────────────────────

    [Fact]
    public void Gate7_EveryShippedTechnologyCarriesTheWireMetalsExactly()
    {
        foreach (var entry in ShippedTechnologies.All)
        {
            var tech = ShippedTechnologies.Load(entry);
            foreach (var w in WireMaterials.All)
            {
                var m = tech.FindMaterial(w.Name);
                Assert.True(m is not null, $"{entry.Id} has no material \"{w.Name}\"");
                Assert.Equal(w.Sigma20, m!.Sigma20);
                Assert.Equal(w.Alpha20, m.Alpha20);
                Assert.Equal(w.DensityKgM3, m.DensityKgM3);
            }
        }
    }

    // ── 8. The reference page ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate8_ReferenceTechnologyDescribesTheNewFields()
    {
        string page = CircuitRF.Cli.DocumentSchema.Render(CircuitRF.Cli.DocumentSchema.Find("technology")!);
        foreach (string field in new[] { "Materials", "Material", "Bodies", "SigmaVsTemp", "ThermalKVsTemp" })
            Assert.Contains(field, page, StringComparison.Ordinal);
    }

    // ── The stackup editor (R-em3d2-5c) ─────────────────────────────────────────────────────────

    [Fact]
    public void Editor_NamingAMaterialLocksWhatItStates_AndNoneKeepsTheValuesShowing()
    {
        var tech = Shipped();
        tech.Materials.Add(new TechMaterial { Name = "Sub", Epsr = 4.4 });
        var vm  = new TechEditorViewModel(Path.Combine(_tmp, "t.ctech"), tech);
        var row = vm.StackupLayers.First(r => r.IsDielectric);

        row.SelectedMaterial = "Sub";
        row = vm.StackupLayers.First(r => r.IsDielectric);                // rows rebuild on commit
        Assert.True(row.EpsrLocked);
        Assert.False(row.TanDLocked);                                     // the material leaves tanδ to the entry
        Assert.Equal("4.4", row.StagedEpsr);

        row.StagedEpsr = "9";
        row.CommitEpsr();                                                 // refused: the material's value
        Assert.Equal(4.4, vm.Working.Stackup.Layers.First(l => l.Kind == StackupKind.Dielectric).Epsr);

        row.SelectedMaterial = StackupLayerRowViewModel.MaterialNone;
        row = vm.StackupLayers.First(r => r.IsDielectric);
        Assert.False(row.EpsrLocked);
        Assert.Equal("4.4", row.StagedEpsr);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static Technology Shipped()
        => ShippedTechnologies.Load(ShippedTechnologies.All.First(e => e.Id.StartsWith("pcb-2layer_FR-4", StringComparison.Ordinal)));

    /// <summary>The technology with its one dielectric naming a material of εr 4.4 while its own
    /// number says 3.0 — the file a hand edit produces.</summary>
    private static string NamedSubstrateJson(Technology original)
    {
        var t = TechPersistence.Deserialize(TechPersistence.Serialize(original));
        var sub = t.Stackup.Layers.Single(l => l.Kind == StackupKind.Dielectric);
        t.Materials.Add(new TechMaterial { Name = "Sub", Epsr = 4.4, TanD = sub.TanD, Mur = sub.Mur });
        sub.Material = "Sub";
        sub.Epsr     = 3.0;
        return TechPersistence.Serialize(t);   // Serialize writes the model as it stands: 3.0 and "Sub"
    }

    /// <summary>A Materials list and an overmold body — through the writer and the reader, so what
    /// the extractors see is what a file carrying them loads as.</summary>
    private static Technology WithMaterialsAndOvermold(Technology original)
    {
        var t = TechPersistence.Deserialize(TechPersistence.Serialize(original));
        t.Materials.AddRange(WireMaterials.All.Select(w => new TechMaterial
            { Name = w.Name, Sigma20 = w.Sigma20, Alpha20 = w.Alpha20, DensityKgM3 = w.DensityKgM3 }));
        t.Materials.Add(new TechMaterial { Name = "Mould compound", Epsr = 3.9, TanD = 0.005, ThermalK = 0.9 });
        t.Bodies.Add(new TechBody
        {
            Name = "Overmold", Material = "Mould compound",
            SitsOn = t.Stackup.Layers.First(l => l.Kind != StackupKind.Via).Name,
            ThicknessDbu = 500_000,
        });
        return TechPersistence.Deserialize(TechPersistence.Serialize(t));
    }

    private static string DumpExtraction(EmSetup setup, EmLayoutSource source)
    {
        var geometry = EmGeometry.ForSetup(setup, source);
        double fMax  = setup.Frequency.Expand().Max();
        var settings = setup.ToExtractionSettings(setup.LayoutRef);
        var sb = new StringBuilder();
        Dump(CrossSectionExtractor.Extract(geometry.Shapes, source.Technology!, source.DbuPerMicron, settings), sb);
        sb.Append("\n---\n");
        Dump(PlanarExtractor.Extract(geometry.Shapes, source.Technology!, source.DbuPerMicron, fMax, settings,
                                     geometry.GeneratorIds), sb);
        return sb.ToString();
    }

    /// <summary>
    /// Every stored FIELD, recursively, as text — a field-by-field comparison of two object graphs
    /// that needs no knowledge of the extractors' types. Fields rather than properties: a computed
    /// property can return a fresh object on every call (a reversed polygon's reverse, …), which no
    /// seen-set can bound; the fields are the state, and a record's properties are its fields.
    /// </summary>
    private static void Dump(object? o, StringBuilder sb, int depth = 0, HashSet<object>? seen = null)
    {
        seen ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (depth > 40) { sb.Append("<deep>"); return; }
        switch (o)
        {
            case null: sb.Append("null"); return;
            case string s: sb.Append('"').Append(s).Append('"'); return;
            case double d: sb.Append(d.ToString("R", inv)); return;
            case float f: sb.Append(f.ToString("R", inv)); return;
            case System.Numerics.Complex c:
                sb.Append(c.Real.ToString("R", inv)).Append('+').Append(c.Imaginary.ToString("R", inv)).Append('j');
                return;
            case Delegate or Type or System.Threading.Lock: sb.Append(o.GetType().Name); return;
        }
        var type = o.GetType();
        if (type.IsPrimitive || type.IsEnum || o is decimal) { sb.Append(Convert.ToString(o, inv)); return; }
        if (!type.IsValueType && !seen.Add(o)) { sb.Append("<seen>"); return; }

        if (o is Array or IList or ICollection)
        {
            sb.Append('[');
            foreach (var item in (IEnumerable)o) { Dump(item, sb, depth + 1, seen); sb.Append(','); }
            sb.Append(']');
            return;
        }

        sb.Append(type.Name).Append('{');
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                          | BindingFlags.DeclaredOnly)
                               .OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                sb.Append(f.Name).Append('=');
                Dump(f.GetValue(o), sb, depth + 1, seen);
                sb.Append(';');
            }
        sb.Append('}');
    }

    /// <summary>Every method in <paramref name="dll"/> whose IL calls <c>StackupLayer.get_Material</c>,
    /// as "DeclaringType::Method".</summary>
    private static IEnumerable<string> MaterialGetterCallers(string dll)
    {
        using var pe = new PEReader(File.OpenRead(dll));
        if (!pe.HasMetadata) yield break;
        var md = pe.GetMetadataReader();

        var targets = new HashSet<int>();
        foreach (var h in md.MethodDefinitions)
        {
            var m = md.GetMethodDefinition(h);
            var t = md.GetTypeDefinition(m.GetDeclaringType());
            if (md.GetString(m.Name) == "get_Material" && md.GetString(t.Name) == "StackupLayer")
                targets.Add(MetadataTokens.GetToken(h));
        }
        foreach (var h in md.MemberReferences)
        {
            var r = md.GetMemberReference(h);
            if (md.GetString(r.Name) != "get_Material" || r.Parent.Kind != HandleKind.TypeReference) continue;
            var tr = md.GetTypeReference((TypeReferenceHandle)r.Parent);
            if (md.GetString(tr.Name) == "StackupLayer" && md.GetString(tr.Namespace) == "CircuitRF.Design.Layout")
                targets.Add(MetadataTokens.GetToken(h));
        }
        if (targets.Count == 0) yield break;

        foreach (var th in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(th);
            foreach (var mh in type.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                if (m.RelativeVirtualAddress == 0) continue;
                var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes();
                if (il is null) continue;
                for (int i = 0; i + 4 < il.Length; i++)
                {
                    if (il[i] != 0x28 && il[i] != 0x6F) continue;              // call / callvirt
                    if (!targets.Contains(BitConverter.ToInt32(il, i + 1))) continue;
                    yield return $"{FullName(md, type)}::{md.GetString(m.Name)}";
                    break;
                }
            }
        }
    }

    private static string FullName(MetadataReader md, TypeDefinition t)
    {
        var declaring = t.GetDeclaringType();
        return declaring.IsNil
            ? $"{md.GetString(t.Namespace)}.{md.GetString(t.Name)}"
            : $"{FullName(md, md.GetTypeDefinition(declaring))}+{md.GetString(t.Name)}";
    }

    private static (EmSetup Setup, EmLayoutSource Source) ResolveExample(string example, string cemName)
        => Resolve(Directory.EnumerateFiles(Path.Combine(RepoRoot(), "examples", example), cemName,
                                            SearchOption.AllDirectories).Single());

    private static (EmSetup Setup, EmLayoutSource Source) Resolve(string cem)
    {
        var setup = EmSetupPersistence.LoadFromFile(cem);
        string? dir = Path.GetDirectoryName(cem), cws = null;
        for (; dir is not null && cws is null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, ".cws"))) cws = Path.Combine(dir, ".cws");

        var resolved = EmSetupResolver.Resolve(cem, setup.LayoutRef, cws, new TechnologyCache());
        Assert.True(resolved.Source?.Technology is not null, $"{cem} did not resolve a technology");
        return (setup, resolved.Source!);
    }

    private readonly record struct RuleHit(string? Rule, string? Severity);

    private static IEnumerable<RuleHit> Rules(JsonDocument doc)
        => doc.RootElement.GetProperty("diagnostics").EnumerateArray()
              .Where(d => d.GetProperty("id").GetString() == "check.tech.problem")
              .Select(d => new RuleHit(
                  d.GetProperty("arguments").TryGetProperty("rule", out var r) && r.ValueKind == JsonValueKind.String
                      ? r.GetString() : null,
                  d.GetProperty("severity").GetString()))
              .ToList();

    private JsonDocument RunCheck(string path)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in new[] { "check", path, "--json" }) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        output.WriteLine(errTask.GetAwaiter().GetResult());
        return JsonDocument.Parse(outTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = typeof(TechMaterialsTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }

    private static string Lf(string s) => s.Replace("\r\n", "\n");

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }
}

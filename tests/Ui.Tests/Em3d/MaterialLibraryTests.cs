using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-em3d-53 — material libraries (.cmat): the format, resolution, the shipped generic library and
//  the CLI. The view models are MaterialsEditorTests. One test per claim; gate numbers are the brief's §9.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class MaterialLibraryTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-em3d53-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { /* best effort */ } }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A technology with one dielectric entry naming <paramref name="entryMaterial"/>, its own materials, and
    /// the libraries named; written to <c>&lt;dir&gt;/tech.ctech</c>.</summary>
    private string Tech(string dir, string? entryMaterial, IEnumerable<TechMaterial> own, params string[] libraries)
    {
        Directory.CreateDirectory(dir);
        var tech = new Technology
        {
            Name = "t",
            Materials = [.. own],
            MaterialLibraries = libraries.Length > 0 ? [.. libraries] : null,
            Stackup = new Stackup
            {
                Layers =
                [
                    new StackupLayer { Name = "Top", Kind = StackupKind.Conductor, ThicknessDbu = 35_000, SigmaSm = 5.8e7, IsGroundReference = true },
                    new StackupLayer { Name = "Sub", Kind = StackupKind.Dielectric, ThicknessDbu = 500_000, Epsr = 1, Mur = 1, Material = entryMaterial },
                ],
            },
        };
        string path = Path.Combine(dir, "tech.ctech");
        TechPersistence.SaveToFile(path, tech);
        return path;
    }

    private static string Lib(string path, params TechMaterial[] materials)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        MaterialLibraryPersistence.SaveToFile(path, materials);
        return path;
    }

    private static TechMaterial Alumina(double epsr = 9.8) => new() { Name = "Alumina", Epsr = epsr, TanD = 3e-4, Mur = 1 };

    // ── 1. Round trip ────────────────────────────────────────────────────────────────────────

    /// <summary>An unknown key survives a save in a .cmat and in a .ctech (one type), and a record cut from a .ctech
    /// reads identically in a .cmat. Every repo .ctech is still a fixed point and gains no key it did not state.</summary>
    [Fact]
    public void Gate1_UnknownKeysSurvive_OneRecordReadsInBothFiles_EveryCtechIsAFixedPoint()
    {
        const string record = """{ "Name": "Lid", "Epsr": 3.9, "FutureKey": { "a": 1 } }""";
        string cmat = $$"""{ "FormatVersion": 1, "Materials": [ {{record}} ] }""";
        var fromLib = MaterialLibraryPersistence.Deserialize(cmat);
        Assert.Contains("\"FutureKey\"", MaterialLibraryPersistence.Serialize(fromLib));

        var tech = TechPersistence.Deserialize(TechPersistence.Serialize(new Technology { Name = "t" }).TrimEnd()[..^1] +
                                               $$""", "Materials": [ {{record}} ] }""");
        Assert.Contains("\"FutureKey\"", TechPersistence.Serialize(tech));
        Assert.Equal(MaterialLibraryPersistence.Serialize(fromLib), MaterialLibraryPersistence.Serialize(tech.Materials));

        string root = RepoRoot();
        foreach (string file in new[] { "src/Design/resources/technologies", "examples", "testdata" }
                     .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.ctech", SearchOption.AllDirectories)))
        {
            string onDisk = File.ReadAllText(file);
            string once = TechPersistence.Serialize(TechPersistence.Deserialize(onDisk));
            Assert.Equal(once, TechPersistence.Serialize(TechPersistence.Deserialize(once)));
            if (!onDisk.Contains("\"MaterialLibraries\"", StringComparison.Ordinal))
                Assert.DoesNotContain("\"MaterialLibraries\"", once, StringComparison.Ordinal);
        }

        // One material type: nothing else in the Design assembly carries a material's εr and σ₂₀.
        var others = typeof(TechMaterial).Assembly.GetTypes()
            .Where(t => t != typeof(TechMaterial) && t.GetProperty("Epsr") is not null && t.GetProperty("Sigma20") is not null).ToList();
        Assert.Empty(others);
    }

    // ── 2. A technology save never writes library records (§1b) ────────────────────────────────

    [Fact]
    public void Gate2_SavingATechnologyNamingALibraryWritesNoLibraryRecord_AndTheMaterialsReadersAreTheListedOnes()
    {
        Lib(Path.Combine(_tmp, "lib.cmat"), Alumina());
        string path = Tech(_tmp, "Alumina", [], "lib.cmat");
        // Saved once as the editor saves (the entry's own numbers then equal the library's, as with an own material).
        TechPersistence.SaveToFile(path, TechPersistence.LoadFromFile(path));
        byte[] before = File.ReadAllBytes(path);

        var tech = TechPersistence.LoadFromFile(path);
        Assert.Equal(9.8, tech.Stackup.Layers[1].Epsr);                           // the library answered
        Assert.Single(tech.ResolvedMaterials);
        TechPersistence.SaveToFile(path, tech);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.DoesNotContain("\"Name\": \"Alumina\"", File.ReadAllText(path));
        Assert.Equal(TechPersistence.Serialize(tech), TechPersistence.Serialize(TechPersistence.Clone(tech)));
        Assert.Single(TechPersistence.Clone(tech).ResolvedMaterials);             // a clone keeps what the library gave

        // Every reader of Technology.Materials — what the file STORES — is one of these. A new one is a conscious
        // choice: "every material this technology can name" is ResolvedMaterials / FindMaterial.
        string[] allowed =
        [
            "CircuitRF.Design.Layout.TechPersistence",        // persistence, and ResolveMaterials' short-cut
            "CircuitRF.Design.Layout.Technology",             // ResolvedMaterials, FindMaterial, LibrarySourceOf
            "CircuitRF.Design.Layout.TechValidation",         // the own list's self rules
            "CircuitRF.Design.Layout.MaterialLibraries",      // the own list as one source of a conflict
            "CircuitRF.Design.Layout.Interchange.PcbStackupMapping", // a WRITE: an import whose technology resolves no Copper
                                                              // (FindMaterial, libraries included) adds the generic record
                                                              // to the own list, which the saved file must carry
            "CircuitRF.Cli.Check",                            // MaterialValidation over a refused file's own list
            "CircuitRF.Cli.Explain",                          // "N own and M from libraries"
            "CircuitRF.Design.ThreeD.C3dRunDocument",         // a run input's physics hash: the FILE's records (brief-em3d-105)
            "CircuitRF.Design.ThreeD.Appearance.AppearanceResolver", // a previewed look's technology keeps own and library apart, so
                                                              // the provenance still names the library (brief-em3d-108)
            "CircuitRF.Ui.Layout.TechEditorViewModel",        // the Materials tab's own rows
            "CircuitRF.Ui.ThreeD.C3dEditorViewModel",         // a WRITE: Flatten's plan, re-run on a CLONE whose own list is given
                                                              // the materials the instance lacks (WithMissingMaterials)
            "CircuitRF.Ui.ViewModels.WorkspaceViewModel",     // own-vs-library in a rename that spans files
        ];
        var readers = new List<string>();
        foreach (string dll in Directory.EnumerateFiles(AppContext.BaseDirectory, "CircuitRF.*.dll"))
        {
            if (Path.GetFileName(dll).Contains(".Tests", StringComparison.Ordinal)) continue;
            readers.AddRange(TechMaterialsTests.GetterCallers(dll, "Technology", "get_Materials"));
        }
        var types = readers.Select(r => r[..r.IndexOf("::", StringComparison.Ordinal)].Split('+')[0]).Distinct().Order().ToList();
        output.WriteLine(string.Join("\n", types));
        Assert.Contains("CircuitRF.Design.Layout.TechPersistence", types);
        Assert.Equal(allowed.Order(StringComparer.Ordinal), types.Order(StringComparer.Ordinal));
    }

    // ── 4. Resolution (§2, §3) ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_LibraryOnly_AndOwnPlusLibraryEqual_Load_AndExplainNamesBothFiles()
    {
        Lib(Path.Combine(_tmp, "a", "lib.cmat"), Alumina());
        var only = TechPersistence.LoadFromFile(Tech(Path.Combine(_tmp, "a"), "Alumina", [], "lib.cmat"));
        Assert.Equal(Path.Combine(_tmp, "a", "lib.cmat"), only.LibrarySourceOf("Alumina"));

        Lib(Path.Combine(_tmp, "b", "lib.cmat"), Alumina());
        string both = Tech(Path.Combine(_tmp, "b"), "Alumina", [Alumina()], "lib.cmat");
        var tech = TechPersistence.LoadFromFile(both);
        Assert.Single(tech.ResolvedMaterials);
        Assert.Null(tech.LibrarySourceOf("Alumina"));                              // own list first

        var (exit, stdout, _) = RunCli("explain", both);
        Assert.Equal(0, exit);
        Assert.Contains("lib.cmat", stdout);
        Assert.Contains("'Alumina'", stdout);
    }

    /// <summary>Different values, own vs library and library vs library, refuse to load — naming the material, each
    /// file and each value — through the resolver's diagnostic, `check`'s material.conflict and a .c3d elaboration.</summary>
    [Fact]
    public void Gate4_ADifferentValueIsARefusal_EverywhereTheTechnologyIsUsed()
    {
        Lib(Path.Combine(_tmp, "ws", "tech", "lib.cmat"), Alumina(9.6));
        string ctech = Tech(Path.Combine(_tmp, "ws", "tech"), "Alumina", [Alumina(9.8)], "lib.cmat");
        var ex = Assert.Throws<MaterialLibraryException>(() => TechPersistence.LoadFromFile(ctech));
        var p = Assert.Single(ex.Problems);
        Assert.Equal(MaterialLibraries.ConflictId, p.Id);
        Assert.Contains("'Alumina'", p.Message);
        Assert.Contains("9.8", p.Message);
        Assert.Contains("9.6", p.Message);
        Assert.Contains(ctech, p.Message);
        Assert.Contains("lib.cmat", p.Message);

        var res = TechnologyResolver.Resolve("tech/tech.ctech", Path.Combine(_tmp, "ws"), null, null, new TechnologyCache());
        Assert.Null(res.Tech);
        Assert.Contains(res.Diagnostics, d => d.Contains("defined differently", StringComparison.Ordinal));

        var hits = CheckRules(ctech);
        Assert.Contains(hits, h => h.Rule == MaterialLibraries.ConflictId && h.Severity == "error");

        // Two libraries disagreeing, the same way.
        Lib(Path.Combine(_tmp, "two", "a.cmat"), Alumina(9.6));
        Lib(Path.Combine(_tmp, "two", "b.cmat"), Alumina(9.9));
        var ex2 = Assert.Throws<MaterialLibraryException>(() => TechPersistence.LoadFromFile(Tech(Path.Combine(_tmp, "two"), null, [], "a.cmat", "b.cmat")));
        Assert.Equal(MaterialLibraries.ConflictId, Assert.Single(ex2.Problems).Id);

        // A .c3d on the refused technology: its object's material is not there to be found, and the refusal is said.
        WorkspacePersistence.SaveToFile(Path.Combine(_tmp, "ws", ".cws"), new CwsFile { DefaultTechRef = "tech/tech.ctech" });
        string c3d = Path.Combine(_tmp, "ws", "cell", "3d", "cell.c3d");
        Directory.CreateDirectory(Path.GetDirectoryName(c3d)!);
        C3dPersistence.SaveToFile(c3d, new C3dDocument
        {
            Objects = [new C3dBox { Name = "b", Material = "Alumina", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(1000, 1000, 1000) }],
        });
        var e = new C3dElaborator().Elaborate(C3dPersistence.LoadFromFile(c3d), c3d, null);
        Assert.Contains(e.Notes.Concat(e.Refusals), n => n.Contains("defined differently", StringComparison.Ordinal));
    }

    [Fact]
    public void Gate4_AMissingLibraryIsARefusalNamingThePathAndTheTechnology_NeverAnEmptyList()
    {
        string ctech = Tech(_tmp, "Alumina", [], "gone.cmat");
        var ex = Assert.Throws<MaterialLibraryException>(() => TechPersistence.LoadFromFile(ctech));
        var p = Assert.Single(ex.Problems);
        Assert.Equal(MaterialLibraries.LibraryMissingId, p.Id);
        Assert.Contains(Path.Combine(_tmp, "gone.cmat"), p.Message);
        Assert.Contains(ctech, p.Message);
    }

    /// <summary>A technology resolves identically from two workspaces: the anchor is the .ctech's own directory.</summary>
    [Fact]
    public void Gate4_TheSameTechnologyResolvedFromTwoWorkspacesIsIdentical()
    {
        Lib(Path.Combine(_tmp, "shared", "lib.cmat"), Alumina());
        Tech(Path.Combine(_tmp, "shared"), "Alumina", [], "lib.cmat");
        foreach (string ws in new[] { "ws1", "ws2/deeper" })
            if (Directory.CreateDirectory(Path.Combine(_tmp, ws)) is not null)
                WorkspacePersistence.SaveToFile(Path.Combine(_tmp, ws, ".cws"),
                new CwsFile { DefaultTechRef = Path.GetRelativePath(Path.Combine(_tmp, ws), Path.Combine(_tmp, "shared", "tech.ctech")) });
        string Resolved(string ws)
        {
            var (r, _) = TechnologyResolver.ResolveForDocument(null, Path.Combine(_tmp, ws, "x.clay"), null, new TechnologyCache());
            return string.Join("|", r.Tech!.ResolvedMaterials.Select(m => m.Name + "@" + r.Tech.LibrarySourceOf(m.Name)))
                 + TechPersistence.Serialize(r.Tech);
        }
        Assert.Equal(Resolved("ws1"), Resolved("ws2/deeper"));
    }

    // ── 5. The role column is the elaborator's rule (§4) ───────────────────────────────────────

    [Fact]
    public void Gate5_TheImpliedRoleIsTheElaboratorsRule_AndTheElaboratorNoLongerReadsTheFieldsItself()
    {
        Assert.Equal(C3dImpliedRole.Conductor, C3dMaterialRole.Implied(new TechMaterial { Name = "Au", Sigma20 = 4e7 }));
        Assert.Equal(C3dImpliedRole.Dielectric, C3dMaterialRole.Implied(new TechMaterial { Name = "Al2O3", EpsrTensor = [9, 9, 11] }));
        Assert.Equal(C3dImpliedRole.Air, C3dMaterialRole.Implied(new TechMaterial { Name = "air", Sigma20 = 1, Epsr = 1 }));
        Assert.Equal(C3dImpliedRole.Ambiguous, C3dMaterialRole.Implied(new TechMaterial { Name = "Si", Sigma20 = 10, Epsr = 11.9 }));
        Assert.Equal(C3dImpliedRole.Unstated, C3dMaterialRole.Implied(new TechMaterial { Name = "Material1" }));

        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src/Design/ThreeD/C3dElaborator.cs"));
        var role = Regex.Match(src, @"private Em3dRole\? Role\(.*?\n        \}", RegexOptions.Singleline).Value;
        Assert.Contains("C3dMaterialRole.Implied", role);
        Assert.DoesNotContain("Sigma20", role);
        Assert.DoesNotContain("Epsr", role);
    }

    // ── 6. Shared validation; '@' reserved ───────────────────────────────────────────────────

    [Fact]
    public void Gate6_AtIsReservedInBothFileKinds_AndTheOlderRulesKeepTheirIdsAndText()
    {
        var bad = new TechMaterial { Name = "Gold@x", Sigma20 = 1 };
        Assert.NotNull(MaterialValidation.NameRefusal(bad.Name));
        string cmat = Lib(Path.Combine(_tmp, "bad.cmat"), bad, new TechMaterial { Name = "dup" }, new TechMaterial { Name = "DUP" });
        string ctech = Tech(Path.Combine(_tmp, "t"), null, [bad]);
        foreach (string file in new[] { cmat, ctech })
            Assert.Contains(CheckRules(file), h => h.Rule == MaterialLibraries.ReservedCharacterId && h.Severity == "error");

        // A pre-existing rule: same id, same sentence as before the move to the Materials area.
        var dup = Assert.Single(MaterialValidation.Validate([new TechMaterial { Name = "dup" }, new TechMaterial { Name = "DUP" }]));
        Assert.Equal(TechValidation.Ids.MaterialDuplicate, dup.Id);
        Assert.Equal(TechProblemArea.Materials, dup.Area);
        Assert.Equal("2 materials are named \"dup\" (material names ignore case). A stackup entry naming it could mean any of " +
                     "them; rename all but one.", dup.Message);

        foreach (var entry in ShippedTechnologies.All)
            Assert.DoesNotContain(TechValidation.Analyze(ShippedTechnologies.Load(entry)), p => p.Id == MaterialLibraries.ReservedCharacterId);
        Assert.DoesNotContain(MaterialValidation.Validate(MaterialLibraries.LoadGeneric()), p => p.Id is not null);
    }

    // ── 8. A .cem reaches a library through its technology ────────────────────────────────────

    [Fact]
    public void Gate8_ACemsStackupEntryAndBodyTakeTheirLibraryMaterialsValues()
    {
        string example = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "examples", "Klopfenstein Taper"), "Taper-MoM.cem",
                                                  SearchOption.AllDirectories).Single();
        var setup = EmSetupPersistence.LoadFromFile(example);
        string? cws = null;
        for (string? d = Path.GetDirectoryName(example); d is not null && cws is null; d = Path.GetDirectoryName(d))
            if (File.Exists(Path.Combine(d, ".cws"))) cws = Path.Combine(d, ".cws");
        var source = EmSetupResolver.Resolve(example, setup.LayoutRef, cws, new TechnologyCache()).Source!;

        // The example's technology, now naming a library that holds its substrate and a lid material.
        var tech = TechPersistence.Clone(source.Technology!);
        var sub = tech.Stackup.Layers.Single(l => l.Kind == StackupKind.Dielectric);
        sub.Material = "LibSub";
        tech.Bodies.Add(new TechBody { Name = "Lid", Material = "LibLid", SitsOn = sub.Name, ThicknessDbu = 100_000 });
        tech.MaterialLibraries = ["lib.cmat"];
        Lib(Path.Combine(_tmp, "lib.cmat"), new TechMaterial { Name = "LibSub", Epsr = 4.4, TanD = sub.TanD, Mur = sub.Mur },
            new TechMaterial { Name = "LibLid", Epsr = 3.9, TanD = 0.008, Mur = 1 });
        Directory.CreateDirectory(_tmp);
        string ctech = Path.Combine(_tmp, "t.ctech");
        TechPersistence.SaveToFile(ctech, tech);
        var loaded = TechPersistence.LoadFromFile(ctech);

        var geometry = EmGeometry.ForSetup(setup, source with { Technology = loaded });
        var planar = PlanarExtractor.Extract(geometry.Shapes, loaded, source.DbuPerMicron, setup.Frequency.Expand().Max(),
                                             setup.ToExtractionSettings(setup.LayoutRef), geometry.GeneratorIds);
        Assert.True(planar.Ok, planar.Refusal);
        var epsr = new List<double> { planar.Problem!.Slab.Material.EpsR };
        if (planar.Problem.MediumStack is { } stack) epsr.AddRange(stack.Layers.Select(l => l.Material.EpsR));
        Assert.Contains(4.4, epsr);

        var three = Em3dGenerator.Generate(setup, source with { Technology = loaded }, loaded);
        Assert.True(three.Ok, three.Refusal);
        var lid = three.Problem!.Materials.Single(m => m.Name == "LibLid");
        Assert.Equal(3.9, lid.Epsr);
        Assert.Contains("via library", three.MaterialSources["LibLid"]);
    }

    // ── 9. Live: a library edit re-resolves exactly the technologies naming it ─────────────────

    [Fact]
    public void Gate9_ALiveLibraryEditReresolvesExactlyTheTechnologiesNamingIt_AndTheNextElaborationCarriesIt()
    {
        string lib = Lib(Path.Combine(_tmp, "ws", "tech", "lib.cmat"), Alumina());
        string named = Tech(Path.Combine(_tmp, "ws", "tech"), null, [], "lib.cmat");
        string also = Tech(Path.Combine(_tmp, "ws", "tech2"), null, [], "../tech/lib.cmat");
        string not = Tech(Path.Combine(_tmp, "ws", "tech3"), null, [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }]);
        var cache = new TechnologyCache();
        foreach (string t in new[] { named, also, not }) cache.Get(t);
        var changed = new List<string>();
        cache.TechnologyChanged += changed.Add;

        cache.SetLiveLibrary(lib, [Alumina(10.2)]);
        Assert.Equal(2, cache.LibraryReresolutions);
        Assert.Equal(new[] { named, also }.Order(), changed.Order());
        Assert.Equal(10.2, cache.Get(named)!.FindMaterial("Alumina")!.Epsr);

        WorkspacePersistence.SaveToFile(Path.Combine(_tmp, "ws", ".cws"), new CwsFile { DefaultTechRef = "tech/tech.ctech" });
        string c3d = Path.Combine(_tmp, "ws", "cell", "3d", "cell.c3d");
        Directory.CreateDirectory(Path.GetDirectoryName(c3d)!);
        C3dPersistence.SaveToFile(c3d, new C3dDocument
        {
            Objects = [new C3dBox { Name = "b", Material = "Alumina", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(1000, 1000, 1000) }],
        });
        var elaborator = new C3dElaborator(cache);
        double Epsr() => elaborator.Elaborate(C3dPersistence.LoadFromFile(c3d), c3d, null).Materials.Single(m => m.Name == "Alumina").Epsr;
        Assert.Equal(10.2, Epsr());
        cache.SetLiveLibrary(lib, [Alumina(11.1)]);
        Assert.Equal(11.1, Epsr());
        Assert.Contains("via library", elaborator.Elaborate(C3dPersistence.LoadFromFile(c3d), c3d, null).MaterialSources["Alumina"]);
    }

    // ── 12. Walkers ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate12_NewWorkspaceFromEveryShippedTechnologyCopiesTheGenericLibrary_AndResolvesAsTheEmbeddedOne()
    {
        foreach (var entry in ShippedTechnologies.All)
        {
            var made = WorkspaceCreate.Create(_tmp, "ws-" + entry.Id, entry.Id);
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(made.TechPath!)!, MaterialLibraries.GenericFileName)), entry.Id);
            var onDisk = TechPersistence.LoadFromFile(made.TechPath!);
            var embedded = ShippedTechnologies.Load(entry);
            Assert.Equal(Names(embedded), Names(onDisk));
            Assert.Equal(TechPersistence.Serialize(embedded), TechPersistence.Serialize(onDisk));
        }

        static string Names(Technology t) => string.Join(",", t.ResolvedMaterials.Select(m => m.Name + "=" + MaterialLibraries.Describe(m)));
    }

    [Fact]
    public void Gate12_AnArchiveFindsALibraryOutsideTheWorkspace()
    {
        string lib = Lib(Path.Combine(_tmp, "elsewhere", "lab.cmat"), Alumina());
        string ctech = Tech(Path.Combine(_tmp, "ws", "tech"), "Alumina", [], "../../elsewhere/lab.cmat");
        Assert.Contains(Path.GetFullPath(lib), CircuitRF.Design.Workspace.DocumentFileRefs.Find(ctech, Path.Combine(_tmp, "ws")).Select(Path.GetFullPath));
    }

    // ── 13. The CLI, as a process ────────────────────────────────────────────────────────────

    [Fact]
    public void Gate13_CheckAndExplainAndFindOnALibrary_AndReferenceMaterialsListsEveryField()
    {
        string lib = Lib(Path.Combine(_tmp, "ws", "tech", "lib.cmat"), Alumina());
        Tech(Path.Combine(_tmp, "ws", "tech"), "Alumina", [], "lib.cmat");
        WorkspacePersistence.SaveToFile(Path.Combine(_tmp, "ws", ".cws"), new CwsFile { DefaultTechRef = "tech/tech.ctech" });

        var check = RunCli("check", lib);
        Assert.Equal(0, check.ExitCode);
        Assert.Contains("1 document(s) checked", check.StdOut);

        var explain = RunCli("explain", lib);
        Assert.Contains("tech.ctech", explain.StdOut);

        var find = RunCli("find", _tmp, "--no-analyses");
        Assert.Contains("material library", find.StdOut);
        Assert.Contains("named by tech.ctech", find.StdOut);

        var reference = RunCli("reference", "materials");
        Assert.Equal(0, reference.ExitCode);
        foreach (var p in typeof(TechMaterial).GetProperties().Where(p => p.GetCustomAttribute<System.Text.Json.Serialization.JsonExtensionDataAttribute>() is null))
            Assert.Contains(p.Name, reference.StdOut);
        Assert.Contains("MaterialLibraries", RunCli("reference", "technology").StdOut);
    }

    // ── 14. The shipped generic library ───────────────────────────────────────────────────────

    [Fact]
    public void Gate14_TheGenericLibraryShips_EveryRecordCitesItsSource_AndAgreesWithEveryShippedTechnology()
    {
        var generic = MaterialLibraries.LoadGeneric();
        Assert.Equal(25, generic.Count);                   // brief-em3d-73 added ten (thermal §1b); soda-lime glass one more
        foreach (var m in generic)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Source), m.Name);
            if (m.Epsr is not null) Assert.Matches(@"\d\s*(k|M|G)?Hz", m.Source!);
        }
        Assert.DoesNotContain(generic, m => string.Equals(m.Name, "Air", StringComparison.OrdinalIgnoreCase));

        foreach (var entry in ShippedTechnologies.All)
        {
            var tech = ShippedTechnologies.Load(entry);                               // throws on a conflict
            Assert.Equal(["generic-materials.cmat"], tech.MaterialLibraries!);
            foreach (var own in tech.Materials)
                if (generic.FirstOrDefault(g => string.Equals(g.Name, own.Name, StringComparison.OrdinalIgnoreCase)) is { } g)
                {
                    // Display fields aside (brief-em3d-105: appearance too; 2026-10-04: a technology's own Silver and
                    // Aluminium carry the library's colour and look, since its own record is the one every view reads).
                    var mine = MaterialLibraryPersistence.Deserialize(MaterialLibraryPersistence.Serialize([own]))[0];
                    (g.Source, g.Color, g.Appearance) = (null, null, null);
                    (mine.Source, mine.Color, mine.Appearance) = (null, null, null);
                    Assert.Equal(MaterialLibraryPersistence.Serialize([mine]), MaterialLibraryPersistence.Serialize([g]));
                }
        }

        // The repository's vendor-name gate, over the library's text.
        var words = Regex.Matches(MaterialLibraries.GenericRawJson().ToLowerInvariant(), @"[a-z][a-z0-9]*").Select(w => w.Value).ToList();
        for (int i = 0; i < words.Count; i++)
        {
            Assert.DoesNotContain(Docs.WsProbeDocsTests.Digest(words[i]), Docs.WsProbeDocsTests.BannedDigests);
            if (i + 1 < words.Count) Assert.DoesNotContain(Docs.WsProbeDocsTests.Digest(words[i] + " " + words[i + 1]), Docs.WsProbeDocsTests.BannedDigests);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private readonly record struct RuleHit(string? Rule, string? Severity);

    private static List<RuleHit> CheckRules(string path)
    {
        var (_, stdout, _) = RunCli("check", path, "--json");
        using var doc = JsonDocument.Parse(stdout);
        return [.. doc.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Select(d => new RuleHit(
                d.GetProperty("arguments").TryGetProperty("rule", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
                d.GetProperty("severity").GetString()))];
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        string cliDir = typeof(MaterialLibraryTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitRF.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }
}

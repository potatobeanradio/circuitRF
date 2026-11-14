// ================================================================
//  C3dElaborationTests.cs — the gate for brief-em3d-42: a .c3d elaborated, assembled with a setup and
//  run, headless. Gates 1 and 2 (the existing goldens and the generator split) are the existing
//  backend tests and Em3dGeneratorDumpTests; the rest are here. Gates 9 and 10 run Palace and skip,
//  naming what is missing, on a machine without it.
// ================================================================

using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ThreeD;

// Gates 9 and 10 start Palace: in the collection every Palace-running class shares, so no other class's
// process-wide solver counters (RunBothTests' "nothing started") see these runs.
[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class C3dElaborationTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-c3d42-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const double Um = 1e-6;

    // ── 3. The equivalence oracle ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Two routes to one problem: the Package example through its own <c>Package C.cem</c>, and a <c>.c3d</c>
    /// holding one layout instance of it at the origin with an embedded copy of that setup. Every object
    /// agrees, primitive for primitive and material for material, except the documented differences —
    /// each asserted by name: the <c>U1/</c> prefix; the bounded extent (the layout draws no outline, so the
    /// substrate takes the box round its drawn geometry instead of the air box); the undrawn ground plane the .cem made its
    /// PEC floor, which is not metal in the instance (a placed layout brings only what it draws); and, following from
    /// both, the air above the stack (the .c3d's background). The air box's floor is the .cem's (brief-icp-10).
    /// </summary>
    [Fact]
    public void Gate3_ALayoutInstanceWithAnEmbeddedCopyOfItsSetup_AssemblesTheCemsProblem_ButForTheNamedDifferences()
    {
        string ws = CopyExample();
        string cem = Path.Combine(ws, "Package", "em", "Package C.cem");
        var cemSetup = EmSetupPersistence.LoadFromFile(cem);
        var resolved = EmSetupResolver.Resolve(cem, cemSetup.LayoutRef, Path.Combine(ws, ".cws"), new TechnologyCache());
        var a = Em3dGenerator.Generate(cemSetup, resolved.Source!, resolved.Source!.Technology!);
        Assert.True(a.Ok, a.Refusal);

        string c3d = WriteC3d(ws, "Assembly", new C3dDocument
        {
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../Package", View = C3dInstanceView.Layout }],
            Setups = [EmSetupPersistence.ToEmbedded(cemSetup)],
        });
        var doc = C3dPersistence.LoadFromFile(c3d);
        var (embedded, why) = C3dSetups.Select(doc, null);
        Assert.Null(why);
        var b = C3dProblemAssembly.Assemble(C3dSetups.ForRun(embedded!, c3d), doc, c3d, Path.Combine(ws, ".cws"));
        Assert.True(b.Ok, b.Refusal);
        var (p, q) = (a.Problem!, b.Problem!);

        // The prefix, and the two objects only one side has.
        var only = new HashSet<string>(StringComparer.Ordinal) { "air" };
        Assert.Equal(p.Solids.Where(s => !only.Contains(s.Name)).Select(s => "U1/" + s.Name).Order(),
                     q.Solids.Select(s => s.Name).Order());
        Assert.Contains(p.Solids, s => s is { Name: "air", Role: Em3dRole.Air });
        Assert.DoesNotContain(q.Solids, s => s.Role == Em3dRole.Air);

        // Conductors and wires: the same primitive, the same material values, the same relative order.
        foreach (var s in p.Solids.Where(s => s.Role == Em3dRole.Conductor))
        {
            var t = q.Solids.Single(x => x.Name == "U1/" + s.Name);
            Assert.Equal(Em3dGeneratorDumpTests.Prim(s.Primitive), Em3dGeneratorDumpTests.Prim(t.Primitive));
            Assert.Equal(p.Materials.Single(m => m.Name == s.Material) with { Name = "" },
                         q.Materials.Single(m => m.Name == t.Material) with { Name = "" });
        }
        Assert.Equal(p.Solids.Where(s => s.Role == Em3dRole.Conductor).OrderBy(s => s.Order).Select(s => "U1/" + s.Name),
                     q.Solids.Where(s => s.Role == Em3dRole.Conductor).OrderBy(s => s.Order).Select(s => s.Name));

        // The bounded extent: the substrate keeps its heights and, with no outline drawn, takes the box round the drawn
        // geometry — not the air box.
        var baseA = (Em3dBox)p.Solids.Single(s => s.Name == "Base").Primitive;
        var baseB = (Em3dExtrudedPolygon)q.Solids.Single(s => s.Name == "U1/Base").Primitive;
        Assert.Equal((baseA.Min.Z, baseA.Max.Z), (baseB.ZBottom, baseB.ZTop));
        Assert.Equal((p.Boundary.Min.X, p.Boundary.Max.X), (baseA.Min.X, baseA.Max.X));
        Assert.Equal((-3.9e-3, -100 * Um, 3.9e-3, 100 * Um),
                     (baseB.Outline.Min(v => v.X), baseB.Outline.Min(v => v.Y), baseB.Outline.Max(v => v.X), baseB.Outline.Max(v => v.Y)),
                     new Tol(1e-9));
        Assert.Equal(p.Boundary.Min.Z, baseB.ZBottom);      // the .cem's floor height is the plane's top, and nothing fills it
        Assert.Contains(b.Notes, n => n.Contains("bounded by the box round its drawn geometry", StringComparison.Ordinal));

        // The terminals hold the same conductors; the ground is the PEC floor on one side, and nothing on the other.
        Assert.Equal(p.Terminals.Select(t => (t.Name, string.Join(",", t.Objects.Select(o => "U1/" + o)))),
                     q.Terminals.Select(t => (t.Name, string.Join(",", t.Objects))));
        Assert.Empty(p.GroundObjects);
        Assert.Empty(q.GroundObjects);

        // The air box: the same everywhere but its floor.
        Assert.Equal((p.Boundary.Min.X, p.Boundary.Min.Y, p.Boundary.Max.X, p.Boundary.Max.Y),
                     (q.Boundary.Min.X, q.Boundary.Min.Y, q.Boundary.Max.X, q.Boundary.Max.Y), new Tol(1e-9));
        Assert.Equal(p.Boundary.Max.Z, q.Boundary.Max.Z);
        // brief-icp-10 — and its floor too: the view floors its air box at the undrawn plane, as the .cem does (it was absorbing,
        // below the geometry, so a port the layout walked to that plane landed on nothing).
        Assert.Equal(p.Boundary.Faces, q.Boundary.Faces);
        Assert.Equal(Em3dBoundaryKind.Pec, p.Boundary.Faces.ZMin);
        Assert.Equal(p.Boundary.Min.Z, q.Boundary.Min.Z, 12);
        Assert.Equal((p.Type, p.Frequency, p.OperatingTempC), (q.Type, q.Frequency, q.OperatingTempC));
    }

    // ── §1d. The lowering table ─────────────────────────────────────────────────────────────────

    /// <summary>R-em3d42-1d — each object lowers to the richest primitive that states it exactly, on the WHOLE
    /// transform it ends up under. The FDTD path's cost depends on this table, so it is pinned row by row.</summary>
    [Fact]
    public void TheLoweringTable_BoxExtrusionCylinderElsePolyhedron_OnTheComposedTransform()
    {
        static C3dPlacement Rot(C3dAxis axis, double deg) => new() { Rotate = [new C3dRotation { Axis = axis, Deg = deg }] };
        var box = new C3dBox { Name = "b", Material = "Gold", Min = new(0, 0, 0), Size = new(1000, 2000, 3000) };
        var prism = new C3dPrism { Name = "p", Material = "Gold", Outline = [new(0, 0), new(1000, 0), new(0, 1000)], Height = 500 };
        var cyl = new C3dCylinder { Name = "c", Material = "Gold", Radius = 100, Length = 1000 };
        var sheet = new C3dSheet { Name = "s", Material = "Gold", Plane = C3dPlane.YZ, Rect = new C3dRect { Size = new(1000, 1000) } };

        string Kind(C3dObject o, C3dTransform? world = null) => C3dLowering.Lower(o, world ?? C3dTransform.Identity, 1000)!.Kind;
        Assert.Equal("box", Kind(box));
        box.Placement = Rot(C3dAxis.X, 90);
        Assert.Equal("box", Kind(box));
        box.Placement = Rot(C3dAxis.X, 30);
        Assert.Equal("polyhedron", Kind(box));
        Assert.Equal("box", Kind(box, C3dTransform.Rotation(C3dAxis.X, -30)));     // 30° inside −30° is a box again
        Assert.Equal("extruded-polygon", Kind(prism));
        prism.Placement = Rot(C3dAxis.Z, 17);
        Assert.Equal("extruded-polygon", Kind(prism));
        prism.Shear = new(10, 0);
        Assert.Equal("polyhedron", Kind(prism));
        prism.Shear = default;
        prism.Plane = C3dPlane.YZ;
        Assert.Equal("polyhedron", Kind(prism));
        cyl.Placement = Rot(C3dAxis.Y, 33);
        Assert.Equal("cylinder", Kind(cyl));
        Assert.Equal("sheet-in-a-plane", Kind(sheet));
        Assert.Equal("sheet", Kind(new C3dSheet { Name = "f", Material = "Gold", Rect = new C3dRect { Size = new(10, 10) } }));
        Assert.Null(C3dLowering.Lower(new C3dPolyline { Name = "l", Points = [new(0, 0), new(1, 1)] }, C3dTransform.Identity, 1000));

        // Every polyhedron the table makes is closed, planar and outward — and a box's faces keep their names.
        prism.Plane = C3dPlane.XZ; prism.Height = -500; prism.Shear = new(20, 30);
        foreach (var o in new C3dObject[] { box, prism })
        {
            var poly = (Em3dPolyhedron)C3dLowering.Lower(o, C3dTransform.Identity, 1000)!.Solid!;
            Assert.Empty(poly.Problems(o.Name));
        }
        box.Placement = Rot(C3dAxis.Z, 90);
        var turned = C3dLowering.Lower(box, C3dTransform.Identity, 1000)!;
        // A quarter turn about +z carries the box's own +y face to world −x: world xmin is its ymax.
        Assert.Equal(["ymax", "ymin", "xmin", "xmax", "zmin", "zmax"], turned.FaceNames);
    }

    // ── 4. A polyhedron box and a box ───────────────────────────────────────────────────────────

    /// <summary>
    /// Gate 4 — a box drawn as a Polyhedron and the same box drawn as a Box: Palace's .geo volumes have equal
    /// bounding boxes and equal volume, and CSXCAD writes the same primitive, so it covers the same cells.
    /// (openEMS's own polyhedron leaves out grid nodes lying ON its faces — measured, RESOLVED.md — which is why
    /// the writer states a box-shaped polyhedron as a Box.)
    /// </summary>
    [Fact]
    public void Gate4_ABoxDrawnAsAPolyhedron_WritesTheBoxesVolumeToPalace_AndTheSameCellsToOpenEms()
    {
        var drawnBox = new C3dBox { Name = "blk", Material = "Gold", Min = new(2_000_000, -200_000, 543_000), Size = new(1_000_000, 400_000, 200_000) };
        C3dPoint3 V(int k) => new(k % 2 == 0 ? 2_000_000 : 3_000_000, k / 2 % 2 == 0 ? -200_000 : 200_000, k / 4 == 0 ? 543_000 : 743_000);
        var drawnPoly = new C3dPolyhedron
        {
            Name = "blk", Material = "Gold", Vertices = [.. Enumerable.Range(0, 8).Select(V)],
            Faces =
            [
                new() { Name = "xmin", Outer = [0, 4, 6, 2] }, new() { Name = "xmax", Outer = [1, 3, 7, 5] },
                new() { Name = "ymin", Outer = [0, 1, 5, 4] }, new() { Name = "ymax", Outer = [2, 6, 7, 3] },
                new() { Name = "zmin", Outer = [0, 2, 3, 1] }, new() { Name = "zmax", Outer = [4, 5, 7, 6] },
            ],
        };
        var (setup, source) = Em3dGeneratorTests.Microstrip();
        var basis = Em3dGenerator.Generate(setup, source, source.Technology!).Problem!;
        Em3dProblem With(C3dObject o)
        {
            var lowered = C3dLowering.Lower(o, C3dTransform.Identity, 1000)!;
            return basis with { Solids = [.. basis.Solids, new Em3dSolid("blk", basis.Solids.First(s => s.Role == Em3dRole.Conductor).Material,
                                                                        Em3dRole.Conductor, lowered.Solid!, basis.Solids.Max(s => s.Order) + 1)] };
        }
        var pBox = With(drawnBox);
        // brief-em3d-47 R-em3d47-1c — the lowering now recognises the drawn polyhedron AS a box; the writers' own
        // polyhedron path is driven with the neutral polyhedron that box states, which is what this gate is about.
        Assert.IsType<Em3dBox>(With(drawnPoly).Solids[^1].Primitive);
        var pPoly = pBox with { Solids = [.. pBox.Solids.SkipLast(1), pBox.Solids[^1] with { Primitive = C3dLowering.BoxPolyhedron((Em3dBox)pBox.Solids[^1].Primitive) }] };
        Assert.IsType<Em3dBox>(pBox.Solids[^1].Primitive);
        var poly = Assert.IsType<Em3dPolyhedron>(pPoly.Solids[^1].Primitive);

        var settings = PalaceSettings.Default;
        var (geoBox, geoPoly) = (GmshGeoWriter.Write(pBox, settings).Geo!, GmshGeoWriter.Write(pPoly, settings).Geo!);
        var (bb1, v1) = GeoVolume(geoBox, "blk");
        var (bb2, _) = GeoVolume(geoPoly, "blk");
        Assert.Equal(bb1, bb2);
        Assert.Equal(v1, poly.SignedVolume() * 1e18, 1e-9 * v1);

        var grid = FdtdGrid.Build(pBox, CemOpenEms.ResolveGrid(null));
        Assert.Equal(grid.Cells, FdtdGrid.Build(pPoly, CemOpenEms.ResolveGrid(null)).Cells);
        var run = CemOpenEms.ResolveRun(null);
        var xmlBox = CsxcadWriter.Write(pBox, grid, CemOpenEms.ResolveGrid(null), run);
        var xmlPoly = CsxcadWriter.Write(pPoly, FdtdGrid.Build(pPoly, CemOpenEms.ResolveGrid(null)), CemOpenEms.ResolveGrid(null), run);
        Assert.True(xmlBox.Ok && xmlPoly.Ok, xmlBox.Refusal ?? xmlPoly.Refusal);
        Assert.Equal(xmlBox.Model, xmlPoly.Model);
    }

    /// <summary>The bound (µm) of one solid's geometry in a .geo, from its Box or its Points, and its volume (µm³)
    /// when it is a Box.</summary>
    private static ((double, double, double, double, double, double) Bound, double Volume) GeoVolume(string geo, string name)
    {
        var lines = geo.Split('\n');
        int at = Array.FindIndex(lines, l => l.StartsWith($"// {name}:", StringComparison.Ordinal));
        var xs = new List<double[]>();
        double volume = 0;
        for (int i = at + 1; i < lines.Length && !lines[i].StartsWith("//", StringComparison.Ordinal) && lines[i].Length > 0; i++)
        {
            var m = Regex.Match(lines[i], @"^(?:v = newv; Box\(v\)|Point\(p \+ \d+\)) = \{([^}]*)\}");
            if (!m.Success) continue;
            var n = m.Groups[1].Value.Split(',').Select(t => double.Parse(t, CultureInfo.InvariantCulture)).ToArray();
            if (n.Length == 6)
            {
                xs.Add([n[0], n[1], n[2]]); xs.Add([n[0] + n[3], n[1] + n[4], n[2] + n[5]]);
                volume = n[3] * n[4] * n[5];
            }
            else xs.Add(n);
        }
        return ((xs.Min(p => p[0]), xs.Min(p => p[1]), xs.Min(p => p[2]), xs.Max(p => p[0]), xs.Max(p => p[1]), xs.Max(p => p[2])), volume);
    }

    // ── 5. Units ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Gate 5 — a child drawn at a picometre DBU, placed in a mil parent at a nanometre DBU at 12.5 mil (not an
    /// integer number of mil): the child's corner at 7.000123 µm lands at 324.500123 µm, to 1e-15 relative —
    /// rounded to neither document's DBU.
    /// </summary>
    [Fact]
    public void Gate5_AChildInItsOwnUnits_LandsAtTheExactMetrePosition_RoundedToNeitherDbu()
    {
        string ws = Workspace(("Gold", 4.1e7));
        WriteC3d(ws, "Die", new C3dDocument
        {
            DbuPerMicron = 1_000_000, DisplayUnit = LayoutUnit.Um,
            Objects = [new C3dBox { Name = "pad", Material = "Gold", Min = new(7_000_123, 0, 0), Size = new(1_000_000, 1_000_000, 1_000_000) }],
        });
        string top = WriteC3d(ws, "Pkg", new C3dDocument
        {
            DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Mil,
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../Die", Placement = new C3dPlacement { Origin = new(317_500, 0, 0) } }],
        });
        var e = C3dElaborator.ElaborateOnce(C3dPersistence.LoadFromFile(top), top, null);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        var pad = (Em3dBox)e.Solids.Single(s => s.Name == "U1/pad").Primitive;
        double expected = (double)(317.5m + 7.000123m) * 1e-6;
        Assert.True(Math.Abs(pad.Min.X - expected) <= 1e-15 * expected, $"{pad.Min.X:R} vs {expected:R}");
        Assert.Equal(8.000123e-6 + 317.5e-6, pad.Max.X, 1e-15 * pad.Max.X);
        Assert.Contains(e.WalkUnits, w => w.Subject == "U1" && w.Detail.StartsWith("1000000 DBU per µm", StringComparison.Ordinal));
    }

    // ── 6. Materials ────────────────────────────────────────────────────────────────────────────

    /// <summary>Gate 6 — two technologies each defining Gold: different σ gives two qualified materials and a
    /// note naming both; equal σ gives one Gold.</summary>
    [Theory]
    [InlineData(3.0e7, new[] { "Gold@a", "Gold@b" })]
    [InlineData(4.1e7, new[] { "Gold" })]
    public void Gate6_SameNamedMaterialsFromTwoTechnologies_MergeWhenEqual_AreQualifiedWhenNot(double sigmaB, string[] names)
    {
        string ws = Workspace();
        SaveTech(Path.Combine(ws, "a.ctech"), ("Gold", 4.1e7));
        SaveTech(Path.Combine(ws, "b.ctech"), ("Gold", sigmaB));
        WriteC3d(ws, "Child", new C3dDocument
        {
            TechRef = "../../b.ctech",
            Objects = [new C3dBox { Name = "m", Material = "Gold", Min = new(0, 0, 1000), Size = new(1000, 1000, 1000) }],
        });
        string top = WriteC3d(ws, "Top", new C3dDocument
        {
            TechRef = "../../a.ctech",
            Objects = [new C3dBox { Name = "m", Material = "Gold", Size = new(1000, 1000, 1000) }],
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../Child" }],
        });
        var e = C3dElaborator.ElaborateOnce(C3dPersistence.LoadFromFile(top), top, null);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        Assert.Equal(names, e.Materials.Select(m => m.Name));
        Assert.Equal(names[0], e.Solids.Single(s => s.Name == "m").Material);
        Assert.Equal(names[^1], e.Solids.Single(s => s.Name == "U1/m").Material);
        Assert.Equal(names.Length > 1, e.Notes.Any(n => n.Contains("'Gold@a'", StringComparison.Ordinal) &&
                                                         n.Contains("'Gold@b'", StringComparison.Ordinal)));
    }

    // ── 7. The cache ────────────────────────────────────────────────────────────────────────────

    /// <summary>Gate 7 — editing one object of a 1,000-object document re-elaborates one object and no child.</summary>
    [Fact]
    public void Gate7_EditingOneObjectOfAThousand_ReElaboratesOneObject_AndNoChild()
    {
        string ws = Workspace(("Gold", 4.1e7));
        WriteC3d(ws, "Child", new C3dDocument { Objects = [new C3dBox { Name = "c", Material = "Gold", Size = new(10, 10, 10) }] });
        var doc = new C3dDocument
        {
            Objects = [.. Enumerable.Range(0, 1000).Select(i => (C3dObject)new C3dBox
                { Name = $"b{i}", Material = "Gold", Min = new(i * 20L, 0, 0), Size = new(10, 10, 10) })],
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../Child", Placement = new C3dPlacement { Origin = new(0, 100, 0) } }],
        };
        string path = WriteC3d(ws, "Big", doc);
        var elaborator = new C3dElaborator();
        Assert.True(elaborator.Elaborate(doc, path, null).Ok);
        var (objects, children) = (elaborator.ObjectsElaborated, elaborator.ChildrenElaborated);
        Assert.Equal((1001L, 1L), (objects, children));

        ((C3dBox)doc.Objects[500]).Size = new(10, 10, 20);
        var e = elaborator.Elaborate(doc, path, null);
        Assert.Equal((objects + 1, children), (elaborator.ObjectsElaborated, elaborator.ChildrenElaborated));
        Assert.Equal(20e-9, ((Em3dBox)e.Solids.Single(s => s.Name == "b500").Primitive).Max.Z, 1e-20);
    }

    // ── 8. A cycle, and a missing child ─────────────────────────────────────────────────────────

    /// <summary>Gate 8 — A → B → A is refused with the path spelled out; a missing child is a refusal naming the
    /// instance and the path tried, never an empty instance.</summary>
    [Fact]
    public void Gate8_ACycleIsRefusedWithItsPath_AndAMissingChildNamesTheInstanceAndThePathTried()
    {
        string ws = Workspace();
        string a = WriteC3d(ws, "A", new C3dDocument { Instances = [new C3dInstance { Name = "U1", CellRef = "../../B" }] });
        WriteC3d(ws, "B", new C3dDocument { Instances = [new C3dInstance { Name = "U3", CellRef = "../../A" }] });
        var e = C3dElaborator.ElaborateOnce(C3dPersistence.LoadFromFile(a), a, null);
        Assert.Contains(e.Refusals, r => r.Contains("A/U1 → B/U3 → A", StringComparison.Ordinal));

        string lone = WriteC3d(ws, "Lone", new C3dDocument { Instances = [new C3dInstance { Name = "X9", CellRef = "../../Nowhere" }] });
        var missing = C3dElaborator.ElaborateOnce(C3dPersistence.LoadFromFile(lone), lone, null);
        var r = Assert.Single(missing.Refusals);
        Assert.Contains("'X9'", r);
        Assert.Contains(Path.Combine(ws, "Nowhere"), r);
    }

    // ── §5. Setups ──────────────────────────────────────────────────────────────────────────────

    /// <summary>R-em3d42-5 — one schema in two containers: an embedded setup round-trips without LayoutRef; a
    /// nameless, duplicate or planar one is refused by name; several need --setup; and the result lands where a
    /// .cem named "&lt;stem&gt; &lt;setup&gt;.cem" beside the .c3d would put it.</summary>
    [Fact]
    public void EmbeddedSetups_OneSchema_ChosenByName_LandWhereASiblingCemWould()
    {
        var (s, _) = Em3dGeneratorTests.Microstrip();
        s.Name = "C";
        var json = EmSetupPersistence.ToEmbedded(s);
        Assert.False(json.TryGetProperty("LayoutRef", out _));
        Assert.Equal(EmSetupPersistence.Serialize(s), EmSetupPersistence.Serialize(EmSetupPersistence.FromEmbedded(json) is var back
                                                                                         ? Relaid(back, s.LayoutRef) : null!));

        var planar = s.Clone(); planar.Name = "P"; planar.Solver3D = Em3dSolverChoice.None;
        var nameless = s.Clone(); nameless.Name = "";
        var doc = new C3dDocument { Setups = [json, EmSetupPersistence.ToEmbedded(s), EmSetupPersistence.ToEmbedded(planar), EmSetupPersistence.ToEmbedded(nameless)] };
        var read = C3dSetups.Read(doc);
        Assert.Null(read[0].Refusal);
        Assert.Contains("both named 'C'", read[1].Refusal);
        Assert.Contains(C3dSetups.PlanarRefusal, read[2].Refusal);
        Assert.Contains("has no Name", read[3].Refusal);
        Assert.Contains("--setup", C3dSetups.Select(doc, null).Refusal);

        var run = C3dSetups.ForRun(read[0].Setup!, "/ws/Pkg/3d/Pkg.c3d");
        Assert.Equal(Path.Combine("/ws/results", "Pkg C.s2p"), EmRunService.ResolveSnpPath("/ws/results", run, 2));
        Assert.Equal(Path.Combine("/ws/results", "Pkg C.palace"), Em3dRunService.SnpBasePath("/ws/results", run, Em3dSolverId.Palace));

        static EmSetup Relaid(EmSetup e, string layoutRef) { e.LayoutRef = layoutRef; return e; }
    }

    // ── 9, 10. A small solve, and the CLI ───────────────────────────────────────────────────────

    /// <summary>
    /// Gate 9 — a drawn two-line structure through Palace against the same geometry generated from a .clay
    /// through a .cem. The .c3d is built object for object from the .cem's own problem (its substrate a Box
    /// spanning the .cem's air box, its lines as drawn), with an embedded setup whose AirBox pads nothing
    /// laterally and floors the box with PEC — so both runs solve the same geometry and the matrices agree to
    /// a mesh-to-mesh tolerance. Electrostatic because a driven run needs ports, which are brief 49's.
    /// </summary>
    [PalaceFact]
    public void Gate9_ADrawnStructureAndTheSameGeometryFromALayout_GiveTheSameCapacitanceThroughPalace()
    {
        var (cem, ws, c3d) = TwoLinesPair();
        var a = EmRunService.Run(cem.Setup, cem.Source, Path.Combine(ws, "results-cem"));
        Assert.True(a.Status == EmRunStatus.Ok, a.Error);
        var doc = C3dPersistence.LoadFromFile(c3d);
        var b = EmRunService.RunThreeDView(C3dSetups.ForRun(C3dSetups.Select(doc, null).Setup!, c3d), doc, c3d, Path.Combine(ws, ".cws"),
                                          Path.Combine(ws, "results"));
        Assert.True(b.Status == EmRunStatus.Ok, b.Error);
        var (ca, cb) = (a.Data![Em3dStaticResult.CapacitanceCube].RealValues, b.Data![Em3dStaticResult.CapacitanceCube].RealValues);
        output.WriteLine($"C from the .cem: {string.Join(", ", ca.Select(v => v.ToString("G6", CultureInfo.InvariantCulture)))}");
        output.WriteLine($"C from the .c3d: {string.Join(", ", cb.Select(v => v.ToString("G6", CultureInfo.InvariantCulture)))}");
        for (int i = 0; i < ca.Length; i++) Assert.InRange(cb[i] / ca[i], 0.99, 1.01);
    }

    /// <summary>Gate 10 — <c>em x.c3d</c> as a process writes the in-process run's result, byte for byte (the em
    /// gate's rule; a static run writes a .npy and no Touchstone, and a .npy carries no timestamp).</summary>
    [PalaceFact]
    public void Gate10_TheEmVerbOnAC3dAsAProcess_WritesTheInProcessResult()
    {
        var (_, ws, c3d) = TwoLinesPair();
        var doc = C3dPersistence.LoadFromFile(c3d);
        var setup = C3dSetups.ForRun(C3dSetups.Select(doc, null).Setup!, c3d);
        var inProcess = EmRunService.RunThreeDView(setup, doc, c3d, Path.Combine(ws, ".cws"), Path.Combine(ws, "results"));
        Assert.True(inProcess.Status == EmRunStatus.Ok, inProcess.Error);
        byte[] expected = File.ReadAllBytes(inProcess.NpyPath!);
        File.Delete(inProcess.NpyPath!);

        var (code, stdout, stderr) = CliProcess.Run(PalaceBackendTests.RepoRoot(), [], "em", c3d);
        output.WriteLine(stdout);
        output.WriteLine(stderr);
        Assert.Equal(0, code);
        Assert.EndsWith("two S.palace_es.npy", inProcess.NpyPath);
        Assert.Equal(expected, File.ReadAllBytes(inProcess.NpyPath!));
    }

    /// <summary>TwoLines, electrostatic, as a .cem (in memory) and as a .c3d with an embedded setup (on disk).</summary>
    private ((EmSetup Setup, EmLayoutSource Source) Cem, string Ws, string C3d) TwoLinesPair()
    {
        var (setup, source) = PalaceStaticTests.TwoLines(Em3dProblemType.Electrostatic);
        var pad = new EmAirBoxFace(500, null);
        setup.AirBox = new EmAirBox(pad, pad, pad, pad, null, new EmAirBoxFace(700, null));
        var p = Em3dGenerator.Generate(setup, source, source.Technology!).Problem!;

        string ws = Workspace();
        // The stackup entries' own numbers, as named materials a drawn object can be made of.
        var tech = TechPersistence.Clone(source.Technology!);
        foreach (var m in p.Materials.Where(m => tech.FindMaterial(m.Name) is null))
            tech.Materials.Add(m.SigmaSm > 0
                ? new TechMaterial { Name = m.Name, Sigma20 = m.SigmaSm, Mur = m.Mur }
                : new TechMaterial { Name = m.Name, Epsr = m.Epsr, TanD = m.TanD, Mur = m.Mur });
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), tech);
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        static long N(double m) => (long)Math.Round(m * 1e9);
        static string Clean(string name) => Regex.Replace(name, "[^A-Za-z0-9]+", "_").Trim('_');
        var objects = new List<C3dObject>();
        foreach (var s in p.Solids.Where(s => s.Role != Em3dRole.Air).OrderBy(s => s.Order))
            objects.Add(s.Primitive switch
            {
                Em3dBox b => new C3dBox { Name = Clean(s.Name), Material = s.Material,
                                          Min = new(N(b.Min.X), N(b.Min.Y), N(b.Min.Z)), Size = new(N(b.Max.X - b.Min.X), N(b.Max.Y - b.Min.Y), N(b.Max.Z - b.Min.Z)) },
                Em3dExtrudedPolygon e => new C3dPrism { Name = Clean(s.Name), Material = s.Material, Offset = N(e.ZBottom), Height = N(e.ZTop - e.ZBottom),
                                                        Outline = [.. e.Outline.Select(q => new C3dPoint2(N(q.X), N(q.Y)))] },
                _ => throw new InvalidOperationException(s.Primitive.GetType().Name),
            });
        foreach (var sh in p.Sheets)
            objects.Add(new C3dSheet { Name = Clean(sh.Name), Material = sh.Material, Offset = N(sh.Z), ThicknessUm = sh.ThicknessM * 1e6,
                                       Outline = [.. sh.Outline.Select(q => new C3dPoint2(N(q.X), N(q.Y)))] });
        var embedded = setup.Clone();
        embedded.Name = "S";
        var none = new EmAirBoxFace(0, setup.AirBox.XMin?.Boundary);
        embedded.AirBox = new EmAirBox(none, none, none, none, new EmAirBoxFace(0, Em3dBoundaryKind.Pec), setup.AirBox.ZMax);
        // A terminal names a drawn conductor by its name: the lines are the nets' conductors.
        var terminals = p.Terminals.Select(t => new EmTerminal3D(t.Name, Clean(t.Objects.Single()))).ToList();
        embedded.Terminals3D = terminals;
        string c3d = WriteC3d(ws, "two", new C3dDocument { Objects = objects, Setups = [EmSetupPersistence.ToEmbedded(embedded)] });
        return ((setup, source), ws, c3d);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>An empty workspace under the test's root, with a technology holding <paramref name="materials"/>
    /// as its default when any are given.</summary>
    private string Workspace(params (string Name, double Sigma)[] materials)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        var cws = new CwsFile();
        if (materials.Length > 0)
        {
            SaveTech(Path.Combine(ws, "tech.ctech"), materials);
            cws.DefaultTechRef = "tech.ctech";
        }
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), cws);
        return ws;
    }

    private static void SaveTech(string path, params (string Name, double Sigma)[] materials)
        => TechPersistence.SaveToFile(path, new Technology
        {
            Name = Path.GetFileNameWithoutExtension(path),
            Materials = [.. materials.Select(m => new TechMaterial { Name = m.Name, Sigma20 = m.Sigma })],
        });

    /// <summary>Writes <paramref name="doc"/> as <c>&lt;ws&gt;/&lt;cell&gt;/3d/&lt;cell&gt;.c3d</c>.</summary>
    private static string WriteC3d(string ws, string cell, C3dDocument doc)
    {
        string dir = Path.Combine(ws, cell, "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, cell + ".c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }

    /// <summary>The shipped 3D EM example, copied under the test's root.</summary>
    private string CopyExample()
    {
        string src = Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "3D EM");
        string dst = Path.Combine(_root, "3D EM");
        foreach (string f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(dst, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        return dst;
    }

    /// <summary>Compares tuples of doubles to an absolute tolerance.</summary>
    private sealed class Tol(double eps) : IEqualityComparer<(double, double, double, double)>
    {
        public bool Equals((double, double, double, double) a, (double, double, double, double) b)
            => Math.Abs(a.Item1 - b.Item1) <= eps && Math.Abs(a.Item2 - b.Item2) <= eps &&
               Math.Abs(a.Item3 - b.Item3) <= eps && Math.Abs(a.Item4 - b.Item4) <= eps;
        public int GetHashCode((double, double, double, double) obj) => 0;
    }
}

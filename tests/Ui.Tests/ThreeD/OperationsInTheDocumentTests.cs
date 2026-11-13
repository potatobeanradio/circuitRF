// ================================================================
//  OperationsInTheDocumentTests.cs — the gate for brief-em3d-64: Boolean, Fillet, Chamfer and Step in the .c3d.
//  Counters, names and document bytes only; nothing is looked at. The gates that need OpenCASCADE are [KernelFact]
//  and skip, naming what to build, where the worker is not built — the ordinary state of a fresh clone.
// ================================================================

using System.Diagnostics;
using System.Reflection;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Tests.ThreeD.Occ;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class OperationsInTheDocumentTests : IDisposable
{
    private const long Um = 1000;                        // DBU per µm at the default 1000 DBU/µm
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-ops64-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 1. byte identity: a document with no kernel object never reaches the worker ───────────

    [Fact]
    public void Gate1_EveryShippedThreeDView_ElaboratesWithNoWorkerCallAndNoTree()
    {
        var fake = new FakeKernel();
        using var kernel = fake.Create();
        // brief-em3d-70 — the 3D Connector example uses the kernel on purpose, and the Hierarchy example carries its own copy
        // of that launch, as does the 3D Wave Ports example (its flange unchanged, its launch's back end opened to a wave
        // port); every OTHER shipped 3D view holds no kernel object, and those are what this gate is about.
        string examples = Path.Combine(RepoRoot(), "examples");
        var all = Directory.GetFiles(examples, "*.c3d", SearchOption.AllDirectories);
        var usesKernel = all.Where(f => C3dKernelUse.Of(C3dPersistence.LoadFromFile(f)).Count > 0).ToList();
        // brief-cxp-5 — the Loop Sensor example's connector, flange, board cut-out and gap feed are built with the same
        // operations, so they use the kernel too.
        Assert.Equal(["3D Connector/Flange/3d/Flange.c3d", "3D Connector/Launch/3d/Launch.c3d",
                       "3D Wave Ports/Flange/3d/Flange.c3d", "3D Wave Ports/Launch/3d/Launch.c3d", "Hierarchy/Launch/3d/Launch.c3d",
                       "Loop Sensor/Gap Fed/3d/Gap Fed.c3d", "Loop Sensor/SMA Flange/3d/SMA Flange.c3d", "Loop Sensor/SMA/3d/SMA.c3d",
                       "Loop Sensor/Sensor/3d/Sensor.c3d"],
            usesKernel.Select(f => Path.GetRelativePath(examples, f).Replace(Path.DirectorySeparatorChar, '/')).Order(StringComparer.Ordinal));
        // The Hierarchy Assembly holds no kernel object but PLACES that launch, so its elaboration reaches the kernel too.
        bool Places(string f) => C3dPersistence.LoadFromFile(f).Instances.Any(i =>
        {
            string cell = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(f)!, i.CellRef)) + Path.DirectorySeparatorChar;
            return usesKernel.Any(k => k.StartsWith(cell, StringComparison.Ordinal))
                || all.Where(o => o.StartsWith(cell, StringComparison.Ordinal) && o != f).Any(Places);
        });
        var placesKernel = all.Except(usesKernel).Where(Places).ToList();
        // The Loop Sensor's ideal-port board places the sensor, and its connector board places the sensor and the SMA.
        Assert.Equal(["Hierarchy/Assembly/3d/Assembly.c3d", "Loop Sensor/Ideal Port/3d/Ideal Port.c3d", "Loop Sensor/With Connector/3d/With Connector.c3d"],
            placesKernel.Select(f => Path.GetRelativePath(examples, f).Replace(Path.DirectorySeparatorChar, '/')).Order(StringComparer.Ordinal));
        var files = all.Except(usesKernel).Except(placesKernel).ToList();
        Assert.NotEmpty(files);
        foreach (string f in files)
        {
            var el = new C3dElaborator(null, kernel);
            var e = el.Elaborate(C3dPersistence.LoadFromFile(f), f, null);
            Assert.True(e.Solids.Count > 0, f);
            Assert.Equal(0, el.KernelTreesBuilt);
            Assert.Empty(e.KernelBuilds);
        }
        Assert.Empty(fake.Requests);                     // not even the handshake: the kernel was never asked
        Assert.Equal(0, kernel.RequestsSent);
    }

    // ── 2. the format: each example reads and writes back byte for byte, defaults omitted ─────

    [Theory]
    [InlineData(nameof(BooleanExample))]
    [InlineData(nameof(FilletExample))]
    [InlineData(nameof(ChamferExample))]
    [InlineData(nameof(StepExample))]
    public void Gate2_EachFormatExample_RoundTripsByteForByte_AndStatesOnlyWhatDiffersFromTheDefaults(string which)
    {
        string text = Example(which);
        var doc = C3dPersistence.Deserialize(text);
        Assert.Equal(text, C3dPersistence.Serialize(doc));
        foreach (string key in (string[])["\"Enabled\"", "\"KeepTools\"", "\"Distance2\"", "\"Role\""])
            Assert.DoesNotContain(key, text, StringComparison.Ordinal);

        // A switch away from its default is written, and read back as it was set.
        if (doc.Objects[0] is C3dOperation op)
        {
            op.Enabled = false;
            if (op is C3dBoolean b) b.KeepTools = true;
            string written = C3dPersistence.Serialize(doc);
            Assert.Contains("\"Enabled\": false", written, StringComparison.Ordinal);
            var back = (C3dOperation)C3dPersistence.Deserialize(written).Objects[0];
            Assert.False(back.Enabled);
            Assert.Equal(op is C3dBoolean, back is C3dBoolean { KeepTools: true });
        }
    }

    // ── 3. an unknown kind INSIDE a boolean is named as clearly as one at the top ─────────────

    [Fact]
    public void Gate3_AnUnknownKindNestedInABoolean_IsRefusedByName()
    {
        string torus = BooleanExample.Replace("\"$type\": \"Cylinder\"", "\"$type\": \"Torus\"").Replace("\"bore\"", "\"x\"");
        var e = Assert.Throws<C3dReadException>(() => C3dPersistence.Deserialize(torus));
        Assert.Equal("c3d.read.unknown-kind", e.Diagnostic.Id);
        Assert.Contains("object 'x' (a \"Torus\")", e.Message, StringComparison.Ordinal);
    }

    // ── 4. names: from OCCT's history, in section 2d's order, unchanged by a move ─────────────

    [KernelFact]
    public void Gate4_TheResultsFacesAndEdges_AreNamedFromItsOperands_InTheObjectsOwnFrame()
    {
        using var kernel = KernelForTests.New();

        // Box minus cylinder: the Blank's six faces bare — zmax one piece, with a hole — and the bore's wall.
        var lid = Faces(kernel, Subtract("lid", Box("", null, 0, 0, 500, 4000, 3000, 250), Cyl("bore", null, 2000, 1500, 400, 500, 300)));
        Assert.Equal(["bore:side", "xmax", "xmin", "ymax", "ymin", "zmax", "zmin"], lid.Select(f => f.Name).Order());

        // A Tool that is itself a boolean: its Blank's faces are outer:<face>, its Tools' outer:inner:<face>.
        var nested = Faces(kernel, Subtract("o", Box("", null, 0, 0, 0, 1000, 1000, 100),
            Subtract("outer", Box("", null, 100, 100, 50, 300, 300, 100), Box("inner", null, 150, 150, 40, 50, 50, 200))));
        Assert.Contains(nested, f => f.Name == "outer:zmin");
        Assert.Contains(nested, f => f.Name == "outer:inner:xmin");

        // A slab split in three: zmax#1…#3 by centroid x — and, turned a quarter turn and moved, the same names with
        // #1 still the piece at the lowest OWN-frame x, now the lowest world y.
        C3dBoolean Slab() => Subtract("slab", Box("", null, 0, 0, 0, 3000, 1000, 100),
            Box("c1", null, 900, -10, 50, 100, 1020, 100), Box("c2", null, 1900, -10, 50, 100, 1020, 100));
        var flat = Faces(kernel, Slab()).Where(f => f.Name.StartsWith("zmax", StringComparison.Ordinal)).ToList();
        Assert.Equal(["zmax#1", "zmax#2", "zmax#3"], flat.OrderBy(f => f.Box[0]).Select(f => f.Name));
        var turned = Slab();
        turned.Placement = new C3dPlacement { Origin = new C3dPoint3(7000 * Um, 0, 0), Rotate = [new C3dRotation { Axis = C3dAxis.Z, Deg = 90 }] };
        var moved = Faces(kernel, turned).Where(f => f.Name.StartsWith("zmax", StringComparison.Ordinal)).ToList();
        Assert.Equal(["zmax#1", "zmax#2", "zmax#3"], moved.OrderBy(f => f.Box[1]).Select(f => f.Name));

        // Two faces sharing two edges: a slot lying in the top (not across it, which would split zmax) meets zmax twice —
        // |1 and |2, by centroid x.
        var trench = new C3dCylinder { Name = "trench", Base = new C3dPoint3(1000 * Um, 200 * Um, 500 * Um), Axis = C3dAxis.Y, Length = 600 * Um, Radius = 200 * Um };
        var tree = GeometryKernelTree.From(Subtract("t", Box("", null, 0, 0, 0, 2000, 1000, 500), trench), 1000);
        var doubled = kernel.Edges(tree, 1).Where(e => e.Name.StartsWith("trench:side|zmax", StringComparison.Ordinal)).ToList();
        Assert.Equal(["trench:side|zmax|1", "trench:side|zmax|2"], doubled.OrderBy(e => e.Polyline[0]).Select(e => e.Name));

        // The Blank inside a written boolean carries no Name, and one written with a Name is a check error.
        Assert.DoesNotContain("\"Name\": \"\"", BooleanExample, StringComparison.Ordinal);
        var named = C3dPersistence.Deserialize(BooleanExample);
        ((C3dBoolean)named.Objects[0]).Blank!.Name = "lidbox";
        Assert.Contains(C3dValidation.Validate(named), d => d.Id == "c3d.operation.operand-named");
    }

    // ── 5. references through the Enabled toggle, with nothing rewritten ──────────────────────

    [KernelFact]
    public void Gate5_ABoundaryOnTheResult_LandsOnTheStandaloneOperandsWhenDisabled_AndTheFileIsUnchanged()
    {
        using var kernel = KernelForTests.New();
        string ws = Workspace();
        var doc = new C3dDocument
        {
            Objects = [Subtract("lid", Box("", "Alumina", 0, 0, 0, 1000, 1000, 200), Box("cav", "Alumina", 300, 300, 100, 400, 400, 200))],
            FaceBoundaries =
            [
                new C3dFaceBoundary { Object = "lid", Face = "zmax", Kind = Em3dFaceBoundaryKind.Pec },
                new C3dFaceBoundary { Object = "lid", Face = "cav:zmin", Kind = Em3dFaceBoundaryKind.Pec },
            ],
        };
        string path = WriteC3d(ws, "Lid", doc);
        string before = C3dPersistence.Serialize(doc);

        Assert.Equal([("lid", "zmax"), ("lid", "cav:zmin")], Landed(doc, path, kernel));
        ((C3dBoolean)doc.Objects[0]).Enabled = false;
        Assert.Equal([("lid", "zmax"), ("cav", "zmin")], Landed(doc, path, kernel));
        ((C3dBoolean)doc.Objects[0]).Enabled = true;
        Assert.Equal(before, C3dPersistence.Serialize(doc));

        // Put on lid's zmax BEFORE anything was subtracted from it, a boundary is on zmax afterwards too.
        var plain = new C3dDocument { Objects = [Box("lid", "Alumina", 0, 0, 0, 1000, 1000, 200)], FaceBoundaries = [doc.FaceBoundaries[0]] };
        Assert.Equal([("lid", "zmax")], Landed(plain, WriteC3d(ws, "Plain", plain), kernel));
    }

    // ── 6. disabled costs nothing; one edit builds one tree ───────────────────────────────────

    [Fact]
    public void Gate6_ADisabledBooleanOfManagedOperands_BuildsNoTree_AndOneEditToOneBooleanBuildsOne()
    {
        var fake = new FakeKernel();
        using var kernel = fake.Create();
        string ws = Workspace();

        var off = Subtract("lid", Box("", "Alumina", 0, 0, 0, 1000, 1000, 200), Cyl("bore", "Alumina", 500, 500, -10, 300, 100));
        off.Enabled = false;
        var offDoc = new C3dDocument { Objects = [off] };
        var el = new C3dElaborator(null, kernel);
        var e = el.Elaborate(offDoc, WriteC3d(ws, "Off", offDoc), null);
        Assert.Equal(["lid", "bore"], e.Solids.Select(s => s.Name));
        Assert.Equal(0, el.KernelTreesBuilt);
        Assert.Empty(fake.Requests);

        var doc = new C3dDocument
        {
            Objects =
            [
                Subtract("a", Box("", "Alumina", 0, 0, 0, 1000, 1000, 200), Cyl("ha", null, 500, 500, -10, 300, 100)),
                Subtract("b", Box("", "Alumina", 2000, 0, 0, 1000, 1000, 200), Cyl("hb", null, 2500, 500, -10, 300, 100)),
                Box("plain", "Alumina", 5000, 0, 0, 100, 100, 100),
            ],
        };
        string path = WriteC3d(ws, "Two", doc);
        el = new C3dElaborator(null, kernel);
        Assert.True(el.Elaborate(doc, path, null).Ok);
        Assert.Equal(2, el.KernelTreesBuilt);
        ((C3dCylinder)((C3dBoolean)doc.Objects[0]).Tools[0]).Radius = 200 * Um;     // an edit to one boolean
        el.Elaborate(doc, path, null);
        Assert.Equal(3, el.KernelTreesBuilt);
        ((C3dBox)doc.Objects[2]).Size = new C3dPoint3(200 * Um, 100 * Um, 100 * Um);  // an unrelated edit
        el.Elaborate(doc, path, null);
        el.Elaborate(doc, path, null);                                                // nothing changed
        Assert.Equal(3, el.KernelTreesBuilt);
    }

    // ── 7. precedence: a kept conductor Tool outranks the dielectric result ───────────────────

    [KernelFact]
    public void Gate7_AKeptConductorTool_LowersAboveTheDielectricResult()
    {
        using var kernel = KernelForTests.New();
        var b = Subtract("body", Box("", "Alumina", 0, 0, 0, 1000, 1000, 200), Cyl("pin", "Copper", 500, 500, -10, 220, 100));
        b.KeepTools = true;
        var doc = new C3dDocument { Objects = [b] };
        var e = new C3dElaborator(null, kernel).Elaborate(doc, WriteC3d(Workspace(), "Pin", doc), null);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        var body = e.Solids.Single(s => s.Name == "body");
        var pin = e.Solids.Single(s => s.Name == "pin");
        Assert.IsType<Em3dShapeSolid>(body.Primitive);
        Assert.Equal((Em3dRole.Dielectric, Em3dRole.Conductor), (body.Role, pin.Role));
        Assert.Equal(body.Order + 1, pin.Order);                 // immediately after the result
        var p = Em3dPrecedence.Of(e.Solids, e.Sheets);
        Assert.True(p.Of(pin) > p.Of(body));
    }

    /// <summary>brief-em3d-70 — a Subtract that keeps its Tools, NESTED as the Blank of a Unite (a housing with its bore kept
    /// as fill, then united with a flange): the kept Tool is still its own solid. Only the top level kept them before.</summary>
    [KernelFact]
    public void Gate7b_AKeptTool_OfASubtractNestedInAUnite_IsStillItsOwnSolid()
    {
        using var kernel = KernelForTests.New();
        var sub = Subtract("body", Box("", "Copper", 0, 0, 0, 1000, 1000, 200), Cyl("fill", "Alumina", 500, 500, -10, 220, 100));
        sub.KeepTools = true;
        var unite = C3dBooleans.Make(C3dBooleanOp.Unite, [Box("plate", "Copper", 1000, 0, 0, 100, 1000, 200), sub], 1, false);
        var doc = new C3dDocument { Objects = [unite] };
        var e = new C3dElaborator(null, kernel).Elaborate(doc, WriteC3d(Workspace(), "Nested", doc), null);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        Assert.Equal(["body", "fill"], e.Solids.Select(s => s.Name));
        Assert.Equal(Em3dRole.Dielectric, e.Solids.Single(s => s.Name == "fill").Role);
    }

    // ── 8. refused on open without the kernel — a disabled-only document included ─────────────

    [Fact]
    public void Gate8_WithoutTheKernel_AKernelDocumentOpensNothing_EvenWhenItsOnlyBooleanIsDisabled()
    {
        var absent = Absent();
        string ws = Workspace();
        var vm = new WorkspaceViewModel { KernelCapability = () => absent };
        var shown = new List<string>();
        vm.KernelRefusalDialog = (_, why) => { shown.Add(why); return Task.CompletedTask; };

        var on = C3dPersistence.Deserialize(BooleanExample);
        vm.OpenOrActivateC3dEditor(WriteC3d(ws, "On", on));
        var off = C3dPersistence.Deserialize(BooleanExample);
        ((C3dBoolean)off.Objects[0]).Enabled = false;          // could elaborate without the kernel, and is refused anyway
        vm.OpenOrActivateC3dEditor(WriteC3d(ws, "Off", off));

        string expected = GeometryKernel.NeedsKernel("'lid' (a Boolean)", absent);
        Assert.Equal([expected, expected], shown);

        // A document with none opens as before, and the kernel is not even asked.
        bool asked = false;
        var plain = new C3dDocument { Objects = [Box("b", "Alumina", 0, 0, 0, 10, 10, 10)] };
        Assert.Null(C3dKernelUse.RefusalOnOpen(plain, () => { asked = true; return absent; }));
        Assert.False(asked);

        // More than five are listed as five and "and N more".
        var many = new C3dDocument { Objects = [.. Enumerable.Range(0, 7).Select(i => (C3dObject)new C3dStep { Name = $"s{i}", File = "x.step", Part = "1" })] };
        Assert.StartsWith("'s0' (a Step part), 's1' (a Step part), 's2' (a Step part), 's3' (a Step part), 's4' (a Step part) and 2 more need ",
                          C3dKernelUse.RefusalOnOpen(many, () => absent), StringComparison.Ordinal);
    }

    // ── 9. check: exit 1 with the finding without the kernel; 0 with it, on every format example ─

    [Fact]
    public void Gate9_CheckWithoutTheKernel_ReportsEachKernelObjectInTheRefusalsWords_AndExitsOne()
    {
        string path = WriteC3d(Workspace(), "Lid", C3dPersistence.Deserialize(BooleanExample));
        string missing = Path.Combine(_root, "no-such-worker");
        var run = RunCli(new Dictionary<string, string> { [GeometryKernel.EnvironmentVariable] = missing }, "check", path);
        Assert.Equal(1, run.ExitCode);
        var cap = new GeometryKernel(new GeometryKernelOptions
        {
            Locate = () => GeometryKernelLocator.Locate(missing, AppContext.BaseDirectory, GeometryKernelLocator.ProcessRid,
                                                        GeometryKernelRecipe.ShippedRids, OperatingSystem.IsWindows()),
        }).Probe();
        Assert.Contains(GeometryKernel.NeedsKernel("'lid' (a Boolean)", cap), run.StdErr + run.StdOut, StringComparison.Ordinal);
    }

    [KernelFact]
    public void Gate9_CheckWithTheKernel_PassesEveryFormatExample_WrittenToDisk()
    {
        string ws = Workspace();
        using var kernel = KernelForTests.New();
        foreach (string which in (string[])[nameof(BooleanExample), nameof(FilletExample), nameof(ChamferExample), nameof(StepExample)])
        {
            var doc = C3dPersistence.Deserialize(Example(which));
            string path = WriteC3d(ws, which, doc);
            if (doc.Objects[0] is C3dStep step)
            {
                // A real STEP file, made by the kernel, copied where the object says, its hash recorded.
                byte[] bytes = kernel.Export([new GeometryKernelExportItem(GeometryKernelTree.From(Box("shell", null, 0, 0, 0, 500, 400, 300), 1000))], "step", "mm");
                string file = Path.Combine(Path.GetDirectoryName(path)!, step.File);
                File.WriteAllBytes(file, bytes);
                step.Hash = C3dValidation.StepHash(file);
                C3dPersistence.SaveToFile(path, doc);
            }
            var run = RunCli(null, "check", path);
            Assert.True(run.ExitCode == 0, $"{which}: {run.StdErr}{run.StdOut}");
        }
    }

    // ── 10. 3D editor round 5: a disabled Boolean INSIDE an enabled operation is as if it were not there ────

    [KernelFact]
    public void Gate10_Round5_ADisabledBooleanInsideAUnite_IsItsBlank_AndItsToolStandsAlone()
    {
        using var kernel = KernelForTests.New();
        var inner = Subtract("", Box("", "Alumina", 0, 0, 0, 1000, 1000, 200), Box("cav", "Copper", 300, 300, 100, 400, 400, 200));
        var doc = new C3dDocument
        {
            Objects = [new C3dBoolean { Name = "u", Op = C3dBooleanOp.Unite, Blank = inner, Tools = [Box("wing", "Alumina", 900, 0, 0, 600, 1000, 200)] }],
        };
        string path = WriteC3d(Workspace(), "Nest", doc);

        var on = new C3dElaborator(null, kernel).Elaborate(doc, path, null);
        Assert.True(on.Ok, string.Join(" ", on.Refusals));
        Assert.Equal(["u"], on.Solids.Select(s => s.Name));
        Assert.Contains(on.Provenance["u"].FaceNames, f => f.StartsWith("cav:", StringComparison.Ordinal));

        inner.Enabled = false;
        var off = new C3dElaborator(null, kernel).Elaborate(doc, path, null);
        Assert.True(off.Ok, string.Join(" ", off.Refusals));
        Assert.Equal([("u", "Alumina"), ("cav", "Copper")], off.Solids.Select(s => (s.Name, s.Material)));
        Assert.DoesNotContain(off.Provenance["u"].FaceNames, f => f.StartsWith("cav:", StringComparison.Ordinal));
    }

    // ── the format's examples, in the writer's own spelling (R-em3d64-1a) ─────────────────────

    private static string Example(string which) => which switch
    {
        nameof(BooleanExample) => BooleanExample,
        nameof(FilletExample)  => FilletExample,
        nameof(ChamferExample) => ChamferExample,
        _                      => StepExample,
    };

    private const string BooleanExample =
"""
{
	"FormatVersion": 1,
	"DbuPerMicron": 1000,
	"DisplayUnit": "Um",
	"SnapDbu": 0,
	"Objects": [
		{
			"$type": "Boolean",
			"Name": "lid",
			"Op": "Subtract",
			"Blank": {
				"$type": "Box",
				"Material": "Lid alloy",
				"Min": [0, 0, 500000],
				"Size": [4000000, 3000000, 250000]
			},
			"Tools": [
				{
					"$type": "Cylinder",
					"Name": "bore",
					"Base": [2000000, 1500000, 400000],
					"Axis": "Z",
					"Length": 500000,
					"Radius": 300000
				}
			]
		}
	]
}
""";

    private const string FilletExample =
"""
{
	"FormatVersion": 1,
	"DbuPerMicron": 1000,
	"DisplayUnit": "Um",
	"SnapDbu": 0,
	"Objects": [
		{
			"$type": "Fillet",
			"Name": "lid",
			"Radius": { "Expr": "r_fil", "Unit": "Um" },
			"Edges": [
				"bore:side|zmax"
			],
			"Target": {
				"$type": "Boolean",
				"Op": "Subtract",
				"Blank": {
					"$type": "Box",
					"Material": "Lid alloy",
					"Min": [0, 0, 500000],
					"Size": [4000000, 3000000, 250000]
				},
				"Tools": [
					{
						"$type": "Cylinder",
						"Name": "bore",
						"Base": [2000000, 1500000, 400000],
						"Axis": "Z",
						"Length": 500000,
						"Radius": 300000
					}
				]
			}
		}
	],
	"Variables": [
		{
			"Name": "r_fil",
			"Expression": "50",
			"Unit": "Um"
		}
	]
}
""";

    private const string ChamferExample =
"""
{
	"FormatVersion": 1,
	"DbuPerMicron": 1000,
	"DisplayUnit": "Um",
	"SnapDbu": 0,
	"Objects": [
		{
			"$type": "Chamfer",
			"Name": "pin",
			"Distance": 20000,
			"Edges": [
				"side|top"
			],
			"Target": {
				"$type": "Cylinder",
				"Material": "Lid alloy",
				"Base": [0, 0, 0],
				"Axis": "Z",
				"Length": 500000,
				"Radius": 150000
			}
		}
	]
}
""";

    private const string StepExample =
"""
{
	"FormatVersion": 1,
	"DbuPerMicron": 1000,
	"DisplayUnit": "Um",
	"SnapDbu": 0,
	"Objects": [
		{
			"$type": "Step",
			"Name": "shell",
			"Material": "Brass",
			"File": "sma-body.step",
			"Part": "1",
			"Hash": "sha256:9f2c",
			"Unit": "mm",
			"SourcePath": "../../../incoming/sma-body.step"
		}
	]
}
""";

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private static C3dBox Box(string name, string? material, long x, long y, long z, long dx, long dy, long dz)
        => new() { Name = name, Material = material, Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(dx * Um, dy * Um, dz * Um) };

    private static C3dCylinder Cyl(string name, string? material, long x, long y, long z, long length, long radius)
        => new() { Name = name, Material = material, Base = new C3dPoint3(x * Um, y * Um, z * Um), Length = length * Um, Radius = radius * Um };

    private static C3dBoolean Subtract(string name, C3dObject blank, params C3dObject[] tools)
        => new() { Name = name, Op = C3dBooleanOp.Subtract, Blank = blank, Tools = [.. tools] };

    private static IReadOnlyList<GeometryKernelFace> Faces(GeometryKernel kernel, C3dObject obj) => kernel.Faces(GeometryKernelTree.From(obj, 1000));

    /// <summary>Each face boundary of <paramref name="doc"/> as the problem states it: (object, primitive face).</summary>
    private static List<(string, string)> Landed(C3dDocument doc, string path, GeometryKernel kernel)
    {
        var e = new C3dElaborator(null, kernel).Elaborate(doc, path, null);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        Assert.Null(C3dProblemAssembly.FaceBoundaries(doc, e, [.. e.Materials], EmSetup.DefaultOperatingTempC, out var landed));
        return [.. landed.Select(b => (b.Object, b.Face))];
    }

    private static GeometryKernelCapability Absent() => new(false, GeometryKernelAbsence.Missing, null, null, "included with circuitRF",
        "The geometry kernel was not found at /nowhere/geometry-kernel.", "Reinstall circuitRF to restore it.");

    /// <summary>A workspace whose default technology holds a dielectric, two metals and a lid alloy.</summary>
    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials =
            [
                new TechMaterial { Name = "Alumina", Epsr = 9.8 },
                new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 },
                new TechMaterial { Name = "Lid alloy", Sigma20 = 2e6 },
                new TechMaterial { Name = "Brass", Sigma20 = 1.5e7 },
            ],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    private static string WriteC3d(string ws, string cell, C3dDocument doc)
    {
        string dir = Path.Combine(ws, cell, "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, cell + ".c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "circuitRF.slnx"))) return d.FullName;
        throw new InvalidOperationException("no source tree above the test assembly");
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(IReadOnlyDictionary<string, string>? env, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        string cliDir = typeof(OperationsInTheDocumentTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env ?? new Dictionary<string, string>()) psi.Environment[k] = v;
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}

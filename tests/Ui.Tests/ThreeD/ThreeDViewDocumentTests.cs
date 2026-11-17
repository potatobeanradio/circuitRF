// ================================================================
//  ThreeDViewDocumentTests.cs — the gate for brief-em3d-41: the 3D view and the .c3d document.
//
//  Headless, every one of them. Nothing in this brief draws or solves, so what there is to prove is
//  that the FORMAT will not move under briefs 42–52: it round-trips byte for byte, it refuses what it
//  cannot read by name, its validator lists every defect, a placement composes exactly, and every
//  place that walks a cell reference or switches on a view type knows the fourth one — including
//  when the fourth one is absent, which is every cell that exists today.
// ================================================================

using System.Diagnostics;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Archive;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class ThreeDViewDocumentTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-c3d-" + Guid.NewGuid().ToString("N")[..12]);

    public ThreeDViewDocumentTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── §6.1  Round trip, byte for byte ────────────────────────────────────────────────────────

    /// <summary>
    /// R-em3d41-3b: read then write is the bytes that were read. The fixtures are the writer's own
    /// output, pinned as TEXT so a change to the layout of the file — a key moved, a default no longer
    /// omitted — fails here rather than in a later brief's diff. Between them they hold every
    /// <c>$type</c>, a placement of each kind, prism, sheet and face holes, an instance with an array,
    /// an empty document, and a mil document carrying a reserved list and a key from a later build.
    /// </summary>
    [Theory]
    [InlineData(nameof(EveryKind))]
    [InlineData(nameof(Empty))]
    [InlineData(nameof(MilWithLaterKeys))]
    public void ReadThenWrite_IsByteIdentical(string fixture)
    {
        string text = Fixture(fixture);
        Assert.Equal(text, C3dPersistence.Serialize(C3dPersistence.Deserialize(text)));
    }

    [Fact]
    public void EveryKindFixture_HoldsEveryType_AndIsClean()
    {
        var doc = C3dPersistence.Deserialize(EveryKind);
        Assert.Equal(C3dObject.Kinds.Select(k => k.Name).Order(),
                     doc.Objects.Select(C3dObject.KindOf).Distinct().Order());
        Assert.Empty(C3dValidation.Validate(doc));
    }

    /// <summary>R-em3d41-2d: what this build cannot read is refused BY NAME — every unknown object
    /// kind listed, and a string where a number belongs called what it is.</summary>
    [Fact]
    public void AnUnknownKind_AndAnExpression_AreRefusedByName()
    {
        string later = EveryKind.Replace("\"$type\": \"Prism\"", "\"$type\": \"Torus\"")
                                .Replace("\"$type\": \"Polyhedron\"", "\"$type\": \"Loft\"");
        var kind = Assert.Throws<C3dReadException>(() => C3dPersistence.Deserialize(later));
        Assert.Equal("c3d.read.unknown-kind", kind.Diagnostic.Id);
        Assert.Contains("'lead' (a \"Torus\")", kind.Message, StringComparison.Ordinal);
        Assert.Contains("'lid' (a \"Loft\")", kind.Message, StringComparison.Ordinal);

        string expression = EveryKind.Replace("\"Length\": 254", "\"Length\": \"w\"");
        var expr = Assert.Throws<C3dReadException>(() => C3dPersistence.Deserialize(expression));
        Assert.Contains("is written as a bare string", expr.Message, StringComparison.Ordinal);
    }

    /// <summary>R-em3d41-2-dims: a negative size is normalised on write by moving the corner; a
    /// prism's height keeps its sign.</summary>
    [Fact]
    public void ANegativeSize_IsNormalisedByMovingTheCorner()
    {
        var doc = new C3dDocument();
        doc.Objects.Add(new C3dBox { Name = "b", Material = "Gold", Min = new(10, 10, 10), Size = new(-4, 5, -6) });
        var back = (C3dBox)C3dPersistence.Deserialize(C3dPersistence.Serialize(doc)).Objects[0];
        Assert.Equal(new C3dPoint3(6, 10, 4), back.Min);
        Assert.Equal(new C3dPoint3(4, 5, 6), back.Size);
        Assert.Equal(-50, ((C3dPrism)C3dPersistence.Deserialize(EveryKind).Objects[1]).Height);
    }

    // ── §6.2  Every ViewType is accepted everywhere that must accept it ────────────────────────

    /// <summary>
    /// R-em3d41-1c: the functions that must accept every member, enumerated over the enum — so a
    /// FIFTH view type fails here, in one place, instead of crashing in whichever switch was missed.
    /// The Project Tree half scans a cell holding one empty file of every type.
    /// </summary>
    [Fact]
    public void EveryViewType_IsAcceptedByTheFunctionsThatMustAcceptIt()
    {
        string cell = CellFolder.CreateCellFolder(_root, "Amp");
        var types = Enum.GetValues<ViewType>();

        Assert.Equal(types.Length, types.Select(CellFolder.SubFolderName).Distinct().Count());
        Assert.Equal(types.Length, types.Select(CellFolder.ViewExtension).Distinct().Count());
        Assert.Equal(types.Length, types.Select(CellFolder.ViewNoun).Distinct().Count());

        var ccell = new CcellFile();
        foreach (var t in types)
        {
            Assert.Equal(PrimaryState.NoView, CellFolder.ResolvePrimary(cell, t).State);
            Assert.Equal(t, CellViewFileValidator.ViewTypeFor("x" + CellFolder.ViewExtension(t)));
            ccell.SetPrimary(t, t + ".f");
            Assert.Equal(t + ".f", ccell.GetPrimary(t));
        }

        // One empty-but-valid view of every type, each through CellCreate — the writers the GUI calls.
        foreach (var t in types)
        {
            string path = t switch
            {
                ViewType.Schematic => CellCreate.WriteSchematicView(cell, "Amp", "Amp"),
                ViewType.Symbol    => CellCreate.WriteSymbolView(cell, "Amp"),
                ViewType.Layout    => CellCreate.WriteLayoutView(cell, "Amp", CellCreate.NewLayoutView(null)),
                ViewType.ThreeD    => CellCreate.WriteThreeDView(cell, "Amp", CellCreate.NewThreeDView(cell, null)),
                _ => throw new InvalidOperationException($"no writer for {t}: add one to CellCreate"),
            };
            Assert.Null(CellViewFileValidator.DescribeDefect(path, t));
            Assert.Equal(PrimaryState.SoleFile, CellFolder.ResolvePrimary(cell, t).State);
        }

        var cellNode = WorkspaceScanner.Scan(_root).Children.Single(n => n.Kind == NodeKind.Cell);
        Assert.Null(cellNode.WarningReason);
        var groups = cellNode.Children.Where(n => n.Kind == NodeKind.CellViewFolder).ToList();
        Assert.Equal(types.Select(CellFolder.SubFolderName), groups.Select(g => g.Name));   // 3D after Layout
        Assert.All(groups, g => Assert.True(g.Children.Single().IsPrimary));
    }

    // ── §6.3  Absent 3d/ everywhere ─────────────────────────────────────────────────────────────

    /// <summary>
    /// D10: a workspace from before this brief — cells with no <c>3d/</c> — opens, scans, is counted,
    /// renamed and archived with no <c>3d/</c> created anywhere and no warning. The folder is made by
    /// writing a 3D view and by nothing else.
    /// </summary>
    [Fact]
    public void AWorkspaceWithNo3dFolders_ScansRenamesAndArchives_AndCreatesNone()
    {
        var made = WorkspaceCreate.Create(_root, "Old", technologyId: null);
        string ws = made.WorkspaceDir;
        string amp  = CellCreate.Create(ws, "Amp",  CellViews.Schematic | CellViews.Layout).CellDir;
        string root = CellCreate.Create(ws, "Top",  CellViews.Schematic).CellDir;
        Assert.False(Directory.Exists(CellFolder.SubFolderPath(amp, ViewType.ThreeD)));

        var tree = WorkspaceScanner.Scan(ws);
        Assert.DoesNotContain(Flatten(tree), n => n.WarningReason is not null);

        Assert.Equal(0, CellUsageScanner.CountReferencingCells(ws, amp).Count);
        Assert.Equal(PrimaryState.NoView, CellFolder.ResolvePrimary(root, ViewType.ThreeD).State);

        string renamed = Path.Combine(ws, "Amp2");
        Directory.Move(amp, renamed);
        CellUsageScanner.RewriteCellReferences(ws, amp, "Amp2", out var failed);
        Assert.Empty(failed);
        var outcomes = PrimaryViewRename.ToCellName(renamed, "Amp2");
        Assert.Equal(PrimaryRenameOutcome.NoPrimary, outcomes.Single(o => o.ViewType == ViewType.ThreeD).Outcome);

        WorkspaceArchiveWriter.Write(WorkspaceArchiveScanner.Scan(ws), Path.Combine(_root, "old.zip"));

        Assert.Empty(Directory.GetDirectories(ws, CellFolder.ThreeDSubFolder, SearchOption.AllDirectories));
    }

    // ── §6.4  Validation lists all problems ─────────────────────────────────────────────────────

    /// <summary>
    /// R-em3d41-2f: five defects, five findings — a duplicate name, a two-point outline, an open
    /// polyhedron, a non-planar face and a zero-volume box — each once, not the first one only.
    /// The non-planar face is the twisted quad of a closed pyramid, so it is the ONLY bad face.
    /// </summary>
    [Fact]
    public void AFixtureWithFiveDefects_ReportsFive()
    {
        var doc = new C3dDocument();
        doc.Objects.Add(new C3dBox   { Name = "slab", Material = "Gold", Size = new(10, 10, 0) });            // zero volume
        doc.Objects.Add(new C3dBox   { Name = "dup",  Material = "Gold", Size = new(1, 1, 1) });
        doc.Objects.Add(new C3dBox   { Name = "dup",  Material = "Gold", Size = new(1, 1, 1) });              // duplicate
        doc.Objects.Add(new C3dPrism { Name = "flat", Material = "Gold", Outline = [new(0, 0), new(5, 0), new(0, 0)], Height = 5 });
        doc.Objects.Add(new C3dPolyhedron                                                                      // open: no base
        {
            Name = "open", Material = "Gold",
            Vertices = [new(0, 0, 0), new(1000, 0, 0), new(0, 1000, 0), new(0, 0, 1000)],
            Faces = [new C3dFace { Name = "front", Outer = [0, 1, 3] }, new C3dFace { Name = "left", Outer = [0, 3, 2] },
                     new C3dFace { Name = "slope", Outer = [1, 2, 3] }],
        });
        doc.Objects.Add(new C3dPolyhedron                                                                      // twisted quad
        {
            Name = "twist", Material = "Gold",
            Vertices = [new(0, 0, 0), new(1000, 0, 0), new(1000, 1000, 50), new(0, 1000, 0), new(500, 500, 2000)],
            Faces = [new C3dFace { Name = "base", Outer = [0, 3, 2, 1] },
                     new C3dFace { Name = "s0", Outer = [0, 1, 4] }, new C3dFace { Name = "s1", Outer = [1, 2, 4] },
                     new C3dFace { Name = "s2", Outer = [2, 3, 4] }, new C3dFace { Name = "s3", Outer = [3, 0, 4] }],
        });

        var found = C3dValidation.Validate(doc);

        Assert.Equal(
            ["c3d.name.duplicate", "c3d.outline.too-few-points", "c3d.polyhedron.not-closed",
             "c3d.polyhedron.not-planar", "c3d.solid.zero-volume"],
            found.Select(d => d.Id).Order());
        Assert.Contains("0–1", found.Single(d => d.Id == "c3d.polyhedron.not-closed").Render(), StringComparison.Ordinal);
    }

    // ── §6.5  Rename Cell rewrites a .c3d reference; Remove Cell counts it ─────────────────────

    [Fact]
    public void RenameCell_RewritesAC3dInstance_AndRemoveCellCountsIt()
    {
        string mmic = CellFolder.CreateCellFolder(_root, "MMIC");
        string pkg  = CellFolder.CreateCellFolder(_root, "Package");
        var doc = new C3dDocument();
        doc.Instances.Add(new C3dInstance { Name = "U1", CellRef = "../../MMIC" });
        string c3d = CellCreate.WriteThreeDView(pkg, "Package", doc);

        Assert.Equal(1, CellUsageScanner.CountReferencingCells(_root, mmic).Count);

        Directory.Move(mmic, Path.Combine(_root, "Die"));
        var rewritten = CellUsageScanner.RewriteCellReferences(_root, mmic, "Die", out var failed);

        Assert.Empty(failed);
        Assert.Equal([c3d], rewritten);
        var back = C3dPersistence.LoadFromFile(c3d);
        Assert.Equal("../../Die", back.Instances.Single().CellRef);
        // Rewritten through its own writer: the file is in the layout every save produces.
        Assert.Equal(C3dPersistence.Serialize(back), File.ReadAllText(c3d));
    }

    // ── §6.6  Placement ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-em3d41-3c: every composition of quarter turns about X, Y and Z is one of 24 integer matrices,
    /// and the mirror doubles that to 48 — distinct, and each exactly invertible: inverse ∘ placement
    /// is the identity with no tolerance, translation included.
    /// </summary>
    [Fact]
    public void QuarterTurnsAndTheMirror_Give48DistinctIntegerMatrices_EachExactlyInvertible()
    {
        var degrees = new[] { 0.0, 90, 180, 270 };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (bool mirror in (bool[])[false, true])
        foreach (double x in degrees)
        foreach (double y in degrees)
        foreach (double z in degrees)
        {
            var placement = new C3dPlacement
            {
                Origin  = new C3dPoint3(123_456, -7, 25_400),
                MirrorX = mirror,
                Rotate  = [new() { Axis = C3dAxis.X, Deg = x }, new() { Axis = C3dAxis.Y, Deg = y },
                           new() { Axis = C3dAxis.Z, Deg = z }],
            };
            var t = placement.ToTransform();

            var m = t.IntegerMatrix();
            Assert.NotNull(m);
            Assert.True(t.IsIntegral);
            seen.Add(string.Join(",", m!));

            Assert.Equal(C3dTransform.Identity, t.Then(t.Inverse()));
            Assert.Equal(C3dTransform.Identity, t.Inverse().Then(t));
        }

        Assert.Equal(48, seen.Count);
    }

    // ── §6.8  Classify: a .c3d from another program is named foreign ───────────────────────────

    /// <summary>
    /// Overview §1c: the extension is shared with a motion-capture format, whose files are binary.
    /// One of those, handed to <c>check</c>, is named as not a 3D view and exits 0 — not reported as a
    /// broken one. A real 3D view with a defect, beside it, exits 1 with the validator's own finding.
    /// </summary>
    [Fact]
    public void Check_NamesAForeignC3d_AndReportsARealOnesFindings()
    {
        string foreign = Path.Combine(_root, "walk.c3d");
        File.WriteAllBytes(foreign, [0x02, 0x50, 0x01, 0x00, 0x1A, 0x00, 0x00, 0x00, 0xFF, 0x00]);
        Assert.False(C3dPersistence.LooksLikeC3d(foreign));

        var run = RunCli("check", foreign);
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("is not a circuitRF 3D view", run.StdErr, StringComparison.Ordinal);

        var doc = new C3dDocument();
        doc.Objects.Add(new C3dBox { Name = "slab", Material = "Gold", Size = new(10, 10, 0) });
        string mine = Path.Combine(_root, "mine.c3d");
        C3dPersistence.SaveToFile(mine, doc);
        Assert.True(C3dPersistence.LooksLikeC3d(mine));

        var bad = RunCli("check", mine);
        Assert.Equal(1, bad.ExitCode);
        Assert.Contains("The box 'slab' has no volume", bad.StdErr, StringComparison.Ordinal);
    }

    // ── §4  New 3D View takes its units from the cell's layout, else the technology, else µm ──

    [Fact]
    public void ANewThreeDView_TakesItsUnits_FromTheLayout_ThenTheTechnology_ThenMicrons()
    {
        var um = new Technology { DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 5 };
        var mm = new Technology { DefaultDisplayUnit = LayoutUnit.Mm, DefaultSnapDbu = 10_000 };

        // A cell with a .clay in mil, under a µm technology: the layout's unit and DBU win.
        string withClay = CellFolder.CreateCellFolder(_root, "WithClay");
        var clay = CellCreate.NewLayoutView(um);
        clay.DisplayUnit  = LayoutUnit.Mil;
        clay.DbuPerMicron = 100;
        CellCreate.WriteLayoutView(withClay, "WithClay", clay);
        var fromClay = CellCreate.NewThreeDView(withClay, um);
        Assert.Equal((LayoutUnit.Mil, 100, 5L), (fromClay.DisplayUnit, fromClay.DbuPerMicron, fromClay.SnapDbu));

        // No .clay, a mm technology: the technology's.
        string bare = CellFolder.CreateCellFolder(_root, "Bare");
        var fromTech = CellCreate.NewThreeDView(bare, mm);
        Assert.Equal((LayoutUnit.Mm, LayoutUnits.DefaultDbuPerMicron, 10_000L),
                     (fromTech.DisplayUnit, fromTech.DbuPerMicron, fromTech.SnapDbu));

        // Neither: µm.
        Assert.Equal(LayoutUnit.Um, CellCreate.NewThreeDView(bare, null).DisplayUnit);
        Assert.Null(fromTech.TechRef);
        Assert.False(Directory.Exists(CellFolder.SubFolderPath(bare, ViewType.ThreeD)));   // nothing written by asking
    }

    // A new 3D view's realistic Look shows every chrome row but the air box; a document stating no Look still hides them all.
    [Fact]
    public void ANewThreeDView_ShowsEveryChromeRowButTheAirBox_InTheRealisticView()
    {
        var look = CircuitRF.Render.Scene3D.Look.RealisticLook.From(CellCreate.NewThreeDView(CellFolder.CreateCellFolder(_root, "Look"), null).Look);
        foreach (var (row, _) in CircuitRF.Render.Scene3D.Look.RealisticLook.Chrome)
        {
            Assert.Equal(row != CircuitRF.Render.Scene3D.Look.Scene3DChrome.AirBox, look.Shows(row));
            Assert.False(CircuitRF.Render.Scene3D.Look.RealisticLook.Default.Shows(row));
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static string Fixture(string name) => name switch
    {
        nameof(EveryKind)        => EveryKind,
        nameof(Empty)            => Empty,
        nameof(MilWithLaterKeys) => MilWithLaterKeys,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static IEnumerable<ProjectTreeNode> Flatten(ProjectTreeNode node)
    {
        yield return node;
        foreach (var c in node.Children)
            foreach (var d in Flatten(c))
                yield return d;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
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

    /// <summary>The built CLI, in this test assembly's configuration — AuthoringCliVerbTests.CliDll's
    /// rule, for its reason.</summary>
    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(ThreeDViewDocumentTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }

    // ── fixtures: the writer's own output, pinned (closing quotes at column 0, so the text is verbatim) ──

    private const string EveryKind =
"""
{
	"FormatVersion": 1,
	"DbuPerMicron": 1000,
	"DisplayUnit": "Um",
	"SnapDbu": 1000,
	"Objects": [
		{
			"$type": "Box",
			"Name": "base",
			"Material": "Alumina",
			"Min": [0, 0, 0],
			"Size": [5000, 4000, 254],
			"Placement": {
				"Origin": [100, 200, 0]
			}
		},
		{
			"$type": "Prism",
			"Name": "lead",
			"Material": "Gold",
			"Role": "Conductor",
			"Plane": "YZ",
			"Offset": 254,
			"Outline": [
				[0, 0],
				[800, 0],
				[800, 300],
				[0, 300]
			],
			"Holes": [
				[
					[100, 100],
					[200, 100],
					[200, 200]
				]
			],
			"Height": -50,
			"Shear": [10, 0],
			"Placement": {
				"Rotate": [
					{
						"Axis": "X",
						"Deg": 90
					},
					{
						"Axis": "Z",
						"Deg": 30
					}
				]
			}
		},
		{
			"$type": "Cylinder",
			"Name": "via",
			"Material": "Gold",
			"Base": [500, 500, 0],
			"Axis": "Z",
			"Length": 254,
			"Radius": 76,
			"Placement": {
				"MirrorX": true
			}
		},
		{
			"$type": "CylinderSheet",
			"Name": "rung",
			"Material": "Gold",
			"Axis": "Z",
			"Base": [500, 500, 0],
			"Radius": 300,
			"Length": 254,
			"StartDeg": -10,
			"SweepDeg": 20
		},
		{
			"$type": "Sphere",
			"Name": "bump",
			"Material": "Gold",
			"Centre": [900, 500, 100],
			"Radius": 50
		},
		{
			"$type": "Ellipsoid",
			"Name": "dome",
			"Material": "Gold",
			"Centre": [900, 900, 100],
			"SemiAxes": [80, 60, 40]
		},
		{
			"$type": "Phantom",
			"Name": "head",
			"Shape": "Ellipsoid3",
			"Centre": [3000, 3000, 3000],
			"SemiAxes": [1000, 800, 1000],
			"Shell1": 90,
			"Shell2": 90,
			"Layers": [
				{
					"Name": "skin",
					"Material": "Gold"
				},
				{
					"Name": "skull",
					"Material": "Gold"
				},
				{
					"Name": "brain",
					"Material": "Gold"
				}
			]
		},
		{
			"$type": "VoxelBody",
			"Name": "anatomy",
			"File": "anatomy.nii.gz",
			"Hash": "",
			"Labels": [
				{
					"Label": 1,
					"Name": "muscle",
					"Material": "Gold"
				},
				{
					"Label": 2,
					"Name": "lumen",
					"Air": true
				}
			],
			"GridStep": 2000
		},
		{
			"$type": "Mesh",
			"Name": "organ",
			"Material": "Gold",
			"Role": "Dielectric",
			"File": "organ.stl",
			"Hash": "",
			"Unit": "mm"
		},
		{
			"$type": "Sheet",
			"Name": "trace",
			"Material": "Gold",
			"Plane": "XY",
			"Offset": 254,
			"Rect": {
				"Min": [0, 1000],
				"Size": [5000, 200]
			},
			"ThicknessUm": 5
		},
		{
			"$type": "Sheet",
			"Name": "pad",
			"Material": "Gold",
			"Plane": "XZ",
			"Offset": 0,
			"Outline": [
				[0, 0],
				[300, 0],
				[300, 300],
				[0, 300]
			],
			"Holes": [
				[
					[100, 100],
					[200, 100],
					[200, 200],
					[100, 200]
				]
			]
		},
		{
			"$type": "Polyline",
			"Name": "path",
			"Plane": "XY",
			"Offset": 0,
			"Points": [
				[0, 0],
				[100, 0],
				[100, 100]
			],
			"Closed": true,
			"Hidden": true
		},
		{
			"$type": "Polyhedron",
			"Name": "lid",
			"Material": "Lid alloy",
			"Vertices": [
				[0, 0, 0],
				[1000, 0, 0],
				[0, 1000, 0],
				[0, 0, 1000]
			],
			"Faces": [
				{
					"Name": "base",
					"Outer": [0, 2, 1]
				},
				{
					"Name": "front",
					"Outer": [0, 1, 3]
				},
				{
					"Name": "left",
					"Outer": [0, 3, 2]
				},
				{
					"Name": "slope",
					"Outer": [1, 2, 3]
				}
			]
		},
		{
			"$type": "Wire",
			"Name": "w1",
			"Material": "Gold",
			"Points": [
				[500, 0, 254],
				[1500, 0, 900],
				[3000, 0, 254]
			],
			"DiameterUm": 25.4,
			"Start": {
				"Style": "Ball"
			},
			"End": {
				"Style": "Wedge",
				"FootLengthUm": 50.8
			}
		},
		{
			"$type": "Boolean",
			"Name": "cap",
			"Op": "Subtract",
			"Enabled": false,
			"KeepTools": true,
			"Blank": {
				"$type": "Box",
				"Material": "Lid alloy",
				"Min": [0, 0, 1000],
				"Size": [2000, 2000, 100]
			},
			"Tools": [
				{
					"$type": "Cylinder",
					"Name": "bore",
					"Material": "Alumina",
					"Base": [1000, 1000, 900],
					"Axis": "Z",
					"Length": 300,
					"Radius": 200
				}
			]
		},
		{
			"$type": "Fillet",
			"Name": "rim",
			"Radius": 20,
			"Edges": [
				"xmax|zmax"
			],
			"Target": {
				"$type": "Box",
				"Material": "Gold",
				"Min": [3000, 0, 0],
				"Size": [500, 500, 500]
			}
		},
		{
			"$type": "Chamfer",
			"Name": "pin",
			"Distance": 20,
			"Distance2": 10,
			"Edges": [
				"side|top"
			],
			"Target": {
				"$type": "Cylinder",
				"Material": "Gold",
				"Base": [4000, 0, 0],
				"Axis": "Z",
				"Length": 500,
				"Radius": 100
			}
		},
		{
			"$type": "Split",
			"Name": "half",
			"Plane": "YZ",
			"At": 5250,
			"Keep": "Negative",
			"Target": {
				"$type": "Box",
				"Material": "Gold",
				"Min": [5000, 0, 0],
				"Size": [500, 500, 500]
			}
		},
		{
			"$type": "Step",
			"Name": "shell",
			"Material": "Lid alloy",
			"File": "shell.step",
			"Part": "1",
			"Hash": "sha256:00",
			"Unit": "mm"
		}
	],
	"Instances": [
		{
			"Name": "U1",
			"CellRef": "../MMIC",
			"View": "Layout",
			"Placement": {
				"Origin": [10, 20, 30],
				"Rotate": [
					{
						"Axis": "Z",
						"Deg": 180
					}
				],
				"MirrorX": true
			},
			"Array": {
				"Counts": [4, 1, 1],
				"Pitch": [2540, 0, 0]
			}
		}
	]
}
""";

    private const string Empty =
"""
{
	"FormatVersion": 1,
	"DbuPerMicron": 1000,
	"DisplayUnit": "Um",
	"SnapDbu": 0,
	"Objects": []
}
""";

    private const string MilWithLaterKeys =
"""
{
	"FormatVersion": 1,
	"DbuPerMicron": 1000,
	"DisplayUnit": "Mil",
	"SnapDbu": 25400,
	"TechRef": "../../tech/package.ctech",
	"Objects": [
		{
			"$type": "Polyhedron",
			"Name": "frame",
			"Material": "Lid alloy",
			"Vertices": [
				[0, 0, 0],
				[254000, 0, 0],
				[254000, 254000, 0],
				[0, 254000, 0],
				[63500, 63500, 0],
				[190500, 63500, 0],
				[190500, 190500, 0],
				[63500, 190500, 0]
			],
			"Faces": [
				{
					"Name": "bottom",
					"Outer": [0, 3, 2, 1],
					"Holes": [
						[4, 5, 6, 7]
					]
				}
			]
		}
	],
	"Variables": [
		{
			"Name": "w",
			"Expression": "10",
			"Unit": "Mil"
		}
	],
	"Booleans": []
}
""";
}

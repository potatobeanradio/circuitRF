using System.Text.RegularExpressions;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// brief-L3d-arbitrary-angle-instances.md gates 3, 4, 7 (end to end), 10 and 11 — the parts that need
/// a cell folder on disk, a real renderer, or the source tree itself.
/// </summary>
public sealed class L3dArbitraryAngleFlattenAndRenderTests : IDisposable
{
    private static readonly LayerKey LayerA = new(1, 0);
    private readonly string _root;

    public L3dArbitraryAngleFlattenAndRenderTests()
    {
        _root = Directory.CreateTempSubdirectory("l3d-").FullName;
        CellLayoutResolver.InvalidateUnder(_root);
        // A placeholder or label draws text via SkiaFonts.PlexRegular, which cannot load without a
        // live Avalonia app host — the same seam every L3a renderer test uses.
        LayoutTextOutline.TestOverrideTypeface = SKTypeface.Default;
    }

    public void Dispose()
    {
        LayoutTextOutline.TestOverrideTypeface = null;
        CellLayoutResolver.InvalidateUnder(_root);
        Directory.Delete(_root, recursive: true);
    }

    private static Technology MakeTech() => new()
    {
        Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers = [new LayerDef { Key = LayerA, Name = "L1", Color = new CircuitRF.Ui.Theming.Rgba(255, 0, 0), FillOpacity = 0.6, ZOrder = 0, Visible = true, Selectable = true }],
    };

    private string CreateCell(string name, Action<LayoutView> populate)
    {
        var cellDir = CellFolder.CreateCellFolder(_root, name);
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        populate(view);
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, $"{name}.clay"), view);

        var ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.PrimaryLayout = $"{name}.clay";
        CellPersistence.SaveToFile(ccellPath, ccell);
        return cellDir;
    }

    /// <summary>Deliberately mixes an L-shaped polygon (asymmetric under rotation, so a wrong sign
    /// cannot pass by accident) with a RECT and a ROUNDED RECT — the two shapes whose type presumes
    /// axis alignment, and therefore the whole point of the render comparison below.</summary>
    private static void KitchenSink(LayoutView v)
    {
        v.Shapes.Add(new PolygonShape { Layer = LayerA, Xy = [0, 0, 3_000, 0, 3_000, 1_000, 1_000, 1_000, 1_000, 3_000, 0, 3_000] });
        v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 4_000, Y1 = 0, X2 = 8_000, Y2 = 2_000 });
        v.Shapes.Add(new RoundedRectShape { Layer = LayerA, X1 = 4_000, Y1 = 3_000, X2 = 8_000, Y2 = 5_000, CornerRadius = 500, FlattenTolDbu = 10 });
        v.Shapes.Add(new CircleShape { Layer = LayerA, Cx = 10_000, Cy = 2_000, R = 800, FlattenTolDbu = 10 });
        v.Shapes.Add(new ViaShape { Layer = LayerA, X = 10_000, Y = 4_500, PadSize = 600, DrillSize = 300, LandingLayer = LayerA });
    }

    private LayoutInstance PlaceInto(string topDir, string leafDir, double degrees, bool mirror = false)
    {
        var topLayoutDir = CellFolder.SubFolderPath(topDir, ViewType.Layout);
        // Never a hand-typed "../Leaf" — that is one level too shallow and silently yields an
        // unresolved placeholder that makes a pixel comparison pass vacuously (the trap
        // LayoutGdsiiTransformTests records at length).
        var cellRef = Path.GetRelativePath(topLayoutDir, leafDir);
        Assert.Equal(CellLayoutState.Resolved, CellLayoutResolver.Resolve(cellRef, topLayoutDir).State);
        return new LayoutInstance
        {
            CellRef = cellRef, X = 12_000, Y = 7_000, RotationDegrees = degrees, MirrorX = mirror, Mag = 1.0,
        };
    }

    /// <summary>A viewport wide enough to hold the whole instance grid, so the counter comparison is
    /// over 40 DRAWN placements rather than over whatever happened to be on screen.</summary>
    private static void RenderWide(LayoutView view, Technology tech, string baseDir, out LayoutRenderResult stats)
    {
        var vp = new LayoutViewport(-10_000, -10_000, 0.004, 800, 600);
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = baseDir };
        stats = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
    }

    private static byte[] Render(LayoutView view, Technology tech, string baseDir, out LayoutRenderResult stats)
    {
        var vp = new LayoutViewport(-4_000, -4_000, 0.02, 400, 400);
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = baseDir };
        stats = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.Bytes;
    }

    // ── Gate 4: a non-cardinal placement renders the same flattened as it does as an instance ────

    /// <summary>
    /// Gate 4 — flattening an instance does not change what you see, at any angle.
    ///
    /// <para><b>The bound is 0.5% of pixels rather than byte equality, and that is a MEASURED
    /// property of this codebase rather than a hedge.</b> Instance rendering draws the sub-cell's
    /// cached path under a matrix; a flattened shape has already been rounded to integer DBU by
    /// <see cref="LayoutInstanceTransform.TransformPoint"/>. The two therefore land on slightly
    /// different subpixel positions and antialias differently. Measured on this fixture: 0 deg differs
    /// by ZERO bytes (nothing is transformed, so nothing rounds), and thereafter 90 deg 0.001%,
    /// 30 deg 0.009%, 180 deg 0.125%, 137.5 deg 0.076%, 212.25 deg mirrored 0.221% — the WORST cases
    /// are cardinal or mirrored ones that predate L3d entirely, so this is sub-DBU rounding, not
    /// anything arbitrary angles introduced. Cardinal cases are included in the theory below
    /// deliberately, as the control that says so.</para>
    ///
    /// <para>The bound still has real power: the bug this gate exists for — a rect walked through a
    /// rotating transform collapsing to its axis-aligned BOUNDING BOX — changes a 4000x2000 rect at
    /// 45 deg into a shape of three times the area, which is tens of thousands of pixels, not hundreds.
    /// <c>TheOracleCanFail</c> below pins that the comparison is capable of rejecting.</para>
    /// </summary>
    [Theory]
    [InlineData(0.0, false)]
    [InlineData(90.0, false)]
    [InlineData(180.0, false)]
    [InlineData(30.0, false)]
    [InlineData(137.5, false)]
    [InlineData(30.0, true)]
    [InlineData(212.25, true)]
    public void Instance_RendersTheSameAsItsOwnFlattenedResult_AtEveryAngle(double degrees, bool mirror)
    {
        var (instancePixels, flatPixels) = RenderBothWays(degrees, mirror);
        var (differing, maxDelta) = Compare(instancePixels, flatPixels);

        double fraction = (double)differing / instancePixels.Length;
        Assert.True(fraction < 0.005, $"{fraction:P3} of pixels differ at {degrees}° (mirror={mirror})");
        Assert.True(maxDelta <= 64, $"max channel delta {maxDelta} at {degrees}° — edge antialiasing only, never a displaced fill");
    }

    /// <summary>0 deg transforms nothing and therefore rounds nothing: the two render paths agree
    /// EXACTLY. This is what makes the tolerance above attributable to coordinate rounding rather
    /// than to a difference between the paths themselves.</summary>
    [Fact]
    public void AtZeroDegrees_InstanceAndFlattenedRendersAreByteIdentical()
    {
        var (instancePixels, flatPixels) = RenderBothWays(0.0, mirror: false);
        Assert.Equal(instancePixels, flatPixels);
    }

    /// <summary>The comparison above is only worth having if it can reject. A flattened result from a
    /// DIFFERENT angle must blow straight through both bounds.</summary>
    [Fact]
    public void TheOracleCanFail_AFlattenFromADifferentAngleIsRejected()
    {
        var (instanceAt30, _) = RenderBothWays(30.0, mirror: false);
        var (_, flatAt137) = RenderBothWays(137.5, mirror: false);
        var (differing, _) = Compare(instanceAt30, flatAt137);
        Assert.True((double)differing / instanceAt30.Length > 0.005);
    }

    private (byte[] Instance, byte[] Flat) RenderBothWays(double degrees, bool mirror)
    {
        var leafDir = CreateCell($"Leaf{Guid.NewGuid():N}", KitchenSink);
        var topDir = CreateCell($"Top{Guid.NewGuid():N}", _ => { });
        var topLayoutDir = CellFolder.SubFolderPath(topDir, ViewType.Layout);

        var inst = PlaceInto(topDir, leafDir, degrees, mirror);
        var tech = MakeTech();

        var asInstance = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        asInstance.Instances.Add(inst);
        var instancePixels = Render(asInstance, tech, topLayoutDir, out var instanceStats);
        Assert.Equal(1, instanceStats.InstancesDrawn);   // never a vacuous comparison of two placeholders

        var result = LayoutFlatten.FlattenOneLevel(inst, topLayoutDir);
        Assert.NotNull(result);
        var flattened = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        foreach (var shape in result!.Shapes) flattened.Shapes.Add(shape);
        return (instancePixels, Render(flattened, tech, topLayoutDir, out _));
    }

    private static (int Differing, int MaxDelta) Compare(byte[] a, byte[] b)
    {
        int differing = 0, maxDelta = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Abs(a[i] - b[i]);
            if (d != 0) { differing++; maxDelta = Math.Max(maxDelta, d); }
        }
        return (differing, maxDelta);
    }

    /// <summary>
    /// Gate 7's geometric half — the assertion that does NOT depend on a renderer. A 4000x2000 rect
    /// placed at 30 deg must come out of flatten as four corners at hand-computed positions. The
    /// failure this catches is the bounding box, whose corners would be axis-aligned.
    /// </summary>
    [Fact]
    public void ARectFlattenedAt30Degrees_LandsOnHandComputedCorners()
    {
        var leafDir = CreateCell("Leaf", v =>
            v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 4_000, Y2 = 2_000 }));
        var topDir = CreateCell("Top", _ => { });
        var topLayoutDir = CellFolder.SubFolderPath(topDir, ViewType.Layout);

        var inst = PlaceInto(topDir, leafDir, 30.0);
        var poly = Assert.IsType<PolygonShape>(LayoutFlatten.FlattenOneLevel(inst, topLayoutDir)!.Shapes.Single());

        const double rad = 30.0 * Math.PI / 180.0;
        double c = Math.Cos(rad), sn = Math.Sin(rad);
        var expected = new (long X, long Y)[] { (0, 0), (4_000, 0), (4_000, 2_000), (0, 2_000) }
            .Select(p => ((long)Math.Round(p.X * c - p.Y * sn) + inst.X, (long)Math.Round(p.X * sn + p.Y * c) + inst.Y))
            .ToArray();

        Assert.Equal(4, poly.Xy.Length / 2);
        for (int i = 0; i < 4; i++)
        {
            Assert.True(Math.Abs(poly.Xy[2 * i] - expected[i].Item1) <= 1, $"corner {i} x: {poly.Xy[2 * i]} vs {expected[i].Item1}");
            Assert.True(Math.Abs(poly.Xy[2 * i + 1] - expected[i].Item2) <= 1, $"corner {i} y: {poly.Xy[2 * i + 1]} vs {expected[i].Item2}");
        }
    }

    /// <summary>Gate 2's companion at the flatten level: nothing about a CARDINAL placement moved.
    /// The same fixture at 90 degrees still emits a RectShape and a RoundedRectShape rather than
    /// promoted polygons, and still reports nothing, because it still loses nothing. (Its rendering is
    /// covered by the 90-degree row of the theory above.)</summary>
    [Fact]
    public void CardinalInstance_PromotesNothingAndReportsNothing()
    {
        var leafDir = CreateCell("Leaf", KitchenSink);
        var topDir = CreateCell("Top", _ => { });
        var topLayoutDir = CellFolder.SubFolderPath(topDir, ViewType.Layout);

        foreach (double deg in new[] { 0.0, 90.0, 180.0, 270.0 })
        {
            var result = LayoutFlatten.FlattenOneLevel(PlaceInto(topDir, leafDir, deg), topLayoutDir)!;
            Assert.Null(result.Notes);
            Assert.Single(result.Shapes.OfType<RectShape>());
            Assert.Single(result.Shapes.OfType<RoundedRectShape>());
        }
    }

    // ── Gate 7 end to end: what a non-cardinal flatten promotes, drops and rounds ────────────────

    [Fact]
    public void NonCardinalFlatten_PromotesRectangles_SkipsBitmaps_CarriesLabelAngles_AndSaysSoOncePerKind()
    {
        var leafDir = CreateCell("Leaf", v =>
        {
            KitchenSink(v);
            v.Shapes.Add(new BitmapShape { Layer = LayerA, ImagePathRef = "trace.png", X = 0, Y = 0, W = 1_000, H = 1_000 });
            v.Shapes.Add(new LabelShape { Layer = LayerA, X = 500, Y = 500, Height = 200, Text = "hi", Rotation = LayoutRotation.R90 });
        });
        var topDir = CreateCell("Top", _ => { });
        var topLayoutDir = CellFolder.SubFolderPath(topDir, ViewType.Layout);

        var result = LayoutFlatten.FlattenOneLevel(PlaceInto(topDir, leafDir, 30.0), topLayoutDir)!;

        Assert.Empty(result.Shapes.OfType<RectShape>());          // promoted…
        Assert.Empty(result.Shapes.OfType<RoundedRectShape>());   // …both of them
        Assert.Empty(result.Shapes.OfType<BitmapShape>());        // cannot rotate — dropped
        Assert.Single(result.Shapes.OfType<CircleShape>());       // rotation-invariant — untouched
        Assert.Single(result.Shapes.OfType<ViaShape>());          // ditto

        // 90 (the label's own) + 30 (the placement) = 120, and a label carries that EXACTLY since
        // LabelShape.RotationDegrees was widened past the cardinals (2026-08-25). It used to round to
        // R90 here and report that it had; there is now nothing to round and nothing to report.
        Assert.Equal(120.0, result.Shapes.OfType<LabelShape>().Single().RotationDegrees, 6);

        var notes = Assert.IsAssignableFrom<IReadOnlyList<string>>(result.Notes);
        Assert.Equal(2, notes.Count);                              // one line per KIND, never per shape
        Assert.Contains(notes, n => n.Contains("2 rectangle"));
        Assert.Contains(notes, n => n.Contains("1 reference image"));
        Assert.DoesNotContain(notes, n => n.Contains("label"));
    }

    /// <summary>The L3c rule this inherits: the preview and the emit share one predicate, or a menu
    /// that promises N shapes produces N-1. A bitmap does not survive a non-cardinal flatten, so the
    /// COUNT must not include it either.</summary>
    [Fact]
    public void FlattenPreviewCount_MatchesTheEmit_ForBothCardinalAndNonCardinal()
    {
        var leafDir = CreateCell("Leaf", v =>
        {
            KitchenSink(v);
            v.Shapes.Add(new BitmapShape { Layer = LayerA, ImagePathRef = "trace.png", X = 0, Y = 0, W = 100, H = 100 });
        });
        var topDir = CreateCell("Top", _ => { });
        var topLayoutDir = CellFolder.SubFolderPath(topDir, ViewType.Layout);

        foreach (double deg in new[] { 0.0, 90.0, 30.0, 212.25 })
        {
            var inst = PlaceInto(topDir, leafDir, deg);
            long? preview = LayoutFlatten.CountOneLevelShapes(inst, topLayoutDir);
            var emitted = LayoutFlatten.FlattenOneLevel(inst, topLayoutDir)!.Shapes.Count;
            Assert.Equal(emitted, (int)preview!.Value);
        }
    }

    // ── Gate 3: persistence is additive ─────────────────────────────────────────────────────────

    [Fact]
    public void ACardinalPlacement_SerializesExactlyAsBefore_WithNoAngleKey()
    {
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        view.Instances.Add(new LayoutInstance { CellRef = "../../Leaf", X = 10, Y = 20, RotationDegrees = 90.0, Mag = 1.0 });

        string json = LayoutPersistence.Serialize(view);
        Assert.Contains("\"Rot\": \"R90\"", json);
        Assert.DoesNotContain("RotDeg", json);
    }

    [Fact]
    public void ANonCardinalPlacement_AddsTheAngleKey_AndRoundTrips()
    {
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        view.Instances.Add(new LayoutInstance { CellRef = "../../Leaf", X = 10, Y = 20, RotationDegrees = 137.5, Mag = 1.0 });

        string json = LayoutPersistence.Serialize(view);
        Assert.Contains("RotDeg", json);

        var path = Path.Combine(_root, "roundtrip.clay");
        LayoutPersistence.SaveToFile(path, view);
        var loaded = LayoutPersistence.LoadFromFile(path);
        Assert.Equal(137.5, loaded.Instances[0].RotationDegrees, 9);
        Assert.Equal(json, LayoutPersistence.Serialize(loaded));
    }

    /// <summary>A <c>.clay</c> written before L3d has no <c>RotDeg</c> key at all — it must load as
    /// the cardinal angle its <c>Rot</c> names and re-save byte-identically.</summary>
    [Fact]
    public void APreL3dFile_LoadsAsItsCardinalAngle_AndReSavesByteIdentically()
    {
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        view.Instances.Add(new LayoutInstance { CellRef = "../../Leaf", X = 10, Y = 20, Rot = LayoutRotation.R270, Mag = 1.0 });
        string legacy = LayoutPersistence.Serialize(view);
        Assert.DoesNotContain("RotDeg", legacy);

        var path = Path.Combine(_root, "legacy.clay");
        File.WriteAllText(path, legacy);
        var loaded = LayoutPersistence.LoadFromFile(path);

        Assert.Equal(270.0, loaded.Instances[0].RotationDegrees, 9);
        Assert.Equal(legacy, LayoutPersistence.Serialize(loaded));
    }

    // ── Gate 11: a counter, never a clock ───────────────────────────────────────────────────────

    /// <summary>An arbitrary angle must not defeat the instance geometry cache: the SAME layout at a
    /// non-cardinal angle must build the same number of paths as at a cardinal one — it is still one
    /// matrix per placement over one cached cell-local path. Deliberately a COUNTER and not a timing
    /// assertion, which would measure the machine.</summary>
    [Fact]
    public void ManyInstances_AtANonCardinalAngle_BuildTheSameNumberOfPathsAsAtACardinalOne()
    {
        var tech = MakeTech();

        // A SEPARATE leaf cell per angle, deliberately: the compiled instance-geometry cache is keyed
        // by cell and survives between renders, so reusing one cell would compare a cold build against
        // a warm one and "prove" that the second angle built no paths at all.
        int PathsAt(double degrees, string tag)
        {
            var leafDir = CreateCell($"Leaf{tag}", KitchenSink);
            var topDir = CreateCell($"Top{tag}", _ => { });
            var topLayoutDir = CellFolder.SubFolderPath(topDir, ViewType.Layout);

            var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
            for (int i = 0; i < 40; i++)
            {
                var inst = PlaceInto(topDir, leafDir, degrees);
                inst.X = (i % 8) * 20_000;
                inst.Y = (i / 8) * 20_000;
                view.Instances.Add(inst);
            }
            RenderWide(view, tech, topLayoutDir, out var stats);
            Assert.Equal(40, stats.InstancesDrawn);
            return stats.PathsConstructed;
        }

        int cardinal = PathsAt(90.0, "Card");
        int angled = PathsAt(30.0, "Angled");

        // O(sub-cell shapes), not O(placements) — five shapes in the leaf, forty placements of it.
        Assert.InRange(cardinal, 1, 20);
        Assert.Equal(cardinal, angled);
    }


    // ── Gate 8: the whole interchange pipeline carries the angle, not just the codec ────────────

    /// <summary>
    /// Pre-L3d this instance would have been written as ANGLE 30 (GDSII always could carry it) and
    /// read back SNAPPED to 0 with a loss note. R-L3d-8 removed the snap; this exercises the real
    /// writer and the real reader rather than the codec in isolation.
    /// </summary>
    [Theory]
    [InlineData(30.0, false)]
    [InlineData(137.5, false)]
    [InlineData(212.25, true)]
    public void GdsiiRoundTrip_CarriesANonCardinalAngle_WithNoSnapReported(double degrees, bool mirror)
    {
        var leafDir = CreateCell("Leaf", KitchenSink);
        var topDir = CreateCell("Top", _ => { });
        var topLayoutDir = CellFolder.SubFolderPath(topDir, ViewType.Layout);

        var topView = LayoutPersistence.LoadFromFile(Path.Combine(topLayoutDir, "Top.clay"));
        topView.Instances.Add(PlaceInto(topDir, leafDir, degrees, mirror));
        LayoutPersistence.SaveToFile(Path.Combine(topLayoutDir, "Top.clay"), topView);

        var tech = MakeTech();
        var plan = CircuitRF.Ui.Layout.Interchange.GdsiiExport.Analyze(topDir, tech, 1000);
        Assert.True(plan.CanWrite);
        var gdsPath = Path.Combine(_root, "export.gds");
        CircuitRF.Ui.Layout.Interchange.GdsiiExport.Write(gdsPath, plan);

        var importDir = Directory.CreateTempSubdirectory("l3d-gds-").FullName;
        try
        {
            CellLayoutResolver.InvalidateUnder(importDir);
            using var stream = File.OpenRead(gdsPath);
            var import = CircuitRF.Ui.Layout.Interchange.GdsiiImport.Import(
                stream, importDir, tech, destDbuPerMicron: 1000, preferSourceResolution: false);
            Assert.False(import.Cancelled);
            Assert.DoesNotContain(import.Messages, m => m.Contains("snapped", StringComparison.OrdinalIgnoreCase));

            var importedTop = Path.Combine(importDir, import.CellNameByStructureName["Top"]);
            var importedLayoutDir = CellFolder.SubFolderPath(importedTop, ViewType.Layout);
            var reloaded = LayoutPersistence.LoadFromFile(
                Path.Combine(importedLayoutDir, $"{import.CellNameByStructureName["Top"]}.clay"));

            var inst = Assert.Single(reloaded.Instances);
            Assert.Equal(degrees, inst.RotationDegrees, 6);
            Assert.Equal(mirror, inst.MirrorX);
        }
        finally
        {
            CellLayoutResolver.InvalidateUnder(importDir);
            Directory.Delete(importDir, recursive: true);
        }
    }

    // ── Gate 10: exactly one accessor reads the two serialized fields ───────────────────────────

    /// <summary>R-L3d-5. Two fields with one meaning drift — three copies of the version number once
    /// did, which is why <c>VersionSingleSourceTests</c> exists. Comments are stripped first: an
    /// unstripped scan reports this codebase's own documentation as a violation (the H8 precedent).
    /// </summary>
    [Fact]
    public void NothingOutsideTheModelReadsRotOrRotDegDirectly()
    {
        var srcRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (srcRoot is not null && !File.Exists(Path.Combine(srcRoot.FullName, "circuitrf.slnx")))
            srcRoot = srcRoot.Parent;
        Assert.NotNull(srcRoot);

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(srcRoot!.FullName, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            if (Path.GetFileName(file) == "LayoutModel.cs") continue;   // the accessor's own home

            string code = Regex.Replace(File.ReadAllText(file), @"/\*.*?\*/", "", RegexOptions.Singleline);
            code = Regex.Replace(code, @"//[^\n]*", "");
            if (Regex.IsMatch(code, @"\.Rot\b") || Regex.IsMatch(code, @"\.RotDeg\b"))
                offenders.Add(Path.GetRelativePath(srcRoot.FullName, file));
        }

        Assert.Empty(offenders);
    }
}

// brief-em3d-106 — the realistic view. Gate 1 the plan (what is drawn, what steps aside, each Look key bringing back exactly its row);
// 2 not document state (the toggle, the Look block's round trip, undo and run text); 3 the reference math (Pbr.cs, no GPU); 4 the
// prefilter's determinism; 5 the WGSL's constants against the C# reference; 6 Metal offscreen (macOS only); 7 the counters; 8 the
// menus and the toolbar. D3D11 and Vulkan are generated-shader currency only (Viewer3DFrameGateTests.Gate1b); their runtime is not
// run on this machine.

using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Appearance;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Render.Scene3D.Look;
using CircuitRF.Ui.Tests.ThreeD;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.Viewer3D;

[Collection(Viewer3DCollection.Name)]
public sealed class RealisticViewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-real-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public RealisticViewTests()
    {
        Snap3DPreference.TestOverrideActive = true;
        Lod3DPreference.TestOverrideActive = true;
    }

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 1. the plan ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>CaseA (solids, sheets, ports, the air box's faces and edges) with a face tint and an image sheet added, every object
    /// shown, the overlays and the drawing grid on: every chrome row has something in it.</summary>
    private (Scene3DModel Scene, Viewer3DViewState View, Scene3DOverlay Mesh) Fixture()
    {
        var g = Scene3DGateTests.CaseA(_root);
        var sheet = g.Problem!.Sheets[0].Name;
        var (lo, hi) = (g.Problem.Boundary.Min, g.Problem.Boundary.Max);
        var tint = new Scene3DFaceTint("tint", Em3dFaceBoundaryKind.Pec,
        [
            new Em3dFacePolygon([new(lo.X, lo.Y, hi.Z), new(hi.X, lo.Y, hi.Z), new(hi.X, hi.Y, hi.Z), new(lo.X, hi.Y, hi.Z)], [], new(0, 0, 1)),
        ]);
        // a wireframe object (no material) draws its feature edges always: the Edges row's
        string wire = (g.Problem.Solids.FirstOrDefault(x => x.Primitive is Em3dBox) ?? g.Problem.Solids[0]).Name;
        var options = new Scene3DBuildOptions(FaceTints: [tint], Wireframe: n => n == wire,
            Images: n => n == sheet ? new C3dPlacedImage(Path.Combine(_root, "missing.png"), new(0, 0, 0), new(1e-3, 0, 0), new(0, 1e-3, 0)) : null);
        var scene = Scene3DBuilder.Build(g.Problem, 1, g.Origins, options: options);
        var view = new Viewer3DViewState { Camera = Camera3D.Fit(scene.ContentMin, scene.ContentMax, 1.6f) };
        view.Adopt(scene, null);
        Array.Fill(view.Visible, true);
        view.ShowMesh = view.ShowMeshSection = view.ShowGrid = true;
        view.Clip.Enabled = true;
        view.DrawingGrid = new DrawingGridSettings();
        var mesh = new Scene3DOverlay([new(0, 0, 0, 0, 0xFFFFFFFF), new(1e-4f, 0, 0, 0, 0xFFFFFFFF)], 1);
        Assert.Contains(scene.Objects, o => o.Kind == Scene3DKind.Port);
        Assert.Contains(scene.Objects, o => o.Tint && o.Kind == Scene3DKind.Boundary && o.Boundary is null && o.Name != Scene3DBuilder.AirBoxEdgesName);
        Assert.Contains(scene.Objects, o => o.Underlay);
        return (scene, view, mesh);
    }

    private static List<Scene3DDraw> Draws(Scene3DFramePlan p) => [.. p.Draws.Take(p.DrawCount)];

    private static Scene3DFramePlan Plan(Scene3DModel scene, Viewer3DViewState view, Scene3DOverlay mesh, bool export = false)
    {
        var plan = new Scene3DFramePlan();
        plan.Plan(scene, view, 800, 500, false, false, mesh, mesh, mesh, export: export);
        return plan;
    }

    private static void Realistic(Viewer3DViewState view, C3dLook? look = null)
    {
        view.Realistic = true;
        view.Environment = EnvironmentPrefilter.Studio(C3dStudio.Studio);
        view.Look = RealisticLook.From(look);
    }

    private static string Key(in Scene3DDraw d) => $"{d.Pipeline}/{d.Buffer}/{d.First}/{d.Count}/{d.Transform}/{d.Tie}/{d.Texture}/{d.Object}";

    [Fact]
    public void Gate1_EveryMaterialIsLit_NoChromeIsDrawn_AndEachShowKeyBringsBackExactlyItsRow()
    {
        var (scene, view, mesh) = Fixture();
        var plain = Draws(Plan(scene, view, mesh));
        // the fixture has every row but Edges: its only scene lines outside a chrome row were a wireframe's, which are its body
        foreach (var (row, _) in RealisticLook.Chrome.Where(c => c.Row != Scene3DChrome.Edges))
            Assert.True(plain.Any(d => Scene3DFramePlan.ChromeOf(scene, d) == row),
                        $"{row} missing; drawn: {string.Join(", ", plain.Select(d => $"{d.Pipeline}:{d.Buffer}:{d.Object}:{Scene3DFramePlan.ChromeOf(scene, d)}"))}");

        Realistic(view);
        var bare = Draws(Plan(scene, view, mesh));
        Assert.DoesNotContain(bare, d => Scene3DFramePlan.ChromeOf(scene, d) is not null);
        // A wireframe object (no material) IS its edges: they are drawn with the Edges row off, or it is invisible but for hover.
        foreach (var o in scene.Objects.Where(o => o.Wireframe))
            Assert.Contains(bare, d => d.Object == o.Id && d.Pipeline == Scene3DPipeline.Lines && d.Buffer == Scene3DBuffer.SceneLines);
        foreach (var o in scene.Objects.Where(o => Scene3DFramePlan.ChromeOfObject(scene, o) is null && o.AppearanceSlot >= 0 && !o.Wireframe && o.Element < 0))
            Assert.True(bare.Any(d => d.Object == o.Id && d.Pipeline is Scene3DPipeline.Pbr or Scene3DPipeline.PbrTranslucent),
                        $"{o.Name} ({o.Kind}): {string.Join(", ", bare.Where(d => d.Object == o.Id).Select(d => d.Pipeline))}; batches " +
                        scene.Batches.Count(b => b.ObjectId == o.Id));
        Assert.DoesNotContain(bare, d => d.Pipeline is Scene3DPipeline.Opaque or Scene3DPipeline.Translucent
                                         && Scene3DFramePlan.ChromeOfObject(scene, scene.Objects[d.Object - 1]) is null
                                         && scene.Objects[d.Object - 1].AppearanceSlot >= 0 && !scene.Objects[d.Object - 1].Wireframe);

        // Each row of the table: its key brings back exactly the default view's draws of that row, and nothing else.
        foreach (var (row, key) in RealisticLook.Chrome)
        {
            var look = new C3dLook();
            typeof(C3dLook).GetProperty(key)!.SetValue(look, true);
            Realistic(view, look);
            var shown = Draws(Plan(scene, view, mesh)).Select(d => Key(d)).Order(StringComparer.Ordinal).ToList();
            var want = bare.Select(d => Key(d)).Concat(plain.Where(d => Scene3DFramePlan.ChromeOf(scene, d) == row).Select(d => Key(d)))
                           .Order(StringComparer.Ordinal).ToList();
            Assert.Equal(want, shown);
        }
    }

    [Fact]
    public void Gate1_ShowAirBox_NeverOverridesAHiddenAirBox()
    {
        var (scene, view, mesh) = Fixture();
        foreach (var o in scene.Objects.Where(o => Scene3DFramePlan.ChromeOfObject(scene, o) == Scene3DChrome.AirBox)) view.Visible[o.Id - 1] = false;
        Realistic(view, new C3dLook { ShowAirBox = true });
        Assert.DoesNotContain(Draws(Plan(scene, view, mesh)), d => Scene3DFramePlan.ChromeOf(scene, d) == Scene3DChrome.AirBox);
    }

    [Fact]
    public void Gate1_TheSelectedOutlineStays_AndAPictureHasNoHoverOrSelection()
    {
        var (scene, view, mesh) = Fixture();
        var metal = scene.Objects.First(o => Scene3DFramePlan.ChromeOfObject(scene, o) is null && o.AppearanceSlot >= 0 && !o.Wireframe
                                             && scene.EdgeBatches.Any(e => e.ObjectId == o.Id));
        view.Selection = [Scene3DItem.OfObject(metal.Id)];
        view.Hovered = metal.Id;
        Realistic(view);
        var live = Plan(scene, view, mesh);
        Assert.True(Draws(live).Any(d => d.Pipeline == Scene3DPipeline.Edges && d.Object == metal.Id),
                    $"{metal.Name}: {string.Join(", ", Draws(live).Select(d => $"{d.Pipeline}:{d.Object}"))}");

        var picture = Plan(scene, view, mesh, export: true);
        Assert.DoesNotContain(Draws(picture), d => d.Pipeline is Scene3DPipeline.Edges or Scene3DPipeline.OnTop);
        var bits = System.Runtime.InteropServices.MemoryMarshal.Cast<float, uint>(picture.Uniforms.AsSpan());
        Assert.Equal(0u, bits[24]);           // hover
        Assert.Equal(0u, bits[28]);           // the selection's count
        // and the selected object is drawn lit, not faded among the translucent ones
        Assert.Contains(Draws(picture), d => d.Object == metal.Id && d.Pipeline == Scene3DPipeline.Pbr);
    }

    [Fact]
    public void Gate1_AStatedTransparencyIsTranslucent_AKindDefaultIsNot()
    {
        var a = Em3dBoundaryKind.Absorbing;
        var problem = new Em3dProblem(
            [new Em3dSolid("glassy", "FR4", Em3dRole.Dielectric, new Em3dBox(new(0, 0, 0), new(1e-3, 1e-3, 1e-4)), 0),
             new Em3dSolid("faded", "Copper", Em3dRole.Conductor, new Em3dBox(new(0, 0, 2e-4), new(1e-3, 1e-3, 3e-4)), 0)],
            [], [], [], new Em3dAirBox(new(-5e-3, -5e-3, -5e-3), new(5e-3, 5e-3, 5e-3), new Em3dFaces(a, a, a, a, a, a)),
            new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), 25);
        var scene = Scene3DBuilder.Build(problem, 1, options: new Scene3DBuildOptions(DrawAirBox: false, Origin: (0, 0, 0), HideOutermostDielectric: false,
            Transparency: n => n == "faded" ? new Scene3DTransparency(40, 1) : null));
        var view = new Viewer3DViewState { Camera = Camera3D.Fit(scene.ContentMin, scene.ContentMax, 1.6f) };
        view.Adopt(scene, null);
        Array.Fill(view.Visible, true);
        Realistic(view);
        var draws = Draws(Plan(scene, view, Scene3DOverlay.None));
        uint Id(string n) => scene.Objects.Single(o => o.Name == n).Id;
        Assert.True(scene.Objects.Single(o => o.Name == "glassy").Translucent);       // translucent in the default view…
        Assert.Contains(draws, d => d.Object == Id("glassy") && d.Pipeline == Scene3DPipeline.Pbr);           // …opaque here (Transmission 0)
        Assert.Contains(draws, d => d.Object == Id("faded") && d.Pipeline == Scene3DPipeline.PbrTranslucent);  // a stated 40 % is kept
        var faded = scene.Objects.Single(o => o.Name == "faded");
        Assert.All(scene.ShadeVertices.Skip(faded.FirstVertex).Take(faded.VertexCount), v => Assert.NotEqual(0u, v.Slot & Scene3DShadeVertex.StatedAlpha));
    }

    /// <summary>A see-through dielectric resting on an opaque slab (a cover on a board) shares the slab's top as its bottom. Both took
    /// the dielectric's tie, so the two triangulations fought per pixel — fans across the board that flickered as the camera moved. The
    /// translucent one gives way: its polygon offset is further from the eye than the slab's in the constant AND the slope term (the
    /// constant alone is below the rasteriser's interpolation error across a board-sized triangle seen obliquely).</summary>
    [Fact]
    public void Gate1_ATranslucentBodyOnAnOpaqueSlab_GivesWayWhereTheirFacesCoincide()
    {
        var a = Em3dBoundaryKind.Absorbing;
        var problem = new Em3dProblem(
            [new Em3dSolid("board", "FR4", Em3dRole.Dielectric, new Em3dBox(new(0, 0, 0), new(1e-3, 1e-3, 1e-4)), 0),
             new Em3dSolid("cover", "FR4", Em3dRole.Dielectric, new Em3dBox(new(0, 0, 1e-4), new(1e-3, 1e-3, 5e-4)), 0)],
            [], [], [], new Em3dAirBox(new(-5e-3, -5e-3, -5e-3), new(5e-3, 5e-3, 5e-3), new Em3dFaces(a, a, a, a, a, a)),
            new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), 25);
        var scene = Scene3DBuilder.Build(problem, 1, options: new Scene3DBuildOptions(DrawAirBox: false, Origin: (0, 0, 0), HideOutermostDielectric: false,
            Transparency: n => n == "cover" ? new Scene3DTransparency(85, 1) : null));
        var view = new Viewer3DViewState { Camera = Camera3D.Fit(scene.ContentMin, scene.ContentMax, 1.6f) };
        view.Adopt(scene, null);
        Array.Fill(view.Visible, true);
        Realistic(view);
        var draws = Draws(Plan(scene, view, Scene3DOverlay.None));
        uint Id(string n) => scene.Objects.Single(o => o.Name == n).Id;
        Assert.Contains(draws, d => d.Object == Id("board") && d.Pipeline == Scene3DPipeline.Pbr && d.Tie == Scene3DDepthTie.Behind);
        Assert.All(draws.Where(d => d.Object == Id("cover")), d => Assert.Equal(Scene3DDepthTie.Translucent, d.Tie));
        var (slabC, slabS, _) = Scene3DFramePlan.DepthBias(Scene3DDepthTie.Behind);
        var (coverC, coverS, _) = Scene3DFramePlan.DepthBias(Scene3DDepthTie.Translucent);
        Assert.True(coverC > slabC && coverS > slabS, $"cover ({coverC}, {coverS}) vs slab ({slabC}, {slabS})");
    }

    /// <summary>An array element is drawn whole from its prototype's DEFAULT-opaque triangles. A dielectric is translucent by its
    /// kind's default and so is not in that draw — yet with no Transmission the realistic view draws it opaque, so each element's copy
    /// must still be drawn by itself (it once vanished from every element but the prototype).</summary>
    [Fact]
    public void Gate1_AnArrayElementsKindDefaultTranslucentPart_IsStillDrawn()
    {
        var a = Em3dBoundaryKind.Absorbing;
        var solids = new List<Em3dSolid>();
        for (int k = 0; k < 3; k++)
        {
            double x = k * 2e-3;
            solids.Add(new Em3dSolid($"U[{k}]/sub", "FR4", Em3dRole.Dielectric, new Em3dBox(new(x, 0, 0), new(x + 1e-3, 1e-3, 1e-4)), 0));
            solids.Add(new Em3dSolid($"U[{k}]/cu", "Copper", Em3dRole.Conductor, new Em3dBox(new(x, 0, 1e-4), new(x + 1e-3, 1e-3, 1.2e-4)), 0));
        }
        var problem = new Em3dProblem(solids, [], [], [],
            new Em3dAirBox(new(-5e-3, -5e-3, -5e-3), new(1e-2, 5e-3, 5e-3), new Em3dFaces(a, a, a, a, a, a)),
            new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), 25);
        var run = new object();
        var scene = Scene3DBuilder.Build(problem, 1, options: new Scene3DBuildOptions(DrawAirBox: false, Origin: (0, 0, 0), HideOutermostDielectric: false,
            Instancing: n => new Scene3DInstancing(run, n[2] - '0', (n[2] - '0') * 2e-3, 0, 0, n[(n.IndexOf('/') + 1)..])));
        Assert.Equal(2, scene.Elements.Length);
        var view = new Viewer3DViewState { Camera = Camera3D.Fit(scene.ContentMin, scene.ContentMax, 1.6f) };
        view.Adopt(scene, null);
        Array.Fill(view.Visible, true);
        Realistic(view);
        var draws = Draws(Plan(scene, view, Scene3DOverlay.None));
        Assert.Equal(2, draws.Count(d => d.Pipeline == Scene3DPipeline.Pbr && d.Object == 0));                 // each element whole
        foreach (var o in scene.Objects.Where(o => o.Name.EndsWith("/sub", StringComparison.Ordinal)))
        {
            Assert.True(o.Translucent);
            Assert.Contains(draws, d => d.Object == o.Id && d.Pipeline == Scene3DPipeline.Pbr);
        }
    }

    // ── 2. not document state ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_TheLookRoundTrips_IsUndoable_AndNoRunSeesIt()
    {
        var doc = new C3dDocument
        {
            Objects = [new C3dBox { Name = "b", Material = "Copper", Min = new(0, 0, 0), Size = new(1000, 1000, 1000) }],
            Look = new C3dLook { Environment = "HighKey", Rotation = 45, Exposure = 0.5, Background = "#ffffff,#d0d4da", ShowPorts = true },
        };
        string text = C3dPersistence.Serialize(doc);
        Assert.Contains("\"Look\"", text);
        Assert.Equal(text, C3dPersistence.Serialize(C3dPersistence.Deserialize(text)));
        Assert.DoesNotContain("\"Look\"", C3dPersistence.SerializeForRun(doc));
        Assert.Equal(text, C3dPersistence.Serialize(doc));                       // and SerializeForRun put it back

        string before = C3dRecordsEdit.Of(doc);
        doc.Look!.Exposure = -1;
        string after = C3dRecordsEdit.Of(doc);
        Assert.NotEqual(before, after);
        C3dRecordsEdit.Apply(doc, before);
        Assert.Equal(0.5, doc.Look!.Exposure);
        C3dRecordsEdit.Apply(doc, after);
        Assert.Equal(-1, doc.Look!.Exposure);
    }

    [Fact]
    public void Gate2_TheToggleMarksNothingDirty_AndIsNeverSaved()
    {
        var vm = Open(new C3dDocument { Objects = [new C3dBox { Name = "b", Material = "Copper", Min = new(0, 0, 0), Size = new(1000, 1000, 1000) }] });
        string saved = C3dPersistence.Serialize(vm.Document);
        vm.Viewer.EnvironmentFor = (s, _) => EnvironmentPrefilter.Studio(s);
        vm.Viewer.IsRealistic = true;
        Pump(() => vm.Viewer.View.Environment is not null);
        Assert.False(vm.IsDirty);
        Assert.Equal(saved, C3dPersistence.Serialize(vm.Document));
        Assert.StartsWith("Realistic · Studio · 0 EV", vm.Viewer.RealisticText);
        vm.Viewer.IsRealistic = false;
        Assert.False(vm.IsDirty);
        Assert.Equal("", vm.Viewer.RealisticText);
    }

    [Fact]
    public void Gate2_CheckReadsTheLook_AndAnUnreadableHdrIsAWarningThatFallsBackToStudio()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "Cell.c3d");
        IReadOnlyList<CircuitRF.Diagnostics.Diagnostic> Check(C3dLook look)
            => C3dValidation.Validate(new C3dDocument { Look = look }, documentPath: path);
        Assert.Contains(Check(new C3dLook { Exposure = 11 }), d => d.Id == "c3d.look.range" && d.Severity == CircuitRF.Diagnostics.DiagnosticSeverity.Error);
        Assert.Contains(Check(new C3dLook { Background = "blue" }), d => d.Id == "c3d.look.background");
        Assert.Contains(Check(new C3dLook { Environment = "sky.exr" }), d => d.Id == "c3d.look.environment");
        var missing = Assert.Single(Check(new C3dLook { Environment = "sky.hdr" }));
        Assert.Equal(("c3d.look.hdr-unreadable", CircuitRF.Diagnostics.DiagnosticSeverity.Warning), (missing.Id, missing.Severity));

        var image = new RadianceImage(16, 8, [.. Enumerable.Range(0, 16 * 8 * 3).Select(i => 0.5f + (i % 7) * 0.1f)]);
        image.Rgb[3 * 20] = image.Rgb[3 * 20 + 1] = image.Rgb[3 * 20 + 2] = 1e6f;          // a sun brighter than a half can hold
        File.WriteAllBytes(Path.Combine(_root, "sky.hdr"), RadianceHdr.Encode(image));
        Assert.Empty(Check(new C3dLook { Environment = "sky.hdr", Exposure = -2, Background = "Environment" }));
        var read = RadianceHdr.Read(Path.Combine(_root, "sky.hdr"), out _)!;
        Assert.Equal((16, 8), (read.Width, read.Height));
        Assert.True(MathF.Abs(read.Rgb[3] - image.Rgb[3]) < 0.01f);

        var fell = EnvironmentPrefilter.For(C3dStudio.Studio, Path.Combine(_root, "nowhere.hdr"));
        Assert.Equal("Studio", fell.Label);
        Assert.Contains("nowhere.hdr cannot be read", fell.Fallback);
        // keyed on the file, as a read one is, so the view model keeps it across an exposure edit rather than re-making it
        Assert.StartsWith("hdr:" + Path.GetFullPath(Path.Combine(_root, "nowhere.hdr")) + "|", fell.Key);
        var user = EnvironmentPrefilter.For(C3dStudio.Studio, Path.Combine(_root, "sky.hdr"));
        Assert.Equal("sky.hdr", user.Label);
        Assert.Null(user.Fallback);
        Assert.All(user.Levels, l => Assert.All(l.Rgba, h => Assert.True(Half.IsFinite(h))));   // clamped, never +inf
    }

    // ── 3. the reference math ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_TheReferenceMath_Brdf_WhiteFurnace_ToneCurve_Srgb()
    {
        var lut = Pbr.IntegrateBrdf(1, 0);
        Assert.True(MathF.Abs(lut.X - 1) < 1e-3f && MathF.Abs(lut.Y) < 1e-3f, $"{lut}");

        var white = EnvironmentPrefilter.Uniform(Vector3.One);
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var n in Directions(400))
        {
            float e = white.Irradiance(n).X;
            lo = MathF.Min(lo, e); hi = MathF.Max(hi, e);
        }
        Assert.True(hi - lo <= 1e-4f, $"white-furnace irradiance spans {lo} … {hi}");
        Assert.True(MathF.Abs(lo - MathF.PI) < 1e-3f);

        var rough = new PbrMaterial(Vector3.One, 0, 1, 0, 1.5f, 0, 0, Vector3.One);
        var light = new PbrLighting(1, 1, 0, Vector3.UnitZ, Vector3.Zero);
        float most = 0;
        foreach (var v in Directions(200).Where(v => v.Z > 0.02f))
            most = MathF.Max(most, Pbr.Radiance(rough, Vector3.UnitZ, v, white, light, out _).X);
        Assert.True(most <= 1 + 2e-3f, $"a rough white dielectric reflects {most} of a white furnace");

        Assert.Equal(Vector3.Zero, ToneCurve.Neutral(Vector3.Zero));
        float last = -1;
        for (float t = 0; t <= 64; t += 1e-3f)
        {
            float y = ToneCurve.Neutral(new Vector3(t)).X;
            Assert.True(y >= last - 1e-6f, $"not monotonic at {t}");          // to float rounding
            last = y;
        }
        Assert.Equal(0.0031308f * 12.92f, ToneCurve.SrgbEncode(0.0031308f), 6);
        Assert.Equal(1.055f * MathF.Pow(0.0031309f, 1 / 2.4f) - 0.055f, ToneCurve.SrgbEncode(0.0031309f), 6);
        Assert.Equal(1f, ToneCurve.SrgbEncode(1f), 6);
        Assert.Equal(0.5f, ToneCurve.SrgbDecode(ToneCurve.SrgbEncode(0.5f)), 5);
        Assert.Equal(0.04045f / 12.92f, ToneCurve.SrgbDecode(0.04045f), 6);
    }

    /// <summary>A Fibonacci sphere: <paramref name="n"/> unit directions spread over all of it.</summary>
    private static IEnumerable<Vector3> Directions(int n)
    {
        float golden = MathF.PI * (3 - MathF.Sqrt(5));
        for (int i = 0; i < n; i++)
        {
            float z = 1 - 2 * (i + 0.5f) / n, r = MathF.Sqrt(1 - z * z);
            yield return new(r * MathF.Cos(golden * i), r * MathF.Sin(golden * i), z);
        }
    }

    // ── 4. determinism ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_TheStudioPrefilter_IsTheSameBytesTwice_AndOnAnotherThread()
    {
        static byte[] Bytes(PrefilteredEnvironment e)
        {
            var all = new List<byte>();
            foreach (var l in e.Levels) all.AddRange(l.Bytes.ToArray());
            all.AddRange(System.Runtime.InteropServices.MemoryMarshal.AsBytes(e.BrdfTable.AsSpan()).ToArray());
            foreach (var c in e.Sh) { all.AddRange(BitConverter.GetBytes(c.X)); all.AddRange(BitConverter.GetBytes(c.Y)); all.AddRange(BitConverter.GetBytes(c.Z)); }
            return [.. all];
        }
        var a = Bytes(EnvironmentPrefilter.FromStudio(StudioEnvironment.Studio));
        var b = Bytes(EnvironmentPrefilter.FromStudio(StudioEnvironment.Studio));
        byte[]? c = null;
        var t = new Thread(() => c = Bytes(EnvironmentPrefilter.FromStudio(StudioEnvironment.Studio)));
        t.Start();
        t.Join();
        Assert.Equal(a, b);
        Assert.Equal(a, c);
        Assert.Equal(Pbr.EnvironmentLevels, EnvironmentPrefilter.Studio(C3dStudio.Studio).Levels.Length);
        Assert.Equal([256, 128, 64, 32, 16], EnvironmentPrefilter.Studio(C3dStudio.Studio).Levels.Select(l => l.Width));
    }

    // ── 5. the constants agree ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate5_EveryConstantInTheShader_IsTheReferencesValue()
    {
        string wgsl = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Viewer3D", "Shaders", "scene.wgsl"));
        var consts = Regex.Matches(wgsl, @"^const (\w+): (f32|u32) = (0x[0-9A-Fa-f]+|[0-9.eE+\-]+)u?;", RegexOptions.Multiline)
                          .ToDictionary(m => m.Groups[1].Value, m => m.Groups[3].Value);
        double F(string name) => double.Parse(consts[name], CultureInfo.InvariantCulture);
        uint U(string name) => consts[name].StartsWith("0x") ? Convert.ToUInt32(consts[name][2..], 16) : uint.Parse(consts[name], CultureInfo.InvariantCulture);
        (string Name, double Value)[] floats =
        [
            ("PI", Math.PI), ("DIELECTRIC_F0", Pbr.DielectricF0), ("MIN_ROUGHNESS", Pbr.MinRoughness), ("MIN_NDOTV", Pbr.MinNdotV),
            ("TONE_START", ToneCurve.StartCompression), ("TONE_DESAT", ToneCurve.Desaturation), ("TONE_TOE_BREAK", ToneCurve.ToeBreak),
            ("TONE_TOE_SLOPE", ToneCurve.ToeSlope), ("TONE_TOE_OFFSET", ToneCurve.ToeOffset), ("SRGB_BREAK", ToneCurve.SrgbBreak),
            ("SRGB_SLOPE", ToneCurve.SrgbLinearSlope), ("SRGB_SCALE", ToneCurve.SrgbScale), ("SRGB_OFFSET", ToneCurve.SrgbOffset),
            ("SRGB_GAMMA", ToneCurve.SrgbGamma),
        ];
        foreach (var (name, value) in floats)
            Assert.True(Math.Abs((float)F(name) - (float)value) <= 1e-6 * Math.Max(1, Math.Abs(value)), $"{name}: WGSL {consts[name]}, C# {value}");
        Assert.Equal(Scene3DShadeVertex.SlotMask, U("SLOT_MASK"));
        Assert.Equal(Scene3DShadeVertex.StatedAlpha, U("STATED_ALPHA"));
        Assert.Equal(Scene3DShadeVertex.Closed, U("CLOSED"));
        Assert.Contains($"array<AP, {Pbr.TableRows}>", wgsl);
        Assert.Contains($"sh: array<vec4f, 9>", wgsl);
        // nothing new in the shader that a scan does not compare: brief 107's eight are ShadowsOcclusionExportTests'
        string[] brief107 = ["SHADOW_MIN_COS", "SHADOW_TAPS", "AO_DIRECTIONS", "AO_STEPS", "AO_BIAS", "AO_MAX_PIXELS", "AO_EMPTY", "GROUND_FADE"];
        Assert.All(brief107, n => Assert.True(consts.ContainsKey(n), n));
        // and brief 109's two are FieldPlotsRealisticTests'
        string[] brief109 = ["FIELD_SHEEN_ROUGHNESS", "SRGB_DECODE_BREAK"];
        Assert.All(brief109, n => Assert.True(consts.ContainsKey(n), n));
        Assert.Equal(floats.Length + 3 + brief107.Length + brief109.Length, consts.Count);
    }

    // ── 6. Metal offscreen ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A sphere of one appearance, drawn realistic by the real Metal backend, read back; and the CPU reference for it.</summary>
    private static (byte[] Rgba, int W, int H, Viewer3DViewState View, PrefilteredEnvironment Env) DrawSphere(string baseColour, double metallic, double roughness,
                                                                                                            C3dLook? lookOf = null)
    {
        var a = Em3dBoundaryKind.Absorbing;
        var problem = new Em3dProblem([new Em3dSolid("ball", "Copper", Em3dRole.Conductor, new Em3dSphere(new(0, 0, 0), 5e-4), 0)], [], [], [],
            new Em3dAirBox(new(-5e-3, -5e-3, -5e-3), new(5e-3, 5e-3, 5e-3), new Em3dFaces(a, a, a, a, a, a)),
            new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), 25);
        var look = new TechAppearance { BaseColor = baseColour, Metallic = metallic, Roughness = roughness };
        var scene = Scene3DBuilder.Build(problem, 1, options: new Scene3DBuildOptions(DrawAirBox: false, Origin: (0, 0, 0),
            Appearance: _ => new AppearanceOverride(look, [])));
        using var metal = new CircuitRF.Ui.Viewer3D.Metal.MetalViewer3DBackend();
        const int w = 320, h = 200;
        metal.CreateOffscreenImages(w, h, 1);
        var view = new Viewer3DViewState { Camera = Camera3D.Fit(scene.ContentMin, scene.ContentMax, w / (float)h) };
        view.Adopt(scene, null);
        Realistic(view, lookOf);
        var session = new Viewer3DSession(() => metal) { ShadeStream = true, Environment = view.Environment };
        session.EnsureBackend();
        var plan = new Scene3DFramePlan();
        plan.Plan(scene, view, w, h, metal.FlipY, pick: false, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
        Assert.True(plan.Realistic);
        session.Frame(0, plan, 1, scene, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None, false);
        return (metal.ReadImage(0), w, h, view, view.Environment!);
    }

    [Fact]
    public void Gate6_Metal_AMirrorSphereShowsWhatIsBehindTheCamera_ARoughOneHasNoPeak_TheBackgroundIsTheTheme_AlphaIs255()
    {
        if (!OperatingSystem.IsMacOS()) return;
        // Turned 90°, the studio's softboxes are well away from what lies behind the default camera, so the reflection there is the
        // smooth gradient: the comparison then tests the shading, not where an interpolated normal lands on a softbox's edge.
        var (px, w, h, view, env) = DrawSphere("#ffffff", 1, 0, new C3dLook { Rotation = 90 });
        int c = 4 * ((h / 2) * w + w / 2);
        var v = -view.Camera.Forward;
        var lighting = view.Look.Lighting(env);
        foreach (var dv in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitX, -Vector3.UnitY, -Vector3.UnitZ })
        {
            var near = ToneCurve.Display(env.Radiance(lighting.ToEnvironment(Vector3.Normalize(v + 0.02f * dv)), 0), 1);
            var here = ToneCurve.Display(env.Radiance(lighting.ToEnvironment(v), 0), 1);
            Assert.True((near - here).Length() * 255 < 2, $"the environment is not smooth behind the camera: {near * 255} vs {here * 255}");
        }
        var want = Pbr.Shade(PbrMaterial.Of(AppearanceResolver.Resolve(new AppearanceRequest(null, null, AppearanceRole.Conductor, Em3dRole.Conductor,
                                 (255, 255, 255), new AppearanceOverride(new TechAppearance { BaseColor = "#ffffff", Metallic = 1, Roughness = 0 }, []))).Values),
                             v, v, env, lighting);
        var envColour = env.Radiance(lighting.ToEnvironment(v), 0);
        for (int k = 0; k < 3; k++)
            Assert.True(Math.Abs(px[c + k] - want[k] * 255) <= 3,
                        $"channel {k}: GPU {px[c + k]},{px[c + 1]},{px[c + 2]}, reference {want * 255}; environment {envColour}, v {v}, " +
                        $"key {lighting.KeyDirectionWorld}");

        // the background is the theme's colour, and every alpha is 255 (the swap image's rule)
        var (r, g, b) = view.Background;
        Assert.True(Math.Abs(px[0] - r * 255) <= 1 && Math.Abs(px[1] - g * 255) <= 1 && Math.Abs(px[2] - b * 255) <= 1, $"{px[0]},{px[1]},{px[2]}");
        for (int i = 3; i < px.Length; i += 4) Assert.Equal(255, px[i]);

        // a rough dielectric has no specular peak: its sharpest local change is a small part of a glossy one's
        static float Peak(byte[] p, int w, int h)
        {
            float L(int x, int y) { int k = 4 * (y * w + x); return 0.2126f * p[k] + 0.7152f * p[k + 1] + 0.0722f * p[k + 2]; }
            bool On(int x, int y) { int k = 4 * (y * w + x); return Math.Abs(p[k] - p[0]) + Math.Abs(p[k + 1] - p[1]) + Math.Abs(p[k + 2] - p[2]) > 6; }
            float most = 0;
            for (int y = 2; y < h - 2; y++)
                for (int x = 2; x < w - 2; x++)
                {
                    bool inside = true;
                    for (int j = -2; j <= 2 && inside; j++) for (int i = -2; i <= 2 && inside; i++) inside = On(x + i, y + j);
                    if (inside) most = MathF.Max(most, MathF.Abs(4 * L(x, y) - L(x - 1, y) - L(x + 1, y) - L(x, y - 1) - L(x, y + 1)));
                }
            return most;
        }
        // brief-em3d-107 — with no ground: its shadow under the sphere is an edge of its own, and this compares the sphere's shading
        var rough = DrawSphere("#b0b0b0", 0, 1, new C3dLook { Ground = false });
        var glossy = DrawSphere("#b0b0b0", 0, 0.05, new C3dLook { Ground = false });
        float pr = Peak(rough.Rgba, rough.W, rough.H), pg = Peak(glossy.Rgba, glossy.W, glossy.H);
        Assert.True(pr < 0.25f * pg && pr < 12, $"rough {pr}, glossy {pg}");
    }

    // ── 7. the counters ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate7_ToggleThriceUploadsOncePerOn_RecomputesNothing_AndARotationIsUniformsOnly()
    {
        var vm = Open(new C3dDocument { Objects = [new C3dBox { Name = "b", Material = "Copper", Min = new(0, 0, 0), Size = new(1000, 1000, 1000) }] });
        var fake = (PatchRecordingBackend)vm.Viewer.Session.Backend!;
        vm.Viewer.EnvironmentFor = (s, _) => EnvironmentPrefilter.Studio(s);
        long tess = vm.TessellationMisses, lowered = vm.ObjectsElaborated, requested = vm.Viewer.Source.Requested;
        var plan = new Scene3DFramePlan();
        void Frame()
        {
            plan.Plan(vm.Viewer.Scene, vm.Viewer.View, 400, 300, false, false, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
            vm.Viewer.Session.Frame(0, plan, 1, vm.Viewer.Scene, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None, false);
        }
        for (int k = 1; k <= 3; k++)
        {
            vm.Viewer.IsRealistic = true;
            Pump(() => vm.Viewer.View.Environment is not null);
            Frame(); Frame();
            Assert.True(plan.Realistic);
            Assert.Equal((k, k, k), (fake.ShadeUploads, fake.EnvironmentUploads, fake.AppearanceUploads));
            vm.Viewer.IsRealistic = false;
            Frame();
            Assert.False(plan.Realistic);
            Assert.Equal((k, k), (fake.ShadeReleases, fake.EnvironmentReleases));
        }
        Assert.Equal((tess, lowered, requested), (vm.TessellationMisses, vm.ObjectsElaborated, vm.Viewer.Source.Requested));

        // A rotation drag: the Look's rotation changes, the environment is the same object, and only uniforms move.
        vm.Viewer.IsRealistic = true;
        Pump(() => vm.Viewer.View.Environment is not null);
        Frame();
        long uploaded = fake.Counters.UploadBytesTotal;
        var env = vm.Viewer.View.Environment;
        for (int deg = 0; deg < 360; deg += 30)
        {
            vm.Document.Look = new C3dLook { Rotation = deg };
            vm.Viewer.LookChanged();
            Frame();
            Assert.Equal(deg, plan.Uniforms[Scene3DFramePlan.LookAt + 2] is var cos && MathF.Abs(cos - MathF.Cos(deg * MathF.PI / 180)) < 1e-5f ? deg : -1);
        }
        Assert.Same(env, vm.Viewer.View.Environment);
        Assert.Equal(uploaded, fake.Counters.UploadBytesTotal);
        Assert.Equal(4, fake.EnvironmentUploads);
    }

    // ── 8. the menus and the toolbar ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate8_BothMenusCarryRealisticViewAsACheckItem_ShowingL_AndTheToggleFollowsOrthographic()
    {
        var xml = XDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Views", "WorkspaceWindow.axaml")));
        var items = xml.Descendants()
            .Where(e => e.Name.LocalName is "MenuItem" or "NativeMenuItem" && ((string?)e.Attribute("Header"))?.Replace("_", "") == "Realistic View")
            .ToList();
        Assert.Equal(["MenuItem", "NativeMenuItem"], items.Select(e => e.Name.LocalName).Order(StringComparer.Ordinal));
        foreach (var e in items)
        {
            Assert.Equal("CheckBox", (string?)e.Attribute("ToggleType"));
            Assert.Equal("{Binding ToggleThreeDRealisticCommand}", (string?)e.Attribute("Command"));
            // the view answers L itself: the in-window menu SHOWS it (as Perspective's P); the macOS menu carries no key equivalent,
            // which would take a bare L from every text box
            Assert.Null(e.Attribute("Gesture"));
            Assert.Equal(e.Name.LocalName == "MenuItem" ? "L" : null, (string?)e.Attribute("InputGesture"));
            // under 3D ▸ View, right after Perspective
            Assert.Equal("Perspective", ((string?)e.ElementsBeforeSelf().Last().Attribute("Header"))?.Replace("_", ""));
        }

        string toolbar = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Views", "ThreeD", "C3dEditorView.axaml"));
        int ortho = toolbar.IndexOf("ViewModel.Viewer.IsOrthographic", StringComparison.Ordinal);
        int real = toolbar.IndexOf("ViewModel.Viewer.IsRealistic", StringComparison.Ordinal);
        Assert.True(ortho > 0 && real > ortho, "the toggle is not after Orthographic");
        string between = toolbar[ortho..real];
        Assert.Single(Regex.Matches(between, "<ToggleButton"));                // the realistic view's, the very next toggle
        Assert.Contains("Kind=\"CameraIris\"", toolbar[real..(real + 400)]);
    }

    /// <summary>The Look panel opens leftward (over the Objects tree, not the viewport), and the viewport is its dismiss overlay's
    /// pass-through element, so the wheel still zooms the picture while the panel is open.</summary>
    [Fact]
    public void LookPanel_OpensLeftward_AndLetsTheViewportTakeTheWheel()
    {
        string dir = Path.Combine(RepoRoot(), "src", "Ui", "Views", "ThreeD");
        string toolbar = File.ReadAllText(Path.Combine(dir, "C3dEditorView.axaml"));
        int look = toolbar.IndexOf("x:Name=\"LookButton\"", StringComparison.Ordinal);
        Assert.True(look > 0);
        Assert.Matches("<Flyout Placement=\"BottomEdgeAlignedRight\"[^>]*Opened=\"OnLookOpened\"", toolbar[look..]);
        Assert.Contains("<Panel Grid.Column=\"1\" x:Name=\"ViewportPanel\">", toolbar);
        Assert.Contains("look.OverlayInputPassThroughElement = ViewportPanel", File.ReadAllText(Path.Combine(dir, "C3dEditorView.axaml.cs")));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────

    private void Pump(Func<bool> until)
        => Assert.True(SpinWait.SpinUntil(() =>
        {
            while (_posted.TryDequeue(out var a)) a();
            return until();
        }, TimeSpan.FromSeconds(60)), "never settled");

    private C3dEditorViewModel Open(C3dDocument doc)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech", Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue);
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(400, 300);
        vm.Viewer.Session.EnsureBackend();
        vm.Start();
        Pump(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested);
        return vm;
    }

    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitRF.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }
}

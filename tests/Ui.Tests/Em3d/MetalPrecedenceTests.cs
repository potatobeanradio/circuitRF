// 3D editor round 3 — metal always takes precedence over dielectric where two solids overlap (em-3d.md §6.3a): in
// openEMS's priorities, in the gmsh script's cut order, in the section view's paint order, and in the 3D view's depth
// test and pick. Construction order decides only within each class.

using System.Numerics;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

public sealed class MetalPrecedenceTests
{
    private const double mm = 1e-3;

    /// <summary>A substrate (order 1), a pad sunk into it flush with its top (order 2), and a dielectric cover drawn LAST
    /// (order 3) over the pad — the case construction order alone would hand to the dielectric.</summary>
    private static Em3dProblem Problem(bool coverLast = true)
    {
        var materials = new[]
        {
            new Em3dMaterial("Sub", 4.4, null, 0, 1, 0),
            new Em3dMaterial("Mold", 3.9, null, 0, 1, 0),
            new Em3dMaterial("Cu", 1, null, 0, 1, 5.8e7),
        };
        var solids = new List<Em3dSolid>
        {
            new("substrate", "Sub", Em3dRole.Dielectric, new Em3dBox(new(-1 * mm, -1 * mm, 0), new(1 * mm, 1 * mm, 0.2 * mm)), 1),
            new("pad", "Cu", Em3dRole.Conductor, new Em3dBox(new(-0.3 * mm, -0.3 * mm, 0.1 * mm), new(0.3 * mm, 0.3 * mm, 0.2 * mm)), 2),
        };
        if (coverLast)
            solids.Add(new("cover", "Mold", Em3dRole.Dielectric, new Em3dBox(new(-0.5 * mm, -0.5 * mm, 0.15 * mm), new(0.5 * mm, 0.5 * mm, 0.4 * mm)), 3));
        var box = new Em3dAirBox(new(-1 * mm, -1 * mm, 0), new(1 * mm, 1 * mm, 1 * mm),
            new Em3dFaces(Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing,
                          Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Pec, Em3dBoundaryKind.Absorbing));
        // One lumped port from the ground plane up to the pad's underside — a writer refuses a problem with nothing to excite.
        var port = new Em3dPort(1, "port/1", "pad", "airbox/zmin", new(-0.3 * mm, -0.1 * mm, 0), new(-0.3 * mm, 0.1 * mm, 0.1 * mm),
                                new(0, 0, 1), 50, new Em3dReferencePlane(new(-0.3 * mm, 0, 0), new(0, 0, 1), 0));
        return new Em3dProblem(solids, [], materials, [port], box, new Em3dFrequency(1e9, 10e9, 10, Em3dSweepKind.Linear), 20);
    }

    [Fact]
    public void TheRule_RaisesEveryMetalAboveEveryDielectric_AndChangesNothingAlreadyInThatOrder()
    {
        var p = Problem();
        var prec = Em3dPrecedence.Of(p);
        int pad = prec.Of(p.Solids[1]), cover = prec.Of(p.Solids[2]), sub = prec.Of(p.Solids[0]);
        Assert.True(pad > cover && cover > sub, $"pad {pad}, cover {cover}, substrate {sub}");
        Assert.True(pad < prec.Max + 1);

        // Metal already above every dielectric: priorities ARE the construction order, so the files do not move.
        var ordered = Problem(coverLast: false);
        var same = Em3dPrecedence.Of(ordered);
        Assert.Equal(0, same.Shift);
        Assert.All(ordered.Solids, s => Assert.Equal(s.Order, same.Of(s)));
    }

    [Fact]
    public void OpenEms_WritesThePadAtAHigherPriorityThanTheDielectricDrawnAfterIt()
    {
        var p = Problem();
        var grid = FdtdGrid.Build(p, OpenEmsGridSettings.Default, long.MaxValue);
        var low = CsxcadWriter.Write(p, grid, OpenEmsGridSettings.Default, OpenEmsRunSettings.Default);
        Assert.True(low.Ok, low.Refusal);
        var doc = System.Xml.Linq.XDocument.Parse(low.Model!);
        int Priority(string name) => (int)doc.Descendants().Single(e => (string?)e.Attribute("Name") == name)
                                             .Descendants("Box").Single().Attribute("Priority")!;
        Assert.True(Priority("pad") > Priority("cover"), $"pad {Priority("pad")}, cover {Priority("cover")}");
        Assert.True(Priority("cover") > Priority("substrate"));
    }

    [Fact]
    public void Palace_CutsTheDielectricByThePad_NeverThePadByTheDielectric()
    {
        var p = Problem();
        var low = GmshGeoWriter.Write(p, PalaceSettings.Resolve(new CemPalace()));
        Assert.True(low.Ok, low.Refusal);
        var cuts = low.Geo!.Split('\n').Where(l => l.Contains("BooleanDifference{ Volume{s")).ToList();
        // s0 substrate, s1 pad, s2 cover: the pad loses nothing; the cover and the substrate lose the pad.
        Assert.DoesNotContain(cuts, l => l.StartsWith("s1[] = "));
        Assert.Contains(cuts, l => l.StartsWith("s2[] = ") && l.Contains("s1[]"));
        Assert.Contains(cuts, l => l.StartsWith("s0[] = ") && l.Contains("s1[]"));
    }

    [Fact]
    public void TheSectionView_PaintsTheMetalLast()
    {
        var p = Problem();
        var scene = CircuitRF.Render.Em3dSectionScene.Build(p, new(CircuitRF.Render.Em3dViewKind.SectionX, 0));
        var order = scene.Regions.Select(r => r.Object).ToList();
        Assert.Equal("pad", order[^1]);
    }

    [Fact]
    public void TheViewport_GivesTheMetalFace_WhereItLiesOnADielectricsFace()
    {
        // Pad top (z 0.2 mm) lies exactly in the substrate's top face; looked at from straight above.
        var p = Problem(coverLast: false);
        var scene = Scene3DBuilder.Build(p, 1);
        uint padId = scene.Objects.Single(o => o.Name == "pad").Id;
        var cam = Camera3D.Fit(scene.ContentMin, scene.ContentMax, 1, Projection3D.Orthographic);
        cam.SetStandardView(StandardView3D.Top);
        const int w = 101, h = 101;

        Assert.Equal(padId, Scene3DPicking.PairAtPixel(scene, cam, 50, 50, w, h, default).Id);
        Assert.Equal(padId, Scene3DPicking.Pick(scene, cam, 50, 50, w, h, default).Id);
        var patch = new Scene3DIdPatch();
        patch.Render(scene, cam, 50.5f, 50.5f, w, h, 5, default);
        Assert.All(patch.Ids.Take(patch.Size * patch.Size), id => Assert.Equal(padId, id));

        // The GPU's half: the substrate's translucent draw and its pick draw carry the bias; the pad's do not.
        var view = new Viewer3DViewState { Camera = cam, CursorX = 50, CursorY = 50 };
        view.Adopt(scene, null);
        Array.Fill(view.Visible, true);   // the viewer opens with the outermost dielectric hidden; the editor shows it
        var plan = new Scene3DFramePlan();
        plan.Plan(scene, view, w, h, false, true, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
        Assert.Contains(plan.Draws.Take(plan.DrawCount), d => d.Pipeline == Scene3DPipeline.Translucent && d.Behind);
        Assert.DoesNotContain(plan.Draws.Take(plan.DrawCount), d => d.Pipeline == Scene3DPipeline.Opaque && d.Behind);
        Assert.Contains(plan.PickDraws.Take(plan.PickDrawCount), d => d.Behind);
        Assert.Contains(plan.PickDraws.Take(plan.PickDrawCount), d => !d.Behind);
    }

    /// <summary>A port is ordered by where it is, not by its centroid against the substrate's (which flipped as the camera turned and
    /// faded a port in and out): one INSIDE the substrate is drawn before it from every side, so the substrate's near face fades it;
    /// one ON its top face, or just above it on the copper as a component port is, is drawn after it seen from above, crisp, and before
    /// it seen from below.</summary>
    [Theory]
    [InlineData(StandardView3D.Top)]
    [InlineData(StandardView3D.Bottom)]
    [InlineData(StandardView3D.Left)]
    [InlineData(StandardView3D.Right)]
    public void APort_IsOrderedByWhereItLiesInTheDielectric_FromEverySide(StandardView3D side)
    {
        var p = Problem(coverLast: false);
        Em3dPort Flat(int n, double z) => new(n, $"port/{n}", "pad", "substrate", new(0.4 * mm, -0.1 * mm, z), new(0.6 * mm, 0.1 * mm, z),
                                              new(1, 0, 0), 50, new Em3dReferencePlane(new(0.4 * mm, 0, z), new(1, 0, 0), 0));
        var scene = Scene3DBuilder.Build(p with { Ports = [p.Ports[0], Flat(2, 0.2 * mm), Flat(3, 0.2175 * mm)] }, 1);
        uint Id(string name) => scene.Objects.Single(o => o.Name == name).Id;
        var cam = Camera3D.Fit(scene.ContentMin, scene.ContentMax, 1, Projection3D.Orthographic);
        cam.SetStandardView(side);
        var view = new Viewer3DViewState { Camera = cam };
        view.Adopt(scene, null);
        Array.Fill(view.Visible, true);
        var plan = new Scene3DFramePlan();
        plan.Plan(scene, view, 101, 101, false, true, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
        var order = plan.Draws.Take(plan.DrawCount).Where(d => d.Pipeline == Scene3DPipeline.Translucent).Select(d => d.Object).ToList();
        int At(string name) => order.IndexOf(Id(name));

        Assert.True(At("port/1") < At("substrate"), $"inside: port/1 at {At("port/1")}, substrate at {At("substrate")}");
        foreach (string flat in new[] { "port/2", "port/3" })
        {
            if (side == StandardView3D.Top) Assert.True(At(flat) > At("substrate"), $"{flat}, seen from above");
            if (side == StandardView3D.Bottom) Assert.True(At(flat) < At("substrate"), $"{flat}, seen from below");
        }
        if (side != StandardView3D.Top) return;
        // Low from above, round the compass: the port's offset across the board outweighs its height, which is where its centroid
        // sorted it behind the substrate's.
        for (int k = 0; k < 4; k++)
        {
            cam.Yaw = k * MathF.PI / 2;
            cam.Pitch = 20 * MathF.PI / 180;
            view.Camera = cam;
            plan.Plan(scene, view, 101, 101, false, true, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
            order = plan.Draws.Take(plan.DrawCount).Where(d => d.Pipeline == Scene3DPipeline.Translucent).Select(d => d.Object).ToList();
            Assert.True(At("port/3") > At("substrate"), $"port/3 at yaw {k * 90}°, 20° above");
            Assert.True(At("port/1") < At("substrate"), $"port/1 at yaw {k * 90}°, 20° above");
        }
    }

    /// <summary>A .c3d dielectric reaches the scene as a Body (C3dElaborator's origin), not a Dielectric: a saw-cut package's
    /// lead, its end flush with the mould's side wall, lost that face to the mould in half the pixels and flickered.</summary>
    [Fact]
    public void TheViewport_GivesALeadEndOverAMouldBodysSideWall()
    {
        var materials = new[] { new Em3dMaterial("Mold", 3.9, null, 0, 1, 0), new Em3dMaterial("Cu", 1, null, 0, 1, 5.8e7) };
        var mould = new Em3dSolid("mould", "Mold", Em3dRole.Dielectric, new Em3dBox(new(-0.5 * mm, -0.5 * mm, 0), new(0.5 * mm, 0.5 * mm, 0.4 * mm)), 1);
        var lead = new Em3dSolid("lead", "Cu", Em3dRole.Conductor, new Em3dBox(new(0.3 * mm, -0.1 * mm, 0), new(0.5 * mm, 0.1 * mm, 0.1 * mm)), 2);
        var p = Problem(coverLast: false) with { Solids = [mould, lead], Materials = materials, Ports = [] };
        var origins = new Dictionary<string, Em3dObjectOrigin>
        {
            ["mould"] = new(Em3dObjectKind.Body, null, null, null), ["lead"] = new(Em3dObjectKind.Conductor, null, null, null),
        };
        var scene = Scene3DBuilder.Build(p, 1, origins);
        uint mouldId = scene.Objects.Single(o => o.Name == "mould").Id, leadId = scene.Objects.Single(o => o.Name == "lead").Id;
        Assert.Equal(Scene3DKind.Body, scene.Objects[mouldId - 1].Kind);
        Assert.Equal(Scene3DDepthTie.Behind, Scene3DFramePlan.TieOf(scene, mouldId));
        Assert.Equal(Scene3DDepthTie.None, Scene3DFramePlan.TieOf(scene, leadId));

        var cam = Camera3D.Fit(scene.ContentMin, scene.ContentMax, 1, Projection3D.Orthographic);
        cam.SetStandardView(StandardView3D.Right);
        const int w = 201, h = 201;
        var q = cam.Project(scene.ToLocal(0.5 * mm, 0, 0.05 * mm), w, h);
        Assert.Equal(leadId, Scene3DPicking.Pick(scene, cam, q.X, q.Y, w, h, default).Id);
    }

    /// <summary>3D editor bugs round 9 — a pad on a substrate, a via through both flush with the pad's top, and a lumped
    /// port lying ON the pad's top beside the via: three coincident faces at z 0.2 mm.</summary>
    private static (Scene3DModel Scene, uint Pad, uint Via, uint Port) Stack()
    {
        var p = Problem(coverLast: false);
        var via = new Em3dSolid("via", "Cu", Em3dRole.Conductor, new Em3dCylinder(new(0, 0, 0), new(0, 0, 0.2 * mm), 0.05 * mm), 3);
        var port = new Em3dPort(1, "port/1", "pad", "pad", new(0.1 * mm, -0.1 * mm, 0.2 * mm), new(0.25 * mm, 0.1 * mm, 0.2 * mm),
                                new(1, 0, 0), 50, new Em3dReferencePlane(new(0.1 * mm, 0, 0.2 * mm), new(1, 0, 0), 0));
        p = p with { Solids = [.. p.Solids, via], Ports = [port] };
        var origins = new Dictionary<string, Em3dObjectOrigin> { ["via"] = new(Em3dObjectKind.Via, null, null, null) };
        var scene = Scene3DBuilder.Build(p, 1, origins);
        uint Id(string name) => scene.Objects.Single(o => o.Name == name).Id;
        return (scene, Id("pad"), Id("via"), Id("port/1"));
    }

    [Fact]
    public void TheViewport_GivesAViaFaceOverItsPad_AndAPortOverBoth()
    {
        var (scene, pad, via, port) = Stack();
        var cam = Camera3D.Fit(scene.ContentMin, scene.ContentMax, 1, Projection3D.Orthographic);
        cam.SetStandardView(StandardView3D.Top);
        const int w = 201, h = 201;
        (float X, float Y) At(double x, double y) { var q = cam.Project(scene.ToLocal(x, y, 0.2 * mm), w, h); return (q.X, q.Y); }

        foreach (var (x, y, want) in new[] { (0.0, 0.0, via), (0.2 * mm, 0.0, port), (-0.2 * mm, 0.0, pad) })
        {
            var (px, py) = At(x, y);
            Assert.Equal(want, Scene3DPicking.PairAtPixel(scene, cam, px, py, w, h, default).Id);
            Assert.Equal(want, Scene3DPicking.Pick(scene, cam, px, py, w, h, default).Id);
            var patch = new Scene3DIdPatch();
            patch.Render(scene, cam, px, py, w, h, 1, default);
            Assert.Equal(want, patch.Ids[0]);
        }

        // The GPU's half: each draw carries its object's tie, which the backends turn into a polygon offset.
        var view = new Viewer3DViewState { Camera = cam, CursorX = 100, CursorY = 100 };
        view.Adopt(scene, null);
        Array.Fill(view.Visible, true);
        var plan = new Scene3DFramePlan();
        plan.Plan(scene, view, w, h, false, true, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
        Scene3DDepthTie TieOf(uint id)
        {
            var b = scene.Batches.Single(x => x.ObjectId == id);
            return plan.Draws.Take(plan.DrawCount).First(d => d.Buffer == Scene3DBuffer.Scene && d.First == b.FirstIndex).Tie;
        }
        Assert.Equal(Scene3DDepthTie.None, TieOf(pad));
        Assert.Equal(Scene3DDepthTie.Via, TieOf(via));
        Assert.Equal(Scene3DDepthTie.Port, TieOf(port));
        Assert.True(Scene3DFramePlan.DepthBias(Scene3DDepthTie.Port).Constant < Scene3DFramePlan.DepthBias(Scene3DDepthTie.Via).Constant);
        Assert.True(Scene3DFramePlan.DepthBias(Scene3DDepthTie.Via).Constant < 0 && Scene3DFramePlan.DepthBias(Scene3DDepthTie.Behind).Constant > 0);
    }

    [Fact]
    public void ALumpedPort_IsACheckerboardOverExactlyItsOwnRectangle_WithAnArrowOnItToThePlusEdge()
    {
        var (scene, _, _, port) = Stack();
        var o = scene.Objects[port - 1];
        var b = scene.Batches.Single(x => x.ObjectId == port);
        var verts = Enumerable.Range(b.FirstIndex, b.IndexCount).Select(i => scene.Vertices[scene.Indices[i]]).ToList();
        const float tol = 1e-9f;
        // 0.15 x 0.2 mm: cells 0.075 mm, two along x and three along y (rounded) in two colours, then the arrow's shaft
        // (two triangles) and head (one) in a third.
        Assert.Equal(3 * (2 * 2 * 3 + 3), b.IndexCount);
        Assert.Equal(3, verts.Select(v => v.Rgba).Distinct().Count());
        // The arrow is drawn last and points along +x (the port's direction): its tip is its farthest vertex that way.
        var arrow = verts.Skip(3 * 2 * 2 * 3).ToList();
        Assert.Single(arrow.Select(v => v.Rgba).Distinct());
        Assert.Equal(arrow[^2].X, arrow.Max(v => v.X));
        Assert.True(arrow[^2].X > (scene.ToLocal(0.1 * mm, 0, 0).X + scene.ToLocal(0.25 * mm, 0, 0).X) / 2);
        // Centred across the sheet's width: the tip on the port's centre line (y 0), the shaft symmetric about it.
        Assert.Equal(scene.ToLocal(0, 0, 0).Y, arrow[^2].Y, tol);
        Assert.Equal(scene.ToLocal(0, 0, 0).Y, (arrow.Min(v => v.Y) + arrow.Max(v => v.Y)) / 2, tol);
        var lo = scene.ToLocal(0.1 * mm, -0.1 * mm, 0.2 * mm);
        var hi = scene.ToLocal(0.25 * mm, 0.1 * mm, 0.2 * mm);
        Assert.Equal(lo.X, verts.Min(v => v.X), tol); Assert.Equal(hi.X, verts.Max(v => v.X), tol);
        Assert.Equal(lo.Y, verts.Min(v => v.Y), tol); Assert.Equal(hi.Y, verts.Max(v => v.Y), tol);
        Assert.All(verts, v => Assert.Equal(lo.Z, v.Z, tol));
        // No arrow standing off the sheet: the port's lines are its outline, in its plane.
        var lines = scene.LineBatches.Where(l => l.ObjectId == port)
                         .SelectMany(l => scene.LineVertices.Skip(l.FirstVertex).Take(l.VertexCount)).ToList();
        Assert.Equal(8, lines.Count);
        Assert.All(lines, v => Assert.Equal(lo.Z, v.Z, tol));
    }

    /// <summary>brief-cxp-2 — a coaxial port is the same checkerboard on its ring: two cells across the gap and an even number round
    /// it, every vertex in the ring's plane between its radii, four arrows last in the arrow's colour pointing from the shell
    /// in to the pin, and its two circles as its lines.</summary>
    [Fact]
    public void ACoaxialPort_IsACheckerboardOverExactlyItsRing_WithFourArrowsFromTheShellToThePin()
    {
        var (a, b0) = (0.2 * mm, 0.67 * mm);
        var ring = new Em3dPort(1, "port/1", "pin", "shell", new(-5 * mm, -b0, -b0), new(-5 * mm, b0, b0), new(0, 0, 0), 50,
                                new Em3dReferencePlane(new(-5 * mm, 0, 0), new(1, 0, 0), 0)) { Annulus = new Em3dAnnulus(a, b0, Outward: false) };
        var p = Problem(coverLast: false) with { Ports = [ring], Solids = [], Boundary = new Em3dAirBox(new(-6 * mm, -1 * mm, -1 * mm), new(-4 * mm, 1 * mm, 1 * mm), Problem(coverLast: false).Boundary.Faces) };
        var scene = Scene3DBuilder.Build(p, 1, options: new Scene3DBuildOptions(DrawAirBox: false, Origin: (0, 0, 0), HideOutermostDielectric: false));
        uint id = scene.Objects.Single(o => o.Name == "port/1").Id;
        var batch = scene.Batches.Single(x => x.ObjectId == id);
        var verts = Enumerable.Range(batch.FirstIndex, batch.IndexCount).Select(i => scene.Vertices[scene.Indices[i]]).ToList();
        // gap 0.47 mm, cells 0.235 mm: round the mid-radius 0.435 mm, 2π·0.435/0.235 → 12; 32 segments a turn → 3 steps a cell;
        // two cells deep: 2·12·3 quads of 6 indices, then four arrows of 9.
        Assert.Equal(2 * 12 * 3 * 6 + 4 * 9, batch.IndexCount);
        Assert.Equal(3, verts.Select(v => v.Rgba).Distinct().Count());
        var centre = scene.ToLocal(-5 * mm, 0, 0);
        double R(Scene3DVertex v) => Math.Sqrt(Math.Pow(v.Y - centre.Y, 2) + Math.Pow(v.Z - centre.Z, 2));
        Assert.All(verts, v => Assert.Equal(centre.X, v.X, 1e-9f));
        Assert.All(verts, v => Assert.InRange(R(v), a - 1e-9, b0 + 1e-9));
        // each arrow's tip (its head's middle vertex) is nearer the pin than any of its other vertices: shell to pin
        var arrows = verts.Skip(2 * 12 * 3 * 6).Chunk(9).ToList();
        Assert.Equal(4, arrows.Count);
        Assert.All(arrows, arrow => Assert.Equal(arrow.Min(R), R(arrow[7]), 1e-9));
        Assert.Single(arrows.SelectMany(x => x).Select(v => v.Rgba).Distinct());
        // its lines are its two circles, in its plane
        var lines = scene.LineBatches.Where(l => l.ObjectId == id).SelectMany(l => scene.LineVertices.Skip(l.FirstVertex).Take(l.VertexCount)).ToList();
        Assert.Equal(2 * 2 * Scene3DBuilder.AnnulusSegments, lines.Count);
        Assert.All(lines, v => Assert.Equal(centre.X, v.X, 1e-9f));
    }

    /// <summary>A copper layer embedded in the slab above it and reaching the board's side (0.6 mm either side of x = 0, on
    /// y = −1 mm), its top face hidden; the realistic view of it, and the copper top's own colour from that view.</summary>
    private static (Scene3DModel Scene, Viewer3DViewState View, Camera3D Cam, uint Copper) BoardSide(float yawDeg)
    {
        var materials = new[] { new Em3dMaterial("Sub", 4.4, null, 0, 1, 0), new Em3dMaterial("Cu", 1, null, 0, 1, 5.8e7) };
        Em3dSolid Slab(string name, double z0, double z1)
            => new(name, "Sub", Em3dRole.Dielectric, new Em3dBox(new(-1 * mm, -1 * mm, z0 * mm), new(1 * mm, 1 * mm, z1 * mm)), 1);
        var copper = new Em3dSolid("copper", "Cu", Em3dRole.Conductor, new Em3dBox(new(-0.6 * mm, -1 * mm, 0.1 * mm), new(0.6 * mm, 0, 0.118 * mm)), 2);
        var p = Problem(coverLast: false) with { Solids = [Slab("lower", 0, 0.1), Slab("upper", 0.1, 0.3), copper], Materials = materials, Ports = [] };
        var scene = Scene3DBuilder.Build(p, 1, options: new Scene3DBuildOptions(DrawAirBox: false, Origin: (0, 0, 0), HideOutermostDielectric: false));

        const int w = 480, h = 360;
        var cam = Camera3D.Fit(scene.ContentMin, scene.ContentMax, w / (float)h, Projection3D.Orthographic);
        (cam.Yaw, cam.Pitch, cam.Distance) = (yawDeg * MathF.PI / 180, 35 * MathF.PI / 180, 0.8e-3f);
        cam.Target = scene.ToLocal(0, -1 * mm, 0.11 * mm);
        var look = new CircuitRF.Design.ThreeD.C3dLook { Shadows = false, AmbientOcclusion = false, Ground = false };
        var view = new Viewer3DViewState { Camera = cam, Realistic = true, Look = CircuitRF.Render.Scene3D.Look.RealisticLook.From(look) };
        view.Environment = CircuitRF.Render.Scene3D.Look.EnvironmentPrefilter.Studio(view.Look.Studio);
        view.Adopt(scene, null);
        Array.Fill(view.Visible, true);
        return (scene, view, cam, scene.Objects.Single(o => o.Name == "copper").Id);
    }

    private const int SideW = 480, SideH = 360;

    private static byte[] Draw(Scene3DModel scene, Viewer3DViewState view)
        => CircuitRF.Render.Scene3D.Look.RealisticPicture.Take(scene, view, null, SideW, SideH, supersample: 1).Rgba;

    private static (int R, int G, int B) At(byte[] px, Camera3D cam, Vector3 world)
    {
        var q = cam.Project(world, SideW, SideH);
        int i = ((int)q.Y * SideW + (int)q.X) * 4;
        return (px[i], px[i + 1], px[i + 2]);
    }

    private static int Diff((int R, int G, int B) a, (int R, int G, int B) b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    /// <summary>The copper's top face meets the slab's wall along that edge and is hidden: the realistic picture shows none of it.
    /// With the dielectric's slope factor at 2 the top face came through the wall as a lit line along the copper's edge (owner
    /// report, 2026-10-25), and at 1/16 as dots; brief-em3d-138 carved the wall where the copper lies on it and drew the rest with
    /// no bias, which closes it at every view rather than thinning it.</summary>
    [Fact]
    public void TheRealisticView_ShowsNoHiddenCopperTop_AlongTheBoardsSide()
    {
        foreach (float yaw in new[] { -40f, -60f, -115f })
        {
            var (scene, view, cam, copperId) = BoardSide(yaw);
            // The copper top's own colour, from this view with the slabs hidden — what a pixel of it coming through would be.
            for (uint id = 1; id <= scene.Objects.Length; id++) view.Visible[id - 1] = id == copperId;
            var top = At(Draw(scene, view), cam, scene.ToLocal(0, -0.5 * mm, 0.118 * mm));
            Array.Fill(view.Visible, true);
            var px = Draw(scene, view);
            var side = At(px, cam, scene.ToLocal(0, -1 * mm, 0.109 * mm));
            Assert.True(Diff(side, top) > 60, $"yaw {yaw}: the copper's side {side} and top {top} are too alike for the picture to show one through the other");
            int through = 0;
            for (int i = 0; i < px.Length; i += 4)
                if (Diff((px[i], px[i + 1], px[i + 2]), top) < 24) through++;
            Assert.True(through == 0, $"yaw {yaw}: {through} pixels of the hidden copper top came through the slab's wall");
        }
    }

    /// <summary>brief-em3d-138 — with no bias left to help it, the copper's side still wins where it lies on the board's side: every
    /// sample along the band at the wall is the copper side's colour, not the slab's.</summary>
    [Fact]
    public void TheRealisticView_DrawsTheCoppersSide_AllAlongItsBandOnTheWall()
    {
        var (scene, view, cam, _) = BoardSide(-60);
        var px = Draw(scene, view);
        var side = At(px, cam, scene.ToLocal(0, -1 * mm, 0.109 * mm));
        var wall = At(px, cam, scene.ToLocal(0, -1 * mm, 0.2 * mm));
        Assert.True(Diff(side, wall) > 60, $"the copper's side {side} and the slab's wall {wall} are too alike to tell apart");
        for (double x = -0.4; x <= 0.4; x += 0.05)
        {
            var c = At(px, cam, scene.ToLocal(x * mm, -1 * mm, 0.109 * mm));
            Assert.True(Diff(c, side) < 24, $"at x = {x:0.00} mm the band is {c}, not the copper's side {side}");
        }
    }
}

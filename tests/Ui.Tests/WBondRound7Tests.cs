using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The owner's SEVENTH batch (2026-08-18) — <b>the loop-profile object is removed</b>, and a wire's
/// points become the only truth about its shape in the model and in the UI.
///
/// <para><i>"User does not care if the profile is any of these 'profiles'. I don't like how wires get
/// this designation… I also don't like how the wire colors change when a wire converts to 'free' —
/// that's the most annoying part."</i> And the answer to the one question that could have saved the
/// concept: <i>"User would never share one loop shape for multiple arrays and want to edit it in one
/// place. Each array is generally its own shape and I want flexibility for user to change each wire
/// within the array."</i></para>
///
/// <para>Shared-shape propagation was the only thing a binding bought. What is gated here is the part
/// a user can SEE: one wire colour in the layout view, a band that spans every drawable member, a
/// group loop-height change that keeps the route the user drew, and a shape transfer that still
/// works with nothing stored anywhere.</para>
///
/// <para>The schema half of the change is gated in <c>WBond.Tests/PersistenceTests</c>, next to the
/// rest of the <c>.wBond</c> I/O.</para>
///
/// <para><b>Sections 4 and 5 are the owner's follow-up reports on the same day</b>, and neither is a
/// consequence of the removal — the profile view's second wire colour was the half this batch's first
/// pass kept, and the other two are older defects the reports finally pinned.</para>
/// </summary>
public class WBondRound7Tests
{
    private static readonly long Mil = WBondUnits.ToNm(1.0, WBondUnit.Mil);

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  1. A group loop-height change preserves the authored X-Y path
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A wire whose interior points sit OFF the straight chord in XY — a deliberate dog-leg, the wire
    /// a user routed around something.
    /// </summary>
    /// <param name="endZMil">The output foot's z; pass something other than the input's for the
    /// unequal-feet case, where the amplitude solve does different arithmetic.</param>
    private static WBondDesign DogLegDesign(double endZMil)
    {
        var design = new WBondDesign();
        var array = new WireArray { Name = "G1" };

        for (int w = 0; w < 2; w++)
        {
            var wire = new Wire { DiameterNm = Mil, Material = "Gold" };
            long y = (long)(w * 6.0 * Mil);

            // Feet at either end; the three interior points wander in Y as well as rising in Z.
            wire.Points.AddRange([
                new Point3(0,          y,             (long)(4.0 * Mil)),
                new Point3(25 * Mil,   y + 9 * Mil,   (long)(14.0 * Mil)),
                new Point3(50 * Mil,   y + 13 * Mil,  (long)(20.0 * Mil)),
                new Point3(75 * Mil,   y + 5 * Mil,   (long)(15.0 * Mil)),
                new Point3(100 * Mil,  y,             (long)(endZMil * Mil)),
            ]);

            array.Wires.Add(wire);
        }

        design.Arrays.Add(array);
        return design;
    }

    /// <summary>
    /// <b>Setting a GROUP's loop height leaves every X and Y exactly as authored.</b>
    ///
    /// <para>This was a real bug until 2026-08-18, and it is the one the removal had to fix on the
    /// way past. <c>SetGroupLoopHeight</c> read each wire's shape, put a new height on it and stamped
    /// it back — and stamping a shape writes X and Y by linear interpolation between the feet, so a
    /// hand-routed wire came back as a plain planar arc. The same wire's <c>LoopHeight_G1</c>
    /// controlling parameter has preserved its path since 2026-08-17, so <b>the editor and the
    /// netlist disagreed about the same wire</b>. Both now go through
    /// <c>WireEdits.SetLoopHeightPreservingPath</c>.</para>
    ///
    /// <para>Both foot cases are run: with the feet level, max-z-minus-min-z is the rise alone; with
    /// them unequal, part of the loop height is already supplied by the foot drop and the solve has
    /// to account for it.</para>
    /// </summary>
    [Theory]
    [InlineData(4.0)]    // level feet
    [InlineData(1.0)]    // die surface down to a package lead — the ordinary chip-and-wire case
    public void AGroupLoopHeightChange_KeepsEveryXAndY(double endZMil)
    {
        var vm = new WBondViewModel(DogLegDesign(endZMil));

        var before = vm.Design.AllWires().Select(w => w.Points.ToArray()).ToList();
        long requested = (long)(35.0 * Mil);

        Assert.Equal(2, vm.SetGroupLoopHeight(0, requested));

        var after = vm.Design.AllWires().ToList();
        for (int i = 0; i < after.Count; i++)
        {
            // Every X and Y, to the nanometre — including the interior points that wander.
            Assert.Equal(before[i].Length, after[i].Points.Count);
            for (int p = 0; p < before[i].Length; p++)
            {
                Assert.Equal(before[i][p].X, after[i].Points[p].X);
                Assert.Equal(before[i][p].Y, after[i].Points[p].Y);
            }

            // Both feet are bit-exact, z included.
            Assert.Equal(before[i][0], after[i].Points[0]);
            Assert.Equal(before[i][^1], after[i].Points[^1]);

            // …and the loop height really is what was asked for.
            Assert.InRange(after[i].LoopHeightNm, requested - 2, requested + 2);
        }
    }

    /// <summary>
    /// The same obligation on ONE wire, from the Properties inspector's own path — a wire and its
    /// group must not disagree about what "set the loop height" does to a route.
    /// </summary>
    [Fact]
    public void AWireLoopHeightChange_KeepsEveryXAndY()
    {
        var vm = new WBondViewModel(DogLegDesign(endZMil: 1.0));
        var before = vm.Design.AllWires().First().Points.ToArray();

        Assert.True(vm.SetWireLoopHeight(0, (long)(35.0 * Mil)));

        var after = vm.Design.AllWires().First();
        for (int p = 0; p < before.Length; p++)
        {
            Assert.Equal(before[p].X, after.Points[p].X);
            Assert.Equal(before[p].Y, after.Points[p].Y);
        }

        // Its sibling did not move at all.
        Assert.Equal(vm.Design.AllWires().Last().LoopHeightNm,
                     DogLegDesign(endZMil: 1.0).AllWires().Last().LoopHeightNm);
    }

    /// <summary>
    /// <b>A dead-straight wire is refused, not arched.</b> There is no rise to scale and nothing
    /// honest to do, so the primitive's own refusal is preserved rather than papered over with the
    /// interpolating path — which would invent a loop the user never drew.
    /// </summary>
    [Fact]
    public void ADeadStraightWire_IsRefusedRatherThanArched()
    {
        var design = new WBondDesign();
        var straight = new Wire { DiameterNm = Mil, Material = "Gold" };
        straight.Points.AddRange([
            new Point3(0, 0, 4 * Mil),
            new Point3(50 * Mil, 0, 4 * Mil),
            new Point3(100 * Mil, 0, 4 * Mil),
        ]);
        design.Arrays.Add(new WireArray { Name = "G1", Wires = { straight } });

        var vm = new WBondViewModel(design);
        var before = straight.Points.ToArray();

        Assert.Equal(0, vm.SetGroupLoopHeight(0, (long)(20.0 * Mil)));
        Assert.False(vm.SetWireLoopHeight(0, (long)(20.0 * Mil)));

        Assert.Equal(before, straight.Points.ToArray());
        Assert.False(vm.CanUndo);   // and neither refusal left an entry behind
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  2. NEITHER view ever colours a wire by its geometry
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// An array whose members are deliberately all different shapes — including one that doubles back
    /// on itself in XY, the wire the profile view cannot draw against normalised span.
    ///
    /// <para>Every one of these used to be coloured differently from its siblings by one view or the
    /// other, which is the whole of what is being gated.</para>
    /// </summary>
    private static WBondDesign MixedShapeArray()
    {
        var design = new WBondDesign();
        var array = new WireArray { Name = "G1" };
        long loopNm = (long)(20.0 * Mil);

        for (int w = 0; w < 4; w++)
            array.Wires.Add(LoopShape.CreateSeedWire(
                new Point3(0, (long)(w * 12.0 * Mil), 4 * Mil),
                new Point3(100 * Mil, (long)(w * 12.0 * Mil), 1 * Mil),
                Mil, "Gold", loopNm));

        WireEdits.ScaleHeightAboutChord(array.Wires[1], 1.9);   // a different shape
        WireEdits.InsertPointOnSegment(array.Wires[2], 2, 0.5); // a different point count
        WireEdits.ScaleHeightAboutChord(array.Wires[3], 0.55);  // …and a third distinct shape, so the
                                                               // profile view's clutter rule collapses
                                                               // none of them onto another

        // …and one whose XY path backtracks: legal geometry, and the ONE thing the band cannot
        // describe. It is drawn on its own, and it must be drawn in the same colour as the rest.
        var doglegs = new Wire { DiameterNm = Mil, Material = "Gold" };
        doglegs.Points.AddRange([
            new Point3(0,          48 * Mil, 4 * Mil),
            new Point3(80 * Mil,   48 * Mil, 24 * Mil),
            new Point3(30 * Mil,   48 * Mil, 24 * Mil),
            new Point3(100 * Mil,  48 * Mil, 1 * Mil),
        ]);
        array.Wires.Add(doglegs);

        design.Arrays.Add(array);
        return design;
    }

    /// <summary>
    /// Every lit pixel, bucketed into "the wire colour" and "something else".
    ///
    /// <para><b>The two POINT accents are excluded, and that is not a loophole.</b> The input-end dot
    /// (<c>wBond.WireStart</c>, WB3 — which end current enters is data, not decoration) and the vertex
    /// dot (<c>wBond.WireVertex</c>) are per-POINT marks that every wire carries identically. What is
    /// being gated is that no WHOLE WIRE is stroked in a second colour because of its shape.</para>
    ///
    /// <para>Matching is on HUE rather than on absolute channel values: an antialiased pixel, or one
    /// blended over the envelope band, is a darker version of the same colour, so comparing the
    /// channel ratios is what separates "the same colour, dimmer" from "a different colour".</para>
    /// </summary>
    private static (int OnColour, int OffColour) WirePixels(SKBitmap bitmap, WBondRenderTheme theme)
    {
        int w = bitmap.Width, h = bitmap.Height;

        // A wire stroke is OPAQUE; the envelope band is the wire's own RGB at alpha ~60 over black.
        // They are therefore the same HUE and only brightness tells them apart, which is why this
        // floor exists — without it the band (a six-figure pixel count) drowns out everything the
        // probe is actually looking at, and it also matches the input-end accent by ratio, which is a
        // DARKER version of the same hue. Both traps were live in the first version of this helper.
        const int StrokeBrightness = 200;

        static int Lum(SKColor c) => c.Red + c.Green + c.Blue;

        var accent = new bool[w, h];
        var candidate = new bool[w, h];
        int on = 0;

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var px = bitmap.GetPixel(x, y);
                if (Lum(px) < 24) continue;                      // background

                if (SameHue(px, theme.Wire))                     // the wire, or the band beneath it
                {
                    if (Lum(px) >= StrokeBrightness) on++;
                    continue;
                }

                if (SameHue(px, theme.InputEnd) || SameHue(px, theme.Vertex)) { accent[x, y] = true; continue; }

                candidate[x, y] = true;
            }

        // A point accent antialiased against the wire underneath produces intermediate hues matching
        // NEITHER colour — a blend, not a third paint. Radius 3 covers that fringe at any of the dot
        // sizes these renders use, and is far too small to hide a whole wire stroked in another
        // colour, which runs hundreds of pixels along its entire length.
        int off = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (candidate[x, y] && !NearAccent(x, y)) off++;

        return (on, off);

        bool NearAccent(int px, int py)
        {
            for (int dy = -3; dy <= 3; dy++)
                for (int dx = -3; dx <= 3; dx++)
                {
                    int x = px + dx, y = py + dy;
                    if (x >= 0 && y >= 0 && x < w && y < h && accent[x, y]) return true;
                }
            return false;
        }

        static bool SameHue(SKColor px, SKColor role)
        {
            double scale = Math.Max(px.Red, Math.Max(px.Green, px.Blue))
                         / Math.Max(1.0, Math.Max(role.Red, Math.Max(role.Green, role.Blue)));
            if (scale <= 0.0) return true;

            return Math.Abs(px.Red - role.Red * scale) < 26
                && Math.Abs(px.Green - role.Green * scale) < 26
                && Math.Abs(px.Blue - role.Blue * scale) < 26;
        }
    }

    /// <summary>
    /// <b>No wire in the LAYOUT view is drawn in a second colour</b> (owner, 2026-08-18: <i>"I don't
    /// like how the wire colors change when a wire converts to 'free' — that's the most annoying
    /// part"</i>).
    ///
    /// <para>The recolouring was involuntary and unexplained: inserting or deleting a vertex detached
    /// a wire, and so did a group height, span or flip — so an edit about one thing silently changed
    /// how the wire looked.</para>
    /// </summary>
    [Fact]
    public void TheLayoutView_DrawsEveryWireInOneColour()
    {
        var design = MixedShapeArray();
        var theme = WBondRenderTheme.Fallback;

        // A viewport that actually FRAMES the wires: at 1,000 DBU/µm the world is 2.54e6 units across
        // for a 100 mil wire, so a plausible-looking round zoom puts everything thousands of pixels
        // off canvas, where every pixel probe passes or fails for the wrong reason.
        const int size = 500;
        double extent = WBondUnits.ToNm(100.0, WBondUnit.Mil);
        double zoom = size / (2.1 * extent);
        double pan = extent / 2.0 - size / (2.0 * zoom);
        var viewport = new LayoutViewport(pan, pan, zoom, size, size);

        using var surface = SKSurface.Create(new SKImageInfo(size, size));
        surface.Canvas.Clear(SKColors.Black);

        var result = WBondRenderer.Draw(surface.Canvas, design, viewport, theme);
        surface.Canvas.Flush();

        Assert.Equal(5, result.WiresDrawn);

        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);
        var (on, off) = WirePixels(bitmap, theme);

        Assert.True(on > 200, $"the wires must actually be on screen; {on} lit pixels");
        Assert.True(off == 0,
            $"{off} pixels are neither the wire colour nor a point accent — a wire is being stroked " +
            $"in a second colour ({on} wire-coloured pixels for comparison).");
    }

    /// <summary>
    /// <b>And no wire in the PROFILE view either</b> (owner, 2026-08-18, second report: <i>"profile
    /// wire still renders in 'bad' color depending on shape… I don't want the wires ever changing
    /// colors based on geometry."</i>).
    ///
    /// <para>This was the half the first pass kept, on the reasoning that distinguishing the one
    /// editable curve from the members behind it is real information about that picture. It is — but
    /// it is carried by geometry, and paying for it in COLOUR is the same complaint one view along: a
    /// wire went off-colour for being SHAPED differently, and a backtracking wire went off-colour
    /// permanently. The role is deleted rather than renamed again.</para>
    ///
    /// <para><b>Presence is still a function of geometry and that is not what was reported</b> — a
    /// member is collapsed onto the representative only when drawing it would put a second polyline on
    /// pixels already covered. The fixture below has four visibly different shapes, so all five curves
    /// are drawn; what is gated is that they are drawn in ONE colour.</para>
    /// </summary>
    [Fact]
    public void TheProfileView_DrawsEveryWireInOneColour()
    {
        var design = MixedShapeArray();
        var theme = WBondRenderTheme.Fallback;

        const int size = 600;
        using var surface = SKSurface.Create(new SKImageInfo(size, size));
        surface.Canvas.Clear(SKColors.Black);

        var result = WBondRenderer.DrawProfile(
            surface.Canvas, design, theme,
            span => (float)(span / 4000.0), z => (float)(size - z / 2000.0),
            pixelsPerNm: 1.0 / 4000.0);
        surface.Canvas.Flush();

        // Every member is drawn: none of them project onto another, so the clutter rule does not fire.
        Assert.Equal(5, result.WiresDrawn);

        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);
        var (on, off) = WirePixels(bitmap, theme);

        Assert.True(on > 200, $"the curves must actually be on screen; {on} lit pixels");
        Assert.True(off == 0,
            $"{off} pixels are neither the wire colour nor a point accent — a curve is being stroked " +
            $"in a second colour ({on} wire-coloured pixels for comparison).");
    }

    /// <summary>
    /// <b>There is exactly ONE wire colour role in the whole of wBond</b>, and that is what stops
    /// this coming back a third time under a third name.
    /// </summary>
    [Fact]
    public void wBond_HasOnlyOneWireColourRole()
    {
        var wireRoles = ColorRole.All
            .Where(r => r.StartsWith("wBond.", StringComparison.Ordinal))
            .Where(r => r.Contains("Wire", StringComparison.OrdinalIgnoreCase)
                     || r.Contains("Member", StringComparison.OrdinalIgnoreCase)
                     || r.Contains("Free", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // wBond.Wire, plus the input-end dot (WB3) and the vertex accent — both are POINT accents on
        // a wire, not a second colour for a whole wire.
        Assert.Equal(["wBond.Wire", "wBond.WireStart", "wBond.WireVertex"], wireRoles);

        Assert.DoesNotContain(ColorRole.All, r => r.Contains("FreeWire", StringComparison.Ordinal));
        Assert.DoesNotContain(ColorRole.All, r => r.Contains("Member", StringComparison.Ordinal));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  3. Copy → Paste shape, with nothing stored anywhere
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Copy Coordinates → Paste still carries a shape between two arrays</b>, neither of which has
    /// a stored profile because there is no such thing any more.
    ///
    /// <para>This survived the removal on purpose: it is a one-shot transfer the user asks for by
    /// name, not a persistent link, and the link is what the owner rejected. The source's shape is
    /// read off its own geometry (<c>LoopShape.Read</c>), travels as normalised text, and is stamped
    /// onto every member of the target — <b>whose feet do not move</b>, because a shape says nothing
    /// about where a wire lands.</para>
    /// </summary>
    [Fact]
    public void CopyThenPasteAShape_CrossesArrays_AndTheTargetsFeetDoNotMove()
    {
        var design = new WBondDesign();

        // G1: a distinctly tall, late-cresting loop, flipped so it is nothing like the seed.
        var g1 = new WireArray { Name = "G1" };
        g1.Wires.Add(LoopShape.CreateWire(
            new Point3(0, 0, 4 * Mil), new Point3(100 * Mil, 0, 1 * Mil),
            Mil, "Gold", LoopShape.Flip(LoopShape.Seed()), (long)(40.0 * Mil)));
        design.Arrays.Add(g1);

        // G2: two shallow seeded wires on completely different pads, at different spans.
        var g2 = new WireArray { Name = "G2" };
        g2.Wires.Add(LoopShape.CreateSeedWire(
            new Point3(20 * Mil, 60 * Mil, 2 * Mil), new Point3(80 * Mil, 60 * Mil, 6 * Mil),
            Mil, "Gold", (long)(12.0 * Mil)));
        g2.Wires.Add(LoopShape.CreateSeedWire(
            new Point3(20 * Mil, 72 * Mil, 2 * Mil), new Point3(140 * Mil, 78 * Mil, 6 * Mil),
            Mil, "Gold", (long)(12.0 * Mil)));
        design.Arrays.Add(g2);

        var vm = new WBondViewModel(design);

        var sourceShape = vm.ShapeForGroup(0);
        Assert.NotNull(sourceShape);

        // Round-trip through the spreadsheet text, which is what the menu items actually do.
        string text = ProfileCoordinateText.Write(sourceShape!.Value.Shape, sourceShape.Value.LoopHeightNm,
                                                  WBondUnit.Mil);
        Assert.True(ProfileCoordinateText.TryRead(text, WBondUnit.Mil, out var shape, out long heightNm));

        var feetBefore = g2.Wires.Select(w => (w.Points[0], w.Points[^1])).ToArray();

        Assert.Equal(2, vm.ApplyShapeToGroup(1, shape, heightNm));

        for (int w = 0; w < g2.Wires.Count; w++)
        {
            // The feet are exactly where they were — a transferred shape lands on the pads that are
            // already there.
            Assert.Equal(feetBefore[w].Item1, g2.Wires[w].Points[0]);
            Assert.Equal(feetBefore[w].Item2, g2.Wires[w].Points[^1]);

            // …and the shape arrived: G2 now measures G1's loop height and crests where G1 crests.
            Assert.InRange(g2.Wires[w].LoopHeightNm, heightNm - 2, heightNm + 2);

            var read = LoopShape.Read(g2.Wires[w]);
            int crest = ArgMax(read);
            Assert.Equal(ArgMax(sourceShape.Value.Shape), crest);
        }

        // Nothing was installed anywhere as a side effect: the shape moved, no link was made.
        Assert.Equal(2, vm.Design.Arrays.Count);

        static int ArgMax(System.Collections.Generic.IReadOnlyList<ShapePoint> shape)
        {
            int best = 0;
            for (int i = 1; i < shape.Count; i++)
                if (shape[i].Height > shape[best].Height) best = i;
            return best;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  4. The profile view's press must hit-test in the plane the view is DRAWING
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Clicking a wire in the profile view selects it, so it can be dragged straight away</b>
    /// (owner, 2026-08-18: <i>"In Wire Profile view, I cannot click and drag wire points or segments.
    /// I must first drag-select with marquee tool, then it allows me to drag."</i>).
    ///
    /// <para><b>Two hit tests had to agree and did not.</b> The canvas resolves its own hit in the
    /// plane its toolbar names — YZ by shipped default since 2026-08-16 — and then handed the raw
    /// coordinates to <c>WBondPointerController.Press</c>, which hit-tested them AGAIN with its own
    /// defaults: <c>azimuthRadians: null</c>, meaning AUTO, each wire projected onto its own chord.
    /// Under a fixed plane those are different pictures, so the controller found nothing where the
    /// canvas had found a wire, cleared the selection, and the canvas then declined to arm a drag on
    /// an empty selection.</para>
    ///
    /// <para><b>It hid completely whenever anything was selected</b>, because a press on an
    /// already-selected element skips the controller call altogether — which is exactly why
    /// marquee-selecting first made dragging work, and why the report reads the way it does.</para>
    ///
    /// <para>The fixture is a wire running east-west viewed in YZ, where the two projections disagree
    /// as sharply as they can: in YZ the whole wire is foreshortened onto one span, while under auto
    /// it stretches across its full chord.</para>
    /// </summary>
    [Fact]
    public void AProfileViewPress_SelectsUsingThePlaneTheViewIsDrawing()
    {
        const double Yz = Math.PI / 2;   // WBondViewState.DefaultProfileAxisDegrees, in radians

        var design = new WBondDesign();
        var wire = LoopShape.CreateSeedWire(
            new Point3(0, 0, 4 * Mil), new Point3(100 * Mil, 0, 1 * Mil),
            Mil, "Gold", (long)(20.0 * Mil));
        design.Arrays.Add(new WireArray { Name = "G1", Wires = { wire } });

        var vm = new WBondViewModel(design);
        double tolerance = 3.0 * Mil;

        // The crest, as the canvas itself projects it.
        int crest = 0;
        for (int i = 1; i < wire.Points.Count; i++)
            if (wire.Points[i].Z > wire.Points[crest].Z) crest = i;

        var at = ProfileProjection.Project(wire, crest, ProfileProjection.SpanMode.Absolute, Yz);
        long span = (long)Math.Round(at.Span), z = (long)Math.Round(at.Z);

        // The canvas's own hit test — in ITS plane — finds the wire there.
        var hit = WireHitTest.HitTestProfile(vm.Mesh, span, z, tolerance,
                                             ProfileProjection.SpanMode.Absolute, azimuthRadians: Yz);
        Assert.True(hit.Found, "the fixture must put a wire under the pointer in the view's own plane");

        // …and the plane-BLIND test does not. This is the whole mechanism, stated once so the fixture
        // cannot quietly stop discriminating.
        var blind = WireHitTest.HitTestProfile(vm.Mesh, span, z, tolerance);
        Assert.False(blind.Found, "the fixture must be one where auto and YZ actually disagree");

        var controller = new WBondPointerController(vm);

        // The old path: coordinates in, re-tested in the wrong plane, selection CLEARED.
        controller.Press(span, z, tolerance, WBondModifiers.None, view: EditorView.Profile);
        Assert.True(vm.Selection.IsEmpty);

        // The fixed path: the canvas passes the hit it already resolved, and the press selects.
        controller.Press(hit, span, z, WBondModifiers.None);
        Assert.False(vm.Selection.IsEmpty);
    }

    /// <summary>
    /// The profile canvas passes its OWN hit to the controller, at both press and deferred-release,
    /// and never asks the controller to re-resolve one.
    ///
    /// <para>A source scan, because the property is about which overload is called — the behavioural
    /// test above cannot see that the canvas is the one calling it, and re-introducing the plane-blind
    /// call is exactly how this comes back.</para>
    /// </summary>
    [Fact]
    public void TheProfileCanvas_NeverAsksTheControllerToReResolveAHit()
    {
        var canvas = Read("src", "Ui", "Controls", "WBondProfileCanvas.cs");

        // EVERY press this canvas makes hands over a hit it resolved itself. There are two — the press
        // and the deferred click-through on release — and both were wrong in the same way.
        var calls = new System.Collections.Generic.List<string>();
        for (int at = 0; (at = canvas.IndexOf("_controller.Press(", at, StringComparison.Ordinal)) >= 0; at++)
            calls.Add(canvas[at..canvas.IndexOf(')', at)]);

        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.StartsWith("_controller.Press(hit,", c, StringComparison.Ordinal));
    }
    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  5. "Group Wire As…" on a right-clicked wire
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A right-click on ONE wire offers "Group Wire As…", singular and enabled</b> (owner,
    /// 2026-08-18: <i>"If I click on wire in layout host, the context menu 'Group Wires As…' is
    /// disabled. For a single wire right-click, this menu should be available for user and it should
    /// say 'Group Wire As…'."</i>).
    ///
    /// <para>The item was selection-scoped only, so a right-click on an unselected wire showed it
    /// greyed. It now follows the SAME rule the Straighten item directly below it already followed —
    /// the selection when there is a multi-selection, the clicked wire otherwise — which is also what
    /// made the two inconsistent before.</para>
    ///
    /// <para>The label already had the singular; nothing reached it, because the only way to get a
    /// count of one was a single selection, and a single selection loses to the click.</para>
    /// </summary>
    [Theory]
    [InlineData(0, "Group Wires As…")]
    [InlineData(1, "Group Wire As…")]
    [InlineData(2, "Group 2 Wires As…")]
    [InlineData(40, "Group 40 Wires As…")]
    public void TheGroupCommandLabel_SaysHowManyWiresItWillMove(int count, string expected) =>
        Assert.Equal(expected, WBondGroupCommand.Label(count));

    /// <summary>
    /// The layout overlay's group item resolves its subject the same way Straighten does, and the two
    /// sit in one block in the menu.
    ///
    /// <para>A source scan for the same reason the one above it is: the rule lives in which arguments
    /// the builder is given, and a builder handed only the host cannot see the pointer at all — which
    /// is precisely the shape the bug had.</para>
    /// </summary>
    [Fact]
    public void TheGroupMenuItem_IsResolvedAgainstThePointerLikeStraightenIs()
    {
        var menu = Read("src", "Ui", "WBond", "WBondLayoutOverlay.ContextMenu.cs");

        // It is given the click position, not just the host.
        Assert.Contains("BuildGroupItem(host, worldX, worldY, tolDbu)", menu, StringComparison.Ordinal);

        // …and resolves it with the same multi-selection-wins rule Straighten uses.
        int group = menu.IndexOf("private MenuItem BuildGroupItem(", StringComparison.Ordinal);
        int straighten = menu.IndexOf("private MenuItem BuildStraightenItem(", StringComparison.Ordinal);
        Assert.True(group >= 0 && straighten >= 0);

        foreach (int at in new[] { group, straighten })
        {
            string body = menu[at..menu.IndexOf("\n    /// <summary>", at, StringComparison.Ordinal)];
            Assert.Contains("selected.Count > 1 ? [.. selected]", body, StringComparison.Ordinal);
            Assert.Contains("hit.Found        ? [hit.Wire]", body, StringComparison.Ordinal);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  6. A profile-view drag moves the whole GROUP, and the band always covers all of it
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Three identical wires in one array, plus a second array to prove containment.</summary>
    private static WBondDesign TwoGroups()
    {
        var design = new WBondDesign();
        long loopNm = (long)(20.0 * Mil);

        for (int a = 0; a < 2; a++)
        {
            var array = new WireArray { Name = "G" + (a + 1) };
            for (int w = 0; w < 3; w++)
                array.Wires.Add(LoopShape.CreateSeedWire(
                    new Point3(a * 200 * Mil, (long)(w * 6.0 * Mil), 4 * Mil),
                    new Point3(a * 200 * Mil + 100 * Mil, (long)(w * 6.0 * Mil), 1 * Mil),
                    Mil, "Gold", loopNm));
            design.Arrays.Add(array);
        }
        return design;
    }

    /// <summary>
    /// <b>A profile-view edit moves every wire in the group</b> (owner, 2026-08-18: <i>"when I click
    /// drag a point/segment in Wire Profile view only 1 wire within the group moves. I want all the
    /// wires within that group to move."</i>).
    ///
    /// <para>The profile view draws a group as one superimposed shape under one envelope band, and a
    /// bond group is one loop program on one bonder. Alt-drag was moved onto the array on 2026-08-17
    /// for exactly that reason (WB24c); the plain drag was not moved with it, so the two gestures
    /// disagreed about what they were pointing at.</para>
    ///
    /// <para><b>The selection itself is left alone</b>, which is also what alt-drag does — this is the
    /// subject of one edit, not a re-selection, so the panel still reports the wire the user clicked.</para>
    /// </summary>
    [Fact]
    public void AProfileDragSubject_IsTheWholeGroup_AndStopsAtItsBoundary()
    {
        var vm = new WBondViewModel(TwoGroups());
        var selection = new WireSelection { Points = { new PointRef(0, 3) } };

        var subject = vm.ProfileGroupSubject(selection);

        // Point 3 of all three wires of G1 — and nothing from G2.
        Assert.Equal([new PointRef(0, 3), new PointRef(1, 3), new PointRef(2, 3)],
                     subject.Points.OrderBy(p => p.Wire).ToArray());
        Assert.Equal([0, 1, 2], subject.TouchedWires().Order().ToArray());

        // The selection the user made is untouched.
        Assert.Single(selection.Points);
    }

    /// <summary>A selected SEGMENT promotes the same way, and carries both its endpoints on every member.</summary>
    [Fact]
    public void AProfileDragSubject_PromotesASegmentAcrossTheGroup()
    {
        var vm = new WBondViewModel(TwoGroups());

        var subject = vm.ProfileGroupSubject(new WireSelection { Segments = { new SegmentRef(1, 2) } });

        Assert.Equal([new SegmentRef(0, 2), new SegmentRef(1, 2), new SegmentRef(2, 2)],
                     subject.Segments.OrderBy(sg => sg.Wire).ToArray());
    }

    /// <summary>
    /// A sibling with too few points to have the named element is <b>skipped, not approximated</b>.
    /// An array's members may legitimately differ in point count (§6.2), and guessing which of its
    /// points "corresponds" would move a wire somewhere nobody asked for.
    /// </summary>
    [Fact]
    public void AProfileDragSubject_SkipsASiblingThatHasNoSuchPoint()
    {
        var design = TwoGroups();
        var stub = new Wire { DiameterNm = Mil, Material = "Gold" };
        stub.Points.AddRange([new Point3(0, 40 * Mil, 4 * Mil), new Point3(100 * Mil, 40 * Mil, 1 * Mil)]);
        design.Arrays[0].Wires.Add(stub);   // two points only — no point 5, no segment 3

        var vm = new WBondViewModel(design);

        var subject = vm.ProfileGroupSubject(new WireSelection { Points = { new PointRef(0, 5) } });
        Assert.DoesNotContain(3, subject.TouchedWires());
        Assert.Equal(3, subject.Points.Count);
    }

    /// <summary>
    /// The LAYOUT view is deliberately unchanged: there each wire is drawn at its own place among the
    /// pads and a drag moves THAT wire onto THAT pad, so its members are not interchangeable.
    /// </summary>
    [Fact]
    public void ALayoutNudge_StillMovesOnlyWhatIsSelected()
    {
        var vm = new WBondViewModel(TwoGroups());
        vm.Selection = new WireSelection { Points = { new PointRef(0, 3) } };

        var siblingBefore = vm.Design.AllWires().ElementAt(1).Points.ToArray();

        vm.NudgeSelection(1, 0, coarse: false, EditorView.Layout);

        Assert.Equal(siblingBefore, vm.Design.AllWires().ElementAt(1).Points.ToArray());
    }

    /// <summary>…and the profile nudge, which is the same edit from the keyboard, promotes like the drag.</summary>
    [Fact]
    public void AProfileNudge_MovesTheWholeGroup()
    {
        var vm = new WBondViewModel(TwoGroups());
        vm.Selection = new WireSelection { Points = { new PointRef(0, 3) } };

        var before = vm.Design.AllWires().Select(w => w.Points[3]).ToArray();

        vm.NudgeSelection(0, 1, coarse: false, EditorView.Profile);

        var after = vm.Design.AllWires().Select(w => w.Points[3]).ToArray();

        for (int w = 0; w < 3; w++) Assert.NotEqual(before[w], after[w]);   // all of G1 moved
        for (int w = 3; w < 6; w++) Assert.Equal(before[w], after[w]);      // none of G2 did
    }

    /// <summary>
    /// <b>Dragging a point far enough to make a wire's XY path backtrack does not remove it from its
    /// group's envelope</b> (owner, 2026-08-18: <i>"the envelope for a wire doesn't get
    /// drawn/included in the envelope if I drag a point/segment too far away. I want the envelope
    /// rendering to always be the entire envelope for that group."</i>).
    ///
    /// <para>This is the reproduction, in the terms the user meets it: drag one interior point along
    /// the span, past its neighbour, and the wire's span stops being monotone. The band used to be
    /// keyed on exactly that test, so the wire silently left it.</para>
    /// </summary>
    [Fact]
    public void DraggingAPointPastItsNeighbour_KeepsTheWireInTheBand()
    {
        var vm = new WBondViewModel(TwoGroups());
        var array = vm.Design.Arrays[0];
        var wire = array.Wires[0];

        Assert.True(ProfileEnvelope.IsProfileEditable(wire));
        Assert.Empty(ProfileEnvelope.Build(array).NonMonotone);

        // Drag point 4 backwards along the span, past point 3 — an ordinary reshape in the profile
        // view, where the horizontal axis moves geometry along the chord.
        vm.Selection = new WireSelection { Points = { new PointRef(0, 4) } };
        WireEdits.Translate(vm.Design, new WireSelection { Points = { new PointRef(0, 4) } },
                            -60 * Mil, 0, EditorView.Profile, azimuthRadians: null);

        Assert.False(ProfileEnvelope.IsProfileEditable(wire), "the fixture must actually make it backtrack");

        var envelope = ProfileEnvelope.Build(array);

        // Reported, not excluded — and the band still brackets it.
        Assert.Contains(0, envelope.Members);
        Assert.Equal([0], envelope.NonMonotone);
        Assert.Equal(array.Wires.Count, envelope.Members.Count);

        foreach (var band in envelope.Bands)
        {
            double h = ProfileEnvelope.HeightAt(wire, band.Span);
            Assert.InRange(h, band.MinHeightNm - 1.0, band.MaxHeightNm + 1.0);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The owner's FOURTH batch of wBond changes (2026-08-16): the drag-slip fix, the render sizes,
/// wire-aware snapping in both editors, the five wBond colour themes, the port-shaped dynamic
/// symbol, and the layout half of the editor becoming a real layout editor.
///
/// <para>As in the round-2 and round-3 files, the toolbar and the context menus live in
/// <c>WBondEditorView</c>'s code-behind and are not reachable from this project. Everything they
/// DRIVE is, and that is what is pinned here.</para>
/// </summary>
public class WBondRound4Tests
{
    // ────────────────────────────────────────────────────────── fixtures

    /// <summary>An array of <paramref name="wires"/> ball-bonded wires running east, pitched in y.</summary>
    private static WBondDesign Design(int wires = 3, double pitchMils = 6.0)
    {
        long loopNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);
        var design = new WBondDesign();

        var array = new WireArray { Name = "G1" };
        for (int w = 0; w < wires; w++)
        {
            array.Wires.Add(LoopShape.CreateSeedWire(
                Point3.Mils(0, w * pitchMils, 4),
                Point3.Mils(100, w * pitchMils, 1),
                WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopHeightNm: loopNm));
        }
        design.Arrays.Add(array);

        return design;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  1. The drag slip — a degraded frame must not throw the drag away
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Motion applied while a wire is collapsed onto its chord survives the restore</b> (owner:
    /// "drag them around the screen very fast, eventually the cursor will slip and my mouse will no
    /// longer be overtop of the wire vertex I originally clicked on").
    ///
    /// <para>The quality ladder collapses a moving wire to two points when frames overrun (WB15).
    /// The drag then moves those two feet — and the restore used to put the CAPTURED array back
    /// verbatim, which threw every one of those frames away and sprang the wire back to wherever it
    /// stood at the instant the ladder stepped down. Fast dragging is what makes it appear, because
    /// the ladder only degrades when frames overrun.</para>
    /// </summary>
    [Fact]
    public void CollapsedWire_CarriesTheDragOntoItsRestoredInteriorPoints()
    {
        var wire = Design(1).AllWires().First();
        var before = wire.Points.ToArray();
        Assert.True(before.Length > 2, "The fixture must have interior points, or it proves nothing.");

        var captured = QualityLadder.CollapseToChord(wire);
        Assert.Equal(2, wire.Points.Count);

        // Two frames of an ordinary translate, applied to the chord the way WireEdits.Translate does.
        long dx = WBondUnits.ToNm(30.0, WBondUnit.Mil);
        long dy = WBondUnits.ToNm(7.0, WBondUnit.Mil);
        for (int i = 0; i < wire.Points.Count; i++)
        {
            var p = wire.Points[i];
            wire.Points[i] = new Point3(p.X + dx, p.Y + dy, p.Z);
        }

        QualityLadder.RestoreFromChord(wire, captured);

        Assert.Equal(before.Length, wire.Points.Count);
        for (int i = 0; i < before.Length; i++)
        {
            Assert.Equal(before[i].X + dx, wire.Points[i].X);
            Assert.Equal(before[i].Y + dy, wire.Points[i].Y);
            Assert.Equal(before[i].Z, wire.Points[i].Z);
        }
    }

    /// <summary>
    /// A collapse with NO drag in between is still exactly reversible, bit for bit — the property the
    /// degraded rung's whole "a solving shortcut, never an edit" contract rests on.
    /// </summary>
    [Fact]
    public void CollapsedWire_WithNoMotion_RestoresExactly()
    {
        var wire = Design(1).AllWires().First();
        var before = wire.Points.ToArray();

        QualityLadder.RestoreFromChord(wire, QualityLadder.CollapseToChord(wire));

        Assert.Equal(before, wire.Points.ToArray());
    }

    /// <summary>
    /// <b>A selection naming a point that no longer exists moves nothing rather than throwing.</b>
    ///
    /// <para>This is the other half of the slip: a selection is resolved against a point list the
    /// chord collapse then shortens, so <c>MovingPoints</c> can legitimately name point 3 of a
    /// two-point wire mid-drag. It used to index straight into the list.</para>
    /// </summary>
    [Fact]
    public void TranslatingASelectionThatOutlivedItsPoints_IsANoOpNotACrash()
    {
        var design = Design(1);
        var wire = design.AllWires().First();

        var selection = new WireSelection();
        selection.Points.Add(new PointRef(0, 4));

        QualityLadder.CollapseToChord(wire);          // now two points; index 4 is gone
        var chord = wire.Points.ToArray();

        WireEdits.Translate(design, selection, 1000, 1000, EditorView.Layout);

        Assert.Equal(chord, wire.Points.ToArray());
    }

    /// <summary>
    /// <b>A wire whose INTERIOR point is being dragged is never collapsed onto its chord</b>, however
    /// hard the ladder is degrading.
    ///
    /// <para>This is the other, worse half of the slip. A two-point wire has no interior point to
    /// move, so once collapsed the drag simply stopped: the geometry froze under a still-moving
    /// cursor, and the selection went on naming a point index the wire no longer had. The shortcut is
    /// kept exactly where it pays — a whole-wire drag still collapses — and skipped where it cannot
    /// represent the gesture.</para>
    ///
    /// <para>The budget is a nanosecond, so EVERY frame overruns and the ladder steps down on the
    /// first one. That is the point: this must hold at the most degraded the ladder can get.</para>
    /// </summary>
    [Fact]
    public void AnInteriorVertexDrag_IsNeverCollapsedOntoTheChord()
    {
        var vm = new WBondViewModel(Design(1));
        int points = vm.Design.AllWires().First().Points.Count;
        Assert.True(points > 2);

        var selection = new WireSelection();
        selection.Points.Add(new PointRef(0, points / 2));
        vm.Selection = selection;

        var controller = new WBondPointerController(vm, frameBudgetMs: 1e-9);
        controller.BeginDrag();

        long step = WBondUnits.ToNm(2.0, WBondUnit.Mil);
        long before = vm.Design.AllWires().First().Points[points / 2].Y;

        for (int frame = 0; frame < 4; frame++)
            controller.DragFrame(_ => WireEdits.Translate(vm.Design, vm.Selection, 0, step, EditorView.Layout));

        Assert.Equal(DragQuality.FreezeAndSnap, controller.Quality);   // the ladder DID degrade
        Assert.Equal(points, vm.Design.AllWires().First().Points.Count);

        controller.EndDrag();

        Assert.Equal(points, vm.Design.AllWires().First().Points.Count);
        Assert.Equal(before + 4 * step, vm.Design.AllWires().First().Points[points / 2].Y);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  2. Render sizes — +5 % on the thin line, +10 % on the vertex dot
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The thin drawing line is 5 % thicker than it was, at BOTH ends of its range: the zoom-scaled
    /// width (<c>ThinStrokeFraction</c>) and the floor a sub-pixel wire falls back to
    /// (<c>LineWidthPx</c>). Raising only one of them would make a wire 5 % thicker at some zooms and
    /// unchanged at others.
    /// </summary>
    [Fact]
    public void TheThinStroke_IsFivePercentThickerThanItWas()
    {
        Assert.Equal(1.05 / 3.0, WBondRenderer.ThinStrokeFraction, 12);
        Assert.Equal(1.5f * 1.05f, new WBondRenderTheme().LineWidthPx, 4);
    }

    /// <summary>
    /// The vertex dot is 10 % bigger — and because the DOT and its HITBOX are the same constant, both
    /// grew together. Two constants here would be the "the hitbox does not match the vertex size"
    /// report all over again.
    /// </summary>
    [Fact]
    public void TheVertexDot_IsTenPercentBiggerAndItsHitboxWithIt()
    {
        Assert.Equal(0.66, WireHitTest.VertexToWireDiameterRatio, 12);
        Assert.Equal(WireHitTest.VertexToWireDiameterRatio, WBondRenderer.VertexToWireDiameterRatio);

        long diameter = WBondUnits.ToNm(1.0, WBondUnit.Mil);
        Assert.Equal(diameter * 0.5 * 0.66, WireHitTest.VertexRadiusNm(diameter), 6);
    }

    /// <summary>
    /// Changing the line's thickness must NOT change the dot: the dot is a fraction of the wire's
    /// apparent diameter and the thin stroke is another fraction of the same quantity, so the two are
    /// independent knobs by construction. Checked well away from the pixel floor, where the two would
    /// otherwise interact.
    /// </summary>
    [Fact]
    public void TheDotIsTheSameSizeInBothThicknessModes()
    {
        var theme = new WBondRenderTheme();
        long diameter = WBondUnits.ToNm(4.0, WBondUnit.Mil);
        const double pixelsPerNm = 1e-2;   // far above the floor

        float thin = WBondRenderer.StrokeWidthPx(diameter, pixelsPerNm, theme, WireThicknessMode.Thin);
        float real = WBondRenderer.StrokeWidthPx(diameter, pixelsPerNm, theme, WireThicknessMode.TrueDiameter);

        Assert.True(thin > theme.LineWidthPx, "The fixture is on the floor; it proves nothing.");
        Assert.Equal(WBondRenderer.VertexRadiusPx(real, WireThicknessMode.TrueDiameter),
                     WBondRenderer.VertexRadiusPx(thin, WireThicknessMode.Thin), 3);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  3. Snapping to the wires themselves
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A vertex within tolerance wins, and the answer is the vertex's own coordinates.</summary>
    [Fact]
    public void WireSnap_FindsAVertex()
    {
        var design = Design(1);
        var foot = design.AllWires().First().Points[0];

        var hit = WireSnap.Nearest(design, foot.X + 200, foot.Y - 150,
                                   WBondUnits.ToNm(1.0, WBondUnit.Mil));

        Assert.Equal(WireSnapKind.Vertex, hit.Kind);
        Assert.Equal(foot.X, hit.XNm);
        Assert.Equal(foot.Y, hit.YNm);
    }

    /// <summary>
    /// <b>A vertex outranks a segment even when the segment is nearer.</b> The layout engine's own
    /// priority order (corner/endpoint above nearest-on-edge) applied to wires: reaching near the end
    /// of a wire means its end, not the line an eyelash away from it.
    /// </summary>
    [Fact]
    public void WireSnap_PrefersAVertexOverASegmentItIsCloserTo()
    {
        var design = new WBondDesign();
        var array = new WireArray { Name = "G1" };
        array.Wires.Add(new Wire { Points = { Point3.Mils(0, 0, 0), Point3.Mils(100, 0, 0) } });
        design.Arrays.Add(array);

        // Directly above the input foot: the SEGMENT is 1 mil away, the vertex 1 mil away too, but
        // one mil further along it the segment is nearer and the vertex must still win.
        long tol = WBondUnits.ToNm(5.0, WBondUnit.Mil);
        var hit = WireSnap.Nearest(design, WBondUnits.ToNm(2.0, WBondUnit.Mil),
                                           WBondUnits.ToNm(1.0, WBondUnit.Mil), tol);

        Assert.Equal(WireSnapKind.Vertex, hit.Kind);
        Assert.Equal(0, hit.XNm);
    }

    /// <summary>
    /// A point beyond every vertex's reach lands on the SEGMENT, at its own perpendicular foot —
    /// clamped to the segment's ends, never off in space.
    /// </summary>
    [Fact]
    public void WireSnap_ProjectsOntoASegment()
    {
        var design = new WBondDesign();
        var array = new WireArray { Name = "G1" };
        array.Wires.Add(new Wire { Points = { Point3.Mils(0, 0, 0), Point3.Mils(100, 0, 0) } });
        design.Arrays.Add(array);

        var hit = WireSnap.Nearest(design, WBondUnits.ToNm(50.0, WBondUnit.Mil),
                                           WBondUnits.ToNm(1.0, WBondUnit.Mil),
                                           WBondUnits.ToNm(3.0, WBondUnit.Mil));

        Assert.Equal(WireSnapKind.Segment, hit.Kind);
        Assert.Equal(WBondUnits.ToNm(50.0, WBondUnit.Mil), hit.XNm);
        Assert.Equal(0, hit.YNm);
    }

    /// <summary>
    /// <b>A wire the gesture is dragging is excluded.</b> Without it a dragged wire's own vertices sit
    /// at distance zero from themselves, every frame snaps the point back onto where it already is,
    /// and the wire cannot be moved at all.
    /// </summary>
    [Fact]
    public void WireSnap_ExcludesTheWiresBeingDragged()
    {
        var design = Design(2);
        var foot = design.AllWires().First().Points[0];
        long tol = WBondUnits.ToNm(1.0, WBondUnit.Mil);

        Assert.True(WireSnap.Nearest(design, foot.X, foot.Y, tol).Found);
        Assert.False(WireSnap.Nearest(design, foot.X, foot.Y, tol, w => w == 0).Found);
    }

    /// <summary>
    /// <c>WBondSnap</c> — the wBond editor's own snap — reaches the wires even with NO reference
    /// layout at all, which is §10's third entry point before any geometry has been dragged in.
    /// </summary>
    [Fact]
    public void WBondSnap_SnapsToWires_WithNoReferenceLayout()
    {
        var design = Design(1);
        var foot = design.AllWires().First().Points[^1];

        var snap = WBondSnap.Snap(view: null, tech: null, baseDir: null,
                                  foot.X + 300, foot.Y + 300, WBondUnits.ToNm(2.0, WBondUnit.Mil),
                                  includeIntersections: false, wires: design);

        Assert.True(snap.Snapped);
        Assert.Equal(SnapFeatureKind.CornerEndpoint, snap.Kind);
        Assert.Equal(foot.X, snap.XNm);
        Assert.Equal(foot.Y, snap.YNm);
    }

    /// <summary>With no wires supplied it behaves exactly as it did before — layout geometry only.</summary>
    [Fact]
    public void WBondSnap_WithNoWires_IsUnchanged()
    {
        var design = Design(1);
        var foot = design.AllWires().First().Points[0];

        var snap = WBondSnap.Snap(view: null, tech: null, baseDir: null,
                                  foot.X, foot.Y, WBondUnits.ToNm(2.0, WBondUnit.Mil));

        Assert.False(snap.Snapped);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  4. The layout underneath keeps its own gestures
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A press on a layout SHAPE is declined by the wire overlay</b>, so the layout editor
    /// underneath gets it and can select and move the thing that was clicked (owner: "wBond editor has
    /// no way to move cell instances in the layout view once they are placed").
    ///
    /// <para>The wire marquee used to take every press that missed a wire — including one squarely on
    /// a pad or an instance — so the only way through was to turn the marquee toggle off. It still
    /// decides who gets a press on genuinely EMPTY space, which is the real ambiguity it exists
    /// for.</para>
    /// </summary>
    [Fact]
    public void APressOnLayoutGeometry_IsDeclinedSoTheLayoutEditorCanHaveIt()
    {
        var vm = new WBondViewModel(Design(1));
        var overlay = new WBondLayoutOverlay(vm, frameBudgetMs: 1e9);

        var view = new LayoutView();
        view.Shapes.Add(new RectShape
        {
            Layer = new LayerKey(1, 0),
            X1 = 500_000, Y1 = 500_000, X2 = 700_000, Y2 = 700_000,
        });
        overlay.ReferenceLayout = view;

        // Dead centre of the rectangle, well clear of every wire.
        Assert.False(overlay.OnPointerPressed(600_000, 600_000, 500, KeyModifiers.None, 1));

        // ...and empty space is still the wire marquee's, which is what the toggle is for.
        Assert.True(overlay.OnPointerPressed(9_000_000, 9_000_000, 500, KeyModifiers.None, 1));
    }

    /// <summary>
    /// <b>An armed LAYOUT tool takes the canvas outright.</b> The overlay is offered every press
    /// first, so without this arming Rectangle in the second toolbar row would start a wire marquee
    /// and the tool would appear to do nothing.
    /// </summary>
    [Fact]
    public void AnArmedLayoutTool_TakesEveryPress()
    {
        var vm = new WBondViewModel(Design(1));
        var overlay = new WBondLayoutOverlay(vm, frameBudgetMs: 1e9);

        Assert.True(overlay.OnPointerPressed(9_000_000, 9_000_000, 500, KeyModifiers.None, 1));

        overlay.LayoutToolArmed = () => true;
        Assert.False(overlay.OnPointerPressed(9_000_000, 9_000_000, 500, KeyModifiers.None, 1));

        // Including a press that lands ON a wire — a rectangle started over a bond wire is ordinary.
        var foot = vm.Design.AllWires().First().Points[0];
        Assert.False(overlay.OnPointerPressed(WBondSnap.ToDbu(foot.X, 1000),
                                              WBondSnap.ToDbu(foot.Y, 1000), 500, KeyModifiers.None, 1));
    }

    /// <summary>
    /// The wBond editor's scratch reference layout resolves a WORKSPACE, which is what a generated
    /// PCell cell needs somewhere to live (owner: "can't drag and drop PCells from the Library palette
    /// into the layout view"). Its own path is under the session scratch directory, outside any
    /// workspace, so the ancestor-<c>.cws</c> walk finds nothing and the host's answer has to be used
    /// instead — which is what <c>IsScratchSurface</c> says.
    /// </summary>
    [Fact]
    public void AScratchSurfaceLayout_ResolvesTheHostsWorkspace()
    {
        string root = Path.Combine(Path.GetTempPath(), "crf-wb4-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "scratch"));

        var vm = new LayoutEditorViewModel(new LayoutView())
        {
            CurrentLayoutPath = Path.Combine(root, "scratch", "reference.clay"),
        };
        vm.FallbackWorkspaceTechDir = Path.Combine(root, "ws", "tech");

        // Not marked: a loose document outside any workspace is genuinely foreign and stays so.
        Assert.Null(vm.WorkspaceRootDir);

        vm.IsScratchSurface = true;
        Assert.Equal(Path.Combine(root, "ws"), vm.WorkspaceRootDir);

        Directory.Delete(root, recursive: true);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  5. The wBond colours
    //
    //  Six wBond-… themes shipped here originally, to be judged side by side. The judging is over
    //  (owner, 2026-08-17): the winner's six wBond.* roles were folded into Default itself — both the
    //  shipped Default.ccolor and the in-code ColorTheme.BuiltIn — and all six files were deleted.
    //  So these tests hold the ROLES, wherever they live, rather than a theme that can be selected.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>ONE theme ships, it is called Default, and no <c>wBond-…</c> theme is offered any more</b>
    /// (owner, 2026-08-17).
    ///
    /// <para>A name still in the picker with no asset behind it resolves silently to
    /// <c>ColorTheme.BuiltIn</c> — a theme that appears to do nothing — so the list and the folder
    /// have to agree, in both directions.</para>
    /// </summary>
    [Fact]
    public void OnlyTheDefaultThemeShips()
    {
        var names = ThemeResolver.DiscoverThemeNames();

        foreach (string builtIn in ThemeResolver.BuiltInThemeNames)
            Assert.Contains(builtIn, names, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(["Default"], ThemeResolver.BuiltInThemeNames);
        Assert.Equal("Default", ThemeResolver.DefaultThemeName);

        string dir = Path.Combine(RepoRoot(), "src", "Ui", "Assets", "Color");
        Assert.Equal(["Default.ccolor"],
                     Directory.GetFiles(dir, "*.ccolor").Select(Path.GetFileName).Order());
    }

    /// <summary>
    /// <b>A built-in theme's name must carry no space</b>, and every one must have a file behind it.
    ///
    /// <para>A built-in is fetched as <c>avares://…/Assets/Color/&lt;name&gt;.ccolor</c>, and
    /// <see cref="Uri"/> percent-escapes a space before the asset loader is ever reached — so
    /// "wBond Copper" would be looked up as "wBond%20Copper", find nothing, and fall silently back to
    /// the built-in default. Silently, which is why this is a test rather than a comment.</para>
    /// </summary>
    [Fact]
    public void EveryBuiltInThemeName_IsUriSafeAndHasAFileBehindIt()
    {
        string dir = Path.Combine(RepoRoot(), "src", "Ui", "Assets", "Color");

        foreach (string name in ThemeResolver.BuiltInThemeNames)
        {
            Assert.DoesNotContain(' ', name);
            Assert.True(File.Exists(Path.Combine(dir, name + ".ccolor")),
                        $"{name} is offered in the picker but ships no .ccolor.");
        }
    }

    /// <summary>
    /// <b>The shipped <c>Default.ccolor</c> and the in-code <c>ColorTheme.BuiltIn</c> state the same
    /// six <c>wBond.*</c> colours, in both variants.</b>
    ///
    /// <para>They are two copies of one palette — the file is what the Settings editor shows and what
    /// a user can copy; the in-code one is the fallback for every role a theme leaves unsaid. The
    /// orchid colours were folded into both on 2026-08-17, and a divergence would mean the wires drew
    /// one colour and the theme editor listed another.</para>
    /// </summary>
    [Fact]
    public void TheDefaultThemeFileAndTheInCodeDefault_AgreeOnEveryWBondRole()
    {
        var file = ColorThemeIo.LoadFile(
            Path.Combine(RepoRoot(), "src", "Ui", "Assets", "Color", "Default.ccolor"));
        Assert.Equal("Default", file.Name);

        string[] roles =
        [
            ColorRole.WBondWire, ColorRole.WBondWireStart, ColorRole.WBondWireVertex,
            ColorRole.WBondSelected, ColorRole.WBondEnvelope,
        ];

        var (light, dark) = file.GetRoleMaps();
        foreach (var (map, variant) in new[] { (light, ColorVariant.Light), (dark, ColorVariant.Dark) })
            foreach (string role in roles)
            {
                Assert.True(map.ContainsKey(role), $"Default.ccolor is missing {role} ({variant}).");
                Assert.Equal(ColorTheme.BuiltIn.Resolve(role, variant), file.Resolve(role, variant));
            }

        // The orchid the owner picked, spot-checked by value so a silent revert to the old gold is a
        // failure rather than a colour nobody notices.
        Assert.Equal(new Rgba(165, 64, 130), file.Resolve(ColorRole.WBondWire, ColorVariant.Light));
        Assert.Equal(new Rgba(214, 122, 182), file.Resolve(ColorRole.WBondWire, ColorVariant.Dark));
    }

    /// <summary>
    /// <b>The vertex dot must be findable on the wire it sits on, and stay ADJACENT to it on the
    /// colour wheel rather than opposite.</b>
    ///
    /// <para>The bound is measured, not chosen: <c>Schematic.Wire</c> and
    /// <c>Schematic.WireJunctionDot</c> — the pairing the owner named as working — sit <b>72° and
    /// 75° apart</b> in the light and dark variants, with the dot about twice as saturated. The
    /// first set of wBond themes used the COMPLEMENT (180°) and the verdict was that the accent was
    /// "too far different than the wBond.Wire colour".</para>
    ///
    /// <para><b>Two-sided on purpose.</b> A one-sided "different enough to see" test is exactly what
    /// let the complementary set through; a one-sided "close enough to match" test would let through
    /// a dot invisible on its own wire.</para>
    ///
    /// <para><c>wBond.Selected</c> is deliberately NOT held to the hue bound: it is a state, not an
    /// accent, and it has to be unmistakable against both the wire and the canvas.</para>
    /// </summary>
    [Fact]
    public void TheWBondVertex_StaysAdjacentToItsWire()
    {
        const string name = "Default";
        var theme = ColorThemeIo.LoadFile(
            Path.Combine(RepoRoot(), "src", "Ui", "Assets", "Color", name + ".ccolor"));

        foreach (var variant in new[] { ColorVariant.Light, ColorVariant.Dark })
        {
            var wire = theme.Resolve(ColorRole.WBondWire, variant);
            var vertex = theme.Resolve(ColorRole.WBondWireVertex, variant);
            var selected = theme.Resolve(ColorRole.WBondSelected, variant);

            Assert.True(Distance(wire, vertex) > 60,
                        $"{name}/{variant}: the vertex blends into the wire (d={Distance(wire, vertex):F0}).");
            Assert.InRange(HueGap(wire, vertex), 40.0, 110.0);

            Assert.True(Distance(wire, selected) > 100,
                        $"{name}/{variant}: selection is not visible on a wire.");
            Assert.True(Distance(vertex, selected) > 100,
                        $"{name}/{variant}: selection is not visible on a vertex dot.");
        }

        static double Distance(Rgba a, Rgba b)
        {
            double dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
            return Math.Sqrt(dr * dr + dg * dg + db * db);
        }

        // Degrees around the colour wheel, the short way. A grey has no hue and is reported as 0 —
        // no theme here is grey, and a bound that a grey passes trivially would say nothing anyway.
        static double HueGap(Rgba a, Rgba b)
        {
            double gap = Math.Abs(Hue(a) - Hue(b)) % 360.0;
            return gap > 180.0 ? 360.0 - gap : gap;
        }

        static double Hue(Rgba c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            if (d <= 0.0) return 0.0;

            double h = max == r ? (g - b) / d % 6.0
                     : max == g ? (b - r) / d + 2.0
                     : (r - g) / d + 4.0;

            return (h * 60.0 + 360.0) % 360.0;
        }
    }

    /// <summary>The renderer projects the six wBond roles, so the theme's colours actually draw.</summary>
    [Fact]
    public void TheWBondRoles_ReachTheRenderer()
    {
        var theme = ColorThemeIo.LoadFile(
            Path.Combine(RepoRoot(), "src", "Ui", "Assets", "Color", "Default.ccolor"));

        var projected = WBondRenderTheme.FromTheme(theme, ColorVariant.Dark);
        var expected = theme.Resolve(ColorRole.WBondWireVertex, ColorVariant.Dark);

        Assert.Equal(expected.R, projected.Vertex.Red);
        Assert.Equal(expected.G, projected.Vertex.Green);
        Assert.Equal(expected.B, projected.Vertex.Blue);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  6. The dynamic symbol
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The pitch is carried in the symbol REFERENCE</b>, first field and positionally, so no array
    /// name can be mistaken for it — an array may legitimately be called "Tight".
    /// </summary>
    [Fact]
    public void TheSymbolReference_CarriesThePitch_AndAnArrayMayShareItsName()
    {
        var design = new WBondDesign();
        design.Arrays.Add(new WireArray { Name = "Tight" });
        design.Arrays.Add(new WireArray { Name = "G2" });

        string payload = WBondEmbedding.Encode(design);

        string loose = WBondSymbolProvider.RefFor(payload);
        string tight = WBondSymbolProvider.RefFor(payload, WBondSymbolPitch.Tight);
        Assert.NotEqual(loose, tight);

        var resolvedLoose = WBondSymbolProvider.Resolve(loose, null);
        var resolvedTight = WBondSymbolProvider.Resolve(tight, null);

        Assert.Equal(CellSymbolState.Resolved, resolvedLoose.State);
        Assert.Equal(CellSymbolState.Resolved, resolvedTight.State);

        // Two arrays either way — the array called "Tight" was not eaten by the leading fields.
        Assert.Equal(4, resolvedLoose.Symbol!.Pins.Count);   // 2 per array; REF is off by default
        Assert.Equal(4, resolvedTight.Symbol!.Pins.Count);
    }

    /// <summary>
    /// Tight halves the row spacing and Loose is the shipped geometry — SnP's own two values, meaning
    /// the same two things.
    /// </summary>
    [Fact]
    public void TightPitch_HalvesTheRowSpacing()
    {
        string[] arrays = ["G1", "G2", "G3"];

        double loose = RowSpacing(WBondSymbolGenerator.Build(arrays, WBondSymbolPitch.Loose)!);
        double tight = RowSpacing(WBondSymbolGenerator.Build(arrays, WBondSymbolPitch.Tight)!);

        Assert.Equal(200.0, loose, 6);
        Assert.Equal(100.0, tight, 6);

        static double RowSpacing(Symbol symbol)
        {
            var lefts = symbol.Pins.Where(p => p.LocalX < 0).OrderBy(p => p.LocalY).ToList();
            return lefts[1].LocalY - lefts[0].LocalY;
        }
    }

    /// <summary>
    /// <b>Every port row reads <c>+ &lt;name&gt; −</c>, left to right</b> (owner) — the plus on the
    /// left lead's side, the minus on the right's, and the array's own name between them. Before this
    /// the body was an empty box and nothing on the schematic named a lead at all.
    /// </summary>
    [Fact]
    public void EachPortRow_IsLabelledPlusNameMinus()
    {
        var symbol = WBondSymbolGenerator.Build(["G1", "D1"])!;
        var text = symbol.Primitives.OfType<TextPrimitive>().ToList();

        foreach (string name in new[] { "G1", "D1" })
        {
            var label = Assert.Single(text, t => t.Content == name);

            var plus = text.Where(t => t.Content == "+" && Math.Abs(t.AnchorY - label.AnchorY) < 1e-9);
            var minus = text.Where(t => t.Content == "−" && Math.Abs(t.AnchorY - label.AnchorY) < 1e-9);

            Assert.True(Assert.Single(plus).AnchorX < label.AnchorX, $"{name}: + is not on the left.");
            Assert.True(Assert.Single(minus).AnchorX > label.AnchorX, $"{name}: − is not on the right.");
        }
    }

    /// <summary>
    /// The body grows sideways for a long array name, so a label never runs out over its own leads —
    /// and the pins move with it rather than sitting inside the box.
    ///
    /// <para>The name here is deliberately absurd. Ordinary array names (<c>G1</c>, <c>D1</c>,
    /// <c>MT</c>) are far inside the minimum body width, so the floor governs and the widening never
    /// binds — which is the point: this pins the ONE case where it does, without asserting anything
    /// about where a normal symbol's pins sit.</para>
    /// </summary>
    [Fact]
    public void TheBodyWidens_ForALongArrayName()
    {
        var narrow = WBondSymbolGenerator.Build(["G1"])!;
        var wide = WBondSymbolGenerator.Build(["GateSideMatchingNetworkBondArrayNumberOne"])!;

        double narrowLead = narrow.Pins[0].LocalX;
        double wideLead = wide.Pins[0].LocalX;

        Assert.True(wideLead < narrowLead,
                    $"The long-named symbol's pin is at {wideLead}, no further out than {narrowLead}.");
    }

    /// <summary>
    /// <b>The pin NAMES still match the model's terminal names exactly</b>, unchanged by any of this.
    /// The drawn label is what says <c>+</c>; the identity underneath is what keeps pin <i>k</i> and
    /// node <i>k</i> from drifting into a correctly-labelled pin wired to the wrong net — and it is
    /// also what a measurement path spells.
    /// </summary>
    [Fact]
    public void ThePinNames_AreStillTheModelsTerminalNames()
    {
        // A real design: the model refuses an empty array, and rightly — it makes the array-basis
        // inductance singular.
        var design = Design(1);
        design.Arrays[0].Name = "G1";
        design.Arrays.Add(new WireArray
        {
            Name = "D1",
            Wires = { new Wire { Points = { Point3.Mils(0, 40, 4), Point3.Mils(100, 40, 1) } } },
        });

        var symbol = WBondSymbolGenerator.Build(design)!;
        var model = new CircuitRF.Core.Devices.WBondModel(design);

        Assert.Equal(model.TerminalNames, symbol.Pins.Select(p => p.Name).ToArray());
    }

    /// <summary>Changing the pitch changes the content key, so a cached symbol is not reused.</summary>
    [Fact]
    public void ThePitch_ChangesTheContentKey()
    {
        var design = new WBondDesign();
        design.Arrays.Add(new WireArray { Name = "G1" });

        Assert.NotEqual(WBondSymbolGenerator.ContentKey(design, WBondSymbolPitch.Loose),
                        WBondSymbolGenerator.ContentKey(design, WBondSymbolPitch.Tight));
    }

    /// <summary>Anything unset or unrecognised means Loose — artwork can never be a reason not to draw.</summary>
    [Theory]
    [InlineData(null, WBondSymbolPitch.Loose)]
    [InlineData("", WBondSymbolPitch.Loose)]
    [InlineData("nonsense", WBondSymbolPitch.Loose)]
    [InlineData("tight", WBondSymbolPitch.Tight)]
    [InlineData("Tight", WBondSymbolPitch.Tight)]
    [InlineData("Loose", WBondSymbolPitch.Loose)]
    public void ParsePitch_FallsBackToLoose(string? text, WBondSymbolPitch expected) =>
        Assert.Equal(expected, WBondSymbolProvider.ParsePitch(text));

    /// <summary>
    /// A placed wBond arrives with a <c>SymbolPitch</c> parameter, and it is ARTWORK — filtered out of
    /// the extracted netlist exactly as <c>Arrays</c> is.
    ///
    /// <para>Named <c>SymbolPitch</c> rather than SnP's bare <c>Pitch</c> (owner, 2026-08-16): on a
    /// wirebond component "pitch" reads as the WIRE pitch, which this is not.</para>
    /// </summary>
    [Fact]
    public void APlacedWBond_CarriesASymbolPitchParameter()
    {
        var comp = WBondPlacement.BuildCarrying(Design(1), "W1");
        var pitch = Assert.Single(comp.Parameters, p => p.Name == "SymbolPitch");

        Assert.Equal(nameof(WBondSymbolPitch.Loose), pitch.Expression);
        Assert.Contains("Loose", comp.ExternalSymbolRef);

        pitch.Expression = nameof(WBondSymbolPitch.Tight);
        Assert.Contains("Tight", comp.ExternalSymbolRef);
    }

    // ────────────────────────────────────────────────────────── helpers

    /// <summary>The repository root, walked up from the test assembly's own location.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

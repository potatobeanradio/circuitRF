using System;
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
/// The owner's third batch of wBond changes (2026-08-16): the XZ/YZ spelling, user-editable wBond
/// colour themes, the paste-pitch fix, the three delete commands, "Group Wires As…", the profile
/// view's selection-independent visibility, and the new default wire.
///
/// <para>As in the round-2 file, the toolbar and the context menus themselves live in
/// <c>WBondEditorView</c>'s code-behind and are not reachable from this project. What IS reachable is
/// every rule they drive — the commands, the palettes, the placement arithmetic and the render — and
/// that is what is pinned here.</para>
/// </summary>
public class WBondRound3Tests
{
    /// <summary>
    /// An array of <paramref name="wires"/> wires on one ball-bond profile, running east/west and
    /// pitched in y — the same fixture shape the round-2 file uses.
    /// </summary>
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

    // ────────────────────────────────────────────────────────── the XZ / YZ spelling

    /// <summary>
    /// <b>Every wBond surface says XZ and YZ, without the hyphen</b> (owner) — and the hyphenated
    /// spellings a saved file or a habit may still carry are read as the same plane and shown back in
    /// the current spelling. A rename that stopped accepting the old text would silently reset the
    /// plane of every design saved before it.
    /// </summary>
    [Theory]
    [InlineData("XZ",  0.0,  "XZ")]
    [InlineData("X-Z", 0.0,  "XZ")]
    [InlineData("x z", 0.0,  "XZ")]
    [InlineData("YZ",  90.0, "YZ")]
    [InlineData("Y-Z", 90.0, "YZ")]
    [InlineData("90",  90.0, "YZ")]
    public void ThePlaneLabels_DropTheHyphenAndStillReadTheOldSpelling(
        string typed, double expectedDegrees, string shownBack)
    {
        Assert.True(ProfileAxisSetting.TryParse(typed, out double? radians));
        Assert.Equal(expectedDegrees, radians!.Value * 180.0 / Math.PI, 9);
        Assert.Equal(shownBack, ProfileAxisSetting.Format(radians));
    }

    /// <summary>The picker's own presets are the new spelling — the list the combo shows.</summary>
    [Fact]
    public void ThePlanePresets_UseTheNewSpelling()
    {
        Assert.Equal(["Auto", "XZ", "YZ"], ProfileAxisSetting.Presets);
        Assert.DoesNotContain(ProfileAxisSetting.Presets, p => p.Contains('-'));
    }

    // ────────────────────────────────────────────────────────── the default wire

    /// <summary>
    /// <b>The shipped wire runs north/south over 30 mil</b> (owner) — it used to run east/west over
    /// 100. Asserted on the design itself rather than on the constants, so a change that edits one
    /// constant and leaves the other behind is caught.
    /// </summary>
    [Fact]
    public void TheDefaultWire_RunsNorthSouthOverThirtyMils()
    {
        var wire = WBondEmbedding.DefaultDesign().AllWires().Single();

        var start = wire.Points[0];
        var end = wire.Points[^1];

        Assert.Equal(start.X, end.X);                                     // no east/west extent at all
        Assert.True(end.Y > start.Y, "the shipped wire must run north");

        double spanMils = WBondUnits.FromNm(end.Y - start.Y, WBondUnit.Mil);
        Assert.Equal(30.0, spanMils, 6);
    }

    /// <summary>
    /// <b>The feet land at the SAME height, 4 mil</b> (owner, 2026-08-17). This test previously
    /// asserted the opposite — the shipped wire descended from a 4 mil die pad to a 1 mil package
    /// lead — on the reasoning that a level wire would make §6.2's scale-about-the-chord rule
    /// untestable from the shipped document.
    ///
    /// <para><b>That reasoning was the wrong way round and is retired.</b> A fixture is not the
    /// shipped default's job: the asymmetric-foot case is exercised where it belongs, by tests that
    /// build their own wires at 4 mil → 1 mil (<c>WBondEditorRound2Tests.Design</c> and
    /// <c>WBondRound6Tests.Design</c>, whose alt-drag cases turn on exactly that asymmetry). What the
    /// shipped wire has to be is the shape a user reads as neutral, and a drop they did not ask for is
    /// something to notice and undo.</para>
    /// </summary>
    [Fact]
    public void TheDefaultWire_KeepsItsFeetLevelAtFourMils()
    {
        var wire = WBondEmbedding.DefaultDesign().AllWires().Single();

        Assert.Equal(wire.Points[0].Z, wire.Points[^1].Z);
        Assert.Equal(4.0, WBondUnits.FromNm(wire.Points[0].Z, WBondUnit.Mil), 6);
        Assert.Equal(4.0, WBondUnits.FromNm(wire.Points[^1].Z, WBondUnit.Mil), 6);

        // The loop still carries its height ABOVE the feet — a level wire must not be read as a flat
        // one, which would have no inductance story at all.
        Assert.True(wire.Points.Max(p => p.Z) > wire.Points[0].Z);
    }

    // ────────────────────────────────────────────────────────── the default profile plane

    /// <summary>
    /// <b>A new document opens on YZ</b> (owner), which is the plane that shows the north/south
    /// default wire side-on rather than foreshortened to nothing.
    /// </summary>
    [Fact]
    public void ANewEditor_OpensOnTheYzPlane()
    {
        var vm = new WBondViewModel(Design(wires: 1));
        Assert.Equal("YZ", vm.ProfileAxisText);
    }

    // ────────────────────────────────────────────────────────── colour theme

    /// <summary>
    /// <b>The five wBond roles exist in the shared vocabulary and in BOTH built-in variants.</b> That
    /// is what makes them editable in the Settings dialog at all: its role list is
    /// <see cref="ColorRole.All"/>, so a role missing from it is a colour nobody can reach.
    /// </summary>
    [Fact]
    public void TheWBondRoles_AreInTheSharedVocabularyAndBothVariants()
    {
        string[] roles =
        [
            ColorRole.WBondWire, ColorRole.WBondWireStart, ColorRole.WBondSelected,
            ColorRole.WBondEnvelope,
        ];

        var (light, dark) = ColorTheme.BuiltIn.GetRoleMaps();

        foreach (string role in roles)
        {
            Assert.Contains(role, ColorRole.All);
            Assert.True(light.ContainsKey(role), $"{role} has no light default");
            Assert.True(dark.ContainsKey(role), $"{role} has no dark default");
        }
    }

    /// <summary>
    /// <b>Every role key carries its family prefix, and wBond's starts with a lowercase w</b> (owner,
    /// 2026-08-16) — the product is "wBond", not "WBond", and the Settings list shows role keys
    /// verbatim, so the key IS the label.
    /// </summary>
    [Fact]
    public void EveryRoleKey_IsPrefixedAndWBondSpellsItselfWithALowercaseW()
    {
        foreach (string role in ColorRole.All)
            Assert.Contains('.', role);

        Assert.Equal("wBond.Wire", ColorRole.WBondWire);
        Assert.Equal("wBond.WireStart", ColorRole.WBondWireStart);
        Assert.Equal("wBond.Selected", ColorRole.WBondSelected);
        Assert.Equal("wBond.Envelope", ColorRole.WBondEnvelope);

        Assert.DoesNotContain(ColorRole.All, r => r.StartsWith("WBond.", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>A theme file written before the rename still loads.</b> No backwards compatibility was asked
    /// for and none is offered — the stale <c>WBond.*</c> entries simply match no role and the built-in
    /// defaults answer instead. What IS required is that reading one cannot throw.
    /// </summary>
    [Fact]
    public void AThemeFileWithStaleRoleKeys_LoadsAndFallsBackRatherThanThrowing()
    {
        string json = ColorThemeIo.Save(new ColorTheme(
            "Old",
            new Dictionary<string, Rgba> { ["WBond.Wire"] = new(1, 2, 3), ["Schematic.Wire"] = new(4, 5, 6) },
            new Dictionary<string, Rgba> { ["WBond.Wire"] = new(7, 8, 9) }));

        var theme = ColorThemeIo.Load(json);

        // The stale key is ignored; the built-in default answers for the role that exists now.
        Assert.Equal(ColorTheme.BuiltIn.Resolve(ColorRole.WBondWire, ColorVariant.Light),
                     theme.Resolve(ColorRole.WBondWire, ColorVariant.Light));

        // …and a key that DID survive the rename is still honoured, so this is a fallback, not a wipe.
        Assert.Equal(new Rgba(4, 5, 6), theme.Resolve(ColorRole.SchematicWire, ColorVariant.Light));
    }

    /// <summary>
    /// <b>The renderer theme is a projection of the active theme, not a constant.</b> Before this
    /// round both wBond canvases drew one hardcoded palette in light and dark alike — which is not a
    /// tuning problem but a wiring one, and the reason the light-mode selection accent was white.
    /// </summary>
    [Fact]
    public void TheWireTheme_DiffersBetweenTheTwoVariants()
    {
        var light = WBondRenderTheme.FromTheme(ColorTheme.BuiltIn, ColorVariant.Light);
        var dark = WBondRenderTheme.FromTheme(ColorTheme.BuiltIn, ColorVariant.Dark);

        Assert.NotEqual(light.Wire, dark.Wire);
        Assert.NotEqual(light.Selected, dark.Selected);
    }

    /// <summary>
    /// <b>The light selection accent is DARK</b> (owner: "in light mode the selection highlighting
    /// colour is currently too light and can't be seen over the canvas background"). Measured as
    /// perceived luminance against the light canvas it is drawn on — <c>Layout.Background</c> — since
    /// "visible" is a contrast statement, not a colour-name one.
    /// </summary>
    [Fact]
    public void TheLightSelectionAccent_ContrastsWithTheLightCanvas()
    {
        var theme = WBondRenderTheme.FromTheme(ColorTheme.BuiltIn, ColorVariant.Light);
        var canvas = ColorTheme.BuiltIn.Resolve(ColorRole.LayoutBackground, ColorVariant.Light);

        double accent = Luminance(theme.Selected.Red, theme.Selected.Green, theme.Selected.Blue);
        double ground = Luminance(canvas.R, canvas.G, canvas.B);

        Assert.True(ground - accent > 0.4,
                    $"the light selection accent must be much darker than the canvas; {accent:F2} vs {ground:F2}");
    }

    /// <summary>
    /// <b>The start-of-wire colour is the wire's own, much darker</b> — the owner's stated default.
    /// Same hue family (each channel no brighter than the wire's) and visibly darker overall, in both
    /// variants, so "which end is the input" reads as the same wire rather than a second object. It
    /// matters beyond looks: the sign of every mutual depends on which foot is the input (WB3).
    /// </summary>
    [Theory]
    [InlineData(ColorVariant.Light)]
    [InlineData(ColorVariant.Dark)]
    public void TheWireStartColour_IsTheWireColourMuchDarker(ColorVariant variant)
    {
        var theme = WBondRenderTheme.FromTheme(ColorTheme.BuiltIn, variant);

        Assert.True(theme.InputEnd.Red <= theme.Wire.Red, "start must not be redder than the wire");
        Assert.True(theme.InputEnd.Green <= theme.Wire.Green, "start must not be greener than the wire");
        Assert.True(theme.InputEnd.Blue <= theme.Wire.Blue, "start must not be bluer than the wire");

        double wire = Luminance(theme.Wire.Red, theme.Wire.Green, theme.Wire.Blue);
        double start = Luminance(theme.InputEnd.Red, theme.InputEnd.Green, theme.InputEnd.Blue);

        Assert.True(wire - start > 0.15, $"start must be MUCH darker; {start:F2} vs {wire:F2}");
    }

    /// <summary>Perceived luminance, 0..1 — the Rec. 601 weighting, which is what "darker" means to an eye.</summary>
    private static double Luminance(byte r, byte g, byte b) =>
        (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;

    // ────────────────────────────────────────────────────────── paste pitch

    /// <summary>
    /// <b>Pasting the same clipboard twice makes two wires, not one and an error.</b> The owner's
    /// report: the second paste landed exactly on the first, and two wires on identical geometry make
    /// the inductance matrix singular — so the refusal fired and no third wire appeared.
    /// </summary>
    [Fact]
    public void PastingTheSameClipboardTwice_AddsTwoDistinctWires()
    {
        var vm = new WBondViewModel(Design(wires: 1));
        vm.SelectAllWires();

        string clipboard = vm.CopySelection()!;
        long pitch = WBondUnits.ToNm(5.0, WBondUnit.Mil);

        string? refusal = null;
        vm.EditRefused += r => refusal = r;

        Assert.Equal(1, vm.PasteWiresAtFreePitch(clipboard, pitch));
        Assert.Equal(1, vm.PasteWiresAtFreePitch(clipboard, pitch));

        Assert.Null(refusal);
        Assert.Equal(3, vm.Design.WireCount);

        // …and the three land on three distinct y positions, one pitch apart.
        var ys = vm.Design.AllWires().Select(w => w.Points[0].Y).OrderBy(y => y).ToList();
        Assert.Equal(3, ys.Distinct().Count());
        Assert.Equal(pitch, ys[1] - ys[0]);
        Assert.Equal(pitch, ys[2] - ys[1]);
    }

    /// <summary>
    /// The offset STEPS over what is already there rather than being a fixed multiple of the paste
    /// count — so a paste into a design that already has wires at one and two pitches lands at three.
    /// </summary>
    [Fact]
    public void ThePasteOffset_StepsPastOccupiedPositions()
    {
        long pitch = WBondUnits.ToNm(5.0, WBondUnit.Mil);

        // Three wires already at y = 0, 5 and 10 mil — i.e. at 0, 1 and 2 pitches.
        var vm = new WBondViewModel(Design(wires: 3, pitchMils: 5.0));

        vm.Selection = new WireSelection { Wires = { 0 } };
        var payload = WBondClipboard.TryParse(vm.CopySelection())!;

        Assert.Equal((0L, pitch * 3), vm.FreePasteOffset(payload, pitch));
    }

    /// <summary>
    /// <b>The pitch never re-spaces the clipboard's own wires</b> — the owner's explicit distinction.
    /// A copied pair 6 mil apart pastes as a pair 6 mil apart, whatever the paste pitch is.
    /// </summary>
    [Fact]
    public void ThePastePitch_DoesNotRespaceTheCopiedWires()
    {
        var vm = new WBondViewModel(Design(wires: 2, pitchMils: 6.0));
        vm.SelectAllWires();

        string clipboard = vm.CopySelection()!;
        long spacingBefore = vm.Design.AllWires().ElementAt(1).Points[0].Y
                           - vm.Design.AllWires().ElementAt(0).Points[0].Y;

        Assert.Equal(2, vm.PasteWiresAtFreePitch(clipboard, WBondUnits.ToNm(25.0, WBondUnit.Mil)));

        var pasted = vm.Design.AllWires().Skip(2).ToList();
        Assert.Equal(spacingBefore, pasted[1].Points[0].Y - pasted[0].Points[0].Y);
    }

    /// <summary>
    /// <b>A paste steps ACROSS the wires, not along a fixed axis</b> (owner, 2026-08-16: "pasting a
    /// north-south wire uses the wrong dimension for the offset"). A bond array is pitched
    /// perpendicular to its wires — that is what a pitch is — so a north/south wire's copy lands
    /// beside it in x, and an east/west wire's above it in y, which is what paste already did and
    /// must keep doing.
    /// </summary>
    [Theory]
    [InlineData(0.0, 30.0, 1.0, 0.0)]    // north/south chord → step east
    [InlineData(100.0, 0.0, 0.0, 1.0)]   // east/west chord   → step north
    [InlineData(0.0, -30.0, 1.0, 0.0)]   // …and a wire's direction along its own line is irrelevant
    [InlineData(-100.0, 0.0, 0.0, 1.0)]
    public void ThePasteOffset_StepsPerpendicularToTheWire(
        double chordXMils, double chordYMils, double expectUx, double expectUy)
    {
        long pitch = WBondUnits.ToNm(5.0, WBondUnit.Mil);

        var vm = new WBondViewModel(OneWire(chordXMils, chordYMils));
        vm.SelectAllWires();

        var payload = WBondClipboard.TryParse(vm.CopySelection())!;
        var (dx, dy) = vm.FreePasteOffset(payload, pitch);

        Assert.Equal((long)Math.Round(pitch * expectUx), dx);
        Assert.Equal((long)Math.Round(pitch * expectUy), dy);
    }

    /// <summary>
    /// A wire on neither axis steps at its own perpendicular rather than being forced onto one — the
    /// offset is orthogonal to the chord and one pitch long. Stated as a dot product, because that is
    /// the property "perpendicular" actually means.
    /// </summary>
    [Fact]
    public void ThePasteOffset_IsOrthogonalToADiagonalWire()
    {
        long pitch = WBondUnits.ToNm(5.0, WBondUnit.Mil);

        var vm = new WBondViewModel(OneWire(70.0, 70.0));
        vm.SelectAllWires();

        var payload = WBondClipboard.TryParse(vm.CopySelection())!;
        var (dx, dy) = vm.FreePasteOffset(payload, pitch);

        Assert.Equal(0.0, (dx * 1.0 + dy * 1.0) / pitch, 3);   // ⟂ to the 45° chord

        // One pitch long, to within the nanometre every wBond coordinate quantises to (§6.4's own
        // measured note) — the offset is rounded to integer nm, so it cannot be exact off-axis.
        double length = Math.Sqrt((double)dx * dx + (double)dy * dy);
        Assert.InRange(length, pitch - 2, pitch + 2);
    }

    /// <summary>
    /// <b>The pasted copy sits BESIDE the original, not end-to-end with it.</b> The dimension bug's
    /// real cost: a north/south wire offset in y landed nose-to-tail with the wire it was copied from,
    /// which is not an array and not what anyone means by a pitch.
    /// </summary>
    [Fact]
    public void PastingANorthSouthWire_LandsBesideTheOriginal()
    {
        var vm = new WBondViewModel(OneWire(0.0, 30.0));
        vm.SelectAllWires();

        Assert.Equal(1, vm.PasteWiresAtFreePitch(vm.CopySelection(), WBondUnits.ToNm(5.0, WBondUnit.Mil)));

        var original = vm.Design.AllWires().ElementAt(0);
        var pasted = vm.Design.AllWires().ElementAt(1);

        Assert.NotEqual(original.Points[0].X, pasted.Points[0].X);   // moved across
        Assert.Equal(original.Points[0].Y, pasted.Points[0].Y);      // …and not along
    }

    /// <summary>One wire from the origin along the given chord, on the seed arch.</summary>
    private static WBondDesign OneWire(double chordXMils, double chordYMils)
    {
        long loopNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);
        var design = new WBondDesign();
        design.Arrays.Add(new WireArray
        {
            Name = "G1",
            Wires =
            {
                LoopShape.CreateSeedWire(Point3.Mils(0, 0, 4), Point3.Mils(chordXMils, chordYMils, 1),
                                   WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopHeightNm: loopNm),
            },
        });
        return design;
    }

    /// <summary>A pitch of zero or less falls back to the shipped step rather than pasting in place.</summary>
    [Fact]
    public void APitchOfZero_FallsBackRatherThanPastingInPlace()
    {
        var vm = new WBondViewModel(Design(wires: 1));
        vm.SelectAllWires();

        var payload = WBondClipboard.TryParse(vm.CopySelection())!;

        Assert.Equal((0L, WireEdits.CoarseNudgeNm), vm.FreePasteOffset(payload, 0));
    }

    // ────────────────────────────────────────────────────────── the three deletes

    /// <summary>
    /// <b>Delete Vertex removes one point and does nothing else.</b>
    ///
    /// <para>It used to also detach the wire from its loop profile, because the profile defined the
    /// point set and a re-apply would have put the point back. With profiles removed (2026-08-18) the
    /// remaining obligation is that the wire's OTHER points do not move — a delete that also reshaped
    /// the loop would be two edits under one menu item.</para>
    /// </summary>
    [Fact]
    public void DeleteVertex_RemovesThePointAndLeavesTheRestWhereTheyWere()
    {
        var vm = new WBondViewModel(Design(wires: 1));
        var wire = vm.Design.AllWires().Single();

        var before = wire.Points.ToArray();
        Assert.True(vm.DeleteWirePoint(0, 3));

        var after = vm.Design.AllWires().Single().Points.ToArray();
        Assert.Equal(before.Length - 1, after.Length);
        Assert.Equal(before.Where((_, i) => i != 3), after);
    }

    /// <summary>
    /// <b>Delete Segment removes exactly one segment and leaves ONE wire.</b> Splitting a bond wire
    /// in two is not offered: the reduction sums current along one continuous path (§3.4), and two
    /// disconnected halves are not something the physics can evaluate.
    /// </summary>
    [Fact]
    public void DeleteSegment_RemovesOneSegmentAndKeepsOneWire()
    {
        var vm = new WBondViewModel(Design(wires: 1));
        int segmentsBefore = vm.Design.AllWires().Single().Points.Count - 1;

        Assert.True(vm.DeleteWireSegment(0, 2));

        Assert.Equal(1, vm.Design.WireCount);
        Assert.Equal(segmentsBefore - 1, vm.Design.AllWires().Single().Points.Count - 1);
    }

    /// <summary>
    /// Neither delete may take a wire below two points — there is no wire below that, and
    /// <c>WireMesh.Build</c> has nothing to flatten. Refused with a reason for the menu to show,
    /// never silently no-op.
    /// </summary>
    [Fact]
    public void NeitherDelete_MayTakeAWireBelowTwoPoints()
    {
        var vm = new WBondViewModel(Design(wires: 1));
        var wire = vm.Design.AllWires().Single();

        while (wire.Points.Count > 2) wire.Points.RemoveAt(1);
        vm.CommitStructuralChange();

        Assert.NotNull(vm.WhyCannotDeletePoint(0, 0));
        Assert.NotNull(vm.WhyCannotDeleteSegment(0, 0));
        Assert.False(vm.DeleteWirePoint(0, 0));
        Assert.False(vm.DeleteWireSegment(0, 0));
        Assert.Equal(2, vm.Design.AllWires().Single().Points.Count);
    }

    /// <summary>Delete Wire removes exactly the one wire, leaving its group and its neighbours.</summary>
    [Fact]
    public void DeleteWire_RemovesOneWireAndKeepsTheRest()
    {
        var vm = new WBondViewModel(Design(wires: 2));

        Assert.True(vm.DeleteWire(0));

        Assert.Equal(1, vm.Design.WireCount);
        Assert.Single(vm.Design.Arrays);
    }

    /// <summary>
    /// <b>The LAST wire CAN be deleted, and leaves an empty design</b> (owner, 2026-08-16: "make it
    /// support 0 wires").
    ///
    /// <para>This test used to assert the opposite, and the reason it did is worth keeping: while
    /// <c>WBondDesign.Validate</c> rejected a design with no arrays, the delete would go through, fail
    /// the rebuild, roll back, and report a mapping-matrix error to someone who had just chosen
    /// Delete Wire. An empty design is now valid — the last group is pruned with the rest — so the
    /// refusal is gone rather than reworded, and only an index naming no wire is refused.</para>
    /// </summary>
    [Fact]
    public void DeleteWire_TakesTheLastWireAndLeavesAnEmptyDesign()
    {
        var vm = new WBondViewModel(Design(wires: 1));

        Assert.Null(vm.WhyCannotDeleteWire(0));
        Assert.True(vm.DeleteWire(0));

        Assert.Equal(0, vm.Design.WireCount);
        Assert.Empty(vm.Design.Arrays);
        vm.Design.Validate();

        Assert.NotNull(vm.WhyCannotDeleteWire(0));   // …and now there is nothing there to delete
    }

    /// <summary>All three are undoable — they are structural, and an undo must put the wire back.</summary>
    [Fact]
    public void TheDeletes_AreUndoable()
    {
        var vm = new WBondViewModel(Design(wires: 2));

        vm.DeleteWire(0);
        Assert.Equal(1, vm.Design.WireCount);
        vm.Undo();
        Assert.Equal(2, vm.Design.WireCount);

        int points = vm.Design.AllWires().First().Points.Count;
        vm.DeleteWirePoint(0, 2);
        vm.Undo();
        Assert.Equal(points, vm.Design.AllWires().First().Points.Count);
    }

    // ────────────────────────────────────────────────────────── Group Wires As…

    /// <summary>
    /// <b>A batch regroup is ONE undo entry and one rebuild.</b> A loop over the single-wire move
    /// would leave N entries on the stack, so Ctrl+Z would walk the regrouping back a wire at a time.
    /// </summary>
    [Fact]
    public void GroupWiresAs_MovesTheWholeSelectionInOneUndoableStep()
    {
        var vm = new WBondViewModel(Design(wires: 4));
        vm.SelectAllWires();

        Assert.Equal(4, vm.MoveWiresToGroup([0, 1, 2, 3], "GND"));

        Assert.Equal(4, vm.Design.Arrays.Single(a => a.Name == "GND").Wires.Count);

        // G1 is GONE, not left behind empty: WBondDesign.Validate rejects an array with no wires —
        // "an empty array makes the mapping matrix rank-deficient and the array-basis inductance
        // singular" — so keeping it would make the whole regroup unevaluable and roll it back.
        Assert.DoesNotContain("G1", vm.GroupNames);

        vm.Undo();

        Assert.Equal(4, vm.Design.Arrays.Single(a => a.Name == "G1").Wires.Count);
    }

    /// <summary>
    /// The selection follows the wires to their new group rather than being dropped — the user is
    /// looking at them, and a blanked selection after a regroup reads as the command having failed.
    /// </summary>
    [Fact]
    public void GroupWiresAs_RePointsTheSelectionToWhereTheWiresLanded()
    {
        var vm = new WBondViewModel(Design(wires: 3));

        vm.MoveWiresToGroup([0, 2], "GND");

        Assert.Equal(2, vm.Selection.TouchedWires().Count);
        foreach (int index in vm.Selection.TouchedWires())
            Assert.Equal("GND", vm.GroupNameOfWire(index));
    }

    /// <summary>A name nobody has used yet creates the group; wires already in it are not moved twice.</summary>
    [Fact]
    public void GroupWiresAs_CreatesANewGroupAndIsIdempotent()
    {
        var vm = new WBondViewModel(Design(wires: 2));

        Assert.Equal(2, vm.MoveWiresToGroup([0, 1], "NEW"));
        Assert.Equal(0, vm.MoveWiresToGroup([0, 1], "NEW"));   // already there — nothing to do
    }

    /// <summary>The suggested new-group name is always one that is actually free.</summary>
    [Fact]
    public void TheSuggestedGroupName_IsNotAlreadyTaken()
    {
        var vm = new WBondViewModel(Design(wires: 1));
        Assert.DoesNotContain(vm.SuggestGroupName(), vm.GroupNames);
    }

    // ────────────────────────────────────────────────────────── profile-view visibility

    /// <summary>
    /// <b>What the profile view DRAWS does not change when the selection changes.</b> The owner's
    /// report: members of a group whose geometry differs were invisible until a marquee happened to
    /// catch them, so wires appeared and disappeared with the selection.
    ///
    /// <para>The oracle is the count of pixels in the WIRE colour — the geometry on screen — measured
    /// with and without a selection. It must not go up: a selection may RECOLOUR a curve, and this
    /// fixture is drawn in the YZ plane where the members genuinely sit apart, so every one of them
    /// has to be on screen either way.</para>
    /// </summary>
    [Fact]
    public void TheProfileView_DrawsTheSameGeometryWhetherOrNotAnythingIsSelected()
    {
        var design = Design(wires: 5, pitchMils: 6.0);
        double yz = Math.PI / 2.0;

        int unselected = DrawnCurvePixels(design, null, yz);
        int selected = DrawnCurvePixels(design, new WireSelection { Wires = { 2 } }, yz);

        Assert.True(unselected > 0, "the members must be drawn at all");

        // Wire 2 changes colour, so its own pixels leave the wire-coloured count — but nothing NEW
        // may appear, which is what the report is about.
        Assert.True(selected <= unselected,
                    $"selecting must not make geometry appear; {unselected} -> {selected}");
    }

    /// <summary>
    /// <b>Every distinctly-placed member of a group is drawn.</b> Stated as a count rather than as an
    /// absence: in the YZ plane five wires 6 mil apart occupy five separate places, so drawing one
    /// representative and a band is not a picture of them.
    /// </summary>
    [Fact]
    public void TheProfileView_DrawsEveryMemberThatSitsSomewhereElse()
    {
        double yz = Math.PI / 2.0;

        Assert.Equal(1, WiresDrawn(Design(wires: 1), yz));
        Assert.Equal(5, WiresDrawn(Design(wires: 5), yz));
    }

    /// <summary>
    /// <b>…and §6.2's clutter rule still applies where it means something.</b> Under AUTO every member
    /// of a same-shape array projects onto its own chord and therefore onto the same curve, so exactly
    /// one is drawn — drawing five would put five identical polylines on the same pixels.
    /// </summary>
    [Fact]
    public void TheProfileView_StillCollapsesCoincidentMembersUnderAuto()
    {
        // AUTO — each wire on its own chord, so the five coincide.
        Assert.Equal(1, WiresDrawn(Design(wires: 5), azimuth: null));
    }

    /// <summary>How many wires one profile render actually put on the canvas.</summary>
    private static int WiresDrawn(WBondDesign design, double? azimuth)
    {
        using var surface = SKSurface.Create(new SKImageInfo(800, 600));

        return WBondRenderer.DrawProfile(
            surface.Canvas, design, WBondRenderTheme.Fallback,
            s => (float)(s / 4000.0), z => (float)(600 - z / 2000.0),
            azimuthRadians: azimuth).WiresDrawn;
    }

    /// <summary>Counts pixels in the theme's WIRE colour — the geometry actually on screen.</summary>
    private static int DrawnCurvePixels(WBondDesign design, WireSelection? selection, double? azimuth)
    {
        var theme = WBondRenderTheme.Fallback;

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        surface.Canvas.Clear(SKColors.Black);

        WBondRenderer.DrawProfile(
            surface.Canvas, design, theme,
            span => (float)(span / 4000.0), z => (float)(600 - z / 2000.0),
            selection: selection, azimuthRadians: azimuth);
        surface.Canvas.Flush();

        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);

        int lit = 0;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                var px = bitmap.GetPixel(x, y);
                if (Math.Abs(px.Red - theme.Wire.Red) < 40
                    && Math.Abs(px.Green - theme.Wire.Green) < 40
                    && Math.Abs(px.Blue - theme.Wire.Blue) < 40) lit++;
            }

        return lit;
    }

    // ────────────────────────────────────────────────────────── the graphic clipboard

    /// <summary>
    /// <b>The copied picture is framed on the SELECTION, and on the geometry under it as well.</b>
    /// Framing the wires alone silently crops away the pads a reader needs in order to make sense of
    /// them — the same lesson <c>LayoutClipboard</c> learned from cropped ports.
    /// </summary>
    [Fact]
    public void TheCopiedGraphic_FramesTheWiresAndTheGeometryTogether()
    {
        var design = Design(wires: 1);

        var layout = new LayoutView();
        layout.Shapes.Add(new RectShape { X1 = -50_000, Y1 = -50_000, X2 = -30_000, Y2 = -30_000 });

        var wiresOnly = WBondClipboardWriter.ContentBoundsForTests(design, null)!.Value;
        var withLayout = WBondClipboardWriter.ContentBoundsForTests(design, layout)!.Value;

        Assert.True(withLayout.W > wiresOnly.W || withLayout.H > wiresOnly.H,
                    "the geometry must widen the page, not be cropped off it");
        Assert.True(withLayout.MinX <= wiresOnly.MinX);
        Assert.True(withLayout.MinY <= wiresOnly.MinY);
    }

    /// <summary>
    /// The picture shows what was COPIED, not the whole board — a selection of one wire out of five
    /// pastes as one wire.
    /// </summary>
    [Fact]
    public void TheCopiedGraphic_ShowsOnlyTheSelectedWires()
    {
        var design = Design(wires: 5);
        var selection = new WireSelection { Wires = { 1 } };

        var copied = WBondClipboardWriter.SelectionDesign(design, selection);

        Assert.Equal(1, copied.WireCount);
        Assert.Equal("G1", copied.Arrays.Single().Name);   // group identity travels with it
    }

    /// <summary>
    /// <b>Every wire POINT lands far enough inside the page for its dot to fit</b> (owner: "the
    /// clipboard image has the points clipped in the rendering, at the edges of the bounding box").
    /// The bounds are of the points; what is DRAWN at each one is a filled dot and a stroke, both in
    /// screen pixels that no world-space bbox knows about — so an extreme point sat exactly on the
    /// frame edge and half its dot fell off.
    ///
    /// <para>Run on the north/south wire because that is where it bites hardest: a straight wire has
    /// ZERO extent across its own axis, so the proportional pad shrinks to nothing there while the
    /// dot stays three pixels wide.</para>
    /// </summary>
    [Theory]
    [InlineData(0.0, 30.0)]     // north/south — the degenerate-in-x case
    [InlineData(100.0, 0.0)]    // east/west   — degenerate in y
    [InlineData(70.0, 70.0)]    // diagonal    — degenerate in neither
    public void TheCopiedGraphic_KeepsEveryPointClearOfThePageEdge(double chordXMils, double chordYMils)
    {
        var design = OneWire(chordXMils, chordYMils);

        var frame = WBondClipboardWriter.BitmapFrameForTests(design, null)!.Value;
        float margin = WBondClipboardWriter.GlyphMarginForTests;

        foreach (var point in design.AllWires().Single().Points)
        {
            double sx = frame.Vp.WorldToScreenX(WBondSnap.ToDbu(point.X, LayoutUnits.DefaultDbuPerMicron));
            double sy = frame.Vp.WorldToScreenY(WBondSnap.ToDbu(point.Y, LayoutUnits.DefaultDbuPerMicron));

            Assert.InRange(sx, margin, frame.W - margin);
            Assert.InRange(sy, margin, frame.H - margin);
        }
    }

    /// <summary>
    /// …and the content is CENTRED on the page rather than offset from its corner by a pad fraction.
    /// The two are only the same thing while the page is exactly the padded content size, and it is
    /// not: the axes share one zoom and each page dimension is clamped to an 80 px floor. That is what
    /// pinned a straight wire to the left edge of its own picture.
    /// </summary>
    [Fact]
    public void TheCopiedGraphic_CentresTheContentOnThePage()
    {
        var design = OneWire(0.0, 30.0);
        var b = WBondClipboardWriter.ContentBoundsForTests(design, null)!.Value;

        var frame = WBondClipboardWriter.BitmapFrameForTests(design, null)!.Value;

        double centreX = frame.Vp.WorldToScreenX(b.MinX + b.W / 2.0);
        double centreY = frame.Vp.WorldToScreenY(b.MinY + b.H / 2.0);

        Assert.Equal(frame.W / 2.0, centreX, 1);
        Assert.Equal(frame.H / 2.0, centreY, 1);
    }

    /// <summary>An empty selection means the whole design, so a copy is never a blank page.</summary>
    [Fact]
    public void TheCopiedGraphic_FallsBackToTheWholeDesignWhenNothingIsSelected()
    {
        var design = Design(wires: 3);
        Assert.Equal(3, WBondClipboardWriter.SelectionDesign(design, new WireSelection()).WireCount);
    }
}

using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// Owner report, 2026-08-12: "MTEE changes the LENGTH of its microstrip lines when the user changes
/// the width. Changing W1 using its layout-editor gripper will move the end point of the microstrip
/// line in the opposite axis. Same issue is seen for MCROSS."
///
/// <para>Confirmed: every junction arm's drawn length was <c>2.5 ×</c> that arm's own width, so a
/// width edit relocated the junction and the other pins along the perpendicular axis. Each arm now
/// declares its own <c>L</c>, and the corner grips drive both — across the arm for width, along it
/// for length (R-pch-4a's orthogonal decomposition).</para>
///
/// <para>Two properties are load-bearing and each has its own test below: the reported bug is gone
/// for a cell that declares lengths, and a cell that does NOT (one authored before these parameters
/// existed) is byte-identical — geometry AND grips — so no <c>GeneratorVersion</c> bump is owed and
/// no placed instance is repointed.</para>
/// </summary>
public sealed class JunctionArmLengthTests : IDisposable
{
    private readonly string _workspaceDir;

    public JunctionArmLengthTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crf-junction-arm-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        File.WriteAllText(Path.Combine(_workspaceDir, ".cws"), "{}");
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir)) Directory.Delete(_workspaceDir, recursive: true);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyDictionary<string, PCellValue> Reals(Dictionary<string, double> v)
        => PCellParameters.FromReals(v);

    /// <summary>Widths only — a cell authored before L1/L2/L3 existed.</summary>
    private static IReadOnlyDictionary<string, PCellValue> LegacyTee()
        => Reals(new() { ["W1"] = 300e-6, ["W2"] = 400e-6, ["W3"] = 500e-6 });

    /// <summary>
    /// The same widths with each arm's length declared — the shipping shape. Lengths are deliberately
    /// well clear of half the crossing arm's width so the clamp does not fire when a test widens one
    /// arm; the clamp has its own test, and a fixture that trips it incidentally would measure that
    /// instead of what it is about.
    /// </summary>
    private static IReadOnlyDictionary<string, PCellValue> TeeWithLengths(
        double l1 = 1e-3, double l2 = 2e-3, double l3 = 3e-3)
        => Reals(new()
        {
            ["W1"] = 300e-6, ["W2"] = 400e-6, ["W3"] = 500e-6,
            ["L1"] = l1, ["L2"] = l2, ["L3"] = l3,
        });

    /// <summary>See <see cref="TeeWithLengths"/> on why the lengths are generous.</summary>
    private static IReadOnlyDictionary<string, PCellValue> CrossLengths() => Reals(new()
    {
        ["W1"] = 300e-6, ["W2"] = 400e-6, ["W3"] = 500e-6, ["W4"] = 600e-6,
        ["L1"] = 1e-3, ["L2"] = 2e-3, ["L3"] = 3e-3, ["L4"] = 4e-3,
    });

    private static IReadOnlyDictionary<string, PCellValue> LegacyCross()
        => Reals(new() { ["W1"] = 300e-6, ["W2"] = 400e-6, ["W3"] = 500e-6, ["W4"] = 600e-6 });

    private static IReadOnlyDictionary<string, PCellValue> CrossWithLengths() => CrossLengths();

    private static PCellResult Tee(IReadOnlyDictionary<string, PCellValue> p)
        => MTeePCell.Generate(p, technology: null, PCellLayerSelection.Default);

    private static PCellResult Cross(IReadOnlyDictionary<string, PCellValue> p)
        => MCrossPCell.Generate(p, technology: null, PCellLayerSelection.Default);

    private static PCellPin Pin(PCellResult r, string name) => r.Pins.Single(p => p.Name == name);

    // ── The reported bug ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void MTee_WithLengthsDeclared_ChangingAWidth_LeavesEveryPinWhereItWas()
    {
        // The headline. Only W1 moves; the junction (and therefore pins 2 and 3) must not.
        var before = Tee(TeeWithLengths());
        var after = Tee(Reals(new()
        {
            ["W1"] = 900e-6,                                  // tripled
            ["W2"] = 400e-6, ["W3"] = 500e-6,
            ["L1"] = 1e-3, ["L2"] = 2e-3, ["L3"] = 3e-3,
        }));

        foreach (string name in (string[])["1", "2", "3"])
        {
            Assert.Equal(Pin(before, name).X, Pin(after, name).X);
            Assert.Equal(Pin(before, name).Y, Pin(after, name).Y);
        }

        // Non-vacuous: the width really did change, so the geometry is not simply identical.
        Assert.NotEqual(Pin(before, "1").WidthDbu, Pin(after, "1").WidthDbu);
    }

    [Fact]
    public void MTee_OnTheDerivedFallback_ChangingAWidth_StillMovesTheOtherPins()
    {
        // The control that gives the test above its teeth: without declared lengths this IS the old
        // behaviour, and it is deliberately preserved so existing artwork does not move.
        var before = Tee(LegacyTee());
        var after = Tee(Reals(new() { ["W1"] = 900e-6, ["W2"] = 400e-6, ["W3"] = 500e-6 }));

        Assert.NotEqual(Pin(before, "2").X, Pin(after, "2").X);
        Assert.NotEqual(Pin(before, "3").X, Pin(after, "3").X);
    }

    [Fact]
    public void MCross_WithLengthsDeclared_ChangingAWidth_LeavesEveryPinWhereItWas()
    {
        var before = Cross(CrossWithLengths());
        var after = Cross(Reals(new()
        {
            ["W1"] = 900e-6, ["W2"] = 400e-6, ["W3"] = 500e-6, ["W4"] = 600e-6,
            ["L1"] = 1e-3, ["L2"] = 2e-3, ["L3"] = 3e-3, ["L4"] = 4e-3,
        }));

        foreach (string name in (string[])["1", "2", "3", "4"])
        {
            Assert.Equal(Pin(before, name).X, Pin(after, name).X);
            Assert.Equal(Pin(before, name).Y, Pin(after, name).Y);
        }
        Assert.NotEqual(Pin(before, "1").WidthDbu, Pin(after, "1").WidthDbu);
    }

    // ── Lengths mean what they say ───────────────────────────────────────────────────────────

    [Fact]
    public void MTee_EachArmIsExactlyItsOwnDeclaredLength()
    {
        var r = Tee(TeeWithLengths(l1: 1e-3, l2: 2e-3, l3: 3e-3));

        // R4 puts pin 1 at the origin, so the junction sits at L1 and pin 2 at L1 + L2.
        Assert.Equal(0, Pin(r, "1").X);
        Assert.Equal(3_000_000, Pin(r, "2").X);      // 1 mm + 2 mm, in nm
        Assert.Equal(1_000_000, Pin(r, "3").X);      // the junction
        Assert.Equal(-3_000_000, Pin(r, "3").Y);     // 3 mm down the branch
    }

    [Fact]
    public void MCross_EachArmIsExactlyItsOwnDeclaredLength()
    {
        var r = Cross(Reals(new()
        {
            ["W1"] = 300e-6, ["W2"] = 300e-6, ["W3"] = 300e-6, ["W4"] = 300e-6,
            ["L1"] = 1e-3, ["L2"] = 2e-3, ["L3"] = 3e-3, ["L4"] = 4e-3,
        }));

        Assert.Equal((1_000_000L, 0L), (Pin(r, "1").X, Pin(r, "1").Y));
        Assert.Equal((0L, 2_000_000L), (Pin(r, "2").X, Pin(r, "2").Y));
        Assert.Equal((-3_000_000L, 0L), (Pin(r, "3").X, Pin(r, "3").Y));
        Assert.Equal((0L, -4_000_000L), (Pin(r, "4").X, Pin(r, "4").Y));
    }

    // ── Nothing already drawn moves ──────────────────────────────────────────────────────────

    [Fact]
    public void ALegacyCell_KeepsExactlyTheGeometryAndGripsItAlwaysHad()
    {
        // 2.5 × width per arm — PCellGeometryHelpers.StubLengthFactor, restated here independently so
        // a change to that constant fails this test rather than silently redefining what "unchanged"
        // means for artwork already on disk.
        var tee = Tee(LegacyTee());
        Assert.Equal(750_000, Pin(tee, "3").X);            // 2.5 × 300 um
        Assert.Equal(-1_250_000, Pin(tee, "3").Y);         // 2.5 × 500 um
        Assert.Equal(1_750_000, Pin(tee, "2").X);          // 2.5 × (300 + 400) um

        // Six grips, width-only, anchored on their own arm's end cap, and NOT pinned — pinning on the
        // derived path would make a plain width drag translate the instance, since the junction moves
        // with W1 there.
        Assert.Equal(6, tee.Handles!.Count);
        Assert.All(tee.Handles!, h => Assert.Null(h.Cross));
        Assert.All(tee.Handles!, h => Assert.False(h.KeepAnchorFixed));
        Assert.Equal((0L, 0L), tee.Handles!.Where(h => h.Parameter == "W1")
                                           .Select(h => (h.AnchorX, h.AnchorY)).First());

        var cross = Cross(LegacyCross());
        Assert.Equal(750_000, Pin(cross, "1").X);
        Assert.Equal(8, cross.Handles!.Count);
        Assert.All(cross.Handles!, h => Assert.Null(h.Cross));
        Assert.All(cross.Handles!, h => Assert.False(h.KeepAnchorFixed));
    }

    [Fact]
    public void APartiallyDeclaredCell_KeepsTheWidthOnlyGrips()
    {
        // Not a state anything ships (the registry seeds all three together), but a hand-edited cell
        // can reach it. A cross axis naming an absent L would be dropped and reported on every
        // generate, so the all-or-nothing gate is the safe answer — the arms that DO declare a length
        // still use it.
        var r = Tee(Reals(new()
        {
            ["W1"] = 300e-6, ["W2"] = 400e-6, ["W3"] = 500e-6, ["L1"] = 1e-3,
        }));

        Assert.Equal(6, r.Handles!.Count);
        Assert.All(r.Handles!, h => Assert.Null(h.Cross));
        Assert.Equal(1_000_000, Pin(r, "3").X);            // L1 honoured
        Assert.Equal(-1_250_000, Pin(r, "3").Y);           // L3 derived, 2.5 × 500 um
    }

    // ── The corner grips drive both axes ─────────────────────────────────────────────────────

    [Fact]
    public void MTee_EachCornerGrip_DeclaresItsArmsWidthAlongOneAxisAndItsLengthAcrossTheOther()
    {
        var handles = Tee(TeeWithLengths()).Handles!;

        Assert.Equal(6, handles.Count);
        Assert.Equal(6, handles.Select(h => (h.X, h.Y)).ToHashSet().Count);   // never coincident

        foreach (var (w, l) in ((string, string)[])[("W1", "L1"), ("W2", "L2"), ("W3", "L3")])
        {
            var pair = handles.Where(h => h.Parameter == w).ToList();
            Assert.Equal(2, pair.Count);
            Assert.All(pair, h => Assert.Equal(l, h.Cross!.Parameter));
            Assert.All(pair, h => Assert.True(h.KeepAnchorFixed));
        }

        // The branch runs along -Y, so its WIDTH is measured across X, unlike the through arms.
        Assert.Equal([0d, 180d], handles.Where(h => h.Parameter == "W3").Select(h => h.AxisDeg).Order().ToArray());
        Assert.Equal([90d, 270d], handles.Where(h => h.Parameter == "W1").Select(h => h.AxisDeg).Order().ToArray());
    }

    [Fact]
    public void MCross_EachCornerGrip_DeclaresItsArmsWidthAndLength()
    {
        var handles = Cross(CrossWithLengths()).Handles!;

        Assert.Equal(8, handles.Count);
        Assert.Equal(8, handles.Select(h => (h.X, h.Y)).ToHashSet().Count);

        foreach (var (w, l) in ((string, string)[])[("W1", "L1"), ("W2", "L2"), ("W3", "L3"), ("W4", "L4")])
        {
            var pair = handles.Where(h => h.Parameter == w).ToList();
            Assert.Equal(2, pair.Count);
            Assert.All(pair, h => Assert.Equal(l, h.Cross!.Parameter));
        }

        // Every anchor is the cross's own centre, which is the cell origin and never moves.
        Assert.All(handles, h => Assert.Equal((0L, 0L), (h.AnchorX, h.AnchorY)));
    }

    [Fact]
    public void EveryJunctionHandle_NamesParametersItsOwnGeneratorReceives()
    {
        // R2's one list, on both axes — the check that would have caught a cross axis naming an L the
        // cell does not declare.
        foreach (var (p, gen) in ((IReadOnlyDictionary<string, PCellValue>, Func<IReadOnlyDictionary<string, PCellValue>, PCellResult>)[])
                 [(TeeWithLengths(), Tee), (LegacyTee(), Tee), (CrossWithLengths(), Cross), (LegacyCross(), Cross)])
        {
            foreach (var h in gen(p).Handles ?? [])
            {
                Assert.Equal(PCellHandleRejection.None, PCellHandleSolver.Validate(h, p));
                if (h.Cross is not null)
                    Assert.Equal(PCellHandleRejection.None, PCellHandleSolver.Validate(h.AsCrossHandle(), p));
            }
        }
    }

    // ── The clamp ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnExplicitLengthUnderHalfTheCrossingWidth_IsClampedAndReported()
    {
        // Below w3/2 the branch overhangs the through arm's own end cap.
        var r = Tee(Reals(new()
        {
            ["W1"] = 300e-6, ["W2"] = 300e-6, ["W3"] = 4e-3,
            ["L1"] = 100e-6, ["L2"] = 300e-6, ["L3"] = 300e-6,
        }));

        Assert.Equal(2_000_000, Pin(r, "3").X);            // clamped up to W3/2
        Assert.Contains(r.Diagnostics ?? [], d => d.Contains("L1", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDerivedFallback_IsNeverClamped()
    {
        // Clamping it too would move artwork already on disk whenever one arm is far wider than
        // another — which is exactly what the "no GeneratorVersion bump" claim rests on not happening.
        var r = Tee(Reals(new() { ["W1"] = 100e-6, ["W2"] = 100e-6, ["W3"] = 4e-3 }));

        Assert.Equal(250_000, Pin(r, "3").X);              // 2.5 × 100 um, well under W3/2 = 2 mm
        Assert.Null(r.Diagnostics);
    }

    // ── Placement defaults ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SymbolKind.MTee, 3)]
    [InlineData(SymbolKind.MCross, 4)]
    public void EachArmLengthDefaultsToItsOwnWidth(SymbolKind kind, int arms)
    {
        // Owner's call, 2026-08-12 — a square stub per arm.
        var defaults = ComponentTypeRegistry.DefaultParameters(kind, 0).ToList();
        for (int i = 1; i <= arms; i++)
        {
            var w = defaults.Single(p => p.Name == $"W{i}");
            var l = defaults.Single(p => p.Name == $"L{i}");
            Assert.Equal(w.Expression, l.Expression);
            Assert.Equal(w.Unit, l.Unit);
            Assert.Equal(UnitDimension.Length, l.Dimension);
        }
    }

    [Fact]
    public void AFreshlyPlacedTee_HasGripsThatDriveBothAxes()
    {
        // End to end through the real placement path: registry defaults -> SI -> generated cell ->
        // the grips the layout editor actually shows.
        var vm = new LayoutEditorViewModel(
            new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 },
            Path.Combine(_workspaceDir, "Doc", "layout", "main.clay"));

        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.MTee, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(
            _workspaceDir, "MTEE", defaults, null, null, PCellLayerSelection.Default);
        vm.Model.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), Mag = 1.0,
        });
        vm.SelectInstance(0);

        Assert.Equal(6, vm.Overlay.PCellHandles.Count);
        Assert.All(vm.Overlay.PCellHandles, h => Assert.True(h.HasCrossAxis));
    }

    [Fact]
    public void DraggingACornerGripAcrossItsArm_EditsThatWidthAndLeavesEveryLengthAlone()
    {
        var vm = PlaceTee();
        var before = ParametersOf(vm);
        var grip = vm.Overlay.PCellHandles.First(h => h.Label == "W1");

        // Straight across the arm — no travel along it, so the cross axis reads zero.
        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 400_000);
        vm.OnPointerMoved(grip.X, 400_000, leftDown: true, KeyModifiers.None, hitTolDbu: 400_000);
        vm.OnPointerReleased(grip.X, 400_000, KeyModifiers.None);

        var after = ParametersOf(vm);
        Assert.NotEqual(before.Real("W1"), after.Real("W1"));
        foreach (string l in (string[])["L1", "L2", "L3"])
            Assert.Equal(before.Real(l), after.Real(l), 9);
    }

    // ── Negative is fine DURING a drag, never after mouse up ─────────────────────────────────

    [Fact]
    public void TheGeneratorDrawsANegativeValueAsAsked_SoADragCanPassThroughZero()
    {
        // Owner's call: "a negative value is OK during drag for MCROSS and MTEE, just not after mouse
        // up." The generator therefore stays SIGN-TRANSPARENT — and that is load-bearing rather than
        // permissive: PCellHandleSolver measures a grip's sensitivity by perturbing the value and
        // re-reading where the generator put the grip, so a clamp would flatten that map below the
        // floor and the grip would stop following the cursor at all.
        var r = Tee(Reals(new()
        {
            ["W1"] = 300e-6, ["W2"] = 300e-6, ["W3"] = 300e-6,
            ["L1"] = -1e-3, ["L2"] = 1e-3, ["L3"] = 1e-3,
        }));

        Assert.Equal(-1_000_000, Pin(r, "3").X);   // the junction really is drawn on the wrong side

        // And the crossing-width clamp does not fight it — a negative stub passes straight through.
        Assert.DoesNotContain(r.Diagnostics ?? [], d => d.Contains("L1", StringComparison.Ordinal));
    }

    [Fact]
    public void ANegativeWidthDraggedAndReleased_CommitsAsItsOwnMagnitude()
    {
        // The exact same rectangle: BuildArmRect spans origin ± width/2, so flipping the sign names
        // the same two coordinates in the other order — an inverted RectShape whose ring winds
        // backwards, which is what makes a same-layer overlap cancel instead of fill.
        var vm = PlaceTee();
        double w1Before = ParametersOf(vm).Real("W1");
        var grip = vm.Overlay.PCellHandles.First(h => h.Label == "W1");

        // Straight across the arm and well past the centreline — a width the projection reads as
        // negative.
        long farSide = -(grip.Y == 0 ? 400_000 : Math.Abs(grip.Y) * 4);
        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 400_000);
        vm.OnPointerMoved(grip.X, farSide, leftDown: true, KeyModifiers.None, hitTolDbu: 400_000);
        vm.OnPointerReleased(grip.X, farSide, KeyModifiers.None);

        double w1After = ParametersOf(vm).Real("W1");
        Assert.True(w1After > 0, $"W1 committed as {w1After}, which is not a width");
        Assert.NotEqual(w1Before, w1After);   // non-vacuous: the drag really did change it
    }

    [Fact]
    public void MTee_ALengthGripDraggedPastTheJunction_StopsAtTheCrossingMinimum()
    {
        // Owner's later call: "clamp the L parameters for MTEE and MCROSS so they never go negative
        // during a drag." The bound is declared on the length AXIS of each corner grip and is exactly
        // the minimum the crossing-width clamp already enforces, so the grip stops precisely where the
        // geometry stops changing rather than a DBU either side of it.
        //
        // The assertion is the FLOOR, not merely "positive": a grip that had gone negative and been
        // normalised at mouse up would land on the magnitude of a long drag, which is nowhere near it.
        var vm = PlaceTee();
        var start = ParametersOf(vm);
        double floorMeters = start.Real("W3") / 2.0;      // MTee clamps L1/L2 against W3/2
        var grip = vm.Overlay.PCellHandles.First(h => h.Label == "W1");   // its cross axis is L1

        // Along arm 1, far past the junction. Note the direction: arm 1 runs from pin 1 at the cell
        // origin TO the junction, and its grip is pinned to the junction — so dragging pin 1 AWAY
        // (-X) lengthens it and dragging it THROUGH the junction (+X) is what would turn L1 negative.
        long past = grip.X + 8_000_000;
        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 400_000);
        vm.OnPointerMoved(past, grip.Y, leftDown: true, KeyModifiers.None, hitTolDbu: 400_000);
        vm.OnPointerReleased(past, grip.Y, KeyModifiers.None);

        var after = ParametersOf(vm);
        Assert.Equal(floorMeters, after.Real("L1"), 9);

        // Non-vacuous: the drag really did move L1 — it did not simply refuse to start.
        Assert.NotEqual(start.Real("L1"), after.Real("L1"), 9);
    }

    [Fact]
    public void TheLengthAxisIsBoundedAndTheWidthAxisDeliberatelyIsNot()
    {
        // The asymmetry is the owner's own, and stating it here stops it being "tidied" into
        // consistency later: a width recovers EXACTLY at mouse up, so stopping the grip buys nothing;
        // a length cannot, so it is stopped.
        foreach (var h in Tee(TeeWithLengths()).Handles!)
        {
            Assert.Null(h.Min);                                   // the width axis: free
            Assert.NotNull(h.Cross!.Min);                         // the length axis: bounded
            Assert.True(h.Cross!.Min > 0);
        }

        foreach (var h in Cross(CrossWithLengths()).Handles!)
        {
            Assert.Null(h.Min);
            Assert.True(h.Cross!.Min > 0);
        }
    }

    [Fact]
    public void TheLengthBoundMatchesTheGeneratorsOwnCrossingClamp_ToTheLastDbu()
    {
        // Derived from the same integer and converted down, so the grip cannot stop one DBU short of
        // its own limit — which would read as a grip refusing to reach where the artwork plainly can.
        var p = TeeWithLengths();
        var handles = Tee(p).Handles!;
        int dbu = LayoutUnits.DefaultDbuPerMicron;

        long w3Half = PCellUnits.MetresToDbu(p.Real("W3"), dbu) / 2;
        long wThroughHalf = Math.Max(PCellUnits.MetresToDbu(p.Real("W1"), dbu),
                                     PCellUnits.MetresToDbu(p.Real("W2"), dbu)) / 2;

        foreach (var h in handles.Where(h => h.Cross!.Parameter is "L1" or "L2"))
            Assert.Equal(PCellUnits.DbuToMetres(w3Half, dbu), h.Cross!.Min!.Value, 12);
        foreach (var h in handles.Where(h => h.Cross!.Parameter == "L3"))
            Assert.Equal(PCellUnits.DbuToMetres(wThroughHalf, dbu), h.Cross!.Min!.Value, 12);
    }

    [Fact]
    public void MCross_ALengthGripDraggedPastTheCentre_StopsAtTheCrossingMinimum()
    {
        var vm = PlaceCross();
        var start = ParametersOf(vm);
        // Arm 1 runs +X, so it is the ±Y arms that cross it: MCross clamps L1 against max(W2, W4)/2.
        double floorMeters = Math.Max(start.Real("W2"), start.Real("W4")) / 2.0;
        var grip = vm.Overlay.PCellHandles.First(h => h.Label == "W1");   // cross axis L1

        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 400_000);
        vm.OnPointerMoved(-8_000_000, grip.Y, leftDown: true, KeyModifiers.None, hitTolDbu: 400_000);
        vm.OnPointerReleased(-8_000_000, grip.Y, KeyModifiers.None);

        var after = ParametersOf(vm);
        Assert.Equal(floorMeters, after.Real("L1"), 9);
        Assert.NotEqual(start.Real("L1"), after.Real("L1"), 9);

        foreach (string n in (string[])["W1", "W2", "W3", "W4", "L1", "L2", "L3", "L4"])
            Assert.True(after.Real(n) > 0, $"{n} committed as {after.Real(n)}, which is not a dimension");
    }

    [Fact]
    public void NormalizationIsScopedToTheJunctionCellsOwnDimensions()
    {
        // A table, not a name-shaped rule — MKlopf's Offset is a length whose sign is MEANINGFUL
        // (off-axis either way), and normalising it would silently straighten every offset taper.
        Assert.True(PCellDimensionSign.IsPositiveDimension(MTeePCell.GeneratorId, "L1"));
        Assert.True(PCellDimensionSign.IsPositiveDimension(MCrossPCell.GeneratorId, "W4"));
        Assert.False(PCellDimensionSign.IsPositiveDimension(MKlopfPCell.GeneratorId, "Offset"));
        Assert.False(PCellDimensionSign.IsPositiveDimension(MTeePCell.GeneratorId, "SignalLayer"));

        // Zero is left alone: nothing to recover from it, a drag can legitimately stop on it, and a
        // zero-length arm still draws a valid junction from the arms that remain.
        Assert.Equal(PCellValue.Real(0.0),
                     PCellDimensionSign.Normalize(MTeePCell.GeneratorId, "L1", PCellValue.Real(0.0)));
        Assert.Equal(PCellValue.Real(2e-3),
                     PCellDimensionSign.Normalize(MTeePCell.GeneratorId, "L1", PCellValue.Real(-2e-3)));
        Assert.Equal(PCellValue.Real(-2e-3),
                     PCellDimensionSign.Normalize(MKlopfPCell.GeneratorId, "Offset", PCellValue.Real(-2e-3)));
    }

    private LayoutEditorViewModel PlaceCross() => Place(SymbolKind.MCross, "MCROSS");

    private LayoutEditorViewModel PlaceTee() => Place(SymbolKind.MTee, "MTEE");

    private LayoutEditorViewModel Place(SymbolKind kind, string generatorId)
    {
        var vm = new LayoutEditorViewModel(
            new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 },
            Path.Combine(_workspaceDir, "Doc", "layout", "main.clay"));
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(kind, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(
            _workspaceDir, generatorId, defaults, null, null, PCellLayerSelection.Default);
        vm.Model.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), Mag = 1.0,
        });
        vm.SelectInstance(0);
        return vm;
    }

    private IReadOnlyDictionary<string, PCellValue> ParametersOf(LayoutEditorViewModel vm)
    {
        var res = CellLayoutResolver.Resolve(vm.Model.Instances[0].CellRef, vm.InstanceBaseDir);
        Assert.Equal(CellLayoutState.Resolved, res.State);
        return res.View!.PCellOrigin!.Parameters;
    }
}

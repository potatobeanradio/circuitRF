using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// M1 and gates 1/9/13/14 of brief-pcell-parameter-handles.md — the contract itself, and the one
/// assertion that would otherwise only fail on a user's machine.
/// </summary>
public sealed class PCellHandleContractTests : IDisposable
{
    private readonly string _workspaceDir;

    public PCellHandleContractTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crf-pcell-contract-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        File.WriteAllText(Path.Combine(_workspaceDir, ".cws"), "{}");
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir)) Directory.Delete(_workspaceDir, recursive: true);
    }

    // ── Gate 1: the workspace-breaker ────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The most consequential assertion in this feature, and the cheapest one to get wrong.</b>
    ///
    /// <para>A generated cell's FOLDER NAME is a hash, and every placed <c>LayoutInstance.CellRef</c>
    /// in every existing workspace names that folder. Adding handles to a generator must not move it.
    /// The way it WOULD move is by bumping <c>PCellRegistry.GeneratorVersion</c> — which is exactly
    /// the reflex a contributor has when changing a generator, and which here would rename every
    /// generated cell in the field while every instance still pointed at the old name. Each one would
    /// then render as the "Not Found" placeholder.</para>
    ///
    /// <para>Asserted against a <b>frozen literal</b>, captured from the recipe independently rather
    /// than recomputed from the code under test — a test that computes both sides passes whatever
    /// the code does, which is precisely the failure mode this needs to avoid.</para>
    /// </summary>
    [Theory]
    [InlineData("MLIN_161352b4c533")]
    public void AGeneratedCellsFolderName_IsUnchangedByAddingHandles(string expected)
    {
        var parameters = new Dictionary<string, PCellValue>
        {
            ["W"] = PCellValue.Real(0.0029),
            ["L"] = PCellValue.Real(0.01),
        };

        string cellDir = GeneratedCellStore.GetOrCreate(
            _workspaceDir, "MLIN", parameters, null, null, PCellLayerSelection.Default);

        Assert.Equal(expected, Path.GetFileName(cellDir));
    }

    [Fact]
    public void NoGeneratorsContentKeyWasBumpedForHandles()
    {
        // The direct statement of the rule the frozen name above enforces indirectly: adding a handle
        // to a generator must never bump its content key.
        //
        // MTEE (branch direction), MBEND (the hexagon outline) and MKLOPF (SmoothSteps' end blend
        // clamped rather than skipped) are each >1 for their own pre-existing reason — a fix that
        // genuinely moved geometry, which is exactly what the field is for. They are asserted here by
        // NAME so that a bump for a real geometry change stays deliberate and visible, rather than
        // this test simply being deleted the first time one is needed.
        Assert.Equal("1", PCellRegistry.GeneratorContentKey("MLIN"));
        Assert.Equal("1", PCellRegistry.GeneratorContentKey("MTAPER"));
        Assert.Equal("1", PCellRegistry.GeneratorContentKey("MCROSS"));
        Assert.Equal("2", PCellRegistry.GeneratorContentKey("MTEE"));
        Assert.Equal("2", PCellRegistry.GeneratorContentKey("MBEND"));
        Assert.Equal("2", PCellRegistry.GeneratorContentKey("MKLOPF"));
    }

    // ── The declarations themselves ──────────────────────────────────────────────────────────

    [Fact]
    public void Mlin_DeclaresOneGripPerEdgeMidpoint_EachAnchoredOnTheOppositeEdge()
    {
        var result = MlinPCell.Generate(
            new Dictionary<string, PCellValue> { ["W"] = 0.0004, ["L"] = 0.003 },
            technology: null, PCellLayerSelection.Default);

        var handles = result.Handles!;
        Assert.Equal(4, handles.Count);

        // The pairing is the whole declaration: each grip's anchor is the OPPOSITE edge, so the
        // projection from anchor to grip IS the dimension (not half of it), and pinning the anchor
        // holds that edge still. Asserted per edge rather than per parameter, because two grips share
        // each parameter and only their anchors tell them apart.
        var right = handles.Single(h => h.Parameter == "L" && h.AxisDeg == 0);
        Assert.Equal(3_000_000, right.X);
        Assert.Equal(0, right.AnchorX);

        var left = handles.Single(h => h.Parameter == "L" && h.AxisDeg == 180);
        Assert.Equal(0, left.X);
        Assert.Equal(3_000_000, left.AnchorX);   // anchored on the right edge

        var top = handles.Single(h => h.Parameter == "W" && h.AxisDeg == 90);
        Assert.Equal(200_000, top.Y);
        Assert.Equal(-200_000, top.AnchorY);     // the full width, not half of it

        var bottom = handles.Single(h => h.Parameter == "W" && h.AxisDeg == 270);
        Assert.Equal(-200_000, bottom.Y);
        Assert.Equal(200_000, bottom.AnchorY);

        // R-pch-4b: every one of them holds its own anchor, so "drag this end, keep the other end
        // still" reads the same on all four rather than only where the origin happens to move.
        Assert.All(handles, h => Assert.True(h.KeepAnchorFixed));
    }

    [Fact]
    public void MTaper_DeclaresSixGrips_LengthFromEitherEnd_AndEachWidthFromEitherSide()
    {
        var result = MTaperPCell.Generate(
            new Dictionary<string, PCellValue> { ["W1"] = 0.0003, ["W2"] = 0.001, ["L"] = 0.002 },
            technology: null, PCellLayerSelection.Default);

        var handles = result.Handles!;
        Assert.Equal(6, handles.Count);
        Assert.Equal(6, handles.Select(h => (h.X, h.Y)).ToHashSet().Count);

        // Length from either end. Only the near-end grip needs the anchor pinned — `L` grows toward
        // +X either way, so dragging the NEAR end is "grow, and hold the far end still".
        var lengths = handles.Where(h => h.Parameter == "L").ToList();
        Assert.Equal(2, lengths.Count);
        Assert.Contains(lengths, h => h is { AxisDeg: 0, X: 2_000_000, KeepAnchorFixed: false });
        Assert.Contains(lengths, h => h is { AxisDeg: 180, X: 0, KeepAnchorFixed: true });

        // Each width from either side of its own end cap, anchored on the centreline there — so the
        // two ends never interfere and the trapezoid stays centred on its own axis.
        var w1 = handles.Where(h => h.Parameter == "W1").ToList();
        var w2 = handles.Where(h => h.Parameter == "W2").ToList();
        Assert.All(w1, h => Assert.Equal(0, h.AnchorX));
        Assert.All(w2, h => Assert.Equal(2_000_000, h.AnchorX));
        Assert.Equal([-150_000, 150_000], w1.Select(h => h.Y).Order().ToArray());
        Assert.Equal([-500_000, 500_000], w2.Select(h => h.Y).Order().ToArray());

        // Every one of them is a LENGTH, which is what lets the readout print millimetres and the
        // committed value land on the layout's own snap grid.
        Assert.All(handles, h => Assert.Equal(PCellHandleQuantity.Length, h.Quantity));
    }

    [Fact]
    public void MKlopf_DeclaresThreeGripsAtEachEnd_MiddleShapesTheTaper_EdgesDriveTheImpedance()
    {
        var result = MKlopfPCell.Generate(
            new Dictionary<string, PCellValue>
            {
                ["Z1"] = 50.0, ["Z2"] = 100.0, ["GammaMax"] = 0.05,
                ["L"] = 0.005, ["Offset"] = 0.0005,
            },
            technology: null, PCellLayerSelection.Default);

        var handles = result.Handles!;
        Assert.Equal(6, handles.Count);
        Assert.Equal(6, handles.Select(h => (h.X, h.Y)).ToHashSet().Count);

        // R-pch-4a: the END of a taper genuinely means two things at once — how long, and how far off
        // axis — so the MIDDLE grip is ONE grip driving both rather than two at the same point (which
        // is what an earlier draft had, and which made them impossible to tell apart under the
        // cursor). There is one at each end; the near one holds the far end still (R-pch-4b).
        var middles = handles.Where(h => h.Parameter == "L").ToList();
        Assert.Equal(2, middles.Count);
        Assert.All(middles, h => Assert.Equal("Offset", h.Cross!.Parameter));

        var far = middles.Single(h => h.AxisDeg == 0);
        Assert.Equal(5_000_000, far.X);
        Assert.Equal(500_000, far.Y);       // pin 2 sits at the full lateral offset
        Assert.Equal(5_000_000, far.ProjectedPosition, 0);
        Assert.Equal(500_000, far.ProjectedCrossPosition, 0);

        var near = middles.Single(h => h.AxisDeg == 180);
        Assert.Equal((0L, 0L), (near.X, near.Y));
        Assert.True(near.KeepAnchorFixed);

        // The edge grips drive the terminating IMPEDANCES, because a Klopfenstein taper's width at an
        // end is a consequence of Z, not a parameter of its own. Neither is a length, so neither is
        // snapped to the layout's distance grid.
        Assert.Equal(2, handles.Count(h => h.Parameter == "Z1"));
        Assert.Equal(2, handles.Count(h => h.Parameter == "Z2"));
        Assert.All(handles.Where(h => h.Parameter is "Z1" or "Z2"),
                   h => Assert.Equal(PCellHandleQuantity.Unspecified, h.Quantity));
        Assert.All(middles, h => Assert.Equal(PCellHandleQuantity.Length, h.Quantity));
        Assert.All(middles, h => Assert.Equal(PCellHandleQuantity.Length, h.Cross!.Quantity));
    }

    [Fact]
    public void MKlopf_NearEndIsFixed_SoDraggingTheFarGripStretchesFromPin1()
    {
        // "The other end should stay fixed while the user drags the grip" — true by CONSTRUCTION
        // rather than by arithmetic: R4 puts pin 1 at the cell origin, so no parameter can move it.
        foreach (double l in (double[])[0.002, 0.005, 0.012])
        foreach (double offset in (double[])[0.0, 0.0005, 0.002])
        {
            var result = MKlopfPCell.Generate(
                new Dictionary<string, PCellValue>
                {
                    ["Z1"] = 50.0, ["Z2"] = 100.0, ["GammaMax"] = 0.05,
                    ["L"] = l, ["Offset"] = offset,
                },
                technology: null, PCellLayerSelection.Default);

            var pin1 = result.Pins.Single(p => p.Name == "1");
            Assert.Equal(0, pin1.X);
            Assert.Equal(0, pin1.Y);
        }
    }

    [Fact]
    public void MTee_DeclaresSixGrips_OneAtEachCornerOfTheMetal()
    {
        var result = MTeePCell.Generate(
            new Dictionary<string, PCellValue> { ["W1"] = 0.0003, ["W2"] = 0.0004, ["W3"] = 0.0005 },
            technology: null, PCellLayerSelection.Default);

        var handles = result.Handles!;
        Assert.Equal(6, handles.Count);

        // Coincident grips would be indistinguishable under the cursor — the failure MKlopf's own
        // earlier draft had.
        Assert.Equal(6, handles.Select(h => (h.X, h.Y)).ToHashSet().Count);

        // Two per arm, at that arm's own end cap — the tee's six outward corners.
        Assert.Equal(2, handles.Count(h => h.Parameter == "W1"));
        Assert.Equal(2, handles.Count(h => h.Parameter == "W2"));
        Assert.Equal(2, handles.Count(h => h.Parameter == "W3"));

        // The branch runs along -Y, so its width is measured across X, not with the through arms.
        Assert.Equal([0d, 180d], handles.Where(h => h.Parameter == "W3").Select(h => h.AxisDeg).Order().ToArray());
        Assert.Equal([90d, 270d], handles.Where(h => h.Parameter == "W1").Select(h => h.AxisDeg).Order().ToArray());

        // Each pair sits on the SAME end cap, so both grips of a pair share their anchor.
        foreach (string p in (string[])["W1", "W2", "W3"])
            Assert.Single(handles.Where(h => h.Parameter == p).Select(h => (h.AnchorX, h.AnchorY)).ToHashSet());

        Assert.All(handles, h => Assert.Equal(PCellHandleQuantity.Length, h.Quantity));
    }

    [Fact]
    public void MCross_DeclaresEightGrips_OneAtEachCornerOfTheMetal()
    {
        var result = MCrossPCell.Generate(
            new Dictionary<string, PCellValue>
            {
                ["W1"] = 0.0003, ["W2"] = 0.0004, ["W3"] = 0.0005, ["W4"] = 0.0006,
            },
            technology: null, PCellLayerSelection.Default);

        var handles = result.Handles!;
        Assert.Equal(8, handles.Count);
        Assert.Equal(8, handles.Select(h => (h.X, h.Y)).ToHashSet().Count);

        // Two per arm, at that arm's own end cap — the cross's eight outward corners.
        foreach (string p in (string[])["W1", "W2", "W3", "W4"])
            Assert.Equal(2, handles.Count(h => h.Parameter == p));

        // The ±X arms are edited vertically; the ±Y arms horizontally.
        Assert.Equal([90d, 270d], handles.Where(h => h.Parameter == "W1").Select(h => h.AxisDeg).Order().ToArray());
        Assert.Equal([0d, 180d],  handles.Where(h => h.Parameter == "W2").Select(h => h.AxisDeg).Order().ToArray());
        Assert.Equal([90d, 270d], handles.Where(h => h.Parameter == "W3").Select(h => h.AxisDeg).Order().ToArray());
        Assert.Equal([0d, 180d],  handles.Where(h => h.Parameter == "W4").Select(h => h.AxisDeg).Order().ToArray());

        Assert.All(handles, h => Assert.Equal(PCellHandleQuantity.Length, h.Quantity));
    }

    [Fact]
    public void MBend_DeclaresWidthAndAnAngleGripAtEachPin_ButNotMiter()
    {
        var result = MBendPCell.Generate(
            new Dictionary<string, PCellValue> { ["W"] = 0.0003, ["Angle"] = 90.0, ["Miter"] = 2.0 },
            technology: null, PCellLayerSelection.Default);

        var handles = result.Handles!;
        Assert.Equal(3, handles.Count);

        var w = handles.Single(h => h.Parameter == "W");
        Assert.Equal(150_000, w.Y);

        var angles = handles.Where(h => h.Parameter == "Angle").ToList();
        Assert.Equal(2, angles.Count);
        Assert.All(angles, a => Assert.Equal(PCellHandleKind.Angular, a.Kind));
        Assert.All(angles, a => Assert.True(a.KeepAnchorFixed));

        long stubLen = (long)Math.Round(PCellGeometryHelpers.StubLengthFactor * 300_000, MidpointRounding.AwayFromZero);

        // PIN 2 swings about the PIVOT — its real geometric path, so the anchor is both the correct
        // measurement frame and an accurate hint arc. Asserted rather than assumed, because a grip
        // anchored at the cell origin would still swing, just around the wrong point.
        var atPin2 = angles.Single(a => a.X != 0 || a.Y != 0);
        Assert.Equal(stubLen, atPin2.AnchorX);
        Assert.Equal(0, atPin2.AnchorY);
        Assert.Equal(0, atPin2.AxisDeg);   // `Angle` is measured from +X, so the projection IS it

        // PIN 1 swings about PIN 2, and that asymmetry is forced. Pin 1 sits at (0,0) for every value
        // of Angle, so its bearing from the pivot is always 180 degrees — a grip anchored there is
        // invariant in the parameter and the host would (correctly) refuse the drag as unmeasurable.
        // Anchoring on the end that DOES move is what makes the same parameter reachable from here.
        var atPin1 = angles.Single(a => a is { X: 0, Y: 0 });
        Assert.Equal(atPin2.X, atPin1.AnchorX);
        Assert.Equal(atPin2.Y, atPin1.AnchorY);

        // The relationship is the inscribed-angle one: bearing = Angle/2 off the 180-degree
        // reference. Checked at two values, so a sign flip or a factor-of-two slip fails here rather
        // than as a drag that runs backwards.
        Assert.Equal(45.0, atPin1.ProjectedPosition, precision: 3);
        var at30 = MBendPCell.Generate(
            new Dictionary<string, PCellValue> { ["W"] = 0.0003, ["Angle"] = 30.0, ["Miter"] = 0.0 },
            technology: null, PCellLayerSelection.Default).Handles!
            .Single(h => h.Parameter == "Angle" && h is { X: 0, Y: 0 });
        Assert.Equal(15.0, at30.ProjectedPosition, precision: 3);

        // Miter still gets none: an enumeration wearing a Real's clothes, with no continuum to drag
        // along. Stated in the generator itself so nobody adds one by reflex.
        Assert.DoesNotContain(handles, h => h.Parameter == "Miter");
    }

    [Fact]
    public void NotDraggable_IsStillTheDefault_EvenThoughEveryBuiltInNowOptsIn()
    {
        // Every shipping built-in declares grips now, so the DEFAULT has to be asserted against the
        // contract itself rather than against a cell that happens not to. It is the trailing
        // defaulted parameter that keeps the feature free for a generator that ignores it — in this
        // repository or in anyone's kit.
        var bare = new PCellResult([], []);

        Assert.Null(bare.Handles);
        Assert.Equal(PCellPreviewMode.Auto, bare.Preview);
    }

    [Theory]
    [InlineData("MLIN")]
    [InlineData("MBEND")]
    [InlineData("MTEE")]
    [InlineData("MCROSS")]
    [InlineData("MTAPER")]
    [InlineData("MKLOPF")]
    public void EveryBuiltInNowDeclaresAtLeastOneUsableGrip(string generatorId)
    {
        Assert.True(PCellRegistry.TryGet(generatorId, out var generate));
        var parameters = DefaultsFor(generatorId);
        var result = generate(parameters, null, PCellLayerSelection.Default);

        Assert.NotNull(result.Handles);
        Assert.NotEmpty(result.Handles!);
    }

    [Fact]
    public void EveryDeclaredHandle_NamesAParameterItsOwnGeneratorReceives()
    {
        // R2's one list. A handle naming something outside it is a defect in the cell; catching it
        // here means no shipping built-in can develop one unnoticed.
        foreach (string id in (string[])["MLIN", "MBEND", "MTEE", "MCROSS", "MTAPER", "MKLOPF"])
        {
            Assert.True(PCellRegistry.TryGet(id, out var generate));
            var parameters = DefaultsFor(id);
            var result = generate(parameters, null, PCellLayerSelection.Default);

            foreach (var handle in result.Handles ?? [])
            {
                Assert.Equal(PCellHandleRejection.None, PCellHandleSolver.Validate(handle, parameters));
                // A cross axis is a parameter too, and a wrong one there would be just as silent.
                if (handle.Cross is not null)
                    Assert.Equal(PCellHandleRejection.None,
                        PCellHandleSolver.Validate(handle.AsCrossHandle(), parameters));
            }
        }
    }

    // ── Gate 9: determinism, end to end ──────────────────────────────────────────────────────

    [Fact]
    public void TheSameSolveTwice_ResolvesToTheSameGeneratedCellFolder()
    {
        // R-pch-11's consequence, stated where it actually bites: a value differing in its last digit
        // between two identical drags mints a SECOND cell folder for one design intent, silently
        // defeating R6's sharing.
        Assert.True(PCellRegistry.TryGet("MLIN", out var generate));
        var start = DefaultsFor("MLIN");
        var handle = generate(start, null, PCellLayerSelection.Default).Handles!
            .Single(h => h.Parameter == "L" && h.AxisDeg == 0);   // MLIN has one L grip per end

        PCellResult Gen(IReadOnlyDictionary<string, PCellValue> p)
            => generate(p, null, PCellLayerSelection.Default);

        PCellHandleSolver.MeasureSensitivity(Gen, start, handle, 0, out double vpp, out _);
        var a = PCellHandleSolver.Solve(Gen, start, handle, 0, 4_321_000, vpp);
        var b = PCellHandleSolver.Solve(Gen, start, handle, 0, 4_321_000, vpp);

        string dirA = GeneratedCellStore.GetOrCreate(_workspaceDir, "MLIN",
            With(start, "L", a.Value), null, null, PCellLayerSelection.Default);
        string dirB = GeneratedCellStore.GetOrCreate(_workspaceDir, "MLIN",
            With(start, "L", b.Value), null, null, PCellLayerSelection.Default);

        Assert.Equal(dirA, dirB, StringComparer.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyDictionary<string, PCellValue> With(
        IReadOnlyDictionary<string, PCellValue> baseParameters, string name, PCellValue value)
        => new Dictionary<string, PCellValue>(baseParameters, StringComparer.Ordinal) { [name] = value };

    private static IReadOnlyDictionary<string, PCellValue> DefaultsFor(string generatorId)
        => PCellParameters.FromReals(generatorId switch
        {
            "MLIN"   => new() { ["W"] = 300e-6, ["L"] = 2e-3 },
            "MBEND"  => new() { ["W"] = 300e-6, ["Angle"] = 90, ["Miter"] = 2 },
            "MTEE"   => new() { ["W1"] = 300e-6, ["W2"] = 300e-6, ["W3"] = 500e-6 },
            "MCROSS" => new() { ["W1"] = 300e-6, ["W2"] = 300e-6, ["W3"] = 300e-6, ["W4"] = 300e-6 },
            "MTAPER" => new() { ["W1"] = 300e-6, ["W2"] = 1e-3, ["L"] = 2e-3 },
            "MKLOPF" => new() { ["Z1"] = 50, ["Z2"] = 100, ["L"] = 5e-3, ["GammaMax"] = 0.05, ["Offset"] = 1e-4 },
            _        => new Dictionary<string, double>(),
        });
}

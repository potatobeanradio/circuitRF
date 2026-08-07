using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Layout.PCells.Wire;
using CircuitRF.Ui.Messages;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// M5 and M6 of brief-pcell-parameter-handles.md — the preview budget, and design §8's degradation
/// table in full.
///
/// <para>Every row of that table has the same shape: the grip is dropped, the reason is said once,
/// and the parameter stays editable in the Properties Inspector. <b>None of them blocks editing and
/// none of them is silent</b> — that pairing is the rule the whole PCell area runs on, and it is
/// what these assert.</para>
///
/// <para>The generators here are synthetic and registered through the ordinary resolver seam, which
/// is the only way to produce a deliberately-broken declaration: the built-in registry is closed,
/// and none of the shipping cells declares a handle that is wrong.</para>
/// </summary>
[Collection(PCellResolverCollection.Name)]
public sealed class PCellHandleDegradationTests : IDisposable
{
    private readonly string _workspaceDir;
    private readonly RecordingSink _sink = new();

    public PCellHandleDegradationTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crf-pcell-degrade-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        File.WriteAllText(Path.Combine(_workspaceDir, ".cws"), "{}");
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        PCellRegistry.ClearResolvers();
        PCellRegistry.AddResolver(new SyntheticResolver());
    }

    public void Dispose()
    {
        PCellRegistry.ClearResolvers();
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir)) Directory.Delete(_workspaceDir, recursive: true);
    }

    // ── The synthetic kit ────────────────────────────────────────────────────────────────────

    private const string GoodId       = "SYN_GOOD";
    private const string UnknownParam = "SYN_UNKNOWNPARAM";
    private const string TextParam    = "SYN_TEXTPARAM";
    private const string DeadGrip     = "SYN_DEAD";
    private const string AngularGrip  = "SYN_ANGULAR";
    private const string SlowGrip     = "SYN_SLOW";
    private const string DeclaredSlow = "SYN_DECLAREDSLOW";
    private const string TwoAxis      = "SYN_TWOAXIS";

    /// <summary>How long <see cref="SlowGrip"/> takes per generate. Comfortably past the one-frame
    /// budget, so the deferred decision is not a coin flip on a loaded machine.</summary>
    private static readonly TimeSpan SlowGenerateTime = TimeSpan.FromMilliseconds(60);

    private sealed class SyntheticResolver : IPCellGeneratorResolver
    {
        public IReadOnlyCollection<string> KnownGeneratorIds =>
            [GoodId, UnknownParam, TextParam, DeadGrip, AngularGrip, SlowGrip, DeclaredSlow, TwoAxis];

        public string Describe() => "synthetic test generators";
        public string? ContentKeyFor(string id) => KnownGeneratorIds.Contains(id) ? "synthetic-1" : null;

        public IReadOnlyDictionary<string, PCellValue>? DeclaredDefaults(string id) => id switch
        {
            TextParam => new Dictionary<string, PCellValue> { ["Model"] = PCellValue.Text("nch") },
            TwoAxis => new Dictionary<string, PCellValue>
            {
                ["L"] = PCellValue.Real(0.002), ["Off"] = PCellValue.Real(0.0),
            },
            // An ANGLE, in degrees — not a length. Angular grips exist precisely for parameters that
            // are not lengths, so the synthetic that exercises them must not be one.
            AngularGrip => new Dictionary<string, PCellValue> { ["A"] = PCellValue.Real(30.0) },
            _ when KnownGeneratorIds.Contains(id) => new Dictionary<string, PCellValue> { ["L"] = PCellValue.Real(0.002) },
            _ => null,
        };

        public PCellGenerator? Resolve(string id)
        {
            if (!KnownGeneratorIds.Contains(id)) return null;
            return (p, tech, layers) =>
            {
                if (id == SlowGrip) Thread.Sleep(SlowGenerateTime);

                long l = PCellUnits.MetresToDbu(p.Real("L", 0.002), LayoutUnits.DefaultDbuPerMicron);
                // A two-axis cell: the far end moves out along +X with L and up with Off, so a
                // diagonal drag has to move BOTH for the artwork to follow the cursor. Trivially
                // fast on purpose — this is the deterministic stand-in for "is the live path
                // actually carrying both axes", a question a real cell can only answer against a
                // clock.
                long off = PCellUnits.MetresToDbu(p.Real("Off", 0.0), LayoutUnits.DefaultDbuPerMicron);

                // The angular cell: an arm of fixed length at angle A about the origin. Its grip
                // genuinely SWINGS, which is what makes it a real exercise of the Angular path rather
                // than a Linear one wearing the wrong kind.
                if (id == AngularGrip)
                {
                    double aRad = p.Real("A", 30.0) * (Math.PI / 180.0);
                    const long arm = 1_000_000;
                    long tipX = (long)Math.Round(arm * Math.Cos(aRad), MidpointRounding.AwayFromZero);
                    long tipY = (long)Math.Round(arm * Math.Sin(aRad), MidpointRounding.AwayFromZero);
                    var armShape = new RectShape
                    {
                        Layer = new LayerKey(1, 0),
                        X1 = Math.Min(0, tipX), Y1 = Math.Min(0, tipY),
                        X2 = Math.Max(0, tipX), Y2 = Math.Max(0, tipY),
                    };
                    var swing = new PCellHandle("A", 0, 0, tipX, tipY, AxisDeg: 0,
                                                Kind: PCellHandleKind.Angular);
                    return new PCellResult([armShape], [], Handles: [swing]);
                }

                var shape = id == TwoAxis
                    ? new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = off - 150_000, X2 = l, Y2 = off + 150_000 }
                    : new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = -150_000, X2 = l, Y2 = 150_000 };

                PCellHandle handle = id switch
                {
                    UnknownParam => new PCellHandle("NotAParameter", 0, 0, l, 0, 0),
                    TextParam    => new PCellHandle("Model", 0, 0, l, 0, 0),
                    // The grip never moves, whatever L says — the declaration and the geometry disagree.
                    DeadGrip     => new PCellHandle("L", 0, 0, 2_000_000, 0, 0),
                    TwoAxis      => new PCellHandle("L", 0, 0, l, off, 0,
                                        Cross: new PCellHandleCrossAxis("Off")),
                    _            => new PCellHandle("L", 0, 0, l, 0, 0),
                };

                // R-pch-10: a generator that already knows it is expensive says so, and is believed
                // without the host spending a regeneration to find out. This one is FAST — the point
                // is that the declaration alone is enough.
                var preview = id == DeclaredSlow ? PCellPreviewMode.Deferred : PCellPreviewMode.Auto;
                return new PCellResult([shape], [], Handles: [handle], Preview: preview);
            };
        }
    }

    private sealed class RecordingSink : IMessageSink
    {
        public List<string> Warnings { get; } = [];

        public void Post(MessageLevel level, string text, string? filePath = null)
        {
            if (level == MessageLevel.Warning) Warnings.Add(text);
        }

        public void Clear() => Warnings.Clear();
    }

    private (LayoutEditorViewModel Vm, string CellRef) Place(string generatorId)
    {
        var vm = new LayoutEditorViewModel(
            new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 },
            Path.Combine(_workspaceDir, "Doc", "layout", "main.clay"),
            messageSink: _sink);

        var defaults = PCellRegistry.DeclaredDefaults(generatorId)!;
        string cellDir = GeneratedCellStore.GetOrCreate(
            _workspaceDir, generatorId, defaults, null, null, PCellLayerSelection.Default);
        string cellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir);

        vm.Model.Instances.Add(new LayoutInstance { CellRef = cellRef, Mag = 1.0 });
        vm.SelectInstance(0);
        return (vm, cellRef);
    }

    // ── Control: the synthetic kit itself works ──────────────────────────────────────────────

    [Fact]
    public void AWellFormedSyntheticGenerator_ShowsAndDragsItsGrip()
    {
        var (vm, cellRef) = Place(GoodId);
        var grip = Assert.Single(vm.Overlay.PCellHandles);

        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 200_000);
        vm.OnPointerMoved(5_000_000, 0, leftDown: true, KeyModifiers.None, hitTolDbu: 200_000);
        vm.OnPointerReleased(5_000_000, 0, KeyModifiers.None);

        Assert.NotEqual(cellRef, vm.Model.Instances[0].CellRef);
        Assert.Empty(_sink.Warnings);   // a working cell says nothing
    }

    // ── Design §8, row by row ────────────────────────────────────────────────────────────────

    [Fact]
    public void AHandleNamingAnUndeclaredParameter_IsDroppedAndReportedByName()
    {
        var (vm, _) = Place(UnknownParam);

        Assert.Empty(vm.Overlay.PCellHandles);
        Assert.Contains(_sink.Warnings, w => w.Contains("NotAParameter") && w.Contains(UnknownParam));
    }

    [Fact]
    public void AHandleOnATextParameter_IsDroppedAndReported()
    {
        var (vm, _) = Place(TextParam);

        Assert.Empty(vm.Overlay.PCellHandles);
        Assert.Contains(_sink.Warnings, w => w.Contains("Model"));
    }

    [Fact]
    public void AnAngularHandle_SwingsAboutItsAnchor_AndCommitsTheAngleItLandsOn()
    {
        // This test used to assert the OPPOSITE — that Angular was dropped as unsupported. That was
        // true while the kind was declared and not implemented; it is now false, so the test asserts
        // the capability rather than the gap. R-pch-6's drop-and-report path is still live and still
        // covered — by the WIRE decoder's unknown-kind test, which is the only place an unrecognised
        // kind can now arrive from.
        var (vm, cellRef) = Place(AngularGrip);
        var grip = Assert.Single(vm.Overlay.PCellHandles);

        // Drag from 30° round to (near) 90°: straight up, at the arm's own radius.
        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 200_000);
        vm.OnPointerMoved(0, 1_000_000, leftDown: true, KeyModifiers.None, hitTolDbu: 200_000);
        vm.OnPointerReleased(0, 1_000_000, KeyModifiers.None);

        Assert.NotEqual(cellRef, vm.Model.Instances[0].CellRef);   // it committed something
        Assert.Empty(_sink.Warnings);

        var res = CellLayoutResolver.Resolve(vm.Model.Instances[0].CellRef, vm.InstanceBaseDir);
        Assert.Equal(CellLayoutState.Resolved, res.State);
        Assert.Equal(90.0, res.View!.PCellOrigin!.Parameters["A"].AsReal(), precision: 1);
    }

    [Fact]
    public void AGripThatDoesNotMove_IsShownButRefusesToDrag_AndSaysWhy()
    {
        // Validation cannot catch this — the declaration is well formed and the parameter is a real
        // number. It only surfaces when the probe asks the generator and nothing happens, which is
        // at drag time.
        var (vm, cellRef) = Place(DeadGrip);
        var grip = Assert.Single(vm.Overlay.PCellHandles);

        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 200_000);
        vm.OnPointerMoved(5_000_000, 0, leftDown: true, KeyModifiers.None, hitTolDbu: 200_000);
        vm.OnPointerReleased(5_000_000, 0, KeyModifiers.None);

        Assert.Equal(cellRef, vm.Model.Instances[0].CellRef);   // design unchanged
        Assert.Contains(_sink.Warnings, w => w.Contains("L") && w.Contains(DeadGrip));
    }

    [Fact]
    public void ARejection_IsReportedOncePerSession_NotOncePerRepaint()
    {
        var (vm, _) = Place(UnknownParam);

        // Twenty rebuilds — a cell with a bad declaration must not fill the Messages pane.
        for (int i = 0; i < 20; i++) vm.SelectInstance(0);

        Assert.Single(_sink.Warnings);
    }

    // ── M5: the preview budget (R-pch-10) ────────────────────────────────────────────────────

    [Fact]
    public void AFastCell_PreviewsLiveArtwork()
    {
        var (vm, _) = Place(GoodId);
        var grip = Assert.Single(vm.Overlay.PCellHandles);

        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 200_000);
        vm.OnPointerMoved(5_000_000, 0, leftDown: true, KeyModifiers.None, hitTolDbu: 200_000);

        Assert.False(vm.PCellHandleDragIsDeferred);
        Assert.NotNull(vm.Overlay.PCellHandlePreview);
    }

    [Fact]
    public void ASlowCell_FallsBackToDeferredPreview_AndStillCommitsTheRightValue()
    {
        // Gate 11. The drag must not stutter behind a cell that cannot regenerate inside a frame —
        // and correctness must not depend on how fast the generator is.
        var (vm, cellRef) = Place(SlowGrip);
        var grip = Assert.Single(vm.Overlay.PCellHandles);

        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 200_000);
        vm.OnPointerMoved(5_000_000, 0, leftDown: true, KeyModifiers.None, hitTolDbu: 200_000);

        Assert.True(vm.PCellHandleDragIsDeferred);
        Assert.Null(vm.Overlay.PCellHandlePreview);       // pre-drag artwork stands...
        Assert.StartsWith("L =", vm.DrawReadoutText);     // ...but the readout still tracks

        vm.OnPointerReleased(5_000_000, 0, KeyModifiers.None);

        Assert.NotEqual(cellRef, vm.Model.Instances[0].CellRef);
        var res = CellLayoutResolver.Resolve(vm.Model.Instances[0].CellRef, vm.InstanceBaseDir);
        Assert.Equal(0.005, res.View!.PCellOrigin!.Parameters.Real("L"), 6);
    }

    [Fact]
    public void ADeferredDrag_StopsRegeneratingArtworkPerPointerMove()
    {
        // The counter, not the clock — the same convention every other cost claim here uses.
        var (vmFast, _) = Place(GoodId);
        var fastGrip = Assert.Single(vmFast.Overlay.PCellHandles);
        vmFast.OnPointerPressed(fastGrip.X, fastGrip.Y, KeyModifiers.None, hitTolDbu: 200_000);
        for (int i = 1; i <= 6; i++)
            vmFast.OnPointerMoved(3_000_000 + i * 200_000, 0, leftDown: true, KeyModifiers.None, hitTolDbu: 200_000);
        int fastCount = vmFast.PCellHandlePreviewGenerateCount;

        var (vmSlow, _) = Place(SlowGrip);
        var slowGrip = Assert.Single(vmSlow.Overlay.PCellHandles);
        vmSlow.OnPointerPressed(slowGrip.X, slowGrip.Y, KeyModifiers.None, hitTolDbu: 200_000);
        for (int i = 1; i <= 6; i++)
            vmSlow.OnPointerMoved(3_000_000 + i * 200_000, 0, leftDown: true, KeyModifiers.None, hitTolDbu: 200_000);

        // The deferred drag still SOLVES (the readout has to stay honest) but stops building ghost
        // artwork, so it does strictly less work per move than the live one.
        Assert.True(vmSlow.PCellHandlePreviewGenerateCount < fastCount,
            $"deferred drag generated {vmSlow.PCellHandlePreviewGenerateCount}, live generated {fastCount}");
    }

    [Fact]
    public void AGeneratorDeclaringDeferredPreview_IsBelievedWithoutBeingTimed()
    {
        // The whole saving: this generator is FAST, so Auto would have chosen live preview. It gets
        // deferred anyway, because it said so — and it never pays the one full regeneration Auto
        // spends measuring.
        var (vm, cellRef) = Place(DeclaredSlow);
        var grip = Assert.Single(vm.Overlay.PCellHandles);

        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 200_000);
        vm.OnPointerMoved(5_000_000, 0, leftDown: true, KeyModifiers.None, hitTolDbu: 200_000);

        Assert.True(vm.PCellHandleDragIsDeferred);
        Assert.Null(vm.Overlay.PCellHandlePreview);
        Assert.StartsWith("L =", vm.DrawReadoutText);

        vm.OnPointerReleased(5_000_000, 0, KeyModifiers.None);

        // Declaring a preference must not change the ANSWER, only how it is drawn on the way there.
        Assert.NotEqual(cellRef, vm.Model.Instances[0].CellRef);
        var res = CellLayoutResolver.Resolve(vm.Model.Instances[0].CellRef, vm.InstanceBaseDir);
        Assert.Equal(0.005, res.View!.PCellOrigin!.Parameters.Real("L"), 6);
    }

    [Fact]
    public void ADeclaredDeferredDrag_CommitsTheSameValueAsAnIdenticalAutoDrag()
    {
        // Stated as a comparison rather than an absolute, because the claim is "the preview mode is
        // presentation only" — which only a side-by-side can actually show.
        var (autoVm, _) = Place(GoodId);
        var autoGrip = Assert.Single(autoVm.Overlay.PCellHandles);
        vm_Drag(autoVm, autoGrip);
        double autoValue = ValueOf(autoVm);

        var (declaredVm, _) = Place(DeclaredSlow);
        var declaredGrip = Assert.Single(declaredVm.Overlay.PCellHandles);
        vm_Drag(declaredVm, declaredGrip);
        double declaredValue = ValueOf(declaredVm);

        Assert.Equal(autoValue.ToString("R"), declaredValue.ToString("R"));

        static void vm_Drag(LayoutEditorViewModel vm, PCellHandleMarker grip)
        {
            vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 200_000);
            vm.OnPointerMoved(5_432_000, 0, leftDown: true, KeyModifiers.None, hitTolDbu: 200_000);
            vm.OnPointerReleased(5_432_000, 0, KeyModifiers.None);
        }

        static double ValueOf(LayoutEditorViewModel vm)
            => CellLayoutResolver.Resolve(vm.Model.Instances[0].CellRef, vm.InstanceBaseDir)
                   .View!.PCellOrigin!.Parameters.Real("L");
    }

    // ── R-pch-4a: the LIVE preview of a two-axis drag ────────────────────────────────────────

    [Fact]
    public void ADiagonalTwoAxisDrag_RendersBothAxesInTheLiveGhost()
    {
        // The claim under test is that the live ghost is built from BOTH solved values, not just the
        // primary — so a diagonal drag redraws the artwork as moved AND stretched, not merely
        // stretched. Driven by a deliberately trivial generator so the answer cannot depend on
        // whether the budget happened to trip; the real-cell timing is measured separately.
        var (vm, _) = Place(TwoAxis);
        var grip = Assert.Single(vm.Overlay.PCellHandles);

        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 500_000);
        vm.OnPointerMoved(5_000_000, 1_200_000, leftDown: true, KeyModifiers.None, hitTolDbu: 500_000);

        Assert.False(vm.PCellHandleDragIsDeferred);
        var preview = vm.Overlay.PCellHandlePreview;
        Assert.NotNull(preview);

        var bbox = LayoutGeometry.BboxOf(preview!.Value.GhostView.Shapes[0]);
        Assert.Equal(5_000_000, bbox.MaxX);                    // the L axis reached the cursor...
        Assert.Equal(1_200_000, (bbox.MinY + bbox.MaxY) / 2);  // ...and so did the Off axis
    }

    [Fact]
    public void ATwoAxisReadout_ShowsBothParameters_InEitherPreviewMode()
    {
        // The readout is what a DEFERRED drag steers by, so it has to carry both axes whether or not
        // the artwork is being redrawn. Asserted for the live path here; the deferred path's own
        // readout is covered by the declared-deferred test above.
        var (vm, _) = Place(TwoAxis);
        var grip = Assert.Single(vm.Overlay.PCellHandles);

        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 500_000);
        vm.OnPointerMoved(5_000_000, 1_200_000, leftDown: true, KeyModifiers.None, hitTolDbu: 500_000);

        Assert.Contains("L =", vm.DrawReadoutText);
        Assert.Contains("Off =", vm.DrawReadoutText);
    }

    [Fact]
    public void AGeneratorThatThrowsWhileProbing_LeavesTheDesignUnchanged()
    {
        var (vm, cellRef) = Place(GoodId);
        var grip = Assert.Single(vm.Overlay.PCellHandles);

        // Pull the generator out from under the drag: the resolver stops answering, so the probe's
        // regeneration fails. The design must survive that untouched.
        PCellRegistry.ClearResolvers();
        PCellRegistry.AddResolver(new BrokenResolver());

        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 200_000);
        vm.OnPointerMoved(5_000_000, 0, leftDown: true, KeyModifiers.None, hitTolDbu: 200_000);
        vm.OnPointerReleased(5_000_000, 0, KeyModifiers.None);

        Assert.Equal(cellRef, vm.Model.Instances[0].CellRef);
    }

    private sealed class BrokenResolver : IPCellGeneratorResolver
    {
        public IReadOnlyCollection<string> KnownGeneratorIds => [GoodId];
        public string Describe() => "a generator that has stopped working";
        public string? ContentKeyFor(string id) => id == GoodId ? "synthetic-1" : null;
        public PCellGenerator? Resolve(string id) => id == GoodId
            ? (_, _, _) => throw new PCellWireException("the script stopped answering")
            : null;
    }
}

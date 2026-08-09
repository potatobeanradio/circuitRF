using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// The owner's MKlopf round: the end-cap grips not following a drag of the middle one, the outline
/// going "extremely thin in the middle" for a small change in Z2, and a grip able to drag the length
/// negative.
///
/// <para>The middle two are the same defect seen from two directions — see
/// <see cref="MKlopfPCell"/>'s own <c>GammaMaxHeadroom</c> for why the profile silently collapses
/// rather than failing.</para>
/// </summary>
public sealed class MKlopfGripAndProfileTests : IDisposable
{
    private readonly string _workspaceDir;

    public MKlopfGripAndProfileTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crf-mklopf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        File.WriteAllText(Path.Combine(_workspaceDir, ".cws"), "{}");
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir)) Directory.Delete(_workspaceDir, recursive: true);
    }

    // ── The reported geometry: Z1=8.35209, Z2=7.54845 works; 7.56359 collapses ──────────────
    //
    // The bound the profile needs GammaMax to sit under is |0.5*ln(Z2/Z1)|, which for these two
    // impedances is 0.050583 — barely above the stated GammaMax of 0.05. Nudging Z2 up by 0.015 ohm
    // drops the bound to 0.049581 and puts GammaMax over it, at which point acosh's argument falls
    // below 1 and the shape parameter is NaN.

    private static Dictionary<string, PCellValue> OwnerParameters(double z2) => new(StringComparer.Ordinal)
    {
        ["Z1"]           = PCellValue.Real(8.35209),
        ["Z2"]           = PCellValue.Real(z2),
        ["GammaMax"]     = PCellValue.Real(0.05),
        ["L"]            = PCellValue.Real(618e-6),
        ["Offset"]       = PCellValue.Real(-257e-6),
        ["SmoothSteps"]  = PCellValue.Real(1),
    };

    /// <summary>
    /// Trace width at each station, read back off the emitted outline. The polygon is the left edge
    /// forward then the right edge back, so station <c>i</c>'s two edge points are <c>i</c> and
    /// <c>2n+1-i</c> — the width is the distance between them.
    /// </summary>
    private static List<double> StationWidths(PCellResult r)
    {
        var xy = ((PolygonShape)r.Shapes[0]).Xy;
        int points = xy.Length / 2;
        var widths = new List<double>(points / 2);
        for (int i = 0; i < points / 2; i++)
        {
            int j = points - 1 - i;
            double dx = xy[2 * i] - xy[2 * j], dy = xy[2 * i + 1] - xy[2 * j + 1];
            widths.Add(Math.Sqrt(dx * dx + dy * dy));
        }
        return widths;
    }

    [Fact]
    public void AGammaMaxAtItsOwnBound_DoesNotCollapseTheOutline_AndSaysWhy()
    {
        var r = MKlopfPCell.Generate(OwnerParameters(7.56359), null, PCellLayerSelection.Default);

        var widths = StationWidths(r);
        Assert.All(widths, w => Assert.True(double.IsFinite(w) && w > 0, $"width {w} is not a width"));

        // The two ends are synthesised from Z1/Z2 directly and were correct even with the bug — it
        // was the INTERIOR that collapsed to the synthesis range's own narrowest width. Half the
        // narrower end is far below anything this taper legitimately reaches (its two impedances are
        // within 10% of each other) and far above the collapsed value, which was ~1/2000 of it.
        double ends = Math.Min(widths[0], widths[^1]);
        double narrowest = widths.Min();
        Assert.True(narrowest > ends * 0.5,
            $"the taper collapsed in the middle: narrowest {narrowest:0.###e+00} m against ends {ends:0.###e+00} m");

        Assert.Contains(r.Diagnostics ?? [], d => d.Contains("GammaMax", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSameTaperJustInsideTheBound_IsUnchangedAndSaysNothing()
    {
        // The control, and it is what keeps the test above honest: 7.54845 leaves GammaMax legally
        // under the bound, so nothing is clamped and nothing is reported. A fix that clamped
        // unconditionally would pass the first test and fail this one.
        var r = MKlopfPCell.Generate(OwnerParameters(7.54845), null, PCellLayerSelection.Default);

        Assert.All(StationWidths(r), w => Assert.True(double.IsFinite(w) && w > 0));
        Assert.DoesNotContain(r.Diagnostics ?? [], d => d.Contains("GammaMax", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoImpedancesThatAreEqual_DrawAUniformLine_RatherThanNothing()
    {
        // No transformation means no reflection bound, so there is no GammaMax that makes the shape
        // parameter finite — acosh(0) is NaN whatever is passed. Answered as the uniform line it is.
        var p = OwnerParameters(8.35209);
        var r = MKlopfPCell.Generate(p, null, PCellLayerSelection.Default);

        var widths = StationWidths(r);
        Assert.All(widths, w => Assert.True(double.IsFinite(w) && w > 0));
        Assert.True(widths.Max() - widths.Min() < widths[0] * 1e-6, "a taper between equal impedances is uniform");
        Assert.NotEmpty(r.Diagnostics ?? []);
    }

    [Fact]
    public void ANonPositiveLength_IsReportedAndDrawn_RatherThanProducingNothing()
    {
        var p = OwnerParameters(7.54845);
        p["L"] = PCellValue.Real(-1e-4);

        var r = MKlopfPCell.Generate(p, null, PCellLayerSelection.Default);

        Assert.NotEmpty(r.Shapes);
        Assert.All(StationWidths(r), w => Assert.True(double.IsFinite(w) && w > 0));
        Assert.Contains(r.Diagnostics ?? [], d => d.Contains("L must be a positive length", StringComparison.Ordinal));
    }

    // ── The grips ───────────────────────────────────────────────────────────────────────────

    private LayoutEditorViewModel PlaceMklopf()
    {
        var vm = new LayoutEditorViewModel(
            new LayoutView { DbuPerMicron = 1000, SnapDbu = 1_000 },
            Path.Combine(_workspaceDir, "Doc", "layout", "main.clay"));
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mklopf, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(
            _workspaceDir, "MKLOPF", defaults, null, null, PCellLayerSelection.Default);
        vm.Model.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), Mag = 1.0,
        });
        vm.SelectInstance(0);
        return vm;
    }

    private static IReadOnlyDictionary<string, PCellValue> ParametersOf(LayoutEditorViewModel vm)
        => CellLayoutResolver.Resolve(vm.Model.Instances[0].CellRef, vm.InstanceBaseDir)
                             .View!.PCellOrigin!.Parameters;

    [Fact]
    public void DraggingTheFarMiddleGrip_MovesTheFarEndCapGripsLive_NotOnlyOnRelease()
    {
        // The reported one. Every grip on a cell is a function of the same parameter set, so all of
        // them move when it regenerates — but only the dragged one was being read from the preview,
        // leaving the end-cap grips on the outline's OLD position until release put them right.
        var vm = PlaceMklopf();

        int far = Array.FindIndex([.. vm.Overlay.PCellHandles], h => h.Label == "L" && h.AxisDx > 0);
        Assert.True(far >= 0);

        // An end-cap grip at the same end: it sits on the far end's own outline edge, so it travels
        // with the length even though the length is not what it drives.
        long farX = vm.Overlay.PCellHandles[far].X;
        int cap = Array.FindIndex([.. vm.Overlay.PCellHandles],
                                  h => h.Label != "L" && Math.Abs(h.AnchorX - farX) < 1_000);
        Assert.True(cap >= 0, "expected an end-cap grip anchored at the far end");

        long capBefore = vm.Overlay.PCellHandles[cap].X;

        var g = vm.Overlay.PCellHandles[far];
        vm.OnPointerPressed(g.X, g.Y, KeyModifiers.None, hitTolDbu: 20_000);
        vm.OnPointerMoved(g.X + 300_000, g.Y, leftDown: true, KeyModifiers.None, hitTolDbu: 20_000);

        long capDuring = vm.Overlay.PCellHandles[cap].X;
        Assert.True(capDuring > capBefore,
            $"the end-cap grip should follow the artwork mid-drag, but stayed at {capDuring}");

        vm.OnPointerReleased(g.X + 300_000, g.Y, KeyModifiers.None);
        Assert.Equal(capDuring, vm.Overlay.PCellHandles[cap].X);   // release moves it no further
    }

    [Fact]
    public void DraggingTheLengthGripPastTheOtherEnd_StopsAtAPositiveLength()
    {
        var vm = PlaceMklopf();

        int far = Array.FindIndex([.. vm.Overlay.PCellHandles], h => h.Label == "L" && h.AxisDx > 0);
        var g = vm.Overlay.PCellHandles[far];

        // Well past the near end, which is where a negative length would come from.
        long to = g.X - 10_000_000;
        vm.OnPointerPressed(g.X, g.Y, KeyModifiers.None, hitTolDbu: 20_000);
        vm.OnPointerMoved(to, g.Y, leftDown: true, KeyModifiers.None, hitTolDbu: 20_000);
        vm.OnPointerReleased(to, g.Y, KeyModifiers.None);

        double committed = ParametersOf(vm).Real("L");
        Assert.True(committed > 0, $"L was dragged to {committed:0.###e+00} m, which is not a length");
    }
}

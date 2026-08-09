using CircuitRF.Ui.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-L5-followups-2.md §2 — MKlopf's smoothing "blip" and R-klp-10's
/// curvature warning.
///
/// <b>§2.1 (end steps): verified correct, NOT a bug.</b> <c>KlopfensteinTaper.ImpedanceAt</c> is
/// already oracle-cross-checked (<c>KlopfensteinTaperTests.cs</c>, github.com/ZiadHatab/klopf-taper
/// BSD-3) and its own doc comment states directly: "t=0 → Z1, t=1 → Z2, exactly... the model NEVER
/// smooths these" — R-klp-4's design. Sampling the profile at t slightly greater than 0 shows a real,
/// large first step (confirmed numerically: at the R-L5g-4 worked example, Z jumps from exactly
/// Z1=50.000Ω at the very first sample to 52.729Ω at the second — a genuine ±ρ0-scale discontinuity,
/// not a bug) — this IS "the design working," matching the brief's own anticipated outcome.
///
/// <b>§2.2 (the blip): root-caused precisely, NOT fixed.</b> Confirmed by direct numeric
/// reconstruction of <c>MKlopfPCell.Generate</c>'s own per-station math (Z1=50,Z2=100,Γmax=0.05,
/// L=200mm,Offset=100mm, SmoothSteps=1): the BLENDED WIDTH SCALAR is itself monotonically decreasing
/// near port 1 (2998.20 → 2966.88 → 2891.16 → 2798.34 → 2715.74 → 2670.72 µm across the blend's own 6
/// stations) — the blend's own cubic Hermite math is not the bug. The RENDERED OUTLINE EDGE, however,
/// is genuinely non-monotonic there: the left-edge Y coordinate (<c>y(x) + cos(tangent(x))·width(x)/2</c>)
/// DIPS from 1.4991mm (station 0) down to 1.4254mm (station 4) and then RISES back past 1.4991mm by
/// station 11 — a real "bump and back," because the width blend is computed purely as a function of
/// the WIDTH SCALAR and never accounts for how the underlying offset centerline's own Y-position and
/// tangent angle are SIMULTANEOUSLY evolving over that same short span. A genuinely correct fix needs
/// to blend the OUTLINE EDGE's own position (or an outline-consistent width target), not just the
/// width value in isolation — real PCell-geometry design work needing visual verification this
/// environment cannot provide. NOT attempted here, reported per this codebase's own "investigate
/// carefully, don't guess a numeric fix for unverifiable geometry" precedent (mirrors the MBend miter
/// investigation in the same brief, which found a comparably deep issue and also declined a
/// speculative fix).
///
/// <b>R-klp-10 (curvature check): CONFIRMED absent before this fix, now wired.</b> This file's own
/// investigation found <c>MicrostripOffsetCenterline.MinRadiusOfCurvature</c> fully implemented and
/// unit-tested, but never called from <c>MKlopfPCell.Generate</c> at all — despite an existing
/// completion note in <c>src/Ui/CLAUDE.md</c> claiming it was wired in. For the R-L5g-4 worked example
/// specifically (Z1=50,Z2=100,Γmax=0.05,L=200mm,Offset=100mm), the computed R_min≈81.12mm at the point
/// of maximum curvature (near x≈168mm — closer to PORT 2, not port 1) is far larger than 3×W_local
/// there (≈2.63mm), so a correctly-wired check would NOT fire for this specific example either — the
/// absence of a warning for THESE numbers was never itself evidence of a threshold bug, only of the
/// check's total absence, which this fix closes. A more aggressive case (short L relative to Offset)
/// is used below to prove the check actually fires when the geometry genuinely warrants it.
/// </summary>
public sealed class MKlopfCurvatureAndSmoothingTests
{
    [Fact]
    public void RL5g4WorkedExample_NoWarning_BecauseRMinIsFarLargerThanThreeWLocal()
    {
        var parms = new Dictionary<string, PCellValue>
        {
            ["Z1"] = 50.0, ["Z2"] = 100.0, ["GammaMax"] = 0.05,
            ["L"] = 0.200, ["Offset"] = 0.100, ["SmoothSteps"] = 1.0,
        };

        var result = MKlopfPCell.Generate(parms, null, PCellLayerSelection.Default);

        Assert.True(result.Diagnostics is null or { Count: 0 },
            "R_min (~81.12mm) is far larger than 3xW_local (~2.63mm) at the point of max curvature — " +
            "no warning is expected for this specific worked example, even with the check correctly wired.");
    }

    [Fact]
    public void AggressiveOffsetRelativeToLength_FiresTheCurvatureWarning_NamingRMin()
    {
        // A short taper with an aggressive offset relative to its length genuinely violates the
        // R_min >= 3xW_local margin (verified directly: R_min~2.85mm vs 3xW_local~7.67mm here) —
        // proving the check fires when the geometry actually warrants it, not just that it exists.
        var parms = new Dictionary<string, PCellValue>
        {
            ["Z1"] = 50.0, ["Z2"] = 100.0, ["GammaMax"] = 0.05,
            ["L"] = 0.010, ["Offset"] = 0.008, ["SmoothSteps"] = 1.0,
        };

        var result = MKlopfPCell.Generate(parms, null, PCellLayerSelection.Default);

        Assert.NotNull(result.Diagnostics);
        Assert.Contains(result.Diagnostics!, d => d.Contains("R-klp-10") && d.Contains("radius of curvature"));
    }

    [Fact]
    public void ZeroOffset_NeverWarns_StraightCenterlineHasNoCurvature()
    {
        var parms = new Dictionary<string, PCellValue>
        {
            ["Z1"] = 50.0, ["Z2"] = 100.0, ["GammaMax"] = 0.05,
            ["L"] = 0.010, ["Offset"] = 0.0, ["SmoothSteps"] = 1.0,
        };

        var result = MKlopfPCell.Generate(parms, null, PCellLayerSelection.Default);

        // Specifically the CURVATURE warning — which is what this test is about. A 10 mm taper on the
        // fallback 1.6 mm substrate is shorter than 3xW1 (~9 mm) allows, so SmoothSteps' own blend is
        // clamped and reports that; see MKlopfEndStepTests for why. Asserting "no diagnostics at all"
        // would make this test fail on an unrelated, correct report.
        Assert.DoesNotContain(result.Diagnostics ?? [], d => d.Contains("R-klp-10"));
    }

    [Fact]
    public void CurvatureWarning_ReachesGeneratedCellStoreCaller_ViaTheDiagnosticsOutParam()
    {
        string root = Path.Combine(Path.GetTempPath(), "crf-mklopf-curv-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var parms = new Dictionary<string, PCellValue>
            {
                ["Z1"] = 50.0, ["Z2"] = 100.0, ["GammaMax"] = 0.05,
                ["L"] = 0.010, ["Offset"] = 0.008, ["SmoothSteps"] = 1.0,
            };

            GeneratedCellStore.GetOrCreate(root, "MKLOPF", parms, null, null, PCellLayerSelection.Default, out var diagnostics);

            Assert.NotNull(diagnostics);
            Assert.Contains(diagnostics!, d => d.Contains("R-klp-10"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

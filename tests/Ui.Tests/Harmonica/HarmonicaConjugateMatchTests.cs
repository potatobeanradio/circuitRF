// ================================================================
//  HarmonicaConjugateMatchTests.cs — §2.6 of brief-harmonicarf-r9d-conjugate-match-and-pa-class-presets
// ================================================================

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaConjugateMatchTests
{
    // ══ ApplyConjugateMatch, via PublishFrame — the same seam ApplyInverseOutcome's own tests use ══

    [Fact]
    public void Found_SetsS1ToConjugateOfZin_AndItsGammaAtTheDocumentsZ0()
    {
        var vm = new HarmonicaViewModel();
        vm.AddMarkerBand(TerminationSideKind.Source, 1);
        var s1 = vm.Markers.Single(m => m.Side == TerminationSideKind.Source && m.Band == 1);

        var zin = new Complex(23.4, -12.1);
        var outcome = new ConjugateMatchOutcome(Found: true, Reason: null,
            RequestedBackoffDb: 5.0, ActualBackoffDb: 4.8, PinDbm: 10.2, Zin: zin);
        vm.PublishFrame(new HarmonicaFrame { ConjugateMatch = outcome });

        var expectedZ = Complex.Conjugate(zin);
        var actualZ   = vm.Terminations.Z(TerminationSide.Source, 1);
        Assert.Equal(expectedZ.Real,      actualZ.Real,      precision: 9);
        Assert.Equal(expectedZ.Imaginary, actualZ.Imaginary, precision: 9);

        var expectedGamma = HarmonicaDataSet.GammaOf(expectedZ, vm.Model.Settings.Z0);
        Assert.Equal(expectedGamma.Real,      s1.Gamma.Real,      precision: 9);
        Assert.Equal(expectedGamma.Imaginary, s1.Gamma.Imaginary, precision: 9);
    }

    [Fact]
    public void NotFound_LeavesTheS1TerminationBitIdentical_AndSetsAMessage()
    {
        var vm = new HarmonicaViewModel();
        vm.AddMarkerBand(TerminationSideKind.Source, 1);
        var before = vm.Terminations.Z(TerminationSide.Source, 1);

        var outcome = new ConjugateMatchOutcome(Found: false,
            Reason: "the drive-up did not reach the compression target, so there is no backoff point to measure from",
            RequestedBackoffDb: 5.0, ActualBackoffDb: 0, PinDbm: 0, Zin: Complex.Zero);
        vm.PublishFrame(new HarmonicaFrame { ConjugateMatch = outcome });

        var after = vm.Terminations.Z(TerminationSide.Source, 1);
        Assert.Equal(before.Real,      after.Real);
        Assert.Equal(before.Imaginary, after.Imaginary);
        Assert.Equal(outcome.Reason, vm.InverseMessage);
    }

    [Fact]
    public void NoS1Marker_ThrowsNothing_AndWritesNothing_TheShippedDefaultHasNone()
    {
        var vm = new HarmonicaViewModel();
        // R8B §3 — the shipped default document starts with NO S1 marker.
        Assert.DoesNotContain(vm.Markers, m => m.Side == TerminationSideKind.Source && m.Band == 1);
        var before = vm.Terminations.Z(TerminationSide.Source, 1);

        var outcome = new ConjugateMatchOutcome(Found: true, Reason: null,
            RequestedBackoffDb: 5.0, ActualBackoffDb: 4.8, PinDbm: 10.2, Zin: new Complex(11, -22));

        var ex = Record.Exception(() => vm.PublishFrame(new HarmonicaFrame { ConjugateMatch = outcome }));
        Assert.Null(ex);

        var after = vm.Terminations.Z(TerminationSide.Source, 1);
        Assert.Equal(before.Real,      after.Real);
        Assert.Equal(before.Imaginary, after.Imaginary);
    }

    // ══ the outcome is produced only when ConjugateMatchBackoffDb is set ═══════════════════════════

    [Fact]
    public void OrdinaryFrame_HasNoConjugateMatchOutcome()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 3, Spokes = 12, RasterResolution = 64,
                                                    SkipContours = true });
        Assert.Null(vm.Frame.ConjugateMatch);
    }

    [Fact]
    public void SettingConjugateMatchBackoffDb_ProducesAnOutcome()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true, ConjugateMatchBackoffDb = 5.0 });
        Assert.NotNull(vm.Frame.ConjugateMatch);
    }

    // ══ §2.6 — IndexOfBackoffStep, pinned as a pure function on a synthetic ladder ══════════════════

    private static PinStep Step(double pavlDbm) => new(pavlDbm, 0, default!)
    {
        Foms = default!, PdcW = 0,
    };

    [Fact]
    public void IndexOfBackoffStep_PicksTheNearestAlreadySolvedRung()
    {
        List<PinStep> ladder = [Step(-4), Step(-2), Step(0), Step(2), Step(4), Step(6), Step(8), Step(10)];

        // compression at 10 dBm, 5 dB backoff -> target 5 dBm -> nearest rung is 4 or 6 (both 1 dB away);
        // IndexOfNearestPin keeps the FIRST minimum found scanning in order, i.e. 4 dBm (index 4).
        int idx = HarmonicaSolver.IndexOfBackoffStep(ladder, compressionPinDbm: 10.0, backoffDb: 5.0);
        Assert.Equal(4, idx);
        Assert.Equal(4.0, ladder[idx].PavlDbm);
    }

    [Fact]
    public void IndexOfBackoffStep_TargetBelowTheLaddersFirstRung_LandsOnTheFirstRung()
    {
        List<PinStep> ladder = [Step(0), Step(2), Step(4), Step(6), Step(8), Step(10)];

        // compression at 2 dBm, 5 dB backoff -> target -3 dBm, below the ladder entirely.
        int idx = HarmonicaSolver.IndexOfBackoffStep(ladder, compressionPinDbm: 2.0, backoffDb: 5.0);
        Assert.Equal(0, idx);
        Assert.Equal(0.0, ladder[idx].PavlDbm);
    }

    [Fact]
    public void IndexOfBackoffStep_EmptyLadder_ReturnsMinusOne()
    {
        int idx = HarmonicaSolver.IndexOfBackoffStep([], compressionPinDbm: 10.0, backoffDb: 5.0);
        Assert.Equal(-1, idx);
    }
}

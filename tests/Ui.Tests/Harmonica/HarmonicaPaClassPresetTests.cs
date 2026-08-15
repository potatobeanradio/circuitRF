// ================================================================
//  HarmonicaPaClassPresetTests.cs — §3.8 of brief-harmonicarf-r9d-conjugate-match-and-pa-class-presets
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaPaClassPresetTests
{
    private const double F0 = 2e9;
    private const double Z0 = 80.0;

    private static CircuitModel Model(int k, LumpedPackage? package = null, DutCapacitances? caps = null) => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[2,0]"] = "0.08*(_v1+3)*(_v1+3)*tanh(0.4*_v2)",
            },
            Capacitances = caps ?? DutCapacitances.None,
        },
        Embedding = new EmbeddingStack { Package = package ?? LumpedPackage.None },
        Bias      = new BiasSpec { Vgs = -1.5, Vds = 10 },
        Settings  = new HarmonicaSettings { HarmonicCount = k, FrequencyHz = F0, Z0 = Z0 },
    };

    // ══ the owner's own example, made a gate: K=5, markers at L1/L2/L3 only ═══════════════════════

    [Fact]
    public void ClassF_WithMarkersAtL1L2L3Only_WritesExactlyThreeTerminations_L4L5StayUnmarked()
    {
        var vm = new HarmonicaViewModel(Model(k: 5));
        // The default constructor already seeds L1/L2/L3 for any K >= 3 — no S1/L4/L5.
        Assert.Equal(3, vm.Markers.Count);

        vm.ApplyPaClassPreset(PaClass.F);

        Assert.True(vm.Terminations.IsMarked(TerminationSide.Load, 1));
        Assert.True(vm.Terminations.IsMarked(TerminationSide.Load, 2));
        Assert.True(vm.Terminations.IsMarked(TerminationSide.Load, 3));
        Assert.False(vm.Terminations.IsMarked(TerminationSide.Load, 4));
        Assert.False(vm.Terminations.IsMarked(TerminationSide.Load, 5));

        var z1 = vm.Terminations.Z(TerminationSide.Load, 1);
        Assert.Equal(PaClassPresets.IntrinsicLoad(PaClass.F, 1, Z0).Real, z1.Real, precision: 6);

        Assert.Equal(TerminationSet.UnmarkedBandOhms, vm.Terminations.Z(TerminationSide.Load, 4).Real);
        Assert.Equal(TerminationSet.UnmarkedBandOhms, vm.Terminations.Z(TerminationSide.Load, 5).Real);
    }

    // ══ no marker is ever created ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ApplyingAPreset_NeverCreatesAMarker_OnEitherSide()
    {
        var vm = new HarmonicaViewModel(Model(k: 5));
        int before = vm.Markers.Count;
        int beforeSource = vm.Markers.Count(m => m.Side == TerminationSideKind.Source);
        int beforeLoad   = vm.Markers.Count(m => m.Side == TerminationSideKind.Load);

        vm.ApplyPaClassPreset(PaClass.B);

        Assert.Equal(before, vm.Markers.Count);
        Assert.Equal(beforeSource, vm.Markers.Count(m => m.Side == TerminationSideKind.Source));
        Assert.Equal(beforeLoad,   vm.Markers.Count(m => m.Side == TerminationSideKind.Load));
    }

    // ══ source markers are untouched ════════════════════════════════════════════════════════════

    [Fact]
    public void SourceMarkers_AreUntouched()
    {
        var vm = new HarmonicaViewModel(Model(k: 5));
        var s1 = vm.AddMarkerBand(TerminationSideKind.Source, 1);
        var beforeZ = vm.Terminations.Z(TerminationSide.Source, 1);
        var beforeGamma = s1.Gamma;

        vm.ApplyPaClassPreset(PaClass.J);

        var afterZ = vm.Terminations.Z(TerminationSide.Source, 1);
        Assert.Equal(beforeZ.Real,      afterZ.Real);
        Assert.Equal(beforeZ.Imaginary, afterZ.Imaginary);
        Assert.Equal(beforeGamma.Real,      s1.Gamma.Real);
        Assert.Equal(beforeGamma.Imaginary, s1.Gamma.Imaginary);
    }

    // ══ Cdg present — transform refused, best-effort at the extrinsic plane ════════════════════════

    [Fact]
    public void WithCdgSet_MarkersStillMove_ToTheIntrinsicValues_AndAMessageIsSet()
    {
        var caps = DutCapacitances.None with { Cdg = new DutCapacitance { Farads = 1e-13 } };
        var vm = new HarmonicaViewModel(Model(k: 5, caps: caps));
        Assert.False(CircuitModel.IntrinsicDragAllowed(vm.Model, out _));

        vm.ApplyPaClassPreset(PaClass.F);

        var expected1 = PaClassPresets.IntrinsicLoad(PaClass.F, 1, Z0);
        var actual1   = vm.Terminations.Z(TerminationSide.Load, 1);
        Assert.Equal(expected1.Real,      actual1.Real,      precision: 9);
        Assert.Equal(expected1.Imaginary, actual1.Imaginary, precision: 9);

        Assert.False(string.IsNullOrEmpty(vm.InverseMessage));
        Assert.Contains("EXTRINSIC", vm.InverseMessage, StringComparison.Ordinal);
    }

    // ══ nonlinear Cgs — the transform runs against a LINEARIZED copy, never written back ═══════════

    [Fact]
    public void WithNonlinearCgs_TransformRunsAgainstALinearizedCopy_ModelCgsStaysNonlinear()
    {
        // Real Rd/Ld on the LOAD side, so the transform actually moves the number — proving the
        // substitution let the transform run rather than silently falling back to best-effort (which
        // would coincidentally also write a non-identity-looking number only if best-effort itself
        // used the untransformed intrinsic value; comparing against the hand-derived transformed value
        // below is what tells the two apart).
        var package = LumpedPackage.None with { Rd = 2.0, Ld = 0.3e-9 };
        var caps = DutCapacitances.None with { Cgs = new DutCapacitance { Coefficients = [1e-12, 3e-14] } };
        var model = Model(k: 3, package: package, caps: caps);
        var vm = new HarmonicaViewModel(model);

        vm.ApplyPaClassPreset(PaClass.J);

        // Never written back — the copy is transform-only (§3.4).
        Assert.True(vm.Model.Dut.Capacitances.Cgs.IsNonlinear);
        Assert.Equal(1e-12, vm.Model.Dut.Capacitances.Cgs.Coefficients![0]);

        // The Load-side chain here is Rd/Ld only (no Cds, no Cpd): Z_ext = Z_intr - (Rd + jωLd) —
        // hand-derived independently of IntrinsicAbcd, matching IntrinsicAbcdTests's own convention.
        double omega = 2.0 * Math.PI * F0;
        var zIntr = PaClassPresets.IntrinsicLoad(PaClass.J, 1, Z0);
        var zExtExpected = zIntr - new Complex(2.0, omega * 0.3e-9);

        var actual = vm.Terminations.Z(TerminationSide.Load, 1);
        Assert.Equal(zExtExpected.Real,      actual.Real,      precision: 6);
        Assert.Equal(zExtExpected.Imaginary, actual.Imaginary, precision: 6);

        // Not the best-effort path — no "EXTRINSIC plane" refusal message.
        Assert.True(vm.InverseMessage is null || !vm.InverseMessage.Contains("EXTRINSIC", StringComparison.Ordinal));
    }

    // ══ exactly one frame is requested per preset application ══════════════════════════════════════

    [Fact]
    public async System.Threading.Tasks.Task ApplyingAPreset_RequestsExactlyOneFrame()
    {
        var vm = new HarmonicaViewModel(Model(k: 5));
        int before = vm.Pool.StartedCount;

        vm.ApplyPaClassPreset(PaClass.B);
        await vm.Pool.DrainAsync();

        Assert.Equal(before + 1, vm.Pool.StartedCount);
    }
}

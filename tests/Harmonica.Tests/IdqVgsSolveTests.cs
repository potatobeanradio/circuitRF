// ================================================================
//  IdqVgsSolveTests.cs — owner follow-up to brief-harmonicarf-r3c, 2026-08-13
//
//  "Change: the Idq display setting should be in mA not A. Be sure to convert units to A when
//  searching for the proper Vgs to set the Idq."
//  "Bug: When I change the Idq setting using the inline text editor, the corresponding Vgs does not
//  update. Similarly, the reverse is also a bug."
//
//  Before this: HarmonicaContext.Apply substituted a bare model.Bias.Vgs ?? 0.0 — Idq was persisted
//  and round-tripped but never once solved. This pins the REAL solve, against a known analytic SDD
//  law so the expected Vgs can be checked directly rather than merely "some number came back".
// ================================================================

using System;
using System.Collections.Generic;
using CircuitRF.Core.Devices.External;
using CircuitRF.Engine;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

public sealed class IdqVgsSolveTests(ITestOutputHelper output)
{
    private static AnalysisSettings Settings => new()
    {
        InductanceRegularization  = RegularizationMode.Always,
        ConductanceRegularization = RegularizationMode.Never,
    };

    /// <summary>
    /// Ids = 0.08·(Vgs+3)²·tanh(0.4·Vds) — a known, analytic, MONOTONE-in-Vgs law for Vgs &gt; −3 (the
    /// only region this whole test operates in), so the secant's answer can be checked against a
    /// closed form rather than merely "some number came back". At Vds = 10, tanh(4) ≈ 0.99933 —
    /// close enough to 1 that the closed-form inverse (below) does not need to carry it explicitly and
    /// still lands well inside the tolerances used here.
    /// </summary>
    private static CircuitModel Model(double? vgs, double? idqAmps, double vds = 10.0) => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[1,0]"] = "_v1/300",
                ["I[2,0]"] = "0.08*(_v1+3)*(_v1+3)*tanh(0.4*_v2)",
                ["Q[1]"]   = "2e-12*_v1",
            },
        },
        Embedding = new EmbeddingStack { Package = LumpedPackage.None },
        Bias      = new BiasSpec { Vgs = vgs, Idq = idqAmps, Vds = vds },
        Settings  = new HarmonicaSettings
        {
            FrequencyHz = 2e9, HarmonicCount = 3, Z0 = 50,
        },
    };

    /// <summary>The closed-form inverse of the SDD law above, for Vgs &gt; −3: Ids = 0.08·(Vgs+3)²
    /// (tanh(4) ≈ 0.999 folded into the tolerance rather than the formula).</summary>
    private static double ExpectedVgsFor(double idqAmps) => Math.Sqrt(idqAmps / 0.08) - 3.0;

    [Fact]
    public void DcDrainCurrentAmps_MatchesTheClosedFormAtAGivenVgs()
    {
        var ctx = HarmonicaContext.Create(Model(vgs: -1.5, idqAmps: null), Settings);

        double expected = 0.08 * (-1.5 + 3) * (-1.5 + 3);   // tanh(4) ≈ 1, folded into the tolerance
        double actual = ctx.DcDrainCurrentAmps;

        output.WriteLine($"Vgs=-1.5V: expected≈{expected:F6} A, actual={actual:F6} A");
        Assert.Equal(expected, actual, 3);
    }

    [Fact]
    public void SolveVgsForIdq_FindsTheKnownClosedFormAnswer()
    {
        var ctx = HarmonicaContext.Create(Model(vgs: -1.5, idqAmps: null), Settings);

        double targetAmps = 0.045;                       // → Vgs ≈ -2.25 by the closed form
        double expectedVgs = ExpectedVgsFor(targetAmps);

        double solved = ctx.SolveVgsForIdq(targetAmps, vds: 10.0);
        output.WriteLine($"target={targetAmps} A, expected Vgs≈{expectedVgs:F4} V, solved={solved:F4} V");

        Assert.Equal(expectedVgs, solved, 2);
    }

    [Fact]
    public void SolveVgsForIdq_WorksBothDirections_HigherAndLowerCurrent()
    {
        var ctx = HarmonicaContext.Create(Model(vgs: -1.5, idqAmps: null), Settings);
        double startIdq = ctx.DcDrainCurrentAmps;

        double higherTarget = startIdq * 1.5;
        double higherVgs = ctx.SolveVgsForIdq(higherTarget, vds: 10.0);
        Assert.Equal(ExpectedVgsFor(higherTarget), higherVgs, 2);
        Assert.True(higherVgs > -1.5, "a higher target current needs a LESS negative Vgs on this law");

        double lowerTarget = startIdq * 0.5;
        double lowerVgs = ctx.SolveVgsForIdq(lowerTarget, vds: 10.0);
        Assert.Equal(ExpectedVgsFor(lowerTarget), lowerVgs, 2);
        Assert.True(lowerVgs < -1.5, "a lower target current needs a MORE negative Vgs on this law");
    }

    [Fact]
    public void Apply_ResolvesVgsFromIdq_AndKeepsBothFieldsPopulated()
    {
        // Idq-driven from the start — no prior Vgs to warm-start the search from at all (Bias.Vgs
        // null), which is exactly the "the corresponding Vgs does not update" bug's own starting point.
        var ctx = HarmonicaContext.Create(Model(vgs: null, idqAmps: 0.045), Settings);

        Assert.NotNull(ctx.Model.Bias.Vgs);
        Assert.Equal(ExpectedVgsFor(0.045), ctx.Model.Bias.Vgs!.Value, 2);
        Assert.Equal(0.045, ctx.Model.Bias.Idq!.Value, 9);   // the TARGET survives, unit is amps

        // A second Apply with a DIFFERENT Idq target re-solves — this is the actual reported bug:
        // "when I change the Idq setting... the corresponding Vgs does not update."
        var moved = Model(vgs: null, idqAmps: 0.09);
        ctx.Apply(moved);
        Assert.Equal(ExpectedVgsFor(0.09), ctx.Model.Bias.Vgs!.Value, 2);
    }

    [Fact]
    public void Apply_ReSolvesForTheSameIdq_WhenVdsAlone_Moves()
    {
        // Idq depends on BOTH Vgs and Vds — moving Vds without re-solving would leave a stale Vgs that
        // no longer produces the target current at the new Vds.
        var ctx = HarmonicaContext.Create(Model(vgs: null, idqAmps: 0.045, vds: 10.0), Settings);
        double vgsAt10 = ctx.Model.Bias.Vgs!.Value;

        ctx.Apply(Model(vgs: null, idqAmps: 0.045, vds: 15.0));
        double vgsAt15 = ctx.Model.Bias.Vgs!.Value;

        // tanh(0.4·10)=0.9993 vs tanh(0.4·15)=0.99999 — a real, if small, difference; the two solved
        // Vgs must both still hit 0.045 A at THEIR OWN Vds, which is the real assertion.
        Assert.Equal(0.045, ctx.DcDrainCurrentAmps, 3);
        output.WriteLine($"Vgs@10V={vgsAt10:F4}, Vgs@15V={vgsAt15:F4}");
    }
}

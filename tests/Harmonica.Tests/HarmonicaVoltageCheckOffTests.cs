// ================================================================
//  HarmonicaVoltageCheckOffTests.cs — brief R-hbv-2: harmonicaRF's interactivity is untouched by the HB voltage-update test.
//
//  harmonicaRF calls HbNewton.Solve directly with settings of its own. Those settings leave the voltage-update test (R-hbv-1)
//  off, so every drag solves exactly as it did: the same iterations and the same bits as a context handed explicit OFF
//  settings. The third solve shows the comparison can tell: the same point with a strict test on costs more iterations.
// ================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using CircuitRF.Harmonica;
using Xunit;

namespace CircuitRF.Harmonica.Tests;

public class HarmonicaVoltageCheckOffTests
{
    private static CircuitModel Model() => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[1,0]"] = "_v1/50",
                ["I[2,0]"] = "0.2*tanh(_v2/6)*(1+_v1*0.3)",
            },
        },
        Bias     = new BiasSpec { Vgs = -1.0, Vds = 20 },
        Settings = new HarmonicaSettings
        {
            HarmonicCount = 3, FrequencyHz = 2e9, Tol = 1e-8,
            BiasChokeHenries = 1e-6, DcBlockFarads = 1e-9,
            CompressionDb = 2.0, PinStartDbm = -10, PinMaxDbm = 20,
        },
    };

    [Fact]
    public void ADragSolve_IsTheSolveWithTheTestOff_BitForBit()
    {
        var model    = Model();
        var settings = HarmonicaContext.DefaultSettings(model);
        Assert.Null(settings.HbVRelTol);
        Assert.Null(settings.HbVAbsTol);

        var terms = new TerminationSet(3);
        terms.Set(TerminationSide.Load, 1, new Complex(40, 5));

        var asShipped = HarmonicaContext.Create(model).Solve(terms, 10);
        var off       = HarmonicaContext.Create(model, settings.WithVoltageCheck(0, 0)).Solve(terms, 10);
        var strict    = HarmonicaContext.Create(model, settings.WithVoltageCheck(1e-12, 0)).Solve(terms, 10);

        Assert.True(asShipped.Converged);
        Assert.Equal(off.Iterations, asShipped.Iterations);
        Assert.Equal(off.V.Length, asShipped.V.Length);
        for (int n = 0; n < off.V.GetLength(0); n++)
            for (int k = 0; k < off.V.GetLength(1); k++)
                Assert.Equal(off.V[n, k], asShipped.V[n, k]);

        Assert.True(strict.Iterations > asShipped.Iterations,
            $"a strict test should cost iterations ({strict.Iterations} vs {asShipped.Iterations}); if it does not, this test cannot tell");
    }
}

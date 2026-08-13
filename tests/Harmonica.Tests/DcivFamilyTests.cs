using System;
using System.Collections.Generic;
using Xunit;

namespace CircuitRF.Harmonica.Tests;

/// <summary>
/// R-h9r2-14 (brief-harmonicarf-r2b) — a fresh document's DCIV family defaults to a FIXED window
/// (Vgs −5…2.5 V × 16, Vds 0…120 V × 120), chosen for the shipped SDD, rather than one centred on the
/// document's own bias. R-h9b-12's override still wins wherever it is set — untouched by this brief.
/// </summary>
public sealed class DcivFamilyTests
{
    private static CircuitModel Model(double vgs = -3.0, double vds = 28.0) => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[1,0]"] = "_v1/50",
                ["I[2,0]"] = "0.1*tanh(_v2)",
            },
        },
        Bias     = new BiasSpec { Vgs = vgs, Vds = vds },
        Settings = new HarmonicaSettings { HarmonicCount = 3, FrequencyHz = 2e9 },
    };

    [Fact]
    public void DefaultKey_IsTheFixedShippedSddWindow()
    {
        var key = DcivFamily.DefaultKey(Model());

        Assert.Equal(-5.0, key.VgsMin);
        Assert.Equal(2.5,  key.VgsMax);
        Assert.Equal(16,   key.VgsSteps);
        Assert.Equal(0.0,  key.VdsMin);
        Assert.Equal(120.0, key.VdsMax);
        Assert.Equal(120,  key.VdsSteps);
    }

    [Fact]
    public void DefaultKey_IsIndependentOfTheDocumentsOwnBias()
    {
        // Two documents with wildly different bias points must produce the IDENTICAL default window —
        // R-h9r2-14's own trade: the default no longer brackets any one document's operating point.
        var a = DcivFamily.DefaultKey(Model(vgs: -3.0, vds: 28.0));
        var b = DcivFamily.DefaultKey(Model(vgs: -0.5, vds: 5.0));

        Assert.Equal(a with { StructuralKey = "" }, b with { StructuralKey = "" });
    }

    [Fact]
    public void DefaultKey_PointCount_MatchesTheOwnersNumbers()
    {
        var key = DcivFamily.DefaultKey(Model());
        int count = key.VgsSteps * key.VdsSteps;
        // 16 x 120 = 1,920 — same order as the prior 9 x 200 = 1,800 default, so tier C's "computed
        // once and held" budget is unaffected by this change.
        Assert.Equal(1920, count);
    }

    [Fact]
    public void DefaultKey_CallersMayStillRequestADifferentResolution_AtTheNewWindow()
    {
        var key = DcivFamily.DefaultKey(Model(), vgsSteps: 5, vdsSteps: 10);
        Assert.Equal(-5.0, key.VgsMin);
        Assert.Equal(2.5,  key.VgsMax);
        Assert.Equal(5,    key.VgsSteps);
        Assert.Equal(0.0,  key.VdsMin);
        Assert.Equal(120.0, key.VdsMax);
        Assert.Equal(10,   key.VdsSteps);
    }

    [Fact]
    public void ResolvedKey_StillPrefersAnExplicitOverride_OverTheNewDefault()
    {
        var baseModel = Model();
        var model = baseModel with
        {
            Settings = baseModel.Settings with
            {
                DcivVgsMin = -6, DcivVgsMax = -2, DcivVgsSteps = 5,
                DcivVdsMin = 0,  DcivVdsMax = 20, DcivVdsSteps = 40,
            },
        };

        var resolved = DcivFamily.ResolvedKey(model);
        Assert.Equal(-6, resolved.VgsMin);
        Assert.Equal(-2, resolved.VgsMax);
        Assert.Equal(5,  resolved.VgsSteps);
        Assert.NotEqual(DcivFamily.DefaultKey(model), resolved);
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Commands.Analysis;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The crash this guards: pasting a Loadpull Pursuit card threw
/// <c>NotSupportedException: Cannot clone analysis type LoadpullPursuitAnalysis</c> from
/// <see cref="DuplicateAnalysisCommand.CloneAnalysis"/> — reached from Paste, not just Duplicate.
///
/// Two distinct failures are covered, because fixing only the first leaves the second live:
///   1. A subtype with no arm at all — the crash. Enumerated by REFLECTION over the Core assembly,
///      so a subtype added later fails here rather than at a user's paste.
///   2. An arm that exists and drops a property — silent, and worse: the pasted card looks right
///      and simulates something else. Compared property-by-property, again by reflection, so a
///      property added later is caught without anyone remembering to extend a list.
/// </summary>
public sealed class AnalysisClonePropertyParityTests
{
    // ── Sample instances, one per concrete Analysis subtype ────────────────────
    // Every value is deliberately NON-default, so a clone arm that forgets a property fails
    // rather than coincidentally matching the constructor default.

    private static Dictionary<Type, Analysis> Samples() => new()
    {
        [typeof(DcAnalysis)] = new DcAnalysis("DC1") { Enabled = false },

        [typeof(SParameterAnalysis)] = new SParameterAnalysis(
            "SP1", new[] { new FrequencySpec("1", "2", 11, SweepKind.Log, "GHz", "GHz") })
        { Enabled = false },

        [typeof(HarmonicBalanceAnalysis)] = new HarmonicBalanceAnalysis("HB1")
        {
            Enabled           = false,
            ToneExpr          = "3.7",
            ToneUnit          = "GHz",
            NumFreqsExpr      = "2",
            ToneExprs         = ["3.7", "3.71"],
            ToneUnits         = ["GHz", "GHz"],
            MaxMixOrderExpr   = "9",
            MaxHarmonicExpr   = "11",
            FFTOverSampleExpr = "4",
            TolExpr           = "1e-9",
            DriveSteppingExpr = "Always",
            GuardHarmonicExpr = "2",
            LambdaExpr        = "0.5",
            MaxIterExpr       = "250",
        },

        [typeof(LoadpullAnalysis)] = new LoadpullAnalysis("LP1")
        {
            Enabled           = false,
            ToneExpr          = "3.7",
            ToneUnit          = "GHz",
            LoadTunerName     = "TL1",
            SourceTunerName   = "TS1",
            GridPath          = "/grids/load.gam",
            PinStartExpr      = "-5",
            PinMaxExpr        = "25",
            MaxHarmonicExpr   = "7",
            SweepExpr         = "Source",
            TuneHarmExpr      = "2",
            CompressionExpr   = "1",
            GainTypeExpr      = "Gp",
            PinStepExpr       = "0.25",
            TickleExpr        = "off",
            MaxIterExpr       = "250",
            FFTOverSampleExpr = "4",
            TolExpr           = "1e-9",
            DriveSteppingExpr = "Always",
            GuardHarmonicExpr = "2",
            SourceDirectory   = "/designs/amp",
        },

        [typeof(LoadpullPursuitAnalysis)] = new LoadpullPursuitAnalysis("LPP1")
        {
            Enabled                   = false,
            ToneExpr                  = "3.7",
            ToneUnit                  = "GHz",
            LoadTunerName             = "TL1",
            SourceTunerName           = "TS1",
            PinStartExpr              = "-5",
            PinMaxExpr                = "25",
            MaxHarmonicExpr           = "7",
            SweepExpr                 = "Source",
            TuneHarmExpr              = "2",
            CompressionExpr           = "1",
            GainTypeExpr              = "Gp",
            PinStepExpr               = "0.25",
            TickleExpr                = "off",
            MaxIterExpr               = "250",
            FFTOverSampleExpr         = "4",
            TolExpr                   = "1e-9",
            DriveSteppingExpr         = "Always",
            GuardHarmonicExpr         = "2",
            EffTypeExpr               = "PAE",
            ZsourceOBOExpr            = "8",
            SearchMethodExpr          = "Simplex",
            OutputGridPath            = "/grids/out.gam",
            Vswr1Expr                 = "2.5",
            Vswr1ResolutionExpr       = "8",
            Vswr2Expr                 = "4",
            Vswr2ResolutionExpr       = "12",
            KeepNonconvergingExpr     = "true",
            NonconvergentVswrExpr     = "1.25",
            CreateLoadpullResultExpr  = "false",
            LoadpullResultZsourceExpr = "MXP",
            SourceDirectory           = "/designs/amp",
        },

        [typeof(ParametricSweepAnalysis)] = new ParametricSweepAnalysis(
            "SWP1", "Vgs", new SweepSpec(-3, -1, 21, SweepAxisMode.PointCount, SweepKind.Linear, ""), "HB1")
        { Enabled = false },
    };

    // ── 1. Every concrete Analysis subtype has a clone arm ────────────────────

    [Fact]
    public void EveryAnalysisSubtype_HasACloneArm_AndTheSampleSetCoversThemAll()
    {
        var subtypes = typeof(Analysis).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(Analysis)) && !t.IsAbstract)
            .OrderBy(t => t.Name)
            .ToList();

        // Guard against the whole test passing vacuously if the reflection query ever goes blank.
        Assert.True(subtypes.Count >= 6, $"Expected at least 6 Analysis subtypes, found {subtypes.Count}.");

        var samples = Samples();
        var missing = subtypes.Where(t => !samples.ContainsKey(t)).Select(t => t.Name).ToList();
        Assert.True(missing.Count == 0,
            "This test has no sample for: " + string.Join(", ", missing) +
            ". Add one, and a CloneAnalysis arm to match.");

        foreach (var t in subtypes)
        {
            var clone = DuplicateAnalysisCommand.CloneAnalysis(samples[t], "renamed");
            Assert.IsType(t, clone);
            Assert.Equal("renamed", clone.Name);
        }
    }

    // ── 2. A clone arm carries every property across ───────────────────────────

    [Fact]
    public void CloningAnAnalysis_CarriesEveryPropertyExceptTheName()
    {
        foreach (var (type, source) in Samples())
        {
            // ParametricSweepAnalysis is the one type whose inner link is deliberately retargeted
            // by the caller, so it is compared with its own inner name passed back in.
            string? inner = source is ParametricSweepAnalysis psa ? psa.InnerAnalysisName : null;
            var clone = DuplicateAnalysisCommand.CloneAnalysis(source, "renamed", inner);

            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                if (p.Name == nameof(Analysis.Name)) continue;

                object? a = p.GetValue(source), b = p.GetValue(clone);
                Assert.True(ValuesMatch(a, b),
                    $"{type.Name}.{p.Name} was not carried across: " +
                    $"expected {Describe(a)}, clone has {Describe(b)}.");
            }
        }
    }

    // ── Comparison helpers ────────────────────────────────────────────────────

    private static bool ValuesMatch(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is string) return Equals(a, b);

        // SweepSpec has no value equality; compare its own readable properties one level down.
        if (a is SweepSpec || a is FrequencySpec)
            return a.GetType() == b.GetType() &&
                   a.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                    .All(p => ValuesMatch(p.GetValue(a), p.GetValue(b)));

        if (a is IEnumerable ea && b is IEnumerable eb)
        {
            var la = ea.Cast<object?>().ToList();
            var lb = eb.Cast<object?>().ToList();
            return la.Count == lb.Count && la.Zip(lb).All(pair => ValuesMatch(pair.First, pair.Second));
        }

        return Equals(a, b);
    }

    private static string Describe(object? v) => v switch
    {
        null            => "<null>",
        string s        => $"\"{s}\"",
        IEnumerable e   => "[" + string.Join(", ", e.Cast<object?>().Select(Describe)) + "]",
        IFormattable f  => f.ToString(null, CultureInfo.InvariantCulture),
        _               => v.ToString() ?? "<?>",
    };
}

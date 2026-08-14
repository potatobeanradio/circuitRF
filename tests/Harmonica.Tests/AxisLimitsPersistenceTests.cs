// ================================================================
//  AxisLimitsPersistenceTests.cs — §5 gate 2 of
//  brief-harmonicarf-r6e-plot-axis-limits-and-autoscale.md
//
//  Every new HarmonicaSettings field this brief adds survives a .charm write -> read, and a .charm
//  written before these fields existed opens with all of them absent (autoscale off, no stored
//  limits) — the same "absent field takes the default" rule every other CharmIo field follows.
// ================================================================

using CircuitRF.Harmonica;
using Xunit;

namespace CircuitRF.Harmonica.Tests;

public sealed class AxisLimitsPersistenceTests
{
    private static CircuitModel Model(HarmonicaSettings settings) => new()
    {
        Dut = new DutSpec { Kind = DutKind.NativeFet, TypeName = "FET_Angelov" },
        Settings = settings,
    };

    [Fact]
    public void R6e_AxisLimitFields_RoundTripThroughACharmFile()
    {
        var settings = new HarmonicaSettings
        {
            DcivXMin = -2.5, DcivXMax = 9.5, DcivYMin = 0.0, DcivYMax = 1.2, DcivAutoscale = true,
            PowerSweepXMin = -10, PowerSweepXMax = 34, PowerSweepYMin = 5, PowerSweepYMax = 20,
            PowerSweepY2Min = 0, PowerSweepY2Max = 70, PowerSweepAutoscale = false,
            TimeDomainXMin = 0, TimeDomainXMax = 0.5, TimeDomainYMin = -1, TimeDomainYMax = 49,
            TimeDomainY2Min = -0.1, TimeDomainY2Max = 0.9, TimeDomainAutoscale = true,
        };
        var model = Model(settings);
        var terms = new TerminationSet(model.Settings.HarmonicCount);

        string json = CharmIo.Write(model, terms);
        var (back, _) = CharmIo.Read(json, null, out var unresolved, withMarkers: true);

        Assert.Empty(unresolved);
        Assert.Equal(model.Settings, back.Settings);
    }

    [Fact]
    public void R6e_AllThirteenAxisLimitFields_AreAbsentOnAPreexistingCharm()
    {
        // No Settings block at all — a document written before this brief existed.
        var (model, _) = CharmIo.Read("""{ "FormatVersion": 1 }""", null, out var unresolved,
                                      withMarkers: true);

        Assert.Empty(unresolved);
        var s = model.Settings;

        Assert.Null(s.DcivXMin); Assert.Null(s.DcivXMax); Assert.Null(s.DcivYMin); Assert.Null(s.DcivYMax);
        Assert.False(s.DcivAutoscale);

        Assert.Null(s.PowerSweepXMin); Assert.Null(s.PowerSweepXMax);
        Assert.Null(s.PowerSweepYMin); Assert.Null(s.PowerSweepYMax);
        Assert.Null(s.PowerSweepY2Min); Assert.Null(s.PowerSweepY2Max);
        Assert.False(s.PowerSweepAutoscale);

        Assert.Null(s.TimeDomainXMin); Assert.Null(s.TimeDomainXMax);
        Assert.Null(s.TimeDomainYMin); Assert.Null(s.TimeDomainYMax);
        Assert.Null(s.TimeDomainY2Min); Assert.Null(s.TimeDomainY2Max);
        Assert.False(s.TimeDomainAutoscale);
    }

    [Fact]
    public void R6e_NoStoredLimitsSet_WritesNoLimitFieldsIntoTheFile()
    {
        // An untouched document (default HarmonicaSettings) should not gain any of the twelve new
        // NULLABLE JSON properties just because this brief exists — DefaultIgnoreCondition.
        // WhenWritingNull already gives every other optional numeric field this property, and this
        // pins it for the new ones too. The three Autoscale flags are ordinary (non-nullable in
        // HarmonicaSettings) booleans, like ComputeCharge/TickleEnabled/ExactCompressionSolve
        // elsewhere in this same block — they are always written, default or not, exactly as those
        // are, so they are deliberately not in this list.
        var model = Model(new HarmonicaSettings());
        var terms = new TerminationSet(model.Settings.HarmonicCount);

        string json = CharmIo.Write(model, terms);

        foreach (string forbidden in new[]
        {
            "DcivXMin", "DcivXMax", "DcivYMin", "DcivYMax",
            "PowerSweepXMin", "PowerSweepXMax", "PowerSweepYMin", "PowerSweepYMax",
            "PowerSweepY2Min", "PowerSweepY2Max",
            "TimeDomainXMin", "TimeDomainXMax", "TimeDomainYMin", "TimeDomainYMax",
            "TimeDomainY2Min", "TimeDomainY2Max",
        })
            Assert.DoesNotContain(forbidden, json, System.StringComparison.Ordinal);
    }
}

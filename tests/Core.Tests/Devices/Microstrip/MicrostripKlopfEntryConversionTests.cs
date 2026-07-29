using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>
/// Gates for <see cref="MicrostripKlopfEntryConversion"/> — the shared Z1/Z2⇄W1/W2 and L⇄F3db
/// conversion math extracted from <c>ComponentModelFactory.CreateMicrostripKlopfModel</c> so a
/// UI-layer "switch entry mode" affordance can reuse the EXACT same physics rather than
/// re-deriving it (the missing affordance itself was the owner's follow-up report after the
/// taper-family brief shipped).
/// </summary>
public class MicrostripKlopfEntryConversionTests
{
    private const double HMeters = 1.6e-3;
    private const double TMeters = 35e-6;
    private const double ErFr4 = 4.4;

    private static MicrostripValidityReporter Reporter() => new("test");

    [Fact]
    public void ImpedanceToWidth_LowerImpedance_IsWiderTrace()
    {
        var (w1, w2) = MicrostripKlopfEntryConversion.ImpedanceToWidth(50.0, 100.0, HMeters, TMeters, ErFr4, Reporter());
        Assert.True(w1 > w2);
    }

    [Fact]
    public void WidthToImpedance_WiderTrace_IsLowerImpedance()
    {
        var (z1, z2) = MicrostripKlopfEntryConversion.WidthToImpedance(2.9e-3, 1.0e-3, HMeters, TMeters, ErFr4, Reporter());
        Assert.True(z1 < z2);
    }

    [Fact]
    public void ImpedanceToWidth_ThenWidthToImpedance_RoundTrips()
    {
        var (w1, w2) = MicrostripKlopfEntryConversion.ImpedanceToWidth(50.0, 100.0, HMeters, TMeters, ErFr4, Reporter());
        var (z1, z2) = MicrostripKlopfEntryConversion.WidthToImpedance(w1, w2, HMeters, TMeters, ErFr4, Reporter());
        Assert.Equal(50.0, z1, 1);
        Assert.Equal(100.0, z2, 1);
    }

    [Fact]
    public void LengthToF3db_ThenF3dbToLength_RoundTrips()
    {
        double f3db = MicrostripKlopfEntryConversion.LengthToF3db(50.0, 100.0, 0.05, 10e-3, HMeters, TMeters, ErFr4, Reporter());
        double l = MicrostripKlopfEntryConversion.F3dbToLength(50.0, 100.0, 0.05, f3db, HMeters, TMeters, ErFr4, Reporter());
        Assert.Equal(10e-3, l, 6);
    }

    [Fact]
    public void LengthToF3db_LongerTaper_HasLowerCutoff()
    {
        double f3dbShort = MicrostripKlopfEntryConversion.LengthToF3db(50.0, 100.0, 0.05, 5e-3,  HMeters, TMeters, ErFr4, Reporter());
        double f3dbLong  = MicrostripKlopfEntryConversion.LengthToF3db(50.0, 100.0, 0.05, 20e-3, HMeters, TMeters, ErFr4, Reporter());
        Assert.True(f3dbLong < f3dbShort);
    }

    [Fact]
    public void MatchesTheFactorysOwnResolution()
    {
        // Cross-check against ComponentModelFactory's own entry-route resolution (the caller this
        // class was extracted FROM) so the two can never silently diverge.
        var direct = ComponentModelFactory.TryCreate("MKLOPF", new Dictionary<string, CircuitRF.Core.Expressions.Value>
        {
            ["Z1"] = new(50.0), ["Z2"] = new(100.0), ["L"] = new(10e-3), ["GammaMax"] = new(0.05),
        });

        var (w1, w2) = MicrostripKlopfEntryConversion.ImpedanceToWidth(50.0, 100.0, HMeters, TMeters, ErFr4, Reporter());

        var viaWidth = ComponentModelFactory.TryCreate("MKLOPF", new Dictionary<string, CircuitRF.Core.Expressions.Value>
        {
            ["W1"] = new(w1), ["W2"] = new(w2), ["L"] = new(10e-3), ["GammaMax"] = new(0.05),
        });

        Assert.NotNull(direct);
        Assert.NotNull(viaWidth);
        Assert.IsType<MicrostripKlopfModel>(direct);
        Assert.IsType<MicrostripKlopfModel>(viaWidth);
    }
}

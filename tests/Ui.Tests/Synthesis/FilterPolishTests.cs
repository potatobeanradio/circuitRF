using System.Globalization;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Filters;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Synthesis;

/// <summary>
/// Polish (docs/design/filter-synthesis.md §6c) on the tapped hairpin the Filter Designer shows for a 0.1 dB Chebyshev
/// of order 5, 2.2 to 2.6 GHz, on the shipped 20 mil RO4350B with its loss: its worst return loss across the band is
/// at least 3 dB better once polished than as realised.
/// </summary>
public class FilterPolishTests(ITestOutputHelper output)
{
    [Fact]
    public void TheTappedHairpin_MatchesBetterOncePolished()
    {
        var board = new MicrostripSubstrate(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);
        var spec = new FilterSpec(FilterResponse.Chebyshev, FilterBand.Bandpass, 5, 2.2e9, 2.6e9, 0.1);
        var realised = new TappedHairpinRealiser().Realise(spec, board);
        var (polished, history) = FilterPolish.Run(spec, realised, board);

        for (int i = 0; i < history.Count; i++)
        {
            var x = history[i].Extraction;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"pass {i}: RL {x.ReturnLossDb:F2} dB, f0 {x.CentreHz / 1e9:F4} GHz, BW {x.BandwidthHz / 1e6:F1} MHz, "
                + $"IL {x.InsertionLossDb:F2} dB, Qu {x.UnloadedQ:F0}"));
            output.WriteLine("  " + string.Join(", ", x.Checks.Skip(4).Select(c => string.Create(CultureInfo.InvariantCulture,
                $"{c.Name} {c.Value:G4}/{c.Target:G4}"))));
            output.WriteLine("  " + string.Join(" ", history[i].Parameters.Where(p => p.Key is "Tap" || p.Key.StartsWith('L')
                || p.Key.StartsWith('S')).Select(p => $"{p.Key}={p.Value}")));
        }
        output.WriteLine(polished.Notes[^1]);
        // Measured: 8.6 dB as realised, 15.5 dB after three passes, against the lossless prototype's 16.4 dB.
        var best = history.MaxBy(h => h.Extraction.ReturnLossDb)!;
        Assert.True(best.Extraction.ReturnLossDb > history[0].Extraction.ReturnLossDb + 3.0);
        Assert.Same(best.Parameters, polished.Parameters);
    }
}

using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;
using CircuitRF.Ui.Filters;
using CircuitRF.Ui.Matching;
using Xunit;

namespace CircuitRF.Ui.Tests.Filters;

/// <summary>
/// Every position the LC Filter Designer's ladder drawing gives — each part, ground, termination, wire end and
/// junction dot — is on the schematic's 100-unit connection grid, so the pane and a placed ladder are on it too.
/// </summary>
public sealed class FilterLadderGridTests
{
    public static TheoryData<FilterResponse, NetworkForm, int> Ladders()
    {
        var data = new TheoryData<FilterResponse, NetworkForm, int>();
        foreach (var r in new[] { FilterResponse.Butterworth, FilterResponse.Chebyshev, FilterResponse.InvChebyshev, FilterResponse.Elliptic })
            foreach (var f in new[] { NetworkForm.Lowpass, NetworkForm.Highpass, NetworkForm.Bandpass, NetworkForm.Bandstop })
                foreach (int n in new[] { 3, 4, 5, 7 })
                    data.Add(r, f, n);
        return data;
    }

    private static void OnGrid(double v, string what) =>
        Assert.True(Math.Abs(v / 100.0 - Math.Round(v / 100.0)) < 1e-9, $"{what} at {v} is off the 100-unit grid");

    [Theory]
    [MemberData(nameof(Ladders))]
    public void EveryPartAndWireOfTheDrawingIsOnTheGrid(FilterResponse response, NetworkForm form, int order)
    {
        var built = FilterLadder.Synthesize(response, form, order, 1e9, 0.9e9, 1.1e9, 0.5, 50,
                                            FilterFirstElement.Parallel, 40, EllipticType.Auto);
        if (built.Realization is not { } real) return;   // no ladder at this order: nothing drawn
        var layout = FilterLadderDrawing.Build(real.Elements, real.Arms, real.SourceOhms, real.LoadOhms,
                                               (_, v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var e in layout.Elements) { OnGrid(e.X, e.Name); OnGrid(e.Y, e.Name); }
        foreach (var t in layout.Terminations) OnGrid(t.X, t.InstanceName);

        // The schematic Place filter puts down: every component origin and wire vertex.
        var sheet = FilterPlacement.PortedLadderFragment(layout);
        Assert.NotEmpty(sheet.Components);
        foreach (var c in sheet.Components) { OnGrid(c.X, c.InstanceName); OnGrid(c.Y, c.InstanceName); }
        foreach (var w in sheet.Wires)
            foreach (var (x, y) in w.Points) { OnGrid(x, "a wire end"); OnGrid(y, "a wire end"); }
        var labelled = FilterPlacement.LadderFragment(layout, "IN", "OUT");
        foreach (var w in labelled.Wires)
            foreach (var (x, y) in w.Points) { OnGrid(x, "a stub end"); OnGrid(y, "a stub end"); }
    }
}

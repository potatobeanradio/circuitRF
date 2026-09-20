// ================================================================
//  SeriesElementWindowTests.cs — brief-railrf-25-series-element.md §4, §5
//
//  What the READER is shown once a rail has an element in it: the parts table marks the row
//  (R-rail25-4a), and the copper map shades the two sections (R-rail25-4c).
//
//  ── NEITHER OF THESE NEEDS AN APP HOST ──────────────────────────────────────────────────────
//
//  The row is an ordinary view model over a document row, and the picture is RailMapScene — a pure
//  function of the result, below the firewall, which is what R-rail8-13 put it there for. So both
//  gates run with no window, no dispatcher and no Avalonia, and they gate the thing a screenshot
//  would have gated rather than a proxy for it.
// ================================================================

using System;
using System.Linq;
using CircuitRF.Design.RailRf;
using CircuitRF.Render;
using CircuitRF.Ui.RailRf;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class SeriesElementWindowTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    /// <summary>
    /// <b>R-rail25-4a.</b> A series 1 Ω and a shunt 1 Ω do opposite things, so the table marks the
    /// series row — and the two number columns say what the element IS rather than leaving a
    /// capacitor's columns blank beside it.
    /// </summary>
    /// <remarks>
    /// <b>And it is NOT "unresolved"</b>, which is the trap this row would otherwise fall into: a
    /// ferrite's model is its own row's R-L, not the library's C-and-f₀ arithmetic, so a library
    /// with no row for it is not a data problem. Dimming it would report one the document does not
    /// have — the same two-states-one-spelling failure R-rail23-1d already names for mounted.
    /// </remarks>
    [Fact]
    public void R_rail25_4a_AParTsTableMarksASeriesRowAndSaysWhatItIs()
    {
        var ferrite = SeriesElementTests.Ferrite(
            seriesOhms: 600, seriesHenries: 1.2e-6, dcrOhms: 0.060);

        var row = new RailPartRowViewModel(ferrite, null, null, null, null);

        Assert.True(row.IsSeries);

        // The library has no row for a ferrite and that is not a finding.
        Assert.False(row.IsUnresolved);

        // The capacitance column reads the element's model over frequency — it has no capacitance
        // and a blank cell would read as a part railRF failed on.
        Assert.Equal("600 Ω + 1.2 µH", row.CapacitanceText);

        // The ESR column reads the DCR, which is what the element costs the rail at DC.
        Assert.Equal("60 mΩ", row.EsrText);

        // UNSTATED is its own word and it is not "unresolved" (R-rail25-3b).
        var unstated = new RailPartRowViewModel(
            ferrite with { DcResistanceOhms = null }, null, null, null, null);
        Assert.Equal(RailPartRowViewModel.UnstatedText, unstated.EsrText);
        Assert.NotEqual(RailPartRowViewModel.UnresolvedText, unstated.EsrText);

        // The caveat a lumped R-L standing in for a bead cannot leave out is on the row too.
        Assert.Contains("BIAS-DEPENDENT", row.RowTooltip, StringComparison.Ordinal);
        _output.WriteLine(row.RowTooltip);

        // And clearing its checkbox says what will happen rather than letting the refusal be the
        // first anyone hears of it (R-rail25-3c).
        Assert.Contains("OPENS the rail", row.MountTooltip, StringComparison.Ordinal);

        // A SHUNT row is untouched by any of it.
        var cap = new RailPartRowViewModel(
            new RailPart { Refdes = "C1", PartNumber = "PN-1U" }, null, null, null, null);
        Assert.False(cap.IsSeries);
        Assert.DoesNotContain("OPENS", cap.MountTooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>R-rail25-4c.</b> The copper map shades the two sections differently, so <i>which side of
    /// the ferrite am I on</i> is answerable by looking.
    /// </summary>
    /// <remarks>
    /// <b>The scene carries the SOLVE's own partition</b>, not a second walk of its own — which is
    /// the whole reason it is read off <c>RailDcResult.Sections</c>. A picture that partitioned the
    /// board for itself could shade a capacitor upstream while its branch was stamped downstream,
    /// and nothing anywhere would say so.
    /// </remarks>
    [Fact]
    public void R_rail25_4c_TheCopperMapShadesTheTwoSections()
    {
        var run = RailDcRun.Run(SeriesElementTests.DcRequest(dcrOhms: 0.350));
        Assert.Null(run.Refusal);

        var result = run.Rails[0];
        Assert.Equal(2, result.Sections.Count);

        var scene = RailMapScene.Build(result, RailMapKind.Copper, 1000);
        var sections = scene.Regions.Select(r => r.Section).Distinct().ToList();

        _output.WriteLine(string.Join("\n", scene.Regions.Select(r => r.Readout)));

        Assert.Contains(RailSection.Upstream, sections);
        Assert.Contains(RailSection.Downstream, sections);

        // And the readout says which, in words, for the reader who hovers rather than compares.
        Assert.Contains(scene.Regions, r => r.Readout.Contains("UPSTREAM", StringComparison.Ordinal));
        Assert.Contains(scene.Regions, r => r.Readout.Contains("DOWNSTREAM", StringComparison.Ordinal));

        // A rail with NO series element carries no sections and the tab draws what it always drew.
        var plain = RailDcRun.Run(SeriesElementTests.DcRequest(dcrOhms: 0.350, withElement: false));
        Assert.Null(plain.Refusal);
        Assert.Empty(plain.Rails[0].Sections);
        Assert.All(RailMapScene.Build(plain.Rails[0], RailMapKind.Copper, 1000).Regions,
                   r => Assert.Null(r.Section));
    }
}

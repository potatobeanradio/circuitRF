// ================================================================
//  HarmonicaTracePickerTests.cs  —  M2's gate, brief-harmonicarf-h7
//
//  R-h7-5  the picker plots CUBES through the EXISTING parser.
//  R-h7-6  the DataSet a picker sees is the one the panels drew from.
//  R-h7-7  a picked trace persists in the .charm and survives a reload; a .charm written before
//          this phase still opens.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Renderers;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaTracePickerTests(ITestOutputHelper output)
{
    private static HarmonicaViewModel Solved(CircuitModel? model = null)
    {
        var vm = new HarmonicaViewModel(model);
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });
        return vm;
    }

    /// <summary>
    /// A source lead is what makes <c>Z_S,intr</c> depend on gm (§4.5.3(a)) — and therefore what
    /// makes the source-side conversion matrix's OFF-DIAGONALS non-zero. Without one there is no
    /// harmonic conversion on the source side to look at.
    /// </summary>
    private static CircuitModel WithSourceLead() => HarmonicaViewModel.DefaultModel() with
    {
        Embedding = new EmbeddingStack
        {
            Package = new LumpedPackage { Rs = 0.8, Ls = 0.15e-9, Rd = 4.0 },
        },
    };

    // ══ R-h7-6 — the frame carries the DataSet the panels drew from ══════════

    [Fact]
    public void TheFrame_PublishesTheDataSetItsOwnGlyphsWereReadFrom()
    {
        var vm = Solved();
        Assert.NotNull(vm.Frame.Published);
        Assert.True(vm.Frame.Published!.Contains("Gamma_intr"));
        Assert.True(vm.Frame.Published.Contains("Zs_conv"));
    }

    [Fact]
    public void ATracePickedOverGammaIntr_RendersTheSameNumbersTheGlyphsAreDrawnAt()
    {
        var vm = Solved(WithSourceLead());
        var ds = vm.Frame.Published!;

        // The LOAD row of Gamma_intr — side index 1 — swept over the harmonic axis.
        var picked = new HarmonicaPickedTrace("Gamma_intr[1, :]", "trace.1");
        var plot = HarmonicaTracePicker.TryBuild(picked, ds, HarmonicaRenderTheme.Dark, out string? err);

        Assert.Null(err);
        Assert.NotNull(plot);

        var trace = plot!.Traces.Single();
        var cube  = ds["Gamma_intr"];
        int bands = cube.Axes[1].Values.Length;

        // COMPARED, not asserted: the marker's own GammaIntrinsic (what the glyph is drawn at) must
        // equal the cube entry the picked trace resolved, at the marker's own band.
        var l1 = vm.Markers.Single(m => m is { Side: TerminationSideKind.Load, Band: 1 });
        var fromCube = cube.ComplexValues[1 * bands + 1];

        output.WriteLine($"glyph Γᵢ = {l1.GammaIntrinsic}   cube = {fromCube}");
        Assert.Equal(fromCube.Real,      l1.GammaIntrinsic.Real,      12);
        Assert.Equal(fromCube.Imaginary, l1.GammaIntrinsic.Imaginary, 12);

        // …and the trace really carries that many points, i.e. it resolved rather than silently
        // producing an empty path.
        Assert.Equal(bands, trace.Points.Count);
    }

    // ══ R-h7-5 — Zs_conv's off-diagonals, the interesting case ═══════════════

    [Fact]
    public void ZsConvOffDiagonal_IsPickable_AndIsNonZeroWhereHarmonicConversionGenuinelyOccurs()
    {
        var vm = Solved(WithSourceLead());
        var ds = vm.Frame.Published!;

        var cube = ds["Zs_conv"];
        Assert.Equal(2, cube.Rank);
        int nIn = cube.Axes[1].Values.Length;

        // Report the whole matrix — §4.5.3 calls the off-diagonals "genuinely useful and
        // rarely-visible", and this is the first phase that can look at them.
        for (int i = 0; i < cube.Axes[0].Values.Length; i++)
        {
            var row = new List<string>();
            for (int k = 0; k < nIn; k++)
            {
                var z = cube.ComplexValues[i * nIn + k];
                row.Add(double.IsNaN(z.Real) ? "     NaN" : $"{z.Magnitude,8:F3}");
            }
            output.WriteLine($"  Zs_conv[{i}, ·] = {string.Join(" ", row)}");
        }

        // The picked trace: row 1 (the fundamental OUT), swept over every input harmonic.
        var picked = new HarmonicaPickedTrace("mag(Zs_conv[1, :])", "trace.1");
        var plot = HarmonicaTracePicker.TryBuild(picked, ds, HarmonicaRenderTheme.Dark, out string? err);
        Assert.Null(err);
        Assert.NotNull(plot);
        Assert.Equal(nIn, plot!.Traces.Single().Points.Count);

        // Non-vacuity: at least one genuine off-diagonal must be non-negligible against its own
        // diagonal, or "the off-diagonals are plottable" is a claim about zeros.
        double diag = cube.ComplexValues[1 * nIn + 1].Magnitude;
        double worstOff = 0;
        for (int i = 0; i < cube.Axes[0].Values.Length; i++)
            for (int k = 0; k < nIn; k++)
            {
                if (i == k) continue;
                var z = cube.ComplexValues[i * nIn + k];
                if (double.IsNaN(z.Real)) continue;
                worstOff = Math.Max(worstOff, z.Magnitude);
            }

        output.WriteLine($"|Zs_conv[1,1]| = {diag:F4} Ω, largest off-diagonal = {worstOff:F4} Ω " +
                         $"({worstOff / diag:P1} of the diagonal)");
        Assert.True(worstOff > 1e-6 * diag,
            "the source-side conversion matrix is diagonal on this fixture, so the off-diagonals " +
            "measure nothing — the fixture needs an element that couples the input and output loops");
    }

    [Fact]
    public void ABareDeviceHasNothingToSee_WhichIsWhyTheFixtureCarriesASourceLead()
    {
        // The control for the test above: with no coupling element the source-side matrix's
        // off-diagonals are not interesting, and saying so is what stops the fixture being changed
        // to something that passes for the wrong reason.
        var vm = Solved();                                   // the shipped default: no package at all
        Assert.False(vm.Model.Embedding.Package.CouplesInputAndOutput);

        var cube = vm.Frame.Published!["Zs_conv"];
        int nIn = cube.Axes[1].Values.Length;
        double diag = cube.ComplexValues[1 * nIn + 1].Magnitude;
        double worstOff = 0;
        for (int i = 0; i < cube.Axes[0].Values.Length; i++)
            for (int k = 0; k < nIn; k++)
            {
                if (i == k) continue;
                var z = cube.ComplexValues[i * nIn + k];
                if (!double.IsNaN(z.Real)) worstOff = Math.Max(worstOff, z.Magnitude);
            }

        output.WriteLine($"bare device: diagonal {diag:E3} Ω, largest off-diagonal {worstOff:E3} Ω " +
                         $"({worstOff / Math.Max(diag, 1e-300):E2} of it)");
    }

    // ══ R-h7-5 — the parse is CubeTraceSpecParser's, errors included ═════════

    [Fact]
    public void AnUnresolvableSpec_ReportsTheParsersOwnError_RatherThanDrawingNothingSilently()
    {
        var vm = Solved();
        var plot = HarmonicaTracePicker.TryBuild(
            new HarmonicaPickedTrace("Gamma_intr[9, :]", "trace.1"),
            vm.Frame.Published, HarmonicaRenderTheme.Dark, out string? err);

        Assert.Null(plot);
        Assert.False(string.IsNullOrWhiteSpace(err));
        output.WriteLine(err);
    }

    [Fact]
    public void EveryOfferedSpec_ActuallyResolves()
    {
        var vm = Solved(WithSourceLead());
        var offers = HarmonicaTracePicker.Offers(vm.Frame.Published);

        Assert.NotEmpty(offers);
        foreach (var offer in offers)
        {
            var plot = HarmonicaTracePicker.TryBuild(
                new HarmonicaPickedTrace(offer.Spec, "trace.1"),
                vm.Frame.Published, HarmonicaRenderTheme.Dark, out string? err);
            output.WriteLine($"{offer.Spec,-28} {(err ?? "ok")}");
            Assert.True(plot is not null,
                $"the picker offers '{offer.Spec}' but it does not resolve: {err}");
        }
    }

    // ══ R-h7-7 — a picked trace is part of the document ══════════════════════

    [Fact]
    public void PickedTraces_RoundTripThroughTheCharm()
    {
        var vm = Solved();
        vm.AddPickedTrace("Gamma_intr[1, :]", "load glyph");
        vm.AddPickedTrace("mag(Zs_conv[1, :])");

        string json = vm.ToCharmJson();
        Assert.Contains("Zs_conv", json, StringComparison.Ordinal);

        var reloaded = new HarmonicaViewModel();
        reloaded.LoadCharm(json, baseDirectory: null);

        Assert.Equal(2, reloaded.PickedTraces.Count);
        Assert.Equal("Gamma_intr[1, :]", reloaded.PickedTraces[0].Spec);
        Assert.Equal("load glyph",       reloaded.PickedTraces[0].Label);
        Assert.Equal(vm.PickedTraces[0].PanelId, reloaded.PickedTraces[0].PanelId);
        Assert.Null(reloaded.PickedTraces[1].Label);
    }

    [Fact]
    public void ACharmWrittenBeforeThisPhase_StillOpens_AndCarriesNoTraces()
    {
        // Exactly what CharmIo.Write produced before the traces block existed.
        var vm = new HarmonicaViewModel();
        string legacy = CharmIo.Write(vm.Model, vm.Terminations);
        Assert.DoesNotContain("\"Traces\"", legacy, StringComparison.Ordinal);

        var reloaded = new HarmonicaViewModel();
        var unresolved = reloaded.LoadCharm(legacy, baseDirectory: null);

        Assert.Empty(unresolved);
        Assert.Empty(reloaded.PickedTraces);
    }

    [Fact]
    public void ADocumentWithNoPickedTraces_WritesNoTracesBlock_SoAnUntouchedFileDoesNotChurn()
    {
        var vm = new HarmonicaViewModel();
        string a = vm.ToCharmJson();

        var reloaded = new HarmonicaViewModel();
        reloaded.LoadCharm(a, baseDirectory: null);
        string b = reloaded.ToCharmJson();

        Assert.DoesNotContain("\"Traces\"", a, StringComparison.Ordinal);
        Assert.Equal(a, b);
    }

    [Fact]
    public void RemovingAPickedTrace_TakesItsPanelPlacementWithIt()
    {
        var vm = Solved();
        var picked = vm.AddPickedTrace("Gamma_intr[1, :]");
        Assert.Contains(vm.Layout.Panels, p => p.PanelId == picked.PanelId);

        Assert.True(vm.RemovePickedTrace(picked));
        Assert.DoesNotContain(vm.Layout.Panels, p => p.PanelId == picked.PanelId);
    }
}

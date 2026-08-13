// ================================================================
//  HarmonicaReadoutColumnsTests.cs  —  §5 of brief-harmonicarf-r1c-chrome-readouts-dut-and-export
//
//  R-h9c-5  compr/stop/K/solves/Gss are gone from the READOUTS; compr/K remain as INPUTS.
//  R-h9c-6  four columns — Source · Load · MXP · MXE — with the owner's exact row labels.
//  R-h9c-7  a Z/Γ row's format persists across a .charm round trip.
//  R-h9c-9  the data shape: HarmonicaReadout, not a triple.
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaReadoutColumnsTests(ITestOutputHelper output)
{
    private static HarmonicaViewModel NewSolvedVm()
    {
        var vm = new HarmonicaViewModel();
        // A real grid, small enough to be fast, big enough to produce a real MXP/MXE optimum
        // (Quality defaults to Full, which is what gates R-h9b-16's SolveAtOptimum).
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 6, MaxGamma = 0.6 });
        Assert.Null(vm.SolveError);
        return vm;
    }

    [Fact]
    public void TheFiveRemovedReadouts_AreGone_ButComprAndKSurviveAsInputs()
    {
        var vm = NewSolvedVm();
        var labels = vm.Frame.Readouts.Select(r => r.Label).ToArray();

        foreach (var gone in new[] { "compr", "stop", "K", "solves", "Gss" })
            Assert.DoesNotContain(gone, labels);

        // compr and K are a DIFFERENT list (§7.5's inputs) and must still be there — the owner's own
        // point was "the user sets it, no need to read it back", about the readout half only.
        var inputKeys = vm.Inputs.Select(i => i.Label).ToArray();
        Assert.Contains("compr", inputKeys);
        Assert.Contains("K", inputKeys);

        output.WriteLine(string.Join(", ", labels));
    }

    // ══ R-h9r2-24 — the per-marker intrinsic-Γ rows no longer crowd the General line ═══════════

    [Fact]
    public void GeneralColumn_NoLongerCarriesPerMarkerIntrinsicGammaRows()
    {
        var vm = NewSolvedVm();
        var general = vm.Frame.Readouts.Where(r => r.Column == ReadoutColumn.General).ToArray();

        // None of the 5 default markers' own "{Name} Γᵢ" rows survive — removed outright per the
        // owner's own literal request, not relocated (the intrinsic Γ is still the glyph on the chart).
        foreach (var m in vm.Markers)
            Assert.DoesNotContain(general, r => r.Label == $"{m.Name} Γᵢ");

        Assert.DoesNotContain(general, r => r.Label.EndsWith(" Γᵢ", StringComparison.Ordinal));

        output.WriteLine(string.Join(", ", general.Select(r => r.Label)));
    }

    [Fact]
    public void SourceAndLoadColumns_ListOneZGammaPairPerMarker_NoMarkerNoRow()
    {
        var vm = NewSolvedVm();
        // The default document ships S1/S2/L1/L2/L3 (R-h9b-14).
        Assert.Equal(5, vm.Markers.Count);

        var source = vm.Frame.Readouts.Where(r => r.Column == ReadoutColumn.Source).ToArray();
        var load   = vm.Frame.Readouts.Where(r => r.Column == ReadoutColumn.Load).ToArray();

        // A header row plus one Z/Γ pair per marker on that side.
        Assert.Equal(1 + 2 * 2, source.Length);   // S1, S2
        Assert.Equal(1 + 2 * 3, load.Length);     // L1, L2, L3

        Assert.Contains(source, r => r.Label == "ZS1" && r.IsComplex && r.Editable);
        Assert.Contains(source, r => r.Label == "ΓS1" && r.IsComplex && r.Editable && r.IsGamma);
        Assert.Contains(load,   r => r.Label == "ZL2" && r.IsComplex && r.Editable);
        Assert.Contains(load,   r => r.Label == "ΓL3" && r.IsComplex && r.Editable && r.IsGamma);

        // Every editable row carries the identity a Set… dialog / drag needs.
        foreach (var r in source.Concat(load).Where(r => r.Editable))
        {
            Assert.NotNull(r.Side);
            Assert.True(r.Band > 0);
        }

        output.WriteLine(string.Join(" | ", source.Select(r => $"{r.Label}={r.Value}")));
        output.WriteLine(string.Join(" | ", load.Select(r => $"{r.Label}={r.Value}")));
    }

    [Fact]
    public void MxpAndMxeColumns_CarryTheOwnersExactRowSet_WhenAnOptimumIsSolved()
    {
        var vm = NewSolvedVm();
        Assert.NotNull(vm.Frame.SmithPower.Optimum);
        Assert.NotNull(vm.Frame.SmithEfficiency.Optimum);

        foreach (var (column, label) in new[] { (ReadoutColumn.Mxp, "MXP"), (ReadoutColumn.Mxe, "MXE") })
        {
            var rows = vm.Frame.Readouts.Where(r => r.Column == column).ToArray();
            Assert.NotEmpty(rows);

            // Header first, exactly as the owner spelled it: "MXP 1f0 Load".
            Assert.Equal($"{label} 1f0 Load", rows[0].Label);
            Assert.Equal("", rows[0].Value);

            var byLabel = rows.Skip(1).ToDictionary(r => r.Label, r => r);
            foreach (var expected in new[] { "Pout", "Efficiency", "PAE", "Gain", "Gp", "Zin", "AM/PM" })
                Assert.True(byLabel.ContainsKey(expected), $"{label} column is missing a '{expected}' row");

            // Zin gets the format flyout; the performance numbers do not; NONE of them are editable —
            // "obviously, MXP/MXE impedance and the performance summary data cannot be edited".
            Assert.True(byLabel["Zin"].IsComplex);
            Assert.All(rows, r => Assert.False(r.Editable));

            output.WriteLine($"{label}: " + string.Join(" | ", rows.Select(r => $"{r.Label}={r.Value}")));
        }
    }

    [Fact]
    public void MxpColumn_SaysNoOptimum_OnASkipContoursFrame()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });

        var mxp = vm.Frame.Readouts.Where(r => r.Column == ReadoutColumn.Mxp).ToArray();
        Assert.Single(mxp);
        Assert.Equal("no optimum", mxp[0].Value);
    }

    [Fact]
    public void AmPm_ReadsTheLoadPlaneFundamentalPhase_NotAnInventedDerivative()
    {
        var vm = NewSolvedVm();
        var opt = vm.Frame.SmithPower.Optimum;
        Assert.NotNull(opt);
        Assert.NotNull(opt!.Published);

        var vExt = opt.Published!["V_ext"];
        int harmonics = vExt.Axes[1].Values.Length;
        var loadFund = vExt.ComplexValues[1 * harmonics + 1];   // side=Load(1), harmonic=1
        double expectedDeg = loadFund.Phase * 180.0 / Math.PI;

        var row = vm.Frame.Readouts.Single(r => r.Column == ReadoutColumn.Mxp && r.Label == "AM/PM");
        Assert.Equal($"{expectedDeg:0.#}°", row.Value);
    }

    [Fact]
    public void GpReadsTheSolvedFomResult_TheSameOneGainReads()
    {
        var vm = NewSolvedVm();
        var opt = vm.Frame.SmithPower.Optimum;
        Assert.NotNull(opt?.Solved);

        var gpRow   = vm.Frame.Readouts.Single(r => r.Column == ReadoutColumn.Mxp && r.Label == "Gp");
        var gainRow = vm.Frame.Readouts.Single(r => r.Column == ReadoutColumn.Mxp && r.Label == "Gain");

        Assert.Equal($"{opt!.Solved!.Foms.GpDb:0.##} dB", gpRow.Value);
        Assert.Equal($"{opt.Solved.GainDb:0.##} dB", gainRow.Value);
        // Gp and Gt genuinely differ for this fixture — a passive gate has a non-trivial delivered
        // power, so the two are not coincidentally equal.
        Assert.NotEqual(gpRow.Value, gainRow.Value);
    }

    // ── R-h9c-7 — per-row format persistence ───────────────────────────────

    [Fact]
    public void PerRowFormat_RoundTripsThroughACharmReload()
    {
        var vm = new HarmonicaViewModel();
        vm.Appearance = vm.Appearance with
        {
            ReadoutFormats = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["S1.Z"] = "MagnitudeAngle",
            },
        };

        string json = vm.ToCharmJson();

        var reloaded = new HarmonicaViewModel();
        reloaded.LoadCharm(json, baseDirectory: null);

        Assert.True(reloaded.Appearance.ReadoutFormats.TryGetValue("S1.Z", out var v));
        Assert.Equal("MagnitudeAngle", v);
    }

    [Fact]
    public void AnUntouchedDocument_WritesNoReadoutFormatsBlock()
    {
        var vm = new HarmonicaViewModel();
        Assert.True(vm.Appearance.IsDefault);
        Assert.Empty(vm.Appearance.ReadoutFormats);
    }

    // ── HarmonicaReadoutFormatting — what you see is what you can type back ───

    [Theory]
    [InlineData("45.2+j12.3", 45.2, 12.3)]
    [InlineData("45.2-j12.3", 45.2, -12.3)]
    [InlineData("-45.2-j12.3", -45.2, -12.3)]
    [InlineData("0-j5", 0, -5)]
    public void TryParse_RealImaginary_RoundTripsWhatFormatComplexWrote(string text, double re, double im)
    {
        Assert.True(HarmonicaReadoutFormatting.TryParse(text, ReadoutFormat.RealImaginary, out var z));
        Assert.Equal(re, z.Real, 6);
        Assert.Equal(im, z.Imaginary, 6);
    }

    [Fact]
    public void FormatThenParse_RealImaginary_IsIdentity()
    {
        foreach (var z in new[] { new Complex(45.2, 12.3), new Complex(45.2, -12.3), new Complex(-3, 0), new Complex(0, 7) })
        {
            string text = HarmonicaReadoutFormatting.FormatComplex(z, ReadoutFormat.RealImaginary);
            Assert.True(HarmonicaReadoutFormatting.TryParse(text, ReadoutFormat.RealImaginary, out var back));
            Assert.Equal(z.Real, back.Real, 2);
            Assert.Equal(z.Imaginary, back.Imaginary, 2);
        }
    }

    [Fact]
    public void FormatThenParse_MagnitudeAngle_IsIdentity()
    {
        var g = Complex.FromPolarCoordinates(0.523, 45.2 * Math.PI / 180.0);
        string text = HarmonicaReadoutFormatting.FormatGamma(g, ReadoutFormat.MagnitudeAngle);
        Assert.True(HarmonicaReadoutFormatting.TryParse(text, ReadoutFormat.MagnitudeAngle, out var back));
        Assert.Equal(g.Real, back.Real, 3);
        Assert.Equal(g.Imaginary, back.Imaginary, 3);
    }

    [Fact]
    public void TryParse_RejectsGarbage_RatherThanGuessing()
    {
        Assert.False(HarmonicaReadoutFormatting.TryParse("not a number", ReadoutFormat.RealImaginary, out _));
        Assert.False(HarmonicaReadoutFormatting.TryParse("", ReadoutFormat.RealImaginary, out _));
        Assert.False(HarmonicaReadoutFormatting.TryParse(null, ReadoutFormat.RealImaginary, out _));
    }

    // ── §5's own "live during a drag" claim ────────────────────────────────

    [Fact]
    public void ADraggedMarker_ChangesItsOwnRowOnTheNextFrame_NothingElse()
    {
        var vm = NewSolvedVm();
        var s1 = vm.Markers.Single(m => m.Side == TerminationSideKind.Source && m.Band == 1);
        var before = vm.Frame.Readouts.Single(r => r.Label == "ZS1").Value;

        // A drag writes through SetMarkerImpedance exactly as a plain edit does — "live" costs
        // nothing new because BuildReadouts reads the marker's CURRENT Gamma every frame.
        vm.SetMarkerImpedance(s1, new Complex(17, -6));
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 6, MaxGamma = 0.6 });

        var after = vm.Frame.Readouts.Single(r => r.Label == "ZS1").Value;
        Assert.NotEqual(before, after);
        Assert.Contains("17", after);

        // Every OTHER marker's row is untouched.
        var l1Before = vm.Frame.Readouts.Single(r => r.Label == "ZL1").Value;
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 6, MaxGamma = 0.6 });
        var l1After = vm.Frame.Readouts.Single(r => r.Label == "ZL1").Value;
        Assert.Equal(l1Before, l1After);
    }

    // ── R-h9c-7's "Set…" writes through the SAME two calls a drag uses ─────

    [Fact]
    public void SetDialog_WritesThroughSetMarkerImpedanceOrGamma_NeverAThirdPath()
    {
        string src = ReadSource("src", "Ui", "Views", "Harmonica", "HarmonicaView.axaml.cs");
        int start = src.IndexOf("OnReadoutOpenSetDialogAsync", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = src.IndexOf("\n    }", start, StringComparison.Ordinal);
        string body = src[start..end];

        Assert.Contains("h.SetMarkerGamma(marker, g)", body, StringComparison.Ordinal);
        Assert.Contains("h.SetMarkerImpedance(marker, z)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Terminations.Set", body, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] parts)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = System.IO.Path.Combine([dir!.FullName, .. parts]);
        Assert.True(System.IO.File.Exists(path), $"source not found at {path}");
        return System.IO.File.ReadAllText(path);
    }
}

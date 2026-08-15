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
        // R6C §3 renamed their LABELS ("compr" -> "Compression:", "K" -> "Harmonic Order:") without
        // touching their KEYS, which is what this asserts against.
        var inputKeys = vm.Inputs.Select(i => i.Key).ToArray();
        Assert.Contains(HarmonicaInputs.KeyCompression, inputKeys);
        Assert.Contains(HarmonicaInputs.KeyHarmonicCount, inputKeys);

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
        // R8B §3 — the default document now ships L1/L2/L3 only; S1/S2 start with no marker.
        Assert.Equal(3, vm.Markers.Count);

        var source = vm.Frame.Readouts.Where(r => r.Column == ReadoutColumn.Source).ToArray();
        var load   = vm.Frame.Readouts.Where(r => r.Column == ReadoutColumn.Load).ToArray();

        // R8B §3 — a bare header, no marker rows, on the source side; its tooltip names the fix.
        Assert.Single(source);
        Assert.Equal("Source", source[0].Label);
        Assert.Contains("right-click the Smith chart", source[0].Tooltip, StringComparison.Ordinal);

        // R-hui-1 — the Γ rows are gone (owner: redundant with ZL*/ZS*, "keep ZL1, ZL2… remove
        // ΓL1, ΓL2…"). A header row plus one Z row per marker on the load side.
        Assert.Equal(1 + 3, load.Length);     // L1, L2, L3

        Assert.Contains(load, r => r.Label == "ZL2" && r.IsComplex && r.Editable);
        Assert.DoesNotContain(source, r => r.IsGamma);
        Assert.DoesNotContain(load,   r => r.IsGamma);
        Assert.DoesNotContain(source.Concat(load), r => r.Label.StartsWith('Γ'));

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
    public void SourceColumn_ListsARow_OnceAMarkerIsAdded()
    {
        var vm = new HarmonicaViewModel();
        vm.AddMarkerBand(TerminationSideKind.Source, 1);
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 6, MaxGamma = 0.6 });

        var source = vm.Frame.Readouts.Where(r => r.Column == ReadoutColumn.Source).ToArray();
        Assert.Equal(1 + 1, source.Length);
        Assert.Contains(source, r => r.Label == "ZS1" && r.IsComplex && r.Editable);
        Assert.Equal("", source[0].Tooltip);
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

            // R8C §1 — the header now carries the optimum's REAL impedance, named by the termination
            // it corresponds to: "MXP 1f0 ZL1=<real Z>". The Z itself is solve-dependent, so this
            // checks the shape rather than an exact number.
            Assert.StartsWith($"{label} 1f0 ZL1=", rows[0].Label, StringComparison.Ordinal);
            Assert.EndsWith(" Ω", rows[0].Label, StringComparison.Ordinal);
            Assert.Equal("", rows[0].Value);

            var byLabel = rows.Skip(1).ToDictionary(r => r.Label, r => r);
            // R-hui-7 — Pdc joined, matching the P-3dB (OperatingPoint) chunk's own row set.
            foreach (var expected in new[] { "Pout", "Eff", "PAE", "Gain", "Gp", "Zin", "AM/PM", "Pdc" })
                Assert.True(byLabel.ContainsKey(expected), $"{label} column is missing a '{expected}' row");

            // Zin gets the format flyout; the performance numbers do not; NONE of them are editable —
            // "obviously, MXP/MXE impedance and the performance summary data cannot be edited".
            Assert.True(byLabel["Zin"].IsComplex);
            Assert.All(rows, r => Assert.False(r.Editable));

            output.WriteLine($"{label}: " + string.Join(" | ", rows.Select(r => $"{r.Label}={r.Value}")));
        }
    }

    [Fact]
    public void MxpHeader_CarriesTheRealOptimumImpedance_NotTheMarkers()
    {
        var vm = NewSolvedVm();
        var optimum = vm.Frame.SmithPower.Optimum;
        Assert.NotNull(optimum);

        double z0 = vm.Frame.SmithPower.Z0;
        var expectedZ = HarmonicaDataSet.ImpedanceOf(optimum!.Gamma, z0);
        // R9A §4 — the header's own Z is the COMPACT (1-decimal) form, not FormatZ's three decimals:
        // an argmax off a fitted RBF surface does not carry that precision.
        string expectedZText = HarmonicaReadoutFormatting.FormatZCompact(expectedZ, ReadoutFormat.RealImaginary);

        var mxp = vm.Frame.Readouts.Where(r => r.Column == ReadoutColumn.Mxp).ToArray();
        Assert.StartsWith($"MXP 1f0 ZL1={expectedZText}", mxp[0].Label, StringComparison.Ordinal);

        // The marker's OWN Z (whatever L1's termination happens to be) is a different quantity — the
        // header must not silently read that instead (§1.2's own rule).
        var l1Marker = vm.Markers.Single(m => m.Side == TerminationSideKind.Load && m.Band == 1);
        var markerZ = HarmonicaDataSet.ImpedanceOf(l1Marker.Gamma, z0);
        Assert.NotEqual(expectedZ, markerZ);

        output.WriteLine($"header: {mxp[0].Label}, marker Z = {markerZ}");
    }

    [Fact]
    public void MxpColumn_SaysNoOptimum_OnASkipContoursFrame()
    {
        // R7C §1.4 — a "no optimum" frame no longer collapses the chunk to one row: the row SHAPE
        // (count and order) is IDENTICAL to a solved frame's, or the whole chunk rebuilds and the
        // 2 x 4 grid re-measures every time the ladder flips between rung kinds — structural churn at
        // frame rate. Only the header's own text states the situation now, and every value is "—".
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });

        var mxp = vm.Frame.Readouts.Where(r => r.Column == ReadoutColumn.Mxp).ToArray();
        Assert.Equal(10, mxp.Length);   // header + Pout/Eff/PAE/Gain/Gp/Zin/AM-PM/Pdc/γ
        Assert.Contains("no optimum", mxp[0].Label, StringComparison.Ordinal);
        Assert.Equal("", mxp[0].Value);

        string[] expectedLabels = ["Pout", "Eff", "PAE", "Gain", "Gp", "Zin", "AM/PM", "Pdc", "γ"];
        for (int i = 0; i < expectedLabels.Length; i++)
        {
            Assert.Equal(expectedLabels[i], mxp[i + 1].Label);
            Assert.Equal("—", mxp[i + 1].Value);
        }

        // The row SHAPE (label/IsComplex/Editable) must match the solved-frame case exactly, or
        // ReadoutStripView's own signature comparison rebuilds the chunk on every optimum flip.
        Assert.True(mxp[6].IsComplex);    // Zin
        Assert.False(mxp[9].IsComplex);   // γ — always non-complex, per §2.4
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
        Assert.Equal(HarmonicaReadoutFormatting.FormatDegrees(expectedDeg), row.Value);
    }

    [Fact]
    public void GpReadsTheSolvedFomResult_TheSameOneGainReads()
    {
        // R8B §3 changed the fresh document's default Source band-1 termination from 25 Ω to 50 Ω
        // (matching the DUT's own input impedance) — a real, near-matched source, which is exactly
        // the degenerate case where Gp and Gt can coincide. Set S1 back to this test's own original,
        // deliberately off-matched fixture value so it still exercises "Gp and Gt genuinely differ".
        var vm = new HarmonicaViewModel();
        vm.SetMarkerImpedance(vm.AddMarkerBand(TerminationSideKind.Source, 1), new Complex(25, 0));
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 6, MaxGamma = 0.6 });
        Assert.Null(vm.SolveError);

        var opt = vm.Frame.SmithPower.Optimum;
        Assert.NotNull(opt?.Solved);

        var gpRow   = vm.Frame.Readouts.Single(r => r.Column == ReadoutColumn.Mxp && r.Label == "Gp");
        var gainRow = vm.Frame.Readouts.Single(r => r.Column == ReadoutColumn.Mxp && r.Label == "Gain");

        Assert.Equal(HarmonicaReadoutFormatting.FormatDb(opt!.Solved!.Foms.GpDb), gpRow.Value);
        Assert.Equal(HarmonicaReadoutFormatting.FormatDb(opt.Solved.GainDb), gainRow.Value);
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
        // R8B §3 — no source marker on a fresh document; add S1 explicitly.
        var vm = new HarmonicaViewModel();
        var s1 = vm.AddMarkerBand(TerminationSideKind.Source, 1);
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 6, MaxGamma = 0.6 });
        Assert.Null(vm.SolveError);
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

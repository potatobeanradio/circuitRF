using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Ui.Signals;
using CircuitRF.Ui.ViewModels;
using RfCore.Data;
using RfCore.Signals;

namespace CircuitRF.Ui.Tests.Signals;

/// <summary>
/// The MOD_Source Signal Designer: what it reads from the part, what Apply writes back (one undo step,
/// expressions untouched), the presets and carrier rows, and the preview data it plots.
/// </summary>
[Collection(UserStateDirectoryCollection.Name)]
public class SignalDesignerTests : FirstLaunchUserState
{
    private static (SchematicViewModel vm, EditableComponent src) Bench(string fc = "RFfreq", string fcUnit = "",
                                                                          string pavg = "Pin")
    {
        var m = new SchematicEditModel();
        var s = new EditableComponent
        {
            InstanceName = "S1", Symbol = SymbolKind.ModSource,
            Parameters =
            {
                new EditableParameter { Name = "Fc", Expression = fc, Unit = fcUnit, Dimension = UnitDimension.Frequency },
                new EditableParameter { Name = "Pavg", Expression = pavg, Unit = "dBm", Dimension = UnitDimension.Power },
                new EditableParameter { Name = "Z", Expression = "50", Unit = "Ω", Dimension = UnitDimension.Resistance },
                new EditableParameter { Name = "Carrier[1]", Expression = "\"NR;BW=20e6;SCS=30e3;Mod=64QAM;Off=-200e6;P=0;Seed=1\"" },
                new EditableParameter { Name = "Carrier[2]", Expression = "\"LTE;BW=20e6;Mod=64QAM;Off=200e6;P=-3;Seed=2\"" },
                new EditableParameter { Name = "Record", Expression = "1e-3" },
            },
        };
        m.Components.Add(s);
        return (new SchematicViewModel(m, messageSink: null), s);
    }

    private static SignalDesignerViewModel Open(SchematicViewModel vm, EditableComponent s)
    {
        var d = new SignalDesignerViewModel();
        d.SetTarget(vm, s);
        return d;
    }

    [Fact]
    public void Load_ReadsEveryCarrier_AndKeepsFcAndPavgExpressions()
    {
        var (vm, s) = Bench();
        using var d = Open(vm, s);
        Assert.Equal(2, d.Carriers.Count);
        Assert.Equal(("NR", 20.0, 30.0, -200.0, 1), (d.Carriers[0].Standard, d.Carriers[0].BandwidthMHz, d.Carriers[0].ScsKHz, d.Carriers[0].OffsetMHz, d.Carriers[0].Seed));
        Assert.Equal(("LTE", 200.0, -3.0, 2), (d.Carriers[1].Standard, d.Carriers[1].OffsetMHz, d.Carriers[1].PowerDb, d.Carriers[1].Seed));
        Assert.Equal("RFfreq", d.FcText);
        Assert.Equal("Pin", d.PavgText);
    }

    [Fact]
    public void Load_ShowsANumericFcInGHz()
    {
        var (vm, s) = Bench(fc: "2.14e9", fcUnit: "", pavg: "12.5");
        using var d = Open(vm, s);
        Assert.Equal("2.14", d.FcText);
        Assert.Equal("12.5", d.PavgText);
    }

    [Fact]
    public void Apply_KeepsUnchangedExpressions_RoundTripsCarriers_AndIsOneUndoStep()
    {
        var (vm, s) = Bench();
        using var d = Open(vm, s);
        d.Carriers[1].OffsetMHz = 300;
        d.ApplyCommand.Execute(null);

        string P(string n) => s.Parameters.First(p => p.Name == n).Expression;
        Assert.Equal("RFfreq", P("Fc"));
        Assert.Equal("Pin", P("Pavg"));
        Assert.Equal("50", P("Z"));
        var c2 = SignalSpec.ParseCarrier(P("Carrier[2]").Trim('"'));
        Assert.Equal(300e6, c2.OffsetHz);
        Assert.Equal(CarrierStandard.Lte, c2.Standard);
        Assert.Equal(-3, c2.PowerDb);

        vm.UndoRedo.Undo();
        Assert.Equal(200e6, SignalSpec.ParseCarrier(s.Parameters.First(p => p.Name == "Carrier[2]").Expression.Trim('"')).OffsetHz);
    }

    [Fact]
    public void Apply_ANumberTypedForFc_IsWrittenInGHz()
    {
        var (vm, s) = Bench();
        using var d = Open(vm, s);
        d.FcText = "3.5";
        d.PavgText = "20";
        d.ApplyCommand.Execute(null);
        var fc = s.Parameters.First(p => p.Name == "Fc");
        var pa = s.Parameters.First(p => p.Name == "Pavg");
        Assert.Equal(("3.5", "GHz"), (fc.Expression, fc.Unit));
        Assert.Equal(("20", "dBm"), (pa.Expression, pa.Unit));
    }

    [Fact]
    public void Presets_AddAndRemove_ProduceTheCarriersTheyName()
    {
        var (vm, s) = Bench();
        using var d = Open(vm, s);

        d.PresetSpacingMHz = 400;
        d.PresetTwoCarrierCommand.Execute(null);
        Assert.Equal(new[] { -200.0, 200.0 }, d.Carriers.Select(c => c.OffsetMHz));
        Assert.All(d.Carriers, c => Assert.Equal("NR", c.Standard));

        d.PresetFourCarrierLteCommand.Execute(null);
        Assert.Equal(new[] { -30.0, -10.0, 10.0, 30.0 }, d.Carriers.Select(c => c.OffsetMHz));

        d.PresetNr100Command.Execute(null);
        Assert.Equal((1, "NR", 100.0), (d.Carriers.Count, d.Carriers[0].Standard, d.Carriers[0].BandwidthMHz));
        d.PresetNr100Fr2Command.Execute(null);
        Assert.Equal((1, "NR", 100.0, 120.0), (d.Carriers.Count, d.Carriers[0].Standard, d.Carriers[0].BandwidthMHz, d.Carriers[0].ScsKHz));
        Assert.Contains(400.0, d.Carriers[0].BandwidthOptions);
        d.PresetNr100Command.Execute(null);

        d.AddCarrierCommand.Execute(null);
        Assert.Equal(2, d.Carriers.Count);
        Assert.Equal(100.0, d.Carriers[1].OffsetMHz);   // placed one channel above the last
        Assert.Equal(new[] { 1, 2 }, d.Carriers.Select(c => c.Index));
        d.RemoveCarrierCommand.Execute(d.Carriers[0]);
        Assert.Equal((1, 1), (d.Carriers.Count, d.Carriers[0].Index));
        d.RemoveCarrierCommand.Execute(d.Carriers[0]);   // the last carrier is never removed
        Assert.Single(d.Carriers);
    }

    [Fact]
    public void ChangingTheStandard_KeepsAValidBandwidth()
    {
        var row = new CarrierRowViewModel { Standard = "NR", ScsKHz = 30, BandwidthMHz = 100 };
        row.Standard = "LTE";
        Assert.Contains(row.BandwidthMHz, row.BandwidthOptions);
        row.Standard = "NR"; row.ScsKHz = 15;
        Assert.Contains(row.BandwidthMHz, row.BandwidthOptions);
    }

    [Fact]
    public void Analyse_PlotsTheSignalAgainstItsOffset_FramedOnTheOccupiedBand()
    {
        var one = SignalComposer.Generate(new SignalSpec
        {
            Carriers = [new CarrierSpec { Standard = CarrierStandard.Lte, BandwidthHz = 20e6 }],
            RecordSeconds = 0.5e-3,
        });
        var (ds, text) = SignalDesignerViewModel.Analyse(one);
        var psd = ds["PSD"];
        var f = psd.Axes[0].Values;
        Assert.InRange(f.Min(), -65, -55);    // ±(10 + 2.5 x 20) MHz, in MHz
        Assert.InRange(f.Max(), 55, 65);
        var vals = psd.RealValues;
        int ipk = System.Array.IndexOf(vals, vals.Max());
        Assert.InRange(f[ipk], -10, 10);      // the peak is inside the channel
        Assert.True(vals.Where((v, i) => System.Math.Abs(f[i]) > 30).Max() < -40, "out-of-band PSD should sit far below the channel");
        var ccdf = ds["CCDF"].Axes[0].Values;
        Assert.True(ccdf.Min() >= 0, "the CCDF starts at the average power");
        Assert.InRange(ccdf.Max(), 8, 14);    // unclipped OFDM peaks
        var prob = ds["CCDF"].RealValues;     // probability in PERCENT, the standard CCDF axis
        Assert.InRange(prob[0], 20, 100);
        Assert.True(prob[^1] < 0.01);
        Assert.Contains("PAPR", text);
        Assert.Contains("ACPR1", text);

        var two = SignalComposer.Generate(new SignalSpec
        {
            Carriers = [new CarrierSpec { Standard = CarrierStandard.Nr, BandwidthHz = 20e6, OffsetHz = -200e6 },
                        new CarrierSpec { Standard = CarrierStandard.Nr, BandwidthHz = 20e6, OffsetHz = 200e6, Seed = 2 }],
            RecordSeconds = 0.5e-3,
        });
        var f2 = SignalDesignerViewModel.Analyse(two).ds["PSD"].Axes[0].Values;
        Assert.True(f2.Min() < -250 && f2.Max() > 250);
    }

    [Fact]
    public async Task Preview_FillsBothPlots_AndTheNumbers()
    {
        var (vm, s) = Bench();
        using var d = Open(vm, s);
        await d.PreviewAsync();
        Assert.True(d.PsdPlot.Traces.Count > 0, "status: " + d.Status);
        Assert.Equal(2, d.CcdfPlot.Traces.Count);                        // OFDM beside its Gaussian reference, named
        Assert.Equal(CircuitRF.Render.DataDisplay.LegendPlacement.Inside, d.CcdfPlot.Legend);
        Assert.Contains("2 carrier(s)", d.Stats);
        Assert.StartsWith("Ready", d.Status);
        Assert.Equal(CircuitRF.Render.DataDisplay.AxisScale.Log, d.CcdfPlot.Axes.YScale);
    }

    [Fact]
    public async Task Preview_FillsTheEvmTab_PerOfdmCarrier()
    {
        var (vm, s) = Bench();
        s.Parameters.Add(new EditableParameter { Name = "Clip", Expression = "8" });
        using var d = Open(vm, s);
        await d.PreviewAsync();
        Assert.True(d.HasEvm, "status: " + d.Status);
        Assert.Equal([1, 2], d.EvmRows.Select(r => r.CarrierIndex).ToArray());
        Assert.Same(d.EvmRows[0], d.SelectedEvm);
        Assert.All(d.EvmRows, r => Assert.InRange(r.Result.RmsPercent, 0.3, 8.0));
        Assert.EndsWith(" %", d.EvmRows[0].RmsText);

        // the constellation: received cloud (capped) and the 64 ideal points, on a square centred window
        Assert.Equal(2, d.ConstellationPlot.Traces.Count);
        Assert.InRange(d.ConstellationPlot.Traces[0].Points.Count, SignalDesignerViewModel.ConstellationPoints / 2, SignalDesignerViewModel.ConstellationPoints);
        Assert.Equal(64, d.ConstellationPlot.Traces[1].Points.Count);
        var w = d.ConstellationPlot.Axes.Window;
        Assert.Equal(w.Width, w.Height, 9);
        Assert.Equal(0, w.X + w.Width / 2, 9);

        // the envelope chunk: power, the average at 0 dB and the peak line at the peak-to-average ratio
        Assert.Equal(6, d.EnvelopePlot.Traces.Count);                 // power, average, peak + the arrow (shaft, two heads)
        var power = d.EnvelopePlot.Traces[0].Points;
        float peak = d.EnvelopePlot.Traces[2].Points[0].Y;
        Assert.Equal(power.Max(p => p.Y), peak, 3);
        Assert.All(d.EnvelopePlot.Traces[1].Points, p => Assert.Equal(0f, p.Y));
        Assert.StartsWith(peak.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), d.PaprText);
        // the default window, in ns, centred on the peak; the field re-cuts it without regenerating
        Assert.InRange(power[^1].X - power[0].X, 1990, 2010);   // 2 µs
        d.EnvelopeWindowUs = 0.5;
        var shorter = d.EnvelopePlot.Traces[0].Points;
        Assert.InRange(shorter[^1].X - shorter[0].X, 490, 510);
        Assert.True(shorter.Count > 1000, "a short window is re-interpolated finely");
        Assert.Equal(peak, d.EnvelopePlot.Traces[2].Points[0].Y, 1);

        // no picker on the tab: it follows the carrier selected on the left, and says which signal it measured
        Assert.StartsWith("C1 of 2:  NR 20 MHz, 30 kHz SCS, 64QAM,  offset -200 MHz", d.EvmSignalText);
        Assert.Contains("clipped to 8 dB PAPR", d.EvmSignalText);
        d.SelectCarrierCommand.Execute(d.Carriers[1]);
        Assert.True(d.Carriers[1].IsSelected && !d.Carriers[0].IsSelected);
        Assert.Same(d.EvmRows[1], d.SelectedEvm);
        Assert.Contains("C2", d.ConstellationPlot.CustomTitle);
        Assert.StartsWith("C2 of 2:  LTE 20 MHz, 64QAM,  offset +200 MHz", d.EvmSignalText);
    }

    [Fact]
    public async Task ExportSignal_WritesTheCompositeAsIq_ThatImportsBack()
    {
        var (vm, s) = Bench();
        using var d = Open(vm, s);
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "crf-export-" + System.Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            Assert.True(await d.ExportSignalAsync(path), d.Status);
            var back = IqFile.Read(path);
            var sig = SignalCache.Get(d.BuildSpec());
            double rate = sig.SuggestedCompositeRateHz();
            Assert.Equal(rate, back.SampleRateHz);
            var x = sig.Composite(rate);
            Assert.Equal(x.Length, back.Samples.Length);
            for (int i = 0; i < x.Length; i += 9973)
                Assert.True((x[i] - back.Samples[i]).Magnitude < 1e-7, $"sample {i}");
            Assert.InRange(back.Samples.Average(z => z.Magnitude * z.Magnitude), 0.98, 1.02);   // unit mean power
            var head = System.IO.File.ReadLines(path).Take(16).ToList();
            Assert.Contains(head, l => l.StartsWith("# carrier C2: LTE 20 MHz, 64QAM"));
            Assert.Contains(head, l => l.StartsWith("# SAMPLE RATE 983.04 MS/s (983040000 Hz)"));
            Assert.Contains(head, l => l == "# samples " + x.Length);
            Assert.Contains(head, l => l == "# columns I Q");
            Assert.Contains(head, l => l.Contains("Fc = RFfreq"));
            Assert.StartsWith("Exported", d.Status);
        }
        finally { System.IO.File.Delete(path); }
    }

    /// <summary>Every preview rebuilds the traces. A marker the user placed survives it on the new trace, with its
    /// one info box pointing there (a box on a discarded trace reads nothing), and Delete removes it.</summary>
    [Fact]
    public async Task AMarker_SurvivesAPreview_WithItsInfoBox_AndDeletes()
    {
        using var d = await OpenLte();
        var before = d.PsdPlot.Traces[0];
        var marker = new CircuitRF.Render.DataDisplay.Marker(before, 0.0, false, false, 1, CircuitRF.Render.DataDisplay.FreqUnit.GHz)
            { PositionStatic = before.Points[before.Points.Count / 2] };
        before.Markers.Add(marker);
        d.PsdContainer.OnPlotChanged(null, System.EventArgs.Empty);     // what the plot raises on adding one

        await d.PreviewAsync();

        var after = d.PsdPlot.Traces[0];
        Assert.NotSame(before, after);
        Assert.Contains(marker, after.Markers);
        var box = Assert.Single(d.PlotHost.MarkerInfoBoxes);
        Assert.Same(after, box.Trace);

        box.SelectOnly();
        d.DeleteSelectedMarkersCommand.Execute(null);
        Assert.Empty(after.Markers);
        Assert.Empty(d.PlotHost.MarkerInfoBoxes);
    }

    /// <summary>One LTE 20 MHz carrier, opened in the Designer and previewed.</summary>
    private static async Task<SignalDesignerViewModel> OpenLte()
    {
        var (vm, s) = Bench();
        foreach (var p in s.Parameters.Where(p => p.Name.StartsWith("Carrier[")).ToList()) s.Parameters.Remove(p);
        s.Parameters.Add(new EditableParameter { Name = "Carrier[1]", Expression = "\"LTE;BW=20e6;Mod=64QAM;Off=0;P=0;Seed=1\"" });
        var d = Open(vm, s);
        await d.PreviewAsync();
        return d;
    }

    [Theory]
    [InlineData("QPSK", 4)]
    [InlineData("16QAM", 16)]
    [InlineData("64QAM", 64)]
    [InlineData("256QAM", 256)]
    [InlineData("1024QAM", 1024)]
    public async Task Calculate_FollowsTheModulationOnTheLeft(string mod, int points)
    {
        using var d = await OpenLte();
        int repaints = 0;
        d.PlotsUpdated += (_, _) => repaints++;
        d.Carriers[0].Modulation = mod;
        await d.CalculateEvmCommand.ExecuteAsync(null);

        Assert.True(d.EvmSignalText.Contains(mod), d.Status + " | " + d.EvmSignalText);
        Assert.Contains(mod, d.SelectedEvm!.Label);
        Assert.Equal(points, d.ConstellationPlot.Traces[1].Points.Count);            // the ideal grid of THAT order
        var received = d.ConstellationPlot.Traces[0].Points;
        Assert.InRange(received.Count, 1000, SignalDesignerViewModel.ConstellationPoints);
        // every received point sits on one of that order's ideal points
        var ideal = d.ConstellationPlot.Traces[1].Points;
        double pitch = 2.0 / System.Math.Sqrt(2.0 * (points - 1) / 3.0);            // spacing of the unit-power grid
        Assert.All(received.Take(200), r => Assert.True(ideal.Min(i => System.Math.Abs(i.X - r.X) + System.Math.Abs(i.Y - r.Y)) < 0.1 * pitch));
        Assert.InRange(d.SelectedEvm.Result.RmsPercent, 0, 0.5);                       // a clean source
        Assert.True(repaints > 0, "the plots must be told to repaint");
    }

    [Fact]
    public async Task Calculate_FollowsAStandardChange_AndEveryNumberMoves()
    {
        using var d = await OpenLte();
        var lte = d.SelectedEvm!;
        d.Carriers[0].Standard = "NR";
        d.Carriers[0].ScsKHz = 30;
        d.Carriers[0].Modulation = "256QAM";
        await d.CalculateEvmCommand.ExecuteAsync(null);
        Assert.NotSame(lte, d.SelectedEvm);
        Assert.StartsWith("C1 of 1:  NR 20 MHz, 30 kHz SCS, 256QAM", d.EvmSignalText);
        Assert.Contains("256QAM", d.ConstellationPlot.CustomTitle + d.SelectedEvm!.Label);
        Assert.Equal(256, d.ConstellationPlot.Traces[1].Points.Count);
        Assert.NotEqual(lte.Result.Subcarriers, d.SelectedEvm.Result.Subcarriers);  // 1200 LTE vs 612 NR
    }

    [Fact]
    public async Task EnvelopeWindow_IsViewOnly_AndRedrawsAtOnce()
    {
        using var d = await OpenLte();
        int repaints = 0;
        d.PlotsUpdated += (_, _) => repaints++;
        d.EnvelopeWindowUs = 3;
        Assert.True(repaints > 0);
        var pts = d.EnvelopePlot.Traces[0].Points;
        Assert.InRange(pts[^1].X - pts[0].X, 2990, 3010);
        Assert.True(d.EnvelopePlot.Axes.Window.X < 0, "a clear strip left of the data holds the arrow");
        Assert.InRange(d.GapArrowX, d.EnvelopePlot.Axes.Window.X, 0);
        Assert.Equal(d.EnvelopePlot.Traces[2].Points[0].Y, d.GapDb, 3);
        Assert.False(d.EnvelopePlot.CustomTitleOn);

        // Apply never writes the view's window (nor the sample rate) to the source
        d.SampleRate = SignalDesignerViewModel.SampleRateOptions.First(o => o.Label == "245.76 MS/s");
        d.ApplyCommand.Execute(null);
        var names = d.EvmRows.Count >= 0 ? GetParameterNames(d) : [];
        Assert.DoesNotContain(names, n => n.Contains("Window", System.StringComparison.OrdinalIgnoreCase)
                                          || n.Contains("Rate", System.StringComparison.OrdinalIgnoreCase));
    }

    private static string[] GetParameterNames(SignalDesignerViewModel d)
    {
        var f = typeof(SignalDesignerViewModel).GetField("_target", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return ((EditableComponent)f.GetValue(d)!).Parameters.Select(p => p.Name).ToArray();
    }

    [Fact]
    public async Task SampleRate_DrivesThePreviewAndTheExport_AndRefusesARateTooLow()
    {
        using var d = await OpenLte();
        Assert.StartsWith("122.88 MS/s (auto)", d.SampleRateInfo);                    // LTE 20: the generator's own pick
        d.SampleRate = SignalDesignerViewModel.SampleRateOptions.First(o => o.Label == "61.44 MS/s");
        await d.PreviewAsync();
        Assert.StartsWith("61.44 MS/s", d.SampleRateInfo);
        Assert.Contains("61,440 samples per record", d.SampleRateInfo);
        Assert.Contains("rate 61.44 MS/s", d.Stats);

        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "crf-rate-" + System.Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            Assert.True(await d.ExportSignalAsync(path), d.Status);
            var back = IqFile.Read(path);
            Assert.Equal(61.44e6, back.SampleRateHz);
            Assert.Equal(61440, back.Samples.Length);
            Assert.Contains(System.IO.File.ReadLines(path).Take(6), l => l.StartsWith("# SAMPLE RATE 61.44 MS/s (61440000 Hz), sample period 16.276042 ns"));
        }
        finally { System.IO.File.Delete(path); }

        d.SampleRate = SignalDesignerViewModel.SampleRateOptions.First(o => o.Label == "15.36 MS/s");
        await d.PreviewAsync();
        Assert.Contains("cannot hold this signal", d.Status);
        Assert.Contains("30.72 MS/s or more", d.Status);
        Assert.False(await d.ExportSignalAsync(path));
        Assert.False(System.IO.File.Exists(path));
    }

    [Fact]
    public async Task Evm_SaysWhyNot_ForACwOnlySource()
    {
        var (vm, s) = Bench();
        foreach (var p in s.Parameters.Where(p => p.Name.StartsWith("Carrier[")).ToList()) s.Parameters.Remove(p);
        s.Parameters.Add(new EditableParameter { Name = "Carrier[1]", Expression = "\"CW;Off=0;P=0\"" });
        using var d = Open(vm, s);
        await d.PreviewAsync();
        Assert.False(d.HasEvm);
        Assert.Null(d.SelectedEvm);
        Assert.Contains("no reference symbols", d.EvmNote);
        Assert.StartsWith("C1 of 1:  CW", d.EvmSignalText);
        Assert.Empty(d.ConstellationPlot.Traces);
    }

    /// <summary>Load Custom: the signal becomes the one file, at the rate its header states; the carriers panel is the
    /// file's (custom mode); Apply stores the path WORKSPACE-relative with its rate, as an SnP's Touchstone is stored;
    /// the preview reads the file back through that stored path; and Remove custom file restores the carriers it
    /// replaced and takes the reference out of the schematic.</summary>
    [Fact]
    public async Task LoadCustom_StoresAWorkspaceRelativeFileWithItsRate_AndPreviewsIt()
    {
        string ws = System.IO.Directory.CreateTempSubdirectory("crf-loadcustom-").FullName;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(ws, "iq"));
            string path = System.IO.Path.Combine(ws, "iq", "a.txt");
            var x = Enumerable.Range(0, 30_720).Select(k => System.Numerics.Complex.FromPolarCoordinates(1, 0.37 * k * k)).ToArray();
            IqFile.Write(path, x, 30.72e6);

            var (vm, s) = Bench();
            vm.WorkspaceRootProvider = () => ws;
            using var d = new SignalDesignerViewModel();
            d.SetTarget(vm, s, preview: false);
            Assert.False(d.IsCustom);
            d.LoadCustomFile(path);

            Assert.True(d.IsCustom);
            Assert.Same(d.Carriers.Single(), d.CustomCarrier);
            Assert.Equal(("FILE", "iq/a.txt", 30.72), (d.Carriers[0].Standard, d.Carriers[0].FilePath, d.Carriers[0].SampleRateMHz));
            Assert.Equal("30.72", d.Carriers[0].SampleRateText);

            // A typed rate: an invalid one is marked and never reaches the signal; a unit is honoured.
            d.Carriers[0].SampleRateText = "fast";
            Assert.True(d.Carriers[0].HasSampleRateError);
            Assert.Equal(30.72, d.Carriers[0].SampleRateMHz);
            d.Carriers[0].SampleRateText = "0.03072 GHz";
            Assert.False(d.Carriers[0].HasSampleRateError);
            Assert.Equal(30.72, d.Carriers[0].SampleRateMHz, 9);

            await d.PreviewAsync();
            Assert.StartsWith("Ready", d.Status);
            Assert.StartsWith("30,720 samples at 30.72 MS/s = 1 ms record", d.CustomInfo);

            // Export is at the Source box's rate: the file (30.72 MS/s) is resampled to it.
            d.SampleRate = SignalDesignerViewModel.SampleRateOptions.Single(o => o.Hz == 61.44e6);
            string exported = System.IO.Path.Combine(ws, "export.txt");
            Assert.True(await d.ExportSignalAsync(exported), d.Status);
            var back = IqFile.Read(exported);
            Assert.Equal((61.44e6, 61_440), (back.SampleRateHz, back.Samples.Length));

            // File rate: the file's own rate, offered while it is loaded, exports it without resampling.
            d.SampleRate = d.SampleRates.Single(o => o.IsOwn);
            Assert.Equal("File rate (30.72 MS/s)", d.SampleRate.Label);
            Assert.True(await d.ExportSignalAsync(exported), d.Status);
            back = IqFile.Read(exported);
            Assert.Equal((30.72e6, 30_720), (back.SampleRateHz, back.Samples.Length));
            for (int i = 0; i < x.Length; i += 997)
                Assert.True((back.Samples[i] - x[i]).Magnitude < 1e-6, $"sample {i}");     // unit-power chirp, unchanged

            d.ApplyCommand.Execute(null);
            Assert.DoesNotContain(s.Parameters, p => p.Name == "Carrier[2]");
            var c1 = SignalSpec.ParseCarrier(s.Parameters.Single(p => p.Name == "Carrier[1]").Expression.Trim('"'));
            Assert.Equal((CarrierStandard.File, "iq/a.txt", (double?)30.72e6), (c1.Standard, c1.FilePath, c1.SampleRateHz));

            d.RemoveCustomCommand.Execute(null);
            Assert.False(d.IsCustom);
            Assert.DoesNotContain(d.SampleRates, o => o.IsOwn);
            Assert.Equal(0, d.SampleRate.Hz);                                       // a selected File rate falls back to Auto
            Assert.Equal(["NR", "LTE"], d.Carriers.Select(c => c.Standard));      // the carriers it had before
            d.ApplyCommand.Execute(null);
            Assert.DoesNotContain(s.Parameters, p => p.Expression.Contains("File="));
        }
        finally { System.IO.Directory.Delete(ws, recursive: true); }
    }

    // ── pulsed CW (brief ENV-PCW §3, gate 8) ────────────────────────────────

    private const string PulseText = "PCW;PRI=100us;PW=50us;Rise=0.5us;Fall=0.5us;Delay=10us;Off=0;P=0";

    private static (SchematicViewModel vm, EditableComponent src) PulseBench()
    {
        var (vm, s) = Bench(pavg: "18");
        foreach (var p in s.Parameters.Where(p => p.Name.StartsWith("Carrier[")).ToList()) s.Parameters.Remove(p);
        s.Parameters.Add(new EditableParameter { Name = "Carrier[1]", Expression = "\"" + PulseText + "\"" });
        s.Parameters.Add(new EditableParameter { Name = "Clip", Expression = "8" });
        return (vm, s);
    }

    [Fact]
    public void Pulse_LoadsItsCard_GreysWhatAPulseIgnores_AndAppliesBackUnchanged()
    {
        var (vm, s) = PulseBench();
        using var d = new SignalDesignerViewModel();
        d.SetTarget(vm, s, preview: false);
        var c = Assert.Single(d.Carriers);
        Assert.Equal(("PCW", "100", "50", "0.5", "0.5", "10"), (c.Standard, c.PriText, c.WidthText, c.RiseText, c.FallText, c.DelayText));
        Assert.Equal(("PRF 10 kHz", "duty 50 %"), (c.PrfText, c.DutyText));
        Assert.True(d.IsPulse);
        Assert.False(d.CanEditRecord || d.CanClip || d.CanShape || d.CanAddCarrier);
        Assert.Equal("Pulse-top power (dBm)", d.PowerLabel);
        Assert.StartsWith("Record average 14.97 dBm", d.PulsePowerInfo);
        Assert.Null(d.BuildSpec().ClipPaprDb);

        d.ApplyCommand.Execute(null);
        Assert.Equal("\"" + PulseText + "\"", s.Parameters.Single(p => p.Name == "Carrier[1]").Expression);
        Assert.DoesNotContain(s.Parameters, p => p.Name == "Clip");
    }

    [Fact]
    public void Pulse_FieldsRefuseAsTyped_OnTheFieldTheRefusalNames()
    {
        var (vm, s) = PulseBench();
        using var d = new SignalDesignerViewModel();
        d.SetTarget(vm, s, preview: false);
        var c = d.Carriers[0];

        c.WidthText = "fifty";                                       // not a time: marked, the value stays
        Assert.True(c.HasWidthError);
        Assert.Equal(50e-6, c.WidthS);
        c.WidthText = "0.1 ms";                                      // a time, but PW >= PRI: PW is marked, PRI is not
        Assert.Equal(100e-6, c.WidthS, 12);
        Assert.True(c.HasWidthError);
        Assert.False(c.HasPriError);
        Assert.Contains("must be shorter than PRI", c.WidthError);
        c.WidthText = "50";
        Assert.False(c.HasWidthError);

        c.DelayText = "";                                            // empty = centred, and says where that is
        Assert.Null(c.DelayS);
        Assert.False(c.HasDelayError);
        Assert.StartsWith("empty: centred, 24.", c.DelayInfo);
        d.ApplyCommand.Execute(null);
        Assert.DoesNotContain("Delay", s.Parameters.Single(p => p.Name == "Carrier[1]").Expression);
    }

    [Fact]
    public async Task Pulse_KeepsItsCcdf_AndThePulseTakesTheEvmTab()
    {
        var (vm, s) = PulseBench();
        using var d = Open(vm, s);
        await d.PreviewAsync();
        // gated by default, as an analyser gates a burst: the flat top against its own average, ~0 dB, with no Gaussian
        // reference beside it (a pulse is no kind of noise)
        Assert.Equal("CCDF", Assert.Single(d.CcdfPlot.Traces).CubeName);
        Assert.Equal("CCDF (pulse on-time)", d.CcdfPlot.CustomTitle);
        Assert.Equal(CircuitRF.Render.DataDisplay.LegendPlacement.Off, d.CcdfPlot.Legend);
        var t = Assert.Single(d.TimePlot.Traces);
        Assert.Equal("Pulse", t.CubeName);
        Assert.Equal("Pulse", d.SecondTabHeader);
        Assert.StartsWith("pCW   PRI 100 µs", d.Stats);
        Assert.InRange(PaprOf(d.Stats, "on-time PAPR "), 0, 0.1);

        d.GateCcdf = false;                                               // over the whole PRI: the duty cycle's step
        await d.PreviewAsync();
        Assert.Equal("CCDF", d.CcdfPlot.CustomTitle);
        Assert.InRange(PaprOf(d.Stats, "\nPAPR "), 2.9, 3.1);            // 1 / power duty, ~50 % here
    }

    private static double PaprOf(string stats, string label)
    {
        int i = stats.IndexOf(label, StringComparison.Ordinal) + label.Length;
        return double.Parse(stats[i..].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void NTonePreset_SetsEqualCwTonesCentredOnFc_WithNewmanPhasesFromThree()
    {
        var (vm, s) = Bench();
        using var d = new SignalDesignerViewModel();
        d.SetTarget(vm, s, preview: false);
        d.PresetToneSpacingMHz = 2;
        d.PresetNToneCommand.Execute(null);
        Assert.Equal(new[] { -1.0, 1.0 }, d.Carriers.Select(c => c.OffsetMHz));
        Assert.All(d.Carriers, c => Assert.Equal(("CW", 0.0), (c.Standard, c.PowerDb)));
        Assert.All(d.Carriers, c => Assert.Null(c.ToSpec().PhaseDeg));

        d.PresetTones = 3;
        d.PresetNToneCommand.Execute(null);
        Assert.Equal(new[] { -2.0, 0.0, 2.0 }, d.Carriers.Select(c => c.OffsetMHz));
        Assert.Equal(new double?[] { null, 60, 240 }, d.Carriers.Select(c => c.ToSpec().PhaseDeg));
    }

    [Fact]
    public void PulsePreset_IsOnePulsedCarrier()
    {
        var (vm, s) = Bench();
        using var d = new SignalDesignerViewModel();
        d.SetTarget(vm, s, preview: false);
        d.PresetPulseCommand.Execute(null);
        Assert.True(d.IsPulse);
        Assert.Equal("Pulse", d.SecondTabHeader);
    }

    // ── FSK ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Fsk_PresetCardApplyAndPreview()
    {
        var (vm, s) = Bench(fc: "2", fcUnit: "GHz");
        using var d = Open(vm, s);
        d.PresetWmbusC1Command.Execute(null);
        Assert.Equal("0.86895", d.FcText);                                    // a number moves to the mode's channel
        var c = Assert.Single(d.Carriers);
        // C mode, meter to other, is 2-FSK with no Gaussian filter (the GFSK is the other-to-meter link's, C2)
        Assert.Equal(("FSK", 100.0, 45.0, false, "PN9"), (c.Standard, c.BitRateKbps, c.DeviationKHz, c.IsGaussian, c.Data));
        Assert.True(d.IsFsk);
        Assert.False(d.CanEditRecord || d.CanClip || d.CanShape || d.CanAddCarrier);
        Assert.Equal("Frequency", d.SecondTabHeader);
        Assert.Equal("h = 0.9  ·  FSK  ·  511 bits  ·  record 5.11 ms", c.FskInfoText);
        c.Filter = CarrierRowViewModel.FilterOptions[1];                      // the card's filter: GFSK at BT 0.5
        Assert.Equal(3.2e6, Assert.Single(d.SampleRates, o => o.IsOwn).Hz);  // no standard rate divides 511 bits

        c.DataChoice = "Fixed bits";                                          // the card's pattern, at once
        Assert.Equal("10110100", c.Data);
        c.BitsText = "1021";                                                  // not bits: marked, the data stays
        Assert.True(c.HasBitsError);
        Assert.Equal("10110100", c.Data);
        c.BitsText = "1100";
        Assert.Equal("1100", c.Data);
        d.ApplyCommand.Execute(null);
        Assert.Equal("\"FSK;Rb=100kbps;Dev=45kHz;BT=0.5;Data=1100;Off=0;P=0\"",
                     s.Parameters.Single(p => p.Name == "Carrier[1]").Expression);

        await d.PreviewAsync();
        Assert.Equal("Frequency", Assert.Single(d.TimePlot.Traces).CubeName);
        Assert.StartsWith("GFSK   Rb 100 kb/s", d.Stats);

        var (vm2, s2) = Bench();                                              // an expression Fc stays as typed
        using var e = new SignalDesignerViewModel();
        e.SetTarget(vm2, s2, preview: false);
        e.PresetWmbusT2Command.Execute(null);
        Assert.Equal("RFfreq", e.FcText);
    }

    [Fact]
    public void BlePresets_AreGfskAtHalfModulationIndex()
    {
        var (vm, s) = Bench(fc: "2", fcUnit: "GHz");
        using var d = new SignalDesignerViewModel();
        d.SetTarget(vm, s, preview: false);
        d.PresetBle2MCommand.Execute(null);
        Assert.Equal("2.44", d.FcText);
        var c = Assert.Single(d.Carriers);
        Assert.Equal(("FSK", 2000.0, 500.0, true, 0.5), (c.Standard, c.BitRateKbps, c.DeviationKHz, c.IsGaussian, c.Bt));
        Assert.StartsWith("h = 0.5", c.FskInfoText);
    }

    // ── DBPSK and CSS ───────────────────────────────────────────────────────

    [Fact]
    public async Task SigfoxPreset_DbpskCardApplyAndPreview()
    {
        var (vm, s) = Bench(fc: "2", fcUnit: "GHz");
        using var d = Open(vm, s);
        d.PresetSigfoxCommand.Execute(null);
        Assert.Equal("0.86813", d.FcText);
        var c = Assert.Single(d.Carriers);
        Assert.Equal(("DBPSK", 100.0, "PN9"), (c.Standard, c.DbpskRateBps, c.Data));
        Assert.True(d.IsOwnModulated && !d.CanClip && !d.CanAddCarrier);
        Assert.Equal("Waveform", d.SecondTabHeader);
        Assert.Equal("bit 10 ms  ·  1,022 bits (the data twice, to end in phase)  ·  record 10.22 s  ·  bandwidth 200 Hz", c.DbpskInfoText);
        Assert.Equal("Native rate (3.2 kS/s)", Assert.Single(d.SampleRates, o => o.IsOwn).Label);
        d.ApplyCommand.Execute(null);
        Assert.Equal("\"DBPSK;Rb=100bps;Data=PN9;Off=0;P=0\"", s.Parameters.Single(p => p.Name == "Carrier[1]").Expression);

        await d.PreviewAsync();
        Assert.Equal("Waveform", Assert.Single(d.TimePlot.Traces).CubeName);
        Assert.Equal("Offset from Fc (Hz)", d.PsdPlot.CustomXLabel);           // a few hundred Hz reads in Hz
        Assert.Equal("CCDF", Assert.Single(d.CcdfPlot.Traces).CubeName);      // no Gaussian reference: not a noise-like signal
        Assert.StartsWith("3.2 kS/s (auto)", d.SampleRateInfo);
        d.SampleRate = SignalDesignerViewModel.SampleRateOptions[^1];          // 1966 MS/s x 10 s: refused, not attempted
        await d.PreviewAsync();
        Assert.Contains("choose Auto or Native rate", d.Status);
    }

    [Fact]
    public async Task LoRaWanPreset_CssCardApplyAndPreview()
    {
        var (vm, s) = Bench(fc: "2", fcUnit: "GHz");
        using var d = Open(vm, s);
        d.PresetLoRaWanSf7Command.Execute(null);
        Assert.Equal("0.8681", d.FcText);
        var c = Assert.Single(d.Carriers);
        Assert.Equal(("CSS", 7, 125.0), (c.Standard, c.Sf, c.CssBandwidthKHz));
        Assert.Equal("Frequency", d.SecondTabHeader);
        Assert.Equal("symbol 1.024 ms  ·  6.836 kb/s raw  ·  packet 44.25 symbols, 45.312 ms", c.CssInfoText);
        c.Sf = 9;
        d.ApplyCommand.Execute(null);
        Assert.Equal("\"CSS;SF=9;BW=125kHz;Off=0;P=0\"", s.Parameters.Single(p => p.Name == "Carrier[1]").Expression);

        await d.PreviewAsync();
        Assert.Equal("Frequency", Assert.Single(d.TimePlot.Traces).CubeName);
        Assert.StartsWith("CSS   SF 9   BW 125 kHz", d.Stats);
        Assert.True(d.CcdfPlot.Axes.Window.Width >= 12, "a constant envelope's step is framed 0-12 dB, not on its own hair");
    }

    /// <summary>Brief PCH-5: an SC card is offered, written back as it was set, clippable but alone on its source, and its
    /// EVM row reads the native record through the matched filter.</summary>
    [Fact]
    public async Task ScCard_ApplyClipAndEvm()
    {
        var (vm, s) = Bench(fc: "2", fcUnit: "GHz");
        using var d = Open(vm, s);
        d.PresetLte20Command.Execute(null);                                   // one carrier, then made SC
        var c = Assert.Single(d.Carriers);
        c.Standard = "SC";
        c.ScSymbolRateMsps = 5;
        c.ScModulation = "16APSK";
        c.RollOff = 0.2;
        c.Preamble = 32;
        c.PilotBlockText = "16/200";
        Assert.True(c.IsApsk);
        Assert.Null(c.ScRefusal);
        Assert.Equal("symbol 200 ns  ·  511 data symbols + 64 known  ·  record 115 µs  ·  bandwidth 6 MHz", c.ScInfoText);
        Assert.True(d.IsSingleCarrier && d.CanClip && !d.CanAddCarrier && !d.CanEditRecord && d.HasEvmTab);
        Assert.Equal("Native rate (40 MS/s)", Assert.Single(d.SampleRates, o => o.IsOwn).Label);
        d.ApplyCommand.Execute(null);
        Assert.Equal("\"SC;Rs=5Msps;Mod=16APSK;RollOff=0.2;Data=PN9;Preamble=32;PilotBlock=16/200;Off=0;P=0\"",
                     s.Parameters.Single(p => p.Name == "Carrier[1]").Expression);

        await d.PreviewAsync();
        var sig = SignalComposer.Generate(new SignalSpec { Carriers = [c.ToSpec()] });
        var row = Assert.Single(SignalDesignerViewModel.AnalyseEvm(sig));
        Assert.True(row.Result.RmsPercent < 1e-9);
        Assert.Equal("—", row.LimitText);

        c.PilotBlockText = "16";                                             // not L/P: refused on the card
        Assert.Contains("PilotBlock", c.ScRefusal);
    }

    // ── the signal families: one card, one preset menu row and one readout each ──

    private static SignalDesignerViewModel Designer()
    {
        var (vm, s) = Bench(fc: "2", fcUnit: "GHz");
        var d = new SignalDesignerViewModel();
        d.SetTarget(vm, s, preview: false);
        return d;
    }

    /// <summary>Runs the item of <paramref name="row"/>'s menu <paramref name="menu"/> whose line starts with <paramref name="item"/>.</summary>
    private static CarrierRowViewModel Pick(SignalDesignerViewModel d, IReadOnlyList<PresetMenu> row, string menu, string item)
    {
        row.Single(m => m.Name == menu).Items.First(i => i.Header.StartsWith(item, System.StringComparison.Ordinal)).Command.Execute(null);
        return Assert.Single(d.Carriers);
    }

    private static string Field(CarrierRowViewModel c, string key) => c.Fields.Single(f => f.Key == key).Text;

    /// <summary>The card's carrier, read back onto a new card, makes the same carrier: the netlist text is the same.</summary>
    private static string RoundTrip(CarrierRowViewModel c)
    {
        string text = SignalSpec.FormatCarrier(c.ToSpec());
        Assert.Equal(text, SignalSpec.FormatCarrier(CarrierRowViewModel.FromSpec(SignalSpec.ParseCarrier(text), 1).ToSpec()));
        return text;
    }

    private static (RfCore.Data.DataSet ds, string stats) Analysed(CarrierRowViewModel c)
        => SignalDesignerViewModel.Analyse(SignalComposer.Generate(new SignalSpec { Carriers = [c.ToSpec()] }));

    [Fact]
    public void WifiPreset_SetsTheCard_AndReadsEvmAgainstTheMcsLimit_UnderItsMask()
    {
        using var d = Designer();
        var c = Pick(d, d.ConnectivityMenus, "Wi-Fi", "802.11ax 80 MHz");
        Assert.Equal("5.21", d.FcText);
        Assert.Equal(("WLAN", "ax", "80MHz", "11", "4"), (c.Standard, Field(c, "Std"), Field(c, "BW"), Field(c, "MCS"), Field(c, "Oversample")));
        Assert.Contains("Std=ax;BW=80MHz;MCS=11;Oversample=4", RoundTrip(c));
        Assert.True(d.IsSingleCarrier && !d.CanClip && d.HasEvmTab);
        Assert.Equal("Packet power (dBm)", d.PowerLabel);
        var (ds, stats) = Analysed(c);
        Assert.Matches(@"Wi-Fi EVM \d\.\d\d %.* limit .*\(MCS11, -35 dB\)   PASS", stats);
        Assert.True(ds.Contains("Mask"));
        c.Fields.Single(f => f.Key == "MCS").Text = "12";                    // not an ax MCS: refused on the card, the carrier kept
        Assert.Contains("MCS", c.FieldsError);
        Assert.Equal(11, c.ToSpec().Mcs);
        c.Standard = "WLAN";                                                  // a new Wi-Fi card starts at 4x
        Assert.Equal("4", Field(c, "Oversample"));
    }

    [Fact]
    public void GnssPreset_SetsTheCard_AndReadsEachPrnsCodePhaseAndCn0()
    {
        using var d = Designer();
        var c = Pick(d, d.NavigationMenus, "GNSS", "GPS L1 C/A");
        Assert.Equal("1.57542", d.FcText);
        Assert.Equal(("GNSS", "L1CA", SignalDesignerViewModel.GnssPresetSatellites, "45"), (c.Standard, Field(c, "Signal"), Field(c, "Sats"), Field(c, "CN0")));
        RoundTrip(c);
        Assert.Equal("Quality", d.SecondTabHeader);
        var (ds, stats) = Analysed(c);
        Assert.Matches(@"PRN 1 +code phase 12\.\d\d chips.*C/N0 4\d\.\d dB-Hz", stats);
        Assert.Contains("PRN 5", stats);
        Assert.True(ds.Contains("Expected"));
    }

    [Fact]
    public void DsssPresets_WcdmaElevenBAndFifteenFour_ReadTheirEvm()
    {
        using var d = Designer();
        var w = Pick(d, d.CellularMenus, "WCDMA", "TM1x64");
        Assert.Equal(("2.14", "WCDMA", "Tm1x64"), (d.FcText, w.Standard, Field(w, "Model")));
        RoundTrip(w);
        Assert.True(d.CanClip);
        Assert.Matches(@"WCDMA EVM \d+\.\d+ %   limit 17\.5 %   PASS   peak code-domain error", Analysed(w).stats);

        var b = Pick(d, d.ConnectivityMenus, "802.11b", "11 Mb/s");
        Assert.Equal(("2.412", "WLAN11B", "11Mbps"), (d.FcText, b.Standard, Field(b, "Rb")));
        RoundTrip(b);
        Assert.Contains("mask margin", Analysed(b).stats);

        var z = Pick(d, d.ConnectivityMenus, "802.15.4", "O-QPSK 2.4 GHz");
        Assert.Equal(("2.405", "OQPSK154"), (d.FcText, z.Standard));
        RoundTrip(z);
        Assert.Matches(@"802\.15\.4 EVM .*limit 35\.0 %   PASS", Analysed(z).stats);
    }

    [Fact]
    public void EdgePreset_SetsTheScCardsNewFields_AndReadsItsEvm()
    {
        using var d = Designer();
        var c = Pick(d, d.RadioMenus, "GSM / EDGE", "EDGE 8PSK");
        Assert.Equal(("SC", "8PSK"), (c.Standard, c.ScModulation));
        Assert.Equal(("LinearGMSK", "67.5", "122", "8.25", "4"),
                     (Field(c, "Pulse"), Field(c, "Rotation"), Field(c, "FrameData"), Field(c, "Guard"), Field(c, "Ramp")));
        Assert.Contains("Pulse=LinearGMSK;Rotation=67.5;FrameData=122;Guard=8.25;Ramp=4", RoundTrip(c));
        Assert.Matches(@"\nEVM \d", Analysed(c).stats);
    }

    [Fact]
    public void BluetoothEdrPreset_SetsTheFskCardsPreset_AndReadsTheDeviationAndThePayloadEvm()
    {
        using var d = Designer();
        var c = Pick(d, d.ConnectivityMenus, "Bluetooth BR / EDR", "EDR 2 Mb/s");
        Assert.Equal(("2.441", "FSK", "Bluetooth EDR 2 Mb/s"), (d.FcText, c.Standard, Field(c, "Preset")));
        Assert.Contains("Preset=Bluetooth EDR 2 Mb/s", RoundTrip(c));
        string stats = Analysed(c).stats;
        Assert.Contains("FSK error", stats);
        Assert.Contains("EDR payload EVM", stats);
    }

    [Fact]
    public void AnalogPresets_SetTheCard_AndReadTheDeviationAndTheBearing()
    {
        using var d = Designer();
        var fm = Pick(d, d.RadioMenus, "FM / AM / SSB", "FM broadcast");
        Assert.Equal(("0.098", "ANALOG", "FM", "75kHz"), (d.FcText, fm.Standard, Field(fm, "Kind"), Field(fm, "Dev")));
        RoundTrip(fm);
        Assert.Matches(@"deviation 7\d(\.\d+)? kHz", Analysed(fm).stats);

        var vor = Pick(d, d.NavigationMenus, "VOR / ILS", "VOR 45");
        Assert.Equal(("VOR", "45"), (Field(vor, "Kind"), Field(vor, "Bearing")));
        RoundTrip(vor);
        Assert.Matches(@"bearing 45\.0°", Analysed(vor).stats);
    }

    [Fact]
    public void NprPreset_SetsTheTonesCard_ReadsTheNpr_AndDrawsTheGaussianReference()
    {
        using var d = Designer();
        var c = Pick(d, d.TonesMenus, "Multitone / NPR / AWGN", "100-tone NPR");
        Assert.Equal(("TONES", "100", "100kHz", "Random", "0/1MHz"), (c.Standard, Field(c, "N"), Field(c, "Spacing"), Field(c, "Phase"), Field(c, "Notch")));
        RoundTrip(c);
        Assert.True(d.CanClip && d.IsSingleCarrier);
        var (ds, stats) = Analysed(c);
        Assert.Matches(@"NPR \d+\.\d dB", stats);
        Assert.True(ds.Contains("Gaussian"));
    }

    [Fact]
    public void RadarPresets_SetThePulseCardsCode_AndTheFmcwCard_AndReadTheCompression()
    {
        using var d = Designer();
        var c = Pick(d, d.RadarMenus, "Codes", "Barker 13");
        Assert.Equal(("PCW", "Barker:13"), (c.Standard, Field(c, "Code")));
        Assert.Contains("Code=Barker:13", RoundTrip(c));
        Assert.Matches(@"compression   mainlobe .* PSL -22\.\d dB", Analysed(c).stats);    // Barker 13: 20 log 1/13

        var f = Pick(d, d.RadarMenus, "FMCW", "FMCW sawtooth");
        Assert.Equal("FMCW 250MHz 50us 12.5us", $"{f.Standard} {Field(f, "BW")} {Field(f, "Ramp")} {Field(f, "Idle")}");
        RoundTrip(f);
        Assert.Equal("Frequency", d.SecondTabHeader);
        Assert.True(Analysed(f).ds.Contains("Frequency"));
    }

    [Fact]
    public void OfdmPresets_SetTheNrOptionsAndTheNumerologyCard_AndReadTheirEvm()
    {
        using var d = Designer();
        var ul = Pick(d, d.CellularMenus, "Uplink", "NR 20 MHz uplink, DFT-s-OFDM π/2-BPSK, RB 10");
        Assert.Equal(("NR", "UL", "1", "1", "10", "24"),
                     (ul.Standard, Field(ul, "Link"), Field(ul, "DFTs"), Field(ul, "Pi2BPSK"), Field(ul, "RbStart"), Field(ul, "RbCount")));
        Assert.Contains("Link=UL;DFTs=1;Pi2BPSK=1;RbStart=10;RbCount=24", RoundTrip(ul));
        var row = Assert.Single(SignalDesignerViewModel.AnalyseEvm(SignalComposer.Generate(new SignalSpec { Carriers = [ul.ToSpec()] })));
        Assert.True(row.Result.RmsPercent < 0.2);

        var dvb = Pick(d, d.BroadcastMenus, "DVB-T / ISDB-T / DAB", "DVB-T 2K");
        Assert.Equal(("OFDM", "DVB-T 2K"), (dvb.Standard, Field(dvb, "Preset")));
        RoundTrip(dvb);
        Assert.Matches(@"\nEVM 0\.\d\d %", Analysed(dvb).stats);

        var nb = Pick(d, d.CellularMenus, "NB-IoT / LTE-M", "NB-IoT uplink, single tone at 3.75 kHz");
        Assert.Equal(("NB-IoT UL", "3.75kHz", "1"), (Field(nb, "Preset"), Field(nb, "SCS"), Field(nb, "NbTones")));
        RoundTrip(nb);
    }

    // ── Tools ▸ Signal Designer ─────────────────────────────────────────────

    /// <summary>Opened from Tools it belongs to no MOD_Source: nothing to Apply to, and a signal it designs or reads
    /// leaves through Export signal. Both hand-mirrored Tools menus open it.</summary>
    [Fact]
    public async Task Standalone_DesignsReadsAndExports_WithNothingToApplyTo()
    {
        using var d = new SignalDesignerViewModel();
        d.SetStandalone(preview: false);
        Assert.False(d.IsAttached);
        Assert.Equal("Signal Designer", d.Title);
        Assert.Equal("LTE", Assert.Single(d.Carriers).Standard);
        d.ApplyCommand.Execute(null);                                         // no source: a no-op, never a throw

        string dir = System.IO.Directory.CreateTempSubdirectory("crf-sd-standalone-").FullName;
        try
        {
            d.PresetWmbusT1Command.Execute(null);
            string iq = System.IO.Path.Combine(dir, "t1.txt");
            Assert.True(await d.ExportSignalAsync(iq));
            d.LoadCustomFile(iq);                                             // read back, by its full path
            Assert.True(d.IsCustom);
            Assert.Equal(iq, d.Carriers[0].FilePath);
            await d.PreviewAsync();
            Assert.Equal("Ready", d.Status[..5]);
            Assert.NotEmpty(d.PsdPlot.Traces);
        }
        finally { System.IO.Directory.Delete(dir, recursive: true); }

        var dirInfo = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (!System.IO.File.Exists(System.IO.Path.Combine(dirInfo.FullName, "circuitRF.slnx"))) dirInfo = dirInfo.Parent!;
        string shell = System.IO.File.ReadAllText(System.IO.Path.Combine(dirInfo.FullName, "src", "Ui", "Views", "WorkspaceWindow.axaml"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(shell, "\\{Binding NewSignalDesignerCommand\\}").Count);
    }
}

// ================================================================
//  KnobButtonReadoutTests.cs — brief-ddl-6: the knob, the illuminated
//  button and the readout; display values in expressions; a trace shown
//  by a control; a two-valued pin. One test per gate, plus the round
//  trip. The S/M/L look and the glow in both themes are looked at in
//  UiSnapshot and `render`.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore.Data;
using RfCore.Export;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

public sealed class KnobButtonReadoutTests : IDisposable
{
    private const int Grid = 12, Steps = 6, Freqs = 5;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"crf_ddl6_{Guid.NewGuid():N}");
    public KnobButtonReadoutTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static readonly Guid M1 = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private static double Pout(int g, int p) => 100 * g + p;
    private static double S21(int f) => 0.5 + 0.1 * f;
    private static double S11(int f) => 0.05 + 0.01 * f;

    /// <summary><c>Pout[gridPoint, pinStep]</c>, <c>Corner[corner, pinStep]</c> over two corners, and <c>SP1.S</c>.</summary>
    private string Run()
    {
        var grid   = new Axis("gridPoint", [.. Enumerable.Range(0, Grid).Select(i => (double)i)]);
        var pin    = new Axis("pinStep", [.. Enumerable.Range(0, Steps).Select(i => -10.0 + 2 * i)], "dBm");
        var corner = new Axis("corner", [0.0, 1.0]);
        var freq   = new Axis("freq", [.. Enumerable.Range(0, Freqs).Select(i => 1e9 + i * 1e8)], "Hz");
        var port   = new Axis("i", [1.0, 2.0]);
        var port2  = new Axis("j", [1.0, 2.0]);
        var ds = new DataSet();
        ds.Add("Pout", new DataCube([grid, pin], [.. Enumerable.Range(0, Grid * Steps).Select(k => Pout(k / Steps, k % Steps))]));
        ds.Add("Corner", new DataCube([corner, pin], [.. Enumerable.Range(0, 2 * Steps).Select(k => 1000.0 * (k / Steps) + k % Steps)]));
        var s = new Complex[Freqs * 4];
        for (int f = 0; f < Freqs; f++)
        {
            s[f * 4 + 0] = S11(f); s[f * 4 + 1] = 0.01;      // S11, S12
            s[f * 4 + 2] = S21(f); s[f * 4 + 3] = S11(f);    // S21, S22
        }
        ds.AddToGroup("SP1", "S", new DataCube([freq, port, port2], s));
        string path = Path.Combine(_dir, "run.npy");
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        return path;
    }

    private string Cdd(string name, DisplayControl[] controls, params TraceConfig[] traces)
    {
        var config = new DataDisplayConfig
        {
            FormatVersion = DataDisplayConfig.CurrentFormatVersion,
            Tabs = [new TabConfig
            {
                Name = "Tab 1",
                Plots = [new PlotContainerConfig { PlotType = PlotType.Rect, Left = 300, Top = 0, Width = 400, Height = 260, Traces = [.. traces] }],
                Controls = [.. controls],
            }],
        };
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, JsonSerializer.Serialize(config, DataDisplayJson.Options));
        return path;
    }

    private static T Sized<T>(T c, string name) where T : DisplayControl
    {
        c.Name = name; c.Left = 10; c.Top = 10;
        c.ApplySize(ControlSize.Medium);
        return c;
    }

    private static async Task<DisplayWindowViewModel> OpenAsync(string cdd)
    {
        var w = new DisplayWindowViewModel();
        await w.LoadAllAsync(cdd);
        return w;
    }

    private static Trace TraceOf(DisplayWindowViewModel w, int i = 0) => w.Tabs[0].DataDisplay.Plots[0].PlotVM.Plot.Traces[i];
    private static double[] Ys(Trace t) => [.. t.Points.Select(p => (double)p.Y)];
    private static ControlContainerViewModel ControlOf(DisplayWindowViewModel w, string name)
        => w.Tabs[0].DataDisplay.Controls.Single(c => c.Model.Name == name);

    [Fact]
    public async Task Gate1_Knob_TurnsAtItsRate_FineWithCtrl_AndTheWheelAndATickSetABoundKnob()
    {
        string npy = Run();
        var range = Sized(new KnobControl { Start = 0, Stop = 100, Points = 201, Value = 0 }, "k1");
        var bound = Sized(new KnobControl { Domain = SliderDomain.Bound, SourcePath = npy, CubeName = "Pout", AxisName = "gridPoint" }, "k2");
        bound.Top = 200;
        var w = await OpenAsync(Cdd("knob.cdd", [range, bound]));

        // A vertical drag of 100 px up turns a range knob half its span; with Ctrl, a tenth of that rate.
        var k1 = ControlOf(w, "k1");
        var knob = (KnobControl)k1.Model;
        var l = ControlRenderer.KnobLayout(knob, (float)k1.ViewWidth, (float)k1.ViewHeight);
        k1.BeginValueDrag(l.Center);
        k1.TurnKnob(l.Center.Y - 100, fine: false);
        k1.EndValueDrag();
        Assert.Equal(50, knob.CurrentValue, 9);
        k1.BeginValueDrag(l.Center);
        k1.TurnKnob(l.Center.Y - 100, fine: true);
        k1.EndValueDrag();
        Assert.Equal(55, knob.CurrentValue, 9);   // a twentieth of the span more
        // Down turns it back, and it stops at its ends.
        k1.BeginValueDrag(l.Center);
        k1.TurnKnob(l.Center.Y + 1000, fine: false);
        k1.EndValueDrag();
        Assert.Equal(0, knob.CurrentValue, 9);

        // The wheel steps a bound knob one stop (the view calls Step per notch), and a press on a tick sets that stop.
        var k2 = ControlOf(w, "k2");
        var b = (KnobControl)k2.Model;
        Assert.Equal(Grid, b.Stops.Count);
        Assert.True(k2.Step(+1));
        Assert.Equal(1, b.StopIndex);
        var bl = ControlRenderer.KnobLayout(b, (float)k2.ViewWidth, (float)k2.ViewHeight);
        double a = ControlRenderer.KnobAngle(7.0 / (Grid - 1)) * Math.PI / 180;
        float rr = bl.Radius + bl.TickRoom * 0.5f;
        var tick = new SKPoint(bl.Center.X + rr * (float)Math.Cos(a), bl.Center.Y + rr * (float)Math.Sin(a));
        k2.BeginValueDrag(tick);
        k2.EndValueDrag();
        Assert.Equal(7, b.StopIndex);
    }

    [Fact]
    public void Gate2_ALitButton_ExportsItsGlowAsVectorShapes_NeverAnImageOrAnEmptyGradient()
    {
        // Skia's SVG writer has no radial gradient: a radial shader comes out as an empty <defs/> and an unfilled circle,
        // which a viewer paints black. The glow is graded vector discs instead — every one with its own fill.
        var button = Sized(new ButtonControl { On = true, Accent = "#34c759", Label = "RF on" }, "button1");
        using var stream = new MemoryStream();
        using (var svg = SKSvgCanvas.Create(SKRect.Create(0, 0, 200, 200), stream))
            ControlRenderer.Draw(svg, button, RenderTheme.Light, SKRect.Create(20, 20, (float)button.Width, (float)button.Height));
        string text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.DoesNotContain("<image", text);
        Assert.DoesNotContain("filter", text);
        var circles = System.Text.RegularExpressions.Regex.Matches(text, "<(ellipse|circle)[^>]*>").Select(m => m.Value).ToList();
        Assert.True(circles.Count > 40, $"{circles.Count} circles");
        Assert.All(circles, c => Assert.Contains("fill", c));   // none left for the viewer to paint black
        Assert.Contains("#34C759", text, StringComparison.OrdinalIgnoreCase);   // the glow is in the button's colour
    }

    [Fact]
    public async Task Gate3_Momentary_IsOneWhileHeld_AndALinkedTwoCaseAxisFollowsBoth()
    {
        string npy = Run();
        var button = Sized(new ButtonControl { Mode = ButtonMode.Momentary }, "button1");
        var trace = new TraceConfig
        {
            SourcePath = npy, CubeName = "Corner", Expression = "Corner[@button1, :]",
            CubeSlice = [new AxisSliceConfig { AxisName = "corner", Role = AxisRole.PinToIndex, Index = 0, Link = button.Id, LinkName = "button1" },
                         new AxisSliceConfig { AxisName = "pinStep", Role = AxisRole.KeepAsX }],
        };
        var w = await OpenAsync(Cdd("momentary.cdd", [button], trace));
        w.ResolveLinks();
        var vm = ControlOf(w, "button1");
        var b = (ButtonControl)vm.Model;
        var undoTop = w.Tabs[0].DataDisplay.UndoRedo.Top;

        Assert.Equal([0, 1, 2, 3, 4, 5], Ys(TraceOf(w)));
        vm.PressButton();
        Assert.True(b.On);
        Assert.Equal(1, ControlLinkSource.Resolve(b, null).Scalar!.Value);
        Assert.Equal(1, TraceOf(w).Slice![0].Index);
        Assert.Equal([1000, 1001, 1002, 1003, 1004, 1005], Ys(TraceOf(w)));
        vm.ReleaseButton();
        Assert.False(b.On);
        Assert.Equal([0, 1, 2, 3, 4, 5], Ys(TraceOf(w)));
        // A momentary press is a gesture, not a setting: nothing to undo, nothing to save.
        Assert.Same(undoTop, w.Tabs[0].DataDisplay.UndoRedo.Top);
    }

    [Fact]
    public async Task Gate4_ExpressionsReadDisplayValues_AndAScalarOperandStillClosesACycle()
    {
        string npy = Run();
        var button = Sized(new ButtonControl(), "button1");
        var knob = Sized(new KnobControl { Start = 0, Stop = 10, Step = 1, Value = 3 }, "k1");
        var switched = new TraceConfig { SourcePath = npy, Expression = "if(@button1, dB(SP1.S(2,1)), dB(SP1.S(1,1)))" };
        var offset   = new TraceConfig { SourcePath = npy, Expression = "Pout[2, :] - @k1" };
        var w = await OpenAsync(Cdd("expr.cdd", [button, knob], switched, offset));
        w.ResolveLinks();

        double[] Db(Func<int, double> mag) => [.. Enumerable.Range(0, Freqs).Select(f => 20 * Math.Log10(mag(f)))];
        Assert.Null(TraceOf(w).ExpressionError);
        static void Near(double[] want, double[] got)
        {
            Assert.Equal(want.Length, got.Length);
            for (int i = 0; i < want.Length; i++) Assert.Equal(want[i], got[i], 4);
        }
        Near(Db(S11), Ys(TraceOf(w)));           // dark: S11
        ControlOf(w, "button1").PressButton();
        Near(Db(S21), Ys(TraceOf(w)));           // lit: S21
        Assert.Equal([.. Enumerable.Range(0, Steps).Select(p => Pout(2, p) - 3)], Ys(TraceOf(w, 1)));

        // A scalar operand is counted by the link graph: a trace reading @m1, where m1 sits on that trace, is a cycle.
        var t = TraceOf(w, 1);
        t.Markers.Add(new Marker(t, 0, isMulti: false, isDelta: false, index: 1) { Id = M1 });
        t.Expression = "Pout[2, :] - @m1";
        var doc = w.BuildLinkDocument();
        Assert.False(LinkCommit.TryCommit(doc, t, out var error));
        Assert.Contains("cycle", error);
    }

    [Fact]
    public async Task Gate5_AReadoutOfAMarker_FollowsItsArrowSteps_InItsInfoBoxesUnit()
    {
        string npy = Run();
        var trace = new TraceConfig
        {
            SourcePath = npy, CubeName = "Pout", Expression = "Pout[3, :]",
            CubeSlice = [new AxisSliceConfig { AxisName = "gridPoint", Role = AxisRole.PinToIndex, Index = 3 },
                         new AxisSliceConfig { AxisName = "pinStep", Role = AxisRole.KeepAsX }],
            Markers = [new MarkerConfig { Id = M1.ToString("D"), Name = "m1", PositionStaticX = -6, PositionStaticY = 0 }],
        };
        var readout = Sized(new ReadoutControl { Source = M1.ToString("D"), SourceName = "m1" }, "readout1");
        var w = await OpenAsync(Cdd("readout.cdd", [readout], trace));
        w.ResolveLinks();
        var r = (ReadoutControl)ControlOf(w, "readout1").Model;
        var plot = w.Tabs[0].DataDisplay.Plots[0];
        var t = TraceOf(w);
        string BoxLine() => t.BuildMarkerBoxLines(t.Markers[0], plot.PlotVM.Plot.FreqUnits, false, plot.PlotVM.Plot.Traces)[1].Text;

        string before = r.Text!;
        Assert.Equal(BoxLine(), before);
        Assert.Contains("dBm", before);

        var box = w.Tabs[0].DataDisplay.MarkerInfoBoxes.Single();
        box.IsSelected = true;
        Assert.True(plot.StepSelectedMarkers(+1));
        Assert.NotEqual(before, r.Text);
        Assert.Equal(BoxLine(), r.Text);
        Assert.Equal("m1", r.NameText);
    }

    [Fact]
    public async Task ShownByAButton_HidesAndShowsATrace_TheLegendSaysSo_AndEveryFieldRoundTrips()
    {
        string npy = Run();
        var button = Sized(new ButtonControl { LabelPlacement = ButtonLabelPlacement.OnFace, Accent = "#ff9500" }, "button1");
        var knob = Sized(new KnobControl { Start = 1, Stop = 9, Points = 5, Value = 7, ShowEnds = false }, "k1");
        var readout = Sized(new ReadoutControl { Source = knob.Id, SourceName = "k1", ShowName = false }, "readout1");
        var shown = new TraceConfig { SourcePath = npy, Expression = "Pout[1, :]", ShownBy = button.Id };
        var inverse = new TraceConfig { SourcePath = npy, Expression = "Pout[2, :]", ShownBy = button.Id, ShownByInverted = true };
        var w = await OpenAsync(Cdd("shown.cdd", [button, knob, readout], shown, inverse));
        w.ResolveLinks();

        Assert.True(TraceOf(w, 0).HiddenByControl);
        Assert.False(TraceOf(w, 1).HiddenByControl);
        Assert.NotEmpty(TraceOf(w, 0).Points);   // hidden, still resolved
        var entries = LegendRenderer.Entries(w.Tabs[0].DataDisplay.Plots[0].PlotVM.Plot, false, null);
        Assert.EndsWith("(hidden)", entries[0].Label);
        Assert.DoesNotContain("(hidden)", entries[1].Label);

        ControlOf(w, "button1").PressButton();   // a toggle
        Assert.False(TraceOf(w, 0).HiddenByControl);
        Assert.True(TraceOf(w, 1).HiddenByControl);
        Assert.Equal("7", ((ReadoutControl)ControlOf(w, "readout1").Model).Text);

        string saved = Path.Combine(_dir, "shown-saved.cdd");
        await w.SaveAllAsync(saved);
        var w2 = await OpenAsync(saved);
        string Json(DisplayControl c) => JsonSerializer.Serialize(c, DataDisplayJson.Options);
        foreach (var name in new[] { "button1", "k1", "readout1" })
            Assert.Equal(Json(ControlOf(w, name).Model), Json(ControlOf(w2, name).Model));
        Assert.True(((ButtonControl)ControlOf(w2, "button1").Model).On);
        Assert.Equal(button.Id, TraceOf(w2, 1).ShownBy);
        Assert.True(TraceOf(w2, 1).ShownByInverted);
        w2.ResolveLinks();
        Assert.True(TraceOf(w2, 1).HiddenByControl);
    }
}

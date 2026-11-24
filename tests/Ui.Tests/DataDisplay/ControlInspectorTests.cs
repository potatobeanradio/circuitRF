// ================================================================
//  ControlInspectorTests.cs — brief-ddl-5 R-ddl5-7: the control inspector.
//  An edit is one undo step and undo shows in the inspector; S/M/L set
//  the frame; the Bound pickers fill the slider's stops; a name already
//  in use is refused.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

public sealed class ControlInspectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"crf_ddl5_insp_{Guid.NewGuid():N}");
    public ControlInspectorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static readonly double[] PinDbm = [-10, -5, 0, 5, 10, 15];

    /// <summary>A run with an analysis cube <c>HB1.Pout[Pin, freq]</c> and a bookkeeping cube, and a display whose one
    /// plot reads it, so the source is loaded when the display opens.</summary>
    private async Task<DisplayWindowViewModel> OpenAsync()
    {
        var pin  = new Axis("Pin", PinDbm, "dBm");
        var freq = new Axis("freq", [1e9, 2e9, 3e9], "Hz");
        var ds = new DataSet();
        ds.AddToGroup("HB1", "Pout", new DataCube([pin, freq], [.. Enumerable.Range(0, 18).Select(i => (double)i)]) { Unit = "dBm" });
        ds.AddToGroup("HB1", "__LabeledNodes", new DataCube([new Axis("k", [0, 1])], [0.0, 1.0]));
        string npy = Path.Combine(_dir, "run.npy");
        DataSetExporter.Export(ds, npy, ExportFormat.Npy);

        var trace = new TraceConfig
        {
            SourcePath = npy, CubeName = "HB1.Pout", Expression = "HB1.Pout[0, :]",
            CubeSlice = [new() { AxisName = "Pin", Role = AxisRole.PinToIndex, Index = 0 },
                         new() { AxisName = "freq", Role = AxisRole.KeepAsX }],
        };
        var config = new DataDisplayConfig
        {
            FormatVersion = DataDisplayConfig.CurrentFormatVersion,
            Tabs = [new TabConfig { Name = "Tab 1", Plots = [new PlotContainerConfig
                { PlotType = PlotType.Rect, Width = 300, Height = 200, Traces = [trace] }] }],
        };
        string cdd = Path.Combine(_dir, "d.cdd");
        File.WriteAllText(cdd, JsonSerializer.Serialize(config, DataDisplayJson.Options));
        var w = new DisplayWindowViewModel();
        await w.LoadAllAsync(cdd);
        return w;
    }

    [Fact]
    public async Task AnEditIsOneUndoStep_AndUndoShowsInTheInspector()
    {
        var w = await OpenAsync();
        var dd = w.DataDisplay!;
        var vm = dd.AddSlider();
        var inspector = dd.ControlInspector;
        Assert.True(inspector.IsActive);
        Assert.Same(vm, inspector.Target);

        var before = dd.UndoRedo.Top;
        inspector.StartText = "2.5";
        var step = dd.UndoRedo.Top;
        Assert.NotSame(before, step);
        Assert.Equal(2.5, ((SliderControl)vm.Model).Start);

        // The same value again records nothing; text that is not a number changes nothing and shows the old value.
        inspector.StartText = "2.5";
        inspector.StartText = "abc";
        Assert.Same(step, dd.UndoRedo.Top);
        Assert.Equal("2.5", inspector.StartText);
        Assert.Equal("Start must be a number.", inspector.Problem);

        // A point count governs the step: the step box is greyed and shows the step the count gives.
        inspector.PointsText = "6";
        Assert.False(inspector.StepEnabled);
        Assert.Equal("1.5", inspector.StepText);
        Assert.Null(inspector.Problem);
        dd.UndoRedo.Undo();
        Assert.True(inspector.StepEnabled);

        dd.UndoRedo.Undo();
        Assert.Equal(0, ((SliderControl)vm.Model).Start);
        Assert.Equal("0", inspector.StartText);
        Assert.Same(before, dd.UndoRedo.Top);
    }

    [Fact]
    public async Task SizePresetsSetTheFrame_AndCustomIsNoPreset()
    {
        var w = await OpenAsync();
        var dd = w.DataDisplay!;
        var vm = dd.AddSlider();
        var inspector = dd.ControlInspector;

        inspector.SetLargeCommand.Execute(null);
        Assert.True(inspector.IsLarge);
        Assert.Equal(vm.Model.PresetSize(ControlSize.Large), (vm.Model.Width, vm.Model.Height));

        inspector.SetSmallCommand.Execute(null);
        Assert.Equal(vm.Model.PresetSize(ControlSize.Small), (vm.Model.Width, vm.Model.Height));

        // A vertical slider takes the preset's turned frame.
        inspector.SetVerticalCommand.Execute(null);
        Assert.Equal(vm.Model.PresetSize(ControlSize.Small), (vm.Model.Width, vm.Model.Height));
        Assert.True(vm.Model.Height > vm.Model.Width);

        vm.BeginResize();
        vm.ResizeTo(300, 400);
        vm.EndResize();
        Assert.True(inspector.IsCustom);
        Assert.False(inspector.IsSmall || inspector.IsMedium || inspector.IsLarge);

        inspector.SetMediumCommand.Execute(null);
        Assert.Equal(vm.Model.PresetSize(ControlSize.Medium), (vm.Model.Width, vm.Model.Height));
    }

    [Fact]
    public async Task BoundPickersFillTheStops()
    {
        var w = await OpenAsync();
        var dd = w.DataDisplay!;
        var vm = dd.AddSlider();
        var s = (SliderControl)vm.Model;
        var inspector = dd.ControlInspector;

        inspector.SetBoundCommand.Execute(null);
        Assert.True(inspector.IsBound);
        Assert.NotNull(s.SourcePath);

        // Only what a trace would offer, and only axes a link can follow.
        Assert.Equal(["HB1.Pout"], inspector.QuantityItems.Select(q => q.Value));
        Assert.Equal(["Pin", "freq"], inspector.AxisItems.Select(a => a.Value));

        inspector.SelectedAxis = inspector.AxisItems.Single(a => a.Value == "freq");
        Assert.Equal([1e9, 2e9, 3e9], s.BoundValues);
        Assert.Equal("Hz", s.BoundUnit);

        s.Index = 2;
        inspector.SelectedAxis = inspector.AxisItems.Single(a => a.Value == "Pin");
        Assert.Equal("HB1.Pout", s.CubeName);
        Assert.Equal("Pin", s.AxisName);
        Assert.Equal(0, s.Index);
        Assert.Equal(PinDbm, s.BoundValues);
        Assert.Equal("dBm", s.BoundUnit);
        Assert.Null(inspector.Problem);
        Assert.Equal("Pin", inspector.SelectedAxis!.Value);
    }

    [Fact]
    public async Task SwitchingDomainKeepsTheValue_InEitherUnit()
    {
        var w = await OpenAsync();
        var dd = w.DataDisplay!;
        var vm = dd.AddSlider();
        var s = (SliderControl)vm.Model;
        var inspector = dd.ControlInspector;

        vm.Edit(c => { var r = (SliderControl)c; r.Unit = "MHz"; r.Start = 1000; r.Stop = 3000; r.Step = 500; r.Value = 2500; }, "range");
        inspector.SetBoundCommand.Execute(null);
        inspector.SelectedAxis = inspector.AxisItems.Single(a => a.Value == "freq");
        vm.Edit(c => ((SliderControl)c).Index = 1, "value");

        inspector.SetRangeCommand.Execute(null);
        Assert.Equal(2000, s.CurrentValue);

        vm.Edit(c => ((SliderControl)c).Value = 3000, "value");
        inspector.SetBoundCommand.Execute(null);
        Assert.Equal(2, s.Index);
        Assert.Equal(3e9, s.CurrentValue);
    }

    [Fact]
    public async Task ANameInUseIsRefused_AndTheOldNameKept()
    {
        var w = await OpenAsync();
        var dd = w.DataDisplay!;
        dd.AddSlider();
        var second = dd.AddSlider();
        var inspector = dd.ControlInspector;
        Assert.Same(second, inspector.Target);

        inspector.NameText = "slider1";
        Assert.Equal("slider2", second.Model.Name);
        Assert.NotNull(inspector.NameError);
        Assert.Equal(inspector.NameError, inspector.Problem);

        inspector.NameText = "drive";
        Assert.Equal("drive", second.Model.Name);
        Assert.Null(inspector.NameError);
    }
}

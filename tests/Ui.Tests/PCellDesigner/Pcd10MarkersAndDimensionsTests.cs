// Owner: PCD-10 (brief-pcd-10 §3). On PadTee from examples/PCell Designer/, through the view model.

using System.Runtime.CompilerServices;
using CircuitRF.Design.PCellDesigner;
using CircuitRF.Ui.PCellDesigner;
using CircuitRF.Ui.Tests.Layout.PCells;

namespace CircuitRF.Ui.Tests.PCellDesigner;

[Collection(PCellResolverCollection.Name)]
public sealed class Pcd10MarkersAndDimensionsTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-pcd10-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    private static string Examples([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "examples", "PCell Designer"));

    /// <summary>PadTee in a scratch copy of the example workspace; <paramref name="edit"/> changes its schematic first.</summary>
    private PCellDesignerViewModel PadTee(Action<SchematicEditModel>? edit = null)
    {
        static void Copy(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
            foreach (var d in Directory.GetDirectories(from)) Copy(d, Path.Combine(to, Path.GetFileName(d)));
        }
        Directory.CreateDirectory(_scratch);
        File.Copy(Path.Combine(Examples(), ".cws"), Path.Combine(_scratch, ".cws"));
        Copy(Path.Combine(Examples(), "tech"), Path.Combine(_scratch, "tech"));
        Copy(Path.Combine(Examples(), "PadTee"), Path.Combine(_scratch, "PadTee"));
        string csch = Path.Combine(_scratch, "PadTee", "schematic", "PadTee.csch");
        if (edit is not null)
        {
            var model = SchematicPersistence.LoadFromFile(csch).model;
            edit(model);
            SchematicPersistence.SaveToFile(csch, model, "PadTee");
        }
        return new PCellDesignerViewModel(Path.Combine(_scratch, "PadTee"), _scratch);
    }

    /// <summary>R-pcd10-1: a marker on S21 is on the rebuilt S21 after an edit re-runs the response, at the same x.</summary>
    [Fact]
    public async Task Markers_SurviveARerun()
    {
        var vm = PadTee();
        await vm.DesignAsync(0);
        var s21 = vm.Plot.Traces.Single(t => t.Label == "S21");
        var marker = new Marker(s21, 10e9, isMulti: false, isDelta: false, index: 1);
        s21.Markers.Add(marker);

        vm.Parameters.Single(p => p.Name == "Lpad").Value = "400 um";
        await vm.DesignAsync(0);
        var rebuilt = vm.Plot.Traces.Single(t => t.Label == "S21");
        Assert.NotSame(s21, rebuilt);
        Assert.Same(marker, Assert.Single(rebuilt.Markers));
        Assert.Equal(10e9, marker.Freq);
    }

    /// <summary>R-pcd10-2: a stretch binding (Lpad), an offset (Dx = Lstub/2) and a primitive parameter (TL1.L = L1) come
    /// out as dimensions of their spans, labelled with their values; a value change moves the line and its label.</summary>
    [Fact]
    public async Task Dimensions_ForAStretchAnOffsetAndAPrimitive_FollowTheValues()
    {
        var vm = PadTee(m =>
        {
            var line = new EditableComponent
            {
                Symbol = SymbolKind.Mlin, InstanceName = "TL9",
                Offset = new OffsetPlacement { Anchor = "TL3.2", Pin = 1, Dx = "Lstub/2" },
            };
            foreach (var d in ComponentTypeRegistry.DefaultParameters(SymbolKind.Mlin, 2))
                line.Parameters.Add(new EditableParameter { Name = d.Name, Expression = d.Expression, Unit = d.Unit, Dimension = d.Dimension });
            m.Components.Add(line);
        });
        await vm.DesignAsync(0);
        long um = vm.Layout!.DbuPerMicron;
        static double Span(PCellDimensionLine d) => Math.Sqrt(Math.Pow(d.ToX - d.FromX, 2) + Math.Pow(d.ToY - d.FromY, 2));
        PCellDimensionLine Of(string subject) =>
            vm.Dimensions.FirstOrDefault(d => d.Subject == subject)
            ?? throw new Xunit.Sdk.XunitException($"no {subject}: {string.Join("; ", vm.Dimensions.Select(d => d.Label))} | {vm.LayoutNote}");

        Assert.Equal("Lpad = 300 µm", Of("Lpad").Label);
        Assert.Equal(300 * um, Span(Of("Lpad")), 0);
        Assert.Equal("L1 = 3 mm", Of("L1").Label);
        Assert.Equal(3000 * um, Span(Of("L1")), 0);
        Assert.Equal("Lstub/2 = 2 mm", Of("Lstub/2").Label);
        Assert.Equal(2000 * um, Span(Of("Lstub/2")), 0);

        vm.Parameters.Single(p => p.Name == "Lpad").Value = "400 um";
        await vm.DesignAsync(0);
        Assert.Equal("Lpad = 400 µm", Of("Lpad").Label);
        Assert.Equal(400 * um, Span(Of("Lpad")), 0);
    }
}

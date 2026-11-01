// brief-dfr33-1 R-dfr33-1-3 / R-dfr33-1-4 — a layout whose EM ports are numbered 1, 3, 4. Every sNp writer numbers
// its ports by position (src/Design/RESOLVED.md, DFR33-1), so the file's port 2 would be P3; the owner's decision
// (D2) is that the run refuses until the numbers are contiguous. `check` reports Preflight's refusal and a run
// reports Run's, so one test each.

using CircuitRF.Core.Design;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

public sealed class PortNumberGapTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static long Mm(double mm) => (long)Math.Round(mm * 1e3 * Dbu);
    private static readonly LayerKey Top = new(1, 0);

    [Fact]
    public void TheSentence_NamesTheMissingNumber_TheRanges_AndWhichPortMoves()
    {
        Assert.Null(EmPortExtraction.NumberGap([3, 1, 2]));
        Assert.Equal(
            "Port numbers skip 2: the 13 ports are 1, 3–14, and a Touchstone file numbers its ports by position, " +
            "so its port 2 would be port 3. Renumber the ports 1–13 so the file and the layout agree.",
            EmPortExtraction.NumberGap([1, .. Enumerable.Range(3, 12)]));
    }

    /// <summary>Two lines; P1 and P3 on the ends of one, P4 on the other. No P2.</summary>
    private static LayoutView Gapped()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape { Layer = Top, X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(2.9) });
        view.Shapes.Add(new RectShape { Layer = Top, X1 = 0, Y1 = Mm(10), X2 = Mm(20), Y2 = Mm(12.9) });
        LabelShape Port(double x, double y, string text, LayoutRotation d) => new()
        {
            Layer = Top, PortLayer = Top, X = Mm(x), Y = Mm(y), Text = text, Height = Mm(1), IsPort = true, PortDirection = d,
        };
        view.Shapes.Add(Port(0, 1.45, "P1", LayoutRotation.R0));
        view.Shapes.Add(Port(20, 1.45, "P3", LayoutRotation.R180));
        view.Shapes.Add(Port(0, 11.45, "P4", LayoutRotation.R0));
        return view;
    }

    [Fact]
    public void CheckAndARun_RefuseTheGap_AndTheRunWritesNothing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-port-gap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var setup = new EmSetup
            {
                Name = "gap", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
                Frequency = new FrequencySpec("2", "2", 1, SweepKind.Linear, "GHz", "GHz"),
            };
            var source = new EmLayoutSource(Path.Combine(dir, "a.clay"), Gapped(), StarterTechnologies.Pcb2Layer(), Dbu);

            var pre = EmRunService.Preflight(setup, source);
            Assert.Contains("Port numbers skip 2", pre.Refusal);

            string results = Path.Combine(dir, "results");
            var run = EmRunService.Run(setup, source, results);
            Assert.NotEqual(EmRunStatus.Ok, run.Status);
            Assert.Contains("its port 2 would be port 3", run.Error);
            Assert.False(Directory.Exists(results) && Directory.EnumerateFiles(results, "*", SearchOption.AllDirectories).Any());
        }
        finally { Directory.Delete(dir, true); }
    }
}

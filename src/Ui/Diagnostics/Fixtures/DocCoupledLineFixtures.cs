using System;
using System.IO;
using CircuitRF.Core.Design;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Views.DataDisplay;
using CircuitRF.Engine.Calibration;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// AN-01's RESULT figures — the s-parameters of the coupled pair the note is about, plotted in the
/// Data Display a reader would plot them in.
///
/// <para><b>The design is not built here.</b> It is <c>testdata/portcal/coupled-pair</c>, the
/// committed fixture the port-calibration series was measured on and that
/// <c>ModalErrorBoxTests</c> runs end to end — resolved through the same
/// <see cref="EmSetupResolver"/> the Simulate button and the <c>em</c> verb use, so the curves on the
/// page are the curves that file produces. <see cref="DocAntennaFixtures"/> states the rule and the
/// walk up to <c>circuitRF.slnx</c>; this file follows it.</para>
///
/// <para><b>Both figures are <c>Static</c> in <see cref="FigureCatalog"/>.</b> A full-wave point is
/// not free — the sweep below is 61 points and about a minute built Release, several times that in
/// the Debug build a plain <c>dotnet run</c> produces — and the answer does not change between
/// regenerations. So an ordinary docs run leaves the committed pictures alone and
/// <c>--rebuild-static</c> redraws them, which is the same arrangement the antenna patterns
/// use.</para>
/// </summary>
public static class DocCoupledLineFixtures
{
    /// <summary>
    /// The plotted sweep: 1–7 GHz, the band the fixture's own <c>.cem</c> declares, at 200 MHz
    /// instead of the fixture's 7 points. <b>Only the point count is changed</b> — the mesh, the
    /// kernel, the ports and the band are the committed file's, so a reader who runs that file gets
    /// these curves at the frequencies it asks for.
    /// </summary>
    private const int SweepPoints = 31;

    /// <summary>
    /// The coupled pair's s-parameters, as the data-source library's own logical id.
    ///
    /// <para>Run once per process, into the documentation's own results directory — never into
    /// <c>testdata/</c>, so a docs regeneration cannot write inside a committed fixture.</para>
    /// </summary>
    public static string Result() => Run.Value;

    private static readonly Lazy<string> Run = new(() =>
    {
        string cem = Path.Combine(
            DocAntennaFixtures.RepoRootPath, "testdata", "portcal", "coupled-pair", "em",
            "coupled-pair.cem");

        if (!File.Exists(cem))
            throw new InvalidOperationException(
                $"AN-01's result figures are drawn from '{cem}', and it is not there. The note's "
              + "numbers are that fixture's; do not point this at another one.");

        var setup    = EmSetupPersistence.LoadFromFile(cem);
        var resolved = EmSetupResolver.Resolve(
            cem, setup.LayoutRef,
            Path.Combine(DocAntennaFixtures.RepoRootPath, "testdata", "portcal", ".cws"),
            new TechnologyCache());

        if (resolved.Source is null || resolved.Source.Technology is null)
            throw new InvalidOperationException(
                "AN-01's coupled pair did not resolve its layout or technology: "
              + string.Join(" ", resolved.Diagnostics));

        var swept = setup.Clone();
        swept.Frequency = new FrequencySpec(
            "1", "7", SweepPoints, SweepKind.Linear, "GHz", "GHz");

        Directory.CreateDirectory(DocRunData.ResultsRoot);
        var result = EmRunService.Run(swept, resolved.Source, DocRunData.ResultsRoot);

        if (result.Status != EmRunStatus.Ok || result.NpyPath is null)
            throw new InvalidOperationException(
                "AN-01's coupled pair did not run, so its result figures would be empty frames: "
              + (result.Error ?? result.Status.ToString()));

        // The NPY, not the .sNp beside it: the data-source picker enumerates results/*.npy, so that
        // is the id it offers and the name it shows in the toolbar combo.
        return Path.GetFileName(result.NpyPath);
    });

    /// <summary>
    /// <b>Return loss at both ends of the driven line.</b> S(1,1) and S(2,2) — ports 1 and 2 are the
    /// two ends of the same 254 µm conductor, so on a uniform line meshed symmetrically they must
    /// lie on top of each other, and here they do. That coincidence is the figure's second job: a
    /// run in which they separate has an asymmetry in the mesh or in the ports that the geometry
    /// does not have.
    /// </summary>
    public static FigureScene ReturnLoss()
    {
        var (vm, plot) = DocDataDisplayFixtures.PlotFor(Result(), PlotType.Rect, traces: 2);
        DocDataDisplayFixtures.PickSignal(plot.Inspector.Traces[0], "S(1,1)");
        DocDataDisplayFixtures.PickSignal(plot.Inspector.Traces[1], "S(2,2)");

        // The SECOND trace dashed, through the trace card's own line-style picker. The two curves
        // coincide to every digit plotted — which is the figure's point — and two coincident solid
        // lines are indistinguishable from one.
        DocDataDisplayFixtures.Dashed(plot.Inspector.Traces[1]);
        return new FigureScene(
            new DataDisplayView { DataContext = DocDataDisplayFixtures.Document(vm, "coupled-pair") })
        { AfterLayout = DocDataDisplayFixtures.CentredPlot(plot) };
    }

    /// <summary>
    /// <b>Through and coupled, on one pair of axes.</b> S(2,1) is the far end of the driven line;
    /// S(3,1) is the near end of its neighbour — the backward-coupled port, which is the number a
    /// coupled-line design is usually about.
    /// </summary>
    public static FigureScene ThroughAndCoupled()
    {
        var (vm, plot) = DocDataDisplayFixtures.PlotFor(Result(), PlotType.Rect, traces: 2);
        DocDataDisplayFixtures.PickSignal(plot.Inspector.Traces[0], "S(2,1)");
        DocDataDisplayFixtures.PickSignal(plot.Inspector.Traces[1], "S(3,1)");
        return new FigureScene(
            new DataDisplayView { DataContext = DocDataDisplayFixtures.Document(vm, "coupled-pair") })
        { AfterLayout = DocDataDisplayFixtures.CentredPlot(plot) };
    }
}

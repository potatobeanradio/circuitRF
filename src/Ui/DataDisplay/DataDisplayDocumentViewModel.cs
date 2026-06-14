using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RfCore;
using CircuitRF.Ui.DataDisplay.ViewModels;

namespace CircuitRF.Ui.DataDisplay;

/// <summary>
/// View model for a Data Display document tab (document shell — not the canvas VM).
/// </summary>
public sealed partial class DataDisplayDocumentViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isDirty;

    /// <summary>
    /// The ported DisplayWindowViewModel — owns tabs, canvas VMs, and commands.
    /// Wrapped here rather than merged so the ported VM stays intact.
    /// </summary>
    public DisplayWindowViewModel Window { get; }

    public DataDisplayDocumentViewModel()
    {
        Window = new DisplayWindowViewModel();
        // The DisplayWindowViewModel constructor already creates one initial tab
        // with ActiveTab set.  Seed the demo plot into that tab's canvas.
        SeedDemoPlot();
    }

    // TEMP 3a — removed when SnpLibrary + Load Touchstone land in 3b
    private void SeedDemoPlot()
    {
        var display = Window.ActiveTab?.DataDisplay;
        if (display is null) return;

        // Clear the default empty Smith plot the constructor added.
        foreach (var existing in display.Plots.ToArray())
            display.InternalRemoveContainer(existing);
        display.UndoRedo.Clear();

        // Build synthetic 2-port S-parameter data: simple low-pass S21 response.
        double[] freqs = { 1e9, 2e9, 3e9, 4e9, 5e9, 6e9, 7e9, 8e9, 9e9, 10e9 };
        var snp = new SNP(freqs, 2);

        for (int i = 0; i < freqs.Length; i++)
        {
            double f   = freqs[i];
            double fc  = 5e9;
            double t   = f / fc;

            double s21Mag = 1.0 / Math.Sqrt(1.0 + t * t);
            double s21Arg = -Math.Atan(t);
            double s11Mag = 0.1;
            double s11Arg = Math.PI - Math.Atan(t);

            snp.Matrices[i][1, 0] = Complex.FromPolarCoordinates(s21Mag, s21Arg);
            snp.Matrices[i][0, 1] = Complex.FromPolarCoordinates(s21Mag, s21Arg);
            snp.Matrices[i][0, 0] = Complex.FromPolarCoordinates(s11Mag, s11Arg);
            snp.Matrices[i][1, 1] = Complex.FromPolarCoordinates(s11Mag, s11Arg);
        }

        var trace = new Trace(snp, MatrixType.S, 1, 0, DependentVarFormat.Db);
        trace.BuildPath(PlotType.Rect, FreqUnit.GHz);

        // Add the container via the VM (sets position, registers with undo, etc.)
        var container = display.AddPlot(PlotType.Rect, FreqUnit.GHz, 20, 20, 520, 360);
        container.PlotVM.Plot.Traces.Add(trace);
        container.PlotVM.Plot.Autoscale();
        container.OnPlotChanged(null, System.EventArgs.Empty);

        // Start with a clean undo stack — the seed is the initial state.
        display.UndoRedo.Clear();
    }
}

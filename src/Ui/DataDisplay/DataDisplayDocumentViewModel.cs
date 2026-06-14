using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RfCore;

namespace CircuitRF.Ui.DataDisplay;

/// <summary>
/// View model for a Data Display document tab (document shell — not the canvas VM).
/// </summary>
public sealed partial class DataDisplayDocumentViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isDirty;

    // TEMP 7.1b — replaced by real plot creation in 7.1c
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlots))]
    private Plot? _currentPlot;

    public bool HasPlots => CurrentPlot != null;

    // TEMP 7.1b — replaced by real plot creation in 7.1c
    [RelayCommand]
    private void InsertDemoPlot()
    {
        // Synthetic 2-port S-parameter data: simple low-pass S21 response
        double[] freqs = { 1e9, 2e9, 3e9, 4e9, 5e9, 6e9, 7e9, 8e9, 9e9, 10e9 };
        var snp = new SNP(freqs, 2);

        // S21 = magnitude drops from ~0 dB at 1 GHz to ~-20 dB at 10 GHz
        // S11 ~ -20 dB (good match)
        for (int i = 0; i < freqs.Length; i++)
        {
            double f   = freqs[i];
            double fc  = 5e9;
            double t   = f / fc;

            // Simple first-order low-pass response
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

        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(trace);
        plot.Autoscale();

        CurrentPlot = plot;
    }
}

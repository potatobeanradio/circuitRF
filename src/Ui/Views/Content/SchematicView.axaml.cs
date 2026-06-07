using System;
using System.Diagnostics;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CircuitRF.Ui.Renderers;

namespace CircuitRF.Ui.Views.Content;

public partial class SchematicView : UserControl
{
    private DispatcherTimer? _fpsTimer;

    public SchematicView()
    {
        InitializeComponent();

        // Read the renderer's last frame ticks every ~333 ms and update the toolbar readout.
        // The canvas also draws its own overlay; this mirrors it in the toolbar for visibility.
        _fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(333) };
        _fpsTimer.Tick += (_, _) => UpdateFpsDisplay();
        _fpsTimer.Start();
    }

    private void OnZoomToFit(object? sender, RoutedEventArgs e) => SchematicCanvasCtrl.ZoomToFit();
    private void OnZoomToPage(object? sender, RoutedEventArgs e) => SchematicCanvasCtrl.ZoomToPage();

    private void UpdateFpsDisplay()
    {
        long ticks = Volatile.Read(ref SchematicRenderer.LastFrameTicks);
        if (ticks <= 0)
        {
            FpsText.Text = "";
            return;
        }
        double ms  = ticks * 1000.0 / Stopwatch.Frequency;
        double fps = ms > 0 ? 1000.0 / ms : 0;
        FpsText.Text = $"{ms:F1} ms · {fps:F0} fps";
    }
}

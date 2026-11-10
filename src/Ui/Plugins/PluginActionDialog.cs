using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CircuitRF.Design.Plugins;

namespace CircuitRF.Ui.Plugins;

/// <summary>What one OK did: whether the dialog may close, and what to show if not.</summary>
public sealed record PluginRunAnswer(bool Close, IReadOnlyList<PluginMessage> Messages);

/// <summary>Runs a plugin with the resolved parameters — <c>hello</c>, <c>update</c>, <c>commit</c> and placement —
/// supplied by the workspace, so the dialog holds no process and no workspace logic.</summary>
public delegate Task<PluginRunAnswer> PluginRunner(
    JsonObject parameters, bool keepStaging, Action<PluginProgress> progress, CancellationToken cancel);

/// <summary>
/// The parameter dialog an <c>action</c> plugin opens with (brief-plg-1 §6), built from the manifest's <c>params</c>:
/// a quantity is typed with an SI prefix or an expression and read by circuitRF's one expression engine; a choice,
/// substrate, technology, cell or result is a picker; a file is a field with a <c>…</c> picker. OK runs the plugin
/// with a busy bar and Stop; the plugin's messages show here, and an error keeps the dialog open.
///
/// <para>Nothing moves while it runs: the busy bar, the message area and Stop are always laid out, and only their
/// state changes.</para>
///
/// <para><b>Widths.</b> The user can widen the dialog — every box without a size hint widens with it — and the width
/// is remembered per plugin. A typed parameter that declares <c>chars</c> gets a box about that many characters wide,
/// measured in the theme's font, with its unit or <c>…</c> beside it rather than at the far edge.</para>
/// </summary>
public static class PluginActionDialog
{
    public const double DefaultWidth = 560;
    public const double MinimumWidth = 440;

    public static Task ShowAsync(Window owner, PluginEntry entry, string? workspaceDir, PluginRunner run)
    {
        string id = entry.Manifest!.Id;
        return Build(entry, workspaceDir, run, PluginHost.DialogWidth(id), w => PluginHost.RecordDialogWidth(id, w)).ShowDialog(owner);
    }

    /// <summary>The dialog, unshown — what a snapshot renders.</summary>
    /// <param name="width">The width to open at — the one last left — or null for the default.</param>
    /// <param name="widthChanged">Hears the width on close when the user changed it.</param>
    public static Window Build(PluginEntry entry, string? workspaceDir, PluginRunner run,
                               double? width = null, Action<double>? widthChanged = null)
    {
        var manifest = entry.Manifest!;
        double opened = Math.Max(width ?? DefaultWidth, MinimumWidth);
        var dialog = new Window
        {
            Title = manifest.Name, Width = opened, MinWidth = MinimumWidth, SizeToContent = SizeToContent.Height, CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        dialog.Closed += (_, _) =>
        {
            if (Math.Abs(dialog.Bounds.Width - opened) >= 1) widthChanged?.Invoke(dialog.Bounds.Width);
        };

        var readers = new Dictionary<string, Func<string>>(StringComparer.Ordinal);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowSpacing = 6, ColumnSpacing = 8 };
        int row = 0;

        foreach (var p in manifest.Params)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = p.DisplayLabel, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, row);
            grid.Children.Add(label);

            // The same control the panel draws (PluginParamControls), so the dialog and the panel cannot differ.
            var control = PluginParamControls.Build(p, workspaceDir, PluginParams.DefaultText(p), p.Chars, () => dialog);
            if (p.Kind != PluginParamKind.Readout) readers[p.Name] = control.Read;
            control.PlaceIn(grid, row);
            row++;
        }

        // Always laid out: the message area keeps its height, the bar its slot, Stop its place.
        var messages = new StackPanel { Spacing = 2 };
        var messageArea = new ScrollViewer { Content = messages, Height = 72, Margin = new Thickness(0, 12, 0, 0) };
        var busy = new ProgressBar { Height = 4, Minimum = 0, Maximum = 1, Opacity = 0, Margin = new Thickness(0, 8, 0, 0) };
        var status = new TextBlock { FontSize = 11, Opacity = 0.7, MinHeight = 15, TextTrimming = TextTrimming.CharacterEllipsis };
        var keep = new CheckBox
        {
            Content = "Keep staging", FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(keep, "Keep the folder the plugin wrote its files into after it finishes — for checking what a plugin produced.");
        var stop   = new Button { Content = "Stop", Width = 80, FontSize = 12, IsEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", Width = 80, FontSize = 12, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var ok     = new Button { Content = "OK", Width = 80, FontSize = 12, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };

        CancellationTokenSource? running = null;
        stop.Click   += (_, _) => running?.Cancel();
        cancel.Click += (_, _) => { running?.Cancel(); dialog.Close(); };
        dialog.Closing += (_, _) => running?.Cancel();

        ok.Click += async (_, _) =>
        {
            var given = readers.ToDictionary(kv => kv.Key, kv => kv.Value(), StringComparer.Ordinal);
            var (values, errors) = PluginParams.Resolve(manifest.Params, given, new PluginParamContext(workspaceDir));
            if (values is null) { Show(errors.Select(e => new PluginMessage("error", e))); return; }

            running = new CancellationTokenSource();
            SetBusy(true);
            PluginRunAnswer answer;
            try
            {
                answer = await run(values, keep.IsChecked == true,
                    pr => Dispatcher.UIThread.Post(() =>
                    {
                        busy.IsIndeterminate = pr.Fraction is null;
                        if (pr.Fraction is { } f) busy.Value = Math.Clamp(f, 0, 1);
                        status.Text = pr.Text ?? "";
                    }),
                    running.Token);
            }
            finally
            {
                running.Dispose();
                running = null;
                SetBusy(false);
            }
            if (answer.Close) { dialog.Close(); return; }
            Show(answer.Messages);
        };

        void SetBusy(bool on)
        {
            busy.Opacity = on ? 1 : 0;
            busy.IsIndeterminate = on;
            if (!on) status.Text = "";
            stop.IsEnabled = on;
            ok.IsEnabled = !on;
            grid.IsEnabled = !on;
        }

        void Show(IEnumerable<PluginMessage> list)
        {
            messages.Children.Clear();
            foreach (var m in list)
                messages.Children.Add(new TextBlock
                {
                    Text = m.Text, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                    Foreground = m.Severity switch { "error" => Brushes.IndianRed, "warning" => Brushes.DarkOrange, _ => null },
                });
        }

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20, 16),
            Children =
            {
                new TextBlock { Text = manifest.Name, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock
                {
                    Text = manifest.Summary ?? "", FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 12), IsVisible = !string.IsNullOrWhiteSpace(manifest.Summary),
                },
                grid,
                messageArea,
                busy,
                status,
                new DockPanel
                {
                    Margin = new Thickness(0, 10, 0, 0), LastChildFill = false,
                    Children =
                    {
                        keep,
                        Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { stop, cancel, ok } }, Avalonia.Controls.Dock.Right),
                    },
                },
            },
        };
        return dialog;
    }

    private static Control Docked(Control c, Avalonia.Controls.Dock dock) { DockPanel.SetDock(c, dock); return c; }
}

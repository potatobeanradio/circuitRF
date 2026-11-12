using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// HZN-8 R-hzn8-1 — Hertzon's row in Settings ▸ Solvers: the path of <c>hertzon-cli</c> (blank means search: the environment, PATH,
/// then the installer's own folders) and what was found — the version, the command-line schema and how it was found — or the
/// sentence that turns Run in Hertzon off. The question is <see cref="HertzonDiscovery.Check"/>, asked off the UI thread.
///
/// <para>The status line keeps its height whatever it says, so nothing below it moves.</para>
/// </summary>
public sealed class HertzonSettingsRow : UserControl
{
    private readonly TextBox _box = new() { FontSize = 12, PlaceholderText = "Leave blank to search (CIRCUITRF_HERTZON, then PATH, then Hertzon's own folder)" };
    private readonly SelectableTextBlock _status = new() { FontSize = 11, Opacity = 0.75, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 32, MaxLines = 3,
                                                           Text = "Not checked yet." };

    public HertzonSettingsRow() : this(HertzonDiscovery.Shared) { }

    private readonly HertzonDiscovery _discovery;

    public HertzonSettingsRow(HertzonDiscovery discovery)
    {
        _discovery = discovery;
        var browse = new Button { Content = "…", FontSize = 11, Padding = new Thickness(8, 3), Margin = new Thickness(6, 0, 0, 0) };
        ToolTip.SetTip(browse, "Pick hertzon-cli from disk");
        ToolTip.SetTip(_box, "The program hertzon-cli, or the folder Hertzon is installed in. circuitRF asks it which version it is and which command-line schema it speaks, once per session.");
        _box.LostFocus += (_, _) => Commit();
        browse.Click += async (_, _) => await BrowseAsync();

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(browse, 1);
        row.Children.Add(_box);
        row.Children.Add(browse);

        var group = new Border { Margin = new Thickness(0, 12, 0, 0) };
        group.Classes.Add("group");
        group.Child = new StackPanel
        {
            Children =
            {
                new SelectableTextBlock { Text = "Hertzon (command line)", FontSize = 12, FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) },
                row,
                new Border { Margin = new Thickness(0, 4, 0, 0), Child = _status },
            },
        };
        Content = group;
        try { _box.Text = AppPreferencesIo.Load().HertzonCliPath ?? ""; } catch (Exception) { }
        AttachedToVisualTree += (_, _) => Refresh();
    }

    private void Commit()
    {
        string typed = (_box.Text ?? "").Trim();
        try { AppPreferencesIo.Update(p => p.HertzonCliPath = typed.Length == 0 ? null : typed); } catch (Exception) { }
        _discovery.Forget();
        Refresh();
    }

    /// <summary>Asks again, off the UI thread, and writes the answer to the status line.</summary>
    public void Refresh()
    {
        if (_pinned) return;
        _status.Text = "Checking…";
        Task.Run(() => _discovery.Check()).ContinueWith(t => Dispatcher.UIThread.Post(() =>
            _status.Text = t.IsCompletedSuccessfully ? _discovery.Summary(t.Result) : "hertzon-cli could not be checked: " + t.Exception?.GetBaseException().Message));
    }

    /// <summary>The line the row shows, for a snapshot that must not wait on a process.</summary>
    public void Show(HertzonReadiness ready) { _pinned = true; _status.Text = _discovery.Summary(ready); }

    private bool _pinned;

    private async Task BrowseAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose hertzon-cli", AllowMultiple = false });
            if (files.Count != 1 || files[0].TryGetLocalPath() is not { Length: > 0 } path) return;
            _box.Text = path;
            Commit();
        }
        catch (Exception) { /* a cancelled or unavailable picker is not an error */ }
    }
}

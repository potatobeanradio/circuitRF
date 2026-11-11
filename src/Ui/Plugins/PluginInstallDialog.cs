using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CircuitRF.Design.Plugins;

namespace CircuitRF.Ui.Plugins;

/// <summary>
/// An install in progress (brief-plg-6 §4): what is happening, its log as it arrives — pip's output included — and
/// Cancel, which leaves nothing behind (the installer deletes its <c>.partial</c> folder). The buttons are both always
/// present; Cancel is enabled while it runs and Close after.
/// </summary>
public sealed class PluginInstallDialog : Window
{
    /// <summary>Said before anything starts, so the network step is never a surprise (§3).</summary>
    public const string NetworkNotice =
        "Downloading the plugin reaches the network, and so does installing a Python plugin's requirements with pip. Nothing else does.";

    private readonly TextBox _log;
    private readonly TextBlock _status;
    private readonly Button _cancel, _close;
    private readonly CancellationTokenSource _cts = new();

    public PluginInstallResult? Result { get; private set; }

    public PluginInstallDialog(string heading, string? notice = NetworkNotice)
    {
        Title = "Install Plugin";
        Width = 600; Height = 420; MinWidth = 440; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;

        _log = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 11, VerticalContentAlignment = VerticalAlignment.Top,
            FontFamily = new FontFamily("Menlo, Consolas, monospace"),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_log, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        _status = new TextBlock { Text = "Working…", FontSize = 12, TextWrapping = TextWrapping.Wrap, MinHeight = 32 };
        _cancel = new Button { Content = "Cancel", Width = 90, FontSize = 12, HorizontalContentAlignment = HorizontalAlignment.Center };
        _close = new Button { Content = "Close", Width = 90, FontSize = 12, IsEnabled = false, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        _cancel.Click += (_, _) => { _cancel.IsEnabled = false; _status.Text = "Cancelling…"; _cts.Cancel(); };
        _close.Click += (_, _) => Close();
        Closing += (_, e) => { if (Result is null && !_cts.IsCancellationRequested) _cts.Cancel(); };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"), Margin = new Thickness(16, 14) };
        void Put(Control c, int row) { Grid.SetRow(c, row); grid.Children.Add(c); }
        Put(new TextBlock { Text = heading, FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }, 0);
        Put(new TextBlock { Text = notice ?? "", FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) }, 1);
        Put(_log, 2);
        _status.Margin = new Thickness(0, 8, 0, 0);
        Put(_status, 3);
        Put(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
            Margin = new Thickness(0, 10, 0, 0), Children = { _cancel, _close },
        }, 4);
        Content = grid;
    }

    /// <summary>One line of the log, from any thread.</summary>
    public void Append(string line)
        => Dispatcher.UIThread.Post(() =>
        {
            _log.Text = string.IsNullOrEmpty(_log.Text) ? line : _log.Text + "\n" + line;
            _log.CaretIndex = _log.Text.Length;
        });

    /// <summary>Shows the dialog, runs <paramref name="work"/> with the log and Cancel wired in, and returns what it did.
    /// The dialog stays open on a refusal, so its log can be read; it closes itself on success.</summary>
    public static async Task<PluginInstallResult> RunAsync(Window owner, string heading,
        Func<Action<string>, CancellationToken, Task<PluginInstallResult>> work, string? notice = NetworkNotice)
    {
        var dialog = new PluginInstallDialog(heading, notice);
        var shown = dialog.ShowDialog(owner);
        PluginInstallResult result;
        try { result = await Task.Run(() => work(dialog.Append, dialog._cts.Token)); }
        catch (OperationCanceledException) { result = PluginInstallResult.Stopped(); }
        dialog.Finish(result);
        if (result.Ok) dialog.Close();
        await shown;
        return result;
    }

    /// <summary>The finished state: the outcome in the status line, Cancel off, Close on.</summary>
    public void Finish(PluginInstallResult result)
    {
        Result = result;
        _status.Text = result.Ok ? $"Installed {result.Id} {result.Version}." : result.Refusal ?? "Nothing was installed.";
        _cancel.IsEnabled = false;
        _close.IsEnabled = true;
    }
}

/// <summary>
/// Install from address… (brief-plg-6 §2): a GitHub or GitLab link — pasted on the Plugins tab or typed here — and the
/// version to install, picked from the repository's releases (or its tags) once the link is entered. The version is
/// typed only when that list cannot be read. A link to a release or tag selects that one. Nothing here is on circuitRF's
/// plugin list, and the dialog says so.
/// </summary>
public sealed class PluginAddressDialog : Window
{
    public const string NotListedNote =
        "A plugin installed from a link is not on circuitRF's plugin list: nobody has reviewed it. It runs with your permissions once you allow it, so install it only if you trust where it came from.";

    private readonly TextBox _link, _typedTag;
    private readonly ComboBox _tags;
    private readonly TextBlock _status;
    private readonly Button _install;
    private readonly IPluginHttp? _http;
    private CancellationTokenSource? _reading;
    private string? _linkTag;
    private string? _lastRead;

    /// <summary>The link and tag to install, or null when cancelled.</summary>
    public (string Link, string Tag)? Answer { get; private set; }

    public PluginAddressDialog(string? link, IPluginHttp? http)
    {
        _http = http;
        Title = "Install Plugin from a Link";
        Width = 560; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;

        _link = new TextBox { Text = link ?? "", FontSize = 12, PlaceholderText = "https://github.com/owner/repository or https://gitlab.com/group/project" };
        _tags = new ComboBox { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Paste a link first" };
        // Typed only when the list cannot be read; it sits in the picker's own cell, so nothing below it moves.
        _typedTag = new TextBox { FontSize = 12, PlaceholderText = "Type the tag, e.g. v1.0.0", IsVisible = false };
        _status = new TextBlock { FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, MinHeight = 30, Margin = new Thickness(0, 4, 0, 0) };
        _install = new Button { Content = "Install", Width = 90, FontSize = 12, IsEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", Width = 90, FontSize = 12, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => Close();
        _install.Click += (_, _) =>
        {
            string? tag = _typedTag.IsVisible ? _typedTag.Text?.Trim() : (_tags.SelectedItem as ComboBoxItem)?.Tag as string;
            if (string.IsNullOrEmpty(tag)) return;
            Answer = (_link.Text!.Trim(), tag);
            Close();
        };
        _link.TextChanged += (_, _) => ReadSoon();
        _typedTag.TextChanged += (_, _) => Validate();
        _tags.SelectionChanged += (_, _) => Validate();

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 8, ColumnSpacing = 10 };
        void Put(Control c, int row, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); form.Children.Add(c); }
        Put(new TextBlock { Text = "Link", FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, 0, 0);
        Put(_link, 0, 1);
        Put(new TextBlock { Text = "Version", FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, 1, 0);
        Put(_tags, 1, 1);
        Put(_typedTag, 1, 1);

        Content = new StackPanel
        {
            Margin = new Thickness(20, 16),
            Children =
            {
                new TextBlock { Text = "Install a plugin from GitHub or GitLab", FontSize = 13, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = "Paste the repository's link — its page, a clone address, or a release. circuitRF installs the release's build for this computer, or its source.",
                    FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10),
                },
                form,
                _status,
                PluginUi.Tile(new TextBlock { Text = NotListedNote, FontSize = 11, TextWrapping = TextWrapping.Wrap }),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
                    Margin = new Thickness(0, 16, 0, 0), Children = { cancel, _install },
                },
            },
        };
        Opened += (_, _) => { if (!string.IsNullOrWhiteSpace(_link.Text)) ReadSoon(immediately: true); };
    }

    public static async Task<(string Link, string Tag)?> AskAsync(Window owner, string? link, IPluginHttp? http)
    {
        var dialog = new PluginAddressDialog(link, http);
        await dialog.ShowDialog(owner);
        return dialog.Answer;
    }

    /// <summary>Fills the picker as a list would arrive — for a snapshot, with no network.</summary>
    public void ShowReleases(System.Collections.Generic.IReadOnlyList<PluginRelease> releases, string? selected = null)
    {
        _tags.Items.Clear();
        foreach (var r in releases)
            _tags.Items.Add(new ComboBoxItem
            {
                Content = r.Tag + (r.Name is { Length: > 0 } n && n != r.Tag ? $" — {n}" : "") + (r.Prerelease ? " (pre-release)" : "") + (r.Assets.Count switch { 0 => "", 1 => " · 1 file", int count => $" · {count} files" }),
                Tag = r.Tag,
            });
        _tags.IsVisible = true; _typedTag.IsVisible = false;
        string? want = selected ?? releases.FirstOrDefault(r => !r.Prerelease)?.Tag ?? releases.FirstOrDefault()?.Tag;
        _tags.SelectedItem = System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.OfType<ComboBoxItem>(_tags.Items), i => (string?)i.Tag == want);
        _status.Text = releases.Count == 0 ? "This repository has no releases or tags to install." : $"{releases.Count} version{(releases.Count == 1 ? "" : "s")} found.";
        Validate();
    }

    private void ReadSoon(bool immediately = false)
    {
        string text = _link.Text?.Trim() ?? "";
        // A text-changed that does not change the link (the box raising it as it is first shown) reads nothing again.
        if (text == _lastRead) return;
        _lastRead = text;
        _reading?.Cancel();
        _tags.Items.Clear();
        _install.IsEnabled = false;
        if (PluginRepository.Parse(text) is not { } parsed)
        {
            _status.Text = text.Length == 0 ? "" : "That is not a GitHub or GitLab repository link.";
            return;
        }
        _linkTag = parsed.Tag;
        var cts = _reading = new CancellationTokenSource();
        _status.Text = $"Reading the versions of {parsed.Repository.Path}…";
        _ = Read();

        async Task Read()
        {
            try
            {
                if (!immediately) await Task.Delay(500, cts.Token);
                var (releases, failure) = _http is null ? (null, "this window cannot reach a network") : await PluginInstaller.ListReleasesAsync(_http, text, cts.Token);
                if (cts.IsCancellationRequested) return;
                if (releases is not null) { ShowReleases(releases, _linkTag); return; }
                _tags.IsVisible = false; _typedTag.IsVisible = true;
                if (_linkTag is not null && string.IsNullOrEmpty(_typedTag.Text)) _typedTag.Text = _linkTag;
                _status.Text = $"The list of versions could not be read ({failure?.TrimEnd('.')}). Type the tag to install.";
                Validate();
            }
            catch (OperationCanceledException) { }
        }
    }

    private void Validate()
        => _install.IsEnabled = PluginRepository.Parse(_link.Text ?? "") is not null
                                && (_typedTag.IsVisible ? !string.IsNullOrWhiteSpace(_typedTag.Text) : _tags.SelectedItem is not null);
}

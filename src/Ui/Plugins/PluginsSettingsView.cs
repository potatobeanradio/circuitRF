using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CircuitRF.Design.Plugins;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Plugins;

/// <summary>Where Settings ▸ Plugins reads from — this installation's, or a snapshot's. Every member is a function so
/// that nothing is read, and nothing reached, until it is needed: <see cref="Http"/> is called only by Refresh, Install
/// and Update (brief-plg-6 gate 5).</summary>
public sealed record PluginsSettingsContext(
    Func<PluginCatalogSources> Sources,
    Func<PluginTrustStore> Trust,
    Func<PluginIndex> Index,
    Func<PluginPolicyState> Policy,
    Func<IPluginHttp> Http,
    string? PublicKey)
{
    public static PluginsSettingsContext Application => new(
        PluginHost.Sources, PluginHost.Trust, PluginHost.Index, () => PluginHost.Policy, () => new PluginHttp(),
        Updates.ReleaseKeys.PublicKeySpkiBase64);
}

/// <summary>
/// Settings ▸ Plugins (brief-plg-6 §4): whether plugins may run; a box to paste a GitHub or GitLab link into; the
/// Installed list with each plugin's permission; the Available list from circuitRF's vetted list; and the other ways in.
/// <b>This tab and the first-run prompt are the only places a plugin is allowed to run.</b> An install made outside the
/// window and not asked about yet is listed first, marked. The trust table is read from the file on every reload and
/// written one key at a time, so a denial the command line recorded while this was open stands.
///
/// <para>Every button is always present and enabled per selection, and every status line has its height reserved, so
/// nothing moves as the selection or a result changes.</para>
/// </summary>
public sealed class PluginsSettingsView : UserControl
{
    private readonly PluginsSettingsContext _ctx;
    private readonly CheckBox _allowAll;
    private readonly TextBlock _policyReason;
    private readonly TextBox _paste;
    private readonly ListBox _installed, _available;
    private readonly Button _update, _rollback, _uninstall, _openFolder, _installListed;
    private readonly TextBlock _status, _listOrigin;
    private IReadOnlyList<PluginInstalledRow> _installedRows = [];
    private IReadOnlyList<PluginAvailableRow> _availableRows = [];
    private PluginIndex _index = new([]);
    private bool _loading;

    public PluginsSettingsView() : this(null) { }

    public PluginsSettingsView(PluginsSettingsContext? context)
    {
        _ctx = context ?? PluginsSettingsContext.Application;

        _allowAll = new CheckBox { Content = "Allow plugins to run", FontSize = 12 };
        ToolTip.SetTip(_allowAll, "Plugins are programs other people write. Each one still asks before it first runs. Turning this off stops every plugin; nothing is uninstalled.");
        _allowAll.IsCheckedChanged += (_, _) => { if (!_loading) AppPreferencesIo.Update(p => p.PluginsEnabled = _allowAll.IsChecked); };
        _policyReason = new TextBlock { FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), MinHeight = 15 };

        _paste = new TextBox { FontSize = 12, PlaceholderText = "Paste a GitHub or GitLab link to install a plugin" };
        var pasteInstall = Btn("Install…", "Install the plugin at this link: pick its version, then it is downloaded and installed.", async () => await InstallFromLinkAsync(_paste.Text));
        _paste.KeyDown += async (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; await InstallFromLinkAsync(_paste.Text); } };

        _installed = new ListBox { Height = 168, FontSize = 12, SelectionMode = SelectionMode.Single, Padding = new Thickness(0) };
        // Items take the list's width: a long note wraps rather than widening its row past the column headers.
        ScrollViewer.SetHorizontalScrollBarVisibility(_installed, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _installed.SelectionChanged += (_, _) => Enable();
        _update     = Btn("Update", "Install the newer version from where this one came from. It asks again before it runs, unless both are on circuitRF's list.", UpdateAsync);
        _rollback   = Btn("Roll Back", "Go back to the version this update replaced.", RollbackAsync);
        _uninstall  = Btn("Uninstall", "Remove the plugin, its permission and what was remembered for it. A linked folder is unlinked and kept.", UninstallAsync);
        _openFolder = Btn("Open Folder", "Show the plugin's folder.", () => { if (Selected is { } r) FileReveal.Reveal(r.Entry.Directory); return Task.CompletedTask; });

        _available = new ListBox { Height = 120, FontSize = 12, SelectionMode = SelectionMode.Single, Padding = new Thickness(0) };
        ScrollViewer.SetHorizontalScrollBarVisibility(_available, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _available.SelectionChanged += (_, _) => Enable();
        _installListed = Btn("Install", "Download this plugin from its release, check it against the list, and install it.", InstallListedAsync);
        _listOrigin = new TextBlock { FontSize = 11, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };

        _status = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, MinHeight = 30, Margin = new Thickness(0, 8, 0, 0) };

        var pasteRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(pasteInstall, 1);
        pasteRow.Children.Add(_paste); pasteRow.Children.Add(pasteInstall);

        Content = new StackPanel
        {
            Spacing = 0,
            Children =
            {
                Heading("Plugins", 0),
                _allowAll,
                _policyReason,

                Heading("Add a Plugin", 12),
                pasteRow,
                Row(12, Btn("Install from Folder…", "Copy a plugin folder in.", InstallFolderAsync),
                        Btn("Link Folder…", "Run a plugin folder in place, for its author: changes take effect at its next start.", LinkFolderAsync),
                        Btn("Install from Address…", "Install from a GitHub or GitLab repository, choosing the version.", async () => await InstallFromLinkAsync(null))),

                Heading("Installed", 18),
                Header(["Name", "Version", "Source", "Permitted", "Update"]),
                _installed,
                Row(6, _update, _rollback, _uninstall, _openFolder),

                Heading("Available on circuitRF's Plugin List", 18),
                Header(["Name", "Summary", "Latest", "Platforms"], available: true),
                _available,
                Row(6, _installListed, Btn("Refresh List", "Fetch the newest list from circuitRF's repository. Its signature is checked; a list that does not verify is not used.", RefreshAsync), _listOrigin),

                _status,
            },
        };
        Reload();
    }

    // ── state ───────────────────────────────────────────────────────────────────

    private PluginInstalledRow? Selected => _installed.SelectedIndex is int i and >= 0 && i < _installedRows.Count ? _installedRows[i] : null;
    private PluginAvailableRow? SelectedAvailable => _available.SelectedIndex is int i and >= 0 && i < _availableRows.Count ? _availableRows[i] : null;

    /// <summary>Reads everything again — files only — and redraws both lists, keeping the selection by id.</summary>
    public void Reload()
    {
        _loading = true;
        try
        {
            string? keep = Selected?.Id;
            var policy = _ctx.Policy();
            _allowAll.IsChecked = policy.IsOverridden ? false : AppPreferencesIo.Load().PluginsEnabled ?? true;
            _allowAll.IsEnabled = !policy.IsOverridden;
            _policyReason.Text = policy.IsOverridden ? policy.Reason : "";

            _index = _ctx.Index();
            var trust = _ctx.Trust();
            _installedRows = PluginsSettingsModel.Installed(_ctx.Sources(), trust, _index);
            _availableRows = PluginsSettingsModel.Available(_index, _installedRows);

            _installed.Items.Clear();
            if (_installedRows.Count == 0) _installed.Items.Add(Item(Placeholder("No plugins are installed. Paste a link above, or install one from the list below."), false));
            foreach (var r in _installedRows) _installed.Items.Add(Item(InstalledItem(r)));
            _available.Items.Clear();
            if (_availableRows.Count == 0) _available.Items.Add(Item(Placeholder("The list has no plugins yet."), false));
            foreach (var r in _availableRows) _available.Items.Add(Item(AvailableItem(r)));
            _listOrigin.Text = _index.Origin == PluginIndexOrigin.Refreshed ? "The list as last refreshed." : "The list this release was built with.";

            int at = keep is null ? -1 : _installedRows.ToList().FindIndex(r => r.Id == keep);
            _installed.SelectedIndex = at;
        }
        finally { _loading = false; }
        Enable();
    }

    private void Enable()
    {
        var r = Selected;
        _update.IsEnabled = r?.UpdateVersion is not null;
        _rollback.IsEnabled = r?.CanRollBack == true;
        _uninstall.IsEnabled = r is not null;
        _openFolder.IsEnabled = r is not null;
        _installListed.IsEnabled = SelectedAvailable is { Dim: null, InstalledVersion: null };
    }

    private void Say(string text, bool error = false)
    {
        _status.Text = text;
        _status.Foreground = error ? Brushes.IndianRed : null;
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    // ── permission ──────────────────────────────────────────────────────────────

    private async Task SetPermissionAsync(PluginInstalledRow row, int choice)
    {
        if (_loading || choice == PluginsSettingsModel.PermissionIndex(row.Permitted)) return;
        if (choice == 1)
        {
            if (Owner is not { } owner) return;
            if (await PluginAllow.AllowAsync(owner, row.Entry) is { } why) Say($"{row.Name} was not allowed: {why}", error: true);
            else Say($"{row.Name} may run on this machine.");
        }
        else
        {
            PluginHost.RecordPermission(row.Entry.Directory, choice == 2 ? false : null);
            Say(choice == 2 ? $"{row.Name} will not run on this machine." : $"{row.Name} will ask before it next runs.");
        }
        Reload();
    }

    // ── actions ─────────────────────────────────────────────────────────────────

    private async Task InstallFromLinkAsync(string? link)
    {
        if (Owner is not { } owner) return;
        IPluginHttp http = _ctx.Http();
        try
        {
            if (await PluginAddressDialog.AskAsync(owner, link?.Trim(), http) is not { } answer) return;
            await RunInstall($"Installing from {answer.Link} ({answer.Tag})",
                (ctx, ct) => PluginInstaller.InstallFromAddressAsync(ctx, answer.Link, answer.Tag, ct), http);
            _paste.Text = "";
        }
        finally { (http as IDisposable)?.Dispose(); }
    }

    private async Task InstallListedAsync()
    {
        if (SelectedAvailable is not { } row) return;
        IPluginHttp http = _ctx.Http();
        try { await RunInstall($"Installing {row.Entry.Name} {row.Latest?.Version}", (ctx, ct) => PluginInstaller.InstallFromListAsync(ctx, _index, row.Entry.Id, row.Latest?.Version, ct), http); }
        finally { (http as IDisposable)?.Dispose(); }
    }

    private async Task InstallFolderAsync()
    {
        if (await PickFolder("Install a Plugin Folder") is not { } folder) return;
        await RunInstall($"Installing from {folder}", (ctx, ct) => PluginInstaller.InstallFolderAsync(ctx, folder, ct), null);
    }

    private async Task LinkFolderAsync()
    {
        if (await PickFolder("Link a Plugin Folder") is not { } folder) return;
        var result = PluginInstaller.Link(PluginHost.InstallContext(null, null), folder);
        Say(result.Ok ? $"Linked {result.Id} in {folder}. It runs in place; changes take effect at its next start." : result.Refusal!, !result.Ok);
        Reload();
    }

    private async Task UpdateAsync()
    {
        if (Selected is not { } row || row.Record is not { } before) return;
        bool wasAllowed = row.Permitted == PluginTrustDecision.Allowed;
        IPluginHttp http = _ctx.Http();
        try
        {
            var result = await RunInstall($"Updating {row.Name} to {row.UpdateVersion}", (ctx, ct) => PluginInstaller.UpdateToNewestAsync(ctx, _index, row.Id, ct), http, reload: false);
            // An update asks again unless the new version is on the vetted list and the old one was (plugins.md §9.4).
            if (result is { Ok: true, Directory: { } dir } && wasAllowed && before.Source == "list" && PluginInstaller.RecordFor(dir)?.Source == "list")
                PluginHost.RecordPermission(dir, true);
        }
        finally { (http as IDisposable)?.Dispose(); }
        Reload();
    }

    private Task RollbackAsync()
    {
        if (Selected is not { } row) return Task.CompletedTask;
        var result = PluginInstaller.Rollback(PluginHost.InstallContext(null, null), row.Id);
        // The version gone back to was this machine's before the update: it keeps the permission it had then, which the
        // file still holds. Nothing is granted here that was not granted before.
        Say(result.Ok ? $"{row.Name} is back to {result.Version}." : result.Refusal!, !result.Ok);
        Reload();
        return Task.CompletedTask;
    }

    private async Task UninstallAsync()
    {
        if (Selected is not { } row || Owner is not { } owner) return;
        string what = row.IsLinked ? $"Unlink {row.Name}? Its folder {row.Entry.Directory} is kept."
                                   : $"Uninstall {row.Name} {row.Version}? Its folder, its permission and what was remembered for it are removed.";
        if (!await TextConfirmDialog.AskAsync(owner, "Plugins", row.IsLinked ? "Unlink Plugin" : "Uninstall Plugin", what, row.IsLinked ? "Unlink" : "Uninstall")) return;
        var prefs = PluginPreferences.Read(PluginHost.PreferencesPath);
        var result = PluginInstaller.Uninstall(PluginHost.InstallContext(null, null), row.Id, null, prefs.LinkedFolders);
        // The uninstall merged its removals into the file; the window's copy is read again so a later save keeps them gone.
        AppPreferencesIo.InvalidateCache();
        Say(result.Ok ? $"{row.Name} was removed." : result.Refusal!, !result.Ok);
        Reload();
    }

    private async Task RefreshAsync()
    {
        IPluginHttp http = _ctx.Http();
        try
        {
            Say("Fetching the plugin list…");
            var (index, refusal) = await PluginIndex.RefreshAsync(http, PluginInstaller.IndexCache(_ctx.Sources().PluginsRoot ?? PluginCatalogSources.DefaultRoot), _ctx.PublicKey);
            Say(index is not null ? $"The plugin list was refreshed: {index.Plugins.Count} plugin{(index.Plugins.Count == 1 ? "" : "s")}." : refusal!, index is null);
        }
        finally { (http as IDisposable)?.Dispose(); }
        Reload();
    }

    private async Task<PluginInstallResult> RunInstall(string heading, Func<PluginInstallContext, System.Threading.CancellationToken, Task<PluginInstallResult>> work,
        IPluginHttp? http, bool reload = true)
    {
        if (Owner is not { } owner) return PluginInstallResult.Refused("no window");
        var result = await PluginInstallDialog.RunAsync(owner, heading, (log, ct) => work(PluginHost.InstallContext(http, log), ct));
        if (result.Ok)
            Say($"Installed {result.Id} {result.Version}. It runs once you set Permitted to Allowed, or when you open it from Tools ▸ Plugins and allow it.");
        else Say(result.Refusal ?? "Nothing was installed.", error: !result.Cancelled);
        if (reload) Reload();
        if (result.Ok) _installed.SelectedIndex = _installedRows.ToList().FindIndex(r => r.Id == result.Id);
        return result;
    }

    private async Task<string?> PickFolder(string title)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return null;
        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    // ── rows ────────────────────────────────────────────────────────────────────

    private static readonly string InstalledColumns = "*,80,100,104,80";
    private static readonly string AvailableColumns = "150,*,70,120";

    private Control InstalledItem(PluginInstalledRow r)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(InstalledColumns), ColumnSpacing = 8 };
        var permitted = new ComboBox { FontSize = 11, Width = 100, ItemsSource = PluginsSettingsModel.PermissionChoices, SelectedIndex = PluginsSettingsModel.PermissionIndex(r.Permitted) };
        ToolTip.SetTip(permitted, "Ask: it asks before it next runs. Allowed: it runs, with your permissions. Denied: it never runs.");
        permitted.SelectionChanged += async (_, _) => await SetPermissionAsync(r, permitted.SelectedIndex);
        Put(grid, new TextBlock { Text = r.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = r.OutsideNotAllowed ? FontWeight.SemiBold : FontWeight.Normal }, 0);
        Put(grid, new TextBlock { Text = r.Version, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }, 1);
        Put(grid, new TextBlock { Text = r.SourceText, VerticalAlignment = VerticalAlignment.Center }, 2);
        Put(grid, permitted, 3);
        Put(grid, new TextBlock { Text = r.UpdateVersion is { } u ? $"{u} available" : "", VerticalAlignment = VerticalAlignment.Center, FontSize = 11 }, 4);
        if (r.Note is null) return grid;
        var note = new TextBlock { Text = r.Note, FontSize = 11, Opacity = r.OutsideNotAllowed ? 0.9 : 0.65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        if (r.OutsideNotAllowed) note.Foreground = Brushes.DarkOrange;
        if (r.Record?.Source is { Length: > 0 } source) ToolTip.SetTip(note, source.StartsWith("folder ", StringComparison.Ordinal) ? source[7..] : source);
        return new StackPanel { Children = { grid, note } };
    }

    private static Control AvailableItem(PluginAvailableRow r)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(AvailableColumns), ColumnSpacing = 8, Opacity = r.Dim is null ? 1 : 0.5 };
        Put(grid, new TextBlock { Text = r.Entry.Name, TextTrimming = TextTrimming.CharacterEllipsis }, 0);
        Put(grid, new TextBlock
        {
            Text = r.Dim is { } dim ? $"{char.ToUpperInvariant(dim[0])}{dim[1..]}. {r.Entry.Summary}" : r.InstalledVersion is { } v ? $"{r.Entry.Summary} (installed: {v})" : r.Entry.Summary ?? "",
            TextTrimming = TextTrimming.CharacterEllipsis,
        }, 1);
        Put(grid, new TextBlock { Text = r.Latest?.Version ?? "-" }, 2);
        Put(grid, new TextBlock { Text = r.Platforms, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 }, 3);
        if (r.Dim is not null) ToolTip.SetTip(grid, $"{r.Entry.Name} {r.Latest?.Version} {r.Dim}.");
        return grid;
    }

    private static Control Header(string[] names, bool available = false)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(available ? AvailableColumns : InstalledColumns), ColumnSpacing = 8, Margin = new Thickness(ItemPad.Left, 0, ItemPad.Right, 3) };
        for (int i = 0; i < names.Length; i++) Put(grid, new TextBlock { Text = names[i], FontSize = 11, Opacity = 0.6 }, i);
        return grid;
    }

    /// <summary>One padding for every row and for the column headers above them, so the two line up.</summary>
    private static readonly Thickness ItemPad = new(8, 4, 8, 4);

    private static ListBoxItem Item(Control content, bool selectable = true)
        => new() { Content = content, Padding = ItemPad, IsEnabled = selectable, HorizontalContentAlignment = HorizontalAlignment.Stretch };

    private static TextBlock Placeholder(string text) => new() { Text = text, FontSize = 12, Opacity = 0.6, FontStyle = FontStyle.Italic };

    private static void Put(Grid grid, Control c, int col) { Grid.SetColumn(c, col); grid.Children.Add(c); }

    private static TextBlock Heading(string text, double top)
        => new() { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Opacity = 0.55, Margin = new Thickness(0, top, 0, 6) };

    private static StackPanel Row(double top, params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, top, 0, 0) };
        foreach (var c in children) row.Children.Add(c);
        return row;
    }

    private Button Btn(string text, string tip, Func<Task> click)
    {
        var b = new Button { Content = text, FontSize = 12, Padding = new Thickness(10, 4) };
        ToolTip.SetTip(b, tip);
        b.Click += async (_, _) =>
        {
            try { await click(); }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { Say(e.Message, error: true); }
        };
        return b;
    }
}

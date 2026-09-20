using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// One row of the Settings ▸ Technology list. Rebuilt wholesale on every change rather than mutated,
/// so nothing here raises change notifications: the list is small, and a row model that could go
/// stale against the folder it describes is the defect this avoids outright.
/// </summary>
public sealed class TechnologyRowModel
{
    public required TechnologyCatalogEntry Entry { get; init; }
    public required bool                   IsDefault { get; init; }

    public string Id   => Entry.Id;
    public string Name => Entry.Name;

    /// <summary>Where it came from, then its id — the id second because the NAME is what a user picks
    /// by and the id is what they need only when something refers to it.</summary>
    public string Subtitle => (Entry.IsUserInstalled ? "Installed" : "Shipped") + "  ·  " + Entry.Id;

    public bool IsUserInstalled => Entry.IsUserInstalled;

    public override string ToString() => Name;
}

/// <summary>
/// Settings ▸ Technology: the technologies a new workspace can be created from, and which one the
/// New Workspace picker opens pre-selected on.
///
/// <para><b>This view owns no rule.</b> What is offered, what an id collision means, what may be
/// removed and what the default falls back to all live in
/// <see cref="CircuitRF.Design.Layout.TechnologyCatalog"/> below the firewall — because
/// <c>circuitrf new workspace</c> has to reach the same answers with no display attached, and a rule
/// that lived in this file would be a rule the headless verb did not have. What is here is the list,
/// the file picker, the confirmation and the reporting.</para>
///
/// <para><b>A separate view rather than a block of <c>SettingsView.axaml</c></b>, following the
/// arrangement <c>RevisionControlSettingsView</c> and <c>UpdateSettingsView</c> already use: the tab
/// is the only host today, and the dialog file stays about which tabs exist rather than about what is
/// on them.</para>
/// </summary>
public partial class TechnologySettingsView : UserControl
{
    public TechnologySettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Load();
    }

    /// <summary>
    /// Rebuilds the list from the catalog and re-selects what was selected before, BY ID.
    ///
    /// <para>By id and not by index: adding or removing shifts every row after it, and re-selecting
    /// an index would quietly move the selection onto a different technology just as the user is
    /// about to press Remove.</para>
    /// </summary>
    public void Load()
    {
        string? keep = (TechList.SelectedItem as TechnologyRowModel)?.Id;

        string defaultId = TechnologyCatalog.DefaultId;
        var rows = TechnologyCatalog.All
            .Select(e => new TechnologyRowModel
            {
                Entry     = e,
                IsDefault = string.Equals(e.Id, defaultId, StringComparison.OrdinalIgnoreCase),
            })
            .ToList();

        TechList.ItemsSource = rows;
        TechList.SelectedItem =
            rows.FirstOrDefault(r => string.Equals(r.Id, keep, StringComparison.OrdinalIgnoreCase))
            ?? rows.FirstOrDefault(r => r.IsDefault)
            ?? rows.FirstOrDefault();

        ShowDetail();
    }

    // ── The right-hand pane ──────────────────────────────────────────────────

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => ShowDetail();

    private void ShowDetail()
    {
        var row = TechList.SelectedItem as TechnologyRowModel;

        RemoveButton.IsEnabled  = row is { IsUserInstalled: true };
        DefaultButton.IsEnabled = row is { IsDefault: false };

        if (row is null)
        {
            DetailName.Text      = "No technology selected.";
            DetailGrid.IsVisible = false;
            Notice(null);
            return;
        }

        DetailGrid.IsVisible = true;
        DetailName.Text      = row.Name;
        DetailId.Text        = row.Id;
        DetailSource.Text    = row.Entry.IsUserInstalled
            ? row.Entry.FilePath ?? TechnologyCatalog.UserDirectory
            : "Shipped with circuitRF";

        // A user file can be deleted or corrupted between the listing and this read, so the summary
        // is the one place the tab has to survive not being able to read what it just listed.
        TechnologySummary summary;
        try   { summary = TechnologySummary.Of(TechnologyCatalog.Load(row.Entry)); }
        catch (Exception ex)
        {
            DetailLayers.Text = DetailStackup.Text = DetailThickness.Text =
                DetailRules.Text = DetailUnits.Text = "—";
            Notice($"This technology could not be read: {ex.Message}");
            return;
        }

        DetailLayers.Text = summary.DrawingLayers.ToString();

        var stack = new List<string>();
        if (summary.Conductors  > 0) stack.Add(Count(summary.Conductors,  "conductor"));
        if (summary.Dielectrics > 0) stack.Add(Count(summary.Dielectrics, "dielectric"));
        if (summary.Vias        > 0) stack.Add(Count(summary.Vias,        "via"));
        DetailStackup.Text = stack.Count == 0 ? "none" : string.Join(", ", stack);

        // Its own row: what the stack is MADE of and what it MEASURES are different questions, and a
        // technology can answer the first and not the second — an entry with no thickness authored is
        // a real, diagnosable state, so it reads as unstated rather than as zero.
        DetailThickness.Text = summary.ThicknessText is { } t ? t + " overall" : "—";

        // The label already says "Design rules:", so the number says it once.
        DetailRules.Text = summary.DrcRules == 0 ? "none" : summary.DrcRules.ToString();
        DetailUnits.Text = LayoutUnits.Suffix(summary.DisplayUnit);

        // The two states worth saying out loud, and nothing else. A row that is simply one of several
        // offered technologies needs no sentence.
        Notice(row.IsDefault
            ? "New workspaces open on this one. Every new workspace can still choose another, or None."
            : null);
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private void Notice(string? text)
    {
        DetailNotice.Text      = text ?? "";
        DetailNotice.IsVisible = text is { Length: > 0 };
    }

    private void Status(string? text)
    {
        StatusText.Text      = text ?? "";
        StatusText.IsVisible = text is { Length: > 0 };
    }

    // ── Add ──────────────────────────────────────────────────────────────────

    private async void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Add a technology",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("circuitRF Technology") { Patterns = ["*.ctech"] },
            ],
        });
        if (files.Count == 0) return;

        // Several at once, each reported on its own terms: picking four and being told only about the
        // first failure would leave three unexplained. The refusals come from the catalog, which is
        // where the rule about a colliding id lives.
        var added    = new List<string>();
        var refusals = new List<string>();

        foreach (var file in files)
        {
            var result = TechnologyCatalog.Install(file.Path.LocalPath);
            if (result.Entry is { } entry) added.Add(entry.Name);
            else                           refusals.Add(result.Refusal ?? "It could not be added.");
        }

        Load();
        if (added.Count > 0)
            TechList.SelectedItem = (TechList.ItemsSource as IEnumerable<TechnologyRowModel>)?
                .FirstOrDefault(r => r.Name == added[^1]) ?? TechList.SelectedItem;

        Status(string.Join("  ", new[]
        {
            added.Count    > 0 ? $"Added {string.Join(", ", added)}." : null,
            refusals.Count > 0 ? string.Join("  ", refusals)          : null,
        }.Where(s => s is { Length: > 0 })));
    }

    // ── Remove ───────────────────────────────────────────────────────────────

    private async void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (TechList.SelectedItem is not TechnologyRowModel row) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        // It asks first because it DELETES a file the user put there, and circuitRF is not where that
        // file came from — the copy here may be the only one left. What it does not do is put the
        // question to somebody who is about to lose a design: every workspace made with this
        // technology holds its own copy, and the dialog says so rather than leaving it to be feared.
        if (await new RemoveTechnologyDialog(row.Name, row.Entry.FilePath!, row.IsDefault)
                .ShowDialog<bool>(owner) is not true)
        {
            Status("Nothing was removed.");
            return;
        }

        if (TechnologyCatalog.Uninstall(row.Id) is { } refusal) { Status(refusal); return; }

        // The default preference is deliberately NOT cleared. TechnologyCatalog falls back on its own
        // when the id names nothing, and leaving it means re-adding the same file restores the choice
        // rather than having silently forgotten it.
        Load();
        Status($"Removed {row.Name}. Workspaces already created with it are unaffected — each holds "
             + "its own copy.");
    }

    // ── Default ──────────────────────────────────────────────────────────────

    private void OnSetDefaultClick(object? sender, RoutedEventArgs e)
    {
        if (TechList.SelectedItem is not TechnologyRowModel row) return;

        // Through AppPreferencesIo, which owns the one in-process copy of preferences.json and writes
        // it whole; TechnologyCatalog only READS the key back. Two writers to that file is how one
        // window's edit disappears.
        AppPreferencesIo.Update(p => p.DefaultTechnologyId = row.Id);

        Load();
        Status($"New workspaces will open on {row.Name}.");
    }

    // ── The folder ───────────────────────────────────────────────────────────

    private void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        string dir = TechnologyCatalog.UserDirectory;
        try
        {
            // Created on demand rather than at startup: a machine where nobody has added a technology
            // has no empty folder to explain. Opening it IS the request for it to exist — this button
            // is how a .ctech gets dropped in by hand.
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status($"{dir} could not be created: {ex.Message}");
            return;
        }

        FileReveal.Reveal(dir, ex => Status(ex.Message));
        Status($"Your technologies are in {dir}. A .ctech copied there is offered the next time this "
             + "list is opened.");
    }
}

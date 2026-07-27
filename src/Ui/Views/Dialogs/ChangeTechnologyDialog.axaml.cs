using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>Result of the Change Technology picker (docs/sonnet-briefs/brief-L1g-technology-retarget.md
/// §4). <see cref="AbsoluteTechPath"/> null means "(Workspace default)" — writes
/// <c>LayoutView.TechRef = null</c> and re-resolves through L0c's resolution order; a non-null path
/// (a <c>tech/</c> entry or a browsed file) becomes an explicit <c>TechRef</c>.</summary>
public sealed record ChangeTechnologyResult(string? AbsoluteTechPath, bool AdoptUnits);

/// <summary>
/// "Change Technology…" picker — the entry point for Gap 1 (docs/sonnet-briefs/brief-L1g-technology-retarget.md
/// §0: "there is no UI to change a layout's technology"). Offers <b>(Workspace default)</b> as an
/// explicit, always-selectable option (§4 — L0c's convention, not something only reachable by never
/// having chosen), every <c>.ctech</c> in the workspace's <c>tech/</c> folder, and <b>Browse…</b> for
/// one outside the workspace. The unit-adoption checkbox defaults OFF — silently overwriting a user's
/// working unit mid-retarget would be exactly the kind of helpfulness that erodes trust (§4 point 3).
/// </summary>
public partial class ChangeTechnologyDialog : Window
{
    private sealed record TechChoiceItem(string Label, string? AbsolutePath)
    {
        public override string ToString() => Label;
    }

    public ChangeTechnologyDialog() => InitializeComponent();

    public ChangeTechnologyDialog(LayoutEditorViewModel vm) : this()
    {
        CurrentText.Text = $"Current: {vm.TechSummaryText}";

        var items = new List<TechChoiceItem> { new("(Workspace default)", null) };
        if (vm.WorkspaceTechDir is { } techDir && Directory.Exists(techDir))
        {
            foreach (var path in Directory.GetFiles(techDir, "*.ctech").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                items.Add(new TechChoiceItem(TryReadTechName(path) ?? Path.GetFileNameWithoutExtension(path), path));
        }

        ChoiceList.ItemsSource = items;
        ChoiceList.SelectedIndex = 0;

        BrowseButton.Click += async (_, _) => await OnBrowseAsync();
        OkButton.Click      += (_, _) => Close(BuildResult());
        CancelButton.Click  += (_, _) => Close(null);
    }

    private static string? TryReadTechName(string path)
    {
        try
        {
            var tech = TechPersistence.LoadFromFile(path);
            return tech.Name is { Length: > 0 } n ? n : null;
        }
        catch
        {
            return null; // corrupt/unreadable — fall back to the filename stem, never throw here
        }
    }

    private async Task OnBrowseAsync()
    {
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a Technology",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("circuitRF Technology") { Patterns = ["*.ctech"] },
                new FilePickerFileType("All Files")             { Patterns = ["*.*"] },
            ],
        });
        if (result.Count == 0) return;

        Close(new ChangeTechnologyResult(result[0].Path.LocalPath, AdoptUnitsCheck.IsChecked == true));
    }

    private ChangeTechnologyResult BuildResult() =>
        new((ChoiceList.SelectedItem as TechChoiceItem)?.AbsolutePath, AdoptUnitsCheck.IsChecked == true);
}

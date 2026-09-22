using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Diagnostics;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// brief-lvs-12-gui.md R-lvs12-1e: "Export offers to run it, as DRC's export gate does — a checkbox,
/// default <b>off</b> for LVS rather than on, because LVS needs a schematic and an artwork-only
/// export is a legitimate thing to do. Off with the box visible is not the same as absent."
///
/// <para><b>It appears only when the comparison found something</b> — <c>DrcExportGateDialog</c>'s
/// own rule, and the reason is the same: a modal that says "the artwork implements the drawing"
/// before every export is the dialog people learn to dismiss unread, which would then get dismissed
/// on the export that mattered.</para>
/// </summary>
public partial class LvsExportGateDialog : Window
{
    /// <summary>How many findings to list before summarising the rest — a wall of lines answers no
    /// question the counts above it have not already answered.</summary>
    private const int MaxListed = 12;

    public LvsExportGateDialog() => InitializeComponent();

    public LvsExportGateDialog(LvsRunResult result, string format) : this()
    {
        HeadlineLabel.Text =
            $"{result.ErrorCount} error(s) and {result.WarningCount} warning(s) before exporting {format}.";

        // Never implicit, here for the same reason it is never implicit in the panel: a comparison
        // made against the wrong process's stackup reads exactly like one made against the right one.
        TechnologyLabel.Text = result.TechnologyName is { Length: > 0 } n
            ? $"Read against \"{n}\" — {result.Counts.Describe()}" +
              (result.WaivedCount > 0 ? $", {result.WaivedCount} waived." : ".")
            : "No technology resolved.";

        var outstanding = result.Findings.Where(f => !f.Waived && f.Severity > DiagnosticSeverity.Info).ToList();
        var lines = new List<string>(outstanding.Take(MaxListed).Select(Describe));
        if (outstanding.Count > MaxListed)
            lines.Add($"… and {outstanding.Count - MaxListed} more. See the LVS panel for the full list.");
        FindingList.ItemsSource = lines;

        KeepCheckingCheck.IsChecked = AppPreferencesIo.Load().CheckLvsOnExport ?? false;
    }

    private static string Describe(LvsFinding f)
    {
        string severity = f.Severity == DiagnosticSeverity.Error ? "Error" : "Warning";
        string objects  = f.Objects.Count > 0 ? $"  ({string.Join(", ", f.Objects)})" : "";
        return $"• {severity} — {f.Render()}{objects}";
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Commit(false);
    private void OnExportClick(object? sender, RoutedEventArgs e) => Commit(true);

    private void Commit(bool proceed)
    {
        // Persisted whichever button is pressed — DrcExportGateDialog's own rule: unticking the box
        // then cancelling is still the user saying "stop comparing".
        AppPreferencesIo.Update(p => p.CheckLvsOnExport = KeepCheckingCheck.IsChecked == true);
        Close(proceed);
    }
}

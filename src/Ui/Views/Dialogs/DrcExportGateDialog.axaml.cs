using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Drc;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// R16d: "Export offers to run DRC first. Not mandatory, not silent — a checkbox in the export
/// dialog, default on, with violations shown before the file is written."
///
/// <para><b>This appears ONLY when the check found something.</b> A modal that says "no violations"
/// before every export is exactly the dialog people learn to dismiss unread, which would then also
/// get dismissed on the export that mattered — the same reasoning the GDSII/Gerber fidelity dialogs
/// already apply when there is nothing to report. A clean design exports with no interruption at
/// all.</para>
///
/// <para>The checkbox therefore lives here (where the setting is in front of you at the moment it is
/// costing you something) and in Settings ▸ General (where a user whose designs are clean can still
/// find it). Unticking it here turns the pre-export check off for good, until it is turned back on.</para>
/// </summary>
public partial class DrcExportGateDialog : Window
{
    /// <summary>How many violations to list before summarising the rest — a wall of a thousand lines
    /// answers no question the counts above it have not already answered.</summary>
    private const int MaxListed = 12;

    public DrcExportGateDialog() => InitializeComponent();

    public DrcExportGateDialog(DrcRunResult result, string format) : this()
    {
        HeadlineLabel.Text =
            $"{result.ErrorCount} error(s) and {result.WarningCount} warning(s) before exporting {format}.";

        TechnologyLabel.Text = result.TechnologyName is { Length: > 0 } n
            ? $"Checked against \"{n}\" — {result.RulesEvaluated} rule(s) over {result.ShapesChecked:N0} shape(s)" +
              (result.WaivedCount > 0 ? $", {result.WaivedCount} waived." : ".")
            : "No technology resolved.";

        var outstanding = result.Violations.Where(v => !v.Waived).ToList();
        var lines = new List<string>(outstanding.Take(MaxListed).Select(Describe));
        if (outstanding.Count > MaxListed)
            lines.Add($"… and {outstanding.Count - MaxListed} more. See the DRC panel for the full list.");
        ViolationList.ItemsSource = lines;

        KeepCheckingCheck.IsChecked = AppPreferencesIo.Load().CheckDrcOnExport ?? true;
    }

    private static string Describe(DrcViolation v)
    {
        string severity = v.Severity == DrcSeverity.Error ? "Error" : "Warning";
        string kind     = v.Kind == DrcRuleKind.MinWidth ? "minimum width" : "minimum spacing";
        return $"• {severity} — {v.RuleName} ({kind}) on layer {v.Layer.Layer}/{v.Layer.Datatype}";
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Commit(false);
    private void OnExportClick(object? sender, RoutedEventArgs e) => Commit(true);

    private void Commit(bool proceed)
    {
        // Persisted whichever button is pressed: unticking the box then cancelling is still the user
        // saying "stop checking", and losing that on Cancel would read as the setting not working.
        AppPreferencesIo.Update(p => p.CheckDrcOnExport = KeepCheckingCheck.IsChecked == true);
        Close(proceed);
    }
}

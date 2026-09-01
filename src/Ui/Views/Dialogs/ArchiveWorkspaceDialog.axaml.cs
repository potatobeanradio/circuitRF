using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CircuitRF.Ui.Archive;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// "Prompt the user in a dialog if they want to copy any of the Referenced Kits into the workspace"
/// (owner, 2026-08-15) — plus the referenced files and the results, each with its size so the choice
/// can be made on the numbers.
///
/// <para>The dialog decides nothing itself: it presents <see cref="WorkspaceArchivePlan"/> and writes
/// the ticks back onto it. What is offered, what each default is, and what is skipped outright all
/// live in <see cref="WorkspaceArchiveScanner"/>, where they are testable without a window.</para>
/// </summary>
public partial class ArchiveWorkspaceDialog : Window
{
    private readonly WorkspaceArchivePlan? _plan;
    private ObservableCollection<ArchiveTreeNode> _roots = [];

    public ArchiveWorkspaceDialog() => InitializeComponent();

    public ArchiveWorkspaceDialog(WorkspaceArchivePlan plan) : this()
    {
        _plan = plan;

        HeaderText.Text = $"Archive “{Path.GetFileName(plan.WorkspaceDir.TrimEnd(Path.DirectorySeparatorChar))}”";

        var roots = _roots = BuildRoots(plan);
        Tree.ItemsSource = roots;

        UpdateTotal();

        // The tick state lives on the plan's options, which the tree writes through to — so polling
        // the plan on a slow tick keeps the total honest without every node raising.
        //
        // Started from Opened, not from here: at construction the window is not visible yet, so a
        // timer whose keep-running answer is `IsVisible` would stop on its very first tick.
        Opened += (_, _) =>
        {
            var alive = true;
            Closed += (_, _) => alive = false;
            DispatcherTimer.Run(() => { UpdateTotal(); return alive; }, TimeSpan.FromMilliseconds(250));
        };

        MeasureKitsInBackground(roots, plan);
    }

    /// <summary>The three branches, in the order the owner described them.</summary>
    private static ObservableCollection<ArchiveTreeNode> BuildRoots(WorkspaceArchivePlan plan)
    {
        var roots = new ObservableCollection<ArchiveTreeNode>();

        if (plan.Kits.Any())
            roots.Add(ArchiveTreeNode.Group(
                $"Referenced Kits ({plan.Kits.Count()})",
                () => plan.Kits.Select(ArchiveTreeNode.Leaf)));

        if (plan.ExternalFiles.Any())
            roots.Add(ArchiveTreeNode.Group(
                $"Referenced Files ({plan.ExternalFiles.Count()})",
                () => plan.ExternalFiles.Select(ArchiveTreeNode.Leaf)));

        if (plan.Results.Any())
            roots.Add(ArchiveTreeNode.Group(
                $"Results ({plan.Results.Count()})",
                () => WorkspaceArchiveScanner.ResultGroupOrder
                        .Where(g => plan.Results.Any(r => r.Group == g))
                        .Select(g => ArchiveTreeNode.Group(
                            $"{g} ({plan.Results.Count(r => r.Group == g)})",
                            () => plan.Results.Where(r => r.Group == g).Select(ArchiveTreeNode.Leaf)))));

        // Opening on the branches already reflects the defaults, so the user sees what they are
        // agreeing to rather than three closed headings.
        foreach (var root in roots) root.IsExpanded = true;

        return roots;
    }

    /// <summary>
    /// Fills in each kit folder's size behind the dialog. A vendor kit is routinely tens of thousands
    /// of files, so measuring one on the UI thread would hold the window shut for seconds — and the
    /// number is exactly what the user needs in order to decide, so it cannot simply be omitted.
    /// </summary>
    private static void MeasureKitsInBackground(IEnumerable<ArchiveTreeNode> roots, WorkspaceArchivePlan plan)
    {
        var pending = plan.Kits.Where(k => k.SizeBytes < 0 && k.IsDirectory).ToList();
        if (pending.Count == 0) return;

        _ = Task.Run(() =>
        {
            foreach (var kit in pending)
            {
                long bytes;
                bool complete;
                try { bytes = WorkspaceArchiveScanner.MeasureDirectory(kit.SourcePath, out complete); }
                catch { continue; }

                kit.SizeBytes = bytes;
                var text = WorkspaceArchivePlan.FormatSize(bytes) + (complete ? "" : "+");

                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var node in roots.SelectMany(r => r.SelfAndDescendants()))
                        if (ReferenceEquals(node.Option, kit)) node.SizeText = text;
                });
            }
        });
    }

    private void UpdateTotal()
    {
        if (_plan is null) return;

        var ticked = _plan.Options.Count(o => o.Selected);
        TotalText.Text =
            $"Approximate uncompressed size: {WorkspaceArchivePlan.FormatSize(_plan.SelectedBytes)}  " +
            $"({WorkspaceArchivePlan.FormatSize(_plan.AlwaysIncludedBytes)} of workspace files, {ticked} item(s) ticked)";
    }

    /// <summary>
    /// Ticks or unticks everything optional.
    ///
    /// <para>The plan's options are set FIRST and the tree second. A group that has never been
    /// expanded is standing in for rows it has not built yet — writing through the node alone would
    /// reach only the rows on screen, which is the shape this dialog's laziness makes easy to get
    /// wrong.</para>
    /// </summary>
    private void SetAll(bool included)
    {
        if (_plan is null) return;

        foreach (var option in _plan.Options) option.Selected = included;
        foreach (var root in _roots) root.IsChecked = included;

        UpdateTotal();
    }

    private void OnIncludeAllClick(object? sender, RoutedEventArgs e)  => SetAll(true);
    private void OnIncludeNoneClick(object? sender, RoutedEventArgs e) => SetAll(false);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
    private void OnArchiveClick(object? sender, RoutedEventArgs e) => Close(true);
}

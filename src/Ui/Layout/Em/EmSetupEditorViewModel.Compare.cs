// brief-emi-6 R-emi6-2 — the Solver picker's Compare… row: one checkbox per 3D solver and a Reference over the checked ones.
//
// A solver that is not installed, or that refuses this setup (its descriptor's Refusal: an eigenmode problem on openEMS), is
// DISABLED in the picker and in the checkbox list, its tooltip saying why. No note text under the controls: tooltips only.
// Installation is read when the panel opens and when Settings ▸ Solvers closes (Em3dSolverInstallState); never polled.
//
// The file holds a comparison of at least two solvers (Em3dSolverChoice.Compare), so unchecking the second-last one is not
// an edit: the box stays checked. Fewer than two checked solvers that can RUN here disables Run, its tooltip saying why.

using System.Collections.ObjectModel;
using CircuitRF.Design.Em3d;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>
/// brief-emi-6 R-emi6-2 — which 3D solvers are installed on this machine, as the EM Setup panel shows them. Read off the UI
/// thread (<see cref="RefreshAsync"/>, each program's version probe) and remembered; null until the first read, when every
/// solver is shown as installed rather than disabled on a guess.
/// </summary>
public static class Em3dSolverInstallState
{
    /// <summary>The last read: each solver, installed or not. Null before the first.</summary>
    public static IReadOnlyDictionary<Em3dSolverId, bool>? Known { get; private set; }

    /// <summary>How a read is made. A seam, so a test decides what is installed without a machine's programs.</summary>
    public static Func<IReadOnlyDictionary<Em3dSolverId, bool>> Probe { get; set; } = Em3dSolvers.Installed;

    /// <summary>Raised on the caller's thread when a read has finished.</summary>
    public static event Action? Changed;

    /// <summary>Reads again, off the calling thread, and raises <see cref="Changed"/> back on it.</summary>
    public static async Task RefreshAsync()
    {
        var read = await Task.Run(() => Probe());
        Known = read;
        Changed?.Invoke();
    }

    /// <summary>Sets <see cref="Known"/> directly and raises <see cref="Changed"/> — a test's read.</summary>
    public static void Set(IReadOnlyDictionary<Em3dSolverId, bool>? known)
    {
        Known = known;
        Changed?.Invoke();
    }
}

/// <summary>One checkbox of a comparison's solver list.</summary>
public sealed partial class Em3dCompareSolverRow(Em3dSolverDescriptor solver, Action<Em3dCompareSolverRow> toggled) : ObservableObject
{
    public Em3dSolverDescriptor Solver { get; } = solver;

    public string Label => Solver.DisplayName;

    [ObservableProperty] private bool _isChecked;

    [ObservableProperty] private bool _isEnabled = true;

    /// <summary>The disabled box's tooltip: not installed, or the solver's own refusal of this setup.</summary>
    [ObservableProperty] private string? _disabledReason;

    internal bool Syncing { get; set; }

    partial void OnIsCheckedChanged(bool value)
    {
        if (!Syncing) toggled(this);
    }
}

public sealed partial class EmSetupEditorViewModel
{
    /// <summary>The tooltip of a solver this machine does not have.</summary>
    public const string NotInstalledTip = "Not installed. Install it from Settings ▸ Solvers.";

    /// <summary>This panel's own picker rows — copies of <see cref="Solver3DChoices"/>, so each panel says what can run on
    /// its setup. A stable list: a ComboBox's selection is resolved against the list it already has.</summary>
    private readonly IReadOnlyList<Em3dSolverPickerRow> _solverRows = [.. Solver3DChoices.Select(r => r.Copy())];

    private IReadOnlyList<Em3dCompareSolverRow>? _compareSolverRows;

    /// <summary>One checkbox per 3D solver, in the registry's order.</summary>
    public IReadOnlyList<Em3dCompareSolverRow> CompareSolverRows
        => _compareSolverRows ??= [.. Em3dSolvers.All.Select(d => new Em3dCompareSolverRow(d, OnCompareSolverToggled))];

    /// <summary>The Reference combo's items: the checked solvers. One collection for the panel's life.</summary>
    public ObservableCollection<Em3dSolverDescriptor> CompareReferenceChoices { get; } = [];

    [ObservableProperty] private Em3dSolverDescriptor? _compareReference;

    /// <summary>True when the setup compares solvers: the checkboxes and the Reference combo are shown.</summary>
    public bool IsCompareSetup => !IsThermalSetup && Working.Solver3D.IsCompare;

    /// <summary>
    /// Why Run is off for a comparison: fewer than two of its solvers can run here. Null when two or more can, and for any
    /// setup that is not a comparison (a single solver's own state is its run's to report, as before).
    /// </summary>
    public string? CompareBlockingReason => IsCompareSetup ? CompareBlockingReasonFor(Working, _installed ?? Em3dSolverInstallState.Known) : null;

    /// <summary>
    /// <see cref="CompareBlockingReason"/> for any setup — the 3D editor's Run asks it of its active setup. Null unless
    /// <paramref name="setup"/> compares solvers and fewer than two of them can run here: not installed by
    /// <paramref name="installed"/> (null: not yet read, so every solver counts as installed), or refused by its descriptor.
    /// </summary>
    public static string? CompareBlockingReasonFor(EmSetup setup, IReadOnlyDictionary<Em3dSolverId, bool>? installed)
    {
        if (setup.IsThermal || !setup.Solver3D.IsCompare) return null;
        var chosen = Em3dSolvers.Of(setup.Solver3D);
        var cannot = chosen.Select(d => (d, Why: SolverUnavailable(d, setup, installed))).Where(x => x.Why is not null).ToList();
        if (chosen.Count - cannot.Count >= 2) return null;
        return "A comparison runs two or more solvers, and " +
               (cannot.Count == chosen.Count ? "none of the checked ones" : $"only {chosen.Count - cannot.Count} of the checked ones") +
               " can run here: " + string.Join(" ", cannot.Select(x => $"{x.d.DisplayName}: {x.Why}"));
    }

    /// <summary>Why <paramref name="solver"/> cannot run this setup here, or null: not installed, or its own refusal's first
    /// sentence.</summary>
    public string? SolverUnavailable(Em3dSolverDescriptor solver)
        => SolverUnavailable(solver, Working, _installed ?? Em3dSolverInstallState.Known);

    private static string? SolverUnavailable(Em3dSolverDescriptor solver, EmSetup setup, IReadOnlyDictionary<Em3dSolverId, bool>? installed)
    {
        if (installed is { } known && known.TryGetValue(solver.Id, out bool present) && !present)
            return NotInstalledTip;
        return solver.Refusal(setup) is { } refusal ? FirstSentence(refusal) : null;
    }

    /// <summary>This panel's own installation read, when one was handed to it; null reads <see cref="Em3dSolverInstallState"/>.</summary>
    private IReadOnlyDictionary<Em3dSolverId, bool>? _installed;

    /// <summary>Applies an installation read — <paramref name="installed"/>, or the latest <see cref="Em3dSolverInstallState"/>
    /// one — and this setup's refusals to the picker rows and the checkboxes.</summary>
    public void UpdateSolverAvailability(IReadOnlyDictionary<Em3dSolverId, bool>? installed = null)
    {
        if (installed is not null) _installed = installed;
        foreach (var row in _solverRows.Where(r => !r.IsCompare && !r.Value.IsNone))
        {
            row.DisabledReason = SolverUnavailable(Em3dSolvers.Get(row.Value.Reference));
            row.IsEnabled = row.DisabledReason is null;
        }
        foreach (var row in CompareSolverRows)
        {
            row.DisabledReason = SolverUnavailable(row.Solver);
            row.IsEnabled = row.DisabledReason is null;
        }
        OnPropertyChanged(nameof(CompareBlockingReason));
        OnPropertyChanged(nameof(BlockingReason));
        OnPropertyChanged(nameof(SimulateTip));
        OnPropertyChanged(nameof(CanRun));
        SimulateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The set Compare… starts on when the setup is not yet a comparison: the format's Both (Palace and openEMS) while both
    /// can run here, else every solver that can, when two or more can; Both otherwise, and Run says why it is off.
    /// </summary>
    private Em3dSolverChoice CompareStart()
    {
        var runnable = Em3dSolvers.All.Where(d => SolverUnavailable(d) is null).Select(d => d.Id).ToList();
        if (Em3dSolvers.Both.Solvers.All(runnable.Contains) || runnable.Count < 2) return Em3dSolvers.Both;
        return Em3dSolverChoice.Compare(runnable, Em3dSolvers.DefaultReference(runnable));
    }

    /// <summary>The checkboxes and the Reference combo, from the file.</summary>
    private void SyncCompareFields()
    {
        bool was = _suppressCommit;
        _suppressCommit = true;
        try
        {
            var choice = Working.Solver3D;
            foreach (var row in CompareSolverRows)
            {
                row.Syncing = true;
                row.IsChecked = choice.IsCompare && choice.Runs(row.Solver.Id);
                row.Syncing = false;
            }
            var chosen = choice.IsCompare ? Em3dSolvers.Of(choice) : [];
            if (!CompareReferenceChoices.SequenceEqual(chosen))
            {
                CompareReferenceChoices.Clear();
                foreach (var d in chosen) CompareReferenceChoices.Add(d);
            }
            // Assigned after its items, in this one path (src/Ui/CLAUDE.md's ComboBox rule).
            CompareReference = choice.IsCompare ? Em3dSolvers.Get(choice.Reference) : null;
        }
        finally { _suppressCommit = was; }
        UpdateSolverAvailability();
        OnPropertyChanged(nameof(IsCompareSetup));
    }

    private void OnCompareSolverToggled(Em3dCompareSolverRow row)
    {
        if (_suppressCommit || !Working.Solver3D.IsCompare) return;
        var set = Em3dSolvers.InOrder(CompareSolverRows.Where(r => r.IsChecked).Select(r => r.Solver.Id));
        if (set.Count < 2)
        {
            // A comparison names two or more: the box stays checked.
            SyncCompareFields();
            return;
        }
        var reference = set.Contains(Working.Solver3D.Reference) ? Working.Solver3D.Reference : Em3dSolvers.DefaultReference([.. set]);
        CommitSolvers(Em3dSolverChoice.Compare(set, reference), row.IsChecked ? $"Compare with {row.Label}" : $"Stop comparing with {row.Label}");
    }

    partial void OnCompareReferenceChanged(Em3dSolverDescriptor? value)
    {
        if (_suppressCommit || value is null || !Working.Solver3D.IsCompare || value.Id == Working.Solver3D.Reference) return;
        CommitSolvers(Em3dSolverChoice.Compare(Working.Solver3D.Solvers, value.Id), "Change comparison reference");
    }

    private void CommitSolvers(Em3dSolverChoice choice, string description)
    {
        var before = SnapshotJson();
        Working.Solver3D = choice;
        CommitEdit(before, description);
        DropPlaneWaveIfSolverCannot();   // brief-hzn-12 — comparing with Palace, which has no plane-wave source
        SyncCompareFields();
        RaiseSolverDependents();
        Refresh();
    }

    /// <summary>A tooltip's worth of <paramref name="text"/>: its first sentence.</summary>
    private static string FirstSentence(string text)
    {
        int end = text.IndexOf(". ", StringComparison.Ordinal);
        return end > 0 ? text[..(end + 1)] : text;
    }
}

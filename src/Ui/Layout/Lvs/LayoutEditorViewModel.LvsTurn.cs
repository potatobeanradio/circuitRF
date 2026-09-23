// Parts placed end for end, from the LVS panel — brief-lvs-16-parts-placed-end-for-end.md §4.
//
// ── ONE GESTURE FOR ALL OF THEM, BUILT ON THE LIST AND NEVER ON THE FINDINGS ─────────────────
//
// The findings are capped at twenty per id, and a real board had thirty of these. The group is
// built from `LvsRunResult.Turned`, which is uncapped by construction, so the count on the button is
// the number of parts and not the number of lines that fitted (R-lvs16-2d, R-lvs16-3a).
//
// ── THE SAME EDIT railRF MAKES ───────────────────────────────────────────────────────────────
//
// `TurnedParts.Edits` builds the edit list both windows apply, through `ReplaceInstances` — one
// undoable entry, dirtying this document like any other edit. A Turn here therefore clears railRF's
// note (its debounced pad read re-reads the layout), and a Turn there leaves nothing for the next
// comparison to report.
//
// ── THE RE-RUN COMPARES THE DOCUMENT IN HAND ─────────────────────────────────────────────────
//
// The panel ordinarily compares what is on disk (this folder's RESOLVED.md §3). A Turn is an unsaved
// edit, so re-reading the file would report every part it had just turned; the re-run hands the open
// model to `LvsRun.Run(cellDir, layout)` instead, which resolves everything else exactly as the
// ordinary run does. That result describes what is on screen, so it is not marked stale.

using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Layout.Lvs;

namespace CircuitRF.Ui.Layout;

public sealed partial class LayoutEditorViewModel
{
    /// <summary>R-lvs16-3a: every part placed end for end, one row each, checked by default.</summary>
    public ObservableCollection<LvsTurnedRow> LvsTurnedParts { get; } = [];

    /// <summary>R-lvs16-3c: every part LVS found reversed — a per-part Turn each, never checked.</summary>
    public ObservableCollection<LvsReversedRow> LvsReversedParts { get; } = [];

    public bool HasLvsTurnedParts => LvsTurnedParts.Count > 0;

    public bool HasLvsReversedParts => LvsReversedParts.Count > 0;

    /// <summary>The group's heading, with the FULL count — never the capped one.</summary>
    public string LvsTurnedHeaderText => LvsTurnedParts.Count switch
    {
        0 => "",
        1 => "1 part is placed end for end. The circuit is the same either way round, but railRF, "
           + "the placement table and cross-probing read its pin 1 backwards.",
        int n => $"{n} parts are placed end for end. The circuit is the same either way round, but "
               + "railRF, the placement table and cross-probing read their pin 1 backwards.",
    };

    /// <summary>The ones the designer left checked.</summary>
    public IReadOnlyList<TurnedPart> SelectedLvsTurnedParts =>
        [.. LvsTurnedParts.Where(r => r.IsSelected).Select(r => r.Part)];

    /// <summary>The button names what it will do — railRF's own words.</summary>
    public string LvsTurnButtonText => SelectedLvsTurnedParts is { Count: > 0 } parts
        ? TurnedParts.ButtonText(parts)
        : "Turn in the layout";

    /// <summary>Why the last Turn did not happen, or empty.</summary>
    [ObservableProperty] private string _lvsTurnProblem = "";

    public bool HasLvsTurnProblem => LvsTurnProblem.Length > 0;

    partial void OnLvsTurnProblemChanged(string value) => OnPropertyChanged(nameof(HasLvsTurnProblem));

    /// <summary>
    /// Refused against a stale result: the lands it would turn about were measured on a layout that
    /// has changed since, and a turn about the wrong midpoint moves the part off its copper.
    /// </summary>
    public bool CanTurnLvsParts => SelectedLvsTurnedParts.Count > 0 && !IsLvsStale;

    /// <summary>Parts the designer unchecked, by designator — kept across a rebuild of the rows
    /// that is not a new comparison (waiving re-marks the result and rebuilds everything).</summary>
    private readonly HashSet<string> _lvsTurnUnchecked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rebuilds both groups from a result — the list, not the findings.</summary>
    private void RebuildLvsTurnRows(LvsRunResult? result)
    {
        LvsTurnedParts.Clear();
        LvsReversedParts.Clear();

        if (result is not null)
        {
            foreach (var part in result.Turned)
                LvsTurnedParts.Add(new LvsTurnedRow(part, this)
                {
                    IsSelected = !_lvsTurnUnchecked.Contains(part.Refdes),
                });

            foreach (var finding in result.Findings.Where(f => f.Id == "lvs.device.reversed"))
                LvsReversedParts.Add(ReversedRowFor(finding));
        }

        NotifyLvsTurnSurface();
    }

    /// <summary>
    /// A reversed part's row: its per-part Turn where a half turn lands it, a sentence where it would
    /// not, and — R-lvs16-3d — a sentence where the part is inside a placed cell, whose instance
    /// index is that cell's and not this layout's.
    /// </summary>
    private LvsReversedRow ReversedRowFor(LvsFinding finding)
    {
        string path = finding.Diagnostic.Arguments.TryGetValue("layoutPath", out object? p)
            ? p?.ToString() ?? "" : "";

        int index = -1;
        for (int i = 0; i < Model.Instances.Count && index < 0; i++)
            if (string.Equals(LayoutDesignFlatten.PathOf(Model.Instances[i], i), path, StringComparison.Ordinal))
                index = i;

        if (index < 0)
            return new LvsReversedRow(path, null,
                $"'{path}' is not placed on this layout — it is inside a placed cell. Turn it in that "
              + "cell's own layout.", this);

        var part = InstanceBaseDir is { Length: > 0 } dir
            ? TurnedParts.LandingHalfTurn(Model, Path.Combine(dir, "_.clay"), Technology, index)
            : null;

        return part is null
            ? new LvsReversedRow(path, null,
                $"A half turn would not put '{path}' back on its own lands, so none is offered.", this)
            : new LvsReversedRow(path, part,
                "Either the part or the copper is the wrong half, and only you know which. Turning it "
              + "is offered, never assumed.", this);
    }

    internal void OnLvsTurnSelectionChanged(LvsTurnedRow row)
    {
        if (row.IsSelected) _lvsTurnUnchecked.Remove(row.Part.Refdes);
        else _lvsTurnUnchecked.Add(row.Part.Refdes);
        NotifyLvsTurnSurface();
    }

    private void NotifyLvsTurnSurface()
    {
        OnPropertyChanged(nameof(HasLvsTurnedParts));
        OnPropertyChanged(nameof(HasLvsReversedParts));
        OnPropertyChanged(nameof(LvsTurnedHeaderText));
        OnPropertyChanged(nameof(SelectedLvsTurnedParts));
        OnPropertyChanged(nameof(LvsTurnButtonText));
        OnPropertyChanged(nameof(CanTurnLvsParts));
        TurnLvsPartsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The checked parts, turned as ONE undoable step — then compared again.</summary>
    [RelayCommand(CanExecute = nameof(CanTurnLvsParts))]
    private void TurnLvsParts() => TurnInLayout(SelectedLvsTurnedParts);

    /// <summary>
    /// Turns <paramref name="parts"/> 180° about their own two lands, as one entry on this
    /// document's undo stack, and re-compares the document in hand.
    /// </summary>
    /// <returns>The new result, or null where nothing was turned — and then
    /// <see cref="LvsTurnProblem"/> says why.</returns>
    public LvsRunResult? TurnInLayout(IReadOnlyList<TurnedPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0) return null;

        if (IsLvsStale)
        {
            LvsTurnProblem = "The layout has changed since this comparison, so the lands it measured "
                           + "may not be where the parts are now. Compare again, then turn them.";
            return null;
        }

        var edits = TurnedParts.Edits(Model, parts, out var stale);
        if (stale is not null)
        {
            LvsTurnProblem = $"{stale.Refdes} is no longer where LVS read it — the layout has changed "
                           + "since. Nothing was turned.";
            return null;
        }

        ReplaceInstances(edits, TurnedParts.EditDescription(parts));
        LvsTurnProblem = "";

        if (CurrentCellDir is not { Length: > 0 } cell) return null;
        var result = LvsRun.Run(cell, Model);
        LvsResult = result;                        // clears IsLvsStale: this IS the document in hand
        _lvsComparedUnsavedDocument = false;
        return result;
    }
}

/// <summary>One part placed end for end, in the panel's group — checked unless the designer
/// unchecks it.</summary>
public sealed partial class LvsTurnedRow : ObservableObject
{
    private readonly LayoutEditorViewModel _owner;

    public LvsTurnedRow(TurnedPart part, LayoutEditorViewModel owner)
    {
        Part = part;
        _owner = owner;
    }

    public TurnedPart Part { get; }

    public string Refdes => Part.Refdes;

    [ObservableProperty] private bool _isSelected = true;

    partial void OnIsSelectedChanged(bool value) => _owner.OnLvsTurnSelectionChanged(this);
}

/// <summary>
/// One part LVS found reversed — R-lvs16-3c: a per-part Turn only, never the bulk button, and only
/// where a half turn lands the part on its own lands.
/// </summary>
public sealed partial class LvsReversedRow : ObservableObject
{
    private readonly LayoutEditorViewModel _owner;

    public LvsReversedRow(string path, TurnedPart? part, string note, LayoutEditorViewModel owner)
    {
        Path = path;
        Part = part;
        Note = note;
        _owner = owner;
    }

    public string Path { get; }

    /// <summary>The half turn, or null where none is offered.</summary>
    public TurnedPart? Part { get; }

    public string Note { get; }

    public bool CanTurn => Part is not null;

    public string ButtonText => $"Turn {Path} in the layout";

    [RelayCommand]
    private void Turn()
    {
        if (Part is { } part) _owner.TurnInLayout([part]);
    }
}

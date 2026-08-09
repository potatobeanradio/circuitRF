using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// One selectable corner in the picker.
///
/// <para>A record rather than a bare string so "leave this axis at the kit's own defaults" is a real,
/// unambiguous entry — a sentinel string would be indistinguishable from a section the kit happened
/// to name the same thing, and getting that wrong silently changes every number the design produces.</para>
/// </summary>
/// <param name="Section">The kit's own section name, or null for "kit default".</param>
/// <param name="Label">What the combo shows.</param>
public sealed record CornerOption(string? Section, string Label);

/// <summary>
/// One corner axis in the Analyses panel: which corner this testbench is set to on it.
///
/// <para>Every commit goes through the schematic's own undo stack, so a corner change is undoable and
/// dirties the document — a corner changes every result, which puts it in the design's history
/// rather than in some view state that persists on its own.</para>
/// </summary>
public sealed partial class CornerAxisRowViewModel : ObservableObject
{
    private readonly Action<string, string?> _commit;
    private bool _suppress;

    public WorkspaceCornerAxis Axis { get; }

    public CornerAxisRowViewModel(
        WorkspaceCornerAxis axis, string? current, Action<string, string?> commit)
    {
        ArgumentNullException.ThrowIfNull(axis);
        Axis    = axis;
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));

        // NAMED, not a bare "(kit default)". Leaving an axis alone applies the kit's own nominal
        // corner — the section it lists first — because a kit states its process constants nowhere
        // else, so binding nothing leaves the model referring to an undefined name. Saying WHICH
        // corner that is keeps the default from being a mystery the user cannot see the effect of.
        Options.Add(new CornerOption(
            null,
            axis.Options.Count > 0 ? $"(kit default: {axis.Options[0]})" : "(kit default)"));
        foreach (var o in axis.Options) Options.Add(new CornerOption(o, o));

        // A recorded corner the kit no longer declares is SHOWN rather than dropped. Silently
        // reverting it to the default would move the design to a corner nobody chose and leave every
        // number plausible; keeping it visible is what makes it repairable.
        if (!string.IsNullOrWhiteSpace(current) &&
            !axis.Options.Any(o => o.Equals(current, StringComparison.OrdinalIgnoreCase)))
        {
            Options.Add(new CornerOption(current, $"{current} — no longer offered"));
            IsStale = true;
        }

        _suppress = true;
        SelectedOption = Options.FirstOrDefault(o =>
            string.Equals(o.Section, current, StringComparison.OrdinalIgnoreCase)) ?? Options[0];
        _suppress = false;
    }

    public ObservableCollection<CornerOption> Options { get; } = [];

    [ObservableProperty] private CornerOption? _selectedOption;

    /// <summary>True when the recorded corner is not one this kit still declares.</summary>
    public bool IsStale { get; }

    public string Label => Axis.Label;

    public string ToolTip =>
        $"{Axis.Kit} · {Axis.AxisId}\n" +
        $"Corners: {string.Join(", ", Axis.Options)}\n" +
        (Axis.Options.Count > 0
            ? $"Left alone, this family uses the kit's own nominal corner: {Axis.Options[0]}."
            : "This family declares no corners.");

    /// <summary>
    /// Re-points the combo at what the model now holds, WITHOUT committing.
    ///
    /// <para>This is what makes Undo/Redo of a corner change visible. Rebuilding the row instead
    /// would work too, but would reset the combo the user may still have open — and would fight the
    /// model change our own commit just raised.</para>
    /// </summary>
    public void SyncFromModel(string? section)
    {
        var match = Options.FirstOrDefault(o =>
            string.Equals(o.Section, section, StringComparison.OrdinalIgnoreCase));

        // A corner set by something other than this row (a paste, a hand-edited .csch) may name a
        // section this axis never offered. Show it rather than silently reverting to the default.
        if (match is null && !string.IsNullOrWhiteSpace(section))
        {
            match = new CornerOption(section, $"{section} — no longer offered");
            Options.Add(match);
        }

        match ??= Options[0];
        if (ReferenceEquals(match, SelectedOption)) return;

        _suppress = true;
        SelectedOption = match;
        _suppress = false;
    }

    partial void OnSelectedOptionChanged(CornerOption? value)
    {
        if (_suppress || value is null) return;
        _commit(Axis.Key, value.Section);
    }
}

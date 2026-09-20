// One row of §2.3 step 2's pick list
// (docs/sonnet-briefs/brief-railrf-19-unreachable-states.md R-rail19-1d).
//
// ── WHY THE LIST IS ROWS AND NOT STRINGS ───────────────────────────────────────────────────────
//
// Because one of them is MARKED and none of them is removed. The reference return has to be
// findable in this list and refused when it is picked, and the brief is explicit that it is not
// filtered out: a user who cannot find GND in a list of every net on the board and is told nothing
// is in exactly the position the dead Run button put him in. A row that says why is an answer; a
// missing row is a second mystery.
//
// So the mark is per-row state, and a list of strings has nowhere to put it.

using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.RailRf;

/// <summary>One net the board netlist named, as the pick list offers it.</summary>
public sealed partial class RailNetRowViewModel(string name) : ObservableObject
{
    /// <summary>The net's name, exactly as the board netlist spells it.</summary>
    public string Name { get; } = name;

    /// <summary>
    /// True where this net is the reference return, MEASURED from the artwork.
    /// </summary>
    /// <remarks>
    /// <b>False until the reference layer has been confirmed</b>, and that is the requirement rather
    /// than an initialisation detail (R-rail19-1d): before the confirmation railRF does not know
    /// which net the return is, and marking a row as though it did is the guess the whole of
    /// R-rail19-1c exists to avoid. <c>PdnRailRegions.ReferenceNetOn</c> is what sets it.
    /// </remarks>
    [ObservableProperty]
    private bool _isReferenceReturn;

    /// <summary>The mark's own words, or empty. Bound rather than composed in the AXAML so the row
    /// and the refusal say the same thing.</summary>
    public string Mark => IsReferenceReturn ? "the reference return" : "";

    partial void OnIsReferenceReturnChanged(bool value) => OnPropertyChanged(nameof(Mark));

    public override string ToString() => Name;
}

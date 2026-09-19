using System;
using CircuitRF.Design.RailRf;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// One row of the Aggressors list (§2.2's last paragraph).
/// </summary>
/// <remarks>
/// <b>An input, not a decoration.</b> A PDN peak matters if something on the board excites it, and
/// this list is the only input that knows whether it does — it drives both the plot markers and §2.4's
/// coincidence check.
///
/// <para><b>The row says where it came from.</b> A frequency pre-filled from the BOM (brief 2's
/// recognition of a crystal or a converter) that nobody checked is exactly the one that will be wrong,
/// so <see cref="OriginText"/> is on the row rather than in a tooltip.</para>
/// </remarks>
public sealed partial class RailAggressorRowViewModel : ObservableObject
{
    private readonly RailSpec _rail;

    /// <summary>
    /// Where this row sits in <see cref="RailSpec.Aggressors"/> — <b>the row's identity</b>.
    /// </summary>
    /// <remarks>
    /// <b>A position, not a value, and the difference is a reported bug</b> (owner, 2026-09-19: "−"
    /// appeared to do nothing). <see cref="RailAggressor"/> is a RECORD, so two rows added with the
    /// button and not yet edited are EQUAL — and a lookup by value then answers 0 for both. Every edit
    /// on the second row landed on the first, and removing the second removed the first, which from
    /// the outside is a button that does nothing or the wrong thing depending on which row you were
    /// looking at. The sources and loads beside it are classes and never had this: they are found by
    /// reference.
    ///
    /// <para>Valid because the rows are rebuilt from the list on every mutation, and checked anyway
    /// before it is used — an index that no longer names this record falls back to the old search
    /// rather than writing to whatever is now at that position.</para>
    /// </remarks>
    public int Index { get; }

    public RailAggressorRowViewModel(RailSpec rail, RailAggressor aggressor, int index)
    {
        _rail = rail;
        _aggressor = aggressor;
        Index = index;
    }

    /// <summary>
    /// Where this row's record actually is, or -1. <see cref="Index"/> where that still names it.
    /// </summary>
    internal int ResolveIndex() =>
        Index >= 0 && Index < _rail.Aggressors.Count && _rail.Aggressors[Index] == _aggressor
            ? Index
            : _rail.Aggressors.IndexOf(_aggressor);

    private RailAggressor _aggressor;

    /// <summary>The record as it stands on the document.</summary>
    public RailAggressor Aggressor => _aggressor;

    /// <summary>Raised after a committed edit.</summary>
    public event EventHandler? Edited;

    /// <summary>What it is — "32 kHz xtal", "converter".</summary>
    public string Name
    {
        get => _aggressor.Name;
        set => Commit(_aggressor with { Name = value?.Trim() ?? "" });
    }

    /// <summary>The fundamental, in the settable column.</summary>
    public string FrequencyEntry
    {
        get => RailValueFormat.FormatWithUnit(_aggressor.FrequencyHz, RailQuantity.Frequency);
        set => Commit(RailValueFormat.TryParse(value, RailQuantity.Frequency, out double hz)
            ? _aggressor with { FrequencyHz = hz }
            : _aggressor);          // refused: Commit still notifies, so the field snaps back
    }

    /// <summary>How many harmonics to draw and check. 1 is the fundamental alone.</summary>
    public int Harmonics
    {
        get => _aggressor.Harmonics;
        set => Commit(_aggressor with { Harmonics = Math.Max(1, value) });
    }

    /// <summary>
    /// The harmonic count in the settable column, as text.
    /// </summary>
    /// <remarks>
    /// <b>A string because the control is the one every other settable value on this window uses</b>
    /// — <c>InlineEditText</c> binds <c>Text</c>, and a row where two of its three values could be
    /// typed and the third could not is the same report this row already carries (owner,
    /// 2026-09-19). A value that does not parse, or one below 1, is REFUSED and the field snaps
    /// back: <see cref="Commit"/> notifies whether or not anything changed, exactly as
    /// <see cref="FrequencyEntry"/> relies on.
    /// </remarks>
    public string HarmonicsEntry
    {
        get => _aggressor.Harmonics.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set => Commit(int.TryParse(value?.Trim(),
                                   System.Globalization.NumberStyles.Integer,
                                   System.Globalization.CultureInfo.InvariantCulture,
                                   out int n) && n >= 1
            ? _aggressor with { Harmonics = n }
            : _aggressor);
    }

    /// <summary>"×8", or empty for the fundamental alone — what the compact row shows.</summary>
    public string HarmonicsText => _aggressor.Harmonics > 1 ? $"×{_aggressor.Harmonics}" : "";

    /// <summary>"from the BOM" on a pre-filled row, empty on a typed one.</summary>
    public string OriginText =>
        _aggressor.Origin == RailAggressorOrigin.Bom ? "from the BOM" : "";

    /// <summary>The whole row as one line — what the compact list shows.</summary>
    public string Summary =>
        $"{Name} · {FrequencyEntry}" + (HarmonicsText.Length > 0 ? $" {HarmonicsText}" : "");

    /// <summary>The row's own refusal, or null.</summary>
    public string? Refusal => _aggressor.Refusal($"Rail '{_rail.Name}'");

    /// <summary>True when this row states something the document refuses.</summary>
    public bool IsFlagged => Refusal is not null;

    private void Commit(RailAggressor next)
    {
        int i = ResolveIndex();
        if (i < 0) return;

        // A REJECTED edit still notifies, and that is not belt-and-braces. An InlineEditText at rest
        // is a TextBlock bound to this property; a typed value the parser refused leaves the control
        // showing text the model does not hold, and with nothing raised it stays on screen looking
        // accepted. Notifying unconditionally snaps it back to what is actually stored.
        bool changed = next != _aggressor;
        if (changed)
        {
            _rail.Aggressors[i] = next;
            _aggressor = next;
        }

        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(FrequencyEntry));
        OnPropertyChanged(nameof(Harmonics));
        OnPropertyChanged(nameof(HarmonicsEntry));
        OnPropertyChanged(nameof(HarmonicsText));
        OnPropertyChanged(nameof(OriginText));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Refusal));
        OnPropertyChanged(nameof(IsFlagged));
        if (changed) Edited?.Invoke(this, EventArgs.Empty);
    }
}

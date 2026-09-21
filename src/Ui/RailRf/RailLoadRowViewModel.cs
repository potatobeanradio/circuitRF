using System;
using System.Collections.Generic;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// One row of the Loads list (§11.3's fourth point).
/// </summary>
/// <remarks>
/// <b>A load row with no current reads <i>observe</i>, because that is what it is</b> — §11.3, and
/// brief 1 made the absence representable rather than defaulting it to zero. A defaulted zero and a
/// stated zero are the same number and mean different things: the DC report lists an observed port AS
/// observed, and a user who typed nothing must not see a number somebody else chose.
/// </remarks>
public sealed partial class RailLoadRowViewModel : ObservableObject
{
    /// <summary>What the current column reads where the row states no current.</summary>
    public const string ObserveText = "observe";

    private readonly RailSpec _rail;

    /// <summary>
    /// How this row spells a coordinate anchor — a FUNCTION rather than a captured value, because the
    /// board's display unit can change under an open window (the layout editor's own unit picker) and
    /// a row holding the unit it was built with would go on printing the old one.
    /// </summary>
    private readonly Func<RailLengthFormat> _lengthFormat;

    /// <summary>The board's pads, for the anchor field's own tooltip — a function for
    /// <see cref="_lengthFormat"/>'s reason.</summary>
    private readonly Func<IReadOnlyList<PlacedPin>> _pads;

    public RailLoadRowViewModel(RailSpec rail, RailLoad load,
                                Func<RailLengthFormat>? lengthFormat = null,
                                Func<IReadOnlyList<PlacedPin>>? pads = null)
    {
        _rail = rail;
        _load = load;
        _lengthFormat = lengthFormat ?? (static () => RailLengthFormat.Dbu);
        _pads = pads ?? (static () => []);
    }

    private RailLoad _load;

    /// <summary>The record as it stands on the document.</summary>
    public RailLoad Load => _load;

    /// <summary>Raised after a committed edit — the window's cue to re-solve (R-rail7-5).</summary>
    public event EventHandler? Edited;

    /// <summary>"U1.VDD", or the coordinate — in the BOARD's units — where there is no pad to
    /// name.</summary>
    public string Anchor => _load.Anchor.Describe(_lengthFormat());

    /// <summary>The board's display unit changed, so the anchor column has to be re-read.</summary>
    public void NotifyAnchorChanged()
    {
        OnPropertyChanged(nameof(Anchor));
        OnPropertyChanged(nameof(AnchorEntry));
        OnPropertyChanged(nameof(AnchorTip));
    }

    /// <summary>
    /// The anchor as the row's own settable first column — see <see cref="RailAnchorEntry"/> for
    /// why it is settable at all.
    /// </summary>
    public string AnchorEntry
    {
        get => RailAnchorEntry.Text(_load.Anchor, _lengthFormat());
        set
        {
            if (RailAnchorEntry.Parse(value, _lengthFormat()) is { } anchor)
                Commit(_load with { Anchor = anchor });
            else
                NotifyAnchorChanged();   // rejected: snap the field back to what is stored
        }
    }

    /// <summary>How to spell an anchor, and the ones this board offers on this rail.</summary>
    public string AnchorTip => RailAnchorEntry.Tip(_pads(), _rail.NetName, _lengthFormat());

    /// <summary>
    /// The DC current, as the settable column shows it — or <see cref="ObserveText"/>.
    /// </summary>
    /// <remarks>
    /// <b>Clearing the field restores <i>observe</i>.</b> That is the gesture, and it has to exist: a
    /// row typed by accident is otherwise a load with a current the user cannot take back, and the
    /// difference between a 0 mA load and an observation port is exactly what the DC report says
    /// aloud.
    /// </remarks>
    public string CurrentEntry
    {
        get => _load.DcCurrentA is { } i
            ? RailValueFormat.FormatWithUnit(i, RailQuantity.Current)
            : ObserveText;
        set
        {
            // The word itself is accepted back, so a user who selects the cell and retypes what it
            // already said gets what it already said rather than a parse failure.
            bool cleared = string.IsNullOrWhiteSpace(value)
                        || string.Equals(value.Trim(), ObserveText, StringComparison.OrdinalIgnoreCase);

            Commit(_load with
            {
                DcCurrentA = cleared ? null
                    : RailValueFormat.TryParse(value, RailQuantity.Current, out double a) ? a
                    : _load.DcCurrentA,
            });
        }
    }

    /// <summary>True where this row draws nothing — what the row's own styling reads.</summary>
    public bool IsObservationOnly => _load.IsObservationOnly;

    /// <summary>The peak current the transient target consumes, where one is stated.</summary>
    public string PeakEntry
    {
        get => RailValueFormat.FormatWithUnit(_load.PeakCurrentA, RailQuantity.Current, "");
        set => Commit(_load with
        {
            PeakCurrentA = RailValueFormat.TryParse(value, RailQuantity.Current, out double a) ? a : null,
        });
    }

    /// <summary>
    /// The minimum input voltage this part needs to regulate, where it is one.
    /// </summary>
    /// <remarks>
    /// <b>Empty is not zero and not a default.</b> Without it railRF can report the input rail's drop
    /// and cannot report that the drop BROKE the rail downstream — the finding the whole chain exists
    /// to produce — so the empty state is carried through to the report, which says which rails had no
    /// minimum rather than passing them.
    /// </remarks>
    public string MinimumInputVoltageEntry
    {
        get => RailValueFormat.FormatWithUnit(_load.MinimumInputVoltageV, RailQuantity.Voltage, "");
        set => Commit(_load with
        {
            MinimumInputVoltageV = RailValueFormat.TryParse(value, RailQuantity.Voltage, out double v) ? v : null,
        });
    }

    /// <summary>The row's own refusal, or null.</summary>
    public string? Refusal => _load.Refusal($"Load {Anchor}");

    /// <summary>True when this row states something the document refuses.</summary>
    public bool IsFlagged => Refusal is not null;

    /// <summary>Writes through in place — see <c>RailSourceRowViewModel.Commit</c> for why by index.</summary>
    private void Commit(RailLoad next)
    {
        int i = _rail.Loads.IndexOf(_load);
        if (i < 0) return;

        // A REJECTED edit still notifies, and that is not belt-and-braces. An InlineEditText at rest
        // is a TextBlock bound to this property; a typed value the parser refused leaves the control
        // showing text the model does not hold, and with nothing raised it stays on screen looking
        // accepted. Notifying unconditionally snaps it back to what is actually stored.
        bool changed = next != _load;
        if (changed)
        {
            _rail.Loads[i] = next;
            _load = next;
        }

        OnPropertyChanged(nameof(Anchor));
        OnPropertyChanged(nameof(AnchorEntry));
        OnPropertyChanged(nameof(CurrentEntry));
        OnPropertyChanged(nameof(IsObservationOnly));
        OnPropertyChanged(nameof(PeakEntry));
        OnPropertyChanged(nameof(MinimumInputVoltageEntry));
        OnPropertyChanged(nameof(Refusal));
        OnPropertyChanged(nameof(IsFlagged));
        if (changed) Edited?.Invoke(this, EventArgs.Empty);
    }
}

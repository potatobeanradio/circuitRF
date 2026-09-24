using System;
using System.Collections.Generic;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// One row of the Sources list (§11.3's fourth point).
/// </summary>
/// <remarks>
/// <b>A row is a REFDES AND A PIN, not a coordinate.</b> Review's boards have more than one source per
/// rail — a cell through a connector, a regulator making a second voltage — so this is a list with add
/// and remove rather than a pair of fields, and every row names the place a person can point at on the
/// board. <see cref="RailPortAnchor"/>'s coordinate form is still representable and still shown, for
/// the assisted-Gerber path where no placement file or netlist named anything.
///
/// <para><b>It is an EDITOR over the record, not a copy of it.</b> <see cref="RailSource"/> is an
/// immutable record on the document; this holds the current one and replaces it on every committed
/// edit, raising <see cref="Edited"/> so the window re-solves (R-rail7-5). A row that accumulated its
/// own fields and wrote them back later is how the row and the document come to disagree.</para>
/// </remarks>
public sealed partial class RailSourceRowViewModel : ObservableObject
{
    private readonly RailSpec _rail;

    /// <summary>How this row spells a coordinate anchor — see the load row's own field for why it is
    /// a function rather than a value.</summary>
    private readonly Func<RailLengthFormat> _lengthFormat;

    /// <summary>The board's pads, for the anchor field's own tooltip — a function for
    /// <see cref="_lengthFormat"/>'s reason: a board can be adopted under an open window.</summary>
    private readonly Func<IReadOnlyList<PlacedPin>> _pads;

    public RailSourceRowViewModel(RailSpec rail, RailSource source,
                                  Func<RailLengthFormat>? lengthFormat = null,
                                  Func<IReadOnlyList<PlacedPin>>? pads = null)
    {
        _rail = rail;
        _source = source;
        _lengthFormat = lengthFormat ?? (static () => RailLengthFormat.Dbu);
        _pads = pads ?? (static () => []);
    }

    private RailSource _source;

    /// <summary>The record as it stands on the document.</summary>
    public RailSource Source => _source;

    /// <summary>Raised after a committed edit has been written to the document — the window's cue to
    /// re-extract and re-solve. Never raised by a half-typed field.</summary>
    public event EventHandler? Edited;

    /// <summary>"BT1.1", or the coordinate — in the BOARD's units — where there is no pad to
    /// name.</summary>
    public string Anchor => _source.Anchor.Describe(_lengthFormat());

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
        get => RailAnchorEntry.Text(_source.Anchor, _lengthFormat());
        set
        {
            if (RailAnchorEntry.Parse(value, _lengthFormat()) is { } anchor)
                Commit(_source with { Anchor = RailAnchorEntry.KeepLayer(anchor, _source.Anchor) });
            else
                NotifyAnchorChanged();   // rejected: snap the field back to what is stored
        }
    }

    /// <summary>How to spell an anchor, and the ones this board offers on this rail.</summary>
    public string AnchorTip => RailAnchorEntry.Tip(_pads(), _rail.NetName, _lengthFormat());

    /// <summary>What model this row carries, as the row's own second line reads.</summary>
    public string ModelSummary =>
        _source.TouchstoneRef is { Length: > 0 } t
            ? $"measured — {System.IO.Path.GetFileName(t)}"
            : _source.IsRl
                ? $"R-L — {RailValueFormat.FormatWithUnit(_source.SeriesResistanceOhms, RailQuantity.Resistance, "–")}"
                  + $" · {RailValueFormat.FormatWithUnit(_source.SeriesInductanceHenries, RailQuantity.Inductance, "–")}"
                : "no model stated";

    /// <summary>
    /// The open-circuit voltage, as the settable column shows it. <b>Empty is not zero</b> — a source
    /// with no stated voltage is a branch whose impedance is known and whose level is not, which the
    /// frequency answer can use and the DC answer reports rather than defaulting (brief 1).
    /// </summary>
    public string VoltageEntry
    {
        get => RailValueFormat.FormatWithUnit(_source.OpenCircuitVoltageV, RailQuantity.Voltage, "");
        set => Commit(_source with
        {
            OpenCircuitVoltageV = RailValueFormat.TryParse(value, RailQuantity.Voltage, out double v) ? v : null,
        });
    }

    /// <summary>
    /// The series resistance of the source's R-L model — its output impedance, which is what the
    /// frequency answer needs a source to state (an ideal source shorts the rail at every frequency).
    /// </summary>
    /// <remarks>
    /// <b>These two had no column on the window at all</b> (field report, 2026-09-23): the sweep's
    /// refusal told a user to state a source's series resistance, and nothing on screen could.
    /// </remarks>
    public string ResistanceEntry
    {
        get => RailValueFormat.FormatWithUnit(_source.SeriesResistanceOhms, RailQuantity.Resistance, "");
        set => CommitValue(value, RailQuantity.Resistance, (s, v) => s with { SeriesResistanceOhms = v });
    }

    /// <summary>The series inductance of the same model. A unit is required, as everywhere an
    /// inductance is typed in this window.</summary>
    public string InductanceEntry
    {
        get => RailValueFormat.FormatWithUnit(_source.SeriesInductanceHenries, RailQuantity.Inductance, "");
        set => CommitValue(value, RailQuantity.Inductance, (s, v) => s with { SeriesInductanceHenries = v });
    }

    /// <summary>
    /// Empty clears the value. Anything else is stored only where it reads as a value at or above
    /// zero — with a unit where the quantity needs one (<see cref="RailValueFormat.IsBareWhereAUnitIsRequired"/>)
    /// — and otherwise the cell snaps back to what is stored rather than clearing it: a typo must not
    /// silently remove a number, and a bare 2 in an inductance cell must not become 2 H.
    /// </summary>
    private void CommitValue(string? text, RailQuantity quantity, Func<RailSource, double?, RailSource> edit)
    {
        if (string.IsNullOrWhiteSpace(text)) { Commit(edit(_source, null)); return; }

        if (RailValueFormat.IsBareWhereAUnitIsRequired(text, quantity)
            || !RailValueFormat.TryParse(text, quantity, out double v) || v < 0 || !double.IsFinite(v))
        {
            Commit(_source);
            return;
        }

        Commit(edit(_source, v));
    }

    /// <summary>The row's own refusal, or null — what turns the row's flag on.</summary>
    public string? Refusal => _source.Refusal($"Source {Anchor}");

    /// <summary>True when this row states something the document refuses.</summary>
    public bool IsFlagged => Refusal is not null;

    /// <summary>
    /// Writes <paramref name="next"/> through to the document in place, then tells the window.
    /// </summary>
    /// <remarks>
    /// <b>In place, by index.</b> The row's position in <see cref="RailSpec.Sources"/> is what the DC
    /// result's own <c>Index</c> refers to (<see cref="RailSourceShare"/>), so an edit that removed and
    /// re-added would renumber every row below it and silently re-point the results table.
    /// </remarks>
    private void Commit(RailSource next)
    {
        int i = _rail.Sources.IndexOf(_source);
        if (i < 0) return;

        // A REJECTED edit still notifies, and that is not belt-and-braces. An InlineEditText at rest
        // is a TextBlock bound to this property; a typed value the parser refused leaves the control
        // showing text the model does not hold, and with nothing raised it stays on screen looking
        // accepted. Notifying unconditionally snaps it back to what is actually stored.
        bool changed = next != _source;
        if (changed)
        {
            _rail.Sources[i] = next;
            _source = next;
        }

        OnPropertyChanged(nameof(Anchor));
        OnPropertyChanged(nameof(AnchorEntry));
        OnPropertyChanged(nameof(VoltageEntry));
        OnPropertyChanged(nameof(ResistanceEntry));
        OnPropertyChanged(nameof(InductanceEntry));
        OnPropertyChanged(nameof(ModelSummary));
        OnPropertyChanged(nameof(Refusal));
        OnPropertyChanged(nameof(IsFlagged));
        if (changed) Edited?.Invoke(this, EventArgs.Empty);
    }
}

// A series row edits its own model in the pane (docs/sonnet-briefs/
// brief-railrf-35-series-parts-from-the-window.md R-rail35-1b).
//
// ── WHY THIS ROW IS EDITABLE WHEN EVERY OTHER PARTS-TABLE ROW IS NOT ───────────────────────────
//
// RailPartRowViewModel is read-only because a decoupling part's model belongs to the part LIBRARY —
// "a row that could be edited here would be a second place the same number lives". A series
// element's DCR, R-L and file are different: they are fields of the RAIL's own row (RailPart), they
// have been since brief 25, and until brief 35 the only way to set them was to write the .crail by
// hand. The library is the FALLBACK for them (R-rail35-2b), not their home — so this edits the row,
// shows the library's value as the watermark where the row states nothing, and never writes the
// library.
//
// ── THE CHOICE, NOT THREE INDEPENDENT BOXES ────────────────────────────────────────────────────
//
// RailPart.Refusal refuses an R-L and a Touchstone file on one row, so the editor offers R-L OR
// file and a committed value in one clears the other. Flipping the choice alone writes nothing: a
// user who flips to "file" and has not picked one yet still has the R-L they had, which is what the
// row then prints.

using System;
using System.Linq;
using CircuitRF.Design.RailRf;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// The in-pane editor for one series row — its DC resistance, and either an R-L or a Touchstone
/// file (R-rail35-1b).
/// </summary>
/// <remarks>
/// <b>An editor over the document row, keyed by refdes</b> — the source row's own pattern
/// (<see cref="RailSourceRowViewModel"/>): nothing is accumulated here, every committed field goes
/// straight through <see cref="RailRfViewModel.EditPart"/>, and that is the window's one funnel for
/// the re-solve, the dirty mark and the undo entry. The instance survives the parts table's rebuild
/// so a field being typed in is not replaced under the cursor.
/// </remarks>
public sealed partial class RailSeriesEditorViewModel : ObservableObject
{
    private readonly RailRfViewModel _owner;

    internal RailSeriesEditorViewModel(RailRfViewModel owner, string refdes)
    {
        _owner = owner;
        Refdes = refdes;
        _isFileModel = Part is { } p && (p.TouchstoneRef is { Length: > 0 } ||
                                         (!p.IsRl && Model?.ImpedanceFrom == RailSeriesValueSource.Library));
    }

    /// <summary>The element this edits.</summary>
    public string Refdes { get; }

    private RailPart? Part => _owner.SelectedRail?.Parts.FirstOrDefault(
        p => string.Equals(p.Refdes, Refdes, StringComparison.OrdinalIgnoreCase));

    private RailSeriesModel? Model => _owner.Parts.FirstOrDefault(
        p => string.Equals(p.Refdes, Refdes, StringComparison.OrdinalIgnoreCase))?.SeriesModel;

    /// <summary>The editor's heading — which element, and where its numbers come from now.</summary>
    public string Title => Model is { } m
        ? $"{Refdes} — in series · {m.SourceText}"
        : $"{Refdes} — in series";

    // ── the DC resistance ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The row's OWN DC resistance, or empty — <b>empty inherits the part library's</b>, and the
    /// watermark says what that is. Ohms may be bare: the base unit is what anybody means.
    /// </summary>
    public string DcrEntry
    {
        get => RailValueFormat.FormatWithUnit(Part?.DcResistanceOhms, RailQuantity.Resistance, "");
        set => Commit(value, RailQuantity.Resistance, (p, v) => p with { DcResistanceOhms = v });
    }

    /// <summary>What an empty DCR field falls back to.</summary>
    public string DcrWatermark => Model is { DcResistanceFrom: RailSeriesValueSource.Library, DcResistanceOhms: { } r }
        ? $"library: {RailValueFormat.FormatWithUnit(r, RailQuantity.Resistance, 3)}"
        : "unstated — DC total is a lower bound";

    // ── R-L or file ────────────────────────────────────────────────────────────────────────────

    /// <summary>True while the editor shows the Touchstone field rather than the R-L pair. <b>A view
    /// choice only</b> — see this file's header.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRlModel))]
    private bool _isFileModel;

    /// <summary>The other half of the choice, for the second radio button.</summary>
    public bool IsRlModel
    {
        get => !IsFileModel;
        set => IsFileModel = !value;
    }

    /// <summary>The R of the R-L, in ohms. Committing one clears a file the row stated.</summary>
    public string ResistanceEntry
    {
        get => RailValueFormat.FormatWithUnit(Part?.SeriesResistanceOhms, RailQuantity.Resistance, "");
        set => Commit(value, RailQuantity.Resistance,
                      (p, v) => p with { SeriesResistanceOhms = v, TouchstoneRef = v is null ? p.TouchstoneRef : null });
    }

    /// <summary>The L of the R-L. <b>A unit is required</b> — a bare "1.2" is henries to the parser
    /// and microhenries to the person typing it.</summary>
    public string InductanceEntry
    {
        get => RailValueFormat.FormatWithUnit(Part?.SeriesInductanceHenries, RailQuantity.Inductance, "");
        set => Commit(value, RailQuantity.Inductance,
                      (p, v) => p with { SeriesInductanceHenries = v, TouchstoneRef = v is null ? p.TouchstoneRef : null });
    }

    /// <summary>The row's own Touchstone file, relative to the <c>.crail</c>. Committing one clears
    /// the row's R-L; empty inherits the library's file.</summary>
    public string TouchstoneEntry
    {
        get => Part?.TouchstoneRef ?? "";
        set
        {
            string? path = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Apply(p => p with
            {
                TouchstoneRef = path,
                SeriesResistanceOhms = path is null ? p.SeriesResistanceOhms : null,
                SeriesInductanceHenries = path is null ? p.SeriesInductanceHenries : null,
            });
        }
    }

    /// <summary>What an empty file field falls back to.</summary>
    public string TouchstoneWatermark =>
        Model is { ImpedanceFrom: RailSeriesValueSource.Library, TouchstonePath: { } f }
            ? $"library: {System.IO.Path.GetFileName(f)}"
            : "a two-port file, measured series-thru";

    /// <summary>True where the row states a file of its OWN — what <b>Save to library</b> offers to
    /// make the part number's model (owner, 2026-09-24).</summary>
    public bool HasOwnFile => Part?.TouchstoneRef is { Length: > 0 };

    /// <summary>The row's part number, which the part library is keyed by, or null.</summary>
    public string? PartNumber => Part?.PartNumber is { Length: > 0 } pn ? pn : null;

    /// <summary>Why the last value typed was not taken, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string _problem = "";

    /// <summary>True while <see cref="Problem"/> has something to say.</summary>
    public bool HasProblem => Problem.Length > 0;

    // ── committing ─────────────────────────────────────────────────────────────────────────────

    private void Commit(string? text, RailQuantity quantity, Func<RailPart, double?, RailPart> edit)
    {
        if (string.IsNullOrWhiteSpace(text)) { Apply(p => edit(p, null)); return; }

        if (RailValueFormat.IsBareWhereAUnitIsRequired(text, quantity))
        {
            Problem = $"'{text.Trim()}' has no unit. State it — 1.2 µH, 1.2u, 800 nH — since a bare " +
                      "number's scale is a guess and the wrong one looks entirely ordinary.";
            Refresh();
            return;
        }

        if (!RailValueFormat.TryParse(text, quantity, out double v) || v < 0 || !double.IsFinite(v))
        {
            Problem = $"'{text.Trim()}' is not a value railRF can read. State a number at or above " +
                      "zero, with its unit.";
            Refresh();
            return;
        }

        Apply(p => edit(p, v));
    }

    private void Apply(Func<RailPart, RailPart> edit)
    {
        Problem = "";
        _owner.EditPart(Refdes, edit);
        Refresh();
    }

    /// <summary>Re-reads every field from the document — after an edit, an undo, or a rebuild.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(DcrEntry));
        OnPropertyChanged(nameof(DcrWatermark));
        OnPropertyChanged(nameof(ResistanceEntry));
        OnPropertyChanged(nameof(InductanceEntry));
        OnPropertyChanged(nameof(TouchstoneEntry));
        OnPropertyChanged(nameof(TouchstoneWatermark));
        OnPropertyChanged(nameof(HasOwnFile));
    }
}

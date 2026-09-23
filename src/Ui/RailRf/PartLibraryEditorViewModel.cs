// The `.crlib` part library, as a DOCUMENT the application can open
// (docs/sonnet-briefs/brief-railrf-24-part-library-editor.md).
//
// ── WHY THIS FILE EXISTS ───────────────────────────────────────────────────────────────────────
//
// `PartLibraryIo` has read and written `.crlib` since brief 2, `PartLibrary` models it,
// `RailPartResolver` resolves against it and the shipped Power Rail example ships one — and until
// this file, NOTHING IN THE APPLICATION COULD OPEN IT. A first-time designer's pass found it the
// only way it can be found: by double-clicking the row and having nothing happen. So the question
// the whole derating answer rests on — why does C10 model the way it does — was answered by a JSON
// file and a text editor.
//
// ── FRAMEWORK-FREE, LIKE EVERY OTHER railRF VIEW MODEL ─────────────────────────────────────────
//
// No Avalonia. Saving needs a file picker and a top level, which this has neither of: `Save` takes a
// resolved path and does the I/O, exactly as `EmSetupEditorViewModel` does, and the picker lives in
// the workspace. That is what lets the whole of this file be driven from a test with no host.
//
// ── UNDO IS A SNAPSHOT, FOR THE REASON `EmSetupSnapshotCommand` GIVES ──────────────────────────
//
// A part library is a handful of scalars per row, so its own serializer doubles as an exact, trivial
// deep clone and there is nothing to gain from a command per field. The snapshot is taken with
// validation OFF, because a library mid-edit is routinely in a state a validated write would refuse
// (R-rail24-3a) and an undo stack that cannot record a work-in-progress state is an undo stack that
// throws while you type.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.Commands;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// One coarse-grained undo entry for the <c>.crlib</c> editor: the whole <see cref="PartLibrary"/>,
/// before and after one committed edit. Written against <c>EmSetupSnapshotCommand</c>, with one
/// difference that matters.
/// </summary>
/// <remarks>
/// <b>The FIRST <see cref="Execute"/> does nothing, because the edit has already happened.</b>
/// <c>UndoRedoStack.Execute</c> runs a command as it pushes it, and applying the "after" snapshot
/// there would re-read the whole library from JSON — a new <see cref="PartLibrary"/>, new
/// <see cref="PartLibraryRow"/>s and therefore a whole new set of row view models — on every
/// committed keystroke. The EM setup panel survives that because it is a flat form whose every
/// property re-reads its model; a GRID does not: the row whose text box has focus is replaced while
/// the user is typing in it. A redo (the second and later <see cref="Execute"/>) genuinely has to
/// apply it, and does.
/// </remarks>
internal sealed class PartLibrarySnapshotCommand(
    PartLibraryEditorViewModel owner, string beforeJson, string afterJson, string description)
    : IUiCommand
{
    private bool   _alreadyApplied = true;
    private string _afterJson      = afterJson;

    public string Description { get; } = description;

    public void Execute()
    {
        if (_alreadyApplied) { _alreadyApplied = false; return; }
        owner.ApplySnapshot(_afterJson);
    }

    public void Undo() => owner.ApplySnapshot(beforeJson);

    /// <summary>
    /// Extends this entry to cover a further edit to the SAME field, instead of pushing a second one.
    /// </summary>
    /// <remarks>
    /// Typing into a cell commits on every keystroke, which is what makes the dirty mark appear while
    /// the user is working rather than when they happen to click elsewhere. Without this, "100 nF"
    /// would be six undo entries and Ctrl+Z would walk back through "100 n", "100 ", "100"… The
    /// <b>before</b> snapshot is the one taken when the run started, so one Ctrl+Z puts the field back
    /// the way the user found it.
    /// </remarks>
    internal void Amend(string afterJson) => _afterJson = afterJson;
}

/// <summary>One point of the selected row's capacitance-versus-bias curve, as the sub-editor edits it.</summary>
/// <remarks>
/// <b>Both fields are settable and both are text</b>, for <c>RailAggressorRowViewModel</c>'s reason:
/// a rejected edit still notifies, so a value the parser refused snaps back to what is stored rather
/// than staying on screen looking accepted.
/// </remarks>
public sealed partial class PartBiasPointViewModel(PartLibraryEditorViewModel owner, int index)
    : ObservableObject
{
    private PartBiasPoint? Point =>
        owner.SelectedRow is { } row && index >= 0 && index < row.Model.BiasCurve.Count
            ? row.Model.BiasCurve[index]
            : null;

    /// <summary>The bias, in the row's own volts.</summary>
    public string BiasEntry
    {
        get => Point is { } p ? RailValueFormat.FormatWithUnit(p.BiasVolts, RailQuantity.Voltage) : "";
        set
        {
            if (Point is { } p && RailValueFormat.TryParse(value, RailQuantity.Voltage, out double v))
                owner.ReplaceBiasPoint(index, p with { BiasVolts = v }, "Change a bias-curve bias");
            Refresh();
        }
    }

    /// <summary>The capacitance at that bias, in farads.</summary>
    public string CapacitanceEntry
    {
        get => Point is { } p
            ? RailValueFormat.FormatWithUnit(p.CapacitanceFarads, RailQuantity.Capacitance)
            : "";
        set
        {
            if (Point is { } p && RailValueFormat.TryParse(value, RailQuantity.Capacitance, out double c))
                owner.ReplaceBiasPoint(index, p with { CapacitanceFarads = c }, "Change a bias-curve capacitance");
            Refresh();
        }
    }

    /// <summary>What this point retains of the marked value — the number derating is actually about.
    /// Empty where the row states no marked capacitance to compare against.</summary>
    public string RetainedText
    {
        get
        {
            if (Point is not { } p) return "";
            if (owner.SelectedRow?.Model.CapacitanceFarads is not { } marked || !(marked > 0)) return "";
            return (p.CapacitanceFarads / marked).ToString("P0", CultureInfo.InvariantCulture);
        }
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(BiasEntry));
        OnPropertyChanged(nameof(CapacitanceEntry));
        OnPropertyChanged(nameof(RetainedText));
    }
}

/// <summary>
/// One row of the footprint picker (R-rail24-4a).
/// </summary>
/// <param name="Token">What is STORED when the row is chosen, and what the closed combo box shows.
/// Empty on the <b>(none)</b> row.</param>
/// <param name="Display">What the DROPDOWN shows — <see cref="SmtCase.Display"/>, which carries the
/// metric twin and the millimetres on every mention. That is the overview’s §1e spelling and it is
/// not decoration: <c>0201</c> imperial and <c>0201</c> metric are two real case sizes differing by
/// 2.4x.</param>
public sealed record FootprintOption(string Token, string Display)
{
    /// <summary>
    /// <b>The TOKEN, deliberately — never the display.</b>
    /// </summary>
    /// <remarks>
    /// An editable <c>ComboBox</c> puts the selected item’s string into its text box, and that text
    /// box is two-way bound to the row’s <see cref="PartLibraryRowViewModel.Footprint"/>. So this is
    /// what lands in the file. The dropdown reads the long form through an <c>ItemTemplate</c>
    /// instead, which is also what keeps a ·0.7 grid column legible.
    ///
    /// <para>It cannot be a <c>TextSearch.Text</c> attached value: that property lives on
    /// <c>AvaloniaObject</c> and this record is a POCO on the far side of the UI firewall, so
    /// Avalonia falls back to <c>ToString()</c> here — which is exactly the behaviour wanted, and is
    /// determined rather than hoped for.</para>
    /// </remarks>
    public override string ToString() => Token;
}

/// <summary>
/// One row of the part library grid — <b>the fields <see cref="PartLibraryRow"/> actually has, and
/// no invented ones</b> (R-rail24-1b), plus the three things the model already computes and nothing
/// showed (R-rail24-2).
/// </summary>
public sealed partial class PartLibraryRowViewModel(PartLibraryEditorViewModel owner, PartLibraryRow model)
    : ObservableObject
{
    /// <summary>The library row this is a view of. Edited IN PLACE — the undo stack restores the
    /// whole library, so a row object never has to survive an undo.</summary>
    internal PartLibraryRow Model { get; } = model;

    // ── the text fields ───────────────────────────────────────────────────────────────────────

    public string PartNumber
    {
        get => Model.PartNumber;
        set => Commit(() => Model.PartNumber = value?.Trim() ?? "", "Change a part number", nameof(PartNumber));
    }

    public string Description
    {
        get => Model.Description ?? "";
        set => Commit(() => Model.Description = Blank(value), "Change a part description", nameof(Description));
    }

    // ══ THE FOOTPRINT COLUMN (R-rail24-4a) ════════════════════════════════════════════════════
    //
    // A PICKER OVER THE CASE TABLE, AND NOT OVER THE WHOLE `FootprintCatalog`.
    //
    // The catalog’s other two sections — a workspace cell’s layout views, and Custom… over a `.clay`
    // — are ARTWORK references, resolved relative to a schematic’s own directory by
    // `SchematicToLayoutGenerator`. A `.crlib` is not a schematic and nothing resolves a footprint
    // relative to one: `PartLibraryRow.Footprint` is documented as "the footprint this part is
    // BOUGHT IN … nothing here resolves it to artwork", and the one thing that reads it — the parts
    // table’s own column, via `FootprintTokens.Match` — would report any such path as unmatched for
    // ever. Offering rows that cannot be read back is offering a refusal.
    //
    // So what is offered is the section that DOES mean something here: the case sizes, with the same
    // metric twin, the same millimetres and the same ambiguity report the brief asks for.
    //
    // WHAT A CHOSEN ROW STORES IS `smt:0402`, WITH NO DENSITY. Two halves:
    //
    //   • The scheme is stated because a bare `0402` is AMBIGUOUS — eight of the codes name a real
    //     case in both schemes, differing by 2.4x — and `smt:0402` is the exact remedy
    //     `FootprintTokens`’ own ambiguity report names. (It is also why that file now parses the
    //     scheme: the spelling a refusal asks for has to be one the matcher can read.)
    //   • The density is NOT stated, because an IPC-7351B density level is a property of a LAND
    //     PATTERN, not of a package a part is bought in. Writing `@N` here would state a fabrication
    //     preference in a purchasing column, in a file no land pattern is generated from.
    //
    // FREE TEXT SURVIVES. The combo box is editable, so a maintained table’s own spelling —
    // `SM/C_0402`, `CAP-0402-X7R`, a vendor decal name — can still be typed and is still shown as
    // written. That is R-fp4-3b, and it is why this is a picker rather than a closed list.

    /// <summary>The picker’s rows. One shared list: it is the case table, which does not vary by
    /// row, by library or by workspace.</summary>
    public static IReadOnlyList<FootprintOption> Footprints { get; } =
    [
        new FootprintOption("", "(none)"),
        .. SmtCaseTable.All.Select(c => new FootprintOption(FootprintRef.Scheme + c.Code, c.Display)),
    ];

    /// <summary>The same list, as an instance property — compiled bindings resolve against the row’s
    /// own type and do not reach a static.</summary>
    public IReadOnlyList<FootprintOption> FootprintOptions => Footprints;

    /// <summary>
    /// The package the part is bought in — the combo box’s own text, which is the token as the file
    /// holds it.
    /// </summary>
    /// <remarks>
    /// <b>Shown, whatever it is</b> (R-rail24-4b): a field that exists and is invisible is a field
    /// that silently disagrees with whatever else claims to know a part’s package.
    /// </remarks>
    public string Footprint
    {
        get => Model.Footprint ?? "";
        set => SetFootprint(value, echo: false);
    }

    /// <summary>Applies a picker row, and echoes it back to the control.</summary>
    /// <remarks>
    /// Called from the view’s <c>SelectionChanged</c> rather than left to the text binding alone, so
    /// the stored value does not depend on whether Avalonia chooses to push a selected item’s string
    /// into an editable combo box’s text box. <b>It echoes</b> — unlike a keystroke, where re-raising
    /// the edited property would push the stored spelling back under the caret (see
    /// <see cref="Commit"/>) — because after a selection there is no caret to move and the box must
    /// read what was chosen.
    /// </remarks>
    internal void SelectFootprint(FootprintOption option) => SetFootprint(option.Token, echo: true);

    /// <summary>
    /// The one write path for the footprint, typed or picked.
    /// </summary>
    /// <remarks>
    /// <b>An edit that changes nothing is not recorded</b>, and here that is load-bearing rather than
    /// tidy: an editable combo box raises <c>SelectionChanged</c> when its text first matches an item,
    /// which happens while the document is being BOUND. Without this guard, opening a library whose
    /// rows already name case sizes would push an undo entry per row and the document would open
    /// dirty — against a file it had not changed a byte of.
    /// </remarks>
    private void SetFootprint(string? text, bool echo)
    {
        string? next = Blank(text);
        if (string.Equals(next, Model.Footprint, StringComparison.Ordinal))
        {
            if (echo) OnPropertyChanged(nameof(Footprint));
            return;
        }

        Commit(() => Model.Footprint = next, "Change a part footprint", nameof(Footprint));
        if (echo) OnPropertyChanged(nameof(Footprint));
    }

    /// <summary>What this row’s footprint token turned out to be — <c>FootprintTokens</c>’ answer,
    /// not a second reading of it. Recomputed rather than cached: it is a handful of dictionary
    /// lookups, and a cache here is a cache to invalidate on every keystroke.</summary>
    private FootprintTokenMatch FootprintMatch => FootprintTokens.Match(Model.Footprint);

    /// <summary>
    /// The sentence the footprint cell carries: the reading for a matched token, BOTH readings for an
    /// ambiguous one, and the case list for an unmatched one.
    /// </summary>
    /// <remarks>
    /// <c>FootprintTokens</c> owns every one of them, so the parts table and this editor cannot come
    /// to two different conclusions about one token. The empty case is the exception, and only
    /// because that file’s own sentence names a bill of materials — which is not what is being edited
    /// here.
    /// </remarks>
    public string FootprintReport =>
        Model.Footprint is { Length: > 0 }
            ? FootprintMatch.Report
            : "This row states no footprint. Pick a case size, or type the package as the maintained "
            + "table spells it — an unrecognised token is shown as written and is never matched to the "
            + "nearest code.";

    /// <summary>
    /// True for a bare four-digit token that names a real case in BOTH schemes.
    /// </summary>
    /// <remarks>
    /// <b>The one footprint state worth colouring</b>, and the picker itself can never produce it. An
    /// UNMATCHED token is ordinary — a vendor decal name is a legitimate thing for a maintained table
    /// to carry, and R-fp4-3b’s whole point is that it is reported rather than corrected. An
    /// ambiguous one is different: <c>0201</c> read the wrong way is 2.4x out, it places, it renders,
    /// it exports, and the first sign of trouble is a board.
    /// </remarks>
    public bool IsFootprintAmbiguous =>
        Model.Footprint is { Length: > 0 } &&
        FootprintMatch.Outcome == FootprintTokenOutcome.Ambiguous;

    /// <summary>X7R, X5R, C0G/NP0, … <b>Blank is load-bearing</b>: it produces a part marked as
    /// having no class rather than one quietly given X7R's dissipation factor.</summary>
    public string DielectricClass
    {
        get => Model.DielectricClass ?? "";
        set => Commit(() => Model.DielectricClass = Blank(value), "Change a dielectric class", nameof(DielectricClass));
    }

    /// <summary>
    /// What the Class drop-down offers: every class railRF has a dissipation factor for, then
    /// <see cref="PartLibraryRow.OtherClass"/> for a part that is not a capacitor. The box stays
    /// editable, so a class a maintained table already spells its own way is kept as written.
    /// </summary>
    /// <remarks>Field report, 2026-09-23 — the designer's suggestion, and the answer to a ferrite
    /// bead and a sense resistor being counted as capacitors missing a bias curve.</remarks>
    public static IReadOnlyList<string> ClassOptions { get; } =
        [.. RailEsrDefaults.KnownClasses, PartLibraryRow.OtherClass];

    public string VoltageRatingEntry
    {
        get => Text(Model.VoltageRatingV, RailQuantity.Voltage);
        set => Commit(() => Model.VoltageRatingV = Parse(value, RailQuantity.Voltage, Model.VoltageRatingV),
                      "Change a voltage rating", nameof(VoltageRatingEntry));
    }

    public string CapacitanceEntry
    {
        get => Text(Model.CapacitanceFarads, RailQuantity.Capacitance);
        set => Commit(() => Model.CapacitanceFarads = Parse(value, RailQuantity.Capacitance, Model.CapacitanceFarads),
                      "Change a marked capacitance", nameof(CapacitanceEntry));
    }

    public string SelfResonantFrequencyEntry
    {
        get => Text(Model.SelfResonantFrequencyHz, RailQuantity.Frequency);
        set => Commit(() => Model.SelfResonantFrequencyHz =
                          Parse(value, RailQuantity.Frequency, Model.SelfResonantFrequencyHz),
                      "Change a self-resonant frequency", nameof(SelfResonantFrequencyEntry));
    }

    /// <summary>An inductance the table states. <b>Compared, never used</b> — see
    /// <see cref="DisagreementText"/>.</summary>
    public string StatedInductanceEntry
    {
        get => Text(Model.StatedInductanceHenries, RailQuantity.Inductance);
        set => Commit(() => Model.StatedInductanceHenries =
                          Parse(value, RailQuantity.Inductance, Model.StatedInductanceHenries),
                      "Change a stated inductance", nameof(StatedInductanceEntry));
    }

    public string EsrEntry
    {
        get => Text(Model.EsrOhms, RailQuantity.Resistance);
        set => Commit(() => Model.EsrOhms = Parse(value, RailQuantity.Resistance, Model.EsrOhms),
                      "Change a stated ESR", nameof(EsrEntry));
    }

    /// <summary>A per-part Touchstone or SPICE model, relative to the library file. <b>It overrides
    /// the row</b>, and it is the only route to a real ESR.</summary>
    public string ModelRef
    {
        get => Model.ModelRef ?? "";
        set => Commit(() => Model.ModelRef = Blank(value), "Change a part model reference", nameof(ModelRef));
    }

    // ── what the library already computes, and nothing showed (R-rail24-2) ─────────────────────

    /// <summary>L = 1/((2·π·f₀)²·C) — <b>the value used</b>. Read-only, because it is derived and a
    /// stored third copy is a field that will be edited on one side only.</summary>
    public string DerivedInductanceText =>
        RailValueFormat.FormatWithUnit(Model.DerivedInductanceHenries, RailQuantity.Inductance, "—");

    /// <summary><b>R-rail24-2a.</b> True when the stated inductance and the derived one are further
    /// apart than <see cref="PartLibrary.InductanceTolerance"/>. Shown ON THE ROW, where it can be
    /// fixed, rather than only in a run's notes.</summary>
    public bool IsInductanceDisagreement =>
        Model.InductanceDisagreement > PartLibrary.InductanceTolerance;

    /// <summary>How far apart they are, or empty where the row does not carry both.</summary>
    public string DisagreementText =>
        Model.InductanceDisagreement is { } d
            ? d.ToString("P1", CultureInfo.InvariantCulture) + " apart"
            : "";

    /// <summary>The sentence the row's warning reads, or null.</summary>
    public string? DisagreementSentence =>
        IsInductanceDisagreement
            ? $"The stated inductance and the one C with f₀ derive are {DisagreementText}. " +
              "The derived value is the one used."
            : null;

    /// <summary><b>R-rail24-2b.</b> Where this row's ESR comes from — the difference between a number
    /// somebody measured and a number railRF assumed.</summary>
    public string EsrBasisText => Model.EsrBasis switch
    {
        EsrProvenance.Measured     => "measured (attached file)",
        EsrProvenance.Stated       => "stated",
        EsrProvenance.ClassDefault => "class default — indicative",
        _                          => "none — resolves to nothing",
    };

    /// <summary>True where the ESR is a dielectric-class default. <b>Q-15's normal case, not the
    /// degraded one</b> — but every peak height computed from one is marked indicative, so the row
    /// says so on its face.</summary>
    public bool IsIndicative => Model.EsrBasis == EsrProvenance.ClassDefault;

    /// <summary>True where the ESR resolves to nothing at all — no file, no stated value, no class.</summary>
    public bool HasNoEsrBasis => Model.EsrBasis is null;

    /// <summary>Which of the two sources this row's model comes from.</summary>
    public string ModelSourceText =>
        Model.ModelSource == PartModelSource.AttachedFile
            ? "the attached file, which overrides the row"
            : "the library row";

    /// <summary>How many bias-curve points this row carries — the count §9 makes visible.</summary>
    public string BiasCurveText =>
        Model.BiasCurve.Count == 0 ? "no bias curve" : $"{Model.BiasCurve.Count} point(s)";

    public bool HasBiasCurve => Model.BiasCurve.Count > 0;

    // ── the refusal (R-rail24-3) ──────────────────────────────────────────────────────────────

    /// <summary>What makes this row unusable, or null. <b>Stated, not prevented</b> — the row turns
    /// red and the sentence shows; the file still saves (R-rail24-3a).</summary>
    public string? Refusal => Model.Refusal();

    public bool IsFlagged => Refusal is not null;

    // ── seeding (brief-authored-board-4 R-ab4-2b, R-ab4-3b) ───────────────────────────────────

    /// <summary>
    /// True where this row states a part number and <b>nothing that models the part</b> — no
    /// capacitance, no self-resonance, no attached file.
    /// </summary>
    /// <remarks>
    /// <b>R-ab4-2b, and it is the thing the obvious implementation gets wrong.</b>
    /// <see cref="PartLibrary.Coverage"/> counts a part as KNOWN the moment a row for it exists, so
    /// seeding nine missing part numbers moves nine parts out of the uncovered count while
    /// contributing nothing at all to the answer. The counts stay honest by themselves — the nine
    /// land in <c>WithoutBiasCurve</c> and in <c>WithoutEsrBasis</c> — and this is what says so on
    /// the row itself, where it gets fixed.
    ///
    /// <para>It is a property of the MODEL, not a memory of how the row arrived: a row somebody
    /// typed a part number into is in exactly the same state, and it stops reading incomplete the
    /// moment a number is entered.</para>
    /// </remarks>
    public bool IsIncomplete =>
        Model.PartNumber.Length > 0
        && Model.ModelRef is not { Length: > 0 }
        && (Model.IsCapacitor
            ? Model.CapacitanceFarads is null && Model.SelfResonantFrequencyHz is null
            : Model.EsrOhms is null);   // an Other part is modelled by its stated resistance

    /// <summary>Where this row came from, or empty — <b>the parts table's own sentence</b>
    /// (<c>RailPartRowViewModel.OriginText</c>), because a pre-filled number nobody checked is the
    /// one that will be wrong and this is the second place it can happen.</summary>
    public string OriginText => owner.OriginOf(Model);

    /// <summary>The part-number cell's tooltip: the refusal where there is one, otherwise what is
    /// still missing. One tip, because a cell has one.</summary>
    public string? PartNumberTip =>
        Refusal
        ?? (IsIncomplete
            ? "This row names the part and states no model for it — no capacitance, no self-resonant "
            + "frequency and no attached file. It counts as a part with no bias curve and no ESR "
            + "basis until one is entered."
            : null);

    // ── plumbing ──────────────────────────────────────────────────────────────────────────────

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Text(double? value, RailQuantity quantity) =>
        value is { } v ? RailValueFormat.FormatWithUnit(v, quantity) : "";

    /// <summary>
    /// A typed value, or the one already stored when the text does not parse — <b>except that an
    /// EMPTY field clears the value</b>, which is the whole reason every electrical field on this row
    /// is nullable: a real maintained table is partly populated, and §9's point is that the emptiness
    /// must be visible rather than filled in.
    /// </summary>
    private static double? Parse(string? text, RailQuantity quantity, double? current)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // ── A BARE NUMBER IS REFUSED WHERE ITS SCALE IS A GUESS (field report, 2026-09-23) ─────
        //
        // The designer asked which unit f0 was in. A self-resonance copied from a table in MHz as 28.89
        // was stored as 28.89 Hz; a capacitance in pF as farads. Nothing about either looks wrong
        // in the cell. Volts and ohms keep the bare form, where the base unit is what anybody means.
        if (RailValueFormat.IsBareWhereAUnitIsRequired(text, quantity)) return current;

        return RailValueFormat.TryParse(text, quantity, out double v) ? v : current;
    }

    /// <summary>
    /// Applies one edit and records it.
    /// </summary>
    /// <remarks>
    /// <b>It notifies whether or not anything changed</b> — a value the parser refused leaves the
    /// control showing text the model does not hold, and with nothing raised it stays on screen
    /// looking accepted.
    ///
    /// <para><b>Except the field being typed into</b> (<paramref name="field"/>). These cells commit
    /// on every keystroke, so that the dirty mark appears while the user is working rather than when
    /// they happen to click something else — owner report, 2026-09-20. Re-raising the edited
    /// property mid-word would push the formatter's own spelling back into the box under the caret,
    /// which moves it: type <c>100</c> and the field becomes <c>100 F</c> with the caret at the end,
    /// before the <c>nF</c> is typed. The value still snaps back when focus leaves, because the
    /// binding re-reads then.</para>
    /// </remarks>
    private void Commit(Action mutate, string description, string field)
    {
        string before = owner.SnapshotJson();
        mutate();
        owner.CommitEdit(before, description, MergeKey(field));
        Refresh(except: field);
        owner.RefreshDerived();
    }

    /// <summary>Identifies one field of one row across a run of keystrokes. <b>By the row's model
    /// object</b>, not by its position: a row's index changes when another is removed above it, and
    /// two consecutive runs must not merge just because they landed on the same index.</summary>
    private string MergeKey(string field) =>
        $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Model)}:{field}";

    internal void Refresh(string? except = null)
    {
        if (except != nameof(PartNumber)) OnPropertyChanged(nameof(PartNumber));
        if (except != nameof(Description)) OnPropertyChanged(nameof(Description));
        if (except != nameof(Footprint)) OnPropertyChanged(nameof(Footprint));
        OnPropertyChanged(nameof(FootprintReport));
        OnPropertyChanged(nameof(IsFootprintAmbiguous));
        if (except != nameof(DielectricClass)) OnPropertyChanged(nameof(DielectricClass));
        if (except != nameof(VoltageRatingEntry)) OnPropertyChanged(nameof(VoltageRatingEntry));
        if (except != nameof(CapacitanceEntry)) OnPropertyChanged(nameof(CapacitanceEntry));
        if (except != nameof(SelfResonantFrequencyEntry)) OnPropertyChanged(nameof(SelfResonantFrequencyEntry));
        if (except != nameof(StatedInductanceEntry)) OnPropertyChanged(nameof(StatedInductanceEntry));
        if (except != nameof(EsrEntry)) OnPropertyChanged(nameof(EsrEntry));
        if (except != nameof(ModelRef)) OnPropertyChanged(nameof(ModelRef));
        if (except != nameof(DerivedInductanceText)) OnPropertyChanged(nameof(DerivedInductanceText));
        if (except != nameof(IsInductanceDisagreement)) OnPropertyChanged(nameof(IsInductanceDisagreement));
        if (except != nameof(DisagreementText)) OnPropertyChanged(nameof(DisagreementText));
        if (except != nameof(DisagreementSentence)) OnPropertyChanged(nameof(DisagreementSentence));
        if (except != nameof(EsrBasisText)) OnPropertyChanged(nameof(EsrBasisText));
        if (except != nameof(IsIndicative)) OnPropertyChanged(nameof(IsIndicative));
        if (except != nameof(HasNoEsrBasis)) OnPropertyChanged(nameof(HasNoEsrBasis));
        if (except != nameof(ModelSourceText)) OnPropertyChanged(nameof(ModelSourceText));
        if (except != nameof(BiasCurveText)) OnPropertyChanged(nameof(BiasCurveText));
        if (except != nameof(HasBiasCurve)) OnPropertyChanged(nameof(HasBiasCurve));
        if (except != nameof(Refusal)) OnPropertyChanged(nameof(Refusal));
        if (except != nameof(IsFlagged)) OnPropertyChanged(nameof(IsFlagged));
        if (except != nameof(IsIncomplete)) OnPropertyChanged(nameof(IsIncomplete));
        if (except != nameof(OriginText)) OnPropertyChanged(nameof(OriginText));
        if (except != nameof(PartNumberTip)) OnPropertyChanged(nameof(PartNumberTip));
    }
}

/// <summary>
/// The <c>.crlib</c> editor. One grid of rows, a bias-curve sub-editor on the selected one, and the
/// three things the library already knew and never said.
/// </summary>
public sealed partial class PartLibraryEditorViewModel : ObservableObject
{
    /// <summary>Absolute path of the <c>.crlib</c>. Never null — a part library is a file on disk
    /// before it is a document, exactly like a <c>.cem</c>.</summary>
    public string FilePath { get; private set; }

    /// <summary>The library being edited.</summary>
    public PartLibrary Working { get; private set; }

    public UndoRedoStack UndoRedo { get; } = new();
    public IRelayCommand UndoCommand   { get; }
    public IRelayCommand RedoCommand   { get; }
    public IRelayCommand SaveCommand   { get; }

    /// <summary>The toolbar's Save As…. It does no I/O of ITS own — see
    /// <see cref="SaveAsRequested"/>.</summary>
    public IRelayCommand SaveAsCommand { get; }

    [ObservableProperty] private bool _isDirty;

    public ObservableCollection<PartLibraryRowViewModel> Rows { get; } = [];

    /// <summary>The bias-curve points of <see cref="SelectedRow"/> — R-rail24-1c's sub-editor.</summary>
    public ObservableCollection<PartBiasPointViewModel> BiasPoints { get; } = [];

    public event Action<string>? SaveError;
    public event Action<string>? PartLibrarySaved;

    /// <summary>Raised after a successful <see cref="SaveAs"/>, with the NEW absolute path — the
    /// workspace has to re-key its open-document map and the document has to retitle.</summary>
    public event Action<string>? PartLibrarySavedAs;

    /// <summary>
    /// Asks the host to put a file picker up and then call <see cref="SaveAs"/> with what it
    /// returned. <b>The picker cannot live here</b> — it needs a top level, and this view model is
    /// framework-free precisely so a test can drive the whole editor without one; the same split
    /// <c>RailRfViewModel.SaveRequested</c> draws. Null (a headless view model) makes the toolbar
    /// button inert rather than adding a second save path.
    /// </summary>
    public Action? SaveAsRequested { get; set; }

    /// <summary>The toolbar's Import table…. The picker is the host's, on <see cref="SaveAsRequested"/>'s
    /// terms; the import itself is <see cref="ImportTable"/>.</summary>
    public IRelayCommand ImportTableCommand { get; }

    /// <summary>Asks the host for a <c>.csv</c> and then calls <see cref="ImportTable"/>.</summary>
    public Action? ImportTableRequested { get; set; }

    /// <summary>What the last import did, or empty — shown above the grid until the next one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportReport))]
    private IReadOnlyList<string> _importReport = [];

    public bool HasImportReport => ImportReport.Count > 0;

    /// <summary>
    /// Reads a table of parts into this library as ONE undoable edit (field report, 2026-09-23: a
    /// designer had every part's C, self-resonance and ESL in a spreadsheet and no way in).
    /// </summary>
    /// <remarks>
    /// Every rule — which columns, which units, which rows match — is
    /// <see cref="PartLibraryTableImport"/>'s, below the firewall. This only snapshots, applies and
    /// shows the report.
    /// </remarks>
    public void ImportTable(string path)
    {
        string text;
        try { text = System.IO.File.ReadAllText(path); }
        catch (Exception ex)
        {
            ImportReport = [$"{System.IO.Path.GetFileName(path)} could not be read: {ex.Message}"];
            return;
        }

        string before = SnapshotJson();
        var report = PartLibraryTableImport.Apply(Working, text, System.IO.Path.GetFileName(path));
        if (report.Added.Count + report.Updated.Count > 0)
        {
            CommitEdit(before, $"Import {System.IO.Path.GetFileName(path)}");
            RebuildRows();
            RefreshDerived();
        }
        ImportReport = [$"{System.IO.Path.GetFileName(path)}: {report.Summary}", .. report.Notes];
    }

    private bool _suppressCommit;

    public PartLibraryEditorViewModel(string filePath, PartLibrary library)
    {
        FilePath = filePath;
        Working  = library ?? throw new ArgumentNullException(nameof(library));

        UndoCommand = new RelayCommand(() => UndoRedo.Undo(), () => UndoRedo.CanUndo);
        RedoCommand = new RelayCommand(() => UndoRedo.Redo(), () => UndoRedo.CanRedo);
        SaveCommand   = new RelayCommand(Save);
        SaveAsCommand = new RelayCommand(() => SaveAsRequested?.Invoke());
        ImportTableCommand = new RelayCommand(() => ImportTableRequested?.Invoke());

        UndoRedo.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UndoRedoStack.CanUndo))    UndoCommand.NotifyCanExecuteChanged();
            if (e.PropertyName is nameof(UndoRedoStack.CanRedo))    RedoCommand.NotifyCanExecuteChanged();
            if (e.PropertyName is nameof(UndoRedoStack.IsModified)) IsDirty = UndoRedo.IsModified;
        };

        RebuildRows();
    }

    // ── the selected row and its bias curve ───────────────────────────────────────────────────

    private PartLibraryRowViewModel? _selectedRow;

    public PartLibraryRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (ReferenceEquals(_selectedRow, value)) return;
            _selectedRow = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedRow));
            OnPropertyChanged(nameof(SelectedRowTitle));
            RebuildBiasPoints();
        }
    }

    public bool HasSelectedRow => _selectedRow is not null;

    /// <summary>
    /// A bias curve is a CAPACITOR's, so a point can be added only to a row that states a marked
    /// capacitance.
    /// </summary>
    /// <remarks>
    /// Field report, 2026-09-23: this seeded a row with no capacitance at 1 µF, so pressing it on a
    /// ferrite bead's row gave the bead a one-point curve — and the bead was then solved as a 1 µF
    /// decoupling capacitor, reading "unresolved → 1 µF" in the parts table.
    /// </remarks>
    public bool CanAddBiasPoint =>
        _selectedRow?.Model is { CapacitanceFarads: > 0, IsCapacitor: true };

    public string SelectedRowTitle =>
        _selectedRow is { } r && r.PartNumber.Length > 0
            ? $"Bias curve — {r.PartNumber}"
            : "Bias curve";

    /// <summary>The selected row's curve, in the order it is stored (sorted by bias). What the plot
    /// draws.</summary>
    public IReadOnlyList<PartBiasPoint> SelectedBiasCurve =>
        _selectedRow is { } r ? [.. r.Model.BiasCurve] : [];

    /// <summary>The selected row's MARKED capacitance, which is what the plot draws the curve
    /// against — a derating curve is only readable beside the value it derates from.</summary>
    public double? SelectedMarkedCapacitanceFarads => _selectedRow?.Model.CapacitanceFarads;

    // ── file-level state (R-rail24-2c, R-rail24-3) ────────────────────────────────────────────

    /// <summary>What this library is called on a report.</summary>
    public string Name
    {
        get => Working.Name;
        set
        {
            string before = SnapshotJson();
            Working.Name = value?.Trim() ?? "";
            CommitEdit(before, "Change the library name");
            OnPropertyChanged();
        }
    }

    /// <summary><b>R-rail24-3.</b> The FILE's refusal — what makes the library as a whole unusable —
    /// or null. Shown in the status strip, in the shape every other railRF refusal takes.</summary>
    public string? Refusal => Working.Refusal();

    public bool IsRefused => Refusal is not null;

    /// <summary>Every row whose stated inductance disagrees with the derived one, as
    /// <see cref="PartLibrary.InductanceDisagreements"/> words them (R-rail24-2a).</summary>
    public IReadOnlyList<string> Disagreements => Working.InductanceDisagreements();

    public bool HasDisagreements => Disagreements.Count > 0;

    /// <summary>How many rows are modelled from a dielectric-class default (R-rail24-2b).</summary>
    public int IndicativeCount => Working.Rows.Count(r => r.EsrBasis == EsrProvenance.ClassDefault);

    /// <summary>
    /// <b>R-rail24-2c.</b> What this library covers of the design it was opened from, or null where
    /// nothing referenced it.
    /// </summary>
    /// <remarks>
    /// Set by the workspace, which is the only thing that can find the <c>.crail</c> that names this
    /// file. The numbers themselves are <see cref="PartLibrary.Coverage"/>'s own — nothing here
    /// counts anything.
    /// </remarks>
    public PartLibraryCoverage? Coverage { get; private set; }

    /// <summary>The design the coverage is against — a file name, shown beside the counts.</summary>
    public string? CoverageSubject { get; private set; }

    public bool HasCoverage => Coverage is not null;

    /// <summary>The sentence R-rail24-2c asks for: <i>"13 referenced, 11 known, 6 with a bias
    /// curve"</i>, against the document that referenced this library.</summary>
    public string CoverageText =>
        Coverage is not { } c
            ? ""
            : $"{CoverageSubject}: {c.Referenced} referenced, {c.Known} known, " +
              $"{c.WithBiasCurve} with a bias curve" +
              (c.Unknown.Count > 0 ? $", {c.Unknown.Count} not in this library" : "") +
              (c.WithoutEsrBasis.Count > 0 ? $", {c.WithoutEsrBasis.Count} resolving no ESR at all" : "") +
              ".";

    /// <summary>
    /// Tells the editor which part numbers a design actually asks this library about.
    /// <b>Re-askable</b> — the workspace calls it again after a Save As, when the library the design
    /// names may no longer be this file.
    /// </summary>
    /// <param name="bom">The bill of materials the design was imported with, or null. Read only when
    /// a row is SEEDED (R-ab4-3a) — nothing here counts it and no column shows it.</param>
    public void SetCoverageContext(
        string? subject, IEnumerable<string>? referencedPartNumbers, BomTable? bom = null)
    {
        if (subject is not { Length: > 0 } || referencedPartNumbers is null)
        {
            Coverage = null;
            CoverageSubject = null;
        }
        else
        {
            CoverageSubject = subject;
            Coverage = Working.Coverage(referencedPartNumbers);
        }
        _referenced = Coverage is null ? null : [.. referencedPartNumbers ?? []];
        _bom = Coverage is null ? null : bom;
        OnPropertyChanged(nameof(Coverage));
        OnPropertyChanged(nameof(CoverageSubject));
        OnPropertyChanged(nameof(HasCoverage));
        OnPropertyChanged(nameof(CoverageText));
        RefreshSeeding();
    }

    /// <summary>The same, from the walk's own answer — what the workspace hands over.</summary>
    public void SetCoverageContext(PartLibraryCoverageContext? context) =>
        SetCoverageContext(context?.Subject, context?.PartNumbers, context?.Bom);

    private List<string>? _referenced;
    private BomTable? _bom;

    // ── row commands (R-rail24-1b) ────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds an empty row and selects it.
    /// </summary>
    /// <remarks>
    /// <b>It has no part number, which means the library is momentarily REFUSED</b> — and that is the
    /// case R-rail24-3a exists for. The strip says so, the row is red, and the file still saves.
    /// </remarks>
    [RelayCommand]
    private void AddRow()
    {
        string before = SnapshotJson();
        var row = new PartLibraryRow();
        Working.Rows.Add(row);
        CommitEdit(before, "Add a part");
        RebuildRows();
        SelectedRow = Rows.LastOrDefault();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedRow))]
    private void RemoveRow()
    {
        if (SelectedRow is not { } row) return;
        int index = Rows.IndexOf(row);

        string before = SnapshotJson();
        Working.Rows.Remove(row.Model);
        CommitEdit(before, "Remove a part");
        RebuildRows();
        SelectedRow = Rows.Count == 0 ? null : Rows[Math.Clamp(index, 0, Rows.Count - 1)];
    }

    // ══ SEEDING THE ROWS THE DESIGN ASKED FOR (brief-authored-board-4, R-ab4-1) ════════════════
    //
    // `AddRow` adds one empty row, and until this command a user who had just imported a thirteen-part
    // board typed thirteen part numbers the application already held. `PartLibraryCoverageContext`
    // resolves which `.crail` names this file — by resolved PATH, not by string comparison — and
    // `PartLibrary.Coverage` already distinguishes which of that design's part numbers have no row.
    // Neither of those is re-derived here: this command adds a row per `Coverage.Unknown` entry and
    // counts nothing of its own, which is the same rule that keeps the coverage strip honest.

    /// <summary>The part numbers the design asks for and this library has no row for — document
    /// order, repeats collapsed. <see cref="PartLibraryCoverage.Unknown"/>'s own list.</summary>
    public IReadOnlyList<string> MissingPartNumbers => Coverage?.Unknown ?? [];

    public int MissingPartCount => MissingPartNumbers.Count;

    /// <summary>
    /// <b>R-ab4-1d.</b> Whether there is a design behind this library at all. False makes the command
    /// ABSENT rather than disabled — a shared library being edited on its own is an ordinary state
    /// and there is nothing to seed FROM, which is a different thing from nothing to seed.
    /// </summary>
    public bool HasSeedSource => Coverage is not null;

    /// <summary><b>R-ab4-1b.</b> The button's own words — the count AND the design, because a user
    /// with two boards in a workspace cannot otherwise tell which one they are about to seed
    /// from.</summary>
    public string SeedMissingPartsText =>
        Coverage is null ? "Add the parts this design asks for"
        : MissingPartCount == 0 ? $"Add the parts {CoverageSubject} asks for"
        : MissingPartCount == 1 ? $"Add the 1 part {CoverageSubject} asks for"
        : $"Add the {MissingPartCount} parts {CoverageSubject} asks for";

    /// <summary><b>R-ab4-1c.</b> Why the button is disabled, where it is — a library that covers its
    /// design is the GOOD state, and a control that vanished on success would read as one that
    /// broke.</summary>
    public string SeedMissingPartsTooltip =>
        Coverage is null
            ? "No design in this workspace references this library."
            : MissingPartCount == 0
                ? $"Every part number {CoverageSubject} asks for is already in this library."
                : $"Adds one row per part number {CoverageSubject} asks for and this library has no "
                + "row for, in the design's own order. Each row states the part number and nothing "
                + "else, so it still counts as a part with no bias curve until you fill it in"
                + (_bom is null
                    ? "."
                    : $" — except where {_bom.FileName} already says something, which is carried "
                    + "across and marked as coming from there.");

    /// <summary>
    /// Adds a row per missing part number, as ONE undo entry.
    /// </summary>
    /// <remarks>
    /// <b>R-ab4-4a.</b> Nine rows that take nine undos to remove is the Match Designer's slider
    /// defect in a new place, so the whole batch is one <see cref="PartLibrarySnapshotCommand"/> —
    /// which costs nothing here, because the snapshot is the whole library either way.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanSeedMissingParts))]
    private void SeedMissingParts()
    {
        var missing = MissingPartNumbers;
        if (missing.Count == 0) return;

        string before = SnapshotJson();
        foreach (string partNumber in missing)
        {
            Working.Rows.Add(PartLibrarySeed.Row(partNumber, _bom, out bool fromBom));
            if (fromBom) _seededFromBom.Add(partNumber);
        }

        CommitEdit(before, missing.Count == 1 ? "Add the part the design asks for"
                                              : $"Add the {missing.Count} parts the design asks for");
        RebuildRows();
        SelectedRow = Rows.Count == 0 ? null : Rows[^missing.Count];
    }

    private bool CanSeedMissingParts() => MissingPartCount > 0;

    /// <summary>
    /// Which part numbers were seeded from the bill of materials in this session.
    /// </summary>
    /// <remarks>
    /// <b>By PART NUMBER, and not on the row</b> (R-ab4-3b, and §6's "no change to the format").
    /// Provenance is session knowledge: it must survive an undo — which replaces every row object in
    /// the grid — and it must not reach the file, because a <c>.crlib</c> written with a field
    /// nothing reads is a format change. Renaming a seeded row's part number drops it, which is
    /// right: it is no longer the row the bill of materials described.
    /// </remarks>
    private readonly HashSet<string> _seededFromBom = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The sentence a row shows for where it came from, or empty. <b>The parts table's
    /// own</b> — see <c>RailPartRowViewModel.OriginText</c>.</summary>
    internal string OriginOf(PartLibraryRow row) =>
        row.PartNumber is { Length: > 0 } p && _seededFromBom.Contains(p)
            ? "pre-filled from the bill of materials"
            : "";

    private void RefreshSeeding()
    {
        OnPropertyChanged(nameof(MissingPartNumbers));
        OnPropertyChanged(nameof(MissingPartCount));
        OnPropertyChanged(nameof(HasSeedSource));
        OnPropertyChanged(nameof(SeedMissingPartsText));
        OnPropertyChanged(nameof(SeedMissingPartsTooltip));
        SeedMissingPartsCommand.NotifyCanExecuteChanged();
    }

    // ── bias-curve commands (R-rail24-1c) ─────────────────────────────────────────────────────

    /// <summary>
    /// Adds a curve point at the row's own rating and marked capacitance, which is the point somebody
    /// is most likely to be entering next — never a zero, which the row would refuse.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddBiasPoint))]
    private void AddBiasPoint()
    {
        if (SelectedRow is not { } row || !CanAddBiasPoint) return;

        double bias = row.Model.BiasCurve.Count == 0
            ? 0.0
            : row.Model.BiasCurve[^1].BiasVolts + Math.Max(1.0, row.Model.VoltageRatingV ?? 1.0) / 4.0;
        double capacitance = row.Model.BiasCurve.Count == 0
            ? row.Model.CapacitanceFarads!.Value
            : row.Model.BiasCurve[^1].CapacitanceFarads;

        string before = SnapshotJson();
        row.Model.BiasCurve.Add(new PartBiasPoint(bias, capacitance));
        SortCurve(row.Model);
        CommitEdit(before, "Add a bias-curve point");
        RebuildBiasPoints();
        row.Refresh();
        RefreshDerived();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveBiasPoint))]
    private void RemoveBiasPoint()
    {
        if (SelectedRow is not { } row) return;
        if (SelectedBiasPointIndex is not { } i || i < 0 || i >= row.Model.BiasCurve.Count) return;

        string before = SnapshotJson();
        row.Model.BiasCurve.RemoveAt(i);
        CommitEdit(before, "Remove a bias-curve point");
        RebuildBiasPoints();
        row.Refresh();
        RefreshDerived();
    }

    private bool CanRemoveBiasPoint() =>
        SelectedRow is { } row && SelectedBiasPointIndex is { } i && i >= 0 && i < row.Model.BiasCurve.Count;

    private int? _selectedBiasPointIndex;

    /// <summary>Which curve point the sub-editor's Remove acts on.</summary>
    public int? SelectedBiasPointIndex
    {
        get => _selectedBiasPointIndex;
        set
        {
            if (_selectedBiasPointIndex == value) return;
            _selectedBiasPointIndex = value;
            OnPropertyChanged();
            RemoveBiasPointCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Replaces one curve point and re-sorts. <b>The sort is the reason this is not a plain setter</b>
    /// — the reader sorts a curve on the way in because an interpolation over an unsorted one is a
    /// plausible wrong number rather than an error, and an edit that moved a point past its neighbour
    /// would put the in-memory curve in exactly the state the reader refuses to produce.
    /// </summary>
    internal void ReplaceBiasPoint(int index, PartBiasPoint point, string description)
    {
        if (SelectedRow is not { } row) return;
        if (index < 0 || index >= row.Model.BiasCurve.Count) return;

        string before = SnapshotJson();
        row.Model.BiasCurve[index] = point;
        SortCurve(row.Model);
        CommitEdit(before, description);

        // Refreshed IN PLACE, and the edited point stays selected wherever the sort put it (field
        // report, 2026-09-23: every digit typed into a bias moved the cell, and the list rebuilt with
        // the FIRST point selected, so the user scrolled back to their cell after each keystroke). The
        // point view models are index-based, so a re-sort needs no new ones — and not replacing them
        // leaves whatever cell the user clicked into alive.
        foreach (var p in BiasPoints) p.Refresh();
        SelectedBiasPointIndex = row.Model.BiasCurve.FindIndex(p => ReferenceEquals(p, point));
        OnPropertyChanged(nameof(SelectedBiasCurve));
        row.Refresh();
        RefreshDerived();
    }

    private static void SortCurve(PartLibraryRow row) =>
        row.BiasCurve.Sort((a, b) => a.BiasVolts.CompareTo(b.BiasVolts));

    /// <summary>The sub-editor's Import curve…. The picker is the host's, on
    /// <see cref="ImportTableRequested"/>'s terms; the import itself is <see cref="ImportBiasCurve"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanAddBiasPoint))]
    private void RequestBiasCurveImport() => ImportBiasCurveRequested?.Invoke();

    /// <summary>Asks the host for a <c>.csv</c> and then calls <see cref="ImportBiasCurve"/>.</summary>
    public Action? ImportBiasCurveRequested { get; set; }

    /// <summary>The sub-editor's Paste curve. The clipboard is the view's — it needs a top level —
    /// and it hands the text to <see cref="PasteBiasCurve"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanAddBiasPoint))]
    private void RequestBiasCurvePaste() => BiasCurvePasteRequested?.Invoke();

    /// <summary>Reads the clipboard and calls <see cref="PasteBiasCurve"/>. Set by the view.</summary>
    public Action? BiasCurvePasteRequested { get; set; }

    /// <summary>
    /// Replaces the selected row's bias curve with a supplier's capacitance-versus-DC-bias export
    /// (owner request, 2026-09-23). One undoable edit.
    /// </summary>
    public void ImportBiasCurve(string path)
    {
        string name = System.IO.Path.GetFileName(path);
        string text;
        try { text = System.IO.File.ReadAllText(path); }
        catch (Exception ex)
        {
            ImportReport = [$"{name} could not be read: {ex.Message}"];
            return;
        }
        ReplaceBiasCurve(text, name);
    }

    /// <summary>
    /// Replaces the selected row's bias curve with pasted text — tab-, comma- or space-separated
    /// columns of bias and capacitance, with the header row or a unit on each capacitance.
    /// </summary>
    /// <returns>False where the text held no curve, so a caller can let an ordinary paste through.</returns>
    public bool PasteBiasCurve(string text) => ReplaceBiasCurve(text, null);

    /// <summary>Every rule — columns, units, the checks against the row, the thinning — is
    /// <see cref="PartBiasCurveImport"/>'s, below the firewall. This snapshots, applies and reports.</summary>
    private bool ReplaceBiasCurve(string text, string? sourceName)
    {
        if (SelectedRow is not { } row || !CanAddBiasPoint) return false;

        var result = PartBiasCurveImport.Read(text, row.Model, sourceName);
        string source = sourceName ?? "Pasted curve";
        if (!result.HasCurve)
        {
            ImportReport = [$"{source}: nothing imported.", .. result.Notes];
            return false;
        }

        string before = SnapshotJson();
        int replaced = row.Model.BiasCurve.Count;
        row.Model.BiasCurve.Clear();
        row.Model.BiasCurve.AddRange(result.Points);
        CommitEdit(before, sourceName is null ? "Paste a bias curve" : $"Import {sourceName}");
        RebuildBiasPoints();
        row.Refresh();
        RefreshDerived();

        string kept = result.Points.Count == result.PointsRead
            ? $"{result.Points.Count} point(s)"
            : $"{result.PointsRead} points read, {result.Points.Count} kept — each dropped point is within " +
              $"{PartBiasCurveImport.ThinningTolerance:P1} of the line through its neighbours";
        ImportReport =
        [
            $"{source}: {row.PartNumber}'s bias curve is now {kept}" +
            (replaced > 0 ? $" (it had {replaced})." : "."),
            .. result.Notes,
        ];
        return true;
    }

    // ── save ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the library where it came from.
    /// </summary>
    /// <remarks>
    /// <b>R-rail24-3a: a refused row does not stop the write.</b> An editor that will not let you save
    /// work in progress is an editor people work around, and the refusal travels with the file — the
    /// strip says it here, and a run against the saved file says the same sentence because the
    /// validated read every run takes still refuses it by name.
    /// </remarks>
    private void Save()
    {
        try
        {
            PartLibraryIo.SaveToFile(FilePath, Working, validate: false);
        }
        catch (Exception ex)
        {
            SaveError?.Invoke($"Couldn't save the part library to '{FilePath}': {ex.Message}");
            return;
        }
        EndEditRun();
        UndoRedo.MarkSaved();
        PartLibrarySaved?.Invoke(FilePath);
    }

    /// <summary>Writes this library to a DIFFERENT <c>.crlib</c> and follows it from then on. The
    /// original file is left exactly as it was on disk — Save As is not a move.</summary>
    public void SaveAs(string newPath)
    {
        if (string.IsNullOrWhiteSpace(newPath)) return;
        try
        {
            PartLibraryIo.SaveToFile(newPath, Working, validate: false);
        }
        catch (Exception ex)
        {
            SaveError?.Invoke($"Couldn't save the part library to '{newPath}': {ex.Message}");
            return;
        }
        EndEditRun();
        FilePath = newPath;
        Working.BaseDirectory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(newPath)) ?? "";
        UndoRedo.MarkSaved();
        PartLibrarySavedAs?.Invoke(newPath);
    }

    // ── snapshot undo plumbing ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The library as bytes. <b>Unvalidated</b> — a library mid-edit is routinely in a state a
    /// validated write would refuse, and an undo stack that throws while you type is worse than no
    /// undo stack.
    /// </summary>
    internal string SnapshotJson() => PartLibraryIo.Serialize(Working, validate: false);

    /// <summary>The field the current run of keystrokes belongs to, and the entry that run is being
    /// collected into. Null whenever there is no run in flight — see <see cref="EndEditRun"/> for
    /// every way one ends.</summary>
    private string? _mergeKey;
    private PartLibrarySnapshotCommand? _mergeCommand;

    /// <param name="mergeKey">Identifies the FIELD being edited, so consecutive keystrokes in it
    /// become one undo entry. Null for an edit that is a gesture of its own — adding a row, removing
    /// a point — which also ends any run in flight.</param>
    internal void CommitEdit(string beforeJson, string description, string? mergeKey = null)
    {
        if (_suppressCommit) return;
        string afterJson = SnapshotJson();
        if (afterJson == beforeJson) { if (mergeKey is null) EndEditRun(); return; }

        if (mergeKey is not null && mergeKey == _mergeKey && _mergeCommand is { } running)
        {
            running.Amend(afterJson);
            // The stack's own state did not change — it still has the same top entry — but the
            // DOCUMENT did, and IsModified is derived from the top entry's identity, so it is already
            // right. Nothing to raise.
            return;
        }

        var command = new PartLibrarySnapshotCommand(this, beforeJson, afterJson, description);
        UndoRedo.Execute(command);
        _mergeKey     = mergeKey;
        _mergeCommand = mergeKey is null ? null : command;
    }

    /// <summary>
    /// Ends the run of keystrokes currently being coalesced, so the next edit starts a new undo
    /// entry.
    /// </summary>
    /// <remarks>
    /// <b>Called at every boundary, and missing one of them is the way this goes wrong.</b> A save
    /// records the top entry as the clean baseline — amending it afterwards would leave the document
    /// looking saved while it is not. An undo or redo replaces the whole library, so the entry the run
    /// was collecting into is no longer the top of the stack. And a different field, or a structural
    /// edit, is simply a different gesture.
    /// </remarks>
    private void EndEditRun()
    {
        _mergeKey     = null;
        _mergeCommand = null;
    }

    internal void ApplySnapshot(string json)
    {
        EndEditRun();
        _suppressCommit = true;
        try
        {
            int selected = SelectedRow is { } r ? Rows.IndexOf(r) : -1;
            Working = PartLibraryIo.Deserialize(json, Working.BaseDirectory, validate: false);
            RebuildRows();
            SelectedRow = selected >= 0 && selected < Rows.Count ? Rows[selected] : null;
            OnPropertyChanged(nameof(Working));
            OnPropertyChanged(nameof(Name));
        }
        finally { _suppressCommit = false; }
    }

    private void RebuildRows()
    {
        Rows.Clear();
        foreach (var row in Working.Rows) Rows.Add(new PartLibraryRowViewModel(this, row));
        RebuildBiasPoints();
        RefreshDerived();
    }

    private void RebuildBiasPoints()
    {
        BiasPoints.Clear();
        if (SelectedRow is { } row)
            for (int i = 0; i < row.Model.BiasCurve.Count; i++)
                BiasPoints.Add(new PartBiasPointViewModel(this, i));

        SelectedBiasPointIndex = BiasPoints.Count > 0 ? 0 : null;
        RemoveRowCommand.NotifyCanExecuteChanged();
        AddBiasPointCommand.NotifyCanExecuteChanged();
        RequestBiasCurveImportCommand.NotifyCanExecuteChanged();
        RequestBiasCurvePasteCommand.NotifyCanExecuteChanged();
        RemoveBiasPointCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedBiasCurve));
        OnPropertyChanged(nameof(SelectedMarkedCapacitanceFarads));
        OnPropertyChanged(nameof(SelectedRowTitle));
    }

    /// <summary>Re-asks everything the library computes for itself. Called after every committed
    /// edit, because a change to one row's f₀ moves that row's derived inductance, its disagreement,
    /// the file's refusal and — through a bias curve — the coverage counts.</summary>
    internal void RefreshDerived()
    {
        if (_referenced is { } referenced && CoverageSubject is { Length: > 0 })
            Coverage = Working.Coverage(referenced);

        OnPropertyChanged(nameof(Refusal));
        OnPropertyChanged(nameof(IsRefused));
        OnPropertyChanged(nameof(Disagreements));
        OnPropertyChanged(nameof(HasDisagreements));
        OnPropertyChanged(nameof(IndicativeCount));
        OnPropertyChanged(nameof(Coverage));
        OnPropertyChanged(nameof(HasCoverage));
        OnPropertyChanged(nameof(CoverageText));
        OnPropertyChanged(nameof(SelectedBiasCurve));
        OnPropertyChanged(nameof(SelectedMarkedCapacitanceFarads));
        AddBiasPointCommand.NotifyCanExecuteChanged();
        RequestBiasCurveImportCommand.NotifyCanExecuteChanged();
        RequestBiasCurvePasteCommand.NotifyCanExecuteChanged();
        RefreshSeeding();
    }
}

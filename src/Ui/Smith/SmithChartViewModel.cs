using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.Smith;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Matching;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Smith;

/// <summary>
/// The Smith Chart tool's view model — the generator panel, the chrome's numbers, and the edit history
/// the shell's Undo reaches (brief-smith-4-document-window.md, <c>docs/design/smith-chart.md</c> §5).
/// </summary>
/// <remarks>
/// <b>It draws nothing and it evaluates nothing.</b> Every number on the face of this window came out of
/// <see cref="SmithCascade"/>, which is below the firewall, and every refusal came out of
/// <see cref="SmithDesign.Refusal"/> or out of the evaluator's own exception. Nothing here re-derives a
/// quantity for display — that is <c>R-smith4-8</c>, and the reason is that a second spelling of
/// "the load impedance" is a second answer nobody can tell apart from the first.
///
/// <para><b>The chart pane is brief 5's and lives in the partial beside this file</b>
/// (<c>SmithChartViewModel.Chart.cs</c>): the plot host, the <c>Plot</c> the evaluator fills, the
/// gripper overlay and the drag loop. <b>The network pane is brief 6's</b> and lives in the other
/// partial (<c>SmithChartViewModel.Network.cs</c>): the projection onto the schematic renderer, the
/// element operations, the sliders and the mirror.</para>
///
/// <para><b>One gesture is one undo entry</b>, and the mechanism is <see cref="SmithSnapshotCommand"/>:
/// every mutation goes through <see cref="Edit"/>, which captures the whole design before and after.
/// Conjugate is therefore one entry that one Undo unwinds completely, which is what <c>R-smith4-7</c>
/// asks for and what the gate counts.</para>
/// </remarks>
public sealed partial class SmithChartViewModel : ObservableObject
{
    // ── construction ─────────────────────────────────────────────────────────

    public SmithChartViewModel(SmithDesign? design = null)
    {
        _design = design ?? NewScratchDesign();

        // BEFORE RefreshDerived, which rebuilds the chart: the host has to exist by then, and a
        // field initializer cannot build it because AddPlot is a call on another initialized field.
        BuildChartHost();

        // The inspector is this document's way IN for reference material (brief 12): its Add button
        // and its trace cards are the overlay UI, and its PlotStructureChanged is what writes the
        // result back. Before the first ReloadOverlays, because a document that opens with overlays
        // must not harvest an empty plot over them.
        WireOverlayInspector();

        RebuildRows();
        ReloadOverlays();
        RefreshDerived();

        // The document's dirty mark IS the stack's own saved-position marker. Nothing else decides it,
        // which is what makes "undo back to the last save and the bullet goes away" true for free.
        UndoRedo.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UndoRedoStack.IsModified)) DirtyChanged?.Invoke();
        };
    }

    /// <summary>
    /// What Tools ▸ Smith Chart opens on: <b>one generator row, 50 Ω at 2 GHz, and a design frequency
    /// that lands on it.</b>
    /// </summary>
    /// <remarks>
    /// Not an empty document, on harmonicaRF's own reasoning (<c>NewHarmonica</c>: "the document opens
    /// on a real, converging device rather than an empty canvas"). An empty generator table is a
    /// <see cref="SmithDesign.Refusal"/> the moment the window appears, so a blank scratch document
    /// would open showing its own error message — and the user's first act would be to dismiss a
    /// complaint about a document they had not yet written.
    /// </remarks>
    public static SmithDesign NewScratchDesign()
    {
        var d = new SmithDesign();
        d.Generator.Rows.Add(new SmithGeneratorRow(2e9, 50.0, 0.0));
        d.Chart.DesignFrequencyHz = 2e9;
        return d;
    }

    // ── the document ─────────────────────────────────────────────────────────

    private SmithDesign _design;

    /// <summary>The `.csmith` this window is editing.</summary>
    public SmithDesign Design => _design;

    /// <summary>
    /// The folder the document lives in — what an S1P/S2P element's relative <c>FileRef</c> resolves
    /// against. Null for a scratch document, which is what the evaluator reads as "the process's
    /// working directory".
    /// </summary>
    public string? DocumentDirectory
    {
        get => _documentDirectory;
        set
        {
            _documentDirectory = value;
            // An overlay's reference is relative to THIS folder, so every one of them resolves
            // differently once it is known — which is exactly what happens on a Save As, and on the
            // ordinary open, where the folder arrives after the design. FORCED for that reason: the
            // LIST is the same, and the answers are not.
            ReloadOverlays(force: true);
            RefreshDerived();
        }
    }
    private string? _documentDirectory;

    /// <summary>The edit history the shell's Ctrl/Cmd+Z reaches through the document
    /// (<c>R-smith4-1</c>).</summary>
    public UndoRedoStack UndoRedo { get; } = new();

    /// <summary>Raised when <see cref="IsDirty"/> may have changed — the document's cue to re-title.</summary>
    public event Action? DirtyChanged;

    /// <summary>True when the current undo position differs from the last save.</summary>
    public bool IsDirty => UndoRedo.IsModified;

    /// <summary>Called after the document has been written — the clean baseline moves here.</summary>
    public void MarkSaved() => UndoRedo.MarkSaved();

    // ── the edit seam ────────────────────────────────────────────────────────

    /// <summary>
    /// One committed edit: snapshot, mutate, snapshot, push. <b>Every mutation in this window goes
    /// through here</b>, which is the single place "one gesture is one undo entry" is enforced.
    /// </summary>
    /// <remarks>
    /// An edit that changed nothing pushes nothing — a re-typed identical value is not an undo entry,
    /// and a stack full of no-ops is how "eight edits took fourteen undos" happens (the Match
    /// Designer's slider write-back defect, <c>src/Ui/RESOLVED.md</c>).
    /// </remarks>
    internal void Edit(string description, Action mutate)
    {
        // A NOTE lives until the next committed edit and no longer. It says what the tool decided
        // about the edit before it — a line's reference frequency left behind by a retune, a name
        // already taken, a file element nobody finished choosing a file for — and the strip's own job
        // the rest of the time is to STATE NUMBERS. One that outlived its occasion would hide the
        // reading indefinitely, which is the same failure as a refusal that does not clear.
        // Cleared BEFORE the mutation, so a note raised about THIS edit (R-smith6-3's F_ref sentence,
        // which is emitted after the edit lands) survives it.
        StripNotice = null;

        string before = SmithDesignIo.SerializeUnvalidated(_design);
        mutate();
        string after = SmithDesignIo.SerializeUnvalidated(_design);

        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            // Nothing changed, but the control that was typed into may be showing text the model does
            // not hold. Re-read everything so it snaps back.
            RebuildRows();
            RefreshDerived();
            return;
        }

        UndoRedo.Execute(new SmithSnapshotCommand(this, before, after, description));
    }

    /// <summary>
    /// Restores a whole design from one of <see cref="SmithSnapshotCommand"/>'s snapshots.
    /// </summary>
    /// <remarks>
    /// <b>The design object is REPLACED, not patched.</b> <see cref="SmithDesign.Elements"/> and
    /// <see cref="SmithGenerator.Rows"/> are get-only collections, so there is no assignment that would
    /// restore them field by field — and a patch that walked them would be a second, partial copy of
    /// the reader. Replacing means every row view model is rebuilt, which is why they hold a reference
    /// to the row rather than an index into the table.
    /// </remarks>
    internal void ApplySnapshot(string json)
    {
        _design = SmithDesignIo.DeserializeUnvalidated(json);
        RebuildRows();

        // THE DESIGN OBJECT IS REPLACED, so the configs the overlay traces were built from are gone
        // with it and the traces have to be built again. This is the one place they are: everywhere
        // else the instances are carried across the rebuild, because the inspector's cards and each
        // trace's markers hold the OBJECT (R-smith12-5a).
        ReloadOverlays();
        RefreshDerived();
        DesignChanged?.Invoke();
    }

    /// <summary>Raised after any committed edit or undo — briefs 5 and 6 rebuild their panes on it.</summary>
    public event Action? DesignChanged;

    // ── the generator table (R-smith4-7) ─────────────────────────────────────

    /// <summary>The table, one view model per document row, in the document's own order.</summary>
    public ObservableCollection<SmithGeneratorRowViewModel> GeneratorRows { get; } = [];

    private SmithGeneratorRowViewModel? _selectedRow;

    /// <summary>Which row <c>[−]</c> removes. Null when the table is empty.</summary>
    public SmithGeneratorRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (ReferenceEquals(_selectedRow, value)) return;
            _selectedRow = value;
            OnPropertyChanged();
            RemoveGeneratorRowCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Rebuilds the row view models from the document's own table.
    /// </summary>
    /// <remarks>
    /// <b>Selection is kept by FREQUENCY and not by reference or by index</b>, because every committed
    /// edit replaces the whole design (see <see cref="ApplySnapshot"/>) and every frequency edit may
    /// re-sort the table. A reference would never match after a restore; an index would follow the
    /// POSITION rather than the row, so editing a frequency would leave the selection on whichever row
    /// slid into the old slot.
    /// </remarks>
    private void RebuildRows()
    {
        double? keepHz = SelectedRow?.Row.FrequencyHz;

        GeneratorRows.Clear();
        foreach (var row in _design.Generator.Rows)
            GeneratorRows.Add(new SmithGeneratorRowViewModel(this, row));

        _selectedRow = (keepHz is { } hz
                            ? GeneratorRows.FirstOrDefault(r => r.Row.FrequencyHz == hz)
                            : null)
                    ?? GeneratorRows.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedRow));
        RemoveGeneratorRowCommand.NotifyCanExecuteChanged();
        ReimportGeneratorCommand.NotifyCanExecuteChanged();

    }

    /// <summary>
    /// One cell of one row, committed. <paramref name="mutate"/> returns false when it could not parse
    /// what the user typed, and a false answer writes nothing and reports nothing: a half-typed field
    /// is an ordinary state of a live editor, and the cell snaps back to what is stored.
    /// </summary>
    /// <remarks>
    /// <b>The table is re-sorted after every committed frequency edit</b>, because "rows are sorted by
    /// frequency" is what makes <see cref="SmithGenerator.Span"/> a span an interpolator can be asked
    /// about (§3.1). A duplicate frequency is <see cref="SmithGenerator.Refusal"/>'s sentence in the
    /// status strip, naming the frequency — <c>R-smith1-3</c>'s rule, surfaced rather than restated.
    /// </remarks>
    internal void EditGeneratorRow(SmithGeneratorRowViewModel row, string description,
                                   Func<SmithGeneratorRow, bool> mutate)
    {
        bool applied = false;
        Edit(description, () =>
        {
            applied = mutate(row.Row);
            if (applied) SortGeneratorRows();
        });

        if (!applied) row.NotifyAll();
    }

    /// <summary>
    /// Stable sort by frequency, in place.
    /// </summary>
    /// <remarks>
    /// <b>Stable, so that two rows the user has just put at one frequency keep the order they were
    /// typed in</b> — the refusal then names that frequency, and the row the user is looking at is
    /// still where they left it. An unstable sort would move the offending pair around under the
    /// message complaining about them.
    /// </remarks>
    private void SortGeneratorRows()
    {
        var sorted = _design.Generator.Rows.OrderBy(r => r.FrequencyHz).ToList();
        _design.Generator.Rows.Clear();
        foreach (var r in sorted) _design.Generator.Rows.Add(r);
    }

    /// <summary>
    /// <c>[+]</c> — a new row, at a frequency that does not collide with an existing one.
    /// </summary>
    /// <remarks>
    /// Ten percent above the top of the table, or 1 GHz into an empty one. <b>Not a duplicate of the
    /// last row's frequency</b>: a new row that is born refused would make the button read as broken,
    /// and the refusal it would raise is about the table rather than about anything the user did.
    /// </remarks>
    [RelayCommand]
    private void AddGeneratorRow()
    {
        SmithGeneratorRow? added = null;

        Edit("Add generator row", () =>
        {
            var rows = _design.Generator.Rows;
            double f = rows.Count == 0 ? 1e9 : rows[^1].FrequencyHz * 1.1;
            double r = rows.Count == 0 ? 50.0 : rows[^1].ResistanceOhm;
            double x = rows.Count == 0 ?  0.0 : rows[^1].ReactanceOhm;

            added = new SmithGeneratorRow(f, r, x);
            rows.Add(added);
            SortGeneratorRows();
        });

        if (added is not null)
            SelectedRow = GeneratorRows.FirstOrDefault(v => v.Row.FrequencyHz == added.FrequencyHz);
    }

    private bool CanRemoveGeneratorRow() => SelectedRow is not null && GeneratorRows.Count > 1;

    /// <summary>
    /// <c>[−]</c> — removes the selected row.
    /// </summary>
    /// <remarks>
    /// <b>The last row cannot be removed.</b> An empty generator table is a refusal (§3.1: "this tool
    /// starts from an impedance"), so a button that could produce one is a button whose only effect is
    /// to break the document — disabled is the honest state, and the user deletes the document rather
    /// than emptying it.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRemoveGeneratorRow))]
    private void RemoveGeneratorRow()
    {
        if (SelectedRow is not { } row) return;

        Edit("Remove generator row", () => _design.Generator.Rows.Remove(row.Row));
    }

    /// <summary>
    /// Conjugate — <b>one click, one undo entry, and not a persistent flag</b> (§3.1, <c>R-smith4-7</c>).
    /// </summary>
    /// <remarks>
    /// A flag would mean the number in the table and the number the tool uses disagree, and there is no
    /// way to display that which does not eventually mislead someone. The arithmetic is
    /// <see cref="SmithGenerator.Conjugate"/>'s, below the firewall, and applying it twice is the
    /// identity — so a user who pressed it by mistake presses it again, and Undo takes the whole thing
    /// in one step either way.
    /// </remarks>
    [RelayCommand]
    private void Conjugate() => Edit("Conjugate generator", _design.Generator.Conjugate);

    // ── .s1p import (R-smith4-7) ─────────────────────────────────────────────

    /// <summary>The imported file's path, for display. Empty when the table was typed rather than
    /// imported.</summary>
    public string SourcePathDisplay => _design.Generator.SourcePath ?? "";

    /// <summary>Whether there is a path to re-import from.</summary>
    public bool HasSourcePath => !string.IsNullOrWhiteSpace(_design.Generator.SourcePath);

    /// <summary>
    /// Import — the rows <b>replace</b> the table and the path is recorded as provenance (§3.1).
    /// </summary>
    /// <remarks>
    /// <b>The picker is the view's; this takes a resolved path</b>, which is what lets the gate drive
    /// the whole import with no display. The read itself is <see cref="SmithGeneratorImport"/>'s, below
    /// the firewall — this adds no second Touchstone interpretation and no second conversion of S₁₁ to
    /// an impedance.
    ///
    /// <para><b>Nothing resolves the path at load</b>, which is the opposite choice from an S1P
    /// element's <c>FileRef</c> and deliberately so: the generator is part of the design, and a design
    /// that stops opening because a file moved is a design that was never portable.</para>
    /// </remarks>
    /// <returns>Null on success, or the refusal's own sentence.</returns>
    public string? ImportGeneratorFrom(string path)
    {
        try
        {
            Edit($"Import generator from {Path.GetFileName(path)}",
                 () => SmithGeneratorImport.ImportInto(_design.Generator, path));
            ImportFailed = null;
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private bool CanReimportGenerator() => HasSourcePath;

    /// <summary>Re-import — repeats the read from <see cref="SmithGenerator.SourcePath"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanReimportGenerator))]
    private void ReimportGenerator()
    {
        if (_design.Generator.SourcePath is not { Length: > 0 } path) return;
        if (ImportGeneratorFrom(path) is { } error) ImportFailed = error;
    }

    /// <summary>The last import refusal, shown in the status strip. Cleared by the next successful
    /// read.</summary>
    public string? ImportFailed
    {
        get => _importFailed;
        set
        {
            if (_importFailed == value) return;
            _importFailed = value;
            OnPropertyChanged();
            RefreshDerived();
        }
    }
    private string? _importFailed;

    // ── the chart's two settings (R-smith4-7) ────────────────────────────────

    /// <summary>
    /// The chart's reference impedance — <b>one, real and document-wide</b> (§3.4). Γ is taken against
    /// it, the grid is the ordinary fixed Smith grid, and every overlay is renormalized to it.
    /// </summary>
    public string ChartZ0Entry
    {
        get => MatchValueFormat.FormatWithUnit(_design.Chart.Z0Ohm, MatchQuantity.Resistance, "Ω", 6);
        set
        {
            bool ok = MatchValueFormat.TryParseWithUnit(value, MatchQuantity.Resistance, "Ω",
                                                        out double z0, out _) && z0 > 0;
            if (ok) Edit("Edit chart Z0", () => _design.Chart.Z0Ohm = z0);
            OnPropertyChanged();
            if (!ok) RefreshDerived();
        }
    }

    /// <summary>
    /// The design frequency — what the trajectories are drawn at and what the strip reports (§3.6).
    /// </summary>
    /// <remarks>
    /// <b>It is free, but it must lie inside the generator table's span</b>, because Z_gen is
    /// interpolated and never extrapolated. One outside it turns this field red and puts
    /// <see cref="SmithDesign.Refusal"/>'s sentence — which names the span, with its numbers in it — in
    /// the status strip. <b>That refusal is brief 2's, surfaced rather than re-derived</b>: a second
    /// copy of the rule here would be a second answer about which frequencies are legal.
    /// </remarks>
    public string DesignFrequencyEntry
    {
        get => MatchValueFormat.FormatWithUnit(_design.Chart.DesignFrequencyHz, MatchQuantity.Frequency,
                                               MatchValueFormat.AutoUnit, 6);
        set
        {
            bool ok = MatchValueFormat.TryParseWithUnit(value, MatchQuantity.Frequency, "GHz",
                                                        out double f, out _) && f > 0;
            if (ok)
            {
                Edit("Edit design frequency", () => _design.Chart.DesignFrequencyHz = f);

                // A line's F_ref stays where it was placed, on purpose (R-smith2-3) — and the ONE time
                // that is worth saying out loud is the first time a frequency edit leaves one behind
                // (R-smith6-3). After the edit, so the note is about where the design now is.
                NoteStrandedReferenceFrequencies();
            }
            OnPropertyChanged();
            if (!ok) RefreshDerived();
        }
    }

    /// <summary>True when the design frequency is outside the table's span — what turns the field red
    /// (§5.3's "the offending input turning red").</summary>
    public bool IsDesignFrequencyInvalid { get; private set; }

    // ── the status strip (R-smith4-8) ────────────────────────────────────────

    /// <summary>
    /// What the strip says at the design frequency: the load impedance in R + jX, Γ in polar and
    /// rectangular, VSWR, and the mismatch loss in dB.
    /// </summary>
    /// <remarks>
    /// <b>Every number is the one the evaluator produced</b>, formatted by <see cref="MatchValueFormat"/>
    /// (<c>R-smith4-8</c>). Γ is <see cref="SmithCascade.Gamma"/>'s against the chart's own Z₀; the load
    /// is the LAST node of <see cref="SmithCascade.Evaluate"/>'s walk, which with no elements placed is
    /// the generator itself.
    /// </remarks>
    public string StatusLine { get; private set; } = "";

    /// <summary>The first thing wrong with the document, or null — the strip's other half.</summary>
    public string? Refusal { get; private set; }

    /// <summary>True when <see cref="Refusal"/> has something to say.</summary>
    public bool HasRefusal => Refusal is not null;

    /// <summary>
    /// Re-reads everything derived from the document. Called after every committed edit, every undo and
    /// every snapshot restore.
    /// </summary>
    private void RefreshDerived()
    {
        Refusal    = ComputeRefusal();
        StatusLine = ComputeStatusLine();

        // The chart is derived from the design exactly as the status strip is, from the same
        // evaluation, and is refreshed on the same channel — including on every pointer move of a
        // gripper drag, which is what makes the strip's numbers and the picture agree at every
        // instant of it. See SmithChartViewModel.Chart.cs.
        RebuildChart();

        // The network strip is derived from the SAME design on the SAME channel, so the picture below
        // the chart, the curves on it and the numbers in the strip are three views of one evaluation
        // rather than three that happen to agree. See SmithChartViewModel.Network.cs.
        RebuildNetwork();

        // A CLAMPED BAND is a STANDING CONDITION rather than an occasion (R-smith9-4), so it is
        // re-raised on every refresh and not once: it stays true for as long as the band asks for
        // more than the generator table can answer for, and a note that appeared once and then went
        // away would leave the picture unexplained. It never displaces a note somebody else just
        // raised about the edit that is landing — the strip is ONE line, and the newer sentence is
        // the one about what just happened.
        if (Scene.BandClampNote is { } clamped && _stripNotice is null) StripNotice = clamped;

        // AND SO IS A REFERENCE THAT DOES NOT RESOLVE (R-smith8-2). With the Overlays panel gone
        // there is no row to mark, and the sentence still has to be said: the document opened, the
        // rest of the chart drew, and one piece of reference material is missing. It never displaces
        // a note somebody just raised about the edit that is landing — the strip is ONE line, and
        // the newer sentence is the one about what just happened.
        if (UnresolvedOverlayNote is { } missing && _stripNotice is null) StripNotice = missing;

        IsDesignFrequencyInvalid = _design.Generator.Rows.Count > 1
            && _design.Generator.Span is { } span
            && !(_design.Chart.DesignFrequencyHz >= span.StartHz
                 && _design.Chart.DesignFrequencyHz <= span.StopHz);

        OnPropertyChanged(nameof(Refusal));
        OnPropertyChanged(nameof(HasRefusal));
        OnPropertyChanged(nameof(HasStripNotice));
        OnPropertyChanged(nameof(ShowStatusLine));
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(MirrorNetwork));
        OnPropertyChanged(nameof(IsDesignFrequencyInvalid));
        OnPropertyChanged(nameof(ChartZ0Entry));
        OnPropertyChanged(nameof(DesignFrequencyEntry));
        OnPropertyChanged(nameof(SourcePathDisplay));
        OnPropertyChanged(nameof(HasSourcePath));

        // Brief 9's two cards. They are refreshed on the same channel as everything else, which is
        // what makes an UNDO of a Q drag move the number in the panel as well as the arcs.
        OnPropertyChanged(nameof(ConstantQEnabled));
        OnPropertyChanged(nameof(ConstantQEntry));
        OnPropertyChanged(nameof(SweepEnabled));
        OnPropertyChanged(nameof(SweepStartEntry));
        OnPropertyChanged(nameof(SweepStopEntry));
        OnPropertyChanged(nameof(SweepPointsEntry));
        ReimportGeneratorCommand.NotifyCanExecuteChanged();
        foreach (var row in GeneratorRows) row.NotifyAll();
    }

    private string? ComputeRefusal()
    {
        if (ImportFailed is { Length: > 0 } imported) return imported;

        // A PIN is a live drag saying it has reached a limit, with the parameter and the limit in
        // the sentence (R-smith3-4). It sits in the refusal half rather than beside it because two
        // rows of text, one in the warning colour, read as two different problems — §5.3's own rule,
        // and the same reason an import failure replaces the reading rather than joining it.
        if (DragPin is { Length: > 0 } pinned) return pinned;

        return _design.Refusal();
    }

    private string ComputeStatusLine()
    {
        double f = _design.Chart.DesignFrequencyHz;

        // EVERY NUMBER BELOW IS SmithReadings' (R-smith10-1). The strip formats; it does not
        // compute — `circuitrf smith` prints the same five quantities about the same document, and a
        // VSWR or a mismatch loss derived twice is two chances to be wrong in a quantity whose
        // wrong value looks entirely ordinary.
        SmithReading r;
        try
        {
            r = SmithReadings.At(_design, f, DocumentDirectory);
        }
        catch (Exception)
        {
            // The refusal half of the strip is already saying what is wrong, with its numbers in it.
            // A second sentence here, in the ordinary colour, would read as a second problem.
            return "";
        }

        var    load       = r.LoadZ;
        var    gamma      = r.Gamma;
        double mag        = gamma.Magnitude;
        double vswr       = r.Vswr;
        double mismatchDb = r.MismatchDb;

        string fText = MatchValueFormat.FormatWithUnit(f, MatchQuantity.Frequency, MatchValueFormat.AutoUnit, 5);
        string rText = MatchValueFormat.Significant(load.Real, 4);
        string xText = MatchValueFormat.Significant(Math.Abs(load.Imaginary), 4);
        string sign  = load.Imaginary < 0 ? "−" : "+";

        string angle = MatchValueFormat.Significant(
            gamma.Phase * 180.0 / Math.PI, 3);

        return $"{fText} · load {rText} {sign} j{xText} Ω"
             + $" · Γ {MatchValueFormat.Significant(mag, 4)} ∠{angle}°"
             + $" ({MatchValueFormat.Significant(gamma.Real, 4)}, {MatchValueFormat.Significant(gamma.Imaginary, 4)})"
             + $" · VSWR {Fmt(vswr)}"
             // A DECIBEL IS NOT A COMPONENT VALUE: MatchValueFormat.Decibels, not Significant, or a
             // perfect match reads 0.000004551 dB beside a VSWR of 1.002. Its own remarks say why.
             + $" · mismatch {MatchValueFormat.Decibels(mismatchDb)} dB";

        static string Fmt(double v) =>
            double.IsFinite(v) ? MatchValueFormat.Significant(v, 4) : MatchValueFormat.InfinityGlyph;
    }

    // ── the three regions' splitters (R-smith4-3) ────────────────────────────

    /// <summary>
    /// The generator column's share of the top region, as a fraction — and the chart's is one minus
    /// it. The chart / network split is <see cref="SplitterMain"/>.
    /// </summary>
    /// <remarks>
    /// <b>A splitter drag is not an edit.</b> It writes the fraction into the document's
    /// <see cref="SmithView"/> block — so a document opened tomorrow opens where it was left, which
    /// is the `.cdd`'s own convention — but it pushes NO undo entry and raises no dirty mark.
    /// Prompting to save because a divider moved is how a close prompt stops meaning anything; the
    /// position rides along on the next real save.
    ///
    /// <para><b>These are fractions and not <c>GridLength</c>s, and the view applies them in code.</b>
    /// A <c>RowDefinition</c> is not in the logical tree, so it inherits no DataContext and a binding
    /// on its <c>Height</c> resolves against nothing — silently, with the splitter simply not
    /// remembering anything. Nothing else in this application binds a definition's size for that
    /// reason, and this window is not the place to find out why.</para>
    /// </remarks>
    public double SplitterSide
    {
        get => Math.Clamp(_design.View.SplitterSide, MinSplit, 1.0 - MinSplit);
        set => _design.View.SplitterSide = Math.Clamp(value, MinSplit, 1.0 - MinSplit);
    }

    /// <summary>The chart region's share of the document's height — <b>the large majority</b>,
    /// because the chart is the tool (§5.2). The network strip takes the rest.</summary>
    public double SplitterMain
    {
        get => Math.Clamp(_design.View.SplitterMain, MinSplit, 1.0 - MinSplit);
        set => _design.View.SplitterMain = Math.Clamp(value, MinSplit, 1.0 - MinSplit);
    }

    /// <summary>The narrowest either side of a split may be recorded as. A stored zero would reopen
    /// the document with a region collapsed to nothing and no obvious way to get it back.</summary>
    private const double MinSplit = 0.05;

    // ── loading ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Adopts a design read from disk: the history is reset and the clean baseline is here, because
    /// this IS the file's own content and there is nothing about it to undo.
    /// </summary>
    public void Load(SmithDesign design, string? documentDirectory)
    {
        _design            = design;
        _documentDirectory = documentDirectory;
        _importFailed      = null;

        UndoRedo.Reset();
        UndoRedo.MarkSaved();

        RebuildRows();
        RefreshDerived();
        DesignChanged?.Invoke();
        DirtyChanged?.Invoke();
    }

    /// <summary>The design as it goes on disk.</summary>
    /// <exception cref="InvalidDataException">The document is not well formed — the sentence names the
    /// element, the frequency or the setting, and <see cref="SmithDesignIo.Serialize"/> refuses rather
    /// than writing a file whose only symptom is that it will not open next week.</exception>
    public string Serialize()
    {
        // THE OVERLAY CARDS' OWN SETTINGS RIDE ALONG HERE (R-smith12-5b). An add or a remove is one
        // undo entry and is harvested when it happens; a colour, a line width or a marker glyph is
        // not an entry — an entry per card keystroke is the Match Designer's "eight edits took
        // fourteen undos" by a slower route — so this is where they reach the file.
        HarvestOverlays();
        return SmithDesignIo.Serialize(_design);
    }
}

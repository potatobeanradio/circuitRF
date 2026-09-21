using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CircuitRF.Design.Matching;
using CircuitRF.Design.Smith;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Smith;

/// <summary>Which of the two ladders the editor is showing.</summary>
public enum SmithPreferredLadder { Capacitors = 0, Inductors = 1 }

/// <summary>Text (paste a whole list) or Rows (one value at a time) — the VAR editor's own pair.</summary>
public enum SmithPreferredValuesMode { Text = 0, Rows = 1 }

/// <summary>
/// One value, as a row. A string rather than a number, because the row IS the line in the text
/// buffer and the two modes must not disagree about what the list currently says.
/// </summary>
public sealed partial class SmithPreferredValueRowViewModel : ObservableObject
{
    private readonly SmithPreferredValuesViewModel _owner;

    internal SmithPreferredValueRowViewModel(SmithPreferredValuesViewModel owner, int index, string entry)
    {
        _owner = owner;
        Index  = index;
        _entry = entry;
    }

    /// <summary>Which line of the working buffer this row is.</summary>
    public int Index { get; }

    [ObservableProperty] private string _entry;

    /// <summary>Writes this row back into the buffer — called when the box loses focus or takes an
    /// Enter, the VAR editor's own commit points.</summary>
    public void Commit() => _owner.SetLine(Index, Entry);

    [RelayCommand]
    private void Remove() => _owner.RemoveLine(Index);
}

/// <summary>
/// The preferred-value list editor (<c>docs/design/smith-chart.md</c> §5.6a; owner instruction,
/// 2026-09-21): paste a list in, or edit it one value at a time, and put it back to what circuitRF
/// ships.
/// </summary>
/// <remarks>
/// <b>The working state is TEXT, in one buffer per ladder, and the rows are a view of it.</b> Two
/// independent representations — a list of numbers for the rows and a string for the paste box —
/// would need a sync in each direction and would disagree the first time one of them refused an
/// entry the other had already taken. So a row edit rewrites its line, Add appends one, and the
/// paste box IS the buffer.
///
/// <para><b>Nothing is stored until Apply</b>, and Apply commits both ladders at once. Writing
/// through on every committed row would put a preferences write and — worse — an undo entry on the
/// open design behind every keystroke of a table edit, which is the Match Designer's
/// entry-per-notification defect wearing a different hat (<c>src/Ui/Match/RESOLVED.md</c>).</para>
///
/// <para><b>Revert writes NULL rather than the shipped numbers</b> — see
/// <see cref="SmithPreferredValueStore"/>. It applies immediately, because a revert nobody pressed
/// Apply after would be a button that appeared to do nothing.</para>
/// </remarks>
public sealed partial class SmithPreferredValuesViewModel : ObservableObject
{
    private readonly Action? _onApplied;

    private string _capacitorText;
    private string _inductorText;

    public SmithPreferredValuesViewModel(Action? onApplied = null)
    {
        _onApplied     = onApplied;
        _capacitorText = SmithPreferredValues.Format(SmithPreferredValueStore.Capacitors,
                                                     MatchQuantity.Capacitance);
        _inductorText  = SmithPreferredValues.Format(SmithPreferredValueStore.Inductors,
                                                     MatchQuantity.Inductance);
        RebuildRows();
    }

    // ── which ladder ─────────────────────────────────────────────────────────

    [ObservableProperty] private SmithPreferredLadder _ladder = SmithPreferredLadder.Capacitors;

    public bool IsCapacitors => Ladder == SmithPreferredLadder.Capacitors;
    public bool IsInductors  => Ladder == SmithPreferredLadder.Inductors;

    partial void OnLadderChanged(SmithPreferredLadder oldValue, SmithPreferredLadder newValue)
    {
        OnPropertyChanged(nameof(IsCapacitors));
        OnPropertyChanged(nameof(IsInductors));
        OnPropertyChanged(nameof(Buffer));
        OnPropertyChanged(nameof(Hint));
        RebuildRows();
        Validate();
    }

    [RelayCommand] private void ShowCapacitors() => Ladder = SmithPreferredLadder.Capacitors;
    [RelayCommand] private void ShowInductors()  => Ladder = SmithPreferredLadder.Inductors;

    /// <summary>The quantity the ladder on show is — what parses and formats its entries.</summary>
    public MatchQuantity Quantity
        => IsCapacitors ? MatchQuantity.Capacitance : MatchQuantity.Inductance;

    /// <summary>The unit a bare number in this ladder is read as — and the smallest one a formatted
    /// entry reaches for. One statement of it, in <see cref="SmithPreferredValues.BareUnit"/>, so
    /// the hint, the parse and the spelling cannot come to disagree.</summary>
    public string DefaultUnit => SmithPreferredValues.BareUnit(Quantity);

    /// <summary>The line under the editor — what the field takes, in the field's own terms.</summary>
    public string Hint => IsCapacitors
        ? "One capacitance per line, or separated by commas. A bare number is read as pF."
        : "One inductance per line, or separated by commas. A bare number is read as nH.";

    // ── text or rows ─────────────────────────────────────────────────────────

    [ObservableProperty] private SmithPreferredValuesMode _activeMode = SmithPreferredValuesMode.Text;

    public bool IsTextMode => ActiveMode == SmithPreferredValuesMode.Text;
    public bool IsRowsMode => ActiveMode == SmithPreferredValuesMode.Rows;

    partial void OnActiveModeChanged(SmithPreferredValuesMode oldValue, SmithPreferredValuesMode newValue)
    {
        OnPropertyChanged(nameof(IsTextMode));
        OnPropertyChanged(nameof(IsRowsMode));
        RebuildRows();
    }

    [RelayCommand] private void SetTextMode() => ActiveMode = SmithPreferredValuesMode.Text;
    [RelayCommand] private void SetRowsMode() => ActiveMode = SmithPreferredValuesMode.Rows;

    // ── the buffer, and the rows over it ─────────────────────────────────────

    /// <summary>The working list for the ladder on show, as text. Two-way bound in Text mode.</summary>
    public string Buffer
    {
        get => IsCapacitors ? _capacitorText : _inductorText;
        set
        {
            if (Buffer == value) return;
            if (IsCapacitors) _capacitorText = value;
            else              _inductorText  = value;
            OnPropertyChanged();
            Validate();
        }
    }

    public ObservableCollection<SmithPreferredValueRowViewModel> Rows { get; } = [];

    private void RebuildRows()
    {
        Rows.Clear();
        var lines = Lines();
        for (int i = 0; i < lines.Count; i++)
            Rows.Add(new SmithPreferredValueRowViewModel(this, i, lines[i]));
        OnPropertyChanged(nameof(RowCountLabel));
    }

    private List<string> Lines()
        => [.. Buffer.Split('\n').Select(l => l.Trim('\r').Trim()).Where(l => l.Length > 0)];

    /// <summary>Rewrites one line. An emptied row is removed rather than left as a blank line, which
    /// is what the × button does and what a user who selects-all-and-deletes means.</summary>
    internal void SetLine(int index, string text)
    {
        var lines = Lines();
        if (index < 0 || index >= lines.Count) return;

        if (string.IsNullOrWhiteSpace(text)) lines.RemoveAt(index);
        else                                 lines[index] = text.Trim();

        Write(lines);
    }

    internal void RemoveLine(int index)
    {
        var lines = Lines();
        if (index < 0 || index >= lines.Count) return;
        lines.RemoveAt(index);
        Write(lines);
    }

    /// <summary>Appends a blank row for the user to type into — seeded with the ladder's own unit so
    /// what is expected is on screen rather than in a placeholder they have to trigger.</summary>
    [RelayCommand]
    private void AddRow()
    {
        var lines = Lines();
        lines.Add($"1 {DefaultUnit}");
        Write(lines);
    }

    /// <summary>
    /// Re-sorts and re-spells the working list — <b>the same normalization Apply performs</b>, run
    /// early so a pasted column can be eyeballed before it is committed.
    /// </summary>
    [RelayCommand]
    private void Tidy()
    {
        if (!SmithPreferredValues.TryParse(Buffer, Quantity, DefaultUnit, out var values, out string? err))
        {
            Error = err;
            return;
        }

        Buffer = SmithPreferredValues.Format(values, Quantity);
        RebuildRows();
    }

    private void Write(List<string> lines)
    {
        Buffer = string.Join(Environment.NewLine, lines) + (lines.Count > 0 ? Environment.NewLine : "");
        RebuildRows();
    }

    public string RowCountLabel
    {
        get
        {
            int n = Rows.Count;
            return n == 1 ? "1 value" : $"{n} values";
        }
    }

    // ── what is wrong with it ────────────────────────────────────────────────

    [ObservableProperty] private string? _error;

    public bool HasError => Error is not null;

    partial void OnErrorChanged(string? oldValue, string? newValue)
        => OnPropertyChanged(nameof(HasError));

    private void Validate()
    {
        Error = SmithPreferredValues.TryParse(Buffer, Quantity, DefaultUnit, out _, out string? err)
            ? null
            : err;
        OnPropertyChanged(nameof(RowCountLabel));
    }

    // ── committing ───────────────────────────────────────────────────────────

    /// <summary>
    /// Stores both ladders. <b>Neither is written unless both parse</b> — a half-applied pair would
    /// leave the user's capacitors replaced and their inductors not, with one error message to
    /// explain it and no way to tell which half landed.
    /// </summary>
    [RelayCommand]
    private void Apply()
    {
        if (!SmithPreferredValues.TryParse(_capacitorText, MatchQuantity.Capacitance, "pF",
                                           out var caps, out string? capErr))
        {
            Ladder = SmithPreferredLadder.Capacitors;
            Error  = capErr;
            return;
        }

        if (!SmithPreferredValues.TryParse(_inductorText, MatchQuantity.Inductance, "nH",
                                           out var inds, out string? indErr))
        {
            Ladder = SmithPreferredLadder.Inductors;
            Error  = indErr;
            return;
        }

        SmithPreferredValueStore.Set(MatchQuantity.Capacitance, caps);
        SmithPreferredValueStore.Set(MatchQuantity.Inductance,  inds);

        _capacitorText = SmithPreferredValues.Format(caps, MatchQuantity.Capacitance);
        _inductorText  = SmithPreferredValues.Format(inds, MatchQuantity.Inductance);
        Error = null;
        OnPropertyChanged(nameof(Buffer));
        RebuildRows();
        _onApplied?.Invoke();
    }

    /// <summary>
    /// Both ladders back to what circuitRF ships, <b>stored at once</b>. The escape hatch the whole
    /// feature needs: a list somebody has pasted a spreadsheet into is one keystroke from useless,
    /// and an editor with no way back would make that permanent.
    /// </summary>
    [RelayCommand]
    private void RevertToShipped()
    {
        SmithPreferredValueStore.Revert();
        _capacitorText = SmithPreferredValues.Format(SmithPreferredValueStore.Capacitors,
                                                     MatchQuantity.Capacitance);
        _inductorText  = SmithPreferredValues.Format(SmithPreferredValueStore.Inductors,
                                                     MatchQuantity.Inductance);
        Error = null;
        OnPropertyChanged(nameof(Buffer));
        RebuildRows();
        _onApplied?.Invoke();
    }
}

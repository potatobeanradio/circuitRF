// The body every Create … from … dialog shares — brief-img-5-dialog.md R-im5-1; brief-artsch-8-gui-command.md R-as8-2 …
// R-as8-4.
//
// One dialog, two sources. Whatever the source — a layout on disk (Create Schematic from Artwork) or a picture (Create
// Schematic / Layout from Image) — what sits below it is the same: where the result goes, the recognition's options,
// the parts table, the report strip, and Create. This class is that body, written once; the source subclasses say only
// what to read, where a new cell goes, and which entry point writes it.
//
// ── IT DECIDES NOTHING ───────────────────────────────────────────────────────────────────────────────
//
// Every option here is a field of a record the CLI fills from its flags (RecognitionOptions, the sweep RecognitionSweep
// composes, the target), and both surfaces hand them to the same function. A rule that lived only in this file would
// be a rule `circuitrf recognize` does not apply.
//
// ── EDITS SURVIVE A RE-RUN ───────────────────────────────────────────────────────────────────────────
//
// Recognition re-runs (debounced, cancellable) whenever the source, an option or a companion file changes, and the
// parts table is rebuilt from what comes back. A table edit is therefore never stored on a row: it is kept as the
// override a parts CSV would carry (R-as4-8), written as CSV text, and laid over every later recognition by the
// recognition's own CSV reader — the contract the CLI's --parts reads.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Design;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Layout.Recognition.Silkscreen;
using CircuitRF.Engine;
using CircuitRF.Ui.Tuning;

namespace CircuitRF.Ui.Recognition;

/// <summary>One report class, one line (R-as8-2's report strip): its sentence and, where it has any, its anchors.</summary>
public sealed partial class RecognitionReportLineViewModel : ObservableObject
{
    public RecognitionReportLineViewModel(RecognitionFinding finding, IReadOnlyList<string> anchorTexts)
    {
        Finding = finding;
        AnchorTexts = anchorTexts;
        Anchors = [.. anchorTexts.Select((t, i) => new RecognitionReportAnchorViewModel(this, i, t))];
    }

    public RecognitionFinding Finding { get; }
    public string Text => Finding.Sentence;
    public int Count => Finding.Count;
    public IReadOnlyList<string> AnchorTexts { get; }

    /// <summary>The anchors as the strip lists them — each one selectable (R-im5-3).</summary>
    public IReadOnlyList<RecognitionReportAnchorViewModel> Anchors { get; }
    public bool HasAnchors => AnchorTexts.Count > 0;
    [ObservableProperty] private bool _isExpanded;
}

/// <summary>One anchor of a report line, as the strip lists it.</summary>
public sealed record RecognitionReportAnchorViewModel(RecognitionReportLineViewModel Line, int Index, string Text)
{
    public RecognitionAnchor Anchor => Line.Finding.Anchors[Index];
}

/// <summary>What a preview produced: the table, the report, and whatever the source draws from it.</summary>
/// <param name="Parts">The recognised table, or null when the source recognises no parts (a traced layout).</param>
/// <param name="Detail">The source's own result — what an image source's overlay is built from.</param>
public sealed record RecognitionPreview(PartsTable? Parts, RecognitionReport Report, string? Refusal, object? Detail = null);

/// <summary>The dialog's body.</summary>
public abstract partial class RecognitionSessionViewModel : ObservableObject
{
    private readonly Action<Action> _post;
    private readonly TimeSpan _debounce;
    private readonly FrequencySpec _basis;
    private readonly Dictionary<string, Dictionary<string, string>> _edits = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PartsTableRowViewModel> _rows = [];
    private CancellationTokenSource? _cts;
    private PartsTable _table = PartsTable.Empty;
    private IReadOnlyList<string>? _modelFiles;
    private bool _constructing = true;

    /// <param name="emSetup">The EM setup the sweep starts from (the artwork's), or null — the default sweep.</param>
    /// <param name="post">How a result from the background reaches the UI thread; immediate when null.</param>
    /// <param name="debounce">How long a burst of changes is let settle before recognising.</param>
    protected RecognitionSessionViewModel(EmSetup? emSetup, Action<Action>? post, TimeSpan? debounce)
    {
        _post = post ?? (a => a());
        _debounce = debounce ?? TimeSpan.FromMilliseconds(300);
        _basis = RecognitionSweep.Basis(emSetup);
        _newCellName = "";
        _startText = $"{_basis.StartExpr} {_basis.StartUnit}";
        _stopText = $"{_basis.StopExpr} {_basis.StopUnit}";
        _pointsText = (_basis.NumPoints ?? RecognitionEmitOptions.DefaultSweep.NumPoints!.Value).ToString(CultureInfo.InvariantCulture);
        _coplanarFactorText = RecognitionOptions.DefaultCoplanarGapFactor.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The subclass's constructor is done: the target is checked and the first recognition scheduled.</summary>
    protected void EndConstruction(string newCellName, bool targetOwn)
    {
        NewCellName = newCellName;
        TargetOwn = targetOwn;
        _constructing = false;
        RefreshTarget();
        ScheduleRecognition();
    }

    /// <summary>Marshals <paramref name="action"/> onto the UI thread.</summary>
    protected void Post(Action action) => _post(action);

    public abstract string Title { get; }

    /// <summary>Whether there is a source to read — false only for a picture dialog still waiting for its picture,
    /// which shows nothing but its drop zone.</summary>
    public virtual bool HasSource => true;

    /// <summary>A suggestion the status line offers, applied only when accepted (a matching layer preset, R-im5-5).</summary>
    public virtual string OfferText => "";
    public virtual bool HasOffer => false;

    [RelayCommand]
    private void AcceptOffer() => OnAcceptOffer();

    [RelayCommand]
    private void DeclineOffer() => OnDeclineOffer();

    protected virtual void OnAcceptOffer() { }
    protected virtual void OnDeclineOffer() { }

    // ── target (R-as8-2, D4; R-im5-1, D14) ───────────────────────────────────────────────────────────

    /// <summary>Whether the source's own document is offered as the target — the artwork cell's schematic (AS D4), or
    /// the layout a placed picture sits in (IM D14).</summary>
    public abstract bool OwnTargetOffered { get; }

    /// <summary>The own target's radio label and tooltip.</summary>
    public abstract string OwnTargetLabel { get; }
    public abstract string OwnTargetTip { get; }

    /// <summary>The New cell radio's tooltip.</summary>
    public abstract string NewCellTip { get; }

    [ObservableProperty] private bool _targetOwn;
    [ObservableProperty] private string _newCellName;
    [ObservableProperty] private string _nameError = "";
    [ObservableProperty] private bool _isReplace;

    public bool TargetNewCell
    {
        get => !TargetOwn;
        set => TargetOwn = !value;
    }

    /// <summary>Create, or Replace when the name names a cell this command wrote (R-as8-2).</summary>
    public string CreateButtonText => IsReplace ? "Replace" : "Create";

    /// <summary>The Replace button's flyout.</summary>
    public virtual string ReplaceConfirmText => $"Replaces {NewCellName.Trim()}'s schematic; a checkpoint is taken first.";

    partial void OnTargetOwnChanged(bool value)
    {
        OnPropertyChanged(nameof(TargetNewCell));
        RefreshTarget();
        OnTargetChanged();
    }

    /// <summary>The target changed (own ↔ new cell).</summary>
    protected virtual void OnTargetChanged() { }

    partial void OnNewCellNameChanged(string value) => RefreshTarget();

    partial void OnIsReplaceChanged(bool value) => OnPropertyChanged(nameof(CreateButtonText));

    /// <summary>Where a new cell of <paramref name="name"/> would be made.</summary>
    protected abstract string NewCellDir(string name);

    /// <summary>Whether <paramref name="cellDir"/> was written by this command, and so may be replaced.</summary>
    protected abstract bool IsReplaceable(string cellDir);

    /// <summary>The refusal for an existing cell this command did not write.</summary>
    protected abstract string NotReplaceableText(string name);

    /// <summary>Live: <see cref="NameValidator"/>, and whether the name is a cell this command may replace.</summary>
    protected void RefreshTarget()
    {
        if (_constructing) return;
        OnPropertyChanged(nameof(ReplaceConfirmText));
        if (TargetOwn) { NameError = ""; IsReplace = false; RefreshCreate(); return; }
        string name = NewCellName.Trim();
        string dir = NewCellDir(name);
        bool exists = name.Length > 0 && (Directory.Exists(dir) || File.Exists(dir));
        IsReplace = exists && NameValidator.Validate(name) is null && IsReplaceable(dir);
        NameError = NameValidator.Validate(name) is { } bad ? bad
                  : exists && !IsReplace ? NotReplaceableText(name)
                  : "";
        RefreshCreate();
    }

    // ── options (the options row) ────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<string> GroundOptions { get; } = ["Auto", "Pick on layout"];
    public static IReadOnlyList<string> ViaOptions { get; } = ["Model as VIAGND", "Plain GND"];
    public static IReadOnlyList<string> CoplanarOptions { get; } = ["Auto", "Microstrip", "GCPW"];

    /// <summary>Whether the recognition's options apply — always for artwork; for a picture, only when a schematic is
    /// made from it.</summary>
    public virtual bool OptionsApply => true;

    [ObservableProperty] private int _groundIndex;
    [ObservableProperty] private (long X, long Y)? _groundPoint;
    [ObservableProperty] private int _viaIndex;
    [ObservableProperty] private int _coplanarIndex;
    [ObservableProperty] private string _coplanarFactorText;
    [ObservableProperty] private string _startText;
    [ObservableProperty] private string _stopText;
    [ObservableProperty] private string _pointsText;

    /// <summary>Set by the window: arms a point pick on the layout (or the picture) and hands the point back, DBU
    /// (Ground ▸ Pick on layout).</summary>
    public Action<Action<long, long>>? PickGroundRequested { get; set; }

    /// <summary>Where the picked ground is, as the layout displays it; empty until one is picked.</summary>
    public string GroundPointText => GroundPoint is { } p
        ? $"({_table.Format.Length(p.X)}, {_table.Format.Length(p.Y)})" : "";

    partial void OnGroundIndexChanged(int value)
    {
        if (value == 1 && GroundPoint is null) PickGround();
        else ScheduleRecognition();
    }

    partial void OnGroundPointChanged((long X, long Y)? value)
    {
        OnPropertyChanged(nameof(GroundPointText));
        ScheduleRecognition();
    }

    [RelayCommand]
    private void PickGround() => PickGroundRequested?.Invoke((x, y) => _post(() =>
    {
        GroundPoint = (x, y);
        if (GroundIndex != 1) GroundIndex = 1;
    }));

    partial void OnViaIndexChanged(int value) => ScheduleRecognition();
    partial void OnCoplanarIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CoplanarFactorApplies));
        ScheduleRecognition();
    }

    /// <summary>The coplanar gap factor is read only under Auto.</summary>
    public bool CoplanarFactorApplies => CoplanarIndex == 0;
    partial void OnCoplanarFactorTextChanged(string value) => ScheduleRecognition();
    partial void OnStartTextChanged(string value) => ScheduleRecognition();
    partial void OnStopTextChanged(string value) => ScheduleRecognition();
    partial void OnPointsTextChanged(string value) => ScheduleRecognition();

    /// <summary>The significant figures the circuit's numbers are written with — the CLI's <c>--digits</c>. Only the
    /// emitted circuit reads it, so a change re-runs nothing.</summary>
    [ObservableProperty] private int _digits = RecognitionEmitOptions.DefaultDigits;

    /// <summary>The digits menu: the Optimizer's choices, one radio item each.</summary>
    public IReadOnlyList<TuningDigitsChoice> DigitsChoices
        => [.. TuningDigits.Choices.Select(d => new TuningDigitsChoice(d, d == Digits, SetDigitsCommand))];

    public string DigitsLabel => TuningDigits.Label(Digits);

    [RelayCommand]
    private void SetDigits(int digits) => Digits = digits;

    /// <summary>Whether a shunt part is drawn on its copper's side of the line — Settings ▸ "Link symbol and footprint
    /// orientation", set by the window; the CLI's <c>--free-orientation</c> is its false.</summary>
    public bool ArtworkSides { get; init; } = true;

    partial void OnDigitsChanged(int value)
    {
        OnPropertyChanged(nameof(DigitsChoices));
        OnPropertyChanged(nameof(DigitsLabel));
    }

    /// <summary>Why the options cannot be read as they stand, or null.</summary>
    protected string? OptionsBlocked()
    {
        if (!OptionsApply) return null;
        if (GroundIndex == 1 && GroundPoint is null) return "Click the ground copper on the layout (Pick…), or set Ground to Auto.";
        if (RecognitionSweep.Parse(StartText) is null) return "Start needs a frequency with its unit (100 MHz).";
        if (RecognitionSweep.Parse(StopText) is null) return "Stop needs a frequency with its unit (6 GHz).";
        if (!int.TryParse(PointsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 2)
            return "Points needs a whole number, at least 2.";
        if (CoplanarIndex == 0 && !(double.TryParse(CoplanarFactorText, NumberStyles.Float, CultureInfo.InvariantCulture, out double k) && k > 0))
            return "The coplanar factor needs a positive number of substrate heights.";
        return null;
    }

    /// <summary>The recognition's options from the options row, over <paramref name="options"/>.</summary>
    protected RecognitionOptions BuildOptions(RecognitionOptions options) => options with
    {
        Vias = ViaIndex == 1 ? ViaPolicy.Ground : ViaPolicy.Model,
        Coplanar = CoplanarIndex switch { 1 => CoplanarReading.Microstrip, 2 => CoplanarReading.Gcpw, _ => CoplanarReading.Auto },
        CoplanarGapFactor = double.TryParse(CoplanarFactorText, NumberStyles.Float, CultureInfo.InvariantCulture, out double k) && k > 0
            ? k : RecognitionOptions.DefaultCoplanarGapFactor,
        GroundAt = GroundIndex == 1 ? GroundPoint : null,
        TopFrequencyHz = StatedStop() is { } stop ? stop.Hz : null,
    };

    /// <summary>The emit options: the sweep composed as the CLI composes its flags — a field left at the basis is unstated.</summary>
    public RecognitionEmitOptions BuildEmit()
    {
        var start = RecognitionSweep.Parse(StartText) is { } s && !(s.Text == _basis.StartExpr && s.Unit == _basis.StartUnit) ? s : (RecognitionFrequency?)null;
        int? npts = int.TryParse(PointsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                    && n != (_basis.NumPoints ?? RecognitionEmitOptions.DefaultSweep.NumPoints) ? n : null;
        return new RecognitionEmitOptions { Sweep = RecognitionSweep.Compose(EmSetupForSweep, start, StatedStop(), npts), Digits = Digits,
                                           ArtworkSides = ArtworkSides };
    }

    /// <summary>The EM setup the sweep is composed over — the artwork's; a picture has none.</summary>
    protected virtual EmSetup? EmSetupForSweep => null;

    private RecognitionFrequency? StatedStop() =>
        RecognitionSweep.Parse(StopText) is { } e && !(e.Text == _basis.StopExpr && e.Unit == _basis.StopUnit) ? e : null;

    // ── the parts table (R-as8-3) ────────────────────────────────────────────────────────────────────

    /// <summary>Whether the source recognises parts — a traced layout has none.</summary>
    public virtual bool PartsApply => true;

    /// <summary>The rows the filter and the sort leave, in order.</summary>
    public ObservableCollection<PartsTableRowViewModel> Rows { get; } = [];

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _sortColumn = "Refdes";
    [ObservableProperty] private bool _sortAscending = true;
    [ObservableProperty] private PartsTableRowViewModel? _selectedRow;

    /// <summary>Set by the window: a Touchstone file picker for the Model cell's Browse….</summary>
    public Func<Task<string?>>? BrowseModelAsync { get; set; }

    /// <summary>The recognised table with the held edits laid over it.</summary>
    public PartsTable Table => _table;

    /// <summary>Whether the table's X and Y columns mean anything — not on a picture, whose parts sit where its pixels do.</summary>
    public virtual bool ShowPlacementColumns => true;

    partial void OnFilterTextChanged(string value) => RefreshRows();

    [RelayCommand]
    private void Sort(string column)
    {
        if (SortColumn == column) SortAscending = !SortAscending;
        else { SortColumn = column; SortAscending = true; }
        RefreshRows();
    }

    partial void OnSelectedRowChanged(PartsTableRowViewModel? value)
    {
        RefreshLearn();
        OnRowSelected(value);
    }

    /// <summary>A row was selected (or none): the source shows where it is.</summary>
    protected virtual void OnRowSelected(PartsTableRowViewModel? row) { }

    /// <summary>A report anchor was chosen in the strip.</summary>
    public virtual void SelectAnchor(RecognitionAnchor anchor) { }

    private void RefreshRows()
    {
        string filter = FilterText.Trim();
        IEnumerable<PartsTableRowViewModel> rows = _rows.Where(r => r.Matches(filter));
        Comparison<PartsTableRowViewModel> by = SortColumn is "X" or "Y"
            ? (a, b) => (SortColumn == "X" ? a.Row.X : a.Row.Y).CompareTo(SortColumn == "X" ? b.Row.X : b.Row.Y)
            : (a, b) => PartsTable.NaturalCompare(a.SortKey(SortColumn), b.SortKey(SortColumn));
        var sorted = rows.ToList();
        sorted.Sort((a, b) => { int c = by(a, b); return SortAscending ? c : -c; });

        string? keep = SelectedRow?.BoardRefdes;
        Rows.Clear();
        foreach (var r in sorted) Rows.Add(r);
        var reselect = keep is null ? null : Rows.FirstOrDefault(r => string.Equals(r.BoardRefdes, keep, StringComparison.OrdinalIgnoreCase));
        if (!ReferenceEquals(reselect, SelectedRow)) SelectedRow = reselect;
    }

    /// <summary>A cell edit, held as the CSV override it is (R-as8-2), under the designator the board gave the part.
    /// The Model cell's Browse… asks for a file first; a designator another part already has is refused.</summary>
    private void OnRowEdited(PartsTableRowViewModel row, string column, string text)
    {
        string key = row.BoardRefdes;
        if (column == "Model")
        {
            if (text == PartsTableRowViewModel.BrowseModel) { _ = BrowseModelFor(row); return; }
            if (text == "Ideal") { SetEdit(key, "Model", "Ideal"); SetEdit(key, "ModelFile", ""); }
            else { SetEdit(key, "Model", "SnP"); SetEdit(key, "ModelFile", text); }
            return;
        }
        if (column == "Refdes")
        {
            string name = text.Trim();
            if (name.Length == 0 || string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                if (_edits.TryGetValue(key, out var cells)) cells.Remove("Refdes");
                Status = "";
            }
            else if (_rows.Any(r => !ReferenceEquals(r, row) && (string.Equals(r.Refdes, name, StringComparison.OrdinalIgnoreCase)
                                                             || string.Equals(r.RefdesText.Trim(), name, StringComparison.OrdinalIgnoreCase))))
                Status = $"{name} is another part's designator; {row.Refdes} was not renamed.";
            else
            {
                SetEdit(key, "Refdes", name);
                Status = "";
            }
            RefreshLearn();
            return;
        }
        SetEdit(key, column, text);
    }

    // ── Learn these glyphs (brief-artsch-10 R-as10-5) ─────────────────────────────────────────────

    private bool CanLearnGlyphs() => SelectedRow is { } row && SilkscreenText.Lesson(row.Corrected) is not null;

    /// <summary>Whether Learn These Glyphs is offered: the selected part's designator came from the silkscreen and has
    /// been corrected.</summary>
    public bool CanLearn => CanLearnGlyphs();

    private void RefreshLearn()
    {
        LearnGlyphsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanLearn));
    }

    /// <summary>
    /// The selected row's corrected designator, taught: each silkscreen glyph the correction says is another character
    /// is kept as a template of that character in the per-user state directory and used on every later recognition.
    /// Nothing is written to the workspace.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLearnGlyphs))]
    private void LearnGlyphs()
    {
        if (SelectedRow is not { } row || SilkscreenText.Lesson(row.Corrected) is not { } lesson) return;
        try
        {
            int added = GlyphTemplates.Learn(AppDataRoot.SubDir(GlyphTemplates.TaughtFolder), lesson);
            Status = added == 0
                ? $"Those glyphs were already learned."
                : $"Learned {string.Join(", ", lesson.Select(l => $"'{l.Char}'"))} from {row.RefdesText.Trim()}; recognising again.";
            ScheduleRecognition();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"The glyphs could not be saved: {ex.Message}";
        }
    }

    private async Task BrowseModelFor(PartsTableRowViewModel row)
    {
        if (BrowseModelAsync is { } browse && await browse() is { Length: > 0 } file)
        {
            SetEdit(row.BoardRefdes, "Model", "SnP");
            SetEdit(row.BoardRefdes, "ModelFile", _table.StoredPath(Path.GetFullPath(file)));
        }
        ScheduleRecognition();   // re-reads the row, so the cell shows what was applied rather than "Browse…"
    }

    private void SetEdit(string refdes, string column, string text)
    {
        if (!_edits.TryGetValue(refdes, out var cells)) _edits[refdes] = cells = new(StringComparer.Ordinal);
        cells[column] = text;
    }

    /// <summary>
    /// The held edits as the parts CSV that carries them — null when there are none. Only edited rows are written, and
    /// on each the cells the user did not touch are written as the table already has them, so nothing but the edit
    /// changes (an empty Value would CLEAR the value — the CSV's own rule). X and Y are written so a corrected
    /// designator finds its part (the CSV's rename rule).
    /// </summary>
    public string? PartsCsvText
    {
        get
        {
            if (_edits.Count == 0) return null;
            string[] columns = ["Refdes", "Kind", "Value", "Model", "ModelFile", "X", "Y"];
            var sb = new StringBuilder(string.Join(",", columns)).Append('\n');
            foreach (var (key, cells) in _edits.OrderBy(e => e.Key, PartsTable.NaturalOrder))
            {
                var row = _table.Rows.FirstOrDefault(r => string.Equals(r.BoardRefdes, key, StringComparison.OrdinalIgnoreCase));
                string Current(string column) => row is null ? "" : PartsTableCsv.Cell(_table, row, column);
                string refdes = cells.TryGetValue("Refdes", out var r) ? r : row?.Refdes ?? key;
                string kind = cells.TryGetValue("Kind", out var k) ? k : Current("Kind");
                string value = cells.TryGetValue("Value", out var v) ? v
                             : row is not null && SameDimension(row, kind) ? Current("Value") : "";
                string model = cells.TryGetValue("Model", out var m) ? m : Current("Model");
                string file = cells.TryGetValue("ModelFile", out var f) ? f : Current("ModelFile");
                sb.Append(string.Join(",", new[] { refdes, kind, value, model, file, Current("X"), Current("Y") }.Select(Escape))).Append('\n');
            }
            return sb.ToString();
        }
    }

    private static bool SameDimension(PartRow row, string kindText)
    {
        var kind = PartsTable.ParseKind(kindText) ?? row.Kind;
        return (kind == PartKind.Unknown ? PartKind.C : kind) == row.GeneratedKind;
    }

    private static string Escape(string field) =>
        field.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;

    /// <summary>Set by the window: a save picker (Export Parts…) and an open picker (Import Parts…), CSV.</summary>
    public Func<Task<string?>>? PickExportPathAsync { get; set; }
    public Func<Task<string?>>? PickImportPathAsync { get; set; }

    [RelayCommand]
    private async Task ExportParts()
    {
        if (PickExportPathAsync is not { } pick || await pick() is not { Length: > 0 } path) return;
        try
        {
            PartsTableCsv.WriteFile(path, EditedTable());
            Status = $"Wrote {path}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"The parts table could not be written: {ex.Message}";
        }
    }

    /// <summary>
    /// The table as the dialog shows it: the last recognition with every held edit laid over it by the CSV reader.
    /// A Kind or Value edit does not re-run recognition, so <see cref="_table"/> alone can be behind the screen —
    /// designer report (round 15): Export Parts… wrote the table without the values just typed into it.
    /// </summary>
    public PartsTable EditedTable() =>
        PartsCsvText is { } edits && PartsTableCsv.Read(edits, _table).Table is { } edited ? edited : _table;

    [RelayCommand]
    private async Task ImportParts()
    {
        if (PickImportPathAsync is not { } pick || await pick() is not { Length: > 0 } path) return;
        Import(path);
    }

    /// <summary>
    /// Import Parts…: every editable cell the file changes becomes a held edit, read by the CSV reader itself — a
    /// refused file changes nothing and says why.
    /// </summary>
    public void Import(string path)
    {
        var read = PartsTableCsv.ReadFile(path, _table);
        if (read.Refusal is { } why) { Status = why; return; }
        foreach (var edited in read.Table!.Rows)
        {
            string key = edited.BoardRefdes;
            if (_table.Rows.FirstOrDefault(r => string.Equals(r.BoardRefdes, key, StringComparison.OrdinalIgnoreCase)) is not { } was) continue;
            if (!string.Equals(edited.Refdes, was.Refdes, StringComparison.Ordinal)) SetEdit(key, "Refdes", edited.Refdes);
            foreach (string column in new[] { "Kind", "Value", "Model", "ModelFile" })
            {
                string after = PartsTableCsv.Cell(read.Table, edited, column);
                if (after != PartsTableCsv.Cell(_table, was, column)) SetEdit(key, column, after);
            }
        }
        var said = read.Notes.Concat(read.NotOnBoard.Count > 0 ? [$"Not on the board: {string.Join(", ", read.NotOnBoard)}."] : []);
        Status = string.Join(" ", said);
        ScheduleRecognition();
    }

    // ── the report strip ─────────────────────────────────────────────────────────────────────────────

    public ObservableCollection<RecognitionReportLineViewModel> ReportLines { get; } = [];

    /// <summary>The last recognition's report, for Messages after a Create.</summary>
    public RecognitionReport? LastReport { get; protected set; }

    // ── recognition: debounced, cancellable ─────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isRecognising;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _progressText = "";

    /// <summary>The recognition in flight (or the last one) — what a test awaits.</summary>
    public Task Recognition { get; private set; } = Task.CompletedTask;

    /// <summary>Why recognition is waiting for the user, or null.</summary>
    public virtual string? Blocked => OptionsBlocked();

    /// <summary>
    /// The read the source makes, captured on the UI thread: a function the background runs, or null when there is
    /// nothing to read yet (no picture).
    /// </summary>
    protected abstract Func<RunControl, RecognitionPreview>? BeginPreview();

    /// <summary>A preview arrived — what the source draws from it.</summary>
    protected virtual void OnPreviewApplied(RecognitionPreview preview) { }

    /// <summary>Starts a recognition after the debounce, cancelling any in flight.</summary>
    public void ScheduleRecognition()
    {
        if (_constructing) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        RefreshCreate();
        if (Blocked is { } why)
        {
            IsRecognising = false;
            Status = why;
            Recognition = Task.CompletedTask;
            return;
        }
        var work = BeginPreview();
        if (work is null)
        {
            IsRecognising = false;
            Recognition = Task.CompletedTask;
            return;
        }
        IsRecognising = true;
        Status = "";
        Recognition = Recognise(work, cts);
    }

    /// <summary>Stops a recognition in flight.</summary>
    protected void CancelRecognition()
    {
        _cts?.Cancel();
        IsRecognising = false;
        ProgressText = "";
    }

    private async Task Recognise(Func<RunControl, RecognitionPreview> work, CancellationTokenSource cts)
    {
        try
        {
            if (_debounce > TimeSpan.Zero) await Task.Delay(_debounce, cts.Token).ConfigureAwait(false);
            var control = new RunControl
            {
                Token = cts.Token,
                Progress = new Progress<RunProgress>(p => _post(() => { if (!cts.IsCancellationRequested) ProgressText = p.Stage; })),
            };
            var preview = await Task.Run(() => work(control), cts.Token).ConfigureAwait(false);
            _post(() => { if (!cts.IsCancellationRequested) Apply(preview); });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _post(() => { if (!cts.IsCancellationRequested) { IsRecognising = false; Status = $"Recognition failed: {ex.Message}"; } });
        }
    }

    private void Apply(RecognitionPreview preview)
    {
        IsRecognising = false;
        ProgressText = "";
        _table = preview.Parts ?? PartsTable.Empty;
        LastReport = preview.Report;
        _modelFiles ??= ModelFiles(_table.BaseDirectory);

        _rows.Clear();
        foreach (var row in _table.Rows) _rows.Add(new PartsTableRowViewModel(_table, row, _modelFiles, OnRowEdited));
        RefreshRows();

        ReportLines.Clear();
        foreach (var f in preview.Report.Findings)
            ReportLines.Add(new RecognitionReportLineViewModel(f, [.. f.Anchors.Select(a => AnchorText(_table, a))]));

        Status = preview.Refusal ?? "";
        OnPropertyChanged(nameof(GroundPointText));
        OnPreviewApplied(preview);
        RefreshCreate();
    }

    /// <summary>An anchor as the layout displays coordinates.</summary>
    public static string AnchorText(PartsTable table, RecognitionAnchor a) =>
        $"({table.Format.Length(a.X)}, {table.Format.Length(a.Y)})" + (a.Layer is { } l ? $" on {l.Layer}/{l.Datatype}" : "");

    private static IReadOnlyList<string> ModelFiles(string? root) =>
        root is null ? [] : [.. new PartModelResolver(root).TwoPortFiles().Select(f =>
        {
            string rel = Path.GetRelativePath(root, f);
            return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? f : rel.Replace('\\', '/');
        })];

    // ── create (R-as8-4, R-im5-9) ────────────────────────────────────────────────────────────────────

    /// <summary>A write, captured on the UI thread.</summary>
    /// <param name="BusyText">The status line while it runs.</param>
    /// <param name="Work">The write, run in the background.</param>
    /// <param name="FailurePrefix">How an exception from it is introduced.</param>
    protected sealed record CreateJob(string BusyText, Func<CreateOutcome> Work, string FailurePrefix);

    /// <summary>What a write did: a refusal, or the report and what to do on the UI thread.</summary>
    protected sealed record CreateOutcome(string? Refusal, RecognitionReport? Report, Action? Done);

    protected abstract CreateJob BeginCreate();

    /// <summary>Why Create is disabled — its tooltip — or null.</summary>
    public virtual string? CreateBlocked => Blocked ?? (TargetOwn || NameError.Length == 0 ? null : NameError);

    /// <summary>Create's tooltip: why it is disabled, or nothing.</summary>
    public string? CreateTip => CreateBlocked;

    private bool CanCreate() => !IsCreating && CreateBlocked is null;

    /// <summary>Create's enablement and its tooltip, re-read.</summary>
    protected void RefreshCreate()
    {
        CreateCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CreateTip));
    }

    [ObservableProperty] private bool _isCreating;

    partial void OnIsCreatingChanged(bool value) => RefreshCreate();

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task Create()
    {
        CancelRecognition();
        var job = BeginCreate();
        IsCreating = true;
        Status = job.BusyText;
        try
        {
            var outcome = await Task.Run(job.Work).ConfigureAwait(false);
            _post(() =>
            {
                IsCreating = false;
                if (outcome.Refusal is { } why) { Status = why; return; }
                Status = "";
                LastReport = outcome.Report;
                outcome.Done?.Invoke();
            });
        }
        catch (Exception ex)
        {
            _post(() => { IsCreating = false; Status = $"{job.FailurePrefix}: {ex.Message}"; });
        }
    }

    /// <summary>Closing the dialog: stops a recognition in flight; nothing is written.</summary>
    public virtual void Close() => _cts?.Cancel();
}

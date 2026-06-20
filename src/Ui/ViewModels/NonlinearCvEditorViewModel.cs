using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

public enum CvEditorMode { Rows = 0, Text = 1 }

/// <summary>A single (V, C) point for the live shape preview (display-unit values).</summary>
public readonly record struct CvPoint(double V, double C);

/// <summary>
/// VM for the NonlinearC CV data editor dialog (briefs #4 + #5).
/// Holds a staging table of (V, C) rows and a fit order.  Nothing touches the
/// component until Apply is clicked — Apply fits a polynomial and executes a
/// single ApplyCvFitCommand so the whole operation is one undo step.
/// Close discards staged edits (deliberate divergence from VarEditorView).
/// </summary>
public sealed partial class NonlinearCvEditorViewModel : ObservableObject, IDisposable
{
    private SchematicViewModel? _schematicVm;
    private EditableComponent?  _comp;
    [ObservableProperty] private string       _instanceName       = "";
    [ObservableProperty] private int          _fitOrder           = 3;
    [ObservableProperty] private bool         _hasValidationErrors;
    [ObservableProperty] private string       _validationSummary  = "";
    [ObservableProperty] private string       _capacitanceUnit    = "pF";
    [ObservableProperty] private CvEditorMode _activeMode         = CvEditorMode.Rows;
    [ObservableProperty] private string       _textContent        = "";
    [ObservableProperty] private IReadOnlyList<CvPoint>? _previewPoints;

    public bool IsTextMode => ActiveMode == CvEditorMode.Text;
    public bool IsRowsMode => ActiveMode == CvEditorMode.Rows;

    public static string[] CapacitanceUnitOptions
        => ComponentTypeRegistry.UnitOptions(UnitDimension.Capacitance);

    public string DialogTitle => _comp is null
        ? "Edit CV Data"
        : $"Edit CV Data — {_comp.InstanceName}";

    public ObservableCollection<CvRowViewModel> Rows { get; } = [];

    // ── Undo/Redo delegates to the owning schematic's stack ───────────────────

    public IRelayCommand UndoCommand { get; }
    public IRelayCommand RedoCommand { get; }
    private UndoRedoStack? _hookedStack;

    private void HookSchematicStack(SchematicViewModel? vm)
    {
        if (_hookedStack is not null) _hookedStack.PropertyChanged -= OnStackChanged;
        _hookedStack = vm?.UndoRedo;
        if (_hookedStack is not null) _hookedStack.PropertyChanged += OnStackChanged;
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void OnStackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UndoRedoStack.CanUndo)) UndoCommand.NotifyCanExecuteChanged();
        if (e.PropertyName is nameof(UndoRedoStack.CanRedo)) RedoCommand.NotifyCanExecuteChanged();
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public IRelayCommand AddRowCommand       { get; }
    public IRelayCommand ApplyCommand        { get; }
    public IRelayCommand SetTextModeCommand  { get; }
    public IRelayCommand SetRowsModeCommand  { get; }

    public NonlinearCvEditorViewModel()
    {
        UndoCommand          = new RelayCommand(
            () => _schematicVm?.UndoRedo.Undo(),
            () => _schematicVm?.UndoRedo.CanUndo ?? false);
        RedoCommand          = new RelayCommand(
            () => _schematicVm?.UndoRedo.Redo(),
            () => _schematicVm?.UndoRedo.CanRedo ?? false);
        AddRowCommand        = new RelayCommand(AddRow);
        ApplyCommand         = new RelayCommand(Apply);
        SetTextModeCommand   = new RelayCommand(SetTextMode);
        SetRowsModeCommand   = new RelayCommand(SetRowsMode);
    }

    // ── Setup ─────────────────────────────────────────────────────────────────

    public void SetTarget(SchematicViewModel schematicVm, EditableComponent comp)
    {
        if (_schematicVm is not null)
            _schematicVm.EditModel.Changed -= OnModelChanged;

        _schematicVm = schematicVm;
        _comp        = comp;
        InstanceName = comp.InstanceName;
        OnPropertyChanged(nameof(DialogTitle));

        HookSchematicStack(schematicVm);
        _schematicVm.EditModel.Changed += OnModelChanged;

        LoadFromComponent();
    }

    // ── Mode changed → raise computed properties ──────────────────────────────

    partial void OnActiveModeChanged(CvEditorMode oldValue, CvEditorMode newValue)
    {
        OnPropertyChanged(nameof(IsTextMode));
        OnPropertyChanged(nameof(IsRowsMode));
    }

    // ── FitOrder / unit change → re-validate ─────────────────────────────────

    partial void OnFitOrderChanged(int oldValue, int newValue)          => Validate();
    partial void OnCapacitanceUnitChanged(string? oldValue, string newValue) => UpdatePreviewPoints();

    // ── Load staged table from component's CvData hidden param ────────────────

    private void LoadFromComponent()
    {
        if (_comp is null) return;

        Rows.Clear();
        HasValidationErrors = false;
        ValidationSummary   = "";

        string? cvDataExpr = _comp.Parameters.FirstOrDefault(p => p.Name == "CvData")?.Expression;
        string? raw        = UnwrapStringLiteral(cvDataExpr);

        if (!string.IsNullOrWhiteSpace(raw))
        {
            var (pts, order, unit) = ParseCvDataFull(raw);
            FitOrder         = order;
            CapacitanceUnit  = unit;
            foreach (var (v, c) in pts)
                Rows.Add(new CvRowViewModel(
                    v.ToString("G15", CultureInfo.InvariantCulture),
                    c.ToString("G15", CultureInfo.InvariantCulture),
                    this));
        }

        if (Rows.Count < 2)
        {
            Rows.Clear();
            Rows.Add(new CvRowViewModel("", "", this));
            Rows.Add(new CvRowViewModel("", "", this));
        }

        Validate();
    }

    // ── Apply: validate → fit → write one undoable command ───────────────────

    private void Apply()
    {
        if (_comp is null || _schematicVm is null) return;

        if (IsTextMode)
            SyncTextToRows();

        Validate();
        if (HasValidationErrors) return;

        double scale = UnitScale(CapacitanceUnit);

        var vs = Rows.Select(r =>
            double.Parse(r.StagedV.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
        var cs = Rows.Select(r =>
            double.Parse(r.StagedC.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) * scale).ToArray();

        double[] coeffs = PolynomialFit.Fit(vs, cs, FitOrder);
        string   serial = SerializeCvData(Rows, FitOrder, CapacitanceUnit);

        _schematicVm.Execute(new ApplyCvFitCommand(_schematicVm.EditModel, _comp, coeffs, serial));
        HasValidationErrors = false;
        ValidationSummary   = "";
    }

    // ── Mode switching ────────────────────────────────────────────────────────

    private void SetTextMode()
    {
        if (ActiveMode == CvEditorMode.Text) return;
        TextContent = SerializeRowsToText(Rows);
        ActiveMode  = CvEditorMode.Text;
    }

    private void SetRowsMode()
    {
        if (ActiveMode == CvEditorMode.Rows) return;
        SyncTextToRows();
        ActiveMode = CvEditorMode.Rows;
    }

    private void SyncTextToRows()
    {
        var (newRows, errors) = ParseTextContent(TextContent);
        Rows.Clear();
        foreach (var (v, c) in newRows)
            Rows.Add(new CvRowViewModel(
                v.ToString("G15", CultureInfo.InvariantCulture),
                c.ToString("G15", CultureInfo.InvariantCulture),
                this));

        if (Rows.Count < 2)
        {
            Rows.Clear();
            Rows.Add(new CvRowViewModel("", "", this));
            Rows.Add(new CvRowViewModel("", "", this));
        }
    }

    // ── Validate ──────────────────────────────────────────────────────────────

    internal void Validate()
    {
        var errors = new List<string>();

        if (IsTextMode)
        {
            var (_, textErrors) = ParseTextContent(TextContent);
            errors.AddRange(textErrors);
            int ptCount = ParseTextContent(TextContent).pts.Count;
            int needed  = FitOrder + 1;
            if (ptCount < needed)
                errors.Add($"Need at least {needed} points for order {FitOrder}.");
        }
        else
        {
            for (int i = 0; i < Rows.Count; i++)
            {
                var r    = Rows[i];
                bool vOk = double.TryParse(r.StagedV.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
                bool cOk = double.TryParse(r.StagedC.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
                if (!vOk || !cOk)
                    errors.Add($"Row {i + 1}: V and C must be numbers.");
            }
            int needed = FitOrder + 1;
            if (Rows.Count < needed)
                errors.Add($"Need at least {needed} points for order {FitOrder}.");
        }

        HasValidationErrors = errors.Count > 0;
        ValidationSummary   = errors.Count == 0 ? "" :
                              errors.Count == 1 ? errors[0] :
                              $"{errors[0]}  (+{errors.Count - 1} more)";
        UpdatePreviewPoints();
    }

    // ── Live shape preview ────────────────────────────────────────────────────

    private void UpdatePreviewPoints()
    {
        List<CvPoint>? pts = null;
        if (IsTextMode)
        {
            var (parsed, _) = ParseTextContent(TextContent);
            if (parsed.Count >= 2)
                pts = parsed.OrderBy(p => p.V).Select(p => new CvPoint(p.V, p.C)).ToList();
        }
        else
        {
            var valid = new List<CvPoint>();
            foreach (var r in Rows)
            {
                if (double.TryParse(r.StagedV.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                 && double.TryParse(r.StagedC.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double c))
                    valid.Add(new CvPoint(v, c));
            }
            if (valid.Count >= 2)
                pts = valid.OrderBy(p => p.V).ToList();
        }

        PreviewPoints = pts is not null && pts.Count >= 2 ? pts : null;
    }

    // ── Text-mode parsing (tab/whitespace-delimited, strip ; and // comments) ─

    internal static (List<(double V, double C)> pts, List<string> errors) ParseTextContent(string text)
    {
        var pts    = new List<(double, double)>();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return (pts, errors);

        int lineNum = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            lineNum++;
            string line = rawLine.TrimEnd('\r');

            // Strip trailing comment (;  or //)
            int semiIdx = line.IndexOf(';');
            if (semiIdx >= 0) line = line[..semiIdx];
            int slashIdx = line.IndexOf("//", StringComparison.Ordinal);
            if (slashIdx >= 0) line = line[..slashIdx];

            line = line.Trim();
            if (line.Length == 0) continue;

            // Split on tab first (Excel paste), fall back to any whitespace
            string[] parts = line.Contains('\t')
                ? line.Split('\t', 2)
                : line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2
                || !double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                || !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double c))
            {
                errors.Add($"Line {lineNum}: expected two numbers.");
                continue;
            }

            pts.Add((v, c));
        }

        return (pts, errors);
    }

    // ── Row management (called by view code-behind and CvRowViewModel) ─────────

    private void AddRow()
    {
        Rows.Add(new CvRowViewModel("", "", this));
        Validate();
    }

    internal void RemoveRow(CvRowViewModel row)
    {
        Rows.Remove(row);
        Validate();
    }

    // ── Model change handler ──────────────────────────────────────────────────

    private void OnModelChanged(object? sender, EventArgs e)
    {
        if (_comp is null || _schematicVm is null) return;
        if (_schematicVm.EditModel.FindComponent(_comp.Id) is null)
        {
            _comp = null;
            return;
        }
        InstanceName = _comp.InstanceName;
        OnPropertyChanged(nameof(DialogTitle));
    }

    // ── Serialization helpers (internal so tests can call them) ───────────────

    internal static string SerializeCvData(IEnumerable<CvRowViewModel> rows, int order, string unit)
    {
        string pts = string.Join(";", rows.Select(r => $"{r.StagedV.Trim()},{r.StagedC.Trim()}"));
        return $"{pts}|order={order}|unit={unit}";
    }

    /// <summary>Legacy overload (unit="None") for tests that pre-date unit support.</summary>
    internal static string SerializeCvData(IEnumerable<CvRowViewModel> rows, int order)
        => SerializeCvData(rows, order, "None");

    /// <summary>
    /// Full parse: returns pts (in display units as stored), order, and unit.
    /// pts.C values are what the user typed — caller multiplies by UnitScale before fitting.
    /// </summary>
    internal static (List<(double V, double C)> pts, int order, string unit) ParseCvDataFull(string raw)
    {
        int    pipeIdx  = raw.IndexOf('|');
        string rowsPart = pipeIdx >= 0 ? raw[..pipeIdx] : raw;
        string metaFull = pipeIdx >= 0 ? raw[(pipeIdx + 1)..] : "";

        int    order = 3;
        string unit  = "None";

        foreach (var tag in metaFull.Split('|'))
        {
            var kv = tag.Split('=', 2);
            if (kv.Length != 2) continue;
            switch (kv[0].Trim())
            {
                case "order" when int.TryParse(kv[1].Trim(), out int o): order = o; break;
                case "unit":  unit = kv[1].Trim(); break;
            }
        }

        var pts = new List<(double, double)>();
        if (!string.IsNullOrWhiteSpace(rowsPart))
        {
            foreach (var seg in rowsPart.Split(';'))
            {
                var parts = seg.Split(',');
                if (parts.Length == 2
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double c))
                    pts.Add((v, c));
            }
        }
        return (pts, order, unit);
    }

    /// <summary>Backward-compatible shim: parses pts and order only (unit discarded).</summary>
    internal static (List<(double V, double C)> pts, int order) ParseCvData(string raw)
    {
        var (pts, order, _) = ParseCvDataFull(raw);
        return (pts, order);
    }

    /// <summary>Maps a display-unit string to its SI multiplier.</summary>
    public static double UnitScale(string unit) => unit switch
    {
        "fF" => 1e-15,
        "pF" => 1e-12,
        "nF" => 1e-9,
        "µF" => 1e-6,
        "mF" => 1e-3,
        "F"  => 1.0,
        _    => 1.0,   // "None" and unknown → values are already SI
    };

    private static string SerializeRowsToText(IEnumerable<CvRowViewModel> rows)
        => string.Join(Environment.NewLine, rows.Select(r => $"{r.StagedV.Trim()}\t{r.StagedC.Trim()}"));

    private static string? UnwrapStringLiteral(string? expr)
    {
        if (expr is null || expr.Length < 2) return null;
        return expr[0] == '"' && expr[^1] == '"' ? expr[1..^1] : null;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_schematicVm is not null)
            _schematicVm.EditModel.Changed -= OnModelChanged;
        HookSchematicStack(null);
    }
}

/// <summary>
/// One (V, C) row in the CV editor staging table.
/// Values are staged locally; nothing writes to the component until Apply.
/// </summary>
public sealed partial class CvRowViewModel : ObservableObject
{
    private readonly NonlinearCvEditorViewModel _editor;

    [ObservableProperty] private string _stagedV = "";
    [ObservableProperty] private string _stagedC = "";

    public CvRowViewModel(string v, string c, NonlinearCvEditorViewModel editor)
    {
        _stagedV  = v;
        _stagedC  = c;
        _editor   = editor;
        RemoveCommand = new RelayCommand(() => _editor.RemoveRow(this));
    }

    public IRelayCommand RemoveCommand { get; }

    public void CommitV() { /* values are two-way bound; LostFocus / Enter hook ensures binding flushes */ }
    public void CommitC() { /* values are two-way bound; LostFocus / Enter hook ensures binding flushes */ }
}

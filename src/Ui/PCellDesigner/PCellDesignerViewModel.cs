// Owner: PCD-6 (brief-pcd-6). The PCell Designer's view model: everything a test touches (D1).

using System.Collections.ObjectModel;
using System.Globalization;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Design.Layout.Em.ParametricEm;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.PCellDesigner;
using CircuitRF.Design.PCellDesigner.Operations;
using CircuitRF.Engine;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RfCore;
using FreqUnit = CircuitRF.Render.DataDisplay.FreqUnit;

namespace CircuitRF.Ui.PCellDesigner;

/// <summary>One row of the parameter table (R-pcd6-3): the <c>.ccell</c> parameter, and a Value that is never saved.</summary>
public sealed partial class PCellParameterRow(Action changed) : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _default = "";
    [ObservableProperty] private string _unit = "";
    [ObservableProperty] private string _minimum = "";
    [ObservableProperty] private string _maximum = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _value = "";

    public UnitDimension Dimension { get; set; } = UnitDimension.Length;

    partial void OnValueChanged(string value) => changed();
    partial void OnNameChanged(string value) => changed();
    partial void OnUnitChanged(string value) => changed();

    // A value still at the default follows it.
    partial void OnDefaultChanged(string? oldValue, string newValue)
    {
        if (Value == (oldValue ?? "")) Value = newValue;
    }

    /// <summary>The value as an expression and its unit (brief-pcd-6 D4): <c>200 um</c>, or <c>200</c> in the row's unit.</summary>
    internal (string Expression, string Unit) Split()
    {
        var (e, u) = TunableValue.Split(Value, Unit);
        return (e, u ?? "");
    }
}

/// <summary>One row of the history panel (brief-pcd-14 R-pcd14-3): the operation, its line, whether its step was applied,
/// and its fields as text to edit in place. Apply writes them back through the host.</summary>
public sealed partial class PCellOperationRow(PCellOperation operation, string? refusal, Action<PCellOperationRow> suppress,
                                               Action<PCellOperationRow> keepModel) : ObservableObject
{
    public PCellOperation Operation { get; } = operation;
    public string Id => Operation.Id;
    public string Description => PCellOperations.Describe(Operation);

    /// <summary>Why its step was not applied on the last plan, or null.</summary>
    public string? Refusal { get; } = refusal;
    public bool Refused => Refusal is not null;

    [ObservableProperty] private bool _suppressed = operation.Suppressed;
    partial void OnSuppressedChanged(bool value) { if (value != Operation.Suppressed) suppress(this); }

    /// <summary>Accept the circuit model as an approximation for this step (brief-pcd-13 R-pcd13-4).</summary>
    [ObservableProperty] private bool _keepModel = operation.KeepModel;
    partial void OnKeepModelChanged(bool value) { if (value != Operation.KeepModel) keepModel(this); }

    /// <summary>The operands, space-separated: A, then "|" and B for the two-set kinds (D3).</summary>
    [ObservableProperty] private string _operands = string.Join(" ", operation.A)
        + (operation.B is { Count: > 0 } b ? " | " + string.Join(" ", b) : "");
    [ObservableProperty] private string _amount = operation.Amount ?? operation.Rows ?? "";
    [ObservableProperty] private string _amount2 = operation.Columns ?? operation.Onto ?? "";
    [ObservableProperty] private string _pitchX = operation.PitchX ?? "";
    [ObservableProperty] private string _pitchY = operation.PitchY ?? "";

    public bool IsGrow => Operation.Kind == PCellOperationKind.Grow;
    public bool IsCorner => Operation.Kind is PCellOperationKind.Fillet or PCellOperationKind.Chamfer;
    public bool IsRepeat => Operation.Kind == PCellOperationKind.Repeat;

    /// <summary>The operation with the row's text written back.</summary>
    public PCellOperation Edited()
    {
        var parts = Operands.Split('|');
        List<string> Ids(string t) => [.. t.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        string? Text(string t) => PCellDesignerViewModel.OperationText(t);
        return Operation with
        {
            A = Ids(parts[0]),
            B = parts.Length > 1 ? Ids(parts[1]) : Operation.B,
            Amount = IsGrow || IsCorner ? Text(Amount) : Operation.Amount,
            Onto = IsGrow ? Text(Amount2) : Operation.Onto,
            Rows = IsRepeat ? Text(Amount) : Operation.Rows,
            Columns = IsRepeat ? Text(Amount2) : Operation.Columns,
            PitchX = IsRepeat ? Text(PitchX) : Operation.PitchX,
            PitchY = IsRepeat ? Text(PitchY) : Operation.PitchY,
        };
    }
}

/// <summary>One window per cell folder (R-pcd6-1), as <c>LcFilterDesignerWindows</c> keeps one per Filter.</summary>
public sealed class PCellDesignerWindows<TWindow> where TWindow : class
{
    private readonly Dictionary<string, TWindow> _open = new(StringComparer.Ordinal);

    private static string Key(string cellDir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(cellDir));

    /// <summary>The window already open on <paramref name="cellDir"/>, activated; otherwise a new one.</summary>
    public (TWindow Window, bool Created) Show(string cellDir, Func<TWindow> create, Action<TWindow> activate)
    {
        if (_open.TryGetValue(Key(cellDir), out var existing))
        {
            activate(existing);
            return (existing, false);
        }
        var window = create();
        _open[Key(cellDir)] = window;
        return (window, true);
    }

    public void Closed(string cellDir, TWindow window)
    {
        if (_open.TryGetValue(Key(cellDir), out var w) && ReferenceEquals(w, window)) _open.Remove(Key(cellDir));
    }

    public bool IsOpen(string cellDir) => _open.ContainsKey(Key(cellDir));
}

/// <summary>
/// brief-pcd-6: a designed cell in the designer. The schematic, the values, the undo stack and every gesture live here;
/// the window turns pointer events into these calls.
/// </summary>
public sealed partial class PCellDesignerViewModel : ObservableObject
{
    /// <summary>The VAR row the values ride on in the copy the planner reads; never saved.</summary>
    internal const string ValuesRow = "__PCDVALUES";

    /// <summary>The debounce before a re-plan and response start, as the LC Filter Designer's.</summary>
    internal const int DesignDebounceMs = 80;

    private readonly string _schematicDir;
    private readonly string _layoutDir;
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private readonly PlotContainerViewModel _container;

    public string CellDir { get; }
    public string CellName { get; }
    public string WorkspaceRoot { get; }
    public string SchematicPath { get; }
    public string ClayPath { get; }
    public Technology? Technology { get; }
    public string? TechIdentity { get; }

    /// <summary>The designer's schematic. Replaced, never mutated, by a gesture (one undo step each).</summary>
    public SchematicEditModel Schematic { get; private set; }

    public ObservableCollection<PCellParameterRow> Parameters { get; } = [];

    /// <summary>Opens a schematic in the workspace and shows its Optimizer (R-pcd6-7); the workspace sets it.</summary>
    public Action<string>? OpenOptimizer { get; set; }

    public string Title => $"PCell Designer — {CellName}";

    /// <summary>The planned layout at the current values (R-pcd6-2), or null when it could not be planned.</summary>
    [ObservableProperty] private LayoutView? _layout;

    /// <summary>What the plan reported: its refusal, or its warnings.</summary>
    [ObservableProperty] private string _layoutNote = "";

    /// <summary>The cell's <c>.cnl</c>, live (R-pcd6-4). An edit is merged by Apply.</summary>
    [ObservableProperty] private string _cnlText = "";

    [ObservableProperty] private string _status = "";

    /// <summary>The circuit response at the current values (R-pcd6-5).</summary>
    [ObservableProperty] private SNP? _response;

    /// <summary>The last Run EM result at the current values (R-pcd6-6), overlaid on <see cref="Response"/>.</summary>
    [ObservableProperty] private SNP? _em;

    /// <summary>Why the plot is empty, shown in its place; null while it has a trace.</summary>
    [ObservableProperty] private string? _responseNote;
    private string? _responseWhy;

    [ObservableProperty] private bool _busy;

    /// <summary>True when the schematic or the table changed since the last Save.</summary>
    [ObservableProperty] private bool _dirty;

    /// <summary>The Data Display the response is drawn on, as the Filter Designer's.</summary>
    public DataDisplayViewModel PlotHost { get; } = new(new DataSourceLibraryViewModel(), addEmptyPlot: false, selectEmptyPlot: false);

    public Plot Plot => _container.PlotVM.Plot;

    /// <summary>The response plot's container: what the window wires the plot's marker tools to (R-pcd10-1).</summary>
    public PlotContainerViewModel ResponseContainer => _container;

    public PCellDesignerViewModel(string cellDir, string workspaceRoot)
    {
        CellDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cellDir));
        CellName = Path.GetFileName(CellDir);
        WorkspaceRoot = Path.GetFullPath(workspaceRoot);
        _schematicDir = CellFolder.SubFolderPath(CellDir, ViewType.Schematic);
        _layoutDir = CellFolder.SubFolderPath(CellDir, ViewType.Layout);
        SchematicPath = PCellDesignSave.SchematicPath(CellDir);
        ClayPath = PCellDesignSave.ClayPath(CellDir);

        Schematic = File.Exists(SchematicPath) ? SchematicPersistence.LoadFromFile(SchematicPath).model : new SchematicEditModel();
        Schematic.SchematicDirectory = _schematicDir;

        (Technology, TechIdentity) = PCellDesignSave.Technology(CellDir, WorkspaceRoot);
        _filterSpec = PCellFilter.SpecOf(CellDir);

        foreach (var p in CellFolder.ReadCcell(CellDir)?.Parameters ?? [])
            Parameters.Add(new PCellParameterRow(OnTableChanged)
            {
                Name = p.Name, Default = p.DefaultExpression, Unit = p.Unit, Dimension = p.Dimension,
                Minimum = SiText(p.Minimum, p.Unit), Maximum = SiText(p.Maximum, p.Unit), Description = p.Description ?? "",
                Value = p.DefaultExpression,
            });
        Parameters.CollectionChanged += (_, _) => OnTableChanged();
        Selection.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SelectionText));
        _container = PlotHost.AddPlot(PlotType.Rect, FreqUnit.GHz, left: 0, top: 0, width: 640, height: 400);
        CnlText = CnlOf(Schematic);
        RebuildOperations();
        _ready = true;
    }

    private bool _ready;

    private void OnTableChanged()
    {
        if (!_ready) return;
        Dirty = true;
        Refresh();
    }

    // ══ Entry points (R-pcd6-1) ═══════════════════════════════════════════════════════════════════════════════════

    /// <summary>The designed cell <paramref name="schematicPath"/> is the schematic of, or null.</summary>
    public static string? DesignedCellOf(string? schematicPath) =>
        schematicPath is not null && EmCapability.CellDirOfSchematic(schematicPath) is { } cell && PCellDesign.IsDesigned(cell) ? cell : null;

    /// <summary>The designed cell a placed component references, or null.</summary>
    public static string? DesignedCellOf(EditableComponent? component, string? schematicDir) =>
        component?.CellRef is { Length: > 0 } cellRef && ExternalCellRef.ResolveCellDir(cellRef, schematicDir) is { } cell
        && PCellDesign.IsDesigned(cell) ? cell : null;

    /// <summary>The designed cell a placed layout instance was generated from (<c>cpcell:&lt;Name&gt;</c>), or null.</summary>
    public static string? DesignedCellOf(LayoutInstance instance, string baseDir, Technology? technology) =>
        CellLayoutResolver.Resolve(instance, baseDir, technology).View?.PCellOrigin?.GeneratorId is { } id
        && id.StartsWith(PCellDesign.GeneratorPrefix, StringComparison.Ordinal) ? DesignedPCellResolver.CellDirOf(id) : null;

    /// <summary>A new designed cell <c>PCell1</c> (<c>PCell2</c>, … when taken) in the workspace (D3).</summary>
    public static CellCreateResult NewCell(string workspaceRoot)
    {
        int k = 1;
        while (Directory.Exists(Path.Combine(workspaceRoot, $"PCell{k}"))) k++;
        return PCellDesign.Create(workspaceRoot, $"PCell{k}");
    }

    /// <summary>File ▸ Import ▸ PCell Package… (R-pcd6-8): the package into <paramref name="libraryDir"/>; the status line.</summary>
    public static (string? CellDir, string Status) Import(string packagePath, string libraryDir)
    {
        PCellPackageOutcome o;
        try { o = PCellPackage.Import(packagePath, libraryDir); }
        catch (Exception e) when (e is not OutOfMemoryException) { return (null, $"{Path.GetFileName(packagePath)} was not imported: {e.Message}"); }
        string findings = string.Join(" ", o.Findings.Select(f => f.Render()));
        return o.Path is { } dir
            ? (dir, $"Imported {Path.GetFileName(dir)} into {libraryDir}." + (findings.Length > 0 ? " " + findings : ""))
            : (null, $"{Path.GetFileName(packagePath)} was not imported: {findings}");
    }

    // ══ The live design loop (R-pcd6-2, R-pcd6-5) ═════════════════════════════════════════════════════════════════

    private CancellationTokenSource? _designCts;
    private int _designGeneration;

    /// <summary>Re-plans the canvas and re-runs the response, debounced; only the latest request's result is shown.</summary>
    [RelayCommand]
    public void Refresh() => _ = DesignAsync();

    public async Task DesignAsync(int debounceMs = DesignDebounceMs)
    {
        _designCts?.Cancel();
        var cts = new CancellationTokenSource();
        _designCts = cts;
        int generation = ++_designGeneration;
        if (debounceMs > 0)
        {
            try { await Task.Delay(debounceMs, cts.Token); }
            catch (OperationCanceledException) { return; }
        }
        if (generation != _designGeneration) return;

        // Read on this (UI) thread; the pool thread works on its own copies.
        string text = SchematicPersistence.Serialize(Schematic, CellName);
        var sets = Parameters.Where(p => p.Name.Length > 0 && p.Value.Length > 0)
                             .Select(p => { var (e, u) = p.Split(); return (p.Name, TunableValue.Text(e, u)); }).ToList();
        var values = ValuesRowOf();
        string? sweep = CellFolder.ReadCcell(CellDir)?.PCellDesign?.Sweep;
        var (tech, techId) = (Technology, TechIdentity);
        var emOnly = PCellOperations.WithoutCircuitModel(Schematic, Technology);
        DesignedCellLayoutResult? plan;
        PCellDesignResponseResult response;
        try
        {
            (plan, response) = await Task.Run(() =>
            {
                DesignedCellLayoutResult? p = null;
                if (tech is not null)
                {
                    var copy = Copy(text);
                    if (values.Parameters.Count > 0) copy.Components.Add(values);
                    p = DesignedCellLayout.At(CellDir, new Dictionary<string, Value>(), WorkspaceRoot, tech, techId, copy);
                }
                cts.Token.ThrowIfCancellationRequested();
                // R-pcd14-5: an operation that changes the copper makes the cell EM-only; its circuit model is not run.
                return (p, emOnly.Count > 0
                    ? new PCellDesignResponseResult(null, $"no circuit model: {string.Join(", ", emOnly)} "
                        + $"{(emOnly.Count == 1 ? "changes" : "change")} the copper, so the cell is simulated by EM only. Run EM for its response.")
                    : PCellDesignResponse.Run(Copy(text), sets, sweep, WorkspaceRoot, cts.Token));
            }, cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            (plan, response) = (null, new PCellDesignResponseResult(null, e.Message));
        }
        if (generation != _designGeneration) return;

        Layout = plan?.Layout;
        LayoutNote = tech is null ? "The cell's layout resolves no technology, so it cannot be planned."
            : plan?.Refusal ?? string.Join("\n", plan!.Lines.Where(l => l.Severity == SchematicLayoutPlanner.ReportSeverity.Warning)
                                                            .Select(l => l.Text));
        Dimensions = DimensionsNow();
        Response = response.S;
        Em = null;   // the EM result was for the values before this change
        _responseWhy = response.Refusal;
        if (response.Refusal is { } why) Status = "Response: " + why;
        RebuildOperations();
        // A filter cell's metrics, read off the response just computed (PCellFilter.CircuitMetrics's own two steps, so
        // the response is not run twice).
        if (_filterSpec is { } spec)
            FilterMetrics = response.S is { } s ? PCellFilter.Metrics(spec, s, CircuitSource) : new PCellFilterMetrics(null, CircuitSource, response.Refusal);
        BuildPlot();
    }

    // ══ A filter cell (brief-pcd-12): its metrics and Polish ═════════════════════════════════════════════════════

    private const string CircuitSource = "Circuit response";

    /// <summary>The filter specification the cell keeps when Make PCell… made it; null for any other cell.</summary>
    private readonly FilterSpec? _filterSpec;

    /// <summary>True for a cell made from filter synthesis: the window shows its metrics and Polish.</summary>
    public bool IsFilterCell => _filterSpec is not null;

    /// <summary>The cell's filter metrics: from the circuit response after each run, from the EM result after Run EM.</summary>
    [ObservableProperty] private PCellFilterMetrics? _filterMetrics;

    /// <summary>Polish (<see cref="PCellFilter.Polish"/>): saves, corrects the cell's dimensions on its circuit model and
    /// writes them back as the defaults; the table follows.</summary>
    [RelayCommand]
    public void Polish()
    {
        if (Busy || !IsFilterCell) return;
        Work = RunWork("Polish", async token =>
        {
            if (SaveCore() is { } why) return why;
            var o = await Task.Run(() => PCellFilter.Polish(CellDir, WorkspaceRoot, 3, token), token);
            if (o.Refusal is { } refused) return refused;
            foreach (var p in o.Parameters)
                if (Parameters.FirstOrDefault(r => r.Name == p.Name) is { } row) row.Default = p.DefaultExpression;
            Dirty = false;
            return string.Create(CultureInfo.InvariantCulture,
                $"worst return loss across the band {o.BeforeDb:0.0} dB before, {o.AfterDb:0.0} dB after; the defaults are the polished dimensions.");
        });
    }

    private SchematicEditModel Copy(string text)
    {
        var copy = SchematicPersistence.Deserialize(text, _schematicDir).model;
        copy.SchematicDirectory = _schematicDir;
        return copy;
    }

    /// <summary>The values as one VAR row, so the planner's copy carries a parameter the saved <c>.ccell</c> has not got yet.</summary>
    private EditableComponent ValuesRowOf()
    {
        var row = new EditableComponent { InstanceName = ValuesRow, Symbol = SymbolKind.Var };
        foreach (var p in Parameters.Where(p => p.Name.Length > 0 && p.Value.Length > 0))
        {
            var (e, u) = p.Split();
            row.Parameters.Add(new EditableParameter { Name = p.Name, Expression = e, Unit = u, Dimension = p.Dimension });
        }
        return row;
    }

    /// <summary>The cell's scope at the current values: its VARs and parameters, each value bound with its unit.</summary>
    internal Scope ScopeAtValues()
    {
        var model = Copy(SchematicPersistence.Serialize(Schematic, CellName));
        var row = ValuesRowOf();
        if (row.Parameters.Count > 0) model.Components.Add(row);
        var scope = new Scope("global");
        foreach (var v in NetExtractor.Extract(model, "tb", DiskCellResolver.Instance).TestBench.GlobalVariables)
            scope.Bind(v.Name, v.Expression, v.Unit);
        return scope;
    }

    /// <summary>The values in base SI, by parameter name, for EM (only those that evaluate).</summary>
    internal Dictionary<string, Value> SiValues()
    {
        var scope = ScopeAtValues();
        var evaluator = new Evaluator();
        var values = new Dictionary<string, Value>(StringComparer.Ordinal);
        foreach (var p in Parameters.Where(p => p.Name.Length > 0))
            try { if (evaluator.Eval(p.Name, scope) is { Kind: ValueKind.Real } v) values[p.Name] = v; }
            catch (Exception) { /* not evaluable: EM keeps the saved default */ }
        return values;
    }

    /// <summary>The cell parameters as dimensions on the planned layout (brief-pcd-10 R-pcd10-2).</summary>
    [ObservableProperty] private IReadOnlyList<PCellDimensionLine> _dimensions = [];

    /// <summary>Draw <see cref="Dimensions"/> over the canvas; on by default, remembered (R-pcd10-3).</summary>
    [ObservableProperty] private bool _showDimensions = CircuitRF.Ui.Theming.AppPreferencesIo.Load().PCellDesignerDimensions ?? true;

    partial void OnShowDimensionsChanged(bool value) =>
        CircuitRF.Ui.Theming.AppPreferencesIo.Update(p => p.PCellDesignerDimensions = value ? null : false);

    private IReadOnlyList<PCellDimensionLine> DimensionsNow()
    {
        if (Layout is null) return [];
        var units = Parameters.Where(p => p.Name.Length > 0).GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First().Unit);
        try { return PCellDimensions.Of(Layout, _layoutDir, Technology, Schematic, ScopeAtValues(), units); }
        catch (Exception e) when (e is not OutOfMemoryException) { return []; }
    }

    /// <summary>Markers by trace name, kept across a re-run while their trace is gone (brief-pcd-10 R-pcd10-1, D3).</summary>
    private readonly Dictionary<string, List<Marker>> _parkedMarkers = new(StringComparer.Ordinal);

    private void BuildPlot()
    {
        // The response is rebuilt on every edit: each marker goes to the new trace of the same name, at its own x.
        foreach (var old in Plot.Traces)
            if (old.Markers.Count > 0) _parkedMarkers[old.Label ?? ""] = [.. old.Markers];
            else _parkedMarkers.Remove(old.Label ?? "");
        Plot.Traces.Clear();
        void Add(SNP? snp, int row, int colour, string label)
        {
            if (snp is null) return;
            int index = TraceProperties.LineColorOrder[colour % TraceProperties.LineColorOrder.Length];
            var t = new Trace(snp, MatrixType.S, row, 0, DependentVarFormat.Db, false,
                new TraceProperties { LineColorIndex = index, MarkerColorIndex = index, FillColorIndex = index, Custom = false })
                { Label = label };
            if (_parkedMarkers.Remove(label, out var kept)) t.Markers.AddRange(kept);
            t.BuildPath(PlotType.Rect, Plot.FreqUnits);
            Plot.Traces.Add(t);
        }
        for (int i = 0; i < (Response?.Ports ?? 0); i++) Add(Response, i, i, $"S{i + 1}1");
        for (int i = 0; i < (Em?.Ports ?? 0); i++) Add(Em, i, i + 4, $"EM S{i + 1}1");
        Plot.CustomTitleOn = true;
        Plot.CustomTitle = "Response";
        if (Plot.Traces.Count > 0) Plot.Autoscale(force: true);
        ResponseNote = Plot.Traces.Count > 0 ? null : "No response: " + (_responseWhy ?? "not run yet.");
        _container.OnPlotChanged(this, EventArgs.Empty);
        OnPropertyChanged(nameof(Plot));
        _container.RequestPlotRedraw();
    }

    // ══ Undo, and the gestures (R-pcd6-2, R-pcd6-4) ═══════════════════════════════════════════════════════════════

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>One undo step: the current schematic onto the stack, <paramref name="next"/> in its place.</summary>
    private void Commit(SchematicEditModel next, string status)
    {
        _undo.Push(SchematicPersistence.Serialize(Schematic, CellName));
        _redo.Clear();
        Show(next);
        Status = status;
    }

    private void Show(SchematicEditModel next)
    {
        Schematic = next;
        Schematic.SchematicDirectory = _schematicDir;
        CnlText = CnlOf(Schematic);
        RebuildOperations();
        Dirty = true;
        OnPropertyChanged(nameof(Schematic));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        Refresh();
    }

    private string CnlOf(SchematicEditModel model)
    {
        try { return SchematicCircuit.CnlTextOf(model, CellName); }
        catch (Exception e) when (e is not OutOfMemoryException) { return $"// the netlist could not be extracted: {e.Message}"; }
    }

    [RelayCommand]
    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(SchematicPersistence.Serialize(Schematic, CellName));
        Show(Copy(_undo.Pop()));
    }

    [RelayCommand]
    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(SchematicPersistence.Serialize(Schematic, CellName));
        Show(Copy(_redo.Pop()));
    }

    /// <summary>A gesture's call into another lane's function: its refusal, or its exception, is the status line and
    /// nothing changes.</summary>
    private bool Gesture(string what, Func<(SchematicEditModel? Next, string Status)> make)
    {
        (SchematicEditModel? Next, string Status) r;
        try { r = make(); }
        catch (Exception e) when (e is not OutOfMemoryException) { r = (null, e.Message); }
        if (r.Next is null)
        {
            Status = $"{what}: {r.Status}";
            return false;
        }
        Commit(r.Next, r.Status);
        return true;
    }

    [RelayCommand]
    private void ApplyEdit() => ApplyText();

    /// <summary>R-pcd6-4: the text merged into the schematic by designator.</summary>
    public bool ApplyText() => Gesture("Apply", () =>
    {
        var m = CellNetlistMerge.Merge(Schematic, CnlText);
        return (m.Schematic, m.Schematic is null ? string.Join(" ", m.Refusals) : string.Join(" ", m.Notes.Prepend("Text applied.")));
    });

    /// <summary>A Library part dropped on a pin: <see cref="PinDrop.Apply"/>.</summary>
    public bool DropPart(EditableComponent part, int partPin, string targetInstance, int targetPin) => Gesture("Drop", () =>
    {
        var r = PinDrop.Apply(Schematic, part, partPin, targetInstance, targetPin);
        return (r.Schematic, r.Schematic is null ? r.Refusal ?? "refused" : $"{r.InstanceName} pin {partPin} joined to {targetInstance} pin {targetPin}.");
    });

    /// <summary>A part as a Library palette tile places it: its registry defaults, the next free designator.</summary>
    public EditableComponent PartFor(SymbolKind kind, int portCount)
    {
        var comp = new EditableComponent
        {
            Symbol = kind,
            InstanceName = SchematicEditModel.NextAvailableName(Schematic.Components.Select(c => c.InstanceName),
                                                                ComponentTypeRegistry.InstancePrefix(kind)),
        };
        int ports = portCount > 0 ? portCount : SymbolPortDefs.For(kind).Length;
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, ports))
            comp.Parameters.Add(new EditableParameter
                { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension });
        // A port takes the next number (R-pcd11-1).
        if (comp.Parameters.FirstOrDefault(p => p.Name == "Num") is { } num && kind is SymbolKind.Term or SymbolKind.TermG)
            num.Expression = (PCellDesign.TerminalsOf(Schematic).Select(t => t.Port).DefaultIfEmpty(0).Max() + 1)
                             .ToString(CultureInfo.InvariantCulture);
        return comp;
    }

    /// <summary>An offset part dragged so its offset pin lands at (<paramref name="toX"/>, <paramref name="toY"/>), DBU:
    /// <see cref="OffsetPlacer.Drag"/> writes the change into its expressions.</summary>
    public bool DragOffset(string instance, long toX, long toY) => Gesture("Drag", () =>
    {
        if (Schematic.Components.FirstOrDefault(c => c.InstanceName == instance)?.Offset is not { } offset)
            return (null, $"{instance} has no offset placement.");
        int dot = offset.Anchor.LastIndexOf('.');
        if (dot <= 0 || !int.TryParse(offset.Anchor[(dot + 1)..], out int anchorPin)
            || PinPoseOf(offset.Anchor[..dot], anchorPin) is not { } anchor || PinPoseOf(instance, offset.Pin) is not { } now)
            return (null, $"{instance}'s anchor {offset.Anchor} is not placed.");
        var next = Copy(SchematicPersistence.Serialize(Schematic, CellName));
        next.Components.First(c => c.InstanceName == instance).Offset =
            OffsetPlacer.Drag(offset, anchor, now, toX, toY, Layout!.DbuPerMicron);
        return (next, $"{instance} moved: Dx and Dy now hold the change.");
    });

    /// <summary>Stretch by Parameter…: the <c>MSHAPE</c>'s <paramref name="moving"/> vertices follow
    /// <paramref name="parameter"/> along <paramref name="axisDeg"/> (<see cref="DrawnMetal.Stretch"/>); the anchor is the
    /// first vertex not moving.</summary>
    public bool StretchByParameter(string instance, IReadOnlyList<int> moving, string parameter, double axisDeg) => Gesture("Stretch", () =>
    {
        int at = Schematic.Components.FindIndex(c => c.InstanceName == instance);
        if (at < 0 || !DrawnMetal.Is(Schematic.Components[at])) return (null, $"{instance} is not drawn metal.");
        var shape = Schematic.Components[at];
        int count = Enumerable.Range(1, int.MaxValue - 1).TakeWhile(i => shape.Parameters.Any(p => p.Name == DrawnMetal.X(i))).Count();
        int anchor = Enumerable.Range(1, count).FirstOrDefault(i => !moving.Contains(i));
        if (moving.Count == 0 || anchor == 0) return (null, "select the vertices that move, and leave at least one that stays.");
        var binding = new StretchBinding { Parameter = parameter, Anchor = anchor, Moving = [.. moving], AxisDeg = axisDeg };
        var stretched = DrawnMetal.Stretch(shape, binding, ScopeAtValues(), new Evaluator());
        var next = Copy(SchematicPersistence.Serialize(Schematic, CellName));
        next.Components[at] = stretched;
        return (next, $"{instance}: vertices {string.Join(", ", moving)} follow {parameter}.");
    });

    /// <summary>An <c>MSHAPE</c> vertex dragged from one world point to another (DBU): the change, in the part's own frame,
    /// is added to <c>X[i]</c> and <c>Y[i]</c> as a literal in each row's unit, so a parameter term stays.</summary>
    public bool DragVertex(string instance, int vertex, long fromX, long fromY, long toX, long toY) => Gesture("Drag vertex", () =>
    {
        int at = Schematic.Components.FindIndex(c => c.InstanceName == instance);
        if (at < 0 || !DrawnMetal.Is(Schematic.Components[at])) return (null, $"{instance} is not drawn metal.");
        if (Layout?.Instances.FirstOrDefault(i => i.SchematicId == instance) is not { } placed) return (null, $"{instance} is not placed.");
        var (ax, ay) = LayoutInstanceTransform.InverseTransformPoint(fromX, fromY, placed, 0, 0);
        var (bx, by) = LayoutInstanceTransform.InverseTransformPoint(toX, toY, placed, 0, 0);
        double metresPerDbu = 1e-6 / Layout.DbuPerMicron;
        var next = Copy(SchematicPersistence.Serialize(Schematic, CellName));
        var comp = next.Components[at];
        foreach (var (name, delta) in new[] { (DrawnMetal.X(vertex), (bx - ax) * metresPerDbu), (DrawnMetal.Y(vertex), (by - ay) * metresPerDbu) })
            if (comp.Parameters.FirstOrDefault(p => p.Name == name) is { } row)
                row.Expression = PlusLiteral(row.Expression, delta / (Units.Scale(UnitNormalizer.ToEngineUnit(row.Unit)) ?? 1.0));
        return (next, $"{instance} vertex {vertex} moved.");
    });

    /// <summary><paramref name="expression"/> plus <paramref name="delta"/>: a plain number becomes the sum, anything else
    /// gains a trailing literal. Six significant figures, the precision of a drag.</summary>
    internal static string PlusLiteral(string expression, double delta)
    {
        string d = Math.Abs(delta).ToString("G6", CultureInfo.InvariantCulture);
        if (Math.Abs(delta) < 1e-12) return expression;
        if (double.TryParse(expression, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return (v + delta).ToString("G6", CultureInfo.InvariantCulture);
        return $"{expression.Trim()} {(delta < 0 ? "-" : "+")} {d}";
    }

    // ══ Authoring on the canvas (brief-pcd-11) ════════════════════════════════════════════════════════════════════

    /// <summary>The cell's length unit (brief-pcd-11 D2): the first length parameter's, else the layout's display unit.</summary>
    public string LengthUnit =>
        Parameters.FirstOrDefault(p => p.Dimension == UnitDimension.Length && p.Unit.Length > 0)?.Unit
        ?? (Layout is { } l ? LayoutUnits.Suffix(l.DisplayUnit).Replace("µm", "um") : "um");

    private (string Instance, int Pin, PinPose Pose)? NearestPin(long x, long y) =>
        Pins().OrderBy(p => Math.Pow(p.Pose.X - x, 2) + Math.Pow(p.Pose.Y - y, 2))
              .Select(p => ((string, int, PinPose)?)p).FirstOrDefault();

    /// <summary>R-pcd11-2: a part dropped on empty canvas, placed by an offset from the nearest pin so its pin 1 lands
    /// at (<paramref name="x"/>, <paramref name="y"/>). On a cell with nothing placed yet it joins the first free port
    /// (R-pcd11-1).</summary>
    public bool DropPartAt(EditableComponent part, long x, long y)
    {
        if (NearestPin(x, y) is not { } near)
        {
            if (PCellAuthoring.FreePins(Schematic, ports: true).FirstOrDefault() is { Instance: not null } port)
                return DropPart(part, 1, port.Instance, port.Pin);
            Status = "Drop: the cell has no pin to place the part from. Add a Port first.";
            return false;
        }
        return Gesture("Drop", () =>
        {
            var (next, name) = PCellAuthoring.OffsetPlaced(Schematic, part, near.Instance, near.Pin, near.Pose, x, y,
                                                          Layout!.DbuPerMicron, LengthUnit);
            return (next, $"{name} placed by an offset from {near.Instance} pin {near.Pin}.");
        });
    }

    [RelayCommand]
    private void NewPort() => AddPort();

    /// <summary>+ Port (R-pcd11-1): the next port, grounded, on <paramref name="instance"/>'s pin, or on the last free
    /// pin when none is named.</summary>
    public bool AddPort(string? instance = null, int pin = 0)
    {
        if (instance is null)
        {
            if (PCellAuthoring.FreePins(Schematic).LastOrDefault() is not { Instance: not null } free)
            {
                Status = "+ Port: no part has a free pin to put a port on.";
                return false;
            }
            (instance, pin) = free;
        }
        return DropPart(PartFor(SymbolKind.Term, 0), 1, instance, pin);
    }

    /// <summary>R-pcd11-4: an <c>MSHAPE</c> from clicked corners (DBU). Pin 1 is the nearest pin when the first corner is
    /// within <paramref name="tolerance"/> of it (joined by net), else the first corner, placed by an offset from the
    /// nearest pin.</summary>
    public bool DrawShape(IReadOnlyList<(long X, long Y)> corners, long tolerance)
    {
        if (corners.Count < 3) { Status = "Draw: a shape needs three corners or more."; return false; }
        if (NearestPin(corners[0].X, corners[0].Y) is not { } near)
        {
            Status = "Draw: the cell has no pin to place the shape from. Drop a part first.";
            return false;
        }
        bool joined = Math.Max(Math.Abs(near.Pose.X - corners[0].X), Math.Abs(near.Pose.Y - corners[0].Y)) <= tolerance;
        var width = Schematic.Components.FirstOrDefault(c => c.InstanceName == near.Instance)?.Parameters.FirstOrDefault(p => p.Name == "W");
        var shape = PCellAuthoring.Shape(corners, joined ? (near.Pose.X, near.Pose.Y) : corners[0], near.Pose.OutwardDeg,
                                         Layout!.DbuPerMicron, LengthUnit, joined ? width : null);
        shape.InstanceName = SchematicEditModel.NextAvailableName(Schematic.Components.Select(c => c.InstanceName),
                                                                  ComponentTypeRegistry.InstancePrefix(SymbolKind.MShape));
        if (joined) return DropPart(shape, 1, near.Instance, near.Pin);
        return Gesture("Draw", () =>
        {
            var (next, name) = PCellAuthoring.OffsetPlaced(Schematic, shape, near.Instance, near.Pin, near.Pose,
                                                          corners[0].X, corners[0].Y, Layout.DbuPerMicron, LengthUnit);
            return (next, $"{name} drawn, placed by an offset from {near.Instance} pin {near.Pin}.");
        });
    }

    // ══ Operands (brief-pcd-14 R-pcd14-1) ══════════════════════════════════════════════════════════════════════

    /// <summary>The selected operands, first selected first: designators, and <c>layer:&lt;name&gt;</c> for a layer.</summary>
    public ObservableCollection<string> Selection { get; } = [];

    /// <summary>The selection as the window lists it.</summary>
    public string SelectionText => Selection.Count == 0 ? "Click a part to select it; Shift-click adds or removes." : string.Join("  ", Selection);

    /// <summary>The technology's layers, for the layer picker.</summary>
    public IReadOnlyList<string> LayerNames => Technology?.Layers.Select(l => l.Name).Where(n => n.Length > 0).ToList() ?? [];

    /// <summary>The placement under (<paramref name="x"/>, <paramref name="y"/>), the smallest box first (D2): a part by its
    /// instance's box, an <c>MSHAPE</c> (a tool draws no copper) by its corners'.</summary>
    public string? PartAt(long x, long y)
    {
        if (Layout is null) return null;
        Bbox BoxOf(LayoutInstance inst)
        {
            var box = CellHierarchy.InstanceBbox(inst, _layoutDir);
            if (!box.IsEmpty || Schematic.Components.FirstOrDefault(c => c.InstanceName == inst.SchematicId) is not { } comp
                || !DrawnMetal.Is(comp)) return box;
            foreach (var v in PCellDimensions.Vertices(comp, inst, Layout.DbuPerMicron, ScopeAtValues()))
                box = box.Union(new Bbox(v.X, v.Y, v.X, v.Y));
            return box;
        }
        return Layout.Instances.Where(i => i.SchematicId is { Length: > 0 })
            .Select(i => (Id: i.SchematicId!, Box: BoxOf(i)))
            .Where(b => b.Box.Contains(x, y))
            .OrderBy(b => (double)(b.Box.MaxX - b.Box.MinX) * (b.Box.MaxY - b.Box.MinY))
            .Select(b => b.Id).FirstOrDefault();
    }

    /// <summary>A click on the canvas: the part under it becomes the selection, or, with <paramref name="additive"/>
    /// (Shift), is added or removed. A click on nothing clears a plain selection.</summary>
    public void SelectAt(long x, long y, bool additive)
    {
        string? id = PartAt(x, y);
        if (!additive) Selection.Clear();
        if (id is null) return;
        if (additive && Selection.Remove(id)) return;
        Selection.Add(id);
    }

    /// <summary>The layer picker: <c>layer:&lt;name&gt;</c> added to the selection.</summary>
    public void AddLayerOperand(string layer)
    {
        string operand = "layer:" + layer;
        if (layer.Length > 0 && !Selection.Contains(operand)) Selection.Add(operand);
    }

    // ══ Operations (brief-pcd-14 R-pcd14-2 to -5) ══════════════════════════════════════════════════════════════

    /// <summary>The history, one row per operation in order; rebuilt after every edit and plan.</summary>
    public ObservableCollection<PCellOperationRow> Operations { get; } = [];

    /// <summary>Grow: the amount (an expression; negative shrinks) and the layer the result goes to (empty: the same).</summary>
    [ObservableProperty] private string _growAmount = "50 um";
    [ObservableProperty] private string _growOnto = "";

    /// <summary>Repeat: the counts and pitches, expressions.</summary>
    [ObservableProperty] private string _repeatRows = "1";
    [ObservableProperty] private string _repeatColumns = "2";
    [ObservableProperty] private string _repeatPitchX = "1 mm";
    [ObservableProperty] private string _repeatPitchY = "0";

    public bool HasOperations => Operations.Count > 0;

    /// <summary>An operation field as typed (the engine reads <c>20 um</c>, <c>20um</c> or an expression); empty is null.</summary>
    internal static string? OperationText(string text) => text.Trim().Length > 0 ? text.Trim() : null;

    private void RebuildOperations()
    {
        var steps = Layout is null ? null : PCellOperations.TraceOf(Layout)?.Steps.ToDictionary(t => t.Id, StringComparer.Ordinal);
        Operations.Clear();
        foreach (var op in PCellOperations.Of(Schematic))
            Operations.Add(new PCellOperationRow(op, steps?.GetValueOrDefault(op.Id) is { Applied: false } step ? step.Refusal ?? "not applied" : null,
                                                 row => SuppressOperation(row.Id, row.Suppressed),
                                                 row => KeepModelOperation(row.Id, row.KeepModel)));
        OnPropertyChanged(nameof(HasOperations));
    }

    private bool OperationGesture(string what, Func<PCellOperationEdit> edit) => Gesture(what, () =>
    {
        var e = edit();
        return (e.Schematic, e.Schematic is null ? e.Refusal ?? "refused" : $"{what}: done. The canvas and the response follow.");
    });

    /// <summary>R-pcd14-2: Merge, Subtract (the first selected minus the rest), Intersect, Exclusive, Grow, Repeat, Fillet
    /// or Chamfer on the selection, added to the history as one undo step.</summary>
    public bool AddOperation(PCellOperationKind kind)
    {
        var ids = Selection.ToList();
        bool twoSets = kind is PCellOperationKind.Merge or PCellOperationKind.Subtract or PCellOperationKind.Intersect or PCellOperationKind.Exclusive;
        if (ids.Count < (twoSets ? 2 : 1))
        {
            Status = $"{kind}: select {(twoSets ? "two operands or more (the first is the one worked on)" : "an operand")} first.";
            return false;
        }
        string? Text(string t) => PCellDesignerViewModel.OperationText(t);
        var op = kind switch
        {
            PCellOperationKind.Grow => new PCellOperation { Kind = kind, A = ids, Amount = Text(GrowAmount), Onto = Text(GrowOnto), Unit = LengthUnit },
            PCellOperationKind.Repeat => new PCellOperation
            {
                Kind = kind, A = ids, Rows = Text(RepeatRows), Columns = Text(RepeatColumns),
                PitchX = Text(RepeatPitchX), PitchY = Text(RepeatPitchY), Unit = LengthUnit,
            },
            _ => new PCellOperation { Kind = kind, A = [ids[0]], B = [.. ids.Skip(1)], Unit = LengthUnit },
        };
        return OperationGesture(kind.ToString(), () => PCellOperations.Add(Schematic, op));
    }

    [RelayCommand] private void Merge() => AddOperation(PCellOperationKind.Merge);
    [RelayCommand] private void Subtract() => AddOperation(PCellOperationKind.Subtract);
    [RelayCommand] private void Intersect() => AddOperation(PCellOperationKind.Intersect);
    /// <summary>Fillet: the radius; Chamfer: the leg along each edge. Expressions.</summary>
    [ObservableProperty] private string _filletRadius = "100 um";
    [ObservableProperty] private string _chamferSize = "100 um";

    [RelayCommand] private void Exclusive() => AddOperation(PCellOperationKind.Exclusive);
    [RelayCommand] private void Grow() => AddOperation(PCellOperationKind.Grow);
    [RelayCommand] private void Repeat() => AddOperation(PCellOperationKind.Repeat);

    /// <summary>R-pcd14-3: the row's edited fields written back (<see cref="PCellOperations.Edit"/>).</summary>
    public bool EditOperation(string id, PCellOperation operation) =>
        OperationGesture($"Edit {id}", () => PCellOperations.Edit(Schematic, id, operation));

    public bool MoveOperation(string id, int index) =>
        OperationGesture($"Move {id}", () => PCellOperations.Move(Schematic, id, index));

    public bool SuppressOperation(string id, bool suppressed) =>
        OperationGesture($"{(suppressed ? "Suppress" : "Unsuppress")} {id}", () => PCellOperations.Suppress(Schematic, id, suppressed));

    /// <summary>Keep model on a step: its circuit model stands as an approximation, so it no longer makes the cell
    /// EM-only. One undo step.</summary>
    public bool KeepModelOperation(string id, bool keep) =>
        PCellOperations.Of(Schematic).FirstOrDefault(o => o.Id == id) is { } op
        && OperationGesture($"{(keep ? "Keep model on" : "EM only for")} {id}", () => PCellOperations.Edit(Schematic, id, op with { KeepModel = keep }));

    public bool DeleteOperation(string id) =>
        OperationGesture($"Delete {id}", () => PCellOperations.Delete(Schematic, id));

    [RelayCommand] private void ApplyOperationRow(PCellOperationRow? row) { if (row is not null) EditOperation(row.Id, row.Edited()); }
    [RelayCommand] private void DeleteOperationRow(PCellOperationRow? row) { if (row is not null) DeleteOperation(row.Id); }
    [RelayCommand] private void MoveOperationUp(PCellOperationRow? row) { if (row is not null) MoveOperation(row.Id, Math.Max(0, Operations.IndexOf(row) - 1)); }
    [RelayCommand] private void MoveOperationDown(PCellOperationRow? row) { if (row is not null) MoveOperation(row.Id, Operations.IndexOf(row) + 1); }

    /// <summary>R-pcd14-4: the selected <c>MSHAPE</c> becomes a cutter (<c>Role = "tool"</c>, <c>Model = "open"</c>), one
    /// undo step.</summary>
    [RelayCommand]
    private void MakeToolOfSelection() => MakeTool();

    public bool MakeTool()
    {
        if (Selection.FirstOrDefault(id => Schematic.Components.Any(c => c.InstanceName == id && DrawnMetal.Is(c))) is not { } shape)
        {
            PCellOperationKind.Fillet => new PCellOperation { Kind = kind, A = ids, Amount = Text(FilletRadius), Unit = LengthUnit },
            PCellOperationKind.Chamfer => new PCellOperation { Kind = kind, A = ids, Amount = Text(ChamferSize), Unit = LengthUnit },
            Status = "Make tool: select an MSHAPE first.";
            return false;
        }
        return Gesture("Make tool", () =>
        {
            var next = Copy(SchematicPersistence.Serialize(Schematic, CellName));
            var comp = next.Components.First(c => c.InstanceName == shape);
            void Set(string name, string word)
            {
                var row = comp.Parameters.FirstOrDefault(p => p.Name == name);
                if (row is null) comp.Parameters.Add(row = new EditableParameter { Name = name });
                row.Expression = $"\"{word}\"";
            }
            Set(DrawnMetal.Role, DrawnMetal.RoleTool);
            Set(DrawnMetal.Model, DrawnMetal.ModelOpen);
            return (next, $"{shape} is a tool: it cuts, merges or bounds in an operation, and is never copper itself.");
    [RelayCommand] private void Fillet() => AddOperation(PCellOperationKind.Fillet);
    [RelayCommand] private void Chamfer() => AddOperation(PCellOperationKind.Chamfer);
        });
    }

    // ══ Reading the plan (D6) ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Every placed pin: its part, its number and its pose, from the generated cells' pins.</summary>
    public IEnumerable<(string Instance, int Pin, PinPose Pose)> Pins() =>
        Layout is null ? [] : PCellDimensions.Pins(Layout, _layoutDir, Technology);

    public PinPose? PinPoseOf(string instance, int pin) =>
        Pins().Where(p => p.Instance == instance && p.Pin == pin).Select(p => (PinPose?)p.Pose).FirstOrDefault();

    /// <summary>The pin nearest (<paramref name="x"/>, <paramref name="y"/>) within <paramref name="tolerance"/> DBU.</summary>
    public (string Instance, int Pin)? PinAt(long x, long y, long tolerance) =>
        Pins().Select(p => (p.Instance, p.Pin, D: Math.Max(Math.Abs(p.Pose.X - x), Math.Abs(p.Pose.Y - y))))
              .Where(p => p.D <= tolerance).OrderBy(p => p.D).Select(p => ((string, int)?)(p.Instance, p.Pin)).FirstOrDefault();

    /// <summary>An <c>MSHAPE</c>'s vertices where the plan put them (DBU), from its <c>X[i]</c>/<c>Y[i]</c> at the values.</summary>
    public IReadOnlyList<(int Vertex, long X, long Y)> VerticesOf(string instance)
    {
        var comp = Schematic.Components.FirstOrDefault(c => c.InstanceName == instance);
        if (comp is null || !DrawnMetal.Is(comp) || Layout?.Instances.FirstOrDefault(i => i.SchematicId == instance) is not { } placed) return [];
        return PCellDimensions.Vertices(comp, placed, Layout.DbuPerMicron, ScopeAtValues());
    }

    // ══ Parameters (R-pcd6-3) ═════════════════════════════════════════════════════════════════════════════════════

    [RelayCommand]
    public void AddParameter()
    {
        int k = 1;
        while (Parameters.Any(p => p.Name == $"P{k}")) k++;
        Parameters.Add(new PCellParameterRow(OnTableChanged) { Name = $"P{k}", Default = "100", Unit = "um", Value = "100" });
    }

    [RelayCommand]
    public void RemoveParameter(PCellParameterRow? row)
    {
        if (row is not null) Parameters.Remove(row);
    }

    private static string SiText(double? si, string unit) =>
        si is not { } v ? "" : (v / (Units.Scale(UnitNormalizer.ToEngineUnit(unit)) ?? 1.0)).ToString("G6", CultureInfo.InvariantCulture);

    private static double? TextSi(string text, string unit, out bool bad)
    {
        bad = false;
        if (string.IsNullOrWhiteSpace(text)) return null;
        var (e, u) = TunableValue.Split(text, unit);
        // Fifteen digits, as TunableValue.Format writes a value: 100 um is 1E-04, not the 9.999999999999999E-05 of 100 × 1e-6.
        if (TunableValue.TryParse(TunableValue.Text(e, u), out _, out _, out double si))
            return double.Parse(TunableValue.Format(si, ""), CultureInfo.InvariantCulture);
        bad = true;
        return null;
    }

    // ══ Save, Run EM, Optimise, Export (R-pcd6-6 to -9) ═══════════════════════════════════════════════════════════

    /// <summary>R-pcd6-9: the <c>.ccell</c>, the <c>.csch</c>, and the cell's <c>.clay</c> as planned at the defaults.
    /// Null when saved, else why not.</summary>
    [RelayCommand]
    private void SaveCell() => Save();

    public string? Save()
    {
        string? why = SaveCore();
        Status = why ?? $"Saved {CellName}: its schematic, its parameters, and its layout at the defaults.";
        return why;
    }

    private string? SaveCore()
    {
        var cell = CellFolder.ReadCcell(CellDir) ?? new CcellFile();
        var old = cell.Parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var parameters = new List<CcellParameter>();
        foreach (var row in Parameters.Where(r => r.Name.Length > 0))
        {
            var p = old.TryGetValue(row.Name, out var known) ? known.Clone() : new CcellParameter { Name = row.Name };
            p.DefaultExpression = row.Default;
            p.Unit = row.Unit;
            p.Dimension = row.Dimension;
            p.Description = row.Description.Length > 0 ? row.Description : null;
            p.Minimum = TextSi(row.Minimum, row.Unit, out bool badMin);
            p.Maximum = TextSi(row.Maximum, row.Unit, out bool badMax);
            if (badMin || badMax) return $"{row.Name}'s range is not a number with a unit; nothing was saved.";
            parameters.Add(p);
        }
        cell.Parameters = parameters;
        // S1: the saved layout is the planner's output at the defaults, never the values column (PCellDesignSave).
        string? why = PCellDesignSave.Save(CellDir, WorkspaceRoot, Schematic, cell, Technology, TechIdentity);
        if (why is null || why.StartsWith("Saved", StringComparison.Ordinal)) Dirty = false;
        return why;
    }

    /// <summary>The latest Run EM: done once shown, cancelled or failed. A test awaits it; the window never does.</summary>
    public Task Work { get; private set; } = Task.CompletedTask;

    private CancellationTokenSource _work = new();

    /// <summary>R-pcd6-6: Save, the cell's <c>.cem</c> made when it has none, then EM at the current values on a worker.</summary>
    [RelayCommand]
    public void RunEm()
    {
        if (Busy) return;
        Work = RunWork("Run EM", async token =>
        {
            if (SaveCore() is { } why) return why;
            string cws = Path.Combine(WorkspaceRoot, ".cws");
            if (EmSetupResolver.FindSetupsForLayout(cws, ClayPath).Count == 0)
                EmSetupFromSchematic.Run(Schematic, cws, ClayPath, CellName);
            if (EmCapability.Resolve(CellDir, null, null, out string? refusal) is not { } target) return refusal ?? "the cell cannot be solved.";
            var values = SiValues();
            var result = await Task.Run(() => EmEvaluation.Evaluate(target, values, control: new RunControl { Token = token }), token);
            if (result.SnpPath is not { } snp) return result.Refusal ?? "no result.";
            Em = TouchstoneIO.ReadFile(snp);
            if (_filterSpec is { } spec) FilterMetrics = PCellFilter.Metrics(spec, Em, "Planar EM");
            BuildPlot();
            return $"{Em.FrequencyCount} points" + (result.Cached ? ", from the EM cache." : ".");
        });
    }

    [RelayCommand]
    public void CancelWork() => _work.Cancel();

    private async Task RunWork(string what, Func<CancellationToken, Task<string>> work)
    {
        _work = new CancellationTokenSource();
        Busy = true;
        Status = $"{what}…";
        try { Status = $"{what}: " + await work(_work.Token); }
        catch (OperationCanceledException) { Status = $"{what} cancelled."; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = $"{what} failed: {ex.Message}"; }
        finally { Busy = false; }
    }

    /// <summary>R-pcd6-7: Save, then the cell's schematic and the Optimizer on it.</summary>
    [RelayCommand]
    public void Optimise()
    {
        if (Save() is { } why) { Status = "Optimise: " + why; return; }
        if (OpenOptimizer is null) { Status = "Optimise needs the workspace window: open the designer from it."; return; }
        OpenOptimizer(SchematicPath);
        Status = $"Optimise: {CellName}'s schematic is open with the Optimizer.";
    }

    /// <summary>R-pcd6-8: Save, then <see cref="PCellPackage.Export"/> (it runs the design check).</summary>
    public string Export(string packagePath)
    {
        if (Save() is { } why) return Status = "Export: " + why;
        PCellPackageOutcome o;
        try { o = PCellPackage.Export(CellDir, packagePath); }
        catch (Exception e) when (e is not OutOfMemoryException) { return Status = "Export: " + e.Message; }
        string findings = string.Join(" ", o.Findings.Select(f => f.Render()));
        return Status = o.Path is { } p
            ? $"Exported {Path.GetFileName(p)}." + (findings.Length > 0 ? " " + findings : "")
            : $"Export refused: {findings}";
    }
}

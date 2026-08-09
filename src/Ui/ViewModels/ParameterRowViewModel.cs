using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// VM for one row in the ParameterEditorView — wraps a single EditableParameter.
/// Expression/Unit/ShowOnSchematic are staged; the row commits through the command stack.
/// Also computes the inline value preview with an honest "=" / "≈" prefix (expressions.md §9.1).
/// When NameEditable is true (extensible component types), StagedName can be committed
/// via CommitName().
/// </summary>
public sealed partial class ParameterRowViewModel : ObservableObject
{
    private readonly EditableParameter  _param;

    /// <summary>
    /// The exact <see cref="EditableParameter"/> this row reads and writes.
    ///
    /// <para>Exposed so the owning editor can tell a row that is still bound to the live model from
    /// one holding an object that has been replaced under it —
    /// <c>SetParametersCommand</c> clones every parameter, so after one runs, a row whose NAME is
    /// unchanged is nonetheless reading a value nothing else can see. See
    /// <c>ParameterEditorViewModel.OnModelChanged</c> for what that looked like.</para>
    /// </summary>
    internal EditableParameter BoundParameter => _param;
    private readonly SchematicViewModel _schematicVm;
    private readonly SymbolKind         _ownerSymbol;
    private readonly EditableComponent? _ownerComp;
    private bool _isRefreshing;

    // Mirrors ComponentModelFactory's private RxCurrentEq/RxCurrentEq1/RxChargeEq1/RxWeightFn.
    // Duplicated here (with this comment) because those fields are private to Core.
    private static readonly Regex RxSddH  = new(@"^H\[(\d+)\]$",      RegexOptions.Compiled);
    private static readonly Regex RxSddI1 = new(@"^I\[(\d+)\]$",      RegexOptions.Compiled);
    private static readonly Regex RxSddI2 = new(@"^I\[(\d+),(\d+)\]$", RegexOptions.Compiled);
    private static readonly Regex RxSddQ  = new(@"^Q\[(\d+)\]$",      RegexOptions.Compiled);

    public string Name         => _param.Name;
    public bool   NameEditable { get; }
    public bool   NameReadOnly => !NameEditable;
    public string NameWatermark { get; }
    public string[] UnitOptions { get; }

    /// <summary>Non-null when this parameter is really a closed set of named modes (e.g. MBend's
    /// "Miter") — the view shows a ComboBox of these labels instead of the plain Expression text
    /// box. See <see cref="ComponentTypeRegistry.EnumParamOptions"/>.</summary>
    public IReadOnlyList<string>? EnumOptions { get; }
    public bool IsEnumParam => EnumOptions is not null;

    /// <summary>
    /// Subtle, read-only readout of the numeric value the selected combo option actually commits
    /// (owner's own request, 2026-07-29): the on-schematic label and the inline text-edit box both
    /// only ever see/accept this raw number (there is no combo on the canvas), so this readout is
    /// what tells the user the combo's items are really just named 0/1/2 flags — and implicitly
    /// explains why the schematic shows "Miter = 2" rather than "Miter = Optimal". Empty string for
    /// every non-enum parameter (the view hides the readout in that case).
    /// </summary>
    public string EnumIndexReadout => IsEnumParam ? SelectedEnumIndex.ToString(CultureInfo.InvariantCulture) : "";

    /// <summary>brief-technology-editor-units-and-layers.md R-tec-6/10: non-null when this parameter is
    /// SignalLayer or GroundReference on a microstrip component. This is the interim mechanism R-tec-10
    /// calls for — no dynamic, technology-sourced choice-parameter mechanism existed before this work;
    /// <see cref="EnumOptions"/> above is the closest precedent, but it is a STATIC, compile-time-fixed
    /// list (MBend's Miter), not one resolved from the schematic's own workspace technology at edit
    /// time. Null for every other parameter.</summary>
    public ComponentTypeRegistry.LayerChoiceKind? LayerChoiceKind { get; }
    public bool IsLayerChoiceParam => LayerChoiceKind is not null;

    /// <summary>Gates the plain Expression text box (column 1) — hidden for a layer-choice parameter,
    /// which shows ONLY the picker <see cref="ComboBox"/> below (owner's explicit follow-up: the
    /// earlier text-field-PLUS-picker design read as "strange" and had a real bug — see
    /// <see cref="LayerChoiceOptions"/>'s own doc comment). A user who needs a value the picker
    /// doesn't offer (a custom or since-renamed layer name) sets it via the schematic canvas's own
    /// inline label text-edit instead — the same escape hatch every other parameter already has.</summary>
    public bool ShowExpressionTextBox => !IsEnumParam && !IsLayerChoiceParam && !IsChoiceParam;

    /// <summary>
    /// True when the value is stated by something the user has already chosen, so the box shows it
    /// but does not take an edit.
    ///
    /// <para>Set for a VerilogA component's <c>Pins</c> once its model file and model are settled:
    /// the number of terminals is the model's own, not an opinion, and typing a different one draws a
    /// symbol with leads the device does not have. It is shown rather than hidden because "how many
    /// terminals does this model have" is exactly what a reader of the dialog wants to know.</para>
    ///
    /// <para>Read-only rather than disabled: a disabled box greys its text out, which reads as "this
    /// does not apply" for a value that very much does.</para>
    /// </summary>
    public bool ExpressionReadOnly { get; private set; }

    internal void SetExpressionReadOnly(bool readOnly)
    {
        if (ExpressionReadOnly == readOnly) return;
        ExpressionReadOnly = readOnly;
        OnPropertyChanged(nameof(ExpressionReadOnly));
    }

    /// <summary>Gates the ordinary Unit combo (column 2) — hidden for a layer-choice parameter (its
    /// <see cref="UnitDimension"/> is always None, a single "None" entry would be clutter, exactly
    /// the same reasoning MBend's Miter enum combo already applies) in favor of the layer picker.
    ///
    /// <para><b>Also hidden for a file-valued parameter, and that one is load-bearing: the Browse…
    /// button lives in the SAME grid column.</b> Both visible means the combo is drawn on top of the
    /// button and the file cannot be picked at all — reported from the running app, because two
    /// controls sharing a cell look fine in the markup. A path has no unit either, so there was
    /// never anything for the combo to offer here.</para>
    ///
    /// <para>Anything else added to column 2 must be excluded here too.</para>
    /// </summary>
    public bool ShowUnitCombo => !IsEnumParam && !IsLayerChoiceParam && !IsChoiceParam && !IsFilePathParam;

    private const string DefaultLayerChoiceLabel = "(Default)";

    /// <summary>The technology-resolved conductor names ONLY — never includes a "ghost" entry for the
    /// currently-staged value. Used solely to decide <see cref="LayerChoiceMissingWarning"/>; the
    /// user-facing option list is <see cref="LayerChoiceOptions"/>, which layers a ghost entry on top
    /// of this when needed.</summary>
    private IReadOnlyList<string> _knownLayerChoiceOptions = [];

    /// <summary>The picker's own option list — "(Default)" first (R-tec-8's empty-means-follow-the-
    /// technology, mirroring L0c's TechRef=null convention), then every conductor layer name for
    /// SignalLayer, or per the owner's explicit instruction ONLY conductors with
    /// <see cref="StackupLayer.IsGroundReference"/> set for GroundReference. Resolved from the
    /// schematic's own ancestor workspace technology
    /// (<see cref="MicrostripSubstrateInjection.ResolveWorkspaceTechnology"/>) on construction AND on
    /// every <see cref="RefreshFromModel"/>, so a stackup edit made while the editor is open is
    /// picked up. <b>Also always includes the CURRENTLY staged value, even when it isn't a real
    /// conductor</b> — a value that arrived some other way (an inline canvas text edit, an older
    /// schematic, a since-renamed/removed layer) must still be the combo's visibly SELECTED item;
    /// without this, Avalonia's ComboBox renders a blank/unselected box whenever `SelectedItem`
    /// doesn't literally match an `ItemsSource` entry — this was the actual bug behind the owner's
    /// "it's a little buggy right now" report, not merely the two-control layout being confusing.</summary>
    public IReadOnlyList<string> LayerChoiceOptions { get; private set; } = [];

    /// <summary>Recomputes both <see cref="_knownLayerChoiceOptions"/> and the ghost-inclusive
    /// <see cref="LayerChoiceOptions"/> from the CURRENT <see cref="StagedExpression"/>.
    /// <b>Only replaces the bound <see cref="LayerChoiceOptions"/> list instance (and raises its
    /// PropertyChanged) when the resulting CONTENT actually differs from what's already there</b> —
    /// this is the fix for the owner's "sometimes doesn't register my new choice, I have to set it
    /// multiple times" report. <c>RefreshFromModel</c> runs on EVERY row after ANY parameter edit on
    /// the component (not just this one), so without this guard, selecting a value in THIS combo
    /// would itself trigger — via <c>Execute → EditModel.Changed → OnModelChanged →
    /// RefreshFromModel</c> — a synchronous, reentrant call back into this SAME method, unconditionally
    /// swapping <see cref="LayerChoiceOptions"/> to a freshly-allocated (content-identical) list
    /// while Avalonia's ComboBox was still in the middle of processing the very selection that
    /// caused it. Swapping `ItemsSource` mid-selection is what made the combo intermittently fail to
    /// keep the new selection visible until a later, unrelated redraw caught it up. Content is
    /// genuinely unchanged the overwhelming majority of the time this runs (most edits are to some
    /// OTHER parameter, or to this one but resolving to the same technology), so this guard also
    /// means the common case does no allocation-driven UI churn at all.</summary>
    private void RecomputeLayerChoiceOptions()
    {
        var known = new List<string> { DefaultLayerChoiceLabel };
        var tech = LayerChoiceKind is not null
            ? MicrostripSubstrateInjection.ResolveWorkspaceTechnology(_schematicVm.EditModel.SchematicDirectory)
            : null;
        if (tech is not null)
        {
            IEnumerable<StackupLayer> conductors = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor);
            if (LayerChoiceKind == ComponentTypeRegistry.LayerChoiceKind.Ground)
                conductors = conductors.Where(l => l.IsGroundReference);
            known.AddRange(conductors.Select(l => l.Name));
        }
        _knownLayerChoiceOptions = known;

        var display = new List<string>(known);
        string current = StagedExpression.Trim();
        if (current.Length > 0 && !display.Contains(current))
            display.Add(current);

        if (!display.SequenceEqual(LayerChoiceOptions))
        {
            LayerChoiceOptions = display;
            OnPropertyChanged(nameof(LayerChoiceOptions));
        }
    }

    /// <summary>Picker binding: maps the staged expression (empty ⇒ "(Default)") to/from a list entry.
    /// Selecting an entry stages AND commits immediately (mirrors the enum combo's own commit-on-select
    /// convention, <see cref="OnSelectedEnumIndexChanged"/>) — this is now the ONLY way to edit a
    /// layer-choice parameter from this dialog.</summary>
    public string SelectedLayerChoice
    {
        get => string.IsNullOrWhiteSpace(StagedExpression) ? DefaultLayerChoiceLabel : StagedExpression;
        set
        {
            if (_isRefreshing) return;
            string newExpr = value == DefaultLayerChoiceLabel ? "" : value;
            if (newExpr == StagedExpression) return;
            StagedExpression = newExpr;
            CommitExpression();
        }
    }

    /// <summary>Informational only (R-tec-9) — the actual FALLBACK behavior already happens correctly
    /// at the <c>SubstrateResolver</c> layer regardless of whether this is ever shown; this just tells
    /// the user their currently-selected value doesn't (or no longer, after a rename/technology swap)
    /// match a conductor in the resolved technology, so the component will silently use the default
    /// instead. Checked against <see cref="_knownLayerChoiceOptions"/> — NOT <see cref="LayerChoiceOptions"/>,
    /// which always contains the current value by construction and would never warn. Empty when the
    /// field is blank, on "(Default)", or matches a genuinely known conductor.</summary>
    public string LayerChoiceMissingWarning
    {
        get
        {
            if (!IsLayerChoiceParam) return "";
            string expr = StagedExpression.Trim();
            if (expr.Length == 0) return "";
            if (_knownLayerChoiceOptions.Contains(expr)) return "";
            return $"\"{expr}\" is not a conductor in the resolved technology — falling back to the default.";
        }
    }
    public bool HasLayerChoiceMissingWarning => LayerChoiceMissingWarning.Length > 0;

    // ── cell-declared choice parameters ───────────────────────────────────────

    /// <summary>
    /// The closed set of values this parameter accepts, declared by the cell it belongs to, or empty
    /// for an ordinary free-text parameter. Read once, from the cell's own <c>.ccell</c>, because a
    /// cell's published interface does not change while a schematic is open — unlike a technology,
    /// which is why <see cref="LayerChoiceOptions"/> re-resolves on every refresh and this does not.
    ///
    /// <para>Always includes the currently staged value even when the cell no longer offers it, for
    /// the same reason the layer picker does: a ComboBox whose <c>SelectedItem</c> is absent from its
    /// <c>ItemsSource</c> renders blank, which reads as "the value was lost".</para>
    /// </summary>
    public IReadOnlyList<string> ChoiceOptions { get; private set; } = [];

    /// <summary>Values the cell declares but circuitRF cannot build. Offered by the picker anyway —
    /// picking one produces a named refusal at Run, which is more useful than the value quietly not
    /// being there.</summary>
    private IReadOnlyList<string> _unsupportedChoices = [];

    public bool IsChoiceParam => ChoiceOptions.Count > 0;

    /// <summary>
    /// Offers a closed set of choices worked out at RUNTIME rather than declared by a cell — today,
    /// the device types a compiled model file turned out to declare.
    ///
    /// <para>Deliberately the same <see cref="ChoiceOptions"/> mechanism a kit part's declared
    /// choices use, so the row renders and commits identically whichever way the set was arrived at.
    /// The currently staged value is always included, for the reason that field already documents:
    /// a ComboBox whose selection is absent from its items renders blank, which reads as the value
    /// having been lost.</para>
    /// </summary>
    internal void SetRuntimeChoices(IReadOnlyList<string> choices)
    {
        if (choices.Count == 0)
        {
            if (ChoiceOptions.Count == 0) return;
            ChoiceOptions = [];
            RaiseChoiceState();
            return;
        }

        var display = new List<string>(choices);
        string current = StagedExpression.Trim();
        if (current.Length > 0 && !display.Contains(current, StringComparer.Ordinal))
            display.Add(current);

        if (ChoiceOptions.SequenceEqual(display, StringComparer.Ordinal)) return;
        ChoiceOptions = display;
        RaiseChoiceState();
    }

    private void RaiseChoiceState()
    {
        OnPropertyChanged(nameof(ChoiceOptions));
        OnPropertyChanged(nameof(IsChoiceParam));
        OnPropertyChanged(nameof(ShowExpressionTextBox));
        OnPropertyChanged(nameof(ShowUnitCombo));
        OnPropertyChanged(nameof(SelectedChoice));
    }

    /// <summary>
    /// True when the cell declares this parameter as naming a FILE — a model library, a data table.
    /// The row then offers a Browse… picker beside the text box: a path is exactly the kind of value
    /// nobody should be asked to type, and a mistyped one fails much later with a worse message.
    /// </summary>
    public bool IsFilePathParam { get; private set; }

    /// <summary>The kit's own one-line description of this parameter, or empty. Shown as the field's
    /// tooltip: it is the sentence the kit's documentation uses, so a user can search for it.</summary>
    public string Description { get; private set; } = "";

    /// <summary>What the name field explains on hover: a problem with the name when there is one,
    /// otherwise the kit's own description of the parameter.</summary>
    public string NameTooltip => NameError.Length > 0 ? NameError : Description;

    /// <summary>Set by the editor when it builds rows; opens the host's own file picker. Null in
    /// contexts with no UI, where the row is simply a text box.
    ///
    /// <para>The setter raises <see cref="ShowBrowseButton"/> because the view supplies this picker
    /// only once its DataContext is set — after rows are built. A plain auto-property leaves the
    /// button's binding evaluated at null forever, which is exactly how it shipped invisible.</para>
    /// </summary>
    public Func<Task<string?>>? PickFileAsync
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            field = value;
            OnPropertyChanged(nameof(ShowBrowseButton));
        }
    }

    public bool ShowBrowseButton => IsFilePathParam && PickFileAsync is not null;

    /// <summary>Picks a file and commits it. A cancelled pick changes nothing.</summary>
    public async Task BrowseForFileAsync()
    {
        if (PickFileAsync is null) return;
        string? picked = await PickFileAsync();
        if (string.IsNullOrWhiteSpace(picked) || picked == StagedExpression) return;
        StagedExpression = picked;
        CommitExpression();
    }

    /// <summary>Picker binding. Selecting an entry stages AND commits immediately, matching the enum
    /// and layer-choice combos — a picker with a separate confirm step is a trap nobody expects.</summary>
    public string SelectedChoice
    {
        get
        {
            string current = StagedExpression.Trim();
            return current.Length > 0 ? current : ChoiceOptions.Count > 0 ? ChoiceOptions[0] : "";
        }
        set
        {
            if (_isRefreshing) return;
            if (value == StagedExpression) return;
            StagedExpression = value;
            CommitExpression();
        }
    }

    /// <summary>Non-empty when the selected value is one circuitRF has no implementation for. Shown
    /// beside the picker so the refusal is visible at the moment of choosing, not only at Run.</summary>
    public string ChoiceUnsupportedWarning =>
        IsChoiceParam && _unsupportedChoices.Contains(StagedExpression.Trim(), StringComparer.Ordinal)
            ? $"\"{StagedExpression.Trim()}\" is not implemented in circuitRF — this component will not simulate."
            : "";

    public bool HasChoiceUnsupportedWarning => ChoiceUnsupportedWarning.Length > 0;

    /// <summary>
    /// Loads this parameter's declared choices from the owning cell, if it is a cell reference that
    /// declares any. Silent on every failure: a missing or unreadable <c>.ccell</c> leaves an
    /// ordinary text-box parameter, which is exactly what a cell without choices should look like.
    /// </summary>
    private void LoadCellDeclaredChoices()
    {
        if (_ownerComp?.CellRef is not { Length: > 0 } cellRef) return;

        // One accessor for both reference forms — a kit part resolves from memory and needs no
        // schematic directory, a cell folder resolves from disk and does.
        string dir = _schematicVm.EditModel.SchematicDirectory ?? "";
        if (dir.Length == 0 && !PdkKitRegistry.IsKitRef(cellRef)) return;

        try
        {
            if (CellSymbolResolver.ResolveCcell(cellRef, dir) is not { } cell) return;

            var declared = cell.Parameters
                               .FirstOrDefault(p => p.Name.Equals(_param.Name, StringComparison.Ordinal));
            if (declared is null) return;

            IsFilePathParam = declared.IsFilePath == true;
            Description     = declared.Description ?? "";

            if (declared.Choices is not { Count: > 0 } choices) return;

            var display = new List<string>(choices);
            string current = _param.Expression.Trim();
            if (current.Length > 0 && !display.Contains(current, StringComparer.Ordinal))
                display.Add(current);

            ChoiceOptions       = display;
            _unsupportedChoices = declared.UnsupportedChoices ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // Leave it an ordinary parameter.
        }
    }

    [ObservableProperty] private string _stagedName       = "";
    [ObservableProperty] private string _stagedExpression = "";
    [ObservableProperty] private string _stagedUnit       = "";
    [ObservableProperty] private bool   _showOnSchematic;
    [ObservableProperty] private string _nameError = "";
    [ObservableProperty] private int    _selectedEnumIndex;

    partial void OnSelectedEnumIndexChanged(int oldValue, int newValue)
    {
        // The readout must update regardless of _isRefreshing (it should track the model even
        // during a Refresh/undo, not only on a live user-driven selection).
        OnPropertyChanged(nameof(EnumIndexReadout));

        // Combo selections commit immediately (mirroring SnP's PinConfig/Pitch combos), not staged
        // via LostFocus like the text fields.
        if (_isRefreshing || EnumOptions is null) return;
        string newExpr = newValue.ToString(CultureInfo.InvariantCulture);
        if (newExpr == _param.Expression) return;
        _schematicVm.Execute(new EditParameterCommand(_schematicVm.EditModel, _param, newExpr, _param.Unit));
    }

    public bool HasNameError => NameError.Length > 0;
    partial void OnNameErrorChanged(string? oldValue, string newValue)
        => OnPropertyChanged(nameof(HasNameError));

    // ── Value preview ("= <evaluated>" / "≈ <rounded>") ───────────────────────
    // Subtle grey, non-interactive (the view makes it selectable-but-read-only). Empty string ⇒
    // the view hides it. Recomputed when the staged expression changes and on RefreshFromModel.

    [ObservableProperty] private string _valuePreview = "";

    public bool HasValuePreview => ValuePreview.Length > 0;
    partial void OnValuePreviewChanged(string? oldValue, string newValue)
        => OnPropertyChanged(nameof(HasValuePreview));

    partial void OnShowOnSchematicChanged(bool oldValue, bool newValue)
    {
        if (_isRefreshing) return;
        _schematicVm.Execute(new SetParameterVisibilityCommand(_schematicVm.EditModel, _param, newValue));
    }

    partial void OnStagedExpressionChanged(string? oldValue, string newValue)
    {
        // Live preview as the user types the expression (cheap; no model mutation).
        // Not gated by _isRefreshing — the preview should also update on refresh/undo.
        RecomputePreview();

        // Keep the picker's own SelectedItem/warning in sync — StagedExpression only ever changes
        // for a layer-choice row via SelectedLayerChoice's own setter (the picker is now the sole
        // editing path) or RefreshFromModel (which recomputes LayerChoiceOptions itself, separately).
        if (IsLayerChoiceParam)
        {
            OnPropertyChanged(nameof(SelectedLayerChoice));
            OnPropertyChanged(nameof(LayerChoiceMissingWarning));
            OnPropertyChanged(nameof(HasLayerChoiceMissingWarning));
        }
    }

    public ParameterRowViewModel(
        EditableParameter  param,
        SchematicViewModel schematicVm,
        SymbolKind         ownerSymbol,
        EditableComponent? ownerComp = null)
    {
        _param       = param;
        _schematicVm = schematicVm;
        _ownerSymbol = ownerSymbol;
        _ownerComp   = ownerComp;
        UnitOptions  = ComponentTypeRegistry.UnitOptions(param.Dimension);
        NameEditable = ComponentTypeRegistry.UserParamTemplate(ownerSymbol) is not null;
        NameWatermark = (ownerSymbol is SymbolKind.Sdd) ? "I[p,w] · Q[p] · H[w]" : "";
        EnumOptions  = ComponentTypeRegistry.EnumParamOptions(ownerSymbol, param.Name);
        LayerChoiceKind = ComponentTypeRegistry.LayerChoiceKindFor(ownerSymbol, param.Name);

        _isRefreshing = true;
        _stagedName       = param.Name;
        _stagedExpression = param.Expression;
        _stagedUnit       = param.Unit;
        _showOnSchematic  = param.ShowOnSchematic;
        _selectedEnumIndex = ParseEnumIndex(param.Expression);
        if (IsLayerChoiceParam) RecomputeLayerChoiceOptions();
        // A built-in primitive has no cell to declare a file-valued parameter on, so the registry
        // states it. Set BEFORE the cell pass, which owns the answer for a kit part and leaves this
        // alone when there is no cell.
        IsFilePathParam = ComponentTypeRegistry.IsFilePathParameter(ownerSymbol, param.Name);
        Description     = ComponentTypeRegistry.ParameterDescription(ownerSymbol, param.Name);
        CanRemove       = ownerComp is not null
                       && ComponentTypeRegistry.IsRemovableParameter(ownerSymbol, param.Name);
        LoadCellDeclaredChoices();
        _isRefreshing = false;

        RecomputePreview();
    }

    /// <summary>Parses an expression as an enum-option index; falls back to 0 (the first option)
    /// when the expression isn't a plain non-negative integer in range — never throws.</summary>
    private int ParseEnumIndex(string expression)
    {
        if (EnumOptions is null) return 0;
        if (int.TryParse(expression.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)
            && idx >= 0 && idx < EnumOptions.Count)
            return idx;
        return 0;
    }

    /// <summary>Commit the staged name to the model (no-op if unchanged or invalid).</summary>
    public void CommitName()
    {
        if (_isRefreshing || !NameEditable) return;
        string name = StagedName.Trim();

        if (name.Length == 0)
        {
            NameError = "Name cannot be empty";
            return;
        }
        if (name == _param.Name) { NameError = ""; return; }

        // Duplicate check against sibling params
        if (_ownerComp is not null &&
            _ownerComp.Parameters.Any(p => !ReferenceEquals(p, _param) && p.Name == name))
        {
            NameError = $"\"{name}\" already exists";
            return;
        }

        // SDD-specific grammar validation — only for SDD owners.
        if (_ownerSymbol is SymbolKind.Sdd)
        {
            if (!TryValidateSddName(name, out string sddError))
            {
                NameError = sddError;
                return;
            }
        }

        NameError = "";
        _schematicVm.Execute(new SetParameterNameCommand(_schematicVm.EditModel, _param, name));
    }

    /// <summary>
    /// Validates an SDD equation parameter name against the accepted grammar.
    /// Returns true (error = "") when the name is valid.
    /// Returns false with a user-facing error message when it is not.
    /// </summary>
    internal static bool TryValidateSddName(string name, out string error)
    {
        // H[w] — check first because it has distinct error messages.
        var mH = RxSddH.Match(name);
        if (mH.Success)
        {
            int w = int.Parse(mH.Groups[1].Value, CultureInfo.InvariantCulture);
            if (w < 2)
            {
                error = "H[0] and H[1] are built-in (1 and jω) — not user-definable";
                return false;
            }
            error = "";
            return true;
        }
        // H[…] with non-integer or empty index.
        if (name.StartsWith("H[", StringComparison.Ordinal))
        {
            error = "H[w] requires an integer weight ≥ 2";
            return false;
        }

        // I[p,w] — two-index form.
        var mI2 = RxSddI2.Match(name);
        if (mI2.Success)
        {
            int p = int.Parse(mI2.Groups[1].Value, CultureInfo.InvariantCulture);
            if (p >= 1) { error = ""; return true; }
            error = "Not a valid SDD equation name (use I[p], I[p,w], Q[p], or H[w])";
            return false;
        }

        // I[p] — single-index current.
        var mI1 = RxSddI1.Match(name);
        if (mI1.Success)
        {
            int p = int.Parse(mI1.Groups[1].Value, CultureInfo.InvariantCulture);
            if (p >= 1) { error = ""; return true; }
            error = "Not a valid SDD equation name (use I[p], I[p,w], Q[p], or H[w])";
            return false;
        }

        // Q[p] — single-index charge.
        var mQ = RxSddQ.Match(name);
        if (mQ.Success)
        {
            int p = int.Parse(mQ.Groups[1].Value, CultureInfo.InvariantCulture);
            if (p >= 1) { error = ""; return true; }
            error = "Not a valid SDD equation name (use I[p], I[p,w], Q[p], or H[w])";
            return false;
        }

        error = "Not a valid SDD equation name (use I[p], I[p,w], Q[p], or H[w])";
        return false;
    }

    /// <summary>Commit the staged expression to the model (no-op if unchanged). An empty expression
    /// is a no-op for every ordinary parameter (clearing a field reverts to the prior value on the
    /// next refresh) — EXCEPT a layer-choice parameter (R-tec-8), where empty is itself a meaningful,
    /// committable value ("follow the technology" / "(Default)").</summary>
    public void CommitExpression()
    {
        string expr = StagedExpression.Trim();
        if (expr == _param.Expression) return;
        if (expr.Length == 0 && !IsLayerChoiceParam) return;
        _schematicVm.Execute(new EditParameterCommand(_schematicVm.EditModel, _param, expr, _param.Unit));
    }

    /// <summary>
    /// True when this row can be removed on its own — see
    /// <see cref="ComponentTypeRegistry.IsRemovableParameter"/> for why this is a per-row "×" and
    /// not the "−" button, which removes the last indexed GROUP and cannot reach the first of a
    /// hundred independent names.
    /// </summary>
    public bool CanRemove { get; }

    /// <summary>
    /// Removes this one parameter from the component, as a single undo entry.
    ///
    /// <para>Removal is by REFERENCE, not by name: the row already holds the exact
    /// <see cref="EditableParameter"/> it is bound to, so a component that somehow carries two rows
    /// of the same name loses the one the user clicked rather than whichever matched first.</para>
    /// </summary>
    public void RemoveSelf()
    {
        if (!CanRemove || _ownerComp is null) return;

        var updated = _ownerComp.Parameters
                                .Where(p => !ReferenceEquals(p, _param))
                                .Select(p => p.Clone())
                                .ToList();
        if (updated.Count == _ownerComp.Parameters.Count) return;   // not ours — change nothing

        _schematicVm.Execute(new SetParametersCommand(_schematicVm.EditModel, _ownerComp, updated));
    }

    /// <summary>Commit a unit selection to the model (no-op if unchanged).</summary>
    public void CommitUnit(string unit)
    {
        if (unit == _param.Unit) return;
        _schematicVm.Execute(new EditParameterCommand(_schematicVm.EditModel, _param, _param.Expression, unit));
    }

    /// <summary>Refresh staged values from the model (called after external edits or undo).</summary>
    public void RefreshFromModel()
    {
        _isRefreshing = true;
        StagedName       = _param.Name;
        StagedExpression = _param.Expression;   // fires OnStagedExpressionChanged → RecomputePreview
        StagedUnit       = _param.Unit;
        ShowOnSchematic  = _param.ShowOnSchematic;
        NameError        = "";
        SelectedEnumIndex = ParseEnumIndex(_param.Expression);
        if (IsLayerChoiceParam)
        {
            // Re-resolve on every refresh (not just at construction) so a stackup edit made while
            // the editor is open — a conductor newly marked IsGroundReference, a renamed layer — is
            // picked up in the picker's own option list (gate 9's "a later stackup edit is picked
            // up again"), not only in what SubstrateResolver itself later resolves at run time.
            // RecomputeLayerChoiceOptions() raises LayerChoiceOptions' own PropertyChanged itself,
            // and ONLY when the content genuinely changed — see its doc comment for why an
            // unconditional raise here would reintroduce the "selection doesn't stick" bug.
            RecomputeLayerChoiceOptions();
            OnPropertyChanged(nameof(SelectedLayerChoice));
            OnPropertyChanged(nameof(LayerChoiceMissingWarning));
            OnPropertyChanged(nameof(HasLayerChoiceMissingWarning));
        }
        if (IsChoiceParam)
        {
            // The option list itself is NOT re-read here: it comes from the cell's published
            // interface, which cannot change while this schematic is open. Only the selection and
            // its warning follow the model.
            OnPropertyChanged(nameof(SelectedChoice));
            OnPropertyChanged(nameof(ChoiceUnsupportedWarning));
            OnPropertyChanged(nameof(HasChoiceUnsupportedWarning));
        }
        _isRefreshing = false;
        RecomputePreview();   // also recompute in case the expression text was unchanged but a
                              // referenced value elsewhere in the schematic changed
    }

    // ── Preview computation ────────────────────────────────────────────────────

    /// <summary>
    /// Recomputes the value preview from the current staged expression, evaluated against the
    /// schematic's current state, with the honest "=" / "≈" prefix of expressions.md §9.1. Shows a
    /// preview ONLY when:
    ///   • the owner is not an SDD device (its equations aren't scalar-evaluable here);
    ///   • the expression is more than a bare number/blank (no "= 2.5" noise on a literal);
    ///   • evaluation succeeds and yields a single Real (or Complex) value.
    /// Any parse/resolve/cycle/type error, or a non-scalar result (e.g. a Cube/sweep), yields an
    /// empty preview (no error surfaced). All failure is swallowed — a preview never throws.
    /// </summary>
    private void RecomputePreview()
    {
        ValuePreview = ComputePreview(StagedExpression);
    }

    private string ComputePreview(string expression)
    {
        // Gate 1: SDD → never evaluate (device-equation params, not scalar).
        if (_ownerSymbol is SymbolKind.Sdd) return "";

        // Gate 2: blank or bare-number → no preview (a literal needs no "≈").
        string expr = expression.Trim();
        if (expr.Length == 0) return "";
        if (IsBareNumber(expr)) return "";

        try
        {
            var scope = DesignScope.Build(_schematicVm.EditModel, selfName: _param.Name);
            // No unit passed: preview shows the RAW evaluated value (display-unit scaling deferred;
            // and the engine's Units table is ASCII-keyed, mismatching the glyph ComboBox strings).
            var value = new Evaluator().Eval(expr, scope);

            // Gate 3: only scalar Real / Complex preview (Cube/Bool/String/All ⇒ no preview).
            // Honest "=" / "≈" prefix per expressions.md §9.1 — shared with the analysis-dialog hint:
            // "=" when the shown digits reconstruct the value, "≈" only when genuinely rounded.
            return AnalysisPreviewHelper.FormatValueHonest(value);
        }
        catch
        {
            // Unresolved name, parse error, cycle, type error, domain error, division by zero, …
            // → simply no preview. The preview is advisory and must never raise to the user.
            return "";
        }
    }

    /// <summary>True if the trimmed text is just a numeric literal (so a preview would be noise).</summary>
    private static bool IsBareNumber(string s)
        => double.TryParse(s, NumberStyles.Float | NumberStyles.AllowLeadingSign,
                           CultureInfo.InvariantCulture, out _);
}

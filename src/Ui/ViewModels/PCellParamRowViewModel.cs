using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Ui.Layout.PCells;

namespace CircuitRF.Ui.ViewModels;

/// <summary>Which control edits a PCell parameter. Decided from what the GENERATOR declared, never
/// from what the value happens to look like — see <see cref="PCellParameterInfo"/>.</summary>
public enum PCellParamEditor
{
    /// <summary>Free text (or a number with no stated bounds). What every parameter used to get.</summary>
    Text,
    /// <summary>A tick box, for a Bool parameter or a two-valued yes/no enumeration.</summary>
    Check,
    /// <summary>A dropdown, for an enumeration of three or more — or of two that are not a yes/no pair.</summary>
    Choice,
    /// <summary>Selectable, non-editable text: the generator DERIVES this one and never reads it.</summary>
    Computed,
}

/// <summary>One row of the L5-followups §5 Properties Inspector parameter list for a selected PCell
/// instance (docs/sonnet-briefs/brief-L5-followups.md §5/R-L5f-8) — mirrors <see cref="VertexRowViewModel"/>'s
/// own shape exactly (thin, bindable, all reading/writing delegated to the owner, which alone knows
/// the resolved cell and the layout's display unit).
///
/// <para><b>The row carries the generator's declaration, not just a string.</b> Every parameter used
/// to render as the same text box, which made a model name, a yes/no flag and a derived capacitance
/// indistinguishable — and the derived one actively misleading, because typing into it does nothing
/// and the number it shows stops matching the geometry as soon as the geometry changes. A vendor kit
/// states which is which in its own declaration; <see cref="Info"/> is that statement, carried
/// through, and <see cref="Editor"/> is the one decision made from it.</para></summary>
public sealed partial class PCellParamRowViewModel : ObservableObject
{
    private readonly LayoutShapePropertiesViewModel _owner;

    /// <summary>The PCell parameter name (e.g. "W", "Z1", "GammaMax") — also the "#"-column label.</summary>
    public string Name { get; }

    /// <summary>The parameter's declared unit (from <c>ComponentTypeRegistry.DefaultParameters</c>) —
    /// "mm" for a length (displayed/parsed in the LAYOUT's own display unit, R-L5f-8), "deg" for an
    /// angle, "Ω" for a resistance, or "" for dimensionless. Display-only; not itself editable.</summary>
    public string Unit { get; }

    /// <summary>What the generator declared about this parameter, or null when nothing did — a
    /// built-in, or a script predating wire version 7. Null renders exactly as before.</summary>
    public PCellParameterInfo? Info { get; }

    /// <summary>Which control this row edits with.</summary>
    public PCellParamEditor Editor { get; }

    /// <summary>What to show beside the field: the generator's own label when it gave one, else the
    /// name. The NAME is still what the row is keyed and committed by.</summary>
    public string Label => Info is { } i ? i.DisplayLabel : Name;

    /// <summary>Shown on hover — the name whenever a label is displayed in its place (so the
    /// identifier a netlist or a script would use is never hidden), plus any stated bounds.</summary>
    public string? Tip { get; }

    /// <summary>The values a <see cref="PCellParamEditor.Choice"/> row offers, as display text. Grows
    /// by at most one entry, when the instance holds a value the generator does not list.</summary>
    public IReadOnlyList<string> Choices
    {
        get => _choices;
        private set => SetProperty(ref _choices, value);
    }
    private IReadOnlyList<string> _choices = [];

    public bool IsText     => Editor == PCellParamEditor.Text;
    public bool IsCheck    => Editor == PCellParamEditor.Check;
    public bool IsChoice   => Editor == PCellParamEditor.Choice;
    public bool IsComputed => Editor == PCellParamEditor.Computed;

    /// <summary>What a derived parameter shows when nothing reported its current value — an em dash,
    /// not the number the design was stored with. See <c>LayoutShapePropertiesViewModel.
    /// PopulatePCellParamRow</c> for why a stale one is worse here than none.</summary>
    public const string NoDerivedValue = "\u2014";

    /// <summary>Stable key for the focus-tracking guard (mirrors <c>VertexRowViewModel.FieldKeyX</c>) —
    /// computed once, not re-derived per refresh.</summary>
    public string FieldKey { get; }

    [ObservableProperty] private string _valueText = "";
    [ObservableProperty] private string? _error;
    public bool HasError => Error is not null;

    /// <summary>Set while the row is being refreshed FROM the model, so the checkbox's and
    /// dropdown's own change notifications — which fire on a programmatic write exactly as they do
    /// on a click — do not bounce straight back as a user edit. A text box does not need this
    /// because it commits on LostFocus/Enter, not on every keystroke.</summary>
    private bool _refreshing;

    /// <summary>The checked state of a <see cref="PCellParamEditor.Check"/> row. Writing it commits,
    /// because a tick box has no "finished editing" moment of its own to wait for.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            SetProperty(ref _isChecked, value);
            if (!_refreshing) _owner.CommitPCellParamFlag(this, value);
        }
    }
    private bool _isChecked;

    /// <summary>The selected item of a <see cref="PCellParamEditor.Choice"/> row. Commits on write,
    /// for the same reason as <see cref="IsChecked"/>.
    ///
    /// <para>A value the generator does not list still shows here, appended to the offered items
    /// rather than silently snapped to the nearest one — an out-of-list value comes from an older
    /// design or a hand-edited file, and rewriting it would change artwork nobody asked to change.
    /// See <see cref="PCellParameterInfo.Choices"/>.</para></summary>
    public string? SelectedChoice
    {
        get => _selectedChoice;
        set
        {
            if (_selectedChoice == value) return;
            SetProperty(ref _selectedChoice, value);
            if (!_refreshing && value is not null) _owner.CommitPCellParamField(this, value);
        }
    }
    private string? _selectedChoice;

    internal PCellParamRowViewModel(LayoutShapePropertiesViewModel owner, string name, string unit,
                                    PCellParameterInfo? info = null, bool computed = false,
                                    bool unread = false)
    {
        _owner   = owner;
        Name     = name;
        Unit     = unit;
        Info     = info;
        FieldKey = $"PCellParam:{name}";

        // A parameter the RUN reported as derived and one the DECLARATION marks derived are the same
        // fact from two sources — see PCellWireGenerateReply.Computed for why both exist — and either
        // is enough. Checked before every other editor: a derived value is not editable by any
        // control, so no other choice about it means anything.
        if (computed || info is { Computed: true })
            Editor = PCellParamEditor.Computed;
        else if (info is { } i && (i.Kind == PCellValueKind.Bool || i.IsYesNoPair))
            Editor = PCellParamEditor.Check;
        else if (info is { Choices.Count: > 0 })
            Editor = PCellParamEditor.Choice;
        else
            Editor = PCellParamEditor.Text;

        _choices = Editor == PCellParamEditor.Choice
            ? [.. info!.Choices!.Select(FormatChoice)]
            : [];

        Tip = BuildTip(info, Editor, name, unread);
        RefreshFromInstance();
    }

    /// <summary>A choice as the text its dropdown shows — the same text the value itself formats to,
    /// so a listed choice and the current value compare equal instead of both being right and looking
    /// different.</summary>
    private static string FormatChoice(PCellValue choice) => choice.Kind switch
    {
        PCellValueKind.String => choice.AsText(),
        PCellValueKind.Bool   => choice.AsBool() ? "true" : "false",
        PCellValueKind.Int    => choice.AsInt().ToString(System.Globalization.CultureInfo.InvariantCulture),
        _                     => choice.AsReal().ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string? BuildTip(PCellParameterInfo? info, PCellParamEditor editor, string name,
                                    bool unread)
    {
        var parts = new List<string>();
        if (info is { } i && !string.Equals(i.DisplayLabel, name, StringComparison.Ordinal))
            parts.Add(name);
        if (editor == PCellParamEditor.Computed)
            parts.Add("Derived by the generator from the other parameters — not an input. " +
                      "An em dash means this generator does not report what it came out to.");
        // Said, not enforced. A parameter can be neither an input to the artwork nor an output of it
        // — a process constant a kit copies out of its own technology data, or a netlist parameter
        // that never touched the geometry — and there is no signal that separates the two, so the
        // box stays. What can be stated is the measurement, which is what this is.
        else if (unread)
            parts.Add("The generator did not read this on the run that drew the current artwork, " +
                      "so changing it will not change the geometry.");
        if (info is { Minimum: { } lo, Maximum: { } hi }) parts.Add($"{lo} to {hi}");
        else if (info is { Minimum: { } only }) parts.Add($"at least {only}");
        else if (info is { Maximum: { } cap }) parts.Add($"at most {cap}");
        return parts.Count > 0 ? string.Join(" — ", parts) : null;
    }

    /// <summary>Re-reads this parameter's current value from the owner's selected instance's resolved
    /// cell and pushes it into <see cref="ValueText"/> — unless this field currently has focus, in
    /// which case it is left alone (same focus guard <c>VertexRowViewModel.RefreshFromShape</c> uses).
    /// Called by the owner for every already-realized row on refresh — never rebuilds the row itself.</summary>
    internal void RefreshFromInstance()
    {
        _refreshing = true;
        try { _owner.PopulatePCellParamRow(this); }
        finally { _refreshing = false; }
    }

    /// <summary>Pushes a freshly-read value into whichever control this row renders as. Called by the
    /// owner at the end of its own populate, so there is ONE place that knows how a value maps onto
    /// each editor.</summary>
    internal void ShowValue(string text, PCellValue? raw)
    {
        ValueText = text;

        if (Editor == PCellParamEditor.Check)
        {
            bool? truth = raw is { } v ? PCellParameterInfo.TruthOf(v) : null;
            SetProperty(ref _isChecked, truth ?? false, nameof(IsChecked));
        }
        else if (Editor == PCellParamEditor.Choice)
        {
            // An out-of-list value is OFFERED rather than corrected, so the dropdown can show what
            // the design actually holds. Without this the box renders empty and the first click
            // silently replaces a value the user never chose to change.
            if (!Choices.Contains(text, StringComparer.Ordinal))
                Choices = [.. Choices, text];
            SetProperty(ref _selectedChoice, text, nameof(SelectedChoice));
        }
    }

    /// <summary>R-L5f-9: commits (LostFocus/Enter) — copy-on-write, via
    /// <see cref="LayoutEditorViewModel.EditInstancePCellParameters"/>.</summary>
    public void Commit(string text) => _owner.CommitPCellParamField(this, text);
}

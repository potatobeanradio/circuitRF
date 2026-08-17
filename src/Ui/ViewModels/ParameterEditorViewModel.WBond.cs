using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.WBond;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// The wBond panel of the Parameter Editor — the counterpart of the SnP panel, and for the same
/// reason: a wBond's interesting parameters are not text, and three of them were showing as text
/// nobody could read or act on (owner, 2026-08-16).
///
/// <h3>What was wrong, and what each row is</h3>
/// <list type="bullet">
/// <item><b><c>Design</c> was "gibberish".</b> Correctly so — it is the whole wirebond design, base64
///   of its JSON (§5.0/WB17b), and it is documented as a HIDDEN parameter. It was never meant to be a
///   row. It is replaced here by the one-line summary the symbol body already carries: how many
///   arrays, how many wires, how much wire.</item>
/// <item><b><c>Arrays</c> is bookkeeping, not a value.</b> It records the array list this instance's
///   wiring was drawn against, so a re-import that REORDERS the arrays can be reported instead of
///   silently re-pointing every wire (§9.2/WB35a — pin order IS array order, so a reorder moves every
///   pin while its name moves to a different row). Also documented hidden. It is now maintained
///   automatically by the array editor below, which is the only thing that should ever write it.</item>
/// <item><b><c>SymbolPitch</c> and <c>RefPin</c></b> get the controls SnP's equivalents have.</item>
/// </list>
///
/// <h3>The array editor is the point</h3>
/// <para>"There's no way to add new arrays" — there is now, and it is the schematic-side half of the
/// workflow the owner wants: declare the arrays (and their names, which ARE the pin names) on the
/// symbol, then draw the wires into them somewhere with geometry.</para>
///
/// <para><b>A new array arrives carrying one default wire</b>, exactly as a freshly-dropped wBond does
/// (<see cref="WBondEmbedding.DefaultDesign"/>). It is not an empty array, and that is deliberate:
/// an empty one is refused by <c>WBondDesign.Validate</c> because it makes the mapping matrix
/// rank-deficient and the array-basis inductance singular — so a schematic that could declare one
/// would place a component that cannot be simulated until someone visits another editor. One wire
/// means the component renders, wires up and solves the moment it is added, and the wire is a thing to
/// MOVE rather than a thing to create.</para>
/// </summary>
public partial class ParameterEditorViewModel
{
    /// <summary>True when the selected component is a wBond — gates the whole panel.</summary>
    public bool IsWBond => _target?.Symbol == SymbolKind.WBond;

    /// <summary>
    /// The parameters this panel owns, and which must therefore NOT also appear as generic text rows.
    /// <c>Design</c> and <c>Arrays</c> are hidden by design (§5.0); the other two have real controls.
    /// </summary>
    internal static bool IsWBondPanelParameter(string name) =>
        name is WBondEmbedding.DesignParameter or WBondPlacement.ArraysParameter
             or "SymbolPitch" or "RefPin";

    // ── The three simple controls ─────────────────────────────────────────────

    /// <summary>
    /// Whether this panel offers its own <b>Update Layout</b> button (owner, 2026-08-17).
    ///
    /// <para>False when there is no workspace to update INTO — a button that can only report "no
    /// workspace is open" is worse than no button, and the schematic-wide Design-menu command is refused
    /// on exactly the same grounds.</para>
    /// </summary>
    public bool CanUpdateWBondLayout => IsWBond && _schematicVm?.UpdateWBondLayout is not null;

    /// <summary>
    /// Raised after <see cref="UpdateWBondLayoutCommand"/> has run, so a HOST dialog can close itself —
    /// the owner asked for the button to close the dialog and leave the layout focused, and only the view
    /// knows whether it is in a dialog at all (this panel is also the docked Properties inspector, which
    /// must not vanish).
    /// </summary>
    public event Action? WBondLayoutUpdated;

    /// <summary>
    /// <b>Update Layout</b> — writes this ONE wBond's wires into the cell's layout and focuses it, then
    /// closes the dialog.
    ///
    /// <para>Deliberately narrower than Design ▸ Update Layout from Schematic: only this component is
    /// written, so nothing else in the layout is re-resolved, re-placed or re-reported around the user
    /// while they are editing wires (owner, 2026-08-17).</para>
    /// </summary>
    [RelayCommand]
    private void UpdateWBondLayout()
    {
        if (_target is null || _schematicVm?.UpdateWBondLayout is not { } run) return;

        run(_schematicVm, _target);
        WBondLayoutUpdated?.Invoke();
    }

    /// <summary>Tight / Loose — SnP's own two values, meaning the same two things.</summary>
    public static string[] WBondSymbolPitchOptions { get; } =
        [nameof(WBondSymbolPitch.Tight), nameof(WBondSymbolPitch.Loose)];

    [ObservableProperty] private int _wBondSymbolPitchIndex = 1;   // Loose
    [ObservableProperty] private bool _wBondRefPin;
    [ObservableProperty] private string _wBondSummary = "";

    /// <summary>Why the design cannot be read, or empty when it can. Shown instead of the summary.</summary>
    [ObservableProperty] private string _wBondPayloadError = "";

    partial void OnWBondSymbolPitchIndexChanged(int oldValue, int newValue)
    {
        if (_isRefreshing || _target is null || _schematicVm is null) return;

        string value = (uint)newValue < (uint)WBondSymbolPitchOptions.Length
            ? WBondSymbolPitchOptions[newValue]
            : nameof(WBondSymbolPitch.Loose);

        ApplyWBondParam("SymbolPitch", value);
    }

    partial void OnWBondRefPinChanged(bool oldValue, bool newValue)
    {
        if (_isRefreshing || _target is null || _schematicVm is null) return;
        ApplyWBondParam("RefPin", newValue ? "true" : "false");
    }

    // ── The array editor ──────────────────────────────────────────────────────

    /// <summary>One row per wire array: its name — which IS a pin-pair name — and how many wires it holds.</summary>
    public ObservableCollection<WBondArrayEditRow> WBondArrays { get; } = [];

    /// <summary>True while there is more than one array, so the last one cannot be removed.</summary>
    public bool CanRemoveWBondArray => WBondArrays.Count > 1;

    /// <summary>
    /// Adds an array, named for the first free <c>G&lt;n&gt;</c>, carrying one default wire.
    ///
    /// <para>The new wire is offset from the ones already there by its own span, so a design built up
    /// here arrives as a row of separate wires rather than N copies stacked in one place — which
    /// would be singular the moment it was solved, two wires of zero separation having infinite
    /// mutual coupling.</para>
    /// </summary>
    [RelayCommand]
    private void AddWBondArray()
    {
        if (!TryReadWBondDesign(out var design) || design is null) return;

        var profile = design.Profiles.FirstOrDefault()
                   ?? LoopProfile.BallBond(WBondUnits.ToNm(WBondEmbedding.DefaultWire.LoopHeightMils, WBondUnit.Mil));
        if (design.Profiles.Count == 0) design.Profiles.Add(profile);

        long pitch = WBondUnits.ToNm(WBondEmbedding.DefaultWire.SpanMils, WBondUnit.Mil);
        long offset = pitch * design.Arrays.Count;

        var start = WBondEmbedding.DefaultWire.Start;
        var end = WBondEmbedding.DefaultWire.End;

        design.Arrays.Add(new WireArray
        {
            Name = NextFreeArrayName(design),
            Profile = profile.Name,
            Wires =
            {
                profile.CreateWire(
                    new Point3(start.X + offset, start.Y, start.Z),
                    new Point3(end.X + offset, end.Y, end.Z),
                    WBondUnits.ToNm(WBondEmbedding.DefaultWire.DiameterMils, WBondUnit.Mil),
                    WireMaterials.Default.Name),
            },
        });

        CommitWBondDesign(design);
    }

    /// <summary>
    /// Removes an array — <b>and every wire in it</b>, which is the part that needs saying.
    ///
    /// <para>The last one is never removable: a wBond with no arrays has no pins, and
    /// <c>WBondModel</c> refuses it by name rather than producing a zero-port component. Better to
    /// disable the button than to explain that afterwards.</para>
    /// </summary>
    [RelayCommand]
    private void RemoveWBondArray(WBondArrayEditRow? row)
    {
        if (row is null) return;
        if (!TryReadWBondDesign(out var design) || design is null) return;
        if (design.Arrays.Count <= 1) return;
        if ((uint)row.Index >= (uint)design.Arrays.Count) return;

        design.Arrays.RemoveAt(row.Index);
        CommitWBondDesign(design);
    }

    /// <summary>
    /// Renames an array, from its row. Refused — silently, by snapping the row back — when the name is
    /// blank or already taken: array names are the symbol's pin names and must be unique
    /// (<c>WBondDesign.Validate</c>), and a duplicate would make the payload unreadable by the
    /// component that carries it.
    /// </summary>
    internal void RenameWBondArray(WBondArrayEditRow row, string name)
    {
        if (_isRefreshing || _target is null || _schematicVm is null) return;
        if (!TryReadWBondDesign(out var design) || design is null) return;
        if ((uint)row.Index >= (uint)design.Arrays.Count) return;

        string trimmed = name.Trim();
        bool taken = design.Arrays
            .Where((_, i) => i != row.Index)
            .Any(a => a.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase));

        if (trimmed.Length == 0 || taken)
        {
            RefreshWBondProperties();   // snap the box back to the name that is actually there
            return;
        }

        if (design.Arrays[row.Index].Name.Equals(trimmed, StringComparison.Ordinal)) return;

        design.Arrays[row.Index].Name = trimmed;
        CommitWBondDesign(design);
    }

    private static string NextFreeArrayName(WBondDesign design)
    {
        var used = new HashSet<string>(design.Arrays.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        for (int n = 1; ; n++)
        {
            string candidate = "G" + n;
            if (used.Add(candidate)) return candidate;
        }
    }

    // ── Reading and writing the payload ───────────────────────────────────────

    private bool TryReadWBondDesign(out WBondDesign? design)
    {
        design = null;
        if (_target is null) return false;

        string? payload = _target.Parameters
            .FirstOrDefault(p => p.Name == WBondEmbedding.DesignParameter)?.Expression;

        return WBondEmbedding.TryDecode(payload, out design) && design is not null;
    }

    /// <summary>
    /// Writes the design back onto the component, <b>and the <c>Arrays</c> record with it</b>.
    ///
    /// <para>Both in ONE command, so undo takes them back together. They are two statements of the
    /// same fact — what this instance's wiring is drawn against — and a design edited without its
    /// record updated would report drift against itself on the next import.</para>
    /// </summary>
    private void CommitWBondDesign(WBondDesign design)
    {
        if (_target is null || _schematicVm is null) return;

        var updated = _target.Parameters.Select(p => p.Clone()).ToList();
        Set(WBondEmbedding.DesignParameter, WBondEmbedding.Encode(design));
        Set(WBondPlacement.ArraysParameter, WBondSymbolProvider.ArraysKeyOf(design));

        _schematicVm.Execute(new SetParametersCommand(_schematicVm.EditModel, _target, updated));

        void Set(string name, string value)
        {
            var param = updated.FirstOrDefault(p => p.Name == name);
            if (param is not null) param.Expression = value;
            else updated.Add(new EditableParameter { Name = name, Expression = value });
        }
    }

    private void ApplyWBondParam(string name, string value)
    {
        var updated = _target!.Parameters.Select(p => p.Clone()).ToList();
        var param = updated.FirstOrDefault(p => p.Name == name);

        if (param is not null) param.Expression = value;
        else updated.Add(new EditableParameter { Name = name, Expression = value });

        _schematicVm!.Execute(new SetParametersCommand(_schematicVm.EditModel, _target!, updated));
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Pulls the panel's state from the component. Mirrors <c>RefreshSnpProperties</c>, including its
    /// <c>_isRefreshing</c> guard so the property callbacks do not write back what they just read.
    /// </summary>
    private void RefreshWBondProperties()
    {
        if (_target?.Symbol != SymbolKind.WBond) return;

        string pitch = _target.Parameters.FirstOrDefault(p => p.Name == "SymbolPitch")?.Expression
                    ?? nameof(WBondSymbolPitch.Loose);
        int pitchIndex = Array.IndexOf(WBondSymbolPitchOptions, pitch);
        if (pitchIndex < 0) pitchIndex = Array.IndexOf(WBondSymbolPitchOptions, nameof(WBondSymbolPitch.Loose));

        bool refPin = (_target.Parameters.FirstOrDefault(p => p.Name == "RefPin")?.Expression ?? "false")
            .Equals("true", StringComparison.OrdinalIgnoreCase);

        bool readable = TryReadWBondDesign(out var design) && design is not null;

        OnPropertyChanged(nameof(CanUpdateWBondLayout));

        _isRefreshing = true;
        WBondSymbolPitchIndex = pitchIndex;
        WBondRefPin = refPin;

        // An unreadable payload is a reported, repairable state — the same stance TryDecode itself
        // takes — not an empty panel that reads as "this component has nothing in it".
        WBondPayloadError = readable
            ? ""
            : "This wBond's embedded design could not be read. Use File ▸ Import ▸ Wirebond Wires… to replace it.";
        // The workspace technology's own unit, not a hard-coded one (owner, 2026-08-17).
        WBondSummary = readable
            ? WBondSymbolGenerator.Describe(design!, _schematicVm?.LengthDisplayUnit ?? LayoutUnit.Mil)
            : "";

        RebuildWBondArrayRows(design);
        _isRefreshing = false;
    }

    /// <summary>
    /// Rebuilds the array rows — <b>only when the list has actually changed</b>.
    ///
    /// <para>Every commit re-enters here through the selection refresh, and rebuilding the rows
    /// unconditionally would replace the very text box the user is typing a rename into, mid-word.
    /// Comparing the name sequence first is what keeps a rename a rename.</para>
    /// </summary>
    private void RebuildWBondArrayRows(WBondDesign? design)
    {
        var names = design?.Arrays.Select(a => a.Name).ToList() ?? [];

        if (names.Count == WBondArrays.Count
            && names.Select((n, i) => WBondArrays[i].Name == n).All(same => same))
        {
            for (int i = 0; i < names.Count; i++)
                WBondArrays[i].WireCount = design!.Arrays[i].Wires.Count;

            OnPropertyChanged(nameof(CanRemoveWBondArray));
            return;
        }

        WBondArrays.Clear();
        for (int i = 0; i < names.Count; i++)
            WBondArrays.Add(new WBondArrayEditRow(this, i, names[i], design!.Arrays[i].Wires.Count));

        OnPropertyChanged(nameof(CanRemoveWBondArray));
    }
}

/// <summary>
/// One row of the wBond array editor: an array's name, and how many wires are in it.
///
/// <para>The name is committed on <see cref="Commit"/> rather than on every keystroke — a half-typed
/// name is routinely blank or a duplicate, and both are refused, so committing per character would
/// fight the user for the box.</para>
/// </summary>
public sealed partial class WBondArrayEditRow : ObservableObject
{
    private readonly ParameterEditorViewModel _owner;

    public WBondArrayEditRow(ParameterEditorViewModel owner, int index, string name, int wireCount)
    {
        _owner = owner;
        Index = index;
        _name = name;
        _wireCount = wireCount;
    }

    /// <summary>Position in the array list — which is also this pin pair's position on the symbol.</summary>
    public int Index { get; }

    [ObservableProperty] private string _name;

    [ObservableProperty] private int _wireCount;

    // The "G1.i / G1.o" column that used to sit here is GONE (owner, 2026-08-17: "the user does not
    // care or even needs to know what G1.i or G1.o is"). The pin NAMES are an internal spelling of
    // "this array's + and − terminals"; what the row is for is the array's own name and its wire count,
    // and the terminals are visible on the symbol itself where they are actually connected to.

    /// <summary>How many wires, phrased for a row that is mostly read at a glance.</summary>
    public string WiresText => WireCount == 1 ? "1 wire" : $"{WireCount} wires";

    partial void OnWireCountChanged(int value) => OnPropertyChanged(nameof(WiresText));

    /// <summary>Commits the typed name. Called from the view on Enter or lost focus.</summary>
    public void Commit() => _owner.RenameWBondArray(this, Name);
}

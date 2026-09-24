using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

// ──────────────────────────────────────────────────────────────────────────────
//  The substrate an MLIN is actually simulated on — SHOWN.
//
//  Owner-reported user feedback, 2026-09-06: "I can't see which tech is used by the
//  mlin component."
//
//  They could not, anywhere. A microstrip component's H / T / Er / Sigma / TanD are
//  injected at extraction time by MicrostripSubstrateInjection and are DELIBERATELY
//  not declared cell parameters — the class's own header says so ("they never appear
//  in ComponentTypeRegistry.DefaultParameters or the parameter editor; they exist
//  solely as these injected overrides"). That was the right call for the parameter
//  LIST, which is the user's own W/L/Angle set. It left the values that decide the
//  electrical answer with no surface at all: the schematic showed a width and a
//  length, and everything else about the line came from a file the schematic never
//  names.
//
//  So this is a READOUT, not a new set of editable fields. It states the technology
//  and the five numbers it produced, computed through the SAME call the extractor
//  makes — never a re-derivation, which would be a second answer that could disagree
//  with the one that gets simulated.
//
//  WHERE THE TECHNOLOGY COMES FROM, and why that is worth printing: a schematic
//  resolves its substrate from the WORKSPACE DEFAULT and from nowhere else. There is
//  no per-schematic technology reference (MicrostripSubstrateInjection §R-pc-8: "add
//  a per-schematic override only if a need appears"). A LAYOUT, by contrast, resolves
//  its own TechRef first. The two can therefore differ, which is what
//  WorkspaceViewModel.SchematicToLayout's own divergence warning is about — and the
//  first step in making that legible is for the schematic side to say what it is
//  using.
// ──────────────────────────────────────────────────────────────────────────────

public partial class ParameterEditorViewModel
{
    /// <summary>True for the six microstrip kinds — the ones whose substrate is injected rather than
    /// entered. Drives the readout's visibility; <see cref="MicrostripSubstrateInjection.IsMicrostripKind"/>
    /// stays the one definition of which kinds those are.</summary>
    public bool IsMicrostripTarget =>
        _target is not null && MicrostripSubstrateInjection.IsMicrostripKind(_target.Symbol);

    /// <summary>
    /// One technology the workspace holds, as the picker offers it.
    ///
    /// <para>A record, so the ComboBox's <c>SelectedItem</c> matches by VALUE — a rebuilt option list
    /// would otherwise clear the selection silently, which is exactly the shipped bug
    /// <c>ParameterRowViewModel.RecomputeLayerChoiceOptions</c> documents for the layer combos.</para>
    /// </summary>
    /// <param name="Display">The technology's own <c>Name</c>, and nothing else (owner, 2026-09-06:
    /// the option text had too many characters). Two files CAN answer to one name — a copied process
    /// keeps it — which is why <paramref name="RelativePath"/> rides along and is shown as the
    /// tooltip on the row and on the selection, rather than being crammed into the label.</param>
    /// <param name="Path">The absolute <c>.ctech</c>.</param>
    /// <param name="RelativePath">The same file as the workspace spells it — never an absolute path,
    /// which prints a location the user did not choose to show.</param>
    public sealed record MicrostripTechnologyOption(string Display, string Path, string RelativePath)
    {
        public override string ToString() => Display;
    }

    /// <summary>Every <c>.ctech</c> in this schematic's workspace. Empty when there is no workspace,
    /// or none in it — the picker is then disabled rather than absent, so the reason is visible.</summary>
    public IReadOnlyList<MicrostripTechnologyOption> MicrostripTechnologyOptions { get; private set; } = [];

    private MicrostripTechnologyOption? _selectedMicrostripTechnology;
    private bool _suppressTechnologySelection;

    /// <summary>
    /// The workspace default, as a selection.
    ///
    /// <para><b>Setting it re-points the WORKSPACE, and nothing narrower is available to mean.</b> A
    /// schematic has no technology reference of its own, so the honest choices were a read-only label
    /// or a control that changes the one thing that actually decides the answer. The owner asked for
    /// the control; it routes through <c>WorkspaceViewModel.ChangeWorkspaceDefaultTechnologyAsync</c>,
    /// which asks the same "N layouts follow this default" question the Project Tree's own item asks,
    /// and the selection SNAPS BACK when that confirmation is declined.</para>
    /// </summary>
    public MicrostripTechnologyOption? SelectedMicrostripTechnology
    {
        get => _selectedMicrostripTechnology;
        set
        {
            if (Equals(_selectedMicrostripTechnology, value)) return;
            var previous = _selectedMicrostripTechnology;
            _selectedMicrostripTechnology = value;
            OnPropertyChanged();

            // The refresh path assigns this too. Without the guard, re-reading the workspace default
            // would be indistinguishable from a user picking it, and would re-run the confirmation.
            OnPropertyChanged(nameof(MicrostripTechnologyTooltip));
            OnPropertyChanged(nameof(CanEditMicrostripTechnology));
            EditMicrostripTechnologyCommand.NotifyCanExecuteChanged();

            if (_suppressTechnologySelection || value is null) return;
            _ = ApplyTechnologySelectionAsync(value, previous);
        }
    }

    /// <summary>Set by the view (as the file pickers are), because only it can reach the workspace
    /// this schematic belongs to. Null in a host that has no workspace — the picker is disabled.</summary>
    public Func<string, Task<bool>>? ChangeWorkspaceTechnologyAsync { get; set; }

    /// <summary>Opens the selected <c>.ctech</c> as a document — the same thing double-clicking it in
    /// the Project Tree does. Set by the view, for the same reason as the setter above.</summary>
    public Action<string>? OpenTechnologyFile { get; set; }

    /// <summary>
    /// The tooltip on the picker: the selected technology's file, as the workspace spells it.
    ///
    /// <para>Dynamic (owner, 2026-09-06) because the label is now the bare name, and the file is the
    /// only thing that tells two same-named technologies apart. It is also the answer to the question
    /// the whole panel exists for — "which tech is this" — so it must never be a fixed string.</para>
    /// </summary>
    public string MicrostripTechnologyTooltip =>
        SelectedMicrostripTechnology is { } sel
            ? $"{sel.RelativePath}\nThe workspace default — what every microstrip component in this "
            + "workspace takes its substrate from. Changing it re-points every layout that follows the "
            + "default too, and says so first."
            : "This schematic's workspace has no default technology set.";

    /// <summary>Edit… is only meaningful once there is a file to open.</summary>
    public bool CanEditMicrostripTechnology =>
        SelectedMicrostripTechnology is not null && OpenTechnologyFile is not null;

    /// <summary>Opens the selected technology for editing — the substrate values shown here belong to
    /// it, and this is the one place they can actually be changed.</summary>
    public IRelayCommand EditMicrostripTechnologyCommand { get; }

    /// <summary>Drives the picker's enabled state: there has to be something to pick and somewhere to
    /// record it.</summary>
    public bool CanChangeMicrostripTechnology =>
        MicrostripTechnologyOptions.Count > 0 && ChangeWorkspaceTechnologyAsync is not null;

    private async Task ApplyTechnologySelectionAsync(
        MicrostripTechnologyOption chosen, MicrostripTechnologyOption? previous)
    {
        if (ChangeWorkspaceTechnologyAsync is null) return;

        bool applied = await ChangeWorkspaceTechnologyAsync(chosen.Path);
        if (!applied)
        {
            // Declined, or nothing changed. A combo left showing a technology the workspace is not on
            // would be a wrong readout produced by the act of reading it.
            _suppressTechnologySelection = true;
            _selectedMicrostripTechnology = previous;
            OnPropertyChanged(nameof(SelectedMicrostripTechnology));
            _suppressTechnologySelection = false;
            return;
        }

        RefreshMicrostripSubstrate();
    }

    /// <summary>The technology's own name, or the reason there is none — the picker's placeholder,
    /// for the state where nothing is selected because nothing resolved.</summary>
    public string MicrostripTechnologyText { get; private set; } = "";

    /// <summary>
    /// True when a substrate actually resolved. False means the component is falling back to
    /// <see cref="ComponentModelFactory"/>'s own hardcoded defaults, which is the state
    /// <see cref="MicrostripSubstrateWarning"/> explains.
    /// </summary>
    public bool MicrostripSubstrateResolved { get; private set; }

    /// <summary>
    /// Why no substrate resolved — <see cref="MicrostripSubstrateInjection.BuildOverrides"/>'s own
    /// reason, which names exactly what is missing (no technology, no ground reference layer, …).
    /// Empty when one did.
    ///
    /// <para>The only line left beside the picker. The VALUES it produces were shown here too and are
    /// not any more (owner, 2026-09-06): they belong to the technology, and Edit… is one click away.
    /// A FAILURE is different — it says the simulated line is not the one the workspace describes,
    /// and nothing else in the application says so until a run.</para>
    /// </summary>
    public string MicrostripSubstrateWarning { get; private set; } = "";

    /// <summary>
    /// Re-resolves the readout. Called wherever the target changes and on every model refresh, so a
    /// stackup edited while the editor is open is picked up — the same reason
    /// <c>ParameterRowViewModel.RecomputeLayerChoiceOptions</c> re-resolves rather than caching.
    /// </summary>
    private void RefreshMicrostripSubstrate()
    {
        OnPropertyChanged(nameof(IsMicrostripTarget));
        if (!IsMicrostripTarget)
        {
            MicrostripTechnologyText    = "";
            MicrostripSubstrateWarning  = "";
            MicrostripSubstrateResolved = false;
            NotifyMicrostripSubstrate();
            RefreshMlinImpedance();
            return;
        }

        string? schematicDir = _schematicVm?.EditModel.SchematicDirectory;
        Technology? tech = MicrostripSubstrateInjection.ResolveWorkspaceTechnology(schematicDir);
        RefreshTechnologyOptions(schematicDir);

        // The instance's OWN layer choices participate: SignalLayer / GroundReference pick which
        // conductors of the stackup the substrate is measured between, so a readout that ignored them
        // would describe a different line from the one that is simulated.
        var overrides = MicrostripSubstrateInjection.BuildOverrides(
            tech, out string? warning,
            NonDefaultLayerChoice("SignalLayer"), NonDefaultLayerChoice("GroundReference"));

        MicrostripTechnologyText    = tech?.Name is { Length: > 0 } n ? n : "No technology";
        MicrostripSubstrateResolved = overrides.Count > 0;
        MicrostripSubstrateWarning  = overrides.Count > 0
            ? ""
            : (warning ?? "No substrate resolved.")
            + " These components fall back to the model's own defaults.";

        NotifyMicrostripSubstrate();
        RefreshMlinImpedance();
    }

    /// <summary>
    /// Rebuilds the picker's options and re-reads the current selection from the <c>.cws</c>.
    ///
    /// <para><b>The list instance is replaced only when its CONTENT differs</b>, and the selection is
    /// assigned AFTER it. Both halves are load-bearing and both are lessons already paid for here:
    /// <c>ParameterRowViewModel</c> records that swapping <c>ItemsSource</c> while a ComboBox is
    /// processing a selection makes the choice intermittently fail to stick, and <c>src/Ui/CLAUDE.md</c>
    /// records that Avalonia silently clears a <c>SelectedItem</c> its items do not yet contain.</para>
    /// </summary>
    private void RefreshTechnologyOptions(string? schematicDir)
    {
        var cwsPath = WorkspaceRootFinder.FindAncestorCws(schematicDir);
        var root    = cwsPath is null ? null : System.IO.Path.GetDirectoryName(cwsPath);

        var options = root is null
            ? []
            : DocumentRemovalImpact.TechnologiesIn(root)
                .Select(p => new MicrostripTechnologyOption(NameOf(p), p, RelativeTo(root, p)))
                .ToList();

        if (!options.Select(o => o.Path).SequenceEqual(MicrostripTechnologyOptions.Select(o => o.Path),
                                                       StringComparer.OrdinalIgnoreCase))
        {
            MicrostripTechnologyOptions = options;
            OnPropertyChanged(nameof(MicrostripTechnologyOptions));
        }

        string? current = MicrostripSubstrateInjection.ResolveWorkspaceTechnologyPath(schematicDir);
        var selected = current is null
            ? null
            : MicrostripTechnologyOptions.FirstOrDefault(o =>
                  string.Equals(System.IO.Path.GetFullPath(o.Path), System.IO.Path.GetFullPath(current),
                                StringComparison.OrdinalIgnoreCase));

        _suppressTechnologySelection = true;
        _selectedMicrostripTechnology = selected;
        OnPropertyChanged(nameof(SelectedMicrostripTechnology));
        _suppressTechnologySelection = false;

        OnPropertyChanged(nameof(CanChangeMicrostripTechnology));
    }

    /// <summary>The technology's own <c>Name</c>, falling back to the file's stem when it declares
    /// none — never a blank row.</summary>
    private static string NameOf(string techPath)
    {
        string? name = null;
        try { name = TechPersistence.LoadFromFile(techPath).Name; } catch { }
        return string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileNameWithoutExtension(techPath) : name;
    }

    /// <summary>The file as the workspace spells it. Never absolute — that prints a location the user
    /// did not choose to show, and says nothing extra about which technology this is.</summary>
    private static string RelativeTo(string root, string techPath)
    {
        try   { return System.IO.Path.GetRelativePath(root, techPath).Replace('\\', '/'); }
        catch { return System.IO.Path.GetFileName(techPath); }
    }

    private void NotifyMicrostripSubstrate()
    {
        OnPropertyChanged(nameof(MicrostripTechnologyText));
        OnPropertyChanged(nameof(MicrostripTechnologyTooltip));
        OnPropertyChanged(nameof(MicrostripSubstrateResolved));
        OnPropertyChanged(nameof(MicrostripSubstrateWarning));
        OnPropertyChanged(nameof(CanEditMicrostripTechnology));
        EditMicrostripTechnologyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The instance's own layer choice for <paramref name="name"/>, or null for the
    /// "(Default)" empty value that means "follow the technology" (L0c's <c>TechRef = null</c>
    /// convention, mirrored per parameter).</summary>
    private string? NonDefaultLayerChoice(string name)
    {
        var value = _target?.Parameters
            .FirstOrDefault(p => p.Name.Equals(name, StringComparison.Ordinal))?.Expression?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }



}

// Design-rule checking, from the editor's side (docs/design/layout-view.md §9A).
//
// R16b — "DRC never blocks editing" — is what shapes this file. Checking runs on demand, produces a
// result the panel and the renderer read, and touches nothing else: no command is pushed, no shape is
// altered, no dialog interrupts. A stale result is cleared rather than left to mislead, and that is
// the only way a check ever affects the editor.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Ui.Layout.Assembly;
using CircuitRF.Ui.Layout.Drc;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Layout;

public sealed partial class LayoutEditorViewModel
{
    /// <summary>
    /// The last check's result, or null when none has run (or the geometry has changed since).
    /// Cleared on every model change: a marker drawn over geometry that has moved is worse than no
    /// marker, and a violation count that no longer matches the artwork is worse than no count.
    /// </summary>
    [ObservableProperty] private DrcRunResult? _drcResult;

    /// <summary>Draw violation markers over the artwork. A view preference — never persisted, never
    /// on the undo stack, mirroring <see cref="ShowPCellPins"/> exactly.</summary>
    [ObservableProperty] private bool _showDrcMarkers = true;

    /// <summary>One row per violation, waived ones included and marked (§9A.1).</summary>
    public ObservableCollection<DrcViolationRow> DrcViolations { get; } = [];

    /// <summary>
    /// The wBond design whose wires ride over this layout, when there is one — installed by the wBond
    /// editor's document, never persisted in the `.clay`.
    ///
    /// <para>Session state, like <see cref="ShowDrcMarkers"/>: which wires are over a layout is a
    /// property of what is open, not of the artwork. A layout with no wires checks exactly as it did
    /// before WB-D, by construction — the wire half of the run is reached only when this is non-null.</para>
    /// </summary>
    [ObservableProperty] private WBondDesign? _wireDesign;

    /// <summary>The resolved `.wasm` assembly rules, or null. Null is not an error (§M1).</summary>
    [ObservableProperty] private WasmResolution? _assemblyRules;

    /// <summary>
    /// Which assembly rule set the last check ran against, named rather than assumed — the same
    /// argument as <see cref="DrcTechnologyText"/>, applied to the second rule file.
    ///
    /// <para><b>"No assembly rules" is no longer one of the answers</b>, because it is not true: a
    /// design with no `.wasm` is checked against <see cref="WBondBuiltInRules"/>, and a panel that
    /// said nothing ran would be describing a check that did. What it says instead is which of the
    /// two sets ran and, for the built-in one, how little it covers.</para>
    /// </summary>
    public string DrcAssemblyText => WireDesign is null
        ? ""
        : AssemblyRules?.Rules is not null
            ? AssemblyRules.Describe()
            : WBondBuiltInRules.Describe(WBondWireClearance.Nm);

    partial void OnWireDesignChanged(WBondDesign? value)
    {
        OnPropertyChanged(nameof(DrcAssemblyText));
        OnPropertyChanged(nameof(HasWireDesign));
    }

    partial void OnAssemblyRulesChanged(WasmResolution? value) =>
        OnPropertyChanged(nameof(DrcAssemblyText));

    /// <summary>True when this layout has wires riding over it — drives the panel's wire column.</summary>
    public bool HasWireDesign => WireDesign is not null;

    [ObservableProperty] private DrcViolationRow? _selectedDrcViolation;

    /// <summary>
    /// Raised when a violation should be brought on screen. The VM cannot pan or zoom — the canvas
    /// owns the viewport (see <c>LayoutCanvas</c>) — so this is the same view-layer seam every other
    /// zoom command in this editor already goes through.
    /// </summary>
    public event Action<Bbox>? ZoomToRegionRequested;

    public string DrcSummaryText => DrcResult is not { } r
        ? "Not checked."
        : r.IsClean
            ? $"No violations. {r.RulesEvaluated} rule(s) over {r.ShapesChecked:N0} shape(s)" +
              (r.WaivedCount > 0 ? $", {r.WaivedCount} waived." : ".")
            : $"{r.ErrorCount} error(s), {r.WarningCount} warning(s)" +
              (r.WaivedCount > 0 ? $", {r.WaivedCount} waived" : "") +
              $" — {r.RulesEvaluated} rule(s) over {r.ShapesChecked:N0} shape(s).";

    /// <summary>
    /// Which technology the last check ran against, named rather than assumed.
    ///
    /// <para><b>This is the one thing a DRC surface must never leave implicit.</b> A layout with no
    /// <c>TechRef</c> of its own resolves the WORKSPACE DEFAULT (see <c>TechnologyResolver</c>), and a
    /// workspace holding two processes has one default. A clean result checked against the wrong
    /// process's rules is indistinguishable from a clean result checked against the right one — unless
    /// the surface says which.</para>
    /// </summary>
    public string DrcTechnologyText => DrcResult?.TechnologyName is { Length: > 0 } n
        ? $"Checked against \"{n}\"."
        : DrcResult is null ? "" : "No technology resolved.";

    partial void OnDrcResultChanged(DrcRunResult? value)
    {
        DrcViolations.Clear();
        if (value is not null)
            foreach (var v in value.Violations)
                DrcViolations.Add(new DrcViolationRow(v, this));

        SelectedDrcViolation = null;
        OnPropertyChanged(nameof(DrcSummaryText));
        OnPropertyChanged(nameof(DrcTechnologyText));
        OnPropertyChanged(nameof(HasDrcResult));
        RebuildOverlay();
    }

    public bool HasDrcResult => DrcResult is not null;

    partial void OnShowDrcMarkersChanged(bool value) => RebuildOverlay();

    partial void OnSelectedDrcViolationChanged(DrcViolationRow? value) => RebuildOverlay();

    /// <summary>
    /// Runs every rule the resolved technology states, over this design's elaborated flat geometry.
    ///
    /// <para>Flat, per §9A.1's own hierarchy answer, using the SAME whole-design flatten Gerber export
    /// already drives (<see cref="LayoutDesignFlatten"/>) rather than a second one. A sub-cell drawn
    /// against a different technology needs a layer mapping confirmed before its geometry can be
    /// placed on this design's layers; a CHECK must not be the thing that asks for that decision, so
    /// such a sub-cell is left out and named in the diagnostics instead.</para>
    /// </summary>
    public DrcRunResult RunDrc()
    {
        var diagnostics = new List<string>();

        var flat = LayoutDesignFlatten.Flatten(
            Model, CurrentCellDir ?? "", Technology, ResolveTechAt, resolvedCrossTechMappings: null);

        if (flat.ExceedsCeiling)
        {
            var refused = DrcRunResult.Empty(Technology?.Name,
                [$"This design flattens to more than {LayoutDesignFlatten.HardCeiling:N0} shapes — " +
                 "the check was refused rather than run. Nothing was changed."]);
            DrcResult = refused;
            return refused;
        }

        foreach (var u in flat.UnresolvedInstances) diagnostics.Add(u);

        foreach (var pending in flat.PendingCrossTechMappings)
            diagnostics.Add($"\"{System.IO.Path.GetFileName(pending.Key)}\" is drawn against a different " +
                            "technology and its layers have not been mapped onto this one, so it was not " +
                            "checked. Flatten it (or place it once) to resolve the mapping first.");

        // The assembly half rides in the SAME run, so a wire violation lands in the same panel, sorts
        // into the same list and waives through the same store as a die-side one. Null when there are
        // no wires, which is what keeps a plain layout's result byte-identical to before WB-D.
        WBondCheckContext? wires = WireDesign is { } design
            ? new WBondCheckContext(
                design,
                AssemblyRules?.Rules,
                Technology,
                Model.DbuPerMicron,
                RegionOf: null,                       // supplied by DrcEngine from its own evaluator
                LayoutExtent: ExtentOf(flat.Shapes))
            : null;

        // The built-in rule set's clearance is a USER preference (WBondWireClearance), so it is read
        // here rather than defaulted inside the engine — the engine's own default is circuitRF's
        // half a mil, which is the right answer for a caller that has no user to ask.
        var settings = wires is null
            ? null
            : DrcRunSettings.Default with { WireClearanceNm = WBondWireClearance.Nm };

        var result = DrcEngine.Run(flat.Shapes, Technology, Model.DrcWaivers, settings, wires);

        DrcResult = result with { Diagnostics = [.. diagnostics, .. result.Diagnostics] };
        return DrcResult;
    }

    /// <summary>The artwork's overall extent — what <c>dist_to_edge</c> measures against.</summary>
    private static Bbox ExtentOf(IReadOnlyList<LayoutShape> shapes)
    {
        var box = Bbox.Empty;
        foreach (var s in shapes) box = box.Union(LayoutGeometry.BboxOf(s));
        return box;
    }

    /// <summary>Waives (or un-waives) one violation. Marks the document dirty so the decision is
    /// saved; deliberately NOT undoable — see <see cref="LayoutView.DrcWaivers"/>.</summary>
    public void SetWaived(DrcViolationRow row, bool waived, string reason = "")
    {
        int existing = Model.DrcWaivers.FindIndex(w => string.Equals(w.Key, row.Violation.Key, StringComparison.Ordinal));

        if (waived)
        {
            if (existing >= 0) Model.DrcWaivers[existing].Reason = reason;
            else Model.DrcWaivers.Add(new DrcWaiver
            {
                Key      = row.Violation.Key,
                Reason   = reason,
                RuleName = row.Violation.RuleName,
            });
        }
        else if (existing >= 0)
        {
            Model.DrcWaivers.RemoveAt(existing);
        }
        else return;   // nothing to do; never dirty the document for a no-op

        IsDirty = true;

        // Re-apply against the SAME result rather than re-running: waiving is a statement about a
        // violation that has already been found, and re-checking would be both slow and (on a design
        // edited since) a different answer to a question the user did not ask.
        if (DrcResult is { } r)
        {
            var byKey = Model.DrcWaivers.ToDictionary(w => w.Key, w => w, StringComparer.Ordinal);
            DrcResult = r with
            {
                Violations = [.. r.Violations.Select(v => byKey.TryGetValue(v.Key, out var w)
                    ? v with { Waived = true, WaiverReason = w.Reason }
                    : v with { Waived = false, WaiverReason = null })],
            };
        }
    }

    [RelayCommand]
    private void ZoomToSelectedViolation()
    {
        if (SelectedDrcViolation is { } row) ZoomToRegionRequested?.Invoke(row.Violation.Marker);
    }

    private IReadOnlyList<DrcMarker> BuildDrcMarkers()
    {
        if (!ShowDrcMarkers || DrcResult is not { Violations.Count: > 0 } r) return [];

        var selectedKey = SelectedDrcViolation?.Violation.Key;
        var markers = new List<DrcMarker>(r.Violations.Count);
        foreach (var v in r.Violations)
            markers.Add(new DrcMarker(
                v.MarkerRings, v.Severity, v.Waived,
                selectedKey is not null && string.Equals(v.Key, selectedKey, StringComparison.Ordinal)));
        return markers;
    }

    /// <summary>Clears a stale result — called from the model's own change notification.</summary>
    internal void ClearDrcResultOnEdit()
    {
        if (DrcResult is not null) DrcResult = null;
    }
}

/// <summary>One row of the violations panel.</summary>
public sealed partial class DrcViolationRow : ObservableObject
{
    private readonly LayoutEditorViewModel _owner;

    public DrcViolationRow(DrcViolation violation, LayoutEditorViewModel owner)
    {
        Violation = violation;
        _owner    = owner;
        _reason   = violation.WaiverReason ?? "";
    }

    public DrcViolation Violation { get; }

    public bool IsWaived => Violation.Waived;
    public bool IsError  => !Violation.Waived && Violation.Severity == DrcSeverity.Error;

    /// <summary>A waived row stays listed (§9A.1 — waivers must be visible) but reads as settled.</summary>
    public double RowOpacity => Violation.Waived ? 0.55 : 1.0;

    [ObservableProperty] private string _reason;

    public string RuleText => Violation.RuleName;

    /// <summary>True for a violation about a bond wire rather than about artwork.</summary>
    public bool IsWireViolation => Violation.WireGroups.Count > 0;

    /// <summary>
    /// Which `.wasm` section the rule came from — "Machine", "Process" or "Material" (WB32). Empty
    /// for a die-side rule and for the structural wire-geometry checks, which belong to no house.
    /// </summary>
    public string SectionText => Violation.Section?.ToString() ?? "";

    public bool HasSection => Violation.Section is not null;

    /// <summary>
    /// The note that stops a wire marker reading as wrong.
    ///
    /// <para><b>A 3D clearance drawn as a 2D marker will otherwise be misread, and predictably so.</b>
    /// Two wires that look far apart in plan can be a diameter apart in space, and two that cross in
    /// plan can clear each other by twenty mil — so a user looking at the marker and at the artwork
    /// will see a violation "in the wrong place" unless the panel says the marker is a projection.</para>
    /// </summary>
    public string ProjectionNote => IsWireViolation
        ? "Marker is a projection into the layout plane — the clearance is measured in 3D."
        : "";

    public string DetailText
    {
        get
        {
            // A wire violation carries its own measured-vs-limit text, in the unit the rule was
            // written in (mil) — see DrcWireCheck.FormatMil.
            if (Violation.MeasuredText is { Length: > 0 } measured)
            {
                string groups = Violation.WireGroups.Count > 0
                    ? $"  ({string.Join(" ↔ ", Violation.WireGroups)})"
                    : "";
                return $"{measured}{groups}";
            }

            long   dbu    = Violation.RequiredDbu;
            var    model  = _owner.Model;
            string value  = $"{LayoutUnits.Format(dbu, model.DisplayUnit, model.DbuPerMicron)} " +
                            $"{LayoutUnits.Suffix(model.DisplayUnit)}";
            string kind   = Violation.Kind == DrcRuleKind.MinWidth ? "narrower than" : "closer than";
            string nets   = Violation.Kind == DrcRuleKind.MinSpacing
                ? $"  ({Violation.NetA ?? "unnamed"} ↔ {Violation.NetB ?? "unnamed"})"
                : "";
            return $"{kind} {value}{nets}";
        }
    }

    public string LocationText
    {
        get
        {
            var model = _owner.Model;
            string x = LayoutUnits.Format(Violation.Marker.MinX, model.DisplayUnit, model.DbuPerMicron);
            string y = LayoutUnits.Format(Violation.Marker.MinY, model.DisplayUnit, model.DbuPerMicron);
            return $"{x}, {y} {LayoutUnits.Suffix(model.DisplayUnit)}";
        }
    }

    [RelayCommand]
    private void ToggleWaive() => _owner.SetWaived(this, !IsWaived, Reason);
}

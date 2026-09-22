// Cross-probing — brief-lvs-12-gui.md §3, docs/design/lvs.md §8.3.
//
// "Cross-probing is the feature that makes LVS usable rather than merely correct, and the
// correspondence brief 7 returns is what makes it possible: once LVS has matched R7 to R7,
// selecting one selects the other."
//
// ── WHAT THIS FILE IS ALLOWED TO KNOW ────────────────────────────────────────────────────────
//
// The correspondence, and how to turn a device PATH into a selection in each of the two editors.
// That is all. It computes no pairing of its own (R-lvs12-5a) — `LvsComparison.Devices` is the
// answer and `LvsPairedBy` is how much it is worth, both decided below the firewall.
//
// ── THE THREE ANSWERS, AND WHY NONE OF THEM IS SILENCE ───────────────────────────────────────
//
// R-lvs12-3b: where the last run did not match a part, THE PANEL SAYS WHY. A probe that quietly
// highlighted nothing is indistinguishable from a probe that found the counterpart off screen,
// and a user who meets that twice stops trusting the feature. So every probe returns a sentence,
// and the three it can return are: matched (and whether the pairing was arbitrary), unmatched
// (and on which side), or stale (R-lvs12-3d).

using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Diagnostics;

namespace CircuitRF.Ui.Layout;

/// <summary>What one cross-probe concluded — <b>always a sentence</b> (R-lvs12-3b).</summary>
/// <param name="Matched">True when a counterpart was found and selected.</param>
/// <param name="Arbitrary">
/// R-lvs12-3c: the pairing was <c>lvs.match.by-symmetry</c> — one of <i>n</i> interchangeable parts,
/// broken by a tie-break. <b>Announced HERE and not only in the report</b>: a user clicking C7 in
/// the schematic and being shown a capacitor the drawing calls C9 will believe the tool is wrong
/// unless it says the pairing was arbitrary.
/// </param>
/// <param name="Message">What to put in front of the user.</param>
public readonly record struct LvsProbeAnswer(bool Matched, bool Arbitrary, string Message)
{
    /// <summary>Nothing was asked.</summary>
    public static readonly LvsProbeAnswer None = new(false, false, "");
}

public sealed partial class LayoutEditorViewModel
{
    /// <summary>The last probe's sentence, for the panel's own strip.</summary>
    [ObservableProperty] private string _lvsProbeText = "";

    // ── From a finding (R-lvs12-3a) ──────────────────────────────────────────────────────────

    /// <summary>
    /// Selects a finding's objects in <b>both</b> open views — the layout instance and the schematic
    /// component.
    /// </summary>
    /// <remarks>
    /// The objects are the designer's own un-reduced names (R-lvs8-2b), which is exactly what both
    /// editors index their own parts by, so no correspondence lookup is needed: a finding that names
    /// both sides already names both sides. Where an object exists on only one of them — a schematic
    /// device the artwork does not have — that view selects nothing, which is the finding.
    /// </remarks>
    public void SelectFindingObjects(LvsFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (finding.IsRunLevel) { LvsProbeText = ""; return; }

        int inLayout    = SelectLayoutDevices(finding.Objects);
        int inSchematic = SelectSchematicDevices(finding.Objects);

        LvsProbeText = (inLayout, inSchematic) switch
        {
            (0, 0) => "This line is about the run rather than about a part.",
            (_, 0) when LvsSchematic is null => $"{inLayout} part(s) selected in the layout. "
                                              + "Open the schematic to see the other side.",
            (0, _) => $"{inSchematic} part(s) selected in the schematic. The artwork has no counterpart.",
            _      => $"{inLayout} part(s) in the layout, {inSchematic} in the schematic.",
        };
    }

    // ── From either view (R-lvs12-3b) ────────────────────────────────────────────────────────

    /// <summary>
    /// The counterpart of whatever is selected in the LAYOUT, highlighted in the schematic.
    /// </summary>
    public LvsProbeAnswer ProbeFromLayout()
    {
        if (Stale() is { } stale) return Announce(stale);

        var paths = SelectedInstancePaths();
        if (paths.Count == 0) return Announce(new LvsProbeAnswer(false, false,
            "Select a placed part in the layout to find it in the schematic."));

        return Announce(Probe(paths, fromSchematic: false));
    }

    /// <summary>The counterpart of whatever is selected in the SCHEMATIC, highlighted here.</summary>
    public LvsProbeAnswer ProbeFromSchematic()
    {
        if (Stale() is { } stale) return Announce(stale);
        if (LvsSchematic is not { } schematic) return Announce(new LvsProbeAnswer(false, false,
            "The schematic for this cell is not open."));

        var names = schematic.EditModel.Components
            .Where(c => schematic.Selection.IsSelected(c.Id))
            .Select(c => c.InstanceName)
            .Where(n => n is { Length: > 0 })
            .ToList();

        if (names.Count == 0) return Announce(new LvsProbeAnswer(false, false,
            "Select a component in the schematic to find it in the layout."));

        return Announce(Probe(names, fromSchematic: true));
    }

    [RelayCommand] private void ProbeLayoutToSchematic() => ProbeFromLayout();
    [RelayCommand] private void ProbeSchematicToLayout() => ProbeFromSchematic();

    // ── The correspondence, read and never recomputed ────────────────────────────────────────

    private LvsProbeAnswer Probe(IReadOnlyList<string> names, bool fromSchematic)
    {
        var result = LvsResult!;
        var comparison = result.Comparison;

        var hits = new List<(LvsPair Pair, IReadOnlyList<string> Counterparts)>();
        foreach (var pair in comparison.Devices.Where(p => p.Kind == LvsObjectKind.Device))
        {
            // The pairs are over the REDUCED netlists, so a merged four-finger device is one pair
            // standing for four parts. Both sides are un-reduced through `Group` (R-lvs6-5b) —
            // otherwise clicking one finger of a merged FET would find nothing.
            var mine   = Group(fromSchematic ? result.Schematic : result.Layout,
                               fromSchematic ? pair.Schematic : pair.Layout);
            var theirs = Group(fromSchematic ? result.Layout : result.Schematic,
                               fromSchematic ? pair.Layout : pair.Schematic);

            if (mine.Any(m => names.Any(n => Same(m, n)))) hits.Add((pair, theirs));
        }

        if (hits.Count == 0) return Unmatched(names, fromSchematic);

        var counterparts = hits.SelectMany(h => h.Counterparts).Distinct(StringComparer.Ordinal).ToList();
        int selected = fromSchematic ? SelectLayoutDevices(counterparts)
                                     : SelectSchematicDevices(counterparts);

        // R-lvs12-3c. Stated at the probe, in the same breath as the answer.
        bool arbitrary = hits.Any(h => h.Pair.By == LvsPairedBy.Symmetry);
        string where = fromSchematic ? "layout" : "schematic";

        if (selected == 0)
            return new LvsProbeAnswer(false, arbitrary,
                $"Matched to {Join(counterparts)}, but the {where} is not open.");

        string sentence = $"{Join(counterparts)} in the {where}.";
        return new LvsProbeAnswer(true, arbitrary, arbitrary
            ? sentence + " This part is one of several the comparison could not tell apart, so the "
                       + "pairing is ARBITRARY — it may mean any other in its group."
            : sentence);
    }

    /// <summary>
    /// R-lvs12-3b's other half: the run did not match this part, and the panel says so rather than
    /// highlighting nothing.
    /// </summary>
    private LvsProbeAnswer Unmatched(IReadOnlyList<string> names, bool fromSchematic)
    {
        var result = LvsResult!;
        var side = fromSchematic ? result.Schematic : result.Layout;
        var unmatched = fromSchematic
            ? result.Comparison.UnmatchedSchematicDevices
            : result.Comparison.UnmatchedLayoutDevices;

        bool known = unmatched.Any(i => Group(side, i).Any(m => names.Any(n => Same(m, n))));

        return new LvsProbeAnswer(false, false, known
            ? $"{Join(names)} was not matched: the comparison found no counterpart for it in the "
            + $"{(fromSchematic ? "layout" : "schematic")}."
            : $"{Join(names)} is not in the comparison — it was read as neither a device nor a "
            + "connection, so there is nothing to cross-probe to.");
    }

    private LvsProbeAnswer? Stale()
    {
        if (LvsResult is null)
            return new LvsProbeAnswer(false, false, "Run LVS to cross-probe.");

        // R-lvs12-3d: the correspondence is a snapshot of the last run. It does not re-run, and it
        // does not silently highlight against a result that no longer describes the design.
        if (IsLvsStale)
            return new LvsProbeAnswer(false, false, LvsStaleText);

        return null;
    }

    private LvsProbeAnswer Announce(LvsProbeAnswer answer)
    {
        LvsProbeText = answer.Message;
        return answer;
    }

    private static IReadOnlyList<string> Group(LvsNetlist netlist, int device)
        => device < 0 || device >= netlist.Devices.Count ? [] : netlist.Devices[device].Group;

    private static string Join(IEnumerable<string> names) => string.Join(", ", names);

    // ── Names into selections ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A device path and an editor's own name for the same part, compared.
    /// </summary>
    /// <remarks>
    /// <b>The path carries two decorations the editors do not.</b> An array element is
    /// <c>R1[0,2]</c> (R-lvs3-4a) and a sub-cell's part is <c>U3/M1</c> (brief 9), and neither
    /// spelling exists in either editor's own list — a top-level document holds <c>R1</c> and
    /// <c>U3</c>. So a comparison strips both, which makes clicking a finding inside a placed module
    /// select the PLACEMENT, and that is the right answer: brief 9 reports a cell's findings once,
    /// and opening the cell is how its own parts are reached.
    /// </remarks>
    private static bool Same(string path, string name)
        => string.Equals(Leaf(path), Leaf(name), StringComparison.OrdinalIgnoreCase)
        || string.Equals(Head(path), Head(name), StringComparison.OrdinalIgnoreCase);

    private static string Leaf(string path)
    {
        int slash = path.LastIndexOf('/');
        string leaf = slash >= 0 ? path[(slash + 1)..] : path;
        int bracket = leaf.IndexOf('[');
        return bracket >= 0 ? leaf[..bracket] : leaf;
    }

    private static string Head(string path)
    {
        int slash = path.IndexOf('/');
        string head = slash >= 0 ? path[..slash] : path;
        int bracket = head.IndexOf('[');
        return bracket >= 0 ? head[..bracket] : head;
    }

    /// <summary>What the layout calls the parts currently selected in it.</summary>
    private IReadOnlyList<string> SelectedInstancePaths()
        => [.. _selectedInstanceIndices
               .Where(i => i >= 0 && i < Model.Instances.Count)
               .Select(i => LayoutDesignFlatten.PathOf(Model.Instances[i], i))];

    /// <summary>
    /// Selects every placement these names refer to, and returns how many. <b>Replaces</b> the
    /// instance selection — a cross-probe answers "where is this", not "add this to what I had".
    /// </summary>
    public int SelectLayoutDevices(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var wanted = names.ToList();
        var indices = new List<int>();

        for (int i = 0; i < Model.Instances.Count; i++)
        {
            string path = LayoutDesignFlatten.PathOf(Model.Instances[i], i);
            if (wanted.Any(n => Same(path, n))) indices.Add(i);
        }

        if (indices.Count > 0) SetInstanceSelection(indices);
        return indices.Count;
    }

    /// <summary>The same, in the schematic — zero when the drawing is not open.</summary>
    public int SelectSchematicDevices(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (LvsSchematic is not { } schematic) return 0;

        var wanted = names.ToList();
        var ids = schematic.EditModel.Components
            .Where(c => c.InstanceName is { Length: > 0 } n && wanted.Any(w => Same(w, n)))
            .Select(c => c.Id)
            .ToList();

        if (ids.Count > 0) schematic.Selection.SetAll(ids);
        return ids.Count;
    }
}

/// <summary>One row of the findings panel.</summary>
public sealed partial class LvsFindingRow : ObservableObject
{
    private readonly LayoutEditorViewModel _owner;

    public LvsFindingRow(LvsFinding finding, LayoutEditorViewModel owner)
    {
        Finding = finding;
        _owner  = owner;
        _reason = finding.WaiverReason ?? "";
    }

    public LvsFinding Finding { get; }

    /// <summary>The stable dotted id — <b>what the panel groups on</b> (R-lvs8-2a), shown because a
    /// user reporting a problem needs something to name that a reword cannot change.</summary>
    public string IdText => Finding.Id;

    public string MessageText => Finding.Render();

    /// <summary>The designer's own names, un-reduced (R-lvs8-2b). Empty for a run-level line.</summary>
    public string ObjectsText => string.Join(", ", Finding.Objects);

    public bool HasObjects => Finding.Objects.Count > 0;

    public bool IsWaived  => Finding.Waived;
    public bool IsError   => !Finding.Waived && Finding.Severity == DiagnosticSeverity.Error;
    public bool IsWarning => !Finding.Waived && Finding.Severity == DiagnosticSeverity.Warning;

    /// <summary>A waived row stays listed (waivers must be visible) but reads as settled.</summary>
    public double RowOpacity => Finding.Waived ? 0.55 : 1.0;

    /// <summary>False for a run-level line and for a schematic-only finding — there is nowhere to
    /// zoom to, and that absence IS the finding (see <c>LvsFinding.HasMarker</c>).</summary>
    public bool CanZoom => Finding.HasMarker;

    [ObservableProperty] private string _reason;

    [RelayCommand]
    private void ToggleWaive() => _owner.SetLvsWaived(this, !IsWaived, Reason);
}

/// <summary>
/// One waiver on this document that the last run matched nothing for — R-lvs12-4d.
/// </summary>
/// <remarks>
/// <b>Listed rather than dropped, and removable by a human who recognises it.</b> It carries the
/// finding's own sentence at the time of waiving, which is <c>DrcWaiver.RuleName</c>'s field and its
/// reasoning unchanged: six months later the key alone says nothing.
/// </remarks>
public sealed partial class LvsOrphanWaiverRow : ObservableObject
{
    private readonly LayoutEditorViewModel _owner;

    public LvsOrphanWaiverRow(LvsWaiver waiver, LayoutEditorViewModel owner)
    {
        Waiver = waiver;
        _owner = owner;
    }

    public LvsWaiver Waiver { get; }

    public string FindingText => Waiver.FindingText is { Length: > 0 } t ? t : Waiver.Key;

    public string ReasonText => Waiver.Reason is { Length: > 0 } r ? $"Waived because: {r}" : "No reason given.";

    [RelayCommand]
    private void Remove() => _owner.RemoveLvsWaiver(Waiver);
}

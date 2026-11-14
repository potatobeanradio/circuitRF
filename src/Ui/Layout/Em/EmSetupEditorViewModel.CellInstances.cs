// brief-icp-7 R-icp7-1/2/4/5 — the Component ports section's cell instances: a tree under the flat footprint rows. Each placed cell
// instance that is not a footprint part has a row under its parent (X1 ▸ Q3) with its path, its cell, a box and the verdict ICP-2's
// build reached for it; checking a parent disables the rows under it (ICP-0 D8: a nested cut is refused) and takes their entries
// out in the same undo step. A cell placed more than once also has one "every <cell>" row, the entry's Cell key. An included
// instance's options — Port at, Reference, Z0, Keep, the keep-out box, and each pin's own reference, Port off or Ground (ICP-4) —
// write its entry through EmComponentPort.Override (LCP-3's one entry-writing rule), one undo entry each.
//
// Nothing here builds: the rows come from the listing (read off the UI thread) and the verdicts from the build Refresh already made
// off the UI thread (ICP-2 R-icp2-8's cached build) or from Check's. An option edit changes Working, whose content keys that cache.

using System.Collections.ObjectModel;
using System.Globalization;
using CircuitRF.Design.Layout.Em;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>A choice of a picker in an instance's options: the value written (null for the default) and what it reads as.</summary>
public sealed record EmOptionChoice(string? Value, string Display)
{
    public override string ToString() => Display;
}

/// <summary>One name a cut can keep in place (a net or a drawing layer), and whether this entry keeps it.</summary>
public sealed partial class EmKeepChoice(EmComponentInstanceRow owner, string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty] private bool _isKept;

    internal bool Syncing;

    partial void OnIsKeptChanged(bool value)
    {
        if (!Syncing) owner.KeepChanged();
    }
}

/// <summary>brief-icp-4 per-pin options, as one row under an instance: its own reference, Port off, Ground.</summary>
public sealed partial class EmCutPinOptionRow(EmComponentInstanceRow owner, string pin, IReadOnlyList<EmOptionChoice> references) : ObservableObject
{
    public string Pin { get; } = pin;

    /// <summary>The instance's reference (the default), Ground, or one of its other pins.</summary>
    public IReadOnlyList<EmOptionChoice> ReferenceChoices { get; } = references;

    [ObservableProperty] private EmOptionChoice? _reference;

    /// <summary>The pin carries a port (unticked: <c>Port: false</c>, only a return).</summary>
    [ObservableProperty] private bool _hasPort = true;

    /// <summary>Tied to ground at its face, no port.</summary>
    [ObservableProperty] private bool _isGrounded;

    internal bool Syncing;

    partial void OnReferenceChanged(EmOptionChoice? value) { if (!Syncing) owner.PinChanged(); }
    partial void OnHasPortChanged(bool value) { if (!Syncing) owner.PinChanged(); }
    partial void OnIsGroundedChanged(bool value) { if (!Syncing) owner.PinChanged(); }
}

/// <summary>brief-icp-7 R-icp7-1 — one placed cell instance in the tree.</summary>
public sealed partial class EmComponentInstanceRow : ObservableObject
{
    private readonly EmSetupEditorViewModel _owner;

    internal EmComponentInstanceRow(EmSetupEditorViewModel owner, EmComponentInstanceItem item, IReadOnlyList<string> keepNames)
    {
        _owner = owner;
        Item = item;
        PortAtChoices = [new("Pin", "At each pin's face"), new("Cell", "At its box")];
        var refs = new List<EmOptionChoice> { new(null, "Ground"), new(EmComponentPort.CommonReference, "Common") };
        refs.AddRange(item.Pins.Select(p => new EmOptionChoice(p, $"Pin {p}")));
        ReferenceChoices = refs;
        KeepChoices = [.. keepNames.Select(n => new EmKeepChoice(this, n))];
        PinRows = [.. item.Pins.Select(p =>
        {
            var choices = new List<EmOptionChoice> { new(null, "As the instance"), new(EmComponentPort.GroundReference, "Ground") };
            choices.AddRange(item.Pins.Where(o => o != p).Select(o => new EmOptionChoice(o, $"Pin {o}")));
            return new EmCutPinOptionRow(this, p, choices);
        })];
    }

    public EmComponentInstanceItem Item { get; }

    public string Path => Item.Path;

    /// <summary>The row's own name: the last segment of its path.</summary>
    public string Name => Item.Path[(Item.Path.LastIndexOf('/') + 1)..];

    public string CellLabel => Item.CellLabel;

    /// <summary>Indented by depth, so a child sits under its parent (device-independent pixels; this folder stays framework-free).</summary>
    public double IndentWidth => 16 * Item.Depth;

    /// <summary>What it is, for the tooltip: a device, or copper the solver can solve (cut only when named).</summary>
    public string KindTip => Item.Kind == CircuitRF.Design.Layout.Em.EmInstanceKind.Part
        ? $"{Path} ({CellLabel}): a device. \"*\" takes it."
        : $"{Path} ({CellLabel}): copper the solver can solve. \"*\" passes over it; tick it, or its cell, to cut it out.";

    [ObservableProperty] private bool _isIncluded;

    /// <summary>False when an instance above it is included: a nested cut is refused, so the row cannot be ticked.</summary>
    [ObservableProperty] private bool _isEnabled = true;

    /// <summary>The verdict the last build reached for it: built (and how many ports), or why not. Empty before one has.</summary>
    [ObservableProperty] private string _verdict = "";

    [ObservableProperty] private bool _isRefused;

    /// <summary>The options are shown.</summary>
    [ObservableProperty] private bool _isExpanded;

    public IReadOnlyList<EmOptionChoice> PortAtChoices { get; }
    [ObservableProperty] private EmOptionChoice? _portAt;

    public IReadOnlyList<EmOptionChoice> ReferenceChoices { get; }
    [ObservableProperty] private EmOptionChoice? _reference;

    [ObservableProperty] private string _z0Text = "";
    [ObservableProperty] private string? _z0Error;

    public IReadOnlyList<EmKeepChoice> KeepChoices { get; }

    /// <summary>The keep-out box, µm in the instance's own frame; all four empty is the cell's own box.</summary>
    [ObservableProperty] private string _keepOutX0 = "";
    [ObservableProperty] private string _keepOutY0 = "";
    [ObservableProperty] private string _keepOutX1 = "";
    [ObservableProperty] private string _keepOutY1 = "";
    [ObservableProperty] private string? _keepOutError;

    public IReadOnlyList<EmCutPinOptionRow> PinRows { get; }

    internal bool Syncing;

    partial void OnIsIncludedChanged(bool value) { if (!Syncing) _owner.ComponentInstanceToggled(this); }
    partial void OnPortAtChanged(EmOptionChoice? value) { if (!Syncing) _owner.CommitInstanceOption(this, e => e with { PortAt = value?.Value == "Cell" ? EmComponentPortAt.Cell : EmComponentPortAt.Pin }, "port at"); }
    partial void OnReferenceChanged(EmOptionChoice? value) { if (!Syncing) _owner.CommitInstanceOption(this, e => e with { Reference = value?.Value }, "reference"); }

    internal void KeepChanged()
    {
        var kept = KeepChoices.Where(k => k.IsKept).Select(k => k.Name).ToList();
        _owner.CommitInstanceOption(this, e => e with { Keep = kept.Count == 0 ? null : string.Join(", ", kept) }, "keep");
    }

    internal void PinChanged()
    {
        var pins = new Dictionary<string, EmPinOptions>(StringComparer.OrdinalIgnoreCase);
        var grounded = new List<string>();
        foreach (var r in PinRows)
        {
            if (r.IsGrounded) grounded.Add(r.Pin);
            if (r.Reference?.Value is not null || !r.HasPort) pins[r.Pin] = new EmPinOptions(r.Reference?.Value, r.HasPort ? null : false);
        }
        _owner.CommitInstanceOption(this, e => e.WithPins(pins) with { Ground = grounded.Count == 0 ? null : string.Join(", ", grounded) }, "pins");
    }

    /// <summary>The options, from the entry (after an edit, an undo, or the listing).</summary>
    internal void Sync(EmComponentPort? entry)
    {
        Syncing = true;
        IsIncluded = entry is not null;
        PortAt = PortAtChoices[entry?.PortAt == EmComponentPortAt.Cell ? 1 : 0];
        Reference = ReferenceChoices.FirstOrDefault(c => string.Equals(c.Value, entry?.Reference, StringComparison.OrdinalIgnoreCase)) ?? ReferenceChoices[0];
        Z0Text = entry?.Z0 ?? "";
        var keep = entry?.KeepNames ?? [];
        foreach (var k in KeepChoices) { k.Syncing = true; k.IsKept = keep.Contains(k.Name, StringComparer.OrdinalIgnoreCase); k.Syncing = false; }
        var box = entry?.KeepOutUm;
        KeepOutX0 = Num(box?.X0); KeepOutY0 = Num(box?.Y0); KeepOutX1 = Num(box?.X1); KeepOutY1 = Num(box?.Y1);
        var options = entry?.PinOptions ?? new Dictionary<string, EmPinOptions>();
        var ground = entry?.GroundNames ?? [];
        foreach (var r in PinRows)
        {
            r.Syncing = true;
            var o = options.GetValueOrDefault(r.Pin);
            r.Reference = r.ReferenceChoices.FirstOrDefault(c => string.Equals(c.Value, o?.Reference, StringComparison.OrdinalIgnoreCase)) ?? r.ReferenceChoices[0];
            r.HasPort = o?.Port != false;
            r.IsGrounded = ground.Contains(r.Pin, StringComparer.OrdinalIgnoreCase) || o?.Ground == true;
            r.Syncing = false;
        }
        Syncing = false;

        static string Num(double? v) => v?.ToString("G6", CultureInfo.InvariantCulture) ?? "";
    }

    public void CommitZ0() => _owner.CommitInstanceZ0(this);

    public void CommitKeepOut() => _owner.CommitInstanceKeepOut(this);
}

/// <summary>brief-icp-7 R-icp7-1 — one cell placed more than once: its <c>Cell</c> entry, every instance of it.</summary>
public sealed partial class EmComponentCellRow(EmSetupEditorViewModel owner, string cell, int count) : ObservableObject
{
    public string Cell { get; } = cell;

    public string Text { get; } = $"every {cell} — {count} placed";

    [ObservableProperty] private bool _isIncluded;

    internal bool Syncing;

    partial void OnIsIncludedChanged(bool value) { if (!Syncing) owner.ComponentCellToggled(this); }
}

public sealed partial class EmSetupEditorViewModel
{
    /// <summary>brief-icp-7 R-icp7-1 — the placed cell instances, parents before children.</summary>
    public ObservableCollection<EmComponentInstanceRow> ComponentInstanceRows { get; } = [];

    /// <summary>brief-icp-7 R-icp7-1 — one row per cell placed more than once.</summary>
    public ObservableCollection<EmComponentCellRow> ComponentCellRows { get; } = [];

    public bool HasComponentInstances => ComponentInstanceRows.Count > 0;

    /// <summary>R-icp7-1 — what <c>*</c> takes, for its row's tooltip.</summary>
    public static string AllComponentPartsTip { get; } =
        "\"*\": every placed part with two pads or more and every device cell. It never takes copper the solver can solve (a PCell, " +
        "a line, a spiral): tick those, or their cell, to cut them out.";

    private IReadOnlyList<EmComponentInstanceItem> _placedInstances = [];

    /// <summary>The tree, from the listing; then synced to the setup.</summary>
    private void RebuildComponentInstanceRows(IReadOnlyList<EmComponentInstanceItem> instances)
    {
        _placedInstances = instances;
        ComponentInstanceRows.Clear();
        ComponentCellRows.Clear();
        var keepNames = KeepNameChoices();
        foreach (var item in instances) ComponentInstanceRows.Add(new EmComponentInstanceRow(this, item, keepNames));
        foreach (var g in instances.GroupBy(i => i.CellLabel, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            ComponentCellRows.Add(new EmComponentCellRow(this, g.Key, g.Count()));
        OnPropertyChanged(nameof(HasComponentInstances));
        SyncComponentInstanceRows();
        UpdateInstanceVerdicts();
    }

    /// <summary>The names a cut can keep: the layout's nets, then its technology's drawing layers on conductors — a picker, since both
    /// can be listed.</summary>
    private IReadOnlyList<string> KeepNameChoices()
    {
        var names = new List<string>();
        if (_componentSource is { } s)
        {
            names.AddRange(s.View.Shapes.Select(x => x.Net).Where(n => n is { Length: > 0 }).Select(n => n!)
                            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));
            if (s.Technology is { } tech)
            {
                var conductorLayers = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).SelectMany(l => l.DrawingLayers).ToHashSet();
                names.AddRange(tech.Layers.Where(l => conductorLayers.Contains(l.Key)).Select(l => l.Name).Where(n => n.Length > 0));
            }
        }
        return [.. names.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The boxes and options, from the setup; a row under an included one is disabled.</summary>
    private void SyncComponentInstanceRows()
    {
        foreach (var row in ComponentInstanceRows)
            row.Sync(Working.ComponentPorts.FirstOrDefault(c => c.Cell is null && c.Refdes.Trim().Equals(row.Path, StringComparison.OrdinalIgnoreCase)));
        var included = ComponentInstanceRows.Where(r => r.IsIncluded).Select(r => r.Path).ToList();
        foreach (var row in ComponentInstanceRows)
            row.IsEnabled = !included.Any(p => row.Path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase));
        foreach (var row in ComponentCellRows)
        {
            row.Syncing = true;
            row.IsIncluded = Working.ComponentPorts.Any(c => c.Cell is { } cell && cell.Equals(row.Cell, StringComparison.OrdinalIgnoreCase));
            row.Syncing = false;
        }
    }

    /// <summary>R-icp7-1 — a row's box: its entry added (any settings it had kept) or removed; ticking it takes out the entries of the
    /// instances under it in the same undo step, since a nested cut is refused.</summary>
    internal void ComponentInstanceToggled(EmComponentInstanceRow row)
    {
        string before = SnapshotJson();
        var list = Working.ComponentPorts.Where(c => !(c.Cell is null && c.Refdes.Trim().Equals(row.Path, StringComparison.OrdinalIgnoreCase))).ToList();
        if (row.IsIncluded)
        {
            list.RemoveAll(c => c.Cell is null && c.Refdes.Trim().StartsWith(row.Path + "/", StringComparison.OrdinalIgnoreCase));
            list.Add(_removedInstanceEntries.GetValueOrDefault(row.Path) ?? new EmComponentPort(row.Path));
        }
        else if (Working.ComponentPorts.FirstOrDefault(c => c.Cell is null && c.Refdes.Trim().Equals(row.Path, StringComparison.OrdinalIgnoreCase)) is { } gone)
            _removedInstanceEntries[row.Path] = gone;    // ticked again this session, it comes back with its settings
        Working.ComponentPorts = list;
        SyncComponentInstanceRows();
        CommitComponentPorts(before, $"Component port {row.Path}");
    }

    private readonly Dictionary<string, EmComponentPort> _removedInstanceEntries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>R-icp7-1 — a by-cell row: the <c>Cell</c> entry added or removed.</summary>
    internal void ComponentCellToggled(EmComponentCellRow row)
    {
        string before = SnapshotJson();
        var list = Working.ComponentPorts.Where(c => !(c.Cell is { } cell && cell.Equals(row.Cell, StringComparison.OrdinalIgnoreCase))).ToList();
        if (row.IsIncluded) list.Add(new EmComponentPort("") { Cell = row.Cell });
        Working.ComponentPorts = list;
        SyncComponentInstanceRows();
        CommitComponentPorts(before, $"Component ports: every {row.Cell}");
    }

    /// <summary>R-icp7-2 — an included instance's option: its entry through <see cref="EmComponentPort.Override"/>, one undo entry
    /// (none when nothing changed). An instance not included takes no option: it is included first.</summary>
    internal void CommitInstanceOption(EmComponentInstanceRow row, Func<EmComponentPort, EmComponentPort> change, string what)
    {
        if (_suppressCommit || !row.IsIncluded) { row.Sync(Working.ComponentPorts.FirstOrDefault(c => c.Cell is null && c.Refdes.Trim().Equals(row.Path, StringComparison.OrdinalIgnoreCase))); return; }
        string before = SnapshotJson();
        Working.ComponentPorts = EmComponentPort.Override(Working.ComponentPorts, row.Path, change);
        CommitComponentPorts(before, $"Component port {row.Path} {what}");
        row.Sync(Working.ComponentPorts.FirstOrDefault(c => c.Cell is null && c.Refdes.Trim().Equals(row.Path, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>R-icp7-2 — the Z0 box: blank is the setup's reference impedance.</summary>
    internal void CommitInstanceZ0(EmComponentInstanceRow row)
    {
        string text = row.Z0Text.Trim();
        if (EmComponentPort.Z0Refusal(text) is { } why) { row.Z0Error = why; return; }
        row.Z0Error = null;
        CommitInstanceOption(row, e => e with { Z0 = text.Length == 0 ? null : text }, "Z0");
    }

    /// <summary>R-icp7-2 — the keep-out box: four numbers of µm, or all four empty for the cell's own box.</summary>
    internal void CommitInstanceKeepOut(EmComponentInstanceRow row)
    {
        string[] texts = [row.KeepOutX0.Trim(), row.KeepOutY0.Trim(), row.KeepOutX1.Trim(), row.KeepOutY1.Trim()];
        if (texts.All(t => t.Length == 0))
        {
            row.KeepOutError = null;
            CommitInstanceOption(row, e => e with { KeepOutUm = null }, "keep-out box");
            return;
        }
        var v = new double[4];
        for (int i = 0; i < 4; i++)
            if (!NumericText.TryParseDouble(texts[i], out v[i]))
            {
                row.KeepOutError = "The box is four numbers of micrometres in the instance's own frame — x0, y0, x1, y1 — or all four empty for its own box.";
                return;
            }
        if (!(v[2] > v[0] && v[3] > v[1])) { row.KeepOutError = "The box needs x1 > x0 and y1 > y0."; return; }
        row.KeepOutError = null;
        CommitInstanceOption(row, e => e with { KeepOutUm = new EmKeepOut(v[0], v[1], v[2], v[3]) }, "keep-out box");
    }

    /// <summary>R-icp7-1/5 — each instance's verdict, from Check's verdicts when it has run since the list last changed, else from the
    /// build Refresh made (the cached one Check, the marks and the mesh preview share). Nothing is built here.</summary>
    private void UpdateInstanceVerdicts(IReadOnlyList<EmComponentPortVerdict>? fromCheck = null)
    {
        if (ComponentInstanceRows.Count == 0) return;
        IReadOnlyList<(string Refdes, bool Ok, string Line)> verdicts = fromCheck is not null
            ? [.. fromCheck.Select(v => (v.Refdes, v.Ok, v.Line))]
            : _lastComponentBuild is { } build
                ? [.. build.Ports.Where(p => p.Cutout).Select(p => (p.Refdes, p.Ok, p.Ok ? p.Name : p.Refusal ?? ""))]
                : [];
        foreach (var row in ComponentInstanceRows)
        {
            var mine = verdicts.Where(v => v.Refdes.Equals(row.Path, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!row.IsIncluded || mine.Count == 0)
            {
                row.Verdict = row.Item.Why is { } why ? why : "";
                row.IsRefused = row.Item.Why is not null && row.IsIncluded;
                continue;
            }
            int ok = mine.Count(v => v.Ok);
            var refused = mine.Where(v => !v.Ok).ToList();
            row.IsRefused = ok == 0;
            row.Verdict = refused.Count == 0 ? $"cut out; {ok} port{(ok == 1 ? "" : "s")}"
                : ok == 0 ? $"left out: {refused[0].Line}"
                : $"cut out; {ok} port{(ok == 1 ? "" : "s")}, {refused.Count} pin(s) left out: {refused[0].Line}";
        }
    }
}

// brief-emb-1 R-emb1-8 — the setup panel's "Component ports" list: every placed part, a box to include it (writing the
// setup's ComponentPorts through the panel's one undoable commit), "Every two-terminal part" for `*`, and Check, which builds
// and verifies the ports exactly as a run would and posts each part's verdict to Messages. The panel itself keeps one short
// line and marks a part that was left out; the sentences live in Messages, where they can be read and copied without crowding
// the setup. A picker, not a text box: the parts can be listed, so a misspelt refdes cannot be written.
//
// brief-lcp-3 — one section, two hosts (IEmComponentPortsHost): a 3D view's setup, and a layout's .cem. The list, the boxes,
// Check and the status line are the same on both. On a Planar .cem each built component port also has a row in the Ports
// list ("Port 3 · C1"), where its Z0, width and reference pin are set; those write the part's entry, never PortZ0s.
//
// An entry naming a part that is not placed keeps a row, marked, so it can be unticked: renaming a part in the layout must
// not erase its settings without saying so. The parts are read again only when what is placed may have changed
// (IEmComponentPortsHost.PartsKey), off the UI thread; a layout edit only marks the last Check's verdicts stale.

using System.Collections.ObjectModel;
using System.Globalization;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Mom;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>One part in the list: placed, or named by the setup and not placed.</summary>
public sealed partial class EmComponentPortRow(EmSetupEditorViewModel owner, string refdes, int pads, bool missing = false, bool unread = false)
    : ObservableObject
{
    public string Refdes { get; } = refdes;

    /// <summary>The setup names this part and nothing by that name is placed.</summary>
    public bool IsMissing { get; } = missing;

    /// <summary>"2 pads", "3 pads", or "not on the layout"; nothing when the parts could not be read, which says nothing either way.</summary>
    public string PadsText { get; } = unread ? "" : missing ? "not on the layout" : pads == 2 ? "2 pads" : $"{pads} pads";

    /// <summary>A placed part with two pads: what <c>*</c> takes.</summary>
    public bool IsTwoTerminal => !IsMissing && PadsText == "2 pads";

    [ObservableProperty] private bool _isIncluded;

    /// <summary>The last Check left this part out; why is in Messages.</summary>
    [ObservableProperty] private bool _isRefused;

    /// <summary>Drawn as a warning: left out by the last Check, or not on the layout.</summary>
    public bool IsMarked => IsRefused || IsMissing;

    internal bool Syncing;

    partial void OnIsIncludedChanged(bool value)
    {
        if (!Syncing) owner.ComponentPortToggled();
    }

    partial void OnIsRefusedChanged(bool value) => OnPropertyChanged(nameof(IsMarked));
}

/// <summary>One row of a component port's reference picker: a pin of the part, or the default.</summary>
public sealed record EmPinChoice(string? Pin, string Display)
{
    public override string ToString() => Display;
}

public sealed partial class EmSetupEditorViewModel
{
    private IEmComponentPortsHost? _componentPortsHost;
    private string? _componentPartsKey;
    private int _componentPartsGeneration;
    private IReadOnlyList<EmComponentPart> _placedParts = [];
    private bool _placedPartsRead;

    /// <summary>Where the list reads its parts and builds its ports. A 3D view sets its own; a <c>.cem</c> reads the layout this
    /// panel resolves.</summary>
    public IEmComponentPortsHost? ComponentPortsHost
    {
        get => _componentPortsHost;
        set
        {
            _componentPortsHost = value;
            _componentPartsKey = null;
            OnPropertyChanged(nameof(ShowComponentPorts));
            RefreshComponentParts();
        }
    }

    /// <summary>Set by the workspace on a <c>.cem</c>: posts one line to Messages, and whether it is a warning.</summary>
    public Action<string, bool>? PostMessage { get; set; }

    /// <summary>The listing the last refresh started, for a caller that has to wait for it.</summary>
    public Task ComponentPartsListing { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// R-lcp3-2 — by setup type, so the section's place is reserved as the rest of the panel's is: hidden on a thermal setup and an
    /// electrostatic one (no ports), and on a setup that names the uniform-line kernel, whose ports are the line's two ends.
    /// </summary>
    public bool ShowComponentPorts => ComponentPortsHost is not null && !IsThermalSetup && !IsElectrostaticSetup
                                      && (Is3DSetup || Working.AnalysisKind != EmAnalysisKind.CrossSection);

    public ObservableCollection<EmComponentPortRow> ComponentPortRows { get; } = [];

    /// <summary>The setup's <c>"*"</c>: every placed two-terminal part.</summary>
    [ObservableProperty] private bool _allComponentParts;

    /// <summary>What the last Check found, or why the list is empty.</summary>
    [ObservableProperty] private string _componentPortsStatus = "";

    /// <summary>R-lcp3-6 — the section's one explanation, for the solver that builds the ports.</summary>
    public string ComponentPortsTip => Is3DSetup ? ComponentPortsTip3D : ComponentPortsTipPlanar;

    public static string ComponentPortsTip3D { get; } =
        "A lumped gap port across each chosen part's own pads, built on the board's copper and checked before meshing: the " +
        "sheet's ends land on the closest copper (a landing rectangle where that copper is a cap or an arc), and nothing it " +
        "touches bridges two nets. A part with more pads gets one port per signal pad against its own ground pad. A finished " +
        "driven run also writes <setup>_cosim.cnl: the board's N-port with each part's model across its port. With Create SnP schematic " +
        "on, the same circuit is also made as a <setup>_cosim cell.";

    public static string ComponentPortsTipPlanar { get; } =
        "A component port across each chosen part's own pads. The solver adds a strip of copper between the two pads and puts " +
        "the port across it, in the gap between the pads; the strip exists in the solved problem only, and nothing is added to " +
        "the layout. A part with more pads gets one port per signal pad against its own ground pad. Check builds them as a run " +
        "would and says in Messages why any part is left out. Each port's Z0, width and reference pin are set in its row under Ports. " +
        "A finished run also writes <setup>_cosim.cnl: the board's N-port with each part's model across its port. With Create SnP " +
        "schematic on, the same circuit is also made as a <setup>_cosim cell.";

    /// <summary>Builds and verifies the ports as a run would, off the UI thread (a board's elaboration and every part's port
    /// is seconds of work): the setup and its geometry are read as they stand when Check is pressed, and the panel stays usable
    /// meanwhile. The button is disabled until it finishes.</summary>
    [RelayCommand]
    private async Task CheckComponentPorts()
    {
        if (ComponentPortsHost is not { } host) return;
        foreach (var row in ComponentPortRows) row.IsRefused = false;
        if (Working.ComponentPorts.Count == 0) { ComponentPortsStatus = "No part is chosen."; return; }
        var check = host.PrepareCheck(Working);
        // What this Check is about: a layout edit (MarkComponentPortsStale) or a change to the list while it runs makes its
        // verdicts history, posted to Messages but not shown as the current state of the parts.
        int generation = ++_componentCheckGeneration;
        string listAtStart = ComponentPortsKey(Working);
        ComponentPortsStatus = "Checking…";
        EmComponentPortCheck result;
        try { result = await Task.Run(check); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException or FormatException)
        {
            result = EmComponentPortCheck.Refused($"Component ports: {e.Message}");
        }
        bool current = generation == _componentCheckGeneration && listAtStart == ComponentPortsKey(Working);
        foreach (var p in result.Ports)
        {
            host.Post(p.Line, !p.Ok);
            if (current && !p.Ok && ComponentPortRows.FirstOrDefault(r => r.Refdes.Equals(p.Refdes, StringComparison.OrdinalIgnoreCase)) is { } row)
                row.IsRefused = true;
        }
        foreach (string w in result.Warnings) host.Post(w, true);
        if (result.Refusal is { } whole) host.Post(whole, true);
        if (current) UpdateInstanceVerdicts(result.Ports);
        if (!current)
        {
            ComponentPortsStatus = "The layout or the list has changed since this Check started; press Check again.";
            return;
        }
        int ok = result.Ports.Count(p => p.Ok), left = result.Ports.Count - ok;
        ComponentPortsStatus = result.NotBuilt ? "Not built; see Messages."
            : left == 0 ? $"{ok} built; details in Messages."
            : $"{ok} built, {left} left out; see Messages.";
        _componentPortsChecked = true;
    }

    private bool _componentPortsChecked;
    private int _componentCheckGeneration;

    private static string ComponentPortsKey(EmSetup setup) => string.Join("|", setup.ComponentPorts);

    /// <summary>R-lcp3-5 — the layout was edited: the last Check's verdicts no longer describe it. Nothing is rebuilt here; on a
    /// board that is seconds of work, and Check is one press away.</summary>
    public void MarkComponentPortsStale()
    {
        _componentCheckGeneration++;          // a Check still running is about the layout before this edit
        if (!_componentPortsChecked) return;
        _componentPortsChecked = false;
        foreach (var row in ComponentPortRows) row.IsRefused = false;
        ComponentPortsStatus = "The layout has changed since the last Check.";
    }

    /// <summary>The placed parts, read again when what is placed may have changed (or <paramref name="force"/>), off the UI thread.</summary>
    public void RefreshComponentParts(bool force = false)
    {
        if (ComponentPortsHost is not { } host || !ShowComponentPorts)
        {
            _componentPartsKey = null;
            _componentPartsGeneration++;
            ComponentPortRows.Clear();
            ComponentInstanceRows.Clear();
            ComponentCellRows.Clear();
            OnPropertyChanged(nameof(HasComponentInstances));
            return;
        }
        string key = host.PartsKey();
        if (!force && key == _componentPartsKey) return;
        _componentPartsKey = key;
        int mine = ++_componentPartsGeneration;
        Func<EmComponentPartListing> list;
        try { list = host.PrepareList(); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException)
        {
            list = () => EmComponentPartListing.Unread(e.Message);
        }
        ComponentPartsListing = ListComponentPartsAsync(list, mine);
    }

    private async Task ListComponentPartsAsync(Func<EmComponentPartListing> list, int mine)
    {
        EmComponentPartListing listing;
        try { listing = await Task.Run(list); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException or FormatException)
        {
            listing = EmComponentPartListing.Unread($"The parts could not be listed: {e.Message}");
        }
        if (mine != _componentPartsGeneration) return;
        _placedParts = listing.Parts;
        _placedPartsRead = listing.Read;
        _placedInstances = listing.Instances;
        _componentPortsChecked = false;
        RebuildComponentPortRows();
        RebuildComponentInstanceRows(listing.Instances);
        ComponentPortsStatus = listing.Status ?? "";
        // The Ports list's component rows name each part's pins in their reference picker.
        if (_lastPlanarPortRows is { } rows && _lastComponentBuild?.Ports.Any(p => p.Ok) == true) RebuildPlanarPortRows(rows);
    }

    /// <summary>One row per placed part, then one per part the setup names that is not placed (or, when the parts could not be
    /// read, that is simply named: it can still be unticked).</summary>
    private void RebuildComponentPortRows()
    {
        ComponentPortRows.Clear();
        // brief-icp-7 — a cell instance that is not a footprint part is the tree's row, not a flat one.
        foreach (var part in _placedParts.Where(p => !_placedInstances.Any(i => i.Path.Equals(p.Refdes, StringComparison.OrdinalIgnoreCase))))
            ComponentPortRows.Add(new EmComponentPortRow(this, part.Refdes, part.Pads));
        foreach (var entry in Working.ComponentPorts.Where(c => c.Refdes != EmSetup.AllComponents && !IsInstanceEntry(c)))
            if (!ComponentPortRows.Any(r => r.Refdes.Equals(entry.Refdes.Trim(), StringComparison.OrdinalIgnoreCase)))
                ComponentPortRows.Add(new EmComponentPortRow(this, entry.Refdes.Trim(), 0, missing: _placedPartsRead, unread: !_placedPartsRead));
        SyncComponentPortRows();
    }

    /// <summary>The boxes, from the setup (after an undo, or a setup edited elsewhere).</summary>
    private void SyncComponentPortRows()
    {
        // An undo can bring back an entry for a part that is not placed: it gets its row again. An unticked one keeps its row
        // until the parts are next read, so unticking it moves nothing below it (R-lcp3-7).
        if (_componentPartsKey is not null && Working.ComponentPorts.Any(c => c.Refdes != EmSetup.AllComponents && !IsInstanceEntry(c)
                && !ComponentPortRows.Any(r => r.Refdes.Equals(c.Refdes.Trim(), StringComparison.OrdinalIgnoreCase))))
        {
            RebuildComponentPortRows();
            return;
        }
        bool all = Working.ComponentPorts.Any(c => c.Refdes == EmSetup.AllComponents);
        bool was = _suppressCommit;
        _suppressCommit = true;
        AllComponentParts = all;
        _suppressCommit = was;
        foreach (var row in ComponentPortRows)
        {
            row.Syncing = true;
            bool named = Working.ComponentPorts.Any(c => c.Refdes.Trim().Equals(row.Refdes, StringComparison.OrdinalIgnoreCase));
            row.IsIncluded = named || (all && row.IsTwoTerminal);
            row.Syncing = false;
        }
        SyncComponentInstanceRows();
    }

    /// <summary>brief-icp-7 — an entry the tree owns, not a footprint row: a <c>Cell</c> entry, or a placed cell instance's path (any
    /// path, and an unnamed placement's <c>@n</c>).</summary>
    private bool IsInstanceEntry(EmComponentPort c)
    {
        string rd = c.Refdes.Trim();
        return c.Cell is not null || rd.Length == 0 || rd.Contains('/') || rd.StartsWith('@')
               || _placedInstances.Any(i => i.Path.Equals(rd, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The entries the tree owns, kept when the footprint rows rewrite theirs.</summary>
    private IEnumerable<EmComponentPort> InstanceEntries() => Working.ComponentPorts.Where(c => c.Refdes != EmSetup.AllComponents && IsInstanceEntry(c));

    partial void OnAllComponentPartsChanged(bool value)
    {
        if (_suppressCommit) return;
        string before = SnapshotJson();
        // A part's own settings (its Z0, its width) are kept beside "*": ticking every part must not throw them away.
        Working.ComponentPorts = value ? [new EmComponentPort(EmSetup.AllComponents), .. Working.ComponentPorts.Where(c => c.Refdes != EmSetup.AllComponents && (!c.IsPlain || IsInstanceEntry(c)))]
                               : [.. ComponentPortRows.Where(r => r.IsIncluded).Select(r => Keep(r.Refdes)), .. InstanceEntries()];
        SyncComponentPortRows();
        CommitComponentPorts(before, "Component ports");
    }

    /// <summary>A row's box was clicked: the setup names exactly the ticked parts (an entry's Z0, Reference and width are kept).</summary>
    internal void ComponentPortToggled()
    {
        string before = SnapshotJson();
        Working.ComponentPorts = [.. ComponentPortRows.Where(r => r.IsIncluded).Select(r => Keep(r.Refdes)),
                                  .. InstanceEntries().Where(c => !ComponentPortRows.Any(r => r.Refdes.Equals(c.Refdes.Trim(), StringComparison.OrdinalIgnoreCase)))];
        _suppressCommit = true;
        AllComponentParts = false;
        _suppressCommit = false;
        foreach (var row in ComponentPortRows) row.IsRefused = false;
        _componentPortsChecked = false;
        ComponentPortsStatus = "";
        CommitComponentPorts(before, "Component ports");
    }

    private EmComponentPort Keep(string refdes)
        => Working.ComponentPorts.FirstOrDefault(c => c.Refdes.Trim().Equals(refdes, StringComparison.OrdinalIgnoreCase)) ?? new EmComponentPort(refdes);

    /// <summary>One undo entry. On a <c>.cem</c> the entry's own apply refreshes the panel, so the Ports list shows the ports the
    /// run would build; the mesh shown no longer has them. A 3D view rebuilds from the committed document itself.</summary>
    private void CommitComponentPorts(string before, string description)
    {
        if (SnapshotJson() == before) return;
        CommitEdit(before, description);
        if (!IsEmbedded) InvalidateMesh();
    }

    // ── brief-lcp-4: the marks the layout draws ────────────────────────────────────────────────────────────────────────────
    //
    // The ports this setup builds, for the layout to draw (R-lcp4-1). Built as the run builds them, on a copy of the layout,
    // off the UI thread, once the layout or the setup has settled: a burst of edits builds once, after the last. Refresh asks
    // for a build; one already made for the same layout state and the same list is kept. A drag edits nothing until it is
    // dropped, so nothing is built during one (R-lcp4-4).

    /// <summary>The built ports' marks, and the layout and edit they were built from. Empty when none are built.</summary>
    public IReadOnlyList<LayoutComponentPortMark> ComponentPortMarks { get; private set; } = [];

    /// <summary>brief-icp-7 R-icp7-3 — the cell instances this setup cuts out, as the layout draws them. Empty when none are.</summary>
    public IReadOnlyList<LayoutCutMark> ComponentCutMarks { get; private set; } = [];

    /// <summary>brief-icp-7 R-icp7-6 — what this setup's run hands its extractor, built when <see cref="SolverGeometryWanted"/>; null
    /// otherwise.</summary>
    public IReadOnlyList<LayoutShape>? SolverGeometry { get; private set; }

    /// <summary>brief-icp-7 R-icp7-6 — set by the workspace while the layout this setup speaks for shows the solver's geometry: the
    /// marks build then carries it too.</summary>
    public bool SolverGeometryWanted
    {
        get => _solverGeometryWanted;
        set
        {
            if (_solverGeometryWanted == value) return;
            _solverGeometryWanted = value;
            ScheduleComponentPortMarks();
        }
    }

    private bool _solverGeometryWanted;

    /// <summary>The layout <see cref="ComponentPortMarks"/> describe, or null.</summary>
    public LayoutView? ComponentPortMarksView { get; private set; }

    /// <summary>That layout's <see cref="LayoutView.EditCount"/> when it was copied.</summary>
    public long ComponentPortMarksEditCount { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="ComponentPortMarks"/> has been built again.</summary>
    public event Action? ComponentPortMarksChanged;

    /// <summary>How long the layout and the setup must be still before the marks are built.</summary>
    public TimeSpan ComponentPortMarksSettle { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>The build the last request started, for a caller that has to wait for it.</summary>
    public Task ComponentPortMarksBuilding { get; private set; } = Task.CompletedTask;

    /// <summary>The layout the list reads, for the workspace to tell whether it is the open editor's.</summary>
    internal LayoutView? ComponentSourceView => _componentSource?.View;

    private int _marksGeneration;
    private string? _marksKey;

    /// <summary>Asks for the marks to be built once things settle. Kept when nothing they depend on has changed.</summary>
    public void ScheduleComponentPortMarks()
    {
        string key = ComponentPortMarksKey();
        if (key == _marksKey) return;
        _marksKey = key;
        int mine = ++_marksGeneration;
        ComponentPortMarksBuilding = BuildComponentPortMarksAsync(mine);
    }

    /// <summary>The setup is closing: nothing in flight may publish, and nothing is shown.</summary>
    public void CancelComponentPortMarks()
    {
        _marksGeneration++;
        _marksKey = null;
        ComponentPortMarks = [];
        ComponentCutMarks = [];
        SolverGeometry = null;
        ComponentPortMarksView = null;
    }

    /// <summary>What the marks depend on: the layout as it stands, and the parts of the setup that move a port.</summary>
    private string ComponentPortMarksKey()
    {
        var view = _componentSource?.View;
        string viewKey = view is null ? "" : $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(view)}/{view.EditCount}";
        // brief-icp-7 — every term of an entry moves a cut (its cell, port-at, box, kept names, pins, ground) as well as a port.
        return viewKey + "#" + ShowComponentPorts + "#" + Is3DSetup + "#" + Working.AnalysisKind + "#" + _solverGeometryWanted + "#"
             + string.Join(";", Working.ComponentPorts.Select(c => $"{c.Refdes}\u001f{c.Reference}\u001f{c.WidthUm?.ToString("R", CultureInfo.InvariantCulture)}"
                                                                 + $"\u001f{c.Cell}\u001f{c.PortAt}\u001f{c.KeepOutUm}\u001f{c.Keep}\u001f{c.Pins}\u001f{c.Ground}\u001f{c.Construction}\u001f{c.Z0}"))
             + "#" + string.Join(",", Working.AnalysisLevelNames) + "#" + Working.SolveRegion?.Describe();
    }

    private async Task BuildComponentPortMarksAsync(int mine)
    {
        if (ComponentPortMarksSettle > TimeSpan.Zero) await Task.Delay(ComponentPortMarksSettle);
        if (mine != _marksGeneration) return;

        EmComponentPortMarksJob? job = null;
        if (ComponentPortsHost is { } host && (ShowComponentPorts || _solverGeometryWanted))
        {
            try { job = host.PrepareMarks(Working, _solverGeometryWanted); }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException) { job = null; }
        }

        var set = EmComponentPortMarkSet.Empty;
        if (job is not null)
        {
            try { set = await Task.Run(job.Build); }
            // A part the build cannot read is Check's to report; the layout simply draws nothing for it.
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException or FormatException) { set = EmComponentPortMarkSet.Empty; }
        }
        if (mine != _marksGeneration) return;

        ComponentPortMarks = set.Ports;
        ComponentCutMarks = set.Cuts;
        SolverGeometry = set.SolverGeometry;
        ComponentPortMarksView = job?.View;
        ComponentPortMarksEditCount = job?.EditCount ?? 0;
        ComponentPortMarksChanged?.Invoke();
    }

    // ── R-lcp3-3: a built component port's row in the Ports list ─────────────────────────────────────────────────────────

    /// <summary>A component port row's Z0 box: blank is the setup's reference impedance for that port.</summary>
    public void CommitComponentPortZ0(EmPortZ0Row row)
    {
        if (row.ComponentRefdes is not { } rd || _suppressCommit) return;
        string text = row.Text.Trim();
        if (EmComponentPort.Z0Refusal(text) is { } why) { row.Error = why; return; }
        row.Error = null;
        SetComponentPortEntry(rd, e => e with { Z0 = text.Length == 0 ? null : text }, $"Component port {rd} Z0");
    }

    /// <summary>A component port row's width box, µm: blank is the width the two pads share.</summary>
    public void CommitComponentPortWidth(EmPortZ0Row row)
    {
        if (row.ComponentRefdes is not { } rd || _suppressCommit) return;
        string text = row.WidthText.Trim();
        double? width = null;
        if (text.Length > 0)
        {
            if (!NumericText.TryParseDouble(text, out double w)) { row.Error = "The width is a number of micrometres, or blank for the width the pads share."; return; }
            width = w;
        }
        if (EmComponentPort.WidthRefusal(width) is { } why) { row.Error = why; return; }
        row.Error = null;
        // The box shows six significant digits: a value that still reads as the stored one is the stored one, so leaving the box
        // does not round a width that was written with more.
        if (width is { } typed && Keep(rd).WidthUm is { } stored &&
            typed.ToString("G6", CultureInfo.InvariantCulture) == stored.ToString("G6", CultureInfo.InvariantCulture))
            width = stored;
        SetComponentPortEntry(rd, e => e with { WidthUm = width }, $"Component port {rd} width");
    }

    /// <summary>A component port row's reference picker: the part's pad that is the port's negative end.</summary>
    private void CommitComponentPortReference(EmPortZ0Row row)
    {
        if (row.ComponentRefdes is not { } rd || _suppressCommit) return;
        string? pin = row.ReferenceChoice?.Pin;
        SetComponentPortEntry(rd, e => e with { Reference = pin }, $"Component port {rd} reference");
    }

    /// <summary>The part's entry changed through <see cref="EmComponentPort.Override"/> (the rule a 3D view's Properties writes
    /// through): one undo entry, none when nothing changed.</summary>
    private void SetComponentPortEntry(string refdes, Func<EmComponentPort, EmComponentPort> change, string description)
    {
        string before = SnapshotJson();
        Working.ComponentPorts = EmComponentPort.Override(Working.ComponentPorts, refdes, change);
        CommitComponentPorts(before, description);
    }

    /// <summary>The Ports list's row for a built component port: named by its part, no type (a component port's is not a
    /// choice), its Z0, width and reference read from the part's entry, blank showing the default as a placeholder.</summary>
    private EmPortZ0Row ComponentPortRow(EmPortRow port, EmPlanarComponentPort built)
    {
        var entry = Keep(built.Refdes);
        var pins = _placedParts.FirstOrDefault(p => p.Refdes.Equals(built.Refdes, StringComparison.OrdinalIgnoreCase))?.Pins ?? [];
        var choices = new List<EmPinChoice> { new(null, "Ground pad") };
        choices.AddRange(pins.Select(p => new EmPinChoice(p, $"Pin {p}")));
        if (entry.Reference is { Length: > 0 } stated && !choices.Any(c => c.Pin == stated))
            choices.Add(new EmPinChoice(stated, $"{stated}  — not a pin of {built.Refdes}"));
        var row = new EmPortZ0Row
        {
            PortNumber = port.Number,
            PortLabel = port.Label,
            Label = $"Port {port.Number} · {built.Name}",
            ComponentRefdes = built.Refdes,
            ShowKind = false,
            Kind = port.Kind,
            Problem = port.Problem,
            Text = entry.Z0 ?? "",
            Z0Placeholder = FormatComplexOhms(Working.ResolvePortZ0(port.Number - 1)),
            WidthText = entry.WidthUm?.ToString("G6", CultureInfo.InvariantCulture) ?? "",
            WidthPlaceholder = built.WidthM > 0 ? (built.WidthM * 1e6).ToString("0.#", CultureInfo.InvariantCulture) : "pads",
            ShowReference = pins.Count > 2 || entry.Reference is { Length: > 0 },
            ReferenceChoices = choices,
            ReferenceChoice = choices.FirstOrDefault(c => c.Pin == entry.Reference) ?? choices[0],
        };
        // Wired after construction: seeding the picker from the entry must not commit.
        row.ReferenceChanged += CommitComponentPortReference;
        return row;
    }
}

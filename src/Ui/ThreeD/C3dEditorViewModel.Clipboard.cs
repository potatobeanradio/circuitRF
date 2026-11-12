// brief-em3d-95 — Copy and Paste of the object tree's rows, between .c3d documents and workspaces.
//
// NO DECISION LIVES HERE. What a copy carries and what a paste means are C3dFragment's (src/Design); this file maps rows to
// a C3dCopySelection, carries the text to and from the view (which owns the system clipboard, through C3dClipboard), and
// wraps the paste in ONE undo entry — a document edit (EditNames), since a paste changes objects, VARs, ports, boundaries,
// thermal places and a setup together, exactly the shape a VAR rename already has. Materials a paste creates are another
// document's edit and are committed by the workspace (the Materials dialog's CommitMaterialList) before the paste.

using Avalonia.Input;
using CircuitRF.Design.ThreeD;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.ThreeD;

/// <summary>A paste the user must answer: the fragment and what the Paste dialog asks.</summary>
public sealed record C3dPasteRequest(C3dFragment.Payload Payload, C3dPastePlan Plan);

public sealed partial class C3dEditorViewModel
{
    /// <summary>The menu item's header in the tree (the canvas's is <see cref="CopyObjectsHeader"/>: its Copy is the picture).</summary>
    public const string CopyHeader = "Copy", PasteHeader = "Paste", CopyObjectsHeader = "Copy Objects";

    /// <summary>Why Paste is disabled when the clipboard holds no 3D copy.</summary>
    public const string NothingCopied = "Nothing copied from a 3D view";

    /// <summary>The system clipboard's text as the view last read it (before a menu opens, and at a paste), or what this view
    /// last copied. The view model never reads the clipboard itself.</summary>
    public string? ClipboardText { get; set; }

    /// <summary>A copy was made: the view puts the text on the system clipboard.</summary>
    public event Action<string>? ClipboardWriteRequested;

    /// <summary>A paste needs the Paste dialog: the workspace shows it, creates the ticked materials, then calls
    /// <see cref="CommitPaste"/>.</summary>
    public event Action<C3dPasteRequest>? PasteDialogRequested;

    /// <summary>A paste's report, line by line, after it landed: the workspace posts it to Messages.</summary>
    public event Action<IReadOnlyList<string>>? PasteReported;

    /// <summary>The last paste's result (the gate reads it).</summary>
    public C3dPasteResult? LastPaste { get; private set; }

    // ── Copy ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>What <paramref name="rows"/> copy, or why they cannot: every row that can is taken, and the first that cannot
    /// says why only when none can.</summary>
    public (C3dCopySelection? Selection, string? Refusal) CopySelectionOf(IReadOnlyList<C3dTreeItem> rows)
    {
        var objects = new List<int>();
        var instances = new List<int>();
        var ports = new List<int>();
        var faces = new List<int>();
        var places = new List<string>();
        var planes = new List<C3dAxis>();
        var thermal = new List<string>();
        string? refusal = null;
        foreach (var row in rows)
        {
            if (CopyRefusal(row) is { } why) { refusal ??= why; continue; }
            if (row.IsGroup)
                foreach (var m in C3dGroups.MembersOf(Document, row.GroupPath!))
                    (m.Instance ? instances : objects).Add(m.Index);
            else if (row.Kind is "Port" or TerminalKind && PortOfRow(row) is { } rowPort && Document.Ports.IndexOf(rowPort) is >= 0 and var pi) ports.Add(pi);
            else if (row.Kind == EmBoundaryKind &&
                     Document.FaceBoundaries.FindIndex(b => Scene3DBuilder.FaceTintPrefix + b.Object + "/" + b.Face == row.Name) is >= 0 and var bi) faces.Add(bi);
            else if (row.Kind is HeatSourceKind or ProbeKind or MeshRegionKind or EffectiveBlockKind) places.Add(row.Name);
            else if (row.Kind == SymmetryPlaneKind && Enum.TryParse<C3dAxis>(row.Name[SymmetryRowPrefix.Length..], out var axis)) planes.Add(axis);
            else if (row.Kind == ThermalBoundaryKindName) thermal.Add(row.Name[ThermalTintPrefix.Length..]);
            else if (row.ObjectIndex >= 0 && row.ObjectIndex < Document.Objects.Count) objects.Add(row.ObjectIndex);   // an operand's or a feature's row: its object
            else if (row.InstanceIndex >= 0 && row.InstanceIndex < Document.Instances.Count) instances.Add(row.InstanceIndex);
        }
        var selection = new C3dCopySelection
        {
            Objects = [.. objects.Distinct()], Instances = [.. instances.Distinct()], Ports = [.. ports.Distinct()],
            FaceBoundaries = [.. faces.Distinct()], Places = [.. places.Distinct()], SymmetryPlanes = [.. planes.Distinct()],
            ThermalBoundaries = [.. thermal.Distinct()],
        };
        return selection.IsEmpty ? (null, refusal ?? "Select something to copy.") : (selection, null);
    }

    /// <summary>Why a row does not copy, or null when it does (brief-em3d-95 §1's "not copyable yet").</summary>
    private static string? CopyRefusal(C3dTreeItem row) => row switch
    {
        { IsAirBox: true } => "The air box is the active setup's, not the geometry's: it does not copy.",
        { Kind: FieldPlotKind } => "A field plot is a reading of a run (it names a setup and a solution): it does not copy yet.",
        { Kind: "Part" } => "Part of a placed cell: it belongs to its own cell. Copy the placed cell instead.",
        { Kind: LumpedElementKind } => "A lumped element does not copy yet: draw another with the Lumped Element tool.",
        _ => null,
    };

    /// <summary>Copies <paramref name="rows"/>: the fragment goes to the system clipboard (through the view). Null, or why not.</summary>
    public string? CopyRows(IReadOnlyList<C3dTreeItem> rows)
    {
        if (IsViewOnly) return "A setup's view shows the design; copy from the design itself.";
        var (selection, refusal) = CopySelectionOf(rows);
        if (selection is null) return refusal;
        string? cws = _workspaceCws();
        var payload = C3dFragment.Build(Document, Elaboration?.Technology, selection,
            new C3dCopyContext(FilePath, cws is null ? null : Path.GetDirectoryName(cws), Cell, ActiveSetupName));
        string text = C3dFragment.Serialize(payload);
        ClipboardText = text;
        ClipboardWriteRequested?.Invoke(text);
        StatusMessage = $"Copied {C3dFragment.Describe(payload)}.";
        return null;
    }

    /// <summary>Ctrl/Cmd+C in the tree, and the canvas's Copy Objects: the tree's rows, else the view's objects' rows.</summary>
    public string? CopySelection() => CopyRows(SelectionVisibilityRows());

    /// <summary>The tree's Copy item for <paramref name="rows"/>: disabled with the reason when none of them copies.</summary>
    public Viewer3DMenuItem CopyItem(IReadOnlyList<C3dTreeItem> rows, string header = CopyHeader)
    {
        var (selection, refusal) = IsViewOnly ? (null, "A setup's view shows the design; copy from the design itself.") : CopySelectionOf(rows);
        return new Viewer3DMenuItem(header, () => Said(CopyRows(rows)), Enabled: selection is not null,
            Gesture: header == CopyHeader ? Viewer3DMenuItem.Command(Key.C) : null,
            Tip: selection is not null ? "The rows, their materials, the variables they name and their references — to paste into this or another 3D view."
                                       : refusal);
    }

    /// <summary>IViewer3DEditHost — the canvas's Copy Objects, on the view's selection mapped to its rows.</summary>
    public Viewer3DMenuItem? CopyObjectsItem() => IsViewOnly ? null : CopyItem(SelectionVisibilityRows(), CopyObjectsHeader);

    // ── Paste ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The tree's Paste item: enabled when <see cref="ClipboardText"/> holds a 3D copy.</summary>
    public Viewer3DMenuItem PasteItem()
    {
        C3dFragment.Payload? payload = null;
        bool ok = !IsViewOnly && C3dFragment.TryDeserialize(ClipboardText, out payload);
        return new Viewer3DMenuItem(PasteHeader, () => Paste(ClipboardText), Enabled: ok, Gesture: Viewer3DMenuItem.Command(Key.V),
            Tip: ok ? $"Paste {C3dFragment.Describe(payload!)} where it was copied from, renamed where a name is taken."
                    : IsViewOnly ? "A setup's view shows the design; paste into the design itself." : NothingCopied);
    }

    /// <summary>The target's paste context: its path, workspace, cell and active setup.</summary>
    private C3dPasteContext PasteContext()
    {
        string? cws = _workspaceCws();
        return new C3dPasteContext(FilePath, cws is null ? null : Path.GetDirectoryName(cws), Cell, ActiveSetupName);
    }

    /// <summary>
    /// Ctrl/Cmd+V in the tree, and its Paste item: <paramref name="text"/> planned against this document — straight in when
    /// nothing needs asking, else the Paste dialog (<see cref="PasteDialogRequested"/>). Null, or why nothing happened.
    /// </summary>
    public string? Paste(string? text)
    {
        if (IsViewOnly) return "A setup's view shows the design; paste into the design itself.";
        if (!C3dFragment.TryDeserialize(text, out var payload)) return Said(NothingCopied + ".");
        var plan = C3dFragment.Plan(Document, Elaboration?.Technology, PasteContext(), payload!);
        if (!plan.NeedsDialog || PasteDialogRequested is null) return Said(CommitPaste(payload!, plan.Defaults()));
        PasteDialogRequested.Invoke(new C3dPasteRequest(payload!, plan));
        return null;
    }

    /// <summary>The plan for <paramref name="payload"/> against <paramref name="tech"/> (a technology just chosen, before the
    /// elaboration has taken it up); the view's own technology when null.</summary>
    public C3dPastePlan PlanPaste(C3dFragment.Payload payload, Technology? tech = null)
        => C3dFragment.Plan(Document, tech ?? Elaboration?.Technology, PasteContext(), payload);

    /// <summary>Whether this 3D view already has <paramref name="name"/>: a VAR, or its cell's parameter.</summary>
    public bool VariableNameTaken(string name) => Document.Variables.Any(v => v.Name == name) || Cell.Parameter(name) is not null;

    /// <summary>What <see cref="CommitPaste"/> would refuse with <paramref name="choices"/> (a cycle a Reuse closes), asked
    /// before anything is created: the dialog stays open on it.</summary>
    public string? PasteRefusal(C3dFragment.Payload payload, C3dPasteChoices choices)
        => C3dFragment.Apply(C3dPersistence.Deserialize(C3dPersistence.Serialize(Document)), Elaboration?.Technology, PasteContext(), payload, choices).Refusal;

    /// <summary>
    /// Pastes <paramref name="payload"/> with <paramref name="choices"/>: ONE undo entry (a document edit), then the pasted rows
    /// are the tree's selection and the scene's. Null, or the refusal (nothing changed).
    /// </summary>
    public string? CommitPaste(C3dFragment.Payload payload, C3dPasteChoices choices)
    {
        C3dPasteResult? result = null;
        var tech = Elaboration?.Technology;
        var context = PasteContext();
        string? refusal = EditNames($"Paste {C3dFragment.Describe(payload)}", (copy, _) =>
        {
            result = C3dFragment.Apply(copy, tech, context, payload, choices);
            return result.Refusal;
        });
        if (refusal is not null) return refusal;
        if (result is null) return null;
        LastPaste = result;
        // brief-em3d-95 D2 — a plane that does not lie on the model's extent as pasted still pastes, and says so
        if (result.SymmetryPlanes.Count > 0 && Elaboration is { } e) result.Report.AddRange(C3dFragment.PlanesOffExtent(e, Document, result.SymmetryPlanes));
        SelectPasted(result);
        StatusMessage = result.Summary + ".";
        PasteReported?.Invoke(result.Report);
        return null;
    }

    /// <summary>The pasted rows, selected in the tree and the scene, their sections expanded to show them.</summary>
    private void SelectPasted(C3dPasteResult r)
    {
        var names = r.Objects.Concat(r.Instances).Concat(r.Places).ToHashSet(StringComparer.Ordinal);
        var ports = r.Ports.Select(C3dPorts.ProblemName)
                     .Concat(r.Ports.Select(n => PortGroupRowPrefix + n.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                     .ToHashSet(StringComparer.Ordinal);
        var rows = AllTreeItems().Where(t => !t.IsGroup && t.OperandPath is null && !t.IsFeature && t.Kind != "Part" &&
                                             ((t.Kind == "Port" ? ports : names).Contains(t.Name) ||
                                              (t.Kind == SymmetryPlaneKind && r.SymmetryPlanes.Any(a => t.Name == SymmetryRowName(a))) ||
                                              (t.Kind == ThermalBoundaryKindName && r.ThermalBoundaries.Contains(t.Name[ThermalTintPrefix.Length..])) ||
                                              (t.Kind == EmBoundaryKind && r.FaceBoundaries.Any(f => t.Name == Scene3DBuilder.FaceTintPrefix + f))))
                                 .Distinct().ToList();
        if (rows.Count == 0) return;
        _syncingTree = true;
        try
        {
            SetTreeRows(CollapseToGroups(rows));
            if (Viewer.SelectMode != Scene3DSelectMode.Object) Viewer.SelectMode = Scene3DSelectMode.Object;
            Viewer.SetSelection(rows.SelectMany(SceneObjectsOfSelectedRow).Select(s => Scene3DItem.OfObject(s.Id)));
        }
        finally { _syncingTree = false; }
        foreach (var row in rows) TreeRevealRequested?.Invoke(row);
        Properties.Reload();
    }

    /// <summary>A refusal to the status line, and back to the caller.</summary>
    private string? Said(string? message)
    {
        Report(message);
        return message;
    }
}

// brief-em3d-43 R-em3d43-1c — one undo entry of the 3D editor.
//
// AN ENTRY STORES ONLY WHAT IT CHANGED: each touched object (or instance) as the file spells it, before and
// after — never a snapshot of the document, so a 10,000-object document does not copy itself per edit. The
// spelling is C3dPersistence's own, which is also the elaborator's per-object cache key: an undone object
// is byte for byte what it was, and its elaboration is a cache hit.
//
// ONE SHAPE PER ENTRY — replacements, or removals, or insertions — so an index always means one thing:
// a replacement's and a removal's index is into the list BEFORE the entry, an insertion's into the list
// AFTER it. Removals run from the highest index down and insertions from the lowest up, which is what
// makes deleting three objects and undoing it put each back where it was.

using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Commands;

namespace CircuitRF.Ui.ThreeD;

/// <summary>One touched slot: an object (or, with <see cref="Instance"/>, an instance) at <see cref="Index"/>,
/// as the file spells it before and after; null before is an insertion, null after a removal.</summary>
public sealed record C3dEditSlot(bool Instance, int Index, string? Before, string? After);

/// <summary>
/// brief-em3d-101 R-em3d101-1b — an undo entry holding objects as the file spells them. An image's path is spelled for the
/// <c>.c3d</c>'s folder, so Save As into another folder rewrites the paths the history holds as well as the document's
/// (<see cref="C3dEditorViewModel.SaveAs"/>): otherwise undoing past the save would put back a path spelled for the old folder.
/// Rewritten in place, so the stack's saved marker (a reference) still means the same entry.
/// </summary>
public interface IC3dObjectTextEntry
{
    /// <summary>Each object's text through <paramref name="objectText"/>, and each whole document's through
    /// <paramref name="documentText"/>.</summary>
    void RewriteObjects(Func<string, string> objectText, Func<string, string> documentText);
}

public sealed class C3dEdit : IUiCommand, IC3dObjectTextEntry
{
    private readonly Action<IReadOnlyList<C3dEditSlot>, bool> _apply;
    private readonly Action<string>? _setBoundaries;
    private bool _alreadyApplied;

    /// <summary>brief-em3d-47 R-em3d47-3c — the document's face references before and after, when a fold renamed a face
    /// something was attached to; null when the entry leaves them alone. Since brief-em3d-86 the text is
    /// <c>C3dPersistence.SerializeFaceReferences</c>': the EM <c>FaceBoundaries</c> and the thermal setups, probes and field
    /// plots, which follow a fold in the same entry.</summary>
    public (string Before, string After)? FaceBoundaries { get; }

    public string Description { get; }
    public IReadOnlyList<C3dEditSlot> Slots { get; private set; }

    public void RewriteObjects(Func<string, string> objectText, Func<string, string> documentText)
        => Slots = [.. Slots.Select(s => s.Instance ? s : s with
           {
               Before = s.Before is null ? null : objectText(s.Before),
               After = s.After is null ? null : objectText(s.After),
           })];

    /// <summary>brief-em3d-51 — the document already holds the after state (a gesture's release), until the first Execute.</summary>
    public bool AlreadyApplied => _alreadyApplied;

    /// <param name="apply">Writes the slots into the document: forward (after) or back (before).</param>
    /// <param name="alreadyApplied">The document already holds the after state (a gesture committed on
    /// release, R-em3d43-1c): the stack's first Execute does nothing, and every Redo after it applies.</param>
    /// <param name="faceBoundaries">The <c>FaceBoundaries</c> list before and after, written by <paramref name="setBoundaries"/>
    /// ahead of the slots, forward and back.</param>
    public C3dEdit(string description, IReadOnlyList<C3dEditSlot> slots, Action<IReadOnlyList<C3dEditSlot>, bool> apply,
                   bool alreadyApplied = false, (string Before, string After)? faceBoundaries = null, Action<string>? setBoundaries = null)
    {
        bool replace = slots.All(s => s.Before is not null && s.After is not null);
        bool remove = slots.All(s => s.Before is not null && s.After is null);
        bool insert = slots.All(s => s.Before is null && s.After is not null);
        if (!(replace || remove || insert))
            throw new ArgumentException("A 3D edit replaces, removes or inserts — one of the three.", nameof(slots));
        Description = description;
        Slots = slots;
        _apply = apply;
        _alreadyApplied = alreadyApplied;
        FaceBoundaries = faceBoundaries;
        _setBoundaries = setBoundaries;
    }

    public void Execute()
    {
        if (_alreadyApplied) { _alreadyApplied = false; return; }
        if (FaceBoundaries is { } b) _setBoundaries?.Invoke(b.After);
        _apply(Slots, true);
    }

    public void Undo()
    {
        if (FaceBoundaries is { } b) _setBoundaries?.Invoke(b.Before);
        _apply(Slots, false);
    }

    /// <summary>Writes <paramref name="slots"/> into <paramref name="doc"/>, forward or back.</summary>
    public static void Apply(C3dDocument doc, IReadOnlyList<C3dEditSlot> slots, bool forward)
    {
        foreach (bool instances in new[] { false, true })
        {
            var mine = slots.Where(s => s.Instance == instances).ToList();
            if (mine.Count == 0) continue;
            foreach (var s in mine.Where(s => (forward ? s.After : s.Before) is null).OrderByDescending(s => s.Index))
                if (instances) doc.Instances.RemoveAt(s.Index); else doc.Objects.RemoveAt(s.Index);
            foreach (var s in mine.OrderBy(s => s.Index))
            {
                string? now = forward ? s.After : s.Before;
                string? was = forward ? s.Before : s.After;
                if (now is null) continue;
                if (instances)
                {
                    var inst = C3dPersistence.DeserializeInstance(now);
                    if (was is null) doc.Instances.Insert(s.Index, inst); else doc.Instances[s.Index] = inst;
                }
                else
                {
                    var obj = C3dPersistence.DeserializeObject(now);
                    if (was is null) doc.Objects.Insert(s.Index, obj); else doc.Objects[s.Index] = obj;
                }
            }
        }
    }
}

/// <summary>
/// brief-em3d-48 — ONE undo entry for a hierarchy edit that removes some objects or instances and inserts others (Flatten,
/// Group into Cell), which <see cref="C3dEdit"/>'s one-shape rule cannot state. It keeps the two lists as the file spells
/// them, before and after — a whole-list copy, which is acceptable for an edit this rare and this large, and exact: an
/// undone object is byte for byte what it was, so its elaboration is a cache hit.
/// </summary>
public sealed class C3dListsEdit(string description, (IReadOnlyList<string> Objects, IReadOnlyList<string> Instances) before,
                                 (IReadOnlyList<string> Objects, IReadOnlyList<string> Instances) after,
                                 Action<IReadOnlyList<string>, IReadOnlyList<string>> apply) : IUiCommand, IC3dObjectTextEntry
{
    public string Description { get; } = description;
    public (IReadOnlyList<string> Objects, IReadOnlyList<string> Instances) Before { get; private set; } = before;
    public (IReadOnlyList<string> Objects, IReadOnlyList<string> Instances) After { get; private set; } = after;

    public void RewriteObjects(Func<string, string> objectText, Func<string, string> documentText)
    {
        Before = ([.. Before.Objects.Select(objectText)], Before.Instances);
        After = ([.. After.Objects.Select(objectText)], After.Instances);
    }

    public void Execute() => apply(After.Objects, After.Instances);
    public void Undo() => apply(Before.Objects, Before.Instances);

    /// <summary>The document's two lists as the file spells them.</summary>
    public static (IReadOnlyList<string> Objects, IReadOnlyList<string> Instances) Of(C3dDocument doc)
        => ([.. doc.Objects.Select(C3dPersistence.SerializeObject)], [.. doc.Instances.Select(C3dPersistence.SerializeInstance)]);

    /// <summary>Writes the two lists into <paramref name="doc"/>.</summary>
    public static void Apply(C3dDocument doc, IReadOnlyList<string> objects, IReadOnlyList<string> instances)
    {
        doc.Objects = [.. objects.Select(C3dPersistence.DeserializeObject)];
        doc.Instances = [.. instances.Select(C3dPersistence.DeserializeInstance)];
    }
}

/// <summary>
/// brief-em3d-49 — ONE undo entry for an edit of the document's records: its ports, its face boundaries and its embedded
/// setups, as the file spells the three lists before and after. They are small and edited together (a setup's air box and
/// the ports measured against it), so the entry keeps the three whole — exact, and an undone list is byte for byte what
/// it was.
/// </summary>
public sealed class C3dRecordsEdit(string description, string before, string after, Action<string> apply, bool alreadyApplied = false) : IUiCommand
{
    private bool _alreadyApplied = alreadyApplied;

    public string Description { get; } = description;
    public string Before { get; } = before;
    public string After { get; } = after;

    public void Execute()
    {
        if (_alreadyApplied) { _alreadyApplied = false; return; }
        apply(After);
    }

    public void Undo() => apply(Before);

    /// <summary>The records as one text: ports, face boundaries, setups — and (3D editor round 3) the air box's material,
    /// (round 5) whether it is hidden, and (brief-em3d-75) the thermal places: heat sources, probes, mesh regions and contact
    /// overrides, which are records beside the ports, never objects — and (brief-em3d-83) the field plots, and (brief-em3d-106) the
    /// realistic view's Look — and (brief-sar-3) the lumped elements, (brief-sar-12) the band sweeps and the document's own air box,
    /// as a document holding nothing else spells them.</summary>
    public static string Of(C3dDocument doc)
        => C3dPersistence.SerializePorts(doc.Ports) + "\u0001" + C3dPersistence.SerializeFaceBoundaries(doc.FaceBoundaries) +
           "\u0001" + C3dPersistence.SerializeSetups(doc.Setups) + "\u0001" + (doc.AirBoxMaterial ?? "") +
           "\u0001" + (doc.AirBoxHidden ? "hidden" : "") + "\u0001" + C3dPersistence.SerializeThermalPlaces(doc) +
           "\u0001" + C3dPersistence.SerializeFieldPlots(doc.FieldPlots) + "\u0001" + C3dPersistence.SerializeLook(doc.Look) +
           "\u0001" + C3dPersistence.Serialize(new C3dDocument { DbuPerMicron = doc.DbuPerMicron, LumpedElements = doc.LumpedElements,
                                                                BandSweeps = doc.BandSweeps, AirBox = doc.AirBox });

    /// <summary>Whether <paramref name="text"/> (an <see cref="Of"/>) holds the air box hidden.</summary>
    public static bool AirBoxHiddenOf(string text) => text.Split('\u0001') is { Length: > 4 } parts && parts[4].Length > 0;

    /// <summary>Writes the three lists of <paramref name="text"/> into <paramref name="doc"/>.</summary>
    public static void Apply(C3dDocument doc, string text)
    {
        var parts = text.Split('\u0001');
        doc.Ports = C3dPersistence.DeserializePorts(parts[0]);
        doc.FaceBoundaries = C3dPersistence.DeserializeFaceBoundaries(parts[1]);
        doc.Setups = C3dPersistence.DeserializeSetups(parts[2]);
        doc.AirBoxMaterial = parts.Length > 3 && parts[3].Length > 0 ? parts[3] : null;
        doc.AirBoxHidden = parts.Length > 4 && parts[4].Length > 0;
        if (parts.Length > 5) C3dPersistence.ApplyThermalPlaces(doc, parts[5]);
        // brief-em3d-83 — the field plots are records too: every plot edit is one entry.
        if (parts.Length > 6) doc.FieldPlots = C3dPersistence.DeserializeFieldPlots(parts[6]);
        // brief-em3d-106 — the Look is a record too: an edit of it (brief 108's panel) is one entry.
        if (parts.Length > 7) doc.Look = C3dPersistence.DeserializeLook(parts[7]);
        // brief-sar-3 — the lumped elements are records too: adding, renaming or editing one is one entry.
        // brief-sar-12 — and the band sweeps: saving one (with the setups it makes) is one entry.
        if (parts.Length > 8)
        {
            var records = C3dPersistence.Deserialize(parts[8]);
            doc.LumpedElements = records.LumpedElements;
            doc.BandSweeps = records.BandSweeps;
            // The air box while no setup is active: sized and walled before any setup exists, one entry per edit.
            doc.AirBox = records.AirBox;
        }
    }
}

/// <summary>
/// brief-em3d-51 — ONE undo entry for an edit of the document's NAMES: its VARs, the fields a rename rewrote, the objects a
/// drag moved through a name — and, when a cell parameter is written (a linked VAR's value, Promote, a drag of a
/// parameter), the cell's <c>.ccell</c>. The document is kept whole, as the file spells it, before and after; the
/// <c>.ccell</c> as its text. A name edit touches the whole document by construction (every field that uses the name), and
/// it is rare, so the copy is the exact choice rather than the cheap one.
/// </summary>
public sealed class C3dDocumentEdit(string description, string before, string after, string? ccellPath, string? ccellBefore,
                                    string? ccellAfter, Action<string, string?, string?> apply, bool alreadyApplied = false)
    : IUiCommand, IC3dObjectTextEntry
{
    private bool _alreadyApplied = alreadyApplied;

    public string Description { get; } = description;
    public string Before { get; private set; } = before;
    public string After { get; private set; } = after;

    public void RewriteObjects(Func<string, string> objectText, Func<string, string> documentText)
        => (Before, After) = (documentText(Before), documentText(After));
    public string? CcellPath { get; } = ccellPath;

    public void Execute()
    {
        if (_alreadyApplied) { _alreadyApplied = false; return; }
        apply(After, CcellPath, ccellAfter);
    }

    public void Undo() => apply(Before, CcellPath, ccellBefore);
}

/// <summary>brief-em3d-51 R-em3d51-3c — entries that happened as one user action (a Define strip's definitions and the
/// object the step then drew), undone and redone together. Each was applied as it was pushed.</summary>
public sealed class C3dGroupEdit(string description, IReadOnlyList<IUiCommand> parts) : IUiCommand, IC3dObjectTextEntry
{
    private bool _applied = true;

    public string Description { get; } = description;
    public IReadOnlyList<IUiCommand> Parts { get; } = parts;

    public void RewriteObjects(Func<string, string> objectText, Func<string, string> documentText)
    {
        foreach (var p in Parts.OfType<IC3dObjectTextEntry>()) p.RewriteObjects(objectText, documentText);
    }

    public void Execute()
    {
        if (_applied) { _applied = false; return; }
        foreach (var p in Parts) p.Execute();
    }

    public void Undo()
    {
        for (int i = Parts.Count - 1; i >= 0; i--) Parts[i].Undo();
    }
}

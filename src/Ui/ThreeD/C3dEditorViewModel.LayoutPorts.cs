// brief-port-width-planar-and-c3d R-pw1-4/5 — a .c3d keeps up with its placed layouts' ports.
//
// A .c3d's ports are its own records (a placed cell's port shapes are never read), made once when the view was created
// from the layout. A Width or "Use edge width" edit on the layout afterwards changes what a 3D run of the layout builds and
// not what the .c3d has. So: on open and whenever a placed layout changes, every port label is translated again in
// memory (C3dHierarchy.PortsFromLayouts, the creation path's own translation) and a record whose rectangle now differs is
// REPORTED, once per difference — never rewritten, since a .c3d port may have been edited by hand on purpose. Update
// Ports from Layout is the one undoable write.

using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>The placed layouts' stamps when their ports were last checked; a check runs again only when one moves.</summary>
    private string? _layoutPortStamp;

    /// <summary>The stale-port messages already posted, so one is said once while it stands.</summary>
    private HashSet<string> _postedStalePorts = new(StringComparer.Ordinal);

    /// <summary>R-pw1-4 — the stale ports found by the last check (for the menu state and the tests).</summary>
    public IReadOnlyList<C3dStalePort> StaleLayoutPorts { get; private set; } = [];

    /// <summary>Raised when a check has finished (tests wait on it).</summary>
    public event Action? LayoutPortsChecked;

    private string LayoutPortStamp()
    {
        string baseDir = Path.GetDirectoryName(FilePath)!;
        var parts = new List<string>();
        foreach (var inst in Document.Instances.Where(i => i.View == C3dInstanceView.Layout))
            if (CircuitRF.Design.Workspace.ExternalCellRef.ResolveCellDir(inst.CellRef, baseDir) is { } dir &&
                C3dHierarchy.ViewFile(dir, C3dInstanceView.Layout) is { } clay)
                parts.Add($"{inst.Name}@{inst.Placement.Origin}:{clay}={Stamp(clay) ?? "-"}");
        return string.Join("|", parts);
    }

    /// <summary>R-pw1-4 — translate every placed layout's port labels again, off the UI thread, when a layout has changed
    /// since the last check; then report each record that no longer matches.</summary>
    private void CheckLayoutPorts()
    {
        if (CanPopOut || Document.Ports.Count == 0) return;
        string stamp = LayoutPortStamp();
        if (stamp.Length == 0 || stamp == _layoutPortStamp) return;
        _layoutPortStamp = stamp;
        var shallow = new C3dDocument { Instances = [.. Document.Instances] };
        string path = FilePath;
        string? cws = _workspaceCws();
        // A cache of its own: this runs off the UI thread, beside the elaborator's.
        Task.Run(() =>
        {
            try { return C3dHierarchy.PortsFromLayouts(shallow, path, cws, null); }
            catch (Exception) { return null; }
        }).ContinueWith(t => _post(() => ReportStalePorts(t.Result)), TaskScheduler.Default);
    }

    private void ReportStalePorts(C3dLayoutPorts? translated)
    {
        StaleLayoutPorts = translated is null ? [] : C3dHierarchy.StalePorts(Document, translated);
        var now = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in StaleLayoutPorts)
        {
            string text = StalePortSentence(s);
            now.Add(text);
            if (!_postedStalePorts.Contains(text)) PostMessage?.Invoke(text, true);
        }
        _postedStalePorts = now;
        LayoutPortsChecked?.Invoke();
    }

    private string StalePortSentence(C3dStalePort s)
    {
        string Len(long dbu) => LayoutUnits.Format(dbu, DisplayUnit, Document.DbuPerMicron) + " " + LayoutUnits.Suffix(DisplayUnit);
        string widths = s.OldWidthDbu == s.NewWidthDbu
            ? $"both {Len(s.OldWidthDbu)} wide, in a different place"
            : $"{Len(s.OldWidthDbu)} wide here and {Len(s.NewWidthDbu)} as the layout builds it now";
        return $"Port {s.Number} of '{Path.GetFileName(FilePath)}' no longer matches its layout's port label: {widths}. " +
               "Update Ports from Layout (an instance's right-click menu) replaces it; nothing is changed until then.";
    }

    /// <summary>
    /// R-pw1-5 — Update Ports from Layout (<see cref="C3dHierarchy.UpdatePorts"/>), as one undo entry: each record that
    /// stands for a placed layout's label takes its sheet; a label nothing stands for is added, at the next free number when
    /// its own is taken (a second placed layout's port 1), and the renumbering is said in the Messages panel. Records no
    /// label stands for are left alone. A layout no 3D setup names is translated with a default driven setup (R-pw1-6).
    /// </summary>
    public string UpdatePortsFromLayout()
    {
        var translated = C3dHierarchy.PortsFromLayouts(Document, FilePath, _workspaceCws(), _technologies);
        C3dPortUpdate? update = null;
        ChangeRecords("Update ports from layout", d => update = C3dHierarchy.UpdatePorts(d, translated));
        foreach (string note in translated.Notes) PostMessage?.Invoke(note, false);
        if (translated.LeftOut.Count > 0)
            PostMessage?.Invoke("Ports left out (each must be one rectangle to become a port here): " +
                                string.Join("; ", translated.LeftOut) + ".", true);
        _layoutPortStamp = null;
        _postedStalePorts.Clear();
        StaleLayoutPorts = [];
        string said = update!.Describe();
        if (update.Added.Any(a => a.Number != a.From.LayoutNumber)) PostMessage?.Invoke(said, false);
        StatusMessage = said;
        return said;
    }
}

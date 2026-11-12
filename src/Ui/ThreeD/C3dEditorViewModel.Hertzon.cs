// HZN-9 R-hzn9-2 — placing a design of a Hertzon project (.hzn) in this 3D view: the same armed placement a cell's is
// (outline following the cursor, one click, one undo entry), refused at the pick — before anything is written — for a
// design that cannot be placed (brief 2's list) or whose materials this view's technology does not state.

using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.ThreeD.Hierarchy;
using CircuitRF.Ui.ThreeD.Hzn;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>
    /// HZN-7 — the path a RUN of this document is named and filed by. A native <c>.c3d</c> is its own file; a design held in a
    /// project has none, so a run is named by the same virtual path <c>em design.hzn</c> uses (<c>&lt;project&gt;.circuitrf/&lt;design&gt;.c3d</c>,
    /// never created). Two designs of one project then never share a run folder, and the editor and the command line agree on where a result is.
    /// </summary>
    public string RunPath => Source is HznDesignSource held && !CanPopOut ? HznEmergeRun.DocumentPath(held.FilePath, held.Design) : TopFilePath;

    /// <summary>A fresh elaborator on this editor's technology cache, for a run on another thread (the scene's own is locked while it builds).
    /// A held design's materials are in that cache for as long as its tab is open.</summary>
    public C3dElaborator NewRunElaborator() => new(_technologies, _kernel);

    /// <summary>
    /// Arms placement of design <paramref name="design"/> of the project at <paramref name="hznPath"/> (referenced as
    /// <paramref name="cellRef"/> from this document). Returns the refusal, or null when armed.
    /// </summary>
    public string? BeginHertzonPlacement(string cellRef, string design, string hznPath)
    {
        string name = $"{Path.GetFileName(hznPath)} · {design}";
        var template = new C3dInstance { Name = C3dHierarchy.NextInstanceName(Document), CellRef = cellRef, View = C3dInstanceView.Hertzon, Design = design };

        var probe = new C3dDocument { DbuPerMicron = Document.DbuPerMicron, TechRef = Document.TechRef, Instances = [template] };
        C3dElaboration e;
        lock (_elaborating) e = _elaborator.Elaborate(probe, FilePath, _workspaceCws());
        if (!e.Ok && e.Solids.Count + e.Sheets.Count == 0)
        {
            string why = e.Refusals.Count > 0 ? e.Refusals[0] : $"'{name}' elaborates to nothing.";
            StatusMessage = why;
            return why;
        }
        if (EncodingRefusal($"'{name}'", e.Solids.Count + e.Sheets.Count, 1) is { } limit) { StatusMessage = limit; return limit; }
        var ext = e.Extent() ?? (0, 0, 0, 0, 0, 0);
        double per = C3dLowering.Metres(1, Document.DbuPerMicron);
        long Lo(double m) => (long)Math.Floor(m / per + 1e-9);
        long Hi(double m) => (long)Math.Ceiling(m / per - 1e-9);
        SetTool(new PlaceInstanceTool(this, template, name, new C3dPoint3(Lo(ext.X0), Lo(ext.Y0), Lo(ext.Z0)),
                                      new C3dPoint3(Hi(ext.X1), Hi(ext.Y1), Hi(ext.Z1))));
        StatusMessage = "";
        return null;
    }
}

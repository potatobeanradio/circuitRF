// PWL-4 — the wall port in the 3D editor. Make Port ▸ Wall on a face of the air box (or a face or sheet lying on one) writes a
// wall port per line ending there (C3dWallPorts.Propose); Port Kind ▸ Wave / Wall switches a face's ports together, since one
// face carries one kind (C3dWallPorts.SwitchFace). Both are the src/Design functions, so what the menu offers is what the run
// accepts, and the item is disabled with the resolver's own refusal as its tip otherwise.

using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    private const string WallTip =
        "A wall port feeds each line where it ends on this face of the air box: the face becomes a PEC wall for every setup that runs " +
        "it, and line standards the run solves first calibrate the gap and the wall out.";

    /// <summary>The port context a set of ports is judged in, with the box the active setup (or the view's own) gives them: a wall
    /// port's face is forced PEC with no padding, so the box depends on the ports.</summary>
    internal C3dPortContext PortContextWith(C3dDocument ports)
    {
        if (Elaboration is not { } e) return CurrentPortContext();
        var boxSetup = ActiveSetup ?? NoSetupBasis?.Setup;
        var box = boxSetup is not null ? C3dProblemAssembly.AirBox(boxSetup, e, out _, ports) : null;
        return C3dProblemAssembly.PortContext(ActiveSetup, ports, e, box);
    }

    /// <summary>The air box's x or y face at <paramref name="offsetDbu"/> on <paramref name="axis"/>, as metres, or null when that plane
    /// is not one of its faces.</summary>
    private double? BoxFaceAt(int axis, long offsetDbu)
    {
        if (axis is not (0 or 1) || ShownAirBox is not { } box) return null;
        double at = C3dLowering.Metres(offsetDbu, Document.DbuPerMicron), tol = 2 * C3dLowering.Metres(1, Document.DbuPerMicron);
        double lo = axis == 0 ? box.Min.X : box.Min.Y, hi = axis == 0 ? box.Max.X : box.Max.Y;
        return Math.Abs(at - lo) <= tol ? lo : Math.Abs(at - hi) <= tol ? hi : null;
    }

    /// <summary>The lines that already have a port on the face at <paramref name="atM"/>: Make Port ▸ Wall leaves them out.</summary>
    private HashSet<string> LinesWithAPortOn(int axis, double atM)
    {
        var plane = axis == 0 ? C3dPlane.YZ : C3dPlane.XZ;
        long offset = (long)Math.Round(atM / C3dLowering.Metres(1, Document.DbuPerMicron), MidpointRounding.AwayFromZero);
        var lines = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in Document.Ports.Where(p => p.Plane == plane && Math.Abs(p.Offset - offset) <= 2))
        {
            if (p.Terminals is { } ts) { lines.UnionWith(ts.Select(t => t.Conductor)); continue; }
            if (p.Positive is { } stated) lines.Add(stated);
            else if (PortResults.FirstOrDefault(r => r.Port.Number == p.Number)?.Resolved is { } r) lines.Add(r.PositiveObject);
        }
        return lines;
    }

    /// <summary>R-pwl4-1 — Make Port ▸ Wall: one wall port per line ending on the face, numbered left to right with the next free
    /// numbers, in one undo entry. Returns the refusal, or null.</summary>
    public string? MakeWallPorts(int axis, double atM)
    {
        var (made, skipped, why) = C3dWallPorts.Propose(CurrentPortContext(), Document.DbuPerMicron, axis, atM, LinesWithAPortOn(axis, atM));
        if (why is not null) return why;
        var numbers = NextPortNumbers(made.Count);
        string z0 = NewPortTemplate().Z0;
        for (int k = 0; k < made.Count; k++) { made[k].Number = numbers[k]; made[k].Name = $"P{numbers[k]}"; made[k].Z0 = z0; }
        string names = string.Join(", ", made.Select(C3dPorts.Label));
        ChangeRecords(made.Count == 1 ? $"Add wall port {made[0].Name}" : $"Add wall ports {names}", d => d.Ports.AddRange(made));
        ToolCommits++;
        string face = BoxFaceKey(axis, made[0].Offset);
        StatusMessage = $"Made wall port{(made.Count == 1 ? "" : "s")} {names} on the {face} face, which is a PEC wall for every setup that runs " +
                        (made.Count == 1 ? "it." : "them.") +
                        (skipped.Count > 0 ? $" Skipped {string.Join(", ", skipped.Select(s => $"'{s}'"))}: it has a port on this face already." : "");
        return null;
    }

    /// <summary>The Make Port ▸ Wall item for the face at <paramref name="atM"/>, enabled only where every port it would add resolves.</summary>
    private Viewer3DMenuItem MakeWallPortItem(int axis, double atM)
    {
        var (_, _, why) = C3dWallPorts.Propose(CurrentPortContext(), Document.DbuPerMicron, axis, atM, LinesWithAPortOn(axis, atM));
        return new Viewer3DMenuItem("Wall", () => Report(MakeWallPorts(axis, atM)), Enabled: why is null, Tip: why ?? WallTip);
    }

    /// <summary>Make Port ▸ Wall on an air-box face object (<c>airbox/xmin</c> …): the floor and the lid take none.</summary>
    private Viewer3DMenuItem? MakeWallPortItemOnBoxFace(string boxFace)
    {
        if (ShownAirBox is not { } box) return null;
        int axis = boxFace[0] - 'x';
        if (axis == 2)
            return new Viewer3DMenuItem("Wall", Enabled: false, Tip: "A wall port stands on an x or y face of the air box; the floor and the lid take none.");
        bool min = boxFace.EndsWith("min", StringComparison.Ordinal);
        double at = axis == 0 ? (min ? box.Min.X : box.Max.X) : (min ? box.Min.Y : box.Max.Y);
        return MakeWallPortItem(axis, at);
    }

    /// <summary>Make Port ▸ Wall on a drawn face: offered only when the face lies on an x or y face of the air box (a line's end
    /// face, a sheet drawn on the wall), as Coaxial is offered only on a round face.</summary>
    private Viewer3DMenuItem? MakeWallPortItemOnFace(Scene3DItem item)
    {
        if (PortFromFace(item.Object, item.Face, Em3dPortKind.Wave, out var draft, out _) is not null || draft is null) return null;
        int axis = draft.Plane switch { C3dPlane.YZ => 0, C3dPlane.XZ => 1, _ => 2 };
        if (BoxFaceAt(axis, draft.Offset) is not { } at) return null;
        // A face no line ends on (a coax's end, a block's side) offers no wall port at all; the box's own face says why instead.
        var (made, skipped, why) = C3dWallPorts.Propose(CurrentPortContext(), Document.DbuPerMicron, axis, at, LinesWithAPortOn(axis, at));
        return made.Count == 0 && skipped.Count == 0 && why?.StartsWith("No line reaches", StringComparison.Ordinal) == true
            ? null : MakeWallPortItem(axis, at);
    }

    /// <summary>R-pwl4-6b — the view's ports on the air box's faces, for an embedded setup's face-port table: a wave port's terminals
    /// each a row, the face each stands on, Z0 and the reference plane's move as the port states them.</summary>
    internal IReadOnlyList<Layout.Em.EmFacePort> FacePortsForSetupPanel()
    {
        var rows = new List<Layout.Em.EmFacePort>();
        string unit = LayoutUnits.Suffix(Document.DisplayUnit);
        // Any wave or wall port on an x or y plane is drawn on a face of the air box by definition; until the box is known its face is
        // named by its plane (the ports of one plane still switch together).
        foreach (var p in Document.Ports.Where(p => p.Kind is Em3dPortKind.Wave or Em3dPortKind.Wall && p.Plane != C3dPlane.XY && p.Corners is null
                                                    && p.Cylinder is null && p.Annulus is null && p.Floquet is null && p.Angle == 0))
        {
            int axis = p.Plane == C3dPlane.YZ ? 0 : 1;
            string face = BoxFaceAt(axis, p.Offset) is not null ? BoxFaceKey(axis, p.Offset)
                        : $"{"xy"[axis]} = {C3dDimension.Spell(p.Offset, Document.DisplayUnit, Document.DbuPerMicron)} {unit}";
            string offset = p.DeembedUm == 0 ? "at the face"
                : $"{C3dDimension.Spell((long)Math.Round(p.DeembedUm * Document.DbuPerMicron), Document.DisplayUnit, Document.DbuPerMicron)} {unit}";
            if (p.Terminals is { Count: > 0 } ts)
                rows.AddRange(ts.Select(t => new Layout.Em.EmFacePort(t.Number, t.Name, p.Kind, face, t.Z0, offset)));
            else rows.Add(new Layout.Em.EmFacePort(p.Number, p.Name, p.Kind, face, p.Z0, offset));
        }
        return rows;
    }

    /// <summary>The label a reader knows a port number by (<c>P1</c>, or a terminal's), from the document; the solved problem
    /// names its ports <c>port/N</c>.</summary>
    internal string PortLabelOf(int number)
        => C3dPorts.OwnerOf(Document, number) is { } p
            ? C3dPorts.TerminalOf(p, number) is { } t ? C3dPorts.TerminalLabel(p, t) : C3dPorts.Label(p)
            : $"port {number}";

    // ── R-pwl4-3 — a face's ports switched between Wave and Wall together ──────────────────────

    /// <summary>What switching <paramref name="port"/>'s face to <paramref name="to"/> would write, or why it cannot: the one call
    /// the menu, Properties and the switch itself all ask.</summary>
    internal (List<C3dPort> Ports, List<string> Switched, string? Refusal) FaceSwitch(C3dPort port, Em3dPortKind to)
        => C3dWallPorts.SwitchFace(Document, port, to, PortContextWith);

    /// <summary>Whether <paramref name="port"/> is drawn on a face of the air box, where Wave and Wall are its two kinds.</summary>
    internal bool IsFacePort(C3dPort port)
        => port.Kind is Em3dPortKind.Wave or Em3dPortKind.Wall && port.Corners is null && port.Cylinder is null && port.Annulus is null
           && port.Floquet is null && port.Angle == 0 && port.Plane != C3dPlane.XY && BoxFaceAt(port.Plane == C3dPlane.YZ ? 0 : 1, port.Offset) is not null;

    /// <summary>Switches every port on <paramref name="port"/>'s face to <paramref name="to"/>, in one undo entry; the status line names
    /// each port that switched. Returns the refusal, or null.</summary>
    public string? SetFaceKind(C3dPort port, Em3dPortKind to)
    {
        var (ports, switched, why) = FaceSwitch(port, to);
        if (why is not null) return why;
        if (switched.Count == 0) return null;
        int axis = port.Plane == C3dPlane.YZ ? 0 : 1;
        string face = BoxFaceKey(axis, port.Offset);
        ChangeRecords($"Make the {face} face's ports {to.ToString().ToLowerInvariant()} ports", d => d.Ports = ports);
        StatusMessage = $"The {face} face's ports are {to.ToString().ToLowerInvariant()} ports now: {string.Join(", ", switched)}. " +
                        "One face carries one kind, so they switch together.";
        return null;
    }

    /// <summary>The Wave or Wall item of Port Kind for the selected face ports, disabled with the refusal when the face would not resolve.</summary>
    private Viewer3DMenuItem FaceKindItem(IReadOnlyList<C3dPort> ports, Em3dPortKind to)
    {
        string word = to == Em3dPortKind.Wave ? "Wave" : "Wall";
        var face = ports.Where(IsFacePort).ToList();
        if (face.Count == 0 || face.Count != ports.Count)
            return to == Em3dPortKind.Wave
                ? new Viewer3DMenuItem(word, () => SetPortKind(ports, Em3dPortKind.Wave), Tip: "A wave port lies on a face of the active setup's air box.")
                : new Viewer3DMenuItem(word, Enabled: false,
                      Tip: "A wall port is drawn on a face of the air box where its line ends; a sheet port between two conductors stays Lumped.");
        if (face.Select(p => (p.Plane, p.Offset)).Distinct().Count() > 1)
            return new Viewer3DMenuItem(word, Enabled: false, Tip: "Select ports on one face: a face's ports switch together.");
        string? why = FaceSwitch(face[0], to).Refusal;
        string tip = $"Every port on this face becomes a {word.ToLowerInvariant()} port: one face carries one kind.";
        return new Viewer3DMenuItem((face.All(p => p.Kind == to) ? "● " : "") + word, () => Report(SetFaceKind(face[0], to)),
                                    Enabled: why is null, Tip: why ?? tip);
    }
}

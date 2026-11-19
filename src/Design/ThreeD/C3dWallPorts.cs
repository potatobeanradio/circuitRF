// PWL-3 — a .c3d's WALL ports: a lumped gap from a PEC face of the air box to the end of the line that meets it.
//
// `docs/briefs/brief-pwl-3-wall-port-model-and-palace.md`. A wall port states what a wave port states — its plane, its offset
// and a rectangle round the line's end on that plane — with Kind Wall. It lowers to an ORDINARY lumped port carrying an
// Em3dWallPort record, so no solver lowers a third kind: the gap is a sheet in the plane of the line's face toward its return,
// from the face (−) to the line's end (+), across the line's width at its end.
//
// Three things change the SOLVED problem and never the document:
//   • the face becomes PEC with zero padding (ForceFaces), whatever the setup states — the wall is the port's ground;
//   • the line is cut back by the gap (Trim), because a line touching a PEC face is shorted there;
//   • the gap is g = min(w, h)/5, w the line's width at its end and h its height above its return (PWL-2 Q1: the calibrated
//     answer is flat from w/40 to w/5, so the size is chosen on cost, and is never a user setting).
//
// A wall port on a face normal to z (the floor or the lid) is refused by name in this version: its line would be vertical.

using System.Globalization;
using System.Numerics;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

/// <summary>The resolution, face forcing and line trimming of wall ports (PWL-3). See the file header.</summary>
public static class C3dWallPorts
{
    /// <summary>g as a fraction of min(width, height) — PWL-2 Q1.</summary>
    public const double GapFraction = 0.2;

    private static readonly string[] FaceKeys = ["xmin", "xmax", "ymin", "ymax"];

    /// <summary>The face key a port drawn on <paramref name="port"/>'s plane and offset lies on, given the content's extent; null
    /// when it lies on no x or y face of it.</summary>
    public static string? FaceOf(C3dPort port, int dbuPerMicron,
                                 (double X0, double Y0, double Z0, double X1, double Y1, double Z1) extent)
    {
        int axis = port.Plane switch { C3dPlane.YZ => 0, C3dPlane.XZ => 1, _ => 2 };
        if (axis == 2 || port.Corners is not null) return null;
        double at = C3dLowering.Metres(port.Offset, dbuPerMicron), tol = 2 * C3dLowering.Metres(1, dbuPerMicron);
        double lo = axis == 0 ? extent.X0 : extent.Y0, hi = axis == 0 ? extent.X1 : extent.Y1;
        return Math.Abs(at - lo) <= tol ? FaceKeys[2 * axis] : Math.Abs(at - hi) <= tol ? FaceKeys[2 * axis + 1] : null;
    }

    /// <summary>
    /// PWL-3 — a 3D view's setup choosing Wave or Wall for its ports on the air box's faces, for that setup only: the setup's
    /// <c>Ports3D</c> rows of kind Wave or Wall applied to COPIES of the document's ports (the document is never changed). A wave
    /// port with terminals run as Wall becomes one wall port per terminal, keeping their numbers; a face's ports must end up one
    /// kind, which <see cref="Faces"/> asks. Lumped rows are a <c>.cem</c>'s spelling and state nothing here. Returns the document
    /// unchanged when the setup states no such row, and a refusal naming the port and the setup when a row cannot apply.
    /// </summary>
    /// <param name="contextFor">The port context for a set of ports (the run's, or the scene's). Given, each face is rebuilt as
    /// the editor's own switch rebuilds it (<see cref="SwitchFace"/>): wall ports become one wave port over the face, with a terminal
    /// per line where there are several, and a wave port becomes a wall port per line with Make Port ▸ Wall's rectangles. Without
    /// one, only the kinds change: enough for which faces carry wall ports (the air box), not for resolving the ports.</param>
    public static C3dDocument ApplySetupKinds(C3dDocument doc, EmSetup? setup, out string? refusal,
                                              Func<C3dDocument, C3dPortContext>? contextFor = null)
    {
        refusal = null;
        var rows = setup?.Ports3D.Where(r => r.Kind is Em3dPortKind.Wave or Em3dPortKind.Wall).ToList() ?? [];
        if (rows.Count == 0) return doc;
        string who = setup!.Name is { Length: > 0 } n ? $"Setup '{n}'" : "This setup";
        if (contextFor is not null)
        {
            var work = new C3dDocument { DbuPerMicron = doc.DbuPerMicron, Ports = [.. doc.Ports.Select(CopyPort)] };
            bool switched = false;
            foreach (var row in rows)
            {
                var p = work.Ports.FirstOrDefault(q => C3dPorts.Numbers(q).Contains(row.Port));
                if (p is null) { refusal = $"{who} runs port {row.Port} as a {row.Kind} port, and this view has no port {row.Port}."; return doc; }
                if (p.Kind == Em3dPortKind.Lumped)
                {
                    refusal = $"{who} runs {C3dPorts.Label(p)} as a {row.Kind} port, and {C3dPorts.Label(p)} is a lumped sheet between two " +
                              "conductors. A setup chooses between Wave and Wall only for a port drawn on a face of the air box.";
                    return doc;
                }
                if (p.Kind == row.Kind) continue;
                var (rebuilt, _, why) = SwitchFace(work, p, row.Kind, contextFor);
                if (why is not null) { refusal = $"{who} runs {C3dPorts.Label(p)} as a {row.Kind} port, and it cannot be one: {why}"; return doc; }
                work.Ports = rebuilt;
                switched = true;
            }
            return switched ? work : doc;
        }
        var ports = doc.Ports.Select(Copy).ToList();
        bool changed = false;
        foreach (var row in rows)
        {
            int at = ports.FindIndex(p => C3dPorts.Numbers(p).Contains(row.Port));
            if (at < 0) { refusal = $"{who} runs port {row.Port} as a {row.Kind} port, and this view has no port {row.Port}."; return doc; }
            var p = ports[at];
            if (p.Kind == Em3dPortKind.Lumped)
            {
                refusal = $"{who} runs {C3dPorts.Label(p)} as a {row.Kind} port, and {C3dPorts.Label(p)} is a lumped sheet between two " +
                          "conductors. A setup chooses between Wave and Wall only for a port drawn on a face of the air box.";
                return doc;
            }
            if (p.Kind == row.Kind) continue;
            changed = true;
            if (row.Kind == Em3dPortKind.Wall && p.Terminals is { Count: > 0 } terminals)
            {
                // one wall port per terminal, on the same face's rectangle, each naming its line
                ports.RemoveAt(at);
                ports.InsertRange(at, terminals.Select(t =>
                {
                    var w = Copy(p);
                    w.Kind = Em3dPortKind.Wall; w.Terminals = null; w.Number = t.Number; w.Name = t.Name;
                    w.Z0 = t.Z0; w.Positive = t.Conductor; w.Negative = null; w.Flip = t.Flip; w.VoltagePath = null;
                    return w;
                }));
                continue;
            }
            p.Kind = row.Kind;
            if (row.Kind == Em3dPortKind.Wall) { p.Negative = null; p.VoltagePath = null; }   // the wall is a wall port's other end
        }
        // A setup that only restates what is drawn (shipped examples state Wave on wave ports) runs the document itself.
        return changed ? new C3dDocument { DbuPerMicron = doc.DbuPerMicron, Ports = ports } : doc;

        static C3dPort Copy(C3dPort p) => CopyPort(p);
    }

    /// <summary>A shallow copy of a port: every settable property.</summary>
    private static C3dPort CopyPort(C3dPort p)
    {
        var c = new C3dPort();
        foreach (var prop in typeof(C3dPort).GetProperties().Where(x => x.CanRead && x.CanWrite && x.GetIndexParameters().Length == 0))
            prop.SetValue(c, prop.GetValue(p));
        return c;
    }

    /// <summary>
    /// PWL-6 R-pwl6-2 — on a grid, a wall port's gap is whole cells: a line at the face and one at the gap's end. A gap the grid
    /// cannot hold (its end merged into the face's line, or off every line) is widened to the first line in from the face, the line
    /// cut back to it, with a note giving both lengths: the gap belongs to the error box, so its size does not reach the answer.
    /// <paramref name="linesOf"/> gives a problem's grid lines (x, y, z, metres), or null when it has none.
    /// </summary>
    public static (Em3dProblem Problem, string? Note, string? Refusal) GapsOnGrid(Em3dProblem problem,
                                                                                Func<Em3dProblem, IReadOnlyList<double>[]?> linesOf)
    {
        if (!problem.Ports.Any(p => p.Wall is not null) || linesOf(problem) is not { } lines) return (problem, null, null);
        var ports = problem.Ports.ToList();
        var said = new List<string>();
        for (int i = 0; i < ports.Count; i++)
        {
            if (ports[i].Wall is not { } w) continue;
            var la = lines[w.Axis];
            double end = w.FaceAtM + w.Inward * w.GapM;
            double next = la.Where(l => w.Inward * (l - w.FaceAtM) > 1e-12).Select(l => (double?)l)
                            .OrderBy(l => Math.Abs(l!.Value - w.FaceAtM)).FirstOrDefault() ?? double.NaN;
            if (double.IsNaN(next)) return (problem, null, $"Port {ports[i].Number}'s wall has no grid line in from its face.");
            double cell = Math.Abs(next - w.FaceAtM);
            if (la.Any(l => Math.Abs(l - end) <= 1e-3 * cell) && Math.Abs(end - w.FaceAtM) > 1e-3 * cell) continue;   // on a line
            double gap = cell;
            if (gap < w.GapM) gap = la.Where(l => w.Inward * (l - w.FaceAtM) >= w.GapM - 1e-12).Select(l => Math.Abs(l - w.FaceAtM)).DefaultIfEmpty(cell).Min();
            double a0 = Math.Min(w.FaceAtM, w.FaceAtM + w.Inward * gap), a1 = Math.Max(w.FaceAtM, w.FaceAtM + w.Inward * gap);
            var p = ports[i];
            ports[i] = p with
            {
                Min = w.Axis == 0 ? p.Min with { X = a0 } : p.Min with { Y = a0 },
                Max = w.Axis == 0 ? p.Max with { X = a1 } : p.Max with { Y = a1 },
                Wall = w with { GapM = gap },
            };
            said.Add($"{(p.SourceLabel ?? p.Name)} {Um(w.GapM)} → {Um(gap)} µm");
        }
        if (said.Count == 0) return (problem, null, null);
        var solids = problem.Solids.ToList();
        var sheets = problem.Sheets.ToList();
        var widened = ports.Where((q, k) => q.Wall is { } w && w.GapM != problem.Ports[k].Wall!.GapM).ToList();
        if (Trim(solids, sheets, widened, 1e-9) is { } why) return (problem, null, why);
        return (problem with { Ports = ports, Solids = solids, Sheets = sheets },
                $"A wall port's gap is whole grid cells, and these were not: {string.Join("; ", said)}. The gap belongs to the calibration's " +
                "error box, so its size does not reach the answer.", null);
    }

    /// <summary>
    /// PWL-6 R-pwl6-4 — a wall port's face with PEC faces beside it (the box's faces across the line's width): with the two end
    /// walls they make a closed box, and an FDTD run's broadband source rings in its modes. Noted, as openEMS's wave port notes
    /// its own side walls, when the box's first mode (c / (2·w·√εr) on the face's width, the densest fill) is below
    /// <see cref="CircuitRF.Engine.Em3d.FdtdWavePorts.SideWallMargin"/> × the sweep's top. Null when no face is so boxed.
    /// </summary>
    public static string? BoxModeNote(Em3dProblem problem)
    {
        var box = problem.Boundary;
        var faces = box.Faces;
        var said = new List<string>();
        double fcLowest = double.PositiveInfinity;
        foreach (var byFace in problem.Ports.Where(p => p.Wall is not null).GroupBy(p => p.Wall!.Face))
        {
            int lat = 1 - byFace.First().Wall!.Axis;
            var beside = lat == 0 ? new[] { ("xmin", faces.XMin), ("xmax", faces.XMax) } : [("ymin", faces.YMin), ("ymax", faces.YMax)];
            var pec = beside.Where(f => f.Item2 == Em3dBoundaryKind.Pec).Select(f => f.Item1).ToList();
            if (pec.Count == 0) continue;
            double w = lat == 0 ? box.Max.X - box.Min.X : box.Max.Y - box.Min.Y;
            var materials = problem.Materials.ToDictionary(m => m.Name, StringComparer.Ordinal);
            double n = Math.Max(1, problem.Solids.Where(x => x.Role != Em3dRole.Conductor && materials.ContainsKey(x.Material))
                                              .Select(x => Math.Sqrt(Math.Max(1, materials[x.Material].Epsr * materials[x.Material].Mur)))
                                              .DefaultIfEmpty(1).Max());
            double fc = CircuitRF.Engine.Em3d.FdtdGrid.C0 / (2 * w * n);
            if (fc >= CircuitRF.Engine.Em3d.FdtdWavePorts.SideWallMargin * problem.Frequency.StopHz) continue;
            said.Add($"{byFace.Key} ({string.Join(", ", pec)})");
            fcLowest = Math.Min(fcLowest, fc);
        }
        if (said.Count == 0) return null;
        string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
        return $"The faces beside the wall ports on the {string.Join(" and ", said)} are PEC, so with the walls they close a box whose first " +
               $"mode is near {G(fcLowest / 1e9)} GHz (c / (2·w·√εr) across the face), below {G(CircuitRF.Engine.Em3d.FdtdWavePorts.SideWallMargin)} × " +
               $"the sweep's top ({G(problem.Frequency.StopHz / 1e9)} GHz). A time-domain source excites it, and the run rings there. Make " +
               "those faces PMC or Absorbing.";
    }

    /// <summary>
    /// The faces the document's modelled wall ports stand on, and a refusal when one of them also carries a wave port
    /// (R-pwl3-5: a face is one kind or the other). Empty when the document has no wall port.
    /// </summary>
    public static (IReadOnlySet<string> Faces, string? Refusal) Faces(C3dDocument doc,
        (double X0, double Y0, double Z0, double X1, double Y1, double Z1) extent)
    {
        var faces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in doc.Ports.Where(p => p.Model && p.Kind == Em3dPortKind.Wall))
            if (FaceOf(p, doc.DbuPerMicron, extent) is { } f) faces.Add(f);
        foreach (var w in doc.Ports.Where(p => p.Model && p.Kind == Em3dPortKind.Wave))
            if (FaceOf(w, doc.DbuPerMicron, extent) is { } f && faces.Contains(f))
            {
                var walls = doc.Ports.Where(p => p.Model && p.Kind == Em3dPortKind.Wall && FaceOf(p, doc.DbuPerMicron, extent) == f)
                                     .Select(C3dPorts.Label);
                return (faces, $"The air box's {f} face carries the wave port {C3dPorts.Label(w)} and the wall port(s) " +
                               $"{string.Join(", ", walls)}. A face is fed by wave ports or by wall ports, not both: make them one kind.");
            }
        return (faces, null);
    }

    /// <summary>
    /// R-pwl3-1 — the setup with each of <paramref name="faces"/> a PEC face with no padding, and the note that says so; the
    /// setup is changed in place (the caller holds a clone).
    /// </summary>
    public static string? ForceFaces(EmSetup setup, IReadOnlySet<string> faces)
    {
        if (faces.Count == 0) return null;
        var box = setup.AirBox ?? new EmAirBox();
        var wall = new EmAirBoxFace(0, Em3dBoundaryKind.Pec);
        var changed = new List<string>();
        foreach (var f in faces.OrderBy(f => Array.IndexOf(FaceKeys, f)))
        {
            var was = f switch { "xmin" => box.XMin, "xmax" => box.XMax, "ymin" => box.YMin, _ => box.YMax };
            if (was is not { PaddingUm: 0, Boundary: Em3dBoundaryKind.Pec, PaddingPercent: null }) changed.Add(f);
            box = f switch
            {
                "xmin" => box with { XMin = wall }, "xmax" => box with { XMax = wall },
                "ymin" => box with { YMin = wall }, _      => box with { YMax = wall },
            };
        }
        setup.AirBox = box;
        return changed.Count == 0 ? null
            : $"The air box's {string.Join(" and ", changed)} face{(changed.Count == 1 ? " is" : "s are")} PEC with no padding for this run, " +
              "whatever the setup states: a wall port's face is the port's ground.";
    }

    /// <summary>
    /// PWL-4 R-pwl4-1 — what *Make Port ▸ Wall* adds on the air box's face at <paramref name="atM"/> on <paramref name="axis"/>
    /// (0 = an x face, 1 = a y face): one wall port per piece of line that reaches the face, ordered by its centre along the face
    /// (the order Make Port ▸ Wave gives terminals), each a rectangle round the line's end from its return to as far above, and
    /// each accepted by <see cref="Resolve"/>. Lines named in <paramref name="skip"/> (they have a port on this face already) are
    /// left out and listed. Unnumbered (Number 0). Empty with the first refusal when the face cannot take them.
    /// </summary>
    public static (List<C3dPort> Ports, List<string> Skipped, string? Refusal) Propose(C3dPortContext ctx, int dbuPerMicron, int axis,
                                                                                      double atM, IReadOnlySet<string>? skip = null)
    {
        var none = new List<C3dPort>();
        var skipped = new List<string>();
        if (axis is not (0 or 1)) return (none, skipped, "A wall port stands on an x or y face of the air box; the floor and the lid take none.");
        if (ctx.Box is not { } box) return (none, skipped, "No setup gives this view an air box, and a wall port stands on one of its faces.");
        double tol = 2 * ctx.Dbu + ctx.OutlineMoveM;
        double lo = C3dPortContext.Get(box.Min, axis), hi = C3dPortContext.Get(box.Max, axis);
        int inward = Math.Abs(atM - lo) <= tol ? 1 : Math.Abs(atM - hi) <= tol ? -1 : 0;
        if (inward == 0) return (none, skipped, "That is not a face of the air box: a wall port is made on the face where the lines end.");
        double at = inward > 0 ? lo : hi;
        string face = FaceKeys[2 * axis + (inward > 0 ? 0 : 1)];
        double probe = at + inward * Math.Max(4 * tol, 1e-9);
        var pieces = new List<(string Line, double WLo, double WHi, double Z)>();
        foreach (var c in ctx.Conductors.Where(c => c.Axis < 0 && !ctx.Ground.Contains(c.Name)))
        {
            var (x0, y0, _, x1, y1, _) = c.Box;
            double cLo = axis == 0 ? x0 : y0, cHi = axis == 0 ? x1 : y1;
            if (!(inward > 0 ? Math.Abs(cLo - at) <= tol : Math.Abs(cHi - at) <= tol)) continue;
            if (EndSection(c, axis, probe) is not { } end) continue;
            if (skip?.Contains(c.Name) == true) { skipped.Add(c.Name); continue; }
            pieces.AddRange(end.Intervals.Select(i => (c.Name, i.Lo, i.Hi, end.Z)));
        }
        if (pieces.Count == 0)
            return (none, skipped, skipped.Count > 0
                ? $"Every line reaching the {face} face has a port there already."
                : $"No line reaches the {face} face. A wall port feeds a line that runs to the face of the air box.");
        double per = C3dLowering.Metres(1, dbuPerMicron);
        long D(double m) => (long)Math.Round(m / per, MidpointRounding.AwayFromZero);
        var plane = axis == 0 ? C3dPlane.YZ : C3dPlane.XZ;
        var ports = new List<C3dPort>();
        bool several = pieces.GroupBy(x => x.Line).Any(g => g.Count() > 1);
        foreach (var (line, wLo, wHi, z) in pieces.OrderBy(x => x.WLo + x.WHi).ThenBy(x => x.Line, StringComparer.Ordinal))
        {
            // From the return (the highest ground under the line, found by the resolver itself) to as far above the line again:
            // a first rectangle from the floor of the box to the line, then the resolver's own height sets it.
            double floor = box.Min.Z;
            var port = new C3dPort
            {
                Kind = Em3dPortKind.Wall, Plane = plane, Offset = D(at), Z0 = "50",
                Rect = new C3dRect { Min = new C3dPoint2(D(wLo), D(floor)), Size = new C3dPoint2(D(wHi) - D(wLo), D(z) - D(floor) + D(z - floor)) },
                Positive = several ? line : null,
            };
            var r = Resolve(port, $"The port on '{line}'", new Complex(50, 0), ctx, dbuPerMicron);
            if (r.Resolved is not { Wall: { } w }) return (none, skipped, r.Refusal);
            port.Rect = new C3dRect { Min = new C3dPoint2(D(wLo), D(z - w.HeightM)), Size = new C3dPoint2(D(wHi) - D(wLo), D(z + w.HeightM) - D(z - w.HeightM)) };
            ports.Add(port);
        }
        return (ports, skipped, null);
    }

    /// <summary>
    /// PWL-4 R-pwl4-3 — the ports on <paramref name="clicked"/>'s air-box face switched to <paramref name="to"/> (Wave or Wall)
    /// together, since a face carries one kind. Each kind is rebuilt exactly as its own *Make Port* writes it, so a round trip
    /// gives back the ports it started from: to Wall, one wall port per line (a wave port's terminals each keep their number,
    /// name and Z0), each with <see cref="Propose"/>'s rectangle; to Wave, one wave port over the whole face (one wall port keeps
    /// its number, name and Z0; several become one port named for the face with a terminal each, its reference inferred as Make
    /// Port ▸ Wave infers it). <c>DeembedUm</c> and <c>Model</c> carry over. Every new port must resolve in the document it
    /// makes (<paramref name="contextFor"/> is the scene's port context for a document). Returns the document's whole new port
    /// list, the labels of the ports that switched, or a refusal; the document is not changed.
    /// </summary>
    public static (List<C3dPort> Ports, List<string> Switched, string? Refusal) SwitchFace(
        C3dDocument doc, C3dPort clicked, Em3dPortKind to, Func<C3dDocument, C3dPortContext> contextFor)
    {
        List<C3dPort> Keep() => [.. doc.Ports];
        if (to is not (Em3dPortKind.Wave or Em3dPortKind.Wall))
            return (Keep(), [], "A port on a face of the air box is a wave port or a wall port.");
        bool OnFace(C3dPort p) => p.Kind is Em3dPortKind.Wave or Em3dPortKind.Wall && p.Plane == clicked.Plane && p.Offset == clicked.Offset
                                  && p.Corners is null && p.Cylinder is null && p.Annulus is null && p.Floquet is null && p.Angle == 0;
        if (!OnFace(clicked)) return (Keep(), [], $"{C3dPorts.Label(clicked)} is not a wave or wall port on a face of the air box.");
        var face = doc.Ports.Where(OnFace).ToList();
        if (face.All(p => p.Kind == to)) return (Keep(), [], null);
        int axis = clicked.Plane switch { C3dPlane.YZ => 0, C3dPlane.XZ => 1, _ => 2 };
        if (axis == 2) return (Keep(), [], "A wall port stands on an x or y face of the air box; the floor and the lid take none.");
        int dbu = doc.DbuPerMicron;
        var ctx = contextFor(doc);
        if (ctx.Box is not { } box) return (Keep(), [], "No setup gives this view an air box.");
        double atM = C3dLowering.Metres(clicked.Offset, dbu);
        var made = new List<C3dPort>();

        // Each line on the face: its port's number, name, Z0, and the line it feeds.
        var lines = new List<(int Number, string Name, string Z0, string Line, C3dPort From)>();
        foreach (var p in face)
        {
            if (p.Terminals is { Count: > 0 } ts) { lines.AddRange(ts.Select(t => (t.Number, t.Name, t.Z0, t.Conductor, p))); continue; }
            if (C3dPorts.Resolve(p, dbu, ctx) is not { Resolved: { } r } res)
                return (Keep(), [], C3dPorts.Resolve(p, dbu, ctx).Refusal ?? $"{C3dPorts.Label(p)} does not resolve.");
            lines.Add((p.Number, p.Name, p.Z0, r.PositiveObject, p));
        }
        double deembed = face[0].DeembedUm;
        bool model = face.All(p => p.Model);

        if (to == Em3dPortKind.Wall)
        {
            var (proposed, _, why) = Propose(ctx, dbu, axis, atM);
            if (why is not null) return (Keep(), [], why);
            foreach (var (number, name, z0, line, _) in lines)
            {
                var rect = proposed.FirstOrDefault(q => (q.Positive ?? LineOf(q)) == line);
                if (rect is null) return (Keep(), [], $"Port {number}'s line '{line}' cannot be fed from the wall: Make Port ▸ Wall finds no end of it on the face.");
                made.Add(new C3dPort
                {
                    Number = number, Name = name, Kind = Em3dPortKind.Wall, Plane = clicked.Plane, Offset = clicked.Offset,
                    Rect = rect.Rect, Positive = rect.Positive, Z0 = z0, DeembedUm = deembed, Model = model,
                });
            }
            string? LineOf(C3dPort q) => Resolve(q, "", new Complex(50, 0), ctx, dbu).Resolved?.PositiveObject;
        }
        else
        {
            int lat = 1 - axis;
            double per = C3dLowering.Metres(1, dbu);
            long D(double m) => (long)Math.Round(m / per, MidpointRounding.AwayFromZero);
            var region = new C3dRect
            {
                Min = new C3dPoint2(D(C3dPortContext.Get(box.Min, lat)), D(box.Min.Z)),
                Size = new C3dPoint2(D(C3dPortContext.Get(box.Max, lat)) - D(C3dPortContext.Get(box.Min, lat)), D(box.Max.Z) - D(box.Min.Z)),
            };
            if (lines.Count == 1)
            {
                var (number, name, z0, _, _) = lines[0];
                made.Add(new C3dPort { Number = number, Name = name, Kind = Em3dPortKind.Wave, Plane = clicked.Plane, Offset = clicked.Offset,
                                       Rect = region, Z0 = z0, DeembedUm = deembed, Model = model });
            }
            else
            {
                var draft = new C3dPort { Kind = Em3dPortKind.Wave, Plane = clicked.Plane, Offset = clicked.Offset, Rect = region };
                if (C3dPorts.TerminalsFor(draft, dbu, ctx, out string? tie) is not var (reference, _, conductors))
                    return (Keep(), [], tie ?? $"The lines on this face do not make one wave port with a terminal each.");
                var byLine = lines.ToDictionary(l => l.Line, StringComparer.Ordinal);
                if (conductors.Any(c => !byLine.ContainsKey(c)))
                    return (Keep(), [], $"A wave port here would also feed {string.Join(", ", conductors.Where(c => !byLine.ContainsKey(c)).Select(c => $"'{c}'"))}, " +
                                        "which has no wall port: give it one first, or keep the face as wall ports.");
                string faceKey = FaceKeys[2 * axis + (Math.Abs(atM - C3dPortContext.Get(box.Min, axis)) <= Math.Abs(atM - C3dPortContext.Get(box.Max, axis)) ? 0 : 1)];
                var taken = doc.Ports.Except(face).Select(p => p.Name).Concat(doc.Ports.Except(face).SelectMany(p => p.Terminals ?? []).Select(t => t.Name))
                               .ToHashSet(StringComparer.Ordinal);
                string portName = faceKey;
                for (int k = 2; taken.Contains(portName); k++) portName = $"{faceKey} {k}";
                made.Add(new C3dPort
                {
                    Name = portName, Kind = Em3dPortKind.Wave, Plane = clicked.Plane, Offset = clicked.Offset, Rect = region, Reference = reference,
                    Terminals = [.. conductors.Select(c => new C3dTerminal { Number = byLine[c].Number, Name = byLine[c].Name, Conductor = c, Z0 = byLine[c].Z0 })],
                    DeembedUm = deembed, Model = model,
                });
            }
        }

        // In the document's order: the new ports where the face's first port stood.
        int at = doc.Ports.FindIndex(OnFace);
        var ports = doc.Ports.Where(p => !OnFace(p)).ToList();
        ports.InsertRange(Math.Min(at, ports.Count), made);
        var after = new C3dDocument { DbuPerMicron = dbu, Ports = ports };
        var check = contextFor(after);
        foreach (var p in made)
            if (C3dPorts.Resolve(p, dbu, check).Refusal is { } refused) return (Keep(), [], refused);
        return (ports, [.. made.SelectMany(p => p.Terminals is { } ts ? ts.Select(t => C3dPorts.TerminalLabel(p, t)) : [C3dPorts.Label(p)])], null);
    }

    /// <summary>
    /// One wall port, resolved against <paramref name="ctx"/>: the line that ends on its face inside its rectangle, the gap and
    /// the lumped port that fills it, or a refusal naming what was found.
    /// </summary>
    internal static C3dPortResult Resolve(C3dPort port, string label, Complex z0, C3dPortContext ctx, int dbuPerMicron)
    {
        C3dPortResult No(string why) => new(port, null, why, [], "");
        if (ctx.Box is not { } box)
            return No($"{label} is a wall port, which stands on a face of the air box, and no setup gives this view an air box.");
        if (port.Corners is not null || port.Angle != 0 || port.Cylinder is not null || port.Annulus is not null || port.Terminals is not null)
            return No($"{label} is a wall port, which is a rectangle on a face of the air box: it states no Corners, Angle, Cylinder, Annulus or Terminals.");
        int axis = port.Plane switch { C3dPlane.YZ => 0, C3dPlane.XZ => 1, _ => 2 };
        if (axis == 2)
            return No($"{label} is a wall port on the XY plane. In this version a wall port stands on an x or y face of the air box, " +
                      "where a horizontal line ends; the floor and the lid take none.");

        double tol = 2 * ctx.Dbu + ctx.OutlineMoveM;
        double at = C3dLowering.Metres(port.Offset, dbuPerMicron);
        double lo = C3dPortContext.Get(box.Min, axis), hi = C3dPortContext.Get(box.Max, axis);
        int inward;
        string face;
        if (Math.Abs(at - lo) <= tol) { inward = +1; face = FaceKeys[2 * axis]; at = lo; }
        else if (Math.Abs(at - hi) <= tol) { inward = -1; face = FaceKeys[2 * axis + 1]; at = hi; }
        else
            return No($"{label} lies at {(axis == 0 ? "x" : "y")} = {Um(at)} µm, which is not a face of the air box " +
                      $"({(axis == 0 ? "x" : "y")} runs {Um(lo)} to {Um(hi)} µm). A wall port is drawn on the face where its line ends, " +
                      "and the line must run to the edge of the structure on that side.");

        // The rectangle's extent on the face: lateral (the face's horizontal axis) and z.
        var frame = C3dTurnedPorts.Frame(port, dbuPerMicron);
        var corners = new[] { frame.P(frame.U0, frame.V0), frame.P(frame.U1, frame.V1) };
        int lat = 1 - axis;
        double rLo = corners.Min(c => C3dPortContext.Get(c, lat)), rHi = corners.Max(c => C3dPortContext.Get(c, lat));
        double rZ0 = corners.Min(c => c.Z), rZ1 = corners.Max(c => c.Z);

        // The line: one conductor, not in the ground set, reaching the face inside the rectangle.
        bool Reaches(C3dPortContext.Conductor c)
        {
            var (x0, y0, z0b, x1, y1, z1) = c.Box;
            double cLo = axis == 0 ? x0 : y0, cHi = axis == 0 ? x1 : y1;
            double lLo = axis == 0 ? y0 : x0, lHi = axis == 0 ? y1 : x1;
            bool touches = inward > 0 ? Math.Abs(cLo - at) <= tol : Math.Abs(cHi - at) <= tol;
            return touches && lHi > rLo + tol && lLo < rHi - tol && z1 >= rZ0 - tol && z0b <= rZ1 + tol;
        }
        var candidates = ctx.Conductors.Where(c => c.Axis < 0 && !ctx.Ground.Contains(c.Name) && Reaches(c)).ToList();
        C3dPortContext.Conductor line;
        if (port.Positive is { } stated)
        {
            if (ctx.Conductors.FirstOrDefault(c => c.Name == stated && c.Axis < 0) is not { } named)
                return No($"{label}'s Positive '{stated}' is not a conductor of this view.");
            if (!Reaches(named))
                return No($"{label}'s Positive '{stated}' does not reach the {face} face inside {label}'s rectangle.");
            line = named;
        }
        else if (candidates.Count == 0)
            return No($"{label}: no line reaches the {face} face inside its rectangle. A wall port feeds the line that runs to the face; " +
                      "draw the line to the face, or move the rectangle over its end.");
        else if (candidates.Count > 1)
            return No($"{label}: {candidates.Count} lines reach the {face} face inside its rectangle ({string.Join(", ", candidates.Select(c => $"'{c.Name}'"))}). " +
                      "A wall port feeds one line: shrink the rectangle to one line's end, or state its Positive. *Make Port ▸ Wall* makes one per line.");
        else line = candidates[0];

        // The line's end: its width just inside the face, and the plane of its face toward the return.
        double probe = at + inward * Math.Max(4 * tol, 1e-9);
        if (EndSection(line, axis, probe) is not { } end)
            return No($"{label}'s line '{line.Name}' is not a sheet, an extruded outline or a box, and a wall port in this version feeds one of those.");
        var interval = end.Intervals.Where(i => i.Hi > rLo + tol && i.Lo < rHi - tol).ToList();
        if (interval.Count != 1)
            return No($"{label}'s line '{line.Name}' crosses its rectangle in {interval.Count} pieces at the {face} face; a wall port feeds one end.");
        var (wLo, wHi) = interval[0];
        double width = wHi - wLo;

        // Its return: the highest ground conductor below the line's end (a PEC floor included).
        double regionLo = Math.Min(at, at + inward * 10 * width), regionHi = Math.Max(at, at + inward * 10 * width);
        double top = double.NegativeInfinity;
        string? returnName = null;
        foreach (var c in ctx.Conductors.Where(c => ctx.Ground.Contains(c.Name)))
        {
            if (c.Axis >= 0 && c.Axis != 2) continue;               // the walls themselves are not below the line
            var (x0, y0, z0b, x1, y1, z1) = c.Box;
            double cLo = axis == 0 ? x0 : y0, cHi = axis == 0 ? x1 : y1, lLo = axis == 0 ? y0 : x0, lHi = axis == 0 ? y1 : x1;
            if (z1 > end.Z + tol || cHi < regionLo || cLo > regionHi || lHi < wLo || lLo > wHi) continue;
            if (z1 > top) { top = z1; returnName = c.Name; }
        }
        if (returnName is null)
            return No($"{label}'s line '{line.Name}' has no ground beneath its end. A wall port's line returns through a ground plane or a " +
                      "PEC floor below it; draw one, or mark the plane ground.");
        double height = end.Z - top;
        double gap = GapFraction * Math.Min(width, height);
        double length = (axis == 0 ? line.Box.X1 - line.Box.X0 : line.Box.Y1 - line.Box.Y0);
        if (!(height > 0) || !(gap > 0)) return No($"{label}'s line '{line.Name}' lies on its return; a wall port needs height above it.");
        if (length < 4 * gap)
            return No($"{label}'s line '{line.Name}' is {Um(length)} µm long, and its {Um(gap)} µm gap needs it at least four gaps long.");

        // The lumped port in the gap: from the face (−) to the line's new end (+), across the line, in the line's lower plane.
        double gEnd = at + inward * gap;
        Point3 Pt(double along, double across) => axis == 0 ? new Point3(along, across, end.Z) : new Point3(across, along, end.Z);
        var a = Pt(Math.Min(at, gEnd), wLo);
        var b = Pt(Math.Max(at, gEnd), wHi);
        var direction = axis == 0 ? new Point3(inward, 0, 0) : new Point3(0, inward, 0);
        string wallName = Em3dAirBox.FaceName(face);
        var refOrigin = Pt(at, 0.5 * (wLo + wHi));
        var resolved = new Em3dPort(port.Number, C3dPorts.ProblemName(port.Number), line.Name, wallName, a, b, direction, z0,
                                    new Em3dReferencePlane(refOrigin, direction, 0))
        {
            Wall = new Em3dWallPort(face, axis, at, inward, gap, wLo, wHi, end.Z, height, line.Name),
        };
        string reason = $"a wall port on the {face} face: '{line.Name}' ({Um(width)} µm wide, {Um(height)} µm above '{returnName}') is cut " +
                        $"back {Um(gap)} µm from the face, and the gap is the port, from the wall (−) to the line (+)";
        return new C3dPortResult(port, resolved, null, [("face", [wallName]), ("line", [line.Name])], reason);
    }

    /// <summary>
    /// PWL-3 — a layout's wall ports: the generator builds them as the wave ports they are drawn as (its face, its zero padding,
    /// its line), and each named here is turned into the wall port it stands for, in the solved problem: the face PEC, the wave
    /// region replaced by the gap from the face to the line, the line cut back. Null with the converted problem, or a refusal.
    /// </summary>
    public static Em3dProblem? FromWavePorts(Em3dProblem problem, IReadOnlySet<int> numbers, List<string> notes, out string? refusal)
    {
        refusal = null;
        var box = problem.Boundary;
        var faces = box.Faces;
        var ports = problem.Ports.ToList();
        var solids = problem.Solids.ToList();
        var sheets = problem.Sheets.ToList();
        foreach (int number in numbers.Order())
        {
            int at = ports.FindIndex(p => p.Number == number);
            if (at < 0) { refusal = $"Port {number} is not in the problem."; return null; }
            var p = ports[at];
            string label = $"Port {number}";
            if (p.Kind != Em3dPortKind.Wave || p.FaceGroup is not null)
            { refusal = $"{label} could not be made a wall port: it is not a plain wave port on a face of the air box."; return null; }
            int axis = Math.Abs(p.Min.X - p.Max.X) < 1e-12 ? 0 : Math.Abs(p.Min.Y - p.Max.Y) < 1e-12 ? 1 : 2;
            if (axis == 2) { refusal = $"{label} is on the floor or the lid; a wall port stands on an x or y face in this version."; return null; }
            double faceAt = axis == 0 ? p.Min.X : p.Min.Y;
            double lo = axis == 0 ? box.Min.X : box.Min.Y, hi = axis == 0 ? box.Max.X : box.Max.Y;
            int inward = Math.Abs(faceAt - lo) < 1e-9 ? 1 : Math.Abs(faceAt - hi) < 1e-9 ? -1 : 0;
            if (inward == 0) { refusal = $"{label} does not lie on a face of the air box."; return null; }
            string face = FaceKeys[2 * axis + (inward > 0 ? 0 : 1)];
            double probe = faceAt + inward * 1e-8;
            int lat = 1 - axis;
            double pLo = lat == 0 ? p.Min.X : p.Min.Y, pHi = lat == 0 ? p.Max.X : p.Max.Y;
            (double Lo, double Hi)? Pick(List<(double Lo, double Hi)> iv) => iv.Where(i => i.Hi > pLo && i.Lo < pHi).Cast<(double, double)?>().FirstOrDefault();
            (double Lo, double Hi)? end; double z;
            if (sheets.FirstOrDefault(x => x.Name == p.PositiveObject && x.Frame is null) is { } sh) { end = Pick(Slice(sh.Outline, sh.Holes, axis, probe)); z = sh.Z; }
            else if (solids.FirstOrDefault(x => x.Name == p.PositiveObject) is { Primitive: Em3dExtrudedPolygon pr }) { end = Pick(Slice(pr.Outline, pr.Holes, axis, probe)); z = pr.ZBottom; }
            else if (solids.FirstOrDefault(x => x.Name == p.PositiveObject) is { Primitive: Em3dBox bx }) { end = (lat == 0 ? bx.Min.X : bx.Min.Y, lat == 0 ? bx.Max.X : bx.Max.Y); z = bx.Min.Z; }
            else { refusal = $"{label}'s line '{p.PositiveObject}' is not a sheet, an extruded outline or a box, which a wall port feeds."; return null; }
            if (end is not { } w) { refusal = $"{label}'s line '{p.PositiveObject}' does not reach the {face} face inside the port."; return null; }
            double top;
            if (p.NegativeObject == Em3dAirBox.FaceName("zmin")) top = box.Min.Z;
            else if (sheets.FirstOrDefault(x => x.Name == p.NegativeObject) is { } gs) top = gs.Z;
            else if (solids.FirstOrDefault(x => x.Name == p.NegativeObject) is { } gso) top = Em3dProblem.Bounds(gso.Primitive).Z1;
            else { refusal = $"{label}'s return '{p.NegativeObject}' is not below its line; a wall port's line returns through a plane beneath it."; return null; }
            double height = z - top, width = w.Hi - w.Lo, gap = GapFraction * Math.Min(width, height);
            if (!(height > 0)) { refusal = $"{label}'s line lies on its return; a wall port needs height above it."; return null; }
            double gEnd = faceAt + inward * gap;
            Point3 Pt(double along, double across) => axis == 0 ? new Point3(along, across, z) : new Point3(across, along, z);
            var dir = axis == 0 ? new Point3(inward, 0, 0) : new Point3(0, inward, 0);
            ports[at] = p with
            {
                Kind = Em3dPortKind.Lumped, NegativeObject = Em3dAirBox.FaceName(face), VoltagePath = null,
                Min = Pt(Math.Min(faceAt, gEnd), w.Lo), Max = Pt(Math.Max(faceAt, gEnd), w.Hi), Direction = dir,
                ReferencePlane = new Em3dReferencePlane(Pt(faceAt, 0.5 * (w.Lo + w.Hi)), dir, p.ReferencePlane.ShiftM),
                Wall = new Em3dWallPort(face, axis, faceAt, inward, gap, w.Lo, w.Hi, z, height, p.PositiveObject),
            };
            faces = face switch
            {
                "xmin" => faces with { XMin = Em3dBoundaryKind.Pec }, "xmax" => faces with { XMax = Em3dBoundaryKind.Pec },
                "ymin" => faces with { YMin = Em3dBoundaryKind.Pec }, _ => faces with { YMax = Em3dBoundaryKind.Pec },
            };
        }
        if (ports.Where(q => q.Kind == Em3dPortKind.Wave).Any(q => ports.Any(wq => wq.Wall is { } ww &&
                (ww.Axis == 0 ? Math.Abs(q.Min.X - q.Max.X) < 1e-12 && Math.Abs(q.Min.X - ww.FaceAtM) < 1e-9
                              : Math.Abs(q.Min.Y - q.Max.Y) < 1e-12 && Math.Abs(q.Min.Y - ww.FaceAtM) < 1e-9))))
        { refusal = "A face of the air box carries both wave ports and wall ports; a face is fed by one kind. Make them one kind."; return null; }
        if (Trim(solids, sheets, ports, 1e-9) is { } why) { refusal = why; return null; }
        notes.Add(GapNote(ports));
        return problem with { Ports = ports, Solids = solids, Sheets = sheets, Boundary = box with { Faces = faces } };
    }

    /// <summary>
    /// D7 / R-pwl3-6 — conductors that meet a wall-port face and are not a wall port's line: the PEC wall shorts each of them to
    /// ground there, in the structure and in every standard. Planes at or below the face's returns are left out (they are the
    /// grounds the wall is meant to meet). One sentence per face, naming each; empty when there is none.
    /// </summary>
    public static IReadOnlyList<string> GroundedByTheWall(Em3dProblem problem)
    {
        var said = new List<string>();
        const double Tol = 1e-9;
        var lines = problem.Ports.Where(p => p.Wall is not null).Select(p => p.Wall!.Conductor).ToHashSet(StringComparer.Ordinal);
        foreach (var face in problem.Ports.Where(p => p.Wall is not null).GroupBy(p => p.Wall!.Face))
        {
            var w = face.First().Wall!;
            double returns = face.Max(p => p.Wall!.ZM - p.Wall!.HeightM);
            bool Meets(double a0, double a1) => Math.Abs((w.Inward > 0 ? a0 : a1) - w.FaceAtM) < Tol;
            var names = new List<string>();
            foreach (var x in problem.Solids.Where(x => x.Role == Em3dRole.Conductor && !lines.Contains(x.Name)))
            {
                var (x0, y0, _, x1, y1, z1) = Em3dProblem.Bounds(x.Primitive);
                if (z1 > returns + Tol && (w.Axis == 0 ? Meets(x0, x1) : Meets(y0, y1))) names.Add(x.Name);
            }
            foreach (var x in problem.Sheets.Where(x => !lines.Contains(x.Name)))
            {
                var (x0, y0, z0, x1, y1, z1) = x.WorldBounds();
                if (Math.Max(z0, z1) > returns + Tol && (w.Axis == 0 ? Meets(x0, x1) : Meets(y0, y1))) names.Add(x.Name);
            }
            if (names.Count > 0)
                said.Add($"{string.Join(", ", names.Distinct().Select(n => $"'{n}'"))} meet{(names.Distinct().Count() == 1 ? "s" : "")} the " +
                         $"{face.Key} face, which wall ports make a PEC wall, and {(names.Distinct().Count() == 1 ? "is" : "are")} shorted to it " +
                         "there, in the structure and in every standard. If that is not a ground, end it short of the face or give it a port.");
        }
        return said;
    }

    /// <summary>A conductor's cross-section just inside a face: the intervals it covers along the face's lateral axis, and the plane
    /// of its face toward the return (a sheet's own plane, a solid's bottom). Null for a kind a wall port does not feed.</summary>
    private static (List<(double Lo, double Hi)> Intervals, double Z)? EndSection(C3dPortContext.Conductor c, int axis, double probe)
    {
        if (c.Sheet is { } sh)
        {
            if (sh.Frame is not null) return null;                    // a vertical sheet
            return (Slice(sh.Outline, sh.Holes, axis, probe), sh.Z);
        }
        if (c.Prism is { } pr) return (Slice(pr.Outline, pr.Holes, axis, probe), pr.ZBottom);
        if (c.Mesh is not null && c.Cylinder is null && c.Patch is null && c.Closed)
        {
            // A box: its own bounds are its section.
            var (x0, y0, z0, x1, y1, z1) = c.Box;
            return ([(axis == 0 ? y0 : x0, axis == 0 ? y1 : x1)], z0);
        }
        return null;
    }

    /// <summary>The intervals a polygon covers along the other horizontal axis at <paramref name="at"/> (see <see cref="Slice"/>), for the
    /// wall calibration's cross-section.</summary>
    public static List<(double Lo, double Hi)> SliceRings(IReadOnlyList<Point2> outline, IReadOnlyList<IReadOnlyList<Point2>> holes, int axis, double at)
        => Slice(outline, holes, axis, at);

    /// <summary>The intervals a polygon (outline minus holes) covers along the other horizontal axis, at coordinate
    /// <paramref name="at"/> along <paramref name="axis"/> — the even-odd crossings of every ring.</summary>
    internal static List<(double Lo, double Hi)> Slice(IReadOnlyList<Point2> outline, IReadOnlyList<IReadOnlyList<Point2>> holes,
                                                       int axis, double at)
    {
        var xs = new List<double>();
        void Ring(IReadOnlyList<Point2> r)
        {
            for (int i = 0; i < r.Count; i++)
            {
                var p = r[i]; var q = r[(i + 1) % r.Count];
                double pa = axis == 0 ? p.X : p.Y, qa = axis == 0 ? q.X : q.Y;
                if ((pa <= at && qa > at) || (qa <= at && pa > at))
                {
                    double t = (at - pa) / (qa - pa);
                    double pl = axis == 0 ? p.Y : p.X, ql = axis == 0 ? q.Y : q.X;
                    xs.Add(pl + t * (ql - pl));
                }
            }
        }
        Ring(outline);
        foreach (var h in holes) Ring(h);
        xs.Sort();
        var r = new List<(double, double)>();
        for (int i = 0; i + 1 < xs.Count; i += 2) r.Add((xs[i], xs[i + 1]));
        return r;
    }

    /// <summary>
    /// R-pwl3-2 — cut each wall port's line back by its gap, in the SOLVED problem only. The cut removes everything of the line on
    /// the face's side of the gap's inner edge, which is its end and nothing else: a line that also runs elsewhere along that face
    /// inside the gap is refused, since cutting it there would remove metal that is not the port's.
    /// </summary>
    public static string? Trim(List<Em3dSolid> solids, List<Em3dSheet> sheets, IEnumerable<Em3dPort> ports, double tolM)
    {
        foreach (var p in ports)
        {
            if (p.Wall is not { } w) continue;
            double cut = w.FaceAtM + w.Inward * w.GapM;
            // keep = the side of `cut` away from the face
            bool Keep(double coord) => w.Inward > 0 ? coord >= cut : coord <= cut;

            int si = sheets.FindIndex(s => s.Name == w.Conductor);
            if (si >= 0)
            {
                var s = sheets[si];
                if (Refuse(s.Outline, s.Holes) is { } why) return why;
                var outline = Clip(s.Outline, w.Axis, cut, Keep);
                var holes = s.Holes.Select(h => (IReadOnlyList<Point2>)Clip(h, w.Axis, cut, Keep)).Where(h => h.Count >= 3).ToList();
                sheets[si] = s with { Outline = outline, Holes = holes };
                continue;
            }
            int pi = solids.FindIndex(s => s.Name == w.Conductor);
            if (pi < 0) return $"The wall port's line '{w.Conductor}' is not in the solved problem.";
            var solid = solids[pi];
            switch (solid.Primitive)
            {
                case Em3dExtrudedPolygon pr:
                    if (Refuse(pr.Outline, pr.Holes) is { } why) return why;
                    solids[pi] = solid with
                    {
                        Primitive = pr with
                        {
                            Outline = Clip(pr.Outline, w.Axis, cut, Keep),
                            Holes = [.. pr.Holes.Select(h => (IReadOnlyList<Point2>)Clip(h, w.Axis, cut, Keep)).Where(h => h.Count >= 3)],
                        },
                    };
                    break;
                case Em3dBox bx:
                    var min = bx.Min; var max = bx.Max;
                    if (w.Axis == 0) { if (w.Inward > 0) min = min with { X = cut }; else max = max with { X = cut }; }
                    else { if (w.Inward > 0) min = min with { Y = cut }; else max = max with { Y = cut }; }
                    solids[pi] = solid with { Primitive = new Em3dBox(min, max) };
                    break;
                default:
                    return $"The wall port's line '{w.Conductor}' is not a sheet, an extruded outline or a box.";
            }

            string? Refuse(IReadOnlyList<Point2> outline, IReadOnlyList<IReadOnlyList<Point2>> holes)
            {
                double mid = w.FaceAtM + w.Inward * 0.5 * w.GapM;
                foreach (var (lo, hi) in Slice(outline, holes, w.Axis, mid))
                    if (lo < w.LineLoM - tolM || hi > w.LineHiM + tolM)
                        return $"The line '{w.Conductor}' also runs along the {w.Face} face beside its wall port's end, inside the port's " +
                               $"{Um(w.GapM)} µm gap, and cutting the gap would remove that metal too. Keep other metal of the line at least " +
                               "one gap away from the face, or make it a separate object.";
                return null;
            }
        }
        return null;
    }

    /// <summary>A ring clipped to the half-plane <paramref name="keep"/> of the line axis = <paramref name="cut"/>
    /// (Sutherland–Hodgman against one edge, which any simple ring may be clipped by).</summary>
    private static List<Point2> Clip(IReadOnlyList<Point2> ring, int axis, double cut, Func<double, bool> keep)
    {
        var r = new List<Point2>(ring.Count + 2);
        for (int i = 0; i < ring.Count; i++)
        {
            var p = ring[i]; var q = ring[(i + 1) % ring.Count];
            double pa = axis == 0 ? p.X : p.Y, qa = axis == 0 ? q.X : q.Y;
            bool pin = keep(pa), qin = keep(qa);
            if (pin) r.Add(p);
            if (pin != qin)
            {
                double t = (cut - pa) / (qa - pa);
                r.Add(axis == 0 ? new Point2(cut, p.Y + t * (q.Y - p.Y)) : new Point2(p.X + t * (q.X - p.X), cut));
            }
        }
        return r;
    }

    /// <summary>What a run with wall ports says of their gaps.</summary>
    public static string GapNote(IEnumerable<Em3dPort> ports, bool calibrated = true)
    {
        var walls = ports.Where(p => p.Wall is not null).ToList();
        return $"{walls.Count} wall port(s) ({string.Join(", ", walls.Select(p => $"{p.Number} on {p.Wall!.Face}"))}): each line is cut back " +
               $"by its gap ({string.Join(", ", walls.Select(p => Um(p.Wall!.GapM) + " µm").Distinct())}) and fed across it" +
               (calibrated ? ", and the gap is calibrated out by line standards the run solves before the structure." : ".");
    }

    private static string Um(double m) => (m * 1e6).ToString("0.###", CultureInfo.InvariantCulture);
}

// brief-emdc-1 R-emdc1-3 — an Em3dProblem lowered for its DC conduction solve: a conductor-only mesh (Gmsh's thermal mode) and,
// once it is meshed, its contacts as node sets.
//
// WHAT IS MESHED. Conductor solids only, through GmshGeoWriter.WriteThermal; the air solids that outrank a conductor (a via's
// bore) are the input's voids, so the precedence cut is the one every EM lowering makes. Dielectrics and the air box are not
// meshed: at DC they carry nothing. Conductor sheets are embedded sheets with their own tags. Each solid also gets one face group
// holding its whole boundary, so every boundary triangle comes back with the Gmsh SURFACE it lies on — the fragment's own
// classification, which is what a contact is chosen from. SIZING is the thermal mode's except in a box or an extrusion, which
// is sized across its width rather than through its thickness (GmshThermalInput.InPlaneElements): the 3D Package never finished
// meshing at two elements through each foil.
//
// A CONDUCTOR READS AT DC AS THE EM LOWERING READS IT (PalaceConfigWriter's rule): σ ≤ 0 or σ = ∞ is PEC, one tie per body;
// finite σ is a conductivity. A sheet's conductance is σ·ThicknessM. The air box: every conductor surface on a PEC face is tied
// to one ground item, except inside a wave port's rectangle, which is the port and not the face; PMC, absorbing and symmetry
// faces are insulating, the natural condition.
//
// CONTACTS. A lumped port's two edges across its Direction (a coaxial port's two circles, a cylinder gap's two arcs); a wave
// port's (or a terminal's) object surface lying in its rectangle of the air-box face; an air-box face is the ground item when it
// is PEC. On a finite-σ solid the contact is the SMALLEST boundary surface that holds the whole edge (ThermalCurrents' rule, at
// mesh level, so circuitRF has one definition of where a port's DC current enters); where none holds it, the union of those it
// lies on, said in a note. A PEC object's contact is its own tie, and its surface is still found so that two different surfaces
// of one body are told apart from the same one. A port with an empty contact, or whose two contacts are the same node set (a
// hollow waveguide's wave port), has no DC meaning and is refused by name.

using System.Globalization;
using CircuitRF.Design.Thermal;
using CircuitRF.Engine.Em3d;
using CircuitRF.Thermal;
using CircuitRF.Thermal.Conduction;

namespace CircuitRF.Design.Em3d;

/// <summary>A meshed conductor solid: its index in the input, and its σ (null: PEC).</summary>
public sealed record Em3dDcBody(string Name, int Solid, double? Sigma);

/// <summary>A conductor sheet: its index in the input, and its σ·t (null: PEC).</summary>
public sealed record Em3dDcSheetBody(string Name, int Sheet, double? Conductance);

/// <summary>Where one contact lies: on an object's EDGE (a lumped port's or element's — the probe points along it), or on an
/// air-box FACE's rectangle (a wave port's). <see cref="Object"/> null searches every conductor (a lumped element names none).</summary>
public sealed record Em3dDcContactSpec(string Label, string? Object, IReadOnlyList<Point3> Edge, double Tolerance,
                                       (int Axis, double At, Point3 Min, Point3 Max)? Face = null);

/// <summary>A port at DC: its number, label, Re Z₀ and two contacts.</summary>
public sealed record Em3dDcPortSpec(int Number, string Label, double Z0, Em3dDcContactSpec Positive, Em3dDcContactSpec Negative);

/// <summary>A lumped element at DC: what it is (<see cref="DcConduction.LumpedAtDc"/>), and its two contacts.</summary>
public sealed record Em3dDcElementSpec(string Name, DcLumpedKind Kind, double Ohms, Em3dDcContactSpec A, Em3dDcContactSpec B);

/// <summary>What <see cref="Em3dDcLowering.Build"/> makes: the Gmsh input and script, and everything the mesh is read back with.</summary>
public sealed record Em3dDcLowered(GmshThermalInput Input, GmshLowering Gmsh, IReadOnlyList<Em3dDcBody> Bodies, IReadOnlyList<Em3dDcSheetBody> Sheets,
                                   IReadOnlyList<Em3dDcPortSpec> Ports, IReadOnlyList<Em3dDcElementSpec> Elements,
                                   IReadOnlyList<(int Axis, double At, string Face)> PecFaces, double PlaneTolerance, IReadOnlyList<string> Notes);

/// <summary>One resolved contact: the object, the Gmsh surfaces it is (empty on a sheet, whose contact is a line of nodes), its
/// area (m², 0 on a sheet), its nodes, and how it was chosen.</summary>
public sealed record Em3dDcContact(string Label, string Object, IReadOnlyList<int> Surfaces, double AreaM2, IReadOnlyList<int> Nodes, string How);

/// <summary>The mesh read back: the conduction problem, every contact, and what the reading said.</summary>
public sealed record Em3dDcMeshed(DcConductionProblem Problem, IReadOnlyList<Em3dDcContact> Contacts, int Tetrahedra, int SheetTriangles,
                                  IReadOnlyList<string> Notes);

public static class Em3dDcLowering
{
    /// <summary>Elements across a flat conductor's width (GmshThermalInput.InPlaneElements). Measured against finer meshes: 8
    /// is within 0.1% of 16 on a bond-wired package and a wired output network, at a third to a quarter of the elements.</summary>
    public const double InPlaneElements = 8;

    /// <summary>The face group holding solid <paramref name="name"/>'s whole boundary.</summary>
    public static string SurfaceGroup(string name) => "dc-surface:" + name;

    /// <summary>R-emdc1-3 — <paramref name="problem"/> lowered for its DC solve, or null with the refusal.</summary>
    public static Em3dDcLowered? Build(Em3dProblem problem, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(problem);
        refusal = null;
        var ci = CultureInfo.InvariantCulture;
        var notes = new List<string>();
        if (problem.Type != Em3dProblemType.Driven)
        {
            refusal = $"A DC row is solved for a driven problem only; this one is {problem.Type.ToString().ToLowerInvariant()}, which writes no S-parameters.";
            return null;
        }
        if (problem.Ports.Count == 0) { refusal = "The problem has no port, so there is no DC row to solve."; return null; }
        foreach (var port in problem.Ports)
            if (!(port.Z0.Real > 0))
            {
                refusal = $"Port {Label(port)}'s reference impedance has a real part of {port.Z0.Real.ToString("G4", ci)} Ω: a DC row is referred to " +
                          "each port's Re Z₀, so it must be positive.";
                return null;
            }
        if (problem.CylinderSheets.Count > 0)
        {
            refusal = $"Sheet '{problem.CylinderSheets[0].Name}' is a conductor on a cylinder, which the conduction mesh cannot state.";
            return null;
        }

        var materials = problem.Materials.ToDictionary(m => m.Name);
        double? Sigma(string material, out string? why)
        {
            why = null;
            if (!materials.TryGetValue(material, out var m)) { why = $"its material '{material}' is not one of the problem's"; return null; }
            return m.SigmaSm > 0 && !double.IsInfinity(m.SigmaSm) ? m.SigmaSm : null;
        }

        // ── what is meshed: conductor solids, their outranking air as voids, conductor sheets ──
        var solids = problem.Solids.Where(s => s.Role == Em3dRole.Conductor).ToList();
        var voids = problem.Solids.Where(s => s.Role == Em3dRole.Air).ToList();
        var bodies = new List<Em3dDcBody>();
        foreach (var s in solids)
        {
            double? sigma = Sigma(s.Material, out string? why);
            if (why is not null) { refusal = $"Conductor '{s.Name}' cannot be solved at DC: {why}."; return null; }
            bodies.Add(new Em3dDcBody(s.Name, bodies.Count, sigma));
        }
        var sheets = new List<GmshThermalSheet>();
        var sheetBodies = new List<Em3dDcSheetBody>();
        foreach (var sh in problem.Sheets)
        {
            double? sigma = Sigma(sh.Material, out string? why);
            if (why is not null) { refusal = $"Sheet '{sh.Name}' cannot be solved at DC: {why}."; return null; }
            double? g = sigma is { } x && sh.ThicknessM > 0 ? x * sh.ThicknessM : null;
            if (sigma is not null && !(sh.ThicknessM > 0))
                notes.Add($"Sheet '{sh.Name}' states no thickness, so its σ gives no sheet conductance: it is solved at DC as a perfect conductor.");
            sheets.Add(new GmshThermalSheet(sh.Name, sh));
            sheetBodies.Add(new Em3dDcSheetBody(sh.Name, sheetBodies.Count, g));
        }
        if (solids.Count == 0 && sheets.Count == 0) { refusal = "The problem holds no conductor, so there is nothing for a DC current to flow in."; return null; }
        var faces = new List<GmshThermalFace>();
        for (int i = 0; i < solids.Count; i++)
        {
            var b = Em3dProblem.Bounds(solids[i].Primitive);
            var box = new Em3dFacePolygon([new Point3(b.X0, b.Y0, b.Z0), new Point3(b.X1, b.Y1, b.Z1)], [], new Point3(0, 0, 1));
            faces.Add(new GmshThermalFace(SurfaceGroup(solids[i].Name), i, [box], ExteriorOnly: false));
        }
        var input = new GmshThermalInput(solids, sheets, faces, Exposed: false, [], ThermalLowerings.Sizing(null)) { Voids = voids, SheetsAlone = true, InPlaneElements = InPlaneElements };
        var gmsh = GmshGeoWriter.WriteThermal(input);
        if (!gmsh.Ok) { refusal = gmsh.Refusal; return null; }

        // ── the air box's PEC faces ──
        var box3 = problem.Boundary;
        double extent = Math.Max(box3.Max.X - box3.Min.X, Math.Max(box3.Max.Y - box3.Min.Y, box3.Max.Z - box3.Min.Z));
        double planeTol = 1e-6 * Math.Max(extent, 1e-3);
        var pec = new List<(int, double, string)>();
        foreach (var (face, axis, at) in new[]
                 {
                     ("xmin", 0, box3.Min.X), ("xmax", 0, box3.Max.X), ("ymin", 1, box3.Min.Y),
                     ("ymax", 1, box3.Max.Y), ("zmin", 2, box3.Min.Z), ("zmax", 2, box3.Max.Z),
                 })
            if (box3.FaceKind(Em3dAirBox.FaceName(face)) == Em3dBoundaryKind.Pec) pec.Add((axis, at, face));

        // ── the ports and the lumped elements ──
        var ports = new List<Em3dDcPortSpec>();
        foreach (var port in problem.Ports.OrderBy(p => p.Number))
        {
            string label = Label(port);
            if (port.Kind == Em3dPortKind.Wave)
            {
                string? face = problem.FaceOf(port.Min, port.Max);
                if (face is null) { refusal = $"Port {label} is a wave port whose rectangle lies on no face of the air box."; return null; }
                int axis = face[0] - 'x';
                double at = face.EndsWith("min", StringComparison.Ordinal) ? Coord(box3.Min, axis) : Coord(box3.Max, axis);
                var region = (axis, at, port.Min, port.Max);
                ports.Add(new Em3dDcPortSpec(port.Number, label, port.Z0.Real,
                                             new Em3dDcContactSpec($"{label}+", port.PositiveObject, [], planeTol, region),
                                             new Em3dDcContactSpec($"{label}−", port.NegativeObject, [], planeTol, region)));
                continue;
            }
            var (pos, neg, tol) = Edges(port.Min, port.Max, port.Direction, port.Annulus, port.Cylinder, port.Quad);
            ports.Add(new Em3dDcPortSpec(port.Number, label, port.Z0.Real,
                                         new Em3dDcContactSpec($"{label}+", port.PositiveObject, pos, tol),
                                         new Em3dDcContactSpec($"{label}−", port.NegativeObject, neg, tol)));
        }
        var elements = new List<Em3dDcElementSpec>();
        foreach (var le in problem.LumpedElements)
        {
            // Validate refuses this too, but nothing obliges a caller to have run it, and LumpedAtDc would read it as a series short
            if (le.R is null && le.L is null && le.C is null)
            {
                refusal = $"Lumped element '{le.Name}' states no R, L or C, so it has no DC meaning.";
                return null;
            }
            foreach (var (key, v) in new[] { ("R", le.R), ("L", le.L), ("C", le.C) })
                if (v is { } x && (!double.IsFinite(x) || x < 0))
                {
                    refusal = $"Lumped element '{le.Name}''s {key} is {x.ToString("G6", ci)}: at DC a lumped value is a finite number of 0 or more.";
                    return null;
                }
            var kind = DcConduction.LumpedAtDc(le.R, le.L, le.C, le.Topology == Em3dRlcTopology.Series);
            var (a, b, tol) = Edges(le.Min, le.Max, le.Direction, null, le.Cylinder);
            elements.Add(new Em3dDcElementSpec(le.Name, kind, le.R ?? 0, new Em3dDcContactSpec($"'{le.Name}' −", null, b, tol),
                                               new Em3dDcContactSpec($"'{le.Name}' +", null, a, tol)));
        }
        return new Em3dDcLowered(input, gmsh, bodies, sheetBodies, ports, elements, pec, planeTol, notes);
    }

    /// <summary>
    /// The mesh Gmsh wrote for <paramref name="low"/>, read back as the conduction problem: tetrahedra per conductor, sheet
    /// triangles inside their outlines, the ties (PEC bodies and sheets, the ground), the resistors and every contact. Null
    /// with the refusal.
    /// </summary>
    public static Em3dDcMeshed? Read(Em3dDcLowered low, MshMesh raw, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(low);
        ArgumentNullException.ThrowIfNull(raw);
        refusal = null;
        var ci = CultureInfo.InvariantCulture;
        var notes = new List<string>();
        int order = low.Input.Sizing.Order;
        if (raw.Order != order) { refusal = $"Gmsh wrote a mesh of order {raw.Order} where order {order} was asked for."; return null; }
        int nSolids = low.Input.Solids.Count, nSheets = low.Input.Sheets.Count;
        int nn = order == 2 ? 10 : 4, nf = order == 2 ? 6 : 3;
        var rawTets = order == 2 ? raw.TetsHigh : raw.Tets;
        var rawTris = order == 2 ? raw.TrianglesHigh : raw.Triangles;

        // ── compaction: the nodes a tetrahedron or a kept sheet triangle uses ──
        var keepTri = new bool[raw.TriangleCount];
        for (int t = 0; t < raw.TriangleCount; t++)
        {
            int attr = raw.TrianglePhysical[t];
            if (attr <= nSolids || attr > nSolids + nSheets) continue;
            var (cx, cy, cz) = Centroid(raw, rawTris, nf, t);
            keepTri[t] = ThermalRunService.OnSheet(low.Input.Sheets[attr - nSolids - 1].Sheet, cx, cy, cz);
        }
        var map = new int[raw.NodeCount];
        Array.Fill(map, -1);
        int next = 0;
        void Use(int v) { if (map[v] < 0) map[v] = next++; }
        for (int e = 0; e < raw.TetCount; e++) for (int k = 0; k < nn; k++) Use(rawTets[nn * e + k]);
        for (int t = 0; t < raw.TriangleCount; t++) if (keepTri[t]) for (int k = 0; k < nf; k++) Use(rawTris[nf * t + k]);
        var nodes = new double[3 * next];
        for (int v = 0; v < raw.NodeCount; v++)
            if (map[v] >= 0)
                for (int c = 0; c < 3; c++) nodes[3 * map[v] + c] = raw.Nodes[3 * v + c] * GmshGeoWriter.LengthUnitM;
        var tets = new int[rawTets.Length];
        var region = new int[raw.TetCount];
        for (int e = 0; e < raw.TetCount; e++)
        {
            int attr = raw.TetPhysical[e];
            if (attr < 1 || attr > nSolids) { refusal = $"The mesh holds a tetrahedron of group {attr}, which is no conductor of the problem."; return null; }
            region[e] = attr - 1;
            for (int k = 0; k < nn; k++) tets[nn * e + k] = map[rawTets[nn * e + k]];
        }
        var sheetTris = new List<int>();
        var sheetTags = new List<int>();
        var boundary = Enumerable.Range(0, nSolids).Select(_ => new List<(int[] Nodes, int Entity)>()).ToArray();
        for (int t = 0; t < raw.TriangleCount; t++)
        {
            int attr = raw.TrianglePhysical[t];
            if (keepTri[t])
            {
                for (int k = 0; k < nf; k++) sheetTris.Add(map[rawTris[nf * t + k]]);
                sheetTags.Add(attr - nSolids - 1);
            }
            else if (attr > nSolids + nSheets && attr <= nSolids + nSheets + nSolids)
            {
                var tri = new int[nf];
                for (int k = 0; k < nf; k++) tri[k] = map[rawTris[nf * t + k]];
                if (tri.Any(v => v < 0)) continue;
                boundary[attr - nSolids - nSheets - 1].Add((tri, raw.TriangleEntity[t]));
            }
        }
        var mesh = new ThermalMesh(nodes, order, tets, region, [.. sheetTris], [.. sheetTags]);
        int ground = mesh.NodeCount;

        double[] P(int v) => [nodes[3 * v], nodes[3 * v + 1], nodes[3 * v + 2]];
        var bodyNodes = new HashSet<int>[nSolids];
        for (int i = 0; i < nSolids; i++) bodyNodes[i] = [];
        for (int e = 0; e < mesh.TetCount; e++) for (int k = 0; k < nn; k++) bodyNodes[region[e]].Add(tets[nn * e + k]);
        var sheetNodes = new HashSet<int>[nSheets];
        for (int k = 0; k < nSheets; k++) sheetNodes[k] = [];
        for (int t = 0; t < sheetTags.Count; t++) for (int k = 0; k < nf; k++) sheetNodes[sheetTags[t]].Add(sheetTris[nf * t + k]);
        foreach (var b in low.Bodies)
            if (bodyNodes[b.Solid].Count == 0) notes.Add($"Conductor '{b.Name}' came out of the mesh with no volume: it carries no DC current.");

        // ── ties: PEC bodies and sheets, and the ground (the air box's PEC faces) ──
        var ties = new List<IReadOnlyList<int>>();
        foreach (var b in low.Bodies) if (b.Sigma is null && bodyNodes[b.Solid].Count > 0) ties.Add([.. bodyNodes[b.Solid].Order()]);
        foreach (var s in low.Sheets) if (s.Conductance is null && sheetNodes[s.Sheet].Count > 0) ties.Add([.. sheetNodes[s.Sheet].Order()]);
        var groundSet = new SortedSet<int> { ground };
        var grounded = new List<string>();
        // a wave port's rectangle is the port, not the face it lies in: the full-wave mesh claims it before the air-box faces, so a
        // conductor ending inside it is not grounded there, even on a PEC face (its own contact would otherwise be the ground)
        var waveRects = low.Ports.Where(x => x.Positive.Face is not null).Select(x => x.Positive.Face!.Value).ToList();
        foreach (var (axis, at, face) in low.PecFaces)
        {
            bool On(int v) => Math.Abs(nodes[3 * v + axis] - at) <= low.PlaneTolerance;
            var inFace = waveRects.Where(f => f.Axis == axis && Math.Abs(f.At - at) <= low.PlaneTolerance).ToList();
            bool InPort(IReadOnlyList<int> vs) => inFace.Any(f => InRectangle(vs, f, P, low.PlaneTolerance));
            int before = groundSet.Count;
            for (int i = 0; i < nSolids; i++)
                foreach (var (tri, _) in boundary[i])
                    if (On(tri[0]) && On(tri[1]) && On(tri[2]) && !InPort(tri)) foreach (int v in tri) groundSet.Add(v);
            for (int k = 0; k < nSheets; k++) foreach (int v in sheetNodes[k]) if (On(v) && !InPort([v])) groundSet.Add(v);
            if (groundSet.Count > before) grounded.Add(face);
        }
        if (grounded.Count > 0)
            notes.Add($"Conductors on the air box's PEC face(s) {string.Join(", ", grounded)} are tied to its ground ({groundSet.Count - 1:N0} node(s)).");
        if (groundSet.Count > 1) ties.Add([.. groundSet]);

        // ── the contacts ──
        var contacts = new List<Em3dDcContact>();
        var dcPorts = new List<DcPort>();
        foreach (var port in low.Ports)
        {
            var pos = Contact(low, port.Positive, boundary, bodyNodes, sheetNodes, P, ground, out string? why);
            if (pos is null) { refusal = $"Port {port.Label} has no DC meaning: {why}"; return null; }
            var neg = Contact(low, port.Negative, boundary, bodyNodes, sheetNodes, P, ground, out why);
            if (neg is null) { refusal = $"Port {port.Label} has no DC meaning: {why}"; return null; }
            if (pos.Nodes.SequenceEqual(neg.Nodes))
            {
                refusal = $"Port {port.Label} has no DC meaning: its positive and negative contacts are the same {(pos.Surfaces.Count > 0 ? "surface" : "nodes")} " +
                          $"of '{pos.Object}', so no DC current can flow through it (a hollow waveguide's wave port is this case).";
                return null;
            }
            contacts.Add(pos);
            contacts.Add(neg);
            dcPorts.Add(new DcPort(port.Number, pos.Nodes, neg.Nodes, port.Z0));
        }
        var resistors = new List<DcResistor>();
        foreach (var el in low.Elements)
        {
            if (el.Kind == DcLumpedKind.Nothing)
            {
                notes.Add($"Lumped element '{el.Name}' is an open at DC (a capacitor): it is not in the network.");
                continue;
            }
            var a = Contact(low, el.A, boundary, bodyNodes, sheetNodes, P, ground, out string? why);
            var b = a is null ? null : Contact(low, el.B, boundary, bodyNodes, sheetNodes, P, ground, out why);
            if (a is null || b is null)
            {
                notes.Add($"Lumped element '{el.Name}' touches no conductor at one end ({why}), so it carries no DC current.");
                continue;
            }
            contacts.Add(a);
            contacts.Add(b);
            if (el.Kind == DcLumpedKind.Tie)
            {
                ties.Add([.. a.Nodes, .. b.Nodes]);
                notes.Add($"Lumped element '{el.Name}' is a short at DC (an inductor, or R = 0): its two ends are one potential.");
            }
            else
            {
                resistors.Add(new DcResistor(el.Name, a.Nodes, b.Nodes, el.Ohms));
                notes.Add($"Lumped element '{el.Name}' is a {el.Ohms.ToString("G4", ci)} Ω resistor at DC.");
            }
        }
        foreach (var c in contacts)
            notes.Add($"{c.Label}: '{c.Object}'" + (c.Surfaces.Count > 0
                          ? $" surface {string.Join("+", c.Surfaces)}, {(c.AreaM2 * 1e12).ToString("0.###", ci)} µm²"
                          : "") + $", {c.Nodes.Count:N0} node(s) — {c.How}.");

        var sigma = low.Bodies.Select(b => b.Sigma).ToArray();
        var sheetG = low.Sheets.Where(s => s.Conductance is not null).ToDictionary(s => s.Sheet, s => s.Conductance!.Value);
        var problem = new DcConductionProblem(mesh, sigma, sheetG, dcPorts) { Ties = ties, Resistors = resistors, VirtualNodes = 1 };
        return new Em3dDcMeshed(problem, contacts, mesh.TetCount, mesh.TriangleCount, notes);
    }

    // ── contacts ─────────────────────────────────────────────────────────────────────────────────

    private static Em3dDcContact? Contact(Em3dDcLowered low, Em3dDcContactSpec spec, List<(int[] Nodes, int Entity)>[] boundary,
                                          HashSet<int>[] bodyNodes, HashSet<int>[] sheetNodes, Func<int, double[]> p, int ground, out string? why)
    {
        why = null;
        var ci = CultureInfo.InvariantCulture;
        if (spec.Object is { } name)
        {
            if (name.StartsWith("airbox/", StringComparison.Ordinal))
            {
                string face = name["airbox/".Length..];
                if (low.PecFaces.Any(f => f.Face == face)) return new(spec.Label, name, [], 0, [ground], "the air box's PEC face, its ground");
                why = $"its {Side(spec)} end is the air box's face '{face}', which is not PEC and so is no conductor at DC.";
                return null;
            }
            int bi = IndexOf(low.Bodies.Select(b => b.Name), name), si = IndexOf(low.Sheets.Select(s => s.Name), name);
            if (bi >= 0)
            {
                var c = OnSolid(spec, boundary[low.Bodies[bi].Solid], p, low.PlaneTolerance, out string how);
                if (c is null) { why = $"its {Side(spec)} contact touches no surface of '{name}'."; return null; }
                return new(spec.Label, name, c.Value.Surfaces, c.Value.Area, c.Value.Nodes, how);
            }
            if (si >= 0)
            {
                var c = OnSheet(spec, sheetNodes[low.Sheets[si].Sheet], p, low.PlaneTolerance);
                if (c.Count == 0) { why = $"its {Side(spec)} contact touches no part of sheet '{name}'."; return null; }
                return new(spec.Label, name, [], 0, c, spec.Face is null ? "the sheet's nodes along the edge" : "the sheet's nodes in the port's rectangle");
            }
            why = $"its {Side(spec)} end is '{name}', which is no conductor this DC solve meshes.";
            return null;
        }

        // a lumped element: every conductor that holds its edge, and the ground when the edge lies on a PEC face
        var all = new SortedSet<int>();
        var names = new List<string>();
        var surfaces = new List<int>();
        double area = 0;
        foreach (var b in low.Bodies)
        {
            var c = OnSolid(spec, boundary[b.Solid], p, low.PlaneTolerance, out _, requireWhole: true);
            if (c is null) continue;
            all.UnionWith(c.Value.Nodes);
            surfaces.AddRange(c.Value.Surfaces);
            area += c.Value.Area;
            names.Add(b.Name);
        }
        foreach (var s in low.Sheets)
        {
            var c = OnSheet(spec, sheetNodes[s.Sheet], p, low.PlaneTolerance);
            if (c.Count < 2) continue;
            all.UnionWith(c);
            names.Add(s.Name);
        }
        foreach (var (axis, at, face) in low.PecFaces)
            if (spec.Edge.All(q => Math.Abs(Coord(q, axis) - at) <= Math.Max(spec.Tolerance, low.PlaneTolerance)))
            {
                all.Add(ground);
                names.Add(Em3dAirBox.FaceName(face));
            }
        if (all.Count == 0) { why = $"its {Side(spec)} edge lies on no conductor"; return null; }
        return new(spec.Label, string.Join("', '", names), surfaces, area, [.. all],
                   names.Count > 1 ? "every conductor its edge lies on" : "the conductor its edge lies on, its smallest surface holding it");
    }

    private static string Side(Em3dDcContactSpec spec) => spec.Label.EndsWith('+') ? "positive" : spec.Label.EndsWith('−') ? "negative" : "one";

    /// <summary>A solid's contact: the smallest surface holding the whole edge (or, with no such surface, the union of those it
    /// lies on — unless <paramref name="requireWhole"/>); on a face rectangle, the solid's triangles lying in it.</summary>
    private static (IReadOnlyList<int> Surfaces, double Area, IReadOnlyList<int> Nodes)? OnSolid(Em3dDcContactSpec spec, List<(int[] Nodes, int Entity)> tris,
                                                                                                Func<int, double[]> p, double planeTol, out string how,
                                                                                                bool requireWhole = false)
    {
        how = "";
        var bySurface = tris.GroupBy(t => t.Entity).ToDictionary(g => g.Key, g => g.ToList());
        if (spec.Face is { } f)
        {
            var hit = tris.Where(t => InRectangle(t.Nodes, f, p, planeTol)).ToList();
            if (hit.Count == 0) return null;
            how = "its surface in the port's rectangle of the air-box face";
            return ([.. hit.Select(t => t.Entity).Distinct().Order()], hit.Sum(t => Area(t.Nodes, p)), [.. hit.SelectMany(t => t.Nodes).Distinct().Order()]);
        }
        var holding = new List<(int Entity, double Area)>();
        var touched = new HashSet<int>();
        foreach (var (entity, list) in bySurface)
        {
            int on = spec.Edge.Count(q => list.Any(t => Distance(q, t.Nodes, p) <= spec.Tolerance));
            if (on == spec.Edge.Count) holding.Add((entity, list.Sum(t => Area(t.Nodes, p))));
            if (on > 0) touched.Add(entity);
        }
        IEnumerable<int> chosen;
        if (holding.Count > 0)
        {
            var best = holding.MinBy(h => h.Area);
            chosen = [best.Entity];
            how = holding.Count > 1 ? $"the smallest of the {holding.Count} surfaces its edge lies on" : "the one surface its edge lies on";
        }
        else if (!requireWhole && touched.Count > 0)
        {
            chosen = touched.Order();
            how = $"no one surface holds its whole edge, so the {touched.Count} it lies on, together";
        }
        else return null;
        var pick = chosen.ToList();
        var sel = pick.SelectMany(e => bySurface[e]).ToList();
        return (pick, sel.Sum(t => Area(t.Nodes, p)), [.. sel.SelectMany(t => t.Nodes).Distinct().Order()]);
    }

    /// <summary>A sheet's contact: its nodes along the edge (or in the face rectangle).</summary>
    private static IReadOnlyList<int> OnSheet(Em3dDcContactSpec spec, HashSet<int> nodes, Func<int, double[]> p, double planeTol)
    {
        if (spec.Face is { } f)
            return [.. nodes.Where(v => InRectangle([v], f, p, planeTol)).Order()];
        return [.. nodes.Where(v => DistanceToPolyline(p(v), spec.Edge) <= spec.Tolerance).Order()];
    }

    private static bool InRectangle(IReadOnlyList<int> vs, (int Axis, double At, Point3 Min, Point3 Max) f, Func<int, double[]> p, double tol)
    {
        double[] lo = [f.Min.X, f.Min.Y, f.Min.Z], hi = [f.Max.X, f.Max.Y, f.Max.Z];
        int corners = Math.Min(3, vs.Count);
        for (int k = 0; k < corners; k++)
            if (Math.Abs(p(vs[k])[f.Axis] - f.At) > tol) return false;
        var c = new double[3];
        for (int k = 0; k < corners; k++) for (int a = 0; a < 3; a++) c[a] += p(vs[k])[a] / corners;
        for (int a = 0; a < 3; a++)
            if (a != f.Axis && (c[a] < lo[a] - tol || c[a] > hi[a] + tol)) return false;
        return true;
    }

    // ── edges ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The positive and negative edges of a lumped port's (or element's) sheet, as probe points, and the tolerance a
    /// point may lie off a surface by: a rectangle's two edges across its direction, a coaxial annulus' two circles, a
    /// cylinder gap's two arcs (or lines).</summary>
    private static (IReadOnlyList<Point3> Positive, IReadOnlyList<Point3> Negative, double Tol) Edges(Point3 min, Point3 max, Point3 direction,
                                                                                                     Em3dAnnulus? annulus, Em3dCylinderGap? cylinder,
                                                                                                     Em3dPortQuad? quad = null)
    {
        double[] fr = [0.01, 0.25, 0.5, 0.75, 0.99];
        if (quad is { } q)
        {
            // A turned port: its two ends across its direction, each from corner to corner.
            var (negEnd, posEnd) = q.Ends(direction);
            static IReadOnlyList<Point3> Along((Point3 P, Point3 Q) e, double[] at)
                => [.. at.Select(t => new Point3(e.P.X + t * (e.Q.X - e.P.X), e.P.Y + t * (e.Q.Y - e.P.Y), e.P.Z + t * (e.Q.Z - e.P.Z)))];
            double tolQ = 1e-3 * Math.Max(q.Extent(direction).Width, 1e-9);
            return (Along(posEnd, fr), Along(negEnd, fr), tolQ);
        }
        if (cylinder is { } cg)
        {
            var pt = cg.Patch;
            bool full = pt.FullCircle;
            IReadOnlyList<Point3> Arc(double s) => [.. (full ? Enumerable.Range(0, 8).Select(k => k / 8.0) : fr)
                                                    .Select(t => pt.World(pt.Alpha0 + t * (pt.Alpha1 - pt.Alpha0), s))];
            IReadOnlyList<Point3> Line(double alpha) => [.. fr.Select(t => pt.World(alpha, pt.Lo + t * (pt.Hi - pt.Lo)))];
            double tolC = Math.Max(1e-3 * (pt.Hi - pt.Lo), 0.05 * pt.Radius);
            return cg.Along == Em3dCylinderGapAxis.Axial
                ? (cg.Sign > 0 ? Arc(pt.Hi) : Arc(pt.Lo), cg.Sign > 0 ? Arc(pt.Lo) : Arc(pt.Hi), tolC)
                : (cg.Sign > 0 ? Line(pt.Alpha1) : Line(pt.Alpha0), cg.Sign > 0 ? Line(pt.Alpha0) : Line(pt.Alpha1), tolC);
        }
        if (annulus is { } an)
        {
            // brief-cxp-1 — in whichever plane the ring lies
            int ringAxis = Em3dAnnulus.NormalAxis(min, max);
            var c = Em3dAnnulus.Centre(min, max);
            IReadOnlyList<Point3> Circle(double r) => [.. Enumerable.Range(0, 8).Select(k => Em3dAnnulus.At(c, ringAxis, r, k * Math.PI / 4))];
            double tolA = 0.05 * an.InnerRadiusM;
            var inner = Circle(an.InnerRadiusM);
            var outer = Circle(an.OuterRadiusM);
            return an.Outward ? (outer, inner, tolA) : (inner, outer, tolA);
        }
        double[] lo = [min.X, min.Y, min.Z], hi = [max.X, max.Y, max.Z], d = [direction.X, direction.Y, direction.Z];
        int along = Enumerable.Range(0, 3).MaxBy(k => Math.Abs(d[k]));
        int normal = Enumerable.Range(0, 3).Where(k => k != along).MinBy(k => hi[k] - lo[k]);
        int across = 3 - along - normal;
        IReadOnlyList<Point3> At(double v)
            => [.. fr.Select(t =>
            {
                var q = new double[3];
                q[along] = v; q[normal] = lo[normal]; q[across] = lo[across] + t * (hi[across] - lo[across]);
                return new Point3(q[0], q[1], q[2]);
            })];
        double tol = 1e-3 * Math.Max(hi[across] - lo[across], 1e-9);
        return d[along] > 0 ? (At(hi[along]), At(lo[along]), tol) : (At(lo[along]), At(hi[along]), tol);
    }

    // ── geometry ─────────────────────────────────────────────────────────────────────────────────

    private static (double X, double Y, double Z) Centroid(MshMesh raw, int[] tris, int nf, int t)
    {
        double x = 0, y = 0, z = 0;
        for (int k = 0; k < 3; k++)
        {
            int v = tris[nf * t + k];
            x += raw.Nodes[3 * v]; y += raw.Nodes[3 * v + 1]; z += raw.Nodes[3 * v + 2];
        }
        double u = GmshGeoWriter.LengthUnitM / 3;
        return (x * u, y * u, z * u);
    }

    private static double Area(int[] tri, Func<int, double[]> p)
    {
        double[] a = p(tri[0]), b = p(tri[1]), c = p(tri[2]);
        double ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2], vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
        double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
        return Math.Sqrt(cx * cx + cy * cy + cz * cz) / 2;
    }

    /// <summary>The distance from <paramref name="q"/> to a triangle (its three corners).</summary>
    private static double Distance(Point3 q, int[] tri, Func<int, double[]> p)
    {
        double[] a = p(tri[0]), b = p(tri[1]), c = p(tri[2]);
        double[] x = [q.X, q.Y, q.Z];
        var ab = Sub(b, a); var ac = Sub(c, a); var ap = Sub(x, a);
        double d1 = Dot(ab, ap), d2 = Dot(ac, ap);
        double[] closest;
        if (d1 <= 0 && d2 <= 0) closest = a;
        else
        {
            var bp = Sub(x, b);
            double d3 = Dot(ab, bp), d4 = Dot(ac, bp);
            var cp = Sub(x, c);
            double d5 = Dot(ab, cp), d6 = Dot(ac, cp);
            double vc = d1 * d4 - d3 * d2, vb = d5 * d2 - d1 * d6, va = d3 * d6 - d5 * d4;
            if (d3 >= 0 && d4 <= d3) closest = b;
            else if (vc <= 0 && d1 >= 0 && d3 <= 0) closest = Add(a, ab, d1 / (d1 - d3));
            else if (d6 >= 0 && d5 <= d6) closest = c;
            else if (vb <= 0 && d2 >= 0 && d6 <= 0) closest = Add(a, ac, d2 / (d2 - d6));
            else if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) closest = Add(b, Sub(c, b), (d4 - d3) / (d4 - d3 + (d5 - d6)));
            else
            {
                double den = 1 / (va + vb + vc), v = vb * den, w = vc * den;
                closest = [a[0] + ab[0] * v + ac[0] * w, a[1] + ab[1] * v + ac[1] * w, a[2] + ab[2] * v + ac[2] * w];
            }
        }
        var dd = Sub(x, closest);
        return Math.Sqrt(Dot(dd, dd));
    }

    private static double DistanceToPolyline(double[] x, IReadOnlyList<Point3> line)
    {
        double best = double.PositiveInfinity;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            double[] a = [line[i].X, line[i].Y, line[i].Z], b = [line[i + 1].X, line[i + 1].Y, line[i + 1].Z];
            var ab = Sub(b, a);
            double len2 = Dot(ab, ab);
            double t = len2 > 0 ? Math.Clamp(Dot(Sub(x, a), ab) / len2, 0, 1) : 0;
            var d = Sub(x, Add(a, ab, t));
            best = Math.Min(best, Math.Sqrt(Dot(d, d)));
        }
        // a rectangle's edge is probed inside its ends: the polyline is extended to them
        if (line.Count >= 2)
        {
            double[] a = [line[0].X, line[0].Y, line[0].Z], b = [line[^1].X, line[^1].Y, line[^1].Z];
            var ab = Sub(b, a);
            double len2 = Dot(ab, ab);
            if (len2 > 0)
            {
                double t = Dot(Sub(x, a), ab) / len2, pad = 0.02;
                if (t > -pad && t < 1 + pad)
                {
                    var d = Sub(x, Add(a, ab, t));
                    best = Math.Min(best, Math.Sqrt(Dot(d, d)));
                }
            }
        }
        return best;
    }

    private static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
    private static double[] Add(double[] a, double[] d, double t) => [a[0] + t * d[0], a[1] + t * d[1], a[2] + t * d[2]];
    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    private static double Coord(Point3 q, int axis) => axis switch { 0 => q.X, 1 => q.Y, _ => q.Z };

    private static int IndexOf(IEnumerable<string> names, string name)
    {
        int i = 0;
        foreach (string n in names) { if (n == name) return i; i++; }
        return -1;
    }

    private static string Label(Em3dPort port) => port.SourceLabel ?? $"P{port.SourceNumber ?? port.Number}";
}

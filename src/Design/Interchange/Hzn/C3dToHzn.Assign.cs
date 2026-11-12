// HZN-4 R-hzn4-1 — what is assigned to objects and faces: conductor sheets, ports, lumped elements, face boundaries, and
// the air box (a region object and the boundaries on its faces).

using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Interchange.Hzn;

internal sealed partial class C3dToHznWriter
{
    private sealed class HAssign
    {
        public required string Name;
        public required string Kind;
        public List<(string Key, string Value)> Props = [];
        public List<string> Objects = [];
        public List<(string Obj, int Face, double[] Sig)> Faces = [];
        public bool Enabled = true;
    }

    private readonly List<HAssign> _boundaries = [];
    private readonly List<HAssign> _excitations = [];

    private string UniqueObjectName(string wanted)
    {
        string name = wanted;
        for (int i = 2; _objects.Any(o => o.Name == name) || _doc.Objects.Any(o => o.Name == name); i++) name = wanted + "_" + i;
        return name;
    }

    private string UniqueBoundaryName(string wanted)
    {
        string name = wanted;
        for (int i = 2; _boundaries.Any(b => b.Name == name); i++) name = wanted + "_" + i;
        return name;
    }

    private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    // ── conductor sheets ──────────────────────────────────────────────────────────────────────────

    /// <summary>A sheet is metal in a <c>.c3d</c>; in Hertzon it is metal because a boundary says so. One Perfect E for the perfect ones,
    /// one Finite Conductivity for each conductivity and thickness.</summary>
    private void Sheets()
    {
        var perfect = new List<string>();
        var finite = new List<(double Sigma, string Thickness, List<string> Names)>();
        foreach (var h in _objects.ToList())
        {
            if (h.Source is not C3dSheet sh || h.ConsumedBy is not null) continue;
            _user = h.Name;
            if (string.IsNullOrEmpty(h.Material)) continue;   // MaterialsUsed() writes it out of the solve
            if (_o.Technology?.FindMaterial(h.Material) is not { } tm)
            {
                _r.Add(HznRefusalKind.Material, $"material '{h.Material}'", "it is not in the document's technology", h.Name);
                continue;
            }
            if (Values(tm, h.Role, h.Name) is not { } v) continue;
            if (v.Pec)
            {
                perfect.Add(h.Name);
                if (sh.ThicknessUm is not null) _notes.Add($"'{h.Name}': a perfect conductor has no thickness, so its stated thickness was not written.");
                continue;
            }
            if (v.Conductivity <= 0)
            {
                _r.Add(HznRefusalKind.Setting, "sheet that is not a conductor", "a .c3d sheet is metal; Hertzon makes a sheet metal by a conductor boundary", h.Name);
                continue;
            }
            string? thickness = null;
            if (sh.ThicknessUm is { } t)
                thickness = C3dBindings.GetExpr(sh, nameof(C3dSheet.ThicknessUm), 0) is { } e
                    ? _x.Write(e.Expr, e.Unit ?? "Um", h.Name)?.Text
                    : HznExpressionWriter.Digits(t) + "um";
            string key = thickness ?? "";
            var group = finite.FirstOrDefault(g => g.Sigma == v.Conductivity && g.Thickness == key);
            if (group.Names is null) finite.Add(group = (v.Conductivity, key, []));
            group.Names.Add(h.Name);
        }
        if (perfect.Count > 0)
            _boundaries.Add(new HAssign { Name = UniqueBoundaryName("PerfE_Sheets"), Kind = "Perfect E", Props = P(("InfGroundPlane", "False")), Objects = perfect });
        foreach (var g in finite)
            _boundaries.Add(new HAssign
            {
                Name = UniqueBoundaryName("FiniteConductivity"), Kind = "Finite Conductivity", Objects = g.Names,
                Props = FiniteConductivityProps(g.Sigma, g.Thickness.Length > 0 ? g.Thickness : null),
            });
    }

    private static List<(string, string)> FiniteConductivityProps(double sigma, string? thickness) =>
        P(("UseMaterial", "False"), ("Material", ""), ("Conductivity", Num(sigma)), ("Permeability", "1"), ("Roughness", "0um"),
          ("UseThickness", thickness is null ? "False" : "True"), ("Thickness", thickness ?? "0um"), ("TwoSided", "False"));

    // ── ports ─────────────────────────────────────────────────────────────────────────────────────

    private void Ports()
    {
        foreach (var port in _doc.Ports)
        {
            string name = port.Name.Length > 0 ? port.Name : $"P{port.Number}";
            _user = name;
            bool wave = port.Kind == Em3dPortKind.Wave;
            if (C3dPorts.HasTerminals(port))
            {
                _r.Add(HznRefusalKind.Excitation, "wave port with several terminals", "one mode is written", name);
                continue;
            }
            if (port.Cylinder is not null)
            {
                _r.Add(HznRefusalKind.Excitation, "cylindrical port", "Hertzon's port is a rectangle sheet in an axis plane", name);
                continue;
            }
            if (port.Angle != 0)
            {
                _r.Add(HznRefusalKind.Excitation, "turned port", "Hertzon's port is a rectangle sheet in an axis plane", name);
                continue;
            }
            if (wave && port.VoltagePath is not null)
            {
                _r.Add(HznRefusalKind.Excitation, "wave port with a stated voltage path", "a wave port's polarity is not written", name);
                continue;
            }
            if (!C3dPorts.TryParseZ0(port.Z0, out var z) || z.Real <= 0 || z.Imaginary != 0)
            {
                _r.Add(HznRefusalKind.Excitation, "reference impedance", $"'{port.Z0}' is not a real positive number of ohms; Hertzon's port has a real impedance", name);
                continue;
            }
            var sheetSource = new C3dSheet { Plane = port.Plane, Offset = port.Offset, Rect = port.Rect };
            var sheetSteps = new List<HStep>();
            if (!SheetSteps(sheetSource, sheetSteps, name)) continue;
            var sheet = new HObj
            {
                Name = UniqueObjectName(name + "_sheet"), History = sheetSteps, IsSheet = true, Source = null,
                Visible = true, Model = true,
            };
            _objects.Add(sheet);
            _extraSheets.Add((sheet, port.Plane, port.Offset, port.Rect));

            var x = new HAssign
            {
                Name = name, Kind = wave ? "Wave Port" : "Lumped Port", Objects = [sheet.Name], Enabled = port.Model,
            };
            x.Props.Add(("PortNumber", port.Number.ToString(CultureInfo.InvariantCulture)));
            if (wave)
            {
                x.Props.Add(("NumberOfModes", "1"));
                x.Props.Add(("ModeType", "Auto"));
                x.Props.Add(("ImpedanceDefinition", "Zpv"));
                x.Props.Add(("Renormalize", "True"));
                x.Props.Add(("RenormImpedance", Num(z.Real) + "ohm"));
                x.Props.Add(("Deembed", "False"));
                x.Props.Add(("DeembedDistance", "0"));
            }
            else
            {
                x.Props.Add(("Impedance", Num(z.Real) + "ohm"));
                var (pos, neg) = port.Flip ? (port.Negative, port.Positive) : (port.Positive, port.Negative);
                if (!string.IsNullOrEmpty(pos) && !string.IsNullOrEmpty(neg))
                {
                    if (IntegrationLine(port.Plane, port.Offset, port.Rect, pos, neg, name) is { } line)
                        x.Props.Add(("IntegrationLine", line));
                }
                else if (port.Flip)
                    _notes.Add($"Port '{name}': its Flip was not written; with no conductors stated, Hertzon places the integration line itself and its polarity is Hertzon's.");
            }
            _excitations.Add(x);
        }
        var dup = _doc.Ports.GroupBy(p => p.Number).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) _r.Add(HznRefusalKind.Excitation, $"port number {dup.Key}", "two ports have it", DesignName);
    }

    /// <summary>Port and element sheets, with the plane, offset and rectangle they were drawn from: the air box must enclose them.</summary>
    private readonly List<(HObj Sheet, C3dPlane Plane, long Offset, C3dRect Rect)> _extraSheets = [];

    // ── lumped elements ───────────────────────────────────────────────────────────────────────────

    private void LumpedElements()
    {
        foreach (var el in _doc.LumpedElements)
        {
            _user = el.Name;
            if (el.Cylinder is not null)
            {
                _r.Add(HznRefusalKind.Boundary, "lumped element on a cylinder", "Hertzon's element is a rectangle sheet in an axis plane", el.Name);
                continue;
            }
            var sheetSource = new C3dSheet { Plane = el.Plane, Offset = el.Offset, Rect = el.Rect };
            var sheetSteps = new List<HStep>();
            if (!SheetSteps(sheetSource, sheetSteps, el.Name)) continue;
            var sheet = new HObj { Name = UniqueObjectName(el.Name + "_sheet"), History = sheetSteps, IsSheet = true, Source = null };
            _objects.Add(sheet);
            _extraSheets.Add((sheet, el.Plane, el.Offset, el.Rect));

            string Part(string? expr, string defaultUnit, string? unit) =>
                expr is null ? "0" + defaultUnit.ToLowerInvariant().Replace("ohm", "ohm") : _x.Write(expr, unit ?? defaultUnit, el.Name)?.Text ?? "0";
            var b = new HAssign { Name = UniqueBoundaryName(el.Name), Kind = "Lumped RLC", Objects = [sheet.Name], Enabled = el.Model };
            b.Props.Add(("Model", el.Topology == Em3dRlcTopology.Series ? "Series RLC" : "Parallel RLC"));
            b.Props.Add(("UseResistance", el.R is null ? "False" : "True"));
            b.Props.Add(("Resistance", Part(el.R, "Ohm", el.RUnit)));
            b.Props.Add(("UseInductance", el.L is null ? "False" : "True"));
            b.Props.Add(("Inductance", Part(el.L, "H", el.LUnit)));
            b.Props.Add(("UseCapacitance", el.C is null ? "False" : "True"));
            b.Props.Add(("Capacitance", Part(el.C, "F", el.CUnit)));
            b.Props.Add(("TouchstoneFile", ""));
            b.Props.Add(("TouchstoneMount", "Series"));
            b.Props.Add(("Component", ""));
            if (!string.IsNullOrEmpty(el.Positive) && !string.IsNullOrEmpty(el.Negative)
                && IntegrationLine(el.Plane, el.Offset, el.Rect, el.Positive, el.Negative, el.Name) is { } line)
                b.Props.Add(("IntegrationLine", line));
            _boundaries.Add(b);
        }
    }

    // ── the integration line ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A lumped port's or element's integration line, from the centre of the contact of the negative conductor to the centre of the
    /// positive one's: every lumped port in Hertzon's examples runs from the ground to the signal conductor. Written only when both
    /// conductors are stated and touch opposite edges of the rectangle; else null (Hertzon places the line itself) and a note says so.
    /// </summary>
    private string? IntegrationLine(C3dPlane plane, long offset, C3dRect rect, string positive, string negative, string who)
    {
        var (u, v, n) = Axes(plane);
        long[] lo = new long[3], hi = new long[3];
        lo[n] = hi[n] = offset;
        (lo[u], hi[u]) = (rect.Min.U, rect.Min.U + rect.Size.U);
        (lo[v], hi[v]) = (rect.Min.V, rect.Min.V + rect.Size.V);
        long tol = Math.Max(2, _dbu / 500);

        Box3? P(string name) => ConductorExtent(name);
        if (P(positive) is not { } pos || P(negative) is not { } neg)
        {
            _notes.Add($"Port '{who}': the extent of '{(P(positive) is null ? positive : negative)}' is not stated, so the integration line is left for Hertzon to place; its polarity is then Hertzon's.");
            return null;
        }
        bool Touches(Box3 e, int axis, bool high)
        {
            int other = axis == u ? v : u;
            long edge = high ? hi[axis] : lo[axis];
            return e.Min[axis] - tol <= edge && edge <= e.Max[axis] + tol
                   && e.Min[other] <= hi[other] + tol && e.Max[other] >= lo[other] - tol
                   && e.Min[n] - tol <= offset && offset <= e.Max[n] + tol;
        }
        var found = new List<(int Axis, bool PosHigh)>();
        foreach (int a in new[] { u, v })
            foreach (bool posHigh in new[] { true, false })
                if (Touches(pos, a, posHigh) && Touches(neg, a, !posHigh))
                    found.Add((a, posHigh));
        if (found.Count > 1)
        {
            // A conductor wide enough to touch both edges: the axis where each touches only one is the gap.
            var exact = found.Where(f => !Touches(pos, f.Axis, !f.PosHigh) && !Touches(neg, f.Axis, f.PosHigh)).ToList();
            found = exact.Count > 0 ? exact : [.. found.OrderBy(f => hi[f.Axis] - lo[f.Axis]).Take(1)];
        }
        if (found.Count == 0)
        {
            _notes.Add($"Port '{who}': '{positive}' and '{negative}' do not touch opposite edges of its rectangle, so the integration line is left for Hertzon to place; its polarity is then Hertzon's.");
            return null;
        }
        var (axis, posHighEdge) = found[0];
        int across = axis == u ? v : u;

        // Coordinates in Hertzon's text, so the line follows the rectangle's variables.
        string MinText(int a) => a == u ? Dim(rect, nameof(C3dRect.Min), 0, rect.Min.U) : Dim(rect, nameof(C3dRect.Min), 1, rect.Min.V);
        string SizeText(int a) => a == u ? Dim(rect, nameof(C3dRect.Size), 0, rect.Size.U) : Dim(rect, nameof(C3dRect.Size), 1, rect.Size.V);
        string Edge(int a, bool high) => high ? $"({MinText(a)})+({SizeText(a)})" : MinText(a);
        string centre = $"({MinText(across)})+({SizeText(across)})/2";
        string Pt(bool high)
        {
            var c = new string[3];
            c[n] = Lit(offset);
            c[axis] = Edge(axis, high);
            c[across] = centre;
            return string.Join(",", c);
        }
        return Pt(!posHighEdge) + ";" + Pt(posHighEdge);
    }

    private Box3? ConductorExtent(string name)
    {
        if (name.StartsWith("airbox/", StringComparison.Ordinal))
            return null;   // the air box is written last; a face of it is not an extent the line can use
        return _byC3dName.TryGetValue(name, out var h) && h.Source is { } src ? Extent(src) : null;
    }

    // ── face boundaries ───────────────────────────────────────────────────────────────────────────

    /// <summary>The face index and signature (centroid, outward normal, area; metres) of a face of an object, found from the written
    /// geometry: analytic for a plain box, else from the geometry worker's build of the object.</summary>
    private (int Index, double[] Sig)? FaceFrame(HObj h, string face, string user)
    {
        var src = h.Source!;
        if (src is C3dBox && AnalyticExtent(src) is { } box)
        {
            int i = System.Array.IndexOf(C3dBox.FaceNameList, face);
            if (i < 0) { _r.Add(HznRefusalKind.FaceReference, $"face '{face}'", $"a box has no such face", user); return null; }
            return (i, BoxSig(box, i, Metres));
        }
        if (!GeometryKernelTree.CanHold(src))
        {
            _r.Add(HznRefusalKind.FaceReference, "face of a sheet", $"'{h.Name}' is not a solid, so it has no faces to name", user);
            return null;
        }
        var kernel = _o.Kernel ?? GeometryKernel.Shared;
        if (!kernel.Capability.Available)
        {
            _r.Add(HznRefusalKind.FaceReference, "face of a built shape", GeometryKernel.NeedsKernel($"naming the face '{face}' of '{h.Name}' by its signature", kernel.Capability), user);
            return null;
        }
        try
        {
            var faces = kernel.Faces(GeometryKernelTree.From(src, _dbu));
            for (int i = 0; i < faces.Count; i++)
            {
                if (faces[i].Name != face) continue;
                var f = faces[i];
                if (f.Centroid.Length != 3 || f.Normal.Length != 3)
                {
                    _r.Add(HznRefusalKind.FaceReference, $"face '{face}'", "the geometry worker gave no centroid or normal for it", user);
                    return null;
                }
                return (i, [f.Centroid[0] * 1e-6, f.Centroid[1] * 1e-6, f.Centroid[2] * 1e-6, f.Normal[0], f.Normal[1], f.Normal[2], f.Area * 1e-12]);
            }
        }
        catch (GeometryKernelException e)
        {
            _r.Add(HznRefusalKind.FaceReference, "face of a built shape", $"the geometry worker refused '{h.Name}': {e.Message}", user);
            return null;
        }
        _r.Add(HznRefusalKind.FaceReference, $"face '{face}'", $"'{h.Name}' has no face of that name", user);
        return null;
    }

    private double Metres(long dbu) => dbu / (1e6 * _dbu);

    /// <summary>A box face's signature: centroid, outward normal, area, in metres. Faces run xmin, xmax, ymin, ymax, zmin, zmax.</summary>
    private static double[] BoxSig(Box3 b, int face, Func<long, double> m)
    {
        int a = face / 2;
        bool high = face % 2 == 1;
        int p = (a + 1) % 3, q = (a + 2) % 3;
        var c = new double[3];
        c[a] = m(high ? b.Max[a] : b.Min[a]);
        c[p] = (m(b.Min[p]) + m(b.Max[p])) / 2;
        c[q] = (m(b.Min[q]) + m(b.Max[q])) / 2;
        var normal = new double[3];
        normal[a] = high ? 1 : -1;
        return [c[0], c[1], c[2], normal[0], normal[1], normal[2], (m(b.Max[p]) - m(b.Min[p])) * (m(b.Max[q]) - m(b.Min[q]))];
    }

    private void FaceBoundaries()
    {
        var wholePec = new List<string>();
        var perFacePec = new HAssign { Name = "PerfE_Faces", Kind = "Perfect E", Props = P(("InfGroundPlane", "False")) };
        var wholeFinite = new Dictionary<double, List<string>>();
        var perFaceFinite = new Dictionary<double, HAssign>();
        foreach (var byObject in _doc.FaceBoundaries.GroupBy(fb => fb.Object))
        {
            string user = byObject.Key;
            if (!_byC3dName.TryGetValue(byObject.Key, out var h) || h.Source is null || h.ConsumedBy is not null)
            {
                _r.Add(HznRefusalKind.FaceReference, "boundary on an object that is not written", $"'{byObject.Key}' is not an object of the design", user);
                continue;
            }
            var all = h.Source.FaceNames();
            foreach (var kindGroup in byObject.GroupBy(fb => (fb.Kind, fb.Material)))
            {
                var (kind, material) = kindGroup.Key;
                if (kind is not (Em3dFaceBoundaryKind.Pec or Em3dFaceBoundaryKind.Conductive))
                {
                    _r.Add(HznRefusalKind.Boundary, $"{kind} boundary on a face of an object",
                           "Hertzon states an open or symmetry wall on the air box, and circuitRF reads one back only there", user);
                    continue;
                }
                double sigma = 0;
                if (kind == Em3dFaceBoundaryKind.Conductive)
                {
                    var tm = material is null ? null : _o.Technology?.FindMaterial(material);
                    if (tm?.Sigma20 is not > 0)
                    {
                        _r.Add(HznRefusalKind.Boundary, "conductive face", $"its material '{material}' states no conductivity", user);
                        continue;
                    }
                    sigma = tm.Sigma20.Value;
                }
                var faces = kindGroup.Select(fb => fb.Face).Distinct(StringComparer.Ordinal).ToList();
                bool whole = all.Count > 0 && all.All(faces.Contains) && faces.All(all.Contains);
                if (whole)
                {
                    if (kind == Em3dFaceBoundaryKind.Pec) wholePec.Add(h.Name);
                    else (wholeFinite.TryGetValue(sigma, out var l) ? l : wholeFinite[sigma] = []).Add(h.Name);
                    continue;
                }
                var target = kind == Em3dFaceBoundaryKind.Pec ? perFacePec
                    : perFaceFinite.TryGetValue(sigma, out var t) ? t
                    : perFaceFinite[sigma] = new HAssign { Name = "FiniteConductivity_Faces", Kind = "Finite Conductivity", Props = FiniteConductivityProps(sigma, null) };
                foreach (string face in faces)
                    if (FaceFrame(h, face, user) is { } f) target.Faces.Add((h.Name, f.Index, f.Sig));
            }
        }
        if (wholePec.Count > 0)
            _boundaries.Add(new HAssign { Name = UniqueBoundaryName("PerfE_Objects"), Kind = "Perfect E", Props = P(("InfGroundPlane", "False")), Objects = wholePec });
        if (perFacePec.Faces.Count > 0) { perFacePec.Name = UniqueBoundaryName(perFacePec.Name); _boundaries.Add(perFacePec); }
        foreach (var (sigma, names) in wholeFinite)
            _boundaries.Add(new HAssign { Name = UniqueBoundaryName("FiniteConductivity_Objects"), Kind = "Finite Conductivity", Props = FiniteConductivityProps(sigma, null), Objects = names });
        foreach (var a in perFaceFinite.Values)
            if (a.Faces.Count > 0) { a.Name = UniqueBoundaryName(a.Name); _boundaries.Add(a); }
    }

    // ── the air box ───────────────────────────────────────────────────────────────────────────────

    private string? _regionMaterial;
    private string? _regionMaterialName;
    private HObj? _region;
    private Box3? _airBoxBox;

    private static readonly string[] BoxFaceNames = ["xmin", "xmax", "ymin", "ymax", "zmin", "zmax"];

    private sealed record AirFace(double? PaddingUm, double? Percent, Em3dBoundaryKind? Boundary);

    /// <summary>Hertzon has one region per design, so every setup's air box must be the same one.</summary>
    private AirFace[]? AirBoxFaces()
    {
        var found = new List<(string Source, AirFace[] Faces)>();
        foreach (var s in C3dSetups.Read(_doc))
        {
            if (s.Setup is not { } setup || setup.IsThermal || setup.IsStatic3D) continue;
            found.Add((s.Name, setup.AirBox is null ? new AirFace[6] : FacesOf(setup.AirBox)));
        }
        if (found.Count == 0 && _doc.AirBox is { } doc)
            found.Add(("the document", FacesOf(doc)));
        if (found.Count == 0) return null;
        for (int i = 1; i < found.Count; i++)
            if (!found[i].Faces.SequenceEqual(found[0].Faces))
            {
                _r.Add(HznRefusalKind.Setting, "setups with different air boxes",
                       $"'{found[0].Source}' and '{found[i].Source}' state different ones, and a Hertzon design has one region", DesignName);
                return null;
            }
        return found[0].Faces;
    }

    private static AirFace[] FacesOf(EmAirBox b) =>
        [.. new[] { b.XMin, b.XMax, b.YMin, b.YMax, b.ZMin, b.ZMax }.Select(f => f is null ? new AirFace(null, null, null) : new AirFace(f.PaddingUm, f.PaddingPercent, f.Boundary))];

    private static AirFace[] FacesOf(CircuitRF.Design.Layout.Em.CemAirBox b) =>
        [.. new[] { b.XMin, b.XMax, b.YMin, b.YMax, b.ZMin, b.ZMax }.Select(f => f is null ? new AirFace(null, null, null) : new AirFace(f.PaddingUm, f.PaddingPercent, f.Boundary))];

    private void AirBox()
    {
        if (AirBoxFaces() is not { } faces) return;
        _user = "air box";
        var pads = new double[6];
        for (int i = 0; i < 6; i++)
        {
            var f = faces[i];
            if (f.Percent is not null)
                _r.Add(HznRefusalKind.Setting, $"air box padding as a percentage ({BoxFaceNames[i]})", "circuitRF reads back an absolute offset only", DesignName);
            else if (f.PaddingUm is not { } pad)
                _r.Add(HznRefusalKind.Setting, $"air box padding not stated ({BoxFaceNames[i]})", "its default depends on the band and the model; state it in the setup", DesignName);
            else pads[i] = pad;
            if (f.Boundary is null)
                _r.Add(HznRefusalKind.Boundary, $"air box face boundary not stated ({BoxFaceNames[i]})", "state it in the setup", DesignName);
            else if (f.Boundary is Em3dBoundaryKind.Symmetry)
                _r.Add(HznRefusalKind.Boundary, "symmetry on an air box face", "Hertzon's symmetry wall is a perfect E or a perfect H, which are the PEC and PMC faces", DesignName);
        }
        if (_r.Any) return;

        Box3? content = null;
        bool first = true;
        foreach (var o in _doc.Objects)
        {
            if (o is C3dPolyline or C3dWire) continue;
            var e = Extent(o);
            if (e is null)
            {
                _r.Add(HznRefusalKind.Setting, "air box around a shape whose extent is not stated",
                       $"'{o.Name}' needs the geometry worker to say how far it reaches", DesignName);
                return;
            }
            content = first ? e : Union(content, e);
            first = false;
        }
        if (content is not { } c) return;   // nothing to surround
        // A wall that is not a perfect conductor is named on the region's face by its signature, and circuitRF finds that face again
        // only around a model whose extent it can state without building it (a rotated or mirrored object is built by the worker).
        if (faces.Any(f => f.Boundary != Em3dBoundaryKind.Pec)
            && _doc.Objects.Any(o => o is not (C3dPolyline or C3dWire) && AnalyticExtent(o) is null))
        {
            _r.Add(HznRefusalKind.Boundary, "open or magnetic air box face around a rotated or built shape",
                   "circuitRF finds the region's face from the signature only when the model's extent is stated by plain shapes; a perfect conductor face has no such need", DesignName);
            return;
        }

        long Pad(int i) => (long)Math.Round(pads[i] * _dbu, MidpointRounding.AwayFromZero);
        var box = new Box3([c.Min[0] - Pad(0), c.Min[1] - Pad(2), c.Min[2] - Pad(4)], [c.Max[0] + Pad(1), c.Max[1] + Pad(3), c.Max[2] + Pad(5)]);
        // Hertzon pads around every object it has, the port and element sheets too.
        Box3 hz = c;
        foreach (var (_, plane, offset, rect) in _extraSheets)
        {
            var (u, v, n) = Axes(plane);
            long[] lo = new long[3], hi = new long[3];
            lo[n] = hi[n] = offset;
            (lo[u], hi[u]) = (rect.Min.U, rect.Min.U + rect.Size.U);
            (lo[v], hi[v]) = (rect.Min.V, rect.Min.V + rect.Size.V);
            hz = Union(hz, new Box3(lo, hi))!.Value;
        }
        var hzPad = new long[6];
        for (int a = 0; a < 3; a++)
        {
            hzPad[a * 2] = box.Min[a] <= hz.Min[a] ? hz.Min[a] - box.Min[a] : -1;
            hzPad[a * 2 + 1] = box.Max[a] >= hz.Max[a] ? box.Max[a] - hz.Max[a] : -1;
        }
        for (int i = 0; i < 6; i++)
            if (hzPad[i] < 0)
            {
                _r.Add(HznRefusalKind.Excitation, $"port or element sheet outside the air box ({BoxFaceNames[i]})",
                       "Hertzon's region encloses every object it has", DesignName);
                return;
            }
        _airBoxBox = box;

        string[] keys = ["-X", "+X", "-Y", "+Y", "-Z", "+Z"];
        var step = new HStep("CreateRegion", P(("PaddingType", "Absolute Offset")));
        for (int i = 0; i < 6; i++) step.Props.Add((keys[i], Lit(hzPad[i])));
        string name = _objects.Any(o => o.Name == "AirBox") || _doc.Objects.Any(o => ObjectName(o.Name) == "AirBox") ? UniqueObjectName("Region") : "AirBox";
        _regionMaterial = _doc.AirBoxMaterial;
        _region = new HObj { Name = name, History = [step], Source = null };
        _regionBoxFaces = faces;
    }

    private AirFace[]? _regionBoxFaces;

    /// <summary>The boundaries on the region's faces: radiation for an absorbing face, perfect E for a PEC one, a perfect H symmetry wall
    /// for a PMC one. A face's index is its place in xmin, xmax, ymin, ymax, zmin, zmax, and its signature is stated from the box.</summary>
    private void RegionBoundaries()
    {
        if (_region is null || _airBoxBox is not { } box || _regionBoxFaces is not { } faces) return;
        var radiation = new HAssign
        {
            Name = UniqueBoundaryName("Radiation"), Kind = "Radiation", Props = P(("Order", "First order"), ("FarFieldReference", "True")),
        };
        var pec = new HAssign { Name = UniqueBoundaryName("PerfE_AirBox"), Kind = "Perfect E", Props = P(("InfGroundPlane", "False")) };
        var pmc = new HAssign { Name = UniqueBoundaryName("Symmetry_PerfectH"), Kind = "Symmetry", Props = P(("Type", "Perfect H"), ("ImpedanceMultiplier", "1")) };
        for (int i = 0; i < 6; i++)
        {
            var target = faces[i].Boundary switch { Em3dBoundaryKind.Absorbing => radiation, Em3dBoundaryKind.Pmc => pmc, _ => pec };
            target.Faces.Add((_region.Name, i, BoxSig(box, i, Metres)));
        }
        // All six walls perfect conductors is one boundary on the whole object, which needs no face found by signature.
        if (pec.Faces.Count == 6) { pec.Faces.Clear(); pec.Objects.Add(_region.Name); }
        foreach (var a in new[] { radiation, pec, pmc })
            if (a.Faces.Count > 0) _boundaries.Add(a);
    }
}

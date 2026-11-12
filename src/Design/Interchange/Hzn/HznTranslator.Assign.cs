// HZN-2 R-hzn2-4, -6 — ports, boundaries and the air box: what is assigned to objects and to faces.

using System.Globalization;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Interchange.Hzn;

internal sealed partial class HznTranslator
{
    // ── the region: the setup's air box ───────────────────────────────────────────────────────────

    private string? _regionName;
    private Dim[]? _regionPadding;   // xmin, xmax, ymin, ymax, zmin, zmax
    private string? _regionMaterial;

    private void Region(HznObject o)
    {
        string name = o.Name!;
        if (_regionName is not null)
        {
            _r.Add(HznRefusalKind.Setting, "two regions", "a design has one air box", name);
            return;
        }
        var step = o.History.First(s => s.Kind == "CreateRegion" && s.Suppressed != true);
        string type = step.Props["PaddingType"] ?? "";
        if (type != "Absolute Offset")
        {
            _r.Add(HznRefusalKind.CreationStep, $"CreateRegion with '{type}'",
                   "whether a percentage is of the content's extent along the axis, as an air box's is, is not established", name);
            return;
        }
        string[] keys = ["-X", "+X", "-Y", "+Y", "-Z", "+Z"];
        var pad = new Dim[6];
        for (int i = 0; i < 6; i++)
        {
            if (Len(step.Props[keys[i]], $"{name} ({keys[i]})") is not { } d) return;
            pad[i] = d;
        }
        (_regionName, _regionPadding, _regionMaterial) = (name, pad, o.Material);
        _prov.RegionObject = name;
        if (o.Material is { Length: > 0 } mat && Material(mat, name) is { } m) _doc.AirBoxMaterial = m.Material.Name;
    }

    // ── numbers for geometry questions ────────────────────────────────────────────────────────────

    /// <summary>Resolves the document's own numbers, so every geometry question below reads them.</summary>
    private void Resolve()
    {
        AddProjectVariables();
        var res = C3dResolver.Resolve(_doc, C3dCell.None);
        foreach (string err in res.Errors) _r.Add(HznRefusalKind.Expression, "dimension", err, DesignName);
    }

    private static double M(long dbu) => dbu / (1e6 * HznToC3d.DbuPerMicron);

    private readonly record struct Box3(long[] Min, long[] Max);

    private static Box3? Union(Box3? a, Box3? b)
    {
        if (a is not { } x || b is not { } y) return null;
        return new Box3([Math.Min(x.Min[0], y.Min[0]), Math.Min(x.Min[1], y.Min[1]), Math.Min(x.Min[2], y.Min[2])],
                        [Math.Max(x.Max[0], y.Max[0]), Math.Max(x.Max[1], y.Max[1]), Math.Max(x.Max[2], y.Max[2])]);
    }

    /// <summary>An object's box in the document's frame, or null when it cannot be stated without the kernel (a rotated
    /// placement, a curved sweep).</summary>
    private Box3? Bounds(C3dObject o)
    {
        var pl = o.Placement;
        if (pl.Rotate.Count > 0 || pl.MirrorX) return null;
        Box3? own = o switch
        {
            C3dBox b => new Box3([b.Min.X, b.Min.Y, b.Min.Z], [b.Min.X + b.Size.X, b.Min.Y + b.Size.Y, b.Min.Z + b.Size.Z]),
            C3dSphere s => new Box3([s.Centre.X - s.Radius, s.Centre.Y - s.Radius, s.Centre.Z - s.Radius],
                                    [s.Centre.X + s.Radius, s.Centre.Y + s.Radius, s.Centre.Z + s.Radius]),
            C3dCylinder c => CylinderBox(c),
            C3dSheet sh => SheetBox(sh),
            C3dPrism p when p.Shear == default => PrismBox(p),
            C3dBoolean bo => BooleanBox(bo),
            C3dFillet { Target: { } t } => Bounds(t),
            C3dChamfer { Target: { } t } => Bounds(t),
            _ => null,
        };
        if (own is not { } box) return null;
        long[] d = [pl.Origin.X, pl.Origin.Y, pl.Origin.Z];
        return new Box3([box.Min[0] + d[0], box.Min[1] + d[1], box.Min[2] + d[2]], [box.Max[0] + d[0], box.Max[1] + d[1], box.Max[2] + d[2]]);
    }

    private static Box3 CylinderBox(C3dCylinder c)
    {
        long[] min = [c.Base.X - c.Radius, c.Base.Y - c.Radius, c.Base.Z - c.Radius];
        long[] max = [c.Base.X + c.Radius, c.Base.Y + c.Radius, c.Base.Z + c.Radius];
        int a = (int)c.Axis;
        long[] b = [c.Base.X, c.Base.Y, c.Base.Z];
        min[a] = b[a];
        max[a] = b[a] + c.Length;
        return new Box3(min, max);
    }

    private static Box3? SheetBox(C3dSheet s)
    {
        var (u, v, n) = Axes(s.Plane);
        var min = new long[3];
        var max = new long[3];
        min[n] = max[n] = s.Offset;
        if (s.Rect is { } r)
        {
            (min[u], max[u]) = (r.Min.U, r.Min.U + r.Size.U);
            (min[v], max[v]) = (r.Min.V, r.Min.V + r.Size.V);
        }
        else if (s.Outline.Count > 0)
        {
            (min[u], max[u]) = (s.Outline.Min(q => q.U), s.Outline.Max(q => q.U));
            (min[v], max[v]) = (s.Outline.Min(q => q.V), s.Outline.Max(q => q.V));
        }
        else return null;
        return new Box3(min, max);
    }

    private static Box3? PrismBox(C3dPrism p)
    {
        if (p.Outline.Count == 0) return null;
        var (u, v, n) = Axes(p.Plane);
        var min = new long[3];
        var max = new long[3];
        (min[u], max[u]) = (p.Outline.Min(q => q.U), p.Outline.Max(q => q.U));
        (min[v], max[v]) = (p.Outline.Min(q => q.V), p.Outline.Max(q => q.V));
        (min[n], max[n]) = (Math.Min(p.Offset, p.Offset + p.Height), Math.Max(p.Offset, p.Offset + p.Height));
        return new Box3(min, max);
    }

    private Box3? BooleanBox(C3dBoolean b)
    {
        if (b.Blank is not { } blank) return null;
        var box = Bounds(blank);
        if (b.Op == C3dBooleanOp.Unite)
            foreach (var t in b.Tools) box = Union(box, Bounds(t));
        return box;
    }

    private static (int U, int V, int N) Axes(C3dPlane p) => p switch
    {
        C3dPlane.XY => (0, 1, 2),
        C3dPlane.YZ => (1, 2, 0),
        _ => (0, 2, 1),
    };

    /// <summary>The union of what is left in the document: the extent an air box is padded around.</summary>
    private Box3? Content()
    {
        Box3? all = null;
        bool first = true;
        foreach (var o in _doc.Objects)
        {
            var b = Bounds(o);
            if (b is null) return null;
            all = first ? b : Union(all, b);
            first = false;
        }
        return all;
    }

    // ── faces ─────────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] BoxFaces = ["xmin", "xmax", "ymin", "ymax", "zmin", "zmax"];

    private static IReadOnlyList<HznFaceFrame> BoxFrames(Box3 b, Func<long, double> m)
    {
        double[] lo = [m(b.Min[0]), m(b.Min[1]), m(b.Min[2])];
        double[] hi = [m(b.Max[0]), m(b.Max[1]), m(b.Max[2])];
        var list = new List<HznFaceFrame>();
        for (int a = 0; a < 3; a++)
        {
            int p = (a + 1) % 3, q = (a + 2) % 3;
            double area = (hi[p] - lo[p]) * (hi[q] - lo[q]);
            foreach (int side in new[] { 0, 1 })
            {
                var c = new double[3];
                c[a] = side == 0 ? lo[a] : hi[a];
                c[p] = (lo[p] + hi[p]) / 2;
                c[q] = (lo[q] + hi[q]) / 2;
                var n = new double[3];
                n[a] = side == 0 ? -1 : 1;
                list.Add(new HznFaceFrame(BoxFaces[a * 2 + side], c, n, area));
            }
        }
        return list;
    }

    /// <summary>The faces of the top-level object (or of the region, as a virtual box) with their geometry. A plain box is
    /// stated here; anything else is built by the geometry worker.</summary>
    private IReadOnlyList<HznFaceFrame>? Frames(string c3dName, string user)
    {
        if (c3dName == _regionName)
        {
            if (Content() is not { } content || _regionPadding is not { } pad) return Unknown(user);
            var padded = new Box3(
                [content.Min[0] - PadDbu(pad[0]), content.Min[1] - PadDbu(pad[2]), content.Min[2] - PadDbu(pad[4])],
                [content.Max[0] + PadDbu(pad[1]), content.Max[1] + PadDbu(pad[3]), content.Max[2] + PadDbu(pad[5])]);
            return BoxFrames(padded, M);
        }
        if (!_top.TryGetValue(c3dName, out var obj)) return Unknown(user);
        if (obj is C3dBox && Bounds(obj) is { } box) return BoxFrames(box, M);
        if (!GeometryKernelTree.CanHold(obj))
        {
            _r.Add(HznRefusalKind.FaceReference, "face of a sheet", $"'{c3dName}' is not a solid, so it has no faces to name", user);
            return null;
        }
        var kernel = _kernel ?? GeometryKernel.Shared;
        var capability = kernel.Capability;
        if (!capability.Available)
        {
            _r.Add(HznRefusalKind.FaceReference, "face of a built shape",
                   GeometryKernel.NeedsKernel($"finding the face of '{c3dName}' a signature describes", capability), user);
            return null;
        }
        try
        {
            var tree = GeometryKernelTree.From(obj, HznToC3d.DbuPerMicron);
            return [.. kernel.Faces(tree).Where(f => f.Normal.Length == 3 && f.Centroid.Length == 3)
                .Select(f => new HznFaceFrame(f.Name, [.. f.Centroid.Select(x => x * 1e-6)], f.Normal, f.Area * 1e-12))];
        }
        catch (GeometryKernelException e)
        {
            _r.Add(HznRefusalKind.FaceReference, "face of a built shape", $"the geometry worker refused '{c3dName}': {e.Message}", user);
            return null;
        }
    }

    private IReadOnlyList<HznFaceFrame>? Unknown(string user)
    {
        _r.Add(HznRefusalKind.FaceReference, "face of an unknown extent",
               "the shape's extent cannot be stated without building it, and the geometry worker is not asked for a rotated placement", user);
        return null;
    }

    private long PadDbu(Dim d) => d.IsLiteral ? d.Dbu : (long)Math.Round(Metres(d) * 1e6 * HznToC3d.DbuPerMicron);

    /// <summary>The name of the face a reference's signature describes; null with a refusal item otherwise.</summary>
    private string? FaceName(HznFaceRef f, string boundary)
    {
        string obj = f.Object ?? "";
        string c3d = _c3dName.GetValueOrDefault(obj, obj);
        string user = $"{boundary} (face {f.Face} of {obj})";
        if (!_top.ContainsKey(c3d) && c3d != _regionName)
        {
            _r.Add(HznRefusalKind.FaceReference, "face of an object that is not translated", $"'{obj}' is not an object of the document", user);
            return null;
        }
        var frames = Frames(c3d, user);
        if (frames is null) return null;
        var result = HznFaceMatch.Match(frames, f.Signature);
        if (result.Name is null)
            _r.Add(HznRefusalKind.FaceReference, "face by index", $"the index is not trusted, and {result.Problem}", user);
        return result.Name;
    }

    // ── removing an object that became something else ─────────────────────────────────────────────

    private void Remove(string c3dName)
    {
        if (_top.Remove(c3dName, out var o)) _doc.Objects.Remove(o);
        _topOrder.Remove(c3dName);
        _prov.Objects.Remove(c3dName);
    }

    private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    // ── ports ─────────────────────────────────────────────────────────────────────────────────────

    private void Ports()
    {
        foreach (var x in _design.Excitations)
        {
            string name = x.Name ?? "";
            if (x.Enabled == false) { _notes.Add($"Excitation '{name}' is switched off and was not translated."); continue; }
            if (x.Objects.Any(_failed.Contains) || x.Faces.Any(f => _failed.Contains(f.Object ?? ""))) continue;
            switch (x.Kind)
            {
                case "Lumped Port" or "Wave Port": Port(x, name); break;
                default:
                    _r.Add(HznRefusalKind.Excitation, x.Kind ?? "(no kind)", "no equivalent in circuitRF", name);
                    break;
            }
        }
        var dup = _doc.Ports.GroupBy(p => p.Number).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) _r.Add(HznRefusalKind.Excitation, $"port number {dup.Key}", "two excitations have it", DesignName);
    }

    private void Port(HznAssignment x, string name)
    {
        bool wave = x.Kind == "Wave Port";
        var props = x.Props;
        int? number = Plain(props["PortNumber"], name) is { } n && n == Math.Floor(n) ? (int)n : null;
        if (number is null) { _r.Add(HznRefusalKind.Excitation, "port number", "it is not a whole number", name); return; }
        string z0 = "50";
        if (wave)
        {
            if (Plain(props["NumberOfModes"] ?? "1", name) is not 1)
            {
                _r.Add(HznRefusalKind.Excitation, "Wave Port with several modes", "one mode is translated", name);
                return;
            }
            if (!IsTrue(props["Renormalize"]))
            {
                _r.Add(HznRefusalKind.Excitation, "Wave Port without renormalisation",
                       "its S-parameters are normalised to the mode's own impedance; circuitRF's port has a stated reference", name);
                return;
            }
            if ((props["Deembed"] is { } de && IsTrue(de)) && Plain(props["DeembedDistance"], name) is not 0)
            {
                _r.Add(HznRefusalKind.Excitation, "Wave Port with de-embedding", "a reference-plane shift is not translated", name);
                return;
            }
            z0 = ImpedanceOhms(props["RenormImpedance"], name) is { } rz ? Num(rz) : "";
        }
        else z0 = ImpedanceOhms(props["Impedance"], name) is { } lz ? Num(lz) : "";
        if (z0.Length == 0) return;

        var port = new C3dPort { Number = number.Value, Name = name, Kind = wave ? Em3dPortKind.Wave : Em3dPortKind.Lumped, Z0 = z0 };
        string? sheetObject = null;
        if (x.Objects.Count == 1 && x.Faces.Count == 0)
        {
            string hz = x.Objects[0];
            if (!_sheets.TryGetValue(hz, out var geom) || geom.Placed)
            {
                _r.Add(HznRefusalKind.Excitation, $"{x.Kind} on '{hz}'",
                       "a port is a rectangle sheet in an axis plane, drawn and not moved or rotated afterwards", name);
                return;
            }
            sheetObject = hz;
            port.Plane = geom.Plane;
            port.Offset = Dbu(geom.Offset);
            PutAll(port.Rect, nameof(C3dRect.Min), geom.Min);
            PutAll(port.Rect, nameof(C3dRect.Size), geom.Size);
            if (!geom.Offset.IsLiteral)
                _notes.Add($"Port '{name}': the sheet's plane offset '{geom.Offset.Text}' is a number in the port (a port's offset does not hold an expression); its rectangle keeps its expressions.");
        }
        else if (wave && x.Objects.Count == 0 && x.Faces.Count == 1)
        {
            string? face = FaceName(x.Faces[0], name);
            if (face is null) return;
            string c3d = _c3dName.GetValueOrDefault(x.Faces[0].Object ?? "", x.Faces[0].Object ?? "");
            if (!_top.TryGetValue(c3d, out var obj) || obj is not C3dBox || Bounds(obj) is not { } box)
            {
                _r.Add(HznRefusalKind.Excitation, "Wave Port on a face of a shape that is not a plain box", "only a box's face is a rectangle circuitRF can state exactly here", name);
                return;
            }
            int a = Array.IndexOf(BoxFaces, face) / 2;
            bool max = Array.IndexOf(BoxFaces, face) % 2 == 1;
            var (u, v, nIdx) = a switch { 0 => (1, 2, 0), 1 => (0, 2, 1), _ => (0, 1, 2) };
            port.Plane = a switch { 0 => C3dPlane.YZ, 1 => C3dPlane.XZ, _ => C3dPlane.XY };
            port.Offset = max ? box.Max[nIdx] : box.Min[nIdx];
            port.Rect = new C3dRect { Min = new C3dPoint2(box.Min[u], box.Min[v]), Size = new C3dPoint2(box.Max[u] - box.Min[u], box.Max[v] - box.Min[v]) };
            _notes.Add($"Port '{name}': the face of '{x.Faces[0].Object}' was found by its signature as {face}; its rectangle is numbers at the box's current size.");
        }
        else
        {
            _r.Add(HznRefusalKind.Excitation, $"{x.Kind} on {x.Objects.Count} object(s) and {x.Faces.Count} face(s)",
                   "one sheet" + (wave ? ", or one face," : "") + " is translated", name);
            return;
        }
        if (sheetObject is not null)
        {
            string c3dSheet = _c3dName.GetValueOrDefault(sheetObject, sheetObject);
            Remove(c3dSheet);
            _sheets.Remove(sheetObject);
        }
        _doc.Ports.Add(port);
        _prov.Ports.Add(new HznPortOrigin(port.Number, name, sheetObject));
        foreach (var (k, v) in props.Names.Select(k => (k, props[k]!)))
            if (k is not ("PortNumber" or "Impedance" or "RenormImpedance" or "Renormalize" or "NumberOfModes" or "Deembed" or "DeembedDistance"))
                _prov.Unused.Add(new HznUnusedSetting($"port {name}", k, v));
    }

    private long Dbu(Dim d) => d.IsLiteral ? d.Dbu : (long)Math.Round(Metres(d) * 1e6 * HznToC3d.DbuPerMicron);

    private double? ImpedanceOhms(string? source, string user)
    {
        if (source is null) { _r.Add(HznRefusalKind.Excitation, "impedance", "it is not stated", user); return null; }
        var e = _expr.Translate(source, user);
        if (e is null) return null;
        UseProjectRefs(e);
        try
        {
            double v = _res!.Evaluate(e.Text, null).AsReal();
            if (e.Refs.Count > 0) _notes.Add($"{user}: the impedance '{source}' is {Num(v)} ohm in the port, which holds a number.");
            return v;
        }
        catch (Exception ex) when (ex is ExpressionException or InvalidCastException)
        {
            _r.Add(HznRefusalKind.Expression, "expression", $"'{source}' does not evaluate: {ex.Message}", user);
            return null;
        }
    }

    // ── boundaries ────────────────────────────────────────────────────────────────────────────────

    private readonly Em3dBoundaryKind?[] _air = new Em3dBoundaryKind?[6];
    private readonly string?[] _airBy = new string?[6];

    private void Boundaries()
    {
        string auto = _design.Json.GetObject("settings")?.GetString("AutoOpen") ?? "Off";
        if (auto != "Off")
            _r.Add(HznRefusalKind.Setting, $"Auto open region '{auto}'", "Hertzon adds its own open boundary; circuitRF states the air box per face", DesignName);
        string solution = _design.Json.GetObject("settings")?.GetString("SolutionType") ?? "Modal";
        if (solution != "Modal")
            _r.Add(HznRefusalKind.Setting, $"solution type '{solution}'", "driven modal S-parameters are translated", DesignName);

        foreach (var b in _design.Boundaries)
        {
            string name = b.Name ?? "";
            if (b.Enabled == false) { _notes.Add($"Boundary '{name}' is switched off and was not translated."); continue; }
            if (b.Objects.Any(_failed.Contains) || b.Faces.Any(f => _failed.Contains(f.Object ?? ""))) continue;
            switch (b.Kind)
            {
                case "Perfect E": Conductor(b, name, finite: false); break;
                case "Finite Conductivity": Conductor(b, name, finite: true); break;
                case "Radiation": Open(b, name, Em3dBoundaryKind.Absorbing, secondOrder: b.Props["Order"] == "Second order"); break;
                case "PML":
                    Open(b, name, Em3dBoundaryKind.Absorbing, secondOrder: false);
                    foreach (string k in b.Props.Names) _prov.Unused.Add(new HznUnusedSetting($"boundary {name}", k, b.Props[k] ?? ""));
                    break;
                case "Symmetry": Symmetry(b, name); break;
                case "Lumped RLC": Lumped(b, name); break;
                default:
                    _r.Add(HznRefusalKind.Boundary, b.Kind ?? "(no kind)", "no equivalent in circuitRF", name);
                    break;
            }
        }
        BuildAirBox();
        // A sheet circuitRF would conduct that Hertzon does not: a sheet is always metal here.
        // A sheet an assignment names is that assignment's business: when it was refused it has its own item.
        var named = _design.Boundaries.Concat(_design.Excitations).SelectMany(a => a.Objects).ToHashSet(StringComparer.Ordinal);
        foreach (var o in _doc.Objects.OfType<C3dSheet>())
            if (o.Model && !named.Contains(_prov.Objects.TryGetValue(o.Name, out var origin) ? origin.HznObject : o.Name)
                && !IsConductorMaterial(o.Material))
                _r.Add(HznRefusalKind.Setting, "sheet that is not a conductor, port or element",
                       "a .c3d sheet always conducts; this one has a non-conducting material and no boundary or excitation turns it into metal", o.Name);
    }

    private void Conductor(HznAssignment b, string name, bool finite)
    {
        var p = b.Props;
        string? sheetMaterial = null;
        double? thicknessUm = null;
        string faceMaterial = "pec";
        if (finite)
        {
            if (IsTrue(p["TwoSided"]))
            {
                _r.Add(HznRefusalKind.Boundary, "Finite Conductivity on both sides", "a .c3d sheet is one thin metal, not a surface lossy on each side", name);
                return;
            }
            if (Plain(p["Roughness"] ?? "0um", name) is not 0)
            {
                _r.Add(HznRefusalKind.Boundary, "Finite Conductivity with roughness", "a conductive face has no roughness", name);
                return;
            }
            if (Plain(p["Permeability"] ?? "1", name) is not 1)
            {
                _r.Add(HznRefusalKind.Boundary, "Finite Conductivity with a permeability other than 1", "a conductive face states a conductivity only", name);
                return;
            }
            double? sigma = IsTrue(p["UseMaterial"]) ? MaterialSigma(p["Material"], name) : Plain(p["Conductivity"], name);
            if (sigma is not > 0) { if (sigma is not null) _r.Add(HznRefusalKind.Boundary, "Finite Conductivity", "its conductivity is not positive", name); return; }
            faceMaterial = ConductiveMaterial(sigma.Value);
            sheetMaterial = faceMaterial;
            if (IsTrue(p["UseThickness"]))
            {
                var t = _expr.Translate(p["Thickness"] ?? "", name);
                if (t is null) return;
                UseProjectRefs(t);
                try { thicknessUm = _res!.Evaluate(t.Text, _siteUnit).AsReal() * 1e6; }
                catch (Exception ex) when (ex is ExpressionException or InvalidCastException)
                {
                    _r.Add(HznRefusalKind.Expression, "expression", $"'{p["Thickness"]}' does not evaluate: {ex.Message}", name);
                    return;
                }
            }
        }
        else
        {
            if (IsTrue(p["InfGroundPlane"]))
            {
                _r.Add(HznRefusalKind.Boundary, "Perfect E as an infinite ground plane", "no equivalent", name);
                return;
            }
            sheetMaterial = "pec";
            Material("pec", name);
        }

        foreach (string hz in b.Objects)
        {
            string c3d = _c3dName.GetValueOrDefault(hz, hz);
            if (c3d == _regionName)
            {
                for (int s = 0; s < 6; s++) SetAir(s, Em3dBoundaryKind.Pec, name);
                continue;
            }
            if (!_top.TryGetValue(c3d, out var obj))
            {
                _r.Add(HznRefusalKind.Boundary, $"{b.Kind} on '{hz}'", "the object was folded into another or is not translated", name);
                continue;
            }
            if (obj is C3dSheet sheet)
            {
                var m = Material(sheetMaterial!, name);
                sheet.Material = m?.Material.Name;
                sheet.Role = m?.Role ?? Em3dRole.Conductor;
                if (thicknessUm is { } t) sheet.ThicknessUm = t;
                _prov.FaceBoundaries.Add(new HznBoundaryOrigin(name, c3d, "(sheet)", null));
                continue;
            }
            if (finite) Material(faceMaterial, name);
            if (IsConductorMaterial(C3dValidation.EffectiveMaterial(obj)))
            {
                _notes.Add($"Boundary '{name}' on '{hz}': a conductor is already bounded by its metal, so nothing was added.");
                continue;
            }
            foreach (string face in obj.FaceNames())
                FaceBoundary(c3d, face, finite, faceMaterial, name, null);
        }
        foreach (var f in b.Faces)
        {
            if (FaceName(f, name) is not { } face) continue;
            string c3d = _c3dName.GetValueOrDefault(f.Object ?? "", f.Object ?? "");
            if (c3d == _regionName)
            {
                SetAir(Array.IndexOf(BoxFaces, face), Em3dBoundaryKind.Pec, name);
                continue;
            }
            if (finite) Material(faceMaterial, name);
            FaceBoundary(c3d, face, finite, faceMaterial, name, (int?)f.Face);
        }
    }

    private double? MaterialSigma(string? material, string user)
    {
        if (material is null) return null;
        var m = Material(material, user);
        if (m is null) return null;
        if (m.Material.Sigma20 is > 0 and var s) return s;
        _r.Add(HznRefusalKind.Boundary, "Finite Conductivity from a material", $"'{material}' states no conductivity", user);
        return null;
    }

    private void FaceBoundary(string c3d, string face, bool finite, string material, string boundary, int? index)
    {
        _doc.FaceBoundaries.Add(new C3dFaceBoundary
        {
            Object = c3d, Face = face, Kind = finite ? Em3dFaceBoundaryKind.Conductive : Em3dFaceBoundaryKind.Pec,
            Material = finite ? material : null,
        });
        _prov.FaceBoundaries.Add(new HznBoundaryOrigin(boundary, c3d, face, index));
    }

    private void Open(HznAssignment b, string name, Em3dBoundaryKind kind, bool secondOrder)
    {
        if (secondOrder)
        {
            _r.Add(HznRefusalKind.Boundary, "Radiation of second order", "circuitRF's absorbing face is first order", name);
            return;
        }
        Air(b, name, kind);
        foreach (string k in b.Props.Names)
            if (b.Kind == "Radiation") _prov.Unused.Add(new HznUnusedSetting($"boundary {name}", k, b.Props[k] ?? ""));
    }

    private void Symmetry(HznAssignment b, string name)
    {
        string type = b.Props["Type"] ?? "";
        var kind = type switch { "Perfect H" => Em3dBoundaryKind.Pmc, "Perfect E" => Em3dBoundaryKind.Pec, _ => (Em3dBoundaryKind?)null };
        if (kind is null)
        {
            _r.Add(HznRefusalKind.Boundary, $"Symmetry of type '{type}'", "only Perfect H and Perfect E symmetry walls are translated", name);
            return;
        }
        if (Plain(b.Props["ImpedanceMultiplier"] ?? "1", name) is not 1)
        {
            _r.Add(HznRefusalKind.Boundary, "Symmetry with an impedance multiplier other than 1", "port impedances are not scaled", name);
            return;
        }
        Air(b, name, kind.Value);
    }

    /// <summary>Boundaries that sit on the model's outer faces are the air box's: whole objects (every face) or faces by signature.</summary>
    private void Air(HznAssignment b, string name, Em3dBoundaryKind kind)
    {
        foreach (string hz in b.Objects)
        {
            string c3d = _c3dName.GetValueOrDefault(hz, hz);
            var frames = Frames(c3d, name);
            if (frames is null) continue;
            foreach (var f in frames) AirFace(c3d, f.Name, kind, name);
        }
        foreach (var f in b.Faces)
            if (FaceName(f, name) is { } face)
                AirFace(_c3dName.GetValueOrDefault(f.Object ?? "", f.Object ?? ""), face, kind, name);
    }

    private void AirFace(string c3d, string face, Em3dBoundaryKind kind, string boundary)
    {
        int slot = Array.IndexOf(BoxFaces, face);
        if (slot < 0)
        {
            _r.Add(HznRefusalKind.Boundary, "boundary on a face that is not a box face", $"face '{face}' of '{c3d}' is not one of the air box's six", boundary);
            return;
        }
        if (c3d != _regionName)
        {
            if (_regionName is not null)
            {
                _r.Add(HznRefusalKind.Boundary, "open boundary inside the region", $"'{c3d}' lies inside the region; the air box is the region", boundary);
                return;
            }
            var content = Content();
            if (content is null || _top.GetValueOrDefault(c3d) is not { } obj || Bounds(obj) is not { } own)
            {
                _r.Add(HznRefusalKind.Boundary, "open boundary on a shape whose extent is not stated", $"'{c3d}'", boundary);
                return;
            }
            int a = slot / 2;
            long at = slot % 2 == 0 ? own.Min[a] : own.Max[a];
            long edge = slot % 2 == 0 ? content.Value.Min[a] : content.Value.Max[a];
            if (at != edge)
            {
                _r.Add(HznRefusalKind.Boundary, "boundary not on the outer extent",
                       $"face {face} of '{c3d}' is inside the model; circuitRF states these on the air box, which is around the model", boundary);
                return;
            }
        }
        SetAir(slot, kind, boundary);
    }

    private void SetAir(int slot, Em3dBoundaryKind kind, string by)
    {
        if (_air[slot] is { } already && already != kind)
        {
            _r.Add(HznRefusalKind.Boundary, "two kinds on one air box face", $"{BoxFaces[slot]} is {already} ({_airBy[slot]}) and {kind} ({by})", by);
            return;
        }
        (_air[slot], _airBy[slot]) = (kind, by);
        _prov.AirBoxFaces.Add((by, BoxFaces[slot]));
    }

    private Layout.Em.CemAirBox? _airBox;

    private void BuildAirBox()
    {
        Layout.Em.CemAirBoxFace Face(int slot)
        {
            double pad = _regionPadding is { } p ? Metres(p[slot]) * 1e6 : 0;
            return new Layout.Em.CemAirBoxFace { PaddingUm = pad, Boundary = _air[slot] ?? Em3dBoundaryKind.Pec };
        }
        _airBox = new Layout.Em.CemAirBox
        {
            XMin = Face(0), XMax = Face(1), YMin = Face(2), YMax = Face(3), ZMin = Face(4), ZMax = Face(5),
        };
        if (_air.All(a => a is null) && _regionName is null)
            _notes.Add("No boundary sits on the model's outer faces: the air box is flush with the model and every face is a perfect conductor, as a closed Hertzon design's outer faces are.");
        if (_regionPadding is { } pd && pd.Any(d => !d.IsLiteral))
            _notes.Add("The air box padding is a number in the setup (a setup holds no expression there); the region's expressions stay in the project.");
    }

    // ── lumped element ────────────────────────────────────────────────────────────────────────────

    private void Lumped(HznAssignment b, string name)
    {
        var p = b.Props;
        string model = p["Model"] ?? "";
        var topology = model switch { "Series RLC" => Em3dRlcTopology.Series, "Parallel RLC" => Em3dRlcTopology.Parallel, _ => (Em3dRlcTopology?)null };
        if (topology is null)
        {
            _r.Add(HznRefusalKind.Boundary, $"Lumped RLC model '{model}'", "only a series or a parallel R, L and C is translated", name);
            return;
        }
        if (!string.IsNullOrEmpty(p["TouchstoneFile"]))
        {
            _r.Add(HznRefusalKind.Boundary, "Lumped element from a Touchstone file", "a mount from data is not translated", name);
            return;
        }
        if (b.Objects.Count != 1 || b.Faces.Count != 0 || !_sheets.TryGetValue(b.Objects[0], out var geom) || geom.Placed)
        {
            _r.Add(HznRefusalKind.Boundary, "Lumped RLC not on one drawn sheet", "the element's gap is the rectangle of a sheet", name);
            return;
        }
        var el = new C3dLumpedElement { Name = name, Plane = geom.Plane, Offset = Dbu(geom.Offset), Topology = topology.Value };
        PutAll(el.Rect, nameof(C3dRect.Min), geom.Min);
        PutAll(el.Rect, nameof(C3dRect.Size), geom.Size);
        string? Part(string use, string key)
        {
            if (!IsTrue(p[use])) return null;
            var e = _expr.Translate(p[key] ?? "", name);
            if (e is null) return null;
            UseProjectRefs(e);
            return e.Text;
        }
        el.R = Part("UseResistance", "Resistance");
        el.L = Part("UseInductance", "Inductance");
        el.C = Part("UseCapacitance", "Capacitance");
        string sheet = b.Objects[0];
        Remove(_c3dName.GetValueOrDefault(sheet, sheet));
        _sheets.Remove(sheet);
        _doc.LumpedElements.Add(el);
        foreach (string k in p.Names)
            if (k is "Component" or "TouchstoneMount" or "IntegrationLine") _prov.Unused.Add(new HznUnusedSetting($"boundary {name}", k, p[k] ?? ""));
    }
}

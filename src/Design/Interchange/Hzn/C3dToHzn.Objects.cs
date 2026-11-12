// HZN-4 R-hzn4-1 — objects: each one a modelling history (a creation step, then the operations that wrap it, innermost
// first, then its placement), a Boolean's tools objects of their own with `consumed_by` set.

using CircuitRF.Design.ThreeD;

namespace CircuitRF.Design.Interchange.Hzn;

internal sealed partial class C3dToHznWriter
{
    /// <summary>One step of a history: the kind, its properties in order, whether it is suppressed.</summary>
    private sealed record HStep(string Kind, List<(string Key, string Value)> Props, bool Suppressed = false);

    /// <summary>One Hertzon object as it will be written.</summary>
    private sealed class HObj
    {
        public required string Name;
        public List<HStep> History = [];
        public string? Material;
        public Engine.Em3d.Em3dRole? Role;
        public bool Model = true, Visible = true, SolveInside = true;
        public string? ConsumedBy, Group;
        public C3dObject? Source;
        public bool IsSheet;
    }

    private readonly List<HObj> _objects = [];
    private readonly Dictionary<string, HObj> _byC3dName = new(StringComparer.Ordinal);

    private static List<(string, string)> P(params (string, string)[] kv) => [.. kv];

    private static string AxisName(C3dAxis a) => a switch { C3dAxis.X => "X", C3dAxis.Y => "Y", _ => "Z" };

    private void Objects()
    {
        foreach (var top in _doc.Objects)
        {
            _user = top.Name;
            switch (top)
            {
                case C3dPolyline:
                    _notes.Add($"'{top.Name}' is construction geometry (a polyline that is not in any solve), so it was not written.");
                    continue;
                case C3dWire:
                    _r.Add(HznRefusalKind.CreationStep, "bond wire", "no defined correspondence between a .c3d wire's points and Hertzon's bondwire profile families", top.Name);
                    continue;
                case C3dStep:
                    _r.Add(HznRefusalKind.CreationStep, "STEP part", "Hertzon's import step is not read back by circuitRF yet", top.Name);
                    continue;
            }
            EmitTop(top);
        }
    }

    private void EmitTop(C3dObject top)
    {
        var h = new HObj { Name = ObjectName(top.Name), Source = top };
        var steps = Steps(top, h);
        if (steps is null) return;
        h.History = steps;
        h.Group = string.IsNullOrEmpty(top.Group) ? null : top.Group;
        h.Visible = !top.Hidden;
        h.Model = top.Model;
        h.IsSheet = top is C3dSheet;
        h.Material = C3dValidation.EffectiveMaterial(top);
        h.Role = C3dValidation.EffectiveRole(top);
        if (top.Transparency is not null || top.Appearance is not null)
            _notes.Add($"'{top.Name}': transparency and appearance are drawing state and were not written.");
        _objects.Add(h);
        _byC3dName[top.Name] = h;
    }

    /// <summary>The history of <paramref name="o"/>: its creation step(s), the wrapping operations, and its placement. A Boolean's tools are
    /// added to <see cref="_objects"/> as objects of their own. Null when something in it cannot be written (already refused).</summary>
    private List<HStep>? Steps(C3dObject o, HObj owner)
    {
        var steps = new List<HStep>();
        string label = string.IsNullOrEmpty(o.Name) ? owner.Name : o.Name;
        _user = label;
        switch (o)
        {
            case C3dBox b:
                steps.Add(new("CreateBox", P(("Position", Dim3(b, nameof(C3dBox.Min), b.Min)),
                                             ("XSize", Dim(b, nameof(C3dBox.Size), 0, b.Size.X)),
                                             ("YSize", Dim(b, nameof(C3dBox.Size), 1, b.Size.Y)),
                                             ("ZSize", Dim(b, nameof(C3dBox.Size), 2, b.Size.Z)))));
                break;
            case C3dCylinder c:
                steps.Add(new("CreateCylinder", P(("CenterPosition", Dim3(c, nameof(C3dCylinder.Base), c.Base)),
                                                  ("Axis", AxisName(c.Axis)),
                                                  ("Radius", Dim(c, nameof(C3dCylinder.Radius), 0, c.Radius)),
                                                  ("Height", Dim(c, nameof(C3dCylinder.Length), 0, c.Length)),
                                                  ("NumberOfSegments", "0"))));
                break;
            case C3dSphere s:
                steps.Add(new("CreateSphere", P(("CenterPosition", Dim3(s, nameof(C3dSphere.Centre), s.Centre)),
                                                ("Radius", Dim(s, nameof(C3dSphere.Radius), 0, s.Radius)),
                                                ("NumberOfVertices", "0"), ("NumberOfSegments", "0"))));
                break;
            case C3dPrism p:
                if (!PrismSteps(p, steps, label)) return null;
                break;
            case C3dSheet sh:
                if (!SheetSteps(sh, steps, label)) return null;
                break;
            case C3dBoolean bo:
                if (!BooleanSteps(bo, owner, steps)) return null;
                break;
            case C3dFillet or C3dChamfer:
                _r.Add(HznRefusalKind.Operation, o is C3dFillet ? "Fillet" : "Chamfer",
                       "Hertzon names the edges by index alone and circuitRF cannot read such a step back, so it is not written", label);
                return null;
            case C3dEllipsoid:
                _r.Add(HznRefusalKind.CreationStep, "ellipsoid", "Hertzon has no primitive that states one exactly", label);
                return null;
            case C3dPolyhedron:
                _r.Add(HznRefusalKind.CreationStep, "polyhedron", "Hertzon has no creation step circuitRF can read back for a faceted solid", label);
                return null;
            case C3dPhantom:
                _r.Add(HznRefusalKind.CreationStep, "phantom", "a layered phantom is not a Hertzon object", label);
                return null;
            case C3dVoxelBody:
                _r.Add(HznRefusalKind.CreationStep, "voxel body", "a segmented voxel model is not a Hertzon object", label);
                return null;
            case C3dMesh:
                _r.Add(HznRefusalKind.CreationStep, "mesh body", "a triangle-surface tissue is not a Hertzon object", label);
                return null;
            case C3dCylinderSheet:
                _r.Add(HznRefusalKind.CreationStep, "cylinder sheet", "Hertzon has no sheet on a cylinder that circuitRF can read back", label);
                return null;
            default:
                _r.Add(HznRefusalKind.CreationStep, C3dObject.KindOf(o), "no Hertzon equivalent", label);
                return null;
        }
        if (o is not C3dBoolean) steps.AddRange(Placement(o.Placement));
        return steps;
    }

    // ── shapes ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The 3D point of a plane's (u, v) at an offset, in the plane's axis order.</summary>
    private static string Point(C3dPlane plane, string u, string v, string n) => plane switch
    {
        C3dPlane.XY => $"{u},{v},{n}",
        C3dPlane.YZ => $"{n},{u},{v}",
        _ => $"{u},{n},{v}",
    };

    private bool PrismSteps(C3dPrism p, List<HStep> steps, string label)
    {
        if (p.Shear != default)
        {
            _r.Add(HznRefusalKind.CreationStep, "oblique prism", "a sheared prism is a swept polyline Hertzon cannot be read back as", label);
            return false;
        }
        if (p.Holes.Count > 0)
        {
            _r.Add(HznRefusalKind.CreationStep, "prism with holes", "a polyline with holes is not read back by circuitRF", label);
            return false;
        }
        var (_, _, n) = Axes(p.Plane);
        string at = Frozen(p, nameof(C3dPrism.Offset), p.Offset, $"Prism '{label}' offset");
        string pts = string.Join(";", p.Outline.Select(q => Point(p.Plane, Lit(q.U), Lit(q.V), at)));
        steps.Add(new("CreatePolyline", P(("Points", pts), ("SegmentTypes", ""), ("Closed", "True"), ("Covered", "True"), ("Holes", ""))));
        string h = Dim(p, nameof(C3dPrism.Height), 0, p.Height);
        string[] v = ["0", "0", "0"];
        v[n] = h;
        steps.Add(new("SweepAlongVector", P(("Vector", string.Join(",", v)))));
        return true;
    }

    private bool SheetSteps(C3dSheet s, List<HStep> steps, string label)
    {
        var (u, v, n) = Axes(s.Plane);
        string[] sizeKeys = ["XSize", "YSize", "ZSize"];
        if (s.Rect is { } r)
        {
            var pos = new string[3];
            pos[n] = Dim(s, nameof(C3dSheet.Offset), 0, s.Offset);
            pos[u] = Dim(r, nameof(C3dRect.Min), 0, r.Min.U);
            pos[v] = Dim(r, nameof(C3dRect.Min), 1, r.Min.V);
            var props = P(("Position", string.Join(",", pos)), ("Axis", AxisName((C3dAxis)n)));
            var sizes = new string[3];
            sizes[u] = Dim(r, nameof(C3dRect.Size), 0, r.Size.U);
            sizes[v] = Dim(r, nameof(C3dRect.Size), 1, r.Size.V);
            sizes[n] = "0";
            for (int i = 0; i < 3; i++) props.Add((sizeKeys[i], sizes[i]));
            steps.Add(new("CreateRectangle", props));
            return true;
        }
        if (s.Holes.Count > 0)
        {
            _r.Add(HznRefusalKind.CreationStep, "sheet with holes", "a polyline with holes is not read back by circuitRF", label);
            return false;
        }
        if (s.Outline.Count < 3)
        {
            _r.Add(HznRefusalKind.CreationStep, "sheet without an outline", "it has neither a rectangle nor three points", label);
            return false;
        }
        string at = Frozen(s, nameof(C3dSheet.Offset), s.Offset, $"Sheet '{label}' offset");
        string pts = string.Join(";", s.Outline.Select(q => Point(s.Plane, Lit(q.U), Lit(q.V), at)));
        steps.Add(new("CreatePolyline", P(("Points", pts), ("SegmentTypes", ""), ("Closed", "True"), ("Covered", "True"), ("Holes", ""))));
        return true;
    }

    // ── operations ────────────────────────────────────────────────────────────────────────────────

    private bool BooleanSteps(C3dBoolean bo, HObj owner, List<HStep> steps)
    {
        if (bo.Blank is not { } blank)
        {
            _r.Add(HznRefusalKind.Operation, "Boolean without a blank", "there is nothing for the tools to act on", owner.Name);
            return false;
        }
        if (blank is C3dSheet or C3dPolyline)
        {
            _r.Add(HznRefusalKind.Operation, "Boolean on a sheet", "a Boolean has solids as operands", owner.Name);
            return false;
        }
        if (bo.KeepTools && bo.Op != C3dBooleanOp.Subtract)
        {
            _r.Add(HznRefusalKind.Operation, "Boolean keeping its tools", "only a Subtract keeps them", owner.Name);
            return false;
        }
        var inner = Steps(blank, owner);
        if (inner is null) return false;
        steps.AddRange(inner);

        bool ok = true;
        var toolNames = new List<string>();
        foreach (var tool in bo.Tools)
        {
            if (tool is C3dSheet or C3dPolyline)
            {
                _r.Add(HznRefusalKind.Operation, $"{bo.Op} with a sheet tool", "a Boolean has solids as operands", owner.Name);
                ok = false;
                continue;
            }
            var th = new HObj { Name = ObjectName(tool.Name), Source = tool };
            var tsteps = Steps(tool, th);
            if (tsteps is null) { ok = false; continue; }
            th.History = tsteps;
            th.Material = C3dValidation.EffectiveMaterial(tool);
            th.Role = C3dValidation.EffectiveRole(tool);
            th.ConsumedBy = bo.Enabled && !bo.KeepTools ? owner.Name : null;
            _objects.Add(th);
            if (tool.Name.Length > 0) _byC3dName[tool.Name] = th;
            toolNames.Add(th.Name);
        }
        _user = owner.Name;
        if (!ok) return false;
        if (toolNames.Count == 0)
        {
            _r.Add(HznRefusalKind.Operation, "Boolean without a tool", "it has nothing to combine", owner.Name);
            return false;
        }
        string kind = bo.Op switch { C3dBooleanOp.Subtract => "Subtract", C3dBooleanOp.Unite => "Unite", _ => "Intersect" };
        steps.Add(new(kind, P(("Tools", string.Join(",", toolNames)), ("KeepOriginals", bo.KeepTools ? "True" : "False")), Suppressed: !bo.Enabled));
        steps.AddRange(Placement(bo.Placement));
        return true;
    }

    // ── placement ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A placement as steps in the order the reader folds them: mirror first, then each rotation about the origin, then the move.</summary>
    private List<HStep> Placement(C3dPlacement pl)
    {
        var steps = new List<HStep>();
        if (pl.MirrorX) steps.Add(new("Mirror", P(("Base", "0,0,0"), ("Normal", "1,0,0"))));
        for (int k = 0; k < pl.Rotate.Count; k++)
        {
            var rot = pl.Rotate[k];
            string angle;
            if (C3dBindings.GetExpr(rot, nameof(C3dRotation.Deg), 0) is { } e)
                angle = _x.Write(e.Expr, e.Unit ?? C3dUnits.Degrees, _user)?.Text ?? "0deg";
            else angle = HznExpressionWriter.Digits(rot.Deg) + "deg";
            steps.Add(new("Rotate", P(("Axis", AxisName(rot.Axis)), ("Angle", angle), ("Center", "0,0,0"), ("CoordinateSystem", "Global"))));
        }
        bool moves = pl.Origin != default || (pl.Exprs?.TryGetValue(nameof(C3dPlacement.Origin), out var ex) == true && ex.Any(c => c is not null));
        if (moves) steps.Add(new("Move", P(("Vector", Dim3(pl, nameof(C3dPlacement.Origin), pl.Origin)))));
        return steps;
    }
}

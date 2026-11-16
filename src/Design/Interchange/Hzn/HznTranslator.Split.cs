// brief-hzn-21 R-hzn21-4 — Hertzon's Split step as a .c3d Split.
//
// From Hertzon's documentation (feature matrix, changelog 0.1.26, tutorial §3.6), never its source: a Split cuts an object at the plane
// XY, YZ or XZ — or a Custom plane from Base and Normal — taken in the step's CoordinateSystem, and keeps the Positive or Negative side.
// A named plane passes through that system's origin, so Base and Normal describe only the Custom plane (Hertzon's own schema: "used
// if Plane == Custom"; Keep is Positive or Negative and nothing else). Positive keeps the side the Normal points to: for a named
// plane it is the side its axis points to (tutorial §3.6: YZ, Positive keeps x ≥ 0), and a Custom plane's Normal is that axis. A system is translated when
// it is the Global one, or a Relative one whose axes are the global axes and whose references end at Global: the plane is then the
// one through its origin, and At is that origin's coordinate along the normal, as the file spells it.

using CircuitRF.Design.ThreeD;

namespace CircuitRF.Design.Interchange.Hzn;

internal sealed partial class HznTranslator
{
    private bool Split(State st, Dictionary<string, string> p, string user)
    {
        string name = user.Split(' ')[0];
        if (!NeedRoot(st, "Split", name)) return false;
        if (st.Root is C3dPolyline || C3dSheetBooleans.IsSheet(st.Root!))
        {
            _r.Add(HznRefusalKind.Operation, "Split of a sheet", "circuitRF splits solids; a sheet is cut by a boolean of sheets", name);
            return false;
        }
        string planeText = (p.GetValueOrDefault("Plane") ?? "").Trim();
        C3dSplitPlane? plane = planeText.ToUpperInvariant() switch
        {
            "YZ" or "ZY" => C3dSplitPlane.YZ, "XZ" or "ZX" => C3dSplitPlane.XZ, "XY" or "YX" => C3dSplitPlane.XY,
            "CUSTOM" => C3dSplitPlane.Custom, _ => null,
        };
        string keepText = (p.GetValueOrDefault("Keep") ?? "").Trim();
        C3dSplitKeep? keep = keepText switch { "Positive" => C3dSplitKeep.Positive, "Negative" => C3dSplitKeep.Negative, _ => null };
        bool ok = true;
        if (plane is null)
        {
            _r.Add(HznRefusalKind.Operation, $"Split on the plane '{planeText}'", "a Split's plane is XY, YZ, XZ or Custom", name);
            ok = false;
        }
        if (keep is null)
        {
            _r.Add(HznRefusalKind.Operation, $"Split keeping '{keepText}'", "a .c3d Split keeps one side, Positive or Negative", name);
            ok = false;
        }
        Dim[]? custom = null;
        double[]? normal = null;
        if (ok && plane == C3dSplitPlane.Custom)
        {
            custom = Triple(p.GetValueOrDefault("Base") ?? "0,0,0", user);
            var parts = HznExpressionTranslator.SplitTopLevel(p.GetValueOrDefault("Normal") ?? "0,0,1", ',');
            var n = parts.Count == 3 ? parts.Select(t => Plain(t, user)).ToArray() : [];
            if (n.Length != 3 || n.Any(v => v is null) || n.All(v => v == 0))
            {
                _r.Add(HznRefusalKind.Operation, "Split on a Custom plane", $"its Normal '{p.GetValueOrDefault("Normal")}' is not three numbers, not all zero", name);
                ok = false;
            }
            else normal = [n[0]!.Value, n[1]!.Value, n[2]!.Value];
            if (custom is null) ok = false;
        }
        Dim at = Dim.Zero;
        if (ok && !CoordGlobal(p))
        {
            if (AlignedOrigin(p["CoordinateSystem"], name) is not { } origin) return false;
            if (custom is not null) custom = [Add(custom[0], origin[0]), Add(custom[1], origin[1]), Add(custom[2], origin[2])];
            else at = origin[plane switch { C3dSplitPlane.YZ => 0, C3dSplitPlane.XZ => 1, _ => 2 }];
        }
        if (!ok) return false;
        FlushPlacement(st, name);
        var target = st.Root!;
        target.Name = "";
        var split = new C3dSplit { Plane = plane!.Value, Keep = keep!.Value, Target = target, At = at.Dbu, Normal = normal };
        if (custom is not null)
        {
            split.Base = new C3dPoint3(custom[0].Dbu, custom[1].Dbu, custom[2].Dbu);
            PutAll(split, nameof(C3dSplit.Base), custom);
        }
        else Put(split, nameof(C3dSplit.At), 0, at);
        st.Root = split;
        st.Sheet = null;
        return true;
    }

    /// <summary>The Global coordinates of <paramref name="cs"/>'s origin, when its axes and every one it is placed in are the global
    /// axes; null (refused) otherwise.</summary>
    private Dim[]? AlignedOrigin(string cs, string user)
    {
        var origin = new[] { Dim.Zero, Dim.Zero, Dim.Zero };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string at = cs;
        while (at is not ("" or "Global"))
        {
            if (!seen.Add(at))
            {
                _r.Add(HznRefusalKind.Operation, "Split in a coordinate system", $"'{cs}' is placed in itself through '{at}'", user);
                return null;
            }
            var sys = _design.CoordSystems.FirstOrDefault(c => c.Name == at);
            if (sys is null)
            {
                _r.Add(HznRefusalKind.Operation, "Split in a coordinate system", $"'{at}' is not a coordinate system of the design", user);
                return null;
            }
            if (sys.Kind != "Relative" || !AxesAreGlobal(sys))
            {
                _r.Add(HznRefusalKind.Operation, "Split in a coordinate system",
                       $"'{at}' is {(sys.Kind == "Relative" ? "turned from the global axes" : $"a {sys.Kind} system")}; a Split is translated in the Global " +
                       "system or a Relative one with the global axes", user);
                return null;
            }
            if (Triple(sys.Props["Origin"] ?? "0,0,0", user) is not { } o) return null;
            origin = [Add(origin[0], o[0]), Add(origin[1], o[1]), Add(origin[2], o[2])];
            at = sys.Reference ?? "Global";
        }
        return origin;
    }

    /// <summary>A Relative system's axes are the global ones: Axis/Position with its x axis along +x and its y point on +y, or Euler
    /// angles that are all zero.</summary>
    private static bool AxesAreGlobal(HznCoordSystem sys)
    {
        string mode = sys.Props["Mode"] ?? "Axis/Position";
        static double[]? Literal(string? t)
        {
            var parts = (t ?? "").Split(',');
            if (parts.Length != 3) return null;
            var v = new double[3];
            for (int k = 0; k < 3; k++)
                if (!double.TryParse(parts[k].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v[k])) return null;
            return v;
        }
        static bool ZeroAngle(string? t) => (t ?? "0").Trim() is "0" or "0deg" or "0rad" or "0.0" or "0.0deg";
        if (mode.StartsWith("Euler", StringComparison.Ordinal))
            return ZeroAngle(sys.Props["Phi"]) && ZeroAngle(sys.Props["Theta"]) && ZeroAngle(sys.Props["Psi"]);
        if (mode != "Axis/Position") return false;
        var x = Literal(sys.Props["XAxis"] ?? "1,0,0");
        var y = Literal(sys.Props["YPoint"] ?? "0,1,0");
        return x is [> 0, 0, 0] && y is [0, > 0, 0];
    }
}

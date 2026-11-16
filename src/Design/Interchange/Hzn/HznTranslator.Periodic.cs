// brief-hzn-16 — a periodic unit cell: Hertzon's Primary and Secondary boundaries as the air box's periodic pairs and its scan, and
// its Floquet ports as .c3d Floquet ports over a whole z face of the cell.

using System.Globalization;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Interchange.Hzn;

internal sealed partial class HznTranslator
{
    /// <summary>The air box faces each Primary or Secondary boundary sits on, by boundary name.</summary>
    private readonly Dictionary<string, List<int>> _periodicSlots = new(StringComparer.Ordinal);

    /// <summary>Each Secondary's Primary, by the Secondary's name.</summary>
    private readonly Dictionary<string, string> _secondaryOf = new(StringComparer.Ordinal);

    /// <summary>The scan every Secondary states, and the boundary that stated it first.</summary>
    private (Em3dScan Scan, string By)? _scan;

    private bool Periodic => _design.Boundaries.Any(b => b.Kind is "Primary" or "Secondary");

    /// <summary>A Primary or Secondary boundary: the outer face of the cell it sits on becomes a periodic face of the air box; a sheet
    /// drawn there to carry it (Hertzon draws one per face) is not geometry and is left out.</summary>
    private void PeriodicFace(HznAssignment b, string name, bool secondary)
    {
        var slots = new List<int>();
        foreach (string hz in b.Objects)
        {
            string c3d = _c3dName.GetValueOrDefault(hz, hz);
            if (_sheets.TryGetValue(hz, out var geom) && !geom.Placed && geom.Angle == 0 && geom.Corners is null)
            {
                if (SheetFace(c3d, geom) is not { } face)
                {
                    _r.Add(HznRefusalKind.Boundary, $"{b.Kind} not on the cell's outer face", $"'{hz}' does not lie on a face of the model's extent", name);
                    continue;
                }
                AirFace(c3d, face, Em3dBoundaryKind.Periodic, name);
                slots.Add(Array.IndexOf(BoxFaces, face));
                Remove(c3d);
                _sheets.Remove(hz);
                continue;
            }
            var frames = Frames(c3d, name);
            if (frames is null) continue;
            foreach (var f in frames)
            {
                AirFace(c3d, f.Name, Em3dBoundaryKind.Periodic, name);
                slots.Add(Array.IndexOf(BoxFaces, f.Name));
            }
        }
        foreach (var f in b.Faces)
            if (FaceName(f, name) is { } face)
            {
                AirFace(_c3dName.GetValueOrDefault(f.Object ?? "", f.Object ?? ""), face, Em3dBoundaryKind.Periodic, name);
                slots.Add(Array.IndexOf(BoxFaces, face));
            }
        _periodicSlots[name] = slots;
        foreach (string k in new[] { "UVector" })
            if (b.Props[k] is { } v) _prov.Unused.Add(new HznUnusedSetting($"boundary {name}", k, v));
        if (!secondary) return;

        _secondaryOf[name] = b.Props["Primary"] ?? "";
        string mode = b.Props["PhaseMode"] ?? "Scan Angles";
        Em3dScan? scan = null;
        if (mode.Equals("Scan Angles", StringComparison.OrdinalIgnoreCase))
        {
            if (ScanDeg(b.Props["ScanTheta"] ?? "0deg", name) is { } theta && ScanDeg(b.Props["ScanPhi"] ?? "0deg", name) is { } phi)
                scan = new Em3dScan(theta, phi);
            if (b.Props["PhaseDelay"] is { } pd) _prov.Unused.Add(new HznUnusedSetting($"boundary {name}", "PhaseDelay", pd));
        }
        else
        {
            _r.Add(HznRefusalKind.Boundary, $"Secondary with phase mode '{mode}'", "only a phase from scan angles is translated", name);
            return;
        }
        if (scan is null) return;
        if (_scan is { } first && (Math.Abs(first.Scan.ThetaDeg - scan.ThetaDeg) > 1e-9 || Math.Abs(first.Scan.PhiDeg - scan.PhiDeg) > 1e-9))
        {
            _r.Add(HznRefusalKind.Boundary, "two scans", $"'{first.By}' scans to ({Deg(first.Scan.ThetaDeg)}°, {Deg(first.Scan.PhiDeg)}°) and '{name}' to " +
                   $"({Deg(scan.ThetaDeg)}°, {Deg(scan.PhiDeg)}°); a cell has one scan", name);
            return;
        }
        _scan ??= (scan, name);
    }

    private static string Deg(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A scan angle in degrees, a number in the setup (noted when it named variables).</summary>
    private double? ScanDeg(string source, string user)
    {
        if (Angle(source, user) is not { } a) return null;
        if (a.Text is null) return a.Deg;
        try
        {
            double deg = _res!.Evaluate(a.Text, null).AsReal() * 180 / Math.PI;
            _notes.Add($"{user}: the scan angle '{source}' is {Deg(deg)}° in the setup, which holds a number.");
            return deg;
        }
        catch (Exception ex) when (ex is CircuitRF.Core.Expressions.ExpressionException or InvalidCastException)
        {
            _r.Add(HznRefusalKind.Expression, "expression", $"'{source}' does not evaluate: {ex.Message}", user);
            return null;
        }
    }

    /// <summary>The air box face an axis-plane sheet lies on (it lies in the plane of the model's extent there), or null.</summary>
    private string? SheetFace(string c3d, SheetGeom geom)
    {
        if (Content(modelOnly: true) is not { } content || _top.GetValueOrDefault(c3d) is not { } obj || Bounds(obj) is not { } own) return null;
        int axis = geom.Plane switch { C3dPlane.YZ => 0, C3dPlane.XZ => 1, _ => 2 };
        if (own.Min[axis] != own.Max[axis]) return null;
        if (own.Min[axis] == content.Min[axis]) return "xyz"[axis] + "min";
        if (own.Max[axis] == content.Max[axis]) return "xyz"[axis] + "max";
        return null;
    }

    /// <summary>Each Secondary names a Primary on the opposite face of the same axis, and every Primary has its Secondary.</summary>
    private void CheckPeriodicPairs()
    {
        var paired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (secondary, primary) in _secondaryOf)
        {
            if (!_periodicSlots.TryGetValue(primary, out var ps))
            {
                _r.Add(HznRefusalKind.Boundary, "Secondary without its Primary", $"it names '{primary}', which is no Primary boundary of the design", secondary);
                continue;
            }
            paired.Add(primary);
            var ss = _periodicSlots[secondary];
            if (ps.Count != 1 || ss.Count != 1 || ps[0] / 2 != ss[0] / 2 || ps[0] == ss[0])
                _r.Add(HznRefusalKind.Boundary, "Primary and Secondary not on opposite faces",
                       $"'{primary}' and '{secondary}' do not lie on the two opposite faces of one axis of the cell", secondary);
        }
        foreach (var primary in _periodicSlots.Keys.Where(k => !_secondaryOf.ContainsKey(k) && !paired.Contains(k)))
            _r.Add(HznRefusalKind.Boundary, "Primary without a Secondary", "no Secondary boundary names it", primary);
    }

    // ── Floquet ports ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A Floquet port: a wave port over the whole zmin or zmax face of the cell (drawn as a sheet there, or named as a face of the
    /// box that bounds the cell), with its number of modes and its medium. Its numbers are set once every port is read
    /// (<see cref="NumberFloquetPorts"/>).
    /// </summary>
    private void FloquetPort(HznAssignment x, string name)
    {
        var props = x.Props;
        int? number = Plain(props["PortNumber"], name) is { } n && n == Math.Floor(n) ? (int)n : null;
        if (number is null) { _r.Add(HznRefusalKind.Excitation, "port number", "it is not a whole number", name); return; }
        int modes = Plain(props["NumberOfModes"] ?? "2", name) is { } m && m == Math.Floor(m) && m >= 1 ? (int)m : 0;
        if (modes < 1) { _r.Add(HznRefusalKind.Excitation, "Floquet Port modes", "NumberOfModes is not a whole number of 1 or more", name); return; }
        double er = Plain(props["Permittivity"] ?? "1", name) ?? double.NaN;
        if (!(er >= 1)) { _r.Add(HznRefusalKind.Excitation, "Floquet Port permittivity", "it is not a number of 1 or more", name); return; }

        string? face = null;
        string? sheetObject = null;
        if (x.Objects.Count == 1 && x.Faces.Count == 0 && _sheets.TryGetValue(x.Objects[0], out var geom) && !geom.Placed && geom.Angle == 0 && geom.Corners is null)
        {
            sheetObject = x.Objects[0];
            face = SheetFace(_c3dName.GetValueOrDefault(sheetObject, sheetObject), geom);
        }
        else if (x.Objects.Count == 0 && x.Faces.Count == 1)
            face = FaceName(x.Faces[0], name);
        if (face is not ("zmin" or "zmax") || Content(modelOnly: true) is not { } content)
        {
            _r.Add(HznRefusalKind.Excitation, "Floquet Port not on the cell's z face",
                   "a Floquet port is one sheet or one face covering the zmin or zmax face of the cell", name);
            return;
        }
        var port = new C3dPort
        {
            Number = number.Value, Name = name, Kind = Em3dPortKind.Wave, Plane = C3dPlane.XY,
            Offset = face == "zmin" ? content.Min[2] : content.Max[2],
            Rect = new C3dRect { Min = new C3dPoint2(content.Min[0], content.Min[1]), Size = new C3dPoint2(content.Max[0] - content.Min[0], content.Max[1] - content.Min[1]) },
            Z0 = C3dPorts.ModeZ0,
            Floquet = new C3dFloquet { Modes = modes, Er = er == 1 ? 0 : er },
        };
        if (sheetObject is not null)
        {
            Remove(_c3dName.GetValueOrDefault(sheetObject, sheetObject));
            _sheets.Remove(sheetObject);
        }
        _doc.Ports.Add(port);
        _prov.Ports.Add(new HznPortOrigin(port.Number, name, sheetObject));
        _floquetPorts.Add(port);
        foreach (var k in props.Names.Where(k => k is not ("PortNumber" or "NumberOfModes" or "Permittivity")))
            _prov.Unused.Add(new HznUnusedSetting($"port {name}", k, props[k] ?? ""));
    }

    private readonly List<C3dPort> _floquetPorts = [];

    /// <summary>
    /// A Floquet port's modes are ports of the result, numbered one after another: the port with the lowest PortNumber takes 1 … its
    /// modes, the next the numbers after (Hertzon's own Touchstone, where its FSS reports S(3,1) for port 2's first mode). Floquet
    /// ports beside other ports are refused: how Hertzon numbers them together is not established.
    /// </summary>
    private void NumberFloquetPorts()
    {
        if (_floquetPorts.Count == 0) return;
        if (_doc.Ports.Count != _floquetPorts.Count)
        {
            _r.Add(HznRefusalKind.Excitation, "Floquet Port beside other ports", "how Hertzon numbers a Floquet port's modes among other ports is not established",
                   _floquetPorts[0].Name);
            return;
        }
        int next = 1;
        foreach (var p in _floquetPorts.OrderBy(p => p.Number))
        {
            int own = p.Number;
            p.Number = next;
            var origin = _prov.Ports.FindIndex(o => o.Number == own && o.Excitation == p.Name);
            if (origin >= 0) _prov.Ports[origin] = _prov.Ports[origin] with { Number = next };
            if (own != next || p.Floquet!.Modes > 1)
                _notes.Add($"Floquet port '{p.Name}' (PortNumber {own}) is ports {next}–{next + p.Floquet!.Modes - 1} of the result, one per mode, as Hertzon numbers them.");
            next += p.Floquet!.Modes;
        }
    }
}

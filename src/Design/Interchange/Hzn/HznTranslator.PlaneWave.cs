// brief-hzn-12 R-hzn12-5 — an Incident Plane Wave: a plane-wave record of the 3D view (`.c3d` `PlaneWaves`, as Hertzon's are
// excitations of the design), its name and enabled flag kept, and the Infinite Sphere its radar cross-section is read on. Translated from the vectors, never the angles: the files carry both, and only
// the vectors' meaning is certain from the files alone. What each key means comes from Hertzon's files, its documentation and
// black-box runs (brief-hzn-0 D1).

using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Design.ThreeD;

namespace CircuitRF.Design.Interchange.Hzn;

internal sealed partial class HznTranslator
{

    /// <summary>The plane-wave props the translation reads; every other one is listed as unused.</summary>
    private static readonly HashSet<string> UsedPlaneWaveKeys = new(StringComparer.Ordinal)
    {
        "VectorInput", "KVector", "E0Vector", "EMagnitude", "ZeroPhasePosition",
        // the spherical spelling of the same wave: not read, since the Cartesian one is (see the file header)
        "Theta", "Phi", "Polarization",
    };

    private int _enabledWaves;

    private void PlaneWave(HznAssignment x, string name)
    {
        bool enabled = x.Enabled != false;
        if (enabled && ++_enabledWaves > 1)
        {
            _r.Add(HznRefusalKind.Excitation, "several enabled Incident Plane Waves", "one plane wave at a time drives circuitRF's run; disable all but one", name);
            return;
        }
        var p = x.Props;
        string input = p["VectorInput"] ?? "Cartesian";
        if (input != "Cartesian")
        {
            _r.Add(HznRefusalKind.Excitation, $"Incident Plane Wave with VectorInput '{input}'",
                   "only the Cartesian form (KVector and E0Vector) is translated: its meaning is certain from the file", name);
            return;
        }
        var k = Vector(p["KVector"], "KVector", name);
        var e = Vector(p["E0Vector"], "E0Vector", name);
        double? e0 = p["EMagnitude"] is { } mag ? PlainNumber(mag, "EMagnitude", name) : 1;
        var origin = Triple(p["ZeroPhasePosition"] ?? "0,0,0", $"{name} (ZeroPhasePosition)");
        if (k is null || e is null || e0 is null || origin is null) return;
        var o = origin.Select(d => Metres(d) * 1e6 * HznToC3d.DbuPerMicron).ToArray();   // DBU
        if (o.Any(double.IsNaN)) return;
        var check = CircuitRF.Engine.Em3d.Em3dPlaneWave.From(new(k[0], k[1], k[2]), new(e[0], e[1], e[2]), e0.Value, default, out string? why);
        if (check is null)
        {
            _r.Add(HznRefusalKind.Excitation, "Incident Plane Wave", why!, name);
            return;
        }
        long R(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);
        _doc.PlaneWaves.Add(new C3dPlaneWave
        {
            Name = name, Enabled = enabled, Direction = k, EField = e, E0 = e0.Value == 1 ? null : e0.Value,
            PhaseOrigin = new C3dPoint3(R(o[0]), R(o[1]), R(o[2])),
        });
        if (x.Objects.Count > 0)
            _notes.Add($"Plane wave '{name}' lists {string.Join(", ", x.Objects.Select(n => $"'{n}'"))} as its objects; the wave fills the whole " +
                       "model either way, so the list is not part of circuitRF's 3D view.");
        foreach (string key in p.Names)
            if (!UsedPlaneWaveKeys.Contains(key)) _prov.Unused.Add(new HznUnusedSetting($"plane wave {name}", key, p[key] ?? ""));
        if (_doc.PlaneWaves.Count == 1)
        {
            if (_design.Json.GetObject("settings") is { } settings && settings.GetString("FieldType") is { } fieldType)
                _prov.Unused.Add(new HznUnusedSetting($"design {DesignName}", "FieldType", fieldType));
            Spheres(name);
        }
        else if (_sphereSteps is { } steps) (_doc.PlaneWaves[^1].ThetaStepDeg, _doc.PlaneWaves[^1].PhiStepDeg) = steps;
    }

    /// <summary>The Infinite Sphere's steps, when they are not the defaults: every wave of the design takes them.</summary>
    private (double?, double?)? _sphereSteps;

    /// <summary>A direction or polarisation: three plain numbers. One built from a named variable is refused, since a setup holds
    /// numbers and could not follow it.</summary>
    private double[]? Vector(string? source, string key, string user)
    {
        if (source is null) { _r.Add(HznRefusalKind.Excitation, $"Incident Plane Wave without {key}", "it is not stated", user); return null; }
        var parts = HznExpressionTranslator.SplitTopLevel(source, ',');
        if (parts.Count != 3)
        {
            _r.Add(HznRefusalKind.Excitation, $"Incident Plane Wave {key}", $"'{source}' is not three comma-separated values", user);
            return null;
        }
        var v = new double[3];
        for (int i = 0; i < 3; i++)
            if (PlainNumber(parts[i], key, user) is { } n) v[i] = n;
            else return null;
        return v;
    }

    private double? PlainNumber(string source, string key, string user)
    {
        var t = _expr.Translate(source, user);
        if (t is null) return null;
        if (t.Refs.Count > 0)
        {
            _r.Add(HznRefusalKind.Expression, $"Incident Plane Wave {key} built from named variables",
                   $"'{source}' uses {string.Join(", ", t.Refs)}; a setup holds numbers, so it could not follow the variable", user);
            return null;
        }
        return Plain(source, user);
    }

    private static readonly Regex Degrees = new(@"^\s*([-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)\s*(deg)?\s*$", RegexOptions.Compiled);

    /// <summary>The design's Infinite Sphere: the radar cross-section grid's steps. Only a sphere over the whole of θ 0–180° and
    /// φ 0–360° is translated, since circuitRF's grid always covers it.</summary>
    private void Spheres(string user)
    {
        var spheres = _design.Json.GetArray("spheres")?.Items.OfType<HznJsonObject>().ToList() ?? [];
        if (spheres.Count == 0)
        {
            _notes.Add($"The design has no Infinite Sphere, so the radar cross-section is read every {CircuitRF.Engine.Em3d.Em3dPlaneWave.DefaultStepDeg}° " +
                       "in θ and φ.");
            return;
        }
        var s = new HznStringMap(spheres[0], "props");
        string name = spheres[0].GetString("name") ?? "Infinite Sphere";
        double? Deg(string key)
        {
            if (s[key] is not { } text) return null;
            var m = Degrees.Match(text);
            if (!m.Success)
            {
                _r.Add(HznRefusalKind.Setting, $"Infinite Sphere {key} '{text}'", "only an angle in degrees is translated", name);
                return null;
            }
            return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        double? t0 = Deg("ThetaStart"), t1 = Deg("ThetaStop"), dt = Deg("ThetaStep");
        double? p0 = Deg("PhiStart"), p1 = Deg("PhiStop"), dp = Deg("PhiStep");
        if (s["CoordinateSystem"] is { } cs && cs != "Global")
            _r.Add(HznRefusalKind.Setting, $"Infinite Sphere in coordinate system '{cs}'", "only the global one is translated", name);
        if (t0 is null || t1 is null || dt is null || p0 is null || p1 is null || dp is null) return;
        if (t0 != 0 || t1 != 180 || p0 != 0 || p1 != 360)
        {
            _r.Add(HznRefusalKind.Setting, "Infinite Sphere that does not cover the full sphere",
                   $"θ {t0}–{t1}° and φ {p0}–{p1}°; circuitRF's radar cross-section grid covers θ 0–180° and φ 0–360°", name);
            return;
        }
        if (!(dt > 0 && dt <= 90) || !(dp > 0 && dp <= 90))
        {
            _r.Add(HznRefusalKind.Setting, "Infinite Sphere step", $"θ step {dt}° and φ step {dp}° must each be more than 0 and at most 90", name);
            return;
        }
        double? ts = dt.Value != CircuitRF.Engine.Em3d.Em3dPlaneWave.DefaultStepDeg ? dt.Value : null;
        double? ps = dp.Value != CircuitRF.Engine.Em3d.Em3dPlaneWave.DefaultStepDeg ? dp.Value : null;
        _sphereSteps = (ts, ps);
        (_doc.PlaneWaves[^1].ThetaStepDeg, _doc.PlaneWaves[^1].PhiStepDeg) = (ts, ps);
        if (spheres.Count > 1)
            _notes.Add($"The design has {spheres.Count} Infinite Spheres; the radar cross-section is read on the first, '{name}'.");
    }
}

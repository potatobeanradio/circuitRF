// brief-hzn-12 R-hzn12-5 (HZN-4) — the 3D view's plane waves, written as Hertzon writes them: an Incident Plane Wave excitation of the
// design in its Cartesian form for each, its name and enabled flag kept, and an Infinite Sphere over the whole sphere at the enabled
// one's far-field steps. Both are the design's, as the view's waves are the view's.

using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;

namespace CircuitRF.Design.Interchange.Hzn;

internal sealed partial class C3dToHznWriter
{
    /// <summary>The name the plane wave is written under, as Hertzon's own examples name theirs.</summary>
    internal const string PlaneWaveName = "IncPWave1";

    /// <summary>The far-field sphere's name, as Hertzon's own examples name theirs.</summary>
    internal const string SphereName = "Infinite Sphere1";

    /// <summary>After every setup: each of the view's plane waves as an excitation (its name and enabled flag kept), and the sphere
    /// at the enabled one's steps (the first one's when none is); an enabled wave beside a modelled port is refused, since a Hertzon
    /// design's excitations drive every setup together.</summary>
    private void PlaneWaveExcitation()
    {
        if (_doc.PlaneWaves.Count == 0) return;
        if (_doc.PlaneWaves.Any(w => w.Enabled) && _excitations.Count > 0)
        {
            _r.Add(HznRefusalKind.Excitation, "Incident Plane Wave beside ports",
                   "an enabled plane wave and the 3D view's ports would both drive every setup of the Hertzon design; disable the wave, or turn the ports' Model off",
                   DesignName);
            return;
        }
        double perMicron = _unit switch
        {
            LayoutUnit.Nm => 1e3, LayoutUnit.Um => 1, LayoutUnit.Mm => 1e-3, LayoutUnit.Mil => 1 / 25.4, _ => 1 / 25400.0,
        };
        string V(double x, double y, double z) => string.Join(",", new[] { x, y, z }.Select(N));
        foreach (var rec in _doc.PlaneWaves)
        {
            if (C3dPlaneWaves.ToSetup(rec, _doc.DbuPerMicron) is not { } w)
            {
                _r.Add(HznRefusalKind.Excitation, $"plane wave '{rec.Name}'", "its Direction and EField are not three numbers each", rec.Name);
                continue;
            }
            var o = w.PhaseOriginUm ?? default;
            _excitations.Add(new HAssign
            {
                Name = rec.Name, Kind = "Incident Plane Wave", Enabled = rec.Enabled,
                Props =
                [
                    ("VectorInput", "Cartesian"),
                    ("E0Vector", V(w.EField.X, w.EField.Y, w.EField.Z)),
                    ("KVector", V(w.Direction.X, w.Direction.Y, w.Direction.Z)),
                    ("Theta", "0deg"), ("Phi", "0deg"), ("Polarization", "0deg"),
                    ("ZeroPhasePosition", V(o.X * perMicron, o.Y * perMicron, o.Z * perMicron)),
                    ("EMagnitude", N(w.E0 ?? EmPlaneWave3D.DefaultE0)),
                ],
            });
        }
        var stepsOf = _doc.PlaneWaves.FirstOrDefault(x => x.Enabled) ?? _doc.PlaneWaves[0];
        _sphere = (stepsOf.ThetaStepDeg ?? CircuitRF.Engine.Em3d.Em3dPlaneWave.DefaultStepDeg, stepsOf.PhiStepDeg ?? CircuitRF.Engine.Em3d.Em3dPlaneWave.DefaultStepDeg);
    }

    private (double Theta, double Phi)? _sphere;

    /// <summary>The design's Infinite Sphere when it has a plane wave: the whole sphere at the wave's steps.</summary>
    private HznJsonArray Spheres()
    {
        var a = new HznJsonArray();
        if (_sphere is not { } s) return a;
        var o = new HznJsonObject();
        o.SetString("name", SphereName);
        o.Set("props", Strings([("CoordinateSystem", "Global"), ("PhiStart", "0deg"), ("PhiStop", "360deg"), ("PhiStep", N(s.Phi) + "deg"),
                                ("ThetaStart", "0deg"), ("ThetaStop", "180deg"), ("ThetaStep", N(s.Theta) + "deg")]));
        a.Add(o);
        return a;
    }

    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}

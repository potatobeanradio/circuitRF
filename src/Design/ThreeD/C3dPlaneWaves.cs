// brief-hzn-12 — the 3D view's plane waves, records of the document as its ports are (owner's choice). The one Enabled wave drives
// every Driven 3D setup of the view: it is put on the setup as it is read (C3dSetups.Read) and again as a problem is assembled, so a
// .cem setup run on the view takes it too; the run, check, explain and every panel see a setup driven by it. A setup never stores one.

using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

public static class C3dPlaneWaves
{
    /// <summary>The wave that drives the view's Driven setups, or null when none is enabled.</summary>
    public static C3dPlaneWave? Enabled(C3dDocument doc) => doc.PlaneWaves.FirstOrDefault(w => w.Enabled);

    /// <summary>A record as the setup's run carries it (µm origin, its name), or null when a vector is not three numbers.</summary>
    public static EmPlaneWave3D? ToSetup(C3dPlaneWave w, int dbuPerMicron)
    {
        if (w.Direction is not { Length: 3 } k || w.EField is not { Length: 3 } e) return null;
        double um(long dbu) => (double)dbu / dbuPerMicron;
        return new EmPlaneWave3D(new Point3(k[0], k[1], k[2]), new Point3(e[0], e[1], e[2]), w.E0,
                                 w.PhaseOrigin == default ? null : new Point3(um(w.PhaseOrigin.X), um(w.PhaseOrigin.Y), um(w.PhaseOrigin.Z)))
        {
            ThetaStepDeg = w.ThetaStepDeg, PhiStepDeg = w.PhiStepDeg, Name = w.Name,
        };
    }

    /// <summary>Why a record cannot be solved, for its diagnostic.</summary>
    public static string Why(C3dPlaneWave w, int dbuPerMicron)
    {
        if (ToSetup(w, dbuPerMicron) is not { } s) return "its Direction and EField are three numbers each.";
        return s.Resolve(out string? why) is null ? why + "." : "";
    }

    /// <summary>The record a setup's wave is written as, named <paramref name="name"/>.</summary>
    public static C3dPlaneWave FromSetup(EmPlaneWave3D w, string name, bool enabled, int dbuPerMicron)
    {
        var o = w.PhaseOriginUm ?? default;
        long dbu(double um) => (long)Math.Round(um * dbuPerMicron, MidpointRounding.AwayFromZero);
        return new C3dPlaneWave
        {
            Name = name, Enabled = enabled,
            Direction = [w.Direction.X, w.Direction.Y, w.Direction.Z], EField = [w.EField.X, w.EField.Y, w.EField.Z],
            E0 = w.E0, PhaseOrigin = new C3dPoint3(dbu(o.X), dbu(o.Y), dbu(o.Z)),
            ThetaStepDeg = w.ThetaStepDeg, PhiStepDeg = w.PhiStepDeg,
        };
    }

    /// <summary>Puts the view's enabled wave on <paramref name="setup"/> when it is a Driven 3D setup; leaves any other untouched.</summary>
    public static EmSetup Apply(EmSetup setup, C3dDocument doc)
    {
        if (setup.IsThermal || !setup.Is3D || setup.Problem3D != Em3dProblemType.Driven) return setup;
        setup.PlaneWave = Enabled(doc) is { } w ? ToSetup(w, doc.DbuPerMicron) : null;
        return setup;
    }

    /// <summary>The first name of the form "Plane wave n" the document does not use.</summary>
    public static string NewName(C3dDocument doc)
    {
        var used = doc.PlaneWaves.Select(w => w.Name).ToHashSet(StringComparer.Ordinal);
        return Enumerable.Range(1, 10_000).Select(n => $"Plane wave {n}").First(n => !used.Contains(n));
    }
}

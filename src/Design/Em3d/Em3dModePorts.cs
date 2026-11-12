// brief-hzn-11 — what a hollow-guide wave port and a port normalised to its mode ask of a run, said once for the run service,
// check and the dry run.
//
// BELOW CUTOFF (R-hzn11-2). A hollow guide carries nothing below its lowest mode's cutoff, c / (2 a sqrt(er mur)) for a guide
// a across its broad side. A sweep point there is refused before meshing, naming the cutoff, rather than handed to a solver
// whose mode solve would return an evanescent mode as though it were the port's. The material is the face's: the one material
// that meets the region, or — a face of several — the highest er·mur among them, whose cutoff is the lowest the guide can have,
// so a point refused is below the cutoff whatever the fill. A 0 Hz row is a conduction solve, and a hollow guide conducts
// nothing between its ports, so it is refused the same way.
//
// WHICH SOLVER (R-hzn11-3). EMerge solves both: its wave port takes a mode of any type, and its S is the mode-normalised one.
// Palace solves both through its own wave port. openEMS builds neither: its wave port is a terminal feed on a lattice, which a
// hollow guide has no terminal for, and its S is always referred to the feed's stated impedance.

using System.Globalization;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

public static class Em3dModePorts
{
    private const double C0 = 299792458.0;

    /// <summary>The cutoff of hollow port <paramref name="p"/>'s lowest mode, Hz, and the material it was taken in; null for a
    /// port that is not a hollow guide.</summary>
    public static (double Hz, string Material)? Cutoff(Em3dProblem problem, Em3dPort p)
    {
        if (p.HollowBroadM is not { } a || !(a > 0)) return null;
        string fill = problem.Boundary.Material ?? GmshGeoWriter.BackgroundMaterial(problem.Solids, problem.Materials);
        var names = FdtdWavePorts.RegionMaterials(problem, p, fill);
        var byName = problem.Materials.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var known = (names.Count > 0 ? names : [fill]).Where(byName.ContainsKey).Select(n => byName[n]).ToList();
        var densest = known.Count > 0 ? known.MaxBy(m => m.Epsr * m.Mur)! : null;
        double index = densest is null ? 1 : Math.Sqrt(Math.Max(densest.Epsr * densest.Mur, 1e-12));
        return (C0 / (2 * a * index), densest?.Name ?? fill);
    }

    /// <summary>The first hollow port the sweep reaches below the cutoff of, as a refusal naming it; null when every point (and
    /// no 0 Hz row) is above every hollow port's cutoff.</summary>
    /// <param name="zeroHzRow">True when the setup's sweep holds 0 Hz, which the run solves apart as a conduction solve.</param>
    public static string? BelowCutoffRefusal(Em3dProblem problem, bool zeroHzRow = false)
    {
        if (problem.Type != Em3dProblemType.Driven) return null;
        var points = problem.Frequency.ListHz ?? Even(problem.Frequency);
        foreach (var p in problem.Ports.Where(q => q.Hollow).OrderBy(q => q.Number))
        {
            if (Cutoff(problem, p) is not var (fc, material)) continue;
            int below = points.Count(f => f < fc) + (zeroHzRow ? 1 : 0);
            if (below == 0) continue;
            double lowest = zeroHzRow ? 0 : points.Min();
            return $"Port {Label(p)} is a hollow guide {G(p.HollowBroadM!.Value * 1e3)} mm across, whose lowest mode is cut off below " +
                   $"{G(fc / 1e9)} GHz in {material}; the sweep starts at {G(lowest / 1e9)} GHz, and {below} of its points " +
                   $"{(below == 1 ? "lies" : "lie")} below the cutoff, where the port carries no mode. Start the sweep above {G(fc / 1e9)} GHz.";
        }
        return null;
    }

    /// <summary>What <paramref name="solver"/> cannot do of the problem's hollow guides and mode-normalised ports, naming the
    /// port and the solver; null when it does all of it.</summary>
    public static string? SolverRefusal(Em3dProblem problem, Em3dSolverId solver)
    {
        if (solver != OpenEmsSolver.Id) return null;
        if (problem.Ports.FirstOrDefault(p => p.Kind == Em3dPortKind.Wave && FdtdWavePorts.IsHollow(problem, p)) is { } hollow)
            return FdtdWavePorts.HollowRefusal(hollow);
        if (problem.Ports.FirstOrDefault(p => p.ModeNormalised) is { } mode)
            return ModeRefusal(Label(mode), "openEMS");
        return null;
    }

    /// <summary>A port normalised to its mode on a solver whose S is always referred to a stated impedance.</summary>
    public static string ModeRefusal(string port, string solver)
        => $"Port {port}'s Z0 is 'mode', and {solver} refers every port's S-parameters to a stated impedance, so it cannot report " +
           "them normalised to the mode. Run the setup on EMerge or Palace, or give the port a number of ohms.";

    /// <summary>The <c>.sNp</c> header line a result with ports normalised to their modes carries (R-hzn11-4): its <c>R 1</c> is
    /// not an impedance, and nothing was renormalised. Null when no port is.</summary>
    public static string? SnpLine(IEnumerable<Em3dPort> ports)
        => ports.Where(p => p.ModeNormalised).Select(p => p.Number).Order().ToList() is { Count: > 0 } n
            ? $"{SnpPrefix}{string.Join(", ", n)}. Each port's S is its mode's unit-power wave as solved, with nothing " +
              "renormalised; the R 1 of the option line is not an impedance, and renormalising these numbers to any R is meaningless."
            : null;

    /// <summary>The start of <see cref="SnpLine"/>, which a reader looks for.</summary>
    public const string SnpPrefix = "circuitRF-EM ports normalised to their modes: ";

    /// <summary>A hollow or mode-normalised port in a provenance line, after its name.</summary>
    public static string PortWords(Em3dPort p, Func<string, string> ascii)
        => (p.Hollow ? $"a hollow guide {G(p.HollowBroadM!.Value * 1e3)} mm across" : $"from '{ascii(p.NegativeObject)}' to '{ascii(p.PositiveObject)}'") +
           ", wave (mode 1), " +
           (p.ModeNormalised ? "normalised to its mode (R 1 is not an impedance)" : $"{p.Z0.Real.ToString("R", CultureInfo.InvariantCulture)} Ohm");

    private static string Label(Em3dPort p)
        => p.SourceLabel is { Length: > 0 } l ? $"'{l}'" : (p.SourceNumber ?? p.Number).ToString(CultureInfo.InvariantCulture);

    private static IReadOnlyList<double> Even(Em3dFrequency f)
        => f.Points <= 1 ? [f.StartHz] : [.. Enumerable.Range(0, f.Points).Select(k => f.StartHz + (f.StopHz - f.StartHz) * k / (f.Points - 1))];

    private static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
}

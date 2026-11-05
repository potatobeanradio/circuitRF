// Metal thin against the skin depth, said before a run that treats it as thick.
//
// Palace and EMerge solve no conductor's interior: its faces carry (1+j)/(σδ), the impedance of metal thick against the skin
// depth. Below about three skin depths the current fills the metal, the true resistance levels off at the DC value and this
// impedance goes on falling, so the loss reads low and a Q reads high with nothing in the answer to show it.
//
// THE NUMBER. A conductor of section A and perimeter P has one thickness that sets both limits: t = 2A/P. It is the thickness for
// a wide strip, the radius for a wire, and wt/(w+t) for a bar. Over a slab of that thickness the true impedance is
// (1+j)/(σδ)·coth((1+j)t/2δ) on each face, and its real part over the thick-metal value's is how far the loss reads low.
//
// The same impedance is as reactive as it is resistive, so the inductance reads high by R/ω at the same frequencies.
//
// MEASURED, Palace against a 2D filament solve, 1 µm aluminium 10 µm over ground. The slab's number holds to within about 15 % from
// 0.1 to 9 GHz. At 2.0 skin depths a 5 µm line read 27 % low, and at 2.4 a 50 µm line 9 %, which the slab does not predict: current
// crowding at the edges. So the warning starts at three skin depths, and near there it states a range rather than the slab's number.
//
// THE CORRECTION (brief-em3d-139). Each face gets (1+j)/(σδ)·coth((1+j)h/2δ) with h = t. That is exact when the two faces carry equal
// current. The solvers do not couple one face to the other, so a real slab's current, uniform through its thickness whatever the field
// outside, is not what they solve: where the field is on one side, a face carries more than its half and the resistance reads high.
// Measured to mesh convergence, 1 µm thick and 10 µm over ground, R' at 2 GHz against a 2D filament solve is 1.18 on a 50 µm line and 1.47
// on a 500 µm line, against 0.32 and 0.40 uncorrected. Near two to three skin depths it reads low, up to 24 % measured.
//
// A WARNING, NEVER A REFUSAL: on many designs the bottom of a sweep is not where anyone reads the loss. It names the worst
// conductor and a count, so a board of a thousand pieces gets one sentence.

using System.Globalization;
using System.Numerics;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Layout.Em3d;

/// <summary>One conductor thin against the skin depth at the frequency a run reads it at.</summary>
/// <param name="Corrected">Whether its faces carry <paramref name="ThicknessM"/> on this run (brief-em3d-139), rather than thick metal's impedance.</param>
public sealed record Em3dThinMetal(Em3dSolid Solid, string Material, double ThicknessM, double FrequencyHz, double SkinDepthM,
                                   double ReadsLowBy, double ThickAboveHz, bool Corrected)
{
    public string Object => Solid.Name;
}

public static class Em3dThinMetalLoss
{
    /// <summary>Where the thick-metal impedance stops being a good account of a conductor, in skin depths of
    /// <see cref="EquivalentThicknessM"/>: measured 9 % to 27 % low already at 2.4 and 2.0.</summary>
    public const double SkinDepths = 3;

    /// <summary>Palace evaluates cosh(h/δ) and sinh(h/δ) directly, which overflow past about 710, and inf/inf is NaN. A conductor that far
    /// past its skin depth at the top of the sweep stays as thick metal: clamping h would move the bottom of the band instead.</summary>
    public const double MostSkinDepthsSolved = 700;

    /// <summary>The slab's ratio below which the slab no longer says how far off a real line is.</summary>
    private const double SlabRatioIsMeaningfulFrom = 1.2;

    private const double Mu0 = 4e-7 * Math.PI;

    /// <summary>The warning for a run of <paramref name="problem"/> under <paramref name="setup"/>, for what is not corrected: one sentence
    /// or none. openEMS writes its metals as perfect conductors and says so itself, so a setup running only that has nothing to say here.</summary>
    public static IReadOnlyList<string> ForRun(Em3dProblem problem, EmSetup setup)
    {
        if (!setup.Solver3D.Runs(PalaceSolver.Id) && !setup.Solver3D.Runs(EmergeSolver.Id)) return [];
        var thin = Find(problem).Where(t => !t.Corrected).ToList();
        return thin.Count == 0 ? [] : [Sentence(thin) + " " + WhyNotCorrected(problem)];
    }

    /// <summary>brief-em3d-139 R-em3d139-4 — the note for what is corrected: one sentence or none.</summary>
    public static IReadOnlyList<string> NotesForRun(Em3dProblem problem, EmSetup setup)
    {
        if (!setup.Solver3D.Runs(PalaceSolver.Id) && !setup.Solver3D.Runs(EmergeSolver.Id)) return [];
        var thin = Find(problem).Where(t => t.Corrected).ToList();
        return thin.Count == 0 ? [] : [CorrectedSentence(thin)];
    }

    /// <summary>brief-em3d-139 R-em3d139-2 — the h each thin conductor's faces carry on Palace and EMerge, metres: only a driven problem with
    /// the correction on, only a conductor <see cref="Find"/> finds and only within the range the solver can evaluate. Empty otherwise, so a
    /// design with no thin metal lowers to the bytes it always did.</summary>
    public static IReadOnlyDictionary<Em3dSolid, double> CorrectedFaces(Em3dProblem problem)
    {
        var faces = new Dictionary<Em3dSolid, double>(ReferenceEqualityComparer.Instance);
        foreach (var t in Find(problem).Where(t => t.Corrected)) faces[t.Solid] = t.ThicknessM;
        return faces;
    }

    /// <summary>Every conductor of a driven or eigenmode problem under <see cref="SkinDepths"/> at the lowest frequency it is
    /// solved at, worst first. A kernel solid, a sphere and a perfect conductor are not assessed.</summary>
    public static IReadOnlyList<Em3dThinMetal> Find(Em3dProblem problem)
    {
        if (LowestHz(problem) is not { } f) return [];
        double top = HighestHz(problem) ?? f;
        var materials = problem.Materials.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var thin = new List<Em3dThinMetal>();
        foreach (var solid in problem.Solids.Where(s => s.Role == Em3dRole.Conductor))
        {
            if (EquivalentThicknessM(solid.Primitive) is not { } t || t <= 0) continue;
            if (!materials.TryGetValue(solid.Material, out var m) || !(m.SigmaSm > 0) || !double.IsFinite(m.SigmaSm)) continue;
            double mu = Mu0 * m.Mur;
            double delta = 1 / Math.Sqrt(Math.PI * f * mu * m.SigmaSm);
            if (t >= SkinDepths * delta) continue;
            // t = n·δ at f = n²/(π·μ·σ·t²).
            // δ at the top of the sweep is δ at the bottom over √(top/bottom).
            bool inRange = t / delta * Math.Sqrt(top / f) < MostSkinDepthsSolved;
            thin.Add(new Em3dThinMetal(solid, solid.Material, t, f, delta, ReadsLowBy(t / delta),
                                       SkinDepths * SkinDepths / (Math.PI * mu * m.SigmaSm * t * t),
                                       problem.ThinMetalCorrection && problem.Type == Em3dProblemType.Driven && inRange));
        }
        return [.. thin.OrderByDescending(x => x.ReadsLowBy)];
    }

    /// <summary>2A/P of the section current flows through, metres, or null where the primitive does not say. An extrusion wider
    /// than it is tall carries its current sideways, one taller carries it up, as a via does.</summary>
    public static double? EquivalentThicknessM(Em3dPrimitive primitive) => primitive switch
    {
        Em3dExtrudedPolygon e when WidthM(e) is > 0 and var w => Bar(w, e.ZTop - e.ZBottom),
        Em3dCylinder c => Bar(c.Radius, Distance(c.AxisStart, c.AxisEnd)),
        Em3dSweep w => w.Diameter / 2,
        Em3dBox b => Section(Math.Abs(b.Max.X - b.Min.X), Math.Abs(b.Max.Y - b.Min.Y), Math.Abs(b.Max.Z - b.Min.Z)),
        _ => null,
    };

    /// <summary>How far a slab <paramref name="thicknessInSkinDepths"/> thick reads low under the thick-metal impedance.</summary>
    public static double ReadsLowBy(double thicknessInSkinDepths)
    {
        var u = new Complex(1, 1) * (thicknessInSkinDepths / 2);
        return (new Complex(1, 1) * Complex.Cosh(u) / Complex.Sinh(u)).Real;
    }

    private static string Sentence(IReadOnlyList<Em3dThinMetal> thin)
    {
        var w = thin[0];
        string who = thin.Count == 1 ? $"'{w.Object}' is" : $"'{w.Object}' and {thin.Count - 1} other conductor(s) are";
        return $"{who} under {SkinDepths:G} skin depths thick at {Hz(w.FrequencyHz)}, the lowest frequency solved. The solver models a " +
               "conductor's faces as thick metal, so its resistance reads low and its inductance high below about " +
               $"{Hz(w.ThickAboveHz)}: on '{w.Object}', {w.Material} with an effective thickness of {Um(w.ThicknessM)} µm and a skin depth of " +
               $"{Um(w.SkinDepthM)} µm, " +
               (w.ReadsLowBy >= SlabRatioIsMeaningfulFrom ? $"about {G(w.ReadsLowBy)} times low" : "roughly 10 % to 30 % low") + $" at {Hz(w.FrequencyHz)}.";
    }

    private static string WhyNotCorrected(Em3dProblem problem)
        => !problem.ThinMetalCorrection ? "Thin Metal Correction is off for this setup."
         : problem.Type != Em3dProblemType.Driven ? "Thin Metal Correction applies to a driven run only."
         : $"Thin Metal Correction leaves it as thick metal, because the sweep reaches over {MostSkinDepthsSolved:G} skin depths of it.";

    private static string CorrectedSentence(IReadOnlyList<Em3dThinMetal> thin)
    {
        var w = thin[0];
        string who = thin.Count == 1 ? $"'{w.Object}' is" : $"'{w.Object}' and {thin.Count - 1} other conductor(s) are";
        return $"{who} under {SkinDepths:G} skin depths thick at {Hz(w.FrequencyHz)}, so Thin Metal Correction gives the faces that " +
               $"thickness ({Um(w.ThicknessM)} µm on '{w.Object}'). Its resistance levels off at the DC value. Near two to three skin depths a line " +
               "can still read up to 25 % low. A wide line close to ground can read high, because its faces carry unequal current: up to 20 % " +
               "on a line 5 times wider than its height over ground, and 50 % at 50 times.";
    }

    private static double? LowestHz(Em3dProblem p)
    {
        if (p.Type == Em3dProblemType.Eigenmode) return p.EigenmodeTargetHz > 0 ? p.EigenmodeTargetHz : null;
        if (p.Type != Em3dProblemType.Driven) return null;
        if (p.Frequency.ListHz is { } list) return list.Where(f => f > 0).Select(f => (double?)f).Min();
        if (p.Frequency.StartHz > 0) return p.Frequency.StartHz;
        // A sweep from 0 Hz: its first point is the DC row, which is a conduction solve of its own and has no such error.
        return p.Frequency.Points > 1 && p.Frequency.StopHz > 0 ? p.Frequency.StopHz / (p.Frequency.Points - 1) : null;
    }

    private static double? HighestHz(Em3dProblem p)
    {
        if (p.Type != Em3dProblemType.Driven) return null;
        return p.Frequency.ListHz is { } list ? list.Select(f => (double?)f).Max() : p.Frequency.StopHz;
    }

    private static double Bar(double width, double height)
        => height >= width ? width : width * height / (width + height);

    private static double Section(double a, double b, double c)
    {
        double[] s = [a, b, c];
        Array.Sort(s);
        return s[0] * s[1] / (s[0] + s[1]);
    }

    /// <summary>2·Area/Perimeter of the outline, which is a strip's width.</summary>
    private static double WidthM(Em3dExtrudedPolygon e)
    {
        double area = Math.Abs(Shoelace(e.Outline)) - e.Holes.Sum(h => Math.Abs(Shoelace(h)));
        double perimeter = Perimeter(e.Outline) + e.Holes.Sum(Perimeter);
        return perimeter > 0 ? 2 * area / perimeter : 0;
    }

    private static double Shoelace(IReadOnlyList<Point2> ring)
    {
        double sum = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var (a, b) = (ring[i], ring[(i + 1) % ring.Count]);
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum / 2;
    }

    private static double Perimeter(IReadOnlyList<Point2> ring)
    {
        double sum = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var (a, b) = (ring[i], ring[(i + 1) % ring.Count]);
            sum += Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        }
        return sum;
    }

    private static double Distance(Point3 a, Point3 b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    private static string G(double v) => v.ToString("G2", CultureInfo.InvariantCulture);
    private static string Um(double m) => (m * 1e6).ToString("G3", CultureInfo.InvariantCulture);
    private static string Hz(double f) => f >= 1e9 ? $"{(f / 1e9).ToString("G3", CultureInfo.InvariantCulture)} GHz"
                                                   : $"{(f / 1e6).ToString("G3", CultureInfo.InvariantCulture)} MHz";
}

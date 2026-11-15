// brief-paa-2 R-paa2-2a — a set of directions a pattern is evaluated at: a record's grid (the overview), a cut or a patch around the
// peak (the local evaluation), or (u,v) points of the FFT. A phase table is built for one set and released with it, so the set is
// the cache key (ArrayPattern.Tables) and nothing outlives it.

using CircuitRF.Engine.Mom;

namespace CircuitRF.Engine.AntennaArray;

/// <summary>Directions as θ and φ, radians, with their direction cosines.</summary>
public sealed class ArrayDirections
{
    private ArrayDirections(double[] theta, double[] phi, PlanarFarFieldGrid? grid)
    {
        ThetaRad = theta;
        PhiRad = phi;
        Grid = grid;
        int n = theta.Length;
        U = new double[n];
        V = new double[n];
        W = new double[n];
        for (int d = 0; d < n; d++)
        {
            var (st, ct) = Math.SinCos(theta[d]);
            var (sp, cp) = Math.SinCos(phi[d]);
            U[d] = st * cp;
            V[d] = st * sp;
            W[d] = ct;
        }
    }

    public double[] ThetaRad { get; }
    public double[] PhiRad { get; }

    /// <summary>sinθ·cosφ, sinθ·sinφ and cosθ of each direction.</summary>
    public double[] U { get; }
    public double[] V { get; }
    public double[] W { get; }

    /// <summary>The record's grid when the set is that grid, in its [theta, phi] order; an element pattern is then read, not interpolated.</summary>
    public PlanarFarFieldGrid? Grid { get; }

    public int Count => ThetaRad.Length;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PlanarFarFieldGrid, ArrayDirections> _onGrid = new();

    /// <summary>Every direction of a grid, row-major [theta, phi]. brief-paa-8 R-paa8-1 — one set per grid object, so the phase tables and the
    /// sampled element patterns kept against it (ArrayPattern.Tables, ElementPattern.Sample) survive from one evaluation to the next: a new set
    /// per call rebuilt the tables at every slider move.</summary>
    public static ArrayDirections OnGrid(PlanarFarFieldGrid grid) => _onGrid.GetValue(grid, Build);

    private static ArrayDirections Build(PlanarFarFieldGrid grid)
    {
        int nt = grid.ThetaDeg.Count, np = grid.PhiDeg.Count;
        var t = new double[nt * np];
        var p = new double[nt * np];
        for (int i = 0; i < nt; i++)
            for (int j = 0; j < np; j++)
            {
                t[i * np + j] = grid.ThetaDeg[i] * Math.PI / 180;
                p[i * np + j] = grid.PhiDeg[j] * Math.PI / 180;
            }
        return new ArrayDirections(t, p, grid);
    }

    /// <summary>Directions given as θ and φ, radians.</summary>
    public static ArrayDirections Of(IReadOnlyList<double> thetaRad, IReadOnlyList<double> phiRad)
    {
        if (thetaRad.Count != phiRad.Count) throw new ArgumentException("θ and φ must be as many as each other.");
        return new ArrayDirections([.. thetaRad], [.. phiRad], null);
    }

    /// <summary>Directions given as unit vectors.</summary>
    public static ArrayDirections OfVectors(IReadOnlyList<(double X, double Y, double Z)> vectors)
    {
        var t = new double[vectors.Count];
        var p = new double[vectors.Count];
        for (int d = 0; d < vectors.Count; d++) (t[d], p[d]) = Angles(vectors[d]);
        return new ArrayDirections(t, p, null);
    }

    /// <summary>θ in [0, π] and φ in (−π, π] of a direction.</summary>
    public static (double Theta, double Phi) Angles((double X, double Y, double Z) u)
    {
        double rho = Math.Sqrt(u.X * u.X + u.Y * u.Y);
        return (Math.Atan2(rho, u.Z), rho == 0 ? 0 : Math.Atan2(u.Y, u.X));
    }

    /// <summary>The unit vector of θ, φ in radians.</summary>
    public static (double X, double Y, double Z) Vector(double theta, double phi)
    {
        var (st, ct) = Math.SinCos(theta);
        var (sp, cp) = Math.SinCos(phi);
        return (st * cp, st * sp, ct);
    }
}

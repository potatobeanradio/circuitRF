// brief-hzn-12 R-hzn12-4 — a scattering run's radar cross-section as a DataSet.
//
// A plane wave drives the problem, so there are no ports, no S and no Touchstone file. The result is one group, "rcs":
//   * Bistatic        [f, theta, phi], m²: σ = 4π r² |E_s|² / |E_i|² as r → ∞, over the far-field grid;
//   * BistaticDbsm    the same in dBsm (10·log10 of σ in m²);
//   * Monostatic      [f], m²: σ in the one direction straight back along the incidence, computed there, never interpolated;
//   * MonostaticDbsm  the same in dBsm;
//   * Etheta, Ephi    [f, theta, phi], complex: the scattered far field, r·E_s with the e^(−jkr) taken out, V, so a polarised
//                     cross-section needs no new run.
// It is deliberately not the port far-field group: that group's readers compute gain, directivity and efficiency from an input
// power, which a plane-wave run does not have.

using System.Numerics;
using RfCore.Data;

namespace CircuitRF.Engine.Em3d;

public static class Em3dScatteringResult
{
    /// <summary>The result's group.</summary>
    public const string Group = "rcs";

    public const string BistaticCube = "Bistatic";
    public const string BistaticDbsmCube = "BistaticDbsm";
    public const string MonostaticCube = "Monostatic";
    public const string MonostaticDbsmCube = "MonostaticDbsm";
    public const string EthetaCube = "Etheta";
    public const string EphiCube = "Ephi";

    /// <summary>The incident wave the result was solved for, carried in the result itself: [xyz] vectors and scalars.</summary>
    public const string DirectionCube = "IncidentDirection", EFieldCube = "IncidentEField", E0Cube = "IncidentE0", OriginCube = "PhaseOrigin";

    /// <summary>The unit of a cross-section, and of one in decibels.</summary>
    public const string AreaUnit = "m^2", DbsmUnit = "dBsm";

    /// <summary>σ = 4π (|Eθ|² + |Eφ|²) / E0², m², from the far field r·E_s.</summary>
    public static double Sigma(Complex eTheta, Complex ePhi, double e0)
        => 4 * Math.PI * (eTheta.Magnitude * eTheta.Magnitude + ePhi.Magnitude * ePhi.Magnitude) / (e0 * e0);

    /// <summary>σ in dBsm. A cross-section of exactly zero (a null the integral resolved to nothing) is −∞ dBsm, written as −400.</summary>
    public static double Dbsm(double sigma) => sigma > 0 ? 10 * Math.Log10(sigma) : -400;

    /// <summary>
    /// The DataSet of a scattering run. <paramref name="far"/> is [f, 2 (θ, φ), θ, φ] row-major and <paramref name="monostatic"/>
    /// [f, 2], both the far field r·E_s in V; <paramref name="e0"/> is the incident amplitude, V/m.
    /// </summary>
    public static DataSet Build(double[] frequenciesHz, double[] thetaDeg, double[] phiDeg, Complex[] far, Complex[] monostatic, double e0)
        => Build(frequenciesHz, thetaDeg, phiDeg, far, monostatic, new Em3dPlaneWave(new(0, 0, 1), new(1, 0, 0), e0, default), carryWave: false);

    /// <summary>The DataSet of a scattering run, carrying the wave it was solved for beside the cross-section.</summary>
    public static DataSet Build(double[] frequenciesHz, double[] thetaDeg, double[] phiDeg, Complex[] far, Complex[] monostatic, Em3dPlaneWave wave)
        => Build(frequenciesHz, thetaDeg, phiDeg, far, monostatic, wave, carryWave: true);

    private static DataSet Build(double[] frequenciesHz, double[] thetaDeg, double[] phiDeg, Complex[] far, Complex[] monostatic, Em3dPlaneWave wave,
                                 bool carryWave)
    {
        double e0 = wave.E0;
        int nf = frequenciesHz.Length, nt = thetaDeg.Length, np = phiDeg.Length;
        if (far.Length != nf * 2 * nt * np) throw new ArgumentException($"The far field holds {far.Length} values, not {nf}×2×{nt}×{np}.", nameof(far));
        if (monostatic.Length != nf * 2) throw new ArgumentException($"The monostatic field holds {monostatic.Length} values, not {nf}×2.", nameof(monostatic));
        var data = new DataSet();
        Axis[] grid =
        [
            new Axis("f", (double[])frequenciesHz.Clone(), "Hz"),
            new Axis("theta", (double[])thetaDeg.Clone(), "deg"),
            new Axis("phi", (double[])phiDeg.Clone(), "deg"),
        ];
        var eTheta = new Complex[nf * nt * np];
        var ePhi = new Complex[nf * nt * np];
        var bistatic = new double[nf * nt * np];
        var bistaticDb = new double[nf * nt * np];
        for (int f = 0; f < nf; f++)
            for (int t = 0; t < nt; t++)
                for (int p = 0; p < np; p++)
                {
                    int at = (f * nt + t) * np + p;
                    eTheta[at] = far[((f * 2 + 0) * nt + t) * np + p];
                    ePhi[at] = far[((f * 2 + 1) * nt + t) * np + p];
                    bistatic[at] = Sigma(eTheta[at], ePhi[at], e0);
                    bistaticDb[at] = Dbsm(bistatic[at]);
                }
        var mono = new double[nf];
        var monoDb = new double[nf];
        for (int f = 0; f < nf; f++)
        {
            mono[f] = Sigma(monostatic[f * 2], monostatic[f * 2 + 1], e0);
            monoDb[f] = Dbsm(mono[f]);
        }
        Axis[] sweep = [new Axis("f", (double[])frequenciesHz.Clone(), "Hz")];
        data.AddToGroup(Group, BistaticCube, new DataCube(grid, bistatic) { Unit = AreaUnit });
        data.AddToGroup(Group, BistaticDbsmCube, new DataCube(grid, bistaticDb) { Unit = DbsmUnit });
        data.AddToGroup(Group, MonostaticCube, new DataCube(sweep, mono) { Unit = AreaUnit });
        data.AddToGroup(Group, MonostaticDbsmCube, new DataCube(sweep, monoDb) { Unit = DbsmUnit });
        data.AddToGroup(Group, EthetaCube, new DataCube(grid, eTheta) { Unit = "V" });
        data.AddToGroup(Group, EphiCube, new DataCube(grid, ePhi) { Unit = "V" });
        if (carryWave)
        {
            Axis[] xyz = [new Axis("xyz", [0, 1, 2], "", ["x", "y", "z"])];
            static double[] V(Point3 p) => [p.X, p.Y, p.Z];
            data.AddToGroup(Group, DirectionCube, new DataCube(xyz, V(wave.Direction)) { Unit = "1" });
            data.AddToGroup(Group, EFieldCube, new DataCube(xyz, V(wave.EField)) { Unit = "1" });
            data.AddToGroup(Group, OriginCube, new DataCube(xyz, V(wave.PhaseOrigin)) { Unit = "m" });
            data.AddToGroup(Group, E0Cube, DataCube.Scalar(wave.E0));
        }
        return data;
    }
}

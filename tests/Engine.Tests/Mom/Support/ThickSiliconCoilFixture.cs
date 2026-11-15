// ICF-4 — a small spiral over a thick, conducting substrate, written for the test.
//
// A two-turn square spiral, 8 um turns at 4 um spacing, 100 um outer, with an underpass: both leads leave to the left
// and end at x = -90 um, where an Uncalibrated (grounded) edge port sits on each. The stack is ~750 um of silicon on a
// backside plane, 2 um of oxide to the underpass level and 4 um more to the top metal, open above. 140 x 100 um with its
// leads — smaller than one local-grid base block at 22 GHz and between one and two at 40 GHz, which is the shape the
// sweep-dependence finding needs. The numbers measured on it are in src/Engine/Mom/RESOLVED.md §ICF-4.
//
// Nothing here comes from a kit: the geometry and stack are round numbers chosen to sit close to a field test's shape.

using System.Numerics;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Engine.Tests.Mom.Support;

public static class ThickSiliconCoilFixture
{
    private const double U = 1e-6;

    public const double SiliconM = 750e-6, UnderOxideM = 2e-6, TopOxideM = 4e-6;
    public const double SiliconEpsR = 11.9, OxideEpsR = 4.1;

    /// <summary>The substrate conductivity the measurement used, S/m (5 kΩ·cm).</summary>
    public const double SiliconSigma = 0.02;

    public static double UnderZ => SiliconM + UnderOxideM;
    public static double TopZ => SiliconM + UnderOxideM + TopOxideM;

    private static PlanarPolygon R(double x1, double y1, double x2, double y2) =>
        PlanarLineFixtures.Rect(x1 * U, y1 * U, x2 * U, y2 * U);

    /// <summary>Layer 0 is the underpass level, layer 1 the top metal.</summary>
    public static PlanarProblem Coil(double fMaxHz, double siliconSigma = SiliconSigma)
    {
        var si = new EmMaterial(SiliconEpsR, 0.0) { SigmaSm = siliconSigma };
        var ox = new EmMaterial(OxideEpsR, 0.0);
        var stack = new LayerStack(Termination.Pec,
            [new MediumLayer(SiliconM, si), new MediumLayer(UnderOxideM, ox), new MediumLayer(TopOxideM, ox)],
            Termination.Air);

        PlanarPolygon[] top =
        [
            R(-90, 42, 50, 50), R(42, -50, 50, 42), R(-50, -50, 42, -42), R(-50, -42, -42, 38),   // outer turn and lead 1
            R(-42, 30, 38, 38), R(30, -38, 38, 30), R(-38, -38, 30, -30), R(-38, -30, -30, 14),   // inner turn
            R(-90, 6, -66, 14),                                                                   // lead 2
        ];
        PlanarPolygon[] under = [R(-74, 6, -30, 14)];
        PlanarVia[] vias =
        [
            new(0, 1, [R(-38, 6, -30, 14)], 3e7),
            new(0, 1, [R(-74, 6, -66, 14)], 3e7),
        ];

        return new PlanarProblem(
            [new PlanarConductorLayer("Under", under, 3e7, 1e-6, UnderZ),
             new PlanarConductorLayer("Top", top, 3e7, 3e-6, TopZ)],
            new GroundedSlab(SiliconM, new EmMaterial(SiliconEpsR)), fMaxHz,
            MediumStack: stack, Vias: vias);
    }

    /// <summary>The two leads' Uncalibrated (grounded, not de-embedded) edge ports, both at x = −90 µm.</summary>
    public static PlanarPort[] Ports(double z0 = 50.0) =>
    [
        new(1, new EmPoint(-90 * U, 46 * U), PlanarPortSide.MinX, z0, LayerIndex: 1) { Uncalibrated = true },
        new(2, new EmPoint(-90 * U, 10 * U), PlanarPortSide.MinX, z0, LayerIndex: 1) { Uncalibrated = true },
    ];

    /// <summary>The product's planar mesh defaults (stacked regions, local grid, merging on), at a stated mesh frequency.</summary>
    public static PlanarMeshSettings Mesh(double? meshFrequencyHz, bool localGrid = true) =>
        PlanarMeshSettings.Default with
        {
            RooftopMerging = true, StackedRegions = true, LocalGrid = localGrid, MeshFrequencyHz = meshFrequencyHz,
        };

    /// <summary>The series inductance of the coil between the two ports, from Y21: L = Im(−1/Y21)/ω.</summary>
    public static double SeriesInductance(Complex s11, Complex s12, Complex s21, Complex s22, double fHz, double z0 = 50.0)
    {
        Complex one = 1;
        Complex det = (one - s11) * (one - s22) - s12 * s21;
        Complex z11 = z0 * ((one + s11) * (one - s22) + s12 * s21) / det;
        Complex z22 = z0 * ((one - s11) * (one + s22) + s12 * s21) / det;
        Complex z12 = z0 * 2 * s12 / det, z21 = z0 * 2 * s21 / det;
        Complex y21 = -z21 / (z11 * z22 - z12 * z21);
        return (-1.0 / y21).Imaginary / (2 * Math.PI * fHz);
    }
}

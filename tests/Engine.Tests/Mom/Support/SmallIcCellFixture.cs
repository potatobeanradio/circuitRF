// ICF-5 — a small cascode-like IC cell, written for the test.
//
// 40 × 100 µm on the thick-silicon stack of ThickSiliconCoilFixture: two device blocks on the top metal, each with three
// 1.5 µm pin stubs (the narrowest metal, 2 µm thinner than the 3 µm top metal is thick), joined by an underpass, with an
// input, an output and a supply lead each ending on an Uncalibrated edge port. Nothing comes from a kit; the shape is
// round numbers close to a field test's cascode. The numbers measured on it are in src/Engine/Mom/RESOLVED.md §ICF-5.

using CircuitRF.Engine.Mom;

namespace CircuitRF.Engine.Tests.Mom.Support;

public static class SmallIcCellFixture
{
    private const double U = 1e-6;

    private static PlanarPolygon R(double x1, double y1, double x2, double y2) =>
        PlanarLineFixtures.Rect(x1 * U, y1 * U, x2 * U, y2 * U);

    /// <summary>Layer 0 is the underpass level (1 µm thick), layer 1 the top metal (3 µm).</summary>
    public static PlanarProblem Cell(double fMaxHz)
    {
        var bare = ThickSiliconCoilFixture.Coil(fMaxHz);
        PlanarPolygon[] top =
        [
            R(0, 10, 15, 16), R(15, 5, 30, 25), R(30, 8, 36, 9.5), R(30, 14, 36, 15.5), R(30, 20, 36, 21.5),
            R(15, 85, 30, 105), R(30, 88, 36, 89.5), R(30, 94, 36, 95.5), R(30, 100, 36, 101.5), R(0, 95, 15, 101),
            R(20, 50, 40, 56),
        ];
        PlanarPolygon[] under = [R(20, 19, 26, 91)];
        PlanarVia[] vias =
        [
            new(0, 1, [R(20, 19, 26, 25)], 3e7), new(0, 1, [R(20, 85, 26, 91)], 3e7), new(0, 1, [R(20, 50, 26, 56)], 3e7),
        ];
        return bare with
        {
            Layers =
            [
                bare.Layers[0] with { Polygons = under },
                bare.Layers[1] with { Polygons = top },
            ],
            Vias = vias,
        };
    }

    /// <summary>Input, output and supply, each an Uncalibrated edge port on the top metal.</summary>
    public static PlanarPort[] Ports(double z0 = 50.0) =>
    [
        new(1, new EmPoint(0, 13 * U), PlanarPortSide.MinX, z0, LayerIndex: 1) { Uncalibrated = true },
        new(2, new EmPoint(0, 98 * U), PlanarPortSide.MinX, z0, LayerIndex: 1) { Uncalibrated = true },
        new(3, new EmPoint(40 * U, 53 * U), PlanarPortSide.MaxX, z0, LayerIndex: 1) { Uncalibrated = true },
    ];
}

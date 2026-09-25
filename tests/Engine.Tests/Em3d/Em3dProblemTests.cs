using System.Numerics;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Engine.Tests.Em3d;

// brief-em3d-3 §7 gate 8 — Em3dProblem.Validate() reports EVERY structural problem, not the first.

public sealed class Em3dProblemTests
{
    [Fact]
    public void Gate8_ValidateReportsAllThree_DuplicateName_OutsideTheBox_UnresolvedMaterial()
    {
        var box = new Em3dAirBox(new Point3(-1e-3, -1e-3, 0), new Point3(1e-3, 1e-3, 1e-3),
            new Em3dFaces(Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing,
                          Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Pec, Em3dBoundaryKind.Absorbing));
        Em3dPrimitive Cube(double h) => new Em3dBox(new Point3(-h, -h, 0), new Point3(h, h, h));

        var problem = new Em3dProblem(
            Solids:
            [
                new Em3dSolid("sub", "FR4", Em3dRole.Dielectric, Cube(1e-4), 1),
                new Em3dSolid("sub", "FR4", Em3dRole.Dielectric, Cube(1e-4), 2),       // duplicate name
                new Em3dSolid("big", "FR4", Em3dRole.Dielectric, Cube(5e-3), 3),       // outside the box
                new Em3dSolid("metal", "Unobtainium", Em3dRole.Conductor, Cube(1e-4), 4), // unresolved
            ],
            Sheets: [],
            Materials: [new Em3dMaterial("FR4", 4.4, null, 0.02, 1, 0)],
            Ports: [],
            Boundary: box,
            Frequency: new Em3dFrequency(1e9, 10e9, 10, Em3dSweepKind.Linear),
            OperatingTempC: 20);

        var problems = problem.Validate();

        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.Contains("'sub'", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'big'", StringComparison.Ordinal) && p.Contains("outside"));
        Assert.Contains(problems, p => p.Contains("'Unobtainium'", StringComparison.Ordinal));

        // And a sound problem is sound — a port on the PEC floor included.
        var sound = problem with
        {
            Solids = [problem.Solids[0], problem.Solids[3] with { Name = "metal", Material = "FR4" }],
            Ports =
            [
                new Em3dPort(1, "port/1", "metal", Em3dAirBox.FaceName("zmin"),
                             new Point3(0, -1e-4, 0), new Point3(0, 1e-4, 1e-4), new Point3(0, 0, 1),
                             new Complex(50, 0), new Em3dReferencePlane(new Point3(0, 0, 5e-5), new Point3(1, 0, 0), 0)),
            ],
        };
        Assert.Empty(sound.Validate());
    }
}

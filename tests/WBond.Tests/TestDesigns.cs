namespace CircuitRF.WBond.Tests;

/// <summary>
/// Realistic wBond designs for the oracle and cost tiers.
///
/// <para>These build the <b>real object graph</b> — arrays, wires, <see cref="Point3"/> lists — not a
/// pre-flattened filament array. That is deliberate: brief-wbond-wba §3 exists because the design
/// note's kernel costs were taken on flat scalars in a tight loop, and the whole question is what
/// happens when the fill walks the model instead.</para>
/// </summary>
internal static class TestDesigns
{
    /// <summary>
    /// The stated worst case: 600 wires of 7 points each, in 12 arrays of 50, on a realistic
    /// packaging pitch. Six filaments per wire, 3,600 filaments total.
    /// </summary>
    public static WBondDesign PowerAmplifier(int wireCount = 600, int arrayCount = 12, int pointsPerWire = 7)
    {
        var design = new WBondDesign();
        int perArray = wireCount / arrayCount;

        for (int a = 0; a < arrayCount; a++)
        {
            var array = new WireArray { Name = $"G{a + 1}" };

            for (int w = 0; w < perArray; w++)
            {
                // Arrays are laid out side by side; wires within an array run on a 6 mil pitch.
                double y = a * 400.0 + w * 6.0;
                array.Wires.Add(BallBond(
                    startX: 0.0, endX: 100.0, y: y,
                    startZ: 4.0, endZ: 1.0, loopHeight: 22.0,
                    points: pointsPerWire));
            }

            design.Arrays.Add(array);
        }

        return design;
    }

    /// <summary>
    /// A single horizontal wire of the given length at the given height — the geometry the
    /// wire-over-ground closed form applies to.
    /// </summary>
    public static WBondDesign SingleHorizontalWire(double lengthMil, double heightMil, double diameterMil)
    {
        var wire = new Wire
        {
            Points =
            {
                Point3.Mils(0, 0, heightMil),
                Point3.Mils(lengthMil, 0, heightMil),
            },
            DiameterNm = WBondUnits.ToNm(diameterMil, WBondUnit.Mil),
        };

        return new WBondDesign
        {
            Arrays = { new WireArray { Name = "G1", Wires = { wire } } },
        };
    }

    /// <summary>
    /// N identical parallel horizontal wires on a uniform pitch, all in one array — the geometry the
    /// (L_s + (N−1)M)/N closed form applies to when the mutuals are made equal.
    /// </summary>
    public static WBondDesign ParallelArray(int n, double pitchMil, double lengthMil, double heightMil,
                                            double diameterMil = 1.0, int arrays = 1)
    {
        var design = new WBondDesign();
        for (int a = 0; a < arrays; a++)
            design.Arrays.Add(new WireArray { Name = $"A{a}" });

        for (int i = 0; i < n; i++)
        {
            var wire = new Wire
            {
                Points =
                {
                    Point3.Mils(0, i * pitchMil, heightMil),
                    Point3.Mils(lengthMil, i * pitchMil, heightMil),
                },
                DiameterNm = WBondUnits.ToNm(diameterMil, WBondUnit.Mil),
            };
            design.Arrays[i * arrays / n].Wires.Add(wire);
        }

        return design;
    }

    /// <summary>
    /// A ball-bond profile: vertical rise from the ball, a kink, an arc over, a shallow descent to
    /// the stitch. All dimensions in mils.
    /// </summary>
    public static Wire BallBond(double startX, double endX, double y,
                                double startZ, double endZ, double loopHeight, int points)
    {
        if (points < 2)
            throw new ArgumentOutOfRangeException(nameof(points), "A wire needs at least 2 points.");

        var wire = new Wire();
        double span = endX - startX;

        for (int i = 0; i < points; i++)
        {
            double s = (double)i / (points - 1);

            // Height above the chord: a skewed arc peaking at ~30 % of the span, which is what a
            // ball bond actually does — it is not a symmetric catenary.
            double shape = Math.Sin(Math.PI * Math.Pow(s, 0.75));
            double chordZ = startZ + (endZ - startZ) * s;

            wire.Points.Add(Point3.Mils(startX + span * s, y, chordZ + loopHeight * shape));
        }

        return wire;
    }
}

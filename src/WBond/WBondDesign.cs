namespace CircuitRF.WBond;

/// <summary>
/// A point in 3D space, in integer nanometres (DBU), z measured from the ground plane.
/// Integer storage is what makes unit switching lossless (<see cref="WBondUnits"/>).
/// </summary>
public readonly record struct Point3(long X, long Y, long Z)
{
    public static Point3 FromMetres(double x, double y, double z) =>
        new(WBondUnits.FromMetres(x), WBondUnits.FromMetres(y), WBondUnits.FromMetres(z));

    /// <summary>Convenience for authoring and tests: a point in mils.</summary>
    public static Point3 Mils(double x, double y, double z) => new(
        WBondUnits.ToNm(x, WBondUnit.Mil),
        WBondUnits.ToNm(y, WBondUnit.Mil),
        WBondUnits.ToNm(z, WBondUnit.Mil));
}

/// <summary>
/// One bond wire: a polyline of at least two points, with a diameter and a metal.
///
/// <para><b><see cref="Points"/> is the truth, and now the ONLY truth (D1 / WB2).</b> A wire is
/// always a 3D polyline — that is what the solver consumes and what <c>.wBond</c> stores. Nothing
/// records which shape a wire was generated from: loop profiles, the ball/wedge designation and the
/// binding between them were removed on 2026-08-18 (see <see cref="LoopShape"/>), because a shared
/// bindable shape is not something anyone wanted and per-wire freedom is.</para>
///
/// <para><b>Direction is data, not a rendering convention (D2 / WB3).</b> <c>Points[0]</c> is the
/// input — current enters there — and <c>Points[^1]</c> is the output. The sign of every mutual
/// inductance depends on it, so reversing a wire negates that wire's off-diagonal row and column of
/// the inductance matrix. Direction is never silently re-inferred; only an explicit reverse
/// changes it.</para>
/// </summary>
public sealed class Wire
{
    /// <summary>The polyline, input end first. At least two points.</summary>
    public List<Point3> Points { get; init; } = [];

    /// <summary>Wire diameter in nanometres. The shipped default is 1 mil.</summary>
    public long DiameterNm { get; set; } = WBondUnits.ToNm(1.0, WBondUnit.Mil);

    /// <summary>The metal, by <see cref="WireMaterial.Name"/>. Defaults to gold (D7).</summary>
    public string Material { get; set; } = WireMaterials.Default.Name;

    public bool Locked { get; set; }

    /// <summary>Wire radius in metres — what the physics layer actually wants.</summary>
    public double RadiusMetres => WBondUnits.ToMetres(DiameterNm) / 2.0;

    /// <summary>
    /// <b>The loop height: the wire's maximum z minus its minimum z.</b> This is the definition
    /// (wbond.md §3.1a) and this property is the one place it lives — every other loop-height
    /// quantity in the codebase is measured or set through it.
    ///
    /// <para><b>It is NOT the rise above the chord</b>, and the difference is not academic. In
    /// chip-and-wire the two feet are usually at different z — a die pad up to a substrate lead — so
    /// the straight line joining them is tilted, and the crest's height above that tilted line is
    /// smaller than its height above the lower foot. A bonder is set up against the second number:
    /// loop height is what a wire-bonder operator measures from the lower pad to the top of the loop.
    /// Reporting the first under that name would read low on exactly the asymmetric loops where it
    /// matters most.</para>
    ///
    /// <para><b>Consequence worth knowing: a wire's loop height can never be below its own foot
    /// drop.</b> With the feet |z₁ − z₂| apart, even a perfectly straight wire measures that much, so
    /// that is the floor any requested loop height is clamped to — see
    /// <see cref="LoopShape.Write"/>.</para>
    /// </summary>
    public long LoopHeightNm
    {
        get
        {
            if (Points.Count == 0) return 0;

            long min = Points[0].Z, max = Points[0].Z;
            foreach (var p in Points)
            {
                if (p.Z < min) min = p.Z;
                if (p.Z > max) max = p.Z;
            }
            return max - min;
        }
    }

    /// <summary>The unavoidable part of the loop height — how far apart in z the two feet are.</summary>
    public long FootDropNm =>
        Points.Count < 2 ? 0 : Math.Abs(Points[^1].Z - Points[0].Z);

    /// <summary>Straight-line 3D distance from the input foot to the output foot.</summary>
    public double ChordLengthMetres()
    {
        if (Points.Count < 2) return 0.0;
        var a = Points[0];
        var b = Points[^1];
        double dx = WBondUnits.ToMetres(b.X - a.X);
        double dy = WBondUnits.ToMetres(b.Y - a.Y);
        double dz = WBondUnits.ToMetres(b.Z - a.Z);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>Total developed length along the polyline.</summary>
    public double PathLengthMetres()
    {
        double total = 0.0;
        for (int i = 1; i < Points.Count; i++)
        {
            var a = Points[i - 1];
            var b = Points[i];
            double dx = WBondUnits.ToMetres(b.X - a.X);
            double dy = WBondUnits.ToMetres(b.Y - a.Y);
            double dz = WBondUnits.ToMetres(b.Z - a.Z);
            total += Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        return total;
    }

    /// <summary>Reverses the current-direction convention (WB26b). Explicit, never inferred.</summary>
    public void Reverse() => Points.Reverse();
}

/// <summary>
/// A named group of wires that share a pin pair on the schematic symbol and are reduced together
/// onto the array basis (wbond.md §3.4). Names like G1, G2, D1, MT are the packaging convention.
/// </summary>
public sealed class WireArray
{
    public required string Name { get; set; }

    public List<Wire> Wires { get; init; } = [];
}

/// <summary>
/// The ground plane the method of images reflects in (wbond.md §3.2).
///
/// <para>The plane is at z = 0 by construction — the model's z origin <i>is</i> the ground
/// reference — so there is no height field here. Disabling it removes the image contribution
/// entirely, at which point the return path must come from wires declared as the reference
/// (WB20, and that refusal belongs to WB-B).</para>
/// </summary>
public sealed class GroundPlane
{
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// The root of a wBond design: one per wBond component instance (wbond.md §2.1).
/// </summary>
public sealed class WBondDesign
{
    public GroundPlane GroundPlane { get; init; } = new();

    /// <summary>Operating temperature in °C. Default 85 (WB4a) — load-bearing for R, so it is a field.</summary>
    public double OperatingTempC { get; set; } = WireMaterials.DefaultOperatingTempC;

    /// <summary>Metals available to this design. Defaults to the shipped table.</summary>
    public List<WireMaterial> Materials { get; init; } = [.. WireMaterials.All];

    /// <summary>
    /// Whether the model includes <b>capacitance to the reference plane</b> (wbond.md §3.7).
    ///
    /// <para><b>The one wBond setting whose default changes the answer for designs that already
    /// exist.</b> Every other default reproduces prior behaviour; this one is <c>true</c>, so a
    /// <c>.wBond</c> written before capacitance existed loads with it ON and its component stamps
    /// shunt capacitors it did not stamp before. That is deliberate — a bond wire has capacitance —
    /// and turning it off reproduces the old answer exactly, bit for bit (gate C1).</para>
    ///
    /// <para><b>Off means NOT COMPUTED, not "computed and stamped as zero".</b>
    /// <see cref="ImpedanceReduction"/> never fills <b>P</b>, never factorises it, and the stamp emits
    /// exactly what it emitted before. Cross-wire capacitance is not a second switch: dropping the
    /// cross terms would bias every multi-wire array's own capacitance HIGH by tens of percent, in
    /// the optimistic direction — see <see cref="CapacitanceReduction.WireGroundCapacitance"/>.</para>
    ///
    /// <para>With the ground plane disabled there is nothing to be capacitive TO, and the reduction
    /// returns null whatever this says (<see cref="CapacitanceReduction.Create(WBondDesign, bool)"/>).</para>
    /// </summary>
    public bool IncludeCapacitance { get; set; } = true;

    /// <summary>
    /// The frequency, in GHz, the Array Inductance panel quotes its effective inductance at
    /// (wbond.md §6.8). Default 10.
    ///
    /// <para><b>It is a READOUT setting and must never reach <c>Stamp</c>.</b> A reader will assume
    /// otherwise, so it is said here: the schematic's own analysis sweep is what the engine stamps
    /// against, and this number decides only which frequency the panel's own number is quoted at. A
    /// test asserts it (gate: <c>ReadoutFrequency_NeverReachesTheStamp</c>).</para>
    ///
    /// <para>Once shunt capacitance exists the inductance seen at the terminals is genuinely a
    /// function of frequency, which is why the panel needs to say which one it is quoting; before
    /// capacitance it reported the frequency-independent partial inductance and needed no such row.
    /// 10 GHz because a representative 1 mm gold wire at 250 µm height is ≈ 1 nH and ≈ 15 fF, so its
    /// SRF is ≈ 40 GHz: high enough that the default never lands in the above-resonance state, low
    /// enough that the ≈ +6 % bump is visible.</para>
    /// </summary>
    public double ReadoutFrequencyGHz { get; set; } = 10.0;

    public List<WireArray> Arrays { get; init; } = [];

    /// <summary>
    /// Layout geometry embedded from the originating workspace, as <b>opaque JSON</b>.
    ///
    /// <para><b>WB-A stores and round-trips this without interpreting a byte of it</b> (R-wb-11).
    /// The <c>.clay</c> model lives in <c>CircuitRF.Ui</c>, which references Avalonia, so parsing it
    /// here would breach the firewall. WB-C — where the layout model is reachable — is what fills it
    /// in, resolves cell references and flattens PDK PCells. Until then the contract is simply that
    /// a load/save cycle must not lose or alter it.</para>
    /// </summary>
    public string? EmbeddedGeometryJson { get; set; }

    /// <summary>Free-form view state (projection azimuth, units, dot/line sizes), also opaque here.</summary>
    public string? ViewStateJson { get; set; }

    /// <summary>
    /// This design's own assembly rule file (`.wasm`), as a path relative to the `.wBond`'s own
    /// directory — or null, which means "use the workspace default" (wbond.md §8, WB31).
    ///
    /// <para><b>Null is the normal case and is not an error.</b> A shop that bonds at one house states
    /// it once on the workspace; a document only carries a reference of its own when it deliberately
    /// deviates — one product qualified at a second house. That is the same convention `.clay`'s
    /// <c>TechRef</c> already follows, and it is what keeps Save-As and folder moves from having to
    /// rewrite a relative path.</para>
    ///
    /// <para>Additive and nullable: the `.wBond` <c>FormatVersion</c> is NOT bumped for it, and a file
    /// that never set one round-trips byte-identically.</para>
    /// </summary>
    public string? AssemblyRef { get; set; }

    /// <summary>Every wire in the design, in array order then member order.</summary>
    public IEnumerable<Wire> AllWires() => Arrays.SelectMany(a => a.Wires);

    public int WireCount => Arrays.Sum(a => a.Wires.Count);

    /// <summary>
    /// Resolves a wire's metal, falling back to the design's first material if the name is unknown
    /// rather than throwing — an unresolvable metal is a load-time diagnostic, not a fill-time crash.
    /// </summary>
    public WireMaterial MaterialFor(Wire wire) =>
        Materials.FirstOrDefault(m => string.Equals(m.Name, wire.Material, StringComparison.OrdinalIgnoreCase))
        ?? WireMaterials.ByName(wire.Material)
        ?? Materials.FirstOrDefault()
        ?? WireMaterials.Default;

    /// <summary>
    /// Checks the structural invariants the array reduction depends on (R-wb-1).
    ///
    /// <para><b>An empty array is refused here rather than in the linear algebra.</b> It makes the
    /// mapping matrix <b>A</b> rank-deficient and <c>L_arr</c> singular, and the failure would
    /// otherwise surface as a confusing Cholesky error far from its cause.</para>
    ///
    /// <para><b>NO arrays, on the other hand, is a valid design</b> (owner, 2026-08-16: "make it
    /// support 0 wires"). The two look alike and are not: an empty ARRAY is a named terminal with
    /// nothing behind it, which the reduction cannot describe; an empty DESIGN is a document the user
    /// has not drawn in yet, or has just cleared, and every quantity it publishes is honestly empty —
    /// no wires, no groups, no matrix, a panel with no rows. Refusing it meant the editor could not
    /// delete its own last wire, and the message it gave for trying was about matrix rank.</para>
    ///
    /// <para>A design with no arrays is still not a thing you can SIMULATE: a placed component needs
    /// pins. That refusal belongs to the component, and lives in <c>WBondModel</c>.</para>
    /// </summary>
    public void Validate()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var array in Arrays)
        {
            if (string.IsNullOrWhiteSpace(array.Name))
                throw new InvalidOperationException("wBond array has no name.");

            if (!seen.Add(array.Name))
                throw new InvalidOperationException(
                    $"Duplicate wBond array name '{array.Name}'. Array names are the symbol's pin names " +
                    "and must be unique.");

            if (array.Wires.Count == 0)
                throw new InvalidOperationException(
                    $"wBond array '{array.Name}' has no wires. An empty array makes the mapping matrix " +
                    "rank-deficient and the array-basis inductance singular — delete it, or move a wire " +
                    "into it.");

            foreach (var wire in array.Wires)
            {
                if (wire.Points.Count < 2)
                    throw new InvalidOperationException(
                        $"A wire in array '{array.Name}' has {wire.Points.Count} point(s); a wire needs at least 2.");

                if (wire.DiameterNm <= 0)
                    throw new InvalidOperationException(
                        $"A wire in array '{array.Name}' has a non-positive diameter.");
            }
        }
    }
}

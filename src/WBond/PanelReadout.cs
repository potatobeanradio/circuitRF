namespace CircuitRF.WBond;

/// <summary>
/// What the inductance panel shows, as plain data (wbond.md §6.8; R-wbc-7).
///
/// <para><b>Picohenries, fixed, never auto-ranged (WB27a / D9.)</b> The panel exists for comparison
/// during a drag — across arrays, and against the same array a second ago. A readout that silently
/// switches nH ↔ pH mid-drag makes a number appear to jump by 1000x when the geometry moved by a
/// mil, which is precisely the illusion a live readout must not create. Wirebond inductances live in
/// the tens-to-thousands of pH, so one unit covers the whole useful range.</para>
///
/// <para>A data record, not a view-model: the bindings and the formatting belong to WB-C2.</para>
/// </summary>
public sealed class PanelReadout
{
    /// <summary>One array's row in the panel.</summary>
    /// <param name="Name">The array name, which is also its symbol pin name.</param>
    /// <param name="SelfPicoHenries">L_arr diagonal, pH.</param>
    /// <param name="MutualPicoHenries">Mutual to every array including itself, pH.</param>
    /// <param name="CouplingCoefficients">
    /// k = M/sqrt(Lii*Ljj). Offered alongside the pH mutuals because it is scale-free, and it is the
    /// number that says whether two arrays are meaningfully coupled — a bare pH mutual does not,
    /// without mentally dividing by the selfs.
    /// </param>
    /// <param name="WireCount">How many wires are in the array.</param>
    /// <param name="TotalLengthMm">Total developed wire length, mm.</param>
    /// <param name="MaxLandingSpanMm">
    /// The largest distance between any two of the array's landing points. Reported, never warned
    /// about (WB9a): it is the proxy for "is the equipotential-pad assumption being stretched", and
    /// there is no threshold separating good from bad without sheet resistance and frequency.
    /// </param>
    /// <param name="CurrentShares">
    /// Per-wire current for 1 A into this array. Edge wires carry measurably more than centre ones —
    /// real physics the reduction captures for free.
    /// </param>
    public readonly record struct ArrayRow(
        string Name,
        double SelfPicoHenries,
        IReadOnlyList<double> MutualPicoHenries,
        IReadOnlyList<double> CouplingCoefficients,
        int WireCount,
        double TotalLengthMm,
        double MaxLandingSpanMm,
        IReadOnlyList<double> CurrentShares);

    public required IReadOnlyList<ArrayRow> Rows { get; init; }

    /// <summary>
    /// What the return path currently is — always stated, because reporting an inductance without one
    /// is the most common way a bondwire model is wrong (WB20 / RW13).
    /// </summary>
    public required string ReturnPath { get; init; }

    /// <summary>Builds the readout from a reduction and its design.</summary>
    public static PanelReadout Build(WBondDesign design, WireMesh mesh, ArrayReduction reduction)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(reduction);

        int m = reduction.ArrayCount;
        var rows = new List<ArrayRow>(m);

        for (int a = 0; a < m; a++)
        {
            var drive = new double[m];
            drive[a] = 1.0;
            var allShares = reduction.CurrentShares(drive);

            var shares = new List<double>();
            for (int w = 0; w < mesh.WireCount; w++)
                if (mesh.ArrayOfWire[w] == a) shares.Add(allShares[w]);

            var mutuals = new double[m];
            var coupling = new double[m];
            for (int b = 0; b < m; b++)
            {
                mutuals[b] = reduction.PicoHenries(a, b);
                coupling[b] = reduction.CouplingCoefficient(a, b);
            }

            var wires = design.Arrays[a].Wires;
            rows.Add(new ArrayRow(
                Name: design.Arrays[a].Name,
                SelfPicoHenries: reduction.PicoHenries(a, a),
                MutualPicoHenries: mutuals,
                CouplingCoefficients: coupling,
                WireCount: wires.Count,
                TotalLengthMm: wires.Sum(w => w.PathLengthMetres()) * 1e3,
                MaxLandingSpanMm: MaxLandingSpan(wires) * 1e3,
                CurrentShares: shares));
        }

        return new PanelReadout
        {
            Rows = rows,
            ReturnPath = design.GroundPlane.Enabled
                ? "image plane at z = 0"
                : "UNDECLARED — the ground plane is disabled and no array is nominated as the return",
        };
    }

    /// <summary>
    /// The largest distance between any two landing points of an array (WB9a's diagnostic).
    /// Both feet of every wire count — an array can span a long lead frame finger at either end.
    /// </summary>
    private static double MaxLandingSpan(IReadOnlyList<Wire> wires)
    {
        var feet = new List<Point3>(wires.Count * 2);
        foreach (var wire in wires)
        {
            if (wire.Points.Count < 2) continue;
            feet.Add(wire.Points[0]);
            feet.Add(wire.Points[^1]);
        }

        double worst = 0.0;
        for (int i = 0; i < feet.Count; i++)
        {
            for (int j = i + 1; j < feet.Count; j++)
            {
                double dx = WBondUnits.ToMetres(feet[j].X - feet[i].X);
                double dy = WBondUnits.ToMetres(feet[j].Y - feet[i].Y);
                double dz = WBondUnits.ToMetres(feet[j].Z - feet[i].Z);
                worst = Math.Max(worst, Math.Sqrt(dx * dx + dy * dy + dz * dz));
            }
        }

        return worst;
    }
}

// L8e — D5/R-res-7: the per-cell current density, reduced IN THE ENGINE and documented once, next
// to the basis it reduces.
//
// L8b left exactly one provision for §10.5's heat map — "one per-cell scalar on
// LayoutRenderer.DrawPlanarMeshOverlay" — and nothing else. L8d returns per-BASIS currents
// (PlanarPortSolution.Currents), so the reduction from those to a per-cell |J| is this slice's, and
// it belongs here rather than in the renderer: a renderer-side approximation would be a second,
// undocumented definition of a physical quantity, and the first time it disagreed with the solve
// nobody would know which one was wrong.
//
// ════════════════════════════════════════════════════════════════════════════════════════════════
// THE REDUCTION, STATED ONCE
// ════════════════════════════════════════════════════════════════════════════════════════════════
//
// L8c normalises each rooftop to UNIT TOTAL CURRENT ACROSS ITS SHARED EDGE (PlanarBasisFunctions'
// own header), so a basis coefficient I_b is in AMPERES: it is the whole current crossing that edge,
// not a density. Along its own direction the rooftop is a linear ramp — 0 at the far edge of cell A,
// I_b at the shared edge, 0 again at the far edge of cell B — so the current crossing a cell's own
// CENTRE plane is exactly half of each covering rooftop's coefficient:
//
//     I_x(cell) = ½ · Σ_{x-rooftops covering the cell} I_b          [A]
//     J_x(cell) = I_x(cell) / (the cell's TRANSVERSE extent)        [A/m]
//
// and the same in y. |J| is the magnitude of the complex vector (J_x, J_y):
//
//     |J| = sqrt(|J_x|² + |J_y|²)                                   [A/m]
//
// Two consequences worth having written down rather than rediscovered:
//
//   • AN OUTERMOST CELL CARRIES HALF, AND THAT IS CORRECT, NOT A BUG. The end cell of a line is
//     covered by ONE rooftop, so its centre plane carries ½·I_port. Half the port current really
//     does cross the middle of the outermost half-cell; a map that showed the full port current
//     there would be the wrong picture.
//   • THE EXACT IDENTITY IS AGAINST THE TWO ADJACENT EDGE CURRENTS, NOT AGAINST THE PORT CURRENT.
//     Summing J_x·(transverse extent) across one transverse column gives exactly the MEAN of the
//     currents crossing that column's two bounding edges — which is what PlanarExcitation.LineCurrent
//     reports. On an electrically short uniform line those two are both ≈ the port current, which is
//     why the map reads flat along the line; the exact statement is the mean, and it is what Tier 4
//     pins to machine precision.
//
// D5's three scoping rules are enforced by the SIGNATURE rather than by discipline: this takes ONE
// solution column (one driven port) at ONE frequency, so a map that superposed every port — a map of
// nothing — or that spanned a sweep is not expressible here.

using System.Numerics;
using NumFlat;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// A per-cell current-density map for ONE driven port at ONE frequency, indexed by L8b's own cell
/// order (R-msh-2) — the order the mesh, the fill, the port excitation and the overlay all share.
/// </summary>
/// <param name="Magnitude">|J| per cell, A/m.</param>
/// <param name="Jx">The x component per cell, A/m — kept because a direction-resolved map is what
/// tells a user whether current is turning a corner or crowding along an edge.</param>
/// <param name="Jy">The y component per cell, A/m.</param>
/// <param name="MaxMagnitude">The largest |J| on the mesh, A/m — the map's own normalisation, which
/// R-res-8 requires be SHOWN rather than implied.</param>
/// <param name="Iz">
/// <b>L9d/D4 — the VERTICAL map, and it is a different OBJECT rather than a third component.</b>
///
/// <para>A via basis's coefficient is the whole current crossing its shared FOOTPRINT — an AREA, not
/// an edge — so the quantity that is well defined per cell is a CURRENT in amperes, not a sheet
/// density in A/m. Dividing it by the footprint area to make it look like <paramref name="Jx"/> and
/// <paramref name="Jy"/> would produce A/m², and <c>sqrt(|Jx|² + |Jy|² + |Jz|²)</c> would then be
/// adding two different dimensions and colouring the result — the map would mean one thing on a cell
/// with a via under it and another on every cell without. So the via current is carried separately,
/// in amperes, and is <b>never folded into <paramref name="Magnitude"/></b>.</para>
///
/// <para>Indexed by L8b's own cell order like everything else, and non-zero on exactly the two FOOT
/// cells of each vertical basis — the same current leaves the lower level and arrives at the upper
/// one, so both feet carry it with the same sign. It is an annotation on the two levels a via joins,
/// which is what it physically is.</para>
/// </param>
/// <param name="MaxViaCurrent">The largest |I_z| anywhere, amperes. Zero when the mesh has no via.</param>
public sealed record PlanarCurrentDensityMap(
    IReadOnlyList<double>  Magnitude,
    IReadOnlyList<Complex> Jx,
    IReadOnlyList<Complex> Jy,
    double                 MaxMagnitude,
    int                    DrivenPort,
    double                 FrequencyHz,
    IReadOnlyList<Complex>? Iz = null,
    double                 MaxViaCurrent = 0)
{
    /// <summary>Whether this mesh carried any vertical (via) current at all.</summary>
    public bool HasVerticalCurrent => MaxViaCurrent > 0;

    /// <summary>The via current in cell <paramref name="cell"/>, amperes; zero where there is no via.</summary>
    public Complex ViaCurrent(int cell)
        => Iz is { } iz && cell >= 0 && cell < iz.Count ? iz[cell] : Complex.Zero;

    /// <summary>R-res-8 for the vertical map, whose units are deliberately NOT the horizontal
    /// map's — see <see cref="Iz"/> for why the two are not one quantity.</summary>
    public string VerticalScaleCaption =>
        HasVerticalCurrent
            ? $"Via current |I_z|, 0 … {SurfaceMesher.Eng(MaxViaCurrent)}A, at the two foot cells of " +
              $"each via. This is a CURRENT in amperes, not a sheet density — a via's unit current " +
              $"crosses an area rather than an edge — so it is reported and normalised separately " +
              $"from |J| and is never added into it."
            : "No via current: this mesh carries no vertical basis.";

    /// <summary>0…1 for the colour ramp. A flat (all-zero) map normalises to 0 rather than NaN.</summary>
    public double Normalised(int cell)
        => MaxMagnitude > 0 && cell >= 0 && cell < Magnitude.Count
            ? Math.Clamp(Magnitude[cell] / MaxMagnitude, 0, 1)
            : 0;

    /// <summary>
    /// R-res-8 — the scale, with its UNITS and its NORMALISATION, as one line the panel prints
    /// verbatim. An unlabelled rainbow over an unstated normalisation is decoration; §10.5 asks for
    /// a diagnostic.
    /// </summary>
    public string ScaleCaption =>
        $"Surface current density |J|, 0 … {SurfaceMesher.Eng(MaxMagnitude)}A/m " +
        $"(normalised to this map's own peak), port {DrivenPort} driven at 1 V, " +
        $"{SurfaceMesher.Eng(FrequencyHz)}Hz. One port at a time and one frequency: a map that " +
        "superposed every excitation would be a map of nothing.";
}

public static class PlanarCurrentDensity
{
    /// <summary>
    /// The reduction above, over one solution column.
    /// </summary>
    /// <param name="basisCurrents">One column of <see cref="PlanarPortSolution.Currents"/> — the
    /// basis currents when a single port is driven at 1 V.</param>
    public static PlanarCurrentDensityMap Compute(
        PlanarMesh mesh, Vec<Complex> basisCurrents, int drivenPortNumber, double fHz)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (basisCurrents.Count != mesh.Bases.Count)
            throw new ArgumentException(
                $"The solution has {basisCurrents.Count} unknowns and the mesh has " +
                $"{mesh.Bases.Count} basis functions — these are not the same solve.",
                nameof(basisCurrents));

        int n = mesh.Cells.Count;
        var ix = new Complex[n];
        var iy = new Complex[n];
        var iz = new Complex[n];

        // ½ per covering rooftop — the ramp's own value at the cell centre.
        //
        // L9d/D4: the three directions are switched on EXPLICITLY. Before L9d this was an if/else on
        // "is it X", which silently counted a vertical basis as a Y one the moment a mesh had a via
        // — a wrong picture rather than a missing one, which is the failure mode worth spelling out.
        for (int b = 0; b < mesh.Bases.Count; b++)
        {
            var bs = mesh.Bases[b];
            Complex i = basisCurrents[b];
            Complex half = 0.5 * i;
            switch (bs.Direction)
            {
                case PlanarBasisDirection.X:
                    ix[bs.CellA] += half;
                    ix[bs.CellB] += half;
                    break;
                case PlanarBasisDirection.Y:
                    iy[bs.CellA] += half;
                    iy[bs.CellB] += half;
                    break;
                default:
                    // A via's current does not ramp: it is the SAME current at both feet, so both
                    // carry the whole of it rather than half. That is not the rooftop's ½ with a
                    // different constant — it is a different quantity (see PlanarCurrentDensityMap.Iz).
                    iz[bs.CellA] += i;
                    iz[bs.CellB] += i;
                    break;
            }
        }

        var jx  = new Complex[n];
        var jy  = new Complex[n];
        var mag = new double[n];
        double max = 0;

        for (int c = 0; c < n; c++)
        {
            var cell = mesh.Cells[c];
            // A current flowing in x spreads across the cell's y extent, and vice versa.
            jx[c] = cell.Height > 0 ? ix[c] / cell.Height : Complex.Zero;
            jy[c] = cell.Width  > 0 ? iy[c] / cell.Width  : Complex.Zero;

            double m = Math.Sqrt(jx[c].Magnitude * jx[c].Magnitude + jy[c].Magnitude * jy[c].Magnitude);
            mag[c] = m;
            if (m > max) max = m;
        }

        double maxVia = 0;
        bool anyVia = false;
        for (int c = 0; c < n; c++)
        {
            double m = iz[c].Magnitude;
            if (m > 0) anyVia = true;
            if (m > maxVia) maxVia = m;
        }

        return new PlanarCurrentDensityMap(mag, jx, jy, max, drivenPortNumber, fHz,
                                           anyVia ? iz : null, maxVia);
    }

    /// <summary>
    /// The total current crossing one transverse column's centre plane, <c>Σ J·(transverse
    /// extent)</c> over the given cells — the quantity Tier 4 checks against
    /// <see cref="PlanarExcitation.LineCurrent"/>'s two adjacent edge currents.
    /// </summary>
    public static Complex ColumnCurrent(
        PlanarMesh mesh, PlanarCurrentDensityMap map,
        IEnumerable<int> cells, PlanarBasisDirection direction)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(map);

        Complex sum = Complex.Zero;
        foreach (int c in cells)
        {
            var cell = mesh.Cells[c];
            sum += direction == PlanarBasisDirection.X
                ? map.Jx[c] * cell.Height
                : map.Jy[c] * cell.Width;
        }
        return sum;
    }

    /// <summary>
    /// Every cell whose own axis-index along <paramref name="direction"/> equals
    /// <paramref name="index"/> — one transverse column (or row) of the tensor grid, in L8b's cell
    /// order. A convenience for the callers that need a cut, so nobody re-derives the index rule.
    /// </summary>
    public static IReadOnlyList<int> Column(
        PlanarMesh mesh, PlanarBasisDirection direction, int index, int layerIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var list = new List<int>();
        for (int c = 0; c < mesh.Cells.Count; c++)
        {
            var cell = mesh.Cells[c];
            if (cell.LayerIndex != layerIndex) continue;
            int i = direction == PlanarBasisDirection.X ? cell.IX : cell.IY;
            if (i == index) list.Add(c);
        }
        return list;
    }
}

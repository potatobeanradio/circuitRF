// A mode's field read at a port, and |Z| across the whole plane at a chosen frequency
// (docs/sonnet-briefs/brief-railrf-15-modes-and-maps.md R-rail15-2, R-rail15-3;
//  docs/design/railrf.md §2.4 "The plane resonances and the impedance map", §4.4).
//
// ── WHERE A MODE LANDS IS THE FINDING, NOT THE FREQUENCY ───────────────────────────────────────
//
// §2.4, and it is the sentence this whole file exists for:
//
//     A mode whose maximum sits on the load pin field is a problem; the same mode with its maximum
//     IN A CORNER IS NOT, and only the map distinguishes them.
//
// So a mode list is not a list of frequencies. "Your board has a mode at 1.2 GHz" is not something
// anybody can act on; "your board has a mode at 1.2 GHz with its maximum on U1's power pins" is.
// <see cref="ValueAtPort"/> is that second sentence's second half, and it is why the mode rows this
// series reports carry a per-port column rather than only an f₀.
//
// ── A PORT IS A PIN FIELD, SO THE READING IS THE PEAK AND NEVER THE MEAN ───────────────────────
//
// §4.3 ties every cell under a load's pads into ONE port, because that is what the die sees. A mode
// with a null running through the middle of a BGA's power field averages to nearly nothing over it
// while being at full amplitude on both halves — which is the OPTIMISTIC direction, and exactly the
// answer §9 says nobody investigates. The port's reading is therefore the largest magnitude over
// its own cells, and the cell it was at travels with it so the map can be pointed at.
//
// ── THE |Z| MAP IS A SOLVE, AND IT IS THE SOLVE EVERYTHING ELSE USES ───────────────────────────
//
// §4.4: "Assemble one sparse complex MNA system per frequency and solve for the port impedances via
// CSparse's LU — THE SAME NUMERICAL LAYER every other circuitRF analysis uses." Inject one amp at
// the driven cell and every node voltage IS the transfer impedance to that cell, in ohms, in one
// solve — so the map is not a second arithmetic beside the curve, it is the same system read at
// every node instead of at one.
//
// AND IT IS THE LOSSY SYSTEM, WHICH IS NOT THE SYSTEM THE MODES CAME OUT OF. §4.5's eigenproblem is
// loss-free because that is what makes its eigenvalues real; a map at a frequency ON a mode would
// divide by zero in a loss-free system and is perfectly finite in the real one. Both read the same
// PdnCavitySystem and each reads the terms it is of, which is the arrangement that stops them
// drifting apart.
//
// NUMBERS ARE BASE SI. Hertz, ohms, farads, henries, siemens.

using System.Numerics;

namespace CircuitRF.Engine.Pdn;

/// <summary>
/// What one mode's field reads at one port.
/// </summary>
/// <param name="Value">The field at the port's own worst cell, SIGNED — so two ports on opposite
/// sides of a mode's null can be seen to be out of phase, which is the half that says it is one
/// mode rather than two.</param>
/// <param name="Magnitude">Its magnitude, 0 … 1 against the mode's own peak (see
/// <see cref="PdnMode.Field"/>). <b>1 means this port IS where the mode is worst.</b></param>
/// <param name="Cell">Which of the port's cells it was read at.</param>
public readonly record struct PdnFieldReading(double Value, double Magnitude, int Cell);

/// <summary>§2.4's field readings and impedance map. Numerics only.</summary>
public static class PdnFieldMap
{
    /// <summary>
    /// A mode's field over one port's cells — <b>the peak, never the mean</b> (see this file's
    /// header).
    /// </summary>
    /// <param name="field">The mode's own field, one value per cell.</param>
    /// <param name="cells">The port's cells. A pin field is more than one (§4.3).</param>
    /// <returns>The reading, or null where the port names no cell this cavity has — a load on
    /// copper that is not plane pair, which is a real board and not an error.</returns>
    public static PdnFieldReading? ValueAtPort(IReadOnlyList<double> field, IEnumerable<int> cells)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(cells);

        int best = -1;
        double bestMagnitude = -1;

        foreach (int cell in cells)
        {
            if (cell < 0 || cell >= field.Count) continue;
            double m = Math.Abs(field[cell]);
            if (m > bestMagnitude) { bestMagnitude = m; best = cell; }
        }

        return best < 0 ? null : new PdnFieldReading(field[best], Math.Abs(field[best]), best);
    }

    /// <summary>
    /// |Z| across the whole plane at one frequency — <b>the transfer impedance from
    /// <paramref name="driveCell"/> to every cell</b>, in ohms.
    /// </summary>
    /// <remarks>
    /// One amp in at the driven cell, one sparse complex LU, and the node voltages that come back
    /// ARE the impedances (V = Z·I with I = 1). The driven cell's own entry is therefore the port
    /// impedance the curve plots at that frequency, which is the cheapest possible check that the
    /// map and the curve are of the same board.
    ///
    /// <para><b>The reference plane is ground here and that is the model rather than a
    /// simplification</b>: a cavity cell's unknown is the potential ACROSS the plane pair, which is
    /// what §4.1's shunt branch is of and what a decoupling capacitor is connected across.</para>
    /// </remarks>
    /// <param name="system">The cavity. Its loss terms are read here and ignored by the mode
    /// solver — see this file's header.</param>
    /// <param name="driveCells">
    /// The driven port's cells. <b>One amp between them, not one amp each</b> — a port is a PIN
    /// FIELD (§4.3) and the map is of the impedance that field sees, so a BGA's twelve power balls
    /// are twelve cells carrying a twelfth of an amp and not twelve amps.
    ///
    /// <para>Driving ONE cell of such a field instead would add the spreading impedance from a
    /// single point to every number on the map — optimistically wrong nowhere and pessimistically
    /// wrong everywhere near the port, which is precisely where §2.4 says the answer matters.</para>
    /// </param>
    /// <param name="frequencyHz">The frequency to map at. Must be above zero: at DC the plane pair
    /// is an open circuit to its reference and there is no impedance field.</param>
    /// <returns>One complex impedance per cell, or null on a refusal — see
    /// <paramref name="refusal"/>.</returns>
    /// <param name="refusal">Why nothing was solved, or null.</param>
    public static Complex[]? Impedance(
        PdnCavitySystem system, IReadOnlyList<int> driveCells, double frequencyHz,
        out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(driveCells);
        refusal = null;

        int n = system.CellCount;
        if (n <= 0 || system.CapacitanceFarads.Count != n)
        {
            refusal = "This plane pair has no cells to map.";
            return null;
        }

        var driven = driveCells.Where(c => c >= 0 && c < n).Distinct().ToList();
        if (driven.Count == 0)
        {
            refusal =
                "The port this map is driven from resolved to no cell of the plane pair, so there " +
                "is nothing to inject into. An observation port on copper that is not plane pair " +
                "is the ordinary cause.";
            return null;
        }

        if (!(frequencyHz > 0))
        {
            refusal =
                "An impedance map needs a frequency above zero: at DC §4.1's shunt branch vanishes " +
                "and the plane pair is an open circuit to its reference, so every cell reads the " +
                "same thing and it is not an impedance field.";
            return null;
        }

        double w = 2.0 * Math.PI * frequencyHz;

        // Cell i is MNA node i + 1; node 0 is the reference plane (see the remarks above).
        var mna = new MnaSystem(n);

        for (int i = 0; i < n; i++)
        {
            double g = i < system.ConductanceSiemens.Count ? system.ConductanceSiemens[i] : 0.0;
            var y = new Complex(g > 0 ? g : 0.0, w * system.CapacitanceFarads[i]);
            if (y != Complex.Zero) mna.AddAdmittance(i + 1, 0, y);
        }

        int stamped = 0;
        foreach (var e in system.Edges)
        {
            if (e.A == e.B || e.A < 0 || e.A >= n || e.B < 0 || e.B >= n) continue;

            double r = e.ResistanceOhms > 0 ? e.ResistanceOhms : 0.0;
            double x = e.InductanceHenries > 0 ? w * e.InductanceHenries : 0.0;
            if (r <= 0 && x <= 0) continue;

            mna.AddAdmittance(e.A + 1, e.B + 1, Complex.One / new Complex(r, x));
            stamped++;
        }

        if (stamped == 0)
        {
            refusal =
                "This plane pair has no path between any two cells at this frequency, so every " +
                "cell would read its own capacitor and the map would say nothing about the board.";
            return null;
        }

        var share = new Complex(1.0 / driven.Count, 0);
        foreach (int cell in driven) mna.AddCurrentInjection(cell + 1, share);

        try
        {
            var lu = mna.Factorize();
            var rhs = mna.BuildRhs();
            var x = new Complex[mna.Size];
            lu.Solve(rhs, x);

            var z = new Complex[n];
            Array.Copy(x, z, n);
            return z;
        }
        catch (SingularMatrixException ex)
        {
            refusal = "The impedance map's own system is singular: " + ex.Message;
            return null;
        }
    }
}

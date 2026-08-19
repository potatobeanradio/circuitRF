using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.WBond;
using NumFlat;
using RfCore;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// Publishing a wBond design as a Touchstone network (wbond.md §11, brief-wbond-wbe M3 / R-wbe-5).
///
/// <h3>What network a wBond publishes, and why it is this one</h3>
/// <para><b>A wBond exports as an M-port, one port per wire ARRAY, port <i>k</i> being that array's
/// own two terminals (<c>Gk.i</c>, <c>Gk.o</c>).</b> Its impedance matrix is then <b>exactly</b>
/// <see cref="WBondModel.ArrayImpedance"/> — by definition, since the array reduction <i>is</i>
/// <c>v = Z_arr · i</c> in the branch basis. No new physics, no new assumption, and the port count
/// matches the schematic symbol's own array pairs.</para>
///
/// <para>A <b>2-port per array, exported separately</b> throws away every off-diagonal — which is the
/// entire content of a coupled bond array — and is not offered.</para>
///
/// <h3>The file says what its ports are</h3>
/// <para>Port identity is written into the file as <c>! Port[k] = &lt;array name&gt;</c> comments, the
/// form <c>TouchstonePortLabels</c> already reads on the way in. A Touchstone whose port order is
/// undocumented is a file somebody wires backwards.</para>
///
/// <h3>…and that basis cannot carry the capacitance, which is why it is no longer the default</h3>
/// <para>Port <i>k</i> above is a <b>floating pair</b> of terminals — the array's own two — so the
/// network it describes has no global node. Shunt capacitance returns to the ground plane, and in
/// that basis the plane is nowhere: there is no terminal for the shunt current to leave by. So an
/// array-pair file carries the <b>series arm only</b>.</para>
///
/// <para><b>The fix is a different port basis, not a limitation to live with</b> (owner, 2026-08-18).
/// Give every TERMINAL its own port, all referenced to the plane — three arrays export as a
/// <b>6-port</b> — and Touchstone's own implicit common reference node <i>is</i> the ground plane. The
/// shunt capacitors then connect from each port to that reference exactly as they do in the stamp,
/// the inter-array capacitors appear as ordinary port-to-port coupling, and nothing is left out.
/// <see cref="WBondPortBasis.Terminals"/> is that basis and is the default.</para>
///
/// <para>Both are offered because they answer different questions: the terminal basis is
/// <b>complete</b>, and the array-pair basis is <b>compact</b> and matches the schematic symbol's own
/// port pairs — which is all a user wants when there is no capacitance to carry. The file says which
/// one it is, in its own header, and an array-pair file written from a design that DOES have
/// capacitance says what it left out.</para>
///
/// <h3>What this deliberately does NOT do</h3>
/// <para>Z→S and Y→S go through <see cref="RFNetwork"/> and the file is written by
/// <see cref="TouchstoneExporter"/> — the same conversions and the same writer every other export in
/// this repository uses. There is no second conversion here and no second writer.</para>
/// </summary>
/// <summary>What a port MEANS in an exported wBond network.</summary>
public enum WBondPortBasis
{
    /// <summary>
    /// <b>Two ports per array, every terminal referenced to the ground plane</b> — three arrays give a
    /// 6-port. The default, because it is the only basis that can carry the capacitance: Touchstone's
    /// implicit common reference node IS the plane the shunt capacitors return to.
    /// </summary>
    Terminals,

    /// <summary>
    /// <b>One port per array, that array's own two terminals as the port's ± pair</b> — three arrays
    /// give a 3-port, and its impedance matrix is exactly <c>ArrayImpedance(f)</c>.
    ///
    /// <para>Compact, and it matches the schematic symbol's own port pairs. <b>It carries the series
    /// arm only</b>: a floating pair has no terminal for a shunt to the plane to leave by, so a design
    /// with capacitance loses it. The written file says so.</para>
    /// </summary>
    ArrayPairs,
}

public static class WBondTouchstoneExport
{
    /// <summary>
    /// The user's choices. <b>The frequency grid is the user's</b>, deliberately: a bond array is
    /// broadband and has no natural band, so inventing one from the design would be inventing a
    /// claim the design does not make.
    /// </summary>
    public sealed record Options(
        double       Z0Ohms       = 50.0,
        double       StartHz      = 1e8,
        double       StopHz       = 2e10,
        int          Points       = 201,
        bool         Logarithmic  = false,
        int          Digits       = 9,
        char         DigitFormat  = 'g',
        MatrixFormat MatrixFormat = MatrixFormat.RI,
        WBondPortBasis PortBasis  = WBondPortBasis.Terminals);

    /// <summary>The frequency grid, exactly as asked for. Linear or logarithmic; one point is legal.</summary>
    public static double[] BuildFrequencies(double startHz, double stopHz, int points, bool logarithmic)
    {
        if (points < 1) throw new ArgumentOutOfRangeException(nameof(points), "At least one point.");
        if (!(startHz > 0) || !(stopHz > 0))
            throw new ArgumentOutOfRangeException(nameof(startHz), "Frequencies must be positive.");
        if (logarithmic && (startHz <= 0 || stopHz <= 0))
            throw new ArgumentOutOfRangeException(nameof(startHz), "A log sweep needs positive endpoints.");

        if (points == 1) return [startHz];

        var f = new double[points];
        if (logarithmic)
        {
            double a = Math.Log10(startHz), b = Math.Log10(stopHz);
            for (int i = 0; i < points; i++)
                f[i] = Math.Pow(10.0, a + (b - a) * i / (points - 1));
        }
        else
        {
            for (int i = 0; i < points; i++)
                f[i] = startHz + (stopHz - startHz) * i / (points - 1);
        }
        return f;
    }

    /// <summary>
    /// The port map, in the design's own array order — <c>G1.i, G1.o, G2.i, …</c> for
    /// <see cref="WBondPortBasis.Terminals"/>, and <c>G1, G2, …</c> for
    /// <see cref="WBondPortBasis.ArrayPairs"/>.
    ///
    /// <para>The terminal order is the same one the component's own <c>TerminalNames</c> uses, so a
    /// file and a schematic symbol cannot disagree about which port is which.</para>
    /// </summary>
    public static IReadOnlyList<string> PortNames(WBondDesign design,
                                                  WBondPortBasis basis = WBondPortBasis.Terminals)
    {
        ArgumentNullException.ThrowIfNull(design);

        if (basis == WBondPortBasis.ArrayPairs)
            return [.. design.Arrays.Select(a => a.Name)];

        var names = new List<string>(design.Arrays.Count * 2);
        foreach (var array in design.Arrays)
        {
            names.Add(array.Name + ".i");
            names.Add(array.Name + ".o");
        }
        return names;
    }

    /// <summary>
    /// The header lines written above the data — the port map, plus enough provenance that a file
    /// found later can be traced back to what produced it.
    /// </summary>
    public static IReadOnlyList<string> HeaderComments(WBondDesign design, Options options)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(options);

        var lines = new List<string>
        {
            options.PortBasis == WBondPortBasis.ArrayPairs
                ? "circuitRF wBond — one port per wire array; port k is that array's own two terminals (Gk.i, Gk.o)."
                : "circuitRF wBond — one port per TERMINAL, all referenced to the ground plane at z = 0, "
                  + "which is this file's common reference node.",
            $"{design.Arrays.Count} array(s), {design.WireCount} wire(s). Reference impedance " +
            options.Z0Ohms.ToString("0.####", CultureInfo.InvariantCulture) + " ohm, uniform and real.",
        };

        var names = PortNames(design, options.PortBasis);
        for (int k = 0; k < names.Count; k++)
            lines.Add($"Port[{k + 1}] = {names[k]}");

        bool capacitance = design.IncludeCapacitance && design.GroundPlane.Enabled;

        lines.Add(capacitance
            ? "Includes the wires' capacitance to the reference plane and between arrays."
            : "Series arm only — this design models no capacitance.");

        // The file outlives the session, so the one thing an array-pair file cannot contain is stated
        // IN it. See the class note: a floating pair has no terminal for a shunt to the plane.
        if (capacitance && options.PortBasis == WBondPortBasis.ArrayPairs)
            lines.Add("WARNING: the array-pair basis carries the SERIES arm only. This design's "
                      + "capacitance is NOT in this file — the schematic component stamps it and this "
                      + "file does not. Re-export on the terminal basis to carry it.");

        return lines;
    }

    /// <summary>
    /// The array-basis impedance at each frequency, as square matrices.
    ///
    /// <para><b>One complex M×M factorisation per frequency</b> — measured at 55.8 ms for N = 600
    /// wires in WB-B, so a 201-point export is roughly 11 s at that scale. The caller is expected to
    /// have said so; a 600-wire export must not look like a hang.</para>
    /// </summary>
    public static Mat<Complex>[] ArrayImpedances(WBondDesign design, IReadOnlyList<double> freqHz)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(freqHz);
        RefuseIfReturnPathUndeclared(design);

        var model = new WBondModel(design, "<export>");
        int m = model.ArrayCount;

        var mats = new Mat<Complex>[freqHz.Count];
        for (int fi = 0; fi < freqHz.Count; fi++)
        {
            var z = model.ArrayImpedance(freqHz[fi]);
            var mat = new Mat<Complex>(m, m);
            for (int i = 0; i < m; i++)
                for (int j = 0; j < m; j++)
                    mat[i, j] = z[i * m + j];   // row-major, matching the model's own stamp
            mats[fi] = mat;
        }
        return mats;
    }

    /// <summary>
    /// The <b>2M × 2M terminal-basis admittance</b> at each frequency, every terminal referenced to
    /// the ground plane — which is the exported file's own common reference node.
    ///
    /// <para>Terminal order is <c>G1.i, G1.o, G2.i, G2.o, …</c>, the same order the component's
    /// <c>TerminalNames</c> uses.</para>
    ///
    /// <h3>What goes into it — the same network the engine stamps</h3>
    /// <para><b>The series arm.</b> The M coupled branches satisfy <c>V_in − V_out = Z_arr·I</c>, so
    /// with <c>Y_arr = Z_arr⁻¹</c> the current into terminal <c>in_k</c> is
    /// <c>Σ_j Y_arr[k,j]·(V_in_j − V_out_j)</c> and the current into <c>out_k</c> is its negative.
    /// That is one 2×2 sign pattern per array pair, and it is the whole of an array-pair file.</para>
    ///
    /// <para><b>The capacitance</b> (wbond.md §3.7, §5.2): per array, <c>C1 + C12</c> from its input
    /// terminal to the reference, <c>C2 + C12</c> from its output terminal, and <c>−C12</c> across it;
    /// per array pair, the inter-array capacitor split half between the two input terminals and half
    /// between the two output terminals. <b>Identical to <c>WBondModel.StampCapacitance</c></b>, which
    /// is what the round-trip gate against a real solve holds shut.</para>
    ///
    /// <para><b>A singular Y is expected and is not a problem.</b> With no capacitance the network has
    /// no connection to the reference at all — it is genuinely floating, and its common-mode row sums
    /// are zero. <see cref="RFNetwork.YToS(Mat{Complex},Complex)"/> solves <c>(I + Ŷ)x = (I − Ŷ)</c>
    /// and never inverts Y, and <c>I + Ŷ</c> is invertible for any passive Y, so S is well defined.
    /// The common mode simply reflects.</para>
    /// </summary>
    public static Mat<Complex>[] TerminalAdmittances(WBondDesign design, IReadOnlyList<double> freqHz)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(freqHz);
        RefuseIfReturnPathUndeclared(design);

        var model = new WBondModel(design, "<export>");
        int m = model.ArrayCount;
        int n = 2 * m;

        var capacitance = model.Capacitance;

        var mats = new Mat<Complex>[freqHz.Count];
        for (int fi = 0; fi < freqHz.Count; fi++)
        {
            double omega = 2.0 * Math.PI * freqHz[fi];

            // Y_arr = Z_arr^-1 — the series arm, in the same M x M basis the stamp uses.
            var zArr = new Mat<Complex>(m, m);
            var flat = model.ArrayImpedance(freqHz[fi]);
            for (int i = 0; i < m; i++)
                for (int j = 0; j < m; j++)
                    zArr[i, j] = flat[i * m + j];

            var yArr = RFNetwork.ZToY(zArr);

            var y = new Mat<Complex>(n, n);
            for (int k = 0; k < m; k++)
            {
                for (int j = 0; j < m; j++)
                {
                    var g = yArr[k, j];
                    y[2 * k,     2 * j    ] += g;
                    y[2 * k,     2 * j + 1] -= g;
                    y[2 * k + 1, 2 * j    ] -= g;
                    y[2 * k + 1, 2 * j + 1] += g;
                }
            }

            if (capacitance is { } cap)
            {
                for (int k = 0; k < m; k++)
                {
                    // Shunts to the reference: they touch the diagonal only, because the reference is
                    // the file's own common node and has no port of its own.
                    y[2 * k,     2 * k    ] += new Complex(0.0, omega * cap.InputShunt(k));
                    y[2 * k + 1, 2 * k + 1] += new Complex(0.0, omega * cap.OutputShunt(k));

                    AddBetween(y, 2 * k, 2 * k + 1, new Complex(0.0, omega * cap.EndBridge(k)));

                    for (int j = k + 1; j < m; j++)
                    {
                        var half = new Complex(0.0, omega * 0.5 * cap.Mutual(k, j));
                        AddBetween(y, 2 * k,     2 * j,     half);
                        AddBetween(y, 2 * k + 1, 2 * j + 1, half);
                    }
                }
            }

            mats[fi] = y;
        }

        return mats;
    }

    /// <summary>Accumulates a two-terminal admittance between two ports, both of which have rows.</summary>
    private static void AddBetween(Mat<Complex> y, int a, int b, Complex value)
    {
        y[a, a] += value;
        y[b, b] += value;
        y[a, b] -= value;
        y[b, a] -= value;
    }

    /// <summary>
    /// The published network, converted to S at a uniform real reference impedance — from the
    /// terminal-basis admittance or the array-basis impedance, as the chosen
    /// <see cref="WBondPortBasis"/> says.
    /// </summary>
    public static SNP BuildNetwork(WBondDesign design, IReadOnlyList<double> freqHz, Options options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var z0 = new Complex(options.Z0Ohms, 0.0);
        Mat<Complex>[] s;

        if (options.PortBasis == WBondPortBasis.ArrayPairs)
        {
            var z = ArrayImpedances(design, freqHz);
            s = new Mat<Complex>[z.Length];
            for (int i = 0; i < z.Length; i++) s[i] = RFNetwork.ZToS(z[i], z0);
        }
        else
        {
            var y = TerminalAdmittances(design, freqHz);
            s = new Mat<Complex>[y.Length];
            for (int i = 0; i < y.Length; i++) s[i] = RFNetwork.YToS(y[i], z0);
        }

        return new SNP([.. freqHz], s, MatrixType.S, options.MatrixFormat, z0);
    }

    /// <summary>
    /// Writes <c>&lt;baseFilePathNoSuffix&gt;.sNp</c> and reports what happened.
    ///
    /// <para>The suffix is chosen by <see cref="TouchstoneExporter"/> from the port count, so an
    /// M-array design lands as <c>.sMp</c> with nothing here deciding it.</para>
    /// </summary>
    public static TouchstoneExportResult Export(WBondDesign design, Options options, string baseFilePathNoSuffix)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(baseFilePathNoSuffix);

        var freqs = BuildFrequencies(options.StartHz, options.StopHz, options.Points, options.Logarithmic);
        var dataSet = DataSetBuilder.FromSnp(BuildNetwork(design, freqs, options));

        var exportOptions = new TouchstoneExportOptions(
            options.Z0Ohms, options.Digits, options.DigitFormat, options.MatrixFormat,
            HeaderComments(design, options));

        // No sweep axes to pin: the cube is [freq, i, j] and nothing else, so the single-file path is
        // the only one reachable here.
        return TouchstoneExporter.Export(
            dataSet, DataSet.DefaultGroup, exportOptions,
            new Dictionary<string, int>(), allSweepFiles: false, baseFilePathNoSuffix);
    }

    /// <summary>
    /// The same refusal <see cref="WBondModel.Stamp"/> makes, for the same reason — an exported file
    /// outlives the session that produced it, so publishing an optimistically low inductance is the
    /// worse half of the failure rather than the milder one.
    /// </summary>
    private static void RefuseIfReturnPathUndeclared(WBondDesign design)
    {
        if (design.GroundPlane.Enabled) return;

        throw new InvalidOperationException(
            "This design has its ground plane disabled and no array nominated as the return " +
            "conductor, so its inductance has no defined return path and would be published " +
            "optimistically low. Re-enable the ground plane (the image plane at z = 0 then IS the " +
            "return), or add ground bond wires and nominate their array as the reference.");
    }
}

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
/// <para>Two rival readings exist and both are worse. A <b>2M-port</b> with every terminal
/// ground-referenced needs a shunt model the reduction does not provide, and the ground plane is the
/// <i>reference</i> rather than a terminal. A <b>2-port per array, exported separately</b> throws
/// away every off-diagonal — which is the entire content of a coupled bond array.</para>
///
/// <h3>The file says what its ports are</h3>
/// <para>Port identity is written into the file as <c>! Port[k] = &lt;array name&gt;</c> comments, the
/// form <c>TouchstonePortLabels</c> already reads on the way in. A Touchstone whose port order is
/// undocumented is a file somebody wires backwards.</para>
///
/// <h3>What this deliberately does NOT do</h3>
/// <para>Z→S goes through <see cref="RFNetwork.ZToS(Mat{Complex},Complex)"/> and the file is written
/// by <see cref="TouchstoneExporter"/> — the same conversion and the same writer every other export
/// in this repository uses. There is no second Z→S here and no second writer.</para>
/// </summary>
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
        MatrixFormat MatrixFormat = MatrixFormat.RI);

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

    /// <summary>Port <i>k</i> (1-based) names array <i>k</i>, in the design's own array order.</summary>
    public static IReadOnlyList<string> PortNames(WBondDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        return [.. design.Arrays.Select(a => a.Name)];
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
            "circuitRF wBond — one port per wire array; port k is that array's own two terminals (Gk.i, Gk.o).",
            $"{design.Arrays.Count} array(s), {design.WireCount} wire(s). Reference impedance " +
            options.Z0Ohms.ToString("0.####", CultureInfo.InvariantCulture) + " ohm, uniform and real.",
        };

        var names = PortNames(design);
        for (int k = 0; k < names.Count; k++)
            lines.Add($"Port[{k + 1}] = {names[k]}");

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
    /// The published network: <c>Z_arr(f)</c> converted to S at a uniform real reference impedance.
    /// </summary>
    public static SNP BuildNetwork(WBondDesign design, IReadOnlyList<double> freqHz, Options options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var z0 = new Complex(options.Z0Ohms, 0.0);
        var z = ArrayImpedances(design, freqHz);

        var s = new Mat<Complex>[z.Length];
        for (int i = 0; i < z.Length; i++) s[i] = RFNetwork.ZToS(z[i], z0);

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

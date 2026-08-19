using System.Numerics;

namespace CircuitRF.WBond.Mom;

/// <summary>
/// What one distributed (MoM) run produced: <c>Z_port</c> and <c>Y_port</c> at every requested
/// frequency, the mesh it was solved on, and the notes a reader needs to know what the numbers do and
/// do not claim.
///
/// <h3>Why the notes are part of the result and not a log line</h3>
/// <para>Every caveat this kernel carries is <b>quantitative</b> — a wavelength, an <c>s/a</c> ratio, a
/// clamped wire count — and each is computed from the design and the frequency grid that were actually
/// run. A caveat that lives in a doc comment is a caveat nobody reads at the moment it applies; one
/// attached to the numbers travels with them into the dialog, the report and the clipboard.</para>
///
/// <h3>Row-major, and the terminal order is the export's</h3>
/// <para><c>Z_port</c> and <c>Y_port</c> are T × T row-major with T = 2M, terminals ordered
/// <c>G1.i, G1.o, G2.i, G2.o, …</c> — the same basis, the same reference (the ground plane at z = 0)
/// and the same order <c>WBondTouchstoneExport.TerminalAdmittances</c> publishes on. That is what makes
/// the MoM answer and the analytic answer comparable by subtraction, with no renormalisation and no
/// port re-mapping.</para>
/// </summary>
public sealed class WireMomResult
{
    private readonly Complex[][] _z;
    private readonly Complex[][] _y;

    internal WireMomResult(
        double[] frequencies,
        Complex[][] z,
        Complex[][] y,
        int terminalCount,
        string[] terminalNames,
        WireMomMeshReport report,
        IReadOnlyList<string> notes)
    {
        Frequencies = frequencies;
        _z = z;
        _y = y;
        TerminalCount = terminalCount;
        TerminalNames = terminalNames;
        Report = report;
        Notes = notes;
    }

    /// <summary>The frequency grid, in hertz, exactly as it was asked for.</summary>
    public IReadOnlyList<double> Frequencies { get; }

    /// <summary>T = 2M.</summary>
    public int TerminalCount { get; }

    /// <summary><c>G1.i, G1.o, G2.i, …</c></summary>
    public string[] TerminalNames { get; }

    /// <summary>The mesh this was solved on — N_s, N_n, the memory arithmetic and the warnings.</summary>
    public WireMomMeshReport Report { get; }

    /// <summary>One user-readable line each. See the class remarks for why they travel with the numbers.</summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>The port impedance at frequency <paramref name="index"/>, T × T row-major, in ohms.</summary>
    public Complex[] PortImpedance(int index) => _z[index];

    /// <summary>The port admittance at frequency <paramref name="index"/>, T × T row-major, in siemens.</summary>
    public Complex[] PortAdmittance(int index) => _y[index];
}

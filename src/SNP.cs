// ================================================================
//  SNP.cs  —  S/Z/Y network parameter data container
//
//  Contains only:
//    • Enumerations  (MatrixType, MatrixFormat, FrequencyUnit)
//    • CommentEntry  (position-tagged comment from Touchstone files)
//    • SNP           (frequency sweep of N×N complex matrices + metadata)
//
//  I/O  →  TouchstoneIO.cs   (ReadFile / Read / WriteFile / Write)
//  Math →  RFNetwork.cs      (conversions, renormalization, stability, de-embedding)
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NumFlat;

namespace RfCore
{
    // ============================================================
    //  Enumerations
    // ============================================================

    public enum MatrixType   { S, Z, Y }
    public enum MatrixFormat { RI, MA, DB }   // Real/Imag, Mag/Angle°, dB/Angle°

    /// <summary>Frequency unit used in the Touchstone option line.</summary>
    public enum FrequencyUnit { Hz, kHz, MHz, GHz, THz }

    // ============================================================
    //  CommentEntry — a comment with its position in the file
    // ============================================================

    /// <summary>
    /// A comment line from a Touchstone file, tagged with the
    /// frequency index after which it appeared (−1 = file header,
    /// before the first data block).
    /// </summary>
    public sealed class CommentEntry
    {
        /// <summary>
        /// Frequency index after which this comment appears.
        /// −1 means the comment precedes the first data block.
        /// </summary>
        public int FrequencyIndex { get; }

        /// <summary>Raw comment text (without the leading '!').</summary>
        public string Text { get; }

        public CommentEntry(int frequencyIndex, string text)
        {
            FrequencyIndex = frequencyIndex;
            Text           = text;
        }

        public override string ToString() => $"[after freq {FrequencyIndex}] {Text}";
    }

    // ============================================================
    //  SNP — the unified data container
    // ============================================================

    /// <summary>
    /// Holds an N-port network parameter sweep (S, Z, or Y) over frequency,
    /// together with all Touchstone metadata
    /// All ports have same reference impedance.  See RFNetwork.Stos() to change
    /// to non-uniform reference impedance.
    /// <para>
    /// I/O:   <see cref="TouchstoneIO"/>
    /// Math:  <see cref="RFNetwork"/>
    /// </para>
    /// </summary>
    public sealed class SNP
    {
        // ---- Core data -----------------------------------------

        /// <summary>Frequency array in Hz.</summary>
        public double[] Frequencies { get; internal set; }

        /// <summary>One N×N complex matrix per frequency point.</summary>
        public Mat<Complex>[] Matrices { get; internal set; }

        /// <summary>Parameter type stored in Matrices.</summary>
        public MatrixType Type { get; set; }


        /// <summary>Preferred format for Touchstone output (RI / MA / DB).</summary>
        public MatrixFormat Format { get; set; }

        /// <summary>
        /// Frequency unit preference for Touchstone output.
        /// Populated from the option line when reading; defaults to GHz.
        /// The underlying <see cref="Frequencies"/> array is always stored in Hz.
        /// </summary>
        public FrequencyUnit FreqUnit { get; set; } = FrequencyUnit.GHz;

        /// <summary>True for placeholder SNPs created for missing files (no data loaded).</summary>
        public bool IsEmpty => Frequencies.Length == 0;

        /// <summary>Number of ports. Returns 0 for empty (broken) SNPs.</summary>
        public int Ports => IsEmpty ? 0 : Matrices[0].RowCount;

        /// <summary>Number of frequency points.</summary>
        public int FrequencyCount => Frequencies.Length;

        /// <summary>
        /// Reference impedance (complex).  All ports in SNP objects are normalized to same
        /// reference impedance.  Use RFNetwork.Stos() to change Matrix data to more than 1
        /// impedance.
        /// Defaults to 50+j0 for all ports when reading a Touchstone file
        /// whose option line specifies a single real R value.
        /// </summary>
        public Complex Z0 { get; set; }

        /// <summary>Comments read from the source file (optional).</summary>
        public List<CommentEntry> Comments { get; } = new();

        // ---- File provenance -------------------------------------------

        /// <summary>Full file path this SNP was loaded from. Null for computed/synthetic SNPs.</summary>
        public string? FilePath { get; set; }

        /// <summary>File name (with extension) derived from FilePath, or "(unnamed)".</summary>
        public string FileName => string.IsNullOrEmpty(FilePath)
            ? "(unnamed)"
            : System.IO.Path.GetFileName(FilePath);

        // ---- Constructors --------------------------------------

        /// <summary>Private constructor for CreateBroken — bypasses validation.</summary>
        private SNP()
        {
            Frequencies = Array.Empty<double>();
            Matrices    = Array.Empty<Mat<Complex>>();
            Z0          = new Complex(50,0);
        }

        /// <summary>
        /// Create a placeholder SNP for a file that cannot be found on disk.
        /// <see cref="IsEmpty"/> is true; <see cref="FilePath"/> holds the expected
        /// path for display and later restore via <c>RefreshFrom</c>.
        /// </summary>
        internal static SNP CreateBroken(string path) => new SNP { FilePath = path };

        /// <summary>
        /// Construct an SNP with zero-filled matrices.
        /// </summary>
        public SNP(double[] frequencies, int ports,
                   MatrixType type     = MatrixType.S,
                   MatrixFormat format = MatrixFormat.MA,
                   Complex? z0       = null)
        {
            if (frequencies.Length == 0)
                throw new ArgumentException("At least one frequency point required.");

            Frequencies = (double[])frequencies.Clone();
            Type        = type;
            Format      = format;

            Matrices = new Mat<Complex>[frequencies.Length];
            for (int i = 0; i < Matrices.Length; i++)
                Matrices[i] = new Mat<Complex>(ports, ports);

            Z0 = z0 ?? new Complex(50, 0);
        }

        /// <summary>
        /// Construct an SNP from pre-built matrix arrays.
        /// This is the canonical entry point for computed data (e.g. from an HB engine
        /// that computes port Y or Z on a frequency grid).
        /// </summary>
        public SNP(double[] frequencies, Mat<Complex>[] matrices,
                   MatrixType type     = MatrixType.S,
                   MatrixFormat format = MatrixFormat.MA,
                   Complex? z0       = null)
        {
            if (frequencies.Length != matrices.Length)
                throw new ArgumentException("Frequency and matrix counts must match.");
            if (matrices.Length == 0)
                throw new ArgumentException("At least one frequency point required.");

            int ports = matrices[0].RowCount;
            foreach (var m in matrices)
                if (m.RowCount != ports || m.ColCount != ports)
                    throw new ArgumentException(
                        "All matrices must be square and the same size.");

            Frequencies = (double[])frequencies.Clone();
            Matrices    = matrices;
            Type        = type;
            Format      = format;
            Z0          = z0 ?? new Complex(50, 0);
        }

        // ---- Indexer -------------------------------------------

        /// <summary>Return the matrix at the given frequency index (read-only).</summary>
        public Mat<Complex> this[int freqIndex] => Matrices[freqIndex];

        /// <summary>Replace the matrix at the given frequency index.</summary>
        public void Set(int freqIndex, Mat<Complex> m) => Matrices[freqIndex] = m;

        // ---- Metadata ------------------------------------------

        /// <summary>
        /// Copy non-type metadata (format, frequency unit, comments) from
        /// a source SNP to this SNP.  MatrixType is intentionally excluded
        /// so conversions can set the correct type themselves.
        /// </summary>
        internal void CopyMetadataFrom(SNP source)
        {
            Format   = source.Format;
            FreqUnit = source.FreqUnit;
            Comments.Clear();
            Comments.AddRange(source.Comments);
        }

        /// <summary>
        /// Replace all data in this SNP with data from <paramref name="source"/>,
        /// preserving the FilePath.  Used by the SNP library reload operation so
        /// that Trace objects that reference this SNP instance see fresh data
        /// without needing their own Data reference updated.
        /// </summary>
        internal void RefreshFrom(SNP source)
        {
            Frequencies = source.Frequencies;
            Matrices    = source.Matrices;
            Type        = source.Type;
            Format      = source.Format;
            FreqUnit    = source.FreqUnit;
            Z0          = source.Z0;
            Comments.Clear();
            Comments.AddRange(source.Comments);
            // FilePath intentionally not overwritten
        }

        // ---- Console debugging ---------------------------------

        /// <summary>
        /// Print the (row, col) element vs frequency to Console.
        /// row and col are 0-based.
        /// </summary>
        public void PrintElement(int row, int col,
                                 MatrixFormat displayFormat = MatrixFormat.MA)
        {
            Console.WriteLine(
                $"\n{Type}[{row + 1},{col + 1}]  " +
                $"(format: {displayFormat}, Z0={Z0:F1})");
            Console.WriteLine($"{"Freq(GHz)",12} {"A",14} {"B",14}");
            Console.WriteLine(new string('-', 42));

            for (int i = 0; i < FrequencyCount; i++)
            {
                var c = Matrices[i][row, col];
                (double a, double b) = RFNetwork.FormatComplex(c, displayFormat);
                string label = displayFormat switch
                {
                    MatrixFormat.RI => "Re / Im",
                    MatrixFormat.MA => "Mag / Ang°",
                    MatrixFormat.DB => "dB  / Ang°",
                    _               => ""
                };
                Console.WriteLine(
                    $"{Frequencies[i] / 1e9,12:F6} {a,14:G8} {b,14:G8}  ({label})");
            }
        }

        /// <summary>Print all N² elements to Console.</summary>
        public void PrintAll(MatrixFormat displayFormat = MatrixFormat.MA)
        {
            for (int r = 0; r < Ports; r++)
            for (int c = 0; c < Ports; c++)
                PrintElement(r, c, displayFormat);
        }
    }
}

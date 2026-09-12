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
        public FrequencyUnit FreqUnit
        {
            get => _freqUnit;
            set { _freqUnit = value; FreqUnitIsStated = true; }
        }

        private FrequencyUnit _freqUnit = FrequencyUnit.GHz;

        /// <summary>
        /// Whether <see cref="FreqUnit"/> was set by somebody, rather than being this type's default.
        ///
        /// <para><b>Why the distinction is load-bearing (AUT-8 R-aut8-3).</b> An SNP built by an
        /// analysis carries whatever this type defaults to, and the default is GHz — so a sweep that
        /// ran in the Hz decade wrote a file whose header said <c>#&#160;GHz</c> above a first column
        /// reading <c>5E-10</c>. That file is internally inconsistent whichever end is wrong, and a
        /// reader has no way to tell which. <see cref="TouchstoneIO"/> lets the DATA choose the unit
        /// when nothing has stated one; a file that was READ keeps the unit its own option line
        /// declared, so a read-write round trip is unchanged.</para>
        /// </summary>
        public bool FreqUnitIsStated { get; private set; }

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

        /// <summary>
        /// The per-port reference impedances the matrices are ACTUALLY expressed in, when the ports
        /// do not share one — null whenever <see cref="Z0"/> is the whole truth, which is almost
        /// always (AUT-9 R-aut9-2).
        ///
        /// <para><b>Why an SNP needs this at all.</b> Touchstone 1.x declares one real R and this
        /// type followed it, so a two-port whose second port was 12 Ω was written as a matrix
        /// generalized w.r.t. [50, 12] under a header saying every port was 50 Ω — and read back the
        /// same way. Nothing was wrong with the solve; the description of it was wrong in the one
        /// direction that matters, because a client renormalising from the reported reference then
        /// computes a wrong answer from a correct simulation.</para>
        ///
        /// <para><b>Nothing here renormalizes.</b> The matrices are untouched: this says what they
        /// are referenced to, and <see cref="Z0"/> stays port 1's value, which is what the option
        /// line declares and what a uniform-only reader will use. <see cref="TouchstoneIO"/> writes
        /// these as a header note and reads them back from one, so a circuitRF round trip keeps
        /// them.</para>
        /// </summary>
        public Complex[]? Z0PerPort { get; set; }

        /// <summary>
        /// <b>Which of these frequency points the producer actually SOLVED</b> — one entry per
        /// <see cref="Frequencies"/> entry — or null whenever the question does not arise, which is
        /// every Touchstone file and almost every computed network.
        ///
        /// <para><b>Why an SNP needs this.</b> An adaptively sampled EM sweep publishes the whole
        /// requested grid and models the points it did not solve, so a run stopped after six points
        /// returns a hundred. Every number in the matrices is the run's own answer at the frequency
        /// asked for and none of them is wrong — but a reader that draws a MARKER at each one says
        /// "here is a sample" a hundred times over six solves, and nobody can see that it is wrong
        /// (owner report, 2026-09-11). See <see cref="RfCore.Data.SampleProvenance"/>, which owns
        /// the convention this is populated from.</para>
        ///
        /// <para><b>It does not survive Touchstone</b>, which holds S and nothing else. It rides
        /// the SNP from the <c>.npy</c> that does carry it, through
        /// <c>DataSetBuilder.ToSnp</c>.</para>
        /// </summary>
        public bool[]? SolvedMask { get; set; }

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

        // ---- Factory methods -----------------------------------

        /// <summary>
        /// Build an S-parameter SNP from a Y-parameter sweep computed on a frequency grid.
        /// Convenience entry point for the HB/MNA engine: extract port Y → wrap as SNP in one call.
        /// </summary>
        /// <param name="frequencies">Frequency grid in Hz.</param>
        /// <param name="yMatrices">Y-parameter matrices, one per frequency point.</param>
        /// <param name="z0">Reference impedance for the resulting S-parameters (default 50 Ω).</param>
        public static SNP FromYSweep(double[] frequencies, Mat<Complex>[] yMatrices,
                                     Complex? z0 = null)
        {
            var z0val  = z0 ?? new Complex(50, 0);
            var ySweep = new SNP(frequencies, yMatrices, MatrixType.Y, MatrixFormat.MA, z0val);
            return RFNetwork.YToS(ySweep);
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
            // Carries the STATED-ness with the value: copying a unit that was only this type's
            // default must not turn it into a declaration, or a converted network would go back to
            // announcing GHz over Hz data (AUT-8 R-aut8-3).
            _freqUnit        = source._freqUnit;
            FreqUnitIsStated = source.FreqUnitIsStated;
            Comments.Clear();
            Comments.AddRange(source.Comments);
        }

        /// <summary>
        /// Replace all data in this SNP with data from <paramref name="source"/>,
        /// preserving the FilePath.  Used by the SNP library reload operation so
        /// that Trace objects that reference this SNP instance see fresh data
        /// without needing their own Data reference updated.
        ///
        /// <para><b>EVERY field that describes the data has to be listed here, including the ones
        /// that are usually null.</b> The whole point of this method is that the INSTANCE survives a
        /// reload so live traces keep their binding — which means a field left out is not reset to
        /// the new file's value, it keeps the OLD file's, silently, for as long as the display stays
        /// open. <see cref="SolvedMask"/> was the one that bit: a re-run whose first load had been a
        /// stopped adaptive sweep kept that run's three-solved-point mask over a complete 101-point
        /// result, so the new sweep drew three markers and only a close-and-reopen fixed it (owner
        /// report, 2026-09-11). <see cref="Z0PerPort"/> was the same defect one field along, not yet
        /// reported. Both are set from the null-safe source value, so reloading a file that says
        /// nothing CLEARS what the previous one said rather than leaving it standing.</para>
        /// </summary>
        internal void RefreshFrom(SNP source)
        {
            Frequencies = source.Frequencies;
            Matrices    = source.Matrices;
            Type        = source.Type;
            Format      = source.Format;
            _freqUnit        = source._freqUnit;
            FreqUnitIsStated = source.FreqUnitIsStated;
            Z0          = source.Z0;
            Z0PerPort   = source.Z0PerPort;
            SolvedMask  = source.SolvedMask;
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

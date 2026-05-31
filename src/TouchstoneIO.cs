// ================================================================
//  TouchstoneIO.cs  —  Touchstone 1.1 file reader and writer
//
//  Public API:
//    TouchstoneIO.ReadFile(path)                  → SNP
//    TouchstoneIO.Read(TextReader, ...)            → SNP
//    TouchstoneIO.WriteFile(snp, path, ...)
//    TouchstoneIO.Write(snp, TextWriter, ...)
//
//  Touchstone 1.1 format notes:
//    • Option line:  # <FreqUnit> <Type> <Format> R <Z0>
//    • 2-port data order:  freq  S11 S21 S12 S22
//    • N>2 data order:     row-major, one matrix row per output line
//    • Comments:  ! to end of line (inline or full-line)
//
//  Touchstone 1.1 compatibility mode (WriteFile/Write):
//    • Renormalizes data to a single uniform real Z0
//    • Converts Z/Y to S before writing
//    • Guarantees a valid R scalar on the option line
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using NumFlat;

namespace RfCore
{
    /// <summary>
    /// Static reader and writer for Touchstone 1.1 network parameter files.
    /// </summary>
    public static class TouchstoneIO
    {
        // ============================================================
        //  Reader
        // ============================================================

        /// <summary>Read a Touchstone file from disk into an SNP object.</summary>
        /// <param name="path">File path.  The extension (.s2p, .s4p, …) is used
        /// to pre-seed the port count; the file content takes precedence.</param>
        /// <param name="readComments">When true, comment lines are preserved in
        /// <see cref="SNP.Comments"/>.</param>
        public static SNP ReadFile(string path, bool readComments = true)
        {
            int? portsFromExt = ParsePortsFromExtension(path);
            using var reader  = new StreamReader(path);
            return Read(reader, portsFromExt, readComments);
        }

        /// <summary>Read Touchstone data from any <see cref="TextReader"/>.</summary>
        /// <param name="reader">Open text reader positioned at the start of the data.</param>
        /// <param name="knownPorts">Port count if known in advance (e.g. from file extension);
        /// null to infer from the first data line.</param>
        /// <param name="readComments">Preserve comment lines in the returned SNP.</param>
        public static SNP Read(TextReader reader,
                               int?  knownPorts   = null,
                               bool  readComments = true)
        {
            double       freqScale = 1e9;
            MatrixType   type      = MatrixType.S;
            MatrixFormat format    = MatrixFormat.MA;
            double       z0Real    = 50.0;
            FrequencyUnit freqUnit = FrequencyUnit.GHz;
            int?         ports     = knownPorts;

            var freqs    = new List<double>();
            var matrices = new List<Mat<Complex>>();
            var comments = new List<CommentEntry>();

            // Accumulate numeric tokens for the current frequency block:
            //   [freq, re0, im0, re1, im1, …, re(N²−1), im(N²−1)]
            var blockTokens    = new List<double>();
            int completedBlocks = 0;

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                // ---- Full-line comment ----
                if (trimmed.StartsWith('!'))
                {
                    if (readComments)
                    {
                        string text = trimmed.Length > 1
                            ? trimmed[1..].TrimStart()
                            : string.Empty;
                        int tag = completedBlocks == 0 ? -1 : completedBlocks - 1;
                        comments.Add(new CommentEntry(tag, text));
                    }
                    continue;
                }

                // ---- Option line ----
                if (trimmed.StartsWith('#'))
                {
                    ParseOptionLine(trimmed, ref freqScale, ref type,
                                    ref format, ref z0Real, ref freqUnit);
                    continue;
                }

                // ---- Strip inline comment ----
                int bang = trimmed.IndexOf('!');
                string dataLine = trimmed;
                if (bang >= 0)
                {
                    if (readComments)
                    {
                        string inlineText = trimmed[(bang + 1)..].TrimStart();
                        if (!string.IsNullOrEmpty(inlineText))
                        {
                            int tag = completedBlocks == 0 ? -1 : completedBlocks - 1;
                            comments.Add(new CommentEntry(tag, inlineText));
                        }
                    }
                    dataLine = trimmed[..bang].TrimStart();
                }
                if (string.IsNullOrWhiteSpace(dataLine)) continue;

                // ---- Parse numeric tokens ----
                var tokens = dataLine.Split(
                    (char[]?)null, StringSplitOptions.RemoveEmptyEntries);

                foreach (var tok in tokens)
                {
                    if (double.TryParse(tok,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out double val))
                    {
                        blockTokens.Add(val);
                    }
                }

                // ---- Infer port count once we have enough data ----
                if (ports is null && blockTokens.Count >= 3)
                    ports = TryInferPorts(blockTokens.Count - 1);

                if (ports is null) continue;

                int needed = 1 + 2 * ports.Value * ports.Value;

                if (blockTokens.Count == needed)
                {
                    FlushBlock(blockTokens, ports.Value, freqScale, format, freqs, matrices);
                    blockTokens.Clear();
                    completedBlocks++;
                }
                else if (blockTokens.Count > needed)
                {
                    // Extension-derived port count is too small; tokens have overflowed
                    // the expected block size.  Try to recover by finding the N whose
                    // block size divides the accumulated token count evenly.
                    int? inferred = TryInferPortsFromTotalTokens(blockTokens);
                    if (inferred.HasValue)
                    {
                        ports  = inferred.Value;
                        needed = 1 + 2 * ports.Value * ports.Value;
                        int offset = 0;
                        while (offset + needed <= blockTokens.Count)
                        {
                            FlushBlock(blockTokens.GetRange(offset, needed),
                                       ports.Value, freqScale, format, freqs, matrices);
                            offset += needed;
                            completedBlocks++;
                        }
                        var remaining = blockTokens.GetRange(offset, blockTokens.Count - offset);
                        blockTokens.Clear();
                        blockTokens.AddRange(remaining);
                    }
                    else
                    {
                        int? apparent = TryInferPorts(blockTokens.Count - 1);
                        string hint   = apparent.HasValue
                            ? $" Data appears to be {apparent.Value}-port."
                            : string.Empty;
                        throw new FormatException(
                            $"Token overflow: got {blockTokens.Count} tokens, " +
                            $"expected {needed} for {ports.Value}-port.{hint} " +
                            $"Check that the file extension matches the data.");
                    }
                }
            }

            // ---- EOF: flush any trailing partial block ----
            if (ports.HasValue && blockTokens.Count > 1)
            {
                int needed = 1 + 2 * ports.Value * ports.Value;
                if (blockTokens.Count == needed)
                {
                    FlushBlock(blockTokens, ports.Value, freqScale, format, freqs, matrices);
                }
                else if (freqs.Count == 0)
                {
                    // Extension-derived port count never produced a complete block.
                    // Try to find N whose block size divides the total token count
                    // evenly and whose block-start values look like monotone frequencies.
                    int? inferred = TryInferPortsFromTotalTokens(blockTokens);
                    if (inferred.HasValue)
                    {
                        int blockSize = 1 + 2 * inferred.Value * inferred.Value;
                        for (int offset = 0; offset + blockSize <= blockTokens.Count; offset += blockSize)
                            FlushBlock(blockTokens.GetRange(offset, blockSize),
                                       inferred.Value, freqScale, format, freqs, matrices);
                    }
                }
            }

            if (freqs.Count == 0)
                throw new FormatException(
                    "No valid frequency points found in Touchstone file.");

            var z0= new Complex(z0Real, 0);

            var snp = new SNP(freqs.ToArray(), matrices.ToArray(), type, format, z0);
            snp.FreqUnit = freqUnit;
            if (readComments)
                snp.Comments.AddRange(comments);
            return snp;
        }

        // ============================================================
        //  Writer
        // ============================================================

        /// <summary>
        /// Write an SNP to a Touchstone file on disk.
        /// </summary>
        /// <param name="snp">Network parameter data to write.</param>
        /// <param name="path">Output path.  Extension should match port count (.s2p, .s3p, etc.).</param>
        /// <param name="formatOverride">Override the stored format for this write only.</param>
        /// <param name="writeComments">Write stored <see cref="CommentEntry"/> records.</param>
        /// <param name="touchstone11Compatible">
        /// When true, enforces strict Touchstone 1.1 compatibility:
        /// <list type="bullet">
        ///   <item>All ports renormalized to a single real Z0.</item>
        ///   <item>Z/Y parameters are converted to S before writing.</item>
        ///   <item>The R value on the option line is a single real scalar.</item>
        /// </list>
        /// </param>
        /// <param name="precision">Numeric format string for data values (default "G10").</param>
        /// <param name="includeDateComment">Prepend a Created/Modified timestamp comment.</param>
        public static void WriteFile(SNP snp, string path,
                                     MatrixFormat? formatOverride      = null,
                                     bool          writeComments       = true,
                                     bool          touchstone11Compatible = false,
                                     string        precision           = "G10",
                                     bool          includeDateComment  = false)
        {
            using var writer = new StreamWriter(path, false, Encoding.ASCII);
            Write(snp, writer, formatOverride, writeComments,
                  touchstone11Compatible, precision, includeDateComment);
        }

        /// <summary>
        /// Write Touchstone data to any <see cref="TextWriter"/>.
        /// </summary>
        public static void Write(SNP snp, TextWriter writer,
                                 MatrixFormat? formatOverride      = null,
                                 bool          writeComments       = true,
                                 bool          touchstone11Compatible = false,
                                 string        precision           = "G10",
                                 bool          includeDateComment  = false)
        {
            MatrixFormat fmt = formatOverride ?? snp.Format;

            // Resolve the SNP that will actually be serialized.
            // Under compatibility mode we may need to convert type and/or renormalize.
            // In all cases the caller's SNP is never mutated.
            SNP writeSnp;
            double z0Option;

            if (touchstone11Compatible)
            {
                double targetZ0Real = snp.Z0.Real;
                var    targetZ0     = new Complex(targetZ0Real, 0);
                z0Option = targetZ0Real;

                SNP sSnp = snp.Type switch
                {
                    MatrixType.Z => RFNetwork.ZToS(snp),
                    MatrixType.Y => RFNetwork.YToS(snp),
                    _            => snp
                };

                bool alreadyNormalized =
                    Math.Abs(snp.Z0.Real - targetZ0Real) < 1e-9 &&
                    Math.Abs(snp.Z0.Imaginary) < 1e-12;

                writeSnp = alreadyNormalized ? sSnp : RFNetwork.SToS(sSnp, targetZ0);
            }
            else
            {
                z0Option = snp.Z0.Real;
                writeSnp = snp;
            }

            // ---- Header comments (FrequencyIndex == −1) ----
            if (writeComments)
            {
                foreach (var c in snp.Comments.Where(c => c.FrequencyIndex == -1))
                    writer.WriteLine($"! {c.Text}");
            }

            // ---- Optional date/time comment ----
            if (includeDateComment)
            {
                bool hasCreated = snp.Comments
                    .Where(c => c.FrequencyIndex == -1)
                    .Any(c => c.Text.IndexOf("created",
                                             StringComparison.OrdinalIgnoreCase) >= 0);
                string verb    = hasCreated ? "Modified" : "Created";
                string dateStr = DateTime.Now.ToString("ddd MMM dd HH:mm:ss yyyy",
                    System.Globalization.CultureInfo.InvariantCulture);
                writer.WriteLine($"! {verb} {dateStr}");
            }

            // ---- Warn in non-strict mode when Z0 is non-uniform or complex ----
            if (!touchstone11Compatible)
            {
                writer.WriteLine("! NOTE: Original data had complex Z0.");
                for (int p = 0; p < snp.Ports; p++)
                    writer.WriteLine($"!   Port {p + 1}: Z0 = {snp.Z0}");
            }

            // ---- Option line ----
            (string freqUnitStr, double freqDiv) = writeSnp.FreqUnit switch
            {
                FrequencyUnit.Hz  => ("Hz",  1.0),
                FrequencyUnit.kHz => ("kHz", 1e3),
                FrequencyUnit.MHz => ("MHz", 1e6),
                FrequencyUnit.GHz => ("GHz", 1e9),
                FrequencyUnit.THz => ("THz", 1e12),
                _                 => ("GHz", 1e9)
            };

            string typeStr = (touchstone11Compatible
                ? MatrixType.S
                : writeSnp.Type).ToString();

            writer.WriteLine($"# {freqUnitStr} {typeStr} {fmt} R {z0Option:G}");

            // ---- Data ----
            for (int fi = 0; fi < snp.FrequencyCount; fi++)
            {
                double freqVal = snp.Frequencies[fi] / freqDiv;
                var    mat     = writeSnp.Matrices[fi];

                if (snp.Ports == 2)
                {
                    // Touchstone 1.1 special 2-port order: S11 S21 S12 S22
                    var sb = new StringBuilder();
                    sb.Append(freqVal.ToString(precision,
                        System.Globalization.CultureInfo.InvariantCulture));
                    AppendFormatted(sb, mat[0, 0], fmt, precision);
                    AppendFormatted(sb, mat[1, 0], fmt, precision);
                    AppendFormatted(sb, mat[0, 1], fmt, precision);
                    AppendFormatted(sb, mat[1, 1], fmt, precision);
                    writer.WriteLine(sb.ToString());
                }
                else
                {
                    // N>2 port: row-major, one matrix row per output line.
                    for (int row = 0; row < snp.Ports; row++)
                    {
                        var rowSb = new StringBuilder();
                        if (row == 0)
                            rowSb.Append(freqVal.ToString(precision,
                                System.Globalization.CultureInfo.InvariantCulture));
                        else
                            rowSb.Append("          ");   // indent continuation rows

                        for (int col = 0; col < snp.Ports; col++)
                            AppendFormatted(rowSb, mat[row, col], fmt, precision);

                        writer.WriteLine(rowSb.ToString());
                    }
                }

                // ---- Inline comments after this frequency index ----
                if (writeComments)
                {
                    foreach (var c in snp.Comments.Where(c => c.FrequencyIndex == fi))
                        writer.WriteLine($"! {c.Text}");
                }
            }
        }

        // ============================================================
        //  Private helpers — reader
        // ============================================================

        private static int? ParsePortsFromExtension(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext.Length >= 3 && ext[0] == '.' && ext[1] == 's' && ext[^1] == 'p'
                && int.TryParse(ext[2..^1], out int n) && n >= 1)
                return n;
            return null;
        }

        private static int? TryInferPorts(int dataCount)
        {
            for (int N = 1; N <= 256; N++)
            {
                int needed = 2 * N * N;
                if (needed == dataCount) return N;
                if (needed > dataCount) break;
            }
            return null;
        }

        // Returns the port count N such that `tokens` partitions evenly into
        // complete N-port frequency blocks AND each block's first token is a
        // strictly increasing positive number (i.e. looks like a frequency sweep).
        private static int? TryInferPortsFromTotalTokens(List<double> tokens)
        {
            int total = tokens.Count;
            for (int N = 1; N <= 256; N++)
            {
                int blockSize = 1 + 2 * N * N;
                if (blockSize > total) break;
                if (total % blockSize != 0) continue;

                int    numBlocks = total / blockSize;
                bool   valid     = true;
                double prevFreq  = double.NegativeInfinity;
                for (int b = 0; b < numBlocks; b++)
                {
                    double freq = tokens[b * blockSize];
                    if (freq <= 0.0 || freq <= prevFreq) { valid = false; break; }
                    prevFreq = freq;
                }
                if (valid) return N;
            }
            return null;
        }

        private static void ParseOptionLine(string line,
            ref double freqScale, ref MatrixType type,
            ref MatrixFormat format, ref double z0Real,
            ref FrequencyUnit freqUnit)
        {
            var parts = line[1..].Trim()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < parts.Length; i++)
            {
                switch (parts[i].ToUpperInvariant())
                {
                    case "HZ":  freqScale = 1.0;  freqUnit = FrequencyUnit.Hz;  break;
                    case "KHZ": freqScale = 1e3;  freqUnit = FrequencyUnit.kHz; break;
                    case "MHZ": freqScale = 1e6;  freqUnit = FrequencyUnit.MHz; break;
                    case "GHZ": freqScale = 1e9;  freqUnit = FrequencyUnit.GHz; break;
                    case "THZ": freqScale = 1e12; freqUnit = FrequencyUnit.THz; break;
                    case "S":   type   = MatrixType.S;    break;
                    case "Z":   type   = MatrixType.Z;    break;
                    case "Y":   type   = MatrixType.Y;    break;
                    case "MA":  format = MatrixFormat.MA; break;
                    case "DB":  format = MatrixFormat.DB; break;
                    case "RI":  format = MatrixFormat.RI; break;
                    case "R":
                        if (i + 1 < parts.Length &&
                            double.TryParse(parts[i + 1],
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out double r))
                        {
                            z0Real = r;
                            i++;   // consume the value token
                        }
                        break;
                }
            }
        }

        private static void FlushBlock(List<double> tokens, int ports,
            double freqScale, MatrixFormat format,
            List<double> freqs, List<Mat<Complex>> matrices)
        {
            freqs.Add(tokens[0] * freqScale);
            matrices.Add(TokensToMatrix(tokens.GetRange(1, 2 * ports * ports), ports, format));
        }

        private static Mat<Complex> TokensToMatrix(
            List<double> tokens, int ports, MatrixFormat format)
        {
            var mat = new Mat<Complex>(ports, ports);
            for (int row = 0; row < ports; row++)
            for (int col = 0; col < ports; col++)
            {
                int idx = (row * ports + col) * 2;
                if (ports == 2 && ((row == 0 && col == 1) || (row == 1 && col == 0)))
                {
                    // Touchstone 1.1 stores 2-port as S11 S21 S12 S22 —
                    // swap indices so the matrix is row-major (S[row,col]).
                    mat[col, row] = ParseComplex(tokens[idx], tokens[idx + 1], format);
                }
                else
                {
                    mat[row, col] = ParseComplex(tokens[idx], tokens[idx + 1], format);
                }
            }
            return mat;
        }

        private static Complex ParseComplex(double a, double b, MatrixFormat fmt) =>
            fmt switch
            {
                MatrixFormat.RI => new Complex(a, b),
                MatrixFormat.MA => Complex.FromPolarCoordinates(a, b * Math.PI / 180.0),
                MatrixFormat.DB => Complex.FromPolarCoordinates(
                                       Math.Pow(10.0, a / 20.0), b * Math.PI / 180.0),
                _               => new Complex(a, b)
            };

        // ============================================================
        //  Private helpers — writer
        // ============================================================

        private static void AppendFormatted(StringBuilder sb, Complex c,
                                            MatrixFormat fmt, string precision)
        {
            (double a, double b) = RFNetwork.FormatComplex(c, fmt);
            string sa  = a.ToString(precision, System.Globalization.CultureInfo.InvariantCulture);
            string sb2 = b.ToString(precision, System.Globalization.CultureInfo.InvariantCulture);
            sb.Append($"  {sa,18}  {sb2,18}");
        }
    }
}

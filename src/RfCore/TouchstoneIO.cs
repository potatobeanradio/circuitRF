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
//    • 2-port noise block: after the network data, starting on a frequency ≤ the last one
//      (or after [Noise Data]); read into SNP.NoiseParameters and written back after the data.
//      Rows: freq NFmin(dB) |Γopt| ∠Γopt(deg) rn, Γopt and rn = Rn/R referenced to the option
//      line's R. The reader is 1.x-only, so rn is always taken as normalised.
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

            // What circuitRF's own header notes said, when this file has them. Null / empty for
            // every other Touchstone in the world, which is the point: neither is invented.
            Complex?   noteZ0      = null;
            var        perPortNote = new SortedDictionary<int, Complex>();

            // Accumulate numeric tokens for the current frequency block:
            //   [freq, re0, im0, re1, im1, …, re(N²−1), im(N²−1)]
            var blockTokens    = new List<double>();
            int completedBlocks = 0;

            // A two-port file may carry a NOISE block after its network data: five numbers per row
            // (freq, NFmin dB, |Γopt|, ∠Γopt°, rn). Touchstone 1.x marks its start only by a
            // frequency not above the last network frequency; Touchstone 2 by [Noise Data]. Before
            // this was read, its first row was folded into the next network block and the whole file
            // was refused as a token overflow.
            bool inNoise     = false;
            var  noiseTokens = new List<double>();
            var  noise       = new List<NoiseParameterPoint>();

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                // ---- Full-line comment ----
                if (trimmed.StartsWith('!'))
                {
                    // Two of circuitRF's own header notes carry information the option line
                    // structurally cannot (AUT-9 R-aut9-2), and they are read back BEFORE the
                    // readComments guard: whether the comment TEXT is preserved is the caller's
                    // choice, but the file's reference impedance is not a comment's worth of
                    // decoration. A file from anywhere else simply has neither note.
                    if (completedBlocks == 0) ReadZ0Note(trimmed, ref noteZ0, perPortNote);

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

                // ---- Touchstone 2's explicit noise keyword ----
                if (trimmed.StartsWith("[Noise Data]", StringComparison.OrdinalIgnoreCase))
                {
                    inNoise = true;
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

                var lineValues = new List<double>(tokens.Length);
                foreach (var tok in tokens)
                {
                    if (double.TryParse(tok,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out double val))
                    {
                        lineValues.Add(val);
                    }
                }

                // A 1.x noise block starts, at a block boundary of a two-port, on a five-number row
                // whose frequency does not advance past the last network frequency (the spec's own
                // marker). The five is what keeps a mis-inferred N-port — whose continuation lines
                // also "go backwards" — failing loudly rather than reading as one point plus noise.
                if (!inNoise && ports == 2 && blockTokens.Count == 0 && freqs.Count > 0 &&
                    lineValues.Count == 5 && lineValues[0] * freqScale <= freqs[^1])
                    inNoise = true;

                if (inNoise)
                {
                    noiseTokens.AddRange(lineValues);
                    while (noiseTokens.Count >= 5)
                    {
                        noise.Add(new NoiseParameterPoint(
                            noiseTokens[0] * freqScale,
                            noiseTokens[1],
                            Complex.FromPolarCoordinates(noiseTokens[2], noiseTokens[3] * Math.PI / 180.0),
                            noiseTokens[4]));
                        noiseTokens.RemoveRange(0, 5);
                    }
                    continue;
                }

                blockTokens.AddRange(lineValues);

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

            // The option line's R is a single REAL, so a complex reference cannot survive it and the
            // reactance was silently lost on a circuitRF round trip. The note carries it; it is
            // honoured only when its real part agrees with the option line, so a note that has
            // drifted from the data it describes is ignored rather than believed.
            var z0 = noteZ0 is { } n && Math.Abs(n.Real - z0Real) < 1e-9
                ? n
                : new Complex(z0Real, 0);

            var snp = new SNP(freqs.ToArray(), matrices.ToArray(), type, format, z0);
            snp.FreqUnit = freqUnit;

            // The per-port references, when this file carries circuitRF's note for them. Honoured
            // only when the note describes exactly the ports the data has — a note that has drifted
            // from the matrix it heads is ignored rather than believed, the same rule the complex
            // reference above is held to.
            if (perPortNote.Count > 0 && perPortNote.Count == snp.Ports &&
                perPortNote.Keys.First() == 1 && perPortNote.Keys.Last() == snp.Ports)
                snp.Z0PerPort = perPortNote.Values.ToArray();

            snp.NoiseParameters.AddRange(noise);

            if (readComments)
                snp.Comments.AddRange(comments);
            return snp;
        }

        // ── circuitRF's own Z0 header notes ─────────────────────────────────

        /// <summary>
        /// The invariant spelling of a complex reference impedance in a header note —
        /// <c>&lt;50; -10&gt;</c>. Round-trippable and culture-free, because a data file whose
        /// numbers change spelling with the machine's locale is not a data file.
        /// </summary>
        internal static string FormatZ0(Complex z)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return $"<{z.Real.ToString("R", inv)}; {z.Imaginary.ToString("R", inv)}>";
        }

        private static readonly System.Text.RegularExpressions.Regex Z0NoteRe = new(
            @"^!\s*NOTE:\s*reference impedance is complex:\s*(?<z><.*>)\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static readonly System.Text.RegularExpressions.Regex PortZ0Re = new(
            @"^!\s*Port\s+(?<p>\d+)\s*:\s*Z0\s*=\s*(?<z><.*>)\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static void ReadZ0Note(string commentLine, ref Complex? uniform,
                                       IDictionary<int, Complex> perPort)
        {
            var m = Z0NoteRe.Match(commentLine);
            if (m.Success && TryParseZ0(m.Groups["z"].Value) is { } z) { uniform = z; return; }

            m = PortZ0Re.Match(commentLine);
            if (m.Success &&
                int.TryParse(m.Groups["p"].Value, System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.InvariantCulture, out int port) &&
                TryParseZ0(m.Groups["z"].Value) is { } pz)
                perPort[port] = pz;
        }

        private static Complex? TryParseZ0(string text)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string body = text.Trim();
            if (body.Length < 2 || body[0] != '<' || body[^1] != '>') return null;
            var parts = body[1..^1].Split(';');
            if (parts.Length != 2) return null;
            return double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, inv, out double re) &&
                   double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, inv, out double im)
                ? new Complex(re, im)
                : null;
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

            // ---- Say what the option line cannot: a complex or a renormalized reference ----
            //
            // AUT-9 R-aut9-2. This block used to run on EVERY non-strict write, announce "Original
            // data had complex Z0" whether or not it was complex, and then print the ONE uniform
            // value once per port — a per-port listing carrying no per-port information, which read
            // as a positive statement that the ports were all 50 Ω on files where they were not.
            // Both halves are conditional now, and each says only what is true of this file.
            if (!touchstone11Compatible)
            {
                if (Math.Abs(snp.Z0.Imaginary) > 0)
                    writer.WriteLine($"! NOTE: reference impedance is complex: {FormatZ0(snp.Z0)}");

                // Present only when the ports genuinely do NOT share a reference. The option line
                // can declare one number and declares port 1's; these are what the data is actually
                // referenced to, and the sentence says so in as many words, because the whole defect
                // was a header that made a positive claim about ports it knew nothing about.
                if (snp.Z0PerPort is { Length: > 0 } perPort)
                {
                    writer.WriteLine(
                        "! NOTE: per-port reference impedances — the data is referenced to THESE, "
                      + $"not to the option line's single R {z0Option:G}.");
                    for (int p = 0; p < perPort.Length; p++)
                        writer.WriteLine($"!   Port {p + 1}: Z0 = {FormatZ0(perPort[p])}");
                }
            }

            // ---- Option line ----
            // AUT-8 R-aut8-3: when nobody has STATED a unit, the data chooses it. An SNP produced by
            // an analysis carries SNP.FreqUnit's own default of GHz, so a sweep that ran in the Hz
            // decade used to write "# GHz" above a first column reading 5E-10 — a file that is wrong
            // whichever end you believe, with nothing in it to say which. A file that was READ keeps
            // the unit its own option line declared, so a read-write round trip is byte-identical.
            var declaredUnit = writeSnp.FreqUnitIsStated
                ? writeSnp.FreqUnit
                : UnitForData(snp.Frequencies);

            (string freqUnitStr, double freqDiv) = declaredUnit switch
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

            // ---- Two-port noise block (Touchstone 1.x): freq NFmin(dB) |Γopt| ∠Γopt(deg) rn ----
            // SNP.NoiseParameters are referenced to Re(snp.Z0), which is the option line's R in both
            // write modes (compatibility mode renormalises the S data to exactly that R), so the rows
            // are written as stored and nothing is re-referenced.
            if (snp.Ports == 2)
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                foreach (var n in snp.NoiseParameters)
                    writer.WriteLine((n.FrequencyHz / freqDiv).ToString(precision, inv) + string.Concat(
                        new[] { n.NFminDb, n.GammaOpt.Magnitude, n.GammaOpt.Phase * 180.0 / Math.PI, n.RnNormalized }
                            .Select(v => $"  {v.ToString(precision, inv),18}")));
            }
        }

        // ============================================================
        //  Port-count sniff — no full parse
        // ============================================================

        /// <summary>
        /// Returns the port count N for the Touchstone file at <paramref name="path"/>
        /// without loading the full data set.  Prefers the .sNp extension; falls back
        /// to reading data tokens until a complete first block is seen.
        /// </summary>
        public static bool TryGetPortCount(string path, out int ports, out string? error)
        {
            ports = 0; error = null;
            try
            {
                if (!File.Exists(path)) { error = $"File not found: {path}"; return false; }
                int? fromExt = ParsePortsFromExtension(path);
                using var reader = new StreamReader(path);
                var tokens = new List<double>();
                string? line;
                int linesRead = 0;
                while ((line = reader.ReadLine()) != null && linesRead++ < 10000)
                {
                    string t = line.Trim();
                    if (t.Length == 0 || t[0] == '!' || t[0] == '#') continue;
                    int bang = t.IndexOf('!');
                    if (bang >= 0) t = t[..bang];
                    foreach (var tok in t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                        if (double.TryParse(tok, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var v))
                            tokens.Add(v);
                    if (tokens.Count >= 3)
                    {
                        int? n = fromExt ?? TryInferPorts(tokens.Count - 1);
                        if (n.HasValue && (tokens.Count - 1) == 2 * n.Value * n.Value)
                        { ports = n.Value; return true; }
                    }
                }
                if (fromExt is > 0) { ports = fromExt.Value; return true; }
                error = "Could not determine port count from file contents or extension.";
                return false;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // ============================================================
        //  Private helpers — reader
        // ============================================================

        /// <summary>
        /// Port count declared by a Touchstone file's own extension (".s4p" -> 4), or null when the
        /// extension does not declare one. Public because a caller reading through a <see cref="TextReader"/>
        /// (a storage-API stream, where the filename is not available to the reader) must supply this
        /// itself — inference from the data alone FAILS for N &gt; 2, whose rows are split across
        /// several physical lines.
        /// </summary>
        public static int? ParsePortsFromExtension(string path)
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
        // strictly increasing non-negative number (i.e. looks like a frequency sweep).
        //
        // NON-NEGATIVE rather than positive since 2026-09-13: an EM result may legitimately carry a
        // DC row (PlanarDcSolve), and a file whose first frequency is 0 would otherwise fail this
        // sniff for every N and fall through to "cannot infer". Strict increase still does the work
        // — a 0 anywhere but the first block is rejected exactly as before — and a negative token is
        // still not a frequency.
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
                    if (freq < 0.0 || freq <= prevFreq) { valid = false; break; }
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
            // (see UnitForData below for the write-side counterpart of this parse)
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

        /// <summary>
        /// The unit an option line should declare for a sweep nobody has stated a unit for: the
        /// largest one in which the highest frequency still reads as at least 1.
        ///
        /// <para>GHz for an empty or all-zero sweep, which is this type's own long-standing default
        /// and the only case where there is nothing in the data to go on.</para>
        /// </summary>
        internal static FrequencyUnit UnitForData(double[] frequencies)
        {
            double max = 0.0;
            foreach (double f in frequencies)
            {
                double a = Math.Abs(f);
                if (double.IsFinite(a) && a > max) max = a;
            }

            return max <= 0.0 ? FrequencyUnit.GHz
                 : max >= 1e12 ? FrequencyUnit.THz
                 : max >= 1e9  ? FrequencyUnit.GHz
                 : max >= 1e6  ? FrequencyUnit.MHz
                 : max >= 1e3  ? FrequencyUnit.kHz
                 :               FrequencyUnit.Hz;
        }

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

using System.Globalization;
using System.Numerics;

namespace CircuitRF.Engine.Loadpull;

/// <summary>
/// Parses a .gam termination-grid file (loadpull.md §2.2).
///
/// Format (forgiving — all header fields are optional):
///   # gamma Z0=50 mag_ang    ← optional header line (starts with #)
///   0.50  30                 ← one point per line
///   0.50+j*0.30              ← or complex literal form
///
/// Header tags (case-insensitive, order-independent):
///   Form:   "gamma" (default) or "impedance"
///   Z0:     Z0=&lt;value&gt;  (default 50 Ω)
///   Format: "re_im" | "mag_ang" | "re+j*imag"
///
/// Absent form tag → "impedance" (per loadpull.md §2.2).
/// Absent format tag → inferred per first data line:
///   contains 'j' or 'i' → re+j*imag literal form
///   else                 → re imag (two-column)
///
/// Each point is stored as both its raw form and its converted Z and Γ.
/// Blank lines and ';' / '#' comment lines are skipped.
/// Γ↔Z conversion: Z = Z0·(1+Γ)/(1−Γ), Γ = (Z−Z0)/(Z+Z0).
/// </summary>
public sealed class GamReader
{
    // ── Public types ──────────────────────────────────────────────────────────

    /// <summary>A single grid point: the full-precision Gamma and Z are both stored.</summary>
    public sealed record GamPoint(Complex Gamma, Complex Z, int LineNumber);

    /// <summary>The parsed grid.</summary>
    public sealed record GamGrid(IReadOnlyList<GamPoint> Points, double Z0);

    /// <summary>
    /// One frequency block of a (possibly multi-frequency) .gam file. <see cref="FreqHz"/> is null for a
    /// freq-less block (usable at ANY frequency). A freq-tagged file has one block per <c>freq=</c> line.
    /// </summary>
    public sealed record GamBlock(double? FreqHz, GamGrid Grid);

    // ── Public entry points ────────────────────────────────────────────────────

    /// <summary>Reads the file as a single grid (first block — back-compatible for freq-less files).</summary>
    public static GamGrid ReadFile(string path, double defaultZ0 = 50.0)
        => ReadText(File.ReadAllText(path), defaultZ0);

    public static GamGrid ReadText(string text, double defaultZ0 = 50.0)
        => new GamReader().ParseBlocks(text, defaultZ0)[0].Grid;

    /// <summary>All frequency blocks in the file, in file order.</summary>
    public static IReadOnlyList<GamBlock> ReadBlocks(string path, double defaultZ0 = 50.0)
        => new GamReader().ParseBlocks(File.ReadAllText(path), defaultZ0);

    public static IReadOnlyList<GamBlock> ReadBlocksText(string text, double defaultZ0 = 50.0)
        => new GamReader().ParseBlocks(text, defaultZ0);

    /// <summary>
    /// Selects the grid for <paramref name="targetFreqHz"/>: a freq-less file (single any-freq block) is
    /// returned as-is; a freq-tagged file returns the block whose <c>freq=</c> is nearest the target. With
    /// no target, returns the first block.
    /// </summary>
    public static GamGrid ReadFileForFreq(string path, double? targetFreqHz, double defaultZ0 = 50.0)
        => SelectForFreq(ReadBlocks(path, defaultZ0), targetFreqHz);

    public static GamGrid SelectForFreq(IReadOnlyList<GamBlock> blocks, double? targetFreqHz)
    {
        if (blocks.Count == 1 || targetFreqHz is null) return blocks[0].Grid;
        // Freq-tagged: nearest block by |Δf|. Freq-less blocks (null) sort last (apply at any freq).
        GamBlock best = blocks[0];
        double bestDelta = double.PositiveInfinity;
        foreach (var b in blocks)
        {
            double d = b.FreqHz is { } f ? Math.Abs(f - targetFreqHz.Value) : double.PositiveInfinity;
            if (d < bestDelta) { bestDelta = d; best = b; }
        }
        return best.Grid;
    }

    // ── Internal state ────────────────────────────────────────────────────────

    private enum Form   { Gamma, Impedance }
    private enum Fmt    { Unknown, ReIm, MagAng, ReJImag }

    private Form   _form   = Form.Impedance;   // absent form → impedance (loadpull.md §2.2)
    private Fmt    _fmt    = Fmt.Unknown;
    private double _z0     = 50.0;
    private bool   _headerParsed;

    // ── Parse ─────────────────────────────────────────────────────────────────

    private List<GamBlock> ParseBlocks(string text, double defaultZ0)
    {
        _z0 = defaultZ0;
        var blocks  = new List<GamBlock>();
        var lines   = text.Split('\n');

        double? curFreq = null;                 // current block's freq (null = freq-less)
        var     curPts  = new List<GamPoint>();
        bool    started = false;                // has any data/freq line been seen?

        void Flush()
        {
            if (curPts.Count > 0 || curFreq is not null)
                blocks.Add(new GamBlock(curFreq, new GamGrid(curPts, _z0)));
        }

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            var raw     = lines[i].TrimEnd('\r').Trim();

            if (raw.Length == 0) continue;

            // Comment lines (';' prefix) — skip entirely.
            if (raw[0] == ';') continue;

            // Header candidate: starts with '#'. (# is a comment token, never a freq line — Layer C.)
            if (raw[0] == '#')
            {
                if (!_headerParsed)
                    ParseHeader(raw[1..]);
                continue;
            }

            // Frequency block delimiter: a bare `freq=<value><unit>` directive (not '#'-prefixed). Data
            // lines start with a digit/sign/dot, so a leading letter 'f' is unambiguous.
            if (raw.Length > 5 && (raw[0] == 'f' || raw[0] == 'F') &&
                raw.StartsWith("freq", StringComparison.OrdinalIgnoreCase) &&
                raw.AsSpan(4).TrimStart() is var afterFreq && afterFreq.Length > 0 && afterFreq[0] == '=')
            {
                _headerParsed = true;
                if (started) Flush();           // close the previous block
                curFreq = ParseFreqHz(afterFreq[1..].ToString(), lineNum);
                curPts  = new List<GamPoint>();
                started = true;
                continue;
            }

            // Data line.
            _headerParsed = true;  // first non-comment, non-header line fixes format inference
            started = true;

            var pt = ParseDataLine(raw, lineNum);
            if (pt is not null)
                curPts.Add(pt);
        }

        Flush();
        // A wholly empty file → one empty freq-less block (keeps [0] valid for back-compat callers).
        if (blocks.Count == 0) blocks.Add(new GamBlock(null, new GamGrid(curPts, _z0)));
        return blocks;
    }

    /// <summary>Parses a `freq=` value with an optional unit suffix: "2e9", "1.8GHz", "900 MHz".</summary>
    private static double ParseFreqHz(string s, int lineNum)
    {
        s = s.Trim();
        int i = s.Length;
        while (i > 0 && char.IsLetter(s[i - 1])) i--;   // strip trailing alpha unit (not the 'e' of 1.8e9)
        string num  = s[..i].Trim();
        string unit = s[i..].Trim();
        double val  = ParseDouble(num.Length > 0 ? num : s, lineNum);
        double scale = unit.ToLowerInvariant() switch
        {
            "" or "hz" => 1.0,
            "khz" => 1e3, "mhz" => 1e6, "ghz" => 1e9, "thz" => 1e12,
            _     => 1.0,
        };
        return val * scale;
    }

    // ── Header parsing ────────────────────────────────────────────────────────

    private void ParseHeader(string headerBody)
    {
        _headerParsed = true;
        var tokens = headerBody.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
        foreach (var tok in tokens)
        {
            if (tok.Equals("gamma",     StringComparison.OrdinalIgnoreCase)) { _form = Form.Gamma;     continue; }
            if (tok.Equals("impedance", StringComparison.OrdinalIgnoreCase)) { _form = Form.Impedance; continue; }
            if (tok.Equals("re_im",     StringComparison.OrdinalIgnoreCase)) { _fmt  = Fmt.ReIm;       continue; }
            if (tok.Equals("mag_ang",   StringComparison.OrdinalIgnoreCase)) { _fmt  = Fmt.MagAng;     continue; }
            if (tok.Equals("re+j*imag", StringComparison.OrdinalIgnoreCase)) { _fmt  = Fmt.ReJImag;    continue; }

            // Z0=<value>
            if (tok.StartsWith("Z0=", StringComparison.OrdinalIgnoreCase))
            {
                var valStr = tok[3..];
                if (double.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var z0))
                    _z0 = z0;
            }
        }
    }

    // ── Data line parsing ─────────────────────────────────────────────────────

    private GamPoint? ParseDataLine(string line, int lineNum)
    {
        // Strip inline comments (semicolon anywhere after data).
        int sc = line.IndexOf(';');
        if (sc >= 0) line = line[..sc].Trim();
        if (line.Length == 0) return null;

        // Infer format from the first data line if not yet determined.
        if (_fmt == Fmt.Unknown)
        {
            bool hasImagMarker = line.IndexOfAny(['j', 'i']) >= 0;
            _fmt = hasImagMarker ? Fmt.ReJImag : Fmt.ReIm;
        }

        Complex raw = _fmt switch
        {
            Fmt.ReJImag => ParseReJImag(line, lineNum),
            Fmt.MagAng  => ParseMagAng(line,  lineNum),
            _           => ParseReIm(line,     lineNum),
        };

        // Convert to Γ and Z.
        Complex gamma, z;
        if (_form == Form.Gamma)
        {
            gamma = raw;
            z     = GammaToZ(gamma, _z0);
        }
        else
        {
            z     = raw;
            gamma = ZToGamma(z, _z0);
        }

        return new GamPoint(gamma, z, lineNum);
    }

    // ── Complex number parsers ────────────────────────────────────────────────

    /// <summary>Two-column: "re imag" (whitespace-separated).</summary>
    private static Complex ParseReIm(string line, int lineNum)
    {
        var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new FormatException($"[.gam line {lineNum}] Expected 'real imag' (two columns), got: '{line}'");
        double re = ParseDouble(parts[0], lineNum);
        double im = ParseDouble(parts[1], lineNum);
        return new Complex(re, im);
    }

    /// <summary>Two-column: "magnitude angle_degrees".</summary>
    private static Complex ParseMagAng(string line, int lineNum)
    {
        var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new FormatException($"[.gam line {lineNum}] Expected 'mag angle' (two columns), got: '{line}'");
        double mag = ParseDouble(parts[0], lineNum);
        double ang = ParseDouble(parts[1], lineNum);
        return Complex.FromPolarCoordinates(mag, ang * Math.PI / 180.0);
    }

    /// <summary>
    /// Complex literal: "re+j*imag" or "re-j*imag" or "re+imag*j" variants.
    /// Strips all whitespace first, then handles the forms:
    ///   0.5+j*0.3  →  re=0.5, im=0.3
    ///   0.5-j*0.3  →  re=0.5, im=-0.3
    ///   80+j*10    →  re=80,  im=10
    /// Falls back to attempting two-column parse if no j/i marker found.
    /// </summary>
    private static Complex ParseReJImag(string line, int lineNum)
    {
        // Remove all whitespace for robust parsing.
        var s = new string(line.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (s.Length == 0) throw new FormatException($"[.gam line {lineNum}] Empty complex literal.");

        // Find the imaginary marker 'j' or 'i' (case-insensitive).
        int jIdx = s.IndexOfAny(['j', 'J', 'i', 'I']);
        if (jIdx < 0)
        {
            // No imaginary marker → try two-column fallback.
            return ParseReIm(line, lineNum);
        }

        // Find the +/- sign separating real and imaginary parts.
        // Search from index 1 (skip possible leading sign) backwards from jIdx.
        int sepIdx = -1;
        for (int i = jIdx - 1; i > 0; i--)
        {
            if (s[i] == '+' || s[i] == '-') { sepIdx = i; break; }
        }

        double re, im;
        if (sepIdx <= 0)
        {
            // Pure imaginary: e.g. "j*0.3", "0.3j", "-j*0.5"
            re = 0.0;
            var imStr = s.Replace("j", "").Replace("J", "").Replace("i", "").Replace("I", "")
                         .Replace("*", "").TrimStart('+');
            im = ParseDouble(imStr.Length > 0 ? imStr : "1", lineNum);
        }
        else
        {
            var reStr = s[..sepIdx];
            var imStr = s[sepIdx..];
            re = ParseDouble(reStr.Length > 0 ? reStr : "0", lineNum);
            // Strip j/i and * from the imaginary part.
            imStr = imStr.Replace("j", "").Replace("J", "").Replace("i", "").Replace("I", "")
                         .Replace("*", "");
            // Handle signs: "+0.3" → 0.3; "-0.3" → -0.3; "+" alone → 1; "-" alone → -1
            im = imStr == "+" ? 1.0 : imStr == "-" ? -1.0 : ParseDouble(imStr, lineNum);
        }

        return new Complex(re, im);
    }

    private static double ParseDouble(string s, int lineNum)
    {
        if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var d))
            return d;
        throw new FormatException($"[.gam line {lineNum}] Cannot parse '{s}' as a number.");
    }

    // ── Γ↔Z conversions ───────────────────────────────────────────────────────

    public static Complex GammaToZ(Complex gamma, double z0)
    {
        var denom = Complex.One - gamma;
        if (denom.Magnitude < 1e-15)
            return new Complex(1e12, 0);   // near unit circle → very large Z
        return z0 * (Complex.One + gamma) / denom;
    }

    public static Complex ZToGamma(Complex z, double z0)
    {
        var denom = z + new Complex(z0, 0);
        if (denom.Magnitude < 1e-15)
            return new Complex(-1, 0);   // near Z=0 → Γ = -1
        return (z - new Complex(z0, 0)) / denom;
    }
}

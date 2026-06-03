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

    // ── Public entry points ────────────────────────────────────────────────────

    public static GamGrid ReadFile(string path, double defaultZ0 = 50.0)
        => ReadText(File.ReadAllText(path), defaultZ0);

    public static GamGrid ReadText(string text, double defaultZ0 = 50.0)
        => new GamReader().Parse(text, defaultZ0);

    // ── Internal state ────────────────────────────────────────────────────────

    private enum Form   { Gamma, Impedance }
    private enum Fmt    { Unknown, ReIm, MagAng, ReJImag }

    private Form   _form   = Form.Impedance;   // absent form → impedance (loadpull.md §2.2)
    private Fmt    _fmt    = Fmt.Unknown;
    private double _z0     = 50.0;
    private bool   _headerParsed;

    // ── Parse ─────────────────────────────────────────────────────────────────

    private GamGrid Parse(string text, double defaultZ0)
    {
        _z0 = defaultZ0;
        var points  = new List<GamPoint>();
        var lines   = text.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            var raw     = lines[i].TrimEnd('\r').Trim();

            if (raw.Length == 0) continue;

            // Comment lines (';' prefix) — skip entirely.
            if (raw[0] == ';') continue;

            // Header candidate: starts with '#'.
            if (raw[0] == '#')
            {
                if (!_headerParsed)
                    ParseHeader(raw[1..]);
                // Remaining '#' lines after the header are also comments — skip.
                continue;
            }

            // Data line.
            _headerParsed = true;  // first non-comment, non-header line fixes format inference

            var pt = ParseDataLine(raw, lineNum);
            if (pt is not null)
                points.Add(pt);
        }

        return new GamGrid(points, _z0);
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

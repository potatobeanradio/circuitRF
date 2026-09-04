// The Excellon drill reader (docs/sonnet-briefs/brief-L4f-excellon-drill-and-vias.md).
// Written from the published format description only — §8's standing rule against ingesting GPL
// sources applies to this format as it does to every other.
//
// A HOLE IS NOT ARTWORK, and the format is materially less self-describing than Gerber. Gerber
// declares its units and digit format in every file; a drill file frequently declares NEITHER, and
// the failure mode of guessing is a board a thousand times too large that parsed without a murmur.
// So the centre of gravity of this reader is ExcellonFormat's inference (§2) and not the state
// machine below it.
//
// Scope, exactly as the brief draws it: text in, tools and hits out. This file knows nothing about a
// CellFolder, a Technology, a Messages sink or a dialog — that is L4g's job, the same split
// GerberReader/L4g, PcbReader/PcbImport and DxfReader/DxfImport already established, and it is what
// makes the whole gate run headlessly with no workspace anywhere. Turning hits into vias is
// DrillViaPairing, also pure, also in this phase.

using System.Globalization;
using System.Text;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>One entry of the tool table. <see cref="DiameterText"/> is kept as it was WRITTEN because
/// the unit inference reads the tool table and nothing else (§2 evidence source 4), and because
/// <see cref="DiameterExact"/> is a statement about that spelling. <see cref="Plated"/> and
/// <see cref="Function"/> are null when the file declared neither — which, for both, is the common
/// case; see R-L4f-10 on why a null <see cref="Function"/> must not be replaced by a guess.</summary>
public sealed record DrillTool(
    int Number, long DiameterDbu, string DiameterText, bool DiameterExact, bool? Plated, string? Function);

/// <summary>One drilled hole. The diameter is copied off the tool rather than referenced through it,
/// so a hit is self-contained where the pairing code needs it.</summary>
public sealed record DrillHit(long X, long Y, int Tool, long DiameterDbu, bool? Plated, string? Function);

/// <summary>A routed slot — R-L4f-7: <b>one</b> opening, not two holes. <see cref="Xy"/> is the
/// centreline the tool cut, flat x,y pairs; the shape it becomes is a <see cref="PathShape"/> of
/// <see cref="WidthDbu"/> with round ends.</summary>
public sealed record DrillSlot(long[] Xy, int Tool, long WidthDbu, bool? Plated, string? Function);

/// <summary>R-L4f-6: which two copper layers a drill file's holes connect, as the file declared it.
/// <c>1,4,PTH</c> spans the whole board; <c>1,2,Blind</c> does not. A production set holds several
/// drill files, one per layer pair, and reading only the one named like the board loses every blind
/// and buried via silently.</summary>
public sealed record DrillSpan(int FromLayer, int ToLayer, string Kind, bool? Plated)
{
    public bool IsThroughHole => Kind.Equals("PTH", StringComparison.OrdinalIgnoreCase) ||
                                 Kind.Equals("NPTH", StringComparison.OrdinalIgnoreCase);
}

/// <summary>An axis-aligned extent in DBU. <see cref="HasAny"/> is false for an empty set — a box of
/// zeros would compare as if it were a real box at the origin.</summary>
public readonly record struct DrillExtents(bool HasAny, long MinX, long MinY, long MaxX, long MaxY)
{
    public static readonly DrillExtents Empty = new(false, 0, 0, 0, 0);

    public long Width => HasAny ? MaxX - MinX : 0;
    public long Height => HasAny ? MaxY - MinY : 0;

    public DrillExtents Include(long x, long y) => HasAny
        ? new DrillExtents(true, Math.Min(MinX, x), Math.Min(MinY, y), Math.Max(MaxX, x), Math.Max(MaxY, y))
        : new DrillExtents(true, x, y, x, y);

    public bool Contains(long x, long y, long margin) =>
        HasAny && x >= MinX - margin && x <= MaxX + margin && y >= MinY - margin && y <= MaxY + margin;
}

/// <summary>§2 evidence source 5, and the strongest one available: drilled points that do not land
/// inside the artwork's own bounding box mean the format inference is wrong. <see cref="Report"/>
/// states the disagreement as a NUMBER rather than as a verdict, because "the format looks wrong"
/// tells a user nothing they can act on and "the drill data is 25.4× the artwork" names the fix.
/// <para><see cref="HitCount"/> counts hits AND routed-slot vertices — a rout file often has no plain
/// hits at all, and counting only hits made the check agree with itself on exactly those files.</para>
/// </summary>
public sealed record DrillExtentsCheck(
    bool Agrees, int HitsOutside, int HitCount, double WidthRatio, double HeightRatio, string Report);

/// <summary>Everything one drill file turned out to be. <see cref="Refusal"/> non-null means nothing
/// was read and nothing must be created.</summary>
public sealed class ExcellonReadResult
{
    public string? Refusal { get; init; }

    public IReadOnlyList<DrillTool> Tools { get; init; } = [];
    public IReadOnlyList<DrillHit> Hits { get; init; } = [];
    public IReadOnlyList<DrillSlot> Slots { get; init; } = [];

    /// <summary>The resolved format AND the evidence that settled each part of it (R-L4f-1). This
    /// reader states the inference; L4h decides whether it warrants a prompt.</summary>
    public required DrillFormatInference Format { get; init; }

    /// <summary>File-level plating (R-L4f-5), from a <c>; #@! TF.FileFunction</c> attribute or a
    /// <c>;TYPE=</c> section. Null when the file declared neither — the third spelling, two files
    /// distinguished only by name, is <see cref="PlatingFromFileName"/>'s job and L4g's to call.</summary>
    public bool? Plated { get; init; }

    /// <summary>R-L4f-6. Null when no span was declared, in which case through-hole is assumed and
    /// <see cref="Diagnostics"/> says so.</summary>
    public DrillSpan? Span { get; init; }

    /// <summary>The raw <c>TF.FileFunction</c> value, when one was present.</summary>
    public string? FileFunction { get; init; }

    /// <summary>False when the declared/inferred format's output unit is not a whole number of DBU
    /// (R-L4e-2's only inexact row, inch at 6 decimals). Coordinates were then rounded, not refused —
    /// import inverts export's "refuse rather than round", because refusing to read a file the user
    /// already has leaves them no path at all.</summary>
    public bool CoordinatesExact { get; init; } = true;
    public double WorstCaseRoundingErrorDbu { get; init; }

    /// <summary>R-L4f-4: false when at least one tool diameter did not land on a whole DBU. The
    /// offending tools are named in <see cref="Diagnostics"/>.</summary>
    public bool ToolDiametersExact { get; init; } = true;

    public DrillExtents Extents { get; init; } = DrillExtents.Empty;

    /// <summary>R-L4f-8: every unrecognized command, by name, once, with a count.</summary>
    public IReadOnlyDictionary<string, int> UnknownCommandCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Constructs read but deliberately degraded or dropped, by name, with a count.</summary>
    public IReadOnlyDictionary<string, int> SkippedConstructCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    public int HoleCount => Hits.Count;
}

public sealed partial class ExcellonReader
{
    /// <summary>The same ceiling the artwork reader uses, deliberately rather than a second number
    /// that can drift from it (R-L4e-20).</summary>
    public const long HitHardCeiling = GerberReader.EntityHardCeiling;

    public static ExcellonReadResult Read(
        Stream stream, int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron, DrillFormatOverride? overrides = null)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        byte[] bytes = ms.ToArray();

        // R-L4f-3: the binary check happens on BYTES, before any decode — a decoder replaces every
        // byte it dislikes with U+FFFD, which would erase the very evidence this refusal reads.
        if (BinaryRefusal(bytes) is { } refusal) return Refuse(refusal, overrides);

        return Read(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'), dbuPerMicron, overrides);
    }

    public static ExcellonReadResult Read(
        TextReader reader, int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron, DrillFormatOverride? overrides = null)
        => Read(reader.ReadToEnd(), dbuPerMicron, overrides);

    public static ExcellonReadResult Read(
        string text, int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron, DrillFormatOverride? overrides = null)
    {
        if (BinaryRefusal(text) is { } binary) return Refuse(binary, overrides);
        if (NotADrillFileRefusal(text) is { } notDrill) return Refuse(notDrill, overrides);

        // The format must be settled BEFORE the state machine runs, because the tool table is one of
        // the things that settles it (§2 evidence source 4) — so the file is scanned for what it
        // declares, the inference is resolved, and only then are coordinates turned into DBU.
        var declared = ScanDeclarations(text);
        var inference = ExcellonFormat.Resolve(declared, overrides);

        var reader = new ExcellonReader(inference, dbuPerMicron);
        reader.Parse(text);
        return reader.Build();
    }

    /// <summary>R-L4f-5's second spelling: two files that differ only in name. Content cannot settle
    /// it, so this is a NAME test and it lives here rather than in the parser — L4g calls it, and only
    /// where the file itself declared nothing. Generic patterns only; no tool's or vendor's private
    /// naming appears here (root CLAUDE.md §"Commercial Vendor References").</summary>
    public static bool? PlatingFromFileName(string fileName)
    {
        string n = Path.GetFileName(fileName).ToLowerInvariant();
        foreach (string marker in NonPlatedMarkers)
            if (n.Contains(marker, StringComparison.Ordinal)) return false;
        foreach (string marker in PlatedMarkers)
            if (n.Contains(marker, StringComparison.Ordinal)) return true;
        return null;
    }

    // Longest / most specific first: "npth" must be tested before "pth", and it is, because the
    // non-plated list is consulted in full before the plated one.
    private static readonly string[] NonPlatedMarkers =
        ["npth", "non-plated", "nonplated", "non_plated", "unplated", "-np.", "_np.", "-np-", "-np_"];

    private static readonly string[] PlatedMarkers = ["pth", "plated"];

    /// <summary>§2 evidence source 5. Compares the hits against the artwork's own bounding box and
    /// reports the disagreement as a number. Free once L4g holds both readers' output, and the
    /// strongest check available — a wrong format is a wrong SCALE, and a wrong scale is visible here
    /// even when every other source was silent.</summary>
    public static DrillExtentsCheck CrossCheckExtents(ExcellonReadResult drill, DrillExtents artwork)
    {
        if (!drill.Extents.HasAny || !artwork.HasAny)
            return new DrillExtentsCheck(true, 0, drill.Hits.Count, 1, 1,
                "No cross-check was possible: one of the two sets is empty.");

        // A hole sits inside the copper it drills, so a hit outside the artwork box is a real
        // disagreement — but the box is the artwork's own centreline extent, so a small margin keeps
        // a hole at the very edge of a board outline from reading as an error.
        long margin = Math.Max(artwork.Width, artwork.Height) / 100;
        int outside = 0, points = 0;
        foreach (var hit in drill.Hits)
        {
            points++;
            if (!artwork.Contains(hit.X, hit.Y, margin)) outside++;
        }

        // ROUTED SLOT VERTICES COUNT TOO. A rout file commonly holds slots and NOTHING ELSE, and
        // testing hits alone made this check pass vacuously on exactly those files — "0 of 0 hits fell
        // outside" reads as agreement, so the wrong-format retry below it never ran and the slots
        // landed off the board at ten times their real coordinates. A slot is drilled through the same
        // copper a hole is, so it belongs inside the artwork by the same argument.
        foreach (var slot in drill.Slots)
            for (int i = 0; i + 1 < slot.Xy.Length; i += 2)
            {
                points++;
                if (!artwork.Contains(slot.Xy[i], slot.Xy[i + 1], margin)) outside++;
            }

        double wr = artwork.Width == 0 ? 1 : (double)drill.Extents.Width / artwork.Width;
        double hr = artwork.Height == 0 ? 1 : (double)drill.Extents.Height / artwork.Height;
        bool agrees = outside == 0;

        string what = drill.Slots.Count > 0 ? "hit/slot points" : "hits";
        string report = agrees
            ? $"Drill data agrees with the artwork: all {points} {what} fall inside the artwork " +
              $"extent, and the two spans differ by a factor of {wr:0.###} in X and {hr:0.###} in Y."
            : $"Drill data DISAGREES with the artwork under the {drill.Format} format: " +
              $"{outside} of {points} {what} fall outside the artwork extent, and the drill span " +
              $"is {wr:0.###}× the artwork's in X and {hr:0.###}× in Y. The format inference is wrong.";

        return new DrillExtentsCheck(agrees, outside, points, wr, hr, report);
    }

    // ── Refusals ──────────────────────────────────────────────────────────────

    private static ExcellonReadResult Refuse(string refusal, DrillFormatOverride? overrides) =>
        new()
        {
            Refusal = refusal,
            Format = ExcellonFormat.Resolve(new DrillFormatDeclarations(), overrides),
        };

    /// <summary>R-L4f-3: some toolchains emit a BINARY EIA-coded drill file under the same extension
    /// as an ASCII one. Parsed as text it yields garbage coordinates that look like a board, so it is
    /// refused by name — and the refusal says what to look for instead, because the user has to go
    /// back to whatever wrote it.</summary>
    private static string? BinaryRefusal(ReadOnlySpan<byte> bytes)
    {
        int limit = Math.Min(bytes.Length, 512);
        for (int i = 0; i < limit; i++)
        {
            byte b = bytes[i];
            if (b is (byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0C) continue;
            if (b < 0x20 || b == 0x7F) return BinaryMessage;
        }
        return null;
    }

    private static string? BinaryRefusal(string text)
    {
        int limit = Math.Min(text.Length, 512);
        for (int i = 0; i < limit; i++)
        {
            char c = text[i];
            if (c is '\t' or '\n' or '\r' or '\f' or '\uFEFF') continue;
            if (c < 0x20 || c == 0x7F) return BinaryMessage;
        }
        return null;
    }

    private const string BinaryMessage =
        "This looks like a BINARY drill file (EIA-coded), not an ASCII Excellon one — circuitRF reads " +
        "only the ASCII form, and nothing was imported. Re-export the drill data as ASCII/Excellon " +
        "(sometimes offered as \"Excellon\" against \"binary\" or \"EIA\") and import that file instead.";

    /// <summary>R-L4f-3's other half: a drill LISTING or REPORT sits alongside the real drill file,
    /// often under the same stem, and is human-readable prose. L4g's classifier must not hand one to
    /// this reader — and this reader must not accept it if it does.</summary>
    private static string? NotADrillFileRefusal(string text)
    {
        int lines = 0, drillLike = 0;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == ';') continue;
            lines++;
            if (LooksLikeDrillSyntax(line)) drillLike++;
        }

        if (lines == 0)
            return "This file contains no drill commands at all — nothing was imported. If it is a " +
                   "drill listing or report rather than a drill file, the drill file itself is a " +
                   "separate file, usually with the same name.";

        // Deliberately generous: a real drill file is essentially ALL command lines, so anything below
        // a clear majority is prose with a few numbers in it.
        return drillLike * 2 >= lines
            ? null
            : $"Only {drillLike} of this file's {lines} content lines are drill commands — this reads as " +
              "a drill listing or report, not a drill file, so nothing was imported. The drill file " +
              "itself is a separate file, usually with the same name.";
    }

    private static bool LooksLikeDrillSyntax(string line)
    {
        if (line[0] == '%') return true;
        if (line.StartsWith("INCH", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("METRIC", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("FMAT", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("ICI", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("VER", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("DETECT", StringComparison.OrdinalIgnoreCase)) return true;

        // A command line is letter-then-number tokens end to end, with nothing else in it.
        int tokens = 0;
        int i = 0;
        while (i < line.Length)
        {
            if (char.IsWhiteSpace(line[i])) { i++; continue; }
            if (line[i] == ';') break;
            if (!char.IsAsciiLetter(line[i])) return false;
            i++;
            int start = i;
            while (i < line.Length && (char.IsAsciiDigit(line[i]) || line[i] is '.' or '+' or '-')) i++;
            if (i == start) return false;
            tokens++;
        }
        return tokens > 0;
    }

    // ── The pre-scan: what the file SAYS about its own format (§2) ─────────────

    internal static DrillFormatDeclarations ScanDeclarations(string text)
    {
        var found = new DrillFormatDeclarations();

        // Evidence source 6: the shape of the coordinate words themselves — see
        // DrillFormatDeclarations.CoordinateDigitWidth for why both of these are needed.
        int minWidth = int.MaxValue, maxWidth = 0, coordinateWords = 0;
        bool anyLeadingZero = false;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0) continue;

            if (line[0] == ';')
            {
                ScanFormatComment(line, found);
                continue;
            }

            if (line.StartsWith("INCH", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("METRIC", StringComparison.OrdinalIgnoreCase))
            {
                ScanUnitsKeyword(line, found);
                continue;
            }

            var tokens = Tokenize(line);

            // A 'C' word is a tool diameter only on a TOOL DEFINITION line. Tokenize is deliberately
            // permissive — it reads a letter and whatever follows it — so a word-shaped header line
            // yields a 'C' with an empty value, and taking those would print blanks into the evidence
            // sentence that names the tool table.
            bool toolDefinition = tokens.Any(t => t.Letter == 'T') && tokens.Any(t => t.Letter == 'C');

            foreach (var (letter, value) in tokens)
            {
                switch (letter)
                {
                    case 'M' when value == "71" && found.Unit is null:
                        found.Unit = GerberUnit.Millimetres;
                        found.UnitEvidence = DrillFormatEvidence.UnitGCode;
                        break;
                    case 'M' when value == "72" && found.Unit is null:
                        found.Unit = GerberUnit.Inches;
                        found.UnitEvidence = DrillFormatEvidence.UnitGCode;
                        break;
                    case 'C' when toolDefinition && value.Length > 0:
                        found.ToolDiameterTexts.Add(value);
                        break;
                    case 'X' or 'Y' when value.Contains('.', StringComparison.Ordinal):
                        found.DecimalCoordinates = true;
                        break;
                    case 'X' or 'Y':
                    {
                        string digits = value.TrimStart('+', '-');
                        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)) break;
                        coordinateWords++;
                        minWidth = Math.Min(minWidth, digits.Length);
                        maxWidth = Math.Max(maxWidth, digits.Length);
                        if (digits[0] == '0' && digits.Length > 1) anyLeadingZero = true;
                        break;
                    }
                }
            }
        }

        // Four words is the floor at which "they are all the same width" stops being a coincidence a
        // two-hole file could produce by accident.
        if (!found.DecimalCoordinates && coordinateWords >= 4 && minWidth == maxWidth && anyLeadingZero)
            found.CoordinateDigitWidth = maxWidth;

        return found;
    }

    /// <summary>§2 evidence source 1 — an explicit format comment, <c>;FILE_FORMAT=2:4</c>. Also the
    /// <c>; #@!</c> attribute comments, which say nothing about the format and must not be mistaken
    /// for one.</summary>
    private static void ScanFormatComment(string line, DrillFormatDeclarations found)
    {
        if (line.Contains("#@!", StringComparison.Ordinal)) return;

        int eq = line.IndexOf('=');
        if (eq < 0) return;
        string key = line[1..eq].Trim();
        if (!key.Contains("FORMAT", StringComparison.OrdinalIgnoreCase)) return;

        string value = line[(eq + 1)..].Trim();
        int colon = value.IndexOf(':');
        if (colon <= 0) return;
        if (int.TryParse(value[..colon].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int ints) &&
            int.TryParse(value[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int decs))
        {
            found.IntegerDigits = ints;
            found.DecimalDigits = decs;
            found.DigitsEvidence = DrillFormatEvidence.FormatComment;
        }
    }

    /// <summary>§2 evidence source 2 — <c>INCH</c>/<c>METRIC</c>, the <c>LZ</c>/<c>TZ</c> word that
    /// usually follows, and an optional <c>000.000</c> digit-count field.
    ///
    /// <para><b>LZ/TZ is inverted against Gerber's %FS and that is not a typo.</b> Excellon's word
    /// names the zeros that are KEPT — <c>LZ</c> is "leading zeros present", so the SUPPRESSED zeros
    /// are the trailing ones. <see cref="GerberZeroOmission"/> keeps its Gerber meaning (what is
    /// omitted) everywhere, so the inversion happens exactly once, here.</para></summary>
    private static void ScanUnitsKeyword(string line, DrillFormatDeclarations found)
    {
        found.Unit = line.StartsWith("METRIC", StringComparison.OrdinalIgnoreCase)
            ? GerberUnit.Millimetres : GerberUnit.Inches;
        found.UnitEvidence = DrillFormatEvidence.UnitsKeyword;

        foreach (string fieldRaw in line.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            string field = fieldRaw.Trim();
            if (field.Equals("LZ", StringComparison.OrdinalIgnoreCase))
                found.ZeroOmission = GerberZeroOmission.Trailing;   // leading zeros KEPT
            else if (field.Equals("TZ", StringComparison.OrdinalIgnoreCase))
                found.ZeroOmission = GerberZeroOmission.Leading;    // trailing zeros KEPT
            else if (field.Contains('.', StringComparison.Ordinal) && found.IntegerDigits is null)
            {
                int dot = field.IndexOf('.');
                string ints = field[..dot], decs = field[(dot + 1)..];
                if (ints.Length > 0 && decs.Length > 0 && ints.All(char.IsAsciiDigit) && decs.All(char.IsAsciiDigit))
                {
                    found.IntegerDigits = ints.Length;
                    found.DecimalDigits = decs.Length;
                    found.DigitsEvidence = DrillFormatEvidence.UnitsKeyword;
                }
            }
        }
    }

    /// <summary>One line into (letter, digits) tokens. Shared by the pre-scan and the state machine so
    /// the two can never disagree about what a line contains.</summary>
    internal static List<(char Letter, string Value)> Tokenize(string line)
    {
        var tokens = new List<(char, string)>();
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == ';') break;
            if (!char.IsAsciiLetter(c)) { i++; continue; }
            i++;
            int start = i;
            while (i < line.Length && (char.IsAsciiDigit(line[i]) || line[i] is '.' or '+' or '-')) i++;
            tokens.Add((char.ToUpperInvariant(c), line[start..i]));
        }
        return tokens;
    }
}

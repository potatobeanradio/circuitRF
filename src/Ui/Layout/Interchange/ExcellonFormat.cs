// What a drill file's numbers MEAN — inferred, with its evidence, and overridable
// (docs/sonnet-briefs/brief-L4f-excellon-drill-and-vias.md §2).
//
// THE PROBLEM THIS FILE EXISTS FOR: Gerber declares its own units and digit format in every file.
// A drill file frequently declares NEITHER — no M48 header, no INCH/METRIC, no format statement of
// any kind, just tool definitions and a stream of coordinates. The failure mode is the worst kind:
// the file parses cleanly and yields a board a thousand times too large. So this never guesses
// silently. It states what it concluded, names the evidence that settled each of the three separate
// unknowns (unit, digit counts, zero suppression), and takes an override for any of them
// independently. The precedent is exact: L4b's R-L4b-4 / DxfUnitsPromptDialog, for a DXF with no
// $INSUNITS — and here the missing statement is the COMMON case rather than the exceptional one.
//
// ONE TRAP, NAMED LOUDLY, BECAUSE IT INVERTS AGAINST THE SIBLING FORMAT: Gerber's %FS<L|T> names the
// zeros that are OMITTED (%FSL = leading zeros omitted). Excellon's LZ/TZ names the zeros that are
// KEPT — "LZ" is a file whose leading zeros are PRESENT, which means its TRAILING zeros are the
// suppressed ones. The two conventions read as opposites of each other, and reading one as the other
// is a coordinate wrong by orders of magnitude, not a coordinate wrong by a rounding. The mapping
// below is the only place that inversion happens, and GerberZeroOmission keeps its Gerber meaning
// throughout (what is OMITTED) so that GerberCoordinateFormat.ParseCoordinateWord — the arithmetic
// both readers share — needs no drill-specific branch.

using System.Globalization;

namespace CircuitRF.Ui.Layout.Interchange;

/// <summary>Where one part of the format came from. Ordered strongest-first, exactly as §2 ranks the
/// evidence, so a caller can compare two sources with <c>&lt;</c> and a report can say which rung
/// settled it.</summary>
public enum DrillFormatEvidence
{
    /// <summary>Given by the caller (L4h's prompt). Beats everything the file says, by definition.</summary>
    Override,
    /// <summary>An explicit format comment — <c>;FILE_FORMAT=2:4</c>.</summary>
    FormatComment,
    /// <summary>The <c>INCH</c> / <c>METRIC</c> keyword and the <c>LZ</c>/<c>TZ</c> word that usually
    /// follows it, plus any <c>000.000</c> digit-count field on the same line.</summary>
    UnitsKeyword,
    /// <summary><c>M71</c> (metric) / <c>M72</c> (inch).</summary>
    UnitGCode,
    /// <summary>The tool diameters. Always written as explicit decimals, and nothing else in a file
    /// with no header is as unambiguous — a tool of 0.0138 is inches and a tool of 0.35 is
    /// millimetres.</summary>
    ToolDiameters,
    /// <summary>Every coordinate in the file carries a literal decimal point, so there is no digit
    /// count and no suppression question to answer (R-L4f-2's third form — what a modern export
    /// writes, and what circuitRF's own <see cref="ExcellonWriter"/> writes).</summary>
    DecimalCoordinates,
    /// <summary>Nothing in the file said. This is the value that <b>needs a prompt</b>.</summary>
    Defaulted,
}

/// <summary>A caller-supplied override for any of the three unknowns, independently — R-L4f-2: a file
/// can make the units clear and leave the suppression open, so one control must not force the
/// other.</summary>
public sealed record DrillFormatOverride(
    GerberUnit? Unit = null,
    int? IntegerDigits = null,
    int? DecimalDigits = null,
    GerberZeroOmission? ZeroOmission = null);

/// <summary>The resolved format plus, for each part of it, the rung of §2's ladder that settled it.
/// <see cref="Evidence"/> is the human-readable form of the same thing — L4h decides from
/// <see cref="RequiredAGuess"/> whether any of it warrants a prompt; this phase asks no one
/// anything.</summary>
public sealed class DrillFormatInference
{
    public required GerberUnit Unit { get; init; }
    public required DrillFormatEvidence UnitEvidence { get; init; }

    public required int IntegerDigits { get; init; }
    public required int DecimalDigits { get; init; }
    public required DrillFormatEvidence DigitsEvidence { get; init; }

    public required GerberZeroOmission ZeroOmission { get; init; }
    public required DrillFormatEvidence ZeroOmissionEvidence { get; init; }

    /// <summary>True when the file's coordinates carry literal decimal points, so the digit counts and
    /// the suppression convention are both moot (R-L4f-2).</summary>
    public required bool DecimalCoordinates { get; init; }

    /// <summary>One sentence per part of the format, naming the source that settled it. Handed
    /// verbatim to whatever reports the import.</summary>
    public required IReadOnlyList<string> Evidence { get; init; }

    /// <summary>True when at least one part of the format came from <see cref="DrillFormatEvidence.ToolDiameters"/>
    /// or <see cref="DrillFormatEvidence.Defaulted"/> — i.e. the file did not state it and this is an
    /// inference that could be wrong. The number R-L4h-6 needs in order to know whether its prompt is
    /// a rare escape hatch or the normal path.</summary>
    public bool RequiredAGuess =>
        UnitEvidence is DrillFormatEvidence.ToolDiameters or DrillFormatEvidence.Defaulted ||
        (!DecimalCoordinates && (DigitsEvidence == DrillFormatEvidence.Defaulted ||
                                 ZeroOmissionEvidence == DrillFormatEvidence.Defaulted));

    public override string ToString() =>
        $"{(Unit == GerberUnit.Inches ? "inch" : "mm")} {IntegerDigits}:{DecimalDigits} " +
        (DecimalCoordinates ? "decimal-point coordinates" :
         ZeroOmission == GerberZeroOmission.Leading ? "leading zeros suppressed" : "trailing zeros suppressed");
}

/// <summary>What the pre-scan found the file actually SAYING, before anything is decided. Every field
/// is null when the file was silent — which, for a drill file, is the common case.</summary>
public sealed class DrillFormatDeclarations
{
    public GerberUnit? Unit { get; set; }
    public DrillFormatEvidence UnitEvidence { get; set; } = DrillFormatEvidence.Defaulted;
    public int? IntegerDigits { get; set; }
    public int? DecimalDigits { get; set; }
    public DrillFormatEvidence DigitsEvidence { get; set; } = DrillFormatEvidence.Defaulted;
    public GerberZeroOmission? ZeroOmission { get; set; }
    public bool DecimalCoordinates { get; set; }

    /// <summary>Every tool diameter as it was WRITTEN (the decimal text), in file order. The unit
    /// inference reads these and nothing else.</summary>
    public List<string> ToolDiameterTexts { get; } = [];
}

public static class ExcellonFormat
{
    /// <summary>Classic defaults, used only when the file said nothing and the tool table could not
    /// settle it either. Both are recorded as <see cref="DrillFormatEvidence.Defaulted"/> so nothing
    /// downstream can mistake them for something the file declared.</summary>
    public const int DefaultInchIntegerDigits = 2, DefaultInchDecimalDigits = 4;
    public const int DefaultMetricIntegerDigits = 3, DefaultMetricDecimalDigits = 3;

    /// <summary>Turns what the file said (plus any caller override) into a decision, and records which
    /// rung of §2's ladder settled each of the three separate unknowns.</summary>
    public static DrillFormatInference Resolve(DrillFormatDeclarations found, DrillFormatOverride? overrides)
    {
        var evidence = new List<string>();

        // ── Unit ──────────────────────────────────────────────────────────────
        GerberUnit unit;
        DrillFormatEvidence unitEvidence;
        if (overrides?.Unit is { } ou)
        {
            unit = ou;
            unitEvidence = DrillFormatEvidence.Override;
            evidence.Add($"Units: {Name(unit)} — set by the caller, overriding the file.");
        }
        else if (found.Unit is { } fu)
        {
            unit = fu;
            unitEvidence = found.UnitEvidence;
            evidence.Add($"Units: {Name(unit)} — declared by the file ({Describe(found.UnitEvidence)}).");
        }
        else if (InferUnitFromToolDiameters(found.ToolDiameterTexts) is { } tu)
        {
            unit = tu;
            unitEvidence = DrillFormatEvidence.ToolDiameters;
            evidence.Add($"Units: {Name(unit)} — INFERRED from the tool table " +
                         $"({string.Join(", ", found.ToolDiameterTexts)}); the file does not say.");
        }
        else
        {
            unit = GerberUnit.Inches;
            unitEvidence = DrillFormatEvidence.Defaulted;
            evidence.Add("Units: inch — DEFAULTED. The file does not say and its tool table could not settle it.");
        }

        // ── Digit counts ──────────────────────────────────────────────────────
        int integerDigits, decimalDigits;
        DrillFormatEvidence digitsEvidence;
        if (overrides?.IntegerDigits is { } oi || overrides?.DecimalDigits is { } od)
        {
            integerDigits = overrides?.IntegerDigits ?? found.IntegerDigits ?? DefaultIntegers(unit);
            decimalDigits = overrides?.DecimalDigits ?? found.DecimalDigits ?? DefaultDecimals(unit);
            digitsEvidence = DrillFormatEvidence.Override;
            evidence.Add($"Digit format: {integerDigits}:{decimalDigits} — set by the caller, overriding the file.");
        }
        else if (found.IntegerDigits is { } fi && found.DecimalDigits is { } fd)
        {
            integerDigits = fi;
            decimalDigits = fd;
            digitsEvidence = found.DigitsEvidence;
            evidence.Add($"Digit format: {fi}:{fd} — declared by the file ({Describe(found.DigitsEvidence)}).");
        }
        else if (found.DecimalCoordinates)
        {
            integerDigits = DefaultIntegers(unit);
            decimalDigits = DefaultDecimals(unit);
            digitsEvidence = DrillFormatEvidence.DecimalCoordinates;
            evidence.Add("Digit format: not needed — every coordinate carries a literal decimal point.");
        }
        else
        {
            integerDigits = DefaultIntegers(unit);
            decimalDigits = DefaultDecimals(unit);
            digitsEvidence = DrillFormatEvidence.Defaulted;
            evidence.Add($"Digit format: {integerDigits}:{decimalDigits} — DEFAULTED for {Name(unit)}; " +
                         "the file declares no format.");
        }

        // ── Zero suppression ──────────────────────────────────────────────────
        GerberZeroOmission zero;
        DrillFormatEvidence zeroEvidence;
        if (overrides?.ZeroOmission is { } oz)
        {
            zero = oz;
            zeroEvidence = DrillFormatEvidence.Override;
            evidence.Add($"Zero suppression: {Name(zero)} — set by the caller, overriding the file.");
        }
        else if (found.DecimalCoordinates)
        {
            zero = GerberZeroOmission.Leading;
            zeroEvidence = DrillFormatEvidence.DecimalCoordinates;
            evidence.Add("Zero suppression: not applicable — every coordinate carries a literal decimal point.");
        }
        else if (found.ZeroOmission is { } fz)
        {
            zero = fz;
            zeroEvidence = DrillFormatEvidence.UnitsKeyword;
            evidence.Add($"Zero suppression: {Name(zero)} — declared by the file's LZ/TZ word " +
                         "(Excellon's LZ/TZ names the zeros KEPT, the opposite sense to Gerber's %FS).");
        }
        else
        {
            zero = GerberZeroOmission.Leading;
            zeroEvidence = DrillFormatEvidence.Defaulted;
            evidence.Add("Zero suppression: leading zeros suppressed — DEFAULTED; the file carries no LZ/TZ word.");
        }

        return new DrillFormatInference
        {
            Unit = unit,
            UnitEvidence = unitEvidence,
            IntegerDigits = integerDigits,
            DecimalDigits = decimalDigits,
            DigitsEvidence = digitsEvidence,
            ZeroOmission = zero,
            ZeroOmissionEvidence = zeroEvidence,
            DecimalCoordinates = found.DecimalCoordinates,
            Evidence = evidence,
        };
    }

    /// <summary>§2 evidence source 4. Tool diameters are always explicit decimals, so they are the one
    /// unambiguous thing in a headerless file. Two independent signals, deliberately combined rather
    /// than either alone: a drill bigger than half an INCH does not exist (so a diameter above 0.5
    /// must be millimetres), and an inch diameter is conventionally written to four or more decimals
    /// where a millimetre one is written to two or three. Returns null when the table is empty or the
    /// two signals leave a genuinely ambiguous file — this must not pretend to know.</summary>
    public static GerberUnit? InferUnitFromToolDiameters(IReadOnlyList<string> diameterTexts)
    {
        double max = 0;
        int maxDecimals = 0;
        int seen = 0;
        foreach (string text in diameterTexts)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v <= 0)
                continue;
            seen++;
            if (v > max) max = v;
            int dot = text.IndexOf('.');
            if (dot >= 0) maxDecimals = Math.Max(maxDecimals, text.Length - dot - 1);
        }
        if (seen == 0) return null;

        if (max > 0.5) return GerberUnit.Millimetres;      // 0.5 inch is not a drill
        if (maxDecimals >= 4) return GerberUnit.Inches;    // 0.0138, 0.0236 — inch spelling
        return GerberUnit.Millimetres;                     // 0.35, 0.20 — millimetre spelling
    }

    private static int DefaultIntegers(GerberUnit u) =>
        u == GerberUnit.Inches ? DefaultInchIntegerDigits : DefaultMetricIntegerDigits;

    private static int DefaultDecimals(GerberUnit u) =>
        u == GerberUnit.Inches ? DefaultInchDecimalDigits : DefaultMetricDecimalDigits;

    private static string Name(GerberUnit u) => u == GerberUnit.Inches ? "inch" : "millimetre";

    private static string Name(GerberZeroOmission z) =>
        z == GerberZeroOmission.Leading ? "leading zeros suppressed" : "trailing zeros suppressed";

    private static string Describe(DrillFormatEvidence e) => e switch
    {
        DrillFormatEvidence.FormatComment => "an explicit format comment",
        DrillFormatEvidence.UnitsKeyword => "its INCH/METRIC line",
        DrillFormatEvidence.UnitGCode => "M71/M72",
        DrillFormatEvidence.ToolDiameters => "its tool table",
        DrillFormatEvidence.DecimalCoordinates => "decimal-point coordinates",
        DrillFormatEvidence.Override => "a caller override",
        _ => "nothing — defaulted",
    };
}

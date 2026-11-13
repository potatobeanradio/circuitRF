// Where what is known about a part came from — brief-artsch-4-parts-and-parts-table.md R-as4-1, R-as4-7;
// overview D10.
//
// Every field of a parts-table row records its SOURCE, because the row is a reading the user checks and
// a value the user cannot trace is a value nobody can correct. The sources are ordered strongest first;
// a later phase adds one (AS-10's silkscreen) by implementing IPartEvidenceSource, not by editing the
// reading.

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>A source of evidence about a part, strongest first (R-as4-1).</summary>
public enum PartEvidenceSource
{
    /// <summary>A placed footprint instance: its designator, part kind and land-pattern cell.</summary>
    Instance,

    /// <summary>A placement file's row, landed on the board's pads.</summary>
    Placement,

    /// <summary>Pads the artwork declares as one part's pins — a Gerber set's <c>%TO.P</c> attributes (AS-12).</summary>
    DeclaredPads,

    /// <summary>A bill of materials row.</summary>
    Bom,

    /// <summary>Two pads whose sizes and gap match a case's land pattern.</summary>
    LandPattern,

    /// <summary>A body outline on the silkscreen whose ends lie on two pieces of copper across a gap: a part with no land of
    /// its own.</summary>
    BodyOutline,

    /// <summary>A designator read off the silkscreen (AS-10).</summary>
    Silkscreen,

    /// <summary>Read off a schematic picture (brief-img-10): its drawing named the kind, its words the designator and the
    /// value.</summary>
    Picture,

    /// <summary>The designator's own letters (<c>R</c>, <c>C</c>, <c>FB</c>, …) — kind only.</summary>
    Refdes,

    /// <summary>Recognition made it up: a generated designator (<c>C_A1</c>).</summary>
    Generated,

    /// <summary>The model came from the workspace's part library.</summary>
    Library,

    /// <summary>The model came from a Touchstone file in the workspace named after the part number.</summary>
    File,

    /// <summary>The user said so in the parts table.</summary>
    User,
}

/// <summary>The fields of a row that carry a source — the keys of the <c>Evidence</c> column.</summary>
public enum PartField { Refdes, Kind, Case, Value, PartNumber, Model }

/// <summary>
/// One claim about a part from an evidence source that is not one of the built-in readers — AS-10's
/// silkscreen designators, and any source plugged in later. A claim names a part at a point; the reading
/// gives the designators of one source to the unnamed parts one to one, the least total distance from each
/// claim to its part's body (<see cref="Silkscreen.RefdesAssociation"/>).
/// </summary>
/// <param name="Source">Which source made it.</param>
/// <param name="X">Where, DBU.</param>
/// <param name="Y">Where, DBU.</param>
/// <param name="Refdes">The designator claimed.</param>
/// <param name="ReachDbu">How far from a part's body the claim may be and still name it; 0 for no limit of the
/// claim's own — three of the part's body diagonals is always the limit (R-as10-4).</param>
public sealed record PartClaim(PartEvidenceSource Source, long X, long Y, string Refdes, long ReachDbu)
{
    /// <summary>The silkscreen line the claim was read from, when it was — what Learn these glyphs teaches from.</summary>
    public Silkscreen.SilkTextLine? Line { get; init; }
}

/// <summary>A source of <see cref="PartClaim"/>s, plugged into <see cref="PartReading"/>.</summary>
public interface IPartEvidenceSource
{
    /// <summary>Which source this is.</summary>
    PartEvidenceSource Source { get; }

    /// <summary>Every claim it makes about the board in scope.</summary>
    IEnumerable<PartClaim> Claims(RecognitionInput input, BoardGraph board);
}

/// <summary>The spelling of sources and fields in the <c>Evidence</c> column.</summary>
public static class PartEvidenceText
{
    public static string Of(PartEvidenceSource source) => source switch
    {
        PartEvidenceSource.Instance => "instance",
        PartEvidenceSource.Placement => "placement",
        PartEvidenceSource.DeclaredPads => "declared",
        PartEvidenceSource.Bom => "bom",
        PartEvidenceSource.LandPattern => "land",
        PartEvidenceSource.BodyOutline => "body",
        PartEvidenceSource.Silkscreen => "silk",
        PartEvidenceSource.Picture => "picture",
        PartEvidenceSource.Refdes => "refdes",
        PartEvidenceSource.Generated => "generated",
        PartEvidenceSource.Library => "library",
        PartEvidenceSource.File => "file",
        _ => "user",
    };

    public static string Of(PartField field) => field switch
    {
        PartField.Refdes => "refdes",
        PartField.Kind => "kind",
        PartField.Case => "case",
        PartField.Value => "value",
        PartField.PartNumber => "pn",
        _ => "model",
    };

    /// <summary><c>refdes=instance;kind=refdes;case=instance;value=bom</c> — fields in a fixed order.</summary>
    public static string Format(IReadOnlyDictionary<PartField, PartEvidenceSource> evidence) =>
        string.Join(";", Enum.GetValues<PartField>()
                             .Where(evidence.ContainsKey)
                             .Select(f => $"{Of(f)}={Of(evidence[f])}"));
}

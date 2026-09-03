// The Gerber (RS-274X / X2) artwork reader (docs/sonnet-briefs/brief-L4e-gerber-import-reader.md).
// Written from the published format specification only — never from another tool's source, per §8's
// standing rule.
//
// ONE STRUCTURAL FACT DRIVES EVERYTHING HERE: a Gerber file is a PAINTED IMAGE, not a shape list.
// Strokes, flashes and filled regions are laid down in order by a small modal state machine, and each
// object is either dark (adds material) or clear (erases it). Nothing in the file says "this is a pad"
// or "this is a trace". So importing is categorically bigger than exporting: it replays the painting
// and then decides what typed shapes, if any, the result should become (see Composite below, R-L4e-13
// — the most consequential decision in the file).
//
// Scope, exactly as the brief draws it: bytes, tokens and coordinates. This file knows nothing about a
// CellFolder, a Technology, a Messages sink or a dialog — that is L4g's job, the same split
// PcbReader/PcbImport and DxfReader/DxfImport already established, and it is what makes this reader
// headlessly testable against fixtures with no workspace anywhere.

using Clipper2Lib;

using System.Globalization;
using System.Text;

namespace CircuitRF.Ui.Layout.Interchange;

/// <summary>One imported object, still carrying the X2 attributes that were in force when it was
/// painted. The net is on <see cref="LayoutShape.Net"/> (where L4c's writer took it from); the rest
/// have nowhere to live on <see cref="LayoutShape"/> and ride alongside — mirroring
/// <c>DxfImportedShape</c>'s own "shape plus the format's side data" precedent. <see cref="Shape"/>'s
/// <see cref="LayoutShape.Layer"/> is always <c>default</c>: one file is one layer, and which layer it
/// is, is L4g's cascade to decide, not this reader's.</summary>
public sealed record GerberImportedShape(LayoutShape Shape, string? AperFunction, string? Component, string? Pin);

/// <summary>Everything one artwork file turned out to be. <see cref="Refusal"/> non-null means nothing
/// was read and nothing must be created.</summary>
public sealed class GerberReadResult
{
    public string? Refusal { get; init; }

    public IReadOnlyList<GerberImportedShape> Shapes { get; init; } = [];

    /// <summary>R-L4e-0: the neutral model both readers and writers of every interchange format
    /// exchange. Layer assignment happens in L4g, so every shape here still carries the default
    /// <see cref="LayerKey"/>.</summary>
    public InterchangeStructure ToStructure(string name) =>
        new(name, [.. Shapes.Select(s => s.Shape)], []);

    // ── What the file declared ────────────────────────────────────────────────

    public GerberUnit Unit { get; init; } = GerberUnit.Millimetres;
    public int IntegerDigits { get; init; }
    public int DecimalDigits { get; init; }
    public GerberNotation Notation { get; init; } = GerberNotation.Absolute;

    /// <summary>False when the declared format's output unit is not a whole number of DBU (R-L4e-2's
    /// only inexact row: inch at 6 decimals). Coordinates were then rounded, not refused.</summary>
    public bool CoordinatesExact { get; init; } = true;

    /// <summary>R-L4e-2: the rounding error the import could have introduced, as a number. Exactly
    /// zero on every exact format.</summary>
    public double WorstCaseRoundingErrorDbu { get; init; }

    // ── X2 attributes (R-L4e-16) ──────────────────────────────────────────────

    /// <summary>Every <c>%TF</c> file attribute, by name (including the leading dot on a standard
    /// one). Names compare case-insensitively — the same file set spells a file function
    /// <c>Soldermask</c> in the artwork and <c>SolderMask</c> in its own job file.</summary>
    public IReadOnlyDictionary<string, string> FileAttributes { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string? FileFunction => Attr(".FileFunction");

    /// <summary>R-L4e-17: what the artwork REPRESENTS, not an instruction to invert it. A solder mask
    /// declares <c>Negative</c> and is nonetheless painted positive; reading this as an inversion turns
    /// every mask inside out, which renders plausibly and is completely wrong. The command that
    /// genuinely inverts is <c>%IPNEG</c>, and that one is refused by name.</summary>
    public string? FilePolarity => Attr(".FilePolarity");

    /// <summary>R-L4e-5: recorded, acted on by nothing. Useful evidence for L4g's identity cascade;
    /// not authority.</summary>
    public string? ImageName { get; init; }
    public string? LayerName { get; init; }

    private string? Attr(string name) => FileAttributes.TryGetValue(name, out string? v) ? v : null;

    // ── Counters (R-L4e-21: counters only, never a wall clock) ────────────────

    /// <summary>R-L4e-19: a vector-filled pour arrives as thousands of strokes. It is correct artwork
    /// and is neither editable copper nor meshable, and the user cannot act on what they are not
    /// told — so the count comes back whether or not anyone asks.</summary>
    public int StrokeCount { get; init; }
    public int FlashCount { get; init; }
    public int RegionCount { get; init; }
    public int ArcCount { get; init; }

    /// <summary>R-L4e-15: how many times an <c>%SR</c> block multiplied the objects inside it (1 when
    /// the file has no step-and-repeat). Reported so a user who imported a panel knows why the shape
    /// count is what it is.</summary>
    public int StepRepeatFactor { get; init; } = 1;

    /// <summary>R-L4e-13: true when the file painted at least one CLEAR object, so the layer had to be
    /// composited through Clipper and its shape identities are gone.</summary>
    public bool Composited { get; init; }
    public string? CompositeReason { get; init; }

    /// <summary>R-L4e-6: every unrecognized command, by name, once, with a count.</summary>
    public IReadOnlyDictionary<string, int> UnknownCommandCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Constructs read but deliberately degraded or dropped, by name, with a count — a
    /// non-circular stroke swept into a region, a moire primitive, a degenerate contour.</summary>
    public IReadOnlyDictionary<string, int> SkippedConstructCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

public sealed partial class GerberReader
{
    /// <summary>R-L4e-20 / L4d's R-L4d-20 unchanged: refuse BEFORE allocating, and name the number.
    /// Deliberately the same constant the other unbounded-expansion path already establishes rather
    /// than a second number that can drift from it.</summary>
    public const long EntityHardCeiling = LayoutFlatten.FlattenAllLevelsHardCeiling;

    /// <summary>Flattening tolerance for the few places a curve must become a polyline here: hole
    /// rings, a non-circular aperture sweep, and the compositing booleans. 0.1 micron at the default
    /// DbuPerMicron. Shapes that survive as shapes keep their arcs (R-L4e-11).</summary>
    private const long TolDbu = GerberPrimitives.CircleTolDbu;

    public static GerberReadResult Read(TextReader reader, int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron)
        => Read(reader.ReadToEnd(), dbuPerMicron);

    public static GerberReadResult Read(string text, int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron)
    {
        // R-L4e-20: the census is a raw scan of the text, before a single token or shape is allocated.
        long operations = CountOperations(text);
        if (operations > EntityHardCeiling)
            return new GerberReadResult
            {
                Refusal = $"This Gerber file contains {operations:N0} draw/flash operations, over " +
                          $"circuitRF's import ceiling of {EntityHardCeiling:N0}. Nothing was imported.",
            };

        var reader = new GerberReader(dbuPerMicron);
        reader.Parse(text);
        return reader.Build();
    }

    private static long CountOperations(string text)
    {
        long n = 0;
        for (int i = 0; i + 2 < text.Length; i++)
            if (text[i] == 'D' && text[i + 1] == '0' && text[i + 2] is '1' or '2' or '3') n++;
        return n;
    }
}

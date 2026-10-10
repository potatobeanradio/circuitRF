// What a schematic picture's reading found — brief-img-7-schematic-wires-and-regions.md R-im7-8 (overview D16).
//
// Everything that is not a refusal is a count. The two refusals are the only ways a schematic picture fails here: no
// line work that reads as wires, or wires with nothing drawn between them.

using System.Globalization;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>The counts of one reading.</summary>
public sealed record SchematicImageReport
{
    public const string NoWiresRefusal = "No connected line work: this picture has no wires to read.";

    public const string NoSymbolsRefusal =
        "No symbols: this picture has wires but nothing drawn between them to read as a part.";

    /// <summary>The stroke width w, pixels.</summary>
    public double StrokeWidth { get; init; }

    /// <summary>How far the picture was turned to straighten it, degrees (0: it was not).</summary>
    public double DeskewDeg { get; init; }

    /// <summary>Dots of a background grid removed.</summary>
    public int GridDotsRemoved { get; init; }

    /// <summary>Whether a border frame (and the title block drawn on it) was removed.</summary>
    public bool BorderRemoved { get; init; }

    public int Words { get; init; }

    /// <summary>Text-sized pieces touching line work, kept as symbol details rather than read as words.</summary>
    public int WordsKeptAsSymbolDetail { get; init; }

    public int WireSegments { get; init; }

    /// <summary>Of <see cref="WireSegments"/>, those neither horizontal nor vertical.</summary>
    public int DiagonalWires { get; init; }

    public int JunctionDots { get; init; }

    public int CrossingsConnected { get; init; }
    public int CrossingsNotConnected { get; init; }
    public int Hops { get; init; }

    public int SymbolRegions { get; init; }
    public int DecorationsRemoved { get; init; }
    public int NetLabels { get; init; }
    public int SupplyMarks { get; init; }
    public int DanglingEnds { get; init; }

    /// <summary>The report in words, one finding per line; a count of nothing is not a line.</summary>
    public IReadOnlyList<string> Lines()
    {
        var r = new List<string>
        {
            "Stroke width " + StrokeWidth.ToString("0.#", CultureInfo.InvariantCulture) + " px.",
        };
        if (DeskewDeg != 0)
            r.Add("Straightened by " + DeskewDeg.ToString("0.##", CultureInfo.InvariantCulture) + "°.");
        if (GridDotsRemoved > 0) r.Add($"Background grid removed ({Count(GridDotsRemoved, "dot")}).");
        if (BorderRemoved) r.Add("Border frame removed.");
        r.Add(Count(Words, "word") + " found.");
        if (WordsKeptAsSymbolDetail > 0)
            r.Add($"{Count(WordsKeptAsSymbolDetail, "text-sized piece")} touching line work kept as symbol detail.");
        r.Add(Count(WireSegments, "wire segment") + (DiagonalWires > 0 ? $", {DiagonalWires} diagonal." : "."));
        if (JunctionDots > 0) r.Add(Count(JunctionDots, "junction dot") + ".");
        if (CrossingsConnected + CrossingsNotConnected > 0)
            r.Add($"Crossings: {CrossingsConnected} connected, {CrossingsNotConnected} not connected.");
        if (Hops > 0) r.Add(Count(Hops, "hop") + ", not connected.");
        r.Add(Count(SymbolRegions, "symbol region") + ".");
        if (DecorationsRemoved > 0) r.Add($"{Count(DecorationsRemoved, "decoration")} with no wire removed.");
        if (NetLabels > 0) r.Add(Count(NetLabels, "net label") + ".");
        if (SupplyMarks > 0) r.Add(Count(SupplyMarks, "supply mark") + ".");
        if (DanglingEnds > 0) r.Add(Count(DanglingEnds, "dangling wire end") + ".");
        return r;
    }

    private static string Count(int n, string noun) => n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");
}

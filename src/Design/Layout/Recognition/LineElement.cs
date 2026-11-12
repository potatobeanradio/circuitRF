// What line recognition produces — brief-artsch-5-traces-to-line-elements.md; docs/design/artwork-to-schematic.md §6.
//
// Elements and the nodes between them, nothing more: AS-6 turns them into instance lines of a TestBench. The
// elements carry their parameters in SI (metres, ohms, hertz, dB/m; a bend's angle in degrees and its miter
// as MicrostripBendMiter's ordinal), named as the components name them, so AS-6 writes each one through
// without converting anything. The substrate is never among them: MLIN, its discontinuities, CPWG and SLIN
// take theirs from the technology at extraction, as a hand-placed one does (R-as5-7).

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>The components line recognition writes.</summary>
public enum LineElementType { MLIN, MBEND, MTEE, MCROSS, MTAPER, CPWG, SLIN, TLIN }

/// <summary>What a node is attached to.</summary>
public enum LineNodeKind
{
    /// <summary>A part's terminal (AS-4).</summary>
    PartTerminal,

    /// <summary>A kept via's end on one island (AS-3).</summary>
    Via,

    /// <summary>A port (AS-3).</summary>
    Port,

    /// <summary>A junction: one node of every arm (a plain junction), or one arm of an MTEE / MCROSS.</summary>
    Junction,

    /// <summary>Between two line elements of one trace.</summary>
    Between,

    /// <summary>An end of a line that lands on nothing recognition knows — an open-ended line.</summary>
    Open,
}

/// <summary>One node of the recognised circuit. Positions in DBU.</summary>
/// <param name="Name">Its name: <c>R1.2</c> (a part's terminal), <c>V4.1</c> (a via's end), <c>P1</c> (a port),
/// <c>J2</c> / <c>J2.3</c> (a junction / one of its arms), <c>T5_2</c> (between elements of trace T5), <c>O1</c>
/// (an open end).</param>
/// <param name="Island">The board island it is on, or -1.</param>
public sealed record LineNode(string Name, LineNodeKind Kind, int Island, long X, long Y, LayerKey? Layer)
{
    /// <summary>The part, for <see cref="LineNodeKind.PartTerminal"/>.</summary>
    public string? Refdes { get; init; }

    /// <summary>The terminal's index in <see cref="PartRow.Terminals"/>, for a part's node; else -1.</summary>
    public int Terminal { get; init; } = -1;

    /// <summary>The via's index in <see cref="BoardGraph.Vias"/>, for a via's node; else -1.</summary>
    public int Via { get; init; } = -1;

    /// <summary>The port's number, for a port's node; else 0.</summary>
    public int Port { get; init; }
}

/// <summary>One line element.</summary>
public sealed record LineElement
{
    /// <summary><c>MLIN1</c>, <c>MBEND1</c>, … — numbered per type in the order found.</summary>
    public required string Name { get; init; }

    public required LineElementType Type { get; init; }

    /// <summary>Its nodes, in the component's pin order: a line's start then end; an MTEE's through-in,
    /// through-out and branch; an MCROSS's four arms counter-clockwise.</summary>
    public required IReadOnlyList<string> Nodes { get; init; }

    /// <summary>Its parameters, SI, named as the component names them — <c>W</c>, <c>L</c>, <c>G</c>,
    /// <c>W1</c>…<c>W4</c>, <c>Angle</c> (degrees), <c>Miter</c> (0 none, 1 fifty, 2 optimal), <c>Z</c>,
    /// <c>Eeff</c>, <c>F</c>, <c>Ac</c>, <c>Ad</c> (dB/m).</summary>
    public required IReadOnlyDictionary<string, double> Parameters { get; init; }

    /// <summary>The drawing layer of the trace it models.</summary>
    public LayerKey Layer { get; init; }

    public string LayerName { get; init; } = "";

    /// <summary>The stackup conductor the trace is on — the <c>SignalLayer</c> the injection binds.</summary>
    public string? SignalLayer { get; init; }

    /// <summary>The measured reference below (above, for a line with none below) — a stackup conductor's name,
    /// or null where the review took the stackup's own bottom as ground.</summary>
    public string? GroundReference { get; init; }

    /// <summary>The trace review's id of the trace it came from (<c>T3</c>), or the junction's (<c>J1</c>).</summary>
    public string Source { get; init; } = "";

    /// <summary>The artwork anchor, DBU (D12): a line's centre line from start to end, a bend's or a
    /// junction's centre as one point.</summary>
    public IReadOnlyList<(long X, long Y)> Anchor { get; init; } = [];

    /// <summary>The measured side gaps, metres, length-weighted — recorded on every MLIN and CPWG whatever the
    /// coplanar reading (D12, D19), so a misread line is one swap away. Null on a side with no ground in reach.</summary>
    public double? GapLeft { get; init; }
    public double? GapRight { get; init; }

    /// <summary>A line's drawn width, metres — on a TLIN too, which has no width parameter, so a swap to
    /// another line type (D19) holds the artwork's W. Null on a discontinuity.</summary>
    public double? Width { get; init; }

    /// <summary>The solved Z0 and εeff along it, length-weighted, where any cut was solved.</summary>
    public double? Z0 { get; init; }
    public double? Eeff { get; init; }

    /// <summary>Why a TLIN is one (D13's table), or null.</summary>
    public string? Fallback { get; init; }

    /// <summary>No cut along it was solved; its Z and εeff are a neighbour's.</summary>
    public bool Unsolved { get; init; }
}

/// <summary>Everything line recognition produced.</summary>
/// <param name="Elements">The line elements.</param>
/// <param name="Nodes">Every node an element names, and every part terminal, via end and port, each once.</param>
public sealed record LineRecognitionResult(IReadOnlyList<LineElement> Elements, IReadOnlyList<LineNode> Nodes)
{
    public static LineRecognitionResult Empty { get; } = new([], []);

    /// <summary>Every name a node was known by — a part terminal, a via end, a port, a junction arm — to the
    /// node it ended up as. Two names are one node when copper joins them with no line between.</summary>
    public IReadOnlyDictionary<string, string> Joined { get; init; } = new Dictionary<string, string>();

    /// <summary>Every part terminal, kept via end and port, each by its own name and where it is — what the copper says must be
    /// joined (<see cref="CircuitCopperCheck"/>), whatever node each ended up as.</summary>
    public IReadOnlyList<LineNode> Attachments { get; init; } = [];

    /// <summary>The node <paramref name="name"/> ended up as — itself when it was never joined to another.</summary>
    public string NodeOf(string name) => Joined.TryGetValue(name, out var n) ? n : name;

    /// <summary>The elements of one type.</summary>
    public IEnumerable<LineElement> OfType(LineElementType type) => Elements.Where(e => e.Type == type);

    /// <summary>The node named <paramref name="name"/>, or null.</summary>
    public LineNode? Node(string name) => Nodes.FirstOrDefault(n => n.Name == name);
}

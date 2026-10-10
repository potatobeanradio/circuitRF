// What a recognition guessed and what it left out — brief-artsch-3-board-graph.md R-as3-8, overview D16.
//
// A list of FINDINGS, never free text alone: each carries its class, a count, one sentence and the
// places it is about, so the GUI can expand a class and cross-probe its anchors and the CLI can print one
// line per class. A recognition is never refused for being imperfect; this is where the imperfection
// goes instead.

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>The kinds of thing a recognition reports. Later phases add their own.</summary>
public enum RecognitionFindingClass
{
    /// <summary>Which copper was read as ground, and how it was chosen (D6).</summary>
    GroundChosen,

    /// <summary>Ground-to-ground vias — stitching, fences, plane ties — dropped (D7).</summary>
    StitchingViasDropped,

    /// <summary>Vias to ground kept as <c>VIAGND</c> (or <c>GND</c> under <c>vias=ground</c>).</summary>
    GroundViasKept,

    /// <summary>A ground pad's vias past the nearest few, counted and not modelled.</summary>
    GroundViasCapped,

    /// <summary>Several <c>VIAGND</c>s on one pad: their mutual inductance is not modelled. Said once.</summary>
    GroundViaCouplingNotModelled,

    /// <summary>Signal-to-signal vias between layers kept as <c>VIA</c>.</summary>
    SignalViasKept,

    /// <summary>Vias whose barrel meets one conductor or none — they join nothing.</summary>
    ViasJoiningNothing,

    /// <summary>A pour galvanically separate from ground, read as a signal island.</summary>
    SeparatePour,

    /// <summary>Copper touching no part, port or line — a test point, a logo, a fiducial.</summary>
    CopperReadAsNothing,

    /// <summary>One piece of copper carrying two different net names; it took neither.</summary>
    ConflictingNetNames,

    /// <summary>Copper separated by a gap narrower than any process draws, read as joined — a pad and
    /// the trace that meets it rounded apart onto a file's coordinate grid.</summary>
    HairlineGapsJoined,

    /// <summary>Ports from the layout's EM setup (R-as3-6, priority 1).</summary>
    PortsFromEmSetup,

    /// <summary>Ports from port labels (priority 2).</summary>
    PortsFromLabels,

    /// <summary>Ports from layout pins (priority 2).</summary>
    PortsFromPins,

    /// <summary>Ports at a multi-pin part's pads (priority 3, D9).</summary>
    PortsAtMultiPinParts,

    /// <summary>Ports where a line reaches the board outline (priority 4).</summary>
    PortsAtBoardEdge,

    /// <summary>Ports at a connector footprint (priority 4).</summary>
    PortsAtConnectors,

    /// <summary>Ports where the scope's boundary cuts a signal island (priority 5, D11).</summary>
    PortsAtScopeCut,

    /// <summary>A port label, pin or pad that lands on no signal copper, and so is no port.</summary>
    PortsNotOnSignalCopper,

    /// <summary>The scope cuts a ground pour or plane; ground was read from the whole board.</summary>
    ScopeCutGround,

    // ── AS-4: parts (R-as4-9) ───────────────────────────────────────────────────────────────────

    /// <summary>Parts read from placed footprint instances.</summary>
    PartsFromInstances,

    /// <summary>Parts named by the placement file on land patterns found on the board.</summary>
    PartsFromPlacement,

    /// <summary>Parts named by the silkscreen (AS-10).</summary>
    PartsFromSilkscreen,

    /// <summary>Parts found from land patterns alone, given generated designators.</summary>
    PartsFromLandPatternOnly,

    /// <summary>Parts whose kind is unknown — generated as a capacitor.</summary>
    PartKindsUnknown,

    /// <summary>Parts whose value is unknown — each a variable with a transparent starting value.</summary>
    PartValuesUnknown,

    /// <summary>Bill-of-materials values in the wrong dimension for the part's kind, not used.</summary>
    PartValueWrongDimension,

    /// <summary>Parts modelled by a Touchstone file.</summary>
    PartModelsSnp,

    /// <summary>Land patterns that fit more than one case about as well.</summary>
    LandPatternAmbiguous,

    /// <summary>Parts with both pads on ground, left out.</summary>
    PartsShortedLeftOut,

    /// <summary>Parts with both pads on one signal island, left out.</summary>
    PartsBridgedLeftOut,

    /// <summary>Parts with a pad on no copper in scope, left out.</summary>
    PartsOffCopper,

    /// <summary>Multi-pin parts and connectors, cut out (D9).</summary>
    MultiPinPartsCut,

    /// <summary>Bill-of-materials designators that name no part found on the board.</summary>
    BomRowsNotOnBoard,

    /// <summary>Placement rows that land on no two-pad land pattern.</summary>
    PlacementRowsNotOnBoard,

    /// <summary>No mask or paste layer on a side: land patterns were read from the copper.</summary>
    MaskPasteAbsent,

    /// <summary>Edits in a parts table that were not applied, and measured columns that differed.</summary>
    PartsCsvNotes,

    /// <summary>Designators a parts table names that the board does not have.</summary>
    PartsCsvRefdesNotOnBoard,

    // ── AS-10: silkscreen (R-as10-6) ────────────────────────────────────────────────────────────

    /// <summary>Lines of stroked text read on the silkscreen, and how many are designators.</summary>
    SilkscreenTextRead,

    /// <summary>Silkscreen designators that name no part within reach, not used.</summary>
    SilkscreenRefdesNotAssociated,

    /// <summary>Silkscreen glyphs that fit two characters about as well.</summary>
    SilkscreenGlyphsUncertain,

    /// <summary>Silkscreen strokes that are not text — outlines, logos, marks.</summary>
    SilkscreenStrokesExcluded,

    /// <summary>Filled silkscreen shapes, which are not read.</summary>
    SilkscreenFilledNotRead,

    // ── AS-5: lines (R-as5-9) ───────────────────────────────────────────────────────────────────

    /// <summary>The line elements read from the traces, by type.</summary>
    LineElements,

    /// <summary>Lines no circuit model covers, written as TLIN, by reason.</summary>
    TlinFallbacks,

    /// <summary>Segments read as CPWG under the coplanar reading in force.</summary>
    LinesReadAsGcpw,

    /// <summary>Segments read as MLIN under the coplanar reading in force.</summary>
    LinesReadAsMlin,

    /// <summary>Junctions with more than four arms, written as a plain node.</summary>
    JunctionsOverFourArms,

    /// <summary>Bends, junctions and steps outside microstrip, which have no discontinuity model. Said once.</summary>
    DiscontinuitiesNotModelled,

    /// <summary>Line ends that land on no part, via or port: open-ended lines.</summary>
    OpenEnds,

    /// <summary>Short pieces and lines with no length left, absorbed into a neighbour.</summary>
    SliversAbsorbed,

    /// <summary>Pairs of lines close and parallel for more than λ/20: coupled, modelled uncoupled.</summary>
    CoupledPairs,

    /// <summary>Segments with no solved cross-section, written as TLIN at a neighbour's Z and εeff.</summary>
    UnsolvedSegments,

    // ── AS-6: the circuit, the drawing and the target (R-as6-2 … R-as6-6) ────────────────────────

    /// <summary>What the circuit could not carry: a part with too few terminals measured, a via that joins nothing
    /// the circuit has.</summary>
    EmitOmissions,

    /// <summary>Lines and vias left out because the copper they model reaches no port and no part (round 15).</summary>
    StrayLines,

    /// <summary>What the drawing does not carry or carries differently (<c>NetlistSchematic.Build</c>'s notes).</summary>
    DrawingNotes,

    /// <summary>Where the schematic was written, and the history checkpoint taken before a replace.</summary>
    SchematicWritten,

    // ── IM-3: a traced layout picture (R-im3-9) ─────────────────────────────────────────────────

    /// <summary>The colours found and what each was read as.</summary>
    ImageColoursMapped,

    /// <summary>Colours not traced: ignored, or mapped to a layer the technology does not have.</summary>
    ImageIgnored,

    /// <summary>The stackup is the technology's; none was read from the picture (D8). Said once.</summary>
    ImageStackupNotRead,

    /// <summary>The scale and the evidence it came from (D6).</summary>
    ImageScaleChosen,

    /// <summary>R-im3-3's resolution line.</summary>
    ImageResolution,

    /// <summary>Shapes traced, per layer.</summary>
    ImageShapesTraced,

    /// <summary>Specks below the minimum feature, removed.</summary>
    ImageSpecksRemoved,

    /// <summary>Edges lying on 0°, 45° or 90°.</summary>
    ImageEdgesSnapped,

    /// <summary>Regions fitted as circles.</summary>
    ImageCirclesFitted,

    /// <summary>Drill circles written as vias.</summary>
    ImageViasPlaced,

    /// <summary>Drill circles with no via layer in the technology, written on no layer.</summary>
    ImageDrillNoViaLayer,

    /// <summary>Silkscreen strokes kept as centre lines for reading text (IM-4).</summary>
    ImageSilkscreenStrokes,

    /// <summary>The underlay lies on a copper layer: the technology has no documentation layer.</summary>
    ImageUnderlayOnCopper,

    /// <summary>The placed bitmap is stretched: its two axes disagree.</summary>
    ImageBitmapStretched,

    /// <summary>Where the traced layout went.</summary>
    ImageLayoutWritten,
}

/// <summary>A place a finding is about, DBU, with the drawing layer when one is known.</summary>
public readonly record struct RecognitionAnchor(long X, long Y, LayerKey? Layer = null);

/// <summary>One class of finding: how many, one sentence, and where.</summary>
public sealed record RecognitionFinding(
    RecognitionFindingClass Class, int Count, string Sentence, IReadOnlyList<RecognitionAnchor> Anchors);

/// <summary>Every finding of one recognition, in the order they were made.</summary>
public sealed class RecognitionReport
{
    private readonly List<RecognitionFinding> _findings = [];

    /// <summary>The findings.</summary>
    public IReadOnlyList<RecognitionFinding> Findings => _findings;

    /// <summary>The finding of <paramref name="cls"/>, or null when nothing of that class was found.</summary>
    public RecognitionFinding? Of(RecognitionFindingClass cls) => _findings.FirstOrDefault(f => f.Class == cls);

    /// <summary>The count stated for <paramref name="cls"/>; zero when there is no such finding.</summary>
    public int Count(RecognitionFindingClass cls) => Of(cls)?.Count ?? 0;

    internal void Add(RecognitionFindingClass cls, int count, string sentence, IReadOnlyList<RecognitionAnchor>? anchors = null)
    {
        if (count <= 0) return;
        _findings.Add(new RecognitionFinding(cls, count, sentence, anchors ?? []));
    }

    /// <summary>One line per class — the CLI's stderr form.</summary>
    public IEnumerable<string> Lines() => _findings.Select(f => f.Sentence);
}

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

    /// <summary>Copper on layers the selection was not made on, joined to none of its copper, left out.</summary>
    ScopeOtherLayersLeftOut,

    // ── AS-4: parts (R-as4-9) ───────────────────────────────────────────────────────────────────

    /// <summary>Parts read from placed footprint instances.</summary>
    PartsFromInstances,

    /// <summary>Parts named by the placement file on land patterns found on the board.</summary>
    PartsFromPlacement,

    /// <summary>Parts named by the silkscreen (AS-10).</summary>
    PartsFromSilkscreen,

    // ── AS-12: the netlist the artwork declares (R-as12-5 … R-as12-7) ─────────────────────────────

    /// <summary>Parts formed from pads the artwork declares (<c>%TO.P</c>).</summary>
    PartsFromDeclaredPads,

    /// <summary>A placed part or placement row naming declared pads differently; the stronger source was taken.</summary>
    DeclaredRefdesDisagrees,

    /// <summary>Copper carrying two declared nets — a short, or an import artefact.</summary>
    DeclaredNetsShorted,

    /// <summary>A declared net on copper that does not join — an open.</summary>
    DeclaredNetsOpen,

    /// <summary>The declared nets agree with the copper. Said once.</summary>
    DeclaredNetsAgree,

    /// <summary>Parts found from land patterns alone, given generated designators.</summary>
    PartsFromLandPatternOnly,

    /// <summary>Parts read from a body outline over a gap in the copper — no land of their own.</summary>
    PartsFromBodyOutlines,

    /// <summary>Body outlines over more than two pieces of copper, not read as two-terminal parts.</summary>
    BodyOutlinesOverManyPieces,

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

    /// <summary>Designators typed as text labels on the silkscreen (a drawing's TEXT and MTEXT), read exactly.</summary>
    SilkscreenLabelsRead,

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

    /// <summary>Coplanar (or coplanar and microstrip) junctions modelled with the microstrip tee or cross — there is no
    /// coplanar tee model (R-dfr22-2-6).</summary>
    CoplanarJunctionsAsMicrostrip,

    /// <summary>Bends, junctions and steps outside microstrip, which have no discontinuity model. Said once.</summary>
    DiscontinuitiesNotModelled,

    /// <summary>Line ends that land on no part, via or port: open-ended lines.</summary>
    OpenEnds,

    /// <summary>Short pieces and lines with no length left, absorbed into a neighbour.</summary>
    SliversAbsorbed,

    /// <summary>Pads whose line ends and part terminals were joined into one node, the pad an ideal short.</summary>
    PadsJoinTheirMembers,

    /// <summary>Pairs of lines close and parallel for more than λ/20: coupled, modelled uncoupled.</summary>
    CoupledPairs,

    /// <summary>Segments with no solved cross-section, written as TLIN at a neighbour's Z and εeff.</summary>
    UnsolvedSegments,

    /// <summary>Part terminals, ports and via ends no line reached, joined to a line end farther than a line end reaches:
    /// copper no line models, shorted.</summary>
    CopperJoinedWithoutLine,

    // ── recognition's check on itself: the circuit against the copper (CircuitCopperCheck) ─────────────

    /// <summary>Two attachments the copper joins and the circuit does not.</summary>
    CircuitSplitsCopper,

    /// <summary>Two attachments the circuit joins on islands the copper keeps apart.</summary>
    CircuitJoinsAcrossCopper,

    /// <summary>The circuit joins what the copper joins. Said once, with what was checked.</summary>
    CircuitAgreesWithCopper,

    // ── AS-6: the circuit, the drawing and the target (R-as6-2 … R-as6-6) ────────────────────────

    /// <summary>What the circuit could not carry: a part with too few terminals measured, a via that joins nothing
    /// the circuit has.</summary>
    EmitOmissions,

    /// <summary>Lines and vias left out because the copper they model reaches no port and no part (round 15).</summary>
    StrayLines,

    /// <summary>What the drawing does not carry or carries differently (<c>NetlistSchematic.Build</c>'s notes).</summary>
    DrawingNotes,

    /// <summary>Instances drawn by the automatic arrangement because, where the board puts them, they would land on
    /// another (R-dfr22-2-2).</summary>
    BoardPlacementFallbacks,

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

    /// <summary>The corrections made by hand, and any refused (brief-dfr29-1 R-dfr29-1-1).</summary>
    ImageCorrections,

    /// <summary>The picture draws copper as outlines, so none is read from its lines (brief-dfr30-1 R-dfr30-1-7).</summary>
    ImageLineArt,

    // ── IM-13: a photograph of a board (R-im13-5) ─────────────────────────────────────────────────

    /// <summary>The photo was put square on four corners: the rectangle, and the residual at each corner in µm.</summary>
    ImagePhotoRectified,

    /// <summary>Specular highlights filled from their neighbourhood before tracing.</summary>
    ImagePhotoHighlights,

    /// <summary>Part bodies covering their pads, reported rather than read.</summary>
    ImagePhotoUnreadParts,

    /// <summary>Photographed widths are not good enough for impedance. Said once.</summary>
    ImagePhotoWidths,

    // ── IM-4: a layout picture made into a schematic ──────────────────────────────────────────────

    /// <summary>The stackup's reference plane is drawn nowhere in the picture and was implied (R-im4-3). Said once.</summary>
    ImageReferenceImplied,

    /// <summary>The picture draws no board outline, so its traced frame was read as the board's edge. Said once.</summary>
    ImageFrameIsBoardEdge,

    /// <summary>Text read from the picture's silkscreen strokes (R-im4-4).</summary>
    ImageSilkscreenRead,

    /// <summary>The picture's silkscreen strokes are too thin to read; nothing was read from them.</summary>
    ImageSilkscreenTooThin,

    // ── IM-10: a schematic picture made into a schematic (R-im10-10) ───────────────────────────────

    /// <summary>What the line work read (IM-7): one finding per line of its report.</summary>
    PictureLineWork,

    /// <summary>What the symbols were named (IM-8).</summary>
    PictureSymbols,

    /// <summary>What the words said (IM-9).</summary>
    PictureWords,

    /// <summary>The nets made, and what joined them.</summary>
    PictureNets,

    /// <summary>The ports found, and how.</summary>
    PicturePorts,

    /// <summary>No port: the schematic is written without its S-parameter analysis (D13).</summary>
    PictureNoPort,

    /// <summary>Supply nets with no source, left as named nets.</summary>
    PictureSupplyNets,

    /// <summary>Unknown values made global variables with tune entries.</summary>
    PictureVariables,

    /// <summary>Variables renamed because the schematic drawn into already has them.</summary>
    PictureVariablesRenamed,

    /// <summary>Instances drawn beside the picture rather than on it.</summary>
    PicturePlacementFallbacks,

    /// <summary>The user's crossing and wire-end corrections.</summary>
    PictureOverrides,

    /// <summary>What the emit could not write, or wrote differently.</summary>
    PictureEmitNotes,

    /// <summary>Why the picture is not under the drawing.</summary>
    PictureUnderlay,

    /// <summary>Which of the candidate readings was kept, and the runner-up (IMF-9, R-imf9-2).</summary>
    PictureReading,
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

    /// <summary><see cref="Add"/>, replacing any finding of <paramref name="cls"/> already made — for what is said of the
    /// emit and the drawing, which a recognition reused by Create (R-dfr22-2-8) has already been through once.</summary>
    internal void Set(RecognitionFindingClass cls, int count, string sentence, IReadOnlyList<RecognitionAnchor>? anchors = null)
    {
        _findings.RemoveAll(f => f.Class == cls);
        Add(cls, count, sentence, anchors);
    }

    /// <summary>One line per class — the CLI's stderr form.</summary>
    public IEnumerable<string> Lines() => _findings.Select(f => f.Sentence);
}

using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// The shape of one of circuitRF's JSON documents, GENERATED from the type the reader deserialises
/// into (AUT-10 R-aut10-3, R-aut10-4).
///
/// <para><b>Why generated rather than written.</b> Two of the formats a client must be able to
/// author have no page at all: an exercise that had to plot a result succeeded only because an
/// unrelated <c>.cdd</c> happened to exist on the machine to copy from, and a client in a fresh
/// workspace has nothing. A hand-written description of a 60-field DTO tree is a second copy of the
/// reader that goes stale silently — the exact failure this whole surface exists to remove — so the
/// page is a walk over the reader's OWN types, with every default read off a freshly-constructed
/// instance rather than transcribed.</para>
///
/// <para><b>What it deliberately does not claim.</b> It states the field names, their types, their
/// defaults and every legal member of an enum. It does not state what a field MEANS: where the type
/// carries no summary there is nothing here to read one from, and an invented meaning is worse than
/// none (R-aut6-8's rule, applied to a format instead of to a parameter). The prose chapter beside
/// each format answers that half.</para>
///
/// <para><b>Descent stops at the declaring assembly.</b> A property whose type lives elsewhere is
/// named and not expanded, because following it reaches the whole result model. Enums are the
/// exception and are expanded wherever they come from: their members ARE the answer, and a client
/// that cannot spell one writes a file the reader refuses.</para>
/// </summary>
internal static class DocumentSchema
{
    /// <summary>One JSON document format this reference can describe.</summary>
    /// <param name="Topic">The reference topic name.</param>
    /// <param name="Title">Its title in the topic list.</param>
    /// <param name="Extension">The file extension it describes.</param>
    /// <param name="Root">The type the reader deserialises the whole file into.</param>
    /// <param name="Preamble">The authored half: what the format is FOR and a minimal working
    /// example. Everything after it is generated.</param>
    /// <param name="More">Further types walked after the root — the blocks a document carries as raw JSON (a
    /// <c>.cmc</c>'s <c>Network</c> and step settings), which the root's own properties cannot reach.</param>
    /// <remarks>A null <paramref name="Root"/> is a format whose JSON is written field by field in a shape its types do not
    /// mirror: its page is the authored preamble alone, never a walk that would name fields the file does not have. A
    /// <c>.cmodel</c>'s <c>model.json</c> is such a format, and it is served whole as the AUTHORED topic <c>cmodel</c>
    /// (<see cref="CircuitRF.Design.Reference.ReferenceLibrary"/>) rather than from here, because a file a script writes
    /// from scratch needs the whole page — its arrays, its knots, its signs — and not a field table alone.</remarks>
    /// <param name="Descend">Further assemblies whose types the walk expands — for a format whose reader deserialises
    /// straight into another project's model. A <c>.csch</c>'s tuning block is <c>src/Core</c>'s <c>TuningSetup</c>
    /// types, and a page that named <c>TunableEntry</c> without expanding it sent an author to the source
    /// (EMB-5 R-emb5-12).</param>
    internal sealed record Format(string Topic, string Title, string Extension, Type? Root, string Preamble, Type[]? More = null,
                                  System.Reflection.Assembly[]? Descend = null);

    // ── the formats ──────────────────────────────────────────────────────────

    /// <summary>
    /// The formats served as generated topics. All are here for the same reason: they are documents
    /// a client has to write and that <c>create</c> does not make (or makes only empty). The
    /// <c>.clay</c>/<c>.cem</c>/<c>.wBond</c> three are what EM and wirebond work needs headlessly,
    /// and docs/design/em-3d.md points at them as the authoring surface a 3D setup extends.
    /// </summary>
    public static readonly Format[] All =
    [
        new("data-display", "The .cdd data-display format", ".cdd",
            typeof(CircuitRF.Render.DataDisplay.DataDisplayConfig), CddPreamble),
        new("technology", "The .ctech technology format", ".ctech",
            typeof(CircuitRF.Design.Layout.CtechFile), CtechPreamble),
        new("layout", "The .clay layout format", ".clay",
            typeof(CircuitRF.Design.Layout.ClayFile), ClayPreamble),
        new("em-setup", "The .cem EM setup format", ".cem",
            typeof(CircuitRF.Design.Layout.Em.CemFile), CemPreamble),
        new("wbond", "The .wBond wirebond format", ".wBond",
            typeof(CircuitRF.WBond.WBondIo.WBondDocument), WBondPreamble),
        new("3d-view", "The .c3d 3D view format", ".c3d",
            typeof(CircuitRF.Design.ThreeD.C3dDocument),
            // brief-em3d-92 — the cap is C3dTransparency.Max's, never a second literal.
            // brief-em3d-109 — the field indicator's labels are RealisticLook's, never a second spelling.
            C3dPreamble.Replace("{TransparencyMax}", CircuitRF.Design.ThreeD.C3dTransparency.Max.ToString(System.Globalization.CultureInfo.InvariantCulture))
                       .Replace("{LitFields}", CircuitRF.Render.Scene3D.Look.RealisticLook.FieldIndicatorText(lit: true, blended: false))
                       .Replace("{BlendedFields}", CircuitRF.Render.Scene3D.Look.RealisticLook.FieldIndicatorText(lit: false, blended: true))),
        new("materials", "The .cmat material library format", ".cmat",
            typeof(CircuitRF.Design.Layout.CmatFile), CmatPreamble),
        // The drawn circuit. A client that wanted a schematic a person could read had nothing but the
        // reader to go on: it found the type key, the parameter shape, the wire shape and every pin
        // offset by trial — the last by measuring rendered pixels — and a wrong key made a
        // component a resistor without a word (SchematicLoadAudit now says so).
        new("schematic", "The .csch schematic format", ".csch",
            typeof(CircuitRF.Design.Schematic.CschFile), CschPreamble,
            Descend: [typeof(CircuitRF.Core.Design.TuningSetup).Assembly]),
        // The Model Composer's document (brief-modelcomposer-13 R-mc13-6). Its field table is the authored file-formats
        // page's own section, served from the embedded page rather than copied, so the reference and the manual cannot
        // disagree, and the walk of its step settings adds every default. The .cmodel it exports is the authored topic
        // `cmodel`.
        new("cmc", "The .cmc Model Composer document", ".cmc",
            typeof(CircuitRF.Design.Modeling.CmcDocument), ModelPreamble("cmc", CmcIntro),
            [typeof(CircuitRF.Design.Modeling.PadsSettings), typeof(CircuitRF.Design.Modeling.NetworkSettings),
             typeof(CircuitRF.Design.Modeling.CurrentFitSettings), typeof(CircuitRF.Design.Modeling.ChargeFitSettings),
             typeof(CircuitRF.Design.Modeling.ThermalFitSettings), typeof(CircuitRF.Design.Modeling.TrapFitSettings),
             typeof(CircuitRF.Design.Modeling.ValidationSettings), typeof(CircuitRF.Design.Modeling.ReportOptions),
             typeof(CircuitRF.Design.Modeling.Passive.PassiveStepSettings)]),
    ];

    private const string CmcIntro = """
        A .cmc is written by `circuitrf model` (new, import, fit, export, …) and by the Model Composer window; edit it
        as JSON to change a step's settings, then run the step again. `Pads` is a PadsSettings, `Network` a
        NetworkSettings, and each `Steps.<Step>.Settings` the settings type of that step (Current: CurrentFitSettings,
        Charge: ChargeFitSettings, Thermal: ThermalFitSettings, Traps: TrapFitSettings, Validate: ValidationSettings,
        Report: ReportOptions, Passive: PassiveStepSettings); every field and its default follows the manual's table.
        The data a data set is imported from — what a model needs, what to run in another simulator, and every file
        format with an example — is `reference model-data`; `model import --dry-run` checks a file and writes nothing.
        """;

    /// <summary>A preamble whose field table is the file-formats page's section <c>{#anchor}</c>, up to the next
    /// heading of its level.</summary>
    private static string ModelPreamble(string anchor, string intro)
    {
        string page = CircuitRF.Design.Reference.ReferenceLibrary.Find("file-formats") is { } t
            ? CircuitRF.Design.Reference.ReferenceLibrary.Read(t) : "";
        var lines = page.Split('\n');
        int start = Array.FindIndex(lines, l => l.StartsWith("## ", StringComparison.Ordinal) && l.Contains("{#" + anchor + "}"));
        if (start < 0) return intro;
        int end = Array.FindIndex(lines, start + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
        var section = lines[(start + 1)..(end < 0 ? lines.Length : end)];
        return intro.TrimEnd() + "\n\n" + string.Join('\n', section).Trim();
    }

    public static Format? Find(string topic)
        => All.FirstOrDefault(f => string.Equals(f.Topic, topic, StringComparison.OrdinalIgnoreCase));

    private const string CddPreamble = """
        A data display is a document, like a schematic. It is JSON, it is what `render` draws when
        given a .cdd, and this is how to write one.

        For ONE plot you do not need to write one at all: `circuitrf plot <result> -o out.svg
        --trace cube=S,i=2,j=1,y=db20` draws it, and `--write-cdd out.cdd` hands you the document it
        built — which is a correct starting point to edit rather than a blank page.

        A display holds tabs; a tab holds plots; a plot holds traces; a trace names a cube in a
        result file and how to slice it. The result files themselves are NOT in the document: the
        `SourceAliases` map names them, and `render --data <file>` binds one at draw time. A .cdd
        whose sources do not resolve is a refusal, not an empty plot.

        This is a whole working display — one tab, one plot, |S21| in dB against frequency. It was
        written out, rendered and checked, not composed from the field list below:

            {
              "FormatVersion": 2,
              "SelectedDataSource": "pad.npy",
              "SourceAliases": { "pad.npy": "pad" },
              "ActiveTabIndex": 0,
              "Tabs": [
                {
                  "Name": "Tab 1",
                  "Plots": [
                    {
                      "Left": 0, "Top": 0, "Width": 800, "Height": 600,
                      "PlotType": "Rect",
                      "Traces": [
                        { "SourcePath": "pad.npy", "Row": 1, "Col": 0,
                          "MatrixType": "S", "YAxis": "Db" }
                      ]
                    }
                  ]
                }
              ]
            }

            circuitrf render pad.cdd --data pad.npy -o pad.svg

        Four things that are not obvious from the field list:

          * FormatVersion must be 2. A file saying anything else is refused, by design — there is
            no back-compatibility path in this format, and a silently migrated file is worse than
            a refusal.
          * A trace is NETWORK-bound when CubeName is null: Row and Col are ZERO-BASED, so S21 is
            Row 1, Col 0, and MatrixType picks S, Z or Y. It is CUBE-bound when CubeName names a
            cube in the source, and CubeSlice then pins or keeps each of that cube's axes. The two
            are not mixed, and a non-null Expression supersedes both.
          * Plot geometry is in logical pixels on a free canvas, not a grid. Left/Top/Width/Height
            are the plot's rectangle inside the tab, and a plot of zero width draws nothing.
          * SourcePath is the source's key in SourceAliases, not necessarily a path on disk —
            relative to the workspace's results root where the file lives under it, absolute
            otherwise. `render --data` supplies the actual file.

        Every field the reader understands follows, with its default. A field this list does not
        name is IGNORED by the reader rather than refused, so check a spelling here before writing
        it: System.Text.Json matches names case-insensitively and drops what it does not know.
        """;

    private const string CtechPreamble = """
        A technology says what a layout's shapes are MADE of: the drawing layers and their colours,
        the default grid and via sizes, the physical stackup an EM run solves, and the design rules
        `check` enforces. It is JSON, one file, and `new workspace` copies a shipped one into the
        workspace it creates — `--tech` names which, and an unknown name is a refusal LISTING the
        shipped ids, which is how to find out what they are. The copy lands in <workspace>/tech/.

        Writing one from scratch is rarely the right move: start from the copy in a workspace and
        change it. What a caller usually needs from this page is which field to change, and what a
        layer's Key means — the (Layer, Datatype) pair is the GDSII number pair, and it is the
        identity a shape stores. Renaming a layer keeps its shapes; renumbering it does not. Whether a
        layer is shown or selectable is not in a technology: that belongs to each layout's own
        Layers list, per user, and a `Visible` or `Selectable` key is ignored on read.

        A minimal technology with one drawing layer. This is a whole file, and `check` passes it:

            {
              "FormatVersion": 1,
              "Name": "One layer",
              "DefaultDisplayUnit": "Um",
              "DefaultSnapDbu": 5,
              "Layers": [
                {
                  "Key": { "Layer": 1, "Datatype": 0 },
                  "Name": "Metal1",
                  "Color": { "r": 224, "g": 176, "b": 64, "a": 255 },
                  "ZOrder": 8,
                  "Purpose": "drawing"
                }
              ]
            }

        A DBU is circuitRF's integer layout unit; the workspace's own DbuPerUm decides what one is
        worth, so DefaultSnapDbu is not a length until that is known. An EM run needs the Stackup
        as well as the layers — a technology with layers and no stackup draws correctly and cannot
        be solved.

        DeviceRules and Constants are the geometric device-recognition deck, which `lvs --recognize`
        reads and nothing else does. It is for artwork carrying no instances: a rule's Body is a
        layer expression whose connected components are candidate devices, its Terminals are the
        layers a terminal may be on, and its Parameters are formulas over Length, Width, Area and
        Perimeter — all SI — plus the Constants this file declares, plus two the process supplies:
        TfrSheetResistance (the sheet resistance a drawing layer states, SheetResistanceOhmPerSq)
        and MimCapDensity (ε0·εr/t of the stackup's capacitor dielectric). A Constants entry of
        the same name overrides either. A technology stating no DeviceRules
        recognises nothing, which is the ordinary case and is not an error. `check` validates the
        deck, so a rule that will not read is refused before any run reads it.

        Two flags complete a rule. CopperBody true says the body is metal that IS the device — a
        microstrip line, a spiral, a via barrel: its copper leaves the connectivity reading, so the
        metal either side of it is two nets rather than one (a line read as plain copper would short
        its two ends). GroundTerminal true adds the ground reference as the device's last terminal,
        for a via to an undrawn backside plane. A recognised device whose body shapes carry a
        Component (see `reference layout`) takes that name as its designator and is matched to the
        schematic part of the same name; with none, it is matched on structure alone.

        The shipped mmic-GaAs technology carries a deck: a thin-film resistor is Resistor not covered
        by Metal1; a MIM capacitor is MIM Metal over Nitride over Metal1; a backside via is a Backside
        Via shape, with Metal1 at one end and ground at the other; a microstrip line is Metal1 (or
        Metal2) under a Line Marker; a spiral is the metal under an Inductor Marker. The two markers
        are drawn-only layers that exist for recognition — a line is otherwise indistinguishable from
        any other copper. `reference technologies mmic-GaAs_2LM_100um` lists every rule with its keys.

        Every field the reader understands follows, with its default.
        """;

    private const string ClayPreamble = """
        A layout is a cell's physical artwork: shapes on drawing layers, instances of other cells,
        and the EM ports a .cem solves between. It is JSON, one file per cell, at
        <cell>/layout/<cell>.clay. `new cell <workspace> <name> --views layout` writes an empty one
        whose DbuPerMicron, DisplayUnit and SnapDbu come from the workspace's technology; start from
        that rather than a blank file.

        A 50 ohm microstrip through-line on the shipped pcb-2layer_RO4350B_20mil_1oz technology,
        10 mm long and 1.1 mm wide, with an EM port at each end. This is a whole file; `check`
        passes it, `render` draws it, and the em-setup topic's example solves it:

            {
              "FormatVersion": 1,
              "DbuPerMicron": 1000,
              "DisplayUnit": "Mm",
              "SnapDbu": 10000,
              "Shapes": [
                { "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 },
                  "X1": 0, "Y1": -550000, "X2": 10000000, "Y2": 550000 },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 },
                  "X": 0, "Y": 0, "Text": "1", "Height": 400000,
                  "IsPort": true, "PortDirection": "R0" },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 },
                  "X": 10000000, "Y": 0, "Text": "2", "Height": 400000,
                  "IsPort": true, "PortDirection": "R180" }
              ],
              "Instances": []
            }

            circuitrf check  ws/thru/layout/thru.clay
            circuitrf render ws/thru/layout/thru.clay -o thru.svg

        Eight things that are not obvious from the field list:

          * Every coordinate and size is an integer DBU. DbuPerMicron says what one is worth: at the
            default 1000, one DBU is one nanometre, so 1.1 mm is 1100000. y is UP.
          * A shape's Layer is the (Layer, Datatype) Key of a drawing layer in the technology, never
            its name. `reference technology` explains the Key; the workspace's own .ctech lists them.
          * "$type" picks the kind of shape and MUST be the first field of each shape. Written
            anywhere else, the whole file is refused with "must specify a type discriminator".
          * Poly, Curve and Path store their vertices as ONE flat list, x0, y0, x1, y1, ... A Poly's
            Holes are further flat lists of the same kind.
          * An EM port is a Label with IsPort true, placed ON the conductor's edge. Its Text is the
            port number. PortDirection is the way current flows INTO the metal (R0 = +x, R90 = +y,
            R180 = -x, R270 = -y); omitted, it is inferred from the geometry, and an ambiguous
            inference is refused at run time rather than guessed.
          * Net, Component and Pin on a shape are optional labels, and none of them changes what is
            connected — copper is connected by touching. Net stamps a net NAME on the copper the shape
            belongs to: `lvs` reads it as a name to match against the schematic's net of that name,
            and reports it when the copper disagrees, but never obeys it. Component names the part a
            shape belongs to: `lvs --recognize` gives a device it recognises in plain artwork the
            Component its body shapes state, so it is matched to the schematic part of that name.
            Pin names the pad of that part (an imported Gerber's %TO.P sets both); inside a cell it
            marks the pad a LayoutPin of that name sits on. A placed cell's own pins are its
            LayoutPins, not these fields.
          * DisplayUnit and SnapDbu are editor conveniences. Nothing is computed from them.
          * ImpedanceReview and ImpedanceAcceptances are the Impedance Analysis's review state: the
            settings and scope last used, and the findings a designer accepted. Both are omitted when
            unset, and `impedance` applies both. An acceptance's Key is
            "<layer name>|<x>,<y>|<x>,<y>|<Kind>": the trace's two end points in whole um (the
            start and end `impedance --json` reports for it, rounded), the lower one by x then y
            first, and the finding's Kind. Moving the trace stops it matching. Reason is required.
            For OutOfTolerance, WorstOhms is the trace's worst Z0 when it was accepted; a run that
            finds it further from target counts the finding again.
          * Review and ReviewAcceptances are the Layout Review's state (`review`). Review holds
            Disabled, the check ids turned off, and Thresholds, keyed "<check>.<threshold>" with a
            length in DBU or a plain number; only values that differ from the default are written, and
            `review --list-checks` lists every check and threshold. A review acceptance's Key is
            "<check>|<layer>/<datatype>|<minX>,<minY>,<maxX>,<maxY>": the check id, the finding's
            layer and its marker's exact bounding box in DBU, as `review --json` reports them. Moving
            the marked copper stops it matching. Reason is required.

        Every field the reader understands follows, with its default. Shapes are listed once per
        kind, each with the "$type" value that selects it.
        """;

    private const string CemPreamble = """
        An EM setup is one electromagnetic run: which layout, which conductor of the stackup carries
        the signal, the frequency plan, the ports' reference impedances and the mesh. It is JSON; by
        convention it lives at <cell>/em/<name>.cem. `circuitrf em <file.cem>` runs it with no other
        argument and writes exactly what the GUI's Simulate writes: <workspace>/results/<Name>.sNp
        and <Name>_em.npy (`-o` moves the Touchstone only).

        This solves the layout topic's microstrip through-line, 1 to 10 GHz at 4 points, in about
        5 seconds on a laptop; |S11| comes back below -35 dB. It is a whole file:

            {
              "FormatVersion": 1,
              "Name": "thru",
              "LayoutRef": "thru/layout/thru.clay",
              "SignalStackupLayerName": "Top Copper (1 oz)",
              "Frequency": {
                "StartExpr": "1", "StopExpr": "10", "NumPoints": 4,
                "Mode": "PointCount", "Kind": "Linear",
                "StartUnit": "GHz", "StopUnit": "GHz"
              },
              "DispersionCorrection": true,
              "AnalysisKind": "Planar"
            }

            circuitrf check ws
            circuitrf em    ws/thru/em/thru.cem

        Six things that are not obvious from the field list:

          * LayoutRef names the geometry document: a .clay or a .c3d. It is relative to the
            WORKSPACE folder (the one holding .cws), not to the .cem. The technology is the
            geometry's own, resolved from its workspace; a .cem names none. A .c3d is solved by the
            3D solvers only (Solver3D Palace or OpenEms), and its own embedded Setups are not read.
          * SignalStackupLayerName is a STACKUP entry's Name ("Top Copper (1 oz)"), not a drawing
            layer's ("Top Copper"). The technology's Stackup lists them.
          * Frequency: StartExpr and StopExpr are expressions in StartUnit and StopUnit. Mode
            PointCount uses NumPoints; Mode StepSize uses StepExpr in StepUnit. AdditionalFrequencies
            is a list of further segments in the same shape; the run solves every segment's
            frequencies together, sorted, each once. A single frequency is a segment with NumPoints 1
            and StopExpr equal to StartExpr.
          * Port impedances: Port1Z0Real/Imag and Port2Z0Real/Imag cover two ports. For more, PortZ0s
            is a flat [re, im, re, im, ...] list in port-number order, and wins where present.
          * AnalysisKind: Planar is the full-wave planar solver; CrossSection is the 2-D per-unit-
            length solve of a uniform line; omitted means Auto, which picks between them and says
            which in the run's notes.
          * DispersionCorrection: a new setup made in the GUI writes true, but a file that OMITS it
            reads false. Write it. Every other omitted flag reads as the GUI's default.

        Every field the reader understands follows, with its default.
        """;

    private const string WBondPreamble = """
        A wirebond design is a set of named ARRAYS of bond wires, each wire a 3-D polyline. It is
        JSON, self-contained, and what the wBond editor saves. A schematic's wBond component usually
        CARRIES its wires embedded in its Design parameter; a hand-written netlist names a .wBond
        file instead.

        Two 1 mil gold wires, 1 mm span, 100 um apart, feet 10 mil above the ground plane and crests
        at 16 mil. This is a whole file:

            {
              "FormatVersion": 1,
              "Arrays": [
                {
                  "Name": "G1",
                  "Wires": [
                    { "DiameterNm": 25400, "Material": "Gold",
                      "Points": [ [0, 0, 254000], [254000, 0, 406400], [1016000, 0, 254000] ] },
                    { "DiameterNm": 25400, "Material": "Gold",
                      "Points": [ [0, 101600, 254000], [254000, 101600, 406400], [1016000, 101600, 254000] ] }
                  ]
                }
              ]
            }

        Simulated through a netlist (`reference components WBond` lists every parameter):

            Port:P1 in  0 Num=1 Z=50 Ohm
            Port:P2 out 0 Num=2 Z=50 Ohm
            WBond:WB1 in out File="pair.wBond"
            analysis SP1 type=sparam start=1 stop=10 npts=4 Unit=GHz

        Six things that are not obvious from the field list:

          * Points are [x, y, z] in integer NANOMETRES, whatever unit the editor displays; z is
            measured from the ground plane at z = 0. Points[0] is where current ENTERS the wire,
            and reversing the list changes the sign of every mutual inductance it takes part in.
          * EVERY array in the file is two nets on the WBond instance, its input end then its output
            end, in the file's array order. The instance's Arrays parameter ("G1|G2") only RECORDS
            the order it was wired against, so a reordered file is reported rather than rewired.
          * Material names a metal: Gold, Aluminium, Copper and Silver are built in. A Materials list
            REPLACES the built-in metals rather than adding to them, so a file that defines one
            custom metal must also list every built-in one its wires name.
          * A relative File= resolves against the .cnl's own folder, exactly as an SnP's does, so
            the netlist above expects pair.wBond beside it.
          * Omitted fields take the editor's defaults: OperatingTempC 125 (degrees C), ground plane
            on, capacitance on, OvermoldEr 1 (air). An OperatingTempC of exactly 85 is read as 125:
            85 was the default every file stated until 2026-10.
          * EmbeddedGeometry and ViewState are the editor's own; leave them out when writing a file.

        Every field the reader understands follows, with its default.
        """;

    private const string CschPreamble = """
        A schematic is the drawn circuit: placed components, the wires between their pins, and the
        analyses that run it. It is JSON. `netlist` shows the .cnl it extracts to, which is what every
        run verb simulates, and `check` reports anything the reader ignored or misread.

        A 25 ohm series resistor between two 50 ohm ports, its value from a VAR block, swept 0.1 to
        6 GHz, with one measurement. This is a whole file:

            {
              "FormatVersion": 2,
              "CellName": "pad",
              "Components": [
                { "InstanceName": "VAR1", "Symbol": "Var", "X": -600, "Y": -800,
                  "Parameters": [ { "Name": "Rs", "Expression": "25" } ] },
                { "InstanceName": "P1", "Symbol": "TermG", "X": -600, "Y": 400,
                  "Parameters": [ { "Name": "Num", "Expression": "1" }, { "Name": "Z", "Expression": "50" } ] },
                { "InstanceName": "P2", "Symbol": "TermG", "X": 600, "Y": 400,
                  "Parameters": [ { "Name": "Num", "Expression": "2" }, { "Name": "Z", "Expression": "50" } ] },
                { "InstanceName": "R1", "Symbol": "Resistor", "X": 0, "Y": 0, "Rotation": "R90",
                  "Parameters": [ { "Name": "R", "Expression": "Rs" } ] },
                { "InstanceName": "MEAS1", "Symbol": "Meas", "X": -600, "Y": 1000,
                  "Parameters": [ { "Name": "S21_dB", "Expression": "dB(SP1.S(2,1))" } ] }
              ],
              "Wires": [
                { "Points": [ [-600, 200], [-600, 0], [-200, 0] ] },
                { "Points": [ [600, 200], [600, 0], [200, 0] ] }
              ],
              "Analyses": [
                { "Type": "sp", "Name": "SP1",
                  "Sweeps": [ { "StartExpr": "0.1", "StopExpr": "6", "StartUnit": "GHz", "StopUnit": "GHz",
                                "Mode": "PointCount", "NumPoints": 60, "Kind": "Linear" } ] }
              ]
            }

        It extracts to:

            Rs = 25
            Port:P1  n1  0  Num=1  Z=50
            Port:P2  n2  0  Num=2  Z=50
            R:R1  n2  n1  R=Rs
            analysis SP1 type=sparam start="0.1" startUnit=GHz stop="6" stopUnit=GHz npts=60
            measure S21_dB = dB(SP1.S(2,1))

        Nine things that are not obvious from the field list:

          * "Symbol" is the component's type, spelled as a SYMBOL KIND — Resistor, Capacitor, Mlin,
            TermG, Var, Meas — which is not always the .cnl token (TermG and Term both write Port).
            `reference components` accepts either spelling and shows each token's kinds. A component with no Symbol is
            read as a Resistor, so `check` reports it as an error; no other key names the type.
          * Parameters are a LIST of { "Name", "Expression" } — the expression is text in the
            expression language ("30um", "Rs", "1/(w0^2*C)"). An object map is not a list: the
            component cannot be read and becomes a placeholder. "Value" holds a JSON DOCUMENT for
            the parameters that carry one; for an ordinary value write Expression.
          * A VAR block's variables are its Parameters rows, one per variable; a Meas block's
            measurements are its rows, Name = expression. Neither has pins. The top-level
            Measurements list is not evaluated by a run — use a Meas block.
          * Coordinates are schematic units on a 100-unit grid, y growing DOWNWARD. A wire is a list
            of [x, y] points. A pin connects only where a wire has a POINT exactly on it — never to
            the middle of a segment. Two wires connect where they share a point, where an end of one
            lies on the other (a T), or where they cross at a Dot; a plain crossing is not a
            connection. NetLabels with the same Name are one net.
          * Pin positions are relative to the component's X, Y at Rotation R0 with no mirror —
            `reference components <TYPE>` lists them as "pins at R0". Rotation R90 maps a pin at
            (x, y) to (-y, x); R180 to (-x, -y); R270 to (y, -x). MirrorX negates x first.
            The resistor above is vertical at R0, pins (0, -200) and (0, 200); at R90 they land on
            (200, 0) and (-200, 0), where the wires end.
          * An analysis is an entry in Analyses with a lower-case Type — dc, sp, hb, sweep, lp or
            lpp. Its fields are the ones listed below for an analysis; an entry the reader cannot
            build is dropped, and `check` says why. `reference analyses` describes the same
            settings as .cnl keys.
          * A port is a TermG (one pin, reference grounded) or a Term (two pins, + and -), with Num
            and Z. Ground is a component too: Symbol "Ground", one pin at (0, 0).
          * A design that belongs to a workspace resolves its technology, and a microstrip or via
            part takes its substrate from it at extraction — `netlist` shows the values it chose.
          * "Tuning" is the same model as a .cnl's tune, goal and optimize lines (`reference
            tuning`, `goals`, `optimizers`, `statistics`): Variables are tune lines, Goals goal
            lines, Optimizer the optimize line. Enums are written by NAME — a goal's Type is Le, Ge,
            Eq, In or Out against Limit (and UpperLimit for In/Out); a Distribution is Gauss, Unif,
            LogNorm or Discrete. Every Spread field is a STRING ("3", "2%"), never a number. A vendor
            part search is a Variables entry on the part's Part key with its query in Parts. This
            block is a working one — a part search, a toleranced resistor and one goal:

                "Tuning": {
                  "Variables": [
                    { "Key": "C1.Part", "Opt": true, "Parts": "0603 C0G 50V" },
                    { "Key": "R1.R", "Tune": true, "Opt": true, "Min": "10 Ohm", "Max": "200 Ohm",
                      "Stat": true, "Distribution": "Gauss", "Spread": { "Tol": "2%", "Sigmas": "3" } }
                  ],
                  "Goals": [
                    { "Name": "Match", "Expression": "dB(SP1.S(1,1))", "Analysis": "SP1",
                      "Range": { "Axis": "freq", "Lo": "1 GHz", "Hi": "2 GHz" },
                      "Type": "Le", "Limit": "-20" }
                  ],
                  "Optimizer": { "Algorithm": "auto" }
                }

        Every field the reader understands follows, with its default.
        """;

    private const string CmatPreamble = """
        A material library is a list of named materials a technology can use, in a file of its own so
        several technologies can share it. It is JSON with two keys that are always written — an EMPTY
        library is this, and nothing more — and an optional third, ThermalInterfaces:

            { "FormatVersion": 1, "Materials": [] }

        A library with two materials, one a dielectric and one a metal:

            {
              "FormatVersion": 1,
              "Materials": [
                { "Name": "Mould compound", "Epsr": 3.9, "TanD": 0.008, "Mur": 1,
                  "Source": "representative epoxy moulding compound, 1 MHz" },
                { "Name": "Plated gold", "Sigma20": 3.3e7, "Alpha20": 0.0034 }
              ]
            }

        A record here is EXACTLY a record of a .ctech's own Materials list — one type, one reader — so
        one cut from either file and pasted into the other reads identically.

        Seven things that are not obvious from the field list:

          * ONLY A TECHNOLOGY NAMES A LIBRARY. A .ctech lists it in MaterialLibraries, as a path
            relative to the .ctech's own directory; a workspace, a .cem and a .c3d never do. They
            reach a library through the technology they already resolve, which is what makes a
            technology resolve identically everywhere it is used. There is no verb that adds one:
            write the path into the .ctech.
          * A name defined by the technology and by a library (or by two libraries) with DIFFERENT
            values is an error, and the technology refuses to load until it is fixed — nothing is
            shadowed. Equal values are one material. A library that cannot be read is also a
            refusal, naming the file, never an empty list. `check` on the .ctech reports both
            (`material.conflict`, `material.library-unreadable`).
          * Null means NOT STATED. Omit a key rather than writing 0 or 1: a material stating only
            Sigma20 is a conductor, one stating only Epsr a dielectric, both is ambiguous and a 3D
            object made of it must state its own Role. A material named Air is air.
          * '@' may not appear in a Name: `Name@technology` is how a 3D view tells two technologies'
            same-name materials apart. Keys this build does not know are kept on save.
          * A thermal run reads ThermalK (W/(m·K)), or ThermalKVsTemp when stated — THE TABLE WINS —
            and σ(T) from SigmaVsTemp when stated, else Sigma20 / (1 + Alpha20·(T − 20)). A table is
            linear between its (TempC, Value) points and held at its ends beyond them. Every EM
            solver still reads Sigma20. `check` warns when a table and its constant disagree at 20 °C
            by more than 1 %. A solid a thermal run meshes must state ThermalK or ThermalKVsTemp.
          * ThermalKTensor [xx, yy, zz] (W/(m·K) at 25 °C, along the 3D view's axes) makes k
            anisotropic, as EpsrTensor does εr: the volume solve reads it instead of ThermalK, and
            with a ThermalKVsTemp table each component follows the table's k(T)/k(25 °C). It refines
            the scalar, never replaces it — ThermalK or ThermalKVsTemp must still be stated, because
            a bond wire and an effective block have no direction. A rotated solid's tensor does not
            rotate. Omitted: isotropic, exactly as before.
          * ThermalInterfaces (here or in a .ctech) are thermal boundary resistances between two
            materials wherever they touch: { "MaterialA": "Gallium nitride", "MaterialB": ...,
            "ResistanceM2KW": 3.3e-8, "Source": ... }. The pair is unordered; the duplicate rule is
            the materials' own (`material.interface-conflict`).
          * ColeCole makes εr and σ follow frequency, as a body tissue's do: { "EpsInf": 4, "SigmaIonic":
            0.2, "Terms": [ { "DeltaEps": 50, "TauSeconds": 7.234e-12, "Alpha": 0.1 }, ... ] } (one to
            four terms; τ in SECONDS; 0 ≤ α < 1), giving ε̂(ω) = ε∞ + Σ Δε/(1 + (jωτ)^(1−α)) + σᵢ/(jωε₀).
            DielectricVsFrequency is the other form, target values at named frequencies —
            [ { "FrequencyHz": 835e6, "Epsr": 41.5, "SigmaSm": 0.90 }, ... ] in increasing frequency —
            interpolated linearly between rows and REFUSED outside them. A record states one or the
            other. A 3D run evaluates every such material ONCE, at the sweep's centre, before lowering
            (the run's notes name the frequency and the values), and the solver sees that εr and σ
            with no tanδ; `explain --materials` reports them. Keep Epsr and Sigma20 as the model's
            values at 1 GHz — what a build without the model reads; `check` warns when they disagree
            by more than 1 % (`tech.material.model-disagrees`). A material with either form is a
            dielectric: an object made of it needs no Role.
          * PerfusionRate (1/s, Pennes' blood perfusion) and MetabolicHeat (W/m³) are stored and
            reported for a later bioheat solver; nothing reads them yet.
          * Appearance is how the material LOOKS in the 3D view's realistic mode — glTF 2.0's
            metallic-roughness parameters, every one optional:
              BaseColor            #rrggbb (sRGB)  a metal's reflectance, a dielectric's body colour
              Metallic             0–1             1 for a metal, 0 for a dielectric
              Roughness            0–1             0 a mirror, 1 matte
              Transmission         0–1             see-through (glass, quartz, thin laminate)
              Ior                  1.0–3.0         the OPTICAL index — never derived from Epsr
              Clearcoat            0–1             a glossy layer over the body (solder mask)
              ClearcoatRoughness   0–1
              AttenuationColor     #rrggbb         the tint light picks up passing through
              AttenuationDistance  metres, > 0     how far it travels before taking that tint
              Like                 a material      that material's look first, these fields over it
            NOTHING PHYSICAL IS READ FROM IT and nothing physical is derived into it: no solver sees
            it, two records differing only in Appearance (or Color, or Source) are one material, and
            editing it never marks a result out of date. Unstated, a field takes the material's Like,
            then BaseColor its Color, then the role's default (a conductor metallic and fairly
            rough, a dielectric not). Out-of-range values and a Like cycle are errors in `check`; a
            Like naming no material is a warning, and falls through.

        circuitRF ships generic-materials.cmat — metals (with σ(T) and k(T) tables to below their
        melting points), ceramics, semiconductors, laminates, die attaches and package alloys, each
        record with its Source — and every shipped technology names it; `new workspace` copies it
        into tech/ beside the technology. `check <file.cmat>` checks a library on its own.
        circuitRF also ships tissue-materials.cmat — the body tissues of Gabriel, Lau and Gabriel
        (1996) as Cole-Cole models, head and body tissue-simulating liquids as target tables, a
        saline gel, and the tissue values openEMS's own SAR and MRI examples state — which no shipped
        technology names: the technology editor's Add Tissue Materials copies it beside a .ctech and
        names it ("MaterialLibraries": [ ..., "tissue-materials.cmat" ]).
        `reference shipped-materials` lists every material it holds, with its values;
        `reference technologies` lists which libraries each shipped technology names.

        To USE a shipped material, refer to it by Name: a technology that names the library (every
        shipped one does) can already use it. To DEFINE a new one, add a record to the .ctech's own
        Materials list, or to a .cmat of your own that you add to the .ctech's MaterialLibraries.
        Prefer that to editing the workspace's copy of generic-materials.cmat: a copy that no longer
        matches the shipped library is refused when circuitRF next offers to add it beside a technology.

        Every field the reader understands follows, with its default.
        """;

    private const string C3dPreamble = """
        A 3D view is a cell's solid model: boxes, prisms, cylinders, sheets and polyhedra, each made of
        a named material, plus instances of other cells. It is JSON, at <cell>/3d/<name>.c3d — the 3d
        folder is made with a cell's first 3D view. `new cell <workspace> <name> --views 3d` writes an
        empty one whose units come from the cell's layout when it has one and from the technology
        otherwise; start from that rather than a blank file. `em` solves one through an embedded setup
        (or a .cem whose LayoutRef names it), `explain` reports how it elaborates, and `render
        --section`/`--iso` draws it. Opening one in the application opens the 3D editor, which draws and
        edits it.

        A microstrip on the shipped pcb-2layer_RO4350B_20mil_1oz technology: a 5 mm square of 20 mil
        RO4350B with a 1.1 mm copper trace across its top. This is a whole file, and `check` passes it:

            {
                "FormatVersion": 1,
                "DbuPerMicron": 1000,
                "DisplayUnit": "Mil",
                "SnapDbu": 25400,
                "Objects": [
                    {
                        "$type": "Box",
                        "Name": "substrate",
                        "Material": "RO4350B",
                        "Min": [0, 0, 0],
                        "Size": [5000000, 5000000, 508000]
                    },
                    {
                        "$type": "Sheet",
                        "Name": "trace",
                        "Material": "Copper",
                        "Plane": "XY",
                        "Offset": 508000,
                        "Rect": {
                            "Min": [0, 1950000],
                            "Size": [5000000, 1100000]
                        },
                        "ThicknessUm": 35
                    }
                ]
            }

            circuitrf check ws/line/3d/line.c3d

        What is not obvious from the field list:

          * Every coordinate and size is an integer DBU; DbuPerMicron says what one is worth (at 1000,
            a nanometre). A point is [x, y, z]; a point on a drawing plane is [u, v]. z is up.
            DisplayUnit and SnapDbu are editor preferences — nothing is computed from them.
          * A drawing plane is named by the two axes it spans: XY is (u, v) = (x, y) with its normal
            along +z; YZ is (y, z) along +x; XZ is (x, z) along +y. Offset is the plane's position
            along that normal, and a prism's Height is measured along it — negative is allowed, and
            says which way the prism was pulled. Shear moves a prism's top against its bottom.
          * Dimensions are SIZES, never second corners: a box is Min and Size, a rectangle Min and
            Size, a cylinder Base, Axis, Length and Radius, a sphere Centre and Radius, an ellipsoid
            Centre and SemiAxes (along x, y and z of its own frame, so a placement's rotation turns
            it). A size is positive; a negative one is normalised on the next save by moving Min.
          * Placement: the object's own frame is mirrored (MirrorX negates its x), then rotated by
            each Rotate entry in list order ({"Axis": "Z", "Deg": 90}, right-handed), then moved to
            Origin. Omitted, it is the identity. A rotation lives here, never in the coordinates, so
            geometry stays integer. An instance's Placement is the same record.
          * Transparency is how see-through an object or an instance is DRAWN, a whole
            percentage from 0 (opaque) to {TransparencyMax}; omitted, its kind's default (a dielectric translucent,
            a conductor opaque). An instance's multiplies onto each part's own. An operation carries
            it for its result, never an operand inside it; a polyline has none. No solver reads it.
          * Appearance on an object or an instance overrides its material's appearance (the .cmat
            format's Appearance keys) FIELD BY FIELD: {"Like": "Gold"} draws a copper trace gold-
            plated, {"Roughness": 0.1} polishes it, and every field it does not state is the
            material's. An instance's applies to every part inside it, and a part's own stated field
            wins; of nested instances the innermost does. An operation carries it for its result; a
            polyline has none. Drawing only: no solver reads it and a run never sees it.
          * "Look" is how the 3D view's REALISTIC mode lights the scene — never a solver, and a run
            never sees it. Every key is optional and an omitted one is its default: Environment
            (Studio, HighKey, Dark, or a Radiance .hdr path relative to this file; an .exr is not
            read), Rotation (degrees about +z, default 30), Intensity (0 to 10, default 1), Exposure
            (EV, -10 to +10, default 0), Background (Theme, #rrggbb, "#rrggbb,#rrggbb" a vertical
            gradient top first, or Environment). The realistic view hides the edges, the drawing grid,
            the mesh/FDTD/section overlays, the air box, ports, boundaries and face tints, and
            reference images; ShowEdges, ShowGrid, ShowOverlays, ShowAirBox, ShowPorts, ShowBoundaries
            and ShowImages (true) each bring one back, drawn exactly as the default view draws it. A
            Show key never shows what the default view hides (a hidden air box stays hidden).
            Shadows (the key light's shadows), AmbientOcclusion (contact shading where surfaces
            meet) and Ground (a shadow-catching floor under the model's lowest point, which draws
            only the darkening) are each on unless stated false; glass (an appearance Transmission
            of 0.5 or more) casts no shadow, and none of the three ever changes a field plot's colour.
            Camera is the camera a picture is taken from, written only by the Look panel's "Set
            Camera" (an orbit never writes it): Direction (x, y, z from the target toward the
            viewer), Target (x, y, z in DBU), Distance (DBU), FovY (degrees) and Projection
            (Perspective or Orthographic). Omitted, a picture is taken from the live view's camera.
            FieldStyle is how a field plot is drawn in it: Exact (the default: the colour map's own
            colour, exactly as the default view draws it), Lit (a clear-coat sheen that only ever
            lightens a colour, so the picture carries "{LitFields}" under the legend) or Glow (the field
            exact, the scene around it dimmed). FieldOpacity (0 to 100, default 100) lets the material
            under a plot show through it; below 100 the colours are blends and the picture carries
            "{BlendedFields}". Neither label can be turned off.
            Whether the realistic view is ON is not saved. `check` refuses a value out of range or a
            spelling it cannot read, and warns on a .hdr that is missing or does not read (the view
            then lights with Studio).
          * "Model": false (written only then) keeps an object, an instance, a port or a heat source
            DRAWN and editable and leaves it out of every simulation run. An operation carries it for
            its result; a polyline is never modelled anyway. A reference to one that is off — a
            modelled port's conductor, a modelled wire's pad, a heat source's or a probe's object, a
            boundary's face — is refused, naming both. A port that is off is absent (open, not
            terminated in its Z0), and the result's ports are the modelled ones renumbered 1…N in Number
            order, the mapping in the run's notes and the .sNp header.
          * A REFERENCE IMAGE is a Sheet carrying "Image": {"Path": "ref/die.png"} — drawn with that
            picture instead of its material's colour, whatever its material (or none). Path is a
            reference, never the bytes: relative to the .c3d when the file lies inside its workspace,
            absolute otherwise, resolved against the .c3d that holds the sheet (an instance's against
            the CHILD document). The image fills the sheet's Rect (an edited Outline clips it); on XY
            its right is +x and its up +y, on XZ right +x and up +z, on YZ right +y and up +z — as
            the Top, Front and Right views read them, never mirrored. A placed image sheet is written
            "Model": false and no Material; turning Model on puts the SHEET in the solve as any sheet
            (the image is never geometry). Transparency is the image's. "Locked": true (written only
            then, only meaningful with an Image) refuses moves in the editor. The image is drawn
            UNDER any face lying on the sheet's plane (a tracing underlay); "Image": {"Path": …,
            "InFront": true} draws it OVER them instead — a picture placed on a solid's face. An image is drawing
            only: changing it never makes a result out of date. `check` warns — never errs — on an
            image file that is missing or does not decode, and on Locked with no Image.
          * "FaceImages" on any object maps images onto its flat faces, one per face: each
            {"Face": "zmax", "Image": {"Path": …}} with optional Transparency (its OWN — the
            object's does not apply; an instance's still multiplies), RotationDeg (counter-clockwise
            seen from outside), Width and Height (dimensions; both omitted fits the image in the face,
            aspect kept), Offset ([right, up] from the face's centroid) and Hidden. Seen from outside,
            up is +z projected into the face (+y on a face facing ±z) and right is up × the outward
            normal. The image is drawn only where the face is. An operation carries them for its
            result. A face the object no longer has is kept and named by `check` (a warning).
          * The editor writes a rotated or mirrored placement in CANONICAL form, so the list never
            grows with editing: at most three entries, in the order Z, Y, X (Z applied first), each
            left out when its angle is 0. A composition of quarter turns and mirrors is stored
            exactly — MirrorX plus 90, 180 or -90 entries; any other rotation as the Z-Y-X Euler
            angles of its matrix (Rx·Ry·Rz), in degrees rounded to 1e-9. Origin is then the whole
            transform's translation, so the rotation's pivot stays where it was. A placement back at
            the identity is omitted again. A hand-written list of any length is still read as
            written; a move changes only Origin and keeps it verbatim.
          * Objects are in construction order: where two solids overlap, the LATER one wins the
            volume. Nothing reorders the list except the editor's Order commands (Bring to Front,
            Send to Back, Forward, Backward), which move objects in it.
          * Names are unique across objects and instances and follow the cell-name rules; "airbox"
            is reserved. Faces are NAMED, never indexed, so what attaches to a face survives an edit:

                Box          xmin xmax ymin ymax zmin zmax (in its own frame)
                Prism        bottom, top, side<k> (the outline edge from vertex k to k+1),
                             hole<h>.side<k>
                Cylinder     bottom, top, side
                Sphere       surface (curved: no boundary, face edit or image goes on it)
                Ellipsoid    surface (curved, as a sphere's)
                Polyhedron   each face's own Name, unique within the object

          * A Polyhedron must be closed: every edge used by exactly two faces, in opposite
            directions. Every face must be planar within 1 DBU.
          * A Phantom is a layered phantom as ONE object: its Layers ([{"Name", "Material"}],
            outermost first) are nested solids, and the innermost wins where they overlap. Shape is
            Ellipsoid3 (Centre, SemiAxes, Shell1, Shell2: the shells come off every semi-axis),
            Cylinder3 (Centre, Radius, Length, Axis, Shell1, Shell2: radial shells, every layer the
            full length), FlatBox (Centre, Size, Shell1: liquid in a shell on its floor and four
            walls, open on top), Sphere1 (Centre, Radius), Box1 (Centre, Size) or Head (Centre,
            SemiAxes, Shell1, and its ear spacers' Radius and thickness Length: circuitRF's own
            generic head, layers ear-left, ear-right, shell, liquid, ears on +y and -y). Its own Material is
            not used. In the solved problem each layer is the solid "<phantom>/<layer>". Voxel (DBU,
            omitted = 0, the smooth layers) gives openEMS the phantom in cubes of that side, each the
            innermost layer holding its centre, on a lattice with a corner at Centre: how openEMS's own
            phantoms are made. Palace and the thermal solver mesh the smooth layers whatever it says.
          * A VoxelBody is a segmented voxel model in its own file beside the .c3d (File: a NIfTI
            .nii/.nii.gz, a CSXCAD DiscMaterial .h5, or a raw volume with its Raw layout {"Nx", "Ny",
            "Nz", "Type": "uint8"…, "VoxelX", "VoxelY", "VoxelZ" in DBU, "BigEndian", "HeaderBytes"}),
            and Labels, one row per label: {"Label": 4, "Name": "muscle", "Material": "Muscle"} or
            {"Label": 9, "Name": "lumen", "Air": true}. Label 0 is air without a row. A label voxels
            carry that no row maps is a refusal naming it and its voxel count. Each mapped row is the
            solid "<body>/<name>", a dielectric. Crop {"I0", "J0", "K0", "I1", "J1", "K1"} keeps voxel
            indices [I0, I1) … along the world's axes as the file is read; GridStep (DBU, omitted = 0)
            is the FDTD cell inside the body, 0 putting a line on every voxel boundary. The placement
            moves it and may turn it by quarter turns only. openEMS gets it as one DiscMaterial (at
            most 255 tissues); Palace and the thermal solver refuse it. `import voxels` writes one.
          * A Mesh is one closed triangle surface in its own file beside the .c3d (File: STL, OBJ or
            PLY; Unit its length unit, mm when omitted; Flip when its triangles face inward), a solid
            of its Material lowered to openEMS through a PolyhedronReader. A per-tissue surface model
            is several, outermost first, so an inner tissue wins. Palace and the thermal solver refuse
            it. `import surfaces` writes them.
          * "LumpedElements" are R, L and C across gaps, each a gap exactly as a lumped port's is (Name,
            Plane, Offset, Rect, and optional Positive/Negative), with Topology Parallel (default) or
            Series and any of R, L, C as expressions with RUnit, LUnit, CUnit (base SI when omitted):
            {"Name": "C1", "Plane": "YZ", "Offset": -130000000, "Rect": {...}, "C": "Ctune", "CUnit":
            "pF"}. A lumped element is not a port: nothing excites it and S has no row for it.
            openEMS solves it; Palace refuses it in this version.
          * "BandSweeps" (phone SAR) each name a Template setup (a driven openEMS one) and BandsGHz;
            each band becomes the setup "<Name> <f> GHz": the template with its Sar at that one
            frequency (so the phantom's liquid is evaluated there), 1 g and 10 g, and its sweep
            scaled by f over the template's SAR frequency. DrivePowerW (default 1 W) with
            DriveReferredTo Accepted (default) or Incident, DutyCycle, Exposure HeadTrunk (default)
            or Limbs, Limits [{"Name", "ValueWkg", "MassGrams", "Exposure", "Source", "Date"}] and
            Targets [{"FrequencyGHz", "MassGrams", "PerWattWkg"}] are read by its table only, never
            by a run. `em --bands <name>` runs every band and prints the table beside the published
            reference values.
          * A Polyline is construction geometry. It has no Material and is never solved. Its Points
            are [u, v] on its Plane at its Offset — unless a vertex leaves that plane (the editor
            snaps a polyline's vertices to features anywhere), when the polyline is written with
            Points3, a list of [x, y, z], instead, and Points is empty. Points3 is written only
            then, so a planar polyline is spelled as it always was.
          * Material names a material of the technology — the one TechRef names, or the workspace's
            default when TechRef is omitted. Role (Conductor, Dielectric, Air) overrides what the
            material implies: a material with σ and no εr is a conductor, one with εr a dielectric,
            one named Air is air, and one with both and no Role is refused. A Sheet must conduct.
          * An instance places another cell: CellRef is spelled as a layout instance's (relative to
            this file, or ws://), and View picks its 3D view (the default) or its Layout. Its objects
            are named <instance>/<object>, an array element's <instance>[i,j,k]/<object>; the array
            pitch is in this document's frame. Only geometry comes in: a placed cell's ports, setups
            and face boundaries are ignored. A placed LAYOUT is made 3D through its OWN technology,
            the bottom of its stackup at the instance's z, its slabs bounded by its
            board outline (else by its drawn extent). Layout instancing's same-technology rule does
            not apply here — it exists because layouts match layers by number, and a 3D view matches
            nothing by layer: every instance is metres and named materials before it arrives. Two
            technologies' same-named materials merge when equal and become <name>@<technology stem>
            otherwise. A 3D view that reaches itself through its instances is refused — judged by cell
            AND view: a cell's 3D view may hold that cell's own layout, which is not a cycle.
          * A Wire's Array makes it a ROW of identical wires: {"Count": 4, "Pitch": [0, 101600, 0]}
            is the drawn wire and three copies, each moved by one more Pitch (a vector, DBU, so a row
            may run in any direction). Omitted, it is one wire. Each copy is a wire of its own in the
            model, named <wire>[k] (k from 0, the drawn one), its ends looked up on the pads under
            them and refused by that name like any wire's. A row is one-dimensional on purpose: a
            bonded row is a number of wires and a pitch. A rotation or mirror of the wire turns its
            Pitch with it.
          * Setups hold EM setups in the .cem schema (see the em-setup topic) without LayoutRef, each
            with a unique Name and Solver3D Palace or OpenEms. `em view.c3d` runs the one there is;
            with several, `--setup <name>`. Its results go to the workspace's results/ under the key
            "<file stem> <setup name>.<solver>" (solver palace, openems or emerge, so two solvers'
            results never collide): a driven setup writes "<file stem> <setup name>.<solver>.s<N>p"
            and "<file stem> <setup name>.<solver>_em.npy", and keeps its run folder
            "<file stem> <setup name>.<solver>/"; an electrostatic, magnetostatic or eigenmode setup
            writes "<file stem> <setup name>.<solver>_es.npy" (_ms, _eig) and keeps its run folder
            "<file stem> <setup name>.<solver>_es/" (_ms, _eig). `-o` moves the Touchstone only.
            `run analysis=em` lists every file it wrote in its outputs, the Touchstone first. A static
            setup's Terminals3D name their conductors with Objects (["top"], U1/... allowed) — a drawn
            object carries no net — instead of Net; stating both is refused.
          * A THERMAL setup is an embedded setup with "Problem3D": "Thermal", no Solver3D (stating one
            is refused: the thermal solver is circuitRF's own) and a "Thermal" section — Sources (a
            power per heat source, overriding its default), Boundaries (FixedT with TempC, or
            Convection with H and AmbientC, on a face "object/face" or on "*exposed*"; at least one),
            Sweep (up to two axes over document variables no geometry reads), Measures ("Rth =
            (Tmax(die_top) - Tavg(flange_bot)) / Pdiss", over probes with Tmax, Tmin, Tavg and T — T reads a
            point, a spot, or a probe's own Stat),
            Mesh, Balance and Submodel ({"From": "<whole-model setup>", "Region": "<mesh region>"}: solve
            only that region's box, finely, its cut faces fixed to the From setup's solution — reused when
            solved from the model and its files as they are now, else solved first — and no Sweep of its own). Every value is an
            expression in the document's variables; temperatures are °C; a measure may read SymmetryFactor
            (2 per symmetry plane). A .cem never holds one. The places it reads are the document's own lists:
            HeatSources (a Sheet on a Plane at an Offset with a Rect or an Outline, lying inside ONE
            solid — or a Solid, by name — with a default Power), Probes (exactly one of Point, Face,
            Solid, Spot, Line, Wire — Face may be a list of faces read as one place, what a face edit's
            fold leaves; a Stat of Max, Min or Avg — what T(probe) and the result's T:<probe>
            report; an optional LimitC), MeshRegions (a
            box and a target SizeUm, for every Gmsh-meshed setup), ContactResistances (two
            touching objects and a resistance, m²·°C/W, overriding the technology's material pair — 0 is
            perfect contact, one pair per entry; a run splits the contact and joins its sides through it), EffectiveBlocks (Name, Min, Size and
            Enabled, false when omitted: enabled, the board dielectric, planes and via barrels of a
            layout instance inside the box become one block of diagonal conductivity — per layer
            k_z = f_Cu·k_Cu + f_d·k_d and k_xy by Rayleigh's formula for parallel cylinders, layers in
            series for k_z and in parallel for k_xy — an approximation), SymmetryPlanes
            ([{"Axis": "X", "At": 0}], at most one per axis, on the model's extent; insulated) and
            WireGroundPlane ({"Z": 0} — DBU or an expression — or "object/face", a horizontal face of a
            conductor): the image plane a run's RF current share among DRAWN bond wires reflects in
            (wBond's image method). It is never inferred: omitted, drawn wires are shared in free space.
            It must lie below every drawn wire; a .wBond's wires keep their own design's plane.
            None of them reaches an EM solver. `check` reports every thermal finding
            (c3d.thermal.*); `explain view.c3d --analysis [setup]` lists what a thermal setup would
            solve; `em` runs it.
          * Ports belong to the document, and every setup uses all of them (choosing a subset per
            setup is a later build). A placed cell's ports are never used: only the parent says where
            a signal enters. A port is a Rect on a drawing Plane at an Offset, with a Number, a Name,
            a Kind (Lumped or Wave) and a Z0 ("50", or a complex "25+j10"):

                { "Number": 1, "Name": "P1", "Kind": "Lumped", "Plane": "XZ", "Offset": 0,
                  "Rect": { "Min": [2000000, 0], "Size": [1100000, 508000] }, "Z0": "50" }

            Which conductors a LUMPED port joins, and which way round, is INFERRED from what it
            touches: each of the rectangle's four edges (its ends pulled in by two DBU, so a corner
            contact does not count) is tested against every elaborated conductor — solids of role
            Conductor, every sheet, a PEC face of the setup's air box — to within one DBU. Exactly
            one pair of OPPOSITE edges must each touch exactly one conductor, the two different;
            that pair's axis is the port's direction. The NEGATIVE end is the one in the setup's
            ground set (its Ground3D net — a drawn conductor's net is its own name — a layout
            instance's ground-reference conductors, or a PEC air-box face); failing that, the one
            with the larger surface. Flip swaps them. Positive and Negative (elaborated names,
            U1/pad allowed, or airbox/zmin) state both ends and override inference entirely; one
            without the other is refused. A refusal names what each edge touched. `check` reports
            every port's polarity and `explain` walks each edge's contacts.
          * A WAVE port's rectangle is a region of an air-box face of the setup being run — the box
            is each setup's own, so this is checked per setup. Its two conductors are the ones that
            meet the region, and its voltage path runs across the gap between them at the positive
            one's centre; VoltagePath ({"From": [u, v], "To": [u, v]}) states it instead.
          * A wave port whose region is met by SEVERAL signal conductors and one reference (a ground,
            a shield) is one port with Terminals — each terminal is its own numbered port of the result,
            with its own Z0 (terminal S-parameters, not modal ones). The port states no Number, Z0,
            Positive, Negative, VoltagePath or Flip; each terminal states its own:

                { "Name": "Left", "Kind": "Wave", "Plane": "YZ", "Offset": 0, "Rect": { ... },
                  "Reference": "gnd",
                  "Terminals": [
                    { "Number": 1, "Name": "P1", "Conductor": "strip_a", "Z0": "50" },
                    { "Number": 2, "Name": "P2", "Conductor": "strip_b", "Z0": "50" } ] }

            Reference is optional: inferred, it is the one conductor meeting the region that is in the
            ground set (a PEC air-box face counts), else the largest surface; a tie is refused. Each
            terminal's Conductor must meet the region and is never the reference; two terminals on one
            conductor are refused; terminal numbers share one namespace with every other port's. A
            terminal's voltage path runs from the reference to its conductor (inferred; VoltagePath
            states it, Flip reverses it). One terminal is refused (write the ordinary port), and so is a
            lumped port with Terminals. Terminals are never inferred when reading: a wave port with no
            Terminals whose region three or more conductors meet is refused, naming the fix. Model
            applies to the whole port. A .cem cannot state one.
          * COMPONENT PORTS are not drawn: an embedded setup's "ComponentPorts" (["C1", "L2"], "*" for every
            placed two-terminal part, or {"Refdes", "Z0", "Reference"} entries) asks for a lumped GAP port
            across each part's own pads on a placed layout, numbered after the drawn ports. circuitRF builds
            it on the copper facing the gap (with a landing rectangle where that copper is a cap or an arc,
            in the solved problem only) and verifies it before meshing; `explain --component-ports` lists
            them, and a driven run writes "<setup>_cosim.cnl" with each part from its port's node to ground.
            A gap port, not a pad-to-plane port at each pad: a part between two pad-to-plane ports carries
            its current down one sheet, along the plane and up the other — a loop that is not on the board —
            so a hand-drawn pair of them is a different, approximate model of the part.
          * FaceBoundaries put a boundary on a NAMED face of a dielectric or air object:
            {"Object": "block", "Face": "zmax", "Kind": "Pec"}, or Kind "Conductive" with a
            Material. A boundary follows its face through every edit, and a fold hands it to each
            piece. A conductor already is a void bounded by its metal, so a boundary on one is
            refused; Absorbing, Pmc and Symmetry are the air box's (both solvers state them only on
            the outer boundary), set in a setup's AirBox. An AirBox face's padding is PaddingUm, or
            PaddingPercent — a percentage of the content's extent along that face's axis, so
            {"XMin": {"PaddingPercent": 10}, "XMax": {"PaddingPercent": 10}} pads a tenth of the
            x-extent on each side; never both on one face. Palace finds a face's surfaces by its
            bounding box and counts them exactly: a neighbour's face lying in the same plane and
            overlapping it is refused, never merged. A curved face (a cylinder's side) is refused.
          * OPERATIONS are built by OpenCASCADE, which ships with circuitRF (Settings ▸ Solvers says
            whether this installation has it). A Boolean is Op (Subtract,
            Unite, Intersect) of one Blank and one or more Tools, owned inline; a Fillet rounds named
            Edges of its Target by Radius; a Chamfer cuts them by Distance (and Distance2 along the
            edge's second face); a Step is ONE solid part of a STEP file in the cell's 3d folder:

                { "$type": "Boolean", "Name": "lid", "Op": "Subtract",
                  "Blank": { "$type": "Box", "Material": "Lid alloy",
                             "Min": [0, 0, 500000], "Size": [4000000, 3000000, 250000] },
                  "Tools": [ { "$type": "Cylinder", "Name": "bore", "Base": [2000000, 1500000, 400000],
                               "Length": 500000, "Radius": 300000 } ] }
                { "$type": "Fillet", "Name": "lid", "Radius": 50000, "Edges": ["bore:side|zmax"],
                  "Target": { "$type": "Boolean", "Op": "Subtract", "Blank": …, "Tools": [ … ] } }
                { "$type": "Chamfer", "Name": "pin", "Distance": 20000, "Edges": ["side|top"],
                  "Target": { "$type": "Cylinder", … } }
                { "$type": "Step", "Name": "shell", "Material": "Brass", "File": "sma-body.step",
                  "Part": "1/2", "Solid": 3, "Hash": "sha256:…", "Unit": "inch",
                  "SourcePath": "../../incoming/sma-body.step" }

            "Solid" (optional, from 1) is one solid of a Part that holds several, in the reader's order;
            without it the object is the whole Part.

            THE WRAPPER TAKES THE NAME: a Boolean is its Blank to the rest of the document, a Fillet or
            Chamfer its Target — the object inside has NO Name (writing one is a check error), and its
            Material and Role are the result's (an operation stating its own is a check error). Tools
            keep their own names, unique across the whole document at every depth. So a port or a
            boundary put on lid before anything was subtracted from it is still on lid afterwards.
            Operands are solids only — Box, Prism, Cylinder, Sphere, Polyhedron, Boolean, Fillet,
            Chamfer, Step; a Sheet, Polyline or Wire is refused. Radius, Distance and Distance2 are dimensions,
            expressions allowed, and must be positive. A Placement on an operation moves its whole
            result, after each operand's own.
          * The RESULT's faces: the Blank's keep their bare names (zmax), a Tool's are <tool>:<face>
            (bore:side; a nested Tool's <tool>:<inner>:<face>), a face the operation split is zmax#1,
            zmax#2 — numbered by centroid in the object's own frame, x then y then z — and a reference
            to zmax covers every piece. Edges are named by the two faces they separate, sorted and
            joined by '|' (xmax|zmax, bore:side|zmax); two faces sharing more than one edge give each
            a third field, its number (trench:side|zmax|1). A seam is not named. A fillet's new faces
            are fillet(<edge>), a chamfer's chamfer(<edge>), for every edge the kernel rounded. A Step
            part's faces are face<n> in the part's own order, meaningful for its Hash: a file whose
            bytes changed outside circuitRF is a check error naming Reload from Source.
          * "Enabled": false (written only then) makes an operation as if it were not there: a
            disabled Boolean elaborates its Blank under the Boolean's name and each Tool under its
            own, at the Boolean's place in the list; a disabled Fillet or Chamfer its Target
            unrounded. A reference to (lid, bore:side) then lands on (bore, side), and (lid, zmax) on
            the Blank — nothing in the file changes. "KeepTools": true on a Subtract also elaborates
            each Tool as its own solid right after the result, with its own material: a dielectric
            fill in a bore. A Unite of different materials takes the Blank's, and the elaboration
            says which it replaced. An operation the kernel cannot build (a fillet too large, an empty
            result) is refused by name and the rest of the document still elaborates.
          * A STEP PART is ONE solid of a STEP file copied into the .c3d's own folder. File is that
            copy's name (relative to the .c3d); Part is the occurrence path in the file's assembly
            ("1", "1/2" — the component index at each level; a part instanced twice is two paths and
            two objects); Hash is "sha256:<hex>" of the copy's bytes; Unit is the file's length unit
            as read (informational — the reader converts every coordinate exactly); SourcePath is
            where it was imported from, relative to the .c3d inside the workspace, else absolute
            (what Reload from Source reads). The file's assembly placement is applied from Part, so
            Placement starts at identity and anything written there composes on top. Material may be
            absent: the part is then drawn, ignored by the solver, and named by `check`. Several
            objects may name one File. To import into an EXISTING 3D view headlessly, copy the file
            beside the .c3d and write these objects; `convert part.step -o <cell>/3d/<name>.c3d`
            makes a NEW one (materials by part name, then by exact colour, else none; --material
            <part>=<name>, --part <path> and --tech <path> answer what the dialog would ask).
          * WITHOUT OPENCASCADE a 3D view that holds any Boolean, Fillet, Chamfer or Step —
            at any depth, ENABLED OR NOT — is refused on open with the reason and the action; `check`
            reports one error per such object and exits 1, and `em` refuses before writing anything.
            A 3D view with none opens and runs exactly as before. Palace meshes a kernel solid's
            curves as they are; openEMS staircases them to its grid. Each setup says, per object,
            what its solver will not respect (a fillet narrower than the grid cell, a curved
            conductor under ten skin depths) — in `check`, the run's messages and the editor's
            setups — and none of it blocks a run. A boundary on a curved face is openEMS's refusal.
          * A NAMED DIMENSION may hold an expression instead of a number: Min and Size (box,
            rectangle), Offset, Height and Shear (prism, sheet, polyline), Base, Length and Radius
            (cylinder), ThicknessUm, a placement's Origin and each Rotate angle, an array's Counts and
            Pitch, a port's Rect, a wire's DiameterUm and its Array's Count and Pitch, a Fillet's Radius
            and a Chamfer's Distance and Distance2. It is an object
            that ALWAYS carries the unit it
            was typed in, component by component:

                "Size": [{ "Expr": "w", "Unit": "Mil" }, 1270000, { "Expr": "h_sub + t_met", "Unit": "Um" }]

            The unit is stored so that changing DisplayUnit never changes what an expression means.
            A number may carry its own unit, glued to it (`2*w + 5um`, `10um + 1mil`). An angle's unit is
            Deg; a count has none and must come out a whole number of at least 1 — never rounded.
            Everything resolves BEFORE any geometry is built; a length is rounded to the DBU once,
            and `check` notes a rounding that moved it by more than 1e-9.
          * A WIRE'S AND A POLYLINE'S POINTS may hold expressions too, any component of any point,
            spelled exactly as a named dimension's components are — the point stays on one line:

                "Points": [
                    [254000, 0, 50800],
                    [381000, 0, { "Expr": "h_loop", "Unit": "Mil" }],
                    [{ "Expr": "x_pad + 50um", "Unit": "Um" }, 0, 50800]
                ]

            A polyline's Points are [u, v] in its Plane at its Offset; its Points3 are [x, y, z]:

                "Points": [[0, 0], [{ "Expr": "x_v", "Unit": "Mil" }, 254000], [0, 254000]]

            A polyline that has Points3 ignores its Points, so an expression in those is refused on
            read. `check` names a point that does not resolve as `'w1' point 2 z = h_loop: …`, 1-based.
            Outline, Holes and a polyhedron's Vertices hold numbers only.
          * A WIRE'S LoopHeight AND Span may be HELD: `"LoopHeight": { "Expr": "h_loop", "Unit": "Um" }`,
            `"Span": { "Expr": "s_w", "Unit": "Um" }`. Each is written only when held. Elaboration shapes
            the points to them, Span first: Span moves the end foot along the run in plan with the
            start pinned, and LoopHeight (the lower pad's top to the top of the wire at its apex)
            scales every point's rise above the foot-to-foot chord. So changing the variable changes
            the wire. The ends keep their z, and an end a held span moves off its pad is refused as any
            end off its pad is. A held span refuses an expression in any point's x or y but the
            start's; a held loop height refuses one in an interior point's z.
          * UNITS. A field's unit is its SITE unit. It is skipped when the expression is
            unit-bearing — it holds a unit literal (`10mil`) or references a name that carries a
            unit of its own (var-unit-wins) — and it then goes to the bare numbers ADDED to,
            subtracted from or compared with a unit-bearing term: in `2*w + 5`, with w in mil, the
            5 is five mil and the 2 stays a multiplier (25 mil). A bare `m` is MILLI (`2m` is 2 mm;
            the metre is `metre`), and `check` warns about one in a length and about any dimension
            above 1 m, the mark a unit slip leaves.
          * Variables are the 3D view's VARs, the record a schematic VAR row holds:

                { "Name": "w", "Expression": "10", "Unit": "Mil" }
                { "Name": "gap", "Expression": "w / 4" }      unit-less: takes w's units

            A name is one the expression engine reads as a reference — not a built-in function,
            not j, pi, e or freq — and unique among the VARs. A 3D view sees its CELL's parameters
            (the .ccell's) and its own VARs, in one namespace; there are no globals, and a 3D view
            outside a cell folder sees its VARs only.
          * A VAR NAMED LIKE A CELL PARAMETER is LINKED to it unless it says "Linked": false:

                Linked (or absent)  the VAR's value is the PARAMETER's — an instance's override, else
                                    the .ccell default. Its own Expression is used only when the
                                    3D view has no cell. Overrides reach every dimension using it.
                false               the VAR's own Expression. Overrides of the name do NOT reach this
                                    3D view, and `check` warns that the VAR hides the parameter.

            This is deliberately NOT the schematic's order. There a same-name VAR replaces the
            parameter's default and only an override gets through, which gives a cell two defaults
            for one name — an instance that overrides nothing would be one size in the schematic and
            another in 3D. Linking to the parameter as a whole keeps one default, in the .ccell.
          * An instance of another cell's 3D view may override that cell's PARAMETERS, each an
            expression evaluated in THIS document's scope:

                "Params": { "w": { "Expr": "2*a", "Unit": "Mil" } }

            A name that is only a VAR of the child is refused (promote it there first), and a Layout
            instance takes none — a .clay has no parameters. Two instances whose overrides come to
            the same values share one elaboration of the child. Names that cycle (a → b → a),
            through defaults, VARs, links and overrides, are refused with the chain.
          * `check` reports an undefined name (error), a VAR hiding a parameter and a dimension above
            1 m (warnings), and an unused VAR (info). `explain` reports where each name's value came
            from, and `explain x.c3d --expr "2*w" --set w=…` evaluates in the document's scope. A
            key this build does not know is kept and written back, and `check` warns of it.

        Refused, with the reason named: a file that is not JSON; a FormatVersion newer than this
        build; an object whose "$type" this build does not know (every one is listed); and a string
        where a number belongs — an expression is an object carrying its unit, never a bare string.
        Every other problem is a `check` finding, and `check` lists all of them rather than the
        first: a duplicate name, an
        outline of fewer than three distinct points, an open or non-planar polyhedron, a solid with
        no volume, a material the technology does not define (a warning).

        Every field the reader understands follows, with its default. Objects are listed once per
        kind, each with the "$type" value that selects it; a type spelled [x, y, z] or [u, v] is a
        point, written as a JSON array.
        """;

    // ── rendering ────────────────────────────────────────────────────────────

    /// <summary>The topic's text. Pure — the topic list and the resource listing measure it by
    /// rendering it, exactly as they measure the component catalogue.</summary>
    public static string Render(Format format) => Render(format, tables: true);

    /// <summary>The page with its field tables, or without them when they travel as <see cref="Json"/> instead.</summary>
    public static string Render(Format format, bool tables)
    {
        var sb = new StringBuilder();
        sb.Append(format.Title).Append(" — ").Append(format.Extension).AppendLine();
        sb.AppendLine();
        sb.AppendLine(format.Preamble);
        sb.AppendLine();
        if (!tables)
        {
            sb.AppendLine("The field tables are in reference.schema: one entry per type, each field with its JSON type and default.");
            return sb.ToString();
        }

        foreach (var (type, fields) in Walk(format))
        {
            sb.AppendLine(type);
            foreach (var f in fields)
                sb.AppendLine($"  {f.Name,-24} {f.Type,-26} {f.Default}".TrimEnd());
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>The same walk as a document, for <c>--json</c>.</summary>
    public static IReadOnlyList<ReferenceSchemaTypeJson> Json(Format format)
        => [.. Walk(format).Select(t => new ReferenceSchemaTypeJson(
                t.Type, [.. t.Fields.Select(f => new ReferenceSchemaFieldJson(f.Name, f.Type, f.Default))]))];

    private sealed record Field(string Name, string Type, string Default);

    private sealed record Block(string Type, IReadOnlyList<Field> Fields);

    /// <summary>
    /// Every object type reachable from the root, breadth first, root first — which is the order a
    /// reader needs them in, since a nested type is only interesting once the field naming it has
    /// been read. Enums come last, all together, because they are referenced from everywhere.
    /// </summary>
    private static IReadOnlyList<Block> Walk(Format format)
    {
        if (format.Root is not { } root) return [];
        var asms    = new HashSet<System.Reflection.Assembly>([root.Assembly, .. format.Descend ?? []]);
        var objects = new List<Block>();
        var enums   = new List<Block>();
        var seen    = new HashSet<Type> { root };
        var queue   = new Queue<Type>([root]);
        foreach (var more in format.More ?? [])
            if (seen.Add(more)) queue.Enqueue(more);

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            var rows = new List<Field>();

            // A default-constructed instance is where every default comes from. A type with no
            // parameterless constructor simply has no defaults to report — never a guessed one.
            object? blank = null;
            try { blank = Activator.CreateInstance(type); } catch { /* no default to report */ }

            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                if (p.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always }) continue;
                // Where a reader PARKS the keys it does not know — not a key anyone writes.
                if (p.GetCustomAttribute<JsonExtensionDataAttribute>() is not null) continue;

                string name = p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name;
                rows.Add(new Field(name, Spell(p.PropertyType), DefaultOf(p, blank)));

                foreach (var t in Referenced(p.PropertyType))
                {
                    if (t.IsEnum) { if (seen.Add(t)) enums.Add(EnumBlock(t)); continue; }
                    if (SpellingsOf(t) is { } spellings) { if (seen.Add(t)) enums.Add(SpelledBlock(t, spellings)); continue; }
                    if (SpelledAs(t) is not null) continue;
                    if (!asms.Contains(t.Assembly) || !seen.Add(t)) continue;
                    queue.Enqueue(t);
                }
            }

            // A polymorphic base (a .clay's shapes) is written as one of its DERIVED types, picked by
            // a discriminator — and the base alone names neither the discriminator nor any derived
            // type's fields, which are most of what a shape is. Both are read off the same
            // attributes System.Text.Json reads, so the page states what the reader accepts.
            var derived = type.GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false).ToList();
            if (derived.Count > 0)
            {
                string key = type.GetCustomAttribute<JsonPolymorphicAttribute>()?.TypeDiscriminatorPropertyName
                             ?? "$type";
                rows.Insert(0, new Field(key, "string (which kind)",
                    string.Join(" | ", derived.Select(d => d.TypeDiscriminator))));

                foreach (var d in derived)
                    if (seen.Add(d.DerivedType)) queue.Enqueue(d.DerivedType);
            }

            string title = type.Name;
            if (type.BaseType?.GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false)
                    .FirstOrDefault(d => d.DerivedType == type) is { } self)
                title += $"   ({BaseKey(type.BaseType)}: \"{self.TypeDiscriminator}\")";

            objects.Add(new Block(title, rows));
        }

        return [.. objects, .. enums.OrderBy(e => e.Type, StringComparer.Ordinal)];
    }

    private static string BaseKey(Type polymorphicBase)
        => polymorphicBase.GetCustomAttribute<JsonPolymorphicAttribute>()?.TypeDiscriminatorPropertyName ?? "$type";

    private static Block EnumBlock(Type t) => new(
        t.Name + "   (one of)",
        [.. Enum.GetNames(t).Select(n => new Field(n, "", ""))]);

    /// <summary>brief-emi-1 — a type written as one string of a set assembled at run time (<see cref="CircuitRF.Design.IJsonSpelled"/>):
    /// listed as an enum is, never expanded into the properties a file does not hold.</summary>
    private static IReadOnlyList<string>? SpellingsOf(Type t)
        => t.GetInterfaces().Contains(typeof(CircuitRF.Design.IJsonSpelled))
               ? t.GetProperty(nameof(CircuitRF.Design.IJsonSpelled.JsonSpellings), BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                     as IReadOnlyList<string>
               : null;

    private static Block SpelledBlock(Type t, IReadOnlyList<string> spellings) => new(
        t.Name + "   (one of)",
        [.. spellings.Select(n => new Field(n, "", ""))]);

    /// <summary>The types a property could pull in: itself, and whatever a list or dictionary holds.
    /// Nullable&lt;T&gt; is unwrapped, so a <c>double?</c> reaches the same place a <c>double</c>
    /// does.</summary>
    private static IEnumerable<Type> Referenced(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t.IsArray)
        {
            foreach (var inner in Referenced(t.GetElementType()!)) yield return inner;
            yield break;
        }
        if (t.IsGenericType)
        {
            foreach (var arg in t.GetGenericArguments())
                foreach (var inner in Referenced(arg)) yield return inner;
            yield break;
        }
        if (t.IsPrimitive || t == typeof(string) || t == typeof(decimal)) yield break;
        yield return t;
    }

    /// <summary>
    /// A type with a converter of its own writes a shape its properties do not describe — a
    /// <c>.c3d</c> point is the array <c>[x, y, z]</c>, not an object with X, Y and Z — so it says
    /// how it is spelled in a <see cref="DescriptionAttribute"/>, and it is not expanded.
    /// </summary>
    private static string? SpelledAs(Type t)
        => t.GetCustomAttribute<JsonConverterAttribute>() is not null
               ? t.GetCustomAttribute<DescriptionAttribute>()?.Description
               : null;

    /// <summary>How a type is written on the page: the JSON shape, not the CLR name.</summary>
    private static string Spell(Type t)
    {
        var nullable = Nullable.GetUnderlyingType(t);
        if (nullable is not null) return Spell(nullable) + "?";
        if (SpelledAs(t) is { } spelled) return spelled;
        if (t == typeof(System.Text.Json.JsonElement)) return "(any JSON)";
        if (typeof(System.Text.Json.Nodes.JsonNode).IsAssignableFrom(t)) return "(a JSON object)";
        // A pair or a complex number is written as a two-element array, never as an object.
        if (t == typeof(System.Numerics.Complex)) return "[re, im]";
        if (t.IsGenericType && t.FullName?.StartsWith("System.ValueTuple`", StringComparison.Ordinal) == true)
            return "[" + string.Join(", ", t.GetGenericArguments().Select(Spell)) + "]";

        if (t == typeof(string))                          return "string";
        if (t == typeof(bool))                            return "bool";
        if (t == typeof(int) || t == typeof(long))        return "integer";
        if (t == typeof(byte))                            return "integer (0-255)";
        if (t == typeof(double) || t == typeof(float))    return "number";
        if (t == typeof(uint))                            return "integer (ARGB)";

        if (t.IsArray) return "[" + Spell(t.GetElementType()!) + "]";

        if (t.IsGenericType)
        {
            var args = t.GetGenericArguments();
            if (args.Length == 1) return "[" + Spell(args[0]) + "]";
            if (args.Length == 2) return "{" + Spell(args[0]) + ": " + Spell(args[1]) + "}";
        }
        return t.Name;
    }

    /// <summary>
    /// The default this field carries when a file omits it — read off the constructed instance,
    /// never written down. An empty answer means the property could not be read at all, which is
    /// the honest report and not a zero.
    /// </summary>
    private static string DefaultOf(PropertyInfo p, object? blank)
    {
        if (blank is null) return "";
        object? v;
        try { v = p.GetValue(blank); } catch { return ""; }

        // A self-spelled value (a point) is shown as the JSON it is written as.
        if (v is not null && SpelledAs(v.GetType()) is not null)
            return System.Text.Json.JsonSerializer.Serialize(v, v.GetType());

        return v switch
        {
            null            => "null",
            string s        => s.Length == 0 ? "\"\"" : "\"" + s + "\"",
            bool b          => b ? "true" : "false",
            Enum e          => e.ToString(),
            // A collection's default is its emptiness — printing its element count would say the
            // same thing less clearly, and a non-empty default collection does not occur here.
            IDictionary d   => d.Count == 0 ? "{}" : "{…}",
            IEnumerable c   => c.Cast<object?>().Any() ? "[…]" : "[]",
            // A nested object default-constructed by its owner is not "absent": omitting the field
            // gets you one of these, which is a different statement from null.
            not null when v.GetType().IsClass && v is not IFormattable => "(a " + v.GetType().Name + ")",
            IFormattable f  => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _               => v.ToString() ?? "",
        };
    }
}

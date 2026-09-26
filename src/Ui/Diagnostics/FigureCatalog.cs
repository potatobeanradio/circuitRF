using System;
using System.Collections.Generic;
using CircuitRF.Ui.Diagnostics.Fixtures;

namespace CircuitRF.Ui.Diagnostics;

/// <summary>
/// Every user-doc figure that is a capture of the live interface, one row each.
///
/// <para>The shape is deliberately the same as <see cref="SymbolArtworkGenerator.Catalog"/>, because
/// that is the property which has kept the symbol generator alive for a year: <b>adding a figure is
/// one row</b>, and there is nowhere else to remember to touch.</para>
///
/// <para><b><see cref="Row.Id"/> is a contract.</b> It is the file stem AND the
/// <c>{{ui: …}}</c> key a documentation page cites. Renaming one breaks a page — and breaks it
/// loudly, because an unresolvable placeholder fails generation.</para>
///
/// <para><b>Every row states an explicit capture size.</b> There is no natural-size fallback: a
/// control captured on its own has no size of its own, and an unsized capture is an empty one.</para>
/// </summary>
public static class FigureCatalog
{
    /// <summary>One figure.</summary>
    /// <param name="Id">File stem and placeholder key.</param>
    /// <param name="Build">Puts the interface into the state worth photographing.</param>
    /// <param name="Width">Capture width, in device-independent pixels. Excludes any window frame.</param>
    /// <param name="Height">Capture height. Excludes any window frame.</param>
    /// <param name="Chrome">A synthetic window frame, or null for a bare panel.</param>
    /// <param name="Caption">The figure caption written under the picture.</param>
    /// <param name="MustContainPopup">
    /// True when this figure declares a popup. Generation fails if the popup contributed nothing —
    /// "the menu silently did not render" is otherwise indistinguishable from "the menu is closed".
    /// </param>
    /// <param name="Static">
    /// <b>Committed, and NOT rebuilt by an ordinary regeneration.</b> For the one kind of figure this
    /// factory's premise does not cover: a picture whose content is not a function of the interface
    /// alone, but of a simulation nobody should be made to re-run. The antenna pattern figures are
    /// that — a radiation pattern is a post-process of solved currents, and the sweep behind them is
    /// 45.7 s optimised and 5 min 11 s unoptimised for an answer that does not change (owner,
    /// 2026-09-12: keep the picture, not the dataset).
    ///
    /// <para>The <see cref="Build"/> is still here and still correct, so the figure remains
    /// reproducible rather than orphaned — <c>--rebuild-static</c> runs it, and the fixture says what
    /// it needs. Everything else about the row is unchanged: the caption is still the catalog's, the
    /// placeholder still resolves through it, and <c>DocsFactoryTests</c> still requires both
    /// variants to exist and to carry ink. What changes is only who wrote the file and when.</para>
    /// </param>
    public readonly record struct Row(
        string Id,
        Func<FigureScene> Build,
        int Width,
        int Height,
        WindowFrame? Chrome,
        string Caption,
        bool MustContainPopup = false,
        bool Static = false);

    public static readonly IReadOnlyList<Row> Catalog =
    [
        // ── The workspace: the window everything else in this catalog lives inside ──
        // 1400x900 rather than the shell's own 1200x800 default: the six panels are all real, and
        // at 1200 the document column is narrower than the schematic in it.

        new("workspace-overview", DocWorkspaceFixtures.Overview, 1400, 900,
            WindowFrame.Titled("Amplifier Design — circuitRF"),
            "The workspace window: a schematic open in the document area, the Project, Properties, "
          + "Library and Messages panels around it, and a layout waiting in the second tab."),

        new("workspace-regions", DocWorkspaceFixtures.Regions, 1400, 900,
            WindowFrame.Titled("Amplifier Design — circuitRF"),
            "The same window with each region numbered."),

        // A CROP of the same window rather than a panel of its own (see FigureCrop): the thing being
        // photographed is 11 px wide, and in workspace-overview above it is four pixels of orange a
        // reader cannot find. No chrome — it is the inside of a window, not a window.
        new("editable-reference-tab", DocWorkspaceFixtures.EditableReferenceTab, 460, 46, null,
            "Two open documents, one of them opened out of a workspace this one references with "
          + "editing allowed. The pencil beside its name says that saving it writes into that other "
          + "project. A tab with no pencil is an ordinary document of this workspace."),

        new("schematic-editor", DocFixtures.SchematicEditor, 1100, 700,
            WindowFrame.Titled("circuitRF — FET S-Parameters"),
            "The schematic editor with the shipped FET S-parameter test bench open."),

        new("schematic-context-menu", DocFixtures.SchematicContextMenu, 1100, 700,
            WindowFrame.Titled("circuitRF — FET S-Parameters"),
            "Right-clicking a component opens its context menu.",
            MustContainPopup: true),

        new("library-palette", DocSchematicFixtures.LibraryPalette, 280, 620, null,
            "The Library Palette on the All category: every built-in component, four tiles to a row "
          + "at the width the default dock layout gives the left column."),

        // 520x386 and 520x616 are the two dialogs' OWN declared sizes less the synthetic title bar
        // (SetupAnalysesDialog is 520x420; AnalysisEditorDialog is 520 wide and sizes to content up
        // to MaxHeight 650). A capture at any other size shows a dialog no reader's build opens.
        new("analyses-setup", DocSchematicFixtures.SetupAnalyses, 520, 386,
            WindowFrame.Titled("Setup Analyses"),
            "Simulate > Setup Analyses on a test bench carrying two analyses: a DC operating point "
          + "and a harmonic-balance run wrapped in a Pin drive sweep."),

        new("analysis-editor-hb", DocSchematicFixtures.HbAnalysisEditor, 520, 616,
            WindowFrame.Titled("Edit Analysis"),
            "The analysis editor on that harmonic-balance analysis: the type, the tone, the harmonic "
          + "order, and the parametric sweep that wraps it. The dialog sizes to its content up to "
          + "650 px and scrolls past that, which is why the sweep rows run off the bottom."),

        // The panel form of the same view the Setup Analyses dialog hosts, carrying one row of every
        // type. Sized to the dock column an Analyses panel actually gets, not to a dialog.
        new("analyses-all-types", DocSchematicFixtures.AllAnalysisTypes, 430, 330, null,
            "The Analyses panel with one of every analysis type on the shipped loadpull bench: a DC "
          + "operating point, an S-parameter sweep, a harmonic-balance run, the Pin sweep that wraps "
          + "it, a loadpull over a termination grid, and a loadpull pursuit."),

        // ── The figures a reader is meant to REBUILD ──────────────────────────────
        // Square, and all the same square, so a schematic and the plot it produces read as one
        // instruction when they are shown side by side.

        // Smaller than the worked-example square on purpose: the same slide box then scales it up,
        // which is the only lever a figure has on how big its content lands in a deck.
        new("inline-value-editor", DocExampleFixtures.InlineValueEditor, 460, 460,
            WindowFrame.Titled("circuitRF - Schematic"),
            "Double-clicking a value label edits it in place: a 50 ohm resistor with the inline "
          + "editor open on R."),

        new("example-dc-schematic", DocExampleFixtures.DcExampleSchematic,
            DocExampleFixtures.Square, DocExampleFixtures.Square,
            WindowFrame.Titled("circuitRF - Example_DC_Ohms_Law"),
            "The New User's Guide's first worked example: 10 V across 100 ohms, with a DC analysis."),

        new("example-sparam-schematic", DocExampleFixtures.SParamExampleSchematic,
            DocExampleFixtures.Square, DocExampleFixtures.Square,
            WindowFrame.Titled("circuitRF - Example_SParam_LC"),
            "The second worked example: a series 2 nH and a shunt 0.8 pF between two 50 ohm Terms."),

        new("example-sparam-plot", DocExampleFixtures.SParamExamplePlot,
            DocExampleFixtures.Square, DocExampleFixtures.Square,
            WindowFrame.Titled("circuitRF - Data Display"),
            "What that schematic produces: S(2,1) in dB against frequency, 1-5 GHz."),

        // Port is deliberately absent: it has no symbol. It is the abstract idea a Pin realises
        // inside a cell and a Term realises on a test bench, and the prose beside this says so.
        new("pin-and-term", DocExampleFixtures.PinAndTerm, 700, 300, null,
            "The two symbols that realise a port: Pin, a cell's connectivity-only interface "
          + "terminal, and Term, a numbered S-parameter port termination."),

        // ── Checking ──────────────────────────────────────────────────────────────

        new("drc-violations", DocVerifyFixtures.DrcViolations, 1100, 620, null,
            "A design-rule check on the MMIC starter process: a 2 um neck breaking minimum width and "
          + "a 2 um gap breaking minimum spacing, listed in the DRC panel and marked on the artwork."),

        new("manage-pdks", DocVerifyFixtures.ManagePdks, 640, 440,
            WindowFrame.Titled("Manage PDKs"),
            "Manage PDKs: the workspace's kit references, what each one resolved to, how many parts "
          + "it loaded, and the Add / Remove / Reveal / Validate actions."),

        new("pdk-import-report", DocVerifyFixtures.PdkImportReport, 660, 460,
            WindowFrame.Titled("Import PDK - AcmeRF GaAs-150"),
            "The report an import writes: what was read, what it holds, and the notes that go with "
          + "it. The kit is invented - real kits are licensed and none is in this repository - but "
          + "the report and the dialog rendering it are the application's own."),

        new("symbol-editor", DocFixtures.SymbolEditor, 1100, 700,
            WindowFrame.Titled("circuitRF — Symbol editor"),
            "The symbol editor, showing the SDD's variadic body and its pins."),

        new("layout-editor", DocLayoutFixtures.LayoutEditorWithArtwork, 1100, 700,
            WindowFrame.Titled("circuitRF - Layout editor"),
            "The layout editor: a microstrip run with a mitred bend, a crossing stub and a ground via."),

        // The Technology Editor's Stackup tab, on the shipped four-layer board technology.
        //
        // TALL ON PURPOSE, and the height is MEASURED, not chosen. A .ctech's stackup is a scrolling
        // list of cards under a drawing, and a capture at the height of a real docked window shows a
        // few of the nine entries with a scrollbar past them - which is the one thing a figure cannot
        // convey, because a reader cannot scroll a picture. So the height is the height at which the
        // whole tab fits with nothing clipped.
        //
        // 2224 = the title bar (34) + the boundary/Add/summary header (57) + the cross-section, which
        // is StackupScene's own intrinsic height at this pane's width (338) + the splitter (6) + the
        // filter row + all nine cards, ending at 2214 measured off the capture itself. It is NOT the
        // old 2080 nudged: the drawing is new above the cards and each conductor card lost the
        // ~52 px R-stk2-9 took out of its drawing-layer picker, and the two changes do not cancel.
        //
        // DocTechEditorFixtures sets the SPLIT through FigureScene.AfterLayout for the same reason -
        // at this height the interactive default would give the drawing ~1,800 px and leave eight
        // cards below the fold. See TechEditorView.ApplyStackupSplitForCapture.
        new("tech-editor-stackup", DocTechEditorFixtures.StackupTab, 980, 2224,
            WindowFrame.Titled("circuitRF - PCB 4-Layer FR-4 (62mil, 1/0.5oz)"),
            "The Technology Editor's Stackup tab on the shipped four-layer FR-4 technology. The "
          + "cross-section at the top is the primary surface: click a band to select it and land on "
          + "its fields below, double-click a value to edit it in place, drag to reorder a layer or "
          + "to move a via's span, right-click for the choices that are not typed numbers. The card "
          + "list under the splitter edits the same nine entries - four coppers, three dielectrics "
          + "and two vias that span different pairs of conductors - and the two always agree, "
          + "because they are two views of one stackup rather than two copies of it. The boundary "
          + "conditions and the three Add buttons are on the top row; the summary beneath them is "
          + "the stack height and what the stack is made of."),

        // The stackup in cross-section, drawn from the shipped MMIC technology rather than from a
        // hand-written list of bands — so the picture cannot outlive the thing it is a picture of.
        //
        // SIZE, R-stk8-7. Both numbers are the SCENE's, not a frame chosen here. 960 is the width at
        // which no label group wraps (DocStackupFixtures.Width - the MMIC process needs 949.7 and is
        // the widest of the three); 396 is StackupScene.Height at that width, 388.7, rounded up.
        // Neither may be nudged: the fixture reports its own intrinsic height and a frame smaller
        // than it clips the bottom band silently.
        new("stackup-mmic", DocStackupFixtures.MmicCrossSection, 960, 396, null,
            "An MMIC stackup in cross-section: two signal metals over a GaAs substrate, a backside "
          + "ground plane, the thin-film capacitor module between the metals, and the three vias "
          + "that connect them. The heavy edge marks the ground-designated conductor - the negative "
          + "terminal of every port in an EM run; the capacitor dielectric is marked with the plate "
          + "it is patterned with, which is what keeps it out of runs that have no capacitor in "
          + "them. Every thickness is printed. Heights are relative WITHIN a kind and never across "
          + "kinds - a conductor against a conductor, a dielectric against a dielectric - and this "
          + "process's dielectrics span 0.2 um to 100 um, which is too wide to draw even on its "
          + "own, so they are compressed while staying in order."),

        // MIM-7 — the capacitor module on its own, for the chapter's MIM section. A WINDOW on the
        // figure above, from the same real Technology object, because seven bands at reading size
        // make the 0.2 um film and its tie the two least legible things in the picture.
        // 280 is the scene's own height at 960 (248) plus the footer line under it - see
        // stackup-mmic above for why neither number is a frame chosen here.
        new("stackup-mim", DocStackupFixtures.MimModuleCrossSection, 960, 280, null,
            "The thin-film capacitor module in cross-section: the plate metal, the capacitor "
          + "dielectric under it - marked with the plate it is patterned with, which is what keeps "
          + "it out of runs that analyse no plate - and the plate via up to the routing metal. It "
          + "is a WINDOW on the figure above, so it states no boundary conditions: a slice of a "
          + "sandwich has none of its own. Every thickness is printed; heights are relative within "
          + "a kind and never across kinds."),

        // The two port types, drawn by the real renderer on real MKLOPF artwork. Landscape and the
        // same size as each other on purpose: they are read as a PAIR — the whole point is that the
        // two marks are not each other, and a reader can only see that if nothing else differs.
        new("ports-edge", DocLayoutFixtures.EdgePortsOnATaper, 562, 422, null,
            "Edge ports at both ends of a Klopfenstein taper: the bar across each end face is where "
          + "current crosses into the structure, and the arrow is which way it flows in."),

        new("ports-internal-gap", DocLayoutFixtures.InternalGapPortOnATaper, 882, 302, null,
            "A 50 ohm line with edge ports at both ends and an internal delta-gap port in the "
          + "middle - where a series component would go. The gap's mark is two bracketed bars facing "
          + "each other across a break in the metal, with the arrow running through the break: a cut "
          + "in the conductor, not a boundary of it."),

        // How an EM result with an internal gap port is actually used. Photographed rather than
        // described because the connection LOOKS like a shunt element and is not one.
        new("em-series-gap-cosim", DocExampleFixtures.EmSeriesGapCoSimulation, 720, 480,
            WindowFrame.Titled("circuitRF - Example_EM_SeriesGap"),
            "The EM result used in a schematic: the .s3p's ports 1 and 2 are the line's ends, and "
          + "the series capacitor sits on port 3 - the gap - where it acts in series in the metal."),

        new("ports-internal", DocLayoutFixtures.InternalPortOnALine, 882, 302, null,
            "The same 50 ohm line with an internal port at its centre - where a component that "
          + "returns to ground would attach. Its mark is a ring round the point with a ground "
          + "symbol on it: the port's other terminal is the ground plane, so its current leaves the "
          + "metal downward rather than crossing a plane in the layout, and the mark claims no "
          + "direction in the plane. A via is drawn here too, which the port then drives; without "
          + "one the solver builds that path itself."),

        new("ports-gap-mesh-width", DocLayoutFixtures.InternalGapPortAtMeshWidth, 882, 302, null,
            "The same gap once the mesh has been computed: the break is drawn at the width the solve "
          + "will actually use - the two mesh cells either side of the cut - so it can be read "
          + "against the gridlines under it. Without a mesh it reverts to a fixed legible width."),

        // ── Application note AN-01 ────────────────────────────────────────────────────────────
        //
        // The pair the note is about, drawn as it is SIMULATED. It was once drawn twice - once with
        // all four ports and once with two - because the note argued from the difference between
        // them; the rewrite of 2026-09-13 dropped that argument, and a figure of a coupled pair with
        // ports on only one of its lines is a trap for a designer scrolling past a page titled "put
        // a port on every end" (owner). Deleted rather than left unreferenced: an Id in this catalog
        // IS the placeholder key a page cites, and a row nothing cites rots quietly.
        new("an01-coupled-pair-4port", DocLayoutFixtures.CoupledPairFourPorts, 882, 192, null,
            "Two parallel microstrips with an edge port on each end of each line - 254 um wide, "
          + "3.83 mm long, 246 um apart. All four ports are present, so both lines are driven and "
          + "both are terminated in 50 ohms."),

        new("an01-coupled-pair-isolated-feeds", DocLayoutFixtures.CoupledPairIsolatedFeeds, 882, 468, null,
            "The same coupled section with 4 mm of line at each port and the other conductor held "
          + "6 mm away there - 6.38 substrate heights, against the 5 a neighbour carrying a port of "
          + "its own needs. Where the two ports of a plane cannot be calibrated together, this is "
          + "what gives each of them the isolated uniform feed the standard assumes."),

        new("an01-coupled-pair-coarse-mesh", DocLayoutFixtures.CoupledPairCoarseMesh, 882, 192, null,
            "The same pair meshed at cells per wavelength 5 with cells across 2, well below the 20 "
          + "and 4 the mesh opens at. One cell lands across each 254 um conductor and the cells "
          + "along the line are a large fraction of its length; two of the reference planes end up "
          + "inside the drawn metal."),

        // AN-01's RESULT figures. Static, because a full-wave sweep is not free and the answer does
        // not change between regenerations - see DocCoupledLineFixtures for the arrangement, which
        // is the antenna patterns' one.
        new("an01-coupled-pair-return-loss", DocCoupledLineFixtures.ReturnLoss, 820, 520,
            WindowFrame.Titled("circuitRF - Data Display"),
            "Return loss at both ends of the driven line, 1-7 GHz. S(1,1) and S(2,2) are the two "
          + "ends of one uniform 254 um conductor, so they lie on top of each other; a run in which "
          + "they separate has an asymmetry the geometry does not.",
            Static: true),

        new("an01-coupled-pair-through-coupled", DocCoupledLineFixtures.ThroughAndCoupled, 820, 520,
            WindowFrame.Titled("circuitRF - Data Display"),
            "Through and coupled on the same axes. S(2,1) is the far end of the driven line; S(3,1) "
          + "is the near end of its neighbour - the backward-coupled port, and the number a "
          + "coupled-line design is usually about.",
            Static: true),

        // ── The MoM chapter's conformal-boundary-cell section ────────────────────────────────────
        //
        // ONE row, not two, because the figure IS the comparison: two canvases in one picture, so a
        // reader cannot be looking at one of them under a caption about the other. The size is two
        // 430x330 panels plus the gap, their headings, and a little slack.
        new("mom-conformal-vs-staircase", DocConformalMeshFixtures.StaircaseVersusConformal,
            900, 372, null,
            "The same Klopfenstein taper meshed twice, differing in one setting. Left, staircase: a "
          + "cell is either in the metal or out of it, so the curved flank is approximated by whole "
          + "cells and the meshed outline is not the drawn one. Right, conformal: the boundary cells "
          + "are cut to follow the metal, and the meshed shape IS the drawn shape. The mesh is "
          + "coarser than the shipping default and edge refinement is off, so that the boundary "
          + "treatment is the only thing there is to see."),

        // ── The MoM chapter's worked example: a 50 ohm line with a right-angle bend ──────────────
        //
        // Five pictures of the DESIGN, in the order the page's own steps build it, and three of the
        // RESULT. The artwork is built by DocMomBendFixtures rather than read out of testdata/ -
        // that file's own note says why this page is the exception to the rule the antenna page
        // states. The frame is 560x620 on square artwork so the fit stays WIDTH-bound, which is the
        // regime DocLayoutFixtures.Framed is reliable in.

        new("mom-bend-layout", DocMomBendFixtures.Layout, 560, 620, null,
            "Step 1: two rectangles on Top Copper, 400 mil by 114 mil and 114 mil by 400 mil, "
          + "overlapping in the corner square that makes them one conductor. The square is left "
          + "unmitred deliberately - the bend is what the exercise measures."),

        // 264 is the scene's own height at 960 (259.8) - see stackup-mmic for why.
        new("mom-bend-stackup", DocStackupFixtures.PcbCrossSection, 960, 264, null,
            "Step 2: the stackup the PCB starter technology hands you, drawn in cross-section - the "
          + "same drawing the Technology Editor's Stackup tab puts above its cards. The FR-4 core "
          + "between the two coppers, the bottom one designated the ground reference (the heavy "
          + "blue edge), and the plated through-hole spanning them - drawn with its bore, because "
          + "it is a barrel and not a rod. Thicknesses are in the technology's OWN display unit, "
          + "which on this board is mil: 62.99 mil is the 1.6 mm the walkthrough quotes and 1.378 "
          + "mil is 1 oz copper. Heights are relative within a kind and never across kinds."),

        new("mom-bend-ports", DocMomBendFixtures.Ports, 560, 620, null,
            "Step 3: an edge port at the centre of each end face, with the bar marking where "
          + "current crosses into the structure and the arrow saying which way it flows in. A label "
          + "at a corner is equally close to two edges and is refused by name rather than guessed."),

        new("mom-bend-mesh-settings", DocMomBendFixtures.MeshSettings, 600, 560, null,
            "Step 4: the mesh settings, on their defaults, cropped out of the EM Setup panel. Under "
          + "them is the engine's own report - the unknown count, the cell count, the largest cell "
          + "and what set it, what the ports return through, and what the edge mesh cost - which is "
          + "what the Mesh button produces without solving anything."),

        new("mom-bend-mesh", DocMomBendFixtures.Mesh, 560, 620, null,
            "The same defaults, drawn: the tensor grid over the metal, with the edge mesh's graded "
          + "cells along every conductor edge and the wavelength pitch in the interior. Current "
          + "density has a 1/sqrt(d) singularity at an edge, and this is what resolving it costs."),

        // The three RESULT figures. Static, because a de-embedded full-wave sweep is not free and
        // the answer does not change between regenerations - the arrangement AN-01's own plots and
        // the antenna patterns use.
        new("mom-bend-smith", DocMomBendFixtures.SmithS11, 760, 620,
            WindowFrame.Titled("circuitRF - Data Display"),
            "Step 6: S(1,1) of the solved bend on its own Smith chart, 1-10 GHz. The locus leaves "
          + "the centre and runs clockwise below the real axis, which is what an unmitred corner's "
          + "excess shunt capacitance looks like when it is not yet a number.",
            Static: true),

        new("mom-bend-mag-phase", DocMomBendFixtures.MagnitudeAndPhase, 880, 560,
            WindowFrame.Titled("circuitRF - Data Display"),
            "The same run against frequency: |S(1,1)| and |S(2,1)| in dB on the left axis, and "
          + "their phases in degrees - dashed - on the right. Two quantities whose ranges have "
          + "nothing to do with each other share a plot only because the trace card can move one of "
          + "them to the right-hand axis.",
            Static: true),

        new("mom-bend-em-vs-circuit", DocMomBendFixtures.EmVersusCircuit, 880, 560,
            WindowFrame.Titled("circuitRF - Data Display"),
            "Step 7: the same S(1,1), solved and modelled. The solid traces are the EM run; the "
          + "dashed ones are MLIN-MBEND-MLIN with the same width, the same 286 mil arms and the "
          + "same substrate, picked per trace through the trace card's own Source combo. Magnitude "
          + "on the left, phase on the right.",
            Static: true),

        new("layout-rulers", DocLayoutFixtures.LayoutRulers, 1100, 700,
            WindowFrame.Titled("circuitRF - Layout editor"),
            "Three ruler annotations on the same artwork: a trace width, a free-angle clearance "
          + "carrying a caption and its dx/dy components, and a Scaled-text ruler across the whole "
          + "run. A ruler is not geometry and never reaches a manufacturing file."),

        new("snap-glyphs", DocLayoutFixtures.SnapGlyphs, 1010, 190, null,
            "The six geometry-snap glyphs, each drawn by the editor's own renderer from a real query."),

        // ── The land patterns the case table generates ────────────────────────────
        // Both are one layout view rather than a row of canvases, so the four sizes share a scale —
        // see DocFootprintFixtures for why that is the whole point of the first one.

        new("footprint-case-sizes", DocFootprintFixtures.CaseSizes, 704, 146, null,
            "Four generated land patterns at the nominal density, all at one scale: 0402, 0805, "
          + "1206 and the 3216-18 moulded tantalum. Copper lands, their soldermask openings, the "
          + "two silkscreen lines and the courtyard rectangle, on the shipped 2-layer PCB "
          + "technology."),

        new("footprint-densities", DocFootprintFixtures.Densities, 482, 132, null,
            "One case, 0805, at the three IPC-7351B density levels. The part is the same in all "
          + "three; what the level sets is how far the land reaches past it — 0.55 mm of toe at M, "
          + "0.35 mm at N and 0.15 mm at L."),

        new("data-display", DocFixtures.DataDisplay, 820, 600,
            WindowFrame.Titled("circuitRF — Data Display"),
            "The Data Display document."),

        new("em-setup-editor", DocFixtures.EmSetup, 640, 790,
            WindowFrame.Titled("EM Setup"),
            "The EM Setup editor: stackup, ports and the frequency sweep."),

        // The same editor in a narrower, shorter window, for a slide.
        //
        // The width is NOT what was making it small: on a half-slide the fit is bound by HEIGHT on
        // every sensible width up to ~700, so trimming 640 to 520 bought nothing in scale and cost
        // enough room that the panel's own toolbar row began to overlap itself. The 790 -> 620 trim is
        // the part that pays — 1.26x the on-slide scale — and 560 is as narrow as the toolbar lays out
        // cleanly. Analysis, conductors, the frequency sweep and the ports are above the fold; the
        // mesh section is below it, which is what a short window of this panel genuinely looks like.
        new("em-setup-compact", DocFixtures.EmSetup, 560, 620,
            WindowFrame.Titled("EM Setup"),
            "The EM Setup editor at the width of a docked panel: analysis, conductors, the frequency "
          + "sweep and the ports, with the mesh section below the fold."),

        // ── ANT-12's example antenna, read out of testdata/antenna/ rather than rebuilt here ──────
        //
        // Three figures because the page asks three different things of the artwork: what the whole
        // board is, what the feed a reader has to copy looks like up close, and what the port setup is
        // in the panel that owns it. The sizes are the artwork's own aspect — the patch is 16.94 x
        // 13.30 mm inside a 40 x 40 mm pour, so a square-ish frame is what fits it without slack.
        new("antenna-patch-layout", DocAntennaFixtures.PatchLayout, 760, 660, null,
            "The shipped 5.8 GHz example: a 16.94 x 13.30 mm inset-fed patch on top copper, the "
          + "40 x 40 mm ground pour under it, and one edge port on the feed's end face. The pour is "
          + "drawn so the run can report how large the real plane is; the ANALYSIS still terminates on "
          + "a laterally infinite one."),

        new("antenna-patch-feed", DocAntennaFixtures.PatchFeed, 560, 380, null,
            "The port, close up: the bar across the 1.68 mm feed's end face is where current crosses "
          + "into the structure, and the arrow is which way it flows in. Everything outside that plane "
          + "is the port discontinuity, and the two-line calibration removes it."),

        new("antenna-patch-em-setup", DocAntennaFixtures.PatchEmSetup, 560, 1560,
            WindowFrame.Titled("EM Setup - patch-5p8GHz"),
            "The example's EM Setup, from the top down to the mesh: the kernel the registry chose and "
          + "why, the conductor level, the sweep with adaptive sampling and the resonance search on, "
          + "and the resolved port with its side, its reference plane and its impedance."),

        // A CROP of the same panel, for the reason FigureCrop exists: the control this page is about
        // is one checkbox near the bottom of a panel 2,000 px tall, and in the figure above it is
        // below the fold. No chrome - it is the inside of a panel, not a window.
        new("antenna-radiation-pattern", DocAntennaFixtures.RadiationPatternControl, 520, 150, null,
            "The one control that turns an ordinary planar run into an antenna run. Off by default, "
          + "because it is only meaningful on a radiator; it changes no s-parameter and no mesh cell, "
          + "and it disables itself with a reason when the pattern could not be computed."),

        // ── The example antenna, SOLVED ──────────────────────────────────────────
        // The only figures in this catalog that need a full-wave sweep behind them. They are all
        // three of ONE run of the shipped testdata/antenna/ example — see DocAntennaFixtures.ExampleResult,
        // which also records what that run costs and why the generator is built Release.

        new("antenna-pattern-cuts", DocAntennaFixtures.PatternCuts, 620, 640,
            WindowFrame.Titled("circuitRF - Data Display"),
            "The two principal-plane cuts of the 5.8 GHz example patch at 5.85 GHz, normalised so "
          + "the outer ring is this pattern's own peak and each ring is 10 dB down. The broader "
          + "trace is the E-plane (phi = 90/270 deg, the plane containing the current); the narrower "
          + "one is the H-plane (phi = 0/180 deg). Each is ONE whole-plane trace, which is why the "
          + "curve crosses broadside instead of stopping at it, and the bearings round the rim are "
          + "the Angles switch beside the dB-radial one.",
            Static: true),

        new("antenna-pattern-3d", DocAntennaFixtures.Pattern3D, 740, 500,
            WindowFrame.Titled("circuitRF - Data Display"),
            "The same pattern as a 3D surface, at the same frequency: radius and colour are both the "
          + "radiation intensity, here over a 20 dB range. The hemisphere is the whole of it - with a "
          + "laterally infinite ground plane the field below the plane is identically zero, so there "
          + "is no lower half to draw - and the shape is one broad lobe with no sidelobe and no null "
          + "anywhere but exact grazing. Drag to rotate; the named views put either principal plane "
          + "in the screen.",
            Static: true),

        new("antenna-efficiency-sweep", DocAntennaFixtures.EfficiencySweep, 800, 480,
            WindowFrame.Titled("circuitRF - Data Display"),
            "Radiation efficiency in decibels across the example's 5.3-6.3 GHz sweep: 22.6 % at the "
          + "bottom of the band, 69.3 % at its best, and 62.6 % at the 5.85 GHz the worked example "
          + "quotes. The two points off the 50 MHz grid are the resonances the resonance search "
          + "added, and the peak is at the upper one of them - the parallel resonance, not the "
          + "series resonance the feed is matched at.",
            Static: true),

        new("em-setup-loaded", DocLayoutFixtures.EmSetupWithLayout, 520, 1430,
            WindowFrame.Titled("EM Setup - bend"),
            "The same panel with a layout resolved: the kernel the registry chose and why, the "
          + "resolved ports, the mesh report and the technology's stackup."),

        // ── Settings, one figure per tab ──────────────────────────────────────────
        // 720x506 is the dialog's OWN declared size (720x540) less the synthetic title bar, so a
        // reader is looking at the window their own build opens rather than a re-proportioned one.
        // The tab strip is in every figure: it is how the page's six sections are told apart.

        new("settings-general", DocSettingsFixtures.General,
            DocSettingsFixtures.Width, DocSettingsFixtures.Height,
            WindowFrame.Titled("circuitRF Settings"),
            "Settings, General: what circuitRF does on launch, how a copied picture is coloured, "
          + "whether an export is design-rule checked first, and how the Messages panel stamps its "
          + "lines. Help at the leading edge of the footer opens this chapter."),

        new("settings-technology", DocSettingsFixtures.Technology,
            DocSettingsFixtures.Width, DocSettingsFixtures.Height,
            WindowFrame.Titled("circuitRF Settings"),
            "Settings, Technology: every technology File > New Workspace offers, what the selected "
          + "one is made of, and which one new workspaces open on. Your own .ctech files are added "
          + "here and are offered from then on; the ones circuitRF ships cannot be removed."),

        new("settings-security", DocSettingsFixtures.Security,
            DocSettingsFixtures.Width, DocSettingsFixtures.Height,
            WindowFrame.Titled("circuitRF Settings"),
            "Settings, Security and Permissions: everything that decides what circuitRF is allowed "
          + "to run or to fetch - a kit's artwork scripts, a kit's device worker, automatic updates, "
          + "and the Verilog-A compiler. Each control carries its explanation as a tooltip."),

        new("settings-revision-control", DocSettingsFixtures.RevisionControl,
            DocSettingsFixtures.Width, DocSettingsFixtures.Height,
            WindowFrame.Titled("circuitRF Settings"),
            "Settings, Revision Control: which git circuitRF runs, who changes are attributed to, "
          + "whether a history is kept for your workspaces and for this one, how long restore points "
          + "are kept, and the two disk-space actions. Every control that reduces what is kept carries "
          + "its consequence in a sentence beside it."),

        new("settings-color-theme", DocSettingsFixtures.ColorTheme,
            DocSettingsFixtures.Width, DocSettingsFixtures.Height,
            WindowFrame.Titled("circuitRF Settings"),
            "Settings, Color Theme: every colour role the application paints with, edited one at a "
          + "time in RGBA or hex, separately for the light and the dark variant."),

        new("settings-wirebonds", DocSettingsFixtures.Wirebonds,
            DocSettingsFixtures.Width, DocSettingsFixtures.Height,
            WindowFrame.Titled("circuitRF Settings"),
            "Settings, Wirebonds: the defaults a newly drawn wire is created with, and the built-in "
          + "assembly rule for how close two wires may pass."),

        new("settings-3d-em", DocSettingsFixtures.Em3dSolvers,
            DocSettingsFixtures.Width, DocSettingsFixtures.Height,
            WindowFrame.Titled("circuitRF Settings"),
            "Settings, 3D EM: one row per program a 3D EM run can need - Palace, Gmsh and openEMS - "
          + "with a path to name it and a line saying what circuitRF found. Blank means search."),

        // ── The 3D EM guide: one section through each cell of examples/3D EM/ ─────────────
        // Drawn by Em3dSectionRenderer from the example's own .cem, exactly as `render --section
        // xz@y=0um` draws it. The 3D VIEW's pictures are not here: they need a GPU and a window, so
        // the page names placeholders for them instead (brief-em3d-30 R-em3d30-2b).

        new("em3d-bond-wire-section", DocEm3dFixtures.BondWireSide, 960, 540, null,
            "The bond-wire cell cut along the wire: two gold pads on 100 um of alumina, the 1 mil "
          + "wire's loop between them, and a port sheet from each pad's outer edge down to the "
          + "ground. The box around it is the closed metal box the setup states."),

        new("em3d-via-section", DocEm3dFixtures.ViaSide, 960, 540, null,
            "The via cell cut along the lines: a microstrip on top, the via through a clearance in "
          + "the ground plane, and an inverted microstrip underneath leaving the other way. The "
          + "plane is the return for both lines, which is what the planar solver cannot state."),

        new("em3d-package-section", DocEm3dFixtures.PackageSide, 960, 540, null,
            "The package cell cut along its leads: a lead on each side, a bond wire from each lead "
          + "to a die pad, the alumina base, and the lid - the top face of the closed box - above "
          + "them."),

        new("cv-editor", DocCvFixtures.Editor, 620, 500,
            WindowFrame.Titled("C-V Editor - C1"),
            "The C-V Editor: a measured C(V) table, the fit order, and the polynomial it fits."),

        // ── Match ─────────────────────────────────────────────────────────────────

        new("match-designer", DocMatchFixtures.Designer, 1280, 860,
            WindowFrame.Titled("Match - MN1"),
            "The Match Designer on the design a freshly placed Match carries: the specification "
          + "pane, the solutions list under it, the synthesised ladder with its value grid, and the "
          + "transform rack."),

        new("match-interstage", DocMatchFixtures.Interstage2Stage, 1280, 860,
            WindowFrame.Titled("Match - MN1"),
            "The two-stage interstage example, solved: 200 ohm || 0.125 pF into 1.25 ohm + 10 pF over "
          + "3.3-5.0 GHz, with the solution applied and the element values it produces."),

        new("match-solutions", DocMatchFixtures.Solutions, 1280, 860,
            WindowFrame.Titled("Match - MN1"),
            "The solutions list, slid out: every valid transform set, simplest first."),

        new("match-dualband", DocMatchFixtures.DualBandDesigner, 1280, 860,
            WindowFrame.Titled("Match - MN1"),
            "The dual-band worked example: 200 ohm || 0.125 pF into 1.25 ohm + 10 pF, matched over "
          + "1.75-1.9 GHz and 2.1-2.2 GHz together at three match points per band. The band-2 edge "
          + "has been widened to mirror band 1, the solutions list is out with the applied card "
          + "checked, and the ladder carries both terminations as absorbed elements."),

        // 1120x700 rather than the Designer's own pane: this is the ONE figure on the Match page a
        // reader is expected to read numbers off (two passbands, a gap, and where |S11| crosses), and
        // the pane's golden-ratio plot inside a scroll view is roughly a third of this area.
        new("match-dualband-response", DocMatchFixtures.DualBandResponse, 1120, 700, null,
            "The dual-band example's response, plotted at +/-20% of the band: |S11| against the left "
          + "axis, |S21| against the right. Both passbands are matched; the region between them is "
          + "not, and that is the design working rather than failing."),

        // ── railRF ──────────────────────────────────────────────────────────────
        // 1600x780 rather than the window's own 1199x741. The three panes are 300 / star / 340 with
        // a canvas between them, so the width has to leave the middle one enough to be a board.
        // (The map legend used to need this width too -- it is a box in DBU with screen-sized text
        // in it, so below about 1500 its three labels ran into each other. R-rail18-4 scales the
        // text to the plate, so that is no longer what sets the width.)

        new("railrf-window", DocRailFixtures.Window, 1600, 780,
            WindowFrame.Titled("railRF - Sensor board"),
            "The railRF window on the shipped Power Rail example, run: the rail, its reference and "
          + "its ports down the left, the board in the middle showing the drop map, and the DC "
          + "answer with its ranked breakdown on the right."),

        // R-rail18-3: the reference is drawn FIRST now, so the rail's own trace sections survive the
        // opaque paint. Until then this figure was one flat rectangle -- the reference's region was
        // classified last and painted over everything -- on every board with a plane, which is every
        // board this feature is for.
        new("railrf-classification", DocRailFixtures.Classification, 1600, 780,
            WindowFrame.Titled("railRF - Sensor board"),
            "The class tab, which draws what the fast model decided about each piece of copper. "
          + "The reference plane is spreading copper and was meshed; the rail's own sections are "
          + "drawn over it, each in the colour of the class it was priced as."),

        new("railrf-impedance", DocRailFixtures.Impedance, 1600, 780,
            WindowFrame.Titled("railRF - Sensor board"),
            "The frequency half of the results column: the impedance at the observation port "
          + "against its target, the anti-resonances the run named, and what removing each "
          + "capacitor would cost."),

        // ── The Smith Chart document ────────────────────────────────────────────
        // 1400x920 rather than the shell's 1200x800. The chart pane takes 65% of the height by the
        // document's own splitter, so the generator column beside it gets that same 65% to fit six
        // cards into: at 860 the Swept band card's last row was cut off by the panel's own scroll
        // view, which in a figure reads as a broken panel rather than as one you can scroll. No
        // WindowFrame on any of these — a docked document is the INSIDE of a window, and the tab
        // strip above it belongs to the shell.

        new("smith-window", DocSmithFixtures.Window, 1400, 920, null,
            "The Smith Chart document on the shipped Smith Chart example: the generator table over "
          + "three frequencies, the two trajectories of an L match with a gripper at each joint, the "
          + "three load points with their conjugate targets, the swept band through them, and the "
          + "network the walk is a picture of."),

        new("smith-trajectories", DocSmithFixtures.Trajectories, 1400, 920, null,
            "Three elements and therefore three curves: a series inductor walking a "
          + "constant-resistance circle, a shunt capacitor walking a constant-conductance circle, "
          + "and a 75 ohm quarter-wave line taking 50 ohms to 112.5 - a half turn about the LINE's "
          + "own impedance, not about the chart's centre. The arrowhead on each says which way the "
          + "walk runs; the ring at each joint is the gripper that drags the element it belongs to."),

        new("smith-constant-q", DocSmithFixtures.ConstantQ, 1400, 920, null,
            "The same walk with the constant-Q pair switched on at Q = 1.75. Every point on the two "
          + "arcs has |x|/r = Q, so a joint inside them is a wider-band network and a joint outside "
          + "them is a narrower one."),

        new("smith-network-strip", DocSmithFixtures.NetworkStrip, 820, 260, null,
            "The network strip as the example draws it: the generator on the left, the cascade "
          + "growing rightward, one ground under each shunt column, and the load at the far end."),

        new("smith-network-mirrored", DocSmithFixtures.NetworkStripMirrored, 820, 260, null,
            "The same network after the mirror button. The drawing and the symbols are reflected; "
          + "the element order, the circuit and the chart are not."),

        new("smith-copied-schematic", DocSmithFixtures.CopiedSchematic, 1100, 700,
            WindowFrame.Titled("circuitRF - Matched input"),
            "What Copy on the network strip puts into a schematic: the same parts at the same "
          + "coordinates, with the generator end terminated as port 1 carrying the generator "
          + "impedance at the design frequency and the load end as port 2 carrying the chart's Z0."),

        new("match-form-glyphs", DocMatchFixtures.FormGlyphs, 780, 150, null,
            "The five Match glyphs. A slash across a wave means that part of the spectrum is blocked; "
          + "two or three smaller bandpass groups mean two or three bands."),

        // ── wBond: the views a WORKSPACE produces ─────────────────────────────────
        // In a workspace a wBond is the wire layer of a layout cell, not a separate application
        // window (owner, 2026-08-20). All three of these are built on ONE four-array design.

        new("wbond-layout", DocWBondFixtures.LayoutWires, 1100, 700,
            WindowFrame.Titled("circuitRF - Layout editor"),
            "Four bond arrays - G1, G2, D1, D2 - and their ten wires, drawn over their pads in the "
          + "layout editor."),

        new("wbond-profile", DocWBondFixtures.Profile, 900, 380, null,
            "The Wire Profile panel: the same wires from the side, where loop height and span are "
          + "the two things you can see."),

        new("wbond-inductance", DocWBondFixtures.InductancePanel, 320, 360, null,
            "The Array Inductance panel, computed from those ten wires."),

        new("wbond-symbol-arrays", DocWBondFixtures.Symbol, 620, 460,
            WindowFrame.Titled("circuitRF - Schematic"),
            "The schematic symbol the same design generates: one pin pair per array, named after it."),

        new("wbond-sparameters", DocWBondFixtures.SParameters, 850, 540,
            WindowFrame.Titled("circuitRF - Data Display"),
            "The array network exported to Touchstone and plotted: 0.1-20 GHz, terminal basis."),

        new("wbond-editor", DocFixtures.WBondEditor, 1100, 700,
            WindowFrame.Titled("wBond"),
            "The standalone wBond application, carrying the shipped default wirebond design."),

        // ── harmonicaRF: the instrument, solved ───────────────────────────────────

        new("harmonica-instrument", DocHarmonicaFixtures.Instrument, 1500, 950,
            WindowFrame.Titled("harmonicaRF"),
            "harmonicaRF on its default document: power and efficiency contours on the load plane, "
          + "the loadline, the power sweep, and the readout strip."),

        new("harmonica-readout-strip", DocHarmonicaFixtures.ReadoutStrip, 970, 300, null,
            "The readout strip: settings on the left, then the source and load markers, then the "
          + "grid's best-power and best-efficiency summaries."),

        // ── Data Display: plots that contain data, and trace cards pointed at it ──
        // Every one of these runs a shipped test bench for real (DocRunData) rather than drawing an
        // empty axis frame. See DocDataDisplayFixtures.

        new("plot-rectangular-data", DocDataDisplayFixtures.RectangularWithData, 850, 540,
            WindowFrame.Titled("circuitRF - Data Display"),
            "A rectangular plot of the shipped FET test bench's S-parameters, 1-10 GHz."),

        new("plot-smith-data", DocDataDisplayFixtures.SmithWithData, 560, 620,
            WindowFrame.Titled("circuitRF - Data Display"),
            "A Smith chart carrying the FET test bench's own S(1,1), swept 1-10 GHz."),

        new("plot-polar-data", DocDataDisplayFixtures.PolarWithData, 560, 620,
            WindowFrame.Titled("circuitRF - Data Display"),
            "The same run on a polar plot: magnitude and angle, without the impedance grid."),

        new("plot-table-data", DocDataDisplayFixtures.TableWithData, 700, 480,
            WindowFrame.Titled("circuitRF - Data Display"),
            "A table of the same run, a complex column beside a scalar one."),

        new("plot-loadpull-contours", DocDataDisplayFixtures.LoadpullContours, 700, 620,
            WindowFrame.Titled("circuitRF - Data Display"),
            "Load-pull contours on the Gamma plane, interpolated from a 61-point termination grid."),

        new("plot-inspector-trace-card", DocDataDisplayFixtures.InspectorTraceCard, 440, 376,
            null,
            "The Plot Inspector: a trace card reading S(2,1) from a swept S-parameter run."),

        new("plot-inspector-passive", DocDataDisplayFixtures.InspectorPassiveReadout, 440, 376,
            null,
            "A trace card reading ESR from a two-port part file, with the Fixture control that "
          + "decides how the two-terminal impedance is recovered."),

        new("plot-inspector-smith", DocDataDisplayFixtures.InspectorSmith, 440, 376,
            null,
            "The trace card behind the Smith figure: the same run, the same S(1,1), on a Smith chart."),

        new("plot-inspector-hb", DocDataDisplayFixtures.InspectorHb, 440, 350,
            null,
            "A trace card configured against a harmonic-balance drive sweep."),

        new("plot-inspector-loadpull", DocDataDisplayFixtures.InspectorLoadpull, 440, 320,
            null,
            "A contour trace card: the metric, the constraint, the levels and the interpolation."),

        new("plot-inspector-wsprobe", DocDataDisplayFixtures.InspectorWsProbe, 440, 420,
            null,
            "The trace card's WSProbe section: which probe the quantity is taken at, which of the "
          + "reference document's quantities it is, and the Kurokawa reading beside it."),

        // ── The WSProbe chapter's own figures (brief-wsprobe-7 §4) ────────────
        //
        // Each is a real run of a committed testdata/ netlist — see DocWsProbeFixtures, whose
        // catalogue names the page section and the brief gate every design serves.

        // A plot each, not two traces on one: 1/H0 is an admittance and 1/Y0 an impedance, so on a
        // shared radius one of them is a dot on the origin (see DocWsProbeFixtures.ResonatorPolar).
        new("wsprobe-resonator-polar", DocWsProbeFixtures.ResonatorPolar, 1060, 600,
            WindowFrame.Titled("circuitRF - Data Display"),
            "The two driving-point loci of an unstable series resonator, a polar plot each. 1/Y0 "
          + "crosses the negative real axis clockwise at the resonance; 1/H0 never does. Both must "
          + "be checked, because a zero can mask the pole in one of them but never in both."),

        new("wsprobe-margin-resonator", DocWsProbeFixtures.MarginResonator, 850, 560,
            WindowFrame.Titled("circuitRF - Data Display"),
            "Both stability margins of a series resonator with the reactance split across the "
          + "probe: SM_Y0 notches at the resonance and SM_H0's minimum is somewhere else entirely "
          + "- pole masking, in margin form. The analysis' threshold and the -12 dB floor are drawn "
          + "beneath them."),

        new("wsprobe-envelope-card", DocWsProbeFixtures.EnvelopeCard, 440, 520,
            null,
            "The trace card's Envelope sub-card: the probe each side is pulled at, a ladder of "
          + "|Gamma| per side, the angular step, and the number of terminations it is about to "
          + "compute."),

        new("wsprobe-margin-envelope-ohtomo", DocWsProbeFixtures.MarginEnvelopeOhtomo, 850, 560,
            WindowFrame.Titled("circuitRF - Data Display"),
            "The margin envelope and the NDF's encirclement count over the same terminations, on "
          + "one pair of axes. The NDF answers yes or no; the margin says how close."),

        new("wsprobe-hb-fan", DocWsProbeFixtures.HbFan, 850, 560,
            WindowFrame.Titled("circuitRF - Data Display"),
            "The stability margin of a pumped varactor against the small-signal probe frequency, "
          + "one curve per pump level. The collapse at half the fundamental is a parametric "
          + "instability, which is the case no linear analysis can see."),

        new("wsprobe-ndf-k", DocWsProbeFixtures.NdfK, 850, 560,
            WindowFrame.Titled("circuitRF - Data Display"),
            "Rollett's K of a two-port whose terminal S-parameters are those of a 6 dB pad, above "
          + "1 across the band, beside the NDF's encirclement count of the loop those terminal "
          + "S-parameters cannot see."),
    ];
}

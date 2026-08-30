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
    public readonly record struct Row(
        string Id,
        Func<FigureScene> Build,
        int Width,
        int Height,
        WindowFrame? Chrome,
        string Caption,
        bool MustContainPopup = false);

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

        // The stackup in cross-section, drawn from the shipped MMIC technology rather than from a
        // hand-written list of bands — so the picture cannot outlive the thing it is a picture of.
        new("stackup-mmic", DocStackupFixtures.MmicCrossSection, 862, 290, null,
            "An MMIC stackup in cross-section: two signal metals over a GaAs substrate, a backside "
          + "ground plane, and the two vias that connect them. The heavy edge marks the "
          + "ground-designated conductor - the negative terminal of every port in an EM run. "
          + "Thicknesses are printed rather than drawn to scale."),

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

        new("layout-rulers", DocLayoutFixtures.LayoutRulers, 1100, 700,
            WindowFrame.Titled("circuitRF - Layout editor"),
            "Three ruler annotations on the same artwork: a trace width, a free-angle clearance "
          + "carrying a caption and its dx/dy components, and a Scaled-text ruler across the whole "
          + "run. A ruler is not geometry and never reaches a manufacturing file."),

        new("snap-glyphs", DocLayoutFixtures.SnapGlyphs, 1010, 190, null,
            "The six geometry-snap glyphs, each drawn by the editor's own renderer from a real query."),

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

        new("em-setup-loaded", DocLayoutFixtures.EmSetupWithLayout, 520, 1430,
            WindowFrame.Titled("EM Setup - bend"),
            "The same panel with a layout resolved: the kernel the registry chose and why, the "
          + "resolved ports, the mesh report and the technology's stackup."),

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

        new("plot-inspector-smith", DocDataDisplayFixtures.InspectorSmith, 440, 376,
            null,
            "The trace card behind the Smith figure: the same run, the same S(1,1), on a Smith chart."),

        new("plot-inspector-hb", DocDataDisplayFixtures.InspectorHb, 440, 350,
            null,
            "A trace card configured against a harmonic-balance drive sweep."),

        new("plot-inspector-loadpull", DocDataDisplayFixtures.InspectorLoadpull, 440, 320,
            null,
            "A contour trace card: the metric, the constraint, the levels and the interpolation."),
    ];
}

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

        new("symbol-editor", DocFixtures.SymbolEditor, 1100, 700,
            WindowFrame.Titled("circuitRF — Symbol editor"),
            "The symbol editor, showing the SDD's variadic body and its pins."),

        new("layout-editor", DocLayoutFixtures.LayoutEditorWithArtwork, 1100, 700,
            WindowFrame.Titled("circuitRF - Layout editor"),
            "The layout editor: a microstrip run with a mitred bend, a crossing stub and a ground via."),

        new("snap-glyphs", DocLayoutFixtures.SnapGlyphs, 1010, 190, null,
            "The six geometry-snap glyphs, each drawn by the editor's own renderer from a real query."),

        new("data-display", DocFixtures.DataDisplay, 820, 600,
            WindowFrame.Titled("circuitRF — Data Display"),
            "The Data Display document."),

        new("em-setup-editor", DocFixtures.EmSetup, 640, 790,
            WindowFrame.Titled("EM Setup"),
            "The EM Setup editor: stackup, ports and the frequency sweep."),

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
            "The Match Designer on the interstage example a freshly placed Match carries: "
          + "specification, the synthesised ladder, the response, and the transform rack."),

        new("match-interstage", DocMatchFixtures.Interstage2Stage, 1280, 860,
            WindowFrame.Titled("Match - MN1"),
            "The two-stage interstage example, solved: 200 ohm || 0.125 pF into 1.25 ohm + 10 pF over "
          + "3.3-5.0 GHz, with the synthesised element values and the response it achieves."),

        new("match-solutions", DocMatchFixtures.Solutions, 1280, 860,
            WindowFrame.Titled("Match - MN1"),
            "The solutions list, slid out: every valid transform set, simplest first."),

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

        new("plot-inspector-hb", DocDataDisplayFixtures.InspectorHb, 440, 350,
            null,
            "A trace card configured against a harmonic-balance drive sweep."),

        new("plot-inspector-loadpull", DocDataDisplayFixtures.InspectorLoadpull, 440, 320,
            null,
            "A contour trace card: the metric, the constraint, the levels and the interpolation."),
    ];
}

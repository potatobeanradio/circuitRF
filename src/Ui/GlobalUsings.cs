// The namespaces that moved to CircuitRF.Design when the EM setup pipeline crossed the UI firewall
// (brief-cli-em-verb.md R-emcli-1/R-emcli-4).
//
// R-emcli-4 required the namespaces to change with the project — `CircuitRF.Ui.Layout` inside a
// non-UI assembly would have lied about the architecture forever. What it did NOT require is that
// the resulting `using` churn be spread across ~300 files: every file that was IN
// `namespace CircuitRF.Ui.Layout` saw those types implicitly, so the mechanical fix is one line per
// namespace, said once, here.
//
// **This file is the map.** A type that seems to appear from nowhere in src/Ui is from one of these,
// and the assembly it lives in is the one the firewall test gates. Adding to this list is not free —
// a global using that shadows a local type is exactly the kind of surprise it exists to avoid — so
// keep it to namespaces src/Ui genuinely consumes wholesale.

global using CircuitRF.Design.Cells;
global using CircuitRF.Design.Layout;
// The `.wasm` assembly rule-file model and the DRC ENGINE that reads it, which crossed the wall in
// AUT-4 (brief-automation-4-check-and-explain.md R-aut4-3) so `circuitrf check` can run design rules
// with no display. What stayed in src/Ui is the two files that are not the engine: `DrcRunReport`
// (posts a run to the Messages panel) and `WBondWireClearance` (reads a per-USER preference).
global using CircuitRF.Design.Layout.Assembly;
global using CircuitRF.Design.Layout.Drc;
global using CircuitRF.Design.Layout.Interchange;
global using CircuitRF.Design.Layout.Em;
global using CircuitRF.Design.Layout.PCells;
global using CircuitRF.Design.Layout.Footprints;
global using CircuitRF.Design.Results;
global using CircuitRF.Design.Theming;
global using CircuitRF.Design.Workspace;

// The schematic and symbol DOCUMENT model, which crossed the same wall for the same reason
// (brief-automation-2-schematic-below-the-firewall.md R-aut2-5/R-aut2-6). The editors, canvases and
// sessions stayed here; what moved is the `.csch`/`.csym` model, their persistence, their geometry
// and net extraction — so `src/Cli` can read a schematic and produce a netlist with no Avalonia on
// the path.
global using CircuitRF.Design.Schematic;
global using CircuitRF.Design.Symbol;

// The Skia renderers and the small framework-free types they read, which crossed the same wall in
// RND-1 (brief-render-1-render-layer-below-the-firewall.md R-rnd1-1) so `circuitrf render` can draw
// what the application draws rather than something that resembles it. What moved is every renderer
// and its theme, the OVERLAY descriptions of a frame's transient chrome (LayoutOverlay,
// SchematicOverlay, SymbolEditorOverlay), the hit-test/handle/snap geometry the renderer and the
// editors share, and the colour theme model with its `.ccolor` reader. The editors, view models,
// canvases and commands all stayed here, as did AppPreferences and ClipboardRenderPolicy, which read
// a per-USER preference store.
global using CircuitRF.Render;

// The Data Display's MODELS and RENDERERS, which crossed the same wall in RND-4
// (brief-render-4-data-display.md R-rnd4-1/R-rnd4-2) so `circuitrf render` can draw a `.cdd` with
// the code the window draws it with. What moved is Plot/Trace/Axes/Marker and their config model,
// the eight Skia renderers, and the parsers and resolvers a trace is resolved through. What stayed
// is every VIEW MODEL, every control, the undo stack and the exporter's file dialog — this
// namespace draws and resolves; it does not edit. The measurement that decided the line is in
// src/Render/RESOLVED.md.
global using CircuitRF.Render.DataDisplay;

// The Smith Chart's PLOT half, which crossed the same wall in SMITH-10
// (brief-smith-10-cli-verb.md R-smith10-3) so `circuitrf smith` draws the chart the window draws
// rather than a second one that would drift. What moved is the scene builder, the trace fill, the
// marker bridge, the overlay resolver and the transient chrome's DRAW calls. What stayed here is
// the gesture — SmithGripperOverlay's hit test, press and drag — and every view model beside it.
global using CircuitRF.Render.Smith;

// The engineering value formatter, which moved to CircuitRF.Design in the same change and for the
// same reason: a load point's frequency label is drawn below the firewall and reported above it, and
// two spellings of one frequency is two answers.
global using CircuitRF.Design.Matching;


// The one place a STORED relative reference is turned into a filesystem path and back, aliased
// rather than imported wholesale: `CircuitRF.Core`'s root namespace also holds `ComponentModel`,
// which would shadow `System.ComponentModel` in every file here that names it.
global using RefPath = CircuitRF.Core.RefPath;

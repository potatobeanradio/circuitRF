// The mirror of src/Ui/GlobalUsings.cs — see that file for why the namespaces that moved to
// CircuitRF.Design are pulled in once rather than file by file.
//
// Gate 2 of brief-cli-em-verb.md asks that the layout/EM tests pass UNCHANGED, as the evidence that
// the move was mechanical. This file is what makes that literally true: no test's assertions, setup
// or fixtures were touched to accommodate the new assembly.

global using CircuitRF.Design.Cells;
global using CircuitRF.Design.Layout;
global using CircuitRF.Design.Layout.Assembly;
global using CircuitRF.Design.Layout.Drc;
global using CircuitRF.Design.Layout.Extraction;
global using CircuitRF.Design.Layout.Em;
global using CircuitRF.Design.Layout.PCells;
global using CircuitRF.Design.Layout.Footprints;
global using CircuitRF.Design.Results;
global using CircuitRF.Design.Theming;
global using CircuitRF.Design.Workspace;
global using CircuitRF.Design.Schematic;
global using CircuitRF.Design.Symbol;

// And the same for the renderers, which moved to CircuitRF.Render in RND-1
// (brief-render-1-render-layer-below-the-firewall.md). Gate 5 of that brief asks that the
// renderers' existing tests keep passing; this line is what makes "unchanged" literally true for
// them too.
global using CircuitRF.Render;

// And the Data Display's models and renderers, which moved to CircuitRF.Render.DataDisplay in RND-4
// (brief-render-4-data-display.md). Same reason again: this project's ~100 Data Display test files
// state their expectations about Plot/Trace/Axes/Marker and the renderers, and none of those
// expectations changed — so the move must not show up in a single one of them.
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


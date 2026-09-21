// The CircuitRF.Design namespaces the renderers consume wholesale.
//
// These files were written inside `namespace CircuitRF.Ui.*` and saw the design-layer types through
// src/Ui/GlobalUsings.cs. R-rnd1-2 required them to move WHOLE rather than be rewritten on the way
// down, so the same list is said once here rather than as ~20 new `using` lines across them.
//
// **This file is the map**, exactly as src/Ui's is: a type that seems to appear from nowhere in
// src/Render is from one of these. Adding to it is not free — a global using that shadows a local
// type is the kind of surprise it exists to avoid — so keep it to namespaces src/Render genuinely
// consumes wholesale.

global using CircuitRF.Design.Cells;
global using CircuitRF.Design.Layout;
global using CircuitRF.Design.Layout.Assembly;
global using CircuitRF.Design.Layout.Drc;
global using CircuitRF.Design.Layout.Extraction;
global using CircuitRF.Design.Layout.Em;
global using CircuitRF.Design.Layout.PCells;
global using CircuitRF.Design.Results;
global using CircuitRF.Design.Schematic;
global using CircuitRF.Design.Symbol;
global using CircuitRF.Design.Theming;
global using CircuitRF.Design.Workspace;

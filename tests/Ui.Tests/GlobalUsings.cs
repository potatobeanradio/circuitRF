// The mirror of src/Ui/GlobalUsings.cs — see that file for why the namespaces that moved to
// CircuitRF.Design are pulled in once rather than file by file.
//
// Gate 2 of brief-cli-em-verb.md asks that the layout/EM tests pass UNCHANGED, as the evidence that
// the move was mechanical. This file is what makes that literally true: no test's assertions, setup
// or fixtures were touched to accommodate the new assembly.

global using CircuitRF.Design.Cells;
global using CircuitRF.Design.Layout;
global using CircuitRF.Design.Layout.Drc;
global using CircuitRF.Design.Layout.Em;
global using CircuitRF.Design.Layout.PCells;
global using CircuitRF.Design.Results;
global using CircuitRF.Design.Theming;
global using CircuitRF.Design.Workspace;

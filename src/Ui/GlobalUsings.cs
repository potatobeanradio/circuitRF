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
global using CircuitRF.Design.Layout.Drc;
global using CircuitRF.Design.Layout.Interchange;
global using CircuitRF.Design.Layout.Em;
global using CircuitRF.Design.Layout.PCells;
global using CircuitRF.Design.Results;
global using CircuitRF.Design.Theming;
global using CircuitRF.Design.Workspace;

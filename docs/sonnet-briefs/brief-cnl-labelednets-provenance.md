# Sonnet Brief — Persist net-label provenance through the .cnl round-trip (fixes node-picker n1/n2/n3 + table dbl-click)

## ROOT CAUSE (proven by reading the whole pipeline — do not re-litigate)
The node-picker filter feature is CORRECT end-to-end *in memory*, and `HbLabeledNodesCubeTests`
passes — but that test injects `tb.LabeledNets.Add(...)` directly. **The GUI never runs in memory.**

The GUI run path is: schematic → `WorkspaceViewModel.RunAnalysis` → **`WriteNetlist` writes a `.cnl`
file** → `SchematicRunService.RunNetlist(path)` → `CnlReader.ReadFile(path)` → engine.

`NetExtractor.AssignNetNames` populates `tb.LabeledNets` only during the schematic→netlist build.
But **`CnlWriter` has no field for label provenance** — it emits globals, instances, analyses,
measures, raw directives, and nothing else. So when `CnlReader` re-reads the file, `tb.LabeledNets`
is EMPTY. Chain of consequence:

  empty LabeledNets → Elaborator copies empty set to Nodes.LabeledNames →
  HbEngine gates `__LabeledNodes` on `labeledNames.Count > 0` → cube NOT emitted →
  picker sees `__LabeledNodes` absent → `labeledSet is null` → ShowAllNodes forced true →
  n1/n2/n3 all show.

This exactly matches "shows everything." The two prior fixes (StackSweepAxis skip-`__`, picker
read-label-by-name) were both correct and DID land — they just address a different break. The live
break is the `.cnl` provenance drop.

## FIX — round-trip LabeledNets through the .cnl format

### 1. CnlWriter: emit a provenance directive (src/Core/Netlist/CnlWriter.cs)
In `Write(TestBench tb, Library? library, string? header)`, AFTER the analyses/measures/raw blocks
(near the end, before `return sb.ToString();`), emit one line listing the labeled nets when any
exist. Sorted for stable, diffable output:
```
        // Net-label provenance: which nets came from user-placed schematic labels.
        // Round-trips tb.LabeledNets so the node-picker filter survives schematic→.cnl→reader.
        if (tb.LabeledNets.Count > 0)
            sb.AppendLine($"labelednets {string.Join(" ", tb.LabeledNets.OrderBy(n => n, System.StringComparer.Ordinal))}");
```
(Add `using System.Linq;` if not already present — it is used elsewhere in the file via `Select`,
so it's already imported.)

### 2. CnlReader: parse the directive (src/Core/Netlist/CnlReader.cs)
In `TryParseLine(string line)`, add a branch alongside the `analysis`/`measure` keyword handlers
(BEFORE the `define`/instance fallthrough). A `labelednets` line is only valid at top level:
```
        if (line.StartsWith("labelednets ", StringComparison.Ordinal) ||
            line.Equals("labelednets", StringComparison.Ordinal))
        {
            if (_currentCell is not null)
                throw new CnlReadException(_lineNumber, line,
                    "'labelednets' is only valid at top level, not inside a define block.");
            var rest = line.Length > "labelednets".Length
                ? line["labelednets".Length..].Trim()
                : "";
            foreach (var net in rest.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
                _testBench!.LabeledNets.Add(net);
            return true;
        }
```

### 3. Confirm the GUI write path actually uses CnlWriter on the in-memory TestBench
The fix only works if `WriteNetlist` builds the TestBench via `NetExtractor` (which sets
LabeledNets) and serializes it with `CnlWriter`. VERIFY in `WorkspaceViewModel.RunAnalysis` /
its `WriteNetlist` helper that the write is `CnlWriter.Write(tb, lib, ...)` where `tb` came from
`NetExtractor.Extract(...)`. If WriteNetlist builds the `.cnl` text by any OTHER path (hand-rolled
string, a different serializer) that bypasses `CnlWriter`, STOP and report — the directive must be
emitted on whatever path actually writes the file. (Expected: it uses CnlWriter; this is a
30-second confirmation, not a refactor.)

## Bug 2 (separate, still unaddressed) — table column dbl-click opens flyout, not inline editor
This is brief-table-cube-layout-fixes.md item #5 and did NOT land. It is INDEPENDENT of Bug 1 — do
NOT bundle the implementation; just report findings so we can scope it. FIND the Table view pointer
handler (likely src/Ui/Views/DataDisplay/PlotCanvasView.axaml.cs or a TableRenderer-linked
code-behind) where a double-click/double-tap on a `TableHitKind.TraceHeader` (column header)
dispatches. Report: (a) the file + handler, (b) what it currently calls (the Plot Properties flyout),
(c) whether an inline column-header text-edit control already exists to reroute to (the same
`TraceRowViewModel.CommitSpec` / `SpecShorthand` editor the axis-role card uses). If the inline
editor exists → one-branch reroute is the fix. If not → it's a real control to build; needs its own
brief. Do not build speculatively.

## Tests
1. **Cnl_RoundTrips_LabeledNets** (Core.Tests): build a TestBench, `tb.LabeledNets.Add("n_drain");
   tb.LabeledNets.Add("n_gate");` → `CnlWriter.Write` → `new CnlReader().Read(text)` → assert the
   re-read `tb.LabeledNets` is set-equal to {n_drain, n_gate}.
2. **Cnl_NoLabeledNets_NoDirective** (Core.Tests): empty LabeledNets → written text contains no
   "labelednets" line; re-read LabeledNets is empty.
3. **Cnl_LabeledNets_InsideDefine_Throws** (Core.Tests): a `labelednets` line inside a
   `define … end` block throws CnlReadException.
4. **EndToEnd_SchematicCnl_EmitsLabeledNodesCube** (Engine.Tests or Ui.Tests): the GAP the existing
   test misses — start from a TestBench with LabeledNets, write to .cnl text, read back via
   CnlReader, Elaborate, run HB, assert the DataSet CONTAINS `__LabeledNodes` with the labeled
   names. This is the regression guard for the whole round-trip (the bug that shipped).

## Gate
Build 0W/0E; tests green. Manual: label nets in a schematic, run HB → node picker lists ONLY the
labeled nets (n1/n2/n3 hidden); "Show all nodes" reveals them. (Bug 2 manual check deferred until
its scope is reported.)

## On completion
Note in `src/Core/CLAUDE.md` (or the netlist-format design doc): the `.cnl` format carries a
top-level `labelednets <name> <name> …` directive recording which nets came from user-placed
schematic labels. CnlWriter emits it from `tb.LabeledNets`; CnlReader parses it back. This is what
lets the node-picker labeled-filter survive the schematic→.cnl→CnlReader run path
(`HbLabeledNodesCubeTests` only exercised the in-memory path and missed this).

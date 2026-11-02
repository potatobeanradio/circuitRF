# Sonnet Brief — VAR component (core): node-less variable component, per-cell isolated, HB-sweepable

**Goal.** Add a **VAR** component: a node-less, port-less component holding many **named variable expressions**
(one variable per line). Variables are usable by other components in the **same cell** (and at the testbench top,
globally), **isolated per cell**, and **sweepable in HB** — so you can place a VAR, define e.g. `Pin = -10`, and
sweep `Pin`. This brief is the **core** (model + netlister + standard-library registration). The schematic
multi-line editor flyout is a **separate brief** (`brief-var-component-ui.md`).

## The key insight (why this is small)
Per-cell variables **already exist** in the data model. `Cell.Variables : List<Variable>` are bound into the
cell scope by `Elaborator.BuildCellScope` (`foreach (var v in cell.Variables) cellScope.Bind(v.Name,
v.Expression, v.Unit)`), and `TestBench.GlobalVariables` into the global scope. Scope is per-frame, so
**isolation is automatic**. HB sweep already re-evaluates scope/`ResolvedGlobals`. **A VAR is just a
schematic-authored way to add entries to the enclosing frame's `Variables`/`GlobalVariables` list.** No scope
engine change, no new stamping, no HB change.

So VAR must NOT become an `ElaboratedComponent` (it has no model/nodes). Instead, its name=expression pairs are
routed into the owning `Cell.Variables` (or `tb.GlobalVariables` at the top) during extraction.

## Design

### 1. SymbolKind + registry (`ComponentTypeRegistry.cs`, `SymbolKind`)
- Add `SymbolKind.Var` (find the `SymbolKind` enum — referenced across Schematic; add the member).
- Register in `ComponentTypeRegistry.Registry`: DisplayName `"VAR"`, InstancePrefix `"VAR"`, Category
  `ComponentCategory.Other` (or a new `Directives`/`Variables` category if one reads better — your call, but
  `Other` is fine), SearchTerms `["VAR", "Variable", "var", "vars", "parameter", "sweep"]`, `IsCommon: true`.
  `DefaultShowTypeLabel: true`, `DefaultShowInstanceName: true`.
- `EngineReference(SymbolKind.Var, _)` → `"VAR"` (a **sentinel**, like `"Pin"` — see netlister; it must NOT be
  `ComponentModelFactory.IsPrimitive`).
- `TryParseCode`: add `case "VAR": kind = SymbolKind.Var; return true;`
- `DefaultParameters(SymbolKind.Var, _)` → `[]` (a fresh VAR starts with no variables; the user adds them).
- VAR has **no ports**: ensure `SymbolPortDefs.For(SymbolKind.Var, _)` returns an empty array (add the case).

### 2. VAR carries an ordered list of name=expression (NOT fixed params)
A VAR instance stores an **ordered list of (name, expression, unit?)** — like the SDD/ZPort dynamic-parameter
pattern, but the names are arbitrary user variable names. Reuse the existing `EditableComponent.Parameters`
list (each `EditableParameter` already has Name/Expression/Unit) — each parameter row **is** one variable. No new
storage type needed. Order is the list order (preserve it; variables may reference earlier ones).

### 3. Netlister: route VAR into the enclosing frame's variables (`NetExtractor.cs`)
VAR must not be emitted as an `Instance`/primitive. In `ExtractModel`, collect VAR bindings and thread them out
alongside instances so the assemblers put them into the right `Variables` list:
- In the emit loop, **skip** `comp.Symbol == SymbolKind.Var` from the normal `EmitInstance` path (like
  `Ground`/`Pin` are skipped).
- Collect every VAR's parameter rows into a `List<Variable>` for this model:
  ```csharp
  var vars = new List<Variable>();
  foreach (var comp in model.Components)
      if (comp.Symbol == SymbolKind.Var)
          foreach (var p in comp.Parameters)
              if (!string.IsNullOrWhiteSpace(p.Name))
                  vars.Add(new Variable(p.Name.Trim(), p.Expression,
                      UnitNormalizer.ToEngineUnit(p.Unit) is { Length: >0 } u ? u : null));
  ```
- Change `ExtractModel` to also return `vars` (extend its tuple to `(List<Instance> Instances,
  IReadOnlyList<string> CellPorts, List<Variable> Variables)`).
- **Top model** (`Extract`): `tb.GlobalVariables.AddRange(vars)` (these are the testbench-level globals).
- **Sub-cell** (`EmitCellInstance`, where it builds `var cell = new Cell(cellName)`):
  `cell.Variables.AddRange(subVars)` from the recursive `ExtractModel` call.
- **Duplicate-name handling within a frame:** if two VAR rows (across one or multiple VARs in the same cell)
  declare the same name, add a `conflicts` message ("Variable 'X' defined more than once in this cell") and keep
  the **first** (or last — pick one and state it; first is simplest). Multiple separate VAR components in one
  cell are allowed and their variables **union** (the user said "many variables per VAR" and VARs are
  per-cell-scoped — multiple VARs in a cell share that cell's scope).

### 4. Elaborator — already done
No change needed: `BuildCellScope` already binds `cell.Variables`; the global scope already binds
`tb.GlobalVariables`. Confirm a VAR-defined variable referenced by a sibling component (e.g. `R = Rval` where
`Rval` is a VAR variable) resolves. **Add an elaborator test** rather than code.

### 5. `.cnl` round-trip (if VAR must serialize to/from `.cnl`)
Check how the schematic persists (`.csch`) vs `.cnl`. The schematic `.csch` stores `EditableComponent`s with
their parameter rows — VAR persists there for free (it's just a component with parameter rows + a SymbolKind).
For **`.cnl`**: if the reader/writer is in the netlist path, add a `VAR` line form (e.g. `VAR:<name>  k1=expr1
k2=expr2 …`) OR — simpler and consistent with the data model — emit each VAR variable as a cell/global
`variable` directive if `.cnl` already has one. **Investigate the `.cnl` grammar first**; if `.cnl` already
represents cell variables, map VAR → those and skip a new line type. If VAR is only ever schematic-authored
(no `.cnl` requirement yet), note that and defer `.cnl` syntax. Don't invent `.cnl` syntax speculatively — flag
the decision.

## Tests (`tests/Ui.Tests` for extraction, `tests/Core.Tests`/elaboration for scope)
1. **Var_BindsIntoCellScope:** a schematic with a VAR (`Rval=100`) and `R:R1 ... R=Rval` → extract → elaborate →
   `R1`'s resolved `R` parameter == 100.
2. **Var_PerCellIsolation:** two cells each with a VAR defining `X` to different values, each used by a local R
   → each R resolves to its own cell's `X` (no leakage).
3. **Var_TopLevelGlobal:** a VAR at the testbench top defines `Pin=-10` → `tb.GlobalVariables` contains `Pin`;
   `netlist.ResolvedGlobals`/HB can see it (so it's sweepable).
4. **Var_MultipleVarsUnion:** two VAR components in one cell → both sets of variables available; duplicate name
   across them produces a conflict message.
5. **Var_NotEmittedAsComponent:** a VAR never appears in `netlist.Components` (no `ElaboratedComponent`).
6. **Var_Sweepable_Hb (if feasible):** define a var at top, reference it in a tone/bias, run a tiny HB sweep over
   it → values change per sweep point (confirms the existing sweep machinery sees VAR globals). If wiring a full
   HB sweep test is heavy, assert via `ResolvedGlobals` + the sweep evaluator instead.

## Gate
Build 0W/0E; tests green. A schematic with a VAR defining a variable that another component references
elaborates and simulates; the same variable name in two different cells stays isolated; a top-level VAR variable
is visible to HB sweep. (UI placement/editing is the separate UI brief — for this brief, author the VAR via a
test/`.csch` fixture.)

## On completion
Note in `src/Ui/CLAUDE.md` + `src/Core/.../CLAUDE.md`: VAR is a node-less component whose parameter rows are
routed by `NetExtractor` into the enclosing frame's `Cell.Variables` (or `tb.GlobalVariables` at the top), so
per-cell isolation and HB sweepability fall out of the existing scope machinery; VAR is never emitted as an
`ElaboratedComponent` (sentinel `EngineReference="VAR"`, not a factory primitive). Next: the schematic
multi-line VAR editor flyout (`brief-var-component-ui.md`).

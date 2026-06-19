# Sonnet Brief — MEAS (measurement-equation) Library component

Goal: add a new annotation Library component **MEAS** (`SymbolKind.Meas`) that lets the user author
measurement equations on a schematic, plus the small resolution-rule change that lets the user reference a
measurement in the Data Display by its **bare name**. It is modeled exactly on **VAR**, differing only in
where its rows go: VAR rows become `TestBench.GlobalVariables`; MEAS rows become `TestBench.Measurements`.
The engine evaluator and run-pipeline wiring already exist (`MeasurementEvaluator`, and
`SchematicRunService.RunNetlist` grouped-assembly + `measurements` group), so this component + the bare-name
rule are the last pieces that make measurements work end to end.

Read first: `docs/skills/adding-a-library-component.md` (the annotation-component archetype),
`docs/design/measurements.md`, and `docs/design/results-dataset-layout.md` (grouped results / addressing).
Universal technique: **grep `SymbolKind.Var`** to find every site; each is a place MEAS needs a parallel
case. Build 0 warnings/0 errors (`TreatWarningsAsErrors=true`); tests green.

Where measurements land (current model): a run writes **one grouped `results/<schematicKey>/run.npy`** with a
group per analysis plus a `measurements` group. Each measurement cube is stored as `measurements.<Name>`.
**Access rule (Part 1 implements this):** in the Data Display the user references a measurement by **bare
`<Name>`** (`measurements.<Name>` also resolves). Analysis data is **always** qualified —
`<analysis>.V` / `<analysis>.S`. No collision is possible because measurement names cannot contain `.`.

---

## Part 1 — bare-name resolution rule (RfCore `DataSet`)

File: `<workspace>/RfCore/src/Data/DataSet.cs`. **This is a flagged DataSet-API change**
(lockstep with splotRF — note it in `src/Core/Data/CLAUDE.md` → "Change carefully").

Add a distinguished-group constant next to `DefaultGroup`:
```csharp
/// <summary>The group name holding post-run measurement cubes; bare-resolvable like the default group.</summary>
public const string MeasurementsGroup = "measurements";
```

Rewrite `BareResolve` so a bare name resolves only in the **default** group or the **measurements** group;
analysis (named) groups require qualification. This **removes the old sole-populated-group fallback**
(deliberate: a single-analysis run must still be addressed `SP1.S`, not bare `S`):
```csharp
private DataCube BareResolve(string name)
{
    // 1. Default group (flat / Touchstone sources).
    if (_groups.TryGetValue(DefaultGroup, out var dg) && dg.TryGetValue(name, out var c1))
        return c1;

    // 2. Measurements group — bare measurement access (names cannot contain '.').
    if (_groups.TryGetValue(MeasurementsGroup, out var mg) && mg.TryGetValue(name, out var c2))
        return c2;

    // Analysis cubes are reachable only by qualification (Analysis.Cube).
    var populated = _groupOrder.Where(g => _groups[g].Count > 0).ToList();
    if (populated.Count > 0)
        throw new KeyNotFoundException(
            $"No cube named '{name}' in the default or measurements group. " +
            $"Groups present: [{string.Join(", ", populated)}]. " +
            $"Qualify analysis cubes as 'Analysis.Cube' (e.g. 'HB1.V').");

    throw new KeyNotFoundException($"No cube named '{name}' — DataSet is empty.");
}
```
Update the `this[string]` doc-comment's bare-name bullet to match (default-or-measurements, else qualify).

**Why this is safe (verified):** flat Touchstone sources keep their cubes in the **default** group, so
`Contains("S")`/`Contains("Z0")` and `ToSnp` still work (step 1) and the SNP bridge still fires for `.sNp`
files. For a grouped `run.npy`, bare `S`/`Z0` now resolve to **false** — which is the wanted behavior under
the "no SNP bridge for results" decision (S-param results are addressed `SP1.S[:, i, j]` as cubes). The
engine's per-analysis flat DataSets (what `MeasurementContext` holds, and what `ds.V/S/...` run against
internally) are default-group, so qualified measurement accessors like `HB1.V(...)` are unaffected.
`NpyRoundTripGroupedTests` asserts ambiguity only across *multiple* groups and bare access only on the
*default* group — both still hold.

### Part 1 tests (extend `NpyRoundTripGroupedTests` or a DataSet unit test)
- **Bare measurement resolves:** the grouped set has `measurements.Pout`; `ds.Contains("Pout")` is **true**
  and `ds["Pout"]` returns the measurement scalar. (Update the existing `Bare_Pout_IsAmbiguous_MultipleGroups`
  test — its premise is now reversed: bare `Pout` **resolves** to the measurements group.)
- **Bare analysis cube still throws:** `ds.Contains("S")` / `ds.Contains("V")` remain **false** (qualify as
  `SP1.S`). (Existing `Bare_S_IsAmbiguous_*` / `Bare_V_IsAmbiguous_*` stay green.)
- **measurements.Pout still works qualified:** `ds["measurements.Pout"]` resolves (qualified path unchanged).
- Flat default-group bare access unchanged (`FlatDataSet_BareContains_WorksWithSingleGroup` stays green).

No UI change is needed for bare access — `CubeTraceSpecParser`/`TraceExpression` already pass bare names to
`ds.Contains`/`ds[...]`, so they pick up the measurements-group resolution for free.

---

## Part 2 — the MEAS Library component (UI authoring)

### 1. Enum — `src/Ui/Schematic/SchematicModel.cs`
Add `Meas` to `SymbolKind` (place it next to `Var`).

### 2. Registry — `src/Ui/Schematic/ComponentTypeRegistry.cs`
Add to `Registry` (next to the `Var` entry):
```csharp
[SymbolKind.Meas] = new("MEAS", "MEAS",
    Category: ComponentCategory.Other,
    SearchTerms: ["MEAS", "Measurement", "measure", "meas", "equation", "eqn"],
    IsCommon: true),
```
Add to `EngineReference` (sentinel — never emitted as an Instance; mirrors VAR):
```csharp
SymbolKind.Meas          => "MEAS",
```
Add to `DefaultParameters` (freshly placed MEAS has no rows, like VAR):
```csharp
case SymbolKind.Meas: return [];
```
Add to `UserParamTemplate` (the `+`-button row template; mirrors VAR's `Var{0}`):
```csharp
SymbolKind.Meas => new IndexedParamGroup(
    NameFormats:     ["Meas{0}"],
    DefaultUnits:    [""],
    ShowOnSchematic: [false],
    Dimensions:      [UnitDimension.None],
    FirstAddIndex:   1,
    SkipIndices:     null),
```
Add to `TryParseCode`:
```csharp
case "MEAS":   kind = SymbolKind.Meas;          return true;
```

### 3. Ports — `src/Ui/Schematic/EditableSchematic.cs`
In `SymbolPortDefs.For`, add (next to the `Var` case):
```csharp
case SymbolKind.Meas:    return [];
```

### 4. Glyph — `BuiltInSymbols.cs`
Add a label-style glyph mirroring VAR's (no leads — there are no ports). Add the static field +
`BuildMeas()` builder and the `Primitives` dispatch case `SymbolKind.Meas => _meas,`. Read VAR's glyph
builder and produce an equivalent (e.g. a small "=" / bracketed-equation motif distinct from VAR's box).
Use `SymbolColorRole.SymbolLine` — never literal colors.

### 5. Extraction routing — `src/Ui/Schematic/NetExtractor.cs`
This is the one structural change. Measurements attach to the **top testbench only** (data-model §2.1;
`CnlReader` rejects `measure` inside a `define` block), so MEAS rows must be collected only at the top
model, never in sub-cells.

(a) **Skip MEAS from instance emission** — in the `ExtractModel` instance loop, alongside the existing
`if (comp.Symbol == SymbolKind.Var) continue;`, add:
```csharp
if (comp.Symbol == SymbolKind.Meas) continue;  // MEAS rows routed to Measurements, not instances
```

(b) **Collect MEAS rows** — add a pass mirroring the existing VAR `frameVars` collection, building
`Measurement` objects:
```csharp
// ── Collect MEAS measurement definitions for this frame ─────────────
var frameMeas    = new List<Measurement>();
var measNamesSeen = new HashSet<string>(StringComparer.Ordinal);
foreach (var comp in model.Components)
{
    if (comp.Disable is DisableState.Open or DisableState.Short) continue;
    if (comp.Symbol != SymbolKind.Meas) continue;
    foreach (var p in comp.Parameters)
    {
        if (string.IsNullOrWhiteSpace(p.Name)) continue;
        var measName = p.Name.Trim();
        if (!measNamesSeen.Add(measName))
        {
            conflicts.Add($"Measurement '{measName}' defined more than once; first definition kept.");
            continue;
        }
        string? unit = UnitNormalizer.ToEngineUnit(p.Unit) is { Length: > 0 } u ? u : null;
        frameMeas.Add(new Measurement(measName, p.Expression, unit));
    }
}
```

(c) **Extend the `ExtractModel` return tuple** to carry the measurements, e.g.
`(List<Instance> Instances, IReadOnlyList<string> CellPorts, List<Variable> Variables, List<Measurement> Measurements)`,
returning `frameMeas` as the new element. Update both call sites:
- **`Extract` (top):** `var (instances, cellPorts, topVars, topMeas) = ExtractModel(...);` then
  `tb.Measurements.AddRange(topMeas);` (add near the existing `tb.GlobalVariables.AddRange(topVars);`).
  This **replaces** the current top-level `foreach (model.Measurements) tb.Measurements.Add(...)` loop
  (that vestigial source is superseded — remove it to avoid duplicates).
- **`EmitCellInstance` (sub-cell):** destructure the new element but **do not** attach it to the cell;
  if it is non-empty, warn: `conflicts.Add($"Cell '{cellName}': MEAS components are ignored inside a cell; measurements attach to the top testbench only.");`

`Measurement` is in `CircuitRF.Core.Design` (already imported in NetExtractor).

### 6. Editor — reuse the VAR multi-line editor
A measurement row is the same `name = expression` shape VAR uses, so reuse VAR's editor rather than
writing a new one. Grep `SymbolKind.Var` in the view-model/view layer to find where the VAR text editor
is launched (double-click / Edit) and where `VarTextParser` is used, and make `SymbolKind.Meas` open the
same editor. Generalize the editor's title/labeling so it reads "Measurements" for MEAS and "Variables"
for VAR (and the add-row placeholder uses the `UserParamTemplate` name format, so MEAS gets `Meas{n}`).
`VarTextParser` itself is format-only (`name = expression`) and needs no change. If the editor is hard-
keyed to `SymbolKind.Var`, widen it to accept either kind; keep the VAR behavior identical.

### 7. Tests — `tests/Ui.Tests` (headless)
1. **Meas_NoInstanceEmitted:** a schematic with one MEAS component extracts zero instances for it.
2. **Meas_RowsBecomeMeasurements:** MEAS rows `Pout = ...`, `PAE = ...` → `tb.Measurements` has both, in
   order, with expressions preserved.
3. **Meas_DuplicateName_FirstKept:** two rows with the same name → one measurement + a conflict message.
4. **Meas_InsideCell_Ignored:** a MEAS placed inside a cell schematic → not attached to the cell, warning
   raised, top `tb.Measurements` unaffected.
5. **Meas_RoundTrip_Csch:** save + reload a schematic with a MEAS component → rows preserved.

---

## Gate (manual)
Place a MEAS component; enter `Gain = dB(HB1.V("out", 1, All) / ...)` (or any valid measurement referencing
a run analysis by its results name, qualified). Run. In a Data Display, import the run's
`results/<schematicKey>/run.npy`; confirm the measurement appears under the **`measurements` group**, is
selectable, and **plots when referenced by its bare name `Gain`** (and that `Gain` does NOT collide with
any analysis cube, which must be addressed `HB1.V` etc.). Confirm VAR still behaves exactly as before.

## On completion
Note in `src/Ui/CLAUDE.md`: MEAS is an annotation component (no ports, no instance) whose `name = expr`
rows route to `TestBench.Measurements` at the top level only; it reuses the VAR text editor. Measurements
are evaluated post-run by `MeasurementEvaluator` into the run's **`measurements` group** (one grouped
`run.npy`) and are referenced in the Data Display by **bare name** (analysis cubes stay qualified
`Analysis.Cube`). Record the `DataSet.MeasurementsGroup` bare-resolution rule in `src/Core/Data/CLAUDE.md`
→ "Change carefully". This completes the measurement authoring path (`docs/design/measurements.md`).

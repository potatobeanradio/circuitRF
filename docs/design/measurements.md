# Measurements — Design

Status: implemented — measurements evaluate end-to-end on a UI run: both cube-reference notations
(accessor and bracket), composition, units, run wiring, and the `.cnl` round-trip all work. The
remaining pieces are the **MEAS authoring component** (creating measurements from the schematic UI —
see `docs/skills/adding-a-library-component.md`) and CLI evaluation. This doc is the reference for the
measurement system end to end.

Read with: `data-model.md` + `src/Core/Data/CLAUDE.md` (the DataSet/DataCube the engine returns and
measurements consume), `data-display.md` (how result `.npy` files become plottable data sources),
`parametric-sweep-ux.md` (swept cubes the measurements address), and `analysis-cards-ui.md` (where the
analyses a measurement references come from).

## What a measurement is

A **measurement** is a named post-processing equation evaluated over a run's analysis results. It is
*not* a circuit element — it draws no current and stamps nothing. It is cube algebra: `PAE = ...`,
`Gain = dB(HB1.V("out", 1, All) / ...)`, etc. The result is a `DataCube` added to the run's output and
plottable like any other trace. Measurements are the RF engineer's "equations" pane: derived figures
of merit computed once the analyses have produced their cubes.

Measurements compose: each is evaluated in declaration order and may reference earlier measurements by
name, so a complex figure of merit can be built from intermediate ones.

## Referencing analysis cubes (two notations)

A measurement pulls values out of an analysis's result cubes. There are **two equivalent notations**
for doing that — they produce identical results and may be mixed freely in one equation. Pick whichever
fits how you are working.

### 1. Accessor — name-keyed: `HB1.V("Vout", 1, All)`

You name what you care about. The first argument is the **node** (for `V`) or **branch** (for `I`) by
its label; the remaining arguments are positional — harmonic index, then any sweep axes. Omitted
trailing axes default to `All` (keep the whole axis).

```
Gain_dB = dB( HB1.V("Vout", 1, All) / HB1.V("Vin", 1, All) )
Idc     = DC1.I("Iout")                      # no-sweep DC → a scalar
```

- `HB1.V("Vout")` — node Vout, all harmonics, all sweep points.
- `HB1.V("Vout", 1)` — node Vout, fundamental, all sweep points (trailing `All` implied).
- `HB1.I("M1:d", 1, All)` — device-port branch current, fundamental, swept.

**Why use it:** it is **order-independent and durable.** You name only the node/branch and the
harmonic; the engine locates each axis by name, so adding or reordering sweep axes does not break the
expression. This is the right choice for measurements you author by hand and keep — figures of merit
that should survive sweep changes and re-runs.

### 2. Bracket — positional: `HB1.V[:, "Vout", 1]`

You write **one token per cube axis, in cube-axis order** (numpy-style):

- `:` keeps the axis whole (it stays in the result).
- A name (`"Vout"`) or integer (`0`) **fixes** the axis to a single value and drops it.
- `a:b` keeps a sub-range (end-exclusive, same as C# `Range`).
- All axes fixed → a scalar (`DC1.I["Iout"]`).
- `~` (the trace card's *family* marker) is rejected with a clear error — a measurement has no curve
  families; use `:` to keep an axis.

```
Pout_W = 0.5 * real( HB1.V[:, "Vout", 1] * conj(HB1.I[:, "Iout", 1]) )
```
Here the cube is `[sweep, node, harmonic]`: `:` keeps the sweep, `"Vout"`/`"Iout"` fix the node/branch,
`1` fixes the fundamental → a swept 1-D result (one Pout per sweep point).

**Why use it:** it is **exactly what the trace card shows.** When you dial in a trace in the Plot
Inspector, the card writes the correct bracket expression for you — it always gets the axis order and
the labels right. Copy that string straight into a measurement and it works, with no need to remember
the accessor's argument order. This is the fast path for **exploration** and for **reusing a plot you
have already set up.**

The catch: brackets are **positional**, so they are tied to the cube's current axis order. If you later
add an outer parametric sweep, a hand-edited bracket can address the wrong axis (the accessor would
not). So: prefer the **accessor** for durable hand-authored measurements; reach for the **bracket** when
copy-pasting from a trace you already have on screen.

### S-parameters: `i`/`j` are 1-based port numbers

An S-parameter cube has axes `[freq, i, j]` (plus a leading sweep axis when swept), where `i` is the
response port and `j` the drive port. On **these two axes the index is the port number, 1-based** — the
way RF engineers name S-parameters — in both notations:

```
s21    = SP1.S(2, 1)         # accessor: S21 over frequency
s21_db = dB( SP1.S[:, 2, 1] ) # bracket:  S21, freq kept (:), ports fixed
s11    = SP1.S[:, 1, 1]       # S11
```

`SP1.S(i, j)` and `SP1.S[:, i, j]` are equivalent and both give Sij. `SP1.S[:, 2, 1]` is **S21**, not
"row 2, column 1 by zero-based index." Only `i`/`j` carry port numbers; `freq`, sweep, harmonic, and
node/branch axes are unchanged (`:` keeps them, integers index 0-based, names fix labeled axes). A port
outside `1..nPorts` is a clear error listing the available ports. For a swept S cube the leading sweep
axis stays 0-based positional — `SP1.S[0, :, 2, 1]` is S21 at the first sweep point.

> **Multi-`:` note.** In a measurement every `:` axis is *kept* — `HB1.V[:, :, 1]` is a 2-D `[sweep,
> node]` result. The trace card reads a second `:` as a *family of curves*; that family concept does not
> exist in measurement algebra, so the same string means "keep both axes" here. Single-`:` specs (the
> common copy/paste case) mean the same thing in both places.

The shared shorthand grammar is documented in `docs/design/trace-card.md` §5.

## Current state (honest inventory)

What works (end-to-end on a UI run):
- **`Measurement` model** (`name`, `expression`, `unit`) on `TestBench.Measurements` (Core.Design).
- **`.cnl` round-trip:** `CnlWriter` emits `measure Name = expr [unit]`; `CnlReader` parses them back
  into `tb.Measurements`. Verified symmetric.
- **Engine evaluator:** `MeasurementEvaluator` (src/Engine) builds a `MeasurementContext` over the
  analysis results, injects resolved globals, evaluates each measurement in declaration order, injects
  each result so later ones compose, and adds each result cube to the DataSet.
- **`MeasurementContext`** (Core.Expressions) resolves qualified analysis accessors and optionally
  carries per-analysis linear back-solvers.
- **Run wiring:** `SchematicRunService.RunNetlist` evaluates measurements after dispatching the
  analyses and folds the results into the run's `measurements` group (see "Run wiring").
- **Both reference notations:** the name-keyed accessor and the positional bracket index (above).

What remains:
- **Authoring (planned):** there is no way to create a measurement in the schematic UI yet —
  `tb.Measurements` is populated only via the `.cnl` `measure` lines today. The plan is a **MEAS
  Library component** (below); the vestigial `SchematicEditModel.Measurements` list is superseded by it.
- **CLI evaluation:** the CLI run path does not evaluate measurements (UI-run only).
- **Lazy node back-solve:** see v1 limitations.

## Evaluation pipeline

`MeasurementEvaluator.EvaluateInto(ds)` does the following, in order:

1. Build a `MeasurementContext` over the run's analysis results (a name → `DataSet` map) plus optional
   back-solvers.
2. Create a `globals` scope and inject every resolved global (the VAR variables, from
   `ElaboratedNetlist.ResolvedGlobals`) so measurements can reference them by name.
3. Create a child `measurements` scope. For each measurement in declaration order: evaluate its
   expression against the context and scope, inject the result back into the scope under its name (so
   later measurements compose), and add the result cube to `ds` (scalars are wrapped as scalar cubes).
   The run pipeline passes a dedicated DataSet here and then folds those cubes into the run's
   `measurements` group (see Run wiring).

The expression surface (the evaluator is the source of truth) includes qualified cube accessors such
as `HB1.V("n_drain", 1, All)`, `HB1.I("M1:d", 1, All)`, element-wise cube arithmetic that broadcasts
over `DataCube`s, and the element-wise helpers `conj`, `real`, `imag`, `mag`, `phase`, `dB`, `dB10`,
`dBm`, `log10`, `ln`. Cubes are referenced by either the name-keyed accessor or the positional bracket
index — see **Referencing analysis cubes (two notations)** above for both forms and when to use each.

**Branch-current accessor** (`brief-unify-i-cube-engine`, 2026-06-18) — `HB1.I("branchName", ...)` pins
the `branch` axis of the single `I` cube, exactly mirroring `HB1.V("nodeName", ...)` for the `node`
axis. Branch labels are device-port paths (`"M1:d"`, `"M1:g"`) or IProbe names (`"Ids"`). `HB1.I("name",
k)` pins harmonic k; `HB1.I("name", k, All)` slices over sweep; `HB1.I` (bare) returns the whole
`[branch, harmonic]` cube. Unknown branch names throw with an "Available:" list. Two-tone `I` uses
`[branch, mixIndex]`; IProbe two-tone back-solve is deferred (no `__ProbeBranches` in two-tone DataSets).

## Run wiring

After `SchematicRunService.RunNetlist` dispatches the analyses, it assembles **one grouped DataSet** for
the whole run: a group per analysis (keyed by the analysis's results name) plus a `measurements` group.
When `tb.Measurements` is non-empty and at least one analysis produced a result, it builds a
name → `DataSet` map from the per-analysis results, calls `EvaluateInto(measDs)` on a fresh DataSet, then
folds those cubes into the run DataSet's `measurements` group. A failing measurement is caught and
surfaced as a run note rather than failing the whole run.

The whole run is one dataset, so a measurement is in scope no matter which group holds it — there is no
per-analysis attachment and no cube duplication. The measurement reads any analysis by name
(`HB1.V(...)`) and its result lives once in the `measurements` group. `RunResultsWriter` writes the
grouped DataSet as a single `results/<schematicKey>/run.npy`; in the Data Display the measurements appear
under a `measurements` group alongside the analyses and plot like any trace (addressed
`measurements.Name`).

## Reference contract

A measurement references an analysis by the **name it appears under in the results** — the name shown
in the data-source tree. For a plain analysis that is the analysis name; for a parametric-sweep chain
it is the base analysis name the cube is produced under. Qualified access then reads from that
analysis's cubes (e.g. `HB1.V("out", 1, All)`). Resolved globals (VAR variables) are in scope by name,
and earlier measurements are in scope by name. Referencing an unknown analysis raises an error naming
the available analyses; the run reports it as a measurement note and continues.

**Swept variables** (`brief-measurement-swept-variable`, 2026-06-18) — a global variable that is also
a parametric-sweep axis is injected as a 1-D cube (one element per sweep point) rather than a scalar,
so `Pin_avail_dBm = Pin` over a 10-point `Pin` sweep yields a 10-element result cube that plots as a
curve. The cube's axis has the same name and values as the `Pin` axis in the swept analysis results, so
it broadcast-aligns with swept analysis data (e.g. `Gain = dB(HB1.V("out",1,All)) - Pin` resolves
element-wise without a shape error). A non-swept global stays a scalar. If a sweep is disabled/collapsed
and its axis is absent from the results, the variable falls back to its scalar global value. This
override applies for the duration of measurement evaluation only.

## Authoring — the MEAS Library component (completed)

Measurements are authored with a dedicated **MEAS** Library component, built on the same archetype as
VAR: a floating annotation component with no ports, skipped from instance emission, whose parameter
rows are `name = expression [unit]` lines edited through the same multi-line text editor VAR uses. The
one difference is routing: `NetExtractor` collects a MEAS component's rows into `TestBench.Measurements`
(exactly as it collects VAR rows into `GlobalVariables`). The order of rows — and of multiple MEAS
components — is the declaration order the evaluator composes in; a duplicated measurement name is a
reported conflict, first definition kept (mirroring the VAR duplicate rule).

This component is the worked example in `docs/skills/adding-a-library-component.md` (the "annotation
component" archetype). The vestigial `SchematicEditModel.Measurements` collection is superseded by this
path and can be retired or left inert.

## v1 limitations

- **No lazy node back-solve.** The run wiring passes no back-solvers, so `V(node)` for un-probed
  linear-interior nodes is unavailable; qualified access against stored result cubes works. Wiring the
  HB/linear back-solvers through is a follow-on.
- **UI-run only.** The CLI does not evaluate measurements yet.
- **Markers on measurement traces** follow the cube-bound trace rules (no network markers).

## Key files

- `src/Core/Design/TestBench.cs` — `Measurements` list; `Measurement` model lives alongside the design
  types (`Variable.cs` is the structural twin).
- `src/Engine/MeasurementEvaluator.cs` — evaluation, scope chain, result-cube emission (`EvaluateInto`).
- `src/Core/Expressions/MeasurementContext.cs` — analysis-result resolution + optional back-solvers.
- `src/Core/Netlist/CnlWriter.cs` / `CnlReader.cs` — `measure` line round-trip.
- `src/Ui/Schematic/SchematicRunService.cs` — run wiring: grouped-DataSet assembly + `measurements` group.
- `src/Ui/Schematic/NetExtractor.cs` — (planned) MEAS-component row collection into `tb.Measurements`,
  mirroring the VAR → `GlobalVariables` path.
- `src/Ui/Schematic/RunResultsWriter.cs` — writes the run's one grouped `run.npy` (measurements group included).

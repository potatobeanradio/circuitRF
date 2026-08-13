# Brief DD-N — Data Display: network-parameter access (S/Z/Y), trace-card identity, and the 4-port stability crash

**Area:** `src/Ui/DataDisplay` (trace card + `Trace` model) only. Do not touch `src/Core`, `src/Engine`.
Conversion math comes from `RfCore` (`RFNetwork`, `RfCore.Data.NetworkMetrics`) — never re-implement it here.

**6 items, one subsystem:** everything below is the S-parameter *trace identity* path — how an
S/Z/Y element or a derived metric is offered in the picker, bound to the trace, and evaluated.
They share root causes, so they are one brief and should land as one slice sequence.

---

## Verified anchors (read before designing — several of these invert the obvious diagnosis)

1. **A SIMULATED S-parameter run has NO SNP and NO Z/Y cubes.** `SParameterEngine.Run`
   (`src/Engine/SParameterEngine.cs:147-150`) emits exactly two cubes: `S` (via
   `DataSetBuilder.FromSnp`) and `Z0`. There is no `Z`, no `Y`. That is the whole reason typing
   `SP1.Z[:, 1, 1]` reports invalid — the parser is right, the cube genuinely does not exist.
2. **For a simulated run, `S(i,j)` is a CUBE item and the derived metrics are NETWORK items.**
   `TraceRowViewModel.RebuildSignals` skips only the *default-group* `S` from the cube path
   (`TraceRowViewModel.cs:2387`), so a grouped `SP1.S` is offered as an ordinary cube; the metrics
   for the same source are added separately against `entry.NetworkView`
   (`TraceRowViewModel.cs:2418-2458`), so they are **not** cube-bound.
3. `ShowMatrixTypeCombo => !_trace.IsCubeBound && _trace.Data is { } d && !d.IsEmpty`
   (`TraceRowViewModel.cs:1021`). Combined with (2) this is **exactly inverted** for a simulated
   run: hidden on the S element (cube-bound), shown on the derived metric (network-bound) — which
   is also the one place it is meaningless, because `Trace.Derived`'s setter force-pins
   `_matrixType = MatrixType.S` (`Trace.cs:196-200`).
4. **`entry.NetworkView`** (`DataSourceEntryViewModel.cs:54-69`) is the lazily-built `SNP` view of a
   simulated S cube (`_snp` for Touchstone). It is the existing, working pattern for "derive a
   network object on demand from a DataSet" — mirror it, don't invent a second one.
5. **The crash is a bypass, not a missing feature.** `Trace.BuildDerivedPath`
   (`Trace.cs:1038-1120`) correctly routes through `NetworkMetrics.TwoPortMetric(...,
   InputPort, OutputPort)` / `TwoPortUniformReal`, which extract the chosen 2-port from an N-port
   and catch `ArgumentException` → empty path, never a crash. **`Trace.DataPoint`
   (`Trace.cs:1180-1206`) does not** — it builds an `SNP` from the raw N-port matrices and calls
   `RFNetwork.StabilityMu/StabilityMuPrime/MaxGain` directly, so `NormalizedS2Port`
   (`src/RfCore/RFNetwork.cs:605`) throws *"This calculation requires a 2-port network."*. It also
   silently returns `NaN` for `K`, `|Δ|` and `Passivity` (the `_ => double.NaN` arm), which the
   Table then prints as NaN cells. The Table path reaches it via
   `TableRenderer.FormatTraceCell → DataPointScalar → DataPoint`.
6. `AvailablePorts` (`TraceRowViewModel.cs:1483`) is populated ONLY by `RebuildPortOptions`
   (`:1588`), reached only through `RefreshNetworkMetricCard` (`:1598`) ← `RefreshDescription`
   (`:2946`). **The `TraceRowViewModel` constructor (`:2124`) never calls `RefreshDescription`** —
   it calls `RebuildSignals()` only. A freshly-constructed card (every plot-type change and every
   `RebuildTraces()`) therefore renders the In/Out row with two EMPTY combos; re-picking the signal
   on the live VM runs `OnSelectedSignalChanged → RefreshDescription` and fills them. That is the
   "sometimes shows, sometimes not" report, exactly.
7. `ToolTip.Tip="{Binding DisabledReason, TargetNullValue={Binding Label}}"`
   (`src/Ui/Views/DataDisplay/PlotInspectorView.axaml:545`). A `{Binding}` inside `TargetNullValue`
   is **not evaluated** — the binding *object* is used as the value and rendered via `ToString()`,
   which is literally `"Avalonia.Data.CompiledBinding"`. It shows on exactly the items whose
   `DisabledReason` is null, i.e. every enabled item.
8. `Trace.BuildPickerExpression` already renders the `i`/`j` axes as **1-based port numbers**
   (`Trace.cs:573`), so `SP1.S[:, 2, 1]` is S21 and the spec text needs no new grammar for §4.

---

## §1 — S/Z/Y selector appears only on derived metrics [BUG]

**Symptom:** the S/Z/Y `IconSelectButton` shows when *Source Stability / Load Stability / …* is
selected and is absent on `S(1,1)`. **Root cause:** anchors (2)+(3).

**Fix — two rules, both stated positively:**
- **Never** show the matrix-type selector on a derived-metric trace (`_trace.IsDerived`). The metric
  is defined on S; `Trace.Derived`'s setter already force-pins `MatrixType.S`, so the control could
  only lie.
- **Always** show it on an S-parameter *element* trace, whether that trace is network-bound
  (Touchstone) or cube-bound (a simulated run's `S` cube). Concretely, replace the `!IsCubeBound`
  test with a positive predicate — "this trace reads a network-parameter matrix element" — that is
  true for (a) a non-derived network trace with non-empty `Data`, and (b) a cube-bound trace whose
  cube is the network-parameter cube of a group that carries `S` + `Z0` (§2 gives you the test).

Gate: on a 4-port simulated `SP1` run, `S(2,1)` shows the S/Z/Y selector and *Source Stability µ′*
does not; on a Touchstone `.s4p`, unchanged from today for `S(2,1)` and the selector disappears
from the metric.

## §2 — Z and Y are unreachable for simulated S-parameter data [BUG]

**Symptom:** no Z/Y from the trace card; `SP1.Z[:, 1, 1]` → invalid. **Root cause:** anchor (1) —
the cubes do not exist, and the cube path has no conversion stage.

**Approach — virtual `Z` and `Y` cubes derived on demand, NOT a new trace mode.** Materialize them
in `DataSourceEntryViewModel` beside `NetworkView`, using its exact lazy pattern (anchor 4): when
the entry's DataSet has an `S` cube (use `NetworkMetrics.FindSCubeSpec`, already the authority) plus
its `Z0`, expose `Z` and `Y` cubes in the **same group and same axis layout** as `S`
(`[<optional sweep>, freq, i, j]`), each element computed per frequency by
`RFNetwork.SToZ(mat, z0PerPort)` / `RFNetwork.SToY(mat, z0PerPort)`. Build once, memoize, and drop
the cache when the entry reloads (the auto-refresh path already rebuilds entries).

Why this and not a conversion flag on the cube trace: every downstream consumer — the spec parser,
the axis-role editor, `TraceExpression`, the Table, `.cdd` persistence, export — then works with no
change at all, and `SP1.Z[:, 1, 1]` parses because the cube genuinely resolves. A conversion flag
would need parallel handling in each of those.

**Wire-up:**
- **Picker:** offer `Z` and `Y` in the same group as `S`. With §4 landed they appear as element
  items (`Z(1,1)`, `Y(2,1)`, …) driven by the matrix-type selector, not as three separate quantity
  entries — see §4.
- **Matrix-type selector (§1):** changing S→Z→Y on a cube-bound network-parameter trace rewrites
  `CubeName` (`SP1.S` → `SP1.Z`), keeps the slice, and re-derives `Expression` via
  `BuildPickerExpression()`. Keep the existing network (SNP) behaviour untouched — there
  `MatrixType` is already honoured in `BuildMatrixPath`/`DataPoint`.
- **Auto-transform:** `TraceRowViewModel.DefaultTransformFor` already returns dB20 for
  S/Y/Z-parameter cubes — make sure the Z/Y cubes are recognised by whatever name test it uses, or
  Z on a Rect plot will seed `Mag` instead of dB20. (For Z/Y, dB20 of an impedance is defensible but
  odd — prefer `Mag` for Z/Y and keep dB20 for S. State whichever you choose in the completion note.)

**Cost guard:** conversion is `F` × (N×N complex inverse). Measure on the largest fixture you have;
if a `>` 8-port × `>` 4001-point case is slow, build per-frequency on demand rather than eagerly —
but do NOT add a progress dialog or a background thread for this.

Gate: on a simulated 4-port run, picking Z or Y renders a curve; typing `SP1.Z[:, 1, 1]` and
`SP1.Y[:, 2, 1]` both parse and render; the values match `RFNetwork.SToZ`/`SToY` of the S cube at
the same frequency (unit test, not eyeball).

## §3 — Combo tooltip shows "Avalonia.Data.CompiledBinding" [BUG]

Root cause: anchor (7). **Fix:** drop `TargetNullValue` and expose a single VM string —
add e.g. `TraceDataItem.TooltipText => DisabledReason ?? Label` and bind
`ToolTip.Tip="{Binding TooltipText}"`. Then **grep the whole `src/Ui` tree for
`TargetNullValue={Binding`** — the same construct anywhere else has the same defect; fix every hit
the same way.

Gate: hovering an enabled S-parameter item shows its label; hovering a disabled metric shows its
reason; no item ever shows a type name.

## §4 — Enumerate every S(i,j) in the combo; remove the port i/j axis rows and the bare "S" item [CHANGE]

**Owner's decision, adopted as specified:** for S-parameter data the port indices are never an
x-axis. Today a cube-bound `S` trace exposes `i` and `j` as ordinary axis-role rows, so the user can
promote a port index to X — a plot nobody wants, and two rows of clutter on every S trace.

- **Remove** the bare `S` (and, per §2, bare `Z`/`Y`) quantity entry from the item combo.
- **Add** one item per ordered pair — `S(1,1)`, `S(1,2)`, `S(2,1)`, `S(2,2)`, … in row-major order —
  listed **above** `Source Stability Circles` in the same S-Parameters group. Labels follow the
  current matrix type, so flipping the S/Z/Y selector relabels them (`Z(2,1)`) rather than adding a
  third dimension of items. The existing `TraceDataItem` matrix-element constructor
  (`TraceDataItem.cs:51`) already produces exactly this label — reuse it.
- **Suppress the `i` and `j` axis-role rows** for a network-parameter cube trace. Every other axis
  row stays: a parametric sweep axis (`Vds`, `RFfreq`) must still be pinnable/promotable, and `freq`
  stays X. Selecting an item pins `i`/`j` in the slice as it does now.
- **N-port scaling:** the item list is N² entries (16 for a 4-port, 64 for an 8-port). That is
  acceptable and is what the owner asked for; do **not** substitute a pair-picker. If N is large the
  combo simply scrolls.
- **Typed specs keep working:** `SP1.S[:, 2, 1]` must still parse, bind, and select the matching
  `S(2,1)` item in the combo (`BuildPickerExpression` already round-trips 1-based ports, anchor 8).
  A spec that pins `i`/`j` to a range or promotes one to X is still *parseable* — leave the parser
  alone; it just is no longer reachable from the picker.

Gate: on a 4-port source the combo lists 16 element items then the metric items; no i/j rows appear
on the card; selecting `S(3,2)` and typing `SP1.S[:, 3, 2]` produce the identical trace and the
identical spec text; a parametric-swept S cube still shows its sweep-axis row.

## §5 — In/Out (and the axis rows) render only on the second visit [BUG]

Root cause: anchor (6) — `AvailablePorts` is empty on a freshly-constructed card.

**Fix:** populate the network-metric card state at construction. Call `RefreshNetworkMetricCard()`
(or at least `RebuildPortOptions()`) from the `TraceRowViewModel` constructor, after `RebuildSignals()`.
Also relax `RebuildPortOptions`' churn guard (`:1591`) — `if (AvailablePorts.Count == n) return;`
is correct for a no-op refresh, but verify it cannot skip a genuine rebuild when the port count
happens to match across a source switch.

**Then verify the whole card, not just this row.** The constructor initialises ~15 backing fields by
hand; any other card element whose only writer is `RefreshDescription` has the same defect. Walk
`RefreshDescription`'s body (`:2912-2947`) and confirm each raised property is either a pure getter
(fine — evaluated at bind time) or has its state initialised in the constructor. Report anything
else you find rather than fixing silently.

Gate: add a 4-port simulated-run fixture; construct a `TraceRowViewModel` on a derived trace
directly (headless) and assert `AvailablePorts` has 4 entries and `ShowPortSelectors` is true —
with **no** prior signal selection. Owner-verifies the S → Source Stability → S → Source Stability
sequence renders identically each time.

## §6 — Crash: plotting a stability metric from a 4-port run [BUG]

```
System.ArgumentException: This calculation requires a 2-port network.
  RfCore.RFNetwork.NormalizedS2Port → StabilityMuPrime
  ← Trace.DataPoint ← DataPointScalar ← TableRenderer.FormatTraceCell
```

Root cause: anchor (5) — `DataPoint`'s derived branch is a second, older implementation that never
learned about the ordered port pair.

**Fix:** delete the duplicate. `DataPoint`'s `IsDerived` branch must produce the value the *plotted
path already computes*, from the same authority:
- Route through `RfCore.Data.NetworkMetrics.TwoPortMetric(Data.Matrices, z0PerPort,
  Derived.ToNetworkMetric(), InputPort, OutputPort)` for the 2-port metrics, and
  `PassivityFull`/`PassivityPair` for passivity — the identical calls `BuildDerivedPath` makes
  (`Trace.cs:1060-1075`), including the same `z0PerPort` resolution (`SourceZ0PerPort` when long
  enough, else `RFNetwork.Z0Array(Data.Z0, nPorts)`).
- Prefer evaluating the whole metric array once and indexing at `fi` (matching `BuildDerivedPath`),
  or cache it — `FormatTraceCell` calls this per row, so a per-cell full-sweep recompute would make
  a large Table crawl. Measure before choosing.
- **Catch `ArgumentException` → `NaN`**, exactly as `BuildDerivedPath` does. A bad port pair must
  render an empty cell, never take the app down.
- The `_ => double.NaN` arm disappears: `K`, `|Δ|` and `Passivity` are real metrics with real
  values and must now print them.

**Then sweep for the same bypass elsewhere.** `RFNetwork.Stability*`/`MaxGain` take an `SNP` and
throw on N≠2. Grep `src/Ui` for direct calls to them and confirm every one goes through
`NetworkMetrics` first. `Trace.cs:1101/1182` are the known sites; check
`GetMarkerImpedanceString`, `MarkerInfoBoxViewModel`, `PlotExporter` and `DataExporterViewModel`
too — a marker readout or an export on the same 4-port trace would crash the same way.

Gate: **a regression test that fails before the fix** — a 4-port SNP, a `MuPrime` trace, and a
`DataPointScalar` call: must return a finite number equal to `NetworkMetrics.TwoPortMetric(...)[fi]`
for the selected pair and must not throw. Repeat for `K`, `DeltaMag`, `Passivity` (both scopes), and
for an out-of-range pair (→ NaN, no throw). Owner-verifies a Table of all seven metrics on the real
4-port run.

---

## Slice plan (each slice compiles and tests green before the next)

- **N1 — §6 the crash + the bypass sweep.** Highest severity, smallest blast radius, no UI change.
- **N2 — §3 tooltip + the `TargetNullValue` grep.** Trivial, independent.
- **N3 — §5 constructor initialisation + the `RefreshDescription` audit.**
- **N4 — §1 matrix-selector visibility** (needs §2's "is a network-parameter cube" predicate, so
  land the predicate here even if the virtual cubes come next).
- **N5 — §2 virtual Z/Y cubes** + auto-transform + spec round-trip.
- **N6 — §4 element enumeration + i/j row suppression.** Last: it is the largest picker change and
  it reads the matrix type §1/§2 establish.

## Constraints / gotchas

- **`RfCore` is the only home for conversion and metric math.** The UI may cache results; it may not
  compute S→Z, S→Y, renormalisation or a stability factor itself.
- `_suppressDataCallback` / `_suppressTransformCallback` / `_rebuildingAxisRoles` guards exist
  because Avalonia's ComboBox resets `SelectedItem` when its `ItemsSource` is cleared mid-callback
  (the "revert bug", documented at `TraceRowViewModel.cs:2905-2910`). §4 rebuilds the item list —
  do not call `RebuildSignals()` from inside `RefreshDescription`.
- `.cdd` round-trip: §2/§4 change what a trace's `CubeName`/`Slice` can hold. Load an existing
  `.cdd` saved before the change and confirm its S traces still resolve; save/reload after.
- TreatWarningsAsErrors: nullable props → locals; no unused privates; no `<`/`>` in XML doc comments.

## Tests

- `TableRenderer`/`Trace` 4-port derived-metric regression (§6) — must fail before the fix.
- `NetworkMetrics` parity: `DataPointScalar(f)` == the plotted `Points` y-value at the same
  frequency, for all seven metrics on a 4-port, across two different port pairs.
- Virtual Z/Y (§2): cube values == `RFNetwork.SToZ/SToY` per frequency; `SP1.Z[:, 1, 1]` and
  `SP1.Y[:, 2, 1]` parse and bind; `.cdd` round-trip.
- Picker (§4): item count == N² + metrics; no `i`/`j` axis rows on an S trace; sweep axis row
  survives; spec ↔ combo round-trip for `S(3,2)`.
- Card construction (§5): `AvailablePorts` populated with no prior selection.
- Tooltip (§3): `TooltipText` non-null and never a type name for enabled and disabled items.
- Run `dotnet test tests/Ui.Tests` and `dotnet test tests/Firewall.Tests` (two invocations — this
  SDK rejects two project paths in one).

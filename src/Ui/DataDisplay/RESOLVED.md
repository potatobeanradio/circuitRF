# DataDisplay — resolved briefs (detail, off the CLAUDE.md growth path)

## A trace that cannot be resolved says so; it does not end the session (2026-09-01)

Reported twice from the field (Windows, 1.0.0-beta.6 then -beta.7, not reproducible on macOS or on
Windows under emulation): adding a trace to a second Smith plot after a 1-port S-parameter run
terminated the application with a bare `IndexOutOfRangeException` out of `DataCube`'s gather, on the
stack `AddTrace` → `TrySetCubeData` → `SetCubeDataFrom` → `DataCube.get_Item`.

**Why the root cause is still open, and why containment is the right move anyway.** The cube and the
slice arguments are both excluded, provably and on the shipped binary — the analysis is in
`src/RfCore/RESOLVED.md`. What is NOT in doubt is the consequence: the user loses an unsaved
workspace over a curve. And that is out of proportion, because **every foreseen way this resolve can
fail already has a defined non-fatal outcome** — a missing source, a missing cube, a wrong rank, an
unparseable spec and a mismatched versus-X all end as `<invalid>` on the trace card, which is the
whole shape of `SetCubeDataFrom`. An unforeseen failure had no reason to be the one exception.

`SetCubeDataFrom` is now a wrapper over `SetCubeDataFromCore`. On any exception it marks the trace
invalid exactly as the foreseen failures do, and writes ONE crash-trail line naming what the next
report needs and this one lacked entirely:

```
trace resolve FAILED: plot=Smith cube='SP1.S' expr='SP1.S[:, 1, 1]' shape=[freq[101] x i[1] x j[1]]
kind=Complex slice=[freq:KeepAsX:0, i:PinToIndex:0, j:PinToIndex:0] — <Type>: <message>
```

Two deliberate choices in that line. It is written **only on failure** — a note on every resolve
would flood the ring-buffered trail on every redraw and destroy the thing that makes it useful. And
every step of building it is individually guarded (`DescribeCubeResolve`), because it runs when
something is already wrong and a diagnostic that throws is worse than none.

This is containment plus instrumentation, **not a fix** — it converts an unexplained fatal crash into
a named, non-fatal, self-describing one. Held by `TraceResolveContainmentTests`.


`CLAUDE.md` in this directory was getting bloated from per-brief write-ups. Going forward, a
completed brief's detail lands here instead; `CLAUDE.md` stays for durable, still-true
conventions only. See the root `CLAUDE.md`'s own note about `src/Ui/HISTORY.md` for the same
pattern applied at the `src/Ui` level.

## A reflection marker read 50 Ω against a COMPLEX port reference (2026-08-31)

**Reported:** a Term at `Z = 5+j100` driving a 1-port `Z = 5-j100` is a perfect power-wave match, so
`S(1,1)` plotted at the Smith centre — correct. The marker's info box, however, said
`impedance=50+j0 Ω` instead of the `5-j100` the port actually looks into. The owner's first read was
that the Kurokawa mapping was being applied wrongly for a complex reference.

**It was not the arithmetic.** `Trace.FormatImpedance` computes `Z = (Z0* + Γ·Z0)/(1-Γ)`, the exact
inverse of `Γ = (Z - Z0*)/(Z + Z0)`, and for `Γ = 0` that is `conj(Z0)` — the right answer for any
reference, real or complex. It was simply handed 50 Ω: the trace's `SourceZ0PerPort` array had been
cleared, and `Trace.MarkerReferenceZ0` then falls through to the trace's own `Z0`, whose default is
50 Ω.

**An ORDERING bug between the two view models.** `PlotInspectorViewModel.TrySetCubeData` stamps the
per-port references (via `ResolveNetworkParamCube`); `TraceRowViewModel.RebuildSignals` then cleared
them for **every** cube-bound trace. `RebuildSignals` runs *after* the stamp on both paths that
matter:

- the row VM's own **constructor** — so every `.cdd` load, plot-type switch, undo/redo and paste;
- `OnLibraryChanged`, which stamps every trace and then calls `RefreshDataSources()` on every row —
  so every post-run refresh.

Hence a correct plot with a wrong readout, and hence a symptom that "healed" the moment the user
touched the picker (the signal-change path ends in a `RebuildAndNotify`, which re-stamps).

### Why the plot could not reveal it

The circuit is a zero-length thru: `S11 = (Z2 - Z1*)/(Z1 + Z2)`, which is 0 both at the ports' own
conjugate references AND at any uniform reference. So the dot sits at the centre either way, and the
marker readout was the only observable that differed. Any investigation that trusts the chart to
disambiguate the reference will chase the wrong half.

### The fix

`PlotInspectorViewModel.StampSourceZ0FromCube(ds, trace, cubeSpec)` is now the single stamping site
(`ResolveNetworkParamCube` calls it), and `RebuildSignals` **re-stamps** a cube-bound
network-param trace from its own group's `Z0` cube instead of nulling it. Only a genuinely
non-network-param cube (or no matching item at all) still clears.

Second half, same site: with Override off, `Trace.Z0` is documented as a read-only MIRROR of the
source's port-1 reference, but nothing on the load path re-seeded it — a `.cdd` persists whatever
the box last held. A display authored before the port became complex reloaded showing 50 Ω, and
ticking Override would then have renormalized to a value the user never typed. `RebuildSignals` now
calls `SeedZ0FromSource()` after a successful stamp when Override is off.

Gate: `tests/Ui.Tests/Z0ComplexPortMarkerTests.cs` (7 tests; 4 fail without the fix). The oracle is
hand-computed and reference-independent — `Γ = 0` at reference `Z0` is `conj(Z0)`, full stop.

### Two unrelated defects found while reproducing this, NOT fixed here

- **`RfCore/TouchstoneIO.cs:322-328`** emits `! NOTE: Original data had complex Z0.` on *every*
  non-strict export regardless of the actual Z0, and prints `snp.Z0` — one uniform value — once per
  port, so a genuinely per-port run is misreported (port 2's `5-j100` printed as `5+j100`). `SNP`
  carries a single reference by design, so the per-port note cannot be made truthful without the
  caller's array. The option line also keeps only the real part (`# GHz S RI R 5`), so a Touchstone
  round-trip of a complex-referenced run silently loses the reactance.
- **`Trace.FormatImpedance` formats with `Marker.FormatComplex`, not `FormatImpedanceComplex`** — so
  the S-parameter impedance readout ignores `MatrixFormatImpedance` and can print an impedance as
  `40.01 dB ∠-87.14°`. The contour path (`Trace.cs`, `ContourImpedance`) uses the right one.

## Every derived trace vanished when the analysis was re-run (2026-08-29)

**Reported:** after re-running the S-parameter analysis, the Max Gain trace disappeared from the
Data Display — then the same for the stability circles. The owner's own read was right: it was
because they are derived-parameter traces.

**It hit derived traces on a SIMULATED source only, and every one of them** — stability circles,
MaxGain, µ, µ′, K, |Δ|, passivity, group delay. Ordinary S(i,j) traces on the same source
survived, which is what made the symptom look metric-specific.

`PlotInspectorViewModel.OnLibraryChanged` sweeps out traces whose source has left the library, and
built its "still here" set from `entry.Snp` alone. **A simulated run has no `Snp` by design** — its
S cube goes through the cube path, which can carry a swept axis an SNP structurally cannot — so its
derived traces bind to the entry's narrow `NetworkView` instead. Reading `Snp` alone made every
such trace look orphaned, and the first `LibraryChanged` after a run deleted it outright. An
ordinary S(i,j) trace on the same source is cube-bound and takes the path-keyed branch, so it was
never in scope.

`LibraryChanged` fires on every re-run: `WorkspaceViewModel.RefreshOpenDataDisplaysAsync` →
`ReloadChangedAsync` → `ReloadAsync` → `RefreshNpy` → the event. So the deletion was reliable, not
intermittent.

### Two halves — fixing only the first would have hidden a second bug behind it

1. **`OnLibraryChanged` now reads `Snp` AND `NetworkView`.** That is the deletion itself.
2. **`RefreshNpy` refreshes the `NetworkView` in place** rather than discarding the instance, via
   the new `RefreshNetworkViewPreservingIdentity`. It used to do `_networkView = null;
   _networkViewBuilt = false;`, so the next access handed out a **new** SNP — and a live trace still
   holds the old one. With only fix 1, the trace would have stopped disappearing and started
   quietly drawing the PREVIOUS run's data, which is worse than a visible deletion. This is exactly
   the guarantee `RefreshTouchstone` already makes for `Snp` ("the SNP instance identity is
   preserved so existing trace bindings survive"), applied to the other view of the same idea; both
   the stale-sweep and the picker's `alreadyApplied` test are reference comparisons.

   A rebuild that comes back null means the reloaded source genuinely stopped being network-shaped
   (S cube gone, or a sweep axis made it rank 4). The view then stays null and the bound traces are
   correctly stale — the sweep should remove them.

### Gates

`SimulatedSourceNetworkMetricsTests` gained a 5-case theory covering the derived kinds across both
plot geometries — rect metrics assert on `Points`, circles on `StabilityCircleCentres`, since a
circle trace draws into neither of the other's storage — plus
`ReloadingASimulatedSource_KeepsTheNetworkViewInstance`, which re-runs onto a **different frequency
grid** so "same instance" cannot be confused with "nothing was refreshed". All five fail on the old
code by deleting the trace; the identity test fails by handing back a new SNP.

### The site that actually caused the reported symptom — `OnSelectedDataSourceChanged`

The three fixes above were all real, and none of them was the one the owner was hitting. It took
driving the owner's own `.cdd` through the real load path to find it, and it lives in
`DataDisplayViewModel.OnSelectedDataSourceChanged`:

```csharp
if (!t.IsCubeBound)
    t.Data = _library.SelectedEntry?.Snp ?? SNP.CreateBroken(t.SourcePath ?? "");
```

Every trace whose `SourceRef` is the "Selected" sentinel is re-pointed here. For a simulated source
`SelectedEntry.Snp` is null, so each derived trace was handed **`SNP.CreateBroken` — a zero-port
SNP**. `BuildDerivedPath` clears its geometry and returns immediately below two ports, so the trace
went blank; and because that placeholder belongs to no library entry, the next `LibraryChanged`
swept it out of the plot for good.

**This fires on every re-run**, because `WorkspaceViewModel.RefreshOpenDataDisplaysAsync` finishes
by re-selecting the datasource it just reloaded. Reproducing it needed that third step: a test that
called `ReloadChangedAsync` alone passed while the product was still broken.

**`DataSourceRef.Selected` is the literal string `"run.npy"`.** Reading the owner's `.cdd`, the
traces' `"SourcePath": "run.npy"` looks like a stale file name beside a `SelectedDataSource` of
`"S-Param.npy"` — it is not, it is the sentinel, and every trace in that file carries it. Naming a
test fixture `run.npy` also silently makes it the sentinel, which cost a debugging round.

### A third site, same shape — and leaving it was the wrong call

`TraceRowViewModel.RebuildSignals` re-finds a trace's entry with the same `e.Snp == _trace.Data`
test, which never matches for a simulated source. This was initially recorded as cosmetic on the
argument that `_suppressDataCallback` is set around the assignment, so the trace could not be
rebound. **That was wrong, and the owner found it immediately:** with the deletion fixed, Max Gain
stopped disappearing and instead turned into S(1,1) on pressing Run — the fallback
`SelectedSignal = match ?? AvailableSignals.FirstOrDefault()` re-pointed the card at the first
signal in the group, and the trace followed. The lesson is the plain one: reasoning about a
suppression flag is not evidence about what a rebuild does. The gate now asserts on
`row.SelectedSignal?.Derived`, not just on the trace surviving.

The lookup now asks about `NetworkView` as well, mirroring the `Entry.NetworkView ?? Entry.Snp`
order the bind in `OnSelectedSignalChanged` uses, so it resolves the very object the trace holds.
The `matchEntry.Snp!` dereferences below it become one `matchView` local; for a Touchstone entry
`NetworkView` **is** `Snp` — including a broken one — so the missing-file (`IsEmpty`) branch is
reached exactly as before. `CubeBoundTraceOnSimulatedSource_KeepsItsCardSelection_AcrossARerun`
guards the path that was already correct.

### Four sites, one mistake

`Snp` read as if it were the only view of a source, in four places that each had to ask
"which SNP does this trace hold?":

| Site | Effect when wrong |
|---|---|
| `PlotInspectorViewModel.OnLibraryChanged` | derived trace swept as stale, deleted |
| `DataSourceEntryViewModel.RefreshNpy` | new NetworkView instance each reload → same sweep |
| `TraceRowViewModel.RebuildSignals` | card falls back to S(1,1), trace follows |
| `DataDisplayViewModel.OnSelectedDataSourceChanged` | trace's Data replaced by a 0-port broken SNP |

Plus the `.cdd` loader (`snp = libEntry?.Snp`), which dropped derived traces on open — the fifth.
All five now use the same `NetworkView ?? Snp` order the picker's bind established. Each was
verified load-bearing by reverting it alone against the `.cdd` gate.

**The lesson for the next one of these:** the first three were found by reading code and confirmed
with synthetic fixtures built through the picker, and each looked like the whole answer. The one
that mattered turned up only when the owner's real artifact went through the real open-and-re-run
sequence. `DerivedTraceRerunFromCddTests` exists to be that path, and it mirrors
`RefreshOpenDataDisplaysAsync` step for step, re-selection included, precisely because leaving that
last step out is what made an earlier version of it pass over a broken product.


## MAG/MSG was 2× too large in dB, and the transform combo is a mislabel (2026-08-29)

**Reported:** a modelled BJT's Max Gain trace read about twice the datasheet's Gms, and the trace
card's transform combo — which auto-selects dB20 for a Max Gain trace — produced a *different*
number when switched to None.

Two separate things, one a real numeric bug and one a naming/plumbing defect.

### 1. `RFNetwork.MaxGain` returned 20·log10 of a POWER ratio

MAG = |S21/S12|·(K−√(K²−1)) and MSG = |S21/S12| are **power** gains, so dB is 10·log10. The
function used 20·log10, so every MAG/MSG the Data Display drew — plot, marker readout and Table
cell alike, since all three share `NetworkMetrics` — was exactly twice its true dB value. A vendor
data set for a small-signal RF bipolar transistor at 3.5 V / 5 mA reads 22.55 dB at 1.8 GHz where
its own data sheet says 11.27 dB.

**Nothing caught it because every existing assertion compared MaxGain to MaxGain** — the SNP path
against the matrix path, the renormalized reference against the raw one. A factor wrong in both
halves cancels identically. The new gates use an oracle that never touches the formula:

- **The unilateral limit is exact.** With S11 = S22 = 0 and S12 → 0, K → ∞ and the whole expression
  collapses to |S21|² — which is the transducer power gain of that matched two-port, a quantity
  defined without reference to K or to |S21/S12|. So MAG in dB must come out at 10·log10(|S21|²),
  i.e. the familiar 20·log10(|S21|). That is `MaxGain_UnilateralMatchedTwoPort_…`.
- Its tolerance is set by how far S12 is from zero and by the cancellation in K−√(K²−1) at
  K ≈ 1.7e4, not by the code under test; 4 decimal places is three orders inside the 2× error.
- A second gate pins the scale on a realistic conditionally-stable amplifier so the guard is not
  anchored only at a degenerate corner.

### 2. The trace card's transform combo does not transform a derived trace

For a network-metric trace the combo is not applied to anything. `Trace.BuildDerivedPath` plots the
`NetworkMetrics` value directly and never consults `YAxis`; `DataPointScalar` explicitly returns the
derived value untransformed (its comment says so). The dB20 entry that appears for Max Gain is set
by the `Derived` setter as `YAxis = Db` and only ever reaches two places: the trace label, which
becomes `dB(Max Gain)`, and a new marker's default `MatrixFormat`. It is a **unit annotation wearing
a transform's clothes** — the value was already dB before the fix and still is after it.

That makes changing it actively harmful rather than merely inert. `CubeTransformToYAxis` maps
None/dB10/dB/Conj → `DependentVarFormat.Complex`, and `GetMarkerValString` branches on
`YAxis == Complex` into `Marker.FormatComplex(Complex(v, 0))`. A marker created while the trace was
in dB carries `MatrixFormat.DB`, whose formatter is `20·log10(|c|)` — so selecting None makes the
readout print 20·log10 of a dB number. That is the second, unrelated "different number", and it has
nothing to do with the factor of 2 above.

**Not changed here** — it is a UI-semantics decision with two defensible answers (disable the combo
for derived traces, or give it a real linear/dB meaning) and the reported numeric bug is §1.


## Max Gain now says which form it is plotting (2026-08-30)

**Reported:** the Max Gain trace's Y-axis label reads "MaxGain dB20", which is misleading; the trace
card offers transforms that make no sense for the metric (dB20 rather than dB10, Real and Imag at
all); and since MAG/MSG has both a linear and a log10 spelling, the UI has to say which one is on
the plot — defaulting to log10 when the metric is picked.

This is the follow-up to §"MAG/MSG was 2× too large in dB" above, which fixed the arithmetic and
deliberately left the display question open ("a UI-semantics decision with two defensible answers").
The answer taken is the second one: **give the combo a real linear/dB meaning.**

### The label said dB20 because, for every other network trace, `YAxis == Db` IS dB20

`TraceLabeler` had its own private `FromDependentVarFormat`, whose documented reasoning was that
"every network path in `Trace` computes 20·log10(|z|)" for `Db`. That stopped being true for MaxGain
on 2026-08-29 — its value arrives from `RFNetwork.MaxGain` already at 10·log10 and the YAxis switch
is bypassed — so the one quantity in the application whose dB is 10·log10 was the one being labelled
dB20. The mapping now lives on the trace as `Trace.DisplayTransform`, which the labeler, the marker
readouts (via `ReadoutDescription` → `QuantityFor`) and the trace card's combo all read. One
mapping, so the label and the selected combo entry cannot disagree about what is plotted.

### The form is stored in `YAxis`, not in `Transform`

`Trace.Transform` looks like the natural home, and `ApplySelectedTransform` was already writing it
for network traces — but nothing reads it back for them, and **`DataDisplayViewModel` does not
restore it for a network/derived trace** (only `YAxis` and `Derived`). Storing the choice there would
have silently reloaded every existing `.cdd`'s Max Gain trace as linear. `YAxis` round-trips, so the
three states map onto it: `Db` → dB10 (the default, and what every old file already carries),
`Mag` → the linear ratio, `Complex` → the same ratio, unlabelled.

**`Complex` for a real scalar is the trap that mapping creates**, and it is why `Trace.YAxisIsComplexValue`
now exists. Four paths used `YAxis == DependentVarFormat.Complex` as "is this a complex trace":
the marker readout, the multi-marker line, the 2-D nearest-point hit test and `MarkerShowsImpedance`.
Left alone, a linear Max Gain trace would have read out as "16 + j0" and been offered an impedance
it has no reflection coefficient for. A stability circle is a genuine Γ-plane locus and is
deliberately still complex under the new predicate.

### The linear form is a primitive, not the dB form undone

`RFNetwork.MaxGainLinear` is the MAG/MSG formula; `RFNetwork.MaxGain` is 10·log10 of it, and
`NetworkMetric.MaxGainLinear` is appended so the cube adapter reaches it the same way. Taking
10^(dB/10) in the UI would have been a second definition of the metric living outside RfCore, which
is exactly what R-stb-1 exists to prevent. `Trace`'s derived-metric memo is keyed on the form too —
without that, switching the combo returns the previously cached array and the curve does not move.

### What the combo offers

`None`, `dB10`, `Mag` — enabled. `dB20`, `dB`, `Phase`, `Real`, `Imag`, `Conj` — keyed out and
disabled (shown, not hidden, which is the existing `ItemContainerTheme` mechanism the picker already
uses everywhere else). `SetDisplayTransform` refuses a transform outside that set and writes
nothing, so the gate does not depend on the view alone. `Trace.MaxGainTransforms` is the one list.

**Scoped to MaxGain on purpose.** The other derived scalars (µ, µ′, K, |Δ|, passivity, group delay)
still have an inert combo showing "Mag" — that is the pre-existing state described in the §2 note
above, it was not part of the report, and unlike MaxGain none of them has a second form to choose
between. Their transform lists are byte-identical to before, which
`NonMaxGainNetworkTrace_KeepsItsExistingTransformList` pins.

Gates: `tests/Ui.Tests/MaxGainDisplayFormTests.cs` (13) and four new methods in
`tests/RfCore.Tests/StabilityAndPassivityTests.cs`. The numeric oracle is the same unilateral limit
that fixed the 2× bug — MAG → |S21|² for a matched unilateral two-port — read at both spellings.


## Contour markers read out the whole plot, not just their own trace (GitHub #2, 2026-08-29)

**Reported:** a user asked for a loadpull marker to show complex impedance, P3dB and efficiency
together, so that picking Zmod/Zopt off the plot is one reading rather than three markers.

A marker on a contour now builds its info box (and the editor popup's readout block) from **every
contour trace in the plot**, in placement order, then the impedance, then Γ. Only contour markers
change; every other marker's box is byte-identical.

- **The plot context had to reach the builder.** `BuildMarkerBoxLines`'s last parameter was
  `otherTraces`, supplied only when `Marker.IsMulti` — four call sites each repeated that gate. It is
  now `plotTraces`: **every** trace in the plot, in order, including the marker's own, supplied by the
  one new `MarkerInfoBoxViewModel.PlotTraces`. Placement order is not recoverable from an
  "everything except me" list, and the requested row order is the plot's order, not owner-first. The
  multi-marker paths skip `this` themselves, so their output is unchanged.
- **The Γ→Z conversion is the loadpull surface's, NOT `FormatImpedance`'s.** `Trace.ContourImpedance`
  uses `Z = Z0·(1+Γ)/(1−Γ)` — what `LoadpullSurface.RenormGamma` and `RebuildContour` fit in, via the
  trace's own `Z0`. `FormatImpedance`, which every S-parameter readout uses, is the power-wave form
  `z0·(z̄0/z0 + s)/(1 − s)`; the two agree only for a real reference. Reporting a termination the
  fitted surface itself does not agree with would be worse than reporting none, so this is a second
  formula on purpose, not a missed reuse. The marker editor's Impedance field now calls the same
  method, so the field and the "Z=" row cannot drift.
- **`ContourData.GammaPlane` is not a safe sole plane oracle.** `ClearContourGrid` sets it to *false*
  when a fit fails, which on a Smith plot would read a Γ out as if it were ohms. `ContourImpedance`
  therefore takes an optional `gammaPlane` override, and the editor — which knows the real
  `PlotType` — passes it. The info box keeps `GammaPlane`, which is what it already used to pick its
  Γ-vs-Z label.
- **`Marker.MatrixFormatImpedance` was dead** — declared, copy-constructed, never read. It is now the
  impedance row's format (default `RI`), because the marker's own `MatrixFormat` is `MA` or `DB` on
  these plots and "133.3∠26.6°" is not the number that goes into a matching network. Γ still uses
  `MatrixFormat`, so one marker shows polar Γ beside rectangular ohms, which is the conventional pair.
- Non-contour traces sharing the plot contribute no rows: nothing else in a plot is sliceable by
  termination.

Tests: `ContourMarkerReadoutTests.cs` (10) — placement order, the five-row order, the Z0 arithmetic
at two references, rectangular-vs-polar spelling, the Rect/Z-plane no-Γ case, non-contour traces
ignored, the null-plot-context fallback, an ordinary marker unchanged, and the VM-supplies-the-plot
wiring end to end (a correct builder fed a null plot list would still have shipped the old one-row
box).

**Also removed:** three `System.Console.WriteLine` debug lines left in
`MarkerEditorViewModel.CommitImpedance` by an earlier round, which fired on every impedance commit in
the shipped GUI.


## A damaged `.cdd` opened blank, silently, and then overwrote itself (2026-08-26)

**Reported:** *"somehow i managed to get the data display to get corrupted .. i had to make a new
workspace to make a fresh one working again. (simple display of a s1p s parameter file, modifying the
frequency range for one of the 2 plots, saving and closing the file then trying to reopen the data
display)"*

The two `.cdd` files sent with the report are both **healthy** — they parse, they load, both plots
and their 601-point traces come back, and each round-trips through load→save byte-for-byte. So the
report is not about a file circuitRF cannot read; it is about what circuitRF DOES with one it cannot,
and about how such a file gets made in the first place. Two defects, one on each side of that:

- **The `.cdd` was the only document type written non-atomically.** `.csch`, `.csym`, `.ccell`,
  `.clay`, `.ctech`, `.cem`, `.wasm` and `.cws` all go through `AtomicFile.WriteAllText`
  (serialize to a sibling temp, rename over the target); `DisplayWindowViewModel.SaveAllAsync` alone
  called `File.WriteAllTextAsync`, which **truncates the target first**. Anything that interrupts the
  write — a crash, a kill, a removable/network volume going away mid-save, two saves racing — leaves
  a half-written file where a display that was fine a moment earlier used to be. Now atomic, like
  everything else.

- **A `.cdd` that would not parse was swallowed.** `LoadAllAsync` had `catch { return; }` around the
  deserialize. The document then materialized at that path as a perfectly ordinary, **clean**,
  one-empty-plot display — no message, no dirty mark, nothing distinguishing it from a display the
  user simply had not authored yet. **The next save wrote that blank over the file**, so a damaged
  copy that still held most of the user's plots became an empty one that held none. Measured, not
  inferred: truncating a good `.cdd` to half its length gave `tabs=1 plots=1 dirty=False`, and the
  save that followed produced a 1,577-byte file with no trace of the display in it. It now throws
  `InvalidDataException` naming the file — the same treatment the `format_version` mismatch has always
  had, and both callers already report it (`WorkspaceViewModel.OpenOrActivateDataDisplay` →
  `Messages.Error`, `DataDisplayView.DoOpenDisplayAsync` → "Cannot open display: …").

- **And a failed open left the wreckage docked.** `OpenOrActivateDataDisplayCoreAsync` registers the
  document in `_openDocsByPath` and opens the tab BEFORE loading, so a throwing load left a blank
  document sitting at that path, materialized, contradicting the error message the caller had just
  posted — and a Ctrl+S or a close-prompt "Save" on it destroyed the file the load had refused to
  read. This was already reachable before this brief via the `format_version` guard. The load is now
  wrapped: on failure the registration is removed and the tab force-closed, so the workspace is
  exactly where it was and the file on disk is still recoverable.

**Not the cause, checked and cleared:** the axis-limits edit the report describes round-trips
correctly for every range tried (inside the data, entirely outside it, reversed, and the exact
0–3 GHz of the reported sweep) — `AutoscaleX` goes false, the window persists, and the reload matches.
The saved files' `NaN`/`Infinity` risk is not real either; no edit path produced one.

**Gates** (`tests/Ui.Tests/DataDisplayFileIntegrityTests.cs`, 3 tests, all verified red against the
pre-fix code): a truncated `.cdd` throws and the file is byte-identical afterwards; a save whose temp
write is blocked leaves the previous file intact (forced deterministically by putting a *directory*
where the sibling temp has to go — the old code had no temp to collide with and would have
"succeeded" straight over the target); and a healthy `.cdd` still round-trips byte-for-byte with no
temp left behind.

**What is still unexplained.** Nothing here says what damaged the user's file, and the copies he sent
are not damaged, so the mechanism above is the one that fits the symptom, not one caught in the act.
The `.cws` (a dotfile — literally named `.cws`, hence invisible in Explorer, which is likely why it
was not sent) would say whether the workspace also lost track of the display.

## The live VSWR drag readout was hardcoded black (2026-08-23)

**Reported:** the VSWR text drawn beside the pointer while a locus is dragged is unreadable in dark
mode.

It painted `SKColors.Black` outright. It now uses `theme.TextColor` — the same colour
`MarkerInfoBox` draws its own lines in, which is what was asked for and what every other readout in
this renderer already did. harmonicaRF's equivalent (`DrawVswrReadout`) was already theme-aware;
only the Data Display copy was not.

Worth keeping for the oracle rather than the fix: the gate is a **differential render** — the plot is
drawn twice into a dark-theme surface, with and without the readout, and every differing pixel
belongs to the readout by construction. "Is there light ink somewhere" cannot gate this, because the
axis chrome is already drawn in that same colour. The readout's own colour is then the BRIGHTEST of
the differing pixels, not the darkest: antialiased edges blend toward the background, and in a dark
theme that direction is darker.

## The interpolated loadpull marker's name overflowed its own ring (2026-08-23)

**Reported:** the name inside a Mode-1 (interpolated, not snapped) contour marker renders slightly
too large for a two-character name — it wants a small margin to the circle's stroke. The circle
itself is the right size.

The name's size is the MXP/MXE letter size, and so is the disc's radius — a deliberate earlier
choice, so the three glyphs read as one family at every zoom. But **that size is sized for ONE
letter**, which is all an MXP/MXE glyph ever draws. In line-width units the disc radius is 3.5, the
ring stroke 0.75 (straddling the circle, so the clear interior radius is 3.125) and the letter size
4.5. IBM Plex Bold's `m1` has an ink half-diagonal of 0.784 em → **3.53 at that letter size, past the
3.125 clear radius**: the name's corners land *in* the ring stroke rather than beside it. Nothing
about the canvas is involved — radius, ring and letter size are all the same multiple of the line
width, so the overflow is identical at every zoom level and is purely a property of the string.

**Fitted, not shrunk by a constant.** The name is scaled down only as far as it needs to clear the
ring, leaving 6 % of the clear radius as margin: `m1` renders at 0.833 × the letter size, and a
ONE-character name is not touched at all — so where the family property was actually true, it stays
true. The disc, the ring and their sizes are untouched.

### Two traps, both measured

- **Text metrics are not linear in font size at these sizes.** The first version measured the ink box
  at the size about to be drawn. The per-em half-diagonal that comes back differs by more than a
  third between a 4.5 px and a 22.5 px draw size (the rasterizer rounds), so the fitted marker
  overshot at one canvas size, undershot at another, and stopped scaling with the canvas — exactly
  the proportionality defect an earlier round removed from this same glyph. The measurement is now
  taken once at a fixed reference size and applied as a ratio.
- **Whether a name overflows is a property of the TYPEFACE, and the headless test run does not have
  the shipped one.** `SkiaFonts.PlexBold` cannot load without a live Avalonia asset system, so tests
  substitute `SKTypeface.Default` — whose `m1` measures 0.680 em and *fits*, where Plex Bold's 0.784
  does not. A test written through the substitute would have gated nothing while looking green. The
  fit therefore takes the measurement as a parameter, and the gate loads the shipped `.ttf` straight
  off disk to supply it — without touching the shared font seam, which other test classes read
  concurrently.

## Table box sizing + keyboard scrolling (2026-08-21)

Follow-ups to the 1x1 table work below.

### One width floor caused BOTH resize symptoms

*"the 1x1 Table width can't be resized using gripper to be the width of the 1 column header width.
Also, double clicking on gripper doesn't resize the 1x1 to the right width."* Two gestures, one
cause: `PlotContainerViewModel.ResizeTo` floored width at a flat **200** logical px for every plot
type. The double-click path was never wrong — `PlotContainerView.OnResizeHandleDoubleTapped` already
computed `min(TotalColumnWidth, viewable width)` = 115 and handed it over; `ResizeTo` clamped it
straight back to 200. The drag path hit the same wall.

A Table's width is set by its COLUMNS, so it now has its own floor (`MinLogicalWidth`):
`max(MinColumnWidth, min(200, TotalColumnWidth))`. Two guards keep the change from reaching anything
else: a table WIDER than 200 keeps the 200 floor (so a multi-column table can still be dragged
narrower and clip, as before), and an **empty** table keeps it too — without that guard a
trace-less table would have taken `natural = 0` and collapsed to the 40 px per-column minimum.

### Page Up / Page Down / Home / End — the implementations already existed and nothing called them

Both `DataDisplayViewModel.ScrollSelectedTable` and `PlotControl.ScrollTableRows` were written,
documented ("Called from DisplayWindow for Page Up / Page Down key handling") and **dead**: no key
binding, no command, no caller anywhere. `TableRenderer.RowPaddingFraction` was even made `public`
"for PageUp/Down visible-row calc". The feature was wiring, not algorithm.

Wired as document-level `KeyBindings` in `DataDisplayView.axaml` → `[RelayCommand]`s on
`DisplayWindowViewModel` → the selection, the same route `Delete` already takes. Scoped to SELECTED
Table plots (all of them, not the old "exactly one" rule, which silently did nothing with two
tables selected).

**Both dead copies re-derived the row geometry from `FontSize` themselves, and both were wrong the
same way**: neither subtracted a summary table's reserved TITLE BAND, which `Draw` translates the
grid down by. They now share `TableRenderer.ScrollMetrics(plot, canvasSize, zoom)`, which runs the
real `BuildLayout` and returns `(PageRows, MaxScroll)`. `PageRows` counts WHOLE visible rows (a
half-clipped last row does not count, so a page step never skips a row the user only partly saw)
while `MaxScroll` uses the ceiling-based visible count `BuildLayout` clamps with — so **End lands
exactly where a run of Page Downs stops**, which is a gate test.

End is the last PAGE, not the last ROW: scrolling until the final row sits alone at the top of a
blank table is not what "bottom" means.

All four commands carry `CanExecute = HasSelectedTable`. Avalonia's `KeyBinding` asks `CanExecute`
at press time and only then marks the event handled, so an ungated command would SWALLOW Page
Up/Down and Home/End from everything else in the display whenever no table was selected.

## A 1x1 cube on a Table drew as 2x2 — two independent causes (2026-08-21)

*"if a Data Display plot is of type Table and it has a trace with 1x1 DataCube, the rendering shows
the first column as empty (also with no header name)"* — then, after the first fix, *"I see an extra
blank column to the right"*. Two separate defects, both visible only on a narrow table, which is why
the report changed shape rather than going away.

### 1. A rank-0 trace emitted a blank X column that existed only to carry a row

`BuildColumns` gave every scalar trace an `XAxis` column with an empty header, a blank cell, and a
synthetic `[0.0]` X — its only job was to give the table one row to draw. That column IS the empty,
unnamed first column in the report. A scalar now contributes exactly one `TraceValue` column; the
single-row anchor rides on the value column itself, since `Trace.SetScalarCubeData` already stores
`CubeXValues = [0.0]` and `FormatCubeCellAt` matches on it. `TableColumn.IsScalar` and its blank-cell
special case in `FormatColumnCell` are gone with it.

**The load-bearing consequence: row count could no longer be measured from `XAxis` columns alone.**
Three sites did exactly that (`BuildLayout`, `RequiredCanvasHeight`, `PlotContainerViewModel.
RowCountLogical`), and a table of only scalars would have reported **zero** rows and drawn nothing.
They now share `TableRenderer.RowCount(columns)`, which is the longest column of any kind.

Mixed tables were the risk the change had to survive — a scalar losing its X column must not disturb
a swept trace beside it. The dedup state is reset across a scalar (`prevAxisName`/`currentXArray`
nulled) so the next trace still gets its own X column, and the scalar's value sits on row 0 with
blanks — not `NaN` — below it.

### 2. The grid painted past the last column, so the empty plot box read as another column

The reason the fix looked like it had only moved the blank from left to right: **the table grid was
drawn to the CANVAS width, not to the column extent.** Odd-row shading (`DrawRowBackgrounds`) and
every horizontal row rule (`DrawCellBorders`) ran to `canvasSize.W`. The header band already stopped
at the last column, so the mismatch was invisible on any table whose columns fill the box — and on
the reported file it was not: `DCTest.cdd` has a plot 200 px wide holding one 115-px column, leaving
85 px of horizontal rules with no cell under them. That reads as exactly one blank, header-less
column. Both now stop at `GridRight(layout, canvasW)` = the last column's right edge, clamped to the
canvas.

Gated by a pixel probe (`ScalarCubeTests.Scalar_GridStopsAtLastColumn`) rather than a column-plan
assertion, because the column plan was already correct — the defect was purely in what got painted.
Verified to catch it: restoring either `canvasSize.W` turns the test red at x = 120.

## Who owns a left-drag inside a plot, and the Autoscale command (2026-08-21)

Follow-ups to the panning work above, once panning actually worked.

### The drag is ONE gesture and only one thing can have it

*"when a plot is added to a Data Display, the Lock Axes Panning is turned off by default, but user
cannot pan due to Data Display drag moving of the plot itself."*

`PlotContainerView.axaml` set `EnablePanning="False"` on its `PlotControl` outright, and the
container took every left-drag to move the plot. So **Lock Axes Panning was inert in the Data
Display** — the menu item toggled a flag nothing consulted. It worked in Match Designer for a reason
worth knowing: **Match Designer hosts `PlotControl` directly** (`MatchDesignerWindow.axaml`), with no
container and no drag-move, which is why every panning report so far came from there.

A drag inside a plot is either an axis pan or the container's move/select gesture. `Axes.LockedPanning`
now decides which, and the two halves are:

- **Locked** — `PlotControl` leaves the press unhandled, `PlotContainerView` moves and selects
  exactly as before.
- **Unlocked** — `PlotControl` takes the drag AND marks the event handled. The handled flag is the
  load-bearing part: without it the container also starts a move and drags the plot out from under
  the pan. It also means the container never sees the press, which is why click-selection had to be
  reproduced (below) rather than left to it.

**Every new plot starts LOCKED** (owner: *"So we get the same behavior as before despite these new
changes"*). The default lives in `Plot(PlotType, FreqUnit)` — the "new plot" constructor — and
deliberately NOT on `Axes`, because `LockedPanning` means nothing outside this control (harmonicaRF's
panels never read it) and a model-wide default of "locked" would assert something untrue about axes
in general. `AxesConfig` does not persist the flag, so a `.cdd` load goes through the same
constructor and lands locked too.

**Trap that would have silently undone it:** `Plot.SetPlotType` swaps the whole `Axes` object (one
per plot type, kept in `_axesStorage`) and **constructs a fresh one for a type visited for the first
time** — which would have reverted the lock to the `Axes` default and left a locked plot suddenly
undraggable after a type change. The lock is carried across explicitly.

### Selection works in both states; only the drag differs

*"Allow a plot to be selected even if Locked Axes Panning is turned on."*

Read as: selection must not depend on the lock state at all. Locked was already fine (the container
handles it). Unlocked was not — this control swallows the press to keep the pan and the pointer
capture with it, so `PlotContainerView`'s release-time click-selection never ran. Handing the press
back is not an option: the container would take the capture and the pan with it. So `PlotControl`
reproduces the click half itself on release, when the pointer travelled less than
`ClickSelectThreshold`. That constant is 4.0 and **must equal `PlotContainerView.DragThreshold`** —
they decide the same question about the same gesture, and a plot needing a different amount of
stillness than the one beside it just feels broken. A test holds them together.

The right-button (secondary-axis) drag deliberately does not select, matching the container, which
ignores anything but the left button.

### Plain scroll zooms an unlocked plot's axes

*"if Lock Axes Panning is turned off the scroll wheel will zoom in/out when the mouse cursor is over
the plot. (When mouse is outside the plot, the scroll wheel zooms the data display instead)"*

`PlotControl.OnPointerWheel` zoomed the axes on Ctrl+scroll only; it now also does so on a plain
scroll while the plot is unlocked. **"Over the plot" needs no hit test** — the event only reaches
this handler when the pointer is over the control. And "outside the plot zooms the display" needs no
new code either: returning unhandled is what gives the canvas the wheel, since
`PlotCanvasView.OnContentGridWheel` bails on an already-handled event. Ctrl+scroll still zooms the
axes in both lock states, unchanged.

### Autoscale from the context menu — two silent defects in one four-line handler

*"The plot doesn't render as autoscaled after I issue the command. (I need to start a pan to see it
render properly)"*

1. **It never repainted.** `Plot.Autoscale()` mutates the axes model, which raises no notification,
   so the new window was computed correctly and simply never drawn — until some later gesture
   happened to invalidate the visual, which is exactly what starting a pan does. Every other
   window-changing handler in the file already called `InvalidateVisual()` + `PlotChanged`; this one
   did not.
2. **It could do nothing at all.** `Plot.Autoscale()` is gated on the per-axis autoscale flags, and
   the Axes Limits panel clears them the moment a user types a limit — so the command was a silent
   no-op for precisely the user who would reach for it. It now passes `force: true`, which bypasses
   the gate for that one call **without rewriting the flags**, so the user's standing preference for
   later automatic autoscales survives.

### A pan is an EDIT: dirty the document, and make it undoable

*"changing the axes panning does not dirty the .cdd document; also an axis panning change needs to
be undoable."*

The axis windows are saved state (`AxesConfig.Window*`), so moving them is an edit like any other.
But the only signal `PlotControl` raised was `PlotChanged`, and that path
(`PlotContainerViewModel.OnPlotChanged`) refreshes bindings and rebuilds marker info boxes **without
ever reaching `ContentChanged`**. So a pan changed what would be written to the file and the document
still looked saved — closing without saving lost it silently.

**The two halves are one fix.** `DataDisplayViewModel` wires `UndoRedo.StateChanged` to
`RaiseContentChanged`, so recording the pan as an undo entry dirties the document by construction.
The new `AxesWindowCommand` records BOTH windows: a Rect plot's right-hand axis has its own, and
undoing a pan that moved only one of them still has to put both back, or the secondary silently
ratchets. Restoring also resets `WindowState`/`WindowSecondaryState`, since those are what the NEXT
pan translates from — without it the first pan after an undo jumps back to where the window used to
be.

**Wheel zoom and Autoscale move the same state and get the same treatment.** Not scope creep: they
had the identical non-dirtying defect, and leaving them would mean a display that loses a zoom on
close but not a pan.

**Coalescing, and the trap in it.** A pan ends at pointer-release, so one drag is one entry. The
wheel has no end, and one entry per notch would bury the rest of the history — so a zoom EXTENDS the
entry already on top. The obvious condition ("same plot, top of stack is an axes entry") is wrong,
and a test caught it: a zoom straight after a pan found the pan's entry and extended it, so one undo
jumped back past both and the pan vanished as a separate step. An entry is only extendable if a
repeated gesture created it — `AxesWindowCommand.Coalescable`, set only on the wheel path.

**Not changed, deliberately: the Lock Axes Panning toggle still does not dirty the document.**
`AxesConfig` does not persist `LockedPanning`, so marking the document dirty for it would claim an
unsaved change that saving cannot capture. Persisting it is a separate decision, not this fix.

Gate tests: `tests/Ui.Tests/AxesWindowUndoTests.cs` (10),
`tests/Ui.Tests/PlotPanLockDefaultTests.cs` (14) and
`tests/Ui.Tests/PlotAutoscaleCommandTests.cs` (4), each verified to fail against the unfixed code.
`PlotControl`/`PlotContainerView` are Avalonia controls this suite does not instantiate, so their
wiring is gated against the source with comments stripped — everything expressible on the model is
gated on the model. `dotnet test tests/Ui.Tests` 8,547 passed; `tests/Firewall.Tests` 6 passed.

## Panning with a right-hand axis, marker glyph clipping, and the marker's own label (2026-08-21)

Three owner reports against Match Designer's response plots, all reproducible in the Data Display —
the plots are the same `PlotControl`/`PlotRenderer` in both.

### 1 — the right-Y traces "glitch out" while panning

*"If Lock Axes panning is turned off, and I drag within the plot, the right-y axis trace rendering
glitches out as I pan the plot's axis."*

`PlotControl.OnPointerMoved` converted the pointer delta to world units **once**, with the PRIMARY
axis's scale, then handed that same world number to `Axes.TranslateSecondary`. The two axes do not
share a scale: the right-hand window is a different world range over the same pixels. So the right
axis panned by `Δpx / primaryScale` world units when it should have panned `Δpx / secondaryScale` —
wrong by the RATIO OF THE TWO Y RANGES.

That ratio is not small in the plots this was reported against. Match Designer's magnitude plot is
|S11| against |S21|; its phase plot is degrees (360 units) against group delay. A 40 dB left axis
under a 360° right axis is a factor of **nine** — the right-hand traces slide nine times too slowly
under the cursor while the grid they are drawn against moves at full speed, which is what reads as
"glitching" rather than as a plain offset.

**Both conversions now live in one place** — `Axes.TranslateFromPointer(dxPx, dyPx, primaryX,
primaryY, secondaryX, secondaryY)` — precisely so a caller cannot hand one axis's scale to the
other again. `PlotControl` passes `tf.Primary.*` and `tf.Secondary.*` from the transform set it
already built. The X delta is converted the same way, for the same reason; it is identical while
the two windows share an X range (they normally do), and correct if they ever do not.

Not a bug in `LockedPanning` itself — that flag was doing exactly what it says. It is only how the
report is phrased, because with panning locked the wrong arithmetic never runs.

### 1b — the tick numbers "wiggle/glitch slightly" on the axis you are NOT panning

*"as I pan left or right in an axis, the y-axis and right y-axis numbers and the ticks wiggle/glitch
slightly … (same with x-axis when I pan up or down in the axis)."*

A separate defect from §1, found while gating it. **The tick geometry was innocent:** a
provably PURE horizontal pan leaves every Y tick's canvas position bit-identical (measured, not
argued — the left- and right-margin pixels are unchanged frame to frame), and the mirror holds for a
pure vertical pan. So the model was never the problem.

**Nobody drags along an exact axis.** A "horizontal" drag carries a few tenths of a pixel of Y, and
the pointer delta is a fractional number of pixels to begin with. Unrounded, those tenths went
straight into the world window, and the whole Y tick column was re-rasterized at a NEW SUB-PIXEL
PHASE on every pointer event. Measured with a realistic jitter track (|dy| < 0.5 px): **~700 changed
pixels in the left Y numbers and ~900 in the right ones, every frame.** That is the shimmer.

The right axis is the worse of the two, which is why the report names it separately. With
`SecondaryShareGrid` (the default) its tick VALUES are derived from
`(y − Window.Top) / Window.Height` — so a sub-pixel change in `Window.Top` does not merely re-place
the right-hand numbers, it **re-numbers** them.

**Fix: `Axes.TranslateFromPointer` quantizes the delta to whole canvas pixels** (and
`TranslateSecondaryFromPointer` does the same for the right-button drag). One rounding fixes both
halves:

- the orthogonal axis does not move at all until the pointer crosses a whole pixel, so an
  axis-aligned drag leaves it **pixel-identical**; and
- the axis being panned translates by an EXACT integer. A tick's canvas position is
  `world·scale + offset`, and the offset term absorbs the pan exactly — `after = before + Δpx` — so
  with an integer Δ every glyph keeps its sub-pixel phase and simply slides, rather than being
  re-rasterized.

Round the **accumulated drag-start delta**, never a per-event increment: `dxPx`/`dyPx` are measured
from `_dragStartScreen`, so the result is a clean staircase with no accumulated drift. Whole-DIP
granularity is the right quantum here because a Rect plot's canvas size IS the control's `Bounds` —
the container sizes plots by `ZoomLevel` rather than applying a render transform, and `PlotRenderer`
forwards `zoomLevel` only to `TableRenderer`.

### 1c — the root cause: the draw operation was reading the LIVE plot

The one that actually mattered, and the one the earlier fixes only masked. Reported as *"still
glitchy … I even see some ticks leave the world space and render outside the rect plot's box"*,
with the decisive clue arriving a message later: **"I don't see it much if I pan slowly with mouse.
But if I pan fast with the mouse, it becomes way more obvious."**

Speed-dependence rules out geometry — every candidate above is speed-invariant. It points at the
frame being composed from state that is *moving underneath it*.

`PlotControl.Render` only RECORDS an `ICustomDrawOperation`; the compositor executes it afterwards.
`PlotDrawOperation` captured the **live `Plot`**, so the drawing code read `Axes.Window` at
execution time — by which point further pointer events had already moved it. Worse, the draw path
reads the window MANY times per frame: once for the world→canvas transform, again inside
`Axes.Ticks()`, again for every tick-mark and gridline endpoint. A pan landing between two of those
reads yields a frame whose tick VALUES came from one window and whose TRANSFORM came from another —
and ticks then land wherever the mismatch puts them, **including outside the axes**. That is the
reported symptom, precisely, and its magnitude is how far the window moved between two reads: a slow
drag hides it, a fast one does not.

It is also why quantizing the pan (§1b) helped without curing it — quantization shrinks the
per-event delta at LOW speed, which is exactly where the artefact was already hard to see.

**Fix: `Plot.RenderSnapshot()`**, taken in `PlotControl.Render` and handed to the draw operation in
place of the live plot. `MemberwiseClone` plus a fresh `new Axes(Axes)` — deliberately not a
hand-written field list, because the renderers read a long tail of plot state (title, custom axis
labels, table layout, format strings) and a copy that forgets one renders the frame WRONG rather
than failing. The traces are shared on purpose: their geometry is rebuilt only on a structural
change, never during a pan. So is the trace collection — whose `CollectionChanged` subscription
still belongs to the live plot, so the snapshot adds no handler and can trigger no autoscale.

**The general rule this is an instance of:** anything handed to an `ICustomDrawOperation` must be a
value the frame owns. The file already said as much about its `aliasFor` delegate ("so
PlotDrawOperation stays a snapshot of what THIS frame needs, matching every other captured field on
it") — every field was a snapshot except the one that mattered.

### 1d — gridline shade flicker, from an exact-equality dedup

Found while gating §1b. `Axes.Ticks` walked each axis by repeated addition (`v += step`) and then
separated minor ticks from major ones by putting the major VALUES in a `HashSet<double>` and
dropping exact matches. With a spacing of 0.2 — which `CalcInterval` returns constantly, and which
has no exact binary form — five accumulated 0.2s are not bit-equal to one accumulated 1.0, so the
dedup silently failed and the minor gridline was painted over the major one in the lighter minor
paint: **three of every four major gridlines rendered in the wrong shade.** Which ones failed
changed as the window moved (measured over a 400 px pan: `.XXX` → `..XX` → `X.XX` → `.X.X` →
`XXXX`), so the gridlines visibly changed shade while dragging.

The Y axis never showed it, which is why it went unnoticed for so long: its spacings came out 2 and
4, and doubling a power of two IS exact.

Now `Axes.Lattice(from, to, step, skipEvery)` generates a tick as `n · step` and identifies a major
as `n % majorMultiplier == 0` — an integer fact that cannot round off. Multiplying by the index also
means a given gridline's value is identical at every pan offset, which repeated addition could not
promise: the value depended on how many additions it took to reach it, and that count changes as the
window moves.

### 1e — the Rect grid was the only grid drawn unclipped

`DrawPolarGrid` and `DrawSmithGrid` have always clipped to the plot box; `DrawRectGrid` did not. The
tick lattice is absolute, so a pan repeatedly brings the window boundary onto it, and a gridline
sitting exactly ON an edge has half its 1.25 px stroke outside the axes — a full-height line one
pixel beyond the axis, appearing and disappearing as the window moves. The tick NUMBERS stay
unclipped (they live in the margin by design), and so does `DrawBorder`, whose frame is meant to
straddle the edge at full thickness.

**Worth recording because it cost time: a single-frame "is there ink outside the box" check cannot
gate this**, and two attempts at one passed against the unfixed code. The escaped gridline hides
inside the border's own stroke, and the tick numbers out there swamp any clipped-vs-unclipped diff.
Comparing FRAMES is what works — the border and the numbers are static and cancel, leaving only what
the pan is wrongly moving. `Pan_HorizontalDragWithJitter_LeavesBothYNumberColumnsPixelIdentical`
gates §1b and §1e together, and was verified to fail for each on its own.

### 2 — marker glyphs pan outside the axes

*"as I pan, the glyph for any markers attached to any trace will pan out of the plotting area — it
should be clipped to be within the axis limits. (Exception to Smith and Polar where it is allowed to
pan into the corners of the circle if necessary.)"*

`PlotRenderer.Draw` clips traces, contour fills and multi-marker lines to the viewport, then calls
`canvas.Restore()` — and only afterwards draws marker SYMBOLS. So a marker whose data point had been
panned out of view kept drawing its triangle and its name over the tick labels and past the axes.
The VSWR locus in the very same loop was already re-clipping itself; the symbol never was.

`MarkerRenderer.DrawSymbol` is now wrapped in the same `viewportClip` that loop already computes.
**The owner's Smith/Polar exception falls out of this rather than needing a branch:** a complex
plot's viewport is the SQUARE that bounds the chart circle (`ComputeViewport`), so clipping to the
viewport rect still permits a marker in the corners outside the circle. One clip, both behaviours.

**Trap in gating this.** The obvious fixture — pan the marker off the bottom of the plot box — puts
the glyph at canvas y ≈ 751 on a 500 px bitmap, i.e. off the IMAGE, where an unclipped renderer also
writes no pixels and the test passes for the wrong reason. `MarkerGlyph_PannedOutOfThePlotBox_…`
therefore pans horizontally into the left margin (glyph at x ≈ 68 px, plot box starts at 120 px) and
asserts the fixture's own geometry before it asserts the pixels. It was checked to fail with the
clip removed; the paired `…_InsideThePlotBox_StillDraws` exists so deleting the marker renderer
outright could not satisfy the first one.

### 3 — the marker readout and the Y-axis label spelled one quantity two ways

*"I plot an S(1,1) trace and the y label renders as 'S(1,1) dB20' on a rect plot. However, the
marker is rendering it as 'dB(S(1,1))'. I don't want these two text renderings to drift, 'S(1,1)
dB20' is the correct."*

Round 7 (2026-08-20) moved the AXIS label onto `TraceLabeler`'s display language for both trace
kinds. It did not move the MARKER readouts, which still read `Trace.Description` /
`ShortDescription` — the function-call form. So one plot showed one trace named two ways, in the
axis and in the box beside it.

The per-trace half of the labeller is now public as **`TraceLabeler.QuantityFor(trace)`** (the same
`BuildNetworkQuantity`/`BuildCubeQuantity` pair `ComputeMinimalLabels` uses, minus the source
component), and `Trace.ReadoutDescription(showFilePrefix)` wraps it with the file-stem prefix.
Six call sites moved onto it: `GetMarkerValString`, `GetStemValString`, `GetEditorDataLine`,
`BuildCubeMarkerBoxLines`, both multi-marker row builders, and the info box's own
"Change to Trace…" submenu.

**`Description`/`ShortDescription` are deliberately unchanged.** They are the trace's own
description, and `BuildPickerYExpression` reads `ShortDescription` as an EXPRESSION fallback — a
trailing `" dB20"` suffix would not parse there. Changing them to fix a label would have broken spec
round-tripping instead, which is why the round-7 note already refused it and why the fix belongs at
the readout call sites. `ShortDescription_StaysTheExpressionForm` gates that refusal.

Gate tests: `tests/Ui.Tests/PanAndMarkerLabelTests.cs` (32). `dotnet test tests/Ui.Tests` 8,519
passed; `tests/Firewall.Tests` 6 passed.

## Plot versus (`Gain vs Pout`): the X side is a FIELD, not expression text (2026-08-19)

Owner's ask: plot one cube slice against another in a Rect plot (and a Table), with families
(Gain-vs-Pout, one curve per RFfreq), an X label that matches what is plotted, and a marker readout
that names the X quantity. Syntax approved before any code: `Y vs X`. Spec: `docs/design/plot-versus.md`.

**The design decision everything else follows from: `XSpec` is its own field on `Trace`, not part of
`Expression`.** The obvious implementation — let `Gain vs Pout` be an ordinary multi-cube expression —
fails for a structural reason that is easy to miss until the card is in front of you:
`SetCubeDataFrom` routes an `Expression` with no `CubeName`/`Slice` to `TraceExpression`, and
`CommitSpec` deliberately NULLS `CubeName`/`Slice` for a multi-cube expression so the axis-role editor
shows nothing stale. So every versus trace would have lost its axis rows, its family toggles, and its
pinned-axis labels — i.e. the feature would have been unusable from the card it was asked to be usable
from. Held as a separate field, the Y side keeps its identity and NOTHING on the card changes.

**Three bugs the tests caught, each from a different direction:**

1. **The compounding `vs`.** `BuildPickerExpression` = Y-half + `" vs " + XSpec`, and the Y half falls
   back to `ShortDescription` when `Slice` is null — which reads `Expression`, which already ends in
   `vs Pout`. One more edit and the spec reads `Gain vs Pout vs Gain`. The fallback now takes only the
   Y half of its own description. A fallback that reads a field the caller is about to rewrite is a
   loop waiting for a second edit.
2. **The card's error message never survived its own edit.** Every `CommitSpec` ends in
   `RebuildAndNotify`, which re-resolves the trace and OVERWRITES `ExpressionError`. So "No loaded data
   source named 'measured'" and "Only one 'vs' is allowed" were both set correctly and both gone by the
   time the UI read them. Fixed by moving BOTH messages into the resolve path: the card parks the raw
   `alias::Cube` text on the trace and `VersusResolver` reports the unmatched alias; `SetCubeDataFrom`
   re-checks the separator itself. **One error path, evaluated where the display reads it.**
3. **A Table's X column is sorted and de-duplicated** — correct for a sweep axis, wrong for a versus X,
   which is the VALUES of another quantity: Pout folds back past compression and can repeat, so sorting
   reorders the pairing and a repeat collapses two sweep points into one row. `TableColumn.PairByIndex`
   keeps sweep order and reads cell i from sample i.

**A family's shared X is the one structural assumption that had to change.** `SetFamilyData` took ONE
X array for all curves; a versus family cannot (Pout at 2.0 GHz is not Pout at 2.4 GHz).
`FamilyCurve.RawX` is per-curve, `BuildFamilyPath` uses `fc.RawX ?? _cubeXValues`, so every ordinary
family is untouched. The marker readout reads the MARKED curve's X — the trace-level array is curve 0's
and would misreport every other curve.

**There is no unit for a versus X, and there cannot be one from the data.** `DataCube` carries units on
**axes only**; cube VALUES have no unit anywhere in the model. So the X label reads `Pout`, exactly as
every Y label already reads `Gain`. Found while checking this: `AxesLimitsViewModel.XUnitLabel`
hardcoded `({FreqUnits})` for every Rect plot, so a Pin sweep's X limits ALREADY said "(GHz)" before
this feature existed — re-pointed at the new `Plot.XAxisUnitLabel`.

**Per-trace X label rows.** Rect drew ONE X label (from `Traces[0]`) while Y labels have always been one
per trace, in the trace's colour. `Plot.XLabelsDiffer` now drives one X row per trace when the X
quantities disagree, the Rect viewport shrinks to make room, and the Y labels' `" dimension mismatch"`
suffix is suppressed on exactly those plots — the rows say it better, and on a `Gain vs Pout` beside
`Gain vs Pin` plot the difference is deliberate rather than a mistake.

Tests: `PlotVersusTests.cs` (28 — grammar, resolution, families, cross-source, labels, Table, markers,
persistence) and `PlotVersusCardTests.cs` (9 — the card flow, typed specs, alias binding). Full
`tests/Ui.Tests` green (8,083).

### Round 2 — three things the card got wrong, all reported from one real trace (2026-08-19)

Owner's trace: `mag(Gp_dB[~, :]) vs HB1.V[~, :, "Vout", 2]`. Three separate defects fall out of it.

1. **"There doesn't seem to be a family button when the vs X checkbox is on."** The X rows only ever
   showed *Fix*, on the reasoning that X and family are inherited and therefore not the X side's to
   choose. That reasoning is right and the UI built from it is still wrong: the family marker was
   simply ABSENT from the half of the card the user was looking at, which reads as a missing control,
   not as an inherited one. **Every X-cube axis now gets a row with the same X/Fam/Fix control,
   DISABLED for the inherited ones**, with a tooltip saying it follows the Y side. Only a foreign
   axis's Fix value is editable. *An inherited state has to be shown, not omitted.*
2. **"Selecting a transform in the vs X space changes the y transform."** There was only one transform
   combo, and it is the Y side's — while the X axis must be REAL and `HB1.V` is complex. So the one
   control the user could reach moved the wrong half of the trace and the X side stayed unplottable.
   The vs row now has **its own transform**, written into the X spec as a function call
   (`mag(HB1.V[…])`) rather than a new field — that is the form `CubeTraceSpecParser` already reads,
   so a typed spec and a picked one are the same object. Picking a complex X **defaults to Mag**
   instead of parking the trace on an error, and None/Conj are not offered for a complex X.
   The X transform is read back from the SPEC first (combo second), or typing a spec without one
   would silently re-acquire the last picked transform on the next edit.
3. **"The expression did not result in an invalid y-label rendering."** The complex-X refusal reached
   `ExpressionError` and the card's spec box — and nowhere else, so the curve vanished with a label
   that still looked correct. The reason is structural and worth remembering: **the Y label is built
   from the trace's AUTHORING state** (`TraceLabeler.BuildCubeQuantity`: cube + pins + transform),
   which is perfectly well-formed when the RESOLVE failed. `RectValueInvalid`/`ScalarOnNonTableInvalid`
   are the only two failures it ever knew about, and both are *rendering* failures. It now appends
   `<invalid>` for any unresolved binding (`InvalidSpecText`/`ExpressionError`) — which also covers a
   cube missing after a re-run and a bad typed spec, neither of which said anything on the plot before.

Tests: `PlotVersusCardTests` grew `ComplexX_GetsItsOwnTransform_AndTheYTransformIsUntouched`,
`XAxisRows_ShowTheInheritedFamily_AndOnlyForeignAxesAreEditable`, and
`AFailedVersusBinding_MarksTheTraceInvalidOnThePlot` (the owner's exact expression, before and after
`mag()` on the X side). **The card tests also had to stop constructing a cube-bound trace with a NULL
`Slice`** — the picker cannot produce that state, and it was quietly sending the Y side down the
multi-cube expression path.

### Round 3 — the card's own churn, and a role that only looked shared (2026-08-19)

1. **"Selecting Measurements in the vs-X group combo blanks the source combo."** `RebuildXPicker` ran on
   every card refresh — i.e. after every edit anywhere on the card — and unconditionally cleared and
   rebuilt `XSourceEntries` from new item objects. Clearing an ItemsSource a ComboBox is bound to drops
   that ComboBox's selection, so the source combo went blank until the next refresh re-selected it.
   This is the `src/Ui/CLAUDE.md` ComboBox note from the other direction: **a stable item list is part of
   the contract, not an optimisation.** Every collection on the vs row now rebuilds ONLY when its content
   actually differs, and re-points its selection either way.
2. **"The X transform is set to mag when I switch X from a complex to a scalar value."** Right — the
   transform was carried across the signal change and only *upgraded* to Mag for a complex cube, never
   cleared for a real one. It is now re-derived from the NEW quantity alone (real → None, complex → Mag),
   which is the convention the Y side's own signal switch already followed.
3. **"X / Fam / Fix are just text — I can't click them."** They were disabled Borders, on the reasoning
   that the roles are inherited. But **inherited is not read-only** — the role is ONE state shared by
   both halves, so the buttons are now live and apply to the Y side's row, reusing its cascade. A
   FOREIGN axis (one the Y side lacks) genuinely can only be pinned, so it now shows no role buttons at
   all rather than two dead ones.

**The bug the third fix's test found, which nobody had reported yet:** an explicit X spec kept the roles
it was composed with. Press Fam on RFfreq and the Y half became `Gain[:, ~]` while the X half still said
`…[:, 0, …]`, which the resolver correctly refused — so the family silently produced nothing. The Y-side
flush (`FlushSliceAndRebuild`, which every Y role edit goes through) now regenerates the X spec from the
new slice before composing the expression text. **Editing one half of a mirrored pair has to rewrite the
other half, not just re-render it.**

### Round 4 — the shared axes needed a sentence, not a control (2026-08-19)

Three revisions of one row, all from owner reports, and the third made it worse:

1. shared axes omitted → *"there doesn't seem to be a family button when the vs X checkbox is on"*
2. shown, disabled    → *"they're just text, I can't click on them"*
3. shown, live        → *"does it make sense to have Pin as X, Fam and Fix as well? I am confused with
   the vs X area"* — because the vs row was now a literal duplicate of the trace's own axis rows a few
   centimetres above, editing the same state.

**The requirement was never a control.** What a user needs about a shared axis is to KNOW that the
family and the swept axis apply to the X half too — one sentence — while the editing stays where it
already was. Final shape: `Shares the axes above: Pin = X, RFfreq = fixed at 2.40 GHz` (the pinned VALUE,
not an index, since that is the frequency the X data is read at), and rows ONLY for axes the X quantity
has and the Y side does not.

**Diagnostic worth keeping: "two controls for one piece of state" is the smell.** Each fix was locally
reasonable and the set of them oscillated, because the question being answered ("how should this control
look?") had the wrong subject. When a second control appears for state something else already owns, the
answer is usually to delete it and say the thing in words.

En route, one genuine gap from the same report: **a shared axis's pinned VALUE was read-only in the vs
area** while its role was editable — the user set RFfreq to Fix and then could not say which frequency.
That inconsistency is gone with the rows themselves; the value lives on the axis row above, which has
always had a picker.

### Round 5 — two blank-card bugs with the same shape, and neither was in the data (2026-08-19)

Both reported as data problems, both actually the CARD failing to reflect a trace that was fine.

**1. "When I check the vs X checkbox, the data source combobox blanks (no options)."** Round 3 added a
CACHE (`_allXSignals` + `_xSignalEntry`) so the vs collections only rebuild when their content changes —
which fixed the churn that was dropping the source combo's selection. But the *teardown* path, which
clears the visible collections when a trace stops being versus, did not know about the cache: it emptied
`XGroups`/`XSignals` and left `_allXSignals` populated. So the next rebuild compared the stale cache
against an identical wanted list, concluded "nothing changed", and refilled nothing — untick, re-tick,
empty combos. **A cache and the thing it describes have to be torn down together**, which is why there is
now one `ClearXPicker()` instead of a clear at each call site. Reproduced against the owner's own
`results/Test.npy` before fixing, and the regression test was checked RED against the pre-fix code.

**2. "Copy and paste a plot with a plot-vs-X trace → the pasted trace has its vs X disabled."** The paste
is fine: `TraceConfig.XSpec` round-trips, the pasted trace keeps its X binding, and it goes on plotting
against Pout. What is wrong is that the **card's** vs state is synced by `RefreshDescription`, which
`TraceRowViewModel`'s constructor never calls — the identical hole `TraceCardConstructionInitTests`
documents for the network-metric row, rediscovered on a new row. A card built over an already-versus
trace (pasted, undone/redone, or restored from a `.cdd`) therefore came up unticked with an empty picker.
The constructor now calls `SyncVersusFromTrace()` beside the existing `RefreshNetworkMetricCard()`.
**Any new card state that RefreshDescription owns needs the same constructor call** — that is now twice.

Also here, from the same report's evidence: the default X quantity used to be "the first cube in the file
that isn't Y", which on a real run is a raw complex HB voltage — so the feature opened on an X that could
not be plotted without a transform. It now prefers a REAL sibling in the Y quantity's own group
(`Gain` → `Pout_dBm`), which is the PA case the feature exists for.

### Adjacent, and NOT part of this feature: a swept HB tone that never moves (2026-08-19)

Testing the family case was blocked by a run-time error that looked like a sweep bug and was not:

```
analysis HB1 type=hb Tone="2" ToneUnit=GHz ...
P1Tone:P1 ... Freq=RFfreq GHz
analysis HB1_sweep_RFfreq type=parametric_sweep Var=RFfreq Start=2 Stop=4 Step=1
→ Commensurability check failed: source 'P1' Freq=3E+09 Hz is not on the HB tone grid {f0=2E+09 Hz…}
```

The source's frequency follows the swept variable; the **analysis's own Tone is a literal**, so the HB
grid stays at 2 GHz and every point past the first is off-grid. **This is not a hand-authoring slip —
the shipped template `src/Ui/resources/schematic-templates/FET_Harmonic_Balance_Sweep.csch` carried it**:
its P1Tone is `Freq = RFfreq GHz` and its global is `RFfreq = 2`, but its HB card's `ToneExpr` was the
literal `"2"`, so the template's own frequency sweep could never run past its first point. Fixed in the
template (`ToneExpr: "RFfreq"`, `ToneUnit: GHz` — which starts at the same 2 GHz and now follows the
sweep). `Tone` has always accepted an
expression (`HbEngine.Resolve` → `FreqUnit.ResolveHz`), so `Tone="RFfreq"` is the whole fix — verified
end to end through `Cli hb` on the owner's own netlist (3 × 2 points, clean Pout/Gain family).
The message named the source, which is the half that is RIGHT, and sent the reader to the wrong place.
`HbEngine.SweptToneHint` now names the variable the source is following and the fix, gated on that
variable's current value actually matching the off-grid tone (accepting the unit-less spelling too,
since `Freq=RFfreq GHz` applies the unit at the use site). Test:
`P1ToneTests.T7b_OffGridSourceFollowingASweptVariable_NamesTheVariableAndTheFix`.

## A curve tracer auto-opens as a curve tracer: the probe current, as a family (2026-08-18)

Owner: *"User runs DC analysis and sweeps VGS + VDS for a Curve Tracer. There is a probe usually called
IDS or IP1. Can a new data display automatically populate with the family? Expression is
`DC1.I[~, :, "IDS"]`."* Same session as the measurement-preference entry below, and the same argument
one step further: seed the thing the designer asked to observe, plotted the way the run was set up.

**Two independent things were wrong, and the axis order is the key to both.** Verified by running the
shipped `FET_Curve_Tracer` nesting (DC1 ← sweep VDS ← sweep VGS) through `ParametricSweepEngine` and
exporting it — not read off the code:

```
[] V              rank=3  VGS:5 x VDS:6 x node:3(n_g,n_dd,n_d)
[] I              rank=3  VGS:5 x VDS:6 x branch:1(IDS)
[] __ProbeBranches rank=1 probe:1(IDS)
```

**Each `parametric_sweep` nesting level PREPENDS its axis**, so the OUTERMOST sweep is axis 0 and the
innermost is last. That is the fact the whole slice rule turns on.

1. **The wrong axes.** `BuildDefaultSlice` takes the first non-structural axis as X — VGS — and pins the
   rest, giving drain current against the GATE voltage at VDS = 0: a flat line. `BuildSeedSlice` now takes
   the INNERMOST swept axis as X and promotes the outermost to `AxisRole.FamilyIterate`. Structural axes
   stay pinned at index 0 *carrying their label*, which is what puts `"IDS"` in the expression instead of
   a bare index.
2. **The wrong cube.** With no measurements in the run, the seed took the first plottable cube — `V`, i.e.
   a node voltage (and the first node is the gate supply, so the plot was literally the sweep against
   itself). A placed `IProbe` is the same kind of signal as a measurement expression: something the
   designer explicitly put there to be seen. So a probe-current cube now outranks raw outputs, below
   measurements.

**`__ProbeBranches` is what makes rule 2 exact.** A DC run's `branch` axis IS the probe list, but an HB
run's is not — it enumerates every DEVICE branch (`M1:g`, `M1:d`, …), so "prefer a labelled branch axis"
would have re-pointed every bare HB run to an arbitrary device current for no reason. Both engines write
`__ProbeBranches` with the placed probes, so `IsProbeCurrent` requires the branch labels to MATCH that
list. DC matches; HB does not.

**Two deliberate limits.**

- **A cube with a `freq` axis is left alone.** Frequency is always the natural X, so the default slice was
  already right and an S-parameter run already opened on a readable plot. Promoting its sweep to a family
  broke `SparamRunAddTraceTests.SweptS_Family`, which documents the seeded-then-manually-promoted
  contract — the test was right and the rule was overreaching.
- **The family is capped at `Trace.MaxFamilyCurves`** (101), the renderer's own guardrail rather than a
  second number. The renderer clamps a long family and says so; a SEEDED trace showing the first 101 of a
  500-point sweep would be claiming to be the whole picture, so past the cap the axis is PINNED instead —
  keeping the corrected X, since current against VDS at one gate voltage is still the right pair of axes.

Result, from the real run: `I[~, :, "IDS"]` — five curves (one per gate step), six points each (the VDS
sweep), family axis VGS. *Note for anyone testing this:* a family trace's data lives in
`Trace.FamilyCurves`, and `Trace.Points` stays EMPTY — asserting on `Points` reads as "the seed renders
nothing" when it is working.

## The auto-seeded trace prefers a MEASUREMENT, and the group is what says so (2026-08-18)

Owner: *"if an HB analysis is performed with many high level measurement expressions in the schematic …
it is unlikely that the user will want to plot voltage at a specific node. Instead, let's plot a
measurement expression … I just want to setup the user with something that makes it easy for them to
customize the data display."*

A run that auto-creates a `.cdd` seeds one trace through `AddTrace` →
`PlotInspectorViewModel.FirstPlottableCubeName`, which returned the literal first plottable cube. For a
swept HB that is `V`, of rank 3: **`[Pin × node × harmonic]`**. The default slice pins all but axis 0, so
the display opened on the voltage at one arbitrary node at one arbitrary harmonic — for a schematic whose
entire content is measurement expressions.

**Measurement cubes are filed in their own DataSet group, and it survives the `.npy`.**
`SchematicRunService` writes them to `DataSet.MeasurementsGroup` (`"measurements"`), which is
bare-resolvable like the default group, and `NpyWriter` records the group list. So the preference needs no
heuristic at all — no guessing from cube names, no inferring "already reduced to the sweep" from the
absence of a `node`/`harmonic` axis. **Established by exporting a real run and reading it back**, not from
the code: `Cli hb` over a `parametric_sweep` of six Pin values wrote

```
[]             V              rank=3 Complex  Pin:6 x node:6 x harmonic:5
[]             Converged      rank=1 Real     Pin:6
[measurements] Pin_avail_dBm  rank=1 Real     Pin:6
```

and the seeded trace is now `Pin_avail_dBm` with six points running −10 → 0 dBm.

**Emitted BARE, no `measurements.` prefix** (owner, on seeing it): a measurements-group cube
bare-resolves (`DataSet.Resolve` tries the default group then the measurements group), and both the trace
picker (`TraceRowViewModel.RebuildSignals`) and the expression parser (`TraceExpression`) already emit
these bare for exactly that reason — so `FirstPlottableCubeName` qualifying them was the one place out of
step, and it put a prefix in the expression box that the user never needs to type. Only ANALYSIS-group
cubes stay qualified, because a bare `V` would resolve to the wrong group.

**Which measurement: the first REAL one in enumeration order.** That order is the order the designer
declared them in — the only opinion the dataset carries about which one matters — and for the shipped
`FET_Harmonic_Balance_Sweep` template it is `Pin_avail_dBm`, the owner's own pick, for the reason they
gave: a plain 1:1 line against the swept drive is immediately readable. Real before complex because a
complex cube needs a transform chosen before it renders as anything.

Everything else is unchanged: the loadpull path still takes its two contour plots ahead of this, and a run
with **no** measurements (DC, a bare HB, an imported Touchstone) seeds exactly the cube it always did. The
change is one extra preference inside one method, so the manual **Add Trace** button gets the better
default for free.

*Test trap worth knowing:* a seed that names the right cube but resolves to nothing renders an empty plot,
which is worse than the old behaviour and invisible to a name-only assertion. `AutoSeedTracePrefersMeasurementTests`
asserts the resolved point count and the X range as well as the cube name.

## A cube marker's frequency lives in its POSITION (2026-08-13)

Switching a trace from S(1,1) to a stability circle sent its marker to 0 Hz, where every lookup read
NaN. Two independent faults, both on the S-param → circle direction:

1. **`PlotControl.SnapMarkerToTrace` deliberately never assigns `Marker.Freq` for a cube trace** —
   `CubeMarkerIndex` re-derives the sample from the position on every read — and markers are
   constructed with `freq: 0.0`. So a marker that has only ever lived on a cube trace has
   `Freq == 0`. Harmless there, fatal the moment the trace becomes network/derived, where `Freq` IS
   the identity. New `Trace.CaptureMarkerFrequencies()` reads it out of the position (nearest cube X
   sample, frequency axis only — a Pin sweep has no frequency to carry and must not invent one), and
   `OnSelectedSignalChanged`'s network branch calls it **before** clearing the cube binding, while
   the cube X values are still there to read.
2. **The `Derived` setter's own snap loop matched with `f == m.Freq - 1e-6`** — an exact float
   comparison against a *shifted* value, so it never matched and every marker fell through to
   `Data.Frequencies.Length - 1`. The marker was snapped to the LAST frequency's circle while its box
   still reported the original frequency. It also zeroed `PositionStatic` first, so "nearest point on
   the circle" was measured from the origin and the marker teleported. Now: `NearestFrequencyIndex`
   (nearest, never equality — a frequency arriving from another mode is not bit-identical to a
   sample), position kept, shortest move onto that frequency's circle.
   `FindNearestPointOnStabilityCircle` also gained a `freqIndex < 0` guard and now returns the
   `+real` perimeter point when queried exactly at the centre, instead of null (which left the marker
   off the locus).

Tests: `StabilityCircleMarkerTransitionTests` (5), verified to fail without the fix (4 of 5 red).

**Verified on the owner's own file** (`results/dataset.cdd` + `dataset.npy`, the non-uniform 4-port):
marker 0 Hz → 1.19 GHz, landing 1.9e-8 from that frequency's circle perimeter, angle preserved.

**Not a bug, checked in full:** the same file's marker reported `Γ=1.023∠−15.14°` alongside
`impedance=374.8∠−95.07° Ω`. Those are consistent — 50·(1+Γ)/(1−Γ) = 374.9∠−94.99°, i.e.
−33.1 − j373.3 Ω — and the marker sits 0.000000 from the 1.19 GHz source-stability circle. |Γ| > 1
on a stability circle is ordinary (µ′ = 1.015, unconditionally stable, so the whole circle lies just
outside the unit disc and every point on it is a small negative resistance). The confusion was the
**MA display format**: `FormatImpedance` renders through `Marker.FormatComplex`, which honours the
marker's `MatrixFormat`, so an impedance can appear as magnitude∠angle — or, in DB mode, as
`20·log10(|Z|) dB`. Left as-is, but that is where to look first if this is reported again.

## A cube binding and a derived metric are mutually exclusive — cube wins (2026-08-13)

Picking "Load Stability Circles" on a simulated 4-port run and then picking `S(1,1)` again left the
trace in **both** states: `OnSelectedSignalChanged`'s cube branch never reset `Trace.Derived` (only
the network branch did). One cause, two symptoms that looked unrelated:

- **S(1,1) never appeared.** `Trace.BuildPath` tests `IsCubeBound` **before** `IsDerived`, so it
  built a cube path and filled `Points` — but `TraceRenderer.BuildPath` branches on
  `IsStabilityCircle`, which was still true, so it drew the **stale circle geometry** and ignored
  `Points` entirely. Nothing in the cube path clears `StabilityCircleCentres`/`Radii`/`StableInside`
  — only `BuildDerivedPath`/`BuildMatrixPath` do, and neither runs for a cube trace.
- **The In/Out port selectors stayed on the card.** `TraceRowViewModel.ShowPortSelectors` reads
  `Trace.Derived`. The property *was* re-raised by `RefreshDescription` → `RefreshNetworkMetricCard`
  — it just still answered "yes". A property-change notification proves nothing when the underlying
  state is the thing that is stale.

**Fixed on the two setters that make a trace cube-bound** — `Trace.CubeName` and `Trace.Expression`
are now real properties that call `DropDerivedForCubeBinding()` on a **non-null** assignment (the
null guard matters: the network branch sets both to null *before* assigning `Derived`, and a drop
there would fight it). One rule covers the picker, a typed spec, and a `.cdd` load. `Trace.Derived`'s
setter now also clears the circle geometry when set to `None`, so the geometry cannot outlive the
mode that owns it whoever does the reset.

`.cdd` files saved in the broken state load correctly without extra work: `DataDisplayViewModel`
restores `Derived` only on its non-cube-bound branch.

Tests: `StabilityCircleSignalSwitchTests` (6) — the round trip both ways, the geometry/`Points`/card
state after the switch, the two setters at model level, and the null-assignment guard. Verified to
fail without the fix (3 of 6 red).

## Stability-circle marker impedance (2026-08-13)

A marker on a load/source stability circle sits on a Γ-plane **locus**, not on an S-matrix element,
so its impedance must come from the marker **position** at the reference the locus lives in. Two
defects, both in `Trace.GetMarkerImpedanceString`:

1. **The per-port ("unusual source") branch ran before any derived check** and read `S[Row, Col]` —
   which for a derived trace is **S11**, since `Derived`'s setter forces `Row = Col = 0`. The readout
   was a constant that did not move when the marker did. Only reachable on a non-uniform source,
   which is where it surfaced. The derived case is now handled **first**, ahead of both branches.
2. **The reference was `Trace.Z0`/port 1's**, but `BuildDerivedPath` hands the circle routines the
   2-port from `NetworkMetrics.TwoPortUniformReal`, which renormalizes **both** ports to
   `Re(z0[InputPort−1])`. New `Trace.DerivedGammaReferenceZ0` mirrors that target exactly — so it is
   the input port's reference, not port 1's, and its **real part only**. Change one without the other
   and the drawn circle silently disagrees with every readout taken on it.

`MarkerReferenceZ0` returns it for any derived trace, which also fixes the info box's `Z0=` line and
the VSWR locus reference. It deliberately **ignores `Z0OverrideEnabled`/`Trace.Z0`**: the Z0 control
is not shown for a derived trace and `BuildDerivedPath` never consults it, so honouring it would
report an impedance the drawn circle does not have.

Also extracted `Trace.SourceZ0PerPortResolved(nPorts)` — the "true per-port references" expression
was copied verbatim in three places (`BuildDerivedPath`, `GetDerivedMetricArray`, `MuString`), and
all derived paths must agree on it or the curve and its readouts diverge.

Tests: `StabilityCircleMarkerImpedanceTests` (7), verified to fail without the fix (5 of 7 red).

## Z0 override is the gate, not a label (brief-dd-z0-nonuniform-override, 2026-08-13)

**Corrects the brief below.** Z1/Z2 made the renormalization to `Trace.Z0` *unconditional* — the
Override checkbox only controlled whether the Z0 *box* was editable, and `Trace.Z0` was seeded from
the source's **port-1** reference. For a uniform source that is invisible (port-1's value IS every
port's value). For a run with per-port `Term` impedances it is destructive: every port was silently
re-referenced to port 1's value, so a design genuinely matched into a 12 Ω port-2 read its match
against 50 Ω instead. Owner's report: a −20 dB return loss displayed as −4 to −6 dB.

**The rule now, and it is one sentence per state:**
- **Override OFF ⇒ absolutely no renormalization**, whatever the source's per-port references are.
  The trace shows the source's own data; `Trace.Z0` is a read-only mirror of port 1 and is not used
  to transform anything.
- **Override ON ⇒ every port renormalized to the user's uniform `Trace.Z0`**, starting from the
  source's true per-port `SourceZ0PerPort` (so a non-uniform source is accounted for, not ignored).

**`Trace.Z0OverrideEnabled` is the single gate**, and it lives on the MODEL, not just the row VM —
every renormalization site consults it: `PlotInspectorViewModel.ResolveNetworkParamCube` (the cube
path's one interception point), and, on the network/SNP path, `Trace.BuildMatrixPath` /`DataPoint`
/`GetMarkerDataPoint`/`GetMarkerImpedanceString`, in both their per-port and uniform branches.
`TraceRowViewModel` mirrors the checkbox into it (including from the ctor, so a `.cdd`-restored or
copied trace keeps its state) and it persists as `TraceConfig.Z0Override`.

**What is deliberately NOT gated:** derived quantities (µ, µ′, |Δ|, MaxGain, stability circles) and
Z/Y conversion still renormalize internally and unconditionally. Those are only *defined* at a
uniform real reference — that renormalization is mathematics, not a display choice.

**Marker readouts follow the same reference, per PORT.** New `Trace.MarkerZ0` — the port's own
reference with Override off, the uniform override Z0 with it on — replaces bare `Trace.Z0` in the
impedance readout, the info box / marker editor `Z0=` line, and the VSWR locus reference
(`PlotRenderer`, `PlotControl.ResolveVswrPlaneAndZ0`). Without it an S(2,2) trace on a 12 Ω port
reported its impedance against the port-1 50 Ω. The cube path gets its port index from the pinned
`"i"` slice (`CubeReflectionPortIndex`); the network path uses `Row`. **The impedance is
reference-independent and the tests pin exactly that**: port 2 of the 50↔12 Ω transformer reads
12 Ω *both* with Override off (S22 = 0 against 12 Ω) and with Override on at 50 Ω (S22 = −0.6129
against 50 Ω) — two different Γ values, one physical impedance. That round trip is the strongest
available check that both branches are right.

**Two consequences worth knowing.** The `" @ Z0=…"` Y-label token is now gated on Override too — with
Override off nothing was re-referenced, so the token would misstate the reference (this includes the
old "an unusual source is always re-referenced" arm, which no longer holds). And
`OnZ0OverrideEnabledChanged` now rebuilds in BOTH directions: turning Override *on* changes the
rendered data for a non-uniform source before the box is ever edited, which the old
off-branch-only rebuild never redrew.

**Test oracle worth reusing:** an ideal 50 Ω ↔ 12 Ω transformer, `S = [[0,1],[1,0]]` at its own port
references — a perfect match. Renormalized to a uniform 50 Ω it reads |S11| = (50−12)/(50+12) =
0.6129 → −4.25 dB, *exactly* the reported symptom. The two behaviors are separated by the largest
possible margin and the expected value is hand-checkable. `Z0NonUniformOverrideTests` (10 tests);
`PerPortZ0ComputeTests.NonUniformSource_S_RenormalizesOnlyUnderOverride` replaces the old
`NonUniformSource_S_Renormalizes`, which asserted the unconditional behavior.

## Z0 renormalization — S-param and loadpull traces (brief-dd-z0-renormalization, 2026-08-13)

Five slices, Z1–Z5. One Z0 field per trace (`Trace.Z0`, already `Complex`, default 50+j0) — no
`stos()` expression function (§0's decision). Builds on brief-dd-network-params-and-stability
(`NetworkMetrics.IsNetworkParamCubeSpec`/`ConvertSCube`) and brief-dd-plot-type-integrity, both
above.

- **Z1 — cube-bound S/Z/Y traces render at `Trace.Z0`.** New `RfCore.Data.NetworkMetrics.
  RenormalizeSCube(sCube, z0Src, z0New)` mirrors `ConvertSCube`'s per-leading-axis-block loop but
  calls `RFNetwork.SToS` instead of `SToZ`/`SToY` — a whole-matrix operation, never an element-wise
  shortcut. `PlotInspectorViewModel.ResolveNetworkParamCube` is the single interception point: called
  from `SetCubeDataFrom` right after `var cube = ds[t.CubeName]`, before ANY slicing — so it's the
  one place that feeds Rect/Smith/Polar/Table AND every marker/table readout downstream (via
  `_cubeComplexValues`). Identity fast path (every port's source Z0 already equals `t.Z0`) returns
  the cube unchanged — a default-50Ω trace on a default-50Ω source is byte-identical to before this
  brief. Also stamps `Trace.SourceZ0PerPort`/`SourceZ0IsUnusual` for cube traces — **reusing the same
  two fields the network/SNP path already used** (no parallel fields), classified via
  `DataSetBuilder.ClassifyZ0` (not UniformReal ⇒ unusual). `TraceRowViewModel.IsCubeNetworkParamTrace`
  mirrors `ShowMatrixTypeCombo`'s existing cube-branch pattern; `ShowZ0Control`/`ShowZ0Row` now cover
  both a scattering network trace and a cube-bound network-param trace. A freshly-picked network-param
  cube signal (`OnSelectedSignalChanged`) seeds `Trace.Z0`/`Z0String` from the group's own port-1
  reference — otherwise a stale default 50Ω would silently renormalize a non-50Ω-native source the
  first time it's plotted, before the user ever touches Override.

- **Z2 — complex Z0 + reconsidering "unusual → disabled."** `RenormalizeSCube` rejects any
  `Re(Z0) ≤ 0` target with `ArgumentException` (the power-wave form divides by `√Re(Z0)`);
  `TraceRowViewModel.OnZ0StringChanged` catches parse failure AND `Re(Z0) ≤ 0` at entry, surfacing a
  new `Z0ErrorText`/`HasZ0Error` (wired as a tooltip + inline text on the Z0 box in
  `PlotInspectorView.axaml`) instead of silently no-op'ing or throwing three layers down. **The old
  "non-uniform source → Z0 box replaced by a static 'Multiple Port Normalization' label, no renorm"
  rule is gone** — `IsMultiPortNormalization` no longer gates `ShowZ0Control` at all; it's purely the
  "badge" signal now (`ShowZ0Badge`/`Z0BadgeTooltip`, extended to cover a cube network-param trace the
  same way `ShowZ0Control` was). No concrete correctness reason was found to keep the block —
  `RFNetwork.SToS` already renormalizes a per-port/complex source to a uniform target natively. The
  network path's three "renorm disabled" per-port branches (`Trace.BuildMatrixPath`, `Trace.
  DataPoint`, `Trace.GetMarkerDataPoint`) now renormalize the FULL matrix (`SToS(mat, sourceZ0,
  Z0Array(Z0, nPorts))`) before extracting `[Row,Col]`, same "matrix op first, then slice" discipline
  as Z1 — for `MatrixType.S` only; Z/Y stay computed straight from the raw per-port source (already
  correct, reference-independent — same invariant Z1 exploits, not a coincidence). The Smith/Polar
  chart grid itself is unaffected by a complex reference — only data-point positions move, confirmed,
  no rendering-math change.

  **Discovered while pinning §1's "order commutes" gate as a test (the brief explicitly asked for
  this instead of assuming it):** `RFNetwork.SToS` is the power-wave (Kurokawa) bilinear form —
  its own doc comment: uses `Conjugate(z0)` in its P/Q coefficients. `RFNetwork.SToZ`/`SToY` are the
  ORDINARY (non-power-wave) `√Z0` form — no conjugate anywhere. The two conventions coincide when Z0
  is real (conjugate is a no-op) but genuinely diverge for a COMPLEX reference. This is not
  introduced by this brief — `NetworkMetrics.TwoPortUniformReal`/`FullUniformReal` (R-stb-1..6)
  already restrict their own renormalization target to REAL for exactly this reason ("The uniform
  real target is the real part of the input port's reference impedance"). Practical consequence: a
  Z/Y cube trace's displayed value is invariant to `Trace.Z0` for a REAL override (pinned by
  `RenormalizeSCube_ThenConvert_CommutesWithDirectConversion_RealTarget`) but drifts slightly for a
  COMPLEX one (pinned, not swept under the rug, by `..._DivergesFromDirect_ComplexTarget`). Fixing
  the underlying gap (making `SToZ`/`SToY` power-wave-aware, or `SToS` ordinary-aware) would touch
  every S/Z/Y conversion call site in the engine and UI — out of scope here; documented at the call
  site (`PlotInspectorViewModel.ResolveNetworkParamCube`'s doc comment) so it isn't silently
  rediscovered later.

- **Z3 — Y-axis label token.** `Trace.RectYLabel` appends `" @ Z0=" + ComplexStringHelper.
  Format(Z0) + "Ω"` when `Trace.IsZ0ReReferenced` (new private property): for a network trace,
  non-derived `MatrixType.S` only (matches `ShowZ0Control`'s original network gating — a Z/Y network
  trace or a derived metric never exposes the Z0 field, so its `Trace.Z0` is an inert default that
  must never spuriously trigger a token) and `Z0 != Data.Z0`; for a cube trace, any network-param
  element (`SourceZ0PerPort` populated) and `Z0` differs from the source's per-port value. A
  genuinely non-uniform/complex ("unusual") source has no single native reference to be "unchanged"
  from, so the token always shows once `SourceZ0IsUnusual` — read as the resolution of the brief's
  "compare against the source, not literal 50" rule for that case. Byte-identical when unchanged
  (hard gate, pinned). Contour traces: `RectYLabel` already returns `""` immediately — untouched, no
  token path reachable.

- **Z4 — marker/table readouts.** `Trace.GetMarkerImpedanceString`'s `if (IsCubeBound) return ""`
  is now a real branch: new private `Trace.IsCubeReflectionElement` (bare cube name "S", pinned
  i == pinned j, read off `Slice`) gates it; because Z1 already renormalized `_cubeComplexValues` to
  `Trace.Z0` upstream, the formula is the SAME shape as the network path's uniform branch, just fed
  a cube-sourced sample — factored into one shared `Trace.FormatImpedance(s, z0, m)` used by both
  paths (no second formatter, per the brief). Off-diagonal or non-S cube-bound traces: `""`, no
  impedance meaning. `MarkerShowsImpedance` gained the matching cube branch. Wired into
  `BuildCubeMarkerBoxLines` the same way the network path wires it into `BuildMarkerBoxLines`. The
  network path's own per-port impedance branch (`GetMarkerImpedanceString`'s `SourceZ0IsUnusual`
  case) also switched from reading `sourceZ0[Row]` straight off the stored S to renormalizing the
  full matrix to `Trace.Z0` first (§2's alignment). Table cells need no separate change — they
  already read `_cubeComplexValues` via `FormatCubeCell`, fixed by Z1.

- **Z5 — loadpull Γ-grid renormalization (`RfCore`).** `LoadpullSurface.RenormGamma` generalized to
  `RenormGamma(Complex gammaSrc, Complex z0Src, Complex z0New)`: `Z2G(G2Z(gammaSrc)*z0Src / z0New)`
  — an exact generalization of the old real-only `z2g(50*g2z(X)/Z0)` (the algebra holds for a
  complex `z0New` too: `Z2G(z) = (z-1)/(z+1)` on a normalized `z = Z/Z0` reduces to `(Z-Z0)/(Z+Z0)`
  by pure cross-multiplication, no conjugation involved — unlike the SToS/SToZ gap above, THIS
  formula genuinely does commute for any complex reference). Short-circuits on `z0Src == z0New` so
  the default-50Ω case stays bit-exact. **`z0Src` default, stated plainly, not buried:** this
  codebase carries NO per-run "loadpull reference Z0" in the DataSet today —
  `LoadpullExportModel.cs` and the pre-brief `RenormGamma` both hardcoded 50 Ω for the stored Γ grid;
  `LoadpullSurface.AssumedSourceZ0 = 50.0` (new named constant, with the same comment) is where a
  future per-run reference would plug in if the loadpull format ever grows one.
  `double? z0` → `Complex? z0` widened on `Reduce`, `Fit`, `MaxPower`, `MaxEfficiency`,
  `MetricAtCoord`, `GetMxx` (private), `VswrCirclePoints`/`VswrBoundingBox` (private — already
  delegated to the public `Complex`-typed `VswrLocus`), and the `FitKey`/`LoadpullFit` records' `Z0`
  field (already part of `FitKey`'s record equality, so cache correctness — a different reference
  must not share a cached fit — falls out for free; pinned anyway since the brief calls it the
  highest-risk regression). **Deliberate non-goal:** `GetPowerSweep`/`BuildStackAtCompression`/
  `StackKey` were NOT widened — confirmed nothing in the UI calls `GetPowerSweep` yet (only
  `LoadpullPowerSweepTests` exercises it directly), so it's unreachable from the trace-Z0 wiring;
  leaving its `double? z0` as `double?` is intentional, not an oversight, so nobody "fixes" the
  inconsistency later without knowing why. Γ-plane only: `Reduce`'s renorm branch stays gated
  `plane == SurfacePlane.Gamma`, unchanged.

  `TraceRowViewModel.RebuildContour` computes `Complex? z0 = plane == Gamma ? Trace.Z0 : null` and
  threads it into every `surface.Fit`/`surface.Reduce` call and the `cd.EvaluateMetric` closure
  (`RecommendedMxx`/`RecommendedBox` already read `fit.Z0` internally once `fit` carries it — no
  separate param needed there). New `ShowContourZ0Control => IsContourTrace && PlotType is Smith or
  Polar` gates a NEW Z0 row inside the contour trace body in `PlotInspectorView.axaml` (the existing
  S-param Z0 row lives inside the `IsStandardTrace`-gated section, which a contour trace never
  enters — a contour and a network/cube trace never coexist on one `Trace`, so this reuses the exact
  same `Z0String`/`Z0OverrideEnabled`/`Z0ErrorText`/`IsZ0Editable` machinery, not a parallel one).
  `SeedZ0FromSource` gained a contour branch (seeds 50+j0, matching `AssumedSourceZ0`, since a
  contour has no "source port-1" concept). A contour's `RebuildAndNotify`/`BuildPath` sweep never
  reaches `RebuildContour` (`Trace.BuildPath` falls through to the network path for a contour trace
  — contour rendering is deliberately driven only by explicit `RebuildContour` calls, same as every
  `OnContourXxxChanged` handler already in this file), so the Z0 box's change handlers route through
  a new small `RebuildAfterZ0Change()` that calls `RebuildContour()` directly for a contour trace
  instead of `_parent.RebuildAndNotify()`. On the Z plane, `z0` is `null` regardless of what
  `Trace.Z0` holds, so a stale override from a prior Smith/Polar view cannot leak into a Z-plane fit
  — verified directly (editing Z0 via the normal VM path on a Rect-plot contour changes `Trace.Z0`
  but leaves `cd.Grid` byte-identical).

**Test-harness trap hit while writing the Ui-side gate tests (recorded so it isn't rediscovered):**
a `Trace` built directly with `CubeName` set but `Slice = null` and then handed to
`TraceRowViewModel`/`PlotInspectorViewModel` never resolves — `RebuildSignals`' own auto-select runs
with `_suppressDataCallback = true` (by design, to avoid the picker "revert bug"), so it sets the VM's
`SelectedSignal` display property WITHOUT ever calling `OnSelectedSignalChanged` and therefore without
ever writing `Trace.Slice` back. A test that then does `row.SelectedSignal = row.AvailableSignals.
First(x => x.Label == "S(1,1)")` picking the SAME item the auto-select already landed on is a
REFERENCE-EQUAL no-op under CommunityToolkit's generated setter — `OnSelectedSignalChanged` never
fires, `Trace.Slice` stays null forever, and every downstream `Points`/`_cubeComplexValues` read is
silently empty. `VirtualZYCubeTests`' existing tests all sidestep this by picking a port pair (or
`MatrixType`) genuinely different from the default before picking the real target; the new
`Z0RenormalizationTests.SelectSignal` test helper does the same by construction (bounces through a
different item first when the target is already selected) rather than relying on every test author to
remember it by hand.

**Tests:** `tests/RfCore.Tests/Z0RenormalizationTests.cs` (8) — `RenormalizeSCube` round-trip
identity, "order commutes" for a real target / diverges for a complex one (Re(Z0)≤0 throws), Γ-grid
`Reduce` identity at the default 50Ω / renormalizes to 25Ω matching a hand computation, fit-cache
distinguishes-then-reuses-identical, Z-plane fit unaffected by z0. `tests/Ui.Tests/
Z0RenormalizationTests.cs` (12) — cube S(1,1) at an overridden Z0 matches `RFNetwork.SToS` to 1e-6
(float `Points`, not 1e-12 — `Vector2` is `float`), two traces of one cube at different Z0 render
distinct loci, complex Z0 renormalizes and renders, `Re(Z0)≤0` refused with `Trace.Z0` left
untouched, a non-uniform per-port source renormalizes to uniform AND the badge still shows unusual,
the Y-label token (byte-identical / shown / 75Ω-source-at-75Ω-shows-none / contour-never-shows-one),
cube-bound reflection-element marker impedance matches the shared formatter exactly (off-diagonal has
none), contour Z0 control gated to Γ plane, and `RebuildContour` Z0 threading moves the Γ grid per a
hand computation while leaving a Z-plane grid provably untouched. Two PRE-EXISTING tests updated to
match the new (intended) behavior: `Z0OverrideTests.NonUniform_ShowsControlBoxWithBadge` (was
`..._ShowsLabelNoBox` — the box is shown now, not hidden) and `PerPortZ0ComputeTests.
NonUniformSource_S_Renormalizes` (was `..._NoRenorm` — an unusual-source S trace at the default Z0
now renormalizes against the true per-port source rather than returning the stored value verbatim).

`dotnet test tests/RfCore.Tests` — 298 passed. `dotnet test tests/Ui.Tests` — 6526 passed.
`dotnet test tests/Firewall.Tests` — 6 passed (separate invocations per the root `CLAUDE.md`).

## Loadpull contour UX round 8 (brief-dd-loadpull-contour-ux-round8, 2026-08-13)

Four slices, C1–C4. `src/Ui/Harmonica` untouched (verified via `git status`).

- **§1 — contour Mode-1 marker glyph.** Scope is narrowly the **ringed circle** glyph
  (`MarkerRenderer.DrawSymbol`, `marker.MarkerKind == Contour && !marker.ContourSnapped`) — the
  triangle (every other marker kind, AND a Mode-2/`ContourSnapped` contour marker) is untouched,
  per the brief's "keep the ContourSnapped/mode-2 distinction."
  - **Size:** `ContourMarkerRadius = max(6f, min(canvasW,canvasH)*0.020)`, replacing the old
    `ts*0.5f` — matches harmonicaRF's termination-marker rule
    (`HarmonicaPanelRenderer.DrawMarkers`) exactly, canvas-proportional (never × zoomLevel, per
    round-7 §2). Name font size for this glyph is now `r*1.15f` (was `SymbolTextSize(...)`).
  - **Name placement:** `marker.Name.Length <= 2` → centred INSIDE the disc, harmonicaRF's
    metrics (`PlexBold` at `r*1.15f`, baseline `centre.Y + ts*0.36f`, always black — the disc
    fill is deliberately light enough for this). `> 2` chars → unchanged: centred above at
    `dataPx.Y - ts - 4f`, in `theme.TextColor`.
  - **Fill colour:** `MarkerRenderer.ResolveContourMarkerFill()` (internal, unit-tested directly)
    — Bone colormap sampled at **t=0.5**, lightened toward white until luminance clears a
    **0.70 floor**. Both numbers picked by eye against a Bone-filled contour (no owner
    round-trip on the exact values yet — flag if they read too light/dark against a real render).
    Mirrors round-7 §3's `ResolveBaseLineColor` luminance-*ceiling* helper, inverted for a
    light-background need.
  - **`SymbolHitRadius`** updated in step: `1.5×` the new Mode-1 disc radius when applicable,
    else unchanged (`1.5×` `SymbolTextSize`).
  - Tests: `MarkerGlyphContourTests.cs` (6) — pixel-probe via `SKSurface`/`SKBitmap.GetPixel`
    (name-inside vs name-above ink presence), hit-radius formula at two canvas sizes, Mode-2
    untouched, fill luminance clears the floor. `SkiaFonts.TestOverrideTypeface =
    SKTypeface.Default` is required (`PlexBold` cannot load headlessly).

- **§2 — Rect contour trace defaults to `LabelSpacing = 150`.** Set in
  `PlotInspectorViewModel.AddContourTrace` alongside the other plane-dependent defaults
  (`plane == SurfacePlane.Z ? 150.0 : 30.0`) — `ContourData.LabelSpacing`'s own default (30) is
  untouched, so Smith/Polar and an already-saved `.cdd` are unaffected. Tests in
  `ContourTraceCardTests.cs` (`AddContourTrace_RectPlot_LabelSpacing150` /
  `..._SmithPlot_LabelSpacing30`) run against the real `Ideal_GaN_FET_1p6_mm_1p8_GHz.spl` fixture.

- **§3 — Heatmap withdrawn from the picker, code intact.**
  `TraceRowViewModel.ContourFillOptions` now returns `[None, Topography]` as a literal array
  instead of `Enum.GetValues<ContourFillSelection>()`. `ContourFillSelection.Heatmap`,
  `ContourFillKind.HeatMap`, `ContourFillType.HeatMap`, `ContourData.Scatter`, the renderer's
  heatmap branch, and even the (already-orphaned, never XAML-wired) `SetHeatMapFillCommand` are
  all untouched — restoring one list re-enables the experiment.
  **Verified, not assumed:** the `IconSelectButton`'s current-selection glyph binds its
  `ContentControl.Content` directly to `SelectedItem` (`PlotInspectorView.axaml`'s
  `ControlTheme` for `IconSelectButton`), NOT looked up against `ItemsSource` — so a `.cdd`
  saved with Heatmap selected shows "Heatmap" on the button (via `SelectedContourFill`'s getter,
  which reads `ContourShowFill`/`ContourSelectedFillKind` directly) rather than blanking. No
  fallback-to-Topography code was needed. Grepped the whole `DataDisplay` tree for
  `Heatmap`/`HeatMap`/`IsHeatMapFill` — no other UI surface exposes it.

- **§4a — Γ-grid vs impedance-grid detector.** `LoadpullRecognition.DetectGridPlane(ds, view)` +
  `GammaGridVswrThreshold = 15.0`, added next to `LoadpullRecognition`'s existing shape
  recognition in the same file. Reads `GammaLoad`'s geometry (max VSWR, clamping `|Γ|` at
  `0.999999` to dodge the `|Γ|→1` singularity, skipping non-finite points) since `GammaLoad` AND
  `ZLoad` are BOTH always emitted — cube presence cannot tell the two apart.
  **Real-fixture measurement (owner-verify recommended — see caveat below):**
  | fixture | kind | max VSWR |
  |---|---|---|
  | `Ideal_GaN_FET_1p6_mm_1p8_GHz.spl` (measured tuner) | Γ-grid | **19.0** |
  | `GaN_FET_1p6_mm_3_Freq.spl` (measured tuner) | Γ-grid | **19.0** |
  | `TestOut.spl` (measured tuner) | Γ-grid | **30.4** |
  | `ConvertedFile.spl` (measured tuner) | Γ-grid | **12.3** ⚠ under 15 |
  | `Hero3/hero3_at_compression.cnl` + `hero3_load.gam` (engine run, its OWN grid explicitly
    header-tagged `# gamma`) | Γ-grid | **9.0** ⚠ under 15 |
  | `Hero3/RLSweep.cnl` + `RLSweep.gam` (engine run, `# z`) | impedance-grid | **2.6** |
  Clean separation for the pair the brief asked to check (a representative measured-tuner Γ-grid
  vs. `RLSweep`'s impedance-grid): 19.0 vs 2.6. **But two other real, legitimately-Γ fixtures sit
  UNDER the 15.0 threshold** (`ConvertedFile.spl` at 12.3, and Hero3's own deliberately-modest
  21-point test grid at 9.0) — both would misclassify as impedance-grid under this rule. This is
  exactly the "heuristic, not a hard fact of the data" caveat the brief itself flagged; recorded
  here rather than silently tuning the threshold to paper over it. Not fixed — left as the
  documented limitation of a geometry-only signal.
  Tests: `LoadpullGridPlaneDetectorTests.cs` (6) — synthetic low/high-VSWR grids, the exact-`|Γ|=1`
  clamp case, a NaN-point skip case, missing-cube default, and one real-fixture check against
  `Ideal_GaN_FET_1p6_mm_1p8_GHz.spl`.

- **§4 — auto-created two-plot loadpull display.**
  `WorkspaceViewModel.PopulateLoadpullContourPlots(newVm, ds)` — **`internal static`** (not
  `private`) purely so `CircuitRF.Ui.Tests` can call the real production method directly via
  `InternalsVisibleTo` (`WorkspaceViewModel` can't be constructed headlessly, but this method
  needs no instance state). Wired into `AutoOpenOrCreateDataDisplayAsync`: when
  `LoadpullRecognition.IsLoadpull(ds)`, this replaces the old single-arbitrary-cube-trace path
  entirely; non-loadpull runs are byte-identical to before.
  - Reuses the tab's already-seeded plot as the LEFT plot; adds exactly one more via
    `DataDisplayViewModel.AddPlot` with explicit `left`/`top`/`width`/`height` — never
    `ComputeNewPlotPosition`'s inferred grid. Left at `(30,30)`; right at
    `(30 + width + 40, 30)` — a flat 40px gap, both plots the same size (square 420×420 for
    Smith, or `520 × 520/RectAspectRatio` for Rect, matching `AddPlot`'s and brief DD-P §2's
    own sizing rule). Not clamped against the ACTUAL canvas viewport size — at document-creation
    time `CanvasSizeProvider` isn't wired to a real view yet (same situation the pre-existing
    single-plot seed was already in, which places at a bare `(30,30)` with no viewport check
    either), so "fully inside the initial viewport" is asserted by convention/modest sizing, not
    measured against a live canvas rect.
  - Metric cube presence checked directly (`ds.Contains(group.Pout_dBm)` etc.) — a metric whose
    cube is absent is skipped, so a loadpull with only one of {Pout_dBm, Efficiency} yields ONE
    plot, not two-with-an-empty-one. If NEITHER exists (a loadpull recognized only via a
    different FOM like bare `Gt_dB`), this degrades to the existing "no default plot" warning —
    an edge case the brief didn't ask for and real loadpull output shouldn't hit (both are
    core headline FOMs the post-processor always adds when Pout/DE are present).
  - `ConstraintKind.Compression` / `ConstraintValue = 3.0` set explicitly via the VM setters —
    already `ContourData`'s own defaults, but pinned so a later default change can't silently
    retarget this auto-created display. Because CommunityToolkit's generated setters no-op when
    the new value equals the current one, this costs nothing extra in practice; only the
    `ContourMetricName` change (Pout_dBm is already the ctor's own default pick, so only the
    RIGHT plot's Efficiency assignment actually fires a rebuild) does real work.
  - **RBF-fit timing measured, not assumed:** `PopulateLoadpullContourPlots` (both plots' fits
    together) on the two largest real `.spl` fixtures — 145 grid points: **121 ms**; 435 grid
    points: **95 ms**. Not material; no spinner added, per the brief's own instruction.
  - Tests: `AutoCreateLoadpullContoursTests.cs` (6) — Γ-grid → 2 Smith plots (metrics, constraint,
    non-overlap), impedance-grid → 2 Rect plots, a grouped (`LPP1`) Loadpull-Pursuit-shaped run →
    same 2-plot result, missing-Efficiency-cube → 1 plot, non-loadpull recognition unaffected,
    and a full `.cdd` save/reload round-trip preserving both plots' type/metric.

### Test inventory added this brief
`MarkerGlyphContourTests.cs`, `LoadpullGridPlaneDetectorTests.cs`,
`AutoCreateLoadpullContoursTests.cs` (new), plus additions to `ContourTraceCardTests.cs`
(§2/§3 gates). `dotnet test tests/Ui.Tests` then `dotnet test tests/Firewall.Tests` — see the
in-repo `CLAUDE.md` §"`dotnet test` is fast by default" for why these are two separate
invocations.

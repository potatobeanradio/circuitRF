# DataDisplay — resolved briefs (detail, off the CLAUDE.md growth path)

`CLAUDE.md` in this directory was getting bloated from per-brief write-ups. Going forward, a
completed brief's detail lands here instead; `CLAUDE.md` stays for durable, still-true
conventions only. See the root `CLAUDE.md`'s own note about `src/Ui/HISTORY.md` for the same
pattern applied at the `src/Ui` level.

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

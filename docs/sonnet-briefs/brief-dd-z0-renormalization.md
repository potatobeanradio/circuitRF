# Brief DD-Z — Data Display: renormalizing displayed data to a new Z0 (S-parameters and loadpull)

**Goal (owner):** change the reference impedance of what is on screen — S-parameter data *and*
loadpull data on the Γ grid — without re-simulating; plot the same data at two different Z0 on one
Smith chart; support a complex Z0; show the reference on the Y-axis label only when it has been
changed; and have marker readouts respect it.

**Areas:** `src/Ui/DataDisplay` (trace card, `Trace`, contour rebuild, axis labels, marker readouts)
and `src/RfCore/Loadpull` (the Γ-grid renormalization is real-only and 50 Ω-hardcoded today — §3).
Do not touch `src/Core` or `src/Engine`.

---

## Verified anchors (on disk)

1. **Half of this already exists for Touchstone/network traces.** `Trace.Z0` is a `Complex`
   (`Models/Trace.cs:168-172`); `BuildMatrixPath` renormalizes with
   `RFNetwork.SToS(mat, Data.Z0, z0Array)` whenever `_z0 != Data.Z0` (`:995-999`); `DataPoint` does
   the same (`:1211-1215`); the card has a Z0 text box plus an **Override** checkbox
   (`TraceRowViewModel.cs:1861-1910`), parsed by `ComplexStringHelper.TryParse` (so complex entry
   already parses); and it persists — `TraceConfig.Z0` is a string round-tripped through
   `ComplexStringHelper` (`Models/DataDisplayConfig.cs:190`,
   `ViewModels/DataDisplayViewModel.cs:1388, 1547`).
2. **`RFNetwork.SToS` is fully general already** — per-port **complex** old and new references via
   the power-wave bilinear form (`src/RfCore/RFNetwork.cs:318-368`). No new math is needed for the
   S-parameter half.
3. **It is switched off exactly where the owner needs it:**
   - `ShowZ0Control => !_trace.IsCubeBound && IsScatteringTrace && !IsMultiPortNormalization`
     (`TraceRowViewModel.cs:1870`) — so a **simulated** S-parameter run (cube-bound, see brief DD-N)
     has no Z0 control at all.
   - The per-port "unusual Z0" path in `BuildMatrixPath` returns the stored S **as-is** with the
     comment *"renorm disabled"* (`Trace.cs:948-987`), and `Z0DisabledReason` tells the user to
     *"renormalize by re-simulating"* (`:1908`).
4. **Loadpull Γ-grid renormalization exists in RfCore but is unreachable and under-general.**
   `LoadpullSurface.Reduce`/`Fit`/`GetMxx` all take an optional `double? z0` and call
   `RenormGamma(coord, z0)` on Γ-plane coordinates (`src/RfCore/Loadpull/LoadpullSurface.cs:137-160`,
   `:172-190`). But:
   - `RenormGamma` (`:882-887`) is **real-only** and **hard-codes 50 Ω as the source reference**
     (`G2Z(gamma50) * (50.0 / z0)`).
   - `TraceRowViewModel.RebuildContour` (`:553`) never passes `z0`, so the UI never uses it.
   - The fit **cache key** `FitKey` already includes `z0` (`:93`, `:176`) — good, but widening the
     type changes the key. *(A stale fit cache was the actual root cause of the L9d bug — treat the
     cache as a first-class part of this change, not an afterthought.)*
5. **VSWR loci already read the trace's complex Z0** — `PlotRenderer.cs:286-291` passes
   `trace.Z0` (explicitly *"never drop the imaginary part"*) into `MarkerRenderer.DrawVswrLocus`.
   Loadpull's own VSWR helpers take a separate `double? z0ref` defaulting to 50
   (`LoadpullSurface.cs:330, 724`) — two references that must not be allowed to disagree.
6. **Marker impedance readout already honours `Trace.Z0`** for network traces
   (`Trace.GetMarkerImpedanceString`, `:2120-2151`) — but returns `""` immediately for
   `IsCubeBound` (`:2122`), so a simulated-S trace shows no impedance at all.
7. **Y-axis labels** come from `TraceLabeler.ComputeMinimalLabels` + `Trace.RectYLabel`
   (`Models/TraceLabeler.cs:38`, `Trace.cs:546`), drawn per-trace in `AxesRenderer.cs:589-616`.
   There is no Z0 token anywhere in that path. Contour traces return `""` from `RectYLabel`
   (`:548`) — consistent with the owner's "contours need no indicator".

---

## §0 — The design decision: a Z0 field on the trace card, not a `stos()` function [DECISION]

The owner asked which to build. **Recommendation: extend the existing per-trace Z0 field. Do not add
a `stos(z0_old, z0_new)` expression function.**

Reasons, in order of weight:
- **The per-trace field already exists and already persists** (anchor 1). This brief mostly *removes
  restrictions* rather than building a mechanism.
- **"Same data at two Z0 on one Smith chart" falls out for free**: add the trace twice, set a
  different Z0 on each. An expression function would need the same two traces anyway.
- **An expression function cannot renormalize a loadpull grid.** The Γ grid points are not a value
  in the expression's value space; they are the *fit domain* (anchor 4). The field can carry a
  reference into `LoadpullSurface.Fit`; a scalar function over cube elements cannot.
- **The expression engine's values are per-element Real/Complex/Bool.** A renormalization is a
  whole-matrix operation across ports at each frequency — expressible only by inventing
  matrix-valued semantics in a v1 language that deliberately has none (see
  `docs/design/expressions.md`).
- The old reference is **not** something the user should have to type: it is already known
  (`Data.Z0` / the source's `Z0` cube). Asking for `z0_old` invites a wrong answer.

A `stos()` function may still be worth adding later for scripted/exported workflows. It is
explicitly **out of scope** here; do not build it as a second path to the same result.

---

## §1 — Z0 renormalization must be available on simulated S-parameter traces [BUG/CHANGE]

Root cause: anchor (3) — the control is gated on `!IsCubeBound`, and a simulated run's S is a cube.

- Show the Z0 row + Override checkbox for a **network-parameter cube trace** (an `S` cube — and,
  once brief DD-N §2 lands, the derived `Z`/`Y` cubes) using the same "is a network-parameter
  trace" predicate DD-N §1/§2 introduces. Keep it hidden for ordinary cubes (`V`, `Pout_W`, …),
  which have no reference impedance.
- Apply it in the cube render path: when `Trace.Z0` differs from the source reference, renormalize
  each frequency's full N×N S matrix with `RFNetwork.SToS(mat, sourceZ0PerPort, Z0Array(traceZ0, N))`
  **before** slicing out the (i,j) element. Renormalization is a *matrix* operation — an element-wise
  shortcut is wrong and will silently produce plausible numbers.
- **Order with Z/Y conversion:** renormalize S first, then convert to Z or Y. (Z and Y are
  reference-independent quantities, so the two orders must agree — assert that in a test rather than
  assuming it.)
- **Brief DD-N §2 interaction:** if the virtual `Z`/`Y` cubes are memoized per entry, they are built
  at the *source* reference. A per-trace Z0 must not read a stale cache — either key the cache by
  reference or convert at render time from the (renormalized) S. State which you chose.

**Gate:** on a simulated 4-port run, `S(1,1)` with Z0 overridden to 75 matches
`RFNetwork.SToS`-of-the-source at 75 Ω to 1e-12; two traces of the same `S(1,1)` at 50 and 75 render
as two distinct loci on one Smith chart.

## §2 — Complex Z0 [CHANGE]

`RFNetwork.SToS` is already general (anchor 2) and `ComplexStringHelper` already parses; the work is
in *not blocking* it and in being honest about the two places it changes meaning:
- Accept and apply a complex override wherever a real one is accepted. Reject
  `Re(Z0) ≤ 0` (the power-wave form divides by `√Re(Z0)`) with a clear message rather than producing
  NaNs.
- **Reconsider the "unusual Z0 → renorm disabled" rule** (anchor 3). It exists because the *source*
  has per-port/complex references, not because renormalization is impossible — `SToS` handles
  per-port complex old references natively. Renormalizing a per-port source **to a uniform user Z0
  is exactly what such a user wants**, and it is what the metric path already does internally
  (`Trace.DataPoint`'s derived branch, `:1183-1189`). Enable it; keep an indicator that the source
  was non-uniform (the Z0 badge, Phase 7.2e) so the provenance is not lost. If you find a concrete
  reason it must stay disabled, report it instead of silently keeping the block.
- Smith rendering is unaffected: with `Re(Z0) > 0` the power-wave Γ of a passive network still lies
  in the unit disc. Do **not** redraw the Smith grid for a complex reference — the chart stays the
  standard one; only the data moves. Say so in the completion note, because it will look wrong to
  someone expecting a rotated chart.

**Gate:** Z0 = `50+10j` renormalizes and renders; `Z0 = -5` is refused with a message; a source with
per-port complex Z0 can be renormalized to a uniform 50 and the badge still shows the source was
unusual.

## §3 — Loadpull: renormalize the grid, not just the readout [CHANGE]

The owner is explicit: *"for the loadpull data, the grid points also need to be renormalized."*

- **Generalize `RenormGamma`** (anchor 4): it must take the **source's own** reference rather than
  the hard-coded 50, and accept a **complex** target. Signature becomes something like
  `RenormGamma(Complex gammaSrc, Complex z0Src, Complex z0New)`. The 50 Ω literal is only correct
  when the loadpull happened to be referenced to 50 — silently wrong otherwise. Take `z0Src` from
  the dataset's own `Z0` (the loadpull DataSet carries it; if it genuinely does not, default to 50
  **and say so in a comment**, do not bury it).
- Widen `double? z0` → `Complex? z0` through `Reduce` / `Fit` / `GetMxx` / `RecommendedMxx` and the
  VSWR helpers (`VswrLocus`, `VswrCirclePoints`, `VswrBoundingBox`), and through **`FitKey`** so the
  cache cannot serve a fit computed at a different reference (anchor 4 — this is the part most
  likely to produce a "works once, wrong after" bug).
- **Pass the trace's Z0 from `RebuildContour`** (`TraceRowViewModel.cs:553`) into `Fit`/`Reduce`, and
  make sure MXP/MXE, the recommended-VSWR search circle and the marker VSWR locus all use the **same**
  reference (anchor 5 — the trace's Z0, not the 50 Ω default) so the overlays cannot disagree with
  the grid under them.
- **Γ plane only.** On the Z plane a reference impedance is meaningless — the impedance grid does not
  move. Hide or disable the Z0 control for a contour trace on a Rect (Z-plane) plot, and make sure a
  stale Z0 on a plot-type switch cannot silently shift a Z-plane fit.
- The contour card gains the Z0 field only where it applies (Γ plane). Contours have no Y-axis label,
  so §4 does not touch them.

**Gate:** a Γ-grid loadpull contour renormalized from 50 to 25 moves its grid points, its iso-lines,
its MXP/MXE and any VSWR circles **consistently** (spot-check one grid point by hand against
`RenormGamma`); the fit cache returns a different fit per Z0; the Z-plane contour is unchanged by
any Z0 edit.

## §4 — Y-axis label shows the reference only when it has been changed [CHANGE]

- When `Trace.Z0` differs from the source's own reference, append a compact token to that trace's
  Y-axis label — e.g. `dB20(S(1,1)) @ Z0=75Ω`, and `@ Z0=50+10jΩ` for a complex value. Use
  `ComplexStringHelper.Format` so the text matches the card's box exactly.
- When it is unchanged, the label is **byte-identical to today**. This is a hard requirement: the
  common case must not grow a suffix.
- Add it in the one place that composes a trace's Y label — `Trace.RectYLabel` (anchor 7), beside
  the existing `<invalid>` / `dimension mismatch` suffixes — not in the renderer, and not in
  `TraceLabeler` (which computes *minimal* labels by dropping constant components; a Z0 token is
  per-trace and must not be de-duplicated away).
- Contour traces return `""` already — no change (owner: contours need no indicator).
- Comparison must be against the **source** reference, not against a literal 50: a source natively
  referenced to 75 Ω and displayed at 75 Ω has *not* been re-referenced and must show nothing.

**Gate:** a default trace's label is unchanged; overriding Z0 adds the token; clearing the override
removes it; a 75 Ω source at 75 Ω shows no token; a contour never shows one.

## §5 — Marker readouts respect the trace's reference [BUG]

- `Trace.GetMarkerImpedanceString` returns `""` for cube traces (anchor 6). Once §1 lands, a
  cube-bound S trace has a real reference and a real impedance — compute and show it, through the
  same formula the network path uses, at the **trace's** Z0.
- The per-port branch (`:2127-2137`) uses `SourceZ0PerPort[Row]` and ignores the override; align it
  with §2's decision (renormalize, then read out at the trace's Z0).
- **Contour markers:** a Γ-plane loadpull marker's impedance readout must use the trace's Z0, and
  must agree with the renormalized grid from §3 — the marker sits *on* that grid, so a mismatch is
  visible immediately.
- `MarkerInfoBoxViewModel` / `BuildMarkerBoxLines` and the marker **table** cells must all take the
  same path; do not add a second impedance formatter.
- Normalized readout (`UseNormalizedImpedance`, `Z0*(…)`) must normalize by the **trace's** Z0.

**Gate:** a marker on a 75 Ω-referenced trace reads the impedance computed at 75 Ω; the same point on
a 50 Ω copy of the trace reads the 50 Ω value; both agree with a hand computation; a contour marker
agrees with its own renormalized grid.

---

## Slice plan

- **Z1 — §1** enable + apply the override on network-parameter cube traces (needs DD-N's predicate;
  land DD-N §1/§2 first).
- **Z2 — §2** complex Z0 end-to-end, including the "unusual source" reconsideration.
- **Z3 — §4** the Y-label token (small, independent, immediately visible to the owner).
- **Z4 — §5** marker/table readouts.
- **Z5 — §3** the loadpull grid renormalization (`RfCore` change + cache key + UI wiring). Largest
  and most cache-sensitive; land last, alone.

## Constraints / gotchas

- **`RfCore` owns the math.** The UI passes a reference in and renders what comes back. §3's
  `RenormGamma` generalization is an `RfCore` change with its own tests in `tests/RfCore.Tests`.
- **The fit cache is part of the contract, not an optimization.** Any reference that reaches a fit
  must be in `FitKey`. Verify by fitting at 50, then 25, then 50 again and asserting the first and
  third are identical *and* the second differs.
- **Renormalization is a matrix operation.** Never renormalize a single S-element in isolation.
- **One reference per trace, used everywhere.** After this brief, the grid, the iso-lines, MXP/MXE,
  the VSWR locus, the marker readout, the table cell and the Y-axis label must all read the same
  `Trace.Z0`. A second default-50 path anywhere is the bug this brief exists to prevent.
- `.cdd` round-trip: `TraceConfig.Z0` already persists as a string; confirm a complex value survives
  save→load, and that a `.cdd` written before this brief still loads with the default reference.
- TreatWarningsAsErrors: nullable props → locals; no unused privates; no `<`/`>` in XML doc comments.

## Tests

- §1: renormalized cube-S element == `RFNetwork.SToS` reference value (1e-12); S→Z conversion
  commutes with renormalization.
- §2: complex Z0 renormalizes; `Re(Z0) ≤ 0` refused; per-port-complex source → uniform target works.
- §3: `RenormGamma` at a non-50 source reference (must fail against today's hard-coded 50);
  `FitKey` distinguishes references; MXP/MXE/VSWR overlays consistent with the renormalized grid;
  Z-plane fit unaffected by a Z0 edit.
- §4: label byte-identical when not re-referenced; token present and correctly formatted (real and
  complex) when it is; 75 Ω source at 75 Ω → no token; contour → no token.
- §5: marker impedance at 50 vs 75 on the same data; normalized form; contour marker vs grid.
- Two traces of one dataset at different Z0 on one Smith plot render distinct loci and persist.
- `dotnet test tests/RfCore.Tests`, `dotnet test tests/Ui.Tests`, `dotnet test tests/Firewall.Tests`
  (separate invocations — this SDK rejects two project paths in one).

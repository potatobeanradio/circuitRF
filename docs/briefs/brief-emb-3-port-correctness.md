# Brief EMB-3 — Lumped-port correctness: the axis, its report, and where the result lands

**Series:** EMB-0 … EMB-5 · **Tag:** `R-emb3-<n>`
**Area:** `src/Design/ThreeD/C3dPorts.cs`, `src/Cli/Check.cs` (3D port notes), `src/Design/Em3d/Em3dRunService.cs`
(`ResultKey`, `RunDirectory`), the `3d-view` reference text, `docs/user/src/reference/em-3d.md`, `src/Design/RESOLVED.md`
**Depends on:** —

---

## 0. The gap

**A lumped port with both ends stated could be driven across its width.** `C3dPorts` chose which pair of
opposite edges the stated conductors sit on by summing `ctx.Distance` from each conductor to each edge — and
`Distance` returns infinity for anything more than 2 DBU outside the conductor's bounding box. A sheet
stopped short of its metal (the reason to state both ends) scored every pair infinite and silently kept the
initial pair 0, the u axis. Every XY-plane port of a 15-port board was sent to Palace as `±X`; the four whose
gaps run along y were driven sideways. Nothing reported it: `check` printed *"runs … along +x"* for a gap that
runs along y. It was caught only because the 3D view draws the port's arrow from the same choice.

This was fixed for rectangular ports (F5, committed in `36ae6a845` without its test: midpoint distance with no cut-off, and a refusal when
nothing is measurable). **The cylinder-port branch has the same pattern** — `CylDistance(ctx, nc, patch, …)`
summed per pair with `(pair, negativeOnLow) = (0, true)` as the default (~line 527) — and was not changed.

Separately, the 3D run writes `<setup key>.<solver token>.s<N>p` (`ResultKey` = `ResolveResultKey(setup) +
"." + ResultToken`, e.g. `board Driven.palace.s15p`), while the `3d-view` format reference says the result is
named `<file stem> <setup name>`. A bench written from the reference points at a file that does not exist.

## 1. Requirements

**R-emb3-1 — F5 lands with its test, and the cylinder branch gets the same fix.** The stated-ends branch for
ports on a cylinder face measures each candidate edge at its midpoint (or along its length) with no box
cut-off, and refuses — naming the port, both conductors and "its direction is undecided" — when no pair is
measurable, instead of keeping pair 0. Extend the existing `src/Design/RESOLVED.md` F5 entry ("A lumped port with both ends stated was driven along
the wrong axis") with the cylinder case. **Why:** a
silent default for a port's axis produces a smooth, plausible, wrong S-matrix; the refusal costs nothing.

**R-emb3-2 — A port's direction is checked, not only printed.** `check` and `explain` state each lumped port's
direction beside its gap length and width, and warn when the direction is not along the line joining the
nearest points of its two conductors (or, for a rectangular sheet with both ends stated, when the chosen pair
is not the pair whose edges face the conductors). The sentence names the port and both axes: *"P5 runs along
+x, but its conductors face each other along y"*. **Why:** the GUI arrow is the only check today; headless,
nothing would have caught F5.

**R-emb3-3 — Inference and stated ends agree.** When both ends are stated AND inference would succeed, the two
must choose the same pair; `check` reports a disagreement as an error. (Today the stated branch never runs
inference, so a mistyped conductor name on a well-drawn port silently wins.)

**R-emb3-4 — The result is where the reference says.** Decide one of: (a) the reference and `em-3d.md` say
`<setup key>.<solver>.s<N>p` and give the static suffixes (`_es`, `_ms`, `_eig`) for the run folder, or
(b) the Touchstone drops the solver token when the setup runs one solver. Prefer (a) — results from two
solvers must not collide — and make `run analysis=em`'s result JSON carry the Touchstone path first, so no
caller has to construct it. **Why:** the name is predictable by design (`EmRunService.ResolveSnpPath`); the
reference must predict the same one.

## 2. Gates

- `C3dPortDirectionTests`: an XY sheet stopped 1 µm short of two pads facing along y, both ends stated →
  direction ±Y; the same along x → ±X; a cylinder-face port in each orientation likewise; a stated port whose
  conductors meet no edge → refused, not ±u.
- `check` on a fixture whose port is drawn across a y-gap but stated with the conductors swapped to an x pair →
  the R-emb3-2 warning naming both axes.
- `check` on a port whose stated conductors disagree with inference → error.
- Reference text: the `3d-view` topic's result-name sentence matches `Em3dRunService.ResultKey` for a driven
  and a static setup (string assertion against the generated reference).

## 3. Not in scope

- Port placement (EMB-1) and mesh embedding (EMB-2).
- Wave-port terminal assignment, which already infers per conductor and is unchanged.

---

## Completion note — EMB-3 (2026-11-01)

### R-emb3-1 — F5's test, and the cylinder branch fixed — done
- `C3dPorts`: the cylinder stated-ends branch now measures each edge at its MIDPOINT with no box cut-off
  (`CylDistance`/`Chords` take `bounded`, passed through to `C3dPortContext.Distance`). Before, a copper block 1 µm off
  the gap scored every pair infinite and the gap silently kept pair 0 (round the cylinder). A conductor that is itself a
  patch of the same cylinder was always measured analytically and was not affected.
- Both branches (planar and cylinder) now decide through one function, `C3dPorts.StatedPair`, which is stricter than
  "least summed distance": the chosen pair must beat the other by more than a DBU, and each stated end must be nearer its
  own edge than the opposite one. Otherwise the port is refused, naming the port, both ends, each end's distance to each
  edge, and "so its direction is undecided", with the remedy. The F5 "cannot be measured" sentence is kept for the
  infinite case.
- Decision: "a stated port whose conductors meet no edge" is read as stated ends that face no pair of edges — e.g. two
  conductors stacked over the sheet's middle (every distance equal) or both on one side of it. Least-sum alone still
  picked a pair for those.
- `src/Design/RESOLVED.md`'s F5 entry is extended with the cylinder case, the stricter decision, and the checks below.

### R-emb3-2 — the direction checked, not only printed — done
- New `src/Design/ThreeD/C3dPortChecks.cs`, called from `check` (`Check.CheckPortDirections`, its own method, one call
  after the port loop) and `explain` (`Explain.PortDirectionWalk`, its own method, one call after `PortWalk`).
  Nothing in `ExplainEm3d.cs` was touched.
- Per lumped port (info): `P1 runs along +y across a 48 µm gap, 100 µm wide.` (on a cylinder: "along its cylinder's
  axis" / "round its cylinder"), in the document's display unit.
- Warning `c3d.port.direction` when the direction is more than 45° off the line joining the nearest points of its two
  conductors to the port's centre; and, for a sheet with both ends stated, when the pair of edges that faces conductors
  (each edge midpoint against every conductor, two different conductors per pair, pruned by box distance) is not the
  chosen pair. Sentence shape: *"P1 runs along +x, but its conductors face each other along y: its bottom and top
  edges are 1 µm and 1 µm from 'south' and 'north', nearer than its left and right edges are to its stated ends 'west'
  and 'east'. Check its Positive and Negative, or redraw it across the gap between them."*
- Decision on the gate fixture "drawn across a y-gap but stated with the conductors swapped to an x pair": with the
  F5/R-emb3-1 chooser the stated pair's own line always agrees with the chosen direction, so it is the
  facing-pair clause that fires there. The nearest-points clause is the general safety net.
- Measured over every shipped and test `.c3d` (32 files) with the built CLI: no new warning or error from these.

### R-emb3-3 — stated ends agree with inference — done
- When both ends are stated, `C3dPortChecks` resolves a copy of the port with its ends cleared (in its own context, so
  a run's resolution still makes no contact query for a stated port — gate 1's counter unchanged). If inference
  succeeds and picks the other pair: ERROR `c3d.port.stated-disagrees`, naming the stated ends, the touched
  conductors and both axes. The same pair with different conductors: warning `c3d.port.stated-other-ends` (the
  mistyped-name case).
- Decision: it is a `check` error only; the run still takes the stated ends as the format says. Making the run refuse
  would mean running inference inside the stated branch, which the stated-ends contract excludes. Flag for the owner if a
  run refusal is wanted too.

### R-emb3-4 — the result is where the reference says — done, option (a)
- `3d-view` reference (`src/Cli/DocumentSchema.cs`, the C3d preamble): now spells
  `"<file stem> <setup name>.<solver>.s<N>p"`, `…<solver>_em.npy`, the run folder `…<solver>/`, and the static /
  eigenmode `_es`/`_ms`/`_eig` npy and run folders; `-o` moves the Touchstone only; `run analysis=em` lists the
  Touchstone first.
- `docs/user/src/reference/em-3d.md`: says what `<setup>` is for a `.c3d` (stem + space + setup name, with the
  `board Driven.palace.s15p` example) and the static run folders. Page regenerated with
  `DocGen --page` (`docs/user/reference/em-3d.html`). DocGen also rewrote `docs/user/assets/js/search-index.js`
  with ~1,800 lines of churn that come from pages already changed in HEAD without a regen (sensitivity, tuning,
  optimization, yield); I reverted that file, so the search index is regenerated at the series' end.
- JSON: a single-solver run already listed the Touchstone first. `Em3dRunService.Outputs` now lists every leg's
  Touchstone before any `.npy` for a multi-solver run, so `outputs[0]` is the S-parameters whatever was run.
  `RunBothTests.Gate3` (Benchmark tier, not run) updated to the new order.

### Tests
New `tests/Ui.Tests/ThreeD/C3dPortDirectionTests.cs` (8 cases): planar stated port stopped 1 µm short along y and along
x (also asserts the gap sentence); cylinder gap axial and azimuthal with box conductors 1 µm off; stated ends facing no
edge → refused; the four-pad fixture → the R-emb3-2 warning naming both axes and no error; the same with touching y pads
through `check` as a process → exit 1 with the R-emb3-3 error; the reference patterns compared against
`SnpBasePath`/`NpyKey`/`RunDirectory` for a driven and an electrostatic setup.

Run (targeted only): `C3dPortDirectionTests` + `SimulateGateTests` + `CylindricalGridSarTests` +
`TerminalWavePortTests` + `ModelFlagTests` → 48/48 passed; earlier also with `PortSnapTests`, `HierarchyGateTests`,
`C3dFragmentTests`, `Em3dGeneratorTests` → 79/79 passed.
`Firewall.Tests`: 78/79 — the one failure is `UserFacingTextGateTests` reporting 7 strings in
`src/Core/Devices/ComponentModelFactory.cs`, `src/Engine/Mom/BlockLowerMatrix.cs` and
`src/Engine/Mom/SymmetricFactorization.cs`. I did not touch any of those files; the failure was there before this work.

No UI change; no UiSnapshot. No EM run.

### Sibling overlap
- `C3dPorts.cs`: edits are in the planar stated-ends block (in `Lumped`), the cylinder stated-ends block (in
  `LumpedOnCylinder`), `CylDistance`/`Chords` signatures, three new private/internal helpers placed before
  `CylDistance`, and `Geometry3.ClosestOnTriangle` private→internal. The inference / contact code and the edge outline
  are untouched, so EMB-2's outline change should merge cleanly. It may conflict textually only if EMB-2 edits the
  same lines of the stated block.
- `Check.cs` / `Explain.cs`: one call line each plus a separate method. EMB-4's edits should not overlap.
- EMB-1 can rely on `StatedPair` for stated ends on both port shapes.

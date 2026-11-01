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

This was fixed for rectangular ports (F5, uncommitted: midpoint distance with no cut-off, and a refusal when
nothing is measurable). **The cylinder-port branch has the same pattern** — `CylDistance(ctx, nc, patch, …)`
summed per pair with `(pair, negativeOnLow) = (0, true)` as the default (~line 527) — and was not changed.

Separately, the 3D run writes `<setup key>.<solver token>.s<N>p` (`ResultKey` = `ResolveResultKey(setup) +
"." + ResultToken`, e.g. `board Driven.palace.s15p`), while the `3d-view` format reference says the result is
named `<file stem> <setup name>`. A bench written from the reference points at a file that does not exist.

## 1. Requirements

**R-emb3-1 — F5 lands with its test, and the cylinder branch gets the same fix.** The stated-ends branch for
ports on a cylinder face measures each candidate edge at its midpoint (or along its length) with no box
cut-off, and refuses — naming the port, both conductors and "its direction is undecided" — when no pair is
measurable, instead of keeping pair 0. Add the `src/Design/RESOLVED.md` entry for F5 and this. **Why:** a
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

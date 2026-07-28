# Sonnet Brief — Shapes must render filled during a live drag

Owner report: shapes used to render **filled** while being dragged, and stopped doing so around the
rendering-performance work. He wants the fill back at low shape counts and doubts it costs anything.

**He is right that it costs nothing — see §3. But check §1 first: this may already be fixed.**

---

## 1. Check this before writing any code

**I could not find any outline-only drag path in `LayoutRenderer`.** Specifically:

- `DrawLayer` substitutes `dragOverrides` for the stored shape and then renders it through the **normal**
  tier — same `fillPaint`, same `def.FillOpacity`. A shape being move-, vertex- or scale-dragged is filled.
- `DrawGhostShape` (the in-progress draw ghost) **also fills**, at `WithAlpha(60)`.
- Nothing in `LayoutCanvas` degrades `LayoutRenderOptions` during interaction — `ForceMergeTier` is never
  set, and the thresholds are constant across frames.

So the fill was not removed. Something is making it *look* removed, and the leading candidate is a bug
already being fixed.

**R-drag-1. Verify whether `brief-layout-testing-fixes.md`'s R-fix-1 resolves this, before changing
anything.** That fix normalizes contour winding when building batched paths. The connection:

- L2c's merge tier collapses a layer into **one** `SKPath` once `shapes.Count > MergeShapeCountThreshold`,
  filled once under Skia's **Winding** rule.
- **Outer rings are not winding-normalized**, so overlapping shapes with opposite vertex order **cancel**.
- Dragging a shape over others is exactly when overlaps appear — so the dragged shape and whatever it
  crosses would blank out mid-gesture and fill again on release. **That reads precisely as "fill stops
  during a drag," and it arrived with the performance work**, matching the owner's recollection.
- It is the same root cause as the "Group into Cell renders XOR'd" report, surfacing through the merge tier
  instead of the instance path.

If R-fix-1 resolves it, say so and stop — §2 is then unnecessary.

**Second candidate if it does not:** the ghost's `WithAlpha(60)` is roughly a quarter opacity, against a
typical layer `FillOpacity` of 0.35 (alpha ≈ 89). A rect being dragged out therefore looks markedly fainter
than the same rect once committed, which could read as "not filled." If that is what the owner is seeing,
raise the ghost fill toward the layer's own `FillOpacity` — keeping the dashed outline, which is what marks
it provisional.

## 2. Only if §1 does not explain it

Find what actually suppresses the fill and fix it directly. **Do not add a drag-time special case to the
renderer** unless the investigation proves one is needed — a second rendering mode for drags is exactly the
kind of divergence that produces "looks different while you're touching it" bugs.

If a threshold genuinely is wanted, express it as **per-shape fills during a drag below a visible-shape
count**, reusing the existing merge threshold rather than introducing a second knob.

## 3. The performance question, answered

**It does not matter, and there is measured data saying so.**

L2a's single-layer sweep found merged fills only **1.02–1.21× cheaper** than per-shape fills — and that was
across the *entire* layer at up to 100,000 shapes per layer. During a drag, the shapes whose rendering
changes are the handful under the cursor. The cost of filling them individually is unmeasurable.

L2a also found the real lever was draw-call count across many layers, not per-layer fill cost, which is
another way of saying: **filling a few dragged shapes is not where any of the time goes.** The owner's
instinct is correct and the benchmark agrees with it.

## 4. Guardrails

- No new render mode, no new option, no second code path for drags unless §2 proves it necessary.
- Do not change `MergeShapeCountThreshold`, `LodPixelThreshold`, or the path cache.
- Do not weaken R-L2c-1's LOD aggregation — a genuinely sub-pixel shape still aggregates, dragged or not.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 5. Gate

Gate command is plain `dotnet test`.

1. Builds green; `dotnet test` green; no existing test regresses.
2. **Filled during a drag** — off-screen pixel test: with a shape mid-drag (a populated
   `Overlay.DragOverrides`), a point inside it is the layer's fill colour at the layer's `FillOpacity`, not
   background.
3. **Overlap during a drag** — drag a shape so it overlaps another **on the same layer** with the opposite
   vertex order; the overlap region stays filled. This is the R-fix-1 regression expressed as a drag, and it
   is the test most likely to have been failing.
4. **Same at both tiers** — the assertion holds with the layer below the merge threshold and above it.
5. **Drawing ghost** — a rect being dragged out is visibly filled, and if the alpha was raised, still
   distinguishable from a committed shape by its dashed outline.
6. **No performance regression** — re-run the relevant benchmark and report; the expectation is no
   measurable change.

## 6. On completion

Record in `src/Ui/CLAUDE.md` **what the cause actually was**. If it was R-fix-1's winding bug reaching the
merge tier, say so explicitly and note that the same defect produced two separate owner reports through two
different paths — that is worth knowing next time a rendering oddity appears.

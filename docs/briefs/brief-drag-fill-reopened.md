# Sonnet Brief — Drag ghost fill, reopened: the gate passed but the user cannot see it

Owner retested after `brief-drag-fill.md` was marked COMPLETE: **the drawing ghost is still not visibly
filled.** His discriminator is the useful part — *"instances are dragging with fill correctly, but the
primitives are not."*

**Do not revert the previous work.** Its conclusion about candidate 1 is correct and stays.

---

## 1. What the last pass got right, and where it stopped short

`brief-drag-fill.md` listed two candidates. The previous pass resolved the first and closed the second on
reasoning alone.

**Candidate 1 — correct, keep it.** R-fix-1's winding normalization does fix the overlap-cancellation case,
and the reasoning is sound: `DrawLayer` substitutes `Overlay.DragOverrides` *before* geometry construction,
so both the individual and merge tiers reach the same `BuildShapePath` where the normalization lives. A
dragged shape was never on a separate code path. That finding is solid and its tests should stay.

**Candidate 2 — the ghost's `WithAlpha(60)` — was dismissed, and it is what the owner is seeing.** The
previous note says it "was NOT the explanation and was left untouched," reasoning that the alpha predates the
performance work so the timing did not implicate it. That reasoning is about *when the code changed*, not
about *what the user sees* — and the owner's instance-versus-primitive observation is new evidence that was
not available then.

## 2. Why the gate did not catch it

`DrawingGhost_IsVisiblyFilled_NotOutlineOnly` passes at alpha 60, because **it asks the wrong question.**
"Is there any fill at all?" is trivially true at 24% opacity. The user's eye is not asking that — it is
comparing the ghost against the committed shape beside it, and against the instance ghost that does read as
filled.

**R-dgf-1. A perceptual claim needs a comparative gate, not a threshold gate.** Replace the assertion with
one that compares the ghost's rendered fill against **the same shape committed on the same layer**, and
requires it to be within a stated fraction of it. A test whose bar sits far below the perceptual threshold
will pass indefinitely while the feature is visibly broken — which is exactly what happened.

This is the general lesson worth carrying: when a report is "I can't see it," the gate has to measure what
the eye measures.

## 3. The asymmetry to investigate first

Both ghosts use the same alpha, so alpha alone is not the whole story:

| | Fill |
|---|---|
| Primitive draw ghost — `DrawGhostShape`, `LayoutRenderer.cs` ~582 | **the layer's own colour** at `WithAlpha(60)` |
| Instance placement ghost — `DrawPendingInstancePlacement`, `LayoutRenderer.Instances.cs` ~404 | **`theme.Selection`** at `WithAlpha(60)` |
| Committed shape — `DrawLayer` ~658 | the layer's colour at the layer's `FillOpacity` (default 0.35 → alpha ≈ 89) |

**The likely explanation is colour, not opacity.** `theme.Selection` is a saturated accent chosen to stand
out against the canvas; a layer colour is whatever the user picked, and a muted or dark one at 24% opacity
over a dark canvas can be effectively invisible. Same alpha, opposite perceptual result — which is precisely
the asymmetry the owner reported.

**R-dgf-2. Determine this empirically before changing anything.** The previous pass reasoned from code to
"it's fine" and was wrong; more reading will not settle it. Render the drawing ghost and a committed shape of
the same layer to an off-screen bitmap, on both light and dark canvas backgrounds, for a muted layer colour
and a saturated one, and **report the measured contrast against the background** for each. Then the fix
follows from data rather than from a guess.

## 4. The fix, once §3 confirms the cause

**R-dgf-3. Raise the drawing ghost's fill toward the layer's own `FillOpacity`, keeping the dashed outline
as the provisional marker.** This was the previous brief's own explicitly conditional fallback — *"if that is
what the owner is seeing"* — and the owner has now said it is. The dashed stroke is what distinguishes a
ghost from committed geometry; the fill does not need to be faint to carry that meaning, and making it faint
is what cost the visibility.

Apply the same treatment to the **paste-fragment ghost**, which shares `DrawGhostShape`, so the two do not
diverge.

**Leave the instance placement ghost alone** unless §3 shows it is also short — it is the one the owner says
already reads correctly, and it is the reference the fix should match.

**If §3 shows the cause is colour rather than opacity**, say so and fix that instead — a ghost that derives
its fill from the layer colour may need a minimum contrast against the canvas rather than a fixed alpha.
Report which it was.

## 5. Guardrails

- **Do not revert R-fix-1 or its tests.** Candidate 1's finding stands (§1).
- Do not add a drag-time special case to `DrawLayer` — the move/handle/scale preview path is confirmed
  correct and covered.
- Do not change `DrawLayer`'s committed-shape fill, the layer `FillOpacity` semantics, or R8a's overlap
  darkening.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 6. Gate

Gate command is plain `dotnet test`.

1. Builds green; `dotnet test` green; no existing test regresses — including the four tests added last pass.
2. **Comparative gate (R-dgf-1)** — the drawing ghost's rendered fill is within a stated fraction of the
   same shape committed on the same layer. Replace, do not supplement,
   `DrawingGhost_IsVisiblyFilled_NotOutlineOnly` — leaving a threshold test beside a comparative one just
   preserves the thing that gave false confidence.
3. **Measured, not asserted (R-dgf-2)** — report ghost-versus-background contrast for a muted and a
   saturated layer colour, on light and dark canvases, before and after.
4. **Paste ghost matches** — the L1f paste-fragment preview gets the same treatment and is asserted the
   same way.
5. **Still distinguishable** — a ghost is still tellable from committed geometry by its dashed outline;
   assert the dash is present, so "make it visible" does not quietly become "make it identical."
6. **Instance ghost unchanged** unless §3 justified touching it, in which case say so.

## 7. On completion

Record in `src/Ui/CLAUDE.md`, and **correct the previous drag-fill entry rather than only appending** — it
currently states the ghost is visibly filled, which the owner has disproved, and a future reader will
otherwise trust it. Note: that **candidate 1's finding stands**; that **candidate 2 was closed too early on
timing reasoning rather than on what the user sees**; the measured cause from §3; and **R-dgf-1** — that a
"can't see it" report needs a comparative gate, because a threshold gate will pass forever while the feature
is broken.

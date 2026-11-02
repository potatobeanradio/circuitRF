# Sonnet Brief — L5 follow-ups, round two: MKlopf entry modes, smoothing blip, generated-cell lifecycle, MBend miter, pin rendering

Six owner reports. **§4 is the structural one** — the obvious implementation of the owner's deletion policy
would lose data, and §4.1 explains why.

Gate command is plain `dotnet test`.

---

## 1. Bug: the layout parameter editor lacks MKlopf's entry-mode controls

The schematic editor offers `Z1`/`Z2` ↔ `W1`/`W2` and `L` ↔ `f3dB`; the layout's PCell parameter editor
(added last round) does not.

**R-L5g-1. The layout parameter editor offers the same linked-field pairs as the schematic's.** Same rules:
`R-klp-3` links length and 3 dB cutoff, `R-klp-3a` links impedance and width, and in both cases **the
last-edited field is authoritative and is never written back from the other** — the Scale-dialog rule, for the
same reason.

Two things specific to the layout side:

- Values are **literals**, not expressions (a layout has no variable scope). The linkage is arithmetic only.
- The `Z ↔ W` conversion needs the **substrate**, so it resolves the technology the same way the generator
  does. If no technology resolves, disable the impedance fields with a reason rather than showing a wrong
  conversion.

## 2. Bug: end steps look large with `SmoothSteps = 0`, and a "blip" at port 1 with `SmoothSteps = 1`

### 2.1 The large end steps are probably correct — verify before changing anything

**R-L5g-2. A Klopfenstein taper is deliberately discontinuous at both ends** (R-klp-4). The impedance steps by
±ρ₀ at each end; the interior is smooth. So *"the first and last steps are large but the ones between are
smooth"* is a description of the design working, not failing — and it is precisely why R-klp-4 forbids
smoothing them in the **model**.

**Verify the step magnitude against the reference implementation** (the BSD-licensed oracle from
`brief-mtaper-mklopf.md` §2.1) rather than against expectation. If the magnitude matches, this is not a bug —
say so, and consider whether the user-facing documentation should mention it, since it will surprise everyone
who sees it.

### 2.2 The blip is a real bug — two candidates

**R-L5g-3. Check the smoothing blend first.** R-klp-4a blends the width from the connecting line's value into
the taper's first station over a length scaled to the **local width**, consumed **inside** the component's own
extent. A blip — a bump and back rather than a monotonic ramp — is what an overshooting or misaligned blend
produces: the blend target disagreeing with the first station's width, or the blend length exceeding the first
section so the two fight.

**R-L5g-4. Also check whether the curvature warning should be firing for his geometry, because it looks like
it should.** With `L = 200` and `Offset = 100`, `Offset/L = 0.5` — an aggressive S-bend. R-klp-10's estimate
gives `R_min ≈ L²/(5.8·Offset) ≈ 69` in the same units, while a 50 Ω trace's `3·W_local` is plausibly larger
than that. **If no warning appeared, that is a second bug** — either the check is not running or its threshold
is wrong. Report the computed `R_min` and `3·W_local` for these exact parameters.

Worth confirming while there: at high curvature, offsetting an outline by more than the local radius produces
a **self-intersecting** edge. It should not trigger at these numbers, but it is the failure mode that looks
like a blip, so confirm the margin rather than assuming it.

## 3. Bug: double-clicking a PCell still enters its hierarchy

`R-L5f-6` was supposed to fix this.

**R-L5g-5. Find why the previous fix did not take, and say what it was.** The likely cause is a **second
double-click path** — the canvas and the project tree, or push-in triggered by something other than the
handler that was changed. A fix applied to one of two entry points is the recurring shape of this defect in
this codebase (the context-menu and Scale-dialog cases both had it).

The behaviour required is unchanged: **double-click opens the parameter editor; push-in is disabled for PCells
with a stated reason.** A PCell's geometry is generated and read-only, so there is nothing inside to edit.

## 4. Generated-cell lifecycle — and why the obvious fix loses data

The owner's proposal: delete generated cells when a workspace is opened or closed, regenerate what a layout
needs when it opens, and never show the folder in the Project Tree.

**The policy is right. Implemented directly, it would destroy data.**

### 4.1 The flaw

`PCellOrigin` — including `Parameters` — is stored **inside the generated cell's own `.clay`**. Delete the
generated cells and you delete the only record of what a layout-first PCell's parameters were. On regeneration
there is nothing to regenerate *from*.

Schematic-linked instances survive, because `LayoutView.SchematicPCellSnapshots` keeps their parameters on the
layout. **Palette-dropped and layout-authored PCells have no snapshot and would be lost.**

### 4.2 The fix that makes the policy safe

**R-L5g-6. Extend the existing snapshot mechanism to cover *every* PCell instance, not only schematic-linked
ones.** Key it by instance identity rather than by `SchematicId`. Then the layout carries everything needed to
rebuild its own generated cells, and:

> **A generated cell becomes a pure cache: deletable at any time, rebuildable from the layout, never
> authoritative.**

That property is what the whole deletion policy rests on, so establish it **first** and assert it directly
(gate 6).

**This does not violate R-L5's "do not add parameters to `LayoutInstance`."** That rule protects the
instance-is-a-transform-of-a-cell model and the geometry cache. A per-layout regeneration record is a
different thing — and `SchematicPCellSnapshots` already is one; this generalises it.

### 4.3 The lifecycle rules

**R-L5g-7. Delete the generated-cell folder on workspace close, and again on open.** Close leaves a clean
workspace on disk; open guarantees a clean start even after a crash. Both are cheap once R-L5g-6 holds.

**R-L5g-8. Regenerate on demand when a layout opens** — or lazily on first render, whichever fits the existing
resolve path. A dangling `CellRef` must trigger regeneration, not a broken-reference placeholder.

**R-L5g-9. Generated cells are never shown in the Project Tree.** R-L5-3 already required this and it did not
land; treat the folder as infrastructure, not content.

**R-L5g-10. Exclude generated cells from anything that persists or ships a workspace** — `.cws` session
contents, save, any archive/share path, and **`.gitignore`** (the demo workspace is committed, so a stray
generated folder would end up in the repository).

## 5. Question: is MBend's miter working? Changing 0/1/2 does nothing, and "optimal" looks wrong

Both symptoms have specific likely causes, and they are different bugs.

**R-L5g-11. "Changing it does nothing" — check whether `Miter` resolves at all.**
`SchematicToLayoutGenerator.TryResolveSiValue` accepts only `ValueKind.Real` and `ValueKind.Bool`; anything
else yields `NaN` and is rejected. **If `Miter` is an enum or string parameter it cannot resolve**, so the
generator would fall back to a default for every value — exactly "changing it does nothing." Enum-valued PCell
parameters need a resolution path; verify and report whether this was the cause.

**R-L5g-12. "Optimal doesn't look optimal" — check for the missing √2.** R-bnd-1: Douville & James gives
**M as a percentage of the corner diagonal**, and for a right-angle bend `d = W·√2`, so the cut is
`(M/100)·W·√2`. Applying `M` to `W` directly makes the chamfer **41% too small**, which is precisely
"under-mitered compared to what I expected." At `W/h = 1` the optimum is ≈69%, so the cut should remove about
two-thirds of the corner.

Assert against a hand-computed cut length, and assert the three modes produce **three different outlines**
(R-pc-18 / R-bnd-10) — a test that passes with all three sharing an implementation has not tested this.

## 6. Question: should PCell pins be rendered? Yes — and here is what they need

**R-L5g-13. Render pins as a screen-space overlay, not as layer geometry.** Pins are metadata (contract R3),
so they must not participate in layer rendering, boolean operations, or the spatial index's geometry queries.

**R-L5g-14. They must never export.** Not to GDSII, DXF, Gerber or Excellon — the same rule R-L4c-5 applies to
port labels, for the same reason: they are markers, not artwork. This is the most likely thing to get wrong,
because rendering them as geometry is the easy implementation and it exports.

**Appearance** — the owner asked for something like schematic pins:

- A **dot** at the pin position, screen-space sized like the L1d handles so it stays legible at any zoom.
- Plus a **short tick showing the outward direction**, which R3 already supplies. That is what makes the
  overlay useful rather than decorative: abutment is about which way a pin *faces*, and a bare dot cannot say.
- A theme colour distinct from layer colours, so a pin never reads as copper.

**R-L5g-15. Make it a view toggle, default on.** On a dense layout every pin drawn at once is noise; the same
argument as any other overlay.

**Note for the geometry-snap work:** pins are a snap feature type there (diamond glyph), and this rendering is
what makes that discoverable. Keep the two visually distinguishable — the pin marker says *a pin is here*, the
snap glyph says *you are about to snap to it*.

## 7. Guardrails

- Do not smooth the end steps in the **model** (§2.1) — `SmoothSteps` affects artwork only.
- Do not add parameters to `LayoutInstance`; §4.2 extends the per-layout snapshot instead.
- Do not make generated cells authoritative for anything (§4.2).
- Do not render pins as layer geometry, and do not export them (§6).
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 8. Gate

1. Builds green; `dotnet test` green; no existing test regresses.
2. **Entry modes (R-L5g-1)** — the layout parameter editor offers both linked pairs; the last-edited field
   stays authoritative; with no technology the impedance fields are disabled with a reason.
3. **End steps (R-L5g-2)** — the ±ρ₀ step magnitudes match the reference implementation. Report the numbers.
4. **Blip (R-L5g-3/4)** — the width profile across the first and last sections is **monotonic**; report
   `R_min` and `3·W_local` for `Z1=50, Z2=100, Γmax=0.05, L=200, Offset=100`, and whether the curvature
   warning fires.
5. **Double-click (R-L5g-5)** — from the canvas **and** from the project tree, double-clicking a PCell opens
   the parameter editor and never pushes in; an ordinary cell still pushes in from both.
6. **Cache property (R-L5g-6)** — delete the entire generated-cell folder with a layout closed, reopen it:
   every PCell instance regenerates identically, **including palette-dropped and layout-authored ones with no
   `SchematicId`**. This is the gate the deletion policy depends on.
7. **Lifecycle (R-L5g-7/8)** — the folder is empty after a workspace closes and after it opens; a layout with
   PCells still renders correctly.
8. **Not visible, not shipped (R-L5g-9/10)** — the folder never appears in the Project Tree, is absent from
   `.cws`, and is git-ignored.
9. **Miter resolves (R-L5g-11)** — `Miter` values 0, 1 and 2 produce **three distinct outlines**; assert the
   resolved value reaching the generator, not just that the parameter was set.
10. **Miter geometry (R-L5g-12)** — the cut length equals `(M/100)·W·√2` against a hand-computed case; at
    `W/h = 1` the optimal cut removes ≈69% of the diagonal.
11. **Pins render (R-L5g-13/14/15)** — pins appear as screen-space dots with direction ticks at constant pixel
    size across zooms; the toggle hides them; **an export of a layout containing PCells has no pin artifacts**
    in GDSII, DXF or Gerber.

## 9. On completion

Record in `src/Ui/CLAUDE.md`: whether §2.1's end steps were correct (and if so, that they are *supposed* to
look like that); **R-L5g-6 — that generated cells are now a pure deletable cache and the layout carries the
regeneration record**, which is the fact every future generated-cell question turns on; why the double-click
fix did not take the first time; whether `Miter` was failing to resolve as an enum, and whether the √2 was
missing; and that **pins are an overlay that never exports**.

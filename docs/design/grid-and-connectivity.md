# circuitRF — Grid & Connectivity Rules

**Status:** Implemented (rev 2) — Layers 1–6 complete · **Date:** 2026-06-08 · **Phase:** 6

Defines how component pins, wires, and connection points stay **on a grid** so that connectivity is
unambiguous and the extracted netlist (6e) is correct. This is a **load-bearing correctness rule**, not a
cosmetic preference: a netlist is only correct if "these two pins touch" has one unambiguous answer, and that
requires every electrical connection point to land on exact, comparable coordinates. Companions:
`ui-design.md` §4.1 (grid), §4.3 (connections), §5/§5.1 (extraction), §5A (symbol editor), §5B (registry);
`project-file-formats.md` (`.csch`, `.csym`); `src/Ui/CLAUDE.md` (standing UI rules).

**Owner intent:** connecting components must be *easy* (hard-to-connect pins are intolerable), while authoring
keeps some freedom over where pins/bodies sit. The resolution below gives both with **two distinct grids**.

---

## 1. The two grids (the central idea)

There are **two grids with two different jobs**. They must not be conflated — conflating them is the source of
the conflict between "easy to connect" (wants coarse) and "freedom to place" (wants fine).

### 1.1 The Connection Grid `P` (coarse, sacred, design-level)
A single coarse pitch — **`P` = `GridSize`**, default **100 world units** (the classic 100-mil EDA pitch).
**Every electrically-connectable point lands on `P` exactly:**
- every component **pin** (its *world* coordinate, after placement + rotation + mirror),
- every **wire endpoint** and every **wire bend vertex**,
- every **junction dot**.

"On the grid" means the coordinate is an exact integer multiple of `P` — verified by arithmetic, **not by
tolerance**. Two pins connect **iff their connection-grid coordinates are equal**. Connection is *equality*,
not proximity. `P` is a property **of the design**, stored in the `.csch` (`GridSize`), and is **stable** (see
§4 on changing it).

### 1.2 The Authoring Grid `p` (fine, optional, cosmetic)
A finer pitch **`p = P / k`** (integer `k ≥ 1`; default **`k = 20` → `p = 5` world units**) used for
everything that is **not** an electrical connection point:
- symbol **body** geometry (the art),
- component **label** offsets, **net-label** positions,
- **canvas-object** (text/shape/image/plot) positions,
- the visual **snap** the user nudges non-connection things with, and the **displayed** grid lines.

`p` is **always a refinement of `P`** (`p = P/k`, never incommensurable). This is the invariant that keeps the
two grids compatible: a point on `P` is always also on `p`, and a fine `p` point never falls "between" `P`
cells in a way that could strand a connection. The user may change `p` (and the displayed grid) freely; they
may **not** casually change `P` (§4).

> **Why two grids resolve the conflict:** pins/wires/dots live on coarse `P` so connection is trivial and
> exact; bodies/labels/decorations live on fine `p` so authoring has freedom. Neither job interferes with the
> other because `p` is a strict refinement of `P`.

### 1.3 Choosing `P`, `k`, `p` — the freedom tradeoffs (read this before changing the defaults)

The three numbers are easy to confuse, and the intuitive move ("make the grid finer for more freedom") is the
wrong lever. The key facts:

- **`P` does NOT control authoring freedom — `p` does.** Freedom to place bodies/labels finely = how small `p`
  is = `p = P/k`. If you want more placement freedom, **increase `k`** (or shrink `p`); do **not** shrink `P`.
- **Shrinking `P` makes connection HARDER, not easier — the opposite of the goal.** A coarse `P` is what makes
  connection points few, far apart, and easy to hit with a wire. Halve `P` and connection targets are twice as
  dense: easier to snap to the wrong adjacent point, and "are these on the same node?" gets visually ambiguous
  at normal zoom. Since the entire reason for this design is "hard-to-connect pins are intolerable," a fine `P`
  pushes against the very thing it must protect.
- **`P = 100` is the universal EDA pitch (100 mils).** other EDA tools and RF engineers' muscle memory all
  assume it; any future geometry interop assumes it. A non-standard `P` (e.g. 50) makes every paste from a
  standard-100 library hit the cross-grid warn+snap path (§5) — i.e. it makes the foreign-grid *exception* the
  *common* case. Keep `P = 100`.
- **`P` is the migration-prone, design-level number; `p` is the free, cheap-to-change number.** Changing `P`
  strands connection points (§4) and is treated as a schema migration. Changing `p` only re-snaps decoration
  and is safe anytime. So put your tuning energy into `k`/`p`, never `P`.

**Worked example of the trap:** "`P=50, k=10, p=5` gives more freedom" — the `p=5` part is more freedom
(finer than `p=10`), but it was bought by halving `P` to 50, which doubles connection-point density and breaks
the 100-mil convention. The same `p=5` freedom is available with **`P=100, k=20, p=5`** — identical authoring
freedom, standard connection grid, no side effects. That is why the defaults are `P=100, k=20, p=5`: maximum
authoring freedom that's actually useful, on the sacred standard connection pitch.

**Lower bound on `p`:** don't go finer than ~`p=5`. The fine grid governs label/net-label nudging and symbol
body art — NOT pin placement (pins are always on `P`). Below `p≈5` the snap stops meaning anything (you're
effectively placing freely); if you ever want truly free decoration placement, allow free placement for canvas
objects rather than driving `p` toward zero. Conversely `p=10` (`k=10`) is a fine conservative choice if `p=5`
ever feels too loose — the difference is small at typical zoom because it only affects decoration, not pins.

**Summary table** (all keep `P=100` — only `k`/`p` vary):

| `P` | `k` | `p` | Authoring freedom | Connection grid | Notes |
|---|---|---|---|---|---|
| 100 | 10 | 10 | conservative | standard 100-mil | safe, slightly coarse decoration snap |
| **100** | **20** | **5** | **more (the default)** | **standard 100-mil** | **chosen: extra freedom, no `P` side effects** |
| 100 | 25 | 4 | finer | standard 100-mil | near-continuous decoration; fine |
| 50 | 10 | 5 | same as `k=20` above | **non-standard 50** | **avoid: denser/harder connection, breaks convention** |

---

## 2. The rules (normative — these are what the code enforces)

**R1 — One sacred connection grid.** All connection points (pins-in-world, wire endpoints, wire bends,
junction dots) are exact integer multiples of `P`. Connection = coordinate equality, not tolerance.

**R2 — Pin local offsets are connection-grid multiples.** In a symbol's local frame, **every pin's offset
from the symbol origin is an integer multiple of `P`.** (The symbol *body art* may use fine `p` detail — only
the **pin tips** must be on `P` multiples.) This is the rule the current library violates: pins sit at local
`±150` and `(150, ±100)` while `P = 100`, so even a perfectly-snapped origin puts pins half a cell off `P`.
**Fix the library:** leads `±150 → ±200`; FET drain/source `150 → 200` (keep `±100` — already a multiple);
`ZPort`/`Sdd` generated pins use `P`-multiple lead length. Body art (plate gaps, arc bumps, arrows) is
unconstrained.

**R3 — Placement snaps the component ORIGIN to `P`.** Because pins are at `origin + (multiple of P)` (R2),
snapping the origin to `P` guarantees every pin lands on `P`. So component placement/drag/nudge snaps the
**origin** to `P` — never to the fine grid, never free. Rotation (90/180/270) and mirror map `P`-multiples to
`P`-multiples, so rotated/mirrored pins stay on `P` automatically (a property that falls out for free; assert
it in a test, don't re-derive it per transform).

**R4 — Wires live on `P`.** Wire drawing, dragging, segment-drag, merge, and endpoint snapping all snap to
`P`. A wire endpoint released near a pin snaps to the pin's exact (on-`P`) coordinate; since both are on `P`,
the snap is exact and the connection is guaranteed.

**R5 — Fine grid `p` governs only non-electrical placement.** Symbol body geometry, label offsets, net-label
positions, and canvas objects snap to `p` (or are free). **Net labels are explicitly NOT required to be on any
grid** — a net label's *position* carries no electrical meaning; only the fact that a label *is on a wire*
affects the net (and the wire is on `P`). So net-label placement uses `p`/free and never participates in the
connection-grid invariant.

**R6 — The auto symbol generator places pins on `P` by construction.** When the generator (and the symbol
editor, §5A) lays out pins, pin coordinates are computed as integer multiples of `P` from the symbol origin —
**never** derived from arbitrary body-art extents. Body art may be on `p`; pins are on `P`. The symbol editor
**snaps placed pins to `P`** and may snap body primitives to `p`.

**R7 — The invariant is testable (the oracle).** After any edit, **every** pin world-coordinate, wire
endpoint, wire bend, and junction dot is an exact multiple of `P`. A headless invariant test asserts this over
a schematic after a battery of edits (place, move, rotate, mirror, wire, drag-segment, paste). This catches
regressions the way the other §5.1 invariants do, and it is the precondition 6e extraction relies on.

---

## 3. Connectivity becomes exact (why this matters for 6e)

Today `ConnectTolerance = 6.0` and the connectivity pass quantizes points to tolerance cells (`QuantKey`
rounds to `ConnectTolerance`). That tolerance was a **workaround** for points not being exactly equal. Under
R1–R4 **connection points are exactly equal**, so:
- The tolerance shrinks to "floating-point dust guard" only (sub-unit) — it stops being load-bearing for
  *correctness*. (Keep a tiny tolerance for float robustness; do not rely on it to bridge real gaps.)
- 6e extraction unions connection points by **exact on-`P` equality** (snap to `P`, compare integers), with
  the tiny tolerance as belt-and-suspenders, not the mechanism. This is the single-source-of-truth principle:
  "are these connected?" has **one** unambiguous answer (same `P` cell), not a fuzzy radius that can disagree
  with the visual.

This is the robustness the owner requires before 6e: the netlist's connectivity is decided by exact grid
coincidence, established at **input** (snapping at placement/draw), not patched up afterward by a tolerance.

---

## 4. Changing the connection grid `P` (the hard case)

`P` is **design-level and stable** — it is NOT a casual UI slider, because changing `P` can strand existing
connection points off the new grid (a pin that was on the old `P` may fall between cells of the new `P`,
silently breaking a connection). Therefore:

- **Display grid resolution** (which grid lines are drawn): free, cosmetic, change anytime.
- **Authoring snap `p`**: free; constrained to `p = P/k` (a refinement of `P`); change anytime. Affects only
  non-connection placement.
- **Connection pitch `P`**: **fixed for a design** in normal use. If changing it is ever exposed, it is a
  **deliberate, explicit operation** ("Change connection grid"), not a slider, and it must:
  1. only allow a new `P'` that is a clean **multiple or divisor** of the current `P` (so existing on-`P`
     points stay commensurable where possible), and
  2. **re-snap and validate** every connection point to `P'`, surfacing to Messages (§8) any point that
     cannot land cleanly (a stranded pin/wire), exactly like the cross-grid paste flow (§5). Treat it as a
     **schema migration**, not an edit.

For v1: `P` is fixed at the design's `GridSize` (from `.csch`); the user changes only `p` and the display.
Exposing a "Change `P`" operation is deferred (and when built, reuses the §5 re-snap+warn machinery).

---

## 5. Cross-grid paste / library insert (warn + snap)

**The problem:** the user copies from a `.csch` (or library cell) authored on a **different** connection grid
`P_src` and pastes into a design on `P_dst`. The pasted pins were on `P_src`; dropped as-is into `P_dst` they
may fall **between** `P_dst` cells — pins that look placed but silently won't connect (a netlist-corrupting
seam). The clipboard payload today carries **no grid metadata**, so this is currently undetected.

**The rule — detect, warn, snap:**
1. **Tag the payload with its source grid.** The clipboard payload (and a library cell) records the
   **`P_src`** it was authored on. (Clipboard: add `GridSize` to the copied payload — it is not there today.
   `.csch`/`.csym`: already carry `GridSize`/their grid.)
2. **On paste, compare `P_src` to `P_dst`.**
   - **Equal** → paste as today (already on-grid; only name-collision resolution runs).
   - **Different** → **warn** the user via Messages (§8): e.g. *"Pasted content was created on a 50-unit grid;
     this schematic uses 100 units. Pins were snapped to this schematic's grid — verify connections."* — a
     **warning**, not a silent fix and not a hard block.
3. **Snap the pasted content's connection points to `P_dst`.** Snap each pasted component **origin** to
   `P_dst` (which, with R2/R3, lands its pins on `P_dst`), and snap pasted wire endpoints/bends and dots to
   `P_dst`. Non-connection things (labels, net-labels, canvas objects) snap to `p_dst` or keep relative
   offsets. Do the snap as part of the paste command so it is **one undoable action**.
4. **Preserve internal coincidence where possible.** Snapping must preserve *relative* connectivity within the
   pasted group: if two pasted pins were coincident on `P_src`, they must remain coincident after snapping to
   `P_dst` (snap the group by a single consistent transform / snap each origin so intra-group coincidences are
   maintained). If `P_src` and `P_dst` are incommensurable enough that some intra-group connection cannot be
   preserved, **report the specific offenders to Messages** (don't silently drop them).
5. **Validate after snap.** Run the §2 R7 on-grid invariant over the pasted-and-snapped content; any point
   still off `P_dst` is a reported warning, never a silent off-grid pin.

The same warn+snap+validate machinery is what a future "Change connection grid" (§4) reuses — both are
"bring foreign-grid geometry onto this design's `P`."

---

## 6. Persistence

- **`.csch`** already stores `GridSize` (= `P`) and `GridSnap`. Add nothing for `P`. Add the **fine grid `p`**
  (e.g. `AuthorGridDivisor k` or `AuthorGridSize`) so a design round-trips its authoring grid; default
  **`k=20` (`p=5`)** when absent (within-version graceful load, alpha policy).
- **`.csym`** (symbol) records the grid its pins were authored on so a library cell knows its `P_src` for the
  cross-grid paste/insert check (§5).
- **Clipboard payload** must include `GridSize` (= `P_src`) so paste can do the §5 compare. This is the one
  net-new persisted field for this feature.
- Alpha policy unchanged: `format_version` written and reject-on-mismatch; `Id` never persisted (fresh on
  import); no migration.

---

## 7. Implementation order (smallest correct first)

1. ✅ **R2 — fix the library pin offsets to `P` multiples** (`±150→±200`, FET `150→200`, ZPort/Sdd lead). The
   immediate mechanical fix that makes existing components connectable. *(Body art untouched.)*
2. ✅ **R3/R4 — confirm placement snaps origin to `P` and all wire ops snap to `P`.** Audit confirmed all snap
   call sites use `EditModel.SnapToGrid` (which uses `GridSize`). `MirrorCommand` wire-reroute gap noted as
   separate task; does not affect pin on-grid correctness.
3. ✅ **R7 — the on-grid invariant test** (the oracle). `OnGridInvariantTests.AllEditOps_KeepConnectionPointsOnGrid`
   covers placement, drag, 4× rotate, 2× mirror, wire draw, segment drag, nudge, paste, undo/redo.
4. ✅ **R1/§3 — tighten connectivity to exact on-`P` equality**. `QuantKey` now uses `GridSize` (P=100);
   `ConnectTolerance` demoted to 0.5 (float-dust guard). All 528 existing tests pass unchanged.
5. ✅ **R5 + §6 — fine authoring grid `p = P/k`** (default `k=20`, `p=5`). `SchematicEditModel.AuthorGridDivisor`
   + `AuthorGridSize` + `SnapToAuthorGrid`; label offsets and canvas objects now snap to `p` not `P`;
   persisted as `AuthorGridDivisor` in `.csch` (absent → default 20, graceful load).
6. ✅ **§5 — cross-grid paste warn+snap+validate**. Clipboard JSON carries `GridSize` (= `P_src`);
   `SchematicPasteCommand` snaps connection points to `P_dst`, canvas objects to `p_dst`, posts warning via
   `IMessageSink`, validates R7 post-snap; one undoable action.
7. **R6 — symbol editor / auto-generator place pins on `P`** (lands with the symbol editor, Phase 6f; the rule
   is fixed here so 6f builds to it).

Steps 1–6 are complete. Step 7 binds the future symbol editor to the rule.

---

## 8. Open / deferred

- **"Change connection grid `P`" operation** (§4) — deferred; reuses §5 re-snap+warn machinery when built.
- **Symbol-editor pin/body snapping UI** (§5A/R6) — built with the symbol editor (Phase 6f); rule fixed here.
- **Incommensurable-grid paste edge cases** (§5.4) — v1 reports offenders to Messages; richer resolution
  (e.g. user-chosen snap origin) deferred.

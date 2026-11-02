# Sonnet Brief — wBond WB-C: the editor

**Design:** `docs/design/wbond.md`, approved 2026-08-07. This brief implements its **phase WB-C** —
the wBond Editor: the two views, the profile binding, selection and drag, the transforms, the live
inductance panel, and `.clay` embedding.

**WB-A and WB-B are complete and are the foundation.** `src/WBond` holds the physics, the array
reduction, the incremental drag path (measured **5.27 ms/frame at 600 wires**), `.wBond` I/O and the
CSV importer (134 tests). `src/Core` holds the component, the stamp and the coupling audit; `src/Ui`
holds the dynamic symbol (1,022 Engine + 5,307 Ui tests green). **This brief adds no physics and no
stamp.**

---

## 0. Read this before planning anything

### 0.1 WB-C is too large for one tranche, and pretending otherwise is the main risk

The design note's §6 asks for: two synchronised views, profile binding with detach semantics,
six selection modes, alt-drag scaling in two axes, seven transforms, a units selector, four snap
kinds, hierarchy descent, clipboard in and out, envelope rendering, a live panel, `.clay` embedding
and PDK-PCell flattening. Each is small; together they are larger than WB-A and WB-B combined.

**So this brief defines four sub-phases with their own gates**, and each is a legitimate stopping
point:

| | scope | project |
|---|---|---|
| **WB-C1** | The **framework-free editing core**: selection resolution, alt-drag scaling, every transform, duplicate-with-pitch, the profile envelope, nudge | `src/WBond` |
| **WB-C2** | The **document and the panel**: `WBondDocument`, the view-model, the live pH readout, current-share ramp, coupling/return-path status | `src/Ui/WBond` |
| **WB-C3** | The **two canvases**: layout-view overlay on the existing Layout Editor, profile view, pointer routing, snapping, hierarchy descent | `src/Ui/WBond` |
| **WB-C4** | **Interchange**: `.clay` embedding, PDK-PCell flattening, clipboard in/out, units selector, project-tree drag-drop | `src/Ui/WBond` |

**Do WB-C1 first and completely.** It is where every rule that can be wrong lives, and it is fully
headless-testable.

### 0.2 The architectural decision that shapes everything: most of WB-C is framework-free

**This is the finding to act on rather than a preference.** Walk the design note's §6 and ask what
actually needs Avalonia:

| needs a UI framework | does **not** |
|---|---|
| the canvas and its render pass | which wires a click selects (`w`/`g`/double/triple promotion) |
| pointer and keyboard event routing | what a right-to-left marquee encloses |
| the panel's bindings and layout | alt-drag height/span scaling arithmetic |
| the docking/tear-off shell | rotate-about-endpoint, mirror, bend, straighten, extend |
| the units combo | duplicate-with-pitch |
| | the profile envelope (min/max band) |
| | nudge steps and their per-view axis mapping |
| | whether a wire is bound or free, and what detaches it |

The right-hand column is **all of the logic that can be wrong**, and none of it needs a pixel. Putting
it in `src/WBond` means it is tested the way WB-A's physics was — against arithmetic, headlessly, in
milliseconds — rather than through a canvas.

**WB-C1 is therefore a real deliverable and not a refactor tax.** It also keeps `src/WBond` a
dependency-free leaf, which WB-B established is what lets `src/Core` reference it.

### 0.3 Seven things that are true before you start

1. **The incremental drag path already exists and is measured.** `IncrementalFill.MoveWires(moved,
   motion)` re-flattens the moved wires, recomputes 2N−1 blocks, rank-2 updates the Cholesky factor
   and reduces — **5.27 ms/frame at 600 wires** (fill 2.34, factor 0.31, reduce 2.62). **Do not
   rebuild any of it.** WB-C1 produces the *selection* and the *new geometry*; the fill does the rest.

2. **`Reduce()` must be passed the maintained factor.** WB-B found this the hard way: reducing with a
   fresh factorisation costs 22.7 ms instead of 2.62 ms and turns a 5 ms frame into 25 ms — silently,
   because the answer is identical. `IncrementalFill.Reduce()` already does the right thing; anything
   new that reduces must too.

3. **The measured multi-wire crossover is between 5 and 10 wires.** 1, 2 and 5 simultaneously-moving
   wires fit a 16.7 ms frame; 10 do not (28.8 ms). That is where WB15's adaptive quality ladder has
   to engage, and the number is measured rather than assumed.

4. **`WireMesh` is a snapshot with an explicit refresh.** Mutating a `Wire` does **not** update the
   mesh; `IncrementalFill` calls `RefreshWire` itself. A point-count change needs a full
   `WireMesh.Build` and throws if you try to refresh through it — which is correct, and is why
   anything that adds or removes a point (straighten, resample) must go through a rebuild.

5. **`LoopProfile` already generates wires, with the feet pinned exactly.** `ApplyTo(wire, start,
   end)` writes the polyline with height measured **above the chord**, so the endpoints have
   normalised height 0 and provably cannot move under scaling. WB-C1's alt-drag builds on that
   property rather than re-deriving it.

6. **The Layout Editor is ~6,700 lines of view-model across 20 partial files**, with marquee
   selection, four snap kinds (`LayoutSnapFeatures`: corner/endpoint, intersection, midpoint,
   centroid), hierarchy descent (`LayoutCoordinateWalk`, `LayoutInstanceTransform`), units
   (`LayoutUnits`), palette drag and rotation already built and tested. **WB-C3 hosts on it; it does
   not reimplement it.** The design note's own answer to "is this a new editor" is *no* (WB22).

7. **`src/WBond` must stay a leaf.** No reference to Core, Engine, RfCore or Ui. WB-C1's types are
   plain geometry and arithmetic; if something seems to need a Core type, it belongs in WB-C2.

---

## 1. Decisions taken — do not relitigate these

- **D1 — `Wire.Points` is the truth; a `LoopProfile` is a generator** (WB2 / WB-A D1).
- **D2 — Alt-drag scales about the CHORD, not a baseline.** The feet have normalised height 0 so they
  cannot move — which matters because in chip-and-wire the two feet are usually at different z, and
  scaling about a flat baseline would drag one off its pad (WB24a).
- **D3 — Alt+horizontal scales span and holds loop height absolute.** A bonder running the same loop
  program over a longer span does not scale height linearly. Alt+Shift is true similarity (WB24b).
- **D4 — On a bound profile curve, span scales by FACTOR, not to a common value** (WB24c) — otherwise
  a deliberate fan-out from a common pad is silently flattened.
- **D5 — Dragging an individual wire's curve DETACHES it from its profile**, with an undoable
  "N wires detached" toast. No best-effort propagation heuristic exists (WB24).
- **D6 — Rotate-about-end-point pivots on the end FURTHER from the grab**, so the gesture needs no
  mode switch. Multi-selection rotates each wire about its own pinned end, with a modifier for a
  shared pivot (WB26a).
- **D7 — `Reverse Wire` is explicit; direction is never silently re-inferred** (WB26b).
- **D8 — Nudge is 1 mil / 5 mil regardless of display unit**, because it is a bonder-process quantity
  — both settable (WB25).
- **D9 — Inductance displays in pH, fixed, never auto-ranged** (WB27a).
- **D10 — Wires are an OVERLAY, not a `.clay` shape type** (WB23), so a wire drag must not invalidate
  the layout's path cache (WB17).

---

## 2. WB-C1 — the framework-free editing core

### R-wbc-1 — the selection model
A selection is a set of **wire indices** and, within them, point and segment indices. Resolution
rules (§6.3), all pure functions:

| gesture | result |
|---|---|
| click | the point or segment hit |
| **`w` held**, or double-click | the whole **wire** |
| **`g` held**, or triple-click | the whole **array** |
| marquee left → right | enclose: fully-contained items |
| marquee right → left | **crossing: the entire wire for any wire with a point in the box** |

**The right-to-left promotion is the one that is easy to get subtly wrong** — it promotes to whole
wires, not to the points that happen to be inside.

### R-wbc-2 — alt-drag scaling (D2, D3, D4)
Given a wire (or a profile and its bound members) and a cursor position:

- **height**: `h_i′ = s·h_i` where `h_i` is height above the chord and `s = h_target/h_max`. Endpoints
  have `h = 0` and must come out **bit-identical**.
- **span**: the foot on the dragged side moves along the chord direction; the other is pinned;
  interior points keep normalised span position and absolute height above the chord.
- **on a bound profile**: every member scales by the same **factor**.

### R-wbc-3 — transforms (§6.4)
`Rotate` (arbitrary centre) · **`RotateAboutEndPoint`** (D6) · `Mirror` (with the traversal-reversal
checkbox) · `Bend` · `Straighten` · `Extend/Shorten` · **`ReverseWire`** (D7) ·
**`DuplicateWithPitch`** (offset + multiplicity).

**WB26 is a performance requirement, not a UX one:** `DuplicateWithPitch` must produce N wires in
**one** undo step, one array assignment and one fill. 200 wires as 200 operations is 200 cold fills,
and that is the difference between usable and not.

### R-wbc-4 — nudge (D8)
1 mil / 5 mil, settable. **Up = +z in the profile view, +y in the layout view** — the axis mapping is
a parameter, not a branch inside the arithmetic.

### R-wbc-5 — the profile envelope (§6.2 idea 3)
For an array: the min/max band of its bound members' height-above-chord against normalised span, plus
the list of free (detached) wires to draw individually. O(members) and allocation-light — this runs
per frame.

### R-wbc-6 — binding and detachment (D1, D5)
`BindToProfile` (resample onto it), `DetachFromProfile` (leave points untouched), and the query
"which wires would this profile edit move".

### R-wbc-7 — the panel model (D9)
Per array: L_arr in **pH**, mutuals in pH, coupling coefficient *k*, wire count, total length, span,
R/ωL at the design frequency, and the per-wire current-share ramp. A plain data record — the
view-model is WB-C2's.

---

## 3. The oracle ladder (WB-C1)

| tier | what | pass |
|---|---|---|
| **0** | **Alt-drag height: both feet bit-identical after any scale factor**, including unequal foot z | exact |
| **1** | Alt-drag height: normalised shape preserved — every `h_i/h_max` unchanged | ≤ 1e-12 rel |
| **2** | Alt-drag span on a bound array scales each member by FACTOR — an array with deliberately different spans keeps their ratios (D4) | ≤ 1e-12 rel |
| **3** | `RotateAboutEndPoint` leaves the pinned end **exactly** fixed and preserves every segment length | exact / 1e-12 |
| **4** | `ReverseWire` negates exactly that wire's off-diagonal row and column of **L** and nothing else | exact |
| **5** | `Straighten` then re-apply profile returns the original points (point count preserved) | exact |
| **6** | Right-to-left marquee promotes to whole wires; left-to-right does not | exact |
| **7** | `DuplicateWithPitch(n)` yields n wires, one array, one fill; and the resulting pitch is what was asked | exact |
| **8** | Envelope: a member outside the band is reported as free; the band brackets every bound member | exact |
| **9** | Detach leaves points untouched; re-bind resamples onto the profile | exact |
| **10** | Cost: a 200-wire `DuplicateWithPitch`, and an alt-drag frame on a 200-member array at 600 wires | reported, measured alone |

**Tier 0 is the one that matters most** — it is the property the whole chord-relative formulation
exists to guarantee, and a scale-about-baseline implementation passes every other tier in this table.

---

## 4. What must NOT be built in WB-C1

- **Any UI.** No canvas, no view-model, no pointer handling, no Avalonia. Those are WB-C2/C3.
- **Any physics.** WB-A's oracles are green; this brief moves geometry.
- **A second inductance path.** Use `IncrementalFill`; pass it the maintained factor (§0.3 item 2).
- **A reference from `src/WBond` to anything.**
- **`.clay` parsing** — WB-C4, and it stays an opaque blob until then (R-wb-11).
- **The assembly DRC** — WB-D.

---

## 5. Milestones (WB-C1)

| M | What | Gate |
|---|---|---|
| **M1** | Selection model + marquee semantics | Tiers 6 |
| **M2** | Alt-drag height and span, bound and free | **Tiers 0, 1, 2** |
| **M3** | Transforms incl. rotate-about-endpoint, reverse, straighten | **Tiers 3, 4, 5** |
| **M4** | Duplicate-with-pitch | **Tier 7**, and its cost |
| **M5** | Profile envelope + binding/detachment | Tiers 8, 9 |
| **M6** | The panel data record + the drag-frame cost | Tier 10 |

**Fault line: after M2.** If tier 0 does not hold bit-exactly, stop — the chord-relative formulation
is the one thing WB-C's whole editing model rests on.

---

## 6. What to report back on

1. **Tier 0's result** — feet bit-identical, or not.
2. **The 200-wire duplicate-with-pitch cost**, and that it is one fill rather than N.
3. **An alt-drag frame on a large bound array** at 600 wires, against the 16.7 ms frame — this is the
   case §0.3 item 3 says lands past the crossover.
4. **Anything in `wbond.md` §6 that turned out to be wrong.** WB-A found four such things and WB-B one;
   treat a contradiction as a finding.

---

## 7. The follow-on briefs

| brief | phase | scope |
|---|---|---|
| `brief-wbond-wbc2-document-and-panel` | WB-C2 | `WBondDocument`, view-model, live pH panel, current-share ramp, return-path and coupling status, undo/redo |
| `brief-wbond-wbc3-canvases` | WB-C3 | Layout-view overlay on the existing Layout Editor, profile view, pointer routing, snapping, hierarchy descent, the WB15 quality ladder |
| `brief-wbond-wbc4-interchange` | WB-C4 | `.clay` embedding, PDK-PCell flattening, clipboard in/out, units selector, project-tree drag-drop |
| `brief-wbond-wbd-assembly-drc` | WB-D | `.wasm`, `DrcLayerExprParser` reuse, 3D predicates, loop-height-vs-span envelope |
| `brief-wbond-wbe-standalone` | WB-E | Third entry point (**`<StartupObject>` for all three configurations**; assembly name stays `CircuitRF.Ui`) |
| `brief-wbond-wbf-kernel-w` | WB-F | Fidelity selector routing to kernel W1/W2 |

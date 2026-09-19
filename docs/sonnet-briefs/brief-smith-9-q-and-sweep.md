# Brief 9 — the constant-Q arcs and the swept band

**Series:** [Smith Chart](brief-smith-0-overview.md) · **Tag:** `R-smith9-n` · **Phase:** P2
**Area:** `src/Design/Smith/`, `src/Ui/Smith/` · **Depends on:** 5 · **Blocks:** nothing
**Design note:** [`smith-chart.md`](../design/smith-chart.md) §4.4, §3.6

---

## 0. What this brief delivers

Two small, independent features that both answer "how narrowband is this really": a draggable pair of
constant-Q arcs, and an optional continuous locus through the per-frequency load points.

---

## 1. `R-smith9-1` — the constant-Q arcs are true circles, and there is a closed form

For normalized `z = r + jx`, constant Q means `|x| = Q·r`. Substituting `z = (1+Γ)/(1−Γ)` with `Γ = u+jv`:

```
r = (1 − u² − v²) / ((1−u)² + v²)          x = 2v / ((1−u)² + v²)

x = Q·r   ⇒   u² + v² + (2/Q)·v − 1 = 0   ⇒   u² + (v + 1/Q)² = 1 + 1/Q²
```

So each branch is a circle:

```
inductive branch (x > 0):   centre (0, −1/Q),  radius √(1 + 1/Q²)
capacitive branch (x < 0):  centre (0, +1/Q),  radius √(1 + 1/Q²)
```

**Do not sample the z plane and map across.** That is the mistake `vswr-locus-gamma-plane.md` records for
the VSWR locus: equal steps in the z parameter become very unequal steps in Γ, and the rendered arc reads
as jaggy at exactly the values people use. Emit `centre + radius·e^{jθ}` at uniform θ.

**Both pass exactly through Γ = ±1**, which is why they are drawn as the arc **inside the unit disc only**
— the renderer's existing disc clip does it, and no arc-endpoint arithmetic is needed. `Q → ∞` degenerates
to the unit circle and `Q → 0` to the real axis; both come out of the same formula and neither is a special
case.

The arithmetic belongs in `src/Design/Smith/` beside the rest of the closed forms, framework-free, so
brief 10's headless render can draw them too.

### `R-smith9-2` — the drag, and the shift quarter-step

The drag point maps to `z_d` and `Q = |x_d| / r_d`. Closed form; no search.

- **Dragging either branch moves both**, because they are one setting.
- **`r_d ≤ 0`** — a drag outside the passive region — has **no finite Q**. Pin at the last valid value and
  say so in the status strip. Do not return infinity and do not draw a circle from it.
- **Holding shift rounds to the nearest 0.25**, applied to the **computed Q before it is stored** — so a
  shift-drag lands on an exact quarter and a subsequent un-shifted drag starts from that exact quarter
  rather than from a rounded display of something else.

**Release the modifier latch on `LostFocus`.** The layout view's "marquee select stopped working" defect
was a held-key flag never cleared because the key-up went to whatever took focus; a stuck shift here means
every later Q drag silently snaps to quarters. It is recorded in memory and in `src/Ui/RESOLVED.md`.

### `R-smith9-3` — the arcs are chrome

Drawn **beneath** the trajectories, carrying no marker, and **excluded from autoscale** (brief 5
`R-smith5-4`). They ride brief 5's overlay seam — this brief adds a handle kind to it, not a second
overlay.

---

## 2. `R-smith9-4` — the swept band

Owner decision: **optional, off by default.** A start/stop/npts band drawn as a thin continuous locus
through the load points. It is what makes bandwidth visible on a tool whose premise is that bandwidth is
not the question, and it costs one `Evaluate` per point.

- `Zgen` across the band is interpolated from the table by brief 2's rule.
- **The band is CLAMPED to the table's span with a stated note, not refused** — the one caller allowed to
  clamp (brief 2 `R-smith2-5`). A band is a viewing choice, where a design frequency is a design input.
  The note appears in the status strip and names the span it was clamped to.
- Its three fields are `InlineEditText` (brief 4 `R-smith4-5`).

---

## 3. The gate

`tests/Ui.Tests/Smith/SmithConstantQTests.cs` — one test per claim:

1. **The drawn circle IS the constant-Q locus** (`R-smith9-1`): sample the emitted arc, map each point to
   z, and assert `|x|/r = Q` to machine precision, over `Q ∈ {0.5, 1, 3, 10, 100}`. This is the gate.
2. **Uniformity**: the ratio of longest-to-shortest segment around the arc is 1.000× at every Q — the
   property the VSWR locus's own note says the sample-and-map approach loses.
3. **The two branches are mirror images** about the real axis, and both pass through Γ = ±1.
4. **The drag inverts**: drag to a reachable Γ, read Q, re-emit, and the arc passes through the drag point.
5. **Shift rounds to 0.25 before storing**, and an un-shifted drag from a shift-set value starts from the
   exact quarter.
6. **`r_d ≤ 0` pins and reports**, and returns no infinity and no NaN.
7. **The band clamps and says so**, with the span in the sentence; and a band inside the span is not
   clamped and produces exactly `Points` samples.

---

## 4. What this brief must NOT do

- **No second overlay.** Add a handle kind to brief 5's seam.
- **No Q readout on markers**, no Q-based autoscale, no Q in the copied JSON payload beyond the `.csmith`
  field brief 1 already defined.
- **No sampling the z plane and mapping across.** `R-smith9-1`.

---

## 5. On completion

Findings to `src/Ui/RESOLVED.md` (and `src/Design/RESOLVED.md` for the closed form). **Never a
`CLAUDE.md`.**

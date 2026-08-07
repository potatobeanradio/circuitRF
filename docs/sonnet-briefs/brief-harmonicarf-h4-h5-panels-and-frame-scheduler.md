# Sonnet Brief — harmonicaRF H4–H5: the panels and the frame scheduler

**Design:** `docs/design/harmonicarf.md`, approved 2026-08-06 — **with one correction pending, see
§0.1.** That note is the specification; this brief implements its **phases H4 and H5**: the document
shell, the locked layout, the four panels, markers, intrinsic glyphs, the `Harmonica.*` colour roles
and both variants, and then the concurrency, cancellation and adaptive-quality layer that makes any
of it live. H6–H8 each get their own brief; §10 names them so nothing is lost.

**Why this is the cut, and it is a measurement rather than a preference.** §10 lists H4 and H5 as
separate phases. They cannot be, because of what H0–H3 measured:

| tier (§6.8) | content | measured cost |
|---|---|---|
| **A — always live** | one Pin drive-up: loadline, power sweep, glyphs, readouts | ~7 solves ≈ **9 ms** |
| **B — adaptive** | a 61-point contour grid | **0.80 s** solve + **68 ms** extract |

Tier A fits a 33 ms frame with room to spare. Tier B is **26× the whole frame budget**, so the
contour panels cannot be built synchronously at all — not "should not", cannot. And the scheduler's
degradation rules cannot be tuned without panels to measure them on. Building H4 first and bolting
H5 on afterwards means writing the panels against a synchronous solve and then rewriting them; the
design's own §6.7 already forbids the end state ("the UI thread never solves").

**No new physics is written in this brief.** The engine is done and gated. If you find yourself
computing an impedance, a figure of merit or a contour, stop — it already exists and §2 says where.

**Read, in this order, before planning anything:**

1. **`src/Harmonica/CLAUDE.md`** — the whole of it. It is the record of what H0–H3 measured, including
   three things that contradict the design note.
2. **`docs/design/harmonicarf.md` §6.7, §6.8, §7.1–§7.5, §7.9.** §6.8 is the scheduler you are
   building; §7.1–§7.5 are the four panels and the readout strip; **§7.9 is the theme, and §7.9.1's
   "built on the existing theming system, not beside it" is the load-bearing sentence.**
3. **`docs/design/color-themes.md`** — the three-layer scheme, in full. Retrofitting a theme onto
   hardcoded colours is the mistake that note was written to prevent, which is why the roles land in
   the same phase as the panels rather than after them.
4. **`src/Ui/DataDisplay/CLAUDE.md`** — display-layer conventions. It is long; read §-by-§ for the
   plot/marker/renderer contract rather than end to end.
5. Then the code, not summaries of it: `PlotControl` (`src/Ui/DataDisplay/Controls/PlotControl.cs`),
   `ContourRenderer.DrawIsoLines` / `DrawGridPoints` / `DrawOptimaMarkers`
   (`src/Ui/DataDisplay/Renderers/ContourRenderer.cs`), `TraceRenderer_MarkerRenderer`,
   `SchematicRenderTheme.FromTheme` (`src/Ui/Renderers/SchematicRenderTheme.cs`) — the Layer-2
   pattern you are copying — `ColorRole` (`src/Ui/Theming/ColorRole.cs`), `ColorTheme`,
   `DataDisplayDocument` + `DataDisplayDocumentViewModel`, `PlotContainerViewModel`, and on the
   engine side `HarmonicaContext.Solve`, `PinSearch.Run`, `ContourGrid.Build`/`Raster`/`Contours`,
   `HarmonicaDataSet.Build`.

---

## Gate command

```
dotnet test tests/Harmonica.Tests --no-build      # the scheduler lives here — framework-free
dotnet test tests/Ui.Tests        --no-build      # the panels and the theme
dotnet test tests/Firewall.Tests  --no-build
```

Run as separate commands — this SDK rejects more than one explicit project path per invocation
(`MSB1008`).

**`Engine.Tests` is not in this brief's gate, and that is deliberate.** Nothing here touches
`src/Engine`. If you find yourself editing it, you have taken a wrong turn — say so and stop rather
than proceeding. Run it once at the phase boundary to confirm you did not.

`Ui.Tests` is **5,075 tests in ~19 s**, so it is a cheap loop; use it constantly. `Harmonica.Tests`
is 49 routine tests in ~0.25 s plus **6 opt-in `Category=Benchmark` methods (~8 s)** reached with
`--settings circuitrf.benchmark.runsettings`.

**The measurement discipline, restated because this repo has now been bitten by it three times.**
A benchmark sharing a run with others reads more than twice as slow. L9d's 71.9 s was first
mis-measured at 16.79 s that way, and in H0–H3 six concurrent timing methods **inverted** the
batched/unbatched comparison — 7.58 ms against 6.39 ms where alone they are 0.49 and 1.34. Every
timing class in `tests/Harmonica.Tests` is therefore in the non-parallel `HarmonicaBenchmarks`
collection **and** takes a best-of-N. **Put any new timing class in that collection, take every
reported measurement ALONE, and say in the report that you did.**

---

## 0. Read this before planning anything

### 0.1 The design note has a pending correction, and it is the owner's call

**`harmonicarf.md` §4.5.3(a) contains a sign error.** It gives

```
Z_seen = (Zs + Z_Ls) / (1 − gm·Z_Ls)
```

and under circuitRF's own passive sign convention it is **`(Zs + Z_Ls) / (1 + gm·Z_Ls)`**. `I[p]` is
the current into the device at the port's `+` terminal and out of its `−`, so port 2 = (drain,
source) delivers `Ids` into node s′ *from the device*, and KCL there reads `Ids = It + V_s/Z_Ls`.
Two independent checks agree with the `+` form: the degenerate case `Zs = 0, Z_Ls = R` gives
`R/(1 + gm·R) → 1/gm`, which is the source-follower output impedance and is what looking *out* of a
degenerated gate–source port must give (the note's form is **negative** for `gm·R > 1`, which a
passive degeneration cannot produce); and numerically the `+` form matches to **1.4e-16** across
three `Ls` values while the `−` form is out by a factor of two.

**The code, the tests and `src/Harmonica/CLAUDE.md` are already correct. The design note is not.**
Amending an approved design note is the owner's decision, not yours — so **M0 of this brief is to
put the correction in front of the owner and apply it if approved**, and to do nothing else to that
note. If the owner declines, say so in the report and leave both the note and the code as they are;
do not "reconcile" them by changing the code.

### 0.2 The budget — re-measured, and §2's table does not reproduce

`harmonicarf.md` §2 records **0.94 ms per warm HB solve at K=5** and every timing claim in §6.8
descends from it. Re-taken on the same machine during H0–H3, alone, Release:

| case | §2 says | measured |
|---|---|---|
| warm-seeded K=5, **seeded from its own answer** (1 Newton iteration) | 0.94 ms | **0.73 ms** |
| warm-seeded K=5, **seeded from a 1 dB Pin step** (3 Newton iterations) | — | **1.33–1.74 ms** |
| 500 warm solves, marker moved every time | ~0.45 s | **0.66 s** |
| 61-point contour grid, solve only | ~0.45 s | **0.80 s** (280 solves, 4.6/Γ point) |
| 61-point grid, RBF fit, two metrics | — | **2.87 ms** |
| 61-point grid, iso-line extract at 256×256 | — | **67.9 ms** |

**§2's 0.94 ms corresponds to about one Newton iteration, which is what you get when the seed is
already the answer. A real Pin step, or a marker move, lands two or three iterations away and costs
roughly double.** Every budget in this brief is written against the re-measured column, and **§6.8's
own numbers should be treated as optimistic by ~1.8× until re-taken.**

The one that matters most: **tier A is ~9 ms and tier B is ~870 ms.** That ratio is the whole design
of the scheduler.

### 0.3 Eight things that are true before you start

1. **The engine is DONE and every number the panels need already exists.** `HarmonicaContext.Solve`
   returns an `OperatingPoint`; `PinSearch.Run` returns the whole power sweep *and* the compression
   point; `IntrinsicPlane.Loadline` returns the time-domain loadline; `ContourGrid` returns points,
   holes, contours and the extrema; `HarmonicaDataSet.Build` publishes all of it as an ordinary
   `DataSet`. **Do not recompute any of it in a view-model.**
2. **A marker move costs no MNA solve and no re-elaboration.** Asserted, not assumed —
   `SchurReterminationTests.T2_4` moves a marker 50 times and checks the rebuild counter and the
   interface object identity. The drag path can rely on it.
3. **The contour cost is the RASTER, not the fit.** §6.4.1 lists five measures and names
   Delaunay/natural-neighbour as the fallback if the fit dominates. It does not: measured at
   n = 37/61/200 the fit is **0.029 / 0.078 / 0.960 ms** (and **0.008 / 0.043 / 0.044 ms** for a
   second metric off the cached factor) while the extract is **1.3 / 10.1 / 18.2 ms** at 96×96 and
   **7.7 / 58.3 / 112.9 ms** at 256×256. §6.4.1 item 1 is built and is essentially free; **item 2,
   the two raster resolutions, is the one worth 6–8× and it is NOT built.** Do not reach for the
   Delaunay fallback — it would optimise the cheap half.
4. **Holes are the common case, not an edge case.** A realistic 61-point grid on Hero 2's own device
   produced **7 holes**. §6.3's hollow dots and §6.4's support mask are on screen in every session,
   so they are not a corner to be handled late.
5. **`|Γ_intr|` legitimately exceeds 1** (§4.5 consequence 2) and must be rendered outside the chart
   boundary on a compressed radial scale — never clamped, never hidden. With conduction-only current
   this is ordinary, not an error.
6. **`ContourGrid` and `HarmonicaContext` are re-entrant-READY, not thread-safe.** No static mutable
   state, nothing shared — but `ContourGrid` caches its fits and its factorization in fields, and
   `HarmonicaContext` mutates models in place. **One context and one grid per worker**; sharing one
   across threads is a data race that will present as an intermittently wrong contour, which is the
   worst possible symptom.
7. **The existing display layer is Avalonia-bound and stays where it is.** `src/Harmonica` references
   no UI framework and is in the `tests/Firewall.Tests` assertion. The scheduler is framework-free
   and belongs in `src/Harmonica`; the panels belong in `src/Ui/Harmonica`. `ui-architecture.md`
   §4's "don't preclude it, don't build the ceremony now" applies — do not extract a `src/Display`
   project.
8. **A `.charm` already round-trips setup, and the theme has to join it.** `CharmIo` writes the DUT,
   the embedding, bias, settings, the marker set and the drive; §7.9.4 says the resolved
   `Harmonica.*` role map for **both variants** persists there too, together with the iso-line fade
   parameters and the label toggle. That is an additive change to an existing, tested format.

---

## 1. Decisions taken — do not relitigate these

Settled during the H0–H3 review and by what it measured. If implementation shows one is wrong,
**stop and report**; do not quietly substitute another.

- **D1 — The scheduler is framework-free and lives in `src/Harmonica`.** It is fed a clock, so it is
  fully testable headless. A scheduler that can only be exercised by moving a mouse is not testable.
- **D2 — One context per worker, pooled, rebuilt only on structural change.** Elaboration is ~ms;
  contexts are pooled. This is §6.7 and it is what item 6 above requires.
- **D3 — Latest-wins, always.** A newer frame supersedes an in-flight one rather than queueing behind
  it. Without this a fast drag builds an unbounded backlog and the UI lags *further the faster you
  move* — the classic failure mode for live-solve tools, and the one users notice first.
- **D4 — Tier A is never degraded.** If a model cannot hold 30 fps on one Pin drive-up, the status
  strip says so. Silently stuttering is the one behaviour that is worse than saying it.
- **D5 — Two raster resolutions, 96×96 during a drag and 256×256 on release.** §6.4.1 item 2, and
  item 3 above says why this one rather than the others. Degrading the raster is nearly free
  perceptually; degrading the grid loses information.
- **D6 — Fit and solve are timed SEPARATELY** (§6.4.1 item 6). A scheduler that lumps them cannot
  tell "the solver is slow" from "the fit is slow" and will degrade the wrong one. The measurement
  in item 3 above is exactly why: they differ by two orders of magnitude.
- **D7 — `Harmonica.*` roles go in the shared `ColorRole.All`** (§11 open item 10). One vocabulary,
  one editor, `.ccolor` interchange for free. If the Settings dialog proves cluttered the fix is
  role *grouping* in the editor, not a second role system.
- **D8 — Contour levels: auto with override, defaulting to 10 levels** (§11 open item 2).
  `ContourExtractor.LevelsBetween` already produces them.
- **D9 — `Gt` is the compression criterion's default** (§11 open item 1), matching `loadpull.md` and
  what `PinSearch` already uses. `Gp` is a selector.
- **D10 — The Tools menu is added HERE, not at H7.** §10 allocates it to H7, but a document nobody
  can open cannot be tested through the product path. H4 adds the menu with harmonicaRF as its only
  entry; H7 fills it out.
- **D11 — Iso-line labels default OFF** (§7.2). The default setting is also the fast one.

---

## 2. What already exists, and what genuinely does not

**Exists — use it, do not reimplement:**

| need | component |
|---|---|
| every solved quantity, as a `DataSet` | `HarmonicaDataSet.Build` |
| one operating point | `HarmonicaContext.Solve` |
| the whole power sweep + the compression point | `PinSearch.Run` → `PinSearchResult.Steps` / `.AtCompression` |
| the time-domain loadline | `IntrinsicPlane.Loadline` |
| intrinsic glyph values | `HarmonicaDataSet`'s `Z_intr` / `Gamma_intr` cubes |
| the source-side conversion matrix | the `Zs_conv` cube, axes `harmonic` × `harmonic_in` |
| Γ grid, holes, support mask, iso-lines, MXP/MXE | `ContourGrid` |
| Smith / Rect plot, axes, ticks, pan/zoom | `PlotControl`, `AxesRenderer`, `PlotRenderer` |
| iso-line drawing, grid points, optima markers | `ContourRenderer.DrawIsoLines` / `DrawGridPoints` / `DrawOptimaMarkers` |
| markers, info boxes, drag/hit-test | `Marker`, `TraceRenderer_MarkerRenderer` |
| the contour model + level machinery | `ContourData` (`MetricName`, `ConstraintKind.Compression`, `ConstraintValue`) |
| document shell, tear-off, dirty tracking | `DataDisplayDocument` / `DataDisplayDocumentViewModel` |
| the three-layer theming scheme | `ColorRole`, `ColorTheme`, `ColorVariant`, `ThemeResolver`, `ThemeService` |
| the Layer-2 projection pattern | `SchematicRenderTheme.FromTheme` |
| setup persistence | `CharmIo` |

**Does NOT exist — this brief builds it:**

- Any `src/Ui/Harmonica` at all.
- The `Harmonica.*` role block and `HarmonicaRenderTheme`.
- A frame scheduler, a solve pool, or any cancellation.
- A coarse/full raster switch on `ContourGrid`.
- The §7.2 alpha ramp.
- The Tools menu.
- Any persistence of colours in `.charm`.

---

## 3. M1 — the measurement that decides the frame budget

**Do this first and report before building on it.** Every number in §0.2 is a *solve* cost. A frame
is a solve **plus a render**, and nothing has ever measured harmonicaRF's render.

The risk is concrete: tier A's 9 ms leaves 24 ms of a 33 ms frame for four panels — two Smith charts
with contours and markers and glyphs, a DCIV family with a loadline over it, and a power sweep — plus
a dense readout strip. If the render is 30 ms, the whole tier-A-is-never-degraded decision (D4) is
built on sand and the owner needs to know before the scheduler is written around it.

**Measure, headless, through the real renderers** (`Ui.Tests` already renders to an offscreen
`SKSurface`; follow the existing pattern rather than inventing one):

1. ms to render **one Smith panel** with a 61-point grid, 10 iso-line levels, 7 hollow hole dots,
   4 markers and 4 glyphs, at 1× and at 2× device scale.
2. ms to render the **loadline panel** with a DCIV family and one loadline.
3. ms to render the **power-sweep panel** with gain and efficiency traces.
4. ms to render the **whole four-panel layout** at a realistic window size.
5. ms for `ContourGrid.Raster` at **96×96** and **256×256**, since D5's whole justification is that
   the difference is worth 6–8× and it should be confirmed on the real path.

**Then report those five numbers before continuing.** If (4) plus tier A's 9 ms exceeds ~25 ms, D4
becomes a claim the product cannot keep and the scheduler's tier structure needs the owner's
attention rather than yours.

> Text rendering is a known trap: `SkiaFonts.PlexRegular` is **unloadable headlessly** and the
> `TestOverrideTypeface` pattern exists for exactly this. See the layout-label brief's note in
> `src/Ui/CLAUDE.md` before you conclude that a measurement failed.

---

## 4. Requirements

### R-h45-1 — the locked default layout
§7.1 exactly: two Smith charts side by side (power left, efficiency right) with the dense
settings/readout strip spanning beneath both; the right column holds the loadline above the power
sweep, full height. **Locked by default**; Edit Display (H7) unlocks it. The layout is data, not
code — it persists in the `.charm` — so that H7 has something to unlock rather than something to
rewrite.

### R-h45-2 — the `Harmonica.*` roles and `HarmonicaRenderTheme`
A block of new roles appended to `ColorRole.All`, with Light and Dark variants, exactly as
`Schematic.*` and `Layout.*` do (D7). A `HarmonicaRenderTheme` token struct projects them into the
`SKColor`s the renderers draw with, built by a `FromTheme(theme, variant)` mirroring
`SchematicRenderTheme`. **No hardcoded static is the source of truth.** Defaults are §7.9.2 and
§7.9.3 verbatim — phosphor green primary, **red reserved for the loadline and the efficiency trace
and nothing else**. The variant follows `ActualThemeVariant`, as the schematic canvas already does.

### R-h45-3 — markers are properties of the CIRCUIT, not of a plot
§4.2. Moving `L2` on the power chart moves it on the efficiency chart **in the same frame**, because
both are views of one model object. A marker's band determines its colour from the five-colour cycle;
`S1`/`L1` are always present; `S2…`/`L2…` are added and removed from a menu; a band with no marker is
1e-6 Ω. The band colours are roles (a user *can* change them) but their defaults are the §4.2 table —
the cycle is a harmonic-identity convention and survives a theme switch untouched.

### R-h45-4 — intrinsic glyphs
Subtle triangular markers, always **beneath** the round termination markers in z-order, same
per-band colour at reduced saturation. Values come from the `Gamma_intr` cube; **do not recompute
them.** `|Γ_intr| > 1` renders outside the chart boundary on a compressed radial scale (§0.3 item 5),
never clamped and never hidden.

### R-h45-5 — holes and the support mask on screen
Thrown-out Γ points render as small hollow dots so the hole reads as *measured* rather than as a
rendering gap (§6.3). Nothing is drawn outside the support mask. `ContourGrid.Raster` already returns
NaN outside it and `ContourExtractor` already treats NaN cells as absent.

> **The fill is the one to check, and there is already evidence it will not.** `ContourData` carries
> a **second** surface — `FillGrid`, "resampled over [−1,1]×[−1,1] at higher resolution so the
> TopoMap fill reaches the Smith circular-clip edge" — and `DrawTopoMapFill`'s own comment says it
> exists to give "a clean circular edge rather than ragged NaN-cell gaps at the boundary". That is
> exactly the right behaviour for the *disk* boundary and exactly the wrong one for a *hole*: a fill
> built to paint across NaN gaps will paint across the support mask too, putting colour inside the
> hole the iso-lines correctly avoid. **Tier 8 is a pixel oracle over the rendered surface rather
> than over the polylines for this reason.** Whatever the fix, contours are unfilled by default
> (§7.2), so this can be a refusal to fill inside a hole rather than a rework of the fill.

`ContourRenderer.DrawGridPoints` takes a `ScatterReduction`, which harmonicaRF's grid is not. An
adapter or an overload is fine; a second grid-point renderer is not.

### R-h45-6 — the §7.2 alpha ramp
Iso-lines fade with **which level they are**, not with position, so the highest level is fully opaque
wherever it lands:

```
levels L₀ < L₁ < … < L_{n−1}
α_i = α_floor + (1 − α_floor) · ( i / (n−1) ) ^ p       α_{n−1} = 1 exactly
```

**Ranked, not value-proportional** — with even levels the two coincide, and with a long low tail the
value-proportional form crushes nearly every contour to invisibility. One flat alpha per polyline: no
shader, no per-vertex work, no geometry cache. Labels, when on, **inherit their line's alpha**.
`α_floor` and `p` are theme values, not constants.

### R-h45-7 — the four panels
§7.2–§7.5. The loadline panel's **plane toggle moves the DCIV family and the loadline together**
(one toggle, not two) with a **persistent** indicator of which plane is shown — never absent. The
power-sweep panel's X-axis unit is **click-to-cycle** on the axis itself. The readout strip is
deliberately dense — small fonts, no section titles, no decoration — every element carries a tooltip
and **all text is selectable**.

### R-h45-8 — the solve pool (§6.7)
`cores − 2` workers, each owning its **own** `HarmonicaContext` and `ContourGrid` (D2, §0.3 item 6).
Contexts are pooled and rebuilt only on structural change. **The UI thread never solves; it renders
the most recent completed result.**

### R-h45-9 — latest-wins cancellation (D3)
A newer frame supersedes an in-flight one. A job that has been superseded stops at the next
cancellation point rather than finishing. **Gated by Tier 4 of §5 — a synthetic 200-event drag must
produce a bounded number of completed solves, not 200.**

### R-h45-10 — the frame scheduler (§6.8)
Measures actual completion times and adapts grid density to hold the frame target. **Tier A never
degrades** (D4). Tier B degrades in a stated order: full user grid → coarse ring set (3 × 12 = 37) →
**freeze-and-snap**, contours ghosted during the drag and computed once on release. Tier C (the DCIV
family) is computed once and held: it depends only on the model, its parameters and the bias sweep
range — never on terminations. **Fed a clock, so it is deterministic and testable headless (D1).**

### R-h45-11 — a colour change must not invalidate physics
§7.9.4, and this is the requirement with the most invisible failure mode. Re-projecting
`HarmonicaRenderTheme` and invalidating the canvas is the *whole* cost of a colour change: **no
re-solve, and specifically no contour-cache or RBF-factorization invalidation.** Asserted directly —
the failure would show only as a frame-rate collapse nobody could attribute.

### R-h45-12 — colours persist in the `.charm`
Both variants' resolved role maps, plus `α_floor`, `p` and the label toggle. **Not** a separately
named `.ccolor`, because harmonicaRF runs with no workspace open and ships standalone, so a
name-plus-search-path scheme has nothing to resolve against. Roles absent from a stored map fall back
to the built-in default — the same nullable-defaulted rule `CharmIo` already follows.

### R-h45-13 — the Tools menu (D10)
circuitRF gains a **Tools** menu whose first entry is **harmonicaRF**. One item. H7 fills it out.

---

## 5. The oracle ladder

Each tier is a separate, independent check. **Where a tier names a closed form or an independently
computed value, that is the oracle — not another circuitRF path agreeing with itself.**

| tier | what | pass |
|---|---|---|
| **0** | the §7.2 alpha ramp against the formula, computed independently: α of the top level is **exactly 1.0**, α is monotone in RANK, and a deliberately uneven level set does not crush the ramp | exact on the top level; monotone |
| **1** | a marker moved on one chart moves on the other **in the same frame** — asserted on the model object, not by re-rendering | same object, one notification |
| **2** | **a colour change invalidates no cache**: `ContourGrid.FactorizationCount` and the fit cache are unchanged across a full theme swap, and no solve is scheduled | counters unchanged, zero jobs |
| **3** | theme round trip: a `.charm` save/reload restores every `Harmonica.*` role in **both** variants; a role omitted from the file resolves to its built-in default; *Reset all* restores exactly the §7.9.2/§7.9.3 tables | exact |
| **4** | **latest-wins**: a synthetic 200-event drag completes a bounded number of solves and the last result corresponds to the last event | ≤ a stated bound; last event wins |
| **5** | **the scheduler, on a synthetic clock**: the tiers degrade in the specified order and **tier A is never degraded**, at every frame time from comfortable to hopeless | deterministic, tier A untouched |
| **6** | tier C is computed **once** across a termination drag — the DCIV depends on no termination | one computation, N frames |
| **7** | `|Γ_intr| > 1` renders **outside** the boundary rather than clamped or hidden — pixel oracle | a lit pixel beyond r = 1 |
| **8** | a grid with a hole draws **no** contour and **no** fill inside the excluded disc — pixel oracle over the rendered surface, not over the polylines | no non-background pixel in the disc |
| **9** | cost: §3's five render measurements, plus frame time at each degradation tier | reported, measured alone |

**Tier 5 is the one that matters most.** It is the only check that tests the scheduler's *policy*
rather than its plumbing, and a policy that degrades the wrong thing is exactly the failure §6.4.1
item 6 and D6 exist to prevent. **Tier 8 is Tier 7 of the previous brief moved one layer out** — that
one proved no *polyline* enters a hole; this one proves no *pixel* does, which is a different claim
and the one a user sees.

---

## 6. What must NOT be built here

- **Any new physics.** No impedance, no figure of merit, no contour, no interpolation. §2 says where
  each already is. If something seems missing, it is a lookup you have not found — ask.
- **Anything in `src/Engine`, `src/Core` or `src/RfCore`.** The gate command deliberately omits
  `Engine.Tests`; if you need it, you have taken a wrong turn.
- **The inverse solve** (intrinsic glyph drag, §6.6) — H6. The glyphs are *drawn* here and are not
  draggable.
- **Edit Display, the trace picker, clipboard, `.gam` interchange, the colour EDITOR, testbench
  export** — H7. The roles and their persistence are here; the UI for editing them is not.
- **The standalone entry point** — H8. *(When it arrives it will need the `ColorPicker` Fluent
  `.xaml` StyleInclude — note `.xaml`, not `.axaml` — or the colour editor renders as an empty box
  with no error. Recorded here so it is not rediscovered.)*
- **A second contour, surface, FOM, marker, plot or theming implementation.** §2.
- **Extracting the display layer into `src/Display`.** §0.3 item 7.
- **Delaunay / natural-neighbour interpolation.** §0.3 item 3 measured the fit at 0.03–0.96 ms; it is
  not the cost and replacing it would optimise the cheap half.
- **Widening or "tidying" any validated limit or refusal.** Nothing here needs one.
- **Two-tone, a real bias network, baseband/video termination.** v2, and they arrive together.

---

## 7. Milestones, each with its own gate

| M | What | Gate |
|---|---|---|
| **M0** | §0.1's design-note correction, put to the owner and applied if approved | the owner's answer, recorded either way |
| **M1** | §3's five render measurements | **reported before anything is built on them; a legitimate stopping point** |
| **M2** | `Harmonica.*` roles + `HarmonicaRenderTheme`, both variants; `.charm` persistence | **Tiers 2, 3**; R-h45-2/11/12 |
| **M3** | The document shell, the Tools menu, the locked layout, the four panels drawn from a STATIC solved result | R-h45-1/7/13; Tier 7 |
| **M4** | Markers, glyphs, holes, the support mask, the alpha ramp — still static | **Tiers 0, 1, 8**; R-h45-3/4/5/6 |
| **M5** | The solve pool, latest-wins, the coarse/full raster switch | **Tier 4**; R-h45-8/9; D5 |
| **M6** | The frame scheduler and the adaptive tiers | **Tiers 5, 6, 9**; R-h45-10 |

**Three natural fault lines.**

- **After M1.** If a four-panel frame does not render in ~25 ms, D4 is not keepable and the tier
  structure is the owner's decision, not yours.
- **After M2.** If the theme cannot be projected without a hardcoded colour somewhere, stop. That is
  the exact failure `color-themes.md` exists to prevent, and it is far cheaper to fix before four
  panels are written against it than after.
- **After M4.** Everything through M4 is a *static* display of a solved result, and it is already
  worth shipping — it is the first time anyone can see what H0–H3 computes. If M5/M6 prove larger
  than they look, **stopping after M4 and reporting is a good outcome.**

---

## 8. File map (indicative)

```
docs/design/harmonicarf.md                    M0: §4.5.3(a)'s sign, if the owner approves

src/Ui/Theming/ColorRole.cs                   M2: the Harmonica.* block appended to All
src/Ui/Renderers/HarmonicaRenderTheme.cs      M2: Layer-2 projection, FromTheme(theme, variant)
src/Harmonica/CharmIo.cs                      M2: the role maps, α_floor, p, the label toggle

src/Ui/Harmonica/                             NEW folder inside the existing Ui project
  HarmonicaDocument.cs                        M3: the DataDisplayDocument pattern
  HarmonicaDocumentViewModel.cs               M3
  HarmonicaViewModel.cs                       M3/M4: the panels' shared circuit state, markers
  Views/HarmonicaView.axaml(.cs)              M3: the locked §7.1 layout
  Views/ReadoutStripView.axaml(.cs)           M3: §7.5, dense, selectable, tooltipped
  Renderers/SmithPanelRenderer.cs             M4: contours, markers, glyphs, grid points, holes
  Renderers/LoadlinePanelRenderer.cs          M3: DCIV family + loadline, plane indicator
  Renderers/PowerSweepPanelRenderer.cs        M3: gain + efficiency, click-to-cycle X unit
src/Ui/ViewModels/WorkspaceViewModel.*.cs     M3: the Tools menu (D10)

src/Harmonica/SolvePool.cs                    M5: per-worker contexts, latest-wins (framework-free)
src/Harmonica/FrameScheduler.cs               M6: synthetic clock, adaptive tiers (framework-free)
src/Harmonica/ContourGrid.cs                  M5: the coarse/full raster switch (D5)

tests/Harmonica.Tests/                        Tiers 4, 5, 6 + the raster measurement
tests/Ui.Tests/Harmonica/                     Tiers 0, 1, 2, 3, 7, 8 + §3's render measurements
```

---

## 9. What to report back on, whatever else happens

1. **The owner's answer on §0.1**, and whether the design note moved.
2. **M1's five render numbers** (§3), measured alone, and whether a four-panel frame plus tier A's
   9 ms fits 33 ms. If it does not, say so plainly — that is the finding, not an obstacle.
3. **Tier 5's degradation table**: at each synthetic frame time, which tier degraded and to what.
   **This is the deliverable of the H5 half.**
4. **Tier 4's bound**: how many solves a 200-event drag actually completed.
5. **The measured coarse-vs-full raster ratio** on the real path, against §0.3 item 3's 6–8×.
6. **Whether the fill path respects the support mask** (R-h45-5) — `FillGrid` and
   `DrawTopoMapFill` are built to paint across NaN gaps, so the expected answer is "no". Report what
   it actually does and what it cost to fix; the defect is invisible in the polylines, which is why
   Tier 8 is a pixel oracle.
7. **What you added to the `Category=Benchmark` tier**, in seconds, and confirmation that every new
   timing class joined the `HarmonicaBenchmarks` collection.
8. **Anything in `harmonicarf.md` that turned out to be wrong.** H0–H3 found three such things in
   §4.5.3, §6.2 and §2. The note was written against measurements and code reading, not against a
   running implementation — **treat a contradiction as a finding to report, not an obstacle to work
   around.**

---

## 10. The follow-on briefs (not this one)

| brief | phase | scope |
|---|---|---|
| `brief-harmonicarf-h6-inverse-solve` | H6 | Simultaneous all-harmonic inverse solve, Broyden updates, reachability shading, the operating-point cursor and its snap-to-compression |
| `brief-harmonicarf-h7-edit-display` | H7 | Edit Display, the trace picker over the published `DataSet`, clipboard, `.gam` interchange, the colour editor + `.ccolor` import/export + reset, testbench export, the rest of the Tools menu |
| `brief-harmonicarf-h8-standalone` | H8 | Standalone entry point + build configuration (**including the `ColorPicker` Fluent `.xaml` StyleInclude — it fails silently without it**) |

**Two open items H0–H3 handed forward rather than closed**, both for H6:

- **§11 item 8 — the source-glyph drag's conditioning.** The load-side inverse target is the §4.5.1
  ratio; the source-side one is the §4.5.3 conversion-matrix diagonal, which depends on `J` and
  therefore changes shape as the solution moves. H0–H3 built `Zs_conv` and gated it, but nothing has
  differentiated it. Expect the source side to want its own FD-refresh cadence.
- **§11 item 4 — reachability shading cost.** Unknown until H6. If it proves expensive it becomes
  opt-in rather than automatic.

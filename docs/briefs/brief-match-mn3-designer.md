# Sonnet Brief — Match MN-3: the Match Designer

**Design:** `docs/design/match.md` §9 (and §4.8, §6.6 for what the controls mean). **Depends on MN-1
and MN-2.** This brief builds the **Match Designer window** — the component's custom parameter dialog:
specification pane, live ladder preview, response plots, the linked Norton-transform slider rack, and
the solutions list. **It adds no new synthesis** — every number comes from `src/Core/Match/`, and if
you find yourself doing algebra in a view-model, stop: the formula belongs in MN-1's library.

**Where findings go: `src/Ui/RESOLVED.md`.** **Do not write in any `CLAUDE.md`.**

---

## Gate command

```
dotnet test tests/Ui.Tests       --no-build
dotnet test tests/Firewall.Tests --no-build
```

Separate commands (`MSB1008`). `Ui.Tests` is ~27 s for 5,075 tests — a fast loop. You should not need
`Core.Tests` or `Engine.Tests`: **this brief adds files under `src/Ui/Match/` and `src/Ui/Views/Match/`
and edits only the handful of existing UI files §7 names. If you are editing `src/Core` or
`src/Engine`, stop and report.**

---

## 0. Read this first

### 0.1 What this window is for

A user places a `Match`, double-clicks it, and designs a real matching network in one sitting: state a
band and two terminations, pick an order and a response, look at the resulting LC ladder and its
frequency response, then **slide the Norton transforms until the element values are ones they can
build** — watching the response *not* move while they do it. That last part is the whole reason the
window exists, and it is why the slider rack is not a detail.

### 0.2 The single biggest departure from the reference implementation

The reference application targets a phone: five tabs, a modal sheet for solutions, and a schematic
sharing ~360 pt with every input field. **Do not reproduce that constraint on a desktop.** The concepts
to keep verbatim are the ones carrying design intent — linked sliders with locks, the schematic ⇄ grid
toggle, the solutions list with its badges, the red flag on an unmatched termination. The *layout*
should spend the space it has: everything visible at once, no tabs, the solutions list a dockable
panel rather than a modal sheet.

### 0.3 Everything the user sets must survive a save/reload

π/T choice, every N, every lock, the link state, the applied solution, the Q-adjust. MN-1 §10 does the
work; this brief's job is to **write the design back to the `Design` parameter on every committed
edit** and never to hold state that only exists in the view-model. Test it (§8).

---

## 1. Hosting

- **Double-clicking a `Match` opens the Designer**, not the 420 px generic `ParameterEditorDialog`.
  Find where `ComponentDoubleTapped` routes to the parameter dialog and branch on
  `SymbolKind.Match`.
- **The Properties region** shows a compact `Match` panel for a selected instance: band, order,
  response, both terminations, worst in-band return loss, and an **Open Match Designer…** button.
  Build it as `src/Ui/ViewModels/ParameterEditorViewModel.Match.cs`, a partial class exactly like
  `ParameterEditorViewModel.WBond.cs` — read that file's header first; it is the pattern, including the
  `IsMatchPanelParameter` gate that keeps `Design` from rendering as a text row.
- The window is **non-modal and resizable**, default 1280 × 860, minimum 1000 × 700,
  `WindowStartupLocation="CenterOwner"`.
- **Undo goes to the owning schematic's stack**, through a command object, exactly as
  `ParameterEditorDialog` does (read its AXAML header). The Designer has no stack of its own.
- One window per instance; re-invoking on an instance that already has one raises the existing window.

---

## 2. Layout

```
┌──────────────────────────────────────────────────────────────────────────────────────────┐
│ Match — MN1                                        [Solutions ▸] [Settings] [Help] [Close]│
├───────────────────┬──────────────────────────────────────┬───────────────────────────────┤
│ SPECIFICATION     │ NETWORK            [schematic│grid]  │ RESPONSE                      │
│  Termination 1    │                                      │  ┌─────────────────────────┐  │
│  Termination 2    │   ladder preview, transform brackets │  │ |S11|, |S21| vs freq    │  │
│  Band / Order     │   drawn under the pairs they act on  │  └─────────────────────────┘  │
│  Response         │                                      │  ┌─────────────────────────┐  │
│  Q-adjust         │   …or the value grid                 │  │ phase / group delay     │  │
│  Allow negative   │                                      │  └─────────────────────────┘  │
│                   ├──────────────────────────────────────┤  plot band, points            │
│                   │ TRANSFORMS      [+ add ▾] [−] [🔗]   │  STATUS strip (§6)            │
│                   │  N1 (π│T) [2.9142] ├───●───┤ 🔓 (L2,L4)│                              │
├───────────────────┴──────────────────────────────────────┴───────────────────────────────┤
│ 3 solutions · applied: 2-transform, Fano   [Flatten to Cell…]  [Apply] [Revert]           │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

`[Flatten to Cell…]` is **disabled with a tooltip saying "MN-5"** until that brief lands. Wire the
button, not the behaviour.

---

## 3. Specification pane

Every numeric field is a circuitRF value+unit pair using the existing parameter-editor field
conventions, so unit parsing, validation and formatting come for free. Find the control the parameter
editor already uses for a `UnitDimension`-typed row and reuse it — do not write a new numeric box.

**Termination groups** each carry:

- topology: `Series` / `Parallel` (two-state selector);
- `R` with a resistance unit;
- reactance kind: `C` / `L` / `None` (three-state) and its value, with the unit switching between
  capacitance and inductance with the kind;
- a small **pictogram** showing the R and the reactive element in the chosen arrangement — an
  R with a C in series, an R with an L in parallel, and so on. This is the fastest way to show
  series-vs-parallel and it is worth the ~60 lines. `None` draws the resistor alone;
- a **Probe** button, **disabled with a tooltip saying "MN-4"** until that brief lands.

**Order** offers only the parities MN-1's `ValidOrders(term1, term2)` returns. When a topology change
makes the current order invalid, adjust it by ±1 **and say so in one line** — a control that silently
changes another control is worse than one that explains itself.

**Response** is the four-way selector of `match.md` §6.6. A response that cannot absorb both ends at
the current order is shown **disabled with the numeric reason in its tooltip** ("Bessel cannot absorb
termination 2 at order 4 — its far-end Q reaches only 0.33 against the 0.64 needed"), never silently
missing. MN-1's refusal already carries those numbers; render them, do not recompute them.

---

## 4. Network pane

Two presentations of one thing, toggled by a segmented control:

**Schematic.** The ladder drawn with circuitRF's own symbol geometry and renderer conventions
(`src/Ui/Renderers/SchematicRenderer.cs`, `src/Ui/Schematic/SymbolGeometry.cs`). Series arms along a
spine, shunt arms dropping to ground. Each element labelled with instance and value.

- **Negative or out-of-range values render red.**
- **Absorbed elements render dimmed**, in a distinct colour role, so it is obvious which two elements
  the user does not have to buy. Add a one-line legend; do not rely on the user inferring it.
- **Transform brackets** are drawn beneath the pairs they act on, labelled `N1`, `N2`…, and **stacked
  vertically when they would overlap** (the reference does this by testing intersection and pushing
  down; do the same).

**Grid.** Instance, type, value, unit — one row per element, sortable, copyable to the clipboard as
CSV. Same red/dimmed treatment.

**No nearest-standard-value column** (owner decision, `match.md` §14.2). What counts as realizable
depends on the flow — in an MMIC flow a capacitor is designed to its value and an E-series is
meaningless. Do not add one "for convenience".

---

## 5. The transform rack

One row per applied transform:

```
  label   π│T selector   numeric box   slider   lock   "on (L2, L4)"
```

- The slider's range is the transform's **recomputed** `[NMin, NMax]` from MN-1 §7.1 — never a stored
  bound, and never a hard-coded range.
- `+ add ▾` lists the currently available transformable pairs **by element name**; `−` removes the
  last.
- `🔗 link` is MN-1 §8.1. **With link on and exactly one transform, N is fully determined — disable the
  slider and the numeric box** rather than letting the user drag something that snaps back.
- Dragging one slider re-solves the other unlocked ones through `MatchLinkage`; locked rows are never
  written.
- Re-synthesis on drag must be smooth. Measure it (§8.3). The whole chain — rebuild the ladder, apply k
  transforms, run the response — is small, but the response is an S-parameter sweep at `PlotPoints`
  points and that is the part that could bite. If it does, throttle the *plot* on drag and update it on
  release; never throttle the ladder or the values, which must track the slider live.

---

## 6. Plots and status

Two `PlotControl`s in rectangular mode (`src/Ui/DataDisplay/Controls/PlotControl.cs`; it takes a
`Plot`, and a `Trace`'s data source is an `SNP`).

- Traces come from running **`SParameterEngine`** on an elaborated netlist of the **full design** —
  ladder plus both terminations, including the absorbed elements — with the two port references set to
  R1 and R2. This is the *design* response, which is what the user is judging.
- Per-port renormalisation goes through `RFNetwork`, and **only** because the design asks for it: leave
  the trace's own Z0-override off. (See `RESOLVED.md` on the Data Display Z0 override — an
  unconditional renormalisation there turned a real −20 dB match into −4 dB.)
- Plot 1: |S11| and |S21|. Plot 2: phase and group delay.
- Plot band defaults to the design band ±`PlotBandFraction` (10 %) and is user-settable, as is
  `PlotPoints`.

**Status strip** states, always: `Q1`, `Q2`, worst in-band return loss, insertion loss and ripple, and
achieved vs required `Π N²`. Any refusal from MN-1 §9 appears **here, with its numbers**, and the
affected termination turns red. "No solutions available for order 4" must be a sentence this window can
say plainly.

---

## 7. Solutions panel

A dockable list (slides out from `Solutions ▸`), **not** a modal sheet — the user must be able to click
through candidates and watch the ladder and response change.

Each row: a badge (current ✓ / previously applied / never applied), the transform count, the element
pairs each transform acts on, the Q-adjust value when non-zero, the response, and **Apply**. Order is
MN-1's: fewest transforms first, then by position, then by Q-adjust.

"Previously applied" comes from `MatchDesign.AppliedSolutions` (the fingerprints MN-1 §8 emits), so the
badges survive a reload.

---

### 7.1 Exports and settings

Buttons, not new formats — `match.md` §9.9. Touchstone `.s2p` of the design response through
`TouchstoneIO`; component listing `.csv` (the same rows as the grid); prototype g-values `.csv`. There
is **no bespoke PDF design summary**: MN-5's flatten writes a cell whose annotation carries the design
record, and the response goes to a Data Display tab like every other result.

`Settings` holds display units per dimension, significant digits, `Qmin` for Q-adjusted solutions, and
whether to offer Q-adjusted solutions at all. Nothing else — and in particular **no standard-value
series**, per §4.

### 7.2 One reference affordance deliberately not built

The reference implementation has a *guided* mode — a one-click "add the transform that reaches the
required ratio". **Do not build it.** The solutions panel already enumerates every valid transform set
and ranks the simplest first, which is the same answer with the reasoning visible. Adding a second,
opaque path to the same place is worse than having one. Record this in `RESOLVED.md` so nobody
re-derives it as a missing feature later.

## 8. Tests

`tests/Ui.Tests/Match/`. View-model tests, not pixel tests, except where a pixel is the only oracle.

| test | what it protects |
|---|---|
| **Session round-trip through the UI** — set two transforms (one π one T, one locked, link on), apply a Q-adjusted solution, close, reopen: identical values, N's, lock/link state, badges | §0.3, the "everything I set is still there" guarantee |
| **Link with one transform disables the slider** | §5 — the reference had this and it matters |
| **Link redistributes** — dragging one slider leaves `Π N²` on target and never writes a locked row | §5 |
| **Slider bounds are the recomputed ones** — add two transforms and confirm the second's range reflects the first's applied N | MN-1 §7.2 |
| **Response does not move on drag** — S11/S21 before and after a slider move agree to 1e-9 | the premise, now through the UI |
| **Order parity** — switching a termination's topology adjusts the order and emits the explanatory line | §3 |
| **Infeasible response is disabled with its numbers in the tooltip** | §3 |
| **Refusal surfaces in the status strip** and turns the right termination red | §6 |
| **Absorbed elements are visually distinct** in both presentations | §4 — assert the colour role, not pixels |
| **Undo** — a Designer edit undoes from the schematic's own stack | §1 |
| **Every committed edit writes `Design`** — no state lives only in the view-model | §0.3 |

### 8.3 Cost

**Measure the drag path** — one slider step, ladder rebuild through response update — and put the
number in your report. `Ui.Tests` has precedent for a cost test of exactly this shape
(`HarmonicaDragCostTests`); follow it, and tag it `[Trait("Category","Benchmark")]` only if it
genuinely exceeds ~5 s, which it should not.

---

## 9. What is NOT in this brief

`Probe` and `Flatten to Cell…` are **wired but disabled**, with tooltips naming MN-4 and MN-5. Do not
implement either, and do not stub them in a way that looks implemented.

---

## 10. Report

State: the measured drag cost; whether the plot needed throttling on drag and what you did; every
existing UI file you touched; anything in `match.md` §9 that turned out to be unbuildable as written,
with what you did instead. Findings to `src/Ui/RESOLVED.md`.

# circuitRF — Smith Chart: narrowband matching, by hand, with the chart doing the arguing

**Status:** **BUILT** — rev 1, closed out 2026-09-19 · **Date:** 2026-09-19 · **Phase:** P1…P3 shipped; see §9.1
**Implementation:** [`docs/sonnet-briefs/brief-smith-0-overview.md`](../sonnet-briefs/brief-smith-0-overview.md) — 11 briefs, all built 2026-09-19
**Reads with:** `docs/design/match.md` §9 (the Designer this one is deliberately *not*, and the window
conventions this one borrows), `docs/design/harmonicarf.md` §7.2 (the interactive Smith chart that
already exists, and what it proved), `docs/design/data-display.md` (the plot layer this reuses whole),
`docs/design/vswr-locus-gamma-plane.md` (the Γ-plane circle closed form), `docs/design/trace-markers-design.md`,
`docs/design/loadpull-contours.md` §labels (the label boxes the load points reuse),
`docs/design/project-file-formats.md` (where `.csmith` sits), `docs/design/ui-architecture.md` (the
firewall), `docs/design/cli.md` (the P3 verb).

**What this document decides.** The element set and what each one's *trajectory* means; what the chart
is normalized to; how a gripper drag inverts to a component value; how the network survives a round trip
through the system clipboard into a real `.csch` and back; and where every piece lives relative to the UI
firewall. **Four questions were put to the owner before it was written** and all four are answered in
§3.5, §3.4, §3.6 and §9. **Five further instructions arrived during drafting** and are folded in: a TLIN
carries a settable characteristic impedance as well as a length (§3.3); *Smith Chart* becomes an **On
Launch Action** in Settings (§5.9); the tool is a **circuitRF document, not an application** (§5.1) —
which is what makes the On Launch row legal; the network drawing can be **mirrored** so the generator
sits on the right (§5.5); and **every editable value in the window is an `InlineEditText`** (§5.3).

---

## 1. What it is, in one paragraph

The Smith Chart tool is a **scratchpad for narrowband impedance matching done by hand**. You state the
generator's impedance — one number, or a table over frequency, or an imported `.s1p` — and then cascade
two-pin elements outward from it, in series and in shunt, watching where the impedance seen at the far
end lands. The chart draws **one curve per element**: the path the impedance takes as that element grows
from nothing to its set value, so the network is not a list of numbers but a visible walk across the
chart. Every joint in that walk carries a **gripper**, and dragging one changes the element it belongs to
and moves everything downstream of it live. That last sentence is the whole tool: the question a matching
network actually poses is *"if I make this one a bit bigger, where does the load end up?"*, and this
answers it by letting you drag it and look.

---

## 2. Scope

### 2.1 In scope

- One **linear cascade** from a generator to an observation point called the *load*, built from two-pin
  elements placed in series or in shunt (§3.3).
- A **live** Smith chart: per-element trajectories, grippers, sliders, all redrawn during the drag.
- **Per-frequency load points**, labelled, for every row of the generator table; and an optional swept
  band through them (§3.6).
- **Markers** with VSWR circles, and **overlaid data** — Touchstone files and S-parameter cubes — so an
  S11, an S22 or a pair of stability circles can sit under the work.
- A **constant-Q pair of arcs**, draggable, with a shift-modified quarter-step.
- **Undo/redo** over every edit, including drags and the clipboard operations.
- **Clipboard both ways**: the network out as vector/bitmap/`.csch`; a compatible `.csch` selection in.
- **Mirroring the network drawing**, so the generator sits on the right and the cascade grows leftward.
- Persistence in a **`.csmith`** document that opens like any other circuitRF document.

### 2.2 Non-goals, and what to use instead

- **No synthesis.** Nothing here computes a network for you. That is the Match Designer's entire job
  (`match.md`), and the two tools are answers to different questions: the Designer is *broadband*, built
  on a Fano-optimum filter prototype with Norton transforms and a solutions list; this is *narrowband*,
  and the user is the algorithm. Adding "solve it for me" here would make the Designer's synthesis exist
  in two places, and the second copy would be worse.
- **No optimizer, no goal, no error function.** A network is judged by looking at it.
- **Nothing nonlinear, no power, no dBm.** The generator has an impedance and no available power.
  Everything in this tool is a linear immittance, and a "gain" readout would be inventing a quantity the
  model does not have.
- **No branching, no hierarchy, no sub-cells.** One cascade, ground on the shunt side. That constraint is
  not a simplification to be relaxed later — it is what makes the per-element trajectory *mean* something,
  because a walk across a chart has to be a walk.
- **No layout, no artwork, no physical length.** A TLIN is an ideal line with a characteristic impedance
  and an electrical length. `MLIN` and the microstrip family are a schematic's business.
- **A `.csmith` is not simulated by the engine.** It is evaluated in closed form (§4) — and gated against
  the engine (§4.6), which is a different and stronger statement than being run by it.

### 2.3 What already exists, and is reused rather than rebuilt

A large fraction of this tool is already in the repository. Cataloguing it up front, because the design
below is mostly an assembly.

| need | existing component | where |
|---|---|---|
| Smith grid, arcs, numbering, zoom-aware labels | `AxesRenderer.DrawSmithGrid` | `src/Render/DataDisplay/Renderers` |
| the chart control: pan, zoom, marker add/drag/hit-test, inspector, context menu | `PlotControl` + `Plot`/`Trace`/`Marker` | `src/Ui/DataDisplay` + `src/Render/DataDisplay/Models` |
| constant-VSWR circle about a marker, closed form, any VSWR | `LoadpullSurface.VswrLocus` (Γ plane) | `src/RfCore/Loadpull` |
| the VSWR-circle drag inverse | `HarmonicaVswrHandle.VswrThroughEx` | `src/Ui/Harmonica` |
| labelled boxes on a curve, world-unit spaced, padded, staggered | `ContourRenderer.DrawIsoLineLabel` / `ComputeLabelAnchors` | `src/Render/DataDisplay/Renderers` |
| stability circles, S↔Z↔Y, derived traces | `Trace.DerivedParameters`, `TraceResolve` | `src/Render/DataDisplay` |
| Touchstone read/write, renormalization, S↔Z↔Y | `TouchstoneIO`, `RFNetwork`, `SNP`, `RfHelpers` | `src/RfCore` |
| an S-parameter file fitted once and sampled many times | `SnpInterpolator`, `TouchstoneCache` | `src/RfCore` |
| drawing a ladder as a real schematic | `SchematicRenderer` | `src/Render/Renderers` |
| projecting a ladder onto editable schematic objects | `MatchSchematicModel` / `MatchSchematicCopy` | `src/Ui/Match` |
| copy a selection as JSON + SVG + PDF + PNG + `CF_ENHMETAFILE` | `SchematicClipboard.CopyAsync` | `src/Ui/Clipboard` |
| paste a schematic selection back | `SchematicClipboard.PasteAsync` | `src/Ui/Clipboard` |
| copy a plot as PDF + SVG + JSON + bitmap | `PlotExporter.CopyPlotToClipboardAsync` | `src/Ui/DataDisplay` |
| marker-guarded clipboard JSON, framework-free | `RailClipboard` (the shape to copy) | `src/Design/RailRf` |
| a document with a tab, dirty mark, Save/Save As, tear-off, undo routing | `DataDisplayDocument` | `src/Ui/DataDisplay` |
| `.csmith` reader/writer conventions | `RailDocumentIo` (the shape to copy) | `src/Design/RailRf` |
| value+unit entry, validation, formatting | `InlineEditText`, `MatchValueFormat` | `src/Ui/Controls`, `src/Ui/Match` |
| the window's chrome rules | `MatchDesignerWindow.axaml` | `src/Ui/Views/Match` |

**What is genuinely new** is short: the cascade evaluator and its per-element trajectories (§4.1–§4.2),
the gripper inverse (§4.3), the constant-Q arcs (§4.4), the topology recognizer for an incoming `.csch`
(§6.2), and the document plus its window.

---

## 3. The model

### 3.1 The generator

The generator is **an impedance and nothing else** — a table of rows, each a frequency and a complex
impedance:

```
f (Hz)        R (Ω)     X (Ω)
1.8e9         12.0      −8.5
2.0e9         11.4      −9.1
2.2e9         10.9      −9.8
```

- **At least one row.** A single row is the ordinary case ("50 Ω at 2 GHz"); a table is what makes the
  per-frequency load points (§3.6) interesting.
- **Rows are sorted by frequency and frequencies are unique.** A duplicate row is a refusal naming the
  frequency, not a silent last-wins.
- **Import `.s1p`** reads the file through `TouchstoneIO`, converts S11 to Z against the file's *own*
  stated reference impedance, and writes **one table row per file frequency**. A file with a
  per-port reference that is not the one in its `#` line is read the way `TouchstoneIO` reads it and no
  other way — this tool adds no second Touchstone interpretation.
- **Conjugate** is a one-shot, undoable edit of the table: every row's X is negated in place. It is *not*
  a persistent flag. A flag would mean the number in the table and the number the tool uses disagree,
  and there is no way to display that which does not eventually mislead someone.
- **The import copies values in; the path is provenance only.** The `.csmith` stores the numbers, not a
  reference to the `.s1p`, so a document is portable on its own and an archived or moved workspace cannot
  break it. The source path is recorded for display and for a **Re-import** button that repeats the read.
  This is the opposite choice from the overlays (§5.7), and deliberately: an overlay is reference material
  the user is comparing against, while the generator is part of the design.

### 3.2 The cascade

An ordered list of elements, index 0 nearest the generator. Each element is **Series** (in the through
path) or **Shunt** (from the through path to ground). There are no other placements, no branches and no
nesting.

```
     ┌─────┐
Zgen │     ├──[ e0 ]──┬──[ e2 ]──┬── ... ──● load
     └─────┘          │          │
                    [ e1 ]     [ e3 ]
                      ⏚          ⏚
```

The state carried along the walk is **the impedance looking back toward the generator**:

```
Z₀    = Z_gen(f)
series:   Z_{k+1} = Z_k + Z_e(f)
shunt:    Z_{k+1} = 1 / ( 1/Z_k + Y_e(f) )
2-port:   Z_{k+1} = Z₂₂ − Z₁₂·Z₂₁ / (Z₁₁ + Z_k)      (port 1 faces the generator)
```

and the observation point — **the load** — is the far end. **There is no load element.** Nothing
terminates the cascade; the load is where you *read*, which is why the tool can show the impedance
"that would be seen at the load" without the user having to assert what the load is. A user who wants
to see a specific load termination on the chart places a marker at it, or overlays its `.s1p`.

**Every element may be disabled** without being deleted (a checkbox on its row). A disabled element
contributes nothing, draws no trajectory, and keeps its values and its place. This costs one boolean and
it is the difference between trying something and losing it.

### 3.3 The element vocabulary

Every element maps **one to one onto a component circuitRF already has**, and that is the binding
constraint on this list rather than a convenience. It is what lets §6.1 paste the network into a real
schematic that really simulates, and it is what keeps this tool from growing a private component model
that agrees with nothing.

| element | placement | `SymbolKind` / engine | parameters (base SI) | sliders | default gripper parameter |
|---|---|---|---|---|---|
| R | series, shunt | `Resistor` / `R` | R | 1 | R |
| L | series, shunt | `Inductor` / `L` | L | 1 | L |
| C | series, shunt | `Capacitor` / `C` | C | 1 | C |
| SRLC | series, shunt | `Srlc` / `SRLC` | R, L, C | 3 | L |
| PRLC | series, shunt | `Prlc` / `PRLC` | R, L, C | 3 | C |
| Z1P | series, shunt | `ZPort` (`NumPorts=1`) / `ZPort` | Z (complex, constant over f) | 2 (Re, Im) | Im |
| S1P | series, shunt | `Snp` (`NumPorts=1`) / `SnP` | file reference | — | — |
| S2P | series only | `Snp` (`NumPorts=2`) / `SnP` | file reference | — | — |
| TLIN | series | `Tline` / `TLIN` | Z₀, E (at F_ref) | 2 | E |
| TLIN — open stub | shunt | `Tline` / `TLIN`, far end open | Z₀, E (at F_ref) | 2 | E |
| TLIN — shorted stub | shunt | `Tline` / `TLIN`, far end grounded | Z₀, E (at F_ref) | 2 | E |

Element impedances at angular frequency ω, all of them the textbook ones:

```
R      Z = R                        Y = 1/R
L      Z = jωL                      Y = 1/(jωL)
C      Z = 1/(jωC)                  Y = jωC
SRLC   Z = R + jωL + 1/(jωC)
PRLC   Y = 1/R + 1/(jωL) + jωC
Z1P    Z = Z                        (a complex constant; no frequency dependence — that is the point of it)
S1P    Z = Z_file·(1+S₁₁)/(1−S₁₁)    S₁₁ interpolated to ω, referenced to the file's own Z
S2P    the Z-parameter form above, S interpolated to ω then converted
TLIN   Z_{k+1} = Z₀·(Z_k + jZ₀·tanθ)/(Z₀ + jZ_k·tanθ)
open   Y = j·tanθ / Z₀
short  Y = 1 / (jZ₀·tanθ)
```

**A 2-port only ever goes in series.** A shunt one-port is what `S1P` and `Z1P` are for, and a 2-port
with its second port grounded is a different component than the one the user placed. An `S2P` whose file
turns out to have one port is a refusal naming the file and the element, not a silent promotion.

#### The TLIN's two parameters, and the frequency its length is quoted at

A TLIN carries **both** a characteristic impedance Z₀ and an electrical length E — owner, mid-review, and
it matters more than it looks: on a Smith chart the line's Z₀ sets *which circle* the rotation happens on
and E sets *how far around*, so a tool that fixed Z₀ at 50 Ω could not draw the most common move there is.
Both get a slider and either can be the gripper's parameter.

**E is quoted at the element's own reference frequency F_ref, and the length scales with frequency:**

```
θ(f) = (π/180) · E · f / F_ref
```

This is `TLIN`'s own semantics (`Z`, `E`, `F`) and it is not negotiable, because the whole reason the
load point is plotted at several frequencies is to see the network come apart at the band edges — and a
line whose electrical length did not change with frequency would be the one element in the cascade that
never did.

- **F_ref defaults to the design frequency at the moment the element is placed**, and is an editable field
  on the element's row. It does **not** follow the design frequency afterwards: a line that silently
  re-specified itself whenever the user retuned the chart would be a different physical line each time,
  and the load points would stop meaning anything. The status strip says so once, the first time a design
  frequency change leaves a TLIN's F_ref behind.
- A **quarter-wave-and-beyond stub** is legal and its trajectory is interesting; see §4.2.

### 3.4 What the chart is normalized to — owner decision

**A single, real, document-wide reference impedance Z₀_chart, default 50 Ω**, user-settable. Γ is the
ordinary voltage reflection coefficient against it:

```
Γ = (Z − Z₀_chart) / (Z + Z₀_chart)
```

Every overlay (§5.7) is renormalized to it on the way in, and the grid is the ordinary Smith grid, fixed.

**And the generator is drawn.** At each generator-table frequency the tool draws a faint,
un-selectable glyph at `Γ(Z_gen(f))` — where the table says the generator is — and the readout strip
states the mismatch in dB for the design frequency. Between them that is what a moving,
generator-referenced normalization would have bought, without the cost of it: a grid whose meaning
changes under the user's hands whenever the generator or the design frequency is edited, and an overlay
that has to be renormalized per frequency to stay comparable.

*Superseded, §9.3:* the glyph used to sit at `Γ(conj(Z_gen(f)))`, the conjugate-match target, on the
reasoning that landing a load point on it **is** the conjugate match. That is still true and the
mismatch number still reports it — but the target is the load point's MIRROR about the real axis, so
the one glyph the generator table could be checked against was the one place the table's own numbers
were not, and nothing in the picture said so.

The alternative was considered and rejected on exactly that ground; it is recorded in §12 as Q-2 in case
review disagrees, because the code difference is one function.

### 3.5 The trajectory rule — owner decision

Each enabled element draws **one curve**, from the impedance at its input to the impedance at its output.
The rule is **scale the element's immittance, not its component values**:

```
series :  Z(t) = Z_in + t·Z_e(ω)        t ∈ [0,1]
shunt  :  Y(t) = Y_in + t·Y_e(ω)        t ∈ [0,1]
TLIN   :  θ(t) = t·θ_total              (the line formula, at the line's own Z₀)
stub   :  θ(t) = t·θ_total              (the stub admittance, added to Y_in)
S1P/S2P:  no parameter — a dashed chord from Γ_in to Γ_out, and no gripper
```

For an L, a C or an R this is exactly the classical construction and produces exactly the classical
curves: a series reactance walks a **constant-resistance** circle, a shunt susceptance walks a
**constant-conductance** circle, a series resistance walks the real-part line, a shunt conductance the
same in the Y plane. Nothing about the familiar picture changes.

What it buys is that **there are no special cases for the multi-parameter elements**. An SRLC's
`Z_in + t(R + jX)` is a straight segment in the Z plane and therefore a circular arc in Γ — one curve, one
gripper, no discontinuity. The alternative, scaling the component *values*, is the reading the phrase
"from 0 to its value" most naturally suggests and it does not survive contact with a capacitor: as C→0 the
reactance −1/ωC runs to −∞, so an SRLC's trajectory would leave the chart at t→0 and come back, and a PRLC's
would do the dual. That is a true picture of a nonsensical question.

**Trajectories are sampled in t, never in the derived quantity.** Sampling a stub's susceptance uniformly
would put no points where the curve is moving fastest; sampling θ uniformly is correct everywhere,
including through the pole (§4.2). Sampling is adaptive on chord error in *canvas* space with a fixed
budget, so a curve is as smooth as the zoom deserves and no smoother.

**Direction is drawn.** Each trajectory carries a small arrowhead at its midpoint pointing from input to
output, because two adjacent arcs sharing a gripper are otherwise ambiguous about which way the walk goes.

### 3.6 Frequency: three different things, kept apart

> **Revised 2026-09-19 (owner instruction).** The design frequency is no longer a number of its own and
> the swept band is no longer a setting. Both are now read off the generator table, which is the only
> statement of "the frequencies this design is about" the document ever had. What that removed is
> recorded in `src/Ui/RESOLVED.md`; the original text of items 1 and 3 is below in its corrected form.

1. **The design frequency.** One number, and it is **derived**: the generator **row nearest the
   table's median** frequency. It is what the trajectories are drawn at, what the sliders' reactances
   are computed at, and what the readout strip reports. `Z_gen` is **linearly interpolated in R and X**
   between the two bracketing rows, and a row of the table is inside its own span by construction — so
   the old refusal about a design frequency outside it survives for exactly one caller,
   `circuitrf smith --at`, where it is still a refusal naming the span rather than an extrapolation.
   (A single-row table is that rule's exception: one row means one impedance, flat, and every
   frequency is legal against it.)

   > **Revised again 2026-09-19 (owner instruction): it is always a ROW.** It was the median itself,
   > which on an even-count table is the mean of the middle two — so a two-row {1.8, 2.2} GHz design
   > was drawn at 2.0 GHz, a frequency the table does not state, whose `Z_gen` is interpolated and
   > whose load point is not one of the labelled ones. Snapping to a row makes the emphasised load
   > point one of the drawn ones, which is what makes the picture readable. **An even table is always
   > an exact tie** — the median is the midpoint of the two middle rows, and no other row can be
   > nearer — and the tie goes to the **upper** of them. That collapses the whole rule to
   > `Rows[n / 2]` for both parities, which is how it is written: computing the mean and then
   > comparing distances would decide an exact tie on whichever way double rounding happened to fall.
   > `SmithDesign.MedianGeneratorFrequencyHz` carries the derivation.
2. **The table frequencies.** Every generator-table row produces a **load point** on the chart, with a
   small label box naming the frequency — the same `ContourRenderer.DrawIsoLineLabel` box the loadpull
   iso-lines use, placed by the same anchor walk, so the two surfaces cannot drift apart in appearance.
   The design frequency's point is drawn emphasised; the others are secondary. Beside each sits that
   frequency's GENERATOR glyph (§3.4). The label boxes are placed vertically, away from the cluster
   (§9.3).
3. **The swept band — always drawn, and it IS the table's span.** A thin continuous locus through the
   load points, from the table's first row to its last, walked at a fixed `SmithBand.Points`. This is
   what makes bandwidth visible on a tool whose premise is that bandwidth is not the question, and it
   costs one evaluation per point of arithmetic that is already measured in nanoseconds. `Z_gen` across
   it is interpolated from the table by the rule above, so there is nothing to clamp and nothing to
   refuse. A **single-row** table draws no band: one row is one impedance, flat, and the locus is the
   load point that is already there.

---

## 4. The arithmetic

All of it is closed form. There is no matrix, no solve, no iteration anywhere in the interactive path.

### 4.1 The cascade evaluator

`SmithCascade.Evaluate(design, f)` walks §3.2's recurrence and returns the impedance at **every** node —
N+1 of them for N elements — not just the last. The whole chart is a projection of that one array, and
computing it costs a handful of complex divides per element. A 12-element network over a 201-point sweep
is under 2,500 element evaluations; the budget for a drag frame is not in question and no caching,
warm-starting or scheduling is proposed. **If a future element makes this untrue, that element is the
thing to reconsider** — an interactive Smith chart that has to schedule frames is a different and much
worse tool, as harmonicaRF's `FrameScheduler` exists to prove.

Touchstone-backed elements (`S1P`, `S2P`) and the generator's own interpolation are the only places where
per-frequency data is fitted. The fit is built **once per file, at load**, through `SnpInterpolator` behind
`TouchstoneCache`, and reused for every sample — the S-parameter engine's own defect of re-fitting its
splines at every frequency point is recorded in `src/Engine/RESOLVED.md` and there is no reason to repeat
it in the one place where the cost would land inside a drag.

### 4.2 Per-element trajectories

Each enabled element emits a polyline in the Γ plane by sampling §3.5's parameter. Three details are worth
stating because each one has a wrong version that looks right:

- **A trajectory that passes through Γ = −1.** A stub longer than a quarter wave has `tan θ` run through a
  pole, so its susceptance sweeps to +∞ and returns from −∞. On the chart that is not a discontinuity at
  all: `Y_in + jB` for B over the whole real line is exactly the **closed constant-conductance circle**,
  traversed through the Γ = −1 point — the SHORT. (Rev 1 said Γ = 1 here, twice; that is the point every
  constant-*resistance* circle passes through. B → ±∞ is Y → ∞ is Z → 0, and Z = 0 is Γ = −1. Corrected
  against the built evaluator, brief 2.) Sampling in θ walks it correctly and continuously; sampling in B
  cannot. The polyline is emitted as one path, and the renderer's own clip to the unit disc handles the
  single vertex that lands on the boundary.
- **A negative-real-part impedance is drawn, not hidden.** An active `S2P` or a Z1P with negative R puts
  a node outside the unit circle. The chart's window is the Data Display's ordinary autoscale, which
  already enforces a unit-circle minimum on a Smith plot and grows past it when the data asks
  (`Plot.AutoscaleEnforceUnityMinimum`). Clamping to the disc would be a lie about a stability result.
- **A disabled element emits nothing**, and the gripper between its neighbours belongs to the next
  *enabled* element. There is no zero-length stub sitting invisibly in the chain.

### 4.3 The gripper inverse — drag to a value

A gripper sits at **every node** of the walk: N+1 of them for N elements.

- **Node 0 is the generator and is an anchor, not a gripper.** It is drawn (so the walk has a visible
  start) and it does not drag. The generator is edited in its own panel, where the frequency table lives;
  a drag would have to guess which row it meant.
- **Node k, for k ≥ 1, drags element k−1.** It changes exactly one parameter of that element — its
  **active parameter**, which is the one whose slider was last touched and defaults per §3.3's table.
  Nodes k+1 … N follow. **This is the feature**: dragging a mid-cascade element and watching the load
  point move is the single most-named behaviour in the tool's specification.

The inverse is closed form in every case, because the reachable set of a single free parameter is a known
locus and the answer is the projection of the drag point onto it. Writing `Z_d = Z₀_chart·(1+Γ_d)/(1−Γ_d)`
for the drag point and `Z_in`/`Y_in` for the element's input:

```
series R        R     = Re(Z_d) − Re(Z_in)
series L        L     = (Im(Z_d) − Im(Z_in)) / ω
series C        C     = −1 / (ω·(Im(Z_d) − Im(Z_in)))
shunt  R        1/R   = Re(Y_d) − Re(Y_in)
shunt  L        L     = −1 / (ω·(Im(Y_d) − Im(Y_in)))
shunt  C        C     = (Im(Y_d) − Im(Y_in)) / ω
SRLC on L       L     = (X_target + 1/(ωC)) / ω              X_target = Im(Z_d) − Im(Z_in)
SRLC on C       C     = 1 / (ω·(ωL − X_target))
SRLC on R       R     = Re(Z_d) − Re(Z_in)
PRLC            the duals of the three above, in Y
Z1P on Im       Im(Z) = Im(Z_d) − Im(Z_in)         (series; the Y dual for shunt)
TLIN on E       project Γ_d onto the rotation circle, read the angle, E = 180·θ/π · F_ref/f
TLIN on Z₀      one real unknown in the line equation — solved by the closed quadratic, see below
stub on E       θ from the required susceptance: θ = atan(Z₀·B_req) (open) or atan(−1/(Z₀·B_req)) (short),
                unwrapped into the branch the current E is in so a drag does not jump a half-turn
```

Only the component of the drag that the parameter can reach is used; the perpendicular component is
discarded. That is not an approximation — it is what "drag along the arc" means, and it is why the
gripper follows the curve rather than the cursor.

**The TLIN's Z₀ inverse** is the one that is not a one-liner. Requiring `Z_out = Z_d` in the line equation
and treating Z₀ as the unknown gives `j·tanθ·Z₀² + (Z_k − Z_d)·Z₀ − j·tanθ·Z_k·Z_d = 0`, a complex
quadratic in Z₀ whose two roots are computed directly; the tool takes the root with positive real part
nearest the current value, and when neither is physical it pins and says so (below). At `tanθ = 0` the
equation degenerates — a zero-length line transforms nothing — and the drag is inert, which is correct.

**Physicality is enforced at the pin, and reported.** L, C and R are non-negative; a TLIN's Z₀ is
positive; an electrical length is non-negative. A drag demanding otherwise **pins at the boundary and the
status strip names the parameter and the limit** — it does not silently produce a negative inductance,
and it does not stop tracking the cursor either, so the gripper stays under the user's hand at the pin.

**One drag is one undo entry.** Every pointer-move during a drag mutates the model, and the undo entry is
pushed on *release*, carrying the before-value captured on *press*. This is not a style preference: the
Match Designer shipped a defect where a two-way-bound slider's coercing write-back reached an unguarded
setter *during* `Undo`, so every undo added an entry and eight edits took fourteen undos to unwind. The
lesson is recorded in `src/Ui/Match/RESOLVED.md` and the rule that comes out of it is stated here as a
requirement: **a control's write-back is not an edit, and the model's value setter must be able to say
whether it is being driven by the user or by a restore.**

### 4.4 The constant-Q arcs

For normalized `z = r + jx`, constant Q means `|x| = Q·r`. Substituting `z = (1+Γ)/(1−Γ)` with `Γ = u+jv`:

```
r = (1 − u² − v²) / ((1−u)² + v²)          x = 2v / ((1−u)² + v²)

x = Q·r   ⇒   u² + v² + (2/Q)·v − 1 = 0   ⇒   u² + (v + 1/Q)² = 1 + 1/Q²
```

So each branch is **a true circle**, and the pair is:

```
inductive branch (x > 0):   centre (0, −1/Q),  radius √(1 + 1/Q²)
capacitive branch (x < 0):  centre (0, +1/Q),  radius √(1 + 1/Q²)
```

Both pass exactly through Γ = ±1, which is the reason they are drawn as the arc **inside the unit disc
only**. *(Correction, 2026-09-19: an earlier revision said the renderer's disc clip does this. It does
not — a Smith `Plot` clips its traces to the plot BOX and deliberately not to the disc, §5.4's own
rule. The in-disc range is closed form and is emitted: on the inductive circle `|Γ|² = 1 − (2/Q)·v`,
so the arc is inside the disc exactly where `v > 0`, which is `θ ∈ [atan(1/Q), π − atan(1/Q)]` — both
ends landing on Γ = ±1 with no intersection to solve. `src/Design/RESOLVED.md`.)* Q → ∞
degenerates to the unit circle itself and Q → 0 to the real axis; both are drawn correctly by the same
formula, and neither is a special case.

**Dragging one sets Q**, closed form: the drag point maps to `z_d` and `Q = |x_d| / r_d`. Dragging either
branch moves both, because they are one setting. `r_d ≤ 0` — a drag outside the passive region — has no
finite Q; the drag pins at the last valid value and the strip says so. **Holding shift rounds to the
nearest 0.25**, applied to the computed Q before it is stored, so a shift-drag lands on exact quarters
and a subsequent un-shifted drag starts from the exact quarter rather than from a rounded display of
something else.

The Q line is chrome, not data: it carries no marker, is drawn beneath the trajectories, and is excluded
from autoscale.

### 4.5 Markers, VSWR, and the readout

Markers are the Data Display's own `Marker` objects on the Smith `Plot`, which means placement, drag,
hit-test, the info box, the context menu, the editor and persistence all come for free — including
`VswrEnabled`/`VswrValue`, whose circle is `LoadpullSurface.VswrLocus` in the Γ plane (the closed form in
`vswr-locus-gamma-plane.md`) and whose drag inverse is `HarmonicaVswrHandle.VswrThroughEx`. The one
correction that file records is worth repeating here because this tool will invite the same mistake: a
constant-VSWR circle about a marker is **not** centred on that marker unless the marker is at Γ = 0.

The status strip reports, for the design frequency: the load impedance in R + jX, Γ in polar and
rectangular, VSWR, and the mismatch loss in dB — **all four against the chart's own Z₀**, so the strip
answers one question in four spellings rather than two questions in one row (Q-17, closed). Every
number is the one the evaluator produced, formatted by `MatchValueFormat`, never re-derived for
display. The generator glyphs carry no number (§3.4, §9.3): a load point landing on the MIRROR of its
frequency's glyph about the real axis is the conjugate match, which is what the mismatch column reports.

### 4.6 How this is gated — the engine is the oracle

The evaluator is closed form and the engine is not, and that is precisely what makes the gate worth
having. **For every element type and both placements, a test builds the equivalent `.cnl` — a `Port` with
the generator's impedance, the cascade as real component lines, a `Port` at the load — runs the ordinary
S-parameter analysis, converts S₁₁ back to an impedance, and compares against `SmithCascade.Evaluate`.**

This is the strongest acceptance anchor available and it is cheap, because §3.3's one-to-one mapping onto
existing components is what makes the equivalent netlist *writable*. Its real value is not catching an
arithmetic slip; it is catching a **convention** slip — a sign, a port order, a reference impedance, a
`tan` where a `cot` belongs — which is the class of error that produces a plausible picture. Tolerance:
1e-9 relative on Z, which is the numerical floor, not an engineering allowance.

The same test is what proves §6.1's claim that the copied schematic is the network the user was looking
at, since it runs the same netlist.

---

## 5. The window

### 5.1 It is a document, not an application — owner decision

harmonicaRF, wBond and railRF each open a **window** of their own, and each has a reason: harmonicaRF and
wBond ship as standalone binaries, and railRF's centre panel is the layout editor's canvas. **The Smith
Chart tool has neither reason**, and the owner's instruction is explicit: treat it as a circuitRF
document.

So `.csmith` opens as a **docked `Document`** in the workspace shell, exactly as a `.cdd` does, and it
inherits — rather than re-implements — the whole of what that means:

- a document tab, a `•` dirty mark, and participation in **Save**, **Save All** and close-time prompting,
  through `IFileBackedDocument`;
- **Ctrl/Cmd+Z routed by the shell** through `IEditHistoryDocument`, which is the interface that exists
  because a *floating* Data Display once undid an edit in an unfocused schematic — on macOS the menu bar
  is app-global and the shell's Undo command must be able to reach whichever document is key;
- **tear-off and floating**, the Window Layout, and restoration of open documents on reopen;
- appearing in the **project tree** when it lives in a workspace, with double-click to open, and the same
  single-instance rule every document type has (a second open focuses the first);
- **needing no workspace**: a scratch `.csmith` opens with nothing else loaded, on harmonicaRF's own
  terms, and Save As gives it a home.

There is no standalone `smithRF` binary and none is proposed. This is a document type.

> **Revised 2026-09-19 (owner instruction): where Tools ▸ Smith Chart OPENS it.** Everything above is
> unchanged — it is a dockable document, it can be dragged back into the tab strip, and nothing about
> it is an application. What changed is the size it opens at. This window is three regions (§5.2), and
> the room a docked tab gets inside a standard 1200×800 shell is not enough of any of them to work in;
> testing on the shipped example bore that out. So the command **measures the document region a docked
> tab would actually get** (`WorkspaceViewModel.DockedDocumentRegionFitsStandardWindow`) and docks only
> when that region is already **a full standard workspace window (1200×800) or larger** — which a
> maximized shell on a large display is, and a default one is not. Otherwise the document goes straight
> out into a window of its own, sized like the shell and offset down-right from it, through
> `OpenDocumentInOwnWindow` — **the same tear-off path harmonicaRF and a user's own drag take**, not a
> hand-built window. The yardstick is the SHIPPED 1200×800 rather than the shell's current size: a
> shell the user has dragged small would otherwise lower its own bar. Opening a `.csmith` from the tree
> is untouched and still docks, because the user asked for that file rather than for the tool.

### 5.2 Layout

```
┌──────────────────────────────────────────────────────────────────────────────────────────────┐
│ • lna_input_match.csmith                                                     (document tab)  │
├───────────────────┬──────────────────────────────────────────────────────────────────────────┤
│ GEN.       [~] [v]│ [ lna_s2p        v ]                        [S] [S+] [Q]                 │
│  f      R      X  │                    .----------------------.                              │
│ 1.80G  12.0  -8.5 │                 .--'                      '--.                           │
│ 2.00G  11.4  -9.1 │                /      ,2.20G                  \                          │
│ 2.20G  10.9  -9.8 │               |         +      .------.        |                         │
│          [+]  [-] │               |       ,2.00G --'      '-- ,     |                        │
│                   │                \        ,1.80G               /                           │
│ LOAD              │                 '--.                      .--'                           │
│  f      R      X  │                     '----------------------'                             │
│ 1.80G  48.2   3.1 │                                                                          │
│ 2.00G  50.1  -0.4 │   ,  load point, labelled   + Z_gen  (shift-drag it to edit              │
│ 2.20G  51.6  -4.0 │   -- element trajectory       that generator row)                        │
│                   │   o  gripper                                                             │
│ Chart Z0  [ 50 ] O│                                                                          │
│                   │   (overlays are added in Plot Properties... - 5.7 - and the              │
│                   │    combo at the top left is what a new trace is seeded from)             │
├───────────────────┴──────────────────────────────────────────────────────────────────────────┤
│ NETWORK                  [Fit] [Zoom box] | [ Add v ] [ Insert v ] [ Del ] [ <> ] [ M ]      │
│                                                                                              │
│         +---+        +----+         +----+                                                   │
│    G ---|   |----+---| L2 |-----+---| TL1|------* load                                       │
│         +---+   ===  +----+    ===  +----+                                                   │
│          L1      C1             C2                                                           │
│                  gnd            gnd                                                          │
│                                                                                              │
│  L2   L  [===========o=========]   3.90 nH        [x] enabled                                │
├──────────────────────────────────────────────────────────────────────────────────────────────┤
│ 2.000 GHz - load 49.1 + j1.8 O - G 0.019 /61deg - VSWR 1.04 - mismatch 0.00 dB               │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
```

The proportions are the point: **the chart is the tool** and takes the large majority of the area; the
network strip below it is tall enough for one row of symbols plus the slider for the selected element;
the generator column is narrow. The splitters are draggable and their positions persist in the document.

### 5.3 Chrome — borrowed, not invented

`match.md` §9 and `railrf.md` §11.1–§11.2 already settled this window's conventions over roughly ten
rounds of owner review, and this tool takes them rather than re-deciding them:

- **Two-tier chrome**: `Border.pane` for a region, `Border.card` for a group inside it; one tile border,
  6 px corners. **Sentence-case headings.** **Label left, value right**, numbers in one right-aligned
  column whether settable or read-only.
- Settable values are **`InlineEditText`** — see below, where this is a rule rather than a convention.
- **Centred text in comboboxes and on buttons**; the one standing exception is a click-to-sort column
  header, which stays left-aligned with its column.
- **A status strip that states numbers**, and refusals that appear in it *with numbers in them*, with the
  offending input turning red.
- **Compact sliders** (the negative-margin trick) so a slider in a row does not make the row tall.
- Advanced settings live behind **Settings**, not on the face of the window.

#### Every editable value in this tool is an `InlineEditText` — owner instruction

Not "most", and not "the ones in the specification pane". **Every** number, name and expression the user
can change in this window is an `InlineEditText`: the generator table's f, R and X cells; the chart Z₀;
every parameter
value beside every slider; a TLIN's F_ref; each element's instance name; each slider's range endpoints;
and a TLIN's F_ref. (An overlay's own fields are the Data Display trace card's — §5.7 — and that card
is the Data Display's, unchanged.)

The reason is the one the owner gives: the control is already carrying the Match Designer's specification
pane, railRF's source/load/aggressor rows and harmonicaRF's readout strip, so **the user has already
learned it and we have already debugged it**. What comes with it, free and identically:

- **The three-key contract, which is the same everywhere in this application** — Return commits and sets
  `e.Handled` (or the hosting window's default button swallows it), LostFocus commits, Escape reverts.
  The control's own doc comment says why it is worth naming: *getting any one of the three wrong is an
  edit the user loses by clicking away*, and it is the kind of bug that is reported months later as
  "sometimes it doesn't take".
- **Double-click to open**, never single-click — a single click opens an editor the user only meant to
  click past.
- **The unit is part of the text and is not selected.** `Text` carries the whole `"1.5 nH"`; opening
  pre-selects only `"1.5"`, so typing replaces the number and keeps the unit. That is the schematic
  editor's own inline-edit behaviour.
- **`HorizontalContentAlignment` is honoured by the resting text *and* the open box together**, which is
  what makes §5.3's right-aligned value column work: a value that jumps to the left edge when it opens
  reads as a different control appearing rather than the same one opening.
- **`Watermark`** for an empty optional field, dimmed and never committed.

**The one thing to decide rather than inherit is the hosting**, and the control's own remarks name the
two that exist: the Designer's rows swap the box **in place** because a row is a fixed grid cell, while
the readout strip **floats** its box in a `Canvas` overlay because its columns are width-shared and an
in-place box would shove every column sideways. The generator table is the only grid of cells in this
window, and its three columns are **fixed-width by construction** (a frequency and two ohm values), so
it takes the in-place hosting. Nothing here needs the floating overlay, which is the more delicate of
the two.

### 5.3a The Load panel — owner instruction, 2026-09-19

Below the generator table, in the same column and in the same three columns — **f / R / X** — a
**read-only** table saying what the cascade **lands on** at each of the generator table's own
frequencies. The generator table says where the design starts; this says where it ends, at the same
frequencies, so the two are read against each other row for row.

- **Every number is `SmithReadings`'**, handed in from the one evaluation that also produced the chart
  and the status strip (§4.6's rule: a VSWR or a conjugate derived twice is two chances to be wrong in a
  quantity whose wrong value looks entirely ordinary). The strip already reports the load — but at the
  **design frequency only**, which is one row of the table, and the question a matching network poses is
  what the *other* rows are doing.
- **Read-only, and the one place in this window that is.** §5.3's rule is that every editable value is an
  `InlineEditText`; a load impedance is not editable, because there is no load element and nothing
  terminates the cascade (§3.2) — it is where the walk arrived. The cells are `SelectableTextBlock`s, so
  a number can still be copied out.
- **A refusal is per row.** A file element that does not span one of the table's frequencies makes the
  cascade refuse *at that frequency* — the same condition the chart reports as a missing load point — so
  that row shows an em dash and its neighbours still show their impedances.
- The rows are **kept in step by count and refilled in place**, never rebuilt: `RefreshDerived` runs on
  every pointer move of a gripper drag, and clearing an `ObservableCollection` bound to a list at that
  rate rebuilds the visual tree twenty times a second.

### 5.4 The chart

A `PlotControl` in `PlotType.Smith`, fed a `Plot` the view model rebuilds from the evaluator. Its traces
are: one per enabled element (the trajectories), one for the load points, one for the generator points,
one for the swept band, and one per overlay. Pan, zoom, the marker context menu, the plot
inspector, axis limits and **copy the plot to the clipboard** are the control's own and are not
re-implemented.

**Grippers and the constant-Q arcs are a new overlay seam on `PlotControl`**, not a second Smith chart.
This is railRF's choice rather than harmonicaRF's, and for railRF's reason: harmonicaRF wrote its own
canvas because it had a frame budget to defend and a contour pipeline to schedule, and this tool has
neither. The seam is small — a drawing callback in canvas space, a hit-test that returns a handle, and
press/move/release — and it is the same shape as the `ILayoutCanvasOverlay` the layout canvas already
exposes to wBond and railRF. **A second Smith renderer is the thing this avoids**, because the two would
drift and the difference would be invisible until someone compared a screenshot with an export.

Gripper appearance is deliberately understated (the specification says *subtle*): a small hollow ring in
the trajectory's own colour, brightening on hover, filled while dragging. They are drawn above the
trajectories and **below** the markers, following harmonicaRF's own z-order rule.

**The chart follows the strip's selection** (owner instruction, 2026-09-19). Selecting an element —
by clicking its symbol in the network strip, or through any of the strip's own commands — draws that
element's trajectory **thicker and at full opacity**, with the **other trajectories faded back**; that
is the answer to *which of these curves is this part*. Deselecting restores every one of them, and so
does clicking the strip's background, which drops both of this window's selections exactly as Escape
does. It is `SmithPlotBuilder.ApplyElementHighlight`, and it **mutates the traces already on the plot
rather than refilling them**: selecting is not an edit, so re-evaluating the cascade and rebuilding the
trace collection — which would re-attach every marker and reload every trace card — to draw one line
thicker is work with a side effect and no reason. The **load points, the band, the constant-Q arcs and
the user's own overlays are untouched**: fading the reference data because a component was clicked
would hide the very thing the cascade is being matched to.

### 5.5 The network strip

The network is drawn by **`SchematicRenderer`** — the renderer the schematic editor draws every frame
with — over a projected model built exactly as `MatchSchematicModel` builds the Designer's ladder pane.
It is a **projection, not an editable schematic**: there is no selection model, no wire tool and no
free placement, because the topology is a list.

- **Selection** is by click, on the symbol or on its label; the selected element's sliders appear beneath.
- **Add** appends at the end (nearest the load); **Insert** places before the selected element; the
  buttons carry a menu of the §3.3 vocabulary, each entry naming series or shunt.
- **Reorder** by drag along the strip, or by the ⇅ buttons.
- **Delete** removes the selected element; the chain closes up.
- Instance names are auto-assigned per type (`L1`, `L2`, `C1`, `TL1`, …), editable, unique, and are what
  §6.1 carries into a real schematic.
- The strip scrolls horizontally, and **Zoom to Fit** is its default on any change that alters the count.

#### Mirror — owner instruction

**One button on the strip's toolbar flips the drawing so the generator is on the right and the cascade
grows leftward.** It is the schematic editor's own Mirror Horizontal control, taken verbatim rather than
re-drawn: `MaterialIcon Kind="FlipHorizontal"`, 16 × 16, `Classes="SelectionBtn"`, `Padding="6,3"`, with a
tooltip naming its accelerator — the same glyph the schematic and layout toolbars already carry for the
same idea, so it needs no learning.

**It mirrors the drawing and nothing else.** Stating the three halves of that separately, because each
has a wrong version:

- **The topology does not change.** Element 0 is still the one nearest the generator, the list order is
  untouched, and §3.2's recurrence runs exactly as before. The flag reaches only the projection, where it
  negates the direction the x-cursor advances.
- **The symbols mirror too, not just their positions.** Each projected component gets `MirrorX = true`,
  which `SchematicGeometry.LocalToWorld` already honours for pin coordinates as well as for the glyph.
  This is not cosmetic: an `S2P`'s port-1 marking means something, and a symbol that kept its handedness
  while its position reflected would draw port 1 on the *load* side of a part whose file says otherwise.
- **The chart does not mirror, and there is no button offering to.** The Γ plane's orientation is fixed
  by physics — inductive above the real axis, capacitive below — and a mirrored Smith chart is simply a
  wrong one.

It is a **view setting**: it lives in the document's `View` block (§7), it marks the document dirty, and
it is **one undo entry**, on the Data Display's own precedent that a persisted view change is undoable
(`PushAxesWindowChange`). A discrete toggle costs one entry and a user who flips it by accident should be
able to take it back with the key they already use.

**The generator panel does not move.** It stays where §5.2 puts it, to the left of the chart, because it
is a docked panel with a persisted splitter and the instruction was about the network rendering. That is
a reading rather than a certainty, and it is Q-16 in §12.

### 5.6 The value sliders

One row per settable parameter of the selected element — one for an L, three for an SRLC, two for a
TLIN, and **none at all for an `S1P` or `S2P`**, whose value is a file. Selecting one shows its file
reference, its port count and its frequency span, and nothing to drag.
Each row is a label, a compact slider and an `InlineEditText` showing the value with its unit.

- **The slider is logarithmic** over a range centred on the current value, one decade either side by
  default, for R/L/C/Z₀. It is **linear** for an electrical length and for the real and imaginary parts
  of a Z1P. The range is shown at the ends and is editable (right-click ▸ *Set range…*); typing a value
  outside the range re-centres it rather than clamping.
- **Dragging a slider is live**: every step re-evaluates and redraws. One drag is one undo entry, on §4.3's
  terms and for §4.3's reason.
- **The last-touched parameter becomes the element's active parameter** — which is what the gripper drags.
  The active row is marked, so the connection between "the slider I just used" and "the handle on the
  chart" is visible rather than remembered.

### 5.7 Overlays

Additional data on the chart, added **the way it is added to a Smith chart on a Data Display**: pick a
source in the combo above the chart, open **Plot Properties…**, press **Add**, and edit the trace card.
There is no Overlays panel. *(Owner instruction, 2026-09-19; brief 12, which replaces brief 8's panel.)*

That sentence is a constraint rather than a comparison, and it settles most of the design: **nothing here
invents an overlay UI.** The trace card already picks a matrix element, a virtual Z or Y, a derived
`DerivedParameters` mode — of which `SourceStabilityCircle` and `LoadStabilityCircle` are the two this
tool was asked for by name — a colour, a line style, a marker glyph, a Z₀ and its override, a cube name
and slice, an expression, a *plot versus* X spec, and its own markers. The panel it replaces had seven
properties where the card has thirty, and a panel left in place beside the inspector would be two authors
of one list.

The sources are the two circuitRF already has:

- **a Touchstone file**, referenced by a path **relative to the document** (the `.cdd` convention, and the
  one that survives an archived or moved workspace — the repointing work in the archive/Window-Layout
  round is what made relative references actually resolve). A scratch `.csmith` with no workspace open
  can still load one: the combo's **Add from file…** is wired to the same picker, which is what keeps
  this a real document rather than a workspace feature;
- **a cube in an open `DataSet`**, referenced the way a Data Display trace card references one.

All of it is renormalized to Z₀_chart on the way in (§3.4), and a trace the card creates seeds with the
chart's Z₀ and the override **on** — *a 75 Ω part drawn on a 50 Ω chart without it is a curve in the wrong
place that looks entirely plausible.* It also seeds **out of the autoscale**: a stability circle can be
enormous, and one unlucky overlay should not reframe the work. Both are the card's to change afterwards,
the second through a checkbox the Data Display's own trace card gained for this (`Trace.ExcludeFromAutoscale`
existed, was set only in code, and had no control and no persistence).

**Opening the trace set is the small part; making an added trace survive is the work.**
`SmithPlotBuilder.Fill` clears the plot's traces and refills them from the design on every committed
edit, so a trace added in the inspector would be gone by the next keystroke and one removed would be back
— which is why brief 5 closed the set in the first place. Three things reopen it:

1. **The plot says so.** `Plot.IsFixedReadout` meant two things — the plot TYPE and the trace SET — and
   only the first is wanted here, so `Plot.AllowUserTraces` opens the second. It defaults **off** and must
   stay off on railRF's `ImpedancePlot` and the Match Designer's response plots, which have nowhere to
   write a user-added trace down.
2. **The trace INSTANCES are carried across the rebuild**, not re-resolved from their configs. The
   inspector's cards, its selection and each trace's markers all hold the `Trace` object; replacing it
   leaves every one of them pointing at a discarded copy.
3. **The set is harvested back into the `.csmith`** as the Data Display's own trace configs (§7). Every
   trace on the plot without `ExcludeFromAxisLabels` is a user trace — the tool sets that flag on
   everything it derives — and an add or a remove is **one undo entry**, while a card's own settings ride
   along on the next save. (An entry per card keystroke is the Match Designer's "eight edits took fourteen
   undos" by a slower route.)

A card for one of the **tool's own** traces offers no trash and no data pickers: removing it would remove
it until the next keystroke, and re-aiming it would be undone by the next rebuild.

**Visible is gone, with no replacement.** `TraceProperties.Enabled` is read by nothing, and a hidden trace
would still sit in the trace list, the legend and the Add Marker menu. On a Data Display you delete the
trace; here the card's trash is the same affordance.

A reference that does not resolve **says why in the status strip and stops there** — the document opens,
the rest of the chart draws, and the overlay is **kept in the document** rather than dropped by the next
harvest. That is the opposite of an S1P element, whose missing file is a refusal because the cascade
cannot be walked without it.

### 5.8 Menus and commands

The document contributes to the shell's menus rather than owning a menu bar (§5.1):

- **File** — New Smith Chart, Open, Save, Save As, Close, all the shell's own.
- **Edit** — Undo, Redo, Cut/Copy/Paste routed to whichever pane has focus (§6).
- **View** — Zoom to Fit (chart), Zoom to Fit (network), Show/hide: grippers, targets, Q arcs, labels.
- **Insert** — the §3.3 vocabulary, mirroring the network strip's Add menu, so every element is reachable
  from the keyboard.
- **Tools ▸ Smith Chart** creates a new scratch document, beside *harmonicaRF*, *Match Designer* and
  *railRF*. Both the native (macOS) and in-window copies of that menu must be edited together; the two
  surfaces are hand-maintained and the file says so.

### 5.9 On Launch — owner instruction

`Settings ▸ General ▸ On Launch Action` gains a **Smith Chart** row, which opens a new scratch `.csmith`
at startup. This is possible *because* of §5.1 — every existing member of that list is a document, and
railRF and wBond are absent from it for the same reason.

**The trap, already documented beside the code and repeated here because this change walks straight into
it:** `LaunchAction` is **serialized as an ordinal**, and the Settings combobox is populated by a
positional string array whose index is cast directly to the enum. So:

- `NewSmithChart` is **appended** to `LaunchAction`, never inserted;
- `"Smith Chart"` is **appended** to `SettingsView.LoadGeneralPrefs`'s array, never reordered;
- and the two must be edited in the same commit, because reordering either one silently changes what
  every already-saved `preferences.json` means.

`ExecuteLaunchActionAsync` and `ApplyOnLaunchActionForNewWorkspace` each gain the case. A test asserts the
array's length and order against the enum, so the next person cannot get it wrong quietly.

---

## 6. The clipboard

**No clipboard code is written.** The owner's instruction is explicit and it is also the cheapest
possible route: the schematic editor's copy path has had a great deal of debugging invested in it —
Windows' `CF_ENHMETAFILE` in particular, which must be written in a *single* P/Invoke session because
Avalonia's `SetDataAsync` empties the clipboard and keeps ownership — and every bit of that is reused as
a call rather than as a pattern.

### 6.1 Copy the network out

Right-click the network strip ▸ **Copy**. A projection built exactly as `MatchSchematicCopy` builds the
Designer's — real `EditableComponent`s at the coordinates the strip drew them at, real `EditableWire`
spine segments in the gaps between series bodies, one ground per shunt column — is handed to
**`SchematicClipboard.CopyAsync`**. That one call produces, simultaneously:

- **the schematic JSON**, which pastes into a real `.csch` as real, editable components;
- **SVG** and **PDF** vector, which paste into Keynote and (via the EMF path) PowerPoint as vectors;
- **PNG**, for everything else.

Two decisions about what the copied circuit *contains*:

- **The generator becomes a `TermG` with `Num=1` and `Z` = the generator impedance at the design
  frequency**, and **the load end becomes a `TermG` with `Num=2` and `Z` = Z₀_chart** — so what lands in a
  schematic is a complete, runnable two-port, not a fragment with dangling ends. No analysis card is
  copied: a pasted selection is a fragment of a circuit, and the TestBench it lands in owns its analyses.
- **A `Term` carries one impedance, so a multi-row generator table is a lossy projection.** When the table
  has more than one row the status strip says so on copy, naming the frequency that was used. It is
  stated rather than prevented, because the copy is still the right circuit at the design frequency and
  that is what a user pasting into a presentation or a schematic wants.

**The copy follows the mirror** (§5.5). `MatchSchematicCopy`'s own stated rule is that a copy is *the
drawing on screen, not the flattened cell* — it places every component at the coordinates the pane drew
it at — and this tool takes that rule with it. Someone who flipped the network to make a figure and then
copied it would not thank us for un-flipping it on the way out. The pasted circuit is electrically
identical either way; only its geometry is reflected, and `MirrorX` travels on each component so a
2-port's port 1 still faces the generator.

The claim that "the copy is the network you were looking at" is not asserted — §4.6's gate runs the same
netlist through the engine, so the two agree by test.

### 6.2 Paste a `.csch` selection in

Right-click the network strip ▸ **Paste**. `SchematicClipboard.PasteAsync` returns components and wires;
a **recognizer** then decides whether they form a cascade this tool can represent, and either replaces the
network wholesale or refuses with a sentence naming what stopped it.

The recognizer builds the net graph and requires, in this order:

1. every component is either a §3.3 element type, a ground, or a `Term`/`Port`;
2. every non-ground net has degree 2, except the two end nets;
3. exactly two end nets exist, and the walk between them is unique — **any branch is a refusal naming the
   net**;
4. every element hanging off the through path has its other pin on ground, and no other component does;
5. no component carries a parameter this tool cannot represent (an expression, a swept variable, a
   hierarchical reference) — **a refusal naming the instance and the parameter**, because silently
   dropping an expression would change the circuit.

**Which end is the generator** is decided by: a `Term`/`Port` with the lowest `Num`, if there is one;
otherwise **the end that matches the strip's current mirror setting** — leftmost by x when the drawing
runs generator-left, rightmost when it is mirrored (§5.5). The geometric fallback had to be stated that
way rather than as a bare "leftmost", because a flipped strip would otherwise reverse every pasted
network that carried no port, silently and half the time. The strip states which of the two rules fired,
because they can disagree and the user is the only one who knows which they meant.

The refusals matter more than the successes. A permissive reader that accepted *part* of a paste would
replace a user's network with something that is not what they copied and report success — which is
exactly the failure `RailClipboard`'s marker guard exists to prevent, and the reason it is quoted in §2.3
as the shape to copy.

**One paste is one undo entry**, restoring the entire previous network.

### 6.3 Copy the chart

Right-click the chart ▸ **Copy**, or Edit ▸ Copy with the chart focused, calls
**`PlotExporter.CopyPlotToClipboardAsync`** — PDF, SVG, the Data Display config JSON, and a 2× bitmap, all
on the clipboard at once. Trajectories, grippers, targets, Q arcs and markers are all in the rendered
picture, because they are all in the `Plot` and its overlay.

Every emitted SVG goes through `SvgFontNormalizer` on the way out of Skia's SVG device, as every other
export in the repository does. That is not optional and it is not this tool's business to know why.

---

## 7. Persistence — the `.csmith`

`.csmith` is read and written by `SmithDesignIo`, which **mirrors `RailDocumentIo` exactly**, which mirrors
`EmSetupPersistence`, which mirrors `TechPersistence`: `System.Text.Json`, `WriteIndented`, enums as
strings, `WhenWritingNull`, a `FormatVersion` that **refuses a newer file rather than half-reading it**,
an atomic write, and a gzip sniff on load. A fifth spelling of the same thing would be a fifth thing to
keep in step.

**Numbers are stored in base SI**, and the scale lives nowhere near them: a frequency field is hertz, an
inductance henries, a capacitance farads, an impedance ohms. This is the sweep-unit trap recorded in
`src/Engine/RESOLVED.md` — a mark read without its scale once produced a run at 2 Hz that looked entirely
normal — and it is worth restating for a tool whose inputs are all picohenries and gigahertz.

**The one deliberate exception is electrical length, stored in degrees in a field named `…Deg`**, following
`RailTarget`'s millivolts precedent. The rule the base-SI convention actually protects is *"a number must
not be readable at the wrong scale"*, and a field whose name carries its unit satisfies it. Radians would
match the letter and would disagree with `TLIN`'s own `E` parameter, the schematic, the UI and every
textbook, at four conversion sites.

```
SmithDesign
  FormatVersion, Name
  Chart        : Z0Ohm, Window (Γ extents), ShowGrippers/Targets/Labels/AdmittanceGrid
  Generator    : Rows[ { FrequencyHz, ResistanceOhm, ReactanceOhm } ], SourcePath (provenance only)
  Elements[]   : Kind, Placement, Name, Enabled, ActiveParameter,
                 Values{ ROhm, LHenry, CFarad, Z0Ohm, ElectricalLengthDeg, ReferenceFrequencyHz,
                         ImpedanceOhm{Re,Im} },   FileRef (relative, S1P/S2P only),
                 SliderRange{ Min, Max } per parameter
  ConstantQ    : Enabled, Q
  Overlays[]   : the Data Display TraceConfig shape, verbatim — one per overlay, opaque JSON
  Markers[]    : the Data Display Marker shape, verbatim
  View         : splitter positions, network scroll/zoom, MirrorNetwork
```

**Both of the last two blocks are the Data Display's own, and only one of them is a mirror.** A marker is
`SmithMarker`, a hand-written copy of `MarkerConfig` field for field, so *the bytes a `.csmith` puts on
disk for a marker are the bytes a `.cdd` puts on disk for the same marker*. That works because a marker's
shape is settled.

**`TraceConfig`'s is not, so `Overlays[]` is opaque JSON and `SmithDesign` holds it as
`List<JsonElement>`** (brief 12). It pulls in `TracePropertiesConfig`, `MarkerConfig`, `AxisSliceConfig`,
`WspTraceConfig`, `ContourTraceConfig` and `SummaryColumnConfig`, and it is a live type that grows
whenever the Data Display gains a per-trace setting — WSProbe, pattern mirroring, the dBm reference
override and *plot versus* are all recent additions to it. A mirror would fall out of step in silence, and
the symptom would be a setting that survives in a `.cdd` and vanishes from a `.csmith`. The file still
contains ordinary readable JSON with the same keys a `.cdd` writes; what it no longer does is give that
JSON a type in a project that must not know about traces. `src/Ui` and `src/Cli` read and write it with
the **same** `JsonSerializerOptions` the `.cdd` uses.

**Brief 8's seven-field `Overlays[]` rows migrate on read and the old block is dropped on write.** Nothing
shipped carries an overlay, so that is cheap insurance rather than a feature — and the alternative is a
user's own `.csmith` quietly losing its reference data. Two of the seven do not survive, both
deliberately: `Visible` (§5.7 — it goes with no replacement, and a hidden row migrates as a visible
trace rather than being dropped) and `ColorHex` (a `.cdd` stores a colour as an INDEX into the palette
and never as an ARGB value, which is the property that made the palette change in RND-4 cost no saved
file).

**Validated on the way out as well as in**: a document that cannot be read back is a document that was
never written, and the alternative is a file whose only symptom is that it refuses to open next week.
The **clipboard** flavour skips the outbound validation, on `RailDocumentIo.SerializeUnvalidated`'s own
reasoning — a half-built design is exactly what someone copies while they are still working, and a copy
that writes nothing leaves the *previous* copy on the clipboard for the next paste to find. **It is also
the undo path**, which is why the overlay round trip is gated through it specifically: every committed
edit calls it to build the snapshot, so a field lost there is lost on the next *undo* rather than on the
next save.

The extension is registered with the shell like every other document type, so a double-click opens it;
`project-file-formats.md` gains a row.

---

## 8. Architecture

The rule is `ui-architecture.md`: nothing below `src/Ui` may reference a UI framework, and
`tests/Firewall.Tests` enforces it transitively.

| piece | home | why |
|---|---|---|
| `SmithDesign`, the element records, `SmithDesignIo` (`.csmith`), `SmithClipboard` (marker-guarded JSON) | `src/Design/Smith/` | The document layer, beside `RailRf/` and on its terms. Framework-free, so the P3 verb and the tests reach it with no display. |
| `SmithCascade` — the evaluator, the trajectories, the gripper inverse, the constant-Q circles | `src/Design/Smith/` | Arithmetic over a document, the way the EM extractors beside it are. It reaches `RfCore` for Touchstone and renormalization, which `src/Design` already references. **Not `src/Engine`**: Engine's own rule is "no domain types", and this takes element records. |
| the chart | **reused**: `src/Render/DataDisplay` + `PlotControl` | One Smith renderer in the product (§5.4). |
| the network drawing | **reused**: `SchematicRenderer` in `src/Render/Renderers` | The renderer the schematic editor draws with, below the firewall since RND-1. |
| the gripper / Q-arc overlay | `src/Ui/Smith/` (draw callback) + the seam on `PlotControl` | Transient chrome. The overlay *description* is framework-free; the input handling is not. |
| `SmithChartDocument`, the view model, the window content, the clipboard wiring | `src/Ui/Smith/`, `src/Ui/Views/Smith/` | The only part that docks, undoes, or observes a canvas. |
| the `smith` CLI verb | `src/Cli/Smith.cs` | P3. Argument parsing, refusals and reporting only — `Authoring.cs`' standing rule. |

**Naming, to avoid a collision that would otherwise be discovered late:** the model is `SmithDesign`
(mirroring `MatchDesign`) and the Dock document is `SmithChartDocument` (mirroring `DataDisplayDocument`).
`SmithDocument` is used for neither, because it would be the obvious name for both.

**Nothing in `src/Design/Smith/` draws and nothing in `src/Render` edits**, which is the pair of sentences
each of those `.csproj` files already makes about itself. This tool is the easy case for both.

---

## 9. Phasing — owner decision

Three phases. The owner's instruction is that the window ships first and the headless verb follows, with
the arithmetic below the firewall **from day one** so that P3 is wiring rather than a refactor.

**P1 — the tool.** The document and its `.csmith`; the generator panel with `.s1p` import and Conjugate;
the element vocabulary; the evaluator and the trajectories; the network strip; selection and sliders;
grippers and their inverse; undo/redo; the per-frequency load points and the generator glyphs; both
clipboard directions; Tools ▸ Smith Chart and the On Launch row. **This is the whole tool as specified**,
and it is the phase to build if only one is ever built.

**P2 — the surrounding material.** Overlays (Touchstone, cubes, stability circles); markers and VSWR
circles; the constant-Q arcs; the swept band; the element enable/disable and reordering polish.
Everything here is independently useful and none of it is needed to match an impedance.

**P3 — headless.** `circuitrf smith <f.csmith>` evaluating a document and writing the load Γ as a
Touchstone `.s1p` (`-o`), or the chart as a picture through the existing `render` path. The verb calls the
same `src/Design/Smith` functions the window calls and holds no logic of its own; the gate is the one
every other verb has — the CLI as a *process*, byte for byte against the in-process call, plus a
comment-stripped source scan proving the view model kept no second copy.

### 9.1 What shipped, per brief (2026-09-19)

All eleven briefs were built. Findings live in the `RESOLVED.md` beside each project — `src/Design` for
briefs 1–3, `src/Ui` for 4–9 and 11, `src/Cli` for 10 — and never here.

| Brief | What landed |
|---|---|
| **1 — the document** | `SmithDesign` and its element records, `SmithDesignIo` (`.csmith`), `SmithClipboard`, and all seven registration points — the `OpenFiles` dispatcher, `WorkspaceViewModel`, `WorkspaceScanner`, the macOS `Info.plist`, the WiX `.wxs`, the Linux mime file and `DocumentKinds.Classify` |
| **2 — the cascade** | `SmithCascade`, every element immittance, the projective walk, the trajectory sampler, the Touchstone elements and the generator interpolation — gated against the S-parameter engine, every element type in both placements |
| **3 — the gripper inverse** | every closed-form inverse, the TLIN Z₀ quadratic, the stub branch unwrap, and physicality pinned at the boundary with the parameter and the limit named |
| **4 — the document window** | `SmithChartDocument`, the view model, the chrome, `InlineEditText` everywhere, the generator panel with `.s1p` import and Conjugate, Tools ▸ Smith Chart, and the `LaunchAction` row with the ordinal test that holds its position |
| **5 — the chart** | the `Plot` built from the evaluator, `PlotControl` hosting **and its container**, the gripper overlay seam, the drag loop and its one-entry undo contract, the load points and the per-frequency generator glyphs |
| **6 — the network strip** | the projection onto `SchematicRenderer`, selection, add / insert / delete / reorder, the sliders, the active-parameter rule and the mirror |
| **7 — the clipboard** | the network out as a runnable two-port, the chart out as PDF/SVG/JSON/bitmap, a `.csch` selection in, the topology recognizer and its five refusals, and the mirror-aware end rule |
| **8 — overlays and markers** | Touchstone and cube sources, renormalization to Z₀_chart, derived stability circles, markers and their VSWR circles |
| **9 — constant Q and the band** | the arc pair and its closed-form in-disc range, its drag inverse and the shift quarter-step, the swept band (whose own start/stop/npts and clamp were withdrawn in the fourth round — §3.6) |
| **10 — the verb** | `circuitrf smith`, the reading, the per-node walk, `-o .s1p` and the picture — and the plot half moved to `CircuitRF.Render` so the picture is the window's |
| **11 — docs and example** | `docs/user/reference/smith-chart.*` with six generated figures, the `Smith Chart` example workspace, and this record |

**Driving the finished tool as a user found three defects, none of which reported a failure** — which is
the shape `railrf.md` §6.1 records for its own equivalent round, and the reason `R-smith11-4` exists.
Each has a test that fails at HEAD; the detail is in `src/Ui/RESOLVED.md`.

- **Every frequency in every refusal was in scientific notation, in bare hertz.** `SmithDesign.Fmt` was
  `"G6"`, and .NET's `G` switches to exponential the moment the decimal exponent reaches the precision, so
  a user who typed `2.9 GHz` was refused with *"the design frequency 2.9E+09 Hz is outside …"* and a drag
  that pinned an inductor reported `-5.55285E-10 H` beside a slider reading `1.97 nH`. It is the same
  defect `MatchValueFormat.Significant`'s own remarks record from the Match Designer's value grid one
  project along. Fixed by calling that, with `FmtHz`/`FmtOhm`/`FmtOf` picking the prefix.
- **The generator table's column headers sat over the wrong columns.** The header grid and the row
  template share `ColumnDefinitions="86,*,*"`, but the headers took `gridhdr`'s left alignment while R and
  X are right-aligned numbers — so `X` was drawn directly above the R column's digits. Two ohm values one
  place out of step is a wrong impedance read off a table that looks entirely ordinary.
- **There was no route from the window to its own chapter.** railRF, harmonicaRF, wBond and the Match
  Designer each carry a Help button; this one did not, so §11's chapter was reachable only by knowing it
  existed. A docked document has no title bar of its own, so it went at the end of the network strip's
  toolbar and its destination is `DocAnchors`', which the docs run gates.

### 9.2 Round two — ten items from driving it (2026-09-19)

A second pass over the finished tool. The detail is in `src/Ui/RESOLVED.md` and
`src/Render/RESOLVED.md`; what changed **in this document's own decisions** is listed here, because the
sections above would otherwise now be wrong.

- **§5.4 — the chart opens with axis panning LOCKED**, reversing the original choice. The reasoning
  then was that a press on empty chart should pan and there is no Data Display canvas here for the
  move/select gesture to conflict with. The gesture this chart actually spends its time on is a drag,
  on a gripper, a Q arc or a marker, and every press that missed one of those slid the chart instead.
  The context-menu item is unchanged; what changed is the state a chart opens on.
- **§5.4 — markers are placed FREELY** (`Plot.FreeMarkers`, `Marker.FreePosition`). §4.5's claim that
  markers are "the Data Display's own `Marker` objects" still holds — they are the same objects, on the
  same traces, with the same VSWR circles and the same persistence — but their POSITION is no longer
  resolved against the curve they are stored on. A marker on a matching chart is a target the user is
  aiming the network at, not a reading of a trace at a frequency. **Holding shift while dragging snaps
  to the nearest curve on the plot**, measured in canvas pixels and across every trace including the
  annotations, which is how one is put exactly on a stability circle or on the load locus. The readout
  is Γ and Z = Z₀·(1+Γ)/(1−Γ) against the chart's own Z₀, with no frequency row.
- **§5.4 — the chart carries an optional ADMITTANCE GRID**, the constant-g/constant-b family, which is
  the impedance family reflected through Γ = 0 and drawn in a faded red beneath it. It is a `Plot`
  setting toggled from the chart's own context menu below *Axes Labels…*, so **every** Smith chart in
  circuitRF has it; it persists in a `.cdd` and in the `.csmith`'s `Chart` block alike. It carries no
  numbers — the impedance labels already crowd in two dimensions.
- **§4.4, §5.2 — the constant-Q card is gone.** The toggle is a square toolbar button with a **Q** glyph
  in the chart's top-left corner, and **the VALUE is drawn on the chart**, hanging from the apex of the
  inductive arc so it tracks the arc as a drag moves it. It is drawn by `SmithChartChrome`, below the
  firewall, so it travels with every copy, export and headless render rather than living in a panel no
  exported picture carries. The typed-entry field went with the card: Q is set by dragging an arc, and
  shift still lands on an exact quarter.
- **§5.6 — a slider's range is a CONSTANT per parameter and placement**, quoted for the 2 GHz design
  frequency. The original rule — one decade either side of the value — was a runaway: the maximum was a
  function of the value the slider sets, so dragging to the top multiplied the ceiling by ten on every
  pointer move. A value carried outside by a gripper drag widens the range to the next 1/2/5 × 10ⁿ,
  decade-snapped, which is stable. **A range that reaches zero is drawn on a LINEAR slider**, because a
  log axis cannot express zero and 0 … 10 nH is the range an inductor wants.
- **§5.5 — Add, Insert and Delete are square icon buttons**, the schematic and layout toolbars' own, and
  **each Add/Insert menu row carries the part's own symbol**, drawn by `SchematicRenderer` through the
  palette's glyph control and turned the way the strip will draw it: horizontal for a series element,
  vertical for a shunt one.
- **§5.4 — a chart with ONE frequency draws no load-point label.** With nothing to tell the point apart
  from, the box sat on top of the one reading the chart is about, and the status strip already names
  that frequency.

Two defects, both recorded with their causes in the `RESOLVED.md` files: the Save picker spelled
`.csmith` twice (the third appearance of one Avalonia trap in this repository), and the trace glitched
during a drag because a Clear-and-refill of the trace collection autoscaled once per trace — including
once on an empty plot — and because `Plot.RenderSnapshot` shared the trace COLLECTION with the live
plot on the premise that traces are not rebuilt under a pointer, which this tool is the first to break.

### 9.3 Round three — from driving it again (2026-09-19)

Detail in `src/Ui/RESOLVED.md` and `src/Render/RESOLVED.md`; what changed **in this document's own
decisions** is here, because the sections above would otherwise be wrong.

- **§3.4, §5.4 — the faint glyph at each generator frequency is the GENERATOR, not the conjugate-match
  target.** It is drawn at Γ(Z_gen(f)), where the table says the generator is. It used to be drawn at
  Γ(conj(Z_gen(f))), which is that point mirrored about the real axis — a perfectly plausible position,
  at the right magnitude, with the wrong sign on its reactance, and nothing in the picture to say which
  it was. *The conjugate match is unchanged as a concept and is still what the strip's mismatch number
  is about; what is gone is the glyph that claimed to mark it.* The trace is named `Zgen`, and
  `SmithChartScene.ConjugateTargets` is now `GeneratorPoints`. `SmithChartSettings.ShowTargets` keeps
  its name so existing `.csmith` files still read.
- **§5.4 — a load point is drawn at EVERY generator-table frequency**, and a row that cannot be
  evaluated — a file element whose Touchstone does not span it — is now NAMED in the status strip
  rather than silently dropped. The swept band adds frequencies to that set; it does not replace it.
- **§5.4 — the frequency labels are placed VERTICALLY, away from the cluster.** Each box hangs above
  its own glyph when the other load points are below it and below when they are above, then is pushed
  one row further out until it clears every glyph and every box already placed. The old rule fanned the
  stubs radially outward from the centre of the chart, which spaces the labels from each other but says
  nothing about where the other points are — and a locus running outward from the centre put every
  label straight over the next point along it.
- **§5.4 — the constant-Q value reads `Q=1.75` and carries no background plate**, and **the grab ring
  is drawn only while an arc is being DRAGGED.** On hover it appeared within eight pixels of either arc
  and then glided along it, which over a chart crossed by two arcs reads as a circle chasing the
  cursor. The hit test is unchanged, so the arcs are grabbed exactly as before.
- **§5.4 — only the user's own data names an axis** (`Trace.ExcludeFromAxisLabels`). A Smith plot
  carries one Y-axis label strip and one `freq (a to b)` X row PER TRACE, and this chart derives a
  dozen traces nobody asked for by name — one per cascade element, the load points, the generator
  points, the band, the arcs. Every one of them was taking a label column down the side and a row
  along the bottom. The flag is set on everything `SmithPlotBuilder` derives and left clear on the
  OVERLAYS, which are the reference data the user chose and are what the labels are for. The Y half
  never drew on screen at all — this window hosts a bare `PlotControl` rather than a
  `PlotContainerView`, so the strips only ever appeared in a copy or an export, and a chart pasted
  into a presentation came out with a column of labels the window had never shown.
- **§4.5 — Add Marker is a single row on this chart, not a submenu.** A marker here is a position
  rather than a reading of one curve (`Plot.FreeMarkers`), so asking which trace to put it on offered a
  choice that changes nothing. **Delete removes the selected markers and Escape drops both selections**
  — the markers' and the network strip's element.
- **§5.2 — the generator column is narrower**, and its **Conjugate** button now sits above **Import
  .s1p…**: Conjugate edits the table directly above it, and the import is what replaces that table.
- **§6.1 — the copied generator port is named `Generator`**, not `Gen`.

**One defect fixed with a new event.** A marker deleted from its context menu came back on the next
component edit. Two of that menu's three removal paths go through the container's view model and never
raise `PlotChanged`, so this document — which is the authority for the marker set, and which rebuilds
every trace on every edit — never heard about the removal and re-attached the marker. `PlotControl`
now raises `MarkerRemoved` on all three.

### 9.4 Round four — overlays move to the inspector (2026-09-19)

**Owner instruction:** remove the Overlays panel; overlay S-parameter data sources are added through the
Plot Properties inspector, **the same way they are added to a Smith chart on a Data Display.** Brief 12,
which replaces brief 8's panel. §5.7 and §7 are rewritten for it.

- **The panel is deleted** — `SmithOverlayRowViewModel`, `AddOverlayCommand`, `RemoveOverlayCommand` and
  the list in `SmithChartView.axaml` — and a source-scan test holds it deleted. Two authors of one list
  would disagree the first time either changed.
- **`Plot.IsFixedReadout` is split.** It meant the plot TYPE and the trace SET, and this chart wants only
  the first kept. `Plot.AllowUserTraces` (default **off**) opens the set; the type picker is untouched.
  railRF's `ImpedancePlot` and the Match Designer's two response plots set only `IsFixedReadout` and are
  unaffected, which their own gates assert by name.
- **`Add` seeds from the LIBRARY on such a plot, never from the last trace.** On a Data Display "another
  one like the last" is the commonest Add and is right there; here the last trace is always one of the
  tool's — a cube trace whose name is an element's and whose points were pushed in by the evaluator — so
  a clone of it is bound to a cube that exists nowhere, drawing a frozen copy of a curve that will not
  track the design. The two kinds are told apart by `Trace.ExcludeFromAxisLabels`, which round three
  already set on everything the tool derives.
- **The chart grew a data-source combo**, the Data Display's own, in the strip beside the **Q** button.
  `Add` seeds from `DataSourceLibraryViewModel.SelectedEntry`, so without a way to choose one the button
  would add nothing and say nothing about why.
- **`SmithDesign.Overlays` is now `List<JsonElement>`** — the Data Display's own `TraceConfig`, opaque to
  `src/Design`. §7 gives the reason; brief-8 rows migrate on read.
- **`PlotConfigLoader.LoadTrace` was extracted** from `LoadPlot`'s loop — a pure extraction, gated by the
  existing `.cdd` suite — so the window and `circuitrf smith` restore an overlay through the `.cdd`'s own
  reader. The writer already existed: `DataDisplayViewModel.BuildTraceConfig`.
- **`TraceConfig.ExcludeFromAutoscale` and a trace-card checkbox** are the one place this round touches
  the Data Display's own card. The flag existed on `Trace`, was set only in code, and had no UI and no
  persistence; §5.7's reason for it is specific enough that losing either the protection or the control
  was not acceptable.
- **`SmithOverlayResolver` is gone.** What it did — read a Touchstone, parse a quantity, set a
  renormalization — a trace config says and `LoadTrace` has honoured since the Data Display's first
  release. What is left of the file is `SmithOverlayMigration`, which is the brief-8 mapping and nothing
  else.
- **Two defects the reuse exposed**, both of which only appear once a `Trace` outlives a refill:
  `SmithPlotBuilder.RestoreMarkers` now clears each trace's markers before re-attaching the document's
  (a reused overlay accumulated a second copy of every marker on every keystroke), and the status strip
  clears its own unresolved-overlay sentence when the overlays are re-resolved (a document's folder
  arrives *after* its design, so every relative reference fails once and then resolves — and the note
  from the first attempt sat there naming a file that was in fact right beside it).

---

## 10. Acceptance

**The invariants worth a test each:**

1. **Every element, both placements, against the engine.** §4.6: the closed-form cascade versus an
   S-parameter run of the equivalent `.cnl`, to 1e-9 relative on Z. This is the anchor.
2. **The classical constructions.** A series L from a real Z sits on that Z's constant-resistance circle
   to machine precision; a shunt C on the constant-conductance circle. Cheap, and it is what a user
   checks by eye.
3. **The gripper inverse is exact.** For each parameter in §4.3's table: drag to a Γ that is reachable,
   read the value back, re-evaluate, and land on the drag point to machine precision.
4. **A stub through the pole is continuous.** A 135° open stub's trajectory is one polyline whose
   successive canvas-space steps are bounded — no jump, no NaN, no dropped segment.
5. **The constant-Q circle is the constant-Q locus.** Sample the drawn circle, map to z, and assert
   `|x|/r = Q` to machine precision on every sample, over Q ∈ {0.5, 1, 3, 10, 100}.
6. **A drag is one undo entry**, and *n* edits take *n* undos. The regression this exists to prevent is
   the Match Designer's, and the test is the same shape: count entries across a synthetic drag, then undo
   to the start.
7. **Round trip through the clipboard.** Copy a network, paste it back, and the resulting element list is
   identical in type, placement, order and value.
8. **Every refusal in §6.2 fires, and names its instance.** One test per rule, with the sentence asserted
   — a refusal whose text does not identify the offending object is not a refusal anyone can act on.
9. **Mirroring is view-only.** Flip the strip and assert that no element value, no node impedance and no
   chart point changed — and that every projected component's `MirrorX` flipped and its pin world
   coordinates reflected, so the symbols really did turn round with their positions.
10. **A mirrored network survives the clipboard.** Copy a mirrored network, paste it back into a
    *non*-mirrored strip, and get the same element list in the same order — the §6.2 end-rule regression,
    which is the one this change could introduce.
11. **`.csmith` round-trips**, refuses a newer `FormatVersion`, and stores base SI (assert a picohenry
   round-trips as `1e-12`, not as `1`).
12. **The `LaunchAction` ordinal contract** (§5.9): the combobox array's length and order against the enum.
13. **Firewall**: no new test is needed and that is the point — `src/Design/Smith/` is a folder inside
    an already-gated project, so `tests/Firewall.Tests`' existing transitive assertion covers it the
    moment the files land. **No new project is created below the firewall**, which is what makes that
    true; anything here that reaches for Avalonia fails the build rather than the review.

**Cost.** All of the above is arithmetic and file I/O; none of it is a benchmark and none of it should be
tagged `Category=Benchmark`. The suite for this tool belongs in `tests/Ui.Tests`, beside the Match and
railRF tests, and is expected to run in seconds.

---

## 11. Traps already paid for elsewhere

Each of these is a defect the repository has already found and fixed somewhere else, and each one is on
this tool's path. They are listed so that the implementation inherits the fix rather than the bug.

- **An inline editor has three keys and all three must work.** Return commits, LostFocus commits, Escape
  reverts. `InlineEditText` already gets this right; the trap is writing a *fourth* editor somewhere in
  this window that gets one of them wrong, which is why §5.3 makes "every editable value" a rule rather
  than a default.
- **A coercing control's write-back is not an edit.** The Match Designer's slider reached an unguarded
  setter during `Undo`, so every undo added an entry and redo was wiped (§4.3). Publish bounds before
  value; gate the setter on whether the user or a restore is driving it.
- **`LaunchAction` is an ordinal and the Settings combobox is positional** (§5.9). Append only.
- **Base SI in the document, with the scale nowhere near the number** (§7). The 2 Hz sweep is the standing
  example.
- **A shared cached object must be cloned before it is narrowed.** `TechnologyCache` hands back a shared
  instance and `render`'s layer selection had to clone it — the defect that only appears on the *second*
  call in the same process. Any per-render narrowing of a `Plot`, a theme or a trace list here follows the
  same rule.
- **A renderer's target canvas is an argument, not an assumption.** `ContourRenderer` once drew every
  contour on every Smith plot to the first target it was given. The overlay seam passes its canvas
  explicitly.
- **`SkiaFonts` and `ThemeResolver` fall back silently with no app host.** Both are ordinary embedded
  resources in `src/Render` now; a headless render of this chart must produce the same bytes as the
  window's, and that is what makes it possible.
- **Windows' `CF_ENHMETAFILE` must be written in one P/Invoke session** (§6). Reuse the call; do not write
  a second clipboard path.
- **A marker's info box needs somewhere to be drawn.** railRF's `PlotControl` had markers before it had a
  panel to host their boxes in, in the control's own coordinate space. This window hosts them from the
  start.
- **A constant-VSWR circle is not centred on its marker** unless the marker is at Γ = 0 (§4.5).
- **`git log --follow` does not cross the RfCore merge**; `git blame` does. Irrelevant to the code and
  relevant the first time someone reads the history of a file this tool touches.

---

## 12. Open questions

Numbered so review can answer them by number. Q-1 … Q-4 were put to the owner before this document was
written and are recorded closed, with the reasoning, in the sections named.

**Closed before drafting:**

- **Q-1 — the trajectory rule.** *Closed:* scale the element's immittance, not its component values (§3.5).
- **Q-2 — what the chart is normalized to.** *Closed:* a fixed real Z₀, plus a per-frequency GENERATOR
  glyph (§3.4; the glyph was the conjugate-match target until §9.3). Worth re-raising only if review
  wants the generator-referenced grid; the code difference is one function and the cost is a grid that
  moves under the user.
- **Q-3 — the swept band.** *Closed, then reopened and closed again (owner instruction, 2026-09-19):*
  it is always drawn, across the generator table's own span, and is no longer a setting at all (§3.6).
- **Q-4 — how far beyond the window.** *Closed:* window first, CLI verb as P3, arithmetic below the
  firewall from day one (§9).

**Closed during drafting, by owner instruction:**

- **Q-5 — a TLIN's characteristic impedance.** *Closed:* settable, with its own slider, alongside the
  electrical length (§3.3).
- **Q-6 — On Launch.** *Closed:* Smith Chart is an On Launch Action (§5.9).
- **Q-7 — window or document.** *Closed:* a circuitRF document, docked, with no standalone binary (§5.1).
- **Q-8 — mirroring the network drawing.** *Closed:* one toolbar button, the schematic editor's own
  `FlipHorizontal`; the drawing and the symbols mirror, the topology and the chart do not (§5.5).
- **Q-9 — the inline editor.** *Closed:* every editable value in the tool is an `InlineEditText` (§5.3).

**Answered at closeout, from the built tool (2026-09-19, `brief-smith-11-docs-and-example.md`
`R-smith11-5`).** Each records the evidence as well as the answer, because an answer given from a
finished tool is only worth more than one given from a specification if it says what it looked at.

- **Q-10 — should `F_ref` track the design frequency by default?** *Closed: no, as §3.3 said.* Two
  things the built tool has that the question did not. First, the cost of "no" is **stated rather than
  silent**: `SmithChartViewModel.NoteStrandedReferenceFrequencies` raises a sentence the first time a
  design-frequency edit leaves a line's F_ref behind, naming the elements and saying where to change
  it — so the user the question worried about is told, once, exactly when it starts to matter.
  Second, the network strip **draws F on the element's own label** (`F = 2 GHz` on the trajectories
  figure), so the reference frequency is legible in the picture instead of being invisible state. And
  the case for "yes" is self-cancelling: a user who never leaves one frequency has F_ref equal to it
  already, so tracking would change nothing for them while silently re-specifying a line for everyone
  else.
- **Q-11 — should `S1P` be in the element vocabulary?** *Closed: keep it.* It cost what the question
  guessed — `SmithComponentMap` is two rows (`Snp` with `NumPorts=1`, no parameters, no gripper) and
  it shares S2P's reader, its cache and its refusals. And it is load-bearing rather than merely free:
  **S2P is series-only by rule**, so without S1P a measured one-port cannot go in shunt at all, and
  the only remaining spelling for "a real capacitor's file to ground" would be `Z1P`, which is
  constant over frequency and is therefore the wrong model for a measured part.
- **Q-12 — what should the gripper drag on a three-parameter element?** *Closed: the active parameter,
  as §4.3 said.* The question's real cost was *"the handle's meaning depends on invisible state"*, and
  in the built window it is not invisible: the slider panel marks the active row
  (`SmithSliderRowViewModel.IsActive` → `Classes.activeparam`) and clicking a row's label makes it
  active without moving anything, so *the slider I just used* and *the handle on the chart* are
  connected on screen. The fixed-per-type alternative would also have made a TLIN's Z₀ — which is what
  decides **which circle** the rotation happens on — permanently undraggable.
- **Q-13 — the two ends of the walk.** *Closed: node 0 anchored, node N draggable, as §4.3 said.* Node
  0 offers no handle at all (`BeginGripperDrag` returns false and the overlay draws no ring), so it
  does not invite a drag and then decline one. Node N does surprise, exactly as the question predicted
  — so it is **documented rather than removed**: the user chapter says in its own words that the last
  node drags the last element and does not solve the network. An anchored node N would have cost the
  tool its most-named gesture to remove a surprise one sentence answers.
- **Q-14 — does the network strip need a second row for long cascades?** *Re-filed, unchanged.*
  Nothing built exercises it: the strip scrolls horizontally, Zoom to Fit reframes on every change
  that alters the count, and no design anybody has authored in this tool — including every test
  fixture and the shipped example — goes past four elements. Still a drawing problem rather than a
  model problem, and still waiting for somebody to build a twenty-element narrowband match.
- **Q-15 — should a `.csmith` be able to reference a workspace cell as an overlay source?**
  *Re-filed, unchanged.* The two sources that were built — a document-relative Touchstone file and a
  cube in an open `DataSet` — cover every overlay the tool has been asked for, and a cell reference
  is a walk-up with its own refusals for a feature nobody has wanted yet.
- **Q-16 — when the network is mirrored, should the generator PANEL move too?** *Closed: no, as §5.5
  said, and the built window makes it a smaller question than it looked.* The panel is not the left
  end of a signal path: it is a **column of six settings cards** — generator table, import, chart
  Z₀ and design frequency, overlays, constant Q, swept band — of which only the first has anything to
  do with the generator. Moving a settings column because a drawing was flipped would be reading it as
  part of the schematic, which the figures show it is not. The splitter question the section raised
  therefore does not arise.

**Opened at closeout, closed 2026-09-19:**

- **Q-17 — is `conj. mismatch` the right quantity for the status strip?** *Closed: no, and the strip
  now reports the mismatch loss against Z₀_chart instead (owner decision).* §3.4 put the target glyphs
  at `Γ(conj(Z_gen(f)))` and the strip reported the mismatch against THEM, which is faithful to the
  note and answers a different question from the VSWR beside it. Driving the shipped example showed
  what that reads like in the hand: a two-element match taking 8 − j12 Ω to 50 Ω lands at
  49.98 − j0.10 Ω, VSWR 1.002, and the strip said **3.411 dB** — and the number got *smaller* at the
  band edges, where the match is worse (2.068 dB at 2.3 GHz, 4.924 dB at 2.6 GHz). A number labelled
  as a mismatch in decibels, in a column beside VSWR, reads as match quality; one that moves the other
  way is worse than no number at all.

  The strip's column is now `mismatch`, −10·log₁₀(1−|Γ|²) against the chart's own Z₀ — **the same Γ
  the VSWR beside it is made of**, so the strip answers one question in four spellings. It reads
  0.00 dB on the example and 0.17 dB at 2.3 GHz, moving with the VSWR and with the picture.
  `SmithReading.ConjugateMismatchDb` is `MismatchDb`, and `--json`'s `conjugateMismatchDb` is
  `mismatchDb`.

  **§3.4's target glyphs are UNCHANGED and this does not make them decorative.** Landing a frequency's
  load point on its own ⊕ is still the conjugate match to the generator; what it no longer has is a
  column, because the strip has no load impedance to state one against. The engine's own reading of
  the copied schematic — S11 against the complex port-1 impedance, which IS the conjugate match at the
  device's terminals — is −59.8 dB on the same network, and the example's README now puts the two side
  by side rather than warning about one of them. The third option considered, **giving the document a
  load impedance** so the conjugate target has something to be about, is a new field, a format change
  and a terminus the walk does not have: its own brief if it is ever wanted, not a closeout fix.

---

## 13. What this document deliberately does not decide

- The exact JSON property names in §7's sketch; those settle at implementation, as every other format's
  have, against the conventions the section names.
- Colours. This tool uses the active circuitRF theme through `RenderTheme`/`ThemeService` and introduces
  no palette of its own — harmonicaRF's phosphor-green theme exists because it is a standalone
  instrument, and this is a document.
- Keyboard shortcuts beyond the shell's own, which are the shell's to assign.
- Whether the `smith` verb should also emit the per-node impedances as a `DataSet`. It should probably be
  able to, and P3 is the place to decide it.

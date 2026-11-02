# Brief 86 — Thermal series follow-ups: the pulse from the exact Z_th, drawn wires' ground plane, the sweep slider, face folds

**Tag:** `R-em3d86-n` · **Series:** follows the thermal series (briefs 71–81) and its two review rounds (d96e7e90 and the
round that wrote this brief). Each part below is something the review found and could not settle with a local fix: it
needs a design decision, a document key, or GUI work.
**Area:** `src/Design/Thermal/ThermalRunService.SmallSignal.cs`, `src/Thermal/Frequency/` (part 1);
`src/Design/Thermal/ThermalRfCurrents.cs`, `src/Design/ThreeD/C3dThermal.cs`, `src/Cli/DocumentSchema.cs` (part 2);
`src/Ui/Viewer3D/`, `src/Ui/Views/ThreeD/` (part 3); `src/Ui/ThreeD/C3dEditorViewModel.FaceEdit.cs`,
`src/Design/ThreeD/Kernel/C3dFaceEdit.cs` (part 4); `examples/Thermal Channel vs Surface/`, `docs/user/src/reference/thermal.md`,
`tests/`
**Depends on:** 75 (plot temperature), 78 (RF shares), 80 (Rth, Z_th, pulse), 83 (field plots) · **Blocks:** —

The four parts are independent. Do them in any order; each has its own gate.

---

## 1. `R-em3d86-1` — a pulse train from the exact Z_th, not from a transfer Z_th's Foster fit

### What is wrong (measured)

Brief 80 answers a pulse train in closed form from **Foster fits** (R-em3d80-4b): each place's temperature is
Σⱼ Pⱼ × (the pulse response of the fit of Z_th(place, source j)). A **self** Z_th (a source's own) is a driving-point
impedance: positive real part, a sum of positive RC stages, and its fit is good (0.06–0.09 % on *Eight Fingers*).
A **transfer** Z_th (place ≠ source) is not. Heat takes time to cross from one finger to the next, and a delay makes the
real part go **negative** at high frequency. A Foster network's real part is Σ R/(1 + ω²τ²) > 0 at every frequency, so no
Foster network can represent it.

The review's new check (mutual fits, against their own DC value) measured this on the shipped *Eight Fingers* example
(`Zth:f1:f4`, fingers 120 µm apart): Re Z goes from 10.34 K/W at DC to −0.45 K/W near 10 kHz. The one-pass NNLS fit
collapsed to **one stage** (R = 10.34 K/W, τ = 1.59 s, the grid's longest), and it misses by **99.8 % of its DC value**. The
pulse temperatures at f1 and f4 are built partly from it. The run now warns about this. Before the review it said nothing,
and it still produces the number.

### What to build

For a **periodic** pulse train (the only kind brief 80 has), the response needs no fit at all:

  T(t) − T₀ = Σₖ Pₖ(t) ∗ z(t) = P̄·Z(0) + 2 Re Σ_{m≥1} Pₘ Z(m f₀) e^{j2πm f₀ t},  f₀ = 1/Period,

where Pₘ are the rectangular pulse's Fourier coefficients (duty D: Pₘ = P·sin(πmD)/(πm) · e^{−jπmD}). It needs Z at the
harmonics of 1/Period, which the Z_th sweep does not sample, so:

1. **Evaluate Z(m f₀) by solving**, not interpolating, for the harmonics that matter. `ThermalSmallSignal.Zth` already
   solves (K + jωC)·T = load at any frequency. Take harmonics up to where |Pₘ Z(m f₀)| falls below 1e-4 of the DC term,
   bounded by the setup's Zth band. Above that band, use the self fit's asymptote for a self term and zero for a transfer
   term, and state both in the notes.
2. **Or interpolate** log-log between the Z_th samples, if solving costs too much. That is the owner's choice (§5).
   Either way, the peak is found on a fine grid of t within the period, as `PulseTrain.Waveform` does now.
3. Keep the Foster **self** fits and their `.cnl` networks (R-em3d80-5). They are what a circuit uses, and they are good.
   The mutual fits stay as data. Only the pulse stops depending on them.
4. `Pulse:<place>:peak/single/avg` keep their names. "Single pulse" is not periodic: compute it from the self/transfer
   step response by inverse transform of the solved Z samples (the same data), or drop it if the owner agrees (§5).

With k(T) on, the review already anchored each point's average at its own average-power steady solve. That stays: the
harmonic sum carries only the ripple about it (the m ≥ 1 terms).

### Gate

- *Eight Fingers*: the pulse temperatures computed from the exact harmonic sum at f1 and f4. The mutual-fit warning is
  gone because the pulse no longer reads the mutual fit. The README's pulse numbers are re-measured, and
  `expected-numbers.json` records them.
- A closed-form check: a 1D slab (`testdata/thermal/slab`) with a known Z(jω), where the harmonic sum's peak matches the
  analytic periodic-pulse peak to 1e-4.
- A transfer-impedance case where the old Foster path was wrong: a source and a probe 3 diffusion lengths apart. The new
  peak matches a brute-force sum over 10⁴ solved harmonics.

## 2. `R-em3d86-2` — a ground plane for DRAWN wires' RF share

### What is wrong

Brief 78 shares an array's RF current among its wires by wBond's inductance reduction (`ArrayShare`). A `.wBond`'s arrays
keep **their design's ground plane** (the image method). Wires drawn in the `.c3d` are shared **in free space**, because a
`.c3d` states no ground reference. Over a ground plane, the image currents weaken mutual coupling at a distance and change
the share, most for the outer wires of a wide array. So the same wires give a different share depending on whether they
came from a `.wBond` or were drawn.

### What to build

A way for a `.c3d` to state the image plane its drawn wires see. Proposed (owner to confirm, §5):

- the thermal setup (or the document, beside `SymmetryPlanes`) gains `"WireGroundPlane": { "Z": <expr> }`, a horizontal
  plane in the view's frame, or `"WireGroundPlane": "<object>/<face>"` naming a horizontal conductor face;
- **inference is NOT offered.** "The nearest conductor below the pads" is exactly the guess em-3d.md §6.4 forbids for
  boundaries, and it would silently change a share when someone draws a lid;
- omitted, free space as today, and the notes keep saying so;
- `ArrayShare.FromCentrelines` already takes `groundPlane: bool`. It must take the plane's **height**, since the image
  sits at 2 z_plane − z. wBond's own designs place the plane at z = 0 of their frame.

### Gate

Three drawn wires over a stated plane give the same share, bit for bit, as the same three wires as a `.wBond` array with
its ground plane on. `check` refuses a plane above any wire's lowest point. The schema documents the key.

## 3. `R-em3d86-3` — the sweep slider (R-em3d75-4c, never built)

### What is wrong

R-em3d75-4c: "A **sweep slider** steps the `.pvd`'s steps, labelled with the sweep values." `Viewer3DViewModel` has the
state (`TemperatureStep`, `TemperatureStepMax`, `TemperatureStepLabel`, and `FieldSolutions` for every step), and the
probe table and the line plots follow `TemperatureStep`. But **no view binds it**. Round 6/7 removed the toolbar strip it
would have lived on (the thermal UI review found this). Today the only way to see another sweep point is a field plot's
*Solution* picker in the Inspector.

### What to build

- A slider (or a stepper with ◀ ▶ and the label) in the 3D view's field overlay, the same overlay that carries the
  legend. It is shown only when the drawn result has more than one step, and labelled `TemperatureStepLabel`
  (`Pdiss = 7 W`), never "step 2 of 3".
- For a two-axis sweep, one control per axis (the table's `PointLabel` already names both), and the step index is
  computed as the run's (last axis fastest).
- It moves the drawn plot's step exactly as the Inspector's picker does: through the plot's `Solution`, one undo
  entry, so a saved document reopens on the point last shown.
- *Fix range across sweep* keeps its meaning. The review made the union skip steps with no finite value (a runaway).

### Gate

A view-model test: a 3-point thermal run, the slider at 2, and the drawn values, the legend, the probe table and the hot
spot all read point 2. The label names the sweep variable with its unit. The gesture is one undo entry. It cannot be
checked by pixels from this machine's shell (Avalonia cannot start here), so say so in the report.

## 4. `R-em3d86-4` — thermal references follow a face fold

### What is wrong

A face edit that must fold a face to keep it planar renames it (`zmax` → two pieces). `FollowFolds` remaps the EM
`FaceBoundaries` in the same undo entry. It does not remap the thermal references to that face:

- each thermal setup's `Boundaries[].Face` (inside `Setups`, a JSON element per setup);
- a probe's `Face` and `Spot.Face`;
- a field plot's `Faces` (brief 83).

After the fold, the tint vanishes and `check` and the run refuse "no face", although the user edited nothing thermal.

### What to build

- One function in `src/Design` (beside `FollowFolds`): the document's thermal and plot references to `object/face`,
  rewritten for a fold. A boundary on a folded face becomes one boundary per piece with the same condition. A plot's face
  becomes its pieces.
- **A probe's `Face` is one face**, so it cannot become two. Either the probe becomes a `Face` probe over the pieces (a new
  list form of `Face`), or it keeps the first piece and the status line says so. Owner to choose (§5). A `Spot` stays on
  the piece that holds its centre, which is unambiguous.
- The face edit's undo entry carries these records too (`C3dEdit` today carries only `FaceBoundaries`).

### Gate

Fold a face that a FixedT boundary, a face probe and a field plot all name. All three follow in the same undo entry, and
undo restores all three. `check` stays clean before and after.

## 5. Decisions for the owner before building

1. Part 1: **solve** Z at the pulse harmonics (exact, costs one complex solve per harmonic per source), or **interpolate**
   the Z_th sweep's samples (free, error depends on PerDecade)? Recommendation: solve, bounded as §1 says. The
   interpolation error on a transfer Z_th near its sign change is exactly where the fit failed.
2. Part 1: is **single pulse** still wanted when the periodic answer no longer comes from a fit?
3. Part 2: the key's home (the setup or the document) and its spelling.
4. Part 4: a probe on a folded face: a list-form `Face`, or the first piece plus a sentence?

## 6. Scope

Not here: a transient solver (PRD non-goal); Foster fitting of transfer impedances by any other network form; inference
of a ground plane.

On completion, record findings in `src/Design/RESOLVED.md` / `src/Ui/RESOLVED.md` — not in any CLAUDE.md.

# Brief PAA-9 — the Phased Array tool window

**Series:** PAA-0 … PAA-10 · **Tag:** `R-paa9-<n>` · **Depends on:** PAA-5, PAA-8, PAA-8b (PAA-6, PAA-7 for their views) ·
**Design note:** §8
**Area:** new `src/Ui/PhasedArray/` and `src/Ui/Views/PhasedArray/` (the window, as the RF Chain Budget's and the Match
Designer's are top-level tool windows); new renderers in `src/Render/PhasedArray/`; `tools/UiSnapshot`; `src/Ui/RESOLVED.md`.

## The gap

At 1024 or 4096 elements a schematic shows one box. What a designer needs to see then is the aperture itself — which elements
carry what amplitude and phase, which are mismatched, which are switched off — and the beam in a space where grating
lobes and the scan range are visible, steered by hand. None of that is a Data Display plot of an analysis result: it is
an interactive view of one array's state.

## Requirements

**R-paa9-1 — A view of an instance, not a document (D7).** Tools ▸ Phased Array opens on the selected PhasedArray
instance (or asks which, when a schematic holds several). Every edit in the window writes that instance's parameters, or
the VARs its weights reference, through the schematic's own commands with undo, and the window follows edits made in the
schematic. *File ▸ New ▸ Phased Array* creates a cell whose schematic is a Port, a Feed-mode PhasedArray (record chosen
in a picker, 8×8 at half a wavelength of the record's centre frequency) and a `type=fields` analysis, through
`CellCreate`, and opens the window on it.

**R-paa9-2 — Aperture.** The element grid as built (rect or tri, `Off` hollow), each element coloured by a chosen
quantity: amplitude, quantised phase, |Γ_act|, delivered power, and — when the bench has them — a PA's Pout or PAE from
PAA-6 or a channel's NF from PAA-7. A legend with its scale. Click toggles an element in `Off`; drag selects a rectangle;
hover names `n`, row, column, position and the coloured value.

**R-paa9-3 — The (u,v) map.** The pattern's realized gain in direction-cosine space, `u = sinθcosφ`, `v = sinθsinφ`,
from PAA-2's padded FFT (R-paa2-2a) times the interpolated element pattern, so a 64×64 beam is resolved,
with the unit circle of real space, the grating-lobe positions for the current steer, and a scan-limit circle the user
sets. When the weights are a `steer()` or `steerDelay()` (R-paa4-3), **dragging the beam marker writes the VARs** of its
θ₀/φ₀ arguments (or the literals, when they are literals); when they are not, the marker is shown and not draggable, and
the tooltip says why.

**R-paa9-4 — Cuts and readouts.** Two principal cuts through the beam, polar or rectangular, co- and cross-polar, with
the L0 array factor of isotropic elements dimmed beneath. Readouts (R-paa2-7 and PAA-7): directivity, gain, realized
gain, EIRP or G/T (whichever the bench has), beamwidth in both planes, sidelobe level, pointing error, scan loss,
grating-lobe margin, worst |Γ_act|, power balance, and the level (D3) in words. No explanatory sentences under them.

**R-paa9-5 — Scan maps.** Realized gain and worst |Γ_act| over (θ₀,φ₀) out to the scan limit, computed in the background
through PAA-8's fast path and cancelled by any edit; a scan blindness shows as a dark ring on the gain map. Where the
fast path does not apply the tab says so instead of starting a sweep of full solves.

**R-paa9-6 — Tapers, bits, failures, frequency.** Taper family and level (writes `Amp`), phase bits and attenuator step;
a failures tab switching off a chosen fraction of elements at random over a chosen number of trials (in memory; nothing
written) and showing the spread of gain and sidelobe level; a frequency tab of pointing against frequency across the
record's frequencies, phase shifters against delays.

**R-paa9-7 — It keeps up.** Every interaction that changes only weights goes through PAA-8's fast path (and PAA-8b's
re-weigh, so it does not elaborate) and redraws the aperture, map, cuts and readouts together; anything else follows PAA-8's
"estimated" rule. The window's frame — evaluation and drawing — is measured at 1024 and 4096 elements during a beam drag and
recorded in `src/Ui/RESOLVED.md` (no timing assertion); the target is 20 frames a second.

**R-paa9-8 — Drawn by `src/Render`.** The aperture and (u,v) renderers are in `src/Render/PhasedArray`, framework-free, so
UiSnapshot and DocGen draw them with the window's code.

## Gates

1. UiSnapshot PNGs, looked at: the window on a 32×32 array steered to 30° with a Taylor taper; a 64×64 array, with
   the main beam visibly resolved in the cuts and the map; the same with 10 elements
   off; the (u,v) map at a pitch that admits a grating lobe; the aperture coloured by |Γ_act| and by PA PAE (on PAA-6's
   bench).
2. A beam drag from boresight to (30°, 0°) writes `theta0 = 30` to the VAR the `steer()` names, as one undo step.
3. An element clicked off appears in `Off`, the schematic's symbol draws it hollow, and undo restores both.
4. *File ▸ New ▸ Phased Array* creates a cell that `check` passes and that simulates.
5. The background scan map is cancelled by an edit (a counter of cancelled jobs) and never starts on a non-fast-path bench. A scan
   of N directions at 4096 elements makes at most one elaboration and one closure factorisation per frequency
   (`PreparedCircuit.Elaborations`, `PhasedArrayFastPath.ClosureFactorisations`).
6. **A drag costs what PAA-8 and PAA-8b say — counters, not timing.** A 20-step beam drag on a 4096-element array with an identical
   channel makes no elaboration and 20 re-weighs (`PreparedCircuit.Elaborations`, `Reweighs`), no closure factorisation after the
   first and 20 back-substitutions (`PhasedArrayFastPath`), and builds no new phase tables for the cuts, the map or the overview
   (`ArrayPattern.TablesBuilt`). R-paa9-7's frame measurement is recorded, never asserted.

## Not here

A second document format; editing the channel cell from the window (it opens the cell's schematic instead).

## Completion note (2026-11-14)

Built:

- **The window:** `PhasedArrayWindow` with tabs Beam (aperture, (u,v) map, cuts, readouts), Scan maps, Tapers and bits, Failures,
  Frequency. Tools ▸ Phased Array and File ▸ New ▸ Phased Array are on both menu surfaces.
- **The view model and session** (framework-free, `src/Ui/PhasedArray`): `PhasedArrayToolViewModel`, `PhasedArraySession`,
  `PhasedArraySteering`, `PhasedArrayFrames`.
- **The renderers** (`src/Render/PhasedArray/ArrayRenderers.cs`): aperture, (u,v) map, scan map, cuts, chart.
- **The run's state:** `ArrayFieldState` on `RunResult.ArrayStates`.
- **The cell:** `CellCreate.PhasedArray`.
- **Snapshots:** UiSnapshot `--phased-array` (with `--pa-color`, `--pa-tab`, `--pa-drag`). The benches are in
  `testdata/phased-array/tool/`.

Findings and the frame table are in `src/Ui/RESOLVED.md` § brief-paa-9; the engine side is in `src/Engine/RESOLVED.md`.

- **Gate 1:** UiSnapshot PNGs looked at:
  - `Taylor 32` (−30 dB, 30°): sidelobes −29.96 dB, pointing error 0.007°;
  - `Uniform 64`: 1.65° × 1.56° beam, resolved in the cuts and the map;
  - `Uniform 64 Off` (ten off, drawn hollow);
  - `Grating` (0.79 λ, the lobe at u ≈ −0.97 in red, margin −14.2°);
  - `Taylor 32` coloured by |Γ_act| (edges highest);
  - `PA Elements` coloured by PA PAE (53-58 %);
  - the scan maps, failures, frequency and tapers tabs.
- **Gate 2:** a drag from boresight to 30° writes `theta0 = 30 deg` as one undo step, and undo restores it. A steer() whose θ₀ is an
  expression does not drag, and says why.
- **Gate 3:** a click writes `Off=10`, the symbol's glyph has 10 in its off set, the window follows, and undo restores both.
- **Gate 4:** the new cell passes `check` (exit 0) and simulates, giving an 8 × 8 at λ/2.
- **Gate 5:**
  - nine directions at 4096 with a channel: 1 elaboration, 9 re-weighs, 0 factorisations after the first frame;
  - a cancelled map counts one cancellation, and an edit in the schematic cancels the running map;
  - a channel reading `n` is refused before the map starts.
- **Gate 6:** a 20-step drag at 4096 with a channel: 0 elaborations, 20 re-weighs, 0 factorisations, 20 back-substitutions,
  0 phase tables.
- **R-paa9-7:** the frame is ~25 ms at 1024 and ~65 ms at 4096 in Release, ~48 ms and ~140 ms in Debug. That meets 20 a second at
  1024, not at 4096.

Deviations and things to know:

- **Directivity and the power balance integrate the (u,v) map**, not the record's grid. On the 5° fixtures the grid read a 64 × 64
  array's directivity 4 dB low, below its gain. The run's own `farfield.DirectivityDbi` and `PowerBalance(F1)` still use the grid,
  so on a coarse record they disagree with the window.
- **The map's element pattern is the centre element's.** The cuts and readouts sum every class.
- **Failures zero the run's waves in memory** and do not solve the circuit again.
- **The frequency tab** sums the run's weights over the record's frequencies when it holds three or more. The fixtures hold one,
  so there it shows the array factor alone over ±20 %.
- **PA Pout, PAE and channel NF** are read from measurements named for an element (`Pout3_dBm`, `PAE3`, `NF3`).
- **The scan map** steps 5° in θ₀ and 15° in φ₀ out to the scan limit. It starts when its tab is shown and starts again after an
  edit cancels it.
- **EIRP** is the realized gain plus the drive level the run's own published EIRP implies. G/T is shown instead when the bench has a
  `GoverTDbK(…)` measurement.
- **Buttons are square glyph buttons**, as on the `.csch` and `.c3d` toolbars, with their words in the tooltip. **Help (?)** opens the
  Phased Antenna Array section of the component reference (`reference/components.html#phasedarray`) until PAA-10 gives the tool a
  page of its own; repoint `PhasedArrayWindow.OnHelpClick` then.
- **Not done:** DocGen figures (PAA-10).

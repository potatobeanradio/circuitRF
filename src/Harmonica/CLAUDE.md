# harmonicaRF — the framework-free half

Standing instructions for `src/Harmonica`. Read with the root `CLAUDE.md` and
`docs/design/harmonicarf.md`. Tests are `tests/Harmonica.Tests`.

**This project references no UI framework and never may.** It is in the
`tests/Firewall.Tests` assertion alongside `RfCore`, `Core`, `Engine` and `Cli`. The standalone
harmonicaRF binary (H8) does not weaken that: it is `src/Ui` with a different `Main`.

## harmonicaRF NEVER FILLS ITS CONTOURS — owner ruling, 2026-08-06

**Iso-lines only. There is no fill, there is no fill setting, and there is not going to be one.**
§7.2's "contours are unfilled" is not a default that a preference may later flip — it is the whole
behaviour. Do not add a `ShowFill`, do not surface `ContourFillType` on a harmonicaRF panel, and do
not spend a measurement on it.

This is enforced structurally rather than by discipline: `HarmonicaPanelRenderer` draws contours by
stroking polylines and has no fill path at all — it never constructs a `ContourData`, so
`DrawTopoMapFill` / `DrawHeatMapFill` are unreachable from harmonicaRF. `HarmonicaPanelTests`
asserts that (`SmithPanel_NeverFills_...`). If you find yourself needing `ContourData` on a
harmonicaRF panel, stop — that is the seam this ruling closes.

*Two consequences worth keeping.* (1) The 73 ms fill cost measured during M1 is **retired, not
carried forward** — it described a code path harmonicaRF cannot reach, and quoting it later would
imply a decision is still open. (2) R-h45-5's own warning about `FillGrid`/`DrawTopoMapFill` painting
across the support mask into a hole **cannot bite harmonicaRF**, because nothing here fills. The
hole is guarded by the support mask on the ISO-LINE side alone, which is where Tier 8's pixel oracle
looks.

## Brief H8 (brief-harmonicarf-h8-standalone-binary-and-set-dut, 2026-08-07) — **COMPLETE (M1–M5)**

Read `src/Ui/CLAUDE.md`'s own H8 entry for the dialog, the standalone binary, the packaging and the
four hooks. What lands in THIS project is two things, and one of them is a real bug.

- **`IntrinsicPortMap.cs` (new) — R-h8-3's "asked for, never guessed", as a TYPE.** H4–H7 persisted
  `DutSpec.IntrinsicMapping` and read it **nowhere** — collecting a mapping and then ignoring it is the
  same unwired-hook debt this phase pays. `IntrinsicPortMap.For(dut, model, package)` resolves the
  gate/drain/source **port indices** (an external model's node layout is `[n₀, 0, n₁, 0, …]`, so port
  index == node index) and `HarmonicaContext.IntrinsicPorts` carries them into `IntrinsicPlane` /
  `HarmonicaDataSet`. A two-port DUT needs no mapping (`IntrinsicPortMap.TwoPort`); an external one
  with none draws the intrinsic panels **empty**, with `NoMappingMessage` naming *File ▸ Set DUT…* as
  the fix rather than a defaulted (0, 1, 2) that would be a plausible wrong answer.
  - **The two sides fail INDEPENDENTLY**, which is the part worth keeping. A resolved mapping still
    **refuses the source side by name** when the package states a source lead (`Rs`/`Ls` ≠ 0): §4.5.3's
    `J′` route reads the model's own gate port, which is referenced to **ground** rather than to the
    lifted source terminal, so the number would be wrong in exactly the way nothing on screen shows.
    The load side is unaffected and still drawn. `LoadUnavailable`/`SourceUnavailable` are separate
    strings for that reason; `Reason` is only a convenience.
  - `IntrinsicPlane.Evaluate(…, sourcePort)` re-references the **REPORTED** port voltages after
    evaluation — never the ones the device is evaluated at. Evaluating at shifted voltages would be a
    different device.

- **THE REAL BUG THIS PHASE FOUND, and it could not have existed before H8.** `CharmIo.ToDocument`
  wrote `ModelFile = m.Dut.Provider` — but a provider is `VerilogA|<abs path>` for the file route (a
  composed **name**, not a path) and a bare **kit name** for the kit route. Neither is a path, so the
  reader's existence check resolved a nonsense concatenation and would have reported a **present** file
  as missing, and **every kit-backed `.charm` as missing**. Fixed to
  `VerilogAFileResolver.ModelFileIn(p)`, null for kits. H4–H7 could not have met it because nothing
  could create an external DUT until Set DUT existed — the field was only ever written for a DUT kind
  that never occurred.

## Brief H7 (brief-harmonicarf-h7-edit-display-interchange-and-colour, 2026-08-07) — **COMPLETE (M1–M5)**

Read `src/Ui/CLAUDE.md`'s own H7 entry for the menus, the picker, Edit Display, the interchange and
the colour editor. What lands in THIS project is small and additive.

- **`CharmIo.CharmTrace` and the `Traces` block (R-h7-7).** A picked trace is `(Spec, PanelId,
  Label)` — **plain strings, deliberately unparsed here**. The spec's grammar belongs to
  `CubeTraceSpecParser`, which is `src/Ui`; storing the string means a spec that no longer resolves
  survives a round trip and is reported *on the panel* rather than dropped at load, the same courtesy
  an unresolved model reference already gets. **No traces ⇒ no block**, so an untouched `.charm` still
  re-serialises byte-for-byte — the third field to follow that rule after appearance and layout.

- **`ContourGrid.Build(…, reuseUnchanged:)` — R-h7-12's single-point invalidation, and the KEY is the
  interesting part.** A dragged Γ point leaves every other sample at the identical Γ against
  identical terminations, so its `PinSearch` answer is bit-identical to re-solving. Reuse is keyed on
  the **Γ value**, not an index (an imported `.gam` may reorder the set), and guarded by
  `_reusableAgainst` — the structural key, the bias, the drive, the compression window, the side and
  band being swept, **and every OTHER band's termination**. The band the grid *sweeps* is deliberately
  excluded: it is overwritten per point and says nothing about what a held point was solved at.
  **OFF by default**, because every other caller builds a grid the previous one has nothing to do
  with. `ReusedPointCount` is the counter.
  - **Measured** (`HarmonicaGridDragCostTests`, `Category=Benchmark`, best-of-N, alone; 61-point grid
    on the default device plus an Rd/Rs/Ls package): a full rebuild is **272 HB solves / 547.8 ms**;
    one dragged point is **3 solves / 3.3 ms with 60 points reused** — **90.7× fewer solves, 165×
    less wall**. §6.4's own estimate was "~8 solves ≈ 8 ms"; it is 3 and 3.3, because the moved point
    warm-starts from its Γ-nearest already-converged neighbour, which is now always adjacent.
  - **The factorization is dropped anyway**, and R-h7-11 says so outright: the node SET moved, which
    is `Rbf2D.Factored`'s own cache key. Gated by a counter, not by a comment.

- **`FrameScheduler.SetGridPreset(rings, spokes)` — §7.6's Grid menu, overriding the ladder's TOP
  rung only.** The coarse rung stays 3 × 12 unless the preset is coarser, in which case the two
  collapse: degrading a grid the user deliberately made small takes away the thing they asked for and
  there is nothing below it worth having.

## Brief H6 (brief-harmonicarf-h6-inverse-solve-and-drag, 2026-08-07) — **COMPLETE (M1–M3)**

Read `src/Ui/CLAUDE.md`'s own H6 entry for the gesture, the panels and the wiring. What lands in THIS
project: `InverseSolve.cs` and `Reachability.cs`, plus one refactor of `HarmonicaDataSet`.

- **`HarmonicaDataSet.Intrinsic` (new) — §4.5's definitions now have ONE call site, and that is why
  the refactor exists.** `Build` used to derive `Z_intr`/`Gamma_intr` inline; the inverse solve needs
  the same numbers a few thousand times a drag. Copying the derivation would have put §4.5.2's
  ratio error one careless edit away from a second home, so `Build` and `InverseSolver` now both call
  the same method. `includeSource: false` skips the §4.5.3 `J′` route entirely and leaves the source
  row NaN — the Schur route is the expensive half of an intrinsic evaluation and a residual with no
  source-side target should not pay for it. `GammaOf`/`ImpedanceOf` moved here too, so the Γ ↔ Z
  convention (and the Γ = 1 nudge) is written down once.

- **§6.6'S OWN COST ESTIMATES ARE CLOSE ON TWO OF THREE AND WRONG BY 3× ON THE THIRD.** Measured
  (`InverseSolveCostTests`, `Category=Benchmark`, best-of-5, alone, on Hero 2's device plus an
  Rs/Ls/Rd package):

  | unknowns | FD at start | per-frame Broyden | solves/frame | full FD every frame |
  |---|---|---|---|---|
  | 1 band (2×2) | 3.2 ms | 1.75 ms | 2.00 | 4.0 ms |
  | 2 bands (4×4) | 4.3 ms | 1.56 ms | 2.00 | 5.8 ms |
  | **4 bands (8×8)** | **10.3 ms** | **2.45 ms** | **2.00** | **12.9 ms** |

  FD-at-start (§6.6 says 9 ms) and per-frame Broyden (says 2 ms) both land. **"Rebuilding the FD
  Jacobian every frame would cost ~30–40 ms and cap the drag at ~25 fps" does not** — it is 12.9 ms,
  and would sit inside a 33 ms budget. The reason is stated rather than hand-waved: a per-frame
  rebuild WARM-STARTS from the previous frame's converged spectrum, while the 10.3 ms figure is a
  cold start. **Broyden is still right and is still 5.2× cheaper — but it is not the difference
  between 30 fps and not.**

- **OPEN ITEM 8 IS ANSWERED NO, AND IT IS A MEASUREMENT.** The source side does not need its own
  FD-refresh cadence. Over a 60-frame CURVED drag (a straight one never asks the Jacobian to turn,
  which is the case Broyden handles best):

  | case | converged | solves/frame | FD refreshes | ms/frame |
  |---|---|---|---|---|
  | load only, stall-driven | 60/60 | 2.00 | **0** | 1.43 |
  | source only, stall-driven | 60/60 | 2.00 | **0** | 2.15 |
  | source only, forced every 8 | 60/60 | 2.23 | 7 | 2.44 |
  | both sides, stall-driven | 60/60 | 2.00 | **0** | 2.21 |

  The stall-driven refresh **never fired**, on either side. Forcing one costs +14% wall and +11%
  solves for no accuracy anyone can measure. `InverseSolveOptions.SourceFdRefreshEveryFrames` exists
  and **defaults to 0** — kept because a device with stronger feedback might yet need it, and the
  knob is one line.

- **THE ONE CASE THAT IS ILL-POSED RATHER THAN MERELY UNUSUAL: an ACTIVE FUNDAMENTAL SOURCE.** R-h6-10
  says an out-of-circle extrinsic solution is allowed and flagged, and for the load side and for every
  source harmonic above the fundamental that is exactly what happens. But `HarmonicaContext.DriveVolts`
  is `√(8·P_avl·Re Z_S(1))` — **available power is undefined against a source with `Re Z ≤ 0`**, and
  the shipped code quietly returns 0 V there. A residual evaluated against no drive converges to the
  quiescent point and means nothing. So the solver refuses that candidate BY NAME
  (`InverseFailure.ActiveSourceFundamental`) rather than solving it. This is narrow and deliberate:
  `S1` only, never the load side, never `S2…`.

- **R-h6-9 IS ENFORCED BY SNAPSHOT, NOT BY A BRANCH.** A failed `Step` restores the working vector,
  the Jacobian AND the warm start, and returns the UNCHANGED Γ vector — so a caller applying the
  result is a no-op by construction rather than by remembering to check `Converged`. The Broyden
  updates a failed attempt made are discarded with it: they describe a point the solve did not stay at.

- **REACHABILITY IS THE IMAGE OF THE EXTRINSIC BOUNDARY CIRCLE, NOT A FILLED LATTICE**, which is 24
  solves instead of a few hundred for the same shape. That is only legitimate if the map does not
  fold, so `ReachableRegion.Interior` carries a handful of interior forward samples whose ONLY purpose
  is to be checked against the polygon — and they are (`ReachabilityTests`, 6/6 inside). Measured at
  the shipping density: **12 samples 21.5 ms · 24 samples 52.4 ms · 48 samples 110.4 ms**, and the
  area is 2.617 / 2.702 / 2.723 Γ² — i.e. 24 is within 1% of 48 for half the cost. **A lossy embedding
  reaches 80.8% of a lossless one's area** (4 Ω series Rd + 1 Ω Rs), measured, not asserted.
  A boundary sample that does not converge is **DROPPED, not substituted** — 1 of 24 on the shipping
  fixture — because interpolating across a gap would shade somewhere nothing was measured.

- **THE CACHE KEY DELIBERATELY EXCLUDES THE TERMINATIONS, and that is the design note's rule rather
  than an oversight.** §6.6 says "refreshed on structural change"; strictly the region moves as the
  other marked bands move, and during an inverse drag they do. Recomputing per frame is 24 solves a
  frame — the entire tier-A budget spent on shading. `Reachability.Key` is
  `(StructuralKey, side, band, PavlDbm)`, so a **bias** change does not move it either; that is
  recorded in `ReachabilityTests.TheCacheKey_...` as a fact a reader should find rather than discover.

## Brief H4–H5 (brief-harmonicarf-h4-h5-panels-and-frame-scheduler, 2026-08-06) — **COMPLETE (M0–M6)**

Read `src/Ui/CLAUDE.md`'s own H4–H5 entry for the Ui half. What lands in THIS project:

- **§4.5.3(a)'s SIGN ERROR IS FIXED IN THE DESIGN NOTE** (M0, owner-approved). `harmonicarf.md` now
  reads `Z_seen = (Zs + Z_Ls)/(1 + gm·Z_Ls)`, with the KCL that forces it written out and a dated
  note recording why. **The code was already right and is untouched** — this closed the
  documentation half of the discrepancy H0–H3 reported.

- **`CharmAppearance` (new) — R-h45-12's appearance block.** Both variants' resolved `Harmonica.*`
  role maps as `role → "r,g,b,a"`, plus §7.2's `α_floor` / `p` and the label toggle. **The role
  VOCABULARY is deliberately not here**: `ColorRole` lives in `src/Ui/Theming`, on the far side of
  the wall, so this project stores plain data and `HarmonicaAppearanceBridge` (Ui) owns the mapping.
  `TryDecode` REFUSES a malformed colour rather than yielding black — a colour silently read as
  black surfaces much later as a rendering bug.

- **`CharmLayout` (new) — R-h45-1's §7.1 layout as DATA, in fractions.** The reason it is here in M3
  rather than in H7: if the four panels were positioned by a hand-written AXAML grid, H7's Edit
  Display would have to REPLACE the layout mechanism to make it editable, and every `.charm` written
  before then would carry no placement at all. A degenerate placement (zero width/height) is
  DROPPED on read, not honoured — an invisible panel with nothing on screen to say why is worse than
  falling back to §7.1's own default for that one panel.

- **`CharmIo.ReadAll` / `CharmContents` — one parse, four answers.** Model, markers, appearance and
  layout. Every narrower `Read` overload now delegates here, so the document shape is interpreted in
  exactly one place. **An untouched document writes NEITHER new block**, so a `.charm` nobody has
  recoloured or rearranged re-serialises byte-for-byte — pinned by a test.

- **R5's raster measurement (`RasterCostTests`, `Category=Benchmark`, ~2.5 s).** On a real 61-point
  grid: **`Raster` 8.14 ms at 96×96 and 55.17 ms at 256×256 — a 7.0× ratio**, inside §0.3 item 3's
  predicted 6–8× band, so **D5's coarse/full switch is worth building**. `Contours` (raster + 10
  levels + extract) is 10.89 / 72.25 ms. **The finding worth keeping: within that cost the RASTER is
  ~76%, not the marching squares** — §0.3 item 3's "extract" figures are raster-plus-extract, and the
  thing to optimise if it ever matters is the per-cell RBF evaluate and support-mask test
  (`O(cells × points)`), not `ContourExtractor`.

## Brief H0–H3 (brief-harmonicarf-h0-h3-headless-engine, 2026-08-06) — **COMPLETE (M1–M6)**

The ten things worth knowing from out here.

- **THE DELIVERABLE — Tier 0 passes at 1.4e-16, and the design note's closed form has a SIGN ERROR.**
  `harmonicarf.md` §4.5.3(a) gives `Z_seen = (Zs + Z_Ls)/(1 − gm·Z_Ls)`; under circuitRF's own
  passive sign convention it is `(Zs + Z_Ls)/(1 + gm·Z_Ls)`. `I[p]` is the current INTO the device at
  the port's + terminal and OUT of its −, so port 2 = (drain, source) delivers `Ids` into node s′
  *from the device* and KCL there reads `Ids = It + V_s/Z_Ls`. Two independent checks agree with the
  `+` form: the degenerate case `Zs = 0`, `Z_Ls = R` gives `R/(1 + gm·R) → 1/gm`, which is the
  source-follower output impedance and is what looking OUT of a degenerated gate–source port must
  give (the note's form is NEGATIVE for `gm·R > 1`, which a passive degeneration cannot produce);
  and numerically the `+` form matches to 1e-16 across three `Ls` values and every harmonic while
  the `−` form is out by a factor of two. **The physics in the note is right and only the sign is
  wrong** — the `gm` term's presence, which is the whole point of §4.5.3(a), is what the fixture's own
  guard checks (it is 0.35–0.88 away from the no-feedback answer).

- **THE PORT IS A PORT, NOT A NODE, and §4.5.3 does not say so.** It writes the gate as one unknown
  `g`. It is not one, the moment a source lead lifts the source terminal off ground — and that is the
  very case the formulation exists for. So the gate enters through its incidence vector (+1 at the
  gate node, −1 at the source node) and `Z_S = bᵀ J′⁻¹ b`, which collapses to `(J′⁻¹)_gg` exactly
  when the source is grounded. Removing the gate-port SELF block also has to be done in PORT space:
  the engine's node-space `G`/`C` arrays have already summed every port pair together, so the
  `(gate,gate)` entry of `dg`/`dc` is recomputed and un-accumulated four-way.

- **§6.2's Schur formula is ALGEBRAICALLY RIGHT AND NUMERICALLY WRONG, and the ORACLE is the side
  that loses.** Closing in the admittance domain reproduced direct extraction to only 1e-4…1e-7
  against R-hrf-6's 1e-12. Three separate causes, all in the REFERENCE: **gmin is counted per NODE**
  and the stamped netlist has two nodes the closure's does not (the far side of each DC block), so
  the two differ by exactly 1e-12 S — nothing absolute, 14% relative on a near-open termination;
  **the ideal 1 F block is 1.26e10 S at 2 GHz** and stamping it next to a termination's 0.04 S spends
  eleven digits of the MNA's condition number; **the ideal 1 H choke is 12.6 GΩ** and forming its
  parallel combination numerically annihilates the small reactive part of the answer. Against a
  hand-derived `1/(jωL) + 1/(Z + 1/(jωC))` the CLOSURE is exact (1.9e-16) and the stamped netlist is
  2.3e-5 out. **So the closure is done in the IMPEDANCE domain** — `Y_NN = ([M⁻¹Z]_PP)⁻¹` with
  `M = I + Z·Y_t` — which leaves exactly one inversion, of the TERMINATED block, conditioned exactly
  as well as the shipped path's. Measured: **≤ 2.1e-16 against the closed form at every Z regime**,
  1.6e-13…6.6e-13 against the stamped reference.

- **THE DC BLOCK IS PART OF THE TERMINATION, NOT OF THE NETLIST**, and that is load-bearing rather
  than tidy. An explicit blocking capacitor leaves the termination plane floating at ω = 0 — an
  all-but-singular row in the one extraction the whole scheme rests on. Folding it into `Y_t` makes
  band 0 an EXACT open (`Y_t(0) = 0`, not a large impedance) and leaves the plane an ordinary,
  well-connected node at every harmonic. `BiasChokeHenries` and `DcBlockFarads` are settings for the
  same reason the finding above exists: a comparison against a stamped netlist has to be made on a
  fixture where the stamped netlist is itself accurate.

- **KCL NEEDS THE TOTAL NONLINEAR INJECTION AND D1 NEEDS THE CONDUCTION HALF; THEY ARE NOT
  INTERCHANGEABLE.** `OperatingPoint.INl` is `i` alone (the intrinsic quantity, by D1) and
  `INlTotal` is `i + jωq + Σ H[w]·W` (what the HB residual balances). Recovering a termination-plane
  voltage with the conduction half was a real defect — it put `Zin` at 249 + j241 Ω where the closed
  form says 11.24 − j32.89 — and it was caught by the closed-form check, not by inspection.

- **`Zin` IS THE DELIVERED CURRENT AND IT NEEDS NO BACK-SOLVE HERE.** The termination is a Norton
  pair `(J, Y_t)`, so what it pushes into the plane is exactly `J − Y_t·V_plane`, in closed form. The
  regression fixture puts a real gate lead between the plane and the device: the delivered-current
  answer is 11.24 − j32.89 Ω and the device's-own-current answer is **300 Ω exactly** — the same 27×
  error class as the shipped loadpull `Zin` bug, pinned so it cannot come back.

- **AN SDD LINE'S MULTIPLIER MUST COME BEFORE ITS EQUATIONS.** Equations are delimited by the next
  `I[p,w]=`-style header at bracket depth zero, so a trailing `m=2` is swallowed into the last
  equation's text and fails to parse. Found by the test, not by review. Related: the SDD charge
  spelling is `Q[p]` (single index) or `I[p,1]` — **`Q[p,w]` is silently ignored**, which reads as a
  device with no charge at all.

- **THE SECANT COSTS 4.6 SOLVES PER Γ POINT against the ladder's ~30, and the saving is in the
  BRACKET rather than in the secant.** The first implementation climbed in uniform 3 dB strides and
  spent 12 of its 14 solves getting from `PinStart` to the compression region — the batch engine's
  ladder wearing a different name. Two changes fixed it: strides that DOUBLE, and a hint from the
  Γ-nearest neighbour that already compressed, so only the first grid point has to find the region at
  all. Measured on Hero 2's own device over a 61-point ring grid: **280 solves, 4.6 per point**,
  better than §6.3's own ~8 estimate.

- **THE FIT DOES NOT DOMINATE; THE RASTER DOES.** §6.4.1 obliges a separate report and names
  Delaunay/natural-neighbour as the fallback if the fit is the problem. Measured at n = 37/61/200: the
  fit is **0.029 / 0.078 / 0.960 ms** (and **0.008 / 0.043 / 0.044 ms** for a second metric off the
  cached factor), while the EXTRACT is **1.3 / 10.1 / 18.2 ms** at 96 × 96 and **7.7 / 58.3 /
  112.9 ms** at 256 × 256. So the fallback is not needed and should not be reached for; what
  §6.4.1's list actually buys is its item 2, the two raster resolutions, worth 6–8×. On the real
  61-point grid the split is **SOLVE 0.804 s, FIT 2.87 ms for two metrics, EXTRACT 67.9 ms** — the
  solves are 92% of it, which is the number a frame scheduler should be sized against.

- **TIER 3 AGREES WITH THE SHIPPED PATH TO 6.7e-5 dB.** The reference is a genuinely different route
  — a `Tuner`-based netlist with the terminations stamped and the drive owned by the source tuner,
  extracted by `HbLinearExtractor` — and both sides read their FOMs through the SAME
  `LoadpullEngine.ComputeFoms`, so the comparison is of two solves rather than two formulas. Pout, Gt
  and Gp to 6.7e-5 dB across three drive levels; DE and PAE to 1.6e-5 relative; Pdc to 1.9e-10; the
  intrinsic spectra to 4.4e-8 of their own scale.

- **AN ENTRY-WISE RELATIVE ERROR IS THE WRONG MEASURE FOR A SPECTRUM.** The gate current in that
  fixture is exactly linear, so its 2nd…5th harmonic bins are identically zero and come back as FFT
  round-off around 1e-18. Entry-wise, 1.1e-18 against 0 is a relative difference of 1.0 — a number
  that says nothing. The comparison is normalised by each port's own largest bin, WITH a guard that
  asserts the forgiven bins really are round-off on both sides, so the normalisation cannot hide a
  real signal.

**NOT BUILT, and the honest list.** H4–H8 in full, as the brief scopes them: the document shell and
the four panels, the frame scheduler and its concurrency, the inverse solve, Edit Display, the colour
theme and the standalone binary. Within this brief, §6.4.1's items 3–5 (freezing the level set during
a drag, suppressing labels, pooling the raster buffers) belong with the scheduler at H5 and are not
here. `LoadpullEngine`, `PursuitEngine` and every hero golden are untouched, and `Rbf2D`'s existing
constructor is byte-identical — R-hrf-9 is purely additive.

**Gate.** `tests/Harmonica.Tests` **49 routine tests in ~0.2 s**, plus **6 methods tagged
`Category=Benchmark`, ~8 s** opt-in via `--settings circuitrf.benchmark.runsettings`. Every timing
class is in the non-parallel `HarmonicaBenchmarks` collection AND takes a best-of-N, because once
there were six of them they contended enough to INVERT the batched/unbatched comparison (7.58 ms
against 6.39 ms, where alone they are 0.49 and 1.34). Elsewhere: `Engine.Tests` 1,004 + 1 skip,
`Core.Tests` 1,118, `RfCore.Tests` 281, `Ui.Tests` 5,075, `Firewall.Tests` 5 — all green.

## The layout

| file | what |
|---|---|
| `CircuitModel.cs` | the value object: DUT, embedding stack, terminations, bias, settings. `StructuralKey` is what decides a rebuild. |
| `HarmonicaNetlist.cs` | `CircuitModel` → the OPEN-port `.cnl` text. The terminations are deliberately not in it. |
| `InterfaceNetwork.cs` | R-hrf-6: the once-per-structure extraction, and the per-frame closure. |
| `HarmonicaContext.cs` | R-hrf-5: netlist ownership, the structural/value boundary, `Solve`. |
| `IntrinsicPlane.cs` | R-hrf-1/2/3: the conduction split, `Z_L` by ratio, `Z_S` by the `J′` route. |
| `HarmonicaDataSet.cs` | R-hrf-10: the published cubes. |
| `CharmIo.cs` | R-hrf-11: `.charm`, setup only. |
| `TouchstoneCoverage.cs` | R-hrf-12: the refusal. |
| `PinSearch.cs` | R-hrf-7 / D4: the tickle, the doubling bracket and the secant. |
| `ContourGrid.cs` | R-hrf-8/9 / D5/D6: the grid, holes, the support mask, MXP/MXE, the factor cache. |
| `CharmAppearance.cs` | R-h45-12: both variants' role maps + the §7.2 fade parameters, as plain data. |
| `CharmLayout.cs` | R-h45-1: the §7.1 panel placement, in fractions. Locked by default. |
| `InverseSolve.cs` | R-h6-6/7/8/9: the simultaneous inverse solve, its FD/Broyden Jacobian, and the refusals. |
| `Reachability.cs` | R-h6-12: the image of the extrinsic boundary circle, its area, and the cache key. |

H7 adds no file here — `CharmIo` gained the `Traces` block, `ContourGrid` gained single-point reuse,
and `FrameScheduler` gained the Grid-menu preset.

## Rules

- **Never re-elaborate on a value change.** A termination is closed algebraically; bias mutates
  `VdcModel.VdcOverride` in place. Going through a global-variable override is ~1000× the cost of the
  thing being changed and is forbidden.
- **The intrinsic current is the CONDUCTION half.** Both for the loadline and for the glyphs. The
  consequence is intended: with charge on, the load glyph separates from its marker even with a bare
  device.
- **A ratio is legitimate at the drain and WRONG at the gate.** See `IntrinsicPlane`'s own remarks
  and `src/Engine/Loadpull/CLAUDE.md`.
- **No static mutable state.** H5 gives each worker its own context; nothing here may be shared.
- **The inverse solve takes its `HarmonicaContext` PER CALL, never as a field.** The Jacobian is plain
  data that belongs to the gesture; a context belongs to a `SolveWorker` and is not thread-safe.
  Keeping them apart is what lets an inverse drag run on the solve pool at all.
- **Nothing but a CONVERGED inverse solve may write a termination.** R-h6-9. A glyph that lands
  somewhere the solver did not actually reach is worse than one that sticks.

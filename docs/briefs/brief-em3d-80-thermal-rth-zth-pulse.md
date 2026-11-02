# Brief 80 — the Rth matrix, Z_th(jω) and radar pulses

**Series:** [3D thermal, fifth series](brief-em3d-71-thermal-overview.md) · **Tag:** `R-em3d80-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §9.3; overview §1f, D10
**Area:** `src/Thermal/Frequency/`, `src/Thermal/Solvers/` (complex path), `src/Design/Thermal/`,
`src/Design/Layout/Em/EmSetupModel.cs` (`Thermal.Rth`, `Thermal.Zth`, `Thermal.Pulse`), `src/Ui/ThreeD/`
(the Setups page's groups, a Z_th plot), `tests/`
**Depends on:** 72 (S6, Z1, Z2), 74; 76 for symmetry · **Blocks:** 81

---

## 0. What this brief delivers

The parts of em-3d.md §9.3 the owner asked for "if easy", and the radar pulse the owner put in scope:

1. **The Rth matrix** across a device's heat sources — self- and mutual heating of the fingers (§1).
2. **Z_th(jω)** — the thermal impedance from the frequency-domain heat equation, inside the no-transient scope
   (§2).
3. **A Foster fit** of it, with its error stated (§3).
4. **Pulse-train peak temperature** for radar duty cycles, in closed form from the fit (§4).
5. **The Foster network written as a `.cnl` subcircuit**, not attached to anything (D10; owner: attaching is
   later) (§5).

Scenario 2's **offset** needs none of this (briefs 74–76 deliver it); this is what turns the same model into
numbers a circuit or a reliability calculation consumes.

---

## 1. `R-em3d80-1` — the Rth matrix

**`R-em3d80-1a` Input.** `"Rth": { "Sources": ["f1", "f2", …] | "*", "Stat": "Avg" | "Max" }`. The result is
R_ij = ΔT_i / P_j: the rise of source i's region (its statistic) per watt in source j, with **every boundary
made homogeneous** — fixed temperatures and ambients set to 0 — so superposition holds and the matrix is a
property of the structure, not of the heatsink setting.

**`R-em3d80-1b` Cost.** N solves with one right-hand side each — **one** factorisation on the direct path, N
PCG solves sharing one AMG hierarchy on the iterative path. With `KOfT` on, the matrix is the **tangent**
(small-signal) matrix at a stated operating point — the first sweep point's solution — and the notes say so;
with constant k it is exact.

**`R-em3d80-1c` Reciprocity.** With `Stat: Avg` the matrix is symmetric by construction (the stiffness matrix
is); the run reports the largest asymmetry as a check on the solve. With `Max` it is not expected to be, and
the notes say why.

**`R-em3d80-1d` Output.** An `Rth` cube (N × N, K/W) with the source names on both axes; each finger's
**effective** rise under the setup's actual powers, ΔT = R·P, as a second cube.

## 2. `R-em3d80-2` — Z_th(jω)

**`R-em3d80-2a`** (K + jωC)·T = q, with C the consistent mass matrix ∫ρc·NᵢNⱼ. Every meshed material needs
`DensityKgM3` and `SpecificHeat`; a missing one is a refusal naming the material and the object.

**`R-em3d80-2b` Input.** `"Zth": { "Sources": […], "Probes": […], "StartHz": "0.01", "StopHz": "1e6",
"PerDecade": 10 }` — defaults as shown, which span a package's seconds and a channel's microseconds. Each
frequency: sparse complex LU (the path the engine already has) below the crossover; **COCG** with the real
AMG of K above it. ω = 0 is the Rth case of §1 and is included as the first point.

**`R-em3d80-2c` Output.** `Zth:<probe>:<source>` complex cubes over frequency (single kind: Complex); a plot
in the probe panel of |Z_th| and phase, log frequency.

## 3. `R-em3d80-3` — the Foster fit

Z(jω) ≈ Σᵢ Rᵢ / (1 + jωτᵢ), **Rᵢ ≥ 0**, fitted by **non-negative least squares** on a fixed logarithmic grid of
τ (four per decade across the computed band, extended one decade past each end), then pruned of terms below
1e-4 of the total. Non-negative by construction, so the network is passive; no pole placement to diverge. The
DC point is weighted so ΣRᵢ equals the computed Rth to 1e-6. The **fit error** (max relative |ΔZ| over the band)
is a result and is printed; above 2 % the notes warn and suggest more points per decade. Self impedances are
fitted per source; mutual terms are fitted too and reported as data — they have no simple network form.

## 4. `R-em3d80-4` — pulse-train peak temperature

**`R-em3d80-4a` Input.** `"Pulse": { "PeakPower": "Pdiss", "Period": "1 ms", "Duty": "0.1" }`, every field an
expression (so duty or period can be swept). The pulse drives every source with its own power scaled by the
same waveform.

**`R-em3d80-4b` The answer**, in closed form from the Foster terms (t_on = Duty × Period):
- steady periodic **peak** rise: ΔT_peak = Σⱼ Pⱼ Σᵢ Rᵢⱼ (1 − e^{−t_on/τᵢⱼ}) / (1 − e^{−Period/τᵢⱼ});
- **single-pulse** rise at the end of one pulse: Σⱼ Pⱼ Σᵢ Rᵢⱼ (1 − e^{−t_on/τᵢⱼ});
- **average** rise: Duty × Σⱼ Pⱼ R_th,j.
Each added to the probe's steady baseline (the setup's boundary temperatures, with power zero). Results per
probe: peak, single-pulse and average temperature, and ΔT(t) over one period sampled at 200 points for a plot.

**`R-em3d80-4c`** With `KOfT` on, the fit is the tangent at the average-power operating point; the notes say so.
No transient solver exists or is built (PRD).

## 5. `R-em3d80-5` — the Foster network as a netlist (D10)

For each source's self impedance, a `.cnl` subcircuit, `<results>/<key>.thermal.<source>.foster.cnl`: Rᵢ ∥ Cᵢ
stages (Cᵢ = τᵢ/Rᵢ) in series between a **thermal pin** and a **reference pin** — the electrical analogy
power ↔ current, temperature rise ↔ voltage, stated in a comment at the top. It is **not attached** to any FET
thermal node (owner: later); `circuitrf check` passes on it, and the comment says it is Foster (so its
internal nodes are not physical temperatures).

## 6. Gates

1. **S6** fingers: the Rth matrix against the reference to its tolerance; `Avg` asymmetry below 1e-9.
2. **Superposition**: R·P equals a direct solve with all powers on, to 1e-9 (constant k).
3. **Z1** slab: Z_th at 30 frequencies against tanh(γL)/(kγ), to the reference's tolerance, by both solver paths.
4. **Fit**: on Z1, error below 1 %, all Rᵢ ≥ 0, ΣRᵢ = Rth to 1e-6.
5. **Z2** pulse: the closed form against brief 72's Fourier-series reference for a known Foster network, to 1e-9;
   duty → 1 gives the steady DC rise, duty → 0 gives zero.
6. **Refusals**: a material without ρ or c, named.
7. **The netlist**: the written `.cnl` passes `check`; an AC analysis of it reproduces the fitted Z(jω) to 1e-9
   (the network is what the fit says).

## 7. Owner check list (Debug build)

1. On the channel-vs-surface example with several fingers, run the Rth setup; read the matrix; note the
   centre finger's self term against an edge finger's.
2. Run the Z_th setup; look at |Z_th| against frequency.
3. Set a pulse of 100 µs period at 10 % duty; read peak, single-pulse and average channel temperature.
4. Open the Foster `.cnl`; run an AC on it.

## 8. Scope

- **No transient solver.** Pulses are answered from Z_th.
- **No attachment to a device** (owner: later).
- Findings in `src/Thermal/RESOLVED.md`; never `CLAUDE.md`.

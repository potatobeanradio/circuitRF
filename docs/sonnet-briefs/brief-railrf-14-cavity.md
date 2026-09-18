# Brief 14 — the cavity: the shunt branch, and sampling that does not step over a resonance

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail14-n` · **Phase:** P2b
**Area:** `src/Design/Layout/Pdn/`, `src/Design/RailRf/` · **Depends on:** 13 · **Blocks:** 15
**Design note:** [`railrf.md`](../design/railrf.md) §4.1, §4.4, §4.5, §7

---

## 0. What this brief delivers

The last two terms of §4.1's unit cell, and the sampling that makes them findable.

```
Shunt capacitance to the reference plane   C = ε₀·εᵣ·Δ² / h
Dielectric loss                            G = ω·C·tan δ
```

Plus **adaptive frequency sampling**, which §4.4 says *"is not optional once the cavity band is in scope:
plane resonances are narrow and a log grid steps straight over one."*

### `R-rail14-1` — why this is LAST, and it is not a hedge

§4.5, and §6 phases on it:

> A 60 × 50 mm board on FR-4 has its first mode at about **1.2 GHz**. The excitation set of §2.2 tops out at
> an RF crystal fundamental of **50 MHz**. **The cavity is real and worth modelling eventually; on these
> boards it is an order of magnitude above anything the board itself drives**, which is why §6 puts it last.

That is the phasing argument and it should survive into the code: this brief is genuinely useful and it is
genuinely not where the findings are on a compact battery-powered board. Nothing here should be built at the
expense of the earlier phases' accuracy.

---

## 1. `R-rail14-2` — the cell size rule changes here, and brief 3 warned about it

Brief 3 `R-rail3-14` set the DC cell size from the **minimum feature width on the rail**, and noted that the
wavelength rule binds only from this brief onward. Here it does:

> Cells are square-ish and sized by the shortest wavelength in the dielectric, **Δ ≤ λ_min/20**. On FR-4
> (ε_r ≈ 4.3) that is **7.2 mm at 1 GHz and 1.4 mm at 5 GHz.** A 90 × 70 mm board at 1.4 mm is about
> **3,200 cells** — trivially sparse.

Both rules now bind, and the cell size is the **smaller** of the two. State it in one place with the reason
each exists, per brief 3's instruction — a later reader who sees only the wavelength rule will "fix" the DC
mesh to it and quietly lose every thin trace.

---

## 2. `R-rail14-3` — tan δ is one of the two numbers most often wrong, and it changes the answer by 14 dB

§2.2:

> The two numbers that matter most and are most often wrong: **the power-to-reference dielectric thickness**
> (it sets the plane capacitance linearly **and** the spreading inductance linearly) and **tan δ** (it sets
> how sharp the cavity resonances are, which is **the difference between a 6 dB bump and a 20 dB one**).

And §9:

> **The stackup is usually wrong.** Designers copy a stackup from the last board. … railRF shows **the
> extracted plane capacitance as a single number early and prominently**, because a designer recognises a
> wrong one instantly and would never notice it buried in a curve.

So this brief adds one readout to the window and the report: **the extracted plane capacitance**, `C = ε₀εᵣA/h`
over the actual overlap area, as a single number. It is cheap, it is the highest-value sanity check in the
whole tool, and it must not be buried.

Where the stackup states no tan δ, brief 11 `R-rail11-5`'s per-class dissipation factor serves and **the
result is flagged** — which here means every peak height in the cavity band is indicative.

---

## 3. `R-rail14-4` — adaptive sampling, through the mechanism that already exists

§4.4: *"The existing adaptive sweep from the EM engine is the mechanism."* `src/Engine/Mom/PlanarAdaptiveSweep.cs`.
Reuse it; do not write a second refiner.

A log grid steps straight over a narrow resonance and produces a curve that looks smooth and is missing its
own worst point. That failure is silent and it is the reason this is not optional.

What is worth measuring rather than assuming: the recorded finding from the EM adaptive-sweep work is that the
saving **tracks grid oversampling** — the gain is large on an oversampled grid and small on a well-chosen one.
Here the motive is different (finding a peak, not saving time), so **gate on the peak being found**, not on a
speed-up.

---

## 4. `R-rail14-5` — Fast is refused in this band, and this brief is what makes the threshold real

Brief 4 `R-rail4-4` stated the rule and derived a threshold from where the shunt branch stops being
negligible. This brief is where "negligible" acquires a number that can be checked: run both, find the
frequency at which the shunt branch moves |Z| by a stated fraction, and assert the refusal threshold is at or
below it.

A threshold set too high is a fast model quietly answering in a band where it cannot.

---

## 5. Tests — `tests/Ui.Tests/RailRf/PdnCavityTests.cs`

### The two gates from §7, both closed form

- **The lumped limit.** *"Well below the first mode, a plane pair is a parallel-plate capacitor. The extracted
  Z must approach `1/(jωC)` with `C = ε₀εᵣA/h` to under 1 %."* This is the gate that catches a wrong ε₀εᵣ,
  a wrong area and a wrong `h` — all three at once, which is what makes it worth more than its cost.
- **The rectangular cavity's input impedance** has a closed form too; brief 15 gates the **modes** and this
  brief gates the **impedance** below and through the first one.

### The rest

- **`R-rail14-3`**: the extracted plane capacitance equals `ε₀εᵣA/h` over the real overlap area to under 1 %,
  including on a board with a cutout — the area is the **overlap**, not the outline.
- **`R-rail14-3` the tan δ sensitivity**: the same board at two tan δ values an order apart produces peak
  heights differing by roughly the 6-vs-20 dB the note names. Not a precise number — the claim is that the
  term is load-bearing, and a model insensitive to tan δ has a bug.
- **`R-rail14-4`**: a board with a deliberately narrow resonance is **found** by the adaptive sweep and
  **stepped over** by a log grid of the same point count. Both halves — the second is what proves the first
  was necessary.
- **`R-rail14-2`**: the cell size is the smaller of the two rules. A thin-trace board at 5 GHz takes the
  feature-width rule; a wide-plane board takes the wavelength rule.
- **`R-rail14-5`**: the fast refusal threshold is at or below the measured shunt-significant frequency.

Per the standing rule: **no timing tests.** A full-wave point on this repo's EM engine costs ~48 s and a
cavity sweep here is orders cheaper — that difference is the design's whole argument (§3) and it is measured
once in the note, not asserted in the suite.

---

## 6. Scope

- **No eigensolve, no mode list, no field maps, no |Z| map.** Brief 15. This brief makes the shunt branch real
  so brief 15's eigenproblem is well posed.
- **No full-wave anything.** §3, and the overview's rule: no `EmProblem`, no `CircuitRF.Engine.Mom` types —
  except `PlanarAdaptiveSweep`, which is a sampling utility rather than a solver, and which is the single
  named exception in this series.
- **No new solver.** The system is complex now rather than real; CSparse's complex LU is what
  `SParameterEngine` already uses.

**On completion:** record findings in `src/Design/RESOLVED.md`. Never in a CLAUDE.md.

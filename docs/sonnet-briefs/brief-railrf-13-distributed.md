# Brief 13 — the distributed low band: inductance, spreading, and the mounting loop

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail13-n` · **Phase:** P2a
**Area:** `src/Design/Layout/Pdn/` · **Depends on:** 3, 4, 12 · **Blocks:** 14, 16
**Design note:** [`railrf.md`](../design/railrf.md) §4.1, §4.3, §2.8, §7

---

## 0. What this brief delivers

The phase that makes the form-factor answer **quantitative** — §6: *"between about 1 MHz and 100 MHz, which
is where the decap self-resonances and the RF crystal are."*

Three additions to extractors that already exist:

1. **L on every mesh edge and every graph section** (§4.1, §4.6).
2. **Spreading inductance** — which falls out of the mesh rather than being a term added to it.
3. **The mounting loop computed from the actual via geometry** — replacing brief 11's typed value.

**No shunt branch. No modes.** Brief 14.

---

## 1. `R-rail13-1` — the inductance, and the factor of two on R

§4.1, at last with ω ≠ 0:

```
Series inductance along each cell edge     L = µ₀·h                    (square cells)
Series resistance along each cell edge     R = 2·Rs                    (both planes)
    with the skin-effect sheet resistance  Rs = √(π·f·µ / σ)   →   ρ/T below two skin depths
```

`h` is the dielectric separation. For non-square cells, L and R scale by the aspect ratio (along/across).

**The factor of two is the two planes in series in the loop, and rev 2 of the note got it wrong** — it used
one plane's sheet resistance and every derived crossover frequency came out at half its real value. Brief 3
already carries the comment; this brief is where the error would have shown up.

### `R-rail13-2` — the skin-depth crossover, and why one R matrix serves an unusually wide band

§2.8:

> Copper stays at its DC resistance until its thickness approaches **two skin depths**: **57 MHz for 0.5 oz,
> 14 MHz for 1 oz, 3.6 MHz for 2 oz** — so on the thin inner copper these boards use, one R matrix serves an
> unusually wide band.

That is a genuine performance property and worth exploiting: below the crossover the R matrix is
frequency-independent and only L and the solve change per point. Implement it as a stated condition rather
than as an implicit assumption, so the point at which R starts varying is visible.

### `R-rail13-3` — the inductance cannot be dropped from this band, and here is where it starts mattering

§2.8, with the numbers corrected in rev 3:

> Using §4.1's loop resistance (`R = 2·R_s`, both planes), **`ωL = R` at roughly 1.2 MHz** for a tight
> four-layer plane pair (`L_sq = μ₀h`, h = 100 µm) and roughly **83 kHz** for a two-layer board on 1.5 mm
> FR-4 — and **|Z| is already 10 % high at 46 % of those**, so about **570 kHz** and **38 kHz**.
>
> A resistance-only mesh is therefore honest only to a few tens of kilohertz on a two-layer board.

Those four numbers are the test rows for this brief's own honesty: the extracted |Z| with L in place must
diverge from the resistance-only answer by 10 % at 570 kHz on the four-layer case and at 38 kHz on the
two-layer one.

---

## 2. `R-rail13-4` — spreading inductance falls out; it is not a term

There is no "spreading inductance" element anywhere in this brief. It is what the mesh produces when current
spreads from a port into a plane, and it is correct **only if the mesh under the port is converged** — which
is brief 3 `R-rail3-8`'s local refinement, and which §9 names as a **correctness requirement, not an
optimisation**:

> A BGA's antipad array removes a large fraction of the copper in a small region and it is precisely under the
> load port. Too coarse a mesh there and the spreading inductance is **underestimated — again optimistically.**

So this brief's arrival is when brief 3's refinement gate stops being precautionary and starts being load
bearing. Re-run it here with L in place: halving Δ under a port changes the port **inductance** by less than
the stated tolerance.

---

## 3. `R-rail13-5` — the mounting loop, from the actual via geometry

§4.3, and it is the quantity the whole form-factor question turns on:

> the partial self-inductance of the power via and the return via, **minus twice their partial mutual
> inductance**, plus the pad-to-via trace. **The dominant term is the via pair's separation and the plane
> separation `h` — precisely the quantity that changes when a part moves.**

`L_loop = L_p + L_r − 2·M_pr + L_pad`. The minus-two-M term is the whole physics: a power via and its return
via close to each other have a small loop, and the same pair 4 mm apart does not.

§2.2: it is typically **0.3–1.5 nH**, it **dominates above roughly 50 MHz**, and *"it is the thing your form
factor change actually altered."* **You can override it** — a computed value is a default, not a fact.

§2.6's worked example is the acceptance story: *"The three parts nearest the load on the reference now sit
11 mm away with **1.3 nH** of mounting inductance instead of **0.45 nH**."* And §2.5's per-part comparison
table is what brief 16 makes of it: *"A part that was 0.4 nH on the reference and is 1.1 nH on yours because
its return via moved 4 mm is a finding you can act on in an afternoon."*

### `R-rail13-6` — shared return vias are coupled because the mesh has ONE node

§4.2, and it is a property to assert rather than a feature to build: two parts sharing one return via are
coupled **through it**, because the mesh has a single node there, not two. Brief 3 already asserts the node
count at DC; assert the **coupling** here, where it has an observable effect.

---

## 4. `R-rail13-7` — the fast model gets its section inductance here too

§2.9: the fast graph extractor carries *"each trace section between junctions becomes one resistance … (and,
above DC, **one loop inductance**)."* Brief 4 built the DC half; this brief adds the inductance, and brief 4's
agreement gate extends to it: on a trace-dominated path, Fast and Accurate agree on |Z| to the stated
tolerance across this band; on a pour-dominated one, Fast still refuses.

That extension is the point at which the fast model could quietly become dishonest — a section inductance is a
much cruder approximation than a section resistance — so the gate's frequency range is part of this brief's
deliverable, not an afterthought.

---

## 5. Tests — `tests/Ui.Tests/RailRf/PdnDistributedTests.cs`

- **`R-rail13-5`, §7's own gate**: **mounting inductance against a closed-form partial-inductance calculation
  for a via pair.** External arithmetic; state the formula and its source in the test comment. Sweep the pair
  separation and assert the answer rises with it — the minus-two-M behaviour, which a sign error would invert.
- **`R-rail13-3`**: the four crossover numbers. |Z| diverges 10 % from the resistance-only answer at ~570 kHz
  on a four-layer plane pair at h = 100 µm, and at ~38 kHz on a two-layer 1.5 mm FR-4 board.
- **`R-rail13-1`**: `L = µ₀h` per square cell on a uniform pair, against the closed form; a 2:1 aspect cell
  scales by 2.
- **`R-rail13-2`**: R is frequency-independent below the crossover and starts varying above it, at the
  thickness the stackup states. Three rows: 0.5 oz, 1 oz, 2 oz — 57 MHz, 14 MHz, 3.6 MHz.
- **`R-rail13-4`**: the port-inductance convergence gate under an antipad field, and the negative — a
  deliberately coarse mesh under the port **under-estimates**, in the optimistic direction, by a measurable
  amount. Proving the direction is what makes the gate worth having.
- **`R-rail13-6`**: two parts on one return via are coupled; the same two parts on separate return vias are
  not, and the difference is visible in the transfer impedance.
- **`R-rail13-7`**: brief 4's agreement gate, extended over 1 MHz–100 MHz.

---

## 6. Scope

- **No shunt branch, no `G`, no `C` to the reference plane.** Brief 14. This band is a distributed R-L network
  with the lumped parts hung on it, exactly as §2.8 says.
- **No eigensolve, no modes, no field maps.** Brief 15.
- **No new elements.** `SeriesRlcModel` carries an edge's R and L together, which is what it is for.
- **No adaptive sampling.** Brief 14 — it becomes *not optional* only when narrow plane resonances are in
  scope (§4.4).

**On completion:** record findings in `src/Design/RESOLVED.md`. Never in a CLAUDE.md.

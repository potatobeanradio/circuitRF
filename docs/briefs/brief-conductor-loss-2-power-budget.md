# Brief CL2 — `P_conductor` stops being an identical zero

**Series:** [`brief-conductor-loss-0-overview.md`](brief-conductor-loss-0-overview.md) ·
**Tags:** `R-cl2-n` · **Area:** `src/Engine/Mom` · **Depends on:** CL1

**Read first:** `PlanarPowerBudget.cs` in full — its header is the whole design and it is already
written around this hole; `PlanarMetrics.cs:440-520` (the metric registry); `PlanarCurrentDensity.cs`;
the series overview §1.

---

## Not affected by the quasi-static port calibration, and it is worth saying once

`brief-quasistatic-port-calibration.md` landed after CL1 and changed how a port is de-embedded below
a per-stack crossover (overview's own note, and CL3 §0). **Nothing in this brief reads it.**
`P_conductor` is a quadratic form in the SOLVED coefficient vector against CL1's Gram matrix, on the
DUT's own mesh; no calibration, error box or reference impedance enters it. The one place to keep an
eye on is R-cl2-4's radiation efficiency, whose `P_accepted` comes through the port — state the
fixture's crossover alongside the before/after numbers so a later reader can tell the two effects
apart.

## The problem

ANT-5 designed the itemisation around a term it could not compute. `PlanarPowerBudget.ConductorW` is
a hard zero, `ConductorNote` (`PlanarPowerBudget.cs:463`) explains why it ships as a zero rather
than being omitted, and `BoundNote` tells the user the reported radiation efficiency reads **high**
by roughly the metal's own share. CL1 makes the term computable. This brief computes it and repairs
the two consequences.

## The one thing that must not be got wrong

`P_dielectric` is **a residual, not an integral** — `PlanarPowerBudget.cs:21-33` is emphatic about
this and about why (a volume integral over a laterally infinite lossy substrate already contains the
whole surface-wave term, so adding them double-counts). Today:

```
P_dielectric = P_accepted − P_radiated − P_surfaceWave
```

With a real conductor term the residual's meaning changes and the arithmetic must change with it:

```
P_conductor  = ½ ∫ Re(Z_s)·|J|² dS            ← a genuine, independent integral
P_dielectric = P_accepted − P_radiated − P_surfaceWave − P_conductor
```

**Getting this backwards — leaving the residual alone and adding the conductor term beside it — is
the failure mode**, because the budget still sums to `P_accepted` by construction and therefore
still *looks* right. `P_dielectric` would silently absorb a negative copy of the conductor term. The
tell is a dielectric term that goes NEGATIVE on a low-tanδ MMIC substrate, where the conductor term
is the larger of the two by a factor of thirty.

## Milestones

1. **The integral.** `½∫Re(Z_s)|J|²dS` over the solved current, one `Re(Z_s)` per conductor level,
   plus the via barrels' `½Re(Z_via)|I|²`. **It is a quadratic form in the solved coefficient
   vector against the SAME Gram matrix CL1 built** — `P_cond = ½ Re(Z_s)·xᴴ G x` — so it costs one
   sparse product and needs no new geometry, no new quadrature and no second discretisation of the
   metal. Say that in the header: a second route to the same number is how a factor of two gets in.
2. **The residual**, re-derived as above, in `PlanarPowerBudget.For`.
3. **The two notes.** `ConductorNote` is rewritten from "identically zero" to what it now is,
   **including what it still cannot carry** — overview §3's thickness-blindness and CL1's R-cl1-9
   result, in a sentence. `BoundNote`'s clause (2) currently says the efficiency reads high by the
   metal's share; when the term is real that clause is retired, and clause (1)'s surface-wave
   lower-bound argument stands alone. **Do not leave a note describing a state the code is no longer
   in** — that is worse than no note.
4. **`Caption`** already prints the conductor line with its percentage and needs no change beyond
   the number becoming non-zero. Check it reads sensibly when the conductor term is the LARGEST of
   the four, which on the MMIC starter it will be.
5. **`PlanarMetric.PowerConductor`** (`PlanarMetrics.cs:80`, registered at `:471-474`) keeps its
   name, unit and axis. Only its note and its value change.

## Gates

- **R-cl2-1 — the lossless balance still closes.** ANT-5's own gate: with tanδ = 0, PEC metal
  (σ = ∞) and a PEC floor there is no absorption anywhere, so `P_dielectric` **and** `P_conductor`
  must both come out zero and the three independent routes — ½Re(Y_jj) from the factorisation,
  ∫U dΩ from the far field, and the pole residues — must still close on one number. Run it on both
  substrates of very different thickness, as ANT-5 does, for the reason ANT-5 gives: a balance that
  closes in one regime may be closing on a cancellation.
- **R-cl2-2 — two routes to the conductor term agree.** The quadratic form against the Gram matrix,
  versus a direct numerical `∫Re(Z_s)|J|²` over `PlanarCurrentDensity.Compute`'s map. Agreement to
  the quadrature's own accuracy. **This is the gate that catches the factor of two**, and the direct
  route is a test-only reference that does not ship.
- **R-cl2-3 — the residual has the right sign everywhere.** On the MMIC starter patch, where the
  conductor term dominates the dielectric term by ~30×, `P_dielectric` stays positive and small.
  A negative dielectric residual is the milestone-2 failure and this test is what names it.
- **R-cl2-4 — the efficiency moved the predicted way.** Radiation efficiency on the ANT-5 patch
  fixture is LOWER than CL1's PEC answer, by roughly the conductor share. Report both numbers.

## Must NOT

- Turn `P_dielectric` into a volume integral. The header explains at length why it is a residual and
  that reasoning is unchanged.
- Add `P_conductor` to the budget without subtracting it from the residual.
- Compute the current density a second time, or on a second mesh, to get `|J|`.
- Touch `PlanarFarField`, the surface-wave residues, or `PoleLossCeiling`.
- Change `PlanarMetric` names, units or axes — a renamed metric breaks every saved display.

## On completion

- `src/Engine/Mom/RESOLVED.md` `§CL2`: the quadratic-form identity, the two-route agreement, the
  before/after efficiency on the patch fixture, and the sign of every budget term on both starters.
- **No `CLAUDE.md` edit.** §3.7's antenna-metrics text will have gone stale — **report the lines to
  the owner**.

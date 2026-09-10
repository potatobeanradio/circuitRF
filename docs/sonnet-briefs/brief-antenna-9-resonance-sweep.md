# Brief — ANT-9: finding a high-Q resonance

**Series:** `brief-antenna-0-overview.md` §1c. **Depends on:** nothing. **Blocks:** nothing.

**Independent of the rest of the series.** Engine-only, and it matters for filters and resonators as
much as for antennas — an antenna is just the case that made it visible.

---

## 1. What was measured

On the imported patch board with the feed present (ANT-1 applied), 1.50-2.00 GHz, 51 points at 10 MHz
spacing:

> Adaptive frequency sampling: **44 of 51** point(s) were SOLVED … The worst disagreement refinement
> stopped at is **|ΔS| = 0.02 against a tolerance of 0.001**.

So the sampler solved 86 % of the requested grid — nearly the whole sweep, with none of the intended
saving — and **still did not meet its own tolerance**. The resonance is narrower than 10 MHz and the
requested grid cannot see it. The user's original `.cem` was worse: 100 MHz to 3 GHz in 10 points,
which cannot see a patch resonance at all.

Two separate problems, and they need separating before either is fixed:

1. **The criterion is fine and the grid is wrong.** `PlanarAdaptiveSweep`'s documented property is that
   "it never adds a frequency you did not ask for", so it "cannot rescue a feature that falls between"
   requested points. That is a deliberate and defensible property — it is what makes every published
   frequency the one the user asked for.
2. **On a high-Q response the adaptive path is not saving anything and is not admitting it.** Solving
   44 of 51 and reporting non-convergence is honest, but a user reading a percentage will read it as
   "it worked". The report should distinguish *converged, having skipped points* from *ran out of
   refinement without converging*, in its first clause rather than its last.

## 2. The decision this brief exists to force

**Should a sweep be allowed to publish a frequency the user did not ask for?**

Today: no, by design. For a resonance search: necessarily yes, or there is nothing to find. The two
cannot both hold silently.

**Recommendation: an explicit opt-in mode, and the added points are marked.** A `ResonanceSearch`
setting on the EM setup, default off, under which:

- the sampler may add frequencies, and **every added frequency is flagged in the result** so a trace
  can show which points were requested and which were found;
- the found resonance (or resonances) is **published as its own diagnostic** — f₀, the Q it implies,
  and the bandwidth at whatever criterion is chosen — so the answer to "where is it" is a number and
  not a plot the user has to read off;
- with it **off**, today's property holds exactly and bit-identically. That is the invariant: an
  existing `.cem` re-run must produce the same frequencies and the same s-parameters.

**The alternative, which is cheaper and worse:** publish an *estimated* f₀ from a coarse pass and make
the user re-run with a seeded grid. It preserves the never-add property, needs almost no new
machinery, and puts the work on the user every single time. It is a legitimate fallback if the
opt-in is refused, and it should be offered as such rather than built by default.

**This is the owner's call and the brief should not proceed past §3 without it.**

## 3. M1 — how the search works, if it is taken

The criterion stays what it is. `PlanarAdaptiveSweep`'s header is emphatic and correct: the criterion
is on **S**, never on a fit residual, and this repository has twice measured why (L7b-b's
`ModeCouplingResidual` is anti-correlated with the terminal error; L8a's `FitResidual` picks one of
the worst configurations). Nothing here touches that.

What is added is **seeding**, which is a different question:

- A resonance is where **Im(Z_in) crosses zero** with Re(Z_in) rising — cheap to detect from the
  interpolant the sampler already builds, with no extra solve.
- Bisect toward the crossing, then refine on the existing |ΔS| criterion until it is met. The criterion
  decides when to stop; the crossing only decides where to look.
- **Cap the added points**, and report the cap when it binds. An unbounded search on a structure with
  no resonance is a sweep that never ends, and a very high-Q resonance can absorb any budget.
- **Multiple resonances are the normal case**, not an edge case — a patch has higher-order modes in
  any wide sweep. Find and report all of them within the requested span; do not return "the" one.

## 4. M2 — the report

Whether or not §2 is taken, the report changes:

- **Lead with converged / not converged**, then the point counts. Today the first clause is the
  saving and the tolerance failure is at the end.
- When it did not converge, say **what would help** — a finer requested grid, or the search mode —
  in the same sentence, the way the mesher's refusals name the settings that act on the count.
- When the saving is negligible (say, above 80 % solved), say that the adaptive path did not help
  here. A percentage presented as a saving when it is not one is the same failure as a control that
  silently does nothing.

## 5. Gates

- **With the search off, bit-identity**: an existing `.cem` produces the same frequency list and the
  same s-parameters. This is the gate that makes the mode safe to add.
- A synthetic high-Q response (an analytic resonator, not a solved structure — this must be testable
  in milliseconds) is found by the search, to a stated accuracy in f₀ and Q.
- A structure with **no** resonance in the span terminates at the cap and says so.
- A **two-resonance** span returns both.
- Added points are flagged in the result and distinguishable from requested ones.
- The report leads with convergence.
- The measurement from §1 is re-taken on the real board after the change and **reported** — it does not
  become a test, per the standing rule.
- `RESOLVED.md` write-up in `src/Engine/Mom`; `CLAUDE.md` gains the new mode and, if §2 is taken, the
  narrowed statement of the never-add property.

## 6. Must NOT

- **Do not change the refinement criterion**, and above all do not move it onto a fit residual. §3.
- **Do not add points when the mode is off.**
- **Do not let a found point be indistinguishable from a requested one.**
- **Do not present a solved-point percentage as a saving without saying whether it converged.**
- **Do not make this a mesh or physics change.** The mesh is sized at `MeshFrequencyHz`, not at the
  frequencies the sweep happens to visit, and a search that re-meshes is a different phase.

## 7. Reading order

`src/Engine/Mom/PlanarAdaptiveSweep.cs` — the header, end to end; it records why the criterion is what
it is and the two measurements that decided it · `src/Engine/Mom/PlanarSolve.cs` (the refinement loop,
which owns the machinery) · `docs/user/src/reference/mom-engine.md` §adaptive (the never-add property,
stated to users) · `src/Engine/Mom/HISTORY.md` §L9e (the tolerance curve and the interpolant
comparison).

# Brief MC-17 — Linear charges from S at one bias or a few

**Series:** `brief-modelcomposer-0-overview.md` (D4, D7) · **Tag:** `R-mc17-<m>` · **Depends on:** MC-8, MC-16
**Area:** `src/Design/Modeling/ChargeTemplates.cs`, `ChargeFit.cs`, `ConservationCheck.cs`; the Charge step in
`src/Ui/ModelComposer/`; `docs/user/src/reference/model-composer.md`, `file-formats.md`; `docs/design/model-composer.md`
**Read first:** MC-8 (the charge step, its templates and the conservation checks); `ChargeFit.Gather`

---

## 1. Goal

S-parameters at a single bias build a model. Today the charge step refuses anything under four usable S points
(`ChargeFit.Gather`: "a charge fit needs at least 4"), whatever the method. A user with S at one operating point — the
commonest small-signal measurement there is — gets no model at all. With this brief they get a model whose charges
are **linear**: constant capacitances, exact at the measured bias.

## 2. Requirements

**R-mc17-1 — The `Linear` charge template.** A third entry in `ChargeTemplates.All`, in matrix form so each charge
is determined by its own two targets:

- `Qg = C11·vgs + C12·vds`, `Qd = C21·vgs + C22·vds`; parameters `C11`, `C12` (Qg) and `C21`, `C22` (Qd), in F,
  unbounded in sign, started at the data's mean.

Each charge is a function, so the model conserves charge by construction like every other template. The report line
gives the circuit set as well, `Cgs = C11 + C12`, `Cgd = −C12`, `Cds = C22 + C12`, `Cm = C21 − C12`
(`IntrinsicQuantities`' convention), because that is what a designer reads.

**R-mc17-2 — The minimum is the method's, not the step's.** The fixed "at least 4" in `Gather` becomes a per-method
rule:
- an equation charge (any template, or a user expression) needs at least as many targets as free parameters — each
  S point gives each charge two (`C11, C12` for `Qg`; `C21, C22` for `Qd`), so `Linear` fits from **one** point;
- ANN and Table keep their minimum of 4, and the refusal names the way out:
  `3 usable S point(s); an ANN charge needs at least 4 — the Linear equation fits from one.`

With one point the fit is exact; with two or three it is the least-squares constant matrix, and the report gives
each value's spread across the points so a user can see the capacitances are not constant.

**R-mc17-3 — No silent method switch.** Fit all with the default ANN and fewer than 4 points is refused with the
sentence above, never switched to `Linear` behind the user's back.

**R-mc17-4 — Charge conservation.** Check (a) reads the data's curl over a bias grid; with fewer points than it needs
it is reported as not run, in one line naming the point count, not as a failure. (b) reads zero by construction; (c)
runs as usual.

**R-mc17-5 — Provenance and explain.** The `.cmodel`'s provenance and `model explain` say `Qg, Qd: Linear from N bias
point(s)`, so a model built this way is never mistaken for a nonlinear one. A linear charge has no training domain, so
no extrapolation applies to it.

**R-mc17-6 — The window.** The Charge step's equation picker lists `Linear` with the other templates. With fewer than 4
points, the ANN and Table choices show the refusal as their tooltip. The C–V plots draw flat lines through the measured
symbols.

**R-mc17-7 — Docs** (sources only; the owner runs DocGen): in `model-composer.md`, the "What a model needs" row for S
reads *one bias or more*. A single bias gives linear charges; four or more allow ANN or Table charges, and biases across
the I–V plane give nonlinear ones. Update the checklist line and the method-picker table to match, and list the template
wherever the others are listed (`grep -rn Angelov docs/user/src`). The design note records the template and the
per-method minimum.

Owner rules: no change history in the pages; no vendor names; no "the design's own".

## 3. Not in this phase
Typed capacitances with no S data at all (MC-18). Bias-dependent charges from fewer than four points.

## 4. Gates
- `LinearChargeTests`: S at one bias of a known intrinsic core (constant C matrix) → `Linear` recovers `C11 … C22` to
  1e-9 relative; with two points of different C, the fit is their least-squares mean and the report carries the
  spread.
- ANN with 3 points is refused with the R-mc17-2 sentence; `Linear` with the same 3 points fits.
- An exported one-bias model, run as S-parameters at that bias at the measurement plane, matches the data with
  `|ΔS|` ≤ 0.02 (the currents' derivatives set gm and gds; the charges set the rest).
- The conservation report on a one-bias model carries the "(a) not run" line, and (b) is zero.
- Run only these classes and `ModelComposerDocumentedPageTests`.

# Brief — PCAL4: two ports on coupled conductors need a modal error box

**Series:** `brief-portcal-0-overview.md`. **No longer provisional — PCAL1 has reported**
(`src/Engine/Mom/RESOLVED.md`, "PCAL1 — how much clearance a calibrated port actually needs") and this
brief is warranted. **Still last:** it is the expensive one and should not be attempted until the
profile machinery PCAL3 builds is working.
**Scope:** the case the whole series started from — two edge ports on conductors that are coupled at
the reference plane.
**Not in scope:** differential and coplanar *ports* as a user-facing feature (`mom-engine.md` §10.6's
later work). This brief is about de-embedding ordinary single-ended ports that happen to be coupled,
which is a different thing that looks similar.

---

## What PCAL1 measured, and why PCAL3 does not subsume this

A driven neighbour needs **2–3× the clearance a passive one does** — ≈ 4 substrate heights at
h = 0.9 mm and ≈ 5.5 at h = 0.225 mm, against ≈ 2 for a portless neighbour at both. So a design PCAL3
makes safe is still refused when the neighbouring conductor carries a port, and the gap between the two
thresholds is exactly the ground this brief has to cover. At the separation the series opened on the
driven case is 22.6 dB out in S₂₁ against 8.1 dB for the passive one, and non-passive at 6 of 7
frequencies against 3 of 7.

**Two measurements that bear directly on the requirements below:**

- **The error is worst at the BOTTOM of the band, in every case measured** — which is D6's 1/a₂₁²
  amplification, and it means R-pcal4-2's mode-separation report and R-pcal4-3's branch residuals are
  most needed exactly where the conditioning is worst.
- **Neither existing residual carries information about the DUT**, structurally: `SolveErrorBox`'s
  arguments are the two standards and not the DUT, so `ConsistencyResidual` was bit-for-bit identical
  between the 22 dB-wrong run and the clean one. When R-pcal4-3 makes those quantities matrices, do not
  assume the matrix version acquires a predictive power the scalar one demonstrably does not have.

---

## 0. The wall

D6's peel is

    y_ij = (S_meas,ij − δ_ij·a₁₁) / (a₂₁(i)·a₂₁(j))

with a per-port scalar box `(A11, A22, A21)` and calibrations **shared** between ports of equal
cross-section. Two coupled conductors support **two modes** — even and odd, with different γ and
different Z_c. No assignment of scalars to two ports can represent that, so this is not a
parameterisation problem that a wider profile solves; PCAL3 can put both conductors in the standard and
the algebra downstream still cannot use them.

The extension is the textbook one — **multiline / multimode TRL**: an N-conductor standard, a modal
decomposition at the reference plane, and error-box quantities that become **matrices**. D5's
`cosh(γΔℓ) = ½·tr(M)` becomes an eigenproblem over a 2N×2N cascade, and D6's two sign ambiguities
become branch-tracking over N modes.

---

## 1. Requirements

**R-pcal4-1 — Ports whose feeds are mutually within the clearance distance form a CALIBRATION GROUP,
and the group is the unit of calibration.** One shared standard reproducing the group's transverse
profile; one modal error box for the group. A group of one is today's case and must remain
bit-identical.

**R-pcal4-2 — Modes come from the standard, not from an assumption.** Even/odd is the right answer for
two identical symmetric lines and the wrong answer for almost anything else — unequal widths, unequal
heights, three conductors. Extract the modal basis from the cascade eigenproblem the standards already
give and **report how well-separated the modes are**, because a nearly-degenerate pair is where this
method fails and the user needs to know.

**R-pcal4-3 — Every branch and sign decision is resolved by evidence and its residual reported.** D6
resolves two sign ambiguities today, one by continuity in frequency and one by a redundant equation,
and reports the rejected residual so a user can see when the choice was made by noise. N modes
multiply both problems. Keep the habit: decide by evidence, report what was discarded, and never
resolve a branch by assuming the modes stay in the same order across frequency — they do not, near a
crossing.

**R-pcal4-4 — Z_c becomes a modal impedance matrix, and its accuracy stays SEPARABLE from the
de-embedding's.** D7 is already explicit that these are two different things reported separately; the
matrix version must not blur them.

**R-pcal4-5 — Kernel A is still not an input.** Overview §3b. Kernel A solves the coupled cross-section
exactly and is *exactly* the shortcut that is forbidden, and it will be more tempting here than
anywhere else in the codebase. If the case for lifting that rule is genuinely stronger for a modal
port region than for a scalar one, **argue it in the brief and propose what replaces the A-vs-B
agreement gate** — do not simply take it.

**R-pcal4-6 — Degrade honestly.** Where the group cannot be calibrated — modes too degenerate, a
neighbour that is not uniform, a group too large — decline by name and fall through to PCAL2's
refusal. A partially-correct modal de-embedding that publishes is the failure this whole series exists
to remove.

**R-pcal4-7 — Cost is a gate, not a footnote.** PCAL3's R-pcal3-5 already flags that standards dominate
the run. A group of N ports needs a standard carrying all N conductors, solved at every frequency, and
the overview's four-port run already spent 6.51× the DUT's unknowns on standards. Measure it on a real
board and report it before this is turned on by default.

---

## 2. Gates

1. `testdata/portcal/coupled-pair` de-embeds to within **0.005 of the kernel-A-vs-kernel-B floor**
   (0.052 on that fixture — PCAL1 §1 for why the floor has to be subtracted and why refining the mesh
   does not move it) against the **Kernel A oracle**, and is passive across the band. This is the case
   the series was opened on; nothing else substitutes.
2. `testdata/portcal/separated-pair` (well-separated, group of one per port) is **byte-identical** to
   what it produces today.
3. A three-conductor group is exercised, not only a symmetric pair — a symmetric pair is the one case
   where an even/odd assumption would also pass, so on its own it proves nothing about R-pcal4-2.
4. An asymmetric pair (unequal widths) is exercised, for the same reason.
5. Mode-separation and rejected-branch residuals are reported per frequency and appear in the run's
   notes.
6. Unknown counts and wall clock reported against PCAL3's baseline.

---

## 3. What not to do

- **Do not generalise the port model at the same time.** Differential and coplanar *ports* are §10.6's
  separate later work; doing both at once means neither can be gated.
- **Do not assume reciprocity buys as much as it does in the scalar case.** D5's `det T = 1` collapse
  is a 2×2 fact; check what survives before relying on it.
- **Do not ship this on by default until R-pcal4-7 is answered.**

---

## 4. On completion

1. Findings to **`src/Engine/Mom/RESOLVED.md`** — the modal extraction, the branch tracking, what
   degrades and how, and the cost.
2. **Do not write any of this to a `CLAUDE.md`.**
3. `docs/user/src/reference/mom-engine.md` and the app note both state the validity condition this
   brief changes. Update both and regenerate.

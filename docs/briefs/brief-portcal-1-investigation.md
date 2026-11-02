# Brief — PCAL1: how much clearance a calibrated port actually needs, and which fix to build

**Series:** `brief-portcal-0-overview.md`. Read it first — this brief is sized by its §1 measurements.
**Scope:** measurement and a written decision. **No production code changes.**
**Not in scope:** changing the error box, the profile, the refusal, or anything a user sees. Those are
briefs 2–4 and they are deliberately not started until this one reports.

---

## 0. Why an investigation rather than a fix

Three things are unknown, and each of them changes what the fix should be:

1. **How much clearance is enough, and what it scales with.** The engine currently compares the
   neighbour distance against the *standard's own length* (2.7 mm in the measured case — a number in
   substrate heights, chosen for how the port's evanescent field decays longitudinally). There is no
   evidence that the longitudinal decay length is also the right *transverse* threshold. It may be far
   too conservative, or not conservative enough.
2. **Whether a driven neighbour and a passive one fail the same way.** If a passive neighbour is
   benign, brief 3 is unnecessary and brief 4 is the whole job. If it is not, brief 3 is the cheap
   majority of the value.
3. **Whether the error is predictable from quantities the solver already has.** If it is, a *bound*
   can be reported beside the result — which is worth more than a binary refusal and may make brief 2
   much better.

Guessing any of these would produce a fix sized for the wrong problem.

---

## 1. Requirements

**R-pcal1-1 — Establish the clearance law by sweep, not by argument.** Take the `fedpair` geometry of
overview §5 and sweep the feed separation *s* from the coupled 246 µm out to well past the standard's
length, holding everything else fixed. At each *s* report, against the Kernel A oracle:

- worst σ_max(S) over the band, and the count of non-passive points;
- max |ΔS₂₁| and |ΔS₁₁| in dB against Kernel A;
- the frequency at which the error is worst.

At least six values of *s*, log-spaced, and the sweep must bracket the transition on both sides — an
all-fail or all-pass sweep has measured nothing.

**R-pcal1-2 — Find what the threshold scales with.** Repeat R-pcal1-1 with **substrate height** changed
(0.9 mm and at least one other, ideally 4× apart) and with **line width** changed. State the threshold
in whichever of `s/h`, `s/w`, `s/λ_g` or an absolute length actually collapses the curves, **and say so
explicitly if none of them does.** A negative result here is a real finding and must be reported as
one, not smoothed over.

**R-pcal1-3 — Separate a driven neighbour from a passive one.** Re-run the coupled case with the
neighbouring line's ports *removed* (so the metal is present and undriven). If the de-embedding is
materially healthier, brief 3's premise holds and brief 4 can be deferred; if it fails identically,
brief 3 is not worth writing on its own. Report which, with numbers.

**R-pcal1-4 — Check a second geometry class.** Everything measured so far is two parallel lines. Check
at least one qualitatively different neighbour: a **ground pour** beside the feed, and/or a feed that
runs close to a **bend or pad of its own net**. The original board's port 2 reported "other metal 0 m
away" — that is a flare on its *own* conductor, which is a different case from a separate net and may
already be handled by `PlanarFeedExtension`. Say which cases are actually distinct.

**R-pcal1-5 — Test whether the error is bounded by something already computed.** The de-embedding
already reports `ConsistencyResidual` and `RejectedResidual` (D6's two-sign check), and the solve
reports σ_max. Correlate those against the measured |ΔS| from R-pcal1-1. If any of them tracks the
error usefully, say so and hand brief 2 a *quantitative* statement to report instead of a binary one.
**If none of them does, say that too** — the area's standing habit is that a residual is "an honest
measure of what was discarded, not a proven predictor of accuracy", and this is the chance to find out
which it is here.

**R-pcal1-6 — Write the decision.** The deliverable is a findings document that names, with evidence:
which of briefs 2, 3 and 4 to write; what each one's scope should actually be; and which of them the
measurements have made unnecessary. **Rewriting or deleting briefs 3 and 4 is an expected outcome, not
a failure.**

---

## 2. What not to do

- **Do not change production behaviour.** Not the refusal, not the threshold, not the profile. A
  threshold "obviously" too tight is still brief 2's to change, after this brief says what it should be.
- **Do not use Kernel A as an input to anything** — only as the oracle. Overview §3b.
- **Do not build the oracle out of Kernel B.** Comparing a de-embedded answer to another de-embedded
  answer measures agreement, not accuracy. Where the geometry is not a uniform cross-section and
  Kernel A cannot serve, the oracle is a **well-separated** version of the same structure (which
  R-pcal1-1 establishes is trustworthy) or a closed-form line — state which, per measurement.
- **Do not add timing benchmark tests.** Measurements belong in the findings document; assert
  structure, not wall clock.
- **Do not reach for a finer mesh to make a case go away.** Overview §1a already measured that it does
  not, and a case that only passes at cells/λ 30 has told you something — record it.

---

## 3. Gates

1. The sweep of R-pcal1-1 brackets the transition, with at least one passing and one failing *s*.
2. The scaling claim of R-pcal1-2 is supported by curves at two substrate heights that actually
   collapse under the proposed variable — or the brief states plainly that they do not.
3. R-pcal1-3's driven-vs-passive comparison is reported with numbers either way.
4. The findings document exists and names the next briefs and their scope.
5. Any fixture the follow-on briefs will need as a regression case is committed under `testdata/`,
   small enough to live there, with a note saying which brief consumes it.

---

## 4. Sizing

The sweep is the cost. One `fedpair` point at cells/λ 5 with the accelerated solve ran in **~3.5
minutes**; the coupled one in ~35 s. Six separations × two substrate heights × two widths is roughly
25 runs, call it **two hours of machine time**, most of it unattended. Do not raise cells/λ for the
sweep: R-pcal1-1 is a *relative* measurement at fixed mesh settings, and overview §1a already shows
the effect is not mesh-limited.

If the sweep turns out not to bracket the transition inside a sensible range of *s*, **stop and
report** rather than extending it indefinitely — that outcome would itself change the diagnosis.

---

## 5. On completion

1. Write the findings to **`src/Engine/Mom/RESOLVED.md`** — the clearance law (or the absence of one),
   the driven-vs-passive result, the residual-correlation result, and the decision on briefs 2–4.
   Create the section if it needs one.
2. **Do not write any of this to a `CLAUDE.md`.** Findings go in the sibling `RESOLVED.md`.
3. Update **`brief-portcal-0-overview.md` §4** to reflect the decision, and rewrite or delete briefs 3
   and 4 to match.
4. If the measured threshold differs from the standard's own length, say so in the app note
   (`docs/user/src/app-notes/an01-ports-and-coupling.md`) — its §"What did fix it" quotes 2.7 mm as the
   figure the engine uses, and a reader acting on it deserves the real number.

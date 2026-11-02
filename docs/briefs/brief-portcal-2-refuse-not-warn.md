# Brief — PCAL2: a 22 dB error ships as a note; make it stop being publishable

**Series:** `brief-portcal-0-overview.md`. **PCAL1 has reported** — its findings are in
`src/Engine/Mom/RESOLVED.md` ("PCAL1 — how much clearance a calibrated port actually needs") and the
threshold below is its measurement, not a placeholder.
**Scope:** what the solver does when a port's feed clearance is breached — the message, the exit, and
what (if anything) is written.
**Not in scope:** fixing the de-embedding. That is briefs 3 and 4. This brief makes the failure
visible; it does not make it go away.

---

## 0. The finding

Today a breach of feed clearance produces a note:

> Port 1's feed has other metal 246 µm away, inside the 2.7 mm the calibration standard assumes is
> empty. The de-embedding replaces the port's neighbourhood with an isolated line of the same width,
> so whatever is closer than that is not removed correctly. Move the feed away, or read the result
> knowing this.

and then **writes the Touchstone anyway**. Overview §1 measures what is in that file: S₂₁ wrong by
22 dB at 1 GHz, non-passive at 48 of 51 points. The note is accurate and the wording is good. The
problem is that the artefact it is attached to outlives it — a `.s4p` on disk carries no notes, and
the next person to open it in the Data Display sees a plausible curve.

The `NOT PASSIVE` note has the same shape and says so more bluntly — *"the s-parameters at those
points should not be used"* — and is likewise attached to a file that was written regardless.

---

## 1. Requirements

**R-pcal2-1 — A breach of clearance is a REFUSAL by default, naming the port and the distance.** The
same sentence, plus what to do, plus a non-zero exit from `Cli em` and no `.sNp` written. The
precedent is the mesh-ceiling refusal, which already stops a run that would take 20 minutes to produce
nothing usable, and which names the flag that answers it.

**R-pcal2-2 — There is an explicit way to get the file anyway, and it is not a silent one.** Users
have reasons to want the raw or the badly-calibrated answer: comparing against a previous run,
debugging, or simply knowing the port region is not where their answer lives. Add one setting on the
`.cem` — named for what it does, not for the check it suppresses — and make the resulting file say so:
the Touchstone's provenance block gains a line recording that the de-embedding was applied outside its
validity, and that line must survive being read back.

**R-pcal2-3 — Turning de-embedding OFF must be reachable.** `PlanarSolveSettings.Deembed` exists in the
engine and defaults to true, and **no `.cem` field exposes it** — so the remedy the mesh-ceiling
refusal already recommends in prose ("turn de-embedding off and read the raw solve instead") cannot be
followed from the GUI or the CLI. Expose it, with the caveat the existing message already carries: the
raw s-parameters include the port discontinuity and are for diagnostics.

**R-pcal2-4 — The threshold is PCAL1's, it lives in one place, and it is NOT `EndRunHeights`.**

Today `PlanarSolve` calls `CheckFeedClearance` with `EndRunHeights × h` — 3 substrate heights, 2.7 mm
on the measured stackup. PCAL1 measured that the **variable is right and the number is not**: the
error follows the neighbour's distance in substrate heights (a 4× change in line width moves the
threshold by 5 %), but at exactly 3 h the coupled pair is still 0.17 out in |ΔS| and non-passive.

**Give the clearance its own setting on `PlanarCalibrationSettings`, separate from `EndRunHeights`.**
They are the same constant today and must stop being: `EndRunHeights` is how far inward the standard
reproduces the DUT's own cells, so raising it to raise the clearance would lengthen every calibration
standard — and standards already cost 4.57× the DUT's unknowns on the fixture and 6.51× on the board.

**And it is two numbers, not one, because the two cases are 2–3× apart:**

| neighbour | measured requirement | recommended setting |
|---|---|---|
| carries a port (driven) | ≈ 4 h at h = 0.9 mm, ≈ 5.5 h at h = 0.225 mm | **5 substrate heights** |
| carries no port (passive trace, ground pour) | ≈ 2 h at both heights | **2 substrate heights** |

One number would either refuse designs that are fine or pass designs that are 18 dB wrong. Whether a
neighbouring conductor carries a port is already known at the call site — the port list is in hand.
The neighbour's own *width* does not enter: a 5 mm pour behaves like a 254 µm trace to within 2 %.

**R-pcal2-5 — Report the margin as `s/h`, and do NOT attempt an error bound.** Every run that
de-embeds should say how close each port came — a pass/fail with no distance is how a threshold change
becomes invisible — and the quantity to report is the neighbour's distance **in substrate heights**,
because that is the variable PCAL1 measured the error to follow.

**PCAL1's R-pcal1-5 found no usable error estimator and the reason is structural, so do not go looking
for one here.** `PlanarDeembed.SolveErrorBox`'s arguments are the two calibration standards and not the
DUT, so `ConsistencyResidual` and `RejectedResidual` are *bit-for-bit identical* between the run that
is 22 dB wrong and the run that is at the floor. σ_max is a good detector and a useless estimator: it
is non-monotonic in the error (the worst case measured reports a **smaller** passivity excess than a
case with half the error).

**R-pcal2-6 — Nothing that passes today changes.** A design with clear feeds produces a **bit-identical**
Touchstone. This is the same rule `PlanarFeedExtension` holds itself to and it is what makes the change
safe to ship.

---

## 2. What not to do

- **Do not refuse on the `NOT PASSIVE` check.** Tempting, and wrong: passivity is a *symptom* with many
  causes, some of them legitimate at the 1e-3 level, and a refusal there would block runs that are
  fine. Refuse on the *cause* — a measurable geometric fact about the port — and keep reporting
  passivity as the diagnostic it is.
- **Do not make the override a global preference.** It belongs on the `.cem`, beside everything else
  that changes the answer, for the reason `em-setup.md` already gives: *"Nothing that changes a result
  lives in a transient dialog."*
- **Do not soften the existing notes.** They are correct and hard-won; this brief adds a consequence,
  it does not rewrite the diagnosis.
- **Do not attempt to correct the answer here.** A refusal that silently applied a partial fix would
  be worse than either.

---

## 3. Gates

1. `testdata/portcal/coupled-pair` **refuses**, names port 1 and 246 µm, exits non-zero, and writes
   no `.sNp`.
2. `testdata/portcal/separated-pair` runs clean and its `.sNp` is **byte-identical** to the one it
   produces today (minus the write timestamp), proving R-pcal2-6.
2b. A passive neighbour at 2.1 h or more — `coupled-pair` with ports 3 and 4 deleted and the
   separation widened — passes on the 2 h threshold while the same geometry with four ports refuses
   on the 5 h one. Without this the two thresholds are untested as two.
3. With the override set, the coupled case runs, writes the file, and the file's provenance block
   records the breach; reading it back surfaces that line.
4. With de-embedding off, the coupled case runs and the notes say the answer includes the port
   discontinuity.
5. The GUI path and the `Cli em` path take the same decision from the same code — no second copy of
   the threshold or the predicate. A comment-stripped source scan for a second threshold literal, in
   the style `tests/Ui.Tests/AuthoringCliVerbTests.cs` already uses.

---

## 4. On completion

1. Findings to **`src/Engine/Mom/RESOLVED.md`** — what refuses, what the override is called, and why
   passivity was not made a refusal.
2. **Do not write any of this to a `CLAUDE.md`.**
3. Update `docs/user/src/reference/em-setup.md` (the new `.cem` settings and the refusal) and
   `docs/user/src/reference/mom-engine.md` (the validity condition), then regenerate the docs.
4. The app note's checklist gains a line: a clearance breach is now a refusal, so the check it asks
   the reader to make by eye is enforced.

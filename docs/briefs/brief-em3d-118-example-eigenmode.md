# Brief 118 — example: 3D Eigenmode

**Series:** [3D EM, eighth series](brief-em3d-112-overview.md) · **Tag:** `R-em3d118-n` ·
**Precedent:** [brief 70](brief-em3d-70-showcase.md) (numbers measured once, quoted everywhere); `3D Package`'s *Lid modes*
setup (the existing `.c3d` eigenmode run, 22.16 GHz, Q 318)
**Area:** `examples/3D Eigenmode/` (new), `examples/examples.json`, `docs/user/src/reference/em-setup.md`,
`drawing-in-3d.md` and `em-solvers.md` (links only), `tests/Ui.Tests/Examples/`
**Depends on:** nothing in this series. Eigenmode from a `.c3d` exists (brief 23), and so do saved field plots
(`C3dDocument.FieldPlots`, brief 83) · **Can be built first**

---

## 0. What this brief delivers

A small example that teaches Palace's **eigenmode** solve on the question every package designer has: *is there a
cavity resonance inside my band, and what moves it?*

- **Cavity**: a 50 Ω microstrip running through a metal cavity with a lid. Its first box mode lies inside the line's band,
  and a driven sweep shows it as a notch in S21.
- **Cavity with post**: the same, plus a **grounded post** from floor to lid beside the line at the mode's field maximum.
  The mode moves **above** the band and the notch is gone.

`3D Package` already runs an eigenmode, but inside a larger example. This one is about nothing else.

**Say what the post does accurately.** A grounded post does not damp the mode. It **shortens the cavity the mode sees**, so
the mode's frequency rises. The README says so in those words, because "the post suppresses the resonance" is the usual
misreading.

Edit doc **sources** only. Do not run DocGen or regenerate `docs/user`: the owner does that.

---

## 1. `R-em3d118-1` — the workspace

| Cell | View | What it is |
|---|---|---|
| **Cavity** | 3D view, mm | A rectangular metal housing (interior a × d × h), a substrate on its floor, a 50 Ω microstrip along its length, the lid. A lumped port at each end of the line, where it meets the end walls. Setups *Modes* (eigenmode) and *Driven* |
| **Cavity with post** | 3D view, mm | The same, plus one grounded cylinder (`post_d`) from the floor through the substrate to the lid, at the TE₁₀₁-like mode's E-field maximum and clear of the line by `post_gap`. Same two setups |

Two cells rather than one with a toggle, so each opens and runs as it stands, and the two results sit side by side in the
Data Display.

**Dimensions, stated once as VARs:** a, d, h, substrate εr and thickness, line width, `post_d`, `post_gap`. Choose a and d so
that:

- the empty-cavity TE₁₀₁ closed form, f = (c/2)·√((1/a)² + (1/d)²), lands just above the band's middle. The substrate pulls
  the real mode below that closed form, which the README states as an **upper bound**, not a prediction;
- the band is one the line is plainly meant for (for example 2–12 GHz with the mode near 8 GHz);
- with the post, the first mode lands **above** the band's top.

Measure, then adjust a, d and the post's position until all three hold. Record the iterations' numbers in
`examples/RESOLVED.md`, not in the README.

## 2. `R-em3d118-2` — the setups

- **Modes**: `Problem3D: Eigenmode`, `Count` 3, `TargetGHz` the band's start. The air box flush with the housing and PEC on
  every face (the housing's interior **is** the problem), as `3D Package`'s *Lid modes* is set up. Quality as `3D Package`
  uses it (*Draft*, element order 2), unless measurement says otherwise.
- **Driven**: the band, enough points to resolve the notch (its width follows from the loaded Q, Δf ≈ f/Q, so the step must
  be under that), Palace.
- **Field plot**: one saved plot per cell (`FieldPlots`), |E| of mode 1 on a horizontal clip plane at mid-substrate height,
  so opening the cell after a run shows *where* the mode lives and why the post sits where it does.

The ports load the mode, so its Q is the **loaded** Q (brief 23, R-em3d23-4b). The README says so and quotes both Q values
when Palace writes both.

## 3. `R-em3d118-3` — the numbers

`examples/3D Eigenmode/expected-numbers.json`:

- the closed-form upper bound;
- each cell's first three modes (f, Q);
- the driven notch frequency and depth;
- wall clock and memory per run.

The README and `em-setup.md` #eigenmodes quote these strings.

The headline sentence, with measured numbers filled in: *the first mode is at X GHz (Q Y), inside the band, and the
driven S21 has a Z dB notch there; with the post the first mode is at W GHz, above the band, and the notch is gone.*

**Cross-check, required.** Each cell's eigenmode frequency must agree with its own driven notch to within the driven
sweep's step. That agreement is what makes the example convincing, and if it fails it is a finding to report, not to tune
away.

## 4. `R-em3d118-4` — the README and the user pages

The README is a tutorial:

1. The question: a resonance inside the band.
2. *Modes* on Cavity, the mode table and how to read it (f, Q, loaded vs unloaded, and energy participation if Palace
   writes it).
3. Open the field plot and see the mode's maximum.
4. *Driven*, and the notch at the same frequency.
5. Cavity with post: *Modes* again, and the mode above the band.
6. *Driven*, and no notch.
7. Why it moved (the post shortens the cavity the mode sees).
8. Eigenmode is Palace only, because openEMS is a time-domain solver with no eigensolver (the refusal a user sees if they
   try).

**User pages** (sources only; overview §2a). Edit `docs/user/src/` only, and do **not** run DocGen or regenerate `docs/user`:
the owner does that.

- `em-setup.md` **#eigenmodes**: a short *Worked example* paragraph with the headline sentence, linking the example and
  saying what the post does (it shortens the cavity the mode sees; it does not damp it).
- `drawing-in-3d.md` **#lid** (`3D Package`'s lid resonance): one sentence at its end pointing to the example, for a
  reader who wants the question on its own.
- `em-solvers.md` (~267, the "Is there a cavity resonance in my band?" row): link the example.

## 5. Gate

1. The index test covers the folder and the `examples.json` row.
2. Both `.c3d` files open, elaborate and pass `check` with 0 errors.
3. **The closed-form bound is computed in the test** from the VARs and matches `expected-numbers.json`.
4. Each quoted Readme string appears in the README and in `em-setup.md`.
5. *(Benchmark)* The four shipped runs (two cells × two setups) re-run. The first mode's frequency holds within 0.2 %, the
   notch's frequency within one sweep step, and *Cavity with post*'s first mode stays above the band's top.

## 6. Owner check

The overview's §5 walk-through, the last paragraph.

## 7. Scope

- No engine change. No openEMS (eigenmode is Palace only).
- `3D Package` and `3D EM` are untouched.

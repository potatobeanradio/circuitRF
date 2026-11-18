# `src/Design/Em3d` — resolved briefs

## PWL-2 — the wall port on Palace, measured before building (2026-11-18)

`docs/briefs/brief-pwl-2-palace-spike.md`. Hand-built `.c3d` problems (a lumped port in a gap from a PEC end wall to the
line), run through `circuitrf em` on Palace 0.18.1 at Standard quality with no refinement passes, and calibrated with the
shared `LineCalibrator` (PWL-1) in a scratch harness. Fixtures S and C are in `testdata/pwl/`. Every comparison reports
the complex VSWR between answers (`RfHelpers.VswrFromGamma`) beside |ΔS|, and pairwise between results, not only
against a reference.

**The one-line result:** the calibration works on Palace with independently meshed standards. It takes the port match
from a raw VSWR of up to 1.39:1 to 1.0003–1.005:1 on the stripline. What is left against a closed form is Palace's own
phase-velocity error, not the wall port.

### Q1 — the gap does not matter once calibrated; cost picks it

Stripline, five gaps, the same standards each time. Calibrated results against each other: worst VSWR 1.0022–1.0094,
worst |ΔS21| 5e-5–1.1e-3. Against the exact line, every gap sits at |ΔS| ≈ 2.3e-2: the ε_eff error they all share.
Raw, the gap matters a great deal: at 10 GHz the raw VSWR is 1.99 at w/40 and 1.09 at w/5.

| Gap | Fraction | Solve time per standard | Peak memory |
|---|---|---|---|
| 42.9 µm | w/40 | 187–323 s | 7.0 GB |
| 85.9 µm | w/20 | 138–219 s | 4.9 GB |
| 100 µm | h/10 | 51–139 s | 4.5 GB |
| 171.8 µm | w/10 | 30–66 s | 3.3 GB |
| 343.6 µm | w/5 | 28–67 s | 2.4 GB |

A port sheet is meshed at least four elements across its short side, so a small gap is expensive.
**Decision: g = min(w, h)/5**, at the cheap end of the flat region. It is never a user setting.

### Q2 — independent meshing is enough; the extruded port block was not built

Third-line residual (the de-embedded line's |S11| in its own Z_c, which must vanish) on independently meshed standards:
stripline ≤ 1.8e-3 (≤ 8.6e-4 on a finer mesh), microstrip ≤ 4.8e-3, coupled pair (modal) ≤ 8.1e-3. Halving the largest
element changes the calibrated port match by VSWR 1.004. Box consistency residuals are 1e-7 to 5e-5.
**Decision: (a), independent meshing.** Forcing an identical port region was not needed and was not measured.

### Q3 — C′ from kernel A on the wall's cross-section

| Source | Stripline | Microstrip | Cost |
|---|---|---|---|
| Palace electrostatic, two standards differenced | +4.13 % | +3.20 % | 6–11 s per standard, Palace only |
| Kernel A (2D quasi-static), default mesh | −3.52 % | +0.03 % | 27–55 ms |
| Kernel A, 96 cells across | +0.20 % | — | 0.47 s |

Palace's electrostatic C′ reads high by the same few percent as its driven ε_eff, consistent with an under-resolved
zero-thickness edge in a potential-formulation FEM. Kernel A grades its mesh into the edge.

**Decision: kernel A**, for every solver alike:
- Grounded metal other than the floor (a second plane, a lid, a side wall) is a grounded conductor, and C′ is the
  Maxwell matrix's diagonal block for the group's conductors.
- **The cross-section mesh must be sized for the widest grounded conductor.** Kernel A's single-strip default put six
  cells across a 38 mm top plane and read −3.5 %.
- For a pair, kernel A's [C] must be symmetrised before the modal step. Its default mesh gave C₁₂ and C₂₁ a factor of three
  apart on a 1 µm-thick symmetric pair.
- A conducting layer enters as εr − jσ/(ωε₀), so C* is recomputed per frequency.

### Q4 — the short standard needs about 6 substrate heights

Microstrip, short standard 1, 2, 3, 5 and 7.9 h with the long standards fixed. At 1 GHz (βΔℓ 34–40° for all), ε_eff reads
1.960 / 1.947 / 1.943 / 1.939 / 1.937 and the third-line residual falls from 6.6e-4 to 1.3e-4: the two walls' end fields
overlap in a short standard and bias γ. 5 h against 7.9 h agree to VSWR 1.0035 and |ΔS21| 4e-3; 1 h is up to 1.10:1 off.

**Decision: the end run is 3 substrate heights per wall, and the short standard at least 6 h**, the planar solver's own
default.

(The 5 and 10 GHz rows of the shortest variants are also at βΔℓ 158–168°, because the long standards were held fixed.
That inflates them, and the product sizes separations from the short standard as built, so it is not a property of the
port.)

### Q5 — the conducting-silicon line: recorded from the existing reference, not re-run

The fixture was built (`testdata/em-si-sigma`'s line, 90 µm on 2 µm oxide on 100 µm of 10 S/m silicon) and it is
expensive on Palace. The first standard took 887 s with the adaptive sweep. The second was stopped after 32 minutes at
3–4 minutes per frequency, and the direct solver at five fixed frequencies did not finish one standard in 10 minutes.
The 2 µm oxide meshes to 2.6× the estimate.

`testdata/em-si-sigma/README.md` already records what Q5 asks, on this line:
- A wave port took the wrong mode at low frequency, and its driven S read a uniform line as S11 = −0.76 dB at 5 GHz.
- The reference γ was itself taken from lumped-port line pairs through the eigenvalues of T₂T₁⁻¹, which is this
  calibration's own γ step.

The full calibrated wall port against that reference is left to brief 7's Line over Silicon cell. On such a stack the
β prediction must be history-free, from kernel A's quasi-static γ with C*(ω), as the planar solver's SI-LOW path already
does: ε_eff falls from 53 to 7.6 over 1–40 GHz, and scaling β from the previous point picks the wrong separation.

### Q6 — what the standards cost

Wall clock, Palace, ten processes:

| Fixture | The 20 mm line | Its three standards together | Ratio |
|---|---|---|---|
| Stripline, gap w/10 | 56 s | 139 s | 2.5× |
| Microstrip, gap w/10 | 73 s | 167 s | 2.3× |
| Coupled pair (4-port), gap 100 µm | 468 s | 685 s | 1.5× |

1–10 GHz needs two separations (three standards). The cache (overview D9) is what makes a tuning sweep pay this once.

### Other findings

- **Palace's ε_eff on these fixtures, Standard quality, no refinement:** +0.76 % on the stripline (+0.41 % with the
  largest element halved), about +3 % on the microstrip (+2.4 % with the metal-edge elements halved). The microstrip's
  also falls with frequency, which a microstrip should not do. This floor, not the wall port, sets every |ΔS| against a
  closed form here.
- **Two wave-port reference runs did not finish:**
  - The stripline (PMC sides, PEC top and bottom faces) carries two TEM modes of equal speed on its port face, the
    degenerate case.
  - The microstrip ran 18 minutes in the solve at full CPU. It was stopped, and the cause is not established.
  - The wall port has neither problem: its PEC end walls tie the planes together, and it solves no mode.
- **A conducting dielectric needs `"Role": "Dielectric"`** on its object when its material states both εr and σ, or
  `check` refuses it, by name, as ambiguous.

### The brief's stop rule was met as written, and the owner chose restated gates

The brief asked to stop if no combination brought the stripline within 1e-2 in |ΔS| of its closed form at 10 GHz. Measured:
- **|ΔS|:** 2.3e-2 at Standard, 1.25e-2 with the largest element halved.
- **What it is:** Palace's phase velocity over 20 mm. The port match there is VSWR 1.0012–1.003, the third-line
  residual 8.6e-4, and all five gaps agree with each other to |ΔS21| ≤ 1.1e-3.

**Decided (owner, 2026-11-18): brief 3 proceeds, with its gates restated around the port match and self-consistency rather
than a finer-mesh reference first.** The gates, from these numbers with margin:
- **Gate 3, stripline:** calibrated VSWR against the exact line ≤ 1.01, third-line residual ≤ 3e-3, |ΔS| ≤ 3e-2 at
  10 GHz, ε_eff within 1 %.
- **Gate 4, coupled pair:** calibrated VSWR against kernel A's quasi-static coupled line ≤ 1.025, modal third-line
  residual ≤ 1.5e-2, both modes' ε_eff within 3 % of kernel A's, mode separation ≥ 0.5°.

# Sonnet Brief — WSProbe series, 0: overview, the equation register, and the decisions

**Read this first; it is the map for `brief-wsprobe-1-probe-and-wsp-matrix.md` through
`brief-wsprobe-8-hb-performance.md` and `brief-wsprobe-9-stability-margin.md`. It contains no
implementation work of its own.** It records what the reference documents say, what in them was
checked and found wrong, how their notation maps onto circuitRF's own conventions, and the decisions
the eight implementation briefs depend on.

---

## 1. The reference, and how to cite it

The whole series implements one public document:

> **T. A. Winslow, *General Circuit Analysis Using The WSProbe*, January 29, 2023** (172 pages, public
> technical report; the copy the owner holds is `<owner>/Stability/Winslow_Probe.pdf`, outside the
> repository).

**Every equation in these briefs is cited by that document's own equation number**, `(Eq. 44)`,
`(§4.2)`, `(App. E.13)`. The code must do the same: a function that computes the reduced admittance
matrix carries a doc-comment saying *Winslow, General Circuit Analysis Using The WSProbe (2023), Eq.
(44)*. The user docs cite it the same way. The document is the contract; a designer who has read it
must be able to find every quantity circuitRF reports under the name the document gave it.

**Notation is the document's, verbatim.** `wsp`, `H0`, `Y0`, `ZG`, `ZL`, `YG`, `YL`, `LG`, `F`,
`Zop`, `Yop`, `idx`, `LGR`, `LGF`, `LG_H`, `LG_MF`, `LG_MR`, `LG_MGF`, `LG_MGR`, `LGa`, `LGf`, `LGH`,
`LGM`, `LGB`, `FB`, `α β γ δ`, `P`, `A B C`, `M`, `G_i`, `SA`, `SP`, `YSo`, `YLo`, and the function
names of its Appendix E (`wsp_yparam`, `wsp_zparam`, `wsp_loopgain`, `wsp_block_calc`, …). Nothing gets
renamed to something more circuitRF-flavoured. Winslow himself regrets `H0` for an impedance (§4.1,
p. 34) and keeps it anyway; so do we.

**The one thing the document says that the repository must not repeat.** The document names a
commercial simulator, its scripting language, its documentation site, an oscillator-test element of
that simulator, a commercial pole-zero identification tool, and the author's employer. Repo-root
`CLAUDE.md` §Commercial Vendor References forbids every one of those names inside this repository
(code, comments, docs, commit messages, this brief series). Use these spellings instead:

| The document says | Write |
|---|---|
| the simulator, its scripting language, a function written in it | *the reference document's function library (Appendix E)*, *derived metric* |
| the built-in function of that simulator | *the reference document names a built-in for this; circuitRF implements it as …* |
| the oscillator-test element | *a circulator-based oscillator-test element* |
| the pole-zero identification tool | *pole-zero identification tools* |
| the author's employer | nothing — cite as *T. A. Winslow* |

The owner's instruction for the Appendix E scripts is explicit: **do not call them scripts of any
kind; they are derived metrics**, and they must be available in the Data Display and in `measure`
lines.

### 1.1 Follow-on literature (public), and what each contributes

Found on the open indexes (Crossref, OpenAlex) on 2026-09-08. The full texts of [M] and [E] are held
by the owner (§1.2); the rest are behind the publishers' paywalls and only their abstracts were
readable.

| Citation | What it adds | Where it lands |
|---|---|---|
| T. A. Winslow, "A Novel CAD Probe for Bidirectional Impedance and Stability Analysis," *2018 IEEE/MTT-S IMS*, DOI 10.1109/MWSYM.2018.8439210 | The original probe paper (the four-Γ dependent-source construction the 2023 document supersedes mathematically). Historical citation only. | user docs appendix |
| **[M]** T. A. Winslow, "A Novel Stability Margin for Transfer Functions," *Proc. 19th EuMIC*, Paris, 2024, pp. 291–294, DOI 10.23919/EuMIC61603.2024.10732614 | **The stability margin — full text held since 2026-09-08** (§1.2). The driving-point functions have units and a circuit-dependent trajectory, so they give a binary answer; each is a sum of two bidirectional immittances (M-Eq. 1, 2), and normalising the real parts and the imaginary parts of the pair against each other gives four bounded proxies `rY, iY, rH, iH` whose means `SM_Y0` (M-Eq. 9) and `SM_H0` (M-Eq. 10) are unitless margins in `[0, 1]`, read in dB. Demonstrated on a K-band amplifier that simulated stable and oscillated at 1.16 GHz: the margin showed −50 dB at 1.06 GHz in the original small-signal simulation. | **WSP-9** (library, engine defaults, summary, threshold), WSP-4 (display), WSP-5 (over `ssfreq`) |
| **[E]** T. A. Winslow, "Stability Envelope Using Nodal Transfer Functions," *Proc. 20th EuMIC*, Utrecht, 2025, pp. 254–257, DOI 10.23919/EuMIC65284.2025.11233915 | **Full text held since 2026-09-08** (§1.2). Builds the core 4-port `Y` of a circuit from the S, L and suspect probes' `wsp` entries (E-Eq. 1–4, with the transpose D-9 already states), adds pulled terminations (E-Eq. 5), reduces to the suspect probe's terminals (E-Eq. 6–8) and reads `H0`, `Y0`, `ZG`, `ZL`, `YG`, `YL` under mismatch (E-Eq. 9–12); sweeps the 2024 margin over source/load phase at fixed `ρ` on Ohtomo's Type-A amplifier against a full NDF sweep — the two agree at `ρ = 0.9` and `0.875`, and at `0.874` NDF reads stable while the margin still reads −40 dB. circuitRF reaches the same `wsp'` by WSP-3's rank-1 update; [E]'s reduction becomes the independent oracle for it. | **WSP-9** (margin envelope, NDF envelope, the oracle gate), WSP-4, WSP-6 (gate (k)), WSP-7 (fixture) |
| T. A. Winslow, "Introduction to the WSProbe," *2025 IEEE BCICTS*, DOI 10.1109/BCICTS63111.2025.11211431 | Abstract restates the 2023 document's thesis: the 2N×2N WSP matrix, precise bidirectional impedances, driving-point immittances "properly and accurately under any level of feedback." Nothing new to implement; cite in the user docs. | user docs |
| T. A. Winslow, IMS 2024 keynote abstract, *Stability Analysis Methods for Microwave Power Amplifiers: A Modern Perspective* (public PDF on the IMS site) | The 14-reference bibliography the user-docs appendix should follow; "4 transfer functions per node, two of which are essentially novel." | user docs appendix |
| F. Wiedmann, public forum thread implementing the 2018 probe in a free SPICE-class simulator (2023) | Independent confirmation that the probe reduces to Tian's dual-injection equations, and that the **bidirectional admittances** `Ysrc`/`Yload` (the document's `YG`/`YL`, Eq. 76) are a useful derived output the original paper did not print. | WSP-2 (`wsp_YG`/`wsp_YL` are first-class, not "not an output") |

### 1.2 The two EuMIC papers, held in full (2026-09-08)

The owner supplied both papers after WSP-3 was built: `<owner>/claude/Stability/
A_Novel_Stability_Margin_for_Transfer_Functions.pdf` ([M], 4 pages) and
`…/Stability_Envelope_Using_Nodal_Transfer_Functions.pdf` ([E], 4 pages), outside the repository like
the 2023 document. **Citation convention:** the 2023 document's equations stay bare, `(Eq. 44)`; [M]'s
are `(M-Eq. n)` and its four unnumbered proxy displays `(M-rY)`, `(M-iY)`, `(M-rH)`, `(M-iH)`; [E]'s are
`(E-Eq. n)`. Code doc-comments and the user docs use the same prefixes, and WSP-7's citation check
accepts all three forms. Their equations are in the register (§4) and their two printed errors in the
typo register (T-16, T-17), both found numerically while writing WSP-9. Both papers name the
commercial simulator, and [E] Fig. 5 prints a transistor **part number** beside its element values —
the numbers are public and usable, the name is not.

The earlier statement here that the margin formula was not public, and decision D-12's refusal, are
superseded: **WSP-9 implements the margin** and retires the placeholders D-12 introduced.

---

## 2. What the WSProbe is, in one page

A WSProbe is a **two-terminal series element with an orientation** (terminals `G` = generator side
and `L` = load side, Fig. 15/16) placed *in* a node — it splits the node into a G-side and an L-side
terminal, exactly as an `IProbe` does. Electrically it is a **0 V short**; it perturbs nothing (§4,
p. 33: "completely nonperturbative … requiring only a single analysis sweep").

Its value is not the element but the **`wsp` matrix** the analysis produces (§4.1). For each probe `i`
the engine applies, mathematically, two vanishingly small auxiliary generators (Fig. 16):

- a **series voltage** `vS` in the probe branch (− at G, + at L, so `v2 = vP + vS`), and
- a **shunt current** `iP` injected into the G-side node,

and reads at *every* probe `j` two responses: the branch current `iS` (flowing G → L through the
probe) and the G-side node voltage `vP`. Normalised, these are the four transfer functions per probe
pair (Eq. 33):

```
wsp(2i−1, 2j−1) = iSj / vSi        series current  response to series voltage   (Y0 when i = j)
wsp(2i−1, 2j  ) = vPj / vSi        shunt  voltage  response to series voltage
wsp(2i  , 2j−1) = iSj / iPi        series current  response to shunt current
wsp(2i  , 2j  ) = vPj / iPi        shunt  voltage  response to shunt current    (H0 when i = j)
```

With `N` probes this is a **2N × 2N matrix, 1-based, rows = stimulus probe, columns = response
probe** (Eq. 32–34). Everything else in the document — the reduced two-port at the probe (Eq. 44/48),
the bidirectional impedances (Eq. 65–68), every loop gain (Eq. 53, 97–104, 141–151), the synthetic
circulator (Eq. 96–99), probe-pair block extraction (Eq. 137–139), network bifurcation (Eq. 163–176),
Ohtomo's global loop gains (Eq. 177–180), the reduced matrices NDF needs (Eq. 182–186), and the
stability envelope (Eq. 187–191) — is **post-processing of `wsp`**. That is the architecture: one engine
addition (compute `wsp`), one library of pure functions over it, and their surfacing in measurements,
the Data Display and the CLI.

The two **driving-point transfer functions**, `H0 = vP/iP` (Bode's driving-point impedance, Eq. 29/50)
and `Y0 = iS/vS` (driving-point admittance, Eq. 52), carry the full network determinant in their
denominators (Eq. 28–30) and are the document's *primary* stability metric (§4.9–4.10): plot `1/H0` and
`1/Y0` on a polar chart and look for Kurokawa's start-up signature — a clockwise crossing of the
negative real axis (Eq. 107/108). **Both must be checked**, because a zero can mask the pole in one of
them but never in both (§4.10, the series and parallel resonator examples, Eq. 109–128).

The document is equally clear about what a loop gain is: every loop gain — including the probe's own
default `LG` and `F = 1 − LG` — is **incomplete** (§4.4, p. 50: "not fundamental circuit quantities and
are therefore not rigorous stability measurements"). They are diagnostic, and useful, and the briefs
carry them all; the user docs must carry the caveat with the same weight the document gives it.

---

## 3. Mapping the document's conventions onto circuitRF's

These are fixed here once. A sign flip in any of them produces plausible wrong answers.

| Document (Fig. 16) | circuitRF |
|---|---|
| Probe terminals `G` then `L` | `WSProbe:<label> nG nL` — first net is G, second is L (same order rule as `IProbe np nm`) |
| `iS` flows G → L through the probe | Engine branch-current convention: *first node → second node* (`src/Engine/CLAUDE.md`). `iS` **is** the branch current unknown, unchanged in sign |
| `vS` has − at G, + at L: `v_L − v_G = vS` | The 0 V constraint row is `V(nG) − V(nL) = value` (`IProbeModel.Stamp`); the series injection is `AddSourceValue(branch, −1)` for unit `vS` |
| `iP` injected **into** the G-side node | `AddCurrentInjection(nG, +1)` — the engine's current source convention already injects into its first node |
| `vP` = voltage of the G-side node to ground | `x[nG − 1]` of the solve |
| `i1` into port 1 of the reduced two-port (G side): `i1 = iP − iS`; `i2 = iS` | Consequences the reduction (Eq. 38, 45) already encodes; nothing to stamp |

The reduced two-port `[Y]`/`[Z]` (Eq. 44/48) has **port 1 = the G-side terminal, port 2 = the L-side
terminal**. `ZG = v1/i1` looks into port 1 (the generator direction); `ZL = v2/i2` into port 2 (the load
direction) (Eq. 64–66). In the zero-feedback limit `ZG = 1/y11` and `ZL = 1/y22` (Eq. 89) — the
impedances you would measure by opening the node and looking each way.

**Which side a subnetwork sits on matters in the multi-probe algebra, and the document is not
consistent about it** — see T-9 in the typo register and D-8 below.

---

## 4. The equation register

Every equation the series implements, with its owner brief. `✓` = independently re-derived or
numerically verified during the writing of these briefs; `≈` = transcribed from the document as
printed (a definitional formula with no independent oracle); `✗` = wrong as printed, corrected form
given in the typo register.

| Eq. | What | Status | Brief |
|---|---|---|---|
| 26–30 | `Zn = Vn/Iinj = Cof11/|Y|`, `Hij = Cofij/|Y|` — why H0/Y0 carry the determinant | ✓ (Cramer) | user docs |
| 31–36 | `wsp` definition and 1-based indexing | ✓ | WSP-1 |
| 37 | remote even-mode impedance `Zj = wsp(2i,2j)/wsp(2i,2j−1)` | ✓ | WSP-2 (`wsp_impedance`, App. C) |
| 38–40 | reduced-Y derivation | 40 ✗ (y12, y21 signs) | WSP-2 |
| 41–44 | `P`, `|P|`, `α β γ δ`, `[Y] = (1/H0)[[δ, −β],[−γ, α]]` | ✓ | WSP-2 (`wsp_yparam`) |
| 45–48 | reduced-Z derivation, `[Z] = (1/Y0)[[α, β],[γ, δ]]` | 47 ✗ (z22 first factor), 48 ✓ | WSP-2 (`wsp_zparam`) |
| 49 | `[Y] = [Z]⁻¹` | ✓ | WSP-2 gate |
| 50–52 | `H0 = wsp(2i,2i)`, `Y0 = wsp(2i−1,2i−1)`, `idx` | ✓ | WSP-1 |
| 53–60 | bilateral loop gain `LG = −(y12+y21)/(y11+y22) = (z12+z21)/(z11+z22)`, `F = 1 − LG` | ✓ | WSP-1 (default outputs), WSP-2 |
| 61–68 | `ZG = (y12+y22)/|Y| = z11 − z12 = −(1/Y0)(vP/vS)`; `ZL = (y21+y11)/|Y| = z22 − z21 = (1/Y0)(1 + vP/vS)` | ✓ | WSP-1, WSP-2 |
| 69–71 | `1/Y0 = ZG + ZL`; Kurokawa form | 70 ✗ (`y12` for `z12`) | WSP-2 gate |
| 72–78 | `YG = y11 + y12 = (1/H0)(1 − iS/iP)`, `YL = y22 + y21 = (1/H0)(iS/iP)`; Z forms | ✓ | WSP-2 (`wsp_YG`, `wsp_YL`) |
| 79–82 | `ZG ≠ 1/YG` in general; equal only with `y12 = y21 = 0` | ✓ | WSP-2 gate |
| 83–88 | pure-feedback capacitor limit, `CG = CL = 2C` for `G1 = G2 → 0` | 84/86/88 ✗ (index swaps; see T-5) | WSP-1 gate (symmetric case only) |
| 89–95 | `Zop = 1/(y11+y22)`, `Yop = 1/(z11+z22)`; `1/H0 = (1−LG)/Zop = YG+YL`; `1/Y0 = (1−LG)/Yop = ZG+ZL`; `Zop/H0 = Yop/Y0`; `YG+YL = |Y|(ZG+ZL)` | ✓ | WSP-2 |
| 96–99 | synthetic circulator `LGR = S21 + S11S22/(1−S12)`, `LGF = S12 + S11S22/(1−S21)`, and their normalised-y forms | ✓ (numerically, random 2-ports) | WSP-2 (`wsp_loopgain` UNI/REV) |
| 100–104 | Hurst, Middlebrook forward/reverse, GFT forward/reverse single-probe loop gains | ≈ | WSP-2 (`wsp_loopgain`) |
| 105–108 | Kurokawa start-up conditions on `1/H0` and `1/Y0` (3 conditions incl. `∂Im/∂ω > 0`) | ✓ | WSP-2 (`wsp_unstable_freq_kurokawa`), WSP-4 |
| 109–128 | closed-form `H0`, `Y0`, poles and zeros of the series and parallel unstable resonators | ✓ (re-derived) | WSP-1 gates, WSP-7 examples |
| 129–136 | 2-probe `wsp` layout; `H0ij`, `Y0ij` and the two unnamed cross terms | ✓ | WSP-1 |
| 137–139 | probe-pair reduction: `A`, `B` blocks, block-diagonal 8×8 | ✓ (re-derived) | WSP-3 (`wsp_yparam2`) |
| 140–151 | Hurst two-port loop gain; the 16 `wsp_block_calc` outputs | 141/150 ✓; 148/149/151 ≈ | WSP-3 (`wsp_block_calc`) |
| 152–155 | Middlebrook `Tv`, `Ti`, `1/TM = 1/Ti + 1/Tv`, `LGM` | 155 RHS = 151 ✓; sign chain ✗ (T-8) | WSP-3 |
| 156–160 | synthetic-FET return difference `FB` and loop gain `LGB` | ✓ (consistent with 148) | WSP-3 |
| 161–168 | Y-bifurcation `(A+I)Yf = C`, `AY = −C`; the four bifurcation functions | ✓ with the side rule of D-8 | WSP-3 (`wsp_YA`, `wsp_YF`) |
| 169–176 | Z-bifurcation `AZ = C`, `(I − A)Zf = C` | 173 ✗ (`B = A + I` should be `I − A`; the code and Eq. 176 agree) | WSP-3 (`wsp_ZA`, `wsp_ZF`) |
| 177–180 | Ohtomo: `M = SP·SA − I`, `Gi = 1 + |M_{N−i+1}|/|M_{N−i}|`, `M0 = 1` | 180 ✗ (index typos; see T-10) | WSP-3 (`wsp_loopgain_ohtomo`) |
| 181 | `NDF = Δ/Δ0 = |Y|/|Y_passive|` | ✓ | WSP-6 |
| 182–186 | reduced `Y = Z⁻¹` from the driving-point matrix; `NDF = |Z_passive|/|Z|` | ✓ (with the transpose rule, D-9) | WSP-3 (`wsp_ymatrix`), WSP-6 |
| 187–191 | stability envelope: `H03'` with `YSo, YLo` swapped for `YS, YL` | 191 ✗ (denominator matrix garbled; T-11) | WSP-3 (`wsp_loadpull`, `wsp_loadpull_unstable`) |
| 192–193 | non-invasive closed-loop return ratio `δ = Vab/Vx`, `F = 1/(1+δ)`, `T = δ/(1+δ)` | ≈ | WSP-6 (cross-check only) |
| 194–195 | stability index as summed reflections | ✓ | user docs |
| 196–201 | even-mode transfer impedance and power gain (`wsp_impedance`, `wsp_gain`) | 196/197 ✓; 199/201 ✗ (T-14; the code is right) | WSP-2 |
| 203–208 | `Γin`, `Γout`, `GP`, `GA`, `GT`, `Gmax` (`GainDEFs`) | ✓ (textbook) | WSP-2 |
| 17 | NDF as the sequential product of return differences (Struble) | ✓ | WSP-6 cross-check |
| 5–25 | K, B, Kurokawa's oscillator (9–10), Barkhausen, Bode's `F = Δ/Δ0`, NDF (17), Jackson's index (18–20), Middlebrook, Tian | textbook | user docs appendix |
| M-Eq. 1–2 | `1/H0 = YG + YL`, `1/Y0 = ZG + ZL` (= Eq. 93/95) | ✓ | WSP-9 |
| M-Eq. 3–4 | `ZG = z11 − z12 = (y12 + y22)/|Y|`, `ZL = z22 − z21 = (y21 + y11)/|Y|` (= Eq. 65–68) | ✓ | WSP-9 |
| M-Eq. 5–6 | `YG = y11 + y12`, `YL = y22 + y21` (= Eq. 72–77) and their Z-forms | Y-forms ✓; Z-forms ✗ (`z12`/`z21` swapped; T-17) | WSP-9 |
| M-Eq. 7–8 | Kurokawa on `1/H0` and `1/Y0` (= Eq. 107/108) | ✓ | WSP-2, WSP-9 |
| M-rY, M-iY, M-rH, M-iH | the four bounded proxies (three-branch real, two-branch imaginary) | ≈ (definitional; precedence and the zero cases are conventions — T-18) | WSP-9 |
| M-Eq. 9–10 | `SM_Y0 = ½(rY + iY)`, `SM_H0 = ½(rH + iH)` | ≈ (definitional); bounds and the −12 dB floor ✓ | WSP-9 |
| E-Eq. 1–4 | `V_m`, `I_m` from the three probes' `wsp` entries; `Y = (V_m⁻¹ I_m)ᵀ` | ✓ (random non-reciprocal 4-port, 2e-15; the transpose is required — D-9) | WSP-9 gate (f) |
| E-Eq. 5–8 | `Ȳ` with pulled `ȲS`, `ȲL`; the `d_ij` cofactor expansions and `D`; `R = d/D` | ✓ (equals the Schur complement to 1e-15) | WSP-9 gate (f) |
| E-Eq. 9–10 | `1/H0 = Σ r_ij`, `1/Y0 = Σ r_ij / |R|` | ✓ | WSP-9 |
| E-Eq. 11 | `Z^R_G`, `Z^R_L` under mismatch | ✗ (numerators swapped between G and L; T-16) | WSP-9 |
| E-Eq. 12 | `Y^R_G = r11 + r12`, `Y^R_L = r22 + r21` | ✓ | WSP-9 |
| E-Appendix | the `wsp(2·G − 1, 2·S)`… mapping of the twelve transfer functions | ✓ (consistent with Eq. 33) | WSP-9 gate (f) |

---

## 5. The typo register — what is wrong in the document as printed

Checked against the typeset pages (rendered, not the text extraction) and, where an oracle exists,
against an independent derivation or a numerical check on random two-ports. **Implement the corrected
form; never the printed one.** Each brief repeats the entries it depends on.

- **T-1 — Eq. 40, `y12` and `y21`.** As printed,
  `y12 = −[(iS/iP)(vP/vS) − (iS/vS)(vP/iP) + vP/vS] / (vP/iP)` and
  `y21 = −[(iS/iP)(vP/vS) − (iS/vS)(vP/iP) − iS/iP] / (vP/iP)`. Both have the `|P|` term with the wrong
  sign. Writing `A = vP/vS, B = iS/vS, C = vP/iP, D = iS/iP`, the correct values are
  `y12 = (AD − BC − A)/C` and `y21 = (AD − BC + D)/C`, which is what **Eq. 44** and the Appendix E.13
  code (`y12_LP = (A*D − B*C − A)/C`, `y21_LP = (A*D − B*C + D)/C`) both give. Verified by solving the
  Fig. 16 circuit directly and numerically (error of the printed Eq. 40: O(1); of Eq. 44: 2e-16).
  `y11` and `y22` in Eq. 40 are right. **Use Eq. 44.**
- **T-2 — Eq. 47, `z22`.** The first factor of its numerator is printed `iS/vP · vP/iP`; it is
  `iS/vS · vP/iP` (the `|P|` term, as in `z11`, `z12`, `z21`). **Use Eq. 48.**
- **T-3 — Eq. 70.** `1/Y0 = z11 + z22 − z21 − y12` should read `− z12`. Verified: `α + δ − β − γ = 1`.
- **T-4 — Eq. 84.** The generator/load numerators are swapped relative to Eq. 65/66 (`ZG` is printed
  with `y21 + y11`, `ZL` with `y12 + y22`). Harmless there (both are 0/0) but do not copy it.
- **T-5 — Eq. 86 and Eq. 88.** Eq. 86's intermediate numerator for `ZG` is printed `−yC + G1 + yC`
  (that is `y21 + y11`), while its final form `1/(G1 + (1 + G1/G2)yC)` is what `y12 + y22 = G2` gives.
  Eq. 88 then states `CG = (1 + G2/G1)C`, whereas Eq. 86's own final form gives `CG = (1 + G1/G2)C`.
  The document contradicts itself here. **Only the symmetric case `G1 = G2`, `CG = CL = 2C` (Eq. 87)
  is used as a gate**; the asymmetric case is not.
- **T-6 — Eq. 100 sign.** The single-probe Hurst loop gain is printed `LG_H = −y21y12/(y11y22)`, while
  the two-block Hurst form (Eq. 141/150) has no minus. The minus follows the convention of Eq. 53
  (`T ≡ −Vp/Vx`, `LG = −T`); it was not re-derived. Implement as printed and say so in the doc-comment.
- **T-7 — Eq. 149** is not a transcription error: the feedback network "as a synthetic FET" is
  passivated by replacing `y12f` with `y21f` (its reverse transfer term is treated as the controlled
  source), which is why its second product reads `(y21f + y21)(y21f + y12)`. Transcribe as printed.
- **T-8 — Eq. 152–155 sign chain.** Combining Eq. 152, 153 and 154 as printed gives
  `TM = +y21·y12f / D`, and Eq. 155 then states `LGM = −TM = +y12f·y21 / D` — one of the two signs is
  wrong. The right-hand side of Eq. 155 equals Eq. 151, and a direct Barkhausen-sense derivation
  (returned voltage over injected voltage around the `y21 → y12f` loop) gives `+y12f·y21/(y11T·y22T)`
  in the limit of no reverse terms, so **`LGM = y12f·y21 / [(y11+y11f)(y22+y22f) − y12y21 − y12f y21f]`
  (Eq. 151) is the form to implement.** `Tv` and `Ti` individually are not implemented.
- **T-9 — Which side is "active" in the bifurcation.** The Y-form (Eq. 161–168) puts the network whose
  port voltages are `V` (no `VS` added) — the **G side** of every probe — in `Y`, and the L side in
  `Yf`. The Z-form (Eq. 169–176, and the `wsp_ZA`/`wsp_ZF` code) puts the network carrying the probe
  current `+iS` — the **L side** — in `Z`, and the G side in `Zf`. So `wsp_YA` and `wsp_ZA` describe
  *opposite* subnetworks. Not a typo, but a trap. See D-8.
- **T-10 — Eq. 180 indices.** The first column is printed `M_{i,j}, M_{i+1,j}, …, M_{N,j}` and the
  last entry `M_{i,N}`. `M_{N−i+1}` is the trailing principal submatrix on rows and columns `i..N`
  (`M_{i,i} … M_{N,N}`). Sanity check: `N = 1` gives `G1 = SP·SA`, the reflection-coefficient product
  (Jackson's stability index), as it must.
- **T-11 — Eq. 191 denominator.** Printed as a 3×3 whose third column is `y12, y12, y12` and third row
  `y21, y22, y12`. It is the full modified matrix
  `[[y11 − YSo + YS, y12, y13], [y21, y22 − YLo + YL, y23], [y31, y32, y33]]`; the numerator is its
  (3,3) cofactor, exactly as Eq. 190 has it for the unmodified matrix.
- **T-12 — Eq. 173.** `B = A + [I]` for the Z-bifurcation contradicts Eq. 176 and the code, both of
  which use `[I] − A`. Use `[I] − A`.
- **T-13 — Eq. 170**'s feedback current `−IPi − Ii` does not match the code's `(I − A)`; treat the code
  (`wsp_ZF`) as authoritative.
- **T-14 — Eq. 199 and Eq. 201.** As printed, both factors of the ratio use the drain probe's index;
  Eq. 198/200 and the Appendix D code put the drain response in the numerator and the gate response in
  the denominator. Implement the code (WSP-2 §2.7).
- **T-15 — Eq. 163–168 and 175–176 (not a typo, a convention).** Written for a symmetric Y; for a
  non-reciprocal network they and the §6 code yield the transpose. See D-9 and WSP-3 §3.

- **T-16 — [E] Eq. 11.** Printed `Z^R_G = (r11 + r12)/(r11r22 − r12r21)`, `Z^R_L = (r22 + r21)/(…)`.
  The 2023 document's Eq. 65/66 (and [M] Eq. 3/4) have `ZG = (y12 + y22)/|Y|`, `ZL = (y21 + y11)/|Y|`;
  numerically, on a random non-reciprocal 4-port reduced per E-Eq. 5–8, the printed form is off by
  O(1) and `Z^R_G = (r12 + r22)/|R|`, `Z^R_L = (r21 + r11)/|R|` is right to 1e-15. The same G/L swap
  as T-4. **Use the corrected form; WSP-9 gate (f) asserts the printed one fails.**
- **T-17 — [M] Eq. 5 and 6, Z-forms.** Printed `YG = (z22 − z21)/|Z|`, `YL = (z11 − z12)/|Z|`. From
  `Y = Z⁻¹`, `y11 + y12 = (z22 − z12)/|Z|` and `y22 + y21 = (z11 − z21)/|Z|`. The off-diagonals are
  swapped; invisible on a reciprocal network, O(1) on a non-reciprocal one (verified). The Y-forms
  `YG = y11 + y12`, `YL = y22 + y21` are right and are what is implemented.
- **T-18 — [M]'s proxy displays (not a typo; conventions the paper leaves open).** The `rY`/`rH`
  third case (`Re sum ≤ 0 ⇒ 0`) is listed last but must be tested **first** — at `Re ZG = 5`,
  `Re ZL = −10` the magnitude branch alone gives 0.25 where Kurokawa's real-part condition holds; the
  equal-magnitude boundary is harmless (both branches agree); a pair with both parts exactly zero is
  undefined (the function is discontinuous at the origin) and circuitRF returns 0.5, the value one
  purely-resistive side gives; the dB convention is not stated and circuitRF uses `20·log10` (D-16).
  WSP-9 §2.1 states each with its gate.

Things that *look* like typos and are not: the `+1` sits in `A(1,1)` but in `B(2,2)` in Eq. 138/139 —
that asymmetry is the probe-pair orientation (derived in WSP-3 §2); Eq. 130's 4×4 matches Eq. 129's
row/column order; `wsp(1::2::N, 2::2::N)` in the §6 code is the odd-rows/even-columns block.

---

## 6. Decisions the seven briefs depend on

**D-1. Order is WSP-1 → WSP-2 → WSP-3 → WSP-9 → WSP-4, then WSP-5 → WSP-8 and WSP-6 in either
order, then WSP-7.** WSP-1 (the probe and the `wsp` cube in the S-parameter engine) is independent and
lands alone. WSP-2 (single-probe functions) needs only WSP-1's cube. WSP-3 (pairs, bifurcation,
Ohtomo, envelope) needs WSP-2's reduction functions. **WSP-9 (the stability margin and its envelope)
needs WSP-2's immittances and WSP-3's `wsp_terminate`, and lands before WSP-4** so the Data Display
draws the published margin rather than the placeholders it retires. WSP-4 (symbol, palette, Data
Display) needs WSP-1–3 and WSP-9's outputs to have something to draw, but its symbol/palette half can
start alongside WSP-1. WSP-5
(harmonic balance) reuses every function unchanged over a differently-produced `wsp`, and **WSP-8
(performance under harmonic balance) is its fast path** — WSP-5's straightforward implementation is
the oracle WSP-8 is measured against, so WSP-5 lands first and keeps that path available to the
tests. WSP-6 (NDF) is independent of the probe except for the probe-based cross-check (Eq. 186).
WSP-7 documents what shipped.

**D-2. `wsp` is computed by the engine; everything else is a pure function over it.** No derived
metric touches a matrix, a netlist or a solve. `H0`, `Y0`, `ZG`, `ZL`, `LG`, `F` — the document's
default per-probe outputs (Fig. 13) — are computed by the engine from `wsp` at run time as a
convenience, *through the same library functions the Data Display calls*, so a run's `ZG:GATE` cube
and a trace card's `wsp_ZG` of the same probe are bit-identical by construction (one implementation,
gated by test).

**D-3. The netlist spelling is `WSProbe:<label> nG nL`.** Two nets, G first. The instance name is the
document's "Label". `idx` is assigned at elaboration in **flattened netlist order, 1-based**, and is
reported in a `__WspProbes` metadata cube (label ↔ idx) so it is never guessed. A probe inside a
sub-cell is fine (§4.3: "at any depth of schematic hierarchy"); its label is its full instance path.

**D-4. Cube naming follows the repository's existing probe convention.** `IProbe` currents are
`I:IP1`; the WSProbe's per-probe outputs are `H0:<label>`, `Y0:<label>`, `ZG:<label>`, `ZL:<label>`,
`LG:<label>`, `F:<label>`, all Complex over `{freq}` (or `{ssfreq}` under WSP-5), and the matrix is one
Complex cube **`wsp` over `{freq, row, col}`** with 1-based integer `row`/`col` labels, so that
`wsp(2i−1, 2j)` in the document is `wsp[…, 2i−1, 2j]` here with no index arithmetic in between. In a
parametric sweep the sweep axis is prepended exactly as `S` and `V` are (`ParametricSweepEngine`
stacks any per-point cube).

**D-5. Measurement access uses the accessor grammar that already exists.** `SP1.wsp` is the cube;
`SP1.idx("GATE")` returns the integer index (the document's `GATE.idx`); `SP1.H0("GATE")` and the other
five are name-keyed accessors like `HB1.V("node")`. The Appendix E functions take `(wsp, idx…)` exactly
as the document writes them: `wsp_yparam(SP1.wsp, SP1.idx("GATE"))`.

**D-6. The function library lives below the firewall, in one place.** `src/RfCore/Stability/` (the
network-parameter math already lives in `RfCore`: `RFNetwork`, `NetworkMetrics`), as pure functions
over `Complex[,]`/`Complex[][,]` with cube-facing wrappers in the expression engine. Every function's
doc-comment cites the document by equation. The Data Display's `DerivedParameters` and the expression
engine both call these; neither re-implements anything.

**D-7. The per-probe reduced two-port is the unit of everything.** `wsp_yparam` (Eq. 44) and
`wsp_zparam` (Eq. 48) are the two primitives; every single-probe metric is a few lines over them. They
are also the place the numerical guard lives: `H0 = 0` or `Y0 = 0` at a frequency (an exact short or
open across the probe) makes the reduction undefined, and the library returns NaN at that point with
a per-run diagnostic rather than a `1e-15` fudge (the document's own `wsp_yop`/`wsp__zop` add
`1e-15` to a denominator; circuitRF does not — see WSP-2 R-wsp2-9).

**D-8. Sides are explicit in every multi-probe function.** Because of T-9, circuitRF's bifurcation API
takes the side as an argument, and the document's names are aliases with the document's sides:
`wsp_YA`/`wsp_YF` = G-side / L-side admittance matrices (Eq. 168/167); `wsp_ZA`/`wsp_ZF` = L-side /
G-side impedance matrices (Eq. 175/176). The identity `wsp_YA = inverse(wsp_ZF)` and `wsp_YF =
inverse(wsp_ZA)` (same side, both formulations) is a **gate**, not a remark. Ohtomo takes the two
subnetworks by side too.

**D-9. `wsp` is stimulus-major; the document's matrices are response-major.** `Z_{jk} = V_j/I_k =
wsp(2k, 2j)` (Eq. 184) — the *transpose* of the even/even block — and the same holds for the
bifurcation: the §6 functions (`−inverse(VV)*VI` and the others) return the **transpose** of the
subnetwork's Y or Z for a non-reciprocal network (WSP-3 §3 derives it). Every use the document makes
of them is transpose-invariant (determinants, principal minors, a diagonal cofactor), so its results
stand; circuitRF returns the true matrices. Test with a non-reciprocal fixture, since a reciprocal one
hides a missing transpose.

**D-10. Under harmonic balance the probe frequency is a small-signal sweep around the large-signal
operating point.** The owner's description is right: the circuit is driven by its HB tones and the
probe's injections are vanishingly small "tickles" at a swept frequency `ssfreq`, generally
incommensurate with the tones. The engine linearises the converged HB solution into the periodically
time-varying (conversion-matrix) system and solves the probe injections against it at each `ssfreq`
(WSP-5). The `wsp` cube then has axes `{ssfreq, row, col}` per operating point, and **every function of
WSP-2/3 applies unchanged** (§4.2: "Linear analysis, harmonic balance, or AC analysis can all be
accommodated"). This is a new engine capability (large-signal small-signal analysis); the HB engine's
Jacobian is its `ssfreq = 0` special case, which is the gate.

**D-11. NDF is native, exact for every built-in active model, explicit for user models, and refused
for black boxes.** circuitRF owns every dependent source in its own device models — the thing the
document says designers usually lack (§5.4, §8). WSP-6 computes `NDF = Δ/Δ0` from the same MNA
assembly the S-parameter engine uses, with each built-in active model stamping a documented
*passivated* form, user-defined devices (SDD, Verilog-A) passivated through a user-named scaling
variable, and an `SnP` with gain, an external device or an un-annotated SDD a **refusal that names the
instance** — never a silently passive-looking NDF. The determinant ratio is computed by the matrix
determinant lemma over the low-rank difference between the active and passive assemblies, not by two
large determinants, so it neither overflows nor cancels.

**D-12. The stability margin is Winslow's, transcribed from [M], and it is a first-class output.**
(Rewritten 2026-09-08; the original D-12 reserved `wsp_stability_margin` as a refusal and shipped
circuitRF's own normalised loci `nZ`/`nY` as placeholders because the formula was not public. It is
now — §1.2.) WSP-9 implements `rY, iY, rH, iH` (M-rY…M-iH), `SM_Y0` (M-Eq. 9) and `SM_H0`
(M-Eq. 10) as pure functions over `ZG, ZL, YG, YL`, under the paper's names; the engine emits
`SM_Y0:<label>` and `SM_H0:<label>` beside the six defaults of D-4 through the same library call;
`wsp_stability_margin(wsp, idx)` answers with `min(SM_Y0, SM_H0)`; the run summary prints each probe's
minimum and its frequency; an Info diagnostic fires below `MarginThreshold` (default −15 dB, the paper's
own rule); and the margin is swept over the WSP-3 envelope (`wsp_loadpull_margin`) beside an NDF over the
same envelope (`wsp_loadpull_ndf`), which is [E]'s comparison as post-processing. **The placeholders are
retired** — `wsp_nZ`, `wsp_nY` and the refusal go, per the original D-12's own promise — so that the
only normalised stability quantity in circuitRF is the published one. Conventions the paper leaves
open are fixed in WSP-9 §2.1 and T-18, each held by a gate.

**D-16. The margin's dB is `20·log10(SM)`.** [M] labels its axes "Stability Margin (dB)" and never
states the convention; the margin is a unitless ratio bounded by 1, circuitRF's Data Display applies
`20·log10` to every unitless magnitude, and adding a special case for one quantity would be the
surprise. Consequences the docs state: a conjugate-matched node reads −6 dB, not 0; a node with
positive resistance on both sides never reads below −12 dB, so anything below that certifies negative
resistance on one side; the paper's −15 dB rule is `0.178` linear (it would be `0.032` under
`10·log10`, and the docs print both so no reader is misled silently). The owner may overrule this by
changing one constant and one sentence; nothing else depends on it.

**D-13. Nothing in this series changes an existing number.** An S-parameter run with no probe is
byte-identical to before (the probe path is not entered). An HB run with no `ssfreq` sweep is
byte-identical. The NDF run is a separate analysis knob. Held by test in every brief.

**D-14. No new timing tests** (owner instruction, 2026-08-23). Structural counters only: "one
factorisation per frequency, 2N back-substitutions per probe set", asserted as counts.

**D-15. Fixtures are analytic, redrawn, and vendor-free.** The gates are the document's own closed-form
resonators (Eq. 109–128), the pure-feedback capacitor (Eq. 87), the zero-feedback cascade (Eq. 81/82,
89/90), random two-ports for the identities, and circuitRF's own two-tone HB as the independent oracle
for WSP-5. No fixture is copied from the document's figures; every schematic figure in the user docs
is redrawn through the docs factory.

---

## 7. Completion rules for the series

Each brief ends with its own gates. Series-wide:

- Findings go to the relevant `RESOLVED.md` (`src/Engine/RESOLVED.md`, `src/RfCore/RESOLVED.md`,
  `src/Ui/RESOLVED.md`, `src/Cli/RESOLVED.md`); **never to any `CLAUDE.md`**.
- `docs/design/stability-wsprobe.md` is created by WSP-1 (the engine half) and extended by
  WSP-2/3/9/5/6/8 (§9 is WSP-9's, §10 WSP-5's, §11 WSP-6's, §12 WSP-8's); it is the design note the
  user docs and the code cite alongside the documents.
- Before any commit the owner makes, grep the diff for the vendor names of §1 — the reference document
  makes them easy to type by accident.
- The user docs are regenerated only by WSP-7; the other briefs edit `docs/user/src/reference/*.md`
  and leave the generated HTML alone.

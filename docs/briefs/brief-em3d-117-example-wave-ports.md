# Brief 117 — example: 3D Wave Ports

**Series:** [3D EM, eighth series](brief-em3d-112-overview.md) · **Tag:** `R-em3d117-n` ·
**Precedent:** [brief 70](brief-em3d-70-showcase.md) (`3D Connector`: one numbers file quoted by the README and the user
page, a test that holds them together, a Benchmark-tier re-run of the shipped setups)
**Area:** `examples/3D Wave Ports/` (new), `examples/examples.json`, `docs/user/src/new-user-guide/index.md`,
`docs/user/src/reference/em-setup.md`, `docs/user/src/reference/em-solvers.md` (links only), `tests/Ui.Tests/Examples/`
**Depends on:** 116 (with 114 for Pair). **Rests on:** brief 113-a's measurements (`src/Design/RESOLVED.md`)

---

## 0. What this brief delivers

The example the owner asked for: it teaches a newcomer **how to set up and run a wave port on Palace and on openEMS**,
including the case where **two conductors end on one face** and each becomes its own port. That second case runs on
**openEMS only** (overview D14): Palace's port modes come from the face, and brief 113-a measured that neither documented
way of splitting a face between two lines gives per-conductor results. The README says so plainly (§4). It is a new workspace
(overview D11), self-contained because *Tools ▸ Examples* copies a workspace whole.

Edit doc **sources** only. Do not run DocGen or regenerate `docs/user`: the owner does that.

---

## 1. `R-em3d117-1` — the workspace

| Cell | View | What it is |
|---|---|---|
| **Board** | layout, mil | Copied unchanged from `3D Connector` |
| **Flange** | 3D view, mm | Copied unchanged from `3D Connector`, with its `flange.step` |
| **Launch** | 3D view, mm | `3D Connector`'s launch with **its back end open**: the bore and the pin run to the air box's xmin face, and P1 is a **wave port** on that face (pin and housing; one terminal, inferred). P2 stays the board's lumped port, so the cell also shows that the two kinds mix. Setups *Palace* and *openEMS* |
| **Pair** | 3D view, mm | An **edge-coupled stripline pair** (overview D12, measured by 113-a): two **zero-thickness strips** (sheets) between two ground planes, 15 mm long, PTFE filled, the air box's side faces **PMC** at least 10 × W from the pair (113-a: PEC side walls close to a stripline make a box mode near the band; brief 116 §1d). A **two-terminal wave port at each end**, so a 4-port result. Setup *openEMS* only |

One technology serves all four. Copy `board-and-connector.ctech` and add nothing unless Pair needs a material it lacks.

**The Launch change, precisely.** In `3D Connector` the bore has a floor and P1 bridges a 0.3 mm gap between that floor and
the pin's end (`examples/RESOLVED.md`, brief-em3d-70: "the coax end is a gap port"). Here:

- the housing's bore is cut through the rear face;
- the pin runs to that face;
- the air box's xmin padding is 0, as it already is, so the face is the box face.

This is the comparison the example exists to make. The lumped gap port carried a small capacitance that was in every
`3D Connector` run, and the wave port does not. The README quotes both |S11| curves.

**Pair's dimensions** are stated once as VARs:

- strip width W, gap S, ground spacing b; the strips are sheets, because Cohn's closed form is exact only at zero
  thickness (113-a: at t/b = 0.01 it is 0.66 dB off on the near-end coupling);
- chosen so Z₀ odd and even straddle 50 Ω (for example Z₀e ≈ 60 Ω, Z₀o ≈ 40 Ω), which gives clearly visible coupling;
- Cohn's Z₀e and Z₀o (zero thickness, unbounded sides) are computed in the README from the VARs. 113-a measured them
  against a 2D field solve of exactly this construction (sheets, PMC walls 10 × W out): **0.08–0.10 % apart**, so they
  are the reference.

Numbering: the left face's terminals are 1 and 2 and the right face's are 3 and 4, with 1–3 and 2–4 on the same strip. That
way **S21 is near-end coupling, S31 is thru and S41 is far-end coupling**, and the README says so in one sentence, because a
4-port's numbering is where readers get lost.

## 2. `R-em3d117-2` — the setups

*Launch* has *Palace* and *openEMS* setups; *Pair* has *openEMS* only. Both over 2–18 GHz, as the connector.

- **Palace** (Launch): the Draft preset the connector uses.
- **openEMS**: the connector's grid settings for Launch. For Pair, at least 8 cells across each strip's height to the
  ground (113-a: 4 cells, the openEMS tutorial's choice, reads Z 5 % low; 8 cells 1.4 % low; 16 cells 0.7 %). The feed
  extension (116) is automatic, and the README shows the run note that reports it, with the line's measured Z and ε_eff.
- **Launch's coax on openEMS is on a Cartesian grid**: a coax meeting a board cannot use brief 120's cylindrical grid. The
  run note names the coax's phase-velocity error (113-a: 1–2.4 %), and the README quotes it beside the Palace result.

Any setting lowered for speed is stated in the README with its cost and what it trades.

## 3. `R-em3d117-3` — the numbers, measured once, quoted everywhere

`examples/3D Wave Ports/expected-numbers.json`, in `3D Connector`'s schema:

- the closed forms (coax Z₀ **50.02 Ω**, with η₀; the `3D Connector` README's 50.06 Ω uses the rounding 60 ≈ η₀/2π, and
  this example says so once; Cohn's Z₀e/Z₀o for Pair);
- the coax impedance Palace reports at the wave port (`Z_PV`; 113-a measured 49.976 Ω on a clean coax), which
  `3D Connector` could not quote (brief 70's second external check, finally possible);
- the coax line's Z and ε_eff openEMS measures (116 §2e);
- every run's wall clock and memory;
- the recorded |S| of every shipped run.

The README and the user pages quote these strings, and the test holds them together.

## 4. `R-em3d117-4` — the README: how to set it up, step by step

The README is the tutorial. In order:

1. **What a wave port is**, in two sentences: the face of the box becomes a matched continuation of the line, so no gap and
   no parasitic.
2. **Launch, by hand**:
   - right-click the xmin face region over the coax → *Make Port ▸ Wave*;
   - read the overlay (the voltage-path arrow from the housing to the pin);
   - run *Palace*, run *openEMS*;
   - compare with `3D Connector`'s lumped result. Quote both |S11| figures at 18 GHz.
3. **Pair, by hand**:
   - *Make Port ▸ Wave* on each end face → two numbered arrows per face, and the reference named in the status line;
   - what "terminal S" means (each strip is a port, voltage from the ground to the strip);
   - the numbering sentence from §1;
   - run it; open the Data Display with S31, S21 and S41, and Cohn's ideal line beside them.
4. **What each solver did**, honestly:
   - on Launch, Palace solved the coax's mode on the face; openEMS fed the line from behind and measured voltage and
     current, and says how slow its staircased coax runs;
   - on Pair, openEMS fed each strip on its own and built S from all four runs together;
   - **why Pair has no Palace setup**: a face split between the two strips makes each port carry only the odd half of the
     field, which brief 113-a measured as 1.2 dB off on the thru and 13 dB off on the far-end coupling at this kind of
     spacing. Say it in two sentences, without the numbers' history.
5. **Numbers**: the table from §3.
6. **What is deliberately not here**: mixed-mode (D13), a hollow waveguide on openEMS (D10).

The reference chapter is **EM Setup ▸ Wave ports** (`em-setup.md` #wave-ports and #wave-port-terminals), which briefs
114–116 have already brought up to date. The README links it, and does not repeat it.

## 5. `R-em3d117-5` — the user pages (sources only; overview §2a)

The capability itself is documented by 114–116. This brief adds only the walk-through and the links. Edit
`docs/user/src/` only, and do **not** run DocGen or regenerate `docs/user`: the owner does that.

- `new-user-guide/index.md`: a new section after §13, **14 · Wave ports: one port per conductor** {#wave-ports-3d}, in
  §13's style ("ten minutes, nothing to draw"):
  - open the example;
  - run *Launch* on both solvers and compare with §13's lumped result;
  - open *Pair*, see the terminals, run it on openEMS, and plot thru and coupling;
  - one paragraph on what each solver did, and why Pair is openEMS's.

  It quotes the headline numbers from `expected-numbers.json`.
- `em-setup.md` #wave-ports and #wave-port-terminals: one line each, linking the walk-through and the example.
- `em-solvers.md`: where the capability table (116) says openEMS builds wave ports, a link to the example.

## 6. Gate

`tests/Ui.Tests/Examples/` (the existing index test covers the folder and the `examples.json` row both ways):

1. Every `.c3d` opens, elaborates and passes `check` with 0 errors; `explain` lists Pair's four terminals, and a Palace
   setup added to Pair by hand is refused with brief 116's sentence.
2. Each quoted number's Readme text appears in both the README and the new-user-guide walk-through (brief 70's mechanism).
3. **The closed forms are computed in the test** from the VARs: coax Z₀ and Cohn's Z₀e/Z₀o match `expected-numbers.json`.
   The shipped Pair result is within brief 116's gate-4 tolerances of the Cohn line (thru 0.1 dB / 1°, coupling
   0.5 dB / 1°, max |ΔS| 0.015).
4. *(Benchmark)* The three shipped runs (Launch on both solvers, Pair on openEMS) re-run and hold every recorded |S| to
   the file's `RegressionTolerance`, as `3D Connector`'s gate 6 does.

## 7. Owner check

The overview's §5 walk-through, the first two paragraphs.

## 8. Scope

- No new engine feature. Anything the example needs that 114 and 116 did not deliver is reported, not patched in here.
- No Palace setup on Pair (D14). If brief 119 later says go, a Palace setup is added then.
- `3D Connector` is untouched.

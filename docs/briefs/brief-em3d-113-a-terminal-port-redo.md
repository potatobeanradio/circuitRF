# Brief 113-a — the terminal-port spike, redone from the solvers' own documentation

**Series:** [3D EM, eighth series](brief-em3d-112-overview.md) · **Tag:** `R-em3d113a-n` ·
**Replaces:** [brief 113](brief-em3d-113-terminal-port-spike.md)'s results, not its questions ·
**Precedent:** the F0 spike (`docs/design/em-3d-f0-findings.md`), which built its openEMS inputs with openEMS's own
interface and so had the solver authors' code as its structural oracle
**Area:** a scratch harness (not committed); findings in `src/Design/RESOLVED.md`; fixtures in `testdata/em3d/terminal/`
**Depends on:** — · **Blocks:** 114, 115, 116

---

## 0. Why this brief exists

Brief 113 was run on 2026-10-05, and its findings are in `src/Design/RESOLVED.md` § "Terminal wave ports —
brief-em3d-113". **Those results are not to be built on.** Every setup in it was built from scratch: Palace's inputs from
reading Palace's C++ source, and openEMS's from imitating `CsxcadWriter`. Neither solver's user documentation or shipped
examples were consulted, although both solvers document and ship examples of almost exactly these setups. Where 113 got
a bad answer, it mostly invented a fix instead of asking how the solver's authors do it.

What went wrong, in short:

| 113 did | Why that was wrong | What the documentation does |
|---|---|---|
| Put both conductors in **one shared port rectangle**, with two Palace `WavePort` entries on it (modes 1 and 2, only one `Active`) | Palace's documentation shows no example of this. It produced overlapping modes on the stripline pair and a mismatched inactive mode on the microstrip pair. 113 then derived two correction formulas of its own to rescue it, and those formulas are unvalidated | Palace's `examples/cpw` (two coupled lines, four ports, near-end and far-end crosstalk) gives **each line its own port rectangle**, one mode each. The two rectangles on an end face touch at the midline between the lines, and the rest of the end face is PEC |
| Meshed by extruding a 2D cross-section in long layers (2 mm elements beside 20 µm strips) | 100:1 element aspect ratios stalled GMRES (170–350 iterations) and pushed runs to 5–16 minutes | Palace's examples mesh with Gmsh size fields on an ordinary tetrahedral mesh (`examples/cpw/mesh/mesh.jl`) |
| Wrote `VoltagePath` from ground to signal | The opposite of Palace's documented polarity convention | `docs/src/guide/boundaries.md`: signal terminal first, ground second (`examples/coaxial/coaxial_lumped_wave.json` does exactly this) |
| Built openEMS ports as one line source plus one voltage probe and one current probe in the same plane | The current probe sits half a cell off the voltage plane, and a line source launches a field that needs a long feed to clean up. 113 measured both and blamed the solver | openEMS's line ports (`AddCoaxialPort`, `AddStripLinePort`, `AddMSLPort`, and the Python `CoaxialPort`, `StripLinePort`, `MSLPort`) excite the line's own transverse field profile, place **three voltage probes and two current probes**, average the current onto the voltage plane, compute β and Z_L from the probes, and shift to the reference plane |
| Ran the coax on a Cartesian grid and reported it 6 % slow, a failure of brief 116's gate | openEMS documents a cylindrical-coordinate coax for exactly this (`matlab/examples/waveguide/Coax_CylinderCoords.m`), and 113 never tried it | Start from `Coax.m` and `Coax_CylinderCoords.m` |
| Boxed the openEMS stripline pair in PEC side walls 3b away | That box has a mode cutting off near 9.4 GHz, which forced a 24 mm feed and still missed | openEMS's `Stripline.m` uses PML on the line's ends and PMC on the sides (a symmetry plane), not a PEC box |

**What 113 got right and the redo may carry forward,** each to be re-confirmed by one cheap run:

- `V_wp` in `port-V.csv` does not move with `Offset`, while Palace's S does (2kₙd).
- On a single-mode port, |T_V|² = `Z_PV`, so T_I = (T_Vᴴ)⁻¹ with factor 1 (Palace's ∫E×H*·n = 1 peak convention, stated
  in `postoperator.cpp`).
- `FdtdPortTransform.Solve` turns N probed ports into S unchanged.
- openEMS drops a zero-thickness source or probe that is not exactly on a grid line, with only a warning.
- Reciprocity alone does not show a wrong port result; passivity (largest singular value of S) does.
- Cohn's formula is no reference for strips of real thickness: at t/b = 0.01 it is 0.66 dB off on near-end coupling. The
  reference is a 2D field solve of the drawn cross-section (113's README gives A's values).

**Already done (2026-10-05), before this brief starts:** 113's `RESOLVED.md` section is marked superseded, 113's
`testdata/em3d/terminal/` fixtures are deleted (115/116 must never replay them), and the overview's D6, D7 and D12 rows
say "pending 113-a" with 113's added rows D14, D15 removed. This brief rewrites those rows from its own results at the
end.

---

## 1. The documentation this redo is based on

Read these **before writing any input**. Use the pinned versions: Palace **v0.18.1** and openEMS **v0.37.0-rc3**. The
paths below are inside each project's source tree at that tag. The web versions are
<https://awslabs.github.io/palace/stable/> and <https://docs.openems.de/>, plus the openEMS wiki at <https://openems.de/>.

### Palace (Apache-2.0)

| Source | What it settles |
|---|---|
| `docs/src/guide/boundaries.md`, *Wave ports* | Wave ports are planar and one-sided. **Touching wave ports treat each other's edges as PEC** in their mode solves, which is what makes adjacent per-line rectangles work. The `VoltagePath` / `PolarityAttributes` polarity runs signal → ground. `WavePortPEC` exists |
| `docs/src/examples/cpw.md` + `examples/cpw/` (`cpw_wave_uniform.json`, `mesh/mesh.jl` `generate_cpw_wave_mesh`) | **The template for the pair.** Two lines with four wave ports, one `Mode` 1 each. Each end face is split at the midline between the lines; the rest of the end face is PEC, the outer box absorbing. Order 2, GMRES 1e-8, and published plots of S11, S21, S31, S41 over 2–32 GHz to reproduce |
| `docs/src/examples/coaxial.md` + `examples/coaxial/` (`coaxial_lumped_wave.json`, `coaxial_matched.json`) | A coax wave port, its `VoltagePath`, and mixing with a lumped port |
| `docs/src/config/boundaries.md` (`WavePort` keys) and `docs/src/guide/postprocessing.md` | What `port-S.csv`, `port-V.csv`, `port-Z.csv` contain |
| The config schema (`scripts/schema/config-schema.json`) | Every key's legal values and defaults |

### openEMS (GPL: read and learn, never copy into `src/` or `tools/`)

| Source | What it settles |
|---|---|
| `matlab/AddCoaxialPort.m`, `AddStripLinePort.m`, `AddMSLPort.m`, `calcTLPort.m`, `calcPort.m`; `python/openEMS/ports.py` (`CoaxialPort`, `StripLinePort`, `MSLPort`) | **How a transmission-line port is built and evaluated:** the excitation's field profile, `FeedShift`, `MeasPlaneShift`, the three voltage and two current probes, current interpolation, β and Z_L from the probes, `RefImpedance` |
| `matlab/examples/waveguide/Coax.m`, `Coax_CylinderCoords.m`, `Coax_Cylindrical_MG.m` | A coax on a Cartesian grid and on a cylindrical one |
| `matlab/examples/transmission_lines/Stripline.m`, `MSL.m`, `directional_coupler.m` | A single stripline (PML ends, PMC sides), a microstrip, and a four-port coupler |
| `matlab/Tutorials/StripLine2MSL.m`, `MSL_NotchFilter.m`; `python/Tutorials/StripLine2MSL.py`, `MSL_NotchFilter.py` | Port placement and feed/measurement shifts as the authors choose them |

**Upstream issue trackers** may be read, never posted to. Search Palace's for multi-conductor or multi-mode wave ports,
and openEMS's for coupled-line or multi-strip ports. Record what is found, with links.

---

## 2. Method rules

1. **Reproduce first.** Each solver's work starts by running the upstream example unchanged (or at its own coarser
   refinement) and matching its published or self-computed result. Only then change one thing at a time toward this
   series' geometry. Every departure from the example is written down with its reason.
2. **openEMS inputs are generated by openEMS's own Python interface in the scratch harness**, as F0 did. Upstream's
   `calcPort` is then the oracle for U, I, β, Z_L and S. Only after that is a hand-written, `CsxcadWriter`-shaped
   equivalent built and shown to give the same probe data. These scripts stay in the scratchpad and are never committed
   (the GPL boundary, em-3d.md §5.2).
3. **No invented corrections.** If a documented setup gives a wrong answer, record the measurement and stop that line of
   work, then report it with the documentation searched. A new formula is the owner's decision, not the spike's.
4. **Runs stay short:** each run under 5 minutes, and about an hour of solver time in total. A run that would be longer
   is shrunk (coarser mesh, one frequency, a shorter line) or asked about first.
5. Every number goes into `RESOLVED.md` with its geometry, mesh/grid size, solver version, wall clock and memory.

---

## 3. `R-em3d113a-1` — Palace

**a. Reproduce `examples/cpw` (wave ports, uniform sweep, `cpw_wave_0.msh` or the example's own coarse refinement).**
Compare |S11|, |S21|, |S31|, |S41| against the published `cpw-p2-*` plots at the sampled frequencies, and record the
time and memory. This checks the install, the reading of the example, and the cost of a four-port order-2 run on this
machine.

**b. The coax:** reproduce `examples/coaxial` (matched, wave port). Then put the `3D Connector`'s coax (pin ⌀ 0.4 mm,
bore ⌀ 1.34 mm, εr 2.1, 50.06 Ω) on the same setup: `Z_PV` against the closed form, ∠S21 against −βℓ, and
re-confirm |T_V|² = `Z_PV` and the `Offset` result (§0).

**c. The pair, the documented way.** Use `generate_cpw_wave_mesh`'s construction: one port rectangle per line, the end
face split at the midline, one mode each, `VoltagePath` signal → ground. Run it on:

- **A**, 113's stripline pair: air, ground planes b = 2 mm apart, W = 1.2 mm, S = 0.4 mm, t = 20 µm, ℓ = 15 mm. Its
  reference is the 2D field solve of the cross-section (Z₀e 101.95 Ω, Z₀o 70.885 Ω, with side walls 6 mm beyond the
  strips; re-solve it if the redo's side boundaries differ).
- **B**, 113's microstrip pair: εr 3.5, h = 0.508 mm, W = 1.1 mm, S = 0.3 mm, t = 17 µm, ℓ = 15 mm. Its references are
  circuitRF's planar quasi-static extractor (`RlgcExtractor` + `ModalDecomposition.Decompose`, open microstrip) and
  Palace's own single-line results.

Then **sweep the spacing S** (for example 4×, 2×, 1× and 0.5× the strip width) and report the error against the
reference at each one. The question is *how tightly coupled two lines can be before a face split between them stops
being accurate*. Report the spacing at which the error crosses 0.05 dB / 0.5°.

**d. Only if (c) fails at the coupling the `3D Wave Ports` example needs:** look at what the documentation and the
issue tracker say about several modes on one shared face (`Active`, `Mode` > 1). Run that setup **only as written in
Palace's documentation**, and report it. Do not derive a correction for it.

**Go/no-go for 115:** brief 115 is rewritten around per-line ports if (c) meets **0.05 dB / 0.5°** on A at the example's
spacing and the passivity residual σ_max is ≤ 1.002. If it meets that only at wider spacings, the example's geometry
changes to one of those spacings, and 115 refuses tighter faces with a sentence that says why. If it fails everywhere,
stop and report to the owner before 114.

---

## 4. `R-em3d113a-2` — openEMS

**a. The coax.** Run `Coax.m`'s setup through the Python interface (Cartesian), then the cylindrical-coordinate version.
Do both for the `3D Connector`'s coax, using `CoaxialPort` as upstream builds it. Report Z_L and β from upstream's own
port calculation against 50.06 Ω and k₀√2.1, on each grid, with cells and time.

**b. One stripline.** Run `Stripline.m`'s setup (PML ends, PMC sides) with `StripLinePort`, at A's single-strip
dimensions. Report Z_L against the 2D field solve.

**c. The pair.** Run two `StripLinePort`s per end face, one per strip, each with its own probes, every port excited once
(no symmetry fill this time). Choose the side boundaries the way the examples do, and justify them. Compare with Palace's
(3c) result and with the 2D reference at the same spacings.

**d. `FeedShift` and `MeasPlaneShift`:** use the tutorials' values first, then measure how much the result moves when each
is halved and doubled. That sensitivity is the rule brief 116 adopts.

**e. circuitRF's half.** Feed upstream's probe files to `FdtdPortTransform.Solve`:

- once as upstream lays them out, with the current interpolated to the voltage plane as `calcTLPort` does;
- once without that interpolation.

The difference is what brief 116 must add to circuitRF. Then hand-write the `CsxcadWriter`-shaped XML for (a) and (c),
and show that it gives the same probe data as the Python-generated XML, or say exactly where it differs.

**f.** Re-run 113's current-box-on-the-shield case using upstream's coax port, so that 116's refusal sentence describes
real behaviour.

**Go/no-go for 116:** go if the coax is within **0.5 Ω and 1°** on at least one grid circuitRF could write, and the pair
is within **0.1 dB / 1°** of Palace (3c). If the coax passes only on a cylindrical grid, report what `CsxcadWriter` would
need, as an owner decision.

---

## 5. Deliverables

- `src/Design/RESOLVED.md`: a section "Terminal wave ports — brief-em3d-113-a", headed by the list of documentation
  read, then every number as §2.5 asks, and the go/no-go for 115 and 116. Add the one-line supersession note under 113's
  section.
- `testdata/em3d/terminal/`: 113's files removed and replaced by this brief's go runs. For Palace, the configs,
  `port-S.csv`, `port-V.csv`, `port-Z.csv` and logs. For openEMS, the XML and probe files of the coax and the pair. Plus a
  README naming the upstream example each case descends from and every departure from it. **No personal paths, no
  meshes.** Shorten the solver launch line in logs as `testdata/em3d/eigen/wr90/palace.log` does.
- The overview: §3 rows rewritten from these results, each marked as measured, and §1b–1g corrected where the
  documented approach replaces them (in particular §1b–1d, which assume a shared face).

## 6. Scope

- No product code, no UI, no file-format change. Only the scratch harness and the files in §5.
- Upstream scripts and anything derived from openEMS's code stay in the scratchpad.
- Nothing is posted upstream.

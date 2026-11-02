# Brief 116 — openEMS: wave ports by transmission-line probes

**Series:** [3D EM, eighth series](brief-em3d-112-overview.md) · **Tag:** `R-em3d116-n`
**Rests on:** [brief 113-a](brief-em3d-113-a-terminal-port-redo.md), `src/Design/RESOLVED.md` § "Terminal wave ports —
brief-em3d-113-a" (R-em3d113a-2a…2f), and its fixtures in `testdata/em3d/terminal/openems/`
**Area:** `src/Design/Em3d/CsxcadWriter.cs` (ports ~255–281, the PML continuation ~31, the head ~501,
`ExcitationProperty` ~567), the FDTD grid (`FdtdGrid*`: fixed lines at the probe and source planes),
`src/Design/Em3d/OpenEmsRun.cs` (reading a terminal's probes), `src/Design/Em3d/Em3dRunService.cs` (`PalaceOnlyRefusal`
~105–122), `src/Engine/Em3d/FdtdPortTransform.cs` (unchanged, gate 7), `src/Cli/` (`explain`), `src/Ui/` (two tips,
the FDTD grid overlay), `docs/user/src/reference/` (`em-setup.md`, `em-3d.md`, `em-solvers.md`)
**Depends on:** 113-a (go for openEMS). The single-terminal half needs nothing else; the multi-terminal half needs 114 ·
**Blocks:** 117, 120

---

## 0. What this brief delivers

A wave port on openEMS, from a `.c3d` or a `.cem`, for any port region met by **two or more conductors**: a coax, a
microstrip, a stripline, and (with 114) a coupled pair with N terminals. The result is **terminal** S referred to each
terminal's own Z0, from the `FdtdPortTransform.Solve` every openEMS lumped port already uses.

**Terminal ports are openEMS-only in this series** (overview D14). A multi-terminal port on Palace stays refused, with a
sentence pointing to openEMS (114's refusal, reworded here, §3). A one-terminal wave port runs on both solvers.

Today's refusal (`Em3dRunService.PalaceOnlyRefusal`: "only Palace builds wave ports in this version") is lifted for those
ports. It stays, reworded, for a hollow waveguide (overview D10).

**How it works** is how openEMS's own authors build a transmission-line port (`ports.py`'s `CoaxialPort`,
`StripLinePort`, `MSLPort`; `calcTLPort.m`), which 113-a reproduced and measured. A terminal is fed from behind on a
uniform continuation of its line. Its voltage is measured on three planes around the reference plane and its current on
the two half-cell planes between them; the current is averaged onto the middle plane. S comes from **all** the runs at
once. **No mode is computed.** openEMS is GPL: everything below is written from the physics and the measurements,
never from its code (em-3d.md §5.2).

---

## 1. `R-em3d116-1` — the feed extension

**`R-em3d116-1a`** For each air-box face carrying a wave port, the lowering grows the box outward on that side by the
**feed length**. Every solid that **crosses the face**, conductor or dielectric, inside the port's rectangle or not, is
extruded through the extension. That is the treatment `CsxcadWriter` already gives a solid reaching an absorbing face
(~31), extended before the PML begins, so the extension is a uniform continuation of the face's own cross-section.

**`R-em3d116-1b`** **The feed length.** The source-to-reference-plane distance is at least **4.5 × s_max** and at least
**10 cells**, where s_max is the largest distance on the face from a terminal conductor to its reference. That is
113-a's smallest tested distance (R-em3d113a-2d: on the stripline pair, source-to-plane distances from 4.5 to 18 mm,
with 1 mm from strip to ground, moved every entry by ≤ 0.001 dB / 0.01°). Shorter was not measured, so it is not
offered. The source sits just inside the PML's inner edge, where upstream's ports put it, so its backward wave goes
straight into the absorber.

**`R-em3d116-1c`** The face is lowered **absorbing (PML)** (overview D9). When the setup stated another kind for it, the
run's notes say so: "The xmin face carries wave port 'P1', so openEMS terminates it in PML behind a 4.5 mm feed; the
setup's PEC applies to Palace only."

**`R-em3d116-1d`** **Side walls.** 113-a traced brief 113's need for a long feed to its PEC side walls: the box they make
has a mode near the band, which the source excites and which carries voltage but no strip current. With the examples'
PMC or absorbing sides the effect is absent. So when the faces **beside** a wave port are PEC, the lowering estimates the
lowest cutoff of the empty cross-section (c / (2·w·√εr_max), w the face's larger side). If that cutoff lies below 1.5 ×
the band's top, the run notes it and names the fix: make those faces PMC or Absorbing. This is not a refusal.

**`R-em3d116-1e`** The grid gets fixed lines at the source plane and at the **three voltage planes**: the reference
plane (face + `Offset`) and one uniform cell either side of it. The extension is gridded uniformly along the line
between the source and the plane after the reference.

**`R-em3d116-1f`** Two wave ports on one face are allowed (two coax pins, say). Their regions must not overlap, as today,
and one extension serves the face.

## 2. `R-em3d116-2` — a terminal's elements

For each terminal (a one-terminal wave port is one terminal). Every probe and source coordinate is written as an **exact
grid line, or the exact midpoint of two written lines, computed from the written values**. 113-a found two silent
traps: a zero-thickness source off its line is dropped with only `Warning: Unused primitive`, and a current-loop edge
on an unresolved tie between two lines snapped into the strip and read 55 % of the current.

**`R-em3d116-2a` The source** (in that terminal's run file only, as the lumped excitation is today), a soft E-field
`Excitation Type="0"` in the source plane, shaped to the line's own field so that little evanescent field reaches the
reference plane. The terminal's geometry on the face decides the shape:

| The terminal is | Source | As upstream builds it |
|---|---|---|
| **coaxial**: a conductor its reference surrounds | a radial field ∝ 1/ρ over the annulus between them, written as a **weight expression** (CSXCAD's `<Weight X="…" Y="…">`, fparser syntax), Cartesian components of the radial field masked to the annulus | `CoaxialPort` |
| **a strip between two reference planes** (stripline) | two sheets across the strip's width, strip to each plane, the fields pointing away from the strip, flat weight | `StripLinePort` |
| **a strip over one reference plane** (microstrip, CPW-backed) | one sheet across the strip's width, strip to the plane, flat weight | `MSLPort` |
| anything else | a sheet along the terminal's voltage path, flat weight, and a run note that the feed may need to be longer | (113's line source; measured worse) |

Brief 120 adds the cylindrical-grid form of the coaxial source (`1/rho`). Every existing source keeps its flat weight,
so existing files are byte-identical.

**`R-em3d116-2b` Voltage probes** `port<k>_ua`, `port<k>_u`, `port<k>_uc`: `ProbeBox Type="0"` along the terminal's
voltage path, on the three voltage planes, with `port<k>_u` on the reference plane. The sign is the lumped port's.
**For a strip between two reference planes**, each plane carries two half-weight paths, strip to each plane, written as
weight-1 probes `…_up`/`…_dn` that `OpenEmsRun` averages (`CsxcadWriter` writes integer weights). The symmetric average
cancels the parallel-plate mode, which a stripline with PMC or absorbing sides also supports and which an asymmetric
probe would read.

**`R-em3d116-2c` Current probes** `port<k>_ia`, `port<k>_ib`: `ProbeBox Type="1"`, normal to the face, on the two
half-cell planes between the voltage planes. Each box is the terminal conductor's cross-section grown by at least one
cell, edges on dual-grid lines. The sign is chosen so I flows **into** the device. **Every point of the contour must lie
in dielectric, with one cell to spare from any other conductor**, corners included (√2 × the half-width for a square
around a round pin). 113-a R-em3d113a-2f measured what happens otherwise: at a coax bore a square box reads 109 Ω when
its corners cut the shield, 283 Ω when its sides touch it, and an open circuit when it lies in the metal, with no
warning. A box that cannot be placed is refused before openEMS runs (§3).

**`R-em3d116-2d` Reading them** (`OpenEmsRun`). Per terminal and run: **U** = `port<k>_u` (or the mean of its two
halves), **I** = the sample-wise mean of `port<k>_ia` and `port<k>_ib`, which share one time column (a run where they do
not is an error naming the files). The pair goes to `FdtdPortTransform.Solve` as today, **all runs at once**, so
S = (U − Z₀I)(U + Z₀I)⁻¹. 113-a measured both halves of that sentence:

- without the current average (one plane, half a cell off), every phase moves by about 0.75° at 5 GHz on the pair;
- built a column at a time from each port's own incident wave, as a single openEMS port's calculation does, a coupled
  pair's S is impossible (|S21| −0.006 dB beside |S11| −11 dB), because a PML-ended pair terminates each strip in a
  2×2 characteristic matrix, not in Z0. Upstream's own four-port example (`directional_coupler.m`) assembles the
  matrix, as `Solve` does.

**`R-em3d116-2e` The line's own Z and ε_eff.** From the three voltage planes and two current planes, the reader also
forms the line's characteristic impedance and propagation constant at the reference plane (telegrapher's equations:
from E, dE/dz, H, dH/dz, β = √(−E′H′/(EH)) and Z = √(EE′/(HH′))). The run reports them per terminal: "Terminal P1:
the line measured Z 50.4 Ω, ε_eff 2.20." On a Cartesian grid this is what shows a staircased coax running slow (113-a:
ε_eff 2.20 for εr 2.1), and brief 120's note builds on it. For a coupled terminal these are the values seen with the other
terminals passive. The report says so and does not compare them with anything.

## 3. `R-em3d116-3` — refusals and reports

- **Hollow waveguide** (the region meets one conductor): "Port 3 is a wave port met by one conductor (a hollow waveguide).
  openEMS needs a mode-matching port for that, which circuitRF does not build yet; run it on Palace." The remedy names the
  setup field and `--solver palace`, as today.
- **A multi-terminal port on Palace** (D14, replacing 114's 2e sentence for Palace): "Port 'Left' has two terminals;
  terminal wave ports run on openEMS only in this version. Set the setup's solver to openEMS." `Both` runs it on openEMS
  and says Palace was skipped for it.
- **A current box that cannot be placed** in dielectric with a cell to spare: the sentence names the terminal, the
  conductor it would touch, and the clearance needed in cells and in the display unit.
- **Feed too thin**: only possible if a later edit makes the feed configurable; refused, naming both distances.
- `explain` prints, per wave port on openEMS: the feed length and how it was set, the source plane and source shape, the
  three voltage planes, each terminal's current-box planes and clearance, and the side-wall note of 1d when it applies.
- Eigenmode stays Palace-only (FDTD has no eigensolver); that refusal is unchanged.

## 4. `R-em3d116-4` — Both

A `Both` setup with single-terminal wave ports runs on both solvers. Their results are comparable because both are
referred to **the same reference plane** (face + `Offset`) and **the same Z0** (overview rule 2): Palace through brief 23's
renormalisation, openEMS through probes. Multi-terminal ports run on openEMS only (§3).

## 4a. `R-em3d116-5` — UI

- **Text that says "Palace" goes:**
  - `EmSetupEditorViewModel.Port3DKindTip` (`EmSetupEditorViewModel.Eigen.cs` ~92, "…and only Palace builds one") says
    both solvers build one, except that openEMS cannot feed a hollow waveguide and Palace cannot give a port with
    several terminals;
  - the `.cem` port table's Offset tip (`EmSetupEditorView.axaml`, "Palace de-embeds the line between") says the
    reference plane is where both solvers measure.

  Grep `src/Ui` for any other wave-port text naming one solver.
- **The feed extension is visible.** The 3D view's FDTD grid overlay, shown for an openEMS setup, draws the extension
  beyond the air-box face (the grid continues through it) and marks the source plane and the reference plane, so the
  source has a visible place and the grown box is not a surprise in the cell count. The air box itself is drawn as today,
  because the extension belongs to the lowering, not the document.

## 4b. `R-em3d116-6` — user docs (sources only; overview §2a)

Edit `docs/user/src/` only. Do **not** run DocGen or regenerate `docs/user`; the owner does that.

- `em-setup.md` **#wave-ports**:
  - the sentence "Palace only; a setup naming openEMS or both solvers is refused, and so is its `check`" is replaced by
    *On openEMS*, which says:
    - the line is fed from a short extension behind the face, with a source shaped to the line;
    - the face is absorbing for openEMS whatever the setup says (D9);
    - voltage and current are measured around the reference plane, so the result is referred to Z0 directly, with no
      renormalisation;
    - the run reports the line's own Z and ε_eff;
    - a hollow waveguide is refused (D10);
    - PEC side walls close to the line are best avoided, and why (1d).
  - `OffsetUm`'s bullet says both solvers use the same plane.

  **#wave-port-terminals** gains its *On openEMS* paragraph (each terminal is fed and measured on its own; S comes from
  all the runs together) and says that **Palace does not run a port with several terminals**, and why, in one sentence:
  its port modes come from the face, and a face split between lines represents only the odd mode.
- `em-3d.md` ~129: "A **wave port** (`Ports3D`, Palace only)" loses "Palace only" and gains the two exceptions.
- `em-solvers.md`: the capability table (~169), where openEMS's column reads "lumped and wave ports, including several
  terminals on one face"; and wherever the page contrasts the two solvers on ports, one sentence on how each builds a
  wave port (a port mode on the face, against a fed and probed line).
- `drawing-in-3d.md`: any wave-port sentence naming Palace alone.

## 5. Gate

`tests/Ui.Tests/Em3d/OpenEmsWavePortTests.cs`.

1. **Lowering, coax, no solver.** A coax wave port's XML: the box grown by the feed on its face, the pin, PTFE and shield
   extruded through it, PML on that face, the weighted radial source in run 1 only, three voltage probes and two current
   probes at the planes of 1e, the current box clear of the shield by a cell at its corners. Every source and probe
   coordinate is an exact written grid or dual-grid value. Golden committed.
2. **Lowering, stripline pair, no solver.** With 114: four terminals, two-sided sources and up/down voltage halves.
   **Structural comparison** with 113-a's `testdata/em3d/terminal/openems/pair-a/exc*/model.xml` (written in this
   writer's shape and replayed against openEMS's own interface to 3e-12): the same element kinds, attributes and
   placement rules.
3. **Replay, stripline pair** (113-a's `pair-a` probe files through `OpenEmsRun`'s readers and `FdtdPortTransform`):
   S equal to 113-a's recorded matrix (S21 −1.3013 dB / −89.029° at 5 GHz) to 1e-3 dB and 0.01°.
4. **The pair against its reference**: gate 3's S against the ideal coupled line from 113-a's 2D field solve of that
   cross-section (Z₀e 104.08, Z₀o 73.58 Ω; t = 0, PMC walls), at 50 Ω: **thru within 0.1 dB / 1°; near-end and far-end
   within 0.5 dB / 1°; max |ΔS| ≤ 0.015**. 113-a measured 0.057 dB, 0.37 dB and 0.11 dB, with phases ≤ 0.03° and max
   |ΔS| 0.0115. The remaining error is the grid's impedance (0.7 % low at 8 cells per strip height), magnified by a
   50 Ω reference on an 88 Ω line. Cohn's formula is not the reference: it is the t → 0, infinite-width limit.
5. **Refusals**: a hollow waveguide on openEMS; a multi-terminal port on Palace; a current box touching a second
   conductor. All before openEMS or Gmsh starts.
6. **Byte identity**: every existing openEMS golden (`testdata/em3d/openems-goldens`) with lumped ports is unchanged.
7. **`FdtdPortTransform` untouched**: its file has no diff (a source scan, as other gates in this repo do).
8. **Real run, coax** *(openEMS; Benchmark if over 5 s)*: the coax end to end through `circuitrf em` on a Cartesian grid
   at r_i/10. Z within **0.6 Ω of 50.021 Ω** (the closed form with η₀; 113-a measured 50.39–50.56 Ω at r_i/10, the
upper end at 2 GHz, and 50.39–50.43 Ω at r_i/20), and the run report
   carries the line's measured ε_eff. **The 1° phase criterion belongs to brief 120's cylindrical grid**: on a Cartesian
   grid 113-a measured the line 1–2.4 % slow (D15).
9. **Real run, pair** *(Benchmark if over 5 s; needs 114)*: gate 4's stripline pair drawn as a `.c3d`, end to end, within
   gate 4's tolerances against the same 2D reference.

## 6. Owner check

- Run the 3D Connector's launch with a wave port on the coax end on openEMS (the example itself is brief 117's), and
  read the feed note and the line's measured Z and ε_eff in the run report.

## 7. Scope

- No mode-matching port, no HDF5 mode files (overview §1f). No hollow waveguide on openEMS (D10).
- No cylindrical grid (brief 120).
- No change to openEMS lumped ports or to Palace.

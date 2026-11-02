# Brief EMW-2 — Single-terminal wave ports on EMerge

**Tag:** `R-emw2-n` · **Series:** EMW (see `brief-emw-0-overview.md`) · **Depends on:** EMW-1 (its findings are
`src/Design/RESOLVED.md` § "EMerge wave ports — EMW-1": read it first, every rule below cites it), EMI-4, EMI-5, EMI-7
**Area:** `src/Design/Em3d/Solvers/EMerge/` — `EmergeLoweringRules.cs`, `EmergeScriptWriter.cs` / `.Emit.cs`,
`circuitrf_emerge.py`, `EmergeResults.cs`, `Em3dRunService.Emerge.cs`; `RenormaliseWavePorts` and `WaveNote` out of
`Em3dRunService.Palace.cs` into shared code; `src/Cli/ExplainEm3d.cs`; `check`; EMI-6's Compare greying; docs. Findings in
`src/Design/RESOLVED.md`.
**Out of scope:** a wave port with several terminals (EMW-3: still refused on EMerge here), a hollow waveguide (refused,
naming Palace; §6), the example's EMerge setups (EMW-4).

---

## 0. What EMW-1 settled, and this brief therefore does not re-measure

- **The port is `WavePortIH`**, EMerge's full wave-port condition: on a lossless microstrip its raw S was lossless
  (σ 1.0000/0.9998) where `ModalPort`'s plain Robin term lost 0.9 % of the power. 1.7× the solve time, 1.5× the memory.
- **Z_PV is the mode's own `Z0`** once the port's integration line is set to circuitRF's voltage path (to 1e-4). Never
  read β or Z₀ from the result's port properties (they repeat mode 1's for every mode), and never read `CoaxPort.portZ0`
  (the medium's wave impedance).
- **EMerge's S post-processing is switched off**: `settings.mw_cap_sp_single = False`, `mw_cap_sp_col = False`.
- **The duplicate-mode guard is required** even on one-mode faces, because a face's polarity reference and EMW-3's
  multimode faces both read the stored list: forget a frequency's stored modes before EMerge solves them again.
- **Mesh sizing** goes through `mesher.set_global_max_size` / `set_curved_boundary_meshing`, never `gmsh.option`
  (`generate_mesh` overwrites it). The coax read 47.25 Ω with curvature sizing at its default and 49.97 Ω at 20.

## 1. `R-emw2-1` — the lowering

`EmergeLoweringRules.KindRefusal` stops refusing a wave port whose `FaceGroup` is null (one terminal). problem.json gains
a `wavePorts` array (and `ports` keeps the lumped ones):

| Key | What |
|---|---|
| `number`, `name` | as a lumped port's |
| `face` | the air-box face key (`xmin` … `zmax`) the region lies on |
| `origin`, `u`, `v` | the region's rectangle on that face, metres |
| `path` | `{from, to}`: `Em3dPort.VoltagePath`, metres, negative object to positive, as circuitRF states it |
| `z0` | the reference the S is renormalised to (real, positive: §6) |
| `shiftM` | `ReferencePlane.ShiftM`, positive into the structure (`em-setup.md` § Wave ports, `OffsetUm`) |
| `mixed` | whether more than one material meets the region, decided here from the solids the rectangle touches, never in the driver |

`FormatVersion` goes to 2 (the driver refuses 1, and 1 refuses 2, with the existing sentence). The three EMI-4 goldens
change by the one key `"wavePorts": []` and nothing else. **A new golden** is the 3D Wave Ports *Launch*: wave port P1 on
xmin, lumped port P2 on the board.

## 2. `R-emw2-2` — the driver (version 5)

- **The region is a surface of its own on the air-box face.** Build it as a lumped port's plate is built
  (`GeoSurface(rectangle(...))`), on the face's plane; `commit_geometry` fragments it into the face. The face-boundary
  loop (`build`, "the air box's faces") then sees the region's fragment among the face's tags, so **exclude every port
  region's tags from the PEC / PMC / absorbing assignment** — a region left PEC is a short across the port. Check it on the
  Launch, whose region is a 2 mm square on a PEC face.
- **The port**: `sim.mw.bc.WavePortIH(region, number)`, then `set_integration_line(path.from, path.to, 101)`, and
  `mixed_materials` from `mixed`. Lumped ports keep `LumpedPort`. EMerge numbers both kinds in one S; the driver writes S
  rows and columns in ascending circuitRF port number, as it does today.
- **The guard**, installed once before the model is built: wrap `Microwave3D.modal_analysis` so it pops the k₀ of the
  frequency it is about to solve from `port.available_modes` (`k0 = 2π f / 299792458`, the expression EMerge uses).
  Popping is harmless if a later EMerge stops duplicating, so the guard stays on for every version. If the attribute is
  missing (a later EMerge renamed it), the driver adds a warning to the run, saying the guard could not be installed, and runs.
- **S corrections off** (§0).
- **Polarity, decided once (R-emw2-2a).** EMerge flips a mode so that its integral along the port's line is positive.
  circuitRF's path runs from the negative object to the positive one, the sense a lumped port's `Direction` has, so the
  two kinds should agree. **Prove it on a thru with a wave port at one end and a lumped port at the other** (§7, test 6):
  a reversed convention shows as S21 180° off −βℓ. If it is reversed, write the line from `to` to `from`; record which
  in RESOLVED and in a comment at the one line that decides it.

## 3. `R-emw2-3` — what the driver writes

- `Z0.npy` becomes `[f, ports]`, complex. A lumped port's column is its constant Z0. A wave port's column is its mode's
  `Z0` at that frequency (a face of one material solves its mode once, so the column is constant there too).
- `gamma.npy`, `[f, ports]`, complex: a wave port's propagation constant jβ (with its loss, if EMerge's β carries one), 0
  for a lumped port. It feeds the reference-plane shift (§4).
- **Under the adaptive sweep** the S comes from EMerge's model at the requested frequencies, which have no mode solve of
  their own. After the sweep the driver solves each wave port's mode at every requested frequency (2D, about 0.15 s per
  face) for Z0.npy and gamma.npy, the guard keeping each solve fresh. A face of one material needs none: its mode is
  solved once and β scales with k₀ for a TEM mode (EMerge's `get_beta`).
- **The second-mode check (R-emw2-3a)**, after the sweep: per wave port, a mode solve asking for two modes at the
  sweep's top frequency. A second mode that EMerge keeps (it drops evanescent ones) is reported in run.json, and becomes
  the warning Palace's run gives (`Em3dRunService.Palace.cs`, "supports a SECOND propagating mode"). Pop that solve's k₀
  afterwards. **Probe it before relying on it**: the Coupled Microstrip's housing carrying one strip, at a frequency above
  the housing's first box mode (find that cutoff with the same mode solve, then go 20 % above it). Record what EMerge
  returned, and the frequency.
- The field steps (EMI-7) need nothing new: their scale reads the port's `power` (1 W for both kinds) and their port
  voltage reads `voltage_integration_line`, both of which a `WavePortIH` has. Check one field step of the Launch draws.

## 4. `R-emw2-4` — the result: shift, then renormalise

`EmergeResults.ReadDriven` reads the new shapes and refuses any other shape, naming both. Then, per frequency:

1. **The reference-plane shift, in the mode's own normalisation**: S′ᵢⱼ = Sᵢⱼ · e^(γᵢdᵢ) · e^(γⱼdⱼ), d = `shiftM`, γ the
   port's own (0 for a lumped port). It must come first: renormalising mixes incident and reflected waves, and the shift
   is exact only on a matched line. That is also the order Palace's run has (its `Offset`, then circuitRF's
   renormalisation). EMW-1 matched ∠S21 to −βℓ within 0.04° on the coax and 0.07–0.2° on the microstrip.
2. **Renormalisation** from each port's per-frequency Z0 to its stated Z0: `RenormaliseWavePorts`, moved out of the Palace
   partial into shared code (`Em3dRunService.cs` or a small `WavePortReferences` beside it) and given a per-frequency
   source impedance. `EmergeResults.AtPortReferences` becomes a call to it. **A lumped-only run's files are byte-identical**
   to today's (the existing EMI-5 gates hold that).
3. The run's note is `WaveNote`, moved with it and taking the solver's name. EMerge's sentence says what Palace's does:
   the S was referred to the mode at unit power, here is each wave port's Z_PV range, renormalised to Z0. Plus one line
   per moved plane: `Port 1's reference plane is 0.5 mm into the structure: its line was de-embedded with the mode's β.`

## 5. `R-emw2-5` — check, explain, Compare

- `check` reports the refusals of §6 for an EMerge setup with no EMerge installed, as it does every other EMerge refusal.
- `explain`'s EMerge object rows gain the wave port: `a wave port, number 1: EMerge's full wave-port condition on the
  xmin face's 2 × 2 mm region, its mode solved once (one material) | per frequency (several materials); voltage path from
  (…) to (…); renormalised from the mode's impedance to 50 Ω; reference plane 0.5 mm into the structure`. The `mesh`
  row's "a port is not refined" stays true; say so for the region as well.
- EMI-6's Compare stops greying EMerge out for a setup whose only wave ports have one terminal. It still greys it out,
  with the §6 sentence, for a terminal group or a hollow waveguide.

## 6. `R-emw2-6` — what EMerge still refuses, by name

Each refusal follows EMI-4's wording rule: what was asked, why EMerge cannot, which solver can.

- **A wave port with several terminals**: `Port 'Left' has two terminals. EMerge runs wave ports with one conductor on
  the face in this version; openEMS runs terminal ports. Set the setup's Solver3D to OpenEms.` (Palace runs some,
  openEMS all: naming openEMS is always right.) EMW-3 lifts it.
- **A hollow waveguide** (a region met by one conductor): `WavePortIH` keeps TEM modes only, and EMW-1 did not measure
  `ModalPort`'s TE path. Name Palace. Use the Engine's own test for "met by one conductor": `FdtdWavePorts.IsHollow`
  is private today and `src/Engine` grants internals to its tests only, so make it public and call it. Do not copy it.
- **A complex or non-positive Z0** on a wave port: the renormalisation's target must be real and positive, as a lumped
  port's must.

## 7. Tests (targeted; one per claim)

1. **Goldens**: the three EMI-4 goldens with only `"wavePorts": []` added, and the new Launch golden, byte for byte.
2. **Refusals**: a two-terminal port, a hollow waveguide and a complex Z0, each refused with its sentence. A one-terminal
   wave port is not refused.
3. **Shift then renormalise, with no EMerge**, on `testdata/em3d/terminal/emerge-modal/coax.json`: the fixture's modal
   S, Z_PV and β through the shared code with d = 1 mm at both ends give ∠S21 = −β(ℓ − 2 mm) to 0.05°, and S at 50 Ω
   within 0.006 of the ideal line of Z_PV and β. Then the same with the shift applied after the renormalisation, to show
   the order matters (it moves S11 by more than the tolerance; if it does not, say so in RESOLVED and keep test 3 alone).
4. **A lumped-only EMerge run's files are byte-identical** to today's: the existing EMI-5 gates, unchanged and passing.
5. **The driver builds the Launch** (`EmergeFact`, mesh mode): the region is a `WavePortIH`, and the region's tags carry
   no PEC.
6. **Polarity** (`EmergeFact`, `Category=Benchmark`): EMW-1's case A (`tools/emerge-wave-spike/cases.py`, as a `.c3d`
   in a test workspace), with a wave port at xmin and a lumped port across the strip's end at xmax; ∠S21 within 20° of
   −βℓ (a reversed convention is 180° off).
7. **The Launch end to end** (`EmergeFact`, `Category=Benchmark`), a test workspace's copy of the 3D Wave Ports Launch
   with `Solver3D` Emerge at Draft: the coax's Z_PV within 1 % of 50.02 Ω, and \|ΔS\| ≤ 0.03 on S11 and S21 against
   Palace's recorded Draft run in `examples/3D Wave Ports/expected-numbers.json` at its five frequencies (Palace and
   openEMS differ by up to 0.026 there). Record the run's time, tets and peak memory in RESOLVED. **If it misses, do
   not widen the tolerance**: record EMerge's numbers beside Palace's Draft and Palace's element-order-2 values the
   example's README quotes (S11 −25.02 dB at 10 GHz, −21.44 dB at 18 GHz), since Draft is first order on Palace, and ask
   the owner.

## 8. Docs (sources only; the owner regenerates at the series' end)

- `docs/user/src/reference/em-solvers.md`: line 57's "wave ports need Palace or openEMS" (single-terminal ones now run on
  EMerge), the capability table's EMerge column (line 187) and the ports row (line 227), and the bullet "The two solvers
  build a wave port differently", which now has three solvers: EMerge solves the mode on the face as Palace does.
- `docs/user/src/reference/em-setup.md` § Wave ports: an **On EMerge** paragraph beside *On openEMS*. It solves the
  mode on the face like Palace, refers the S to it and is renormalised. The reference plane is shifted with the mode's β.
  Several terminals and hollow waveguides run on the other solvers.

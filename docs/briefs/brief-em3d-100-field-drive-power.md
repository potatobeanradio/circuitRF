# Brief 100 — A field plot's drive: the incident power it is read at

**Tag:** `R-em3d100-n` · **Series:** 3D field plots, follow-up to designer feedback round 11 (the "1 W incident" label).
**Area:** `src/Render/Scene3D/Fields/FieldRun.cs` (`FieldStep.OpenOpenEms`, `FieldRun.OpenOpenEms`),
`src/Render/Scene3D/Fields/FieldPlotResolver.cs` (`SolutionLabel`, `LegendLines`, `FieldPlotRequest`),
`src/Render/Scene3D/Fields/FieldModel.cs` (`FieldNames`: which arrays scale how), `src/Design/ThreeD/C3dFieldPlots.cs`
(`C3dFieldPlot`), `src/Ui/Viewer3D/FieldLayer.cs` + `Viewer3DViewModel.Plot.cs` (`RangeGroups`, the layer's sampled values),
`src/Ui/ThreeD/C3dPropertiesViewModel.FieldPlot.cs` (the Inspector), `src/Engine/Em3d/FdtdPortTransform.cs` (`Dft`, read only),
`src/Design/Em3d/OpenEmsRun.cs` (`ReadProbe`, read only), `docs/user/src/reference/em-3d.md` + `cli.md` (§7), `docs/design/cli.md` §13.8.1
**Depends on:** brief 96 (field layers, range groups), brief 87 (the run's own saved document) · **Blocks:** —

## Why

A driven field plot shows field strengths in V/m, A/m, W/m², and someone reading a breakdown margin, a current density
or a heating density needs to know **what drive those numbers are for**, and needs to set it to their own.

- **Palace has no power setting.** A port's only drive key is `"Excitation"`, and Palace normalises every excitation to
  unit incident power. Each port is its own excitation (`PalaceConfigWriter.cs:251`), so each saved field is "this port
  driven, every other port terminated in its own R". Round 11 put "with 1 W incident" in the picker label for this.
- **openEMS has no normalisation at all.** Its frequency-domain dump is a DFT of the response to a Gaussian pulse, so its
  magnitude at a frequency is set by how much energy the pulse has there. `FieldRun.OpenOpenEms` draws those dumps
  **raw**. An openEMS field strength in circuitRF today is therefore referred to nothing, and an openEMS and a Palace plot
  of the same structure disagree in magnitude by an arbitrary, frequency-dependent factor. The far-field path already
  divides by the driven port's voltage (`Em3dRunService.OpenEmsPattern`, `0.5 / u`); the field plot never did.
- **The problem is linear, so the drive is post-processing.** Every amplitude scales by √(P / P₀) and every quadratic
  quantity by P / P₀. Nothing needs re-solving, so this belongs on the **plot**, not in the analysis setup. A setup field
  would wrongly suggest it changes the solve, and editing one would mark a solved setup stale (brief 98) for nothing.
  `C3dFieldPlot` is already "display, not physics" (its file header, R-em3d83-2): `SerializeForRun` leaves plots out.
- **Incident power is available power.** For a port of real reference resistance R, the incident power |a|² is exactly
  what a source of internal resistance R makes available. What enters is |a|²(1 − |S_kk|²), with the other ports
  terminated. A user who wants "this many watts delivered" needs that second reading too (§3, D2).

## 0. `R-em3d100-0` — settle Palace's convention first, from evidence (blocking)

The committed fixtures say something the current label does not account for:

```
testdata/em3d/f0/B-via/palace/config.json   LumpedPort 1: "R": 50.0, "Excitation": true
postpro/port-V.csv   V_inc[1] = 7.071067811865 V   (= √50)
postpro/port-I.csv   I_inc[1] = 0.1414213562373 A  (= 1/√50)
```

So Palace's unit power is **V_inc·I_inc = |V_inc|²/R = 1**, with **no ½**. Two readings fit those numbers, and they differ
by a factor of 2 in power and √2 in every field strength:

- **(a) RMS phasors.** The 1 W is time-averaged, and the field Palace writes is an RMS amplitude.
- **(b) Peak phasors.** Palace's "1 W" is |V|²/R on peak values, so the time-averaged incident power is **0.5 W** and the
  current "1 W incident" label is wrong by 2×.

circuitRF's own convention is **peak phasors with ½·Re(V·I*)** (the planar kernel's 1 V delta gap, `Em3dRunService.cs:809`,
`OpenEmsPattern`'s `accepted = 0.5 * (c / u).Real`, and HB). Determine which reading Palace uses. Read Palace's own
documentation and source (Apache-2.0, readable; it is not GPL) for its time-harmonic field convention and its port
excitation normalisation. Cross-check against `domain-E.csv`'s `E_elec`/`E_mag` on a fixture if that is decisive.
Record the answer, with the file and line it came from, in `src/Design/RESOLVED.md`, and express it as **one constant**:
`PalaceDrive.IncidentPowerW` = the time-averaged incident power, in circuitRF's peak convention, of the field Palace
writes (1.0 under (a), 0.5 under (b)). Everything below reads that constant and never repeats the arithmetic.

If the evidence cannot settle it, **stop and report** rather than pick one. A drive control on a wrong base is worse
than the label it replaces.

## 1. `R-em3d100-1` — openEMS fields referred to their incident wave

At load, an openEMS step is scaled so it is the field of a port driven at **the same incident wave Palace's step
carries**, which is what makes the two solvers' plots comparable.

- For the step of port k at frequency f, read port k's own probes **in port k's run directory** (`p<k>/`,
  `CsxcadWriter.VoltageProbe(k)` / `CurrentProbe(k)`) with `OpenEmsRun.ReadProbe`. DFT them at f with
  `FdtdPortTransform.Dft`, the function S was formed with (each probe on its own time column, R-em3d9-4b). Do not write
  a second DFT.
- The incident wave is v_inc = (U + Z₀·I) / 2 with Z₀ the port's reference impedance, in the probes' convention. The dump
  is first halved into that convention (OpenEmsFarField's header: openEMS's FD dump carries a ×2), exactly as
  `OpenEmsPattern` does. The scale applied to the complex field is
  `0.5 / v_inc × √(2·R·PalaceDrive.IncidentPowerW)` (the peak incident voltage at that power, phase zero). Derive it once,
  in one function, with the convention written beside it.
- **Z₀ comes from the run's own saved document** (brief 87's `document.c3d` beside the run), never the open document. A
  port's Z₀ edited since the run would otherwise rescale an old field in silence.
- Every array an openEMS step carries (E, and H with `OpenEms.SaveH`) takes the same complex scale.
- Dividing by v_inc also fixes the phase reference to the incident wave, as Palace's is. The φ animation of an openEMS
  plot therefore starts at the same instant as Palace's.
- **A run whose probe files are missing** (deleted, or a partial run): D3.

## 2. `R-em3d100-2` — the plot's drive, in the document

Add to `C3dFieldPlot`:

- `DrivePowerW` (`double?`, watts, time-averaged, peak convention). **Null means the default** (D1), and it is omitted
  when null, so a `.c3d` written before this brief loads and re-serialises byte-identically.
- `DriveReferredTo` (`Incident` | `Accepted`, default `Incident`, omitted at default).

Both are display. Neither is read by `SerializeForRun`, by any lowering, or by `EmSnpProvenance`, so changing either
never makes a result stale.

## 3. `R-em3d100-3` — applying it

- **Per layer, never on the shared step.** Brief 96 shares one loaded `FieldStep` among every plot of a solution, so the
  scale is applied to the **layer's** sampled values (and its hover readout), never to the cached `FieldArray`. Two plots
  of one solution at two drives must both be right.
- **Which arrays scale how.** Add the exponent to `FieldNames` beside the unit, so one table says both:
  - amplitude, ×√k: `E`, `B`, `H`, `J_s`, `Q_s`, `V`, `A`;
  - quadratic, ×k: `S`, `U_e`, `U_m`;
  - never scaled: `Indicator`, `Rank`, `T_C`, and the port mode fields `E0_k` (a mode shape, not a driven field);
  - an array the table does not know: **not scaled**, and the legend says "not referred to the drive" rather than
    guessing.
- **k for `Incident`:** `DrivePowerW / PalaceDrive.IncidentPowerW` (both solvers, after §1).
- **k for `Accepted`:** divide that by (1 − |Γ_k|²) at the plot's frequency, with Γ_k the driven port's reflection:
  - Palace: from `port-V.csv`'s row at that frequency, Γ = (V − V_inc)/V_inc (the columns `PalaceRun.ReadPortV` already
    reads);
  - openEMS: from the same DFT'd U and I as §1, Γ = (U − Z₀I)/(U + Z₀I).

  **Never interpolate.** A frequency with no port row is refused for `Accepted`, by sentence, in the Inspector's error
  line ("Palace wrote no port voltage at 7.3 GHz, so the accepted power there is not known; Incident still works"). A
  plot with 1 − |Γ|² below 1e-6 (a port that accepts nothing) is refused the same way rather than scaled toward infinity.
- **dB.** One rule: the shift is 10·log₁₀(k) dB for every scaled array, since an amplitude's dB is 20·log of √k.
- **Range groups (brief 96 D2).** Add the effective k to `RangeGroups`' key. Two plots of one quantity and solution at
  different drives must not share a range: their numbers differ.
- Colours under an automatic percentile range do not change with k; the legend's numbers, the hover value and a fixed
  range's meaning do. That is correct, and no gate should expect a colour change from it.

## 4. `R-em3d100-4` — saying it

- **Picker label** (`SolutionLabel`): drop "with 1 W incident". The picker lists solutions, and the drive is the plot's,
  so it reads "2.4 GHz, port 1 driven".
- **Legend**: one new line under the solution line, from `LegendLines` (so the view, Export picture and `render --field`
  all print it):
  `Drive: 10 W incident (available) on port 1, Z₀ 50 Ω, peak` — or `… 10 W accepted (|S11| −14.2 dB) …`.
  "peak" says what the field magnitude means. RMS is 1/√2 of it, and the tooltip says so.
- **Inspector** (`C3dPropertiesViewModel.FieldPlot.cs`), only for a `Driven` solution, under the solution row:
  - **Drive power**: a value field taking W, mW or dBm (parsed as the Inspector's other quantity fields parse, and stored
    in watts). Its tooltip carries the available-power sentence from "Why".
  - **Referred to**: Incident (available) | Accepted.
  - Hidden for eigenmode, electrostatic, magnetostatic and thermal solutions, whose normalisation is their own and is out
    of scope.
- **`render --field <plot>`** reads the plot's record, so it honours the drive with no new flag. Say so in `cli.md`
  §13.8.1's `--field` paragraph.

## 5. Owner decisions already taken (2026-10-02)

- **D1 — the default drive is Palace's native power, `PalaceDrive.IncidentPowerW`**, so an existing Palace plot's numbers
  do not move. If §0 lands on reading (b), that default is 0.5 W, and the legend and the docs say 0.5 W: the label is
  corrected to the truth, not the numbers to the label.
- **D2 — `Accepted` ships in this brief**, with §3's refusal rule.
- **D3 — an openEMS run with no probe files is drawn, not refused.** Its legend reads
  `Drive: not referred, this run kept no port record (relative values)`, and the hover value has no unit. Today's raw
  numbers must not keep appearing as V/m.

## 6. Not in this brief

**Several ports driven at once** (a power and a phase each: differential or even/odd drive, combiners, array steering).
Linearity makes it a weighted sum of the per-port solutions, which every run already has, so it needs no re-solve. It
does need every port's step loaded at the frequency, a per-port table in the Inspector, and a layer that sums complex
arrays before sampling. Its own brief, if the owner wants it.

## 7. `R-em3d100-5` — user documentation (words only, no figures)

A short section in `docs/user/src/reference/em-3d.md` on the drive settings. **No images, no headless figure, no
`<!-- figure -->` comment.** Prose and a short list only. It is part of this brief's deliverable, not an afterthought.

- **Where:** in the *Field plots* bullet list (under "3D view"), add a **Drive** item after **dB and a range
  percentile**. **Replace** that item's last sentence ("A Palace driven field is the one **1 W incident** … and the
  legend says so"), which this brief makes wrong or incomplete. Then add a short paragraph, **What drive a field is shown
  at**, after the "Up to four plots are drawn at once" paragraph, with an anchor (`id="field-drive"`) so `em-setup.md`
  and `cli.md` can link to it.
- **What the Drive item says**, briefly:
  - *Drive power* is the power the field is shown at, in W, mW or dBm. It is set per plot and saved with it.
  - *Referred to* chooses **Incident**, the power a source matched to the port's Z₀ makes available, or **Accepted**,
    the power that actually enters the port after reflection, with every other port terminated in its own Z₀.
  - Shown only for a driven solution.
- **What the paragraph says**, in about 6 to 10 sentences:
  - The solve is linear, so the drive only rescales what is shown: field strengths by the square root of the power
    ratio, power and energy densities by the ratio. Changing it never needs a re-run and never marks a result stale.
  - The default is the power the solver's own field is at (state §0's number and the convention as §0 settled them).
  - Colours under an automatic range do not change with the drive; the legend's numbers and the value under the cursor
    do.
  - Magnitudes are **peak**. RMS is 1/√2 of them.
  - *Accepted* is refused at a frequency where the run has no port record, and the Inspector says why.
  - openEMS fields are now shown at the same drive as Palace's, so the two solvers' plots of one structure compare
    directly. An openEMS run that kept no port record draws in relative values, and its legend says so.
  - Each port is driven on its own, with the others terminated. Driving several ports at once is not offered.
- Follow the page's existing voice: second person, present tense, no "simply"/"just", and no promise of later work.
- In `docs/user/src/reference/cli.md`'s `render --field` entry, one sentence: the plot's drive is applied, there is no
  flag for it, and a link to `em-3d.html#field-drive`.

## 8. Gates (headless; counters and records, no pixels seen)

1. **§0, from the fixture:** on `testdata/em3d/f0/B-via/palace`, |V_inc|·|I_inc| = 1 within 1e-9 and |V_inc| = √R. The
   test's comment cites where §0's answer came from, and `PalaceDrive.IncidentPowerW` equals the value recorded there.
2. **§1, one convention:** a matched openEMS port (a through line terminated in Z₀) scaled by §1 has
   |U + Z₀I|/2 = √(2·R·IncidentPowerW) and phase 0 at the field frequency. The scale function is the only place `0.5 / v`
   appears for fields: a source scan of `src/Render/Scene3D/Fields` finds one.
3. **§1, Z₀ from the run:** change the open document's port Z₀ after a run; the openEMS plot's scale is unchanged.
4. **§3, exponents:** at `DrivePowerW` = 4 × the default, a plot of `E` reads 2× and a plot of `S` reads 4× at the same
   sample (the hover value), and `Indicator` reads 1×. In dB, both shift by 6.02 dB.
5. **§3, shared step:** two plots of one solution at two drives load the step once (brief 96's read counter) and read
   values in the ratio √(P₁/P₂). They are in two range groups.
6. **§3, Accepted:** on a Palace fixture with a known |S11|, `Accepted` reads `Incident` × 1/(1 − |S11|²). A frequency
   absent from `port-V.csv` is refused with the sentence, and the plot stays as it was.
7. **§2, nothing stale:** changing `DrivePowerW` leaves `C3dPersistence.SerializeForRun` byte-identical and the setup's
   solved mark (brief 98) set. A pre-brief `.c3d` round-trips byte-identically.
8. **§4, one legend:** `render --field` of a plot at 10 W prints the drive line, identical to `LegendLines`' output for
   the same plot in-process.
9. **D3:** an openEMS run with its `p1/port1_u` removed draws, with the "not referred" line and no unit.

## 9. On completion

Record findings, §0's answer and where it came from first, in `src/Design/RESOLVED.md` and `src/Render/RESOLVED.md`
(create either if absent), never `CLAUDE.md`. The user docs are §7's. Do not run DocGen; the owner regenerates at the end
of the series.

# DE-5 — The "TRL and De-embedding" example workspace

Read `brief-de-0-overview.md` first. Needs DE-2, DE-3 and DE-4 landed.

**What.** `examples/TRL and De-embedding/`, offered by File ▸ New ▸ Examples. It teaches TRL in a page, then gives three
benches a user duplicates for their own data: calibrate, de-embed a 2-port, de-embed a FET to its intrinsic plane. Everything
is 2-port except the FET's 6-port extrinsic network. Follow `examples/README.md`: every design in a cell, analyses on test
benches, a row in `examples.json` (after S-Parameters; title "TRL and De-embedding"), and no results committed.

## The data is synthetic, and the example says so

Like the Model Composer example: circuitRF simulates every "measurement" from networks whose values are stated, so the
de-embedded answer can be checked against the truth, which measured data never allows. The data files are committed as
inputs. The cells that made them are committed too, so a user can see where they came from and change them.

| Cell | Kind | What |
|---|---|---|
| `Launch A`, `Launch B` | 2-pin cells | Two **different** fixtures, each a series L, a shunt C and a short mismatched lossy line (DE-0's prototype used 0.3 nH, 0.15 pF and 40-60 Ω lines of 9-12 mm). Port 1 is the instrument side for A; port 1 is the DUT side for B (D9) |
| `Line Standard` | 2-pin cell | A 50 Ω lossy line whose length gives about 20° at 1 GHz, so 1-8 GHz stays inside [20°, 160°] |
| `DUT` | 2-pin cell | Something with features worth seeing: a short stepped-impedance low-pass, for example |
| `Lab Thru`, `Lab Reflect`, `Lab Line`, `Lab DUT` | benches | Each a 2-port S-parameter run, 1-8 GHz, 141 points, that wrote `measurements/thru.s2p`, `reflect.s2p`, `line.s2p`, `dut.s2p`. Reflect is A into a short on port 1 and a short into B on port 2, with an offset so the short is not ideal |
| `GaN Extrinsic` | 6-pin cell | The extrinsic network the example's GaN model carries, at the placed device's size (below) |
| `Lab Extrinsic` | bench | The 6-port run that wrote `measurements/gan_extrinsic.s6p`. Ports 1-3 are the outer gate, drain and source; 4-6 the inner ones |

**The FET.** Copy `gan_4x125.cmodel` (only that file) from `examples/Model Composer/synthetic-gan/` into this workspace and
place it as the Model Composer benches do (`N=8`). Its network is stored per finger (`Cpgi`, `Cpdi`, `Cpdgi` scale with
fingers, `rg`, `rd`, `rs` inversely). **Take `GaN Extrinsic`'s values from the elaborated ComposedFET at `N=8`, not from the
stored values by hand.** Read them with `explain` or the elaborated netlist, and record them in the cell's own variables
with a comment saying where they came from. Make the `.s6p` from 0 Hz if the S-parameter run accepts a 0 Hz start, so
DC through the Deembed uses the network's real DC values. Otherwise start low and say in the README what that costs.

## The three benches the user duplicates

1. **`TRL Calibration`**: SnP blocks `THRU`, `REFLECT`, `LINE` on `measurements/*.s2p`, each between a port pair, so an
   `sp` analysis plots the raw standards. Then `analysis CAL type=trl … reflectType=short fixtureA="fixtures/fixture_A.s2p"
   fixtureB="fixtures/fixture_B.s2p"`. A `.cdd` beside it plots `LineDeg` with the 20°/160° lines, and `|SA|`, `|SB|`.
2. **`Deembed DUT`**: `P1 — Deembed(fixtures/fixture_A.s2p) — SnP(measurements/dut.s2p) — Deembed(fixtures/fixture_B.s2p) — P2`.
   Beside it, the `DUT` cell between P3 and P4 (the truth), and the raw measurement between P5 and P6. A `.cdd` overlays S21
   and S11 of all three: the de-embedded trace lies on the truth, and the raw one does not. It needs `TRL Calibration` run
   first. The README says so, and the Deembed's missing-file refusal names the file.
3. **`FET Deembed`**: the GaN device biased as in `GanPowerSweep` (−0.8 V gate, 8 V drain), with the bias applied
   **outside** the Deembed, so DC flows through it. Gate and drain reach the 50 Ω ports through
   `Deembed(measurements/gan_extrinsic.s6p)`: pins 4 and 5 to the device's gate and drain, pin 6 (outer source) to ground,
   pins 1 and 2 to the ports, and pin 3 (inner source) to ground. A second copy of the same device, not de-embedded, sits on
   ports 3 and 4. Analyses: DC, S-parameters 0.5-20 GHz, and one HB point at moderate drive with IProbes at the Deembed's
   pins 1 and 2, so the intrinsic-plane voltage and current are there to plot. Measurements extract the intrinsic elements
   from Y for both copies: `Cgs = Im(Y11+Y12)/ω`, `Cgd = −Im(Y12)/ω`, `gm = |Y21 − Y12|` at the low end, and
   `Rds = 1/Re(Y22 + Y12)`. A `.cdd` plots them against frequency: flat after de-embedding, bent before it. That is the point
   of de-embedding to the intrinsic plane, made visible. If they are not flat, find out why before writing the README. It is
   either the network values (above) or something DE-2 has not handled.

## README.md (what the user sees first; the in-app renderer shows headings, bold, italics, bullets, fenced code and two-column tables only, so equations go in fenced code blocks in plain Unicode)

Brief, in this order:

- **What TRL is.** The instrument is calibrated at its connectors, and the part sits behind fixtures. TRL measures three
  standards through the same fixtures and solves for both fixtures, needing to know almost nothing about the standards.
- **The model and the equations**, as in DE-1, in cascade-matrix form: `Mt = A·B`, `Ml = A·L·B`, `L = diag(e^−γℓ, e^+γℓ)`.
  `Ml·Mt⁻¹ = A·L·A⁻¹`, so A's columns are its eigenvectors. Reflect fixes the last unknown, and the measured part is
  `DUT = A⁻¹·M·B⁻¹`. Add the S ↔ T conversion. Keep it short, and give no citations.
- **What the standards must be**: a Thru (it sets the reference plane at its middle), a Reflect that is the same on both
  ports and known only as open or short, and a Line whose extra length is 20°-160° across the band. That gives an 8:1 band
  per line, with the formula for choosing the length. The Line's impedance becomes the reference impedance.
- **Using your own measurements**: duplicate `TRL Calibration`, point the three SnPs at your files, set `reflectType`, run.
  Then duplicate `Deembed DUT` and point it at your part's file. Tables of what to change.
- **The Deembed component**: the opposite of an SnP, any even number of ports, wired as you would wire the SnP that adds
  the network (D1's two chains). It works in DC, S-parameter, HB and loadpull runs, and an SnP and a Deembed on the same
  file cancel.
- **The FET**: a 6-port network between the outer and inner gate, drain and source, de-embedded to the intrinsic plane, with
  what the flat `Cgs`/`Cgd`/`gm`/`Rds` show.
- **The synthetic data**: the table above.

## Tests

- `ExampleWorkspacesTests` passes with the new row (index ↔ disk, every design a cell, every schematic extracts with its
  device, no home paths).
- `TrlExampleTests`, headless and with no display. Run the four `Lab` benches and compare with the committed files to the
  written precision; that is how a stale file is caught. Then run `TRL Calibration` into a temp copy of the workspace, then
  `Deembed DUT`, and assert the de-embedded S matches the `DUT` cell ≤ 1e-6 (or the measured Touchstone floor, stated).
  Then run `FET Deembed`'s DC and S-parameter analyses and assert `Cgs` varies by less than a stated few percent across
  1-20 GHz after de-embedding and by much more before. A few seconds in total. No timing assertion.
- UiSnapshot PNGs of the three benches' schematics and the two `.cdd` pages, looked at.

## Afterwards

`docs/user/src` gains a link to the example from the Deembed and `trl` sections. Regenerate only the edited pages. Update the
memory note on example workspaces with the new count.

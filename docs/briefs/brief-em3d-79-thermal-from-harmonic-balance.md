# Brief 79 — wire temperature from a harmonic-balance power sweep

**Series:** [3D thermal, fifth series](brief-em3d-71-thermal-overview.md) · **Tag:** `R-em3d79-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §4.5; [`harmonic-balance.md`](../design/harmonic-balance.md);
overview §1c, §1h, D11
**Area:** `src/Design/Thermal/` (the circuit link), `src/Engine/HarmonicBalance/` (pin currents of a linear
instance, **opt-in only**), `src/Cli/` (`em` runs the HB first), `src/Ui/ThreeD/` (*From circuit…*), `tests/`
**Depends on:** 78 · **Blocks:** 81

---

## 0. What this brief delivers

The owner's better UX for scenario 3: **the schematic drives the wires.** A thermal setup names a schematic,
its HB analysis and the instance of this 3D view's S-parameter result; the run executes the HB power sweep,
takes each pin's **DC and harmonic currents at every power step**, and solves the wires' temperatures at each
— giving **wire temperature against Pin**, with **Pout** and the other HB measures beside it.

The loop is **one way** (owner: closing it back into HB is later). The HB sees the wires at whatever
temperature its S-parameters were solved at; the thermal run reports what temperature they actually reach.

---

## 1. `R-em3d79-1` — the link

```jsonc
"Currents": [ { "FromCircuit": { "Schematic": "../schematic/pa.csch", "Analysis": "HB1", "Instance": "X3",
                                 "Carry": [ "Pout", "PAE" ] } } ]
```

- `Schematic` is a `.csch` or `.cnl`, resolved relative to the `.c3d` (the workspace walk-up rules every
  reference follows; `explain --ref` reports it). `Analysis` names the HB analysis — or its `parametric_sweep`
  wrapper, promoted exactly as the `hb` verb promotes it (CLAUDE.md: running the inner alone drops the sweep).
- `Instance` is the instance whose **S-parameter model is this `.c3d`'s EM result** — any of its EM setups'
  predictable result paths (`EmRunService.ResolveSnpPath`'s rule). It may be omitted when exactly one such
  instance exists; several is a refusal listing them; an instance whose model is another file is a refusal
  naming that file.
- **Port p is pin p.** The S-parameter result has one pin per port, numbered as the ports are (overview §1c),
  so there is no mapping dialog. The Setups page shows the mapping read-only (port name ↔ pin ↔ net in the
  schematic) so the user can see it is right.
- `FromCircuit` excludes per-port `Dc`/`Harmonics` in the same setup (a refusal if both appear).
- `Carry` lists HB cubes copied into the thermal `DataSet` on the same axes. Absent: every **scalar measure**
  of the HB testbench.

## 2. `R-em3d79-2` — the pin currents

**`R-em3d79-2a`** For every sweep point, every pin of the instance, every harmonic k = 0…K of the HB: the
complex current into the pin. HB already publishes `I:<instance>:<terminal>` cubes for device terminals
(`HbEngine`'s per-branch accumulator); **whether an S-parameter block's pins are among them is to be
established first, by a test**. If they are not, add them through the linear back-solver (`HbLinearBackSolver`
recovers linear branch currents) **only when a caller asks for that instance** — an opt-in list on the HB
settings, empty by default — so **every existing HB result and golden stays byte-identical**. The thermal run
service asks; nothing else does.

**`R-em3d79-2b` The convention.** Whether HB's spectra are peak or RMS phasors is **pinned by a test** — a
linear circuit where a known sinusoidal current flows into the instance — and the conversion to brief 78's
peak magnitudes follows from it, in one named function. The harmonic numbers map to `N`; F0 comes from the HB
analysis's fundamental at that point (so a frequency-swept HB carries its own F0 per point).

**`R-em3d79-2c`** Each pin's currents become that port's `Dc` and `Harmonics` for the point (all harmonics HB
carries, trimmed below a relative floor of 1e-6 of the largest, with the count trimmed in the notes).

## 3. `R-em3d79-3` — the run

1. Run the HB through the function the `hb` verb calls (the testbench's `measure` lines evaluated as the GUI
   evaluates them). A failed or partly converged HB sweep is carried: a point HB did not converge at is a
   point the thermal run skips, marked `hb-not-converged`.
2. Mesh once; for each HB sweep point in order, set the port currents, solve (conductive balance, brief 77),
   warm-starting from the previous point. Runaway is reported as brief 77 reports it, in Pin.
3. Write the `DataSet` on **the HB's own sweep axes** (Pin, and a second axis when the HB has one), with the
   wire table, the probes, the measures, the carried HB cubes, and the pin currents used (so the numbers
   behind every temperature are inspectable).

**`R-em3d79-3a` Limits — D11.** For each probe with `LimitC`, a scalar cube gives the **first sweep value at
which it is crossed** (interpolated between the bracketing points, NaN if never), and the notes say it in a
sentence: *"w1[3] reaches the mould compound's limit of 150 °C at Pin ≈ 31.2 dBm (Pout ≈ 44.9 dBm)."* That
sentence is the answer the scenario asks for.

**`R-em3d79-3b` Staleness.** The result records the content hashes of the schematic (as extracted) and the S-
parameter file; the thermal result is marked stale in the editor when either changes (brief 75's banner).

## 4. `R-em3d79-4` — surfaces

- **Setups ▸ Thermal ▸ Currents ▸ From circuit…**: pick the schematic, the analysis (only HB analyses and their
  wrappers are offered) and the instance (only instances of this view's result are offered); the read-only
  port ↔ pin ↔ net table.
- **Headless**: `circuitrf em x.c3d --setup HotHB` runs the HB and then the thermal run; `--set var=expr`
  applies to the schematic's globals before elaboration, as a run verb applies it. Exit codes as `em`'s; an HB
  that cannot run is that refusal, verbatim.
- The probe table (brief 75 §4e) plots any probe against the sweep axis, and the carried cubes are selectable
  as a second axis (wire temperature against Pout).

## 5. Gates

1. **Pin currents**: a linear schematic — a 1 A peak sinusoidal source through a two-port S-parameter block
   (a synthetic thru written at the `.c3d`'s predicted result path) into 50 Ω — gives |I| = 1 A at k = 1 on
   both pins; the peak/RMS convention is asserted.
2. **Byte identity**: every existing HB golden and every HB test's `DataSet` is unchanged (the opt-in list is
   empty for them).
3. **Instance resolution**: one instance → used; two → refusal listing both; another file → refusal naming it.
4. **End to end** — `[GmshFact]`: the gate-1 schematic swept over three drive levels, the `.c3d` with three wires
   on the thru's pad; the thermal `DataSet` has three points on the HB's axis, the wire table, the carried cube,
   and a `LimitC` crossing interpolated between the right two points. Under ~5 s or `Category=Benchmark`.
5. **Skipped points**: an HB point marked non-converged is skipped and flagged, and its neighbours still solve.

## 6. Owner check list (Debug build)

1. In the output-wire example, open the schematic; run its HB power sweep once by itself.
2. In the `.c3d`, Setups ▸ Thermal ▸ Currents ▸ From circuit…; pick the HB and the instance; check the mapping.
3. Simulate; plot the hottest wire against Pin and against Pout; read the limit sentence in Messages.
4. `circuitrf em` the same setup from a terminal; compare the `.npy`.

## 7. Scope

- **One way only**: nothing goes back into the HB (owner: later).
- **No change to any existing HB output** (gate 2).
- **No new verb**; `em` runs it.
- Findings in `src/Design/RESOLVED.md`, `src/Engine/RESOLVED.md`; never `CLAUDE.md`.

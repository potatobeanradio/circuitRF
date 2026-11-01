# Brief SENS-2 — Every S-parameter of every SnP block as a tunable, and the ranking by entry, port and instance

**Series:** SENS-1 … SENS-3 (decisions in `brief-sens-1-sensitivity-run.md` §0) · **Tag:** `R-sens2-<n>`
**Area:** `src/Core/Devices/SnpModel.cs`, `src/Core/Devices/ComponentModelFactory.cs` (`CreateSnpModel`), new
`src/RfCore/SnpPerturbation.cs`, `src/RfCore/RFNetwork.cs` (a `Reciprocity` beside `Passivity`, ~line 979),
`src/Design/Optimization/TunableCatalog.cs` (+ `TunableKey`), `src/Design/Optimization/TunableOverrides.cs`,
`src/Design/Statistics/SensitivityRun.cs` / `SensitivityDataSet.cs` (the roll-ups), `src/Cli/Explain.cs`
(`--tunables`), `src/Cli/Yield.cs` (`yield sens`'s JSON and text), `src/Ui/Tuning/TuningAddViewModel.cs` (grouping
only), `docs/design/yield.md` §18, `docs/design/tuning-optimization.md`, `docs/design/linear-engine.md` (the SnP
section)
**Depends on:** SENS-1.

---

## 0. The gap

An SnP instance is a black box to every tool that varies a design. A designer whose filter fails its spec in
production cannot ask whether the measured connector's S31 phase or the matching network's capacitor is the reason,
and an agent working through the MCP server cannot either. Once each S-parameter is an ordinary tunable, SENS-1 ranks
it, and the same key is also a Monte Carlo variable, a DOE factor, a Contributions entry and an optimizer variable,
because all of them read the catalog.

## 1. Requirements

**R-sens2-1 — The perturbation parameters.** An SnP instance takes, besides its file and interpolation settings:
- `SdB_<i>_<j>` — an offset in dB on |S(i,j)|, default 0;
- `Sdeg_<i>_<j>` — an offset in degrees on ∠S(i,j), default 0;
- `Reciprocal=auto|tied|untied` — default `auto` (R-sens2-3).

Ports are 1-based and always separated by `_` (`SdB_10_1`, never an ambiguous `S101`). A default is never written to
`.cnl` or `.csch`. A parameter naming a port outside `NumPorts` is a `check` error naming the instance and its port
count. Confirm `dB` and `deg` parse as units on these values and on `tune` lines (`Units.cs` lists `dB`); if a
`dB`-carrying value does not round-trip through `TunableValue`, fix it there, not by special-casing these keys.

**R-sens2-2 — Applied in one function, everywhere the block is evaluated.** `SnpPerturbation.Apply(S, offsets, hz)`
(`src/RfCore`): S′(i,j) = S(i,j) · 10^(δdB/20) · e^{j·δdeg·π/180}, applied after interpolation. **At 0 Hz only the
magnitude offset applies** — a DC S-parameter is real, and a phase offset there would make the block's DC solution
complex. With every offset 0 it returns its input unchanged (same instance — no copy, no cost: the zero-offset path
must not move any existing result by a single bit).
- `SnpModel.Stamp` calls it on `interp.Evaluate(hz)`.
- Audit every other reader of SnP data and record the verdict per file in `src/Core/RESOLVED.md`: the engines
  (`SParameterEngine`, `NonlinearDcEngine`, `HbEngine`, `HbLinearExtractor`), `ChainModel`, `ComposedModelCache`,
  `SystemBlockPassivity`, `SmithCascade`, `FieldCosimulation`, `ThermalCircuitLink`, the envelope SnP fit
  (`EnvelopeSnpFit`). Each either reads S through the model (and so sees the offsets), reads no S values (port
  bookkeeping only), or **refuses a nonzero offset by name** — "SNP3's S-parameter offsets are not applied by the
  envelope's rational fit". A path that silently uses the unperturbed file is the defect this audit exists to prevent.
- `SnpModel.ActivityFor` measures the PERTURBED matrix on the file's grid. A step or a trial that makes a passive
  file active is not refused by the sensitivity run itself; the analyses that already refuse an active block refuse
  that one point by name, and SENS-1's per-contributor status carries the sentence.

**R-sens2-3 — Reciprocity, decided by the file.** `RFNetwork.Reciprocity(SNP)` → the worst |S(i,j) − S(j,i)| over the
file's own frequency points and every pair, with the pair and frequency where it occurs. Judge it in absolute linear
terms, not dB: a −40 dB entry differing by 3 dB is a 0.003 difference and is reciprocal for this purpose.
- `Reciprocal=auto` resolves to **tied** when that worst difference is ≤ `SnpPerturbation.ReciprocalTolerance`, else
  **untied**. Before fixing the constant (start at 0.01), measure it on every `.s?p` under `examples/` and on a
  3D-EM-produced file, and record the table in `src/RfCore/RESOLVED.md`: every passive file must land tied and the
  amplifier files (`examples/S-Parameters/Amplifier/schematic/potentially_unstable_amp.s2p`, `FetStability`'s
  `fet_bias.s2p`) untied, with a margin on both sides. If no constant separates them, stop and report.
- **Tied:** only the lower triangle is offered (`SdB_i_j` and `Sdeg_i_j` with i ≥ j), and one offset moves both S(i,j)
  and S(j,i). Stating an upper-triangle key on a tied instance is a `check` error that names the lower-triangle key
  and `Reciprocal=untied`.
- **Untied:** all N² entries are offered.
- `Reciprocal=tied` on a file that is not reciprocal is allowed and warned once by `check`, with the worst difference
  in dB ("S21 and S12 differ by up to 18.3 dB at 2.4 GHz; one offset moves them together"). It is not refused.

**R-sens2-4 — The catalog.** `TunableKind.SnpEntry`. For each SnP instance whose file loads (through
`TouchstoneCache`, never a second read), `TunableCatalog` offers its `SdB`/`Sdeg` keys per R-sens2-3: unit `dB`/`deg`,
value 0, `IsDefault` true (Push adds the override), the optimizer's default range ±1 dB / ±10°. The `Tunable` record
carries the instance, i, j, part and the resolved tie state with its reason. A file that does not load is
`notOffered` with the load's own sentence. Inside a sub-cell the usual rule holds: the key lives in the cell and moves
every instance of it (D2), and `Location` says `×n`. `TunableOverrides` writes these keys as instance overrides like
any parameter; no special path.
- **Display label** — one function, `TunableKey.SnpEntryLabel`: `SNP3 · S31 dB`, `SNP3 · S31 phase`;
  `S(10,1)` above 9 ports. Every UI, text table and `--json` `label` field uses it; the key stays the identity.
- `explain --tunables` lists each instance's state: `SNP3: 4 ports, tied (auto: worst |S(i,j) − S(j,i)| = 0.0021),
  20 entries`.
- The Tuning panel's Add list groups an instance's entries under the instance, collapsed — a 12-port file offers
  156 keys and must not flood the list. Grouping only; no other Tuning change.

**R-sens2-5 — SENS-1's steps for SnP entries.** An entry without a tolerance steps ±`snpdb` / ±`snpdeg` (the
`sensitivity` line's, defaults 0.1 dB and 1°), basis `0.1 dB` / `1°`. With a tolerance (`tune SNP3.SdB_3_1 dist=gauss
sd=0.15 dB`) it steps ±kσ like any entry. `factors=all` includes SnP entries.

**R-sens2-5a — Leaving SnP blocks out: all of them, or chosen ones.** A design often has to use a block it cannot
change (a packaged part, a connector), and the question is then which COMPONENTS around it matter; another SnP in the
same design may be one the designer can change (an EM-simulated structure of their own) and should stay ranked. Two
controls, both on the `sensitivity` line, both defaulting to "everything in":
- **`snp=on|off`** (`--snp on|off`) — the master switch. Off leaves every SnP entry out.
- **`snpexclude=<instance>,<instance>`** (`--snp-exclude a,b`, replacing the line's list for one run) — leaves the
  named instances out and keeps every other one. An instance is named as its keys' owner prefix names it (`SNP4`, or
  `DUT.SNP4` inside a cell, the catalog's own spelling), so the panel and the line can never disagree about which
  block a name means.
- **The master wins, and does not erase the list.** With `snp=off` the list is kept, unused, so turning the master
  back on restores the user's per-instance choices.
- **Exclusion wins over a tolerance.** An excluded instance's entries are not stepped even when a `tune` line gives
  them a distribution; the notes say so once (`SNP4 excluded: 2 toleranced entries not stepped`). Exclusion is a
  sensitivity setting only: a Monte Carlo, DOE or optimization run is unaffected by it.
- **Excluded is reported, not hidden.** The result carries `sens.excluded` (the instances, with each one's factor
  count), the JSON `excluded: [{instance, factors}]`, and the text table one line after the ranking
  (`excluded: SNP4 (20 factors)`), so a block left out on purpose is never read as a block found insensitive.
- A name in `snpexclude` that is not an SnP instance is a `check` **warning**, ignored by the run — not an error,
  because deleting an instance from the schematic must not make the bench unrunnable. The panel removes a deleted
  instance's name from the list in the same undo step as the deletion when it can; the warning covers a `.cnl`
  edited by hand.
- Because each factor is stepped on its own, leaving SnP entries out does **not** change any component's Δ; it
  renormalizes the shares over what is left (so `C3: 41 %` reads as 41 % of what the designer can change), shortens
  the list, and saves two simulations per entry. `reference statistics` states this in one sentence.

**R-sens2-6 — The roll-ups.** The ranking SENS-1 computes is per KEY (one dB or phase offset). Three more levels come
from the same Δs with **no extra simulation**:
- **entry** — `SNP3 · S31`: its dB and phase keys combined, Δ = √(Δ_dB² + Δ_deg²), share = the sum of the two shares.
  Every non-SnP key is its own entry.
- **port** — `SNP3 · port 3`: every entry of SNP3 in row 3 or column 3, shares summed. **Port shares overlap** (S31
  counts toward ports 3 and 1), so the port level has no `:cumulative` cube and its JSON says `overlapping: true`.
  Non-SnP keys do not appear at this level.
- **instance** — `SNP3`: every key whose owner is that instance, shares summed; for a part, `R1` holds `R1.R`; a
  variable is its own instance. Each instance carries its factor count, because a 4-port block with 20 factors and a
  resistor with one rank on one scale, and the count is what keeps that honest.

Result cubes: `sens.contrib:<goal>:by=entry|port|instance` (+ `:cumulative` for entry and instance), over a labelled
`contributor` axis whose labels are the display labels; `sens.factors:<goal>:by=instance`. JSON: per goal,
`contributors` (keys, as SENS-1), `entries[]`, `ports[]` (with `instance`, `port`), `instances[]` (with `factors`).
The text table takes `--by key|entry|port|instance` (default `entry`).

**R-sens2-7 — Docs.** `docs/design/yield.md` §18 gains the SnP subsection (the parameters, the tie rule, the audit's
verdicts, the roll-ups); `tuning-optimization.md` lists `SnpEntry`; the SnP section of `linear-engine.md` states the
perturbation and the DC rule; `reference` for the SnP component lists the three parameters.

## 2. Gates (minimal; run only these classes)

- `SnpPerturbationTests` (`tests/RfCore.Tests`): zero offsets return the same matrix instance; a +1 dB / +10° offset on
  S21 of a tied 2-port moves S12 identically and nothing else; at 0 Hz the phase offset is not applied.
- `SnpReciprocityTests` (`tests/RfCore.Tests`): the shipped amplifier `.s2p` resolves untied, a shipped spiral
  `.s2p` tied.
- `SnpEntryTunableTests` (`tests/Ui.Tests`, beside `SnpComponentTests`): a tied 3-port offers 12 keys and an untied
  one 18; an upper-triangle key on a tied instance is the `check` error; an S-parameter run with `SdB_2_1=1` on a
  through line moves |S21| of the circuit by 1 dB to 1e-9.
- `SensitivityRollupTests` (beside SENS-1's): on a design with one 3-port SnP and one resistor, the instance level's
  shares sum to 1, the entry level equals the key level summed in pairs, and port 1's share equals the sum over its row
  and column.
- `SnpExcludeTests` (beside SENS-1's): on a design with two SnPs and two components, `snpexclude` naming one leaves
  only its keys unstepped (evaluations drop by twice its factor count), the components' Δ are bit-identical to the
  run with nothing excluded, and it is listed under `excluded`; `snp=off` with the list set steps no SnP key and the
  line still round-trips the list.
- One existing SnP golden test, run unchanged, is the proof that the zero-offset path moved nothing.

## 3. Out of scope

Frequency-local offsets (a band, a ripple) — the uniform offset is the v1 model and the docs say so; offsets on
other data-backed blocks (the `.cmodel`, a vendor part's internals, which brief vendorlib-4 already keeps closed);
roll-ups of a Monte Carlo's Contributions (they list SnP keys as entries; grouping them is a later step).

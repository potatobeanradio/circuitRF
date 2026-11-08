# DE-2 — The `Deembed` netlist type and its model

Read `brief-de-0-overview.md` (D1-D7 and the gates) and `brief-de-1-network-math.md` first.

**What.** `Deembed:X1 n1 n2 … File="fixtures/fixture_A.s2p" NumPorts=2` in a `.cnl` elaborates, stamps `F⁻¹` and runs in
every analysis an SnP runs in. No editor work here (DE-3), but a hand-written `.cnl`, `check`, `explain` and every run verb
work by the end of this brief.

## The model

- `SnpModel` gains `Inverse` (init-only; false by default, and false stamps exactly as today). With it true, `Stamp`
  evaluates the interpolated S exactly as now (`LoadInterpolator(c).Evaluate(hz)`), passes it through
  `NetworkInverse.Invert(s, NumPorts / 2)`, and stamps the result. **Always use the S-wave stamp**
  (`IdealSBlockModel.StampWaveConstraints`), never the Z expansion. A thru-like fixture inverts to a thru-like block, and a
  thru has no Z. The S-wave stamp is exact for any S.
- `Describe` says `Deembed 'X1.D1'` for an inverse block, so every existing refusal (missing file, folder, not found) names
  it correctly with no further edits.
- A non-invertible point (DE-1's typed refusal) throws a refusal naming the instance and the frequency in GHz. At 0 Hz the
  sentence says the fixture has no DC path through it and suggests where to put the bias (outside the de-embedded part).
- `ActivityFor` → `BlackBox`, with a passivation note saying it is the inverse of a network and has gain wherever that network
  has loss (D6).
- `Offsets` are refused at construction (D7): a `SdB_i_j`/`Sdeg_i_j` key on a Deembed is a `ParameterRefusalException`
  naming the key.

## Wiring the type through, the EM Field Data precedent

Follow every place `EmFieldDataRecord.TypeName` was threaded through for a second SnP-shaped type, and add `Deembed`
where it means "a block with a Touchstone `File`":

- `ComponentModelFactory`: the known-type list (line ~66) and `TryCreate`, as `CreateSnpModel(…) with { Inverse = true }`
  (or a constructor argument), after refusing an odd `NumPorts` (D3).
- `CnlReader`: `File` resolution exactly as SnP (`resolvesFile`), the same ignored parameters, the N-or-N+1 nets rule and
  the `NumPorts` checks, with messages that say `Deembed:` rather than `SnP:`. `CnlWriter`: quote the `File` as an SnP's is
  quoted (its comment says why).
- `Elaborator`: `File` is a path, not an expression (line ~1460), and resolves against the base directory as an SnP's does.
- `FileReferences`, `InstanceNetContract` (`NumPorts`, N-or-N+1), and `ProtectPartition`'s list of types that carry data.
- `VendorAReader`/`KitNetlistReader`: nothing. No foreign netlist has this type.

## Sites that read the block's FILE rather than its stamp: apply the inverse or refuse

Audit every `is SnpModel` / `FilePath` / `TouchstoneCache.Get` reader. Known today:

| Site | What it must do with an inverse block |
|---|---|
| `Engine/Envelope/EnvelopeSnpFit` | Not fit the file. Decide between the envelope's tangent fallback, if it says so in the run's notes, and a refusal naming the instance and saying a de-embedding block has negative delay, which no causal model represents. Write down which and why |
| `Engine/SParameterEngine.BlockWaves` (≈ line 156) | Serves the fields analysis's EM block. Refuse a Deembed as "not an EM result", as a plain SnP is refused upstream |
| `Design/Em3d/EmFieldBlocks`, `FieldCosimulation` | Already key on `RecordPath`, which a Deembed never has. Confirm, no change expected |
| `Design/Thermal/ThermalCircuitLink` | Matches SnPs that are an EM run's result by file path. A Deembed on such a file is not that block's result: exclude `Inverse` blocks |
| Sensitivity / yield / tunables (`SensitivityRun`, `TunableCatalog`, `TunableOverrides`, the yield panel's SnP rows) | List no entries for a Deembed (D7). They key on the SnP **type**, so check the strings, not just the model |
| `EmCoSimNetlist` (≈ line 433) | Keys on `SymbolKind.Snp`; a Deembed is not a co-simulation board block. Confirm |
| `SnpModel.OffsetsNotAppliedBy` | Unreachable for an inverse block (no offsets). Leave it |

Anything else the audit finds goes in this table in `src/Core/RESOLVED.md` with what was done.

## Gate tests (`tests/Engine.Tests/Linear/DeembedTests.cs`; one test per claim)

Use small Touchstone files the test writes itself: a lossy, mismatched 2-port, an asymmetric 4-port and a 6-port from a
lumped network with series R and L, shunt C and a mutual. Each is sampled 1-10 GHz, plus one 6-port that also has a 0 Hz
row.

1. **Cancellation, S-parameters**: `Port — SnP(f) — Deembed(f) — Port` and the reverse order equal a through connection,
   max |ΔS| ≤ 1e-12. Check at the file's points, at off-grid points, below and above its band, for each file, and with
   `InterpMode`/`InterpDomain` at a non-default value on both blocks.
2. **Cancellation, DC**: a resistive divider X behind `SnP(f6) — Deembed(f6)`, where f6 is the 6-port with a 0 Hz row. The
   node voltages equal X alone to 1e-12.
3. **Cancellation, HB**: a nonlinear part (a diode or a simple SDD FET) behind the pair. The fundamental and the third
   harmonic match the part alone to the HB tolerance. Every harmonic falls inside or clamps the same way on both blocks.
4. **Recovery**: `Port — Deembed(A) — SnP(A·DUT·B) — Deembed(B) — Port` returns DUT ≤ 1e-12, where A·DUT·B is written
   with DE-1's cascade.
5. **Refusals**: an odd `NumPorts` (named at read time); a fixture with a series DC block at 0 Hz (instance and frequency
   named); a `SdB_1_2` key; NDF on a bench with a Deembed (named, with the reason).
6. **Unchanged**: one existing SnP golden test passes untouched. `SnpStampTests` already exists, so run it.

Run `Engine.Tests` filtered to these classes, plus `SnpStampTests` and `SnpElaborationTests`.

## Docs

`docs/user/src/reference/netlist.md` (the type, its nets and parameters) and `components.md` get a Deembed section: what it
is, D1's wiring rule with the two chains, the even port count, that it has gain and NDF refuses it, the envelope behaviour
you chose, and that an SnP and a Deembed on one file cancel. Regenerate only the edited pages.

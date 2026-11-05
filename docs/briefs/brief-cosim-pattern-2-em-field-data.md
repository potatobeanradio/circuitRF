# Brief CP-2 — The EM Field Data block, and Fields requires it

**Series:** `brief-cosim-pattern-0-overview.md` · **Tag:** `R-cp2-<n>` · **Depends on:** CP-1
**Area:** `src/Design/Schematic/ComponentTypeRegistry.cs`, `src/Core/Devices/ComponentModelFactory.cs`, `src/Core/Elaboration/FileReferences.cs`,
`src/Core/Netlist/{CnlReader,CnlWriter,InstanceNetContract}.cs`, `src/Design/Em3d/{FieldCosimulation,PortFieldSet}.cs`, `src/Ui/ViewModels/ParameterEditorViewModel*.cs`,
`src/Ui/Views/Dialogs/AnalysisEditorDialog.axaml`, `examples/SAR MRI Loop Coil/`, `testdata/sar/circuitrf/`, `tests/Ui.Tests/Sar/`

## 0. Why

A Fields analysis names a block, and the block looks like any Touchstone file, while what it really stands for is reached through a
comment in that file. Nothing in the schematic shows that one SnP has fields and another does not until the analysis refuses.

## 1. Requirements

**R-cp2-1 — A component type: `EM Field Data`, `.cnl` spelling `EmFieldData`.** One parameter: `File`, a `.cemr`. Its picker filters on `.cemr` and
also accepts an older `.sNp`, offering CP-1's `Adopt`. The symbol is the SnP's dynamic symbol with its own label and glyph, so a
glance tells the two apart.

**R-cp2-2 — Pins from the record.** Its port count, names and order come from the record; there is no `NumPorts` to set. Pins show the
port names (`P1`, `C3`). When the record is re-written with a different port count, the block says so and the existing pin rule for a
changed SnP applies.

**R-cp2-3 — It stamps the existing model.** The factory reads the record's `.sNp` path and returns `SnpModel`, unchanged, so every
`is SnpModel` site works: the HB and DC paths, the envelope fit, `BlockWaves`, `TouchstoneCache`, Yield and SENS-2's entries. Expose the
record path on the model for Fields. **Audit, do not bulk-replace:** `SymbolKind.Snp` and `"SnP"` appear 93 times in 43 files. For each,
decide whether it means *any S-parameter block* (add the new kind) or *a Touchstone file* (leave it), and list both groups in
`RESOLVED.md`. Files that must learn the kind include the registry, `NetExtractor`, `FileReferences`, the `.cnl` reader and writer,
`TunableCatalog`, `TunableOverrides`, `ProtectPartition` and `ModelLibraryExport`. Recognition emitters do not.

**R-cp2-4 — It works from saved data only.** Elaboration and every analysis read the record and the files it names, and no `.c3d`,
`.cem` or `.clay`. A test deletes the 3D view and Simulate still runs.

**R-cp2-5 — What it refuses.** Each names the block and the file:
- no such record;
- a record holding neither a far field nor a field set, saying that its S-parameters go in an SnP and which setting keeps fields;
- a file it names is missing or truncated;
- stamps disagree, naming which (replacing the comment's stale check, and comparing no file times).

**R-cp2-6 — What it says.** The property panel shows one read-only line from the record: when, which solver and quality, the sweep, the
port count. Beside it, **Run settings…** opens the recorded setup read-only, with what differs from the current setup marked. A
difference never blocks and adds one note to a run. It is not a warning: running against older data is the point of the design. A
record with `Setup` null reads "settings not recorded".

**R-cp2-7 — Fields requires it.**
- `block=` on a plain SnP is refused, saying to place an EM Field Data block and how to pick the same run's record.
- The analysis editor's block control lists the schematic's EM Field Data blocks by path, including those inside sub-cells, and still takes
  typed text.
- `FieldCosimulation.Run` finds the data through the model's record path.
- `PortFieldSet.Open` takes the record; `ReadProvenance`'s folder lookup and the `.sNp`'s `port fields:` line go.

**R-cp2-8 — Existing documents.** A saved `.csch` or `.cnl` with `type=fields` on an SnP loads and refuses at run with R-cp2-7's sentence.
It is not silently converted, since no record exists for it to point at until the setup runs again or the file is adopted. Provide a
one-click **Change to EM Field Data** on the refusal when the `.sNp` beside it can be adopted.

**R-cp2-9 — Move what ships.**
- `examples/SAR MRI Loop Coil`: `Coil Bench.csch`'s `EM1` becomes an EM Field Data block on `results/Loop Coil CoSim.openems.cemr`. Update its
  README's steps, and its saved field plots if they refer to the block.
- `testdata/sar/circuitrf/loop/cosim/LoopCosim.cnl` and `birdcage/cosim/BirdcageQuadrature.cnl`: the same, with the committed results
  adopted once so the reference data does not change. Their tests' agreements must not move; say so with the numbers.
- `examples/SAR Phone Dipole` has no fields analysis. Check that and leave it.
- Docs: `cosimulation.md` §2, `sar.md` §11, `cli.md` §26, the `reference` topics and the MCP tool text.

## 2. Gates

- `CosimLoopCoilTests`, `BirdcageReplicationTests`, `FieldGoalTests`, `CosimSuperpositionTests` and `FieldCosimTests` pass with their numbers
  unchanged, after only the block's spelling moved.
- EM Field Data and SnP on the same `.sNp` give identical S to the last bit.
- Round trip: `.csch` → `.cnl` → run; `netlist` writes `EmFieldData`; a hierarchical block's path resolves.
- The Firewall and netlist-writer scans stay green.
- UiSnapshot of the block on a schematic, its property panel, **Run settings…** with a difference marked, and the analysis editor,
  all inspected and listed in the note.

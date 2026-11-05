# Brief CP-1 — The result record, and a run that does not finish changes no result

**Series:** `brief-cosim-pattern-0-overview.md` · **Tag:** `R-cp1-<n>` · **Depends on:** nothing
**Area:** new `src/Design/Layout/Em/EmResultRecord.cs`, `src/Design/Layout/Em/EmRunService.cs`, `src/Design/Em3d/Em3dRunService.cs`
and each solver's run service, `src/Design/Em3d/PortFieldSetWriter.cs`, `src/Design/Em3d/PortFieldSet.cs`, `src/Cli/DocumentKinds.cs`,
`src/Cli/Check*.cs`, `src/Cli/Explain*.cs`. Findings go in `src/Design/RESOLVED.md`.

## 0. Why

Nothing on disk says how a result was made. A `.sNp` header carries hashes that answer whether the document changed, not what it
said, so after a setup is edited there is no way to read back the settings behind the results still on disk.

The results also are not protected as a set:
- `PortFieldSetWriter.Write` deletes the old field set first, then spends minutes writing the new one, so a failure there leaves
  no fields at all and a `.sNp` whose stamp names them.
- `.npy` and `.sNp` are written one after the other, so a failure between them leaves a mismatched pair.
- The solve-time failure, such as running out of memory, does leave the `.sNp` and `.npy` alone today, because they are written after
  the solve. Hold that with a test. It is what a person relies on.

## 1. Requirements

**R-cp1-1 — `<key>.cemr`, beside the `.sNp`.** One JSON document for every finished driven run, 3D and planar. It holds:
- `FormatVersion` and a `Stamp` unique to the run;
- `Written` (date and time), the product version, the solver with its version and the device;
- `Files`: the `.sNp`, the `.npy` and the field set when there is one, each **relative to the record**;
- `Ports`: number, name, Z0, kind (`Gap`, `Plane`, `Wave`), and for a component port its part and pins;
- `Frequencies`: every sweep point, and the frequencies holding a far field and a field set;
- `Setup`: the whole `EmSetup` as `EmSetupPersistence` writes it, and the 3D view's document as text when the run came from one;
- `Geometry`: the hashes `EmSnpProvenance` already forms, and mesh size and unknowns as the run reports them.

Its schema is a reference topic and the document kind goes through `DocumentKinds.Classify`, so `check` and `find` name it.

**R-cp1-2 — Each data file carries the stamp.** The `.sNp` keeps a provenance line of the same name. The `.npy` carries it as a `__`
metadata cube and the field set's manifest already has one. The record is the only pointer: nothing reads a file's location from a
Touchstone comment. Remove `PortFieldSet.ReadProvenance`'s folder lookup in CP-2, when its last caller goes.

**R-cp1-3 — Publishing.** A run writes every result file under staging names in the results folder, then renames them into place in
the order field set, `.npy`, `.sNp`, record. Disk space, a write error or a cancel before the renames leaves the previous result
whole; the staging files are removed, and leftovers from a killed process are removed by the next run of that setup. Renames are
not atomic as a group. If a process dies in that window, the stamps disagree and CP-2's block refuses and says which file
differs. Replace `PortFieldSetWriter`'s delete-first with this.

**R-cp1-4 — What a new run may clear when it starts.** Palace, openEMS and EMerge clear their own run directories (`PalaceRun`'s
`post`, `OpenEmsRun`'s `dir`, `EmergeFields`' `post`), where the 3D view's saved field plots live. Those are working files, so a new
run clearing them is acceptable for now. State it in `em-3d.md` rather than leave it to be discovered: the S-parameters, pattern and
fields a schematic reads survive a failed run, and a 3D field plot of the old run may not. Report in `RESOLVED.md` what each solver
actually clears, and when.

**R-cp1-5 — Reading a record back, headlessly.** `explain <file.cemr>` prints the run's settings in the setup's own words and, when
the setup it names can be found, what differs from it now. `--json` carries the same. A difference is information and never exits
nonzero. This reads no solver and writes nothing.

**R-cp1-6 — An older result.** A run from before this brief has a `.sNp` and an `.npy` and no record. `EmResultRecord.Adopt(snpPath)`
writes one beside them from what they hold, with `Setup` null, read back as "settings not recorded". It refuses when no `.npy`
matches the `.sNp`. It is reached by `circuitrf em --adopt <file.sNp>` and by CP-2's file picker. It never touches the data files.

**R-cp1-7 — Output name: one name for the whole result.** The setup's `SnpOutputPathOverride` moves only the `.sNp` and the field set today;
`Em3dRunService.NpyKey` and `EmRunService.ResolveNpyKey` ignore it, so the `.npy` stays under the setup's key in `results/`, and a second
output name leaves one `.npy` for two `.sNp` files. From this brief the name decides every result file: `<name>.sNp`, `<name>_em.npy`,
`<name>.fields/` and `<name>.cemr`, together, relative to `results/` or absolute.
- A run overwrites a result of the same name, as a planar run does today. **A different name is how an earlier result is kept**; there is no
  separate command.
- The solver's run directory is a working folder and does not follow the name.
- A name typed or browsed with a `.sNp`, `.npy` or `.cemr` suffix has it removed, as `ResolveSnpBasePath` does for `.sNp` now.
- Follow every reader of the `.npy`'s path to the run's returned `NpyPath`, not a rebuilt key. `WorkspaceViewModel` keys a planar run's Data
  Display on `ResolveNpyKey`, so two names would share one display; key it on the file, as it already does for a 3D run. A result outside
  `results/` is not in the Data Display's list of that folder, so say how it is opened.
- `src/Cli/EmComponent.cs`'s `-o` is built on this same field; check the `em` verb's other paths too. `CLAUDE.md`'s rule for `em` is that `-o`
  moves the Touchstone only, with a byte-for-byte gate on it. Split the two so `-o` keeps its meaning: a separate Touchstone destination on the run, not the setup's name.
- `EmSnpSchematic.CellNameFor` and `EmCoSimNetlist.FileNameFor` name their output after the setup, so two output names share one `_sim` cell and
  one `_cosim.cnl`. Name them after the output name, so each result has its own.

**R-cp1-8 — The box says so.** One control serves planar, `.cem` and 3D setups (`EmSetupEditorView`'s `SnpOutputPathBox`), so this is one change:
- `SnpOutputPlaceholder` shows the bare name a blank box resolves to, with no suffix. It reads the same rule the run does. Today it shows the
  layout's name, while a run uses the setup's name and, in 3D, the solver's token (`Setup.palace`), so it also follows a change of name or solver.
- The label reads **Output name:**, in the same column. No control moves.
- The tooltip says the name applies to the S-parameters, the far field and field data and the run's settings, and where a blank or
  relative name lands. The Browse picker's title and filter follow it.
- Update `em-setup.md`'s *Output file* paragraph and its list of what a run writes, `EmOutputPathAndMessageLevelTests`, and `cli.md` where it
  describes the setup's key.

## 2. Gates

- A cancelled run, a run whose solve throws and a run whose field-set write fails each leave the previous `.sNp`, `.npy`, field set
  and record **byte-identical**. Use the fake runners the run services' tests already have; start no solver.
- A finished run's record names files that exist and whose stamps match; a setup edit afterwards leaves them alone.
- A folder moved elsewhere still resolves, because its paths are relative.
- `explain` on a record after the setup is edited prints the old value and the new one.
- `Adopt` on a pair, and its refusal on a `.sNp` with no `.npy`.
- Two runs under two output names leave two whole results, and each record resolves to its own files; a third run under the first name replaces
  only that one. Hold this on a planar setup and on one 3D setup, with the fake runners.
- The placeholder, for a planar setup and a 3D setup: what it shows is the stem a blank box's run writes.
- UiSnapshot of the setup header, empty and filled, inspected: the bare name fits and nothing shifts.

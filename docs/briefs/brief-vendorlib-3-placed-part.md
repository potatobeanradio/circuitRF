# Brief 3 — the placed part: choosing, stepping, running, sharing

**Series:** [Vendor libraries](brief-vendorlib-0-overview.md) · **Tag:** `R-vl3-n` · **Phase:** UI, extraction
**Area:** `src/Ui/ViewModels/ParameterEditorViewModel.SpiceModel.cs`, `src/Ui/Views/ParameterEditor/ParameterEditorView.axaml(.cs)`,
`src/Design/Schematic/SpiceModelPeek.cs`, `src/Design/Schematic/SpiceModelNetlist.cs`,
`src/Design/Schematic/NetExtractor.cs` (`SymbolKind.SpiceModel` emission), `src/Ui/Archive/WorkspaceArchiveScanner.cs`
(`AddKits`' sibling), `src/Cli/Check.cs`, `src/Cli/Explain.cs` (`--ref`)
**Depends on:** 1, 2
**Rule:** overview §0 and §3 (responsiveness by counter).

---

## 1. `R-vl3-1` — the part picker, and stepping the value

The SpiceModel panel today fills a `ComboBox` with every definition in the file (`SpiceModelNameOptions`)
after `SpiceModelPeek` parses the whole file. For one library file that is **12,000+ options and a
full parse of a ~14 MB file on selection.** Neither is acceptable.

- **`R-vl3-1a`** For a `vendorlib://` instance, the panel shows:
  - the library and file as text
  - a **Part** field with search-as-you-type over that file's index entries (`VendorLibrarySearch`,
    brief 2 §4), in a virtualized drop-down
  - the part's description and frequency range as plain text

  Choosing a part rewrites `Name` and `Glyph` and keeps the instance, its wires, rotation and label
  placement. **Selecting the instance parses nothing**: no `SpiceModelPeek` call for a `vendorlib://`
  file, and the counter proves it.
- **`R-vl3-1b`** **Value ▲ / ▼** beside the Part field steps to the next / previous part *of the same
  kind*:
  - "Same kind" means the same file and the same description fields except the value.
  - For a part with no description, it means the same file only.
  - Steps are ordered by value, and equal values tie-break by part name.

  This is the manual form of "tune over real parts". Wire it to the same keys that step a
  parameter in the tuning panel only if that already exists as a reusable command. Otherwise
  buttons only.
- **`R-vl3-1c`** A file-based (non-library) SpiceModel keeps today's panel unchanged.
- **`R-vl3-1d`** **A part change carries its footprint only when the footprint came from the part.**
  When the Part picker or Value ▲/▼ changes the part:
  - If the instance's current `Footprint` equals what `FootprintDefaults.ForCaseCodes` (brief 2 §3d)
    gives for the OLD part, it becomes what that function gives for the NEW part. That may be None,
    when the new part's size is not in the table.
  - Any other value is the user's choice and is left untouched. That includes a hand-picked case, a
    workspace cell, a custom `.clay`, and a None the user set on a part that had a case.

  This takes no extra stored state: "came from the part" is the comparison itself. Value stepping
  keeps the description fields, so it normally keeps the size and the footprint stays. The rule
  matters for the Part picker, which can cross sizes. The footprint change rides the same undo entry
  as the part change.

## 2. `R-vl3-2` — extraction reads one block

- **`R-vl3-2a`** For a `vendorlib://` instance, the extractor:
  - takes the entry's byte block from the index (brief 1 §2d)
  - checks the file's mtime/length against the index (a mismatch re-scans that file first)
  - reads **only that block**
  - hands it to `SpiceNetlistReader.Read(text, sourceDirectory, fileLabel)`
  - continues through the existing `SpiceModelNetlist.Build`

  This is still one translation path, with a smaller input. The emitted cell names and
  `claimedCells` behaviour are unchanged.
- **`R-vl3-2b`** Headless, the index for the referenced library is built on demand by the extraction
  for the files placed parts name. It is not built for the whole library: a run that places three
  parts scans three files' headers.
- **`R-vl3-2c`** A part the library no longer holds, or an unknown library name, is an extraction
  **refusal** naming the part and the library, with brief 1's two distinct reasons. It is never a
  silent skip.

## 3. `R-vl3-3` — the frequency-range warning

- **`R-vl3-3a`** When a run's analysis frequencies leave a placed part's indexed range, the run returns
  **one warning per part number**: *"<part> is characterised from <low> to <high>; this run reaches
  <f>."* 0 Hz (a DC operating point) is exempt, because a DC solve of a passive R/L/C network is
  exact. A part with no stated range gets no warning.
- **`R-vl3-3b`** The warning is returned through the extraction's diagnostics, posted by the GUI to
  Messages and written by the CLI to stderr. It is not a panel note under any readout, and it is not a
  refusal. Exit codes are unchanged.

## 4. `R-vl3-4` — sharing and repair

- **`R-vl3-4a`** **Archive Workspace** lists each referenced vendor library as one row, **unticked by
  default**, beside the kit rows, on `AddKits`' terms. The row's sentence: *"Not included by default:
  a vendor library is usually licensed for your own use. The recipient can add their own copy."*
  - `DocumentFileRefs` must not ALSO offer the library's files as external-file rows. A
    `vendorlib://` string is not a path, and it must not resolve as one.
  - Ticked, the library is copied under the archive's kits folder and the archived `.cws` reference is
    rewritten to the copy.
- **`R-vl3-4b`** A missing library draws its placed parts with their stored glyph in the `NotFound`
  treatment (brief 2 §3a). It produces one Messages summary per library with Repair (brief 1 §4d).
  It never blocks the open.
- **`R-vl3-4c`** `circuitrf check` reports, through `VendorLibraryRefs.Validate`:
  - a referenced library that is missing (error)
  - a placed part the library no longer holds (error)
  - unreadable files in a referenced library (warning)
- **`R-vl3-4d`** `explain --ref vendorlib://…` reports the walk (which `.cws`, which library entry,
  which folder) and whether the folder leaves the workspace, as it does for other references.

## 5. Tests (minimal — counters)

1. **Selecting parses nothing:** selecting a placed `vendorlib://` part with the panel open makes zero
   `SpiceModelPeek` / `SpiceNetlistReader` calls.
2. **Extraction reads one block:** a run placing two different parts from one 300-part file calls
   `SpiceNetlistReader.Read` twice, on inputs no longer than those two blocks, and the S-parameters
   match the same two parts placed through a plain file-based SpiceModel to 1e-12.
3. **Value stepping** walks one file's same-description parts in value order and keeps the instance's
   wires.
4. **The footprint follows the part only when it came from the part:**
   - Picking a 0603 part in place of a 0402 part moves `smt:0402@N` to `smt:0603@N`.
   - The same pick on an instance whose footprint the user set to `smt:0805@L` leaves `smt:0805@L`.
   - Undo restores both the part and the footprint.
5. **Frequency warning:** a sweep from 1 MHz on a part characterised from 100 MHz warns once. DC does
   not warn.
6. **Archive:** the library row is present and unticked, and no external-file row names any of its
   files.
7. **`check`:** a missing library and a vanished part are both errors, and an unreadable file is a
   warning.

Run only the classes you touch.

## 6. Scope

No optimizer variable over discrete parts (overview §4). No change to the file-based SpiceModel's
behaviour.

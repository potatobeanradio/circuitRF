# Brief 1 — the library reference and its index

**Series:** [Vendor libraries](brief-vendorlib-0-overview.md) · **Tag:** `R-vl1-n` · **Phase:** model, format, CLI
**Area:** new `src/Design/VendorLibraries/` (`VendorLibraryIndex.cs`, `VendorLibraryHeader.cs`,
`VendorLibraryRefs.cs`), `src/Design/Workspace/WorkspacePersistence.cs` (`CwsFile`),
`src/Design/Workspace/WorkspaceCreate.cs`, `src/Design/Schematic/SpiceModelSymbolProvider.cs`
(`ResolvePath`), `src/Core/Netlist/Spice/` (the `Rser` spelling), `src/Ui/Schematic/PdkReferenceManager.cs`,
`src/Ui/Views/Dialogs/ManagePdksDialog.axaml(.cs)`, the File menu, `src/Ui/Theming/AppPreferences.cs`,
`src/Cli/Authoring.cs`, `src/Cli/Find.cs`, `src/Cli/Serve/ToolCatalog.cs`
**Depends on:** — · **Blocks:** 2, 3
**Rule:** overview §0 — no manufacturer, part, series, file or simulator names anywhere; fixtures are
synthetic.

---

## 1. `R-vl1-1` — the reference in `.cws`

- **`R-vl1-1a`** Add `CwsFile.VendorLibraries` (`List<CwsVendorLibraryRef>?`, written only when
  non-empty, no `FormatVersion` bump). Each entry has:
  - `Name`: the name the user knows it by, and the name every placed part's reference carries.
  - `Path`: stored through `WorkspaceRefs.ToStoredRef`, so it is relative inside the workspace and
    absolute outside it. That is the PDK reference's rule, not `SnpPathPolicy.ToStored`'s
    two-levels-up rule. `WorkspaceRefs` is in `src/Ui/Schematic` and framework-free, so **move it to
    `src/Design/Workspace`** (add the namespace to `src/Ui/GlobalUsings.cs`) rather than writing a
    second copy in `src/Design`.

  Doc-comment why it is keyed on the NAME, pointing at `CwsPdkRef.Provider`: re-pointing a moved
  library is one edit, and no schematic changes.
- **`R-vl1-1b`** A placed part's `File` parameter is `vendorlib://<Name>/<path relative to the library
  root>`, with `/` separators. `SpiceModelSymbolProvider.ResolvePath` gains the scheme branch, resolving
  through the nearest ancestor `.cws`. That is the same walk-up it already uses, so editor, extractor
  and CLI cannot disagree.
  - An unknown name resolves to **null with a reason** ("this workspace references no vendor library
    named …"). A known name whose folder is missing resolves to null with a different reason. These
    are the two states `pdk-import.md` §5 insists must stay distinguishable.
  - Mirror `ExternalCellRef`'s `ws://` parsing (`src/Design/Workspace/ExternalCellRef.cs`). Do not
    write a second parser shape.
- **`R-vl1-1c`** `VendorLibraryRefs` (`src/Design`, framework-free) holds `List`, `AddOrRepair`
  (replaces by name, never appends a duplicate), `Remove` and `Validate`, on `PdkReferenceManager`'s
  terms:
  - `Validate` returns what it CHECKED as well as what is wrong: parts indexed, files unreadable,
    placed parts checked, placed parts the library no longer holds.
  - It reports −1 parts for a library that could not be read.

  The GUI dialog and the CLI both call these functions. Neither holds a rule of its own.

## 2. `R-vl1-2` — the index: headers only, in memory

- **`R-vl1-2a`** `VendorLibraryIndex.Build(root, control)` walks the folder for SPICE-dialect files.
  It uses the extensions in `SpiceModelPeek.FileExtensions` and ignores anything else silently. A
  `.txt` read-me in that list yields no `.subckt`, and therefore no entry.
  It scans **headers only**: for each `.subckt` it records one entry, and it **never calls
  `SpiceNetlistReader`**. Each entry holds:
  - part name, file (relative), byte offset and length of the `.subckt` … `.ends` block, port count
  - folder path segments under the root, and file stem
  - from the header, when present: Description (raw text plus its `/`-separated fields), Property
    quantity (`C`/`L`/`R`/other) and value in SI, frequency range low/high in Hz, and the
    temperature and bias lines as text

  One more field comes out of the Description: the **case-code pair**. A description field reading
  `<digits>M(<digits>)` is a case size stated in both systems, metric first, for example
  `1005M(0402)`. Record both codes as written. Brief 2 §3d turns them into a footprint. This reads a
  header FIELD, not a part number.

  Unit spellings in the header vary (`100[MHz]-30[GHz]`, `50MHz - 20GHz`, `1.00e-05[uF]`, `10[nH]`).
  Parse them with `SpiceNumber` plus the bracketed unit. A field that does not parse is left empty,
  and the count of such fields goes in the build result, never in an exception.
- **`R-vl1-2b`** **The header is the comment block immediately above the `.subckt`**, back to the
  previous `.ends` or the start of the file. A header that belongs to no `.subckt` is ignored.
  Different editions of one library put their separator lines and spacing differently, so match field
  names case-insensitively, trim spaces either side of `:` and `=`, and test both spacings.
- **`R-vl1-2c`** **Encrypted and unreadable files are recognised and counted, never read.** A `.subckt`
  whose body is an encrypted block marks its file *encrypted*. Recognise it structurally: a protection
  directive, a protection pragma, a begin-marker followed by hex or base64 lines, or a comment stating
  the file is encrypted. Write the shapes from the format, not from any tool name. A zero-byte file is
  *empty*. The build result says, once per library: *"N files are encrypted for another simulator and
  cannot be read. The plain-text (static) edition of this library can be."* A folder with no readable
  part at all is a **refusal** with that same sentence, because an empty library in the palette tells
  the user nothing.
- **`R-vl1-2d`** A part name appearing in two files is indexed once (first by path order), and the
  duplicate is counted and named in the result. The block offset makes the entry self-sufficient:
  brief 3 reads ONLY that block.
- **`R-vl1-2e`** The index is rebuilt in memory when a workspace opens, **on a background thread**.
  Opening the workspace does not wait for it. Each file's entry list is keyed on its mtime and length,
  so `Validate` and a re-open within a session re-scan only changed files. **No on-disk cache**
  (overview D5).
- **`R-vl1-2f`** **Measure** with a scratch harness, not a test: generate a 35,000-part, ~40 MB
  library in the header shapes of overview §0, then build the index cold and warm. Record both numbers
  in `src/Design/RESOLVED.md`. The expectation is well under 100 ms warm. If it is over, look for
  something being parsed that should be scanned. **If it is still over, stop and ask** (overview D5).

## 3. `R-vl1-3` — the SPICE reader drops nothing

One dialect writes an inductor's series resistance as `L1 a b 1n Rser=0.0`. The reader carries `Rser`
through as an override, and `InductorModel` reads only `R`. So a non-zero value is **silently
ignored**, which breaks `spice-models.md` §2 property 2.

- Translate `Rser` on an `L` element to the inductor's own `R` in the reader. This is a spelling change
  in the same place as the other dialect spellings.
- An `L` element override the inductor does not take becomes a `SpiceNetlistNote`.
- Test: `Rser=5` gives an inductor whose impedance at 1 GHz has real part 5 Ω.

## 4. `R-vl1-4` — Add, Manage, and the per-user list

- **`R-vl1-4a`** **File ▸ Add Vendor Library…** opens a folder picker:
  - It builds the index, then adds the reference through `VendorLibraryRefs.AddOrRepair`.
  - The name defaults to the folder's name and is editable in a small confirm step that also shows the
    part count, the folder groups and any unreadable-file sentence.
  - It posts one summary line to Messages.
  - It is workspace-gated, like Manage PDKs.
- **`R-vl1-4b`** **Manage PDKs… becomes Manage Libraries…**, with the same dialog, Ctrl/⌘+P and
  actions.
  - It gains a **Kind** column (PDK / Vendor library) and a parts count.
  - **Add…** offers both kinds and classifies the folder by content: a folder with readable `.subckt`
    parts and no kit structure is a vendor library. It never asks the user which it is.
  - Remove warns with the count of placed parts in OPEN schematics, as the kit path already does.
  - Reveal reuses `WorkspaceViewModel.RevealPathInFileManager`.
  - Validate calls `VendorLibraryRefs.Validate`.
  - Rename the menu text everywhere it appears (all menu surfaces). Code identifiers may keep the
    `Pdk` name if renaming them is churn with no reader benefit. Say which in `src/Ui/RESOLVED.md`.
- **`R-vl1-4c`** `AppPreferences.VendorLibraries` holds the per-user list (overview D3): name, path,
  **UseInNewWorkspaces** (default true).
  - Add appends or updates the list.
  - `WorkspaceCreate.Create` takes the entries to add **as an argument** (a preference is an argument,
    `src/Design/CLAUDE.md`). The GUI's New Workspace passes the list, and the CLI's `new workspace`
    passes nothing unless `--vendor-library <name=path>` is given.
  - The Manage dialog shows the checkbox per library row.
- **`R-vl1-4d`** On workspace open, a referenced library whose folder is missing posts **one** summary
  per library, never one per part. When the per-user list knows that name at an existing path, the
  message carries a **Repair** action that calls `AddOrRepair` with that path.

## 5. `R-vl1-5` — headless

- **`R-vl1-5a`** `circuitrf import library <folder> --into <workspace> [--name <n>]` calls
  `AddOrRepair`. It prints the part count, the folder groups and any unreadable-file sentence, and has
  `--json`. A refusal (§2c) exits 1 with the sentence. `import` stays one verb with nouns.
- **`R-vl1-5b`** `circuitrf find <workspace> --parts "<query>" [--library <n>] [--limit 50] [--json]`
  lists matching parts. Each row gives:
  - library, part, value, description, frequency range
  - **the instance parameters to write** (`File=vendorlib://…`, `Name=…`, `Glyph=…`, see brief 2
    §3), so an agent can place one in a `.csch`

  The query grammar is brief 2's `VendorLibrarySearch`. If this brief lands first, put the matcher
  in `src/Design` now and let brief 2 reuse it.
- **`R-vl1-5c`** Add both to `ToolCatalog`: the `import` tool's `what=library` and the `find` tool's
  `parts`.

## 6. Tests (one per claim)

1. A generated library (3 folders, 6 files, ~300 parts, one encrypted-shaped file, one empty file)
   indexes every readable part. The encrypted and empty files are counted, and **`SpiceNetlistReader`
   is called zero times** (a counter on the reader, or an injected scanner seam).
2. Each entry's byte block, re-read alone through `SpiceNetlistReader.Read`, yields exactly that one
   cell.
3. Header parsing covers both frequency-range spellings, a bracketed and a bare unit, a missing
   Description, and a missing header (everything empty, no throw).
4. A folder of only encrypted files is a refusal carrying the sentence.
5. `.cws` round-trip: no libraries means byte-identical to today, and one library round-trips.
   `vendorlib://` resolves, and an unknown name and a missing folder give the two different reasons.
6. `Rser` (§3).
7. `import library` and `find --parts` as a process, against the in-process call (the
   `AuthoringCliVerbTests` pattern).

Run only the classes you touch.

## 7. Scope

No palette (brief 2). No placement, part picker, extraction change or archive (brief 3).

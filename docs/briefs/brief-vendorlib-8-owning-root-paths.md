# Brief VL-8 — a file reference resolves where its document lives

**Series:** [Model libraries](brief-vendorlib-6-model-library-format.md) · **Tag:** `R-vl8-n` · **Phase:** extraction, paths
**Area:** `src/Design/Schematic/SnpPathPolicy.cs`, `src/Design/Schematic/ComposedFetSymbolProvider.cs`
(`ResolvePath`), `src/Design/Schematic/VerilogAModelIntrospection.cs`, `src/Design/Schematic/NetExtractor.cs`
(cell emission), `src/Design/Schematic/SpiceModelNetlist.cs`, `src/Core/Elaboration/Elaborator.cs`
(`ResolveFileReference`, read-only here), `src/Cli/Explain.cs` (`--ref`)
**Depends on:** 6 (`ModelFolder.OwningModelFolder`)

---

## 0. Why

A relative `File` on an SnP, ComposedFET or VerilogA resolves against **one** base per run:
`Elaborator.ResolveFileReference(file, BaseDirectory)`, where `BaseDirectory` is the root of the workspace
the run started in. The editor uses the same base through `SnpPathPolicy`. That works when every document
in a run belongs to that one workspace. Two cases break it:

1. **A model folder** (brief 6). Its references are relative to the model folder, and the model is placed
   from somebody else's workspace.
2. **A cell from a referenced workspace** (`workspace.md` § Using cells from another workspace). Its SnP is
   stored relative to *its* workspace root and resolved against the *consuming* one. This is a finding to
   **verify first** (§1). It was read from the code and has not been run.

A model library cannot be shared until both resolve where their document lives.

## 1. `R-vl8-1` — confirm the referenced-cell case first

Build the fixture, not a theory. Workspace A has cell `Line` placing an SnP with `File=data/line.s2p`
(A-root-relative, the file at `A/data/line.s2p`). Workspace B references A, places `Line`, and runs an
S-parameter analysis.

- If the run already finds `A/data/line.s2p`, record which code rebases it in `src/Design/RESOLVED.md`, and
  §2 adds model folders to that mechanism rather than writing a second one.
- If it fails (missing file, or B's `data/line.s2p` silently picked up when B happens to have one), that is
  the defect §2 fixes. Record the observed behaviour **before** fixing it.

## 2. `R-vl8-2` — the owning root

- **`R-vl8-2a`** **One function** answers "what does a relative reference in this document resolve
  against": `OwningRoot.Of(documentDir)` returns the nearest ancestor model folder
  (`ModelFolder.OwningModelFolder`, brief 6) if there is one, otherwise the workspace root
  (`WorkspaceRootFinder`), otherwise null. The **nearest** wins, so a library kept inside a workspace tree
  still resolves its models against their own folders.
- **`R-vl8-2b`** `SnpPathPolicy.Resolve` and `ToStored` take their root from `OwningRoot.Of`, and so do
  `ComposedFetSymbolProvider.ResolvePath` and VerilogA introspection's path. The editor then reads the
  same file the run will. Keep `SnpPathPolicy`'s own warning: do not add a third resolver. If
  `WorkspaceRefs.Resolve` must change too, change it in the same commit.
- **`R-vl8-2c`** **Extraction rebases; the elaborator is unchanged.** When the extractor emits a cell whose
  document's owning root differs from the run's base (a model folder, or a referenced workspace's cell), it
  writes each relative file-valued parameter of that cell as an **absolute** path resolved against the
  cell's own owning root. Absolute paths pass through `ResolveFileReference` untouched. That keeps one rule
  in the elaborator, and a `.cnl` written by `circuitrf netlist` from such a design still runs on its own.
  It holds local absolute paths, which is acceptable for a derived file. **The model's own `.cnl` in the
  library stays relative** (brief 6 §3c). Only the consumer's extraction rebases.
- **`R-vl8-2d`** Which parameters are file-valued: the existing resolution sites decide (SnP `File`,
  ComposedFET `File`, VerilogA `File`, SpiceModel `File`, and any other site that calls
  `ResolveFileReference`). Collect them as **one** predicate, and test that every caller of
  `ResolveFileReference` is covered by it, so the next file-valued parameter cannot be forgotten.
  A `vendorlib://`, `pdk://` or other virtual reference is not a path and is never rebased.
- **`R-vl8-2e`** A Verilog-A source's `` `include `` files resolve relative to the `.va` itself. That is
  the compiler's rule, already true, and unchanged. Brief 10 copies them alongside.

## 3. `R-vl8-3` — `explain --ref`

`explain --ref <value> --from <document>` reports the owning root it used and which walk produced it
(model folder, or workspace). That walk is the one thing a user cannot see when a reference resolves
somewhere unexpected.

## 4. Tests (minimal)

1. The §1 fixture: B's run uses A's `data/line.s2p`. Then add a decoy `B/data/line.s2p` with different
   data, and B's result does not change.
2. A synthetic model folder inside a workspace tree, holding `models/x.s2p`: `OwningRoot.Of` its schematic
   folder is the model folder, not the workspace.
3. `SnpPathPolicy.Resolve` on the model's `.csch` finds `models/x.s2p`, the same file a run uses.
4. The rebasing predicate covers every `ResolveFileReference` call site (a source scan, as other gates in
   this repo do).

Run only the classes you touch.

## 5. Docs

`workspace.md` § Using cells from another workspace: one paragraph saying a referenced cell's files
resolve in its own workspace, if §1 found that they did not. `model-library-format.md` (brief 6) already
states the model-folder rule.

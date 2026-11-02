# Sonnet Brief — AUT-3: Authoring verbs — workspace, cell, part

**Read `brief-automation-0-overview.md` first**, and note that this brief **depends on AUT-2**
(`brief-automation-2-schematic-below-the-firewall.md`) — the `new cell` verb cannot write a symbol or
a schematic view until the schematic and symbol models are below the firewall. The architecture is
`docs/design/automation-architecture.md`; R-aut-2 and R-aut-5 are what this brief implements.

**Scope: creating correct initial documents, headlessly.** Three verbs. **No per-primitive edit
verbs** (R-aut0-5) — no `place-instance`, no `add-wire`, no `set-parameter`. Once a document exists,
the way to change it is to write it, because it is already readable JSON and the format is the
contract (R-aut-5).

---

## 1. What the caller gets

```
circuitrf new workspace <dir> [--name N] [--tech <id>]
circuitrf new cell <workspace-or-dir> <cellName> [--views symbol,schematic,layout]
circuitrf import part <file> --into <workspace-or-cell-dir> [--cell N] [--variant V]
```

- **`new workspace`** — a directory, a `.cws`, and optionally a shipped technology copied in with
  `DefaultTechRef` pointed at it. The same four steps `WorkspaceViewModel.NewWorkspace:1292` performs
  after its dialog returns.
- **`new cell`** — a cell folder with the correct sub-folder structure and view files, created
  through `CellFolder.CreateCellFolder` (`src/Design/Cells/CellFolder.cs:103`), which both
  `ComponentImport.cs:174` and `SubcircuitCellBuilder.cs:790` already call.
- **`import part`** — a footprint and its symbol brought in as a cell, through the existing
  `ComponentImport.Import` (`src/Ui/Layout/ComponentImport.cs:157`), which is already framework-free
  and whose only cross-namespace dependency is `CircuitRF.Ui.Schematic` — the thing AUT-2 moves.

**`--json` (AUT-1) applies to all three**, reporting the paths created. A caller's next step is
usually to read or rewrite one of those files, so the paths are the result.

---

## 2. The rule that decides the implementation

**R-aut3-1. Each verb calls a capability that the GUI's own command also calls — after this brief,
there is exactly one implementation of each.** R-aut-2: an operation that exists only inside a view
model is not a capability. This brief is therefore two-sided, and the GUI side is not optional:

| Capability | Lives in | Called by |
|---|---|---|
| Create a workspace | `src/Design/Workspace` | the `new workspace` verb **and** `WorkspaceViewModel.NewWorkspace` |
| Create a cell folder with views | `src/Design/Cells` | the `new cell` verb **and** the GUI's New Cell command |
| Import a part | `src/Design` (post-AUT-2) | the `import part` verb **and** the GUI's component import |

**A verb that re-implements what the view model does is the failure this brief exists to prevent.**
The two will diverge, silently, and the first symptom will be a workspace created headlessly that the
GUI treats as subtly malformed.

**R-aut3-2. Extract the capability; leave the dialog.** `NewWorkspace:1292` is a dialog, a dirty-work
prompt, then four operations. Only the four move. The view model keeps its dialog, its prompt, and
everything it does *after* the save — cache resets, tree refresh, launch action — which are shell
concerns.

---

## 3. `new workspace`

**R-aut3-3. The headless default is the GUI's default.** Whatever `NewWorkspaceDialog` pre-selects,
the verb selects with no flag. A headless default that differs from the dialog's is a second product.

**R-aut3-4. The shipped-technology copy is part of the capability, not the dialog.** The GUI copies
the chosen shipped technology's **own raw bytes** into `<ws>/tech/` and sets `DefaultTechRef` to the
relative path (`:38-52`). That byte-for-byte copy is deliberate — do not re-serialize a `Technology`
through `TechPersistence` on the way, which would produce a different file.

`ShippedTechnologies` (`src/Ui/Layout/ShippedTechnologies.cs`) must be reachable from `src/Design`.
The class is already framework-free by explicit design, but **its resources are `EmbeddedResource`
items in `src/Ui/CircuitRF.Ui.csproj`** — move those entries with the class, or the class compiles,
enumerates nothing, and reports nothing (AUT-2 R-aut2-8 flags the same trap).

**R-aut3-5. `--tech` names an id from `ShippedTechnologies.All`, and an unknown id is a refusal that
lists the valid ones.** Not a fallback to the default: a caller that asked for a specific process and
silently got another has a wrong design and no way to know.

**R-aut3-6. Refuse to create over an existing workspace.** `WorkspaceLock` already exists and its own
header explains what is at stake when two things believe they own one `.cws`. The verb neither
overwrites nor merges.

---

## 4. `new cell`

**R-aut3-7. A cell created headlessly satisfies the primacy rules, or it is not created.**
`CellFolder.ResolvePrimary` (`:143`) has real states — `MissingNamedPrimary`, an empty sub-folder,
more than one file needing a named primary (`:188`). A verb that creates a folder the resolver then
reports as broken has produced something worse than nothing.

**R-aut3-8. `--views` selects which of the three views are created, and each is created empty but
valid** — a `.csym`, `.csch` or `.clay` that `SymbolPersistence` / `SchematicPersistence` /
`LayoutPersistence` load without error and that the GUI opens on an empty canvas. Default: symbol and
schematic, matching the GUI's own New Cell.

**R-aut3-9. Name validation is `NameValidator`'s, not the verb's.** `src/Design/Cells/NameValidator.cs`
already exists. A second rule set would let a headless caller create a name the GUI rejects.

---

## 5. `import part`

**R-aut3-10. The verb adds no import logic.** `ComponentImport.Build`/`Import` already resolve the
part, build the layout variants and the symbol, and produce `ImportResult` with its message list
(`:21-27`, `:48-58`). The verb passes a file and a destination, and reports what came back.

**R-aut3-11. The default layer mapping is the one the GUI's dialog pre-selects.** `circuitrf convert`
already fixed this rule for interchange imports (repo `CLAUDE.md`) and it applies unchanged: headless
takes the dialog's default, and anything the dialog would have *asked* is a refusal that prints the
inference and the flags that answer it — never a guess.

**R-aut3-12. `ImportResult.Messages` reaches the caller in full.** Import messages are how a caller
learns a pin was inferred, a layer was dropped or a variant was skipped. They go to stderr as today,
and into the `--json` document as diagnostics (AUT-1 R-aut1-6).

---

## 6. Verb shape

**R-aut3-13. `new` is one verb with a noun, not three verbs.** R-aut-9 — the surface has a standing
cost and few broad verbs beat many narrow ones. `new workspace` / `new cell`, and `import part`
alongside the existing `convert`. Adding `new schematic` later is a noun, not a fourth top-level verb.

**R-aut3-14. Follow `cli.md` §9.** Results to stdout, everything else to stderr; the exit-code rule
chosen honestly for a verb that either created the artifacts or did not (0 or 1 — there is no
`2` here, because there is no convergence to fail); `--kits` already applies to every verb via
`TakeKitFolders`; a line in `PrintHelp`.

---

## 7. Gates

1. **Byte identity against the GUI's own path**, per R-aut0-6 and following `EmCliVerbTests`: create
   a workspace through the extracted capability with the same inputs the GUI passes, and assert the
   `.cws` and the copied `.ctech` are byte-identical to what a GUI-path creation produces. Same for a
   cell folder (every file, and the folder structure) and for an imported part.
2. **The GUI path actually calls the extracted capability.** A source-scan or a call-graph assertion,
   because R-aut3-1's whole value evaporates if the view model keeps its own copy. **Strip comments
   before scanning** — that trap is recorded in `project-brief-harmonicarf-h8`.
3. **Round trip**: `new workspace` → `new cell` → `import part` → open the workspace in the GUI's own
   scanner (`WorkspaceScanner`) and assert the tree is what the GUI would have built.
4. **The end-to-end that justifies the series**: `new workspace`, `new cell`, write a `.csch` by hand
   as a fixture, extract it to a `.cnl`, `circuitrf sparam` it, and assert a known S-parameter. This
   is the first test in the repo that proves a design can be authored and simulated with no display
   involved at any step.
5. **Refusals**: existing workspace (R-aut3-6), unknown `--tech` id (R-aut3-5), an invalid cell name
   (R-aut3-9), an import needing a decision the dialog would have asked for (R-aut3-11). Each exits 1
   with a sentence naming what is wrong and what would fix it.

---

## 8. On completion

Write the findings to `src/Cli/RESOLVED.md` and `src/Design/RESOLVED.md` — **never to a `CLAUDE.md`**.
Update `docs/design/cli.md` §2's verb table and §9, `docs/design/workspace-and-project-tree.md` with
the extracted creation capability, and `docs/design/automation-architecture.md` §3.

**Report, do not silently absorb:**
- Anything in `NewWorkspace` or the GUI's New Cell that could not be extracted without dragging shell
  state below the wall.
- Any byte difference in §7.1 between the headless and GUI artifacts, and which one is right.
- The `ShippedTechnologies` resource move (R-aut3-4) if it landed here rather than in AUT-2.

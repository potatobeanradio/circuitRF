# Brief series PLG — plugins: tools other people write, that circuitRF draws

**Design:** `docs/design/plugins.md`. Read it first; this series builds it, and every rule it states holds here.

**Goal.** A contributor can write a tool for circuitRF in their own repository — a filter synthesiser, a coupler
generator, an impedance calculator, an importer — and a user can install it, use it in a panel that looks and
behaves like the rest of circuitRF, place what it makes in a workspace, and remove it again. The same tool runs
headlessly from the CLI and MCP.

**Why.** Today a tool that is not part of circuitRF has no way in:
- **No install path.** A tool has to be merged into this repository to appear in the application, so every niche
  tool becomes code this project maintains.
- **No access to the workspace.** A tool outside circuitRF cannot read the workspace's substrate, technology or
  cells, so it re-enters them by hand and can disagree with them.
- **No way back in except interchange files.** A synthesised design comes back as DXF or GDSII and is rebuilt into
  a cell, an EM setup and ports by hand. A field test of exactly this flow (GitHub discussion #51) is what started
  this series.
- **No shared pictures.** A tool draws its own plots and layouts, in its own style, with none of the Data Display's
  markers or the layout editor's colours and rulers.

## The briefs, in order

| Brief | What | Depends on |
|---|---|---|
| `brief-plg-1-runtime.md` | Manifest, discovery from a folder, the process host and frames, permission, `action` plugins with a parameter dialog, the staging folder and **Place in workspace**, Tools ▸ Plugins | — |
| `brief-plg-2-panel.md` | `panel` plugins: the layout tree, the widget set, readouts and messages, the update / cancel / progress loop, the plugin window | 1 |
| `brief-plg-3-views.md` | The views: `plot` and `smith` on the Data Display with bound markers, `table`, `layout` with handles, `schematic`, `image` | 2 |
| `brief-plg-4-host-services.md` | `simulate`, `resolve`, `evaluate`, `lineModel`; `window` plugins | 1 (2 for the panel's busy state) |
| `brief-plg-5-cli-mcp.md` | `circuitRF plugin list/describe/run/install/uninstall` and the MCP `plugin` tool | 1, 3 |
| `brief-plg-6-install.md` | Settings ▸ Plugins, `plugins/index.json`, install from the list, an address or a folder; updates, rollback, Python environments, the policy switch | 1 |
| `brief-plg-7-docs-sdk-example.md` | The Python helper package, a NativeAOT C# `window` plugin with a headless form, the example plugins, the user doc and the plugin-author guide | 2, 3, 4, 6 |

Briefs 4 and 6 can run in parallel with 2–3 once 1 is in. Brief 5 needs 3 because `run` writes views as files.

## Decisions already made

- **D1 — Out of process, always.** No plugin assembly is ever loaded into circuitRF (`plugins.md` P-1). No brief
  adds an in-process path "for speed".
- **D2 — circuitRF draws; the plugin describes.** Every view is drawn by circuitRF's existing renderers from data and
  documents the plugin returns (P-2). No plugin-supplied control, XAML, style or colour.
- **D3 — The frame is the PCell wire's.** `[uint32 jsonLen][uint32 binLen][json][int64…]`, through the existing
  codec (`PCellWireFrame`/`PCellWireCodec`), not a second one. Layout shapes and handles are the PCell wire's
  shape vocabulary and editor hints, not a new geometry format.
- **D4 — Physical values in base SI; geometry in DBU.** `quantity` parameters arrive and readouts return in base SI
  with their unit. Layout shapes are int64 DBU with the technology's `dbuPerMicron` (the wire's version-2 rule).
- **D5 — circuitRF writes the workspace; a plugin writes only its staging folder** (P-5). Placement validates,
  checkpoints and goes through `CellCreate`/`ComponentImport`.
- **D6 — Permission is per user, keyed by installed directory, never in a `.cws`** (P-6), with the same
  policy-file / environment / preference precedence `ExternalWorkerPolicy` has.
- **D7 — Headless parity.** Anything a panel plugin can do, `circuitRF plugin run` and MCP can do (P-7).
- **D8 — The vocabulary starts at what `plugins.md` lists and grows only for a real plugin** (P-8).
- **D9 — The new code lives in `src/Design/Plugins/` (everything that does not draw) and `src/Ui/Plugins/` (everything
  that does).** `tests/Firewall.Tests` holds the first.

## Risk, stated before the series starts

- **Briefs 1, 5, 6:** low to medium. Process hosting, trust and placement all have working precedents
  (`PCellWorkerTransport`, `PCellTrustStore`, `CellCreate`, the updater's verified download). The risk in 6 is
  Python environments on Windows ARM64 and on a machine with no compiler for a requirement that needs one; the
  brief makes that a stated refusal, not a hang.
- **Brief 2:** medium. The widget set is small, but the update loop has to stay correct under a slow or dead
  plugin: superseded replies dropped, no UI thread ever waiting on a pipe, a restart after a crash.
- **Brief 3:** medium. Each view reuses a renderer, but each renderer today is fed by a document on disk inside an
  editor. Feeding them from a reply without copying their code is the work, and `netlist --toSchematic`'s drawing
  must move out of `src/Cli` first.
- **The real risk is the contract itself.** Once a plugin exists outside this repository, a field renamed in
  this series is a broken plugin we cannot fix. Brief 7's example plugins are written against the finished
  contract before anything is published; a change after that is a new `apiVersion`.

## Gates, for the whole series

1. **A plugin cannot take circuitRF down.** A plugin that crashes, hangs, writes garbage to stdout, writes outside
   its staging folder, or never answers leaves circuitRF responsive, says what happened, and loses no user work.
   Each is a test against a deliberately broken example plugin.
2. **Nothing runs without permission.** Listing, describing and the Plugins tab read manifests only; a counting
   probe proves no process started.
3. **Headless and panel agree.** `plugin run` and the panel give byte-identical staged documents for the same
   parameters.
4. **Placement is undoable.** A placed cell is one history checkpoint, and restoring it removes the cell.
5. **An example plugin, written only from the user doc, works end to end** (brief 7) — once in Python as a panel,
   once in C# as a NativeAOT `window` plugin.

## Rules that apply throughout

- **Tests:** one per claim, named classes only. Never all of `Ui.Tests`, never the full suite. No new
  `Category=Benchmark` test and no timing assertion. Example plugins used by tests live in `tools/plugin-examples/`
  and reference nothing in the repository.
- **Every UI piece is rendered with `tools/UiSnapshot` and looked at**, and the PNGs are listed in the completion note.
- **Pickers over text boxes** wherever the valid values can be listed (`src/Ui/CLAUDE.md`). Controls never shift.
- **No commercial tool or PDK names** in code, docs, the index or example plugins. The examples are generic RF tools.
- **No citations and no other tool's name** in the code or the docs. Filter and line equations are written out in
  our own words.
- Findings go in the relevant `RESOLVED.md`, never `CLAUDE.md`.

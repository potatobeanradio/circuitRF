# Brief: polish-messages-coverage — log every file write; path shown once; links on opens

**Goal.** Make the Messages panel a reliable record of disk activity and clean up file-path display:
(1) every genuine file write logs a message with the file path; (2) a file path appears **once** (as
the clickable link), never duplicated in the body text; (3) "opened" files also get a reveal link;
(4) external-open failures (e.g. Generate Netlist can't open `.cnl`) log a message.

Authority: laundry-list "Message Panel" items (missing save logs; path-once; links on opens; netlist
external-open failure). Size: **S–M** (an audit + small caller edits; no new types). Pairs with
**brief-polish-messages-ux** (B6).

## Background (confirmed)

- `IMessageSink.Post(level, text, filePath = null)` with `Info/Success/Warning/Error` helpers.
- `MessagesView` **already** renders a clickable reveal-in-file-manager link for any message whose
  `FilePath` is non-null (`RevealFileCommand`). So links are a *view feature already shipped* — the work
  here is making **callers** pass `filePath` and stop embedding the path in `Text`.

## Convention (the single rule that fixes "path twice")

> When a message refers to a file, put a short **label** in `Text` and pass the path as **`filePath`**.
> Never embed the path in `Text` when you also pass `filePath` — the view shows the path once, as the link.

Examples:
- `Messages.Success($"Saved: {path}", path)` → `Messages.Success("Saved", path)`
- `Messages.Success($"Wrote netlist to {path}", path)` → `Messages.Success("Wrote netlist", path)`
- `Messages.Info($"Opened {path}", path)` → `Messages.Info("Opened", path)`

Audit every `Post/Info/Success/Warning/Error` call that passes a `filePath` (or embeds a path in the
text) and apply the convention. After this pass, no message should contain a path substring in `Text`
when `filePath` is set.

## Coverage — every genuine disk write logs (with filePath)

Audit these write sites in `WorkspaceViewModel` (and neighbours) and ensure each posts a
`Success`/`Info` with the written file's path. Add a log where missing; normalize existing ones to the
convention above:

- `WriteWorkspaceFile` (the `.cws`) — note its `silent` parameter (below).
- `SaveSingleDocument` / the schematic + symbol single-save paths.
- `ExecuteSavePlan` (scratch → workspace): log **each** file the plan writes.
- Materialized-write and orphaned-session writes inside `SaveAllDocuments` and `PromptSaveBeforeClose`.
- `SaveLooseToWorkspace` / `SaveLoosePlainFile`.
- `SaveScratchSymbolToCell` / `SaveScratchSymbolAsFile`.
- `WriteNetlist` / `GenerateNetlist` (the `.cnl`).
- `MakePrimary` (writes `.ccell`).
- Cell/view creation: `NewCellInWorkspace`/`NewCellAsync`, `NewSchematicAsync`, `NewSymbolAsync` (each
  creates a file on disk).
- `CopyToWorkspace` (copies a known file into the workspace).

Use `Success` for user-initiated saves/creations/copies; `Info` is fine for incidental-but-genuine
writes you decide to surface. Keep the label short ("Saved", "Created", "Copied", "Wrote netlist",
"Made primary").

## "Opened" files get a link

Where the app opens a document for the user, post an `Info("Opened", path)` so the message carries a
reveal link. Cover: open-from-project-tree (`OpenNode` / `OpenOrActivateSchematic` /
`OpenOrActivateSymbol`), the new cell-context-menu opens (`OpenCellSchematic`/`OpenCellSymbol` — see
brief-polish-cell-open-menu), and Open-Recent/Open-Workspace. Don't double-log when merely *activating*
an already-open tab — log on actual open, not on re-activation (guard with the same "already open"
check the openers use).

## External-open failure logs (supersedes the gennet try/catch)

`GenerateNetlist` opens the `.cnl` in the OS default editor via the `OpenPathExternal` helper. Today a
failure ("No external editor is configured for .cnl files…") can be silent because `Process.Start("open"/"xdg-open", …)`
doesn't throw on a non-zero handler result. Make the failure observable, best-effort:

- Wrap the launch in try/catch → on exception, `Messages.Warning($"Couldn't open externally", path)`
  (label-only text per convention; include the reason in the label if available, e.g.
  `"Couldn't open externally: {ex.Message}"` — but keep the path in `filePath`, not the text).
- On macOS, prefer launching with a redirected process and checking the exit code: `open` returns
  non-zero when no application is configured for the type — surface that as the same Warning.
- This replaces the simpler try/catch specified in brief-gennet-generate-netlist; if that brief already
  landed, update its `OpenPathExternal` site to this best-effort version. Don't block netlist
  generation on the open result — the `.cnl` is already written and logged; the editor open is a
  convenience.

## DECISION NEEDED — high-frequency incidental writes

Two write paths fire very frequently and would **spam** the panel if logged literally:
- **`CellParameterEditModel.Save`** writes the `.ccell` on *every* parameter add/remove/rename/edit
  (and on undo). Logging each would flood the panel during parameter editing.
- **Debounced `.cws` config autosave** (filter-toggle / tree-state changes) writes the `.cws` with
  `silent: true` today.

Your instruction was "every file write except recovery." Recommended default (pending your call): treat
these two as **incidental like recovery** — *don't* log per-edit `.ccell` autosaves or the debounced
`.cws` config write; *do* log user-initiated saves, creations, copies, netlist writes, make-primary,
and the explicit Save-All `.cws` write (B19, non-silent). If you'd rather see literally every write,
say so and I'll drop the exclusions (and we accept the per-edit noise).

Mechanically: keep the `WriteWorkspaceFile` `silent` flag for the debounced autosave path (no message);
the explicit Save-All / Save-Workspace calls pass `silent: false` (logged). Add an analogous "quiet"
write for `CellParameterEditModel.Save`, or simply don't post from it.

## Acceptance

- Saving any document (schematic/symbol/cell), generating a netlist, making a view primary, creating a
  cell/schematic/symbol, and copying a known file each produce exactly **one** message, with the path as
  a clickable link and **no** path text duplicated in the body.
- Opening a file from the tree (or the cell context menu) produces an "Opened" message with a link.
- Generate Netlist with no configured `.cnl` handler produces a Warning in the panel (path linked).
- Per-edit `.ccell` autosaves and debounced `.cws` config writes do **not** spam the panel (per the
  default above, unless you change it).

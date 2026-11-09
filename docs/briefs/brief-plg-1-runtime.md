# Brief PLG-1 — the plugin runtime: manifest, process, permission, placement

Series: `brief-plg-0-overview.md`. Design: `docs/design/plugins.md` §1–3, §6, §9.4. Depends on nothing.

**Why.** There is no way for a program outside circuitRF to be listed in it, be started by it with the workspace's
context, or hand back documents that land in the workspace as cells. This brief is that path, for the simplest
plugin shape: a parameter dialog, one run, and placement. Panels and views come in briefs 2 and 3.

## What to build

### 1. The manifest — `src/Design/Plugins/PluginManifest.cs`

- A reader for `plugin.json` exactly as `plugins.md` §3 and §3.1 specify: `apiVersion`, `id`, `name`, `version`,
  `summary`, `author`, `license`, `homepage`, `minCircuitRF`, `run`, `kind`, `menu`, `contexts`, `params`,
  `timeouts`, and `ui` kept as raw JSON for brief 2.
- **Validation returns diagnostics, never throws and never posts** (`src/Design/CLAUDE.md`). Each refusal names the
  field: a duplicate parameter name, a `choice` default not in its options, a `quantity` whose unit circuitRF does
  not know, a `run` with no entry for any platform, an `id` that is not `[a-z0-9.-]+`.
- `run` resolution picks this platform's RID entry, else `python`, else reports "no build for <rid>".
- `apiVersion` above `PluginApi.Max` lists the plugin dimmed with "needs a newer circuitRF". Unknown optional fields
  are ignored.

### 2. Discovery — `PluginCatalog`

- Reads every `<plugins root>/<id>/<version>/plugin.json` and every **linked** folder (a list in preferences,
  added by brief 6's Link folder…; for this brief a test seam and `CRF_PLUGIN_PATH`, `;`/`:`-separated).
- **Reads manifests only.** It starts nothing, imports nothing and runs no interpreter. A test with a counting
  process-start seam holds this (series gate 2).
- One active version per `id`: the one recorded as active in preferences, else the highest.

### 3. The process host — `PluginSession`

- Starts the resolved program with stdin/stdout/stderr redirected, working directory = the plugin folder.
  `python` plugins start the discovered interpreter (`PythonInterpreterDiscovery`; brief 6 swaps in a per-plugin
  environment) with `-u` and the entry path.
- **Frames through the existing `PCellWireFrame`/`PCellWireCodec`**, not a copy. If the codec's types are named
  for PCells, give them a neutral home and leave the PCell names as the users of it; do not fork it.
- **Messages:** `hello`, `update`, `action`, `commit`, `cancel`, `bye`, and the plugin's `reply`, `placed`,
  `progress` (`plugins.md` §6.2). `host` calls are refused with "not supported in this version" until brief 4.
- **Every read and write is off the UI thread**, with the deadlines in §6.2 and the manifest's `timeouts`. A
  deadline passed is a `PluginFailure.Timeout` result, never an exception crossing to the caller, and the process
  tree is killed (`BoundedProcess`'s rules: drain both pipes, kill the tree).
- **stderr** goes to a 200-line ring the caller can read, and to the run log.
- **Protocol errors are failures with a sentence**, not crashes: bytes on stdout that are not a frame, a frame
  that does not decode, a reply to an unknown `id`, a reply of the wrong type. The sentence quotes at most the
  first 200 bytes.
- `bye` waits 2 s, then kills.

### 4. The staging folder

- One per session: `<scratch>/plugins/<id>/<session-guid>/`, created before `hello` and named in it.
- **A plugin writes only there.** After every `reply` and `placed`, every path the plugin names is resolved and
  must be under the staging folder; anything else is refused by name. (circuitRF cannot stop a process writing
  elsewhere — §9.4 says so — but it never *uses* a file from elsewhere.)
- Deleted when the session ends, unless the user chose Keep staging in the dialog (for plugin authors).

### 5. Permission — `PluginTrustStore` and `PluginPolicy`

- **Per user, keyed by the plugin's absolute installed directory**, stored in preferences, never in `.cws` — the
  `PCellTrustStore` shape and its reasoning, in a store of its own so revoking a kit does not touch a plugin.
- `PluginPolicy` resolves whether plugins may run at all: a `no-plugins` file beside the install, then
  `CRF_NO_PLUGINS=1`, then a preference (default on). Same precedence and same record shape as
  `ExternalWorkerPolicy`; it lives below the firewall with the preference passed in as an argument.
- `PluginSession.Start` refuses with `PluginFailure.NotPermitted` / `PluginFailure.DisabledByPolicy` before starting
  any process.

### 6. The UI — `src/Ui/Plugins/`

- **Tools ▸ Plugins** submenu, grouped by `menu`, built from `PluginCatalog`. The macOS native menu gets the same
  items (`NativeMenu` instance never replaced; change `Items`). An empty catalogue shows one disabled item, "No
  plugins installed", and a **Manage Plugins…** item that opens Settings ▸ Plugins once brief 6 adds it (until
  then, hidden).
- **The permission prompt** — `PluginTrustDialog`, the `PCellTrustDialog` layout: Name, Version, Author, Source
  (folder, address, or "the vetted list"), Runs (the executable, or interpreter + entry), and the sentence "A plugin
  runs with your permissions and can read and write your files." Allow / Don't Allow; Don't Allow is remembered
  until the user changes it in Settings.
- **`action` plugins:** a parameter dialog built from `params` with the parameter editor's existing controls
  (`quantity` uses the same entry the analysis editor uses, so SI prefixes and expressions work; `choice`,
  `substrate`, `technology`, `cell`, `result` are pickers). OK sends `hello`, `update`, then `commit`, with a busy
  bar and Stop. `messages` from the reply are shown in the dialog; an `error` keeps it open.
- **`panel` plugins** are listed and, until brief 2, open the same dialog. **`window` plugins** are listed dimmed
  until brief 4.

### 7. Placement — `PluginPlacement` in `src/Design/Plugins/`

`plugins.md` §6.3, in this order:
1. Validate each staged file with its kind's validator (`CellViewFileValidator`, and the `check` validators for a
   `.cnl`/`.cem`). Any error refuses the placement and shows both the plugin's messages and the validator's.
2. The cell name: the plugin's `name` in `placed`, else the plugin's `id` tail; Replace / New name when it exists,
   using the dialog ISC-1 added.
3. A history checkpoint whose intent is "Place <plugin name> <version>" (`GitCheckpoint`), skipped silently when the
   workspace keeps no history, as every other checkpoint is.
4. Write through `CellCreate` / `ComponentImport`, then refresh the Project Tree and select the new cell.

The function takes everything as arguments and returns its result, so brief 5's `--place` calls the same one.

## Gates

1. **Manifest:** every refusal in §1 has a test naming its field; a valid manifest round-trips.
2. **Discovery runs nothing:** the counting probe sees zero process starts over a catalogue of 3 plugins.
3. **A plugin cannot take circuitRF down** — one test each, against `tools/plugin-examples/broken/`, a single script
   with a mode argument: exits at once; never answers (deadline, tree killed); prints a non-frame line to stdout;
   sends a malformed frame; replies to a wrong `id`; names a file outside staging; writes 10 MB to stderr. Each
   ends in the named `PluginFailure`, the caller unblocked, and no process left running.
4. **Permission:** no process starts for an `Unknown` or `Denied` plugin; the policy file beats the environment,
   which beats the preference; a workspace carrying a trust entry is ignored.
5. **Placement:** `tools/plugin-examples/hello/` (an `action` plugin that writes a two-resistor `.cnl` and `.csch`)
   places a cell; the checkpoint exists; restoring it removes the cell; an invalid staged `.csch` is refused with
   the validator's sentence and writes nothing.
6. **UI:** UiSnapshot PNGs of the Tools ▸ Plugins menu (empty and populated), the permission prompt, and the
   `action` parameter dialog with each parameter kind, light and dark. Looked at and listed.

## Not in this brief

The panel and its views (2, 3), host services and `window` plugins (4), the CLI (5), installing and Settings (6).

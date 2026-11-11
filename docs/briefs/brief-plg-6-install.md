# Brief PLG-6 — installing plugins: Settings ▸ Plugins, the vetted list, addresses and folders

Series: `brief-plg-0-overview.md`. Design: `docs/design/plugins.md` §9, §10. Depends on PLG-1.

**Why.** After PLG-1 a plugin runs only from a folder found by an environment variable. A user needs to find
plugins worth trusting, install one in a click, update it, go back if an update breaks, and remove it — and a plugin
author needs to run the one they are writing without reinstalling it after every edit.

## What to build

### 1. The install store — `src/Design/Plugins/PluginInstaller.cs`

- Layout: `<app data>/circuitRF/plugins/<id>/<version>/`, plus `active.json` per `id` naming the active version and
  the previous one. Linked folders are a list in preferences (path + `id` read from its manifest).
- **Install** = unpack to `<id>/<version>.partial/`, validate the manifest (its `id` and `version` must match what was
  asked for), set up Python (§3), rename to `<version>/`, set active. Any failure deletes the `.partial` folder and
  leaves the previous version active. Never a half-installed plugin.
- **Update** = install the new version; the previous one stays on disk until the new one has started once
  successfully, then is deleted. **Roll back** sets the previous one active again.
- **Uninstall** = remove the folder and its trust entry; preferences remembered per plugin (window size, last
  parameters) are removed too.
- **Zip safety:** entries with `..`, absolute paths or links outside the target are refused by name.
- All functions take paths and preferences as arguments (`src/Design/CLAUDE.md`), so §5's `plugin install` calls them.

### 2. Three sources

- **The vetted list** — `plugins/index.json` (schema in `plugins.md` §10).
  - circuitRF ships the copy from the release it was built from, as an embedded resource.
  - **Refresh** on the Plugins tab fetches `main`'s copy and its detached signature, verified with the key the updater
    already uses (`tools/ReleaseSigner`, `PayloadVerifier`). A bad signature is refused and the shipped copy stays.
    Nothing fetches the list without Refresh being pressed.
  - Install downloads the version's asset for this platform (or `any`) and checks its SHA-256 before unpacking.
  - Add `tools/ReleaseSigner` a mode that signs `plugins/index.json`, and a line to `BUILDING.md` saying a list
    change is signed before it is merged.
- **An address** — Install from address…: a GitHub repository URL and a tag (a picker filled from the repository's
  releases once the address is entered; the tag is typed only when the release list cannot be read). Downloads the
  release asset whose name contains this platform's RID or `any`, else the tag's source archive. No credential, no
  private repositories; `GIT_TERMINAL_PROMPT=0` semantics — anything that would ask is a refusal.
- **A folder** — Install from folder… copies it in. **Link folder…** runs it in place, for an author: the manifest is
  re-read at every session start, and the permission prompt shows "Linked folder — changes take effect on the next
  start".

### 3. Python environments

- A plugin with `run.python` gets a virtual environment at `<id>/<version>/.venv`, created with the interpreter
  `PythonInterpreterDiscovery` resolves (the Settings choice kits already use), checked against `requiresPython`.
- `requirements.txt`, if present, is installed into it with `pip install --no-input -r`. **This is the only step that
  reaches the network for a Python plugin**, and the install dialog says so before it starts. Its output streams into
  the install dialog's log.
- A requirement that fails to build is a refusal quoting pip's last lines and naming the requirement; the install
  rolls back. No retry with other flags.
- `PluginSession` (PLG-1) starts the venv's interpreter when one exists.

### 4. Settings ▸ Plugins

A new tab between Security & Permissions and Revision Control.
- **Allow plugins to run** checkbox (`PluginPolicy`'s preference), disabled with the reason when a policy file or the
  environment overrides it — the device-worker checkbox's exact behaviour.
- **Installed** list: name, version, source, permitted (a picker: Ask / Allowed / Denied), and Update available.
  Buttons under the list, always present, enabled per selection: Update, Roll back, Uninstall, Open folder.
- **Available** list from the vetted list: name, summary, latest version, platforms; Install. A version needing a
  newer circuitRF is shown dimmed with the reason.
- Buttons: Refresh list, Install from address…, Install from folder…, Link folder….
- An install shows a progress dialog with its log and Cancel; Cancel leaves nothing behind.
- Tools ▸ Plugins ▸ Manage Plugins… opens this tab.
- **Allowing happens here and in the first-run prompt, and nowhere else** (owner decision, 2026-11-10; `plugins.md`
  §9.4). The command line and MCP can deny, install and uninstall, never allow. This tab is therefore where an installed
  plugin becomes runnable, and an installed plugin that is `not asked` and was installed outside the window (§5) is
  listed first, marked "Installed outside circuitRF — not yet allowed" with its origin and time, its picker on Ask.
- **A denial the command line recorded survives an open window — a security requirement, not a convenience.**
  `circuitRF plugin deny` (PLG-5) merges `plugin_trust` into `preferences.json` while the application may be running.
  Today the application reads preferences once and writes its whole copy back on every change, so it neither sees that
  denial nor keeps it past its next save — a revoked plugin would quietly become allowed again (`src/Cli/RESOLVED.md`,
  PLG-5). The trust table is read from the file whenever it is consulted (the prompt, this tab, a plugin's start) and
  written by merging that one key — never from or by the in-memory copy, and the whole-file save keeps the file's
  `plugin_trust` rather than its own. Gate: deny a plugin through the CLI with the application's preferences loaded,
  change an unrelated preference in process, and the file still says denied, the Installed list shows Denied, and
  opening the plugin from the menu does not run it.

### 5. `circuitRF plugin install` / `uninstall`, and installing over MCP

PLG-5's verb gains the two nouns it refuses today, each calling §1's functions and nothing else (the PLG-5 source scan
extends to them):

```
circuitRF plugin install <folder | address | id-from-the-list> [--tag t] [--link]
circuitRF plugin uninstall <id> [--version v]
circuitRF plugin update <id> | rollback <id>
```

- The source is inferred: an existing folder is §2's folder source (`--link` links it instead of copying), a GitHub URL
  is an address (`--tag` required when the release list cannot be read — a refusal naming it, never a guess), anything
  else is an `id` looked up in the vetted list. Output: the installed folder, id and version; `--json` as `plugin list`'s
  row. Exit 0, 1 on a refusal or failed install (the atomic rule of §1 holds), 130 cancelled with nothing left behind.
- **Install never allows.** A freshly installed plugin is `not asked`, and the install's output carries PLG-5's
  `plugin.permission.needs-user` notice: only the person can allow it, in circuitRF (§4).
- **An install made outside the window runs nothing.** `pip install` of a `requirements.txt` can run a package's build
  scripts, which is running code before anyone allowed it. So `plugin install` (CLI or MCP) unpacks, validates the
  manifest and stops; §3's environment is created when the person allows the plugin in the window, with the install
  dialog's progress and log, and a failure there leaves the plugin installed and not runnable, saying why. `active.json`
  records the install's origin (`cli`, `mcp`, `settings`) and time.

**Over MCP an agent may install and uninstall, and never allow.** Installing puts files on disk and runs nothing;
running is what executes someone else's program with the user's own permissions, and that is granted only by the person
in the window (§4). So no server flag is needed:

- The `plugin` tool's `action` gains `install` and `uninstall`. Their descriptions say they install someone else's
  program for a source the person named, and that it will not run until the person allows it in circuitRF. `tools/list`
  marks both with the MCP annotations `destructiveHint: true` and `openWorldHint: true`, so a client that confirms tool
  calls asks the person.
- MCP's `install` takes a folder under the server root, an address or a list id — never a path outside the root.
  `update`, `rollback` and `--link` are not offered over MCP: an update runs new code, and a link runs a folder that
  keeps changing.
- The flow: the user tells the agent where a plugin is; the agent calls `install` and relays the notice; the person
  allows it in circuitRF; the agent calls `describe` and `run`. The server re-reads preferences on every call
  (`PluginPreferences.Read`), so a grant made in the open window is seen by the next tool call with no restart.

### 6. The list's own gate

`tests/Ui.Tests/Plugins/PluginIndexTests.cs` reads the repository's `plugins/index.json` and fails on: a schema
error, a duplicate `id`/`version`, a missing `sha256`, or an asset URL not on `github.com`. Commercial tool and PDK
names are a review item, not a test: a test would need a list of the names, and no such list may be in the
repository.

## Gates

1. **Atomic install:** a failure at each step (bad zip, manifest mismatch, pip failure, cancel) leaves the previous
   version active and no `.partial` folder.
2. **Rollback:** an update whose new version fails `hello` is rolled back by the user in one click, and the old version
   runs.
3. **Integrity:** an asset whose SHA-256 differs is refused before unpacking; a list with a bad signature is refused
   and the shipped copy used.
4. **Zip safety:** a `..` entry and an absolute entry are refused by name.
5. **No unasked network:** with a counting HTTP seam, opening Settings, listing and starting plugins make zero
   requests; only Refresh, Install and Update do.
6. **Linked folder:** editing the linked plugin's script changes the next session's behaviour without reinstalling.
7. **CLI and MCP install:** `plugin install <folder>` and `uninstall` as a process give a byte-identical installed tree
   to the in-process `PluginInstaller` call. Over MCP the `plugin` tool's actions are `list`, `describe`, `run`,
   `install` and `uninstall` — never `allow` or `deny`; `install` puts a folder under the root in place (one outside it
   is refused), creates no Python environment and starts no process (the counting probe), and its result carries
   `plugin.permission.needs-user`; `run` then refuses with that id; after the grant is recorded as Settings ▸ Plugins
   records it, the same `run` succeeds with no server restart; `uninstall` removes the folder and its trust entry.
8. **UI:** UiSnapshot PNGs of the Plugins tab (empty; with installed and available plugins; one installed outside
   circuitRF and not yet allowed; policy-overridden), the
   install-from-address dialog, and an install in progress; light and dark. Looked at and listed.

## Not in this brief

A plugin store with ratings or search beyond the list; private repositories; automatic updates (the user presses
Update — a plugin update runs new code and is asked for, like the permission).

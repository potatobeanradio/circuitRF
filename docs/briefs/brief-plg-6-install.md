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
- All functions take paths and preferences as arguments (`src/Design/CLAUDE.md`), so PLG-5's `install` calls them.

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

### 5. The list's own gate

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
7. **UI:** UiSnapshot PNGs of the Plugins tab (empty; with installed and available plugins; policy-overridden), the
   install-from-address dialog, and an install in progress; light and dark. Looked at and listed.

## Not in this brief

A plugin store with ratings or search beyond the list; private repositories; automatic updates (the user presses
Update — a plugin update runs new code and is asked for, like the permission).

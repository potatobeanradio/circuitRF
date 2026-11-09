# Brief PLG-7 — the plugin author's kit: Python package, example plugins, user docs

Series: `brief-plg-0-overview.md`. Design: `docs/design/plugins.md`. Depends on PLG-2, PLG-3, PLG-4 and PLG-6.

**Why.** A protocol with no worked example and no guide is usable only by whoever wrote it. Most plugins will be
written by RF engineers with an AI assistant doing much of the coding, so the documentation is the interface: it has
to be complete enough that an assistant given only the user doc and an example produces a plugin that installs and
runs. The examples are also the first plugins written against the finished contract, so they are the last chance to
change it cheaply.

## What to build

### 1. `tools/plugin-python/circuitrf_plugin/` — the Python helper

References nothing in this repository except the frame format, which it implements itself (like `tools/pcell-python`,
and sharing that package's frame code if it is importable without circuitRF on the path; otherwise a copy with the
same tests).

```python
from circuitrf_plugin import Plugin, Reply, Plot, Layout, Table

plugin = Plugin()

@plugin.update
def update(p, ctx) -> Reply:
    ...
    return Reply(readouts={"z0": 50.2}, views={"response": Plot(...), "artwork": Layout(...)})

@plugin.commit
def commit(p, ctx):
    ctx.stage("stub.clay", clay_text)
    return [{"name": "stub", "files": ["stub.clay", "stub.cnl"]}]

plugin.run()
```

- Frames, `hello` negotiation, `cancel` (raises `Cancelled` in the handler at the next `ctx.check()`), `progress`,
  `bye`, and the host calls as functions: `ctx.simulate(cnl)`, `ctx.resolve(...)`, `ctx.evaluate(expr)`,
  `ctx.line_model(...)`.
- **`print` is redirected to stderr** before the handler runs, so a stray print cannot corrupt the wire.
- `Layout` builders take DBU integers and refuse floats, with a `ctx.dbu(metres)` helper that rounds once, the
  technology's way.
- Helpers to write a `.cnl` from a list of parts and a `.clay` from shapes, so a plugin author does not hand-write
  either format.
- Distributed by copying it into a plugin (it is small and dependency-free); no package index publication in this
  brief.

### 2. Example plugins — `tools/plugin-examples/`

Each is an independent implementation of the contract, written against the user doc only, and each is a test fixture:

| Folder | Kind | Shows |
|---|---|---|
| `hello/` | `action` | The minimum: one parameter, one staged `.cnl` + `.csch`, placement (used by PLG-1) |
| `calculator/` | `panel` | Quantities, readouts, messages, `enabled`, no views (used by PLG-2) |
| `stub/` | `panel` | **The reference plugin:** a single-stub matching network on a chosen substrate. Inputs: frequency, load impedance, substrate. Uses `lineModel` for widths and lengths and `simulate` for the response. Views: response plot with a marker bound to the frequency, Smith chart, dimension table, layout with a `linear` handle on the stub length, schematic. Places a cell with a `.clay`, `.csch` and a `.cem` naming its ports |
| `window/` | `window` | A script with no real window that sends `placed` (used by PLG-4) |
| `broken/` | `panel` | One script, a mode argument per failure (used by PLG-1 and PLG-2) |

The stub plugin is deliberately simple RF — every number in it can be checked by hand — so its correctness is not in
question when it is used to test the host. **No filter plugin ships here.** Filter synthesis is the kind of tool this
series exists so that someone else can own.

### 3. User documentation

- **`docs/user/src/reference/plugins.md` — using plugins.** What a plugin is; Tools ▸ Plugins; the permission prompt
  and what it means (a plugin runs with your permissions); Settings ▸ Plugins (install, update, roll back, uninstall,
  link a folder); the vetted list and what "vetted" covers (one reviewed version, not the author); turning plugins off
  for a managed installation; `circuitRF plugin` on the command line. Added to `_nav.txt` under Design, after Match.
- **`docs/user/src/reference/writing-plugins.md` — writing a plugin.** Written for an engineer who will hand it to an
  AI assistant:
  - the whole manifest schema, every parameter kind, every layout node, every view, every message and host call,
    as tables, complete enough to write a plugin from;
  - units: base SI in, base SI out, DBU for shapes, and why;
  - a walk through the stub plugin, file by file;
  - the rules circuitRF enforces (staging folder only, no stdout outside frames, deadlines, determinism);
  - testing a plugin: Link folder…, Keep staging, `circuitRF plugin run -o`, `circuitRF check plugin.json`;
  - **Getting a plugin on the list:** the release-asset layout, the SHA-256, the PR to `plugins/index.json`, and the
    review checklist from `plugins.md` §10;
  - a short "Plugin, PCell, or pull request?" section (`plugins.md` §14).
  Added to `_nav.txt` after `plugins.html`.
- **`settings.md`** gains the Plugins tab; **`cli.md`** (user) gains the `plugin` verb; **`mcp-server.md`** gains the
  `plugin` tool. Regenerate only the edited pages.
- `docs/design/cli.md` gains a `plugin` section; `docs/design/plugins.md` moves from "proposed" to "current" with any
  change the series made recorded in place (no change history in the text).

### 4. The documentation gate

A clean session (an agent with no access to this repository's source) is given only `writing-plugins.md` and asked to
write a small `panel` plugin — a quarter-wave transformer calculator with a plot and a layout. Its plugin is
installed with Link folder…, run in the panel and with `circuitRF plugin run`, and placed. Every gap it hit is fixed
in the doc, and the run is repeated until it needs no help. The finished plugin is not committed; the transcript's
list of gaps and fixes goes in the completion note.

## Gates

1. `circuitrf_plugin`'s own tests: frame round trip, partial reads, cancel, stray `print`, float coordinates refused.
2. Each example plugin passes the test that its brief named it for; `stub` passes PLG-3's and PLG-5's gates.
3. **Series gate 5:** the documentation gate above.
4. `_nav.txt` lists both new pages; the doc build's orphan check passes; regenerated pages only for edited sources.
5. **No commercial tool or PDK names** in the package, the examples or the pages (grep before handing back).

## Not in this brief

Publishing `circuitrf_plugin` to a package index; helper packages for other languages (the user doc's tables are
the contract, and a C# or Rust author implements the frame from them).

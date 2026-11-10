# Brief PLG-7 — the plugin author's kit: Python package, a C# example, example plugins, user docs

Series: `brief-plg-0-overview.md`. Design: `docs/design/plugins.md`. Depends on PLG-2, PLG-3, PLG-4 and PLG-6.

**Why.** A protocol with no worked example and no guide is usable only by whoever wrote it. Most plugins will be
written by RF engineers with an AI assistant doing much of the coding, so the documentation is the interface: it has
to be complete enough that an assistant given only the user doc and an example produces a plugin that installs and
runs. The examples are also the first plugins written against the finished contract, so they are the last chance to
change it cheaply.

Python is not the only shape plugins will arrive in. A tool that already exists as a C# desktop program — with its
own window, compiled to a single native binary — is exactly what `plugins.md` P-1 promises to accept, and nothing in
the kit shows one. Without a worked example its author has to derive the frame, the `hello` exchange, staging and the
`window` kind from tables alone, and the first such plugin would be the first test of whether those tables are
enough. A second-language example also checks the contract against an implementation that shares no code with the
Python helper.

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

### 2. `tools/plugin-examples/csharp-window/` — a native C# plugin with its own window

A `window` plugin written in C#, published with NativeAOT to one native binary per platform, that **also has a
headless form** (it declares `params` and answers `commit` when `hello`'s `mode` is `headless`, PLG-4 §3). It is the
template for an author who already has a .NET desktop tool and wants it to place cells instead of exporting
interchange files.

- **What it makes:** a microstrip line section of a given electrical length. Parameters: frequency, characteristic
  impedance, electrical length in degrees; the substrate comes from `hello`'s workspace. It asks circuitRF's
  `lineModel` host call for the width and εeff — so it shows a host call from C# — and stages a `.clay` (the line
  plus a layout port label at each end), a `.cnl` and a `.cem` naming those ports, so the placed cell opens ready
  for an EM run. Every number can be checked by hand.
- **Files, each readable on its own:**
  - `Frame.cs` — the frame codec, BCL only, written to be copied into another program unchanged: the 8-byte
    header, **little-endian** lengths and int64 payload (`pcell-wire-schema.md` §2), partial reads on a pipe, a
    clean end of stream. JSON through `System.Text.Json` **source generation**, because reflection-based
    serialisation is trimmed away under NativeAOT and fails only at run time.
  - `Protocol.cs` — the conversation: `hello` and its `mode`, `commit` → `placed`, `host`/`hostReply`, `open`,
    `bye`. All pipe I/O on a background thread; stdout is reserved for frames and `Console.Out` is pointed at
    stderr before anything else runs, the C# equivalent of the Python helper's `print` redirect.
  - `LineSection.cs` — the deterministic part: parameters + `lineModel` answer → the three staged documents. Both
    the window and the headless path call it, so they stage identical bytes.
  - `MainWindow.cs` — an Avalonia window built in code (no XAML, so there is no XAML-compilation step to explain):
    the three parameters, **Place in workspace** (sends `placed`), **Open in circuitRF** (sends `open` for the
    placed `.clay`), and a status line. The UI thread never waits on the pipe. It is the plugin's own window;
    circuitRF draws none of it and it is not a `tools/UiSnapshot` subject.
  - `plugin.json` — `kind: window`, `params`, and a `run` entry per RID pointing at `bin/<rid>/<name>`.
  - `publish.sh` / `publish.ps1` — one `dotnet publish -c Release -r <rid> -p:PublishAot=true` per RID into
    `bin/<rid>/`. A NativeAOT publish needs the platform's native toolchain and cannot cross operating systems;
    the scripts say so and stop rather than skip a RID silently. `publish.ps1` is pure ASCII (the packaging rule's
    reason applies to any `.ps1`).
  - `README.md` — build, link, run, and what to change first when turning it into your own tool.
- **Self-contained from this repository.** It references no project here, is **not** in `circuitRF.slnx` (a plain
  `dotnet build` neither builds it nor restores Avalonia for it), and carries its own empty `Directory.Build.props`
  and `Directory.Build.targets` so the repo root's version stamping and test settings do not leak in. Copied out of
  the repository, it must build exactly as it does inside it.

### 3. Example plugins — `tools/plugin-examples/`

Each is an independent implementation of the contract, written against the user doc only, and each is a test fixture:

| Folder | Kind | Shows |
|---|---|---|
| `hello/` | `action` | The minimum: one parameter, one staged `.cnl` + `.csch`, placement (used by PLG-1) |
| `calculator/` | `panel` | Quantities, readouts, messages, `enabled`, no views (used by PLG-2) |
| `stub/` | `panel` | **The reference plugin:** a single-stub matching network on a chosen substrate. Inputs: frequency, load impedance, substrate. Uses `lineModel` for widths and lengths and `simulate` for the response. Views: response plot with a marker bound to the frequency, Smith chart, dimension table, layout with a `linear` handle on the stub length, schematic. Places a cell with a `.clay`, `.csch` and a `.cem` naming its ports |
| `window/` | `window` | A script with no real window that sends `placed` (used by PLG-4) |
| `csharp-window/` | `window` + headless | §2: a NativeAOT C# program with its own Avalonia window, a host call, and a headless `commit` |
| `broken/` | `panel` | One script, a mode argument per failure (used by PLG-1 and PLG-2) |

The stub plugin is deliberately simple RF — every number in it can be checked by hand — so its correctness is not in
question when it is used to test the host. **No filter plugin ships here.** Filter synthesis is the kind of tool this
series exists so that someone else can own.

### 4. User documentation

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
  - **Writing a plugin in C# (or any compiled language):** the frame byte by byte with a worked hex example, the
    `run` RID table, `window` vs `panel` and when a native window is the right choice, the headless `mode`,
    NativeAOT's two traps (source-generated JSON; publish per OS on that OS), and a walk through `csharp-window/`;
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

### 5. The documentation gate

A clean session (an agent with no access to this repository's source) is given only `writing-plugins.md` and asked to
write a small `panel` plugin — a quarter-wave transformer calculator with a plot and a layout. Its plugin is
installed with Link folder…, run in the panel and with `circuitRF plugin run`, and placed. Every gap it hit is fixed
in the doc, and the run is repeated until it needs no help. The finished plugin is not committed; the transcript's
list of gaps and fixes goes in the completion note.

A **second** clean session, given only `writing-plugins.md`, writes the same calculator in C# as a `window` plugin
with a headless form, published with NativeAOT for this machine's RID. It is run with `circuitRF plugin run --place`
and opened from Tools ▸ Plugins; the same rule applies — every gap fixed in the doc, repeated until it needs no help,
not committed, gaps listed.

## Gates

1. `circuitrf_plugin`'s own tests: frame round trip, partial reads, cancel, stray `print`, float coordinates refused.
2. Each example plugin passes the test that its brief named it for; `stub` passes PLG-3's and PLG-5's gates.
3. **`csharp-window` end to end, on this machine's RID:** published with NativeAOT by its own script, linked,
   `circuitRF plugin run --place` stages and places a cell whose `.clay`, `.cnl` and `.cem` pass `circuitRF check`,
   and the `.cem`'s `run analysis=em dryRun=true` reports no refusal. Opened from Tools ▸ Plugins, Place in
   workspace places **byte-identical** documents for the same parameters (series gate 3), and quitting circuitRF
   closes it within `bye`'s 2 s. A publish takes tens of seconds and needs the native toolchain, so this is run in
   the brief and recorded in the completion note, not added to the test suite.
4. **The frame does not drift under the C# example:** `Frame.cs`'s encoding of a fixed set of messages (header,
   empty binary part, a non-empty int64 part, non-ASCII JSON) is committed as golden `.bin` files beside it, and a
   routine test decodes each with `PCellWireCodec` and with the Python helper. A change to the host's codec that
   would break a compiled plugin fails here, without building one.
5. **Series gate 5:** both documentation gates above.
6. `_nav.txt` lists both new pages; the doc build's orphan check passes; regenerated pages only for edited sources.
7. **No commercial tool or PDK names** in the package, the examples or the pages (grep before handing back).

## Not in this brief

Publishing `circuitrf_plugin` to a package index; a NuGet package for the C# frame code (`Frame.cs` is copied, as
the Python helper is); helper packages for other languages (the user doc's tables are the contract, and a Rust or C
author implements the frame from them); signing or notarising a plugin's binary (PLG-6 installs it, the author signs
it).

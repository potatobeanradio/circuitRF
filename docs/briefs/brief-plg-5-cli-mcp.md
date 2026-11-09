# Brief PLG-5 — `circuitRF plugin` and the MCP `plugin` tool

Series: `brief-plg-0-overview.md`. Design: `docs/design/plugins.md` §11, P-7. Depends on PLG-1 and PLG-3.

**Why.** A plugin that only works in a window is invisible to scripts, CI and an automated design agent — exactly
the clients the automation architecture exists for (`automation-architecture.md` R-aut-2). A filter plugin should be
usable by an agent that is asked "design a 2.4 GHz bandpass on this substrate and simulate it" with no display.

## What to build

### 1. The verb — `src/Cli/Plugin.cs`

One verb with nouns, on `cli.md`'s anatomy and `Authoring.cs`' terms: argument parsing, refusals and reporting only.

```
circuitRF plugin list [--json]
circuitRF plugin describe <id> [--json]
circuitRF plugin run <id> [--set name=value]… [--workspace <dir>] [--context <path>] -o <dir>
circuitRF plugin run <id> [--set …] --place <workspace> [--cell name] [--replace | --new-name]
circuitRF plugin allow <id> | deny <id>
circuitRF plugin install <folder | address> [--tag t] [--link] / uninstall <id> [--version v]   (after PLG-6)
```

- **`list`**: id, name, version, kind, source (list / address / folder / linked), permitted, and the reason for any
  dimmed plugin. Reads manifests only.
- **`describe`**: the manifest's parameters (kind, unit, default, range, options), views by name and kind, `run`
  per platform, and where it is installed. `--json` is the manifest normalised, plus those facts.
- **`run`**: `hello` (`mode: "headless"`), one `update` with the defaults overridden by `--set`, then:
  - with `-o`: each view written as files (`plugins.md` §11): a plot or Smith view as its data (`.npy`, or the
    `.sNp` it came from) plus a `.cdd` that `render --data` draws; a layout as `.clay` (+ the technology reference);
    a schematic as its `.cnl`; a table as `.csv`; an image copied; readouts as `readouts.json`; messages to stderr.
  - with `--place`: `commit`, then `PluginPlacement` — the function the panel calls. An existing cell is a refusal
    naming `--replace` / `--new-name` (the dialog would have asked).
- `--set` values are parsed as the panel parses them: an SI-prefixed quantity, an expression evaluated in
  `--workspace`'s scope, a `choice` option, a `substrate`/`technology`/`cell`/`result` by name or path. An unknown
  name or an out-of-range value is a refusal listing the valid ones.
- **Permission:** a plugin not yet allowed is a refusal naming `circuitRF plugin allow <id>`, which records it in the
  same store the prompt writes. `CRF_NO_PLUGINS` / the policy file beat it, and say so.
- **Exit codes:** 0 ok; 1 a refusal, a plugin `error` message, or a plugin failure (with its sentence and the last
  stderr lines); 130 cancelled. `window` plugins with no headless form are a refusal.

### 2. The MCP tool — `src/Cli/Serve/`

`plugin` with `action: list | describe | run | allow`, the same arguments and refusals, calling the same functions.
`install`/`uninstall` are **not** offered over MCP: installing runs someone else's code, and that is a decision for
the person at the keyboard. `allow` is offered only for a plugin already installed, and the tool's description says
it records the user's permission.

### 3. `explain` and `check`

- `check` on a plugin folder (`plugin.json` inferred as a kind by `DocumentKinds`) runs PLG-1's manifest validation
  and PLG-2's layout validation and exits on `--severity` as for any document — for plugin authors.
- `reference plugins` prints the manifest schema and message set from `docs/design/plugins.md`'s tables, for an agent
  writing a plugin.

## Gates

1. **Headless and panel agree** (series gate 3): `plugin run --place` and the panel's Place in workspace give
   byte-identical cells for `tools/plugin-examples/stub` at the same parameters.
2. **No logic in the verb:** a comment-stripped source scan of `Plugin.cs` finds no frame handling, no placement
   steps and no manifest parsing — only calls into `src/Design/Plugins`.
3. **Files from views:** `plugin run -o` on the stub writes a `.cdd` that `render --data` draws, and a `.clay` that
   `render` draws, both without a warning.
4. **Refusals:** not permitted, disabled by policy, unknown parameter, out-of-range value, existing cell without
   `--replace`, `window` without a headless form — each a test of its sentence and exit code.
5. **MCP:** the tool lists, describes and runs the stub; `install` is absent from its schema.
6. **`check`:** a manifest with a duplicate parameter and a layout naming an undeclared parameter reports both.

## Not in this brief

Installing over MCP (deliberately absent), and running a plugin in a remote job.

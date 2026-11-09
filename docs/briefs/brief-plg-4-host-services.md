# Brief PLG-4 — host services, and `window` plugins

Series: `brief-plg-0-overview.md`. Design: `docs/design/plugins.md` §7, §8. Depends on PLG-1 (PLG-2 for the panel's
busy bar during a host call).

**Why.** A synthesis plugin that predicts its design's response with its own approximations shows the user an
answer that disagrees with what circuitRF gives after placement — different line models, different dispersion,
different discontinuities. The same is true of a plugin that re-derives a substrate's εeff. circuitRF already has
the engine, the planar line models and the resolution of a workspace's technology; a plugin should be able to ask
for them. And some tools need more than the panel vocabulary and will draw their own window; they still need the
workspace context and placement.

## What to build

### 1. The `host` message

A plugin sends `host {id, call, …}` before its reply to request `id`; circuitRF answers `hostReply {id, call, …}` or
`hostReply {id, error}`. Several host calls may precede one reply, one at a time. A host call is cancelled with its
request (`cancel {id}` stops the analysis it started). Implemented in `src/Design/Plugins/PluginHostServices.cs`,
each call a thin wrapper over the capability that already exists, holding no logic of its own (the `Authoring.cs`
rule; a source scan holds it).

### 2. The calls

| `call` | Arguments | Calls | Returns |
|---|---|---|---|
| `simulate` | `cnl`: a staged `.cnl` path; `analysis`: the analysis line's name in it (or the only one) | The same run `circuitRF sparam`/`dc`/`hb` performs, in-process, through `CircuitSource` and the engine | `{result: <staged .npy>, touchstone: <staged .sNp, for sparam>, cubes: [...], messages: [...]}` |
| `resolve` | `what`: `technology` / `substrate` / `cell` / `reference`, and its name or path | `TechnologyResolver`, `SubstrateResolver`, `CellFolder.ResolvePrimary`, the reference walk `explain --ref` uses | The resolved object, as `explain --json` spells it |
| `evaluate` | `expr` | The expression engine, in the workspace's resolved scope | `{value, unit}` or `{error}` |
| `lineModel` | `type` (`microstrip`, `stripline`, `cpw`, `cpwg`), `substrate`, `width` (m), `gap` (m, CPW), `freq` (Hz, array) | circuitRF's own planar line models, the ones the MLIN family uses | `{z0: [...], eeff: [...], alphaDbPerM: [...]}` |

- **Results land in the staging folder**, never the workspace.
- `simulate` refuses EM analyses (`model=em`, a `.cem`) with "EM runs are placed and run by the user" — a plugin
  update must not start a run measured in minutes. Loadpull and pursuit are refused for the same reason; `sparam`,
  `dc` and `hb` are allowed.
- `simulate` honours the manifest's `update` deadline when called from an `update`, so a slow sweep is cut off and
  reported rather than stalling the panel; the plugin author moves it behind an `action`.
- The panel's busy bar says "circuitRF is simulating…" during a `simulate`.

### 3. `window` plugins

- `kind: window`: started with the same `hello` (workspace, context, staging), then **circuitRF draws nothing** and
  listens for `placed`, `host` and `open {path}` (opens a staged or workspace document in circuitRF) until the
  process exits.
- `id` on these messages is the plugin's own; circuitRF answers with the same `id`.
- Listed under Tools ▸ Plugins with a small "opens its own window" note in its tooltip.
- **One instance per plugin** at a time; choosing it again brings nothing forward (circuitRF cannot raise another
  process's window) and says it is already running.
- When circuitRF exits it sends `bye` to each, waits 2 s, then kills.
- No headless form unless the manifest also declares `params` and the program answers `commit` when started with
  `--headless` in `hello`'s `mode` field; brief 5 uses that if present and refuses with "has no headless form"
  otherwise.

## Gates

1. **`simulate` agrees with the CLI:** `tools/plugin-examples/stub` simulates its staged `.cnl`; the `.s2p` it gets
   back is byte-identical to `circuitRF sparam` on the same file, apart from the provenance line the CLI already
   excludes in its own gates.
2. **`lineModel` agrees with the part:** Z0 and εeff for a microstrip on a shipped technology equal what an MLIN on
   that substrate reports through `explain`, to round-off.
3. **No logic in the adapter:** the source scan finds no model equation, no resolver walk and no engine setup in
   `PluginHostServices.cs`.
4. **Refusals:** an EM analysis, a loadpull, a `.cnl` outside staging, an unknown call — each a `hostReply` error
   with a sentence, and the plugin session still alive.
5. **Cancel:** cancelling during a long `simulate` stops the engine run (the run's own cancellation token) and the
   session answers the next `update`.
6. **`window`:** `tools/plugin-examples/window` (a script that opens no real window, sends `placed` then exits) places
   its cell; a second start is refused while the first runs.

## Not in this brief

Host calls that change the workspace (place happens through `placed`/`commit` only), EM runs, and any call that
returns circuitRF internals rather than a document or a resolved value.

# Brief 2 — the gdsfactory cell adapter

**Series:** [gdsfactory kits](brief-gdf-0-overview.md) · **Tag:** `R-gdf2-n` · **Phase:** Python bridge, discovery, consent, palette
**Area:** new `tools/pcell-python/gdsfactory_bridge/` (`__init__.py`, `bridge.py`), `tools/pcell-python/verify.py`,
`src/Design/Layout/PCells/Wire/` (`KitPCellLibrary.cs`, `PCellGeneratorManifest.cs`, `PCellWorkerResolver.cs`,
`PCellTrust.cs`), new `src/Design/Layout/PCells/Wire/GdsfactoryKitDiscovery.cs`, `src/Ui/Views/Dialogs/PCellTrustDialog.axaml(.cs)`,
`src/Ui/Theming/AppPreferences.cs`, `src/Ui/Schematic/KitPaletteMerge.cs` (report only), the Import PDK
flow, `src/Cli/Authoring.cs`, new `docs/design/pcell-gdsfactory-bridge.md`
**Depends on:** — (brief 1's `import pdk` if it has landed; otherwise add the noun here) · **Blocks:** 3, 4, 5
**Rule:** overview §0. `gdsfactory` may be named. Kits may not.

---

## 0. The shape, and why it is small

circuitRF already runs a kit's Python cells. `KitPCellLibrary.EnsureDeclared` writes a
`pcell-generators.json` and a `kit_entry.py` into the workspace. The entry calls
`cni.bridge.register_kit("<package>")` and then `circuitrf_pcell.run()`.
`PCellWorkerResolver` starts it only after consent. A gdsfactory kit takes **the same road**:

```python
import circuitrf_pcell as crf
from gdsfactory_bridge import register_pdk

result = register_pdk("<module>")
for problem in result.problems:
    print(problem, file=__import__("sys").stderr)
crf.run()
```

There is no new process kind, no new wire message, no second consent mechanism and no second palette
path. `gdsfactory_bridge` is to a gdsfactory `Pdk` what `cni.bridge` is to a `cni` cell library.

## 1. `R-gdf2-1` — finding kits without running them

- **`R-gdf2-1a`** `GdsfactoryKitDiscovery` (in `src/Design`, framework-free) takes an interpreter
  path. It asks the interpreter for `sys.version_info` and its `site-packages` directories. That is
  one tiny `-I -c` script with no imports beyond `sys` and `sysconfig`.
- It then **reads files only**: every `*.dist-info/METADATA` whose `Requires-Dist` names `gdsfactory`.
  For each one it reports:
  - the distribution's name and version
  - its `Requires-Python`
  - its top-level module (`top_level.txt`, or `RECORD`)
  - the installed directory
- **No kit module is imported to list kits** (overview D2).
- **`R-gdf2-1b`** A kit **source tree** is also accepted: a directory holding a package whose
  `__init__.py` (head only, the `KitPCellLibrary` peek rule) imports `gdsfactory` and constructs
  `Pdk(`. Recognised structurally, like `KitPCellLibrary.ApiModule`.
- **`R-gdf2-1c`** Refusals name the fact:
  - an interpreter below the kit's `Requires-Python`, or below 3.12 (D5)
  - an environment holding no gdsfactory kit
  - **gdsfactory itself not installed**, even though a kit says it requires it. This happens when a
    kit was installed `--no-deps` and its dependencies never followed.

## 2. `R-gdf2-2` — consent, interpreter, reference

- **`R-gdf2-2a`** Consent is keyed on the kit's installed directory, resolved through symlinks, inside
  `PCellTrustStore`. There is no new store.
  - `PCellTrustDialog` gains rows for **package, version, location, interpreter**, shown only for a
    gdsfactory kit.
  - Dialog text: "Running this kit runs its Python code with your user account."
  - Nothing else about the dialog changes.
- **`R-gdf2-2b`** The workspace manifest records `Interpreter = null`. It records the kit as a
  module name plus the version seen, in a new optional `PCellGeneratorManifest.Module` /
  `ModuleVersion` (no `SchemaVersion` bump; absent on every existing manifest).
  - `PCellWorkerResolver` resolves the interpreter from a **per-user preference** keyed by module name
    (`AppPreferences`, beside the trust decisions). Overview D4.
  - With no preference recorded, the GUI asks once, offering interpreters found on `PATH` plus
    "Choose…".
  - Headless with no preference refuses with a sentence naming `--python`.
- **`R-gdf2-2c`** `KitRoot` is the kit's installed directory, so consent, the cache key and repair all
  anchor on it. `Sources = ["${kit}"]`.
  - The cache key also folds in the **gdsfactory version** the interpreter reports, because a
    gdsfactory upgrade can change geometry with the kit untouched.
  - Never hash `site-packages` (the `PythonPath` comment in `PCellGeneratorManifest` says why).
- **`R-gdf2-2d`** A recorded version different from the installed one gives one NOTE per kit per open,
  never a refusal.

## 3. `R-gdf2-3` — describe: cells and parameters

`register_pdk(module)` imports the module, finds its `Pdk` (`module.PDK`, else the only `Pdk` instance
at module level, else a problem naming the module) and **activates it**. Then, for each
`pdk.cells` entry:

- **Parameters come from `inspect.signature`, in declaration order.** Default kinds:

  | Default | Declared as | Notes |
  |---|---|---|
  | `bool` | `flag` | |
  | `int` | `integer` | |
  | `float` | **`real`, passed through exactly** | D3. Never `length`. The editor tooltip says "kit units (µm for dimensions)" |
  | `str` | `text` | |
  | `None`, sequence, callable, spec object | **not exposed** | the default always applies. Counted per cell in `problems` ("3 parameters not offered: cross_section, layers_no_fill, …") |
  | no default | the cell is **not offered** | the problem names the parameters. Measured: DOE packers, `import_gds`, transform helpers |

- **Layer-name text parameters** (a `str` default that names a layer in `pdk.layers`) are exposed as
  ordinary text parameters. Do not try to hide them by name. Instead, order them **after** every
  non-layer parameter, so the editor leads with what a designer changes. Measured: 300 of kit A's
  string parameters are layer names.
- **The declared model.** `model` is the `model` parameter's default if there is one, else
  `info["model"]` from a default build, else none. It is reported to circuitRF as the generator's model
  so `KitPaletteMerge`'s model step can pair it (D7).
- **Discovery is bounded.** Building every cell at its defaults to read `info` costs 0.4–7.6 s per kit
  (measured). Do it once, at `describe`, under the existing describe timeout. A cell that throws while
  being described is **listed with its exception's last line** and offered no further.

## 4. `R-gdf2-4` — generate: geometry and pins

- **`R-gdf2-4a`** Build with `gf.get_component(name, **params)`, where `params` holds only those
  differing from the default. This keeps gdsfactory's own cell cache effective.
- Flatten with `get_polygons` by `(layer, datatype)`. Polygons carrying holes go out as `Polygon` with
  holes.
- Scale µm → DBU **once**, with `crf.dbu()` on `x * tech.dbu_per_micron` (the README's rounding rule).
- **Never `round()` and never `int()`.**
- **`R-gdf2-4b`** Layers map by `(layer, datatype)` → `tech.layer_named`, or the stream key the
  technology carries.
  - A layer the technology lacks is **not dropped**. It is reported once per generate, by
    `(layer, datatype)` and by the kit's layer name.
  - The technology-import passing a null destination dropped every layer silently once already
    (`src/Design/RESOLVED.md`).
- **`R-gdf2-4c`** **Every port is a pin, whatever its `port_type`, except `placement`.**
  - Kit B types its CPW ports `optical` (finding 3). Port type is gdsfactory's routing vocabulary, not
    a statement about connectivity.
  - `placement` ports are alignment marks, and they go into a diagnostic count instead.
  - Pin layer is the port's layer. Width is the port's width. Outward angle is the port's orientation.
- **`R-gdf2-4d`** All-angle components (kit B's `*_all_angle`) fail **inside gdsfactory**. Report the
  exception and do not work around it.
- Kit D's fixed-GDS cells (finding 2) also fail on the kit's own missing files. The diagnostic quotes
  the path in the exception, so the user sees it is the kit's install, not circuitRF.

## 5. `R-gdf2-5` — the flows

- **`R-gdf2-5a`** **File ▸ Import PDK…** gains a second source: **"A Python package (gdsfactory)…"**.
  1. Pick an interpreter (§2b list).
  2. Pick from the kits §1 found.
  3. Consent (§2a).
  4. Import.

  "Choose folder…" still takes a kit tree, and §1b recognises a source tree inside it.
- **`R-gdf2-5b`** The import report states:
  - cells offered
  - cells not offered, with reasons, grouped by reason
  - parameters not offered (a count, with the list on expand)
  - pins dropped as `placement`
  - every model-step pairing made, **and every pairing refused, with the parameters that did not
    cover** (D7)
- **`R-gdf2-5c`** `circuitrf import pdk --module <m> --python <interp> --into <ws> [--allow-code]`.
  It uses the same function as the GUI. `--allow-code` is required headless (D2).
  `circuitrf explain --pdk <name>` reports the interpreter resolved, and whether it came from a
  preference or a flag.

## 6. Tests

**UI is rendered headlessly and inspected** (overview §6): the trust dialog's new package/version/location/interpreter rows, each step of the Import PDK Python-package source (interpreter list, kit list, consent), the per-user interpreter preference, a gdsfactory cell's palette tile and its parameter-editor rows with the "kit units" tooltip, and the import report's new sections. Render each with `tools/UiSnapshot`
(add a mode where none hosts it), look at the PNG, fix what it shows, and list the pictures in the completion
note.


Run only the classes you touch. Tier B is never in the default gate.

**Tier A (routine, no Python packages):**

1. Discovery from **synthetic** `site-packages` trees (made-up distribution names): it finds
   `Requires-Dist: gdsfactory` kits, ignores others, reads `Requires-Python`, and starts **no**
   interpreter except the version/paths probe (a counting seam).
2. A missing gdsfactory, a low Python and an empty environment each refuse with their own sentence.
3. Consent keyed on the resolved directory: a symlinked path and its target are one decision, and two
   environments are two.
4. The manifest round-trips `Module`/`ModuleVersion`. Existing manifests are byte-identical after
   load/save.
5. The interpreter comes from the per-user preference. None recorded headless is a refusal naming
   `--python`.
6. A float parameter declared `real` reaches the generate request unchanged, including
   `3.3303e-11` (finding 4).

**Tier B (`[GdsfactoryFact]`, real gdsfactory, synthetic kit):**

`tests/Ui.Tests/Gdsfactory/Fixtures/synthkit/` is a made-up package with these cells:

- a rectangle, a polygon with a hole, and a cell with references rotated 90° and mirrored
- one port of each type, `placement` included
- one parameter of every default kind, a layer-name text parameter, and a declared `model`
- a cell that raises, a cell with a required argument, and a cell using a layer the technology lacks

For each, run it through circuitRF's worker and export with `GdsiiExport`. Then:

7. Per-layer polygon sets equal gdsfactory's own `write_gds` of the same cell at the same parameters.
8. Pins equal ports, with `placement` excluded and counted.
9. Non-scalar parameters are not offered and are counted. The required-argument cell is not offered.
   The raising cell is listed with its message.
10. The missing layer is reported, not dropped silently.
11. **Kit E, every cell at its defaults:** the same polygon-set comparison. A cell gdsfactory itself
    cannot build is excluded, and the count is printed.
12. `register_pdk` on a module with no `Pdk` is a problem naming the module.

`tools/pcell-python/verify.py` gains a self-contained check that `gdsfactory_bridge` imports
**without** gdsfactory installed. It must fail with the sentence §1c uses, never a traceback.

## 7. Scope

- No technology from the stack (brief 3).
- No symbols (brief 4).
- No parameter map between a kit symbol and a gdsfactory cell (D7).
- No photonics-kit acceptance (D8).
- Never install anything (D5).

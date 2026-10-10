# circuitRF — the command-line interface (`src/Cli`)

**Status:** current · **Covers:** `src/Cli` (built as `src/Cli.Verbs` + the one-line `src/Cli/Program.cs`), and the installed `circuitRF` executable's dispatch (§20) · **Related:** `ui-architecture.md`,
`loadpull.md`, `loadpull_pursuit.md`, `harmonic-balance.md`, `mom-engine.md`

## 1. What it is, and the one constraint that shapes it

`circuitRF.Cli` is the **headless driver**: it reads a `.cnl`, elaborates it, runs one analysis, and
reports. It is simultaneously the project's own **test harness** — a `.cnl` that works headless is a
`.cnl` that works when opened, because the CLI evaluates the TestBench's `measure` lines through the
same `MeasurementEvaluator` the GUI uses rather than re-deriving them.

The constraint that decides everything below is the **UI firewall** (`ui-architecture.md`):

```
src/Cli  ──►  src/Core  ──►  (expressions, design model, elaboration)
   └────►  src/Engine ──►  src/RfCore
                    NO Avalonia anywhere on this path
```

`tests/Firewall.Tests` fails the build if that is violated. So the CLI can drive **anything whose
engine lives in `src/Engine` or `src/RfCore`**, and nothing whose driver lives in `src/Ui`.

`src/Design` joined that path in 2026-08: it holds the design-layer artifacts an EM problem is built
from — the layout model, the technology model, the cell-folder format, the `.cem` and the extractors —
and it is gated by the same firewall test. That is what the `em` verb (§8) runs on.

**There are two front doors to one set of verbs** (§20, AUT-13). `src/Cli`'s sources compile into
the library `CircuitRF.Cli.Verbs`; `src/Cli/CircuitRF.Cli.csproj` is `Program.cs` alone —
`CliEntry.Run(args)` — and is what `dotnet run --project src/Cli`, every test and every script
launches. The INSTALLED `circuitRF` executable is the other door: `src/Ui` references the library and
its `Program.Main` hands a verb to the same `CliEntry.Run` before any of the GUI starts. The arrow
into `src/Ui` does not cross the firewall the wrong way — the wall forbids the CLI referencing a UI
framework, not the GUI referencing the CLI — and `CircuitRF.Cli.Verbs` has its own firewall row.

## 2. The verbs

| Verb | Input | Runs | Writes |
|---|---|---|---|
| `sparam` | `.cnl` or `.csch` | `SParameterEngine` | Touchstone `.sNp` by default; `-o`'s extension picks the format (`.sNp`, or `.npy`/`.mat`/`.txt` for the cubes). With a `WSProbe` in the netlist it also prints one line per probe (`WSProbe GATE idx=1 H0(f_lo)=… ZG(f_lo)=… SM_Y0 min −18.1 dB @ 1.5913 GHz SM_H0 min −19.8 dB @ 1.7337 GHz` — each stability margin's minimum over the sweep and its frequency, in dB), evaluates the bench's `measure` lines, carries `wsprobes: [{label, idx, smY0Min, smY0MinHz, smH0Min, smH0MinHz}]` in `--json` (**linear**, because dB is a display convention and a document carries the number), reports a probe whose margin falls below the analysis line's `MarginThreshold=` (default −15 dB, `MarginThreshold=none` disables) as an Info diagnostic, and — a probe with no port being legal — refuses a Touchstone of a run that has no `S`, naming the cube spellings (`docs/design/stability-wsprobe.md` §3, §9) |
| `dc` | `.cnl` or `.csch` | `NonlinearDcEngine` | node voltages + probe currents + the bench's `measure` lines (named by the declared DC analysis, `DC` when there is none) to stdout; `--json` carries `WireTemp` when the netlist has a wBond |
| `hb` | `.cnl` or `.csch` | `HbEngine` (single- or multi-tone) | stdout tables; `-o .mat/.npy/.txt` — with `WireTemp`/`WireTempState` when the netlist has a wBond |
| `lp` | `.cnl` or `.csch` | `LoadpullEngine` + `LoadpullPostProcessor` | stdout grid table; `-o .mat/.npy/.txt/.spl/.lpcwave` (`WireTemp [grid, pin, wire array]` with a wBond) |
| `lpp` | `.cnl` or `.csch` | `LoadpullPursuitEngine` | stdout optima + follow-on grid; `-o` as `hb` (the follow-on carries `WireTemp`); `--out-grid` writes the `.gam` |
| `em` | `.cem` | `EmSetupResolver` + `EmRunService` (kernel chosen by `EmKernelRegistry`) | Touchstone `.sNp` + grouped `.npy` at the path Simulate writes; `-o` moves the Touchstone |
| `rail` | `.crail` (or a `.clay` / `.csch` / cell folder with one beside it) | `RailOrder` + `RailDcRun` — the extractor, the solve and the via check `src/Design/RailRf` already holds | stdout tables; `-o .csv/.npy/.mat/.txt` for the numbers and `.svg/.pdf` for the report page — §17 |
| `smith` | `.csmith` | `SmithCascade` + `SmithReadings` + `SmithBand` — the evaluator the Smith Chart window's status strip reads on every edit | stdout reading + the per-node walk; `-o .s1p` for the load Γ and `.svg/.pdf/.png` for the chart — §18 |
| `opt` | `.cnl` or `.csch` | `OptimizationRun` — the Optimizer window's run — over the `tune`, `goal` and `optimize` lines | stdout result tables; `-o .npy` the best point's full results plus the `opt` history group; `--history .npy`; `--save-preset` the one write to the design — §24 |
| `yield` | `.cnl` or `.csch` | `StatisticalRun` — the Yield panel's run — over the tolerances, the yield-spec goals and the `statistics` line; nouns `mc`, `estimate`, `trial` | stdout yield, goals, statistics and worst-trial tables; writes `<design>.yield.npy` (`-o` moves it); `--save-preset`/`--save-corner` with `--trial` the two writes to the design — §25 |
| `elab` | `.cnl` or `.csch` | elaboration only | the elaborated netlist, for development |

**A run verb takes a SCHEMATIC as well as a netlist, and extracts it in memory** (§14). Any other
document kind is a refusal naming what the path holds — `cli.input.wrong-kind`. It used to be handed
to `CnlReader`, which parsed the JSON as netlist text and reported its first key as a missing cell
name.

Fourteen verbs run no analysis, so none of §3-§6 applies to them and §7's exit codes reduce to 0-or-1:

| Verb | Input | Does | Writes |
|---|---|---|---|
| `convert` | any interchange format; a STEP file; a `.c3d` or a cell folder (to STEP); a `.c3d` (to glTF) | one import, one export | the target format; documented in the repo-root `CLAUDE.md`. **A `clay` target is a DIRECTORY**; a STEP source's one target is a new `.c3d`, and a STEP target takes a `.c3d`, a `.clay`, a cell folder or any interchange source; a `.glb` target takes a `.c3d` only, and glTF is never a source — see below |
| `new workspace` | a directory | `WorkspaceCreate.Create` | a `.cws` and, unless `--tech none`, a copied `.ctech` |
| `new cell` | a workspace + a name | `CellCreate.Create` | a cell folder and one empty-but-valid file per `--views` |
| `import part` | a component file or folder | `ComponentRead` + `ComponentImport.Import` | a cell folder holding the land patterns and the symbol |
| `check` | a workspace, a cell folder, or one document | the validators that already exist | **nothing** — §10 |
| `explain` | the same, plus `--expr` / `--analysis` / `--ref` / `--cells` / `--layers` / `--extents` / `--footprints` / `--tunables`, and `--setup` and `--object` (one object's resolved appearance) for a `.c3d` | reports what resolution DECIDED | **nothing** — §10 |
| `render` | the same three view documents, a cell folder, a workspace + `--cell`, a `.cdd`, a 3D `.cem`, or a `.c3d` (and its field plots) | draws it with the renderer the GUI draws with | one `.svg` / `.pdf` / `.png` — §13, §13.7 for a data display, §13.8 for a 3D setup, §13.8.1 for a field plot |
| `read` | a result file, or one of circuitRF's own documents | loads it back through the readers the GUI reads through | **nothing** — §11.4 |
| `netlist` | a `.csch`, a cell folder, or a workspace + `--cell` — or a `.cnl` + `--to-schematic` | the extraction the GUI's own Simulate performs, or `NetlistSchematic.Build` | one `.cnl`, or the text on stdout; a drawn `.csch` — §14, §14.2 |
| `plot` | a result file | builds a one-plot data display and draws it | one `.svg` / `.pdf` / `.png`, and the `.cdd` under `--write-cdd` — §15 |
| `find` | a directory | enumerates the workspaces, cells, views and analyses under it | **nothing** — §16 |
| `lvs` | a cell folder, a workspace, a `.clay` or a `.csch` | compares the artwork against the drawing, through `LvsRun.Run` | **nothing** unless `-o` names a report — §19 |
| `recognize` | a `.clay`, a cell folder, or a workspace + `--cell` | reads the artwork as a circuit, through `ArtworkRecognition` | **nothing** unless `-o` (a `.cnl`), `--into` (a schematic) or `--parts-out` (the parts table) — §26 |
| `serve` | `--root <dir>` | a protocol server on stdin/stdout — §11 | whatever the tool it was asked for writes |

**`convert`'s `clay` target is a directory, and a file-shaped path there is a refusal** (R-aut12-4).
An import writes one cell FOLDER per structure plus the technology beside them — which is why `--to
clay` simply stops after the import — so `-o out/board.clay` used to produce a *directory* called
`out/board.clay` with the real `.clay` two levels inside it. Nothing was lost, because the result
document reported the true paths, but a path that names a file and yields a directory of that name is
a surprise nobody is there to notice on a build machine. The `.clay` extension is also how a caller
SAYS clay, so the inference stays and the SHAPE is what is refused, with the directory spelling in the
message. Collapsing to a single file was the alternative and is worse: an import that produced a
hierarchy has no one file to collapse to, so the rule would work for the flat case and discard the
other silently.

**`convert --no-coalesce` keeps a painted pour's individual strokes** (R-rf3-7). A CAM tool may
express a copper pour by PAINTING it with thousands of abutting one-mil scanline strokes rather than
emitting a filled region; the import turns those back into the region they paint, **by default, both here and in the
GUI** — because the region is the shape the renderer, the
mesher, the DRC engine and every writer want, and a layer that arrived as 29,000 strokes is neither
editable copper nor meshable. The flag exists for the one case the default cannot serve: comparing an
import against its CAM source, or chasing an import bug, where the primitives have to arrive exactly
as authored. Either way the import SAYS what it did, per layer, with both counts and the flattening
tolerance — on stderr with the rest of the import's notes, never on stdout, which stays the result
document. The GUI's own switch is Settings ▸ General ▸ Import; detail and the two counted conditions
that decide it are in `src/Design/RESOLVED.md`.

**A label reaching a Gerber target is written by its FONT** (brief-silkscreen-stroke-font.md R-ssf-6). A
stroke-font label — every label's default — becomes D01 strokes through one round aperture of its pen width,
from `StrokeText`, the geometry the canvas draws; it is data in `src/Design`, so it is the same geometry in
every process. A **Sans** label is filled TrueType glyph outlines, as before, and only those depend on the
face a process could load: the "platform default typeface" note is said **only when a Sans label was
converted** in a process without the embedded faces. stderr counts the two separately, and counts any
character the stroke font lacks (drawn as a hollow box) — none is lost without a word.

**`convert part.step -o <cell>/3d/<name>.c3d` makes a NEW 3D view from a STEP file** (brief-em3d-68 R-em3d68-7).
`step` is a SOURCE with exactly one legal target, a `.c3d` that does not exist yet; importing into an existing one
is writing its `Step` objects (the format is the contract — `reference topic=c3d` describes every field), so an
existing target is a refusal saying so (a `.step` TARGET is the export, below). The
verb is argument parsing and reporting around `StepImport.Import` — the function the Import STEP dialog calls — and
a comment-stripped scan of `src/Cli` holds that. The dialog's defaults are the verb's: materials by part name,
then by exact colour, else none (a note lists the unmapped parts); a part that is not a closed solid is skipped
and named. What the dialog would ASK is a flag: `--material <part>=<name>` (repeatable; a part is its occurrence
path, product name or object name), `--part <path>` (repeatable; import only these) and `--tech <path>` (the new
document's `TechRef`; otherwise the workspace's default by the usual walk-up). **A product of several solids is one
`Step` object per solid, all gathered in one group named after the file** (brief-em3d-128): `--part <path>#<k>`
selects one solid (a CLI spelling only; the document writes `Part` and `Solid`), `--group <name>` names the group
and `--group ""` makes none — `convert` owns `--group`, which elsewhere narrows a result's cube groups, as `smith`
owns `--at` — and `--list-parts` prints the table, one line per row with its `--part` spelling, colour, match and
name, writing nothing. The JSON result's `stepImport` lists each object with its part, solid, name, material, match
and group. A file's length unit the reader
cannot resolve is a refusal naming it, never a guess — Excellon's rule. A `.step`/`.stp` is recognised by
extension, and anything else by its first line (ISO 10303-21's header) through `DetectSource`, the one classifier
`check` also names a foreign file with. Without the geometry kernel it refuses with the capability's own sentence.

**`convert <x.c3d | x.clay | cell | board…> -o out.step` writes the ELABORATED model as one STEP file**
(brief-em3d-69 R-em3d69-5, D9) — the solids the solver gets, named by their instance paths and coloured by their
materials, in the document's display unit mapped to millimetres (nm, µm, mm) or inches (mil, inch). The verb is argument
parsing and reporting around `StepExport.Export`, the function File ▸ Export ▸ STEP… calls; a comment-stripped scan of
`src/Cli` finds no call into the worker and no STEP text, and the process's file matches the in-process call's byte for
byte but for `FILE_NAME`'s time-stamp. Legal sources: a `.c3d`, a `.clay`, a cell folder (its 3D view, else its layout;
a cell with both is a refusal listing the two and naming `--view 3d|layout`, `render`'s rule) and every interchange
source, imported into a scratch cell first as for every other target — so `convert board.kicad_pcb -o board.step` is
one line. The flags mirror the dialog: `--assembly` (each placed cell a sub-assembly, written once however often it is
placed), `--as-drawn` (every solid whole; the default applies precedence, so the file holds disjoint solids),
`--thicken-sheets`, `--include-airbox` and `--schema ap214|ap242` (AP214 by default). Any of them with another target is
a refusal. Refusals name their remedy: a `.clay` whose stackup cannot place a drawn layer is `Em3dLayoutSolids.From`'s
own sentence, verbatim; a `.clay` with no resolvable technology names `--tech` (never an empty technology — with no
stackup nothing has a height); a model with nothing in it says *nothing to export*; an absent kernel is the
capability's sentence. stdout is the written path, and `--json` records it as an output of kind `step`. A cancelled
export exits 130 and writes nothing: the worker's bytes go to a temporary file renamed into place.

**`convert x.c3d -o x.glb` writes what the 3D view draws as binary glTF** (brief-em3d-111). The verb is
`GltfConvert`: argument checks, the view state headlessly and the report around `GltfExport.Build` (in `src/Render`,
because the scene and its shading normals live there), the function File ▸ Export ▸ glTF… calls. The scene is the one a
freshly opened editor draws — `C3dProblemAssembly.ViewProblem` of the elaboration's solids and sheets, the document's
origin, theme (the workspace's recorded scheme, light) and appearances — and what it shows is the document's: its
`Hidden` objects are left out, and there is no hidden-by-session state headlessly. `--gltf-assembly` and
`--gltf-field <plot>` mirror the dialog; the field is resolved by `render --field`'s own resolution
(`RenderEm3d.Request.FieldSink`), so a plot `render --look realistic` can draw is one `convert` can write, at phase 0,
and a ClipPlane plot is refused (`convert.gltf.field-clip-plane`). The camera is the Look's `Camera` when the document
saves one (framed at `render`'s default 4:3), and none otherwise. **glTF is never a source**: a `.glb`/`.gltf` input is
`convert.gltf.import`, a `.gltf` target is `convert.gltf.binary-only` (D1), and any other source kind is
`convert.gltf.source`. The file matches the dialog's byte for byte with no camera (`GltfExportTests` gate 7); stdout is
the written path, `--json` records it as an output of kind `glb`, and the write goes through a temporary file renamed
into place.

**`oasis` is a sixth stream-and-artwork format, and `--engine native|gdstk` picks a GDSII end's reader or writer**
(brief-oasis-gdstk.md §7d, §10a). Both go through `StreamInterchange.Import`/`Write` in `src/Design`, the two calls the
GUI's File ▸ Import / Export entries make, so `src/Cli` names a `StreamRoute` and branches on nothing else. OASIS is
read and written by the gdstk worker only, so the route of an `oasis` end is fixed; `--engine` applies to a `gdsii`
end (default `native`, D5), and with no `gdsii` end it is refused (`convert.engine.not-gdsii`) rather than ignored. An
`.oas`/`.oasis` path is OASIS, and a file with no telling extension is OASIS when it begins with the
`%SEMI-OASIS\r\n` signature (`StreamInterchange.LooksLikeOasis`, checked in `DetectSource` before the Gerber
classifier). The Export OASIS dialog's options are flags at the dialog's DEFAULTS, never the GUI user's remembered
preference: `--oas-compression 0-9`, `--oas-validation none|crc32|checksum32`, `--oas-standard-properties`; with no
OASIS target they are refused (`convert.oasis.flags-not-oasis`). The dialog's fourth option, shape detection, has no
flag and stays on. Without the worker an OASIS end is refused before anything is read (`convert.oasis.unavailable`, D6).
**The byte gate has no exclusion**: gdstk's OASIS writer writes no timestamp (G0's Q8), so `convert`'s `.oas` equals
`StreamInterchange.Write`'s byte for byte, with the default options and with all three flags set
(`ConvertCliVerbTests.ConvertingAClayToOasis_WritesWhatTheApplicationsOwnExportWrites`). The all-pairs matrix is 35
pairs over six formats — 24 that need no worker, 11 with an `oasis` end under `GdstkTheory` — plus the `--engine gdstk`
rows over every pair with a `gdsii` end.
| `reference` | **nothing at all** | reports what a caller may WRITE: the shipped reference pages, plus four topics generated from the live registries and readers — the component catalogue, the analysis directives, and the `.cdd` and `.ctech` formats | **nothing** — §12 |

**`new` is one verb with a noun, not three** (`brief-automation-3-authoring-verbs.md` R-aut3-13): the
surface has a standing cost, and adding `new schematic` later is a noun rather than a fourth
top-level verb. The three authoring verbs share one rule that decides every default they have —
**whatever the GUI's dialog pre-selects, the verb selects with no flag, and anything the dialog would
have ASKED is a refusal that names the flag answering it.** So `--tech` defaults to the New Workspace
dialog's own pre-selected technology and an unknown id lists the real ones rather than falling back;
`--views` defaults to `schematic`, which is what the GUI's New Cell creates (`3d` is accepted too, and
writes through the same `CellCreate` call the tree's New ▸ 3D View makes); and a source folder
holding several parts is refused with `--cell` / `--variant` / `--list-parts` named, never
resolved by taking the first. They add no import or creation logic of their own: each calls the same
function the GUI's own command calls, which is what
`tests/Ui.Tests/AuthoringCliVerbTests` gates, byte for byte and by scanning the view model's source.

The paths created ARE the result and go to stdout, because a caller's next step is almost always to
read or rewrite one of them — the documents are the interface (`automation-architecture.md` §4), and
there are deliberately no per-primitive edit verbs.

Some flags are pulled out of the argument list before dispatch, so **every** verb takes them and no
verb's own argument loop has to learn about any of them:

| Flag | What it does |
|---|---|
| `--kits <dir>` | makes an externally-supplied device model resolve headlessly, the way opening a workspace does in the GUI. Repeatable. |
| `--trust-kit <dir>` | lets THIS run execute the PCell scripts of the kit whose generator manifest is in `<dir>`, to rebuild the generated cells a layout places — §23. Repeatable. |
| `--json` | one JSON document on stdout and nothing else — §3.2 |
| `--only`, `--group` | narrow that document's `result` by cube and group name — §3.2 |
| `--at`, `--range`, `--interp`, `--result` | narrow it by AXIS, or ask for the shape alone — §3.2 |
| `--summary` | report the informational notes as counts rather than in full — §3.2b |

## 3. The anatomy of a run verb

`hb`, `lp` and `lpp` are the same five steps. A new run verb should be the same five steps.

1. **Read** — `CnlReader.ReadFile` → `(Library, TestBench)`.
2. **Override globals** — each `--set name=expr` REPLACES the variable in `tb.GlobalVariables`, so it
   joins the netlist's own scope and everything derived from it re-derives. An override pushed at the
   engine instead would move one number and leave every expression computed from it stale.
3. **Elaborate** — `new Elaborator(lib).Elaborate(tb)`.
4. **Select the chain** — `SelectTop`, shared by all three (§4).
5. **Dispatch, report, export** — run; print warnings again *after* the run (the engine adds its own
   while assembling and solving, long after elaboration finished); evaluate measurements; print;
   export.

### 3.1 Two channels, and they are not interchangeable

**stdout is the result. stderr is everything else** — progress, per-grid-point and per-query
engine chatter, `[circuitRF]` notes, elaboration and engine warnings, device-worker logs. The split
is what makes `circuitrf lp x.cnl > table.txt` produce a table and still show progress, and it is
why the engines' own `Console.Error` progress lines need no CLI plumbing at all.

**`serve` is exempt, and only `serve`** (R-aut5-2). Its stdout carries the protocol framing, so
nothing else may be written there — ever. The stderr half is unchanged: every engine progress line,
`[circuitRF]` note and worker log still goes there, which is where a client's own logging picks it
up. The guarantee is structural rather than a rule each printer has to remember — `Console.Out` is
replaced with a sink before a single capability runs, and each verb's document is written to a
string (§11.2) — and it is audited rather than assumed, by exercising every exposed capability with
stdout captured and asserting that every byte of it is a protocol frame.

### 3.2 `--json`: the third channel rule, not a per-verb feature

`--json` is available on **every** verb, spelled that way everywhere — not `--format json`, not a
per-verb variant. It changes one thing: **stdout carries a single JSON document and nothing else.**
stderr is untouched, so progress, `[circuitRF]` notes, warnings and refusal sentences all stream
exactly as they did, and a script watching stderr cannot tell whether the flag was passed.

That guarantee is structural rather than a rule each printer has to remember: the moment the flag is
parsed, `Console.Out` is replaced with a null writer and the real stdout is held until the document
is written to it (`src/Cli/JsonRun.cs`). A verb prints its table exactly as before, into a sink.

| | Without `--json` | With `--json` |
|---|---|---|
| stdout | the human table | one JSON document |
| stderr | unchanged | unchanged |
| exit code | §7's per-verb rules | **the same**, and echoed in the document |

**A failed run still emits a document.** The failure is the payload, so a caller never has to tell
"no output" apart from "output I could not parse". `status` is `ok` / `not-converged` / `failed` and
always agrees with `exitCode`, which still follows §7 — including the deliberate difference between
`hb`'s convergence test and `lp`'s.

The shape is one schema across every verb (`RfCore.Export.ResultDocument`):

```
{ "circuitrf": {"version","verb"}, "input": {"path","analysis"},
  "status", "exitCode",
  "outputs":     [ {"kind","path"}, … ],      // every file written — for em, BOTH the .sNp and the .npy
  "diagnostics": [ {"id","severity","message","arguments"}, … ],
  "diagnosticSummary": {"info","warning","error","omitted","full"},   // --summary only, §3.2b
  "result":      { "summary":  …,
                   "shape":    { "groups": { "<group>": { "<cube>": {"kind","unit","elements","axes"} } } },
                   "narrowed": [ {"axis","unit","mode","asked","at","from","to","length","clamped","cubes"}, … ],
                   "groups":   { "<group>": { "<cube>": {"kind","unit","axes","values"} } } } }
```

(`result` also carries `check`, `explain` and `document` for the three verbs that produce one of
those instead of cubes — §10.5 and §11.4.)

- **`input.analysis` is the chain that ACTUALLY ran**, after §4's promotion — not what was requested.
- **`result.groups` mirrors the `DataSet`**; a cube's `kind` decides whether `values` holds numbers
  or `[re, im]` pairs. Numbers are raw, invariant and unrounded: the dB/percent presentation and the
  column widths are terminal concerns and none of them is encoded in the document. NaN and infinity
  are written as JSON's named literals (`"NaN"`), because a loadpull grid genuinely contains NaN
  wherever a point never converged and substituting a zero would turn "no measurement" into one.
- **Every cube carries a `unit`**, and so does every entry in `shape` — see §3.2a.
- **`result.summary`** is `lp`/`lpp`'s one-row-per-grid-point projection — the same one §6.3 prints,
  from the same code (`RfCore.Loadpull.LoadpullResultSummary`), so the table and the document cannot
  disagree. For those two verbs it is the DEFAULT and the cubes are omitted; `--all` adds them.
- **`result.shape` is present on EVERY run and every `read` that produced a `DataSet`**, whether or
  not the values are inline (AUT-9 R-aut9-10). `run sparam` returned its whole result inline while
  `run lpp` returned a written path and nothing else, with nothing in either tool's schema to say
  which a caller would get; the payload rule is still per-verb, but a caller always learns what the
  run produced and can then decide what to ask for. It is O(cubes) rather than O(numbers) — a
  four-port 551-point run's shape is under 2 KB — which is what lets it be unconditional.
- **`--only <cube>,…` and `--group <name>,…`** narrow `result` and nothing else. An unknown name is
  skipped silently, matching `DataSetSubset.SelectGroups`.

#### `--at`, `--range`, `--interp`, `--result` — narrowing by AXIS

`--only` and `--group` narrow by cube NAME, which does nothing at all when the result has one cube.
A 551-point two-port S-parameter run is **173 KB inline** to answer what one entry is at one
frequency. So (AUT-9 R-aut9-9):

| Flag | What it does |
|---|---|
| `--at <axis>=<value>` | one point of an axis, e.g. `--at freq=2GHz`. Nearest grid point. |
| `--interp` | makes every `--at` interpolate between the bracketing points instead |
| `--range <axis>=<lo>:<hi>` | a band of an axis, e.g. `--range freq=1GHz:3GHz` |
| `--result full\|summary` | `summary` returns the shape, units and extents and **no values** |

- **Values carry their own unit**, because a bare `2` could be 2 Hz or 2 GHz and this repo already
  has the run that went out at 2 Hz because a scale was read without its mark. The axis's own unit is
  stripped first and the SI prefix second, so `5mm` is five millimetres on a metre axis and `5m` is
  five metres on the same one. A bare number is taken as already being in the axis's base unit.
- **The axis is located by NAME**, never by position: a sweep prepends one axis per nesting level.
- **A cube that does not HAVE the named axis is left whole** — a `Z0` cube has no frequency axis and
  narrowing by frequency is not a claim about it. An axis **no** cube has is a refusal listing the
  ones that exist (`narrow.axis.unknown`), and it fails the invocation: any file the run wrote is
  still in `outputs` and the shape still comes back, but the caller must not receive the whole
  un-narrowed result under the impression that it answers the question asked.
- **`result.narrowed` says what happened**, per axis: the value asked for, the value returned, and
  whether it was `nearest` or `interpolated`. `--at` keeps the axis at length 1 rather than
  collapsing it, so the point returned is visible in the data as well.
- **`--result`, not `--format`** — `render --format pdf` already exists and means the picture's file
  format. AUT-9 suggested the name `format`; one word meaning two things across two verbs is what a
  caller gets wrong once and forever.

#### 3.2a Every cube says what its numbers are in

The axes have carried a unit since they were written; the values did not. One loadpull-pursuit
result carried `Efficiency` reading 65.84 and `MXE_Eff` reading 0.7087 at the same operating point —
the cube in percent, the scalar as a fraction, with nothing anywhere saying which. Both "format
`MXE_Eff` as a percentage" (0.7%) and "multiply `Efficiency` by 100" (6584%) are one plausible line
of code.

So **`unit` is always present on a cube, and never empty** (AUT-9 R-aut9-3):

- SI symbols as they are written — `Hz`, `V`, `A`, `W`, `Ohm`, `F`, `H`, `S`, `K`, `m`, `s` — and the
  logarithmic units as they are written, `dB` and `dBm`.
- **`%`** for a ratio already scaled to a percentage, **`1`** for one that is not, **`index`** for a
  flag or a count, and **`unknown`** where circuitRF genuinely cannot say — a designer's own
  `measure` expression, most often. `unknown` is an answer; an empty string is not.
- A cube's producer states it (`DataCube.Unit`) where the NAME cannot: `LoadpullPostProcessor.Enrich`
  scales `PAE` from a fraction to a percentage **under its own name**, so two cubes called `PAE` mean
  different things. Where the name IS the answer, `RfCore.Export.ResultUnits`' vocabulary supplies
  it — annotating every cube in the engine would be a large change for a value already determined.
  A stated unit survives the `.npy` and `.mat` round trip.
- **Nothing is rescaled.** The loadpull summary's `columns` and the pursuit's optima each carry the
  unit of the RAW value alongside `consoleScale` and `consoleUnit` — MXE's value is a fraction the
  terminal prints as a percentage, and those used to be one field naming the terminal's unit.

#### 3.2b `--summary`: the notes as counts

`--summary` reports the **`info`** diagnostics as counts by severity instead of in full, and adds
`diagnosticSummary` saying how many were omitted and what returns them. **Warnings and errors are
never collapsed** — a caller that asked for less text did not ask to be told less about what went
wrong — and stderr is untouched either way, so the full account is still on the terminal.

It exists for the Gerber-shaped case (AUT-9 R-aut9-11): those diagnostics are the best-written text
on the whole surface and are not weakened by this; they are simply not the right default payload for
a listing call whose answer is one cell name, which measured **30 KB** over the wire.

**A diagnostic is not emitted twice** (R-aut9-8). A large family here is templated `"{text}"` — the
whole sentence is one substituted value forwarded from a reader or an elaborator — so `message` and
`arguments.text` came out byte-identical on most of them; one Gerber import was ~15 KB of exact
duplication in one response. `arguments.text` is now emitted only when it DIFFERS from `message`.
The rule is by that one NAME rather than "any argument equal to the message", because
`convert.cell.listed` is templated `"{cell}"` and its argument is the ANSWER the caller asked for.

**§7A applies inside the document.** Every diagnostic carries `message` — always
`Diagnostic.Render()`, always English, always culture-invariant — alongside its stable dotted `id`
and its typed `arguments`. **The id is the contract; the message is not.** A caller matching on the
sentence is doing the thing the id exists to make unnecessary, and templates are reworded freely.

### 3.3 An unrecognised option is a refusal, on every verb

`convert`, `new`, `import`, `check`, `explain` and `read` have refused an unknown option since they
were written. The five older run verbs — `sparam`, `dc`, `hb`, `lp`/`lpp`, `em` — dropped one in
silence until 2026-09-05, and **the silence was not the whole cost**: four of the five find their
input by *"the first token that does not start with a dash"*, so the dropped flag's **value** became
the input path. `circuitrf lp x.cnl --maxmix 3` answered `File not found: 3` — a refusal naming
neither the real problem nor the file the caller gave. `dc`, which reads its path positionally,
simply ran without the override and said nothing, which is a run answering a different question than
the one asked.

All five now emit `cli.args.unknown-option` and exit 1. This is the same principle §6.1 already
applies to `--grid` on `lpp`, reaching the case where the option belongs to no verb at all.

**It is also what makes `serve`'s tool catalog gateable.** `ToolCatalog` names a CLI flag for every
argument it advertises, and an argument naming a flag the verb does not read is now a loud refusal on
the first call rather than a swallowed path — which is how
`ServeProtocolAdapterTests.EveryAdvertisedArgument_IsAFlagTheVerbActuallyReads` can be a behavioural
gate rather than a source scan. It found three: `sparam` was advertising `-a` and `--set`, `dc` was
advertising `--set`, `--tol`, `--maxharm` and `--maxmix`, and both loadpull modes were advertising
`--maxmix`.

## 4. Chain selection: dispatch at the SWEEP, never at the inner analysis

`SelectTop(tb, requested, isBase, kindLabel, directiveHint, out why)` picks what runs. The rule it
exists to enforce:

> A `parametric_sweep` wrapping the analysis must be dispatched **at the sweep**. Naming the inner
> analysis runs one point and silently loses the sweep axis.

That failure produces a converged, plausible, complete-looking result for a run the user thinks
swept, so `-a <inner-name>` is **promoted** to its outermost enabled wrapper (with a note on stderr)
rather than being honoured literally. This is not HB-specific — a frequency-swept loadpull is exactly
this shape — which is why one function serves every verb and takes the base-analysis test as an
argument.

Ambiguity is reported, never guessed at silently: more than one runnable chain prints all their names
and runs the first; zero prints whether the netlist declares none or declares one that is disabled.

## 5. Overrides land in the DIRECTIVE, not at the engine

`--maxharm`, `--tol`, `--max-iter`, `--pin`, `--compression`, `--grid`, `--out-grid` all work by
**replacing the analysis directive in the TestBench** (`ApplyHbOverrides`, `ApplyLoadpullOverrides`).
The directive records are `init`-only, so "replacing" means rebuilding the record with every field
copied — verbose, and correct for a reason that is not obvious:

`ParametricSweepEngine` **re-elaborates and re-resolves the inner directive at every sweep point.** An
override handed to a freshly constructed engine would be discarded after the first point of a swept
run, and there would be nothing to see: the sweep would simply run at the directive's own values.

The netlist is the single source both the direct path and the sweep engine read. Put an override
anywhere else and the two paths disagree.

Two path rules follow from where the reader resolves things:

- `--grid` is made **absolute against the working directory** at parse time, because the directive's
  own `Grid=` was already resolved against the `.cnl`'s directory by `CnlReader`. A relative override
  left alone would silently change which directory it is relative to.
- `--out-grid` likewise.

## 6. `lp` and `lpp`

### 6.1 One function, two verbs

Loadpull and pursuit differ only in which directive is dispatched and which overrides apply.
Everything around that — selection, `--set`, measurements, printing, export — is identical, so
`RunLoadpull(args, pursuit:)` is one function. Options that belong to only one of them are
**refused, not ignored**: `--grid` on `lpp` (a pursuit searches for its terminations, it does not read
a grid) and `--out-grid` on `lp` both stop the run with a sentence saying which verb owns them.

### 6.2 `lp` enriches; `lpp` does not

`lp` runs `LoadpullPostProcessor.Enrich` on its result, exactly as `SchematicRunService` does, so a
headless export carries the same derived display metrics (`Pout_dBm`, `Zin`, `IRL_dB`, `AMPM_deg`)
as a GUI run. Without it a `.npy` written here and one written by the GUI would not carry the same
cubes.

A pursuit's follow-on loadpull grid is embedded under the engine's **raw** cube names, matching what
`SchematicRunService` publishes. The console printer therefore reads **both** spellings —
`Pout_dBm`/`Pout`, `Gt_dB`/`Gt`, `Efficiency`/`DE` — and scales the raw fractions to percent. Reading
only one set prints a table of em-dashes for the other, which looks like a run that produced no
figures of merit rather than like a naming mismatch.

### 6.3 What gets printed, and why it is not the cubes

A loadpull's cubes are `[gridPoint × pinStep]`. A 61-point grid driven up in 1 dB steps is a 61 × 30
table **per figure of merit**, and eight of those scroll a terminal without answering the question
anyone runs a loadpull to ask. So the default is **one row per Γ grid point**: where it was, how it
stopped, and its FOMs at the **last converged, non-tickle drive step** — the compression point when
the point compressed, the highest drive it managed otherwise. Reading a fixed drive index instead
would mix compressed and uncompressed points in one column. `--all` still dumps every cube.

A pursuit prints its MXP and MXE optima first, including a **non-converged** one: the engine still
publishes the last termination it looked at, and printing nothing there reads as "the search found
nothing" when what happened is "nothing it tried reached compression".

Swept results are printed **per sweep point**. The grid axis is located by NAME (`gridPoint`), not by
position, because a sweep prepends one axis per nesting level: taking the last axis would read a
two-frequency run as one grid of twice the size with half its rows mislabelled.

### 6.4 `.spl` / `.lpcwave` export

`lp -o out.spl` writes through `RfCore.Loadpull.SplWriter` rather than `DataSetExporter`. These are
the loadpull interchange formats the Data Display reads back, so a headless run can produce a file
the GUI opens as a measured surface. The writers take the **group** holding the loadpull cubes; it is
searched for (`GammaLoad`) rather than assumed, because a swept run leaves the cubes in the sweep's
own group — an unfound group would otherwise surface as "no frequency blocks", which describes the
symptom and not the cause.

## 7. Exit codes

| Code | Meaning |
|---|---|
| 0 | ran, and produced something usable |
| 1 | could not run — bad arguments, missing file, no matching analysis, a refusal, an exception |
| 2 | ran, but did not converge |
| 3 | `opt`: finished, and at least one enabled goal is unmet (§24.3) · `yield estimate`: finished, and the yield is below `--target` (§25.3) |
| 130 | stopped — `em`, `render`, `rail`, `smith`, `lvs`, `opt`, `yield` and `recognize`, and only when the run was cancelled at a work boundary (§8.4, §13.6, §17.5, §18.6, §19.4, §24.3, §25.3, §26.5). All of them write NOTHING on a cancellation |

`2` is deliberately **not** the same test for every verb. `hb` and `dc` fail on any non-converged
solve. A loadpull grid in which some points do not converge is a normal, useful result — the edge of
a Γ grid routinely will not — so `lp` returns `2` only when **every** grid point failed, and `lpp`
only when neither optimum converged and there is no follow-on grid. A rule that failed the whole run
on one bad point would make the exit code useless in a script.

**`3` exists for one reason** (brief-tuneopt-11 R-to11-3, overview D15; `yield` takes the same table, yield
overview D13). An optimization
that ran to its end without meeting its spec is neither a refusal (1: it could not run) nor a solver
failure (2: nothing converged), and a script must be able to tell all three apart — "the design cannot
meet this" is the answer it asked for, not an error in asking. Reusing 1 or 2 for it would make a CI job
treat a spec miss as a broken invocation, or a broken invocation as a spec miss.

## 7A. The CLI stays English, permanently

**Decided, not assumed.** If the GUI is ever localized, the CLI is not.

Every diagnostic the run services produce goes to two places: the Messages window and this program's
stderr. A localized error on stderr breaks every user's `grep`, every log scraper, and every CI job
that matches on a message — silently, and in a way that only shows up on machines in one country.
So the split is by SURFACE, not by user:

| | Follows the user's locale? |
|---|---|
| GUI display text — status lines, Messages entries, dialogs | yes, when localization lands |
| CLI stdout and stderr | **no, ever** |
| Every file format (`.cnl`, `.clay`, Touchstone, Gerber, DXF, `.kicad_pcb`, …) | no — see `FormatCultureInvarianceTests` |
| The expression language | no — see `expressions.md` §15A |

Mechanically this costs nothing, because of how coded diagnostics are shaped
(`brief-localization-groundwork.md` R-loc-5). A `CircuitRF.Diagnostics.Diagnostic` carries an id,
typed arguments **and an English default template**. The GUI renders it through the one render point
in `src/Ui` — the place a resource lookup would later be inserted. The CLI calls `Render()` and gets
the English template, always, with no lookup and no language setting consulted. Numbers inside a
diagnostic render invariantly for the same reason: `2.5` on stderr must not become `2,5` because of
where the machine is.

This is also why `EmRunResult` carries **both** `Error` (a plain string) and `Diagnostic`. The
redundancy is deliberate: the string is the contract §8 already promises — a refusal stays a refusal,
exit 1 with the run service's own sentence, `Cancelled` exits 130 — and the diagnostic is the
structure the Messages window needs to group, deduplicate and act on it. Neither replaces the other.

## 8. `em`

```
circuitrf em Amp.cem                     # → <workspace>/results/Amp.s2p (+ Amp_em.npy)
circuitrf em Amp.cem -o /tmp/amp.s2p     # explicit Touchstone destination
```

**The verb owns no EM logic.** It resolves two paths, calls `EmSetupResolver.Resolve` and
`EmRunService.Run`, and reports. Which kernel runs, how the geometry is meshed and what is refused all
live in `CircuitRF.Design` and `src/Engine/Mom`, and are the same code the Simulate button drives —
which is what makes "a headless run and a Simulate produce the same file" true by construction rather
than by care.

### 8.1 Both paths resolve by a WALK-UP, and neither is a flag

A `.cem` names a layout; the layout names (or inherits) a technology. Neither reference is stored
absolutely and neither needs an argument:

- **The layout.** `EmSetup.LayoutRef` is relative to the **workspace root** — the nearest ancestor
  `.cws` walking up from the `.cem` — and absolute when it names something outside it. With no
  workspace above it at all, the reference falls back to the `.cem`'s own directory, so a loose `.cem`
  beside its `.clay` works. That fallback is the GUI's own rule, not a headless special case.
- **The technology.** Resolved against **the layout's own parent workspace**, found by walking up from
  the `.clay` (`brief-foreign-documents.md` R-fgn-3) — never against "the current workspace", of which
  there is none here. A `.clay` with a null `TechRef` is the normal case and picks up the `.cws`'s
  `DefaultTechRef`.

The two walks start from different files and can land on different workspaces. That is deliberate: a
`.cem` in one workspace may point at a layout in another, and that layout's layers must be read by
*its* technology.

`--workspace <path.cws>` overrides the first walk, for a `.cem` being run from outside its own tree.
It is never required.

### 8.2 Where the results go, and why `-o` moves only one of them

Without `-o`, the run writes exactly where Simulate writes: `<workspace>/results/`, through
`EmRunService.ResolveSnpPath`. **That path is predictable by design** (R-em-19) so a schematic's SnP
reference stays valid across re-runs — a headless run that minted its own filename would orphan every
one of them, which is why the CLI does not get to choose a default here.

Two files come out, and they are not redundant:

| File | Holds |
|---|---|
| `<key>.sNp` | S only — the artifact a schematic REFERENCES by path |
| `<key>_em.npy` | the whole `DataSet`, including the diagnostics group (`tline` or `planar`) that makes a wrong answer diagnosable |

`-o` sets `EmSetup.SnpOutputPathOverride` — the same field the EM panel writes — so the Touchstone
moves and the `.npy` does not. There is no second naming rule to keep in step, and a `.sNp` extension
typed into `-o` is not doubled: the exporter appends the real one from the port count it finds.

With no workspace above the `.cem`, `results/` is created beside the `.cem` itself. The GUI's own
fallback there is the scratch recovery session, which does not exist headlessly; using the `.cem`'s
directory reuses the fallback its `LayoutRef` already has rather than inventing a third rule.

### 8.2a A 3D setup: `--solver`, and the solver in the path (brief-em3d-7)

```
circuitrf em via.cem                     # Solver3D: Palace → results/via.palace.s2p (+ via.palace_em.npy)
circuitrf em via.cem --solver palace     # this run only; the .cem is not rewritten
```

**The verb is `em`** (owner decision D1): a 3D setup is a `.cem`, and a sibling verb would split one
format's runs in two. `--solver palace|openems` overrides the setup's `Solver3D` in memory for this run
and never writes it back; any other value is refused (`cli.em.unknown-solver`). A 3D setup goes through
the same door — `EmRunService.Run` hands it to `Em3dRunService`, and nothing else calls that — so the
exit codes are this verb's own: a refusal (a missing or unvalidated solver, a 3D problem that cannot be
built, a mesh whose entity check fails) exits 1 with the run service's sentence, a cancellation 130.

**A 3D result carries its solver in its name** (em-3d.md §4.5), so running a second solver on one setup
never overwrites the first: `<key>.palace.sNp`, the `.npy` key `<key>.palace_em`, and a run directory
`results/<key>.palace/` that is **kept** — the `.geo` script, the mesh and its SHA-256, `groups.json`
(named object → Palace attribute), `entities.txt`, `config.json`, both programs' logs and Palace's
`postpro/` — so a run can be repeated by hand. An unchanged script reuses the mesh. `-o` still moves
the Touchstone only. **A planar result's path does not move by one byte.**

**`--solver both` (or `Solver3D: Both`) runs Palace and then openEMS on one generated problem**
(brief-em3d-10), each result landing where it would alone, plus a comparison, `<key>.compare_em.npy`
— `S_palace`, `S_openems`, `dMag_dB`, `dPhase_deg` (wrapped to (−180°, 180°]) and `dVec` = |ΔS|, each
over `[freq, i, j]`, so `circuitrf plot x.compare_em.npy --trace cube=dMag_dB,i=2,j=1` needs no `.cdd`.
Its first `note:` is the summary — the largest S21 and S11 differences and where — followed by what
makes a difference expected on THIS run; the same sentences are in the file, as the labels of the
`compare.Notes` cube. The comparison needs the two frequency vectors equal and refuses otherwise; it
never interpolates. With `-o base` the files are `base.palace.sNp`, `base.openems.sNp` and
`base.compare_em.npy`, and every file written is listed on stdout and in `--json`.
Everything either backend can refuse is refused **before either starts**, naming `--solver palace` or
`--solver openems` when one of them would run. A solver failing mid-run keeps the other's result,
writes no comparison and exits 1; a cancellation keeps a finished Palace result and exits 130.

**`--solver` on a PLANAR setup is refused** (`cli.em.solver-on-planar`, exit 1, brief-em3d-21): it
chooses between 3D solvers and never makes a setup 3D — that is the `.cem`'s `Solver3D` field. It used
to convert the run silently. **A Palace run prints Palace's own stages on stderr** — `Meshing (Gmsh)`,
`Solving: refinement pass k of N`, `Sweep: sampling · error … → tolerance …`, `Sweep: evaluating
frequencies`, `Reading results` — one row per stage change, and ends with a `note:` summary of what it
cost; **stdout and `--json` are unchanged**. The memory Palace's processes are using is shown by the
panel only (it changes every second, and a terminal row per second is noise). **`--force`** starts a
Palace run whose memory estimate is past 150 % of the machine's, which is otherwise refused before Gmsh
(or, when only the mesh's size shows it, before Palace, keeping the mesh for the forced re-run).

**A static 3D setup prints a MATRIX, not a point count** (brief-em3d-22). `Problem3D: Electrostatic`
or `Magnetostatic` runs Palace only (openEMS/Both are refused before discovery, and by `check`), writes
`results/<key>.palace_es.npy` (or `_ms`) with cubes `C` + `C_mutual` (farads) or `L` + `L_mutual`
(henries) on axes `Terminal i` × `Terminal j` labelled with the terminal names, and **no `.sNp`**. Stdout
prints the matrix in one engineering unit chosen so the largest entry reads 1–1000 (`Maxwell capacitance
(fF), ground = GND`, then a labelled square), and `--json`'s `data` is the DataSet. Progress on stderr
reads `Solving: pass 1 of 1 (no refinement) · terminal k of N`.

**An eigenmode 3D setup prints a MODE TABLE** (brief-em3d-23). `Problem3D: Eigenmode` runs Palace only
(openEMS/Both are refused before discovery, and by `check`, as is a wave port on either), writes
`results/<key>.palace_eig.npy` with cubes `f` (Hz) and `Q` along `Mode`, plus `Q_ext`/`Q_unloaded` when
the problem has lumped ports and `Participation` [Mode × Domain], and **no `.sNp`**. Stdout prints one row
per mode: its number, f in GHz, Q, Q unloaded (when there are ports) and the region holding most of its
electric energy. A driven setup with wave ports writes its `.sNp` renormalised from each wave port's mode
impedance to its Z0, and the header's port line says so.

### 8.3 What goes where, and the three lists

§3.1's split, applied: the summary and the written file paths are **stdout**; progress, the resolved
workspace/layout/technology, and the run's own three lists are **stderr**.

`EmRunResult` separates `Notes` / `Warnings` / `Errors` by what the reader is expected to DO about
each, and the verb prints all three under those labels rather than flattening them:

| Prefix | Means |
|---|---|
| `note:` | the run explaining itself — which kernel ran and why, the mesh's own sentences, RLGC, ports |
| `warning:` | an answer was produced and something in it is NOT what was drawn — a level with artwork dropped, a drawn via discarded, a stale `.sNp` about to be replaced, `cell/separation` past the range it was measured over |
| `error:` | something the user asked for and did not get — a results file that could not be written |

Flattening them into one list is the exact defect the three-list split was introduced to fix, and it
is just as wrong on a terminal as it was in the Messages region.

**Worst first, and the class is DATA** (`brief-em-run-severity-and-check.md` R-emsev-1). Errors, then
warnings, then notes. Grouping alone is half the answer: a run of this shape produces of order
thirty-five lines and a reader works down from the top, which is how three sentences saying a user's
capacitor had been removed from the solve went unread among the core count and the equal-area via
substitution. Which list a line goes on is decided by the `EmFinding.Severity` its PRODUCER attached
— `PlanarExtractor`, `SurfaceMesher`, `PlanarSolve` — never by which list a call site happened to
append to, and never by a `"WARNING: "` prefix in the prose, which nothing downstream can read
without being told the spelling.

### 8.4 Exit codes: a refusal stays a refusal

`EmRunStatus` distinguishes `Refused` / `NoLayout` / `EngineError` / `Cancelled`, and each carries a
written explanation of what is wrong with *this* setup. The verb prints that explanation and exits
non-zero; it never collapses them into "EM failed", because the explanation is the only part a user
can act on.

| Status | Code |
|---|---|
| `Ok` | 0 |
| `Refused`, `NoLayout`, `EngineError` | 1 |
| `Cancelled` | 130 |

**A `.cem` that names a 3D solver (`Solver3D`) is `Refused`** until a 3D backend exists
(brief-em3d-3; briefs 7 and 9 add them), with `em.solver-3d.not-built`: running a planar kernel
instead would write a result for a solver the setup did not ask for. `check` on such a setup builds
the 3D problem (`Em3dGenerator`) in place of the planar preflight, reports `Em3dProblem.Validate()`'s
findings as errors, and names the planar-only fields the setup keeps but does not read, at info.
The layout's stem-paired `.wBond` is part of that problem (brief-em3d-4): its wires' refusals are the
generator's, and a foot overhanging its pad or a metal defined differently by the technology and the
`.wBond` is a warning (`check.em.finding`).

### 8.5 What the verb does NOT do

- **Create or edit a `.cem`.** It runs one. A setup with no ports, no technology or no signal
  conductor is REFUSED with the sentence the run service already writes.
- **Back-annotate.** Writing an SnP component into a schematic is an editor operation and stays in
  `src/Ui`.

### 8.6 The gate

`tests/Ui.Tests/Em/EmCliVerbTests.cs` builds a real workspace on disk, runs the real `Cli em` process,
and compares the `.sNp` **byte for byte** against what `EmRunService.Run` writes for the same setup —
and asserts the file lands at the same PATH. A tolerance-based comparison would pass just as happily
if the two paths had drifted onto different geometry, a different technology or a different filename,
which are the three failures the project split could plausibly have introduced.

One line is exempt and only one: `EmSnpProvenance` stamps the UTC time the file was written, so two
runs a second apart can never match byte for byte. Everything else does, **including all three
provenance hashes** — geometry, mesh and ports — which is what proves both paths resolved the same
layout, stackup and ports. The `.npy` matches with no exception.

**Any test that launches a verb as a process follows `Engine.Tests`' pattern** (`MatchStampTests`,
which learned it first): a `ReferenceOutputAssembly="false"` project reference on `src/Cli` plus a
`CliDir` assembly-metadata attribute, and the DLL exec'd directly. A nested `dotnet run` starts an
MSBuild inside a `dotnet test` that already holds the build locks and does not finish — silently, with
no CPU and no child process. Drain both of the child's pipes concurrently too: `em` says enough on
stderr to fill that pipe's buffer and deadlock a sequential reader.

### 8.7 A thermal setup (brief-em3d-74)

`circuitrf em x.c3d --setup Hot` runs an embedded thermal setup (`Problem3D: Thermal`) — the same verb, no new one
(overview D2). It goes through the same door, `EmRunService.RunThreeDView`, which hands a thermal setup to
`ThermalRunService.Run` (`src/Design/Thermal`) — the function the GUI's Simulate reaches the same way. What differs
from an EM run:

- **What it writes.** The `DataSet` as `results/<key>.thermal.npy` and the run directory `results/<key>.thermal/`,
  kept: `model.geo`, `model.msh` and `postpro/paraview/thermal/thermal.pvd` (one step per sweep point, Palace's layout
  and encoding). **`-o` moves the `DataSet` only**, as it moves only the Touchstone for EM; the extension is forced to
  `.npy`.
- **What it prints.** Each probe statistic and measure at the sweep's last point on stdout; every cube is in `--json`.
  The energy balance, the solver, its iterations and residual are notes on stderr.
- **What refuses it.** Every error `check` reports for the setup (`C3dThermal`'s one validator), and a missing Gmsh —
  the Palace run's refusal, with the same install offer. `--solver` names an EM solver and is refused on a thermal
  setup. Exit codes as §7.

`explain x.c3d --analysis Hot` reports the size before any mesher runs: the estimated tetrahedra and unknowns, the
solver that count selects (direct below 5,000 unknowns — brief 72's crossover) and the memory.
Gate: `tests/Ui.Tests/ThreeD/ThermalRunTests.cs`.

**A thermal setup driven from a circuit (brief-em3d-79).** A `Currents` entry `{ "FromCircuit": { "Schematic": …,
"Analysis": …, "Instance": … } }` makes `em` run the circuit's HB power sweep first — through `HbCircuitRun`
(`src/Design/Circuit`), the function the `hb` verb calls, with the chain chosen by `ChainSelector`, which moved there
for this — and then the thermal run at every HB point, on the HB's own axes. **`--set var=expr` sets a global of the
CIRCUIT**, before it is elaborated, as `hb` sets it; the thermal setup's own values are the document's. An HB that
cannot run is the refusal, verbatim; exit codes as above. Gate: `tests/Ui.Tests/ThreeD/ThermalCircuitLinkTests.cs`.

### 8.8 How a run ended, and a re-run of a current result (brief-em3d-98)

Every 3D and thermal run leg keeps a **`status.json`** in its run directory (`results/<key>.palace/`, `.openems/`,
`.thermal/`): `State` (`running`, `complete`, `cancelled`, `notConverged`, `failed`), `Started`/`Finished` (UTC), a one-sentence
`Detail`, and the `Pid` that wrote `running`. The run services in `src/Design` write it (`C3dRunStatus`), so `em` and the GUI's
Simulate keep the same record. A leg writes `running` and **removes the previous run's `document.c3d`/`inputs.json`** before the
solver touches the directory, then writes how it ended on every path; its inputs record is kept only when it completed or did
not converge — so a cancelled re-run can never leave the old record reading as current, and a Both run whose openEMS leg is
cancelled still keeps Palace's. `notConverged` is each solver's own signal: openEMS's per-port convergence, Palace's "linear
solver did not converge", the thermal solver's Newton flag.

**`em` never asks before replacing a current result**, as the GUI does. When every leg the run will run already has a
complete, current result (`C3dSolveStatus.AllCurrent`), it prints one stderr line and runs:

```
note: the result for 'EM1' was already current; running again
```

### 8.9 One component's drawn part: `--component` (brief-agent-authoring-overview AA-1)

```
circuitrf em [<workspace dir | .cws | .ctech>] --component "SPIRAL N=3 W=10 um S=8 um Din=100 um" \
             -o L1.s2p [--freq 1GHz:20GHz:1GHz]
```

The "extract this spiral" half of the spiral's hybrid model (owner decision): the closed form is an
estimate, flagged as one, and this is how a caller gets the number to rely on. **A mode of `em`, not a
verb**, because what it produces is exactly what `em` produces — a Touchstone, the `.npy` in the
workspace's `results/`, and the same three-list report (`ReportEmRun`, shared with the `.cem` path).

It owns nothing. The line is read by `CnlReader` and elaborated, so `W=10 um` means what it means on an
instance line; the artwork is the component's own built-in PCell; `ComponentEmExtraction`
(`src/Design/Layout/Em`) puts a port label on every pin — **port n is terminal n**, so the file drops into
the schematic as an SnP in the part's place — and `EmRunService.Run` solves it. The technology is the
usual walk-up from the path (or the current directory), or a `.ctech` named directly; none is a refusal.
`-o` is required — there is no setup to name a default after — and `--freq` is refused on a `.cem` run.

**Two things it refuses rather than answer wrongly.** A component with no built-in generator has no
drawn part. A part whose artwork draws on a layer the stackup does not carry (other than a
`PresentWithLayer` mask) is refused naming the layer: a `TFR`'s film is a sheet resistance, not a
conductor level, and a solve would see two unconnected contacts.

**The mesh is traded for reach, and says so.** `ComponentEmExtraction.ExtractionMesh` is two cells
across the narrowest metal with no edge fan: at the default the shipped 2.5-turn spiral was 20,637
unknowns, past the planar kernel's 5,000 ceiling. The edge-mesh-off warning the run prints is the
trade's own statement (Q reads low). Measured numbers: `src/Design/RESOLVED.md` (AA-1). MCP: the `em`
tool's `component` and `freq` options, with `path` the workspace folder.

## 9. Adding a verb

1. Add the case to the dispatch switch and a line to `PrintHelp`.
2. Follow §3's five steps; use `SelectTop` with your base-analysis test.
3. Put overrides in the directive (§5), not at the engine.
4. Results to stdout, everything else to stderr (§3.1).
5. Pick the exit-code rule that is honest for that analysis (§7) — do not copy `hb`'s by reflex.
6. **Give the structured document its three lines** (§3.2): set `JsonRun.InputPath` when the input is
   known, `JsonRun.Analysis` to the chain that actually ran, and `JsonRun.Data` to the same `DataSet`
   the export writes. Call `JsonRun.AddOutput` beside every "Wrote …", and refuse through
   `JsonRun.Fail(CliDiagnostics.…)` rather than `Console.Error.WriteLine` + `return 1` — a refusal
   that is only prose is a refusal a caller has to parse back apart.
7. **Record every new diagnostic id in `CliStructuredOutputTests.ExpectedIds`, in the same change.**
   An id is a permanent contract and that list is what makes an accidental change visible; a new one
   not recorded leaves `DiagnosticIds_AreTheCommittedSet_UniqueAndCaseDistinct` RED, so the next
   person to run the suite inherits a failure that is not theirs. **This has now happened twice** —
   the `.cdd` arm's four ids, and railRF's `rail.frequency-flags.not-in-this-phase` — which is what
   makes it a checklist step rather than a habit. The obligation runs the same way a row added to
   `DocumentKinds.Classify` obliges an arm in `check` and a case in `explain`.
8. Update this file and the verb list in the repo-root `CLAUDE.md`.

`em` follows 1, 4, 5, 6, 7 and 8 and is deliberately outside 2 and 3: it does not read a `.cnl`, so there is
no chain to select and no directive to override. Its analogue of §5's rule is §8.2's — the one
override it takes lands in the `EmSetup`, not at the run service, for the same reason.

`check`, `explain` and `read` follow 1, 4, 6, 7 and 8, and their §5 analogue is §10's — they run
nothing and they write nothing.

`render` follows 1, 4, 6, 7 and 8. Its §5 analogue is §13's and it is the same one the authoring verbs
have in a different costume: **it owns no rendering.** Every pixel comes out of the three renderers in
`CircuitRF.Render` that the application draws each frame with, so "a headless picture and the GUI's are
the same picture" is true by construction rather than by care — which is what makes §13.6's byte
identity a gate rather than an aspiration.

`rail` follows 1, 4, 5, 6, 7 and 8 and is outside 2 and 3: it reads a `.crail` rather than a `.cnl`, so
there is no chain to select and no directive to override. Its §5 analogue is `render`'s and the
authoring verbs' in one: **it owns no analysis and no rendering** (§17.1). Its §7 rule is `em`'s —
a refusal exits 1 with the run service's own sentence, a cancellation exits 130 and writes nothing.

`reference` follows 1, 4, 6, 7 and 8 and is outside everything else, because it reads no file either
(§12). Its §5 analogue is R-aut6-7: **it transcribes nothing.** The prose half is the authored page,
embedded; the component half is generated from the live registries at every call. A fact typed into
that verb is a fact that will disagree with the code the first time either changes.

`lvs` follows 1, 4, 5, 6, 7 and 8 and is outside 2, 3 and 5: it reads a cell's two views rather than a
`.cnl`, so there is no chain to select. Its §5 analogue is the authoring verbs' and `render`'s in one:
**it owns no comparison** (§19.1). Every finding comes out of `LvsRun.Run` in `src/Design/Layout/Lvs`,
which is the function the GUI panel calls, so "a design that passes headlessly passes when it is
opened" is true by construction. Its §7 rule is `check`'s with `em`'s cancellation on the end: 0 unless
something at or above `--severity` was found, 1 on a refusal, 130 on a cancellation, and never 2.

`serve` follows 1, 7 and 8 and is outside all the rest, because it is not a verb that does work: it is
the one adapter that dispatches to the others (§11). Its §4 analogue is the inversion of §3.1 —
stdout is the protocol and the result goes into a frame — and its §5 analogue is R-aut-1: it owns
no logic at all, so there is nothing for an override to land in.

`convert`, `new` and `import part` follow 1, 4, 6, 7 and 8 and are outside 2, 3 and 5 for the same
reason: they run no analysis. Their §5 analogue is stronger and is the whole of
`brief-automation-3-authoring-verbs.md` R-aut3-1: **an authoring verb calls the capability the GUI's
own command calls, and adds nothing of its own.** A verb that re-implements what a view model does
will diverge from it silently, and the first symptom is a document created headlessly that the
application treats as subtly malformed — so step 2, for these, is "find the function the GUI calls,
and if it is trapped inside a view model, extract it and change the view model to call it too". That
extraction is not optional and it is not a follow-up.

## 10. `check` and `explain`

`brief-automation-4-check-and-explain.md`. These are the two verbs that close a headless client's
loop: it writes a document, asks whether the document is sound, and asks what circuitRF made of it —
without paying for a run.

```
circuitrf check   <path> [--recursive] [--severity warning|error]
circuitrf explain <path> [--expr "<expression>"] [--set var=expr]
                         [--analysis [<name>]] [--ref <relative-ref>] [--setup <name>]
```

**`--setup <name>` chooses which of a `.c3d`'s embedded setups the 3D picture is explained through** (designer
feedback round 11), spelled as `em` and `render` spell it — a view embedding several cannot choose one itself, and
said so by naming this flag, which `explain` did not take until then. An unknown name is refused listing the real
ones; on a `.cem` (its own setup) or any other kind it is a refusal. `render` takes it too, for the same reason.
`--analysis <name>` on a `.c3d` stays what it was: its THERMAL setups' size walk. The air-box line now gives every
face's distance from the content and where it came from — `setup: 2 mm`, `setup: 1.2 mm, 10 % of the content's
extent`, `floor`, or `default: 18.74 mm, λ/8 at 2 GHz, the sweep's lowest frequency` — where a face that stated
only its boundary used to read `setup` and hide that its padding was the default's.
Gate: `tests/Ui.Tests/Cli/ExplainSetupCliTests.cs`.

**A picture is a kind: `picture`** (brief-img-2 R-im2-6) — `.png`, `.jpg`/`.jpeg`, `.bmp`, `.gif`, `.webp`, and a
named file with a wrong or missing extension by its signature. `check` reports what kind of drawing it reads as
(`check.picture.kind`, or `check.picture.no-drawing` with what was seen) as an INFO and exits 0; `explain` prints the
measurements behind that reading; `find` lists a workspace's pictures with their kinds. All three read through
`ImageSource` and `ImageKind` and nothing past them — `docs/design/image-to-circuit.md` §5.5.

### 10.1 Neither runs an analysis, and neither writes

**R-aut4-1.** `check` on a large design has to be cheap enough to call after every edit, so it stops
at elaboration — which is what answers "do the parameters, expressions and cycles resolve?" — and
nothing here solves a matrix. A check that needs a solve to answer belongs in the run verbs' own
warnings.

**R-aut4-6.** Not a repair, not a re-save, not a cache file. A caller must be able to run either verb
on a read-only tree and on a workspace another process has open. The technology cache is per
invocation and in memory; DRC waivers are read from the `.clay` and never written back;
a `.csch`'s `.cnl` round trip (§10.3) happens as a string.

### 10.2 `check` calls the validators that already exist

**R-aut4-2, and it is the whole design.** The repo is full of validators and they are scattered
rather than missing; what `src/Cli/Check.cs` adds is the WALK and the reporting, never a rule.

| Validator | Where | What it answers |
|---|---|---|
| `CellViewFileValidator.DescribeDefect` | `src/Design/Cells` | is the file the view its extension claims? |
| `CellFolder.ResolvePrimary` | `src/Design/Cells` | primacy — a named primary that is missing, or none chosen |
| `LayoutPersistence` (`LayoutView.LoadFindings`, built by `LayoutLoadAudit`) | `src/Design/Layout` | what reading a `.clay` IGNORED — a key the reader does not know (a warning: a newer file carries some) — and a `Poly`/`Path`/`Curve` with too few vertices to be a shape (an error). The GUI posts the same findings on a fresh load |
| `NameValidator` | `src/Design/Cells` | a cell name the GUI would reject |
| `TechValidation.Analyze` | `src/Design/Layout` | a `.ctech`'s problems, already typed as `TechProblem` |
| `TechnologyResolver.ResolveForDocument` | `src/Design/Layout` | the technology walk-up |
| `EmSetupResolver.Resolve` | `src/Design/Layout/Em` | a `.cem`'s layout and technology, and its refusals |
| `EmRunService.Preflight` | `src/Design/Layout/Em` | a `.cem`'s EXTRACTION and MESH — everything wrong with a run that is knowable before the first frequency point is solved |
| `CellSymbolResolver` | `src/Design/Schematic` | every cell reference on a schematic |
| `NetExtractor` | `src/Design/Schematic` | naming conflicts — two labels on one physical net |
| `Elaborator.Elaborate` | `src/Core/Elaboration` | parameters, expressions, cycles, node numbering |
| `ChainSelector` | `src/Design/Circuit/ChainSelection.cs` | whether a declared analysis chain will dispatch |
| `DrcPredicateParser` | `src/Design/Layout/Drc` | a `.wasm` rule that will not parse |
| `DrcEngine` | `src/Design/Layout/Drc` | layout design rules |
| `TerminalMap.Validate` | `src/Design/Layout` | which layout pin is which schematic port — a declared map that names a pin the `.clay` does not have, a port outside range, a duplicate, a pin claimed twice, an unmapped port or pin, a map DERIVED by order, or one that cannot be derived at all |

**A `.cem` is extracted and meshed, and never solved** (`brief-em-run-severity-and-check.md`
R-emsev-5). `EmRunService.Preflight` is the first half of `EmRunService.Run` — the same flatten, the
same two extractors, the same registry choice, the same ports, the same mesh — **factored out of
`RunCore` so both call it**, with the solve left off the end. Every finding a run reports is produced
before the first frequency point: on the design that motivated EM-SEV the extract-and-mesh phase is a
second or two and the solve is eleven minutes, and a run that had silently dropped half the drawn
circuit used to `check` clean. It writes nothing, exactly as §10.1 requires, and the findings carry
the severity their PRODUCER attached (`EmFinding`), never one this verb invents.

**A cell's TERMINAL MAP is checked before anyone asks for an LVS**
(`brief-lvs-1-terminal-map.md` R-lvs1-4). `TerminalMap.Resolve` answers "which layout pin is which
schematic port" once, for `check`, for the cell Properties panel and for LVS itself, and it **returns
the ORIGIN of its answer as well as the answer** — declared, the import table, by name, by order, or
nothing. A map derived BY ORDER is a warning on every run that derives one, including an otherwise
clean one: it is a guess that happens to be right most of the time, and a guess that is never
announced is the shape of a wrong answer nobody finds. **A cell with no layout view — which is most
cells — is silent**, because one finding each would bury every real one.

**A rule that exists only in `check` is a rule the GUI does not enforce** — a design would pass here
and be refused when someone opened it. The converse matters just as much and cost a round to find:
see §10.3.

**Every finding is a `Diagnostic`** with a stable `check.` id and the producing validator's own typed
values (R-aut4-4) — `TechProblem`'s `Area`, a DRC violation's rule name, layer and measurement. The
id is the contract; the sentence is not.

**Exit code (R-aut4-5): 0 if nothing at or above `--severity` was found, 1 otherwise.** Default
severity is `error`. There is no `2` — nothing here converges. A check that found warnings and no
errors **exits 0 and still reports them**, because the alternative makes the exit code useless in CI.
Two states are deliberately warnings rather than errors: a cell sub-folder holding several views and
no named primary (`PrimaryState.NoPrimary`, which that enum's own remarks call "not an error"), and a
layout that resolves no technology (`layout-view.md` §2.4's normal, fully-supported state).

**One verb over every document type** (R-aut4-11, R-aut-9). The kind comes from the path — by
extension, and for a directory by what it contains — and an extension circuitRF does not own is
offered to **`convert`'s own classifier**, which reads content, before being called unknown. A GDSII
or Gerber file is reported as interchange rather than as something circuitRF cannot read; it is not
VALIDATED, because there is nothing to validate it against. **OASIS is the exception** (brief-oasis-gdstk.md
§10b): `GdstkImport.Header` opens it in the gdstk worker and closes it, transferring no geometry, and
`check.oasis.summary` reports its cells, counts, grid and `LAYERNAME` names. gdstk parses the whole file on `open`
(its CRC-32 or checksum included), so a damaged file is `check.file.unreadable`, an error, exactly when the import
would refuse it. With no worker it is `check.oasis.no-worker`, a warning: the file is named and was not read.

**A bare `.ccell` is the CELL it declares** (R-lvs1-4d), so `check` on one checks the folder around
it. That one redirection lives in `check` rather than in `DocumentKinds.Classify`, because every other
verb that classifies a path — `render`, `explain`, `find` — takes a cell as a DIRECTORY and would be
handed a file.

### 10.3 A `.csch` goes through the `.cnl` on its way to the elaborator

The GUI's Simulate is `NetExtractor.Extract → CnlWriter.Write → CnlReader.Read → Elaborator`
(`WorkspaceViewModel.WriteNetlist`, then `SchematicRunService.Prepare`), and the round trip is
load-bearing: a schematic parameter is an EXPRESSION, so `BiasTee=on` read straight out of extraction
fails elaboration with "Unresolved name 'on'", while the same value written to a `.cnl` and read back
is quoted by `CnlReader` and elaborates.

`check` and `explain` therefore both read a schematic through `src/Cli/CircuitSource.cs`, which
performs that round trip in memory. Skipping it made `check` report errors the application does not
have — the mirror image of R-aut4-2's rule, and just as bad.

### 10.4 `explain` reports resolution, and shows the walk

**R-aut4-7.** The value is as much in the path taken as in the answer, so every resolution comes back
as a step: what was being resolved, from where, to what, and by which rule.

- **A `.cem` or `.clay`** — the workspace found by walking up, the layout it resolved to, the
  technology and *which* workspace resolved it. The two walks start from different files and can land
  on different workspaces; that is deliberate (§8.1) and is exactly the thing a caller cannot
  otherwise see.
- **A `.cem` also reports its `return plane`** (RP-1, `brief-em-return-plane-1-explicit-ground-layer.md`
  R-rp1-8) — the conductor every port in that run returns through, **its height in µm**, and whether
  it came from R-em-4's inferred rule (the top surface of the highest ground-designated conductor
  below the lowest analysis level) or from the setup's own `GroundStackupLayerName`. It is the
  headless half of the run's "Every port returns through …" note, and before RP-1 the answer had no
  spelling outside a full solve: the panel's own Ground-reference row is bound to the *cross-section*
  readback, which a full-wave run never produces. **It runs the EXTRACTION, not the analysis** —
  geometry and a stackup in, a medium out, no solve, so R-aut4-1's no-solve budget is untouched — and
  it reports what the extraction resolved rather than restating the rule, because a rule restated
  here is a rule that can disagree with the run. The step is absent when the extraction refuses: the
  refusal is a `check` answer, and repeating it here would report a plane the run does not have.
- **…and each port's OWN return, once a port in that run has one** (RP-2b,
  `brief-em-return-plane-2b-port-reference-in-the-layout.md` R-rp2b-9). Since RP-2a a port may be
  referenced to DRAWN metal instead — two cuts at one station, driven against each other, with the
  plane nowhere in its loop — so a mixed run reported through the single plane step alone would say
  the plane was the negative terminal of a port for which it is not. Each port then gets a
  `port N return` step: the plane, or the conductor its return terminal landed on and the point it
  landed at. Read off the resolved `PlanarPort` the extraction produced, for the same reason the
  plane step is: a rule restated in the CLI is a rule that can disagree with the run. **Silent when
  every port returns through the plane**, which keeps an ordinary board's `explain` exactly as long
  as it was — but the moment one port differs, EVERY port gets a row, because the interesting
  question about a mixed run is which ports are which.
- **`--analysis`** — every declared chain, whether it is runnable, which one would dispatch and for
  which verb, and whether a named inner analysis would be **promoted** to its wrapper (§4). For a
  kind that reads one, it also prints the effective **`MarginThreshold`** in dB, or the word `none`
  — the WSProbe stability-margin report threshold (`stability-wsprobe.md` §9.4). The default is not
  written in the document, so it is reported rather than left to be assumed. Chain
  selection goes through `ChainSelector`, the same function the run verbs select with, so the report
  and the run cannot part company. A named analysis that comes back from selection but is not of that
  verb's kind is **not** reported as dispatched: `SelectTop` hands back `owner ?? named`, so
  `lp -a HB1` returns HB1, and calling that "lp dispatches HB1" would describe a run that cannot happen.

  **`runnable` is two claims, not one (AUT-8 R-aut8-8).** The chain has to bottom out in an enabled
  analysis *and* every reference the analysis names by string has to resolve. It used to be the first
  half alone, so a loadpull-pursuit naming a load tuner and a source tuner that do not exist in the
  design came back `runnable: true, dispatched: true, dispatchedBy: lpp`. Whether a thing will run is
  the question this verb exists to answer, and answering it optimistically is worse than not
  answering: the caller acts on the yes, and the refusal it eventually gets is about something it has
  already been told is fine. The references checked are the tuner instance names, the inner analysis a
  sweep wraps, the swept variable, and the variables a tone expression reads — each a lookup against a
  list already in memory, so R-aut4-1's no-solve budget is untouched. When any fails, `unresolved`
  carries one line per failure naming the key and what it pointed at, because "not runnable" without
  **which** reference failed leaves the caller no better off. What is deliberately NOT claimed is
  anything needing a solve: "this bench has no bias source" is a property of the solved circuit, and
  asserting it here would be the same overreach in the other direction.
- **`--expr`** — evaluated in the design's own resolved scope, through the one expression engine
  (`Elaborator.EvaluateInGlobalScope`), never by substitution. The kind is reported, never coerced.
  `--set` applies first, exactly as it does for a run verb (§5).
- **`--ref`** — what a relative cell reference resolves to from that document's directory, its
  three-state result (`resolved` / `not-found` / `primary-missing`), whether it leaves the workspace,
  and whether it only resolved through a recorded move.

RND-3 (`brief-render-3-query-surface.md`) adds **the three questions a caller has to be able to ask
before `render` is usable** — *what cells does this hold and which views does each have*, *what layers
can I ask for*, *how big is this*. They are options here and not three new verbs (R-rnd3-1): they are
all asking what circuitRF DECIDED, which is what this verb is for, and `serve`'s tool count is capped
deliberately (§11.3).

- **`--cells`** *(a workspace, a folder, or one cell folder)* — every cell reachable from the path
  and, for each, the three views with the file primacy resolved to and the **state** it resolved in.
  It reports the RESOLUTION, not a directory listing (R-rnd3-3): `state` is `CellFolder.ResolvePrimary`'s
  own five-way answer and `defect` is `CellViewFileValidator.DescribeDefect`'s, both of which `check`
  already surfaces, so a cell whose schematic sub-folder holds three files and names no primary is
  **listed with its ambiguity**, not omitted and not silently resolved to the alphabetically first one.
  The enumeration is `CellLookup` — the same one `render --cell` resolves through, so a cell this lists
  is a cell that verb can draw. **`.generated-cells` is excluded by default and `--all` includes it**
  (R-rnd3-4): the project tree hides that folder deliberately, and the name lives once in
  `ReservedFolders`, below the firewall, shared with the tree's own scanner. On a cell folder it
  reports that one cell, in the same shape — which is what makes it composable with `render`.
- **`--layers`** *(a `.clay`, a `.ctech`, a cell or a workspace)* — the resolved technology's layers,
  each with its number/datatype pair, purpose, visibility, selectability, colour and fill, **and how
  many shapes this document draws on it**. That last field is what makes this worth having (R-rnd3-5):
  a technology defines every layer a process has and a given `.clay` draws on a handful, so a caller
  told only the technology's list will ask `render --layers` for empty layers and conclude the render
  is broken. The count is **hierarchy-inclusive and array-multiplied** (R-rnd3-6), through
  `CellHierarchy.ShapeCountsByLayer` — the same walk `render --json`'s own `layers[].shapes` now uses,
  so the two verbs cannot disagree. On a `.ctech` or a workspace there is no document to count against
  and the field is **absent, not zero**. The technology walk is reported as a walk (R-rnd3-7), and
  where it resolves to nothing that is the answer, with the **fallback palette named** — that is what
  `render` will draw with, and a caller needs to know the colours it gets are not the process's.
- **`--extents`** *(a `.clay`, `.csch`, `.csym` or a cell)* — how big the document is, **from the same
  function `render --fit` frames on** (R-rnd3-9): `CircuitRF.Render.DocumentExtents`. If the two could
  disagree the number would be worse than useless, because a caller uses this one to compute a
  `--window` for that one. **Base SI with the unit AND the scale named** (R-rnd3-8) — a layout's
  numbers in metres with the DBU scale that produced them, a schematic's and a symbol's as
  `design-units`, said to be dimensionless rather than dressed up in metres. A document with no
  geometry reports `empty: true` **and no coordinates** (R-rnd3-10): `(0,0,0,0)` is a point at the
  origin, which is a different fact and one a caller would happily divide by. `perLayer` is layout-only
  and covers the document's own shapes on the layers that have geometry.

  What this box does NOT carry is the room a FIT adds for marks measured in PIXELS at render time — a
  symbol pin's name and a Fixed-mode ruler's readout, neither of which has a world extent until a page
  size is chosen. That is the stable, zoom-independent answer a zoom-independent verb owes, and the
  `note` field says so.

  **Every box also comes back as a `window` STRING, in the spelling `render --window` accepts**
  (R-aut12-3) — whole document and per layer. The numbers above are base SI, and `--window` refuses a
  bare number, correctly and for a good reason; so until this landed, the one tool that says where the
  content is emitted exactly what the other tool rejects, and every windowed render needed a hand
  conversion. A layout is spelled in the document's own DISPLAY unit (`0um,-3000um,402000um,402000um`)
  and not in metres, because `LayoutUnits.TryParse` — which is what `render` parses a coordinate with —
  reads nm, um, mm, mil and in and **has no spelling for a bare metre at all**, so emitting the numeric
  field's own unit would have produced a string that reads plausibly and is refused. A schematic's and a
  symbol's are bare design units, which is what `--window` takes there.

  The spelling is `LayoutUnits.Spell`, beside the parser it inverts, and **its decimal count is derived
  rather than chosen**: one DBU is `1000 / (nm-per-unit × dbu-per-micron)` of the display unit, so that
  many places resolve a single DBU and one more puts the two roundings an order of magnitude apart. A
  fixed four places silently quantises a nanometre-resolution layout written in millimetres to 10 nm —
  a window off by a hair, which is invisible. The suffix table is `LayoutUnits.AsciiSuffix`, which is
  **not** the display table beside it: that one gives micrometres as `µm`, which the parser does accept
  but which travels through an argument list, a JSON document and somebody's shell on the way back.

- **`--footprints`** *(a `.csch` or a cell)* — per component: **what it states, what that resolved to,
  how many pads against how many ports, and — for a built-in case size — the technology the land
  pattern would be generated against** (brief-footprint-4 R-fp4-4b). Resolution here is two different
  walks chosen by the first four characters of the stored value: `smt:` goes to the case table and is
  GENERATED on demand, anything else is a path resolved against the schematic's own folder exactly as
  a `CellRef` is — so which one produced the answer is reported, not just the answer. The technology
  is named **for a built-in and only for a built-in**, because a generated land pattern resolves its
  copper, mask and silkscreen BY ROLE against it and the shipped technologies disagree about every
  layer key; a cell's artwork is already on disk on keys of its own, and naming a technology beside it
  would suggest it was about to be re-resolved. The pad count is printed **against the port count on
  the same line**, because the pair is the contract and two numbers in two places is how a mismatch
  goes unread. It reads the SCHEMATIC and not the netlist: `Footprint` is dropped before parameter
  resolution (R-fp2-6), so asking the netlist would report every design as stating none. The
  resolution is `FootprintCatalog.Resolve`, the same one Update Layout performs, so `explain` cannot
  describe artwork the application would then refuse.
- **`--tunables`** *(a `.csch`, a cell, or a `.cnl`)* — every value that can be tuned or optimized, at
  any depth (`docs/design/tuning-optimization.md` §2): its key (`R1.R`, `Wline`, `X1.Rbias`,
  `DUT:R3.R`), where it lives and how many instances share it (`DUT · ×2`), its value, and the range the
  setup gives it — or the default range a first activation would — with whether Push can write it and,
  if not, why. Then the keys the schematic's tuning setup names that resolve to nothing. It owns no
  discovery: a schematic's list is `TunableCatalog.Discover` through `DiskCellResolver`, the descent a
  run makes, so the keys are spelled as the netlist spells its cells. On a `.cnl` every global counts as
  a variable and nothing is read-only, because a netlist has no drawing to push into.

The seven questions are **refused together rather than ordered** (R-rnd3-2) — each asks something
different, and a precedence nobody stated would be an invention. `--all` is `--cells`' own modifier and
is refused beside anything else; `--view` is only ever a cell folder's disambiguator, spelled exactly
as `render` spells it, and a cell folder holding more than one view is a refusal LISTING them.

**A 3D `.cem` (`brief-em3d-5`) reports its problem IN ADDITION to the walks above**: the solver and
em-3d.md §4.3's guidance for this geometry (a sentence citing its row, never a change to `Solver3D`),
the operating temperature and where it came from, each conductor's σ at it, every material and where
its values resolved from, every solid and sheet in construction order with its bounding box (a sheet
with the reason it is one), each wire with BOTH loop heights and the level that set its foot length,
the ports with their reference planes, the air box's faces, and the size of the run. Palace's size row
is an ESTIMATE from the Palace section's mesh sizes (`brief-em3d-7`); openEMS's is EXACT, because
`FdtdGrid.Build` is the grid a run writes (`brief-em3d-8`): lines per axis, cells, the smallest cell
and the features that set it, the Courant Δt estimate, steps, memory, every line merge, and the refusal
a run would stop with when the grid would not fit. The air box's `enlargements` list each absorbing
face's PML — the grid grows OUTWARD by that much. An unavailable row says why and is never printed as 0. It starts no process; the gate holds that
with a counter on `Em3dProcessLauncher`. In `--json` the rows are `explain.em3d`, lengths in base SI
with `lengthUnit`/`lengthScale`.

**A `.c3d` also reports whether each setup is solved (`brief-em3d-98` R-em3d98-8).** A **Solved** section, one line per
(setup, solver leg), in the words the editor's setup cards use — the state, whether the run was partial, when it finished,
how long it took, and what changed since:

```
Solved
  'Driven'     FEM (Palace)    Solved 14:32 (18 min)
  'Both'       FEM (Palace)    Solved 14:32 (18 min)
  'Both'       FDTD (openEMS)  Cancelled 14:51: no complete result
  'Lid modes'  FEM (Palace)    Out of date: 'Board.clay' has changed
  'Heat'       thermal         Not run
```

and in `--json` as `explain.solved`:

```
"solved": [ { "setup": "Driven", "solver": "fem", "state": "current", "partial": false, "running": false,
              "endedAs": "complete", "solved": "2026-10-02T14:32:05+01:00", "tookSeconds": 1080 },
            { "setup": "Both", "solver": "fdtd", "state": "notRun", "partial": true, "running": false,
              "endedAs": "cancelled", "solved": "2026-10-02T14:51:40+01:00", "tookSeconds": 95.3 },
            { "setup": "Lid modes", "solver": "fem", "state": "outOfDate", "partial": false, "running": false,
              "endedAs": "complete", "solved": "2026-10-01T09:12:00+01:00", "tookSeconds": 420,
              "staleWhat": "'Board.clay'" } ]
```

`state` is `notRun`, `current` or `outOfDate`; `partial` (cancelled, not converged, failed or interrupted) is independent
of it, because a not-converged result can still be the model's. `tookSeconds` is absent for a run kept before brief 98,
which recorded no duration. **It holds no rule of its own**: every row is `C3dSolveStatus.Of` (src/Design), the function the
editor's glyphs, the tree and the confirmation before a re-run read, against the results root `em` writes (`ResultsRoot`).
It replaced the per-setup `result of setup '…'` walk that brief 87 added; the `inputs` walk stays. `check` does not carry
it (D1): `check` reports soundness, and a result being out of date is not a defect of the design.

**R-aut4-8: `explain` never guesses and never falls back silently.** Where resolution fails, that is
the answer — a diagnostic naming what was looked for and where it was looked, because a caller uses
this verb precisely when something did not resolve.

**R-aut4-9: sweep units are reported with their scale.** `--analysis` prints a sweep's resolved
start, stop and step in **base SI**, with the unit it was stated in AND the scale that got it there.
Reading a mark without its scale has already produced a run at 2 Hz that looked entirely normal.

### 10.5 `--json`

Per §3.2, with `diagnostics` carrying the findings and `result` carrying the report:

```
"result": { "check":   { "root","severity","documentsChecked","errors","warnings","notes",
                         "documents":[{"path","kind","errors","warnings"}] } }

"result": { "explain": { "path","kind",
                         "walks":[{"step","from","resolved","how"}],
                         "analyses":[{"name","kind","enabled","runnable","isRoot","chain",
                                      "dispatched","promotedFrom","sweep"}],
                         "expression":{"expression","kind","text","real","complex","boolean"},
                         "reference":{"ref","from","resolvedPath","state","outsideWorkspace","redirect"},

                         "cells":[{"name","folder","outsideWorkspace","generated",
                                   "views":[{"type","primary","state","candidates","defect"}]}],

                         "layers":{"technology","resolvedFrom","resolvedBy","truncated",
                                   "layers":[{"name","number","datatype","purpose","visible",
                                              "selectable","color","fill","shapes","instancesUsing"}]},

                         "extents":{"x0","y0","x1","y1","width","height","window","unit","scale",
                                    "empty","note",
                                    "perLayer":[{"name","x0","y0","x1","y1","window"}]} } }
```

Exactly one of `analyses` / `expression` / `reference` / `cells` / `layers` / `extents` is ever
present — the six are refused together. `walks` is always present, because "which workspace, which
technology" is the context every other answer is read against; `--layers` adds a `layers` STEP to it
carrying the technology and the defined/used counts, so the block below needs no heading of its own.
Coordinates and shape counts are **absent rather than zero** wherever there is nothing to measure or
nothing to count — a zero there would be a claim.

`read` uses the same two halves the run verbs do — `result.groups` for a result file it loaded back —
plus one field of its own for a document returned verbatim:

```
"result": { "document": { "path","kind","text" } }
```

`outputs` is empty for both — neither verb writes a file, and a caller looking for one must not find
one invented.

---

## 10A. The netlist contract: what the reader refuses

**AUT-8.** `check` and `explain` can only be as honest as the reader underneath them, and the reader
used to accept a line and discard whatever it did not recognise. That combination — accept, discard,
report clean — is what made the surface manufacture confident wrong answers about the product: an
out-of-process client wrote up a working component as defective, with a minimal reproduction case,
because nothing in the surface was willing to say which of the two participants was wrong
(`brief-automation-7-mcp-hardening.md` §3).

**The rule is that silence is the defect.** Three things follow from it.

### 10A.1 The analysis directive has a schema, and it is a registry rather than a document

`src/Core/Netlist/AnalysisDirectiveSchema.cs` states every `type=` token and, for each, every legal
key with whether the directive is incomplete without it. `CnlReader` validates against it before any
`TryParse*Directive` sees the line, so:

- an unknown `type=` is refused **with the legal tokens listed** — it used to fall through to a
  `RawDirective` and surface, much later, as *"The document declares no analysis"*, which named
  neither the token nor the problem and cost one exercise eight guesses;
- an unknown key is refused **by name, with the legal keys for that type**;
- **every** missing required key is reported at once, because a caller that must re-run to discover
  the second one pays the full cost of a run for each.

The schema is authoritative about a key's NAME and whether it is required. It is deliberately **not**
authoritative about defaults: the reader's own `GetValueOrDefault` calls still apply those, and a
second copy here would be a second place to change. `NetlistContractTests` holds the two halves in
step — every key `CnlWriter` emits must be one the schema declares (or the application could not read
its own output), and every token the schema declares must produce a typed analysis (or it is a
promise the reader does not keep).

**Aliases are derived, not tabulated.** The schematic serialises the same concepts as
`LpLoadTunerName`, `LpToneExpr`, `LppOutputGridPath` and about twenty more; the `.cnl` spelling of
each is that name with an `Lp`/`Lpp`/`Psa` prefix and an `Expr`/`Name`/`Path` suffix removed. Writing
the rule rather than sixty pairs is what keeps it true after the next key is added — and a key that
would itself be changed by the rule (and so could shadow another) fails a test rather than colliding.

**The tuning directives are the exception that is not refused** (TO-1). `tune`, `preset`, `goal` and
`optimize` have their own table (`AnalysisDirectiveSchema.TuningDirectives`); a key it does not list is a
WARNING naming the key, and the key is kept and written back, because a later version's key — a yield
tolerance on a `tune` line — must survive a round trip through this one. A malformed line is still
refused. `check` then applies `TuningValidator`'s rules (`tuning-optimization.md` §6), reported as
`check.tuning.*`.

### 10A.2 A net count is not a port count

`src/Core/Netlist/InstanceNetContract.cs` states how many nets each primitive's instance line binds.
It is a separate statement from `ComponentModel.PortCount` because **"port" means three different
things in this codebase and none of them is "net"**: a resistor's two ports are its two terminals; a
FET's two ports are (gate,source) and (drain,source), which is three nets; an ideal S-block's ports
take a signal net and a reference net each; a `Tuner` declares one port and takes two nets.

Reading a net count off `PortCount` is how the generated catalogue came to describe `Tuner` as a
one-net part — and a client that wrote it that way got a circuit whose bias tee delivered nothing,
`status: ok`, and Pout at the engine's floor sentinel at all 56 drive points.

The check runs in `Elaborator`, **after** the model is constructed (so a parameterised part answers
from its own resolved parameters) and **before** any node minting or family expansion (so the array
still holds exactly what the line wrote). The ordering matters more than it looks: every family
expansion in that method is guarded by an exact length — `resolvedNodes.Length == 3` and friends — so
a short line did not fail there, it *skipped* the expansion and built a wired-wrong circuit that
simulated to completion.

A model that returns null states no count, and each null is a named exception with a reason: `SnP`
takes N nets or N+1 and `CnlReader.ValidateSnpNets` already says so better; `ExtDevice`'s count is
the provider's external pin count, which is neither fixed per type nor equal to `PortCount`, and
`BuildExternalDeviceNodes` already refuses a mismatch with more detail than a number could carry.
There is no third category: a test walks `ComponentModelFactory`'s registry and fails on a type that
is neither counted nor named.

### 10A.3 One reading of a boolean, and one of an inline unit

`BooleanParameter` is the single reading of a yes/no parameter. There were three, each silent about
what it did not recognise, and two of them disagreed: `BiasTee` accepted the literal `on`, `IsTrue`
accepted the literal `true`, and the Verilog-A op-vars flag recognised four spellings of FALSE and
read everything else — a typo included — as true. **Widening without refusing would only move the
silent boundary**, so both halves ship together: the ordinary spellings all work, and anything else
is refused by name with the list.

`Units.LiftInlineUnit` is the single reading of a unit written inline in an assignment. The `.cnl`
reader lifted only the scaling units and the schematic's VAR path lifted those plus the identity ones,
so `VDS = 48 V` meant a variable in a schematic and a parse error in a netlist while the reference
page documented one rule for both. The rule is now the wider one, and the parse verification the wide
unit table needs — split only when the split turns text the parser rejects into text it accepts — is
what keeps `x = 2 * f` a multiplication rather than two femtoseconds.

---

## 11. `serve` — the protocol adapter

`brief-automation-5-protocol-adapter.md`. A **stdio protocol server** that advertises circuitRF's
capabilities to an external client and invokes them on request. The concrete target is the Model
Context Protocol — a JSON-RPC convention in which a server advertises tools over stdin/stdout and a
client discovers and calls them — but the protocol is the first adapter, not the architecture
(`automation-architecture.md` R-aut-13).

```
circuitrf serve --root <dir> [--kits <dir>]
```

**This is the disposable layer, and it is written to be deleted.** Everything durable was built in
AUT-1 through AUT-4; `src/Cli/Serve/` is five files that translate, dispatch and report, and
removing them takes nothing with it.

### 11.1 It calls the verb — it does not re-implement it

**R-aut-1, and it is the whole design.** A tool call becomes an argument vector and is handed to
`CliEntry.Run`, which is the same function `Program.cs` hands the real command line to. So R-aut-13
— nothing reachable here that is not reachable from the command line, and vice versa — is a
property of the code rather than a rule to remember, and the parity gate compares two documents that
came out of one function.

That is why `Program.cs` is now three lines and `CliEntry.cs` holds the dispatch: a local function of
a top-level program is private to `<Main>$` and callable by nobody. Nothing about the verbs changed.

**The adapter refuses only what it alone can see** — a tool that does not exist, an argument that
belongs to another mode of the same tool, an argument of the wrong JSON type, and a path outside the
root. Everything else is the verb's own refusal, arriving unchanged: `--grid` handed to a pursuit, an
unstated Excellon coordinate format, a source folder holding several parts.

### 11.2 stdout is the protocol, and nothing else may reach it

§3.1's exemption. The real stdout is taken at startup and held for the framing alone; `Console.Out`
is replaced with a sink before any capability runs, and each verb's document is written to a string
through `JsonRun.Sink`. `--json` on `serve` itself is **refused** rather than silently one-or-the-
other: it would have captured the stream the framing needs, and every tool call already returns a
document.

Nothing this program launches can leak there either — every child process it starts (the device
worker, the PCell host, the interpreter probe, the updater) redirects its own stdout to a pipe, which
was checked rather than assumed.

### 11.3 The tool surface

**Seventeen tools, and the count is the point** (R-aut-9). A client that discovers tools up front
carries every description for the whole session whether or not it calls one, so the surface is a
standing cost paid on every interaction. Sixteen come out of `ToolCatalog`'s one table; the
seventeenth, `batch`, is advertised beside them by `HistoryBatch` because it is the only one that is
not a command line.

**`import` and `convert` are two tools because they are two verbs** (2026-10-06). They were one,
`import` with a `what` selector, and that filed every EXPORT — a `.clay` to GDSII, Gerber, DXF,
STEP — under a tool whose name and description say artwork comes IN; the agent docs ended up telling
an agent to export "with `import`". Saving a tool is worth nothing if the capability it holds cannot
be found by its name.

| Tool | Becomes |
|---|---|
| `run` | `sparam` / `dc` / `hb` / `lp` / `lpp` / `em` / `opt` (`analysis=optimize`), selected by an argument — one tool, not seven. `em` takes a `.cem` or a `.c3d`, whose embedded setup `setup` names — every shipped eigenmode example embeds two |
| `check` | `check` |
| `explain` | `explain`, including RND-3's `--cells` / `--layers` / `--extents`, `--footprints` and `--tunables` |
| `create` | `new workspace` / `new cell` |
| `import` | `import part` |
| `convert` | `convert` — one import and one export between any two formats, so an export is a `convert` |
| `render` | `render` — **one tool over every document kind**, as the verb is (R-rnd0-4/R-rnd5-2). The kind comes from the path, so there is no selector; making the view type one would advertise three modes where there is one verb |
| `netlist` | `netlist` — the extraction Simulate performs, as a document |
| `plot` | `plot` — one picture out of a result file, with no `.cdd` to author first |
| `find` | `find` — what is here: workspaces, cells, views, analyses |
| `lvs` | `lvs` — one tool over every document kind, as the verb is. **The capability an out-of-process author needs most**: an agent that wrote a `.clay` cannot look at the screen |
| `impedance` | `impedance` — a drawn layout's traces, or (`tech`) the line calculator with nothing drawn |
| `read` | `read` |
| `history` | `history checkpoint` / `list` / `restore` (RC-5, `revision-control.md` §5.3d) |
| `solver` | `solver list` only — which 3D solvers are installed and what each build can do (§22). `install` and `remove` stay off: both download or delete behind a consent that is the person's to give |
| `reference` | `reference` — the same bytes the resources below serve, for a client that does not surface resources to the model |
| `batch` | none. Session state this process holds; see §11.6 |

`ToolCatalog` is one table, and **the JSON schema is generated from the same rows that build the
command line**. A description that says an argument exists and a translation that drops it cannot
happen, because there is one list; adding a flag is one row. Every argument is named after the CLI
flag it becomes.

**Two option kinds exist for `render` alone and both are about confinement.** `PathRepeat` is a
repeated flag whose items are files (`--data a.npy --data b.npy`), which no existing kind was.
`PathOrName` is `--theme`, which is genuinely two things: a theme NAME resolved through a chain of
directories the server root has nothing to do with, or a `.ccolor` file the caller points at.
Confining the first would turn `dark` into `<root>/dark`, which resolves to no theme at all; not
confining the second would let a client name a file outside the root and learn from the refusal
whether it exists. The split — an extension, a separator or a root means a path — is
`Render.ResolveTheme`'s own, one step earlier, and each end names the other.

A third, `StrMap`, arrived with `layerColors` (R-aut12-1) and is about SHAPE rather than confinement:
a JSON **object** of layer name to colour, emitted as the flag repeated once per entry. An object
because that is what the thing is, and a client made to assemble the `=` itself will eventually
assemble it wrong; repeated rather than comma-joined because the KEY is a layer name a technology
author chose, and one containing a comma would split into two names that resolve to nothing. The verb
takes both spellings, so a person at a shell still writes one quoted list.

`--only` and `--group` are reachable as tool arguments (R-aut5-6) — reading is the expensive
direction, and a client that receives eight full loadpull cubes when it wanted one number is the
failure mode this whole series is about.

**`--kits` is the operator's, not the client's.** It is one of the flags taken before dispatch, so
`circuitrf serve --root <dir> --kits <dir>` registers the resolver for the whole server and every
`run` resolves an externally-supplied device model with it. It is deliberately not a tool argument: a
kit folder is installed software rather than design data, it lives outside the root by nature, and
letting a client name one would be the server pointing at an arbitrary directory on its say-so.
**`--trust-kit` is the operator's for the same reason, and more so** (§23): `serve --trust-kit <dir>`
holds for the life of the server, and no tool advertises it — a client granting itself permission to
run a kit's scripts is the one thing that permission exists to prevent. What a client CAN do is let
the server ask its USER, through MCP elicitation — §23.3.

**Both of `reference`'s arguments are OPTIONAL positionals**, which no other tool has. Its
no-argument form is the topic LIST, which is a real answer rather than a usage error — so `topic` is
absent from the schema's `required`. Because argv is positional, an argument given with an earlier
one missing is REFUSED rather than promoted into the empty slot: `{"type": "MLIN"}` with no topic
would otherwise have asked for a topic called `MLIN`, which is a different question answered in
silence.

### 11.3b The one argument that is NOT a flag: `render`'s image attachment

**R-rnd5-4.** `attachImage` is advertised on `render` and becomes no command line at all. It is the
single deliberate exception to "every argument is named after the CLI flag it becomes", and it is
carried in its own list (`ToolSpec.Adapter`) rather than as a flavour of `ToolOption`, so the
invariant stays checkable by reading: everything in `Modes` becomes argv, and the only things that do
not are there.

**Why it has no CLI spelling and must not get one.** A command line writes the file and the person
opens it. A protocol client may have no way to read the path it was handed — and an agent that cannot
*see* the picture it asked for has gained nothing over `--json`. So it is a property of the ENVELOPE,
not of the render.

Three constraints, and they matter more than the feature:

- **The same bytes the verb wrote, read back — never a second render.** §11.1 applies to drawing most
  of all: the adapter attaches the file `outputs` names and decides no viewport, no page and no
  format. `ServeProtocolAdapterTests` decodes the attachment and compares it to the file on disk.
- **Opt-in, and capped at `ToolCatalog.AttachmentCapBytes` (4 MiB).** The number is derived from
  §13.4's measured table, not from taste: a real six-layer board is 1.4 MB as a PNG and 23.5 MB as an
  undecimated SVG, so the cap admits every raster this verb plausibly produces and refuses exactly the
  case where the caller should have narrowed. Base64 adds a third on top, which is why the cap is on
  the file rather than on the frame. **Over it, the answer is the path plus a diagnostic naming the
  size and what would narrow it** — never truncated and never dropped in silence, because a client
  that asked for a picture and got nothing with no explanation simply asks again. **The schema says
  so too** (AUT-10 R-aut10-5): stating the cap's behaviour only in the result leaves a caller that
  asked for bytes and got a path working out why from a note it may not have read.
- **Each format is attached as itself or not at all.** A `.png`/`.svg` is `image` content; a `.pdf` is
  an embedded `resource` with a `blob`, because a PDF is not an image. It is never transcoded to make
  it attachable — that would be the adapter making a rendering decision.

**The document is untouched, always.** Everything the attachment produces is an ADDITIONAL content
block, so block 0 stays byte-identical to what `circuitrf render --json` writes and §11.7's parity gate
keeps meaning what it means. The cap's diagnostic rides as its own text block rather than as a
diagnostic inside a document the CLI would not have written it into.

**Nothing here becomes a resource** (R-rnd5-5). §11.3a's rule is that a resource is right for a
standing catalogue whose bytes do not change. A rendered image is per-call and ephemeral, and
advertising one would mean advertising a URI whose content depends on arguments the URI does not
carry.

### 11.3c The server's `instructions` carry one worked example

**AUT-10 R-aut10-5.** `initialize` already said the three things a client cannot learn from a tool
schema — the formats are the interface, every path resolves under the root, nothing deletes. It now
SHOWS them once as well: create a workspace, write a six-line `.cnl`, `check`, `run`, `read`, in
about fifteen lines, with the three sentences a client most often needs after that (nets come before
the first `Key=value`; `nets` is not the symbol's pin count; `render` does not take a `.cnl`).

Most of what the exercise behind this series learned by trial and error is in that sequence, and a
worked example is the one form of documentation a client does not have to know to go and ask for. It
is ~1 kB per session against `tools/list`'s 20 kB, which is the proportion that makes it worth the
standing cost. **The example is a real one** — it was written out and run before it was written down.

**A second, shorter paragraph names the 3D EM half** (2026-10-07). Nothing else in the instructions
mentioned a `.cem`, so an agent could learn that circuitRF solves cavity and package resonances only by
spotting `Eigenmode` among `Problem3D`'s values in a 54 kB format topic. It says `run analysis=em` takes
a `.cem` or a `.c3d`, lists what each `Problem3D` returns, names the two format topics, and points at
`solver` for whether this machine's Palace can do it — about 600 bytes.

**A third names the optimizer** (brief-tuneopt-11 R-to11-6): five steps — `explain --tunables`, write
`tune` (one complex example, `tune mag(ZL) …`), `goal` and `optimize` lines, `check`, `run
analysis=optimize`, `read` the `.npy` — and the three topics that hold the grammar. About 650 bytes.

### 11.3a Resources — the cheaper channel for the same bytes

The server also declares MCP **resources**, one per reference topic — the generated ones
included, so `circuitrf://reference/analyses`, `.../data-display`, `.../technology`, `.../layout`,
`.../em-setup` and `.../wbond` are advertised
beside the authored pages — at `circuitrf://reference/<topic>`. This is the correct channel for the reference surface: a resource
costs a URI, a title and a size until it is read, where a tool description is a standing per-session
cost. Each entry advertises its `size` in bytes for the reason the CLI's own topic list prints one —
a list that hides the cost makes the cheap topics and the expensive ones look alike.

**Both channels return the same bytes**, because both translate to `reference <topic> --json` and
hand back what came out of `CliEntry.Run`. The `mimeType` is `application/json` rather than
`text/markdown` for exactly that reason: the payload is the verb's own document with the page inside
it, not a second encoding of the page. `ServeProtocolAdapterTests` compares a resource read against
the CLI's document byte for byte, which is R-aut-13 applied to the resource half.

`circuitrf read` is deliberately NOT the vehicle. Its `path` is confined to `--root` (§11.5) and a
reference topic is not a file in the client's tree; overloading it would put a non-path through a
path-confinement check, which is the kind of exception that makes a security boundary stop meaning
one thing.

### 11.4 `read` — the verb the tool table needed

`read` is the inverse of a run verb: a `.npy` through `DataSetImporter`, a Touchstone through
`TouchstoneIO` plus `DataSetBuilder.FromSnp` — **the same pair the GUI's own source library reads a
file with** — and one of circuitRF's own documents returned as its own bytes, because the formats
ARE the interface (`automation-architecture.md` §4) and a round trip through a reader and a writer
would hand back something that differs from disk wherever the reader is lossy.

It was added because R-aut-13 required it: the tool table has a `read` in it, and a tool with no verb
behind it is exactly the privileged adapter that rule forbids. It writes nothing, for `check`'s
reason (§10.1). A directory is refused rather than walked — what a workspace holds is what `check`
and `explain` answer — and an interchange file is refused NAMING `convert`, since half those formats
are binary and handing back a GDSII stream as a JSON string would be an encoding decision this verb
has no business making.

**A `.wasm` assembly-rule module is refused for the same reason** (AUT-10 R-aut10-5). It is one of
circuitRF's OWN document kinds, so it fell through to the text path and came back as whatever its
bytes decoded to, with nothing saying so — a plausible-looking string, which is the failure class
this surface exists to remove.

**Its declared path kinds are audited against what it accepts.** The schema omitted `.cdd`, which
this verb has always taken; an out-of-process client found that by trying it. A schema that
under-promises costs a caller exactly what one that over-promises does, because a client believes it
either way, and `ServeProtocolAdapterTests` is where that stays true.

### 11.5 What it refuses

**R-aut5-8. The server runs with the invoking user's authority, and it constrains itself in one
place.**

- **A root is required at startup**, and every path a client names resolves under it — a relative one
  against the root, since the client cannot see the server's working directory. A path that escapes
  is a **refusal naming the root, never a silent clamp**: clamping runs a different operation than
  the one asked for and says nothing about it. Symlinks are resolved on both sides and at **every
  level of the path**, not just its last component — `ResolveLinkTarget` answers about the item it is
  called on, so asking it about `<root>/link/file` reports "not a link" and the escape goes straight
  through.
- **No shell and no arbitrary process launch.** The device-worker and PCell paths still start their
  own; nothing new becomes launchable because a client asked.
- **Destructive operations are refusals, not confirmations** — and by omission rather than by a
  filter: there is no tool that deletes, and no capability below writes outside the paths it chooses
  itself. There is no user at the other end to confirm with. A client that wants a file gone deletes
  it itself.

### 11.5a `--print-config` — the configuration, written by the thing it configures

`serve --root <dir> --print-config` starts nothing. It prints the `mcpServers` JSON that starts
*this* server and exits. The command is `Environment.ProcessPath`, plus the entry assembly as the
first argument when that is the `dotnet` host, because the executable's full path is the part people
get wrong by hand and a client's `PATH` is often not their shell's. The root and every `--kits`
folder are written absolute (a client starts the server from a working directory of its own), and
the root goes through `PathRoot.Open` first, so a configuration naming a missing root is refused
rather than printed. stdout carries the JSON alone, so it redirects cleanly into a file, and the
Claude Code `claude mcp add` line goes to stderr. `--json` beside it is refused
(`serve.args.print-config-json`), because what it prints is already JSON. The gate launches the
printed command exactly as a client would, from an unrelated directory, and requires it to answer
`initialize`.

### 11.6 Progress, cancellation, and one call at a time

Capability calls are **serialized** — the verbs use process-wide state (`JsonRun`, `Console.Out`), so
two cannot be in flight together — but **the reader loop never blocks on one**. That split is the
whole reason it exists: `notifications/cancelled` and `ping` have to be answerable while a run is
going, and a run that cannot be cancelled is one a client times out on and retries, doubling the cost
of the run it gave up on.

Cancellation and progress both go through **the same `RunControl` the `em` verb already uses**
(`RunHost`), so cancellation lands at a work boundary and progress counts leaf units exactly as that
type's contract describes. A client that sends a progress token is sent `notifications/progress`; one
that does not is not sent notifications it never asked for. A cancelled run answers with **130**, the
code §7 already gives a run stopped at a work boundary, and it writes nothing — a cancelled run
abandons its result rather than publishing a partial one.

With no host installed, `RunHost.Control` is null and every engine takes the same optional argument
it always took, so a command line behaves exactly as it did.

### 11.7 The gate

`tests/Ui.Tests/ServeProtocolAdapterTests.cs`. For every tool, the document that comes back through
the server is compared **byte for byte** against the one `circuitrf <verb> --json` writes. Two things
are normalized and nothing else: the adapter RESOLVES paths, so the CLI side is given the resolved
path; and a verb that CREATES something cannot create it twice, so those two calls are given
different destinations and the destination is substituted out. The stdout audit, the three
root-escapes, the lifecycle and the cancellation are in the same file.

### 11.8 A server outlives an update — or says so and leaves

`serve` can run for hours out of an installation the GUI then updates (AUT-13 R-aut13-4). **Measured on
macOS, not assumed:** a `serve` whose single-file bundle was exchanged under it (`renamex_np`
`RENAME_SWAP`, which is what the updater does) went on answering `check` — every assembly it needs was
already loaded — and failed `run` with *Could not load file or assembly 'NumFlat'* and `history` with
*… 'System.Diagnostics.Process'*. The runtime reads a not-yet-loaded assembly out of its executable BY
PATH, and after the exchange that path is a different file (the same finding `src/Ui/RESOLVED.md`
records for the GUI's own update hand-over). The same family reaches Linux if an `app-<ver>` directory
a server runs from is reclaimed; Windows cannot delete a running executable's folder.

A server that answers some calls and fails others with a missing-file message is worse than a dead
one: a client concludes the TOOL is broken — AUT-7 §3's false defect report, by another road. So
`InstallationGuard` records the executable's size and write time at start, and before every
`tools/call` and `resources/read` asks whether it still matches. If not, that call gets a JSON-RPC
error **−32001** naming the reason, the sentence goes to stderr, and the server exits 1. The client's
next launch of the same command runs the new version, because the path it launches is exactly the one
the update replaced. Everything the guard runs is CoreLib, and `Changed` is called once at construction
so it is prepared while its whole closure is certainly loaded. Under `dotnet CircuitRF.Cli.dll` the
guarded file is `dotnet` itself, which never changes, so the development server never trips it.

Gate: `InstalledCliTests.Serve_AnswersAndLeaves_WhenItsInstallationChangesUnderIt` (replaced and
removed). The exchange itself was measured by hand, with a structurally different second bundle — two
builds whose assemblies sit at the same offsets make the swap invisible, which is how the first run
of that measurement came back clean and wrong.

## 12. `reference` — what a caller may write, before it writes it

`check` tells a caller that what it wrote is wrong. `explain` tells it what circuitRF made of what it
wrote. Neither tells it what it is **allowed** to write — the primitive type names, how many nets each
takes, what its parameters are called, what a unit suffix means, where a `define … end` block goes.
A client that cannot spell `MLIN` is blocked before `check` can help it.

**And the failure is quiet.** A netlist naming a type that does not exist fails at elaboration with a
sentence about an unresolved name; a component given a plausible-but-wrong parameter name resolves to
that parameter's default and simulates, producing a converged, complete-looking, wrong answer. That
second one is the same class of failure `explain --analysis` was built for.

```
circuitrf reference                     # the topic list, with each topic's size in bytes
circuitrf reference netlist             # one topic, as its own text
circuitrf reference components          # the catalogue
circuitrf reference components MLIN     # one primitive
circuitrf reference analyses            # every analysis directive and every key it takes
circuitrf reference analyses sparam     # one directive
circuitrf reference data-display        # the .cdd format, generated from the reader's own type
circuitrf reference technology          # the .ctech format, likewise
circuitrf reference layout              # the .clay format
circuitrf reference em-setup            # the .cem format
circuitrf reference wbond               # the .wBond format
```

It takes no path, reads no file and writes nothing — the only verb here about no document at all,
which is also why it is not a mode of `explain`: every `explain` answer is anchored to a path.

### 12.1 Two halves, and they are different in kind

**The prose topics are AUTHORED.** They are the pages under `docs/user/src/reference/`, embedded in
`CircuitRF.Design` as plain .NET `EmbeddedResource` items and read through
`Assembly.GetManifestResourceStream` — never Avalonia's `AssetLoader`, which throws with no live
platform. `ShippedTechnologies` is the precedent and its header names the trap this follows: **the
`<EmbeddedResource>` item and the class ship in the same commit**, because a class shipped without its
resources compiles, enumerates nothing and reports nothing.

The `.csproj` references the authored files **in place** rather than copying them into the project — a
copy is a file that will be edited on one side only and nothing will report it — so the embedded bytes
are the authored bytes, and `ReferenceCliVerbTests` compares them. The YAML front matter and the docs
factory's `{{…}}` placeholders are stripped **at read**, not at build, which is what keeps that
comparison possible.

**The component catalogue is GENERATED**, at every call, from `ComponentModelFactory` (the `.cnl`
tokens), `ComponentTypeRegistry` (parameters, defaults, units, visibility, meanings, category, search
terms) and `SymbolPortDefs` (the terminals, in the order a netlist line writes their nets). It
transcribes nothing. `DocTables` renders the documentation tables *from the same `ComponentCatalog`*,
because the page and the machine answer must be one computation or they will disagree the first time
one is changed.

**There is no third thing.** No grammar, no schema, no BNF: a hand-written grammar in the adapter
would be a second description of `CnlReader` that drifts from it silently, which is the exact failure
this whole series exists to prevent.

**Three more topics are generated the same way** (AUT-10). `analyses` is read from
`AnalysisDirectiveSchema` — the table `CnlReader` itself validates against — so every `type=` token,
every alias, every key, its default and whether it is required come from the thing that enforces
them. `data-display`, `technology`, `layout`, `em-setup` and `wbond` are read by REFLECTION from
`DataDisplayConfig`, `CtechFile`, `ClayFile`, `CemFile` and `WBondIo.WBondDocument`, the types their
readers deserialise into, with each field's default taken off a freshly-constructed instance, and a
polymorphic list (a layout's shapes) expanded into every kind with the `$type` value that selects it.
Each carries an authored preamble — what the format is for, and a minimal
example that was written out and run — and everything after the preamble is generated.

**What the generated half deliberately will not say is what a field MEANS.** A `.ctech` type carries
no per-field summary the walk could read, and an invented meaning is worse than none — R-aut6-8's
rule, applied to a format instead of to a parameter. The prose chapters (`stackup.html`,
`data-display.html`) answer that half.

### 12.2 The topic set is curated

Reading is the expensive direction (`automation-architecture.md` R-aut-10) and a client pays for every
byte, so what ships is the authoring critical path: `netlist`, `expressions`, `units`,
`measurements`, `pins-ports-terms`, `sdd`, `file-formats`, and `component-notes`. **`cli.md` is
excluded deliberately** — at 54 kB it is the largest page of them all, and a protocol client already
has every verb's schema from `tools/list`, so it is the one page it needs least.

The generated topics follow: `data-display`, `technology`, `layout`, `em-setup`, `wbond`, `analyses`,
`tuning`, `goals`, `optimizers`, `components`. **`tuning` and `goals`** (TO-1) print the four tuning directives from
`AnalysisDirectiveSchema.TuningDirectives` — the table the reader checks their keys against — each with a
worked example that is a complete netlist `check` passes as it stands. **`optimizers`** (TO-7) prints the
algorithm registry (`OptimizerAlgorithms`) — the same entries the optimizer reads its option defaults
from — with an id not built yet marked "not in this build". The five formats are here because they are documents a client must WRITE and that
`create` does not make (or makes only empty) — `layout`, `em-setup` and `wbond` are the authoring
surface for EM and wirebond runs, and the one `docs/design/em-3d.md` §4.6 extends to 3D — the
exercise behind this series got a plot only because an unrelated `.cdd` happened to be on the machine
to copy from.

The topic list carries each topic's size **as served**, in bytes, so a client choosing between a
4.4 kB page and an 84 kB one can choose.

**`components` is the catalogue; the prose page about components is `component-notes`.** The two
answer different questions — the catalogue says what may be WRITTEN, the page says what it MEANS —
and they cannot both hold the same name. The machine answer keeps the plain one.

### 12.3 A symbol's pin count is not a netlist line's net count, and the catalogue says both

**This was the origin of the series' worst finding** (AUT-7 §3, AUT-10 R-aut10-2). The catalogue
published the SYMBOL's pin count under the heading `nets`. They differ wherever a terminal is
implicit on the glyph — a `Tuner` draws one pin and its instance line binds two, and so do `Port`,
`Term`, `Vdc` and `IProbe` — and they differ for an `SDD` by construction. A client wrote
`Tuner:T1 n1 Z[1]=50 BiasTee=on Vbias=48` from the catalogue's own description, got a bench whose
bias tee delivered nothing with `status: ok` and Pout at the engine's floor sentinel at every drive
point, and reported the component as broken. The model was correct.

Each entry now carries **two** facts under two headings:

```
Tuner
  nets: 2                     <- what the .cnl instance line binds
  terminals: 1  1             <- what the symbol draws
```

`nets` comes from `InstanceNetContract` — the same table the elaborator refuses a wrong count with
(§R-aut8-4), so the catalogue and the reader cannot disagree. It is **measured**, not written down:
`ForToken` constructs the type at three port counts and asks `Expected` of each, so three equal
answers is a fixed count and a progression is a rule with its multiplier and intercept read off the
measurement. Four tokens no measurement can reach (`SnP`, `wBond`, `ExtDevice`, `VerilogA`) state a
sentence instead, each because the rule genuinely is not a number.

**The parameter that sets a variadic count is the NETLIST's spelling, which is not always the
symbol's.** The parameter panel calls an SDD's port count `NumPorts`; a `.cnl` line spells it
`SddPortCount`. The catalogue prints the first against `terminals` and the second against `nets`,
which is exactly what is true of it.

The note against a symbol-less type used to end "…and nothing below the UI firewall states how many
nets its instance line takes." **That caveat was the defect, not a disclaimer of it**, and it is
gone: the count is stated for those types like every other, and what such a type is genuinely
missing is the palette's defaults and its pin names.

### 12.4 A port count that is not fixed is reported as not fixed

Several primitives are variadic: an SDD's and a `Z_Port`'s and an `SnP`'s port count follow
`NumPorts`, a Verilog-A model's follows `Pins`, the ideal switch's follows `Throws`, a wBond's follows
the arrays it places. Printing a *default* where the answer is *"it depends, and here is what on"* is
the `sweep-unit-scale-and-mark` failure class — a number that is plausible, specific and wrong, with
nothing reporting it. So those report `determinedBy` and no count, and the terminals they do carry are
labelled with the port count they were listed at.

**Nothing constructs a parameterized model to ask it for a TERMINAL count.** And nothing reads
`ComponentModel.PortCount` anywhere: that is the model's port count in the MNA sense and is not the
number of nets an instance line writes — a current probe reports 1 and takes two nets, a FET reports 2
and takes three, a 2-port SDD reports 2 and takes four. `SymbolPortDefs` is the contract
`NetExtractor` emits nets by, so it is the one that answers the terminal question; `InstanceNetContract`
answers the net one (§12.3), and it DOES construct — from a minimal parameter set that the three-point
probe proves cannot have moved the answer, which is a different thing from reading a count off a model
built out of invented values.

### 12.5 The mismatch is part of the answer

`ComponentModelFactory` keys on the `.cnl` token; `ComponentTypeRegistry` keys on `SymbolKind`; and
`EngineReference` bridges them without being total in either direction. **Five** `EngineReference`
targets have no factory entry (`GND`, `MEAS`, `Pin`, `SpiceModel`, `VAR` — three of them are not
components at all, which is the answer for them) and **seven** factory types no `EngineReference` maps
to (`Chain`, `ExtDevice`, `I_nTone`, `SemiC`, `Short`, `Term`, `V_nTone`). A type in the second list is
placeable in a `.cnl` and has no palette metadata; one in the first is drawable and will not
elaborate. Both are things a client needs told, so the catalogue emits both with a note, never the
intersection.

### 12.6 The gate

`tests/Ui.Tests/ReferenceCliVerbTests.cs` for the verb, the embedded set and the catalogue;
`tests/Ui.Tests/ServeProtocolAdapterTests.cs` for both protocol channels. Every catalogue assertion is
made against the live registry rather than a committed list — a golden of all 68 primitives would pass
forever after somebody froze it.

`tests/Ui.Tests/GeneratedReferenceTests.cs` for AUT-10's three: **every stated net count is the count
the reader binds** — asserted by writing the instance line and elaborating it, one net short and one
net long as well, not by comparing two functions in one file; **every `type=` token and every key the
generated analyses topic lists is one the reader accepts, and vice versa**; and **every topic the index
lists resolves, is non-empty, and is the size the index promised**. The two format topics' examples are
extracted from the served page and parsed, because an example that does not work is the failure the
topic was written to prevent.


## 13. `render` — the one output the command line did not have

`brief-render-2-render-verb.md`. circuitRF could already run, check, explain, convert and author
headlessly. It could not SHOW anything: a client that had just authored a layout had no way to look at
what it made, and neither did the person reading its report.

```
circuitrf render <path> -o <out.svg|.pdf|.png> [options]
```

**One verb over every document kind**, with the kind inferred from the path through
`src/Cli/DocumentKinds.Classify` — the same function `check` and `explain` infer with (§10.2). There is
no `render-schematic`.

### 13.1 It owns no rendering, and that is the whole design

`SchematicRenderer`, `SymbolEditorRenderer` and `LayoutRenderer` are ~7,000 lines of measured, tuned
Skia that already draw every frame the application shows and already produce the SVG and PDF on its
clipboard. RND-1 moved them into `CircuitRF.Render`, below the firewall, precisely so this verb could
CALL them rather than resemble them: a CLI that re-implemented any of it would drift, and the drift
would be invisible, because a picture that is *plausible* is indistinguishable from a picture that is
*right*.

What `src/Cli/Render.cs` contains is argument parsing, viewport arithmetic, refusals and reporting —
which is what `src/Cli/Authoring.cs` already established a CLI verb is allowed to be.

**A layout's labels draw in the stroke font unless a label says Sans** (brief-silkscreen-stroke-font.md). The
stroke font is plain data in `src/Design`, so a stroke label rendered here is the same picture the
application draws, with no typeface to install; only a Sans label depends on the embedded faces
`RenderTypefaceInstaller` loads.

### 13.2 What it takes, and what it refuses to guess

| Input | Resolved by |
|---|---|
| a `.csch`, `.csym` or `.clay` | directly, **including one that belongs to no workspace** |
| a cell folder | `--view`, or the sole view it holds; primacy is `CellFolder.ResolvePrimary`'s answer |
| a workspace | `--cell <name>`, resolved by the same walk `explain --cells` reports |

**An orphan document is a first-class input, not a degraded one.** A `.clay` with no workspace above it
resolves no technology, renders on the fallback palette exactly as the layout editor does with an
unresolved technology, and says so as a NOTE. A caller rendering a bare `.clay` handed to it by a
converter already knows there is no workspace; calling that a warning teaches it to ignore warnings.

Everything a dialog would have ASKED is a refusal naming the flag that answers it (R-rnd0-6): a cell
folder holding three views lists them and names `--view`; a workspace with no `--cell` says so, because
rendering "the workspace" is not a picture of anything; an output extension this verb does not write
lists the three it does; a layer the technology does not define names `explain --layers`, because a
misspelling that was silently skipped is indistinguishable from a layer that is genuinely empty.

**`-o` is required and there is no picture on stdout.** A binary there would break §3.1's contract that
stdout is *the result* in a form a caller can read, and `--json` has to be able to co-exist with the
write. The extension picks the format exactly as `convert` infers one from a path; `--format` overrides.

### 13.3 The viewport, and the unit rule

```
--fit                      the whole document, with --margin (default 0.10 — Zoom to Fit's own)
--window <x0,y0,x1,y1>     an explicit world-space rectangle
--center <x,y> --span <w>  a centre and a width; height follows from the output aspect
```

The three are **refused together rather than ordered** — `explain`'s rule for its own three questions.
A precedence nobody stated is an invention.

**On a layout, every coordinate carries an SI unit and a bare number is a refusal.** `--window
0,0,500,300` could mean DBU, micrometres or millimetres; those are three pictures six orders of
magnitude apart and all three are plausible, and the picture that comes back from the wrong one is a
plausible picture of the wrong thing. This is `sweep-unit-scale-and-mark`'s failure class exactly, so
the refusal prints what it would have accepted and does not guess. A schematic or symbol takes bare
numbers, because its coordinates ARE dimensionless design units — and the `--json` document says
`"unit": "design-units"` rather than leaving a caller to assume metres.

**The requested window is honoured exactly and an aspect mismatch is LETTERBOXED** — never cropped and
never stretched. A caller that asked for a region and silently got less of it than it asked for has no
way to notice. The resolved window, after letterboxing, is in the document.

**What is fitted is the PAINTED box, not the stored one.** A label's stored bbox is its anchor, an EM
port paints a width bar and an arrow beyond it, and an instance's extent resolves through its cell.

That measurement lives in **`CircuitRF.Render.DocumentExtents`**, not in this verb, because
`explain --extents` reports the same box and the two must not be able to disagree (R-rnd3-9). It hands
back TWO: the document's own **zoom-independent** box, which is what the `--json` document reports as
`extents`, and that box **solved for the marks measured in pixels at render time** — a symbol pin's
name, a Fixed-mode ruler's readout — which is what a fit is framed on. On a document carrying neither
they are the same box, which is most of them.

### 13.4 Size, resolution and detail

```
--size <W>x<H>   device pixels for png, points for svg/pdf. Default 1600x1200.
--scale <n>      raster multiplier. png only; a refusal on svg/pdf, which have no pixels to multiply.
--dpi <n>        the same number spelled relative to 96. Refused together with --scale.
--detail full | screen | <pixel budget>
```

| `--detail` | Means |
|---|---|
| `full` (default) | every level-of-detail tier off. What is stored is what is drawn. |
| `screen` | the tiers engage exactly as they would on a canvas at this zoom — what a user sees. |
| `<n>` | the pixel budget; `LayoutRenderDetail`'s octave bucketing still applies and the effective tolerance is reported. |

**The default is `full` and it is a deliberate cost.** Measured on a board matched to
`LayoutRenderDetail`'s own import (3,284 shapes, 764,032 vertices), whole board at 1600x1200: an
undecimated SVG is **23.5 MB in 1.44 s**; the same picture at `--detail screen` is **6.2 MB in 0.36 s**.
The PDF is 9.6 MB against 2.6 MB. A PNG barely moves (1.4 MB against 1.0 MB) because a raster's size is
set by its pixels, not by the geometry behind them. The verb reports the vertex count and the file size
it produced, so a caller finds this out from the answer rather than from a 23 MB file.

`--detail` is layout-only and is refused on a schematic or a symbol, whose renderers key their own LOD
on zoom rather than on this. `PathCache` is null on this path — one-shot render, nothing to persist
across frames — which is what every existing export already passes.

### 13.5 Layers, colour and what is off by construction

```
--layers <a,b,...>       render only these        (layout only; refused on a schematic or symbol)
--hide-layers <a,b,...>  render everything except these
--fit-layers <a,b,...>   frame the fit on these; draw everything
--layer-colors <name=#rrggbb[aa],...>   how a layer draws, for this render only. Repeatable.
--theme <name|path.ccolor>   --variant light|dark   --background opaque|transparent
--grid                   default off      --no-rulers   default: whatever the document says
```

The default is every layer the resolved technology marks visible — `LayerDef.Visible`, which is what the
editor honours, not "all layers regardless". **The selection is applied to a CLONE of the resolved
technology, never to the cached one**: `TechnologyCache` hands back a shared instance and flipping
`Visible` on it would leak into the next render in the same process, which is not hypothetical because
`serve` runs many calls in one. That is the class of defect that only appears on the second call.
**All four of these flags go through that one clone** — `TechnologyLayerSelection.WithLayers`, in
`src/Design` — in a single pass, so a colour predicate reads the technology's own definition rather
than whatever a first pass left behind.

**`--fit-layers` exists because `--fit` frames what is DRAWN, and that is the wrong knob for one real
case.** The framing rule is not new and is now stated: `DocumentExtents.LayoutBox` gates on
`LayerDef.Visible`, which is the same flag `--layers` / `--hide-layers` write on that clone, so hiding a
layer takes it out of the framing as well as out of the picture. What the exercise hit is a Gerber
import whose drill-map fabrication drawing sits far outside the board and, framed with everything else,
shrinks the board to a fraction of the page — and hiding it is a *different picture* from the one that
was wanted. `--fit-layers` narrows the framing without narrowing the drawing. It is refused together
with `--window` and `--center/--span`, which state the frame outright, and refused when the layers it
names draw nothing, because framing on nothing is not a page. The `--json` layer report carries
`framed` per layer when it is in force, and omits it otherwise — "framed on everything drawn" is the
ordinary rule and reporting it per layer would read as a choice somebody made.

**`--layer-colors` is a RENDER-time override and writes nothing.** A Gerber import assigns the six
copper layers near-identical colours, so a copper overlay is unreadable; before this the only route was
to hand-edit the generated `.ctech`, which is changing the design to change a picture of it. The names
resolve through the same map `--layers` resolves through — a layer the technology does not define but
the document draws on is nameable under exactly the generated `L<layer>/<datatype>` name `explain
--layers` prints for it — and an unknown name, or a value that is not a colour, is a refusal rather
than a skip. **An eight-digit colour sets the layer's FILL OPACITY, not the colour's alpha**:
`LayoutRenderer` builds its `SKColor` from R, G and B alone at all four of its call sites and takes the
alpha from `LayerDef.FillOpacity`, so an override that wrote the alpha into the colour and stopped
would parse, report itself as applied, and change nothing in the picture. The layer report carries each
layer's `color` and `fillOpacity` AS DRAWN, which is what makes the override checkable — a colour
change is the one thing a caller receiving only a picture cannot verify from the numbers beside it.

Theme resolution is `ThemeResolver`'s existing chain and nothing new — an explicit `--theme <path>`
first, then workspace directory, user themes directory, shipped `.ccolor`. With no `--theme`, the
workspace's own recorded theme; with no workspace, the shipped default. A theme NAME that resolves to
nothing is a refusal listing what was looked at, because that chain's last step always succeeds and a
misspelling would otherwise produce a differently-coloured picture reported as a success.

**Overlay, handles, marquee, PCell pins, snap glyphs and the EM/DRC overlays are off by construction** —
each defaults off in `LayoutRenderOptions` and this verb never sets one. **Rulers are the exception and
they stay ON**, because a ruler is document content rather than overlay state: it is in the `.clay`, and
an export that dropped it would contradict `layout-view.md` §9B.9.

### 13.6 Progress, `--json`, and the gate

Progress goes to stderr and through `RunHost`'s `RunControl` — the same one `em` uses — so `serve` gets
`notifications/progress` and cancellation with no plumbing in this verb, and a cancelled render exits
**130 and writes nothing**. The bytes are complete before the file is ever opened, which is what makes
that true rather than merely intended. Four stages are reported: *resolve*, *measure*, *draw*, *encode*.
Measured, a whole real board is under a second and a half, so the value here is the cancellation rather
than the bar.

`--json` carries `outputs` with the file written and `result.render` with what was decided — the
viewport (including `letterboxed`), the document's extents, the size, the theme and WHICH step of the
chain resolved it, the layers with whether each was drawn and how many shapes the document has on it (hierarchy included
and arrays multiplied, from the walk `explain --layers` counts with — deliberately **not**
`counters.shapesDrawn`, which counts only the top-level shapes this frame issued a draw call for),
the detail mode with its effective tolerance in DBU, and `counters`. **There is no duration**:
`counters` is `LayoutRenderResult`'s own work count — deterministic and machine-independent by
construction — which is what lets a gate assert about work done rather than about a shared runner's
wall clock. Extents and viewport come back in base SI **with the unit and the scale named**, the rule
`explain --analysis` already follows.

AUT-12's own gates sit beside RND-2's, in the same file and for the same reason: **the colour override
is asserted across three renders in ONE process** — reference, coloured, plain — with the third
required to be byte-identical to the first, because a shared cached `Technology` written in place is
the defect that only appears on the second call; and `--fit-layers` is asserted to produce the viewport
`--hide-layers` produces while drawing what an unfiltered render draws, which is the whole difference
between the two flags and is measurable only where the narrowed frame still contains some of the other
layer's content.

`verticesEmitted` is the one counter this verb added, in that existing style and for that reason: nothing
else could be asserted against `--detail`'s claim, because the same shapes are drawn and the same paths
are built. It counts vertices of stored vertex lists emitted into committed-layer geometry, after
decimation; analytic geometry (a circle, a via annulus) has no vertex list and contributes none.

The gate is `tests/Ui.Tests/Render/RenderCliVerbTests.cs`: the verb run **as a process** writes the same
bytes as the in-process `CircuitRF.Render` call on the same document, theme and viewport — for the
schematic, the symbol and the layout, in SVG and in PDF — and that call is itself gated against the
GUI's own clipboard export by RND-1. Measured, **all four came back byte-identical with no exclusion at
all**: RND-1's `clipPath` id counter is per process and does not differ between the two here, and the
`SKDocumentPdfMetadata` date §5.2 predicted never appeared. Both normalisations are written and applied
only where the raw bytes differ, so an exclusion that stops being needed stops being applied.

### 13.7 A `.cdd` — the same verb, a different anatomy

`brief-render-4-data-display.md`. A data display is the fourth document kind this verb draws, and it is
the one that is not a drawing:

```
circuitrf render <path.cdd> -o <out.svg|.pdf|.png> [--data file]... [--tab name|n] [--plot n] [--all-tabs]
```

**It holds no data.** Its traces name a source — a path, or the sentinel `run.npy` meaning "whatever
this document has SELECTED" — and every curve in the picture is re-resolved from a file on disk each
time it opens. So rendering one is three jobs, not one: resolve the sources to `DataSet`s, resolve each
trace against its cube, then compose and draw. Only the first is this verb's;
`src/Cli/RenderDataDisplay.cs` and `src/Cli/CddSources.cs` are argument parsing, source binding,
refusals and reporting, on §13.1's terms.

**Where the other two live, and why they moved.** `CircuitRF.Render.DataDisplay` — the Data Display's
models and its eight Skia renderers, plus four functions that were view models' until RND-4 and are now
called by BOTH sides: `TraceResolve` (spec → cube → points), `ContourResolve`, `SummaryResolve` and
`PlotConfigLoader` (a saved `PlotContainerConfig` → a live `Plot`), with `PlotComposer`,
`PlotDocumentWriter`, `PlotCanvasGeometry` and `PlotLabelStrips` carrying the page layout out of
`PlotExporter`. See `docs/design/data-display.md` §"Where the resolution lives". A CLI that
re-implemented any of it would produce a plot that is subtly different from the one on screen, which is
the worst possible output of this series, because nobody can see that it is wrong.

**An unresolvable source is a refusal naming `--data`, never an empty plot.** This is the rule the whole
verb is arranged around: an empty plot is a valid picture, it exports cleanly, and it looks exactly like
a measurement that came back empty. So every source the chosen pages reference is resolved BEFORE
anything is drawn. A reference is looked for beside the `.cdd` and then under the nearest ancestor
workspace's `results/`; the sentinel takes the document's own recorded selection, or the first `--data`.
`--data` may be repeated, it overrides a path that does not resolve, and **one that binds nothing is a
refusal too** — a caller that handed over last week's run must not get a picture drawn from whatever
happened to be lying beside the document.

**Selection and pages.** `--tab` takes a name or a 1-based number (a tab literally called "2" wins over
the second tab), `--plot` takes a 1-based number within it, and the default is the tab the document
opens on. **`--all-tabs` writes one page per tab and is PDF's alone** — `SKDocument` is a multi-page
format and SVG and PNG are not, and writing `out-1.svg`, `out-2.svg` from one `-o` is a filename this
tool invented, which §13.2's rule forbids. On those it is a refusal naming `--tab`.

**`--size` replaces the page; everything else about the composition is unchanged.** The default stays
`PlotExporter`'s own 792×612 pt landscape with 36 pt margins, so an unadorned render writes the
byte-identical file the GUI's own **Export** does. The bounding-box fit — plots, axis label strips and
marker info boxes scaled uniformly to fill the usable area and centred on it — is what makes a marker
info box the user dragged land in the file exactly where it sits on screen, and it was kept verbatim;
only the page became a parameter, and the margin scales with it.

**`--theme`/`--variant`/`--background` apply**, and the options that describe a DRAWING do not:
`--window`, `--center`, `--span`, `--fit`, `--layers`, `--hide-layers`, `--detail`, `--view`, `--cell`,
`--grid` and `--no-rulers` are each a refusal naming themselves. A display has no world coordinates and
no layers — its plots carry their own axis windows — and a caller that passed `--window` expecting a
crop would otherwise get a full picture back with no hint that its flag did nothing.

**`--json`** reports `result.render.dataDisplay`: the tab drawn and how many there are, the page count,
the plot count, and **every source with the file it resolved to and whether `--data` or the document
bound it**. That last is the part a caller cannot get from the picture: "the plot is empty" and "the
plot read the wrong run" look identical.

The gate is `tests/Ui.Tests/Render/RenderDataDisplayCliTests.cs`, which authors a display through the
application's own view models, saves it, and compares the verb's output as a PROCESS against
`PlotExporter`'s — for eight trace kinds (a cube slice, an expression, a "plot versus", a derived
metric, an S→Z conversion, a stability circle, a loadpull contour and a summary-table column). The one
normalisation is Skia's SVG element ids, whose counter is per process and in hex; the PDF is compared
with none at all.

---

### 13.8 A 3D EM setup — sections and an outline, with no solver

`brief-em3d-5-render-and-explain.md`. A `.cem` whose `Solver3D` is set is drawn from the problem the
backend would receive — generated in-process by `Em3dGenerator` through the `.cem`'s own two walk-ups,
so no solver, no mesher and no window is involved (`src/Cli/RenderEm3d.cs`, `Em3dSetupSource.cs`):

```
circuitrf render amp.cem -o top.svg  --section z=35um       # the XY plane at that height
circuitrf render amp.cem -o side.svg --section xz@y=1.2mm   # or yz@x=…, vertical cuts
circuitrf render amp.cem -o iso.svg  --iso                  # silhouettes and sharp edges
circuitrf render x.c3d -o iso.svg --iso --setup Palace      # a .c3d through one of its embedded setups
```

**Exactly one view, refused together rather than ordered**, and no view at all is a refusal listing
the three. **Every length carries an SI unit; a bare number is a refusal**, for §13.3's reason. A
planar `.cem` is a refusal NAMING ITS LAYOUT, which is what a picture of it would show. The layout
options (`--window`, `--layers`, `--detail`, `--grid`, …) and the `.cdd` ones are refused by name.

**The interface convention** is stated in the picture's caption: a solid is drawn where bottom ≤
plane < top, and the plane snaps onto any boundary within a part in a billion first — so
`z=<the substrate's top>` shows the copper, every time. **The isometric view has no hidden-line
removal** and its caption says so. A view is framed on the model, not on the air box (which the
generator pads by λ/8 at the lowest frequency); a face outside the frame is labelled with its distance.
Conductors take their drawing layer's colour from the technology; `--json` adds `render.em3d` with the
plane after snapping, in metres, and every object the picture drew. Gate:
`tests/Ui.Tests/Em3d/Em3dRenderExplainTests.cs` — the verb as a process against the in-process render,
section and outline, SVG and PDF. **A `.c3d` that does not build as a whole is still drawn**: what
resolved, in a box at its own extent, with one `render.em3d.partial` warning per thing left out (a
field naming an undefined variable, a wire off its pad), exit 0. A picture is a best attempt at what
works; a run of the same file still refuses.

#### 13.8.1 A field plot, headlessly (`--field`, `--list-fields`)

`brief-em3d-84-field-plots-in-the-cli.md`. A `.c3d` keeps its field plots as records (brief-em3d-83), so
a plot can be drawn with no GPU:

```
circuitrf render cavity.c3d -o cut.png --field Field1           # the plot's own section, the field under it
circuitrf render cavity.c3d -o wave.png --field Field1 --phase 90
circuitrf render cavity.c3d --list-fields                        # every plot, and whether its data is there
circuitrf render Output.c3d -o wires.png --field "DC 14 A — along wire 4" --labels --tight --axes --scale-bar
circuitrf render "Eight Fingers.c3d" -o surface.png --field Surface --iso    # a Surfaces plot, seen from a direction
circuitrf render cavity.c3d -o walls.png --field Surf --view-dir front --region cavity
```

**It owns no resolution, no slicing and no range arithmetic** — §13.1's rule, and the reason the plot
resolution moved below the firewall to `CircuitRF.Render.Scene3D.Fields.FieldPlotResolver`, which the
3D view and the 3D editor now call too. The run directories, the discovery, the solution **by value**, the
R-em3d83-5 sentences, the quantity pick, the legend's lines and the scene origin are that one class's;
the cut and its range are `FieldSection`'s (the view's own slice goes through it); the colours and the
thinning are `Em3dSectionField`'s; every pixel is `Em3dSectionRenderer`'s. The stale comparison is
`C3dRunDocument.Check` in `src/Design`, the editor's banner's — the kept `document.c3d` and, since brief-em3d-87, the
`inputs.json` hashes of every file the run read, which the run service (not the GUI) writes, so `em` keeps them too. The results root is `ResultsRoot.For`, the
function `em` writes by. `src/Cli/RenderEm3dField.cs` is arguments, refusals and the report, and a source
scan holds it to that.

- **The view follows the plot.** A ClipPlane plot IS a section at its axis and DBU offset; a `--section`
  that disagrees, `--iso` or `--view-dir` is a refusal naming both — never a silent re-cut.
- **Missing data is a refusal carrying R-em3d83-5's sentence verbatim** — never the nearest frequency, never
  an outline presented as the field. **A stale run still draws**, with a `note:`.
- **The plot's drive is the record's, with no flag** (`brief-em3d-100`). `DrivePowerW` and `DriveReferredTo`
  are read through `FieldPlotResolver.Drive` → `FieldDrive.Read`, the reading the 3D view's layer takes; its
  factor goes into `FieldSection.Cut` / `FieldSurfacePlot.Em` and its line into `LegendLines`, so the legend
  `render` prints is the window's. Each step is opened at its solution's `DumpScale` (an openEMS dump referred to
  its port's incident wave), and an *Accepted* drive the run has no reflection for is a refusal carrying the
  Inspector's sentence.
- **A Surfaces or Faces plot is a picture of surfaces in depth** (`brief-em3d-89`). What it draws is
  `FieldSurfacePlot`'s (`src/Render`), moved out of the 3D view's view model so the window and `render` build
  the same triangles, nudges and range: All Faces (`FieldSurfaces.Exterior`) and picked faces for a
  temperature, the selected region's boundary or the conductors and picked faces (`FieldFacePainter`) for an
  EM field, with the view's ranges (a temperature's true minimum and maximum, extended to the wires; an EM
  field's percentile). `Em3dSurfaceField` projects it orthographically and removes hidden surfaces with a
  **software depth buffer**, supersampled up to 3 × 3 within an 8 M-sample budget: a painter's sort is wrong for
  interlocking parts, which a bond-wire package always has. The rest of the model is drawn as the 3D view's
  scene draws it (`Scene3DBuilder`, the editor's origin and face names): shaded `0.3 + 0.7|n·v|`, translucent
  objects blended back to front. The field is unshaded and read per SAMPLE through the quantity, range and
  map, the GPU's order. Feature edges (the outline's own rule, `Em3dSectionScene.FeatureEdges`) are drawn where
  the depth buffer does not hide them; the window draws none, but an unshaded field on a closed package
  otherwise reads as a flat blob. Owner decisions: **any direction** — `--iso` (the 3D view's Standard Views
  ▸ Isometric, from +x −y +z, which is *not* `render --iso`'s outline direction), `--view-dir
  top|bottom|front|back|left|right|isometric`, or `--view-dir x,y,z` toward the viewer at the camera's own
  yaw/pitch, so `0,0,1` is exactly Top; **PNG only** for now (`render.field.surface-png-only`); and **a
  temperature is mirrored across the document's symmetry planes by default**, as the view mirrors it
  (`--no-mirror` draws the modelled half; an EM field is never mirrored, as in the view). With no direction the
  plot is a refusal (`render.field.direction-required`): the view's camera is not saved. A volume quantity on
  Surfaces is drawn on the region SELECTED in the view's tree, which a file does not record, so it takes
  `--region <object>` (`render.field.region-required` lists the candidates). A face the model no longer has is
  left out, as the view leaves it out, with a `render.field.faces-missing` warning. `--scale-bar` works on a
  view along an axis and is refused on an oblique one (`render.field.scale-bar-oblique`); `--axes`, `--labels`
  (each object's material at the centre of its visible piece) and `--tight` carry over. The hot spot is ringed
  where it can be seen, and named in the legend. `--json`'s `render.em3d` is `view: "projection"` with
  `toward`, and its field adds `mirrored` and `hotSpot`.
- **A temperature section is a thermal page** (`brief-em3d-88`). A thermal setup has no EM problem, so the
  section is drawn from the view's own elaboration — `C3dProblemAssembly.ViewProblem`, the one problem the 3D
  editor's scene is built from too — in a box at its extent. The range is `FieldColorScale.MinMax`, the view's
  rule for a temperature (brief-em3d-75 D9), extended to the wires drawn; this point's, not the sweep's union
  (owner decision Q1). **Each bond wire is painted from its own T(s)** — a wire is a 1D element and is not in
  the 3D field — by `ThermalWireChains` (`src/Design`, the run's chain paired with its table) and
  `Em3dSectionThermal` (`src/Render`, the cut of each chain segment and node by the plane). Metals are
  outlined, not filled; there are no ports; the thermal boundaries are drawn and labelled on the frame instead
  of the air box's faces, and the caption names the setup, the point and the plane. `--json`'s field report
  adds `wires` (per wire: the pieces drawn, their T span, and every centreline crossing with its s and T) and
  `boundaries`.
- **`--labels`, `--tight`** (owner decision brief-em3d-88 Q2), field plots only: each solid labelled with its
  material where the words fit inside its cut; the page cropped to the section — the width kept, the height
  following the frame, the legend inset in its corner, margin 0 unless `--margin` says otherwise, no caption.
- **`--axes`, `--scale-bar`** — the 3D view's axis indicator (bottom left of the frame) and scale bar (bottom
  right), on any 3D section; the axis indicator on `--iso` too. Both are `Em3dDrawingSheet.DrawChrome`, the
  painter the 3D view's vector export uses, and the bar's length is `Em3dDrawingSheet.ScaleBarLength`, the view's
  own rounding, in the document's display unit. `--scale-bar` with `--iso` is a refusal: an isometric outline
  shortens each axis differently, so no one bar measures it.
- **PNG and vector differ in one way.** A PNG is `DrawVertices` with a colour per vertex, subdivided where
  one triangle spans more than 1/32 of the range, because Skia blends corner COLOURS and the GPU blends the
  field. A vector page has no mesh gradient: each triangle is one path of its centroid's colour. Above
  50,000 triangles a vector slice is **thinned** (owner decision Q2): neighbours of one colour step (256) are
  merged, area kept, and `Triangles`/`TrianglesDrawn` both reported; `--no-thin` draws every one.
- **`--phase <deg>`** (Q3) is refused on a quantity that is not read instantaneously, and never written to
  the document. A hidden plot renders as a shown one: hiding chooses the window's plot, `--field` names one.
- **`--json`** adds `render.em3d.field` — plot, setup, solver, the solution as the file spells it, label,
  quantity and mode, the triangle counts, the range, `stale`, and the run directory read (`explain`'s rule:
  the walk is what a caller cannot otherwise see). `--list-fields --json` returns `fieldPlots`.
- A `.cem` holds no plots: `--field` on one is a refusal saying they live in the `.c3d`.

Gate: `tests/Ui.Tests/Render/FieldRenderCliTests.cs` — the verb as a process on the committed cavity,
against the 3D view's own triangles and range (gate 11 a Surfaces plot from the front); `SurfaceFieldRenderTests.cs`
for a Surfaces and a Faces temperature (depth, the mirror, the range); and `tests/Ui.Tests/Render/TemperatureSectionTests.cs` for a
temperature (its process gate solves *Thermal Output Wires*' `RfHarmonics`, so it is `Category=Benchmark`).

#### 13.8.2 The realistic picture (`--look realistic`)

`brief-em3d-110-headless-realistic-render.md`. What the 3D view's Export Picture makes in the realistic view, with no
window and no GPU:

```
circuitrf render pkg.c3d -o shot.png --look realistic --iso                     # the .c3d's own Look
circuitrf render pkg.c3d -o shot.png --look realistic                           # from the Look's Camera
circuitrf render pkg.c3d -o hot.png  --look realistic --look-set Exposure=1 --look-set Environment=Dark
circuitrf render pkg.c3d -o t.png    --look realistic --field Temps --look-set FieldStyle=Glow --supersample 4
```

- **The format is the contract.** The picture is drawn with the `.c3d`'s own `Look`. `--look-set Key=value` overrides
  one key (repeatable, and NOT split on commas: a gradient `Background` is `#rrggbb,#rrggbb`) on the copy the run
  reads. The value is read as the file's JSON reads it — a number, `true`/`false`, an object such as a `Camera`,
  `null` for the default, anything else a string — by the file's own reader. It is then held to the file's own Look
  validation (`C3dValidation.LookFaults`, `check`'s rules). **There is no flag per Look key**, so a new key needs no
  new flag. An unknown key, an unreadable value and a value the validation refuses are three refusals
  (`render.look-set.*`). `--look-set` and `--supersample` without `--look realistic` are refused, not ignored.
- **The camera.** `--iso` or `--view-dir` (brief 89's spellings: the 3D view's standard views, or `x,y,z` toward the
  viewer) is orthographic and fitted as the view's Fit frames. With neither, the Look's `Camera` (overview D17, written
  by *Set Camera*) is the camera, perspective included, through `Camera3D.SetPictureCamera`, the
  function Go to Camera View calls. With no camera at all, the run is refused (`render.look.direction-required`).
- **`--supersample 1|2|4`** (default 2) and `--background transparent` are Export Picture's: drawn at that factor
  each way within `FieldPicture.MaxSide` (`PictureResample.FactorFor`, the one rule), brought down by
  `PictureResample.Downsample`, and straightened before the PNG is encoded. `--size`/`--scale` are the picture's
  device pixels; `--scale` also sizes the legend's text and the occlusion's reach in pixels, as Export Picture's
  window multiple does. `--variant` picks the Theme background (`Viewer3DViewState.ThemeBackground`).
- **`--field <plot>` composes** for a Surfaces or Faces plot, in the Look's field style (Exact, Lit, Glow, and the
  opacity). The legend, the solution as the caption, and the `Lit Fields`/`Blended Fields` indicator are painted as
  Export Picture paints them, in the embedded typeface. A ClipPlane plot is a section and is refused
  (`render.look.clip-plane-plot`).
- **Refusals:** a `.cem` or any other non-`.c3d` (no appearances), an SVG or PDF ("a realistic picture is pixels;
  use .png"), `--section`, `--tight`, `--labels`, `--axes` and `--scale-bar`. A Look whose `.hdr` cannot be read
  lights the picture with Studio and says so in a `note:`, as the view's status line does. Reference images shown by
  `ShowImages` are not drawn headlessly yet, and a `note:` says how many were left out.
- **`--json`** adds `render.em3d.look`. It gives the environment and any fallback, the exposure, the field style, the
  supersample factor, where the camera came from and its projection, and each override. It also counts the work —
  samples shaded, shadow texels written, occlusion pixels — with no time in it.
- **`explain x.c3d --look`** walks the Look: every key with its value and whether the file stated it, the environment
  (a preset, or the `.hdr`'s resolved path and whether it reads), and the appearance table, each slot's values with
  the statement that decided each field and the objects that use it.

**Nothing here is a second renderer.** `src/Cli/RenderEm3dRealistic.cs` is arguments, the Look's overrides, the
camera, refusals and the report. The frame is planned by `Scene3DFramePlan`, the object the GPU backends read, and
executed by `RealisticPicture` (`src/Render/Scene3D/Look/`) pass for pass as `MetalViewer3DBackend` executes it.
The passes are the shadow map, the occlusion prepass with its horizon pass and blur (quantised to R8 as the GPU's
targets are), then the colour draws in order (rounded to 8 bits per write). Each fragment is shaded by `Pbr.cs`, the
reference the WGSL is scanned against. The triangles are rasterised by `SoftwareRaster`, the rasteriser brief 89's
`Em3dSurfaceField` draws with, factored out so there is one. Bands of rows run in parallel, and each pixel is
computed by one band in the plan's order, so the bytes do not depend on the thread count
(`CRF_RASTER_THREADS` caps it).

**The GPU/CPU agreement rule.** The two paths can only drift if the WGSL and `Pbr.cs` disagree. 106 §3g scans their
constants. `RealisticPictureTests` compares the PICTURES on macOS: the Metal backend offscreen and `RealisticPicture`,
the same plan, for spheres of each metal, dielectrics, a clear coat, glass, a box on a plate and a field plane,
Exact orthographic and Lit perspective. Away from a one-pixel band at each surface discontinuity (the two
rasterisers' coverage differs at an edge), at least 99 % of pixels must be within 4/255, and every field pixel within
1/255. Off macOS the gate is skipped with that reason. Gates: `tests/Ui.Tests/Render/RealisticRenderCliTests.cs`
(plain unchanged by recorded hash, the Look from the file, refusals, determinism, cancellation, as a process,
`explain --look`, the Look's camera) and `tests/Ui.Tests/Viewer3D/RealisticPictureTests.cs`.

## 14. `netlist` — the extraction, as a document

`brief-automation-11-missing-verbs.md` R-aut11-1.

**The gap it closes is the largest one this surface had.** `run` on a `.csch` failed with
`Error: Cell '"FormatVersion"' not found in libraries` — it had parsed the JSON document as netlist
text and reported its first key as a missing cell name — while `check` and `explain` both accepted a
`.csch` happily, so the surface read as though a run should too. The consequence was that **the
automation surface could not simulate any design a user had actually drawn.** It ran hand-authored
netlists only.

Two things landed together, and neither is complete without the other:

- **Every run verb takes a `.csch` and extracts it in memory** — the same round trip §10.3 describes,
  through `CircuitSource.ReadRunInput`. A document that is neither is a refusal naming its kind.
- **`circuitrf netlist <path> [-o out.cnl]`** writes that extraction as a file.

```
circuitrf netlist Stage1.csch -o stage1.cnl
circuitrf netlist ./MyWorkspace --cell Stage1 -o stage1.cnl
circuitrf netlist Stage1.csch                    # the text on stdout
```

**It owns no extraction.** Every byte comes from `CircuitSource.CnlTextOf` — `NetExtractor.Extract`
followed by `CnlWriter.Write`, the first half of the round trip the GUI's own Simulate performs — so
the file a caller is handed is not merely equivalent to what a run consumes, **it is the same bytes**.
The provenance comment is deliberately constant for that reason: a timestamp or a verb name in it
would make two extractions of one schematic differ. `tests/Ui.Tests/Cli/MissingVerbsCliTests.cs`
compares the verb run as a PROCESS against the in-process call, and scans the whole of `src/Cli` for
a second `CnlWriter.Write`.

**It is also the reference answer.** AUT-7 §3's false defect report — a working component written up
as broken — came from a one-net instance line that one look at a known-good extraction would have
settled. A client that has written a `.cnl` by hand can now compare it against what the application
produces for the equivalent drawing.

**A cell folder and a workspace resolve as `render` resolves them** — the same `CellLookup` and
`CellFolder.ResolvePrimary`, so the cell extracted is the cell that verb would have drawn. There is no
`--view`: a netlist comes out of a schematic and out of nothing else. `-o` takes a `.cnl` and refuses
any other extension, because there is one format here and `-o plot.svg` is a caller who meant `render`.
A `.cnl` input is refused rather than re-emitted: passing it through the reader and the writer would
hand back a file that is not the one given — comments gone, directives reordered — and call it an
extraction.

### 14.2 The same verb, the other way: `--to-schematic`

`brief-agent-authoring-overview.md` AA-6.

```
circuitrf netlist filter.cnl --to-schematic -o filter.csch
circuitrf netlist filter.cnl --to-schematic > filter.csch
```

An agent can write a correct netlist far more easily than it can place symbols and route wires, and
a person reviewing that agent's design wants a drawing. **A drawing from a netlist is the extraction
run backwards**, so it is this verb in another mode and not a new noun.

**A flag, not an inference from `-o`.** The verb's direction everywhere else is schematic → netlist,
and a `.cnl` handed to it is refused (§14). Reversing the direction because `-o` happened to end in
`.csch` would make one typo turn an extraction into a drawing. `-o` with the flag must end in `.csch`.
The board flags and `--cell` are refused alongside it.

**It owns no placement and no routing.** Both are `NetlistSchematic.Build` in `src/Design/Schematic`,
below the firewall, so the window can offer the same drawing without a second copy. The wiring is
`SchematicAutoRouter`, the router the SPICE subcircuit import draws with, because geometry IS
connectivity here: a wire laid wrongly across another does not look wrong, it joins two nets.

**The gate is the round trip.** Every committed `.cnl` (they are all under `testdata/`; `examples/`
ships schematics) either draws and extracts back, through `SchematicCircuit`, to the same instances,
nets in order, parameters, globals, measurements and analyses, or is refused for a reason the test
pins. Today 51 draw and 4 are refused: two define cells, one uses a type with no symbol, and one is
a wirebond. **Every net is labelled** with its netlist name because a net's name is part of the
circuit (`V(out)` reads it), and an unlabelled wire extracts under an invented one. The one parameter
a drawing adds is a variadic symbol's port count, when the line left it to the nets. Gate:
`tests/Ui.Tests/Cli/NetlistToSchematicTests.cs`, which also holds every drawing to no overlapping
symbols and orthogonal wires, and the verb as a process byte for byte against the in-process call.

**The layout, in order.** The main line is the shortest chain of elements from port 1 to port 2 (with
fewer than two ports, between the two drive or termination fixtures furthest apart). Each two-terminal
element on it is turned so its entry pin faces left. Elements with one signal net hang below the net
they connect to, signal pin uppermost, spaced by their measured glyph and label widths. A
two-terminal element bridging two path nets sits above. Everything else goes in rows below, growing
downwards from the nets it connects to. Upright parts have their labels moved beside them, because
the default place under the glyph is on top of the ground symbol. Labels are keep-out for wires.

**The netlist is read as written** (`CnlReader`, not the technology binding a run uses), because the
binding writes the workspace's substrate into each microstrip line. A drawing carrying those numbers
would carry them twice once its own extraction bound the technology again. A Touchstone `File`, which
the reader makes absolute, is written relative to where the drawing is saved.

### 14.1 The same verb, over a BOARD

`brief-authored-board-3-companion-writers.md` R-ab3-2.

A board netlist, a placement table and a bill of materials are *the extraction a layout performs*.
That is the same sentence about a different document, so it is the same verb — **not a fourth noun,
and certainly not a `convert` pair**: `convert` is artwork to artwork and every one of its readers
lands on a cell folder plus a technology, where these are derived tables and neither is that.

```
circuitrf netlist board.clay --ipc out.ipc --placement out.csv --bom out.csv
circuitrf netlist ./MyCell   --placement out.csv          # a cell folder's layout view
```

**`-o` alone on a `.clay` is a refusal naming all three flags.** Two of the three tables are `.csv`
and the extension cannot say which — and "which table" is exactly what a dialog would ask. The same
refusal covers a `.clay` with no destination at all: there is no table on stdout, because three of
them cannot share one stream. A `.csch` behaves exactly as §14 describes; the board flags on one are
a refusal BY KIND.

**More than one in a run is ordinary and encouraged.** Footprint 5 R-fp5-2's reason is the governing
one: *they have to agree, and hand-editing three files is how that goes wrong silently.* One
invocation is ONE projection — `BoardCompanions.Project`, off briefs 1 and 2's pads, the root's own
vias and the schematic beside the artwork — and three files that cannot disagree.

**It owns no projection and no writer.** `BoardCompanions` is what the layout editor's File ▸ Export
▸ Board netlist / Placement / Bill of materials rows call, and byte identity between the CLI as a
process and that in-process call is the gate — the two drifting is invisible, because a board
exported from the window and one exported on a build machine both look like board netlists.

**Nothing is written on a refusal, and a partial table is never written.** `BoardNetlist` and
`PlacementTable` both state on the way IN that a non-null refusal means nothing was read and nothing
may be used; that contract applies symmetrically on the way out. Over the flatten ceiling the pads
were never read, so no table is written; a connected piece of copper carrying two different net
names is either a short or a wrong label, and nothing is written for that board either. A part whose
footprint pins cannot be joined to its component's ports contributes no records and is NAMED — brief
1's per-part rule, unchanged.

**A board that resolves no schematic still writes all three**, thin and honest about being thin: the
netlist and the placement off the artwork, the bill of materials off nothing but the reference
designator and the footprint each placement carries. The verb says so on stderr before it writes, and
the dialog says so before it writes; neither refuses, because a placement file with no nets in it is
a perfectly useful placement file.

Three things the writers declare that their readers would otherwise have to infer — and the middle
one is the payoff: the netlist's units, the placement's units, and **the placement's coordinate
ORIGIN**, which is a refusal in the import dialog when a file does not state it (*three quarters of a
millimetre on an 0402 is the difference between landing on the part's own pad and landing on its
neighbour's*). A file circuitRF wrote must never provoke that question, and `SymbolOrigin` is the
honest answer because that is what an instance's origin is. Conversely, **a field the artwork does
not state is OMITTED rather than defaulted**: a surface land writes no drill and no access code, and
a plausible `A01` there would make a through feature read as a surface one.

Gate: `tests/Ui.Tests/Cli/NetlistBoardVerbTests.cs` and `tests/Ui.Tests/RailRf/CompanionWriterTests.cs`.

---

## 15. `plot` — one picture, without authoring a display first

`brief-automation-11-missing-verbs.md` R-aut11-2.

Hand-authoring a `.cdd` to draw a single trace was the largest piece of incidental work in an
otherwise short task: a document with a tab, a plot container, a placement, a source reference and a
slice, every field of which has to be right before anything appears.

```
circuitrf plot lc.s2p -o s21.svg --trace cube=S,i=2,j=1,y=db20 --title "LC lowpass"
circuitrf plot run.npy -o pae.png --trace cube=PAE --trace cube=Pout,axis=right --x 5:25
```

**There is ONE plotting path.** The verb builds a `DataDisplayConfig` — the document a `.cdd`
deserializes to — and hands it to `RenderDataDisplay.Draw`, which is the same function §13.7's `.cdd`
half calls. `--write-cdd` hands that document back, so a caller has a correct starting point to edit
rather than a blank page, and so the claim is checkable: the gate renders the written display with
`render` and compares the two pictures byte for byte, in all three formats.

**A trace spec is the trace card's own.** `cube=` is parsed by `CubeTraceSpecParser`, which is what
the spec box on a trace card parses, so `S[:,1,0]`, `Pout` and `mag(V[:,"X1.drain"])` mean here
exactly what they mean there. The fields are split on TOP-LEVEL commas only, because the shorthand
they carry is full of commas.

| Key | Means |
|---|---|
| `cube` | the cube, bare or with a slice and a transform. Required. |
| `i`, `j` | pin the cube's axes named `i` and `j` by **port number**. Refused alongside a bracketed slice. |
| `y` | `db`, `db10`, `db20`, `mag`, `phase`, `real`, `imag`, `conj` — folded in as the transform prefix, so there is one table of those names and it is the parser's. |
| `axis` | `left` (default) or `right`. |
| `probe` | the WSProbe a metric is taken at, by the LABEL `__WspProbes` carries. Turns the trace into a probe metric. |
| `with` | the second probe of a pair, for the `wsp_block_calc` metrics. |
| `set` | the ordered probe set for Ohtomo, semicolon separated. Order is part of the answer. |
| `metric` | which of the reference document's quantities (WSP-4). Required alongside `probe`. |
| `z0` | the reference the circulator, pair and Ohtomo metrics normalise by; absent means the source group's own port-1 Re(Z0), else 50 Ω. |
| `side` | `G` or `L` — which side of every probe Ohtomo treats as the active subnetwork. |
| `gi` | which of Ohtomo's `G_i` to draw, 1-based. |
| `stat` | `histogram`, `cdf`, `quantile` or `yieldsens` — rewrites the trace exactly as the trace card's Statistics menu does (`TraceStatistics.Build`, brief-yield-8); the `.cdd` written carries the same expression, style and origin. |
| `over`, `bins`, `percent`, `param`, `fit` | the statistic's options: the axis (default `trial`), the bin count (default Freedman–Diaconis), `percent=1`, a yield sensitivity's statistical variable (`R1.R`), `fit=normal` for a histogram's fitted normal. Refused without `stat=` (a fit is also accepted on a typed `histogram(…)`). |
| `style` | `line`, `bars` or `step`. |
| `colorby` | colour a family's members (or a scatter's points) by a per-member cube (brief-yield-9 R-ya9-1): `pass` (the result's `trials.pass`), `corner` (a family over `corner`, one colour each), or any cube on the family axis such as `trials.goal:S21:pass`. Passes draw in the trace's colour at reduced opacity, fails in the fail colour after them, a trial that did not evaluate not at all; the legend counts them. |
| `envelope` | `minmax`, `p:<p>` (the Pp–P(100−p) band) or `sigma:<k>` (mean ± kσ): a shaded band per X point from `pctl_over`/`median_over`/`mean_over`/`std_over` over the family axis, the median (or mean) as a line. A Rect plot's only. |
| `curves`, `nominal` | `0` draws the envelope without the members; `nominal=0` hides the nominal's curve that a yield result's family draws over its trials. Both default to `1`. |
| `fitline` | `1`: a scatter's least-squares line, its R² in the legend. |

**`--spec-lines` / `--no-spec-lines`** turn the goals' limits on or off (brief-yield-8 R-ya8-3); absent is the
window's default, on, and a line exists only where the result records its goals — a `.yield.npy`. `--spec-lines` on a
result that records none is refused (`plot.spec-lines.no-goals`) rather than drawn without them. The gate
`HistogramPlotParityTests` compares the verb's SVG of a histogram with spec lines to the in-process composer's, byte
for byte. `TrialPlotParityTests` does the same for a pass/fail family with a P1–P99 envelope (brief-yield-9
R-ya9-7). Selecting a trial is the window's alone — a picture has no one to click it.

```
circuitrf plot div.yield.npy -o h.svg --trace cube=trials.goal:Vout:worst,stat=histogram,fit=normal --spec-lines
circuitrf plot div.yield.npy -o ys.svg --trace cube=trials.pass,stat=yieldsens,param=R1.R
circuitrf plot lpf.yield.npy -o fam.svg --trace cube=SP1.S,i=2,j=1,y=db20,colorby=pass,envelope=p:1
```

**An integer on an `i`/`j` axis is a PORT NUMBER, not an index** — `S[:,2,1]` is S21, which is what
makes that spelling readable. Off by one here is the quietest possible wrong answer, since S12 and S21
are both legal curves and on a reciprocal part they are the same one; the gate pins the slice indices.

**A cube the result does not hold is refused BY NAME, listing what it holds.** The parser's own answer
for a bare unrecognised name is "Missing `[`" — correct from where it stands and useless to a caller
who mistyped a cube or is looking at the wrong run — so that one refusal is made here rather than
forwarded. **A plot with no trace is refused rather than drawn**, for R-rnd4-4's reason: an empty plot
is a valid picture that exports cleanly and looks exactly like a measurement that came back empty.

**A WSProbe quantity is the same trace the card authors** (WSP-4 R-wsp4-12), so there is no second
probe path here any more than there is a second plotting one:

```
circuitrf plot run.npy -o loci.svg  --type polar --trace cube=SP1.wsp,probe=GATE,metric=invH0
circuitrf plot run.npy -o margin.svg          --trace cube=SP1.wsp,probe=GATE,metric=SM_Y0,y=db20
circuitrf plot run.npy -o lgm.svg   --type polar --trace cube=SP1.wsp,probe=GATE,with=DRAIN,metric=LGM
```

`cube=` names the run's own `wsp` MATRIX and the metric is taken of it; the written `.cdd` carries
the probe spec in the field the window writes, and the byte-identity gate above extends to it
unchanged. **Case is load-bearing in the metric names** — the document writes `LGF` for one probe's
forward circulator loop gain and `LGf` for a probe pair's feedback-as-synthetic-FET one — so the
exact spelling resolves first and a case-insensitive form resolves only where it is unambiguous;
`1/H0`, which no shell takes, is also spelled `invH0`. **A probe the run does not have is refused BY
NAME with the run's own list**, in the library's own sentence, and a `probe=` without a `metric=` is
refused naming the flag that answers it: a probe alone would draw the raw matrix entry and look like
an answer. A `probe=` on a cube that is not a `wsp` matrix is refused by kind, for the same reason.

**The stability envelope has its own five keys** (R-wsp4-9), and they are the card's own: `src=` and
`load=` name the probe each side is pulled at, `gammaS=`/`gammaL=` take a `|Γ|` **ladder** —
semicolon separated, because a comma is the field separator — `theta=` is the angular step in
degrees, and `passive=` names the passivated run `NDFenc` is taken against.

```
circuitrf plot run.npy -o env.svg \
  --trace cube=SP1.wsp,probe=P3,src=PS,load=PL,gammaS=0.9;0.875;0.874,theta=15,metric=SMenv,y=db20
circuitrf plot run.npy -o ndf.svg \
  --trace cube=SP1.wsp,probe=P3,src=PS,gammaS=0.875,passive=SP2.wsp,metric=NDFenc
```

An omitted side, an empty ladder or a single `0` leaves that side unpulled, and both sides unpulled is
a refusal. **A pulled probe must sit directly at its `Term`** — the library's
`wsprobe.envelope-probe-not-at-termination` refusal is forwarded verbatim rather than drawn as an
empty picture. The slice the verb writes is the ENVELOPE's own axes (`rhoS`, `thetaS`, `rhoL`,
`thetaL`, and `freq` for the two loci), with the pulled side's **phase** as the x axis when there is
no frequency axis, because that is the axis [E] Fig. 6–9 read the margin against.

`--x`/`--y`/`--y2` are optional and independent — an axis without one autoscales, which works because
`Plot.RestoreAxesFromConfig` re-autoscales only the axes whose own flag is still set. They are refused
on a Smith or Polar chart, whose window is the complex plane framed on the unit circle. **This is the
convenience over `.cdd` authoring, not a replacement for it**: everything a display can express stays
reachable by writing one and calling `render`.

### 15.1 An antenna pattern — `--radial db`, and the cut

ANT-7. **Whatever the Data Display gains, this verb gains the same**, because it writes the document
the display reads; `--radial db` is one field on the plot container and nothing here draws it.

```
circuitrf plot run.npy -o eplane.svg --type polar --radial db --db-unit "dB(W/sr)" \
  --trace cube=farfield.U,cut=0,port=1,freq=2.45G,y=db10
circuitrf plot run.npy -o pattern.svg --type polar --radial db --db-floor -30 --db-ring 5 \
  --trace cube=farfield.U,cut=all,y=db10
```

| Flag | Means |
|---|---|
| `--radial linear\|db` | how the polar RADIUS is read. `db` makes it a pattern plot; refused on any other `--type`, because there is no radius for it to be. |
| `--db-floor` | the centre, **relative to the outer ring**. Default −40. A positive value is a refusal naming the sign convention, not a silent flip. |
| `--db-ring` | ring spacing in dB. Default 10. |
| `--db-ref peak\|<dB>` | the outer ring: the data's own peak (normalised, the default) or an absolute level. |
| `--db-unit` | what the radial numbers are in — `dBi`, `dB(W/sr)`. Blank takes the cube's own `Unit`, which ANT-4's and ANT-5's cubes do not yet carry. |
| `--whole-plane` | each `cut=` becomes **ONE** trace spanning −θ_max … +θ_max, fetching the φ + 180° half alongside its own, instead of the two traces below. Needs a pattern scale, like the flags above it. |
| `--angle-labels` | bearings every 30° outside the disc, with a spoke to each. **Not a dB option** — a LOCUS has a bearing too — so it is not in `DbOptions` and is refused on its own terms: polar only, either radial mode. |

Every one of those is refused when the plot has **no pattern scale at all**, rather than doing nothing.
`--radial db` gives a polar plot one; `--type surface` (§15.2) has one by construction, so the floor,
the reference, the ring step and the unit are live there with no `--radial` — they are properties of
the SCALE, and both kinds read them the same way.

**Values below the floor are drawn AT the floor and never dropped.** A gap in a pattern trace reads as
a null in the antenna, and a real null and a clipped value must not look the same. Above an ABSOLUTE
reference a sample is drawn at the outer ring and the plot says how many were — the same discipline,
the other end of the disc.

**The plot states whether it is normalised or absolute**, with its reference, under the picture; a 0 dB
peak with no reference is not a result. Beside it, the θ span and what it means, built from the axis
rather than written as a constant, so ANT-11's extension to 180° changes the sentence with it. The
cut, the driven port and the frequency are the trace's own pinned axes and are already in its label
strip.

Two trace keys, both shorthand over the general slice mechanism:

| Key | Means |
|---|---|
| `cut=<deg>` | pin `phi` to the nearest sample to that bearing and sweep `theta` — the E-plane / H-plane plot. The verb PRINTS the φ it landed on. |
| `cut=all` | sweep `theta` and keep every `phi` as a curve family — the whole pattern at one frequency. |
| `port=<n>` | pin a `port` axis by **port number**. |
| `freq=<f>` | pin the `freq` axis to its nearest sample; takes an SI suffix (`2.45G`). |

**An integer on a `port` axis is a 1-based PORT NUMBER, not an index** — the same trap `i`/`j` record,
and for the same reason. It is resolved against the axis's own VALUES rather than by subtracting one,
because a far-field cube's port axis carries the numbers of the ports that were DRIVEN and those need
not start at 1; a port the run does not have is refused listing the ports it does.

**0° is at the top and angles increase clockwise** — the compass convention, which is what both an
elevation cut (θ from zenith) and an azimuth cut read on. It is fixed rather than a flag: a plot whose
orientation has to be read off a control before the picture means anything is worse than one
convention stated on the plot.

### 15.2 The 3D pattern surface — `--type surface`

ANT-10. A surface r(θ, φ) = the pattern in dB above a floor, over the upper hemisphere, coloured by
the same value. **It is not the default pattern view and must not be read as the better one**: the
principal-plane cuts of §15.1 tell an engineer more about an antenna. What the surface is genuinely
better at is seeing a pattern is *not* what you assumed — a squint, an unexpected lobe, a mode that is
not the one you designed for — and being the picture that goes in a report.

```
circuitrf plot run.npy -o lobe.png --type surface --view iso --db-unit "dB(W/sr)" \
  --trace cube=farfield.U,cut=all,port=1,freq=2.45G,y=db10
circuitrf plot run.npy -o broadside.svg --type surface --view broadside --db-floor -25 \
  --trace cube=farfield.U,cut=all,y=db10
```

**The trace spelling is `cut=all`, unchanged** — the same two-open-angle-axis slice the polar family
plot uses. Nothing new is authored: the surface finds θ and φ **by name**, on the same
`theta`/`el`/`phi`/`az` test the polar cut already applies to decide whether an axis can be an angle
at all, so it never depends on which of the two `:` the positional family convention would have made
the X axis. A cube with no such pair is refused by name rather than drawn as something plausible.

| Flag | Means |
|---|---|
| `--view iso\|broadside\|phi0\|phi90` | a named camera. `broadside` looks down +z at the zenith; `phi0` and `phi90` put that principal plane IN the screen. Keeps the current `--zoom`. |
| `--rotate <az>,<el>` | the camera directly, in degrees. Azimuth wraps; elevation clamps to ±90 — **negative is allowed and is informative**, since from below a hemisphere shows nothing but the ground disc. |
| `--zoom <k>` | how much of the canvas the unit sphere fills. 0.25 … 8. |
| `--color-map <name>` | the contour plot's own ramp set. Default `cool`. |

All four are refused on any other `--type`, and `--x`/`--y`/`--y2` are refused on a surface: its
framing is the camera's and it has no x and no y axis for a range to be a range of.

**The principal planes are named by their own φ, not "E-plane" and "H-plane".** Which cut is the
E-plane is a property of the antenna's polarization; the cube does not say, ANT-6 computes it
separately, and a view button that named the wrong plane would be a caption that is confidently wrong.

**The ground plane is drawn as a disc at θ = 90°, and the θ span is stated under the picture** — the
same sentence §15.1's cut carries, from the same place. It is §4's whole point: in 2D a missing lower
hemisphere reads as a half-disc and needs a note, but **in 3D a hemisphere floating above a plane
reads as a complete, very good antenna** unless the view says otherwise. Both halves are built from
the cube's own axis, so ANT-11's extension to 180° changes the sentence and closes the surface
underneath with no constant to edit.

**Orthographic, no lighting, no perspective.** Orthographic is easier to read for a pattern and easier
to get right; a shaded surface would encode the same number twice, in two scales, one of which has no
legend. The scale that IS shown is the colour bar, with its floor and its reference, on ANT-7's own
ring lattice and in ANT-7's own words.

**The camera is carried in the `.cdd`, not re-read from a flag** — two angles and a zoom, which is the
whole of it — so `plot --write-cdd` followed by `render` draws the same view, byte for byte. That is
the gate (`tests/Ui.Tests/Cli/Pattern3DCliTests.cs`, all four named views).

**Frame cost, measured once** (Release, an M-series Mac, the drawing alone): at **1° × 1° — 63,000
triangles — 55 ms, about 18 fps**, which is not comfortably interactive; decimated for interaction it
is **4,050 triangles and 4.4 ms**, past 200 fps. So the app draws the decimated grid while the pointer
is down and the full one on release, and the endpoints of both angle axes are kept whatever the stride
so the silhouette and the peak direction do not move between the two. `plot` and `render` always draw
the full grid. Cost is set by the triangle count and barely by the canvas: halving the canvas in each
direction moved 55 ms to 50 ms. A 2° × 2° grid is 15,660 triangles at 14 ms and needs no decimation
at all.

---

## 16. `find` — what is here

`brief-automation-11-missing-verbs.md` R-aut11-3.

There was no way to ask the surface what exists; locating a workspace that holds a particular device
meant searching the filesystem outside the automation surface entirely, which a protocol client with
only the server cannot do at all. Every document below is one this program already knew how to read.

```
circuitrf find ./projects                    # workspaces, cells, views, analyses
circuitrf find ./projects --depth 6 --json
circuitrf find ./big-tree --no-analyses      # names only; each analysis costs an extraction
```

**It reads what the other verbs read**: a workspace is a directory holding a `.cws`
(`DocumentKinds.Classify`), its cells are `CellLookup`'s answer, each view is
`CellFolder.ResolvePrimary`'s, and the analyses are the ones the elaborator would see —
`CircuitSource`'s extraction, which is the GUI's own Simulate path. A cell whose analyses could not be
read reports `analyses` as ABSENT rather than empty, because "declares none" and "could not be read"
are different answers and the second must not read as the first.

**The walk is bounded and says when it stopped short.** A listing that quietly gave up is the one
failure this verb must not have: a caller reads a short answer as "the workspace is not here" and goes
elsewhere. So `--depth` is an argument (default 4, at most 12) and `truncated` is in the document,
with a warning beside it. A workspace nested inside another is a leaf: two workspaces have different
default technologies, and attributing the inner one's cells to the outer is worse than not listing
them.

**It never leaves the root.** A directory symbolic link is not followed — that is the one way a
bounded walk stops being bounded and a confined one stops being confined. On `serve` the root is
already `PathRoot`'s.

**A 3D view says which of its setups are solved (`brief-em3d-98` R-em3d98-8).** Its view row carries `solved` — per
(setup, solver leg) the state alone, `{"setup","solver","state","partial"}`, from `C3dSolveStatus.Of` as `explain`'s
Solved section is; the detail (when, how long, what changed) is `explain`'s. In text, one `solved:` line under the cell:

```
    Launch                   3d: Launch.c3d
                             solved: Palace fem current, openEMS fdtd outOfDate (partial)
```


## 17. `rail` — the whole railRF window, with no display

`brief-railrf-10-cli-verb.md`; `docs/design/railrf.md` §11.5 and §5.

**Why it exists** is §5's claim for the whole railRF architecture: *a board that can only be judged by
opening a window cannot be judged in CI.* This is the one command that reads the document, resolves the
artwork and the stackup, runs the extraction and the solve, applies the via check and answers with an
exit code. It is also, per R-rail10-9, the cheapest way to exercise a real reference board — a refusal
that names its own flag is readable in a terminal in a way a red control in a dialog is not.

```
circuitrf rail board/Panel.crail
circuitrf rail board/Panel.crail --rail +1V8 --accurate -o report.pdf
circuitrf rail board/Panel.crail --load U1.VDD=120mA --target-drop 50mV --json
circuitrf rail board/Panel/layout/Panel.clay          # the .crail beside it
```

### 17.1 It owns no analysis, and no rendering either

`src/Cli/Rail.cs` is argument parsing, refusals and reporting, on `src/Cli/Authoring.cs`' terms. Every
number comes out of `RailDcRun.Run` — which is what the window's Run button calls — and every pixel of
an `.svg`/`.pdf` comes out of `RailReportPage` in `CircuitRF.Render`. The file names neither SkiaSharp
nor any extractor, and `RailCliVerbTests.TheVerbHoldsNoAnalysisAndNoSecondExportPath` is a
comment-stripped scan that keeps it that way.

**`RailReportPage` is new and it is below the firewall on purpose.** §11.7 says *there is one route
from an overlay to a page and not two*, and brief 9 put the clipboard composition in
`src/Ui/RailRf/RailGraphicExport.cs` — which cannot be reached from `src/Cli`, because it goes through
`LayoutClipboard` and that class performs `IClipboard` traffic and hands back an Avalonia `Bitmap`. So
the page composition lives in `src/Render`, where the headless verb and the GUI's future `Report ▸`
both reach it. Detail in `src/Cli/RESOLVED.md`.

### 17.2 What it takes, and what it refuses to guess

The kind is inferred through `DocumentKinds.Classify`, exactly as `check`, `render` and `netlist` infer
it, and **any other kind is a refusal BY KIND** — a `.cnl` is refused as a netlist, not as an unreadable
file. A `.clay` with no `.crail` beside it is the interesting case: there is a board and no rail
declaration, so the verb refuses and names the four flags a declaration answers (`--rail`,
`--reference`, `--source`, `--load`). **It does not author one** — `new`'s own rule: once a document
exists, the way to change it is to WRITE it, because the format is the contract.

| Option | Meaning |
|---|---|
| `--rail <name>` | Which rail. **Omitting it runs them all**, in `RailOrder`'s dependency order — `hb`/`lp`'s own shape for a wrapped sweep, and for the same reason: a downstream rail solved alone starts its source from a nominal instead of from the upstream answer. |
| `--fast` (default) / `--accurate` | §2.9's two readings of the geometry. Fast is the default, as in the window. A rail Fast refuses because it reaches a load only through spreading copper is **meshed in the same run** and its report opens with a `model:` line saying so — the window does the same and shows a Model card. |
| `--no-escalate` | Keeps that refusal instead: the rail is not solved, and the sentence names the copper and both remedies. |
| `--source REFDES.PIN=<model>` | Repeatable. The model is `3.7V,50mOhm,10nH` (any subset, in any order, each field identified by its UNIT or by a `v=`/`r=`/`l=` key) or a Touchstone file. A row for the same anchor is **replaced**, not added beside — two sources on one rail are two branches in the same mesh. |
| `--load REFDES.PIN[=<current>]` | Repeatable. **No current is accepted** — see §17.3. |
| `--target-transient dV=<v>\|ripple=<pct>%[,dI=<a>][,tr=<s>]` | §2.2's transient target, which the model always carried and no flag could state (designer feedback round 11). `ripple` is taken of the first source's voltage and `dI` defaults to the loads' peak (else DC) current — the window's "derive Z from the load" defaults — through the same `RailTransientSpec.FromRipple`; `tr` is optional and sets the band top to 0.35/tr. The derived target is said (`rail.target.derived`, stderr and `--json`) WITH its arithmetic, because V/I — the load's DC resistance — is the commonest slip and is 20× too large at a 5 % ripple. Stating it with `--target-z` is a refusal: both are the one frequency-domain target. |
| `--target-drop`, `--target-z`, `--mask [PORT=]<file>` | §2.2's target forms. A mask is per observation port, so the un-anchored spelling states which ports it means; a mask that lands on no port is a refusal, because a mask nobody applied reads on the report exactly like one that was honoured. |
| `--aggressor NAME=<freq>[xN]` | Repeatable. `x` and `×` both spell the harmonic count. |
| `--reference <layer>`, `--extent as-imported\|filled\|infinite` | Brief 1 `R-rail1-6`. |
| `--rows N`, `--all` | How much of the ranked breakdown the console prints. |
| `-o out.{csv,npy,mat,txt,svg,pdf}` | The result document — §17.4. |

**Anchors.** `REFDES`, `REFDES.PIN`, or `@x,y` in DBU. The coordinate form is the fallback
`RailPortAnchor` documents — *a coordinate is accepted where there is no placement file and no board
netlist* — and headless that is every document, because a `.crail` names no placement file and nothing
fills `PdnExtractionRequest.Pads` in. See `src/Cli/RESOLVED.md`: that gap is not this verb's and it is
the window's too.

**Values carry units, through `Units`** — the expression engine's own table, so a spelling that works
in a `.cnl` works here and the two cannot drift. A bare number is base SI, which is what every number
in a `.crail` already is.

### 17.3 An unstated value is a refusal; an unstated CURRENT is not

R-rail10-3, and the distinction is the whole of Q-16 — it is easy to implement backwards.

| Unstated | Answer |
|---|---|
| The reference layer | **Refusal**, naming `--reference`. railRF never infers one (Q-8), and the verb checks it before the run so the sentence can name a flag rather than a combo box. |
| The technology | **Refusal**. Unlike `render`'s orphan `.clay`, which is a NOTE: a picture on the fallback palette is honestly a picture of geometry, where copper priced with no thickness and no conductivity produces numbers that look exactly like numbers with physics behind them. |
| The Excellon coordinate format | **Refusal** — `convert`'s existing sentence, on the import path. |
| The via plating thickness | **Not a refusal.** Brief 6 takes it as a setting and every flag says which basis produced its limit. |
| A load's current | **Not a refusal.** It is an observation port, it contributes nothing to the DC solve, and the report lists it AS observed. Refusing it — or defaulting it to zero — would make *not added* and *added with no current* indistinguishable. |

`--set` is the one flag in the brief's table with nothing to land on: a `.crail` declares no variables,
every quantity in it is stated in base SI and none is an expression. It is a **refusal naming the flags
that do state those quantities**, rather than being accepted and dropped — §3.3's own finding, one verb
along.

### 17.4 What it writes, and the provenance every export carries

`.csv` is the tables (ports, sources, the ranked breakdown, the via fields and their flags, the
regulators, the findings and the notes). `.npy`/`.mat`/`.txt` go through `DataSetExporter` over a
`DataSet` holding each rail's own cubes in a group named after it. `.svg`/`.pdf` is the report page.
`.sNp` is **refused**: Z(f) at the observation ports is the frequency answer and this phase answers DC,
and a Touchstone holding the DC point repeated would look like a measurement. **There is no picture on
stdout** — `render`'s own rule, because stdout is the result document and `--json` has to co-exist with
the write.

**R-rail10-5: every export says which model produced it.** Overview §4 rule 1 — a file read six months
later has no status strip beside it. So every format carries the model (Fast or Accuracy), the reference
extent, the temperature, how many parts are modelled from a file, how many have no bias curve, and
whether any ESR resolved to a class default (which makes a derived peak height *indicative*, Q-15). The
CSV carries it as a comment header, the `.npy` as a `provenance` group whose strings ride on a labelled
axis (`em`'s diagnostics group is the precedent), the page as a banner under its title, and `--json` as
`result.rail`.

**Where there is no part library, the counts are not zero — they are unknown**, and the line says so. A
`0` there would read as "checked, and all fine".

### 17.5 Progress, cancellation and exit codes

`RunHost`'s `RunControl`, exactly as `em` and `render` ride it. 0 on success; **1** on a refusal, with
the run service's own sentence kept whole; **130** on a cancellation, which **writes nothing** — the
bytes of every format are complete before anything reaches the filesystem, so that is true by
construction rather than by a guard.

### 17.6 The gate

`tests/Ui.Tests/RailRf/RailCliVerbTests.cs`. The verb run as a PROCESS writes the `.svg` and the `.pdf`
that an in-process `RailReportPage.Draw` writes for the same result, and its `--json` voltages are
`RailDcRun.Run`'s to twelve places. **Two exclusions, both stated and neither a property of either code
path**: Skia's SVG device numbers its `clipPath` elements from a process-wide counter it never resets,
and a PDF carries its own creation timestamp — the same two `RenderCliVerbTests` already measured, and
applied only when the raw bytes differ. Beside them: the source scan, the five rows of §17.3, the
provenance read back out of three written files, the two exit codes, and the end-to-end that authors a
workspace, checks a rail document and solves it **with no display at any step**, which is §5's claim
for this whole architecture.

## 18. `smith` — the Smith Chart tool's answer, with no display

`brief-smith-10-cli-verb.md`; `docs/design/smith-chart.md` §9 (P3).

**Why it exists** is that note's own phasing decision: the window ships first, the arithmetic lives
below the firewall from day one, and the headless verb is therefore *wiring rather than a refactor*. A
matching network that can only be judged by dragging a gripper cannot be judged in CI; this is the one
command that evaluates a `.csmith`, reports the reading and the walk, and answers with an exit code.

```
circuitrf smith match/Lmatch.csmith                      # the reading and the per-node table
circuitrf smith match/Lmatch.csmith --at 1.9GHz          # somewhere else in the band
circuitrf smith match/Lmatch.csmith -o load.s1p          # the load Γ across the band
circuitrf smith match/Lmatch.csmith -o chart.svg --json  # the picture AND the document
```

### 18.1 It owns no arithmetic, and no rendering either

`src/Cli/Smith.cs` is argument parsing, refusals and reporting, on `src/Cli/Authoring.cs`' terms. Every
number comes out of `src/Design/Smith` — `SmithCascade.Evaluate`, `SmithReadings`, `SmithBand`,
`SmithDesign.Refusal` — which is what the window's status strip reads on every edit, and every pixel
comes out of `SmithPlotBuilder`, `SmithChartChrome` and `PlotComposer` in `CircuitRF.Render`, which is
what the window draws each frame with. `SmithCliVerbTests.TheVerbHoldsNoEvaluatorAndNoPlotOfItsOwn` is
a comment-stripped scan that keeps it that way: no `new Plot(`, no `Complex.Conjugate`, no
`Math.Log10`, no VSWR quotient.

**Three things had to move below the firewall for that to be true**, and each is the wiring the design
note promised rather than a rewrite:

- **`SmithPlotBuilder`, `SmithChartScene`, `SmithMarkerBridge` and `SmithOverlayResolver`** →
  `src/Render/Smith/`. They were already framework-free; they simply lived in `src/Ui`, which
  `src/Cli` cannot reference. *(`SmithOverlayResolver` is retired as of `smith-chart.md` §9.4: an
  overlay is now a Data Display trace config, restored through `PlotConfigLoader.LoadTrace` over a
  `SmithDocumentSources`, which are in the same folder and reached by the same route.)*
- **The DRAW half of `SmithGripperOverlay`** → `src/Render/Smith/SmithChartChrome.cs`. The gesture —
  hit test, press, drag, undo entry — stayed in `src/Ui` with the view model it calls back into.
  Without the split, a headless picture silently loses the arrowheads and the load-point frequency
  labels, which is exactly the failure `PlacedPlot.Overlay`'s own remark was added for.
- **`MatchValueFormat`** → `src/Design/Matching/`. A load point's frequency is LABELLED below the
  firewall and REPORTED above it, and two spellings of one frequency are two answers.

**The reading itself was extracted, not copied** (`SmithReadings`): VSWR and the conjugate-match
mismatch used to be computed in `SmithChartViewModel.ComputeStatusLine`, and a verb deriving them a
second time would be a second chance to get a sign, a conjugate or a square wrong in a quantity whose
wrong value looks entirely ordinary. The strip now formats what this type computes.

### 18.2 What it takes

The kind is inferred through `DocumentKinds.Classify`, exactly as `check`, `render` and `rail` infer
it, and **any other kind is a refusal BY KIND** — a `.csch` is refused as a schematic, not as an
unreadable file, because the caller very likely meant a run verb.

| Option | Meaning |
|---|---|
| `--at <freq>` | Evaluate here instead of the document's design frequency. Parsed by `MatchValueFormat.TryParseWithUnit`, the window's own frequency field's parser, so `2.4 GHz`, `2.4e9` and `900 MHz` all mean what they look like and a bare number is hertz. **Outside the generator table's span it is a refusal with the span in the sentence** (`R-smith2-5`) — the document's own rule, including its one-row exception: one row is one impedance, flat, and every frequency is legal against it. |
| `--set var=expr` | **Refused.** A `.csmith` states every quantity as a number in base SI and holds no expression scope, so there is nothing for an override to land on; `rail`'s own `--set` refusal is the precedent and §3.3's accepted-and-dropped defect is the reason. The remedy named is the standing one — once a document exists, the way to change it is to WRITE it. |
| `-o out.s1p` | The load Γ as Touchstone — §18.4. |
| `-o out.{svg,pdf,png}` | The chart — §18.3. |
| `--size WxH`, `--scale N` / `--dpi N`, `--background opaque\|transparent`, `--dark` | `plot`'s own picture options, spelling for spelling and refusal for refusal. |

**`smith` is the one verb that owns `--at` itself.** AUT-9's global `--at axis=value` narrows the axes
of a RESULT document and is taken before dispatch; this verb produces no `DataSet`, and its `--at`
says where to EVALUATE. `JsonRun.TakeFlags` therefore leaves the flag alone for this verb by name —
not by the shape of its value, because deciding on the presence of an `=` would make
`smith --at freq=2GHz` mean something different from `smith --at 2GHz`, silently.

**There are no per-primitive edit options** — no `--add-element`, no `--set-value`. The format is the
contract; `new` and `history` already follow the same rule.

### 18.3 The picture goes through `render`, not through a second renderer

The chart is built as the `Plot` the window builds — `SmithPlotBuilder.NewChartPlot` + `Fill`, which
brings the constant-Q arcs, the per-element trajectories, the swept band, the load points, the
conjugate targets, the resolved overlays and the document's markers in the window's own draw order —
placed by `RenderDataDisplay.Place`, and composed and encoded by `RenderDataDisplay.Emit`, which is
the same page, theme, composer and writer `render --data` and `plot` already go through.

The transient chrome rides `PlacedPlot.Overlay`, the field the GUI's own `PlotExporter` fills from
`IPlotOverlay.Draw`, with `SmithChromeState.None`: **nothing hovered and nothing dragged**, because
those are states a live pointer has and an exported picture does not.

**Nothing is narrowed in place.** There is no per-render layer or overlay selection here at all, which
is the cheapest possible answer to `TechnologyCache`'s defect — a shared instance narrowed once stays
narrowed for every later call in the same process, and that is invisible until the second call.

**An overlay row that does not resolve is a WARNING, not a refusal** (`R-smith8-2`): reference material
that is missing must not take the work down with it, and the chart still draws everything that did.

**There is no picture on stdout.** stdout is the result document, `--json` has to co-exist with the
write, and both blocks are present when it does — `result.render` for what was written and
`result.smith` for what was evaluated.

### 18.4 `-o out.s1p` — the band, or one point

The band is the generator table's own span and is always walked (owner instruction, 2026-09-19), so
the file holds exactly the locus the picture draws. A **single-row** table has no band — one row is
one impedance, flat — and the file then holds the one frequency the report is about. That is the only pair of
answers that cannot surprise anyone: a caller who asked for a band and got one point, or the reverse,
would have a file that plots as something they did not run.

**A load is a one-port**, so anything other than `.s1p` is refused rather than padded — a `.s2p` of one
reflection coefficient would be three quarters invented, and it would plot. The refusal is raised from
the ARGUMENT, before the document is read.

**No date comment**, deliberately: the same document written twice is the same bytes, so a caller can
diff two revisions of a matching network and see only what changed about the network.

### 18.5 What the report says, and why the table is the point

With no `-o` the verb prints the reading the window's status strip states — the design frequency, the
generator and load impedances, Γ in polar form, VSWR and the mismatch loss in dB — and then
**the walk, one row per node**, generator first and load last, each with its impedance and its Γ.

The reading alone answers *is it matched*. The table answers *where did it stop being matched*, which
is the question a caller has when the answer is no, and it is the half an exit code cannot carry. It
is also what a caller comparing two revisions of a network actually diffs.

`--json` carries all of it as `result.smith`, including `nodes[]` and, when there is one, `band`.
`vswr` and `mismatchDb` are **absent rather than large** where |Γ| ≥ 1: an active S2P or a
Z1P with negative R legitimately puts the load outside the unit circle, and a finite VSWR reported
there is a lie about a stability result.

### 18.6 Progress, cancellation and exit codes

`RunHost`'s `RunControl`, exactly as `em`, `render` and `rail` ride it. 0 on success; **1** on a
refusal, with the model's own sentence kept whole; **130** on a cancellation, which **writes nothing**
— the bytes are complete before anything reaches the filesystem.

### 18.7 The gate

`tests/Ui.Tests/Smith/SmithCliVerbTests.cs`. The verb run as a PROCESS writes the `.svg` an in-process
`SmithPlotBuilder` + `SmithChartChrome` + `PlotComposer` composition writes for the same document —
**byte-identical, with no exclusion applied at all**, not even the Skia `clipPath` counter the render
gates allow for. Its `.s1p` carries `SmithReadings`' own Γ and is reproducible run to run. Beside
them: the source scan, the three refusals by kind, `--at` against the span and its one-row exception,
the `--set` refusal, exit 130 writing nothing with its vacuity guard, and `--json` co-existing with the
picture.

## 19. `lvs` — does the artwork implement the drawing?

`brief-lvs-11-cli-verb.md`; `docs/design/lvs.md` §8.3.

```
circuitrf lvs <path> [--flat] [--flatten-cell <name>] [--testbench] [--no-reduce]
                     [--recognize] [--set var=expr] [--severity warning|error]
                     [--json] [-o report.txt]
```

It answers the one question a headless client cannot answer any other way: **the design was drawn
twice — as a netlist and as artwork — and do the two say the same thing?** An agent that authored a
`.clay` cannot look at the screen.

### 19.1 It owns no comparison, and that is the whole design

`src/Cli/Lvs.cs` is argument parsing, refusals, reporting and **one call** into `LvsRun.Run`
(R-lvs11-1a) — the same function the GUI panel calls, with the same arguments. §9's rule for the
authoring verbs, unchanged: *an operation that lives only in a verb is not a capability, and a verb
that re-implements one diverges from it silently.* A second extraction, partition, terminal
derivation or correspondence in `src/Cli` would mean a design passed headlessly and was refused when
somebody opened it, and nothing would report the drift. The gate is a comment-stripped source scan
over the whole of `src/Cli` for any of them, plus the assertion that `LvsRun.Run(` appears exactly
once — two calls would be two sets of defaults.

**It is its own verb and is NOT folded into `check`** (R-lvs11-1c, note R-lvs-54). §10's rule is
that `check` must be cheap enough to call after every edit and stops at elaboration; an LVS on a
real board is seconds rather than milliseconds, and a `check` that had become slow is a `check`
people stop running. What `check` *did* gain from that series is the terminal-map validation, which
is cheap and static and which the GUI enforces too.

### 19.2 One verb over the kinds it can answer for

The kind comes from the path through `DocumentKinds.Classify`, exactly as `check` and `render` infer
it (R-lvs11-2a).

| Path | Compared |
|---|---|
| a **cell folder** | its primary schematic against its primary layout — **the default unit** |
| a **workspace** | every cell holding both views; one holding a single view is reported at info and skipped |
| a **`.clay`** | its sibling schematic, in the cell folder that holds it |
| a **`.csch`** | its sibling layout, likewise |

A view file names the CELL, because the cell is the unit of comparison — so all four spellings reach
the same one call.

**A cell with only one of the two views is not an error** (R-lvs11-2b). It is the ordinary mid-design
state, and a verb that failed on it would be a verb nobody runs while a design is being drawn. A
workspace of nothing but land patterns therefore exits 0 and says, per cell, which view is missing.

**Any other kind is a refusal BY KIND** (R-lvs11-2c) — `render`'s own rule: the sentence names what
the path *is*, because "circuitRF cannot read this" and "circuitRF reads this and `lvs` does not
compare it" are different answers and only one of them says what to do next. A foreign extension
goes through `convert`'s own content classifier first, so a GDSII or Gerber file is named rather than
called unknown.

**`--set var=expr` lands in the design's own scope before elaboration**, exactly as §5 spells it for
every run verb. It is here because a design whose component values depend on a configured global has
more than one correct layout, and a caller must be able to say which. It reaches the SCHEMATIC side
and, there, the resolved parameter values the property pass compares: LVS reads its topology from
the drawing's own instances and nets rather than from the elaborated netlist, and the artwork is
already drawn.

**`--recognize` reads devices out of COPPER as well as out of instances, and it is OFF by default**
(`brief-lvs-14-recognition.md` R-lvs14-1d; `lvs.md` §4.1 tier 3). circuitRF's layout is
instance-bearing: a device is already a first-class object in the file, so the primary reading is
correspondence rather than recognition, and turning this on for a design circuitRF authored would
re-recognise devices it already knows, from geometry, less reliably than reading the instance that is
right there. It exists for artwork that carries no instances at all — a hand-drawn MMIC device, a
GDSII import whose hierarchy was flattened away, a Gerber board read back as polygons.

It is off per RUN and off per TECHNOLOGY, and both halves matter: the deck is a `DeviceRules` block
in the `.ctech`, and a process that declares none recognises nothing whatever the flag says, which is
not an error. A run that recognised anything reports `lvs.recognize.in-use` at info naming the
technology and the count, so a clean report is never mistaken for the stronger claim — recognition
answers *what does this copper look like*, never *is this the device the process actually makes*.
Every candidate it rejected is reported with its reason, and a recognised device carries no
designator, so it can only ever be matched structurally.

The deck itself is checked by `circuitrf check <tech.ctech>`, before any run: an unreadable region,
an unknown `Kind` (listing the real ones), a formula naming something neither measured nor declared,
and a cyclic constant are all technology problems, because a deck that fails at run time instead is a
deck that fails during the one operation the user wanted to succeed.

### 19.3 What it writes, and what it does not

**stdout is the result and the result is the report** (R-lvs11-3a/3b): a summary line per cell, then
the findings grouped by severity, each naming the designer's own objects — un-reduced, so a
collapsed four-finger device names all four — with the technology, the reduction mode and both
sides' counts on the face of it. Progress and refusals go to stderr.

`--json` projects `LvsRunResult` and **adds nothing** (R-lvs11-3c). Every finding travels as its
`Diagnostic`: a stable `lvs.` id and typed arguments, so a caller reads the layer, the coordinate or
the two values **without parsing the sentence back apart**. The id is the contract; the sentence is
not. The findings are in the document's own `diagnostics` array as well, which is where a caller
that does not care which cell produced what reads them; `result.lvs` carries the per-cell structure,
the marker box and the waiver state, which a flat array cannot.

**Each cell also carries a `turned` array** (brief LVS 16): every part on that cell's own layout
placed end for end, as `{refdes, instance, land1: [x, y], land2: [x, y], margin}` in DBU. It is
**uncapped**, where the `lvs.device.turned` findings stop at 20 per id, and it is everything a caller
needs to make the fix itself — the verb stays read-only: move the instance to `land1 + land2 − (x, y)`
and add 180° to its rotation, and each land then sits exactly where the other one was. A part inside
a placed cell is reported as a finding under its placement but is not in the parent's array, because
its index is that cell's; it is in the cell's own entry when the cell is compared on its own account,
which a workspace run does.

**`-o report.txt` is the only thing this verb ever writes, and with no `-o` it writes nothing at
all** (R-lvs11-3d). LVS is read-only on §10.1's terms, so it runs on a read-only tree and on a
workspace another process has open — asserted by mtime over a copy of a whole workspace, which
catches a re-save that happened to write identical bytes.

**It honours waivers and does not create them.** A waiver is a deliberate, reasoned act with a
sentence attached, written in the editor beside the thing being waived; a waived finding is still
reported here and merely not counted.

### 19.4 Exit codes

**0** when nothing at or above `--severity` was found, **1** otherwise; the default threshold is
`error`. A run holding warnings and no errors **exits 0 and still reports them** — §10's rule for its
reason: the alternative makes the exit code useless in CI. A refusal exits 1 with the producing
component's own sentence and carries **no `lvs` payload at all**, so a run that could not happen
cannot be read as one that concluded something with a note attached. **130** on a cancellation,
through `RunHost`'s `RunControl` like `em` and `render`, and nothing is written.

**There is no 2.** Nothing here converges; LVS runs no solve, ever.

### 19.5 `serve`

The tool falls out of the verb with no second implementation, and its result is the `--json`
projection (R-lvs11-5a/5b). This is the surface the capability matters most on, for the reason at the
top of this section.

### 19.6 The gate

`tests/Ui.Tests/Lvs/LvsCliVerbTests.cs`. The one that matters is the first: the verb run as a
PROCESS reports exactly what an in-process `LvsRun.Run` reports — same findings, same ids, same
objects, same order — which is what makes *"a design that passes headlessly passes when it is
opened"* true rather than hoped for. Beside it: the correct board clean and the six-fault board
naming all six, warnings-only exiting 0 with `--severity warning` flipping it to 1 and changing
nothing else, all four document kinds resolving to one comparison, the single-view cell skipped at
info, three refusals by kind, the mtime proof that nothing is written, 130 with its vacuity guard,
the typed arguments read as values, `--set` distinguishing three answers on one board, the source
scan, and `serve` returning the identical payload.

**One measured correction to the brief.** Its gate 11 asks that `--no-reduce` and `--flat` *change
the counts* on the correct board. They do not, and cannot: nothing on either example board collapses
and the MMIC's only sub-cells are leaf parts, so both flags leave every count identical. What they
do change is what the run SAYS it did — the mode is on the face of the human report and of the
document, both sides, every run — and that is what the gate pins instead.

## 20. The installed CLI — the `circuitRF` executable is the command line

**Every release through 1.0.0-beta.32 shipped no command-line driver at all** (AUT-13). All three
packaging scripts published `src/Ui` only, `src/Ui` did not reference `src/Cli`, and `Program.Main`
handled no verb — so `circuitRF serve` opened a window, and on Linux, where `/usr/bin/circuitrf` and
`~/.local/bin/circuitrf` link to the application, the documented `circuitrf sparam amp.cnl` started
the GUI and handed it `sparam` as a file to open. Every CLI gate stayed green throughout, because
every one of them launches `src/Cli/bin`.

### 20.1 One executable, dispatching on its first argument

The owner's two requirements — no meaningful size, and the command is called `circuitRF` — point at
one design. Each application is a self-contained single-file publish of 130–143 MB carrying its own
runtime, Avalonia and Skia; a second self-contained executable would be another copy of all of it,
×15 artifacts, for code that is ~0.67 MB. And it could not be called `circuitRF` anyway: it would sit
beside the GUI's `circuitRF.exe`, or inside the same `Contents/MacOS/`, on file systems that are
case-insensitive by default.

So `src/Ui/Program.cs`'s `Main` begins:

```csharp
if (args.Length > 0 && CircuitRF.Cli.CliEntry.IsVerb(args[0]))
    Environment.Exit(CircuitRF.Cli.CliEntry.Run(args));
```

**First, and before every line that was already there**, each of which is wrong for a CLI call and
fails silently: `CrashReporter.Install` (a CLI exit is not a GUI session), `AppRelaunch` and
`ReleaseNotesGate` (a CLI call is not a launch), `UpdateStartup.RunBeforeUi` (it applies a staged
update and HANDS THE PROCESS OVER — `execv` on Linux — so `circuitrf check` could become a GUI launch of
the new version), and the Windows mutex / Linux lock / socket (a CLI call made while the window is open
would take the "not first" branch and forward its arguments to the window). `ExternalWorkerPolicy` is
left out as well, so the installed CLI behaves as `src/Cli`'s own executable does. Avalonia is never
initialised on that path: no `AppBuilder`, no `NSApplication`, no Dock icon. Only circuitRF's entry
point does this — harmonicaRF and wBond have no CLI (out of AUT-13's scope).

**`IsVerb` is answered by the switch `Run` dispatches on** (`CliEntry.Dispatch`), plus `--version`,
the one first argument that is not a verb. No list of verbs exists anywhere else and `src/Ui` names
none. It compares the WHOLE argument, lower-cased as `Run` does — never its file name — which is what
keeps opening a document untouched: a double-click delivers a full path (Windows `"%1"`, Linux `%F`)
or, on macOS, an Apple Event with no argument at all, so a file literally named `check` arrives as
`/home/x/check` and is not a verb. The corollary is that **the verb must come first** on the installed
executable; `circuitrf --json check .` reaches the GUI. (`JsonRun.TakeFlags` already assumed the verb
leads — `smith`'s own `--at` is decided on `args[0]`.)

`--version` prints `JsonRun.Version()` — the assembly's `InformationalVersion`, stamped from the
`VERSION` file — and nothing else, and exits 0. It is not a document: a caller asking which build
answered has not yet decided how to talk to it.

### 20.2 A library, not a reference to the executable

`src/Ui` referencing `src/Cli` directly builds and publishes — measured — but the publish tree then
carries a framework-dependent `CircuitRF.Cli` apphost with its `.deps.json` and `.runtimeconfig.json`
beside `circuitRF`: three files in every installer that cannot run there, since no shared runtime is
installed. `ValidateExecutableReferencesMatchSelfContained=false` would silence NETSDK1150/1151 without
removing them. So the verbs are `src/Cli.Verbs/CircuitRF.Cli.Verbs.csproj`, which compiles
`src/Cli/**/*.cs` **where they stand** (minus `Program.cs`, `bin/`, `obj/`). Moving them would have
broken every source scan that names one (`Authoring.cs`, `History.cs`, the second-`CnlWriter.Write`
scan), dozens of document references, and `dotnet run --project src/Cli`, which needs exactly one
project file in that folder. The `InternalsVisibleTo` grants moved with the code: `CircuitRF.Render`
now grants `CircuitRF.Cli.Verbs`, and the verbs grant `CircuitRF.Ui.Tests` and `CliSmoke`.

Measured growth of the published executable, every RID, before → after: **+668,818 to +677,648
bytes**, which is `CircuitRF.Cli.Verbs.dll` (668,160 bytes) and nothing else; no file was added to any
publish tree. harmonicaRF and wBond carry the same DLL unused, because the three applications are one
`.csproj`.

**What the application's module initializers change.** `src/Ui` has seven `[ModuleInitializer]`s and
they run before `Main`, so the installed CLI gets them and `CircuitRF.Cli.dll` does not. Each only
installs a seam — the PCell generator source for `CellPins`, the Verilog-A cache directories and
preferred compiler, the git path, a Data Display note sink (a no-op until `CrashReporter` is
installed), a wBond default, and a `NumericUpDown` property hook. Measured: `check --json` over all ten
shipped examples and `lvs --json` over three are byte-identical through both doors. The one seam that
can change an ANSWER is the PCell generator source, and only for a PCell cell written before pins were
persisted — there the installed CLI resolves its pins exactly as the GUI does, which is the better of
the two answers. The Verilog-A compiler and the git executable follow the user's GUI preference when
it is set.

### 20.3 Windows: the launcher stub, and `circuitRF.com`

Two routes reach the CLI on Windows, and `packaging/windows/stub/circuitrf-stub.c` serves both.

1. **A program spawning `circuitRF.exe` with pipes** — every MCP client. In a per-user install that
   is the stub, which starts `app-<version>\circuitRF.exe`. It now sets `STARTF_USESTDHANDLES` with its
   own three handles (inheriting them makes them VALID in the child; only the flag makes them its
   stdio), and it raises its `MessageBox` only when it has no pipe, file or console to write the
   reason to — a modal dialog on a headless agent waits for a click nobody makes.
2. **A person typing `circuitrf` in cmd or PowerShell.** A GUI-subsystem executable gets no console
   and neither shell waits for it. So the same source is compiled a second time with `-DCRF_CONSOLE`
   for the CONSOLE subsystem and installed as `circuitRF.com` beside `circuitRF.exe` in both scopes.
   `PATHEXT` puts `.COM` before `.EXE`, so a typed `circuitrf` reaches it, while shortcuts, every
   association (`TargetFile="CircuitRfExe"`) and `CreateProcess("circuitrf")` — which appends `.exe`
   — still reach the `.exe`. With no `current` file (a per-machine install) it starts the
   `circuitRF.exe` beside it. It ties the child to itself with a kill-on-close job, so Ctrl+C in the
   console does not leave a solve running in the background; `SILENT_BREAKAWAY_OK` limits that to
   the direct child, so the GUI's own Relaunch successor and the device workers are never killed
   with it. Both stub builders read the subsystem back out of the PE and refuse a wrong one — 2 for
   the stub, 3 for the `.com`.

Both MSIs add `INSTALLFOLDER` to `PATH` (`Part="last"`): the user's for per-user, the system's for
per-machine, removed on uninstall. It is the folder that never changes across updates — never an
`app-<version>` folder.

### 20.4 The packaging gate: run what was built

`tools/CliSmoke` is the gate that was missing for 32 releases, and every packaging script runs it
against the tree that goes into its installers — `Contents/MacOS/circuitRF` in the bundle about to be
imaged, `publish/linux-*/circuitRF`, and on Windows the stub laid out as a per-user install
(stub + `current` + a copy of the publish tree), because that is the pipe route MCP clients take.
It fails the build unless `--version` prints exactly the `VERSION` file, `reference --json` exits 0 and
parses, and `serve` answers `initialize` with `serverInfo`, answers `tools/list` with exactly the tools
`ToolCatalog` defines plus `batch` — compared with the catalogue, never a count — and exits 0 when stdin
closes.

An architecture the build machine cannot execute is reported as **NOT SMOKE-TESTED** and fails the run
at the end unless `CRF_ALLOW_UNSMOKED=1`: x64 on Apple Silicon needs Rosetta, the other Linux
architecture needs qemu's binfmt handler, and Windows on ARM runs all three. The `.com` cannot be
checked through a pipe — a pipe is the one condition it does not exist for — so it is checked by hand
from a real console at a phase boundary (`packaging/RESOLVED.md`).

Source-tree halves, in `tests/Ui.Tests`: `Cli/InstalledCliTests.cs` (dispatch order, `IsVerb` over every
shape a double-click delivers, `check --json` byte identity through the application executable, the
update guard) and three text gates in `PackagingScriptTests` (the smoke step in all three scripts,
double-click routes still targeting the `.exe` and `%F`, the `.com`'s subsystem demand).

## 21. `impedance` — Trace Impedance Analysis, headless

`circuitrf impedance <layout> [--target 50] [--tol 10] [--warn 20] [--max-freq 6GHz] [--layers "A,B"] [--max-width <um>] [--severity warning|fail] [-o report.pdf]`

**It owns no analysis and no page.** Every number is `TraceImpedanceAnalysis.AnalyzeFile`
(`src/Design/Layout/Em`) and every pixel of the PDF is `TraceImpedanceReportDocument.Pdf`
(`src/Render/Renderers`) — the two calls the layout editor's Impedance Analysis dialog makes — so the
headless report and the exported one are the same document. `src/Cli/Impedance.cs` is argument
parsing, the layer-name lookup, refusals and reporting.

- **Input.** A `.clay`, or a cell folder (its primary layout view). Anything else is refused by KIND
  (`impedance.path.not-a-layout`).
- **Layers by NAME, refused with the names that exist.** `--layers` resolves against the layout's own
  technology; a name that is not a stackup-bound copper layer is `impedance.layers.unknown` listing the
  copper layers. A review that quietly analysed fewer layers than it was asked for reads exactly like
  one that found nothing wrong.
- **Units.** stdout and the PDF speak the layout's own `DisplayUnit`; `--json` is µm throughout, so a
  script reads one unit whatever the layout says.
- **Three tiers** (brief-impedance-1): every trace is PASS / WARN / FAIL and every finding a warning or a
  fail, graded in `TraceImpedanceAnalysis` and nowhere else. `--max-freq` requires its unit, the rule
  every CLI frequency follows.
- **Exit codes**, on `check`'s convention. 0 nothing fails (warnings are reported and still exit 0); 1 one
  fails, is unsolved, or the run is refused — and with `--severity warning`, one warns; 130 cancelled.
  **A cancelled run writes the layers that finished** (owner, 2026-09-25) — the analysis is layer by
  layer so that stopping a long run keeps what it has done. This is the one verb where a cancellation
  writes anything, and the report's first page says it was cancelled.
- **The findings are the report, not diagnostics.** Every `impedance.` id is a refusal of the verb's
  own; a failing trace is the verb working.
- MCP: the `impedance` tool, single-mode, `path` positional (`ToolCatalog`); `path` is optional so the
  calculator below is reachable as the same tool.

### 21.1 The line calculator — `impedance --tech <t> --layer <name> (--width … | --z0 …) [--gap g] [--freq f]` (AA-3)

A MODE of `impedance`, not a verb (owner's answer to brief-agent-authoring-overview AA-3): that verb owns
the cross-section, and the question is the same one asked before the line exists. `--tech` selects it.
**It owns no analysis**: every number is `LineCalculator.Calculate` (`src/Design/Layout/Em`), and
`src/Cli/ImpedanceLine.cs` is the technology lookup, refusals and reporting.

- **Two columns, neither a second copy.** The model column is an MLIN *elaborated* — a one-instance test
  bench with `MicrostripSubstrateInjection.BuildOverrides` for the layer's conductor, through the
  `Elaborator`, asked `MicrostripLineModel.LineParameters`, which is what its `Stamp` calls (the method
  was split out of `Stamp` for this, with the sum order kept so stamps are bit-identical). The
  cross-section column is `TraceImpedanceAnalysis.Analyze` on a layout built in memory: one straight line
  20 widths long, and for `--gap` ground strips 10·(W+G) wide either side, selected by a pick so the
  strips are copper and never traces under review. Gate: `tests/Ui.Tests/Em/LineCalculatorTests.cs` —
  the model equals an elaborated `.cnl` MLIN field for field with `==`, and the cross-section equals
  `AnalyzeFile` on a `.clay` with the line drawn elsewhere and at another length.
- **Synthesis per column.** Model: `HammerstadJensen.SynthesizeWidth` (bisection on the static
  `Compute`, 60 halvings over W/h ∈ [0.01, 100]) — the MLIN parameter editor's Z0 field calls the same
  function. Cross-section: a bracket grown ×1.6 from the model's width (or H), then false position on
  ln W with the Illinois step, to **one DBU** (1 nm); at most 60 solves. Both target the STATIC Z0.
- **`--tech`.** A `.ctech`; a `.clay` by the layout's own resolution; any other file or folder by the
  walk a schematic there uses (nearest `.cws`, its default technology) — because that is the substrate an
  MLIN there elaborates on; else a shipped id. Nothing resolving is `impedance.tech.none`/`not-found`.
- **Refused, not ignored:** a layout path with `--tech` (`impedance.line.path-not-used`), any
  layout-review flag (`impedance.line.option-not-used`), `--z0`/`--gap`/`--freq` without `--tech`
  (`impedance.line.tech-required`), a layer the technology lacks (`impedance.layers.unknown`, listing).
- **`--json`** is `impedanceLine`, lengths in µm, loss in dB/mm. Exit 0 when every row has an answer in
  every column it can have one in (a coplanar line has no model, which is said, not failed), 1 when a row
  has none or the calculator is refused; a column that could not answer is `impedance.line.unanswered`.

## 22. `solver` — the 3D solver install assistant, headless

`circuitrf solver list` · `circuitrf solver install <palace|gmsh|openems> [--version <v>] [--yes]` ·
`circuitrf solver remove <palace|gmsh|openems> [--version <v>] [--yes]` · `circuitrf solver remove --all [--yes]`

(brief-em3d-24 R-em3d24-7; `em-3d.md` §7.2's "a build machine installs the same way the GUI does".)
**One verb with nouns, on `history`'s pattern** (owner decision D1); brief-em3d-25 added `remove`.

**It owns no install logic.** `list` is `SolverStatus.Of` — the call each Settings ▸ Solvers row makes —
and `install` is `SolverInstaller.Consent` then `SolverInstaller.Install` (`src/Design/Em3d/Install`),
which the Settings row and a 3D run's *Install …* action call too. `src/Cli/Solver.cs` is argument
parsing, the consent refusal, progress on stderr and reporting; a comment-stripped source scan in
`SolverInstallTests` holds it (and the GUI runner) to that.

- **`list --json` is `solvers`**: per tool its id, state, path, version, route, whether the version is
  validated, the install command when one would work, and each probed capability with a stable id —
  `driven`, `wave-ports`, `eigenmode`. That id is how an agent learns, before writing a 3D setup,
  whether an eigenmode or wave-port run can happen on this machine; the MCP `solver` tool is this.
- **Nothing is fetched without `--yes`.** Without it the verb prints the consent text on stderr — the
  program and version, every upstream URL and how each is checked, where it installs, what it cost when
  measured and on what machine, the ParMETIS sentence for Palace — and exits 1
  (`solver.install.consent-required`). It creates nothing, not even the state directory.
- **A missing prerequisite is a refusal before any download**, naming the one command the user runs for
  the detected distribution (`solver.install.refused`). circuitRF never runs `sudo`.
- **Progress** is one stderr row per stage, keyed on the stage's name, and the stage's live figure (bytes
  downloaded, the Spack package being built) at most every two seconds. A Palace build prints
  *package k of N*, N from Spack's own plan.
- **Ctrl-C cancels cleanly**: the running step's process tree is stopped and nothing is published. A
  second Ctrl-C ends the process at once.
- **Exit codes.** 0 installed or already installed (the program's path on stdout and in `--json`
  `outputs`); 1 refused or failed, with the report on stderr (`solver.install.failed` carries `step` and
  `log`); 130 cancelled.
- `list` exits 0 whatever it finds; each tool's line says found or not, the route, the version, whether
  it is validated, each capability probe, and — where installing would change what a run finds — the
  command that installs it here.
  It also lists each home circuitRF installed, with the command that removes it.

**`remove` (brief-em3d-25)** is `SolverUninstaller.PlanOne`/`PlanAll` then `SolverUninstaller.Remove` —
what each Settings row's *Uninstall …*, *Remove all 3D solvers* and the Windows Apps-list uninstall call.

- **Only what circuitRF installed.** A tool found any other way is `solver.remove.refused`, naming where
  it was found and that circuitRF did not install it. Several installed versions and no `--version` is
  the same refusal, listing them.
- **Nothing is removed without `--yes`.** Without it the verb prints the confirmation on stderr — each
  home, its size measured now, that removal is permanent, what reinstalling cost on this computer, that
  documents are untouched — and exits 1 (`solver.remove.consent-required`). With `--yes` the
  confirmation is not repeated; the report names what went.
- **Refused while in use** — by a 3D run in any process (each holds an `in-use.<pid>` lock in the home;
  a dead pid's lock is ignored and deleted) or by an install of that tool. `--all` is all or nothing.
- **Exit codes.** 0 removed (each home in `--json` `outputs` as `removed`); 1 refused, or
  `solver.remove.incomplete` when files could not be deleted — the home is already out of discovery's
  sight (renamed `<home>.removing`), the files are listed by path, and the next `remove` finishes it.
- **There is no `circuitrf uninstall`** (R-em3d25-4c): a build machine runs `solver remove --all --yes`
  and then the platform's own uninstall.

**Palace on Windows (brief-em3d-26).** Both nouns reach the Linux subsystem through the same functions
the Settings row calls, so the verb gained no code of its own beyond one call:
`SolverInstallPlan.For` picks the recipe and the installer — natively everywhere else, and for Palace on
Windows brief 24's LINUX recipe run inside a WSL 2 distribution (the location setting's, else the
default one). A subsystem precondition — the feature not enabled, virtualization off, no distribution,
a WSL 1 one, a distribution that does not start — is `solver.install.refused` carrying the one step that
fixes it; nothing starts. `list` names the distribution a Palace was found in, offers `install` "inside
the Linux subsystem", and lists a home installed there by its mirrored record, which it reads without
starting the subsystem. `remove` of such a home renames and deletes it inside the distribution and then
the mirror; it is `solver.remove.refused` while the distribution cannot start. Headless there is no
location preference, so the verb always acts as *Automatic*.

## 23. Generated cells — every geometry verb rebuilds them, or refuses

`brief-generated-cells-2-headless-regeneration.md`. A placed PCell — a built-in microstrip element, an
`smt:` land pattern, a kit's script-drawn cell — is an instance of a cell folder under the workspace's
`.generated-cells/`, which is a **cache**: nothing commits it, and an unpacked archive, a
`history clone`, a CI checkout or a workspace nobody has opened since a generator changed does not have
it. Until this, a headless verb resolved such an instance only if a GUI session had already written the
folder; otherwise the flatten dropped it with a warning and the verb went on to give a complete,
plausible answer for a board with parts missing — a rail with no land patterns, an LVS against missing
devices, a Gerber with no pads.

### 23.1 One rebuild, the application's

Every verb that USES layout geometry — `render`, `check`, `lvs`, `explain --extents`, `rail`,
`impedance`, `em`, `convert` from a `.clay`, `netlist` on a board — first calls
`GeneratedCellsRun.Prepare` (`src/Design/Layout/PCells`), which walks what the layout PLACES (and the
hierarchy under it, never the rest of the workspace) and rebuilds each placed generated cell from the
placing layout's snapshot through `GeneratedCellsLifecycle.Rebuild` — the step the application's own
workspace open takes, so an open document and a CLI run can never generate different artwork.
`src/Cli/GeneratedCells.cs` owns no generation: it is the flag, the choice of target, and the sentences.

| Verb | Where a rebuilt cell goes |
|---|---|
| `check`, `explain`, `render`, `lvs` | **in memory, for the run** (`GeneratedCellOverlay`). These promise to write nothing, and they still write nothing. |
| `rail`, `impedance`, `em`, `convert`, `netlist` | the workspace's `.generated-cells/`, as the application would, so the next run is free — **never on a read-only workspace** (SL2), which falls back to memory. |

**A headless run edits no document.** The application answers a STALE cell (its generator or
technology changed, so its snapshot now builds under a new name) by repointing the instances and saving
the layout; a CLI run redirects the stale folder to the rebuilt one for its own duration instead, and
never prunes. The artwork is the same; the `.clay` another process may have open is untouched.

### 23.2 What cannot be rebuilt is a refusal

A placed generated cell that is **not on disk and cannot be rebuilt** — no snapshot to rebuild from, a
generator nothing provides, a kit that is not allowed to run or has no interpreter, a generator that
throws — makes every verb above exit **1** with `cli.generated-cell.unbuildable`, naming the cell, its
generator, the layout that places it and why. Nothing is run and nothing is written: a number computed
without a part cannot show a placeholder the way a window can. `check` reports the same thing as the
ERROR finding `check.generated-cell.unbuildable`, so its exit code carries it; `lvs` counts it as an
error for that cell and compares the rest.

A cell that IS on disk but whose generator cannot be asked whether it is current is used as it is, with
a `cli.generated-cell.note` — the application's open does the same.

The sentence is `GeneratedCellsLifecycle.CouldNotRebuild`'s, and the application's Messages line says
the same one for the same cell (it keeps its placeholder), so a user who has seen one has read the other.

### 23.3 A kit's scripts run only under trust — `--trust-kit`

A kit's generator is a script, and circuitRF runs one only with consent (`PCellTrustStore`). A headless
run cannot ask, so it honours:

1. **a decision already recorded on this machine** — read from the per-user `preferences.json` by the
   key the Settings store writes, and never written back; and
2. **`--trust-kit <dir>`**, which allows the kit whose generator manifest is in `<dir>` for THIS run
   only, and is taken before dispatch like `--kits`, so every verb has it.

3. **under `serve`, the person's answer to circuitRF's own question** — below.

Anything else is a refusal naming each kit that was not allowed and the exact flag that would allow it
(`cli.generated-cell.kit-not-allowed`, a note beside the refusal).

**`serve` asks the person, never the agent.** When a client declares the `elicitation` capability at
`initialize`, and a call needs a MISSING cell from a kit nobody on this machine has decided about, the
server sends `elicitation/create` and the run waits for the answer. The client shows the question to its
user; the agent driving the tools never answers it, and no tool argument can. Its one field, `allow`,
is a required boolean that defaults to false, so a form accepted without being read grants nothing.

- **Allow** holds for the life of the server, and one question covers every later call. **Decline** is
  remembered too, so a session is not nagged; **a dismissal** is not a decision, and the next call that
  needs the kit asks again.
- **Nothing is written.** A grant given to one agent session is not a machine-wide one: it never reaches
  `preferences.json`, and circuitRF asks again in a later session.
- **Never asked:** a kit recorded on this machine as NOT allowed (the person already refused it, and a
  headless question must not be a way round that); a cell that is on disk (it is used as it is, and a
  question about artwork that is already there would train the reflexive "Allow"); a client that did not
  declare the capability, which gets the refusal it always got — worded for a client, since it cannot
  pass a flag.
- The request is the only one the server ever sends. Its answer arrives on the reader thread and is
  handed to the waiting run (`JsonRpc.Request`/`Deliver`); a cancelled call or a disconnect releases it. **A flag, not a prompt and not an
environment variable**: a grant has to be visible in the command that used it, and a variable left
exported would quietly grant every later run in that shell. The spelling names the directory because
consent is keyed by the directory — the same key the application records.

### 23.4 The gate

`tests/Ui.Tests/Cli/GeneratedCellsHeadlessTests.cs` runs the CLI as a process on a board whose copper is
cut under one land-pattern pad, so the part is load-bearing: `rail` gives the same answer with
`.generated-cells` deleted as with it present and rewrites the folder byte for byte; `render`, `check`
and `lvs` agree with and without it and leave it deleted; an unbuildable cell is a refusal and a check
error; a kit's cell is refused without trust, naming the kit and the flag, and with `--trust-kit` the
folder written is the application's placement's, byte for byte. Through `serve`, a client that can ask
is asked once and an Allow rebuilds the cell; a Decline is a refusal and is not asked again; a client
that cannot ask is never sent the question.

## 24. `opt` — the Optimizer window's run, headless

**brief-tuneopt-11.** `circuitrf opt <path.csch|path.cnl>` runs the design's `tune`, `goal` and
`optimize` lines (`reference tuning`, `goals`, `optimizers`) and reports what it found. It follows the
run-verb anatomy (§3): parse, read, run, report, export.

### 24.1 It owns no optimization

The run is `OptimizationRun` (`src/Design/Optimization`), the object the Optimizer panel drives, over the
`PreparedCircuit` Simulate prepares — `FromSchematic` for a `.csch`, `FromFile` for a `.cnl`. The best
point's full results come from `CircuitEvaluation.Evaluate`, as the panel's finish re-evaluates it.
`src/Cli/Optimize.cs` is argument parsing, the flag overrides, the two narrowing flags, reporting and the
one opt-in write. The gate `OptParityTests` runs the verb and the panel's headless view model on the `Optimization`
example's L-section (TO-12), with the algorithm and seed its schematic saves, and compares best values,
cost and evaluation count exactly.

### 24.2 Flags override the file for this run only

`--algorithm`, `--max-iter`, `--max-evals`, `--time` (seconds, or a number and `s`/`ms`/`min`/`h`),
`--cost lsq|minimax`, `--analyses goals|all`, `--parallel`, `--seed` replace the `optimize` line's keys
for this run; `--set` overrides a global as every run verb does. `--vars key,key` and `--goals name,name`
narrow to a subset of the file's opt-enabled entries and enabled goals — a name outside that set is a
refusal listing the set, and **a whole complex key (`--vars ZL`) is a refusal naming its four parts**,
since a complex value is optimized by its parts (overview D18). A narrowed-out entry keeps its range,
because a complex value's ranges hold together whatever the flags. `--snap` is TO-8's snap and polish at
the end; `--sensitivity` adds a sensitivity pass at the best point (n more evaluations, never unasked);
`--show-iterations` reports every iteration as well (§24.3). Preferred values snap to the SHIPPED ladders: the
user's own live in the GUI's preferences, which a headless run has none of. `--corners all|none|a,b` replaces the
optimize line's `corners=` (brief-yield-7): every goal must then be met at the nominal and at each corner at once,
one evaluation per corner per point; a statistical corner replays the `<design>.yield.npy` beside the design when it
is the run the corner names (`yield.md` §11).

**Nothing is written to the design's values** (overview D14). The values are REPORTED as the text the
schematic would hold, so an agent that wants them writes the file. **`--save-preset <name>`** is the one
write: the best values as a preset in a `.csch`'s tuning block — `TuningPresets.LockIn`, the Optimizer's
own Lock in — after a `BeforeBatch` checkpoint when the workspace keeps a history; the file goes back
through its own persistence, so every other byte is unchanged (gated). A `.cnl` is refused: the preset is
a line the caller adds. A preset name Lock in would refuse is refused before the run, not after it.

### 24.3 Output and exit codes

stdout: the finish reason, the best cost, a variables table (key, start, best, min, max, railed — a part
of a complex value is its own row, followed by one line per value, `ZL  80+0j Ohm → 107.551763917658-48.0049696570421j Ohm` — the
text Push would write, at its full precision) and
a goals table (name, met, value, where on the axis, margin — and, across corners, the `binding` corner, whose
values every other column is; `Corners:` above it names the points and what one costs). **A met goal reports its TIGHTEST point** —
the one closest to its limit — and its margin, the slack there in the expression's own unit; an unmet
goal reports its worst point and −(violation). `--json` carries the same as `result.optimize` plus the
snap and the sensitivity when they ran, and — for a part — the `whole` value beside it, as `explain
--tunables` does. Across corners each goal carries `corner` (the binding one) and `perCorner` (met, value, at and
margin at each), and the report `corners` and `evaluationsPerPoint`.

**The final result is the default, and the whole default** (owner decision, 2026-10-07): an optimization
of a few hundred iterations would otherwise hand a script or an agent hundreds of lines it did not ask
for. `--show-iterations` adds each iteration — a line on stderr
(`iter 12 · 140 evals · best cost 0.0123 · goals met 1/2 · 3 infeasible`) and an entry in
`result.optimize.perIteration` (iteration, evaluations, best cost, goals met, failures, infeasible, stage,
the best values so far). The document carries no cubes: `-o`
writes them (`.npy` only — the one format that keeps the results and the `opt` group apart), stamped with
the tuned-values provenance.

Exit: **0** every enabled goal met · **3** finished with a goal unmet · **1** refused (before the run, or
a goal that cannot be scored at the start point) · **2** no evaluation converged · **130** cancelled,
writing nothing (§7).

### 24.4 Over the protocol

`run analysis=optimize` is the same verb (§11.1): every flag is an argument (`maxIter`, `savePreset`,
`showIterations` …). It returns the final result only; with `showIterations` it returns `perIteration` too
and sends one `notifications/progress` per iteration, carrying the line stderr prints — **a progress
token alone does not turn them on**, because the default is the result and nothing else. Total is left out, because an Auto run's stages and a stall make the iteration count
an upper bound rather than a denominator. Cancellation is the existing path: the token reaches
`OptimizationRun`, which abandons the run, and the verb answers 130. A long run does not hold the
writer between notifications: `JsonRpc` takes its lock per frame, so the progress path costs one frame
write per iteration. The server's `instructions` carry a five-step walk-through beside the end-to-end
example (§11.3c).

### 24.5 `check` and `explain`

`check` reports an `optimize` setup that would refuse at run time — nothing to optimize, no enabled goal,
three parts of one complex value, an algorithm this build lacks, a cost form the algorithm refuses — in
the run's own words, by asking `OptimizationRun.Create` (which evaluates nothing). It asks only when the
tuning rules found no error, since the run refuses on the first of those itself.
`explain --analysis` says, per analysis, whether an optimization runs it under `analyses=goals`, under
`analyses=all`, and which scope the optimize line chose — through `OptimizationRun.AnalysesUnder`, the
promotion an evaluation applies — and, when the line names corners, the points each candidate is evaluated at and
what one point costs (`optimizeAt`; `OptimizationRun.EvaluationPointsOf`).

### 24.6 The gate

`tests/Ui.Tests/Optimization/OptCliTests.cs`: `OptCliVerbTests` (the verb as a process — the L-section
meets its goal at the analytic L and C, the unreachable pad exits 3 naming its goal, `--save-preset` adds
one preset and nothing else byte for byte, a complex load prints its whole best value and `--vars ZL`
refuses naming the parts), `OptParityTests`, `OptMcpTests` (the protocol returns the verb's object,
progress arrives, a cancelled call answers 130 with no outputs) and `OptReferenceTests` (the goals page is
generated from the schema and the template catalog).

## 25. `yield` — Monte Carlo and yield, headless

**brief-yield-5.** `circuitrf yield mc|estimate|trial|corners <path.csch|path.cnl>` runs the tolerances on the
design's `tune` lines, its kit's distribution calls and its `statistics` line (`reference statistics`)
and reports the spread, the yield or one trial. It follows the run-verb anatomy (§3). **One verb with
nouns** — the `new`/`history` rule: `mc` (the spread alone, every enabled goal scored, no target),
`estimate` (pass/fail against the `use=yield|both` goals), `trial` (one trial re-run alone), `corners` (every
enabled corner, §25.7). YA-11 adds `center` as a noun, not a verb. It landed **before any UI** (yield overview D12), so an
agent can set up and run a yield with nothing but the MCP tools.

### 25.1 It owns no statistics

The run is `StatisticalRun` (`src/Design/Statistics`), the object the Yield panel drives, over the
`PreparedCircuit` Simulate prepares; the result file is the one that run writes, at
`StatisticalRun.ResultPathFor` — `<design>.yield.npy`, never `run.npy` — unless `-o` moves it. The setup
is handed to the run only when a flag changed it, so a plain run IS the in-process one: `YieldParityTests`
compares the verb's `.npy` with `StatisticalRun.Run`'s for the same file and seed **byte for byte**.
`src/Cli/Yield.cs` is argument parsing, the flag overrides, the two narrowing flags, reporting and the
two opt-in writes. The verb's noun and flag tables (`Yield.Nouns`, `Yield.Flags`) are what `reference
statistics` renders, so a flag added there is documented with nothing else to edit.

### 25.2 Flags override the file for this run only

`--trials`, `--seed`, `--sampling`, `--target p%`, `--confidence p%`, `--autostop`, `--nonconverged`,
`--save`, `--process 0|1`, `--mismatch 0|1`, `--sigma-scale`, `--parallel`, `--analyses goals|all`
replace the `statistics` line's keys; `--set` overrides a global as every run verb does. `--vars k,k`
draws only those statistical entries (the rest keep their distribution and stay at nominal, as `stat=0`
keeps them, and a `correlate` line naming an entry left out goes with it) and `--goals g,g` scores only those
enabled goals; a name outside the set is a refusal
listing it. **`--target` and `--autostop` on `mc` are refused**, not ignored: a Monte Carlo has no target,
and a flag that silently does nothing is a run answering a different question. Settings the run itself
refuses (`lhs` with auto-stop) are refused in its own words, because the run validates the setup.

`--trial n` re-runs ONE trial — the same draws and the same evaluation as inside a full run
(`StatisticalRun.EvaluateTrial`) — and prints what it drew (`R1.R = 979.9 Ohm`) and how it scored; `-o`
then writes that trial's analysis results. `trial` needs it; `mc` and `estimate` take it too, choosing
which goals score it — and `trial` itself scores as the design's own run would: a yield when the design has a
yield goal, a Monte Carlo otherwise, so `yield trial 5` and `yield mc --trial 5` agree on a Monte Carlo design. **Nothing is written to the design's values** (D12): `--save-preset <name>` adds the
trial's values as a preset (`TuningPresets.LockIn`), and `--save-corner <name>` adds one statistical
corner, `corner <name> trial=n seed=s sampling=m trials=N`, naming the run the trial came from — both to
a `.csch` only, both with `--trial`, both after a `BeforeBatch` checkpoint exactly as `opt --save-preset`
takes one (`Optimize.CheckpointBefore`), every other byte as the file's persistence writes it (gated). A
`.cnl` is refused: the line is the caller's to add.

### 25.3 Output and exit codes

stdout: the mode and settings line (seed, sampling, trials run of the cap, the stop reason); the yield as
a percent with one decimal, its interval and the target verdict; a per-goal table (yield, interval, worst
margin and its trial); the did-not-evaluate count with each reason and its trials; a statistics table —
mean, σ, min, max, median — per goal margin (with Cpk against the margin's limit of 0) and per real scalar
measurement (in its unit); each goal's five tightest trials with their values; and the kit statistics in
use (process / mismatch stream counts). stderr: one progress line per batch
(`trials 64/500 · yield 84.4 % [75.1 %, 91.2 %] · 2 did not evaluate`), suppressed by `-q`. `--json`
carries the same as `result.yield` — a yield is a FRACTION there — plus, with `--contributions` and never
unasked, what drives each goal's and measurement's spread (R-ya4-9). The text output prints the same ranking with
each share clamped to 0–100 %; `--json` carries β·r as it stands (owner decision D-a, `yield.md`).

Exit (D13, §7): **0** finished, the yield met `--target` or there was none · **3** finished below the
target · **1** refused · **2** no trial evaluated · **130** cancelled, writing nothing — a file the run
wrote as the cancellation landed is deleted.

### 25.4 Over the protocol

`run analysis=montecarlo` is `yield mc` and `run analysis=yield` is `yield estimate` (§11.1): every flag
is an argument (`trials`, `target`, `trial`, `savePreset`, `saveCorner` …) and the result is the verb's
`result.yield`. **A progress notification per batch** goes through the existing token — the batch is the
run's natural unit and a yield run's few dozen batches are what a client wants to see, unlike `opt`'s
hundreds of iterations — with the trial count as `progress` and the cap as `total`. Progress is delivered
on the run's own thread (a `Progress<T>` would race the result frame, `RunHost`'s reason), and
`JsonRpc` takes its lock per frame, so nothing is held between notifications. Cancellation is the
existing path. The server's `instructions` carry a seven-step yield walk-through beside the optimize one.

### 25.5 `check`, `explain`, `read` and `plot`

`check` reports a statistical setup the run would refuse — nothing varies, a yield run with no
`use=yield|both` goal — in the run's own words, asking `StatisticalRun.Create` (which evaluates nothing);
a setup with a target or a yield-only goal is asked as a yield run, one with only tolerances or a
statistics line as a Monte Carlo. `explain --analysis` adds which chains a yield run evaluates under
`analyses=goals` and `analyses=all` (`OptimizationRun.AnalysesUnder` over the yield specs) and the trial
cost — **in nominal evaluations, labelled an estimate**: `explain` runs nothing, so it cannot time one;
the cost is the nominal plus ⌈trials ÷ parallelism⌉ batches at the parallelism the run would use.
`read` of a `.yield.npy` prints the `yield` summary first and puts that group first in the document;
`--at trial=417` narrows to one trial. `plot` takes the statistics functions in a trace (`cube=histogram(
trials.goal:S21:worst, 20)`): an expression calling an axis function is evaluated ONCE over the cubes it
names (`TraceExpression`, not per sample), and its one remaining axis is the X; the drawing styles a
histogram wants arrive in YA-8, until then it is a line over its `bin` axis.

### 25.6 The gate

`tests/Ui.Tests/Statistics/YieldCliTests.cs`: `YieldCliVerbTests` (on a divider: `estimate` exits 0 at a
reachable target and 3 at an unreachable one naming the goal; `mc` with no goal exits 0; `trial --trial 7`
draws the full run's values exactly; `plot` takes a histogram; `--save-corner` adds one corner line and
nothing else byte for byte), `YieldParityTests`, `YieldMcpTests` (the protocol returns the verb's object,
progress arrives per batch, a cancelled call answers 130 and writes nothing), `YieldReferenceTests` (the
page lists every schema key, flag and noun, and every MCP field is a verb flag) and
`YieldAgentWalkthroughTests` (the walk-through's MCP calls, in order, on a fresh workspace, end with a yield
and no error).

### 25.7 `yield corners` (brief-yield-6)

`yield corners <path>` runs `CornerRun` (`docs/design/yield.md` §10): the nominal and every enabled corner, one batch,
scored against the yield specs. stdout is the **corner × goal margin table** — corner, temp, each goal's margin with
`✗` on a failing cell, and a status (`passes`, `FAILS`, `did not evaluate`) — then each goal's worst corner and every
corner that did not evaluate with its reason. The result is `<design>.corners.npy` (`-o` moves it); `--json` carries
`result.corners`. Exit (D13): **0** every goal met at every corner · **3** a goal fails at a corner (or, under
`nonconverged=fail`, a corner did not evaluate) · **1** refused · **2** nothing evaluated · **130** cancelled.

- `--corners a,b` narrows to those enabled corners; an unknown name is a refusal listing the enabled ones.
- `--mc` runs a Monte Carlo at each corner instead — a yield when the design has an enabled yield goal — with the
  kit's process draws off at a corner; the table is a yield per corner and the worst corner. `mc` and `estimate`
  do the same when `--corners` is given or the statistics line says `corners=all|<names>`. The file is then
  `<design>.yield.npy` with a `corner` axis outside `trial`.
- A statistical corner replays its trial from `<design>.yield.npy` when that file is the run it names (same seed and
  sampling), naming any stream the design no longer has; otherwise it is drawn afresh, with a note saying so.
- `--generate "axis=a,b;temp=-40,25,85;Vdd=3.0,3.6"` prints the corners the cross product makes and **writes
  nothing** — `corner` lines for a `.cnl`, the tuning block's JSON for a `.csch` (a kit axis selection has no `.cnl`
  spelling). `result.corners.generated[]` carries each corner in full for both — `name`, `temp`, `values`, a
  schematic's kit `axes`, and a netlist's `line`. `--write` appends them to a `.csch` after a history checkpoint, refusing names the design already has;
  on a `.cnl` it is refused, the lines being the caller's to add (D12). More than 256 corners is a refusal naming the
  count.
- A `.csch` in a kit workspace extracts with the workspace's corner axes bound, as Simulate extracts it, so a corner's
  kit selections reach the run headlessly.

Over MCP it is `run analysis=corners` (`corners`, `mc`, `generate`, `write`, `output`); progress is one
notification per corner. Gate: `tests/Ui.Tests/Statistics/CornerTests.cs` — `CornerRunTests`,
`CornerMonteCarloTests`, `StatisticalCornerTests`, `CornerGeneratorTests`, `CornerCliTests`.

### 25.8 `yield center` (brief-yield-11)

`yield center <path>` runs `CenteringRun` (`docs/design/yield.md` §15): the `opt=1` nominals moved to maximize yield,
every candidate scored on M common trials, then the start and the best point verified on fresh trials. Flags are the
`center` line's — `--algorithm`, `--trials` (M), `--verify`, `--max-iter`, `--max-evals`, `--time`, `--width`,
`--parallel`, `--seed` — plus the statistics line's that still apply (`--target`, `--confidence`, `--nonconverged`,
`--sampling`, `--save`, the kit switches) and `--vars`/`--goals`; `--trial`, `--save-corner`, `--autostop`,
`--corners` and `--contributions` are refused as belonging to another noun, and the `center`-only flags are refused on
the others. stderr carries the estimate, then one line per iteration; stdout the verified `start [interval] →
centred [interval]` sentence, the centred nominals beside the start with railed marks, and the yield-vs-iteration
table. The file is the best point's verification, `<design>.yield.npy` (`-o` moves it); `--json` carries
`result.center`. `--save-preset <name>` adds the centred nominals as one preset to a `.csch` after a history
checkpoint. Exit (D13): **0** · **3** the verified yield is below `--target` · **1** refused · **2** no candidate
evaluated · **130** cancelled, nothing written. Over MCP it is `run analysis=center`.

`--surrogate quadratic` (brief-yield-12, `yield.md` §16) scores each candidate on a quadratic fit of its yield-goal
margins — 2k + 1 points in z-space plus the cross terms when k ≤ 12, and k + 2 common trials — and 10,000 virtual
trials, instead of M simulations. The report then states the surrogate, labels the search's yields as not verified,
adds each iteration's poorest fit R² to the progress line and the history (`history[].rSquared`), and says when three
poor fits switched the search back to simulated trials (`switchedBackAt`). The verification, the exit code and the
result file are unchanged: they are always simulated. A design whose surrogate would cost no fewer simulations than M is
refused with the counts. MCP: `surrogate`.

### 25.9 `yield doe` (brief-yield-14)

`yield doe <path>` runs `DoeRun` (`docs/design/yield.md` §17): the factors — the `opt=1` entries over their ranges, or
`--factors stat` the `stat=1` entries at nominal ± k σ — at the points of a full factorial, fraction, Plackett–Burman or
face-centred composite design, each response's effects judged against Lenth's margin. A noun on the verb, not a verb of
its own: it shares the evaluator, `--set`, `--goals`, `--parallel`, `-o`, `-q`, `--json` and the exit codes. Its own
flags are the `doe` line's — `--design`, `--resolution`, `--factors`, `--levels`, `--centre`, `--responses` — and
`--optimum`; every Monte Carlo flag (`--trials`, `--seed`, `--sampling`, `--target`, `--trial`, `--vars`, …) is refused
as belonging to another noun, and the `doe` flags are refused on the others. stderr carries the design and its
simulation count, then a line per batch; stdout the factor table, then per response its effects largest first — active
ones starred, each with its alias set — the Lenth margin, R² and the curvature. `--optimum` adds the fitted model's
best point and its confirmation: predicted vs simulated per goal. The file is `<design>.doe.npy` (`-o` moves it);
`--json` carries `result.doe` (`responses[].effects[].aliases`, `optimum`). It writes nothing to the design. Exit: **0**
· **1** refused (or the optimum refused) · **2** no run evaluated · **130** cancelled, nothing written — there is no
target, so no 3. Over MCP it is `run analysis=doe`. `--optimum` with stat factors and `--resolution` with any design
but `frac` are refused once the file's `doe` line is under the flags, before anything runs; a cancel during the optimum
is a cancel of the run, and the file the design wrote is removed.

### 25.10 No flag is silently ignored (brief-yield-16)

**`Yield.Flags` says which run honours each flag, and every other run refuses it by name** through
`cli.yield.corner-flag` ("yield: --trials belongs to yield mc, estimate, trial, corners --mc and center."). The runs are
`mc`, `estimate`, `trial`, `corners`, `corners --mc`, `corners --generate`, `center` and `doe`; `mc` and `estimate` with
`--trial` are `trial`. `reference statistics` prints the table with each flag's runs in brackets, so the page and the
verb cannot disagree, and the old per-noun lists (`CornerFlagProblem`, `DoeFlagProblem`, `CenterFlagProblem`) are gone.
In particular: on `doe` every statistics-line flag (`--confidence`, `--nonconverged`, `--save`, `--process`,
`--mismatch`, `--sigma-scale`, `--analyses`); on `corners` without `--mc` `--trials`, `--seed`, `--sampling`,
`--target`, `--confidence`, `--autostop`, `--save` and `--contributions`; on one trial `--corners`, `--target`,
`--parallel`, `--contributions`. A few refusals say more than the table: `--target` on `mc`, `--save-*` without
`--trial`, and `--contributions` on a run at each corner, which ranks nothing.

**`--save-corner` is refused with `--vars`, `--sigma-scale`, `--process`, `--mismatch` or `--set`**
(`cli.yield.save-corner-uncarried`). A statistical corner records the trial, seed, sampling and trial count and replays
the trial under the design's own statistics line, so a flag that changed the draws would replay different values from
the ones printed. `--save-preset` keeps the values instead.

**`plot` refuses what would draw nothing** (`plot.trace.stat-refused`, with the reason): `envelope=` on a trace that is
not a family or on a Smith or polar plot (`TrialViews.EnvelopeRefusal`); `colorby=` whose cube cannot reach the trace's
members, or on a trace with no trial axis (`TrialViews.ColourByRefusal`, the resolve's own early exits); either one with
`stat=`; and a statistics option its statistic does not read — `fit=` and `percent=` are a histogram's, `param=` a yield
sensitivity's, `over=` anything's but a yield sensitivity's. **On a run at each corner** a statistic takes
`corner=<name>`; without it, it is one trace per corner, each pinned, never the first corner alone. `--spec-lines`
reads a stacked goal line's first slice (the line is the same at every corner). The yield display, the statistics
table, contributions and a yield sensitivity refuse such a result by name.


## 26. `recognize` — a board's artwork as a circuit, headless

**brief-artsch-7.** `circuitrf recognize <path>` is Create Schematic from Artwork with no display: a board's
copper becomes a circuit of native components — ground, vias, ports, parts and lines (MLIN and its
discontinuities, CPWG, SLIN, the TLIN physical form) — with an S-parameter analysis, written as a schematic,
a `.cnl`, or both. The recognition itself is `docs/design/artwork-to-schematic.md`.

### 26.1 It owns no recognition

Every decision is `ArtworkRecognition`'s (`src/Design/Layout/Recognition`), the function the GUI command
calls: `Run` (recognise → emit → draw → write, the `--into` path) or `Circuit` (recognise → emit, no write —
the `-o` path and the read-only default). `src/Cli/Recognize.cs` is argument parsing, refusals and reporting,
on `Authoring.cs`' terms. A comment-stripped source scan holds it: `src/Cli` names no public type of that
namespace but the entry point, its options (`RecognitionInput`, `RecognitionOptions`, `RecognitionScope`,
`RecognitionTarget`, `RecognitionRunOptions`, `RecognitionEmitOptions`, `ViaPolicy`, `CoplanarReading`), its
result (`RecognitionResult`/`Run`/`Circuit`/`Report`, `PartsTable`) and `PartsTableCsv`.

### 26.2 Input

A `.clay`, a cell folder (its primary layout; several layout files with no primary is a refusal listing
them), or a workspace with `--cell <name>` — the kind inferred through `DocumentKinds.Classify` as `check`
and `render` infer it. Any other kind is a refusal **by kind**. The layout is read by
`RecognitionInput.FromFile`, the walk the trace review and railRF use, so its `.ctech` and its `.cem` (whose
ports and sweep come first) resolve exactly as they do in the editor.

### 26.3 Read-only by default; the parts round trip

With neither `-o` nor `--into` **nothing is written**: stdout carries the report (one line per class) and the
parts table as CSV. That is an agent's first call. `--parts-out p.csv` writes the same table (allowed alone);
edit its `Kind`/`Value`/`Variable`/`Model`/`ModelFile` and pass it back with `--parts p.csv` — a value given
there replaces the variable that stood for it. A `Refdes` read off the silkscreen or generated may be corrected too:
the row keeps its `X`/`Y`, which is how it finds its part (`artwork-to-schematic.md` §5.3). The CSV is the contract (`PartsTableCsv`): the dialog's grid,
`--parts-out` and `--parts` all go through it.

### 26.4 Outputs and options

`-o x.cnl` writes the circuit and nothing else (a non-`.cnl` path is refused); `--into new:<name>` writes a new
cell beside the artwork's, `--into artwork` the artwork's own cell (refused when it has a schematic view,
naming `new:`). A target holding a schematic this command wrote needs **`--replace`** (the GUI asks; a build
machine cannot be asked), and the replace takes a history checkpoint first; a hand-drawn schematic is refused
whatever the flags. `-o` and `--into` together write both from one recognition. The paths written are the
result — on stdout and in `outputs`.

Each option absent is the dialog's default: `--bom`, `--placement` (with `--placement-origin
symbol|body|pin1`, `--placement-unit mm|mil|in`; an origin neither stated nor declared is the reader's own
refusal, which names the flag), `--region x0,y0,x1,y1` (**every coordinate with a unit; a bare number is a
refusal** — `render --window`'s rule), `--ground <net>` / `--ground-at x,y` (units required), `--vias
model|ground`, `--coplanar auto|microstrip|gcpw`, `--coplanar-factor k`, `--start f --stop f --npts n`
(frequencies with their unit; a flag changes only its own field of the `.cem`'s sweep, else of 100 MHz – 6 GHz
in 201 points; `--stop` is also the top frequency a TLIN and the coupled-pair check are judged at), `--digits n`
(the significant figures every number the circuit carries is written with — line widths and lengths, via sizes,
a port's Z, part values, variables and their tuning range; 1 to 15, default 6; the dialog's digits menu),
`--free-orientation` (every shunt part drawn below its line rather than on its copper's side — the GUI's "Link symbol
and footprint orientation" setting, off).
`--json` carries `result.recognize`: the layout, technology and scope, the instance count, each report class
with its count, sentence and anchors (DBU), the parts table row for row keyed by the CSV's columns, and the
paths written.

### 26.5 Exit codes

**0** recognised (and written, when asked) · **1** refused — the recognition's own refusal (no technology, no
copper in scope, no port, a refused companion file) arrives as `recognize.refused` with its sentence, nothing
written · **130** cancelled through `RunHost`'s `RunControl`, nothing written (the targets are written last).
Never 2: nothing here solves a circuit.

### 26.6 Over the protocol, `check` and `explain`

The MCP `recognize` tool has every flag as a field (`output`, `into`, `replace`, `partsOut`, `parts`, …), the
same read-only default and the same refusals; the server's `instructions` carry a four-step walk-through.
`check` on a recognised schematic reports nothing new — its components are ordinary. (A layer name the `.cnl`
quotes for its space is stored bare in the drawn schematic, as every schematic stores one; kept quoted, the
extraction warned and bound the default layer.) `explain` on one reports the `ArtworkSource` block as plain
walk lines — the source `.clay` resolved against the schematic, the scope, the options, the parts table's hash,
the version and time — and `explain --ref <the .clay>` resolves a reference that names a FILE to that file.

### 26.7 The gate

`tests/Ui.Tests/Recognition/RecognizeCliVerbTests.cs`: the verb as a process against `ArtworkRecognition.Run`
in process on the same synthetic board (the `.csch` byte for byte but the provenance time, the `.cnl` byte for
byte), the read-only default leaving the tree's files and timestamps unchanged, the parts round trip, the
refusals (`--region` bare numbers, `--into artwork` on a cell with a schematic, `--into new:x` twice without
and then with `--replace`), the end to end (`recognize --into` then `sparam` on the schematic, no display), and
the source scan. `ServeProtocolAdapterTests.Recognize_IsListed_AndItsReadOnlyDefaultWritesNothing` holds the
tool; `NetlistToSchematicTests.AQuotedLayerName_IsStoredBare` the layer-name fix.

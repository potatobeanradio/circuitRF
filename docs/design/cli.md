# circuitRF — the command-line interface (`src/Cli`)

**Status:** current · **Covers:** `src/Cli/Program.cs` · **Related:** `ui-architecture.md`,
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

## 2. The verbs

| Verb | Input | Runs | Writes |
|---|---|---|---|
| `sparam` | `.cnl` or `.csch` | `SParameterEngine` | Touchstone `.sNp` by default; `-o`'s extension picks the format (`.sNp`, or `.npy`/`.mat`/`.txt` for the cubes). With a `WSProbe` in the netlist it also prints one line per probe (`WSProbe GATE idx=1 H0(f_lo)=… ZG(f_lo)=… SM_Y0 min −18.1 dB @ 1.5913 GHz SM_H0 min −19.8 dB @ 1.7337 GHz` — each stability margin's minimum over the sweep and its frequency, in dB), evaluates the bench's `measure` lines, carries `wsprobes: [{label, idx, smY0Min, smY0MinHz, smH0Min, smH0MinHz}]` in `--json` (**linear**, because dB is a display convention and a document carries the number), reports a probe whose margin falls below the analysis line's `MarginThreshold=` (default −15 dB, `MarginThreshold=none` disables) as an Info diagnostic, and — a probe with no port being legal — refuses a Touchstone of a run that has no `S`, naming the cube spellings (`docs/design/stability-wsprobe.md` §3, §9) |
| `dc` | `.cnl` or `.csch` | `NonlinearDcEngine` | node voltages + probe currents to stdout |
| `hb` | `.cnl` or `.csch` | `HbEngine` (single- or multi-tone) | stdout tables; `-o .mat/.npy/.txt` |
| `lp` | `.cnl` or `.csch` | `LoadpullEngine` + `LoadpullPostProcessor` | stdout grid table; `-o .mat/.npy/.txt/.spl/.lpcwave` |
| `lpp` | `.cnl` or `.csch` | `LoadpullPursuitEngine` | stdout optima + follow-on grid; `-o` as `hb`; `--out-grid` writes the `.gam` |
| `em` | `.cem` | `EmSetupResolver` + `EmRunService` (kernel chosen by `EmKernelRegistry`) | Touchstone `.sNp` + grouped `.npy` at the path Simulate writes; `-o` moves the Touchstone |
| `elab` | `.cnl` or `.csch` | elaboration only | the elaborated netlist, for development |

**A run verb takes a SCHEMATIC as well as a netlist, and extracts it in memory** (§14). Any other
document kind is a refusal naming what the path holds — `cli.input.wrong-kind`. It used to be handed
to `CnlReader`, which parsed the JSON as netlist text and reported its first key as a missing cell
name.

Twelve verbs run no analysis, so none of §3-§6 applies to them and §7's exit codes reduce to 0-or-1:

| Verb | Input | Does | Writes |
|---|---|---|---|
| `convert` | any interchange format | one import, one export | the target format; documented in the repo-root `CLAUDE.md`. **A `clay` target is a DIRECTORY** — see below |
| `new workspace` | a directory | `WorkspaceCreate.Create` | a `.cws` and, unless `--tech none`, a copied `.ctech` |
| `new cell` | a workspace + a name | `CellCreate.Create` | a cell folder and one empty-but-valid file per `--views` |
| `import part` | a component file or folder | `ComponentRead` + `ComponentImport.Import` | a cell folder holding the land patterns and the symbol |
| `check` | a workspace, a cell folder, or one document | the validators that already exist | **nothing** — §10 |
| `explain` | the same, plus `--expr` / `--analysis` / `--ref` / `--cells` / `--layers` / `--extents` | reports what resolution DECIDED | **nothing** — §10 |
| `render` | the same three view documents, a cell folder, a workspace + `--cell`, or a `.cdd` | draws it with the renderer the GUI draws with | one `.svg` / `.pdf` / `.png` — §13, and §13.7 for a data display |
| `read` | a result file, or one of circuitRF's own documents | loads it back through the readers the GUI reads through | **nothing** — §11.4 |
| `netlist` | a `.csch`, a cell folder, or a workspace + `--cell` | the extraction the GUI's own Simulate performs | one `.cnl`, or the text on stdout — §14 |
| `plot` | a result file | builds a one-plot data display and draws it | one `.svg` / `.pdf` / `.png`, and the `.cdd` under `--write-cdd` — §15 |
| `find` | a directory | enumerates the workspaces, cells, views and analyses under it | **nothing** — §16 |
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
| `reference` | **nothing at all** | reports what a caller may WRITE: the shipped reference pages, plus four topics generated from the live registries and readers — the component catalogue, the analysis directives, and the `.cdd` and `.ctech` formats | **nothing** — §12 |

**`new` is one verb with a noun, not three** (`brief-automation-3-authoring-verbs.md` R-aut3-13): the
surface has a standing cost, and adding `new schematic` later is a noun rather than a fourth
top-level verb. The three authoring verbs share one rule that decides every default they have —
**whatever the GUI's dialog pre-selects, the verb selects with no flag, and anything the dialog would
have ASKED is a refusal that names the flag answering it.** So `--tech` defaults to the New Workspace
dialog's own pre-selected technology and an unknown id lists the real ones rather than falling back;
`--views` defaults to `schematic`, which is what the GUI's New Cell creates; and a source folder
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
| 130 | stopped — `em` only, and only when the run was cancelled at a work boundary (§8.4) |

`2` is deliberately **not** the same test for every verb. `hb` and `dc` fail on any non-converged
solve. A loadpull grid in which some points do not converge is a normal, useful result — the edge of
a Γ grid routinely will not — so `lp` returns `2` only when **every** grid point failed, and `lpp`
only when neither optimum converged and there is no follow-on grid. A rule that failed the whole run
on one bad point would make the exit code useless in a script.

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

### 8.3 What goes where, and the three lists

§3.1's split, applied: the summary and the written file paths are **stdout**; progress, the resolved
workspace/layout/technology, and the run's own three lists are **stderr**.

`EmRunResult` separates `Notes` / `Warnings` / `Errors` by what the reader is expected to DO about
each, and the verb prints all three under those labels rather than flattening them:

| Prefix | Means |
|---|---|
| `note:` | the run explaining itself — which kernel ran and why, the mesh's own sentences, RLGC, ports |
| `warning:` | something to act on — a stale `.sNp` about to be replaced, a technology that resolved but failed validation |
| `error:` | something the user asked for and did not get — a results file that could not be written |

Flattening them into one list is the exact defect the three-list split was introduced to fix, and it
is just as wrong on a terminal as it was in the Messages region.

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
7. Update this file and the verb list in the repo-root `CLAUDE.md`.

`em` follows 1, 4, 5 and 6 and is deliberately outside 2 and 3: it does not read a `.cnl`, so there is
no chain to select and no directive to override. Its analogue of §5's rule is §8.2's — the one
override it takes lands in the `EmSetup`, not at the run service, for the same reason.

`check`, `explain` and `read` follow 1, 4, 6 and 7, and their §5 analogue is §10's — they run
nothing and they write nothing.

`render` follows 1, 4, 6 and 7. Its §5 analogue is §13's and it is the same one the authoring verbs
have in a different costume: **it owns no rendering.** Every pixel comes out of the three renderers in
`CircuitRF.Render` that the application draws each frame with, so "a headless picture and the GUI's are
the same picture" is true by construction rather than by care — which is what makes §13.6's byte
identity a gate rather than an aspiration.

`reference` follows 1, 4, 6 and 7 and is outside everything else, because it reads no file either
(§12). Its §5 analogue is R-aut6-7: **it transcribes nothing.** The prose half is the authored page,
embedded; the component half is generated from the live registries at every call. A fact typed into
that verb is a fact that will disagree with the code the first time either changes.

`serve` follows 1 and 7 and is outside all the rest, because it is not a verb that does work: it is
the one adapter that dispatches to the others (§11). Its §4 analogue is the inversion of §3.1 —
stdout is the protocol and the result goes into a frame — and its §5 analogue is R-aut-1: it owns
no logic at all, so there is nothing for an override to land in.

`convert`, `new` and `import part` follow 1, 4, 6 and 7 and are outside 2, 3 and 5 for the same
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
                         [--analysis [<name>]] [--ref <relative-ref>]
```

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
| `NameValidator` | `src/Design/Cells` | a cell name the GUI would reject |
| `TechValidation.Analyze` | `src/Design/Layout` | a `.ctech`'s problems, already typed as `TechProblem` |
| `TechnologyResolver.ResolveForDocument` | `src/Design/Layout` | the technology walk-up |
| `EmSetupResolver.Resolve` | `src/Design/Layout/Em` | a `.cem`'s layout and technology, and its refusals |
| `CellSymbolResolver` | `src/Design/Schematic` | every cell reference on a schematic |
| `NetExtractor` | `src/Design/Schematic` | naming conflicts — two labels on one physical net |
| `Elaborator.Elaborate` | `src/Core/Elaboration` | parameters, expressions, cycles, node numbering |
| `ChainSelector` | `src/Cli/ChainSelection.cs` | whether a declared analysis chain will dispatch |
| `DrcPredicateParser` | `src/Design/Layout/Drc` | a `.wasm` rule that will not parse |
| `DrcEngine` | `src/Design/Layout/Drc` | layout design rules |

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
VALIDATED, because there is nothing to validate it against.

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

The six questions are **refused together rather than ordered** (R-rnd3-2) — each asks something
different, and a precedence nobody stated would be an invention. `--all` is `--cells`' own modifier and
is refused beside anything else; `--view` is only ever a cell folder's disambiguator, spelled exactly
as `render` spells it, and a cell folder holding more than one view is a refusal LISTING them.

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

**Ten tools, and the count is the point** (R-aut-9). A client that discovers tools up front carries
every description for the whole session whether or not it calls one, so the surface is a standing
cost paid on every interaction. Nine come out of `ToolCatalog`'s one table; the tenth, `batch`, is
advertised beside them by `HistoryBatch` because it is the only one that is not a command line.

| Tool | Becomes |
|---|---|
| `run` | `sparam` / `dc` / `hb` / `lp` / `lpp` / `em`, selected by an argument — one tool, not six |
| `check` | `check` |
| `explain` | `explain`, including RND-3's `--cells` / `--layers` / `--extents` |
| `create` | `new workspace` / `new cell` |
| `import` | `import part` / `convert` |
| `render` | `render` — **one tool over every document kind**, as the verb is (R-rnd0-4/R-rnd5-2). The kind comes from the path, so there is no selector; making the view type one would advertise three modes where there is one verb |
| `read` | `read` |
| `history` | `history checkpoint` / `list` / `restore` (RC-5, `revision-control.md` §5.3d) |
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

### 11.3a Resources — the cheaper channel for the same bytes

The server also declares MCP **resources**, one per reference topic — the four generated ones
included, so `circuitrf://reference/analyses`, `.../data-display` and `.../technology` are advertised
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
them. `data-display` and `technology` are read by REFLECTION from `DataDisplayConfig` and `CtechFile`,
the types their readers deserialise into, with each field's default taken off a freshly-constructed
instance. Each of those two carries an authored preamble — what the format is for, and a minimal
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

The generated topics follow: `data-display`, `technology`, `analyses`, `components`. The first two are
here because they are formats a client must WRITE and that `create` does not make one of — the
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

---

## 15. `plot` — one picture, without authoring a display first

`brief-automation-11-missing-verbs.md` R-aut11-2.

Hand-authoring a `.cdd` to draw a single trace was the largest piece of incidental work in an
otherwise short task: a document with a tab, a plot container, a placement, a source reference and a
slice, every field of which has to be right before anything appears.

```
circuitrf plot lc.s2p -o s21.svg --trace cube=S,i=2,j=1,y=db --title "LC lowpass"
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

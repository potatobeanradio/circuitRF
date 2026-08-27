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
| `sparam` | `.cnl` | `SParameterEngine` | Touchstone `.sNp` (always; `-o` names it) |
| `dc` | `.cnl` | `NonlinearDcEngine` | node voltages + probe currents to stdout |
| `hb` | `.cnl` | `HbEngine` (single- or multi-tone) | stdout tables; `-o .mat/.npy/.txt` |
| `lp` | `.cnl` | `LoadpullEngine` + `LoadpullPostProcessor` | stdout grid table; `-o .mat/.npy/.txt/.spl/.lpcwave` |
| `lpp` | `.cnl` | `LoadpullPursuitEngine` | stdout optima + follow-on grid; `-o` as `hb`; `--out-grid` writes the `.gam` |
| `em` | `.cem` | `EmSetupResolver` + `EmRunService` (kernel chosen by `EmKernelRegistry`) | Touchstone `.sNp` + grouped `.npy` at the path Simulate writes; `-o` moves the Touchstone |
| `elab` | `.cnl` | elaboration only | the elaborated netlist, for development |

`--kits <dir>` is pulled out of the argument list before dispatch, so **every** verb takes it: it is
what makes an externally-supplied device model resolve headlessly, the way opening a workspace does
in the GUI.

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
6. Update this file and the verb list in the repo-root `CLAUDE.md`.

`em` follows 1, 4, 5 and 6 and is deliberately outside 2 and 3: it does not read a `.cnl`, so there is
no chain to select and no directive to override. Its analogue of §5's rule is §8.2's — the one
override it takes lands in the `EmSetup`, not at the run service, for the same reason.

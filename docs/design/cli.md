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
engine lives in `src/Engine` or `src/RfCore`**, and nothing whose driver lives in `src/Ui` — which is
exactly the line between the verbs that exist today and the one that does not (§7).

## 2. The verbs

| Verb | Input | Runs | Writes |
|---|---|---|---|
| `sparam` | `.cnl` | `SParameterEngine` | Touchstone `.sNp` (always; `-o` names it) |
| `dc` | `.cnl` | `NonlinearDcEngine` | node voltages + probe currents to stdout |
| `hb` | `.cnl` | `HbEngine` (single- or multi-tone) | stdout tables; `-o .mat/.npy/.txt` |
| `lp` | `.cnl` | `LoadpullEngine` + `LoadpullPostProcessor` | stdout grid table; `-o .mat/.npy/.txt/.spl/.lpcwave` |
| `lpp` | `.cnl` | `LoadpullPursuitEngine` | stdout optima + follow-on grid; `-o` as `hb`; `--out-grid` writes the `.gam` |
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
| 1 | could not run — bad arguments, missing file, no matching analysis, an exception |
| 2 | ran, but did not converge |

`2` is deliberately **not** the same test for every verb. `hb` and `dc` fail on any non-converged
solve. A loadpull grid in which some points do not converge is a normal, useful result — the edge of
a Γ grid routinely will not — so `lp` returns `2` only when **every** grid point failed, and `lpp`
only when neither optimum converged and there is no follow-on grid. A rule that failed the whole run
on one bad point would make the exit code useless in a script.

## 8. What the CLI does NOT do: EM

There is no `em` verb, and the reason is structural rather than an omission. The EM run pipeline —
`.cem` model and persistence, geometry flatten, port extraction, the cross-section and planar
extractors, `EmRunService` — is **already framework-free by rule** (`src/Ui/Layout/Em/`, R-em-1: no
Avalonia, no SkiaSharp). But it lives in the `CircuitRF.Ui` assembly and its dependency closure
reaches the layout model, the technology model, and the PCell generators, which live there too. The
CLI cannot reference that assembly without pulling Avalonia across the firewall.

The engines are not the problem: `src/Engine/Mom` is already on the CLI's side of the line. Only the
`.cem`-to-`EmProblem` half is on the wrong one.

**The fix, and its measured cost, are specified in `docs/sonnet-briefs/brief-cli-em-verb.md`.** Do not
start on an `em` verb without reading it — the design question it settles is where the extracted
project's boundary goes, not whether the verb is possible.

## 9. Adding a verb

1. Add the case to the dispatch switch and a line to `PrintHelp`.
2. Follow §3's five steps; use `SelectTop` with your base-analysis test.
3. Put overrides in the directive (§5), not at the engine.
4. Results to stdout, everything else to stderr (§3.1).
5. Pick the exit-code rule that is honest for that analysis (§7) — do not copy `hb`'s by reflex.
6. Update this file and the verb list in the repo-root `CLAUDE.md`.

# Sonnet Brief — AUT-8: the netlist contract — refuse what the reader does not understand

**Read `brief-automation-7-mcp-hardening.md` first** — it carries the evidence these requirements
come from and the governing rule they all serve. This brief also depends on **AUT-1** (structured
diagnostics are what a refusal is delivered as) and inherits **AUT-4**'s budget rule: `check` runs no
analysis (R-aut4-1), so every check here is a parse-time or resolution-time check.

**Scope: the `.cnl` reader and the parameter parser, and making `check` and `explain` tell the truth
about what they read.** Nothing here changes what the engine computes. Every requirement converts a
silent acceptance into a refusal, or a misleading report into an accurate one.

**This is the delicate brief of the series.** `CnlReader` is what every `.cnl` under `testdata/` and
every hero regression depends on, and some committed files may rely on keys it currently ignores.
**Run the full suite, not a scoped one, and treat any newly-refused fixture as a finding to report
rather than a fixture to edit** — a file that has been silently mis-simulating is exactly what these
refusals exist to surface.

---

## R-aut8-1. An unrecognised key on an `analysis` directive must be refused, by name

The exercise reached the loadpull-pursuit directive by copying key names out of a schematic's own
serialised analysis block — `LpLoadTunerName`, `LpSourceTunerName`, `LpToneExpr`, `LpToneUnit`, and
about twenty more. **Every one was accepted and discarded.** `check` was clean, `explain` was clean,
and the run then failed with `LoadpullAnalysis: LoadTuner= is required` — one missing key per
attempt, discovered serially. Separately, the dropped tone key made the run announce `f0=0 GHz` and
proceed.

The `.cnl` spellings turn out to be the schematic's keys minus the `Lp`/`Lpp` prefix and the `Expr`
suffix. That is guessable in hindsight and is stated nowhere.

- **Refuse an unknown key on any `analysis` line, naming the key and listing the legal ones for that
  `type=`.** This is the single highest-leverage fix in the brief: it converts R-aut8-3, this
  requirement, and most of R-aut8-2 from silent wrong answers into one-line refusals.
- **Report every missing required key at once**, not the first one. A caller that must re-run to
  discover the second missing key pays the full cost of a run for each.
- Consider accepting the schematic's own key spellings as aliases. The two serialisations of one
  concept diverging is what produced this.

## R-aut8-2. An unrecognised `analysis type=` token is silently dropped

`type=lpp` produced, from `check`, zero errors and the warning *"The document declares no analysis."*
Nothing indicated the type token was the problem. Eight candidate spellings were tried before
`type=loadpull_pursuit` was found (`type=loadpull` for the plain sweep). **Refuse an unknown
`type=`, listing the legal tokens.** The same warning text is correct for a document that genuinely
declares none and must stay for that case.

## R-aut8-3. `Unit=` on a `sparam` directive is silently ignored and the sweep runs in Hz

**The highest-severity finding in this brief.** Given

```netlist
analysis SP1 type=sparam Start=0.5 Stop=6 Npts=551 Unit=GHz
```

`check` reports zero errors, `explain --analysis` reports `kind: sparam, runnable: true`, and the run
sweeps **0.5 Hz to 6 Hz**. The written Touchstone header says `# GHz` while its first frequency
column reads `5E-10`. The resulting S11 is flat at the DC reflection coefficient of the terminations
— a completely plausible-looking curve, at frequencies nine orders of magnitude from the ones asked
for.

The cause is in `CnlReader.TryParseSParamDirective` (`src/Core/Netlist/CnlReader.cs:986`): it reads
`startUnit`, `stopUnit` and `stepUnit` (`:1033`, `:1034`, `:1053`), each defaulting to `"Hz"`. It
never reads `Unit`. **The `parametric_sweep` directive, in the same file (`:1249`), reads `Unit`.**
Two directives in one format use different key spellings for the same concept, and the one a caller
guesses from the documented example is the one that is thrown away.

Fix all three of:

- **Accept `Unit=` on `sparam`** as the shorthand that sets all three when they are not given
  individually. The two spellings must not stay divergent.
- **Refuse an unrecognised key** on any `analysis` directive (R-aut8-1).
- **Make the written Touchstone's declared unit follow the data.** A file whose header says `GHz`
  and whose first column is `5E-10` is internally inconsistent regardless of which end is wrong.

**Gate: a test that writes the directive above, runs it, and asserts the first swept frequency is
5e8 Hz.** Assert on the *frequency*, not on a parse result — the whole failure mode is that parsing
"succeeds".

## R-aut8-4. A component instance line with the wrong number of nets is not validated

This is the requirement that would have made R-aut10-2 a one-line refusal instead of a false defect
report.

```netlist
R:R1 n1 R=1000 Ohm          ; a two-terminal part given one net
```
`check` reports **zero errors and zero warnings**. The run then fails with
`Error: Index was outside the bounds of the array.` — an unhandled exception surfaced verbatim as a
diagnostic, naming neither the line, the instance, nor the count.

The `Tuner` case is worse, because there is no exception at all: the short line **simulates**, returns
`status: ok`, and yields a wired-wrong circuit whose output is quietly meaningless.

- **Refuse an instance line whose net count does not match the type's**, naming the type, the
  instance, the count given and the count expected. This is a parse-time check against the same
  registry R-aut10-2 exposes; it needs no elaboration and belongs in `check`.
- **No unhandled exception may reach a diagnostic.** `Index was outside the bounds of the array` tells
  a caller nothing and tells a user less. Audit the `.cnl` path for others.

**Gate: the one-net resistor line above, asserting `check` reports an error naming `R:R1`.**

## R-aut8-5. `BiasTee` accepts exactly `on`; every other boolean spelling silently disables it

`true`, `1`, `True` and `Yes` were each accepted with no warning and each produced a circuit with no
bias at all. Accept the ordinary boolean spellings, **or refuse an unrecognised one by name**. Do not
leave a third option in which the circuit quietly differs from the document. Audit every other
`bool`-kinded component parameter for the same pattern while you are here — this is a parameter-
parsing question, not a tuner question, and `BiasTee` is unlikely to be the only one.

## R-aut8-6. Units on a top-level variable: make the behaviour match the documentation

`RFfreq = 2 GHz` parses and is the reference page's own worked example. `VDS = 48 V` is
`Parse error at position 6: Unexpected token 'V'`. The reference states that a variable may carry a
unit, without qualification.

Decide which is correct and make the other match it, then say so on the page. If only frequency-like
units are supported on a bare variable assignment, that is a legitimate design — it must be
documented, and the refusal must say it. Separately, **that error message carries no line number and
no variable name**; see R-aut8-7.

## R-aut8-7. Netlist parse and elaboration errors must carry a line number and the offending token

`Parse error at position 6: Unexpected token 'V'` gives a column into a string the caller cannot see.
Every diagnostic from the `.cnl` path should carry the source line number, the line's text, and the
token. This is cheap and it compounds with everything else in §2 — a client that can see *which line*
converges in one round trip instead of by bisection.

---

## R-aut8-8. `explain` reports `runnable: true` for a cell that cannot run

A DUT cell was reported `runnable: true, dispatched: true, dispatchedBy: lpp` while naming a load
tuner and a source tuner that do not exist in it, containing no bias source, and referencing an
undefined variable. Whether a thing will run is the question `explain` exists to answer; answering it
optimistically is worse than not answering.

Resolve the analysis's own named references (tuner names, tone variable) before reporting `runnable`,
and where a chain cannot run, **say which reference failed**. If a full determination is too
expensive for `explain`'s no-solve budget (R-aut4-1), report the weaker claim honestly — `declared`
rather than `runnable` — rather than the strong one incorrectly.

---

## Gates

Per-requirement gates are stated above. In addition:

**A source-scan or registry-driven test that every `analysis type=` token and every directive key the
reader accepts is either handled or refused** — no third category. The failure mode this brief exists
to remove is the third category, and it will grow back silently otherwise.

**Run the full unfiltered suite at this brief's boundary**, per the repo-root `CLAUDE.md`'s guidance,
and read the TRX rather than re-running.

## On completion

Write the findings to the relevant `RESOLVED.md`; **never to a `CLAUDE.md`.** Update
`docs/design/cli.md` and `docs/design/automation-architecture.md`, and the user docs at
`docs/user/src/reference/cli.md` where a verb or flag changed — do not regenerate the user docs.
See `brief-automation-7-mcp-hardening.md` §7 for the series-level completion rules.

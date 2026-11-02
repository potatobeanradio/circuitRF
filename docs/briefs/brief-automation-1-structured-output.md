# Sonnet Brief — AUT-1: Structured results and structured failures

**Read `brief-automation-0-overview.md` first.** It records the findings this brief is built on and
fixes R-aut0-1 … R-aut0-7, which it does not repeat. The architecture is
`docs/design/automation-architecture.md`; R-aut-6, R-aut-7 and R-aut-10 are what this brief
implements.

**Scope: output only.** No new verb, no new capability, no change to any analysis, no change to what
the existing human-readable output prints. **This brief must be invisible to anyone who does not pass
the new flag.** It is independent of the other four and can ship alone.

---

## 1. What the caller gets

Every verb that produces a result gains **`--json`** (R-aut0-7 — that spelling, on every verb, with
no per-verb variant):

```
circuitrf sparam amp.cnl --json
circuitrf hb pa.cnl --json
circuitrf lp pa.cnl --json --only Pout_dBm,PAE
circuitrf em amp.cem --json
```

- **One JSON document on stdout**, and nothing else on stdout. Progress, notes, warnings and errors
  stay on stderr exactly as today (`cli.md` §3.1).
- **The same exit code as without the flag.** `cli.md` §7's per-verb rules are unchanged, including
  the deliberate difference between `hb`'s convergence test and `lp`'s (R-aut-8).
- **A failed run still emits a document** — the failure is the payload (§4). A caller must not have
  to distinguish "no output" from "output I could not parse".

---

## 2. The one rule that decides the implementation

**R-aut1-1. The JSON is a projection of the same values the human printer reads — never a second
computation.** Concretely: the printers in `src/Cli/Program.cs` (`PrintHbDataSet:1452`,
`PrintCube:1489`, `PrintLoadpullGrid:856`, `PrintPursuitOptima:811`) each select, index and scale
values before formatting them. Every one of those selections is a decision, and several of them are
load-bearing:

- `lp` reports each grid point at its **last converged, non-tickle drive step**, not at a fixed drive
  index — reading a fixed index would mix compressed and uncompressed points in one column
  (`cli.md` §6.3).
- The printers read **both** cube spellings — `Pout_dBm`/`Pout`, `Gt_dB`/`Gt`, `Efficiency`/`DE` —
  and scale the raw fractions to percent, because a pursuit's follow-on grid uses the engine's raw
  names (`cli.md` §6.2).
- The grid axis is located by **name** (`gridPoint`), never by position, because a sweep prepends one
  axis per nesting level.

If the JSON path re-derives any of that, the two outputs will disagree the first time one is changed.
**Extract the selection into a shared step that returns values, and give it two formatters.** The
human printer must end up reading through the same step it does today, and its output must be
byte-identical afterwards (§6.1).

**R-aut1-2. No numeric formatting decisions in the JSON.** Numbers are emitted as JSON numbers at
full `double` precision, invariant, unrounded — the human column widths and dB/percent presentation
are a terminal concern. Where a value is genuinely a scaled quantity (percent vs fraction), emit the
**raw** engine value and name the cube; do not encode the terminal's scaling into the document.

---

## 3. The result document

**R-aut1-3. One schema shape across every verb.** A caller that can read one verb's output can find
its way around another's.

```
{
  "circuitrf":   { "version": "…", "verb": "lp" },
  "input":       { "path": "…", "analysis": "…" },
  "status":      "ok" | "not-converged" | "failed",
  "exitCode":    0,
  "outputs":     [ { "kind": "touchstone", "path": "…" }, … ],
  "diagnostics": [ … ],                    // §4
  "result":      { "groups": { "<group>": { "<cube>": { … } } } }
}
```

- **`result` mirrors the `DataSet`**, which is already the universal result type (repo `CLAUDE.md`
  invariants): groups, then named cubes, each with its axes (name, unit, values) and its data. A
  cube's `DataKind` decides whether values are numbers or `[re, im]` pairs — do not invent a third
  encoding, and do not silently drop the imaginary part.
- **`analysis`** records the chain that actually ran after `SelectTop`'s promotion (`cli.md` §4), not
  what was requested. A caller that asked for an inner analysis and got its wrapper must be able to
  see that from the document alone.
- **`outputs`** lists every file written, with its kind. For `em` that is both the `.sNp` and the
  `_em.npy` — they are not redundant (`cli.md` §8.2) and both belong here.

**R-aut1-4. `--only <cube>[,<cube>…]` and `--group <name>[,<name>…]` narrow `result`.** R-aut-10:
reading is the expensive direction. A loadpull's cubes are `[gridPoint x pinStep]` and a caller that
wants one figure of merit should not receive eight. `RfCore.Export.DataSetSubset.SelectGroups`
already does the group half; the cube half is the same shape. Narrowing affects `result` only —
`status`, `diagnostics` and `outputs` are always complete.

**R-aut1-5. `--json` and `--all` compose.** Today `--all` dumps every cube instead of the
one-row-per-grid-point summary. Under `--json` the default stays the summary (it is the useful
projection, not a terminal compromise) and `--all` still means every point.

---

## 4. Structured failures

**R-aut1-6. Every diagnostic in the document is a `CircuitRF.Diagnostics.Diagnostic`, serialized with
its id and typed arguments intact.**

```
{ "id": "em.layout.not-found", "severity": "error",
  "message": "The layout 'x.clay' …",          // Render(), English, invariant — always present
  "arguments": { "layoutRef": "x.clay" } }
```

`src/Diagnostics/Diagnostic.cs` already carries exactly this: a stable dotted id, a severity, an
English default template and named typed arguments. Its own header names filtering, grouping,
deduplication and robust assertion as the reasons — all of which a machine caller needs at least as
much as the Messages window does. **`message` is always present and always the `Render()` output**,
so `cli.md` §7A's English-forever, culture-invariant rule holds inside the JSON as well.

**R-aut1-7. Widen `Diagnostic` adoption at the call sites this brief's verbs actually reach — and no
further.** Adoption today is 6 construction sites in 2 files (`EmDiagnostics`,
`FileAccessDiagnostics`). Converting all 118 text-only sites the `Diagnostic` header describes is a
much larger project and is **not** this brief. Convert:

- the CLI's own refusals and argument errors — every `Console.Error.WriteLine` that precedes a
  `return 1` in `Program.cs` and `LayoutConvert.cs`;
- `EmRunResult`'s three lists, which already carry a `Diagnostic` alongside the string for exactly
  this reason (`cli.md` §7A) — the JSON reads the structured half, stderr keeps reading the string;
- elaboration and engine warnings surfaced by the run verbs, **only where the producing site already
  has the typed values to hand**. Where it does not, wrap the existing sentence in an
  argument-free `Diagnostic` with an honest id rather than inventing arguments by parsing prose.

**Report the count converted and the count left**, so the size of the remaining migration is a known
number rather than a guess.

**R-aut1-8. An id is a permanent contract.** `Diagnostic.cs` is explicit: change the template freely,
change the id and you have made a new diagnostic. Ids chosen here are chosen once. Follow the
existing dotted, lower-kebab convention (`em.layout.not-found`) and scope the first segment to the
producing area (`cli.`, `em.`, `elab.`, `lp.`).

---

## 5. Where the code goes

**R-aut1-9. The serializer lives below the firewall, not in `src/Cli`.** A protocol adapter (AUT-5)
must emit the identical document, and R-aut-13 forbids one adapter being able to do what another
cannot. Put the result-document builder and the `Diagnostic` serializer in `src/RfCore/Export` and
`src/Diagnostics` respectively — both are already firewall-gated
(`UiFirewallTests.NonUiAssemblies`) — and have `src/Cli` call them.

Use `System.Text.Json` with explicit options, matching the conventions the persistence types already
use (`WorkspacePersistence`, `LayoutPersistence`, `TechPersistence` all share one set): enum-as-string,
`WhenWritingNull`, invariant number handling. Do not introduce a second JSON convention in the same
repository.

---

## 6. Gates

### 6.1 The one that matters

**A golden-file test per verb asserting the human output is byte-identical before and after.** Capture
the current stdout of `sparam`, `hb`, `lp`, `lpp`, `em` and `elab` on existing fixtures, commit those
bytes, and assert them after the refactor. R-aut1-1 makes the human and JSON paths share a selection
step; this is the test that proves the sharing did not move a number.

### 6.2 The rest

1. **`--json` output parses**, for every verb, on a passing run, a non-converged run and a refused
   run. All three produce a document; `status` and `exitCode` agree with each other and with the
   process's actual exit code.
2. **stdout holds nothing but the document** under `--json` — no stray progress line, no `[circuitRF]`
   note. Assert by parsing the whole of stdout, not by looking for a `{`.
3. **`--only` / `--group` narrow `result` and nothing else**; an unknown name is skipped silently on
   the group side, matching `DataSetSubset`'s existing behaviour, and the same on the cube side.
4. **A cube's complex values survive**: a `DataKind.Complex` cube round-trips through the document
   with both parts, compared against the same cube read back from the verb's own `.npy`.
5. **Culture invariance**: run one verb under a comma-decimal culture and assert the document is
   byte-identical. `FormatCultureInvarianceTests` is the existing pattern.
6. **Diagnostic ids are stable and unique**: a test enumerating every id this brief introduces,
   asserting no duplicates and no id differing only by case. It exists to make an accidental id
   change show up as a failing test rather than as a silently broken caller.

Follow `EmCliVerbTests`' process-launch pattern for anything that runs a verb as a process: a
`ReferenceOutputAssembly="false"` project reference on `src/Cli` plus the `CliDir` metadata attribute,
the DLL exec'd directly, **and both child pipes drained concurrently** — `em` says enough on stderr to
fill that buffer and deadlock a sequential reader.

---

## 7. On completion

Create `src/Cli/RESOLVED.md` and write the findings there — **never to a `CLAUDE.md`**. Update
`docs/design/cli.md` with the `--json` contract (a new section beside §3.1, since it is a third
channel rule rather than a verb) and `docs/design/automation-architecture.md` §5 if anything about
R-aut-6/R-aut-7 turned out differently in practice.

**Report, do not silently absorb:**
- Any place where the human printer's selection could not be shared with the JSON path, and why.
- The R-aut1-7 counts: diagnostics converted, diagnostics left as prose.
- Any golden-file difference in §6.1 that turned out to be a pre-existing bug rather than a
  regression — that is a finding about the repo, not about this change.

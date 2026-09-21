# circuitRF — automation architecture: headless capabilities, adapters, and clients

**Status:** current · **Covers:** the boundary between what circuitRF can do without a GUI, how that
is exposed, and who calls it · **Related:** `cli.md`, `ui-architecture.md`, `project-file-formats.md`,
`workspace-and-project-tree.md`

---

## 1. What this note decides

circuitRF is increasingly driven by something other than a person at a keyboard: continuous
integration, batch characterisation, the project's own documentation factory, and — the case that
motivates writing this down — automated design agents that author, run and read back a design without
a human in the loop.

Every one of those wants the same thing, and it is not a scripting language and not a remote-control
protocol. It is **a complete, headless, machine-legible capability surface**. This note fixes where
that surface lives, what shape it takes, and what is deliberately excluded from it.

The constraint that decides everything below is the same one that shapes `cli.md` — the **UI
firewall** (`ui-architecture.md` §3):

```
adapters ──► src/Design ──► src/Core ──► src/Engine ──► src/RfCore ──► src/Diagnostics
                              NO Avalonia anywhere on this path
```

`tests/Firewall.Tests/UiFirewallTests.cs` fails the build if that is violated. An automation surface
is therefore not something bolted on beside the application: it is **whatever fraction of the
application already sits below that line**, plus verbs.

---

## 2. Three layers, and only the middle one is a long-term asset

**R-aut-1. Automation is layered as capabilities → adapters → clients, and an adapter owns no
logic.**

| Layer | What it is | Lifetime |
|---|---|---|
| **Capabilities** | Headless operations below the firewall: create a workspace, create a cell, import a footprint, author a layout, extract a netlist, elaborate, run, export, validate | Years. This is the product. |
| **Adapters** | The CLI verbs; a protocol server; anything else that translates an external request into a capability call | Disposable by design — a few hundred lines each |
| **Clients** | A shell script, CI, a documentation build, an automated design agent | Churns continuously and is not ours |

The rule that keeps this honest is already stated for one verb in `cli.md` §8: *the verb owns no EM
logic.* Generalised, it is R-aut-1 — and it is what makes a second or third adapter nearly free to
add and free to delete. The client layer will be re-implemented against whatever conventions prevail
at the time; the capability layer must not notice.

**R-aut-2. A capability that exists only inside a UI event handler is not a capability.** If an
operation can be performed only by a view model reacting to a click, it is invisible to every client
in the table above, and no adapter can reach it without duplicating it. The remedy is always the
same — move the operation below the firewall and have the view model call it too — never to
re-implement it in the adapter.

---

## 3. Where the capability surface actually stands

Measured against the tree rather than assumed. Every document format circuitRF owns is
already plain, human-readable JSON (`project-file-formats.md`); the question is only which side of
the firewall its reader and writer sit on.

| Format | Persistence type | Project | Reachable headlessly today |
|---|---|---|---|
| `.cws` workspace | `WorkspacePersistence` | `src/Design/Workspace` | yes |
| `.ccell` cell folder | `CellPersistence` | `src/Design/Cells` | yes |
| `.clay` layout | `LayoutPersistence` | `src/Design/Layout` | yes |
| `.ctech` technology | `TechPersistence` | `src/Design/Layout` | yes |
| `.cem` EM setup | `EmSetupResolver` | `src/Design/Layout/Em` | yes |
| `.cnl` netlist | `CnlReader` / `CnlWriter` | `src/Core/Netlist` | yes |
| GDSII, DXF, Gerber, Excellon, `.kicad_pcb` | `src/Design/Layout/Interchange` | `src/Design` | yes |
| Touchstone, `.npy`, `.mat`, `.spl`, `.lpcwave` | `src/RfCore/Export`, `src/RfCore/Loadpull` | `src/RfCore` | yes |
| `.csch` schematic | `SchematicPersistence` | `src/Design/Schematic` | yes |
| `.csym` symbol | `SymbolPersistence` | `src/Design/Symbol` | yes |

**The last two rows were the whole gap, and they were a packaging accident rather than a coupling.**
Of the 105 files then in `src/Ui/Schematic`, 104 declared `namespace CircuitRF.Ui.Schematic` and
exactly one — `PlacementService.cs`, which is a view-model service and belongs where it is —
referenced a UI framework package at all. **None referenced Avalonia.** They sat in the
`CircuitRF.Ui` assembly for historical reasons only.

**Closed by AUT-2 on 2026-09-05** (`brief-automation-2-schematic-below-the-firewall.md`): 41 of those
files moved to `src/Design/Schematic` and `src/Design/Symbol` — the closure of the chain above, let
out by the compiler rather than hand-picked — and 64 editor, shell and session files stayed. The
chain is gated end to end by
`tests/Firewall.Tests/SchematicChainBelowTheFirewallTests`, which assembles it in a project that
cannot reference `src/Ui`, and by its `Ui.Tests` companion, which asserts the GUI's own chain writes
the same `.cnl` bytes. Findings are in `src/Design/RESOLVED.md` and `src/Ui/RESOLVED.md`.

**R-aut-3. The schematic and symbol model, their persistence, and net extraction belong below the
firewall.** This is the same carve-out `src/Design` performed for the layout side in 2026-08, for the
same reason and by the same route, and it is worth doing on its own merits: it is what makes "author
a cell, place instances, extract a netlist, simulate it" expressible without a display.

**R-aut-4. `src/Design` stays the design-layer artifact project; the editors stay in `src/Ui`.** The
existing boundary is not weakened by R-aut-3. What moves is the document model, its serializer and
the pure functions over it. What does not move is the canvas, the edit session, undo, hit-testing,
drag-follow, the palette, or anything that observes a viewport.

### 3.1 Creating the first correct document

Reading and writing the formats was never the whole gap. **Making the first correct one was**, and
until AUT-3 the three operations that do it lived inside a view model, where R-aut-2 says a capability
cannot live. **Closed by AUT-3 on 2026-09-05** (`brief-automation-3-authoring-verbs.md`):

| Capability | Lives in | Called by |
|---|---|---|
| Create a workspace | `WorkspaceCreate` (`src/Design/Workspace`) | `circuitrf new workspace` **and** `WorkspaceViewModel.NewWorkspace` |
| Write a cell's view files | `CellCreate` (`src/Design/Cells`) | `circuitrf new cell` **and** the GUI's New Cell / New Schematic / New Symbol / New Layout |
| Import a part | `ComponentImport` (`src/Design/Layout`) | `circuitrf import part` **and** the GUI's Import Component |

Each has exactly one implementation and the GUI calls it — which is not something a byte comparison
can prove (two copies agree right up until one is edited), so
`tests/Ui.Tests/AuthoringCliVerbTests` scans the view model's own source for the calls, with comments
stripped first. `ShippedTechnologies` moved to `src/Design/Layout` with its `EmbeddedResource` items,
because a class that reads resources out of its own assembly enumerates nothing when only the class
moves.

These are **scaffolding, not an editing API** (R-aut0-5): they produce a correct INITIAL document, and
the answer to "how do I add an instance" stays §4's — write the document.

### 3.2 Saying what may be written

The gap that remains once a document can be authored, validated and explained is the one BEFORE any of
that: **a client that cannot spell `MLIN` is blocked before `check` can help it.** Making the formats
the interface (R-aut-5) puts the burden of knowing them on the caller, and until AUT-6 that knowledge
existed only in `docs/user/` — repository content, absent from an installed tree — and in the reader
source. An automated client either guessed or had been trained on this repository.

**Closed by AUT-6 on 2026-09-05** (`brief-automation-6-reference-and-components.md`), in two halves
that are different in kind:

| Half | Source | How it stays true |
|---|---|---|
| The reference pages | the authored `docs/user/src/reference/` pages, embedded in `CircuitRF.Design` | referenced in place from the `.csproj`, so the embedded bytes ARE the authored bytes; a test compares them |
| The component catalogue | `ComponentModelFactory`, `ComponentTypeRegistry`, `SymbolPortDefs`, `InstanceNetContract` | generated at every call; `DocTables` renders the documentation tables from the same `ComponentCatalog` |

**There is no third thing** — no grammar, no schema, no BNF. A hand-written grammar in an adapter is a
second description of `CnlReader` that drifts from it silently, which is the failure this whole series
exists to prevent. The prose is authored and maintained; the code facts are generated; and where a
registry knows a parameter's name, default, unit and visibility but not what it is FOR, the catalogue
says nothing rather than inventing a meaning.

**AUT-10 widened the generated half to everything else a client has to write** (2026-09-08):

| Topic | Generated from | What it prevents |
|---|---|---|
| `analyses` | `AnalysisDirectiveSchema`, the table `CnlReader` validates against | eight guesses to find `loadpull_pursuit`, and ~fifteen round trips to reconstruct one directive's keys |
| `data-display` | `DataDisplayConfig`, by reflection over the type the `.cdd` reader deserialises into | a client that could only plot because an unrelated `.cdd` was on the machine to copy from |
| `technology` | `CtechFile`, likewise | the same, for `.ctech` |

Each of the two format topics carries an authored preamble — what the format is FOR and a minimal
example that was written out and RUN — and generates everything after it. Neither claims what a field
MEANS: the types carry no per-field summary to read one from, and an invented meaning is worse than
none.

**The catalogue's own worst defect was fixed here, not in AUT-6.** It published the SYMBOL's pin count
under the heading `nets`, which is a different quantity: a `Tuner` draws one pin and its instance line
binds two. A client that believed it reported a working component as broken. The net count is now its
own field, from `InstanceNetContract` — the same table the elaborator refuses a wrong count with — and
the gate writes an instance line per registered type and elaborates it.

`cli.md` §12 has the detail, including why a port count that is not fixed is reported as not fixed and
why the two keyings' mismatch is part of the answer rather than filtered out of it.

---

## 4. Documents are the interface — declarative, not imperative

**R-aut-5. Authoring is expressed by writing a document, not by replaying a sequence of edit
commands.**

An imperative surface — `place_instance`, `add_wire`, `set_parameter`, one call at a time — is the
obvious design and the wrong one. It makes every client pay a round trip per primitive, it multiplies
the number of ways a request can fail, and it obliges the adapter to model editor state that only the
editor should own. A file that is already JSON does not need a second, worse API in front of it.

What follows from R-aut-5:

- **The format is the contract.** A client that can write `.clay` or `.cnl` can author artwork or a
  circuit today, with no verb at all. This is only true because the formats are readable, versioned
  and culture-invariant, which they are (`FormatCultureInvarianceTests`).
- **The interesting verbs are not authoring verbs.** They are **validate**, **explain**, **run** and
  **diff** — the ones that close the loop for a client that just wrote a file and needs to know
  whether it is well formed, what it resolved to, and what it produced.
- **Convenience authoring verbs are still worth having**, but as scaffolding for the cases where the
  right initial document is non-obvious — a workspace skeleton, an empty cell with the correct
  sub-folder structure and primacy files, a footprint plus symbol imported as a part. Not as a
  general-purpose editing API.
- **A partial or malformed document must be diagnosable, not merely rejected.** A client's only
  repair mechanism is the sentence it gets back.

### 4.1 The two verbs that close the loop

**Closed by AUT-4 on 2026-09-05** (`brief-automation-4-check-and-explain.md`). Of the four verbs
R-aut-5 predicted would matter most, **validate** and **explain** now exist as `circuitrf check` and
`circuitrf explain`; `run` already did; **diff** does not and is not scheduled — the documents are
JSON, and an ordinary text diff on them is already useful. **`read` joined them on 2026-09-05**, when
building the first adapter found that nothing could hand a file back (§8.1).

| Verb | Answers | Detail |
|---|---|---|
| `check <path>` | is it well formed, does it resolve, is it sound? | `cli.md` §10.2 |
| `explain <path>` | what did circuitRF DECIDE — which technology, which chain, what value, which cell? | `cli.md` §10.4 |
| `read <path>` | what is IN this file — a result as cubes, a document as its own bytes | `cli.md` §11.4 |
| `reference [topic] [type]` | what MAY be written — the reference pages, and every primitive with its terminals and parameters | `cli.md` §12 |
| `netlist <path>` | what will actually RUN — the extraction Simulate performs, as a document | `cli.md` §14 |
| `find <root>` | what EXISTS — the workspaces, cells, views and analyses under a directory | `cli.md` §16 |

Three properties of that pair are what make it an architectural answer rather than two more verbs:

- **`check` writes no validation logic** (R-aut4-2). Every finding comes from a validator that
  already exists and that the GUI already uses. A rule living only in `check` would be a rule the
  application does not enforce, and a design would pass headlessly and be refused when opened.
- **Neither runs an analysis and neither writes** (R-aut4-1, R-aut4-6). That is what makes `check`
  callable after every edit, on a read-only tree, and on a workspace another process has open.
- **`explain` reports the WALK, not just the answer** (R-aut4-7). Resolution in circuitRF is a series
  of walk-ups — a document's ancestor workspace, a layout's technology, a `.cem`'s two independent
  walks — and which one produced an answer is exactly what a caller cannot see from the file.

The DRC engine crossed the UI firewall to make the third question answerable headlessly (R-aut4-3);
`src/Design/RESOLVED.md` records what moved and what deliberately did not.

**`netlist` and `find` joined them on 2026-09-08** (`brief-automation-11-missing-verbs.md`), and they
close two holes that only exercise could have found:

- **Nothing turned a schematic into something runnable.** `check` and `explain` both accepted a
  `.csch`; a run verb handed the JSON to `CnlReader` and reported its first key as a missing cell
  name. The surface could author a design, validate it, explain it and draw it — and could not
  simulate it. Every run verb now extracts a `.csch` in memory, and `netlist` writes that extraction
  out. It is also the REFERENCE ANSWER a client checks its own hand-authored `.cnl` against: AUT-7
  §3's false defect report came from an instance line one look at a known-good extraction would have
  settled.
- **Nothing said what exists.** Locating a workspace holding a particular device meant searching the
  filesystem outside the surface entirely — which a protocol client with only the server cannot do at
  all. `find` enumerates what every other verb already knew how to read.

Both follow R-aut3-1 unchanged: each calls the function the GUI's own command calls, and the gate
scans `src/Cli` for a second copy rather than trusting that agreement today means one implementation.

---

## 5. The output contract

`cli.md` §3.1 already fixes the channel split — stdout is the result, stderr is everything else — and
§7A already fixes the language: **the CLI is English permanently, and culture-invariant**, because a
localized diagnostic on stderr silently breaks every grep, scraper and CI matcher on machines in one
country. Both rules carry over to every adapter unchanged.

Two additions, both aimed at making output cheap to consume rather than merely possible to consume.

**R-aut-6. Every verb that produces a result offers a structured form of it, and the structured form
is a projection of the same data the human form prints — never a second computation.** The human
table and the machine document must not be able to disagree; if they are computed twice they
eventually will.

This matters most where the human form is already a deliberate summary. A loadpull's cubes are
`[gridPoint x pinStep]`, and `lp` prints one row per grid point precisely because eight full cubes
would scroll a terminal without answering the question (`cli.md` §6.3). A client wanting one number
should be able to ask for that number rather than parse a table or dump every cube with `--all`.

**Status, 2026-09-05 — R-aut-6 and R-aut-7 are implemented** (`brief-automation-1-structured-output.md`).
`--json` is on every verb, spelled once; the document's shape is `RfCore.Export.ResultDocument` and the
contract is `cli.md` §3.2. The loadpull summary R-aut-6 is about is now
`RfCore.Loadpull.LoadpullResultSummary`, which the console table and the document BOTH read through —
the human printers no longer make any of those selections themselves, which is what makes "never a
second computation" a property of the code rather than a rule to remember.

**R-aut-7. A failure is emitted as structure, not only as prose.** `src/Diagnostics` already defines
exactly the right shape — `Diagnostic` carries a stable dotted id, typed arguments and an English
default template, and the project is itself firewall-gated so every layer can author one. Its own
header lists filtering, grouping, deduplication and robust assertion as the reasons, all of which a
client needs at least as much as the Messages window does.

Adoption was thin when this note was written — six construction sites across two files
(`EmDiagnostics`, `FileAccessDiagnostics`) — and the CLI called `Render()` and discarded the
structure. **Widening that adoption is the single highest-value output change**, and it is additive:
the English template is carried alongside, permanently, so nothing that reads stderr today changes.

**Status, 2026-09-05:** the CLI's own refusals are converted — 50 ids in `src/Cli/CliDiagnostics.cs`,
covering every argument error and refusal in `Program.cs` and `LayoutConvert.cs`, plus the EM
refusal `EmRunService` had always carried and the CLI had always discarded. Stderr came out
byte-identical, which is the proof the diagnostics carry everything the strings did. The counts left
and the reason for the line are in `src/Cli/RESOLVED.md`.

**R-aut-8. Exit codes stay honest per analysis.** `cli.md` §7's rule — that `2` means "ran, did not
converge", and that its test is chosen per verb rather than copied — is a machine-facing contract and
becomes more important, not less, as the callers stop being human.

### 5.1 What a result document has to SAY, beyond being structured

**Status, 2026-09-07 (AUT-9, `brief-automation-9-result-documents.md`).** Structure was not enough on
its own. An out-of-process client driving the surface end to end produced four ways a correctly
structured document still said the wrong thing, and every one of them is now a rule:

- **A quantity states its own unit.** One result carried the same efficiency twice — a cube in
  percent and a scalar as a fraction — with nothing anywhere saying which. Every cube now carries a
  `unit`, never empty, and `unknown` is a legitimate value: a designer's own `measure` expression has
  a unit circuitRF cannot state, and saying so is an answer where guessing is not (`cli.md` §3.2a).
- **A file is the format its name says.** `sparam -o out.npy` wrote a Touchstone under that name; the
  extension is honoured now, and one naming no format the verb writes is a refusal listing the ones
  it does. Writing format A to a path named B is the option worth removing.
- **A description of a computation must not contradict it.** A two-port with a 12 Ω second port was
  written and read back as though every port were 50 Ω, so a client renormalising from the reported
  reference computed a wrong answer from a correct simulation. Touchstone 1.x declares one R; the
  per-port references travel with the SNP and are written to, and read back from, the file's own
  header note. **Nothing is renormalized** — the solve was never the problem.
- **`status: ok` is not a diagnosis, and neither is `status: not-converged`.** A bench whose device
  was inert returned the engine's floor sentinel at all 56 drive points and exited 0 with an empty
  diagnostics array; on that evidence the client wrote up a working component as defective. A run
  that converged nowhere returned exit 2, an empty diagnostics array and no result at all, while
  stderr carried a full per-grid-point account. Three findings now say what the SHAPE of a loadpull
  result implies (`RfCore.Loadpull.LoadpullRunFindings`), including the one a single parameter fixes:
  a first drive step tens of dB above the tickle breaks the harmonic-balance warm start, and that is
  worth naming rather than letting a blanket non-convergence look like a broken circuit.

**R-aut-14. A refusal names the thing that failed, and two different problems get two different
sentences.** A data display whose own source file could not be read was reported as an unreadable
data display, naming the RESULT file's path inside that sentence — so a caller rewrote a `.cdd` that
was never wrong. A bad `--data` argument, a bad reference inside the document, and a bad document are
three problems and now three ids.

---

## 6. Economy: the interface has a size, and the size is a cost

**R-aut-9. Prefer few broad verbs over many narrow ones.**

For a protocol adapter this is not a style preference. A client that discovers tools up front carries
every tool's description for the whole session, so the surface is a standing cost paid on every
interaction, whether or not the tool is used. Forty single-purpose verbs are worse than seven
well-chosen ones even when the forty are individually simpler.

**R-aut-10. Reading is the expensive direction, and should be designed first.** Authoring a document
is one write. Understanding a result is the part that is unbounded — which is why R-aut-6's
projection, and the ability to ask for a subset of it, do more for a client's cost than any authoring
convenience.

**The surface grew by three on 2026-09-08 and each was weighed against R-aut-9.** `netlist` and
`find` are capabilities the surface did not have at all — one made it possible to simulate a drawn
design, the other to discover one — and a capability a client cannot reach is not a capability. `plot`
is the one that is a convenience over something already expressible, and it earns its description by
removing the single largest piece of incidental work an exercise measured: authoring a whole data
display to draw one trace. All three are ONE tool each, with the document kind inferred from the path
rather than spelled as a mode, which is the same rule that kept `render` from being three.

**`lvs` joined them on 2026-09-21** (`brief-lvs-11-cli-verb.md`, `cli.md` §19), and of every tool on
this surface it is the one whose value is highest HERE rather than on a command line. A person who
has just drawn a board can look at it; **an out-of-process author cannot**. Asking whether the
artwork implements the drawing is the only way an agent that wrote a `.clay` can find out whether it
wrote the right one, and the answer comes back as typed `lvs.` findings with the designer's own
object names on them — actionable without parsing an English sentence back apart. It is one tool
over four document kinds, inferred from the path, by the same rule as the three above; it costs no
second implementation, because it is a command line onto the same `LvsRun.Run` the GUI panel calls.

**Status, 2026-09-07 (AUT-9 R-aut9-8 through R-aut9-12).** The measured payloads that produced these
requirements: `reference components` 297 KB, a 551-point two-port `run sparam` **173 KB returned
inline**, and `import convert --list-cells`, **whose answer is one cell name**, 30 KB. Four
structural amplifiers were behind those numbers and all four are addressed:

| Amplifier | What it is now |
|---|---|
| every diagnostic emitted twice | `arguments.text` is emitted only when it differs from `message` |
| results narrowable only by cube NAME | `--at`, `--range`, `--interp` narrow by AXIS; `--result summary` returns the shape alone |
| a listing call paying for tens of notes | `--summary` reports the informational ones as counts; warnings and errors always travel |
| JSON serialised inside a JSON string | `serve` emits `structuredContent` beside the text block, which stays as the protocol's own fallback |

**And the payload rule is now UNIFORM even where the payload is not.** `run sparam` returned its whole
result inline while `run lpp` returned a written path and nothing else, with nothing in either tool's
schema to say which. Which verbs return values inline is still a per-verb decision — a loadpull's
eight `[gridPoint x pinStep]` cubes are not what a caller wants by default — but **every** run now
returns `result.shape`: the groups, cube names, units and axis extents it produced. A caller always
learns what exists before deciding what to pay for, and the schema says so.

**And R-aut-9 has a second channel to weigh against it now.** MCP **resources** cost a URI, a title
and a size until they are read, where a tool description is paid every session whether or not
anything calls it — so a capability that is a body of TEXT belongs on the resource channel, and the
reference surface is published there (`cli.md` §11.3a). What that does not settle is reachability:
**not every client surfaces resources to the model, and a capability the model cannot reach is not a
capability.** So the reference surface is also the seventh tool, and the two return the same bytes
because both translate to the same verb — which is R-aut-13 applied to a channel rather than to an
adapter.

The seventh tool is the one place in this series where a standing per-session cost was accepted
knowingly. It earns it by being the thing that unblocks writing a document at all: every other tool
assumes the caller already knows what to put in the file.

---

## 6A. Silence is the defect

**AUT-7 R-aut7-0, inherited by AUT-8 through AUT-12.** A client that writes a document, asks `check`
whether it is sound, and is told "yes" must be able to act on that answer.

The cost of a surface that stays quiet is usually described as wasted effort, and that much is
already high: one exercise spent eight guesses finding an undocumented `type=` token and roughly
fifteen round trips reconstructing one directive's key names, each a full process launch and a full
result payload. But the real cost is worse than slow.

> **A surface that stays silent manufactures confident wrong answers about the product.**

The exercise's loadpull task was abandoned as impossible. It was blocked by a one-net `Tuner`
instance line the client had written from the catalogue's own description of the part. `check`
reported zero errors and zero warnings; `explain` reported the analysis runnable; the run returned
`status: ok` with Pout at the engine's floor sentinel at all 56 drive points, with no diagnostic. On
that evidence the client wrote up the component model as defective, with a minimal reproduction case,
and stopped. The model is correct. With the second net supplied the same run completes in 38 seconds.

**A client that reports a working product as broken is behaving reasonably on the evidence it was
given.** That is the argument for treating acceptance-in-silence as a defect class of its own rather
than as a rough edge, and it is why the fixes are shaped the way they are:

- **A fix that makes something work but leaves the silent-acceptance path intact does not satisfy the
  requirement.** Accepting `Unit=` on a `sparam` directive is only half of R-aut8-3; refusing the
  unrecognised keys around it is the other half, and it is the half that generalises.
- **Widening and refusing ship together.** Accepting more boolean spellings without refusing the rest
  moves the silent boundary rather than removing it.
- **No third category.** Every input is handled or refused. A registry the reader validates against
  is only worth having if a test holds it exhaustive — otherwise the third category grows back, and
  it grows back silently, which is the whole problem.
- **A refusal names the thing to change.** The pattern is the artwork half's unknown-technology
  refusal, which lists the five real ids: not "invalid layer mapping" but the key, what it was given,
  and what it accepts.
- **A page that states the caveat instead of closing it is the defect too.** The catalogue carried a
  note against `Term` saying nothing below the UI firewall stated how many nets its instance line
  takes. It was accurate and it was the problem: the reader knew, and nothing asked it. AUT-10's
  R-aut10-2 removed the note by removing the gap.
- **A schema that under-promises costs what one that over-promises does.** `read` accepted a `.cdd`
  and did not say so; a client that believes a schema is behaving correctly either way. Auditing the
  declared path kinds against what each tool accepts is part of the same rule.

Where a full determination is genuinely too expensive for a verb's budget, the weaker claim stated
honestly beats the strong one stated wrongly — `declared` rather than `runnable`. `explain` reports
what it can establish from names already in memory and declines to claim anything that would need a
solve.

---

## 6B. The answer of one verb is the argument of the next

**AUT-12, and the counterpart to §6A.** Silence is one way a surface fails a caller. The other is
telling it the truth in a form it cannot use — where nothing is wrong, no diagnostic is owed, and the
caller is nonetheless left doing arithmetic, or editing the design, to get an ordinary result.

The same exercise §6A's evidence comes from found the artwork half of this surface *good*: `import`,
`explain --layers/--extents/--cells` and `render` composed cleanly, and the Gerber import's
diagnostics named every inference AS an inference, which is exactly what lets a non-human caller
decide whether to trust a result. What it also found were four places where the composition stopped
one step short. Each is a small change, and together they are a rule:

> **A value one verb emits to be acted on is emitted in the form the acting verb takes.**

- **`explain --extents` emitted metres; `render --window` refuses a bare number.** Both are right on
  their own: base SI with the unit and the scale named is the reporting rule (R-rnd3-8), and a bare
  layout coordinate is genuinely ambiguous across six orders of magnitude (R-rnd2-4). But the one
  tool that says where the content is emitted exactly what the other rejects, so every windowed
  render needed a hand conversion. `--extents` now carries a `window` string in the accepted spelling
  beside the numbers, whole document and per layer. The numbers were not changed: a report is read by
  more than one kind of caller, and replacing a measurement with a command line would trade one
  half-answer for another.
- **`render --layers` chose which layers drew; nothing chose how.** A Gerber import gives the six
  copper layers near-identical colours, so an overlay is unreadable, and the only route was to
  hand-edit the generated `.ctech`. **Changing a design to change a picture of it is not a fix** — a
  render-time override is (`--layer-colors`), and it is applied to the same clone the layer selection
  already goes through.
- **`--hide-layers` was the only way to keep an outlier out of a fit, and it removes the content
  too.** `--fit-layers` frames on some layers and draws all of them.
- **`convert -o out/board.clay` produced a DIRECTORY called `out/board.clay`.** The result document
  reported the true paths, so nothing was lost — and a path that names a file and yields a directory
  of that name is still a surprise nobody is there to notice on a build machine. It is now a refusal
  naming the directory spelling, because the alternative — collapsing to one file — works for a flat
  import and discards a hierarchy silently.

The shape of all four: **the capability existed and the composition did not.** That is a class worth
naming, because it produces no error, no warning and no wrong answer — only a caller writing code
that this surface was supposed to make unnecessary.

---

## 7. What is deliberately excluded

**R-aut-11. The running GUI is not remote-controlled.** No command channel into a live
`WorkspaceWindow`. It would couple external callers to the least testable state in the product, and
it duplicates a path that is already proven headlessly — the documentation factory
(`user-docs-factory.md`) and `tests/Ui.Tests/Em/EmCliVerbTests.cs` both drive real work through the
headless path today, the latter comparing its output byte for byte against what the Simulate button
writes.

The legitimate need behind the idea — a person wanting to watch, inspect or take over work that a
client is doing — is served **one-way** instead: a client writes files; the GUI observes them. The
project tree already re-scans on window focus and on an explicit Refresh, deliberately without a
`FileSystemWatcher` (`ProjectTreeTool.cs:16`, `:431`; the same decision is recorded in
`TechnologyCache` and `CellLayoutResolver`). That is the mechanism, and tightening it — if it ever
needs tightening — is a project-tree question, not an automation one.

**R-aut-12. circuitRF does not host a scripting language.** Embedding an interpreter inverts the
dependency: the application becomes a runtime for someone else's program, and every capability then
needs a second, language-shaped binding maintained in parallel with the first. The existing
out-of-process Python dependency for PCell generation is the cautionary precedent, and its failure
modes were packaging ones (`packaging-installed-app-no-pcell-artwork`). Clients script circuitRF from
outside, in whatever language they already use.

**R-aut-13. No adapter is a privileged one.** Nothing may be reachable through a protocol server that
is not reachable through the CLI, and vice versa. The moment one adapter can do something the others
cannot, the logic has leaked out of the capability layer and R-aut-1 is broken.

---

## 8. Adding an adapter

1. Establish that every operation it needs already exists below the firewall. If one does not, that
   is a capability change and belongs in the capability layer first (R-aut-2).
2. Translate, dispatch, report. No decisions, no defaults the capability layer does not already
   have, no logic (R-aut-1).
3. Results structured (R-aut-6); failures as diagnostics (R-aut-7); English and invariant
   (`cli.md` §7A).
4. Keep the verb count small and the verbs broad (R-aut-9).
5. Confirm parity with the CLI, as a test (R-aut-13).

### 8.1 The first one: `circuitrf serve`

**Landed 2026-09-05** (`brief-automation-5-protocol-adapter.md`), as a **verb on the existing
binary** rather than a second executable — packaging is the constraint, not style: what ships is
named after the application rather than the assembly, that name is a literal in five packaging files,
and a second executable is a second thing a platform script can silently omit. `src/Cli/Serve/`,
five files, and `cli.md` §11 is the detail.

**Step 2 turned out to be a stronger rule than it reads.** The adapter does not translate a request
into a capability CALL; it translates it into the **argument vector the CLI would have been given**
and hands that to `CliEntry.Run` — the same function the process entry point calls. R-aut-13 is then
not something a test hopes to catch: the two adapters are one code path, and the parity gate compares
two documents that came out of one function. What that cost was moving `Program.cs`'s dispatch into a
callable class, because a local function of a top-level program is private to `<Main>$`.

**Step 1 found one gap, and it is worth naming.** The tool surface needs to hand a file back — a
result the client just produced, or the document it just wrote — and no verb did that. It was added
as `circuitrf read` in the same commit, on BOTH adapters, because a tool with no verb behind it is
precisely the privileged adapter R-aut-13 forbids. It reads through the two loaders the GUI's own
source library already uses and adds none of its own.

**Two things the CLI gained by being hosted, both null-by-default on a command line:**
`RunHost` carries the host's `RunControl` — the same one the `em` verb already used — into the
sparam, sweep, loadpull and pursuit call sites, so a long run is cancellable and reports progress to
whoever is driving it; and `JsonRun` gained a reset and a sink, because one process now produces more
than one document.

**What the adapter added that the capability layer did not have, and could not:** a root directory,
under which every path a client names must resolve (R-aut5-8). That is not a decision about a design
— it is the boundary of the process's own authority, and it belongs where the process is.

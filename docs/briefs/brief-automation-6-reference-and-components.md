# Sonnet Brief — AUT-6: The reference surface — formats and components

**Read `brief-automation-0-overview.md` first.** This brief **depends on AUT-2** (the component
registry is only reachable below the firewall because of it) and **AUT-5** (it adds to the adapter
that brief built). The architecture is `docs/design/automation-architecture.md`; R-aut-5, R-aut-9,
R-aut-10 and R-aut-13 are what it implements.

**Scope: telling a caller what it may write, before it writes it.** No new document, no new
analysis, no editing, no change to any existing verb's behaviour. Everything here is read-only and
answers a question that is not about any particular file.

Every claim below was read out of the tree. Counts are measured, not estimated.

---

## 1. Why this exists

R-aut-5 makes the file formats the interface: *"the way to change a document is to WRITE it,
because the format is the contract"* (R-aut0-5 restates it, and AUT-3 declines to add per-primitive
edit verbs on the strength of it). That decision is right, and it has a consequence the series has
not paid yet:

> **A client that cannot spell `MLIN` is blocked before `check` can help it.**

`check` tells a caller that what it wrote is wrong. `explain` tells it what circuitRF made of what it
wrote. Neither tells it what it is *allowed* to write — the primitive type names, how many nodes each
takes, what its parameters are called, what a unit suffix means, where a `define … end` block goes.
Today that knowledge exists only in `docs/user/`, which is repository content, and in the reader
source. An automated client either guesses, or it was trained on this repository.

**The failure mode is quiet.** A netlist naming a type that does not exist fails at elaboration with
a sentence about an unresolved name; a component given a plausible-but-wrong parameter name resolves
to the parameter's default and simulates, producing a converged, complete-looking, wrong answer. That
second one is the same class of failure `explain --analysis` was built for.

---

## 2. What already exists, and what must never be re-typed

### 2.1 The prose is written and maintained

`docs/user/src/reference/` holds 34 authored pages. **13 of them carry no `{{…}}` generator
placeholder at all** and are therefore servable as they stand:

| Page | Bytes | Page | Bytes |
|---|---|---|---|
| `netlist.md` | 12,741 | `sdd.md` | 12,939 |
| `expressions.md` | 9,737 | `measurements.md` | 6,392 |
| `units.md` | 11,348 | `pins-ports-terms.md` | 4,764 |
| `file-formats.md` | 10,128 | `grid.md` | 5,480 |
| `simulations.md` | 37,422 | `npy-export.md` | 9,246 |
| `pcells.md` | 10,644 | `pdk-authoring.md` | 17,187 |
| `cli.md` | 54,826 | | |

The rest carry placeholders — `components.md` carries **143** — which is exactly the point below.

### 2.2 The component facts are already generated from the live registries

`src/Ui/Diagnostics/DocTables.cs` renders `components.md`'s parameter tables **from
`ComponentTypeRegistry`**, and its own header states the rule this brief inherits verbatim: *"the
prose never re-types a fact the code already knows … every hand-written table restating them is a
copy waiting to go stale — silently, because a wrong default in a document does not fail anything."*

What the registries hold, measured:

| Source | Where | Holds |
|---|---|---|
| `ComponentModelFactory` | `src/Core/Devices` | **68 distinct primitive type names** — the `.cnl` tokens (13 parameterless, 55 parameterized) |
| `ComponentModel.PortCount` / `.TerminalNames` | `src/Core` | port count and semantic terminal names |
| `ComponentTypeRegistry.DefaultParameters` | `src/Design/Schematic` | name, default expression, unit, dimension, on-schematic — **71 of 75 `SymbolKind`s** have an explicit arm; see R-aut6-12 |
| `ComponentTypeRegistry.EngineReference` | `src/Design/Schematic` | `SymbolKind` → the `.cnl` token |
| `ComponentTypeRegistry.ParameterDescription` | `src/Design/Schematic` | what a parameter MEANS — **partial**, see R-aut6-8 |
| `ComponentTypeRegistry.Category` / `SearchTerms` | `src/Design/Schematic` | how a person goes looking for one |

**All of it is below the firewall, and AUT-2 is why.** `ComponentTypeRegistry` was in
`src/Ui/Schematic` until 2026-09-05. This brief is cheap only because that move already happened.

### 2.3 The two keyings do not line up, and that is a finding to expose

`ComponentModelFactory` keys on the `.cnl` token. `ComponentTypeRegistry` keys on `SymbolKind`.
`EngineReference` bridges them and **is not total in either direction** — measured:

- **5 `EngineReference` targets have no factory entry**: `GND`, `MEAS`, `Pin`, `SpiceModel`, `VAR`.
  Three of those are not components at all, which is itself the answer for them.
- **7 factory types no `EngineReference` maps to**: `Chain`, `ExtDevice`, `I_nTone`, `SemiC`,
  `Short`, `Term`, `V_nTone`.

A type in the second list is placeable in a `.cnl` and has no palette metadata; one in the first is
drawable and will not elaborate. **Both are things a client needs told, and neither is currently
told to anyone.** See R-aut6-9.

---

## 3. The reference capability

**R-aut6-1. One verb with a topic, not one verb per topic.** R-aut-9.

```
circuitrf reference                     # the topic list, with each topic's size
circuitrf reference netlist             # one topic, as its own text
circuitrf reference components          # the catalogue — §4
circuitrf reference components MLIN     # one primitive
```

`--json` per AUT-1, as every verb has it. Exit 0, or 1 for an unknown topic — which is a refusal
**listing the real topics**, following the `--tech` precedent (AUT-3: an unknown id lists the real
ones, never a fallback).

**R-aut6-2. The topic set is CURATED, not "every page".** Reading is the expensive direction
(R-aut-10) and a client pays for every byte. Ship the authoring critical path — `netlist`,
`expressions`, `units`, `components`, `measurements`, `file-formats`, `sdd`, `pins-ports-terms` —
and nothing else in the first pass. **`cli.md` is deliberately excluded**: at 54 kB it is the largest
of them, and a protocol client already has every verb's schema from `tools/list`, so it is the one
page it needs least.

**R-aut6-3. The topic list carries each topic's size in bytes.** A client choosing between a 4.7 kB
page and a 37 kB one should be able to choose. A list that hides the cost makes the cheap topics and
the expensive ones look alike.

**R-aut6-4. Both adapters, in the same commit, and the protocol side is BOTH channels.**

- **MCP resources** — `resources/list` and `resources/read`, with `resources` declared in the
  `initialize` capabilities. This is the correct channel: a resource costs a URI and a title until
  it is read, where a tool description is a standing per-session cost (R-aut-9, R-aut5-4).
- **A `reference` tool as well**, because **not every client surfaces resources to the model**, and a
  capability the model cannot reach is not a capability. It is ONE tool with a topic argument, its
  description is two lines, and it is the seventh — weigh that as a real cost and say so in the
  findings if it turns out not to earn its place.
- Both read one catalogue and return the same bytes, so they cannot disagree. This is R-aut1-9's
  rule — the builder below the firewall, the adapters calling it — applied again.

`circuitrf read` is **not** the vehicle. Its `path` is confined to `--root` (R-aut5-8) and a
reference topic is not a file in the client's tree; overloading it would put a non-path through a
path-confinement check, which is the kind of exception that makes a security boundary stop meaning
one thing.

**R-aut6-5. The text ships in the binary, as `EmbeddedResource`.** `docs/user/src/` is repository
content and is not in an installed tree. Follow `ShippedTechnologies` exactly — plain .NET
`EmbeddedResource` and `Assembly.GetManifestResourceStream`, never Avalonia's `AssetLoader`, which
throws with no live platform.

> **The trap is named in `ShippedTechnologies`' own header and in `src/Design`'s `.csproj` comment,
> and it has already been paid for once:** *moving the class without its `EmbeddedResource` items
> leaves it compiling, enumerating nothing, and reporting nothing.* The `<EmbeddedResource>` item and
> the class go in the same commit, and §6.1 is the gate that proves it.

**R-aut6-6. The embedded copy is the authored file, byte for byte — not a second copy someone edits.**
Reference the files in place from the `.csproj` (`<EmbeddedResource Include="…/docs/user/src/…" />`
with a `LogicalName`) rather than copying them into the project. A copy is a file that will be edited
on one side only, and nothing will report it. Strip only the YAML front matter (`title`, `slug`,
`doc-kind`, `breadcrumb`, `lede`) at read time, since it addresses the site generator and not a
reader — and strip it **at read**, not at build, so the embedded bytes remain the file's own.

---

## 4. The components capability

**R-aut6-7. `components` is GENERATED from the registries at every call. It transcribes nothing.**
This is R-aut4-2's rule (`check` writes no validation logic of its own) applied to description
instead of validation, and `DocTables`' header already argues it for this exact data.

For each primitive, report:

- the **`.cnl` type token** — `ComponentModelFactory`'s own key, which is what a caller writes;
- the **port count and terminal names** — from the model, subject to R-aut6-9;
- **each parameter**: name, default expression, unit, dimension, and whether it shows on the
  schematic — `ComponentTypeRegistry.DefaultParameters`;
- the **meaning**, where the registry has one — and nothing where it does not (R-aut6-8);
- the **category** and **search terms**, so a client can find "the thing that does X" without
  reading all 68.

**R-aut6-8. Where a meaning is not in the code, say nothing — do not invent one and do not scrape
one.** `ParameterDescription` today covers `VerilogA`, `Mixer`/`MixerD`, `Duplexer` and the system
blocks. It does **not** cover R, L, C, the FET family or the microstrip models. `DocTables`' own
comment states the limit and states it as the right answer: *"the registry knows a parameter's name,
default, unit and visibility — the facts that drift — but not what it is FOR"*, and those words
belong in the Markdown beside the table rather than in a C# string literal.

So the honest shape is: **the generated catalogue answers "what may I write", and the `components`
TOPIC (§3, the prose page) answers "what does it mean"** — and a client that wants both asks for
both. `components.md` is the one reference page carrying placeholders, and the placeholders are
precisely the generated half; do not attempt to expand them (`DocGen` lives in `tools/`, is not
referenced by anything shipped, and dragging its placeholder expander below the firewall is a much
larger change than this brief). **Serve the page with its placeholders removed and a line saying the
tables are available from the catalogue**, or exclude the page and serve the catalogue alone —
whichever reads better once you have seen both. Report which you chose and why.

**R-aut6-9. A port count that is not fixed is REPORTED as not fixed, naming the parameter that sets
it.** Several primitives are variadic — an SDD's `NumPorts`, a `Z_Port`'s port count, a `VerilogA`
model's `Pins`, the ideal `Switch`'s throw count. Printing a *default* where the answer is *"it
depends, and here is what on"* is the same error class as `sweep-unit-scale-and-mark`: a number that
is plausible, specific and wrong, with nothing reporting it. **Never instantiate a parameterized
model with invented parameters to ask it its port count.**

**R-aut6-10. The §2.3 mismatch is part of the answer, not filtered out of it.** A factory type with
no palette metadata is reported with its token and its ports and an explicit note that it has no
registry entry; an `EngineReference` target with no factory entry is reported as not simulatable, or
as not a component at all where that is what it is. **Do not quietly emit the intersection.** The two
lists in §2.3 are the exact content of this requirement, and if either has changed by the time this
is built, that change is itself worth reporting.

**R-aut6-11. One catalogue, read by DocGen too.** Extract the data half of
`DocTables.ComponentParameters` into a `ComponentCatalog` in `src/Design/Schematic` beside the
registry it reads, and have `DocTables` render *from* it. `DocTables` stays in `src/Ui` — it also
reads `ToolbarCatalog`, which is UI, and this brief moves nothing else. The point is R-aut1-1's,
one level up: **the documentation table and the machine answer must be one computation, or they will
disagree the first time one is changed.** `tools/DocGen/check-docs-current.sh` then gates both.

**R-aut6-12. "No parameters" and "nobody wrote an arm" are the same answer today, and the catalogue
must not deepen that.** `DefaultParameters` has explicit arms for 71 of the 75 `SymbolKind`s and a
`default: return []` for the rest — and an empty list is also the CORRECT answer for a component
whose rows the user authors, which the function's own header says of the SDD. So an empty result
carries two meanings and `DocTables` renders both as *"No fixed parameters — this component's rows
are authored by the user"*, which is true of one of them.

Report which of the four unarmed kinds are genuinely parameterless and which are simply undescribed.
**Do not close the gap by guessing**, and do not add arms as part of this brief: it is a registry
question, and a wrong default written into the registry to make a catalogue look complete is exactly
the silent-wrong-value failure §1 opens with.

---

## 5. What this brief does not do

- **No `--components` on `explain`.** Every `explain` answer is anchored to a path — *what did
  circuitRF decide about **this** document*. A catalogue is about no document, and putting it there
  would make the verb mean two things.
- **No grammar, no schema, no BNF.** A hand-written grammar in the adapter is a second description of
  `CnlReader` that will drift from it silently, which is the exact failure this whole series exists
  to prevent. The prose reference is authored and maintained; the code facts are generated. There is
  no third thing.
- **No new document format, no writing, and no analysis.** As `check` and `explain`: it must run on a
  read-only tree and on a workspace another process has open (R-aut4-6).
- **No localisation, ever.** `cli.md` §7A. The reference text is English and invariant like every
  other machine-readable surface.

---

## 6. Gates

1. **The embedded set is the authored set.** A test asserting every topic in the catalogue resolves
   to a non-empty embedded stream, and that its bytes equal the file in `docs/user/src/reference/`.
   This is the gate that catches R-aut6-5's trap — a class that compiles and enumerates nothing —
   and it must fail if a topic is added to the list and not to the `.csproj`.
2. **Parity, per R-aut-13** — for the `reference` tool and for every resource, the bytes the server
   returns are the bytes `circuitrf reference <topic> --json` writes, compared exactly as
   `ServeProtocolAdapterTests` already compares the other six tools. A topic reachable one way and
   not the other fails here.
3. **The catalogue is generated, proven by a negative.** Add a parameter to one component in
   `ComponentTypeRegistry` in the test's own fixture — or assert against the registry directly rather
   than against a committed list — so the test cannot pass by agreeing with a transcription. A
   committed golden of all 68 primitives would pass forever after somebody froze it.
4. **`DocTables` and the catalogue agree** (R-aut6-11): for every `SymbolKind` the docs render, the
   rendered table's rows are the catalogue's rows, in order.
5. **R-aut6-9 is exercised**: a variadic primitive reports its port count as parameter-determined and
   names the parameter, and no test anywhere instantiates a parameterized model with invented values.
6. **R-aut6-10 is exercised**: both §2.3 lists appear in the output, asserted by token.
7. **stdout purity** (R-aut5-2): the new tool and the resource reads join
   `EveryCapability_WritesNothingToStdoutButTheFraming`. A reference read is a large write and is
   exactly the shape that finds a framing bug.
8. **The new arguments join the flag gate**: `ServeProtocolAdapterTests.`
   `EveryAdvertisedArgument_IsAFlagTheVerbActuallyReads` covers every mode of every tool, so the new
   tool's positionals must be added to its `positionals` table. It will fail by name if they are not.
9. **Firewall** (`dotnet test tests/Firewall.Tests`) and **full solution green** — run it **once**
   and read `tests/*/TestResults/last-run.trx` for failures.

---

## 7. On completion

Write the findings to `src/Cli/RESOLVED.md`, and to `src/Design/RESOLVED.md` for anything about the
catalogue or the embedded resources. **Never to a `CLAUDE.md`** — the verb list in the repo-root
`CLAUDE.md` is standing project memory, so flag the one-line addition for the owner rather than
editing it as part of a findings write-up.

Update `docs/design/cli.md` (§2's verb table, §9, and §11's tool table),
`docs/design/automation-architecture.md` (§3 and §6 — R-aut-9's cost argument now has a resource
channel to weigh against it), and `docs/user/src/reference/cli.md` with the new verb and an example
of each form. **Do not regenerate the user docs**; run `tools/DocGen/check-docs-current.sh` and
report what it says.

**Report, do not silently absorb:**

- **Whether the seventh tool earned its place** (R-aut6-4), measured against what a resource-only
  surface would have cost. If resources alone would have done, say so — removing it later is a
  larger change than not adding it.
- **The §2.3 lists as they stand when you build it.** They are measured here on 2026-09-05; a
  difference is a change in what circuitRF can place versus what it can draw, and that is worth
  knowing on its own.
- **Which `components.md` route you took** (R-aut6-8), and how the page reads without its tables.
- **Anything the capability needed that the registries could not answer.** By R-aut-1 that is a gap
  in the capability layer, and a fact a client needs that no registry holds is the most valuable
  thing this brief can find — the same way R-aut4-2's "three gaps" were AUT-4's.
- **R-aut6-12's four kinds**, by name, split into genuinely-parameterless and undescribed.
- **The total embedded size**, as a number. It ships in every binary on every platform, and nobody
  will notice it growing unless somebody writes it down once.

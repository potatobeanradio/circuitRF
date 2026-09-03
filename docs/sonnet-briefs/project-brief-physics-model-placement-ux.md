# Brief PM2 — placing a physics-based compact model in few clicks

**Status:** proposed, and **the first phase needs an explicit yes** · **Date:** 2026-09-03 ·
**Companion:** PM1 (`project-brief-physics-model-backend.md`), which makes these models *correct*.
This one makes them *pleasant*. **PM1 stands alone; PM2 does not** — its gates want a working
five-terminal electrothermal device, which is PM1 P2.

**The half of the owner's question this brief answers: is it as simple as pointing a Verilog-A
component at a `.va` file?**

**No.** The component takes a *compiled* `.osdi`. `VerilogAFileResolver.Resolve` says so outright —
*"compile one from your Verilog-A source with your own compiler — circuitRF does not build them."*
That is correct policy and the whole of the clicks problem, because a user who has downloaded a
model family has source, a manual and a parameter file, and no artefact.

Model families are referred to as **Family S** and **Family V**, as in PM1. Neither is named, nor is
any version, nor any path outside the repository — see §5.

---

## 1. The three things that cost the clicks

### C1 — the artefact does not exist yet

Source → artefact is a compiler step circuitRF deliberately does not take. The standard compiler for
this ABI is GPL-3.0; circuitRF is MIT and must not ingest, link or bundle it. So today the first
"click" is leaving the application entirely.

### C2 — a parameter set is a file, and there is no way to load one

Both families ship parameter sets in **Verilog-A declaration syntax** — `parameter real vxo = 1.3e7;
// comment` — not as SPICE `.model` cards. `src/Core/Netlist/Spice/SpiceModelCardTranslation.cs`
reads a different dialect for a different purpose and is not the place for it. Without an importer,
a fitted parameter set is **50–200 individual picker gestures per placed device**, which is the
difference between "usable" and "demonstrable".

The existing picker (`ModelParameterPickerDialog`) is right for adding one parameter by name, and
its central property must survive: a parameter the component does not carry is not forwarded, which
already means *use the model's own default*. Materialising all of them would freeze the model's
defaults at the moment of placement, so that recompiling with a changed default silently would not
take effect.

### C3 — five identical leads, numbered

`BuildVerilogASymbol` (`src/Ui/Schematic/BuiltInSymbols.cs:992`) draws a generic box with **numbered**
terminals, deliberately: circuitRF does not know what the user's model is, and drawing a transistor
glyph would assert something untrue. But the worker already reports each node's own `label`
(`tools/osdi-worker/osdi_worker.c:341`) and the host already carries it
(`ExternalDeviceDescriptor.Label`). On a five-terminal part where the fifth is thermal, numbers are
the largest single source of mis-wiring, and the model has already said which is which.

---

## 2. Phases

### P1 — accept a `.va` and compile it, with the **user's own** compiler *(needs a yes — §4)*

`File` accepts `.va`/`.vams`. circuitRF locates a Verilog-A compiler — `PATH` first, then a user-set
preference — runs it as a **separate process**, caches the `.osdi` keyed on source hash + compiler
identity, and re-runs only when either changes. No compiler on the machine is an ordinary outcome:
refuse by sentence, naming what to install and where to point circuitRF, and leave the direct
`.osdi` path working exactly as today.

**This does not change circuitRF's licence position.** Invoking a separately-installed program at
arm's length is what every host already does with a C compiler; nothing is linked, bundled or
ingested, and `THIRD-PARTY-NOTICES.md` gains no entry because nothing third-party ships.

**Three traps to design for rather than discover:**

- **`include` resolution.** Both families `\`include "disciplines.vams"`, and one ships parameter
  and macro `.include` files beside the source. Include paths must be passed, and the source's own
  directory is the first of them. A model that compiles from its own folder and fails from
  circuitRF is this, every time.
- **Compiler diagnostics are the user's to read.** Pass them through **verbatim** in the refusal. A
  paraphrase of a compiler error is strictly worse than the error.
- **Cache location.** Beside the source is the obvious place and is wrong when the source sits in a
  read-only kit tree. Fall back to a per-user cache, and say which one was used — a user who
  recompiles and sees no change needs to know where the artefact went.

**Gate:** a trivial fixture `.va` **of our own, MIT** compiles, caches, re-uses the cache,
recompiles after a touch, and refuses cleanly with the compiler removed from `PATH`. Every one of
those is testable without any third-party model.

### P2 — load a parameter set

A reader for the Verilog-A parameter-declaration shape — `parameter <type> <name> = <value>;`, `//`
and `/* */` comments, `from [...]`/`exclude` ranges ignored — reachable from the parameter editor as
**Load parameters…** on a VerilogA component.

It writes only names the chosen model actually declares, respelled to the model's own case through
`OsdiModelDiscovery.AlignParameterCase` (which respells only on a case-insensitive match, so a
genuine typo is still refused by name rather than quietly turned into something the model accepts).

**It reports the names it dropped.** A parameter set written for a different version of the same
family is the common case, not the exotic one, and a silent drop is a wrong answer that converges.

Keep it out of `SpiceModelCardTranslation`.

**Gate:** a fixture parameter block round-trips; unknown names are reported rather than dropped;
case is aligned; a value in engineering notation and one in bare exponent form both parse; a
comment containing a semicolon does not truncate the declaration.

### P3 — say which lead is which

- Draw the model's own terminal **labels** instead of numbers when the file has been read and the
  labels are non-empty; fall back to numbers otherwise, so a component placed before a file is
  chosen still draws. The generic *body* stays generic — this changes what the leads are called,
  not what the part claims to be.
- When `Pins` is one less than the model's declared terminal count and the omitted terminal is
  thermal, **say so in the parameter dialog**. That is the ordinary configuration for a part with
  self-heating handled internally, and it should not read as a mistake. (It only becomes expressible
  at all once PM1 P1 lands — before that, the worker claims every terminal is connected.)

**Gate:** `VerilogAComponentTests` / `DynamicSymbolGeometryTests` for the label fallback; the
message asserted by text, not by screenshot.

### P4 — one worked example in `docs/user/`

Source → compile → place → set `Pins` → load parameters → run DC, S-parameters and HB. Written
against a model the reader supplies, naming none.

Per the standing note-writing rule: this page's audience has already chosen this path, so it should
say what the path **buys** — not that a simpler alternative exists.

---

## 3. What this does not do

- No bundled compiler, no vendored model source, no committed `.osdi`.
- No noise analysis and no op-var read-back (both noted in PM1 §3 P3 as documented omissions).
- No change to the kit/PDK path — this is the *user supplies one file* path.
- No parallel component species. Everything here is the existing `SymbolKind.VerilogA`.

---

## 4. The decision this brief needs

**P1 is the whole of "minimal clicks", and it is also the only thing in either brief that adds an
external dependency to a user's workflow.** Without it the answer to the owner's original question
stays *compile it yourself first, then point at the `.osdi`* — which is honest, already works today,
and is what every other host asks of a user with Verilog-A source.

P2, P3 and P4 are worth doing either way and do not depend on P1.

Two smaller questions:

1. **Op-var read-back** — a five-terminal physics model computes ~40 quantities a user would want on
   a plot, and the worker already parses them and excludes them from the settable list. Genuinely
   useful, genuinely a third brief. Say if it should be folded into this one instead.
2. **Where should the compiler preference live** if P1 goes ahead — application settings, or per
   workspace? Application settings is the recommendation: it is a property of the machine, like the
   Python interpreter the PCell path already locates, not of a design.

---

## 5. Naming and licensing posture

Identical to PM1 §6, and repeated here because this brief is the one that touches user-facing text:

- **No model family, version, author, institution, supplier or external file path enters the
  repository from this work** — not in a dialog string, not in a doc page, not in a fixture name.
- **Pre-existing exceptions are flagged in PM1 §6, not changed here.**
- **Citing a published paper or thesis is approved** by the owner; nothing here requires one.
- The model sources are the user's to obtain under their own licences; the compiler is the user's,
  invoked at arm's length; circuitRF ingests neither and stays MIT.

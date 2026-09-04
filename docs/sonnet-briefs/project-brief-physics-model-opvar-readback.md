# Brief PM3 — reading a compact model's operating-point variables back

**Status:** proposed, and **two questions in §3 need answers before P3 starts** · **Date:** 2026-09-03 ·
**Companions:** PM1 (`project-brief-physics-model-backend.md`), which makes these models correct, and
PM2 (`project-brief-physics-model-placement-ux.md`), which makes them pleasant to place. Both list
op-var read-back as a documented omission — PM1 §3 P3 and PM2 §3 — and PM2 §4 asked whether it should
be folded in or written separately. **This is the separate one.** It depends on neither: the worker
half stands alone, and nothing here needs a five-terminal electrothermal device to be finished first.

**The owner's question: can we add op-var read-back?**

**Yes, and most of the mechanism is already in the tree.** A compact model computes tens of internal
quantities — transconductances, junction capacitances, node temperatures — and circuitRF's worker
already parses every one of them and throws them away. What is missing is not a line in that loop.
It is a **place in the result model** and a **way to ask for a value at an operating point**, and the
second of those is a different question in DC than it is in harmonic balance. That is the whole of
why this is a brief and not an afternoon.

Model families are referred to as **Family S** and **Family V**, as in PM1. Neither is named, nor is
any version, nor any path outside the repository — see §6.

---

## 1. What exists already, and the evidence for it

### The worker reads them and drops them, deliberately and in two places

`emit_describe` skips every op-var when it builds the settable parameter list
(`tools/osdi-worker/osdi_worker.c:343`), and `cmd_defaults` skips them again when it probes for
defaults (`:510`). Both are correct and must stay: an op-var is a model **output**, and offering one
as settable would put a writable box in the editor for a value the model computes. The comment at
`:333-338` already says what is missing and why it is a feature in its own right rather than a
forgotten line. **This brief extends that loop; it does not reverse it.**

### The metadata costs nothing, exactly as parameter units and descriptions did

An op-var is an `OsdiParamOpvar` like any other declared quantity (`osdi.h:137-144`) and carries its
own `name`, `units`, `description` and type flags. PM2's parameter picker already established the
precedent: those three fields sit in the descriptor, so reporting them is free, and a model that
declares dozens of quantities is unreadable without them.

### A value is a pointer dereference — but only into the last-evaluated bias

`access(inst, NULL, id, ACCESS_FLAG_READ | ACCESS_FLAG_INSTANCE)` returns a pointer to the op-var's
storage inside the instance struct (flags at `osdi.h:32-34`). The model writes that storage during a
**load**, so the value describes whichever bias was evaluated last — `cmd_eval`
(`osdi_worker.c:716`) writes the instance at the voltages it was handed, and nothing else does.

**This one sentence is the whole design.** A read-back is not a computation the worker performs; it
is a deref, positioned correctly in time. Getting the position wrong yields a plausible number from
the *previous* point — the same failure class as reading a residual offset as an array index, which
`tools/osdi-worker/README.md` §2 records as caught by reading the reference host rather than by a
test going red.

### The engine already walks external devices in the place this needs

`NonlinearDcEngine` iterates `_nl.Components` filtering on `ec.Model is ExternalDeviceModel` at
`:360` (thermal-node collection) and `:528` (the ambient hold's conductance probe). `ec.InstancePath`
is the hierarchical name a read-back would be labelled with. No new traversal is required.

### The result-model shapes are established and are the thing to extend

`DcResultPacker` writes `V` on a **node** axis and `I` on a **branch** axis, plus `__LabeledNodes`
and `__ProbeBranches` as picker provenance; `HbEngine.BuildSingleToneDataSet` (`:1814-1876`) writes
the same cube names one axis wider. The `__` prefix is what makes a cube sweep-invariant through
`DataSet.StackSweepAxis` (`src/RfCore/Data/DataSet.cs:191`).

The Data Display picker maps an axis name to its provenance cube in a **two-line switch**
(`src/Ui/DataDisplay/ViewModels/TraceRowViewModel.cs:2937-2938`). A third axis family is a third
line — provided it is one cube on a labelled axis, not one cube per quantity. See §3 D2.

### Nothing in the repository can gate this yet

`tools/fake-osdi-model/fake_osdi.c` declares exactly **one** op-var (`:177` — `temp_k`, on the RC),
and it is a passthrough of the temperature the host stated. A feature about transconductances cannot
be gated by it. PM1 §1 records Family S at ~40 op-vars and Family V at ~30, computed with `ddx`;
the test model needs a handful with **closed forms** so a read-back can be checked against arithmetic
rather than against itself.

### The doc that has to change

`docs/user/src/reference/veriloga.md` §limits currently reads *"No operating-point variable
read-back… there is not yet a way to plot one."* That sentence is the acceptance criterion.

---

## 2. Phases

### P1 — the worker says what it computes

`describe` gains `"opvars":[{"name","units","description","type"}]` per type, built from the same
loop that currently skips them, with the skip **inverted** rather than removed. The settable `params`
array is untouched: a quantity appears in exactly one of the two lists.

`ExternalDeviceDescriptor` gains `IReadOnlyList<ExternalOpVarDescriptor> OpVars` — additive and
defaulted to empty, so every existing construction site and every other provider is unchanged.

**Trap: a non-real op-var has nowhere to land.** `PARA_TY_INT` and `PARA_TY_STR` are legal
(`cmd_defaults` already branches on all three), and a `DataCube` is single-kind Real or Complex. An
integer op-var is a real; a **string** op-var is reported in `describe` with its type and then
**omitted from read-back**, said once in the code, rather than being silently absent.

**Gate:** the fake model declares op-vars of each type across more than one device type; `describe`
lists them, `params` does not, and the same quantity never appears in both.

### P2 — the worker answers with values

A new command, `{"cmd":"opvars","handle":n}`, reading through `access` on the **live** instance and
answering with one value per declared op-var, in declared order, in the frame's binary payload.

- **It uses the existing handle and allocates nothing.** Unlike `defaults`, which stands a probe
  model up and tears it down, this reads an instance the host already owns — so `MAX_INSTANCES` is
  not touched and there is no slot to leak.
- **It performs no evaluation of its own.** It reports the bias the caller last evaluated. Making it
  evaluate would hide the ordering question rather than answer it, and the host is the only party
  that knows which point is the converged one.
- **`access` returning NULL is an omission, not a zero** — the rule `cmd_defaults` already follows.
- **An unknown command is an error, at `osdi_worker.c:893`.** `senior-worker` and
  `tools/DeviceWorkerExample` host different ABIs and will never implement this. The host must ask
  **only** when `describe` declared at least one op-var. Probing blind turns a working simulation
  into a refusal on two providers that were never in scope.

**Gate:** `verify.py` drives eval → opvars → eval-at-a-different-bias → opvars and checks both
against closed form; a read with no prior eval is defined and tested; 64 reads leave the instance
table where it started.

### P3 — a place in the result model *(DC; needs §3 D1 and D2)*

After `NonlinearDcEngine` converges, one evaluation per external device **at the converged solution**
followed by one `opvars` read, packed by `DcResultPacker` alongside `V` and `I`:

```
"OP"        Real, axis [opvar]  — values, Labels = "<InstancePath>.<opvarName>"
"__OpVars"  provenance, same labels, so the picker can filter and StackSweepAxis passes it through
```

One cube on a labelled axis, matching `I` on `branch` — not a cube per quantity. Family S at ~40
op-vars on a handful of devices is hundreds of names; as separate cubes that is a `DataSet` nobody
can navigate and a picker with no structure to group by.

**Trap: correspondence under a sweep.** After `StackSweepAxis` the value at sweep index *k* must be
the one read at *k*'s converged point. The extra evaluation must therefore happen inside the solve's
own scope, before anything re-evaluates the device at another bias — the trap named in §1.

**Gate:** the fake model's closed-form op-var matches arithmetic at a known bias; a two-point
parametric sweep gives two different values, in the right order, and not two copies of one;
`__OpVars` survives stacking; a run with no external device adds no cubes at all.

### P4 — a way to name one

The measurement and Data Display spelling — see §3 D2. Whatever it is, it resolves against the `OP`
cube's labels by exact match and refuses an unknown name **by name**, the way PM2 P2 requires of a
parameter set: a silently dropped reference is a wrong answer that converges.

**Gate:** a measurement referencing an op-var evaluates headless under `Cli dc` and in the GUI, and
one referencing a name the model does not declare is refused with the name in the sentence.

### P5 — harmonic balance *(deferred by default — §3 D1)*

If D1 defers it, HB gains **no** op-var cube and the omission is written where it lives, per PM1 §3
P3's standing rule: a comment in `HbEngine`'s DataSet builders saying that an op-var at a large-signal
point is a waveform, not a scalar, and that circuitRF reports none rather than reporting the last
sample the Newton loop happened to evaluate. **That last clause is the reason the note is required:**
without it, a future reader adds four lines and gets numbers that look right.

---

## 3. The decisions this brief needs

### D1 — is an op-var a DC quantity only, in v1?

`ExternalDeviceModel.EvaluateBatch` hands the worker a whole set of time samples per Newton
iteration, so in HB an op-var is naturally **per sample** — a waveform on the existing harmonic or
time axis, not a number. Three answers are coherent:

1. **DC only** *(recommended)*. `OP` appears in a DC run and in a DC sweep; HB says nothing and says
   why, per P5. It is the smallest thing that answers the question actually asked — *a designer wants
   to read `gm` at the bias point* — and it is the only one of the three that cannot produce a
   plausible wrong number.
2. **DC plus the HB operating point.** Read at the zero-drive bias only. Cheap, but it is a different
   quantity from the same name at drive, and the label would not say so.
3. **Per sample in HB.** Correct and the most useful eventually; it is also ~40 quantities × the
   sample count × every device, and it needs the same thinking about spectra that `V` and `INl`
   already carry. A phase of its own.

### D2 — what does a measurement call one?

There is no accessor to extend: `V(...)`/`I(...)` are hand-resolved and `Evaluator.RegisterFunction`
is the only registration point. So this is a **language decision**, not a refactor:

- `OP("X1.gm")` — a new function, symmetrical with the qualified cube accessors the measurement
  evaluator already documents (`MeasurementEvaluator.cs:14`).
- `DC1.OP("X1.gm")` — qualified, matching `HB1.V("n_drain", 1, All)`.

Recommendation: both, since qualification is already how one names a cube from a specific analysis
and the unqualified form is the same resolution with the analysis defaulted.

### D3 — how much does a read-back cost when nobody asked for one?

P3 as written adds one evaluation per external device per converged DC point, always. That is one
extra worker round trip per device per sweep point — negligible next to the solve, but not zero, and
it is paid by a user who never plots an op-var.

Recommendation: **pay it**, and revisit only if measured. The alternative is a per-run switch, and a
result that is present or absent depending on a setting is the thing the Data Display's picker is
worst at explaining. Say this in the code so the choice is visible.

---

## 4. What this does not do

- **No change to the settable parameter list.** Op-vars stay out of it. That property is the entire
  reason this is read-*back* and not a second kind of parameter.
- **No noise analysis.** Unchanged from PM1 §4 and PM2 §3; `load_noise` is still never called.
- **No new component species.** This is `SymbolKind.VerilogA` and the OSDI worker, as before.
- **Nothing for the other two workers.** `senior-worker` hosts a proprietary ABI and
  `tools/DeviceWorkerExample` is a reference implementation; the protocol addition is optional and
  both stay silent. Do not fork one into another — the standing rule in the osdi-worker README.
- **No kit/PDK path change.** This is the *user supplies one file* path.
- **No hand-port of any model.** `docs/PRD.md` §6.1 stands.

---

## 5. Test posture

Per the repo's standing rule: run the suite **once** and read the TRX. The projects this can reach
are `Core.Tests`, `Engine.Tests` and `Ui.Tests`. `tools/osdi-worker` has its own `build.sh` and
`verify.py`, which are **not** part of `dotnet test` and must be run separately — and the fake-model
work in P1's gate is where most of this brief's real risk is retired.

No new `Category=Benchmark` timing test. D3's cost question, if it needs an answer, is answered with
a scratch harness rather than a test that measures the machine; the structural assertion that belongs
in the suite is a **counter** — one evaluation per device per converged point, not one per op-var.

---

## 6. Naming and licensing posture

Identical to PM1 §6 and PM2 §5:

- **No model family, version, author, institution, supplier or external file path enters the
  repository from this work** — not in a dialog string, not in a doc page, not in a fixture name.
  Op-var *names* are the model's own and are opaque: rendered, never interpreted, never hardcoded.
- **Pre-existing exceptions are flagged in PM1 §6, not changed here.**
- **Citing a published paper or thesis is approved** by the owner; nothing here requires one.
- `osdi.h` stays byte-identical, MPL-2.0, and separate — extending the worker never edits it. Its
  `PARA_KIND_*` macros are signed and overflow at `3 << 30`; the masks are re-expressed as unsigned
  at the call sites, which is where `KIND_OPVAR` (`osdi_worker.c:288`) already lives.

---

## On completion

Findings go in the **`RESOLVED.md`** beside the code that changed — `src/Core/RESOLVED.md`,
`src/Engine/RESOLVED.md`, `src/Ui/RESOLVED.md` — never in a `CLAUDE.md`. Delete the
*"No operating-point variable read-back"* bullet from `docs/user/src/reference/veriloga.md` §limits
and regenerate the HTML; amend PM1 §3 P3's table row and PM2 §3's line to point here rather than
leaving them reading as open omissions.

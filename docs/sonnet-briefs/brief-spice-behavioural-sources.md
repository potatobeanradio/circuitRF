# Sonnet Brief — SPICE behavioural sources: `E`/`G … VALUE={…}`, nonlinear charge, and `.func`

**Design:** `docs/design/spice-models.md` — §8 measures the gap, §9 is the design. Read §9.0 first.
Also `docs/design/sdd.md` (§"implicit equations" — the out-of-scope note this brief closes),
`docs/design/expressions.md`, `docs/design/nonlinear-dc.md` §4.
**Sibling brief:** `brief-spice-library-workflow.md` — archiving a deck's include closure, choosing a
`.lib` section, and importing more than one definition from one file. **No dependency either way**;
that one is UI-and-reader-entry-point work with a 30-second test gate, this one reaches the nonlinear
solver.
**Code:** `src/Core/Netlist/Spice/` (`SpiceNetlistReader`, `SpiceExpression`), `src/Ui/Schematic/`
(`SubcircuitTranslation`, `SpiceModelNetlist`), `src/Core/Devices/SddModel.cs` +
`src/Core/Expressions/Sdd*.cs`, `src/Core/ComponentModel.cs` (`NonlinearResult`),
`src/Engine/NonlinearDcEngine.cs` (`BuildResidualAndJacobian`),
`src/Engine/HarmonicBalance/HbLinearExtractor.cs`.

**One sentence:** circuitRF reads library files in the SPICE dialect end to end and then refuses
almost every device in them, because it has no behavioural sources — and because those sources are
how this whole family of models writes its nonlinear **charge**.

---

## 0. The measurement this brief is built on

Four real library files (two power switches, a switch+diode library, a gate driver) were run through
`SpiceNetlistReader` and `SpiceCellImport` on 2026-09-01. **All four parsed. Of 45 subcircuits,
exactly 1 could be imported** (plus 8 `.model` cards). None of the four files even uses a `.lib`
*section* — the extension is not the obstacle.

Every subcircuit was then classified by which capabilities it needs, with refusal propagated up
through every `X` call that reaches it (what `SubcircuitTranslator.ResolveDependencies` does):

| Capability set | Importable | Top-level parts |
|---|---|---|
| today, as shipped | **1** of 45 | 1 of 10 |
| \+ reader hygiene incl. `TEMP` (M1) | 1 | 1 |
| \+ `V`/`I` sources and affine `E`/`G` (M2) | 1 | 1 |
| \+ nonlinear `G` via the SDD with `.func` inlined (M3) | 1 | 1 |
| \+ **nonlinear `E` and the charge idiom** (M4) | **22** | **7** |
| \+ `time` read as steady state (M1) | **37** | **9** |
| \+ `TABLE` and switches (deliberately not built) | 45 | 10 |

**Read that table before planning anything.** The obvious ladder — hygiene, then cheap elements,
then the SDD, and leave the behavioural voltage source for later because it is expensive — ships
three milestones that move the count from 1 to 1. Every part that matters depends, transitively, on
a subcircuit containing a nonlinear behavioural **voltage** source. M4 is load-bearing; M1/M2/M3 are
its prerequisites, not independently shippable value.

Element forms actually present (234 `E`/`G` lines, **all** of them the `VALUE` form — no positional
gain, no `POLY` anywhere):

| Form | count |
|---|---|
| nonlinear `E`/`H` — behavioural **voltage** | **123** |
| affine `E`/`H` | 53 |
| nonlinear `G`/`F` — behavioural **current** | 51 |
| …of which call a `.func` | 35 |
| `TABLE` | 7 |

The behavioural voltage source outnumbers the current source better than two to one — the opposite
of what an SDD-shaped intuition suggests, and the reason the table above is so flat until M4.

**And most of those voltage sources exist to carry a CHARGE.** Not one of the four files has a
voltage-dependent capacitor, a `Q={…}`, a `ddt()`, or a `.model` capacitor with voltage
coefficients: every capacitor is constant-valued, and every nonlinear capacitance is written as a
behavioural voltage source driving a linear one —

```
E_Edg4  d    ox4  VALUE {-(V(g,d) - Q02(V(g,d))/Cdg4)}
C_Cdg4  ox4  g    {Cdg4}
```

— whose stored charge works out to exactly `−Q02(V(g,d))`, the capacitor value cancelling. **8 such
pairs across the four files, six in one subcircuit.** Charge is therefore not a follow-on to the
behavioural-source work; it *is* that work, and M4 is not done until §2's charge oracles pass.

**Nothing from those files may enter the repository** — no name, no part number, no fixture. Every
test fixture in this brief is synthetic, per `docs/design/pdk-import.md` §0. Cite a count, never an
identity.

---

## 1. Structural facts

1. **A branch unknown is allocated in the LINEAR stamp pass, which runs for nonlinear models too.**
   `NonlinearDcEngine` calls `Stamp` on every component while building the constant `_gAug`
   (`NonlinearDcEngine.cs:276-305`), which is how `Vdc`'s and `IProbe`'s branches come to exist and
   how `SddModel.ControlBranchIndices` can be resolved afterwards. So a nonlinear model may call
   `mna.AddBranch()`, `AddBranchCurrent()`, and `AddConstraint(br, a, +1)`/`(br, b, −1)` in its
   `Stamp` and get **the ±1 KCL coupling and the `V(a) − V(b)` half of the constraint row for free,
   as constants.** M4 adds only the per-iteration remainder.

2. **The per-iteration remainder is shaped exactly like `DControl`, transposed.**
   `BuildResidualAndJacobian` adds `res.I[p]` to node rows, `res.Dg[p,q]` to node×node, and
   `res.DControl[p,ci]` to (node row, branch col). M4 needs (branch row) residuals, (branch row,
   node col) and (branch row, other-branch col) derivatives. Every `IMnaContext` primitive it needs
   already exists: `AddConstraint`, `AddBranchConstraint`, `AddNodeBranchCoupling`, `AddSourceValue`.
   **No new MNA primitive.**

3. **The SDD cannot call a `.func` today.** `SddEvaluator`, `SddCompiled` and `SddRegisterProgram`
   each end their function switch in `default: throw new UnknownFunctionException`. Separately,
   `UserFunction` lives on `TestBench` — one flat namespace for the whole design — so two imported
   files that both define `ni(T)` would collide silently. **Inlining at compile time solves both**:
   `CompiledSddExpr` is built once per model, so substituting a function's AST for its call site
   costs nothing per evaluation and never touches the shared namespace.

4. **`limit` is currently classified as a statistical distribution and reduced to its first
   argument** (`SpiceExpression.Statistical`). 65 occurrences across the four files are the
   three-argument clamp, and all 65 have their clamp silently removed. The two readings are
   separable by **arity**: 2 arguments is the distribution (keep the reduction and the report),
   3 arguments is `min(max(x, lo), hi)`.

5. **`PARAMS:` is skipped on a `.subckt` line and not on an `X` line.** `ReadSubcktHeader` skips it
   explicitly; `ReadElement` takes the reference from the end of the bare-word run, so
   `X1 a b c SUB PARAMS: k=1` records a call to a subcircuit named `PARAMS:`. **25 of the 45
   subcircuits contain such a line.** It unlocks nothing on its own (§0) but it is why 25 of them
   currently report a refusal that names the wrong thing.

6. **One refused element refuses the whole subcircuit, and refusal propagates up through `X` calls.**
   That rule is correct and stays (a netlist with a line missing is not a smaller circuit, it is a
   different one that elaborates and produces numbers). It is also **why §0's table is so flat**: a
   single unsupported line anywhere in a dependency chain refuses everything above it, which is how
   123 nonlinear `E` lines come to gate 44 of 45 subcircuits.

7. **circuitRF has no transient analysis** (`docs/PRD.md`). Three `E` sources reference `time`,
   two files use `S`/`W` switches and one uses `TABLE`. `time` inside a **condition** has a
   well-defined steady-state reading and is in scope (M1); everything else here stays refused — **by
   name**, with a sentence saying what circuitRF does not have, never the generic "a kind this reader
   does not read".

## 2. Milestones

M1 → M2 → M3 → M4 is one dependency chain and, per §0, **only M4 moves the count**. M1/M2/M3 are its
prerequisites, not independently shippable value — say so rather than presenting them as progress.

### M1 — reader hygiene, and refusals that name the real reason

No new capability. Its deliverable is that a refused subcircuit says *why* truthfully.

- `PARAMS:` skipped on an `X` line (fact 6), the same way `ReadSubcktHeader` skips it.
- `limit` split by arity (fact 5): 3 args → `min(max(x,lo),hi)`; 2 args → unchanged, still reported
  in `Statistics`.
- `sgn` → `sign` in `SpiceExpression`.
- An inner `{…}` inside a larger expression is grouping and is stripped —
  `{LIMIT((a*(b/300)**{c}),-1,1)}` currently fails to parse at the inner brace.
- `,IC=…` glued to a passive's value: keep the value, note the `IC` and drop it (it is a transient
  initial condition; circuitRF has no transient analysis).
- **`TEMP` bound to circuitRF's ambient** — `ambientC`, already threaded through
  `ComponentModelFactory.TryCreate`. One reserved identifier, and without it the only part across
  the four files that needs no thermal termination from the user (it drives its own thermal node
  with `E1 Tj w VALUE={TEMP}`) fails to resolve.
- **`time` inside a condition read as steady state**: `if(time > 0, a, b)` → `a`, with a note.
  Three occurrences, all in start-up-suppression or differentiator blocks. This is an
  interpretation of a transient construct and must say so out loud — and it is worth **15
  subcircuits and 2 top-level parts** once M4 exists, because one such line sits inside a
  subcircuit that two otherwise-clean parts depend on. A `time` reference *outside* a condition has
  no steady-state reading and stays refused by name.
- Every element letter circuitRF will not implement (`S`, `W`, `T`, `O`, `U`, and the `TABLE`/
  `LAPLACE`/`FREQ` forms of `E`/`G`) gets a **named** refusal saying what the element is and what
  circuitRF does not have — not the generic unknown-letter message.
- A `.model` card declared inside a `.subckt` is hoisted globally today; the existing redefinition
  note should say the card was *local* to a subcircuit, so the collision is legible.

### M2 — independent sources and affine controlled sources

Reader: add `V`, `I`, `E`, `G`, `F`, `H` to the `Elements` table; parse both `VALUE = {…}` and
`VALUE {…}` (the second is two bare words after `SplitBareAndAssignments`, the first is one
assignment). Translation:

| Line | Becomes |
|---|---|
| `Vx a b 0` | `IProbe` (this is the zero-volt sensor idiom — most `V` lines in these files) |
| `Vx a b <dc>` / `DC <dc>` | `Vdc` |
| `Vx a b … AC …`/`PULSE`/`SIN`/`PWL` | `Vdc` at its DC value, **noted**: circuitRF drives a design from its own TestBench |
| `Gx a b c d <gm>` or affine `VALUE` | `VCCS` (exists) |
| `Ex a b c d <k>` or affine `VALUE` | new **`VCVS`** — linear, one branch, stamped like `VdcModel` |
| `Fx`, `Hx` | control-current reference to the named source's branch |
| `Ix a b <dc>` | a DC current-source primitive (none exists; smallest thing that works) |

"Affine" = a constant-coefficient combination of `V()`/`I()` terms: no function call, no product or
quotient of two terminal quantities, no `**`. Decide it on the parsed AST, not on the text.

**Expect the importable count to stay at 1** (fact 0). That is the correct outcome, not a failure —
report it and continue.

### M3 — nonlinear `G` through the SDD, with `.func` inlined

`G a b VALUE={f}` becomes an `SddModel`:

- port 1 spans the `G`'s own two nodes and carries `I[1,0] = f`;
- each distinct `V(x,y)` on other nodes becomes an extra **sense port** with `I[p,0] = 0`;
- each `I(Vx)` becomes a control-current reference — `SddModel.ControlRefs`, resolved by
  `ResolveControlCurrentBranches` against `Vdc`/`IProbe`/inductor branches, which `sdd.md` already
  documents as sensable and which is honoured in DC, HB and S-parameters alike.

`.func` inlining (fact 4): substitute a `UserFunction`'s AST at its call site with arguments bound,
**once, at `CompiledSddExpr` construction**, so `SddEvaluator`, `SddCompiled` and
`SddRegisterProgram` all get it and none of them needs a new function case. Memoize per (function,
depth) and cap the expanded node count; on exceeding the cap, refuse **by name and by number**
("`Jh` expands to N nodes, over the limit of M"), never truncate.

**First, fix the drop.** `SubcircuitTranslator.TranslateAll` reads `Library.Cells`, `ModelCards` and
`IncompleteCells` and nothing else — it never touches `result.Functions` or `result.Variables`, and
neither does `SubcircuitCellBuilder`. `SpiceModelNetlist` copies the cell's own `Variables` and no
functions. **Every `.func` a file declares is parsed correctly and then discarded by both consumers**
(8, 102, 0 and 9 across the four files, in files whose devices are built out of them). Route them
into the translation before inlining anything, or M3 inlines an empty table.

Global `.param` (`result.Variables`) has the same hole and a different answer: a cell cannot hold a
global, so it goes to the `TestBench` through the existing `NetlistImports.MergeInto`. Give it the
**variable**-style conflict report — that method currently reports a duplicate variable and skips a
duplicate function with no report at all, which is the wrong way round for the risky one. After
inlining, no function reaches that namespace anyway, which is the point (`spice-models.md` §4.2).

**Measure the expansion before choosing the cap.** The worst case available is a file whose ~100
`.func` definitions are mutually nested three deep with a body appearing three times in its caller.
Report the measured node counts.

### M4 — the nonlinear branch row *(the milestone the count moves on)*

`ComponentModel`/`NonlinearResult` gains an optional branch block, mirroring `DControl`:

- `BranchResidual[k]` — the value of `g(v, i)` for branch equation k;
- `DBranchV[k, q]` — `∂g_k/∂v_q` over the model's ports;
- `DBranchC[k, n]` — `∂g_k/∂i_n` over its control-current references.

The model owns its branches by calling `AddBranch`/`AddBranchCurrent`/`AddConstraint` in `Stamp`
(fact 2), so the constant half is already in `_gAug`. Then one stamping loop each in:

- `NonlinearDcEngine.BuildResidualAndJacobian` — `f[br] −= BranchResidual[k]`, and `−DBranchV`,
  `−DBranchC` into (branch row, node col) and (branch row, other-branch col);
- `HbLinearExtractor` — the same per harmonic, alongside the existing `DControl` handling;
- `StampLinearized` — the S-parameter linearisation about the DC point, which already receives the
  bias.

Then `E a b VALUE={f}` becomes that device with one branch equation `V(a) − V(b) − f = 0`, built
from the same translation M3 wrote (sense ports, control currents, inlined `.func`s).

**`DBranchC` is on the critical path, not an edge case.** The first file that would work leans on
it twice — `E_E001 b 0 VALUE {-I(V_sense2)}` and `E_Eds4 … I(V_sense3)/Cds4` are branch-row
equations whose derivative is with respect to *another branch current*. Build and test it alongside
`DBranchV`, not after.

**Do not build the penalty formulation** (`I = G_big·(V(a,b) − f)`). It is a different circuit,
silently, and its conditioning depends on what the source drives. It was considered and rejected in
`spice-models.md` §9.2.

#### M4 is not done until charge is right

Six of one subcircuit's six `E` sources exist only to carry a nonlinear charge (§0). Two conditions,
both assertions to test rather than things to hope for (`spice-models.md` §9.5.1):

- The branch residual is evaluated **in the time domain and transformed**, like any other nonlinear
  contribution. A branch row linearised once about the DC point yields the small-signal capacitance
  and **silently loses every harmonic of the charge** — it converges, and it is wrong.
- The branch block carries **no `Q` counterpart**. The residual is algebraic in `v` and `i`; the
  charge lives entirely in the linear capacitor, and `jkω·C·V_k` applied to the harmonics of
  `−Q(v(t))` is already exactly `−dQ/dt`. Adding a charge term to the branch block double-counts.

**Report three numbers, not one.** How many subcircuits import (§0 predicts 22 of 45, 37 with the
`time` rule); how many reach a **DC solution**; and how many pass the charge oracles in §3. A
logic-style macromodel built from ~100 `if(…)` sources is discontinuous by construction and may
simply not converge — that is a real answer, not a failure of this milestone.

### M4b — collapse the charge pair *(optimisation; only after M4 measures)*

When the pattern matches exactly — an `E` from `n+` to an interior node `mid`, a linear capacitor
`K` from `mid` to `n−`, and **nothing else connected to `mid`** — the pair is algebraically one
one-port with charge `Q(v) = K·f(v)`, which is the SDD's existing `I[p,1]` bucket. Emitting that
drops one node and one branch row per charge (six per device in the worst case measured, × N
harmonics) and makes the device eligible for the SDD grid evaluator.

- **"Nothing else on `mid`" is the entire correctness condition**, and it must be checked on the
  elaborated netlist, not on the text.
- The general path stays the default. **A test must assert the two paths agree entry-by-entry** on a
  fixture where both apply; if they ever disagree, the collapse is wrong, not the general path.
- Build it only if an HB measurement with and without justifies it.

### M4c — direct charge spellings *(independent of M4b)*

None of the four files uses these; other suppliers write the same physics this way, and
`SpiceModelPeek` accepts any file the user points at. All three are translation-only:

| Written | Becomes |
|---|---|
| `Q={expr}` on a capacitor | SDD `I[p,1] = expr` |
| `Gx a b VALUE={ddt(expr)}` | SDD `I[p,1] = expr` — `ddt` is the charge marker, not a function to evaluate |
| `C={polynomial in v}`, or `.model CAP` with `VC1`/`VC2` | `NonlinearCModel`, which already integrates |

**The trap, and it is silent:** `C = f(v)` declares the small-signal *capacitance*, so
`Q = ∫₀ᵛ f(u) du` — **not** `Q = f(v)·v`. The two agree only for constant `f`. `NonlinearCModel`
already integrates correctly for a polynomial. A general non-polynomial `C={expr}` has no symbolic
integral available, so **refuse it by name** ("write this one as `Q={…}`") rather than approximate.
A wrong charge law converges and produces plausible numbers.

---

## 3. Tests

**Every fixture synthetic** — the repository commits no third-party kit
data (`docs/design/pdk-import.md` §0). `Core.Tests/Netlist/` for the reader and expressions,
`Ui.Tests/` for translation and symbols, `Engine.Tests/Nonlinear/` for M4.

- **M1** — one fixture per item: an `X` line with `PARAMS:` resolves to the named subcircuit;
  `limit(a,b,c)` becomes a clamp and `limit(a,b)` stays a reported reduction (assert on
  `Statistics` for both); `sgn` evaluates; a nested `{…}` parses; `C1 a b 1u,IC=0` keeps 1 µF and
  notes the `IC`; an `S` line's refusal contains the word "switch"; `TEMP` resolves to the design's
  ambient and **changes** when the ambient does; `if(time>0,a,b)` becomes `a` **and** emits a note,
  while a bare `time*k` is refused by name.
- **M2** — an affine `E`/`G` fixture solved against a closed-form oracle (a VCVS in a divider has an
  algebraic answer); `V… 0` becomes an `IProbe` whose current a `SpiceModel` can reference; a
  `PULSE` source is read as its DC value **and** produces a note.
- **M3** — a `G` whose expression senses a node it is not connected to (asserts the sense port
  carries zero current); a `G` referencing `I(Vx)` (asserts the DC branch column, the existing
  `sdd.md` §control-current oracle); a `.func` calling a `.func` calling a `.func` inlines and
  evaluates; two files declaring the same `.func` name in one design do **not** collide.
- **M4** — an `E` source in a resistive divider against a closed-form oracle at DC; the same
  circuit's S-parameters against the analytic VCVS result; an `E` whose expression is nonlinear in a
  port voltage, gated on the *converged bias*, not on the residual norm; an `E` whose expression is
  a function of a **branch current** (`DBranchC`, on the critical path — see M4); and an S-parameter
  run where the referenced branch index differs from the DC one (`sdd.md` records that the S-param
  engine re-resolves — this must keep working).
- **M4 charge oracles** — the part whose errors are invisible in a converged answer, so three gates
  of increasing strength (`spice-models.md` §9.5.4), each on a synthetic `E`+`C` pair with a known
  charge law:
  1. **Small-signal**: linearise at bias `v₀`; the port susceptance equals `jω·dQ/dv|v₀`, compared
     against the **analytic derivative of the charge function** — never against another circuitRF
     path.
  2. **Charge conservation**: over one HB period, `∮ i dt = 0` to solver tolerance on every charge
     branch. A resistive contamination of a charge term breaks this and nothing else catches it.
  3. **Harmonic content**: drive the pair hard enough that `Q(v)` generates a second harmonic, and
     compare against the analytic Fourier coefficients of `dQ/dt`. This is the one that fails if the
     branch row was linearised about DC instead of transformed (M4's first condition).
  Plus: a DC-only check that the interior node is non-singular with the capacitor open (two
  equations, two unknowns — `spice-models.md` §8.8), which is the case that looks like it should
  fail and does not.
- **M4b/M4c** — the collapsed path agrees with the general path entry-by-entry on a fixture where
  both apply; a third element on the interior node correctly *prevents* the collapse; `Q={…}` agrees
  with the equivalent `E`+`C` pair written longhand; a polynomial `C={…}` gives `∫C dv` and **not**
  `C(v)·v` (assert they differ for a non-constant polynomial, so the test cannot pass by accident);
  a non-polynomial `C={…}` is refused by name.
- Regression: all existing `Spice*Tests` in `Core.Tests` and `Ui.Tests` green and unchanged.

---
- Regression: all existing `Spice*Tests` in `Core.Tests` and `Ui.Tests` green and unchanged.

---

## 4. Gates

`dotnet test tests/Core.Tests` then `dotnet test tests/Ui.Tests` — two invocations, this SDK rejects
two project paths in one. M4 additionally `dotnet test tests/Engine.Tests` (~3 min 24 s). **Run once
and read `TestResults/last-run.trx` for failures** — never re-run to find out what broke.

Per milestone, report the importable-subcircuit count against the owner's own files (run the reader
over them from a scratch harness; **do not commit the files, a fixture derived from them, or their
names**), so §0's table is confirmed or corrected rather than assumed. **M1's expected delta is zero
importable subcircuits and 25 corrected refusal messages.**

No new `Category=Benchmark` timing test. If M3's inlining or M4b's collapse needs a cost number,
measure it in a scratch harness in Release, not in the test suite.

---

## 5. On completion

Findings to **`src/Core/RESOLVED.md`** (reader, expressions, translation) and
**`src/Engine/RESOLVED.md`** (the branch row, charge). **Never to any `CLAUDE.md`.** Update
`docs/design/spice-models.md` §8 with the measured after-counts and close the §13 open questions that
got answered. Do not commit; the owner commits.

Before any commit is proposed, grep the diff for vendor, product, part and simulator-dialect names —
`docs/design/pdk-import.md` §0 — and report what was removed.

---

## 6. Out of scope, deliberately

- **Transient analysis**, and therefore `S`/`W` switches, `TABLE` transfers, and `IC=` as anything
  other than a note. These are the last 8 of the 45 subcircuits; refusing them **by name** is the
  deliverable. (`time` inside a *condition* is in scope — M1 — because steady state gives it a
  well-defined reading; `time` anywhere else is not.)
- **`POLY(n)`** — absent from all four measured files. If it turns up it expands to an ordinary
  expression and costs nothing; do not build it speculatively.
- **Per-subcircuit `.model` card scoping.** The reader hoists local cards globally and notes a
  redefinition; no measured file collides. M1 only improves the wording.
- **A structural guard for an undriven thermal pin.** `spice-models.md` §7.1.1: once M1 restores
  `limit` as a clamp, a floating `Tcase` stops diverging and starts converging *at the clamp limit* —
  a correctness fix that turns a loud failure into a quiet wrong answer. The detection ("a node whose
  only DC path is `gmin`, feeding a clamp") is a general engine question. **Raise it as its own
  brief; do not bolt it on here.**
- **Sampling statistical distributions.** Reducing to nominal and reporting it stays exactly as it
  is; M1 only stops mistaking a clamp for a distribution.
- Everything in `brief-spice-library-workflow.md`.

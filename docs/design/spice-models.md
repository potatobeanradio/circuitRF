# circuitRF — SPICE Models: reading, importing, placing, archiving

**Status:** §1–§7 Shipped · §8–§9 Shipped (see §8.0) · §10–§12 Proposed · **Date:** 2026-09-01

How circuitRF reads a netlist written in the SPICE dialect today — the reader, the two import
gestures, the placed `SpiceModel` component, and what happens to the referenced file when a
workspace is archived (§1–§7). Then what a **library file** needs on top of that, measured against
real library files rather than guessed at (§8–§12).

Companions: `expressions.md` (the one expression engine), `sdd.md` (the equation-defined device),
`pdk-import.md` (§0's naming rule, and the corner mechanism this shares), `data-model.md` §5 (how a
component type is added), `workspace-and-project-tree.md` (the `.cws`, cells, archiving).

---

## 0. The standing rule

**No vendor, product, kit or part name appears anywhere in circuitRF** — code, comments, tests,
fixtures, log output or documentation. That rule is stated in full in `pdk-import.md` §0 and applies
here without change. It also covers the names of other simulators and their dialects: this document
says *the dialect*, *one dialect*, *some dialects*, never a tool name.

Every measurement in this document was taken by running circuitRF's own reader over library files
the owner supplied. **Those files are not in the repository and must never be**; what is recorded
here is the count and the shape, never the identity.

---

## 1. What "a SPICE model" is in circuitRF

Two gestures, one reader beneath them.

| Gesture | What the user gets | Where it lives |
|---|---|---|
| **Import** — project tree ▸ *Copy to Workspace as Cell…*, File ▸ *Import ▸ Model or Subcircuit…* | A cell **folder** in the workspace: a `.csch` they can open, edit, re-symbol, and place like anything they drew | `SpiceCellImport` → `SubcircuitCellBuilder` / `ModelCardCellBuilder` |
| **Place** — the `SpiceModel` palette component | An instance that **points at the file on disk**. No folder, no copy; the symbol is generated from the definition, and the netlist is built at extraction time | `SpiceModelSymbolProvider` → `SpiceModelPeek` → `SpiceModelNetlist` |

The two are deliberately the same translation. `SubcircuitTranslator` and
`SpiceModelCardTranslation` are read by both, so a file cannot be classified one way by the import
dialog and another way by a placed component. What differs is only *where the result is written*.

Neither gesture filters on extension in the places that matter: the import picker and the
`SpiceModel` file picker both offer `*.lib`, `*.cir`, `*.sp`, `*.spi`, `*.txt` and more, and
`SpiceModelPeek.KnownExtensions` includes `.lib`. Only the **project-tree context menu** narrows
itself (`ModelCardCellBuilder.IsSpiceCellFile`), on the grounds that a dead menu item on most of a
workspace is worse than one extra menu step — that is a UI decision about a context menu, not a
statement about what the reader can read.

---

## 2. The reader — `src/Core/Netlist/Spice`

`SpiceNetlistReader` turns a deck into the same `Library` of `Cell`s that circuitRF's own `.cnl`
produces. A `.subckt` becomes a cell; its elements become `Instance`s; its `PARAMS:` bindings become
`ParameterDeclaration`s. Nothing downstream can tell a cell came from this reader.

Five properties are structural and everything else follows from them:

1. **It reads a FORMAT, not a kit.** Nothing in it names a supplier or a model family.
2. **Nothing is dropped in silence.** Every line it could not use becomes a
   `SpiceNetlistNote(File, Line, Message)`. An included file's line 12 and the including file's
   line 12 are different lines, which is why the note carries the file.
3. **"Incomplete" means a line of the DEFINITION was skipped**, not that a type was unfamiliar. A
   skipped `.tran` does not make a cell incomplete — the circuit is still the one the file wrote. A
   skipped element does, because what is left is a plausible-looking *different* circuit that
   elaborates, simulates and produces numbers. `SpiceNetlistResult.IncompleteCells` carries that
   set, and `SubcircuitTranslator` turns membership in it into a refusal.
4. **One refused element refuses the whole subcircuit** (§3 above, one level up).
5. **Model cards bind in a second pass** (`SpicePassiveModelBinding`), because a card may be
   declared after — or in a different file from — the subcircuit that uses it, and a single-pass
   answer would depend on read order.

### 2.1 What it recognises today

**Directives.** `.subckt`/`.ends`/`.eom`; `.param`/`.params`/`.parameter`; `.func`/`.function`;
`.model`; `.include`/`.inc`; `.lib` in **both** forms (`.lib <name>` opens a section, `.lib <file>
<section>` reads one out of another file); `.endl`; `.if`/`.elseif`/`.else`/`.endif` with a real
condition evaluation. Roughly thirty simulator directives (`.tran`, `.probe`, `.option`, …) are
recognised well enough to be *named and skipped* — a deck full of them must read as understood, not
as a deck full of mysteries.

**Elements**, by leading letter:

| Letter | Nets | Names a model? |
|---|---|---|
| `R` `C` `L` | 2 | value **or** a card, decided by whether the third word reads as a value |
| `D` | 2 | yes |
| `Q` | 3 (+substrate) | yes |
| `M` | 3 minimum | yes — three nets is a vertical device, four a lateral one |
| `J` | 3 | yes |
| `N` | 2 minimum | yes — a device backed by a compiled model |
| `X` | as many as the definition has ports | yes (a subcircuit) |

The name of what implements a device is taken from the **end** of the bare-word run, not by
position. That one rule covers a three- and a four-terminal device and a subcircuit call of any
width without guessing.

**`.model` cards** translate to circuitRF components through `SpiceModelCardTranslation`:
`D`, `NPN`, `PNP`, `NMF`, `PMF`, `NJF`, `PJF`, `NMOS`, `PMOS`, `VDMOS`, `BEAD`, `RES`/`R`,
`CAP`/`C`, `IND`/`L`. Anything else is refused by name, with the card's own type in the sentence.

### 2.2 Line assembly and tokenising

`Join` handles the leading-`+` continuation and reports a joined line at the number it *started* on.
`Words` splits on whitespace **but keeps a bracketed or quoted run whole** — an expression in this
dialect routinely contains spaces, and splitting one produces a value plus several words that look
exactly like net names. `SplitBareAndAssignments` accepts `k=v`, `k = v`, `k =v` and `k= v` as one
binding, and returns bare words in order so a positional read is safe.

---

## 3. The expression bridge — `SpiceExpression`

**circuitRF has one expression engine** (`expressions.md`): tokenize → Pratt-parse → AST →
evaluate, never string substitution. The SPICE reader does not get a second one. `SpiceExpression`
is the single place a value written in the dialect is rewritten into circuitRF's own grammar, and
**every rewrite there is a spelling change with one deliberate exception**:

- `'…'` and `{…}` wrappers are stripped when a matched pair spans the whole value.
- `**` → `^` (both right-associative, both bind tighter than the arithmetic operators).
- `cond ? a : b` → `if(cond, a, b)`, split on the first top-level `?` and the `:` that *matches* it.
- Numeric literals normalise through `SpiceNumber` (the engineering suffixes: `m`, `u`/`µ`, `n`,
  `p`, `f`, `k`, `meg`, `g`, `t`, and `mil`).
- Whitespace outside quotes is removed — a hard requirement, not tidying: circuitRF's own instance
  parser splits on whitespace and reads bare words as nets, so an unquoted value containing a space
  becomes a value plus phantom nets, which shifts every later node index and **still runs**.
- **The one change of meaning:** a statistical distribution call is reduced to its first argument
  (its nominal) and the reduction is *reported* through `SpiceNetlistResult.Statistics`. circuitRF
  does not sample distributions; running at nominal is what the user almost certainly wants; doing
  it in silence is not acceptable, because the resulting number is indistinguishable from a value
  that carried no distribution at all.

`.param` becomes a `Variable` (outside a subcircuit) or a `ParameterDeclaration` with a default
(inside one — this dialect lets a call site override one, and reading it as a sealed internal
variable would give a placed part one size forever). `.func` becomes a `UserFunction`, which is the
same type `.cnl` and the kit reader produce.

### 3.1 Where user functions actually live

`UserFunction` is stored on the **`TestBench`**, one flat namespace for the whole design
(`TestBench.Functions`, read by `Elaborator`). `Cell` has no function list. Three consequences,
all of which matter in §9:

- A `.func` imported from a file has to be **hoisted to the TestBench** to be callable at all.
- Two files that both define `ni(T)` collide, silently, in that one namespace.
- `SddEvaluator` / `SddCompiled` / `SddRegisterProgram` — the three compile paths of the
  equation-defined device — **do not resolve user functions at all**. Their function switch ends in
  `default: throw new UnknownFunctionException`. An SDD equation cannot call a `.func` today.

### 3.2 The built-in function set

`exp`, `log`/`ln`, `log10`, `sqrt`, `pow`, `abs`, `sin`, `cos`, `tan`, `asin`, `acos`, `atan`,
`atan2`, `sinh`, `cosh`, `tanh`, `min`, `max`, `sign`, `floor`, `ceil`, `round`, `int`, `if`, plus
the complex/cube helpers. The SDD path carries a subset of the same names.

Two dialect spellings have no home in it: **`sgn`** (circuitRF spells it `sign`) and **`limit`**.

---

## 4. Import — a `.subckt` or a card becomes a cell folder

`SpiceCellImport.Scan(path)` reads the file once and returns everything importable in it —
subcircuits first, then cards, because a file holding both almost always states the cards as
*support* for the subcircuit that is the part. `SpiceCellPickerDialog` shows that one list;
`SpiceCellImport.Write` builds the chosen candidate.

A subcircuit's nested calls each become a cell of their own (`SubcircuitTranslation.Dependencies`,
leaf-first) — a circuitRF cell instance references a cell folder, so there is nowhere else for a
nested definition to live.

### 4.1 A file with several definitions: nested works, siblings do not

**"Should a multi-definition file import as several cells?" is not really a choice.** A circuitRF
cell instance references a cell *folder*; there is no representation in which one cell holds five
subcircuits. So the only open question is *how many at once*.

**Nested definitions already do the right thing.** Importing one subcircuit writes one cell per
transitive dependency, leaf-first, each under its own `.subckt` name (the chosen one takes the name
the user typed; a nested one takes its own, because there is nobody to ask). Every cell lands in the
same parent folder and reaches its children by `"../../" + name`.

**Siblings did not, and the second one could not be imported at all** (fixed 2026-09-01; the rest of
this section is the diagnosis it came from). The picker was single-select, and
`SubcircuitCellBuilder.Write` refused outright when a planned cell folder already existed:

> A cell named '…' already exists here, and '…' needs it because it calls that subcircuit. Importing
> a subcircuit never writes over a cell that is already in the workspace.

That refusal is correct in isolation — never overwriting is the right default — but it makes a
shared core a dead end. Measured across the four files:

| File | top-level parts | cells per import | cells shared between parts |
|---|---|---|---|
| A | 1 | 2 | — |
| B | 2 | 5 each | **4** |
| C | 5 | 30, 2, 2, 1, 1 | 1 |
| D | 2 | 2 each | **1** |

So in two of the four files, importing one variant of a part **permanently blocks the other**: they
share a core, and the second import trips the existing-folder refusal on that core rather than on
anything the user chose.

Three changes, in dependency order — **all three are built**:

1. **A multi-select picker.** One gesture, one shared dependency plan, each shared core written
   once. This is the change that matches what someone holding a library file actually wants — both
   variants of a part — and it makes the collision impossible rather than recoverable.
2. **Reuse an identical existing cell instead of refusing**, for the come-back-tomorrow case. Only
   on *proven* content identity: if the cell that would be written differs from the one on disk,
   keep today's refusal and say it may have been edited since. Never overwrite.
3. **Record provenance.** A written cell currently records **nothing** about where it came from —
   no source file, no definition name, no content hash — so (2) can only be a content diff. Storing
   the file, the definition and a hash turns reuse into a lookup.

**Decided (2026-09-01): the layout stays FLAT, and the reason is not clutter.** Importing file C's
top-level part writes 30 cell folders into the parent directory, and a per-import subfolder was
weighed against three facts:

- **It breaks every placement already made.** A placed cell's `CellRef` is stored relative to the
  schematic that places it, so moving imported cells one level down re-points nothing and breaks
  every `.csch` in every workspace that already places one. That is a silent, retroactive cost paid
  by users who asked for nothing.
- **It buys less than it looks.** Inside the subfolder the cells are still siblings, so the
  `"../../" + name` convention between them is unchanged — the *only* thing that moves is where the
  top-level cell sits relative to the workspace root, which is precisely the part everything else
  references.
- **The count is not the layout's doing.** Thirty dependencies are thirty cells under any layout: a
  circuitRF cell instance references a cell folder. What multi-select removes is the *collision*
  (§4.1's real defect), not the count, and the collision was the part that made a file's second part
  unimportable.

It stays reversible — nothing in the builder assumes flatness beyond that one reference string — and
would be worth revisiting alongside a migration for existing placements, never as a side effect.

**Provenance is what the layout question was really about.** A written cell now records
`ImportedFrom` in its `.ccell` (`CcellImportProvenance`: source file NAME, definition name, and a
SHA-256 over the schematic, symbol and declared interface). That is what turns "is this the cell I
already wrote?" from a content diff into a lookup, and it is what lets an existing folder be REUSED
without the never-overwrite rule bending: reuse requires the recorded hash to match both what a fresh
import would write *and* what is on disk right now, so an edited cell is a refusal that says it was
edited rather than the generic already-exists sentence. The file's NAME is recorded, not its path — a
`.ccell` travels into archives and onto other machines, where the sender's absolute path means
nothing and should not have gone.

### 4.2 `.func` and global `.param` are read and then dropped

`SubcircuitTranslator.TranslateAll` reads `result.Library.Cells`, `result.ModelCards` and
`result.IncompleteCells` — and nothing else. It never touches `result.Functions` or
`result.Variables`, and `SubcircuitCellBuilder` mentions neither. The placed-component path is the
same: `SpiceModelNetlist` copies `sub.Definition.Variables` (which is the *cell's* own list) and no
functions at all.

**So an imported subcircuit silently loses every `.func` its file declared** — 8, 102, 0 and 9
across the four files, in files whose devices are built out of them. The reader does its job:
`SpiceNetlistResult.Functions` holds them, correctly parsed. Two consumers then discard them.

There is a merge path for exactly this — `NetlistImports.MergeInto`, which folds an imported
netlist's functions and globals into the design's `TestBench` — but it is fed from *kit* netlists
(`.net`/`.inc`/`.ckt`) and never from the SPICE reader. Note also its policy: a duplicate **variable**
name reports a conflict; a duplicate **function** name is skipped with no report at all.

This is where §4.1's question meets §9.1's. Scoping by cell solves one half and cannot solve the
other:

| Declaration | Scope on import | Collides? |
|---|---|---|
| `.param` **inside** a `.subckt` | the Cell's own `ParameterDeclaration`s | **No** — this is exactly what one-cell-per-subcircuit buys, and it already works. Two subcircuits declaring `Rd` differently are two cells. |
| `.param` **outside** any `.subckt` | nowhere — `Cell.Variables` is not a global scope | Would collide in `TestBench.GlobalVariables` |
| `.func` | **nowhere** — `Cell` has no function list; `UserFunction` exists only on `TestBench` | Would collide, first-wins, silently |

A cell cannot hold a function, so no amount of per-cell scoping fixes `.func`. **Inlining (§9.1)
is what fixes it**: substitute the body at the call site and the name never enters a namespace, so
the collision is structurally impossible rather than policed. Global `.param` still needs the
`MergeInto` path, with the variable-style conflict report rather than the function-style silence.

---

## 5. Place — the `SpiceModel` component

A placed `SpiceModel` carries four parameters: `File`, `Name` (which definition inside it),
`PinConfig` and `Pitch`. Its **symbol is generated** from the definition — a diode card draws a
diode, a five-port subcircuit draws a five-pin box — so `CellSymbolResolver` resolves a
`spicemodel://` reference the same three ways it resolves a cell folder (Resolved / NotFound /
PrimaryMissing).

`SpiceModelPeek` caches the parsed file by mtime and length. Three paths ask the same question
constantly — the symbol resolver on every schematic model rebuild, the parameter dialog on every
selection change, the extractor once per simulate — and re-parsing a large library file on each of
those is a re-read of the same bytes for the same answer.

At extraction, `SpiceModelNetlist.Build` puts the translated cells into the extraction's own
`Library` **under the file's own names**, because a subcircuit's calls name their targets and
renaming the targets would mean rewriting every call inside every definition. A name the design
already uses is never replaced; the collision is reported.

**Path resolution.** `SpiceModelSymbolProvider.ResolvePath(file, schematicDir)` delegates to
`SnpPathPolicy.Resolve(file, workspaceRoot, schematicDir)` — the same policy an SnP component's
`File` parameter uses. A stored reference is therefore **workspace-relative** by convention, with
the schematic folder as a fallback.

---

## 6. Archiving a workspace that references a SPICE file

`Archive Workspace…` has to answer one question for every reference a design holds: *would this
arrive broken at the recipient?* `WorkspaceArchiveScanner` answers it in three passes — the
workspace's own files (always included), the `results/` folder (itemised options), and then
**everything referenced from inside the workspace but living outside it**.

`DocumentFileRefs` finds those references by walking each design document as JSON and asking, of
every string and every object **key**, *does this resolve to a file that exists?* — tried against
three bases (`RefBase.Document`, `RefBase.Workspace`, `RefBase.Results`), because circuitRF has
three conventions and a reference cannot be repointed without knowing which one it belongs to. A
property named `File` is read workspace-first, which is exactly the `SnpPathPolicy` convention a
`SpiceModel` uses.

So **a `SpiceModel` pointing at a library file outside the workspace is already found**, already
appears in the dialog as an *External file* row, is **ticked by default** (unlike a kit, which is
off by default: this is one small file, and without it the design the recipient opens is missing a
piece of itself), lands at `external/<name>` inside the archive, and the `.csch`'s `File` parameter
is repointed at the archived copy by `WorkspaceArchiveWriter`.

**That is exactly one file.** §12 is about why one file is not always enough.

---

## 7. Where the scope line is

circuitRF is an RF circuit simulator — DC, S-parameters, harmonic balance, loadpull. **It is not a
SPICE simulator and there is no transient analysis** (`docs/PRD.md`). Reading a deck written in the
SPICE dialect is an *interoperability* feature: it lets a device someone already has become a
circuitRF cell.

This matters for library files specifically, and it should be said once, plainly, rather than
discovered: many of them describe **hard-switching power devices**, whose whole purpose is a
turn-off transient. circuitRF will elaborate such a subcircuit, solve its DC operating point, and
give an S-parameter linearisation about that point — all genuinely useful, and all that circuitRF
offers. It will not reproduce a switching waveform, and a harmonic-balance run driving one into
hard switching is not what harmonic balance is for.

Supporting these files is still worth doing: the same grammar carries small-signal and RF device
subcircuits, thermal networks, and passive macromodels, and today **every one of them is refused
for reasons that have nothing to do with what analysis will eventually run.** §8 measures that.

### 7.1 What a harmonic-balance run on one of these would actually be

Measured on the four files, since "will it run in HB" is the first question anyone asks:

**The internal time constants are RF-relevant, which was not obvious.** These macromodels are
analog computers — a charge or a delay is built from `R`, `C` and behavioural sources rather than
stated as a device parameter — so it is worth knowing what rates they actually encode:

| Mechanism | τ |
|---|---|
| a reverse-recovery `L`/`R` loop (0.87 H over 333 MΩ — both "component values" are scaling constants) | **2.6 ns** |
| terminal capacitances, 1.3–4.5 nF | RF |
| charge integrators (`1 µF` against `1 mΩ`), a differentiator (`0.1 µF` against `1 mΩ`) | 1 ns, 0.1 ns |
| a stored-charge node: `1 µF` against the driving source's own `1u/τ` term, so the unit cancels | ≈ carrier lifetime, µs |
| a thermal `Rth·Cth` ladder | 0.18 µs → 9.8 ms |

So HB on one of these is meaningful **biased in the active region under a modest RF drive** — real
nonlinear behaviour, real nonlinear C-V. What HB cannot do is the thing the models were written for:
a truncated harmonic spectrum cannot represent a hard-switched edge, and the same files carry `if()`
kinks in their channel and junction equations. Newton is fine while the swing stays on one side of a
kink and stalls when it crosses one every cycle — which is the same limit stated twice.

Two practical consequences worth recording before anyone is surprised by them:

- **An `if()` anywhere in a device's equations disables the SDD grid evaluator** (a device is asked
  for all its equations at once), so these devices run the scalar path. Slower, not wrong.
- **Thermal caps up to 30 mF sit in the same matrix as 500 MΩ resistors** — roughly fifteen decades
  of dynamic range at RF. Survivable with LU and `gmin`, worth watching.

#### 7.1.1 The undriven thermal pin, and why the `limit` fix makes it worse

Three of the parts expose a `Tcase` pin. In each, a behavioural source injects dissipated power into
`Tj`, an `Rth` ladder runs `Tj → … → Tcase`, and every `Cth` goes to ground — so **at DC the
capacitors are open and the pin is the thermal chain's only exit.** Leaving it unconnected makes the
thermal block singular. The files say so themselves; it is a wiring requirement, not a defect.

**But it does not present as a failure once §9.4's `limit` fix lands.** `gmin` gives `Tj` a ~1e-12 S
path to ground, `V(Tj)` runs to ~1e12, and the model's own `limit(V(Tj), -200, 300)` clamps it — so
the solve **converges, at the clamp limit, silently**. Before the fix (with `limit` stripped to its
nominal) the same circuit diverges visibly instead. That is a rare shape: a correctness fix that
converts a loud failure into a quiet wrong answer, and it needs its own guard.

circuitRF can detect the shape structurally — a node whose only DC path to ground is `gmin`, feeding
a clamp — and that is the right place to catch it, not in the SPICE reader. The **one** part across
the four files that avoids the problem entirely drives its thermal node from `TEMP` instead of
exposing a pin (§8.9).

---

## 8. Library files — what is actually missing

### 8.0 What §9 actually delivered, measured on the same four files

**33 of 45, from 1.** `brief-spice-behavioural-sources.md` was built and measured against the same
four files on 2026-09-01. The counts below replace the "importable" column of the table in §8;
everything else in §8 is the measurement that led to the work and is left as written.

| File | `.subckt` | before | after | reader notes before → after |
|---|---|---|---|---|
| A — power switch, 2 parts | 2 | 0 | **2** | 14 → 0 |
| B — switch + diode library, 6 parts | 6 | 0 | **6** | 56 → 4 |
| C — gate driver, 34 parts | 34 | 1 | **22** | 171 → 9 |
| D — power switch, 2 parts + 1 core | 3 | 0 | **3** | 17 → 0 |

**22 of the 33 reach a DC operating point**, measured in a scratch harness that flattens each
definition and grounds every port through 1 MΩ. Two logic-macromodel blocks do not converge (§13's
open question 2, answered); the rest of the shortfall is the harness's own — flattening loses the
per-cell parameter scoping a real import keeps.

**The 12 that remain refused trace to exactly three causes, all deliberate:** a `TABLE(…)` transfer
(7 subcircuits), two voltage-controlled switches, and one source that reads `V(TIME)` as a delay
ramp. Each is refused by name.

**Two causes worth more than anything §9 anticipated were not in §8 at all**, because §8's own
measurement stopped at "the element is of a kind this reader does not read" and never reached the
expressions inside those elements:

- **This dialect writes the logical connectives with ONE character** — `V(a)>0.5 & V(b)<0.5`.
  circuitRF spells them `&&`/`||`, the parser stopped at the character, and the element was refused.
  **28 of file C's 34 subcircuits**, every logic block in it. A pure spelling change: circuitRF has
  no bitwise operators, so neither character can mean anything else.
- **The dialect is case-insensitive and circuitRF is not.** `IF(…)`, `MAX(…)`, `TANH(…)` parse as
  calls to unknown functions and fail at SIMULATE time. Re-spelled only where a bracket follows the
  name and only for circuitRF's own function set, so a net called `MAX` is untouched. `arctan` and
  its two siblings joined `sgn` in the alias table.

**§8.10's rule needed correcting, and in the direction that converges.** `time` inside a condition is
NOT simply "take the then-branch": file C writes `if((V(r)>0.5) | TIME < 1NS, 0, …)`, where the time
comparison is **false** in steady state. Taking the then-branch sticks that output at 0 forever — a
different circuit that solves. What is read is the COMPARISON (`>`/`>=` true, `<`/`<=` false, `==`
false, `!=` true), leaving the rest of the condition to decide.

**And one finding that has nothing to do with this dialect at all:** `Evaluator.EvalLogic` combined
its two operands with AND after short-circuiting, so **`false || true` evaluated to FALSE** —
everywhere circuitRF evaluates an expression. See `src/Core/RESOLVED.md`.

---

**The extension is not the obstacle.** Four real library files were run through
`SpiceNetlistReader` and `SpiceCellImport` on 2026-09-01. All four parsed. All four produced cells.
Of **45 subcircuits across the four files, exactly 1 could be imported** (plus 8 `.model` cards).
Nothing in the failures had anything to do with the `.lib` extension, and none of the four files
even uses a `.lib` *section*.

| File | `.subckt` | `.func` | Reader notes | Importable subcircuits |
|---|---|---|---|---|
| A — power switch, 2 parts | 2 | 8 | 14 | **0** |
| B — switch + diode library, 6 parts | 6 | 100 | 56 | **0** |
| C — gate driver, 34 parts | 34 | 0 | 171 | **1** |
| D — power switch, 2 parts + 1 core | 3 | 9 | 17 | **0** |

The notes group into eight causes, in order of how many lines each accounts for:

### 8.1 Behavioural and controlled sources — `E`, `G` (189 lines of 241)

`E` and `G` are not in the `Elements` table at all, so every one of them is
`"element 'X' is of a kind this reader does not read"` → the cell is incomplete → the whole
subcircuit is refused. **This is the whole ballgame**: 234 lines across the four files.

Both spellings occur — `G… VALUE = {expr}` and `G… VALUE {expr}` — and the expressions reference
node voltages (`V(a)`, `V(a,b)`) and branch currents (`I(Vname)`), which is what makes them
interesting rather than merely unsupported.

**Every one is the `VALUE` form.** Not one positional-gain (`E a b c d 2.5`) or `POLY(n)` line
appears in any of the four files — worth knowing, because the positional forms are the easy ones
and there is no credit to be had from them.

| Form | count |
|---|---|
| nonlinear `E`/`H` (behavioural **voltage**) | **123** |
| affine `E`/`H` — an ideal VCVS/CCVS | 53 |
| nonlinear `G`/`F` (behavioural **current**) | 51 |
| …of which call a `.func` | 35 |
| `TABLE` (piecewise-linear transfer) | 7 |

**The behavioural VOLTAGE source outnumbers the current source better than two to one**, which is
the opposite of what the SDD-shaped intuition suggests and is why §9.0 comes out the way it does.

File C's 103 `if(…)` sources are a logic-style macromodel. Three sources across files B and C
reference `time` — see §8.10.

### 8.2 Independent sources — `V`, `I` (14 lines)

Almost all of them are the **zero-volt current sensor** idiom: `V_sense d d5 0`, present only so
that some other element can write `I(V_sense)`. circuitRF already has exactly this component —
`IProbe` — and an ordinary DC source, `Vdc`.

### 8.3 `PARAMS:` read as the model name (5 subcircuits)

`X1 d1 g s Tj SOME_SUB PARAMS: a={act} …` — the reader takes the model name from the **end** of the
bare-word run, and `PARAMS:` is a bare word. So the instance is recorded as calling a subcircuit
named `PARAMS:`, and the refusal reads *"'X1' names the model 'PARAMS:', which this file does not
define and does not include."*

`ReadSubcktHeader` already skips `params:` when reading the *definition* line. `ReadElement` does
not, on the *call* line. This is a one-line asymmetry that refuses five otherwise-clean subcircuits
across two of the four files, and it is the cheapest fix in this document.

### 8.4 `limit(…)` reduced to its nominal (65 occurrences)

`SpiceExpression.Statistical` contains `limit`, so `limit(x, lo, hi)` is treated as a distribution
and rewritten to `x` — the clamp is **removed**.

In one dialect `limit(nominal, spread)` really is a distribution. In another, `LIMIT(x, min, max)`
is an ordinary three-argument clamp, and these files use it that way everywhere: `limit(dVth,0,1)`
guards a normalised knob, `limit(V(Tj),-200,300)` guards a temperature before it reaches a power
law, and file B wraps *every one of its hundred* `.func` bodies in `LIMIT(…, -1e12, 1e12)` as an
overflow guard.

The two readings are distinguishable by **arity**: two arguments is the distribution, three is the
clamp. Today all 65 are read as the distribution, all 65 are reported in
`SpiceNetlistResult.Statistics` (so the run is at least *labelled* a nominal-corner run), and every
clamp is silently gone.

### 8.5 A nested `{…}` inside an expression (2 lines)

`.FUNC TAU_X(T) {LIMIT((TX1*((T+t0)/300)**{ETX1}),-1e12,1e12)}` — the inner `{ETX1}` is a parameter
reference written in braces *inside* a larger expression. `SpiceExpression.Unwrap` only strips a
matched pair spanning the whole value, so the inner braces survive into the parser:
*"Parse error at position 19: Unexpected character '{'"*. In this dialect braces around an
interpolated parameter carry no meaning inside an expression; they group nothing.

### 8.6 A capacitor's transient initial condition (1 line)

`CQB b 0 1u,IC = 0` — the comma-attached `IC=` is tokenised into the value word, so the value is
lost and the line is refused as *"has no value and names no model"*. `IC` is a transient initial
condition, which circuitRF has no analysis for; the right outcome is to **recognise it, note it, and
keep the capacitance**, not to lose the component.

### 8.7 Switches — `S`, `W` (2 lines)

Voltage- and current-controlled switches with a `VSWITCH`-family `.model`. Genuinely not something
circuitRF has, and genuinely a discontinuity that harmonic balance does not want. These should stay
refused — **by name**, with a sentence that says circuitRF has no switch device, rather than by the
generic *"a kind this reader does not read"*.

### 8.8 Nonlinear charge is written as a behavioural source driving a capacitor

**This is the single most important structural finding in this document, and it is why charge is a
design item rather than an open question.** Not one of the four files contains a voltage-dependent
capacitor, a `Q={…}` element, a `ddt()` call, or a `.model` capacitor with voltage coefficients.
Every capacitor in all four files is constant-valued. **Every nonlinear capacitance is expressed
instead as a behavioural voltage source driving a linear capacitor** — the charge-source idiom:

```
E_Edg4  d    ox4  VALUE {-(V(g,d) - Q02(V(g,d))/Cdg4)}
C_Cdg4  ox4  g    {Cdg4}
```

Work it through: the `E` constraint gives `V(ox4) − V(g) = −Q02(V(g,d)) / Cdg4`, so the charge
stored on `C_Cdg4` is `Cdg4 · (V(ox4) − V(g)) = −Q02(V(g,d))`. **The capacitor value cancels
exactly** — it is a scaling constant, and the device's real content is the charge function `Q02`,
which one file declares as a `.func` under that name.

There are **8 such pairs across the four files**, six of them in a single subcircuit (the four
gate–drain and drain–source charge branches plus two more). A file's entire reactive nonlinearity
lives in this idiom.

Three consequences:

1. **Charge is not an add-on to the behavioural-source work; it IS the behavioural-source work.**
   Six of one subcircuit's six `E` sources exist only to carry a charge. Building `E` without
   getting the charge right builds nothing.
2. **The idiom is exact in HB with no new mechanism** — provided the `E` branch row is evaluated in
   the time domain and transformed like any other nonlinear contribution. The capacitor is linear,
   so HB stamps `jkωC` per harmonic, and `jkω` applied to the harmonics of `−Q02(v(t))` is exactly
   `−dQ02/dt`. §9.5 states the conditions and §9.5.1 the cheaper equivalent.
3. **It is DC-solvable**, which is not obvious. At DC the capacitor is open, so the interior node
   (`ox4`) is reached only by the `E` branch. KCL there forces the branch current to zero, and the
   branch row fixes `V(ox4)`: two equations, two unknowns, non-singular.

### 8.9 `TEMP` — the simulator's own temperature variable

One file drives its thermal node from the global ambient rather than from a pin:

```
E1  Tj  w  VALUE={TEMP}
R1  w   0  1u
```

`TEMP` is a reserved name, not a `.param`, and circuitRF has the value already — `ambientC`, threaded
through `ComponentModelFactory.TryCreate` and defaulting to `Temperature.NominalC`. Unbound it is
simply an unknown identifier, and the enclosing subcircuit fails to resolve.

This matters more than one line suggests: that subcircuit is a **3-pin part with no thermal pin at
all**, which makes it the only device across the four files that needs no thermal termination from
the user (§7.1).

### 8.10 `time` in a condition

`E_OUT out 0 VALUE = {if(time > 0, -V(sig), 0)}` — three occurrences across two files, all of them
inside a differentiator or start-up-suppression block. `time` is transient-only and circuitRF has no
transient analysis.

The defensible reading is that in DC, S-parameter and HB analysis the circuit is in **steady state**,
so `time > 0` is true and the expression is its then-branch. That is a decision, not a free
translation, and it must be a named note. It is also worth far more than it looks: it takes the
importable count from 22 of 45 to **37 of 45**, and the top-level parts from 7 of 10 to 9 of 10,
because one such line sits inside a subcircuit that two otherwise-clean parts depend on.

A `time` reference anywhere **other** than a condition (a ramp, `V=time*k`) has no steady-state
reading and stays refused by name.

### 8.11 What is a `.lib` *section*, then?

The format feature named by the extension is the section: `.LIB <name>` … `.ENDL`, offering a set of
alternatives inside one file, selected from elsewhere with `.LIB <file> <section>`. **The reader
already implements both halves fully** — `Session.Run` tracks section framing above conditionals,
collects the names into `SpiceNetlistResult.Sections` grouped by file (structural, because a kit
states one axis per file), and skips every section when none was requested, because sections are
alternatives and choosing one nobody asked for is a guess.

There is exactly one consumer: `PdkCorners`, which discovers a kit's corner axes and binds a chosen
corner by asking the reader for `.lib <file> <section>` — the format's own mechanism, so the
section's conditionals and nested includes are handled by the one reader that already handles them.

`ReadFile` and `Read` used to hard-code `section: null`, so there was no way for the import gesture or
a placed `SpiceModel` to ask for one and no UI anywhere surfaced `Sections`. **Both now take an
optional `section`** (§10), and blank is treated as *no section* rather than as a section named "" —
the value arrives from a stored parameter and from a combo box, both of which spell "unset" as an
empty string.

---

## 9. Design — behavioural sources onto the expression engine

The answer to *"can we reuse our expression engine?"* is **yes for the arithmetic, and the SDD is
the device** — for behavioural *current* sources. Behavioural *voltage* sources are a different
problem and, as §9.0 measures, they are the one that actually matters.

### 9.0 The measurement that decides the order of work

The obvious plan is a ladder: reader hygiene first, then the cheap element forms, then the SDD, and
leave the behavioural voltage source for later because it is the expensive one. **That plan
delivers nothing.** Classifying all 45 subcircuits by which capabilities each one needs, and
propagating a refusal up through every `X` call that reaches it (which is what
`SubcircuitTranslator.ResolveDependencies` does), gives:

| Capability set | Subcircuits importable | Top-level parts |
|---|---|---|
| today, as shipped | **1** of 45 | 1 of 10 |
| \+ reader hygiene, incl. `TEMP` (§9.4) | 1 | 1 |
| \+ `V`/`I` sources and affine `E`/`G` | 1 | 1 |
| \+ nonlinear `G` via the SDD, `.func` inlined | 1 | 1 |
| \+ **nonlinear `E` / the charge idiom** (§9.2, §9.5) | **22** | **7** |
| \+ `time` read as steady state (§8.10) | **37** | **9** |
| \+ `TABLE` and switches (not planned) | 45 | 10 |

Every part that matters depends, transitively, on a subcircuit containing a behavioural **voltage**
source with a nonlinear expression — and, per §8.8, most of those exist to carry a nonlinear
*charge*. There is no useful subset that avoids it, and building the ladder bottom-up would ship
three milestones that change the count from 1 to 1.

That does not make the earlier work optional — every item is a prerequisite, and the reader hygiene
in particular is what turns a misleading refusal (*"names the model 'PARAMS:'"*, affecting 25
subcircuits) into a truthful one. It does mean the branch-unknown work is the **load-bearing**
milestone and has to be sized first, not last.

Note the two cheapest rows in that table are not in the ladder at all: `TEMP` (one identifier) and
`time`-as-steady-state (one condition rule) are together worth 15 subcircuits and 2 top-level parts,
but only once the branch row exists.

### 9.1 `G … VALUE={f}` is an SDD port equation, almost exactly

The equation-defined device (`sdd.md`) evaluates user-authored expressions in dual arithmetic and
returns `i`, `q`, `dg`, `dc`. Its current equation `I[p,0]` is written in terms of port voltages
`_v1.._vn` and **control currents** `_c1.._cm`, where a control current is a reference to another
device's branch unknown (`ControlRefs` → `ControlBranchIndices`), honoured across DC, HB and
S-parameters.

A behavioural current source maps onto that with no new engine concept:

| In the file | In the SDD |
|---|---|
| the `G`'s own two nodes | port 1 |
| `V(a,b)` on nodes the `G` does not touch | an extra **sense port** across `a`,`b` with `I[p,0] = 0` |
| `V(a)` | a sense port from `a` to ground |
| `I(Vx)` | a control-current reference `_cn` to `Vx`'s branch |
| the expression | `I[1,0]`, rewritten by `SpiceExpression` |

`sdd.md` §"current of these device classes can be sensed" already lists the independent voltage
source and the inductor as sensable, which is exactly what `I(Vx)` names in these files.

Two things are needed that do not exist:

- **`.func` must be callable from an SDD equation** (§3.1). The three SDD compile paths throw on any
  name they do not know. The natural fix is **inlining at compile time**: `CompiledSddExpr` is built
  once per model, so substituting a `UserFunction`'s AST for its call site with the arguments bound
  costs nothing per evaluation and needs no change to the register program or the grid path.
  Inlining also **sidesteps the flat `TestBench.Functions` namespace entirely** (§3.1), which is
  what would otherwise make two imported library files collide on a name like `ni`.
- **Inlining needs a size cap.** File B's functions are mutually nested (`vfb` calls `EG` and `ni`;
  `ni` calls `EG`; `LA` calls `DA` and `TAU_X`), and file A's `Jh` reaches `Ue` through two levels
  with `Ue` appearing three times in `Ue1`. Naive inlining is exponential in nesting depth. Cap the
  expanded node count, refuse **by name and by number** when a call site exceeds it, and measure the
  real files rather than assuming either outcome.

### 9.2 `E … VALUE={f}` needs a nonlinear branch row

An `E` is a **voltage** source. circuitRF's nonlinear interface is current-based by construction:
`ComponentModel.Evaluate(in PortVoltages, in ControlCurrents) → (i, q, dg, dc, …)`, and
`NonlinearDcEngine.BuildResidualAndJacobian` adds `res.I[p]` to **node** rows and `res.Dg[p,q]` to
**node × node** Jacobian entries. A device that constrains `V(a) − V(b) − f(…) = 0` needs a *branch*
row in the Newton system — a Group-2 element in a path that today only accepts Group-1
contributions. `sdd.md` records `F[…]` implicit equations as **explicitly out of scope**, which is
the same gap seen from the SDD's side.

There is no honest way to convert a general behavioural voltage source into a current source: a
Norton equivalent needs a finite source impedance the file did not write, and inventing one changes
the circuit. A large-conductance penalty formulation (`I = G_big·(V(a,b) − f)`) was considered and
is **not** the design — it is a different circuit, it is silently a different circuit, and its
conditioning depends on what the source happens to drive.

**The seam is smaller than it looks, and it is already half-built.** A branch is allocated during
the *linear* stamp pass, which runs for every component including nonlinear ones, so a model can
call `mna.AddBranch()` + `AddBranchCurrent()` + `AddConstraint(br, a, +1)` / `(br, b, −1)` in its
`Stamp` and get the ±1 KCL coupling and the `V(a) − V(b)` half of the constraint row into the
engine's **constant** `_gAug` for free. What is missing is only the per-iteration part:

- a residual contribution at the branch row, `f[br] −= g(v, i)`;
- Jacobian entries `−∂g/∂v_k` at (branch row, node col);
- and, for a current-controlled source, `−∂g/∂i_n` at (branch row, other-branch col) — which is
  exactly the shape `res.DControl` already has, transposed into a branch row instead of a node row.

So the change is an **optional extra result block on `NonlinearResult`**, mirroring `DControl`, plus
one stamping loop in each of the three engines (`NonlinearDcEngine`, the HB extractor,
`StampLinearized` for the S-parameter linearisation). `AddConstraint`, `AddBranchConstraint`,
`AddNodeBranchCoupling` and `AddSourceValue` all already exist on `IMnaContext`; no new MNA
primitive is needed.

`E` then splits three ways:

1. **Affine `E`** — `E out ref in+ in− <gain>`, or a `VALUE` expression that is a constant-coefficient
   combination of `V()`/`I()` terms. This is an ideal VCVS/CCVS: a **linear** element with a branch,
   stamped by an ordinary `Stamp`, exactly as `VdcModel` does. circuitRF has `VCCS` but no `VCVS`.
   52 of the 189 lines (§8.1). Cheap, but on its own it unlocks nothing (§9.0).
2. **Nonlinear `E`** — the branch-row path above. **This is the milestone the count moves on.**
3. **`TABLE`, `LAPLACE`, `FREQ`** forms — refused by name. (`POLY(n)` does not occur in any of the
   three files; if implemented at all it expands into an ordinary expression and costs nothing.)

### 9.3 Element mapping, in full

| Line | Form | Becomes |
|---|---|---|
| `Vx a b 0` | zero-volt sensor | `IProbe` |
| `Vx a b <dc>` / `DC <dc>` | DC source | `Vdc` |
| `Vx a b … AC …` / `PULSE`/`SIN`/`PWL` | stimulus | `Vdc` at its DC value, **noted**: circuitRF drives the design from its own TestBench |
| `Ix a b <dc>` | DC current source | current-source primitive (an SDD with a constant equation until one exists) |
| `Gx a b c d <gm>` | VCCS | `VCCS` (exists) |
| `Gx a b VALUE={f}` | behavioural current | **SDD** (§9.1) |
| `Ex a b c d <k>` | VCVS | new linear `VCVS` (§9.2.1) |
| `Ex a b VALUE={f}` | behavioural voltage | affine → `VCVS`; otherwise the nonlinear branch row (§9.2.2) |
| `Fx` | current-controlled current | SDD with a control-current reference |
| `Hx` | current-controlled voltage | `VCVS`/branch row with a control-current reference |
| `Ex`/`Gx` referencing `time` | transient stimulus | refused **by name**: circuitRF has no transient analysis (§7) |
| `Sx`, `Wx` | switch | refused **by name** (§8.7) |
| `Tx`, `Ox`, `Ux` | line, opamp macro, digital | refused by name |

### 9.4 Reader hygiene

- `PARAMS:` skipped on an `X` line, as it already is on a `.subckt` line (§8.3).
- `limit` classified **by arity**: 3 arguments is a clamp and rewrites to
  `min(max(x, lo), hi)`; 2 arguments stays a distribution and stays reported (§8.4).
- `sgn` → `sign`.
- Inner `{…}` inside an expression stripped as grouping (§8.5).
- `,IC=…` on a passive: value kept, `IC` noted and dropped (§8.6).
- **`TEMP` bound to circuitRF's ambient** (§8.9) — `ambientC`, which
  `ComponentModelFactory.TryCreate` already threads through. One reserved identifier; without it the
  only device in the four files that needs no thermal termination fails to resolve.
- **`time` in a condition read as steady state** (§8.10) — `if(time > 0, a, b)` becomes `a`, with a
  note. Worth 15 subcircuits and 2 top-level parts once the branch row exists. A `time` reference
  outside a condition has no steady-state reading and stays refused by name.
- A `.model` card declared **inside** a `.subckt` is hoisted into the one global card list today.
  That is what the dialect's own scoping does *not* do, and two subcircuits that each declare a
  local `D1` will collide — the reader already notes the redefinition, so the collision is visible,
  but the note should say it was a *local* card.

---

### 9.5 Charge

Nonlinear charge is **not optional and not deferred**. §8.8 measures why: 8 charge pairs across the
four files, six in one subcircuit, and no other nonlinear-reactive form appears at all. A behavioural
`E` built without the charge idiom working is a behavioural `E` that imports nothing.

circuitRF already owns every piece needed. Nothing here is a new engine concept:

| Piece | Where it already is |
|---|---|
| `Q` alongside `I` in a nonlinear result, with `Dc = ∂Q/∂v` | `NonlinearResult`, `ComponentModel.Evaluate` |
| the SDD's charge equation `I[p,1]` | `SddModel._chargeAst`, honoured in HB |
| `Q(V) = ∫₀ᵛ C(u) du` for a polynomial `C(V)` | `NonlinearCModel` — already integrates rather than using `Q = C·V` |
| `jkω` applied to the charge harmonics | the HB formulation itself |

#### 9.5.1 The general path — the idiom as written

Translate `E` to a nonlinear branch row (§9.2) and leave the capacitor as an ordinary linear
capacitor. This is **exact**, needs no pattern recognition, and works for every shape of the idiom
including the ones nobody anticipated. Its cost is one extra node and one extra branch unknown per
charge, per harmonic — 6 charges in one subcircuit is 6 extra branch rows × N harmonics.

Two conditions, both of which are assertions to test rather than things to hope for:

- The branch residual must be evaluated **in the time domain and transformed**, like any other
  nonlinear contribution. A branch row linearised once about the DC point would give the
  small-signal capacitance and silently lose every harmonic of the charge.
- The `E` branch row carries **no charge term of its own**. `BranchResidual` is algebraic in `v`
  and `i`; the charge lives entirely in the capacitor. Adding a `Q` counterpart to the branch block
  would double-count.

#### 9.5.2 The collapsed path — recognise the idiom, emit one device

When the pattern matches exactly — an `E` from `n+` to an interior node `mid`, a linear capacitor
from `mid` to `n−` with a constant value `K`, and **nothing else connected to `mid`** — the pair is
algebraically identical to a single one-port whose charge is `Q(v) = K · f(v)`, which is precisely
the SDD's `I[p,1]` bucket. Emit that instead and the extra node, the extra branch row and the
constraint all disappear; the device also becomes eligible for the SDD grid evaluator.

**"Nothing else connected to `mid`" is the whole of the correctness condition** and it must be
checked on the elaborated netlist, not on the text — a third element on that node makes the
collapse a different circuit.

Both paths must exist, the general one must be the default, and **a test must assert that the two
give identical results on a fixture where both apply**. The collapse is an optimisation; if the two
ever disagree, the collapse is wrong.

#### 9.5.3 Direct charge spellings

None of the four files uses these, but they are the ordinary way other suppliers write the same
physics, and circuitRF accepts any file the user points at. All three are translation-only:

| Written | Becomes | Note |
|---|---|---|
| `Q={expr}` on a capacitor | SDD `I[p,1] = expr` | exact, direct |
| `Gx a b VALUE={ddt(expr)}` | SDD `I[p,1] = expr` | `ddt` is the charge marker, not a function to evaluate |
| `C={poly in v}`, or `.model` `CAP` with `VC1`/`VC2` | `NonlinearCModel` | polynomial only |

**The semantic trap, stated once because getting it wrong is silent:** `C = f(v)` declares the
small-signal *capacitance*, so the charge is `Q = ∫₀ᵛ f(u) du` — **not** `Q = f(v)·v`. The two agree
only for constant `f`. `NonlinearCModel` already integrates correctly for a polynomial. For a
general non-polynomial `C={expr}` the integral is not available symbolically, so that form is
**refused by name** — "circuitRF can integrate a polynomial capacitance to a charge; write this one
as `Q={…}`" — rather than approximated. A wrong charge law converges and produces plausible numbers,
which is exactly the failure that must not be shipped.

#### 9.5.4 The oracle

Charge is the one part of this work whose errors are invisible in a converged answer, so it needs a
gate that is not "it ran". Three, in increasing strength:

1. **Small-signal**: linearise at bias `v₀`; the port susceptance must equal `jω·dQ/dv|v₀`, compared
   against the analytic derivative of the charge function — not against another circuitRF path.
2. **Charge conservation**: over one HB period, `∮ i dt = 0` to solver tolerance for every charge
   branch. A resistive contamination of a charge term breaks this and nothing else catches it.
3. **Equivalence**: the general path (§9.5.1) and the collapsed path (§9.5.2) agree entry-by-entry
   on a fixture where both apply; and a `Q={…}` spelling agrees with the equivalent `E`+`C` pair
   written out longhand.

## 10. Design — selecting a `.lib` section from the import and place gestures

The reader half existed (§8.11). What was missing was a way to ask, and a place to show the answer.
**Built 2026-09-01; every bullet below is now the implementation.**

- `SpiceNetlistReader.ReadFile(path, section)` and `Read(text, …, section)` — the parameter is
  already threaded all the way through `Session.Run`; only the public entry points hard-code null.
- `SpiceCellImport.Scan` returns `SpiceNetlistResult.Sections` alongside its candidates, so a file
  that declares alternatives can be scanned once to *learn what they are* (the whole-file pass
  collects section names precisely because it skips their contents) and re-scanned for the chosen
  one.
- The import picker gains a **Section** combo above the definition list, shown only when the file
  declares any. The `SpiceModel` parameter panel gains the same, as a fifth parameter
  (`Section`) beside `File` and `Name`.
- **A section is part of the reference.** `SpiceModelSymbolProvider.RefFor` must include it in the
  `spicemodel://` reference, or the symbol cache returns the wrong definition when the section
  changes and nothing else does.
- The default is **no section**, which is today's behaviour exactly, and a file that declares
  sections but has none chosen keeps saying so in its notes rather than picking one.

**Two things the implementation added that the design did not say.** A file offering sections and
asked for none reads *nothing*, so "this file holds no `.model` cards and no `.subckt` definitions" is
a true sentence about the read and a misleading one about the file — both the placed component's
status line and the picker's intro name the alternatives instead. And the same case must reach the
IMPORT picker rather than the holds-nothing refusal, because the picker is where the section is
chosen; `CreateCellFromModelCardFromPathAsync` branches on `SectionNames.Count > 0` for exactly that.

---

## 11. Design — one file is rarely one file

`SpiceNetlistResult.FilesRead` already records **every** file that contributed to a read, including
those pulled in by `.include` and `.lib`. Nothing outside the reader consumes it. That is the fact
the next section turns into archive behaviour, and it is worth stating on its own: circuitRF already
knows the transitive file set of any SPICE reference it has read — it simply never asks.

---

## 12. Design — archiving the referenced model files

§6 is correct as far as it goes: an external `SpiceModel` file is found, offered, ticked by default,
copied to `external/<name>` and repointed. Three things break the moment the referenced file is a
library file that is not self-contained.

**Built 2026-09-01.** What follows is the diagnosis; the state now is at the end of the section
(§12.5).

### 12.1 The include closure is invisible

`DocumentFileRefs` walks a design document **as JSON**. A `.lib`'s own `.include`/`.lib` lines are
inside a text file it never opens, so a library file that pulls in a shared model file contributes
exactly one row to the dialog. The recipient gets the entry point and none of its contents.

**And the failure is quiet.** The reader reports the missing file as a note, marks the enclosing
cell incomplete, and the subcircuit is refused *at simulate time*, in a different session, on a
different machine, with a message about a file the recipient has never heard of.

**Fix:** when an external reference is a SPICE file, read it (through the same `SpiceModelPeek`
cache) and expand the row into its `FilesRead` set.

### 12.2 A flat `external/` folder breaks relative includes

Even with every file ticked, `external/` is flat: `UniqueName` renames collisions to `foo-2.lib`.
A deck whose entry point says `.include ../shared/models.lib` cannot resolve that after the copy,
because the directory structure it was written against is gone.

**Fix:** a SPICE reference is archived as a **group with its own subtree**, rooted at the deepest
common ancestor of its `FilesRead` set — `external/spice/<group>/…` preserving relative structure —
so every `.include` inside it resolves exactly as it did. One tick, one row, one self-consistent
copy. Repointing then rewrites only the entry point, and the includes need no rewriting at all,
which is the property that makes this robust rather than clever.

### 12.3 The row should say what it is

Today an external row is titled by file name with the absolute path as the tooltip. A SPICE group
should say how many files travel with it and what pulled it in — a recipient's *"why is there a
folder of model files in here?"* is answered on the row, not by opening it. A group whose closure is
a single file stays a single row and behaves exactly as it does today.

### 12.4 What is deliberately not changed

- **Ticked by default stays.** This is the design's own reasoning from §6, and a model file is small.
- **A kit stays off by default.** A library file reached *through a kit* is part of that kit's row
  and must not be duplicated into `external/` — the closure walk must skip any file that already
  lives inside an included kit, exactly as `AddExternalFiles` already skips anything inside the
  workspace.
- **No "copy into the workspace" gesture is added here.** Importing to a cell folder is already that
  gesture (§1), and it is a different decision — made once, at import time — from *"send this design
  to a colleague"*.

### 12.5 What the archive actually does now

`WorkspaceArchiveScanner.AddExternalFiles` reads every external reference whose extension is one of
`SpiceModelPeek.FileExtensions` (the same list the SpiceModel picker offers, so the archive and the
component cannot disagree about what a deck is) through the same `SpiceModelPeek` mtime cache the
editor and the extractor use, and takes `SpiceNetlistResult.FilesRead` as its closure. From that:

- **A closure of one file is left exactly as it was** — one flat row at `external/<name>`. That is the
  common case and it already worked; a subtree row for it would be a folder containing one file.
- **A closure of several becomes ONE row with its own subtree**, rooted at the deepest common ancestor
  and preserving relative offsets, at `external/spice/<group>/…`. Repointing rewrites only the entry
  point; the `.include` lines inside the deck are untouched and resolve after the copy exactly as they
  did before. That is the property that makes it robust rather than clever, and the gate test proves
  it by extracting the archive and re-reading the deck from inside it — not by counting copied files,
  which would pass with the structure flattened.
- **Only the closure travels, not the folder it is rooted at.** `ArchiveOption.Members` names the exact
  files and their offsets; a directory row with no members still copies its whole folder, which is
  what a kit is. Without this, carrying three files out of a model directory would archive the
  directory.
- **Two decks sharing a directory are ONE subtree**, merged when their roots are equal or nested. Two
  overlapping subtree rows would copy the shared model file twice and leave the recipient with two
  divergent copies of one file.
- **A closure member inside the workspace or inside a referenced kit is skipped** — the same rule
  `AddExternalFiles` already applied to the workspace, and §12.4's rule for kits.
- **Ticked by default, unchanged**, and the row says how many files travel and which document pulled
  them in.

One measured guard rail: a file is not read as a deck above 32 MB. The extension list has to include
the spellings suppliers actually use (`.txt` among them), and a Known File that happens to be a large
log must not be parsed line by line to discover that it includes nothing.

---

## 13. Open questions

1. ~~**What does `.func` inlining cost on real functions?**~~ **Answered, and it is a non-issue.**
   File B's 102 mutually-nested definitions all inline, and all six of its subcircuits import. The
   cap (`UserFunctionInliner.DefaultNodeLimit`, 200,000 expanded nodes) was never approached. What
   the exercise DID change is *where* inlining happens: at translation, not at model construction —
   an imported subcircuit becomes a cell folder on disk and there is nowhere in one for a function
   definition to live (`src/Core/RESOLVED.md`).
2. ~~**Does a nonlinear branch row converge on these circuits?**~~ **Answered: 22 of the 33 that
   import reach a DC operating point** (§8.0). Two logic-macromodel blocks do not converge, which is
   exactly the shape this question anticipated — a device built from ~100 `if()` sources is
   discontinuous by construction.
3. **`.model` inside `.subckt`** — is per-subcircuit card scoping worth implementing, or is the
   redefinition note sufficient? Two files here declare local cards; neither collides today.
4. **A sectioned file** was not among the four measured — none of them uses `.LIB`/`.ENDL` at all.
   §10's design came from the reader's implementation and from `PdkCorners`, not from a file in hand,
   and it is now built and gated on a synthetic two-section fixture. **Still open in the same way:**
   no real supplier file has exercised it here, so the one thing unverified is whether a real kit's
   section names and nesting look like the fixture's.
5. ~~**Is the collapsed charge path (§9.5.2) worth building at all?**~~ **Answered, and the
   dependency ran the other way: it is BUILT, and it is what completes M4.** It is not an
   optimisation that saves a node and a branch row — **it is the only formulation harmonic balance
   can carry.** HB's unknowns are the voltage phasors at the nonlinear-facing nodes, so a nonlinear
   branch unknown is neither one of them nor reducible into the linear network; carrying one means
   bordering the Newton system with an extra unknown per constraint per harmonic, through the
   extractor, the Jacobian, the back-solver, the warm start and both the 2-D and N-D variants. That
   was not built, and `HbEngine` refuses such a circuit by name.

   The COLLAPSED device states a charge, and HB has applied `jkω` to charge harmonics since it was
   written. So `SpiceChargePairCollapse` performs the rewrite at import, unconditionally, whenever
   the pattern holds exactly: an `E` from `n+` to `mid`, a linear `K` from `mid` to `n−`, nothing
   else on `mid`, and the expression not sensing the pair it constrains. The question asked whether
   an HB measurement would justify the collapse; that measurement cannot be taken while the general
   path refuses HB, which is what made the collapse the prerequisite rather than the reward.

   **All three charge oracles of §9.5.4 now pass** (`SddBranchEquationTests`): the small-signal
   susceptance against the analytic `dQ/dv` at three biases (T7), charge conservation over one HB
   period (M4b2, measured at 9e-11 of the fundamental, with a DC-offset drive so the question is not
   vacuous), and second-harmonic content against the analytic Fourier ratio `a·A / 2(C0 + a·V₀)`
   (M4b3, 0.3572 against 0.35714) — the gate that fails if the charge were linearised about DC
   instead of transformed. The collapsed and general paths are held together entry-by-entry at three
   frequencies (M4b1).

   **Still refused by HB:** a voltage constraint that is not a charge — a logic or behavioural block
   written as one.

6. **Does an undriven thermal pin need a structural guard?** §7.1.1: the `limit` fix turns a visible
   divergence into a converged answer at the clamp limit. The detection is a general engine
   question — "a node whose only DC path is `gmin`, feeding a clamp" — not a SPICE-reader one, and
   it may belong in its own brief.

# `src/Core` — the design layer, elaboration layer, and expression engine

Standing instructions for `src/Core` (design layer, elaboration layer, the expression engine, and
the `ComponentModel` types). Read with the root `CLAUDE.md`. Design notes: `docs/design/data-model.md`,
`docs/design/expressions.md`. `src/Engine` has its own `CLAUDE.md` for the numeric layer.

> ### Where the history went
> This file was an append-only phase log that reached **2,156 lines**. The full text is preserved
> verbatim at **`src/Core/HISTORY.md`** (line numbers unchanged) — every dated brief write-up,
> measured number and test-pass count. Grep it (`grep -n "2026-08-08" src/Core/HISTORY.md`) rather
> than reading it whole.
>
> **Maintenance rule, or this regrows.** A completed phase's narrative belongs in `HISTORY.md`, not
> here. This file only *changes* — a new invariant, a moved default, a new refusal, a trap that now
> has a name. If a phase adds nothing true of the code tomorrow, it adds nothing here.

---

## What lives here
- **Design layer:** `Library`, `Cell`, `Instance`, `TestBench`, `ParameterDeclaration`,
  `ParameterAssignment`, `Variable`, `Analysis` subtypes, `Measurement`. Editable, serializable
  (`.cnl` + JSON), human-readable.
- **Elaboration layer:** flatten hierarchy → `ElaboratedNetlist` (`ElaboratedComponent` list +
  `NodeMap`), resolving parameters/variables and numbering nodes.
- **Expression engine:** tokenizer → Pratt parser → AST → evaluator. Serves variables, cell
  parameters, the SDD, and measurements.
- **`UnitNormalizer`** (`Expressions/UnitNormalizer.cs`): `ToEngineUnit(editorUnit)` maps editor
  glyph unit strings (Ω, µ) to ASCII engine spellings (Ohm, u). Called at the extraction boundary
  only — do not scatter; editor glyphs and the `Units` table are both unchanged.
- **`ComponentModel`** base + `Devices/` (the numeric behaviors; their stamping/evaluation contract
  is detailed in `data-model.md` §5 and exercised by the engine).
- **`Netlist/`**: the native `.cnl` reader/writer plus format readers for vendor dialects
  (`Netlist/Spice/` — a generic SPICE-family reader; `Pdk/` — kit/PDK discovery and binding).

## Key type distinctions — do not blur
- **`Cell` vs `TestBench`.** A `Cell` is a **reusable** definition (ports, parameter interface,
  contents) that gets instanced. A `TestBench` is the **one** thing you simulate (top cell +
  globals + analyses + measurements). **Analyses and measurements attach to the `TestBench`, never
  to a `Cell`.**
- **`ComponentModel`, not "Device".** The single base for passive **and** active parts. "Device" is
  reserved for its RF meaning (an active part). A resistor and a FET are both `ComponentModel`s.
- **Three layers flow one way:** design → elaboration → numeric. Nothing in `src/Core` may depend
  on `src/Engine` or `src/Ui`. The GUI edits the design layer; it never hands a design-layer object
  to the engine — always elaborate first.
- **A model card (SPICE-family reader) is NOT given a design-layer type.** It is the parameter block
  of whatever device implements its type — built-in, compiled-model-behind-a-provider, or nothing
  yet — carried on the result and bound by whoever supplies that device.

## Expression engine — non-negotiables
- **Never string substitution.** Real tokenize → Pratt-parse → AST → evaluate.
- **Parse once, evaluate many.** Cache the AST on the owning `Variable`/parameter/SDD/`Measurement`;
  the SDD hot path (per time sample × Newton step × sweep point) must allocate no garbage.
- **`SddModel`'s hot path does not call `SddEvaluator` directly.** It compiles each equation once, in
  its own constructor, to `CompiledSddExpr` (a slot-resolved evaluator; a flat register program for
  the (overwhelmingly common) no-conditional case, `SddCompiler`'s node-tree walk as the fallback for
  one that has an `if`/ternary). `SddEvaluator.EvalDual`/`EvalDouble` are unchanged and remain the
  reference implementation every caller outside `SddModel` still uses directly, and the bit-identical
  gate (`SddCompiledBitIdenticalTests`) diffs against. See `RESOLVED.md`'s evaluator entry for the
  measured payoff and why the compiled path does not cover conditionals.
- **Kinded values: Real / Complex / Bool.** A resolved variable or parameter is Real **or** Complex
  (not forced complex) — most component values are Real, impedances Complex. Ordering comparisons
  are **real-only**; SDD equations are real-only time-domain (no `j`).
- **Cycle detection is mandatory** and spans variables, cell-parameter defaults, and instance
  overrides. Report the offending chain (e.g. `a → b → a`); never recurse without the in-progress
  guard.
- **Scope is structural,** not string-keyed: globals at the base; each cell instance pushes a scope
  binding parameters. **Override expression evaluates in the PARENT scope; default in the cell's own
  scope.** Resolution is local-then-global; no upward/sideways reach.
- **`null` means "no unit"; `""` is an error** — `Evaluator.ApplyUnit` returns early on `null` only;
  an empty string falls through to `Units.Scale` and throws `Unknown unit ''`. A caller injecting a
  possibly-unitless value must guard explicitly (`IsNullOrEmpty(baseUnit) ? null : baseUnit`), never
  pass `""` meaning "none".
- **Var-unit-wins**: when an expression references a variable that itself declares a unit in scope,
  the site unit is skipped (the variable's unit was already applied once in `Resolve`). Literals and
  unit-less variable refs still take the site unit.

## Elaboration — string-param devices (do not `Eval` their non-numeric params)
`Elaborator.ResolveParameters` dispatches to a per-device resolver for primitives whose overrides
must NOT be expression-evaluated: **SDD**, **Z_Port**, **V_1Tone/V_nTone**, **P1Tone**, **SnP**.
Each stores its string-valued params as `new Value(raw)` (verbatim), and only evaluates genuinely
numeric overrides via `_evaluator.Eval()`.

**Why:** a file path (`File=/Users/…/x.s2p`) is not an expression — the leading `/` crashes the
expression parser at position 0. Similarly `InterpMode=Cubic` is a string enum, not a numeric
expression.

**Rule:** when adding a new primitive device that has string-valued params (file paths, enum names,
equation strings), add a `ResolveXxxParameters` dispatcher in `Elaborator.cs` that stores those
params raw. The generic `ResolveParameters` fallback evaluates ALL overrides — never let a string
param fall through to it.

## Elaboration — general rules
- Flatten depth-first; primitives emitted, cells recursed with a fresh scope.
- Resolve every expression to a kinded value (units applied here), with cycle detection.
- Number nodes: ports map onto parent nets; internal nets uniquified by instance-path prefix;
  **ground = 0**. Node names carry the instance path (`X1.drain`) — this is what measurement paths
  resolve against, so keep it stable.
- Compute `NonlinearComponents` / `NonlinearNodes` (the HB partition seed) here.
- Numbering is stable + unique; the fill-reducing permutation for the solve is the **engine's** job,
  not the elaborator's.
- A component needing extra unknowns (series-R internal node, a port pair, a thermal node) mints
  them via `Nodes.GetOrAssign` with a `__`-prefixed name (`__extdev_{path}_n{k}`,
  `__tuner_{inst}_bias`, …) — the shared mechanism every such device uses. Internal nodes are real,
  global-matrix unknowns and are deliberately **not** locally eliminated (Schur reduction is wrong
  for HB, where an internal node's voltage carries its own harmonic content).
- **A device multiplier `m`** lives at the `ElaboratedComponent` level (`Stamp`/`StampLinearized`/
  `Evaluate` all go through it) — **never call `ec.Model.Stamp(...)` directly**, it bypasses the
  multiplier and silently simulates one device where the netlist asked for several. `m ≤ 0` is
  refused, not obeyed (some dialects read `m=0` as "not there"; silently deleting a placed component
  is worse). Anything that would allocate a **branch-current unknown** under a multiplier is refused
  outright (two ideal voltage sources in parallel is not a circuit).

## `.cnl` reader + writer
- Vendor-neutral hierarchical netlist; maps directly onto the design layer. JSON carries the same
  logical model. `CnlReader` parses `.cnl` text to `TestBench`; `CnlWriter` is its exact inverse —
  round-trips back to an equivalent `TestBench`.
- **Standing check when adding anything to `TestBench` or `Cell`: can `CnlWriter` say it?** A field
  the writer cannot express is silently absent from every run (`RunAnalysis → WriteNetlist →
  netlist.cnl → SchematicRunService.RunNetlist → CnlReader`), and the resulting error names a
  position in a generated file that no longer contains the thing that was actually wrong. When a
  kit-backed run fails, check the round trip before the extractor.
- **SnP / frequency-domain N-port reference node (N-or-N+1 rule).** A frequency-domain N-port
  component (SnP block, impedance block, TLIN, user freq model) lists either **N nets** (each port
  referenced to ground) or **N+1 nets** (last net is the common floating reference for all ports).
  **`Z_Port` is the deliberate exception** — it always uses **2N** nets as differential ± pairs with
  *per-port* references, parallel to the SDD convention, not shared.
- The VendorA importer translates legacy `if…then…else…endif` → canonical `if(cond,then,else)` at
  import time, so the engine grammar stays single-form; native `.cnl` only ever uses the canonical
  form.
- Analysis/measurement directives are typed `Analysis` subclasses with dedicated `.cnl` parsers
  (DC, S-param, HB, parametric sweep, loadpull, loadpull-pursuit); an `enabled=false` token on any
  of them is read by a shared `ParseEnabledToken` helper. `AnalysisChain` (pure, framework-free)
  resolves the effective top/inner analysis through a chain of possibly-disabled sweeps: a disabled
  sweep collapses (its inner runs in its place); a disabled base makes the whole chain inert.

## How to add a component type
Derive from `ComponentModel`, declare ports + params, implement `Stamp(...)` (linear) and/or
`Evaluate(...)` (nonlinear: `i`, `q`, `dg`, `dc`), register in `ComponentModelFactory`. See the root
`CLAUDE.md`'s own "How to add a component type" for the full contract. Two `src/Core`-specific
points:
- If any parameter is string-valued (file path, enum name, equation text), add a
  `ResolveXxxParameters` dispatcher (see above) — do not let it fall through the generic evaluator.
- If the device needs extra internal nodes, mint them through `Nodes.GetOrAssign` with a `__`-prefixed
  name, and keep them as real unknowns rather than eliminating them locally.

---

## Load-bearing pitfalls, one line each — do not lose these

Grouped by area; each is a distinct silent-wrong-answer trap found in production, with the date it
was fixed. Full derivation and gate tests for any of these are in `HISTORY.md` — grep the quoted
phrase or the date.

### Units, numbers, and the tokenizer
- **A bare word after a `key=value` param is that value's UNIT, never a net** (2026-07-31,
  2026-08-08). An unrecognized unit token (`TOhm` was missing from the table) silently became a
  phantom net name, and the circuit still "solved" — wrongly. Any bare-word-after-value construct in
  this codebase must resolve through `Units.IsRecognizedUnit`, never a raw net-name fallback.
- **Identity/measurement units** (`V`, `A`, `W`, `dBm`, `dB`, …) are a *separate* allow-list
  (`Units.IsRecognizedUnit` = `IsKnown || _identityUnits.Contains`) from the linear-scale `_scales`
  table — they were absent from `_scales` and leaked into the net list as phantom nodes, shifting
  every later node index (2026-06-16). The consume check fires **only inside the `key=value`
  branch**, never in the leading net section, so a real net literally named `V` stays safe.
- **`m` (metre-adjacent lowercase) stays MILLI, deliberately** — every netlist dialect agrees, and a
  hand-authored `C=1m` silently becoming 1 F is a wrong answer that parses, stamps and converges. The
  metre's own base symbol is `"metre"` (2026-08-07); `nm`/`cm` used to be **identity units** (scale
  1.0, not 1e-9/1e-2) and were silently 1e9× / 100× wrong. `mil` (2.54e-5) and `in`/`inch` (2.54e-2)
  are now real base-unit-mapped lengths too.
- **`Scale(BaseUnit(u)) == 1.0`** is the property `ParametricSweepEngine`'s re-injection depends on —
  verify it for any *new* unit before trusting a sweep over that axis; length never actually
  satisfied it before 2026-08-07.
- **A cell-parameter declaration (`parameters W=5 nm`) and a top-level variable assignment
  (`W = 5 nm`) are separate code paths from an instance-line param** — both silently dropped or
  mis-parsed the unit even after the instance-line path was fixed (2026-08-07). Whenever a unit
  token bug is fixed in one of these three parse sites, check the other two.
- **A power-of-ten prefix must be applied by RE-READING the literal with the exponent appended, not
  by multiplying** — `3.0 * 1e-9` is not the nearest double to `3e-9`, and the drift shows up as
  noise on read-back (SPICE reader, 2026-08-03).
- **After an explicit exponent, no prefix is applied** — `1e-12F` is one picofarad, not 1e-27
  (SPICE reader).
- **Lower-case `m` is the device multiplier; upper-case `M` is the diode's grading coefficient** —
  resolved parameters compare ordinally, so they are genuinely different keys on a component that
  can carry both (2026-08-02/03). The SPICE reader normalizes instance-line `M=4` → `m` (that
  dialect is case-insensitive); a `.model` CARD is left alone, where `M` means what circuitRF means.
- **Instance lines never stripped a trailing `;` comment** (only the variable-assignment splitter
  did) — every word of a comment silently joined the net list, invisible on a two-terminal device
  that only reads its first two nets (2026-07-31). Now stripped quote-aware.
- **An unquoted parameter value must contain no spaces** — the generic instance-line parser splits
  on whitespace and reads bare words as nets, so `R=if(a, 1, 2)` (spaces after commas) silently
  becomes a value plus two phantom nets, shifting every later node index. `R=if(a,1,2)` is safe;
  quoted values are safe (the tokenizer is quote-aware). Only the SDD line parser does depth-aware
  boundary detection.
- **A rewritten/round-tripped value must carry NO whitespace** for the same reason, when a reader
  rewrites a foreign dialect's construct (e.g. `if(a, b, c)`) into circuitRF's own text.

### PDK / kit reading — general rules
- **Recognized STRUCTURALLY, never by extension, namespace URI, or the tool named in the file.**
  Extensions and namespaces are shared across unrelated formats/tools; keying off them puts one
  supplier's habits into circuitRF and silently fails the next kit. Applies to every format reader
  in `Netlist/` and `Pdk/`.
- **A directive/section discriminator must be searched with a wide enough peek window.** A 4,096-byte
  `PeekChars` window missed a kit's corner declarations at byte 4,114/4,184 — raised to 64 KB;
  the separate `PeekLimitBytes` (512 KB, "is this file worth opening at all") already bounds cost, so
  widening the read-window inside an accepted file was free (2026-08-08).
- **Sections are ALTERNATIVES, never read all-at-once.** A corner/model-library file states the same
  parameters several times over for different process corners; reading it whole double-defines
  everything. `.lib <file> <section>` reads exactly one; an unrequested section is skipped **and
  named** rather than silently omitted, and a file that declares corners is discovered by scanning
  for the *opening* directive (one word after `.lib`) before it is ever parsed.
- **A corner/section is requested the way the dialect itself requests one** (`.lib <file> <section>`)
  — never by reaching into the file and reading its parameters directly. Two independent reads of
  the same section (its own bindings, plus whatever it `.include`s) double-bind names; verify they
  are mutually exclusive in practice, don't assume it.
- **Automatic exact-name binding of a kit part to "the subcircuit with the same name" was built,
  measured, and REVERTED** (2026-08-08) — it attached most of a palette to LVS
  regression-data netlists that merely share a name, and even a correct match would be unusable
  because its values live behind a process corner the kit deliberately leaves to the user. Do not
  resurrect a name-matching heuristic here without a corner-selection UI already in place.
- **"Incomplete" means the DEFINITION was damaged, not that a type is unfamiliar.** An unfamiliar
  device type is very often a device a provider supplies later — marking it broken reports the
  working case as broken. A conditional that cannot be evaluated takes **no branch** (never "false"),
  and the cell is marked incomplete rather than silently building a different circuit.
- **Statistical distributions are reduced to nominal value and REPORTED**, never silently — the
  reduced number is indistinguishable from one that never carried a distribution at all.
- **Inclusion is guarded by file IDENTITY, and the ROOT file is registered before it is read** — else
  the root is the one file that can be entered twice and a cycle back to it recurses to the depth
  limit instead of being reported as a cycle.
- **A directive's name is the leading dot plus letters, not the first whitespace-separated word** —
  `.if(x==1)` (glued) must not fall through to `.endif`'s switch arm.
- **A `.subckt`-local `.param` is a cell VARIABLE** (overridable per call site), not a fixed default —
  treating it as fixed silently seals shut every geometry parameter a kit relies on being overridden
  (2026-08-08).
- **A model-card mapping is a mapping of NAMES onto circuitRF's existing physics, never a second
  implementation of the arithmetic.** Two passes (parameter-declaration widening, card→model binding)
  must run AFTER the whole read completes — a card or subcircuit may be declared in a different file
  or later in the same one, and resolving at the element line makes the result depend on file read
  order (2026-08-08).
- **The dialect's case sensitivity and circuitRF's are different, and both directions need explicit
  handling.** `AlignSubcircuitParameterCase` only ever renames a *call's* names into its
  *definition's* spelling (the direction that cannot invent a parameter) — never the reverse.
- **A malformed record in a kit file costs ITSELF and nothing else.** Kits ship the occasional
  damaged line; bound a parse at the next RECORD boundary rather than letting quote-tracking or
  brace-tracking run on and swallow everything after it (found on a kit's malformed `template=`
  line, 2026-08-03).
- **A kit's own catalog and its own symbol library can spell the same part name differently**
  (`A_B` vs `A B`) — match separator-insensitively, or parts silently lose their pins (18 of 109 on
  a kit, 2026-08-01).
- **A kit's netlists are often ONE library split across files** — read every netlist beside the named
  one (imports), not just the named file; a definition's own file may have no process constants.
- **Kit data files are anchored where the netlist is read, not the workspace and not the import
  root** — a bounded look (two ancestors, one level of children each) around the netlist's own
  folder. Do not relax the bound on a miss; one level too far starts listing a home/temp directory.
- **A backslash in a quoted kit value is a directory separator, not an escape.**
- **An empty parameter value is genuinely dangerous, not merely absent**: `CnlWriter` emitting
  `Key=` verbatim and `CnlReader` gluing the *next* token onto a trailing `=` silently eats a real
  parameter's own value (OPEN/unfixed as of 2026-07-31 — see `HISTORY.md` for why every candidate fix
  was rejected as a `.cnl` contract change).

### External / compiled devices (OSDI, senior-worker, VerilogA)
- **A provider's current is positive INTO the device** — the passive sign convention, verified by
  test rather than assumed for every new provider added.
- **Batching is the point, not an optimization.** HB evaluates every device once per harmonic sample
  per Newton iteration; a per-call round trip to an out-of-process provider makes the transport the
  simulator (measured ~24× at batch 2000). Any new provider path must expose a batch entry point.
- **`__`-prefixed parameters are circuitRF plumbing and must NOT be forwarded** to an external
  provider — forwarding `__instanceLabel` alongside real parameters broke every device served by a
  strict provider (2026-07-31).
- **"Grounded" and "slaved" are separate claims.** A node reported `SlavedTo` follows another node's
  voltage; a node reported grounded (`UINT32_MAX` / `"to": -1` on the wire) is tied to the ground
  reference. Conflating them grounds a device's own first terminal. A node reported **both** is
  refused — nothing here can tell which is meant.
- **An EXTERNAL pin (one the user wired a net to) reported as grounded/collapsed is a REFUSAL, not a
  silent node-0 substitution or a silently-ignored report** — both readings are wrong and both are
  invisible on screen.
- **Node-collapse resolution needs a GROUP pass done in two passes, resolved BEFORE minting new
  unknowns** — one master may have several slaves and only one external terminal; assigning as each
  is encountered copies the wrong index before the terminal is reached, and minting a node that later
  gets absorbed leaves an orphan all-zero row/column (2026-08-03).
- **Simulator parameter arrays passed to a compiled model must be non-null and null-terminated** —
  four null pointers meaning "no simulator parameters" instead crashes *inside* the model the moment
  it scans for one (`$simparam`), presenting as the worker dying with no output (2026-08-03).
- **Temperature rides as its own reserved, `__`-prefixed key**, lifted out of the parameter
  dictionary at `create` — never passed as an ordinary model parameter (a model happening to declare
  that name would get the value twice, competing).
- **A missing external-model file/library is a REFUSAL from the resolver, not a null** — null means
  "this resolver has no opinion" and sends the caller looking for the wrong half of the system
  (a registration problem instead of a missing file).
- **A `File`/`Model` parameter is always taken verbatim, never tried as an expression first** — a
  path starting `/` crashes the expression parser at position 0.
- **A model-library override travels IN THE PROVIDER NAME** (`kit|path`), because the registry keys
  providers by name — two instances naming different libraries must get two providers or the second
  is silently evaluated by the first's model.
- **On macOS, a chosen library path must go through the VM's SHARE mechanism, never be handed to the
  worker as a bare host path** — the worker runs inside a VM and a host-side path does not resolve
  inside the guest; the older bug looked nothing like an override problem (`dlopen … No such file`
  naming a file that plainly exists on the host) (2026-07-31).
- **Any process worker (VM host included) must never outlive circuitRF.** Closing a pipe does not
  signal end-of-stream to a virtio console guest, so a leaked macOS VM holds its slot until killed —
  and the *next* run then fails to start its own VM with no diagnostic at all, because it is killed
  by the system before it can print anything. Any new long-lived worker needs both an
  `AppDomain.ProcessExit` teardown hook on the host side AND self-termination on the worker side
  (parent-process-exit watch + stdin-EOF), because no caller code runs on a crash or `kill -9`.
- **A fast-dying worker's stderr is easy to lose**: read it with the no-timeout `WaitForExit()`
  overload (the only one that waits for redirected readers to reach end of stream) before reporting
  "no output" — this reproduces only under full-suite load, not in isolation (5/12 vs 0/40 measured;
  see the root-level race-verification memory note).
- **Partial pipe reads must be looped** on both sides of any device-worker transport — a short read
  is normal on a pipe, and treating one as end-of-stream produces garbage only under load.
- **An implausible frame length is a protocol desync, not a large result** — never allocate on the
  strength of an unchecked length prefix.
- **stderr must be drained on its own thread** — an unread stderr pipe fills and the worker blocks
  forever inside a write, presenting as a hang with no error.
- **One request in flight at a time, locked** — concurrent writers interleave frames on one pipe.
- **An unknown parameter name is rejected at `Create`**; a genuinely blank value is omitted (keeps
  the model's own default) rather than rejected.
- **A failed evaluation point can mean three different things through one `status=0` on the wire**
  (model refused / SIGSEGV caught / result non-finite) — a message naming only one sends debugging in
  the wrong direction; attach the worker's own log, since a failed point never otherwise reaches the
  code path that captures worker output.

### Device models — recurring shapes
- **The junction temperature relations (`JunctionPotentialAt`, `DepletionCapacitanceScale`,
  `SaturationCurrentScale`, `BandgapAt`) are shared in `Temperature`, not duplicated per model.**
  `Eg ≤ 0` means "bandgap term not modelled" (returns zero) — without it there is no way to state a
  device whose `Is` does not move with temperature.
- **Ambient temperature must NEVER move `Tnom`.** `Tnom` is the model card's own extraction
  temperature; moving both together makes every ΔT-based relation collapse to the identity while the
  device still looks temperature-aware and every number stays finite — the single most dangerous
  silent failure mode in this area.
- **`Temp` (absolute) beats `Dtemp` (ambient+rise) beats ambient**, and stating both on one instance
  is *resolved and reported*, never silently discarded.
- **A resistor's `TC1`/`TC2` must be resolved at construction/elaboration, not read inside `Stamp`** —
  the run's ambient temperature is known at elaboration and nowhere else; `ParametricSweepEngine`
  re-elaborates every point, so resolving early loses nothing.
- **Each FET law is its own type with its own parameter set — never a shared parameter block.**
  Different laws reuse the same parameter *name* for different physics (`Beta` is a transconductance
  in the quadratic law, a bias-shift coefficient in the cubic one); a shared block silently mis-feeds
  whichever model the user did not mean.
- **FET temperature-coefficient forms are NOT all the same shape** — `Beta`/`Alpha` scale
  exponentially (`1.01^(tc·ΔT)`, coefficients in *percent*/degree), `Gamma` scales as a plain
  fraction (`1 + tc·ΔT`), `Vto` shifts additively. Confusing the first two costs ~4% at ΔT=100.
- **Below pinch-off, current AND both derivatives must be exactly zero** — a fudge conductance puts
  current where there is none (the DC engine's own `gmin` already keeps the node solvable).
- **Derivatives are analytic, never finite-differenced**, inside a Newton loop — cheaper and most
  accurate exactly where a finite difference is least accurate (near strong nonlinearity).
- **A cross-coupling capacitance (e.g. `Cgd`) appears on BOTH ports and in the off-diagonal `Dc`
  entries** in whatever two-port coordinate system the law is written in — dropping the cross term is
  the classic plausible-but-wrong Jacobian.
- **Diode `Bv = 0` means "breakdown not modelled", never "breaks down at 0 V".** `Nbv` defaults to
  the published value (1), not to the forward ideality factor `N` — nothing physical ties them.
- **Diode `Gmin` defaults to ZERO, unlike SPICE** — the DC engine already adds its own `gmin` to
  every voltage node; a device supplying its own doubles it exactly where it matters.
- **Both diode runaway regions (forward exponential, depletion-charge blow-up) are continued by their
  TANGENT, not clamped** — value *and* slope must stay continuous or Newton stalls in a way that
  looks like a bad circuit, not a numerical one.
- **A recombination current is a SECOND exponential with its own `Is`/ideality, never a correction
  folded into the main one** — folding it in fits one bias decade and misses the rest.
- **A device's internal series-resistance node (diode `Rs`, VerilogA/OSDI internal nodes) is a
  genuine unknown, not locally eliminated** — solving it inside `Evaluate` is exact at DC and wrong
  in HB, where the internal node carries its own harmonic content.
- **A junction potential driven past its own domain falls back to the card's own value BEFORE use** —
  a relation outside its valid range says nothing about the device; don't let it extrapolate.
- **A capacitor's process coefficients (`SemiC`: `Cfixed + Cj·area + Cjsw·perimeter`, temperature
  polynomial) are LINEAR — a bias-dependent capacitance is a junction and belongs on the diode's own
  charge formulation, never a second, worse copy under a capacitor's name.**

### `Chain`, `Z_Port`, `SDD`, and other primitives
- **`Chain` (ABCD two-port) exists because a pure series element has no impedance matrix** (`Z11 =
  A/C` is infinite when `C=0`, which every frequency-domain line model degenerates to at DC) — use
  `Chain`, not `Z_Port`, for anything that can present as a bare series/shunt element.
- **`Z_Port` is 2N nets, per-port ± references — the one exception to the shared N-or-(N+1)
  reference-node rule** every other frequency-domain N-port follows.
- **SDD equations may contain whitespace** — the SDD line parser does bracket-depth-zero boundary
  detection instead of the general whitespace tokenizer; a general parser change elsewhere does not
  automatically cover SDD lines.
- **`H[0]=1` and `H[1]=jω` are built into the SDD weighting scheme and not user-redefinable**; `w≥2`
  weights need an explicit `H[w]=expr` and are evaluated with `freq` bound at evaluation time, not at
  parse time.
- **A frequency-dependent value crossing a cell boundary must be inlined AST-level, and the two
  inlining rules must not be conflated**: at a cell boundary every non-`freq` name is folded to a
  literal (the parent's names are not visible in the child scope); at a device, only names that are
  *themselves* frequency-dependent are inlined. Conflating them (applying the cell-boundary rule at a
  device) silently breaks any "inject these scope vars by name" contract downstream (found on
  `Z_Port`'s `InjectZPortScopeVars`, not by review).
- **Frequency dependence must terminate at a model that actually binds `freq`** (`Z_Port`, `Chain`,
  SDD's `H[w]`) — reaching any other device raises a named exception rather than an opaque
  "unresolved name 'freq'".

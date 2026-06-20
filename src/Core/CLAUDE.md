# Core — local conventions

SDD arbitrary weighting `I[p,w]` + `H[w]=expr` (brief-sdd-weighting-parser, 2026-06-19) — COMPLETE: SDD now accepts arbitrary weighting `I[p,w]` for `w≥2` with user `H[w]=expr` (Complex, in `freq`). `H[0]=1` and `H[1]=jω` are built-in and not redefinable. `I[p,w]` uses the real dual-AD evaluator (`SddEvaluator`); `H[w]` uses the Complex general `Evaluator` with `freq=ω/2π` bound at evaluation time. Parser chain: **CnlReader** `SddAssignmentHeader` regex extended to match `H\[\d+\]`; **Elaborator** `RxSddEquation` extended with `H` so `H[w]` params are stored raw and scope vars injected; **ComponentModelFactory** drops the v1 hard-error for `w≥2`, parses `H[w]` entries via `RxWeightFn`, and cross-validates that every referenced `w` has a matching `H[w]`. **SddModel** gains `_higherAst[][]` (per-port `w≥2` bucket lists) and `_weightAst` (w→H[w] AST), emits one `WeightedTerm` per distinct `w` from `Evaluate`, and overrides `Weight(w,ω)` to evaluate `H[w]` via the Complex evaluator. Ctor gains two optional params (existing tests unaffected). 10 gate tests: 8 in `SddWeightingParserTests.cs` (Core.Tests/Devices) + 2 in `SddWeightingParserE2eTests.cs` (Engine.Tests/HarmonicBalance). Build 0W/0E.

`NonlinearCModel` + `PolynomialFit` (brief-nonlinearc-model, 2026-06-19) — COMPLETE: Added `src/Core/Devices/NonlinearCModel.cs` (1-D polynomial nonlinear capacitor; `PortCount=1`, `Kind=Nonlinear`; `CapAt` Horner, `ChargeAt` Horner-integrated, `Stamp` no-op, `Evaluate` returns `Dc[[C(Vd)]]` and `Q[ChargeAt(Vd)]`). Added `src/Core/Expressions/PolynomialFit.cs` (normal-equation least-squares, Gauss partial-pivot, lowest-power-first output). Wired `"NonlinearC"` into `ComponentModelFactory._parameterizedTypes` and `TryCreate(typeName, params)` with `CreateNonlinearCModel` (reads `C0,C1,…` consecutively). 8 gate tests in `NonlinearCModelTests.cs`. Build 0W/0E, 1899 total tests.

`ComponentModel.StampLinearized` (brief-nonlinear-engine-seam, 2026-06-19) — COMPLETE: Added `using System.Numerics` and a new `public virtual void StampLinearized(IMnaContext mna, ElaboratedComponent c, double omega, in PortVoltages bias)` to `ComponentModel` base class. Default implementation calls `Evaluate(bias)` and stamps `Y[p,q] = Dg[p,q] + jω·Dc[p,q]` as an N-port admittance block via `AddBlockAdmittance`, using the same port→node-pair convention as `NonlinearDcEngine`. Linear engines call this for `Kind==Nonlinear` devices; HB/DC never call it.

Schematic housecleaning (brief-schematic-housecleaning, 2026-06-19) — COMPLETE (Core items): **Item 2 (P1Tone S-param lint):** `Elaborator.LintTopLevelTerms` now includes `P1ToneModel` in the top-level port-family filter alongside `PortModel` and `TermModel`. A netlist with `P1Tone Num=1` + `Term Num=2` no longer emits a "port 1 missing" warning. Warning text still says "Terms are numbered…" but the diagnostic now spans the full S-param port family. **Item 5 (ohm/ohms units):** `Units._scales` (case-sensitive ordinal map) now includes `{ "ohm", 1.0 }` and `{ "ohms", 1.0 }` so `Z=50 ohms` no longer tokenizes `ohms` as a phantom net. `IsKnown("ohm")` / `IsKnown("ohms")` return true; `Scale("ohm")` / `Scale("ohms")` return 1.0. `Ohm`/`Ohms` (Title-case) are unchanged. 9 gate tests: 5 in `OhmLowercaseTests.cs` + 4 in `P1ToneLintTests.cs`. Build 0W/0E.

Standing instructions for `src/Core` (the design layer, the elaboration layer, the expression
engine, and the `ComponentModel` types). Read with the root `CLAUDE.md`. Design notes:
`docs/design/data-model.md`, `docs/design/expressions.md`. `Data/` and (numeric `ComponentModel`
behavior) the engine have their own notes.

## What lives here
- **Design layer:** `Library`, `Cell`, `Instance`, `TestBench`, `ParameterDeclaration`,
  `ParameterAssignment`, `Variable`, `Analysis` subtypes, `Measurement`. Editable, serializable
  (`.cnl` + JSON), human-readable.
- **Elaboration layer:** flatten hierarchy → `ElaboratedNetlist` (`ElaboratedComponent` list +
  `NodeMap`), resolving parameters/variables and numbering nodes.
- **Expression engine:** tokenizer → Pratt parser → AST → evaluator. Serves variables, cell
  parameters, the SDD, and measurements.
- **`UnitNormalizer`** (`Expressions/UnitNormalizer.cs`): `ToEngineUnit(editorUnit)` maps editor glyph
  unit strings (Ω, µ) to ASCII engine spellings (Ohm, u). Called at the extraction boundary only — do
  not scatter; editor glyphs and the `Units` table are both unchanged.
- **`ComponentModel`** base + `Devices/` (the numeric behaviors; their stamping/evaluation contract
  is detailed in `data-model.md` §5 and exercised by the engine).

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

## Expression engine — non-negotiables
- **Never string substitution.** Real tokenize → Pratt-parse → AST → evaluate. (This replaces the
  prototype's NSExpression/NSPredicate/regex path.)
- **Parse once, evaluate many.** Cache the AST on the owning `Variable`/parameter/SDD/`Measurement`;
  the SDD hot path (per time sample × Newton step × sweep point) must allocate no garbage.
- **Kinded values: Real / Complex / Bool.** A resolved variable or parameter is Real **or** Complex
  (not forced complex) — most component values are Real, impedances Complex. Ordering comparisons
  are **real-only**; SDD equations are real-only time-domain (no `j`).
- **Cycle detection is mandatory** and spans variables, cell-parameter defaults, and instance
  overrides. Report the offending chain (e.g. `a → b → a`); never recurse without the in-progress
  guard. Fixture `recursion.log` (a valid multi-hop chain → resolves to `2`) and a synthetic
  cyclic fixture (`a=b, b=a` → must be reported) are the Phase-1 tests.
- **Scope is structural,** not string-keyed: globals at the base; each cell instance pushes a scope
  binding parameters. **Override expression evaluates in the PARENT scope; default in the cell's
  own scope.** Resolution is local-then-global; no upward/sideways reach.

## Elaboration — string-param devices (do not Eval their non-numeric params)

`Elaborator.ResolveParameters` dispatches to a per-device resolver for primitives whose overrides
must NOT be expression-evaluated. Currently: **SDD**, **Z_Port**, **V_1Tone/V_nTone**, **P1Tone**,
and **SnP** (brief-snp-fixes, 2026-06-17). Each stores its string-valued params as `new Value(raw)`
(verbatim), and only evaluates genuinely numeric overrides via `_evaluator.Eval()`.

**Why:** a file path (`File=/Users/…/x.s2p`) is not an expression — the leading `/` crashes the
expression parser at position 0. Similarly, `InterpMode=Cubic` and `ExtrapMode=NearestEdge` are
string-valued enum names, not numeric expressions.

**Rule:** when adding a new primitive device that has string-valued params (file paths, enum names,
equation strings), add a `ResolveXxxParameters` dispatcher in `Elaborator.cs` that stores those
params raw. The generic `ResolveParameters` fallback evaluates ALL overrides — never let a string
param fall through to it.

## Elaboration
- Flatten depth-first; primitives emitted, cells recursed with a fresh scope.
- Resolve every expression to a kinded value (units applied here), with cycle detection.
- Number nodes: ports map onto parent nets; internal nets uniquified by instance-path prefix;
  **ground = 0**. Node names carry the instance path (`X1.drain`) — this is what measurement paths
  resolve against, so keep it stable.
- Compute `NonlinearComponents` / `NonlinearNodes` (the HB partition seed) here.
- Numbering is stable + unique; the fill-reducing permutation for the solve is the **engine's** job,
  not the elaborator's.

## `.cnl` reader + writer
- Vendor-neutral hierarchical netlist; maps directly onto the design layer. JSON carries the same
  logical model.
- `CnlReader` (existing) parses `.cnl` text to `TestBench`.
- `CnlWriter` (Phase 6e Step 2, `src/Core/Netlist/CnlWriter.cs`) is the exact inverse: emits a
  `TestBench` as `.cnl` text that `CnlReader` round-trips back to an equivalent `TestBench`.
  Handles: variables, standard instances (R/L/C/Port/SnP…), SDD (equation format), Z_Port (Z[i,j]=
  format + N-or-N+1 rule), Tuner (skips synthetic TunerName), typed analyses (HB/Loadpull/etc.),
  measurements, raw directives verbatim, and a top-level `labelednets <name> <name> …` directive
  recording which nets came from user-placed schematic labels (see below). Gate: 10 round-trip tests
  in `tests/Core.Tests/Netlist/CnlWriterTests.cs`, all green.
- **`labelednets` directive (brief-cnl-labelednets-provenance, 2026-06-16).** `CnlWriter` emits a
  top-level `labelednets n1 n2 …` line (sorted, stable) from `tb.LabeledNets` when any labeled nets
  exist; `CnlReader` parses it back into `tb.LabeledNets`. This is what lets the node-picker
  labeled-filter survive the schematic→`.cnl`→CnlReader run path. `HbLabeledNodesCubeTests` (T4/T6)
  previously only exercised the in-memory injection path and missed the `.cnl` round-trip gap; T7
  (`EndToEnd_SchematicCnl_EmitsLabeledNodesCube`) is the regression guard for the full round-trip.
- **Skip unknown header/comment lines** so real-world exports import cleanly; committed fixtures are
  clean `.cnl`.
- The **VendorA importer** (a separate front-end, Phase 2) translates legacy `if…then…else…endif`
  → canonical `if(cond,then,else)` at import time, so the engine grammar stays single-form. (Native
  `.cnl` only ever uses the canonical form.)
- Analysis/measurement **directive grammar is deliberately deferred** (data-model §10) — nail it
  down before implementing those lines; the circuit/cell/variable lines are settled.
- **SnP / frequency-domain N-port reference node (N-or-N+1 rule).** A frequency-domain N-port
  component (SnP block, impedance block, TLIN, user freq model) lists either **N nets** (each port
  referenced to ground, node 0) **or N+1 nets**, in which case the **last net is the common
  reference node** for all ports (the floating-block case). The reader validates node count against
  `NumPorts`: `== NumPorts` or `== NumPorts + 1`, else error. The reference node is recorded on the
  component (a `ReferenceNet`, ground when absent) and the model uses it in its own stamp
  (linear-engine §4.1). This rule does **not** apply to 2-terminal R/L/C. SnP line fields:
  `File` (relative paths resolved against the `.cnl` file's dir; absolute as-is), `Type`
  (v1: `"touchstone"` only — **hard-error on any other value**; extensible, future `"datacube"`),
  `InterpMode` (`"spline"` default | `"linear"`), `InterpDom`, `ExtrapMode` (`"clamp"` default |
  `"extrapolate"`); `Temp` and passivity/noise flags are parsed and ignored.

## Phase 1 deliverable — COMPLETE (2026-05-30)
Expression engine + elaboration + `.cnl` reader, validated by the cycle-detection fixtures.
**Phase 1 does not need RfCore** — it is built and tested standalone.

### Implementation notes (reality vs. design)
- `if(...)` keyword is handled in the **Parser** (produces `ConditionalExpr`), not in `Evaluator.EvalCall`.
- `Evaluator.InjectResolved(scopeDebugName, name, value)` lets the Elaborator inject
  pre-resolved override values into the memo cache without round-tripping through `ToString()`
  (avoids breakage on Complex values).
- Left-assoc operators use `rbp = lbp + 1` in the Pratt table; right-assoc (`^`) uses `rbp = lbp - 1`.
- Analysis/measure `.cnl` lines → `RawDirective { Kind; RawLine }` on `TestBench`. Typed `Analysis`
  subclasses (`SParameterAnalysis`, etc.) are defined but not populated by the Phase-1 reader.
- Top-level instances in a `.cnl` (outside any `define` block) live directly on `TestBench.Instances`
  (no synthetic wrapper cell).
- `ComponentModel.Stamp` uses `object` placeholders for `mna` and `c` — types resolved in Phase 2.

## Phase 2 Step 1 deliverable — COMPLETE (2026-05-31)
SnP/Touchstone block end-to-end: `.cnl` reader parses `SnP:` lines, elaboration creates `SnpModel`,
`SnpModel.Stamp` performs Z-expansion into a real `MnaSystem`. RfCore is now a dependency of Core.
Gate: 4 tests in `tests/Engine.Tests/Linear/SnpStampTests.cs` — all green.

### Implementation notes (reality vs. design)
- `ValueKind.String` was added to `Value` (storage-only; no operators, no coercions). This is the
  mechanism for SnP configuration params (`File`, `Type`, `InterpMode`, `ExtrapMode`). The tokenizer
  lexes `"..."` as `StringLiteral`; the parser produces `StringLiteralExpr`; the evaluator returns
  `Value.String(...)`. A String value is a type error in any arithmetic context.
- `Instance.RefNetBinding` (nullable string net name) carries the N+1 floating reference node for
  frequency-domain N-ports. `null` = ground. The elaborator resolves it to `ElaboratedComponent.ReferenceNode`.
- `IMnaContext` interface lives in `CircuitRF.Core` (not Engine) because `ComponentModel.Stamp` must
  be able to call it. `MnaSystem` in `CircuitRF.Engine` implements it.
- `ComponentModel.Stamp(IMnaContext mna, ElaboratedComponent c, double omega)` — `object` placeholders
  replaced with real types.
- The elaborator now resolves parameters **before** creating the model (order swapped). This allows
  `ComponentModelFactory.TryCreate(typeName, params)` to construct `SnpModel` with its file path
  and settings baked in.
- `CnlReader` stores `_sourceDirectory` (from `ReadFile`); relative `File=` paths are resolved to
  absolute at parse time and re-stored in the `ParameterAssignment.Expression` as a string literal.
- `TokeniseLine` in CnlReader now respects quoted regions so `key="value with spaces"` stays one
  token.

## Phase 3 deliverable — COMPLETE (2026-06-01)
AD engine + SDD device, validated by the hero GaN HEMT bias.

### Step 1: AD engine
New files in `src/Core/Expressions/`:
- `IAdScalar.cs` — static-abstract interface (C# 11 generic math) for real-only scalar operations
- `Dual.cs` — forward-mode dual number, N-wide gradient via `[InlineArray(8)]` (MaxN=8, allocation-free)
- `SddScalar.cs` — thin `double` wrapper implementing `IAdScalar<SddScalar>` (FD / plain-eval path)
- `AdWarnings.cs` — thread-static model-name context for domain-clamp warnings to `Console.Error`
- `SddEvaluator.cs` — generic `Eval<T>(Expr, bindings, modelName)` — ONE tree-walk, two scalar types
- `AstWalker.cs` — collects all `RefExpr` names from an AST (used by Elaborator for SDD scope injection)
- `FiniteDiff.cs` — central-difference gradient helper (AD oracle + production FD fallback)

Gate (tests/Core.Tests/Expressions/AdVsFdTests.cs): AD of the hero i2 at (v1=−3.05, v2=48) matches
central FD to ≥4 sig figs: gm = ∂i2/∂v1 ≈ 62.4 mS, gds = ∂i2/∂v2 ≈ −9.45 µS (negative — correct).

### Implementation notes (reality vs. design)
- `SddEvaluator.Eval<T>` is a generic local-function nest inside a single static method. Conditions in
  `ConditionalExpr` are evaluated by extracting `T.ValueOf()` (the scalar) and comparing doubles —
  AD takes the active-branch derivative, the other branch is not evaluated.
- `Dual.Exp` caps argument at 700 (preventing overflow); `Dual.Log`/`Sqrt` clamp with warn.
  Together, `log(exp(x)+1)` (softplus) evaluates correctly for all x — large x gives ≈ x, very
  negative x gives ≈ exp(x). No special softplus pattern needed.
- SDD equation expressions **may contain whitespace** — the SDD line parser uses bracket-depth-zero
  boundary detection instead of the general whitespace tokenizer (Phase 3 follow-up, 2026-06-02).
  Boundary: next `I[p,w]=`, `Q[p,w]=`, etc. at paren-depth 0. Multiple assignments on one line OK.
  Backslash line-continuation (`\` at end of line) is also supported.
- `Dual.NMax(a, b)`: picks the larger N for binary operations; constants have N=0 (zero gradient).

### Step 2: SDD device
New file `src/Core/Devices/SddModel.cs`:
- `ComponentModel` subclass, `ModelKind.Nonlinear`, `Stamp` is a no-op.
- Constructor receives cached equation ASTs + resolved scope-variable dict.
- `Evaluate(in PortVoltages v)` calls `SddEvaluator.EvalDual` for each port equation → (i, q, dg, dc).

`ComponentModelFactory` change: "SDD" added to `_parameterizedTypes`. `CreateSddModel` parses
`Value.String` equation entries, validates `F[]/C[]/w≥2` hard errors, skips `In[]/Nc[]` noise entries.

`Elaborator` change: `ResolveSddParameters` special-cases SDD — stores equation strings as
`Value.String`, walks each equation AST to collect scope-variable references, resolves them from scope,
and injects them as `Value.Real` in the resolved-params dict. The factory then sees both strings and
resolved numbers.

Gate (tests/Core.Tests/Devices/SddModelTests.cs): hero SDD parses; `Evaluate` at (−3.05, 48) returns
i2 ≈ 49.11 mA, i1 = −61 mA, gm ≈ 62.4 mS, gds ≈ −9.45 µS (negative).

### New device: VoltageSourceModel
`src/Core/Devices/VoltageSourceModel.cs` — Group-2 branch-current element. Stamps Va − Vb = V
(branch constraint + KCL). Parameter `V=`. Required for bias sources in the DC hero circuit.
Registered as type `V` in `ComponentModelFactory`.

## Enabled semantics — AnalysisChain (brief-sweep-revamp-2-dispatch, 2026-06-17)

`AnalysisChain` (`src/Core/Design/AnalysisChain.cs`) is a pure, framework-free resolver that
honors `Analysis.Enabled` when walking a parametric-sweep chain.

- **`ResolveEffectiveInner(innerName, tb)`**: descends from `innerName`, skipping disabled
  `ParametricSweepAnalysis` nodes (collapse), until it reaches either an enabled sweep or any base
  analysis. Used by `ParametricSweepEngine.Run` in place of the former raw name lookup.
- **`ResolveEffectiveTop(root, tb)`**: from the chain root, skips disabled outer sweeps to the
  first thing that actually runs. Used by `SchematicRunService` dispatch.
- **`IsChainRunnable(top, tb)`**: true only if the chain eventually bottoms out at an enabled base
  analysis. Used by `SchematicRunService` to skip dead chains (disabled base).

Semantics:
- Disabled sweep → collapses (its axis is dropped; its inner runs in its place). Spec is untouched.
- Disabled base → whole chain is inert (nothing runs, no result emitted).
- Both sweeps disabled → effective top is the base; runs as a plain single-point analysis.

Gate: 9 tests in `tests/Core.Tests/Design/AnalysisChainTests.cs` (pure); 4 integration tests in
`tests/Engine.Tests/Parametric/ParametricSweepEnabledTests.cs`. Build 0W/0E; 1629 total pass.

Stage 3 (unified editor UX with per-axis Enabled + reorder) follows.

## `.cnl` enabled flag + Spec persistence (brief-sweep-revamp-1-persistence, 2026-06-17)

- **`enabled=false` in `.cnl`**: `CnlWriter` appends `enabled=false` to every sub-line of any
  analysis whose `Enabled` is false (multi-segment S-param gets it on each segment line). `CnlReader`
  has a shared `ParseEnabledToken` helper wired into all six typed parsers (DC, HB, S-param,
  parametric sweep, loadpull, loadpull-pursuit). Absent token → `Enabled = true` (default). Gate:
  5 tests in `tests/Core.Tests/Netlist/CnlEnabledTests.cs`.
- **Sweep Spec round-trip through `.csch`/`.canl`/clipboard**: `CschAnalysis` DTO now carries
  `PsaMode`, `PsaStart`, `PsaStop`, `PsaStepOrCount`, `PsaKind` for spec-form sweeps. `ToDto`
  prefers the Spec fields (omits `PsaValues`) when `Spec` is non-null; `FromDto` has a Spec arm
  (tried first) and a list arm. Explicit-list PSAs still round-trip unchanged. Gate: 7 new tests in
  `tests/Ui.Tests/AnalysisSerializationTests.cs`.

## Parametric sweep — Start/Stop/Step|Npts (brief-parametric-sweep-stepcount, 2026-06-16)

`SweepExpander` and `SweepAxisMode` moved from `src/Ui/Schematic/` → **`src/Core/Design/SweepExpander.cs`** (no Avalonia deps) so the CNL reader can use them without violating the Core→UI firewall.

`SweepSpec` (in `Analysis.cs`) redesigned: `{ Start, Stop, StepOrCount, Mode: SweepAxisMode, Kind: SweepKind }` — no `Variable` field. `ParametricSweepAnalysis` gains a **spec constructor** that expands eagerly (populates `SweepValues`) and stores `Spec` for `.cnl` round-trip fidelity; the existing array constructor sets `Spec = null`.

**CNL reader** (`TryParseParametricSweepDirective`): now accepts both `Values=v1,v2,…` (list; `Spec=null`) and `Start= Stop= (Step= | Npts=) [log | log=true]` (spec; `Spec` retained). Bare `log` keyword detected via `HashSet<string> bare` (same pattern as SParam parser).

**CNL writer** (`FormatParametricSweepAnalysis`): emits compact `Start= Stop= Step=|Npts=` form when `psa.Spec != null`, falling back to `Values=` list for array-only PSAs.

6 gate tests in `tests/Core.Tests/Netlist/SweepSpecCnlTests.cs`: StartStopStep (121 pts), StartStopNpts (7 pts linspace), Log (4 decades), log=true keyword, Values regression, round-trip compact form. Build 0W/0E; 260 Core.Tests pass.

## Vdc — DC voltage source (brief-vsource-vdc-fix, 2026-06-16)

`VoltageSourceModel` **deleted**; replaced by `VdcModel` (`src/Core/Devices/VdcModel.cs`).

**Root bug:** legacy `V:` CNL lines produced `Vac=` parameter names, but `VoltageSourceModel.Stamp` only read the `"V"` key → voltage silently stamped as 0 V at DC.

**Fix architecture:**
- `VdcModel` stamps at DC only: `Math.Abs(omega) < OmegaTolRads (1 rad/s)` → stamps `Vdc` value; all other ω → stamps zero. Reads `"Vdc"` param (alias `"V"`). Keeps `LastBranchIndex` for HB linear extractor.
- **CnlReader backward compat** (`ParseInstanceLine`): if `typeName == "V"` (OrdinalIgnoreCase) → remap to `"Vdc"`. Also normalizes `Vac=` or `V=` param names to `Vdc=` for any `Vdc` instance (if no `Vdc` override already present). Old `.cnl` files with `V:` sources load and simulate correctly with no manual conversion.
- **`ToneSourceModel`**: fixed 0-Hz tone superposition — at ω=0, now accumulates `_currentVdc` plus all tone phasors whose `FreqHz ≈ 0` into the source voltage. `GetZeroHzToneWarnings(path)` returns a list of warnings (one per zero-Hz tone with non-trivial phasor); called by the Elaborator which routes them into `netlist.AddWarningOnce`.
- **`HbLinearExtractor`**: updated `VoltageSourceModel` references at lines 390 and 547 → `VdcModel`.
- **`ComponentModelFactory`**: `"V"` factory entry → `"Vdc"` → `new VdcModel()`.

8 gate tests: 4 Engine.Tests (`tests/Engine.Tests/Devices/VdcComponentTests.cs`) + 4 Ui.Tests (`tests/Ui.Tests/VdcComponentTests.cs`). Build 0W/0E; 1419 total tests pass.

## VAR variable component — design note
`NetExtractor` in `src/Ui` routes `SymbolKind.Var` component parameter rows into `Cell.Variables` (sub-cell) or `TestBench.GlobalVariables` (testbench top). No Core change was needed: `Elaborator.BuildGlobalScope` already binds `tb.GlobalVariables` and `BuildCellScope` already binds `cell.Variables`, so per-cell isolation and HB sweepability are automatic. VAR never appears as an `Instance` or `ElaboratedComponent`; its `EngineReference` sentinel is `"VAR"` (not a factory primitive).

## CNL generic instance parser — unit token handling (brief-unit-token-phantom-nodes, 2026-06-16)

The CNL generic instance parser (`ParseInstanceLine` in `CnlReader.cs`) now recognises
**identity/measurement unit tokens** — `V`, `A`, `W`, `dBm`, `dB`, `kV`, `mV`, etc. — as
consumable trailing units after a `key=value` param token. Previously, only linear-scale units
(in the `Units._scales` table) were consumed; tokens like `V` and `dBm` that are absent from that
table leaked into the net list as phantom "net" entries, shifting all subsequent node indices.

**Root cause fixed:** A P1Tone line `Pavl=Pin dBm` or a Vdc line `Vdc=-3.05 V` placed `dBm`/`V`
in the net section because `Units.IsKnown` is intentionally linear-scale-only (see `Units.cs`
comments). The fix adds `Units.IsRecognizedUnit(u)` = `IsKnown(u) || _identityUnits.Contains(u)`,
where `_identityUnits` is a fixed allow-list of valid-but-identity units. This predicate replaces
`IsKnown` in both the separate-token path and `TrySplitGluedUnit` in `ParseInstanceLine`.

**Position gate (safety):** the consume check fires **only inside the `key=value` param branch**,
never in the leading net section. A net token (even one named `V`) can never appear in that
position, so the single-letter `V` is unambiguous.

**Evaluator:** `Evaluator.ApplyUnit` extended to treat identity/measurement units as scale = 1.0
(value already in base unit) rather than throwing `Unknown unit`. Linear-scale units are unchanged.

**Node-picker effect:** with phantom nodes removed, the V-cube node axis contains only real
user-named nets. The existing `__`-prefix filter hides internal engine-minted nodes. No additional
picker code was needed — the cleanup is fully from the parser fix.

5 gate tests: `CnlReader_P1Tone_NoPhantomUnitNets`, `CnlReader_Vdc_NoPhantomUnitNets`,
`CnlReader_DoesNotEatRealNet`, `GluedUnit_StillSafe` (in Core.Tests) and
`Hb_Vout2_NonZeroFundamental` (Engine.Tests — verifies back-solved linear node is non-zero
after the index-shift bug is eliminated).

## SDD single-index equations + net-arity validation (brief-sdd-single-index-nets, 2026-06-16)

SDD equations accept **single-index** sugar in both `CnlReader` and `ComponentModelFactory`:
- `I[p]=expr` ≡ `I[p,0]` (port-p current); `Q[p]=expr` ≡ `I[p,1]` (port-p charge).
- Two-index `I[p,w]` and `I[p,1]` (legacy charge form) still work unchanged.

**CnlReader**: `SddAssignmentHeader` regex extended to `\d+(,\d+)?` — single-index `I[1]=` is now a valid boundary marker, so equation fragments no longer leak into the net list as phantom nodes. `ParseSddLine` also strips any `key=value` tokens in the net section (e.g. `Ports=2`) into parameter overrides rather than treating them as net names.

**Elaborator**: `RxSddEquation` extended to `^[IFCQi][^\[]*\[` to pass `Q[p]` single-index through to the factory. Odd net count (not divisible by 2) now throws: `"SDD '<inst>': expected an even number of nets (2 per port: +,−); got N."` — no more silent `portCount = N/2` truncation.

**Factory**: `RxCurrentEq1 = ^I\[(\d+)\]$` and `RxChargeEq1 = ^Q\[(\d+)\]$` handle single-index forms. The shared `ValidateAndBind` helper gives a clear error when an equation references a port index beyond the net count: `"equation references port P but only K ports of nets were given (need 2P nets for a P-port SDD)"`.

**User-facing correction**: `SDD:X1  Vin 0  Vout 0  I[1]=…  I[2]=…` — 4 nets (each port referenced to ground). `_v1 = V(Vin)−V(0)`, `_v2 = V(Vout)−V(0)`.

**Node-picker (deferred)**: `n1/n2/n3`-style auto-named nodes are real user nets — filtering them from the axis combo requires a scope decision (hide `^n\d+$`? user toggle?). Not implemented here.

7 gate tests: 6 in `tests/Core.Tests/Devices/SddSingleIndexTests.cs` (net/equation split, `I[p]` binds current, `Q[p]` binds charge, two-index regression, odd-net error, port-ref-beyond-nets error) + 1 in `tests/Engine.Tests/HarmonicBalance/SddSingleIndexHbTests.cs` (full HB sweep with single-index SDD, Vout fundamental non-zero, no phantom nodes in axis).

## Ask before
- Changing the `.cnl` or JSON format (round-trip + interop).
- Changing the scope/binding rule or the kinded-value model (ripples into the engine and SDD).
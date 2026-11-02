# Units inside expressions — `x = 10um + 1mil`

**Phase:** a `src/Core` expression-engine change, with its consumers in `src/Design` (the `.c3d` resolver) and `src/Ui`
(the parameter editor, the 3D editor's typed fields). One engine serves all of them, so this lands everywhere at once.

**Design authority:** root `CLAUDE.md` §Expressions ("One expression engine … **never string substitution**");
`docs/design/expressions.md` §3 (tokens), §8 (units, var-unit-wins), §15A (locale); `src/Core/CLAUDE.md`'s **"Ask
before: changing the `.cnl` or JSON format"** — this brief adds no key and no format, and §5 says why that is true.

**Predecessors, read first:** `brief-core-length-units.md` (why `m` is milli and the metre is `metre`),
`brief-var-unit-wins-consistency-complete.md` (the rule this extends), and `src/Core/RESOLVED.md` "A variable's own
unit re-scaled the unit-bearing names it referenced (2026-09-27)" — the fix that made a VAR's own unit obey var-unit-wins,
which this brief builds on directly.

---

## 0. What this is, in one paragraph

Today a unit attaches only at the **assignment** level: a row's unit field, or one trailing token lifted into it
(`RFfreq = 2 GHz`). Inside an expression a unit is a parse error — `10um + 1mil` fails at `um`, because juxtaposition is
not an operator. Other tools accept per-term units, and users keep writing them: the 3D editor's own docs carry a
callout, **"the unit trap"**, because `cav_w + 40` (with `cav_w` in mil) is `cav_w` plus forty **metres** under
var-unit-wins, and the only advice it can give is "write the forty as a name". This brief adds **unit-suffixed
literals** — a number with a unit glued to it, `10um`, `1mil`, `2.4GHz`, `1.5nH`, `50Ohm` — evaluated to base SI at parse
time and counted as **unit-bearing**, so a site unit is never applied on top of them. `cav_w + 40mil` then means what it
says. What a **bare** literal beside a unit-bearing term should mean is a separate decision (§5 Q1) and must be
settled before M3.

---

## 1. What exists — read before writing anything

### 1.1 The grammar

- `src/Core/Expressions/Token.cs` — `ReadNumber` takes digits, `.`, and an optional exponent; `ReadIdentifier` takes
  letters/digits/`_`. `10um` lexes as `Number("10")`, `Identifier("um")`.
- `src/Core/Expressions/Parser.cs` — the Number case accepts exactly one juxtaposition, `10j` (implicit imaginary,
  line ~95); anything else after a number is `Unexpected token`. **So no text that parses today contains a glued unit**,
  which is what makes this change additive (§4 G1).
- `src/Core/Expressions/Ast.cs` — `NumberExpr(double Value)`. Consumers that switch on it: `Evaluator`, `AstWalker`,
  `FreqDeferral` (and its `Render` printer), `SddCompiled`, `SddRegisterProgram`, `SddEvaluator`,
  `Spice/SpiceExpression.cs`, `Spice/SpiceBehaviouralSource.cs` (and its `Print`), `src/Design/Schematic/SpiceChargeSpelling.cs`.
  `grep -rln NumberExpr src` is the list; re-run it, do not trust this one.

### 1.2 The unit table and var-unit-wins

- `src/Core/Expressions/Units.cs` — `_scales` (linear: SI prefixes `T G M k m u n p f`, Hz…THz, H/F families, Ohm
  family, S family, V/A/W prefixed, length `metre mm um cm nm mil in inch`, angle `deg rad`), `_identityUnits` (V, A, W,
  dB, dBm … scale 1 or not linear at all), `UnitNormalizer.ToEngineUnit` (`Ω`→`Ohm`, `µ`→`u`).
- **`m` is milli**, deliberately (brief-core-length-units §5 q1). The metre is `metre`. Do not change this.
- **Var-unit-wins** (expressions.md §8): a site unit is skipped when the expression references a unit-bearing name.
  Implemented by `Evaluator.ReferencesUnitBearingVar(ast, scope)`, now used by `Eval`, `Resolve` (2026-09-27),
  `FreqDeferral.InlineRef`, and outside the engine by `ReferencesUnitBearingVariable(string, Scope)`:
  `Elaborator` (~701, ~1881), `C3dResolver` (`MarkDerivedUnits`, `Field`, `SkipsSiteUnit`), `FreqUnit`. Re-grep
  `ReferencesUnitBearing` — every caller is a site this brief must teach about literals.

### 1.3 The unit splitters at the edges — five of them, and one will break

Each of these lifts a unit written in the value text into the row's unit field. They exist because the grammar had no
unit production. All five must be audited; **the parameter editor's is wrong for a compound expression the moment
literals exist**:

| Where | What it does | With unit literals |
|---|---|---|
| `Units.LiftInlineUnit` (`.cnl` VAR lines, `NetExtractor`) | splits a SPACED trailing unit only when the unsplit text does NOT parse | unchanged for `2 GHz`; `2GHz` now parses and is simply kept — same value, still unit-bearing |
| `CnlReader.TrySplitGluedUnit` (component params, `KitNetlistReader`) | regex, WHOLE value `<number><unit>` only | unchanged — a compound expression never matches |
| `SchematicViewModel.ParseExpressionUnit` / `TrySplitTrailingUnit` | splits a trailing glued unit run after a digit, anywhere | **breaks**: `10um + 1mil` → expression `10um + 1`, unit `mil`. Must split only a whole `<number><unit>` (as the `.cnl` regex does) |
| `C3dEditorViewModel.SplitUnit` (3D typed fields) | splits a SPACED trailing length unit | unchanged; note `cav_w + 40 mil` still means metres for the 40 (the site unit is skipped) — Q1 |
| `LayoutUnits.TryParse` (3D/layout typed boxes, plain values) | exact decimal `<number><length unit>` to DBU | **keep** for a plain value: it is exact (decimal→DBU), the engine is double. Only an expression goes to the engine |

### 1.4 Renaming is by TOKEN

`C3dExpressionText.Rename` (`src/Design/ThreeD/C3dVariableEdits.cs`) walks `Tokenizer` identifier tokens. If `um` in
`10um` stays an identifier token, renaming a VAR called `um` or `mil` rewrites a unit. The suffix must be part of the
number's token.

---

## 2. The design

### R-uie-1 — lexing: a unit suffix is part of the number token

- A number immediately followed (**no whitespace**) by a run of letters (plus `Ω`, `µ`, `μ`) is ONE token, `Quantity`,
  when the run — normalised by `UnitNormalizer` — is a unit the table knows. `10um`, `1mil`, `2.4GHz`, `1.5nH`, `50Ω`,
  `10µm`, `1e-3metre`.
- **Whitespace separates.** `2 GHz` is not a literal; it stays the assignment-level spelling `LiftInlineUnit` lifts.
  This keeps every existing lift path meaning exactly what it means today (§1.3).
- **`10j` keeps its meaning** (checked before the unit rule). No unit starts with `j`.
- An exponent is consumed first: `1e-3mm` is 1e-3 × 1e-3 m. `2e` with no digits is still the error it is today.
- **Unknown suffix** (`10mils`, `2pi`): a parse error naming the suffix and the length units, in the style of
  `C3dUnits.Engine`'s message — not `Unexpected token`.
- **Logarithmic units are refused**: `0dBm`, `3dB` → "a dB value is not a multiplier; use dBm(…)" (expressions.md §8:
  dB/dBm are functions).
- Which units may be suffixes: every linear `_scales` entry, and the scale-1 identity units V, A, W — see Q2 and Q3.
- A user name that spells a unit is still a name where it is not glued to a number: `2*mil` references a VAR `mil`.
  Say so in expressions.md.

### R-uie-2 — the AST: `NumberExpr` carries its unit

`NumberExpr(double Value, string? Unit = null)`, **Value already in base SI**. Every consumer that only needs the value
(`Evaluator`, the SDD compilers, the SPICE writers, differentiation) works unchanged — that is the reason not to add a new
node type. Two things read `Unit`:
- **unit-bearing detection** (R-uie-3);
- **printers** — `FreqDeferral.Render` prints `10um` back (the deferred text must stay unit-bearing, or a site unit is
  applied to an already-scaled value after inlining); the SPICE printers print the scaled value.

### R-uie-3 — a unit literal is unit-bearing

Rename `ReferencesUnitBearingVar` to what it now means (e.g. `IsUnitBearing(ast, scope)`): true when the expression
references a unit-bearing name **or contains a unit literal**. Every caller in §1.2 goes through it, including
`C3dResolver.MarkDerivedUnits`, so a `.c3d` VAR `w = 10mil` with no unit field is marked with the scale-1 `metre` and a
field typed in mil does not re-scale it.

### R-uie-4 — a bare literal beside a unit-bearing term

**Settle Q1 before M3.** Today, and after R-uie-1..3 alone, `cav_w + 40` is forty metres. With literals, users can write
`cav_w + 40mil`; the question is whether the bare `40` should keep meaning metres. The rule, if Q1 answers (b): during
evaluation at a site with a unit, a **bare** operand (no unit literal, no unit-bearing reference) of `+`, `-`, a
comparison, or a conditional's branches, whose sibling IS unit-bearing, is scaled by the site unit. A multiplicative
operand (`2*w`, `w/4`) stays dimensionless. Powers are the hard case — `sqrt(w*w + 25)` adds 25 to an AREA — so either
track a length power through `*`, `/`, `^` and scale by site^power, or refuse a bare additive literal beside any
non-unit-power term with a message saying "give it a unit". Pick one in M3 and record why.

### R-uie-5 — the edge splitters

Fix `SchematicViewModel.TrySplitTrailingUnit` to lift only a WHOLE `<number><unit>` value (share
`CnlReader.TrySplitGluedUnit`'s regex rather than writing a third one). Leave the other four as §1.3 says, and add a
test per splitter that a compound expression with literals passes through unsplit.

### R-uie-6 — what stays separate

`LayoutUnits` and `WBondUnits` are exact integer/decimal systems (expressions.md §8 "do not unify them"). A typed box
keeps `LayoutUnits.TryParse` for a plain value, and hands only an expression to the engine. The SPICE reader's own
suffix dialect (`SpiceNumber`: `meg`, …) is a different language and is not touched.

### R-uie-7 — what the user sees

- The typed-field preview (`=`/`≈`), the Variables panel and `explain --expr` show the value in the site's unit, as now.
- `check`: the existing ">1 m" warning stays; under Q1 (a) it gains "a bare number beside a name with a unit is in
  base SI".
- Docs: expressions.md §3 (a `Quantity` token), §8 (literals, unit-bearing, Q1's rule, `m` is milli, `metre`); the
  3D chapter's "unit trap" callout rewritten; the parameter-editor and `.cnl` reference pages; `circuitrf reference`
  topics that describe expressions. **Doc sources only — do not run DocGen.**

---

## 3. Milestones

**M1 — the literal.** Tokenizer + parser + `NumberExpr.Unit` + unknown/log-unit refusals. Gates: `10um + 1mil` =
10e-6 + 25.4e-6; `2.4GHz`; `50Ω`, `10µm`; `10j` unchanged; `1e-3mm`; `10mils` and `0dBm` refused with their messages;
every consumer in §1.1 compiles and an SDD equation containing `1pF` evaluates and differentiates.

**M2 — unit-bearing everywhere.** R-uie-3 through every caller; `FreqDeferral.Render` round-trips a literal; rename of a
VAR named `mil` leaves `10mil` alone. Gates: a site in mil over `10um + 1mil` takes no second scale (`Eval`, `Resolve`,
`FreqDeferral`, `Elaborator` override path, `C3dResolver` field); a `.c3d` VAR `w = 10mil` (no unit field) drives a mil
field to exactly 10 mil.

**M3 — the bare literal (after Q1).** R-uie-4 as decided, with the `.c3d` gate `Gate6_ALiteralBesideAUnitBearingName_…`
(`tests/Ui.Tests/ThreeD/ExpressionsGateTests.cs`) rewritten to the new rule — it currently pins the metres behaviour.

**M4 — the edges and the docs.** R-uie-5, R-uie-7. Gate: typing `10um + 1mil` in the schematic parameter editor, the
3D typed field and a `.cnl` VAR line gives the same value in all three.

---

## 4. Guardrails

- **G1 — additive only.** Every expression that parses today must parse to the same value. Prove it with a scan over
  every expression in `examples/` and `testdata/` (the scan in `src/Core/RESOLVED.md` 2026-09-27 is a starting point),
  before and after, plus Core.Tests and the targeted Ui classes that cover units (`FreqExprUnitTests`,
  `ExpressionsGateTests`, `EvaluatorVarUnitWinsTests`, `LpAuthoringTests`, `LppAuthoringTests`).
- **G2 — no string substitution.** A literal is a token and an AST node, never a text rewrite.
- **G3 — no new file key, no format change.** A literal is ordinary expression text in fields that already hold
  expressions. Existing documents are byte-identical on a round trip.
- **G4 — `m` stays milli, the metre stays `metre`.** Document the consequence: `2m` in a length field is 2 mm.
- **G5 — locale invariant** (§15A): `.` is the decimal point; `,` is the argument separator.
- Tests: targeted classes only (`--filter FullyQualifiedName~…`), never the full suite or all of Ui.Tests; one test per
  claim; read failures from the TRX. Findings go in `src/Core/RESOLVED.md` (and `src/Ui/RESOLVED.md` for the editors),
  **never in any `CLAUDE.md`**.

---

## 5. Owner questions

**Q1 — a bare number beside a unit-bearing term** (`cav_w + 40`, typed in mil):
- (a) keep base SI (metres), and warn in `check` and the typed-field preview;
- (b) **recommended:** the bare number takes the site unit where it is added to, subtracted from or compared with a
  unit-bearing term (R-uie-4). Changes the value of any existing document that relied on metres — the scan in G1 will
  say whether any does; the shipped examples avoided the pattern on purpose;
- (c) refuse it: "give 40 a unit".

**Q2 — bare SI prefixes as suffixes** (`2u`, `3k`, `100p`, `1m`): **recommended yes** — the table already treats them as
units and it is how circuit people write values. Cost: `2m` is milli (G4).

**Q3 — the identity units V, A, W as suffixes** (`48V`, `2W`): recommended yes for V and A. `W` is also this codebase's
name for a microstrip width, but `2W` does not parse today, so nothing existing changes meaning; the risk is only that a
user who meant `2*W` gets two watts. Allow, refuse, or warn?

---

## 6. Not this phase

Unit algebra and dimension checking across different dimensions (`1GHz + 1mil` is accepted and meaningless — a
dimension system is a larger design); units in measurement cube expressions beyond what falls out; a unit column
redesign in the VAR editor.

## 7. Completion note

Record in `src/Core/RESOLVED.md`: the token rule, what Q1–Q3 decided and why, what the G1 scan found, and every
consumer that needed changing. Update `docs/design/expressions.md` §3/§8 so they no longer say units are not tokens.

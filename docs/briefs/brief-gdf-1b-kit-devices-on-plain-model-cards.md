# Brief 1b — kit devices on plain model cards

**Series:** [gdsfactory kits](brief-gdf-0-overview.md) · **Tag:** `R-gdf1b-n` · **Phase:** measure, decide, fix
**Area:** `src/Core/Pdk/` (`KitSymbolFileReader`, `PdkImporter`), `src/Core/Netlist/Spice/SpiceModelCardTranslation.cs`,
`src/Design/Schematic/` (`NetExtractor` — `BindNativeDeviceTypes`, `RouteToCompiledModel`; `PdkPartInstaller`),
`testdata/`, `src/Core/RESOLVED.md`, `src/Design/RESOLVED.md`
**Depends on:** 1 · **Blocks:** 5
**Rule:** overview §0. No kit, foundry, process name, device name or model name in the repo. Fixtures
are synthetic. Results are recorded by process.

---

## 0. Why

Brief 1 put every part of the three open kits into the Library palette with a symbol (A 43, C 628, D 73),
and found that most of them still do not simulate. Its completion note lists three open items, and they
are one gap: **a kit device whose behaviour is an ordinary SPICE model card has no route to circuitRF's own
physics.**

1. Inside a kit netlist, `NetExtractor.BindNativeDeviceTypes` sends every device that references a
   `.model` card to `RouteToCompiledModel`, and nowhere else. A diode, BJT or sheet-resistor card the
   kit's simulator builds in finds no compiled implementor and is refused with advice to compile
   Verilog-A that the kit does not ship. `SpiceModelCardTranslation` already turns these card types into
   circuitRF components on every other SPICE path, and is never asked here.
2. A record-format symbol is paired with its subcircuit only by `PdkImporter.BestSubcircuitFor`, a
   name-token matcher that never pairs a single-word part name. The symbol's own netlist line says what
   it calls.
3. Kit D's diodes are primitive elements on `.model` cards with no subcircuit at all, a shape the kit-part
   path has no route for.

These are the largest single cause of kit parts not simulating, in all three kits.

### What the three kits use (measured in brief 1, model folders only, regression folders excluded)

| Card type | Kit A: 130 nm SiGe BiCMOS | Kit C: 130 nm CMOS | Kit D: 180 nm CMOS |
|---|---|---|---|
| diode | 26 (level 1 or unstated) | 22 (level 3) | 29 (level 3) |
| npn | **12 (level 9)**, the HBTs | 3 (level 1) | 6 (level 1) |
| pnp | 3 (level 1 or unstated) | 3 (level 1) | 4 (level 1) |
| resistor | none (its resistors use a compiled model) | 26 | 23 |
| capacitor | 2 | 43 | 24 |
| MOS | compiled model, out of scope here | compact-model level, out of scope | level 54, out of scope |

**The trap this brief must not fall into.** `SpiceModelCardTranslation` reads `LEVEL` for a MOS card and
refuses level 4 and above. It binds a **diode or BJT card without looking at `LEVEL` at all.** Routing kit
A's level-9 HBT cards there as it stands would quietly simulate them as a different, simpler BJT law. That
answer would parse, converge, and be wrong.

## 1. `R-gdf1b-1`: measure first

Re-run brief 1's scratch harnesses (placement through the real seeding path; `circuitrf netlist`; DC at the
typical corner) against the three upstream clones, outside the repo. For every diode, BJT, resistor and
capacitor card the kits' parts reach, record:

- its type and stated level;
- which of its parameters `SpiceModelCardTranslation` would map, and which it would report as unmapped;
- for resistor cards, whether `SpicePassiveModelBinding` binds them, and if not why (kit C's resistor cards
  were refused at Run; find out whether the card states no sheet resistance or something else).

Write the table into the completion note before changing anything.

## 2. `R-gdf1b-2`: the level gate, before anything is routed

In `SpiceModelCardTranslation`, a diode, NPN or PNP card that **states** a level circuitRF does not implement
is refused, and the refusal names the card, its type and its level. Default (D2): diode and BJT accept level 1
or an unstated level. Nothing is ever read as "the nearest level".

This changes behaviour on the existing SPICE import paths too. That is intended: they have the same silent
answer today. Gate: a synthetic level-9 NPN card is refused by name; a level-1 card binds as before.

## 3. `R-gdf1b-3`: a kit device on a card uses circuitRF's own physics when no compiled model claims it

In `RouteToCompiledModel`, the order is:

1. A compiled implementor of the card's type, as today. The kit or the user compiled it, and that is the
   kit's own statement of the model.
2. Otherwise `SpiceModelCardTranslation.Translate(card)`. When it binds, the instance becomes that built-in
   component: the card's parameters go under the instance's, and the instance's geometry (`area`, `pj`,
   `m`) goes where the binding says it goes.
3. Otherwise refuse with the translation's own refusal sentence. Today's "compile them" advice stays only
   for a type for which the kit actually ships a Verilog-A source.

**Every unmapped parameter is reported, once per card per run**, never per instance. A card whose unmapped
set includes a parameter that changes the DC answer (D3 decides the list per type) is refused rather than
approximated.

Cards inside corner sections are read from the SELECTED section, through the binder extraction already uses.
Gate: the same synthetic card in two sections gives two different operating points.

## 4. `R-gdf1b-4`: a record symbol's own statement of what it calls

A record symbol states its netlist line in `format` and its defaults in `template`. Brief 1 already reads the
format's terminals. Read the rest:

- **The element.** `spiceprefix` when the template sets it, else the first letter of the template's
  `name`. `X` means a subcircuit call. `D`, `Q`, `R`, `C` and `M` mean a primitive element on a card.
- **The name called.** The format's model token with `@model` substituted from the template, literal prefix
  included (`<prefix>@model` exists in one kit).

Carry both on `PdkPart`. Then:

- **`X`: pair with the subcircuit of that name** when exactly one definition of it exists in the kit's
  model libraries, case-insensitively, and its port count equals pins plus extra terminals. "Model
  libraries" means the files the kit's corner sections reach, or `PreferTheKitsMainLibrary`'s canonical set.
  Test, regression and LVS folders never count. **This is D1, and it is the owner's to approve**: the
  repository has reverted name-based pairing once (`src/Core/CLAUDE.md`, "Automatic exact-name binding").
  The differences are that the symbol STATES the name rather than circuitRF guessing it, and the corner
  selection that pairing lacked then exists now. The brief stops here and asks before building the pairing.
- **`D`/`Q`/`R`/`C`: a primitive element on a card** (kit D's shape). Emit the built-in component §3 binds
  the card to, under the same level gate and the same corner rule. `M` stays refused (MOS compact models are
  out of scope), with a sentence that says so.

The existing token matcher stays as the fallback for kits that state nothing.

## 5. `R-gdf1b-5`: a numerical reference, generated outside circuitRF

This is a numerical change, so it needs a `testdata/` regression against an externally generated reference
(`CLAUDE.md`, Validation expectations).

- Write **synthetic** cards: a level-1 diode with area and perimeter scaling, a level-1 NPN and PNP, and a
  sheet-resistor card. Use the kits' parameter shapes, with no kit's numbers.
- Write each into a DC bench in the SPICE dialect and in `.cnl`.
- **Stop and ask the owner** to generate the reference operating points with an independent simulator of
  that dialect, outside the repo, and commit them as fixed data. This brief does not generate them, and a
  reference circuitRF produced itself proves nothing.
- Gate: node voltages and terminal currents within 1e-6 relative, or 1 nV / 1 pA absolute below that.

Until the reference exists, §3 ships behind nothing and the completion note says the routing is unvalidated
in those words.

## 6. Decisions (defaults stand unless the owner changes them before the build)

- **D1: pairing on the symbol's statement.** Default: **ask** (§4). Not built until approved.
- **D2: the level gate.** Accept level 1 or unstated for diode and BJT; refuse any other stated level by name.
- **D3: the diode level used by kits C and D.** Default: refused, until §1's measurement shows which of its
  parameters circuitRF's diode models with the same meaning. If the measurement shows a faithful mapping,
  bring it back as a proposal; do not build it in this brief.
- **D4: order.** A compiled implementor first, circuitRF's own physics second, refusal third.

## 7. Non-goals

- MOS compact models with no source in the kit (kits C and D): no native implementation, nothing to compile.
- Kit A's level-9 HBTs: refused by name under D2. A compiled implementation the user supplies already
  routes through step 1 of §3.
- Parameter expressions that depend on a node voltage (varactors in kits A and C).
- Any new device physics.

## 8. Tests (one per claim, synthetic)

1. The level gate: a non-implemented stated level is refused by name; level 1 binds.
2. A kit device on a supported card, with no compiled implementor, becomes the built-in component with the
   card's parameters under the instance's.
3. A compiled implementor still wins over a built-in.
4. Unmapped parameters are reported once per card.
5. A card's value follows the selected corner section.
6. (If D1 is approved) A record symbol pairs with the one subcircuit its format names, and not with a
   same-named definition in a test folder.
7. A primitive-element symbol on a card emits the built-in component.
8. The `testdata/` reference gate (§5).

Run only the classes you touch.

## 9. Completion note

- §1's table.
- Brief 1's family table re-run, per kit: which families now reach DC, and through which route (compiled,
  built-in, or refused, with the sentence).
- What D1 and D3 were decided as.
- Whether the §5 reference exists. If it does not, say the routing is unvalidated.

---

## Completion note (2026-10-30)

Built: §2, §3, §4 (both halves — D1 was left to judgement and is built, see Decisions), and §5's
benches. **The §5 reference does not exist, so the built-in routing is unvalidated.** Measured against the
upstream clones outside the repository, typical corner.

### §1 — the cards the kits' parts reach

| Card | Kit A: 130 nm SiGe BiCMOS | Kit C: 130 nm CMOS | Kit D: 180 nm CMOS |
|---|---|---|---|
| diode | 10 unstated level + 14 level 1; all map. Left behind: IK (13), TLEV/TLEVC (12), TCV (7), JSW/CJSW (5), IKF, TRS/TRS2, MJSW, PHP, CJP, NS, CTA, AF/KF | 12 level 3 | 13 level 3 |
| npn | 3 **level 9** | level 1, maps | 6 level 1, map |
| pnp | 1 unstated + 1 level 1, map. Left behind: AF/KF; CJS/MJS/VJS; TRC1/2, TRE1/2, TRM1/2 | 1 level 1, maps. Left behind: CJS/MJS/VJS, ISS, NKF, NS, TNF1/2, TIKF1, PTF, AF/KF, TREF (now read) | 4 level 1, map. Left behind: CJS/MJS/VJS, NKF, TBF1/2, TIKF1, TVJC/TVJE, CTC/CTE, TLEVC, PTF, AF/KF, TREF (now read) |
| resistor | none — its resistors are a compiled model | 26: 14 state only RSH (bound per instance by the passive binding when `w`/`l` are given); 12 state no resistance at all — TC-only cards whose ELEMENT states `r=`, which the passive binding refused (fixed below) | 19: 12 RSH-only, 7 TC-only |
| capacitor | 1, no capacitance | 43, no capacitance (geometry cards, bound per instance) | 13, as C |

Level-3 diodes would map RS, N, CJ, PB, MJ, IBV, AREA, XTI, EG and leave JS, JSW, CJSW, MJSW, PHP, IK, IKR,
TCV, TRS, TLEVC, CTA, CTP, TPB, TPHP, TREF and the level-3 geometry words (LM, LP, WM, WP, XM, XP, XOI, XOM,
XW). Kit D's resistor subcircuits, and kit C's high-value resistor body, compute their value from a node
voltage (`v(a,b)`) — a non-goal, so neither resistor family reaches DC whatever the card does.

**Kit D's typical corner read no cards at all.** Its corner file's sections read other sections of the
same file (`.lib 'self' dio`), which the reader refused as an inclusion cycle. Fixed (keyed on file and
section). Its device families are still declared as ALTERNATIVES on one axis (`typical` vs
`diode_typical` vs `bjt_typical`), where the kit means several to be included together, so a diode card
is unreachable while the MOS corner is selected. Not fixed here: it is the corner model's shape, not
this brief's.

### What changed

- **Level gate (D2)**: a `D`/`NPN`/`PNP` card stating any level but 1 is refused by card, type and level,
  on every import path.
- **Routing (D4)**: compiled implementor → built-in → refusal with the translation's own sentence. The
  compile advice remains only for a type with no built-in. Refused rather than approximated: a net
  count that differs from the built-in's terminals; a DC-affecting parameter left behind (nonzero
  literal or any expression); an instance word with no home. Left-behind parameters reported once per
  card.
- **Two silent wrong answers found on the way, both fixed**: an unstated bipolar parameter took circuitRF's
  component default (Vaf 71 V, Br 3.3, Ise 1.6 fA, Cje 83 fF …) instead of the dialect's — it now
  takes the dialect's, on every path; and `TREF` (the extraction temperature) was unread.
- **Primitive-element symbols** (`D`/`Q`/`R`/`C`/`M`) read their element and called name, point at the
  one file declaring the card, and extract as a one-element cell routed like the same element inside a
  subcircuit. `M` is refused with a sentence. A cards-only model file is now recognised as the SPICE
  dialect at extraction.
- **A resistor element stating its own `r=`** on a TC-only card binds (kit C's contact resistors).

### Brief 1's families, re-run

| Family | Kit A | Kit C | Kit D |
|---|---|---|---|
| MOSFET | unchanged: DC solves (compiled) | unchanged: cannot simulate | unchanged |
| HBT / BJT | HBT refused: LEVEL=9 (D2). Its Schottky family's level-1 PNP now runs on the built-in, but that cell's three diode cards are refused (IK, TLEV, TCV) | refused: the instances bind 4 nets, the built-in has 3 terminals | not placed in brief 1's set |
| Resistor | unchanged (compiled) | contact resistors now bind; the body is voltage-dependent (non-goal) | voltage-dependent (non-goal) |
| MIM capacitor | unchanged: DC solves | unchanged: DC solves | not paired |
| Diode | refused, built-in: IK/IKF, TLEV, TCV, JSW stated nonzero | refused: LEVEL=3 (D3) | the 9 primitive diode parts now point at their card file (0 before); refused: card only inside a corner section not selected, and LEVEL=3 (D3) |
| MOM capacitor (A) | compile advice kept: not a built-in type | — | — |

**No family newly reaches DC.** What changed is that every card-backed device now says the true
reason it does not: no family is told to compile Verilog-A the kit does not ship.

### Decisions

- **D1: built** (the owner left it to judgement). Measured first: the likeness matcher had paired 23
  stated-name parts with a DIFFERENT device than the symbol names — kit C 13 (an LVT device with an RF
  variant, a 20 V device with its isolated variant), kit D 10 (a p-channel part with an n-channel
  definition), and every one of kit D's 18 pairings landed in a regression netlist. The rule: exact name,
  terminal count = pins + extra terminals, ties by the canonical library then by the files the nominal
  corner sections reach; ambiguous or absent → unpaired, never a likeness guess; test, regression and
  LVS folders excluded from every pairing. Result: kit A 25 → 39 paired (all 39 stated-name symbols);
  kit C 59 → 74, the 13 corrected, one dropped (a 3-pin symbol against a 4-port definition, which
  extraction refused anyway); kit D 18 → 0 — its real definitions sit inside corner sections offered
  as alternatives on one axis, the corner-model limit recorded above. Import time unchanged (kit C ~9 s
  on this machine with and without the nominal-corner walk). Recorded in `docs/design/pdk-import.md` §9.
- **D2: as the default.**
- **D3: refused.** The measurement says the AREA half of a level-3 card has the same meaning in
  circuitRF's diode (JS × AREA → Is, CJ × AREA → Cj0, PB → Vj, MJ → M, RS ÷ AREA → Rs, N, BV, IBV), but the
  PERIMETER half (JSW, CJSW, MJSW, PHP × PJ) has no home, and 12 of 12 kit C and 9 of 13 kit D cards state
  JSW. **Proposal, not built**: a level-3 card as two built-in diodes in parallel — the area junction, and
  a sidewall junction with Is = JSW, Cj0 = CJSW, M = MJSW, Vj = PHP, Area = PJ — provided the geometry
  words (LM … XW) are measured inert in the kits' cards and the sidewall shares the card's N.
- **D4: as the default.**

### §5

Synthetic cards and a one-operating-point bench in both dialects are in `testdata/kit-card-dc/`
(`cards.lib`, `bench.sp`, `bench.cnl`, README). **The reference has not been generated; it needs the owner
and an independent simulator. Until it exists, the built-in routing is unvalidated.**

Gates: `tests/Core.Tests/Netlist/KitCardTranslationTests.cs` (5), `tests/Core.Tests/Pdk/StatedSubcircuitPairingTests.cs`
(2: test 6, and the nominal-corner tie), `tests/Ui.Tests/KitCardDeviceRoutingTests.cs` (5: tests 2, 3, 4, 5, 7).
Test 8 waits on the reference. Ran only the touched classes:
Core SPICE/PDK classes 354/354; Ui card import, subcircuit import, compiled routing, kit, corner, CLI
import classes 404/405 with one skip (and `ModelCardImportTests.OldSpiceMultipliers_…` re-pinned).

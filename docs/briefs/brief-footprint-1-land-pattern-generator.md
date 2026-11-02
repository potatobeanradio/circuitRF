# Brief 1 — the land-pattern generator: twelve case sizes, three densities, layers by role

**Series:** [SMT footprints](brief-footprint-0-overview.md) · **Tag:** `R-fp1-n` · **Phase:** P1
**Area:** `src/Design/Layout/Footprints/` (new), `src/Design/Layout/PCells/`
**Depends on:** nothing · **Blocks:** briefs 2-5

---

## 0. What this brief delivers

One new folder, `src/Design/Layout/Footprints/`, holding:

- **`SmtCase`** — the case table: code, metric twin, body dimensions, termination dimensions.
- **`LandPattern`** — IPC-7351B land geometry from a case plus a density level.
- **`ChipLandPatternGenerator`** — a `PCellGenerator` producing the `LayoutView` for one case at one
  density, on the layers the resolved technology actually has.
- **`FootprintRef`** — the reference string grammar (`smt:<case>@<density>`) and its parser.

It draws nothing, it names no Avalonia type, and `src/Cli` can call it. That is the point of putting
it here and not beside the six microstrip generators in `src/Ui` — see the overview §1a.

---

## 1. `R-fp1-1` — the case table

Two-terminal chip components, imperial codes, body length x width. **Every entry carries its metric
twin**, for the overview's §1e reason.

| code | metric | body L x W (mm) | note |
|---|---|---|---|
| `008004` | 0201 | 0.25 x 0.125 | |
| `01005` | 0402 | 0.40 x 0.20 | |
| `0201` | 0603 | 0.60 x 0.30 | **the series default** |
| `0402` | 1005 | 1.00 x 0.50 | |
| `0603` | 1608 | 1.60 x 0.80 | |
| `0805` | 2012 | 2.00 x 1.25 | |
| `1206` | 3216 | 3.20 x 1.60 | |
| `1210` | 3225 | 3.20 x 2.50 | |
| `1812` | 4532 | 4.50 x 3.20 | |
| `1825` | 4564 | 4.50 x 6.40 | W > L; the code is inches L x W and this one is wider than it is long |
| `2010` | 5025 | 5.00 x 2.50 | |
| `2512` | 6332 | 6.30 x 3.20 | |

**Reverse-geometry**, where the terminations are on the LONG edges — these exist to shorten the
mounting loop, which is the quantity railRF's Q2 is entirely about, so a power-integrity tool that
cannot draw one is missing the interesting half:

| code | metric | body L x W (mm) |
|---|---|---|
| `0306` | 0816 | 0.80 x 1.60 |
| `0508` | 1220 | 1.25 x 2.00 |
| `0612` | 1632 | 1.60 x 3.20 |

**Larger MLCC bodies**, which is where bulk ceramics live:

| code | metric | body L x W (mm) |
|---|---|---|
| `1808` | 4520 | 4.50 x 2.00 |
| `2220` | 5750 | 5.70 x 5.00 |
| `2225` | 5763 | 5.70 x 6.30 |

**Moulded tantalum / polymer**, EIA metric codes — the Power Rail example's own 100 uF bulk part has
nowhere to point today:

| code | letter | body L x W x H (mm) |
|---|---|---|
| `3216-18` | A | 3.20 x 1.60 x 1.80 |
| `3528-21` | B | 3.50 x 2.80 x 2.10 |
| `6032-28` | C | 6.00 x 3.20 x 2.80 |
| `7343-31` | D | 7.30 x 4.30 x 3.10 |
| `7343-43` | X | 7.30 x 4.30 x 4.30 |

**`R-fp1-1a`** The table is DATA, in one place, with a test that walks every row and asserts the
metric twin is consistent with the imperial code (`0402` -> `1005` because 0.040 in = 1.0 mm) —
because a hand-maintained twin column is a column that goes wrong once and is never looked at again.

**`R-fp1-1b`** A tantalum code is already metric, so its "twin" column reads the letter (`A`…`X`) and
not a second number. A reader who sees `3216-18 (A)` cannot confuse it with an imperial code; a
reader who saw `3216 (metric 8064)` would rightly think something was broken.

---

## 2. `R-fp1-2` — the land pattern, from IPC-7351B

A land pattern is not a case size. IPC-7351B gives three **density levels** for the same body:

| level | code | for |
|---|---|---|
| Most | `M` | maximum land protrusion — wave solder, hand rework, high reliability |
| **Nominal** | **`N`** | **the default** — general commercial reflow |
| Least | `L` | minimum land — high-density, fine pitch |

Density is computed from the case's termination dimensions and the level's three fillet goals
(toe `J_T`, heel `J_H`, side `J_S`), not tabulated per case per level — 20 cases x 3 levels is 60
numbers nobody will ever re-check, where the formula is one function with a test per level.

**`R-fp1-2a`** `LandPattern.For(SmtCase, DensityLevel)` returns pad width, pad height, pad centre
pitch and the courtyard rectangle, all in DBU at the view's own `DbuPerMicron`.

**`R-fp1-2b`** **The density is part of the reference**, not a setting — `smt:0402@N`. Two instances
of one case at two densities are two different pieces of artwork and must be two different generated
cells, or the second one silently gets the first one's pads out of the content-addressed store.

**`R-fp1-2c`** Reuse `ComponentImport`'s existing variant concept rather than inventing a second
one: `BuiltLayout.Variant` is already "R-PL1-25's density suffix", and the nominal pattern is already
the one that becomes `PrimaryLayout`. The grammar here and the suffix there must spell a density the
same way.

---

## 3. `R-fp1-3` — layers by ROLE, never by key

The overview's §1b is the whole reason this is a generator. Four roles, resolved in this order:

| role | resolved by | required? |
|---|---|---|
| copper | `SubstrateResolver.ResolveSignalLayerKey`, i.e. the topmost conductor | **yes** |
| soldermask | `Interchange.PcbLayerName == "F.Mask"`, else `Purpose` | no |
| silkscreen | `Interchange.PcbLayerName == "F.SilkS"`, else `Purpose` | no |
| courtyard / assembly | `Interchange.PcbLayerName == "Edge.Cuts"` is NOT it — see R-fp1-3c | no |

**`R-fp1-3a`** A missing OPTIONAL role omits its shapes and emits one `PCellResult.Diagnostics`
line naming the role and the technology. It does not fall back to another layer. Putting a
silkscreen outline on Soldermask Top because the technology has no silk is worse than drawing no
outline: the mask opening is manufacturing data and a stray rectangle in it is a defect nobody sees
until fabrication.

**`R-fp1-3b`** A missing COPPER role is a **refusal**, not a diagnostic — there is no land pattern
without lands. The sentence names the technology and says which layer role is missing, in the shape
`convert`'s own refusals take. This is what happens on a MMIC technology, and it is the right
answer: a chip land pattern on `mmic-GaAs_2LM_100um` is a category error.

**`R-fp1-3c`** **Do not draw a courtyard on `Edge.Cuts`.** That layer is the board outline; a
courtyard rectangle on it is a routed slot. Where the technology declares no assembly layer, the
courtyard is omitted with a diagnostic (R-fp1-3a) and is NOT relocated. The Power Rail example's
technology has neither, which is exactly why this rule is written down before anyone tries it.

---

## 4. `R-fp1-4` — the pins, and what they are for

**`R-fp1-4a`** Two `PCellPin`s, named `"1"` and `"2"`, on the copper layer, at the pad centres,
`WidthDbu` equal to the pad width, outward directions 180 and 0. Same contract `MlinPCell` states
(`src/Ui/Layout/PCells/MlinPCell.cs:33`), so nothing downstream needs a second case.

**`R-fp1-4b`** Pin names are ORDINALS and not `A`/`K` or `+`/`-`. A polarised part's orientation is
a property of the symbol and the placement, not of the land pattern, and a generator that named pins
`+`/`-` would have to know a capacitor from a resistor, which it does not and must not.

**`R-fp1-4c`** `PCellResult.Handles` is EMPTY. A case size is a discrete choice from a table, not a
continuous dimension, so there is nothing for a grip to drag. A handle here would let a user drag an
0402 into a shape no case code names, and the reference string would then lie about what the artwork
is.

---

## 5. `R-fp1-5` — the reference grammar

```
smt:<case>            density defaults to N
smt:<case>@<density>  density is M, N or L
```

**`R-fp1-5a`** The parser is TOTAL and returns a refusal sentence, never throws and never falls back.
An unknown case code lists the codes it does know — the rule `--tech` already follows.

**`R-fp1-5b`** The grammar is deliberately NOT a path. A `Footprint` value that parses as
`smt:` is a built-in; anything else is a relative path to a `.clay` or a cell folder, resolved by
brief 3 exactly as `CellRef` is. One discriminator, no third case.

**`R-fp1-5c`** `smt:` is reserved. A user's own `.clay` named `smt:something` cannot exist (`:` is
not a path character on Windows and is a foot-gun on the others), so the two spaces cannot collide.

---

## 6. `R-fp1-6` — registration

`src/Ui` registers `ChipLandPatternGenerator` with `PCellRegistry` at startup. **Not by adding
twenty entries to the built-in dictionary** — that dictionary is closed by design
(`src/Ui/Layout/PCells/PCellRegistry.cs:12`) — but through `AddResolver`, with one resolver that
answers for every `smt:` id and nothing else.

**`R-fp1-6a`** The resolver is registered ONCE, process-wide, and is not workspace-scoped — the case
table is a constant and does not belong to a workspace. So it is registered from the same place the
built-ins are and is never passed to `RemoveResolver`. (Contrast MW1's R-mw1-4: a kit's resolver IS
workspace-scoped because a kit belongs to a workspace.)

**`R-fp1-6b`** `src/Cli` calls the generator directly rather than through `PCellRegistry`, which it
cannot see. That is what putting the generator in `src/Design` buys, and a test asserts the CLI path
and the registry path produce byte-identical `LayoutView`s for the same case, density and technology.

---

## 7. Gate

`tests/Design.Tests/Footprints/ChipLandPatternTests.cs` — new.

1. **The table is self-consistent.** Every imperial row's metric twin follows from its code. One
   test, walking the table.
2. **Three densities differ, and in the right direction.** For `0402`: `M` pad area > `N` > `L`, and
   all three are centred on the same pitch axis.
3. **Layers resolve by role on three technologies.** `pcb-2layer_FR-4_70mil_1oz` (silk at (5,0)),
   `pcb-4layer_FR-4_62mil_1oz` (silk at (7,0)) and `examples/Power Rail/tech/pcb-4layer-1p6mm` (no
   silk at all) each produce copper on their own top conductor, and the third omits silk with a
   diagnostic naming it. **This is the test that would have caught a shipped `.clay`.**
4. **A MMIC technology is refused by name.** `mmic-GaAs_2LM_100um` produces no pattern and a sentence
   naming the technology and the missing role.
5. **No courtyard on `Edge.Cuts`.** Assert no shape of any generated pattern lands on the layer whose
   `PcbLayerName` is `Edge.Cuts`, on every shipped PCB technology.
6. **The parser is total.** `smt:0402`, `smt:0402@M`, `smt:9999`, `smt:0402@Q`, `smt:`, `""` — five
   refusals and two successes, and the unknown-case refusal lists the real codes.
7. **CLI and registry agree.** Byte-identical `LayoutView` from both paths, per R-fp1-6b.

Firewall: `tests/Firewall.Tests` already asserts `src/Design` references no UI framework; the new
folder needs no new rule, and a test that it names no Avalonia type would duplicate one that exists.

## 8. Scope

- **No polarity marker.** R-fp1-4b's reason.
- **No thermal pad, no paste layer, no 3D body.** Copper, mask, silk, courtyard. Paste is a
  manufacturing output and belongs with the exports if it is ever wanted.
- **No non-chip packages** — no SOT, SOIC, QFN, BGA, connector. Two-terminal chips and moulded
  tantalums only. A multi-pin package is a different land-pattern problem and Component Import
  already covers it for anyone who has the data.

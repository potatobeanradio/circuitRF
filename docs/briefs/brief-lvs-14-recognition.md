# Brief 14 — geometric device recognition, for artwork with no instances

**Series:** [LVS](brief-lvs-0-overview.md) · **Tag:** `R-lvs14-n` · **Design note:** [`lvs.md`](../design/lvs.md) §4.1 tier 3
**Area:** `src/Design/Layout/Lvs/DeviceRecognition.cs` (new), `src/Design/Layout/TechModel.cs`,
`src/Design/Layout/TechPersistence.cs`, `src/Design/Layout/Drc/DrcLayerExpr.cs`
**Depends on:** 3 · **Blocks:** 15

---

## 0. What this brief delivers

The fallback for artwork that carries **no instances at all** — a hand-drawn MMIC device, a GDSII
import whose hierarchy was flattened away, a Gerber board read back as polygons.

**It ships and it is default off.** Turning it on for a design circuitRF authored would
re-recognise devices it already knows, from geometry, less reliably than reading the instance that
is right there.

---

## 1. `R-lvs14-1` — why this is a fallback and not the main path

**`R-lvs14-1a`** Note §1.1: circuitRF's layout is **instance-bearing**. A device is already a
first-class object in the file, so the primary reading is correspondence, not recognition. That
premise is what makes this whole series small.

**`R-lvs14-1b`** Recognition is where a classical LVS spends most of its complexity and almost all
of its configuration burden. Building it as the default would import that cost for no benefit on
every design circuitRF itself produced.

**`R-lvs14-1c`** It is specified and built now because the extraction's **shape** must accommodate
it — a recognised device and an instance device must be the same `LvsDevice` to the comparator —
and retrofitting that is the kind of change that never happens later.

**`R-lvs14-1d` Default off, per run, and per technology.** `--recognize` on the verb; a
`DeviceRules` block in the `.ctech`. A technology with no block cannot recognise anything and
saying so is not an error.

---

## 2. `R-lvs14-2` — the deck, beside `DrcRules`

```json
"DeviceRules": [
  { "Name": "NiCr resistor", "Kind": "Resistor",
    "Body": "Resistor", "Terminals": "Metal1",
    "Parameters": { "R": "SheetRho * Length / Width" } },

  { "Name": "MIM capacitor", "Kind": "Capacitor",
    "Body": "MIM Metal AND Nitride", "Terminals": ["Metal1", "MIM Metal"],
    "Parameters": { "C": "CapDensity * Area" } }
]
```

**`R-lvs14-2a` `Body` is a layer expression and reuses `DrcLayerExpr` unchanged.** A DRC rule's
region already says `Metal1 AND NOT Nitride`; this is the same question asked for a different
purpose. **A second layer grammar is a second grammar to document, test and get wrong.**

**`R-lvs14-2b` `Parameters` go through the ONE expression engine** (`expressions.md`) — tokenize,
Pratt-parse, AST, evaluate; never string substitution. The names available are the measured
geometry (`Length`, `Width`, `Area`, `Perimeter`) plus the technology's own declared constants.

**`R-lvs14-2c`** A rule that will not parse is a **`check` error**, on brief 1's terms:
`DrcPredicateParser` is already in `check`'s validator table for exactly this reason and the
device deck joins it. A deck that fails at run time instead is a deck that fails during the one
operation the user wanted to succeed.

**`R-lvs14-2d`** `Kind` maps to the canonical `DeviceKind` (brief 4). An unknown kind is a
`check` error listing the real ones — never a fallback, which is `--tech`'s own rule.

---

## 3. `R-lvs14-3` — how a device is recognised

**`R-lvs14-3a`** Evaluate `Body` to a region; each connected **component** of it is one candidate
device. `DrcRegions.Components` already does this and is the same function the partition uses.

**`R-lvs14-3b`** Terminals are the connected pieces of the `Terminals` layers that **touch** the
body, ordered by the rule's own layer order and then by position along the body's principal axis.
Two terminals for a two-terminal device; a candidate with a different count is
`lvs.recognize.terminal-count`, warning, and is **not** emitted as a device.

**`R-lvs14-3c`** Geometry measurements are taken the way the region eval already takes them.
`Length` and `Width` are along and across the body's principal axis; a body whose principal axis
is ambiguous (within a few percent of square) reports `lvs.recognize.ambiguous-axis` and does not
guess — a resistor read the wrong way round is off by (L/W)², silently.

**`R-lvs14-3d` Recognition never runs inside an instance.** Instances are already devices; a
recognised device inside one would be the same device twice. Recognition runs **only** on the
root's own shapes and on cells explicitly flattened for LVS (brief 9 `R-lvs9-3d`).

**`R-lvs14-3e`** A recognised device's path is derived from its coordinate — `R@1.204,3.881` —
because nothing named it. It has no designator, so it can only ever be matched **structurally**
(tier 2), which is honest and is what recognition buys.

---

## 4. `R-lvs14-4` — what recognition may never do

**`R-lvs14-4a` Never override an instance.** Where copper belongs to a placed device, the
instance wins. A rule that matched inside an instance's artwork would double it.

**`R-lvs14-4b` Never invent a parameter the rule did not state.** A recognised device with no
`Parameters` claims nothing, and brief 10's *compare-only-where-both-claim* handles it.

**`R-lvs14-4c` Never silently skip a candidate.** Every candidate the deck produced and the
extraction rejected is reported with its reason. A recognition pass that quietly dropped half the
devices would make a design read as clean.

**`R-lvs14-4d` No rule authoring UI in this brief.** The deck is written in the `.ctech`, which is
already a hand-editable file with an editor. A rule builder is a product decision with no evidence
behind it yet.

---

## 5. `R-lvs14-5` — the honest limit, stated where users meet it

**`R-lvs14-5a`** Recognition answers *what does this copper look like*. It cannot answer *is this
the device the process actually makes*. A run using it reports `lvs.recognize.in-use` at **info**,
naming the deck and the count, so a clean report is never mistaken for a stronger claim than it
is.

**`R-lvs14-5b`** The note's own limit applies and is worth repeating here: where the artwork
declares a device that is not really there, LVS cannot catch it and does not pretend to. That is
the EM extractor's question.

---

## 6. Gate

`tests/Ui.Tests/Lvs/RecognitionTests.cs`.

1. **Default off.** The correct board and the MMIC cell compare identically with and without a
   deck present in the technology, as long as `--recognize` is absent (`R-lvs14-1d`).
2. **A hand-drawn NiCr resistor is recognised** — body, two terminals, an `R` from the formula —
   against hand arithmetic, not against another circuitRF path.
3. **A hand-drawn MIM cap is recognised** with terminals on **two different layers**
   (`R-lvs14-3b`).
4. **Recognition does not run inside an instance.** A board with both a placed resistor and a
   hand-drawn one yields exactly two devices, not three (`R-lvs14-3d`).
5. **An ambiguous axis is reported and not guessed** (`R-lvs14-3c`).
6. **A wrong terminal count is reported and emits no device** (`R-lvs14-3b`).
7. **Every rejected candidate is reported with a reason** (`R-lvs14-4c`).
8. **A malformed deck is a `check` error**, by id, before any run (`R-lvs14-2c`); an unknown
   `Kind` lists the real ones (`R-lvs14-2d`).
9. **The layer expression is `DrcLayerExpr`'s** — a source scan for a second parser
   (`R-lvs14-2a`), and a test that a DRC region expression and a `Body` expression with the same
   text produce the same region.
10. **Parameters go through the one expression engine** — a formula using a technology constant
    and a measured quantity evaluates, and a cyclic one is caught by the engine's own cycle
    detection (`R-lvs14-2b`).
11. **`lvs.recognize.in-use` is reported whenever recognition contributed** (`R-lvs14-5a`).

## 7. Scope

- **No rule authoring UI** (`R-lvs14-4d`).
- **No transistor recognition.** Two-terminal passives are what the shipped technologies describe.
  A three-terminal rule is additive to the same deck when a real design needs one.
- **No second layer grammar and no second expression language** (`R-lvs14-2a`, `b`).
- **No change to any instance-based reading.** Recognition is additive and subordinate
  (`R-lvs14-4a`).

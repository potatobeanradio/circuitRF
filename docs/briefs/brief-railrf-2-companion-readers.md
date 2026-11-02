# Brief 2 — the three companion readers: placement, BOM, and the part library

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail2-n`
**Area:** `src/Design/Layout/Interchange/`, `src/Design/RailRf/` · **Depends on:** 1 · **Blocks:** 3, 11
**Design note:** [`railrf.md`](../design/railrf.md) §2.2 ("the three companion files"), §5, Q-11, Q-14, Q-15

---

## 0. What this brief delivers

Three readers, none of which creates geometry:

| Reader | File | What it attaches |
|---|---|---|
| `PlacementFile` | `src/Design/Layout/Interchange/PlacementFile.cs` | A refdes → (x, y, rotation, mirror, footprint) table, in the artwork's own DBU. |
| `BomFile` | `src/Design/Layout/Interchange/BomFile.cs` | A refdes → (internal part number, value, footprint, description) table, plus what it parsed out of the description. |
| `PartLibrary` | `src/Design/RailRf/PartLibrary.cs` | A **document** keyed by internal part number: the model, the voltage rating, the dielectric class, the footprint, the bias curve, the ESR where one exists. |

### `R-rail2-1` — the governing rule, and it is `BoardNetlistFile`'s rule verbatim

> **These files are EVIDENCE ABOUT THE ARTWORK. They are never geometry.**

`src/Design/Layout/Interchange/BoardNetlistFile.cs` states it at the top of the file and it is the reason
the first two of these live beside it. Nothing in this brief creates a shape, moves a shape or deletes a
shape. The Gerber, Excellon and `.kicad_pcb` readers remain the sole source of every coordinate, every
diameter and every outline; these attach **facts** to objects those readers already built, and **report**
anything they cannot attach. A placement row naming a refdes the artwork does not have is a message with
a count in it, never a footprint.

Read `BoardNetlistFile.cs` before writing a line of this. Its recognition/units/cross-check shape is the
one to follow, and two of its three hard-won lessons apply directly (§1, §2 below).

---

## 1. `R-rail2-2` — the placement origin is a REFUSAL, never a guess

§2.2, and Q-14 closed it explicitly:

> **The coordinate origin is a choice made at export — symbol origin, body centre, or pin 1** — and the
> exporting tool does not always record which. Three quarters of a millimetre on an 0402 is the
> difference between landing on the part's own pad and landing on its neighbour's.

This is the same shape as the Excellon coordinate format, which is already a refusal in `convert` for the
same reason: leading vs trailing suppression differ by four orders of magnitude on identical text. Here
the error is smaller and **therefore worse** — a wrong Excellon read misses the board and is loud; a
wrong placement origin lands on the neighbouring pad and is silent.

```csharp
public enum PlacementOrigin
{
    /// <summary>The footprint's own origin as the library defines it.</summary>
    SymbolOrigin,

    /// <summary>The centre of the part body.</summary>
    BodyCentre,

    /// <summary>Pin 1.</summary>
    PinOne,
}
```

- **Where the file states it**, read it, and say so as `Declared`.
- **Where it does not**, `PlacementFile.Read` returns a result whose `Origin` is null and whose `Refusal`
  is a sentence **naming the flag that answers it**, on `convert`'s exact spelling. Headless that is the
  end of it. In the window (brief 7) it is a three-way choice on the import dialog **with nothing
  pre-selected** — Q-14 is explicit that there is no house convention to learn, and *a default here is
  the guess the refusal exists to prevent.*

### `R-rail2-3` — the cross-check, because a refusal is not the only failure

`BoardNetlistFile` already cross-checks its own inferred units against the artwork's extent, because *"a
netlist read at the wrong scale that matches nothing is at least loud; one read at a wrong scale that
still lands inside the board mislabels every pad on it, silently."* The same applies to units here, and
to the origin:

- **Units**: the header's stated units, cross-checked against the artwork extent, reported with its
  evidence (`Declared` / `Defaulted` / `Artwork`), on `BoardNetlistUnitsEvidence`'s exact shape.
- **Origin**: once an origin is chosen, every row is resolved to pads and **the count that landed on no
  pad at all is reported.** A chosen origin that puts 40 % of the parts on empty copper is almost
  certainly the wrong one of the three, and the count is the only thing that can say so. It is a
  reported number, not an automatic re-choice: railRF does not try the other two and pick the best, because
  a board where two of the three score similarly would then be chosen silently.

### `R-rail2-4` — `mirror` is what puts a part on the bottom, and a mismatch is reported

A row that sets `mirror` and a footprint with no bottom-side artwork is **reported, not assumed** (§2.2).
Count it, name the refdes, and carry on.

---

## 2. `R-rail2-5` — the BOM's description is parsed, and what was parsed is SHOWN

§2.2:

> The description conventionally carries the dielectric class and the voltage rating
> (`MLCC 10n0 50V 0402 X7R ±10%`), which is exactly what derating needs (§9), so railRF **parses it and
> shows what it parsed** in the parts table for correction. **It never silently acts on a guess about a
> free-text field.**

So the reader returns both:

```csharp
public sealed record BomRow(
    string Refdes,
    string? PartNumber,
    string? Value,
    string? Footprint,
    string? Description)
{
    /// <summary>What was recognised in <see cref="Description"/> — each field null where nothing was.
    /// <b>Shown in the parts table beside the description it came from</b>, for correction. A parsed
    /// dielectric class drives the ESR default (brief 11) and the derating (Q-12), and both of those are
    /// wrong in an invisible way if the parse was.</summary>
    public BomDescriptionParse Parsed { get; init; } = BomDescriptionParse.Nothing;
}

public sealed record BomDescriptionParse(
    string? DielectricClass,   // X7R, X5R, C0G/NP0, …
    double? VoltageRatingV,
    string? CaseCode,          // 0402, 0603, …
    double? TolerancePercent)
{
    public static readonly BomDescriptionParse Nothing = new(null, null, null, null);
}
```

**A field the parse could not recognise is null and stays null.** It is not filled from the value column,
not inferred from the case code, and not defaulted per class. Brief 11's ESR fallback keys on
`DielectricClass`; a null one produces a part marked as having no class rather than a part quietly given
X7R's dissipation factor.

### `R-rail2-6` — the aggressor pre-fill, and it is a suggestion

Where the BOM names a crystal or a converter, emit a `RailAggressor` with
`Origin = RecognisedFromBom` (brief 1 `R-rail1-9`). It is a **row the user can delete**, shown as
recognised rather than typed, because a pre-filled frequency nobody checked is exactly the one that will
be wrong. Recognition is by value and description shape only; a part it cannot classify contributes no
row rather than a guessed one.

---

## 3. The part library

### `R-rail2-7` — it is keyed by PART NUMBER, and that is a modelling decision, not a schema one

§2.2:

> **Parts are identified by PART NUMBER, and that is what the model attaches to.** … A model is entered
> once and used twenty-two times. The same rule holds at DC — one on-resistance for every instance of the
> same FET.

So the file splits in two and this brief builds both halves:

- **The part library**, keyed by internal part number: the model, the voltage rating, the dielectric
  class, the footprint, the bias curve, the ESR where one exists. A `.crlib` document beside the
  `.crail`, read headlessly so the `rail` verb works in CI.
- **The placement**, mapping each refdes to a part number and a coordinate — which is the BOM row and the
  placement row joined on refdes, and is built rather than read.

A library row entered once and referenced twenty-two times is what makes the parts table of §2.3 step 3
tractable, and it is what makes a correction *one* edit.

### `R-rail2-8` — `L` is DERIVED from C and f₀, and it is derived once

§2.2: a library row carries **C and self-resonant frequency**, from which

```
L = 1 / ((2·π·f₀)² · C)
```

follows exactly. A 1 µF part resonating at 5.31 MHz is 898 pH; a 33 nF part at 39.1 MHz is 502 pH. Both
are in the note and both are test rows.

> **railRF reads such a table directly and derives L rather than asking for it twice.** A row carrying
> both C, f₀ *and* L is the interesting case: the derived value is used, the stated one is **compared**,
> and a disagreement beyond a stated tolerance is reported on the row. That is §7's "derived part
> inductance" gate — an arithmetic gate on the importer, not on the physics.

### `R-rail2-9` — the library carries no ESR, and that is a permanent condition (Q-15)

This brief does **not** solve it — brief 11 does. What brief 2 owns is that the model can represent all
three states honestly:

```csharp
public enum EsrProvenance
{
    /// <summary>From the part's own Touchstone file. The only route to a real number (§2.2).</summary>
    Measured,

    /// <summary>Stated on the library row.</summary>
    Stated,

    /// <summary>Derived from a dissipation-factor default for this part's dielectric class.
    /// <b>The normal case, not the degraded one</b> (Q-15) — and every peak height computed from one is
    /// marked INDICATIVE wherever it appears.</summary>
    ClassDefault,
}
```

The library row carries `EsrOhms` and `EsrProvenance`, and `ClassDefault` is what a row with neither an
ESR nor a Touchstone resolves to. **A row with no dielectric class either resolves to nothing** and is
counted — brief 11 reports the count as a headline number, because *"a mask margin in dB computed from an
indicative peak looks exactly as authoritative as a real one."*

### `R-rail2-10` — the bias curve is per part number, and coverage is a reported number

Q-12 closed derating as *correct it*, and §9 names the residual risk precisely: *"a library that is only
partly populated with curves produces a result that is partly derated, and that is worse than either
extreme unless it is visible."*

So the library row carries an optional capacitance-versus-bias curve, and `PartLibrary` exposes
**coverage** as a first-class query: how many referenced part numbers have a curve, and how many do not.
Brief 7's status strip prints it (*"3 parts with no bias curve"*), brief 11 applies it, and brief 12's
export carries it in its provenance.

### `R-rail2-11` — a per-part file attachment overrides the row

Q-11 closed this as *both*: a maintained table **and** a per-item Touchstone or SPICE model, with the
**file overriding the row**. `PartLibrary` resolves in that order and reports which won, per part, because
that is what the parts table's *model source* column shows.

---

## 4. `R-rail2-12` — a foreign file is classified through the import's own classifier

`convert`, `check` and `explain` all classify an unknown extension **by content, through the import's own
classifier**, so a GDSII or Gerber file handed to the wrong reader is *named* rather than called
unreadable. Follow it: `GerberFileClassifier.ClassifyContent` already runs `BoardNetlistFile.Recognize`
as one of its tests, and the two new readers add their own `Recognize(string head, out string why)` in
the same shape.

Recognition ordering matters and is the same doctrine GI4 states: nothing added here may take a file the
artwork or drill tests already claimed, and the placement/BOM signatures are weaker than the board
netlist's, so they run **after** it.

**A CSV that is a BOM and a CSV that is a placement file are genuinely ambiguous.** Do not guess between
them on column count: recognise on the **header row's own column names**, require a stated minimum, and
where both match, refuse and name the two flags (`--bom` / `--placement`). One more refusal is cheaper
than a board whose parts are all at (0,0).

---

## 5. Tests — `tests/Ui.Tests/RailRf/RailReaderTests.cs`

- **`R-rail2-2`**: a placement file with no origin record is refused, and the refusal text contains the
  flag name. One with a stated origin is read, and `Evidence` says `Declared`.
- **`R-rail2-3`**: the same rows read at each of the three origins against a fixture whose pads are known
  produce three different landed-count answers, and the reader reports the count rather than choosing.
- **`R-rail2-5`**: `MLCC 10n0 50V 0402 X7R ±10%` parses to `(X7R, 50, 0402, 10)`. A description with no
  class parses its voltage and leaves the class **null** — asserted as null, not as a default.
- **`R-rail2-8`**: both of the note's own rows. 1 µF at 5.31 MHz → 898 pH ± 1 %; 33 nF at 39.1 MHz →
  502 pH ± 1 %. A row carrying a contradictory stated L is **reported**, and the derived value is the one
  used.
- **`R-rail2-9`**: a row with no ESR and class `X7R` resolves `ClassDefault`; a row with no ESR and no
  class resolves nothing and is counted.
- **`R-rail2-10`**: coverage over a library where 5 of 8 referenced part numbers carry a curve reports 3
  without one, by part number.
- **`R-rail2-11`**: a row plus an attached `.s2p` resolves to the file, and the source is reported as the
  file.
- **`R-rail2-12`**: a Gerber file handed to `PlacementFile.Read` is **named as a Gerber**, not called
  unreadable. A CSV matching both header signatures is refused naming both flags.

### `R-rail2-13` — fixtures, and the deferral

§7 gates these readers *"against real files … and on a refusal where a required field is absent."*
**The owner's decision is that the reference package arrives during manual testing, after the briefs are
implemented** (2026-09-18, overview §1h).

So this brief commits **synthetic fixtures of the right shape** — hand-written, small, and deliberately
covering the refusal cases — and carries a **second, `FixtureFact`-guarded gate** that runs against the
real package when it lands, exactly as `RfCore.Tests`' proprietary loadpull fixtures already do: skipped
with a reason on a fresh clone, never failing.

Be honest about what the synthetic gate proves: **it proves the reader parses its own output.** It does
not prove the reader parses a real export. **Write that sentence into the test file's own header**, so
nobody later reads a green suite as evidence the importers work.

When real files arrive they are committed **anonymised** — the repo carries no company, vendor or product
names, so part numbers and library prefixes in a fixture are rewritten to the same *shape* (Q-18's own
parenthesis). Grep for vendor and PDK names before committing anything under `testdata/`.

### `R-rail2-14` — the four shape assumptions that must NOT be baked in

Because the real files arrive late, the cost of a surprise is what matters, and the difference between an
adjustment and a redesign is decided here. Four things a real export plausibly does that the obvious
implementation forbids. **None of these costs much to allow now. Each is a propagating change to allow
later** — into brief 3's pad resolution, brief 7's parts table and brief 16's matcher.

1. **The BOM→placement join may be ONE-TO-MANY.** §2.2 says it outright: *"behind that number sits a list
   of approved manufacturers with their own item codes."* A BOM with one row per approved manufacturer per
   part is entirely ordinary. So **do not key a dictionary on refdes and assume one row** — resolve to a
   list, and report the count where it is greater than one rather than taking the first silently. Taking
   the first is the shape that produces a plausible model from the wrong manufacturer's part.

2. **A refdes cell may carry a RANGE or a LIST.** `C1-C9`, `C1,C2,C3`, `C1 C2 C3` — a grouped BOM is the
   normal way a purchasing document is written, and it is exactly the document §2.2 describes. Parse the
   cell into a refdes *set*; a single refdes is the one-element case. A reader that treats `C1-C9` as a
   refdes named "C1-C9" matches nothing and reports nine unresolved parts, which reads as a broken board.

3. **There may be no header row at all.** `R-rail2-12` recognises on the header's own column names, and
   that is the right primary rule — but it must **fail into a refusal naming the flag**
   (`--columns`, or the equivalent), never into a positional guess. Column order is not a standard and a
   positional fallback puts the value column in the footprint column, silently.

4. **Text-file mechanics, and one naming hazard.** Encoding, line endings, quoted delimiters containing
   the delimiter, trailing separators, and a leading **byte order mark**. All ordinary; all cheap to
   handle up front; all annoying to retrofit.

   > **The naming hazard: in this feature "BOM" is a bill of materials, and in every text reader ever
   > written "BOM" is a byte order mark.** `BomFile` stripping a `Bom` is a sentence that will be
   > misread. Spell the byte order mark out in full everywhere in this file, every time.

**Test each of the four on a synthetic fixture built to exercise it.** They are the cases most likely to
be the first thing a real file does, and a test written now is a test that will be green or red on the day
the package arrives — which is the whole value of deferring with a plan rather than deferring and hoping.

### `R-rail2-15` — the twenty-line shape review, if it can be had

Overview §1h: closing Q-18's *format shape* half needs far less than the package — **the first ~20 lines
of one file of each kind**, unanonymised, uncommitted, just read. If those arrive before this brief
starts, `R-rail2-14`'s four assumptions become facts instead of allowances and the guarded gate is all
that is left outstanding.

If they do not, build `R-rail2-14` as written and carry on. That is the point of it.

---

## 6. Scope

- **No solve, no extraction, no mesh.** Briefs 3-6.
- **No ESR arithmetic, no derating arithmetic, no Touchstone impedance extraction.** Brief 11. This brief
  *represents* all three and computes none of them.
- **No new netlist reader.** `BoardNetlistFile` reads IPC-D-356 and this brief does not touch it. If
  Q-21's example turns out to be another flavour, that is a fourth reader and a brief of its own.
- **No UI, no import dialog.** Brief 7.
- **No `.kicad_pcb` change.** A board file already carries nets, footprints, refdes and values, so on that
  path these three readers are *optional* rather than required — which is §2.2's whole point about the
  two paths, and is a thing brief 3 has to honour rather than a thing this brief implements.

**On completion:** record findings in `src/Design/RESOLVED.md`. Never in a CLAUDE.md.

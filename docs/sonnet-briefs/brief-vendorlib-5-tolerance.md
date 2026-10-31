# Brief 5 — a vendor part's tolerance, read from its part number, drawn in yield

**Series:** [Vendor libraries](brief-vendorlib-0-overview.md) · **Tag:** `R-vl5-n` · **Phase:** index, extraction, yield, UI
**Area:** `src/Design/VendorLibraries/VendorLibraryHeader.cs` (the `Property` and `P/N` lines), new
`src/Design/VendorLibraries/VendorPartTolerance.cs`, `VendorLibraryIndex.cs` (`VendorLibraryEntry`),
`src/Design/Schematic/SpiceModelNetlist.cs` / `NetExtractor.cs` (the emitted cell's scale parameter),
`src/Design/Optimization/TunableCatalog.cs`, `TuningValidator.cs`, `src/Design/Statistics/StatisticsValidator.cs`,
`src/Core/Netlist/TuningDirectiveText.cs` (`tol=part`), the Parameter Editor's vendor panel and part picker
(`src/Ui/ViewModels/ParameterEditorViewModel.SpiceModel.cs`, `src/Ui/Views/ParameterEditor/`), `src/Cli/Find.cs`,
`src/Cli/Explain.cs` (`--tunables`), `src/Cli/VendorLibraryVerbs.cs` (`import library`'s summary)
**Depends on:** 1–4, and the yield series (YA-0…13) as built
**Rule:** overview §0 and §3 — with the one narrow change to §0 stated in §0 below.

---

## 0. What exists, what is missing, and the rule this brief changes

A static vendor model carries no tolerance: neither the `.subckt` nor its comment header states one
(overview §0's header fields). So brief 4 left a vendor part out of yield entirely (R-vl4-3d), and a user
who wants a real part in a Monte Carlo run has to look the tolerance up and type it.

The tolerance IS in the part number. Manufacturers spell a part's value as a short code (`100` = 10 pF,
`R60` = 0.6 pF, `1N3` = 1.3 nH, `1R0` = 1 µH) and put a **tolerance letter immediately after it**. The
letters come from one common code: `B` ±0.1 pF, `C` ±0.25 pF, `J` ±5 %, `K` ±10 %, and so on (§1).

**Overview §0 says "No code may decode a part NUMBER." This brief replaces that sentence with:** *"No code
may decode a part number, except the tolerance letter of §1 of brief 5, which is found by anchoring on the
value code computed from the header's own stated value — never by a position, a prefix or a series."* Update
the overview's §0 and its §1 table (add row 5) in the same change. No manufacturer's layout, series prefix
or letter position appears anywhere in the code, tests or docs, and the decoder works the same way for every
library.

**Measured on two manufacturers' real libraries before this brief was written** (scratch harness, outside the
repo, never committed — record the counts in `src/Design/RESOLVED.md` without naming either):

| Library | Parts with a C/L value | Decoded | Not known | Checked against |
|---|---|---|---|---|
| First, SPICE edition | 30,140 | 30,116 | 24 | the manufacturer's own published tolerance for **every** part: **30,116 of 30,116 agree** |
| Second, SPICE edition | 633 | 609 | 24 | — (no published list in plain text) |
| Second, catalogue edition | 11,950 | 11,562 | 384 + 4 ambiguous | an independent position-based decode agrees on 11,181; the rest are series where position-counting fails |

The 24 not known in each are honest refusals (a four-digit value code; a series that spells 56 nH as `056`),
never a wrong answer. Across the 334 series in the two libraries, **no series decodes at more than one offset**
(§1d's guard costs nothing).

**Approaches that were measured and REJECTED — do not reintroduce them:**

- **A fixed position from the end** (4th, 3rd, …) is one manufacturer's layout. It differs between the two
  manufacturers, between kinds, and between series of one manufacturer, and a wrong position decodes garbage
  with no error.
- **Inferring the position from "sibling" parts** (identical headers, part numbers one letter apart): right
  for one library, ambiguous for the other, whose headers say so little that voltage- and packaging-code
  siblings look identical. One series' siblings pointed at its last letter, so 106 capacitors would have decoded wrong.
- **Choosing a position by plausibility alone**: 795 wrong (2.3 %) on the first library. A voltage code
  such as `1H` reads as "±3 %", and nothing in the value contradicts it.

## 1. `R-vl5-1` — the decoder (`VendorPartTolerance`, `src/Design`)

- **`R-vl5-1a` The input** is the header's stated value and quantity (C or L) and the part number. The part
  number comes from the header's `P/N` line when it has one, or the `.subckt` name otherwise. One library's
  subcircuit names carry an edition suffix (`…_s`, `…_p`) that the `P/N` line does not, so
  `VendorLibraryHeader` gains a `PartNumber` field. A part with no C/L value, or any other quantity, has no
  tolerance and says nothing.
- **`R-vl5-1b` The value code** is every spelling of the stated value in each of the kind's units (pF, nF,
  µF for C; nH, µH for L):
  - **significand–multiplier**, for a value of at least 10 units: `100`, `104`, `226` — two significant digits
    then a power of ten
  - **`R` as the decimal point**, for a value below 100 units, every three-character spelling: `R60`, `0R6`,
    `R75`, `1R5`, `8R0`, `4R7`
  - for L in nH only, **`N` as the decimal point**: `1N3`, `0N2`, `33N`

  A match counts only when it is **not preceded by a digit** (so `100` inside `1005` does not match) and is
  **followed by a letter**.
- **`R-vl5-1c` The letter table** — exactly the letters both libraries agree on. Nothing else decodes:

  | | Absolute | Percent |
  |---|---|---|
  | **C** | `W` ±0.05 pF · `B` ±0.1 pF · `C` ±0.25 pF · `D` ±0.5 pF | `F` 1 · `G` 2 · `H` 3 · `J` 5 · `K` 10 · `M` 20 |
  | **L** | `W` ±0.05 nH · `B` ±0.1 nH · `C` ±0.2 nH · `S` ±0.3 nH · `D` ±0.5 nH | the same |

  `C` is ±0.25 pF on a capacitor and ±0.2 nH on an inductor in BOTH libraries. That is not a typo.
  **An absolute code is plausible only on a part of at most 10 pF / 10 nH whose value exceeds the tolerance.**
  An implausible letter is not a match.
- **`R-vl5-1d` Refusals, each with its reason, never a guess:**
  - no match → *not known*
  - two matches giving different letters → *ambiguous* (a value code that appears twice)
  - **one offset per file:** when the decoded letters of one library file sit at more than one offset from the
    end of their part numbers, NONE of that file decodes, and the build reports the file once. This never
    fired on the real libraries; it is the guard against a coincidental match.
- **`R-vl5-1e` A header that states a tolerance wins.** A header line or Description field reading
  `Tolerance = ±5%`, `±0.1pF` or `+/-0.1[pF]` is taken as written, and decoding is skipped for that part.
  Neither library has one today; the field is what a manufacturer would add, so read it if it appears.
- **`R-vl5-1f` Where it runs:** at index build, from text the scan already read. The tolerance lives on
  `VendorLibraryEntry` (value, absolute-or-percent, source = header or part number, or the refusal reason).
  **Counter gate: `VendorLibraryIndex.FileReads` is unchanged by this brief** — decoding reads no file.
- **`R-vl5-1g` The build says what it found:** "Tolerance known for 30,116 of 30,140 parts." The line appears
  in `VendorLibraryBuild.SummaryLines()` (so `import library`, Manage Libraries and `find` all print it), plus
  one line per file refused by R-vl5-1d.

## 2. `R-vl5-2` — fix: inductor values with a test frequency

One library writes `* Property: L=10nH at 500MHz` (and `C=1uF` with no space or brackets). The header parser
drops the whole value when ` at <frequency>` trails it, so **every inductor in that library indexes with no
value and no `L` glyph today**: the palette shows no value, brief 3's stepping and brief 4's ideal proxy both
lose the part, and §1 has nothing to anchor on.

- **`R-vl5-2a`** `VendorLibraryHeader.Parse` reads the value and drops a trailing `at <frequency>` (keep that
  frequency as text on the header, for the vendor panel). Value, quantity and glyph come back as they do for
  the bracketed spelling.
- **`R-vl5-2b`** Test: the generated fixture gains this spelling (made-up part names, overview §3), and the
  part indexes with quantity L, its value and the `L` glyph.

## 3. `R-vl5-3` — a vendor part in yield

- **`R-vl5-3a` The tunable.** A placed vendor part with a C or L quantity offers **`<Instance>.C`** or
  **`<Instance>.L`**, the spelling an ideal component already uses (`yield.md` §2). It is **statistical only**:
  `opt=1` on it is a `TuningValidator` error, because a real part's value is not a continuous knob — brief 4's
  `.Part` is. Its nominal is the part's stated value.
- **`R-vl5-3b` `tol=part`.** A new spread value meaning "this part's own tolerance":

  ```
  tune C1.C dist=gauss tol=part sigmas=3 trunc=3
  tune L2.L dist=unif tol=part
  ```

  It resolves at check time from the index, to the text an explicit spread would have (`0.1 pF`, `5%`), and
  **follows the part**: brief 3's picker and Value ▲/▼, and brief 4's Apply, change the part, and the resolved
  tolerance changes with it, with no edit to the line. An explicit `tol=0.05 pF` is still written and wins.
  `tol=part` on a part whose tolerance is not known is a `check` error naming the part and the reason, with
  the fix ("write tol= explicitly"). `tol=part` on anything that is not a vendor part is a reader error with a
  position. `CnlWriter` round-trips it byte for byte, and so does `.csch → .cnl → .csch`.
- **`R-vl5-3c` How a draw moves the model.** The extractor emits the part's cell with **one scale parameter,
  default 1, multiplied into every element of the part's own quantity** (every C of a capacitor's model, every
  L of an inductor's). An evaluation binds it to drawn ÷ nominal. The parasitics of the other kinds stay put,
  and the self-resonance moves as a real part's does. **The parameter is emitted only when the part is
  statistical**, so every other netlist is byte-identical to today's (gate: an existing extraction test's bytes
  are unchanged). Test: on a generated ladder model, the scaled cell's capacitance at the low end of its stated
  frequency range moves by the factor to within 1e-3.
- **`R-vl5-3d` With brief 4.** When `<Instance>.Part` is an optimizer variable and `<Instance>.C` is statistical
  with `tol=part`, each candidate carries its OWN tolerance. A candidate whose tolerance is not known is dropped
  from the candidate list and reported, as R-vl4-2a drops an unreadable one. Replace R-vl4-3d's last sentence
  ("this brief does not invent one") with a pointer here.

## 4. `R-vl5-4` — UI

- **`R-vl5-4a`** The Parameter Editor's vendor panel shows the tolerance on its own row beside the value:
  `Tolerance ±0.1 pF`. When it is not known the row shows `Tolerance not known`, with no explanatory text
  under it (the reason is in `check` and `find`). The row is always present, so nothing shifts when the part
  changes.
- **`R-vl5-4b`** A **Yield** toggle beside brief 4's Tune toggle adds or removes the `<Instance>.C|L` entry with
  `tol=part` and D1's distribution. It is disabled, with a tooltip giving the reason, when the tolerance
  is not known.
- **`R-vl5-4c`** The part picker's rows show the tolerance after the value (`10 pF ±5 %`). The palette's rows
  are unchanged (D3).
- Render every new row headlessly with `tools/UiSnapshot`, look at it, and list the PNGs in the completion note.

## 5. `R-vl5-5` — CLI and agents

- **`R-vl5-5a`** `find --parts` prints the tolerance as a column, and `--json` carries
  `"tolerance": {"text": "0.1 pF", "kind": "absolute", "source": "part-number"}`, or `null` beside
  `"toleranceNote": "<reason>"`.
- **`R-vl5-5b`** `explain --tunables` lists `<Instance>.C|L` as statistical-only, with the resolved `tol=part`.
  `check` reports R-vl5-3b's errors through `StatisticsValidator` / `TuningValidator`. No rule may live in the
  CLI alone.
- **`R-vl5-5c`** `reference statistics` documents `tol=part` in one sentence.

## 6. Tests (minimal — one per claim; generated fixture, made-up part names)

The fixture spells its part names in the value-code-then-letter shape (`TSTC0402A100J01`,
`TSTL1005R1N3C10`) — invented, never a real manufacturer's.

1. **Decoder table:** each spelling of R-vl5-1b decodes (one theory row per spelling: sig–mult, `R`, `N`), an
   absolute code on a 47 pF part does not, `100` inside `1005` does not match, and a value code that appears
   twice with different letters is ambiguous.
2. **The one-offset guard:** a file whose decodes sit at two offsets decodes nothing and is reported once.
3. **Header wins:** a `Tolerance =` field is used and the part number is not consulted.
4. **No extra reads:** `FileReads` before and after the build is unchanged with decoding on.
5. **The `at <frequency>` fix:** R-vl5-2b.
6. **`tol=part` round trip** through `.cnl` and `.csch`, and its two errors (`check` unknown, reader non-vendor).
7. **The scale parameter:** R-vl5-3c's 1e-3 test, and an existing extraction's bytes unchanged when the part is
   not statistical.
8. **A yield run over one vendor part** draws within ± its tolerance: every trial's bound scale lies in
   [1 − tol/value, 1 + tol/value] under `dist=unif`.

Run only the classes you touch. **The real-library measurement is a scratch harness, not a test**, and it is
re-run once at the end against both libraries (the owner has them locally; ask for the paths). It must
reproduce §0's table, or the difference is explained in the completion note.

## 7. Decisions for the owner (proposed defaults in bold)

- **D1 — The Yield toggle's distribution:** **`dist=gauss tol=part sigmas=3 trunc=3`** (a part screened to its
  limit, mostly near nominal), or `dist=unif tol=part` (every value up to the limit equally likely; more
  pessimistic).
- **D2 — How a draw moves the model:** **scale every element of the part's own quantity (R-vl5-3c)**, or add an
  ideal ΔC / ΔL beside the model (exact in value, but the self-resonance does not move, which a real part's does).
- **D3 — Tolerance in the palette:** **picker rows and the vendor panel only**, or also the palette's rows and as a
  search token (`±5%`).
- **D4 — Letters outside the shared table** (one library uses `R` for ±2.5 % on two parts): **not decoded**, or
  added per letter when a second library confirms it.

## 8. Not in this brief

- Temperature or DC-bias drift (dynamic models; overview D1).
- **The catalogue-plus-Touchstone edition** one manufacturer publishes (an XML part list beside one `.s2p` per
  part). It is a separate part-library format, and overview §4 already reserves it as a follow-up. §1's decoder
  is written so that edition can reuse it unchanged (it needs only a value, a quantity and a part number).
- Correlated tolerances between parts of one reel or lot.

## 9. On completion

Findings in `src/Design/RESOLVED.md` (decoder, the header fix, the scale parameter, and §0's measured counts
with no manufacturer named) and `src/Ui/RESOLVED.md` (the panel rows), never a `CLAUDE.md`. Update
`docs/user/src/reference/pdk-integration.md` (Vendor part libraries: where a tolerance comes from and when it is
not known), `docs/design/yield.md` (`tol=part` and the scale parameter) and `cli.md` (`find --parts`'s column and
JSON). Do not run DocGen. **Do not reply to the feature request**; the owner will, after this is built and committed.

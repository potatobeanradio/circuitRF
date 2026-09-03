# Sonnet Brief — Phase L4f: Excellon drill reading, and rebuilding vias

**Design:** `docs/design/layout-view.md` §8 (interchange), §3.1a (holes).
**Consumes L4c (Gerber + Excellon export), L4e (the Gerber reader), and the via/stackup work.**

**Second of four phases that together add Gerber import** — L4e reads artwork, **L4f** reads drill files
and rebuilds vias, L4g orchestrates a file set into a cell and a technology, L4h adds the menu entry and
the round-trip gate.

**Depends on L4e**, and not only for ordering: §4's whole payoff is pairing a drill hit with a copper
flash the Gerber reader produced. Do not start this phase first.

**Scope is drill files and the via reconstruction that needs them.** `ExcellonReader` is a pure
text-to-hits reader in the same mould as `GerberReader`: no `CellFolder`, no `Technology`, no
`Messages`, no dialog. The via pairing is a small pure function over L4e's shapes and this phase's hits.

**Write from public documentation only** — §8's standing rule against ingesting GPL sources applies to
this format as it does to every other.

**Test loop:**
```
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

---

## 1. Why this is its own phase

**A hole is not artwork, and the format is materially less self-describing than Gerber.** Gerber at
least declares its own units and digit format in every file. Drill files frequently declare **neither** —
no `M48` header, no `INCH`/`METRIC`, no format statement of any kind, just tool definitions and a stream
of coordinates. §2 is therefore the centre of gravity of this phase, not §3 or §4, and the risk it
manages is the worst kind: a file that parses cleanly and yields a board a thousand times too large or a
hundred times too small.

The payoff (§4) is that a drill hit plus a copper flash at the same point **is** a via, and pairing them
re-joins exactly what L4c's export split apart — which is what makes the round trip closed on vias
rather than merely lossy.

## 2. The format problem: a file that does not say what its numbers mean

**R-L4f-1. Infer the coordinate format, state the inference, and let the user override it. Never guess
silently.** The precedent is exact and already in the tree: L4b's R-L4b-4 for a DXF with no `$INSUNITS`,
implemented as `DxfUnitsPromptDialog`, on the reasoning that a drawing interpreted at 1000× scale is the
worst possible silent failure. That reasoning holds here with more force, because here the missing
statement is the *common* case rather than the exceptional one.

Evidence sources, strongest first:

1. An explicit format comment, e.g. `;FILE_FORMAT=2:4`.
2. `INCH` / `METRIC`, with the `LZ` / `TZ` zero-suppression word that usually follows.
3. `M71` (metric) / `M72` (inch).
4. **The tool diameters.** These are always written as explicit decimals — a tool of `0.0138` is inches
   and a tool of `0.35` is millimetres, and nothing else in the file is as unambiguous.
5. **A cross-check against the artwork's own extents.** This is the strongest signal available and it is
   free once L4g holds both readers' output: if the hits under the inferred format do not land inside
   the Gerber's bounding box, the inference is wrong. Report the comparison rather than just the verdict.

The reader returns the inference **and its evidence**; L4h decides whether that warrants a prompt.

**R-L4f-2. Zero suppression changes a coordinate by orders of magnitude, and both spellings are in
circulation — and a third form sidesteps the question entirely.** A file may declare a *decimal* format
and write literal decimal points (`X177.5Y-48.0`); there is then no suppression question and no digit
count to infer, and this is what a modern export does. Detect it and take it, rather than forcing every
file through an inference it does not need. Where it is absent: With one integer and five decimal digits, `X05` is `0.50000` under trailing suppression
and `0.00005` under leading suppression. The inference must state which convention it chose and on what
evidence, and the choice must be overridable independently of the units — they are two separate
unknowns and a file can make one clear while leaving the other open.

**R-L4f-3. Binary drill files exist, are not text, and must be refused by name.** Some toolchains emit a
binary EIA-coded drill file under the same extension as an ASCII one. Detect it (non-printable bytes in
the first block) and refuse with a sentence that says so and says what to look for instead — never parse
it into garbage coordinates. A drill **listing** or **report** that sits alongside is human-readable
prose, not a drill file; L4g's classifier (R-L4g-1) must not hand it to this reader, and this reader must
not accept it if it does.

## 3. Tools, plating, slots

**R-L4f-4. The tool table is where multiple drill sizes come from, and they cost nothing extra.**
`T<n>C<diameter>` may appear in an `M48` header or inline in the body; both forms are in use. Each tool
is a diameter, hits reference a tool modally, and a file with six tools is no harder than a file with
one. L4c's writer already dedupes tools by diameter on the way out, so the two sides already agree on
the shape of this data.

Tool diameters must land on exact DBU by the same table as L4e's R-L4e-2: an inch diameter at ≤5
decimals and a millimetre diameter at ≤6 are exact; beyond that, round and report.

**R-L4f-5. Plated / non-plated arrives in three different spellings, and the newest one declares far
more than plating.** A `;TYPE=PLATED` / `;TYPE=NON_PLATED` split between tool definitions; two separate
files distinguished only by name; or — in a modern export — **X2 attributes smuggled through Excellon
comments** with a `; #@!` marker:

```
; #@! TF.FileFunction,Plated,1,4,PTH
; #@! TA.AperFunction,Plated,PTH,ViaDrill
T1C0.025
; #@! TA.AperFunction,Plated,PTH,ComponentDrill
T3C1.905
```

Parse the `; #@!` form as attributes, not as comments. It states the plating, **the layer span**
(R-L4f-6) and **what each tool is for** (R-L4f-10) — three facts that otherwise have to be inferred or
cannot be recovered at all. Carry all of it, and let L4g put plated and non-plated hits on **different
drill layers**.

State the asymmetry plainly rather than quietly: **L4c's writer emits a single plated file** and says so
in its own header, because `InterchangeMapping` carries no plated/non-plated field to split on. So this
distinction is **import-only** in this phase — it survives into the design and is then flattened again on
export. Adding the field to `InterchangeMapping` is additive and cheap, but it is a `.ctech` change and
it belongs in whichever phase can show it is needed, not here on speculation.

**R-L4f-6. The layer span is declared, and it is how blind and buried vias arrive.**
`TF.FileFunction,Plated,<from>,<to>,PTH|Blind|Buried` names the two copper layers a drill file's holes
connect — `Plated,1,4,PTH` spans the whole board, `Plated,1,2,Blind` does not. A production set
therefore holds **several drill files, one per layer pair**, and reading only the one named like the
board loses every blind and buried via silently.

Map the span onto `ViaShape.Layer` (barrel) and `ViaShape.LandingLayer` (pad) through L4g's resolved
copper layers. Blind and buried vias are a stated limitation of board import (L4d); here they are
*declared*, so recovering them costs only the parsing. Where no span is declared, assume through-hole
and say so.

**R-L4f-7. A slot is a stroked path, not a hole.** Routed slots appear as a tool-down / move / tool-up
sequence (`G00` position, `M15` down, `G01` move, `M16` up) or as the canned `G85` form. A slot becomes a
`PathShape` on the drill layer with `Width` = the tool diameter and `End = Round`. Reading a slot as two
independent hits is a specific, plausible-looking wrong answer — it produces two holes where the board
has one opening — so a slot fixture is not optional.

**R-L4f-8. The remaining syntax that must parse:** `M48` header start and `%` / `M95` header end; `G90`
absolute and `G91` incremental; `;` comments anywhere; the repeat form (`R<n>` with a step) where
present; `M30` end of file. Unknown commands are reported by name, once, with a count — the same rule as
everywhere else in this series.

## 4. Rebuilding vias

**R-L4f-9. Pair each drill hit with a copper flash at the same coordinate; the pair becomes a
`ViaShape`.** `PadSize` = the flashed aperture's diameter, `DrillSize` = the tool's diameter,
`Layer` = the barrel layer, `LandingLayer` = the pad layer. `ViaShape`'s own doc comment states what
getting those two backwards produces — a plausible-looking export with copper where the hole should be —
so L4d's R-L4d-10 discipline applies verbatim: **prove the orientation by exporting and comparing, never
by reading the two fields back.**

**Pair on exact coordinate equality in DBU, not on a tolerance.** L4c writes the pad flash and the drill
hit from the same X/Y, so exactness is achievable and it is the correct criterion. A tolerance would pair
a via with a neighbouring pad on a fine-pitch part, and it would do so more often on exactly the dense
boards where it matters.

**R-L4f-10. Whether a hole is a via or a component hole is DECLARED when X2 attributes are present,
and only then is it a judgement call.** `TA.AperFunction,Plated,PTH,ViaDrill` versus `…,ComponentDrill`
says outright which tool drills vias, and the artwork side says the same thing from the other direction
with `%TA.AperFunction,ViaPad` on the flash. Where both are present the pairing is a lookup, not an
inference, and it should be one — a heuristic that overrides a declaration is a bug.

Where they are absent, the two are **genuinely indistinguishable from artwork alone**: both are a plated
hole with copper landing on it on every layer. Reconstruct both as `ViaShape` in that case, which is
structurally what they are and is what the EM path needs, and say in the summary that the distinction was
not available rather than implying one the files do not contain.

**R-L4f-11. An unpaired hit becomes a `CircleShape` on the drill layer.** That is precisely the shape
L4c's export already recognizes and drills for (its R-via-5: a bare circle on a drill-function layer is
how a via is genuinely drawn for MMIC), so an unpaired hit survives a re-export as a hole rather than
vanishing. Count them and report the number — an import with many unpaired hits usually means a drill
file and an artwork set that do not belong together, and that is worth saying out loud.

**R-L4f-12. A hit with no drill layer to land on is a refusal, not a silent drop.** If L4g could not
establish a drill layer, say so; do not scatter circles onto whatever layer is nearest.

## 5. Scope guardrails

- **Drill reading and via pairing only.** No file-set discovery, no layer identity, no technology, no
  cell writing — all L4g.
- No prompt, no dialog, no menu — L4h. This phase returns the inference and its evidence; it does not
  ask anyone anything.
- No change to `InterchangeMapping` (see R-L4f-5) and no change to L4c's writer.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 6. Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); documented gate green; no existing test regresses.
2. **Format inference (R-L4f-1)** — a file with a full `M48`/`INCH,TZ`/`FILE_FORMAT` header parses
   directly; a file with **no header at all** yields an inference plus its evidence, and the evidence
   names which sources it used.
3. **The extents cross-check (R-L4f-1)** — a deliberately wrong format is caught by comparing the hits
   against an artwork bounding box, and the report states the disagreement as a number.
4. **Zero suppression (R-L4f-2)** — the same coordinate text under leading and under trailing
   suppression yields the two different, correct positions; the chosen convention is stated on the
   result.
5. **Binary refusal (R-L4f-3)** — a binary drill file is refused by name; nothing is imported from it;
   a drill listing/report is likewise not accepted as a drill file.
6. **Multiple tools (R-L4f-4)** — a fixture with at least three distinct diameters yields three tools,
   each hit carrying its own diameter. A single-tool fixture cannot fail this test and is not sufficient.
7. **Exact diameters (R-L4f-4)** — an inch tool at 5 decimals lands on exact DBU; the negative-coordinate
   case from L4e's R-L4e-2 is retested here because drill coordinates commonly go negative.
8. **Plated split (R-L4f-5)** — all three spellings work: `;TYPE=` sections, two separate files, and
   `; #@! TF.FileFunction,…` attribute comments. The summary states that export will flatten the
   distinction again.
9. **Layer span (R-L4f-6)** — a set of per-layer-pair drill files yields through, blind **and** buried
   vias with the correct barrel/landing layers; importing only the through-hole file loses the others,
   and the test asserts that it does not happen.
10. **Slots (R-L4f-7)** — a routed slot yields **one** `PathShape` of the tool's width, not two hits.
11. **Via pairing (R-L4f-9)** — a design exported by L4c re-imports with its vias as `ViaShape`s of the
    original `PadSize` and `DrillSize`; barrel and landing layers are proven by re-export comparison,
    not by reading the fields.
12. **Exact pairing (R-L4f-9)** — a fixture with a via 100 µm from an unrelated pad pairs the via and
    leaves the pad alone; a tolerance-based implementation fails this test.
13. **Declared beats inferred (R-L4f-10)** — with `ViaDrill`/`ComponentDrill` present, the declaration
    decides; a fixture whose geometry would fool a heuristic is classified correctly anyway.
14. **Unpaired hits (R-L4f-11)** — an artwork set with no matching flash yields circles on the drill
    layer, counted and reported, and those circles re-export as drill hits.
15. **No drill layer (R-L4f-12)** — refuses by name rather than placing circles arbitrarily.
16. **Counters only** — no wall-clock assertion anywhere.

**Fixtures** follow L4e's rule: author them where possible; anything committed under `testdata/` that
this phase did not author must be redistributable and must not name a vendor, tool or product.

## 7. On completion

Write a **"Phase L4f — COMPLETE"** entry at the top of `src/Ui/RESOLVED.md` — **not** `CLAUDE.md`.
Call out:

1. **How often the format inference actually had to guess**, across whatever fixtures were available,
   and which evidence source settled it each time. This is the number that says whether R-L4h-6's prompt
   is a rare escape hatch or the normal path.
2. **The via orientation proof** — how barrel-vs-landing was demonstrated, and what a wrong answer looked
   like when tried deliberately.
3. **How often the via/component distinction was actually declared** (R-L4f-10) rather than inferred,
   and what the fallback got wrong when it had to guess.
4. **Blind and buried vias** (R-L4f-6): whether the declared span mapped cleanly onto `ViaShape`'s two
   layer fields, or whether that model needs something it does not have.
5. **Whether `InterchangeMapping` should gain a plated/non-plated field**, and on what evidence — not as
   a preference.
6. **Stated limitations**: every construct refused by name, and every one skipped and counted.

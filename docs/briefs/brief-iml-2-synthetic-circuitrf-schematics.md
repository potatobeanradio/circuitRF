# Brief IML-2 — Generated pictures of circuitRF's own schematics

**Series:** `brief-iml-0-overview.md` (§1 item 1, L1, L4, L9, L10) · **Tag:** `R-iml2-<m>`
**Depends on:** IML-1
**Area:** `tools/SchematicSynth/` (new C# console + library, references `src/Design` and `src/Render`, listed in
`circuitrf.slnx`), possibly a small read-only measuring hook in `src/Render` (R-iml2-4), `tools/schematic-ml/README.md`,
design note §4

---

## 1. Goal

The owner's starting set has **no** pictures of circuitRF's own schematics, yet a circuitRF screenshot is among the
most common things a user will paste. Generate **tens of thousands** of them — random but realistic circuits built from
circuitRF's real component catalogue, drawn by **circuitRF's own renderer** — each with an **exact** label, because the
generator knows every glyph, pin, wire and word it drew.

## 2. Requirements

**R-iml2-1 — Random circuits that look designed.** `CircuitGenerator.Next(Random)` builds a `TestBench` from a weighted
set of topology recipes, each drawn from what RF schematics actually contain:
- ladders (LC low/high/band-pass, 2–9 sections), pi and tee pads, L-sections;
- matching networks (series/shunt L and C, stubs, quarter-wave lines) between two ports;
- transmission-line networks: lines, tees, crosses, bends, steps, open and short stubs, vias;
- an amplifier stage: a BJT or FET with bias resistors, RF chokes, DC blocks, a supply, decoupling to ground;
- a block-level chain: System `Amp`, `Filter`, `Atten`, `Switch`, `Circulator`, `Coupler`, `Balun`, `Mixer`;
- an S-parameter block (`SnP`, 1–4 ports) with surrounding parts;
- free mixtures of the above (two to four recipes joined).
Values come from realistic ranges with circuitRF's own spellings (`2.62 pF`, `L=0.56 nH`, `50 Ohm`, `W=0.5 mm`),
designators numbered as the editor numbers them, some parameters shown and some hidden as a user would leave them.
Every class in `classes.json` that maps to a circuitRF kind appears in at least 1 % of generated pictures.

**R-iml2-2 — Drawn as a user draws them.** Two placement modes, mixed: **Tidy** (`NetlistSchematic.Build`) and
**hand-placed** — parts at random grid positions and turns, wired with `SchematicAutoRouter`, net labels on some nets,
junction dots where the editor puts them, a few deliberate non-connecting crossings. The repo's own `.csch` files
(`examples/`, test fixtures) are a third source: each is rendered as it is, then perturbed (parts moved one grid step,
values re-drawn), so real authored layouts are in the mix.

**R-iml2-3 — Drawn by the renderer the app draws with.** Pictures come from `CircuitRF.Render`'s `SchematicRenderer` —
never a second renderer — with randomised view state:
- every shipped colour theme (light and dark), with the canvas grid on and off, dots and lines;
- zoom so a resistor body spans **12–300 px** (log-uniform), the view origin random, crops that cut parts at the edge;
- selection highlights, hover outlines and a placed bitmap underlay in a small share of pictures;
- **screenshot conditions**: window chrome strips (a toolbar band, a status bar, a scroll bar — drawn generically, no
  real app's artwork), Retina 2× and 1×, downscale by a random factor with a random filter, JPEG at quality 40–95,
  slight blur, PNG palette reduction.

**R-iml2-4 — Exact labels.** For each picture, a `.mllabel.json` (IML-1 schema) and three masks:
- **parts:** the body box from the glyph geometry `BuiltInSymbols.Primitives` gives the renderer, through the
  instance transform and the view transform; the **body** excludes straight leads that end on a pin (computed once per
  kind and cached — this same "body" is what IML-8 measures sizes with, so define it in `src/Design` as
  `GlyphBody.Of(kind, portCount)`, with a test); pins from the symbol's port positions;
- **wires:** every wire segment as drawn, with the pen width at that zoom; junction dots; crossings with no dot;
- **texts:** every designator, value, net label, port name and pin number, with its quad and string. The quad comes
  from the renderer's own text measurement — if `src/Render` does not expose placed text rectangles, add a read-only
  measuring call there (a list of `(string, quad, role)` for a model and a view), used by the renderer's own drawing
  path so the two cannot disagree;
- **masks** by rendering **filtered copies of the model** (wires only; symbols only with labels hidden; labels only),
  thresholded against the background — pixel-exact, no hand-drawn approximation, and no change to how the app draws.

**R-iml2-5 — The command.** `dotnet run --project tools/SchematicSynth -- crf --count 30000 --seed 1 --out
$CRF_ML_DATA/synth/crf/batch-001` — deterministic for a seed (same seed, same bytes), parallel across cores, resumable
(skips what exists), and prints a class histogram at the end. A `--preview 24` option writes a contact sheet PNG with
the labels drawn over the pictures, for a human to look at before training on them.

**R-iml2-6 — Kept honest.** Every picture's `.cnl` (the generator's `TestBench`) is written beside it as netlist truth.
A sample of generated pictures is round-tripped through the **existing** extraction (`NetExtractor` on the generated
`.csch`) and must give the same netlist as the `.cnl` — that is what proves the drawing is the circuit.

## 3. Gates

- `tests/Ui.Tests/Vision/SchematicSynthTests` (fast, no data): three seeds generate, each label validates against the
  schema rules (a C# port of the structural checks — or the generator's own validator), every body box lies inside
  the picture or is marked cut, every pin is on ink in the symbol mask, and the drawing's extracted netlist equals the
  `.cnl`. Under 3 s.
- `GlyphBodyTests` — for R, L, C, ground, Term, a source, a BJT: the body excludes the leads and its long side matches
  the drawn glyph measured from a render.
- The owner sees one `--preview` contact sheet (sent as a file) before IML-6 trains on the batch.

## 4. Not in this phase

Other editors' conventions (IML-3). Training. Any change to how the application draws a schematic.

## 5. Traps

- **The renderer's caches are shared** (`TechnologyCache` taught this for layouts): a filtered copy of a model must not
  leave a cache narrowed for the next picture. Render each pass from its own model instance.
- **Hidden parameters are not text.** A value the instance does not show must not appear in `texts`.
- **A crop that cuts a part** marks it `"cut": true`; the trainer still learns from it, IML-6 does not score it.

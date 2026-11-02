# Brief IML-1 — The data home, the label format, the class list

**Series:** `brief-iml-0-overview.md` (L1, L2, L8, L9, L11) · **Tag:** `R-iml1-<m>`
**Depends on:** —
**Area:** `tools/schematic-ml/` (new: `README.md`, `classes.json`, `schema/mllabel.schema.json`, `crfml/` package with
`data.py`, `importer.py`, `validate.py`, `requirements.txt`), `docs/design/schematic-vision.md` (new, §1–§3),
`.gitignore`

---

## 1. Goal

Before anything is generated, labelled or trained, fix **where the data lives, what a label is, and what a part
class is** — the three contracts every later phase reads. Get the owner's starting set into the data home under
neutral names with a test split that never moves.

## 2. Requirements

**R-iml1-1 — The data home.** `$CRF_ML_DATA` (default `~/circuitRF-ml-data`; every tool takes `--data` too). Layout:

```
$CRF_ML_DATA/
  manifest.json                 every real picture: id, family, sha256, size, split, imported-from (local only)
  real/<family>/<id>.<ext>      the picture, renamed; <id>.mllabel.json beside it once labelled (IML-4)
  synth/<generator>/<batch>/    generated pictures + labels (IML-2/3) — regenerable, safe to delete
  fonts/                        open-licensed fonts for generated text, with LICENSES/ (IML-3)
  runs/<run-id>/                checkpoints, logs, metrics.json (IML-6)
  exports/<model>-<version>/    .onnx, .crfnn, probe, model card draft (IML-6)
```

Nothing under it is ever committed (L1). A `.gitignore` line for `**/circuitRF-ml-data/` guards the case of someone
pointing it inside the repo, and every tool **refuses** a `$CRF_ML_DATA` inside the repository's working tree.

**R-iml1-2 — The importer.** `python -m crfml.importer <folder> [--family <id>]` walks a folder recursively and
imports every picture (PNG, JPEG, WebP, GIF first frame, BMP):
- each **top-level sub-folder becomes one family** (`family-a`, `family-b`… in order of first import; a later import of
  a new folder takes the next letter), and loose pictures at the top level become their own family; the original
  folder name is written only to `manifest.json` (L2);
- each picture gets id `<family>-<first 10 hex of sha256>`; the original **file** name is not kept anywhere — many name
  a tool or a part;
- an identical picture (same hash) imported twice is one picture;
- WebP and GIF are decoded and stored as PNG; JPEG is kept as JPEG (its artefacts are training signal);
- the split is decided **once**, at import: a picture is `test` when `int(sha256[:8], 16) % 4 == 0`, re-balanced so
  each family has at least 2 test pictures; otherwise `train` (with `val` drawn from train by the trainer, never from
  test). The split is written to the manifest and **never recomputed** (L8).

Also `--from-corpus testdata/image-sources` imports the datasheet/web corpus as family `corpus-ds` / `corpus-web`
with every picture forced to `test` and their existing `answer.cnl` copied beside them as netlist truth.

**R-iml1-3 — The class list.** `tools/schematic-ml/classes.json` — the single source the generator, the trainer and
the exported models share (the exported model carries a copy; IML-7's mapping test reads it). Each class:

```json
{ "name": "resistor", "group": "two-pin", "symmetry": "c2",
  "pins": [ {"name": "1", "at": [0.5, 0.0]}, {"name": "2", "at": [0.5, 1.0]} ],
  "circuitrf": { "kind": "Resistor" }, "sizeReference": true }
```

- `pins` are in the class's **canonical orientation**, normalised to its body box (x, y in 0…1, may sit outside the
  box for leads); multi-pin boxes (`ic-box`, `block`) declare `"pins": "perimeter"`;
- `symmetry` (`none`, `c2`, `c4`, `mirror`, `c2+mirror`) says which orientations are the same drawing, so the trainer
  does not punish a resistor read upside down;
- `circuitrf` maps to circuitRF's emit (`SymbolKind`, a variant, or `"ignore"` for things that are drawn but not
  circuit — analysis boxes, probes, title blocks);
- `sizeReference` says whether the class's body may set the picture's scale (IML-8): true for R, L, C, diode, ground,
  sources; false for boxes whose size depends on pin count.

The file also holds the text network's `charset` (IML-6 R-iml6-2), so the generator never draws a character the
reader cannot emit.

The **v1 list** (the implementer may split or merge with a note in the design note, never silently):
resistor, capacitor, capacitor-polarised, inductor, ground, terminal (circle/dot/flag/arrow end of a net),
power-port (supply arrow/bar naming a rail), port (a port symbol with an impedance — the box-with-source kind),
source-ac, source-dc (incl. battery), source-current, diode (incl. LED, zener and varactor as `variant`), bjt-npn,
bjt-pnp, fet-n, fet-p, amplifier (triangle, incl. op-amp), ic-box, block (a labelled box with ports — an SnP),
tline (ideal or coax line), mlin (a planar line box), mtee, mcross, mbend, mstep (step/taper), stub-open, stub-short,
via, transformer, attenuator, filter, circulator, switch, coupler, balun, mixer, probe (ignore), controller (ignore —
an analysis, variable or simulation box), no-connect.

**R-iml1-4 — The label format.** `schema/mllabel.schema.json` (JSON Schema), version 1, one file per picture, all
coordinates in the picture's own pixels, origin top-left:

```json
{ "version": 1, "image": "family-b-3f2a9c01de.jpg", "width": 1373, "height": 661,
  "origin": "real", "family": "family-b", "style": null,
  "parts": [ { "id": "p1", "class": "resistor", "orientation": {"turn": 90, "mirror": false},
               "body": [x0, y0, x1, y1], "pins": [ {"name": "1", "x": 0, "y": 0}, … ],
               "designator": "t3", "values": ["t4"], "params": {"R": "28.495"} } ],
  "wires": [ { "points": [[x, y], …], "pen": 2.5 } ],
  "junctions": [[x, y]], "crossings": [[x, y]],
  "texts": [ { "id": "t3", "quad": [[x, y] ×4], "string": "R1", "role": "designator" } ],
  "annotations": [ { "box": [x0, y0, x1, y1], "kind": "arrow|frame|chrome|title|other" } ],
  "ignore": [ [x0, y0, x1, y1] ],
  "masks": { "wire": "…wire.png", "symbol": "…symbol.png", "text": "…text.png" } }
```

A part may carry `"cut": true` (the picture's edge cuts it) and `"variant"` (e.g. `zener`, `led`). `texts[].role` is one of `designator`, `value`, `net`, `port`, `pin-number`, `pin-name`, `title`, `other`. `masks` is
present for generated pictures (exact, IML-2/3) and absent for real ones (IML-6 derives them). `ignore` regions are
neither trained on nor scored. Netlist truth for scoring is a `<id>.cnl` beside the label (IML-4 derives it).

**R-iml1-5 — The validator.** `python -m crfml.validate <path…>` checks a label against the schema **and** against
sense: every class is in `classes.json`; pin count matches the class (or ≥ 2 on the perimeter for boxes); every
pin lies within a pen width of ink, or of a wire end; every wire end reaches a pin, a junction, another wire or the
picture edge (else a warning); every referenced text id exists; every `values` string parses through the same grammar
rules as `ValueGrammar` (a Python port of its unit table, checked against the C# one by a C# test reading a shared
`tools/schematic-ml/units.json`). Exit 0/1, one line per finding.

**R-iml1-6 — The handbook and the design note.** `tools/schematic-ml/README.md` opens with: what the kit is for, the
data rule (L1, L2), setting up Python (version pinned; `requirements.txt` pinned to exact versions), `$CRF_ML_DATA`,
and the import command. `docs/design/schematic-vision.md` §1–§3: what the system is (overview §2), the data home, the
label format and the class list, and why each is shaped as it is.

## 3. Gates

- `tests/Ui.Tests/Vision/ClassListTests` — `classes.json` parses; names are unique kebab-case; every non-`ignore`
  class maps to a `SymbolKind` that exists; every pin list matches that kind's pin count where it is fixed.
- `tests/Ui.Tests/Vision/UnitsTableTests` — `units.json` agrees with `ValueGrammar`'s units.
- A Python self-test (`python -m crfml.selftest`) over three tiny labels in `tools/schematic-ml/tests/fixtures/` drawn
  by our own code — **not** part of `dotnet test` (L9); the handbook says when to run it.
- The importer run once on the owner's starting set: the manifest lists every picture, the split per family is
  printed, and `git status` is clean afterwards.

## 4. Not in this phase

Generating, labelling, training. Any C# beyond the two small tests.

## 5. Traps

- **A file name is data.** Do not log original file names to the console in a way that ends up pasted into a brief or
  a RESOLVED.md entry (L2).
- **The split must not depend on import order or machine** — hash-based, then frozen in the manifest.

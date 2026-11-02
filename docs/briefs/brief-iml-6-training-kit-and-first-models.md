# Brief IML-6 — The training kit, the two networks, and the checkpoint

**Series:** `brief-iml-0-overview.md` (§2, §4, L4, L7, L8, L11, O4) · **Tag:** `R-iml6-<m>`
**Depends on:** IML-2, IML-3 (generated data), IML-4 (real labels), IML-5 (the runtime and the exporter's operator list)
**Area:** `tools/schematic-ml/crfml/` (`dataset.py`, `augment.py`, `models/perception.py`, `models/text.py`,
`losses.py`, `train.py`, `evaluate.py`, `export.py`, `ship.py`), `src/Vision/Models/` (the first real weights +
`MODEL_CARD.md`), `tests/Ui.Tests/ImageRecognition/FieldScore.cs` (scores read from labels), design note §8

---

## 1. Goal

A training kit any circuitRF developer can run from the handbook, two networks trained with it, measured on pictures
they never saw, exported to `.crfnn` — and then **stop and show the owner the numbers** (L7).

## 2. Requirements

**R-iml6-1 — The perception network.** `models/perception.py`, written here from scratch (L4), within IML-5's
operators. A compact encoder–decoder: a residual convolutional encoder to stride 32 (depthwise-separable blocks,
**≤ 2.5 M parameters**, O2), a feature-pyramid / U-Net decoder back to stride 2, and heads:
- **parts** (stride 4, centre-point detection): a heat map per class; body width and height; centre offset; an
  orientation head over 8 classes (4 turns × mirror) with the loss taken over each class's `symmetry` equivalence set;
- **pins** (stride 4): one pin-point heat map + sub-cell offset;
- **masks** (stride 2, upsampled ×2 at assembly): background / wire / symbol ink / text / annotation;
- **junctions** (stride 4): a connecting-dot heat map and a non-connecting-crossing heat map;
- **text lines** (stride 4): a line-region map and a 3-way reading direction (left-to-right, rotated up, rotated
  down);
- **picture** (global, pass 1 only): schematic / not-a-schematic, and the log of the median part body size in pixels.
Input RGB normalised; trained on crops of 512–768 px at the **canonical scale** (median part body ≈ 48 px, ±35 %
jitter), plus whole pictures downscaled to ≤ 768 px for the picture head.

**R-iml6-2 — The text network.** `models/text.py`: a convolutional recogniser (no recurrent layer) over a line crop
normalised to 32 px tall, width up to 512, CTC-decoded over the charset in `classes.json`'s `charset` field (Latin
letters, digits, `. , = + − - _ ( ) [ ] { } / : % ' ? # * < > ±`, `Ω µ μ ° ²`, space). **≤ 0.75 M parameters** (O2). Trained on
line crops from IML-2/3 and real labels, plus pure text lines rendered with every fetched font (values, designators,
net names drawn from the same grammar as the generator).

**R-iml6-3 — Data loading.** `dataset.py` reads labels and pictures from `$CRF_ML_DATA`, derives masks for real
pictures (wire polylines rasterised at the labelled pen width ∩ ink; symbol ink = ink inside part bodies not in the
wire or text masks; text = ink inside text quads), and **refuses any picture whose manifest split is `test`** — the
refusal is in the loader, not in a convention (L8). `augment.py`: colour inversion and hue shifts, pen thickening and
thinning by morphology, blur, JPEG, scale jitter, crop with part-cut marking, small rotation (≤ 2°) — all applied to
labels consistently.

**R-iml6-4 — The schedule.** `train.py --config configs/perception-v1.yaml` (configs committed): pre-train on
generated data (IML-2 + IML-3), then fine-tune on a mix weighted toward real training pictures (each real picture
seen many times under augmentation). Seeds fixed, the config and the git commit written into the run directory,
checkpoints every epoch, resumable, Metal backend on Apple silicon (O4), CUDA if present, CPU as a slow fallback.
The handbook gives the expected wall-clock on the owner's machine once measured.

**R-iml6-5 — Evaluation, two levels.**
- `evaluate.py` (Python, per run): part F1 by class at IoU 0.5 and pin-within-a-pen; mask IoU per class; junction F1;
  text character error rate and exact-line rate per role; the picture head's accuracy and the scale error. Reported on
  real **val** during training and on real **test** at the end, per family and per style pack.
- **End to end, in C#** (the score that matters, §4): once IML-7 exists this is `FieldScore`; before it, a minimal
  assembly in Python (`evaluate.py --e2e`) turns model outputs into a netlist with the same rules as IML-4's
  `label_to_cnl` and compares topology with the truth `.cnl`. `FieldScore` gains a mode that reads its expectation
  from a `.mllabel.json` + `.cnl`, so IML-7 onward scores the data home with no hand-written `expected.json`.
- **Leave one family out:** one extra run trained without each real family (not shipped) measures how well the
  models generalise to an editor they have never seen. Reported, not gated.

**R-iml6-6 — Export and ship.** `export.py` → ONNX → `.crfnn` with probe and metadata (IML-5). `ship.py <export>`
copies the `.crfnn` files into `src/Vision/Models/`, writes `MODEL_CARD.md` (version, date, architecture summary,
parameter counts, data by family id and count, the training-code commit, every number from R-iml6-5, known
weaknesses), refuses to ship over the recorded size budget (a larger model waits for the owner's approval, O2), and refuses to
ship unless the test numbers beat the currently shipped model's by O2's margin (L11, O2) — weights
change rarely, and every version stays in the history.

**R-iml6-7 — The checkpoint (L7).** After the first full training, write design note §8 with every number from
R-iml6-5 against overview §4, the per-family table, the leave-one-family-out table, a size table (O2: the perception net
at half and at the shipped width, so the owner sees what the size costs), the five worst pictures with
their overlays (as PNG files sent to the owner, not committed), and a recommendation: continue to IML-7, add data,
or change course. **Then stop and wait for the owner.**

## 3. Gates

- `python -m crfml.selftest` (handbook): one training step of each network on the three committed tiny fixtures, an
  export, and the export's probe re-run in PyTorch — minutes, CPU, no data home.
- `tests/Vision.Tests`: the shipped models' probes (IML-5's parity test now on real weights); both models together
  within the budget `MODEL_CARD.md` records (6 MB until the owner approves more — O2).
- The checkpoint report.

## 4. Not in this phase

Wiring the models into the reader (IML-7). Changing circuitRF's behaviour. Training on any `test` picture.

## 5. Traps

- **Do not tune on the test split.** Hyper-parameters and the stopping epoch come from val. The test numbers are read
  once per shipped version.
- **A class with almost no real examples** (a mixer, a balun) is learned from generated pictures only; report its
  real count beside its score so a 1-of-1 is not read as 100 %.
- **The scale head is the reader's first decision** — a wrong scale makes pass 2 see parts at the wrong size. Report
  its error distribution, not just its mean.

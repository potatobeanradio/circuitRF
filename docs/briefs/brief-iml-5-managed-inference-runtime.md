# Brief IML-5 — `src/Vision`: running the networks in managed code

**Series:** `brief-iml-0-overview.md` (L3, O1, O2, L9, L11) · **Tag:** `R-iml5-<m>`
**Depends on:** IML-1 (class list carried by the model). Independent of training: built and tested against small
networks exported by a test script.
**Area:** `src/Vision/` (new project `CircuitRF.Vision`, in `circuitrf.slnx`), `tests/Vision.Tests/` (new),
`tests/Firewall.Tests` (the new project in the no-UI list), `tools/schematic-ml/crfml/export.py` (the `.crfnn` writer),
`tools/CliSmoke`, design note §7

**O1 settled:** this is the engine circuitRF ships; ONNX Runtime is not used.

---

## 1. Goal

Run the two trained networks inside circuitRF on every shipped platform, with no native dependency, fast enough for
the paste time budget **on the owner's Debug build**, and give exactly the outputs PyTorch gives (to float tolerance).

## 2. Requirements

**R-iml5-1 — The project.** `src/Vision/CircuitRF.Vision.csproj`: references nothing in this repo and no UI framework
(Firewall). **`<Optimize>true</Optimize>` in every configuration** — the owner runs Debug, and unoptimised SIMD loops
are 5–10× slower. `src/Design` references it. Public surface: `VisionModel.Load(Stream)`, `VisionModel.Run(inputs) →
outputs`, `Tensor` (float32, NCHW, pooled buffers), `ShippedModels.Perception` / `ShippedModels.Text` (lazy, loaded
once from embedded resources, thread-safe).

**R-iml5-2 — The `.crfnn` container.** Written by `export.py` from the exported ONNX graph, read by `VisionModel`:
a small header (magic, format version), a JSON section (model name, version, training-code commit, data manifest hash,
`classes.json` copy, input normalisation, output names and meanings, the measured numbers — L11), the graph as a
topologically ordered operator list with attributes, the weights as float16 (O2) with per-tensor offsets, and a
**probe**: one small input and its PyTorch outputs. The exporter folds batch norm into convolutions, removes training
ops, and **refuses** an operator not in R-iml5-3's list, naming it — so a network change that needs a new operator
fails at export, not in circuitRF.

**R-iml5-3 — The operators.** Exactly what the IML-6 architectures need, nothing speculative: `Conv` (any kernel,
stride, padding, dilation, groups incl. depthwise), `ConvTranspose` or `Resize` (nearest and bilinear, ×2),
`Relu`, `Sigmoid`, `HardSwish`/`SiLU`, `Add`, `Mul`, `Concat`, `MaxPool`, `GlobalAveragePool`, `Gemm`/`MatMul`,
`Reshape`/`Flatten`/`Transpose`/`Squeeze`/`Unsqueeze`, `Softmax`, `Slice`. IML-6 keeps its architectures inside this
list (no recurrent layers — the text net is convolutional, decoded with CTC).

**R-iml5-4 — Fast enough.** Convolution as tiled im2col + a blocked SIMD GEMM on `System.Numerics.Vector<float>`
(or `System.Runtime.Intrinsics` with a portable fallback), depthwise as its own kernel, parallel over output channel
blocks with a **fixed partition that does not depend on core count** (so results are identical run to run on one
machine). Buffers pooled; no allocation per pixel. Target, measured once from a scratch harness and written in the
design note (no timing test — L9): IML-6's perception net on a 1024 × 1024 input in **≤ 0.6 s** and the text net on
40 lines in **≤ 0.3 s**, Debug build, on the owner's machine.

**R-iml5-5 — Parity.** For every shipped model, running its embedded probe reproduces the stored outputs to
**max |Δ| ≤ 1e-3** (relative to output scale). Per-operator reference cases (tiny tensors computed by PyTorch,
committed as JSON under `tests/Vision.Tests/Cases/`, written by `tools/schematic-ml/crfml/opcases.py`) cover every
operator and attribute combination the exporter allows.

**R-iml5-6 — Shipped and found.** The two `.crfnn` files are embedded resources of `CircuitRF.Vision`. Until IML-6
ships real ones, a **tiny placeholder model** (a few kilobytes, random weights, correct input/output shapes) is
embedded so everything above it can be built and tested; `ShippedModels.IsPlaceholder` lets IML-7 refuse to read
pictures with a placeholder ("This build has no trained schematic reader."). `tools/CliSmoke` gains a check that the
published CLI loads both models and runs their probes — a trimmed or mis-packaged build is caught by the packaging
scripts, on every platform (x86 Windows included: check its memory use).

## 3. Gates

- `tests/Vision.Tests` (new, in the slnx, < 5 s): per-operator cases, the placeholder's probe, a determinism case
  (same input twice → bitwise the same output), buffer-pool reuse.
- `tests/Firewall.Tests`: `CircuitRF.Vision` references no UI framework.
- The CliSmoke model check passes on the macOS publish tree locally; the Windows/Linux runs happen with the next
  packaging run and are noted in the design note.

## 4. Not in this phase

Training (IML-6). Any reading of pictures (IML-7). GPU inference.

## 5. Traps

- **Float16 storage, float32 compute.** Convert once at load; never compute in half precision.
- **Vector width differs by machine** (8 floats on one, 4 on another) and summation order with it; results differ in
  the last bits across machines. Tests compare with tolerance; IML-7's assembly must not hinge on the last bit (its
  thresholds have margin).

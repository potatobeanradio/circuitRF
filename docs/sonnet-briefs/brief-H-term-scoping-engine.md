# Brief H — Term scoping + engine stamping refinement (+ linter)

**Scope:** make Terms behave correctly across analyses and hierarchy: (1) a Term is an S-param port
**only at the analysis's top schematic** — a Term inside an instantiated sub-cell is inert + warned;
(2) the engine stamps the Term/Port branch **only in driven (S-parameter) analyses** — inert in
DC/AC/HB so it doesn't short/load the node; (3) a linter flags misuse. **Read
`docs/design/ports-pins-and-terms.md` (the scoping section) first.** Land **after** Briefs F and G.

This is the riskiest brief (engine + elaboration). Keep changes minimal and well-instrumented.

**Firewall:** all `src/Core` + `src/Engine` — no Avalonia.

---

## Read first (real names)

- `docs/design/ports-pins-and-terms.md` — "The scoping rule" section (the three rules).
- `src/Engine/SParameterEngine.cs` — `StampAll(mna, netlist, omega)` stamps **every** component
  each frequency; `CollectPortsAndBranchLabels` gathers `PortModel`/`TermModel` as ports (reads
  `Num`/`Z`). This is the S-param (driven) path where Terms SHOULD act.
- `src/Core/Devices/PortModel.cs` — `PortModel` + `TermModel`: both `Stamp` a 0 V source between
  `Nodes[0]`/`Nodes[1]` **unconditionally**. In a non-S-param analysis this shorts the port node.
- Other engines that call stamping — grep for `.Stamp(` and `StampAll` across `src/Engine`
  (DC / AC / harmonic-balance). Find how each assembles the MNA; that's where a Term must be inert.
- `src/Core/Elaboration/Elaborator.cs` — `FlattenInstances`. Components carry `InstancePath`
  (dot-path). **Top-level** Terms have a single-segment path (no `.`); **sub-cell** Terms have a
  dotted path. This distinguishes "analysis-top Term" from "buried Term".
- `src/Core/Elaboration/ElaboratedComponent.cs` — has `InstancePath`, `ComponentType`,
  `Parameters`, `Model`. Use `ComponentType`/`Model is TermModel or PortModel` + `InstancePath`.
- `src/Core/Devices/ModelKind.cs` (grep) + how the engine decides analysis type — to gate stamping
  on "is this a driven/S-param analysis".
- `docs/design/linear-engine.md` — §9 port extraction; update it.

---

## Spine (do-not-violate)

1. A Term/Port branch is a **port** (driven 0 V source the engine reads) **only** in the
   S-parameter (driven-port) analysis. In every other analysis it is **inert (open)** — no branch,
   no short, no load. (Optional, behind a flag: present `Z` as a real termination — default OFF.)
2. A Term is recognized as an S-param port **only at the analysis's top schematic**
   (single-segment `InstancePath`). A Term inside an instantiated sub-cell is **inert** and
   **warned** — it must never silently load the circuit.
3. Reusable cells should contain **Pins, never Terms** — the linter enforces this.
4. Don't change the S-param math or the `.cnl`/`Port:` format. Only *when/where* Term branches stamp.

---

## Layer 1 — Term/Port inert outside driven analyses

Today `PortModel.Stamp`/`TermModel.Stamp` always add the 0 V branch. Make stamping
analysis-aware so the branch is added **only** in the S-parameter (driven-port) path:
- Preferred (localized): the **S-parameter engine** is the only place that should treat Term/Port
  as a driven branch. So gate the branch creation on an analysis context. Options:
  - (a) Add an `analysisKind`/`isDrivenPortAnalysis` flag to the stamping context (`IMnaContext`
    or a parameter threaded through `StampAll`); `PortModel`/`TermModel.Stamp` add the branch only
    when set. DC/AC/HB call stamping with the flag false → Term contributes nothing.
  - (b) Have each non-S-param engine **skip** `TermModel`/`PortModel` components when assembling
    (filter them out before `Stamp`), and keep the S-param engine stamping them as today.
  Recommend (b) if the non-S-param engines have their own stamp loops (smaller blast radius, no
  model signature change); use (a) if stamping is centralized. State which and why.
- The S-parameter engine path is unchanged (Terms still become driven ports there).
- **Optional Z-termination (default OFF):** if you add a `TerminateInBias` option, an inert Term may
  instead stamp `Z` as an admittance for bias/DC. Leave OFF in v1; just make Terms inert elsewhere.

**Gate 1:** A DC/HB analysis on a circuit containing a Term does **not** short or load the port node
(the Term contributes nothing). The same circuit's S-parameter analysis is unchanged (Terms drive
the ports). Add a focused test mirroring `tests/Core.Tests/Design/SParameterAnalysisTests.cs`.

---

## Layer 2 — Terms recognized only at the analysis top

In the S-parameter port collection (`CollectPortsAndBranchLabels`) — or at elaboration — restrict
recognized ports to **top-level** Terms/Ports:
- A component is "analysis-top" when its `InstancePath` has **no `.`** (it lives in the testbench
  root frame, not a sub-cell). Only those become S-param ports.
- A `TermModel`/`PortModel` with a **dotted** `InstancePath` (inside an instantiated cell) is:
  - **not** collected as a port, AND
  - **inert** (combined with Layer 1 it never stamps in non-S-param; in S-param it must also not
    stamp a driven branch — so filter it out of `StampAll` for S-param too, or simply never create
    its branch). Net effect: a buried Term is electrically nothing.
  - **warned**: emit a diagnostic (collected on the `ElaboratedNetlist` or surfaced to the UI run
    log) — `"Term '<path>' is inside an instantiated cell and was ignored; use a Pin for cell
    interfaces and place Terms only in the testbench."`

**Gate 2:** A design that (wrongly) instantiates a cell containing a Term runs S-params using only
the top-level Terms; the buried Term neither adds a port nor perturbs results; a warning is emitted.

---

## Layer 3 — Linter

Add lightweight checks (where the run/elaboration validates a design; grep for existing validation
or the run-service diagnostics path):
- **Term in a non-testbench cell** → warning (Terms belong only in testbenches; use Pins).
- **Pin in the top testbench with no parent binding** → info/warning (a top-level Pin is a no-op).
- **Duplicate or missing `Num`** among top-level Terms (gaps, collisions) → warning.
- **S-param analysis with zero top-level Terms** → the existing engine error already covers this;
  ensure the message points users to "place Term components."

Surface these through the existing run-diagnostics/warning channel (don't invent a new one).

**Gate 3:** Each lint condition produces its warning on a crafted design; a clean testbench is silent.

---

## Layer 4 — Docs

Update `docs/design/linear-engine.md` §9 to state: Term/Port branches are driven ports **only** in
the S-parameter analysis and at the analysis top; inert elsewhere; buried Terms are ignored + warned.
Cross-link `ports-pins-and-terms.md`.

**Gate 4:** `linear-engine.md` reflects the new behavior; no contradictory older statements remain.

---

## Acceptance
- Term/Port is a driven port only in S-param analysis; inert (no short/load) in DC/AC/HB. ✅
- Only top-level Terms become S-param ports; buried Terms are inert + warned. ✅
- Linter flags Terms-in-cells, stray top-level Pins, duplicate/missing `Num`. ✅
- S-param results for existing valid testbenches are unchanged; tests pass. ✅
- `linear-engine.md` updated. ✅

## Guardrails
- Do not change S-param math, `Y→S`, or the `.cnl`/`Port:` format — only *when/where* Term branches
  stamp and which Terms count.
- Prefer filtering Terms out of non-S-param assembly over changing model signatures, if it's smaller.
- Keep everything in `src/Core`/`src/Engine` — no Avalonia.
- Add/extend tests; don't regress `SParameterAnalysisTests`.
- Minimal diff; list files touched.

## Scope fence (NOT here)
- No new Term/Pin UI (Briefs F/G). No mixed-mode S-parameters; no Z-as-bias-termination beyond an
  optional OFF-by-default flag.

## Exit / report
State: how you gated stamping (option a/b) and why; how "analysis-top" is determined
(`InstancePath` dot-test); where warnings are surfaced; the new/updated tests; and confirmation the
4 gates run mentally. Call out any place the non-S-param engines made (b) infeasible.

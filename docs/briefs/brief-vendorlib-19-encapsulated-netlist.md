# Brief VL-19 — Encapsulated Netlist: export a cell's linear network as one S-parameter block

**Series:** [Vendor libraries](brief-vendorlib-0-overview.md), model-library part ([VL-6](brief-vendorlib-6-model-library-format.md)) · **Tag:** `R-vl19-m`
**Area:** `src/Design/VendorLibraries/ModelLibrary/` (a new `ModelLibraryExport.Encapsulate.cs` beside
`ModelLibraryExport.Protect.cs`; `Protect/ProtectPartition.cs` and `Protect/ProtectCheck.cs`, reused), the Export
to Library dialog's **Protected model** section and **Frequency** plan, `circuitrf export part` and its MCP entry,
`docs/user/src/reference/protected-models.md`
**Depends on:** 10, 11, 15, 16. Works with 17 (a Model Composer document exports through the same dialog).
**Rule:** overview §0's naming rule holds unchanged. Fixtures are synthetic (`TSTFET100`, `TSTPA200`).

---

## 0. What this adds

Compile netlist (brief 16) protects a cell by compiling it, which needs a Verilog-A compiler on the maker's
machine. It also refuses a table constituent, and it leaves every SnP, transmission line and technology part
outside the module, where they can be seen. **Encapsulated netlist** is a second way to protect a model, and it
needs no compiler. **Every linear element of the cell becomes one N-port S-parameter block.** The nonlinear
devices are placed against that block's ports. What ships is the network's behaviour at its ports over the
frequency plan. Its topology, its element values and its parts list are gone.

Four things already exist and are reused, never copied:
- **The linear/nonlinear split** is the engine's own: the `ModelKind` each component model reports, which is
  what harmonic balance partitions by (`HbLinearExtractor`, `ElaboratedComponent.IsNonlinear`).
- **Sampling a network into an SnP**, including its 0 Hz row: brief 11's conversion
  (`ModelLibraryExport.Convert.cs`), which already turns technology parts into SnPs on a frequency plan.
- **The wrapper and the proof:** brief 16's wrapper writer (the `.csch` drawn from the wrapper `.cnl`) and
  `ProtectCheck`.
- **The frequency plan:** the dialog's existing **Frequency** section, unchanged.

## 1. Decisions

- **D1 — Exactly one block, or none.** Every linear element of the flattened cell goes into one block: sub-cells
  inlined; R, L, C, mutuals, transmission lines, every technology part, and **every SnP the cell already
  places**. An exported model holds **exactly one** S-parameter block, or **none** when the cell has nothing
  linear in it (then the model is its devices, and the dialog says so as a note, not a refusal). It never holds
  two. A linear element that cannot be sampled is a refusal naming it. It is never left outside.
- **D2 — What stays outside is decided by the engine, not a table.** A component whose model reports
  `ModelKind.Nonlinear` stays outside as an instance: a ComposedFET, a Verilog-A device, a diode, a FET model, an
  SDD, an external device. A SPICE subcircuit is flattened first, so its primitives are classified one by one.
  Independent sources and probes also stay outside, because they are not part of the network and an S-parameter
  block cannot carry a source. A model cell rarely holds one, and the dialog lists any it finds. A linear model
  added to circuitRF later goes into the block automatically. Nothing here keeps a list of types.
- **D3 — The ports.** There is one port per net where the block meets an outside component, plus one per cell pin
  the block touches. Ground (node 0) is the reference. A pin that only devices touch needs no port. A thermal net
  is a node like any other: linear elements on it go into the block, so the thermal network is hidden as well.
  Ports are numbered cell pins first, in pin order, then the joining nets. In the wrapper, the joining nets get
  neutral names (`n1`, `n2`, …), because a flattened net name (`X1.mid`) would describe the hierarchy the block
  hides.
- **D4 — The frequency plan is the dialog's own.** The **Frequency** section is enabled whenever Encapsulated
  netlist is chosen. Today it says *"No component uses the technology"* and greys out. It is the same plan,
  segments and default, and 0 Hz is always sampled, as the section already states. **The 0 Hz row is the linear
  network's DC solution, as brief 11 writes it, not the RF engine's 0 Hz clamp.** Above the plan's top
  frequency the block holds its last point and warns (`OutOfRangePolicy.WarnClamp`, the SnP default). So under
  the plan the dialog states the consequence for harmonic balance: the highest fundamental that keeps the
  default harmonic count inside the sampled range. It also states the block's size: *"14 ports × 1,001 points,
  about 3.1 MB."*
- **D5 — Parameters.** A cell parameter that reaches only outside devices (a FET's `N`, `Temp`) stays a parameter
  of the part and passes through to them. **A parameter that reaches any element inside the block is frozen at
  its default**, because the block is sampled at one set of values. The dialog lists every frozen parameter by
  name. The exported part does not have it, and a device it also reached gets the frozen value. A **spread or a
  corner** (brief 13) on a frozen parameter is a refusal: *"Lp has a spread and reaches the S-parameter block,
  which is sampled at one value. Make it reach only a device, or use Compile netlist."* The block is sampled at
  the design ambient (`temp`, default 25 °C), and a temperature-dependent linear element is fixed there.
- **D6 — Devices ship as they are.** Encapsulation hides the linear network only. A ComposedFET's `.cmodel` and a
  Verilog-A source ship readable unless **Compile Verilog-A models** is also ticked, and that option works with
  this one unchanged. The dialog names every device that stays readable, in one line, when that box is off.
- **D7 — Export proves the encapsulated part matches the cell, or writes nothing.** See §4.
- **D8 — Nothing in the library describes the network.** The original `.csch`, its sub-cell folders, the element
  values and every SnP the block absorbed are not copied. The block's Touchstone header carries the part number,
  the plan and the circuitRF version. It carries no element name, no net name and no timestamp, so the same
  cell and plan always give the same bytes.

## 2. `R-vl19-1` — the partition and the block

- **`R-vl19-1a` Partition.** Flatten the cell as `ProtectPartition` already does (sub-cells inlined, instance
  paths as prefixes, expressions kept). Classify each element by the `ModelKind` of the model it builds, per D2.
  `ProtectPartition` gains this as a second rule beside brief 16's table. It is not a second flattener.
- **`R-vl19-1b` The block.** Write the inside elements as one netlist, with a port from each D3 net to ground.
  Sample it on the plan with brief 11's sampler, 0 Hz row included, and write
  `models/<Part>.s<N>p`. The sampler is the one brief 11 uses for a technology part, generalised from one
  part's terminals to a sub-network's ports. It is not a second sampler.
- **`R-vl19-1c` The wrapper.** The wrapper `.cnl` places `SnP:ENC1` on the port nets, with each outside component
  as written (its parameters per D5) and the cell's pins. The wrapper `.csch` is drawn from it by brief 16's
  path, so brief 6 D1 still holds.
- **`R-vl19-1d` No linear network.** The export goes ahead with no block. The dialog's note says *"Nothing in the
  cell is linear: the model is its devices."*
- **`R-vl19-1e` Port count.** There is no cap. The dialog shows the count and the file size before anything is
  written (D4), and the page says that a block with many ports is a large dense matrix at every frequency.

## 3. `R-vl19-2` — Export to Library

- **`R-vl19-2a` The section.** **Protected model** keeps its checkbox, now labelled **Protect the model**, **off**
  by default. Under it are two choices, always shown and greyed while the box is unticked, so nothing moves when
  it is ticked:
  - **Encapsulated netlist (one S-parameter block)**: **preselected**.
  - **Compile netlist (one compiled module)**: brief 16, unchanged, greyed with its own sentence when no
    compiler is found.

  Ticked with Encapsulated chosen, the right-hand list becomes the block's summary. That covers: the port count
  and what each port joins (a cell pin, or a device terminal by instance); what stays outside (D2); the frozen
  parameters (D5); the devices that stay readable (D6); and every refusal. **Proof bias** shows, as it does for
  Compile netlist. Export stays disabled while a refusal stands.
- **`R-vl19-2b` What the model folder holds.** `models/<Part>.s<N>p`, the outside devices' own files (a `.cmodel`,
  a `.va`, or their builds when compiled), and the wrapper `.cnl`/`.csch`. `part.cpart` records
  `"Protection": "encapsulated"` and the plan (brief 11's `Frequency` record), so a later refresh samples on the
  same plan.
- **`R-vl19-2c` After an export**, the dialog's existing finished state shows the model folder as a link that
  reveals it, with the proof's summary, as Compile netlist's does today. Every line there is selectable.
- **`R-vl19-2d` CLI and MCP.** `circuitrf export part … --protect encapsulate|compile`. `--compile-netlist` stays
  as the spelling of `--protect compile`. `--sweep` gives the plan, as it does for technology parts, and
  `--check-bias` gives the proof's points. The MCP entry gains a `protect` key. As with every verb, the logic
  lives in `src/Design`, and `src/Cli` only parses arguments and reports.

## 4. `R-vl19-3` — the proof (D7)

Before anything reaches the library, `ProtectCheck` runs the **original cell** and the **wrapper with its block**
through the same engine, at the **Proof bias** points (brief 16's default set when none are typed):

- **The DC terminal currents.** The block's 0 Hz row is the network's DC solution, so these agree to the
  limit.
- **Y-parameters at three frequencies taken ON sampled points** across the plan, where the block is exact by
  construction. The limit is brief 16's: 1e-6 relative, with absolute allowances of 1e-12 A and 1e-12 S.
- **Between samples** the block is its interpolation. The worst |S| difference halfway between samples is
  reported in dB, as brief 11 reports it, as information and not a gate.
- **At the ambient the block was sampled at.** The block does not follow temperature (D5), so a second ambient
  would measure that choice, not an error.

Over the limit, nothing is written, and the worst point is named with both values.

## 5. `R-vl19-4` — the page

`protected-models.md` gains an **Encapsulated netlist** section beside Compile netlist. It covers: what goes into
the block and what stays outside (D1, D2); the ports; why the frequency plan must reach the highest harmonic a
run will use, and what happens above it; which parameters freeze and why a spread on one is refused; that the
devices ship as they are unless compiled; and the proof. A short table compares the two methods on what each
needs (a compiler or nothing), what each hides, what stays visible, and whether parameters survive (all of them,
or only those that reach devices). Next to the page's list of "what a compiled model still reveals", add a line
on what the block reveals: its port count, and the network's behaviour at those ports over the plan.

## 6. Tests (minimal)

1. Partition: a cell with R/L/C, a sub-cell with an L, an SnP, a technology line and a ComposedFET gives exactly
   one block holding every linear element (the SnP and the line included), with the ComposedFET outside, and
   exactly the D3 ports.
2. A cell that is only a ComposedFET exports with no block, and the note is shown.
3. The proof: the encapsulated `TSTPA200` passes; a block written with one entry deliberately altered fails it,
   and nothing reaches the library.
4. Parameters: a parameter reaching only the FET passes through and is overridable on the placed part; one
   reaching an inside element is frozen and absent from `part.cpart`; a `gauss(…)` on that one is refused with
   D5's sentence.
5. The library folder holds one `.sNp`, no original `.csch`, no sub-cell folder and no absorbed SnP, and checks
   clean. Two exports of one cell give identical block bytes.
6. UI: the section defaults to protection off with Encapsulated preselected; Compile netlist is greyed without
   a compiler while Encapsulated is not.

Run only the classes you touch.

## 7. Not in this brief

- **Noise.** circuitRF has no noise analysis today. When one is added, the block must contribute the thermal
  noise of the passive network it replaced (from its S-parameters and temperature), or the absorbed resistors'
  noise is lost.
- **A parameterised block** (sampled on a grid of a frozen parameter's values, and interpolated). Frozen is the
  first cut.
- **Speed.** Nothing here measures or promises a faster run. A block with many ports is dense where the original
  netlist was sparse.

## 8. On completion

As brief 6 §9. The dialog's Protected model section, unticked and then ticked with Encapsulated showing a block
summary, frozen parameters and a refusal, each gets a `tools/UiSnapshot` render that has been looked at.

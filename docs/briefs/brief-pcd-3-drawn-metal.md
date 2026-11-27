# Brief PCD-3 — The drawn-metal primitive MSHAPE, and Stretch by Parameter

**Series:** [PCell Designer](brief-pcd-0-overview-and-contract.md) · **Tag:** `R-pcd3-n`
**Area:** `src/Design/Schematic/SchematicModel.cs`, `ComponentTypeRegistry.cs`, `BuiltInSymbols.cs`, `EditableSchematic.cs`;
`src/Design/Layout/PCells/PCellRegistry.cs`, `GroundPourPlanner.cs`, `DrawnMetal.cs`, new `MShapePCell.cs`;
`src/Design/Layout/Lvs/DeviceType.cs`; `src/Design/Layout/Em/ParametricEm/EmModelSwitch.cs`;
`src/Core/Devices/ComponentModelFactory.cs`, new `src/Core/Devices/MShapeModel.cs`, `src/Core/Netlist/InstanceNetContract.cs`;
`src/Ui/Diagnostics/SymbolArtworkGenerator.cs`; `docs/user/assets/symbols/mshape*.svg`
**Depends on:** PCD-0, PCD-1's seams commit. PCD-4, PCD-5 and PCD-6 use `MSHAPE` and `DrawnMetal.Stretch`.

---

## 0. What this adds

A designed cell needs copper that no line model draws: a pad, a launch, a ground flag. `MSHAPE` is that copper as a
primitive. Its outline and its pins are its own parameters (PCD-0 §4.3), so they are expressions in the cell's scope like
any other part's, and Stretch by Parameter writes those expressions for a drag handle.

## 1. Requirements

- **`R-pcd3-1` — `MSHAPE` in the schematic.** `SymbolKind.MShape`, netlist type `MSHAPE`, designator `SH`, under
  Microstrip. Parameters and placed defaults as PCD-0 §4.3. The symbol has one pin per `PinX[k]`. The vertices are the
  indexed group (`X[{0}]`, `Y[{0}]`). A Core model stamps nothing, so its pins are open in a circuit simulation.
  `EmModelSwitch.WithoutCircuitModel` names every `MSHAPE` whose `Model` is not `open`, and
  `NoCircuitModelSentence` no longer gives a 3D-parametric cell as the only cause (PCD-1 D4).
- **`R-pcd3-2` — `MShapePCell.Generate`.** A polygon (`Form=polygon`) or a path of width `W` (`Form=path`) through the
  vertices, on the signal layer (`SignalLayer`, else the technology's top conductor). One `PCellPin` per pin k, named
  `k`, at `PinX[k]`, `PinY[k]`, width `PinW[k]`, facing `PinDir[k]`. A polygon with fewer than three vertices, or a path
  with fewer than two or no width, draws nothing and says why. Registered in `PCellRegistry` and the ground pour's
  generator set.
- **`R-pcd3-3` — `DrawnMetal.Stretch`.** Each moving vertex's `X[i]` and `Y[i]` gain `(P − P0)·cos θ` and
  `(P − P0)·sin θ`. P is the bound parameter, P0 its value now, and θ is `AxisDeg`. The result is unchanged at the
  current values and moved by δ when P grows by δ. The binding is appended to `Stretch`. An unknown vertex, or an anchor
  that also moves, is refused.
- **`R-pcd3-4` — its LVS kind.** `MSHAPE` is a `TransmissionLine`, as every drawn conductor is, and the generator id
  `MSHAPE` maps back to `SymbolKind.MShape`.

## 2. Decisions

- **D1 — The two words are placed quoted.** `Form` and `Model` are placed as `"polygon"` and `"none"`. That makes the
  line `Form="polygon" Model="none"`. A string literal passes through the elaborator, which evaluates every
  other key as an expression. An unquoted `Model=open` also works, because `Model` is a selector the elaborator keeps
  verbatim. An unquoted `Form=path` does not, until the elaborator reads `MSHAPE`'s `Form` as a word, as it reads
  MRRC's. Every reader unquotes.
- **D2 — Pin count by parameter rows.** Pin k exists when the instance has a `PinX[k]` row, for k = 1, 2, … up to the
  first missing one. The symbol, the `.cnl` line's net count and the generator's pins all read this one rule.
- **D3 — Stretch is additive.** The old expression stays as written, and the stretch term is added to it. Two
  bindings on one vertex add up. P0 is written in the vertex's unit when P carries a unit (var-unit-wins), and bare
  otherwise, so the old text and the new term are read the same way.
- **D4 — No substrate.** `MSHAPE` is not a microstrip kind for `MicrostripSubstrateInjection`. It has no line model that
  would read H or Er, and its µm defaults suit every technology.

## 3. Tests

`tests/Ui.Tests/PCellDesigner/Pcd3*.cs`, one per requirement: `Pcd3SchematicTests` (`R-pcd3-1`), `Pcd3GeneratorTests`
(`R-pcd3-2`), `Pcd3StretchTests` (`R-pcd3-3`), `Pcd3LvsTests` (`R-pcd3-4`). The existing tests a new kind touches
(`LibraryCatalogTests`, `PCellGeneratorVersioningTests`) are updated.

## 4. Possible enhancements

- **Pins that ride a stretch.** A pin on a stretched edge stays where it is. Moving its `PinX`/`PinY` with the
  vertices would keep a launch's pin on its edge without a second binding.
- **Arcs.** Vertices are straight edges only. A bulge per vertex, as `MCURVE` draws, would give round pads without
  many vertices.

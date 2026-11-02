# Brief 3 — the solver-neutral 3D problem, and generating it from a layout

**Series:** [3D EM, first series](brief-em3d-0-overview.md) · **Tag:** `R-em3d3-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §4.1, §4.1a (lateral extent), §4.2, §4.4, §4.6, §6.3 Tier A, §6.4
**Area:** `src/Engine/Em3d/Em3dProblem.cs` (new), `src/Design/Layout/Em3d/Em3dGenerator.cs` (new),
`src/Design/Layout/Em/EmSetupModel.cs`, `EmSetupPersistence.cs`, `src/Engine/Mom/EmKernelRegistry.cs`
**Depends on:** 2 · **Blocks:** 4, 5, 7, 8, 9

---

## 0. What this brief delivers

A `.cem` that says `"Solver3D": "Palace"` (or `"OpenEms"`) plus the layout and technology it
already names produce, **headlessly and with no solver installed**, one resolved `Em3dProblem`:
named solids with materials, named lumped ports with reference planes, an air box whose faces say
what they are, a frequency range and an operating temperature. Every number is in SI and resolved.

```
.clay + .ctech (+ .wBond, brief 4)  ──► Em3dGenerator ──► Em3dProblem ──► (brief 7) Palace
                                                                     └──► (brief 9) openEMS
```

Nothing is solved here. Brief 5 makes the problem visible and briefs 7 and 9 lower it.

---

## 1. `R-em3d3-1` — `Em3dProblem`, in the numeric layer

**`R-em3d3-1a`** A new type in `src/Engine/Em3d/`, **beside** `Mom/EmProblem`, not derived from it
(overview §1a). Immutable records, SI units (metres, S/m, Hz, °C), no DBU, no `LayerKey`, no
document types. It is the 3D counterpart of what `EmProblem`'s own summary says of itself: *it knows
nothing about `.clay` shapes*.

**`R-em3d3-1b`** Contents:

- **`Solids`** — each has a `Name` (unique, ordinal), a `Material` name, a `Role` (`Dielectric`,
  `Conductor`, `Air`), a **construction primitive**, and a **construction order** (an integer,
  §1d):
  - `ExtrudedPolygon(outline: IReadOnlyList<(x,y)>, holes, zBottom, zTop)`: the layout's
    workhorse;
  - `Box(min, max)`;
  - `Cylinder(axisStart, axisEnd, radius)`: round vias;
  - `Sweep(path: IReadOnlyList<Point3>, section: Hexagon|Circle, up: Vector3, size)`: brief 4;
  - `Sphere` / `TruncatedSphere`: brief 4's balls; bumps, later.

  **No boolean primitive in this brief.** Tier A never needs one, because §1d handles overlap by
  order. Tier B (F4) adds it.
- **`Materials`** — the resolved values each `Material` name refers to (εr or the tensor, tanδ,
  μr, σ at the operating temperature). **Resolved**, so a backend never consults a technology.
- **`Ports`** — §2.
- **`Boundary`** — the air box's six faces, each `Absorbing`, `Pec`, `Pmc` or `Symmetry`, and the
  box's extent.
- **`Frequency`** — start/stop/points/sweep kind, in Hz.
- **`OperatingTempC`** — §4.
- **`Sheets`** — conductors thin enough to be a surface. §5 decides which, here, once, so that
  both backends inherit the same decision.

**`R-em3d3-1c`** `Em3dProblem.Validate()` returns every structural problem it finds rather than
throwing on the first: duplicate names, a port referencing no solid, a solid outside the air box,
a material referenced but not resolved, a zero-thickness non-sheet. A backend calls it first.

**`R-em3d3-1d` Overlap is resolved by construction order, and the order is part of the problem.**
Where two solids overlap, the one with the higher order wins the volume. That is CSXCAD's priority
rule (§6.5), and the same rule the Palace lowering applies by subtracting (brief 7). Tier A assigns
order bottom-up: the stackup's dielectrics, then air above the top, then bodies, then conductors
(metal wins over what it is embedded in), then vias, then wires. **State it in the problem, never
infer it in a backend.** Two backends inferring order separately would disagree on exactly the
overlapping geometry a cross-check is meant to test.

---

## 2. `R-em3d3-2` — 3D ports

**`R-em3d3-2a`** This brief builds **lumped ports only** (overview §4 defers wave ports). A lumped
port is a **rectangular sheet** between two named objects, with a direction, a reference impedance
(complex allowed, as planar's `PortZ0s` is) and a **reference plane**.

**`R-em3d3-2b`** In Tier A a port comes from what the layout already declares: a **layout pin** that
is an EM port (the same labels `EmPortExtraction` reads for planar). Tier A makes the sheet
vertical, from the pin's conductor down to the conductor the setup's return names
(`GroundStackupLayerName`, or R-em-4's inferred rule, **reused through the planar code's own
function**, not re-derived), spanning the pin's edge width. A port the planar extractor would refuse
is refused here with the planar refusal's own sentence.

**`R-em3d3-2c` The reference plane is explicit** (§4.4). For a Tier A port it is the pin's edge,
and the de-embedding length is zero. The field exists from day one so that brief 10's comparison
can say which plane both results are referred to, and so that wave ports can add a non-zero shift
later without a format change.

**`R-em3d3-2d`** Port numbering follows the planar setup's numbering for the same layout, so a
schematic that swaps a planar SnP for a 3D one sees the same port order. A test asserts it on a
two-port.

---

## 3. `R-em3d3-3` — the `.cem`'s new fields

All additive, nullable and omitted at default, with no `FormatVersion` bump (the `.cem`'s standing
rule, which `EmSetupModel.cs` documents at length).

| Field | Type | Default | Meaning |
|---|---|---|---|
| `Solver3D` | enum `None \| Palace \| OpenEms \| Both` | `None` | `None` is a planar setup exactly as today. Anything else makes this a 3D setup. `Both` is brief 10's. |
| `OperatingTempC` | double? | null → 20 °C (§4) | |
| `AirBox` | object? | null → §6's defaults | padding per face, boundary kind per face |
| `Palace` | object? | null → defaults | brief 7 fills it in; this brief declares an empty section |
| `OpenEms` | object? | null → defaults | brief 9 fills it in |

**`R-em3d3-3a` 3D is chosen by name only** (overview §1g). `EmAnalysisKind.Auto` never resolves to a
3D solver, and `Solver3D` is not an `EmAnalysisKind` member. It is a separate field, so the
planar registry's selection logic is untouched. A test runs `Auto` resolution over every `.cem`
in the repo and asserts none becomes 3D.

**`R-em3d3-3b`** A setup with `Solver3D ≠ None` ignores the planar-only fields (`PlanarMesh`,
`AnalysisLevelNames`, …) and `check` notes any that are set, at info. A user switching a planar
setup to 3D keeps them, and switching back finds them where they were. That is the "both sections
kept" rule of §4.2, applied to planar vs 3D as well.

**`R-em3d3-3c`** The generated reference page (`circuitrf reference em-setup`) must describe the new
fields. It is generated from `CemFile`, so it will, **provided** each new type lives in
`CircuitRF.Design`. `DocumentSchema`'s descent stops at the declaring assembly, so a type declared
in `src/Engine` would be named but not expanded. Put the **file** types (the DTOs) in
`src/Design/Layout/Em/`, and keep `Em3dProblem` in the engine. Gate 9 checks the page.

---

## 4. `R-em3d3-4` — operating temperature

**`R-em3d3-4a`** Default **20 °C** (overview §1c, owner decision D3). Every conductor's σ in the
problem is `Sigma20 / (1 + Alpha20·(T − 20))`, `WireMaterial.SigmaAt`'s formula, reused rather than
re-typed. A material with no `Alpha20` gets σ₂₀ at every temperature, and `explain` says so.

**`R-em3d3-4b`** A stackup conductor with **no** named material has only a `SigmaSm`, a number of
unknown temperature. It is used as given, at any operating temperature, and the run adds one note
naming the entries treated that way. The alternative, guessing an α, is a silent assumption.

---

## 5. `R-em3d3-5` — Tier A, from a layout and a technology

**`R-em3d3-5a`** `Em3dGenerator.Generate(EmSetup, EmLayoutSource, Technology) → (Em3dProblem?,
diagnostics)`. It uses the **same** `EmSetupResolver` walk-ups as the planar run (the `.cem`'s
layout walk and the layout's technology walk, which may land on different workspaces; `explain`
reports both). It does not re-implement them.

**`R-em3d3-5b` Geometry, reusing the layout code that already exists.** Flatten through
`LayoutDesignFlatten` (the planar extractor's route), per conductor stackup entry. Merge touching
shapes with `LayoutClipper`/`LayoutBooleans`, **the same union the planar extractor uses**, so the
3D model and the planar one agree on what is connected. Each resulting polygon, with its holes,
becomes an `ExtrudedPolygon` through the entry's thickness at the stackup's z. Honour `SheetAt`
and `PresentWithLayer`. Per §4.1a, `PresentWithLayer` is simply true in 3D: the film is extruded
from the mask.

**`R-em3d3-5c` Dielectrics and lateral extent** (§4.1a). A dielectric entry extends to the
board-outline drawing layer when the layout draws one, and otherwise to the air box's lateral
extent. **The outline layer is found the way the Gerber/PCB paths find it; do not add a second
convention.** If none exists, find out what the interchange code calls it and record the answer in
`RESOLVED.md` before inventing one.

**`R-em3d3-5d` Vias.** Round drills become `Cylinder`s, spanning `ViaSpanResolver`'s answer (the
code that already resolves `SpanFromLayer`/`SpanToLayer`). Plated vias with a `WallThicknessDbu`
become a tube, as a cylinder of the plating metal with a higher-order cylinder of the fill (air or
dielectric) inside it. That is §1d's order rule doing the subtraction.

**`R-em3d3-5e` Bodies** (brief 2). Each `TechBody` becomes an `ExtrudedPolygon`, from the union of
its `OutlineLayers`' shapes or from the air box's lateral extent when empty, starting on `SitsOn`'s
top face.

**`R-em3d3-5f` Sheets vs solids** (§6.1, "the single largest mesh saving"). A conductor is a
**sheet** when its thickness is below **both** a small multiple of the skin depth at the top
frequency **and** a fraction of its smallest lateral dimension. It then becomes a surface carrying
its σ and thickness. The two thresholds are named constants with a comment saying where they come
from. **F0's Q6 decides whether they are right**; until then use 3δ and 1/10, and say in
`RESOLVED.md` that they are provisional. The decision is made **here, once**, for both backends.

**`R-em3d3-5g` Names** (§6.4). Solids are named from what the user already calls things:
`<stackup entry>/<net or piece index>` for layout metal (the net name if the shape carries one, else
a deterministic piece ordinal from the merge order), `<body name>`, `via/<n>`, `port/<number>`.
**Deterministic**: the same layout gives the same names in the same order on every platform, with no
dictionary iteration order and no `GetHashCode`.

**`R-em3d3-5h` Solve region.** The planar `.cem`'s `SolveRegion` (`EmSolveRegion.cs`) already says
which geometry is solved at all. Tier A honours it with the same code. A 3D problem is expensive
enough that solving less of the board is usually the first thing a user wants.

---

## 6. `R-em3d3-6` — the air box

**`R-em3d3-6a`** Default padding: a fixed fraction of the largest wavelength in the band on the
four lateral faces and on top, with the bottom face on the lowest ground-reference conductor when
there is one (`Pec`), `Absorbing` otherwise. State the fraction as a named constant. Palace's
absorbing boundary needs more room than openEMS's PML (§2, §3), so each backend section may
**enlarge** the box (never shrink it) and says so when it does.

**`R-em3d3-6b`** Every solid must lie inside the box. The generator clips nothing, and a solid
crossing the box is `Validate()`'s error, naming the solid.

---

## 7. Gate

`tests/Ui.Tests/Em3d/Em3dGeneratorTests.cs` and `tests/Engine.Tests/Em3d/Em3dProblemTests.cs`.
No solver is involved anywhere in this brief.

1. **A 50 Ω microstrip `.clay`**, the one `circuitrf reference layout` uses as its example. It
   produces: one substrate solid, one air solid, one conductor (sheet or solid per §5f, asserted),
   two ports with the planar setup's numbering, and a PEC bottom face. Assert names, orders, and z
   extents to the DBU-to-metre conversion.
2. **Determinism.** Generating twice gives equal problems, and the problem's canonical text form (a
   test helper that writes it with `"R"` formatting) is byte-identical across two runs.
3. **Case B's geometry** (brief 1's via transition, written as a `.clay` + `.ctech`) generates a
   plated via as tube-plus-fill with the right orders, and an antipad hole in the ground plane.
4. **An overmold body** covers the lateral extent with the order above dielectrics and below
   conductors.
5. **`Auto` never resolves to 3D** over every `.cem` in the repo (§3a).
6. **Round-trip.** Every existing `.cem` in the repo loads and saves byte-identically.
7. **A refused port** gets the planar extractor's own refusal text.
8. **Validate()** reports all of: duplicate name, solid outside box, unresolved material. Assert
   the count is three, not one.
9. **`circuitrf reference em-setup`** describes `Solver3D`, `AirBox`, `OperatingTempC`.

## 8. Scope

- **No bond wires.** Brief 4.
- **No wave ports, no Floquet ports.** A later series.
- **No lowering to any solver format.** Briefs 7 and 9.
- **No booleans in the problem.** Order resolves overlap (§1d).

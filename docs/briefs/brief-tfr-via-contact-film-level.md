# Brief TFR-V — a via-contacted resistor film on a level of its own

**Tag:** `R-tfrv-n`
**Area:** `src/Design/Layout` (`ResistiveFilms`, `ResistorFilmBinding`, `TechValidation`, `PCells/MmicStack`, `PCells/MmicPCells`, `Lvs/DeviceRecognition`), `src/Design/Schematic` (`ComponentTypeRegistry`, `MmicPassiveInjection`), `src/Ui/ViewModels/ParameterRowViewModel.cs`, `src/Design/Layout/Em/PlanarExtractor.cs`, `src/Design/Layout/Em3d/Em3dLayoutSolids.cs`, `src/Ui/Layout/TechEditorViewModel.Films.cs`, `src/Ui/Layout/StackupLayerRowViewModel.cs`, `src/Render/Renderers/StackupScene.cs`, `src/Design/resources/technologies`
**Depends on:** [EM-TFR](brief-em-tfr-resistive-film-in-planar-em.md) and [stackup-render 9](brief-stackup-render-9-resistor-films.md), both shipped, and the fixture `testdata/em-tfr/zigzag`

---

## The gap

Thin-film resistors are contacted in two ways, and a `StackupFilm` can state only the first.

| | Overlap contact | Via contact |
|---|---|---|
| Common on | GaAs | a silicon back-end, LDMOS among them |
| Film sits | on the surface the metal is deposited on | on a level of its own in the dielectric |
| Contact | the metal's underside, across the overlap | dedicated vias |
| Metal over the film | is a contact | passes without touching it |
| Length is set by | the metal's edges | the via openings |

**"Metal wins" is correct for the first and wrong for the second.** A film shares its conductor's sheet, and `ClipFilmsToMetal` removes it wherever metal on that level covers it. A resistor with a strap crossing it reads as shorted, and there is nowhere to state a contact via, because a via spans two conductors and the film has no conductor of its own.

**The way this can be drawn today works in the solver and misleads everything else.** A second conductor entry with σ = 1/(R_s·t) and its own drawing layer solves correctly, and:

- **It is copper.** Its layer is in `DrawingLayers`, so connectivity reads contact, via, film, via and contact as one piece: a short across the resistor. About 100 source files read that list. Stackup-render 9 moved films out of it for this reason.
- **The `TFR` part does not use it.** `MmicStack` finds no film, so the part warns and falls back to its standalone defaults. `TfrSheetResistance` stops resolving.
- **Naming a material replaces its σ.** A level naming `Gold` solves at gold's conductivity. `check` warns.

## The approach: a film on a conductor entry with no metal

**A Conductor entry whose `DrawingLayers` is empty and whose `Films` is not is a film level.** There is no new `StackupKind` and no new `.ctech` field, so no `FormatVersion` change. The film stays out of `DrawingLayers`, so no reader of copper needs teaching. The via's `SpanToLayer` already resolves by entry name.

```json
{ "Kind": "Dielectric", "Name": "TFR Isolation", "ThicknessDbu": 200, "Epsr": 6.8, "TanD": 0.001, "...": "..." },
{ "Kind": "Conductor",  "Name": "TFR Level", "ThicknessDbu": 50, "DrawingLayers": [],
  "Films": [ { "Layer": { "Layer": 4, "Datatype": 0 }, "SheetResistanceOhmPerSq": 50 } ] },
{ "Kind": "Via", "Name": "TFR Contact", "Fill": "Solid", "SpanFromLayer": "Metal1", "SpanToLayer": "TFR Level",
  "DrawingLayers": [ { "Layer": 11, "Datatype": 0 } ] }
```

**Overlap contact stays exactly as it is.** A film on a level that has metal keeps "metal wins", and nothing about an existing technology, run or `.snp` moves.

## What was measured

The EM-TFR zig-zag: 100 µm GaAs, 3 µm gold, 50 Ω/sq. A 200 nm dielectric (εᵣ 6.8) and a 50 nm film level were added under Metal1, with a solid via on each contact's overlap, so the resistor's length is unchanged and the result should match the overlap model. Both levels are named in `AnalysisLevelNames`. Planar kernel, 4,495 unknowns including 72 vertical, levels 0.25 µm apart with cell/separation 39.9.

**The planar solve agrees with the overlap model.** Max |ΔS| against the committed run `refs/p_dut.s2p`, with a σ-based level on the same mesh:

| DC | 1 GHz | 5 GHz | 10 GHz | 20 GHz | 40 GHz |
|---|---|---|---|---|---|
| 0.0007 | 0.0005 | 0.0010 | 0.0018 | 0.0035 | 0.0058 |

It is reciprocal and passive at every point.

**The metal-less form gives the same answer.** At 1 GHz its S11 and S21 match the σ-based level to six or more decimal places, on the same 4,495 unknowns. The sweep above was not repeated on it. It carries two warnings that R-tfrv-1 removes.

**LVS already reads it correctly on the placed part.** The PDK `ThinFilmResistor` on this technology reports a match, with no fallback warning, and no piece of the film counts as copper. It also matches with no contact via drawn at all, which R-tfrv-5 closes.

**The 3D dry run accepts it.** The film lowers to a sheet at the level's z and the via to a solid, with no refusal. No 3D solve has been run.

**The Technology editor opens it, and treats it as a broken conductor.** A draft of the silicon stack in R-tfrv-7 loads, passes `check` with only the two warnings R-tfrv-1 removes, and draws its via landing on the film level. The tab shows those warnings as two problems, paints the band in a metal's colour and labels it `σ = 0e+0`. No solve has been run on that stack.

**The planar kernel does not read a dielectric's conductivity.** `PlanarExtractor` builds each layer of the medium from `Epsr`, `TanD` and `Mur`, so a `SigmaSm` on a Dielectric entry changes nothing and nothing says so. A silicon substrate can be stated only as a constant loss tangent, which R-tfrv-7 has to work within.

### The defect to close first

A metal-only line on this stack is wrong when the film level has nothing drawn on it, so only Metal1 is analysed. Here is the zig-zag's 2×-thru at 1 GHz, 988 unknowns, by what lies under Metal1:

| Under Metal1 | \|S11\| | The run says |
|---|---|---|
| GaAs alone (`refs/p_thru.s2p`) | 0.0014 | passive |
| 2 µm dielectric | 0.0019 | passive |
| 200 nm dielectric | 0.0078 | passive |
| 200 nm dielectric, 50 nm level | **0.181** | not passive; de-embedding error up to 0.11 |
| 50 nm level directly under Metal1, no dielectric | none | engine error: the quasi-static calibration reads a negative capacitance |

The σ-based and metal-less levels read the same 0.1809 on the fourth row. Over the full sweep that row is non-passive from 1 to 10 GHz, worst σ_max 1.0318, with a de-embedding error up to 0.17.

**The cause is not established.** Removing the unanalysed band clears most of the error, and the 200 nm row is still 5.6× the baseline. The two runs also take different paths: the 200 nm row adds a uniform feed lead and uses 4 standards of N = 717 to 1127, and the fourth row adds no lead and uses 4 of N = 963 to 1332. The solved resistor, with both levels analysed, shows none of this.

## What to build

### R-tfrv-0 — find and close the thru defect

Before any other step. Reproduce the table on the fixture at 1 GHz, where each run takes about two minutes, and find why an unanalysed level changes the calibration.

**Gate:** with no film drawn, a line on the film-level stack reads as a line on the same stack without that band, within 0.002 in |S|. The 200 nm row's 0.0078 is either explained or fixed, and the answer is recorded in `src/Engine/Mom/RESOLVED.md`.

**Stop and report if the medium cannot be made independent of whether a film is drawn.** Every ordinary line on such a technology would then depend on what else the layout contains, and the design needs rethinking.

### R-tfrv-1 — the film level in the model

- `ResistiveFilms.IsFilmLevel(entry)`: Conductor, no `DrawingLayers`, at least one film, not ground. Every other rule asks this.
- `TechValidation`: the "claims no drawing layer" and "non-positive conductivity" warnings do not fire on a film level.
- `ResistorFilmBinding.CanPlaceOn` already allows it. Add three rules, each with a `tech.film.*` id and called from both validation and the canvas:
  - **`no-contact`, warning:** no via spans to the film level, so a film on it can only ever be an open.
  - **`touching-metal`, error:** the entry directly above or below is a conductor, because no dielectric separates them (the last row of the table).
  - **`face-on-level`, warning:** a film on a film level states `At`, which has no meaning without metal.
- **The level's thickness is the entry's `ThicknessDbu`, and the film's own `ThicknessDbu` is not used on a film level.** A second number for one height would drift. Validation warns if both are stated and differ.

### R-tfrv-2 — the planar extractor

- **A drawn film's level joins the analysis on its own, with a note.** An unanalysed film level is an open circuit, which reaches the solver as smooth and plausible. Naming levels by hand stays possible. Naming a set that excludes a level carrying drawn film is a refusal that names the level.
- **`ClipFilmsToMetal` does nothing here.** It already compares levels, so a test is enough.
- **The conductor-thickness correction runs on Metal1.** It declines only on a level carrying a film, which Metal1 no longer is. Hold this with a test, because it is a real gain over overlap contact.
- **Rooftop merging does not apply**, since it needs one level and no vias. Say so in the run's note, as today, and do not change it here. A run's cost comparison belongs in the completion note, measured on a quiet machine with a Release build.

### R-tfrv-3 — metal over a film

The capability this brief exists for, and the two tests that prove it on the planar kernel:

1. **A strap crossing the film does not short it.** The zig-zag with a Metal1 strap across one leg, joined to nothing: DC within 1 % of the same film with no strap. The same artwork in overlap style must read lower, which shows the test can tell them apart.
2. **The via openings set the length.** A straight film with metal heads extending well past the vias: at DC the film reads R_s × (distance between the vias' inner edges) / W, within the fixture's 2.3 % spread between methods.

### R-tfrv-4 — 3D

`Em3dLayoutSolids` already lowers the film to a sheet at the level's z, in every `Conductors` mode, so the 50 nm Palace trap recorded in `testdata/em-tfr/zigzag/README.md` should not arise. Verify that rather than assume it.

- **Run Palace with `Sheets` and `Solids`** on the fixture, de-embedded against the thru from R-tfrv-0: |ΔS| ≤ 0.02 against the planar run, the gate EM-TFR uses. Then EMerge and openEMS, whose film lowerings were each found the hard way, on `Sheets`.
- **The risk is the mesh.** A 200 nm dielectric gap between solid metal and a sheet may force far more elements than the 14,584-unknown estimate the dry run gives. Report what it costs. If a solve is impractical, say so in the docs and in the run's note. Do not thicken the gap silently.
- **New 3D View from Layout** must carry the via and the film. The placed-part path has its own open question: `C3dHierarchy.NewFromLayout` called directly on the PDK `ThinFilmResistor` reported no metal, whatever the film's level. Establish whether the GUI path shares that, and fix it here if it does.

### R-tfrv-5 — one `TFR` component, both styles

**There is one `TFR`.** The user chooses a film, and the film decides the sheet resistance and the contact style. Nothing else about the part changes between styles: `W`, `L`, `N` and `Lc` mean the same, and R = R_s × squares in both.

**Today the part cannot choose.** `MmicStackResolver` takes the first film in stackup order, so a second film in a technology cannot be placed at all. `Rs` is injected from that first film, and `TfrSheetResistance` names it.

- **A `Film` parameter, shown as a combobox.** `ComponentTypeRegistry.LayerChoiceKindFor` gains `LayerChoiceKind.Film` for `(SymbolKind.Tfr, "Film")`, and `ParameterRowViewModel` lists every film of the resolved technology through `ResistiveFilms.All`, in stackup order. Each row names the drawing layer and states what it gives, for example `Resistor · 50 Ω/sq · TFR Level (via)` or `Resistor · 50 Ω/sq · Metal1 (overlap)`. The row is there even with one film, so the dialog's layout does not change between technologies.
- **The value is the drawing layer's name, and empty means the first film.** A cell or netlist that states no `Film` reads as it does today, so no existing schematic, layout or result moves.
- **A name that matches no film is a refusal naming the film and listing the real ones.** It never falls back to the first film, which is the failure a typed layer name has already caused on `TESTPAD`.
- **Everything resolves through the chosen film.** `MmicStackResolver.Resolve(technology, film)` returns `ResistorLayer`, `ResistorFilm` and `ResistorLevel` for that film, so `Rs`, `H`, `ErSub`, the artwork and the readout agree. `H` and `ErSub` are taken under the chosen film's level. `Film` is text and does not reach the numeric layer: it is resolved and removed in `MmicPassiveInjection`, as a test pad's `PadLayer` is.
- **The style is derived from the film, never stated.** `ResistiveFilms.IsFilmLevel(level)` is the one answer. Overlap draws contacts on the film's own level, lapping its ends. Via draws heads on `ResistorContactMetal` and contact vias inside them. There is no style parameter, so the two cannot disagree.
- **`MmicStack` gains the contact.** `ResistorContactVia` is the via spanning to the film level, and `ResistorContactMetal` is its other end. Today `ResolvedMmicStack.Key` falls back to the shipped GaAs layer numbers when the level has no metal, which happens to be right on the first test stack and would be silently wrong on any other technology.
- **`TfrPCell` draws the vias.** Length is measured between the vias' inner edges. The via stays inside its head and inside the film, by rules read from the technology. A film level with no contact via is a refusal through `MmicStackResolver.Why`, in the words of the existing film refusal.
- **Changing `Film` re-renders the part.** The readout updates, the PCell fingerprint includes `Film`, and a snapshot written before the parameter existed still reads as the first film. Update Layout handles a change from one style to the other as a redraw of the same part, keeping its pins.
- **`DeviceRecognition` requires the via, and reads the film the body is drawn on.** On a via-style film, the rule's body is the film and its terminals come through the contact via. A resistor with a contact missing is an open in the LVS report, which matches what the EM run sees, and must not report a match. A rule's R comes from the film of its body's layer, derived per rule as `TfrSheetResistance` is, so a two-film technology compares each resistor against its own R_s. LVS compares `Film` against the layer actually drawn.
- **DRC:** via-in-film and via-in-metal enclosure rules, as ordinary rules a via-contact technology states.

The DC rail solver and cross-section readers read `DrawingLayers`, so they see no film, as they do for overlap contact. Nothing changes there.

### R-tfrv-6 — the Technology editor

The editor needs its own work: every field on a conductor's card assumes the level has metal.

- **The tab offers a film level.** **+ Film Level** sits beside **+ Conductor** and creates the entry, then asks for its film on the same path as **New Resistor Film…**: an existing drawing layer or a new one, and a sheet resistance.
- **The card shows what the level has.** Name, thickness and **Resistive films** stay. Material, σ, Rough, **Metal thickness goes to** and **Ground reference** go, because none of them means anything without metal.
- **Metal can still be added.** The drawing-layer checklist stays. Checking a layer makes the entry an ordinary metal level, whose film is overlap-contact. Removing the last film from a film level leaves a conductor with neither, which keeps today's "claims no drawing layer" warning.
- **The drawing tells it apart from metal.** The band has no metal colour and no σ in its label. Its film strip fills the band, whose card and selection behave as for any other entry. A via barrel lands on it, as it already does.
- **The face gestures do not apply on a film level.** `Face` is absent from the menu, and a drag to the other face writes nothing. A film can still be moved between levels, and a move onto a metal level makes it overlap-contact, which is what that move means.
- **Every value that can be listed is a combobox.** The film dialog's drawing layer is a picker of unused layers, with **New layer…** as its last row, so a name is typed only when creating one. The via span pickers already list the film level, and a test holds that. A film's move target, the level a **+ Film Level** is inserted beside, and the conductivity unit use pickers too.
- **Round trip:** a film level saved from the editor writes the same bytes it read, with no `Material` or `SigmaSm` gained.
- **Render it with `tools/UiSnapshot --ctech` and inspect it**, in both a tall and a narrow pane.

### R-tfrv-7 — a silicon demo technology, fixture and docs

**`mmic-GaAs_2LM_100um.ctech` is not edited**, nor are its copies or the starter. It stays the overlap-contact demo, and a Si demo beside it is the via-contact one, so the pair shows both styles.

**`src/Design/resources/technologies/rfic-Si_3LM_TFR_100um.ctech`**, offered wherever a shipped technology is. Generic round numbers, stated as an example and not taken from any process. Two films on the film level, `Resistor` at 50 Ω/sq and `Resistor Hi` at 500 Ω/sq, so a placed `TFR`'s combobox has a real choice:

| Entry | Thickness | Material | Notes |
|---|---|---|---|
| Metal3 | 3 µm | Aluminium | thick top metal, the lines |
| ILD3 | 1 µm | oxide, εᵣ 4.1 | |
| Metal2 | 1 µm | Aluminium | the resistor's heads, above the film |
| ILD2 Upper | 0.4 µm | oxide | |
| **TFR Level** | 50 nm | films, 50 and 500 Ω/sq | no metal |
| ILD2 Lower | 0.4 µm | oxide | |
| Metal1 | 1 µm | Aluminium | routes under the film |
| ILD1 | 1 µm | oxide | |
| Silicon | 100 µm | `High-resistivity silicon` from the shipped library | see below |
| Backside Metal | 3 µm | Gold | ground |

Vias: `TFR Contact` Metal2 → TFR Level, `Via12` Metal1 → Metal2, `Via23` Metal2 → Metal3. Width, spacing and enclosure rules on the film and the contact, and device rules for the resistor and for a Metal3 line.

**The film sits between two metals**, which the GaAs-derived measurement did not cover, so this stack is a test in its own right:

- `Via12` passes through the film level's z and must not connect to a film there unless it is drawn to. A film in its path is a DRC error.
- `TFR Contact` lands on the film and must not reach Metal1.
- Metal1 under the film and Metal2 over it both cross without contact, on a planar run with all three levels analysed.
- `MmicStack` finds the contact metal (Metal2) rather than the base metal, and the `TFR` part places, simulates and passes LVS on it.

**The silicon's loss is a stated limit until [the conductivity brief](brief-si-dielectric-conductivity.md) lands.** The library material is tanδ 0.0015, its value at 10 GHz for 10 kΩ·cm, and real conduction loss falls with frequency where a constant tanδ does not. The docs say so, with that frequency. Do not put a `SigmaSm` on the entry until then, since the planar kernel would ignore it. When that brief lands, this entry states its conductivity instead.

**Also in this step:**

- **`examples/`:** a short workspace on a copy of this technology, with a placed `TFR` crossed by a Metal1 line and a Metal3 feed, and a README stating what it shows and its run times. A gate holds the copy byte-identical to the shipped file, as `TheMmicTechnologyIsTheShippedOneByteForByte` does.
- **`testdata/em-tfr/zigzag-via`:** the GaAs-derived structure measured above, with its own technology and committed runs, because it is what compares directly with the overlap model. `EveryCopyStatesTheOneFilm` keeps matching only `mmic-GaAs_2LM*`, so this file gets its own name.
- **A test-only technology with both styles at once:** one overlap film on Metal1 and one via film on a film level, which is what proves one `TFR` handles both. Shipped technologies stay one style each.
- **`docs/user/src/reference/stackup.md` `#film`:** the sentence saying a film cannot state via contact becomes a description of both styles, with the film level's rules and the editor's gesture. `components.md` `#tfr` says which contact the part draws on each. Regenerate those pages. A new figure of the Si stack is added to the catalog and generated when figures next are.
- **Findings go in `src/Design/RESOLVED.md` and `src/Engine/Mom/RESOLVED.md`.**

## Order and gates

R-tfrv-0, then 1 and 2 together, then 3. Stop there and report: at that point the planar solve is complete and the cost of the rest is known. Then 4, 5, 6 and 7.

| Gate | Holds |
|---|---|
| 1 | An unanalysed, undrawn film level leaves a line within 0.002 in \|S\| of the same stack without the band |
| 2 | Zig-zag on the film level against `refs/p_dut.s2p`: \|ΔS\| ≤ 0.01 through 40 GHz, against the committed result so no solver runs in CI |
| 3 | A strap over the film: within 1 % at DC, and the overlap form reads lower |
| 4 | Via-defined length at DC within 2.3 % |
| 5 | The conductor-thickness correction applies to Metal1 on a film-level stack |
| 6 | A contact via missing: LVS reports an open, and the planar run reads one |
| 7 | Palace `Sheets` and `Solids` within 0.02 of the planar film section |
| 8 | Every existing overlap-contact technology: no `.snp` provenance hash moves, and `mmic-GaAs_2LM_100um.ctech` is unchanged in the final diff |
| 9 | The Si demo: `check` clean, a placed `TFR` passes LVS, and a Metal3 line reads as a line under R-tfrv-0's gate |
| 10 | On the Si demo, `Via12` and `TFR Contact` connect only what they span, and metal over and under the film leaves its resistance within 1 % at DC |
| 11 | A film level saved from the editor round-trips byte for byte |
| 12 | One `TFR`, on the two-style technology: changing `Film` changes R_s, draws the right contact for each style, and each passes LVS against its own R_s |
| 13 | A `TFR` stating no `Film` is identical to today's on every existing technology, artwork and value |
| 14 | A `Film` matching no film is a refusal listing the real ones, in the dialog, `check` and a run |

One test per claim, each running under the ~5 s `Category=Benchmark` threshold. No new timing benchmarks. Run the affected classes only.

## Not in this brief

- **A silicon substrate's conductivity.** That is [its own brief](brief-si-dielectric-conductivity.md).
- **Contact resistance.** A via's contact is ideal here, as an overlap's is. If a measured value matters, it belongs on the via or the film in a brief of its own, covering both styles.
- **Film temperature coefficient,** and a film's heating in the thermal solve.
- **Poly, implanted and epitaxial resistors.** They are in the semiconductor and contacted like devices. This brief covers films in the dielectric.
- **Moving a technology between styles automatically.** The artwork differs, since one has contact vias and the other does not.

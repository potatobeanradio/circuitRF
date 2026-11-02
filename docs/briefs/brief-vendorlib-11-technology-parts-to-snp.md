# Brief VL-11 — technology parts become S-parameters at export

**Series:** [Model libraries](brief-vendorlib-6-model-library-format.md) · **Tag:** `R-vl11-n` · **Phase:** authoring, numeric
**Area:** `ModelLibraryExport` (brief 10); the six technology seams in `src/Design/Schematic/`
(`MicrostripSubstrateInjection`, `PlanarLineSubstrateInjection`, `BroadsideSubstrateInjection`, `ViaSubstrateInjection`,
`MmicPassiveInjection`, `TestPadInjection`); the SnP symbol (`dynamic-symbols.md#snp`); the Export dialog;
`src/Ui/Views/Layout/EmSetupEditorView.axaml` (the frequency plan, to be shared); `FrequencySpecViewModel`
**Depends on:** 10

---

## 0. Why

A model never carries a technology (D3). MLIN and the other stackup-bound parts take their substrate from
the workspace they run in, so a model built on its author's board would otherwise be re-simulated on
whatever substrate the consumer has, with nothing said. Brief 10 refuses such parts. This brief converts
them instead. Each one becomes an SnP, sampled on the author's technology at export time, using the same
circuit model a run would use.

The conversion is **exact at every sampled frequency**. circuitRF already simulates each of these parts
as its own linear n-port referenced to ground, and the Touchstone file holds exactly that n-port's S.
The only approximations are between samples (interpolation) and outside the sampled range, where the
range warning (brief 9 §3d) applies.

## 1. `R-vl11-1` — which parts

- **`R-vl11-1a`** One predicate, `TechnologyBound.Is(SymbolKind)`: the union of the kinds the six
  injection seams handle. Today that is MLIN and its bends, tees, crosses, tapers and curves, MCLIN, CPWG,
  SLIN, the broadside pair, VIA, VIAGND, the MMIC passives, the GSG pad and the test pad. A test asserts
  every seam's own kind set is inside it, so a seventh seam cannot be missed.
- **`R-vl11-1b`** Sub-cells are converted too, recursively.
- **`R-vl11-1c`** The substrate is the one **Simulate would use** for the source schematic: its own
  technology if it names one, otherwise the workspace default, resolved through the seams themselves and
  never re-derived. **No technology resolved is a refusal**: *"TL1 (MLIN) needs a technology to be
  converted, and this schematic has none."*

## 2. `R-vl11-2` — the conversion

- **`R-vl11-2a`** For each instance, a transient bench places that one part with a 50 Ω port from **each
  terminal to ground**, with the seams' substrate injection applied, and runs the ordinary S-parameter
  analysis over the dialog's frequency plan. Result: `models/<instance>.s<N>p`, where N is the terminal
  count (MLIN 2, MTEE 3, MCLIN 4, VIAGND 1).
- **`R-vl11-2b` DC is always in the file.** If the plan's lowest frequency is above 0 Hz, 0 Hz is added.
  Without a DC row, a circuit DC run clamps an SnP to its lowest frequency, and a drain-feed line inside
  an active model would bias wrongly with nothing said.

  **The DC row is what circuitRF's own DC engines do with that part**, not the RF model evaluated at
  0 Hz. A line is a DC short (`IDcShortedTerminals`), so its row is the ideal junction of each shorted
  group, and a terminal in no group is open. A part without that interface is evaluated by the
  S-parameter analysis at 0 Hz. A non-finite result there is a **refusal naming the type**. Stop and
  report it rather than patching the model.
- **`R-vl11-2c`** Each file's header comment records the provenance: the part type, its parameters as
  written, the technology's **name** (never its path), the frequency plan, and the circuitRF version. The
  same record goes into `part.cpart`'s `Converted` entry.
- **`R-vl11-2d` The converted instance keeps its glyph.** An SnP symbol's pins sit somewhere else, so
  swapping glyphs would leave the wires disconnected in the exported `.csch`. Give SnP the same
  artwork-only `Symbol` parameter brief 7 gave VerilogA: it names a built-in kind, and SnP port k binds
  to that glyph's pin k. The exported instance keeps its name, position, rotation and glyph
  (`Symbol=Mlin`), and only its kind and `File` change. Wires stay attached, and the picture still reads
  as a line. On a placed SnP, `Symbol` is a choice in the parameter editor like brief 7's. Do not add any
  other new UI for it.
- **`R-vl11-2e`** The `.cnl` is then extracted from the converted `.csch` (brief 10 §2d), so brief 9's
  `.cnl`/`.csch` identity holds without any special case.

## 3. `R-vl11-3` — the self-check, extended

Brief 10's self-check compares the source cell with the exported model. With converted parts:

- the S-parameters are compared at **every sampled frequency**, not 11;
- the DC comparison **drives current through the converted parts**: behind each pin's 50 Ω port is a DC
  source of a different value (pin k: k × 0.1 V), so a converted line on a bias path carries current and
  a wrong DC row shows up;
- the tolerance stays 1e-9, relative, per entry, and a mismatch is still a refusal naming the worst entry;
- the worst difference **halfway between samples** is measured and given in the success line (*"…
  between samples, within 0.02 dB"*). It is information, not a gate.

## 4. `R-vl11-4` — the dialog

- **`R-vl11-4a`** A **Frequency** group using the EM Setup's frequency plan, with Start / Stop, Step or
  Points, Lin / Log, **+ Segment** and **Single**. It is the same control, not a copy: extract the plan's
  markup from `EmSetupEditorView.axaml` into a reusable control over `FrequencySpecViewModel` rows, and
  have both use it.
- **`R-vl11-4b`** **Default: one linear segment, 0 Hz to 20 GHz, 1001 points.**
- **`R-vl11-4c`** One line above the group names what will be converted: *"4 components use the technology
  and will be converted: TL1 (MLIN), TL2 (MLIN), T1 (MTEE), V1 (VIAGND)."* With none, the group is
  **greyed out** and the line reads *"No component uses the technology."* Under the group: *"DC is always
  included."*
- **`R-vl11-4d`** The part's **Frequency range** field fills in from the plan when anything is converted.
  It can be narrowed but not widened past the plan. Widening is a refusal: the S-parameters stop there.

## 5. `R-vl11-5` — headless

`export part` (brief 10) gains `--sweep start=… stop=… npts=…|step=… [unit=GHz] [log]`, repeatable, one per
segment, using the `sparam` directive's own keys. The default is §4b's. With no technology-bound part,
`--sweep` is refused rather than ignored.

## 6. Tests (minimal)

1. A synthetic cell holding R, MLIN, MTEE and VIAGND exports. Every converted file has 1001 points, 0 Hz
   first. The self-check passes at all points, including the driven DC
   comparison.
2. A plan from 1 to 20 GHz still writes a 0 Hz row, and the driven DC comparison passes. Then delete the
   row by hand and the same comparison fails, which proves the row matters.
3. The exported `.csch` keeps each converted instance's pin positions, and its extraction has the same
   nets as the source.
4. `TechnologyBound` covers every seam's kinds.
5. No technology resolved is a refusal. A cell with no technology-bound part has the group greyed out
   and refuses `--sweep`.

Run only the classes you touch. The full 1001-point conversion of four small parts is milliseconds of
circuit evaluation. If test 1 takes over ~5 s, measure where the time goes before tagging anything.

## 7. Docs

`model-library-format.md`: the conversion, the DC row, `Converted`. `dynamic-symbols.md#snp`: the
`Symbol` parameter. UiSnapshot: the Export dialog with the Frequency group live and with it greyed out.
Inspect both and list them in the completion note.

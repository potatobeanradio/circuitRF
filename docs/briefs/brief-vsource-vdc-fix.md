# Sonnet Brief — Remove legacy "V" source; add dedicated Vdc component; fix DC bias

**Bug (root cause of the "Vout DC = 0.00" report).** A plain `V:` source emits `Vac=`/`Freq=` from the registry,
but `VoltageSourceModel` reads **only `V=`**. With no `V=` key it stamps **0 V**, so in a netlist like
`V:V2 n2 0 Vac=-3.05 V Freq=0` the −3.05 V (and a 48 V) DC bias are silently zero → the biased interface node's
DC collapses to 0. The code already flags this as a known unconverged seam.

**Owner decision (LOCKED): there is no plain "V" component going forward.** The library has exactly **three**
voltage sources: **`V_1Tone`**, **`V_nTone`** (both AC tone sources that also carry a `Vdc`), and a new
**`Vdc`** (pure DC). The legacy `V`/`VoltageSource`/`SymbolKind.VoltageSource` is **removed entirely** — the owner
does not want to document or explain a "V" component to users. This brief deletes it and adds `Vdc`.

This is **independent of the Table/UX cluster** Sonnet is coding now — different files.

## Part A — Remove the legacy `V` / `VoltageSource` component

Delete the plain voltage source end-to-end (alpha, no back-compat):
- **`SymbolKind.VoltageSource`** — remove the enum member and every reference (registry entry, `DefaultParameters`
  case, `EngineReference`, `TryParseCode "V"`, `SymbolPortDefs`, palette, symbol art). Build must be 0W/0E after.
- **`ComponentModelFactory`** — remove the `"V" → VoltageSourceModel` registry entry.
- **`VoltageSourceModel.cs`** — **delete the class** (its DC role is taken over by the new `Vdc` model in Part B;
  its never-worked AC role was always the tone sources' job). Confirm nothing else references it (HB extractor,
  loadpull, tests). If something does, repoint it to the `Vdc` model or `ToneSourceModel` as appropriate and note
  it.
- **`.cnl` reader/writer** — the `V:` line spelling is removed. **However**, existing test fixtures and user
  netlists use `V:` for DC bias (e.g. `V:V3 n3 0 Vac=48`). Decision: **map the `V:` reference to `Vdc` at read
  time** so old netlists still load — `CnlReader` rewrites a `V:` instance to reference `Vdc`, taking its DC value
  from `Vdc=`, else `V=`, else `Vac=` (with the 0-Hz warning from Part C). This keeps Hero fixtures working
  without a documented "V" component. (If you'd rather hard-reject `V:` and fix the fixtures, flag it — but
  silent `V:`→`Vdc` remap is lower-friction and matches "no V component to explain.")

## Part B — New `Vdc` component (pure DC source)

A DC-only voltage source — one parameter, no AC.

- **SymbolKind:** add `SymbolKind.Vdc` (find the `SymbolKind` enum in `src/Ui/Schematic`; add the member).
- **Registry:** DisplayName `"Vdc"`, InstancePrefix `"V"`, Category `Sources`,
  SearchTerms `["Vdc","DC","bias","supply","voltage","V"]`, `IsCommon: true`. `EngineReference(Vdc) => "Vdc"`.
  `TryParseCode "VDC" → SymbolKind.Vdc` (and, per Part A, **`"V"` no longer parses** to a component —
  optionally also map `TryParseCode "V" → Vdc` so users typing "V" in the palette search still find the DC
  source; recommended for discoverability).
- **DefaultParameters(Vdc):** `[new("Vdc","0","V", true, UnitDimension.Voltage)]` — the single `Vdc` parameter,
  **displayed on the schematic** (owner: "for the new DC component, the DC parameter IS displayed"). Not
  user-extensible (`UserParamTemplate(Vdc) => null`).
- **Engine model `VdcModel`:** a minimal Group-2 branch source that stamps `Vdc` at ω≈0 and **0 at all other
  ω** (a DC source is an AC short). This is the old `VoltageSourceModel.Stamp` scaffolding
  (`AddBranch`/`AddConstraint`/`AddBranchCurrent`/`AddSourceValue`, keep `LastBranchIndex` for the HB extractor's
  branch-name map) with `E(ω) = (|ω|<OmegaTol) ? Vdc : 0`. Reads `Vdc` (Real or Complex→.Real), default 0; also
  accepts `V` as an alias for `Vdc` (so the Part-A `V:`→`Vdc` remap needs no value-key translation). Register
  `"Vdc" → () => new VdcModel()` in `ComponentModelFactory._registry` (so `IsPrimitive("Vdc")` is true).
- **Symbol:** a battery / DC-source glyph (two unequal parallel bars, or a circle with "−⎓+"/"=" mark). Visually
  distinct from the AC `V_1Tone`/`V_nTone` and `P1Tone` glyphs.
- **2 terminals.** `SymbolPortDefs.For(Vdc)` = 2.

## Part C — Tone sources: Vdc parameter present, hidden by default

`V_1Tone` / `V_nTone` (`ToneSourceModel`) **already** support `Vdc` (the model reads it; see
`CreateToneSourceModel`). Two adjustments:
- **Registry default params** for `ToneSource` (and the n-tone variant): ensure a **`Vdc` parameter exists**
  (default `0`, `UnitDimension.Voltage`) and is **NOT displayed on the schematic by default**
  (`ShowOnSchematic: false`). The user can enable its display via the parameter editor. (Owner: "For any
  V_nTone/V_1Tone, the DC parameter is not displayed on schematic by default; user has the option to enable its
  display.") The existing tone params (`V`/`Freq`/`Phase` or the indexed groups) keep their current shown/hidden
  flags.
- **0-Hz tone guard (shared):** in `ToneSourceModel.Stamp`, the ω=0 branch already uses `_currentVdc`. Add a
  one-time warning when a **tone** entry resolves to `Freq≈0` with a nonzero amplitude:
  `"<inst>: a tone has Freq=0 — use Vdc for DC bias."` Don't drop it; superpose into the ω=0 term (DC = Vdc +
  any 0-Hz tone amplitudes). Route via the netlist warning channel. (This is the precedence rule: at ω=0,
  superpose Vdc + 0-Hz tones; warn that a 0-Hz tone is really DC.)

## Tests (`tests/Engine.Tests` + factory + Ui)
1. **Vdc_Stamped:** `Vdc:Vx a 0 Vdc=48` → DC operating point at `a` = 48 V (the regression proving the bug is
   fixed). `Vdc:Vx a 0 V=48` (alias) → same.
2. **Vdc_IsAcShort:** a `Vdc` source contributes 0 to the source vector at every ω>0 (no spurious AC drive).
3. **LegacyV_RemapsToVdc:** a `.cnl` with `V:V3 n3 0 Vac=48` loads as a `Vdc` instance with 48 V; no
   `SymbolKind.VoltageSource` / `VoltageSourceModel` exists in the build.
4. **NetlistRepro:** the attached topology (Vac-style bias via ideal-L chokes + P1Tone drive) → biased node DC ≈
   the supply value (−3.05 V gate, 48 V drain), not 0.
5. **ToneSource_VdcHidden:** `DefaultParameters(ToneSource)` includes a `Vdc` param with `ShowOnSchematic == false`;
   setting `Vdc` on a V_1Tone biases its node while the tone drives AC.
6. **ToneSource_ZeroHzToneWarns:** a tone with `Freq=0`, nonzero amplitude → one-time warning + the amplitude is
   superposed into the DC stamp.
7. **Vdc_Displayed / Palette:** `DefaultParameters(Vdc)` shows `Vdc`; the `Vdc` component is placeable from the
   palette; `EngineReference(Vdc)=="Vdc"`; no `V` component appears in the palette.
8. **No_V_Component:** `SymbolKind.VoltageSource` is gone; `TryParseCode("V")` resolves to `Vdc` (or returns
   false if you chose not to alias) — assert whichever you implemented.

## Gate
Build 0W/0E (TreatWarningsAsErrors). All Engine + Ui tests green; Hero fixtures still load and run (via the
`V:`→`Vdc` remap). Manual: re-run the attached HB sweep → Vout's **DC** (harmonic 0) reads ≈48 V (was 0.00), the
fundamental still rises with Pin. The palette shows only `V_1Tone`, `V_nTone`, `Vdc` (and `P1Tone`) for sources —
no plain "V". A placed `Vdc` shows its value on the schematic; a placed `V_1Tone` hides `Vdc` until the user
enables it.

## On completion
Note in `src/Core/.../CLAUDE.md` + `src/Ui/CLAUDE.md`: the plain `V`/`VoltageSource` component is **removed**;
voltage sources are now `V_1Tone`, `V_nTone` (tone sources carrying a hidden-by-default `Vdc`), and the new
**`Vdc`** DC-only source (single `Vdc` param, shown on schematic, `VdcModel` stamps DC at ω≈0 and shorts at
ω>0). Legacy `V:` netlist lines remap to `Vdc` on read. At ω=0, tone sources superpose `Vdc` + any 0-Hz tone and
warn that a 0-Hz tone should be `Vdc`. This fixes DC bias silently reading 0 for `Vac=`-style sources.

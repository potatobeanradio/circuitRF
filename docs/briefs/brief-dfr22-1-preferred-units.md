# Brief DFR22-1 — Preferred length and frequency units

**Series:** designer feedback round 22 (2026-10-20) — DFR22-1 units, DFR22-2 artwork → schematic drawing, DFR22-3
layout round trip. Independent of each other; any order. · **Tag:** `R-dfr22-1-<n>`
**Area:** `src/Ui/Theming/AppPreferences.cs`, `src/Ui/Views/Dialogs/SettingsView.axaml(.cs)`,
`src/Design/Workspace/WorkspaceCreate.cs`, `src/Design/Schematic/MicrostripSubstrateInjection.cs` (`LengthUnitFor`),
`src/Ui/ViewModels/WorkspaceViewModel.cs` (new-layout `DisplayUnit`, ~4617 and `WorkspaceViewModel.SchematicToLayout.cs`
~182), `src/Ui/ViewModels/FreqUnitHelper.cs`, `AnalysisSeeding.cs`, the analysis body view models, the Data Display's
`new Plot(…, FreqUnit.GHz)` sites, `src/Ui/Match/MatchDesignerSettings.cs`; `docs/user/src/reference/settings.md`
**Tests:** `tests/Ui.Tests` (a new `PreferredUnitsTests` class), `tests/Firewall.Tests`

---

## 1. Goal

A designer works in one length unit (mm or mil) and one frequency unit (GHz or MHz), and today has to set it again in
every place: each workspace's technology decides the layout and line-parameter unit, analysis dialogs pick a frequency
unit from the value, and the Data Display and Match Designer open in GHz. The owner agreed (round 22) to add a preferred
**length** unit and a preferred **frequency** unit in Settings, used wherever circuitRF chooses a unit for something
NEW, and overridable everywhere exactly as today.

## 2. What exists

- **Length.** `Technology.DefaultDisplayUnit` (`LayoutUnit`: Nm, Um, Mm, Mil, Inch) is the single source today. A new
  layout's `DisplayUnit` copies it; `MicrostripSubstrateInjection.LengthUnitFor(tech)` maps it to the unit a newly
  placed line parameter is written in (PCB → mil, MMIC → µm, else mm). `WorkspaceCreate.Create` copies a shipped
  `.ctech` into the new workspace unchanged. Settings ▸ Technology lists technologies and shows their units.
- **Frequency.** `FreqUnitHelper.Split` picks the "best" unit from the magnitude (auto). Analysis seeding
  (`AnalysisSeeding`) keeps a unit a component already states. The Data Display creates plots with `FreqUnit.GHz`
  (about 34 sites — count them before and after), Match Designer's `FrequencyUnit` defaults to `"GHz"`, and a placed
  part's `F` row defaults to `GHz` in `ComponentTypeRegistry`.
- **Precedent for a "default for new things" preference:** `AppPreferences.DefaultRulerSizeMode` (Settings ▸ General).

## 3. Requirements

**R-dfr22-1-1 — Two preferences.** `AppPreferences` gains `PreferredLengthUnit` and `PreferredFrequencyUnit`, both
nullable; null means **Automatic** — today's behaviour, byte for byte. Stored by NAME (`"Mil"`, `"GHz"`), never by enum
ordinal, and an unknown name reads as null (the `LayoutPartKind.Parse` rule). Choices offered:
- length: Automatic, µm, mm, mil (inch is a `LayoutUnit` but not a schematic Length option — leave it out);
- frequency: Automatic, Hz, kHz, MHz, GHz.

**R-dfr22-1-2 — Settings ▸ General gets a "Units" group** with the two combos, beside the other new-document
defaults. One sentence under them, no more (feedback: panel readouts carry no explanatory notes): "Used for new
workspaces, layouts, analyses and plots; any document can still choose its own."

**R-dfr22-1-3 — A preference is an ARGUMENT below the firewall** (`src/Design/CLAUDE.md`). Nothing in `src/Design`
reads `AppPreferencesIo`. Each place that needs it takes the unit as a parameter, defaulting to today's behaviour:
- `WorkspaceCreate.Create(…, LayoutUnit? displayUnit = null)` writes the copied technology's `DefaultDisplayUnit` when
  one is given. This is how "new workspaces get their technology set to the preferred unit" is honoured; the shipped
  `.ctech` resources are not changed. The GUI's New Workspace passes the preference.
- `LengthUnitFor` stays technology-driven. Because the workspace technology now carries the preferred unit, newly
  placed MLIN/CPWG/SLIN rows follow it with no second code path.
- The CLI: `new workspace` gains `--length-unit um|mm|mil`, default the technology's own (the dialog's default when the
  preference is Automatic). Holds the `Authoring.cs` rule — the verb only parses and passes.

**R-dfr22-1-4 — What follows the length preference (new things only).**
- A **new** layout opens in the preferred unit (both creation sites above).
- New workspace technology, as R-3.
- Existing `.clay`/`.ctech`/`.csch` files are **never rewritten** by a preference change: a stored unit is the user's
  own override. Opening an existing layout keeps its stored `DisplayUnit`.

**R-dfr22-1-5 — What follows the frequency preference.**
- `FreqUnitHelper.Split` takes the preferred unit: with a preference, the coefficient is expressed in that unit
  (`0.1` GHz rather than `100` MHz); Automatic keeps the magnitude rule. A unit already stated by the thing being edited
  (an analysis's own `startUnit`, a component's `F` unit) still wins — that is the override.
- New analyses (S-parameter, HB, loadpull…) seed their sweep/tone units from it when nothing in the design states one.
- New Data Display plots use it for the frequency axis and marker readouts (replace the `FreqUnit.GHz` literals with
  one resolver; a plot that already saved its unit keeps it).
- Match Designer's default `FrequencyUnit`, and the default unit of a newly placed part's `F` row.
- Create Schematic from Artwork's sweep fields display in it (the sweep values themselves do not change).

**R-dfr22-1-6 — Display only, never a value change.** Switching a preference changes no stored number anywhere and no
simulated result. Gate it.

**R-dfr22-1-7 — Docs (sources only, no DocGen — the owner runs it at the end of a series).** `settings.md` gains the
Units group; one line each in the layout-editor and analyses pages that the default comes from Settings.

## 4. Not in this brief

Converting an existing workspace to another unit; per-workspace unit preferences; angle/temperature units.

## 5. Gates

- `PreferredUnitsTests`:
  - preferences round-trip by name; an unknown name reads as Automatic; Automatic reproduces today's
    `FreqUnitHelper.Split` output for a table of values;
  - `WorkspaceCreate.Create` with `Mil` writes a technology whose `DefaultDisplayUnit` is Mil, and without it is
    byte-identical to today's copy;
  - a new layout's `DisplayUnit` and a newly placed MLIN's `W`/`L` units follow the workspace technology;
  - with GHz preferred, a 100 MHz start shows as `0.1 GHz`; an analysis stating `startUnit=MHz` still shows MHz;
  - changing either preference leaves a saved `.csch`/`.clay` byte-identical on re-save.
- CLI: `new workspace --length-unit mil` matches the in-process call byte for byte (extend
  `AuthoringCliVerbTests`).
- `Firewall.Tests` (no preference read below the firewall).
- Run only these classes plus `SettingsView*`, `Authoring*`, `AnalysisSeeding*` tests (memory rule: targeted runs).

## 6. Open for the owner

- Whether Automatic should remain the shipped default (recommended: yes — nobody's output changes until they choose).
- Whether a preference change should offer to update the **open** workspace's technology unit too (recommended: no —
  it is one click in the technology editor, and a silent change to a shared `.ctech` is worse than none).

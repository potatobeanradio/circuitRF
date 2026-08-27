# Sonnet Brief — Localization groundwork: culture correctness, an invariance contract, and coded diagnostics

**Date:** 2026-08-27 · **Status:** specified, not started · **Depends on:** nothing
**Related:** `docs/design/expressions.md`, `docs/design/ui-architecture.md`, `docs/design/cli.md`,
`src/Ui/Messages/`, `tests/Firewall.Tests`

## 0. What is being asked for

Four pieces of groundwork that make circuitRF **localizable later** and that are worth doing on their
own merits **now**. This brief does **not** translate anything, does not add `.resx`, does not add a
language picker, and does not touch the docs.

1. Fix the culture-sensitive number handling that is a live bug today.
2. Write down, and enforce with a test, that the expression language and every file format are
   invariant forever.
3. Pin `en-US` in the test fixtures so the suite stops depending on the runner's OS locale.
4. Give user-facing messages that originate below the UI firewall an **id and typed arguments**
   instead of a baked English sentence.

Items 1–3 are small and mechanical. Item 4 is the only one with a design decision in it, and §7 scopes
it deliberately small — the pattern plus one converted family, not a retrofit of all 131 messages.

## 1. Why this is the right stopping point

A full localization was scoped and priced first: ~2,102 literal strings across 122 `.axaml` files, ~248
message-sink call sites, ~79,000 words of user documentation *per language*, a per-language layout pass
over every dialog, and a permanent maintenance tail. That is not what this brief is.

What this brief is: **the part of that work whose value does not depend on ever shipping a second
language.** Every item below fixes a defect, removes an unstated assumption, or improves the Messages
window regardless. If localization is never revisited, none of this is wasted; if it is, none of it has
to be redone.

## 2. The measured state — read this before planning, it is smaller than it looks

All figures measured 2026-08-27 on `main` at `a983d9d`.

### 2.1 Nothing sets the thread culture. The app runs at OS locale today.

There is no `CultureInfo.CurrentCulture =`, no `DefaultThreadCurrentCulture`, and no
`InvariantGlobalization` anywhere in `src/`, `Directory.Build.props`, or any `.csproj`. The only
assignment in the repo is inside one test that pins invariant around its own assertion
(`tests/Ui.Tests/LiveProgressMessageTests.cs:230`, restored at `:240`).

**So the culture-sensitive paths in §2.3 are not hypothetical and are not gated behind a future
feature.** They are what a user in France, Germany, Sweden or Finland gets right now.

### 2.2 The file formats are already invariant. This is the good news.

Checked writer by writer, not assumed:

| Path | How it stays invariant |
|---|---|
| `.clay`, `.ctech`, `.cem`, `.csch`, `.csym`, `.cws`, `.ccell` | `System.Text.Json` throughout (`LayoutPersistence`, `TechPersistence`, `EmSetupPersistence`, `SchematicPersistence`, `SymbolPersistence`, `WorkspacePersistence`, `CellPersistence`) — JSON numbers are invariant by construction |
| `.cnl` | `CnlWriter` names `InvariantCulture` at 4 sites; `CnlReader` names it at every numeric parse |
| Touchstone, `.spl`, `.lpcwave`, `.gam` | reader and writer both name `InvariantCulture` (`TouchstoneIO`, `SplReader`/`SplWriter`, `LpcwaveReader`/`LpcwaveWriter`, `GamReader`/`GamWriter`, `TouchstoneExporter`, `TsvWriter`) |
| Gerber / Excellon | never converts a `double`: `GerberUnits.FormatCoordinate` is `long.ToString(InvariantCulture)` and `FormatDecimalMm` inserts a decimal point by integer string manipulation |
| DXF | every real goes through the single chokepoint `DxfRecordIo.WriteDouble` → `ToString("G17", InvariantCulture)`; `WriteCoord` is the only place DBU crosses into drawing units |
| `.kicad_pcb` | `PcbWriter.Mm`/`My`/`Deg` name `InvariantCulture`, as do the inline stackup writes |
| `.npy`, `.mat` | binary |

**No known culture leak into any file format exists.** The requirement in §5 is therefore to *keep* it
that way under a test, not to go and fix it.

### 2.3 The real exposure is nine floating-point parse sites

**Correcting an earlier count in conversation.** A line-based `grep … | grep -v Invariant` reports ~21
offenders in `src/Ui`. That number is wrong: it counts calls whose `CultureInfo.InvariantCulture`
argument sits on the *following* line. A brace-matched scan — walk from the opening paren to its match,
then look for a culture in the whole call — gives the true list:

**`src/Ui` — 4 sites, all live bugs:**

- `src/Ui/Layout/StackupLayerRowViewModel.cs:322` — εr
- `src/Ui/Layout/StackupLayerRowViewModel.cs:331` — tan δ
- `src/Ui/Layout/StackupLayerRowViewModel.cs:340` — µr
- `src/Ui/Layout/LayerRowViewModel.cs:159` — fill opacity

All four are `if (!double.TryParse(x, out var v)) { RefreshFromModel(); return; }`. The failure a
comma-decimal user sees is the worst kind: type `4,4` into εr, focus out, **the field silently reverts
to its old value with no message**. Nothing is logged, nothing throws. Note the sibling on
`StackupLayerRowViewModel.cs:349` (σ) already names invariant — so the file is internally inconsistent
and three of its four rows are wrong.

**`src/Cli` — 5 sites**, all in one helper: `src/Cli/Program.cs:1653-1657`, the `GHz`/`MHz`/`kHz`/`Hz`
suffix parser and its bare fallback. Not a locale bug in practice today (an argument parser should be
invariant and nobody passes `2,5GHz`), but it is the CLI's *only* unqualified numeric parse and it
should be pinned for the same reason as the rest.

**`src/Core`, `src/Engine`, `src/Design`, `src/RfCore`, `src/WBond` — zero floating-point sites.**

There are ~50 unqualified `int.TryParse`/`int.Parse` calls in `src/Ui` and ~32 elsewhere. `NumberStyles.Integer`
admits no group separator, so the only culture-varying element is the negative-sign glyph. **Low risk —
note it, do not chase it.**

### 2.4 728 culture-sensitive *format* sites exist, and they should stay that way

Interpolations carrying a format specifier (`$"{x:F2}"`, `{n:N0}`) plus `ToString("F2")`-style calls,
counted by project: Engine 311, Ui 242, Core 71, WBond 36, Cli 34, RfCore 27, Design 7.

Spot-checked across the largest files, **every one of these is display text** — status lines, Messages
entries, mesh and solve diagnostics, DRC violations, export summaries. `PcbExport.cs`'s 17 hits, the
largest cluster in a file with "Export" in its name, are `{shapeCount:N0}` in *progress messages*, not
in the `.kicad_pcb`.

**Do not convert them to invariant.** Display formatting that follows the user's locale is correct
behaviour and is exactly what a future localization wants. The distinction this brief needs to make
enforceable is *display vs. file*, not *invariant vs. not*.

One example of why that distinction matters and is currently unstated:
`src/Ui/Messages/MessageEntry.cs`'s `TimeText` formats `"HH:mm:ss"` with no culture. In a custom format
string `:` is the *culture's time separator placeholder*, so a Finnish user already sees `14.23.05`.
That is arguably right — but it is right by accident, and one test has already had to pin culture in
this area to get a stable assertion.

### 2.5 The message text that crosses the firewall

131 literal exception messages live outside `src/Ui`: Core 61, RfCore 37, Engine 19, Design 8, WBond 6.

`IMessageSink`'s own doc comment says *"the engine itself never calls it directly (it returns a DataSet;
the UI layer reads the result and posts)."* That is true of the *interface* and false of the *text*:
there are **118 sites in `src/Ui`** that interpolate `ex.Message` into a `Messages.Warning`/`Error`
call. The English sentence is authored in the numeric layer and laundered through an exception.

That laundering is the coupling item 4 exists to break — and it is worth breaking for reasons that have
nothing to do with language (§7.1).

## 3. Non-goals — do not do these in this brief

- **No `.resx`, no string externalization, no translation.** Not one `.axaml` literal moves.
- **No language setting, no picker, no `AppPreferences` field.** When it arrives it belongs next to the
  other real prefs in `src/Ui/Theming/AppPreferences.cs`, *not* in
  `src/Ui/DataDisplay/Models/AppSettings.cs`, whose `Load()`/`Save()` are still documented no-ops.
- **No docs work.** `docs/user` stays English, and `_nav.txt` is untouched.
- **No conversion of the 728 display-format sites** (§2.4).
- **No new font assets.** CJK/Devanagari coverage is a real gap in the Skia-drawn canvases
  (`SkiaFonts.PlexRegular`) and is out of scope here.
- **Do not retrofit all 131 messages** in item 4. §7.3 fixes the size.

## 4. R-loc-1 — the nine parse sites become invariant

The four in `src/Ui` (§2.3) and the five in `src/Cli/Program.cs:1653-1657`.

For the four stackup/layer rows, invariant parsing is a **stopgap, not the answer**, and the brief says
so out loud: it makes a German user's `4,4` fail *consistently* rather than differently from the row
below it. The row still reverts silently. **Fixing the silent revert is optional in this brief and
should be recorded as a follow-up if not done** — the honest end state is that a rejected value says why,
the way `DrcRuleRowViewModel` and `SweepAxisRowViewModel` already parse invariantly and the way §7's
diagnostics would let it explain itself.

Do not "fix" the ~50 integer sites.

## 5. R-loc-2 — a Firewall-style test that keeps the file formats invariant

`tests/Firewall.Tests` already fails the build when the non-UI projects reference Avalonia. Add the
locale analogue there, or as a peer, so the invariance in §2.2 stops being a property that happens to
hold and starts being one that cannot stop holding.

**The gate must be behavioural, not a source scan.** A grep for `InvariantCulture` proves nothing (it
cannot see `System.Text.Json`, and `GerberUnits` is correct while naming a culture only incidentally).
The shape that actually works:

> Set `CultureInfo.CurrentCulture` and `CurrentUICulture` to a comma-decimal, dot-grouping locale
> (`de-DE` is the sharpest — comma decimal *and* dot thousands, so a leak is visible in both
> directions). Round-trip a fixture through every format: write, read back, assert numeric equality;
> and additionally assert the **bytes** of the written file match the same write under `en-US`.

Byte equality is the part that catches the leak, because a value that round-trips through *its own*
comma-decimal writer and reader is self-consistent and still unreadable by everyone else. Formats to
cover: `.cnl`, `.clay`, `.ctech`, `.cem`, `.csch`, `.csym`, `.cws`, Touchstone, `.spl`, `.lpcwave`,
`.gam`, DXF, Gerber, Excellon, `.kicad_pcb`, `.npy`, `.mat`, `.txt`/TSV.

Note the ordering trap: this test **mutates process-wide state**, so it needs
`[CollectionDefinition(..., DisableParallelization = true)]` and a `finally` that restores the previous
culture, the way `LiveProgressMessageTests` already does. Getting this wrong makes *other* tests flake
under full-suite load, which per standing project guidance is not something isolated repetition will
ever show you.

## 6. R-loc-3 — the expression language is invariant, stated and gated

`docs/design/expressions.md` gains a short, explicit section, and a test enforces it:

- **Numeric literals use `.` as the decimal separator in every locale.** `1.5e9` parses; `1,5e9` does
  not, anywhere, ever.
- **`,` is the function-argument separator and nothing else.** This is why the rule above is not
  negotiable: `if(a,b,c)` and a comma decimal cannot coexist in one grammar.
- **This holds when the UI is localized.** The expression language is a formal language, like C# or a
  SPICE deck, and does not follow the user's locale. Precedent: every circuit simulator does this.
- **The gate:** the existing expression tests run once more under `de-DE` and produce identical results.

R-loc-4 (below) makes this cheap — pinning the suite means the *default* run no longer accidentally
proves anything about locale, so the `de-DE` pass has to be deliberate.

While here, record the interaction that already bites: a unit suffix is a *row field*, not part of the
expression, which is why `60u` is a parse error in circuitRF. Localization does not change that and
must not be used as an excuse to revisit it.

## 7. R-loc-4 — pin `en-US` in the test fixtures

The suite has 3,420 `Assert…Contains("…")` calls and formats numbers into a large share of its
assertions. With nothing pinning culture (§2.1), an unknown number of them depend on the OS locale of
whatever machine runs them. CI runs Windows, macOS and Linux; today they happen to agree.

Set `CultureInfo.DefaultThreadCurrentCulture` and `DefaultThreadCurrentUICulture` to `en-US` once per
test assembly, in a `[ModuleInitializer]` — process-wide, runs before any test, no per-test cost, and
unaffected by xunit's parallelism. Seven assemblies: `Core.Tests`, `Engine.Tests`, `Ui.Tests`,
`RfCore.Tests`, `WBond.Tests`, `Harmonica.Tests`, `Firewall.Tests`.

**Expect this to turn some tests red, and treat each one as a finding rather than a nuisance.** A test
that only passes because the developer's machine is `en-US` has been asserting the wrong thing. Report
what turned up.

Do this **before** §5 and §6, so those two are measuring what they claim to.

## 8. R-loc-5 — coded diagnostics for messages that cross the firewall

### 8.1 Why, independent of language

The Messages window currently receives a finished English sentence, sometimes wrapped in a second
English sentence (`$"Could not open the {tool.Title} panel: {ex.Message}"`). Because the text is the
only thing that crosses, the UI cannot:

- **filter or group by kind** — "show me every technology-resolution failure" is a substring search;
- **deduplicate** — a sweep that refuses at 400 points posts 400 near-identical lines;
- **attach an action** — a diagnostic that knows it is *"this `.cem` names a layout that is not under
  any workspace"* could offer the walk-up path as a link; a string cannot;
- **be asserted on robustly** — a test pins the sentence, so rewording the sentence breaks the test.

Localizability is the fourth benefit, not the first. **Write the brief's justification that way, and the
work survives a decision never to translate.**

### 8.2 Where the type lives — the one real design decision

The reference graph is:

```
RfCore ──┐                WBond ──┐
         └──> Core <──────────────┘
                ^
     Engine ────┤────> Design ────> (Cli, Ui)
```

`RfCore` and `WBond` are both leaves that reference nothing. **They have no common ancestor**, so no
existing project can hold a type visible to all five message-producing projects.

- **Recommended: a new leaf project** (one file, no dependencies) referenced by `RfCore`, `WBond` and
  `Core`. Costs a `circuitrf.slnx` entry and a `tests/Firewall.Tests` entry, and keeps `RfCore`'s
  charter — Touchstone I/O, network params, `DataSet`/`DataCube`, interpolation, plotting — intact.
- **Cheaper alternative:** put it in `RfCore` and defer `WBond`'s 6 messages. Covers 125 of 131. Off
  `RfCore`'s stated charter, but architecturally harmless: a diagnostic record references no framework.

Either is acceptable. **Name and placement are the implementer's; the constraint is that it references
no UI framework and that `tests/Firewall.Tests` says so.**

### 8.3 Scope — the pattern plus one family, not 131 messages

The minimum that establishes the contract:

1. **The type.** An id (a stable string or enum, e.g. `em.layout.not-under-workspace`), typed arguments,
   and an **English default template** carried alongside. The default template is what makes this
   shippable in one step: the UI renders it today with no resource lookup, and a resource lookup can be
   inserted later at exactly one place.
2. **The rendering point.** One place in `src/Ui` turns a diagnostic into a `MessageEntry`. This is where
   a future `.resx` lookup goes, and it is also where dedup-by-id and filter-by-id would go.
3. **One family converted end to end, as the proof.** Recommended: **the EM run-service refusals**, which
   already have named outcomes (`Refused` / `NoLayout` / `EngineError`) and a documented CLI contract —
   *"a refusal stays a refusal"*, exit 1 with the run service's own sentence, `Cancelled` exits 130.
   That contract is the acceptance test: the CLI's stderr text and exit codes must be **byte-identical
   before and after**, which proves the diagnostic carries everything the string did.
4. **A gate for new text**, so the count goes down rather than up: a source-scan test over the non-UI
   projects flagging newly-added user-facing literals. Needs an explicit allow-list of the 131 existing
   ones so it lands green — and the allow-list is the visible backlog.

**Leave the other 130 alone.** Convert opportunistically when a message is touched for another reason.

### 8.4 The CLI stays English, permanently

Record it here so it is decided rather than assumed: those same diagnostics go to the CLI's stderr, and
a localized CLI error breaks every user's `grep`, log scraper and CI job. **The GUI localizes; the CLI
does not.** The English default template of §8.3(1) is what the CLI renders, always, regardless of any
future language setting. This belongs in `docs/design/cli.md` alongside the existing stdout/stderr split.

## 9. Gates

- `dotnet test tests/Ui.Tests` and `dotnet test tests/Firewall.Tests` green (two invocations — this SDK
  rejects two project paths in one).
- Full-solution `dotnet test` green at the end, run **once**; read
  `tests/<Project>/TestResults/last-run.trx` for any failure rather than re-running.
- The `de-DE` format round-trip (§5) passes, and is demonstrated to actually catch a regression — revert
  one `InvariantCulture` in `PcbWriter.Mm` temporarily and confirm the test goes red. A gate nobody has
  seen fail is not known to be a gate.
- `Cli em` / `Cli sparam` stderr text and exit codes byte-identical across §8's conversion.

## 10. Traps

- **`grep -v Invariant` on `grep -n double.TryParse` lies** (§2.3). Match braces, or you will "fix" a
  dozen call sites that were already correct and miss the four that were not.
- **`de-DE` is a better probe than `fr-FR`** — comma decimal *and* dot thousands, so a leak is visible in
  both directions.
- **Round-trip equality is not sufficient** (§5). A comma-decimal writer paired with a comma-decimal
  reader agrees with itself perfectly and produces a file nobody else can open. Assert bytes.
- **Culture is process-wide state.** Any test that sets it needs `DisableParallelization` and a `finally`.
  Getting this wrong flakes *other* projects under full-suite load, and isolated repetition will never
  show it.
- **`InvariantGlobalization=true` is not the shortcut.** It would make §5 pass trivially and would also
  make a future localization impossible, break `ToString("HH:mm:ss")` expectations, and change collation.
  Do not reach for it.
- **Do not convert display formatting to invariant** (§2.4). Rendering `2,5 GHz` to a French user is the
  goal, not the bug.
- **§8 is not a rewrite.** If it starts touching more than the one converted family plus the type plus the
  render point, stop and report.

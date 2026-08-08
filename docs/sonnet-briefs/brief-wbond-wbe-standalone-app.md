# WB-E — the standalone wBond application, and the Touchstone it has never been able to write

**Phase:** WB-E — `docs/design/wbond.md` §13's own next row, and the one WB-B2's §6 names as next.

**Design authority:** `docs/design/wbond.md` §10 (WB37, the three entry points), §11 (WB38–WB41, the
standalone application), §9.4 (DXF, already shipped — the *other* interchange path). Read those
first; this brief implements them and does not restate their reasoning.

**Predecessor:** WB-A, WB-B, WB-B2, WB-C and WB-D are all complete. **Read `src/Ui/CLAUDE.md`'s
harmonicaRF H8 entry before writing a line of M1, M2 or M4** — that phase solved this exact shape
once already, found five things the hard way, and wrote every one of them down. This brief is
largely "do that again, for a third binary, and do not rediscover any of it."

---

## 0. What this phase is, in one paragraph

wBond stops being a tool inside circuitRF and becomes an application you can hand someone: a third
`Main`, a third `Application`, a plain window per document, its own icon and bundle identity, and a
`.wBond` that opens by double-click. **Almost all of that is a build configuration rather than new
code** — WB38's own framing, and H8 proved it. The one genuinely new *capability* is **Touchstone
export**: §11 requires it, nothing in the repo does it today, and a wBond has never been able to
publish its own network. That is where this phase's real work and its only physics oracle live.

**The scope trap to name up front:** §11 says the standalone has *"every feature of the built-in
tool — including drag-and-drop from a circuitRF project tree."* A project tree implies a workspace,
and a workspace is the thing this binary exists to do without. §5 question 1 settles what that
sentence actually buys; do not build a second project-tree implementation on the strength of it.

---

## 1. What exists, and what you will reuse

Read these before writing anything. Signatures are given so you do not have to re-derive them.

### 1.1 The two-applications-one-assembly machinery — already built, twice proven

```
src/Ui/CircuitRF.Ui.csproj   <CrfApp> = circuitrf | harmonica     ← add 'wbond'
                             <StartupObject> set EXPLICITLY per value
                             CrfValidateApp target errors on a typo
                             per-app MacOSXBundleIcon / MacOSXBundleInfoPlist
src/Ui/Program.cs            circuitRF's Main            (single-instance pipe, --generate-symbols)
src/Ui/ProgramHarmonica.cs   harmonicaRF's Main          (deliberately smaller — read its doc comment)
src/Ui/App.axaml(.cs)        circuitRF's Application
src/Ui/HarmonicaApp.axaml(.cs)  harmonicaRF's Application (StartupFiles, IActivatableLifetime)
src/Ui/Styles/CircuitRfResources.axaml
src/Ui/Styles/CircuitRfStyles.axaml   ← R-h8-6: BOTH Applications include BOTH files, and neither
                                        declares an application-scope style or resource of its own
src/Ui/Views/Harmonica/HarmonicaShellWindow.axaml(.cs)   plain Window, not a Dock document
src/Ui/Views/Harmonica/HarmonicaMenuView.axaml(.cs)      NativeMenu + in-window Menu, one file
src/Ui/bundleForHarmonicaMacOS.sh                        records the publish command IN THE REPO
src/Ui/Assets/macOS/Harmonica-Info.plist                 own CFBundleIdentifier, own icon, UTI import
```

### 1.2 The wBond half — complete, and none of it should change

```csharp
// src/Ui/WBond/WBondDocument.cs
public sealed class WBondDocument : Document                 // a Dock document today
public static WBondDocument Open(string path, string? scratchDir = null);
public void Save(string? path = null, bool embedGeometry = false);
public bool IsScratch => FilePath is null;
public bool HasEmbeddedGeometry { get; }
public WasmResolution ResolveAssemblyRules(...);

// src/Ui/WBond/WBondDocumentViewModel.cs
public WBondViewModel      Editor  { get; }
public WBondPanelViewModel Panel   { get; }
public LayoutEditorViewModel? ReferenceLayout { get; }
public void EnsureReferenceLayout(string scratchLayoutDir);

// src/WBond/  (framework-free, references NOTHING — WB41's own correction)
WBondDesign / WireArray / Wire / LoopProfile / GroundPlane
WBondIo.ReadFile / WriteFile
WirePairSweep, WireGeometry3D, WireTableCsv

// src/Core/Devices/WBondModel.cs   — the physics, reachable from the app
public Complex[] ArrayImpedance(double frequencyHz);   // M×M, row-major, the EXACT reduction (WB19a)
public ArrayReduction InductanceOnly();
public int ArrayCount { get; }
public WBondDesign Design { get; }
```

### 1.3 The RfCore writer you will export through

```csharp
// src/RfCore — the SAME writer every other Touchstone export uses
TouchstoneExporter.Export(...);          // see DataExporterViewModel for a worked call site
RFNetwork.ZToS(...) / SToZ(...);         // and the per-port-Z0 overloads
```

**`tests/Firewall.Tests` already asserts `CircuitRF.WBond` references no Avalonia** — WB41 landed in
WB-A. Do not re-add it; do check it is still green.

---

## 2. The traps — read this section twice

### R-wbe-1 — the THIRD entry point is where "there is only one `Main`" finally bites

`src/Ui` sets `TreatWarningsAsErrors`, so a third `Main` is CS0017 unless **every** `CrfApp` value
names a `<StartupObject>` — including the default. H8 wrote this down as R-h8-5 and it is quoted in
`wbond.md` as WB39 specifically because it bites again here. Extend `CrfValidateApp` too: a typo in
`CrfApp` must be an MSBuild `<Error>`, never a silent circuitRF build.

**The assembly name stays `CircuitRF.Ui` for all three** (WB40). `RfCore`'s `InternalsVisibleTo`
names it; renaming loses `SNP.CreateBroken`/`RefreshFrom` and the Data Display half stops compiling.
The `.app` bundle is what differs, not the binary. H8 tried it the other way and it broke at compile
time — that is the cheap failure; assume nothing about the expensive ones.

### R-wbe-2 — the style/resource superset must hold BY CONSTRUCTION, not by inspection

R-h8-6: neither `App.axaml` nor `HarmonicaApp.axaml` declares an application-scope style or resource
of its own; both include the same two `Styles/` files. `WBondApp` must do the same. **The way this
fails is silent** — omit the ColorPicker Fluent include and `ColorView` renders as an empty box with
no error — so the gate is a structural test over the XAML, not a grep for one `StyleInclude`.

The wBond editor uses more of the shared surface than harmonicaRF does: the **Layout Editor canvas
and renderer**, the **Properties dock's wire-inspector context**, the **DRC panel**, and the
**layer/technology resolution** behind the reference geometry. Every one of those is an
application-scope resource consumer. Enumerate them and assert, rather than launching and looking.

### R-wbe-3 — `WBondDocument` is a Dock `Document`, and the standalone shell has no Dock

harmonicaRF's shell hosts a `HarmonicaView` whose DataContext is a `HarmonicaDocument`; the same
view is a Dock tab inside circuitRF. Do that here: **`WBondShellWindow` hosts the existing wBond view
with an existing `WBondDocument` as its DataContext**, and nothing about the document changes.

But the wBond editor is not one view — it is a **profile canvas above a layout canvas, a docked
panel, and a Properties context**. Decide explicitly whether the standalone reproduces that as a
fixed `Grid` with `GridSplitter`s (harmonicaRF's own answer, and it needs no Dock) or brings Dock
along. **The fixed-grid answer is strongly preferred**: bringing Dock means bringing the whole
tool-window lifecycle — `CrfHostWindow`, the tool-float teardown workaround, the layout persistence
— into a binary that has one document and no workspace, and every one of those is a thing to keep
in step for no gain. If you take the other answer, say why.

### R-wbe-4 — one window per document, and the macOS menu bar is attached PER WINDOW

R-h8-8: several `.wBond` files open as several **windows**, not tabs — with no document shell there
is nothing to tab, and the OS window list *is* the document list. R-h8-11 is the one that costs an
afternoon if missed: **`NativeMenu.Menu` is a per-`AvaloniaObject` attached property, and Avalonia
does NOT fall back to an application-scope menu for a key window that has none.** Attach the same
`NativeMenu` instance to each window. harmonicaRF's `HarmonicaMenuView` does this by a
**type-NAME** comparison against the shell so the view takes no dependency on a type that does not
exist in this build — copy that, including its pinning test, because a rename would otherwise
silently stop the menu bar appearing with nothing failing to compile.

### R-wbe-5 (THE ONE WITH REAL PHYSICS) — what network does a wBond publish?

§11 requires Touchstone export and gives no definition of the network. **State one, defend it, and
gate it against a closed form.** The recommendation:

> **A wBond exports as an M-port, one port per wire array, port *k* being that array's own two
> terminals (`Gk.i`, `Gk.o`).** Its impedance matrix is then **exactly** `WBondModel.ArrayImpedance(f)`
> — by definition, since the array reduction *is* `v = Z_arr · i` in the branch basis. No new
> physics, no new assumption, and the port count matches the schematic symbol's own array pairs.

Two rival readings exist and both are worse: a **2M-port** with every terminal ground-referenced
needs a shunt model the reduction does not provide and the ground plane is the reference, not a
terminal; and a **2-port per array, exported separately** throws away every off-diagonal, which is
the entire content of a coupled bond array. Whichever you choose, **the file must say what its ports
are** — a comment line naming port *k* → array name, the same way `TouchstonePortLabels` reads them
back on the way in. A Touchstone whose port order is undocumented is a file somebody wires backwards.

- **Z → S goes through `RFNetwork`, never a hand-rolled conversion.** Reference impedance is the
  user's choice, defaulting to 50 Ω, uniform and real.
- **The frequency grid is the user's**, prompted at export (start/stop/points, log or linear). Do
  NOT invent one from the design: a bond array is broadband and has no natural band.
- **`ArrayImpedance` is one complex M×M factorisation per frequency** — 55.8 ms at N = 600 wires,
  measured in WB-B. A 201-point export is ~11 s at that scale. Report progress or state the cost;
  do not let a 600-wire export look like a hang.

### R-wbe-6 — the reference geometry is the thing a workspace was providing

Inside circuitRF, a wBond's reference geometry resolves through `CellLayoutResolver` against a
workspace's cells. Standalone there is no workspace, so **the only two designs that open completely
are one with EMBEDDED geometry (§9.1) and one with none**. A design that *references* cells will
resolve nothing.

**That is not a failure and must not be presented as one** — WB35's rule applies unchanged: report
which references could not be resolved, offer to re-point, never silently substitute. The natural
re-point in a workspace-less binary is a folder picker naming the directory those cells live in.
**Do not make the standalone refuse to open a referencing `.wBond`.**

### R-wbe-7 — `.wBond` is not declared to macOS at all yet

Checked: neither `Info.plist` nor `Harmonica-Info.plist` mentions it. R-h8-10's rule — **exported by
exactly one application, imported by the others** — has to be applied from scratch here. circuitRF
ships the type's description (it is circuitRF's format); the standalone states that it understands
it; both open a double-clicked `.wBond`. Two applications both *exporting* one UTI is what Launch
Services cannot arbitrate.

The standalone claims **no** `.crfw`, and no `.charm` — offering an app that cannot open a workspace
in "Open With" for every workspace on the machine is a lie, and H8 already made that call.

---

## 3. Milestones

Each is independently completable and independently gated.

### M1 — the third binary exists

- `ProgramWBond.cs`, `WBondApp.axaml(.cs)`, `WBondShellWindow.axaml(.cs)`; `CrfApp=wbond`;
  `<StartupObject>` named for all three values; `CrfValidateApp` updated.
- `WBondApp` stands up no `WorkspaceWindow`, no `WorkspaceViewModel`, runs no launch action, claims
  no `.crfw`, and registers no `ProcessTechnologyRecognizers`. It **keeps** the `ProcessExit`
  cleanups (`ExternalDeviceRegistry.ResetResolved`, `PCellRegistry.ClearResolvers` — a wBond's
  reference geometry can hold PCells), `ThemeResolver.SetBuiltInProvider`, and the saved theme.
- Startup files: argv on Windows/Linux, `IActivatableLifetime` on macOS, both landing on one method.

**Gate M1:** `dotnet build`, `dotnet build -p:CrfApp=harmonica` and `dotnet build -p:CrfApp=wbond`
all succeed; `-p:CrfApp=typo` fails with the named MSBuild error; the produced binary launches to a
blank wBond editor with no workspace, no project tree and no launch action; a structural test asserts
all three `Application` objects include the same two `Styles/` files and declare no application-scope
resource of their own.

### M2 — it is an editor, not a shell

- File ▸ New / Open… / Save / Save As… / Close Window, plus Import ▸ Wirebond Table… and the DXF
  pair (§9.4) — all of which already exist as commands; this is wiring, not new behaviour.
- Several files open as several windows (R-wbe-4); the macOS menu bar is populated while any of them
  is key.
- **Open a `.wBond` with embedded geometry and see its pads.** Open one that references cells and get
  WB35's report plus a re-point (R-wbe-6).
- Edit ▸ Preferences reaches the wBond defaults block (`WBondDefaults` — points, diameter, material)
  and the theme.

**Gate M2:** the same `.wBond` opens in both binaries through the **same** `WBondDocument.Open`; a
dirty document prompts on window close; Save As re-titles the window and the next Save writes the new
path; a referencing design reports its unresolved cells BY NAME and re-points to a chosen folder;
the profile view, the layout view and the panel are all present and the splitters drag.

### M3 — Touchstone export (the one new capability)

- A dialog: reference impedance, frequency grid, format (RI/MA/DB), and a **read-only port list**
  showing port *k* → array name, so the mapping is visible before the file is written.
- `WBondDesign` → `DataSet` → `.sNp` through `RFNetwork` and the existing `TouchstoneExporter`. No
  second Z→S, no second writer.
- Port identity written into the file as comments, in the form `TouchstonePortLabels` already reads.

**Gate M3:** a **one-array** wBond exports an `.s1p` whose Z at each frequency equals
`ArrayImpedance(f)[0]` to 1e-12 after `SToZ` round-trips it back — a self-consistency check that a
transposed or mis-scaled conversion cannot pass. A **two-array** design's exported `.s2p` reproduces
the full 2×2 `Z_arr` including its off-diagonal, which is the whole point of exporting the array
rather than the wires. And a **closed-form anchor**: at a frequency low enough that R ≪ ωL, the
exported S must match the analytic S of the series-inductance network `InductanceOnly()` describes
— the same free cross-oracle WB19b already established between the editor's fast path and the
simulator's exact one, now extended to the file. Read the `.snp` back with `TouchstoneIO` and compare;
do not assert against stored numbers.

### M4 — packaging and identity

- `wBondIcon.icns` from an SVG in `Assets/artwork/`, `Assets/macOS/WBond-Info.plist`
  (`com.circuitRF.wBond`, own name, own icon), `bundleForWBondMacOS.sh` — which, per H8's own
  M3 gate, **records the publish command in the repository** and checks the plist's
  `CFBundleIdentifier` against its own `BUNDLE_ID`, because that identifier lives in three places
  (plist / script / codesign) and there must be exactly one place the three are compared.
- `.wBond` **exported** by circuitRF's `Info.plist`, **imported** by the standalone's and by
  `Harmonica-Info.plist`? — no: harmonicaRF has no business opening a `.wBond`. Exported by
  circuitRF, imported by the standalone. Both declare `CFBundleDocumentTypes`; circuitRF's role is
  **Viewer**, the standalone's is **Editor**.

**Gate M4:** `dotnet publish -c Release -r osx-arm64 --self-contained -p:CrfApp=wbond` produces a
runnable arm64 binary; the bundle script produces an `.app` with its own icon and identifier;
double-clicking a `.wBond` opens the standalone, and double-clicking one while circuitRF is running
opens it there.

### M5 — the phase gate: one file, two binaries, the same numbers

The value half: build a design in one binary, save, open it **in the other from the file alone**,
and compare — wire count, array membership, every profile binding, the panel's own inductance
readout, and an exported `.snp` compared entry-for-entry. The structural half is what a value
comparison cannot reach: both binaries open a `.wBond` through the **same** `WBondDocument.Open`, so
"two binaries, one file" holds by construction rather than by two implementations agreeing today.

---

## 4. Guardrails

- **Do not change the physics, `WBondModel`'s stamp, the array reduction, or `.wBond`'s format
  version.** Anything new in the file is an additive nullable field written only when set.
- **Do not build a second project tree, a second workspace, or a second cell resolver.** See §5 q1.
- **Do not split `src/Ui`.** WB38 is explicit that this is a build configuration; H8's own §0.2
  measured the alternative and found a ~95 MB .NET-plus-Avalonia floor dominates, so a source split
  buys ~1.6 MB of 138. The standalone is the same size as circuitRF and that is the accepted outcome.
- **Do not rename the assembly** (WB40).
- `src/Engine` and `RfCore` are untouched. `tests/Firewall.Tests` must stay green.
- **No GDSII work**, unchanged from WB-C's and WB-D's own rulings.
- WB-F (kernel W) is not this phase; the fidelity selector stays where it is.

---

## 5. Open questions the owner should settle before or during M1

State the answer you adopted in the completion note either way.

1. **What does §11's "drag-and-drop from a circuitRF project tree" mean in a binary with no
   workspace?** Three readings: (a) the standalone opens a workspace folder read-only purely to
   browse cells as reference geometry — real work, and it reintroduces the concept the binary exists
   to avoid; (b) it accepts a `.clay` or a cell folder through File ▸ Open Reference Geometry… and
   through an OS drag onto the canvas — cheap, covers the actual need, no tree; (c) it is satisfied
   already by embedded geometry (§9.1), which is the "hand a colleague one file" case the standalone
   is *for*. **The recommendation is (b) plus (c)**, with (a) named as its own later decision.
2. **Which network does a wBond publish?** R-wbe-5 recommends the M-port branch basis. Confirm, or
   name the other.
3. **Does the standalone get the DRC panel?** WB-D's assembly rules are workspace-resolvable
   (`CwsFile.DefaultAssemblyRef`) *or* document-local (`WBondDesign.AssemblyRef`). The document-local
   half works standalone with no changes; the workspace default does not. Recommendation: ship the
   panel, resolve only the document's own reference, and say so in the panel rather than silently
   finding no rules.
4. **Windows and Linux packaging.** H8 shipped a macOS bundle script and recorded the two publish
   one-liners for the other platforms without producing installers. Same here, or more?

---

## 6. Known gaps that are NOT this phase

- **WB-F, kernel W** — downstream of `mom-wirebond-kernel.md` LW1; nothing here depends on it.
- **`CouplingDomain`** — v2 by O-3. The audit carries v1, and it now fires from the run (WB-B2).
- **A `.wasm` editor** — deferred by WB-D §5 question 4, unchanged.
- **Tail/stitch land length and reverse-bond allowance** — WB-D reported both as needing a `Wire`
  MODEL change, not a language one. Unchanged.
- **A LENGTH-dimensioned global cannot be parametrically swept, and it fails silently.** Found by
  measurement in WB-B2 and deliberately not fixed there: `Units.BaseUnit("mm")` is `"m"`, and
  `Units._scales["m"]` is `1e-3` — the SI prefix *milli*, not the metre — so
  `ParametricSweepEngine`'s already-SI value is re-scaled by 1e-3 on the way back in; `"mil"` is not
  in `_baseUnitMap` at all and re-scales by 25.4e-6. A loop-height sweep authored in the unit a
  bonder is actually specified in therefore clamps to the wire's own foot drop and produces a
  **plausible flat curve rather than an error**. This blocks WB21, which `wbond.md` calls "the
  feature a PA designer will actually use the tool for." **It is a Core expression-engine defect, it
  is not WB-E's, and it should get its own brief** — the fix probably needs a distinct base symbol
  for length rather than one more row in the map, and it moves every existing length sweep by three
  to five orders of magnitude, so it needs the owner's decision rather than a quiet correction.

---

## 7. Completion note — what to record

Follow the house convention in `src/Ui/CLAUDE.md`: what was built, **what was found**, what was
deliberately not built and why, the gate numbers, and an explicit "not interactively verified" list.

Specifically, record: the answer to every §5 question; **which network a wBond publishes and what it
was gated against**; whether the shared style/resource superset held by construction or needed a
file moved; anything the wBond editor turned out to need from `WorkspaceViewModel` that harmonicaRF
did not (that is the interesting difference between the two standalones, and the most likely place
this phase costs more than H8 did); and — if the third `Main` broke the build in the way WB39
predicts — say so plainly, because a rule that has now bitten twice deserves to be recorded as
having bitten twice.

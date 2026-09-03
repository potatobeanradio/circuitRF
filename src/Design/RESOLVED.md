# src/Design — resolved findings (detail, off the CLAUDE.md growth path)

## Writability is DISCOVERED, and `.cws` writes now have a choke point (2026-09-03)

`brief-shared-library-2-read-only-workspaces.md` R-sl2-1/-2/-3/-6. Two changes here; the behaviour that
hangs off them is in `src/Ui/RESOLVED.md`.

**`WorkspaceWritability` sits beside `WorkspaceRootFinder`, not in `src/Ui`,** for the ordinary reason:
`src/Cli` writes workspaces too and cannot reference Avalonia. It answers "can a file be created in this
directory?" by creating one and deleting it. `File.GetAttributes` reports the DOS read-only bit and says
nothing about a share ACL, a POSIX mode or a read-only mount option; `Directory.Exists` says nothing at
all. **The only portable answer is to try**, which is why there is no cheaper implementation waiting to
replace this one.

Its memo is dropped by `WorkspaceRootFinder.InvalidateCache` rather than on a lifecycle of its own —
that call already drops the ancestor walk-up and `ExternalCellRef`'s alias table, and a third memo that
had to be invalidated separately would be the one that went stale.

**`WorkspacePersistence.SaveToFileAtomic` returns `bool` and is now the guard.** It skips the write and
returns `false` when the containing directory is unwritable. The guard is at the LOWEST level on purpose:
there were fifteen call sites and no choke point (reads have had one — `TryLoadCws` — since the
beginning), and a rule fifteen callers have to remember is a rule that is true in fourteen places. A
sixteenth site inherits it without knowing the rule exists.

**A trap for anyone adding a `.cws` writer:** `SaveToFile` (non-atomic) is still public and is NOT
guarded — it exists for test fixtures and for the doc-fixture generator, which build throwaway workspaces
under a scratch directory. Production code must use `SaveToFileAtomic`;
`ReadOnlyWorkspaceTests.EveryCwsWriteInProductionCodeGoesThroughTheChokePoint` is what says so.

**`FileOptions.DeleteOnClose` is not a crash guarantee on Unix — measured.** It is a kernel flag on
Windows; on Unix .NET emulates it by unlinking at handle close, and a `SIGKILL` closes no handles, so
a process killed mid-probe leaves the file. `Probe` therefore sweeps stale `.crf-write-probe-*` files
(age cut-off five minutes, so a concurrent probe is never touched) rather than trusting the flag. It
matters because the project tree hides only `.DS_Store` and `*.source`, not dotfiles generally.

**A second trap, in the probe's failure mode:** `AtomicFile.WriteAllText` has never created the target
directory, so a `.cws` write into a directory that does not exist used to throw. It now returns `false`
silently instead, because a probe of a non-existent directory answers "read-only" — the same answer, for
the same underlying reason, delivered quietly. A caller that depended on the exception must check the
return value.

## `${NAME}` in a stored cross-workspace path, and why it lives here (2026-09-03)

`brief-shared-library-1-reaching-the-library.md` R-sl1-5/-8. `PathTokens` expands `${NAME}` from the
environment in the three `.cws` fields that name a location OUTSIDE the workspace —
`ReferencedWorkspaces[].Path`, `LibraryRefs`, `KnownFiles` — so a librarian can hand out a starter
workspace whose library reference works on every engineer's machine. One user's `Z:\eda\stdlib` is
another's `\\server\eda\stdlib` and a third's `/Volumes/eda/stdlib`; the alias indirection already meant
each user repaired that once, but a site-wide `.cws` template was impossible.

**It is in `src/Design/Workspace/`, not in `src/Ui`, and that is the load-bearing part of the decision.**
`ExternalCellRef.ResolveOtherRoot` already re-implements `WorkspaceRefs.Resolve`'s rule in three lines
rather than calling it, and its own comment says why: `WorkspaceRefs` is in `src/Ui`, on the far side of
the firewall, and a headless `circuitrf convert` or `em` run resolves these references too. A token
expander sitting in `src/Ui` would resolve a tokenised alias in the GUI and silently fail to in the CLI —
the two would disagree about what the same `.cws` means. Gated by a test that resolves a tokenised
`ws://` reference through `src/Design` types alone.

**Three traps, all of which produce a plausible wrong answer rather than an error:**

- **An unset variable must NOT expand to empty.** `Environment.GetEnvironmentVariable` returns null, and
  substituting empty turns `${CRF_LIB}/stdlib/v2.3/.cws` into `/stdlib/v2.3/.cws` — a ROOTED path that
  resolves to somewhere real on some machines and reports a missing folder on others. `TryExpand` returns
  false with the offending token, callers report a broken reference naming it, and nothing is ever
  half-expanded (an unset token in the middle leaves the whole string untouched).
- **One syntax on every platform.** `${NAME}` only — never `%NAME%`, never bare `$NAME`. A `.cws` travels
  between machines; a per-platform spelling resolves on the machine that wrote it and nowhere else.
- **A `CellRef` is never expanded.** It is the workspace-relative remainder and has no business naming a
  machine — a token there would be a second place a cross-workspace path can hide, which is exactly what
  the `ws://` alias form exists to prevent. `ExternalCellRef.ResolveCellDir` expands the alias's stored
  PATH and leaves the remainder verbatim, in both the `ws://` and the plain relative form.

Nothing ever WRITES a token: circuitRF writes a plain path, and a token is what a librarian or a site
template types by hand — the same treatment R-mw2-5 gives the raw relative `CellRef` (resolve it, never
produce it). There is deliberately no token *definition* mechanism: the environment is where a site
already configures this on all three platforms, and a second definition site would need precedence rules
of its own.

## The interchange stack moved here, and `circuitrf convert` is what it bought (2026-09-02)

The layout interchange readers and writers — GDSII, DXF, Gerber, Excellon and `.kicad_pcb`, ~16,700
lines across 61 files — moved from `src/Ui/Layout/Interchange` to `src/Design/Layout/Interchange`,
namespace and all. The `em` verb's own carve-out (`brief-cli-em-verb.md` R-emcli-1/R-emcli-4) is the
precedent it followed, including the rule that the namespace changes with the project.

The point of the move is the CLI: `src/Cli` cannot reference `src/Ui`, so a headless conversion had
to have the readers on this side of the wall. `src/Ui/RESOLVED.md` §"A headless import verb, and what
moving L4e-L4g to `src/Design` would cost" scoped exactly this and left it unattempted; the numbers
below are what it actually cost.

### 1. What had to move with it, and the one thing that could not

Seven `src/Ui/Layout` files went too, all framework-free as written: `LayoutFragment`,
`LayoutLayerMapping`, `FallbackPalette`, `LayoutViewport`, `PinInference`, `LayoutDesignFlatten` and
`LayoutTextFlatten`. None of them needed an edit beyond its namespace line, and none of their five
other consumers in `src/Ui` needed one either — `src/Ui/GlobalUsings.cs` already carries
`CircuitRF.Design.Layout`, so a type moving INTO that namespace is invisible to every file that used
it. The whole `using` churn across `src/Ui` was one added line for `…Layout.Interchange`.

**`LayoutTextOutline` was the one genuine obstacle, and `src/Ui/RESOLVED.md`'s scoping said it was:
it depends on Skia, so "GerberExport must NOT move".** That prediction was half right. SkiaSharp is
explicitly ALLOWED across the firewall (`tests/Firewall.Tests`: "headless 2D graphics is not a UI
framework"), so glyph geometry crosses fine; what does not is `SkiaFonts`, which loads the embedded
IBM Plex faces through Avalonia's `AssetLoader` and needs a live app host. So the split is not
import-here/export-there. It is **one line lower down**: `LayoutTextOutline` moved with everything
else and gave up only its font SOURCE, now a
`Func<LabelFontStyle, SKTypeface>? TypefaceSource` that `src/Ui` fills in from a `[ModuleInitializer]`
(`UiTypefaceInstaller`) and that falls back to `SKTypeface.Default` when nothing did.

A module initializer rather than a call from `App.Initialize` because `src/Ui` has three entry points
(circuitRF, harmonicaRF, wBond) and a startup step that must run in all three is a startup step
somebody eventually forgets in one.

**The consequence is real and is reported rather than hidden:** a label flattened headlessly is a
different SHAPE from the same label flattened in the app, because the glyph outlines come from a
different face. `LayoutTextOutline.HasEmbeddedTypefaces` is false in that case and `convert` prints a
note whenever it flattened a label without them.

`ResolveLabelAnchor` moved out of `LayoutRenderer` into `LayoutTextOutline` for the same reason it
was shared in the first place: the renderer draws a label with it and the flattener places glyphs
with it, and the property worth protecting is that those two can never disagree. One copy, in the
project both callers reach.

### 2. `convert` is one import and one export, and the intermediate is a real cell

Every reader lands on a cell folder plus a technology and every writer starts from one, so the
N x N table of conversions is not N x N pieces of code. A conversion whose target is `.clay` stops
after the import; every other one runs the import into a scratch directory and exports out of it
(`--keep-cells` keeps that directory, which is the way to see what a conversion understood).

Two things the GUI answers with a dialog had to be answered another way:

- **The layer-mapping dialog** — handed a null callback, every importer already falls through to
  `LayoutLayerMapping.BuildChoices`, which is the same default the dialog pre-selects. Nothing to
  decide; the CLI just does not pass one.
- **The drill-format prompt is a REFUSAL, not a default.** Leading versus trailing zero suppression
  differ by four orders of magnitude on identical text (L4f §2), so `convert` prints the inference,
  its evidence and the artwork cross-check, names the three flags that answer it, and exits 1 having
  created nothing. `--accept-inferred-drill-format` takes the inference as it stands.

### 3. A null destination technology silently drops every layer — measured, not reasoned

The first working conversion wrote Gerber files named `via.G-2_0` from a technology with **zero**
layers. The cause is not in the move: every importer reconciles the file's layers against the
DESTINATION technology and returns the ones it would ADD, and handed a `null` destination there is
nothing to compare against, so `LayersToAdd` comes back empty and the layers arrive as bare numeric
keys with no names, no colours and no `GerberSuffix`. A re-export then names its files from a
synthetic suffix.

The fix is one line — `destTech ??= new Technology { Name = name }` — and the reasoning is that an
EMPTY technology is the honest destination for a conversion that has none: every source layer is then
an unmatched row, which is exactly what it is. **This is the failure mode to remember whenever a
headless caller reuses an importer**, because nothing errors and the result looks structurally fine.

### 4. GDSII is the one format that cannot carry names through, and that is the format's doing

The same fix does nothing for GDSII, deliberately. `GdsiiImport` does not apply the
NoMatch → AddToTechnology default that DXF, board and Gerber import all do (L4b's own divergence, and
it was reasoned about name-keyed formats). GDSII identifies a layer by a NUMBER, so an import has
nothing to name it with: numbers come through exactly, names do not. `--tech` pointing at the
technology those numbers belong to is the answer, and it is documented as such rather than papered
over. The gate asserts a non-empty layer table for every source EXCEPT gdsii, and says why.

### 5. Two smaller things the matrix exposed

- **`$MODEL` is DxfReader's own name for model space**, not something anyone typed, and it reached
  the Gerber writer as a file stem: `$MODEL.gbr`. A DXF's drawing is named after the file, so that is
  what `convert` calls it; `--name` overrides.
- **A `--to clay` result is not shaped the same way for every source.** Gerber import puts its whole
  result inside an `ImportFolder` of its own (R-L4g-13) while the others create cells directly under
  the parent. That is a real difference between the importers, and `convert` does not normalize it
  away — the gate searches recursively rather than pretending otherwise.

### 6. The firewall's text gate fired, and it was right to

23 exception messages appeared "below the UI firewall" the moment the code crossed it — unchanged
sentences that have been in the tree since the importers were written. They are all format invariants
(a truncated GDSII record, a shape type no writer has a case for, an unbalanced macro expression),
which is the deliberate plain-exception case `user-facing-text-allowlist.txt` describes, so they were
added there under a heading that says they moved rather than being authored.

### 7. What the gate proves, and what it does not

`tests/Ui.Tests/ConvertCliVerbTests.cs`, 32 tests, 7 s, untagged and in the routine gate. It launches
the built `CircuitRF.Cli.dll` as a real process (EmCliVerbTests' pattern, for its recorded reason) and
checks all 24 ordered format pairs plus byte identity against the in-process `GdsiiExport` and
`GerberExport` calls the GUI's own File ▸ Export makes.

**A GDSII file is not byte-comparable raw**, and the first version of this gate only looked like it
was: BGNLIB and BGNSTR record when the library and each structure were written, so two writes of the
same design differ at byte 21 unless they land in the same second. It passed for an afternoon and
then failed on a second boundary. Masked by record type and named, the way `EmCliVerbTests` names the
Touchstone provenance line — everything else still compares byte for byte, which is the point.

**It proves the two sides agree and nothing more.** The matrix's sources are built by `convert`
itself, so a pair is tested against our own writer's output, not against a third party's dialect —
the same limitation L4h's round-trip gate states about itself (R-L4h-16), and for the same reason.

**Stale after this change:** the root `CLAUDE.md` source map still describes `src/Ui` as the home of
the layout interchange code and lists seven CLI verbs. Neither is true now. Left for the owner.

## MIM-7 — a dielectric that is patterned with its plate, so ONE MMIC technology serves both (2026-08-30)

`docs/sonnet-briefs/brief-em-mim-7-one-technology.md`. The extraction half is here; the shipped-file
merge, the editor row and the documentation are `src/Ui/RESOLVED.md` §MIM-7. **`src/Engine` is
untouched — the refusal, the via z-integral and the kernel are exactly as they were.**

### The premise that was actually wrong

circuitRF shipped two MMIC technologies that differed only by a capacitor module, and MIM-2 measured
two real reasons for the split (`src/Ui/RESOLVED.md` §MIM-2): a capacitor dielectric between the
interconnect metals makes every Metal1-Metal2 airbridge post cross a dielectric interface — which
`PlanarKernel.CanSolve` refuses for the WHOLE RUN — and it sits on a Metal1 line as superstrate, so
Z₀ falls 2.8%.

**Both costs come from the film being in the medium of EVERY run, including runs with no capacitor in
them — and the 2.5D premise does not require that.** It forces "laterally infinite per RUN"; it says
nothing about which runs a patterned film belongs to. Physically the nitride exists under the plates
and nowhere else, and the honest per-run proxy for "this run has capacitors in it" was already being
computed: **is the plate conductor among the run's levels?** The extractor's default level selection
is "every non-ground conductor that carries artwork", so an interconnect-only layout answers no with
no configuration at all. It is also the kernel's own suggested remedy, verbatim in its refusal text:
*"…or remove the interface if it carries no physics."*

### The field, and the two halves of the rule

`StackupLayer.PresentWithLayer` (`string?`, a conductor entry's NAME) — additive, nullable, no
`.ctech` `FormatVersion` bump, meaningless on a non-Dielectric entry: the `SheetAt`/`SpanFromLayer`
pattern. `TechValidation` requires an existing, non-ground Conductor and refuses the field on a
non-Dielectric entry. **"Name the conductor directly ABOVE" is a RECOMMENDATION, not a validation
rule** — a tie further away is expressible and honoured, only harder to read — so it is stated in the
field's own documentation, the editor's tooltip and the user page rather than failed.

When the named plate is not in the run:

1. the film's band enters the medium as **air** — εᵣ 1, tanδ 0, µᵣ 1, thickness untouched, so every
   band above it keeps the height the process states;
2. **`SheetAt = Top` on the conductor whose band sits directly BENEATH the film is treated as unset
   for that run.**

**(2) is what makes the gate bit-identity rather than "close", and it is not a convenience.** MIM-6
put Metal1's sheet on the top of its band expressly so a plate gap reads 0.2 µm; with no film there
is no gap to read, and the pre-MIM-6 placement is the established baseline for interconnect. Without
the revert the same airbridge would extract at z = 103/106 instead of 100/106 — a plausible answer,
3% out, to a question about a stack with no capacitor in it.

**A tie naming a conductor the stackup does not have leaves the film ACTIVE**, with a note. The other
choice would let a typo silently thin the medium, which is the failure the mechanism exists to
prevent. It is not a refusal, because the extraction is still a valid one — validation is where the
typo is called an error.

### BOTH extractors read it, and finding that was the one surprise

The brief named `PlanarExtractor`. Implementing only that left nine `Ui.Tests` failures, four of them
the acceptance tests: **`CrossSectionExtractor` builds its own layered medium from the same stackup**,
so a film left switched on there is exactly MIM-2's second cost — measured, not argued:
`Mmic_LineOnMetal1_...` came back at Z₀ 48.25 Ω against the hand-built 49.62, and the 72 µm line's
ε_eff at 8.54 against a (6, 8.5) band. Both pass with the tie honoured there too.

Its version of "in this run" is a set of one: a uniform-line cross-section refuses multi-level
geometry outright, so the question is "is the plate THE signal conductor". There is no sheet surface
to revert — that kernel models real metal of real thickness and never reads `SheetAt` (MIM-6's own
recorded decision).

So the rule lives in one file, `Em/PatternedDielectric.cs`, against this area's standing rule that
the two extractors restate the stackup rules rather than call each other. **That rule is about the
cross-section extractor's REDUCTION test and its refusals**, which must never appear on the planar
acceptance path. This is the opposite shape: one paragraph of policy and one sentence of user-facing
text — and the sentence is the reason. Two copies of the "your medium lost a layer" note would drift
into two accounts of one decision.

Mechanically both callers rebuild rather than patch: deactivating changes materials and z, and the
bands already in hand are re-resolved by their stackup INDEX, which the rebuild preserves. The
`Technology` object the caller passed in is never mutated (it is a live document, re-extracted at
every frequency of a sweep) — the affected entries are cloned field for field.

### The gates

- **`MimCapacitorTests.AnAirbridgePost_SolvesOnTheOneTechnology_AndExtractsIdenticallyToTheModuleFreeStack`**
  — the brief's own gate, and it flipped a test that asserted the refusal. Level names, every level z
  and thickness, every medium region's thickness/εᵣ/tanδ/µᵣ, the slab, the via's indices and its
  footprint areas: all compared with `Assert.Equal` on doubles, no tolerance. The comparison
  technology is DERIVED from the shipped one by removing the module, not restated.
- **`MimCapacitorTests.TheCapacitorRun_IsWhatTheRetiredSecondTechnologyProduced`** — the ACTIVE side,
  as literals captured from `MmicGaAsMim()` before the merge, because the object they came from no
  longer exists. 103 / 103.2 / 106 µm, medium 103 µm εᵣ 12.9 | 0.2 µm εᵣ 6.8 | 2.8 µm air, the plate
  via 1→2 at 3.6e-11 m².
- **`PatternedDielectricTests`** — the mechanism on a probe technology built in the test, so the
  assertions are about the rule rather than about what circuitRF happens to ship: both extractors,
  the note, the broken tie, named analysis levels overriding artwork, and the schema half
  (validation, `.ctech` round trip and absence when unset, merge conflict description, editor row).

`dotnet test tests/Ui.Tests` 10,364 passed / 0 failed; `tests/Firewall.Tests` 10/0.

### What did NOT come out bit-identical, and why it cannot

Two measured residuals, both outside the brief's stated gate and both stated rather than tuned away:

- **A Metal2 line's CLOSED-FORM substrate is 102.75 µm instead of 103** (−0.24%), with ε_eff a shade
  higher. `SubstrateResolver` sums dielectric bands and has no notion of an analysis level, so it
  cannot ask the tie's question — and teaching it would not close this anyway: skipping the film
  gives 102.55 µm, further away. The missing 0.25 µm is the plate METAL, and no closed-form path
  counts a metal band. Pinned in
  `MimCapacitorTests.TheClosedFormPathDoesNotReadTheTie_AndTheOnlyCostIsAMetal2LineBy025Micron`.
- **A run whose LOWEST analysis level is Metal2 gets a sizing εᵣ of 9.78 instead of 9.58** (+2.1%).
  `slabBands` sums the dielectric bands under the lowest level; the deactivated film is still a
  0.2 µm dielectric band and the plate's 0.25 µm is a conductor band, which that sum never counts —
  the same structural gap as above. Since MIM-4 the slab is a SIZING object only (calibration-standard
  geometry, the β seed, the near-radius floor, the mesh), never the published reference impedance.
  A Metal1-fed run — every de-embedded one, until MIM-4's ports move — is unaffected: its slab is the
  GaAs alone, bit for bit.

## MIM-4 — the stratified sub-feed refusal, retired (2026-08-30)

`docs/sonnet-briefs/brief-em-mim-4-interior-static-greens.md`, gap 4 of the MIM series. The engine
half is `src/Engine/Mom/RESOLVED.md` §MIM-4; what changed HERE is `PlanarExtractor`.

**What was refused.** More than one dielectric entry between the ground plane and the lowest analysis
level: *"L9's Green's function handles a stratified medium happily — what does not is the
de-embedding … Merge the layers under the feed into one substrate entry, or wait for a static Green's
function at interior heights."* That merge was **a change to the physics offered as a workaround** —
two dielectrics in series under a trace are not one dielectric of either εᵣ — and the only reason for
it was that `C_pul` came from an image series over one grounded slab. MIM-4's
`InteriorStaticImages` removes the reason.

**What it does now.** The layers are carried at their stated thicknesses (`BuildMediumStack` always
built them; nothing there changed), and a note replaces the refusal.

**Two things worth keeping.**

1. **The `GroundedSlab` is now a SIZING object where the region is stratified, and the right average
   for that job is the series-capacitance equivalent** — `h/ε_eff = Σ d_i/ε_i`. It still sets the
   calibration standards' geometry, the branch-continuation β seed, the accelerated near-radius floor
   and the mesh; none of those is the published reference impedance any more. It reduces to the single
   layer's own εᵣ, bit for bit, when there is one, and it is what a wide line over the real stack
   converges to: 21.3% / 10.3% / 3.2% / 1.1% difference from the true stratified `C_pul` at
   W/h = 0.5 / 2 / 8 / 24. The note says out loud that the number is for sizing and never for the
   reference impedance — a number the user can see and misread is exactly the shape of thing that
   gets trusted silently.
2. **A stratified medium turns the general kernel on at ONE level too.** The explicit `MediumStack`
   used to be attached only when `levels.Count > 1`. Before this brief that was sufficient — a
   stratified region under the lowest level was refused, and with one level there is nothing above it
   in the stack, so a one-level problem was always one dielectric. Carrying the layers without also
   changing this would have handed L8's one-slab kernel a stack it does not describe:
   `generalMedium = levels.Count > 1 || mediumStack.LayerCount > 1`.

**Held by** `tests/Ui.Tests/Em/StratifiedSubFeedExtractionTests.cs` — it extracts, both layers reach
the medium, the sizing slab is the series equivalent while the medium is not, the note carries the
layer names and says what the effective εᵣ is and is not for, and a ONE-dielectric region is
unchanged (one layer, the slab's own material bit for bit, no note, and still the one-slab kernel
path).

## MIM-6 — the level reference surface: a conductor's sheet learns which surface of its band it sits on (2026-08-30)

`docs/sonnet-briefs/brief-em-mim-6-level-reference-surface.md`, the fifth gap of the MIM series —
MIM-2's own finding 1. Extraction only; `src/Engine` untouched (a level's `ZM` is already arbitrary
there), and kernel A's cross-section path untouched (it models real metal thickness and has no sheet
to place).

### The problem

`PlanarExtractor` placed every conductor level's zero-thickness sheet at the BOTTOM of its stackup
band and absorbed the band's own z range into the dielectric ABOVE it. Both rules are right for
everything that came before — together they are what makes a microstrip's height come out as the
substrate thickness. Between two capacitor plates they are wrong: the lower plate's whole metal
thickness lands INSIDE the gap. The shipped MIM technology extracted its levels at
z = 100 / 103.2 / 106 µm, so the solver saw a **3.2 µm plate separation where the process states
0.2 µm — 16×** — with the whole 3.2 µm carrying the capacitor dielectric's εᵣ.

Not fixable by authoring: the gap is `Metal1.Thickness + MIMDielectric.Thickness`, `TechValidation`
requires a positive thickness on every band, and Metal1's sheet was pinned 100 µm above ground by
the microstrip case.

### The shape, and why the two halves are ONE choice

`StackupLayer` gains `SheetAt` (`ConductorSheetSurface?` — `Bottom`/`Top`), additive and nullable,
no `.ctech` `FormatVersion` bump, meaningless on a non-Conductor entry: the `Fill`/`SpanFromLayer`
pattern already in `TechModel.cs`.

**The absorption direction is not a second setting — it follows the surface, and that pairing is
load-bearing.** `PlanarProblem.CanSolve` refuses a level that is not on an interface of its own
medium (L9c's first earned refusal). Sheet at the bottom + band absorbed upward puts the sheet on an
interface; sheet at the top + band absorbed downward puts it on an interface. Either half alone does
not: a sheet moved to the top of its band while the band still went to the dielectric above would
land 3 µm inside a region, and every MIM extraction would refuse.

`Band` gained a `SheetM` alongside `BottomM`/`TopM`, and every z decision in the file — level z, the
ground-band query, the slab height, the slab-band window, the medium's cut set, `topOfInterest`, the
level ordering, the ungrounded refusal's "dielectric under this level" — reads `SheetM`.
`BottomM`/`TopM` stay the band's own extent, which is what the absorption arithmetic and the
conductor's reported `ThicknessM` are written in. **`SheetAt` chooses where the sheet is, never how
thick the metal is.**

`BuildMediumStack`'s absorption is one added branch: when an interval's midpoint is inside no
dielectric, find the CONDUCTOR band it is inside and ask its surface — `Top` takes the dielectric
whose top is this interval's bottom, anything else keeps the pre-existing "dielectric whose bottom is
this interval's top". That is why `Bottom` and unset are bit-identical rather than merely equivalent:
the old expression is still literally the else branch.

### The gate, measured

On the shipped MIM technology (`Metal1 = Top`), the series capacitor:

| | Before | After |
|---|---|---|
| Levels (Metal1 / MIM Metal / Metal2) | 100 / 103.2 / 106 µm | **103 / 103.2 / 106 µm** |
| Region between the plates | 3.2 µm at εᵣ 6.8 | **0.2 µm at εᵣ 6.8** |
| Region under Metal1 | 100 µm GaAs | **103 µm GaAs** |
| Slab height | 100 µm | 103 µm |

The airbridge post's kernel refusal is UNCHANGED and was re-measured, not assumed: the post now runs
z = 103 → 106 µm rather than 100 → 106, and it still straddles the plate dielectric's upper interface
at 103.2, so `PlanarKernel.CanSolve` refuses it by the same sentence with a different z in it.

### The notes now name the surface, and that is not decoration

A level at 103 µm on a conductor whose band runs 100–103 is either a mistake or a deliberate
reference-surface choice, and the run notes are the only place a user can tell which — the panel's
own stackup readback is bound to the CROSS-SECTION readback, which a full-wave run does not produce.
The multi-level note now reads `103 µm (top of 'Metal1'), 103.2 µm (bottom of 'MIM Metal'), …`.

### `SubstrateResolver` is deliberately NOT taught the field — the decision, with its measurement

The closed-form microstrip path sums dielectric thicknesses. On the MIM technology it and the EM
extractor now disagree about a Metal1 line's substrate by exactly one metal thickness: **100 µm
against 103**. Measured cost of teaching it the field instead: a 70 µm line's static Z₀ would go
**49.42 → 50.06 Ω, +1.3%**.

**Not taught, because the two numbers answer different questions.** Hammerstad-Jensen models real,
finite-thickness metal and takes that thickness as its own parameter `t`; its h is the physical
substrate — ground plane to the underside of the metal — which is what the process states. The
extractor's h is where a ZERO-thickness sheet was placed, a discretisation position rather than a
dimension. Feeding the sheet position to the closed form would count Metal1's 3 µm twice and move
every Metal1 microstrip on this technology to agree with a discretisation artifact. The discrepancy
is bounded by one metal thickness by construction and the run prints the number it used. Recorded in
`MimCapacitorTests.AMetal1Microstrip_ResolvesDifferentlyOnTheTwoTechnologies_ButAgainstTheSamePlane`,
which asserts both heights side by side so the divergence stays deliberate.

### What this brief does NOT claim

**No capacitance accuracy.** A 0.2 µm gap against micron cells is exactly MIM-3's unmeasured regime;
this fixes the geometry so MIM-3's ladder measures the real one. The raw-solve gate is still a
with-via/without-via comparison carrying no magnitude band (|S21| 3.92e-4 with the plate via against
1.49e-4 without, ratio 2.64 — re-measured at the new geometry), because a raw port's own
discontinuity dominates any absolute number, which is MIM-2's finding-2 retraction.

### Tests

`tests/Ui.Tests/Em/SheetReferenceSurfaceTests.cs` is the mechanism: unset ≡ explicit `Bottom` as a
WHOLE-EXTRACTION identity (levels, medium regions, slab, vias, polygon count and every note) over all
three shipped technologies; clearing the shipped `Top` restores 100 / 103.2 / 106 exactly; a purpose-
built stackup where the intervening dielectric is THINNER than the metal below it, so the absorption
direction is visible rather than a rounding difference; `SheetAt` on a non-conductor entry ignored;
`.ctech` round trip (and absent from the file when unset); the merge clone and its conflict
description. `MimCapacitorTests` carries the shipped technology's own numbers.


## MIM-1 — region vias: drawn via artwork beyond the point `ViaShape` (2026-08-30)

`docs/sonnet-briefs/brief-em-mim-1-region-vias.md`, gap 1 of the MIM series. Extraction and
reporting only; `src/Engine` untouched, and every §7 via refusal still fires unchanged.

### What was wrong, and why it was silent rather than refused

`PlanarExtractor`'s classification loop recognised a via-bound drawing layer in exactly one place:
inside its `if (s is ViaShape)` branch. Every other shape fell through to `binding`, the layer→z-band
map — and `BuildStack` builds that map from **non-Via entries only**, because a via contributes no
thickness and has no z band of its own. So a rectangle or polygon drawn on a via layer missed the map
and landed in `ignoredOther`.

**The counter it landed in is what made the failure worse than a drop.** `ignoredOther`'s note says
the shape is *"not bound to a stackup conductor or via entry"* — which is exactly the wrong advice
for artwork on a layer that IS bound, and sends the user to the technology editor to redo something
already done. The same silence swallowed a drawn backside-via slot or bar.

This is the same map-vs-branch split that made `BuildVias` unreachable at L9's phase gate. It is
worth stating once more: the two bindings answer different questions (where a layer sits in z, versus
which two conductors a via joins), keeping them apart is right, and the cost of keeping them apart is
that every new shape kind has to be routed to the second one deliberately.

### What it does now

- **A filled region on a via-bound layer becomes a `PlanarVia` footprint**, through the conductor
  path's own shape→`PlanarPolygon` conversion — outer ring plus holes, the layout's own flatten
  tolerance, the same degenerate-ring floor. Reused rather than restated: a via footprint and a
  conductor footprint are resolved onto the same tensor grid, and two conversions that could drift
  apart would show up as a via meshing to a slightly different set of cells than the metal it lands
  on.
- **The footprint is NOT squared.** The equal-area square (side = 0.886 × drill) exists so a round
  barrel *nobody drew* does not contribute a hard gridline per facet. A drawn outline already is the
  footprint, so it goes to the mesher as it stands.
- **Span, conductivity and the ground rule come from the stackup entry**, identical to the point
  path, and a region via participates in the same `noSpan` / `unknownLevels` / `notAdjacent` /
  `toGround` / `wrongGround` accounting — counted in SHAPES, because a shape is what the user drew
  and can go and look at.
- **Nothing on a via-bound layer falls into `ignoredOther` any more.** A `PathShape` there gets its
  own sentence (a centreline encloses no area; draw the region), and so does a region that flattens
  to nothing.

### The one design decision worth the words: regions are GROUPED PER STACKUP ENTRY

Every region on one via entry becomes **one `PlanarVia` carrying several footprint polygons**, not
one `PlanarVia` each. The obvious reason is that the span, conductivity and ground rule all come from
the entry, so per-shape vias would be N identical records. The real reason is correctness:

`SurfaceMesher` scans every grid cell against a via's polygon list and **stops at the first polygon
that covers it**. Two overlapping footprints inside one `PlanarVia` therefore give a shared cell
**one** vertical basis. As separate `PlanarVia`s they would give it **one each**, silently doubling
the vertical current in the overlap — and a plate connection drawn as two overlapping rectangles is
an ordinary thing to draw, not a corner case. `TwoOverlappingRegions_GiveTheirSharedCellsOneVerticalBasisEach`
pins it as a counter that is independent of the mesh pitch: no cell index appears twice,
and the meshed footprint is the union (60 × 40 µm) rather than the sum (2 × 40 × 40 µm).

**The same hazard exists on the point path and was left alone**, deliberately — it is pre-existing
behaviour, changing it would move existing runs, and the brief forbids touching the point path. It
was *measured* while sizing the structural gate below: a 2 × 2 array of nominally touching point
vias overlaps by 0.37 nm (see the next section), and the meshed footprint comes out at
1600.0591 µm² against the true union — i.e. the overlap strip is counted twice, exactly as the
first-cover argument predicts.

### The structural gate, and why it compares AREA rather than a basis list

The brief asks that a region via covering the cells of an N×N array of touching point vias yield the
same vertical basis functions. **That cannot be a basis-list comparison, and asserting it would be
asserting something false.** L9c's own mesher finding is that a via footprint must contribute HARD
gridlines or the via vanishes silently — so N×N touching footprints put N−1 interior gridlines per
axis into the shared tensor grid that one large footprint does not. Those lines *subdivide* the
covered cells; they do not move the covered boundary. Measured: 943 unknowns (4 vertical) for the
single region against 943 (4 vertical) for the drawn 2 × 2 array on this fixture.

The grid-independent statement of the same claim is **the plan-view area the vertical bases cover**
— still a cell counter (one basis per covered cell, summed over the cells' own areas), never an
S-parameter. The gate is in two halves:

| | Fixture | Claim | Result |
|---|---|---|---|
| A | 2 × 2 drawn squares vs one drawn rectangle over their union | covered area equal, **to the bit**; the single footprint needs no more unknowns | 1600 µm² both, N = 943 both |
| B | 2 × 2 point vias vs the same region | covered area equal to the equal-area square's own DBU rounding | −3.6926 × 10⁻⁵ relative, **predicted exactly** |

**Half B's discrepancy is predicted rather than bounded**, which is the part worth keeping. A point
via's square is 0.886 × drill and a drill is an integer number of DBU, so the square that gets meshed
has side s′ ≠ the nominal s and the array covers n²s′² against the region's (ns)². On this fixture
s′ − s = +0.37 nm, so nominally touching point vias in fact *overlap*, and 1 − (s′/s)² reproduces the
measured area difference to 12 decimal places. If the two ever disagree by anything that rounding
does not account for, one of the two paths has a real defect. (The overlap also costs N: 1096
unknowns with 16 vertical bases, against the drawn array's 943 with 4 — a sub-nanometre sliver run,
and a good illustration of why the point path snaps nothing.)

### Milestone 4's assumed paths, checked rather than assumed

- **A Via stackup row binds a drawing layer and states its span** — real, `ShowsDrawingLayerPicker`
  is `Kind == Via` and a Conductor row deliberately does not show that control (it binds through the
  layer table). Verified against a live `TechEditorViewModel` over the MMIC starter, including that
  the picker's option list actually contains the layer the extractor keys on.
- **A rectangle drawn on that layer reaches the extractor** — real, and is now the main body of tests.
- **`EmDiagnostics`' via count includes region vias** — **this path does not exist.** `EmDiagnostics`
  is the EM run service's REFUSAL family (`em.run.cancelled`, `em.layout.not-found`, …); it has no
  via counter and no counter of any other extraction quantity. The via count a user actually sees is
  carried in the run's NOTES, which `EmRunService` concatenates from the extractor and the mesher.
  Nothing was built: the smallest version is a test that both note sources count a region via, which
  is what `TheRunsOwnViaCount_IncludesRegionVias` asserts. Growing a diagnostic for a *quantity*
  would be the first non-refusal member of that family and is a decision for whoever converts the
  next family, not a side effect of this brief.

### Tests

`tests/Ui.Tests/Em/RegionViaExtractionTests.cs`, 13 methods, all routine tier (~70 ms). Point-via
bit-identity is asserted with `BitConverter.DoubleToInt64Bits` against the documented rule restated
in the test, not read back from the object under test.

The terminal resolution (`SpanFrom`/`SpanTo` → a level pair or one of five counters) is now a single
local function both artwork kinds call. That is not tidiness either: "the artwork says WHERE, the
stackup says WHICH TWO CONDUCTORS" only holds if the answer cannot depend on how the via was drawn,
and a second copy of that block is exactly how it would stop holding.

## R-em-4's ground query returns null for TWO reasons, and the note claimed the wrong one (2026-08-30)

`PlanarExtractor` resolves the EM ground as **the highest ground-designated conductor BELOW the
lowest analysis level**. When that query comes back empty and `Stackup.Bottom == Ground`, it fell
back to the bottom of the stack and said:

> No conductor layer in technology 'X' is marked as a ground reference, so the ground plane was taken
> from Stackup.Bottom = Ground at the bottom of the stack.

**That sentence is only true for one of the two ways to reach it.** The query is scoped to
conductors *below the signal*, so it also returns null on a stackup that HAS a designated ground
sitting *above* — and there the message is flatly false, contradicted by a ticked checkbox on the
Stackup tab the user is looking at.

**It survived because no shipped technology could reach the false branch.** Every PCB starter was
2-layer and the MMIC's ground is its backside metal, so the only ground candidate was always the
bottom conductor: "none below the signal" and "none at all" were the same statement. The first
technology with an INNER ground plane (`pcb-4layer_FR-4_62mil_1oz`, added the same day) made them
different, and a trace on a lower layer was told its technology designates no ground at all. Worse
than the wording: the run **succeeded**, solving against a reference further away than the real one,
so there was no refusal to prompt anyone to look.

The fallback now asks which case it is and names the planes it did find, says why they cannot serve
(a port returns through a plane BENEATH the conductor it feeds), and states the cost — the reading
will be a higher impedance than the real structure. The original sentence is kept verbatim for the
genuinely-undesignated case.

### Two neighbouring messages were wrong in the same way — advice for a situation that was not this one

- **The zero-height slab refusal** said *"Check the stackup order in the technology editor."* The
  commoner way to arrive there is a correctly-ordered board whose BOTTOM conductor is being treated
  as the signal: it rests on the `Stackup.Bottom = Ground` boundary, so the slab has zero height and
  nothing is misordered at all. That case is now named, with the two things that actually help
  (mark it as a ground reference, or move the trace up a layer).
- **The no-signal-conductor refusal** said *"Draw the artwork on a conductor layer, or bind the layer
  it is on to a conductor entry."* When every shape is on a ground-designated conductor the layer IS
  bound — the advice sends the user to redo something already done. Reachable on any stackup with
  more than one plane (a 4-layer board whose only artwork so far is an inner pour), so it now says
  the plane is not meshed and points at the "Ground reference" tick.

**None of the three was found by reading the extractor.** They were found by running it on each
conductor of the new 4-layer technology in turn and printing the result — a scratch xunit probe, run
once and deleted. A message can only be checked against the state that reaches it.

Gated by `tests/Ui.Tests/Em/FourLayerGroundReferenceTests.cs`, which drives the extractor rather than
scanning source, and includes the negative: the 2-layer starter must reach neither new branch.

## A board outline refused the EM run, and the dielectric binding was the workaround (2026-08-30)

User proposal: remove the dielectric's "Drawing layer" control from the `.ctech` editor, since the
binding is never used except under the hood. **The premise was wrong and the conclusion was right,
for a reason neither of us had.**

### What the binding actually did

Nothing electrical: `PlanarExtractor.BuildMediumStack` reads only `Epsr`/`TanD`/`Mur`/`ThicknessDbu`
and every dielectric is a laterally infinite slab, so a dielectric bound to `(none)` is everywhere.
Every other consumer filters it out — `WBondClearance` reads `DrawingLayers` only after
`if (sl.Kind != StackupKind.Conductor) continue`, `PcbLayerNaming`/`DrcConnectivity`/`GerberExport`
take conductors and vias, `PcbWriter` writes dielectric thickness with no layer reference, and in
`PlanarExtractor` a dielectric-bound and an unbound shape reach the same `ignoredOther`.

Its ONE effect was in `CrossSectionExtractor.Classify`, and it was not subtle. Measured on the MMIC
starter with a Metal1 trace plus a die outline on `Substrate`: binding kept → `Ok=True` with a note;
binding removed → **hard refusal.** The field was the difference between the run working and failing.

### The defect underneath: the refusal fired on the normal case

Sweeping every layer of the shipped 2-layer PCB starter, one shape at a time beside a solvable trace:

| Layer | Result |
|---|---|
| Top Copper, Bottom Copper, Drill | Ok |
| Soldermask Top / Bottom, Silk Top / Bottom, **Outline** | **REFUSED** |

**Every PCB layout has a board outline**, so the failing case was the normal one — and the refusal's
advice was *"add this drawing layer to a conductor entry's DrawingLayers list"*, i.e. declare your
board outline to be copper. The dielectric-`DrawingLayers` binding was a narrow escape hatch from
this, applied only where the MMIC starter tripped over it.

### The discriminator was available and was not being asked for

A layer the technology **declares** but binds to no stackup entry is the technology stating the layer
is not metal. Silk, soldermask and outline are exactly that. A layer the technology **does not
declare at all** — a foreign import, a hand-edited file — is the case nobody has said anything about,
and there the original reasoning holds in full.

So the refusal is narrowed, not deleted: declared-but-unbound is ignored with a note that names every
distinct layer once and still offers the fix (*"If one of them IS metal, bind it to a conductor entry
on the Stackup tab"*); undeclared still refuses, now pointing at the Layers tab rather than telling
anyone to call it copper. Ignoring is REPORTED, never silent — a trace genuinely drawn on a forgotten
layer is still visible in the run's own output.

With the workaround unnecessary, the editor's dielectric picker is gone
(`StackupLayerRowViewModel.ShowsDrawingLayerPicker`, via only). **The model field stays**: shipped and
user `.ctech` files carrying a dielectric binding still parse, validate, round-trip through
`TechnologyMerge`, and take their original more-specific "substrate extent" note — removing a control
must not rewrite anyone's file. `IsSingleDrawingLayer` is deliberately left answering `true` for a
dielectric, because the CARDINALITY rule did not change.

Gated by `tests/Ui.Tests/Em/UnboundLayerArtworkTests.cs` (10 tests), including both halves that make
this safe rather than merely permissive: the MMIC die outline extracts with the binding removed, and
a file that still carries one behaves exactly as before.

**None of this was visible by reading the extractor.** It came from running it on each layer in turn
and printing the verdict — a scratch xunit probe, run once and deleted. The same method found the
ground-reference bug above. A refusal can only be checked against the state that reaches it.

## A laterally-finite dielectric cannot be drawn, because the kernel cannot represent one

Asked while the section above was being investigated: how does a user simulate a MIM cap built on a
GaAs substrate, if the dielectric is always everywhere? Surely the nitride must be drawn on a layer.

**It cannot be, and no binding would have helped** — this is a formulation limit, not a missing
feature. `BuildMediumStack` produces a `LayerStack` of `MediumLayer(thickness, material)`: a 1-D
stack of laterally infinite slabs, and the DCIM Green's function is derived from exactly that stack.
Unknowns live on conductor surfaces and via barrels only. A nitride island under a top plate needs
either volume-equivalent currents inside the dielectric (a VIE) or a surface-equivalence formulation
on its boundary, and neither exists in `src/Engine`'s planar kernel.

Drawn dielectric geometry is therefore ignored — before the change above it fell into
`PlanarExtractor`'s `ignoredOther`; it is now named in the declared-but-unbound note. Reported, but
inert either way. Note also that the MMIC starter's own `Cap Dielectric` and `Nitride` drawing layers
are bound to no stackup entry at all: they are artwork/DRC/GDS layers, and their presence must not be
read as EM support.

What actually works, best first: a **lumped C in the schematic** (C = ε₀εᵣA/d from the process's
capacitance density, with the EM run covering the interconnect around it — the normal MMIC flow); or
**stating the inter-metal dielectric as nitride** in the stackup and meshing both metal levels, which
gets the plate overlap right out of the solve but puts every airbridge and crossover in the same run
in nitride instead of air; or **splitting the run**, EM for the passive interconnect and lumped caps
combined in the schematic.

## Union was quadratic in the operand count, which made the Gerber importer's own advice unusable (2026-09-03)

`LayoutBooleans.Combine` folded every boolean **linearly**: `acc = acc op operand[i]`, one full
Clipper2 `BooleanOp` per operand against an accumulator that had already absorbed everything before it.
For Intersection, Difference and Xor that shape is required — Difference is not commutative, so those
operands must be applied in selection order. For **Union** the same shape is pure cost: operand N is
clipped against a result carrying N-1 operands' worth of contours, so the total is quadratic.

**This is not a theoretical complaint — the codebase routes users into it by name.** `GerberImport`
tells anyone importing a vector-filled pour that their layer "arrived as N separate strokes ... use the
editor's Merge action to turn them into one region before setting up EM ports". On an owner-supplied
4-up RF panel that is **46,721 strokes on one copper layer**, and Union on it ran for **over forty
minutes without finishing** (killed, not completed). The advice was not actionable on the exact file
class that triggers it.

Union is associative, so it is reduced as a **balanced tree** — `(A∪B)∪(C∪D)` rather than
`((A∪B)∪C)∪D`. That is a change of ORDER, not of semantics, and every step stays a real pairwise
`BooleanOp` between two already-resolved regions.

| | 76,517 operands, 10 layers |
|---|---|
| linear fold | >45 min, never finished |
| balanced tree | **9.8 s** |

Result: 76,517 shapes collapse to 2,478 (top copper: 47,530 strokes → 190 polygons).

### The obvious faster version is WRONG, and a test caught it

The first attempt was the one-call form: concatenate every operand's `Paths64` into a single subject
set and resolve it in one `BooleanOp(Union, all, empty, NonZero)` — which is exactly what `Repair`
already does for one self-intersecting shape. It is 40 s, still a huge win, and it produces the wrong
answer for any operand carrying a hole: **under NonZero a hole contour from one operand cancels another
operand's fill where they overlap**, so a union that should have closed a hole punches one instead.
`PcbImportTests.ACustomPad_IsOneUnionedRegion_IncludingEachFilledPrimitivesPen` failed immediately —
one region came back as two. Union two resolved regions at a time; never a raw pile of contours.

### Merging a hatched pour is the right move for the MODEL and does not make rendering faster

Worth stating because the import message implies otherwise. The unioned panel renders **slower** than
the unmerged one (240 ms vs 126 ms/frame at Zoom-to-Fit), because a hatched pour's union has a
comb-shaped boundary: 2,478 shapes carrying **308,326 outer vertices plus 771,663 hole vertices**, one
polygon of 12,335 vertices with 15 holes. The artwork really is that complicated; the strokes were
hiding it in a form Skia happened to rasterize cheaply. Merge for editability and for a meshable
conductor — which is what the import message actually claims — not for frame rate.

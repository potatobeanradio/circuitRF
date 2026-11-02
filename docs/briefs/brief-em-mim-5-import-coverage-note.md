# Brief MIM-5 — technology import: say what the via list cannot reach, and document the hand-add

**Problem.** A process stack description often omits optional modules — a MIM plate, its thin
dielectric, its plate via — while the accompanying layer table still lists their drawing layers.
`ProcessTechnologyBuilder` imports the stack faithfully and already notes dangling via spans and
undrawn vias, but it says nothing about the opposite gap: a CONDUCTOR that no via in the file can
reach, or a layer-table row that hints at structure the stack never states. A user who imports such
a kit gets a valid technology that silently cannot express the capacitor their process actually
offers, and no pointer to the two-minute fix.

**What can honestly be said.** The importer cannot detect the absence of something the file never
mentions — no guessing, no name matching (the builder's own header: "Nothing here names a process,
a supplier or a tool"). It CAN state two structural facts and one remedy:

1. **Reachability**: after `BuildVias`, list every non-device conductor that is an endpoint of no
   via entry (the topmost conductor is exempt from "unreached from above"; the bottom-of-stack
   conductor from below). Wording: connectivity the file does not state, not an error.
2. **Layer-table orphans**: drawing layers the layer table defines that no stackup entry binds are
   already imported as plain layers; add one summarising note naming them, so "my capacitor layers
   imported but do nothing" is answered by the import report itself.
3. **The remedy, in the user docs**: a short "adding a thin-film (MIM) capacitor to an imported
   technology" section — three stackup rows in the technology editor (a thin dielectric: thickness
   and εr from the process's capacitance density, εr = C″·d/ε₀; a plate conductor bound to the
   plate drawing layer; a Via entry bound to the plate-via drawing layer with its span set), with
   generic silicon-nitride-class example numbers and the laterally-infinite-dielectric
   approximation note, consistent with `docs/design/mom-engine.md` §10.12.

Read first: `src/Ui/Layout/TechImport/ProcessTechnologyBuilder.cs` (`BuildVias`'s existing
`dangling`/`undrawn` notes — the new notes follow their exact voice); `ProcessStackReader.cs`
(what the format can and cannot state); `tests/Ui.Tests/Layout/TechImport/`; the user docs source
under `docs/user/src/` and its verification fixtures.

## Milestones

1. The reachability note and the orphan-layer note, with tests: a stack whose via list reaches
   every conductor produces neither note; removing one via entry produces note 1 naming exactly
   that conductor; a layer table carrying two extra rows produces note 2 naming exactly those.
2. The user-docs section, and the import dialog's existing notes surface confirmed to show the new
   notes (no new UI — the notes list already renders).
3. A cross-pointer from the import notes to the docs section, so the note is actionable in one hop.

## Must NOT

- Match on names, invent stackup entries, or "complete" the stack on the user's behalf — the
  import stays faithful; only the REPORT grows.
- Name any foundry, vendor, kit or tool — in notes, tests, fixtures or docs. Fixture files are
  invented processes with invented names (the existing test fixtures' pattern).
- Turn either new note into a warning/error state — they are ordinary notes, same standing as
  `dangling`/`undrawn`.

## Gates

Milestone 1's tests; docs build/verification green; `dotnet test tests/Ui.Tests` green. Write-up
in `src/Ui/RESOLVED.md`.

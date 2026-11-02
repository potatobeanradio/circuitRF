# Sonnet Brief — Importing a PDK writes nothing into the workspace

Today `Import PDK` translates a vendor kit into real cell folders under `<workspace>/pdk/<kit>/`. Make it an
**in-memory** operation instead: the workspace records a *reference* to the kit and the decisions circuitRF
made about it, and every translated artifact — symbols, parameter interfaces, palette icons — is rebuilt in
memory when the workspace opens.

Gate command is plain `dotnet test`.

---

## 0. Scope — what this achieves, what it does not, and one standing rule

**R-pdk-1. No vendor or product names anywhere this work touches — code, comments, tests, fixtures, log
output, or documentation.** circuitRF is vendor-neutral, and a kit's own identity is *data it supplies at run
time*, never something written down in the product. This is the same principle already load-bearing three
times over in this area: the ELF symbol-table scan instead of a compiled-in name list, the PE import walk
instead of a remembered module name, and the runtime alias map instead of a compiled-in table.

Concretely: no manufacturer, part number, model-family name, formulation name, EDA-tool name, or library
filename from any kit. Test fixtures use synthetic names (`SampleKit`, `PART_A`, `KITLIB_DEVICE_v1`,
`TYPEA`/`TYPEB`) — a kit is recognised structurally, by the entry points circuitRF's own worker calls, so a
synthetic fixture exercises the real rule. Where a real measurement has to be cited, cite the number and not
the kit. Gate 2 enforces this by scan.

**This fixes a share path that does not currently work.** `<workspace>/pdk/<kit>/` is only 40 KB, but its
`.ccell` and `device-provider.json` are full of **absolute paths** — the kit root, the model library, the
part's icon, its netlist, and the worker command. A colleague opening a shared workspace today gets a symbol
that renders and everything else dangling, failing quietly until they press Run. Sharing is already broken;
this makes the kit an explicit, repairable dependency instead of a set of silent dead paths.

**It does NOT stop a shared workspace from carrying vendor parameter data, and that is accepted.** Instance
parameters are copied into the schematic at placement and persisted there — this is how instance overrides
work for every component, not a PDK quirk. A `.csch` that places a kit part already contains, in shape:

```
CellRef: ../../pdk/SampleKit/PART_A_MODEL
Params : [('ModelAs','TYPEA'), ('TAMB','-1'), ('RTH1','1E-06'), ('CTH1','1E-07'),
          ('RTH2','2E-06'), ('CTH2','5E-08')]
```

This brief removes the **symbol geometry** from the workspace. It does not remove the part name, the kit
name, or the parameter values. **Do not add scope to chase that** — say so plainly in the completion note so
nobody later reads it as an oversight.

**No migration.** circuitRF is alpha; a workspace built by the current code is not supported across this
change and needs its kit re-imported. Do not write a conversion path, and do not read or delete an existing
`pdk/` folder.

---

## 1. What `.cws` records — the reference and the decisions, never the content

**R-pdk-2. Persist what circuitRF *decided*; rebuild what it *translated*.** An import both translates
(symbols, parameter interfaces, icons) and decides (which of a dozen library builds, which variant is the
default, which host module a Windows build imports from). The translations are the leak and are rebuilt in
memory. The decisions are tiny, carry no geometry, and are the difference between a workspace that opens the
same way twice and one that quietly re-decides — so they are recorded.

Per referenced kit, `.cws` records: the kit path, the provider name, the resolved model-library path per
platform, the chosen variant defaults, and R-pdk-4's translation version. Nothing else.

**R-pdk-3. Store the kit path relative when it is inside the workspace tree, absolute otherwise — and
normalise separators.** This is exactly `WorkspaceRefs`' existing rule for Touchstone references; reuse it
rather than writing a second path-storage convention. A kit is normally *outside* the workspace, so the
absolute branch is the common case here — which is fine, and is precisely why R-pdk-13's repair flow has to
be good.

**R-pdk-4. Pin a translation version per kit reference.** `DsnSymbolReader` snaps every pin to the P=100
connection grid. If that reader ever changes — a scale fix, a snap fix, anything touching pin placement —
re-derivation moves pins, and **wires attached to them silently disconnect**. Today the frozen `.csym` is
what prevents this; in-memory removes that protection, so the version pin replaces it. See R-pdk-12 for what
a mismatch must do. **Design this in from the start; it cannot be retrofitted once designs exist.**

---

## 2. A kit part's `CellRef` becomes explicitly virtual

**R-pdk-5. Give kit parts a virtual reference form (e.g. `pdk://<kit>/<part>`), not a relative path that
happens not to resolve.** A missing kit and a mistyped path must be distinguishable — otherwise every
"is this cell reachable" check has to guess which it is looking at, and the repair flow in §6 cannot tell the
user anything useful. A virtual ref is also what lets a kit part be placed on an **unsaved** schematic:
`CommitCellPlacementAsync` currently refuses, because `CellRef` is computed relative to the schematic's own
directory. Removing that restriction is a welcome consequence, not a separate feature.

**R-pdk-6. Resolution goes through the existing funnel, with an in-memory branch — do not add a second
resolver.** `CellSymbolResolver.Resolve(cellRef, baseDir)` is already the single static entry point, with its
own cache. Add the in-memory lookup there, mirroring the two patterns this codebase already has for exactly
this shape: `CellLayoutResolver.SetLive`/`ClearLive` and `TechnologyCache`'s separate `_live` dictionary
checked ahead of the file-backed one. A separate dictionary rather than a value inside the existing cache, for
the same reason `TechnologyCache` uses one: dropping the override must fall back cleanly without forcing a
disk read of a file that was never there.

**R-pdk-7. The `.ccell` side is scattered and is the bulk of the work — converge it.** Symbols are
centralised; parameter interfaces are not. `.ccell` is read directly by parameter seeding at placement, by
`PdkPartInstaller.LoadInstalled` at every workspace open, and by `CellFolder.ResolvePrimary`. Every one of
those must resolve a virtual kit part from memory. Expect this, not the symbol work, to be where the time
goes.

**R-pdk-8. A kit part is not the user's cell and stops appearing as one in the Project Tree.** Today
`pdk/<kit>/<part>/` shows up as ordinary cell folders. With nothing on disk they simply vanish, which is the
correct outcome — but it is a visible behaviour change, and it is why §6's dialog is required rather than
optional.

---

## 3. Loading on workspace open

**R-pdk-9. Silent on success.** A workspace open re-reads and re-processes every referenced kit. It must
produce **no import report and no per-part messages** when everything resolves. The report moves to §5's
explicit action.

**R-pdk-10. Read the recorded decisions; do not re-derive them.** This is what keeps the open fast and
deterministic. Measured: parsing is negligible (two symbol files of a few KB, netlists of 11 KB
and 6 KB, out of a 17 MB / 46-file kit), while **library discovery alone is ~62 ms**, because it byte-scans
candidate builds across a separate multi-MB package. Re-discovering per kit per open is the only part with a
cost worth caring about, and R-pdk-2 already records the answer. Re-derive only when the recorded decision no
longer resolves.

**R-pdk-11. Budget: a 20-symbol kit loads in under 100 ms — and a miss is a STOP, not a slower number.**
This is the whole bet of the design. If a workspace open cannot re-derive a kit inside that, the in-memory
approach is paying a cost the on-disk one did not, and that is a decision for the owner, not something to
optimise around quietly or to write up as "acceptable". **If the measurement does not clear 100 ms, stop,
report the number and where the time goes, and ask.** Do not tune, do not cache extra artifacts to disk, and
do not relax the budget.

Measure against a **synthetic 20-symbol kit fixture** built for this (R-pdk-1 — no kit in the test
suite), warm, excluding first-JIT, and report the median. The number that matters is per-kit load, not
whole-workspace open.

For context on why this should clear comfortably: symbol and netlist parsing is negligible above, and the
~62 ms is **library discovery**, which R-pdk-10 removes from the open path entirely. 20 symbols should be
nearer 10 ms than 100 ms. If it is not, something is being re-derived that R-pdk-10 says should have been
recorded — look there first, then stop and ask.

**R-pdk-12. A translation-version mismatch is refused and reported, never applied silently.** When the
recorded version (R-pdk-4) differs from the current reader's, the kit does **not** silently re-translate.
Report it, keep the design openable, and make the upgrade an explicit action the user takes — because the
thing on the other side of that upgrade is pins moving under placed wires.

---

## 4. Broken references

**R-pdk-13. A broken kit reference is a first-class, repairable state — never a silent failure and never a
blocked open.** A workspace whose kits are absent must still open, with its kit parts drawn as the existing
`NotFound` placeholder. This is the same rule as R-dock-5 for layouts and R-fgn-* for foreign documents: the
user's design is their data; a missing dependency degrades, it does not deny.

**R-pdk-14. One summary on open, not one message per part.** A kit with forty parts must not produce forty
warnings. Report per *kit*: which references are broken, and the one action that fixes them.

**R-pdk-15. Details go to a log file in the workspace; Messages carries the summary and a clickable path to
it.** `Messages.Post` already takes a file path and renders it as a link — reuse that, do not invent a second
reporting channel. The log is a diagnostic artifact, not project state: it is overwritten per load, and it
must never be something a user has to clean up or that changes whether the workspace opens. R-pdk-1 applies
to its contents.

---

## 5. `Validate PDK` — the report, on demand only

**R-pdk-16. The import report becomes an explicit action, not a side effect of opening a workspace.**
`Validate PDK` re-reads a referenced kit, re-runs the full analysis including the parts an ordinary open skips
(R-pdk-10's decisions get genuinely re-derived here), and shows the existing `PdkImportReportDialog`. Reuse
the existing report type and dialog; this is a new entry point to work that already exists, not a second
report. Its only entry point is §6's dialog — one place to reach it, not a menu item as well.

**R-pdk-17. Validate reports drift, not just breakage.** A recorded decision that no longer matches what the
kit now offers — a different library build present, a variant gone — is the interesting output. A kit that
merely resolves is a one-line "no problems found".

---

## 6. `File ▸ Manage PDKs…` — the management dialog

**R-pdk-18. One dialog, listing every referenced kit with its status.** Reached from a new `File ▸ Manage
PDKs…` item, enabled only with a workspace open. Per kit: name, path as stored, resolved/broken, part count,
translation version. Four actions:

| Action | Behaviour |
|---|---|
| **Add…** | Folder picker; imports the chosen kit and adds the reference. The ordinary import path — this dialog is a second entry point to it, never a second implementation. |
| **Remove** | Drops the reference (R-pdk-21). |
| **Reveal** | Opens the kit folder in the platform file manager (R-pdk-19). |
| **Validate** | Runs §5 on the selected kit. |

**The File menu is maintained by hand in three places** — the in-window `Menu`, the macOS `NativeMenu`, and
`TornOffFileMenuView`'s own copy. Add the item to all three; that is this codebase's existing convention, not
an oversight to fix here.

**R-pdk-19. Reveal reuses the existing platform-aware implementation, and its label follows the platform.**
`WorkspaceViewModel.Reveal` already does `open -R` / `explorer /select,` / `xdg-open`, and `RevealLabel`
already yields "Reveal in Finder" / "Reveal in Explorer" / "Reveal in File Manager". Reuse both. Do not write
a second reveal, and do not hard-code "Finder" in a label. Reveal on a **broken** reference is disabled with
a stated reason (R13a) — there is nothing to open.

**R-pdk-20. Repairing a reference must not disturb a design that uses it.** Whether the repair is `Remove`
then `Add…` or an in-place path edit, the virtual refs in every `.csch` are keyed on the kit *name*, not its
path — so re-pointing re-resolves every placed part with **no schematic edit**. That is the whole reason
R-pdk-5 makes the reference virtual rather than a path; say so at the implementation site.

**R-pdk-21. Removing a reference while parts are placed is allowed, warned, and reversible.** It does not
delete anything from any schematic; those parts become broken references (R-pdk-13) until the kit is
re-added. Name the count of affected instances in the confirmation.

---

## 7. Guardrails

- No vendor or product names in anything this work produces (R-pdk-1) — including test fixtures and log output.
- **Stop and ask if the 100 ms budget is missed (R-pdk-11)** — do not tune around it, do not relax it.
- Do not prevent a workspace from opening because a kit is missing (R-pdk-13).
- Do not report the import on every workspace open (R-pdk-9).
- Do not silently re-translate across a reader change (R-pdk-4/R-pdk-12) — pins move and wires disconnect.
- Do not add a second symbol resolver, a second reporting channel, a second reveal, or a second import path
  (R-pdk-6, R-pdk-15, R-pdk-19, R-pdk-18).
- Do not write a migration path, and do not read or delete an existing `pdk/` folder — alpha, re-import instead.
- Do not extend scope to stripping vendor parameters from `.csch` — §0 accepts that deliberately.
- Do not write a per-user disk cache of translated artifacts. It was considered and rejected: it keeps the
  freeze that R-pdk-4 replaces, but it puts a translation back on disk, which is the thing being removed.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

---

## 8. Gate

1. Builds green; `dotnet test` green; no existing test regresses.
2. **No vendor or product names (R-pdk-1)** — a scan over every file this work adds or changes finds no
   manufacturer, part number, model-family, formulation, EDA-tool or real library name. Assert it as a test,
   not a review step, so it holds for whatever is added next.
3. **Nothing is written (R-pdk-2)** — import a kit into a fresh workspace, then assert **no `pdk/` directory
   exists** and the workspace tree contains no file derived from the kit. Assert the directory's absence, not
   merely that parts are placeable.
4. **Round-trip** — import, place a part, save, reopen: the part resolves, renders with its real symbol, and
   carries the same parameters. Assert the rendered symbol, not just that the component exists.
5. **`.cws` carries decisions, not content (R-pdk-2)** — assert the saved `.cws` names the kit, the resolved
   library, the variant default and the translation version, and contains **no** symbol geometry and no
   parameter defaults.
6. **Path storage (R-pdk-3)** — a kit inside the workspace tree stores relative with `/` separators; a kit
   outside stores absolute. Move the whole workspace folder: the inside case still resolves.
7. **Silent open (R-pdk-9)** — open a workspace with a resolving kit and assert **zero** messages are posted.
   This is the test that catches the report leaking back in.
8. **Decisions are not re-derived (R-pdk-10)** — instrument library discovery with a counter; assert an
   ordinary workspace open runs it **zero** times, and that `Validate PDK` runs it.
9. **Load budget (R-pdk-11)** — a synthetic **20-symbol** kit loads in **under 100 ms** (median, warm,
   first-JIT excluded). Report the measured number either way. **If it misses, stop and ask the owner** —
   do not tune, do not relax the threshold, do not ship a slower number as acceptable.
10. **Translation-version mismatch (R-pdk-12)** — hand-edit the recorded version, reopen: the kit is reported
    and **not** silently re-translated; the design still opens.
11. **Broken reference opens (R-pdk-13)** — move the kit aside, reopen: the workspace opens, placed parts draw
    the `NotFound` placeholder, and the design is still editable and saveable.
12. **One message per kit, not per part (R-pdk-14)** — a broken kit with several parts posts exactly one
    summary.
13. **Log file (R-pdk-15)** — a broken load writes the log and posts a message whose path resolves to it.
    Assert the file exists and the message carries the path.
14. **Validate reports (R-pdk-16/17)** — `Validate PDK` on a healthy kit reports no problems; on a kit whose
    library has been moved it names the drift.
15. **Menu and dialog (R-pdk-18)** — `File ▸ Manage PDKs…` is present in **all three** File-menu surfaces and
    disabled with no workspace open; the dialog lists every referenced kit with its status and offers Add,
    Remove, Reveal and Validate. **Add… goes through the ordinary import path** — assert that, not just that
    a reference appears.
16. **Reveal (R-pdk-19)** — the label matches the platform, and Reveal on a broken reference is disabled with
    a reason.
17. **Repair leaves designs untouched (R-pdk-20)** — break a kit path, repair it through the dialog, and
    assert every placed part resolves again **with no edit to any `.csch`** (compare the file bytes before
    and after).
18. **Remove reference (R-pdk-21)** — removing a referenced kit warns with the affected instance count, leaves
    every schematic unmodified, and re-adding the kit restores the parts.
19. **Unsaved schematic (R-pdk-5)** — a kit part can be placed on a never-saved scratch schematic.

---

## 9. On completion

**`docs/design/pdk-import.md` is the durable home for this design and already describes it** — §4–§10 there
are written from this brief. On completion, update that doc's **Status** line from "designed, not built" to
shipped, correct anything the implementation resolved differently (say what and why — do not silently
rewrite the design to match the code), and fill in §7's measured load figure against the 100 ms budget.

Then record the short version in `src/Ui/Schematic/CLAUDE.md`, as the changelog entry: that an import writes
**nothing** into the workspace and what `.cws` records instead; **R-pdk-2's split** — decisions are persisted, translations are rebuilt — and that this is
what keeps the open both fast and deterministic; **R-pdk-4/R-pdk-12**, that the translation version exists
because re-derivation moves pins and disconnects wires, so it must never be bypassed for convenience; that a
kit part's `CellRef` is virtual and keyed on kit *name*, which is what makes repairing a reference a no-op for
every design that uses it; **R-pdk-1**, that a kit's identity is run-time data and never appears in the
product, with the scan test as the standing guard; and **§0's accepted limitation** — a shared workspace still
carries the vendor's parameter names and values through the schematic itself, deliberately, so it is not
re-litigated later as a bug.

Also record the measured load figure from gate 9 against the 100 ms budget, and the pre-existing one this
brief rests on: ~62 ms for library discovery per kit, against negligible symbol and netlist parsing.

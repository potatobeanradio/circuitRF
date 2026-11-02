# Sonnet Brief — RC-10: one history panel

**Read `brief-revision-control-0-overview.md` first.** The architecture is
`docs/design/revision-control.md` **rev 6** — §5.10 (the merge, the row, the expander, the glyph),
§5.8's new *way forward* rule, §5.6 rule 6's fourth kept kind, §12 Q32, Q34 and Q35. **Depends on RC-5,
RC-6, RC-7 and RC-9** — every panel it merges is built and every piece of data it displays is already
recorded. **Supersedes RC-7 R-rc7-9.**

**Scope: presentation, and one keep rule.** This brief writes nothing new to a repository, changes no
message, and adds no boundary. If a change here alters what is stored, it is out of scope and the
brief is wrong — §5.10 rule 6.

**Why it exists.** Stages 1–4 shipped two panels, and the second one was never argued for: §5's
argument is about two *kinds of entry* and rev 2 spent it as an argument for two *windows*. The reason
actually given for the split, in the shipped user documentation, is that a list of three hundred
automatic entries with four deliberate ones among them is unreadable — **which is an argument for a
filter**, and a filter is strictly better, because the default view can be the four deliberate entries
while the designer never has to know which panel to open. That question is harder than it looks at the
moment it is asked, which is the moment something has gone wrong.

---

## 1. One panel

**R-rc10-1. One dock panel, titled History, replacing Restore Points and Versions.** Not *Versions*:
that word has a precise meaning in §5.2 — titled, permanent, travels with a clone — and it is the word
that distinguishes the two kinds of row. A panel in which everything is a version has no word left for
the distinction, and a designer who learns these are all versions and then finds half of them did not
arrive with a clone (§5.2a) has been told something false.

**R-rc10-2. The two kinds of row stay visibly distinct, and one mark carries the whole difference.** A
version keeps the accent-coloured tag it has today; a restore point carries none. **The mark says
*titled, permanent, travels* — three promises the unmarked rows do not make.** It is not an importance
badge. One mark and one accent: a second colour scheme is noise on a list whose entire problem is
noise.

**R-rc10-3. A saved dock layout naming a retired panel must open correctly, and this is the one place
the merge can break a workspace somebody already has.** `DockPanelIds.RestorePoints` and
`DockPanelIds.VersionHistory` are written into every `.cwsuser` in existence
(`CircuitRfDockFactory.cs:218`, `:623`). **Both retired ids resolve to the merged panel, and a layout
that docked both collapses to one instance** — not two tabs of the same panel, and not an empty pane.
The Window menu, the toolbar and the panel-toggle checkboxes each name it in two places today
(`WorkspaceWindow.axaml:214`, `:792`, `:1014` and their `VersionHistory` twins); all of them become
one.

**R-rc10-4. Nothing changes about storage, and a gate says so.** Checkpoints stay on §5.2a's
per-checkpoint references and still do not clone; versions stay ordinary commits on the branch;
retention's scope is unchanged (§5.6 rule 5 — human-written commits are out of it entirely). The moment
the two are stored alike, §5.2a's travel table stops being true and §5.6 has a commit in its scope.

---

## 2. What the list shows, and what it hides by default

**R-rc10-5. The default view is every entry somebody stated an intent for.** Versions, explicit
save-points, and before-batch checkpoints — §5.3a rule 2 makes the batch's stated intent its label.
**Workspace-close checkpoints are hidden by default**, revealed by one toggle.

**R-rc10-6. The close checkpoint keeps being taken, and this brief may not stop it.** §5.3's argument
is the strongest sentence in that section: close is the one boundary that reliably exists in every
session, including the ones where the designer never thought about history at all — which is exactly
the designer §1 is written for. **The complaint is list noise and the remedy is a display decision.**
A change to `WorkspaceCheckpoints` or to the close path fails this brief.

**R-rc10-7. The filter is a flyout in the header, in `ProjectTreeView`'s shape** (`ProjectTreeView.axaml:145`)
— a funnel glyph opening checkboxes. Minimum: *versions*, *save-points*, *AI batches*, *automatic
(workspace close)*, *tidied away*. The default has the fourth off and every other on. **The
tidied-away row keeps RC-6's behaviour** — thinned entries are marked and bring-back-able, and hiding
them is a filter choice rather than a new state.

**R-rc10-8. The filter state is per-user view state in the `.cwsuser`** (§3.1), like every other fact
about how somebody arranged their view. **It is not a Settings row**: every row on §10A's tab changes
what is *kept*, and a filter a designer flips while hunting is not a preference about what exists.
Putting it there is the category error that tab is most exposed to.

**R-rc10-9. Search, over what a person wrote** — titles, batch intents, and the author — in
`ProjectTreeView`'s shape (`:133`, `:171`): a magnifier toggles an inline field that exists only while
it is open. §10B's *"I broke the match network yesterday"* is a search, and making a designer scroll a
year of entries to perform it is making them do the machine's work.

**R-rc10-10. A search that matches thinned entries says so, on a line of its own.** RC-6's journal
already holds them. *Three tidied-away entries also match* is the difference between an incomplete
answer and a wrong one, and it is the same reasoning as R-rc9-6's incoming-versions line, which is
already in this panel.

**R-rc10-11. The gap row survives the merge** (RC-7 R-rc7-10, RC-6 R-rc6-13). An off period is a row
with its reason on it and never an ordinary interval, and it is now the only row in the list that is
neither kind.

---

## 3. The row, and its expander

**R-rc10-12. What a row carries** — it is what a designer *scans*:

- the time, and the date **including the year whenever it is not the current year**;
- the title, or the origin sentence for an entry nobody titled (§5.3 — a save-point with no label
  shows as *save-point*, never as a bare time);
- the tag mark, for a version;
- who kept it, on a workspace with more than one author;
- the restored-from line (§5.5, RC-7 R-rc7-6), the left-out-file line (§8.2b), the thinned mark and
  RC-9's incoming-version note — all four already exist and all four survive.

**R-rc10-13. The year is a defect, not a sparseness.** `VersionHistoryTool.Day` renders `ddd d MMM`
(`:44`) and `RestoredFrom` renders `d MMM HH:mm` (`:60`); a version kept in December reads in January
as one kept this week. Relative labels for recent entries — *today*, *yesterday* — are welcome and are
safe: §5.6 rule 2 already establishes that the wall clock supplies the label and never the ordering.

**R-rc10-14. An expander carries what a row cannot** — the full timestamp with its zone, the origin
spelled out, whether the entry is local-only or has been shared, the **commit identity** (short, with
the full one copyable), the sequence number (§5.6 rule 2) and the kept mark.

**R-rc10-15. The identity in the expander is permitted by RC-7 R-rc7-4 and by nothing else.** Nothing
git-shaped appears unbidden; what an *explicit action* produces may be named precisely, and opening an
expander is that action. It is the one string with which a designer — or somebody helping them — can
ask git a question circuitRF's own window cannot answer (§4.1). The vocabulary scan (RC-7 gate 3) must
be extended to this panel, not exempted from it: the identity is asserted by name, the way R-rc7-7's
already is.

**R-rc10-16. The action that goes back may not wear the undo glyph.** Both panels use `ArrowULeftTop`
today (`VersionHistoryToolView.axaml`, `RestorePointsToolView.axaml`) — the application's own undo
arrow, on the one action every line of the design note and both user chapters is at pains to
distinguish from an undo (§5.4). Pick a glyph naming a **point in time** rather than a step backwards,
verify the `Kind` exists in the pinned `Material.Icons.Avalonia`, and check it reads distinctly from
whatever the merged panel uses as its own tab icon — *history* and *restore* are near-twins in that
set. **One glyph, used in every place the action appears**; that part of the old comment was right.

**R-rc10-17. A row is right-clickable.** Go back to *"&lt;title&gt;"* (truncated — titles are free
text), copy the identifier, keep permanently / stop keeping (§5.6 rule 6), and compare with the current
state (RC-7 R-rc7-11). RC-11 adds to this menu and must not have to build it. The menu is what keeps
the header a place for the two actions that *create* something rather than a row of eight glyphs.

---

## 4. The way forward, and the entry that makes it true

**R-rc10-18. A restore reports itself in the panel, on arrival, by name** (§5.8, §12 Q35). One line at
the top of the list: what it went back to, what the previous state was kept as, and the action that
returns to it. **It creates nothing** — the entry already exists, and going to it is an ordinary
restore taking an ordinary checkpoint of its own.

**R-rc10-19. This is the two-panel split producing a safety failure rather than clutter, and it is why
the merge is not cosmetic.** §5.8's restore takes a checkpoint of the current state first and files it
among the restore points; a restore begun from the Versions panel therefore left the designer looking
at a window with no evidence that the afternoon they had just replaced still existed. The reassurance
was implemented, correct, and in the room the designer was not in.

**R-rc10-20. `WorkspaceCheckpoints.IsAlwaysKept` gains `CheckpointOrigin.BeforeRestore`** (§5.6 rule 6,
§12 Q34). It covers `SavePoint`, `RecordingOff` and `RecordingOn` today (`WorkspaceCheckpoints.cs:151`)
and `WorkspaceRestore` takes its pre-restore checkpoint with `kept` defaulted to false
(`WorkspaceRestore.cs:87`) — so the one entry whose entire purpose is to undo a destructive operation
ages out of the list on its own while the designer keeps working. It is not data loss (§4.5 keeps the
objects, RC-6's journal brings the entry back); it is the promise leaving the place the promise was
made. **This is the one storage-visible change in the brief and it is a keep, never a delete.**

---

## 5. The headless spelling

**R-rc10-21. `history list` gains the panel's default view and its filter** (§5.3d). A verb that cannot
express what the window shows means an agent and a designer are reading two different histories.
Minimum: a flag that includes the automatic entries the panel hides, a flag that selects kinds, and a
search term. `history versions` is unchanged and stays — it answers a different question, narrowly.

**R-rc10-22. The default of `history list` matches the panel's default**, and where it cannot it says
which it used. Two defaults that differ silently is the defect this requirement exists to prevent.

---

## 6. Gates

`tests/Ui.Tests/Revision/OneHistoryPanelTests.cs`, beside the RC-5/6/7/9 files.

1. **One panel, both retired ids** (R-rc10-3): a `.cwsuser` naming `RestorePoints`, one naming
   `VersionHistory`, and one naming **both**, each open to exactly one History panel — asserted on the
   restored dock tree, not on a screenshot.
2. **Both kinds appear in one list, and the mark is the only thing that distinguishes them**
   (R-rc10-2): a workspace with versions and restore points yields one ordered list in which the
   version rows carry the mark and no other row does.
3. **The default view hides close checkpoints and nothing else** (R-rc10-5): a workspace with one of
   each of the five origins plus a version shows every intent-bearing entry and not the close one; the
   toggle reveals it.
4. **The close checkpoint is still taken** (R-rc10-6): close a workspace with the default filter and
   assert the checkpoint exists on its reference — plus a comment-stripped source scan proving this
   brief's paths contain no change to the close boundary.
5. **The filter and search state round-trip through the `.cwsuser` and appear in no preference file**
   (R-rc10-8).
6. **Search matches title, intent and author, and reports thinned matches separately** (R-rc10-9,
   R-rc10-10): thin an entry whose title matches, and assert the count line rather than a silent
   omission.
7. **The year appears exactly when it is not the current year** (R-rc10-13): entries stamped in the
   current year and a prior one, asserted on the rendered strings — both the row's date and the
   restored-from line.
8. **The go-back glyph is not the undo glyph** (R-rc10-16): assert the `Kind` is not `ArrowULeftTop`,
   that the same `Kind` is used at every site the action appears, and that it resolves in the pinned
   icon package.
9. **The expander names the identity and the vocabulary scan still passes** (R-rc10-14, R-rc10-15):
   extend RC-7 gate 3's scan to this panel, with the identity asserted by name as its single
   exemption.
10. **A restore reports the way forward, and following it returns the state** (R-rc10-18): restore,
    assert the line names both entries, follow it, and assert the tree equals the pre-restore tree —
    and that a fresh pre-restore checkpoint was taken on the way (RC-5 gate 15's assertion, reached
    from this entry point).
11. **A pre-restore checkpoint survives a sweep that would have thinned it** (R-rc10-20): restore, then
    drive retention hard enough to thin every unkept entry, and assert it is still live — the RC-6
    retention fixture, with one origin added.
12. **`history list` shows what the panel shows** (R-rc10-21, R-rc10-22): the verb as a process against
    the in-process query, same filter, same order, byte for byte on the reported fields.
13. **Nothing about storage moved** (R-rc10-4): after exercising the panel, assert the checkpoint
    references, the branch and the retention scope are what RC-5/RC-6/RC-7's own gates assert — and a
    comment-stripped source scan showing this brief added no call that writes a reference or a commit,
    the single exception being R-rc10-20's kept mark, named.

---

## 7. Documentation

**The two chapters become one**, for the same reason the two panels do:
`docs/user/src/reference/restore-points.md` and `reference/versions.md` → one chapter, with both kinds
of entry in it and the *not undo* passage kept intact (it is the clearest paragraph in either file).
The nav (`docs/user/src/_nav.txt`) and every inbound link change with them, and the retired slugs must
not 404 for someone with a bookmark.

- **§10B.2 scenario 17** — *"I went back and I want to come forward again."* Scenario 3 already covers
  restore-then-keep-working; **this is the twenty seconds before that**, and it is the moment a
  designer is most likely to believe they have lost the day.
- **The sentence in the merged chapter that replaces the old split's justification**: the two kinds are
  still two kinds, and the filter is how the deliberate ones stay findable among the automatic ones.
- **§10B.3 stays a separate section** — the AI-checkpoint material is separate for an audience reason
  (§10B.3) and merging panels does not merge chapters' audiences.
- The Settings chapter's figure and the workspace chapter's panel list both name the retired panels;
  DocGen regenerates the figures.

---

## 8. On completion

Findings to `src/Ui/RESOLVED.md` — **never to a `CLAUDE.md`**.

**Report, do not silently absorb:**
- What a `.cwsuser` naming both retired panels actually restored to before R-rc10-3 was implemented —
  an empty pane, a duplicate, or a silent drop. Whichever it is, it is the shape of every future panel
  retirement and the next person needs the answer measured rather than assumed.
- Whether the default filter left any entry a designer would look for out of the list — in particular a
  workspace whose only history is close checkpoints, which under R-rc10-5 opens on an empty list. If
  the empty-state line does not carry it, that is a finding and the fix is wording.
- Whether the merged list needed virtualisation at the sizes retention actually produces, measured
  rather than guessed, and what the number was.
- Whether any RC-7 or RC-9 assertion had to be relaxed rather than re-pointed. A relaxed assertion in
  this brief is a storage change wearing a display change's clothes.

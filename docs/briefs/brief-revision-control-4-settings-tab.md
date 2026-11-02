# Sonnet Brief — RC-4: Settings ▸ Revision Control

**Read `brief-revision-control-0-overview.md` first.** The architecture is
`docs/design/revision-control.md` §10A, plus §4.4 (identity), §5.6 (retention), §5.6a (reclaim, new in
rev 5), §5.7 (off) and §2.4 (packing). **Depends on RC-3** for git discovery, the packing threshold, the
identity reader and the reclaim operation.

**Scope: one new tab in the Settings dialog, holding every preference this feature needs, and the
tab-strip change that makes room for it.** No checkpoint is taken by this brief; RC-5 does that. What
this brief ships is the place a designer reads to find out what is being kept.

---

## 1. The tab, and where it goes

**R-rc4-1. A new `TabItem` headed "Revision Control", immediately to the right of "Security &
Permissions"** (owner, 2026-09-06). The resulting order is:

```
General | Security & Permissions | Revision Control | Color Theme | Wirebonds
```

**R-rc4-2. It is a tab of its own and does not share one with Security & Permissions.** That tab
answers exactly one question — what circuitRF is allowed to *run* and to *fetch* — and its own header
records that things were collected there for that shape rather than for the tab they arrived in. Naming
a git executable is the only row here that would fit, and the other nine are about what is kept.

**R-rc4-2a. That placement decides where a setting is READ, and is not a statement that git is exempt
from consent** (§10A, rev 3). Git is both things Security & Permissions governs: a **program circuitRF
runs** — which is exactly why `VerilogACompilerSettingsView` is on that tab, per its own comment — and,
in RC-9, a **network operation**, which is why the updater is there too. **If running an external
program or reaching a network requires the user's consent in this application, git requires it on the
same terms**, recorded where the other consents are recorded and visible from the tab that owns them.
Read that tab before building this one and **report the answer** rather than inferring it: a second,
parallel consent model for one program is worse than either placement.

**R-rc4-3. The whole tab is hidden when git is unavailable and no path has been configured** (§4.3),
**with the single exception of the path field itself** — which is how someone with git in an unusual
location makes it available in the first place. Hidden, not disabled: a designer who does not want this
should never learn it exists. Contrast RC-6's hold state, where the affordances stay visible and
refuse.

---

## 2. The tab-strip change, and the trap already recorded in this repo

`SettingsView.axaml:17` has a bare `<TabControl>` with **no `ItemsPanel` override and no scoped
`TabItem` style**, so its four headers inherit the theme's header metrics. A fifth will not fit in a
720-wide dialog.

**R-rc4-4. Reduce the tab header font size** (owner, 2026-09-06), following the precedent already in
the tree at `TechEditorView.axaml:251`–`:272`, which exists because of a real failure:

> `TabControl`'s **default `ItemsPanel` is a `WrapPanel`**. Headers that do not fit **spill onto a
> second row** — they do not clip, and nothing errors. That is why four headers silently became two
> rows as that window narrowed. The fix already in the tree is a horizontal `StackPanel` as the
> `ItemsPanel`, **plus** a scoped `TabItem` style pinning `FontSize`, `Padding` and `MinHeight`,
> because the panel alone makes one row a promise and the style is what makes it a fit.

Do both, scoped to this `TabControl`'s own `Styles` (never a global `TabItem` selector — this dialog is
not the only `TabControl` in the application). Take `TechEditorView`'s values as the starting point and
adjust only if five headers still do not fit at `MinWidth="620"`.

**R-rc4-5. Verify at the dialog's minimum width, not its default.** The `Window` is `Width="720"` with
`MinWidth="620"` (`SettingsView.axaml:7`–`:9`). A tab strip that fits at 720 and wraps at 620 is the
same defect arriving later.

---

## 3. Three things renumber, and one test asserts the old count

**R-rc4-6. `DocSettingsFixtures` selects each documentation figure by tab INDEX**
(`src/Ui/Diagnostics/Fixtures/DocSettingsFixtures.cs:30`–`:39` — `General() => Tab(0)` … `Wirebonds()
=> Tab(3)`). Inserting at index 2 moves **Color Theme to `Tab(3)` and Wirebonds to `Tab(4)`**. Miss
this and two chapters illustrate the wrong tab, silently, with no test failing on the substance.

**R-rc4-7. A fifth `FigureCatalog` record and a fifth `{{ui: …}}` citation.**
`FigureCatalog.cs:241`–`:261` holds the four; `settings-revision-control` joins them, cited from the
settings chapter.

**R-rc4-8. `SettingsDialogHelpAndTooltipsTests.EveryTabOfTheDialogHasItsOwnFigureAndThePageCitesThemAll`
(`:132`) asserts exactly four `<TabItem Header=` and enumerates the four figure ids.** Extend it to
five. **This is a test doing its job** — its own comment says a page whose prose walks four tabs while
its figures show three is the drift the docs factory exists to prevent — so extend it, do not relax it.

**R-rc4-9. The four existing figures regenerate, because the tab strip appears in all of them.** Per
this repo's standing practice on generated-file churn: **classify before reporting.** A DocGen run
touching the four settings figures plus one new one is expected; a run reporting hundreds of changed
`.svg`s is not, and the id counter in those files is hexadecimal, so a `\d+` pattern mis-classifies id
churn as real change. Two figure families in this repo are independently nondeterministic and must be
reverted rather than committed — check the standing notes before reporting a diff.

---

## 4. The controls

**R-rc4-10.** Ten rows, in this order. Each cross-reference is the section that decides its
behaviour; this brief builds the control and the sentence beside it, not the behaviour.

| setting | shape | notes |
|---|---|---|
| **Path to git** | text box, Browse, Detect, status line | Blank means "search `PATH`". **Copy `VerilogACompilerSettingsView`** (`src/Ui/Views/Dialogs/VerilogACompilerSettingsView.axaml`, 49 lines) — same blank-means-PATH default, same Browse, its Test becomes **Detect**, same status line. On success it shows **the resolved path and the version**, so *"it says it can't find git"* is answerable without a support call. **A git below RC-3 R-rc3-3a's floor is reported as too old, naming the version found and the version needed** — this is the one place that state is visible, because everywhere else treats an under-floor git as absent (§4.7). |
| **Commit identity — name and email** | two text boxes | §4.4. **A per-USER preference in `AppPreferences`, applying to every workspace this person opens on this machine** — written to **no git config file at all**, and supplied per invocation by RC-3 (R-rc3-1a). **Stored in the per-user preference file where RC-3's `src/Design` reader finds it** (R-rc3-1c, rev 5), so `serve` commits under the same name. **Pre-filled from the user's existing global git identity** if they have one: reading it is unobjectionable, and only writing it was ever the problem. **Required before the feature arms, and the tab says so** rather than letting the first checkpoint fail. |
| **Keep a history of my workspaces** | on / off | **§5.7a, §12 Q13 — new in rev 4, and it is the row that makes every other one reachable.** A per-user application preference, and **the default a workspace with no recorded setting of its own falls back to**. **On by default** — see R-rc4-12a. Off means circuitRF writes nothing in any workspace and **deletes nothing in any workspace**, and the control says so in those words, exactly as the per-workspace one does (R-rc4-14). |
| **Revision control for this workspace** | on / off | §5.7. Stored **in the `.cws`**, not in `AppPreferences` — see R-rc4-12. **It outranks the preference above, for this workspace only.** |
| **Keep restore points for** | duration | §5.6. |
| **Always keep at least *N* restore points** | number, with a hard floor | §5.6 rule 1. The field **cannot be set below the minimum**; this is the control that makes a clock jump cost the user nothing. |
| **Take a restore point on workspace close** | checkbox, on by default | §5.3. |
| **Take a restore point before an AI edit** | shown, **on, and not switchable** | §1.2 — the reason the feature exists. **Shown rather than hidden** so nobody has to wonder whether it is happening. |
| **Pack the repository** | threshold, plus a "do it now" action | §2.4, RC-3 R-rc3-17. Ordinary users never touch it; it exists because someone eventually asks where the disk went. Packing never reclaims. |
| **Reclaim space from thinned restore points** | an age, plus a confirmed action | §5.6a, RC-3 R-rc3-16a (rev 5). **The one destructive control on the tab.** The confirmation says what it destroys — states already thinned more than the age ago, which after this nobody can bring back — and that every live restore point and every commit survives. Nothing reclaims on a schedule, and the age field on its own does nothing. |

**R-rc4-11. The nine application-wide preferences live in `AppPreferences`**
(`src/Ui/Theming/AppPreferences.cs`), following `VerilogACompiler` (`:315`) — nullable, `JsonIgnore`
when null, absent means the documented default.

**R-rc4-11a. The commit identity is one of them, and rev 2 of the architecture had this wrong** (§4.4,
corrected in rev 3). It said the identity was written to the **workspace repository's** config. That
makes a person's identity a property of a **directory**, and these workspaces live on network shares
(§4.7, §7A): **the second designer to open a shared workspace would commit under the first one's
name**, silently, until somebody read a history and disbelieved it. It is §3.1's mistake in another
file — per-user state written into a shared document — and it takes §3.1's fix.

So: `AppPreferences` is already documented as *the one per-user state directory*
(`AppPreferencesIo.PrefsDir`), which is exactly the scope this has — one person, every workspace they
touch on this machine. **Nothing is written into `.git/config`, and nothing into `~/.gitconfig`.**
The original objection to the global config stands and is unchanged: circuitRF has no business
changing a setting that affects every other repository on the machine.

**`src/Cli` reads this preference too, through RC-3's reader, and rev 4 had this wrong** (§4.4,
§12 Q21). rev 4 let the headless case fall through to git's own resolution because `src/Cli` cannot
reference `AppPreferences`. On the fresh Windows machine this section opens with, that resolution names
nobody — so the AI-batch checkpoint was refused for exactly the population it protects, with no remedy
but a global config write. The file in `AppPreferencesIo.PrefsDir` is readable from `src/Design` without
crossing anything; this brief writes it through `AppPreferences` as today, and shares the path and the
two key names with R-rc3-1c through a constant in `src/Design`. **Where the preference is unset, git's
ordinary resolution applies**; where neither names a committer, the feature does not arm (RC-5 R-rc5-9)
and the refusal names this tab.

**R-rc4-12. "Revision control for this workspace" is per-workspace state and lives in the `.cws`.** An
installation-wide flag cannot gate per-workspace state: it is correct for the first workspace and
silently wrong for the second. **This repo has already made that mistake once and recorded it**
(`src/Ui/RESOLVED.md`, the wirebond group work, where a per-installation flag failed on the second
workspace as floating panels). It goes in the **versioned** half, not the `.cwsuser`, which has a
pleasing side effect: turning it off is itself a recorded change, so the last commit before the
history goes quiet is the one that says why.

This means the control's state depends on which workspace is open, and the tab must say which one it
is talking about. A Settings dialog opened with no workspace open shows the row disabled with that as
the reason.

---

## 5. The two rules that bind this tab specifically

Both are §1.4, and they are why this tab is specified in the architecture rather than left to a dialog.

**R-rc4-12a. The two switches answer different questions, and rev 3 had only one of them.** "Arming"
was used throughout rev 3 with no default stated anywhere — §1.2's whole floor resting on an unstated
value. The preference answers *"do I want this at all"*; the `.cws` flag answers *"not for this one"*.
**§5.7's rule that an installation-wide flag cannot gate per-workspace state is untouched by this** —
R-rc4-12 still holds, and the preference is not that flag; it is the value the flag defaults to when a
workspace has never recorded one.

**It ships on**, because §12 Q5 puts the floor in before the capability and a floor found only in a
settings tab is not one — and because §4.3 already makes this entire tab invisible to everyone without
git, so the default only ever reaches people who installed git deliberately. **The two guards that keep
"on" from being the surprise R-rc3-8 rejects belong to RC-5, not here** (armed at the first boundary
that would record something rather than at open, and announced once when a workspace first gains a
repository) — but this tab is where the announcement points, so the row's wording and the announcement's
must be written together.

**R-rc4-13. Every control that can reduce what is kept states its consequence next to itself, in a
sentence, in the settings UI.** Not in a tooltip, and not only in the manual. *"Keep restore points for
7 days"* is accompanied by what that means: after seven days, automatic restore points are thinned and
the states they held are no longer offered. **A designer who reads only this tab must come away with a
correct belief.**

Note the tension with the existing tooltip pass. `SettingsDialogHelpAndTooltipsTests` pins that four
paragraphs of standing helper text became tooltips, because a tab of prose with controls in it is
clutter. **That pass and this rule do not conflict**: what moved into tooltips was *explanation* — what
a feature is for. What R-rc4-13 requires is a *consequence* — what you will not get back. The General
tab already carries exactly this shape at `SettingsView.axaml:73`–`:75`, a wrapped 11pt line under the
DRC checkbox. Follow that; keep it to one sentence.

**R-rc4-14. Turning revision control off does not delete anything, and the control says so in those
words.** An off switch that quietly discarded a history would be the single most damaging control in
the application: nobody expects a checkbox to be irreversible, and by the time they discover it was,
there is nothing to discover it with. Off means circuitRF stops writing. Removal is deleting one
plainly-named folder and is not offered here (§5.7) — RC-6 builds the behaviour; this brief ships the
sentence.

**R-rc4-15. Every paragraph tooltip wraps and is bounded.** Avalonia hands a bare string to a
`ContentPresenter` that does not wrap, so a 400-character tip renders as one line running off the
screen edge. The repo's idiom is an explicit `TextBlock` with `TextWrapping="Wrap"` and a `MaxWidth`,
and `EveryParagraphTooltipWrapsAndIsBounded` already asserts it for the three existing sub-views.

---

## 6. Gates

1. **Order and position.** The fifth `TabItem` is at index 2, headed "Revision Control", immediately
   after "Security & Permissions" (R-rc4-1).
2. **One row.** With five headers, the tab strip occupies one row at `MinWidth="620"` — assert the
   `ItemsPanel` is a horizontal `StackPanel` and the scoped `TabItem` style pins the size, since that
   is the structural property; a rendered-pixel assertion is not what `Ui.Tests` does (R-rc4-4).
3. **The figures renumber** (R-rc4-6): `ColorTheme()` is `Tab(3)` and `Wirebonds()` is `Tab(4)`, and
   each figure's content is checked to be the tab it claims — this is the failure no existing test
   catches.
4. **Five figures, five citations** (R-rc4-7, R-rc4-8), with the tab-count assertion extended to five.
5. **Hidden without git** (R-rc4-3): with no git found and no path configured, the tab is absent — and
   the path field is still reachable. Assert both halves; the exception is the whole point.
6. **The per-workspace flag round-trips through the `.cws`**, and two workspaces open at once hold
   independent values (R-rc4-12). This is the gate that catches the mistake the repo made before.
6a. **Precedence, all four combinations** (R-rc4-12a): preference off beats an armed workspace; preference
   on with the workspace switched off is off; preference on with no recorded workspace setting is on;
   and a workspace that recorded a setting keeps it when the preference changes. **The fourth is the one
   that would be missed** — a per-workspace decision must not be silently rewritten by a global one.
6b. **The preference ships on** (R-rc4-12a): a fresh `AppPreferences` with no recorded value reads as on,
   and asserts against the documented default rather than against a transcription of the writer.
7. **The count floor cannot be set below its minimum** (R-rc4-10), including by typing a value rather
   than using the stepper.
8. **The AI-edit row is shown, on, and cannot be switched off** — assert the control is present and
   not interactive, not that it is absent.
9. **Consequence sentences exist** (R-rc4-13): a source scan for a standing wrapped sentence beside
   each of the retention duration, the count floor, the off switch **and the reclaim action**. Strip
   comments before scanning — this repo has been caught by a source scan matching its own documentation.
9a. **Reclaim asks, names what it will destroy, and is the only control that calls RC-3's reclaim**
   (R-rc4-10): assert the confirmation is presented with the count and the age, that cancelling reclaims
   nothing, and — source-scanned, comments stripped — that no other path in `src/Ui` reaches
   R-rc3-16a's function.
10. **Tooltips wrap and are bounded** (R-rc4-15), through the existing theory.
11. **Identity is written to NO config file** (R-rc4-11a): after arming and committing, assert the
    fixture repository's config carries no `user.name`/`user.email` **and** the user's global config is
    untouched, while the commit names the right author and committer. RC-3 holds the
    two-identities-one-workspace half of this; **this brief holds that the preference is per-user and
    not per-workspace** — assert it does not move when the open workspace changes, which is the
    opposite of R-rc4-12's flag and the pair is worth testing together. **And assert RC-3's reader
    returns what this tab wrote** (R-rc3-1c) — the same name, from a type that references nothing in
    `src/Ui`.
12. **The identity field pre-fills from an existing global git identity** and does not write it back
    (R-rc4-11a).
13. **Detect reports an under-floor git as too old**, naming both versions, and the rest of the tab
    still behaves as though git were absent (R-rc4-10, RC-3 R-rc3-3a).
14. **Git-absent machines skip with a reason** rather than failing (R-rc0-10b) — the same idiom every
    brief from RC-3 onward uses.

---

## 7. Documentation

`docs/user/src/reference/settings.md` is written tab by tab and gains a section for this one, cited to
the new figure. The chapter's existing framing — every tab but Color Theme writes immediately, and
Cancel does not undo it — applies here and should be checked rather than assumed.

The Help button already opens `reference/settings.html` and `DocAnchors` already knows the page, so no
anchor contract changes; the new section's anchor follows the chapter's existing scheme.

**The consequence sentences appear in both places** — beside the control and in the chapter. That is
not duplication: R-rc4-13 exists because a designer who reads only the tab must still come away
correct.

---

## 8. On completion

Findings to `src/Ui/RESOLVED.md` — **never to a `CLAUDE.md`**.

**Report, do not silently absorb:**
- Whether five headers actually fit at `MinWidth="620"` with `TechEditorView`'s values, and what the
  final size was (R-rc4-4).
- **What the existing consent model actually says about running a program and reaching a network**
  (R-rc4-2a), and whether git was brought under it or deliberately left outside. Either answer is
  fine; an unstated one is not.
- **Whether anything still wanted the identity in a git config file** (R-rc4-11a). If some path did,
  say exactly which — that is the shape of the mistake rev 2 made, and it should not be re-made in a
  corner.
- The DocGen diff, **classified**: figures that changed because the tab strip changed, versus anything
  else (R-rc4-9). A count of changed files is not a report.
- **Whether the "off deletes nothing" sentence reads correctly for the application-wide row as well as
  the per-workspace one** (R-rc4-12a, R-rc4-14). It is the same promise at a wider scope, and a sentence
  that is reassuring about one workspace can read as alarming about all of them.
- Anything the tab needed that RC-3 did not provide.

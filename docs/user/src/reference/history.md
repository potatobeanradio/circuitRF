---
title: History
slug: reference/history.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > History
lede: Everything circuitRF has kept of a workspace, in one list — the states it kept for you automatically, and the versions you kept on purpose. What is in it, how to find something, and how to go back.
keywords: history, restore, undo, revert, safety net, save point, version, commit, release, share, compare, conflict, rename, correct, title
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#two-kinds">Two kinds of entry, one list</a></li>
<li><a href="#when">When circuitRF keeps a state</a></li>
<li><a href="#keeping">"I want to keep this one — it's what I'm sending out"</a></li>
<li><a href="#panel">The History panel</a></li>
<li><a href="#going-back">Going back — "I broke the match network yesterday"</a></li>
<li><a href="#forward">"I went back and I want to come forward again"</a></li>
<li><a href="#comparing">What changed between two versions</a></li>
<li><a href="#titles">"I typed the title wrong"</a></li>
<li><a href="#restore-then">"I went back to an old version, then kept working"</a></li>
<li><a href="#gaps">Gaps in the list</a></li>
<li><a href="#results">"My results are gone"</a></li>
<li><a href="#started">"circuitRF says it started keeping a history"</a></li>
<li><a href="#copy">"I saved a copy and my history didn't come with it"</a></li>
<li><a href="#customer">"I'm sending this workspace to a customer"</a></li>
<li><a href="#other-copy">Exchanging versions with another copy</a></li>
<li><a href="#two-of-you">"Two of us are editing the same workspace"</a></li>
<li><a href="#kept">What is kept, and what is not</a></li>
<li><a href="#tidying">Tidying up — "I can't get back to last month"</a></li>
<li><a href="#enclosing">"My workspace is inside another version-controlled folder"</a></li>
<li><a href="#already">"This workspace already had version control and circuitRF asked me something"</a></li>
<li><a href="#off">Turning it off, and getting rid of it entirely</a></li>
<li><a href="#disk">"My disk is full and it says the history is taking the space"</a></li>
<li><a href="#rewriting">"Something went in that shouldn't have"</a></li>
<li><a href="#assistants">History and AI assistants</a></li>
</ol>
</nav>

circuitRF uses Git under the hood for revision control. To enable History tracking, Git must be
installed and then configured in the circuitRF [Settings](settings.html#revision-control).

## Two kinds of entry, one list {#two-kinds}

**circuitRF records what you did and never edits it. What you *wrote about* it is yours until you have
shared it.** Those are two different promises and it is worth having both of them in mind from the
start: the files, the times and the order they happened in are fixed for good, and the line you typed
on an entry is a label you can correct. Nobody writes a careful title if they believe a careless one is
permanent, so [it is not](#titles) — and the moment a version leaves your machine, the line is drawn
and a correction sits beside the original instead of replacing it.

circuitRF keeps two different things about a workspace, and the History panel shows both.

A **restore point** is the whole workspace as it stood at one moment: every cell, every schematic,
every layout, the workspace's own configuration. circuitRF takes them mostly by itself — when you
close the workspace, when you ask for one, and before an assistant changes anything. They are a safety
net, and they never leave this machine.

A **version** is a statement. You decide on it, you give it a title, and it is what you come back to
six weeks later and what goes with the workspace when it leaves your machine.

| | restore points | versions |
|---|---|---|
| who makes them | circuitRF, mostly automatically | you, on purpose |
| how many | dozens, thinned as they age | as few as you decide |
| what they are for | getting back from something that went wrong | saying "this one is finished" |
| do they travel? | no — they belong to this machine | yes — they go with a copy or a clone |

**They are still two kinds of thing, and the list says which is which.** A version carries a small
coloured tag; a restore point does not. The tag is not a mark of importance — it means *this one has a
title, nothing tidies it away, and it travels with a copy*, which are three promises the unmarked
entries do not make.

**They are in one list because the alternative was worse.** A list with three hundred automatic
entries and four deliberate ones mixed among them is a list nobody reads — so the panel opens showing
only the entries somebody stated an intent for, and the automatic ones are one click away in the
filter. Two separate panels answered the same problem by making you decide which one to open, and that
question is hardest at the moment you have to ask it, which is the moment something has gone wrong.

It is not undo either. **Undo** is per-editor and per-keystroke, it covers the last few minutes inside the
document you are looking at, and it ends when you close the window. A restore point is
workspace-wide, action-grained and durable — it is still there next week. The two are deliberately
unrelated, and between them they cover the whole range: undo for the last few minutes in one view,
restore points for everything else.

<div class="callout note">
<span class="label">Going back is never a one-way door</span>
<p>Before it replaces anything, circuitRF keeps a restore point of the state you are in right now —
including work you have done since the last one. So if you go back and it was the wrong moment, you
can come forward again. That entry appears in the list like any other, the panel offers it to you by
name the moment you arrive, and tidying up never removes it.</p>
</div>

## When circuitRF keeps a state {#when}

Three moments, and no others:

| moment | why this one |
|---|---|
| **you ask for one** — File ▸ Keep This State… | your judgement about what matters beats any rule circuitRF could invent |
| **the workspace closes** | the one moment that reliably arrives in every session, including the ones where you never thought about history |
| **before an assistant changes anything** | so anything an AI assistant did can be undone in one action |

**Not on every save.** One thing you do — widening a match network — writes a layout, a schematic and
the workspace file, so a restore point per file save would keep fragments of an edit, some of them
half-finished. And a list with three hundred entries in it is a list nobody reads, which would make it
useless exactly when you needed it.

**Not on a simulation run, and not on a timer.** Runs are frequent and usually change nothing; a sweep
would leave dozens of near-identical entries. And an entry made because you went to lunch is one you
cannot predict, at a moment that meant nothing, labelled with a time instead of a reason.

**A moment where nothing changed keeps nothing.** Close a workspace you only looked at, or press Keep
This State twice, and there is one entry, not two.

<div class="callout note">
<span class="label">Nothing is created until something is worth keeping</span>
<p>Opening a workspace to look at it writes nothing at all — which matters most for a workspace on a
shared drive that belongs to somebody else. circuitRF starts keeping a history at the first moment that
would actually record something, and tells you once when it does.</p>
</div>

## "I want to keep this one — it's what I'm sending out" {#keeping}

**File ▸ Keep This Version…**, or the tag button at the top of the History panel.

One field: a title. Write what the design *is* at this moment, not what you did to it —
*"Output match retuned for 3.5 GHz"*, not *"changed some stuff"*. Six weeks from now that line is the
only thing that tells one version from another, and it is what anyone you send the workspace to will
read first.

**And, under *Add more detail*, as much as you want to write.** A title is one line and some decisions
need a paragraph: what you tried first, what the measurement actually said, which of two options this
is and why you took it. That is the part the files themselves can never tell you afterwards. The field
is closed until you open it and you never have to use it — but when a version matters, it is the
difference between a line you recognise and a line you can act on.

It is kept with the version and travels with it, so whoever you send the workspace to reads it too.
[You can correct it later](#titles), exactly as you can correct the title.

What is recorded is **the whole workspace as it stands**: every cell, every schematic, every layout, the
technology, the workspace's own configuration. Not just the document you have open.

**Keep This State… asks for the same two things**, and the same way round: one line for the list, and
as much detail as you want behind the expander. On a save-point the detail is usually *what you were in
the middle of* — the part you would otherwise have to reconstruct when you come back to it on Monday.

**Where you read it back.** The row in the History panel shows the title and a small mark saying there
is more; **open the row** and the note is at the top of what unfolds, above the time and the identity.
The [search](#search) reads it too, which is very often how you find the entry at all.

<div class="callout note">
<span class="label">Nothing changed means nothing to keep, and you are told before you type</span>
<p>If the workspace is exactly as it was when the last entry was made, both keep buttons are
<b>greyed</b> and hovering one says why. Two entries holding identical content would be two entries you
could not tell apart. Change anything and they come back.</p>
<p>Anything unsaved is saved first, in the same words a close asks in — so what gets recorded under the
title you just wrote is what you are looking at, not what was last written to disk.</p>
</div>

Afterwards, one line appears in the Messages panel saying what was kept **and giving the version's
identity** — a short string of letters and digits. You will almost certainly never need it. It is there
for the day you do: it is what to give someone helping you look at the workspace with tools other than
circuitRF.

Simulation results are not kept in a version, for the same reason they are not kept in a restore point:
a result is a function of the design and the engine, so the design is the thing worth recording. See
[what is kept, and what is not](#kept) — the same table applies.

## The History panel {#panel}

**View ▸ Panels ▸ History**, or the clock button on the workspace toolbar. A list, newest first.

**Each row is what you scan**, and it reads left to right in three parts.

- **The time** — *just now*, *7 minutes ago*, *3 hours ago* for anything inside the last day, and the
  clock time beyond that — with the date under it, carrying the year whenever the entry is not from
  this year. The column is the same width on every row, so everything to the right of it lines up.
- **The entry's own short name**, in a fixed-width face: seven letters and digits, such as `f15b942`.
  **This is what one entry is called.** A time tells two entries apart; it is not something you can
  quote, point at, or give to somebody helping you — and forty automatic entries share their wording.
  It is the first few characters of the [full identifier](#panel) in the entry's own expander, and it
  is what the buttons and the menu name their destination by.
- **The label** — the title you wrote, or a short line saying how the entry came about for one nobody
  titled: *closed*, *save-point*, *before going back*, and *"before: widen the output match"* for an
  assistant's batch, which quotes what the assistant said it was about to do.
  Those generated ones are deliberately short and deliberately repetitive: they name the *kind* of
  moment, which is all a label can honestly do, and the short name beside them is what tells one from
  another.

A version carries its tag. On a workspace more than one person works in, the row says who kept it. And
a row says the three things that change what it *means*: that it was brought back from an earlier
state, that a file was left out of it, or that tidying up has tidied it away.

**Open a row** for what a row cannot carry: the full date and time with its time zone, a sentence
spelling out how the entry came about, whether it has left this machine or is still only here, its
ordering number, and its identifier **in full**. The row shows the first seven characters of that
same string, and **Copy the identifier** puts the whole of it on the clipboard — it is one value shown
two ways, never two values. You will almost certainly never need the long form; it is there for the day
you do, to give someone helping you look at the workspace with tools other than circuitRF.

**Right-click a row** for everything you can do to it:

- **Go to "…"** — see [going back](#going-back) below. **It names where it will take you, and it
  quotes only words a person wrote.** A titled entry reads *Go to "Output match retuned for 3.5 GHz"*;
  one nobody titled reads *Go back to f15b942*, naming that entry's own short name, because *go back to
  this state* read identically on every row and said nothing about which of forty it would act on.
- **Compare to Current** — lists the documents that entry holds which your files no longer do.
  Nothing is changed. If they hold the same files, it says so.
- **Keep permanently** — marks an entry so circuitRF's own tidying-up will never remove it. Ones you
  asked for, and the one labelled *before going back*, are already marked.
- **Bring this back** — on an entry marked *tidied away*, puts it back in the list as an ordinary one.
  See [tidying up](#tidying).
- **Rename this entry…**, **Edit Comments…** and **Tidy this away** — the label or the title, what
  happens to a version you have already sent, and putting an entry out of the way.
  See ["I typed the title wrong"](#titles).
- **Copy the identifier**.

**There is no delete.** Nothing on this menu destroys anything: *Tidy this away* is the same putting-away
that automatic tidying does, and the entry stays in the list marked *tidied away* until you bring it
back. The only thing that actually frees space is **Settings ▸ Revision Control ▸ Reclaim space**, which
works by age over everything already tidied away — never on one entry you picked. See
[tidying up](#tidying).

Two buttons at the top of the panel *create* something: **keep this state** writes a restore point, and
**keep this version** records a titled version. They are the same two things File ▸ Keep This State…
and File ▸ Keep This Version… do.

**Both go grey when there is nothing to record**, and hovering one says so: *this state is already
kept — nothing has changed since the last entry in the history*. Change anything and they come back.
Until this existed, both opened their dialog, took a title off you, and only then said they had
recorded nothing.

A button that is grey because history is **off**, **held**, or has **no git** is a different thing and
looks different: those stay live and refuse out loud, because "not allowed to record" and "nothing to
record" are not the same answer.

**One more button appears under the list when you select a row**: the same *Go to "…"* the row's menu
carries, on the surface, because going back is this panel's whole point and it should not need a
gesture you have to guess at. It is **absent** when it would put the workspace back into the state it
is already in — there would be nothing for it to change.

### The filter

The funnel opens five tick boxes: **versions**, **states you kept**, **before an assistant**, **kept
automatically**, and **tidied away**. Everything is on except *kept automatically* — the entries
circuitRF takes when you close the workspace, which are the ones that arrive three hundred at a time.

**Those entries are still being taken.** Closing the workspace is the one moment that reliably happens
in every session, including the ones where you never thought about history at all, so hiding them from
a list is a decision about the list and never about what is kept. Tick the box and they are there.

The setting is yours and it belongs to this workspace: it is remembered next time you open it, and it
is not something anybody else inherits.

### Search

The magnifier opens a field, and it searches **what a person wrote** — the titles you gave your
versions, **the longer notes under them**, what an assistant said it was about to do, and who kept each
entry. *"I broke the match network yesterday"* is a search, and scrolling a year of entries to perform
it is doing the machine's work for it.

The notes are the half most worth searching. A title is four words you chose in a hurry; the paragraph
under it is where you wrote down the thing you would later go looking for — *"the 4.7 pF"*, *"the band
edge"*, a part number.

If entries that circuitRF has **tidied away** also match and the filter is hiding them, the panel says
so on a line of its own with a count. An incomplete answer that says it is incomplete is a very
different thing from a wrong one.

If a title has been [corrected](#titles), the search finds the entry under **both** wordings — the one
in front of you and the one it replaced. Looking for a line you have already corrected is very often
exactly why you are searching.

### The same list, without the window

`circuitrf history list <workspace>` prints what this panel shows, with the same default: every entry
somebody stated an intent for, and the automatic ones hidden. `--include-automatic` reveals them,
`--kinds versions,save-points,ai-batches,automatic,tidied-away` picks what to show, and `--search
<text>` searches the same three fields the magnifier does. See the [command line](cli.html).

<div class="callout note">
<span class="label">If circuitRF is not recording</span>
<p>History is off for this workspace, or the workspace sits inside another version-controlled folder:
the buttons stay where they are and say why rather than disappearing, and a strip at the foot of the
window says so for as long as the workspace is open. A button that quietly vanished would look exactly
like a feature that had never been built.</p>
</div>

## Going back — "I broke the match network yesterday" {#going-back}

Open **View ▸ Panels ▸ History**, find the entry — the titles and the dates are what you are looking
for, and the [search](#panel) is faster than scrolling — then either press the **Go to "…"** button
under the list, or right-click the row and choose the same item there. They are one action with two
places to reach it, and both name the entry they will take you to.

What happens, in order:

1. If anything is unsaved, circuitRF asks about it, in the same words it asks when you close a
   workspace. A restore is built from what is on disk, so unsaved edits sitting on top of it would
   leave you with a workspace matching neither state.
2. The state you are in now is kept, so you can come forward again.
3. The files go back. Anything created since is taken away, because yesterday's files plus today's new
   cell is a workspace that never existed.
4. **Only the documents whose files actually changed are re-opened.** Your panel layout, your other
   open tabs, the project tree and the Messages panel are already correct and are left exactly as they
   are. Going back to a state that differed in one schematic touches one schematic.

<div class="callout warning">
<span class="label">Undo does not survive going back</span>
<p>Every open document's undo history is cleared, deliberately. An undo after going back would re-apply
the last few minutes of the state you <i>replaced</i> onto the file you just brought <i>back</i> —
producing a document that never existed at any moment: perfectly well-formed, and wrong.</p>
</div>

Going back covers **this workspace's files**. Anything in a workspace this one refers to is not part of
this workspace's history and is left exactly as it is.

## "I went back and I want to come forward again" {#forward}

You went back to Tuesday. It was the wrong Tuesday, or you only wanted to look. **The afternoon you
just replaced is not gone**, and this is the twenty seconds in which people believe it is.

**The panel tells you, at the top of the list, on arrival.** One line, in two halves: *Now at f15b942,
Tue 14:32. Your work up to 15:07 is kept as a3c91de.* Where you are, and where the afternoon went —
each named by [the entry's own short name](#panel) and by its time, because a name answers *which* and
a time is what you are actually navigating by.

Under it, a button that says **Go back to a3c91de** — the second half of that line, again. It names
its **destination**, not a direction: going back and coming forward are one operation performed twice,
so a button labelled *Come forward again* could be pressed for ever and never tell you where the last
press had landed. Press this one and you are back where you were, and the name on it changes to the
state you just left.

It is not a different or a scarier operation than the one that brought you here — it is the same one.
Coming forward keeps a restore point of *this* state first, exactly as going back did, so there is no
step in this that you cannot undo by taking it again.

**If there is nowhere to go, the button is not there.** Going back to a state whose files were
identical to the ones you already had replaced nothing, so there is no earlier afternoon to return to;
the line then says only where you are.

**Nothing was created to make this possible.** The entry the line points at is the one circuitRF wrote
before it replaced a single file, and it is in the list with everything else. Tidying up never removes
it.

## What changed between two versions {#comparing}

Select a version and the panel lists **the documents that differ** between it and the one before it:
which cells changed, which were added, which were removed. Right-click any entry — a version or a
restore point — and choose **Compare to Current** to ask the other question: what that entry holds
that your files no longer do. When the two hold the same files it tells you so, rather than showing
you an empty list.

That is deliberately where it stops. circuitRF does not show you a line-by-line comparison of what is
inside a schematic or a layout — open both and look, or go back to one of them. A list of changed
coordinates is not a picture of what moved.

## "I typed the title wrong" {#titles}

Right-click the entry. What you get depends on which of three things it is, and the difference is only
ever about **who else has already read it**.

**The longer note is edited in the same place, at the same time.** The dialog carries both: the line,
and *More detail* under it — already open when there is something in it. You wrote them together about
one entry, and if one of them is wrong the other very often is too. Whichever of the three cases below
applies governs both halves.

| the entry | what you can do | why |
|---|---|---|
| **a restore point** | **Rename this entry…** — change the label to anything. You can also **Tidy this away**. | Nothing chains to a restore point, no copy takes one and no send carries one. It is your machine's own safety net and the label is yours alone. |
| **a version you have not shared** | **Edit Comments…** — the title changes, and that is the end of it. | Nobody else has seen it, so there is nothing to put out of step. |
| **a version you have sent to another copy** | **Edit Comments…** — this adds a correction *beside* the original. | The original string is on somebody else's disk. circuitRF can put your wording in front of it; it cannot reach into their copy and take the old one out. |

**Correcting a title or a note never changes what the version holds.** The files, who kept it and when
are the ones already recorded, in all three cases. A correction is about what you wrote, never about
the design.

circuitRF works out which case applies — you do not have to know whether a version has been sent. If it
cannot tell (there is another copy configured and this machine has never heard from it), it takes the
cautious reading and offers you the correction rather than the rename.

<div class="callout warning">
<span class="label">A correction does not erase the original</span>
<p>On a version that has been shared, <b>the original wording stays in the history and can still be read
by anyone holding a copy of this workspace, including the copies already sent.</b> What the correction
buys you is that your wording is what they see first. If a title contains something that must never have
left your machine at all, that is a different problem and it has a
<a href="#rewriting">different answer</a>.</p>
</div>

**Letting a restore point go is the same thing tidying up does.** It stops appearing in the main list,
it is still there under *tidied away* in the filter, you can bring it back, and it frees no disk space
until you [reclaim](#disk) — which you have to ask for. There is nothing here that destroys a state.

**Where a correction goes.** It travels with the version it is attached to: whoever pulls or clones
this workspace next sees your corrected wording in their own History panel, with the original one click
away in the entry's expander, exactly as you see it here.

**Headless:** `circuitrf history rename <workspace> --point <n> --label "…"`,
`circuitrf history forget <workspace> --point <n>`,
`circuitrf history retitle <workspace> --title "…"` (the newest version) and
`circuitrf history correct <workspace> --version <id> --text "…"`. Each of the three that changes
wording also takes `--note "…"` for the longer note — and leaving `--note` off leaves whatever is
there alone, so correcting a title never quietly discards the paragraph under it. `--note ""` removes
it.

Recording one headlessly is the same flag: `circuitrf history commit <workspace> --title "…"
--note "…"` and `circuitrf history checkpoint <workspace> --intent "…" --note "…"`.

## "I went back to an old version, then kept working" {#restore-then}

This is one line of work, not two, and there is nothing to manage.

1. You go back to Tuesday's version. The state you were in a moment ago is kept first, so nothing is
   lost — it is in the History list like any other entry, and [the panel offers it to you by
   name](#forward) if you picked the wrong moment.
2. You keep working. The files on disk are Tuesday's, plus whatever you do next.
3. You keep a version. **That version records that it was brought back from Tuesday's**, naming it and
   the date it was kept.

That last part is the one worth knowing about. Without it, the list would show two versions in a row
where the second undoes most of the first, and there would be nothing anywhere saying why — it would
read as a change of mind. The line says what actually happened, and it appears **only** on the version
that followed a going-back. The next one after that is your own work again and carries no such line.

<div class="callout note">
<span class="label">Going back is still not a one-way door, even after you keep a version</span>
<p>The restore point taken just before you went back survives everything you do afterwards, including
keeping a version on top of the mistake. If you went back to the wrong moment and only noticed later,
that entry is still in the History list.</p>
</div>

## Gaps in the list {#gaps}

If history was [switched off](#off) for a stretch, the list shows that
stretch **as a gap**, with its dates and the reason — *"Recording was off from 3 Mar to 17 Mar. Anything
done in between is not in this history."*

It is not shown as a quiet fortnight in which you happened not to keep anything. Those are completely
different situations and only one of them means the work is recoverable.

## "My results are gone" {#results}

They were never kept, and that is deliberate — go back to a restore point and your `.npy`, `.spl`,
`.lpcwave` and `.mat` files are still sitting exactly where they were.

**A result is a function of the design and the engine**, so the design is the thing worth keeping: run
the analysis again and you have the results back. It is not about disk space. It is also why going back
does *not* delete them: bringing back last Tuesday's design should not destroy the hours of simulation
sitting beside it.

## "circuitRF says it started keeping a history" {#started}

The first time a workspace reaches one of the [three moments](#when), circuitRF creates a small folder
inside the workspace to keep restore points in, and says so once in the Messages panel.

- **Where the setting is** — Settings ▸ Revision Control. You can turn it off entirely, or off for one
  workspace.
- **It cannot harm your design.** The folder is called `.git`; it sits beside your cells and holds only
  copies of them. Deleting it removes every restore point and touches nothing else.
- **Nothing was created before that moment.** If you only opened the workspace and looked, nothing was
  written at all.

## "I saved a copy and my history didn't come with it" {#copy}

**File ▸ Save Workspace As** makes a copy of your design and **the copy starts a history of its own.**
The original keeps every restore point it had.

That is on purpose. A history holds *every earlier version of every file it ever kept*, including files
you deleted long ago — so a copy made for one customer must not carry another customer's deleted
artwork inside it, invisibly. Save Workspace As says so on the message it posts when the copy is made.

If you want the history to travel, use **File ▸ Archive Workspace** and include history — read the
next section first.

## "I'm sending this workspace to a customer" {#customer}

**File ▸ Archive Workspace…** puts the workspace into one `.zip`: every cell, every technology, the
workspace file, and whatever else you tick — kits, referenced files, results. **The history is not in
it unless you say so**, and there is a tickbox for saying so.

### The one sentence that matters

**A file you deleted from the workspace is still in the history.**

A history holds every earlier version of every file it ever kept, and that includes files that are not
in the workspace any more. If you imported one customer's artwork, finished with it, deleted it, and
then archived this workspace for a *different* customer, **including the history would send that
artwork with it** — and nothing in the visible file tree would show it.

That is why the tickbox starts unticked. Everything else circuitRF is careful about here is a *loss*
you can recover from; this one is the opposite, and nobody can undo it afterwards.

### What each choice sends

| | what the recipient gets |
|---|---|
| **history left out** *(the default)* | a working workspace with no history. Completely normal — it opens, everything is present and current, and if they switch recording on they start a history of their own. |
| **history included** | the same workspace, **plus every earlier version of every file it ever kept, and every restore point** — so they can go back to any of them. |

**Including it is often the right thing to do.** A design handed to a partner *with* its history is a
far better handover than a snapshot, and inside your own organisation it is usually what you want. The
point of the default is only that it should be a decision, not something that happens by itself.

### What circuitRF tells you when you tick it

Before you press Archive, the dialog states — for this workspace, not in general:

- **how much larger** the archive becomes;
- **how many versions and restore points** it carries;
- and **how many files are in the history that are not in the workspace now, and what they are
  called.**

That last line is the one to read. If you recognise a filename on it, you have found the problem while
you can still do something about it.

### And it shows you every title that is about to leave

Before the archive is written, circuitRF lists **the version titles the recipient will read** — all of
them, with their dates, and with any [corrections](#titles) you have made shown in place of what they
correct.

**This is worth more than any of the correction mechanisms above**, and it is not really about careless
wording. The expensive version of this problem is a customer's name, or a part number, sitting in a
title that is about to go to a *different* customer. Nobody can catch that from memory — forty titles
written over six months are not something anyone can recall — and it takes about ten seconds to read.

It is not a box to tick. If a title says something it should not, there is a **Correct…** button on
its own row — the list re-reads itself afterwards, so what you are looking at is what is actually about
to leave. The newest version can be retitled outright and any of them can take a correction; see
["I typed the title wrong"](#titles).

**The same list appears in front of Push Changes and in front of copying a workspace**, for the same
reason and from the same computation. In front of a send it shows only the versions the other copy does
not have yet, because those are the ones actually going.

<div class="callout note">
<span class="label">An archive is the only way a restore point travels</span>
<p>A copy of a workspace (<b>File ▸ Save Workspace As</b>) carries no history at all. An archive with
history included carries the whole of it — your versions <i>and</i> your restore points. It is the
strongest handover circuitRF can make.</p>
</div>

### There is no "just the last few versions"

It sounds like the best of both, and it is not offered, deliberately. It cannot be done without
changing every entry in the history, so the copy you sent could never be compared with the one you
kept. Worse, it would invite exactly the wrong belief: having chosen "the last ten", you would assume
the thing you were worried about was gone — and a filter that missed one file, or a file renamed before
it was deleted, would be a leak you had explicitly tried to prevent and been told was handled.

**Two honest choices are better than a third that is nearly true.** If something must never leave your
machine, take it out of the history before you send it — that is
[yours to do, with `git`](#rewriting) — or send the archive without the history.

## Exchanging versions with another copy {#other-copy}

If this workspace was brought here with
[File ▸ Clone Workspace…](workspace.html#copying-a-library), two more items on the File menu apply
to it.

- **Pull Changes** brings down what is new on the copy it came from and **lists it**. The versions
  waiting for you appear at the top of the History panel, marked *on the copy this came from — not
  here yet*, with a line above the list saying how many. **Nothing in your own files is touched by the
  pull itself.** Select one to see which documents it changes; **Go back to this** puts those files
  into your workspace — and, like every other way back, keeps what you have now as a restore point
  first, so it is not a one-way door.
- **Push Changes** sends the versions *you* have kept back to that copy. Your restore points stay here:
  they are your machine's safety net and mean nothing on anybody else's.

**Neither happens by itself.** circuitRF never contacts anything without being asked, and it holds no
sign-in of its own — it uses whatever your machine's `git` is already set up with. If a sign-in is
needed that cannot be supplied, the operation stops and says what was wanted rather than waiting.

If the other side has moved on since you last brought its changes in, **Push Changes** says so and
sends nothing. Bring the changes in first, choose where the two disagree, and send again.

## "Two of us are editing the same workspace" {#two-of-you}

If two people change the same document and the changes come together, circuitRF shows you **both
versions and asks which one you want**. Who wrote each, when, and what else changed alongside it. You
pick one, and that document becomes that version **whole** — exactly the bytes that person wrote.

**circuitRF will not combine them, and that is a statement about geometry and connectivity rather than
about effort.** Interleaving two sets of edits to a polygon's outline can produce a shape that is
invalid, or valid and *wrong*, in a file that opens without a word of complaint. The same is true of a
schematic's connections: two people's wires merged line by line can produce a netlist that elaborates,
simulates, and is not the circuit either of them drew. **A design that is silently wrong is worse than
being asked a question**, because at least the question is visible.

This is also why the big centralised design-management systems in this industry lock a cellview while
someone has it open rather than merging afterwards. When merging is not possible, keeping two people out
of one file is the only strategy left. circuitRF does not run a lock server — but it is why
[referenced workspaces are read-only by default](workspace.html), which is the cheapest version of the
same idea.

<div class="callout warning">
<span class="label">Choose, do not reconcile</span>
<p>If both versions contain work you need, the answer is not to hand-merge the file. Keep one, open the
other version's copy of the document beside it, and redo the changes you want in the editor — where the
tool can tell you whether the result is a valid design.</p>
</div>

## What is kept, and what is not {#kept}

| | in a restore point? |
|---|---|
| cells, schematics, symbols, layouts, technologies | **yes** |
| the workspace file — analyses, references, configuration | **yes** |
| simulation results (`.npy`, `.spl`, `.lpcwave`, `.mat`) | no — re-run the analysis |
| your own panel layout, open tabs and colour theme | no — they are yours, not the design's |
| generated PCell artwork | no — it is rebuilt from the layout |
| a folder inside the workspace that keeps a history of its own | no — it is left entirely alone |
| a file you answered **"never include files like this"** about | no — **and it is not in a restore, and not in a copy anyone takes** |
| a file left out because nobody was there to be asked | **not yet** — the entry says so and names it, and circuitRF asks the next time you keep a state yourself |
| anything saved while history is switched **off** for the workspace | no — **but everything recorded before you switched it off is still there** |
| anything at all, when your workspace sits inside another version-controlled folder | no — circuitRF records nothing there, and says so |
| the history, in a **workspace archive** | **not unless you tick it** — and you still have it either way. It is the only way a restore point ever travels: see [sending a workspace to a customer](#customer) |
| restore points, in a workspace **copied from an address** (File ▸ Clone Workspace…) | no — **and not recoverable.** The versions its author kept do come; their restore points belong to the machine they were taken on, and yours start here. See [using a library another team maintains](workspace.html#copying-a-library) |
| **which version of a referenced library** your design uses | **yes** — so going back brings the library back with your files, not just the files. See [going back, with the library included](workspace.html#pinning-restore) |
| the **title** you typed on a version you have not shared | **yes — and it is yours to correct.** See ["I typed the title wrong"](#titles) |
| the **title** you typed on a version you have already sent | **yes, and it stays.** You can add a correction that shows in place of it and travels with it; the original wording stays in the history and can still be read by anyone holding a copy |
| the **label** on a restore point | **yes — and it is yours to correct, or to let go of, at any time.** It never left this machine |
| a restore point circuitRF **tidied away** | **yes, still** — it stays in the list marked *tidied away*, and you can bring it back, until you reclaim the space (which asks first) |

### Unusually large files

The first time something much larger than a design document would go into the history — an imported
artwork file, a whole fabrication set — circuitRF asks what it is:

- **Include it** — this is design input. It is kept: one copy now, and one more only when it changes.
- **Leave it out this time** — nothing is written, and you will be asked again.
- **Never include files like this** — it is a by-product. Files matching it are not kept, so they are
  **not in a restore and not in a copy**. They stay on disk exactly as they are.

There is deliberately no "keep it once, then ignore it": it cannot be made to work honestly. Either
nothing would change while the button said it had, or your history would hold one stale version of a
file that is missing from every copy anyone else takes.

**At a moment nobody is at** — a workspace closing, or an assistant working — circuitRF does not stop
to ask. It leaves the file out, records that it did, shows the entry as incomplete with the file's
name, and asks you the next time you keep a state yourself. Leaving a file out can be undone; putting
one in cannot.

## Tidying up — "I can't get back to last month" {#tidying}

circuitRF thins the automatic restore points as they get old, so a workspace you have had open every
day for two years does not carry two years of entries. **Two rules decide what goes**, and the first
one is the one worth knowing:

- **The newest ones are always kept, however old they are.** Settings ▸ Revision Control ▸ *Always keep
  at least* sets how many. This is not a nicety: a computer's clock is something anyone can change, and
  a machine that wakes up believing it is 2140 would otherwise think every restore point had expired.
  The count floor means a wrong clock costs you nothing at all, which is also why the field will not go
  below its minimum.
- **After that, age.** Settings ▸ Revision Control ▸ *Keep them for* sets how long.

**Ones you asked for are never thinned.** Nor is anything you marked **Keep permanently**, nor the
entries that mark where you switched history off and on again.

<div class="callout note">
<span class="label">Thinned is not deleted, and the list still shows it</span>
<p>A restore point circuitRF tidies away stays in the list, marked <b>tidied away</b> and shown a little
dimmer. Select it and press <b>Bring this back</b> and it is an ordinary entry again. Thinning stops
circuitRF <i>offering</i> a state; it does not destroy it, and nothing destroys it until you ask —
see <a href="#disk">"my disk is full"</a> below.</p>
</div>

**So turning the retention setting down is not the door it looks like.** If you set it to seven days
and then want last month back, the entries are still there and still marked; bring one back and go to
it. The only thing that ever actually removes them is **Reclaim Space**, which asks first and says
exactly what it will destroy.

**If circuitRF ever tells you it was about to tidy away far more than usual and did not**, that is the
safety catch working: it means the dates it is reading do not make sense — almost always a clock that
has jumped. **Nothing was removed.** Check the machine's date and time, and the next tidy-up will be
ordinary.

## "My workspace is inside another version-controlled folder" {#enclosing}

If your workspace folder sits *inside* a folder that already has a version history of its own — a
project repository, a shared checkout — **circuitRF stops. It keeps no history for that workspace**, it
says so once when you open it, and a strip at the foot of the window says **History held** for as long
as the workspace is open.

**Why it stops rather than joining in.** A restore point captures *everything* in the workspace,
including files you never opened — that is the whole point of it. Doing that inside a folder somebody
is also using for something else would sweep up work in progress that was nothing to do with circuitRF,
under a message circuitRF wrote. And circuitRF will not add its own settings files to a folder that
belongs to someone else's project.

**What is therefore not being kept:** nothing at all. No restore points are taken, none of the three
moments records anything, and an AI assistant is stopped rather than allowed to edit with no floor
under it.

**How to fix it:** move or copy the workspace to a folder that is *not* inside the other one. As soon
as it is on its own, circuitRF starts keeping restore points at the next moment worth keeping, and says
so.

<div class="callout note">
<span class="label">A folder inside your workspace is a different thing</span>
<p>A library somebody cloned <i>into</i> your workspace keeps its own history and is left entirely
alone — circuitRF excludes it, tells you once which folder it skipped, and everything else in the
workspace is kept normally. That is not the held state.</p>
</div>

## "This workspace already had version control and circuitRF asked me something" {#already}

If the workspace folder *itself* already has a version history — you set one up, or you were given one
— circuitRF does not take it over. It asks, once, and remembers your answer.

| your answer | what happens |
|---|---|
| **Use circuitRF's settings** *(recommended)* | circuitRF keeps restore points here and applies its own settings for how the history is looked after. Everything already in the history stays exactly as it is. |
| **Keep my settings** | circuitRF keeps restore points here under your existing settings. See below for what that costs. |
| **Don't keep a history here** | circuitRF records nothing for this workspace, and the window says **History held**. You can change your mind in Settings ▸ Revision Control. |

**What "keep my settings" actually costs.** Two things, and both are about *getting work back*, not
about tidiness:

- **A restore point circuitRF tidies away is destroyed permanently after a fortnight**, instead of
  staying recoverable for as long as you leave it. The whole reason the recovery window is measured in
  weeks is that a design that is wrong but well-formed often is not noticed for weeks — so a fortnight
  is inside the window this feature exists to cover.
- **The way back from a mistaken reset or rewrite of your own work disappears after thirty days.**

Everything already in your history survives whichever answer you give. **And one thing is circuitRF's
either way:** it records under the name in Settings ▸ Revision Control, and it never runs the scripts
your own tools attach to a change. A restore point that fired somebody's build hook — or that a hook
could refuse — would not be a safety net.

## Turning it off, and getting rid of it entirely {#off}

These are two different things and it matters which one you mean.

| | what circuitRF does | what happens to the history you already have |
|---|---|---|
| **Off** | writes nothing at all | **kept, listed, and still restorable** |
| **Removed** | not something circuitRF does — you delete one folder | gone, permanently |

**Off — Settings ▸ Revision Control ▸ *Keep a history of this workspace*.** Untick it and circuitRF
stops writing. **It deletes nothing.** Every restore point you already have stays in the list and you
can still go back to any of them; tick it again and it carries on where it left off. There is no
checkbox anywhere in circuitRF that destroys a history, and there is no "delete all history" command,
deliberately.

The setting belongs to the workspace, not to you, so:

- **The stretch while it was off is shown as a gap**, with an entry at each end saying that recording
  stopped and started. It is not shown as a quiet week in which you happened not to save anything.
- **It travels.** A copy or an archive of a workspace that was switched off arrives switched off,
  whatever the person opening it prefers — and they are told, with the setting one click away.
- **An AI assistant is stopped**, told plainly that no restore point can be kept, and offered the
  switch — before anything is changed. See [below](#assistants).

**Removed — delete the `.git` folder.** That is the whole of it: one folder, plainly named, sitting
beside your cells, using your file manager. It removes every restore point and touches nothing else.

<div class="callout note">
<span class="label">Removing the history cannot harm your design</span>
<p>Your design was never <i>inside</i> the history in any meaningful sense. A workspace is ordinary
files in a folder; the <code>.git</code> folder sits beside them and holds copies. Delete it and the
workspace opens exactly as it did, with every file present and current. Nothing you can do to the
history can damage the design.</p>
</div>

## "My disk is full and it says the history is taking the space" {#disk}

Two settings answer this, and they do completely different things.

**Compact — Settings ▸ Revision Control ▸ *Compact Now*.** Stores the same history in less space.
Nothing is discarded, nothing becomes unavailable, and it is safe to press at any time. Try this first;
on a workspace with large layouts in it, it very often is the whole answer.

**Reclaim Space — the same tab, below it.** This is the one destructive control in the feature, and it
exists because thinning frees a restore point's *listing* and not its *contents*: a large file you once
included and later regretted goes on taking space for as long as you leave it. Reclaiming frees that
space for good.

It destroys **only states that were already tidied away**, and only ones tidied away longer ago than
the age you set. It asks first, tells you how many, and says in plain words that after it nobody can
bring them back. **Every restore point still listed, and every change you recorded yourself, survives
it untouched, and your design files are never touched at all.**

**Nothing reclaims on a schedule.** circuitRF never destroys a state on its own; the age field on its
own does nothing until you press the button.

## "Something went in that shouldn't have" {#rewriting}

**This section is about a *file*.** If it is a *title* you want to change, that is
[a different thing entirely and you can do it](#titles) — the two rules are separate on purpose.

The **files** in a version are a record, and **circuitRF never alters what was recorded.** That is the
whole value of it: a version whose contents could be quietly edited later is not something you can
point at and say "this is what I sent".

So if something is in the history that must not be — a very large file you regret including, or
something that should never have left your machine — circuitRF has no button for it, deliberately, and
this is the honest answer rather than a dead end:

**It is possible, and it is yours to do, with `git` on the command line.** The history circuitRF keeps
is an ordinary git repository sitting in a folder called `.git` beside your cells; every standard tool
reads it, including whatever your IT department already supports. Rewriting a history to remove
something is a well-documented operation and there are good guides to it.

**Why circuitRF will not do it for you:** rewriting replaces every entry in the history, so every copy
anyone else has taken of this workspace stops matching and cannot be brought back into line. That is a
consequence nobody should trigger from a menu item, and it is worth doing when it is worth that.

If it is only about disk space, it probably is not what you want —
[Compact and Reclaim Space](#disk) do that without breaking anything.

## History and AI assistants {#assistants}

**Before an AI assistant changes anything in your workspace, circuitRF keeps a restore point**, labelled
with what the assistant said it was about to do — *"before: widen the output match"*. Going back to that
entry undoes everything the assistant did, in one action.

This is automatic, you do not have to ask for it, and it happens **before** the first change rather than
after the last: an assistant that stopped halfway still leaves the restore point exactly where it should
be.

It is also a floor the assistant cannot go around. If circuitRF cannot keep a restore point first —
history is off for this workspace, the workspace sits inside another version-controlled folder, the
workspace has not been saved anywhere yet, or you have unsaved changes open — **the assistant is
stopped and told, and nothing is changed.**

**Switching history off switches this off too**, which is the one case where two settings pull against
each other. That is a perfectly reasonable thing to choose and a bad thing to discover by accident, so
circuitRF says it out loud at the moment it matters: an AI edit asked for while history is off is
refused **before anything is modified**, with a button offering to turn history back on. Being stopped is the
feature working, not failing: it is not permitted to make its own backup arrangements instead, because
you would then be told you were protected by something you could not go back through.

An assistant's restore points are subject to the same tidying-up as the automatic ones. If one of them
matters, select it and press **Keep permanently**.

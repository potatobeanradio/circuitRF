---
title: The Workspace
slug: reference/workspace.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > The Workspace
lede: The window everything else happens in — documents in the middle, tool panels around them, and a folder on disk behind it all.
keywords: project, folder, library, docking, panels, window, tree
---

Every other chapter in this guide describes something you do *inside* one window. This chapter is
about the window: what the panels are, how to move them, and what a **workspace** actually is once
you close the application.

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#regions">The regions of the window</a></li>
<li><a href="#workspace">What a workspace is</a></li>
<li><a href="#other-workspaces">Using cells from another workspace</a></li>
<li><a href="#library-team">Using a library another team maintains</a></li>
<li><a href="#shared">Sharing a workspace with other people</a></li>
<li><a href="#documents">Documents and tabs</a></li>
<li><a href="#panels">The tool panels</a></li>
<li><a href="#docking">Moving, hiding and resetting the layout</a></li>
<li><a href="#several">Switching workspaces, and working without one</a></li>
</ol>
</nav>

## The regions of the window {#regions}

{{ui: workspace-regions}}

{{regions: workspace}}

<div class="callout note">
<span class="label">Where the menu bar is on macOS</span>
<p>The figure shows the menu bar inside the window, which is where Windows and Linux put it. On macOS
the same menus are in the system menu bar at the top of the screen, and the window starts at the
toolbar. Nothing else differs — the commands, their order and their shortcuts are the same, with
<kbd>⌘</kbd> in place of <kbd>Ctrl</kbd>.</p>
</div>

## What a workspace is {#workspace}

A **workspace is a folder.** Its name is the folder's name, and it holds a `.cws` file plus a folder
per [cell](file-formats.html#hierarchy). Membership is the filesystem itself: the Project panel shows you
what is in the folder, so copying a cell folder in with the Finder or Explorer puts that cell in the
workspace, and there is no index to repair afterwards.

The `.cws` records **configuration, never content** — referenced libraries, bookmarked "Known
Files", the default technology and assembly rules, and the other projects and kits this one uses. A
cell is referenced, not embedded, so the same cell or library can belong to several workspaces at
once. The full on-disk layout, file type by file type, is in
<a href="file-formats.html">File Formats</a>.

The things that describe **your** view of the project rather than the project itself — the panel
arrangement, which documents were open, which tree categories you expanded, the colour theme — are
kept in a second file beside it, the **`.cwsuser`**. It is optional: a workspace with no `.cwsuser`
opens on the default arrangement and nothing is wrong. It is also the one file you can safely delete
to get your panels back if they end up somewhere unusable. See
<a href="file-formats.html#cwsuser">Your own view of a workspace</a>.

Two consequences worth knowing early:

- **A workspace is version-controllable.** It is text files in folders; nothing is hidden in a binary
  project database.
- **Moving a workspace is moving a folder.** To send one to somebody else, use
  **File ▸ Archive Workspace…**, which additionally pulls in the things it references from outside
  the folder — libraries, technologies, optionally kits and results — and repoints the references at
  the copies, so the archive opens on a machine that has none of them.

## Using cells from another workspace {#other-workspaces}

A cell does not have to live in the workspace you are working in. Two commands bring one in, and they
answer different questions.

**File ▸ Reference Workspace…** points this workspace at another one and gives it a short **alias**.
It appears in the Project panel as one row at the top level, carrying a network-folder icon, and all of
its cells are inside it ready to place. Nothing is copied: the cells stay where they are, and when their
owner edits one, your design picks up the change the next time it draws — no restart, and nothing to
re-import.

A referenced workspace is re-read when you open your workspace, when you expand its branch in the
Project panel, and when you press **Refresh** there — not every time you switch back to the window. So
if a colleague has just added a cell to the shared library, press Refresh to see it. This keeps the
application responsive when the library is at the far end of a slow network link.

**File ▸ Add Cell to Workspace…** takes a single cell from another project — as does dragging a cell
from one workspace window onto another. Either way you are asked the same question:

- **Copy the cell in.** You get your own independent copy. Later changes on the other side do not reach
  you, and yours do not reach them. If the cell places cells of its own, you are asked whether those
  come along as copies too or stay referenced where they are.
- **Reference the cell where it is.** *That cell* — and only that cell — appears in your Project panel
  as one row at the top level, carrying the same network icon a referenced workspace does, and is
  instanced from there. One master, many
  users. The rest of the other project's cells do not come with it. Its own sub-cells are always
  referenced with it — a referenced cell is the other project's, all the way down. Right-click the row
  and choose **Remove Cell Reference** to stop listing it; nothing is deleted.

<div class="callout note">
<span class="label">Which to choose</span>
<p><strong>Reference</strong> when the cell belongs to somebody else and should stay theirs — a company
standard-parts library on a network share, a colleague's verified amplifier block, a footprint set
maintained by one person for the whole team. Everyone gets the librarian's corrections automatically,
and nobody has a private copy that has quietly drifted.</p>
<p><strong>Copy</strong> when you are about to change it, when you want the project to be
self-contained, or when you are branching off a known-good design to try something. A copy is yours to
break.</p>
</div>

**Referencing a cell needs both workspaces on the same [technology](stackup.html).** A cell drawn for a
different stack-up would place and be silently wrong, so when the two disagree the Reference option is
offered greyed out with the reason beside it, and Copy is what you are left with. (Adding the reference
to the *workspace* is not blocked — it costs nothing until a cell is actually placed — but circuitRF
warns you at that point.) You are also told before copying if the cell uses parts from a kit this
workspace has not imported.

Because a reference is written as *alias + cell name* rather than as a path, moving the shared library
later is one edit in one place, not a hunt through every document that used it.

**If a reference does break** — you removed it, or the other project moved — every instance that placed
that cell draws as **Not Found**. Right-click one, in the schematic or the layout editor, and choose
**Re-reference Cell…**. It looks for the cell first: in this workspace, in the ones open in other
windows, in the ones you already reference, and in your recent list. If it finds it, the reference is
put back — usually without changing your document at all, because the alias goes back under the name it
had. If it finds nothing, or two cells of that name, it asks you to point at the cell's folder. Either
way the repair covers *every* instance of that cell in the document, in one undo step. Referencing a single
cell still records that alias — it is how the cell is addressed — but the alias itself draws no row, so
one referenced cell is one row. Run **File ▸ Reference Workspace…** on the same project later and the
existing alias is promoted rather than duplicated: the whole workspace then appears alongside, and
anything already placed goes on resolving.

The Project panel's filter button (the funnel) has a checkbox for each of these — *Referenced Cells* and
*Referenced Workspaces* — both on by default, so either kind can be put out of the way without touching
your own cells.

### Fixing a cell in a library you reference {#editing-referenced-cells}

*(This is the maintainer's side. If you only **use** a library somebody else looks after, the section
you want is [using a library another team maintains](#library-team).)*

**Referenced cells are read-only from here.** You can open one, read it, push into its hierarchy and pull
it about to understand it — but the first time you try to *change* something, circuitRF stops you and
says why. The refusal comes with a button: **Open ‹library›**, which opens that workspace in a window of
its own and takes you straight to the cell you were trying to edit. Edit it there and save normally.

That is not circuitRF being fussy about a file it could perfectly well write. It is the only point in the
sequence where you can still do something sensible about it. Editing a library cell in place *works* — the
file is written, your simulation picks it up, and everything looks right:

- the fix reaches you and **nobody else**; every colleague goes on using the cell as it was;
- the library's owner was never asked, and has no idea;
- the next time they publish, your edit is overwritten, or it clashes and there is nothing to merge it
  back from.

Every step of that is silent. One refusal, at the moment you type, replaces the lot.

**The cell stays in the other workspace, and that is the point.** Opening the library as a workspace of
its own means the corrected cell is the library's corrected cell — everyone referencing it gets it, and
the person who maintains it can see what changed. Your own workspace records nothing about the edit,
because the cell was never yours.

**A cell is edited in one window only.** If the library is already open in another window, the edit does
not become possible here — that window comes forward with the cell in front of you instead, and this
window closes its read-only view of it. Two editors over one file would mean two sets of unsaved changes
over the same file, and whichever you saved second would silently throw the other away.

<div class="callout note">
<span class="label">If you really do maintain the library</span>
<p>Right-click the referenced workspace's row in the Project panel and choose <strong>Allow Editing
Through This Reference…</strong>. circuitRF asks once, then lets you edit its cells from here. The row is
marked with a pencil afterwards, and so is the tab of every document you open from it, because writing
into somebody else's project is not something to have to remember. Two people doing this in one library
at the same time is not arbitrated — the same last-save-wins that applies to any shared folder.</p>
<p>Choose <strong>Make Reference Read-Only</strong> on the same row to put it back. Existing workspaces
are read-only too: a reference made before this existed is treated as read-only, because that is the safe
reading and not the one you happened to have.</p>
</div>

### The pencil on a document tab {#pencil-mark}

{{ui: editable-reference-tab}}

The pencil appears beside a document's name when that document lives in **another workspace that you
have allowed editing through**. It answers one question, and only that one: *if I press Save, whose
project does this get written into?* A tab with no pencil is a document of the workspace you have open.

**You have not done anything wrong.** The mark is not a warning and there is no error behind it. It is
there because allowing editing through a reference is a deliberate, unusual choice somebody made once —
often weeks ago, possibly not by you — and the moment it matters is the moment you are about to save,
which is exactly when nothing on screen would otherwise mention it.

A few things it is worth knowing:

- **It never turns green, and there is no second state.** The mark is either there or it is not. Green
  would imply a check that had passed, and nothing is being checked.
- **It is not about unsaved changes.** That is the bullet in the window title, and the two are
  independent.
- **It does not depend on whether the other workspace is open.** It describes the relationship between
  the two projects, which stays true whatever windows you have.
- **To make it go away**, right-click that workspace's row in the Project panel and choose **Make
  Reference Read-Only**. The reference keeps working; you simply read through it again, which is the
  default for every reference.

The same pencil appears on the referenced workspace's own row in the Project panel, which is where you
can act on it.

## Using a library another team maintains {#library-team}

This is the arrangement most RF groups end up with: one person, or one team, maintains a set of
verified cells, and everybody else references them. This section is the **consumer's** side of it. The
maintainer's side is [fixing a cell in a library you reference](#editing-referenced-cells) above, and
the two are worth reading together.

### Getting the library onto your machine {#copying-a-library}

If the library lives on a share you can already reach, **File ▸ Reference Workspace…** is all you need.

If instead you were given an *address* — the library is kept somewhere central and handed out rather
than sat on a share — use **File ▸ Clone Workspace…**. Paste the address, say which folder to put
it in, and press Clone. What arrives is an ordinary workspace: open it, read it, reference its cells
from your own designs.

Two things are worth knowing before you press the button:

- **circuitRF never asks you for a sign-in and never stores one.** It uses whatever your machine's
  `git` is already set up with — whatever your IT department gave you. If the address needs a sign-in
  that cannot be supplied, the copy **stops and says what was wanted**. It will not sit there waiting.
- **Their restore points do not come with the copy.** Every version the library's author deliberately
  *kept* does come, and that is what you would ever want to look at. Their automatic restore points
  belong to the machine they were taken on. circuitRF starts a safety net of your own the first time it
  has something to record here.

Once a workspace has been cloned this way, **File ▸ Pull Changes** brings down what is new on the copy
it came from and lists it at the top of the History panel — marked, and applied to your files only if
you choose one. **File ▸ Push Changes** sends the versions you have kept back, if you are allowed to.
Neither happens by itself: circuitRF never contacts anything without being asked.

### "I want my design to keep using the version I tested against" {#pinning}

By default a referenced workspace is a live link. The librarian corrects a cell, and the next time your
design draws, it uses the corrected cell. That is usually what you want — it is the reason you
referenced the library instead of copying its cells.

It is not what you want on a design that has been signed off.

Right-click the library's row in the Project panel and choose **Use a Fixed Version…**. From then on
your design uses *that* version of the library and nothing else.

<div class="callout note">
<span class="label">What this actually buys you</span>
<p>It is the difference between "this design uses the amplifier from the shared library" and "this
design uses <em>the version of it we measured</em>". The first sentence is not reproducible: it means
whatever the library contains on the day somebody opens the design. The second one is.</p>
<p>It is also what makes going back to an earlier state <strong>complete</strong> — see below.</p>
</div>

**The librarian carries on as before.** They publish whenever they like and nothing in your workspace
changes. When they publish something newer, the library's row in your Project panel says so, and
there is a **Take the Newer Version…** item on it. Nothing is taken until you take it. There is no
prompt and no notification that interrupts you.

**Taking the newer version is a change to your workspace**, so it goes into your history with today's
date. Six weeks later, when something has stopped working, *"when did this design start using the new
library?"* is a question you can actually answer.

### The part that surprises people {#pinning-surprise}

**With a fixed version, editing a cell in the library and coming back to your design does not show the
change.** That is not a bug and it is not a refresh problem — it is the whole point. The design goes on
matching what it was verified against, and it tells you a newer version is available instead.

If you are the person maintaining the library *and* the person using it, this will catch you out at
least once. Choose **Follow the Newest Version** on the library's row while you are working on it, and
fix a version again when you are finished.

A reference with a fixed version is always read-only from your workspace, even if you had allowed
editing through it. What you would be editing is circuitRF's copy of that one version — not the library
— so the change would reach nobody and would not survive.

### Going back, with the library included {#pinning-restore}

This is the reason the feature is worth the bother.

[Restore points and versions](history.html) record your workspace — which now
includes *which version of the library it uses*. So going back to last Tuesday brings back last
Tuesday's files **and** last Tuesday's library. Without that, a restore hands you your own files back
and silently keeps today's library, which is a state nobody asked for and nothing tells you about.

### When the version cannot be reached {#pinning-unreachable}

**A fixed version is a name, not a copy.** The cells still live in the other workspace. If that
workspace is moved away, or its owner rewrites its history so the version you named no longer exists,
circuitRF says so plainly and the cells stop resolving.

It deliberately does **not** fall back to whatever the library contains now. Falling back would look
like everything was fine while your design quietly used content it had never been checked against,
which is precisely what fixing a version exists to prevent.

If you need the library's *content* to travel with your design — to a customer, to an archive, to a
machine that will never reach the library — that is a
[workspace archive](history.html#customer), which is a different tool for a different problem.

## Sharing a workspace with other people {#shared}

Put a workspace on a network share and several people can reach it at once. What that means depends on
whether they can write to it.

(That is separate from a share whose *permissions* stop you writing, below. A referenced workspace is
read-only whether or not the folder is — the two reasons stack, and Save says which one applies.)

**A read-only share is the intended shape, and everything still works.** If the folder's permissions
allow you to read but not write — the usual arrangement for a company library, where one librarian
maintains the masters — circuitRF notices when you open it and says so once. Any number of people can
have it open at the same time with nothing to arbitrate. You can browse it, open its schematics, push
into a hierarchy, read a cell's parameters and reference its cells from your own designs. You can even
edit a document to understand it. What you cannot do is save on top of it: **Save** is greyed out with
the reason shown, and **Save As** puts your version into a workspace you own.

**If two people open a *writable* workspace at the same time, circuitRF tells you.** The second person
to open it sees a notice naming who has it open and on which machine, and chooses:

- **Open read-only** — browse and read it without writing anything back. The safe answer, and the
  default.
- **Open anyway** — work normally, knowing that if you both save, the last save wins.
- **Cancel** — leave it alone.

<div class="callout note">
<span class="label">It is a notice, not a lock</span>
<p>circuitRF cannot reliably tell across a network share whether somebody is still in there, so it
never blocks you — a lock it could not clear would be worse than the problem. A session that crashed
on your own machine is recognised straight away and says nothing; one left behind on another machine
is ignored after several hours.</p>
<p>The thing genuinely at risk is not your schematics — those are separate files, and a clash is
visible. It is the workspace's own settings: the panel arrangement, the open-document list and the
list of referenced workspaces. Two people saving those overwrite each other silently, which is why
the notice exists.</p>
</div>

**For a team, the arrangement that avoids the question entirely** is one shared library workspace,
read-only to everyone but its maintainer, referenced by each engineer's own project. Everybody reads
the same masters, nobody can damage them, and each person's own work lives in a workspace only they
open.

## Documents and tabs {#documents}

The middle of the window is the **document area**, and everything you open lands there as a tab:
schematics, symbols, [layouts](layout-editor.html), [data displays](data-display.html),
technologies, [EM setups](em-setup.html), and the tool documents
[harmonicaRF](harmonicarf.html) and [wBond](wbond.html). A fresh workspace opens on a single
**Welcome** tab, which is a placeholder and closes like any other.

Opening a document is a double-click in the Project panel. A cell can hold three views — schematic,
symbol and layout — and each opens as its own tab, so the schematic and the layout of the same cell
are two tabs you can put side by side.

Tabs are rearrangeable, splittable and detachable: drag one along the strip to reorder it, drop it
against an edge of the document area to split the area in two, or drag it clear of the window to
give it a window of its own. A detached document is still part of the workspace — it saves, runs and
undoes exactly as it did while docked — and where you left it is recorded in the `.cwsuser`.

## The tool panels {#panels}

Nine panels can be on screen; **View ▸ Panels** lists all of them, and the four used most often have
toolbar buttons of their own.

<table class="param-table">
<thead><tr><th>Panel</th><th>Shows</th></tr></thead>
<tbody>
<tr><td class="nowrap"><b>Project</b></td><td>The workspace's cells and their views, its technologies and its libraries. Double-click to open; right-click for the actions on a cell.</td></tr>
<tr><td class="nowrap"><b>Library</b></td><td>Every component you can place, by category and searchable. Click a tile to arm it, then click on the canvas to drop it. See <a href="components.html">Components</a>.</td></tr>
<tr><td class="nowrap"><b>Properties</b></td><td>The parameters of the current selection, editable in place. What it shows depends on what is selected — a component, a wire, a layout shape, a bond-wire array.</td></tr>
<tr><td class="nowrap"><b>Analyses</b></td><td>The analyses the open test bench will run, and the Run button that runs them. See <a href="simulations.html">Simulations</a>.</td></tr>
<tr><td class="nowrap"><b>Messages</b></td><td>What the application did, with warnings and errors. Each message links back to the file or object it is about.</td></tr>
<tr><td class="nowrap"><b>DRC</b></td><td>Design-rule violations from the last check, each one selectable in the layout it came from.</td></tr>
<tr><td class="nowrap"><b>History</b></td><td>Everything circuitRF has kept of this workspace — the states it kept for you and the versions you kept on purpose — with a filter and a search. See <a href="history.html">History</a>.</td></tr>
<tr><td class="nowrap"><b>Wire Profile</b></td><td>Bond wires seen from the side — loop height and span. See <a href="wbond.html">wBond</a>.</td></tr>
<tr><td class="nowrap"><b>Array Inductance</b></td><td>The inductance computed for the selected bond-wire array.</td></tr>
</tbody>
</table>

The toolbar's panel buttons are **toggles**, not "open it" buttons: press once to bring the
panel back where you last had it in this workspace, press again to close it. The menu items under
**View ▸ Panels** only ever show a panel — a menu item named after a panel must not close it.

## Moving, hiding and resetting the layout {#docking}

Tool panels dock the same way documents do. Drag a panel by its tab: onto another panel to tab them
together, against an edge to give it a column or a row of its own, or out of the window to float it.
Drag a splitter to change the proportions. All of it is saved into the `.cwsuser` beside the `.cws`,
so a workspace reopens arranged the way you left it — and deleting that one file is how you get the
default arrangement back if it ever ends up somewhere you cannot use.

Three commands cover the rest:

- **View ▸ Hide Dockers** (<kbd>Ctrl/⌘+Shift+H</kbd>) closes every tool panel and gives the whole
  window to the documents. Pressing it again puts them all back exactly where they were — it is a
  toggle, not a close.
- **View ▸ Reset Layout** returns to the arrangement **Settings ▸ On Launch ▸ Window Layout**
  names. That setting is the only place an arrangement is chosen, and it offers three:
  *Project Tree Focus* and *Library Focus* (Project and Library tabbed together on the left,
  differing only in which tab is on top) and *Project Tree & Library* — Project on the left, the
  Library in its own column to the right of the documents. The last is the shipped default and is
  what the figure above shows.
- **Fit Windows to Frame** on the toolbar pulls every floating window back into the workspace.

## Switching workspaces, and working without one {#several}

**File ▸ New Workspace…** (<kbd>Ctrl/⌘+N</kbd>) and **File ▸ Open Workspace…**
(<kbd>Ctrl/⌘+O</kbd>) both work **in place**: the window you are in becomes that workspace. Anything
unsaved is offered to you first, the dock layout is rebuilt from your Window Layout setting, and the
documents the incoming workspace's `.cwsuser` had open are reopened. **File ▸ Open Recent** is the
same operation against a list of the last few.

You do not need a workspace at all to get started. With no workspace open, **File ▸ New Schematic**
opens a scratch sheet that belongs to no cell and no folder (with one open, it creates a new cell); wire it up and simulate it immediately. If it turns out to be
worth keeping, **File ▸ Save Schematic As…** puts it into a workspace as a cell. With no workspace
open the Project panel lists the workspaces you had open recently instead of a tree, so getting back
to one is a single click.

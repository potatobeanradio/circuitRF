---
title: Settings
slug: reference/settings.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Settings
lede: Every tab of the circuitRF Settings dialog, control by control — what each one changes, when it takes effect, and which of them are shared with harmonicaRF and wBond.
keywords: preferences, options, configuration, theme, dark mode, colours, colors, technology, ctech, stackup, default technology
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#opening">Opening it, and when a change takes effect</a></li>
<li><a href="#general">General</a></li>
<li><a href="#technology">Technology</a></li>
<li><a href="#security">Security &amp; Permissions</a></li>
<li><a href="#revision-control">Revision Control</a></li>
<li><a href="#color-theme">Color Theme</a></li>
<li><a href="#wirebonds">Wirebonds</a></li>
<li><a href="#em3d">3D EM</a></li>
<li><a href="#footer">The footer: Help, Revert, Cancel, Close</a></li>
<li><a href="#where">Where the settings are stored</a></li>
</ol>
</nav>

Settings are **per user, not per workspace**, with one deliberate exception noted below. A workspace
someone sends you cannot change how your copy of circuitRF behaves — that is the reason several of these
controls exist here rather than in the document that uses them.

<div class="callout note">
<span class="label">The one setting that does travel</span>
<p><b>Keep a history of this workspace</b>, on the Revision Control tab, is stored <i>with the
workspace</i> and not with your preferences. It has to be: an application-wide switch would be right for
the first workspace you open and silently wrong for the second. So that one row travels with a copy of
the workspace, and it is the only thing on this dialog that does. Everything else here is yours.</p>
</div>

## Opening it, and when a change takes effect {#opening}

**File ▸ Settings…**, or `Ctrl` `,`. On macOS it is in the application menu instead — **circuitRF ▸
Settings…**, `⌘` `,` — where that platform's users look for it.

The dialog is **not modal**. It stays open while you carry on working, so you can change a setting and
watch what it does without closing anything.

<div class="callout note">
<span class="label">Every tab but Color Theme writes immediately</span>
<p>A combo box, a checkbox or a number on the <b>General</b>, <b>Technology</b>, <b>Security &amp;
Permissions</b>, <b>Revision Control</b> and <b>Wirebonds</b> tabs is saved the moment you change it. There is no "apply" step, and
<b>Cancel does not undo it</b> — Cancel and Revert act on the colour editor only, which is the one tab
that edits a live document-like thing you might want to abandon.</p>
</div>

Most settings apply to the next thing you do: a launch setting takes effect at the next launch, a
wirebond default applies to the next wire you draw. Three are live: the **theme**, which recolours
every open window as you click it, the **message timestamp format**, which re-renders the Messages
panel as you change it, and every **colour role**, which repaints the application as you drag the
slider.

## General {#general}

{{ui: settings-general}}

### On Launch

| Control | What it does |
|---|---|
| **Action** | What circuitRF does with no file to open: show the Welcome screen, or go straight to a new Schematic, Workspace, Data Display, Symbol or Layout, open a workspace, or start harmonicaRF. The default is **Welcome**. |
| **Window Layout** | The dock arrangement the shell opens with — *Project Tree Focus* and *Library Focus* tab the two panels together on the left and differ only in which tab is on top; *Project Tree &amp; Library* (the default) puts the Project Tree on the left and the Library in its own column to the right of the documents. |
| **Show Dockers** | Whether the tool panels are open at launch and when a new workspace is created. Turn it off and they start collapsed, exactly as **View ▸ Hide Dockers** collapses them. |

<div class="callout note">
<span class="label">Window Layout is also what Reset Layout resets to</span>
<p><b>View ▸ Reset Layout</b> deliberately offers no choices of its own — it restores whatever this
setting names, so there is one place a layout is chosen and not two that can disagree. The panels
themselves are described in {{anchor: workspace.html#panels|the workspace chapter}}.</p>
</div>

### Theme

Three buttons, one of them always in force: **System** follows your operating system's light/dark
setting, **Light** and **Dark** pin circuitRF to one of them regardless. The change is immediate and
applies to every open window and everything drawn in them.

This is the light/dark *variant*. Which colour each thing is drawn in is the
{{anchor: settings#color-theme|Color Theme}} tab, and every colour theme carries both variants — the two
settings compose rather than competing.

### Copy / Export

| Control | What it does |
|---|---|
| **Copy color** | The light/dark variant a schematic is rendered in when you copy it to the clipboard: *Follow System* (the variant the application is currently showing), *Force Light*, or *Force Dark*. Pasting a dark schematic into a white document is the case this exists for. |
| **Transparent background** | Whether the copied picture carries its background or leaves it clear. On by default, so a pasted schematic takes the colour of whatever it lands on. |

### Design Rules

**Check design rules before exporting** runs a design-rule check before writing GDSII, DXF or Gerber.
It is on by default and it is **not** a gate: a clean design exports with no interruption, and when
there are violations they are listed first and you can still go ahead. Catching a spacing error before
it reaches a fabricator is most of what DRC is worth, and a check you have to remember to run by hand
is one you will forget before the export that mattered.

**Run LVS before exporting** makes the same offer for
{{anchor: lvs|layout versus schematic}}, and it is **off** by default. The difference is that a
design-rule check needs only artwork, and LVS needs a drawing to compare the artwork against &mdash;
exporting artwork on its own is a legitimate thing to do, so this one is opt-in. Off with the box
visible is not the same as absent: you can see that the offer exists.

The rules themselves, and the panel that lists the violations, are in
{{anchor: layout-editor.html|the layout editor chapter}}.

### Messages

**Timestamps** — how the Messages panel stamps each line: *Time*, *Date + Time*, or *Hidden*. This one
is live; the panel re-renders as you change it.

## Technology {#technology}

{{ui: settings-technology}}

Which technologies **File ▸ New Workspace** offers, and which one it opens on. A technology is a
`.ctech` file — the drawing layers, the stackup, the design rules and the drafting defaults of one
board or one process — and it is what a layout is drawn against. The four circuitRF ships are listed
here, and you can add your own.

| Control | What it does |
|---|---|
| **Add…** | Copies a `.ctech` into circuitRF's own technologies folder, so it is offered for every new workspace from then on. Pick several at once and each is reported on its own terms. |
| **Remove** | Stops offering a technology you added, and deletes circuitRF's copy of it. It asks first. Only your own can be removed; the four circuitRF ships cannot. |
| **Set as Default** | Makes the selected technology the one the New Workspace picker opens pre-selected on. |
| **Open Folder** | Opens the folder your technologies live in. A `.ctech` copied there by hand is offered exactly as one added here. |

Selecting a technology shows what it is made of — how many drawing layers it declares, how many
conductors, dielectrics and vias are in its stackup, what those add up to, how many design rules it
carries and which units its editors open in. It is enough to tell two boards apart without opening
either.

<div class="callout note">
<span class="label">Removing a technology cannot break a design</span>
<p>Creating a workspace <b>copies</b> the technology into that workspace's own <code>tech/</code>
folder, and everything in the workspace reads that copy from then on. So this list is about what is
<i>offered</i>, never about what an existing design depends on — removing one here leaves every
workspace made with it exactly as it was, and is also why a workspace opens correctly on a machine
that has never seen the file.</p>
</div>

<div class="callout note">
<span class="label">The file name is the id</span>
<p>A technology's <b>name</b> is what this list displays and comes from inside the file; its
<b>id</b> is the file's name without the extension, and that is what a workspace records, what
<code>--tech</code> takes on the command line, and what the default setting stores. Two technologies
cannot share one — adding a file whose name is already in use is refused, naming the collision,
rather than one of them quietly shadowing the other. Rename the file you are adding, or remove the
one that is there.</p>
</div>

<div class="callout note">
<span class="label">The default is a pre-selection, not a rule</span>
<p>Every new workspace can still choose a different technology, or <b>None</b> — a workspace with no
technology is a supported state, and layouts in it draw on a fallback palette. The default is also
what <code>circuitrf new workspace</code> uses when it is given no <code>--tech</code>, so the
command line creates what the dialog would; see {{anchor: cli.html|the command-line chapter}}. If
you remove the technology you had set as the default, new workspaces go back to circuitRF's own
until you choose another.</p>
</div>

To author or edit a technology rather than install one, see
{{anchor: stackup.html|the stackup chapter}} — the technology editor is where its layers, its stackup
and its design rules are written, and **File ▸ Save As** from it produces exactly the `.ctech` this
tab installs.

## Security &amp; Permissions {#security}

{{ui: settings-security}}

This tab answers one question: **what is circuitRF allowed to run, and what is it allowed to fetch?**
Everything with that shape is collected here rather than living in whichever tab it happened to arrive
in, so that a user auditing what this binary may do has one place to look.

Each control carries its explanation as a tooltip — hover it to read what it governs.

### External PDKs — Generated Artwork

A kit can ship **generator scripts** that draw the artwork for its parameterised cells, and circuitRF
asks before running any of them. The answer is remembered per kit, keyed by that kit's directory on
disk, so the prompt does not nag — and because a refusal is remembered too, there has to be a way back.

**Ask Again…** forgets every remembered answer, and the line beside it says how many are held. The next
workspace that uses a kit's generators asks about it again from scratch.

<div class="callout note">
<span class="label">Trust never travels with a workspace</span>
<p>These answers live in this installation's preferences and are deliberately absent from the
<code>.cws</code>. A workspace arriving from somebody else with its scripts pre-marked trusted would run
them on open with no prompt, which would defeat the question entirely. See
{{anchor: pdk-authoring.html|the kit-authoring chapter}} for what a generator script is.</p>
</div>

### External Device Workers

The **other** kind of program a kit can make circuitRF run. A kit may ship its own executable for
evaluating its device models, and circuitRF starts it the first time a design uses one of that kit's
parts.

**Allow kits to run their own device workers** is on by default — every kit installed before this
checkbox existed evaluates its devices through a worker, and shipping it off would have broken those
workspaces silently at the next Run. Turn it off and those parts cannot be simulated; the rest of the
design is unaffected, and a refusal is reported rather than swallowed. How a kit declares a worker, and
what happens when one refuses, is in {{anchor: pdk-integration.html#models|the kit-integration chapter}}.

If your administrator has fixed this setting for the machine, the checkbox is disabled and a line
underneath says so.

### Updates

| Control | What it does |
|---|---|
| **Automatic updates** | Downloads new versions in the background and installs them the next time the application is relaunched. |
| **Include beta releases** | Includes pre-release builds when looking for a new version. **On by default while circuitRF is in beta**, since that is where the releases are; untick it to be offered stable releases only. A sub-item of the box above, and disabled while it is off. Turning it off discards a staged beta; a staged stable version is left alone. |
| **Show release notes after an update** | Opens the release notes once, the first time a newly installed version is launched — never on a fresh installation, and never twice for the same version. If releases went out while the application was not launched, **the versions you skipped are listed underneath**, newest first, up to ten of them; a single update shows one set of notes as before. **Not** a sub-item of automatic updates, and deliberately not disabled with it: a version installed by hand is still a new version, and its notes are still worth reading. |

**Last checked** underneath is read from the updater's own state file and is never written here. It is
the first thing to look at when wondering whether the feature is working at all.

### Verilog-A Compiler

circuitRF loads **compiled** models (`.osdi`). Point a component at Verilog-A source (`.va`) instead and
circuitRF builds it once with the compiler named here, caching the result until the source changes.

**Leave the box blank** and the compiler on your `PATH` is used, which is what most machines want. The
row is here for the machine that has two compilers, or has one somewhere `PATH` does not reach; a named
compiler outranks `PATH`. **Browse…** picks one from disk, and **Test** runs it and reports what it says
it is, in place, so you find out now rather than at the first simulation.

<div class="callout note">
<span class="label">circuitRF ships no compiler and links to none</span>
<p>It starts the one you name as a separate process — the same arm's-length arrangement as building
circuitRF itself with a C compiler. That is why this row belongs on the Security &amp; Permissions tab
at all: it names a program circuitRF is permitted to <b>run</b>. Which compiler to install, what it does
with the source and where the built artefact is cached are all in
{{anchor: veriloga.html#compiler|the Verilog-A chapter}}.</p>
</div>

## Revision Control {#revision-control}

{{ui: settings-revision-control}}

circuitRF can keep a **history** of a workspace: the state of every file in it at points you can go back
to. This tab is where you say whether it does, and what it keeps.

<div class="callout note">
<span class="label">Nothing here works without git</span>
<p>circuitRF keeps a history by running <b>git</b> as a separate program. It bundles none and links to
none. If your machine has no usable git, everything on this tab below the <b>Git</b> row is greyed out,
a line says so, and <b>no history is being kept for any workspace</b> — whatever the switches appear to
say. The <b>Git</b> row itself stays available, because it is how you fix that: name a git, or install
one and press <b>Detect</b>. The rest of the tab comes to life as soon as Detect finds one.</p>
<p>Elsewhere in circuitRF the feature is simply absent on a machine with no git — no restore-point list,
no menu items, nothing. This tab is the one place it is shown and disabled instead, so that "circuitRF
is not keeping a history for me" is something you can find out rather than something you assume.</p>
</div>

### Git

Leave the box **blank** and the git on your `PATH` is used, which is what most machines want. The row is
here for the machine that has two of them, or has one somewhere `PATH` does not reach; a git named here
outranks `PATH`. **Browse…** picks one from disk.

**Detect** finds it and reports the path and the version it identifies itself as, in place. That is the
answer to *"it says it can't find git"* without anyone needing to ask you for a log. It is also the one
place circuitRF will tell you your git is **too old** — naming the version you have and the version it
needs. Everywhere else, a git below that floor is simply treated as though it were not installed.

### Commit Identity

Your name and email, recorded against every change circuitRF keeps.

They are **yours, not the workspace's**: they apply to every workspace you open on this machine.
circuitRF writes them into **no git configuration file at all** — not the workspace's, and not your own.
Storing them with a workspace would mean that on a shared drive, the next person to open it would have
their changes recorded under your name, and nobody would notice until they read a history and did not
believe it.

If you already use git and have set an identity, these boxes are filled in from it as a convenience.
circuitRF only ever *reads* that setting; it never changes it.

**Both are needed before circuitRF starts keeping a history.** The tab says so, rather than letting the
first attempt fail.

### What Is Kept

Two switches, answering two different questions.

| | |
|---|---|
| **Keep a history of my workspaces** | *Do I want this at all?* Your own preference, applying to every workspace. **On by default.** |
| **Keep a history of "*this workspace*"** | *Not for this one.* This workspace's own answer, which **overrides the setting above for it alone**. |

The second is stored **with the workspace**, which is why it travels: a copy of a workspace that was
switched off arrives switched off, whatever the person opening it prefers.

<div class="callout note">
<span class="label">Turning either of these off deletes nothing</span>
<p>Off means circuitRF <b>stops writing</b>. It does not remove anything it has already kept: every
restore point stays listed and can still be restored, and turning it back on carries on where it left
off. Nobody expects a checkbox to be irreversible, and this one is not.</p>
<p>If you genuinely want a history <i>gone</i>, that is deleting one plainly named <code>.git</code>
folder inside the workspace, with your file manager. circuitRF does not offer a button for it, on
purpose. <b>Doing it cannot harm your design</b> — a workspace is ordinary files in a folder, and the
history sits beside them rather than containing them. Delete it and the workspace opens exactly as it
did, with every file present and current.</p>
</div>

A workspace is armed at the first moment there is something to record, never simply because you opened
it. Looking at a colleague's workspace on a shared drive creates nothing in it.

### Restore Points

**Keep them for** *N* days is how long an automatic restore point survives. After that it is **thinned**:
it is no longer offered, and the state it held is no longer something you can return to. Changes you
recorded yourself are never thinned.

**Always keep at least** *N* restore points is the rule that makes the one above safe. The newest *N* are
kept **however old they are**. That matters more than it sounds: a computer's clock is not trustworthy —
a flat battery, a bad time sync, or a machine that disagrees with itself about time zones can make
everything look years old at once. The count floor means a wrong clock costs you nothing, which is why
the field will not go below its minimum.

**Take a restore point when a workspace is closed** is on by default. Closing is the one moment that
reliably happens in every session, and it is behind you rather than in front of you. A session in which
nothing was written records nothing.

**Take a restore point before an AI edit** is always on and cannot be switched off. It is the reason the
rest of this tab exists: an edit made on your behalf can span many files at once, and circuitRF records
where you were first. It is shown rather than hidden so you never have to wonder whether it is
happening.

### Disk Space

Most people never touch either of these. They are here because a history accumulates, and one day
somebody asks where the disk went. The two answers are not the same answer.

**Both buttons name the workspace they act on** — the one you have open. Neither opens a chooser, so
the name is in the button rather than in a dialog you would have to reach to find out. With no
workspace open they are greyed out and the line beneath says so.

**Compact above** *N* MB, and the **Compact** button beside it, store the same history in less space.
**Nothing is discarded** and nothing becomes unavailable. circuitRF does this on its own once a history
passes the size named here.

**Reclaim after** *N* days, and the **Reclaim Space in…** button beside it, are different, and this is
**the one destructive control in circuitRF's revision control**. When a restore point is thinned its contents are not actually
freed — circuitRF never destroys anything on its own, so a thinning you did not mean is recoverable.
Reclaiming is you saying: those are really gone.

<div class="callout warning">
<span class="label">What reclaiming destroys</span>
<p>It permanently frees the states behind restore points that were <b>already thinned</b>, more than the
number of days you name. <b>After it, nobody can bring them back</b> — not you, and not anybody you send
the workspace to.</p>
<p>Every restore point still listed, and every change you recorded yourself, <b>survives it unchanged</b>.
Your design files are not touched. It asks first, and tells you how many states it would destroy.
<b>Nothing ever reclaims on a schedule</b> — the number in the field does nothing on its own.</p>
</div>

## Color Theme {#color-theme}

{{ui: settings-color-theme}}

Every colour circuitRF paints with is a named **role** — schematic wire, layout metal, plot trace,
selection, grid — and a theme is a value for each role, in each variant. Editing a role repaints the
application immediately, so the picture you are judging is the real one.

| Control | What it does |
|---|---|
| **Color Theme** | The theme to edit. The list is every theme circuitRF can find; edit a role and the selection reads *Custom* until you save. |
| **Light / Dark** | Which variant you are editing. A theme holds **both**, so switching here changes the values under the sliders, not the theme. It opens on whichever variant the application is currently rendering. |
| **Save Theme…** | Writes the current values out as a named theme and makes it the active one. Saving over the shipped `Default` is not possible — it is saved as *Custom* instead, so the shipped palette is always there to go back to. |
| **Role list** | Every role, each with a swatch of its current value. Select one to edit it; **double-click** one to open the colour picker instead of using the sliders. |
| **R / G / B / A sliders** | The selected role's colour, 0–255 per channel. The box at the right of each slider takes a typed number. |
| **#** | The same colour as hex — `RRGGBB` or `RRGGBBAA`. |

The path under the editor is the role's own name, which is what a `.ccolor` file records.

<div class="callout note">
<span class="label">Where a theme is looked for</span>
<p>A <code>.ccolor</code> beside the workspace wins, then one in your per-user themes folder, then the
palette built into the application. A workspace can therefore carry its own house colours without
changing anything on your machine. Opening Settings from the macOS application menu when no workspace is
open simply omits the first of the three.</p>
<p><b>Which theme is active is yours, not the project's.</b> The choice is recorded in the workspace's
<code>.cwsuser</code> (see <a href="file-formats.html#cwsuser">Your own view of a workspace</a>), so a
workspace you receive from somebody else does not switch your theme — its <code>.ccolor</code> files
travel with it and are there in the list, but you pick one.</p>
</div>

## Wirebonds {#wirebonds}

{{ui: settings-wirebonds}}

These are **creation defaults**, per user. They describe how one shop's bonder is set up, so a `.wBond`
arriving from somebody else must not change what the next wire you draw looks like. Existing wires keep
whatever they were drawn with.

| Control | Default | What it does |
|---|---|---|
| **Points per wire** | 7 | How many points a new wire's profile is built from — the resolution of the loop, not a mesh setting. |
| **Wire diameter** | 1 mil | The wire gauge a new wire is created at. Shown in mil because that is the unit a bonder is specified in; stored in nanometres like every other wBond dimension. |
| **Wire material** | Gold | Gold, Aluminium, Copper or Silver. Gold is both the RF packaging norm and the metal the 3D kernel was validated against. |
| **Wire z-height** | 4 mil | Where **both** feet of a new wire land — a wire drawn in the layout view, and the wires a new wBond component is created with. |
| **Paste pitch** | 5 mil | How far a pasted wire steps in +y to land clear of what is already there. It never re-spaces the wires on the clipboard. |

<div class="callout note">
<span class="label">Zero and negative z are real values</span>
<p>Wire z-height is the one preference here where "absent" and "zero" are different things: a foot at
z = 0 lands on the reference plane and a negative one sits in a cavity below it, and both are geometry
somebody bonds. The <b>profile view's</b> own wire tool ignores this setting entirely — there you click
the height you want, which is the whole point of drawing in that view.</p>
</div>

Nudge steps stay at 1 mil and 5 mil and are not settings: they are bonder-process quantities, not
display preferences.

### Design Rules

**Wire clearance** (default 0.5 mil) is circuitRF's own built-in assembly rule: how close two wires may
pass, measured surface to surface between their outer edges. It is reported as an error by a design-rule
check **whether or not** a `.wasm` assembly rule file is referenced — a house's rule file is not what
makes overlapping metal invalid. Zero reports only wires that actually touch. A `.wasm`'s own spacing
rules are checked **as well as** this one, never instead of it.

Everything about the wires themselves — loop height, span, the array basis, the inductance the kernel
computes and the S-parameters it exports — is in {{anchor: wbond.html|the wBond chapter}}.

## 3D EM {#em3d}

{{ui: settings-3d-em}}

A 3D EM setup runs one of two separately installed solvers — **Palace** (FEM) or **openEMS** (FDTD) —
and Palace's problems are meshed by **Gmsh**. circuitRF includes none of the three. This tab has one row
per program, and each row says what circuitRF found: the version, whether it is one circuitRF has
validated, where the program is, and how it was found.

| Control | Default | What it does |
|---|---|---|
| **Path** (one per program) | blank | The program to run. **Blank means search**: the environment variable (`CIRCUITRF_PALACE`, `CIRCUITRF_GMSH`, `CIRCUITRF_OPENEMS`), then `PATH`, then the usual install folders (`~/.local/bin`, `/opt/homebrew/bin`, `/usr/local/bin`, and `~/opt/openEMS/bin` for openEMS), then Spack's install record, which is where a Palace built with Palace's own recipe is found. A path you name here always wins, and if it does not work circuitRF says so rather than using a different one it found somewhere else. |
| **MPI launcher** | blank | The `mpirun` that lets Palace use more than one core. **Blank means search**: `CIRCUITRF_MPIRUN`, then an `mpirun` beside Palace, then — for a Palace built with Spack — the MPI that build was linked against, then `PATH` and the usual install folders. The row shows which one a run would use. Without one, Palace still runs, on one core. |

**A version circuitRF has not validated is refused, not just flagged.** Palace's configuration changes
meaning between versions, and a setting read the wrong way gives an answer that looks plausible. The
row says which versions are validated. The same check runs at the start of every 3D run and in
`circuitrf explain`, so all three always agree.

The Palace row also carries its licence note: a default Palace build includes ParMETIS, which may be
used commercially for evaluation only. If you build Palace, you accept those terms.

How each program was installed for validation is in
{{anchor: em-setup.html#install-3d-solvers|Installing the 3D solvers by hand}}.

## The footer: Help, Revert, Cancel, Close {#footer}

**Help** sits at the leading edge and opens this page. Everything that acts on the dialog is grouped at
the trailing edge.

| Button | What it does |
|---|---|
| **Revert** | Puts the colour editor back to the theme that was active when the dialog opened, without closing it. It does **not** undo anything on the other tabs — those were already saved. |
| **Cancel** | The same restore, and closes. Again, colours only. |
| **Close** | Keeps the current colours and records the active theme as your preference. |

## Where the settings are stored {#where}

One `preferences.json`, in circuitRF's per-user state directory —
`%LOCALAPPDATA%\circuitRF` on Windows, `~/Library/Application Support/circuitRF` on macOS,
`~/.local/share/circuitRF` on Linux. Saved themes are `.ccolor` files in a `themes` folder beside it, and the technologies you add are
`.ctech` files in a `technologies` folder beside that.

<div class="callout note">
<span class="label">One file serves all three applications</span>
<p>circuitRF, harmonicaRF and wBond share that directory, so <b>External Device Workers</b>,
<b>Updates</b> and the <b>Verilog-A Compiler</b> are one setting each, not three — set in any of the
three applications, honoured by all of them. harmonicaRF has no workspace and therefore its own smaller
Settings dialog, which hosts those same three sections; the controls are literally the same controls,
not a second copy of them.</p>
</div>

An absent key means the default, so a machine with no `preferences.json` at all is a correctly
configured one. Nothing here is written until you change something.

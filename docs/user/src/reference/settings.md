---
title: Settings
slug: reference/settings.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Settings
lede: Every tab of the circuitRF Settings dialog, control by control — what each one changes, when it takes effect, and which of them are shared with harmonicaRF and wBond.
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#opening">Opening it, and when a change takes effect</a></li>
<li><a href="#general">General</a></li>
<li><a href="#security">Security &amp; Permissions</a></li>
<li><a href="#color-theme">Color Theme</a></li>
<li><a href="#wirebonds">Wirebonds</a></li>
<li><a href="#footer">The footer: Help, Revert, Cancel, Close</a></li>
<li><a href="#where">Where the settings are stored</a></li>
</ol>
</nav>

Settings are **per user, not per workspace**. Nothing on this dialog travels inside a `.cws`, and a
workspace someone sends you cannot change how your copy of circuitRF behaves — that is deliberate, and
it is the reason several of these controls exist here rather than in the document that uses them.

## Opening it, and when a change takes effect {#opening}

**File ▸ Settings…**, or `Ctrl` `,`. On macOS it is in the application menu instead — **circuitRF ▸
Settings…**, `⌘` `,` — where that platform's users look for it.

The dialog is **not modal**. It stays open while you carry on working, so you can change a setting and
watch what it does without closing anything.

<div class="callout note">
<span class="label">Every tab but Color Theme writes immediately</span>
<p>A combo box, a checkbox or a number on the <b>General</b>, <b>Security &amp; Permissions</b> and
<b>Wirebonds</b> tabs is saved the moment you change it. There is no "apply" step, and
<b>Cancel does not undo it</b> — Cancel and Revert act on the colour editor only, which is the one tab
that edits a live document-like thing you might want to abandon.</p>
</div>

Most settings apply to the next thing you do: a launch setting takes effect at the next launch, a
wirebond default applies to the next wire you draw. The two that are live are the **message timestamp
format**, which re-renders the Messages panel as you change it, and every **colour role**, which
repaints the application as you drag the slider.

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

The rules themselves, and the panel that lists the violations, are in
{{anchor: layout-editor.html|the layout editor chapter}}.

### Messages

**Timestamps** — how the Messages panel stamps each line: *Time*, *Date + Time*, or *Hidden*. This one
is live; the panel re-renders as you change it.

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
| **Include beta releases** | Includes pre-release builds when looking for a new version. A sub-item of the box above, and disabled while it is off. Turning it off discards a staged beta; a staged stable version is left alone. |
| **Show release notes after an update** | Opens the release notes once, the first time a newly installed version is launched — never on a fresh installation, and never twice for the same version. **Not** a sub-item of automatic updates, and deliberately not disabled with it: a version installed by hand is still a new version, and its notes are still worth reading. |

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

## The footer: Help, Revert, Cancel, Close {#footer}

**Help** sits at the leading edge and opens this page. Everything that acts on the dialog is grouped at
the trailing edge.

| Button | What it does |
|---|---|
| **Revert** | Puts the colour editor back to the theme that was active when the dialog opened, without closing it. It does **not** undo anything on the other three tabs — those were already saved. |
| **Cancel** | The same restore, and closes. Again, colours only. |
| **Close** | Keeps the current colours and records the active theme as your preference. |

## Where the settings are stored {#where}

One `preferences.json`, in circuitRF's per-user state directory —
`%LOCALAPPDATA%\circuitRF` on Windows, `~/Library/Application Support/circuitRF` on macOS,
`~/.local/share/circuitRF` on Linux. Saved themes are `.ccolor` files in a `themes` folder beside it.

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

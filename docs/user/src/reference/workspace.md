---
title: The Workspace
slug: reference/workspace.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > The Workspace
lede: The window everything else happens in — documents in the middle, tool panels around them, and a folder on disk behind it all.
---

Every other chapter in this guide describes something you do *inside* one window. This chapter is
about the window: what the panels are, how to move them, and what a **workspace** actually is once
you close the application.

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#regions">The regions of the window</a></li>
<li><a href="#workspace">What a workspace is</a></li>
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

The `.cws` records **configuration, never content** — the panel arrangement, which documents were
open, referenced libraries, bookmarked "Known Files", the default technology and the colour theme. A
cell is referenced, not embedded, so the same cell or library can belong to several workspaces at
once. The full on-disk layout, file type by file type, is in
<a href="file-formats.html">File Formats</a>.

Two consequences worth knowing early:

- **A workspace is version-controllable.** It is text files in folders; nothing is hidden in a binary
  project database.
- **Moving a workspace is moving a folder.** To send one to somebody else, use
  **File ▸ Archive Workspace…**, which additionally pulls in the things it references from outside
  the folder — libraries, technologies, optionally kits and results — and repoints the references at
  the copies, so the archive opens on a machine that has none of them.

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
undoes exactly as it did while docked — and where you left it is recorded in the `.cws`.

## The tool panels {#panels}

Eight panels can be on screen; **View ▸ Panels** lists all of them, and the three used most often
have toolbar buttons of their own.

<table class="param-table">
<thead><tr><th>Panel</th><th>Shows</th></tr></thead>
<tbody>
<tr><td class="nowrap"><b>Project</b></td><td>The workspace's cells and their views, its technologies and its libraries. Double-click to open; right-click for the actions on a cell.</td></tr>
<tr><td class="nowrap"><b>Library</b></td><td>Every component you can place, by category and searchable. Click a tile to arm it, then click on the canvas to drop it. See <a href="components.html">Components</a>.</td></tr>
<tr><td class="nowrap"><b>Properties</b></td><td>The parameters of the current selection, editable in place. What it shows depends on what is selected — a component, a wire, a layout shape, a bond-wire array.</td></tr>
<tr><td class="nowrap"><b>Analyses</b></td><td>The analyses the open test bench will run, and the Run button that runs them. See <a href="simulations.html">Simulations</a>.</td></tr>
<tr><td class="nowrap"><b>Messages</b></td><td>What the application did, with warnings and errors. Each message links back to the file or object it is about.</td></tr>
<tr><td class="nowrap"><b>DRC</b></td><td>Design-rule violations from the last check, each one selectable in the layout it came from.</td></tr>
<tr><td class="nowrap"><b>Wire Profile</b></td><td>Bond wires seen from the side — loop height and span. See <a href="wbond.html">wBond</a>.</td></tr>
<tr><td class="nowrap"><b>Array Inductance</b></td><td>The inductance computed for the selected bond-wire array.</td></tr>
</tbody>
</table>

The toolbar's three panel buttons are **toggles**, not "open it" buttons: press once to bring the
panel back where you last had it in this workspace, press again to close it. The menu items under
**View ▸ Panels** only ever show a panel — a menu item named after a panel must not close it.

## Moving, hiding and resetting the layout {#docking}

Tool panels dock the same way documents do. Drag a panel by its tab: onto another panel to tab them
together, against an edge to give it a column or a row of its own, or out of the window to float it.
Drag a splitter to change the proportions. All of it is saved into the `.cws`, so a workspace
reopens arranged the way you left it.

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
documents the incoming `.cws` had open are reopened. **File ▸ Open Recent** is the same operation
against a list of the last few.

You do not need a workspace at all to get started. **File ▸ New Schematic** opens a scratch sheet
that belongs to no cell and no folder; wire it up and simulate it immediately. If it turns out to be
worth keeping, **File ▸ Save Schematic As…** puts it into a workspace as a cell. With no workspace
open the Project panel lists the workspaces you had open recently instead of a tree, so getting back
to one is a single click.

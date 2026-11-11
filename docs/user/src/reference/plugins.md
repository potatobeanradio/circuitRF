---
title: Using Plugins
slug: reference/plugins.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Using Plugins
lede: Tools other people write that circuitRF draws - opening one, what allowing it means, installing, updating and removing them, and running one from the command line.
keywords: plugin, plugins, Tools Plugins, Settings Plugins, install plugin, link folder, vetted list, permission, allow, deny, update, roll back, uninstall, CRF_NO_PLUGINS, no-plugins, circuitRF plugin, plugin run
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What a plugin is</a></li>
<li><a href="#open">Opening one: Tools ▸ Plugins</a></li>
<li><a href="#permission">Allowing a plugin, and what that means</a></li>
<li><a href="#place">Placing what it makes</a></li>
<li><a href="#settings">Settings ▸ Plugins: install, update, roll back, remove</a></li>
<li><a href="#python">Python plugins</a></li>
<li><a href="#list">The vetted list, and what "vetted" covers</a></li>
<li><a href="#managed">Turning plugins off on a managed installation</a></li>
<li><a href="#cli">Plugins on the command line and through MCP</a></li>
</ol>
</nav>

## What a plugin is {#what}

A **plugin** is a tool somebody else writes and maintains — a filter synthesiser, an impedance calculator, a coupler
generator, an importer — that you install into circuitRF without rebuilding it. It is a **separate program**: circuitRF
starts it, sends it your inputs, and draws what it returns with its own plots, Smith charts, layout and schematic views,
so a plugin's panel looks and behaves like the rest of circuitRF. When you are happy with the result, circuitRF places
what the plugin made as a cell in your workspace.

A plugin is one of three kinds:

- **A panel** — a tool window circuitRF builds from the plugin's description: its inputs on the left, its views in
  tabs, recalculated as you type. Markers on its plots and grips on its layout can be dragged, and change the inputs.
- **An action** — a dialog of inputs, then one run that places a cell.
- **A window** — the plugin's own program window. circuitRF draws none of it. Its tooltip says "Opens its own window."

Writing one is covered in [Writing a Plugin](writing-plugins.html).

## Opening one: Tools ▸ Plugins {#open}

**Tools ▸ Plugins** lists every installed and linked plugin, grouped by the submenu its author chose. Hover one for its
summary. A plugin dimmed in the menu cannot run here, and its tooltip says why: it has no build for this computer, it
needs a newer circuitRF, or plugins are turned off. **Manage Plugins…** at the foot of the menu opens Settings ▸ Plugins.

A panel opens as a tool window, as Match Designer does, and remembers its size and layout per plugin. While the plugin
works the panel shows a busy bar; **Stop** asks it to stop, and restarts it if it does not. If the plugin fails, the
panel says so, keeps the last views, and shows the plugin's own log under its messages.

A window plugin runs one instance at a time: choosing it again while it is open says it is already running (circuitRF
cannot bring another program's window forward). When you quit circuitRF, its plugin windows are asked to close.

## Allowing a plugin, and what that means {#permission}

The first time you open a plugin, circuitRF asks:

> **Allow *name* to run?**
> A plugin runs with your permissions and can read and write your files. Allow it only if you trust where it came from.

with its version, author, where it came from, whether it is on the vetted list, and exactly what will run (the program,
or the Python interpreter and script). **Allow** or **Don't Allow**.

<div class="callout warn">
<span class="label">A plugin runs as you</span>
<p>Running in a separate process protects circuitRF from a plugin that crashes; it does <b>not</b> protect your files
from a plugin that misbehaves. A plugin is a program, and it can do whatever a program you start yourself can do. The
rules circuitRF enforces — it places only what the plugin staged, it validates every file, it takes a restore point
first — keep your <em>workspace</em> safe from a buggy plugin, not your computer from a hostile one. Allow what you would
run yourself.</p>
</div>

- Your answer is remembered **on this computer, per user** — never in a workspace, so opening someone else's workspace
  never runs a plugin you have not allowed.
- An update to a new version asks again, unless both versions are on the vetted list.
- **Permission is given only in circuitRF's window** — this prompt, or Settings ▸ Plugins. The command line and an
  AI agent through MCP can take permission away, never give it: anything that can type a command can be an agent, and
  running someone else's program with your permissions is your decision.

## Placing what it makes {#place}

A plugin never writes into your workspace. It writes into a staging folder circuitRF gives it, and **Place in
workspace** (a button the plugin provides) has circuitRF:

1. check every file the plugin staged with the reader that file kind already has, and refuse with the reasons if any
   fails — nothing is placed half-way;
2. ask for the cell's name, with **Replace** or **New name** when that name is taken;
3. take a [restore point](restore-points.html) named after the plugin and its version;
4. create the cell and refresh the Project Tree.

So every placement can be undone from [History](history.html). A plugin may place a layout with its ports as port labels
and an EM setup naming them, so a placed cell can open ready for an EM run.

## Settings ▸ Plugins: install, update, roll back, remove {#settings}

**Settings ▸ Plugins** (also **Tools ▸ Plugins ▸ Manage Plugins…**) has three parts.

**At the top**, **Allow plugins to run** turns every plugin on or off for this user, and a box to **paste a GitHub or
GitLab link**: the repository's page, a clone address, or a link to a release or tag. **Install…** asks which version
(from the repository's releases, or its tags) and installs it. Below it:

| Button | Does |
|---|---|
| **Install from Folder…** | Copies a plugin folder in |
| **Link Folder…** | Runs a folder in place, for a plugin you are writing: circuitRF re-reads it every time the plugin starts, so an edit takes effect the next time you open it |
| **Install from Address…** | The same as pasting a link, with the address typed in the dialog |

**Installed** lists each installed or linked plugin: its version, where it came from, whether it is permitted (**Ask**,
**Allowed** or **Denied** — change it here), and whether an update is available. For the selected one:

| Button | Does |
|---|---|
| **Update** | Installs the newer version from where this one came from. It asks again before it runs, unless both versions are on the list |
| **Roll Back** | Goes back to the version the last update replaced. The old version is kept until the new one has started once |
| **Uninstall** | Removes the plugin, its permission and what circuitRF remembered for it. A linked folder is unlinked and left where it is |
| **Open Folder** | Shows the plugin's folder |

**Available** lists the [vetted list](#list): **Install** downloads the reviewed release and checks it against the
list before unpacking anything; **Refresh List** fetches the newest list.

Every install is all-or-nothing: an install that fails at any step — a bad archive, a manifest that is not what was
asked for, a cancelled download — leaves nothing behind and the previous version still active. Nothing reaches the
network unless you press Install, Update or Refresh List. Plugins live per user, beside your preferences, one folder per
plugin and version.

## Python plugins {#python}

A plugin written in Python runs on **the same Python** circuitRF uses for kit scripts: the first `python3` on your
`PATH` (on Windows, `python.exe` on the `PATH`, then the `py` launcher, then the standard installation folders) — so an
activated virtual environment is used. circuitRF never installs another.

- A plugin that needs **no other packages** runs on that Python as it is. Installing it makes nothing and downloads
  nothing.
- A plugin that ships a `requirements.txt` has them installed at install time into **one shared plugin environment**,
  so two plugins that need the same package share one copy. circuitRF first checks that the new plugin's requirements
  fit together with every other plugin's in that environment, and changes it only if they do.
- Only when they conflict does the plugin get an environment of its own; the install log says which requirement could
  not be shared.

Installing requirements is the one step that reaches the network for a Python plugin, and the install dialog says so.
Kits keep their own environments: installing a plugin never changes a kit's packages.

## The vetted list, and what "vetted" covers {#list}

The **Available** list comes from `plugins/index.json` in the circuitRF repository, shipped with each release and
refreshed when you press **Refresh List** (a refreshed list is used only if its signature verifies).

**What is vetted is one version, not the author.** A version goes on the list after a review of that release: its
source matches its tag, it writes only into its staging folder, it reaches the network only when a parameter or button
says so, it starts no background process, and its licence is stated. A later version is reviewed again before it is
listed. Being on the list is not a promise that a plugin is correct or that it suits your work, and a plugin installed
from a link or a folder has had no review at all — the permission prompt says which.

## Turning plugins off on a managed installation {#managed}

Plugins can be turned off for every user of an installation, before anyone can turn them on:

- an environment variable `CRF_NO_PLUGINS=1`, or
- a file named `no-plugins` beside the installed application.

Either beats every user's settings and every permission, the same way the switches for external device workers do.
Settings ▸ Plugins then says which one is in force.

## Plugins on the command line and through MCP {#cli}

Every plugin with a panel — and every window plugin that declares a headless form — runs headless:

<pre><code class="cmd"><span class="prompt">$ </span>circuitRF plugin list
<span class="prompt">$ </span>circuitRF plugin describe example.single-stub
<span class="prompt">$ </span>circuitRF plugin run example.single-stub --set f0="5.8 GHz" --set rl="75 Ohm" --workspace ws -o out
<span class="prompt">$ </span>circuitRF plugin run example.single-stub --set f0="5.8 GHz" --place ws</code></pre>

`run` writes every view as a file (a plot as its data and a `.cdd`, a layout as a `.clay`, a table as CSV, the readouts
as `readouts.json`); `--place` places the cell exactly as the panel's button does. `install`, `uninstall`, `update`,
`rollback` and `deny` manage plugins; there is no `allow`. The [CLI reference](cli.html#plugin) has every option, and
the [MCP server](mcp-server.html#tools)'s `plugin` tool offers `list`, `describe`, `run`, `install` and `uninstall` to an
agent. A plugin you have not allowed is refused with a message saying that only you can allow it, in circuitRF.

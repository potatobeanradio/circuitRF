---
title: The MCP Server
slug: reference/mcp-server.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > The MCP Server
lede: circuitRF as a Model Context Protocol server — installing it, connecting a client to it, the tools it offers and what it refuses.
keywords: MCP, MCP server, Model Context Protocol, serve, print-config, mcpServers, client, AI agent, assistant, install, unattended install, JSON-RPC, stdio, tools, resources, root, attachImage, async, job, Claude Code
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What it is</a></li>
<li><a href="#connect">Connecting a client</a></li>
<li><a href="#tools">The tools</a></li>
<li><a href="#limits">What it will not do</a></li>
<li><a href="#running">Progress, updates and stderr</a></li>
<li><a href="#install">Installing for an agent</a></li>
</ol>
</nav>

## What it is {#what}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf serve --root &lt;dir&gt; [--kits &lt;dir&gt;] [--print-config]</code></pre>

**This is circuitRF as an MCP server.** It speaks the **Model Context Protocol** over its standard
input and output — the stdio transport, newline-delimited JSON-RPC 2.0, with `initialize`,
`tools/list` and `tools/call` — so any MCP client can discover what circuitRF can do and ask it to
do it. Protocol versions `2025-06-18`, `2025-03-26` and `2024-11-05` are accepted.

In practice that means an assistant, a design agent, a CI job or an automation harness can run a
simulation, check a design, create a workspace or import a part without a human at a terminal. It is
started by that program, not by you, and it ends when that program disconnects.

What to tell an agent once it is connected, which reference topics it should read, and the design
flow that works are in [Designing with an AI agent](ai-agents.html). Every tool is also a
[command-line verb](cli.html).

## Connecting a client {#connect}

<div class="callout note">
<span class="label">Point an MCP client at it</span>
<p>A client is configured with a command and its arguments. The command is the installed circuitRF
executable (<a href="cli.html#invoking">where it is</a>), the arguments are <code>serve --root &lt;dir&gt;</code>,
and <code>--root</code> is the only directory tree the server will read or write, and a path escaping
it is refused rather than clamped (see <a href="#limits">What it will not do</a>). Nothing else about the
host matters, because the transport is the process's own stdin and stdout.</p>
</div>

Most clients take the same shape of configuration — a server name, a command and its arguments:

```json
{
  "mcpServers": {
    "circuitrf": {
      "command": "/Applications/circuitRF.app/Contents/MacOS/circuitRF",
      "args": ["serve", "--root", "/Users/you/designs"]
    }
  }
}
```

Use the full path to the executable rather than relying on the client's `PATH`, which is often not
your shell's. On Windows it is `%LOCALAPPDATA%\Programs\circuitRF\circuitRF.exe` or
`C:\Program Files\circuitRF\circuitRF.exe`; on Linux, `/usr/bin/circuitrf` or
`~/.local/bin/circuitrf`. With Claude Code it is one line:

<pre><code class="cmd"><span class="prompt">$ </span>claude mcp add circuitrf -- /Applications/circuitRF.app/Contents/MacOS/circuitRF serve --root ~/designs</code></pre>

**Or let circuitRF write the configuration for you.** `--print-config` starts nothing: it prints the
`mcpServers` JSON for the executable you ran it with, with the root (and any `--kits` folders) made
absolute, and exits. The JSON goes to stdout, so it can be redirected into a file; the matching
`claude mcp add` line goes to stderr, ready to paste.

<pre><code class="cmd"><span class="prompt">$ </span>/Applications/circuitRF.app/Contents/MacOS/circuitRF serve --root ~/designs --print-config</code></pre>

The root is checked before anything is printed, so a configuration naming a folder that does not
exist is refused rather than written.

**Choose the root deliberately.** It is everything the server can read and write, with your own
authority. Point it at the folder your designs live in, such as `~/designs`, not at your home
folder or a whole drive: an agent working inside `~/designs` needs nothing outside it, and a root of
`~` lets every tool read and write anywhere in your home folder.

**From a source build**, the command is `dotnet` and the first argument is the built
`CircuitRF.Cli.dll`. Build it once, then point the client at the build output rather than at
`dotnet run`. `dotnet run` builds before every start, so the server is slow to come up and a client
may give up waiting for it:

<pre><code class="cmd"><span class="prompt">$ </span>dotnet build src/Cli -c Release
<span class="prompt">$ </span>dotnet src/Cli/bin/Release/net10.0/CircuitRF.Cli.dll serve --root ~/designs --print-config</code></pre>

Rebuild after pulling changes; the client keeps running whatever was built last.

**Most clients load a server when a session starts**, so one registered mid-session appears in the
next one. Nothing is lost meanwhile: every tool is a verb, and the same verbs answer from a shell with
the same documents.

## The tools {#tools}

**Twenty-one tools, and all but two are a verb you already have:**

| Tool | Runs |
|---|---|
| `run` | `sparam`, `dc`, `hb`, `env`, `lp`, `lpp`, `em`, `opt` or a `yield` run (Monte Carlo, yield, corners, centering, DOE, sensitivity), chosen by `analysis` |
| `check` | `check` |
| `explain` | `explain`, including `--cells`, `--layers`, `--extents` and `--footprints` |
| `create` | `new workspace` or `new cell` |
| `import` | `import part` — a component, as a cell; `import schematic` (`what=schematic`) — a schematic design, as a cell |
| `convert` | `convert` — artwork between formats, in either direction, so exporting a layout is a `convert` |
| `render` | `render` — one tool over every document kind, as the verb is |
| `read` | `read` |
| `netlist` | `netlist` — the extraction a schematic runs as |
| `plot` | `plot` — one picture from one result file. It takes `attachImage` too |
| `find` | `find` — the workspaces, cells, views and analyses under a directory |
| `recognize` | [`recognize`](cli.html#recognize) — a board's artwork, or a picture, read as a circuit |
| `lvs` | [`lvs`](cli.html#lvs) — does a cell's artwork implement its schematic |
| `impedance` | [`impedance`](cli.html#impedance) — every trace on a layout against a target Z0, and its return path |
| `review` | [`review`](cli.html#review) — the Layout Review's checks over a board |
| `model` | [`model`](cli.html#model) — the Model Composer's nouns, chosen by `noun` |
| `history` | [`history checkpoint`, `list` or `restore`](history.html) — the correction nouns (`rename`, `retitle`, `correct`, `review`) are on the verb but not on this server |
| `solver` | [`solver list`](cli.html#solver) — which 3D solvers are installed and what each build can do |
| `reference` | `reference` |
| `job` | No verb: the status, cancellation and result of a run started with `async=true` |
| `batch` | No verb: it holds a restore-point batch open across several calls, which a process that exits after one command cannot |

**Every tool returns exactly the document `--json` writes**, byte for byte, because the server calls
the verb rather than re-implementing it. Nothing is reachable through the server that is not
reachable from your own shell, and nothing is reachable from your shell that the server cannot do.

**`render` and `plot` can hand the picture back, not just its path.** Pass `attachImage: true` and the result
carries the rendered file itself — a `.png` or `.svg` as image content, a `.pdf` as an embedded
resource with its own type. It is **off by default**, because an image is expensive in a way a JSON
document is not, and a client that only wanted the path should not pay for one.

<div class="callout note">
<span class="label">Over 4 MB it hands back the path and says what to narrow</span>
<p>The file is still written and still named in <code>outputs</code>; only the attachment is withheld,
and the answer says how large it came to and which argument would bring it under —
<code>--detail screen</code>, <code>--layers</code>, <code>--window</code>, a smaller
<code>--size</code>, or <code>.png</code> instead of a vector format. It is never truncated and never
dropped in silence: half a PNG is not a smaller PNG, and a request that comes back empty with no
explanation just gets sent again.</p>
<p>The number is the <a href="cli.html#render-detail">measured one</a>. A whole six-layer board is 1.4 MB as a PNG and
23.5 MB as an undecimated SVG, so the cap admits every raster this verb plausibly produces and refuses
exactly the case where you should have narrowed the render.</p>
</div>

**The attached bytes are the bytes on disk.** The server attaches the file the verb wrote; it never
draws a second time, and it never converts one format into another to make it attachable — that would
be the adapter making a rendering decision, which is the one thing it does not do.

## What it will not do {#limits}

<ul>
<li><b><code>--root</code> is required</b>, and every path a client names resolves under it. A path
that escapes — through <code>../</code>, through an absolute path, or through a symbolic link — is
<b>refused, naming the root</b>. It is never quietly clamped to something inside.</li>
<li><b>Nothing deletes, and nothing overwrites an existing workspace.</b> There is no person at the
other end to confirm with, so the answer is no. A client that wants a file gone deletes it itself.
<code>create</code> does make a missing <i>parent</i> directory, because that overwrites nothing and
a client with no file tools of its own had nowhere to go from the refusal.</li>
<li><b>No tool writes a file of the client's own text.</b> circuitRF is driven by writing its
documents, and the client supplies that half itself — what these tools write is what they
<i>produce</i>: a created document, a result, a netlist, a picture. A general write tool would put an
unbounded filesystem write behind the root, and it is deliberately not offered.</li>
<li><b>No shell, and no program a client names.</b> Device workers and PCell generators still run as
they always did; nothing new becomes launchable because something asked.</li>
</ul>

## Progress, updates and stderr {#running}

**The reference surface is also published as protocol *resources*** — one per topic, at
`circuitrf://reference/<topic>`, each advertising its size so a client can decide what to spend. That
is the cheaper channel: a resource costs a URI and a title until something reads it, where a tool
description is carried for the whole session whether or not it is used. The `reference` tool exists
alongside it because not every client shows resources to the model at all, and both return the same
bytes.

**A long run reports progress and can be cancelled** — a client that asks for progress is sent it as
the run moves, and a cancellation stops the run at a work boundary and returns exit code 130 having
written nothing.

**A server outlives an update, and says so when it cannot.** A `serve` that is still running when
the application updates itself is running out of an installation that has changed underneath it —
and a running program cannot load code it had not already loaded from files that have since been
replaced. So before each call the server checks that its own executable is still the one it started
from. If not, that call is answered with a JSON-RPC error, code `-32001`, saying so, and the server
exits. Start it again with the same command and it runs the updated version.

`serve` is the one verb whose stdout is not the result: it carries the protocol, so everything else —
progress, notes, warnings, device-worker logs — goes to stderr, where the program that started it
picks it up. For that reason it takes no `--json` of its own; every call through it already returns
one.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf serve --root ~/designs 2&gt; serve.log</code></pre>

## Installing for an agent {#install}

An agent — or any unattended script — can install circuitRF and start driving it with no person at
the keyboard. Five steps:

**1. Find the release and pick the asset.** Asset names carry the version, and every circuitRF
release so far is a *pre*release, which GitHub's "latest release" link and a tagless
`gh release download` both skip. **There is no fixed download URL**, so look the newest tag up
first:

<pre><code class="cmd"><span class="prompt">$ </span>TAG=$(gh release list --repo potatobeanradio/circuitRF --limit 1 --json tagName -q '.[0].tagName')
<span class="prompt">$ </span>gh release download "$TAG" --repo potatobeanradio/circuitRF --pattern 'circuitRF-*-arm64.dmg'</code></pre>

Without `gh`, the same list is `https://api.github.com/repos/potatobeanradio/circuitRF/releases`,
newest first. A release that carries `update-manifest.json` lists every asset in it with its name,
download URL, size and SHA-256, which is the one file to read to choose and to verify a download.

| Platform | Asset |
|---|---|
| Windows | `circuitRF-<version>-win-x64-user.msi` — per-user, no administrator (also `arm64`, `x86`) |
| macOS | `circuitRF-<version>-arm64.dmg` (Apple Silicon) or `circuitRF-<version>-x64.dmg` (Intel) |
| Linux | `circuitRF-<version>-linux-x64.tar.gz` or `…-linux-arm64.tar.gz` |

**2. Install it without a prompt.**

<pre><code class="cmd"><span class="prompt">&gt; </span>msiexec /i circuitRF-&lt;version&gt;-win-x64-user.msi /qn</code></pre>

<pre><code class="cmd"><span class="prompt">$ </span>hdiutil attach -nobrowse -mountpoint /tmp/circuitrf-dmg circuitRF-&lt;version&gt;-arm64.dmg
<span class="prompt">$ </span>cp -R /tmp/circuitrf-dmg/circuitRF.app /Applications/
<span class="prompt">$ </span>hdiutil detach /tmp/circuitrf-dmg</code></pre>

<pre><code class="cmd"><span class="prompt">$ </span>tar -xzf circuitRF-&lt;version&gt;-linux-x64.tar.gz
<span class="prompt">$ </span>./circuitRF-&lt;version&gt;/install.sh</code></pre>

The Windows install adds circuitRF to the user's `PATH`, but only processes started afterwards see
it — so in the session that installed it, use the full path,
`%LOCALAPPDATA%\Programs\circuitRF\circuitRF.exe`. The Linux one links `~/.local/bin/circuitrf`.

**3. Check that it answers:**

<pre><code class="cmd"><span class="prompt">$ </span>/Applications/circuitRF.app/Contents/MacOS/circuitRF --version</code></pre>

It prints the release's version — the tag from step 1 — and exits `0`, opening no window. Anything else means the path is wrong.

**4. Register the server** with the client: the command is that same executable, the arguments are
`serve --root <dir>` ([above](#connect)). `serve --root <dir> --print-config` prints exactly that
configuration, with the executable's full path filled in.

**5. Keep working in the meantime.** Most clients load a newly registered server only when their
next session starts, so the tools will not appear in the session that did the install. Nothing is
lost: every MCP tool is a verb returning the same JSON document, so the same work can be driven from
the shell right away — `circuitrf check <path> --json` returns byte for byte what the `check` tool
would.

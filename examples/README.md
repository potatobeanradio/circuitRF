# Example workspaces

These are **real circuitRF workspaces**, not templates and not an archive to unpack. Open any of
them here, in this folder, and edit it — that is the point of them being files.

They also ship with the application. `src/Ui/CircuitRF.Ui.csproj` copies this whole tree into the
build output, and **File ▸ New ▸ Examples** in the running application copies one to a folder the user
picks and opens it there. The user's copy is theirs; nothing writes back into this one.

## Adding an example

1. Make the workspace in here, as a folder with a `.cws` in it.
2. Add a row to `examples.json` — the folder name, the title the menu shows, and one sentence.
   The order of that file is the order of the menu. A folder that is not in the index is offered
   by nothing; an index row whose folder is missing is skipped rather than shown as a menu item
   that fails when pressed.
3. **Put every design in a cell**, the way File ▸ New Cell makes one: a folder with a `.ccell`, and
   its schematic, symbol, layout and 3D view in `schematic/`, `symbol/`, `layout/` and `3d/`, each
   named after the cell. A part placed into another view (a board placed into a 3D assembly, a die
   into a package) is a cell of its own. Analyses live on a **test bench** cell's schematic — never
   in a bare `.cnl`, which the user cannot open as a drawing. An example is read as the way to
   build a design, so a document in a plain folder teaches the wrong thing.
4. Write a `README.md` beside the `.cws` saying what the example teaches. **It is what the user
   sees first**: a workspace whose open restores no documents of its own opens on its README,
   read-only, instead of the Welcome tab. Headings, bold, italics, bullets, fenced code blocks
   and two-column tables all render, and so does a picture on a line of its own
   (`![what it shows](pictures/name.png)`, relative to the README, PNG, JPEG, GIF, BMP or WebP);
   anything else degrades to plain text (`src/Ui/Markdown/`). A picture inside a sentence, or one
   that does not resolve, shows its alt text, so write the alt text as a sentence.
5. Add its abstract to the user documentation's **Example Workspaces** page,
   `docs/user/src/examples/index.md`: a `###` heading with the menu title and an `{#ex-<folder>}` anchor, 100–250
   words on what it builds and shows, and a `*Folder:*` line naming the folder. Each heading is its own search result,
   which is how a user finds an example by subject. DocGen also renders this README, with its pictures, as
   `docs/user/examples/<folder>/index.html`, which the abstract links to; regenerate with DocGen.

`tests/Ui.Tests/Examples/ExampleWorkspacesTests.cs` holds the rest shut: the index and the disk
agree both ways, every example has its abstract on the documentation's Example Workspaces page, every design
document is a view of a cell, every schematic still extracts with
its device in it, nothing names anybody's home directory, and the tree reaches the build output.

## What is deliberately not here

**Results.** An example ships its design and its EM setup; the user runs them. The antenna's own
sweep is 52 MB, and a committed result is a claim about an engine version that will stop being
true. `examples/*/results/` is git-ignored at the repo root and excluded from the packaged build.

**Anything a user could not have drawn.** These are teaching documents, so every one of them is
authored out of the ordinary component library and the ordinary analyses.

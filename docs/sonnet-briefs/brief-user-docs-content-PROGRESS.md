# brief-user-docs-content.md — closed out (2026-08-20)

**All eight items of the STILL-TO-DO list are done.** The completion report is in the chat log of the
session that finished them; the findings that outlive it are in **`src/Ui/RESOLVED.md`**, section
*"User docs, content build-out — the tool-chapter half"*.

Gate, green as of closure:

```
dotnet run --project tools/DocGen -- --out docs/user   # 187 files, 18.1 MB, 11.4 s, zero lint failures
dotnet test tests/Ui.Tests --no-build                  # 8,314 passed
python3 tools/pcell-python/verify.py                   # all checks passed
```

Plus two checks run by hand at closure and worth repeating after any large doc change:

- **No bitmap references anywhere in `docs/user/`** (`grep -rniE '\.(png|jpe?g|gif)'` over the
  emitted HTML/CSS returns nothing).
- **No broken internal links or anchors** — a script that resolves every `href` in the emitted HTML
  against the target file's own `id` set reports 0. The generator's own cross-link check does *not*
  validate in-page anchors, and it missed a real one (`components.html#fet-family`, which does not
  exist; the anchor is `#fets`). **If in-page anchors matter, check them separately.**

## What is deliberately NOT done

- **The layout chapter's opening figure is the layout editor, not the whole workspace shell.**
  §4.1 asks for a full `WorkspaceWindow` capture with the project tree and panels around an open
  layout document. `WorkspaceViewModel` builds a dock factory and a layout on construction and no
  test in the repo constructs one, so it is not known to be headless-safe; the figure that ships
  (`{{ui: layout-editor}}`) has the real artwork, the technology and a drawn primitive in it, which
  is the content the box is about, inside a synthetic window frame.
- **No figure of a menu bar.** A `MenuItem`'s submenu does not render under headless capture (a
  `ContextMenu` does). The harmonicaRF menus are documented as a table. See `RESOLVED.md`.

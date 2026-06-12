# circuitRF — Bitmap drop rejected from Finder: INSTRUMENT the DataTransfer first (Claude Code / Sonnet)

A macOS Finder PNG drop onto the schematic is rejected (snap-back animation); Palette drops still work. The
bitmap file-drop handler exists but `TryExtractImagePath` returns null for a real Finder file, so
`OnImageFileDragOver` sets `DragDropEffects.None` → the OS shows the reject animation. **This is a
platform-specific DataTransfer-format issue. Do NOT guess the API — INSTRUMENT what a macOS Finder drop actually
carries, then extract accordingly.** (This is the drag-drop gotcha the round-3 brief warned about.) Firewall
green.

## Why it's rejected (confirmed by reading SchematicCanvas)
`TryExtractImagePath` only does:
```
if (item.TryGetRaw(DataFormat.File) is IEnumerable<IStorageItem> files)
    var path = files.FirstOrDefault()?.Path?.LocalPath;
```
If a macOS Finder drop doesn't surface as `DataFormat.File` → `IEnumerable<IStorageItem>` (Avalonia surfaces
dropped files differently per platform, and the raw payload TYPE varies), this returns null. Then in
`OnImageFileDragOver`, null → `DragDropEffects.None` → reject/snap-back. Two compounding suspects:
1. **Wrong/narrow format or payload type** — the Finder drop may present a different `DataFormat`, or
   `DataFormat.File` may return something other than `IEnumerable<IStorageItem>` (e.g. file-name strings, a URI
   list, or a single IStorageItem).
2. **DragOver can't read file contents on macOS** — file payloads are often only fully resolvable on DROP, not
   during DragOver. If so, DragOver must accept based on the PRESENCE of a file format (not full path
   extraction), and the path is resolved only in Drop.

## STEP 1 — Instrument the drop (do this FIRST; change no behavior except logging)
Add temporary logging in BOTH `OnImageFileDragOver` and `OnImageFileDrop` that dumps EVERYTHING the
`DataTransfer` exposes, then drag the user's PNG from Finder and report the literal output.
```
private static void DumpDataTransfer(string where, DragEventArgs e)
{
    System.Console.Error.WriteLine($"[DROP] --- {where} ---");
    try
    {
        int i = 0;
        foreach (var item in e.DataTransfer.Items)
        {
            System.Console.Error.WriteLine($"[DROP] item[{i}] formats: " +
                string.Join(", ", item.Formats.Select(f => f.ToString())));
            foreach (var fmt in item.Formats)
            {
                object? raw = null;
                try { raw = item.TryGetRaw(fmt); } catch (Exception ex) { raw = $"<throws: {ex.GetType().Name}>"; }
                System.Console.Error.WriteLine($"[DROP]   {fmt} → {raw?.GetType().FullName ?? "null"} = {Describe(raw)}");
            }
            i++;
        }
    }
    catch (Exception ex) { System.Console.Error.WriteLine($"[DROP] enumerate threw: {ex}"); }
}
private static string Describe(object? raw) => raw switch
{
    null => "null",
    string s => $"\"{s}\"",
    System.Collections.IEnumerable en when raw is not string =>
        "[" + string.Join(" | ", en.Cast<object?>().Select(o =>
            o is Avalonia.Platform.Storage.IStorageItem si ? si.Path?.LocalPath ?? si.Name : o?.ToString())) + "]",
    _ => raw.ToString() ?? "?",
};
```
Call `DumpDataTransfer("DragOver", e)` at the top of `OnImageFileDragOver` and
`DumpDataTransfer("Drop", e)` at the top of `OnImageFileDrop`. (Also confirm whether `OnImageFileDrop` is even
REACHED — if DragOver rejects, Drop never fires; the log tells us.)

**Run the exact repro:** drag `example.png` from Finder onto the schematic. Report the FULL `[DROP]` output for
DragOver (and Drop, if reached). We need: which `DataFormat`s appear, the .NET TYPE each `TryGetRaw` returns,
and the value (path string? IStorageItem? URI? file-name?).

## STEP 2 — Fix extraction based on what the log shows (do NOT pre-guess)
Once the log reveals the real format/type, broaden `TryExtractImagePath` to handle it. Likely outcomes and the
matching fix (apply the one the log supports — don't add all blindly):
- **A file format returns path STRINGS (not IStorageItem):** accept `string` and `IEnumerable<string>` and treat
  each as a path.
- **A different DataFormat carries the files** (e.g. a files/URI format distinct from `DataFormat.File`): match
  that format name from the log.
- **A URI-list / `text/uri-list` string** (`file:///…`): parse the URI(s) → LocalPath.
- **Single `IStorageItem` (not a list):** handle the non-enumerable case.
- **DragOver can't resolve the path but a file format is PRESENT:** in `OnImageFileDragOver`, accept
  (`DragDropEffects.Copy`) when a file-bearing format is present at all (by format name), and do the actual
  path extraction + image-validity check only in `OnImageFileDrop`. (This alone fixes the snap-back if the issue
  is DragOver-time unreadability.)
Keep the image-extension guard (`IsImageExtension`) for the final accept in Drop; if the path is non-image or
unreadable, create no bitmap (existing intended behavior).

## STEP 3 — Verify, then remove instrumentation
- Drag `example.png` from Finder → the drag shows a COPY cursor over the schematic (no snap-back) → drop creates
  a bitmap primitive at the drop point. 
- Drag a non-image file (e.g. a .txt) → rejected (no bitmap). 
- Palette component drag still works (the palette handler runs first and pre-marks Handled). 
- Repeat on the SYMBOL editor canvas (same fix must be applied there — the round-3 bitmap feature targets both;
  mirror whatever the log-driven fix is). 
- Remove all `[DROP]` logging once it works.

## Guardrails
- **Instrument first, then fix from evidence** — do not theorize the macOS format and ship a guess (this is the
  exact trap from the pin saga; one log run settles it).
- Don't break the Palette text-payload drop (it runs first and sets Handled on accept; the file handlers run
  only when palette rejected).
- DragOver may need to accept on format PRESENCE while Drop does the real extraction — that's expected for macOS
  file drops.
- Apply the same fix to BOTH SchematicCanvas and SymbolEditorCanvas.
- Keep the "no valid image path → no bitmap created" guard.
- Remove all `[DROP]` instrumentation before done. Build/test green; firewall green.

*Exit: a [DROP] transcript identifies exactly how a macOS Finder file drop is presented through DataTransfer;
the extraction is broadened to match; dragging example.png from Finder creates a bitmap in both editors;
instrumentation removed.*

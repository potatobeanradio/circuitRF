# Brief: polish-messages-followups — one-line rows, timestamps, dedupe .cws msg, .csym msg, selectable path

Five follow-up fixes from testing B6/B7. Touches `MessageEntry.cs`, `MessagesView.axaml(.cs)`, and
`WorkspaceViewModel.cs`. All small. Verify each at runtime (don't just build).

---

## Fix 1 — messages occupy one line (label + path on the same row)

**Problem:** the item template's body is a vertical `StackPanel`, so "Opened"/"Saved" render with the
label on line 1 and the file path on line 2.

**Fix:** replace the per-row `Grid`+`StackPanel` body in `MessagesView.axaml` with a single-line
`DockPanel`: icon (left), timestamp (left), path link (right), message text (fills + wraps). Short
path-bearing messages sit on one line; long messages (errors, no path) still wrap in the fill area.

Replace the entire `<DataTemplate x:DataType="msg:MessageEntry">…</DataTemplate>` body with:

```xml
<DataTemplate x:DataType="msg:MessageEntry">
    <DockPanel Margin="4,1" LastChildFill="True">

        <!-- Level icon (meaning, not color alone) -->
        <mi:MaterialIcon DockPanel.Dock="Left"
                         Kind="{Binding Level, Converter={StaticResource LevelToIcon}}"
                         Foreground="{Binding Level, Converter={StaticResource LevelToColor}}"
                         Width="14" Height="14"
                         Margin="0,0,6,0"
                         VerticalAlignment="Top"/>

        <!-- Timestamp (muted) — see Fix 2 -->
        <SelectableTextBlock DockPanel.Dock="Left"
                             Text="{Binding TimeText}"
                             FontSize="11"
                             Foreground="{DynamicResource SystemBaseMediumColor}"
                             Margin="0,0,6,0"
                             VerticalAlignment="Top"/>

        <!-- File path: selectable AND click-to-reveal — see Fixes 4/5 -->
        <SelectableTextBlock DockPanel.Dock="Right"
                             IsVisible="{Binding FilePath, Converter={StaticResource IsNotNull}}"
                             Text="{Binding FilePath}"
                             FontSize="11"
                             Foreground="{DynamicResource SystemAccentColor}"
                             TextDecorations="Underline"
                             Cursor="Hand"
                             Margin="8,0,0,0"
                             VerticalAlignment="Top"
                             Tapped="OnRevealPathTapped"
                             ToolTip.Tip="Reveal in file manager"/>

        <!-- Message text (fills; wraps for long messages) -->
        <SelectableTextBlock Text="{Binding Text}"
                             FontSize="12"
                             TextWrapping="Wrap"
                             Foreground="{DynamicResource SystemBaseHighColor}"
                             VerticalAlignment="Top"/>
    </DockPanel>
</DataTemplate>
```

Notes:
- The path docks right, so it's on the same line as a short label (e.g. `14:23:01  Saved … /…/foo.csch`).
  If you'd prefer the path immediately after the label instead of right-aligned, say so — but right-dock
  is the robust choice because it lets the message text keep `TextWrapping="Wrap"` for long errors.
- Also set the `ListBox` `SelectionMode="None"` (it's currently `"Multiple"`, unused) so row selection
  doesn't fight per-message text selection (Fix 5).

## Fix 2 — show the timestamp

`MessageEntry` already carries `Timestamp` (`DateTime`); the view just never showed it. Add a brief,
time-only display string on the record (`src/Ui/Messages/MessageEntry.cs`), inside the record body:

```csharp
public sealed record MessageEntry(
    MessageLevel Level, string Text, string? FilePath, DateTime Timestamp)
{
    /// <summary>Brief time-only stamp for the Messages panel.
    /// B3 (timestamp-format setting) will replace this fixed format with the AppPreferences value.</summary>
    public string TimeText => Timestamp.ToString("HH:mm:ss");

    // … existing static factories unchanged …
}
```

Bound via `{Binding TimeText}` in Fix 1. (Interim default `HH:mm:ss`; when B3 lands, route the format
through `AppPreferences` and update `TimeText` accordingly.)

## Fix 3 — don't show the `.cws` "Saved" message on a single-document save

Only show the `.cws` message on a true **Save All** (AllDocs scope). The `.cws` is still *written* on
every save (B19) — only the message is suppressed for single-doc saves.

In `WorkspaceViewModel.SaveAllDocuments`, change the `finally` block:

```csharp
finally
{
    if (CurrentWorkspacePath is not null)
        WriteWorkspaceFile(CurrentWorkspacePath, silent: ActiveSaveScope != SaveScope.AllDocs);
}
```

(`WriteWorkspaceFile(path, silent)` already exists; `silent: true` writes without posting "Saved".)
This preserves B19 (always writes `.cws`) while removing the redundant message on single `.csch`/`.csym`
saves.

## Fix 4 — a single materialized-symbol save posts a "Saved" message

The materialized-symbol save path calls `doc.ViewModel.SaveSymbolCommand.ExecuteAsync(...)`, which writes
the `.csym` but posts no Messages entry. (Scratch symbol paths — `SaveScratchSymbolToCell` /
`SaveScratchSymbolAsFile` — already post "Saved".) Add a small helper and route the three materialized
call sites through it.

Add to `WorkspaceViewModel`:

```csharp
/// <summary>Saves an already-materialized symbol via its VM command and logs one "Saved" message.</summary>
private async Task SaveMaterializedSymbolDoc(SymbolEditorDocument doc, Window owner)
{
    var path = doc.ViewModel.CurrentSymbolPath;
    await doc.ViewModel.SaveSymbolCommand.ExecuteAsync(owner);
    if (path is not null && !doc.IsDirty)   // dirty cleared ⇒ save succeeded
        Messages.Success("Saved", path);
}
```

Replace the three direct calls:
1. `SaveSingleSymbolDocument` (the `else` branch):
   `await doc.ViewModel.SaveSymbolCommand.ExecuteAsync(window);` → `await SaveMaterializedSymbolDoc(doc, window);`
2. `SaveAllDocuments` AllDocs loop:
   `foreach (var symDoc in dirtyMaterializedSymbols) await symDoc.ViewModel.SaveSymbolCommand.ExecuteAsync(window);`
   → `await SaveMaterializedSymbolDoc(symDoc, window);`
3. `PromptSaveBeforeClose` loop:
   `foreach (var symDoc in dirtyMatSymbols) await symDoc.ViewModel.SaveSymbolCommand.ExecuteAsync(owner);`
   → `await SaveMaterializedSymbolDoc(symDoc, owner);`

**Verify (avoid a double):** confirm `SymbolEditorViewModel.SaveSymbolCommand`/`PerformSave` and the
`OnSymbolSaved` handler do **not** already post a "Saved" message (the user currently sees none, so they
shouldn't). If one does, post in exactly one place — prefer the helper — and remove the other.

Net result: saving a `.csym` shows one `Saved … /…/foo.csym` message (Fix 3 removed the `.cws` noise).

## Fix 5 — the file path text is selectable

Fix 1 already renders the path as a `SelectableTextBlock` (was a `Button`), so its text is selectable
and copyable. Preserve click-to-reveal with a `Tapped` handler in
`src/Ui/Views/Messages/MessagesView.axaml.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Input;            // TappedEventArgs
using Avalonia.VisualTree;
using CircuitRF.Ui.Messages;     // MessageEntry
using CircuitRF.Ui.ViewModels.Dock;

// … inside MessagesView …
private void OnRevealPathTapped(object? sender, TappedEventArgs e)
{
    if (sender is Control { DataContext: MessageEntry { FilePath: { } path } }
        && DataContext is MessagesTool tool)
    {
        tool.RevealFileCommand.Execute(path);
        e.Handled = true;
    }
}
```

`RevealFileCommand` is the existing public command on `MessagesTool`. A single tap (no drag) reveals;
dragging selects the text. **Verify at runtime** that tap-to-reveal and drag-to-select coexist on the
`SelectableTextBlock`. If a single tap is swallowed by selection, fall back to `DoubleTapped` for reveal
(double-click to open, single-drag to select) — note which you chose.

## Acceptance

- Open/Save/Created/Wrote-netlist messages render on **one line**: `HH:mm:ss  <label>  <path-link>`.
- Every message shows a timestamp.
- Saving a single `.csch` shows exactly one message (the `.csch`), no `.cws` message.
- Saving a single `.csym` shows exactly one message (the `.csym`), no `.cws` message.
- **Save All** (a tool panel active, AllDocs scope) still shows the `.cws` "Saved" message.
- The path text can be selected/copied, and clicking it still reveals the file in the OS file manager.
- Long messages without a path still wrap (not clipped to one line).

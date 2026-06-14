# Brief: polish-messages-ux — Messages panel layout & selectable text

**Goal.** Tighten the Messages panel: remove the redundant in-view "Messages" header text, shrink
row height so more messages fit, and make each message's text user-selectable/copyable.

Authority: laundry-list "Message Panel" items (header removal, row height, selectable text). Size: **S–M**.
Pairs with **brief-messages-coverage** (B7) for the file-path conventions. Files:
`src/Ui/Views/Messages/MessagesView.axaml` only (no model/VM changes).

## Context (current state)

`MessagesView.axaml` has a header `Grid` with a `TextBlock Text="Messages"` + a Clear button, then a
`ListBox` whose item template is a `Grid` (icon + a `StackPanel` of a body `TextBlock` and an optional
file-link `Button`). The Dock **tab title** already says "Messages" (`MessagesTool.Title = "Messages"`),
so the in-view header text is redundant.

## Change 1 — remove the redundant "Messages" header text

Remove the title `TextBlock` from the header row; keep the Clear button. Minimal, low-risk version:
collapse the header `Grid` to just the right-aligned Clear button with a tight margin:

```xml
<!-- Header strip: Clear button only (tab title already labels the panel) -->
<Grid Grid.Row="0" Margin="4,1,4,1">
    <Button HorizontalAlignment="Right"
            Command="{Binding ClearMessagesCommand}"
            ToolTip.Tip="Clear messages"
            Padding="4,1"
            Background="Transparent"
            BorderThickness="0"
            FontSize="11">
        <mi:MaterialIcon Kind="DeleteOutline" Width="14" Height="14"/>
    </Button>
</Grid>
```

(Optional, if you want to reclaim the row entirely: drop the header row, make the outer container a
single-cell `Grid`, put the `ListBox` as the fill and the Clear `Button` as a top-right overlay with
`Background="{DynamicResource SystemChromeLowColor}"` so it reads cleanly over the corner. The
minimal version above is the safe default — note which you chose.)

## Change 2 — smaller row height

Tighten the per-row spacing so more messages are visible. In the `ListBox`:

- `ItemContainerTheme` → `ListBoxItem` `Padding` `2` → `0`.
- Item template root `Grid` `Margin="4,2"` → `Margin="4,1"`.
- Body `StackPanel` `Spacing="1"` → `Spacing="0"`.
- Keep the level icon at `Width/Height=14`; change its `Margin="0,0,6,0"` and `VerticalAlignment="Top"`
  to `Margin="0,1,6,0"` so it aligns with the first text line at the tighter spacing.

Keep body font at 12 and link font at 11 (legible). Don't shrink the icon.

## Change 3 — selectable / copyable message text

Make each message body selectable so the user can copy text out. Replace the body `TextBlock` with a
`SelectableTextBlock`:

```xml
<SelectableTextBlock Text="{Binding Text}"
                     FontSize="12"
                     TextWrapping="Wrap"
                     Foreground="{DynamicResource SystemBaseHighColor}"/>
```

`SelectableTextBlock` supports drag-select + Ctrl/Cmd+C. The file-path link stays a `Button` (its
`ToolTip` already says "Reveal in file manager").

Interaction note: the `ListBox` currently has `SelectionMode="Multiple"` (row selection isn't wired to
any command). If row-drag selection competes with text selection or scrolling feels off, set the
`ListBox` `SelectionMode="None"` — per-message `SelectableTextBlock` already provides copy. Prefer
leaving it as-is unless you observe a conflict; note the outcome.

## Out of scope (handled elsewhere)

- **Timestamps** + format setting → brief-messages-timestamps (B3). `MessageEntry.Timestamp` already
  exists; that brief adds rendering + an AppPreferences format setting. Leave room — don't add a
  timestamp column here.
- **Path shown once / links on opens / log every write / external-open failure** → these are caller
  conventions, handled in brief-messages-coverage (B7). The view already renders the path once (as the
  link) and shows a link for any message with a `FilePath`; nothing to change here for that.

## Acceptance

- The in-view "Messages" title text is gone; the Clear button still works; the Dock tab still reads
  "Messages".
- Rows are visibly tighter (more messages fit in the same pane height) while staying legible, icons
  aligned.
- Selecting text in a message and Ctrl/Cmd+C copies it; auto-scroll-to-newest still works.

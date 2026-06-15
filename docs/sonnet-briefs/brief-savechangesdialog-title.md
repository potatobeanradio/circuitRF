# Sonnet Brief — SaveChangesDialog shows a context-wrong title

**Bug:** `SaveChangesDialog` always shows the same window title regardless of why it's being shown. The title is
hardcoded in `src/Ui/Views/Dialogs/SaveChangesDialog.axaml` as `Title="Save Changes"` and the constructor
(`SaveChangesDialog.axaml.cs`) sets the message text and the three button labels but **never sets `Title`**. So
when the dialog is reused for other confirmations (auto-generate-symbol prompt, and the new Remove Cell / Remove
file / Remove Data Display confirmations), the title bar still says "Save Changes" — misleading for, e.g., a
"Remove cell '<name>'?" confirm.

## Fix — parameterize the title
In `SaveChangesDialog.axaml.cs`, add a `title` parameter to the message constructor (LAST, so existing positional
calls keep compiling) and apply it:
```csharp
public SaveChangesDialog(
    string  message,
    string  saveLabel     = "Save",
    string? dontSaveLabel = "Don't Save",
    string  cancelLabel   = "Cancel",
    string  title         = "Save Changes") : this()
{
    Title                = title;          // ← was never set; AXAML default "Save Changes" leaked into every context
    MessageText.Text     = message;
    SaveButton.Content   = saveLabel;
    CancelButton.Content = cancelLabel;
    if (dontSaveLabel is null)
        DontSaveButton.IsVisible = false;
    else
        DontSaveButton.Content = dontSaveLabel;
}
```
Leave the AXAML `Title="Save Changes"` as the design-time default (harmless; the constructor overrides it at
runtime).

## Fix — give every call site a fitting title
Find every `new SaveChangesDialog(` call site (grep the `src/Ui` tree) and pass an appropriate `title:`. Known
ones today:
- **Unsaved-changes / save-before-close prompt** (`WorkspaceViewModel` `PromptSaveBeforeClose` and any
  workspace/document close path): `title: "Unsaved Changes"` (the body asks to save before closing; "Save
  Changes" is also acceptable here — pick one and keep it consistent).
- **Auto-generate-symbol prompt** (`SchematicView.axaml.cs` `ShowAutoGenPromptAsync`, which uses Yes/No labels):
  `title: "Generate Symbol"`.
- Any other current callers: set a title matching the body's intent (don't leave the default where it would
  read wrong).

For the **Remove** dialogs introduced by the housecleaning briefs, pass the matching titles when you implement
those briefs:
- Remove Cell → `title: "Remove Cell"`
- Remove Data Display → `title: "Remove Data Display"`
- Remove file/results → `title: "Remove"`

Use a named argument (`title: "…"`) at each call site so the labels/title can't get positionally confused.

## Test (`tests/Ui.Tests`, headless — construct the dialog, don't show it)
**`SaveChangesDialog_SetsTitleFromArg`**: `new SaveChangesDialog("body", saveLabel:"Remove", dontSaveLabel:null,
cancelLabel:"Cancel", title:"Remove Cell")` → `dialog.Title == "Remove Cell"`, `SaveButton.Content == "Remove"`,
`DontSaveButton.IsVisible == false`. A second case with no `title` arg → `dialog.Title == "Save Changes"`
(default preserved, no regression).

## Gate
Build 0W/0E; test green. Manually: the auto-generate-symbol prompt and each Remove confirmation show a title that
matches what's being asked; the save-before-close prompt still reads correctly. No call site leaves the default
title where it would mislead.

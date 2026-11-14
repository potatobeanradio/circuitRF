using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// <b>Copy a workspace here</b> (RC-9 R-rc9-1, <c>docs/design/revision-control.md</c> §9).
///
/// <para>The strongest product argument in the whole architecture, and once git is already being
/// driven as a subprocess it is nearly free: a public library of reference designs, opened in one
/// action, from any git host — with exchanging changes for those who want it, and copy-and-read for
/// somebody who only wants to look. It is also the mechanism RC-2's refusal points at: <b>the way to
/// edit a library is to open it as a workspace of its own</b>, which on a shared one is a copy plus a
/// send, or a request to its maintainer. Either way it is a deliberate act with a record.</para>
///
/// <para><b>Two fields, and nothing host-specific</b> (R-rc9-4). No account, no provider list, no
/// sign-in button: circuitRF binds to git, not to any hosting service, and whatever git accepts as an
/// address is what this accepts. <b>And no credential field</b> (§9.1) — circuitRF holds no sign-in
/// and asks for none; it uses whatever git on this machine is already configured with, and an address
/// that needs one git cannot supply produces a refusal naming what was wanted rather than a window
/// that never comes back.</para>
///
/// <para><b>Nothing here starts a process.</b> The dialog collects two strings and the caller does the
/// work, which is what lets the refusals be the caller's — and lets this be tested without a
/// network.</para>
/// </summary>
public partial class CopyWorkspaceDialog : Window
{
    /// <summary>The address, once the dialog has closed with Copy. Null when it was cancelled.</summary>
    public string? Source { get; private set; }

    /// <summary>The folder to copy into. Null when the dialog was cancelled.</summary>
    public string? Destination { get; private set; }

    // The XAML loader needs a parameterless constructor (AVLN3001).
    public CopyWorkspaceDialog() : this(null) { }

    /// <param name="startIn">Where Browse opens, usually beside the workspace already open.</param>
    public CopyWorkspaceDialog(string? startIn)
    {
        InitializeComponent();
        _startIn = startIn;

        // R-rc9-5a's row, BEFORE the button rather than after it. "That is what I expected" and "why
        // has my history gone" are the same sentence at two different moments, and the three ways a
        // workspace leaves a machine disagree deliberately.
        WhatComesLabel.Text =
            "What comes with it: every version its author kept, and the workspace itself. What does "
          + "not: their automatic restore points, which belong to the machine they were taken on. "
          + "circuitRF starts a safety net of your own here the first time it has something to record.";
    }

    private readonly string? _startIn;

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var start = _startIn is { Length: > 0 }
                ? await StorageProvider.TryGetReachableFolderAsync(_startIn)
                : null;

            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title                  = "Where should the copy go?",
                AllowMultiple          = false,
                SuggestedStartLocation = start,
            });

            if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { Length: > 0 } chosen) return;

            // The picker returns a folder that EXISTS, and a copy is only made into an empty one — so
            // a name derived from the address is appended rather than the picked folder being used as
            // it stands. The designer can type over it; what they cannot do is press Copy and have it
            // refuse for a reason they did not create.
            string leaf = SuggestedFolderName(SourceBox.Text);
            DestinationBox.Text = leaf.Length > 0 ? Path.Combine(chosen, leaf) : chosen;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "That folder could not be used: " + ex.Message;
        }
    }

    /// <summary>
    /// A folder name from an address — the last path segment with a trailing <c>.git</c> off it, which
    /// is the convention every git host writes. <b>Only ever a SUGGESTION in a field the designer can
    /// see and change</b> (R-rc9-4): circuitRF never derives a destination and acts on it.
    /// </summary>
    internal static string SuggestedFolderName(string? source)
    {
        string s = (source ?? "").Trim().TrimEnd('/', '\\');
        if (s.Length == 0) return "";

        int cut = s.LastIndexOfAny(['/', '\\', ':']);
        string leaf = cut >= 0 ? s[(cut + 1)..] : s;
        if (leaf.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) leaf = leaf[..^4];

        foreach (char bad in Path.GetInvalidFileNameChars()) leaf = leaf.Replace(bad, '-');
        return leaf.Trim();
    }

    private void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        string source      = (SourceBox.Text ?? "").Trim();
        string destination = (DestinationBox.Text ?? "").Trim();

        if (source.Length == 0)      { StatusLabel.Text = "Give the address to copy from."; return; }
        if (destination.Length == 0) { StatusLabel.Text = "Say where to put it.";           return; }

        Source      = source;
        Destination = destination;
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}

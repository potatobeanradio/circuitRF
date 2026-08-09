using System;
using System.Collections.Generic;
using System.Text;

namespace CircuitRF.Ui.Messages;

/// <summary>
/// Selection/clipboard text helpers for the Messages panel. Framework-free on purpose: the actual
/// defect this exists to work around is one line of arithmetic, and keeping it out of view code is
/// what makes it directly testable (no Avalonia runtime).
///
/// <para><b>The bug (owner-reported, 2026-07-30).</b> Selecting message text and right-clicking
/// offered an enabled Copy — but extending the selection to the far right of the control, past the
/// end of the visible string, silently DISABLED Copy. Root-caused by decompiling Avalonia 12.0.0:
/// <c>SelectableTextBlock.GetSelection()</c> ends with</para>
/// <code>
///   if (num2 == num3 || num &lt; num3) return "";   // num = Inlines.Text.Length, num3 = selection end
/// </code>
/// <para>and <c>UpdateCommandStates</c> then sets <c>CanCopy = !string.IsNullOrEmpty(selection)</c>.
/// So a selection whose END index exceeds <c>Inlines.Text.Length</c> yields an empty string and a
/// disabled Copy, instead of being clamped to the available text.</para>
///
/// <para><b>Why the indices diverge here specifically:</b> each message row's
/// <c>SelectableTextBlock</c> ends with an <c>InlineUIContainer</c> (the clickable file-path link).
/// <c>InlineUIContainer.BuildTextRun</c> adds an <c>EmbeddedControlRun</c> — so it OCCUPIES positions
/// in the hit-test/character index space the selection is expressed in — while its
/// <c>AppendText</c> override is <b>empty</b>, contributing zero characters to <c>Inlines.Text</c>.
/// The two index spaces therefore differ by the container's run length, and dragging to the end of
/// the line reliably pushes the selection end past the text length. The container is present on
/// EVERY row, including messages with no file path (it is merely invisible), which is why the
/// symptom appeared for ordinary messages too.</para>
///
/// <para>Clamping is the whole fix. <see cref="Clamp"/> is deliberately total — it never throws for
/// any index pair, in or out of range, ordered or reversed.</para>
/// </summary>
internal static class MessageTextSelection
{
    /// <summary>
    /// Returns the selected substring of <paramref name="text"/>, clamping both indices into range
    /// and tolerating a reversed selection (drag right-to-left gives end &lt; start).
    /// Returns "" only when the selection is genuinely empty or the text is.
    /// </summary>
    public static string Clamp(string? text, int selectionStart, int selectionEnd)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var lo = Math.Min(selectionStart, selectionEnd);
        var hi = Math.Max(selectionStart, selectionEnd);

        // THE fix: clamp instead of bailing out. Avalonia returns "" when hi > text.Length.
        lo = Math.Clamp(lo, 0, text.Length);
        hi = Math.Clamp(hi, 0, text.Length);

        return hi > lo ? text.Substring(lo, hi - lo) : string.Empty;
    }

    /// <summary>
    /// One message as a single clipboard line: timestamp, text, then the file path when present.
    /// Shared by the per-row Copy fallback and by Copy All so the two can never format differently.
    /// </summary>
    public static string FormatEntry(MessageEntry entry)
    {
        if (entry is null) return string.Empty;

        // Uses Text, not TextInline: TextInline carries the two-space gaps that exist purely to
        // separate the on-screen runs, and they have no business in clipboard content.
        var sb = new StringBuilder();
        void Add(string? part)
        {
            if (string.IsNullOrWhiteSpace(part)) return;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(part.Trim());
        }

        Add(entry.TimeText);
        Add(entry.Text);
        // The live row's counter lives in its own element (it renders after the progress bar) — the
        // clipboard has no bar to sit around, so it just follows the text.
        Add(entry.ProgressText);
        Add(entry.FilePath);
        return sb.ToString();
    }

    /// <summary>
    /// Every message as one continuous newline-separated string, in display order.
    /// Returns "" for a null/empty list so a caller can skip the clipboard write.
    /// </summary>
    public static string FormatAll(IEnumerable<MessageEntry>? entries)
    {
        if (entries is null) return string.Empty;

        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            var line = FormatEntry(e);
            if (line.Length == 0) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        return sb.ToString();
    }
}

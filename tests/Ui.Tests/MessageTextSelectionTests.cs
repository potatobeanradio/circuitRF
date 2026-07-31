using System;
using CircuitRF.Ui.Messages;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Messages panel: "Copy goes disabled when I select to the end of the line".
//
//  Root cause (owner-reported 2026-07-30, confirmed by decompiling Avalonia 12.0.0):
//  SelectableTextBlock.GetSelection() ends with
//
//      if (num2 == num3 || num < num3) return "";     // num = Inlines.Text.Length
//
//  and UpdateCommandStates() then does CanCopy = !string.IsNullOrEmpty(selection). So a selection
//  whose END index exceeds the text length yields "" and a DISABLED Copy rather than being clamped.
//
//  The indices diverge because every message row's SelectableTextBlock ends with an
//  InlineUIContainer (the file-path link): InlineUIContainer.BuildTextRun adds an EmbeddedControlRun
//  (so it occupies positions in the hit-test index space the selection uses) while its AppendText
//  override is EMPTY (so it contributes zero characters to Inlines.Text).
// ──────────────────────────────────────────────────────────────────────────────

public sealed class MessageTextSelectionTests
{
    // ── The headline regression: the exact shape Avalonia gets wrong ──────────

    [Fact]
    public void Clamp_SelectionEndPastTextLength_StillReturnsText_NotEmpty()
    {
        const string text = "12:00:00  Wrote netlist.cnl  ";

        // An InlineUIContainer sits after the text, so dragging to the far right lands the selection
        // end BEYOND text.Length. Avalonia returns "" here (-> Copy greys out); we must not.
        var selected = MessageTextSelection.Clamp(text, 0, text.Length + 8);

        Assert.False(string.IsNullOrEmpty(selected));
        Assert.Equal(text, selected);
    }

    [Fact]
    public void Clamp_ReproducesAvaloniaGuard_ToProveTheTestHasTeeth()
    {
        const string text = "hello world";
        const int end = 25;               // past the end, as a real drag produces

        // What Avalonia does (the defect), reconstructed by hand rather than described:
        static string AvaloniaGetSelection(string t, int start, int end)
        {
            var lo = Math.Min(start, end);
            var hi = Math.Max(start, end);
            if (lo == hi || t.Length < hi) return "";   // <- the unclamped bail-out
            return t.Substring(lo, hi - lo);
        }

        Assert.Equal("", AvaloniaGetSelection(text, 0, end));            // disabled Copy
        Assert.Equal(text, MessageTextSelection.Clamp(text, 0, end));    // fixed
    }

    // ── Totality: never throws, for any index pair ───────────────────────────

    [Theory]
    [InlineData(0, 5, "hello")]
    [InlineData(5, 0, "hello")]          // reversed drag (right-to-left)
    [InlineData(6, 11, "world")]
    [InlineData(-4, 5, "hello")]         // negative start clamps to 0
    [InlineData(0, 999, "hello world")]  // far past the end
    [InlineData(999, 999, "")]           // wholly out of range -> empty, not a throw
    [InlineData(3, 3, "")]               // caret only, no selection
    public void Clamp_IsTotal(int start, int end, string expected)
        => Assert.Equal(expected, MessageTextSelection.Clamp("hello world", start, end));

    [Fact]
    public void Clamp_NullOrEmptyText_ReturnsEmpty_NeverThrows()
    {
        Assert.Equal("", MessageTextSelection.Clamp(null, 0, 10));
        Assert.Equal("", MessageTextSelection.Clamp("", 0, 10));
    }

    // ── Copy All / per-entry formatting ──────────────────────────────────────

    private static MessageEntry Entry(string text, string? path = null)
        => new(MessageLevel.Info, text, path, new DateTime(2026, 7, 30, 12, 0, 0));

    [Fact]
    public void FormatEntry_UsesRawText_NotTheInlinePaddedVariant()
    {
        var line = MessageTextSelection.FormatEntry(Entry("Wrote netlist.cnl"));

        // TextInline pads with two spaces either side purely for on-screen run separation; that
        // padding must not reach the clipboard.
        Assert.DoesNotContain("  ", line);
        Assert.Contains("Wrote netlist.cnl", line);
    }

    [Fact]
    public void FormatEntry_IncludesFilePath_WhenPresent()
    {
        var line = MessageTextSelection.FormatEntry(Entry("Wrote", "/tmp/netlist.cnl"));
        Assert.Contains("/tmp/netlist.cnl", line);
    }

    [Fact]
    public void FormatAll_IsOneNewlineSeparatedStringInDisplayOrder()
    {
        var all = MessageTextSelection.FormatAll(new[]
        {
            Entry("first"), Entry("second"), Entry("third"),
        });

        var lines = all.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Contains("first",  lines[0]);
        Assert.Contains("second", lines[1]);
        Assert.Contains("third",  lines[2]);
    }

    [Fact]
    public void FormatAll_EmptyOrNull_ReturnsEmpty_SoCallerCanSkipTheClipboardWrite()
    {
        Assert.Equal("", MessageTextSelection.FormatAll(null));
        Assert.Equal("", MessageTextSelection.FormatAll(Array.Empty<MessageEntry>()));
    }
}

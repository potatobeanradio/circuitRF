// The format a circuitRF copy carries its own document in — a plot's configuration, a schematic, layout or
// symbol selection, a wBond fragment — beside the pictures it puts on the clipboard for everybody else.
//
// It is deliberately NOT the clipboard's plain text. A receiver chooses among the formats on offer by its own
// rule, not by the order they were written in, and a chat client's message box takes text whenever text is
// there: a Data Display plot pasted into a chat arrived as its JSON instead of as the picture beside it. With
// the document in a format only circuitRF reads, every other application finds pictures and nothing else, and
// takes one. circuitRF reads this format first and plain text after it (ClipboardRead.TextAsync), so a
// payload copied as text by an earlier build, or typed into another application, still pastes.
//
// The bytes are UTF-8 with a NUL terminator. Windows hands back an HGLOBAL's whole allocation (GlobalSize),
// which may be rounded up past what was written, and JSON never contains a raw NUL — so the reader cuts at
// the first one on every platform.

using System;
using System.Text;
using Avalonia.Input;

namespace CircuitRF.Ui.Clipboard;

internal static class ClipboardPayload
{
    /// <summary>The application-format identifier. Every circuitRF executable (circuitRF, harmonicaRF,
    /// wBond) uses the same one, so a copy in one pastes in another.</summary>
    public const string Identifier = "circuitRF.payload";

    /// <summary>The format through Avalonia's clipboard.</summary>
    public static readonly DataFormat<byte[]> Format = DataFormat.CreateBytesApplicationFormat(Identifier);

    /// <summary>The registered Windows clipboard format name Avalonia gives <see cref="Format"/> — what
    /// <c>WindowsClipboard.SetClipboard</c>, which bypasses Avalonia, must register to be read back by it.</summary>
    public const string WindowsFormatName = "avn-app-fmt:" + Identifier;

    public static byte[] Encode(string text)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, 0);
        return bytes;
    }

    public static string? Decode(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        int end = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }

    /// <summary>Puts <paramref name="text"/> on <paramref name="item"/> in <see cref="Format"/>.</summary>
    public static void Set(DataTransferItem item, string text) => item.Set(Format, Encode(text));
}

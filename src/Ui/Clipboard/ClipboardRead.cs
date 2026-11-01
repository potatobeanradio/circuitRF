// Every read of the SYSTEM clipboard goes through here, because the platform can refuse one at any moment
// and the refusal is an exception, not an empty result. On Windows another process can hold the
// clipboard open, or replace its data object between our enumerating its formats and reading one, and
// Avalonia's OLE wrapper then throws a COMException ("EnumFormatEtc failed") out of a call whose
// signature promises null. Unhandled on the UI thread that terminates the application — the Data
// Display's Copy did exactly that, because Copy re-reads the clipboard to decide whether Paste is enabled.
//
// A clipboard that cannot be read is indistinguishable, to every caller here, from one holding nothing
// pasteable: the read answers the fallback and the trail records why.

using System;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using CircuitRF.Ui.Diagnostics;

namespace CircuitRF.Ui.Clipboard;

public static class ClipboardRead
{
    /// <summary>The clipboard's text — a circuitRF copy's own document (<see cref="ClipboardPayload"/>) when it
    /// holds one, else its plain text — or null when it holds neither, there is no clipboard, or the platform
    /// refused the read.</summary>
    public static async Task<string?> TextAsync(IClipboard? clipboard)
    {
        if (clipboard is null) return null;
        var payload = ClipboardPayload.Decode(await Try(() => clipboard.TryGetValueAsync(ClipboardPayload.Format), null));
        if (!string.IsNullOrEmpty(payload)) return payload;
        return await Try(clipboard.TryGetTextAsync, null);
    }

    /// <summary>Runs one clipboard read, answering <paramref name="fallback"/> when the platform throws.</summary>
    public static async Task<T> Try<T>(Func<Task<T>> read, T fallback)
    {
        try { return await read(); }
        catch (Exception ex)
        {
            CrashReporter.NoteHandled("clipboard read", ex);
            return fallback;
        }
    }
}

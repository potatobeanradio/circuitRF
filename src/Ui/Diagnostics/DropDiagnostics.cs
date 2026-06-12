using System;
using System.Collections;
using System.Linq;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace CircuitRF.Ui.Diagnostics;

/// <summary>
/// Dormant drag-and-drop diagnostic. Dumps every <see cref="DataFormat"/> a drop carries and the
/// .NET type + value of each <c>TryGetRaw</c> payload to stderr — the one log run that pins down how a
/// given OS presents a Finder/Explorer file drop through Avalonia's DataTransfer model.
///
/// OFF by default (zero output, negligible cost). Enable per-run with the env var:
///   CIRCUITRF_DROP_DEBUG=1 dotnet run --project src/Ui 2>&amp;1 | grep '^\[DROP\]'
///
/// History: a macOS Finder drop surfaces <c>Universal: File</c> as a SINGLE
/// <c>Avalonia.Native.StorageFile</c> (IStorageItem), not an IEnumerable&lt;IStorageItem&gt; — which is why
/// the first cut of TryExtractImagePath rejected it. Keep this around to diagnose the WINDOWS variant when the
/// dev machine changes (Windows is expected to present files differently again; this dump will show exactly
/// how, so TryExtractImagePath's switch can be extended from evidence rather than guesswork).
/// </summary>
public static class DropDiagnostics
{
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("CIRCUITRF_DROP_DEBUG") is "1" or "true" or "TRUE";

    /// <summary>Dumps the DataTransfer of a drag/drop event when CIRCUITRF_DROP_DEBUG is set; otherwise a no-op.</summary>
    public static void Dump(string where, DragEventArgs e)
    {
        if (!Enabled) return;
        Console.Error.WriteLine($"[DROP] --- {where} ---");
        try
        {
            int i = 0;
            foreach (var item in e.DataTransfer.Items)
            {
                Console.Error.WriteLine($"[DROP] item[{i}] formats: " +
                    string.Join(", ", item.Formats.Select(f => f.ToString())));
                foreach (var fmt in item.Formats)
                {
                    object? raw;
                    try { raw = item.TryGetRaw(fmt); }
                    catch (Exception ex) { raw = $"<throws: {ex.GetType().Name}>"; }
                    Console.Error.WriteLine(
                        $"[DROP]   {fmt} → {raw?.GetType().FullName ?? "null"} = {Describe(raw)}");
                }
                i++;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[DROP] enumerate threw: {ex}"); }
    }

    private static string Describe(object? raw) => raw switch
    {
        null => "null",
        string s => $"\"{s}\"",
        IStorageItem si => si.Path?.LocalPath ?? si.Name,
        IEnumerable en => "[" + string.Join(" | ", en.Cast<object?>().Select(o =>
            o is IStorageItem inner ? inner.Path?.LocalPath ?? inner.Name : o?.ToString())) + "]",
        _ => raw.ToString() ?? "?",
    };
}

using System.Buffers.Binary;
using System.Text;

namespace CircuitRF.Core.Pdk;

/// <summary>One terminal a symbol template declares, at the position the kit drew it.</summary>
/// <param name="Name">The kit's own name for the pin — often just its index. Never invented here.</param>
/// <param name="X">Horizontal position, in the library's own units.</param>
/// <param name="Y">Vertical position, in the library's own units.</param>
public sealed record KitSymbolPin(string Name, int X, int Y);

/// <summary>One symbol a library declares, and the terminals it carries.</summary>
/// <param name="Name">The symbol's own name — what a part's catalog entry references it by.</param>
/// <param name="Pins">In the order the library declares them.</param>
public sealed record KitSymbolTemplate(string Name, IReadOnlyList<KitSymbolPin> Pins);

/// <summary>
/// Reads a record-based binary symbol LIBRARY: one file holding several named symbols, each with its
/// terminals.
///
/// <para><b>Why a library rather than a file per symbol matters.</b> The readers beside this one take
/// one drawing per file. A library inverts that — many parts share a handful of templates, and a part
/// names the one it wants. Measured: <b>7 templates serving 109 parts</b>, in about four
/// kilobytes. So reading this one file is what makes a whole kit placeable, and the same seven symbols
/// are what the palette then shows.</para>
///
/// <para><b>This reads a FORMAT.</b> The two record tags below are the format's own structure names,
/// exactly as <see cref="KitSymbolDefinitionReader"/>'s <c>create_parm</c> is the definition
/// language's. Nothing here names a supplier, a library, a part or a model family, and the fixtures
/// that exercise it are synthetic.</para>
///
/// <para><b>Best-effort, and deliberately partial.</b> Only the records whose layout is unambiguous
/// are read: the symbol names and the terminals. The drawn body — lines, arcs, text placement — is
/// not, so a part gets correct, correctly-named pins and a body circuitRF draws itself. That is the
/// half that decides whether a part can be wired up; the rest is appearance.</para>
/// </summary>
public static class KitSymbolLibraryReader
{
    /// <summary>Starts a symbol. Its name follows, terminated by the '@' that qualifies it.</summary>
    private static readonly byte[] SymbolTag = "KDefaultSymb_2"u8.ToArray();

    /// <summary>Starts a terminal record: seven little-endian int32 fields, then the pin's name.</summary>
    private static readonly byte[] PinTag = "KNodePos"u8.ToArray();

    /// <summary>Fields before the pin name. Constant across every record observed.</summary>
    private const int PinFieldBytes = 7 * sizeof(int);

    private const long MaxFileBytes = 4 * 1024 * 1024;
    private const int  MaxNameChars = 64;

    public static IReadOnlyList<KitSymbolTemplate> TryReadFile(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxFileBytes) return [];
            return Read(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Reads every symbol the library declares. Empty when the bytes are not one.</summary>
    public static IReadOnlyList<KitSymbolTemplate> Read(ReadOnlySpan<byte> data)
    {
        var starts = new List<int>();
        for (int i = IndexOf(data, SymbolTag, 0); i >= 0; i = IndexOf(data, SymbolTag, i + 1))
            starts.Add(i);
        if (starts.Count == 0) return [];

        var result = new List<KitSymbolTemplate>(starts.Count);
        for (int s = 0; s < starts.Count; s++)
        {
            // A symbol owns everything up to the next one; the last owns the remainder.
            int from = starts[s];
            int to   = s + 1 < starts.Count ? starts[s + 1] : data.Length;
            var seg  = data[from..to];

            string name = ReadSymbolName(seg[SymbolTag.Length..]);
            if (name.Length == 0) continue;

            var pins = new List<KitSymbolPin>();
            for (int p = IndexOf(seg, PinTag, 0); p >= 0; p = IndexOf(seg, PinTag, p + 1))
            {
                int at = p + PinTag.Length;
                if (at + PinFieldBytes >= seg.Length) break;

                var f = seg[at..(at + PinFieldBytes)];
                int x = BinaryPrimitives.ReadInt32LittleEndian(f[4..]);
                int y = BinaryPrimitives.ReadInt32LittleEndian(f[8..]);

                // The name follows the fields. A pin whose name is unreadable still counts — losing
                // it would silently change the terminal COUNT, which is the one thing everything
                // downstream relies on.
                string pin = ReadRun(seg[(at + PinFieldBytes)..], 16);
                pins.Add(new KitSymbolPin(pin.Length > 0 ? pin : (pins.Count + 1).ToString(), x, y));
            }

            if (pins.Count > 0) result.Add(new KitSymbolTemplate(name, pins));
        }
        return result;
    }

    /// <summary>
    /// The symbol's name: the printable run that is qualified by '@'. Taking the FIRST printable run
    /// instead would pick up whatever padding the record begins with.
    /// </summary>
    private static string ReadSymbolName(ReadOnlySpan<byte> seg)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < seg.Length && sb.Length <= MaxNameChars; i++)
        {
            byte b = seg[i];
            if (b == (byte)'@') return sb.ToString().Trim();
            if (IsNameChar(b)) sb.Append((char)b);
            else sb.Clear();                       // a break ends the candidate; start again
        }
        return "";
    }

    /// <summary>
    /// A short printable run, for a pin name.
    ///
    /// <para>Brackets end it. They frame records in this format, so a name sits immediately against
    /// the terminator of its own record and the opener of the next — taking every printable byte
    /// reads a pin called <c>1</c> as <c>1][</c>. That is not a crash and not obviously wrong in a
    /// dump; it just quietly renames every pin on every symbol.</para>
    /// </summary>
    private static string ReadRun(ReadOnlySpan<byte> seg, int max)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < seg.Length && sb.Length < max; i++)
        {
            byte b = seg[i];
            if (b is (byte)'[' or (byte)']') break;
            if (b is >= 0x21 and <= 0x7E) sb.Append((char)b);
            else break;
        }
        return sb.ToString();
    }

    private static bool IsNameChar(byte b) =>
        b is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z')
          or (>= (byte)'0' and <= (byte)'9') or (byte)'_' or (byte)'-' or (byte)' ';

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle, int from)
    {
        if (from < 0 || from >= haystack.Length) return -1;
        int at = haystack[from..].IndexOf(needle);
        return at < 0 ? -1 : at + from;
    }
}

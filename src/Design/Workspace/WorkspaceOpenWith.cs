// A workspace's two files are named `.cws` and `.cwsuser` — a name and no stem. macOS reads a name that STARTS with a dot as
// a hidden file with no extension at all, so neither file ever takes the circuitRF Workspace type the bundle declares for
// `cws`/`cwsuser`: both resolve to plain data, and a double-click in Finder opened them in a text editor. No declaration in
// Info.plist can reach a file that has no extension.
//
// What does reach it is the per-file choice Finder's own Get Info ▸ Open With records: the
// `com.apple.LaunchServices.OpenWith` extended attribute, a property list naming the application. This writes that, after
// every save of either file and whenever the GUI opens a workspace, so an existing workspace is marked the first time it is
// opened. It is metadata, not content: the bytes, the modification time and what git sees are unchanged. Windows and Linux
// need none of this — both match `.cws` by its trailing `.cws` — and the call does nothing there.
//
// Measured on macOS before writing it: a dot-named `.cws` resolved to a text editor, and to circuitRF once marked; a stale `path`
// still resolved through `bundleidentifier`, and so did a list with no `path` at all — which is what a process outside a
// bundle (a `dotnet run`, a test) writes.

using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CircuitRF.Design.Workspace;

/// <summary>Marks a workspace's <c>.cws</c> and <c>.cwsuser</c> to open in circuitRF from Finder.</summary>
public static partial class WorkspaceOpenWith
{
    /// <summary>circuitRF's <c>CFBundleIdentifier</c>; a test holds it equal to <c>Info.plist</c>'s.</summary>
    public const string BundleIdentifier = "com.circuitRF.circuitRF";

    /// <summary>The extended attribute Finder's Open With writes.</summary>
    public const string AttributeName = "com.apple.LaunchServices.OpenWith";

    /// <summary>Marks the workspace file <paramref name="cwsPath"/> and its <c>.cwsuser</c> beside it, those that exist.
    /// Does nothing off macOS; a failure (a read-only volume, a file system with no extended attributes) is ignored, since
    /// the mark only chooses which application a double-click starts.</summary>
    public static void MarkWorkspace(string cwsPath)
    {
        Mark(cwsPath);
        Mark(WorkspaceUserPersistence.PathFor(cwsPath));
    }

    /// <summary>Marks one file. See <see cref="MarkWorkspace"/>.</summary>
    public static void Mark(string path)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            if (!File.Exists(path)) return;
            if (Value(RunningBundlePath()) is not { } value) return;
            _ = setxattr(path, AttributeName, value, (nuint)value.Length, 0, 0);
        }
        catch { /* the mark is a convenience; the file is already written */ }
    }

    /// <summary>The attribute's value: a binary property list <c>{bundleidentifier, path?, version = 0}</c>, the form
    /// Finder writes. <paramref name="appPath"/> null leaves <c>path</c> out; null when the list would not fit two-byte
    /// offsets (a path tens of thousands of characters long).</summary>
    internal static byte[]? Value(string? appPath)
    {
        var entries = new List<(string Key, object Value)> { ("bundleidentifier", BundleIdentifier) };
        if (appPath is not null) entries.Add(("path", appPath));
        entries.Add(("version", 0));
        return BinaryPlist(entries);
    }

    /// <summary>The <c>.app</c> this process runs from, when that bundle is circuitRF's; otherwise null.</summary>
    private static string? RunningBundlePath()
    {
        string? exe = Environment.ProcessPath;
        int at = exe?.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal) ?? -1;
        if (exe is null || at < 0) return null;
        string bundle = exe[..(at + 4)];
        try
        {
            string plist = File.ReadAllText(Path.Combine(bundle, "Contents", "Info.plist"));
            return IdentifierRx().Match(plist) is { Success: true } m && m.Groups[1].Value == BundleIdentifier ? bundle : null;
        }
        catch { return null; }
    }

    [GeneratedRegex(@"<key>CFBundleIdentifier</key>\s*<string>([^<]*)</string>")]
    private static partial Regex IdentifierRx();

    /// <summary>A one-level binary property list (<c>bplist00</c>) of a dictionary whose values are strings or small
    /// non-negative integers, at most 14 entries: object 0 the dictionary, then the keys, then the values; two-byte
    /// offsets.</summary>
    private static byte[]? BinaryPlist(List<(string Key, object Value)> entries)
    {
        using var body = new MemoryStream();
        var offsets = new List<int>();
        body.Write("bplist00"u8);
        int n = entries.Count;

        offsets.Add((int)body.Position);
        body.WriteByte((byte)(0xD0 | n));
        for (int i = 0; i < n; i++) body.WriteByte((byte)(1 + i));          // key refs
        for (int i = 0; i < n; i++) body.WriteByte((byte)(1 + n + i));      // value refs

        foreach (var (key, _) in entries) { offsets.Add((int)body.Position); WriteString(body, key); }
        foreach (var (_, value) in entries)
        {
            offsets.Add((int)body.Position);
            if (value is string s) WriteString(body, s);
            else { body.WriteByte(0x11); WriteBe(body, (int)value, 2); }    // a two-byte integer
        }

        int tableAt = (int)body.Position;
        if (tableAt > 0xFFFF) return null;
        foreach (int o in offsets) WriteBe(body, o, 2);

        body.Write(new byte[6]);
        body.WriteByte(2);                                                   // offset size
        body.WriteByte(1);                                                   // object reference size
        WriteBe(body, offsets.Count, 8);
        WriteBe(body, 0, 8);                                                 // top object
        WriteBe(body, tableAt, 8);
        return body.ToArray();
    }

    private static void WriteString(Stream s, string text)
    {
        bool ascii = text.All(c => c < 0x80);
        byte[] bytes = ascii ? Encoding.ASCII.GetBytes(text) : Encoding.BigEndianUnicode.GetBytes(text);
        int count = text.Length;                                             // characters, for both forms
        int marker = ascii ? 0x50 : 0x60;
        if (count < 15) s.WriteByte((byte)(marker | count));
        else
        {
            s.WriteByte((byte)(marker | 0x0F));
            s.WriteByte(0x11);
            WriteBe(s, count, 2);
        }
        s.Write(bytes);
    }

    private static void WriteBe(Stream s, long value, int width)
    {
        for (int i = width - 1; i >= 0; i--) s.WriteByte((byte)(value >> (8 * i)));
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setxattr(string path, string name, byte[] value, nuint size, uint position, int options);
}

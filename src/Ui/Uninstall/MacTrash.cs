using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CircuitRF.Ui.Uninstall;

/// <summary>
/// Moves a file or bundle to the user's Trash through <c>-[NSFileManager trashItemAtURL:resultingItemURL:error:]</c>
/// (brief-em3d-25 R-em3d25-4a, macOS). The system call rather than a rename into <c>~/.Trash</c>: it picks
/// the right Trash for the volume, names a clash the way Finder does, and needs no access to the Trash
/// folder itself, which macOS protects.
/// </summary>
internal static class MacTrash
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";

    [DllImport(Objc, EntryPoint = "objc_getClass")]
    private static extern nint GetClass(string name);

    [DllImport(Objc, EntryPoint = "sel_registerName")]
    private static extern nint Sel(string name);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint sel);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern nint SendStr(nint receiver, nint sel, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendTrash(nint receiver, nint sel, nint url, out nint resulting, out nint error);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern nint SendPtr(nint receiver, nint sel, nint arg);

    /// <summary>Moves <paramref name="path"/> to the Trash. False, with the system's reason, when it could not.</summary>
    public static bool MoveToTrash(string path, out string? why)
    {
        why = null;
        if (!OperatingSystem.IsMacOS()) { why = "not macOS"; return false; }
        if (!Directory.Exists(path) && !File.Exists(path)) { why = $"{path} does not exist"; return false; }
        try
        {
            // Foundation is loaded in the application already; a test process may not have it yet.
            NativeLibrary.TryLoad("/System/Library/Frameworks/Foundation.framework/Foundation", out _);
            nint pool = Send(Send(GetClass("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
            try
            {
                nint str = SendStr(GetClass("NSString"), Sel("stringWithUTF8String:"), Path.GetFullPath(path));
                nint url = SendPtr(GetClass("NSURL"), Sel("fileURLWithPath:"), str);
                nint fm  = Send(GetClass("NSFileManager"), Sel("defaultManager"));
                if (SendTrash(fm, Sel("trashItemAtURL:resultingItemURL:error:"), url, out _, out nint error)) return true;
                why = error == 0 ? "the system gave no reason" : Utf8(Send(error, Sel("localizedDescription")));
                return false;
            }
            finally { Send(pool, Sel("drain")); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            why = e.Message;
            return false;
        }
    }

    private static string Utf8(nint nsString)
        => nsString == 0 ? "" : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String"))) ?? "";
}

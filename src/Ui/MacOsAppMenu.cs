using System;
using System.Runtime.InteropServices;

namespace CircuitRF.Ui;

/// <summary>
/// Names the macOS application menu's Quit item after the application — <c>Quit circuitRF</c>, the
/// spelling every other Mac application uses and the one Apple's own guidelines ask for.
///
/// <para><b>Why this is Objective-C and not XAML.</b> The item is not ours to declare. circuitRF's
/// <c>NativeMenu</c> in <c>App.axaml</c> holds only About and Settings; the Services / Hide / Show
/// All / <b>Quit</b> block below them is appended for us, and it arrives with the plain title
/// "Quit" while its neighbour in the same block reads "Hide circuitRF". Declaring a Quit item of
/// our own is what earlier attempts did, and it does not replace that one — it adds a SECOND, which
/// is the two-Quit menu the owner reported. So the item is renamed in place: one
/// <c>setTitle:</c> on the <c>NSMenuItem</c> that is already there, adding nothing and removing
/// nothing.</para>
///
/// <para><b>Identified by its action, not its title.</b> The item is found by
/// <c>terminate:</c> — the selector that makes it the Quit item — so a localized build, where the
/// title is not the English word, is renamed just the same and no OTHER item can be hit by
/// accident. The title match is a fallback for the case where the action is unset.</para>
///
/// <para><b>Idempotent, and it has to be.</b> The menu bar is re-exported whenever a window becomes
/// key, so this runs again on every activation; a rename that appended would spell "Quit circuitRF
/// circuitRF" by the third window switch. It returns without touching anything when the title is
/// already right, which also makes it cheap enough to call on that path.</para>
///
/// <para>Every call is a no-op off macOS, and the whole body is guarded: a cosmetic menu title is
/// never a reason for a launch to fail.</para>
/// </summary>
internal static class MacOsAppMenu
{
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
    private static extern nint GetClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern nint Sel(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint sel);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendPtr(nint receiver, nint sel, nint arg);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendIdx(nint receiver, nint sel, long arg);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern long SendLong(nint receiver, nint sel);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendStr(nint receiver, nint sel,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    /// <summary>
    /// Renames the application menu's Quit item to "Quit <paramref name="appName"/>". Safe to call
    /// at any time, from any of the three applications this assembly ships, and as often as wanted.
    /// </summary>
    internal static void NameQuitItem(string appName)
    {
        if (!OperatingSystem.IsMacOS()) return;

        try
        {
            nint app = Send(GetClass("NSApplication"), Sel("sharedApplication"));
            if (app == 0) return;

            nint mainMenu = Send(app, Sel("mainMenu"));
            if (mainMenu == 0 || SendLong(mainMenu, Sel("numberOfItems")) < 1) return;

            // Index 0 is the application menu — the one titled with the application's own name.
            nint appMenu = Send(SendIdx(mainMenu, Sel("itemAtIndex:"), 0), Sel("submenu"));
            if (appMenu == 0) return;

            nint terminate = Sel("terminate:");
            long n = SendLong(appMenu, Sel("numberOfItems"));
            string wanted = "Quit " + appName;

            for (long i = 0; i < n; i++)
            {
                nint item = SendIdx(appMenu, Sel("itemAtIndex:"), i);
                if (item == 0) continue;

                string? title = NsString(Send(item, Sel("title")));
                bool isQuit = Send(item, Sel("action")) == terminate
                              || string.Equals(title, "Quit", StringComparison.Ordinal);
                if (!isQuit) continue;

                if (string.Equals(title, wanted, StringComparison.Ordinal)) return;   // already named
                SendPtr(item, Sel("setTitle:"), NsFromString(wanted));
                return;
            }
        }
        catch { /* a menu title is never worth a crash */ }
    }

    private static nint NsFromString(string s) =>
        SendStr(GetClass("NSString"), Sel("stringWithUTF8String:"), s);

    private static string? NsString(nint ns)
    {
        if (ns == 0) return null;
        nint utf8 = Send(ns, Sel("UTF8String"));
        return utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
    }
}

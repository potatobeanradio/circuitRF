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

    // BOOL is a signed char, so only the low byte of the return register is defined — marshalled as
    // I1 rather than read out of a long, which would be reading bits the ABI does not set.
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBool(nint receiver, nint sel);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendStr(nint receiver, nint sel,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendPtrIdx(nint receiver, nint sel, nint arg, long index);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_retain")]
    private static extern nint Retain(nint obj);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_release")]
    private static extern void Release(nint obj);

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


    /// <summary>
    /// The application-only menu AppKit was last seen showing — Avalonia's own
    /// <c>showAppMenuOnly</c> menu, captured rather than constructed, and retained because the
    /// application drops its reference the moment a window menu goes back on.
    /// <see cref="RedrawMenuBar"/> swaps through it. Never released: there is exactly one for the
    /// life of the process.
    /// </summary>
    private static nint _appOnlyMenu;

    /// <summary>
    /// Makes macOS re-draw the menu bar it is already holding. Call after the menu bar has been
    /// re-installed; on a one-item bar it records that menu and returns.
    ///
    /// <para><b>The bar can be right and still be drawn wrong.</b> Owner-reported on macOS 27
    /// (2026-09-15) and captured with <c>CRF_MENU_DIAG</c>: closing the About dialog with the
    /// window's own ✕ leaves only "circuitRF" on the bar until the user switches away and back,
    /// while <c>NSApp.mainMenu</c> reads all nine items — File, Edit, Design, Simulate, Tools, View,
    /// Window, Help — one 500 ms sample after the dialog closed and for the whole eight seconds the
    /// user sat looking at a bar that did not have them. Closing the SAME dialog with its OK button,
    /// in the same session a minute later, drew correctly. So nothing is missing from the menu;
    /// AppKit did not repaint. Avalonia has the same shape open against file pickers since May 2025
    /// (AvaloniaUI/Avalonia#18780) and 12.1's native menu code is unchanged here, so there is no
    /// upstream fix to take.</para>
    ///
    /// <para><b>Two attempts, and the first one taught the rule.</b> Re-inserting the
    /// application-menu item and setting the SAME menu back changed nothing on screen:
    /// <c>setMainMenu:</c> is an ordinary property setter and returns without doing anything when
    /// handed the menu it already holds, so a mutation of the menu's CONTENTS never reaches the
    /// bar. What repaints is a different menu OBJECT — which is exactly what app-switching does,
    /// because <c>showAppMenuOnly</c> and <c>showWindowMenuWithAppMenu</c> swap between two menus
    /// (read from <c>libAvaloniaNative.dylib</c>'s disassembly). So this swaps through the real
    /// application-only menu, the one AppKit was last showing, kept from the last time this was
    /// called over a bare bar. A window menu is always preceded by a bare bar — the dialog that
    /// caused the trouble put one there — so by the time it is wanted it has been seen. A menu of
    /// our own stands in only if it somehow has not.</para>
    ///
    /// <para><b>Both sets happen in one run-loop pass</b>, before anything is drawn, so there is no
    /// intermediate bar to see.</para>
    ///
    /// <para><b>It refuses to repaint a one-item bar</b>, and says so by returning false. That is
    /// the CORRECT state whenever no window with a menu is key — a dialog is up, or the application
    /// is in the background — and forcing it is how a display fault becomes a flicker on every
    /// activation. The caller uses the answer to decide whether to try again later.</para>
    ///
    /// <para><b>Call this SYNCHRONOUSLY from the activation handler.</b> It was posted at Background
    /// priority at first and the owner could see the result: the bar stayed bare for something like
    /// half a second after the dialog closed, and again on some switches back into the application,
    /// before the menus appeared. Nothing needs to happen before this runs —
    /// <c>-[AvnWindow becomeKeyWindow]</c> installs the window's menu synchronously and
    /// <c>windowDidBecomeKey:</c> then calls into managed code synchronously too (both read from the
    /// disassembly), so by the time <c>Activated</c> is raised the menu AppKit failed to draw is
    /// already on <c>NSApp</c>. Running here puts the repair in the same run-loop pass as the install
    /// that needed it, which is before anything is drawn at all.</para>
    ///
    /// <para><b>But NOT on every activation — see <see cref="MenuBarRepairGate"/>.</b> Swapping the
    /// menu is not free: doing it on the way back from another application eats the menu-bar click
    /// that caused the activation, because clicking the bar of a background application activates it
    /// and opens the menu in one gesture and this replaces the bar in between (owner, 2026-09-21:
    /// the menu would not pull down, and clicking away and back cleared it). That path never needed
    /// the repair anyway — macOS draws the bar correctly when the application itself is reactivated,
    /// which is why switching away and back was the workaround for the fault this fixes. So the
    /// caller runs it only after an in-application focus change.</para>
    ///
    /// <para><b><c>CRF_MENU_FIX=2</c> picks the other candidate</b>: toggling
    /// <c>+[NSMenu setMenuBarVisible:]</c> off and on, which asks AppKit to tear the bar down and
    /// build it again rather than asking it to notice a new menu. It is second because it acts on
    /// the whole bar, including the parts that are not ours, and the restore is in a
    /// <c>finally</c> for that reason. Unset, the swap above is what runs.</para>
    /// </summary>
    internal static bool RedrawMenuBar()
    {
        if (!OperatingSystem.IsMacOS()) return true;

        try
        {
            nint app = Send(GetClass("NSApplication"), Sel("sharedApplication"));
            if (app == 0) return false;

            nint mainMenu = Send(app, Sel("mainMenu"));
            long n = mainMenu == 0 ? 0 : SendLong(mainMenu, Sel("numberOfItems"));
            if (n < 2)
            {
                Diagnostics.MenuBarProbe.Note($"RedrawMenuBar: nothing to repaint yet, mainMenu[{n}]");
                return false;
            }

            if (Environment.GetEnvironmentVariable("CRF_MENU_FIX") == "2") { ToggleMenuBar(); return true; }

            // Held across the swap: the application's own reference goes the moment the other menu is
            // installed, and this is the menu every window on screen is sharing.
            nint held = Retain(mainMenu);
            nint stand = 0;
            try
            {
                nint other = _appOnlyMenu;
                if (other == 0 || other == mainMenu)
                    other = stand = Send(Send(GetClass("NSMenu"), Sel("alloc")), Sel("init"));
                if (other == 0) return false;

                SendPtr(app, Sel("setMainMenu:"), other);
                SendPtr(app, Sel("setMainMenu:"), held);
                Diagnostics.MenuBarProbe.Note(
                    $"RedrawMenuBar: swapped via {(stand != 0 ? "a stand-in" : "the app-only menu")}, " +
                    $"mainMenu[{SendLong(Send(app, Sel("mainMenu")), Sel("numberOfItems"))}]");
                return true;
            }
            finally
            {
                if (stand != 0) Release(stand);
                Release(held);
            }
        }
        catch (Exception e) { Diagnostics.MenuBarProbe.Note("RedrawMenuBar: threw " + e.Message); }
        return false;
    }

    /// <summary>
    /// <c>[NSApp isActive]</c> — whether circuitRF is the frontmost application right now.
    ///
    /// <para>Read from the shell window's <c>Deactivated</c> handler, one dispatcher pass later, it
    /// is what separates the two ways a window stops being active: a dialog of ours taking focus
    /// leaves the application active, and another application taking focus does not. Only the first
    /// needs <see cref="RedrawMenuBar"/> — see <see cref="MenuBarRepairGate"/> for why running it on
    /// the second costs the user a menu click. True off macOS, where neither caller does anything.</para>
    /// </summary>
    internal static bool ApplicationIsActive()
    {
        if (!OperatingSystem.IsMacOS()) return true;

        try
        {
            nint app = Send(GetClass("NSApplication"), Sel("sharedApplication"));
            return app != 0 && SendBool(app, Sel("isActive"));
        }
        catch { return true; }
    }

    /// <summary>
    /// Records the application-only menu while it is on the bar, for <see cref="RedrawMenuBar"/> to
    /// swap through later. Call when a window has just STOPPED being active — that is when the bare
    /// bar is up and the menu can be had.
    ///
    /// <para><b>The capture has to happen here because the repair cannot make its own.</b> By the
    /// time the bar needs repainting a window menu is on it, and the application-only menu is
    /// Avalonia's, built in native code and reachable through no managed API. The one moment it is
    /// observable is while it is installed — which is precisely the moment the dialog that causes
    /// the trouble creates, so it is always seen before it is wanted.</para>
    /// </summary>
    internal static void RememberBareMenuBar()
    {
        if (!OperatingSystem.IsMacOS()) return;

        try
        {
            nint app = Send(GetClass("NSApplication"), Sel("sharedApplication"));
            if (app == 0) return;

            nint mainMenu = Send(app, Sel("mainMenu"));
            if (mainMenu == 0 || SendLong(mainMenu, Sel("numberOfItems")) > 1) return;   // not the bare bar
            if (mainMenu == _appOnlyMenu) return;                                        // already have it

            _appOnlyMenu = Retain(mainMenu);
            Diagnostics.MenuBarProbe.Note("RememberBareMenuBar: captured the application-only menu");
        }
        catch { /* the repair falls back to a stand-in menu */ }
    }

    /// <summary>
    /// Hides the menu bar and shows it again, which makes AppKit build it afresh. The restore is in a
    /// <c>finally</c> because this one is not confined to circuitRF's own menus: leaving it hidden
    /// would take the bar away from everything until the user switched applications.
    /// </summary>
    private static void ToggleMenuBar()
    {
        nint nsMenu = GetClass("NSMenu");
        try { SendPtr(nsMenu, Sel("setMenuBarVisible:"), 0); }
        finally { SendPtr(nsMenu, Sel("setMenuBarVisible:"), 1); }
        Diagnostics.MenuBarProbe.Note("RedrawMenuBar: menu bar hidden and shown again (CRF_MENU_FIX=2)");
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

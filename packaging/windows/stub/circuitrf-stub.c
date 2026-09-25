/*
 * ── circuitRF per-user launcher stub ─────────────────────────────────────────────────────────
 *
 * The one file in %LOCALAPPDATA%\Programs\circuitRF\ that NEVER changes. Shortcuts, the Start Menu
 * entry and every file association point here, so an update re-registers nothing.
 *
 * It reads `current` -- one line naming a directory -- and starts <that directory>\circuitRF.exe,
 * forwarding its own command line and returning the child's exit code.
 *
 *     %LOCALAPPDATA%\Programs\circuitRF\
 *         circuitRF.exe        <- THIS
 *         current              <- "app-1.0.0-beta.2"
 *         app-1.0.0-beta.1\    <- the previous version, kept as rollback insurance
 *         app-1.0.0-beta.2\    <- circuitRF.exe and the rest of the publish tree
 *         staging\
 *
 * WHY A STUB AT ALL. You cannot delete or overwrite a running .exe or a loaded .dll on Windows --
 * but you never need to, because the new version goes into a NEW directory and the update is a
 * pointer flip. That is the Squirrel / VS Code model, and it is what makes an update a rename
 * rather than an overwrite of a file that is currently in use.
 *
 * WHY IT IS NOT A .NET PROGRAM. It is choosing between directories that each contain their own
 * self-contained .NET runtime; a managed stub would need a runtime of its own, in the directory it
 * is choosing between. It is also on the launch path of every start, so it has to be instant.
 * tools/senior-worker already builds a Windows launcher stub in C for the same class of reason --
 * this follows it rather than inventing a second pattern.
 *
 * WHY IT WAITS instead of exiting immediately: the parent is what a shortcut, a shell "open with"
 * and a debugger all attach to, and an exit code that is always 0 hides every startup failure.
 *
 * Built by build-stub.ps1 (Windows) or build-stub.sh (anywhere, with zig).
 *
 * -- THE SAME SOURCE, BUILT A SECOND TIME, IS circuitRF.com ------------------------------------
 *
 * The circuitRF executable is also the command line (brief-automation-13-installed-cli.md): its
 * Program.Main hands `circuitRF check .` or `circuitRF serve --root <dir>` to the CLI before any
 * of the GUI starts. Two routes reach it on Windows, and this file serves both:
 *
 *   1. A PROGRAM that spawns circuitRF.exe with redirected pipes - every MCP client, every agent's
 *      shell tool. The stub hands the child its own standard handles (STARTF_USESTDHANDLES), so a
 *      GUI-subsystem child writes into the caller's pipes, and it never shows a dialog when there is
 *      a pipe or a file to write the reason to: a modal box on a headless agent or CI box waits for
 *      a click nobody will make.
 *
 *   2. A PERSON typing `circuitrf check .` in cmd or PowerShell. A GUI-subsystem executable gets no
 *      console, and neither shell waits for it - the prompt returns at once and the output is lost.
 *      So this file is compiled again with -DCRF_CONSOLE and the CONSOLE subsystem, and installed as
 *      circuitRF.com beside circuitRF.exe. PATHEXT lists .COM before .EXE, so a TYPED `circuitrf`
 *      resolves to it, while shortcuts, file associations and CreateProcess("circuitrf") - which
 *      appends .exe - still reach the .exe. The console build never shows a dialog, and when there
 *      is no `current` file (a per-machine install, where circuitRF.exe beside it IS the
 *      application) it starts that .exe instead.
 */

#ifdef _WIN32

#include <windows.h>
#include <stdio.h>
#include <string.h>

/*
 * ONE stub source, three applications. The build scripts pass -DCRF_APP_NAME=circuitRF (or
 * harmonicaRF, or wBond), for the same reason src/Ui/CircuitRF.Ui.csproj derives everything from
 * CrfApp: three applications are the same code with a different name, and a second copy of this
 * file would be a second place to fix a bug in it.
 *
 * THE NAME ARRIVES AS A BARE TOKEN AND IS STRINGIFIED HERE, and that is deliberate rather than
 * tidy. It used to arrive already quoted, which meant every build script had to get a literal "
 * through PowerShell's native-argument handling intact - and one of them did not. Windows
 * PowerShell 5.1 strips a bare " when it builds a native command line, so zig cc received
 * -DCRF_APP_NAME=circuitRF, L##circuitRF pasted into the undeclared identifier LcircuitRF, and the
 * build failed at the first architecture (owner-reported, 2026-08-25). The cl.exe branch escaped
 * it as \" and the zig branch did not, which is exactly the kind of disagreement that survives
 * review. A bare token has nothing to escape, so the class of bug is gone rather than fixed.
 *
 * All three application names are valid C identifiers, which is what makes this work.
 */
#ifndef CRF_APP_NAME
#define CRF_APP_NAME circuitRF
#endif
#define CRF_STR_(x)   #x
#define CRF_STR(x)    CRF_STR_(x)
#define CRF_WIDEN_(x) L##x
#define CRF_WIDEN(x)  CRF_WIDEN_(x)

#define CRF_APP_TITLE CRF_WIDEN(CRF_STR(CRF_APP_NAME))
#define CRF_APP_EXE   CRF_APP_TITLE L".exe"
#define CRF_POINTER   L"current"
#define CRF_MAX       32768

/* The directory this executable lives in, with a trailing backslash. */
static int stub_directory(wchar_t *out, DWORD count)
{
    DWORD n = GetModuleFileNameW(NULL, out, count);
    if (n == 0 || n >= count) return 0;

    wchar_t *slash = wcsrchr(out, L'\\');
    if (!slash) return 0;
    *(slash + 1) = L'\0';
    return 1;
}

/*
 * Reads `current`. Deliberately tolerant of trailing whitespace and of a UTF-8 BOM, and
 * deliberately INTOLERANT of anything that is not a plain relative directory name: a pointer
 * holding a path separator or a drive letter is not something this stub wrote, and following it
 * would turn a corrupt file into an arbitrary program launch.
 */
static int read_pointer(const wchar_t *dir, wchar_t *out, DWORD count)
{
    wchar_t path[CRF_MAX];
    _snwprintf(path, CRF_MAX, L"%s%s", dir, CRF_POINTER);
    path[CRF_MAX - 1] = L'\0';

    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return 0;

    char bytes[512] = {0};
    DWORD got = 0;
    BOOL ok = ReadFile(h, bytes, sizeof(bytes) - 1, &got, NULL);
    CloseHandle(h);
    if (!ok || got == 0) return 0;

    char *start = bytes;
    if (got >= 3 && (unsigned char)start[0] == 0xEF &&
                    (unsigned char)start[1] == 0xBB &&
                    (unsigned char)start[2] == 0xBF) start += 3;

    for (char *p = start; *p; p++)
        if (*p == '\r' || *p == '\n') { *p = '\0'; break; }

    size_t len = strlen(start);
    while (len > 0 && (start[len - 1] == ' ' || start[len - 1] == '\t')) start[--len] = '\0';
    if (len == 0) return 0;

    for (size_t i = 0; i < len; i++)
        if (start[i] == '\\' || start[i] == '/' || start[i] == ':') return 0;

    /* Separators are already gone, so `..` can only be the WHOLE name -- and it names the directory
     * ABOVE the install, which is not a version and is not something this stub ever wrote. The
     * comment above claimed this was rejected; it was not (security review, 2026-08-25). */
    if (strcmp(start, ".") == 0 || strcmp(start, "..") == 0) return 0;

    return MultiByteToWideChar(CP_UTF8, 0, start, -1, out, (int)count) > 0;
}

/*
 * The child's command line: our own, with argv[0] replaced by the resolved executable so the app
 * sees a real path for itself. GetCommandLineW is used rather than a rebuilt argv because
 * re-quoting an argument list is a well-known way to lose a path with a space in it.
 */
static const wchar_t *arguments_after_argv0(const wchar_t *cmdline)
{
    const wchar_t *p = cmdline;

    if (*p == L'"')
    {
        p++;
        while (*p && *p != L'"') p++;
        if (*p == L'"') p++;
    }
    else
    {
        while (*p && *p != L' ' && *p != L'\t') p++;
    }

    while (*p == L' ' || *p == L'\t') p++;
    return p;
}

/*
 * Is there somewhere OTHER than a dialog to say why this failed? True when standard output is a
 * pipe, a file or a console - i.e. a program or a shell started us, not a shortcut or a
 * double-click. The GUI build then writes the reason to stderr and never raises a MessageBox: a
 * modal dialog on a headless agent or CI box hangs until someone clicks it, and nobody will.
 */
static int has_standard_output(void)
{
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    if (h == NULL || h == INVALID_HANDLE_VALUE) return 0;
    DWORD type = GetFileType(h);
    return type == FILE_TYPE_PIPE || type == FILE_TYPE_DISK || type == FILE_TYPE_CHAR;
}

/*
 * The reason, as UTF-8 on the stderr HANDLE - written with WriteFile rather than through the C
 * runtime's stderr, whose initialisation in a GUI-subsystem program is not something to rely on.
 */
static void write_stderr(const wchar_t *text)
{
    HANDLE h = GetStdHandle(STD_ERROR_HANDLE);
    if (h == NULL || h == INVALID_HANDLE_VALUE) return;

    char bytes[CRF_MAX];
    int n = WideCharToMultiByte(CP_UTF8, 0, text, -1, bytes, (int)sizeof(bytes) - 2, NULL, NULL);
    if (n <= 1) return;
    bytes[n - 1] = '\n';                 /* replaces the terminator counted in n */
    DWORD written = 0;
    WriteFile(h, bytes, (DWORD)n, &written, NULL);
}

static void report(const wchar_t *what, const wchar_t *detail)
{
    wchar_t msg[CRF_MAX];
    _snwprintf(msg, CRF_MAX,
               CRF_APP_TITLE L" could not start.\n\n%s\n%s\n\nReinstalling "
               CRF_APP_TITLE L" will repair this.", what, detail ? detail : L"");
    msg[CRF_MAX - 1] = L'\0';
    write_stderr(msg);
#ifndef CRF_CONSOLE
    if (!has_standard_output())
        MessageBoxW(NULL, msg, CRF_APP_TITLE, MB_ICONERROR | MB_OK);
#endif
}

/*
 * Hands the child OUR standard handles. Without STARTF_USESTDHANDLES a GUI-subsystem child of a
 * process that was given pipes writes nowhere: inheriting handles (bInheritHandles) makes them
 * VALID in the child, but only this flag makes them its stdin/stdout/stderr. Skipped when we have
 * none at all (a shortcut launch), so that path behaves exactly as it always has.
 */
static void pass_standard_handles(STARTUPINFOW *si)
{
    HANDLE in  = GetStdHandle(STD_INPUT_HANDLE);
    HANDLE out = GetStdHandle(STD_OUTPUT_HANDLE);
    HANDLE err = GetStdHandle(STD_ERROR_HANDLE);

    int any = 0;
    HANDLE all[3] = { in, out, err };
    for (int i = 0; i < 3; i++)
    {
        if (all[i] == NULL || all[i] == INVALID_HANDLE_VALUE) continue;
        any = 1;
        /* A pipe we inherited is already inheritable; a console handle may not be. Best effort -
         * a handle that refuses keeps whatever inheritance it had. */
        SetHandleInformation(all[i], HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT);
    }
    if (!any) return;

    si->dwFlags   |= STARTF_USESTDHANDLES;
    si->hStdInput  = in;
    si->hStdOutput = out;
    si->hStdError  = err;
}

static int run(void)
{
    wchar_t dir[CRF_MAX];
    if (!stub_directory(dir, CRF_MAX))
    {
        report(L"Its own location could not be determined.", NULL);
        return 1;
    }

    wchar_t exe[CRF_MAX];
    wchar_t version[512];
    if (read_pointer(dir, version, 512))
    {
        _snwprintf(exe, CRF_MAX, L"%s%s\\%s", dir, version, CRF_APP_EXE);
    }
    else
    {
#ifdef CRF_CONSOLE
        /* No `current`: a per-machine install, where the application itself sits beside this .com
         * (circuitRF.wxs installs publish\circuitRF.exe straight into Program Files). Never this
         * stub's own name - a .com cannot start itself, because it IS the .com. In a per-user
         * install `current` exists, and if it has been removed the .exe beside us is the GUI
         * stub, which reports that properly. */
        _snwprintf(exe, CRF_MAX, L"%s%s", dir, CRF_APP_EXE);
#else
        /* `current` is written by rename and never by truncation, precisely so this cannot happen
         * from a full disk (design 13.2). Reaching here means the file was removed or replaced by
         * something else. */
        report(L"The 'current' file naming the version to run is missing or unreadable.", dir);
        return 1;
#endif
    }
    exe[CRF_MAX - 1] = L'\0';

    if (GetFileAttributesW(exe) == INVALID_FILE_ATTRIBUTES)
    {
        report(L"The version 'current' names is not installed.", exe);
        return 1;
    }

    wchar_t cmdline[CRF_MAX];
    _snwprintf(cmdline, CRF_MAX, L"\"%s\" %s", exe, arguments_after_argv0(GetCommandLineW()));
    cmdline[CRF_MAX - 1] = L'\0';

    STARTUPINFOW si = { sizeof(si) };
    PROCESS_INFORMATION pi = {0};
    pass_standard_handles(&si);

#ifdef CRF_CONSOLE
    /* Ctrl+C in the console reaches THIS process and not the child, which is GUI-subsystem and has
     * no console to receive it. Without this, Ctrl+C ends the .com and leaves the child solving in
     * the background, still printing into the console. A kill-on-close job ties the child's life
     * to ours; SILENT_BREAKAWAY_OK keeps the tie to the DIRECT child only, so something IT starts -
     * a device worker, or the successor of the GUI's own Relaunch - is never killed along with it.
     * Best effort: if the job cannot be made, the child simply runs as it would have. */
    HANDLE job = CreateJobObjectW(NULL, NULL);
    if (job)
    {
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits;
        ZeroMemory(&limits, sizeof(limits));
        limits.BasicLimitInformation.LimitFlags =
            JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, sizeof(limits)))
        {
            CloseHandle(job);
            job = NULL;
        }
    }
    DWORD flags = job ? CREATE_SUSPENDED : 0;
#else
    DWORD flags = 0;
#endif

    if (!CreateProcessW(exe, cmdline, NULL, NULL, TRUE, flags, NULL, NULL, &si, &pi))
    {
        wchar_t why[64];
        _snwprintf(why, 64, L"Windows error %lu.", (unsigned long)GetLastError());
        report(why, exe);
        return 1;
    }

#ifdef CRF_CONSOLE
    if (job)
    {
        AssignProcessToJobObject(job, pi.hProcess);   /* best effort; see above */
        ResumeThread(pi.hThread);
    }
#endif

    /* Wait, so that the exit code is the application's and a shortcut, a shell "open with" and a
     * debugger all attach to something that outlives the launch - and, for the .com, so the shell
     * does not print its prompt over the output. */
    WaitForSingleObject(pi.hProcess, INFINITE);

    DWORD code = 1;
    GetExitCodeProcess(pi.hProcess, &code);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return (int)code;
}

#ifdef CRF_CONSOLE
int wmain(int argc, wchar_t **argv)
{
    (void)argc; (void)argv;
    return run();
}
#else
int APIENTRY wWinMain(HINSTANCE inst, HINSTANCE prev, PWSTR args, int show)
{
    (void)inst; (void)prev; (void)args; (void)show;
    return run();
}
#endif

#else
#error "The circuitRF launcher stub is a Windows-only program."
#endif

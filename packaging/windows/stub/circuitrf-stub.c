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

static void report(const wchar_t *what, const wchar_t *detail)
{
    wchar_t msg[CRF_MAX];
    _snwprintf(msg, CRF_MAX,
               CRF_APP_TITLE L" could not start.\n\n%s\n%s\n\nReinstalling "
               CRF_APP_TITLE L" will repair this.", what, detail ? detail : L"");
    msg[CRF_MAX - 1] = L'\0';
    fwprintf(stderr, L"%s\n", msg);
    MessageBoxW(NULL, msg, CRF_APP_TITLE, MB_ICONERROR | MB_OK);
}

int APIENTRY wWinMain(HINSTANCE inst, HINSTANCE prev, PWSTR args, int show)
{
    (void)inst; (void)prev; (void)args; (void)show;

    wchar_t dir[CRF_MAX];
    if (!stub_directory(dir, CRF_MAX))
    {
        report(L"Its own location could not be determined.", NULL);
        return 1;
    }

    wchar_t version[512];
    if (!read_pointer(dir, version, 512))
    {
        /* `current` is written by rename and never by truncation, precisely so this cannot happen
         * from a full disk (design 13.2). Reaching here means the file was removed or replaced by
         * something else. */
        report(L"The 'current' file naming the version to run is missing or unreadable.", dir);
        return 1;
    }

    wchar_t exe[CRF_MAX];
    _snwprintf(exe, CRF_MAX, L"%s%s\\%s", dir, version, CRF_APP_EXE);
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

    if (!CreateProcessW(exe, cmdline, NULL, NULL, TRUE, 0, NULL, NULL, &si, &pi))
    {
        wchar_t why[64];
        _snwprintf(why, 64, L"Windows error %lu.", (unsigned long)GetLastError());
        report(why, exe);
        return 1;
    }

    /* Wait, so that the exit code is the application's and a shortcut, a shell "open with" and a
     * debugger all attach to something that outlives the launch. */
    WaitForSingleObject(pi.hProcess, INFINITE);

    DWORD code = 1;
    GetExitCodeProcess(pi.hProcess, &code);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return (int)code;
}

#else
#error "The circuitRF launcher stub is a Windows-only program."
#endif

<#
  == Build the circuitRF per-user launcher stub (Windows) ======================

    .\packaging\windows\stub\build-stub.ps1 -Arch x64

  Writes build\<AppName>-stub-<Arch>.exe. Called by build-windows.ps1 when -Scope perUser.

  Two toolchains are tried, in order: zig cc (one download, no daemon, cross-compiles every
  architecture from any host) and then the MSVC cl.exe on PATH inside a Developer prompt. The
  stub is ~150 lines of plain Win32, so either produces the same thing.

  ------------------------------------------------------------------------------
  THIS FILE MUST STAY PURE ASCII - see the note at the top of build-windows.ps1. Windows PowerShell
  5.1 reads a BOM-less .ps1 as cp1252, and a UTF-8 emoji decodes to the curly-quote bytes
  0x93/0x94, which PowerShell honours as string delimiters. Nothing errors; the block is PRINTED
  instead of run. tests/Ui.Tests/PackagingScriptTests.cs holds this shut.
  ------------------------------------------------------------------------------
#>

[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64', 'x86')]
    [string]$Arch = 'x64',

    [string]$AppName = 'circuitRF'
)

$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'circuitrf-stub.c'
$out = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$exe = Join-Path $out "$AppName-stub-$Arch.exe"

# Removed before anything runs: every guard below is `-not (Test-Path $exe)`, so a stub left by an
# earlier run would skip both compilers and be packaged as though it had just been built.
if (Test-Path $exe) { Remove-Item $exe -Force }

$zigTarget = @{ 'x64' = 'x86_64-windows-gnu'; 'arm64' = 'aarch64-windows-gnu'; 'x86' = 'x86-windows-gnu' }[$Arch]

# NO QUOTES AROUND THE APP NAME, on either branch, and that is the fix rather than the shortcut.
# Windows PowerShell 5.1 strips a bare " when it builds a native command line, so the zig branch's
# -DCRF_APP_NAME="circuitRF" arrived as -DCRF_APP_NAME=circuitRF, L##circuitRF pasted into the
# undeclared identifier LcircuitRF, and the build failed at the first architecture
# (owner-reported, 2026-08-25). The cl.exe branch escaped it as \" and this one did not. The stub
# now takes a BARE TOKEN and stringifies it itself, so there is nothing here to escape and nothing
# for a future branch to get wrong. See the note in circuitrf-stub.c.

# The compiler's own output is CAPTURED and re-thrown with the failure. Without it a broken build
# says only "zig cc failed", which is a sentence with no information in it - and this script runs on
# whichever machine cuts the Windows release, where a round trip to diagnose is expensive.

# BOTH toolchains are TRIED, not just the first one present. zig failing is not the same thing as
# zig being absent, and treating them the same is what turned a crashing zig into "no per-user
# channel" on a machine that also had MSVC (owner-reported, 2026-08-25: zig cc exited with
# -1073741819, an access violation - zig itself fell over, having reported nothing).

$tried = @()

if (-not (Test-Path $exe) -and (Get-Command zig -ErrorAction SilentlyContinue)) {
    Write-Host "Building the launcher stub with zig cc ($zigTarget) ..."
    #
    # -Wl,--subsystem,windows and NOT -mwindows. Both are meant to ask for the GUI subsystem so that
    # launching circuitRF opens no console window - and with zig cc, -mwindows silently does not.
    # Measured on zig 0.13.0 by reading the subsystem field back out of the built PE:
    #
    #     -mwindows                          -> 3 (CONSOLE)   wrong, and no warning
    #     -Wl,--subsystem,windows            -> 2 (GUI)       correct
    #     both together                      -> 3 (CONSOLE)   -mwindows wins and undoes it
    #
    # The stub exists to be invisible; a console window flashing up on every launch of every one of
    # the three applications is about as visible as a defect gets. It was CUI until this was
    # measured, which is why the check below now reads the field rather than trusting the flag.
    #
    # -municode: wWinMain is the Unicode entry point; without it the mingw CRT looks for WinMain.
    # The linker flag is QUOTED: unquoted, PowerShell reads the commas as an array separator and
    # the script does not even parse.
    $log = & zig cc -target $zigTarget -O2 -municode '-Wl,--subsystem,windows' "-DCRF_APP_NAME=$AppName" `
                    $src -o $exe -luser32 2>&1
    if ($LASTEXITCODE -ne 0) {
        $log | ForEach-Object { Write-Host "  $_" }
        # -1073741819 is 0xC0000005, an access violation: zig crashed rather than refusing the code.
        $why = if ($LASTEXITCODE -eq -1073741819) { 'zig crashed' }
               else { "zig failed (exit $LASTEXITCODE)" }
        $tried += $why
        if (Test-Path $exe) { Remove-Item $exe -Force -ErrorAction SilentlyContinue }
    }
}

if (-not (Test-Path $exe) -and (Get-Command cl -ErrorAction SilentlyContinue)) {
    Write-Host 'Building the launcher stub with cl.exe ...'
    Push-Location $out
    try {
        $log = & cl /nologo /O2 /W3 /DUNICODE /D_UNICODE "/DCRF_APP_NAME=$AppName" $src `
                    /link /SUBSYSTEM:WINDOWS /ENTRY:wWinMainCRTStartup user32.lib "/OUT:$exe" 2>&1
        if ($LASTEXITCODE -ne 0) {
            $log | ForEach-Object { Write-Host "  $_" }
            $tried += "cl.exe failed (exit $LASTEXITCODE)"
            if (Test-Path $exe) { Remove-Item $exe -Force -ErrorAction SilentlyContinue }
        }
    }
    finally { Pop-Location }
}

# WHAT WAS ACTUALLY BUILT, read out of the PE rather than assumed from the flags. Two things this
# catches, both of which ship silently otherwise:
#
#   * THE SUBSYSTEM. -mwindows was a no-op under zig cc and produced a console stub (above).
#   * THE ARCHITECTURE. The cl.exe branch has no -Arch switch of its own - it builds for whatever
#     the Developer PowerShell it was started from targets. On an x64 prompt that yields an x64
#     binary named circuitRF-stub-arm64.exe, which the arm64 .msi would then ship. Nothing about
#     that fails until an ARM user double-clicks the shortcut.
#
# Same discipline as build-macos.sh reading architecture back with lipo, and build-linux.sh reading
# the ELF header of the device worker: never trust a toolchain to have done what it was asked.

function Test-StubBinary($path, $wantArch) {
    $want = @{ 'x64' = 0x8664; 'arm64' = 0xAA64; 'x86' = 0x014C }[$wantArch]
    $bytes = [System.IO.File]::ReadAllBytes($path)
    if ($bytes.Length -lt 512) { return "the file is $($bytes.Length) bytes; that is not a PE" }

    $pe = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($pe -le 0 -or $pe + 92 -ge $bytes.Length) { return 'the PE header offset is out of range' }
    if ($bytes[$pe] -ne 0x50 -or $bytes[$pe + 1] -ne 0x45) { return 'no PE signature' }

    $machine   = [BitConverter]::ToUInt16($bytes, $pe + 4)
    $subsystem = [BitConverter]::ToUInt16($bytes, $pe + 24 + 68)

    if ($machine -ne $want) {
        return ("it is machine 0x{0:X4}, not the 0x{1:X4} a {2} install needs" -f $machine, $want, $wantArch)
    }
    if ($subsystem -ne 2) {
        return "its subsystem is $subsystem (2 is GUI); a console window would open on every launch"
    }
    return $null
}

if (Test-Path $exe) {
    $wrong = Test-StubBinary $exe $Arch
    if ($wrong) {
        Remove-Item $exe -Force -ErrorAction SilentlyContinue
        throw "The launcher stub was built but is not usable: $wrong."
    }
    Write-Host "OK  $exe"
    return
}

if ($tried.Count -gt 0) {
    throw ($tried -join '; ')
}

throw @'
No C compiler was found for the launcher stub.

Install one of these and run this script again:

    zig      (winget install zig.zig)   - one download, no daemon; the preferred route
    MSVC     (a Visual Studio Developer PowerShell, so cl.exe is on PATH)

The stub is the one file in a per-user install that never changes: shortcuts and file
associations point at it, and it starts the version named in `current`. Without it there is no
per-user install channel and therefore no automatic updates on Windows.
'@

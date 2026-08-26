<#
  == Build the circuitRF per-user launcher stub (Windows) ======================

    .\packaging\windows\stub\build-stub.ps1 -Arch x64

  Writes build\<AppName>-stub-<Arch>.exe. Called by build-windows.ps1 when -Scope perUser.

  The stub is ~150 lines of plain Win32, so any C compiler produces the same thing. Several
  routes are tried in order and the FIRST ONE THAT PRODUCES A USABLE BINARY wins:

    1. zig cc, -mcpu=baseline            cross-compiles every architecture from any host
    2. zig cc, -mcpu=baseline -O0        same, with the optimiser out of the picture
    3. zig cc, native CPU                what this script used to do, kept as a last resort
    4. cl.exe already on PATH            a Developer PowerShell
    5. cl.exe found via vswhere          any plain PowerShell on a machine with VS installed

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


# == Running a compiler without PowerShell deciding it failed ==================
#
# A WARNING ON STDERR MUST NOT FAIL THE BUILD, and making sure of that needs its own function.
#
# `$log = & zig cc ... 2>&1` looks like plain output capture and is not. Under Windows PowerShell
# 5.1, merging a NATIVE command's stderr into the success stream while $ErrorActionPreference is
# 'Stop' turns the first stderr line into a TERMINATING error: PowerShell wraps it in a
# NativeCommandError, throws it, and $LASTEXITCODE is never reached. The compiler's exit code is
# irrelevant - it can be 0.
#
# That is not hypothetical. It cost the x86 stub of a whole release (owner-reported, 2026-08-25):
# zig printed one harmless line -
#
#     '-macrofusio' is not a recognized feature for this target (ignoring feature)
#
# - which is an LLVM warning that says, in its own text, that it is ignoring the feature and
# carrying on. The compile succeeded. PowerShell threw anyway, build-windows.ps1 caught it, and the
# release shipped without the x86 per-user installer or its update payload. The clue that it was
# never a compiler failure: that line is the caught EXCEPTION MESSAGE, not something this script
# printed - a NativeCommandError's message IS the offending stderr text.
#
# So: run the command with the preference set to 'Continue' for exactly its duration, and decide
# what happened from the EXIT CODE and the artifact, which is what those things are for.

function Invoke-Compiler {
    param(
        [Parameter(Mandatory)] [string]   $Exe,
        [Parameter(Mandatory)] [string[]] $Arguments,
        [hashtable] $Environment
    )

    # Environment variables are set for the duration of the call and put back afterwards, so a route
    # that needs one (a private zig cache, below) cannot leak it into the next route or into the
    # caller. $null restores 'was not set', which is not the same as 'was empty'.
    $saved = @{}
    if ($Environment) {
        foreach ($k in $Environment.Keys) {
            $saved[$k] = [Environment]::GetEnvironmentVariable($k)
            [Environment]::SetEnvironmentVariable($k, $Environment[$k])
        }
    }

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $raw  = & $Exe @Arguments 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    }

    $text = @($raw | ForEach-Object { $_.ToString() })
    return [pscustomobject]@{ ExitCode = $code; Output = $text }
}


# == What was actually built, read out of the PE ===============================
#
# Never trust a toolchain to have done what it was asked - the same discipline as build-macos.sh
# reading architecture back with lipo and build-linux.sh reading the device worker's ELF header.
# Two things this catches, both of which ship silently otherwise:
#
#   * THE SUBSYSTEM. -mwindows is a NO-OP under zig cc and yields a console stub, measured by
#     reading the field back out of the PE (zig 0.13.0):
#
#         -mwindows                      -> 3 (CONSOLE)   wrong, and no warning
#         -Wl,--subsystem,windows        -> 2 (GUI)       correct
#         both together                  -> 3 (CONSOLE)   -mwindows wins and undoes it
#
#     The stub exists to be invisible; a console window flashing up on every launch of every one of
#     the three applications is about as visible as a defect gets.
#
#   * THE ARCHITECTURE. The cl.exe-already-on-PATH route has no architecture switch of its own - it
#     builds for whatever the Developer PowerShell it was started from targets. On an x64 prompt
#     that yields an x64 binary named circuitRF-stub-arm64.exe, which the arm64 .msi would then
#     ship. Nothing about that fails until an ARM user double-clicks the shortcut.
#
# A binary that fails this is DISCARDED AND THE NEXT ROUTE IS TRIED, rather than ending the run:
# "the compiler on PATH targets the wrong architecture" is a reason to go and find the right one,
# which is exactly what the vswhere route below does.

function Test-StubBinary {
    param([string]$Path, [string]$WantArch)

    $want  = @{ 'x64' = 0x8664; 'arm64' = 0xAA64; 'x86' = 0x014C }[$WantArch]
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 512) { return "the file is $($bytes.Length) bytes; that is not a PE" }

    $pe = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($pe -le 0 -or $pe + 92 -ge $bytes.Length) { return 'the PE header offset is out of range' }
    if ($bytes[$pe] -ne 0x50 -or $bytes[$pe + 1] -ne 0x45) { return 'no PE signature' }

    $machine   = [BitConverter]::ToUInt16($bytes, $pe + 4)
    $subsystem = [BitConverter]::ToUInt16($bytes, $pe + 24 + 68)

    if ($machine -ne $want) {
        return ("it is machine 0x{0:X4}, not the 0x{1:X4} a {2} install needs" -f $machine, $want, $WantArch)
    }
    if ($subsystem -ne 2) {
        return "its subsystem is $subsystem (2 is GUI); a console window would open on every launch"
    }
    return $null
}


# == The routes ================================================================

$zigTarget = @{ 'x64' = 'x86_64-windows-gnu'; 'arm64' = 'aarch64-windows-gnu'; 'x86' = 'x86-windows-gnu' }[$Arch]

# NO QUOTES AROUND THE APP NAME. Windows PowerShell 5.1 strips a bare " when it builds a native
# command line, so -DCRF_APP_NAME="circuitRF" arrived as -DCRF_APP_NAME=circuitRF, L##circuitRF
# pasted into the undeclared identifier LcircuitRF, and the build failed at the first architecture
# (owner-reported, 2026-08-25). The stub now takes a BARE TOKEN and stringifies it itself, so there
# is nothing here to escape and nothing for a future route to get wrong. See circuitrf-stub.c.
$appDefine = "-DCRF_APP_NAME=$AppName"

# -mcpu=baseline IS THE POINT OF THE FIRST TWO ROUTES, not tidiness.
#
# Without an explicit -mcpu, zig resolves the CPU natively whenever the target's architecture and OS
# match the host's - so `-target aarch64-windows-gnu` is a NATIVE build on a Windows-on-ARM machine
# and a cross build everywhere else, down a different code path, with a CPU model and feature set
# that vary per machine. That is why the release box built x86_64 fine and crashed zig outright
# (0xC0000005, no output at all) on aarch64 while running the correct native windows-aarch64 zig
# (owner-reported, 2026-08-25). Pinning the CPU takes the native path out of it.
#
# It is also the right answer independently of the crash: the stub is shipped to other people's
# machines, so building it for whatever CPU happened to cut the release is wrong even when it works.
#
# -municode: wWinMain is the Unicode entry point; without it the mingw CRT looks for WinMain.
# The linker flag is QUOTED: unquoted, PowerShell reads the commas as an array separator and the
# script does not even parse.
#
# ROUTE 3 GIVES ZIG A PRIVATE CACHE. zig caches compiled libc and CRT objects globally
# (%LOCALAPPDATA%\zig), and a half-written entry there makes it fall over on the target that reads
# it while every other target still builds - which is exactly the shape of the aarch64 failure.
# Pointing both cache variables at a scratch directory tests that without deleting anything the
# operator has, and the directory goes away with the build tree.
$zigCache = Join-Path $out 'zig-cache-scratch'

$zigRoutes = @(
    @{ Label = "zig cc ($zigTarget, baseline CPU)";      Flags = @('-mcpu=baseline', '-O2') },
    @{ Label = "zig cc ($zigTarget, baseline CPU, -O0)"; Flags = @('-mcpu=baseline', '-O0') },
    @{ Label = "zig cc ($zigTarget, baseline CPU, private cache)";
       Flags = @('-mcpu=baseline', '-O2')
       Env   = @{ ZIG_GLOBAL_CACHE_DIR = $zigCache; ZIG_LOCAL_CACHE_DIR = $zigCache } },
    @{ Label = "zig cc ($zigTarget, native CPU)";        Flags = @('-O2') }
)

$tried = @()

function Complete-Route {
    param([string]$Label, [object]$Result)

    if ($Result.ExitCode -ne 0 -or -not (Test-Path $script:exe)) {
        # -1073741819 is 0xC0000005, an access violation: the compiler crashed rather than refusing
        # the code, so nothing about the source or the flags is implicated.
        $why = if ($Result.ExitCode -eq -1073741819) { 'crashed' } else { "exit $($Result.ExitCode)" }
        $script:tried += "$Label - $why"
        $script:lastOutput = $Result.Output
        if (Test-Path $script:exe) { Remove-Item $script:exe -Force -ErrorAction SilentlyContinue }
        return $false
    }

    $wrong = Test-StubBinary $script:exe $script:Arch
    if ($wrong) {
        $script:tried += "$Label - built, but $wrong"
        $script:lastOutput = $Result.Output
        Remove-Item $script:exe -Force -ErrorAction SilentlyContinue
        return $false
    }

    if (Test-Path $script:zigCache) { Remove-Item $script:zigCache -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host "OK  $script:exe"
    Write-Host "    Built by $Label."
    return $true
}

$lastOutput = @()

if (Get-Command zig -ErrorAction SilentlyContinue) {
    foreach ($route in $zigRoutes) {
        if (Test-Path $exe) { break }
        Write-Host "Building the launcher stub with $($route.Label) ..."
        $r = Invoke-Compiler -Exe zig -Environment $route.Env -Arguments (
                 @('cc', '-target', $zigTarget) + $route.Flags +
                 @('-municode', '-Wl,--subsystem,windows', $appDefine,
                   $src, '-o', $exe, '-luser32'))
        if (Complete-Route $route.Label $r) { return }
    }
}
else {
    $tried += 'zig - not on PATH'
}

# cl.exe already on PATH: a Developer PowerShell. It builds for whatever that prompt targets, which
# the PE check above verifies rather than assumes.
if (Get-Command cl -ErrorAction SilentlyContinue) {
    Write-Host 'Building the launcher stub with cl.exe (on PATH) ...'
    Push-Location $out
    try {
        $r = Invoke-Compiler cl @('/nologo', '/O2', '/W3', '/DUNICODE', '/D_UNICODE', $appDefine, $src,
                                  '/link', '/SUBSYSTEM:WINDOWS', '/ENTRY:wWinMainCRTStartup',
                                  'user32.lib', "/OUT:$exe")
    }
    finally { Pop-Location }
    if (Complete-Route 'cl.exe (on PATH)' $r) { return }
}

# == cl.exe found for us =======================================================
#
# Visual Studio ships the cross tools for all three architectures; the only thing a plain PowerShell
# is missing is the environment that puts one of them on PATH. vswhere locates the install and
# vcvarsall sets up the exact host_target pair, so "open a Developer PowerShell instead" stops being
# something the operator has to know - which matters, because the alternative advice this script
# used to print was the only remaining option when zig fell over.
#
# The command line is written to a .cmd rather than passed through `cmd /c "..."`. PowerShell 5.1
# mangles embedded quotes when it builds a native command line - the same handling that ate the app
# name above - and these arguments contain paths that may have spaces in them.

$vsRoot  = ${env:ProgramFiles(x86)}
$vswhere = if ($vsRoot) { Join-Path $vsRoot 'Microsoft Visual Studio\Installer\vswhere.exe' } else { $null }
if (-not (Test-Path $exe) -and $vswhere -and (Test-Path $vswhere)) {

    $vsPath = (Invoke-Compiler $vswhere @('-latest', '-products', '*',
                                          '-requires', 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64',
                                          '-property', 'installationPath')).Output |
              Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

    $vcvars = if ($vsPath) { Join-Path $vsPath 'VC\Auxiliary\Build\vcvarsall.bat' } else { $null }

    if ($vcvars -and (Test-Path $vcvars)) {

        # PROCESSOR_ARCHITECTURE reads 'x86' inside a 32-bit PowerShell on a 64-bit machine;
        # PROCESSOR_ARCHITEW6432 is what says so.
        $hostRaw  = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
        $hostVc   = @{ 'ARM64' = 'arm64'; 'AMD64' = 'amd64'; 'x86' = 'x86' }[$hostRaw]
        if (-not $hostVc) { $hostVc = 'amd64' }
        $targetVc = @{ 'x64' = 'amd64'; 'arm64' = 'arm64'; 'x86' = 'x86' }[$Arch]
        $pair     = if ($hostVc -eq $targetVc) { $targetVc } else { "${hostVc}_${targetVc}" }

        Write-Host "Building the launcher stub with cl.exe (Visual Studio, $pair) ..."

        $bat = Join-Path $out 'build-stub-msvc.cmd'
        $lines = @(
            '@echo off',
            "cd /d `"$out`"",
            "call `"$vcvars`" $pair > nul",
            'if errorlevel 1 exit /b 90',
            ("cl /nologo /O2 /W3 /DUNICODE /D_UNICODE $appDefine `"$src`" " +
             "/link /SUBSYSTEM:WINDOWS /ENTRY:wWinMainCRTStartup user32.lib `"/OUT:$exe`"")
        )
        Set-Content -Path $bat -Value $lines -Encoding Ascii

        try {
            $r = Invoke-Compiler $env:ComSpec @('/c', $bat)
            if ($r.ExitCode -eq 90) {
                $r = [pscustomobject]@{ ExitCode = 90; Output = @("vcvarsall.bat has no $pair tools installed") }
            }
        }
        finally { Remove-Item $bat -Force -ErrorAction SilentlyContinue }

        if (Complete-Route "cl.exe (Visual Studio, $pair)" $r) { return }
    }
}


# == Nothing worked ============================================================
#
# The compiler's own output is re-thrown with the failure. Without it a broken build says only
# "the stub could not be built", which is a sentence with no information in it - and this script
# runs on whichever machine cuts the Windows release, where a round trip to diagnose is expensive.

if (Test-Path $zigCache) { Remove-Item $zigCache -Recurse -Force -ErrorAction SilentlyContinue }

if ($tried.Count -gt 0) {
    $detail = ''
    if ($lastOutput.Count -gt 0) {
        $detail = "`n  last compiler output:`n    " + (($lastOutput | Select-Object -Last 12) -join "`n    ")
    }
    throw ("no C compiler could build it. Tried:`n  " + ($tried -join "`n  ") + $detail)
}

throw @'
No C compiler was found for the launcher stub.

Install one of these and run this script again:

    zig      (winget install zig.zig)   - one download, no daemon; the preferred route
    MSVC     (Visual Studio with "Desktop development with C++", or the Build Tools;
              this script finds it with vswhere, so no Developer PowerShell is needed)

The stub is the one file in a per-user install that never changes: shortcuts and file
associations point at it, and it starts the version named in `current`. Without it there is no
per-user install channel and therefore no automatic updates on Windows.
'@

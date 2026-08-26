<#
  == Build the circuitRF per-user launcher stub (Windows) ======================

    .\packaging\windows\stub\build-stub.ps1 -Arch x64

  Writes build\<AppName>-stub-<Arch>.exe. Called by build-msi.ps1 when -Scope perUser.

  Two toolchains are tried, in order: zig cc (one download, no daemon, cross-compiles every
  architecture from any host) and then the MSVC cl.exe on PATH inside a Developer prompt. The
  stub is ~150 lines of plain Win32, so either produces the same thing.

  ------------------------------------------------------------------------------
  THIS FILE MUST STAY PURE ASCII - see the note at the top of build-msi.ps1. Windows PowerShell
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

$zigTarget = @{ 'x64' = 'x86_64-windows-gnu'; 'arm64' = 'aarch64-windows-gnu'; 'x86' = 'x86-windows-gnu' }[$Arch]

if (Get-Command zig -ErrorAction SilentlyContinue) {
    Write-Host "Building the launcher stub with zig cc ($zigTarget) ..."
    # -mwindows: GUI subsystem, so a shortcut opens no console window.
    & zig cc -target $zigTarget -O2 -municode -mwindows "-DCRF_APP_NAME=`"$AppName`"" $src -o $exe -luser32
    if ($LASTEXITCODE -ne 0) { throw 'zig cc failed building the launcher stub.' }
}
elseif (Get-Command cl -ErrorAction SilentlyContinue) {
    Write-Host 'Building the launcher stub with cl.exe ...'
    Push-Location $out
    try {
        & cl /nologo /O2 /W3 /DUNICODE /D_UNICODE "/DCRF_APP_NAME=\`"$AppName\`"" $src `
            /link /SUBSYSTEM:WINDOWS /ENTRY:wWinMainCRTStartup user32.lib "/OUT:$exe"
        if ($LASTEXITCODE -ne 0) { throw 'cl.exe failed building the launcher stub.' }
    }
    finally { Pop-Location }
}
else {
    throw @'
No C compiler was found for the launcher stub.

Install one of these and run this script again:

    zig      (winget install zig.zig)   - one download, no daemon; the preferred route
    MSVC     (a Visual Studio Developer PowerShell, so cl.exe is on PATH)

The stub is the one file in a per-user install that never changes: shortcuts and file
associations point at it, and it starts the version named in `current`. Without it there is no
per-user install channel and therefore no automatic updates on Windows.
'@
}

Write-Host "OK  $exe"

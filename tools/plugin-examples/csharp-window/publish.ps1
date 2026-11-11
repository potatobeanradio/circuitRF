# Publishes this plugin with NativeAOT into bin\<rid>\, one native program per runtime identifier.
#
#   .\publish.ps1                   every RID this machine can build (win-x64 and win-arm64)
#   .\publish.ps1 -Rid win-arm64    one RID
#
# NativeAOT compiles to machine code with the platform's own native toolchain (Visual Studio's "Desktop development
# with C++" workload on Windows), so it cannot cross operating systems: Windows builds the win-* RIDs and nothing
# else. Asked for a RID it cannot build, this script STOPS and says so - a plugin.json that names a RID with no
# program behind it fails for whoever installs it, not for you.
#
# This file is pure ASCII on purpose: Windows PowerShell 5.1 reads a file with no byte-order mark as the ANSI code
# page, and one non-ASCII character can turn the rest of the script into a string literal without an error.
param([string[]]$Rid = @('win-x64', 'win-arm64'))
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

foreach ($r in $Rid) {
    if (-not $r.StartsWith('win-')) {
        Write-Error "Cannot publish $r here: NativeAOT builds win-* RIDs on Windows only. Publish $r on its own operating system."
        exit 2
    }
}

foreach ($r in $Rid) {
    Write-Host "Publishing $r ..."
    dotnet publish LineSection.csproj -c Release -r $r -p:PublishAot=true -o "bin\$r"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
Write-Host "Done. Ship plugin.json with the bin\ folder; each bin\<rid>\ holds the program and the native libraries beside it."

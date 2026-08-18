# ── The version, read from the one place it is written ────────────────────────
#
# Dot-sourced by build-msi.ps1:   . "$PSScriptRoot\..\version.ps1"
#
# Sets:
#   $CrfVersion      the full string from the repo-root VERSION file, e.g. 0.9.0-beta.1
#                    — what users see, and what the .msi file name carries. Set it BEFORE
#                    dot-sourcing this file to override it for a one-off build; nothing is ever
#                    written back, so VERSION stays the source of truth.
#   $CrfMsiVersion   the MSI ProductVersion: purely numeric, four fields, prerelease suffix
#                    stripped (0.9.0-beta.1 → 0.9.0.0). Windows Installer rejects anything else.
#
# Note that Windows Installer compares only the FIRST THREE fields when deciding whether one
# package upgrades another, so two builds of 0.9.0 differing only by prerelease suffix look
# identical to it. Bump the numeric part for anything a user is meant to be able to upgrade to.

if (-not $CrfVersion) {
    $CrfVersion = (Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\VERSION') -Raw).Trim()
}

$fields = @()
foreach ($part in (($CrfVersion -split '-')[0] -split '\.')) {
    $digits = $part -replace '\D', ''
    if ($digits -eq '') { $digits = '0' }
    $fields += [int]$digits
}
while ($fields.Count -lt 3) { $fields += 0 }

$CrfMsiVersion = ($fields[0..2] -join '.') + '.0'

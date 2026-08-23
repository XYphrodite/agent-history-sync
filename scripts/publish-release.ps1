#Requires -Version 5.1
<#
.SYNOPSIS
  Create a git tag and push it to trigger the GitHub Actions release workflow.

.PARAMETER Version
  SemVer without leading v, e.g. 0.5.1  → tag v0.5.1

.EXAMPLE
  .\scripts\publish-release.ps1 -Version 0.5.1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($Version.StartsWith("v")) {
    $Version = $Version.Substring(1)
}
if ($Version -notmatch '^\d+\.\d+\.\d+([.-].+)?$') {
    throw "Version must look like 0.5.1 (got '$Version')"
}

$tag = "v$Version"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$status = git status --porcelain
if ($status) {
    throw "Working tree is dirty. Commit or stash before tagging.`n$status"
}

$branch = (git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne "main") {
    Write-Warning "Current branch is '$branch' (usually tag from main)."
}

Write-Host "Creating annotated tag $tag"
git tag -a $tag -m "Release $tag"
Write-Host "Pushing tag $tag to origin (publishes agent-sync.exe through .github/workflows/release.yml)"
git push origin $tag
Write-Host "Done. Watch: gh run list --workflow release.yml"
Write-Host "When green: gh release view $tag"

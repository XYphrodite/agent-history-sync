#Requires -Version 5.1
<#
.SYNOPSIS
  Install Agent History Sync (codex-sync.exe) from GitHub Releases.

.DESCRIPTION
  Downloads the win-x64 single-file binary from
  https://github.com/XYphrodite/agent-history-sync/releases
  and installs it under %LOCALAPPDATA%\Programs\CodexHistorySync by default.

.PARAMETER Version
  Release tag without or with leading v, e.g. 0.2.0 or v0.2.0. Default: latest.

.PARAMETER InstallDir
  Target directory. Default: $env:LOCALAPPDATA\Programs\CodexHistorySync

.PARAMETER Repo
  GitHub owner/name of the source repository.

.PARAMETER AddToPath
  Prepend InstallDir to the current user's PATH if missing.

.PARAMETER SkipHash
  Do not require/verify the .sha256 asset (not recommended).

.EXAMPLE
  # One-liner (after the script is on main):
  irm https://raw.githubusercontent.com/XYphrodite/agent-history-sync/main/scripts/install.ps1 | iex

.EXAMPLE
  .\scripts\install.ps1 -Version v0.2.0 -AddToPath
#>
[CmdletBinding()]
param(
    [string] $Version = "latest",
    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\CodexHistorySync"),
    [string] $Repo = "XYphrodite/agent-history-sync",
    [switch] $AddToPath,
    [switch] $SkipHash
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step([string] $Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Get-GitHubRelease {
    param([string] $Repository, [string] $Tag)
    $headers = @{
        "Accept"               = "application/vnd.github+json"
        "User-Agent"           = "agent-history-sync-install"
        "X-GitHub-Api-Version" = "2022-11-28"
    }
    if ($env:GITHUB_TOKEN) {
        $headers["Authorization"] = "Bearer $($env:GITHUB_TOKEN)"
    }

    if ($Tag -eq "latest") {
        $uri = "https://api.github.com/repos/$Repository/releases/latest"
    }
    else {
        $normalized = if ($Tag.StartsWith("v")) { $Tag } else { "v$Tag" }
        $uri = "https://api.github.com/repos/$Repository/releases/tags/$normalized"
    }

    Write-Step "Fetching release metadata: $uri"
    return Invoke-RestMethod -Uri $uri -Headers $headers -Method Get
}

function Get-AssetUrl {
    param($Release, [string] $Name)
    $asset = @($Release.assets) | Where-Object { $_.name -eq $Name } | Select-Object -First 1
    if (-not $asset) {
        throw "Release '$($Release.tag_name)' does not contain asset '$Name'. Available: $((@($Release.assets) | ForEach-Object name) -join ', ')"
    }
    return $asset.browser_download_url
}

function Expand-Sha256File {
    param([string] $Path)
    $line = (Get-Content -LiteralPath $Path -Raw).Trim()
    if ($line -match '^(?<hash>[A-Fa-f0-9]{64})\s+') {
        return $Matches["hash"].ToLowerInvariant()
    }
    if ($line -match '^(?<hash>[A-Fa-f0-9]{64})$') {
        return $Matches["hash"].ToLowerInvariant()
    }
    throw "Could not parse SHA-256 from $Path"
}

try {
    $release = Get-GitHubRelease -Repository $Repo -Tag $Version
    $tag = $release.tag_name
    Write-Step "Using release $tag"

    $exeUrl = Get-AssetUrl -Release $release -Name "codex-sync.exe"
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("agent-history-sync-install-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    $tempExe = Join-Path $tempRoot "codex-sync.exe"
    $tempSha = Join-Path $tempRoot "codex-sync.exe.sha256"

    Write-Step "Downloading codex-sync.exe"
    Invoke-WebRequest -Uri $exeUrl -OutFile $tempExe -UseBasicParsing

    if (-not $SkipHash) {
        try {
            $shaUrl = Get-AssetUrl -Release $release -Name "codex-sync.exe.sha256"
            Write-Step "Downloading and verifying SHA-256"
            Invoke-WebRequest -Uri $shaUrl -OutFile $tempSha -UseBasicParsing
            $expected = Expand-Sha256File -Path $tempSha
            $actual = (Get-FileHash -Algorithm SHA256 -Path $tempExe).Hash.ToLowerInvariant()
            if ($expected -ne $actual) {
                throw "SHA-256 mismatch. Expected $expected, got $actual"
            }
            Write-Host "SHA-256 OK: $actual"
        }
        catch {
            if ($SkipHash) { throw }
            Write-Warning "Checksum asset missing or invalid: $($_.Exception.Message)"
            throw "Refusing to install without a valid codex-sync.exe.sha256 (pass -SkipHash to override)."
        }
    }
    else {
        Write-Warning "Skipping SHA-256 verification (-SkipHash)."
    }

    $existing = Join-Path $InstallDir "codex-sync.exe"
    if (Test-Path -LiteralPath $existing) {
        Write-Step "Existing binary found; uninstalling agent task if owned by this path (best effort)"
        try {
            & $existing agent uninstall 2>$null | Out-Null
        }
        catch {
            # Task may not exist or may be owned by another path.
        }
    }

    Write-Step "Installing to $InstallDir"
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item -LiteralPath $tempExe -Destination $existing -Force

    if ($AddToPath) {
        $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
        if (-not $userPath) { $userPath = "" }
        $parts = $userPath -split ";" | Where-Object { $_ -and $_.Trim() -ne "" }
        $normalizedInstall = [IO.Path]::GetFullPath($InstallDir).TrimEnd("\")
        $already = $parts | Where-Object { [IO.Path]::GetFullPath($_).TrimEnd("\") -ieq $normalizedInstall }
        if (-not $already) {
            Write-Step "Adding install directory to user PATH"
            $newPath = if ($userPath.Trim()) { "$normalizedInstall;$userPath" } else { $normalizedInstall }
            [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
            $env:Path = "$normalizedInstall;" + $env:Path
        }
        else {
            Write-Host "Install directory already on user PATH."
        }
    }

    Write-Step "Smoke test: --help"
    & $existing --help
    if ($LASTEXITCODE -ne 0) {
        throw "Installed binary failed --help (exit $LASTEXITCODE)"
    }

    Write-Host ""
    Write-Host "Installed Agent History Sync $tag" -ForegroundColor Green
    Write-Host "  Binary: $existing"
    Write-Host "  Next:"
    Write-Host "    & `"$existing`" doctor"
    Write-Host "    & `"$existing`" status"
    Write-Host "    & `"$existing`" init https://github.com/OWNER/agent-history-sync-data.git"
}
finally {
    if ($tempRoot -and (Test-Path -LiteralPath $tempRoot)) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

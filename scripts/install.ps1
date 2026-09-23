#Requires -Version 5.1
<#
.SYNOPSIS
  Install Agent History Sync (agent-sync.exe) from GitHub Releases.

.DESCRIPTION
  Downloads the win-x64 single-file binary from
  https://github.com/XYphrodite/agent-history-sync/releases
  and installs it under %LOCALAPPDATA%\Programs\CodexHistorySync by default.

  When the .NET runtime is installed, the smaller framework-dependent
  agent-sync-light.exe is used instead; -Variant overrides the choice.
  The chosen variant is recorded next to the executable so
  `agent-sync update` keeps installing the same kind of build.

.PARAMETER Version
  Release tag without or with leading v, e.g. 0.5.2 or v0.5.2. Default: latest.

.PARAMETER InstallDir
  Target directory. Default: $env:LOCALAPPDATA\Programs\CodexHistorySync

.PARAMETER Repo
  GitHub owner/name of the source repository.

.PARAMETER AddToPath
  Prepend InstallDir to the current user's PATH without asking.

.PARAMETER NoPath
  Do not add InstallDir to PATH and do not ask.

.PARAMETER Variant
  auto (default) picks the light package when the .NET runtime is
  installed, full always installs the self-contained package, light always
  installs the framework-dependent package (and fails fast without the runtime).

.PARAMETER SkipHash
  Do not require/verify the .sha256 asset (not recommended).

.EXAMPLE
  # One-liner (after the script is on main):
  irm https://raw.githubusercontent.com/XYphrodite/agent-history-sync/main/scripts/install.ps1 | iex

.EXAMPLE
  .\scripts\install.ps1 -Version v0.5.2 -AddToPath

.EXAMPLE
  .\scripts\install.ps1 -Variant full
#>
[CmdletBinding()]
param(
    [string] $Version = "latest",
    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\CodexHistorySync"),
    [string] $Repo = "XYphrodite/agent-history-sync",
    [ValidateSet("auto", "full", "light")]
    [string] $Variant = "auto",
    [switch] $AddToPath,
    [switch] $NoPath,
    [switch] $SkipHash
)

if ($AddToPath -and $NoPath) {
    throw "Use only one of -AddToPath or -NoPath."
}

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step([string] $Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

$ExeAssetName = "agent-sync.exe"
$LightExeAssetName = "agent-sync-light.exe"
$VariantMarker = ".agent-sync-variant"
# Major of the .NET runtime the light package targets. Bump together
# with the app's target framework; must match AgentSyncUpdate in the source.
$RuntimeMajor = "10"

function Test-DotNetRuntimeLine {
    param([string] $Line, [string] $Major = $RuntimeMajor)
    # Console apps run on the shared framework: Microsoft.NETCore.App qualifies.
    # A machine with Microsoft.WindowsDesktop.App also qualifies, the desktop
    # bundle includes the base runtime.
    return [bool]($Line -match "^Microsoft\.(NETCore|WindowsDesktop)\.App\s+$Major\.")
}

function Test-DotNetRuntime {
    try {
        $runtimes = & dotnet --list-runtimes 2>$null
    } catch {
        return $false
    }
    foreach ($line in @($runtimes)) {
        if (Test-DotNetRuntimeLine -Line ([string]$line)) { return $true }
    }
    return $false
}

function Select-AgentSyncAsset {
    param(
        [ValidateSet("auto", "full", "light")]
        [string] $Variant = "auto",
        [bool] $HasRuntime = $false
    )
    if ($Variant -eq "light") { return $LightExeAssetName }
    if ($Variant -eq "full") { return $ExeAssetName }
    if ($HasRuntime) { return $LightExeAssetName }
    return $ExeAssetName
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

$tempRoot = $null
try {
    $release = Get-GitHubRelease -Repository $Repo -Tag $Version
    $tag = $release.tag_name
    Write-Step "Using release $tag"

    $hasRuntime = Test-DotNetRuntime
    if ($Variant -eq "light" -and -not $hasRuntime) {
        throw "The light package needs the .NET $RuntimeMajor runtime, which was not found. Install the runtime or use -Variant full."
    }
    $assetName = Select-AgentSyncAsset -Variant $Variant -HasRuntime $hasRuntime
    $variantName = if ($assetName -eq $LightExeAssetName) { "light" } else { "full" }
    $shaName = "$assetName.sha256"
    Write-Step "Using $variantName package $assetName"

    $exeUrl = Get-AssetUrl -Release $release -Name $assetName
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("agent-history-sync-install-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    $tempExe = Join-Path $tempRoot $assetName
    $tempSha = Join-Path $tempRoot $shaName

    Write-Step "Downloading $assetName"
    Invoke-WebRequest -Uri $exeUrl -OutFile $tempExe -UseBasicParsing

    if (-not $SkipHash) {
        try {
            $shaUrl = Get-AssetUrl -Release $release -Name $shaName
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
            throw "Refusing to install without a valid $shaName (pass -SkipHash to override)."
        }
    }
    else {
        Write-Warning "Skipping SHA-256 verification (-SkipHash)."
    }

    $existing = Join-Path $InstallDir "agent-sync.exe"
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
    # Records which kind of build this is, so `agent-sync update` keeps the variant.
    [IO.File]::WriteAllText((Join-Path $InstallDir $VariantMarker), $variantName)

    $shouldAddToPath = $false
    if ($AddToPath) {
        $shouldAddToPath = $true
    }
    elseif ($NoPath) {
        $shouldAddToPath = $false
        Write-Host "PATH will not be modified (-NoPath)."
    }
    elseif ([Environment]::UserInteractive) {
        Write-Host ""
        Write-Host "Install directory: $InstallDir"
        Write-Host "Adding it to your user PATH lets you run 'agent-sync' without the full path."
        $answer = Read-Host "Add install directory to user PATH? [Y/n]"
        if ([string]::IsNullOrWhiteSpace($answer) -or $answer -match '^(y|yes)$') {
            $shouldAddToPath = $true
        }
        else {
            Write-Host "Skipped PATH update."
        }
    }
    else {
        Write-Host "Non-interactive host: PATH not modified (pass -AddToPath or -NoPath)."
    }

    if ($shouldAddToPath) {
        $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
        if (-not $userPath) { $userPath = "" }
        $parts = @($userPath -split ";" | Where-Object { $_ -and $_.Trim() -ne "" })
        $normalizedInstall = [IO.Path]::GetFullPath($InstallDir).TrimEnd("\")
        $already = $false
        foreach ($part in $parts) {
            try {
                if ([IO.Path]::GetFullPath($part).TrimEnd("\") -ieq $normalizedInstall) {
                    $already = $true
                    break
                }
            }
            catch {
                # Ignore malformed PATH entries.
            }
        }
        if (-not $already) {
            Write-Step "Adding install directory to user PATH"
            $newPath = if ($userPath.Trim()) { "$normalizedInstall;$userPath" } else { $normalizedInstall }
            [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
            $env:Path = "$normalizedInstall;" + $env:Path
            Write-Host "PATH updated for new terminals. This session: use full path or open a new PowerShell."
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
    Write-Host "Installed Agent History Sync $tag ($variantName)" -ForegroundColor Green
    Write-Host "  Binary: $existing"
    if ($shouldAddToPath) {
        Write-Host "  PATH:   user PATH includes install dir (new terminals)"
    }
    Write-Host "  Next:"
    Write-Host "    & `"$existing`" doctor"
    Write-Host "    & `"$existing`" status"
    Write-Host "    & `"$existing`" join https://github.com/XYphrodite/agent-history-sync-data.git"
}
finally {
    if ($null -ne $tempRoot -and (Test-Path -LiteralPath $tempRoot)) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

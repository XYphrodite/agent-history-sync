#Requires -Version 5.1
# Variant selection of scripts/install.ps1: the light package is picked only when the
# .NET runtime is present. Loads only the pure selection functions,
# never the download/install/PATH code.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$installer = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/install.ps1'
$source = Get-Content -LiteralPath $installer -Raw -Encoding UTF8
if ($source.ToCharArray() | Where-Object { [int]$_ -gt 127 }) { throw 'Installer must remain ASCII-only' }
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$RuntimeMajor = '10'
$ExeAssetName = 'agent-sync.exe'
$LightExeAssetName = 'agent-sync-light.exe'
foreach ($name in @('Test-DotNetRuntimeLine', 'Select-AgentSyncAsset')) {
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if ($null -eq $function) { throw "Function $name not found in scripts/install.ps1" }
    . ([scriptblock]::Create($function.Extent.Text))
}
function Assert { param([bool] $Condition, [string] $Message) if (-not $Condition) { throw $Message } }
$passed = 0
function Check {
    param([string] $Name, [scriptblock] $Body)
    try { & $Body; Write-Host "PASS $Name" -ForegroundColor Green; $script:passed++ }
    catch { Write-Host "FAIL $Name : $_" -ForegroundColor Red; throw }
}

Check 'auto picks light with the runtime' { Assert ((Select-AgentSyncAsset -Variant auto -HasRuntime $true) -eq $LightExeAssetName) 'not light' }
Check 'auto picks full without the runtime' { Assert ((Select-AgentSyncAsset -Variant auto -HasRuntime $false) -eq $ExeAssetName) 'not full' }
Check 'explicit full wins over the runtime' { Assert ((Select-AgentSyncAsset -Variant full -HasRuntime $true) -eq $ExeAssetName) 'not full' }
Check 'explicit light wins without the runtime' { Assert ((Select-AgentSyncAsset -Variant light -HasRuntime $false) -eq $LightExeAssetName) 'not light' }
Check 'base runtime line matches exact major' {
    Assert (Test-DotNetRuntimeLine -Line 'Microsoft.NETCore.App 10.0.5 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]') 'missed 10.x'
}
Check 'desktop runtime line also matches exact major' {
    Assert (Test-DotNetRuntimeLine -Line 'Microsoft.WindowsDesktop.App 10.0.5 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]') 'missed desktop 10.x'
}
Check 'other majors do not count' {
    Assert (-not (Test-DotNetRuntimeLine -Line 'Microsoft.NETCore.App 9.0.1 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]')) 'matched 9.x'
    Assert (-not (Test-DotNetRuntimeLine -Line 'Microsoft.WindowsDesktop.App 11.0.0 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]')) 'matched 11.x'
}
Check 'other frameworks do not count' {
    Assert (-not (Test-DotNetRuntimeLine -Line 'Microsoft.AspNetCore.App 10.0.5 [C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App]')) 'matched AspNetCore'
}
Check 'empty and garbage lines do not count' {
    Assert (-not (Test-DotNetRuntimeLine -Line '')) 'matched empty'
    Assert (-not (Test-DotNetRuntimeLine -Line 'Microsoft.NETCore.App')) 'matched bare name'
}

Write-Host ''
Write-Host "Variant checks: $passed passed" -ForegroundColor Green

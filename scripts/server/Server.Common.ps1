$ErrorActionPreference = 'Stop'
$deploymentRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$deployment = Get-Content -LiteralPath (Join-Path $deploymentRoot 'deployment.json') -Raw | ConvertFrom-Json
$env:DOCKER_CONFIG = Join-Path $deploymentRoot 'docker-cli'
$env:DOCKER_HOST = 'npipe:////./pipe/dockerDesktopLinuxEngine'

function Invoke-ServerCompose {
    param([Parameter(ValueFromRemainingArguments=$true)][string[]]$ComposeArguments)
    & $deployment.DockerExe compose --project-name agent-sync-server --project-directory $deploymentRoot -f (Join-Path $deploymentRoot 'compose.server.yaml') -f (Join-Path $deploymentRoot 'compose.host.yaml') @ComposeArguments
    if ($LASTEXITCODE -ne 0) { throw "Docker Compose failed with exit code $LASTEXITCODE" }
}

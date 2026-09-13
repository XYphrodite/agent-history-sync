. (Join-Path $PSScriptRoot 'Server.Common.ps1')
Invoke-ServerCompose up -d --no-build --wait --wait-timeout 120
$response = Invoke-WebRequest -UseBasicParsing -Uri ($deployment.Origin + '/readyz') -TimeoutSec 10
if ($response.StatusCode -ne 200) { throw 'Server readiness check failed' }
Write-Output 'agent-sync server is ready.'

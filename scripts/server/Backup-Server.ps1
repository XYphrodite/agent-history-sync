. (Join-Path $PSScriptRoot 'Server.Common.ps1')
$backupRoot = Join-Path $deploymentRoot 'backups'
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
$backupName = 'agent-sync-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff', [Globalization.CultureInfo]::InvariantCulture)
$dumpPath = Join-Path $backupRoot ($backupName + '.dump')
$containerPath = '/tmp/' + $backupName + '.dump'
try {
    Invoke-ServerCompose exec -T postgres pg_dump -U agent_sync -d agent_sync -Fc -f $containerPath
    Invoke-ServerCompose exec -T postgres pg_restore --list $containerPath | Out-Null
    Invoke-ServerCompose cp ('postgres:' + $containerPath) $dumpPath
    $hash = (Get-FileHash -LiteralPath $dumpPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($dumpPath + '.sha256', $hash + '  ' + [IO.Path]::GetFileName($dumpPath) + "`n", [Text.Encoding]::ASCII)
    Copy-Item -LiteralPath (Join-Path $deploymentRoot 'deployment.json') -Destination (Join-Path $backupRoot ($backupName + '.deployment.json'))
    Write-Output ('Backup verified: ' + $dumpPath)
} finally {
    Invoke-ServerCompose exec -T postgres rm -f $containerPath
}

# Keep the newest 14 verified dumps; remove only named backup files under this root.
$oldBackups = Get-ChildItem -LiteralPath $backupRoot -Filter 'agent-sync-*.dump' -File |
    Where-Object { Test-Path -LiteralPath ($_.FullName + '.sha256') } |
    Sort-Object Name -Descending | Select-Object -Skip 14
foreach ($backup in $oldBackups) {
    foreach ($candidate in @($backup.FullName, ($backup.FullName + '.sha256'), [IO.Path]::ChangeExtension($backup.FullName, '.deployment.json'))) {
        $fullPath = [IO.Path]::GetFullPath($candidate)
        if (-not $fullPath.StartsWith($backupRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Backup path escaped its directory' }
        if (Test-Path -LiteralPath $fullPath) { Remove-Item -LiteralPath $fullPath }
    }
}

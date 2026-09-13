# Windows host with Docker Desktop (Linux containers)

These operations scripts run from a dedicated deployment directory, outside the source checkout. Copy `Server.Common.ps1`, `Start-Server.ps1`, `Backup-Server.ps1`, `compose.server.yaml` and your host override there. The Compose project name is always `agent-sync-server`, so upgrades reuse its PostgreSQL volume.

Create `deployment.json` in that directory, using the actual Docker executable path and server address:

```json
{
  "DockerExe": "C:\\Program Files\\Docker\\Docker\\resources\\bin\\docker.exe",
  "Origin": "http://100.101.102.103:58080",
  "Version": "0.13.0",
  "Revision": "full release commit hash",
  "Image": "agent-sync-server:0.13.0"
}
```

The directory must have an isolated `docker-cli/config.json`, a private `.secrets/postgres_password`, and `compose.host.yaml` overriding the API image, port and allowed hosts. Limit its Windows ACL to the operator, SYSTEM and administrators. Configure the firewall before publishing the port; see [the server trust boundary](../../docs/server.md).

Run `Start-Server.ps1` to bring up the existing deployment and check readiness. It never builds or pulls a new image. Docker must already be running. With Docker Desktop, startup usually depends on a Windows user signing in; `restart: unless-stopped` restarts containers when the daemon returns, but does not itself start Docker Desktop. Do not assume unattended recovery after a host reboot without verifying this separately.

Run `Backup-Server.ps1` to create a PostgreSQL custom-format dump, verify its archive listing and write a SHA-256 sidecar. Binary data is transferred with `docker compose cp`, avoiding Windows PowerShell 5 redirection corruption. The script keeps the newest 14 verified dumps, with deployment metadata, under `backups`. A daily scheduled task may run it as SYSTEM if that account can access the Docker engine pipe. Use `-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy RemoteSigned -File` for the task action.

Backups on the same host do not survive loss of that host or disk. Copy them to another trusted device. Restore into a separate PostgreSQL volume and validate before changing the active deployment; never run `down -v` on the production project. The database password file is separate from the client encryption key/passphrase; preserve both through their respective secure backups.

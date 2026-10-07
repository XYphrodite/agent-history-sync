using System.Security.Cryptography;

namespace CodexHistorySync.Core.Hermes;

/// <summary>
/// SQLite cannot share WAL state across a network share — a live <c>state.db</c> on
/// <c>\\wsl.localhost</c> (or any UNC path) fails with "database is locked". Reads and writes go
/// through a local copy of the database files instead. Writes commit back only while the remote
/// write-ahead log is unchanged since the copy; otherwise nothing is written and the caller sees
/// an <see cref="IOException"/>. A remote home can lose a race with its own Hermes process this
/// way, but it can never be silently overwritten.
/// </summary>
internal sealed class HermesDatabaseShadow : IDisposable
{
    private readonly string? shadowDirectory;
    private readonly string remoteDatabase;
    private readonly bool write;
    private readonly string? remoteWalFingerprint;

    private HermesDatabaseShadow(string remoteDatabase, string database, string? shadowDirectory, bool write, string? remoteWalFingerprint)
    {
        this.remoteDatabase = remoteDatabase;
        this.shadowDirectory = shadowDirectory;
        this.write = write;
        this.remoteWalFingerprint = remoteWalFingerprint;
        Database = database;
    }

    /// <summary>The database path to open: the original for local homes, a local copy for remote ones.</summary>
    public string Database { get; }

    public static bool IsRemote(string path) => path.StartsWith(@"\\", StringComparison.Ordinal);

    public static HermesDatabaseShadow ForRead(string databasePath) => Open(databasePath, write: false);

    public static HermesDatabaseShadow ForWrite(string databasePath) => Open(databasePath, write: true);

    /// <summary>Test seam: shadow-copy even a local path, so the copy semantics are testable.</summary>
    internal static HermesDatabaseShadow Open(string databasePath, bool write, bool forceShadow) =>
        Open(databasePath, write, forceShadow ? PathKind.Shadow : PathKind.Keep);

    private static HermesDatabaseShadow Open(string databasePath, bool write) =>
        Open(databasePath, write, IsRemote(databasePath) ? PathKind.Shadow : PathKind.Keep);

    private static HermesDatabaseShadow Open(string databasePath, bool write, PathKind kind)
    {
        if (kind == PathKind.Keep) return new HermesDatabaseShadow(databasePath, databasePath, null, write, null);

        var directory = Directory.CreateTempSubdirectory("agent-sync-hermes-");
        try
        {
            var local = Path.Combine(directory.FullName, "state.db");
            // The write-ahead log holds full pages newer than the database copy, so a later wal
            // copy still reads as one consistent state; the shared-memory index is rebuilt as needed.
            CopyIfPresent(databasePath, local);
            CopyIfPresent(databasePath + "-wal", local + "-wal");
            CopyIfPresent(databasePath + "-shm", local + "-shm");
            return new HermesDatabaseShadow(databasePath, local, directory.FullName, write, WalFingerprint(databasePath));
        }
        catch
        {
            try { directory.Delete(recursive: true); } catch (Exception exception) when (IsFileFailure(exception)) { }
            throw;
        }
    }

    /// <summary>Publishes a shadow write back to the remote home, or refuses when it changed underneath.</summary>
    public void CommitBack()
    {
        if (shadowDirectory is null || !write) return;
        if (!string.Equals(WalFingerprint(remoteDatabase), remoteWalFingerprint, StringComparison.Ordinal))
            throw new IOException("The Hermes state database changed while it was being updated; nothing was written.");

        var parent = Path.GetDirectoryName(remoteDatabase);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        // The shadow copy had the captured wal frames merged in when it was written; retiring the
        // remote wal afterwards cannot drop data that is not already inside the copied database.
        File.Copy(Database, remoteDatabase, overwrite: true);
        TryDelete(remoteDatabase + "-wal");
        TryDelete(remoteDatabase + "-shm");
    }

    public void Dispose()
    {
        if (shadowDirectory is null) return;
        try { Directory.Delete(shadowDirectory, recursive: true); }
        catch (Exception exception) when (IsFileFailure(exception)) { }
    }

    private static void CopyIfPresent(string source, string destination)
    {
        if (File.Exists(source)) File.Copy(source, destination, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            throw new IOException("The Hermes state database could not be updated.", exception);
        }
    }

    private static string WalFingerprint(string databasePath)
    {
        var wal = databasePath + "-wal";
        try
        {
            if (!File.Exists(wal)) return "absent";
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(wal))).ToLowerInvariant();
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            return "unreadable-" + Guid.NewGuid().ToString("N");
        }
    }

    private static bool IsFileFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private enum PathKind { Keep, Shadow }
}

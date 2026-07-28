using System.Security.Cryptography;
using System.Text.Json;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.IO;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Sync;

public sealed record BackupRecord(string Id, string OriginalPath, ContentHash ContentHash, DateTimeOffset CreatedAtUtc, string OperationId, string DirectoryPath, string ContentPath, string ManifestPath);
internal sealed record BackupManifest(int SchemaVersion, string OriginalPath, ContentHash ContentHash, DateTimeOffset CreatedAtUtc, string OperationId);

public sealed class BackupStore
{
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CodexPaths _paths;
    private readonly IAtomicFileSystem _fileSystem;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _retention;

    public BackupStore(string repositoryId, string? localAppDataDirectory, CodexPaths codexPaths, IAtomicFileSystem? fileSystem = null, TimeProvider? timeProvider = null, TimeSpan? retention = null)
    {
        ArgumentNullException.ThrowIfNull(codexPaths);
        PathSafety.ValidateFileComponent(repositoryId, nameof(repositoryId));
        var local = PathSafety.Canonicalize(localAppDataDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), nameof(localAppDataDirectory));
        RootPath = Path.GetFullPath(Path.Combine(local, "CodexHistorySync", "repositories", repositoryId, "backups"));
        PathSafety.EnsureOutsideCodex(RootPath, codexPaths, nameof(localAppDataDirectory));
        _paths = codexPaths;
        _fileSystem = fileSystem ?? new AtomicFileSystem();
        _clock = timeProvider ?? TimeProvider.System;
        _retention = retention ?? DefaultRetention;
        if (_retention < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
    }

    public string RootPath { get; }

    public async Task<BackupRecord> CreateAsync(string originalPath, string operationId, CancellationToken ct)
    {
        PathSafety.RejectReparsePoints(RootPath, nameof(RootPath));
        var original = EnsureSynchronized(originalPath);
        PathSafety.ValidateFileComponent(operationId, nameof(operationId));
        if (!File.Exists(original)) throw new FileNotFoundException("The file to back up does not exist.", original);
        var created = _clock.GetUtcNow();
        var id = $"{created:yyyyMMdd'T'HHmmssfffffff'Z'}-{Guid.NewGuid():N}";
        Directory.CreateDirectory(RootPath);
        var directory = Path.Combine(RootPath, id);
        var staging = Path.Combine(RootPath, $".{id}.tmp");
        Directory.CreateDirectory(staging);
        var contentPath = Path.Combine(staging, "content.bin");
        var manifestPath = Path.Combine(staging, "manifest.json");
        try
        {
            await using (var source = OpenRead(original)) await WriteDurableAsync(contentPath, source, ct).ConfigureAwait(false);
            var originalHash = await HashFileAsync(original, ct).ConfigureAwait(false);
            var backupHash = await HashFileAsync(contentPath, ct).ConfigureAwait(false);
            if (!StringComparer.OrdinalIgnoreCase.Equals(originalHash.Hex, backupHash.Hex)) throw new IOException("The backup copy could not be verified.");
            var manifest = new BackupManifest(1, original, backupHash, created, operationId);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            await WriteDurableAsync(manifestPath, new MemoryStream(bytes, writable: false), ct).ConfigureAwait(false);
            Directory.Move(staging, directory);
            return Record(id, directory, manifest);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    public async Task RestoreAsync(string backupId, CancellationToken ct)
    {
        PathSafety.RejectReparsePoints(RootPath, nameof(RootPath));
        var backup = await LoadAsync(backupId, ct).ConfigureAwait(false);
        if (!HashEquals(await HashFileAsync(backup.ContentPath, ct).ConfigureAwait(false), backup.ContentHash)) throw new InvalidDataException("Backup content hash verification failed.");
        var displaced = File.Exists(backup.OriginalPath)
            ? await CreateAsync(backup.OriginalPath, $"restore-{backup.Id}", ct).ConfigureAwait(false)
            : null;
        Directory.CreateDirectory(Path.GetDirectoryName(backup.OriginalPath)!);
        var temporary = SiblingTemporaryPath(backup.OriginalPath);
        try
        {
            await using var content = OpenRead(backup.ContentPath);
            await _fileSystem.WriteTemporaryAsync(temporary, content, ct).ConfigureAwait(false);
            if (!HashEquals(await HashFileAsync(temporary, ct).ConfigureAwait(false), backup.ContentHash)) throw new InvalidDataException("Staged restore content hash verification failed.");
            if (displaced is null)
                await _fileSystem.ReplaceAsync(temporary, backup.OriginalPath, ct).ConfigureAwait(false);
            else if (!await _fileSystem.ReplaceIfUnchangedAsync(temporary, backup.OriginalPath, displaced.ContentHash, mutationAllowed: null, ct).ConfigureAwait(false))
                throw new IOException("The restore destination changed after it was backed up; restore was not applied.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<IReadOnlyList<BackupRecord>> ListAsync(CancellationToken ct)
    {
        PathSafety.RejectReparsePoints(RootPath, nameof(RootPath));
        if (!Directory.Exists(RootPath)) return [];
        var result = new List<BackupRecord>();
        foreach (var directory in Directory.EnumerateDirectories(RootPath).Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal)).OrderBy(x => x, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            result.Add(await LoadAsync(Path.GetFileName(directory), ct).ConfigureAwait(false));
        }
        return result;
    }

    public async Task<int> PruneAsync(CancellationToken ct)
    {
        PathSafety.RejectReparsePoints(RootPath, nameof(RootPath));
        var threshold = _clock.GetUtcNow() - _retention;
        var removed = 0;
        foreach (var backup in await ListAsync(ct).ConfigureAwait(false))
        {
            if (backup.CreatedAtUtc >= threshold) continue;
            PathSafety.RejectReparsePoints(backup.DirectoryPath, nameof(backup.DirectoryPath));
            Directory.Delete(backup.DirectoryPath, recursive: true);
            removed++;
        }
        return removed;
    }

    private async Task<BackupRecord> LoadAsync(string id, CancellationToken ct)
    {
        PathSafety.RejectReparsePoints(RootPath, nameof(RootPath));
        PathSafety.ValidateFileComponent(id, nameof(id));
        var directory = Path.GetFullPath(Path.Combine(RootPath, id));
        if (!CodexPaths.IsPathWithin(directory, RootPath)) throw new ArgumentException("Backup ID escapes the backup root.", nameof(id));
        var manifestPath = Path.Combine(directory, "manifest.json");
        await using var input = OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(input, JsonOptions, ct).ConfigureAwait(false) ?? throw new InvalidDataException("Backup manifest is empty.");
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("Backup manifest schema is unsupported.");
        EnsureSynchronized(manifest.OriginalPath);
        PathSafety.ValidateFileComponent(manifest.OperationId, nameof(manifest.OperationId));
        return Record(id, directory, manifest);
    }

    private BackupRecord Record(string id, string directory, BackupManifest manifest) => new(id, manifest.OriginalPath, manifest.ContentHash, manifest.CreatedAtUtc, manifest.OperationId, directory, Path.Combine(directory, "content.bin"), Path.Combine(directory, "manifest.json"));

    private string EnsureSynchronized(string path)
    {
        var canonical = PathSafety.Canonicalize(path, nameof(path), requireFullyQualified: true);
        if (!new[] { _paths.Sessions, _paths.ArchivedSessions, _paths.Attachments }.Any(root => CodexPaths.IsPathWithin(canonical, root) && !StringComparer.OrdinalIgnoreCase.Equals(canonical, Path.TrimEndingDirectorySeparator(root))))
            throw new ArgumentException("The backup source is outside synchronized Codex paths.", nameof(path));
        return canonical;
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
    private static async Task WriteDurableAsync(string path, Stream content, CancellationToken ct)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await content.CopyToAsync(output, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
        output.Flush(true);
    }
    internal static async Task<ContentHash> HashFileAsync(string path, CancellationToken ct)
    {
        await using var input = OpenRead(path);
        return new ContentHash(Convert.ToHexString(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false)).ToLowerInvariant());
    }
    internal static bool HashEquals(ContentHash left, ContentHash right) => StringComparer.OrdinalIgnoreCase.Equals(left.Hex, right.Hex);
    internal static string SiblingTemporaryPath(string destination) => Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using CodexHistorySync.Core.Claude;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Grok;
using CodexHistorySync.Core.IO;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Sync;

public sealed record BackupRecord(string Id, string OriginalPath, ContentHash ContentHash, DateTimeOffset CreatedAtUtc, string OperationId, string DirectoryPath, string ContentPath, string ManifestPath);
internal sealed record BackupManifest(int SchemaVersion, string OriginalPath, ContentHash ContentHash, DateTimeOffset CreatedAtUtc, string OperationId);
internal interface IStagingDirectoryCleaner { void Delete(string path); }

public sealed class BackupStore
{
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CodexPaths _paths;
    private readonly GrokPaths? _grokPaths;
    private readonly ClaudePaths? _claudePaths;
    private readonly IAtomicFileSystem _fileSystem;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _retention;
    private readonly IStagingDirectoryCleaner _stagingCleaner;

    public BackupStore(string repositoryId, string? localAppDataDirectory, CodexPaths codexPaths, IAtomicFileSystem? fileSystem = null, TimeProvider? timeProvider = null, TimeSpan? retention = null, GrokPaths? grokPaths = null, ClaudePaths? claudePaths = null)
        : this(repositoryId, localAppDataDirectory, codexPaths, fileSystem, timeProvider, retention, null, grokPaths, claudePaths) { }

    internal BackupStore(string repositoryId, string? localAppDataDirectory, CodexPaths codexPaths, IAtomicFileSystem? fileSystem, TimeProvider? timeProvider, TimeSpan? retention, IStagingDirectoryCleaner? stagingCleaner, GrokPaths? grokPaths = null, ClaudePaths? claudePaths = null)
    {
        ArgumentNullException.ThrowIfNull(codexPaths);
        PathSafety.ValidateFileComponent(repositoryId, nameof(repositoryId));
        var local = PathSafety.Canonicalize(localAppDataDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), nameof(localAppDataDirectory));
        RootPath = Path.GetFullPath(Path.Combine(local, "CodexHistorySync", "repositories", repositoryId, "backups"));
        PathSafety.EnsureOutsideCodex(RootPath, codexPaths, nameof(localAppDataDirectory), grokPaths, claudePaths);
        _paths = codexPaths;
        _grokPaths = grokPaths;
        _claudePaths = claudePaths;
        _fileSystem = fileSystem ?? new AtomicFileSystem();
        _clock = timeProvider ?? TimeProvider.System;
        _retention = retention ?? DefaultRetention;
        _stagingCleaner = stagingCleaner ?? StagingDirectoryCleaner.Instance;
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
            ValidateConcretePaths(staging, contentPath, manifestPath);
            await using (var source = OpenRead(original)) await WriteDurableAsync(contentPath, source, ct).ConfigureAwait(false);
            var originalHash = await HashFileAsync(original, ct).ConfigureAwait(false);
            var backupHash = await HashFileAsync(contentPath, ct).ConfigureAwait(false);
            if (!StringComparer.OrdinalIgnoreCase.Equals(originalHash.Hex, backupHash.Hex)) throw new IOException("The backup copy could not be verified.");
            var manifest = new BackupManifest(1, original, backupHash, created, operationId);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            await WriteDurableAsync(manifestPath, new MemoryStream(bytes, writable: false), ct).ConfigureAwait(false);
            ValidateConcretePaths(staging, contentPath, manifestPath, directory);
            Directory.Move(staging, directory);
            return Record(id, directory, manifest);
        }
        catch (Exception primary)
        {
            try { if (Directory.Exists(staging)) _stagingCleaner.Delete(staging); }
            catch (Exception cleanup)
            {
                throw new AtomicMutationException("Backup staging cleanup failed; evidence was preserved.", new AggregateException(primary, cleanup), Directory.Exists(staging) ? [staging] : []);
            }
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
            await _fileSystem.PublishAsync(temporary, backup.OriginalPath, backup.ContentHash, displaced?.ContentHash, mutationAllowed: null, ct).ConfigureAwait(false);
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

    internal async Task<BackupRecord> LoadAsync(string id, CancellationToken ct)
    {
        PathSafety.RejectReparsePoints(RootPath, nameof(RootPath));
        PathSafety.ValidateFileComponent(id, nameof(id));
        var directory = Path.GetFullPath(Path.Combine(RootPath, id));
        if (!CodexPaths.IsPathWithin(directory, RootPath)) throw new ArgumentException("Backup ID escapes the backup root.", nameof(id));
        var manifestPath = Path.Combine(directory, "manifest.json");
        var contentPath = Path.Combine(directory, "content.bin");
        ValidateConcretePaths(directory, manifestPath, contentPath);
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
        var roots = new List<string> { _paths.Sessions, _paths.ArchivedSessions, _paths.Attachments };
        if (_grokPaths is not null) roots.Add(_grokPaths.Sessions);
        if (_claudePaths is not null) roots.Add(_claudePaths.Projects);
        if (!roots.Any(root => CodexPaths.IsPathWithin(canonical, root) && !StringComparer.OrdinalIgnoreCase.Equals(canonical, Path.TrimEndingDirectorySeparator(root))))
            throw new ArgumentException("The backup source is outside synchronized history paths.", nameof(path));
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
    private sealed class StagingDirectoryCleaner : IStagingDirectoryCleaner
    {
        public static readonly StagingDirectoryCleaner Instance = new();
        public void Delete(string path) => Directory.Delete(path, recursive: true);
    }
    private static void ValidateConcretePaths(params string[] paths)
    {
        foreach (var path in paths) PathSafety.RejectReparsePoints(path, nameof(paths));
    }
}

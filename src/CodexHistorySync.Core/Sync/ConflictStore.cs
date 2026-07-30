using System.Security.Cryptography;
using System.Text.Json;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.IO;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Sync;

public sealed record ConflictProvenance(EnvelopeMetadata Metadata, ContentHash LocalHash, ContentHash RemoteHash, ContentHash BaselineHash,
    string LocalDeviceId, string RemoteDeviceId, DateTimeOffset LocalTimestampUtc, DateTimeOffset RemoteTimestampUtc,
    bool LocalDeleted = false, bool RemoteDeleted = false, EnvelopeMetadata? LocalMetadata = null,
    EnvelopeMetadata? RemoteMetadata = null);
public sealed record ConflictRecord(string Id, ConflictProvenance Provenance, string DirectoryPath, string LocalEncryptedPath, string RemoteEncryptedPath, string ManifestPath);
public enum ConflictResolution { KeepLocal, KeepRemote, ExportBoth }
public sealed record ConflictResolutionResult(string? SelectedEncryptedPath, string? LocalPlaintextPath, string? RemotePlaintextPath);
internal sealed record ConflictManifest(int SchemaVersion, ConflictProvenance Provenance, ContentHash LocalEnvelopeHash,
    ContentHash RemoteEnvelopeHash, string? Fingerprint = null);
internal interface IAtomicDirectoryPublisher { void Publish(string stagingPath, string destinationPath); }
internal interface IConflictRetirementFileSystem
{
    void Move(string source, string destination);
    void Delete(string path);
}
internal interface IConflictStoreHooks
{
    void OnAfterFirstEnvelope();
    void OnAfterFirstPlaintext();
    void OnBeforePreservePublication();
    void OnBeforeExportPublication();
}

public sealed class ConflictStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CodexPaths _paths;
    private readonly IAtomicDirectoryPublisher _publisher;
    private readonly IConflictStoreHooks _hooks;
    private readonly IStagingDirectoryCleaner _stagingCleaner;
    private readonly IConflictRetirementFileSystem _retirement;

    public ConflictStore(string repositoryId, string? localAppDataDirectory, CodexPaths codexPaths)
        : this(repositoryId, localAppDataDirectory, codexPaths, null, null, null, null) { }

    internal ConflictStore(string repositoryId, string? localAppDataDirectory, CodexPaths codexPaths,
        IAtomicDirectoryPublisher? publisher, IConflictStoreHooks? hooks, IStagingDirectoryCleaner? stagingCleaner,
        IConflictRetirementFileSystem? retirement = null)
    {
        _paths = codexPaths ?? throw new ArgumentNullException(nameof(codexPaths));
        PathSafety.ValidateFileComponent(repositoryId, nameof(repositoryId));
        var local = PathSafety.Canonicalize(localAppDataDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), nameof(localAppDataDirectory));
        RootPath = Path.GetFullPath(Path.Combine(local, "CodexHistorySync", "repositories", repositoryId, "conflicts"));
        PathSafety.EnsureOutsideCodex(RootPath, codexPaths, nameof(localAppDataDirectory));
        _publisher = publisher ?? DirectoryPublisher.Instance;
        _hooks = hooks ?? NoopConflictStoreHooks.Instance;
        _stagingCleaner = stagingCleaner ?? ConflictStagingDirectoryCleaner.Instance;
        _retirement = retirement ?? ConflictRetirementFileSystem.Instance;
    }

    public string RootPath { get; }

    public async Task<ConflictRecord> PreserveAsync(ConflictProvenance provenance, Stream localEncrypted, Stream remoteEncrypted, CancellationToken ct)
    {
        PathSafety.RejectReparsePoints(RootPath, nameof(RootPath));
        ValidateProvenance(provenance);
        ArgumentNullException.ThrowIfNull(localEncrypted);
        ArgumentNullException.ThrowIfNull(remoteEncrypted);
        var fingerprint = Fingerprint(provenance);
        if (await FindByFingerprintAsync(fingerprint, ct).ConfigureAwait(false) is { } existing) return existing;
        var id = $"{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmssfffffff'Z'}-{Guid.NewGuid():N}";
        Directory.CreateDirectory(RootPath);
        var directory = Path.Combine(RootPath, id);
        var staging = Path.Combine(RootPath, $".{id}.tmp");
        Directory.CreateDirectory(staging);
        var localPath = Path.Combine(staging, "local.encrypted");
        var remotePath = Path.Combine(staging, "remote.encrypted");
        var manifestPath = Path.Combine(staging, "manifest.json");
        try
        {
            ValidateConcretePaths(staging, localPath, remotePath, manifestPath);
            await WriteDurableAsync(localPath, localEncrypted, ct).ConfigureAwait(false);
            _hooks.OnAfterFirstEnvelope();
            ct.ThrowIfCancellationRequested();
            await WriteDurableAsync(remotePath, remoteEncrypted, ct).ConfigureAwait(false);
            var manifest = new ConflictManifest(1, provenance, await BackupStore.HashFileAsync(localPath, ct).ConfigureAwait(false),
                await BackupStore.HashFileAsync(remotePath, ct).ConfigureAwait(false), fingerprint);
            await WriteDurableAsync(manifestPath, new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions), writable: false), ct).ConfigureAwait(false);
            ValidateConcretePaths(staging, localPath, remotePath, manifestPath, directory);
            _hooks.OnBeforePreservePublication();
            ct.ThrowIfCancellationRequested();
            _publisher.Publish(staging, directory);
            return new ConflictRecord(id, provenance, directory, Path.Combine(directory, "local.encrypted"), Path.Combine(directory, "remote.encrypted"), Path.Combine(directory, "manifest.json"));
        }
        catch (Exception primary)
        {
            try { if (Directory.Exists(staging)) _stagingCleaner.Delete(staging); }
            catch (Exception cleanup)
            {
                throw new AtomicMutationException("Conflict staging cleanup failed; evidence was preserved.", new AggregateException(primary, cleanup), Directory.Exists(staging) ? [staging] : []);
            }
            throw;
        }
    }

    public async Task<IReadOnlyList<ConflictRecord>> ListAsync(CancellationToken ct)
    {
        PathSafety.RejectReparsePoints(RootPath, nameof(RootPath));
        if (!Directory.Exists(RootPath)) return [];
        CleanupRetiredBestEffort();
        var records = new List<ConflictRecord>();
        foreach (var directory in Directory.EnumerateDirectories(RootPath).Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal)).OrderBy(x => x, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            records.Add(await LoadAsync(Path.GetFileName(directory), ct).ConfigureAwait(false));
        }
        return records;
    }

    public async Task<ConflictResolutionResult> ResolveAsync(string conflictId, ConflictResolution resolution, string? destination, RepositoryCrypto crypto, ReadOnlyMemory<byte> masterKey, CancellationToken ct)
    {
        PathSafety.RejectReparsePoints(RootPath, nameof(RootPath));
        var conflict = await LoadAsync(conflictId, ct).ConfigureAwait(false);
        if (!BackupStore.HashEquals(await BackupStore.HashFileAsync(conflict.LocalEncryptedPath, ct).ConfigureAwait(false), await ReadManifestHashAsync(conflict.ManifestPath, true, ct).ConfigureAwait(false)) ||
            !BackupStore.HashEquals(await BackupStore.HashFileAsync(conflict.RemoteEncryptedPath, ct).ConfigureAwait(false), await ReadManifestHashAsync(conflict.ManifestPath, false, ct).ConfigureAwait(false)))
            throw new InvalidDataException("Conflict envelope hash verification failed.");
        if (resolution == ConflictResolution.KeepLocal) return new(conflict.LocalEncryptedPath, null, null);
        if (resolution == ConflictResolution.KeepRemote) return new(conflict.RemoteEncryptedPath, null, null);
        ArgumentNullException.ThrowIfNull(crypto);
        var target = PathSafety.Canonicalize(destination ?? throw new ArgumentNullException(nameof(destination)), nameof(destination), requireFullyQualified: true);
        PathSafety.EnsureOutsideCodex(target, _paths, nameof(destination));
        if (CodexPaths.IsPathWithin(target, RootPath) || CodexPaths.IsPathWithin(RootPath, target)) throw new ArgumentException("Conflict exports must be outside conflict storage.", nameof(destination));
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("The conflict export destination already exists.");
        var parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("The export destination has no parent.", nameof(destination));
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The conflict export parent directory does not exist.");
        var staging = Path.Combine(parent, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(staging);
        var localOutput = Path.Combine(staging, $"{conflict.Id}.local.jsonl");
        var remoteOutput = Path.Combine(staging, $"{conflict.Id}.remote.jsonl");
        try
        {
            ValidateConcretePaths(staging, localOutput, remoteOutput);
            await DecryptDurablyAsync(crypto, conflict.LocalEncryptedPath, localOutput, masterKey,
                LocalMetadata(conflict.Provenance), ct).ConfigureAwait(false);
            _hooks.OnAfterFirstPlaintext();
            ct.ThrowIfCancellationRequested();
            await DecryptDurablyAsync(crypto, conflict.RemoteEncryptedPath, remoteOutput, masterKey,
                RemoteMetadata(conflict.Provenance), ct).ConfigureAwait(false);
            ValidateConcretePaths(staging, localOutput, remoteOutput, target);
            _hooks.OnBeforeExportPublication();
            ct.ThrowIfCancellationRequested();
            _publisher.Publish(staging, target);
            return new(null, Path.Combine(target, Path.GetFileName(localOutput)), Path.Combine(target, Path.GetFileName(remoteOutput)));
        }
        catch (Exception primary)
        {
            try { if (Directory.Exists(staging)) _stagingCleaner.Delete(staging); }
            catch (Exception cleanup)
            {
                throw new AtomicMutationException("Plaintext export staging cleanup failed; evidence was preserved.", new AggregateException(primary, cleanup), Directory.Exists(staging) ? [staging] : []);
            }
            throw;
        }
    }

    private async Task<ConflictRecord> LoadAsync(string id, CancellationToken ct)
    {
        PathSafety.RejectReparsePoints(RootPath, nameof(RootPath));
        PathSafety.ValidateFileComponent(id, nameof(id));
        var directory = Path.GetFullPath(Path.Combine(RootPath, id));
        if (!CodexPaths.IsPathWithin(directory, RootPath)) throw new ArgumentException("Conflict ID escapes the conflict root.", nameof(id));
        var manifestPath = Path.Combine(directory, "manifest.json");
        var localPath = Path.Combine(directory, "local.encrypted");
        var remotePath = Path.Combine(directory, "remote.encrypted");
        ValidateConcretePaths(directory, manifestPath, localPath, remotePath);
        await using var input = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        var manifest = await JsonSerializer.DeserializeAsync<ConflictManifest>(input, JsonOptions, ct).ConfigureAwait(false) ?? throw new InvalidDataException("Conflict manifest is empty.");
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("Conflict manifest schema is unsupported.");
        ValidateProvenance(manifest.Provenance);
        return new(id, manifest.Provenance, directory, localPath, remotePath, manifestPath);
    }

    public async Task RetireAsync(string conflictId, CancellationToken ct)
    {
        PathSafety.RejectReparsePoints(RootPath, nameof(RootPath));
        var conflict = await LoadAsync(conflictId, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        ValidateConcretePaths(conflict.DirectoryPath, conflict.LocalEncryptedPath, conflict.RemoteEncryptedPath,
            conflict.ManifestPath);
        ValidateCompleteEvidenceDirectory(conflict.DirectoryPath);
        var retired = Path.Combine(RootPath, $".{conflict.Id}.resolved-{Guid.NewGuid():N}");
        if (Directory.Exists(retired) || File.Exists(retired))
            throw new IOException("The retired conflict destination already exists.");
        PathSafety.RejectReparsePoints(retired, nameof(retired));
        _retirement.Move(conflict.DirectoryPath, retired);
        TryDeleteRetired(retired);
    }

    private void CleanupRetiredBestEffort()
    {
        foreach (var directory in Directory.EnumerateDirectories(RootPath)
                     .Where(path => Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal) &&
                         Path.GetFileName(path).Contains(".resolved-", StringComparison.Ordinal)))
            TryDeleteRetired(directory);
    }

    private void TryDeleteRetired(string directory)
    {
        try
        {
            ValidateCompleteEvidenceDirectory(directory);
            _retirement.Delete(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                             ArgumentException or System.Security.SecurityException)
        {
        }
    }

    private static void ValidateCompleteEvidenceDirectory(string directory)
    {
        ValidateConcretePaths(directory);
        if ((File.GetAttributes(directory) & FileAttributes.Directory) == 0)
            throw new IOException("The conflict evidence target is not a directory.");
        if (Directory.EnumerateDirectories(directory).Any() ||
            Directory.EnumerateFiles(directory).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal)
                .SetEquals(["local.encrypted", "remote.encrypted", "manifest.json"]) is false)
            throw new IOException("The conflict evidence directory contains unexpected entries.");
        ValidateConcretePaths(Path.Combine(directory, "local.encrypted"),
            Path.Combine(directory, "remote.encrypted"), Path.Combine(directory, "manifest.json"));
    }

    private async Task<ConflictRecord?> FindByFingerprintAsync(string fingerprint, CancellationToken ct)
    {
        if (!Directory.Exists(RootPath)) return null;
        foreach (var directory in Directory.EnumerateDirectories(RootPath)
                     .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileName(directory);
            var record = await LoadAsync(id, ct).ConfigureAwait(false);
            await using var input = new FileStream(record.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var manifest = await JsonSerializer.DeserializeAsync<ConflictManifest>(input, JsonOptions, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("Conflict manifest is empty.");
            if (StringComparer.Ordinal.Equals(manifest.Fingerprint ?? Fingerprint(manifest.Provenance), fingerprint)) return record;
        }
        return null;
    }

    private static string Fingerprint(ConflictProvenance provenance)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            localSchemaVersion = LocalMetadata(provenance).SchemaVersion,
            remoteSchemaVersion = RemoteMetadata(provenance).SchemaVersion,
            objectId = provenance.Metadata.ObjectId.Value,
            localKind = (int)LocalMetadata(provenance).Kind,
            remoteKind = (int)RemoteMetadata(provenance).Kind,
            localHash = provenance.LocalHash.Hex,
            remoteHash = provenance.RemoteHash.Hex,
            baselineHash = provenance.BaselineHash.Hex,
            provenance.LocalDeviceId,
            provenance.RemoteDeviceId,
            provenance.LocalDeleted,
            provenance.RemoteDeleted
        }, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    public static string GetIdentity(ConflictProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        ValidateProvenance(provenance);
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            localSchemaVersion = LocalMetadata(provenance).SchemaVersion,
            remoteSchemaVersion = RemoteMetadata(provenance).SchemaVersion,
            objectId = provenance.Metadata.ObjectId.Value,
            localKind = (int)LocalMetadata(provenance).Kind,
            remoteKind = (int)RemoteMetadata(provenance).Kind,
            localHash = provenance.LocalHash.Hex,
            remoteHash = provenance.RemoteHash.Hex,
            baselineHash = provenance.BaselineHash.Hex,
            provenance.LocalDeleted,
            provenance.RemoteDeleted
        }, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    private static void ValidateProvenance(ConflictProvenance value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(value.Metadata);
        PathSafety.ValidateFileComponent(value.Metadata.ObjectId.Value, nameof(value.Metadata.ObjectId));
        PathSafety.ValidateFileComponent(value.LocalDeviceId, nameof(value.LocalDeviceId));
        PathSafety.ValidateFileComponent(value.RemoteDeviceId, nameof(value.RemoteDeviceId));
        var local = LocalMetadata(value);
        var remote = RemoteMetadata(value);
        if (local.SchemaVersion < 1 || remote.SchemaVersion < 1 || !Enum.IsDefined(local.Kind) ||
            !Enum.IsDefined(remote.Kind) || local.ObjectId != value.Metadata.ObjectId ||
            remote.ObjectId != value.Metadata.ObjectId)
            throw new ArgumentException("Conflict envelope metadata is invalid.", nameof(value));
    }

    internal static EnvelopeMetadata LocalMetadata(ConflictProvenance provenance) =>
        provenance.LocalMetadata ?? provenance.Metadata;

    internal static EnvelopeMetadata RemoteMetadata(ConflictProvenance provenance) =>
        provenance.RemoteMetadata ?? provenance.Metadata;

    private static async Task WriteDurableAsync(string path, Stream input, CancellationToken ct)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
        output.Flush(true);
    }

    private static async Task DecryptDurablyAsync(RepositoryCrypto crypto, string encryptedPath, string outputPath, ReadOnlyMemory<byte> masterKey, EnvelopeMetadata metadata, CancellationToken ct)
    {
        await using var encrypted = new FileStream(encryptedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous);
        await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await crypto.DecryptAsync(encrypted, output, masterKey, metadata, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
        output.Flush(true);
    }

    private static async Task<ContentHash> ReadManifestHashAsync(string manifestPath, bool local, CancellationToken ct)
    {
        await using var input = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        var manifest = await JsonSerializer.DeserializeAsync<ConflictManifest>(input, JsonOptions, ct).ConfigureAwait(false) ?? throw new InvalidDataException("Conflict manifest is empty.");
        return local ? manifest.LocalEnvelopeHash : manifest.RemoteEnvelopeHash;
    }

    private static void ValidateConcretePaths(params string[] paths)
    {
        foreach (var path in paths) PathSafety.RejectReparsePoints(path, nameof(paths));
    }

    private sealed class DirectoryPublisher : IAtomicDirectoryPublisher
    {
        public static readonly DirectoryPublisher Instance = new();
        public void Publish(string stagingPath, string destinationPath)
        {
            PathSafety.RejectReparsePoints(stagingPath, nameof(stagingPath));
            PathSafety.RejectReparsePoints(destinationPath, nameof(destinationPath));
            Directory.Move(stagingPath, destinationPath);
        }
    }

    private sealed class NoopConflictStoreHooks : IConflictStoreHooks
    {
        public static readonly NoopConflictStoreHooks Instance = new();
        public void OnAfterFirstEnvelope() { }
        public void OnAfterFirstPlaintext() { }
        public void OnBeforePreservePublication() { }
        public void OnBeforeExportPublication() { }
    }

    private sealed class ConflictStagingDirectoryCleaner : IStagingDirectoryCleaner
    {
        public static readonly ConflictStagingDirectoryCleaner Instance = new();
        public void Delete(string path) => Directory.Delete(path, recursive: true);
    }

    private sealed class ConflictRetirementFileSystem : IConflictRetirementFileSystem
    {
        public static readonly ConflictRetirementFileSystem Instance = new();
        public void Move(string source, string destination) => Directory.Move(source, destination);
        public void Delete(string path) => Directory.Delete(path, recursive: true);
    }
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.IO;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Providers;
using CodexHistorySync.Core.State;

namespace CodexHistorySync.Core.Sync;

public enum SyncMode { Pull, Push, Bidirectional }

public sealed record SyncResult(string RemoteRevision, int Uploaded, int Downloaded, int Deleted, int Conflicts, bool RemoteChangedDuringAttempt);

public sealed class SyncConcurrencyException : InvalidOperationException
{
    public SyncConcurrencyException() : base("The remote repository changed during all five synchronization attempts.") { }
}

public sealed class SyncEngine
{
    private const int IndexSchemaVersion = 1;
    private const string IndexObjectId = "__repository_index__";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly EnvelopeMetadata IndexMetadata = new(IndexSchemaVersion, new LogicalObjectId(IndexObjectId), ObjectKind.RepositoryIndex);
    private static readonly Regex HashPattern = new("^[a-f0-9]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepositoryMutexes = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _repositoryId;
    private readonly string _deviceId;
    private readonly CodexPaths _paths;
    private readonly byte[] _masterKey;
    private readonly SessionScanner _scanner;
    private readonly RepositoryCrypto _crypto;
    private readonly LocalStateStore _stateStore;
    private readonly CodexHistoryWriter _historyWriter;
    private readonly ConflictStore _conflictStore;
    private readonly IStorageProvider _provider;
    private readonly string _stagingRoot;
    private readonly SemaphoreSlim _mutex;

    public SyncEngine(string repositoryId, string deviceId, CodexPaths paths, ReadOnlyMemory<byte> masterKey,
        SessionScanner scanner, RepositoryCrypto crypto, LocalStateStore stateStore, CodexHistoryWriter historyWriter,
        ConflictStore conflictStore, IStorageProvider provider, string stagingDirectory)
    {
        if (string.IsNullOrWhiteSpace(repositoryId)) throw new ArgumentException("Repository ID is required.", nameof(repositoryId));
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId is "." or ".." || deviceId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || deviceId.Contains('/') || deviceId.Contains('\\'))
            throw new ArgumentException("Device ID is invalid.", nameof(deviceId));
        if (masterKey.Length != RepositoryCrypto.MasterKeySize) throw new ArgumentException("The repository key has an invalid length.", nameof(masterKey));
        _repositoryId = repositoryId;
        _deviceId = deviceId;
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _masterKey = masterKey.ToArray();
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _historyWriter = historyWriter ?? throw new ArgumentNullException(nameof(historyWriter));
        _conflictStore = conflictStore ?? throw new ArgumentNullException(nameof(conflictStore));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        if (string.IsNullOrWhiteSpace(stagingDirectory)) throw new ArgumentException("A staging directory is required.", nameof(stagingDirectory));
        _stagingRoot = PathSafety.Canonicalize(stagingDirectory, nameof(stagingDirectory));
        PathSafety.EnsureOutsideCodex(_stagingRoot, paths, nameof(stagingDirectory));
        _mutex = RepositoryMutexes.GetOrAdd(Path.GetFullPath(_stateStore.GetStatePath(repositoryId)), _ => new SemaphoreSlim(1, 1));
    }

    public async Task<SyncResult> SynchronizeAsync(SyncMode mode, CancellationToken ct)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var raced = false;
            var preservedConflicts = new HashSet<ConflictFingerprint>();
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                var locals = await _scanner.ScanAsync(_paths, ct).ConfigureAwait(false);
                var snapshot = await _provider.ReadSnapshotAsync(ct).ConfigureAwait(false);
                var remote = await AuthenticateSnapshotAsync(snapshot, ct).ConfigureAwait(false);
                var baseline = await LoadBaselineAsync(ct).ConfigureAwait(false);
                var localVersions = CreateLocalVersions(locals, baseline);
                var plan = ThreeWayPlanner.CreatePlan(localVersions, remote.Versions, baseline);
                var operationId = Guid.NewGuid().ToString("N");
                var directory = Path.Combine(_stagingRoot, operationId);
                Directory.CreateDirectory(directory);
                try
                {
                    var successful = new Dictionary<LogicalObjectId, ObjectVersion>();
                    var deferred = new HashSet<LogicalObjectId>();
                    var entries = remote.Entries.ToDictionary(entry => entry.Id);
                    var changes = new List<EncryptedObjectChange>();
                    var attemptUploads = 0;
                    var attemptDownloaded = 0;
                    var attemptDeleted = 0;
                    var attemptConflicts = 0;
                    var stagedImports = new Dictionary<LogicalObjectId, StagedImport>();
                    foreach (var action in plan.Actions.Where(action => action.Kind == SyncActionKind.Download && mode != SyncMode.Push))
                        stagedImports.Add(action.ObjectId, await StageDownloadAsync(action, locals, remote, directory, ct).ConfigureAwait(false));

                    foreach (var action in plan.Actions.Where(action => action.Kind == SyncActionKind.Conflict))
                    {
                        if (preservedConflicts.Add(new ConflictFingerprint(action.ObjectId, action.Local, action.Remote, action.Baseline)))
                            await PreserveConflictAsync(action, locals, remote, ct).ConfigureAwait(false);
                        attemptConflicts++;
                        deferred.Add(action.ObjectId);
                    }

                    foreach (var action in plan.Actions)
                    {
                        switch (action.Kind)
                        {
                            case SyncActionKind.Accept:
                                if ((action.Remote ?? action.Local) is { } accepted) successful[action.ObjectId] = accepted;
                                break;
                            case SyncActionKind.Download when mode != SyncMode.Push:
                                successful[action.ObjectId] = action.Remote!;
                                break;
                            case SyncActionKind.ApplyTombstone when mode != SyncMode.Push:
                                successful[action.ObjectId] = action.Remote!;
                                break;
                            case SyncActionKind.Upload when mode != SyncMode.Pull:
                            {
                                var source = locals.Single(item => item.Id == action.ObjectId);
                                var entry = await StageEncryptedObjectAsync(source.Id, source.Kind, source.Hash, false, source.SourcePath, directory, ct).ConfigureAwait(false);
                                entries[entry.Id] = entry;
                                changes.Add(new EncryptedObjectChange(new LogicalObjectId(entry.OpaqueObjectId), entry.StagedPath!, false));
                                successful[action.ObjectId] = Version(entry);
                                attemptUploads++;
                                break;
                            }
                            case SyncActionKind.PublishTombstone when mode != SyncMode.Pull:
                            {
                                var previous = action.Baseline ?? action.Remote ?? throw new InvalidDataException("A tombstone has no prior version.");
                                var entry = await StageEncryptedObjectAsync(previous.Id, previous.Kind, EmptyHash(), true, null, directory, ct).ConfigureAwait(false);
                                entries[entry.Id] = entry;
                                changes.Add(new EncryptedObjectChange(new LogicalObjectId(entry.OpaqueObjectId), entry.StagedPath!, false));
                                successful[action.ObjectId] = Version(entry);
                                attemptUploads++;
                                break;
                            }
                            case SyncActionKind.Conflict:
                                break;
                            default:
                                deferred.Add(action.ObjectId);
                                break;
                        }
                    }

                    var revision = snapshot.Revision;
                    if (changes.Count != 0)
                    {
                        var referenced = entries.Values.Select(entry => entry.OpaqueObjectId).ToHashSet(StringComparer.Ordinal);
                        foreach (var old in remote.Entries.Where(entry => !referenced.Contains(entry.OpaqueObjectId)))
                            changes.Add(new EncryptedObjectChange(new LogicalObjectId(old.OpaqueObjectId), string.Empty, true));
                        var indexPath = await StageIndexAsync(entries.Values, directory, ct).ConfigureAwait(false);
                        var result = await _provider.TryPublishAsync(new PublishRequest(snapshot.Revision,
                            new EncryptedIndexChange(indexPath, false), changes, "Synchronize encrypted Codex history"), ct).ConfigureAwait(false);
                        if (!result.Published)
                        {
                            raced = true;
                            if (attempt == 5) throw new SyncConcurrencyException();
                            continue;
                        }
                        revision = result.CurrentRevision;
                    }

                    foreach (var action in plan.Actions)
                    {
                        if (action.Kind == SyncActionKind.Download && mode != SyncMode.Push)
                        {
                            await ApplyStagedImportAsync(stagedImports[action.ObjectId], operationId, ct).ConfigureAwait(false);
                            attemptDownloaded++;
                        }
                        else if (action.Kind == SyncActionKind.ApplyTombstone && mode != SyncMode.Push && action.Local is not null)
                        {
                            var source = locals.Single(item => item.Id == action.ObjectId);
                            if (await _historyWriter.ApplyTombstoneAsync(source, action.Baseline!.PlaintextHash, operationId, ct).ConfigureAwait(false) == TombstoneApplyResult.Conflict)
                            {
                                successful.Remove(action.ObjectId);
                                deferred.Add(action.ObjectId);
                                var refreshed = await _scanner.ScanAsync(_paths, ct).ConfigureAwait(false);
                                var current = refreshed.SingleOrDefault(item => item.Id == action.ObjectId)
                                    ?? throw new InvalidDataException("The concurrent local tombstone conflict could not be scanned stably.");
                                var conflict = action with
                                {
                                    Kind = SyncActionKind.Conflict,
                                    Local = new ObjectVersion(current.Id, current.Kind, current.Hash, "local:" + current.Hash.Hex, false)
                                };
                                if (preservedConflicts.Add(new ConflictFingerprint(conflict.ObjectId, conflict.Local, conflict.Remote, conflict.Baseline)))
                                    await PreserveConflictAsync(conflict, refreshed, remote, ct).ConfigureAwait(false);
                                attemptConflicts++;
                                continue;
                            }
                            attemptDeleted++;
                        }
                    }

                    var next = baseline.ToDictionary(pair => pair.Key, pair => pair.Value);
                    foreach (var action in plan.Actions)
                        if (!deferred.Contains(action.ObjectId) && successful.TryGetValue(action.ObjectId, out var version)) next[action.ObjectId] = version;
                    await _stateStore.SaveAsync(new DeviceState(LocalStateStore.CurrentSchemaVersion, _repositoryId,
                        next.Values.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToArray()), ct).ConfigureAwait(false);
                    return new SyncResult(revision, attemptUploads, attemptDownloaded, attemptDeleted, attemptConflicts, raced);
                }
                finally
                {
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
            }
            throw new SyncConcurrencyException();
        }
        finally { _mutex.Release(); }
    }

    private async Task<Dictionary<LogicalObjectId, ObjectVersion>> LoadBaselineAsync(CancellationToken ct)
    {
        try { return (await _stateStore.LoadAsync(_repositoryId, ct).ConfigureAwait(false)).Objects.ToDictionary(value => value.Id); }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }

    private static Dictionary<LogicalObjectId, ObjectVersion> CreateLocalVersions(
        IReadOnlyList<LocalObject> objects, IReadOnlyDictionary<LogicalObjectId, ObjectVersion> baseline)
    {
        if (objects.Any(item => StringComparer.Ordinal.Equals(item.Id.Value, IndexObjectId)))
            throw new InvalidDataException("Local history uses the reserved repository-index object ID.");
        var result = objects.ToDictionary(item => item.Id,
            item => new ObjectVersion(item.Id, item.Kind, item.Hash, "local:" + item.Hash.Hex, false));
        foreach (var missing in baseline.Values.Where(item => !result.ContainsKey(item.Id)))
            result[missing.Id] = new ObjectVersion(missing.Id, missing.Kind, EmptyHash(), "local:deleted", true);
        return result;
    }

    private async Task<AuthenticatedSnapshot> AuthenticateSnapshotAsync(RemoteSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.IndexCiphertext is null)
        {
            if (snapshot.Objects.Count != 0) throw new InvalidDataException("Remote ciphertext exists without an authenticated repository index.");
            return new([], new Dictionary<LogicalObjectId, ObjectVersion>(), new Dictionary<LogicalObjectId, byte[]>(), new Dictionary<string, byte[]>(StringComparer.Ordinal));
        }
        byte[] plaintext;
        try
        {
            await using var input = new MemoryStream(snapshot.IndexCiphertext, false);
            await using var output = new MemoryStream();
            await _crypto.DecryptAsync(input, output, _masterKey, IndexMetadata, ct).ConfigureAwait(false);
            plaintext = output.ToArray();
        }
        catch (CryptographicException exception) { throw new InvalidDataException("The repository index could not be authenticated.", exception); }

        var entries = ParseIndex(plaintext);
        Dictionary<string, byte[]> encrypted;
        try { encrypted = snapshot.Objects.ToDictionary(item => item.ObjectId.Value, item => item.Ciphertext, StringComparer.Ordinal); }
        catch (ArgumentException exception) { throw new InvalidDataException("Remote snapshot contains duplicate opaque object references.", exception); }
        var referenced = entries.Select(entry => entry.OpaqueObjectId).ToHashSet(StringComparer.Ordinal);
        if (referenced.Count != entries.Count) throw new InvalidDataException("Repository index contains duplicate opaque object references.");
        if (!referenced.SetEquals(encrypted.Keys)) throw new InvalidDataException("Remote ciphertext set does not exactly match the authenticated index.");

        var versions = new Dictionary<LogicalObjectId, ObjectVersion>();
        var objects = new Dictionary<LogicalObjectId, byte[]>();
        foreach (var entry in entries)
        {
            var ciphertext = encrypted[entry.OpaqueObjectId];
            if (!StringComparer.Ordinal.Equals(Sha256(ciphertext).Hex, entry.OpaqueObjectId))
                throw new InvalidDataException("Encrypted object ID does not match its ciphertext hash.");
            byte[] bytes;
            try
            {
                await using var input = new MemoryStream(ciphertext, false);
                await using var output = new MemoryStream();
                await _crypto.DecryptAsync(input, output, _masterKey, new EnvelopeMetadata(IndexSchemaVersion, entry.Id, entry.Kind), ct).ConfigureAwait(false);
                bytes = output.ToArray();
            }
            catch (CryptographicException exception) { throw new InvalidDataException($"Encrypted object '{entry.Id.Value}' could not be authenticated.", exception); }
            if (!StringComparer.Ordinal.Equals(Sha256(bytes).Hex, entry.PlaintextHash.Hex))
                throw new InvalidDataException("Encrypted object plaintext hash does not match the authenticated index.");
            versions.Add(entry.Id, Version(entry));
            objects.Add(entry.Id, bytes);
        }
        return new(entries, versions, objects, encrypted);
    }

    private IReadOnlyList<IndexEntry> ParseIndex(byte[] plaintext)
    {
        try
        {
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("schemaVersion").GetInt32() != IndexSchemaVersion)
                throw new InvalidDataException("Repository index schema is unsupported.");
            if (!StringComparer.Ordinal.Equals(root.GetProperty("repositoryId").GetString(), _repositoryId))
                throw new InvalidDataException("Repository index belongs to a different repository.");
            var result = new List<IndexEntry>();
            var ids = new HashSet<LogicalObjectId>();
            string? previousId = null;
            foreach (var value in root.GetProperty("objects").EnumerateArray())
            {
                var idText = value.GetProperty("id").GetString();
                var hash = value.GetProperty("plaintextHash").GetString();
                var opaque = value.GetProperty("opaqueObjectId").GetString();
                var version = value.GetProperty("version").GetString();
                var rawKind = value.GetProperty("kind").GetInt32();
                if (string.IsNullOrWhiteSpace(idText) || StringComparer.Ordinal.Equals(idText, IndexObjectId) || Path.IsPathRooted(idText) || idText.Contains('/') || idText.Contains('\\') || idText is "." or "..")
                    throw new InvalidDataException("Repository index contains an invalid logical object ID.");
                if (!Enum.IsDefined(typeof(ObjectKind), rawKind) || (ObjectKind)rawKind is ObjectKind.RepositoryIndex or ObjectKind.Tombstone)
                    throw new InvalidDataException("Repository index contains an invalid object kind.");
                if (hash is null || !HashPattern.IsMatch(hash) || opaque is null || !HashPattern.IsMatch(opaque) || string.IsNullOrWhiteSpace(version))
                    throw new InvalidDataException("Repository index contains invalid object metadata.");
                var entry = new IndexEntry(new LogicalObjectId(idText), (ObjectKind)rawKind, new ContentHash(hash),
                    value.GetProperty("deleted").GetBoolean(), opaque, version);
                if (!ids.Add(entry.Id)) throw new InvalidDataException("Repository index contains duplicate logical object IDs.");
                if (previousId is not null && StringComparer.Ordinal.Compare(previousId, entry.Id.Value) >= 0)
                    throw new InvalidDataException("Repository index entries are not in canonical logical-ID order.");
                previousId = entry.Id.Value;
                result.Add(entry);
            }
            return result;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new InvalidDataException("Repository index is malformed.", exception); }
    }

    private async Task<StagedImport> StageDownloadAsync(SyncAction action, IReadOnlyList<LocalObject> locals,
        AuthenticatedSnapshot remote, string directory, CancellationToken ct)
    {
        var version = action.Remote!;
        var existing = locals.SingleOrDefault(item => item.Id == action.ObjectId);
        var root = version.Kind switch
        {
            ObjectKind.ActiveSession => _paths.Sessions,
            ObjectKind.ArchivedSession => _paths.ArchivedSessions,
            _ => throw new InvalidDataException("Only session history can be imported.")
        };
        var path = existing?.SourcePath ?? Path.Combine(root, action.ObjectId.Value + ".jsonl");
        var stagedDirectory = Path.Combine(directory, "downloads");
        Directory.CreateDirectory(stagedDirectory);
        var stagedPath = Path.Combine(stagedDirectory, Guid.NewGuid().ToString("N") + ".jsonl");
        var plaintext = remote.Plaintext[action.ObjectId];
        await using (var output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await output.WriteAsync(plaintext, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        var staged = await File.ReadAllBytesAsync(stagedPath, ct).ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(Sha256(staged).Hex, version.PlaintextHash.Hex))
            throw new InvalidDataException("Staged download does not match its authenticated plaintext hash.");
        ValidateSessionJsonl(staged, action.ObjectId, ct);
        var incoming = new LocalObject(action.ObjectId, version.Kind, path, version.PlaintextHash,
            staged.LongLength, DateTimeOffset.UtcNow);
        return new StagedImport(incoming, stagedPath);
    }

    private async Task ApplyStagedImportAsync(StagedImport staged, string operationId, CancellationToken ct)
    {
        await using var content = new FileStream(staged.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await _historyWriter.ImportAsync(staged.Incoming, content, operationId, ct).ConfigureAwait(false);
    }

    private static void ValidateSessionJsonl(byte[] bytes, LogicalObjectId expectedId, CancellationToken ct)
    {
        if (bytes.Length == 0 || bytes[^1] != (byte)'\n')
            throw new InvalidDataException("Session JSONL must be complete through its final newline.");
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException exception) { throw new InvalidDataException("Session JSONL is not strict UTF-8.", exception); }
        LogicalObjectId? found = null;
        try
        {
            using var reader = new StringReader(text);
            while (reader.ReadLine() is { } line)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Every JSONL record must be an object.");
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "session_meta") continue;
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object ||
                    !payload.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Session metadata has no valid ID.");
                var value = id.GetString();
                if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Contains('/') || value.Contains('\\') || value is "." or "..")
                    throw new InvalidDataException("Session ID is unsafe.");
                var parsed = new LogicalObjectId(value);
                if (found is not null && found.Value != parsed)
                    throw new InvalidDataException("Session JSONL contains inconsistent IDs.");
                found = parsed;
            }
        }
        catch (JsonException exception) { throw new InvalidDataException("Session JSONL contains malformed JSON.", exception); }
        if (found is null || found.Value != expectedId)
            throw new InvalidDataException("Session JSONL ID does not match its logical object ID.");
    }

    private async Task<IndexEntry> StageEncryptedObjectAsync(LogicalObjectId id, ObjectKind kind, ContentHash hash,
        bool deleted, string? sourcePath, string directory, CancellationToken ct)
    {
        var plaintext = sourcePath is null ? Array.Empty<byte>() : await File.ReadAllBytesAsync(sourcePath, ct).ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(Sha256(plaintext).Hex, hash.Hex)) throw new InvalidDataException("Local object changed after stable scanning.");
        await using var input = new MemoryStream(plaintext, false);
        await using var output = new MemoryStream();
        await _crypto.EncryptAsync(input, output, _masterKey, new EnvelopeMetadata(IndexSchemaVersion, id, kind), ct).ConfigureAwait(false);
        var ciphertext = output.ToArray();
        var opaque = Sha256(ciphertext).Hex;
        var path = Path.Combine(directory, opaque + ".chs");
        await File.WriteAllBytesAsync(path, ciphertext, ct).ConfigureAwait(false);
        return new(id, kind, hash, deleted, opaque, _deviceId + ":" + Guid.NewGuid().ToString("N"), path);
    }

    private async Task<string> StageIndexAsync(IEnumerable<IndexEntry> entries, string directory, CancellationToken ct)
    {
        await using var json = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(json))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", IndexSchemaVersion);
            writer.WriteString("repositoryId", _repositoryId);
            writer.WriteStartArray("objects");
            foreach (var entry in entries.OrderBy(item => item.Id.Value, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", entry.Id.Value);
                writer.WriteNumber("kind", (int)entry.Kind);
                writer.WriteString("plaintextHash", entry.PlaintextHash.Hex);
                writer.WriteBoolean("deleted", entry.Deleted);
                writer.WriteString("opaqueObjectId", entry.OpaqueObjectId);
                writer.WriteString("version", entry.Version);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            await writer.FlushAsync(ct).ConfigureAwait(false);
        }
        json.Position = 0;
        var path = Path.Combine(directory, "repository.chs");
        await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await _crypto.EncryptAsync(json, destination, _masterKey, IndexMetadata, ct).ConfigureAwait(false);
        await destination.FlushAsync(ct).ConfigureAwait(false);
        return path;
    }

    private async Task PreserveConflictAsync(SyncAction action, IReadOnlyList<LocalObject> locals, AuthenticatedSnapshot remote, CancellationToken ct)
    {
        var local = locals.SingleOrDefault(item => item.Id == action.ObjectId);
        var localBytes = local is null ? Array.Empty<byte>() : await File.ReadAllBytesAsync(local.SourcePath, ct).ConfigureAwait(false);
        if (action.Local is not null && !StringComparer.Ordinal.Equals(Sha256(localBytes).Hex, action.Local.PlaintextHash.Hex))
            throw new InvalidDataException("Local conflict version changed after stable scanning.");
        await using var localPlaintext = new MemoryStream(localBytes, false);
        await using var localEncrypted = new MemoryStream();
        var kind = action.Remote?.Kind ?? action.Local?.Kind ?? action.Baseline?.Kind
            ?? throw new InvalidDataException("A conflict has no object kind.");
        var metadata = new EnvelopeMetadata(IndexSchemaVersion, action.ObjectId, kind);
        await _crypto.EncryptAsync(localPlaintext, localEncrypted, _masterKey, metadata, ct).ConfigureAwait(false);
        localEncrypted.Position = 0;
        var empty = EmptyHash();
        await using var remoteEncrypted = new MemoryStream();
        string remoteDevice;
        if (action.Remote is not null)
        {
            var remoteEntry = remote.Entries.Single(entry => entry.Id == action.ObjectId);
            await remoteEncrypted.WriteAsync(remote.Encrypted[remoteEntry.OpaqueObjectId], ct).ConfigureAwait(false);
            remoteDevice = DeviceFromVersion(remoteEntry.Version);
        }
        else
        {
            await using var remotePlaintext = new MemoryStream(Array.Empty<byte>(), false);
            await _crypto.EncryptAsync(remotePlaintext, remoteEncrypted, _masterKey, metadata, ct).ConfigureAwait(false);
            remoteDevice = "remote";
        }
        remoteEncrypted.Position = 0;
        var provenance = new ConflictProvenance(metadata, action.Local?.PlaintextHash ?? empty, action.Remote?.PlaintextHash ?? empty,
            action.Baseline?.PlaintextHash ?? empty, _deviceId, remoteDevice, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await _conflictStore.PreserveAsync(provenance, localEncrypted, remoteEncrypted, ct).ConfigureAwait(false);
    }

    private static ObjectVersion Version(IndexEntry entry) => new(entry.Id, entry.Kind, entry.PlaintextHash, entry.Version, entry.Deleted);
    private static ContentHash Sha256(byte[] bytes) => new(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    private static ContentHash EmptyHash() => Sha256([]);
    private static string DeviceFromVersion(string version)
    {
        var separator = version.IndexOf(':');
        var value = separator > 0 ? version[..separator] : "remote";
        return string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "remote" : value;
    }

    private sealed record IndexEntry(LogicalObjectId Id, ObjectKind Kind, ContentHash PlaintextHash, bool Deleted,
        string OpaqueObjectId, string Version, string? StagedPath = null);
    private sealed record AuthenticatedSnapshot(IReadOnlyList<IndexEntry> Entries,
        IReadOnlyDictionary<LogicalObjectId, ObjectVersion> Versions,
        IReadOnlyDictionary<LogicalObjectId, byte[]> Plaintext,
        IReadOnlyDictionary<string, byte[]> Encrypted);
    private sealed record StagedImport(LocalObject Incoming, string Path);
    private sealed record ConflictFingerprint(LogicalObjectId Id, ObjectVersion? Local, ObjectVersion? Remote, ObjectVersion? Baseline);
}

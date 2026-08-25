using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Codex;

public sealed record SessionScanResult(
    IReadOnlyList<LocalObject> Objects,
    IReadOnlySet<ObjectKind> UncertainKinds,
    IReadOnlySet<LogicalObjectId> DuplicateIds)
{
    public bool HasFatalErrors => DuplicateIds.Count != 0;
    public bool IsAbsenceConfirmed(ObjectKind kind) => !UncertainKinds.Contains(kind);

    /// <summary>
    /// Sessions that exist on disk and are deliberately kept out of <see cref="Objects"/>. They
    /// are excluded, not absent: a caller that mistook one for a deletion would publish a
    /// tombstone and erase the file on every other machine that pulls it.
    /// </summary>
    public IReadOnlySet<LogicalObjectId> IgnoredIds { get; init; } = new HashSet<LogicalObjectId>();

    public bool IsIgnored(LogicalObjectId id) => IgnoredIds.Contains(id);
}

public sealed class SessionScanner
{
    private static readonly HashSet<string> DisallowedDirectorySegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "logs",
        "cache",
        "tmp",
        "temp",
        ".sandbox",
        ".sandbox-secrets",
        "machine",
        "machines",
        "machine-id",
        "machine-identity"
    };

    private readonly Func<CancellationToken, Task> waitForStability;

    public SessionScanner() : this(TimeSpan.FromMilliseconds(50))
    {
    }

    public SessionScanner(TimeSpan stabilityDelay)
    {
        if (stabilityDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stabilityDelay));
        waitForStability = cancellationToken => Task.Delay(stabilityDelay, cancellationToken);
    }

    internal SessionScanner(Func<CancellationToken, Task> waitForStability)
    {
        this.waitForStability = waitForStability ?? throw new ArgumentNullException(nameof(waitForStability));
    }

    public async Task<IReadOnlyList<LocalObject>> ScanAsync(CodexPaths paths, CancellationToken cancellationToken)
    {
        var result = await ScanDetailedAsync(paths, cancellationToken).ConfigureAwait(false);
        if (result.HasFatalErrors)
            throw new InvalidDataException("Local history contains duplicate logical object IDs.");
        return result.Objects;
    }

    public async Task<SessionScanResult> ScanDetailedAsync(CodexPaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var objects = new List<LocalObject>();
        var objectsById = new Dictionary<LogicalObjectId, LocalObject>();
        var uncertainKinds = new HashSet<ObjectKind>();
        var duplicateIds = new HashSet<LogicalObjectId>();
        var ignoredIds = new HashSet<LogicalObjectId>();
        var candidates = new List<ObservedCandidate>();
        CollectCandidates(paths.Sessions, ObjectKind.ActiveSession, candidates, uncertainKinds, cancellationToken);
        CollectCandidates(paths.ArchivedSessions, ObjectKind.ArchivedSession, candidates, uncertainKinds,
            cancellationToken);

        if (candidates.Count != 0)
            await waitForStability(cancellationToken).ConfigureAwait(false);

        var results = new ScannedSession?[candidates.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, candidates.Count), new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8)
        }, async (index, ct) =>
        {
            results[index] = await ReadStableSessionAsync(candidates[index], ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[index];
            var scanned = results[index];
            if (scanned is null)
            {
                uncertainKinds.Add(candidate.Kind);
                continue;
            }

            var session = scanned.Value.Object;
            if (scanned.Value.IsSubagent)
            {
                // A subagent thread is machine-local transcript noise the manager already hides.
                // It is recorded as ignored rather than dropped so its absence never reads as a
                // deletion of a session another machine still holds.
                ignoredIds.Add(session.Id);
                continue;
            }

            if (objectsById.TryAdd(session.Id, session)) objects.Add(session);
            else
            {
                // Never silently prefer the first path: drop both sides from Objects and fail closed.
                duplicateIds.Add(session.Id);
                uncertainKinds.Add(objectsById[session.Id].Kind);
                uncertainKinds.Add(session.Kind);
                if (objectsById.Remove(session.Id, out var prior))
                    objects.Remove(prior);
            }
        }
        return new SessionScanResult(objects, uncertainKinds, duplicateIds) { IgnoredIds = ignoredIds };
    }

    private static void CollectCandidates(
        string directory,
        ObjectKind kind,
        List<ObservedCandidate> observed,
        HashSet<ObjectKind> uncertainKinds,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            uncertainKinds.Add(kind);
            return;
        }

        IReadOnlyList<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(
                    directory,
                    "*.jsonl",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    })
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            uncertainKinds.Add(kind);
            return;
        }

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CodexPaths.IsPathWithin(candidate, directory) || IsDisallowedCandidate(candidate, directory))
                continue;
            try
            {
                observed.Add(new ObservedCandidate(candidate, kind, ReadObservation(candidate)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                uncertainKinds.Add(kind);
            }
        }
    }

    private static async Task<ScannedSession?> ReadStableSessionAsync(
        ObservedCandidate candidate,
        CancellationToken cancellationToken)
    {
        try
        {
            var second = ReadObservation(candidate.Path);
            if (candidate.First != second) return null;

            var bytes = await ReadFileAsync(candidate.Path, second.Length, cancellationToken).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0 || bytes[^1] != (byte)'\n') return null;

            // Hash/sync the reduced view so compaction snapshots do not dominate size or identity.
            var normalized = SessionJsonlNormalizer.Normalize(bytes);
            if (normalized.Length == 0 || normalized[^1] != (byte)'\n') return null;

            var identity = ReadSessionIdentity(normalized);
            if (identity is null) return null;

            return new ScannedSession(
                new LocalObject(
                    identity.Value.Id,
                    candidate.Kind,
                    Path.GetFullPath(candidate.Path),
                    new ContentHash(Convert.ToHexString(SHA256.HashData(normalized)).ToLowerInvariant()),
                    normalized.LongLength,
                    new DateTimeOffset(second.LastWriteTimeUtc, TimeSpan.Zero)),
                identity.Value.IsSubagent);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static bool IsDisallowedCandidate(string path, string root)
    {
        if (Path.GetFileName(path).Contains(".sqlite", StringComparison.OrdinalIgnoreCase)) return true;

        var relativeDirectory = Path.GetDirectoryName(Path.GetRelativePath(root, path));
        return relativeDirectory is not null
            && relativeDirectory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
                .Any(DisallowedDirectorySegments.Contains);
    }

    private static FileObservation ReadObservation(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();
        return new FileObservation(info.Length, info.LastWriteTimeUtc);
    }

    private static async Task<byte[]?> ReadFileAsync(string path, long expectedLength, CancellationToken cancellationToken)
    {
        if (expectedLength > int.MaxValue) return null;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        var bytes = new byte[expectedLength];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken);
            if (read == 0) return null;
            offset += read;
        }

        return await stream.ReadAsync(new byte[1], cancellationToken) == 0 ? bytes : null;
    }

    private static SessionIdentity? ReadSessionIdentity(byte[] bytes)
    {
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        LogicalObjectId? id = null;
        var subagent = false;

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("type", out var type)) continue;
            if (type.ValueKind != JsonValueKind.String) return null;
            if (type.GetString() != "session_meta") continue;
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return null;
            if (!payload.TryGetProperty("id", out var value) || value.ValueKind != JsonValueKind.String) return null;

            var parsed = value.GetString();
            if (!IsSafeLogicalId(parsed)) return null;
            var candidate = new LogicalObjectId(parsed!);
            if (id is not null && id.Value != candidate) return null;
            id = candidate;
            if (IsSubagent(payload)) subagent = true;
        }

        return id is null ? null : new SessionIdentity(id.Value, subagent);
    }

    /// <summary>
    /// The same two markers <c>CodexSessionCatalogSource</c> hides a subagent thread by. Kept
    /// byte-for-byte identical so a session cannot be invisible in the manager yet synchronized.
    /// </summary>
    private static bool IsSubagent(JsonElement payload) =>
        string.Equals(GetString(payload, "thread_source"), "subagent", StringComparison.OrdinalIgnoreCase) ||
        payload.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object &&
        source.TryGetProperty("subagent", out var subagent) && subagent.ValueKind == JsonValueKind.Object;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsSafeLogicalId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !Path.IsPathRooted(value)
        && !value.Contains('/')
        && !value.Contains('\\')
        && value != "."
        && value != "..";

    private readonly record struct SessionIdentity(LogicalObjectId Id, bool IsSubagent);
    private readonly record struct ScannedSession(LocalObject Object, bool IsSubagent);
    private readonly record struct FileObservation(long Length, DateTime LastWriteTimeUtc);
    private readonly record struct ObservedCandidate(string Path, ObjectKind Kind, FileObservation First);
}

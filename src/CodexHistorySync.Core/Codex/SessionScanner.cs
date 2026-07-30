using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Codex;

public sealed record SessionScanResult(
    IReadOnlyList<LocalObject> Objects,
    IReadOnlySet<ObjectKind> UncertainKinds)
{
    public bool IsAbsenceConfirmed(ObjectKind kind) => !UncertainKinds.Contains(kind);
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

    public async Task<IReadOnlyList<LocalObject>> ScanAsync(CodexPaths paths, CancellationToken cancellationToken)
        => (await ScanDetailedAsync(paths, cancellationToken).ConfigureAwait(false)).Objects;

    public async Task<SessionScanResult> ScanDetailedAsync(CodexPaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var objects = new List<LocalObject>();
        var ids = new HashSet<LogicalObjectId>();
        var uncertainKinds = new HashSet<ObjectKind>();
        if (!await ScanDirectoryAsync(paths.Sessions, ObjectKind.ActiveSession, ids, objects, cancellationToken).ConfigureAwait(false))
            uncertainKinds.Add(ObjectKind.ActiveSession);
        if (!await ScanDirectoryAsync(paths.ArchivedSessions, ObjectKind.ArchivedSession, ids, objects, cancellationToken).ConfigureAwait(false))
            uncertainKinds.Add(ObjectKind.ArchivedSession);
        return new SessionScanResult(objects, uncertainKinds);
    }

    private async Task<bool> ScanDirectoryAsync(
        string directory,
        ObjectKind kind,
        HashSet<LogicalObjectId> ids,
        List<LocalObject> objects,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) return false;

        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(
                directory,
                "*.jsonl",
                new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        var complete = true;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CodexPaths.IsPathWithin(candidate, directory) || IsDisallowedCandidate(candidate, directory)) continue;

            var session = await ReadStableSessionAsync(candidate, kind, cancellationToken);
            if (session is null) complete = false;
            else if (ids.Add(session.Id)) objects.Add(session);
        }
        return complete;
    }

    private async Task<LocalObject?> ReadStableSessionAsync(string path, ObjectKind kind, CancellationToken cancellationToken)
    {
        try
        {
            var first = ReadObservation(path);
            await waitForStability(cancellationToken);
            var second = ReadObservation(path);
            if (first != second) return null;

            var bytes = await ReadFileAsync(path, second.Length, cancellationToken);
            if (bytes is null || bytes.Length == 0 || bytes[^1] != (byte)'\n') return null;

            var id = ReadSessionId(bytes);
            if (id is null) return null;

            return new LocalObject(
                id.Value,
                kind,
                Path.GetFullPath(path),
                new ContentHash(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()),
                second.Length,
                new DateTimeOffset(second.LastWriteTimeUtc, TimeSpan.Zero));
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

    private static LogicalObjectId? ReadSessionId(byte[] bytes)
    {
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        LogicalObjectId? id = null;

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
        }

        return id;
    }

    private static bool IsSafeLogicalId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !Path.IsPathRooted(value)
        && !value.Contains('/')
        && !value.Contains('\\')
        && value != "."
        && value != "..";

    private readonly record struct FileObservation(long Length, DateTime LastWriteTimeUtc);
}

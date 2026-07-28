using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Codex;

public sealed class SessionScanner
{
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
    {
        ArgumentNullException.ThrowIfNull(paths);

        var objects = new List<LocalObject>();
        var ids = new HashSet<LogicalObjectId>();
        await ScanDirectoryAsync(paths.Sessions, ObjectKind.ActiveSession, ids, objects, cancellationToken);
        await ScanDirectoryAsync(paths.ArchivedSessions, ObjectKind.ArchivedSession, ids, objects, cancellationToken);
        return objects;
    }

    private async Task ScanDirectoryAsync(
        string directory,
        ObjectKind kind,
        HashSet<LogicalObjectId> ids,
        List<LocalObject> objects,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) return;

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
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CodexPaths.IsPathWithin(candidate, directory) || IsDisallowedCandidate(candidate)) continue;

            var session = await ReadStableSessionAsync(candidate, kind, cancellationToken);
            if (session is not null && ids.Add(session.Id)) objects.Add(session);
        }
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

    private static bool IsDisallowedCandidate(string path) =>
        Path.GetFileName(path).Contains(".sqlite", StringComparison.OrdinalIgnoreCase);

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
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "session_meta") continue;
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

using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Kimi;

namespace CodexHistorySync.Core.Management;

internal sealed class KimiSessionCatalogSource(KimiPaths paths, ISessionCatalogIo io) : ILocalSessionCatalogSource
{
    private const int MaximumMetadataBytes = 64 * 1024;
    private const int MaximumMetadataRecords = 64;
    private const int MaximumTitleLength = 80;

    public ManagedAgent Agent => ManagedAgent.Kimi;

    public async Task<IReadOnlyList<SessionCatalogCandidate>> ScanAsync(
        SessionCatalogReadLimiter limiter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        var candidates = EnumerateCandidates();
        var collected = new CandidateResult?[candidates.Count];

        await Parallel.ForEachAsync(Enumerable.Range(0, candidates.Count), new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = 8
        }, async (index, token) =>
        {
            var (candidate, sessionId) = candidates[index];
            if (!ManagedSessionPathPolicy.TryResolveConcreteTarget(candidate, paths.Sessions, expectDirectory: true,
                    out var nativePath))
                return;

            var metadata = await ReadMetadataAsync(nativePath, sessionId, limiter, token).ConfigureAwait(false);
            collected[index] = new CandidateResult(sessionId, nativePath, metadata);
        }).ConfigureAwait(false);

        var rows = collected.Where(result => result is not null).Select(result => result!).Select(result =>
        {
            var metadata = result.Metadata;
            return new SessionCatalogCandidate(
                result.SessionId,
                result.NativePath,
                DisplayTitle(metadata.Title, result.SessionId),
                metadata.LastModifiedAt ?? LastWriteTime(result.NativePath),
                metadata.CanRead,
                NormalizeTitle(metadata.Title) is null ? ManagedTitleSource.SessionId
                    : metadata.TitleIsOfficial ? ManagedTitleSource.Official
                    : ManagedTitleSource.Fallback);
        }).ToArray();

        // The same uuid can sit in two workDir buckets after a relocated session; the same rule as
        // every other agent applies — never two rows for one id.
        var duplicates = rows.GroupBy(row => row.SessionId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rows.Select(row => duplicates.Contains(row.SessionId) ? row with { CanRead = false } : row).ToArray();
    }

    private List<(string Candidate, string SessionId)> EnumerateCandidates()
    {
        try
        {
            var result = new List<(string, string)>();
            foreach (var bucket in io.EnumerateDirectories(paths.Sessions))
            {
                if (!Path.GetFileName(Path.TrimEndingDirectorySeparator(bucket)).StartsWith("wd_", StringComparison.Ordinal))
                    continue;
                foreach (var candidate in io.EnumerateDirectories(bucket))
                {
                    // Enumeration is recursive; sessions live exactly one level under a bucket, so
                    // a same-named directory deeper inside (runtime task state) is not a session.
                    if (!StringComparer.OrdinalIgnoreCase.Equals(
                            Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(candidate) ?? string.Empty),
                            Path.TrimEndingDirectorySeparator(bucket)))
                        continue;
                    var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(candidate));
                    if (!directoryName.StartsWith(KimiPaths.SessionIdPrefix, StringComparison.Ordinal)) continue;
                    var sessionId = directoryName[KimiPaths.SessionIdPrefix.Length..];
                    if (!IsSafeSessionId(sessionId)) continue;
                    result.Add((candidate, sessionId));
                }
            }

            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private async Task<Metadata> ReadMetadataAsync(
        string directory,
        string sessionId,
        SessionCatalogReadLimiter limiter,
        CancellationToken cancellationToken)
    {
        var statePath = Path.Combine(directory, KimiSessionPackage.StateFileName);
        var wirePath = Path.Combine(directory, "agents", "main", KimiSessionPackage.WireFileName);
        var title = default(string);
        var officialTitle = default(string);
        var modified = default(DateTimeOffset?);
        var stateReadable = false;
        var wireExists = io.FileExists(wirePath);
        var wireReadable = true;

        try
        {
            if (io.FileExists(statePath))
            {
                var state = await limiter.RunAsync(token => io.ReadPrefixAsync(statePath, MaximumMetadataBytes, token),
                    cancellationToken).ConfigureAwait(false);
                if (state.IsComplete)
                {
                    try
                    {
                        using var document = JsonDocument.Parse(state.Text);
                        var root = document.RootElement;
                        if (root.ValueKind == JsonValueKind.Object)
                        {
                            var declaredId = GetString(root, "id");
                            stateReadable = string.Equals(declaredId, KimiPaths.SessionIdPrefix + sessionId,
                                    StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(declaredId, sessionId, StringComparison.OrdinalIgnoreCase);
                            title = NormalizeTitle(GetString(root, "title"));
                            officialTitle = title;
                            modified = ReadEpochMilliseconds(root, "updatedAt") ?? ReadEpochMilliseconds(root, "createdAt");
                        }
                    }
                    catch (JsonException) { }
                }
            }

            if (wireExists && title is null)
            {
                var wire = await ReadWireMetadataAsync(wirePath, limiter, cancellationToken).ConfigureAwait(false);
                wireReadable = wire.IsReadable;
                title ??= wire.Title;
                modified ??= wire.LastModifiedAt;
            }

            modified ??= LastWriteTime(statePath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or
                                          ArgumentException)
        {
            stateReadable = false;
        }

        return new Metadata(title, modified, stateReadable && wireExists && wireReadable,
            officialTitle is not null);
    }

    private async Task<WireMetadata> ReadWireMetadataAsync(
        string path,
        SessionCatalogReadLimiter limiter,
        CancellationToken cancellationToken)
    {
        try
        {
            var prefix = await limiter.RunAsync(token => io.ReadPrefixAsync(path, MaximumMetadataBytes, token), cancellationToken)
                .ConfigureAwait(false);
            string? title = null;
            DateTimeOffset? modified = null;
            var readable = true;
            foreach (var line in CompleteLines(prefix.Text, discardLast: !prefix.IsComplete).Take(MaximumMetadataRecords))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        readable = false;
                        continue;
                    }

                    if (root.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Number &&
                        time.TryGetInt64(out var epochMs))
                    {
                        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(epochMs);
                        if (modified is null || timestamp > modified) modified = timestamp;
                    }

                    if (title is null && IsUserAppendedRecord(root))
                    {
                        var preview = ReadUserPreview(root);
                        if (preview is not null && !ConversationTechnicalText.IsWrapper(preview)) title = preview;
                    }
                }
                catch (JsonException)
                {
                    readable = false;
                }
            }
            return new WireMetadata(title, modified, readable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException)
        {
            return new WireMetadata(null, null, false);
        }
    }

    private static IEnumerable<string> CompleteLines(string text, bool discardLast)
    {
        var lines = text.Split('\n');
        var end = lines.Length - (discardLast ? 1 : 0);
        for (var index = 0; index < end; index++) yield return lines[index].TrimEnd('\r');
    }

    private static bool IsSafeSessionId(string value)
    {
        try
        {
            _ = KimiSessionPackage.ToLogicalId(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsUserAppendedRecord(JsonElement root)
    {
        if (!string.Equals(GetString(root, "type"), "agent.message.appended", StringComparison.Ordinal)) return false;
        return root.TryGetProperty("message", out var envelope) && envelope.ValueKind == JsonValueKind.Object &&
               envelope.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
               string.Equals(GetString(message, "role"), "user", StringComparison.Ordinal);
    }

    private static string? ReadUserPreview(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var envelope) || envelope.ValueKind != JsonValueKind.Object ||
            !envelope.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content))
            return null;
        if (content.ValueKind == JsonValueKind.String) return NormalizeTitle(content.GetString());
        if (content.ValueKind != JsonValueKind.Array) return null;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object &&
                string.Equals(GetString(block, "type"), "text", StringComparison.Ordinal))
                return NormalizeTitle(GetString(block, "text"));
        }
        return null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? NormalizeTitle(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string DisplayTitle(string? value, string fallback)
    {
        var title = NormalizeTitle(value) ?? fallback;
        return title.Length > MaximumTitleLength ? title[..MaximumTitleLength] : title;
    }

    private DateTimeOffset LastWriteTime(string path)
    {
        try { return io.LastWriteTime(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static DateTimeOffset? ReadEpochMilliseconds(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var epochMs)
            ? DateTimeOffset.FromUnixTimeMilliseconds(epochMs)
            : null;

    private sealed record Metadata(
        string? Title,
        DateTimeOffset? LastModifiedAt,
        bool CanRead,
        bool TitleIsOfficial = false);
    private sealed record WireMetadata(string? Title, DateTimeOffset? LastModifiedAt, bool IsReadable);
    private sealed record CandidateResult(string SessionId, string NativePath, Metadata Metadata);
}

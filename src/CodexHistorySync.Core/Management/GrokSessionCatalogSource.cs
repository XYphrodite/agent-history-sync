using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Grok;

namespace CodexHistorySync.Core.Management;

internal sealed class GrokSessionCatalogSource(GrokPaths paths, ISessionCatalogIo io) : ILocalSessionCatalogSource
{
    private const int MaximumMetadataBytes = 64 * 1024;
    private const int MaximumMetadataRecords = 64;
    private const int MaximumTitleLength = 80;
    private static readonly string[] TechnicalPreviewOpeningTags =
    [
        "<environment_context>", "<recommended_plugins>", "<user_info>", "<system-reminder>",
        "<permissions instructions>", "<skills_instructions>", "<apps_instructions>", "<plugins_instructions>"
    ];

    public ManagedAgent Agent => ManagedAgent.Grok;

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
                metadata.CanRead);
        }).ToArray();

        var duplicates = rows.GroupBy(row => row.SessionId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rows.Select(row => duplicates.Contains(row.SessionId) ? row with { CanRead = false } : row).ToArray();
    }

    private List<(string Candidate, string SessionId)> EnumerateCandidates()
    {
        try
        {
            return io.EnumerateDirectories(paths.Sessions)
                .Select(candidate => (Candidate: candidate, SessionId: Path.GetFileName(candidate)))
                .Where(candidate => IsSafeSessionId(candidate.SessionId))
                .ToList();
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
        var summaryPath = Path.Combine(directory, "summary.json");
        var chatPath = Path.Combine(directory, "chat_history.jsonl");
        var title = default(string);
        var modified = default(DateTimeOffset?);
        var summaryReadable = false;
        var chatExists = false;
        var chatReadable = true;

        try
        {
            if (io.FileExists(summaryPath))
            {
                var summary = await limiter.RunAsync(token => io.ReadPrefixAsync(summaryPath, MaximumMetadataBytes, token),
                    cancellationToken).ConfigureAwait(false);
                if (summary.IsComplete)
                {
                    try
                    {
                        using var document = JsonDocument.Parse(summary.Text);
                        var root = document.RootElement;
                        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("info", out var info) &&
                            info.ValueKind == JsonValueKind.Object)
                        {
                            var metadataId = GetString(info, "id");
                            summaryReadable = string.Equals(metadataId, sessionId, StringComparison.OrdinalIgnoreCase);
                            title = NormalizeTitle(GetString(root, "generated_title")
                                                   ?? GetString(root, "session_summary")
                                                   ?? GetString(info, "title")
                                                   ?? GetString(root, "title"));
                            AddLatestTimestamp(info, ref modified);
                            AddLatestTimestamp(root, ref modified);
                        }
                    }
                    catch (JsonException) { }
                }
            }

            chatExists = io.FileExists(chatPath);
            if (chatExists && (title is null || modified is null))
            {
                var chat = await ReadChatMetadataAsync(chatPath, limiter, cancellationToken).ConfigureAwait(false);
                chatReadable = chat.IsReadable;
                title ??= chat.Title;
                modified ??= chat.LastModifiedAt;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or
                                          ArgumentException)
        {
            summaryReadable = false;
        }

        return new Metadata(title, modified, summaryReadable && chatExists && chatReadable);
    }

    private async Task<ChatMetadata> ReadChatMetadataAsync(
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

                    AddLatestTimestamp(root, ref modified);
                    if (title is null && IsUserRecord(root))
                        foreach (var preview in ReadUserPreviews(root))
                            if (!IsTechnicalPreview(preview))
                            {
                                title = preview;
                                break;
                            }
                }
                catch (JsonException)
                {
                    readable = false;
                }
            }
            return new ChatMetadata(title, modified, readable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException)
        {
            return new ChatMetadata(null, null, false);
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
            _ = GrokSessionPackage.ToLogicalId(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsUserRecord(JsonElement root) =>
        string.Equals(GetString(root, "role"), "user", StringComparison.Ordinal) ||
        string.Equals(GetString(root, "type"), "user", StringComparison.Ordinal);

    private static IEnumerable<string> ReadUserPreviews(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content)) yield break;
        if (content.ValueKind == JsonValueKind.String)
        {
            if (NormalizeTitle(content.GetString()) is { } value) yield return value;
            yield break;
        }
        if (content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
                if (block.ValueKind == JsonValueKind.Object &&
                    (string.Equals(GetString(block, "type"), "text", StringComparison.Ordinal) ||
                     string.Equals(GetString(block, "type"), "input_text", StringComparison.Ordinal)) &&
                    NormalizeTitle(GetString(block, "text")) is { } text)
                    yield return text;
    }

    private static bool IsTechnicalPreview(string preview) =>
        TechnicalPreviewOpeningTags.Any(tag => preview.StartsWith(tag, StringComparison.OrdinalIgnoreCase));

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

    private static void AddLatestTimestamp(JsonElement element, ref DateTimeOffset? latest)
    {
        foreach (var name in new[] { "timestamp", "created_at", "createdAt", "updated_at", "updatedAt", "last_modified_at", "lastModifiedAt" })
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), out var timestamp) && (latest is null || timestamp > latest))
                latest = timestamp;
    }

    private sealed record Metadata(string? Title, DateTimeOffset? LastModifiedAt, bool CanRead);
    private sealed record ChatMetadata(string? Title, DateTimeOffset? LastModifiedAt, bool IsReadable);
    private sealed record CandidateResult(string SessionId, string NativePath, Metadata Metadata);
}

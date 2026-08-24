using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Claude;

namespace CodexHistorySync.Core.Management;

internal sealed class ClaudeSessionCatalogSource(ClaudePaths paths, ISessionCatalogIo io) : ILocalSessionCatalogSource
{
    private const int MaximumMetadataBytes = 64 * 1024;
    private const int MaximumMetadataRecords = 64;
    private const int MaximumTitleLength = 80;
    private static readonly string[] TechnicalPreviewOpeningTags =
    [
        "<environment_context>", "<recommended_plugins>", "<user_info>", "<system-reminder>",
        "<permissions instructions>", "<skills_instructions>", "<apps_instructions>", "<plugins_instructions>",
        "<ide_opened_file>", "<ide_selection>", "<local-command-stdout>", "<command-name>", "<command-message>",
        "[Request interrupted by user",
        "# Files mentioned by the user:", "# Context from my IDE setup:"
    ];

    public ManagedAgent Agent => ManagedAgent.Claude;

    public async Task<IReadOnlyList<SessionCatalogCandidate>> ScanAsync(
        SessionCatalogReadLimiter limiter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        var candidates = EnumerateCandidates();
        var collected = new SessionCatalogCandidate?[candidates.Count];

        await Parallel.ForEachAsync(Enumerable.Range(0, candidates.Count), new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = 8
        }, async (index, token) =>
        {
            var (candidate, sessionId) = candidates[index];
            if (!ManagedSessionPathPolicy.TryResolveConcreteTarget(candidate, paths.Projects, expectDirectory: false,
                    out var nativePath))
                return;

            var metadata = await ReadMetadataAsync(nativePath, sessionId, limiter, token).ConfigureAwait(false);
            collected[index] = new SessionCatalogCandidate(
                sessionId,
                nativePath,
                DisplayTitle(metadata.Title, sessionId),
                metadata.LastModifiedAt ?? LastWriteTime(nativePath),
                metadata.CanRead);
        }).ConfigureAwait(false);

        var rows = collected.Where(row => row is not null).Select(row => row!).ToArray();
        // The same session id under two project directories: neither copy can be trusted as the session.
        var duplicates = rows.GroupBy(row => row.SessionId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rows.Select(row => duplicates.Contains(row.SessionId) ? row with { CanRead = false } : row).ToArray();
    }

    private List<(string Candidate, string SessionId)> EnumerateCandidates()
    {
        try
        {
            return io.EnumerateDirectories(paths.Projects)
                .SelectMany(EnumerateProjectTranscripts)
                .Select(candidate => (Candidate: candidate, SessionId: Path.GetFileNameWithoutExtension(candidate)))
                .Where(candidate => IsSafeSessionId(candidate.SessionId))
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private IEnumerable<string> EnumerateProjectTranscripts(string project)
    {
        try
        {
            return io.EnumerateFiles(project, "*.jsonl");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>
    /// Reads a bounded prefix only: a transcript can be megabytes, and the manager opens every
    /// session on every refresh. The newest title inside the window wins (design D7).
    /// </summary>
    private async Task<Metadata> ReadMetadataAsync(
        string transcript,
        string sessionId,
        SessionCatalogReadLimiter limiter,
        CancellationToken cancellationToken)
    {
        try
        {
            var prefix = await limiter.RunAsync(token => io.ReadPrefixAsync(transcript, MaximumMetadataBytes, token), cancellationToken)
                .ConfigureAwait(false);
            string? aiTitle = null;
            string? summaryTitle = null;
            string? firstUserPreview = null;
            DateTimeOffset? modified = null;
            var identityConfirmed = false;
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

                    if (GetString(root, "sessionId") is { } recordId)
                    {
                        if (!string.Equals(recordId, sessionId, StringComparison.OrdinalIgnoreCase)) readable = false;
                        else identityConfirmed = true;
                    }

                    AddLatestTimestamp(root, ref modified);
                    switch (GetString(root, "type"))
                    {
                        case "ai-title":
                            aiTitle = NormalizeTitle(GetString(root, "aiTitle")) ?? aiTitle;
                            break;
                        case "summary":
                            summaryTitle ??= NormalizeTitle(GetString(root, "summary"));
                            break;
                        case "user" when firstUserPreview is null && !IsSidechain(root):
                            foreach (var preview in ReadUserPreviews(root))
                                if (!IsTechnicalPreview(preview))
                                {
                                    firstUserPreview = preview;
                                    break;
                                }
                            break;
                    }
                }
                catch (JsonException)
                {
                    readable = false;
                }
            }

            return new Metadata(
                aiTitle ?? summaryTitle ?? firstUserPreview,
                modified,
                readable && identityConfirmed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException)
        {
            return new Metadata(null, null, false);
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
            _ = ClaudeSessionPackage.ToLogicalId(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsSidechain(JsonElement root) =>
        root.TryGetProperty("isSidechain", out var value) && value.ValueKind == JsonValueKind.True;

    private static IEnumerable<string> ReadUserPreviews(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) yield break;
        if (!message.TryGetProperty("content", out var content)) yield break;
        if (content.ValueKind == JsonValueKind.String)
        {
            if (NormalizeTitle(content.GetString()) is { } value) yield return value;
            yield break;
        }
        if (content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
                if (block.ValueKind == JsonValueKind.Object &&
                    string.Equals(GetString(block, "type"), "text", StringComparison.Ordinal) &&
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
        if (element.TryGetProperty("timestamp", out var value) && value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), out var timestamp) && (latest is null || timestamp > latest))
            latest = timestamp;
    }

    private sealed record Metadata(string? Title, DateTimeOffset? LastModifiedAt, bool CanRead);
}

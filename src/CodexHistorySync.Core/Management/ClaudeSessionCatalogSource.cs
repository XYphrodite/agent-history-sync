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
        "# Files mentioned by the user:", "# Context from my IDE setup:",
        // Editor integrations prepend a hidden primer turn the user never sees, so it must not
        // become the session's name when there is no title record to fall back from.
        "[vscode-supergrok primer", "## HIDDEN PRIMER"
    ];
    private const string UserQueryOpenTag = "<user_query>";
    private const string UserQueryCloseTag = "</user_query>";

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
                ResolveLastModified(nativePath, metadata.LastModifiedAt),
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

            foreach (var line in CompleteLines(prefix.Text, discardFirst: false, discardLast: !prefix.IsComplete)
                         .Take(MaximumMetadataRecords))
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

            var tailTitle = prefix.IsComplete
                ? null
                : await ReadTailTitleAsync(transcript, sessionId, limiter, cancellationToken).ConfigureAwait(false);

            return new Metadata(
                tailTitle ?? aiTitle ?? summaryTitle ?? firstUserPreview,
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

    /// <summary>
    /// A rename appends a fresh ai-title record, so the last one in the file is the current
    /// name. On a transcript longer than the prefix window that record sits past it, and a
    /// prefix-only read would name the session after its opening turn. Only long transcripts
    /// pay for this second bounded read, and a failed one leaves the prefix title standing.
    /// </summary>
    private async Task<string?> ReadTailTitleAsync(
        string transcript,
        string sessionId,
        SessionCatalogReadLimiter limiter,
        CancellationToken cancellationToken)
    {
        try
        {
            var tail = await limiter.RunAsync(token => io.ReadTailAsync(transcript, MaximumMetadataBytes, token), cancellationToken)
                .ConfigureAwait(false);
            string? title = null;
            foreach (var line in CompleteLines(tail.Text, discardFirst: !tail.IsComplete, discardLast: false)
                         .TakeLast(MaximumMetadataRecords))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (GetString(root, "sessionId") is { } recordId &&
                        !string.Equals(recordId, sessionId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(GetString(root, "type"), "ai-title", StringComparison.Ordinal))
                        title = NormalizeTitle(GetString(root, "aiTitle")) ?? title;
                }
                catch (JsonException)
                {
                }
            }
            return title;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException)
        {
            return null;
        }
    }

    private static IEnumerable<string> CompleteLines(string text, bool discardFirst, bool discardLast)
    {
        var lines = text.Split('\n');
        var start = discardFirst ? 1 : 0;
        var end = lines.Length - (discardLast ? 1 : 0);
        for (var index = start; index < end; index++) yield return lines[index].TrimEnd('\r');
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
            if (NormalizeTitle(content.GetString()) is { } value) yield return Unwrap(value);
            yield break;
        }
        if (content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
                if (block.ValueKind == JsonValueKind.Object &&
                    string.Equals(GetString(block, "type"), "text", StringComparison.Ordinal) &&
                    NormalizeTitle(GetString(block, "text")) is { } text)
                    yield return Unwrap(text);
    }

    /// <summary>
    /// Editor integrations wrap every turn in a user_query tag that carries no information of
    /// its own: strip it so the tag neither hides a technical opening turn nor reaches a title.
    /// </summary>
    private static string Unwrap(string preview)
    {
        var value = preview;
        if (value.StartsWith(UserQueryOpenTag, StringComparison.OrdinalIgnoreCase))
            value = value[UserQueryOpenTag.Length..].TrimStart();
        if (value.EndsWith(UserQueryCloseTag, StringComparison.OrdinalIgnoreCase))
            value = value[..^UserQueryCloseTag.Length].TrimEnd();
        return value.Length == 0 ? preview : value;
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

    /// <summary>
    /// A transcript is one append-only file, so its write time is the session's real last
    /// activity. Record timestamps are only a fallback: the metadata read is bounded to a
    /// 64 KiB prefix, so on a multi-megabyte session the newest timestamp it can see is from
    /// near the start — a live session would sort below sessions untouched for days.
    /// </summary>
    private DateTimeOffset ResolveLastModified(string path, DateTimeOffset? fromRecords)
    {
        var written = LastWriteTime(path);
        return written > DateTimeOffset.MinValue ? written : fromRecords ?? written;
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

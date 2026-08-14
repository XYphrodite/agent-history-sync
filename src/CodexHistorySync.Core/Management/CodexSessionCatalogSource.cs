using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Codex;

namespace CodexHistorySync.Core.Management;

internal sealed class CodexSessionCatalogSource(CodexPaths paths, ISessionCatalogIo io) : ILocalSessionCatalogSource
{
    private const int MaximumMetadataBytes = 64 * 1024;
    private const int MaximumMetadataRecords = 64;
    private const int MaximumTitleLength = 80;
    private static readonly HashSet<string> DisallowedDirectorySegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "logs", "cache", "tmp", "temp", ".sandbox", ".sandbox-secrets", "machine", "machines",
        "machine-id", "machine-identity"
    };
    private static readonly string[] TechnicalPreviewOpeningTags =
    [
        "<environment_context>", "<recommended_plugins>", "<user_info>", "<system-reminder>",
        "<permissions instructions>", "<skills_instructions>", "<apps_instructions>", "<plugins_instructions>"
    ];

    public ManagedAgent Agent => ManagedAgent.Codex;

    public async Task<IReadOnlyList<SessionCatalogCandidate>> ScanAsync(
        SessionCatalogReadLimiter limiter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        var titles = await ReadIndexAsync(limiter, cancellationToken).ConfigureAwait(false);
        var candidates = EnumerateCandidates();
        var collected = new CandidateResult?[candidates.Count];

        await Parallel.ForEachAsync(Enumerable.Range(0, candidates.Count), new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = 8
        }, async (index, token) =>
        {
            var (candidate, root) = candidates[index];
            if (!ManagedSessionPathPolicy.TryResolveConcreteTarget(candidate, root, expectDirectory: false, out var nativePath))
                return;
            var metadata = await ReadMetadataAsync(nativePath, limiter, token).ConfigureAwait(false);
            if (metadata is null) return;
            collected[index] = new CandidateResult(metadata, nativePath);
        }).ConfigureAwait(false);

        var rows = collected.Where(result => result is not null).Select(result => result!).Select(result =>
        {
            var metadata = result.Metadata;
            return new SessionCatalogCandidate(
                metadata.SessionId,
                result.NativePath,
                DisplayTitle(titles.GetValueOrDefault(metadata.SessionId) ?? metadata.Title, metadata.SessionId),
                metadata.LastModifiedAt ?? LastWriteTime(result.NativePath),
                metadata.IsReadable);
        }).ToArray();

        var duplicates = rows.GroupBy(row => row.SessionId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rows.Select(row => duplicates.Contains(row.SessionId) ? row with { CanRead = false } : row).ToArray();
    }

    private List<(string Candidate, string Root)> EnumerateCandidates()
    {
        var candidates = new List<(string, string)>();
        foreach (var root in new[] { paths.Sessions, paths.ArchivedSessions })
        {
            IReadOnlyList<string> files;
            try { files = io.EnumerateFiles(root, "*.jsonl"); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { continue; }
            foreach (var candidate in files)
                if (!IsDisallowedCandidate(candidate, root)) candidates.Add((candidate, root));
        }
        return candidates;
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadIndexAsync(
        SessionCatalogReadLimiter limiter,
        CancellationToken cancellationToken)
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var index = await limiter.RunAsync(token => io.ReadTailAsync(
                Path.Combine(paths.Home, "session_index.jsonl"), MaximumMetadataBytes, token), cancellationToken).ConfigureAwait(false);
            var lines = CompleteLines(index.Text, discardFirst: !index.IsComplete, discardLast: index.Text.EndsWith('\n'))
                .TakeLast(MaximumMetadataRecords);
            foreach (var line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    var id = GetString(root, "id");
                    var title = NormalizeTitle(GetString(root, "thread_name"));
                    if (IsSafeSessionId(id) && title is not null) titles[id!] = title;
                }
                catch (JsonException) { }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException) { }
        return titles;
    }

    private async Task<Metadata?> ReadMetadataAsync(string path, SessionCatalogReadLimiter limiter, CancellationToken cancellationToken)
    {
        try
        {
            var prefix = await limiter.RunAsync(token => io.ReadPrefixAsync(path, MaximumMetadataBytes, token), cancellationToken)
                .ConfigureAwait(false);
            string? sessionId = null;
            string? title = null;
            string? preview = null;
            DateTimeOffset? modified = null;
            var readable = true;
            foreach (var line in CompleteLines(prefix.Text, discardFirst: false, discardLast: !prefix.IsComplete).Take(MaximumMetadataRecords))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) { readable = false; continue; }
                    AddLatestTimestamp(root, ref modified);
                    if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                        !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                        continue;
                    AddLatestTimestamp(payload, ref modified);
                    if (string.Equals(type.GetString(), "session_meta", StringComparison.Ordinal))
                    {
                        var id = GetString(payload, "id");
                        if (!IsSafeSessionId(id) || sessionId is not null && !string.Equals(sessionId, id, StringComparison.OrdinalIgnoreCase))
                            readable = false;
                        else sessionId = id;
                        title ??= GetString(payload, "title") ?? GetString(payload, "thread_name");
                    }
                    else if (preview is null && string.Equals(type.GetString(), "response_item", StringComparison.Ordinal))
                    {
                        var candidate = ReadUserPreview(payload);
                        if (candidate is not null && !IsTechnicalPreview(candidate)) preview = candidate;
                    }
                }
                catch (JsonException) { readable = false; }
            }
            return IsSafeSessionId(sessionId)
                ? new Metadata(sessionId!, string.IsNullOrWhiteSpace(title) ? preview : title, modified, readable)
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
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

    private static bool IsDisallowedCandidate(string candidate, string root)
    {
        if (Path.GetFileName(candidate).Contains(".sqlite", StringComparison.OrdinalIgnoreCase)) return true;
        var relativeDirectory = Path.GetDirectoryName(Path.GetRelativePath(root, candidate));
        return relativeDirectory is not null && relativeDirectory.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(DisallowedDirectorySegments.Contains);
    }

    private static string? ReadUserPreview(JsonElement payload)
    {
        if (!string.Equals(GetString(payload, "type"), "message", StringComparison.Ordinal) ||
            !string.Equals(GetString(payload, "role"), "user", StringComparison.Ordinal) ||
            !payload.TryGetProperty("content", out var content)) return null;
        string? value = content.ValueKind == JsonValueKind.String ? content.GetString() : null;
        if (content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
                if (block.ValueKind == JsonValueKind.Object &&
                    string.Equals(GetString(block, "type"), "input_text", StringComparison.Ordinal) &&
                    GetString(block, "text") is { } text)
                {
                    value = text;
                    break;
                }
        return DisplayTitle(value, string.Empty) is { Length: > 0 } title ? title : null;
    }

    private static bool IsTechnicalPreview(string preview) => TechnicalPreviewOpeningTags.Any(tag => preview.StartsWith(tag, StringComparison.OrdinalIgnoreCase));
    private static string? GetString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool IsSafeSessionId(string? value) => !string.IsNullOrWhiteSpace(value) && char.IsAsciiLetterOrDigit(value[0]) && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    private static string DisplayTitle(string? value, string fallback)
    {
        var title = NormalizeTitle(value) ?? fallback;
        return title.Length > MaximumTitleLength ? title[..MaximumTitleLength] : title;
    }
    private static string? NormalizeTitle(string? value) => string.IsNullOrWhiteSpace(value) ? null : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private DateTimeOffset LastWriteTime(string path)
    {
        try { return io.LastWriteTime(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return DateTimeOffset.MinValue; }
    }
    private static void AddLatestTimestamp(JsonElement element, ref DateTimeOffset? latest)
    {
        foreach (var name in new[] { "timestamp", "created_at", "createdAt", "updated_at", "updatedAt", "last_modified_at", "lastModifiedAt" })
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var timestamp) && (latest is null || timestamp > latest)) latest = timestamp;
    }

    private sealed record Metadata(string SessionId, string? Title, DateTimeOffset? LastModifiedAt, bool IsReadable);
    private sealed record CandidateResult(Metadata Metadata, string NativePath);
}

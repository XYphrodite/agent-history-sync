using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Grok;

namespace CodexHistorySync.Core.Management;

public sealed class LocalSessionCatalog : ILocalSessionCatalog
{
    private const int MaximumTitleLength = 80;
    private const int MaximumMetadataBytes = 64 * 1024;
    private const int MaximumMetadataRecords = 64;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly HashSet<string> CodexDisallowedDirectorySegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "logs", "cache", "tmp", "temp", ".sandbox", ".sandbox-secrets", "machine", "machines",
        "machine-id", "machine-identity"
    };

    private readonly CodexPaths? codexPaths;
    private readonly GrokPaths? grokPaths;
    private readonly IManagedSessionActiveState activeState;
    private readonly SessionScanner codexScanner;
    private readonly GrokSessionScanner grokScanner;

    public LocalSessionCatalog(
        CodexPaths? codexPaths,
        GrokPaths? grokPaths,
        IManagedSessionActiveState activeState,
        SessionScanner? codexScanner = null,
        GrokSessionScanner? grokScanner = null)
    {
        this.codexPaths = codexPaths;
        this.grokPaths = grokPaths;
        this.activeState = activeState ?? throw new ArgumentNullException(nameof(activeState));
        this.codexScanner = codexScanner ?? new SessionScanner();
        this.grokScanner = grokScanner ?? new GrokSessionScanner();
    }

    public async Task<SessionCatalogSnapshot> ScanAsync(CancellationToken cancellationToken)
    {
        var codex = await ScanCodexAsync(cancellationToken).ConfigureAwait(false);
        var grok = await ScanGrokAsync(cancellationToken).ConfigureAwait(false);
        return new SessionCatalogSnapshot(Order(codex), Order(grok));
    }

    private async Task<List<ManagedSession>> ScanCodexAsync(CancellationToken cancellationToken)
    {
        var result = new List<ManagedSession>();
        if (codexPaths is null) return result;

        var stable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var scan = await codexScanner.ScanDetailedAsync(codexPaths, cancellationToken).ConfigureAwait(false);
            foreach (var item in scan.Objects)
                stable[Path.GetFullPath(item.SourcePath)] = item.Id.Value;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Candidate-level parsing below still exposes safely identifiable entries as unreadable.
        }

        var candidates = EnumerateCodexCandidates(codexPaths).ToArray();
        if (candidates.Length == 0) return result;
        var isActive = await IsAgentActiveAsync(ManagedAgent.Codex, cancellationToken).ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = SelectCodexRoot(candidate, codexPaths);
            if (root is null || !ManagedSessionPathPolicy.TryResolveConcreteTarget(
                    candidate, root, expectDirectory: false, out var nativePath))
                continue;

            var metadata = await ReadCodexMetadataAsync(nativePath, cancellationToken).ConfigureAwait(false);
            var isStable = stable.TryGetValue(nativePath, out var scannedId);
            var sessionId = isStable ? scannedId! : metadata?.SessionId;
            if (!IsSafeCodexSessionId(sessionId)) continue;

            result.Add(CreateSession(
                ManagedAgent.Codex,
                sessionId!,
                nativePath,
                isStable,
                metadata,
                isActive));
        }
        return result;
    }

    private async Task<List<ManagedSession>> ScanGrokAsync(CancellationToken cancellationToken)
    {
        var result = new List<ManagedSession>();
        if (grokPaths is null || !Directory.Exists(grokPaths.Sessions)) return result;

        var stable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var scan = await grokScanner.ScanDetailedAsync(grokPaths, cancellationToken).ConfigureAwait(false);
            foreach (var item in scan.Objects)
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(item.SourcePath));
                if (directory is not null) stable.Add(Path.TrimEndingDirectorySeparator(directory));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Candidate-level parsing below still exposes safely identifiable entries as unreadable.
        }

        var candidates = EnumerateGrokCandidates(grokPaths.Sessions).ToArray();
        if (candidates.Length == 0) return result;
        var isActive = await IsAgentActiveAsync(ManagedAgent.Grok, cancellationToken).ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ManagedSessionPathPolicy.TryResolveConcreteTarget(
                    candidate, grokPaths.Sessions, expectDirectory: true, out var nativePath))
                continue;
            var sessionId = Path.GetFileName(nativePath);
            if (!IsSafeGrokSessionId(sessionId)) continue;

            var metadata = await ReadGrokMetadataAsync(nativePath, sessionId, cancellationToken)
                .ConfigureAwait(false);
            result.Add(CreateSession(
                ManagedAgent.Grok,
                sessionId,
                nativePath,
                stable.Contains(nativePath),
                metadata,
                isActive));
        }
        return result;
    }

    private async Task<bool> IsAgentActiveAsync(ManagedAgent agent, CancellationToken cancellationToken)
    {
        try
        {
            return await activeState.IsAgentActiveAsync(agent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return true;
        }
    }

    private static ManagedSession CreateSession(
        ManagedAgent agent,
        string sessionId,
        string nativePath,
        bool stable,
        DisplayMetadata? metadata,
        bool isActive)
    {
        var identityMatches = metadata is not null &&
                              string.Equals(metadata.SessionId, sessionId, StringComparison.OrdinalIgnoreCase);
        return new ManagedSession(
            agent,
            sessionId,
            nativePath,
            identityMatches ? DisplayTitle(metadata!.Title, sessionId) : sessionId,
            identityMatches && metadata!.LastModifiedAt is { } modified
                ? modified
                : LastWriteTime(nativePath, agent == ManagedAgent.Grok),
            isActive,
            CanRead: identityMatches && (stable || isActive && metadata!.HasReadableNativeStructure));
    }

    private static IReadOnlyList<ManagedSession> Order(IEnumerable<ManagedSession> sessions) =>
        sessions.OrderByDescending(session => session.LastModifiedAt)
            .ThenBy(session => session.SessionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IEnumerable<string> EnumerateCodexCandidates(CodexPaths paths)
    {
        foreach (var root in new[] { paths.Sessions, paths.ArchivedSessions })
        foreach (var candidate in EnumerateFiles(root, "*.jsonl"))
            if (!IsDisallowedCodexCandidate(candidate, root)) yield return candidate;
    }

    private static IEnumerable<string> EnumerateGrokCandidates(string root)
    {
        foreach (var directory in EnumerateDirectories(root))
            if (IsSafeGrokSessionId(Path.GetFileName(directory))) yield return directory;
    }

    private static IReadOnlyList<string> EnumerateFiles(string root, string pattern)
    {
        if (!Directory.Exists(root)) return [];
        try
        {
            if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) return [];
            return Directory.EnumerateFiles(
                    root,
                    pattern,
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                        IgnoreInaccessible = true
                    })
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> EnumerateDirectories(string root)
    {
        if (!Directory.Exists(root)) return [];
        try
        {
            if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) return [];
            return Directory.EnumerateDirectories(
                    root,
                    "*",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                        IgnoreInaccessible = true
                    })
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static string? SelectCodexRoot(string candidate, CodexPaths paths)
    {
        if (ManagedSessionPathPolicy.IsWithin(candidate, paths.Sessions)) return paths.Sessions;
        return ManagedSessionPathPolicy.IsWithin(candidate, paths.ArchivedSessions)
            ? paths.ArchivedSessions
            : null;
    }

    private static bool IsDisallowedCodexCandidate(string candidate, string root)
    {
        if (Path.GetFileName(candidate).Contains(".sqlite", StringComparison.OrdinalIgnoreCase)) return true;
        var relativeDirectory = Path.GetDirectoryName(Path.GetRelativePath(root, candidate));
        return relativeDirectory is not null && relativeDirectory
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(CodexDisallowedDirectorySegments.Contains);
    }

    private static async Task<DisplayMetadata?> ReadCodexMetadataAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var prefix = await ReadBoundedPrefixAsync(path, cancellationToken).ConfigureAwait(false);
            using var reader = new StringReader(prefix.Text);
            string? sessionId = null;
            string? title = null;
            string? cwd = null;
            string? firstUser = null;
            DateTimeOffset? lastModified = null;
            for (var record = 0; record < MaximumMetadataRecords; record++)
            {
                var line = reader.ReadLine();
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException) { break; }
                using (document)
                {
                var root = document.RootElement;
                    AddLatestTimestamp(root, ref lastModified);
                    if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                        !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                        continue;
                    AddLatestTimestamp(payload, ref lastModified);
                    if (string.Equals(type.GetString(), "session_meta", StringComparison.Ordinal))
                    {
                        sessionId ??= GetString(payload, "id");
                        title ??= GetString(payload, "title") ?? GetString(payload, "thread_name");
                        cwd ??= GetString(payload, "cwd") ?? GetString(payload, "working_directory");
                    }
                    else if (firstUser is null &&
                             string.Equals(type.GetString(), "response_item", StringComparison.Ordinal))
                    {
                        firstUser = ReadCodexUserPreview(payload);
                    }
                }
            }
            if (!IsSafeCodexSessionId(sessionId)) return null;
            return new DisplayMetadata(
                sessionId!,
                string.IsNullOrWhiteSpace(title) ? firstUser : title,
                cwd,
                lastModified,
                HasReadableNativeStructure: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          DecoderFallbackException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task<DisplayMetadata?> ReadGrokMetadataAsync(
        string directory,
        string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var summaryPath = Path.Combine(directory, "summary.json");
            if (!File.Exists(summaryPath)) return null;
            var summary = await ReadBoundedPrefixAsync(summaryPath, cancellationToken).ConfigureAwait(false);
            if (!summary.IsComplete) return null;
            using var document = JsonDocument.Parse(summary.Text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("info", out var info) ||
                info.ValueKind != JsonValueKind.Object)
                return null;
            var metadataId = GetString(info, "id");
            if (!string.Equals(metadataId, sessionId, StringComparison.OrdinalIgnoreCase)) return null;
            var title = GetString(info, "title") ?? GetString(root, "title");
            var cwd = GetString(info, "cwd") ?? GetString(root, "cwd");
            DateTimeOffset? modified = null;
            AddLatestTimestamp(info, ref modified);
            AddLatestTimestamp(root, ref modified);
            var chatPath = Path.Combine(directory, "chat_history.jsonl");
            var chatExists = File.Exists(chatPath);
            if (string.IsNullOrWhiteSpace(title) && chatExists)
                title = await ReadGrokUserPreviewAsync(chatPath, cancellationToken).ConfigureAwait(false);
            return new DisplayMetadata(sessionId, title, cwd, modified, chatExists);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          DecoderFallbackException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadGrokUserPreviewAsync(string path, CancellationToken cancellationToken)
    {
        var prefix = await ReadBoundedPrefixAsync(path, cancellationToken).ConfigureAwait(false);
        using var reader = new StringReader(prefix.Text);
        for (var record = 0; record < MaximumMetadataRecords && reader.ReadLine() is { } line; record++)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (string.Equals(GetString(root, "role") ?? GetString(root, "type"), "user",
                        StringComparison.Ordinal))
                    return Preview(ReadTextContent(root, "input_text"));
            }
            catch (JsonException) { return null; }
        }
        return null;
    }

    private static string? ReadCodexUserPreview(JsonElement payload)
    {
        if (!string.Equals(GetString(payload, "type"), "message", StringComparison.Ordinal) ||
            !string.Equals(GetString(payload, "role"), "user", StringComparison.Ordinal))
            return null;
        return Preview(ReadTextContent(payload, "input_text"));
    }

    private static string? ReadTextContent(JsonElement element, string expectedType)
    {
        if (!element.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object &&
                string.Equals(GetString(block, "type"), expectedType, StringComparison.Ordinal) &&
                GetString(block, "text") is { } text)
                return text;
        }
        return null;
    }

    private static string? Preview(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= MaximumTitleLength ? trimmed : trimmed[..MaximumTitleLength];
    }

    private static void AddLatestTimestamp(JsonElement element, ref DateTimeOffset? latest)
    {
        foreach (var name in new[]
                 {
                     "timestamp", "created_at", "createdAt", "updated_at", "updatedAt", "last_modified_at",
                     "lastModifiedAt"
                 })
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), out var timestamp) &&
                (latest is null || timestamp > latest))
                latest = timestamp;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task<BoundedPrefix> ReadBoundedPrefixAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, FileOptions.SequentialScan);
        var length = checked((int)Math.Min(stream.Length, MaximumMetadataBytes));
        var bytes = new byte[length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            offset += read;
        }
        var isComplete = stream.Length <= MaximumMetadataBytes;
        if (!isComplete)
        {
            var lastNewline = Array.LastIndexOf(bytes, (byte)'\n', offset - 1);
            offset = lastNewline < 0 ? 0 : lastNewline + 1;
        }
        return new BoundedPrefix(Utf8.GetString(bytes, 0, offset), isComplete);
    }

    private static bool IsSafeCodexSessionId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool IsSafeGrokSessionId(string value)
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

    private static string DisplayTitle(string? title, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(title) ? fallback : title.Trim();
        return value.Length <= MaximumTitleLength ? value : value[..MaximumTitleLength];
    }

    private static DateTimeOffset LastWriteTime(string nativePath, bool isDirectory)
    {
        try
        {
            var path = isDirectory ? GrokSessionPackage.ChatHistoryPath(nativePath) : nativePath;
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private sealed record DisplayMetadata(
        string SessionId,
        string? Title,
        string? WorkingDirectory,
        DateTimeOffset? LastModifiedAt,
        bool HasReadableNativeStructure);

    private readonly record struct BoundedPrefix(string Text, bool IsComplete);
}

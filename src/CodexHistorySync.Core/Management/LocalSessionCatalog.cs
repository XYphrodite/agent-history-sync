using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Grok;

namespace CodexHistorySync.Core.Management;

public sealed class LocalSessionCatalog : ILocalSessionCatalog
{
    private const int MaximumTitleLength = 80;
    private const int MaximumMetadataCharacters = 64 * 1024;
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
    private readonly IConversationReader codexReader;
    private readonly IConversationReader grokReader;

    public LocalSessionCatalog(
        CodexPaths? codexPaths,
        GrokPaths? grokPaths,
        IManagedSessionActiveState activeState,
        SessionScanner? codexScanner = null,
        GrokSessionScanner? grokScanner = null)
        : this(
            codexPaths,
            grokPaths,
            activeState,
            codexScanner ?? new SessionScanner(),
            grokScanner ?? new GrokSessionScanner(),
            new CodexConversationReader(),
            new GrokConversationReader())
    {
    }

    internal LocalSessionCatalog(
        CodexPaths? codexPaths,
        GrokPaths? grokPaths,
        IManagedSessionActiveState activeState,
        SessionScanner codexScanner,
        GrokSessionScanner grokScanner,
        IConversationReader codexReader,
        IConversationReader grokReader)
    {
        this.codexPaths = codexPaths;
        this.grokPaths = grokPaths;
        this.activeState = activeState ?? throw new ArgumentNullException(nameof(activeState));
        this.codexScanner = codexScanner ?? throw new ArgumentNullException(nameof(codexScanner));
        this.grokScanner = grokScanner ?? throw new ArgumentNullException(nameof(grokScanner));
        this.codexReader = codexReader ?? throw new ArgumentNullException(nameof(codexReader));
        this.grokReader = grokReader ?? throw new ArgumentNullException(nameof(grokReader));
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

        foreach (var candidate in EnumerateCodexCandidates(codexPaths))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = SelectCodexRoot(candidate, codexPaths);
            if (root is null || !ManagedSessionPathPolicy.TryResolveConcreteTarget(
                    candidate, root, expectDirectory: false, out var nativePath))
                continue;

            var isStable = stable.TryGetValue(nativePath, out var scannedId);
            var sessionId = isStable ? scannedId! : TryReadCodexSessionId(nativePath);
            if (!IsSafeCodexSessionId(sessionId)) continue;

            result.Add(await CreateSessionAsync(
                ManagedAgent.Codex,
                sessionId!,
                nativePath,
                isStable,
                codexReader,
                cancellationToken).ConfigureAwait(false));
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

        foreach (var candidate in EnumerateGrokCandidates(grokPaths.Sessions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ManagedSessionPathPolicy.TryResolveConcreteTarget(
                    candidate, grokPaths.Sessions, expectDirectory: true, out var nativePath))
                continue;
            var sessionId = Path.GetFileName(nativePath);
            if (!IsSafeGrokSessionId(sessionId)) continue;

            result.Add(await CreateSessionAsync(
                ManagedAgent.Grok,
                sessionId,
                nativePath,
                stable.Contains(nativePath),
                grokReader,
                cancellationToken).ConfigureAwait(false));
        }
        return result;
    }

    private async Task<ManagedSession> CreateSessionAsync(
        ManagedAgent agent,
        string sessionId,
        string nativePath,
        bool stable,
        IConversationReader reader,
        CancellationToken cancellationToken)
    {
        bool isActive;
        try
        {
            isActive = await activeState.IsActiveAsync(agent, sessionId, nativePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            isActive = true;
        }

        try
        {
            var conversation = await reader.ReadAsync(nativePath, cancellationToken).ConfigureAwait(false);
            var expectedAgent = agent == ManagedAgent.Codex ? ConversationAgent.Codex : ConversationAgent.Grok;
            var identityMatches = conversation.SourceAgent == expectedAgent &&
                                  string.Equals(conversation.SourceSessionId, sessionId,
                                      StringComparison.OrdinalIgnoreCase);
            if (identityMatches && (stable || isActive))
            {
                return new ManagedSession(
                    agent,
                    sessionId,
                    nativePath,
                    DisplayTitle(conversation.Title, sessionId),
                    conversation.LastModifiedAt,
                    isActive,
                    CanRead: true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or ArgumentException)
        {
            // Keep only the already-established safe identity and target, with actions disabled.
        }

        return new ManagedSession(
            agent,
            sessionId,
            nativePath,
            sessionId,
            LastWriteTime(nativePath, agent == ManagedAgent.Grok),
            isActive,
            CanRead: false);
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
        foreach (var chatPath in EnumerateFiles(root, "chat_history.jsonl"))
        {
            var directory = Path.GetDirectoryName(chatPath);
            if (directory is not null) yield return directory;
        }
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

    private static string? TryReadCodexSessionId(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096, leaveOpen: false);
            var characters = 0;
            for (var record = 0; record < MaximumMetadataRecords; record++)
            {
                var line = reader.ReadLine();
                if (line is null) return null;
                characters = checked(characters + line.Length);
                if (characters > MaximumMetadataCharacters) return null;
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                    !string.Equals(type.GetString(), "session_meta", StringComparison.Ordinal))
                    continue;
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object ||
                    !payload.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
                    return null;
                return id.GetString();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          DecoderFallbackException or JsonException or OverflowException)
        {
            return null;
        }
        return null;
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
}

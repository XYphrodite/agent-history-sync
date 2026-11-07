using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Antigravity;

namespace CodexHistorySync.Core.Conversion;

public sealed class AntigravityConversationWriter : IConversationWriter
{
    private const int MaximumIdAttempts = 10;
    private const int TitlePreviewLength = 80;
    private readonly AntigravityPaths paths;
    private readonly Func<string> idGenerator;
    private readonly IConversationReader validator;
    private readonly Func<DateTimeOffset> utcNow;

    public AntigravityConversationWriter(
        AntigravityPaths paths,
        Func<string>? idGenerator = null,
        Func<DateTimeOffset>? utcNow = null)
        : this(paths, idGenerator, new AntigravityConversationReader(), utcNow)
    {
    }

    internal AntigravityConversationWriter(
        AntigravityPaths paths,
        Func<string>? idGenerator,
        IConversationReader validator,
        Func<DateTimeOffset>? utcNow)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.idGenerator = idGenerator ?? (() => Guid.NewGuid().ToString("D").ToLowerInvariant());
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<ConversationWriteResult> WriteAsync(
        PortableConversation conversation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (conversation.Turns is null || conversation.Turns.Count == 0)
            throw new ArgumentException("A conversation must have at least one turn.", nameof(conversation));

        cancellationToken.ThrowIfCancellationRequested();

        var now = utcNow();
        var lastModified = now;
        var created = conversation.CreatedAt > lastModified ? lastModified : conversation.CreatedAt;
        if (created <= DateTimeOffset.MinValue)
        {
            created = lastModified;
        }

        var createdSeconds = DateTimeOffset.FromUnixTimeSeconds(created.ToUnixTimeSeconds());
        var modifiedSeconds = DateTimeOffset.FromUnixTimeSeconds(lastModified.ToUnixTimeSeconds());
        conversation = conversation with { CreatedAt = createdSeconds, LastModifiedAt = modifiedSeconds };

        var title = DeriveTitle(conversation);

        string[] workspaceUris;
        string? workspaceUri = null;
        if (!string.IsNullOrWhiteSpace(conversation.WorkingDirectory))
        {
            var full = Path.GetFullPath(conversation.WorkingDirectory);
            workspaceUri = full.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                ? full
                : new Uri(full).AbsoluteUri;
            workspaceUris = [workspaceUri];
        }
        else
        {
            workspaceUris = [];
        }

        for (var attempt = 0; attempt < MaximumIdAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rawId = idGenerator();
            if (string.IsNullOrWhiteSpace(rawId) || !AntigravityPaths.IsSessionId(rawId))
                throw new InvalidOperationException($"The generated session ID '{rawId}' is invalid.");

            var sessionId = rawId.ToLowerInvariant();
            var convDb = paths.ConversationDatabasePath(sessionId);

            if ((conversation.SourceAgent == ConversationAgent.Antigravity &&
                 string.Equals(sessionId, conversation.SourceSessionId, StringComparison.OrdinalIgnoreCase)) ||
                File.Exists(convDb))
            {
                continue;
            }

            var anchorPath = paths.AnchorPath(sessionId);
            var brainDir = paths.BrainSessionDirectory(sessionId);
            var transcriptPath = paths.TranscriptPath(sessionId);

            try
            {
                Directory.CreateDirectory(paths.ConversationsDirectory);
                Directory.CreateDirectory(paths.BrainDirectory);
                Directory.CreateDirectory(paths.AnchorRoot);

                // 1. Write conversations/<id>.db
                var steps = new List<AntigravityStepRow>(conversation.Turns.Count);
                var trajectoryId = Guid.NewGuid().ToString("D").ToLowerInvariant();
                var cascadeId = sessionId;
                var currentTurnId = Guid.NewGuid().ToString("D").ToLowerInvariant();
                var lastUserStepIndex = -1;

                for (var i = 0; i < conversation.Turns.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var turn = conversation.Turns[i];
                    var spanHex = Guid.NewGuid().ToString("N");
                    if (turn.Role == ConversationRole.User)
                    {
                        currentTurnId = Guid.NewGuid().ToString("D").ToLowerInvariant();
                        lastUserStepIndex = i;
                        var (metadata, payload) = AntigravityProtobuf.EncodeUserStep(
                            stepIndex: i,
                            text: turn.Text,
                            timestamp: createdSeconds,
                            sessionId: trajectoryId,
                            cascadeId: cascadeId,
                            turnId: currentTurnId,
                            workspaceUri: workspaceUri,
                            spanHex: spanHex);
                        steps.Add(new AntigravityStepRow(i, AntigravityProtobuf.StepTypeUserInput, AntigravityProtobuf.StepStatusDone, payload, metadata));
                    }
                    else if (turn.Role == ConversationRole.Assistant)
                    {
                        var (metadata, payload) = AntigravityProtobuf.EncodeAssistantStep(
                            stepIndex: i,
                            text: turn.Text,
                            timestamp: modifiedSeconds,
                            sessionId: trajectoryId,
                            cascadeId: cascadeId,
                            turnId: currentTurnId,
                            spanHex: spanHex);
                        steps.Add(new AntigravityStepRow(i, AntigravityProtobuf.StepTypePlannerResponse, AntigravityProtobuf.StepStatusDone, payload, metadata));
                    }
                    else
                    {
                        throw new InvalidDataException("The portable conversation has an unsupported role.");
                    }
                }

                AntigravityDatabase.WriteSteps(convDb, trajectoryId, sessionId, steps, workspaceUri, createdSeconds);

                // 2. Write conversation_summaries.db
                var summaryRow = new AntigravitySummaryRow(
                    ConversationId: sessionId,
                    ParentConversationId: string.Empty,
                    NestingDepth: 0,
                    AgentName: string.Empty,
                    Title: title,
                    Status: "CASCADE_RUN_STATUS_IDLE",
                    WorkspaceUris: workspaceUris,
                    CreatedAt: createdSeconds,
                    UpdatedAt: modifiedSeconds);
                AntigravityDatabase.WriteSummary(
                    paths.ConversationSummariesDbPath,
                    summaryRow,
                    stepCount: steps.Count,
                    lastUserStepIndex: lastUserStepIndex,
                    trajectoryId: trajectoryId,
                    preview: title);

                // 3. Write brain/<id>/transcript.jsonl and native .system_generated/logs/transcript.jsonl
                Directory.CreateDirectory(brainDir);
                var nativeLogsDir = Path.Combine(brainDir, ".system_generated", "logs");
                Directory.CreateDirectory(nativeLogsDir);
                var nativeTranscriptPath = Path.Combine(nativeLogsDir, "transcript.jsonl");
                var utf8NoBom = new UTF8Encoding(false);
                var createdIso = createdSeconds.ToUniversalTime().ToString("O");
                var modifiedIso = modifiedSeconds.ToUniversalTime().ToString("O");

                await using (var stream = new FileStream(transcriptPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                await using (var writer = new StreamWriter(stream, utf8NoBom))
                await using (var nativeStream = new FileStream(nativeTranscriptPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                await using (var nativeWriter = new StreamWriter(nativeStream, utf8NoBom))
                {
                    for (var i = 0; i < conversation.Turns.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var turn = conversation.Turns[i];
                        var isUser = turn.Role == ConversationRole.User;
                        using var memory = new MemoryStream();
                        using (var json = new Utf8JsonWriter(memory))
                        {
                            json.WriteStartObject();
                            json.WriteNumber("step_index", i);
                            json.WriteString("source", isUser ? "USER_EXPLICIT" : "MODEL");
                            json.WriteString("type", isUser ? "USER_INPUT" : "PLANNER_RESPONSE");
                            json.WriteString("status", "DONE");
                            json.WriteString("created_at", isUser ? createdIso : modifiedIso);
                            json.WriteString("content", turn.Text);
                            json.WriteEndObject();
                            json.Flush();
                        }
                        var line = Encoding.UTF8.GetString(memory.ToArray());
                        await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                        await nativeWriter.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                    }
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    await nativeWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                // 4. Write anchor snapshot
                AntigravitySessionPackage.WriteAnchorSnapshot(anchorPath);

                // 5. Roundtrip validation
                var roundTrip = await validator.ReadAsync(anchorPath, cancellationToken).ConfigureAwait(false);
                ValidateRoundTrip(conversation, roundTrip, sessionId, title);

                return new ConversationWriteResult(sessionId, anchorPath);
            }
            catch (Exception)
            {
                CleanupStagedSession(sessionId);
                throw;
            }
        }

        throw new IOException("Unable to allocate a unique Antigravity session ID after 10 attempts.");
    }

    private static string DeriveTitle(PortableConversation conversation)
    {
        var title = conversation.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            var firstUserTurn = conversation.Turns.FirstOrDefault(t => t.Role == ConversationRole.User);
            if (firstUserTurn is not null)
            {
                title = firstUserTurn.Text.Trim().Replace("\r", " ").Replace("\n", " ");
            }
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Antigravity Conversation";
        }

        title = title.Replace("\r", " ").Replace("\n", " ").Trim();
        if (title.Length > TitlePreviewLength)
        {
            var len = TitlePreviewLength;
            if (len > 0 && char.IsHighSurrogate(title[len - 1]))
            {
                len--;
            }
            title = title[..len];
        }

        return title;
    }

    private static void ValidateRoundTrip(
        PortableConversation expected,
        PortableConversation actual,
        string sessionId,
        string expectedTitle)
    {
        if (actual.SourceAgent != ConversationAgent.Antigravity ||
            !string.Equals(actual.SourceSessionId, sessionId, StringComparison.Ordinal) ||
            !string.Equals(actual.Title, expectedTitle, StringComparison.Ordinal) ||
            !actual.Turns.SequenceEqual(expected.Turns))
        {
            throw new InvalidDataException("The staged Antigravity conversation failed validation.");
        }
    }

    private void CleanupStagedSession(string sessionId)
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var convDb = paths.ConversationDatabasePath(sessionId);
            if (File.Exists(convDb)) File.Delete(convDb);
            if (File.Exists(convDb + "-wal")) File.Delete(convDb + "-wal");
            if (File.Exists(convDb + "-shm")) File.Delete(convDb + "-shm");

            var brainDir = paths.BrainSessionDirectory(sessionId);
            if (Directory.Exists(brainDir)) Directory.Delete(brainDir, recursive: true);

            var anchorPath = paths.AnchorPath(sessionId);
            if (File.Exists(anchorPath)) File.Delete(anchorPath);

            AntigravityDatabase.DeleteSummary(paths.ConversationSummariesDbPath, sessionId);
        }
        catch
        {
            // Suppress cleanup exceptions to preserve original error
        }
    }
}

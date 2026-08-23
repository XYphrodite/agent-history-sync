using System.Globalization;
using System.Text.Json;
using CodexHistorySync.Core.Grok;

namespace CodexHistorySync.Core.Conversion;

public sealed class GrokConversationWriter : IConversationWriter
{
    private const int MaximumIdAttempts = 10;
    private readonly GrokPaths paths;
    private readonly Func<Guid> idGenerator;
    private readonly IConversationReader validator;
    private readonly IConversationPublisher publisher;
    private readonly IConversationStagingDirectoryFactory stagingFactory;
    private readonly Func<DateTimeOffset> utcNow;

    public GrokConversationWriter(GrokPaths paths, Func<Guid>? idGenerator = null, Func<DateTimeOffset>? utcNow = null)
        : this(paths, idGenerator ?? Guid.CreateVersion7, new GrokConversationReader(), SystemConversationPublisher.Instance, null, utcNow)
    {
    }

    internal GrokConversationWriter(
        GrokPaths paths,
        Func<Guid> idGenerator,
        IConversationReader validator,
        IConversationPublisher publisher,
        IConversationStagingDirectoryFactory? stagingFactory = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        this.stagingFactory = stagingFactory ?? SystemConversationStagingDirectoryFactory.Instance;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<ConversationWriteResult> WriteAsync(
        PortableConversation conversation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (string.IsNullOrWhiteSpace(conversation.WorkingDirectory))
            throw new ArgumentException("A working directory is required for a Grok conversation.", nameof(conversation));

        conversation = conversation with { LastModifiedAt = utcNow() };
        var workingDirectory = Path.GetFullPath(conversation.WorkingDirectory);
        var intendedParent = Path.GetDirectoryName(paths.SessionDirectory(workingDirectory, Guid.Empty.ToString()))!;
        var destinationGuard = ConversationDestinationGuard.Prepare(paths.Home, paths.Sessions, intendedParent);
        var parent = destinationGuard.DestinationDirectory;

        for (var attempt = 0; attempt < MaximumIdAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generatedId = idGenerator();
            var sessionId = generatedId.ToString();
            var destination = Path.Combine(parent, sessionId);
            if (ConversationWriterIdentity.IsSourceSessionId(generatedId, conversation.SourceSessionId) ||
                Directory.Exists(destination) || File.Exists(destination))
                continue;

            destinationGuard.VerifyUnchanged();
            var stagingDirectory = stagingFactory.Create(parent);
            try
            {
                var stagingSession = stagingDirectory.DirectoryPath(sessionId);
                _ = stagingDirectory.FilePath(sessionId, "chat_history.jsonl");
                _ = stagingDirectory.FilePath(sessionId, "summary.json");
                _ = stagingDirectory.FilePath(sessionId, "updates.jsonl");
                _ = stagingDirectory.FilePath(sessionId, "rewind_points.jsonl");
                _ = stagingDirectory.FilePath(sessionId, "signals.json");
                _ = stagingDirectory.FilePath(sessionId, "plan.json");
                Directory.CreateDirectory(stagingSession);
                await WriteChatHistoryAsync(stagingSession, conversation, cancellationToken).ConfigureAwait(false);
                await WriteSummaryAsync(stagingSession, sessionId, workingDirectory, conversation, cancellationToken)
                    .ConfigureAwait(false);
                await WriteUpdatesAsync(stagingSession, sessionId, conversation, cancellationToken).ConfigureAwait(false);
                await WriteRewindPointsAsync(stagingSession, conversation, cancellationToken).ConfigureAwait(false);
                await WriteSignalsAsync(stagingSession, conversation, cancellationToken).ConfigureAwait(false);
                await WritePlanAsync(stagingSession, cancellationToken).ConfigureAwait(false);

                var seal = destinationGuard.Protect(stagingDirectory.Seal());
                _ = GrokSessionPackage.Parse(GrokSessionPackage.BuildFromDirectory(stagingSession));
                var roundTrip = await validator.ReadAsync(stagingSession, cancellationToken).ConfigureAwait(false);
                ValidateRoundTrip(conversation, roundTrip, sessionId, workingDirectory);

                publisher.PublishDirectory(stagingSession, destination, seal);
                return new ConversationWriteResult(sessionId, destination);
            }
            catch (IOException) when (Directory.Exists(destination) || File.Exists(destination))
            {
                continue;
            }
            finally
            {
                _ = stagingDirectory.TryDelete();
            }
        }

        throw new IOException("Unable to allocate a unique Grok session ID after 10 attempts.");
    }

    private static async Task WriteChatHistoryAsync(
        string stagingSession,
        PortableConversation conversation,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(stagingSession, "chat_history.jsonl");
        await using var stream = DurableFile(path);
        foreach (var turn in conversation.Turns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var json = new Utf8JsonWriter(stream))
            {
                json.WriteStartObject();
                json.WriteString("type", RoleName(turn.Role));
                if (turn.Role == ConversationRole.Assistant)
                    json.WriteString("content", turn.Text);
                else
                {
                    json.WriteStartArray("content");
                    json.WriteStartObject();
                    json.WriteString("type", "text");
                    json.WriteString("text", turn.Text);
                    json.WriteEndObject();
                    json.WriteEndArray();
                }
                json.WriteEndObject();
                json.Flush();
            }
            stream.WriteByte((byte)'\n');
        }
        await FlushDurablyAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteSummaryAsync(
        string stagingSession,
        string sessionId,
        string workingDirectory,
        PortableConversation conversation,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(stagingSession, "summary.json");
        await using var stream = DurableFile(path);
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteStartObject("info");
            json.WriteString("id", sessionId);
            json.WriteString("cwd", workingDirectory);
            json.WriteString("title", conversation.Title);
            json.WriteString("created_at", conversation.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("updated_at", conversation.LastModifiedAt.ToString("O", CultureInfo.InvariantCulture));
            json.WriteEndObject();
            json.WriteString("generated_title", conversation.Title);
            json.WriteString("session_summary", conversation.Title);
            json.WriteString("created_at", conversation.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("updated_at", conversation.LastModifiedAt.ToString("O", CultureInfo.InvariantCulture));
            json.WriteNumber("num_messages", conversation.Turns.Count);
            json.WriteNumber("num_chat_messages", conversation.Turns.Count);
            json.WriteString("current_model_id", "grok-4.6");
            json.WriteNumber("next_trace_turn", Math.Max(1, conversation.Turns.Count(turn => turn.Role == ConversationRole.User)));
            json.WriteNumber("chat_format_version", 1);
            json.WriteBoolean("title_is_manual", true);
            json.WriteEndObject();
            json.Flush();
        }
        await FlushDurablyAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteUpdatesAsync(
        string stagingSession,
        string sessionId,
        PortableConversation conversation,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(stagingSession, "updates.jsonl");
        var unixSeconds = conversation.LastModifiedAt.ToUnixTimeSeconds();
        var unixMs = conversation.LastModifiedAt.ToUnixTimeMilliseconds();
        await using var stream = DurableFile(path);
        var eventIndex = 0;
        var promptIndex = 0;
        foreach (var turn in conversation.Turns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var json = new Utf8JsonWriter(stream))
            {
                json.WriteStartObject();
                json.WriteNumber("timestamp", unixSeconds);
                json.WriteString("method", "session/update");
                json.WriteStartObject("params");
                json.WriteString("sessionId", sessionId);
                json.WriteStartObject("update");
                json.WriteString(
                    "sessionUpdate",
                    turn.Role == ConversationRole.User ? "user_message_chunk" : "agent_message_chunk");
                json.WriteStartObject("content");
                json.WriteString("type", "text");
                json.WriteString("text", turn.Text);
                json.WriteEndObject();
                if (turn.Role == ConversationRole.User)
                {
                    json.WriteStartObject("_meta");
                    json.WriteNumber("promptIndex", promptIndex);
                    json.WriteEndObject();
                }
                json.WriteEndObject();
                json.WriteStartObject("_meta");
                json.WriteString("eventId", sessionId + "-" + eventIndex.ToString(CultureInfo.InvariantCulture));
                json.WriteNumber("agentTimestampMs", unixMs);
                json.WriteEndObject();
                json.WriteEndObject();
                json.WriteEndObject();
                json.Flush();
            }
            stream.WriteByte((byte)'\n');
            eventIndex++;
            if (turn.Role == ConversationRole.User) promptIndex++;
        }
        await FlushDurablyAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteRewindPointsAsync(
        string stagingSession,
        PortableConversation conversation,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(stagingSession, "rewind_points.jsonl");
        var created = conversation.LastModifiedAt.ToString("O", CultureInfo.InvariantCulture);
        await using var stream = DurableFile(path);
        var promptIndex = 0;
        foreach (var turn in conversation.Turns)
        {
            if (turn.Role != ConversationRole.User) continue;
            cancellationToken.ThrowIfCancellationRequested();
            using (var json = new Utf8JsonWriter(stream))
            {
                json.WriteStartObject();
                json.WriteNumber("prompt_index", promptIndex);
                json.WriteString("created_at", created);
                json.WriteStartObject("file_snapshots");
                json.WriteEndObject();
                json.WriteStartObject("after_snapshots");
                json.WriteEndObject();
                json.WriteEndObject();
                json.Flush();
            }
            stream.WriteByte((byte)'\n');
            promptIndex++;
        }
        await FlushDurablyAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteSignalsAsync(
        string stagingSession,
        PortableConversation conversation,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(stagingSession, "signals.json");
        var userCount = conversation.Turns.Count(turn => turn.Role == ConversationRole.User);
        var assistantCount = conversation.Turns.Count(turn => turn.Role == ConversationRole.Assistant);
        await using var stream = DurableFile(path);
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("turnCount", userCount);
            json.WriteNumber("userMessageCount", userCount);
            json.WriteNumber("assistantMessageCount", assistantCount);
            json.WriteNumber("errorCount", 0);
            json.WriteNumber("toolCallCount", 0);
            json.WriteEndObject();
            json.Flush();
        }
        await FlushDurablyAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WritePlanAsync(string stagingSession, CancellationToken cancellationToken)
    {
        var path = Path.Combine(stagingSession, "plan.json");
        await using var stream = DurableFile(path);
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteStartObject("todos");
            json.WriteEndObject();
            json.WriteEndObject();
            json.Flush();
        }
        await FlushDurablyAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static FileStream DurableFile(string path) => new(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 4096,
        FileOptions.WriteThrough);

    private static async Task FlushDurablyAsync(FileStream stream, CancellationToken cancellationToken)
    {
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static string RoleName(ConversationRole role) => role switch
    {
        ConversationRole.User => "user",
        ConversationRole.Assistant => "assistant",
        _ => throw new InvalidDataException("The portable conversation has an unsupported role.")
    };

    private static void ValidateRoundTrip(
        PortableConversation expected,
        PortableConversation actual,
        string sessionId,
        string workingDirectory)
    {
        if (actual.SourceAgent != ConversationAgent.Grok ||
            !string.Equals(actual.SourceSessionId, sessionId, StringComparison.Ordinal) ||
            !string.Equals(actual.Title, expected.Title, StringComparison.Ordinal) ||
            !string.Equals(actual.WorkingDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase) ||
            actual.CreatedAt != expected.CreatedAt ||
            actual.LastModifiedAt != expected.LastModifiedAt ||
            !actual.Turns.SequenceEqual(expected.Turns))
            throw new InvalidDataException("The staged Grok conversation failed validation.");
    }
}

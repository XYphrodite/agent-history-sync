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

    public GrokConversationWriter(GrokPaths paths, Func<Guid>? idGenerator = null)
        : this(paths, idGenerator ?? Guid.NewGuid, new GrokConversationReader(), SystemConversationPublisher.Instance, null)
    {
    }

    internal GrokConversationWriter(
        GrokPaths paths,
        Func<Guid> idGenerator,
        IConversationReader validator,
        IConversationPublisher publisher,
        IConversationStagingDirectoryFactory? stagingFactory = null)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        this.stagingFactory = stagingFactory ?? SystemConversationStagingDirectoryFactory.Instance;
    }

    public async Task<ConversationWriteResult> WriteAsync(
        PortableConversation conversation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (string.IsNullOrWhiteSpace(conversation.WorkingDirectory))
            throw new ArgumentException("A working directory is required for a Grok conversation.", nameof(conversation));

        var workingDirectory = Path.GetFullPath(conversation.WorkingDirectory);
        var parent = Path.GetDirectoryName(paths.SessionDirectory(workingDirectory, Guid.Empty.ToString()))!;
        Directory.CreateDirectory(parent);

        for (var attempt = 0; attempt < MaximumIdAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generatedId = idGenerator();
            var sessionId = generatedId.ToString();
            var destination = paths.SessionDirectory(workingDirectory, sessionId);
            if (ConversationWriterIdentity.IsSourceSessionId(generatedId, conversation.SourceSessionId) ||
                Directory.Exists(destination) || File.Exists(destination))
                continue;

            var stagingDirectory = stagingFactory.Create(parent);
            try
            {
                var stagingSession = stagingDirectory.DirectoryPath(sessionId);
                _ = stagingDirectory.FilePath(sessionId, "chat_history.jsonl");
                _ = stagingDirectory.FilePath(sessionId, "summary.json");
                Directory.CreateDirectory(stagingSession);
                await WriteChatHistoryAsync(stagingSession, conversation, cancellationToken).ConfigureAwait(false);
                await WriteSummaryAsync(stagingSession, sessionId, workingDirectory, conversation, cancellationToken)
                    .ConfigureAwait(false);

                var seal = stagingDirectory.Seal();
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
                json.WriteString("role", RoleName(turn.Role));
                json.WriteStartArray("content");
                json.WriteStartObject();
                json.WriteString("type", turn.Role == ConversationRole.User ? "input_text" : "output_text");
                json.WriteString("text", turn.Text);
                json.WriteEndObject();
                json.WriteEndArray();
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

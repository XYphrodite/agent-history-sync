using System.Globalization;
using System.Text.Json;
using CodexHistorySync.Core.Claude;

namespace CodexHistorySync.Core.Conversion;

public sealed class ClaudeConversationWriter : IConversationWriter
{
    private const int MaximumIdAttempts = 10;
    private const string WriterVersion = "0.6.0";
    private readonly ClaudePaths paths;
    private readonly Func<Guid> idGenerator;
    private readonly IConversationReader validator;
    private readonly IConversationPublisher publisher;
    private readonly IConversationStagingDirectoryFactory stagingFactory;
    private readonly Func<DateTimeOffset> utcNow;

    public ClaudeConversationWriter(ClaudePaths paths, Func<Guid>? idGenerator = null, Func<DateTimeOffset>? utcNow = null)
        : this(paths, idGenerator ?? Guid.NewGuid, new ClaudeConversationReader(), SystemConversationPublisher.Instance, null, utcNow)
    {
    }

    internal ClaudeConversationWriter(
        ClaudePaths paths,
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
            throw new ArgumentException("A working directory is required for a Claude conversation.", nameof(conversation));

        conversation = conversation with { LastModifiedAt = utcNow() };
        var workingDirectory = Path.GetFullPath(conversation.WorkingDirectory);
        var project = ClaudePaths.EncodeProjectSegment(workingDirectory);
        var destinationGuard = ConversationDestinationGuard.Prepare(
            paths.Home,
            paths.Projects,
            Path.Combine(paths.Projects, project));
        var parent = destinationGuard.DestinationDirectory;

        for (var attempt = 0; attempt < MaximumIdAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generatedId = idGenerator();
            var sessionId = generatedId.ToString();
            var destination = Path.Combine(parent, sessionId + ".jsonl");
            // Never reuse the source id: a copy is a new session, and the original must stay intact.
            if (ConversationWriterIdentity.IsSourceSessionId(generatedId, conversation.SourceSessionId) ||
                File.Exists(destination) || Directory.Exists(destination))
                continue;

            destinationGuard.VerifyUnchanged();
            var stagingDirectory = stagingFactory.Create(parent);
            try
            {
                var staging = stagingDirectory.FilePath(sessionId + ".jsonl");
                await WriteTranscriptAsync(staging, sessionId, workingDirectory, conversation, cancellationToken)
                    .ConfigureAwait(false);

                var seal = destinationGuard.Protect(stagingDirectory.Seal());
                _ = ClaudeSessionPackage.Parse(ClaudeSessionPackage.BuildFromFile(staging));
                var roundTrip = await validator.ReadAsync(staging, cancellationToken).ConfigureAwait(false);
                ValidateRoundTrip(conversation, roundTrip, sessionId, workingDirectory);

                publisher.PublishFile(staging, destination, seal);
                return new ConversationWriteResult(sessionId, destination);
            }
            catch (IOException) when (File.Exists(destination) || Directory.Exists(destination))
            {
                continue;
            }
            finally
            {
                _ = stagingDirectory.TryDelete();
            }
        }

        throw new IOException("Unable to allocate a unique Claude session ID after 10 attempts.");
    }

    private static async Task WriteTranscriptAsync(
        string staging,
        string sessionId,
        string workingDirectory,
        PortableConversation conversation,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            staging,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);

        string? parentUuid = null;
        for (var index = 0; index < conversation.Turns.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var turn = conversation.Turns[index];
            var uuid = Guid.NewGuid().ToString();
            // The first turn anchors CreatedAt and the last one LastModifiedAt, so a read back
            // recovers both ends of the range from the records themselves.
            var timestamp = index == 0 ? conversation.CreatedAt : conversation.LastModifiedAt;
            var chainedParent = parentUuid;
            WriteRecord(stream, json =>
            {
                json.WriteStartObject();
                if (chainedParent is null) json.WriteNull("parentUuid");
                else json.WriteString("parentUuid", chainedParent);
                json.WriteBoolean("isSidechain", false);
                json.WriteString("type", RoleName(turn.Role));
                json.WriteString("uuid", uuid);
                json.WriteString("timestamp", Timestamp(timestamp));
                json.WriteString("sessionId", sessionId);
                json.WriteString("cwd", workingDirectory);
                json.WriteString("version", WriterVersion);
                json.WriteString("gitBranch", string.Empty);
                json.WriteStartObject("message");
                json.WriteString("role", RoleName(turn.Role));
                json.WriteStartArray("content");
                json.WriteStartObject();
                json.WriteString("type", "text");
                json.WriteString("text", turn.Text);
                json.WriteEndObject();
                json.WriteEndArray();
                json.WriteEndObject();
                json.WriteEndObject();
            });
            parentUuid = uuid;
        }

        // Claude's own title record. Without it a read back would fall back to the first user turn
        // and lose the title the source agent carried. The timestamp is ours, not Claude's, and it
        // is what makes LastModifiedAt recoverable from a single-turn conversation.
        WriteRecord(stream, json =>
        {
            json.WriteStartObject();
            json.WriteString("type", "ai-title");
            json.WriteString("aiTitle", conversation.Title);
            json.WriteString("sessionId", sessionId);
            json.WriteString("timestamp", Timestamp(conversation.LastModifiedAt));
            json.WriteEndObject();
        });

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static void WriteRecord(Stream stream, Action<Utf8JsonWriter> write)
    {
        using (var json = new Utf8JsonWriter(stream))
        {
            write(json);
            json.Flush();
        }
        stream.WriteByte((byte)'\n');
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

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
        if (actual.SourceAgent != ConversationAgent.Claude ||
            !string.Equals(actual.SourceSessionId, sessionId, StringComparison.Ordinal) ||
            !string.Equals(actual.Title, expected.Title, StringComparison.Ordinal) ||
            !string.Equals(actual.WorkingDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase) ||
            actual.CreatedAt != expected.CreatedAt ||
            actual.LastModifiedAt != expected.LastModifiedAt ||
            !actual.Turns.SequenceEqual(expected.Turns))
            throw new InvalidDataException("The staged Claude conversation failed validation.");
    }
}

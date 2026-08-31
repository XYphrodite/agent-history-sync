using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexHistorySync.Core.Continue;

namespace CodexHistorySync.Core.Conversion;

/// <summary>
/// Writes a portable conversation as a new Continue session.
///
/// The session file alone is not enough: Continue lists what is in <c>sessions.json</c>, so a
/// session written without an entry exists and cannot be opened from the UI. The entry is
/// therefore merged in after the file is published, and it is the only place the conversation's
/// creation time can be recorded — a Continue session file carries no timestamps at all.
/// </summary>
public sealed class ContinueConversationWriter : IConversationWriter
{
    private const int MaximumIdAttempts = 10;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions SessionOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ContinuePaths paths;
    private readonly Func<Guid> idGenerator;
    private readonly IConversationReader validator;
    private readonly IConversationPublisher publisher;
    private readonly IConversationStagingDirectoryFactory stagingFactory;
    private readonly Func<DateTimeOffset> utcNow;

    public ContinueConversationWriter(ContinuePaths paths, Func<Guid>? idGenerator = null, Func<DateTimeOffset>? utcNow = null)
        : this(paths, idGenerator ?? Guid.NewGuid, new ContinueConversationReader(), SystemConversationPublisher.Instance,
            null, utcNow)
    {
    }

    internal ContinueConversationWriter(
        ContinuePaths paths,
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
        if (conversation.Turns.Count == 0)
            throw new ArgumentException("A conversation must carry at least one turn.", nameof(conversation));

        conversation = conversation with { LastModifiedAt = utcNow() };
        var workspace = ToWorkspaceUri(conversation.WorkingDirectory);
        var destinationGuard = ConversationDestinationGuard.Prepare(paths.Home, paths.Sessions, paths.Sessions);
        var parent = destinationGuard.DestinationDirectory;

        for (var attempt = 0; attempt < MaximumIdAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generatedId = idGenerator();
            var sessionId = generatedId.ToString();
            var destination = Path.Combine(parent, sessionId + ".json");
            // Never reuse the source id: a copy is a new session, and the original must stay intact.
            if (ConversationWriterIdentity.IsSourceSessionId(generatedId, conversation.SourceSessionId) ||
                File.Exists(destination) || Directory.Exists(destination))
                continue;

            destinationGuard.VerifyUnchanged();
            var stagingDirectory = stagingFactory.Create(parent);
            try
            {
                var staging = stagingDirectory.FilePath(sessionId + ".json");
                await WriteSessionAsync(staging, sessionId, workspace, conversation, cancellationToken)
                    .ConfigureAwait(false);

                var seal = destinationGuard.Protect(stagingDirectory.Seal());
                var roundTrip = await validator.ReadAsync(staging, cancellationToken).ConfigureAwait(false);
                ValidateRoundTrip(conversation, roundTrip, sessionId);

                publisher.PublishFile(staging, destination, seal);
                await MergeIndexEntryAsync(sessionId, workspace, conversation, cancellationToken).ConfigureAwait(false);
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

        throw new IOException("Unable to allocate a unique Continue session ID after 10 attempts.");
    }

    private static async Task WriteSessionAsync(
        string staging,
        string sessionId,
        string workspace,
        PortableConversation conversation,
        CancellationToken cancellationToken)
    {
        var history = new JsonArray();
        foreach (var turn in conversation.Turns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The shapes are not symmetric in Continue's own files: a user message holds content
            // parts, an assistant message a bare string. Writing both as parts would read back
            // through Continue's UI as an empty assistant turn.
            JsonNode content = turn.Role == ConversationRole.User
                ? new JsonArray { new JsonObject { ["type"] = "text", ["text"] = turn.Text } }
                : JsonValue.Create(turn.Text)!;
            history.Add(new JsonObject
            {
                ["message"] = new JsonObject { ["role"] = RoleName(turn.Role), ["content"] = content },
                ["contextItems"] = new JsonArray()
            });
        }

        var session = new JsonObject
        {
            ["sessionId"] = sessionId,
            ["title"] = conversation.Title,
            ["workspaceDirectory"] = workspace,
            ["history"] = history,
            ["mode"] = "agent"
        };

        await using var stream = new FileStream(
            staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, FileOptions.WriteThrough);
        var bytes = Utf8.GetBytes(session.ToJsonString(SessionOptions));
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Adds the session to the shared list. Merged rather than rewritten, and appended, which is
    /// where Continue appends and therefore where its reversed listing shows it first.
    /// </summary>
    private async Task MergeIndexEntryAsync(
        string sessionId,
        string workspace,
        PortableConversation conversation,
        CancellationToken cancellationToken)
    {
        var indexPath = paths.IndexFilePath;
        var current = File.Exists(indexPath)
            ? await File.ReadAllTextAsync(indexPath, Utf8, cancellationToken).ConfigureAwait(false)
            : null;
        var entry = ContinueSessionIndex.Synthesize(
            sessionId, conversation.Title, workspace, conversation.CreatedAt, conversation.Turns.Count);
        var merged = ContinueSessionIndex.Merge(current, entry);

        var temporary = indexPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, merged, Utf8, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, indexPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Renders a working directory the way Continue records one: a file URI with the drive colon
    /// percent-encoded. A conversation that carries no directory gets an empty string, which stays
    /// a string so Continue's workspace filter still compares safely.
    /// </summary>
    internal static string ToWorkspaceUri(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory)) return string.Empty;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
            var uri = new Uri(full).AbsoluteUri;
            var root = Path.GetPathRoot(full);
            if (root is { Length: >= 2 } && root[1] == ':')
                uri = uri.Replace($"{root[0]}:", $"{char.ToLowerInvariant(root[0])}%3A", StringComparison.Ordinal);
            return uri;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException
                                             or UriFormatException)
        {
            return string.Empty;
        }
    }

    private static string RoleName(ConversationRole role) => role switch
    {
        ConversationRole.User => "user",
        ConversationRole.Assistant => "assistant",
        _ => throw new InvalidDataException("The portable conversation has an unsupported role.")
    };

    /// <summary>
    /// Timestamps are deliberately absent from the comparison: a Continue session file records
    /// none, so the staged file cannot round-trip them, and the entry that will carry the creation
    /// time is not written until the file is published.
    /// </summary>
    private static void ValidateRoundTrip(PortableConversation expected, PortableConversation actual, string sessionId)
    {
        if (actual.SourceAgent != ConversationAgent.Continue ||
            !string.Equals(actual.SourceSessionId, sessionId, StringComparison.Ordinal) ||
            !string.Equals(actual.Title, expected.Title, StringComparison.Ordinal) ||
            !actual.Turns.SequenceEqual(expected.Turns))
            throw new InvalidDataException("The staged Continue conversation failed validation.");
    }
}

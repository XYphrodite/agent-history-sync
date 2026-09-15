using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Kimi;

namespace CodexHistorySync.Core.Conversion;

/// <summary>
/// Writes a portable conversation as a new native Kimi Code session: state.json plus a minimal
/// agents/main/wire.jsonl, registered in the shared session index. The synthesized wire carries
/// one agent.message.appended event per turn; whether Kimi can resume such a synthesized session
/// is not verified (see docs), but the package validates, hashes stably, and shows up in the
/// catalog, search, and viewer like any other session.
/// </summary>
public sealed class KimiConversationWriter : IConversationWriter
{
    private const int MaximumIdAttempts = 10;
    private readonly KimiPaths paths;
    private readonly Func<Guid> idGenerator;
    private readonly IConversationReader validator;
    private readonly IConversationPublisher publisher;
    private readonly IConversationStagingDirectoryFactory stagingFactory;
    private readonly Func<DateTimeOffset> utcNow;

    public KimiConversationWriter(KimiPaths paths, Func<Guid>? idGenerator = null, Func<DateTimeOffset>? utcNow = null)
        : this(paths, idGenerator ?? Guid.CreateVersion7, new KimiConversationReader(), SystemConversationPublisher.Instance, null, utcNow)
    {
    }

    internal KimiConversationWriter(
        KimiPaths paths,
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
            throw new ArgumentException("A working directory is required for a Kimi conversation.", nameof(conversation));

        conversation = conversation with { LastModifiedAt = utcNow() };
        // state.json records millisecond epochs; truncate before writing so the round trip reads
        // back exactly what was staged.
        conversation = conversation with
        {
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(conversation.CreatedAt.ToUnixTimeMilliseconds()),
            LastModifiedAt = DateTimeOffset.FromUnixTimeMilliseconds(conversation.LastModifiedAt.ToUnixTimeMilliseconds())
        };
        var workingDirectory = Path.GetFullPath(conversation.WorkingDirectory);
        var workDirKey = KimiPaths.ComputeWorkDirKey(workingDirectory);
        var intendedParent = Path.GetDirectoryName(
            paths.SessionDirectory(workDirKey, KimiPaths.SessionIdPrefix + Guid.Empty.ToString()))!;
        var destinationGuard = ConversationDestinationGuard.Prepare(paths.Home, paths.Sessions, intendedParent);
        var parent = destinationGuard.DestinationDirectory;

        for (var attempt = 0; attempt < MaximumIdAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generatedId = idGenerator();
            var sessionId = generatedId.ToString();
            var sessionName = KimiPaths.SessionIdPrefix + sessionId;
            var destination = Path.Combine(parent, sessionName);
            if (ConversationWriterIdentity.IsSourceSessionId(generatedId, conversation.SourceSessionId) ||
                Directory.Exists(destination) || File.Exists(destination))
                continue;

            destinationGuard.VerifyUnchanged();
            var stagingDirectory = stagingFactory.Create(parent);
            try
            {
                var stagingSession = stagingDirectory.DirectoryPath(sessionName);
                var statePath = stagingDirectory.FilePath(sessionName, KimiSessionPackage.StateFileName);
                var wirePath = stagingDirectory.FilePath(
                    sessionName, "agents", "main", KimiSessionPackage.WireFileName);
                Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(wirePath)!);
                await WriteStateAsync(statePath, sessionName, destination, workingDirectory, conversation, cancellationToken)
                    .ConfigureAwait(false);
                await WriteWireAsync(wirePath, workDirKey, sessionId, conversation, cancellationToken)
                    .ConfigureAwait(false);

                var seal = destinationGuard.Protect(stagingDirectory.Seal());
                _ = KimiSessionPackage.Parse(
                    KimiSessionPackage.BuildFromDirectory(stagingSession, workDirKey));
                var roundTrip = await validator.ReadAsync(stagingSession, cancellationToken).ConfigureAwait(false);
                ValidateRoundTrip(conversation, roundTrip, sessionId, workingDirectory);

                publisher.PublishDirectory(stagingSession, destination, seal);
                MergeIndexEntry(sessionName, destination, workingDirectory);
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

        throw new IOException("Unable to allocate a unique Kimi session ID after 10 attempts.");
    }

    private static async Task WriteStateAsync(
        string path,
        string sessionName,
        string destination,
        string workingDirectory,
        PortableConversation conversation,
        CancellationToken cancellationToken)
    {
        await using var stream = DurableFile(path);
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("id", sessionName);
            json.WriteNumber("version", 2);
            json.WriteString("cwd", workingDirectory.Replace('\\', '/'));
            json.WriteNumber("createdAt", conversation.CreatedAt.ToUnixTimeMilliseconds());
            json.WriteNumber("updatedAt", conversation.LastModifiedAt.ToUnixTimeMilliseconds());
            json.WriteBoolean("archived", false);
            json.WriteStartObject("agents");
            json.WriteStartObject("main");
            // The published session's own directory, not the staging one.
            json.WriteString("homedir", (destination + "/agents/main").Replace('\\', '/'));
            json.WriteString("type", "main");
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteStartObject("custom");
            json.WriteEndObject();
            json.WriteString("lastPrompt",
                conversation.Turns.LastOrDefault(turn => turn.Role == ConversationRole.User)?.Text);
            json.WriteString("title", conversation.Title);
            json.WriteString("titleKind", "replaceable");
            json.WriteBoolean("isCustomTitle", true);
            json.WriteEndObject();
            json.Flush();
        }
        await FlushDurablyAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteWireAsync(
        string path,
        string workDirKey,
        string sessionId,
        PortableConversation conversation,
        CancellationToken cancellationToken)
    {
        var startedAtMs = conversation.CreatedAt.ToUnixTimeMilliseconds();
        var finishedAtMs = conversation.LastModifiedAt.ToUnixTimeMilliseconds();
        await using var stream = DurableFile(path);
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true))
        {
            await writer.WriteLineAsync(
                "{\"type\":\"metadata\",\"protocol_version\":\"1.5\",\"created_at\":" + startedAtMs + "}");
            await writer.WriteLineAsync(
                "{\"type\":\"runtime.set_binding\",\"workspaceId\":\"" + workDirKey + "\",\"runtimeId\":\"local\",\"agentId\":\"main\",\"time\":" +
                startedAtMs + "}");
            long timestamp = startedAtMs;
            var delta = Math.Max(0, finishedAtMs - startedAtMs);
            var step = conversation.Turns.Count == 0 ? 0 : Math.Max(1, delta / conversation.Turns.Count);
            foreach (var turn in conversation.Turns)
            {
                cancellationToken.ThrowIfCancellationRequested();
                timestamp = Math.Min(timestamp + step, finishedAtMs);
                WriteAppendedEvent(writer, turn, timestamp);
            }
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        await FlushDurablyAsync(stream, cancellationToken).ConfigureAwait(false);

        static void WriteAppendedEvent(StreamWriter writer, PortableTurn turn, long timestamp)
        {
            var role = turn.Role == ConversationRole.User ? "user" : "assistant";
            using var output = new MemoryStream();
            using (var json = new Utf8JsonWriter(output))
            {
                json.WriteStartObject();
                json.WritePropertyName("message"u8);
                json.WriteStartObject();
                json.WritePropertyName("message"u8);
                json.WriteStartObject();
                json.WriteString("role", role);
                json.WritePropertyName("content"u8);
                json.WriteStartArray();
                json.WriteStartObject();
                json.WriteString("type", "text");
                json.WriteString("text", turn.Text);
                json.WriteEndObject();
                json.WriteEndArray();
                json.WriteEndObject();
                json.WritePropertyName("meta"u8);
                json.WriteStartObject();
                json.WriteEndObject();
                json.WriteEndObject();
                json.WriteString("type", "agent.message.appended");
                json.WriteNumber("time", timestamp);
                json.WriteEndObject();
                json.Flush();
            }
            writer.Write(Encoding.UTF8.GetString(output.ToArray()));
            writer.Write('\n');
        }
    }

    /// <summary>Adds the session to the shared index, preserving every line already there.</summary>
    private void MergeIndexEntry(string sessionName, string destination, string workingDirectory)
    {
        var indexPath = paths.IndexFilePath;
        string? current = null;
        if (File.Exists(indexPath)) current = File.ReadAllText(indexPath);
        var merged = KimiSessionIndex.Merge(current,
            KimiSessionIndex.CreateEntry(sessionName, destination.Replace('\\', '/'), workingDirectory.Replace('\\', '/')));
        if (string.Equals(current, merged, StringComparison.Ordinal)) return;
        var temporary = indexPath + ".tmp";
        try
        {
            File.WriteAllText(temporary, merged, new UTF8Encoding(false));
            File.Move(temporary, indexPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
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

    private static void ValidateRoundTrip(
        PortableConversation expected,
        PortableConversation actual,
        string sessionId,
        string workingDirectory)
    {
        if (actual.SourceAgent != ConversationAgent.Kimi ||
            !string.Equals(actual.SourceSessionId, sessionId, StringComparison.Ordinal) ||
            !string.Equals(actual.Title, expected.Title, StringComparison.Ordinal) ||
            !string.Equals(actual.WorkingDirectory, workingDirectory.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
            actual.CreatedAt != expected.CreatedAt ||
            actual.LastModifiedAt != expected.LastModifiedAt ||
            !actual.Turns.SequenceEqual(expected.Turns))
            throw new InvalidDataException("The staged Kimi conversation failed validation.");
    }
}

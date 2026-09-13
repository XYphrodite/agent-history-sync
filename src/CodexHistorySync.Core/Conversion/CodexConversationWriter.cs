using System.Globalization;
using System.Text.Json;
using CodexHistorySync.Core.Codex;

namespace CodexHistorySync.Core.Conversion;

public enum CodexExecutableAvailability
{
    Configured,
    Discovered,
    AutomaticDiscoveryAbsent
}

public sealed record CodexExecutableOption(
    string? ExecutablePath,
    CodexExecutableAvailability Availability);

public sealed class CodexConversationWriter : IConversationWriter
{
    private const int MaximumIdAttempts = 10;
    private readonly CodexPaths paths;
    private readonly CodexExecutableOption executable;
    private readonly Func<Guid> idGenerator;
    private readonly IConversationReader validator;
    private readonly IConversationPublisher publisher;
    private readonly Func<string, string, CancellationToken, Task<CompatibilityResult>> compatibilityProbe;
    private readonly IConversationStagingDirectoryFactory stagingFactory;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<string, string, CancellationToken, Task<ICodexSessionRegistration>>? registrationFactory;

    public CodexConversationWriter(
        CodexPaths paths,
        CodexExecutableOption executable,
        CodexCompatibilityProbe compatibilityProbe,
        Func<Guid>? idGenerator = null,
        Func<DateTimeOffset>? utcNow = null)
        : this(
            paths,
            executable,
            idGenerator ?? Guid.NewGuid,
            new CodexConversationReader(),
            SystemConversationPublisher.Instance,
            (compatibilityProbe ?? throw new ArgumentNullException(nameof(compatibilityProbe))).ProbeConversationAsync,
            null,
            utcNow,
            async (path, home, token) => await CodexSessionRegistrar.StartAsync(path, home, token).ConfigureAwait(false))
    {
    }

    internal CodexConversationWriter(
        CodexPaths paths,
        CodexExecutableOption executable,
        Func<Guid> idGenerator,
        IConversationReader validator,
        IConversationPublisher publisher,
        Func<string, string, CancellationToken, Task<CompatibilityResult>> compatibilityProbe,
        IConversationStagingDirectoryFactory? stagingFactory = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<string, string, CancellationToken, Task<ICodexSessionRegistration>>? registrationFactory = null)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.executable = executable ?? throw new ArgumentNullException(nameof(executable));
        this.idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        this.compatibilityProbe = compatibilityProbe ?? throw new ArgumentNullException(nameof(compatibilityProbe));
        this.stagingFactory = stagingFactory ?? SystemConversationStagingDirectoryFactory.Instance;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.registrationFactory = registrationFactory;
    }

    public async Task<ConversationWriteResult> WriteAsync(
        PortableConversation conversation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (string.IsNullOrWhiteSpace(conversation.WorkingDirectory))
            throw new ArgumentException("A working directory is required for a Codex conversation.", nameof(conversation));
        conversation = conversation with { LastModifiedAt = utcNow() };
        var executablePath = ResolveExecutablePath();
        var createdUtc = conversation.CreatedAt.UtcDateTime;
        var destinationDirectory = Path.Combine(
            paths.Sessions,
            createdUtc.ToString("yyyy", CultureInfo.InvariantCulture),
            createdUtc.ToString("MM", CultureInfo.InvariantCulture),
            createdUtc.ToString("dd", CultureInfo.InvariantCulture));
        var destinationGuard = ConversationDestinationGuard.Prepare(
            paths.Home,
            paths.Sessions,
            destinationDirectory);
        destinationDirectory = destinationGuard.DestinationDirectory;
        await using var registration = executablePath is not null && registrationFactory is not null
            ? await registrationFactory(executablePath, paths.Home, cancellationToken).ConfigureAwait(false)
            : null;

        for (var attempt = 0; attempt < MaximumIdAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generatedId = idGenerator();
            var sessionId = generatedId.ToString();
            var destination = Path.Combine(
                destinationDirectory,
                $"rollout-{createdUtc:yyyy-MM-dd'T'HH-mm-ss}-{sessionId}.jsonl");
            if (ConversationWriterIdentity.IsSourceSessionId(generatedId, conversation.SourceSessionId) ||
                File.Exists(destination) || Directory.Exists(destination))
                continue;

            destinationGuard.VerifyUnchanged();
            var stagingDirectory = stagingFactory.Create(destinationDirectory);
            var published = false;
            try
            {
                var staging = stagingDirectory.FilePath(Path.GetFileName(destination));
                await WriteRolloutAsync(staging, sessionId, conversation, registration?.ModelProvider ?? "openai", cancellationToken)
                    .ConfigureAwait(false);
                var seal = destinationGuard.Protect(stagingDirectory.Seal());
                var roundTrip = await validator.ReadAsync(staging, cancellationToken).ConfigureAwait(false);
                ValidateRoundTrip(conversation, roundTrip, sessionId);

                if (executablePath is not null)
                {
                    var compatibility = await compatibilityProbe(executablePath, staging, cancellationToken)
                        .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!compatibility.IsCompatible)
                        throw new InvalidDataException("The staged Codex conversation failed the compatibility probe.");
                }

                publisher.PublishFile(staging, destination, seal);
                published = true;
                if (registration is not null)
                {
                    try
                    {
                        await registration.RegisterAsync(sessionId, destination, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or
                        OperationCanceledException or JsonException)
                    {
                        throw new CodexSessionRegistrationException(exception);
                    }
                }
                return new ConversationWriteResult(sessionId, destination);
            }
            catch (IOException) when (!published && (File.Exists(destination) || Directory.Exists(destination)))
            {
                continue;
            }
            finally
            {
                _ = stagingDirectory.TryDelete();
            }
        }

        throw new IOException("Unable to allocate a unique Codex session ID after 10 attempts.");
    }

    private string? ResolveExecutablePath()
    {
        return executable.Availability switch
        {
            CodexExecutableAvailability.Configured => RequireExecutable(
                executable.ExecutablePath,
                "The configured Codex executable is unavailable."),
            CodexExecutableAvailability.Discovered => RequireExecutable(
                executable.ExecutablePath,
                "The discovered Codex executable is unavailable."),
            CodexExecutableAvailability.AutomaticDiscoveryAbsent when executable.ExecutablePath is null => null,
            CodexExecutableAvailability.AutomaticDiscoveryAbsent => throw new InvalidOperationException(
                "Automatic Codex discovery absence cannot include an executable path."),
            _ => throw new InvalidOperationException("The Codex executable availability is invalid.")
        };
    }

    private static string RequireExecutable(string? executablePath, string message)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            throw new InvalidOperationException(message);
        return Path.GetFullPath(executablePath);
    }

    private static async Task WriteRolloutAsync(
        string staging,
        string sessionId,
        PortableConversation conversation,
        string modelProvider,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            staging,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);

        WriteRecord(stream, json =>
        {
            json.WriteStartObject();
            json.WriteString("timestamp", Timestamp(conversation.CreatedAt));
            json.WriteString("type", "session_meta");
            json.WriteStartObject("payload");
            json.WriteString("session_id", sessionId);
            json.WriteString("id", sessionId);
            json.WriteString("timestamp", Timestamp(conversation.CreatedAt));
            json.WriteString("cwd", conversation.WorkingDirectory);
            json.WriteString("title", conversation.Title);
            json.WriteString("originator", "codex-history-sync");
            json.WriteString("cli_version", "0.7.0");
            json.WriteString("model_provider", modelProvider);
            json.WriteString("source", "cli");
            json.WriteNull("base_instructions");
            json.WriteEndObject();
            json.WriteEndObject();
        });

        foreach (var turn in conversation.Turns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteRecord(stream, json =>
            {
                json.WriteStartObject();
                json.WriteString("timestamp", Timestamp(conversation.LastModifiedAt));
                json.WriteString("type", "response_item");
                json.WriteStartObject("payload");
                json.WriteString("type", "message");
                json.WriteString("role", RoleName(turn.Role));
                json.WriteStartArray("content");
                json.WriteStartObject();
                json.WriteString("type", turn.Role == ConversationRole.User ? "input_text" : "output_text");
                json.WriteString("text", turn.Text);
                json.WriteEndObject();
                json.WriteEndArray();
                json.WriteEndObject();
                json.WriteEndObject();
            });

            if (turn.Role == ConversationRole.User)
            {
                WriteRecord(stream, json =>
                {
                    json.WriteStartObject();
                    json.WriteString("timestamp", Timestamp(conversation.LastModifiedAt));
                    json.WriteString("type", "event_msg");
                    json.WriteStartObject("payload");
                    json.WriteString("type", "user_message");
                    json.WriteNull("client_id");
                    json.WriteString("message", turn.Text);
                    json.WriteNull("images");
                    json.WriteStartArray("local_images");
                    json.WriteEndArray();
                    json.WriteNull("audio");
                    json.WriteStartArray("local_audio");
                    json.WriteEndArray();
                    json.WriteStartArray("text_elements");
                    json.WriteEndArray();
                    json.WriteEndObject();
                    json.WriteEndObject();
                });
            }
            else
            {
                WriteRecord(stream, json =>
                {
                    json.WriteStartObject();
                    json.WriteString("timestamp", Timestamp(conversation.LastModifiedAt));
                    json.WriteString("type", "event_msg");
                    json.WriteStartObject("payload");
                    json.WriteString("type", "agent_message");
                    json.WriteString("message", turn.Text);
                    json.WriteEndObject();
                    json.WriteEndObject();
                });
            }
        }

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
        string sessionId)
    {
        if (actual.SourceAgent != ConversationAgent.Codex ||
            !string.Equals(actual.SourceSessionId, sessionId, StringComparison.Ordinal) ||
            !string.Equals(actual.Title, expected.Title, StringComparison.Ordinal) ||
            !string.Equals(actual.WorkingDirectory, expected.WorkingDirectory, StringComparison.Ordinal) ||
            actual.CreatedAt != expected.CreatedAt ||
            actual.LastModifiedAt != expected.LastModifiedAt ||
            !actual.Turns.SequenceEqual(expected.Turns))
            throw new InvalidDataException("The staged Codex conversation failed validation.");
    }
}

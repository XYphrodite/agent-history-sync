using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Muse;

namespace CodexHistorySync.Core.Conversion;

public sealed class MuseConversationWriter : IConversationWriter
{
    private readonly MusePaths paths;
    private readonly Func<Guid> idGenerator;
    private readonly IConversationReader validator;
    private readonly Func<DateTimeOffset> utcNow;

    public MuseConversationWriter(MusePaths paths, Func<Guid>? idGenerator = null, Func<DateTimeOffset>? utcNow = null)
        : this(paths, idGenerator ?? Guid.CreateVersion7, new MuseConversationReader(), utcNow)
    {
    }

    internal MuseConversationWriter(MusePaths paths, Func<Guid> idGenerator, IConversationReader validator, Func<DateTimeOffset>? utcNow = null)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<ConversationWriteResult> WriteAsync(PortableConversation conversation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (paths.IsReadOnly) throw new IOException("Sessions inside a stopped WSL disk are read-only.");
        conversation = conversation with { LastModifiedAt = utcNow() };
        conversation = conversation with
        {
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(conversation.CreatedAt.ToUnixTimeMilliseconds()),
            LastModifiedAt = DateTimeOffset.FromUnixTimeMilliseconds(conversation.LastModifiedAt.ToUnixTimeMilliseconds())
        };

        var sessionsRoot = paths.Sessions;
        Directory.CreateDirectory(sessionsRoot);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var guid = idGenerator();
            var sessionId = guid.ToString();
            if (ConversationWriterIdentity.IsSourceSessionId(guid, conversation.SourceSessionId))
                continue;

            var directory = Path.Combine(sessionsRoot, sessionId);
            if (Directory.Exists(directory)) continue;

            var sessionFile = Path.Combine(directory, MusePaths.SessionFileName);
            Directory.CreateDirectory(directory);
            try
            {
                await WriteSessionAsync(sessionFile, sessionId, conversation, cancellationToken).ConfigureAwait(false);
                // Validate
                var roundTrip = await validator.ReadAsync(directory, cancellationToken).ConfigureAwait(false);
                ValidateRoundTrip(conversation, roundTrip, sessionId);
                return new ConversationWriteResult(sessionId, directory);
            }
            catch (IOException) when (Directory.Exists(directory))
            {
                try { Directory.Delete(directory, true); } catch { }
                continue;
            }
            catch
            {
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
                throw;
            }
        }
        throw new IOException("Unable to allocate a unique Muse session ID after 10 attempts.");
    }

    private static async Task WriteSessionAsync(string path, string sessionId, PortableConversation conversation, CancellationToken ct)
    {
        var lines = new List<string>();
        // Add minimal header
        lines.Add(JsonSerializer.Serialize(new { sessionId, title = conversation.Title, createdAt = conversation.CreatedAt.ToUnixTimeMilliseconds() }));
        foreach (var turn in conversation.Turns)
        {
            ct.ThrowIfCancellationRequested();
            var role = turn.Role == ConversationRole.User ? "user" : "assistant";
            lines.Add(JsonSerializer.Serialize(new { role, text = turn.Text, sessionId }));
        }
        await File.WriteAllLinesAsync(path, lines, Encoding.UTF8, ct).ConfigureAwait(false);
        File.SetCreationTimeUtc(path, conversation.CreatedAt.UtcDateTime);
        File.SetLastWriteTimeUtc(path, conversation.LastModifiedAt.UtcDateTime);
    }

    private static void ValidateRoundTrip(PortableConversation expected, PortableConversation actual, string sessionId)
    {
        if (actual.SourceAgent != ConversationAgent.Muse || !StringComparer.OrdinalIgnoreCase.Equals(actual.SourceSessionId, sessionId))
            throw new InvalidDataException("Muse round-trip session id mismatch.");
        if (actual.Turns.Count != expected.Turns.Count)
            throw new InvalidDataException("Muse round-trip turn count mismatch.");
        for (var i = 0; i < expected.Turns.Count; i++)
        {
            if (expected.Turns[i].Role != actual.Turns[i].Role || expected.Turns[i].Text != actual.Turns[i].Text)
                throw new InvalidDataException("Muse round-trip turn mismatch.");
        }
    }
}

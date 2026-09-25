using System.Globalization;
using CodexHistorySync.Core.Hermes;
using Microsoft.Data.Sqlite;

namespace CodexHistorySync.Core.Conversion;

/// <summary>
/// Writes a portable conversation into Hermes's <c>state.db</c> as a new CLI session. The row uses
/// Hermes's own tables, including <c>active=1</c> on each message, so a later <c>hermes --resume</c>
/// can load it. Columns a future Hermes adds and this writer does not know stay at their defaults.
/// </summary>
public sealed class HermesConversationWriter : IConversationWriter
{
    private const int MaximumIdAttempts = 8;
    private readonly HermesPaths paths;
    private readonly Func<Guid> idGenerator;
    private readonly Func<DateTimeOffset> utcNow;

    public HermesConversationWriter(HermesPaths paths, Func<Guid>? idGenerator = null, Func<DateTimeOffset>? utcNow = null)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.idGenerator = idGenerator ?? Guid.NewGuid;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<ConversationWriteResult> WriteAsync(PortableConversation conversation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (conversation.Turns.Count == 0)
            throw new ArgumentException("Hermes conversation is invalid.", nameof(conversation));

        var created = utcNow();
        var started = created.ToUnixTimeMilliseconds() / 1000d;
        var title = string.IsNullOrWhiteSpace(conversation.Title) ? null : conversation.Title.Trim();
        var cwd = string.IsNullOrWhiteSpace(conversation.WorkingDirectory) ? null : conversation.WorkingDirectory.Trim();

        for (var attempt = 0; attempt < MaximumIdAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionId = created.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" +
                idGenerator().ToString("N")[..8];
            if (!HermesPaths.IsSessionId(sessionId)) continue;
            if (HermesSessionDatabase.ReadOne(paths, HermesPaths.DefaultProfileName, sessionId) is not null) continue;

            var session = new List<HermesCell>
            {
                new("id", "text", sessionId, null, null),
                new("message_count", "integer", null, conversation.Turns.Count, null),
                new("source", "text", "cli", null, null),
                new("started_at", "real", started.ToString("G17", CultureInfo.InvariantCulture), null, null)
            };
            if (cwd is not null) session.Add(new HermesCell("cwd", "text", cwd, null, null));
            if (title is not null) session.Add(new HermesCell("title", "text", title, null, null));

            var messages = new List<IReadOnlyList<HermesCell>>(conversation.Turns.Count);
            for (var index = 0; index < conversation.Turns.Count; index++)
            {
                var turn = conversation.Turns[index];
                var role = turn.Role == ConversationRole.User ? "user" : "assistant";
                messages.Add(
                [
                    new HermesCell("active", "integer", null, 1, null),
                    new HermesCell("content", "text", turn.Text, null, null),
                    new HermesCell("role", "text", role, null, null),
                    new HermesCell("session_id", "text", sessionId, null, null),
                    new HermesCell("timestamp", "real", (started + index / 1000d).ToString("G17", CultureInfo.InvariantCulture), null, null)
                ]);
            }

            var snapshot = new HermesSnapshot(HermesPaths.DefaultProfileName, sessionId, session, messages, started);
            try
            {
                HermesSessionDatabase.Write(paths, snapshot);
            }
            catch (IOException exception) when (IsUniqueTitle(exception) && attempt + 1 < MaximumIdAttempts && title is not null)
            {
                title += " (copy)";
                continue;
            }

            return Task.FromResult(new ConversationWriteResult(
                sessionId, paths.AnchorPath(HermesPaths.DefaultProfileName, sessionId)));
        }

        throw new IOException("Hermes could not allocate a new session id.");
    }

    private static bool IsUniqueTitle(IOException exception) =>
        exception.InnerException is SqliteException sqlite &&
        sqlite.SqliteErrorCode == 19 &&
        sqlite.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);
}

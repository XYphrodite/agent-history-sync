using System.Globalization;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Hermes;

namespace CodexHistorySync.Core.Conversion;

/// <summary>
/// Reads a Hermes session out of <c>state.db</c>. Tool rows and reasoning stay behind; the portable
/// model is the user and assistant text.
/// </summary>
public sealed class HermesConversationReader : IConversationReader
{
    private const int TitlePreviewLength = 80;

    public Task<PortableConversation> ReadAsync(string nativePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(nativePath))
            throw new ArgumentException("A Hermes session path is required.", nameof(nativePath));
        cancellationToken.ThrowIfCancellationRequested();
        if (!HermesPaths.TryParseAnchor(nativePath, out var home, out var profile, out var sessionId))
            throw InvalidConversation();

        try
        {
            var snapshot = HermesSessionDatabase.ReadOne(new HermesPaths(home), profile, sessionId);
            if (snapshot is null || !string.Equals(snapshot.SessionId, sessionId, StringComparison.Ordinal))
                throw InvalidConversation();

            var turns = ReadTurns(snapshot.Messages);
            if (turns.Count == 0) throw InvalidConversation();

            var created = Unix(Cell(snapshot.Session, "started_at")) ?? DateTimeOffset.UnixEpoch;
            var modified = UnixTime(snapshot.LastActiveUnix) ?? created;
            if (created > modified) created = modified;
            var title = Cell(snapshot.Session, "title")?.Text;
            if (string.IsNullOrWhiteSpace(title))
                title = Preview(turns.FirstOrDefault(turn => turn.Role == ConversationRole.User)?.Text);

            return Task.FromResult(new PortableConversation(
                ConversationAgent.Hermes,
                sessionId,
                string.IsNullOrWhiteSpace(title) ? sessionId : title.Trim(),
                EmptyToNull(Cell(snapshot.Session, "cwd")?.Text),
                created,
                modified,
                turns));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                          InvalidDataException or Microsoft.Data.Sqlite.SqliteException or ArgumentException)
        {
            if (exception is InvalidDataException invalid && invalid.Message == "Hermes conversation is invalid.") throw;
            throw InvalidConversation(exception);
        }
    }

    private static List<PortableTurn> ReadTurns(IReadOnlyList<IReadOnlyList<HermesCell>> messages)
    {
        var turns = new List<PortableTurn>();
        foreach (var message in messages)
        {
            var role = Cell(message, "role")?.Text switch
            {
                "user" => ConversationRole.User,
                "assistant" => ConversationRole.Assistant,
                _ => (ConversationRole?)null
            };
            if (role is null) continue;
            var text = ReadContent(Cell(message, "content")?.Text);
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (role.Value == ConversationRole.User && ConversationTechnicalText.IsWrapper(text)) continue;
            turns.Add(new PortableTurn(role.Value, text));
        }

        return turns;
    }

    private static string? ReadContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var trimmed = content.Trim();
        if (!trimmed.StartsWith('[')) return content;
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return content;
            var builder = new StringBuilder();
            foreach (var part in document.RootElement.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.String) builder.Append(part.GetString());
                else if (part.ValueKind == JsonValueKind.Object &&
                         part.TryGetProperty("text", out var text) &&
                         text.ValueKind == JsonValueKind.String)
                    builder.Append(text.GetString());
            }

            var combined = builder.ToString();
            return string.IsNullOrWhiteSpace(combined) ? null : combined;
        }
        catch (JsonException)
        {
            return content;
        }
    }

    private static HermesCell? Cell(IReadOnlyList<HermesCell> cells, string name) =>
        cells.FirstOrDefault(cell => string.Equals(cell.Name, name, StringComparison.Ordinal));

    private static DateTimeOffset? Unix(HermesCell? cell)
    {
        if (cell?.Type == "real" && cell.Text is not null &&
            double.TryParse(cell.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return UnixTime(seconds);
        if (cell?.Type == "integer" && cell.Integer is not null)
            return UnixTime(cell.Integer.Value);
        return null;
    }

    private static DateTimeOffset? UnixTime(double seconds)
    {
        try
        {
            var millis = (long)Math.Round(seconds * 1000d, MidpointRounding.AwayFromZero);
            return DateTimeOffset.FromUnixTimeMilliseconds(millis);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? Preview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        return trimmed.Length <= TitlePreviewLength ? trimmed : trimmed[..TitlePreviewLength];
    }

    private static InvalidDataException InvalidConversation(Exception? inner = null) =>
        new("Hermes conversation is invalid.", inner);
}

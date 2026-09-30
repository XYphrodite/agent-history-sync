using System.Text.Json;
using CodexHistorySync.Core.Mimo;

namespace CodexHistorySync.Core.Conversion;

public sealed class MimoConversationReader : IConversationReader
{
    private const int TitlePreviewLength = 80;

    public Task<PortableConversation> ReadAsync(string nativePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(nativePath))
            throw new ArgumentException("A MiMo session path is required.", nameof(nativePath));
        cancellationToken.ThrowIfCancellationRequested();
        if (!MimoPaths.TryParseAnchor(nativePath, out var home, out var sessionId))
            throw InvalidConversation();

        try
        {
            var snapshot = MimoSessionDatabase.ReadOne(new MimoPaths(home), sessionId);
            if (snapshot is null || !string.Equals(snapshot.SessionId, sessionId, StringComparison.Ordinal))
                throw InvalidConversation();

            var turns = ReadTurns(snapshot);
            if (turns.Count == 0) throw InvalidConversation();

            var created = ParseTime(snapshot) ?? DateTimeOffset.UnixEpoch;
            var modified = UnixTime(snapshot.LastActiveUnix) ?? created;
            if (created > modified) created = modified;
            var title = Cell(snapshot.Session, "title")?.Text;
            if (string.IsNullOrWhiteSpace(title))
                title = Preview(turns.FirstOrDefault(t => t.Role == ConversationRole.User)?.Text);

            var workingDir = Cell(snapshot.Session, "directory")?.Text;
            return Task.FromResult(new PortableConversation(
                ConversationAgent.Mimo,
                sessionId,
                string.IsNullOrWhiteSpace(title) ? sessionId : title.Trim(),
                string.IsNullOrWhiteSpace(workingDir) ? null : workingDir.Trim(),
                created,
                modified,
                turns));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                   InvalidDataException or Microsoft.Data.Sqlite.SqliteException or ArgumentException)
        {
            if (ex is InvalidDataException inv && inv.Message == "MiMo conversation is invalid.") throw;
            throw InvalidConversation(ex);
        }
    }

    private static List<PortableTurn> ReadTurns(MimoSnapshot snapshot)
    {
        var turns = new List<PortableTurn>();
        for (var i = 0; i < snapshot.Messages.Count; i++)
        {
            var messageCells = snapshot.Messages[i];
            var parts = i < snapshot.Parts.Count ? snapshot.Parts[i] : [];
            var role = TryGetRole(messageCells);
            if (role is null) continue;
            var text = ExtractText(messageCells, parts);
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (role.Value == ConversationRole.User && ConversationTechnicalText.IsWrapper(text)) continue;
            turns.Add(new PortableTurn(role.Value, text));
        }
        return turns;
    }

    private static ConversationRole? TryGetRole(IReadOnlyList<MimoCell> messageCells)
    {
        var dataCell = Cell(messageCells, "data");
        if (dataCell is not null && !string.IsNullOrWhiteSpace(dataCell.Text))
        {
            try
            {
                using var doc = JsonDocument.Parse(dataCell.Text);
                if (doc.RootElement.TryGetProperty("role", out var role) && role.ValueKind == JsonValueKind.String)
                {
                    return role.GetString() switch
                    {
                        "user" => ConversationRole.User,
                        "assistant" => ConversationRole.Assistant,
                        _ => null
                    };
                }
            }
            catch { }
        }
        // Fallback to direct role column if present
        var roleCell = Cell(messageCells, "role");
        if (roleCell?.Text is "user") return ConversationRole.User;
        if (roleCell?.Text is "assistant") return ConversationRole.Assistant;
        return null;
    }

    private static string? ExtractText(IReadOnlyList<MimoCell> messageCells, IReadOnlyList<MimoPart> parts)
    {
        var texts = new List<string>();
        // First try parts
        foreach (var part in parts)
        {
            var dataCell = Cell(part.Cells, "data");
            if (dataCell is null || string.IsNullOrWhiteSpace(dataCell.Text)) continue;
            try
            {
                using var doc = JsonDocument.Parse(dataCell.Text);
                if (doc.RootElement.TryGetProperty("type", out var type) && type.GetString() == "text" &&
                    doc.RootElement.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
                {
                    var txt = textProp.GetString();
                    if (!string.IsNullOrWhiteSpace(txt)) texts.Add(txt);
                }
            }
            catch { }
        }
        if (texts.Count > 0) return string.Join("\n", texts);
        // Fallback: message data may contain text directly
        var msgData = Cell(messageCells, "data");
        if (msgData?.Text is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(msgData.Text);
                // User message may have summary or other, but try to find text in parts nested
                // Also some messages have data.text
                if (doc.RootElement.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    return t.GetString();
            }
            catch { }
        }
        // Last resort: try content cell
        var content = Cell(messageCells, "content")?.Text;
        if (!string.IsNullOrWhiteSpace(content)) return content;
        return null;
    }

    private static DateTimeOffset? ParseTime(MimoSnapshot snapshot)
    {
        var createdStr = Cell(snapshot.Session, "time_created")?.Text ?? Cell(snapshot.Session, "timeCreated")?.Text;
        if (createdStr is not null && double.TryParse(createdStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            return UnixTime(v);
        var integer = Cell(snapshot.Session, "time_created")?.Integer;
        if (integer is not null) return DateTimeOffset.FromUnixTimeMilliseconds(integer.Value);
        return null;
    }

    private static MimoCell? Cell(IReadOnlyList<MimoCell> cells, string name) =>
        cells.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    private static DateTimeOffset? UnixTime(double seconds)
    {
        try
        {
            // Handle seconds vs milliseconds
            var secs = seconds;
            if (secs > 1e12) secs /= 1000.0;
            var millis = (long)Math.Round(secs * 1000d, MidpointRounding.AwayFromZero);
            return DateTimeOffset.FromUnixTimeMilliseconds(millis);
        }
        catch { return null; }
    }

    private static string Preview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw InvalidConversation();
        var trimmed = text.Trim();
        return trimmed.Length <= TitlePreviewLength ? trimmed : trimmed[..TitlePreviewLength];
    }

    private static InvalidDataException InvalidConversation(Exception? inner = null) => new("MiMo conversation is invalid.", inner);
}

using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Muse;

namespace CodexHistorySync.Core.Conversion;

public sealed class MuseConversationReader : IConversationReader
{
    public async Task<PortableConversation> ReadAsync(string nativePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(nativePath))
            throw new ArgumentException("A Muse session directory is required.", nameof(nativePath));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetFullPath(nativePath);
            var sessionFile = Path.Combine(directory, MusePaths.SessionFileName);
            if (!Directory.Exists(directory) || !File.Exists(sessionFile))
                throw InvalidConversation();

            var sessionId = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!Guid.TryParse(sessionId, out _))
                throw InvalidConversation();

            var turns = await Task.Run(() => ReadTurns(sessionFile), cancellationToken).ConfigureAwait(false);
            if (turns.Count == 0) throw InvalidConversation();

            var title = turns.FirstOrDefault(t => t.Role == ConversationRole.User)?.Text ?? sessionId;
            if (title.Length > 80) title = title[..80];

            var createdAt = File.GetCreationTimeUtc(sessionFile);
            var lastWrite = File.GetLastWriteTimeUtc(sessionFile);
            return new PortableConversation(
                ConversationAgent.Muse,
                sessionId,
                title,
                null,
                new DateTimeOffset(createdAt, TimeSpan.Zero),
                new DateTimeOffset(lastWrite, TimeSpan.Zero),
                turns);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            throw InvalidConversation();
        }
    }

    private static List<PortableTurn> ReadTurns(string sessionFile)
    {
        var turns = new List<PortableTurn>();
        foreach (var line in File.ReadLines(sessionFile, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                // Try to find user/assistant messages in various payload structures
                if (TryExtractTurn(root, out var turn) && turn is not null)
                {
                    if (turn.Role == ConversationRole.User && ConversationTechnicalText.IsWrapper(turn.Text)) continue;
                    turns.Add(turn);
                }
            }
            catch { }
        }
        return turns;
    }

    private static bool TryExtractTurn(JsonElement root, out PortableTurn? turn)
    {
        turn = null;
        try
        {
            // Check for direct payload with text
            if (root.TryGetProperty("payload", out var payload))
            {
                if (payload.TryGetProperty("record", out var record))
                {
                    if (record.TryGetProperty("prompt", out var prompt) && prompt.ValueKind == JsonValueKind.String)
                    {
                        var text = prompt.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            turn = new PortableTurn(ConversationRole.User, text!);
                            return true;
                        }
                    }
                    if (record.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
                    {
                        var text = textProp.GetString();
                        var role = record.TryGetProperty("role", out var roleProp) && roleProp.GetString() == "assistant" ? ConversationRole.Assistant : ConversationRole.User;
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            turn = new PortableTurn(role, text!);
                            return true;
                        }
                    }
                }
            }
            // Fallback: look for any text field
            if (root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            {
                var text = t.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    turn = new PortableTurn(ConversationRole.User, text!);
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static InvalidDataException InvalidConversation() => new("Muse conversation is invalid.");
}

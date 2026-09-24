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

            var turns = await Task.Run(() => ReadTurns(sessionFile, cancellationToken), cancellationToken).ConfigureAwait(false);
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

    private static List<PortableTurn> ReadTurns(string sessionFile, CancellationToken ct)
    {
        var turns = new List<PortableTurn>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(sessionFile, Encoding.UTF8))
        {
            ct.ThrowIfCancellationRequested();
            using var document = ParseRecord(line);
            if (document is null) continue;
            foreach (var root in Records(document.RootElement, 0))
            {
                ct.ThrowIfCancellationRequested();
                if (Text(root, "id") is { } id && !seen.Add(id)) continue;
                var turn = ExtractTurn(root);
                if (turn is null || turn.Role == ConversationRole.User && ConversationTechnicalText.IsWrapper(turn.Text)) continue;
                turns.Add(turn);
            }
        }
        return turns;
    }

    // Current Muse journals may retain events inside a transaction frame as serialized records.
    private static IEnumerable<JsonElement> Records(JsonElement root, int depth)
    {
        if (root.ValueKind != JsonValueKind.Object || depth > 8) yield break;
        if (!root.TryGetProperty("retained_frame", out _)) { yield return root; yield break; }
        if (!root.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array) yield break;
        foreach (var child in children.EnumerateArray())
        {
            using var document = ParseRecord(Text(child, "record_json"));
            if (document is null) continue;
            foreach (var record in Records(document.RootElement, depth + 1)) yield return record;
        }
    }

    internal static PortableTurn? ExtractTurn(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
        {
            if (Text(root, "payload_type") == "runtime.session" && Text(payload, "kind") == "run" &&
                payload.TryGetProperty("event", out var runEvent))
            {
                // Use committed visible messages only: deltas, reasoning and task/tool diagnostics are not dialogue.
                return Text(runEvent, "kind") switch
                {
                    "started" => Turn(ConversationRole.User, Text(runEvent, "prompt")),
                    "assistant_message_committed" => Turn(ConversationRole.Assistant, Text(runEvent, "text")),
                    _ => null
                };
            }
            if (Text(root, "payload_type") is not null) return null;
            if (payload.TryGetProperty("record", out var record))
            {
                if (Text(record, "prompt") is { } prompt) return Turn(ConversationRole.User, prompt);
                return RoleTurn(Text(record, "role"), Text(record, "text"));
            }
        }
        return RoleTurn(Text(root, "role"), Text(root, "text"));
    }

    private static PortableTurn? RoleTurn(string? role, string? text) => role switch
    {
        "assistant" => Turn(ConversationRole.Assistant, text),
        "user" or null => Turn(ConversationRole.User, text),
        _ => null
    };

    private static PortableTurn? Turn(ConversationRole role, string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : new PortableTurn(role, text);

    private static string? Text(JsonElement node, string key) => node.ValueKind == JsonValueKind.Object &&
        node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static JsonDocument? ParseRecord(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonDocument.Parse(text); }
        catch (JsonException) { return null; }
    }

    private static InvalidDataException InvalidConversation() => new("Muse conversation is invalid.");
}

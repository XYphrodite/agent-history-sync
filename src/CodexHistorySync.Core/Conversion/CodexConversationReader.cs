using System.Text;
using System.Text.Json;

namespace CodexHistorySync.Core.Conversion;

public sealed class CodexConversationReader : IConversationReader
{
    private const int TitlePreviewLength = 80;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public async Task<PortableConversation> ReadAsync(string nativePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(nativePath) ||
            !string.Equals(Path.GetExtension(nativePath), ".jsonl", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Codex conversations must be JSONL files.", nameof(nativePath));

        try
        {
            var metadata = default(SessionMetadata);
            var hasMetadata = false;
            var turns = new List<PortableTurn>();
            var timestamps = new List<DateTimeOffset>();

            await using var stream = new FileStream(nativePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096,
                leaveOpen: false);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw InvalidConversation();

                AddTimestamp(root, timestamps);
                if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) continue;
                if (string.Equals(type.GetString(), "session_meta", StringComparison.Ordinal))
                {
                    if (hasMetadata || !TryReadMetadata(root, out metadata)) throw InvalidConversation();
                    hasMetadata = true;
                    if (metadata.Timestamp is { } timestamp) timestamps.Add(timestamp);
                    continue;
                }

                if (string.Equals(type.GetString(), "response_item", StringComparison.Ordinal) &&
                    TryReadTurn(root, out var turn, out var turnTimestamp))
                {
                    if (turnTimestamp is { } value) timestamps.Add(value);
                    if (turn is not null) turns.Add(turn);
                }
            }

            if (!hasMetadata || turns.Count == 0) throw InvalidConversation();

            var fallback = new DateTimeOffset(File.GetLastWriteTimeUtc(nativePath), TimeSpan.Zero);
            var createdAt = timestamps.Count == 0 ? fallback : timestamps.Min();
            var lastModifiedAt = timestamps.Count == 0 ? fallback : timestamps.Max();
            var title = string.IsNullOrWhiteSpace(metadata.Title)
                ? Preview(turns.FirstOrDefault(turn => turn.Role == ConversationRole.User)?.Text)
                : metadata.Title!;

            return new PortableConversation(
                ConversationAgent.Codex,
                metadata.Id,
                title,
                metadata.WorkingDirectory,
                createdAt,
                lastModifiedAt,
                turns);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or JsonException)
        {
            throw InvalidConversation();
        }
    }

    private static bool TryReadMetadata(JsonElement root, out SessionMetadata metadata)
    {
        metadata = default;
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object ||
            !TryGetNonEmptyString(payload, "id", out var id) || !IsSafeSessionId(id))
            return false;

        metadata = new SessionMetadata(
            id,
            GetString(payload, "title") ?? GetString(payload, "thread_name"),
            GetString(payload, "cwd") ?? GetString(payload, "working_directory"),
            ReadTimestamp(payload));
        return true;
    }

    private static bool TryReadTurn(JsonElement root, out PortableTurn? turn, out DateTimeOffset? timestamp)
    {
        turn = null;
        timestamp = null;
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object ||
            !HasString(payload, "type", "message") || !payload.TryGetProperty("role", out var role) ||
            role.ValueKind != JsonValueKind.String)
            return false;

        var conversationRole = role.GetString() switch
        {
            "user" => ConversationRole.User,
            "assistant" => ConversationRole.Assistant,
            _ => (ConversationRole?)null
        };
        if (conversationRole is null) return false;

        timestamp = ReadTimestamp(payload) ?? ReadTimestamp(root);
        var text = ReadMessageText(payload);
        if (!string.IsNullOrWhiteSpace(text)) turn = new PortableTurn(conversationRole.Value, text);
        return true;
    }

    private static string? ReadMessageText(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;

        var blocks = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object ||
                !block.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                continue;
            blocks.Add(text.GetString()!);
        }
        return blocks.Count == 0 ? null : string.Concat(blocks);
    }

    private static void AddTimestamp(JsonElement element, ICollection<DateTimeOffset> timestamps)
    {
        if (ReadTimestamp(element) is { } timestamp) timestamps.Add(timestamp);
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element)
    {
        foreach (var name in new[] { "timestamp", "created_at", "createdAt", "updated_at", "updatedAt" })
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), out var timestamp))
                return timestamp;
        }
        return null;
    }

    private static bool TryGetNonEmptyString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var candidate) || candidate.ValueKind != JsonValueKind.String) return false;
        value = candidate.GetString()!;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool HasString(JsonElement element, string name, string expected) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString(), expected, StringComparison.Ordinal);

    private static bool IsSafeSessionId(string value) =>
        !Path.IsPathRooted(value) && !value.Contains('/') && !value.Contains('\\') && value is not "." and not "..";

    private static string Preview(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) throw InvalidConversation();
        return trimmed.Length <= TitlePreviewLength ? trimmed : trimmed[..TitlePreviewLength];
    }

    private static InvalidDataException InvalidConversation() => new("Codex conversation is invalid.");

    private readonly record struct SessionMetadata(string Id, string? Title, string? WorkingDirectory, DateTimeOffset? Timestamp);
}

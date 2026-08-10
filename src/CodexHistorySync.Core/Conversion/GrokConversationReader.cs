using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Grok;

namespace CodexHistorySync.Core.Conversion;

public sealed class GrokConversationReader : IConversationReader
{
    private const int TitlePreviewLength = 80;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public async Task<PortableConversation> ReadAsync(string nativePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(nativePath)) throw new ArgumentException("A Grok session directory is required.", nameof(nativePath));

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetFullPath(nativePath);
            var chatPath = Path.Combine(directory, "chat_history.jsonl");
            var summaryPath = Path.Combine(directory, "summary.json");
            if (!Directory.Exists(directory) || !File.Exists(chatPath) || !File.Exists(summaryPath))
                throw InvalidConversation();

            var package = GrokSessionPackage.Parse(GrokSessionPackage.BuildFromDirectory(directory));
            var metadata = await ReadMetadataAsync(summaryPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(metadata.Id, package.SessionId, StringComparison.OrdinalIgnoreCase))
                throw InvalidConversation();

            var turns = await ReadTurnsAsync(package.ChatHistory, cancellationToken).ConfigureAwait(false);
            if (turns.Count == 0) throw InvalidConversation();

            var fallbackCreated = new DateTimeOffset(File.GetCreationTimeUtc(chatPath), TimeSpan.Zero);
            var fallbackModified = new[]
            {
                new DateTimeOffset(File.GetLastWriteTimeUtc(chatPath), TimeSpan.Zero),
                new DateTimeOffset(File.GetLastWriteTimeUtc(summaryPath), TimeSpan.Zero)
            }.Max();
            var title = string.IsNullOrWhiteSpace(metadata.Title)
                ? Preview(turns.FirstOrDefault(turn => turn.Role == ConversationRole.User)?.Text)
                : metadata.Title!;

            return new PortableConversation(
                ConversationAgent.Grok,
                package.SessionId,
                title,
                package.Cwd,
                metadata.CreatedAt ?? fallbackCreated,
                metadata.LastModifiedAt ?? metadata.CreatedAt ?? fallbackModified,
                turns);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or JsonException or ArgumentException)
        {
            throw InvalidConversation();
        }
    }

    private static async Task<SessionMetadata> ReadMetadataAsync(string summaryPath, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(summaryPath, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(Utf8.GetString(bytes));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("info", out var info) ||
            info.ValueKind != JsonValueKind.Object || !TryGetNonEmptyString(info, "id", out var id))
            throw InvalidConversation();

        return new SessionMetadata(
            id,
            GetString(info, "title") ?? GetString(root, "title"),
            ReadTimestamp(info, "created_at", "createdAt", "timestamp") ?? ReadTimestamp(root, "created_at", "createdAt"),
            ReadTimestamp(info, "updated_at", "updatedAt", "last_modified_at", "lastModifiedAt") ??
            ReadTimestamp(root, "updated_at", "updatedAt", "last_modified_at", "lastModifiedAt"));
    }

    private static async Task<List<PortableTurn>> ReadTurnsAsync(byte[] chatHistory, CancellationToken cancellationToken)
    {
        var turns = new List<PortableTurn>();
        using var stream = new MemoryStream(chatHistory, writable: false);
        using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: false);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw InvalidConversation();

            var roleText = GetString(root, "role") ?? GetString(root, "type");
            var role = roleText switch
            {
                "user" => ConversationRole.User,
                "assistant" => ConversationRole.Assistant,
                _ => (ConversationRole?)null
            };
            if (role is null) continue;

            var text = ReadContent(root, role.Value);
            if (!string.IsNullOrWhiteSpace(text)) turns.Add(new PortableTurn(role.Value, text));
        }
        return turns;
    }

    private static string? ReadContent(JsonElement root, ConversationRole role)
    {
        if (!root.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;

        var blocks = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object &&
                block.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                IsTextBlock(type.GetString(), role) &&
                block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                blocks.Add(text.GetString()!);
        }
        return blocks.Count == 0 ? null : string.Concat(blocks);
    }

    private static bool IsTextBlock(string? type, ConversationRole role) =>
        role == ConversationRole.User
            ? string.Equals(type, "input_text", StringComparison.Ordinal)
            : string.Equals(type, "output_text", StringComparison.Ordinal);

    private static DateTimeOffset? ReadTimestamp(JsonElement element, params string[] names)
    {
        foreach (var name in names)
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

    private static string Preview(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) throw InvalidConversation();
        return trimmed.Length <= TitlePreviewLength ? trimmed : trimmed[..TitlePreviewLength];
    }

    private static InvalidDataException InvalidConversation() => new("Grok conversation is invalid.");

    private readonly record struct SessionMetadata(string Id, string? Title, DateTimeOffset? CreatedAt, DateTimeOffset? LastModifiedAt);
}

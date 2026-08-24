using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Claude;

namespace CodexHistorySync.Core.Conversion;

public sealed class ClaudeConversationReader : IConversationReader
{
    private const int TitlePreviewLength = 80;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public async Task<PortableConversation> ReadAsync(string nativePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(nativePath)) throw new ArgumentException("A Claude transcript path is required.", nameof(nativePath));

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transcript = Path.GetFullPath(nativePath);
            if (!File.Exists(transcript)) throw InvalidConversation();

            // Reuses the sync package to get the session id and cwd under one set of rules:
            // it rejects a transcript whose records disagree with the file name or carry no cwd.
            var package = ClaudeSessionPackage.Parse(ClaudeSessionPackage.BuildFromFile(transcript));
            var content = await ReadContentAsync(transcript, cancellationToken).ConfigureAwait(false);
            if (content.Turns.Count == 0) throw InvalidConversation();

            var fallbackCreated = new DateTimeOffset(File.GetCreationTimeUtc(transcript), TimeSpan.Zero);
            var fallbackModified = new DateTimeOffset(File.GetLastWriteTimeUtc(transcript), TimeSpan.Zero);
            var title = content.Title is { Length: > 0 } official
                ? official
                : Preview(content.Turns.FirstOrDefault(turn => turn.Role == ConversationRole.User)?.Text);

            return new PortableConversation(
                ConversationAgent.Claude,
                package.SessionId,
                title,
                package.Cwd,
                content.CreatedAt ?? fallbackCreated,
                content.LastModifiedAt ?? content.CreatedAt ?? fallbackModified,
                content.Turns);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException
                                              or JsonException or ArgumentException)
        {
            throw InvalidConversation();
        }
    }

    private static async Task<TranscriptContent> ReadContentAsync(string transcript, CancellationToken cancellationToken)
    {
        var turns = new List<PortableTurn>();
        var timestamps = new List<DateTimeOffset>();
        string? aiTitle = null;
        string? summaryTitle = null;

        await using var stream = new FileStream(
            transcript,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: false);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) continue;

            switch (GetString(root, "type"))
            {
                // Claude rewrites the title as the conversation develops, so the last one wins (design D7).
                case "ai-title" when GetNonEmpty(root, "aiTitle") is { } value:
                    aiTitle = value;
                    continue;
                case "summary" when GetNonEmpty(root, "summary") is { } value:
                    summaryTitle ??= value;
                    continue;
                case "user" or "assistant":
                    break;
                // attachment, last-prompt, queue-operation, atis-latch and file-history-snapshot are
                // bookkeeping: they carry no conversation text.
                default:
                    continue;
            }

            // Sidechain records belong to a subagent, not to this conversation.
            if (root.TryGetProperty("isSidechain", out var sidechain) && sidechain.ValueKind == JsonValueKind.True) continue;

            if (ReadTimestamp(root) is { } timestamp) timestamps.Add(timestamp);

            var role = GetString(root, "type") == "user" ? ConversationRole.User : ConversationRole.Assistant;
            var text = ReadMessageText(root);
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (role == ConversationRole.User && ConversationTechnicalText.IsWrapper(text)) continue;
            turns.Add(new PortableTurn(role, text));
        }

        return new TranscriptContent(
            turns,
            aiTitle ?? summaryTitle,
            timestamps.Count == 0 ? null : timestamps.Min(),
            timestamps.Count == 0 ? null : timestamps.Max());
    }

    /// <summary>
    /// Text blocks only. Thinking, tool calls and tool results are deliberately dropped: the portable
    /// model carries plain turns, and a cross-agent copy has nowhere to put them.
    /// </summary>
    private static string? ReadMessageText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return null;
        if (!message.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;

        var blocks = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object ||
                !block.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                !string.Equals(type.GetString(), "text", StringComparison.Ordinal) ||
                !block.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                continue;
            blocks.Add(text.GetString()!);
        }
        return blocks.Count == 0 ? null : string.Concat(blocks);
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element) =>
        element.TryGetProperty("timestamp", out var value) && value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), out var timestamp)
            ? timestamp
            : null;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? GetNonEmpty(JsonElement element, string name)
    {
        var value = GetString(element, name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string Preview(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) throw InvalidConversation();
        return trimmed.Length <= TitlePreviewLength ? trimmed : trimmed[..TitlePreviewLength];
    }

    private static InvalidDataException InvalidConversation() => new("Claude conversation is invalid.");

    private sealed record TranscriptContent(
        IReadOnlyList<PortableTurn> Turns,
        string? Title,
        DateTimeOffset? CreatedAt,
        DateTimeOffset? LastModifiedAt);
}

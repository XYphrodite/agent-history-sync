using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexHistorySync.Core.Continue;

namespace CodexHistorySync.Core.Conversion;

/// <summary>
/// Reads a Continue session into the portable model.
///
/// Two things make this different from the other readers. The session file carries no timestamps
/// at all — Continue keeps the creation time in the shared index — so the index is consulted for
/// it and the file's own write time stands in when the index does not list the session. And the
/// two roles are shaped differently: a user message holds an array of content parts, an assistant
/// message a plain string.
/// </summary>
public sealed class ContinueConversationReader : IConversationReader
{
    private const int TitlePreviewLength = 80;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public async Task<PortableConversation> ReadAsync(string nativePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(nativePath))
            throw new ArgumentException("A Continue session path is required.", nameof(nativePath));

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(nativePath);
            if (!File.Exists(path) || ContinuePaths.IsIndexFile(path)) throw InvalidConversation();

            var sessionId = Path.GetFileNameWithoutExtension(path);
            var text = await File.ReadAllTextAsync(path, Utf8, cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(text) is not JsonObject session) throw InvalidConversation();
            if (!StringComparer.OrdinalIgnoreCase.Equals(ReadString(session, "sessionId"), sessionId))
                throw InvalidConversation();

            var turns = ReadTurns(session);
            if (turns.Count == 0) throw InvalidConversation();

            var title = ReadString(session, "title") is { Length: > 0 } declared
                ? declared
                : Preview(turns.FirstOrDefault(turn => turn.Role == ConversationRole.User)?.Text);
            var lastModified = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            var created = ReadCreatedAt(path, sessionId)
                          ?? new DateTimeOffset(File.GetCreationTimeUtc(path), TimeSpan.Zero);
            if (created > lastModified) created = lastModified;

            return new PortableConversation(
                ConversationAgent.Continue,
                sessionId,
                title,
                ToLocalDirectory(ReadString(session, "workspaceDirectory")),
                created,
                lastModified,
                turns);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                                             or DecoderFallbackException or ArgumentException)
        {
            throw InvalidConversation(exception);
        }
    }

    /// <summary>
    /// Turns the index entry's creation time back into a date. Best effort on purpose: a session
    /// the index has lost still reads, it just falls back to the file's own creation time.
    /// </summary>
    private static DateTimeOffset? ReadCreatedAt(string sessionPath, string sessionId)
    {
        try
        {
            var indexPath = Path.Combine(Path.GetDirectoryName(sessionPath)!, ContinuePaths.IndexFileName);
            if (!File.Exists(indexPath)) return null;
            var entry = ContinueSessionIndex.Find(
                ContinueSessionIndex.Parse(File.ReadAllText(indexPath, Utf8)), sessionId);
            if (entry is null) return null;
            var created = entry["dateCreated"];
            var milliseconds = created is JsonValue value && value.TryGetValue<string>(out var text)
                ? long.TryParse(text, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : (long?)null
                : created is JsonValue number && number.TryGetValue<long>(out var direct) ? direct : null;
            return milliseconds is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(milliseconds.Value);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                                             or InvalidDataException or DecoderFallbackException or ArgumentException)
        {
            return null;
        }
    }

    private static List<PortableTurn> ReadTurns(JsonObject session)
    {
        var turns = new List<PortableTurn>();
        if (session["history"] is not JsonArray history) return turns;

        foreach (var element in history)
        {
            if (element is not JsonObject entry || entry["message"] is not JsonObject message) continue;
            var role = ReadString(message, "role") switch
            {
                "user" => ConversationRole.User,
                "assistant" => ConversationRole.Assistant,
                // "thinking" and anything else Continue adds later: the portable model is the
                // conversation, not the model's working notes.
                _ => (ConversationRole?)null
            };
            if (role is null) continue;

            var text = ReadContent(message["content"]);
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (role.Value == ConversationRole.User && ConversationTechnicalText.IsWrapper(text)) continue;
            turns.Add(new PortableTurn(role.Value, text));
        }

        return turns;
    }

    /// <summary>A user message carries content parts; an assistant message carries a string.</summary>
    private static string? ReadContent(JsonNode? content)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        if (content is not JsonArray parts) return null;

        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (part is not JsonObject item) continue;
            if (!string.Equals(ReadString(item, "type"), "text", StringComparison.Ordinal)) continue;
            if (ReadString(item, "text") is { Length: > 0 } partText) builder.Append(partText);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>
    /// Continue records the workspace as a file URI. Handing the portable model a real path is
    /// what lets a copy into Claude land in the project directory the conversation came from.
    /// </summary>
    private static string? ToLocalDirectory(string? workspaceDirectory)
    {
        if (string.IsNullOrWhiteSpace(workspaceDirectory)) return null;
        if (!Uri.TryCreate(workspaceDirectory, UriKind.Absolute, out var uri) || !uri.IsFile) return null;
        try
        {
            var local = uri.LocalPath;
            if (string.IsNullOrWhiteSpace(local)) return null;
            // Continue percent-encodes the drive colon, so .NET does not recognise the authority
            // as a drive and leaves the leading slash on: "/c:/Repos/Reborn". Passing that to
            // GetFullPath resolves it against the current drive and produces "C:\c:\Repos\Reborn",
            // which is where a copy would then be written.
            if (local.Length >= 3 && local[0] is '/' or '\\' && char.IsAsciiLetter(local[1]) && local[2] == ':')
                local = local[1..];
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(local));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonObject document, string property) =>
        document[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Preview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(untitled)";
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= TitlePreviewLength ? single : single[..TitlePreviewLength];
    }

    private static InvalidDataException InvalidConversation(Exception? inner = null) =>
        new("The Continue session could not be read as a conversation.", inner);
}

using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Kimi;

namespace CodexHistorySync.Core.Conversion;

public sealed class KimiConversationReader : IConversationReader
{
    private const int TitlePreviewLength = 80;

    public async Task<PortableConversation> ReadAsync(string nativePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(nativePath))
            throw new ArgumentException("A Kimi session directory is required.", nameof(nativePath));

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetFullPath(nativePath);
            var statePath = Path.Combine(directory, KimiSessionPackage.StateFileName);
            var wirePath = Path.Combine(directory, "agents", "main", KimiSessionPackage.WireFileName);
            if (!Directory.Exists(directory) || !File.Exists(statePath) || !File.Exists(wirePath))
                throw InvalidConversation();

            var metadata = await ReadMetadataAsync(statePath, cancellationToken).ConfigureAwait(false);
            if (!IsSessionId(metadata.Id, out var sessionId))
                throw InvalidConversation();

            var turns = await Task.Run(() => ReadTurns(wirePath), cancellationToken).ConfigureAwait(false);
            if (turns.Count == 0) throw InvalidConversation();

            var fallbackModified = new DateTimeOffset(File.GetLastWriteTimeUtc(statePath), TimeSpan.Zero);
            var title = string.IsNullOrWhiteSpace(metadata.Title)
                ? Preview(turns.FirstOrDefault(turn => turn.Role == ConversationRole.User)?.Text)
                : metadata.Title!;

            return new PortableConversation(
                ConversationAgent.Kimi,
                sessionId,
                title,
                metadata.WorkingDirectory,
                metadata.CreatedAt ?? new DateTimeOffset(File.GetCreationTimeUtc(statePath), TimeSpan.Zero),
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

    private static List<PortableTurn> ReadTurns(string wirePath)
    {
        var turns = new List<PortableTurn>();
        foreach (var turn in KimiWireConversation.ReadTurns(wirePath))
        {
            // Same rule as the Grok and Codex readers: wrapper records around the real user text
            // made round-trip validation fail for every session that contained one.
            if (turn.Role == ConversationRole.User && ConversationTechnicalText.IsWrapper(turn.Text)) continue;
            turns.Add(turn);
        }
        return turns;
    }

    private static async Task<SessionMetadata> ReadMetadataAsync(string statePath, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(statePath, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw InvalidConversation();

        return new SessionMetadata(
            GetString(root, "id") ?? string.Empty,
            GetString(root, "title"),
            GetString(root, "cwd"),
            ReadEpochMilliseconds(root, "createdAt"),
            ReadEpochMilliseconds(root, "updatedAt"));
    }

    private static bool IsSessionId(string declared, out string sessionId)
    {
        sessionId = string.Empty;
        if (string.IsNullOrWhiteSpace(declared)) return false;
        var value = declared.StartsWith(KimiPaths.SessionIdPrefix, StringComparison.Ordinal)
            ? declared[KimiPaths.SessionIdPrefix.Length..]
            : declared;
        try
        {
            _ = KimiSessionPackage.ToLogicalId(value);
        }
        catch (ArgumentException)
        {
            return false;
        }
        sessionId = value;
        return true;
    }

    private static DateTimeOffset? ReadEpochMilliseconds(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var epochMs)
            ? DateTimeOffset.FromUnixTimeMilliseconds(epochMs)
            : null;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Preview(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) throw InvalidConversation();
        return trimmed.Length <= TitlePreviewLength ? trimmed : trimmed[..TitlePreviewLength];
    }

    private static InvalidDataException InvalidConversation() => new("Kimi conversation is invalid.");

    private sealed record SessionMetadata(
        string Id,
        string? Title,
        string? WorkingDirectory,
        DateTimeOffset? CreatedAt,
        DateTimeOffset? LastModifiedAt);
}

using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Core.Viewing;

/// <summary>Reads native Codex traces; other agents reuse their existing conversation readers.</summary>
public sealed class SessionTraceReader(ISessionContentReader? conversationReader = null) : ISessionTraceReader
{
    private readonly ISessionContentReader conversations = conversationReader ?? new SessionContentReader();

    public async Task<SessionTrace> ReadAsync(ManagedSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        if (!session.CanRead) throw new InvalidDataException("The session is not readable.");
        if (session.Agent != ManagedAgent.Codex)
        {
            var conversation = await conversations.ReadAsync(session, cancellationToken).ConfigureAwait(false);
            return new SessionTrace(session, conversation.Turns.Select((turn, index) => new TraceEntry(index,
                turn.Role == ConversationRole.User ? TraceEntryKind.User : TraceEntryKind.Assistant,
                turn.Role.ToString(), turn.Text)).ToArray(), []);
        }

        var entries = new List<TraceEntry>();
        var warnings = new List<string>();
        var calls = new Dictionary<string, string>(StringComparer.Ordinal);
        var identityVerified = false;
        var malformed = 0;
        var replacementCharacters = false;
        await using var file = new FileStream(session.NativePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        // Fix the byte boundary now: a live writer must not turn reading into an unbounded tail.
        using var snapshot = new SnapshotStream(file);
        // A live write may end inside a UTF-8 character. Decode that tail with replacement
        // so complete earlier records remain readable; the unfinished JSON record is skipped.
        using var reader = new StreamReader(snapshot, new UTF8Encoding(false, false), true, 65536);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            replacementCharacters |= line.Contains('\uFFFD');
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { malformed++; continue; }
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("payload", out var payload) ||
                    payload.ValueKind != JsonValueKind.Object) continue;
                var type = Text(root, "type");
                if (type == "session_meta")
                {
                    if (!string.Equals(Text(payload, "id"), session.SessionId, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The session identity changed. Refresh the session list.");
                    identityVerified = true;
                }
                if (type != "response_item") continue;
                var timestamp = DateTimeOffset.TryParse(Text(root, "timestamp"), out var time) ? time : (DateTimeOffset?)null;
                var itemType = Text(payload, "type");
                var callId = Text(payload, "call_id");
                switch (itemType)
                {
                    case "message":
                        var role = Text(payload, "role");
                        if (role is not ("user" or "assistant")) break;
                        var content = MessageText(payload);
                        if (string.IsNullOrWhiteSpace(content)) break;
                        var notification = content.TrimStart().StartsWith("<subagent_notification>", StringComparison.Ordinal);
                        entries.Add(new TraceEntry(entries.Count,
                            notification ? TraceEntryKind.Notification : role == "user" ? TraceEntryKind.User : TraceEntryKind.Assistant,
                            notification ? "Subagent notification" : role == "user" ? "User" : "Assistant", content, timestamp));
                        break;
                    case "function_call":
                    case "custom_tool_call":
                        var name = Text(payload, "name") ?? "Tool";
                        if (callId is not null) calls[callId] = name;
                        entries.Add(new TraceEntry(entries.Count, TraceEntryKind.ToolCall, name,
                            Value(payload, itemType == "function_call" ? "arguments" : "input"), timestamp, callId));
                        break;
                    case "function_call_output":
                    case "custom_tool_call_output":
                        var tool = callId is not null && calls.TryGetValue(callId, out var known) ? known : "Tool";
                        entries.Add(new TraceEntry(entries.Count, TraceEntryKind.ToolResult, tool + " · result",
                            Value(payload, "output"), timestamp, callId));
                        break;
                    case "web_search_call":
                    case "local_shell_call":
                    case "image_generation_call":
                        entries.Add(new TraceEntry(entries.Count, TraceEntryKind.ToolCall, itemType,
                            Value(payload, "action"), timestamp, callId));
                        break;
                }
            }
        }
        if (!identityVerified) throw new InvalidDataException("The session metadata is missing.");
        if (malformed > 0) warnings.Add($"Skipped {malformed} incomplete or malformed JSONL record(s). Refresh if this session is still running.");
        if (replacementCharacters) warnings.Add("The file contains Unicode replacement characters; text may have been incomplete or damaged.");
        return new SessionTrace(session, entries, warnings);
    }

    private static string MessageText(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out var content)) return string.Empty;
        if (content.ValueKind == JsonValueKind.String) return content.GetString()!;
        if (content.ValueKind != JsonValueKind.Array) return string.Empty;
        return string.Join("\n\n", content.EnumerateArray()
            .Where(block => block.ValueKind == JsonValueKind.Object && Text(block, "type") is "input_text" or "output_text" or "text")
            .Select(block => Text(block, "text"))
            .Where(text => !string.IsNullOrEmpty(text) && !ConversationTechnicalText.IsWrapper(text)));
    }

    private static string Value(JsonElement payload, string property) =>
        payload.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()
            : string.Empty;

    internal static string? Text(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() : null;

    private sealed class SnapshotStream(Stream inner) : Stream
    {
        private long remaining = inner.Length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, (int)Math.Min(count, remaining));
            remaining -= read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], cancellationToken).ConfigureAwait(false);
            remaining -= read;
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

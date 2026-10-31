using System.Text;

namespace CodexHistorySync.Core.Antigravity;

/// <summary>
/// Minimal protobuf wire encoder/decoder for Antigravity step payloads, trajectory metadata,
/// and summary blobs.
/// </summary>
public static class AntigravityProtobuf
{
    public const int StepTypeUserInput = 14;
    public const int StepTypePlannerResponse = 15;
    public const int StepStatusDone = 3;

    public static byte[] EncodeTimestamp(DateTimeOffset timestamp)
    {
        using var stream = new MemoryStream();
        var seconds = timestamp.ToUnixTimeSeconds();
        var nanos = (timestamp.UtcTicks % TimeSpan.TicksPerSecond) * 100;
        WriteVarintField(stream, 1, (ulong)seconds);
        if (nanos > 0)
            WriteVarintField(stream, 2, (ulong)nanos);
        return stream.ToArray();
    }

    public static byte[] EncodeWorkspaceInfo(string workspaceUri)
    {
        using var stream = new MemoryStream();
        WriteStringField(stream, 1, workspaceUri);
        WriteStringField(stream, 2, workspaceUri);
        return stream.ToArray();
    }

    public static byte[] EncodeTrajectoryMetadata(
        string? workspaceUri,
        DateTimeOffset createdAt,
        string cascadeId,
        string installId)
    {
        using var stream = new MemoryStream();
        if (!string.IsNullOrWhiteSpace(workspaceUri))
            WriteBytesField(stream, 1, EncodeWorkspaceInfo(workspaceUri));
        WriteBytesField(stream, 2, EncodeTimestamp(createdAt));
        WriteStringField(stream, 3, installId);
        WriteStringField(stream, 6, cascadeId);
        if (!string.IsNullOrWhiteSpace(workspaceUri))
            WriteStringField(stream, 7, workspaceUri);
        WriteStringField(stream, 18, "default-cli-project");
        return stream.ToArray();
    }

    public static byte[] EncodeRawSummary(
        string sessionId,
        string cascadeId,
        string title,
        string preview,
        int stepCount,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset lastUserInputAt,
        int lastUserStepIndex,
        string? workspaceUri,
        string installId)
    {
        using var stream = new MemoryStream();
        WriteStringField(stream, 1, preview);
        WriteVarintField(stream, 2, (ulong)Math.Max(0, stepCount));
        WriteBytesField(stream, 3, EncodeTimestamp(updatedAt));
        WriteStringField(stream, 4, sessionId);
        WriteVarintField(stream, 5, 1);
        WriteBytesField(stream, 7, EncodeTimestamp(createdAt));
        if (!string.IsNullOrWhiteSpace(workspaceUri))
            WriteBytesField(stream, 9, EncodeWorkspaceInfo(workspaceUri));
        WriteBytesField(stream, 10, EncodeTimestamp(lastUserInputAt));
        WriteBytesField(stream, 15, EncodeSingleStringMessage(1, title));
        if (lastUserStepIndex >= 0)
            WriteVarintField(stream, 16, (ulong)lastUserStepIndex);
        WriteBytesField(stream, 17, EncodeTrajectoryMetadata(workspaceUri, createdAt, cascadeId, installId));
        WriteVarintField(stream, 22, 4);
        WriteVarintField(stream, 28, 4);
        return stream.ToArray();
    }

    public static (byte[] Metadata, byte[] StepPayload) EncodeUserStep(
        int stepIndex,
        string text,
        DateTimeOffset timestamp,
        string sessionId,
        string cascadeId,
        string turnId,
        string? workspaceUri,
        string spanHex)
    {
        var tsBytes = EncodeTimestamp(timestamp);
        byte[] metadata;
        using (var metaStream = new MemoryStream())
        {
            WriteBytesField(metaStream, 1, tsBytes);
            WriteVarintField(metaStream, 3, 4);
            WriteStringField(metaStream, 12, turnId);
            WriteBytesField(metaStream, 20, EncodeStepTraceRef(sessionId, stepIndex, modelTag: null, cascadeId));
            WriteBytesField(metaStream, 26, EncodeStatusTimestamp(tsBytes));
            WriteStringField(metaStream, 35, spanHex);
            metadata = metaStream.ToArray();
        }

        byte[] userBody;
        using (var bodyStream = new MemoryStream())
        {
            WriteStringField(bodyStream, 2, text);
            WriteBytesField(bodyStream, 3, EncodeSingleStringMessage(1, text));
            WriteStringField(bodyStream, 4, string.Empty);
            if (!string.IsNullOrWhiteSpace(workspaceUri))
                WriteBytesField(bodyStream, 12, EncodeSingleStringMessage(12, workspaceUri));
            userBody = bodyStream.ToArray();
        }

        using var payloadStream = new MemoryStream();
        WriteVarintField(payloadStream, 1, StepTypeUserInput);
        WriteVarintField(payloadStream, 4, StepStatusDone);
        WriteBytesField(payloadStream, 5, metadata);
        WriteBytesField(payloadStream, 19, userBody);
        return (metadata, payloadStream.ToArray());
    }

    public static (byte[] Metadata, byte[] StepPayload) EncodeAssistantStep(
        int stepIndex,
        string text,
        DateTimeOffset timestamp,
        string sessionId,
        string cascadeId,
        string turnId,
        string spanHex,
        string? thinking = null)
    {
        var tsBytes = EncodeTimestamp(timestamp);
        byte[] metadata;
        using (var metaStream = new MemoryStream())
        {
            WriteBytesField(metaStream, 1, tsBytes);
            WriteVarintField(metaStream, 3, 2);
            WriteBytesField(metaStream, 6, tsBytes);
            WriteBytesField(metaStream, 7, tsBytes);
            WriteBytesField(metaStream, 8, tsBytes);
            WriteStringField(metaStream, 12, turnId);
            WriteBytesField(metaStream, 20, EncodeStepTraceRef(sessionId, stepIndex, modelTag: 5, cascadeId));
            WriteBytesField(metaStream, 26, EncodeStatusTimestamp(tsBytes));
            WriteBytesField(metaStream, 32, tsBytes);
            WriteStringField(metaStream, 35, spanHex);
            metadata = metaStream.ToArray();
        }

        byte[] assistantBody;
        using (var bodyStream = new MemoryStream())
        {
            WriteStringField(bodyStream, 1, text);
            if (!string.IsNullOrEmpty(thinking))
            {
                WriteStringField(bodyStream, 3, thinking);
            }
            WriteStringField(bodyStream, 8, text);
            WriteVarintField(bodyStream, 12, 2);
            assistantBody = bodyStream.ToArray();
        }

        using var payloadStream = new MemoryStream();
        WriteVarintField(payloadStream, 1, StepTypePlannerResponse);
        WriteVarintField(payloadStream, 4, StepStatusDone);
        WriteBytesField(payloadStream, 5, metadata);
        WriteBytesField(payloadStream, 20, assistantBody);
        return (metadata, payloadStream.ToArray());
    }

    public static string? TryExtractStepText(int stepType, ReadOnlySpan<byte> stepPayload)
    {
        if (stepPayload.IsEmpty) return null;
        if (stepType == StepTypeUserInput)
        {
            if (!TryGetLengthDelimitedField(stepPayload, 19, out var userBody))
                return null;
            if (TryGetStringField(userBody, 2, out var directText) && !string.IsNullOrWhiteSpace(directText))
                return directText;
            if (TryGetStringField(userBody, 1, out var queryText) && !string.IsNullOrWhiteSpace(queryText))
                return queryText;
            if (TryGetLengthDelimitedField(userBody, 3, out var nested) &&
                TryGetStringField(nested, 1, out var nestedText) &&
                !string.IsNullOrWhiteSpace(nestedText))
                return nestedText;
            return null;
        }

        if (stepType == StepTypePlannerResponse)
        {
            if (!TryGetLengthDelimitedField(stepPayload, 20, out var assistantBody))
                return null;
            if (TryGetStringField(assistantBody, 1, out var primary) && !string.IsNullOrWhiteSpace(primary))
                return primary;
            if (TryGetStringField(assistantBody, 8, out var secondary) && !string.IsNullOrWhiteSpace(secondary))
                return secondary;
            if (TryGetStringField(assistantBody, 3, out var thinking) && !string.IsNullOrWhiteSpace(thinking))
                return thinking;
            return null;
        }

        return null;
    }

    public static string? ExtractUserInput(byte[]? payload)
    {
        if (payload is null || payload.Length == 0) return null;

        var text = TryExtractStepText(StepTypeUserInput, payload);
        if (!string.IsNullOrWhiteSpace(text))
            return text;

        ReadOnlySpan<byte> body = payload;
        if (TryGetStringField(body, 2, out var directText) && !string.IsNullOrWhiteSpace(directText))
            return directText;
        if (TryGetStringField(body, 1, out var queryText) && !string.IsNullOrWhiteSpace(queryText))
            return queryText;
        if (TryGetLengthDelimitedField(body, 3, out var nested) &&
            TryGetStringField(nested, 1, out var nestedText) &&
            !string.IsNullOrWhiteSpace(nestedText))
            return nestedText;

        return null;
    }

    public static (string? Response, string? Thinking) ExtractPlannerResponse(byte[]? payload)
    {
        if (payload is null || payload.Length == 0)
            return (null, null);

        ReadOnlySpan<byte> body;
        if (TryGetLengthDelimitedField(payload, 20, out var plannerBody))
        {
            body = plannerBody;
        }
        else
        {
            body = payload;
        }

        string? response = null;
        if (TryGetStringField(body, 1, out var primary) && !string.IsNullOrWhiteSpace(primary))
        {
            response = primary;
        }
        else if (TryGetStringField(body, 8, out var secondary) && !string.IsNullOrWhiteSpace(secondary))
        {
            response = secondary;
        }

        string? thinking = null;
        if (TryGetStringField(body, 3, out var thinkingText) && !string.IsNullOrWhiteSpace(thinkingText))
        {
            thinking = thinkingText;
        }

        return (response, thinking);
    }

    public static byte[] EncodeUserInput(string text)
    {
        var now = DateTimeOffset.UtcNow;
        var (_, payload) = EncodeUserStep(
            stepIndex: 0,
            text: text ?? string.Empty,
            timestamp: now,
            sessionId: Guid.NewGuid().ToString(),
            cascadeId: Guid.NewGuid().ToString(),
            turnId: Guid.NewGuid().ToString(),
            workspaceUri: null,
            spanHex: Guid.NewGuid().ToString("N")[..16]);
        return payload;
    }

    public static byte[] EncodePlannerResponse(string response, string? thinking = null)
    {
        var now = DateTimeOffset.UtcNow;
        var (_, payload) = EncodeAssistantStep(
            stepIndex: 1,
            text: response ?? string.Empty,
            timestamp: now,
            sessionId: Guid.NewGuid().ToString(),
            cascadeId: Guid.NewGuid().ToString(),
            turnId: Guid.NewGuid().ToString(),
            spanHex: Guid.NewGuid().ToString("N")[..16],
            thinking: thinking);
        return payload;
    }

    private static byte[] EncodeSingleStringMessage(int fieldNumber, string value)
    {
        using var stream = new MemoryStream();
        WriteStringField(stream, fieldNumber, value);
        return stream.ToArray();
    }

    private static byte[] EncodeStepTraceRef(string sessionId, int stepIndex, int? modelTag, string cascadeId)
    {
        using var stream = new MemoryStream();
        WriteStringField(stream, 1, sessionId);
        if (stepIndex > 0)
            WriteVarintField(stream, 2, (ulong)stepIndex);
        if (modelTag is not null)
            WriteVarintField(stream, 3, (ulong)modelTag.Value);
        WriteStringField(stream, 4, cascadeId);
        return stream.ToArray();
    }

    private static byte[] EncodeStatusTimestamp(byte[] tsBytes)
    {
        using var inner = new MemoryStream();
        WriteVarintField(inner, 1, StepStatusDone);
        WriteBytesField(inner, 2, tsBytes);
        using var outer = new MemoryStream();
        WriteBytesField(outer, 1, inner.ToArray());
        return outer.ToArray();
    }

    private static void WriteVarintField(Stream stream, int fieldNumber, ulong value)
    {
        WriteRawVarint(stream, ((ulong)fieldNumber << 3) | 0UL);
        WriteRawVarint(stream, value);
    }

    private static void WriteBytesField(Stream stream, int fieldNumber, ReadOnlySpan<byte> bytes)
    {
        WriteRawVarint(stream, ((ulong)fieldNumber << 3) | 2UL);
        WriteRawVarint(stream, (ulong)bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteStringField(Stream stream, int fieldNumber, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteBytesField(stream, fieldNumber, bytes);
    }

    private static void WriteRawVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    private static bool TryGetStringField(ReadOnlySpan<byte> message, int targetField, out string value)
    {
        value = string.Empty;
        if (!TryGetLengthDelimitedField(message, targetField, out var slice))
            return false;
        try
        {
            value = Encoding.UTF8.GetString(slice);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetLengthDelimitedField(ReadOnlySpan<byte> message, int targetField, out ReadOnlySpan<byte> result)
    {
        result = default;
        var offset = 0;
        while (offset < message.Length)
        {
            if (!TryReadVarint(message, ref offset, out var tag)) return false;
            var fieldNumber = (int)(tag >> 3);
            var wireType = (int)(tag & 0x07);
            switch (wireType)
            {
                case 0:
                    if (!TryReadVarint(message, ref offset, out _)) return false;
                    break;
                case 1:
                    if (offset + 8 > message.Length) return false;
                    offset += 8;
                    break;
                case 2:
                    if (!TryReadVarint(message, ref offset, out var length)) return false;
                    if (length > (ulong)(message.Length - offset)) return false;
                    var len = (int)length;
                    var slice = message.Slice(offset, len);
                    offset += len;
                    if (fieldNumber == targetField)
                    {
                        result = slice;
                        return true;
                    }
                    break;
                case 5:
                    if (offset + 4 > message.Length) return false;
                    offset += 4;
                    break;
                default:
                    return false;
            }
        }
        return false;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> buffer, ref int offset, out ulong value)
    {
        value = 0;
        var shift = 0;
        while (offset < buffer.Length && shift < 64)
        {
            var b = buffer[offset++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }
}

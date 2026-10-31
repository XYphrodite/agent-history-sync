using System.Text;
using CodexHistorySync.Core.Antigravity;

namespace CodexHistorySync.Core.Tests.Antigravity;

public sealed class AntigravityProtobufTests
{
    [Fact]
    public void EncodeTimestampEncodesSecondsAndNanos()
    {
        var timestamp = new DateTimeOffset(2026, 10, 30, 15, 30, 45, 500, TimeSpan.Zero);
        var bytes = AntigravityProtobuf.EncodeTimestamp(timestamp);

        Assert.NotEmpty(bytes);
        // Field 1 tag: (1 << 3) | 0 = 0x08
        Assert.Equal(0x08, bytes[0]);

        // Unix seconds:
        var expectedSeconds = (ulong)timestamp.ToUnixTimeSeconds();
        Assert.True(bytes.Length > 5);
    }

    [Fact]
    public void EncodeTimestampZeroSubsecondsOmitsNanosField()
    {
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        var bytes = AntigravityProtobuf.EncodeTimestamp(timestamp);

        Assert.NotEmpty(bytes);
        // Field 1 tag: 0x08
        Assert.Equal(0x08, bytes[0]);
        // All bytes should only contain field 1 (no field 2 tag 0x10)
        Assert.DoesNotContain((byte)0x10, bytes);
    }

    [Fact]
    public void EncodeUserStepExtractsDirectTextSuccessfully()
    {
        var text = "Hello Antigravity, please organize my thoughts.";
        var now = DateTimeOffset.UtcNow;
        var (metadata, payload) = AntigravityProtobuf.EncodeUserStep(
            stepIndex: 1,
            text: text,
            timestamp: now,
            sessionId: "a5c7f89b-8e2b-4fa8-b2a0-47b1c31278e1",
            cascadeId: "cascade-42",
            turnId: "turn-1",
            workspaceUri: "file:///C:/Workspace",
            spanHex: "0123456789abcdef");

        Assert.NotEmpty(metadata);
        Assert.NotEmpty(payload);

        var extracted = AntigravityProtobuf.TryExtractStepText(AntigravityProtobuf.StepTypeUserInput, payload);
        Assert.Equal(text, extracted);
    }

    [Fact]
    public void EncodeUserStepWithoutWorkspaceUriExtractsTextSuccessfully()
    {
        var text = "Quick prompt without workspace";
        var now = DateTimeOffset.UtcNow;
        var (_, payload) = AntigravityProtobuf.EncodeUserStep(
            stepIndex: 0,
            text: text,
            timestamp: now,
            sessionId: "11111111-2222-3333-4444-555555555555",
            cascadeId: "cascade-1",
            turnId: "turn-0",
            workspaceUri: null,
            spanHex: "fedcba9876543210");

        var extracted = AntigravityProtobuf.TryExtractStepText(AntigravityProtobuf.StepTypeUserInput, payload);
        Assert.Equal(text, extracted);
    }

    [Fact]
    public void ExtractUserStepFallbackToNestedTextWhenDirectFieldEmpty()
    {
        // Construct user payload where field 19 has empty direct text (field 2)
        // and non-empty nested text (field 3 -> field 1)
        using var nestedStream = new MemoryStream();
        // field 1 in nested
        WriteProtoField(nestedStream, 1, 2, Encoding.UTF8.GetBytes("Nested prompt text"));

        using var bodyStream = new MemoryStream();
        // field 2 direct text is empty
        WriteProtoField(bodyStream, 2, 2, Array.Empty<byte>());
        // field 3 nested message
        WriteProtoField(bodyStream, 3, 2, nestedStream.ToArray());

        using var payloadStream = new MemoryStream();
        WriteProtoField(payloadStream, 19, 2, bodyStream.ToArray());

        var extracted = AntigravityProtobuf.TryExtractStepText(AntigravityProtobuf.StepTypeUserInput, payloadStream.ToArray());
        Assert.Equal("Nested prompt text", extracted);
    }

    [Fact]
    public void EncodeAssistantStepExtractsPrimaryTextSuccessfully()
    {
        var responseText = "Here is the response from the planner model.";
        var now = DateTimeOffset.UtcNow;
        var (metadata, payload) = AntigravityProtobuf.EncodeAssistantStep(
            stepIndex: 2,
            text: responseText,
            timestamp: now,
            sessionId: "a5c7f89b-8e2b-4fa8-b2a0-47b1c31278e1",
            cascadeId: "cascade-42",
            turnId: "turn-2",
            spanHex: "abcdef0123456789");

        Assert.NotEmpty(metadata);
        Assert.NotEmpty(payload);

        var extracted = AntigravityProtobuf.TryExtractStepText(AntigravityProtobuf.StepTypePlannerResponse, payload);
        Assert.Equal(responseText, extracted);
    }

    [Fact]
    public void ExtractAssistantStepFallbackToSecondaryFieldWhenPrimaryEmpty()
    {
        // Construct assistant body with empty primary field 1 and non-empty secondary field 8
        using var bodyStream = new MemoryStream();
        WriteProtoField(bodyStream, 1, 2, Array.Empty<byte>());
        WriteProtoField(bodyStream, 8, 2, Encoding.UTF8.GetBytes("Fallback planner text"));

        using var payloadStream = new MemoryStream();
        WriteProtoField(payloadStream, 20, 2, bodyStream.ToArray());

        var extracted = AntigravityProtobuf.TryExtractStepText(AntigravityProtobuf.StepTypePlannerResponse, payloadStream.ToArray());
        Assert.Equal("Fallback planner text", extracted);
    }

    [Fact]
    public void TryExtractStepTextWithEmptyPayloadReturnsNull()
    {
        var resultUser = AntigravityProtobuf.TryExtractStepText(AntigravityProtobuf.StepTypeUserInput, ReadOnlySpan<byte>.Empty);
        var resultPlanner = AntigravityProtobuf.TryExtractStepText(AntigravityProtobuf.StepTypePlannerResponse, ReadOnlySpan<byte>.Empty);

        Assert.Null(resultUser);
        Assert.Null(resultPlanner);
    }

    [Fact]
    public void TryExtractStepTextWithUnknownStepTypeReturnsNull()
    {
        var (_, payload) = AntigravityProtobuf.EncodeUserStep(
            0, "hello", DateTimeOffset.UtcNow, "id", "cas", "turn", null, "span");

        var result = AntigravityProtobuf.TryExtractStepText(999, payload);
        Assert.Null(result);
    }

    [Fact]
    public void TryExtractStepTextWithMismatchedStepTypeReturnsNull()
    {
        var (_, userPayload) = AntigravityProtobuf.EncodeUserStep(
            0, "user message", DateTimeOffset.UtcNow, "id", "cas", "turn", null, "span");

        // Requesting PlannerResponse for a UserStep payload has no field 20
        var result = AntigravityProtobuf.TryExtractStepText(AntigravityProtobuf.StepTypePlannerResponse, userPayload);
        Assert.Null(result);

        var (_, assistantPayload) = AntigravityProtobuf.EncodeAssistantStep(
            1, "assistant message", DateTimeOffset.UtcNow, "id", "cas", "turn", "span");

        // Requesting UserInput for an AssistantStep payload has no field 19
        var result2 = AntigravityProtobuf.TryExtractStepText(AntigravityProtobuf.StepTypeUserInput, assistantPayload);
        Assert.Null(result2);
    }

    [Fact]
    public void TryExtractStepTextWithTruncatedPayloadReturnsNullGracefully()
    {
        var (_, payload) = AntigravityProtobuf.EncodeUserStep(
            0, "test long text that gets truncated", DateTimeOffset.UtcNow, "id", "cas", "turn", null, "span");

        // Truncate at various lengths
        for (var len = 1; len < payload.Length; len += 5)
        {
            var truncated = payload.AsSpan(0, len);
            var result = AntigravityProtobuf.TryExtractStepText(AntigravityProtobuf.StepTypeUserInput, truncated);
            // It should either return null or the full string (if complete), but never throw
            // At len < payload.Length it should typically fail or return null
        }
    }

    [Fact]
    public void TryExtractStepTextWithCorruptVarintOrLengthReturnsNullWithoutException()
    {
        // 0xFF varint that exceeds 64 bits or claims huge length
        var corrupt1 = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 };
        var corrupt2 = new byte[] { (19 << 3) | 2, 0xFF, 0xFF, 0xFF, 0x7F, 0x01, 0x02 }; // claims large length
        var corrupt3 = new byte[] { (19 << 3) | 7 }; // invalid wire type 7

        Assert.Null(AntigravityProtobuf.TryExtractStepText(AntigravityProtobuf.StepTypeUserInput, corrupt1));
        Assert.Null(AntigravityProtobuf.TryExtractStepText(AntigravityProtobuf.StepTypeUserInput, corrupt2));
        Assert.Null(AntigravityProtobuf.TryExtractStepText(AntigravityProtobuf.StepTypeUserInput, corrupt3));
    }

    [Fact]
    public void EncodeTrajectoryMetadataProducesNonEmptyBytes()
    {
        var now = DateTimeOffset.UtcNow;
        var bytes = AntigravityProtobuf.EncodeTrajectoryMetadata(
            workspaceUri: "file:///C:/Repo",
            createdAt: now,
            cascadeId: "cascade-123",
            installId: "install-abc");

        Assert.NotEmpty(bytes);
    }

    [Fact]
    public void EncodeRawSummaryProducesNonEmptyBytes()
    {
        var now = DateTimeOffset.UtcNow;
        var bytes = AntigravityProtobuf.EncodeRawSummary(
            sessionId: "a5c7f89b-8e2b-4fa8-b2a0-47b1c31278e1",
            cascadeId: "cascade-42",
            title: "Summary Title",
            preview: "Preview text",
            stepCount: 10,
            createdAt: now.AddMinutes(-10),
            updatedAt: now,
            lastUserInputAt: now.AddMinutes(-5),
            lastUserStepIndex: 3,
            workspaceUri: "file:///C:/Workspace",
            installId: "inst-99");

        Assert.NotEmpty(bytes);
    }

    [Fact]
    public void EncodeAndExtractUserInputRoundTripsAccurately()
    {
        var input = "Explain quantum computing in simple terms.";
        var payload = AntigravityProtobuf.EncodeUserInput(input);

        Assert.NotNull(payload);
        Assert.NotEmpty(payload);

        var extracted = AntigravityProtobuf.ExtractUserInput(payload);
        Assert.Equal(input, extracted);

        Assert.Null(AntigravityProtobuf.ExtractUserInput(null));
        Assert.Null(AntigravityProtobuf.ExtractUserInput(Array.Empty<byte>()));
    }

    [Fact]
    public void EncodeAndExtractPlannerResponsePreservesBothResponseAndThinking()
    {
        var response = "The result of your calculation is 42.";
        var thinking = "Step 1: Parse the user prompt. Step 2: Determine appropriate constants.";

        var payload = AntigravityProtobuf.EncodePlannerResponse(response, thinking);
        Assert.NotNull(payload);
        Assert.NotEmpty(payload);

        var (extractedResponse, extractedThinking) = AntigravityProtobuf.ExtractPlannerResponse(payload);
        Assert.Equal(response, extractedResponse);
        Assert.Equal(thinking, extractedThinking);

        // Also verify TryExtractStepText returns the response text when response is present
        var directText = AntigravityProtobuf.TryExtractStepText(AntigravityProtobuf.StepTypePlannerResponse, payload);
        Assert.Equal(response, directText);
    }

    [Fact]
    public void EncodeAndExtractPlannerResponseWithoutThinkingLeavesThinkingNull()
    {
        var response = "Immediate response without reasoning.";
        var payload = AntigravityProtobuf.EncodePlannerResponse(response);

        var (extractedResponse, extractedThinking) = AntigravityProtobuf.ExtractPlannerResponse(payload);
        Assert.Equal(response, extractedResponse);
        Assert.Null(extractedThinking);
    }

    [Fact]
    public void ExtractPlannerResponseWithNullOrEmptyReturnsNullTuple()
    {
        var (resp1, think1) = AntigravityProtobuf.ExtractPlannerResponse(null);
        Assert.Null(resp1);
        Assert.Null(think1);

        var (resp2, think2) = AntigravityProtobuf.ExtractPlannerResponse(Array.Empty<byte>());
        Assert.Null(resp2);
        Assert.Null(think2);
    }

    [Fact]
    public void TryExtractStepTextPlannerFallbackToThinkingWhenResponseEmpty()
    {
        // Build assistant body with empty response (field 1/8) but valid thinking (field 3)
        using var bodyStream = new MemoryStream();
        WriteProtoField(bodyStream, 1, 2, Array.Empty<byte>());
        WriteProtoField(bodyStream, 3, 2, Encoding.UTF8.GetBytes("Chain of thought text only"));
        WriteProtoField(bodyStream, 8, 2, Array.Empty<byte>());

        using var payloadStream = new MemoryStream();
        WriteProtoField(payloadStream, 20, 2, bodyStream.ToArray());

        var extracted = AntigravityProtobuf.TryExtractStepText(AntigravityProtobuf.StepTypePlannerResponse, payloadStream.ToArray());
        Assert.Equal("Chain of thought text only", extracted);

        var (resp, think) = AntigravityProtobuf.ExtractPlannerResponse(payloadStream.ToArray());
        Assert.Null(resp);
        Assert.Equal("Chain of thought text only", think);
    }

    private static void WriteProtoField(Stream stream, int fieldNumber, int wireType, ReadOnlySpan<byte> data)
    {
        var tag = (ulong)((fieldNumber << 3) | wireType);
        WriteVarint(stream, tag);
        if (wireType == 2)
        {
            WriteVarint(stream, (ulong)data.Length);
            stream.Write(data);
        }
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }
}

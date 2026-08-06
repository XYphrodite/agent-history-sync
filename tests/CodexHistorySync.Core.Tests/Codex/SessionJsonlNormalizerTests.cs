using System.Security.Cryptography;
using System.Text;
using CodexHistorySync.Core.Codex;

namespace CodexHistorySync.Core.Tests.Codex;

public sealed class SessionJsonlNormalizerTests
{
    private static readonly UTF8Encoding Utf8 = new(false);

    [Fact]
    public void Normalize_DropsCompactedAndEphemeralRuntimeRecords()
    {
        var source = Utf8.GetBytes(
            """
            {"type":"session_meta","payload":{"id":"thread-1"}}
            {"type":"response_item","payload":{"role":"user","content":[{"type":"text","text":"hi"}]}}
            {"type":"compacted","payload":{"replacement_history":"HUGE"}}
            {"type":"turn_context","payload":{"x":1}}
            {"type":"world_state","payload":{"y":2}}
            {"type":"event_msg","payload":{"kind":"info"}}
            {"type":"inter_agent_communication_metadata","payload":{}}

            """);

        var normalized = SessionJsonlNormalizer.Normalize(source);
        var text = Utf8.GetString(normalized);

        Assert.Contains("session_meta", text, StringComparison.Ordinal);
        Assert.Contains("response_item", text, StringComparison.Ordinal);
        Assert.Contains("event_msg", text, StringComparison.Ordinal);
        Assert.DoesNotContain("compacted", text, StringComparison.Ordinal);
        Assert.DoesNotContain("turn_context", text, StringComparison.Ordinal);
        Assert.DoesNotContain("world_state", text, StringComparison.Ordinal);
        Assert.DoesNotContain("inter_agent_communication_metadata", text, StringComparison.Ordinal);
        Assert.EndsWith("\n", text);
    }

    [Fact]
    public void Normalize_IsDeterministicForHashing()
    {
        var source = Utf8.GetBytes(
            """
            {"type":"session_meta","payload":{"id":"thread-1"}}
            {"type":"compacted","payload":{"replacement_history":"aaaaaaaa"}}
            {"type":"response_item","payload":{"role":"assistant","content":[]}}

            """);

        var first = SessionJsonlNormalizer.Normalize(source);
        var second = SessionJsonlNormalizer.Normalize(source);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(first)), Convert.ToHexString(SHA256.HashData(second)));
        Assert.True(first.Length < source.Length);
    }

    [Fact]
    public void Normalize_ReplacesInputImageBlocksAndDataUris()
    {
        var source = Utf8.GetBytes(
            """
            {"type":"session_meta","payload":{"id":"thread-1"}}
            {"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"see photo"},{"type":"input_image","image_url":"data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAAB","detail":"high"}]}}
            {"type":"response_item","payload":{"type":"custom_tool_call_output","output":"data:image/jpeg;base64,/9j/4AAQSkZJRgABAQAAAQABAAD"}}

            """);

        var text = Utf8.GetString(SessionJsonlNormalizer.Normalize(source));

        Assert.Contains("see photo", text, StringComparison.Ordinal);
        Assert.Contains("[image omitted]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("data:image/png", text, StringComparison.Ordinal);
        Assert.DoesNotContain("data:image/jpeg", text, StringComparison.Ordinal);
        Assert.DoesNotContain("iVBORw0KGgo", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/9j/", text, StringComparison.Ordinal);
        Assert.DoesNotContain("input_image", text, StringComparison.Ordinal);
    }
}

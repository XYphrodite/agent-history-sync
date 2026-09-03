using System.Security.Cryptography;
using System.Text;
using CodexHistorySync.Core.Codex;

namespace CodexHistorySync.Core.Tests.Codex;

public sealed class SessionJsonlNormalizerTests
{
    private static readonly UTF8Encoding Utf8 = new(false);

    [Fact]
    public void Normalize_DropsBulkRuntimeAndToolOutputRecords()
    {
        var source = Utf8.GetBytes(
            """
            {"type":"session_meta","payload":{"id":"thread-1"}}
            {"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"hi"}]}}
            {"type":"response_item","payload":{"type":"function_call_output","output":"HUGE TOOL OUTPUT"}}
            {"type":"response_item","payload":{"type":"custom_tool_call_output","output":"MORE OUTPUT"}}
            {"type":"response_item","payload":{"type":"reasoning","content":"think"}}
            {"type":"compacted","payload":{"replacement_history":"HUGE"}}
            {"type":"event_msg","payload":{"type":"token_count"}}
            {"type":"turn_context","payload":{"x":1}}

            """);

        var text = Utf8.GetString(SessionJsonlNormalizer.Normalize(source));

        Assert.Contains("session_meta", text, StringComparison.Ordinal);
        Assert.Contains("\"hi\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("function_call_output", text, StringComparison.Ordinal);
        Assert.DoesNotContain("custom_tool_call_output", text, StringComparison.Ordinal);
        Assert.DoesNotContain("reasoning", text, StringComparison.Ordinal);
        Assert.DoesNotContain("compacted", text, StringComparison.Ordinal);
        Assert.DoesNotContain("event_msg", text, StringComparison.Ordinal);
        Assert.DoesNotContain("turn_context", text, StringComparison.Ordinal);
        Assert.EndsWith("\n", text);
    }

    [Fact]
    public void Normalize_TruncatesLongRetainedStrings()
    {
        var longText = new string('x', SessionJsonlNormalizer.MaximumRetainedStringChars + 500);
        var source = Utf8.GetBytes(
            "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"" +
            longText + "\"}]}}\n");

        var text = Utf8.GetString(SessionJsonlNormalizer.Normalize(source));

        Assert.Contains("[...truncated 500 chars]", text, StringComparison.Ordinal);
        Assert.DoesNotContain(longText, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_IsDeterministicForHashing()
    {
        var source = Utf8.GetBytes(
            """
            {"type":"session_meta","payload":{"id":"thread-1"}}
            {"type":"compacted","payload":{"replacement_history":"aaaaaaaa"}}
            {"type":"response_item","payload":{"type":"message","role":"assistant","content":[]}}

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
            {"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"data:image/jpeg;base64,/9j/4AAQSkZJRgABAQAAAQABAAD"}]}}

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

    [Fact]
    public void Normalize_IsAFixedPointForAlreadyNormalizedContent()
    {
        // The scanner normalizes what the importer wrote, so a second pass has to be a no-op.
        // It was not: the marker sat past the limit, the next pass cut the marker, and the count
        // changed - which is how 137 of 141 imported sessions became permanent conflicts.
        var source = Utf8.GetBytes(
            "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\"," +
            "\"content\":[{\"type\":\"input_text\",\"text\":\"" + new string('x', 20_000) + "\"}]}}\n");

        var once = SessionJsonlNormalizer.Normalize(source);
        var twice = SessionJsonlNormalizer.Normalize(once);

        Assert.Equal(once, twice);
        Assert.DoesNotContain("truncated 26 chars", Utf8.GetString(twice), StringComparison.Ordinal);
    }
}

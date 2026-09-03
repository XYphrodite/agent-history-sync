using System.Text;
using CodexHistorySync.Core.Grok;

namespace CodexHistorySync.Core.Tests.Grok;

public sealed class GrokChatNormalizerTests
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private static byte[] ChatWithContent(int length) => Utf8.GetBytes(
        "{\"type\":\"user\",\"content\":\"" + new string('x', length) + "\"}\n");

    [Fact]
    public void Normalize_TruncatesLongContent()
    {
        var text = Utf8.GetString(
            GrokChatNormalizer.Normalize(ChatWithContent(GrokChatNormalizer.MaximumContentChars + 500)));

        Assert.Contains("[...truncated 500 chars]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_IsAFixedPointForAlreadyNormalizedContent()
    {
        // Materialize writes the normalized chat to disk and the import hashes it straight back
        // through BuildFromDirectory, which normalizes it again. A second pass that moves the
        // bytes makes that read-back disagree with the authenticated hash, and the session is
        // returned as a conflict rather than imported at all.
        var once = GrokChatNormalizer.Normalize(ChatWithContent(20_000));
        var twice = GrokChatNormalizer.Normalize(once);

        Assert.Equal(once, twice);
        Assert.DoesNotContain("truncated 26 chars", Utf8.GetString(twice), StringComparison.Ordinal);
    }
}

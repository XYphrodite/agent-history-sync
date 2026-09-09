using CodexHistorySync.Cli;

namespace CodexHistorySync.IntegrationTests;

public sealed class ReleaseNotesDisplayTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    [InlineData("\u001b\u0007\u202e")]
    public void EmptyOrInvisibleNotesHaveAFallback(string? notes) =>
        Assert.Equal("No release notes provided.", ReleaseNotesDisplay.Summarize(notes));

    [Fact]
    public void ShortNotesPreserveUnicodeAndNormalizeLineEndings() =>
        Assert.Equal("## Changes\n- Привет 👋\n- Fixed search",
            ReleaseNotesDisplay.Summarize(" \r\n## Changes\r\n- Привет 👋\r- Fixed search\n "));

    [Theory]
    [InlineData(599)]
    [InlineData(600)]
    [InlineData(601)]
    public void LongNotesAreLimitedToSixHundredCharactersWithAnEllipsis(int length)
    {
        var notes = new string('x', length);

        var result = ReleaseNotesDisplay.Summarize(notes);

        Assert.Equal(length > 600 ? notes[..600] + "..." : notes, result);
    }

    [Fact]
    public void TruncationDoesNotSplitAnEmoji() =>
        Assert.Equal(new string('x', 599) + "...",
            ReleaseNotesDisplay.Summarize(new string('x', 599) + "👋 rest"));

    [Fact]
    public void ManyShortLinesCannotFloodTheTerminal() =>
        Assert.Equal(string.Join('\n', Enumerable.Repeat("x", 12)) + "...",
            ReleaseNotesDisplay.Summarize(string.Join('\n', Enumerable.Repeat("x", 100))));

    [Fact]
    public void ControlAndBidiCharactersCannotControlTheTerminal() =>
        Assert.Equal("hello[2J world    !",
            ReleaseNotesDisplay.Summarize("hello\u001b[2J\u0007 world\t\u202e!"));
}

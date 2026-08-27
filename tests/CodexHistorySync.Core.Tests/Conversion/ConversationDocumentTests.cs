using CodexHistorySync.Core.Conversion;

namespace CodexHistorySync.Core.Tests.Conversion;

public sealed class ConversationDocumentTests
{
    [Fact]
    public void Build_PutsARoleHeaderBeforeEachTurnAndABlankLineBetweenTurns()
    {
        var document = ConversationDocument.Build(Conversation(("question", true), ("answer", false)), 40);

        Assert.Equal(
            [
                (ConversationLineKind.RoleHeader, "User"),
                (ConversationLineKind.Text, "question"),
                (ConversationLineKind.Blank, ""),
                (ConversationLineKind.RoleHeader, "Assistant"),
                (ConversationLineKind.Text, "answer")
            ],
            document.Lines.Select(line => (line.Kind, line.Text)));
    }

    [Fact]
    public void Build_TagsEachLineWithItsTurn()
    {
        var document = ConversationDocument.Build(Conversation(("one", true), ("two", false)), 40);

        Assert.Equal(0, document.Lines[0].TurnIndex);
        Assert.Equal(1, document.Lines[^1].TurnIndex);
    }

    [Fact]
    public void Build_WrapsOnWordBoundaries()
    {
        var document = ConversationDocument.Build(Conversation(("alpha bravo charlie delta", true)), 12);

        Assert.Equal(
            ["alpha bravo", "charlie", "delta"],
            document.Lines.Where(line => line.Kind == ConversationLineKind.Text).Select(line => line.Text));
    }

    [Fact]
    public void Build_FillsALineExactlyWithoutSpilling()
    {
        // "alpha bravo" is exactly 11 characters: it must stay on one line at width 11.
        var document = ConversationDocument.Build(Conversation(("alpha bravo", true)), 11);

        Assert.Equal(
            ["alpha bravo"],
            document.Lines.Where(line => line.Kind == ConversationLineKind.Text).Select(line => line.Text));
    }

    [Fact]
    public void Build_HardSplitsAWordWiderThanThePane()
    {
        // A pasted path or a base64 blob must not overflow the pane.
        var document = ConversationDocument.Build(Conversation(("short abcdefghijklmno end", true)), 10);
        var text = document.Lines.Where(line => line.Kind == ConversationLineKind.Text).Select(line => line.Text).ToArray();

        Assert.Equal(["short", "abcdefghij", "klmno end"], text);
        Assert.All(text, line => Assert.True(line.Length <= 10, $"line too wide: '{line}'"));
    }

    [Fact]
    public void Build_KeepsBlankLinesInsideATurn()
    {
        var document = ConversationDocument.Build(Conversation(("first\n\nsecond", true)), 40);

        Assert.Equal(
            [
                (ConversationLineKind.RoleHeader, "User"),
                (ConversationLineKind.Text, "first"),
                (ConversationLineKind.Blank, ""),
                (ConversationLineKind.Text, "second")
            ],
            document.Lines.Select(line => (line.Kind, line.Text)));
    }

    [Fact]
    public void Build_ClampsAnUnusableWidth()
    {
        var document = ConversationDocument.Build(Conversation(("alpha bravo charlie", true)), 0);

        Assert.Equal(ConversationDocument.MinimumWidth, document.Width);
        Assert.All(document.Lines, line => Assert.True(line.Text.Length <= ConversationDocument.MinimumWidth));
    }

    [Fact]
    public void FindMatches_ReturnsLineIndexesCaseInsensitively()
    {
        var document = ConversationDocument.Build(Conversation(("Alpha", true), ("beta ALPHA", false)), 40);

        var matches = document.FindMatches("alpha");

        Assert.Equal(2, matches.Count);
        Assert.All(matches, index => Assert.Contains("lpha", document.Lines[index].Text, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindMatches_TreatsAnEmptyQueryAsNoMatch()
    {
        var document = ConversationDocument.Build(Conversation(("anything", true)), 40);

        Assert.Empty(document.FindMatches(""));
        Assert.Empty(document.FindMatches(null));
    }

    [Fact]
    public void Build_AcceptsAConversationWithNoTurns()
    {
        var document = ConversationDocument.Build(
            new PortableConversation(ConversationAgent.Codex, "id", "title", @"C:\Repos\Demo",
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, []),
            40);

        Assert.Empty(document.Lines);
    }

    private static PortableConversation Conversation(params (string Text, bool IsUser)[] turns) =>
        new(ConversationAgent.Codex, "source-id", "title", @"C:\Repos\Demo",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            turns.Select(turn => new PortableTurn(
                turn.IsUser ? ConversationRole.User : ConversationRole.Assistant, turn.Text)).ToArray());
}

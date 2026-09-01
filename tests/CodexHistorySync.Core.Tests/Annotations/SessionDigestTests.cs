using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Conversion;

namespace CodexHistorySync.Core.Tests.Annotations;

public sealed class SessionDigestTests
{
    [Fact]
    public void Build_RendersEveryTurnInOrderBehindItsRole()
    {
        var digest = SessionDigest.Build(Conversation(
            (ConversationRole.User, "what broke"),
            (ConversationRole.Assistant, "the event log service"),
            (ConversationRole.User, "fixed")));

        Assert.Equal(
            "USER: what broke\n\nASSISTANT: the event log service\n\nUSER: fixed",
            digest.Text);
    }

    [Fact]
    public void Build_DropsTechnicalWrapperTurns()
    {
        var digest = SessionDigest.Build(Conversation(
            (ConversationRole.User, "<ide_opened_file>ROADMAP.md</ide_opened_file>"),
            (ConversationRole.User, "<system-reminder>remembered</system-reminder>"),
            (ConversationRole.User, "the real question")));

        Assert.Equal("USER: the real question", digest.Text);
    }

    [Fact]
    public void Build_DropsBlankTurnsAndTrimsTheRest()
    {
        var digest = SessionDigest.Build(Conversation(
            (ConversationRole.User, "   "),
            (ConversationRole.Assistant, "  padded  ")));

        Assert.Equal("ASSISTANT: padded", digest.Text);
    }

    [Fact]
    public void Build_CutsASingleTurnThatWouldEatTheWholeBudget()
    {
        var digest = SessionDigest.Build(Conversation(
            (ConversationRole.User, new string('u', SessionDigest.MaximumTurnCharacters + 500)),
            (ConversationRole.Assistant, "answer")));

        Assert.Equal(
            "USER: " + new string('u', SessionDigest.MaximumTurnCharacters) + "\n\nASSISTANT: answer",
            digest.Text);
    }

    [Fact]
    public void Build_KeepsTheHeadAndTheTailOfATooLongConversation()
    {
        var turns = Enumerable.Range(0, 40)
            .Select(index => (ConversationRole.User, $"turn {index} " + new string('x', 500)))
            .ToArray();

        var digest = SessionDigest.Build(Conversation(turns), maximumCharacters: 4000);

        Assert.True(digest.Text.Length <= 4000, $"digest was {digest.Text.Length} characters");
        Assert.StartsWith("USER: turn 0 ", digest.Text, StringComparison.Ordinal);
        Assert.Contains("[... middle omitted ...]", digest.Text, StringComparison.Ordinal);
        // The end of a session says how it turned out, so it has to survive the cut as well.
        Assert.EndsWith("xxx", digest.Text, StringComparison.Ordinal);
        Assert.Contains("turn 39", digest.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ReturnsAnEmptyDigestWhenNothingIsWorthSending()
    {
        var digest = SessionDigest.Build(Conversation((ConversationRole.User, "<system-reminder>only noise</system-reminder>")));

        Assert.Empty(digest.Text);
        Assert.True(digest.IsEmpty);
    }

    [Fact]
    public void Build_KeepsTheRequestTheSessionOpenedWith()
    {
        // What the session was for. Without it a model names the loudest problem in the
        // transcript rather than the work the session was.
        var digest = SessionDigest.Build(Conversation(
            (ConversationRole.User, "<system-reminder>noise</system-reminder>"),
            (ConversationRole.User, "make this machine a second GPU box"),
            (ConversationRole.Assistant, "starting"),
            (ConversationRole.User, "and put gpt-oss on it")));

        Assert.Equal("make this machine a second GPU box", digest.OpeningRequest);
    }

    [Fact]
    public void Build_CutsAnOpeningRequestThatIsAnEssay()
    {
        var digest = SessionDigest.Build(Conversation(
            (ConversationRole.User, new string('o', SessionDigest.MaximumOpeningCharacters + 200))));

        Assert.Equal(SessionDigest.MaximumOpeningCharacters, digest.OpeningRequest?.Length);
    }

    [Fact]
    public void Build_HasNoOpeningRequestWhenTheUserNeverSpoke()
    {
        var digest = SessionDigest.Build(Conversation((ConversationRole.Assistant, "only an answer")));

        Assert.Null(digest.OpeningRequest);
    }

    [Fact]
    public void Build_HashesTheSameConversationTheSameWayTwice()
    {
        var first = SessionDigest.Build(Conversation((ConversationRole.User, "question")));
        var second = SessionDigest.Build(Conversation((ConversationRole.User, "question")));

        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal(64, first.Hash.Length);
    }

    [Fact]
    public void Build_HashesADifferentConversationDifferently()
    {
        var first = SessionDigest.Build(Conversation((ConversationRole.User, "question")));
        var second = SessionDigest.Build(Conversation(
            (ConversationRole.User, "question"),
            (ConversationRole.Assistant, "answer")));

        Assert.NotEqual(first.Hash, second.Hash);
    }

    [Fact]
    public void Build_IgnoresEverythingButTheTurnsWhenHashing()
    {
        // Two machines must agree on the hash of one synchronized session, and they do not agree
        // on its native path, its clock, or the title the agent gave it.
        var first = SessionDigest.Build(new PortableConversation(
            ConversationAgent.Claude, "one", "A title", @"C:\Repos\Reborn",
            new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 1, 11, 0, 0, TimeSpan.Zero),
            [new PortableTurn(ConversationRole.User, "question")]));
        var second = SessionDigest.Build(new PortableConversation(
            ConversationAgent.Codex, "two", "Another title", @"D:\elsewhere",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 2, 2, 0, 0, 0, TimeSpan.Zero),
            [new PortableTurn(ConversationRole.User, "question")]));

        Assert.Equal(first.Hash, second.Hash);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Build_RefusesABudgetItCannotHonour(int maximumCharacters)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionDigest.Build(
            Conversation((ConversationRole.User, "question")), maximumCharacters));
    }

    private static PortableConversation Conversation(params (ConversationRole Role, string Text)[] turns) =>
        new(
            ConversationAgent.Claude,
            "10000000-0000-0000-0000-000000000001",
            "Session title",
            @"C:\Repos\Reborn",
            new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 1, 11, 0, 0, TimeSpan.Zero),
            turns.Select(turn => new PortableTurn(turn.Role, turn.Text)).ToArray());
}

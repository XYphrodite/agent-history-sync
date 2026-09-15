using System.Text;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Kimi;

namespace CodexHistorySync.Core.Tests.Kimi;

public sealed class KimiWireConversationTests : IDisposable
{
    private readonly string wirePath = Path.Combine(
        Path.GetTempPath(), $"codex-history-sync-kimi-wire-{Guid.NewGuid():N}", "wire.jsonl");

    [Fact]
    public void ReadTurnsExtractsUserAndAssistantTextAndSkipsThinkingAndToolTraffic()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(wirePath)!);
        File.WriteAllText(wirePath, """
            {"type":"metadata","protocol_version":"1.5"}
            {"type":"agent.message.appended","message":{"message":{"role":"user","content":[{"type":"text","text":"first question"}]},"meta":{}},"time":1}
            {"type":"agent.message.appended","message":{"message":{"role":"assistant","content":[{"type":"think","think":"hidden reasoning"},{"type":"text","text":"first answer part 1"},{"type":"text","text":"first answer part 2"}],"toolCalls":[{"type":"function","name":"Read","arguments":"{}"}]},"meta":{}},"time":2}
            {"type":"context.append_loop_event","event":{"type":"tool.result","text":"raw tool output"}}
            {"type":"agent.message.appended","message":{"message":{"role":"system","content":[{"type":"text","text":"not portable"}]},"meta":{}},"time":3}
            {"type":"agent.message.appended","message":{"message":{"role":"user","content":"plain string content"},"meta":{}},"time":4}

            """, new UTF8Encoding(false));

        var turns = KimiWireConversation.ReadTurns(wirePath);

        Assert.Equal(3, turns.Count);
        Assert.Equal(new PortableTurn(ConversationRole.User, "first question"), turns[0]);
        Assert.Equal(new PortableTurn(ConversationRole.Assistant, "first answer part 1\n\nfirst answer part 2"), turns[1]);
        Assert.Equal(new PortableTurn(ConversationRole.User, "plain string content"), turns[2]);
        Assert.DoesNotContain(turns, turn => turn.Text.Contains("hidden reasoning", StringComparison.Ordinal));
        Assert.DoesNotContain(turns, turn => turn.Text.Contains("raw tool output", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadTurnsToleratesBrokenLinesAndNonMessageEvents()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(wirePath)!);
        File.WriteAllText(wirePath, """
            not json at all
            {"type":"turn.prompt","input":[{"type":"text","text":"duplicate stream ignored"}]}
            {"type":"agent.message.appended","message":{"message":{"role":"assistant","content":[]},"meta":{}},"time":1}
            {"type":"agent.message.appended","message":{"message":{"role":"assistant","content":[{"type":"text","text":"answer"}]},"meta":{}},"time":2}

            """, new UTF8Encoding(false));

        var turns = KimiWireConversation.ReadTurns(wirePath);

        var turn = Assert.Single(turns);
        Assert.Equal(new PortableTurn(ConversationRole.Assistant, "answer"), turn);
    }

    [Fact]
    public void ReadTurnsCollectsAssistantProseThatOnlyExistsAsLoopTextParts()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(wirePath)!);
        File.WriteAllText(wirePath, """
            {"type":"metadata","protocol_version":"1.5"}
            {"type":"agent.message.appended","message":{"message":{"role":"user","content":[{"type":"text","text":"вопрос"}]},"meta":{}},"time":1}
            {"type":"context.append_message","agentId":"main","message":{"role":"user","content":[{"type":"text","text":"вопрос"}]}}
            {"type":"context.append_loop_event","event":{"type":"content.part","part":{"type":"think","think":"скрытое рассуждение"}}}
            {"type":"context.append_loop_event","event":{"type":"content.part","part":{"type":"text","text":"часть ответа"}}}
            {"type":"context.append_loop_event","event":{"type":"tool.result","text":"сырой вывод инструмента"}}
            {"type":"context.append_loop_event","event":{"type":"content.part","part":{"type":"text","text":"часть ответа"}}}

            """, new UTF8Encoding(false));

        var turns = KimiWireConversation.ReadTurns(wirePath);

        Assert.Equal(2, turns.Count);
        Assert.Equal(new PortableTurn(ConversationRole.User, "вопрос"), turns[0]);
        Assert.Equal(new PortableTurn(ConversationRole.Assistant, "часть ответа"), turns[1]);
    }

    [Fact]
    public void ReadTurnsPrefersTheAppendedCopyWhenStreamsOverlapAndKeepsOrder()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(wirePath)!);
        File.WriteAllText(wirePath, """
            {"type":"agent.message.appended","message":{"message":{"role":"assistant","content":[{"type":"text","text":"ответ два блока"}]},"meta":{}},"time":1}
            {"type":"context.append_loop_event","event":{"type":"content.part","part":{"type":"text","text":"ответ два блока"}}}
            {"type":"context.append_loop_event","event":{"type":"content.part","part":{"type":"text","text":"продолжение воркфлоу"}}}
            {"type":"agent.message.appended","message":{"message":{"role":"user","content":[{"type":"text","text":"следующий вопрос"}]},"meta":{}},"time":2}

            """, new UTF8Encoding(false));

        var turns = KimiWireConversation.ReadTurns(wirePath);

        Assert.Equal(3, turns.Count);
        Assert.Equal(new PortableTurn(ConversationRole.Assistant, "ответ два блока"), turns[0]);
        Assert.Equal(new PortableTurn(ConversationRole.Assistant, "продолжение воркфлоу"), turns[1]);
        Assert.Equal(new PortableTurn(ConversationRole.User, "следующий вопрос"), turns[2]);
    }

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(wirePath)!;
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

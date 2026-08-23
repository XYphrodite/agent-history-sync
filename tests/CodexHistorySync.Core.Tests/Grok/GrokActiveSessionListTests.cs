using System.Text.Json;
using CodexHistorySync.Core.Grok;

namespace CodexHistorySync.Core.Tests.Grok;

public sealed class GrokActiveSessionListTests
{
    [Fact]
    public void Parse_keeps_session_ids_with_positive_pids()
    {
        var records = GrokActiveSessionList.Parse(
            """[{"session_id":"open-one","pid":111},{"session_id":"open-two","pid":222}]""");

        Assert.Equal(
            [new GrokActiveSessionRecord("open-one", 111), new GrokActiveSessionRecord("open-two", 222)],
            records);
    }

    [Fact]
    public void Parse_skips_records_without_a_usable_pid_or_id()
    {
        var records = GrokActiveSessionList.Parse(
            """[{"session_id":"missing-pid"},{"pid":12},{"session_id":"zero","pid":0},{"session_id":"ok","pid":12}]""");

        Assert.Equal([new GrokActiveSessionRecord("ok", 12)], records);
    }

    [Fact]
    public void Parse_returns_empty_for_a_non_array_document()
    {
        Assert.Empty(GrokActiveSessionList.Parse("""{"session_id":"x","pid":1}"""));
    }

    [Fact]
    public void Parse_rejects_invalid_json()
    {
        Assert.ThrowsAny<JsonException>(() => GrokActiveSessionList.Parse("{"));
    }
}

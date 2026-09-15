using System.Text.Json.Nodes;
using CodexHistorySync.Core.Kimi;

namespace CodexHistorySync.Core.Tests.Kimi;

public sealed class KimiSessionIndexTests
{
    [Fact]
    public void ParseTreatsMissingOrEmptyContentAsAnEmptyList()
    {
        Assert.Empty(KimiSessionIndex.Parse(null));
        Assert.Empty(KimiSessionIndex.Parse(""));
        Assert.Empty(KimiSessionIndex.Parse("  \n "));
    }

    [Fact]
    public void ParseRejectsContentThatIsNotJsonLines()
    {
        Assert.Throws<InvalidDataException>(() => KimiSessionIndex.Parse("not json"));
        Assert.Throws<InvalidDataException>(() => KimiSessionIndex.Parse("\"just a string\""));
        Assert.Throws<InvalidDataException>(() => KimiSessionIndex.Parse("[1,2]"));
    }

    [Fact]
    public void MergeAppendsANewEntryAsOneCompactLine()
    {
        var entry = KimiSessionIndex.CreateEntry("session_a", "C:/h/sessions/wd_a_1/session_a", "C:/work");

        var merged = KimiSessionIndex.Merge(null, entry);
        var roundTrip = KimiSessionIndex.Parse(merged);

        Assert.Single(roundTrip);
        Assert.Equal("session_a", KimiSessionIndex.SessionIdOf(roundTrip[0]));
        Assert.DoesNotContain("\n", merged);
    }

    [Fact]
    public void MergeReplacesOnlyTheMatchingLineAndKeepsEveryOtherLineVerbatim()
    {
        var original = string.Join("\n", new[]
        {
            "{\"sessionId\":\"session_keep\",\"sessionDir\":\"C:/h/keep\",\"workDir\":\"C:/w1\"}",
            "this line is not json and must survive",
            "{\"sessionId\":\"session_target\",\"sessionDir\":\"C:/h/old\",\"workDir\":\"C:/w2\"}",
            ""
        });
        var entry = KimiSessionIndex.CreateEntry("session_target", "C:/h/new", "C:/w2");

        var merged = KimiSessionIndex.Merge(original, entry);
        var lines = merged.Split('\n');

        Assert.Contains(lines, line => line == "{\"sessionId\":\"session_keep\",\"sessionDir\":\"C:/h/keep\",\"workDir\":\"C:/w1\"}");
        Assert.Contains(lines, line => line == "this line is not json and must survive");
        Assert.Contains(lines, line => line.Contains("\"sessionDir\":\"C:/h/new\"", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("\"sessionDir\":\"C:/h/old\"", StringComparison.Ordinal));
        Assert.Single(lines, line => line.Contains("session_target", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoveDropsOnlyTheMatchingLine()
    {
        var original = string.Join("\n", new[]
        {
            "{\"sessionId\":\"session_keep\",\"sessionDir\":\"C:/h/keep\",\"workDir\":\"C:/w1\"}",
            "{\"sessionId\":\"session_target\",\"sessionDir\":\"C:/h/old\",\"workDir\":\"C:/w2\"}"
        });

        var removed = KimiSessionIndex.Remove(original, "session_target");
        var remaining = KimiSessionIndex.Parse(removed);

        Assert.Single(remaining);
        Assert.Equal("session_keep", KimiSessionIndex.SessionIdOf(remaining[0]));
    }

    [Fact]
    public void CreateEntryCarriesExactlyTheFieldsKimiWrites()
    {
        var entry = KimiSessionIndex.CreateEntry("session_a", "C:/h/session_a", "C:/work");

        Assert.Equal(3, entry.Count);
        Assert.Equal("session_a", entry["sessionId"]!.GetValue<string>());
        Assert.Equal("C:/h/session_a", entry["sessionDir"]!.GetValue<string>());
        Assert.Equal("C:/work", entry["workDir"]!.GetValue<string>());
        Assert.IsType<JsonObject>(entry);
    }
}

using System.Text;
using System.Text.Json;

namespace CodexHistorySync.IntegrationTests;

public sealed class SessionMcpProcessTests
{
    [Fact]
    public async Task StdioHandshakeSearchAndPagedReadWorkWithoutSyncConfiguration()
    {
        await using var fixture = await McpProcessFixture.StartAsync();
        var original = await File.ReadAllBytesAsync(fixture.SessionPath);
        var initialized = await fixture.InitializeAsync();
        Assert.Equal("2025-11-25", initialized.GetProperty("protocolVersion").GetString());
        Assert.Equal("agent-sync", initialized.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(initialized.GetProperty("capabilities").TryGetProperty("tools", out _));

        var listed = await fixture.RequestAsync("tools/list", new { });
        var tools = listed.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(new[] { "get_session", "search_sessions" }, tools.Select(t => t.GetProperty("name").GetString()).Order());
        Assert.All(tools, tool =>
        {
            Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
            Assert.False(tool.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("cancellationToken", out _));
        });
        Assert.False(File.Exists(fixture.CatalogPath));

        var search = await fixture.CallAsync("search_sessions", new { query = "xylophone handshake" });
        var hit = Assert.Single(Data(search).GetProperty("sessions").EnumerateArray());
        Assert.Equal("continue", hit.GetProperty("agent").GetString());
        Assert.Equal(McpProcessFixture.SessionId, hit.GetProperty("session_id").GetString());
        Assert.Equal("Local example", hit.GetProperty("title").GetString());
        Assert.True(File.Exists(fixture.CatalogPath));

        var text = new StringBuilder();
        int? offset = 0;
        do
        {
            var result = await fixture.CallAsync("get_session", new
            {
                agent = "continue", session_id = McpProcessFixture.SessionId, offset = offset.Value, max_characters = 17
            });
            var page = Data(result);
            var fragment = page.GetProperty("text").GetString()!;
            Assert.InRange(fragment.Length, 1, 17);
            Assert.Equal(offset.Value, page.GetProperty("offset").GetInt32());
            text.Append(fragment);
            var next = page.GetProperty("next_offset");
            offset = next.ValueKind == JsonValueKind.Null ? null : next.GetInt32();
            if (offset.HasValue) Assert.Equal(text.Length, offset.Value);
        } while (offset.HasValue);

        Assert.Equal($"USER:\n{McpProcessFixture.UserText}\n\nASSISTANT:\n{McpProcessFixture.AssistantText}", text.ToString());
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.SessionPath));
        await fixture.FinishAsync();
    }

    [Fact]
    public async Task InvalidArgumentsAreToolErrorsAndDoNotEndTheConnection()
    {
        await using var fixture = await McpProcessFixture.StartAsync();
        await fixture.InitializeAsync();
        var errors = new[]
        {
            await fixture.CallAsync("search_sessions", new { query = " ", limit = 20 }),
            await fixture.CallAsync("search_sessions", new { query = "test", limit = 201 }),
            await fixture.CallAsync("get_session", new { agent = "1", session_id = McpProcessFixture.SessionId }),
            await fixture.CallAsync("get_session", new { agent = "continue", session_id = "../config.json" }),
            await fixture.CallAsync("get_session", new { agent = "continue", session_id = "absent" }),
            await fixture.CallAsync("get_session", new { agent = "continue", session_id = McpProcessFixture.SessionId, offset = -1 }),
            await fixture.CallAsync("get_session", new { agent = "continue", session_id = McpProcessFixture.SessionId, max_characters = 64001 }),
            await fixture.CallAsync("get_session", new { agent = "continue", session_id = McpProcessFixture.SessionId, offset = int.MaxValue })
        };
        Assert.All(errors, result =>
        {
            Assert.True(result.GetProperty("isError").GetBoolean());
            Assert.DoesNotContain(fixture.Root, result.ToString(), StringComparison.OrdinalIgnoreCase);
        });
        Assert.False(File.Exists(fixture.CatalogPath));
        Assert.Single(Data(await fixture.CallAsync("search_sessions", new { query = "handshake" }))
            .GetProperty("sessions").EnumerateArray());
        await fixture.FinishAsync();
    }

    [Fact]
    public async Task SearchRefreshesChangedTitlesAndDeletedSessionsDuringTheSameConnection()
    {
        await using var fixture = await McpProcessFixture.StartAsync();
        await fixture.InitializeAsync();
        Assert.Single(Data(await fixture.CallAsync("search_sessions", new { query = "handshake" }))
            .GetProperty("sessions").EnumerateArray());
        await fixture.WriteSessionAsync("NewlyRenamedTitle");
        var renamed = Data(await fixture.CallAsync("search_sessions", new { query = "NewlyRenamedTitle" }));
        Assert.Single(renamed.GetProperty("sessions").EnumerateArray());
        File.Delete(fixture.SessionPath);
        Assert.Empty(Data(await fixture.CallAsync("search_sessions", new { query = "handshake" }))
            .GetProperty("sessions").EnumerateArray());
        var deleted = await fixture.CallAsync("get_session", new { agent = "continue", session_id = McpProcessFixture.SessionId });
        Assert.True(deleted.GetProperty("isError").GetBoolean());
        await fixture.FinishAsync();
    }

    internal static JsonElement Data(JsonElement result)
    {
        Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean(), result.ToString());
        var data = result.GetProperty("structuredContent");
        var fallback = result.GetProperty("content")[0];
        Assert.Equal("text", fallback.GetProperty("type").GetString());
        using var fallbackJson = JsonDocument.Parse(fallback.GetProperty("text").GetString()!);
        Assert.True(JsonElement.DeepEquals(data, fallbackJson.RootElement));
        return data;
    }
}

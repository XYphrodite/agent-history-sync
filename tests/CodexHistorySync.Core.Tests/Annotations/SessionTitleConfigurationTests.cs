using CodexHistorySync.Core.Annotations;

namespace CodexHistorySync.Core.Tests.Annotations;

public sealed class SessionTitleConfigurationTests
{
    [Fact]
    public void Load_LeavesTitlingOffWhenNothingIsConfigured()
    {
        using var fixture = new ConfigurationFixture();

        var configuration = fixture.Load();

        Assert.False(configuration.IsConfigured);
        Assert.Null(configuration.Options.Endpoint);
        Assert.Null(configuration.Rejection);
    }

    [Fact]
    public void Load_ReadsTheEndpointTheModelAndTheLanguageFromTheFile()
    {
        using var fixture = new ConfigurationFixture();
        fixture.Write("""
            { "schemaVersion": 1, "endpoint": "http://100.105.87.52:11434",
              "model": "gpt-oss:20b", "language": "ru" }
            """);

        var configuration = fixture.Load();

        Assert.True(configuration.IsConfigured);
        Assert.Equal("http://100.105.87.52:11434", configuration.Options.Endpoint);
        Assert.Equal("gpt-oss:20b", configuration.Options.Model);
        Assert.Equal("ru", configuration.Options.Language);
    }

    [Fact]
    public void Load_DefaultsTheModelAndTheLanguageTheFileLeavesOut()
    {
        using var fixture = new ConfigurationFixture();
        fixture.Write("""{ "schemaVersion": 1, "endpoint": "http://127.0.0.1:11434" }""");

        var configuration = fixture.Load();

        Assert.Equal(SessionTitleOptions.DefaultModel, configuration.Options.Model);
        Assert.Equal("auto", configuration.Options.Language);
    }

    [Fact]
    public void Load_LetsTheEnvironmentOverrideTheFile()
    {
        using var fixture = new ConfigurationFixture();
        fixture.Write("""{ "schemaVersion": 1, "endpoint": "http://127.0.0.1:11434", "model": "qwen3:8b" }""");
        fixture.Environment["AGENT_SYNC_TITLE_ENDPOINT"] = "http://10.0.0.5:11434";
        fixture.Environment["AGENT_SYNC_TITLE_MODEL"] = "mistral-nemo:12b";

        var configuration = fixture.Load();

        Assert.Equal("http://10.0.0.5:11434", configuration.Options.Endpoint);
        Assert.Equal("mistral-nemo:12b", configuration.Options.Model);
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://localhost:11434")]
    [InlineData("http://192.168.1.2:8080")]
    [InlineData("http://10.0.0.5:11434")]
    [InlineData("http://172.16.4.4:11434")]
    [InlineData("http://100.105.87.52:11434")]
    [InlineData("https://100.105.87.52:11434")]
    [InlineData("http://[::1]:11434")]
    public void Load_AcceptsThisMachineAndPrivateAddresses(string endpoint)
    {
        // A tailnet address is a private one: 100.64.0.0/10 is where every tailnet node lives.
        using var fixture = new ConfigurationFixture();
        fixture.Environment["AGENT_SYNC_TITLE_ENDPOINT"] = endpoint;

        var configuration = fixture.Load();

        Assert.True(configuration.IsConfigured, configuration.Rejection);
        Assert.Null(configuration.Rejection);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("http://8.8.8.8:11434")]
    [InlineData("http://ollama.example.com:11434")]
    public void Load_RefusesAnEndpointOffThisMachineAndOffAPrivateNetwork(string endpoint)
    {
        // Session text is the whole conversation. It goes to a box the operator names by address,
        // never to a name that could resolve anywhere.
        using var fixture = new ConfigurationFixture();
        fixture.Environment["AGENT_SYNC_TITLE_ENDPOINT"] = endpoint;

        var configuration = fixture.Load();

        Assert.False(configuration.IsConfigured);
        Assert.NotNull(configuration.Rejection);
    }

    [Theory]
    [InlineData("ftp://127.0.0.1:11434")]
    [InlineData(@"C:\ollama")]
    [InlineData("127.0.0.1:11434")]
    public void Load_RefusesAnAddressThatIsNotHttp(string endpoint)
    {
        using var fixture = new ConfigurationFixture();
        fixture.Environment["AGENT_SYNC_TITLE_ENDPOINT"] = endpoint;

        var configuration = fixture.Load();

        Assert.False(configuration.IsConfigured);
        Assert.NotNull(configuration.Rejection);
    }

    [Fact]
    public void Load_StaysInertWhenTheFileCannotBeParsed()
    {
        using var fixture = new ConfigurationFixture();
        fixture.Write("{ not json");

        var configuration = fixture.Load();

        Assert.False(configuration.IsConfigured);
        Assert.NotNull(configuration.Rejection);
    }

    [Fact]
    public void Load_StaysInertOnASchemaVersionItDoesNotKnow()
    {
        using var fixture = new ConfigurationFixture();
        fixture.Write("""{ "schemaVersion": 99, "endpoint": "http://127.0.0.1:11434" }""");

        var configuration = fixture.Load();

        Assert.False(configuration.IsConfigured);
        Assert.NotNull(configuration.Rejection);
    }

    private sealed class ConfigurationFixture : IDisposable
    {
        public ConfigurationFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"chs-titles-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(Root, "CodexHistorySync"));
        }

        public string Root { get; }

        public Dictionary<string, string?> Environment { get; } = [];

        public void Write(string json) =>
            File.WriteAllText(Path.Combine(Root, "CodexHistorySync", "titles.json"), json);

        public SessionTitleConfiguration Load() =>
            SessionTitleConfiguration.Load(Root, name => Environment.GetValueOrDefault(name));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

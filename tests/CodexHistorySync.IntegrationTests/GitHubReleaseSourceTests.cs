using System.Net;
using CodexHistorySync.Cli;

namespace CodexHistorySync.IntegrationTests;

public sealed class GitHubReleaseSourceTests
{
    [Theory]
    [InlineData("\"Added session search\"", "Added session search")]
    [InlineData("null", null)]
    [InlineData("42", null)]
    [InlineData("{}", null)]
    [InlineData("[]", null)]
    [InlineData(null, null)]
    public async Task ReadsOptionalNotesAndBuildsALinkToTheTrustedRepository(string? bodyJson, string? expected)
    {
        using var source = new GitHubReleaseSource(new ReleaseHandler(bodyJson));

        var release = await source.ResolveAsync(null, CancellationToken.None);

        Assert.Equal(expected, release.Notes);
        Assert.Equal("https://github.com/XYphrodite/agent-history-sync/releases/tag/v0.12.0",
            release.ReleasePageUrl!.AbsoluteUri);
    }

    private sealed class ReleaseHandler(string? bodyJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bodyProperty = bodyJson is null ? string.Empty : $"\"body\": {bodyJson},";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
                    {
                        "tag_name": "v0.12.0",
                        {{bodyProperty}}
                        "html_url": "https://untrusted.example/fake-release",
                        "assets": [
                            { "name": "agent-sync.exe", "browser_download_url": "https://github.com/XYphrodite/agent-history-sync/releases/download/v0.12.0/agent-sync.exe" },
                            { "name": "agent-sync.exe.sha256", "browser_download_url": "https://github.com/XYphrodite/agent-history-sync/releases/download/v0.12.0/agent-sync.exe.sha256" }
                        ]
                    }
                    """)
            });
        }
    }
}

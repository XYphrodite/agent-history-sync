using System.Net;
using System.Net.Http.Headers;
using CodexHistorySync.Cli;
using SelfUpdateKit;

namespace CodexHistorySync.IntegrationTests;

/// <summary>
/// Every refusal used to read "The release could not be read from GitHub", which sent the reader
/// to look at a release that was published and intact. These pin the four causes apart, through
/// the wired agent-sync source rather than the kit internals.
/// </summary>
public sealed class GitHubReleaseFailureTests
{
    [Fact]
    public async Task AUsedUpRateLimitSaysSoAndSaysHowLongTheWaitIs()
    {
        var reset = DateTimeOffset.UtcNow.AddSeconds(119);
        using var source = new GitHubReleaseSource(AgentSyncUpdate.Options(), Refused(HttpStatusCode.Forbidden,
            ("X-RateLimit-Remaining", "0"),
            ("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString())));

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            source.ResolveAsync(null, CancellationToken.None));

        Assert.Contains("rate limit", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2 minutes from now", failure.Message, StringComparison.Ordinal);
        Assert.Contains("60 requests an hour", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AForbiddenWithBudgetLeftIsNotReportedAsARateLimit()
    {
        // 403 is also how GitHub refuses things no amount of waiting will fix. Telling the
        // operator to wait for a limit that is not spent is worse than saying nothing.
        using var source = new GitHubReleaseSource(AgentSyncUpdate.Options(), Refused(HttpStatusCode.Forbidden,
            ("X-RateLimit-Remaining", "57")));

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            source.ResolveAsync(null, CancellationToken.None));

        Assert.DoesNotContain("rate limit", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("403", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecondaryLimitIsARateLimitEvenWithNoBudgetHeaders()
    {
        // A secondary limit answers 429 and carries Retry-After instead of a budget.
        var refused = new HttpResponseMessage((HttpStatusCode)429);
        refused.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(4));
        using var source = new GitHubReleaseSource(AgentSyncUpdate.Options(), Fixed(refused));

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            source.ResolveAsync(null, CancellationToken.None));

        Assert.Contains("rate limit", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4 minutes from now", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingReleaseNamesTheOtherThingsThatLookTheSame()
    {
        using var source = new GitHubReleaseSource(AgentSyncUpdate.Options(), Fixed(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            source.ResolveAsync(null, CancellationToken.None));

        Assert.DoesNotContain("rate limit", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("renamed or made private", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnythingElseCarriesTheStatusGitHubAnswered()
    {
        using var source = new GitHubReleaseSource(AgentSyncUpdate.Options(), Fixed(new HttpResponseMessage(HttpStatusCode.BadGateway)));

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            source.ResolveAsync(null, CancellationToken.None));

        Assert.Contains("502", failure.Message, StringComparison.Ordinal);
        Assert.StartsWith("The release could not be read from GitHub", failure.Message, StringComparison.Ordinal);
    }

    private static HttpMessageHandler Refused(HttpStatusCode status, params (string Name, string Value)[] headers)
    {
        var refused = new HttpResponseMessage(status);
        foreach (var (name, value) in headers) refused.Headers.Add(name, value);
        return Fixed(refused);
    }

    private static HttpMessageHandler Fixed(HttpResponseMessage response) =>
        new FixedHandler(response);

    private sealed class FixedHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }
}

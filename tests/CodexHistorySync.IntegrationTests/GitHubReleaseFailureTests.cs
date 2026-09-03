using System.Net;
using System.Net.Http.Headers;
using CodexHistorySync.Cli;

namespace CodexHistorySync.IntegrationTests;

/// <summary>
/// Every refusal used to read "The release could not be read from GitHub", which sent the reader
/// to look at a release that was published and intact. These pin the four causes apart.
/// </summary>
public sealed class GitHubReleaseFailureTests
{
    private const string Subject = "The release could not be read from GitHub";

    // 2026-09-03 16:40:24 UTC, with a rate limit that resets at 16:42:19 - the real refusal this
    // was written for, kept as an absolute instant so the wait does not depend on the test clock.
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 16, 40, 24, TimeSpan.Zero);
    private const string ResetsAt164219 = "1788453739";

    [Fact]
    public void AUsedUpRateLimitSaysSoAndSaysHowLongTheWaitIs()
    {
        var headers = Headers(("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset", ResetsAt164219));

        var message = GitHubReleaseSource.DescribeFailure(Subject, HttpStatusCode.Forbidden, headers, Now);

        Assert.Contains("rate limit", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2 minutes from now", message, StringComparison.Ordinal);
        Assert.Contains("60 requests an hour", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AForbiddenWithBudgetLeftIsNotReportedAsARateLimit()
    {
        // 403 is also how GitHub refuses things no amount of waiting will fix. Telling the
        // operator to wait for a limit that is not spent is worse than saying nothing.
        var headers = Headers(("X-RateLimit-Remaining", "57"));

        var message = GitHubReleaseSource.DescribeFailure(Subject, HttpStatusCode.Forbidden, headers, Now);

        Assert.DoesNotContain("rate limit", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("403", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondaryLimitIsARateLimitEvenWithNoBudgetHeaders()
    {
        // A secondary limit answers 429 and carries Retry-After instead of a budget.
        var headers = Headers();
        headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(4));

        var message = GitHubReleaseSource.DescribeFailure(Subject, HttpStatusCode.TooManyRequests, headers, Now);

        Assert.Contains("rate limit", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4 minutes from now", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingReleaseNamesTheOtherThingsThatLookTheSame()
    {
        var message = GitHubReleaseSource.DescribeFailure(Subject, HttpStatusCode.NotFound, Headers(), Now);

        Assert.DoesNotContain("rate limit", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("renamed or made private", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnythingElseCarriesTheStatusGitHubAnswered()
    {
        var message = GitHubReleaseSource.DescribeFailure(Subject, HttpStatusCode.BadGateway, Headers(), Now);

        Assert.Contains("502", message, StringComparison.Ordinal);
        Assert.StartsWith(Subject, message, StringComparison.Ordinal);
    }

    private static HttpResponseHeaders Headers(params (string Name, string Value)[] values)
    {
        using var response = new HttpResponseMessage();
        foreach (var (name, value) in values) response.Headers.Add(name, value);
        return response.Headers;
    }
}

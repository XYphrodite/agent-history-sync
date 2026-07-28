using System.Text.Json;

namespace CodexHistorySync.Git;

public sealed record GitHubVisibilityResult(bool IsPrivate, string Diagnostic);

public sealed class GitHubVisibilityVerifier
{
    private readonly string _ghExecutable;

    public GitHubVisibilityVerifier(string ghExecutable = "gh")
    {
        _ghExecutable = string.IsNullOrWhiteSpace(ghExecutable)
            ? throw new ArgumentException("GitHub CLI executable is required.", nameof(ghExecutable))
            : ghExecutable;
    }

    public async Task<GitHubVisibilityResult> VerifyPrivateAsync(string repository, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repository) || repository.Count(c => c == '/') != 1)
            return new GitHubVisibilityResult(false, "GitHub repository must use the owner/repository form.");

        GitCommandResult result;
        try
        {
            result = await new GitCommand(_ghExecutable).RunAsync(
                ["repo", "view", repository, "--json", "visibility"],
                Environment.CurrentDirectory,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return new GitHubVisibilityResult(false, "GitHub CLI (gh) is unavailable. Install it and authenticate before setup.");
        }

        if (result.ExitCode != 0 || result.TimedOut)
            return new GitHubVisibilityResult(false, "GitHub visibility could not be verified. Run 'gh auth status' and retry setup.");

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (document.RootElement.TryGetProperty("visibility", out var visibility) &&
                visibility.ValueKind == JsonValueKind.String &&
                visibility.GetString() == "PRIVATE")
            {
                return new GitHubVisibilityResult(true, "GitHub repository visibility is PRIVATE.");
            }
        }
        catch (JsonException)
        {
            // The diagnostic below is intentionally actionable without reproducing child-process output.
        }

        return new GitHubVisibilityResult(false, "GitHub repository must have visibility PRIVATE before setup can clone it.");
    }
}

using System.Diagnostics;

namespace CodexHistorySync.IntegrationTests;

public sealed class RepositoryIgnoreTests
{
    [Fact]
    public async Task Generated_sensitive_trees_are_ignored_without_hiding_legitimate_source_or_synthetic_fixtures()
    {
        var root = await GitAsync(Environment.CurrentDirectory, "rev-parse", "--show-toplevel");
        var sensitive = new[]
        {
            "CodexHistorySync/config.json",
            "CodexHistorySync/keys/repository.key",
            "CodexHistorySync/logs/agent.log",
            "CodexHistorySync/repositories/repository/state.json",
            "CodexHistorySync/repositories/repository/git/config",
            "CodexHistorySync/repositories/repository/backups/backup/content.bin",
            "CodexHistorySync/repositories/repository/conflicts/conflict/local.encrypted",
            "CodexHistorySync/repositories/repository/staging/operation/incoming.jsonl",
            ".codex/sessions/2026/01/01/rollout.jsonl",
            ".codex-history-fixtures/copied-rollout.jsonl"
        };
        foreach (var path in sensitive)
            Assert.Equal(0, (await GitResultAsync(root, "check-ignore", "--no-index", "-q", "--", path)).ExitCode);

        var legitimate = new[]
        {
            "src/CodexHistorySync.Cli/Program.cs",
            "tests/CodexHistorySync.IntegrationTests/SecurityBoundaryTests.cs",
            "tests/fixtures/synthetic-session.jsonl"
        };
        foreach (var path in legitimate)
            Assert.Equal(1, (await GitResultAsync(root, "check-ignore", "--no-index", "-q", "--", path)).ExitCode);
        Assert.Equal(0, (await GitResultAsync(root, "ls-files", "--error-unmatch", "--",
            "src/CodexHistorySync.Cli/Program.cs")).ExitCode);
    }

    private static async Task<string> GitAsync(string directory, params string[] arguments)
    {
        var result = await GitResultAsync(directory, arguments);
        Assert.Equal(0, result.ExitCode);
        return result.Output.Trim();
    }

    private static async Task<GitResult> GitResultAsync(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new GitResult(process.ExitCode, await outputTask, await errorTask);
    }

    private sealed record GitResult(int ExitCode, string Output, string Error);
}

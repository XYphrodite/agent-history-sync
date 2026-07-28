using System.Diagnostics;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Providers;
using CodexHistorySync.Git;

namespace CodexHistorySync.Git.Tests;

public sealed class GitStorageProviderTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"chs-git-tests-{Guid.NewGuid():N}");
    private string _remote = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _remote = Path.Combine(_root, "remote.git");
        await GitAsync(_root, "init", "--bare", "--initial-branch=main", _remote);
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
            // The Windows Git executable can create test objects under a different sandbox token.
            // Each fixture is uniquely named below the OS temporary directory and contains no source data.
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TryPublishAsync_RejectsStaleRevisionWithoutMergeOrPartialCommit()
    {
        var source = Path.Combine(_root, "encrypted.chs");
        await File.WriteAllBytesAsync(source, "CHS1-test"u8.ToArray());
        var first = CreateProvider("first");
        var second = CreateProvider("second");
        var revision = (await first.ReadSnapshotAsync(CancellationToken.None)).Revision;
        var id = new LogicalObjectId(new string('a', 64));

        var published = await first.TryPublishAsync(Request(revision, id, source), CancellationToken.None);
        var stale = await second.TryPublishAsync(Request(revision, id, source), CancellationToken.None);

        Assert.True(published.Published);
        Assert.False(stale.Published);
        Assert.Equal(published.CurrentRevision, stale.CurrentRevision);
        Assert.Equal(1, int.Parse((await GitAsync(_root, "--git-dir", _remote, "rev-list", "--count", "main")).Trim()));
        Assert.Empty((await GitAsync(_root, "--git-dir", _remote, "log", "--merges", "--format=%H", "main")).Trim());
    }

    [Fact]
    public async Task GitCommand_RedactsCredentialsFromFailures()
    {
        var result = await new GitCommand("git", TimeSpan.FromSeconds(10)).RunAsync(
            ["ls-remote", "https://alice:secret@example.invalid/repository.git"], _root, CancellationToken.None);

        Assert.DoesNotContain("secret", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alice:", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VisibilityVerifier_RejectsNonPrivateResponse()
    {
        var script = Path.Combine(_root, "gh.cmd");
        await File.WriteAllTextAsync(script, "@echo {\"visibility\":\"PUBLIC\"}\r\n");

        var result = await new GitHubVisibilityVerifier(script).VerifyPrivateAsync("owner/repository", CancellationToken.None);

        Assert.False(result.IsPrivate);
        Assert.Contains("private", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    private GitStorageProvider CreateProvider(string name) => new(
        repositoryId: $"repository-{name}",
        remoteUrl: _remote,
        storageRoot: Path.Combine(_root, "provider-clones"));

    private static PublishRequest Request(string revision, LogicalObjectId id, string source) =>
        new(revision, [new EncryptedObjectChange(id, source, Delete: false)], "publish encrypted object");

    private static async Task<string> GitAsync(string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(stderr);
        return stdout;
    }
}

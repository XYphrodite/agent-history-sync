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

    public async Task DisposeAsync()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(_root)) return;
                foreach (var path in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Reverse())
                    File.SetAttributes(path, FileAttributes.Normal);
                Directory.Delete(_root, recursive: true);
                return;
            }
            catch (Exception) when (attempt < 4)
            {
                await Task.Delay(50 * (attempt + 1));
            }
        }

        Assert.Fail($"Disposable Git fixture leaked: {_root}");
    }

    [Fact]
    public void ProviderContract_ContainsOnlyOpaqueCiphertextNotAuthoritativeObjectVersions()
    {
        Assert.DoesNotContain(typeof(RemoteSnapshot).GetProperties(), property =>
            property.PropertyType.FullName?.Contains("ObjectVersion", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(typeof(EncryptedRemoteObject).GetProperties(), property =>
            property.Name is "Kind" or "PlaintextHash" or "IsDeleted");
        Assert.Contains(typeof(EncryptedRemoteObject).GetProperties(), property => property.Name == "Ciphertext");
    }

    [Fact]
    public async Task ReadSnapshotAsync_MaterializesFetchedRevisionAndReturnsExactCiphertext()
    {
        var id = new LogicalObjectId(new string('a', 64));
        var firstIndex = "CHS1-index-one"u8.ToArray();
        var firstObject = "CHS1-object-one"u8.ToArray();
        var firstRevision = await SeedRemoteAsync(firstIndex, id, firstObject, "first");
        var provider = CreateProvider("reader");

        var first = await provider.ReadSnapshotAsync(CancellationToken.None);

        Assert.Equal(firstRevision, first.Revision);
        Assert.Equal(firstIndex, first.IndexCiphertext);
        Assert.Equal(firstObject, Assert.Single(first.Objects).Ciphertext);

        var secondIndex = "CHS1-index-two"u8.ToArray();
        var secondObject = "CHS1-object-two"u8.ToArray();
        var secondRevision = await SeedRemoteAsync(secondIndex, id, secondObject, "second");
        var second = await provider.ReadSnapshotAsync(CancellationToken.None);

        Assert.Equal(secondRevision, second.Revision);
        Assert.Equal(secondIndex, second.IndexCiphertext);
        Assert.Equal(secondObject, Assert.Single(second.Objects).Ciphertext);
    }

    [Fact]
    public async Task ReadSnapshotAsync_PrunesDeletedRemoteMain()
    {
        var id = new LogicalObjectId(new string('a', 64));
        await SeedRemoteAsync("index"u8.ToArray(), id, "object"u8.ToArray(), "seed");
        var provider = CreateProvider("reader-prune");
        Assert.NotEmpty((await provider.ReadSnapshotAsync(CancellationToken.None)).Revision);
        await GitAsync(_root, "--git-dir", _remote, "update-ref", "-d", "refs/heads/main");

        var empty = await provider.ReadSnapshotAsync(CancellationToken.None);

        Assert.Empty(empty.Revision);
        Assert.Null(empty.IndexCiphertext);
        Assert.Empty(empty.Objects);
    }

    [Fact]
    public async Task TryPublishAsync_RejectsStaleRevisionWithoutMergeOrPartialCommit()
    {
        var first = CreateProvider("first");
        var second = CreateProvider("second");
        var revision = (await first.ReadSnapshotAsync(CancellationToken.None)).Revision;

        var published = await first.TryPublishAsync(await RequestAsync(revision, 'a', "first"), CancellationToken.None);
        var stale = await second.TryPublishAsync(await RequestAsync(revision, 'b', "second"), CancellationToken.None);

        Assert.True(published.Published);
        Assert.False(stale.Published);
        Assert.Equal(published.CurrentRevision, stale.CurrentRevision);
        Assert.Equal(1, int.Parse((await GitAsync(_root, "--git-dir", _remote, "rev-list", "--count", "main")).Trim()));
        Assert.Empty((await GitAsync(_root, "--git-dir", _remote, "log", "--merges", "--format=%H", "main")).Trim());
    }

    [Fact]
    public async Task TryPublishAsync_TruePushRaceReturnsOneWinnerAndOneCleanStaleResult()
    {
        var gate = new TwoPartyPushGate();
        var first = CreateProvider("race-first", publicationHook: gate);
        var second = CreateProvider("race-second", publicationHook: gate);
        var revision = (await first.ReadSnapshotAsync(CancellationToken.None)).Revision;

        var results = await Task.WhenAll(
            first.TryPublishAsync(await RequestAsync(revision, 'a', "first"), CancellationToken.None),
            second.TryPublishAsync(await RequestAsync(revision, 'b', "second"), CancellationToken.None));

        Assert.Single(results, result => result.Published);
        var loser = Assert.Single(results, result => !result.Published);
        Assert.Equal((await CreateProvider("race-observer").ReadSnapshotAsync(CancellationToken.None)).Revision, loser.CurrentRevision);
        Assert.Empty((await GitAsync(_root, "--git-dir", _remote, "log", "--merges", "--format=%H", "main")).Trim());
    }

    [Fact]
    public async Task TryPublishAsync_NoOpRechecksCasAfterPreflight()
    {
        var advancing = CreateProvider("no-op-advancer");
        var initial = (await advancing.ReadSnapshotAsync(CancellationToken.None)).Revision;
        var hook = new DelegatePublicationHook(async ct =>
        {
            var result = await advancing.TryPublishAsync(await RequestAsync(initial, 'a', "advance"), ct);
            Assert.True(result.Published);
        });
        var noOp = CreateProvider("no-op", publicationHook: hook);
        var request = new PublishRequest(initial, Index: null, Changes: [], "no op");

        var result = await noOp.TryPublishAsync(request, CancellationToken.None);

        Assert.False(result.Published);
        Assert.NotEqual(initial, result.CurrentRevision);
    }

    [Fact]
    public async Task InterruptedStaging_IsRemovedBeforeReadAndNextPublication()
    {
        var baselineId = new LogicalObjectId(new string('c', 64));
        var baselineIndex = "CHS1-index-baseline"u8.ToArray();
        var baselineObject = "CHS1-object-baseline"u8.ToArray();
        var initial = await SeedRemoteAsync(baselineIndex, baselineId, baselineObject, "interrupted-baseline");
        var hook = new FailAfterFirstStagingHook();
        var provider = CreateProvider("interrupted", publicationHook: hook);
        Assert.Equal(initial, (await provider.ReadSnapshotAsync(CancellationToken.None)).Revision);
        var abandoned = await RequestAsync(initial, 'a', "abandoned");

        await Assert.ThrowsAsync<InjectedPublicationException>(() =>
            provider.TryPublishAsync(abandoned, CancellationToken.None));

        var afterFailure = await provider.ReadSnapshotAsync(CancellationToken.None);
        Assert.Equal(initial, afterFailure.Revision);
        Assert.Equal(baselineIndex, afterFailure.IndexCiphertext);
        Assert.Equal(baselineObject, Assert.Single(afterFailure.Objects).Ciphertext);

        var replacement = await RequestAsync(initial, 'b', "replacement");
        replacement = replacement with
        {
            Changes = [.. replacement.Changes, new EncryptedObjectChange(baselineId, string.Empty, Delete: true)]
        };
        var published = await provider.TryPublishAsync(replacement, CancellationToken.None);
        var final = await CreateProvider("interrupted-observer").ReadSnapshotAsync(CancellationToken.None);

        Assert.True(published.Published);
        Assert.Equal("CHS1-index-replacement"u8.ToArray(), final.IndexCiphertext);
        var finalObject = Assert.Single(final.Objects);
        Assert.Equal(new string('b', 64), finalObject.ObjectId.Value);
        Assert.Equal("CHS1-object-replacement"u8.ToArray(), finalObject.Ciphertext);
    }

    [Fact]
    public async Task PushAcceptedThenReportedAsError_ReturnsAuthoritativeSuccess()
    {
        var transport = new AcceptThenReportErrorPushTransport();
        var provider = CreateProvider("ambiguous-push", pushTransport: transport);
        var initial = (await provider.ReadSnapshotAsync(CancellationToken.None)).Revision;

        var result = await provider.TryPublishAsync(await RequestAsync(initial, 'a', "accepted"), CancellationToken.None);

        Assert.True(result.Published);
        Assert.Equal(transport.AcceptedRevision, result.CurrentRevision);
        Assert.Equal((await CreateProvider("ambiguous-observer").ReadSnapshotAsync(CancellationToken.None)).Revision, result.CurrentRevision);
    }

    [Fact]
    public async Task PushRejectedWithUnchangedRemote_ThrowsRedactedActionableFailure()
    {
        var provider = CreateProvider("rejected-push", pushTransport: new RejectWithoutRemoteChangePushTransport());
        var initial = (await provider.ReadSnapshotAsync(CancellationToken.None)).Revision;
        var request = await RequestAsync(initial, 'a', "rejected");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.TryPublishAsync(request, CancellationToken.None));

        Assert.Contains("branch policy denied", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("token=***", exception.Message);
        Assert.DoesNotContain("push-secret", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty((await CreateProvider("rejected-observer").ReadSnapshotAsync(CancellationToken.None)).Revision);
    }

    [Fact]
    public async Task GitHubRemote_RequiresExactPrivateVisibilityBeforeClone()
    {
        var verifier = new FakeVisibilityVerifier(new GitHubVisibilityResult(false, "visibility is PUBLIC"));
        var storageRoot = Path.Combine(_root, "github-clones");
        var provider = new GitStorageProvider(
            "github-repository",
            "https://github.com/owner/repository.git",
            GitRemoteKind.GitHub,
            storageRoot,
            visibilityVerifier: verifier);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ReadSnapshotAsync(CancellationToken.None));

        Assert.Contains("PUBLIC", exception.Message);
        Assert.Equal("owner/repository", verifier.Repository);
        Assert.False(Directory.Exists(Path.Combine(storageRoot, "github-repository", "git")));
    }

    [Fact]
    public void GitHubRemote_CannotBypassVisibilityThroughLocalClassification()
    {
        Assert.Throws<ArgumentException>(() => new GitStorageProvider(
            "misclassified-repository",
            "https://github.com/owner/repository.git",
            GitRemoteKind.Local,
            Path.Combine(_root, "misclassified-clones")));
    }

    [Fact]
    public async Task GitCommand_RedactsUrlQueryAndScpCredentialsFromAllOutput()
    {
        var script = Path.Combine(_root, "echo-secrets.ps1");
        await File.WriteAllTextAsync(script,
            "$text = $args -join ' '; [Console]::Out.WriteLine($text); [Console]::Error.WriteLine($text); exit 7\r\n");
        var result = await new GitCommand("powershell.exe", TimeSpan.FromSeconds(10)).RunAsync(
            [
                "-NoProfile",
                "-File",
                script,
                string.Concat("https://", "alice", ":", "secret", "@example.invalid/repo?access_token=query-secret"),
                "https://example.invalid/repo?api_key=api-secret",
                "https://example.invalid/repo?client_secret=client-secret",
                "https://example.invalid/repo?secret=plain-secret",
                "https://example.invalid/repo?sig=signature-secret",
                "https://example.invalid/repo?X-Amz-Signature=aws-secret",
                "https://example.invalid/repo?arbitraryName=arbitrary-secret",
                "https://example.invalid/repo?encoded=value%2Fwith%2Bsecret",
                "https://example.invalid/repo?token=prefix'apostrophe-suffix",
                "https://example.invalid/repo?token=prefix\"quote-suffix",
                "https://example.invalid/repo?token=fragment-secret#public-fragment",
                "https://example.invalid/repo?first=one-secret&second=two-secret",
                "ghp_scp-secret@github.com:owner/repo.git"
            ],
            _root,
            CancellationToken.None);

        Assert.Equal(7, result.ExitCode);
        foreach (var output in new[] { result.StandardOutput, result.StandardError })
        {
            Assert.DoesNotContain("alice:secret", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("query-secret", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("api-secret", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("client-secret", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ghp_scp-secret", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("plain-secret", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("signature-secret", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("aws-secret", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("arbitrary-secret", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("value%2Fwith%2Bsecret", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("apostrophe-suffix", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("quote-suffix", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fragment-secret", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("one-secret", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("two-secret", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ghp_", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("access_token=***", output);
            Assert.Contains("api_key=***", output);
            Assert.Contains("client_secret=***", output);
            Assert.Contains("secret=***", output);
            Assert.Contains("sig=***", output);
            Assert.Contains("X-Amz-Signature=***", output);
            Assert.Contains("arbitraryName=***", output);
            Assert.Contains("encoded=***", output);
            Assert.Contains("#public-fragment", output);
            Assert.Contains("first=***", output);
            Assert.Contains("second=***", output);
        }
    }

    [Fact]
    public async Task GitFailureException_RedactsEveryUrlQueryValue()
    {
        var script = Path.Combine(_root, "git-error.cmd");
        await File.WriteAllTextAsync(script,
            "@echo fatal https://example.invalid/repo?secret=exception-secret^&anything=other-secret 1>&2\r\n" +
            "@echo fatal https://example.invalid/repo?token=prefix'apostrophe-suffix 1>&2\r\n" +
            "@echo fatal https://example.invalid/repo?token=prefix^\"quote-suffix 1>&2\r\n" +
            "@exit /b 7\r\n");
        var provider = new GitStorageProvider(
            "exception-redaction",
            _remote,
            GitRemoteKind.Local,
            Path.Combine(_root, "exception-clones"),
            gitExecutable: script);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ReadSnapshotAsync(CancellationToken.None));

        Assert.DoesNotContain("exception-secret", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("other-secret", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apostrophe-suffix", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quote-suffix", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secret=***", exception.Message);
        Assert.Contains("anything=***", exception.Message);
    }

    [Fact]
    public async Task VisibilityVerifier_AcceptsOnlyExactPrivateResponse()
    {
        var script = Path.Combine(_root, "gh.cmd");
        await File.WriteAllTextAsync(script, "@echo {\"visibility\":\"private\"}\r\n");
        var lower = await new GitHubVisibilityVerifier(script).VerifyPrivateAsync("owner/repository", CancellationToken.None);
        await File.WriteAllTextAsync(script, "@echo {\"visibility\":\"PRIVATE\"}\r\n");
        var exact = await new GitHubVisibilityVerifier(script).VerifyPrivateAsync("owner/repository", CancellationToken.None);

        Assert.False(lower.IsPrivate);
        Assert.True(exact.IsPrivate);
    }

    private GitStorageProvider CreateProvider(
        string name,
        IGitPublicationHook? publicationHook = null,
        IGitPushTransport? pushTransport = null) => new(
        repositoryId: $"repository-{name}",
        remoteUrl: _remote,
        remoteKind: GitRemoteKind.Local,
        storageRoot: Path.Combine(_root, "provider-clones"),
        publicationHook: publicationHook,
        pushTransport: pushTransport);

    private async Task<PublishRequest> RequestAsync(string revision, char idCharacter, string marker)
    {
        var index = Path.Combine(_root, $"{marker}-repository.chs");
        var ciphertext = Path.Combine(_root, $"{marker}-object.chs");
        await File.WriteAllBytesAsync(index, System.Text.Encoding.UTF8.GetBytes($"CHS1-index-{marker}"));
        await File.WriteAllBytesAsync(ciphertext, System.Text.Encoding.UTF8.GetBytes($"CHS1-object-{marker}"));
        return new PublishRequest(
            revision,
            new EncryptedIndexChange(index, Delete: false),
            [new EncryptedObjectChange(new LogicalObjectId(new string(idCharacter, 64)), ciphertext, Delete: false)],
            $"publish {marker}");
    }

    private async Task<string> SeedRemoteAsync(byte[] index, LogicalObjectId id, byte[] ciphertext, string marker)
    {
        var clone = Path.Combine(_root, $"seed-{marker}-{Guid.NewGuid():N}");
        await GitAsync(_root, "clone", _remote, clone);
        await GitAsync(clone, "config", "user.email", "tests@localhost");
        await GitAsync(clone, "config", "user.name", "Tests");
        await File.WriteAllBytesAsync(Path.Combine(clone, "repository.chs"), index);
        var objectDirectory = Path.Combine(clone, "objects", id.Value[..2]);
        Directory.CreateDirectory(objectDirectory);
        await File.WriteAllBytesAsync(Path.Combine(objectDirectory, id.Value[2..] + ".chs"), ciphertext);
        await GitAsync(clone, "add", "repository.chs", "objects");
        await GitAsync(clone, "commit", "-m", marker);
        await GitAsync(clone, "push", "origin", "HEAD:main");
        return (await GitAsync(clone, "rev-parse", "HEAD")).Trim();
    }

    private static async Task<string> GitAsync(string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(stderr);
        return stdout;
    }

    private sealed class FakeVisibilityVerifier(GitHubVisibilityResult result) : IGitHubVisibilityVerifier
    {
        public string? Repository { get; private set; }

        public Task<GitHubVisibilityResult> VerifyPrivateAsync(string repository, CancellationToken cancellationToken)
        {
            Repository = repository;
            return Task.FromResult(result);
        }
    }

    private sealed class DelegatePublicationHook(Func<CancellationToken, Task> action) : IGitPublicationHook
    {
        public Task BeforePushAsync(CancellationToken cancellationToken) => action(cancellationToken);
    }

    private sealed class TwoPartyPushGate : IGitPublicationHook
    {
        private readonly TaskCompletionSource _bothReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public async Task BeforePushAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _count) == 2) _bothReady.TrySetResult();
            await _bothReady.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FailAfterFirstStagingHook : IGitPublicationHook
    {
        private int _fail = 1;

        public Task AfterStagingAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _fail, 0) == 1) throw new InjectedPublicationException();
            return Task.CompletedTask;
        }

        public Task BeforePushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class InjectedPublicationException : Exception;

    private sealed class AcceptThenReportErrorPushTransport : IGitPushTransport
    {
        public string? AcceptedRevision { get; private set; }

        public async Task<GitCommandResult> PushAsync(GitCommand git, string workingDirectory, CancellationToken cancellationToken)
        {
            var accepted = await git.RunAsync(["push", "origin", "HEAD:main"], workingDirectory, cancellationToken);
            Assert.Equal(0, accepted.ExitCode);
            var revision = await git.RunAsync(["rev-parse", "HEAD"], workingDirectory, cancellationToken);
            AcceptedRevision = revision.StandardOutput.Trim();
            return new GitCommandResult(1, string.Empty, "simulated lost push response", TimedOut: false);
        }
    }

    private sealed class RejectWithoutRemoteChangePushTransport : IGitPushTransport
    {
        public Task<GitCommandResult> PushAsync(GitCommand git, string workingDirectory, CancellationToken cancellationToken) =>
            Task.FromResult(new GitCommandResult(
                1,
                string.Empty,
                "branch policy denied https://example.invalid/repository?token=push-secret",
                TimedOut: false));
    }
}

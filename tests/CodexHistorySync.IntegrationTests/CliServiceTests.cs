using System.Security.Cryptography;
using CodexHistorySync.Cli;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.Grok;
using CodexHistorySync.Core.IO;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Providers;
using CodexHistorySync.Core.State;
using CodexHistorySync.Core.Sync;
using CodexHistorySync.Windows;
using System.Reflection;

namespace CodexHistorySync.IntegrationTests;

public sealed class CliServiceTests
{
    private const string Remote = "https://github.com/example/private-history.git";

    [Fact]
    public async Task Initialize_publishes_authenticated_manifest_and_index_before_local_state()
    {
        var log = new List<string>();
        var gateway = new FakeGateway(log);
        var local = new FakeLocalRepository(log);
        var runtime = new FakeRuntime(log);
        var service = new DefaultCliServices(gateway, local, runtime, new RepositoryCrypto());
        var passphrase = "strong-passphrase".ToCharArray();

        var result = await service.InitializeAsync(Remote, passphrase, CancellationToken.None);

        Assert.Equal(["remote.publish", "local.key", "local.config", "local.state"], log);
        Assert.NotNull(gateway.Published);
        Assert.DoesNotContain("strong-passphrase", System.Text.Encoding.UTF8.GetString(gateway.Published!.Manifest));
        var authenticated = await RepositoryManifestAuthenticator.AuthenticateAsync(
            gateway.Published.Manifest, passphrase, new RepositoryCrypto(), CancellationToken.None);
        try
        {
            Assert.Equal(result.RepositoryId, authenticated.Manifest.RepositoryId);
            await RepositoryManifestAuthenticator.AuthenticateIndexAsync(
                gateway.Published.Index, authenticated.Manifest.RepositoryId, authenticated.MasterKey,
                new RepositoryCrypto(), CancellationToken.None);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(authenticated.MasterKey);
        }
    }

    [Fact]
    public async Task Initialize_remote_failure_leaves_no_local_key_config_or_state()
    {
        var log = new List<string>();
        var gateway = new FakeGateway(log) { PublishFailure = new IOException("offline") };
        var local = new FakeLocalRepository(log);
        var service = new DefaultCliServices(gateway, local, new FakeRuntime(log), new RepositoryCrypto());

        await Assert.ThrowsAsync<IOException>(() => service.InitializeAsync(Remote, "secret".ToCharArray(), CancellationToken.None));

        Assert.Equal(["remote.publish"], log);
        Assert.Empty(local.Configurations);
        Assert.Empty(local.Keys);
    }

    [Fact]
    public async Task Initialize_removes_url_credentials_before_remote_or_local_persistence()
    {
        var log = new List<string>();
        var gateway = new FakeGateway(log);
        var local = new FakeLocalRepository(log);
        var service = new DefaultCliServices(gateway, local, new FakeRuntime(log), new RepositoryCrypto());

        await service.InitializeAsync(string.Concat("https://", "user", ":", "token", "@github.com/example/private-history.git"),
            "secret".ToCharArray(), CancellationToken.None);

        Assert.Equal("https://github.com/example/private-history.git", gateway.ObservedRemoteUrl);
        Assert.Equal("https://github.com/example/private-history.git", local.Configurations.Single().RemoteUrl);
    }

    [Fact]
    public async Task Join_wrong_passphrase_or_tampered_index_performs_no_local_writes()
    {
        var setupLog = new List<string>();
        var gateway = new FakeGateway(setupLog);
        var source = new DefaultCliServices(gateway, new FakeLocalRepository(setupLog), new FakeRuntime(setupLog), new RepositoryCrypto());
        await source.InitializeAsync(Remote, "right-passphrase".ToCharArray(), CancellationToken.None);
        gateway.Setup = new CliRemoteSetup(gateway.Published!.Manifest, gateway.Published.Index, "revision-1");

        var wrongLog = new List<string>();
        var wrongLocal = new FakeLocalRepository(wrongLog);
        var wrong = new DefaultCliServices(gateway, wrongLocal, new FakeRuntime(wrongLog), new RepositoryCrypto());
        await Assert.ThrowsAsync<CliGateException>(() => wrong.AuthenticateRepositoryAsync(
            Remote, "wrong-passphrase".ToCharArray(), CancellationToken.None));
        Assert.Empty(wrongLog);

        gateway.Setup.Index[^1] ^= 0x01;
        var tamperedLog = new List<string>();
        var tamperedLocal = new FakeLocalRepository(tamperedLog);
        var tampered = new DefaultCliServices(gateway, tamperedLocal, new FakeRuntime(tamperedLog), new RepositoryCrypto());
        await Assert.ThrowsAsync<CliGateException>(() => tampered.AuthenticateRepositoryAsync(
            Remote, "right-passphrase".ToCharArray(), CancellationToken.None));
        Assert.Empty(tamperedLog);
    }

    [Fact]
    public async Task Join_apply_persists_only_after_authentication_compatibility_and_preview()
    {
        var setupLog = new List<string>();
        var gateway = new FakeGateway(setupLog);
        var source = new DefaultCliServices(gateway, new FakeLocalRepository(setupLog), new FakeRuntime(setupLog), new RepositoryCrypto());
        await source.InitializeAsync(Remote, "right-passphrase".ToCharArray(), CancellationToken.None);
        gateway.Setup = new CliRemoteSetup(gateway.Published!.Manifest, gateway.Published.Index, "revision-1");

        var log = new List<string>();
        var local = new FakeLocalRepository(log);
        var runtime = new FakeRuntime(log);
        var service = new DefaultCliServices(gateway, local, runtime, new RepositoryCrypto());
        var authenticated = await service.AuthenticateRepositoryAsync(Remote, "right-passphrase".ToCharArray(), CancellationToken.None);
        var gate = await service.ProbeCompatibilityAsync(authenticated, CancellationToken.None);
        var plan = await service.PlanJoinAsync(authenticated, CancellationToken.None);

        Assert.True(gate.Passed);
        Assert.Empty(log);
        var applied = await service.ApplyJoinAsync(authenticated, plan, CancellationToken.None);
        Assert.Equal(0, applied.Conflicts);
        Assert.Equal(["local.key", "local.config", "local.state", "runtime.pull", "local.config"], log);
        Assert.Equal("remote.revision", setupLog.Last());
        Assert.Equal("revision-1", local.Configurations.Last().LastSuccessfulRevision);
    }

    [Fact]
    public async Task Join_abort_zeroes_pending_master_key_and_is_idempotent()
    {
        var setupLog = new List<string>();
        var gateway = new FakeGateway(setupLog);
        var source = new DefaultCliServices(gateway, new FakeLocalRepository(setupLog), new FakeRuntime(setupLog), new RepositoryCrypto());
        await source.InitializeAsync(Remote, "right-passphrase".ToCharArray(), CancellationToken.None);
        gateway.Setup = new CliRemoteSetup(gateway.Published!.Manifest, gateway.Published.Index, "revision-1");
        var runtime = new FakeRuntime([]);
        var service = new DefaultCliServices(gateway, new FakeLocalRepository([]), runtime, new RepositoryCrypto());
        var repository = await service.AuthenticateRepositoryAsync(Remote, "right-passphrase".ToCharArray(), CancellationToken.None);
        await service.PlanJoinAsync(repository, CancellationToken.None);
        Assert.Contains(runtime.ObservedJoinKey.Span.ToArray(), value => value != 0);

        await service.AbortJoinAsync(repository, CancellationToken.None);
        await service.AbortJoinAsync(repository, CancellationToken.None);

        Assert.All(runtime.ObservedJoinKey.Span.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task Join_apply_rechecks_pinned_revision_before_any_local_write()
    {
        var setupLog = new List<string>();
        var gateway = new FakeGateway(setupLog);
        var source = new DefaultCliServices(gateway, new FakeLocalRepository(setupLog), new FakeRuntime(setupLog), new RepositoryCrypto());
        await source.InitializeAsync(Remote, "right-passphrase".ToCharArray(), CancellationToken.None);
        gateway.Setup = new CliRemoteSetup(gateway.Published!.Manifest, gateway.Published.Index, "revision-1");
        var log = new List<string>();
        var local = new FakeLocalRepository(log);
        var service = new DefaultCliServices(gateway, local, new FakeRuntime(log), new RepositoryCrypto());
        var repository = await service.AuthenticateRepositoryAsync(Remote, "right-passphrase".ToCharArray(), CancellationToken.None);
        gateway.CurrentRevision = "revision-2";

        await Assert.ThrowsAsync<CliGateException>(() => service.ApplyJoinAsync(repository,
            new CliJoinPlan(0, 0, 0, 0), CancellationToken.None));

        Assert.Empty(local.Configurations);
        Assert.Empty(local.Keys);
    }

    [Fact]
    public async Task Join_apply_failure_zeroes_pending_master_key()
    {
        var setupLog = new List<string>();
        var gateway = new FakeGateway(setupLog);
        var source = new DefaultCliServices(gateway, new FakeLocalRepository(setupLog), new FakeRuntime(setupLog), new RepositoryCrypto());
        await source.InitializeAsync(Remote, "right-passphrase".ToCharArray(), CancellationToken.None);
        gateway.Setup = new CliRemoteSetup(gateway.Published!.Manifest, gateway.Published.Index, "revision-1");
        var runtime = new FakeRuntime([]) { SyncFailure = new OperationCanceledException() };
        var service = new DefaultCliServices(gateway, new FakeLocalRepository([]), runtime, new RepositoryCrypto());
        var repository = await service.AuthenticateRepositoryAsync(Remote, "right-passphrase".ToCharArray(), CancellationToken.None);
        await service.PlanJoinAsync(repository, CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ApplyJoinAsync(repository,
            new CliJoinPlan(0, 0, 0, 0), CancellationToken.None));

        Assert.All(runtime.ObservedJoinKey.Span.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task Compatibility_probe_uses_disposable_fixture_when_live_history_is_empty()
    {
        string? observedFixture = null;
        var runtime = new CoreCliSyncRuntime(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            new FakeGateway([]), new RecordingProcessDetector(), async (fixture, cancellationToken) =>
            {
                observedFixture = fixture;
                var text = await File.ReadAllTextAsync(fixture, cancellationToken);
                // Codex lists a thread only when the file looks like one of its own. Measured
                // against Codex 0.146 and 0.151: a rollout-<timestamp>-<uuid> name and a user
                // message after the metadata are both required, and either one missing is a
                // fixture that no Codex lists - a gate that fails whatever it is asked about.
                Assert.StartsWith("rollout-", Path.GetFileName(fixture), StringComparison.Ordinal);
                Assert.True(Guid.TryParse(Path.GetFileNameWithoutExtension(fixture)[^36..], out _),
                    $"{fixture} does not end in a thread id.");
                Assert.Contains("session_meta", text);
                // cwd, originator and cli_version are each required: without any one of them no
                // Codex we measured lists the thread.
                Assert.Contains("cwd", text, StringComparison.Ordinal);
                Assert.Contains("originator", text, StringComparison.Ordinal);
                Assert.Contains("cli_version", text, StringComparison.Ordinal);
                Assert.Contains("\"type\":\"user_message\"", text);
                return new CompatibilityResult(true, "test", "compatible");
            });

        var result = await runtime.ProbeCompatibilityAsync(CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(observedFixture);
        Assert.False(File.Exists(observedFixture));
    }

    [Fact]
    public async Task Compatibility_probe_does_not_soft_skip_a_missing_configured_executable()
    {
        var runtime = new CoreCliSyncRuntime(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            new FakeGateway([]), new RecordingProcessDetector(), (_, _) => Task.FromResult(
                new CompatibilityResult(false, "unknown",
                    "Codex executable was not found. Install the OpenAI Codex VS Code extension or set CODEX_EXE.")),
            null, null, CodexExecutableSource.Configured);

        var result = await runtime.ProbeCompatibilityAsync(CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal("codex-compatibility", result.Name);
        Assert.StartsWith("Codex executable was not found", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compatibility_probe_remains_a_hard_failure_when_codex_reindex_fails()
    {
        const string diagnostic = "The imported JSONL thread was not listed by Codex.";
        var runtime = new CoreCliSyncRuntime(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            new FakeGateway([]), new RecordingProcessDetector(), (_, _) => Task.FromResult(
                new CompatibilityResult(false, "test", diagnostic)));

        var result = await runtime.ProbeCompatibilityAsync(CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal("codex-compatibility", result.Name);
        Assert.Equal(diagnostic, result.Diagnostic);
    }

    [Fact]
    public async Task Automatically_missing_codex_still_previews_and_imports_existing_grok_home()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-sync-grok-only-" + Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(root, "source");
        var targetRoot = Path.Combine(root, "target");
        var missingCodexHome = Path.Combine(targetRoot, "missing-codex");
        var sourceGrokHome = Path.Combine(sourceRoot, "grok");
        var targetGrokHome = Path.Combine(targetRoot, "grok");
        var key = RandomNumberGenerator.GetBytes(RepositoryCrypto.MasterKeySize);
        const string sessionId = "019fd29d-8f07-7eb3-8fcd-cadaf33d2de6";
        const string cwd = @"C:\Repos\GrokOnly";
        var provider = new TestMemoryProvider();
        var sourceConfiguration = new CliLocalConfiguration(1, "repository-grok-only", "source-device", Remote, string.Empty);
        var targetConfiguration = sourceConfiguration with { DeviceId = "target-device" };

        try
        {
            await WriteGrokSessionAsync(sourceGrokHome, cwd, sessionId, "hello from Grok");
            Directory.CreateDirectory(Path.Combine(targetGrokHome, "sessions"));
            await using (var source = CreateGrokEngine(sourceRoot, Path.Combine(sourceRoot, "missing-codex"),
                             sourceGrokHome, sourceConfiguration, key, provider))
            {
                var published = await source.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
                Assert.Equal(1, published.Uploaded);
            }

            var runtime = new CoreCliSyncRuntime(targetRoot, new FakeGateway([]), new RecordingProcessDetector(),
                (_, _) => Task.FromException<CompatibilityResult>(
                    new InvalidOperationException("Automatic executable absence must not invoke the compatibility process.")),
                (configuration, currentKey) => CreateGrokEngine(targetRoot, missingCodexHome, targetGrokHome,
                    configuration, currentKey, provider), null, CodexExecutableSource.AutomaticDiscoveryAbsent,
                missingCodexHome, targetGrokHome);

            var gate = await runtime.ProbeCompatibilityAsync(CancellationToken.None);
            Assert.True(gate.Passed);
            var preview = await runtime.PreviewJoinAsync(targetConfiguration, key,
                new CliRemoteSetup([], [], "1"), CancellationToken.None);
            Assert.Equal(1, preview.Remote);
            Assert.Equal(1, preview.Pending);

            var pulled = await runtime.SynchronizeAsync(targetConfiguration, key, SyncMode.Pull, CancellationToken.None);

            Assert.Equal(1, pulled.Downloaded);
            Assert.False(Directory.Exists(missingCodexHome));
            var imported = Path.Combine(targetGrokHome, "sessions", GrokPaths.EncodeCwdSegment(cwd), sessionId,
                "chat_history.jsonl");
            Assert.Contains("hello from Grok", await File.ReadAllTextAsync(imported), StringComparison.Ordinal);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Real_runtime_disposes_temporary_engine_and_zeroes_only_its_key_copy()
    {
        var root = Path.Combine(Path.GetTempPath(), "codex-history-runtime-disposal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var callerKey = RandomNumberGenerator.GetBytes(RepositoryCrypto.MasterKeySize);
        SyncEngine? created = null;
        try
        {
            var runtime = new CoreCliSyncRuntime(root, new FakeGateway([]), new RecordingProcessDetector(),
                (_, _) => Task.FromResult(new CompatibilityResult(true, "test", "compatible")),
                (configuration, key) => created = CreateEngine(root, configuration, key));
            var configuration = new CliLocalConfiguration(1, "repository-123", "device-123", Remote, "old-revision");

            await runtime.SynchronizeAsync(configuration, callerKey, SyncMode.Pull, CancellationToken.None);

            Assert.NotNull(created);
            var engineKey = Assert.IsType<byte[]>(typeof(SyncEngine)
                .GetField("_masterKey", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(created));
            Assert.All(engineKey, value => Assert.Equal(0, value));
            Assert.Contains(callerKey, value => value != 0);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                created.PreviewAsync(SyncMode.Pull, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            CryptographicOperations.ZeroMemory(callerKey);
        }
    }

    [Fact]
    public async Task Status_counts_exact_union_of_persisted_and_planned_conflict_identities()
    {
        var root = Path.Combine(Path.GetTempPath(), "codex-history-status-union-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var key = RandomNumberGenerator.GetBytes(RepositoryCrypto.MasterKeySize);
        var provider = new TestMemoryProvider();
        var configuration = new CliLocalConfiguration(1, "repository-123", "target-device", Remote, "last-0");
        try
        {
            var sourceRoot = Path.Combine(root, "source");
            var sourceConfiguration = configuration with { DeviceId = "source-device" };
            await WriteSessionAsync(Path.Combine(sourceRoot, "codex", "sessions"), "planned-b", "remote");
            await using (var source = CreateEngine(sourceRoot, sourceConfiguration, key, provider))
                await source.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
            await WriteSessionAsync(Path.Combine(root, "codex", "sessions"), "planned-b", "local");
            var targetPaths = CodexPaths.Resolve(Path.Combine(root, "codex"));
            var conflictStore = new ConflictStore(configuration.RepositoryId, root, targetPaths);
            var crypto = new RepositoryCrypto();
            var metadata = new EnvelopeMetadata(1, new LogicalObjectId("persisted-a"), ObjectKind.ActiveSession);
            var localBytes = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"session_meta\",\"payload\":{\"id\":\"persisted-a\"}}\n");
            await using var encryptedLocal = new MemoryStream();
            await using var encryptedRemote = new MemoryStream();
            await crypto.EncryptAsync(new MemoryStream(localBytes), encryptedLocal, key, metadata, CancellationToken.None);
            await crypto.EncryptAsync(new MemoryStream(localBytes), encryptedRemote, key, metadata, CancellationToken.None);
            await conflictStore.PreserveAsync(new ConflictProvenance(metadata, new ContentHash("local-a"),
                    new ContentHash("remote-a"), new ContentHash("baseline-a"), "target-device", "source-device",
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                new MemoryStream(encryptedLocal.ToArray()), new MemoryStream(encryptedRemote.ToArray()),
                CancellationToken.None);
            var runtime = new CoreCliSyncRuntime(root, new FakeGateway([]), new RecordingProcessDetector(),
                (_, _) => Task.FromResult(new CompatibilityResult(true, "test", "compatible")),
                (current, currentKey) => CreateEngine(root, current, currentKey, provider));

            var status = await runtime.GetStatusAsync(configuration, key, CancellationToken.None);

            Assert.Equal(2, status.Conflicts);
            Assert.Equal("1", status.RemoteRevision);
            Assert.Equal("last-0", status.LastSuccessfulRevision);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    [Fact]
    public async Task Successful_manual_sync_updates_the_last_successful_revision()
    {
        var log = new List<string>();
        var local = new FakeLocalRepository(log);
        local.Configurations.Add(new CliLocalConfiguration(1, "repository-123", "device-123", Remote, "old-revision"));
        local.Keys.Add(RandomNumberGenerator.GetBytes(RepositoryCrypto.MasterKeySize));
        var service = new DefaultCliServices(new FakeGateway(log), local, new FakeRuntime(log), new RepositoryCrypto());

        await service.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);

        Assert.Equal("revision-1", local.Configurations.Last().LastSuccessfulRevision);
    }

    [Fact]
    public async Task Doctor_actually_queries_the_Codex_process_detector()
    {
        var detector = new RecordingProcessDetector();
        var runtime = new CoreCliSyncRuntime(Path.GetTempPath(), new FakeGateway([]), detector);

        var report = await runtime.RunDoctorAsync(null, ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.True(detector.WasChecked);
        Assert.Contains(report.Checks, check => check.Name == "process-state");
    }

    [Fact]
    public async Task Doctor_still_reports_on_a_machine_that_never_joined()
    {
        // Its environment checks - git, gh, Codex, disk - are exactly what someone needs before
        // the first join, so a missing configuration must not end the command.
        var log = new List<string>();
        var service = new DefaultCliServices(new FakeGateway(log), new NeverJoinedLocalRepository(),
            new FakeRuntime(log), new RepositoryCrypto());

        var report = await service.RunDoctorAsync(CancellationToken.None);

        Assert.NotNull(report);
    }

    [Fact]
    public async Task A_failing_gh_command_carries_what_gh_said_into_the_failure()
    {
        // A bare 'Unable to read the repository revision.' fits a missing branch, an expired
        // token and a dead network equally, and those are three different things to go and do.
        var root = Path.Combine(Path.GetTempPath(), $"agent-sync-gh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var script = Path.Combine(root, "gh-fails.cmd");
            await File.WriteAllTextAsync(script,
                "@echo gh: Not Found (HTTP 404) 1>&2" + Environment.NewLine + "@exit /b 1" + Environment.NewLine);
            var gateway = new GitHubCliRepositoryGateway(ghExecutable: script);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => gateway.ReadCurrentRevisionAsync("https://github.com/owner/repository.git", CancellationToken.None));

            Assert.Contains("Unable to read the repository revision.", exception.Message, StringComparison.Ordinal);
            Assert.Contains("HTTP 404", exception.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    private sealed class NeverJoinedLocalRepository : ICliLocalRepository
    {
        public Task SaveKeyAsync(string repositoryId, ReadOnlyMemory<byte> key, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task<byte[]?> LoadKeyAsync(string repositoryId, CancellationToken cancellationToken) =>
            Task.FromResult<byte[]?>(null);
        public Task SaveConfigurationAsync(CliLocalConfiguration configuration, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task<CliLocalConfiguration> LoadConfigurationAsync(CancellationToken cancellationToken) =>
            throw new CliNotJoinedException();
        public Task SaveInitialStateAsync(string repositoryId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    [Fact]
    public async Task A_local_repository_without_a_configuration_says_the_machine_never_joined()
    {
        // The file is absent because join never ran here, and the caller can act on that.
        var root = Path.Combine(Path.GetTempPath(), $"agent-sync-unjoined-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var repository = new FileCliLocalRepository(root, new UnusedKeyStore());

            await Assert.ThrowsAsync<CliNotJoinedException>(
                () => repository.LoadConfigurationAsync(CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    private sealed class UnusedKeyStore : CodexHistorySync.Windows.IKeyStore
    {
        public Task SaveAsync(string repositoryId, ReadOnlyMemory<byte> key, CancellationToken ct) =>
            throw new InvalidOperationException("The key store must not be reached.");
        public Task<byte[]?> LoadAsync(string repositoryId, CancellationToken ct) =>
            throw new InvalidOperationException("The key store must not be reached.");
        public Task DeleteAsync(string repositoryId, CancellationToken ct) =>
            throw new InvalidOperationException("The key store must not be reached.");
    }
    private sealed class FakeGateway(List<string> log) : ICliRepositoryGateway
    {
        public CliPublishedInitialization? Published { get; private set; }
        public CliRemoteSetup? Setup { get; set; }
        public Exception? PublishFailure { get; set; }
        public string? ObservedRemoteUrl { get; private set; }
        public string CurrentRevision { get; set; } = "revision-1";

        public Task<CliGateResult> VerifyPrivateAsync(string remoteUrl, CancellationToken cancellationToken) =>
            Task.FromResult(new CliGateResult(true, "private-visibility"));

        public Task<CliGateResult> VerifyInitializationTargetAsync(string remoteUrl, CancellationToken cancellationToken) =>
            Task.FromResult(new CliGateResult(true, "empty-private-repository"));

        public Task<CliPublishedInitialization> PublishInitializationAsync(string remoteUrl, string repositoryId,
            byte[] manifest, byte[] encryptedIndex, CancellationToken cancellationToken)
        {
            log.Add("remote.publish");
            ObservedRemoteUrl = remoteUrl;
            if (PublishFailure is not null) return Task.FromException<CliPublishedInitialization>(PublishFailure);
            Published = new CliPublishedInitialization(manifest.ToArray(), encryptedIndex.ToArray(), "revision-1");
            Setup = new CliRemoteSetup(Published.Manifest, Published.Index, Published.Revision);
            return Task.FromResult(Published);
        }

        public Task<CliRemoteSetup> ReadSetupAsync(string remoteUrl, CancellationToken cancellationToken) =>
            Task.FromResult(Setup ?? throw new InvalidOperationException("No setup snapshot."));

        public Task<string> ReadCurrentRevisionAsync(string remoteUrl, CancellationToken cancellationToken)
        {
            log.Add("remote.revision");
            return Task.FromResult(CurrentRevision);
        }
    }

    private sealed class FakeLocalRepository(List<string> log) : ICliLocalRepository
    {
        public List<CliLocalConfiguration> Configurations { get; } = [];
        public List<byte[]> Keys { get; } = [];

        public Task SaveKeyAsync(string repositoryId, ReadOnlyMemory<byte> key, CancellationToken cancellationToken)
        {
            log.Add("local.key");
            Keys.Add(key.ToArray());
            return Task.CompletedTask;
        }

        public Task<byte[]?> LoadKeyAsync(string repositoryId, CancellationToken cancellationToken) =>
            Task.FromResult<byte[]?>(Keys.LastOrDefault()?.ToArray());

        public Task SaveConfigurationAsync(CliLocalConfiguration configuration, CancellationToken cancellationToken)
        {
            log.Add("local.config");
            Configurations.Add(configuration);
            return Task.CompletedTask;
        }

        public Task<CliLocalConfiguration> LoadConfigurationAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Configurations.Last());

        public Task SaveInitialStateAsync(string repositoryId, CancellationToken cancellationToken)
        {
            log.Add("local.state");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRuntime(List<string> log) : ICliSyncRuntime
    {
        public ReadOnlyMemory<byte> ObservedJoinKey { get; private set; }
        public Exception? SyncFailure { get; set; }
        public Task<CliGateResult> ProbeCompatibilityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CliGateResult(true, "codex-compatibility"));

        public Task<CliJoinPlan> PreviewJoinAsync(CliLocalConfiguration configuration, ReadOnlyMemory<byte> key,
            CliRemoteSetup setup, CancellationToken cancellationToken)
        {
            ObservedJoinKey = key;
            return Task.FromResult(new CliJoinPlan(0, 0, 0, 0));
        }

        public Task<SyncResult> SynchronizeAsync(CliLocalConfiguration configuration, ReadOnlyMemory<byte> key,
            SyncMode mode, CancellationToken cancellationToken)
        {
            log.Add(mode == SyncMode.Pull ? "runtime.pull" : "runtime.sync");
            return SyncFailure is null
                ? Task.FromResult(new SyncResult("revision-1", 0, 0, 0, 0, false))
                : Task.FromException<SyncResult>(SyncFailure);
        }

        public Task<CliStatusReport> GetStatusAsync(CliLocalConfiguration configuration, ReadOnlyMemory<byte> key,
            CancellationToken cancellationToken) => Task.FromResult(new CliStatusReport(0, 0, 0, 0,
                "revision-1", configuration.LastSuccessfulRevision));

        public Task<CliDoctorReport> RunDoctorAsync(CliLocalConfiguration? configuration, ReadOnlyMemory<byte> key,
            CancellationToken cancellationToken) => Task.FromResult(new CliDoctorReport([]));

        public Task<IReadOnlyList<CliConflictInfo>> ListConflictsAsync(CliLocalConfiguration configuration,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CliConflictInfo>>([]);

        public Task<CliResolutionResult> ResolveAsync(CliLocalConfiguration configuration, ReadOnlyMemory<byte> key, string conflictId,
            CliResolution resolution, string? exportDirectory, CancellationToken cancellationToken) =>
            Task.FromResult(new CliResolutionResult(0, resolution == CliResolution.ExportBoth));
    }

    private sealed class RecordingProcessDetector : ICodexProcessDetector
    {
        public bool WasChecked { get; private set; }
        public bool IsRunning() { WasChecked = true; return false; }
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static SyncEngine CreateEngine(string root, CliLocalConfiguration configuration, ReadOnlyMemory<byte> key,
        IStorageProvider? provider = null)
    {
        var home = Path.Combine(root, "codex");
        Directory.CreateDirectory(home);
        var paths = CodexPaths.Resolve(home);
        Directory.CreateDirectory(paths.Sessions);
        var state = new LocalStateStore(root);
        var backups = new BackupStore(configuration.RepositoryId, root, paths);
        var conflicts = new ConflictStore(configuration.RepositoryId, root, paths);
        var writer = new CodexHistoryWriter(paths, backups, new RecordingProcessDetector());
        return new SyncEngine(configuration.RepositoryId, configuration.DeviceId, paths, key,
            new SessionScanner(TimeSpan.Zero), new RepositoryCrypto(), state, writer, conflicts,
            provider ?? new EmptyProvider(), Path.Combine(root, "staging"));
    }

    private static SyncEngine CreateGrokEngine(string root, string codexHome, string grokHome,
        CliLocalConfiguration configuration, ReadOnlyMemory<byte> key, IStorageProvider provider)
    {
        var paths = new CodexPaths(Path.GetFullPath(codexHome), Path.GetFullPath(Path.Combine(codexHome, "sessions")),
            Path.GetFullPath(Path.Combine(codexHome, "archived_sessions")),
            Path.GetFullPath(Path.Combine(codexHome, "attachments")));
        var grokPaths = GrokPaths.TryResolve(grokHome) ?? throw new InvalidOperationException("Grok fixture is unavailable.");
        var state = new LocalStateStore(root);
        var backups = new BackupStore(configuration.RepositoryId, root, paths, grokPaths: grokPaths);
        var conflicts = new ConflictStore(configuration.RepositoryId, root, paths);
        var writer = new CodexHistoryWriter(paths, backups, new RecordingProcessDetector(), grokPaths: grokPaths);
        return new SyncEngine(configuration.RepositoryId, configuration.DeviceId, paths, key,
            new SessionScanner(TimeSpan.Zero), new RepositoryCrypto(), state, writer, conflicts, provider,
            Path.Combine(root, "staging"), grokPaths, new GrokSessionScanner(TimeSpan.Zero));
    }

    private static async Task WriteGrokSessionAsync(string grokHome, string cwd, string sessionId, string message)
    {
        var directory = Path.Combine(grokHome, "sessions", GrokPaths.EncodeCwdSegment(cwd), sessionId);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "chat_history.jsonl"),
            $"{{\"type\":\"user\",\"content\":\"{message}\"}}\n{{\"type\":\"assistant\",\"content\":\"received\"}}\n",
            new System.Text.UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"),
            System.Text.Json.JsonSerializer.Serialize(new { info = new { id = sessionId, cwd } }),
            new System.Text.UTF8Encoding(false));
    }

    private static async Task WriteSessionAsync(string directory, string id, string side)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, id + ".jsonl"),
            $"{{\"type\":\"session_meta\",\"payload\":{{\"id\":\"{id}\"}}}}\n" +
            $"{{\"type\":\"message\",\"payload\":{{\"text\":\"{side}\"}}}}\n");
    }

    private sealed class EmptyProvider : IStorageProvider
    {
        public Task<RemoteSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteSnapshot(string.Empty, null, []));
        public Task<PublishResult> TryPublishAsync(PublishRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No publication expected.");
    }

    private sealed class TestMemoryProvider : IStorageProvider
    {
        private readonly Dictionary<LogicalObjectId, byte[]> objects = [];
        private byte[]? index;
        private int revision;

        public Task<RemoteSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(
            new RemoteSnapshot(revision == 0 ? string.Empty : revision.ToString(), index?.ToArray(),
                objects.Select(pair => new EncryptedRemoteObject(pair.Key, pair.Value.ToArray())).ToArray()));

        public async Task<PublishResult> TryPublishAsync(PublishRequest request, CancellationToken cancellationToken)
        {
            var expected = revision == 0 ? string.Empty : revision.ToString();
            if (!StringComparer.Ordinal.Equals(expected, request.ExpectedRevision)) return new(false, expected);
            if (request.Index is { Delete: false } changedIndex)
                index = await File.ReadAllBytesAsync(changedIndex.CiphertextPath, cancellationToken);
            foreach (var change in request.Changes)
            {
                if (change.Delete) objects.Remove(change.ObjectId);
                else objects[change.ObjectId] = await File.ReadAllBytesAsync(change.CiphertextPath, cancellationToken);
            }
            revision++;
            return new(true, revision.ToString());
        }
    }
}

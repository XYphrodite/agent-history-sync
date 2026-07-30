using System.Security.Cryptography;
using CodexHistorySync.Cli;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.Sync;

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

        await service.InitializeAsync("https://user:token@github.com/example/private-history.git",
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
                Assert.Contains("session_meta", text);
                Assert.Contains("compatibility-fixture", text);
                return new CompatibilityResult(true, "test", "compatible");
            });

        var result = await runtime.ProbeCompatibilityAsync(CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(observedFixture);
        Assert.False(File.Exists(observedFixture));
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
            CancellationToken cancellationToken) => Task.FromResult(new CliStatusReport(0, 0, 0, 0, "revision-1"));

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
}

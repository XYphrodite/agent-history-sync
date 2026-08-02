using CodexHistorySync.Cli;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Sync;
using CodexHistorySync.Windows;

namespace CodexHistorySync.IntegrationTests;

public sealed class AgentWorkerTests
{
    [Fact]
    public async Task Logon_while_stopped_runs_bidirectional_sync()
    {
        using var cancellation = new CancellationTokenSource();
        var sync = new FakeSync { OnSynchronize = _ => cancellation.Cancel() };
        var worker = CreateWorker(new FakeDetector(false), sync, new FakeClock(), new FakeNotifier(), new FakeLogger());

        await worker.RunAsync(cancellation.Token);

        Assert.Equal([SyncMode.Bidirectional], sync.Modes);
    }

    [Fact]
    public async Task Active_Codex_runs_export_only_and_authenticated_preview_without_import()
    {
        using var cancellation = new CancellationTokenSource();
        var sync = new FakeSync { OnStatus = () => cancellation.Cancel() };
        var worker = CreateWorker(new FakeDetector(true), sync, new FakeClock(), new FakeNotifier(), new FakeLogger());

        await worker.RunAsync(cancellation.Token);

        Assert.Equal([SyncMode.Push], sync.Modes);
        Assert.Equal(1, sync.StatusCalls);
    }

    [Fact]
    public async Task Active_to_stopped_waits_for_exit_and_quiescence_before_bidirectional_sync()
    {
        using var cancellation = new CancellationTokenSource();
        var detector = new FakeDetector(true) { ExitWhenWaited = true };
        var clock = new FakeClock();
        var sync = new FakeSync { OnSynchronize = mode => { if (mode == SyncMode.Bidirectional) cancellation.Cancel(); } };
        var worker = CreateWorker(detector, sync, clock, new FakeNotifier(), new FakeLogger());

        await worker.RunAsync(cancellation.Token);

        Assert.Equal([SyncMode.Push, SyncMode.Bidirectional], sync.Modes);
        Assert.Equal(TimeSpan.FromSeconds(2), TimeSpan.FromTicks(clock.Delays.Where(delay => delay < TimeSpan.FromSeconds(10)).Sum(delay => delay.Ticks)));
        Assert.True(detector.EnumerationCount >= 9);
    }

    [Fact]
    public async Task Incoming_changes_during_activity_notify_once_until_import_can_run()
    {
        using var cancellation = new CancellationTokenSource();
        var detector = new FakeDetector(true);
        var clock = new FakeClock { CompletePeriodicDelays = true };
        var sync = new FakeSync
        {
            Status = new CliStatusReport(1, 2, 1, 0, "revision-2", "revision-1"),
            OnStatus = () => { if (syncPlaceholder!.StatusCalls == 2) cancellation.Cancel(); }
        };
        syncPlaceholder = sync;
        var notifier = new FakeNotifier();
        var worker = CreateWorker(detector, sync, clock, notifier, new FakeLogger());

        await worker.RunAsync(cancellation.Token);

        Assert.Equal(2, sync.Modes.Count);
        Assert.All(sync.Modes, mode => Assert.Equal(SyncMode.Push, mode));
        Assert.Equal(1, notifier.Notifications.Count(item => item.Kind == AgentNotificationKind.PendingRestart));
    }

    [Fact]
    public async Task Late_Codex_activity_is_deferred_without_failure_or_backoff_then_waits_for_quiescence()
    {
        using var cancellation = new CancellationTokenSource();
        var detector = new FakeDetector(false) { ExitWhenWaited = true };
        var attempts = 0;
        var sync = new FakeSync
        {
            Synchronize = _ =>
            {
                attempts++;
                if (attempts <= 2)
                {
                    detector.Start();
                    throw new CodexBecameActiveException();
                }
                cancellation.Cancel();
                return Result();
            }
        };
        var clock = new FakeClock { CompleteBackoffDelays = true };
        var notifier = new FakeNotifier();
        var logger = new FakeLogger();
        var worker = CreateWorker(detector, sync, clock, notifier, logger);

        await worker.RunAsync(cancellation.Token);

        Assert.Equal([SyncMode.Bidirectional, SyncMode.Bidirectional, SyncMode.Bidirectional], sync.Modes);
        Assert.Equal(2, detector.WaitCalls);
        Assert.Equal(1, notifier.Notifications.Count(item => item.Kind == AgentNotificationKind.PendingRestart));
        Assert.DoesNotContain(notifier.Notifications, item => item.Kind == AgentNotificationKind.RepeatedFailure);
        Assert.DoesNotContain(logger.Entries, item => item.Kind == AgentLogKind.Failure);
        Assert.DoesNotContain(clock.Delays, delay => delay >= TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(4), TimeSpan.FromTicks(
            clock.Delays.Where(delay => delay < TimeSpan.FromSeconds(10)).Sum(delay => delay.Ticks)));
    }

    [Fact]
    public async Task Unresolved_conflicts_generate_one_notification_until_a_clear_cycle()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var sync = new FakeSync
        {
            Synchronize = _ =>
            {
                calls++;
                if (calls == 2) cancellation.Cancel();
                return Result(conflicts: 2);
            }
        };
        var clock = new FakeClock { CompletePeriodicDelays = true };
        var notifier = new FakeNotifier();
        var worker = CreateWorker(new FakeDetector(false), sync, clock, notifier, new FakeLogger());

        await worker.RunAsync(cancellation.Token);

        Assert.Equal(1, notifier.Notifications.Count(item => item.Kind == AgentNotificationKind.UnresolvedConflict));
    }

    private FakeSync? syncPlaceholder;

    [Fact]
    public async Task Failures_back_off_exponentially_to_thirty_minute_cap_and_recovery_notifies()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        var sync = new FakeSync
        {
            Synchronize = mode =>
            {
                attempts++;
                if (attempts <= 8) throw new IOException("secret path and URL must not be logged");
                return Result();
            }
        };
        var clock = new FakeClock
        {
            CompleteBackoffDelays = true,
            OnDelay = delay => { if (delay == TimeSpan.FromSeconds(10)) cancellation.Cancel(); }
        };
        var notifier = new FakeNotifier();
        var logger = new FakeLogger();
        var worker = CreateWorker(new FakeDetector(false), sync, clock, notifier, logger);

        await worker.RunAsync(cancellation.Token);

        Assert.Equal([
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4),
            TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(16), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30)
        ], clock.Delays.Where(delay => delay >= TimeSpan.FromSeconds(30)).ToArray());
        Assert.Equal(1, notifier.Notifications.Count(item => item.Kind == AgentNotificationKind.RepeatedFailure));
        Assert.Equal(1, notifier.Notifications.Count(item => item.Kind == AgentNotificationKind.Recovered));
        Assert.DoesNotContain(logger.Entries, entry => entry.ErrorCode.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Cancellation_during_wait_is_normal_shutdown_without_another_cycle_or_failure_notice()
    {
        using var cancellation = new CancellationTokenSource();
        var sync = new FakeSync();
        var clock = new FakeClock { OnDelay = _ => cancellation.Cancel() };
        var notifier = new FakeNotifier();
        var logger = new FakeLogger();
        var worker = CreateWorker(new FakeDetector(false), sync, clock, notifier, logger);

        await worker.RunAsync(cancellation.Token);

        Assert.Single(sync.Modes);
        Assert.DoesNotContain(notifier.Notifications, item => item.Kind == AgentNotificationKind.RepeatedFailure);
        Assert.DoesNotContain(logger.Entries, item => item.Kind == AgentLogKind.Failure);
    }

    [Fact]
    public async Task Cancellation_during_failure_handling_returns_without_backoff_or_another_cycle()
    {
        using var cancellation = new CancellationTokenSource();
        var sync = new FakeSync { Synchronize = _ => throw new IOException("offline") };
        var clock = new FakeClock { CompleteBackoffDelays = true };
        var logger = new CancellingLogger(cancellation);
        var worker = CreateWorker(new FakeDetector(false), sync, clock, new FakeNotifier(), logger);

        await worker.RunAsync(cancellation.Token);

        Assert.Single(sync.Modes);
        Assert.Empty(clock.Delays);
    }

    [Fact]
    public async Task Rotating_log_rejects_free_form_fields_and_retains_at_most_five_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "codex-agent-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var logger = new RotatingAgentLogger(root, maximumBytes: 180, retainedFiles: 5);
            var unsafeEntry = new AgentLogEntry(AgentLogKind.Failure, Guid.NewGuid(), SyncMode.Push,
                0, 0, 0, 0, 1, "revision/../../secret", "C:\\Users\\name\\history.jsonl https://user:token@example.test", 12);
            for (var index = 0; index < 30; index++) await logger.WriteAsync(unsafeEntry, CancellationToken.None);

            var files = Directory.GetFiles(root, "agent*.log");
            Assert.InRange(files.Length, 1, 5);
            var combined = string.Join("", files.Select(File.ReadAllText));
            Assert.DoesNotContain("Users", combined, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("example.test", combined, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", combined, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("..", combined, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("run")]
    [InlineData("install")]
    [InlineData("uninstall")]
    public async Task Cli_routes_agent_subcommands_without_exposing_agent_in_manual_services(string command)
    {
        var operations = new FakeAgentCliOperations();
        var app = new CliApplication(new NoopCliServices(), new FakeConsole(), operations);

        var exitCode = await app.RunAsync(["agent", command], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(command, Assert.Single(operations.Calls));
    }

    [Fact]
    public async Task Doctor_uses_injected_task_scheduler_installation_check()
    {
        var checker = new FakeInstallationChecker(true);
        var runtime = new CoreCliSyncRuntime(Path.GetTempPath(), new UnusedGateway(), new FakeDetector(false),
            (_, _) => Task.FromResult(new CodexHistorySync.Core.Codex.CompatibilityResult(true, "test", "compatible")),
            null, checker);

        var report = await runtime.RunDoctorAsync(null, ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.True(checker.WasChecked);
        Assert.Contains(report.Checks, check => check.Name == "agent-installation" && check.Passed);
    }

    private static AgentWorker CreateWorker(ICodexProcessDetector detector, IAgentSyncOperations sync,
        IAgentClock clock, IAgentNotifier notifier, IAgentLogger logger) =>
        new(detector, sync, clock, notifier, logger, new AgentWorkerOptions(
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30), 3));

    private static SyncResult Result(int conflicts = 0) => new("revision-1", 1, 0, 0, conflicts, false);

    private sealed class FakeDetector(bool active) : ICodexProcessDetector
    {
        public bool Active { get; private set; } = active;
        public bool ExitWhenWaited { get; set; }
        public int EnumerationCount { get; private set; }
        public int WaitCalls { get; private set; }

        public void Start() => Active = true;

        public bool IsRunning()
        {
            EnumerationCount++;
            return Active;
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitCalls++;
            if (ExitWhenWaited)
            {
                Active = false;
                return Task.CompletedTask;
            }
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FakeSync : IAgentSyncOperations
    {
        public List<SyncMode> Modes { get; } = [];
        public int StatusCalls { get; private set; }
        public Action<SyncMode>? OnSynchronize { get; set; }
        public Action? OnStatus { get; set; }
        public Func<SyncMode, SyncResult>? Synchronize { get; set; }
        public CliStatusReport Status { get; set; } = new(0, 0, 0, 0, "revision-1", "revision-1");

        public Task<SyncResult> SynchronizeAsync(SyncMode mode, CancellationToken cancellationToken)
        {
            Modes.Add(mode);
            var result = Synchronize?.Invoke(mode) ?? Result();
            OnSynchronize?.Invoke(mode);
            return Task.FromResult(result);
        }

        public Task<CliStatusReport> GetStatusAsync(CancellationToken cancellationToken)
        {
            StatusCalls++;
            OnStatus?.Invoke();
            return Task.FromResult(Status);
        }
    }

    private sealed class FakeClock : IAgentClock
    {
        public List<TimeSpan> Delays { get; } = [];
        public bool CompletePeriodicDelays { get; set; }
        public bool CompleteBackoffDelays { get; set; }
        public Action<TimeSpan>? OnDelay { get; set; }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            OnDelay?.Invoke(delay);
            if (delay <= TimeSpan.FromSeconds(2) ||
                CompletePeriodicDelays && delay == TimeSpan.FromSeconds(10) ||
                CompleteBackoffDelays && delay >= TimeSpan.FromSeconds(30)) return Task.CompletedTask;
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FakeNotifier : IAgentNotifier
    {
        public List<AgentNotification> Notifications { get; } = [];
        public Task NotifyAsync(AgentNotification notification, CancellationToken cancellationToken)
        {
            Notifications.Add(notification);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLogger : IAgentLogger
    {
        public List<AgentLogEntry> Entries { get; } = [];
        public Task WriteAsync(AgentLogEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class CancellingLogger(CancellationTokenSource cancellation) : IAgentLogger
    {
        public Task WriteAsync(AgentLogEntry entry, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromCanceled(cancellationToken);
        }
    }

    private sealed class FakeInstallationChecker(bool installed) : IAgentInstallationChecker
    {
        public bool WasChecked { get; private set; }
        public Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default)
        {
            WasChecked = true;
            return Task.FromResult(installed);
        }
    }

    private sealed class UnusedGateway : ICliRepositoryGateway
    {
        public Task<CliGateResult> VerifyInitializationTargetAsync(string remoteUrl, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CliGateResult> VerifyPrivateAsync(string remoteUrl, CancellationToken cancellationToken) => Task.FromResult(new CliGateResult(false, "private"));
        public Task<CliPublishedInitialization> PublishInitializationAsync(string remoteUrl, string repositoryId, byte[] manifest, byte[] encryptedIndex, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CliRemoteSetup> ReadSetupAsync(string remoteUrl, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> ReadCurrentRevisionAsync(string remoteUrl, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeAgentCliOperations : IAgentCliOperations
    {
        public List<string> Calls { get; } = [];
        public Task RunAsync(CancellationToken cancellationToken) { Calls.Add("run"); return Task.CompletedTask; }
        public Task InstallAsync(CancellationToken cancellationToken) { Calls.Add("install"); return Task.CompletedTask; }
        public Task UninstallAsync(CancellationToken cancellationToken) { Calls.Add("uninstall"); return Task.CompletedTask; }
    }

    private sealed class FakeConsole : ICliConsole
    {
        public void WriteLine(string value) { }
        public void WriteError(string value) { }
        public Task<char[]> ReadSecretAsync(string prompt, CancellationToken cancellationToken) => Task.FromResult(Array.Empty<char>());
    }

    private sealed class NoopCliServices : ICliServices
    {
        public Task<CliGateResult> VerifyInitializationTargetAsync(string remoteUrl, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CliGateResult> VerifyPrivateRepositoryAsync(string remoteUrl, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CliInitializationResult> InitializeAsync(string remoteUrl, ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CliAuthenticatedRepository> AuthenticateRepositoryAsync(string remoteUrl, ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CliGateResult> ProbeCompatibilityAsync(CliAuthenticatedRepository repository, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CliJoinPlan> PlanJoinAsync(CliAuthenticatedRepository repository, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SyncResult> ApplyJoinAsync(CliAuthenticatedRepository repository, CliJoinPlan plan, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AbortJoinAsync(CliAuthenticatedRepository repository, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SyncResult> SynchronizeAsync(SyncMode mode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CliStatusReport> GetStatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CliDoctorReport> RunDoctorAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<CliConflictInfo>> ListConflictsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CliResolutionResult> ResolveAsync(string conflictId, CliResolution resolution, string? exportDirectory, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

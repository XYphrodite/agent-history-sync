using System.Diagnostics;
using CodexHistorySync.Cli;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Sync;
using CodexHistorySync.Windows;

namespace CodexHistorySync.IntegrationTests;

public sealed class CliTests
{
    private static readonly string Remote = string.Concat("https://", "user", ":", "credential", "@github.com/example/private-history.git");
    private static readonly string Passphrase = string.Join(' ', "correct", "horse", "battery", "staple");
    private static readonly string PromptMarker = string.Join('-', "UNIQUE", "PLAINTEXT", "PROMPT", "MARKER");

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task Help_is_a_successful_non_mutating_command(string option)
    {
        var fixture = new Fixture();

        var exitCode = await fixture.Application.RunAsync([option], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage: agent-sync", fixture.Console.OutputText);
        Assert.Contains("init|join|sync|pull|push|status|doctor|conflicts|resolve|agent", fixture.Console.OutputText);
        Assert.Contains("[--manage]", fixture.Console.OutputText);
        Assert.Empty(fixture.Console.ErrorText);
        Assert.Empty(fixture.Services.Calls);
    }

    [Fact]
    public async Task Manage_exact_flag_invokes_only_the_manager_runner()
    {
        var fixture = new Fixture();
        var manager = new FakeSessionManagerRunner();
        var application = new CliApplication(fixture.Services, fixture.Console, managerRunner: manager);

        var exitCode = await application.RunAsync(["--manage"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, manager.RunCount);
        Assert.Empty(fixture.Services.Calls);
    }

    [Fact]
    public async Task Manage_with_extra_argument_is_a_usage_error_without_running_manager()
    {
        var fixture = new Fixture();
        var manager = new FakeSessionManagerRunner();
        var application = new CliApplication(fixture.Services, fixture.Console, managerRunner: manager);

        var exitCode = await application.RunAsync(["--manage", "extra"], CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Equal(0, manager.RunCount);
        Assert.Contains("[--manage]", fixture.Console.ErrorText);
        Assert.Empty(fixture.Services.Calls);
    }

    [Fact]
    public async Task Manage_composition_does_not_construct_sync_or_remote_services()
    {
        var console = new FakeConsole();
        var manager = new FakeSessionManagerRunner();
        var application = CliComposition.CreateForArguments(
            ["--manage"],
            console,
            _ => throw new InvalidOperationException("Git/GitHub construction must not run."),
            () => manager);

        var exitCode = await application.RunAsync(["--manage"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, manager.RunCount);
    }

    [Fact]
    public async Task Manage_active_state_dispatches_to_the_matching_local_agent_probe()
    {
        var codexCalls = 0;
        var grokCalls = 0;
        var activeState = new WindowsManagedSessionActiveState(
            () => { codexCalls++; return true; },
            () => { grokCalls++; return false; });

        var codex = await activeState.IsActiveAsync(
            ManagedAgent.Codex, "codex", "unused", CancellationToken.None);
        var grok = await activeState.IsActiveAsync(
            ManagedAgent.Grok, "grok", "unused", CancellationToken.None);

        Assert.True(codex);
        Assert.False(grok);
        Assert.Equal(1, codexCalls);
        Assert.Equal(1, grokCalls);
    }

    [Fact]
    public void Manage_maps_windows_locator_source_to_core_writer_availability()
    {
        var option = CliComposition.ToCodexExecutableOption(
            new CodexExecutableResolution("C:\\tools\\codex.exe", CodexExecutableSource.Configured));

        Assert.Equal("C:\\tools\\codex.exe", option.ExecutablePath);
        Assert.Equal(CodexExecutableAvailability.Configured, option.Availability);
    }

    [Fact]
    public void Manage_resolves_missing_codex_layout_without_creating_it_during_composition()
    {
        var home = Path.Combine(Path.GetTempPath(), $"agent-sync-task5-missing-{Guid.NewGuid():N}");

        var paths = CliComposition.TryResolveCodexPaths(home);

        Assert.NotNull(paths);
        Assert.Equal(Path.GetFullPath(home), paths.Home);
        Assert.False(Directory.Exists(home));
    }

    [Fact]
    public async Task Manage_directory_deleter_removes_only_the_selected_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agent-sync-task5-{Guid.NewGuid():N}");
        var selected = Path.Combine(root, "cwd", Guid.NewGuid().ToString());
        var sibling = Path.Combine(root, "cwd", Guid.NewGuid().ToString());
        Directory.CreateDirectory(selected);
        Directory.CreateDirectory(sibling);
        await File.WriteAllTextAsync(Path.Combine(selected, "chat_history.jsonl"), "{}\n");
        await File.WriteAllTextAsync(Path.Combine(sibling, "chat_history.jsonl"), "{}\n");
        try
        {
            var deleter = new WindowsManagedSessionDirectoryDeleter();

            await deleter.DeleteAsync(root, selected, CancellationToken.None);

            Assert.False(Directory.Exists(selected));
            Assert.True(Directory.Exists(sibling));
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Manage_directory_deleter_refuses_concrete_sessions_root_replacement_before_identity_capture()
    {
        var container = Path.Combine(Path.GetTempPath(), $"agent-sync-task5-root-preidentity-{Guid.NewGuid():N}");
        var sessionsRoot = Path.Combine(container, "sessions");
        var preservedRoot = Path.Combine(container, "sessions.preserved");
        var replacementRoot = Path.Combine(container, "replacement");
        var relativeSession = Path.Combine("cwd", Guid.NewGuid().ToString());
        var selected = Path.Combine(sessionsRoot, relativeSession);
        var replacementSession = Path.Combine(replacementRoot, relativeSession);
        var original = Path.Combine(selected, "owned.txt");
        var sentinel = Path.Combine(replacementSession, "outside-keep.txt");
        Directory.CreateDirectory(selected);
        Directory.CreateDirectory(replacementSession);
        await File.WriteAllTextAsync(original, "owned");
        await File.WriteAllTextAsync(sentinel, "outside");
        bool ReplaceRoot()
        {
            Directory.Move(sessionsRoot, preservedRoot);
            Directory.Move(replacementRoot, sessionsRoot);
            return true;
        }

        try
        {
            var deleter = new WindowsManagedSessionDirectoryDeleter(
                afterContainmentValidation: ReplaceRoot,
                afterRootPathValidation: null,
                afterPathValidation: null,
                afterTreeCapture: null);

            await Assert.ThrowsAsync<IOException>(() =>
                deleter.DeleteAsync(sessionsRoot, selected, CancellationToken.None));

            Assert.Equal("outside", await File.ReadAllTextAsync(
                Path.Combine(sessionsRoot, relativeSession, "outside-keep.txt")));
            Assert.Equal("owned", await File.ReadAllTextAsync(
                Path.Combine(preservedRoot, relativeSession, "owned.txt")));
        }
        finally
        {
            if (Directory.Exists(sessionsRoot) && Directory.Exists(preservedRoot))
                Directory.Move(sessionsRoot, replacementRoot);
            if (Directory.Exists(preservedRoot) && !Directory.Exists(sessionsRoot))
                Directory.Move(preservedRoot, sessionsRoot);
            if (Directory.Exists(container)) Directory.Delete(container, recursive: true);
        }
    }

    [Fact]
    public async Task Manage_directory_deleter_refuses_concrete_sessions_root_replacement_after_validation()
    {
        var container = Path.Combine(Path.GetTempPath(), $"agent-sync-task5-root-identity-{Guid.NewGuid():N}");
        var sessionsRoot = Path.Combine(container, "sessions");
        var preservedRoot = Path.Combine(container, "sessions.preserved");
        var replacementRoot = Path.Combine(container, "replacement");
        var relativeSession = Path.Combine("cwd", Guid.NewGuid().ToString());
        var selected = Path.Combine(sessionsRoot, relativeSession);
        var replacementSession = Path.Combine(replacementRoot, relativeSession);
        var original = Path.Combine(selected, "owned.txt");
        var sentinel = Path.Combine(replacementSession, "keep.txt");
        Directory.CreateDirectory(selected);
        Directory.CreateDirectory(replacementSession);
        await File.WriteAllTextAsync(original, "owned");
        await File.WriteAllTextAsync(sentinel, "keep");
        bool ReplaceRoot()
        {
            Directory.Move(sessionsRoot, preservedRoot);
            Directory.Move(replacementRoot, sessionsRoot);
            return true;
        }

        try
        {
            var deleter = new WindowsManagedSessionDirectoryDeleter(
                afterRootPathValidation: ReplaceRoot,
                afterPathValidation: null,
                afterTreeCapture: null);

            await Assert.ThrowsAsync<IOException>(() =>
                deleter.DeleteAsync(sessionsRoot, selected, CancellationToken.None));

            Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(sessionsRoot, relativeSession, "keep.txt")));
            Assert.Equal("owned", await File.ReadAllTextAsync(Path.Combine(preservedRoot, relativeSession, "owned.txt")));
        }
        finally
        {
            if (Directory.Exists(sessionsRoot) && Directory.Exists(preservedRoot))
                Directory.Move(sessionsRoot, replacementRoot);
            if (Directory.Exists(preservedRoot) && !Directory.Exists(sessionsRoot))
                Directory.Move(preservedRoot, sessionsRoot);
            if (Directory.Exists(container)) Directory.Delete(container, recursive: true);
        }
    }

    [Fact]
    public async Task Manage_directory_deleter_refuses_ancestor_replacement_after_path_validation()
    {
        var container = Path.Combine(Path.GetTempPath(), $"agent-sync-task5-root-race-{Guid.NewGuid():N}");
        var sessionsRoot = Path.Combine(container, "sessions");
        var sessionId = Guid.NewGuid().ToString();
        var ancestor = Path.Combine(sessionsRoot, "cwd");
        var selected = Path.Combine(ancestor, sessionId);
        var preserved = ancestor + ".preserved";
        var outside = Path.Combine(container, "outside");
        Directory.CreateDirectory(selected);
        var outsideSelected = Path.Combine(outside, sessionId);
        Directory.CreateDirectory(outsideSelected);
        VerifyDirectoryJunctionsAvailable(container);
        var sentinel = Path.Combine(outsideSelected, "keep.txt");
        await File.WriteAllTextAsync(Path.Combine(selected, "owned.txt"), "owned");
        await File.WriteAllTextAsync(sentinel, "keep");
        bool ReplaceAncestor()
        {
            Directory.Move(ancestor, preserved);
            CreateDirectoryJunction(ancestor, outside);
            return true;
        }

        try
        {
            var deleter = new WindowsManagedSessionDirectoryDeleter(
                afterPathValidation: ReplaceAncestor, afterTreeCapture: null);

            await Assert.ThrowsAsync<IOException>(() =>
                deleter.DeleteAsync(sessionsRoot, selected, CancellationToken.None));

            Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));
            Assert.True(File.Exists(Path.Combine(preserved, sessionId, "owned.txt")));
        }
        finally
        {
            RestoreReplacedDirectory(ancestor, preserved);
            if (Directory.Exists(container)) Directory.Delete(container, recursive: true);
        }
    }

    [Fact]
    public async Task Manage_directory_deleter_refuses_descendant_replacement_without_partial_deletion()
    {
        var container = Path.Combine(Path.GetTempPath(), $"agent-sync-task5-child-race-{Guid.NewGuid():N}");
        var sessionsRoot = Path.Combine(container, "sessions");
        var selected = Path.Combine(sessionsRoot, Guid.NewGuid().ToString());
        var child = Path.Combine(selected, "child");
        var preserved = child + ".preserved";
        var outside = Path.Combine(container, "outside");
        Directory.CreateDirectory(child);
        Directory.CreateDirectory(outside);
        VerifyDirectoryJunctionsAvailable(container);
        var owned = Path.Combine(child, "owned.txt");
        var sentinel = Path.Combine(outside, "keep.txt");
        await File.WriteAllTextAsync(owned, "owned");
        await File.WriteAllTextAsync(sentinel, "keep");
        bool ReplaceChild()
        {
            Directory.Move(child, preserved);
            CreateDirectoryJunction(child, outside);
            return true;
        }

        try
        {
            var deleter = new WindowsManagedSessionDirectoryDeleter(
                afterPathValidation: null, afterTreeCapture: ReplaceChild);

            await Assert.ThrowsAsync<IOException>(() =>
                deleter.DeleteAsync(sessionsRoot, selected, CancellationToken.None));

            Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));
            Assert.True(File.Exists(owned) || File.Exists(Path.Combine(preserved, "owned.txt")));
        }
        finally
        {
            RestoreReplacedDirectory(child, preserved);
            if (Directory.Exists(container)) Directory.Delete(container, recursive: true);
        }
    }

    private static void RestoreReplacedDirectory(string path, string preserved)
    {
        if (Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            Directory.Delete(path);
        if (Directory.Exists(preserved) && !Directory.Exists(path)) Directory.Move(preserved, path);
    }

    private static void VerifyDirectoryJunctionsAvailable(string container)
    {
        var target = Path.Combine(container, "junction-probe-target");
        var link = Path.Combine(container, "junction-probe-link");
        Directory.CreateDirectory(target);
        CreateDirectoryJunction(link, target);
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
        Directory.Delete(link);
        Directory.Delete(target);
    }

    private static void CreateDirectoryJunction(string link, string target)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(link);
        startInfo.ArgumentList.Add(target);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the directory-junction helper.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new IOException($"Directory-junction creation failed with exit code {process.ExitCode}: {output}{error}");
    }

    [Fact]
    public async Task Unknown_command_is_a_usage_error()
    {
        var fixture = new Fixture();

        var exitCode = await fixture.Application.RunAsync(["unknown"], CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("Usage:", fixture.Console.ErrorText);
        Assert.Empty(fixture.Services.Calls);
    }

    [Theory]
    [InlineData("sync", SyncMode.Bidirectional)]
    [InlineData("pull", SyncMode.Pull)]
    [InlineData("push", SyncMode.Push)]
    public async Task Manual_sync_commands_select_the_exact_engine_mode(string command, SyncMode expectedMode)
    {
        var fixture = new Fixture();
        fixture.Services.SyncResult = new SyncResult("revision-7", 2, 3, 1, 0, false);

        var exitCode = await fixture.Application.RunAsync([command], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(expectedMode, fixture.Services.SyncMode);
        Assert.Contains("uploaded=2", fixture.Console.OutputText);
        Assert.Contains("downloaded=3", fixture.Console.OutputText);
        Assert.DoesNotContain(PromptMarker, fixture.Console.AllText);
    }

    [Fact]
    public async Task Sync_with_unresolved_conflicts_returns_exit_code_four()
    {
        var fixture = new Fixture();
        fixture.Services.SyncResult = new SyncResult("revision-8", 0, 0, 0, 2, false);

        var exitCode = await fixture.Application.RunAsync(["sync"], CancellationToken.None);

        Assert.Equal(4, exitCode);
        Assert.Contains("conflicts=2", fixture.Console.OutputText);
    }

    [Fact]
    public async Task Operational_failures_return_one_without_echoing_exception_secrets()
    {
        var fixture = new Fixture();
        fixture.Services.Failure = new InvalidOperationException($"failed for {Remote} using {Passphrase}; saw {PromptMarker}");

        var exitCode = await fixture.Application.RunAsync(["sync"], CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("credential", fixture.Console.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Passphrase, fixture.Console.AllText);
        Assert.DoesNotContain(PromptMarker, fixture.Console.AllText);
    }

    [Fact]
    public async Task Init_checks_private_visibility_before_reading_any_passphrase()
    {
        var fixture = new Fixture();
        fixture.Services.SetupGate = new CliGateResult(false, "private-visibility");

        var exitCode = await fixture.Application.RunAsync(["init", Remote], CancellationToken.None);

        Assert.Equal(3, exitCode);
        Assert.Equal(["verify-initialization"], fixture.Services.Calls);
        Assert.Equal(0, fixture.Console.SecretReadCount);
        Assert.DoesNotContain("credential", fixture.Console.AllText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Init_requires_matching_hidden_passphrases()
    {
        var fixture = new Fixture();
        fixture.Console.Secrets.Enqueue(Passphrase.ToCharArray());
        fixture.Console.Secrets.Enqueue("different".ToCharArray());

        var exitCode = await fixture.Application.RunAsync(["init", Remote], CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Equal(2, fixture.Console.SecretReadCount);
        Assert.Equal(["verify-initialization"], fixture.Services.Calls);
        Assert.DoesNotContain(Passphrase, fixture.Console.AllText);
    }

    [Fact]
    public async Task Init_publishes_then_reports_repository_without_echoing_secrets()
    {
        var fixture = new Fixture();
        fixture.Console.Secrets.Enqueue(Passphrase.ToCharArray());
        fixture.Console.Secrets.Enqueue(Passphrase.ToCharArray());

        var exitCode = await fixture.Application.RunAsync(["init", Remote], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(["verify-initialization", "initialize"], fixture.Services.Calls);
        Assert.Equal(Passphrase, fixture.Services.ObservedPassphrase);
        Assert.Contains("repository-123", fixture.Console.OutputText);
        Assert.DoesNotContain(Passphrase, fixture.Console.AllText);
        Assert.DoesNotContain("credential", fixture.Console.AllText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Join_without_apply_authenticates_and_prints_dry_run_without_local_commit()
    {
        var fixture = new Fixture();
        fixture.Console.Secrets.Enqueue(Passphrase.ToCharArray());

        var exitCode = await fixture.Application.RunAsync(["join", Remote], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(["verify-private", "authenticate", "compatibility", "plan", "abort"], fixture.Services.Calls);
        Assert.Contains("local=1", fixture.Console.OutputText);
        Assert.Contains("remote=4", fixture.Console.OutputText);
        Assert.Contains("pending=3", fixture.Console.OutputText);
        Assert.Contains("--apply", fixture.Console.OutputText);
        Assert.DoesNotContain(Passphrase, fixture.Console.AllText);
    }

    [Fact]
    public async Task Join_apply_commits_local_configuration_only_after_all_gates_and_plan()
    {
        var fixture = new Fixture();
        fixture.Console.Secrets.Enqueue(Passphrase.ToCharArray());

        var exitCode = await fixture.Application.RunAsync(["join", Remote, "--apply"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(["verify-private", "authenticate", "compatibility", "plan", "apply", "abort"], fixture.Services.Calls);
        Assert.True(fixture.Services.JoinApplied);
    }

    [Fact]
    public async Task Join_gate_failure_returns_three_and_never_applies()
    {
        var fixture = new Fixture();
        fixture.Console.Secrets.Enqueue(Passphrase.ToCharArray());
        fixture.Services.CompatibilityGate = new CliGateResult(false, "codex-compatibility",
            "The imported JSONL thread was not listed by Codex.");

        var exitCode = await fixture.Application.RunAsync(["join", Remote, "--apply"], CancellationToken.None);

        Assert.Equal(3, exitCode);
        Assert.Equal(["verify-private", "authenticate", "compatibility", "abort"], fixture.Services.Calls);
        Assert.False(fixture.Services.JoinApplied);
        Assert.Contains("Gate failed: codex-compatibility", fixture.Console.ErrorText);
        Assert.Contains("diagnostic:", fixture.Console.ErrorText);
    }

    [Fact]
    public async Task Join_warns_and_continues_when_codex_is_missing()
    {
        var fixture = new Fixture();
        fixture.Console.Secrets.Enqueue(Passphrase.ToCharArray());
        fixture.Services.CompatibilityGate = new CliGateResult(true, "codex-compatibility",
            "skipped-no-codex: Codex executable was not found. Install the OpenAI Codex VS Code extension or set CODEX_EXE.");

        var exitCode = await fixture.Application.RunAsync(["join", Remote], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(["verify-private", "authenticate", "compatibility", "plan", "abort"], fixture.Services.Calls);
        Assert.Contains("warning: codex-compatibility skipped", fixture.Console.OutputText);
        Assert.Contains("Join plan:", fixture.Console.OutputText);
    }

    [Fact]
    public async Task Status_reports_only_counts_and_revision_and_flags_conflicts()
    {
        var fixture = new Fixture();
        fixture.Services.Status = new CliStatusReport(5, 6, 2, 1, "current-revision-safe", "last-revision-safe");

        var exitCode = await fixture.Application.RunAsync(["status"], CancellationToken.None);

        Assert.Equal(4, exitCode);
        Assert.Contains("local=5", fixture.Console.OutputText);
        Assert.Contains("remote=6", fixture.Console.OutputText);
        Assert.Contains("pending=2", fixture.Console.OutputText);
        Assert.Contains("conflicts=1", fixture.Console.OutputText);
        Assert.Contains("current-revision-safe", fixture.Console.OutputText);
        Assert.Contains("last-revision-safe", fixture.Console.OutputText);
    }

    [Fact]
    public async Task Doctor_reports_each_named_check_and_uses_gate_exit_code()
    {
        var fixture = new Fixture();
        fixture.Services.Doctor = new CliDoctorReport([
            new("codex-paths", true), new("codex-version", true), new("git-version", true),
            new("github-private", false), new("key-access", true), new("repository-schema", true),
            new("process-state", true), new("free-disk-space", true), new("agent-installation", true)]);

        var exitCode = await fixture.Application.RunAsync(["doctor"], CancellationToken.None);

        Assert.Equal(3, exitCode);
        foreach (var check in fixture.Services.Doctor.Checks)
            Assert.Contains(check.Name, fixture.Console.OutputText);
        Assert.DoesNotContain(PromptMarker, fixture.Console.AllText);
    }

    [Fact]
    public async Task Doctor_compatibility_session_passes_exact_inputs_without_persistent_doctor_work()
    {
        var fixture = new Fixture();
        var session = Path.GetFullPath(@"C:\Archive\sensitive-session.jsonl");
        var codexExecutable = Path.GetFullPath(@"C:\Users\Test\.vscode\extensions\openai.chatgpt-0.146.0-win32-x64\bin\windows-x86_64\codex.exe");
        fixture.Services.CompatibilityResult = new CompatibilityResult(true, "codex_vscode/0.146.0", "The imported JSONL thread was listed from the disposable Codex home.");

        var exitCode = await fixture.Application.RunAsync(
            ["doctor", "--compatibility-session", session, "--codex-exe", codexExecutable], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(session, fixture.Services.CompatibilitySession);
        Assert.Equal(codexExecutable, fixture.Services.CompatibilityExecutable);
        Assert.Equal(["compatibility-session"], fixture.Services.Calls);
        Assert.Contains("codex_vscode_0.146.0", fixture.Console.OutputText);
        Assert.Contains("PASS", fixture.Console.OutputText);
        Assert.DoesNotContain(session, fixture.Console.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(codexExecutable, fixture.Console.AllText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Doctor_compatibility_session_returns_gate_exit_code_for_incompatibility()
    {
        var fixture = new Fixture();
        fixture.Services.CompatibilityResult = new CompatibilityResult(false, "unknown", "The compatibility session was not found.");

        var exitCode = await fixture.Application.RunAsync(
            ["doctor", "--codex-exe", @"C:\Codex\codex.exe", "--compatibility-session", @"C:\Archive\session.jsonl"],
            CancellationToken.None);

        Assert.Equal(3, exitCode);
        Assert.Contains("FAIL", fixture.Console.OutputText);
        Assert.Empty(fixture.Console.ErrorText);
    }

    public static TheoryData<string[]> InvalidCompatibilityDoctorArguments => new()
    {
        new[] { "doctor", "--compatibility-session", @"C:\Archive\session.jsonl" },
        new[] { "doctor", "--codex-exe", @"C:\Codex\codex.exe" },
        new[] { "doctor", "--compatibility-session", @"C:\Archive\session.jsonl", "--codex-exe", @"C:\Codex\codex.exe", "extra" },
        new[] { "doctor", "--compatibility-session", "", "--codex-exe", @"C:\Codex\codex.exe" },
        new[] { "doctor", "--compatibility-session", @"C:\Archive\session.jsonl", "--compatibility-session", @"C:\Archive\other.jsonl" }
    };

    [Theory]
    [MemberData(nameof(InvalidCompatibilityDoctorArguments))]
    public async Task Doctor_compatibility_session_rejects_incomplete_duplicate_or_unknown_arguments(string[] args)
    {
        var fixture = new Fixture();

        var exitCode = await fixture.Application.RunAsync(args, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Empty(fixture.Services.Calls);
    }

    [Fact]
    public async Task Conflicts_lists_provenance_without_plaintext_and_returns_four()
    {
        var fixture = new Fixture();
        fixture.Services.Conflicts = [new CliConflictInfo(
            "conflict-1", "local-hash", "remote-hash", "device-a", "device-b",
            DateTimeOffset.Parse("2026-07-29T10:00:00Z"), DateTimeOffset.Parse("2026-07-29T11:00:00Z"))];

        var exitCode = await fixture.Application.RunAsync(["conflicts"], CancellationToken.None);

        Assert.Equal(4, exitCode);
        Assert.Contains("conflict-1", fixture.Console.OutputText);
        Assert.Contains("local-hash", fixture.Console.OutputText);
        Assert.Contains("device-a", fixture.Console.OutputText);
        Assert.DoesNotContain(PromptMarker, fixture.Console.AllText);
    }

    public static TheoryData<string[]> InvalidResolveArguments => new()
    {
        new[] { "resolve", "conflict-1" },
        new[] { "resolve", "conflict-1", "--keep-local", "--keep-remote" },
        new[] { "resolve", "conflict-1", "--export-both" }
    };

    [Theory]
    [MemberData(nameof(InvalidResolveArguments))]
    public async Task Resolve_requires_exactly_one_complete_resolution_option(string[] args)
    {
        var fixture = new Fixture();

        var exitCode = await fixture.Application.RunAsync(args, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Null(fixture.Services.Resolution);
    }

    [Theory]
    [InlineData("--keep-local", CliResolution.KeepLocal)]
    [InlineData("--keep-remote", CliResolution.KeepRemote)]
    public async Task Resolve_maps_keep_option(string option, CliResolution expected)
    {
        var fixture = new Fixture();

        var exitCode = await fixture.Application.RunAsync(["resolve", "conflict-1", option], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(expected, fixture.Services.Resolution);
        Assert.Null(fixture.Services.ExportDirectory);
    }

    [Fact]
    public async Task Resolve_export_both_passes_the_destination()
    {
        var fixture = new Fixture();
        var destination = Path.Combine(Path.GetTempPath(), "codex-history-export");

        var exitCode = await fixture.Application.RunAsync(["resolve", "conflict-1", "--export-both", destination], CancellationToken.None);

        Assert.Equal(4, exitCode);
        Assert.Equal(CliResolution.ExportBoth, fixture.Services.Resolution);
        Assert.Equal(destination, fixture.Services.ExportDirectory);
    }

    [Fact]
    public async Task Init_nonempty_repository_fails_before_reading_any_passphrase()
    {
        var fixture = new Fixture();
        fixture.Services.SetupGate = new CliGateResult(false, "empty-repository");

        var exitCode = await fixture.Application.RunAsync(["init", Remote], CancellationToken.None);

        Assert.Equal(3, exitCode);
        Assert.Equal(["verify-initialization"], fixture.Services.Calls);
        Assert.Equal(0, fixture.Console.SecretReadCount);
    }

    [Fact]
    public async Task Join_apply_uses_actual_sync_conflicts_for_exit_code()
    {
        var fixture = new Fixture();
        fixture.Console.Secrets.Enqueue(Passphrase.ToCharArray());
        fixture.Services.JoinResult = new SyncResult("revision-9", 0, 0, 0, 2, false);

        var exitCode = await fixture.Application.RunAsync(["join", Remote, "--apply"], CancellationToken.None);

        Assert.Equal(4, exitCode);
        Assert.Contains("conflicts=2", fixture.Console.OutputText);
    }

    private sealed class Fixture
    {
        public FakeServices Services { get; } = new();
        public FakeConsole Console { get; } = new();
        public CliApplication Application { get; }

        public Fixture() => Application = new CliApplication(Services, Console);
    }

    private sealed class FakeConsole : ICliConsole
    {
        private readonly StringWriter output = new();
        private readonly StringWriter error = new();

        public Queue<char[]> Secrets { get; } = new();
        public int SecretReadCount { get; private set; }
        public string OutputText => output.ToString();
        public string ErrorText => error.ToString();
        public string AllText => OutputText + ErrorText;

        public void WriteLine(string value) => output.WriteLine(value);
        public void WriteError(string value) => error.WriteLine(value);

        public Task<char[]> ReadSecretAsync(string prompt, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SecretReadCount++;
            return Task.FromResult(Secrets.Dequeue());
        }
    }

    private sealed class FakeSessionManagerRunner : ISessionManagerRunner
    {
        public int RunCount { get; private set; }

        public Task RunAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeServices : ICliServices
    {
        public List<string> Calls { get; } = [];
        public CliGateResult SetupGate { get; set; } = new(true, "private-visibility");
        public CliGateResult CompatibilityGate { get; set; } = new(true, "codex-compatibility");
        public CompatibilityResult CompatibilityResult { get; set; } = new(true, "test", "compatible");
        public SyncResult SyncResult { get; set; } = new("revision-1", 0, 0, 0, 0, false);
        public SyncResult JoinResult { get; set; } = new("revision-1", 0, 0, 0, 0, false);
        public CliStatusReport Status { get; set; } = new(0, 0, 0, 0, "none", "none");
        public CliDoctorReport Doctor { get; set; } = new([]);
        public IReadOnlyList<CliConflictInfo> Conflicts { get; set; } = [];
        public Exception? Failure { get; set; }
        public SyncMode? SyncMode { get; private set; }
        public CliResolution? Resolution { get; private set; }
        public string? ExportDirectory { get; private set; }
        public string? ObservedPassphrase { get; private set; }
        public bool JoinApplied { get; private set; }
        public string? CompatibilitySession { get; private set; }
        public string? CompatibilityExecutable { get; private set; }

        public Task<CliGateResult> VerifyPrivateRepositoryAsync(string remoteUrl, CancellationToken cancellationToken)
        {
            Calls.Add("verify-private");
            Assert.Equal(Remote, remoteUrl);
            return Task.FromResult(SetupGate);
        }

        public Task<CliGateResult> VerifyInitializationTargetAsync(string remoteUrl, CancellationToken cancellationToken)
        {
            Calls.Add("verify-initialization");
            return Task.FromResult(SetupGate);
        }

        public Task<CliInitializationResult> InitializeAsync(string remoteUrl, ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken)
        {
            Calls.Add("initialize");
            ObservedPassphrase = passphrase.ToString();
            return Task.FromResult(new CliInitializationResult("repository-123"));
        }

        public Task<CliAuthenticatedRepository> AuthenticateRepositoryAsync(string remoteUrl, ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken)
        {
            Calls.Add("authenticate");
            ObservedPassphrase = passphrase.ToString();
            return Task.FromResult(new CliAuthenticatedRepository("repository-123", "revision-4"));
        }

        public Task<CliGateResult> ProbeCompatibilityAsync(CliAuthenticatedRepository repository, CancellationToken cancellationToken)
        {
            Calls.Add("compatibility");
            return Task.FromResult(CompatibilityGate);
        }

        public Task<CompatibilityResult> ProbeCompatibilitySessionAsync(string sourceSession, string codexExecutable,
            CancellationToken cancellationToken)
        {
            Calls.Add("compatibility-session");
            CompatibilitySession = sourceSession;
            CompatibilityExecutable = codexExecutable;
            return Task.FromResult(CompatibilityResult);
        }

        public Task<CliJoinPlan> PlanJoinAsync(CliAuthenticatedRepository repository, CancellationToken cancellationToken)
        {
            Calls.Add("plan");
            return Task.FromResult(new CliJoinPlan(1, 4, 3, 0));
        }

        public Task<SyncResult> ApplyJoinAsync(CliAuthenticatedRepository repository, CliJoinPlan plan, CancellationToken cancellationToken)
        {
            Calls.Add("apply");
            JoinApplied = true;
            return Task.FromResult(JoinResult);
        }

        public Task AbortJoinAsync(CliAuthenticatedRepository repository, CancellationToken cancellationToken)
        {
            Calls.Add("abort");
            return Task.CompletedTask;
        }

        public Task<SyncResult> SynchronizeAsync(SyncMode mode, CancellationToken cancellationToken)
        {
            Calls.Add("sync");
            SyncMode = mode;
            return Failure is null ? Task.FromResult(SyncResult) : Task.FromException<SyncResult>(Failure);
        }

        public Task<CliStatusReport> GetStatusAsync(CancellationToken cancellationToken)
        {
            Calls.Add("status");
            return Task.FromResult(Status);
        }

        public Task<CliDoctorReport> RunDoctorAsync(CancellationToken cancellationToken)
        {
            Calls.Add("doctor");
            return Task.FromResult(Doctor);
        }

        public Task<IReadOnlyList<CliConflictInfo>> ListConflictsAsync(CancellationToken cancellationToken)
        {
            Calls.Add("conflicts");
            return Task.FromResult(Conflicts);
        }

        public Task<CliResolutionResult> ResolveAsync(string conflictId, CliResolution resolution, string? exportDirectory, CancellationToken cancellationToken)
        {
            Calls.Add("resolve");
            Resolution = resolution;
            ExportDirectory = exportDirectory;
            return Task.FromResult(new CliResolutionResult(resolution == CliResolution.ExportBoth ? 1 : 0,
                resolution == CliResolution.ExportBoth));
        }
    }
}

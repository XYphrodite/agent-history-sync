using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Xml.Linq;

namespace CodexHistorySync.Windows;

internal sealed record AgentTaskRegistration(
    string Name,
    string ExecutablePath,
    string Arguments,
    string UserId,
    bool LogonTrigger,
    bool ExactShape = true);

internal interface IAgentTaskStore
{
    Task<AgentTaskRegistration?> GetAsync(string name, CancellationToken cancellationToken);
    Task RegisterAsync(AgentTaskRegistration registration, CancellationToken cancellationToken);
    Task DeleteAsync(string name, CancellationToken cancellationToken);
}

internal static class AgentTaskDefinitionParser
{
    internal static AgentTaskRegistration Parse(string name, string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.None);
        var root = document.Root ?? throw new InvalidDataException("The task definition is empty.");
        XNamespace ns = root.Name.Namespace;
        var actions = root.Element(ns + "Actions")?.Elements().ToArray() ?? [];
        var triggers = root.Element(ns + "Triggers")?.Elements().ToArray() ?? [];
        var exec = actions.Length == 1 && actions[0].Name == ns + "Exec" ? actions[0] : null;
        var logon = triggers.Length == 1 && triggers[0].Name == ns + "LogonTrigger" ? triggers[0] : null;
        var principal = root.Element(ns + "Principals")?.Elements(ns + "Principal").SingleOrDefault();
        var principalUser = principal?.Element(ns + "UserId")?.Value ?? string.Empty;
        var triggerUser = logon?.Element(ns + "UserId")?.Value ?? string.Empty;
        var exact = exec is not null && logon is not null &&
            !string.IsNullOrWhiteSpace(principalUser) && !string.IsNullOrWhiteSpace(triggerUser) &&
            StringComparer.OrdinalIgnoreCase.Equals(principalUser, triggerUser);
        return new AgentTaskRegistration(name,
            exec?.Element(ns + "Command")?.Value ?? string.Empty,
            exec?.Element(ns + "Arguments")?.Value ?? string.Empty,
            triggerUser, logon is not null, exact);
    }
}

public interface IAgentInstallationChecker
{
    Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default);
}

public sealed class AgentScheduler : IAgentInstallationChecker
{
    public const string TaskName = "AgentHistorySync";
    /// <summary>Pre-0.3.0 task name; removed on install/uninstall when owned by this user and executable.</summary>
    public const string LegacyTaskName = "CodexHistorySync";
    private readonly IAgentTaskStore store;
    private readonly Func<string?> currentExecutable;
    private readonly Func<string> currentUserId;

    public AgentScheduler()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Task Scheduler requires Windows.");
        store = new WindowsTaskStore();
        currentExecutable = () => Environment.ProcessPath;
        currentUserId = CurrentUserSid;
    }

    internal AgentScheduler(IAgentTaskStore store, Func<string?> currentExecutable, Func<string> currentUserId)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.currentExecutable = currentExecutable ?? throw new ArgumentNullException(nameof(currentExecutable));
        this.currentUserId = currentUserId ?? throw new ArgumentNullException(nameof(currentUserId));
    }

    public async Task InstallAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        var canonical = CanonicalExecutable(executablePath);
        var expected = OwnedRegistration(TaskName, canonical);
        var existing = await store.GetAsync(TaskName, cancellationToken).ConfigureAwait(false);
        if (existing is not null && !IsOwned(existing, expected))
            throw new InvalidOperationException("A task named AgentHistorySync exists but is not owned by this executable and user.");
        await TryRemoveOwnedTaskAsync(LegacyTaskName, LegacyExecutable(canonical), cancellationToken).ConfigureAwait(false);
        await store.RegisterAsync(expected, cancellationToken).ConfigureAwait(false);
    }

    public async Task UninstallAsync(CancellationToken cancellationToken = default)
    {
        var executable = CanonicalExecutable(currentExecutable()
            ?? throw new InvalidOperationException("The current executable path is unavailable."));
        await RemoveOwnedTaskOrThrowAsync(TaskName, executable, cancellationToken).ConfigureAwait(false);
        await TryRemoveOwnedTaskAsync(LegacyTaskName, LegacyExecutable(executable), cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default)
    {
        var executable = currentExecutable();
        if (string.IsNullOrWhiteSpace(executable)) return false;
        var canonical = CanonicalExecutable(executable);
        if (await IsOwnedTaskAsync(TaskName, canonical, cancellationToken).ConfigureAwait(false)) return true;
        return await IsOwnedTaskAsync(LegacyTaskName, LegacyExecutable(canonical), cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveOwnedTaskOrThrowAsync(string name, string executablePath, CancellationToken cancellationToken)
    {
        var expected = OwnedRegistration(name, executablePath);
        var existing = await store.GetAsync(name, cancellationToken).ConfigureAwait(false);
        if (existing is null) return;
        if (!IsOwned(existing, expected))
            throw new InvalidOperationException($"The {name} task is not owned by this executable and user.");
        await store.DeleteAsync(name, cancellationToken).ConfigureAwait(false);
    }

    private async Task TryRemoveOwnedTaskAsync(string name, string executablePath, CancellationToken cancellationToken)
    {
        var expected = OwnedRegistration(name, executablePath);
        var existing = await store.GetAsync(name, cancellationToken).ConfigureAwait(false);
        if (existing is null) return;
        if (!IsOwned(existing, expected)) return;
        await store.DeleteAsync(name, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> IsOwnedTaskAsync(string name, string executablePath, CancellationToken cancellationToken)
    {
        var expected = OwnedRegistration(name, executablePath);
        var existing = await store.GetAsync(name, cancellationToken).ConfigureAwait(false);
        return existing is not null && IsOwned(existing, expected);
    }

    private AgentTaskRegistration OwnedRegistration(string name, string executablePath) =>
        new(name, executablePath, "agent run", currentUserId(), true);

    private static bool IsOwned(AgentTaskRegistration actual, AgentTaskRegistration expected) =>
        actual.ExactShape && actual.LogonTrigger &&
        StringComparer.Ordinal.Equals(actual.Name, expected.Name) &&
        StringComparer.OrdinalIgnoreCase.Equals(CanonicalExecutable(actual.ExecutablePath), expected.ExecutablePath) &&
        StringComparer.Ordinal.Equals(actual.Arguments, expected.Arguments) &&
        StringComparer.OrdinalIgnoreCase.Equals(actual.UserId, expected.UserId);

    private static string CanonicalExecutable(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("The agent executable path must be absolute.", nameof(path));
        return Path.GetFullPath(path);
    }

    private static string LegacyExecutable(string executablePath) =>
        Path.Combine(Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException("The agent executable directory is unavailable."), "codex-sync.exe");

    [SupportedOSPlatform("windows")]
    private static string CurrentUserSid() => WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");

    [SupportedOSPlatform("windows")]
    private sealed class WindowsTaskStore : IAgentTaskStore
    {
        private const int TaskActionExecute = 0;
        private const int TaskTriggerLogon = 9;
        private const int TaskCreateOrUpdate = 6;
        private const int TaskLogonInteractiveToken = 3;

        public Task<AgentTaskRegistration?> GetAsync(string name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dynamic service = Connect();
            try
            {
                dynamic folder = service.GetFolder("\\");
                try
                {
                    dynamic task = folder.GetTask(name);
                    return Task.FromResult<AgentTaskRegistration?>(AgentTaskDefinitionParser.Parse(name, (string)task.Xml));
                }
                catch (COMException exception) when ((uint)exception.HResult is 0x80070002 or 0x8004130F)
                {
                    return Task.FromResult<AgentTaskRegistration?>(null);
                }
            }
            finally { Release(service); }
        }

        public Task RegisterAsync(AgentTaskRegistration registration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dynamic service = Connect();
            try
            {
                dynamic folder = service.GetFolder("\\");
                dynamic definition = service.NewTask(0);
                definition.RegistrationInfo.Description = "Synchronizes encrypted Codex history for the current Windows user.";
                definition.Principal.UserId = registration.UserId;
                definition.Principal.LogonType = TaskLogonInteractiveToken;
                definition.Principal.RunLevel = 0;
                definition.Settings.Enabled = true;
                definition.Settings.StartWhenAvailable = true;
                definition.Settings.ExecutionTimeLimit = "PT0S";
                definition.Settings.MultipleInstances = 2;
                dynamic trigger = definition.Triggers.Create(TaskTriggerLogon);
                trigger.UserId = registration.UserId;
                trigger.Enabled = true;
                dynamic action = definition.Actions.Create(TaskActionExecute);
                action.Path = registration.ExecutablePath;
                action.Arguments = registration.Arguments;
                action.WorkingDirectory = Path.GetDirectoryName(registration.ExecutablePath)!;
                folder.RegisterTaskDefinition(registration.Name, definition, TaskCreateOrUpdate,
                    registration.UserId, null, TaskLogonInteractiveToken, null);
                return Task.CompletedTask;
            }
            finally { Release(service); }
        }

        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dynamic service = Connect();
            try
            {
                dynamic folder = service.GetFolder("\\");
                folder.DeleteTask(name, 0);
                return Task.CompletedTask;
            }
            finally { Release(service); }
        }

        private static dynamic Connect()
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Task Scheduler requires Windows.");
            var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            return service;
        }

        private static void Release(object value)
        {
            if (Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
    }
}

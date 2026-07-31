using CodexHistorySync.Windows;

namespace CodexHistorySync.Windows.Tests;

public sealed class AgentSchedulerTests
{
    private const string UserSid = "S-1-5-21-test";

    [Fact]
    public async Task Install_registers_exact_unquoted_path_arguments_and_per_user_logon_trigger()
    {
        var store = new FakeTaskStore();
        var executable = Path.GetFullPath(@"C:\Program Files\Codex History Sync\codex-sync.exe");
        var scheduler = new AgentScheduler(store, () => executable, () => UserSid);

        await scheduler.InstallAsync(@"C:\Program Files\Codex History Sync\folder\..\codex-sync.exe");

        Assert.NotNull(store.Task);
        var task = store.Task!;
        Assert.Equal(AgentScheduler.TaskName, task.Name);
        Assert.Equal(executable, task.ExecutablePath);
        Assert.Equal("agent run", task.Arguments);
        Assert.Equal(UserSid, task.UserId);
        Assert.True(task.LogonTrigger);
    }

    [Fact]
    public async Task Install_refuses_to_replace_foreign_same_name_task()
    {
        var executable = Path.GetFullPath(@"C:\Tools\codex-sync.exe");
        var store = new FakeTaskStore
        {
            Task = new AgentTaskRegistration(AgentScheduler.TaskName, @"C:\Other\foreign.exe", "agent run", UserSid, true)
        };
        var scheduler = new AgentScheduler(store, () => executable, () => UserSid);

        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.InstallAsync(executable));
        Assert.Equal(@"C:\Other\foreign.exe", store.Task!.ExecutablePath);
    }

    [Fact]
    public async Task Uninstall_removes_only_task_owned_by_current_executable_and_user()
    {
        var executable = Path.GetFullPath(@"C:\Tools\codex-sync.exe");
        var store = new FakeTaskStore
        {
            Task = new AgentTaskRegistration(AgentScheduler.TaskName, executable, "agent run", UserSid, true)
        };
        var scheduler = new AgentScheduler(store, () => executable, () => UserSid);

        await scheduler.UninstallAsync();

        Assert.Null(store.Task);
    }

    [Theory]
    [InlineData(@"C:\Other\foreign.exe", "agent run", UserSid, true)]
    [InlineData(@"C:\Tools\codex-sync.exe", "agent install", UserSid, true)]
    [InlineData(@"C:\Tools\codex-sync.exe", "agent run", "S-1-5-21-other", true)]
    [InlineData(@"C:\Tools\codex-sync.exe", "agent run", UserSid, false)]
    public async Task Uninstall_refuses_foreign_or_inexact_task(string path, string arguments, string userId, bool logon)
    {
        var executable = Path.GetFullPath(@"C:\Tools\codex-sync.exe");
        var store = new FakeTaskStore
        {
            Task = new AgentTaskRegistration(AgentScheduler.TaskName, path, arguments, userId, logon)
        };
        var scheduler = new AgentScheduler(store, () => executable, () => UserSid);

        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.UninstallAsync());
        Assert.NotNull(store.Task);
    }

    [Fact]
    public async Task Installation_check_rejects_foreign_task_without_mutation()
    {
        var executable = Path.GetFullPath(@"C:\Tools\codex-sync.exe");
        var store = new FakeTaskStore
        {
            Task = new AgentTaskRegistration(AgentScheduler.TaskName, @"C:\Other\codex-sync.exe", "agent run", UserSid, true)
        };
        var scheduler = new AgentScheduler(store, () => executable, () => UserSid);

        Assert.False(await scheduler.IsInstalledAsync());
        Assert.NotNull(store.Task);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public void Task_xml_requires_explicit_per_user_logon_and_exactly_one_action(bool includeTriggerUser, int actionCount)
    {
        var extraAction = actionCount == 2 ? "<Exec><Command>C:\\Other.exe</Command></Exec>" : string.Empty;
        var triggerUser = includeTriggerUser ? $"<UserId>{UserSid}</UserId>" : string.Empty;
        var xml = $"""
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Principals><Principal><UserId>{UserSid}</UserId></Principal></Principals>
              <Triggers><LogonTrigger>{triggerUser}</LogonTrigger></Triggers>
              <Actions><Exec><Command>C:\Program Files\Codex History Sync\codex-sync.exe</Command><Arguments>agent run</Arguments></Exec>{extraAction}</Actions>
            </Task>
            """;

        var registration = AgentTaskDefinitionParser.Parse(AgentScheduler.TaskName, xml);

        Assert.False(registration.ExactShape);
    }

    private sealed class FakeTaskStore : IAgentTaskStore
    {
        public AgentTaskRegistration? Task { get; set; }

        public Task<AgentTaskRegistration?> GetAsync(string name, CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(Task is { } task && task.Name == name ? task : null);

        public Task RegisterAsync(AgentTaskRegistration registration, CancellationToken cancellationToken)
        {
            Task = registration;
            return System.Threading.Tasks.Task.CompletedTask;
        }

        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            if (Task?.Name == name) Task = null;
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }
}

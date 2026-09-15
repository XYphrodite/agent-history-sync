using Spectre.Console;

namespace CodexHistorySync.Cli;

internal static class CliEntryPoint
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken ct,
        Func<string[], CancellationToken, Task<int>>? run = null, ICliConsole? console = null,
        CliProfileStore? store = null,
        Func<IReadOnlyList<CliProfile>, CancellationToken, Task<string?>>? select = null,
        Func<CliProfile, IDisposable>? apply = null)
    {
        console ??= new SystemCliConsole();
        run ??= static (command, token) => CliComposition.CreateDefault(command).RunAsync(command, token);
        try
        {
            var parsed = CliProfileArguments.Parse(args);
            var managing = parsed.Command.FirstOrDefault() == "profile";
            // Existing commands, including update and MCP, keep their startup path and require no registry.
            if (!managing && parsed.Name is null && !parsed.Select)
                return await run(parsed.Command, ct).ConfigureAwait(false);

            store ??= new CliProfileStore(Environment.GetEnvironmentVariable("LOCALAPPDATA") ??
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            if (managing)
            {
                if (parsed.Name is not null || parsed.Select)
                    throw new CliProfileException("Profile management uses the base settings directory. Omit --profile and --select-profile.");
                return await ManageAsync(parsed.Command, store, console, ct).ConfigureAwait(false);
            }
            var name = parsed.Name;
            // Avalonia must start on the entry thread. Complete only its profile setup here;
            // all other commands retain asynchronous startup. There is no UI context to resume yet.
            Task<T> CompleteSetup<T>(Task<T> pending) => parsed.Command is ["--sessions"]
                ? Task.FromResult(pending.GetAwaiter().GetResult()) : pending;
            if (parsed.Select)
            {
                select ??= SelectAsync;
                var profiles = await CompleteSetup(store.ListAsync(ct)).ConfigureAwait(false);
                name = await CompleteSetup(select(profiles, ct)).ConfigureAwait(false);
                if (name is null) return 0;
            }
            var profile = await CompleteSetup(store.ResolveAsync(name!, ct)).ConfigureAwait(false);
            if (profile.Name != "default" && parsed.Command is ["agent", "install" or "uninstall"])
                throw new CliProfileException("Scheduled agent installation does not support named profiles yet. Use 'agent run --profile <name>' for a foreground worker.");
            if (parsed.Command.Length == 0) throw new CliProfileException("Specify a command, for example: agent-sync status --profile " + profile.Name);
            PrepareHomes(profile, parsed.Command);
            using var environment = (apply ?? (static selected => new CliProfileEnvironment(selected)))(profile);
            if (parsed.Command[0] is "init" or "join" or "sync" or "pull" or "push" or "status" or "doctor" or "conflicts" or "resolve" or "agent")
                console.WriteLine($"Profile: {profile.Name}");
            return await run(parsed.Command, ct).ConfigureAwait(false);
        }
        catch (CliProfileException exception)
        {
            console.WriteError(exception.Message);
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            console.WriteError($"Profile operation failed: {exception.GetType().Name}.");
            return 1;
        }
    }

    private static async Task<string?> SelectAsync(IReadOnlyList<CliProfile> profiles, CancellationToken ct)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            throw new CliProfileException("Interactive profile selection requires a terminal. Use --profile <name> in scripts and MCP configuration.");
        var prompt = new SelectionPrompt<CliProfile>()
            .Title("Choose a profile for this command")
            .UseConverter(profile => Markup.Escape(profile.Name))
            .AddChoices(profiles);
        var selected = await AnsiConsole.PromptAsync(prompt, ct).ConfigureAwait(false);
        return selected.Name;
    }

    private static void PrepareHomes(CliProfile profile, string[] command)
    {
        if (profile.SessionsDirectory is not { } root || command[0] is not ("init" or "join" or "pull" or "push" or "sync")) return;
        foreach (var (agent, directory) in new[] { ("codex", "sessions"), ("codex", "archived_sessions"),
            ("grok", "sessions"), ("claude", "projects"), ("continue", "sessions"), ("kimi", "sessions") })
            Directory.CreateDirectory(Path.Combine(root, agent, directory));
    }

    private static async Task<int> ManageAsync(string[] args, CliProfileStore store, ICliConsole console, CancellationToken ct)
    {
        if (args is ["profile"] or ["profile", "list"])
        {
            foreach (var profile in await store.ListAsync(ct).ConfigureAwait(false)) Print(profile, console);
            return 0;
        }
        if (args is ["profile", "show", var name])
        {
            Print(await store.ResolveAsync(name, ct).ConfigureAwait(false), console);
            return 0;
        }
        if (args is ["profile", "remove", var removed])
        {
            await store.RemoveAsync(removed, ct).ConfigureAwait(false);
            console.WriteLine($"Removed profile '{CliProfileStore.NormalizeName(removed)}'. Its settings, keys and sessions remain on disk.");
            return 0;
        }
        if (args.Length >= 3 && args[1] == "add")
        {
            string? data = null;
            string? sessions = null;
            for (var index = 3; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--data-dir" when data is null && index + 1 < args.Length:
                        data = args[++index];
                        break;
                    case "--sessions-dir" when sessions is null && index + 1 < args.Length:
                        sessions = args[++index];
                        break;
                    default: throw new CliProfileException("Usage: agent-sync profile add <name> [--data-dir <path>] [--sessions-dir <path>]");
                }
            }
            var profile = await store.AddAsync(args[2], data, sessions, ct).ConfigureAwait(false);
            Print(profile, console);
            console.WriteLine($"Use: agent-sync status --profile {profile.Name}");
            return 0;
        }
        foreach (var line in new[]
        {
            "agent-sync profile list",
            "agent-sync profile add <name> [--data-dir <path>] [--sessions-dir <path>]",
            "agent-sync profile show <name>",
            "agent-sync profile remove <name>    remove only the name; keep its files",
            "agent-sync <command> --profile <name>",
            "agent-sync <command> --select-profile"
        }) console.WriteLine(line);
        return args is ["profile", "--help" or "-h"] ? 0 : 2;
    }

    private static void Print(CliProfile profile, ICliConsole console)
    {
        console.WriteLine($"{profile.Name}  data={profile.DataDirectory}");
        console.WriteLine($"  sessions={profile.SessionsDirectory ?? "native agent directories"}");
    }
}

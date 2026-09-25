using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexHistorySync.Cli;

internal sealed class CliProfileException(string message) : Exception(message);

internal sealed record CliProfile(string Name, string DataDirectory, string? SessionsDirectory = null);

internal sealed record CliProfileArguments(string[] Command, string? Name, bool Select)
{
    internal static CliProfileArguments Parse(string[] args)
    {
        var command = new List<string>();
        string? name = null;
        var select = false;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument == "--")
            {
                command.AddRange(args.Skip(index + 1));
                break;
            }
            if (argument == "--select-profile")
            {
                if (select || name is not null) throw new CliProfileException("Choose either --profile <name> or --select-profile, once.");
                select = true;
            }
            else if (argument == "--profile" || argument.StartsWith("--profile=", StringComparison.Ordinal))
            {
                if (select || name is not null) throw new CliProfileException("Choose either --profile <name> or --select-profile, once.");
                if (argument == "--profile")
                {
                    if (++index == args.Length) throw new CliProfileException("--profile requires a name. Use --select-profile to choose from a list.");
                    name = args[index];
                }
                else name = argument["--profile=".Length..];
                name = CliProfileStore.NormalizeName(name);
            }
            else command.Add(argument);
        }
        return new CliProfileArguments(command.ToArray(), name, select);
    }
}

/// <summary>Names point to existing settings and optional agent homes; no keys or history are moved.</summary>
internal sealed class CliProfileStore
{
    private const int MaximumRegistryBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Regex NamePattern = new(@"\A[a-zA-Z0-9][a-zA-Z0-9_-]{0,47}\z", RegexOptions.CultureInvariant);
    private readonly string registryPath;

    internal CliProfileStore(string dataDirectory)
    {
        DataDirectory = FullPath(dataDirectory);
        registryPath = Path.Combine(DataDirectory, "CodexHistorySync", "profiles.json");
    }

    internal string DataDirectory { get; }
    internal string RegistryPath => registryPath;

    internal static string NormalizeName(string name)
    {
        if (!NamePattern.IsMatch(name))
            throw new CliProfileException("Profile names must contain 1-48 letters, digits, '-' or '_', starting with a letter or digit.");
        return name.ToLowerInvariant();
    }

    internal static string FullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new CliProfileException("A profile directory cannot be empty.");
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new CliProfileException("A profile directory is invalid."); }
    }

    internal async Task<IReadOnlyList<CliProfile>> ListAsync(CancellationToken ct)
    {
        var registered = await ReadAsync(ct).ConfigureAwait(false);
        return new[] { new CliProfile("default", DataDirectory) }.Concat(registered.OrderBy(profile => profile.Name, StringComparer.Ordinal)).ToArray();
    }

    internal async Task<CliProfile> ResolveAsync(string name, CancellationToken ct)
    {
        name = NormalizeName(name);
        if (name == "default") return new CliProfile("default", DataDirectory);
        return (await ListAsync(ct).ConfigureAwait(false)).SingleOrDefault(profile => profile.Name == name)
            ?? throw new CliProfileException($"Unknown profile '{name}'. Run 'agent-sync profile list' or 'agent-sync profile add {name}'.");
    }

    internal async Task<CliProfile> AddAsync(string name, string? dataDirectory, string? sessionsDirectory, CancellationToken ct)
    {
        name = NormalizeName(name);
        if (name == "default") throw new CliProfileException("The default profile already represents the current settings and cannot be replaced.");
        var profile = new CliProfile(name,
            FullPath(dataDirectory ?? Path.Combine(DataDirectory, "CodexHistorySync", "profiles", name)),
            sessionsDirectory is null ? null : FullPath(sessionsDirectory));
        Validate(profile);
        await using var registryLock = await LockAsync(ct).ConfigureAwait(false);
        var profiles = await ReadAsync(ct).ConfigureAwait(false);
        if (profiles.Any(existing => existing.Name == name))
            throw new CliProfileException($"Profile '{name}' already exists. Its settings have not been replaced.");
        if (profiles.Count >= 128) throw new CliProfileException("The profile registry already contains 128 profiles.");
        await WriteAsync(profiles.Append(profile).ToArray(), ct).ConfigureAwait(false);
        return profile;
    }

    internal async Task RemoveAsync(string name, CancellationToken ct)
    {
        name = NormalizeName(name);
        if (name == "default") throw new CliProfileException("The default profile cannot be removed.");
        await using var registryLock = await LockAsync(ct).ConfigureAwait(false);
        var profiles = await ReadAsync(ct).ConfigureAwait(false);
        if (!profiles.Any(profile => profile.Name == name)) throw new CliProfileException($"Unknown profile '{name}'.");
        await WriteAsync(profiles.Where(profile => profile.Name != name).ToArray(), ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<CliProfile>> ReadAsync(CancellationToken ct)
    {
        RejectReparsePoints(registryPath);
        if (!File.Exists(registryPath)) return [];
        try
        {
            await using var stream = new FileStream(registryPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MaximumRegistryBytes) throw new CliProfileException("The profile registry is too large.");
            var registry = await JsonSerializer.DeserializeAsync<Registry>(stream, JsonOptions, ct).ConfigureAwait(false);
            if (registry is null || registry.SchemaVersion != 1 || registry.Profiles is null || registry.Profiles.Length > 128)
                throw new CliProfileException("The profile registry has an unsupported format.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in registry.Profiles)
            {
                if (profile is null) throw new CliProfileException("The profile registry contains an invalid entry.");
                Validate(profile);
                if (profile.Name == "default" || !names.Add(profile.Name))
                    throw new CliProfileException("The profile registry contains a duplicate or reserved name.");
            }
            return registry.Profiles;
        }
        catch (JsonException) { throw new CliProfileException("The profile registry is not valid JSON. Repair profiles.json before selecting a profile."); }
        catch (IOException) { throw new CliProfileException("The profile registry could not be read."); }
        catch (UnauthorizedAccessException) { throw new CliProfileException("The profile registry is not accessible to this user."); }
    }

    private static void Validate(CliProfile profile)
    {
        if (profile.Name is null || NormalizeName(profile.Name) != profile.Name || string.IsNullOrWhiteSpace(profile.DataDirectory) ||
            !Path.IsPathFullyQualified(profile.DataDirectory)) throw new CliProfileException("The profile registry contains invalid settings.");
        var data = FullPath(profile.DataDirectory);
        if (profile.SessionsDirectory is not { } sessions) return;
        if (string.IsNullOrWhiteSpace(sessions) || !Path.IsPathFullyQualified(sessions))
            throw new CliProfileException("A profile's session directory must be an absolute path.");
        sessions = FullPath(sessions);
        var sessionsPrefix = Path.EndsInDirectorySeparator(sessions) ? sessions : sessions + Path.DirectorySeparatorChar;
        if (string.Equals(data, sessions, StringComparison.OrdinalIgnoreCase) ||
            data.StartsWith(sessionsPrefix, StringComparison.OrdinalIgnoreCase))
            throw new CliProfileException("Profile settings must be outside the session directory.");
    }

    private async Task<FileStream> LockAsync(CancellationToken ct)
    {
        RejectReparsePoints(registryPath);
        Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);
        var lockPath = registryPath + ".lock";
        RejectReparsePoints(lockPath);
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 50) { await Task.Delay(100, ct).ConfigureAwait(false); }
            catch (IOException) { throw new CliProfileException("The profile registry is busy. Try again after the other command finishes."); }
        }
    }

    private async Task WriteAsync(IReadOnlyList<CliProfile> profiles, CancellationToken ct)
    {
        var temporary = registryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new Registry(1, profiles.ToArray()), JsonOptions, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(true);
            }
            RejectReparsePoints(registryPath);
            File.Move(temporary, registryPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RejectReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new CliProfileException("The profile registry path contains a symbolic link or junction.");
    }

    private sealed record Registry(int SchemaVersion, CliProfile[] Profiles);
}

/// <summary>The selected profile is scoped to this invocation, including all local viewers and MCP.</summary>
internal sealed class CliProfileEnvironment : IDisposable
{
    private readonly Dictionary<string, string?> previous = new(StringComparer.Ordinal);
    private readonly Action<string, string?> set;

    internal CliProfileEnvironment(CliProfile profile, Func<string, string?>? get = null, Action<string, string?>? set = null)
    {
        get ??= Environment.GetEnvironmentVariable;
        this.set = set ?? Environment.SetEnvironmentVariable;
        var values = new Dictionary<string, string> { ["LOCALAPPDATA"] = profile.DataDirectory };
        if (profile.SessionsDirectory is { } sessions)
        {
            values["CODEX_HOME"] = Path.Combine(sessions, "codex");
            values["GROK_HOME"] = Path.Combine(sessions, "grok");
            values["CLAUDE_CONFIG_DIR"] = Path.Combine(sessions, "claude");
            values["CONTINUE_GLOBAL_DIR"] = Path.Combine(sessions, "continue");
            values["KIMI_CODE_HOME"] = Path.Combine(sessions, "kimi");
            values["MUSE_HOME"] = Path.Combine(sessions, "muse");
            values["HERMES_HOME"] = Path.Combine(sessions, "hermes");
        }
        try
        {
            foreach (var (name, value) in values)
            {
                previous[name] = get(name);
                this.set(name, value);
            }
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        foreach (var (name, value) in previous) set(name, value);
        previous.Clear();
    }
}

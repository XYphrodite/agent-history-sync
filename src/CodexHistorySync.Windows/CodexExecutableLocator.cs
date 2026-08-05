namespace CodexHistorySync.Windows;

public sealed class CodexExecutableLocator
{
    private readonly string? configuredExecutable;
    private readonly string userProfile;
    private readonly string pathEnvironment;
    private readonly Func<string, bool> fileExists;
    private readonly Func<string, IReadOnlyList<string>> enumerateDirectories;

    public CodexExecutableLocator()
        : this(Environment.GetEnvironmentVariable("CODEX_EXE"),
            DefaultUserProfile(),
            Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
            File.Exists,
            EnumerateDirectories)
    {
    }

    internal CodexExecutableLocator(string? configuredExecutable, string userProfile, string pathEnvironment,
        Func<string, bool> fileExists, Func<string, IReadOnlyList<string>> enumerateDirectories)
    {
        this.configuredExecutable = configuredExecutable;
        this.userProfile = userProfile ?? string.Empty;
        this.pathEnvironment = pathEnvironment ?? string.Empty;
        this.fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
        this.enumerateDirectories = enumerateDirectories ?? throw new ArgumentNullException(nameof(enumerateDirectories));
    }

    public string? Resolve()
    {
        if (!string.IsNullOrWhiteSpace(configuredExecutable)) return Path.GetFullPath(configuredExecutable);

        foreach (var extensionRoot in VsCodeExtensionRoots(userProfile))
        {
            IReadOnlyList<string> extensions;
            try { extensions = enumerateDirectories(extensionRoot); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var extension in extensions
                         .Where(IsFirstPartyWindowsExtension)
                         .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var executable = Path.GetFullPath(Path.Combine(extension, "bin", "windows-x86_64", "codex.exe"));
                if (fileExists(executable)) return executable;
            }
        }

        foreach (var pathEntry in pathEnvironment.Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = pathEntry.Trim('"');
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                var executable = Path.GetFullPath(Path.Combine(directory, "codex.exe"));
                if (fileExists(executable)) return executable;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Ignore malformed PATH entries and continue looking for a concrete executable.
            }
        }

        return null;
    }

    internal static IReadOnlyList<string> VsCodeExtensionRoots(string userProfile) =>
        string.IsNullOrWhiteSpace(userProfile)
            ? []
            : [Path.Combine(userProfile, ".vscode", "extensions"),
                Path.Combine(userProfile, ".vscode-insiders", "extensions")];

    internal static string DefaultUserProfile() => SelectUserProfile(
        Environment.GetEnvironmentVariable("USERPROFILE"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static string SelectUserProfile(string? environmentProfile, string? specialFolderProfile)
    {
        var selected = !string.IsNullOrWhiteSpace(environmentProfile) ? environmentProfile : specialFolderProfile;
        return string.IsNullOrWhiteSpace(selected) ? string.Empty : Path.GetFullPath(selected);
    }

    internal static bool IsFirstPartyWindowsExtension(string extensionPath)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(extensionPath));
        return name.StartsWith("openai.chatgpt-", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith("-win32-x64", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> EnumerateDirectories(string root) =>
        Directory.Exists(root) ? Directory.GetDirectories(root, "openai.chatgpt-*-win32-x64", SearchOption.TopDirectoryOnly) : [];
}

namespace CodexHistorySync.Core.Management;

/// <summary>
/// Best-effort audit trail for copy and delete. The manager status bar is ephemeral; this file is
/// how a failed copy can be diagnosed after the window is closed. Never throws and never records
/// conversation text or native paths.
/// </summary>
public sealed class SessionOperationsLog
{
    public const string FileName = "session-operations.log";
    private readonly string path;
    private static readonly object Gate = new();

    public SessionOperationsLog(string? localAppDataDirectory = null)
    {
        var root = string.IsNullOrWhiteSpace(localAppDataDirectory)
            ? Environment.GetEnvironmentVariable("LOCALAPPDATA")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localAppDataDirectory;
        path = Path.Combine(root, "CodexHistorySync", "logs", FileName);
    }

    public string PathFor => path;

    public void RecordCopy(ManagedSession source, string destination, string outcome) =>
        Append("copy", SourceLabel(source), destination, outcome);

    public void RecordDelete(ManagedSession source, string outcome) =>
        Append("delete", SourceLabel(source), null, outcome);

    /// <summary>For UI refusals that never reach <see cref="LocalSessionOperations"/>.</summary>
    public void RecordRefusal(string operation, ManagedSession source, string outcome) =>
        Append(operation, SourceLabel(source), null, outcome);

    private static string SourceLabel(ManagedSession source) =>
        source.Agent.ToString().ToLowerInvariant() + "/" + Safe(source.SessionId) +
        " \"" + Safe(source.Title) + "\"";

    private void Append(string operation, string source, string? destination, string outcome)
    {
        try
        {
            var line =
                DateTimeOffset.UtcNow.ToString("O") + " " +
                operation + " " + source +
                (destination is null ? string.Empty : " -> " + Safe(destination)) +
                " " + Safe(outcome) + Environment.NewLine;
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
                                           or ArgumentException or NotSupportedException
                                           or System.Security.SecurityException)
        {
            // A diagnostic that throws would replace the operation being diagnosed.
        }
    }

    private static string Safe(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var kept = value
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Where(character => !char.IsControl(character));
        var collapsed = string.Join(' ', new string(kept.ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0) return "-";
        return collapsed.Length <= 200 ? collapsed : collapsed[..200];
    }
}

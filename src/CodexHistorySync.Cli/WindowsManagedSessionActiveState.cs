using System.Globalization;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Cli.Management;
using CodexHistorySync.Cli.Search;
using CodexHistorySync.Cli.Mcp;
using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Claude;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Continue;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.Grok;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Search;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Providers;
using CodexHistorySync.Core.State;
using CodexHistorySync.Core.Sync;
using CodexHistorySync.Git;
using CodexHistorySync.Windows;
using Spectre.Console;

namespace CodexHistorySync.Cli;

internal sealed class WindowsManagedSessionActiveState : IManagedSessionActiveState
{
    private const string GrokProcessName = "grok";
    private const string ClaudeProcessName = "claude";
    private static readonly IReadOnlySet<string> EmptyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly CodexPaths? codexPaths;
    private readonly GrokPaths? grokPaths;
    private readonly ClaudePaths? claudePaths;
    private readonly Func<int, string, bool> isNamedProcessRunning;
    private readonly Func<string, bool> isExclusiveLockHeld;
    private readonly Func<string, string, IReadOnlyList<string>> enumerateFiles;
    private readonly Func<string, string?> readAllText;
    private readonly Func<string, bool> isAnyNamedProcessRunning;
    private readonly Func<string, DateTime?> lastWriteTimeUtc;
    private readonly Func<DateTime> utcNow;

    public WindowsManagedSessionActiveState(CodexPaths? codexPaths, GrokPaths? grokPaths, ClaudePaths? claudePaths = null)
        : this(
            codexPaths,
            grokPaths,
            IsNamedProcessRunning,
            IsExclusiveLockHeld,
            EnumerateFiles,
            ReadAllText,
            claudePaths,
            IsAnyNamedProcessRunning,
            path => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null,
            () => DateTime.UtcNow)
    {
    }

    internal WindowsManagedSessionActiveState(
        CodexPaths? codexPaths,
        GrokPaths? grokPaths,
        Func<int, string, bool> isNamedProcessRunning,
        Func<string, bool> isExclusiveLockHeld,
        Func<string, string, IReadOnlyList<string>> enumerateFiles,
        Func<string, string?> readAllText,
        ClaudePaths? claudePaths = null,
        Func<string, bool>? isAnyNamedProcessRunning = null,
        Func<string, DateTime?>? lastWriteTimeUtc = null,
        Func<DateTime>? utcNow = null)
    {
        this.codexPaths = codexPaths;
        this.grokPaths = grokPaths;
        this.claudePaths = claudePaths;
        this.isAnyNamedProcessRunning = isAnyNamedProcessRunning ?? IsAnyNamedProcessRunning;
        this.lastWriteTimeUtc = lastWriteTimeUtc ?? (path => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null);
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.isNamedProcessRunning = isNamedProcessRunning
            ?? throw new ArgumentNullException(nameof(isNamedProcessRunning));
        this.isExclusiveLockHeld = isExclusiveLockHeld
            ?? throw new ArgumentNullException(nameof(isExclusiveLockHeld));
        this.enumerateFiles = enumerateFiles ?? throw new ArgumentNullException(nameof(enumerateFiles));
        this.readAllText = readAllText ?? throw new ArgumentNullException(nameof(readAllText));
    }

    public Task<bool> IsActiveAsync(
        ManagedAgent agent,
        string sessionId,
        string nativePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sessionId)) return Task.FromResult(true);
        return Task.FromResult(ReadActiveIds(agent).Contains(sessionId));
    }

    public Task<IReadOnlySet<string>> GetActiveSessionIdsAsync(
        ManagedAgent agent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadActiveIds(agent));
    }

    private IReadOnlySet<string> ReadActiveIds(ManagedAgent agent) => agent switch
    {
        ManagedAgent.Codex => ReadCodexActiveIds(),
        ManagedAgent.Grok => ReadGrokActiveIds(),
        ManagedAgent.Claude => ReadClaudeActiveIds(),
        _ => EmptyIds
    };

    /// <summary>
    /// Claude publishes no active-session file, so liveness is a running claude process plus a
    /// recent write, mirroring the scanner's rule (design D3). Without a running process nothing
    /// is active; with one, only transcripts touched inside the window are.
    /// </summary>
    private IReadOnlySet<string> ReadClaudeActiveIds()
    {
        if (claudePaths is null || !isAnyNamedProcessRunning(ClaudeProcessName)) return EmptyIds;

        var activeSince = utcNow() - ClaudeSessionScanner.DefaultActivityWindow;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in EnumerateDirectories(claudePaths.Projects))
            foreach (var path in enumerateFiles(project, "*.jsonl"))
            {
                var sessionId = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(sessionId)) continue;
                if (lastWriteTimeUtc(path) is { } written && written >= activeSince) ids.Add(sessionId);
            }

        return ids;
    }

    private IReadOnlySet<string> ReadGrokActiveIds()
    {
        if (grokPaths is null) return EmptyIds;
        var path = Path.Combine(grokPaths.Home, "active_sessions.json");
        var json = readAllText(path);
        if (json is null) return EmptyIds;

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in GrokActiveSessionList.Parse(json))
        {
            if (isNamedProcessRunning(record.ProcessId, GrokProcessName))
                ids.Add(record.SessionId);
        }

        return ids;
    }

    private IReadOnlySet<string> ReadCodexActiveIds()
    {
        if (codexPaths is null) return EmptyIds;
        var directory = Path.Combine(codexPaths.Home, "thread-writer-locks");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in enumerateFiles(directory, "*.lock"))
        {
            var name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.')) continue;
            var sessionId = Path.GetFileNameWithoutExtension(name);
            if (!IsSafeSessionId(sessionId)) continue;
            if (isExclusiveLockHeld(path)) ids.Add(sessionId);
        }

        return ids;
    }

    private static bool IsAnyNamedProcessRunning(string processName)
    {
        try
        {
            var processes = Process.GetProcessesByName(processName);
            try { return processes.Length != 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or
                                          UnauthorizedAccessException or SecurityException)
        {
            // Fail closed: an unreadable process list must not make a live session look copyable.
            return true;
        }
    }

    private static IReadOnlyList<string> EnumerateDirectories(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        try { return Directory.GetDirectories(directory); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static bool IsNamedProcessRunning(int processId, string processName)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
        catch (Exception exception) when (exception is Win32Exception or UnauthorizedAccessException or SecurityException)
        {
            return true;
        }
    }

    private static bool IsExclusiveLockHeld(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.SequentialScan);
            return false;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return true;
        }
    }

    private static IReadOnlyList<string> EnumerateFiles(string directory, string pattern)
    {
        if (!Directory.Exists(directory)) return [];
        return Directory.GetFiles(directory, pattern);
    }

    private static string? ReadAllText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    private static bool IsSafeSessionId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}


namespace CodexHistorySync.Core.Management;

public enum ManagedAgent { Codex, Grok, Claude }

public sealed record ManagedSession(
    ManagedAgent Agent,
    string SessionId,
    string NativePath,
    string Title,
    DateTimeOffset LastModifiedAt,
    bool IsActive,
    bool CanRead);

public sealed record SessionCatalogSnapshot(
    IReadOnlyList<ManagedSession> Codex,
    IReadOnlyList<ManagedSession> Grok,
    IReadOnlyList<ManagedSession> Claude)
{
    /// <summary>Kept so the two-agent call sites that predate Claude still compile.</summary>
    public SessionCatalogSnapshot(IReadOnlyList<ManagedSession> codex, IReadOnlyList<ManagedSession> grok)
        : this(codex, grok, []) { }

    public IReadOnlyList<ManagedSession> For(ManagedAgent agent) => agent switch
    {
        ManagedAgent.Codex => Codex,
        ManagedAgent.Grok => Grok,
        ManagedAgent.Claude => Claude,
        _ => throw new ArgumentOutOfRangeException(nameof(agent))
    };

    /// <summary>Agents with at least one session, in panel order.</summary>
    public IReadOnlyList<ManagedAgent> PopulatedAgents => ManagedAgents.All
        .Where(agent => For(agent).Count != 0).ToArray();
}

public static class ManagedAgents
{
    public static IReadOnlyList<ManagedAgent> All { get; } =
        [ManagedAgent.Codex, ManagedAgent.Grok, ManagedAgent.Claude];

    /// <summary>A session copied out of <paramref name="source"/> can land on any other agent.</summary>
    public static IReadOnlyList<ManagedAgent> Destinations(ManagedAgent source) =>
        All.Where(agent => agent != source).ToArray();
}

public interface ILocalSessionCatalog
{
    Task<SessionCatalogSnapshot> ScanAsync(CancellationToken cancellationToken);
}

public interface ILocalSessionOperations
{
    /// <summary>Copies to the only other configured agent; ambiguous with more than one.</summary>
    Task<string> CopyAsync(ManagedSession source, CancellationToken cancellationToken);

    Task<string> CopyAsync(ManagedSession source, ManagedAgent target, CancellationToken cancellationToken);

    /// <summary>Agents this session can be copied to right now, in panel order.</summary>
    IReadOnlyList<ManagedAgent> AvailableCopyTargets(ManagedSession source);
    Task DeleteAsync(ManagedSession source, CancellationToken cancellationToken);
}

public enum ManagedSessionOperationFailure { Copy, Delete }

public enum ManagedSessionFailureReason
{
    Unspecified,
    Active,
    Unreadable,
    Changed,
    DestinationUnavailable,
    Incompatible
}

public sealed class ManagedSessionOperationException : Exception
{
    public ManagedSessionOperationException(ManagedSessionOperationFailure failure)
        : this(failure, ManagedSessionFailureReason.Unspecified)
    {
    }

    public ManagedSessionOperationException(
        ManagedSessionOperationFailure failure,
        ManagedSessionFailureReason reason)
        : base(failure switch
        {
            ManagedSessionOperationFailure.Copy => "The session copy failed.",
            ManagedSessionOperationFailure.Delete => "The session deletion failed.",
            _ => "The session operation failed."
        })
    {
        Failure = failure;
        Reason = reason;
    }

    public ManagedSessionOperationFailure Failure { get; }
    public ManagedSessionFailureReason Reason { get; }
}

public interface IManagedSessionActiveState
{
    Task<IReadOnlySet<string>> GetActiveSessionIdsAsync(
        ManagedAgent agent,
        CancellationToken cancellationToken);

    Task<bool> IsActiveAsync(
        ManagedAgent agent,
        string sessionId,
        string nativePath,
        CancellationToken cancellationToken);
}

public interface IManagedSessionDirectoryDeleter
{
    Task DeleteAsync(string sessionsRoot, string sessionDirectory, CancellationToken cancellationToken);
}

internal static class ManagedSessionPathPolicy
{
    public static bool TryResolveConcreteTarget(
        string candidate,
        string root,
        bool expectDirectory,
        out string canonicalTarget)
    {
        canonicalTarget = string.Empty;
        try
        {
            var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            if (string.Equals(target, canonicalRoot, StringComparison.OrdinalIgnoreCase) ||
                !IsWithin(target, canonicalRoot) ||
                !Directory.Exists(canonicalRoot) ||
                File.GetAttributes(canonicalRoot).HasFlag(FileAttributes.ReparsePoint))
                return false;

            var current = target;
            while (!string.Equals(current, canonicalRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(current) && !Directory.Exists(current)) return false;
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return false;
                current = Path.GetDirectoryName(current) ?? string.Empty;
                if (string.IsNullOrEmpty(current)) return false;
            }

            var attributes = File.GetAttributes(target);
            if (attributes.HasFlag(FileAttributes.Directory) != expectDirectory) return false;
            canonicalTarget = target;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static bool IsWithin(string candidate, string root)
    {
        var canonicalCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return canonicalCandidate.StartsWith(canonicalRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase) ||
               canonicalCandidate.StartsWith(canonicalRoot + Path.AltDirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }
}

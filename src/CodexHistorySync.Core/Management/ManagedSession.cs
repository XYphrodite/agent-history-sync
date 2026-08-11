namespace CodexHistorySync.Core.Management;

public enum ManagedAgent { Codex, Grok }

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
    IReadOnlyList<ManagedSession> Grok);

public interface ILocalSessionCatalog
{
    Task<SessionCatalogSnapshot> ScanAsync(CancellationToken cancellationToken);
}

public interface ILocalSessionOperations
{
    Task<string> CopyAsync(ManagedSession source, CancellationToken cancellationToken);
    Task DeleteAsync(ManagedSession source, CancellationToken cancellationToken);
}

public enum ManagedSessionOperationFailure { Copy, Delete }

public sealed class ManagedSessionOperationException : Exception
{
    public ManagedSessionOperationException(ManagedSessionOperationFailure failure)
        : base(failure switch
        {
            ManagedSessionOperationFailure.Copy => "The session copy failed.",
            ManagedSessionOperationFailure.Delete => "The session deletion failed.",
            _ => "The session operation failed."
        })
    {
        Failure = failure;
    }

    public ManagedSessionOperationFailure Failure { get; }
}

public interface IManagedSessionActiveState
{
    Task<bool> IsAgentActiveAsync(
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

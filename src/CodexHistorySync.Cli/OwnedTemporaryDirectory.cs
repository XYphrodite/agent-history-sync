using System.Security;
using System.Text;

namespace CodexHistorySync.Cli;

internal sealed class OwnedTemporaryDirectory
{
    private readonly string temporaryRoot;
    private readonly string markerToken;
    private readonly string expectedPrefix;
    private readonly WindowsOwnedTreeDeleter.FileIdentity? rootIdentity;

    private OwnedTemporaryDirectory(string temporaryRoot, string rootPath, string expectedPrefix,
        string markerPath, string markerToken, WindowsOwnedTreeDeleter.FileIdentity? rootIdentity)
    {
        this.temporaryRoot = temporaryRoot;
        RootPath = rootPath;
        this.expectedPrefix = expectedPrefix;
        MarkerPath = markerPath;
        this.markerToken = markerToken;
        this.rootIdentity = rootIdentity;
    }

    public string RootPath { get; }
    public string MarkerPath { get; }

    public static OwnedTemporaryDirectory Create(string temporaryRoot, string expectedPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPrefix);
        if (expectedPrefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            expectedPrefix.Contains(Path.DirectorySeparatorChar) || expectedPrefix.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException("The temporary-directory prefix must be one safe path component.", nameof(expectedPrefix));

        var canonicalTemporaryRoot = CanonicalDirectory(temporaryRoot);
        var suffix = Guid.NewGuid().ToString("N");
        var rootPath = Path.Combine(canonicalTemporaryRoot, expectedPrefix + suffix);
        Directory.CreateDirectory(rootPath);
        var markerToken = Guid.NewGuid().ToString("N");
        var markerPath = Path.Combine(rootPath, ".codex-history-sync-owned-" + markerToken);
        try
        {
            using var marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough);
            marker.Write(Encoding.ASCII.GetBytes(markerToken));
            marker.Flush(flushToDisk: true);
            WindowsOwnedTreeDeleter.FileIdentity? identity =
                WindowsOwnedTreeDeleter.TryGetIdentity(rootPath, out var captured) ? captured : null;
            return new OwnedTemporaryDirectory(canonicalTemporaryRoot, rootPath, expectedPrefix, markerPath, markerToken,
                identity);
        }
        catch
        {
            try { Directory.Delete(rootPath, recursive: false); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or SecurityException) { }
            throw;
        }
    }

    public bool TryDelete(Func<bool>? afterValidation = null, Func<bool>? beforeFirstMutation = null)
    {
        if (!OperatingSystem.IsWindows() || rootIdentity is null) return false;
        try
        {
            ValidateRootLocation();
            return WindowsOwnedTreeDeleter.TryDelete(RootPath, rootIdentity.Value, Path.GetFileName(MarkerPath),
                markerToken, afterValidation, beforeFirstMutation);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          SecurityException or InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    private void ValidateRootLocation()
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootPath));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetDirectoryName(root)!));
        if (!PathEquals(parent, temporaryRoot))
            throw new InvalidDataException("Temporary directory is not an immediate child of its trusted root.");
        var name = Path.GetFileName(root);
        var suffix = name.StartsWith(expectedPrefix, StringComparison.Ordinal) ? name[expectedPrefix.Length..] : string.Empty;
        if (suffix.Length != 32 || suffix.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("Temporary directory name does not match the owned prefix.");
    }

    private static string CanonicalDirectory(string path)
    {
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var info = new DirectoryInfo(canonical);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The temporary root must be an existing concrete directory.");
        for (var current = info.Parent; current is not null; current = current.Parent)
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("The temporary root cannot have a reparse ancestor.");
        return canonical;
    }

    private static bool PathEquals(string left, string right) =>
        StringComparer.OrdinalIgnoreCase.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)));
}

using System.Security;
using System.Text;

namespace CodexHistorySync.Cli;

internal sealed class OwnedTemporaryDirectory
{
    private readonly string temporaryRoot;
    private readonly string markerToken;
    private readonly string expectedPrefix;

    private OwnedTemporaryDirectory(string temporaryRoot, string rootPath, string expectedPrefix,
        string markerPath, string markerToken)
    {
        this.temporaryRoot = temporaryRoot;
        RootPath = rootPath;
        this.expectedPrefix = expectedPrefix;
        MarkerPath = markerPath;
        this.markerToken = markerToken;
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
            return new OwnedTemporaryDirectory(canonicalTemporaryRoot, rootPath, expectedPrefix, markerPath, markerToken);
        }
        catch
        {
            try { Directory.Delete(rootPath, recursive: false); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or SecurityException) { }
            throw;
        }
    }

    public bool TryDelete(Func<bool>? afterValidation = null)
    {
        try
        {
            ValidateOwnedTree();
            if (afterValidation is not null && !afterValidation()) return false;
            var snapshot = ValidateOwnedTree();
            foreach (var file in snapshot.Files.Where(path => !PathEquals(path, MarkerPath))
                         .OrderByDescending(path => path.Length))
            {
                ValidateConcreteContainedEntry(file, expectDirectory: false);
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            foreach (var directory in snapshot.Directories.OrderByDescending(path => path.Length))
            {
                ValidateConcreteContainedEntry(directory, expectDirectory: true);
                Directory.Delete(directory, recursive: false);
            }
            ValidateMarker();
            File.SetAttributes(MarkerPath, FileAttributes.Normal);
            File.Delete(MarkerPath);
            ValidateRootIdentity();
            Directory.Delete(RootPath, recursive: false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          SecurityException or InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    private TreeSnapshot ValidateOwnedTree()
    {
        ValidateRootIdentity();
        var files = new List<string>();
        var directories = new List<string>();
        Collect(new DirectoryInfo(RootPath), files, directories, isRoot: true);
        ValidateMarker();
        return new TreeSnapshot(files, directories);
    }

    private void Collect(DirectoryInfo directory, List<string> files, List<string> directories, bool isRoot)
    {
        ValidateConcreteContainedEntry(directory.FullName, expectDirectory: true);
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            var fullPath = Path.GetFullPath(entry.FullName);
            EnsureContained(fullPath);
            var attributes = entry.Attributes;
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Reparse points are not owned temporary content.");
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                Collect(new DirectoryInfo(fullPath), files, directories, isRoot: false);
            }
            else
            {
                files.Add(fullPath);
            }
        }
        if (!isRoot) directories.Add(Path.GetFullPath(directory.FullName));
    }

    private void ValidateRootIdentity()
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootPath));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetDirectoryName(root)!));
        if (!PathEquals(parent, temporaryRoot)) throw new InvalidDataException("Temporary directory is not an immediate child of its trusted root.");
        var name = Path.GetFileName(root);
        var suffix = name.StartsWith(expectedPrefix, StringComparison.Ordinal) ? name[expectedPrefix.Length..] : string.Empty;
        if (suffix.Length != 32 || suffix.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("Temporary directory name does not match the owned prefix.");
        ValidateConcreteContainedEntry(root, expectDirectory: true);
    }

    private void ValidateMarker()
    {
        ValidateConcreteContainedEntry(MarkerPath, expectDirectory: false);
        using var stream = new FileStream(MarkerPath, FileMode.Open, FileAccess.Read, FileShare.None);
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: false);
        if (!StringComparer.Ordinal.Equals(reader.ReadToEnd(), markerToken))
            throw new InvalidDataException("Temporary directory ownership marker does not match this process.");
    }

    private void ValidateConcreteContainedEntry(string path, bool expectDirectory)
    {
        var fullPath = Path.GetFullPath(path);
        EnsureContained(fullPath);
        var attributes = File.GetAttributes(fullPath);
        if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory) != expectDirectory)
            throw new InvalidDataException("Temporary content changed type or became a reparse point.");
    }

    private void EnsureContained(string path)
    {
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(RootPath) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) && !PathEquals(path, RootPath))
            throw new InvalidDataException("Temporary content escaped its owned directory.");
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

    private sealed record TreeSnapshot(IReadOnlyList<string> Files, IReadOnlyList<string> Directories);
}

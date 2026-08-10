using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace CodexHistorySync.Core.Conversion;

public sealed record ConversationWriteResult(string SessionId, string NativePath);

public interface IConversationWriter
{
    Task<ConversationWriteResult> WriteAsync(PortableConversation conversation, CancellationToken cancellationToken);
}

internal static class ConversationWriterIdentity
{
    public static bool IsSourceSessionId(Guid generatedId, string sourceSessionId) =>
        Guid.TryParse(sourceSessionId, out var sourceId)
            ? generatedId == sourceId
            : string.Equals(generatedId.ToString(), sourceSessionId, StringComparison.OrdinalIgnoreCase);
}

internal interface IConversationPublisher
{
    void PublishFile(string stagingPath, string destinationPath, IConversationPublicationSeal seal);
    void PublishDirectory(string stagingPath, string destinationPath, IConversationPublicationSeal seal);
}

internal interface IConversationPublicationSeal
{
    void VerifyUnchanged();
}

internal sealed class SystemConversationPublisher : IConversationPublisher
{
    public static SystemConversationPublisher Instance { get; } = new();

    private SystemConversationPublisher()
    {
    }

    public void PublishFile(string stagingPath, string destinationPath, IConversationPublicationSeal seal)
    {
        seal.VerifyUnchanged();
        File.Move(stagingPath, destinationPath);
    }

    public void PublishDirectory(string stagingPath, string destinationPath, IConversationPublicationSeal seal)
    {
        seal.VerifyUnchanged();
        Directory.Move(stagingPath, destinationPath);
    }
}

internal interface IConversationStagingDirectoryFactory
{
    IConversationStagingDirectory Create(string parentDirectory);
}

internal interface IConversationStagingDirectory
{
    string RootPath { get; }
    string DirectoryPath(params string[] components);
    string FilePath(params string[] components);
    IConversationPublicationSeal Seal();
    bool TryDelete();
}

internal sealed class SystemConversationStagingDirectoryFactory : IConversationStagingDirectoryFactory
{
    public static SystemConversationStagingDirectoryFactory Instance { get; } = new();

    private SystemConversationStagingDirectoryFactory()
    {
    }

    public IConversationStagingDirectory Create(string parentDirectory) =>
        SystemConversationStagingDirectory.Create(parentDirectory);
}

internal sealed class SystemConversationStagingDirectory : IConversationStagingDirectory
{
    private const string Prefix = ".agent-sync-";
    private readonly string parentDirectory;
    private readonly string markerPath;
    private readonly byte[] markerToken;
    private readonly HashSet<string> ownedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> ownedDirectories = new(StringComparer.OrdinalIgnoreCase);
    private FileStream? markerHandle;
    private bool cleanupAttempted;

    private SystemConversationStagingDirectory(
        string parentDirectory,
        string rootPath,
        string markerPath,
        byte[] markerToken,
        FileStream markerHandle)
    {
        this.parentDirectory = parentDirectory;
        RootPath = rootPath;
        this.markerPath = markerPath;
        this.markerToken = markerToken;
        this.markerHandle = markerHandle;
    }

    public string RootPath { get; }

    public static SystemConversationStagingDirectory Create(
        string parentDirectory,
        Action<FileStream>? afterMarkerFlushed = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentDirectory));
        var parentInfo = new DirectoryInfo(parent);
        if (!parentInfo.Exists || parentInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The staging parent must be an existing concrete directory.");

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var root = Path.Combine(parent, Prefix + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(root) || File.Exists(root)) continue;
            Directory.CreateDirectory(root);
            var token = Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N"));
            var marker = Path.Combine(root, ".codex-history-sync-owned");
            FileStream? handle = null;
            try
            {
                handle = new FileStream(marker, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 4096, FileOptions.WriteThrough);
                handle.Write(token);
                handle.Flush(flushToDisk: true);
                afterMarkerFlushed?.Invoke(handle);
                return new SystemConversationStagingDirectory(parent, root, marker, token, handle);
            }
            catch
            {
                handle?.Dispose();
                try
                {
                    if (File.Exists(marker)) File.Delete(marker);
                    Directory.Delete(root, recursive: false);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or SecurityException) { }
                throw;
            }
        }

        throw new IOException("Unable to allocate an owned staging directory.");
    }

    public string DirectoryPath(params string[] components)
    {
        var path = OwnedPath(components);
        RegisterParentDirectories(path);
        ownedDirectories.Add(path);
        return path;
    }

    public string FilePath(params string[] components)
    {
        var path = OwnedPath(components);
        RegisterParentDirectories(path);
        ownedFiles.Add(path);
        return path;
    }

    public IConversationPublicationSeal Seal()
    {
        EnsureUnchanged();
        return new PublicationSeal(
            this,
            ownedFiles.ToDictionary(path => path, HashFile, StringComparer.OrdinalIgnoreCase));
    }

    public bool TryDelete()
    {
        if (cleanupAttempted) return false;
        cleanupAttempted = true;
        try
        {
            ValidateRoot();
            if (!MarkerMatches() || !TreeContainsOnlyOwnedEntries()) return Abandon();

            foreach (var file in ownedFiles)
            {
                if (!File.Exists(file)) continue;
                if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) return Abandon();
                File.Delete(file);
            }

            foreach (var directory in ownedDirectories.OrderByDescending(path => path.Length))
            {
                if (!Directory.Exists(directory)) continue;
                if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) return Abandon();
                Directory.Delete(directory, recursive: false);
            }

            markerHandle!.Dispose();
            markerHandle = null;
            File.Delete(markerPath);
            Directory.Delete(RootPath, recursive: false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or
                                          InvalidDataException or ArgumentException)
        {
            return Abandon();
        }
    }

    private string OwnedPath(IReadOnlyList<string> components)
    {
        if (components.Count == 0) throw new ArgumentException("At least one staging path component is required.", nameof(components));
        var path = RootPath;
        foreach (var component in components)
        {
            if (string.IsNullOrWhiteSpace(component) || component is "." or ".." ||
                !string.Equals(Path.GetFileName(component), component, StringComparison.Ordinal) ||
                component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("A staging path component is invalid.", nameof(components));
            path = Path.Combine(path, component);
        }
        return Path.GetFullPath(path);
    }

    private void RegisterParentDirectories(string path)
    {
        for (var parent = Path.GetDirectoryName(path); parent is not null &&
             !string.Equals(parent, RootPath, StringComparison.OrdinalIgnoreCase); parent = Path.GetDirectoryName(parent))
            ownedDirectories.Add(parent);
    }

    private void ValidateRoot()
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootPath));
        if (!string.Equals(Path.GetDirectoryName(root), parentDirectory, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(root).StartsWith(Prefix, StringComparison.Ordinal) ||
            !Directory.Exists(root) || File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The staging directory is no longer the owned concrete path.");
    }

    private bool MarkerMatches()
    {
        if (markerHandle is null || !File.Exists(markerPath) ||
            File.GetAttributes(markerPath).HasFlag(FileAttributes.ReparsePoint)) return false;
        markerHandle.Position = 0;
        var actual = new byte[markerToken.Length];
        return markerHandle.Read(actual, 0, actual.Length) == actual.Length && actual.SequenceEqual(markerToken) &&
               markerHandle.ReadByte() == -1;
    }

    private bool TreeContainsOnlyOwnedEntries()
    {
        var expected = new HashSet<string>(ownedFiles, StringComparer.OrdinalIgnoreCase) { markerPath };
        expected.UnionWith(ownedDirectories);
        var pending = new Stack<string>();
        pending.Push(RootPath);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (!expected.Contains(entry)) return false;
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
                if (attributes.HasFlag(FileAttributes.Directory)) pending.Push(entry);
            }
        }
        return true;
    }

    private void EnsureUnchanged(IReadOnlyDictionary<string, byte[]>? expectedHashes = null)
    {
        const string message = "The staged conversation changed after validation.";
        try
        {
            ValidateRoot();
            if (!MarkerMatches() || !TreeContainsOnlyOwnedEntries() ||
                ownedDirectories.Any(directory =>
                    !Directory.Exists(directory) ||
                    File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) ||
                ownedFiles.Any(file =>
                    !File.Exists(file) ||
                    File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) ||
                expectedHashes is not null && expectedHashes.Any(expected =>
                    !HashFile(expected.Key).SequenceEqual(expected.Value)))
                throw new InvalidDataException(message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or
                                          ArgumentException)
        {
            throw new InvalidDataException(message, exception);
        }
    }

    private static byte[] HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return SHA256.HashData(stream);
    }

    private sealed class PublicationSeal(
        SystemConversationStagingDirectory owner,
        IReadOnlyDictionary<string, byte[]> expectedHashes) : IConversationPublicationSeal
    {
        public void VerifyUnchanged() => owner.EnsureUnchanged(expectedHashes);
    }

    private bool Abandon()
    {
        markerHandle?.Dispose();
        markerHandle = null;
        return false;
    }
}

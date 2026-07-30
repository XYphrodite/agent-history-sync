namespace CodexHistorySync.Core.Sync;

internal sealed class RepositorySyncLock : IAsyncDisposable
{
    internal const string FileName = ".sync.lock";
    private readonly FileStream _handle;

    private RepositorySyncLock(FileStream handle) => _handle = handle;

    public static string CanonicalStateIdentity(string statePath)
    {
        var fullPath = Path.GetFullPath(statePath);
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    public static async Task<RepositorySyncLock> AcquireAsync(string statePath, CancellationToken ct)
    {
        var identity = CanonicalStateIdentity(statePath);
        var directory = Path.GetDirectoryName(identity) ?? throw new ArgumentException("The repository state path has no directory.", nameof(statePath));
        Directory.CreateDirectory(directory);
        var lockPath = Path.Combine(directory, FileName);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var handle = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.Asynchronous | FileOptions.WriteThrough);
                return new RepositorySyncLock(handle);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
            }
        }
    }

    public ValueTask DisposeAsync() => _handle.DisposeAsync();
}

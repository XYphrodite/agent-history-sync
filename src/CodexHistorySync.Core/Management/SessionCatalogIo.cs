using System.Text;

namespace CodexHistorySync.Core.Management;

internal readonly record struct BoundedTextRead(
    string Text,
    bool IsComplete,
    int BytesRead,
    long FileLength);

internal interface ISessionCatalogIo
{
    IReadOnlyList<string> EnumerateFiles(string root, string pattern);
    IReadOnlyList<string> EnumerateDirectories(string root);
    bool FileExists(string path);
    DateTimeOffset LastWriteTime(string path);
    Task<BoundedTextRead> ReadPrefixAsync(
        string path, int maximumBytes, CancellationToken cancellationToken);
    Task<BoundedTextRead> ReadTailAsync(
        string path, int maximumBytes, CancellationToken cancellationToken);
}

internal sealed class SystemSessionCatalogIo : ISessionCatalogIo
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly Action<string>? afterLengthObserved;
    private readonly Func<string, int, Stream> openRead;

    public SystemSessionCatalogIo() : this(OpenRead)
    {
    }

    internal SystemSessionCatalogIo(Action<string> afterLengthObserved) : this(OpenRead)
    {
        this.afterLengthObserved = afterLengthObserved;
    }

    internal SystemSessionCatalogIo(Func<string, int, Stream> openRead)
    {
        this.openRead = openRead;
    }

    public IReadOnlyList<string> EnumerateFiles(string root, string pattern) =>
        Enumerate(root, pattern, SearchTarget.Files);

    public IReadOnlyList<string> EnumerateDirectories(string root) =>
        Enumerate(root, "*", SearchTarget.Directories);

    public bool FileExists(string path) => File.Exists(path);

    public DateTimeOffset LastWriteTime(string path) => File.GetLastWriteTimeUtc(path);

    public Task<BoundedTextRead> ReadPrefixAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken) =>
        ReadAsync(path, maximumBytes, readTail: false, cancellationToken);

    public Task<BoundedTextRead> ReadTailAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken) =>
        ReadAsync(path, maximumBytes, readTail: true, cancellationToken);

    private static IReadOnlyList<string> Enumerate(string root, string pattern, SearchTarget target)
    {
        try
        {
            if (!Directory.Exists(root) || IsReparsePoint(root))
                return [];

            var options = new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
            };
            var entries = target == SearchTarget.Files
                ? Directory.EnumerateFiles(root, pattern, options)
                : Directory.EnumerateDirectories(root, pattern, options);
            return entries.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private async Task<BoundedTextRead> ReadAsync(
        string path,
        int maximumBytes,
        bool readTail,
        CancellationToken cancellationToken)
    {
        ValidateMaximumBytes(maximumBytes);
        await using var stream = openRead(path, maximumBytes);
        var fileLength = stream.Length;
        afterLengthObserved?.Invoke(path);
        var logicalTailOffset = readTail ? Math.Max(0, fileLength - maximumBytes) : 0;
        var tailContextBytes = readTail
            ? (int)Math.Min(Math.Min(3, maximumBytes - 1), logicalTailOffset)
            : 0;
        var physicalOffset = logicalTailOffset - tailContextBytes;
        stream.Seek(physicalOffset, SeekOrigin.Begin);
        var buffer = new byte[(int)Math.Min(maximumBytes, Math.Max(0, fileLength - physicalOffset))];
        var bytesRead = 0;
        while (bytesRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(bytesRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            bytesRead += read;
        }
        var finalFileLength = stream.Length;
        var textStart = readTail
            ? tailContextBytes + SkipLeadingContinuationBytes(buffer, tailContextBytes, bytesRead)
            : 0;
        var textLength = readTail ? bytesRead - textStart :
            bytesRead < fileLength ? TrimIncompleteTrailingSequence(buffer, bytesRead) : bytesRead;
        return new BoundedTextRead(
            StrictUtf8.GetString(buffer, textStart, textLength),
            physicalOffset == 0 && bytesRead == fileLength && fileLength == finalFileLength,
            bytesRead,
            finalFileLength);
    }

    private static Stream OpenRead(string path, int bufferSize) => new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        bufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static int SkipLeadingContinuationBytes(byte[] buffer, int boundary, int length)
    {
        var skipped = 0;
        while (boundary + skipped < length && skipped < 3 && IsContinuationByte(buffer[boundary + skipped]))
            skipped++;

        if (skipped == 0)
            return 0;

        var continuationCountBeforeOffset = 0;
        var position = boundary - 1;
        while (position >= 0 && continuationCountBeforeOffset < 3)
        {
            var value = buffer[position];
            if (!IsContinuationByte(value))
            {
                var sequenceLength = Utf8SequenceLength(value);
                return sequenceLength == 1 + continuationCountBeforeOffset + skipped ? skipped : 0;
            }

            continuationCountBeforeOffset++;
            position--;
        }

        return 0;
    }

    private static int TrimIncompleteTrailingSequence(byte[] buffer, int length)
    {
        var sequenceStart = length - 1;
        while (sequenceStart >= 0 && IsContinuationByte(buffer[sequenceStart]))
            sequenceStart--;

        if (sequenceStart < 0)
            return length;

        var sequenceLength = Utf8SequenceLength(buffer[sequenceStart]);
        return sequenceLength > 0 && length - sequenceStart < sequenceLength
            ? sequenceStart
            : length;
    }

    private static bool IsContinuationByte(byte value) => (value & 0b1100_0000) == 0b1000_0000;

    private static int Utf8SequenceLength(byte value) => value switch
    {
        >= 0b1100_0010 and <= 0b1101_1111 => 2,
        >= 0b1110_0000 and <= 0b1110_1111 => 3,
        >= 0b1111_0000 and <= 0b1111_0100 => 4,
        _ => 0,
    };

    private static void ValidateMaximumBytes(int maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
    }

    private enum SearchTarget
    {
        Files,
        Directories,
    }
}

internal sealed class SessionCatalogReadLimiter(int maximumConcurrency) : IDisposable
{
    private readonly SemaphoreSlim gate = maximumConcurrency > 0
        ? new(maximumConcurrency, maximumConcurrency)
        : throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));

    public async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await operation(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public void Dispose() => gate.Dispose();
}

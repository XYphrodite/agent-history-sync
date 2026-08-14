using System.Text;

using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Core.Tests.Management;

public sealed class SessionCatalogIoTests
{
    [Fact]
    public async Task PrefixAndTailNeverConsumeMoreThanTheBudget()
    {
        // Reading beyond the requested bound would make large histories unbounded catalog work.
        await using var fixture = new CatalogIoFixture();
        var path = await fixture.WriteAsync(new string('a', 200_000));
        var io = new SystemSessionCatalogIo();

        var prefix = await io.ReadPrefixAsync(path, 64 * 1024, CancellationToken.None);
        var tail = await io.ReadTailAsync(path, 64 * 1024, CancellationToken.None);

        Assert.Equal(64 * 1024, prefix.BytesRead);
        Assert.Equal(64 * 1024, tail.BytesRead);
        Assert.False(prefix.IsComplete);
        Assert.False(tail.IsComplete);
    }

    [Fact]
    public async Task ReadTailAsyncNeverReadsMorePhysicalBytesThanTheBudget()
    {
        // Context reads beyond the tail buffer would make the advertised byte bound inaccurate.
        await using var fixture = new CatalogIoFixture();
        var content = new byte[64 * 1024 + 1];
        content[0] = 0xE2;
        content[1] = 0x82;
        content[2] = 0xAC;
        Array.Fill(content, (byte)'a', 3, content.Length - 3);
        var path = await fixture.WriteBytesAsync(content);
        var physicalBytesRead = 0L;
        var io = new SystemSessionCatalogIo((sourcePath, bufferSize) => new RecordingStream(
            new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize),
            bytes => physicalBytesRead += bytes));

        var read = await io.ReadTailAsync(path, 64 * 1024, CancellationToken.None);

        Assert.Equal(64 * 1024, read.BytesRead);
        Assert.Equal(64 * 1024, physicalBytesRead);
    }

    [Fact]
    public async Task ReadTailAsyncRetainsTheExactEofSuffixWithinThePhysicalBudget()
    {
        // Reserving boundary context by ending early would silently drop this final JSONL record.
        await using var fixture = new CatalogIoFixture();
        const string finalRecord = "{\"final\":true}\n";
        var path = await fixture.WriteAsync(new string('a', 100) + "\n" + finalRecord);
        var physicalBytesRead = 0L;
        var io = new SystemSessionCatalogIo((sourcePath, bufferSize) => new RecordingStream(
            new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize),
            bytes => physicalBytesRead += bytes));

        var read = await io.ReadTailAsync(path, 32, CancellationToken.None);

        Assert.Equal(32, read.BytesRead);
        Assert.Equal(32, physicalBytesRead);
        Assert.EndsWith(finalRecord, read.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadPrefixAsyncTrimsAnIncompleteTrailingUtf8Sequence()
    {
        // Passing an incomplete terminal sequence to strict UTF-8 would reject an otherwise valid bounded prefix.
        await using var fixture = new CatalogIoFixture();
        var path = await fixture.WriteBytesAsync([0x61, 0xE2, 0x82, 0xAC]);

        var read = await new SystemSessionCatalogIo().ReadPrefixAsync(path, 2, CancellationToken.None);

        Assert.Equal("a", read.Text);
        Assert.Equal(2, read.BytesRead);
        Assert.False(read.IsComplete);
    }

    [Fact]
    public async Task ReadTailAsyncSkipsAnIncompleteLeadingUtf8Sequence()
    {
        // Decoding a tail beginning in a multibyte character must retain the valid text after that character.
        await using var fixture = new CatalogIoFixture();
        var path = await fixture.WriteBytesAsync([0xE2, 0x82, 0xAC, 0x78, 0x79]);

        var read = await new SystemSessionCatalogIo().ReadTailAsync(path, 4, CancellationToken.None);

        Assert.Equal("xy", read.Text);
        Assert.Equal(4, read.BytesRead);
        Assert.False(read.IsComplete);
    }

    [Fact]
    public async Task ReadTailAsyncTreatsLeadingContinuationAsABoundaryFragment()
    {
        // The initial partial JSONL line is discarded by the caller, including this untrusted boundary fragment.
        await using var fixture = new CatalogIoFixture();
        var path = await fixture.WriteBytesAsync([0x61, 0x80, 0x62]);

        var read = await new SystemSessionCatalogIo().ReadTailAsync(path, 2, CancellationToken.None);

        Assert.Equal("b", read.Text);
    }

    [Fact]
    public async Task ReadTailAsyncRejectsMalformedContinuationAfterTheBoundaryFragment()
    {
        // Strict decoding applies after the untrusted leading tail fragment.
        await using var fixture = new CatalogIoFixture();
        var path = await fixture.WriteBytesAsync([0x61, 0x0A, 0x80, 0x62]);

        await Assert.ThrowsAsync<DecoderFallbackException>(() =>
            new SystemSessionCatalogIo().ReadTailAsync(path, 3, CancellationToken.None));
    }

    [Fact]
    public async Task ReadPrefixAsyncMarksAFileThatGrowsDuringReadAsIncomplete()
    {
        // Treating the pre-read length as final would falsely claim this raced read is a complete file snapshot.
        await using var fixture = new CatalogIoFixture();
        var path = await fixture.WriteAsync("a");
        var io = new SystemSessionCatalogIo(changedPath => File.AppendAllText(changedPath, "b"));

        var read = await io.ReadPrefixAsync(path, 2, CancellationToken.None);

        Assert.Equal("a", read.Text);
        Assert.False(read.IsComplete);
    }

    [Fact]
    public async Task ReadPrefixAsyncRejectsMalformedUtf8InsideTheRetainedWindow()
    {
        await using var fixture = new CatalogIoFixture();
        var path = await fixture.WriteBytesAsync([0x61, 0xFF]);

        await Assert.ThrowsAsync<DecoderFallbackException>(() =>
            new SystemSessionCatalogIo().ReadPrefixAsync(path, 2, CancellationToken.None));
    }

    [Fact]
    public async Task ReadPrefixAsyncRejectsAnIncompleteSequenceWhenTheWholeFileIsRetained()
    {
        // Trimming is only valid at a truncated read boundary, not when malformed bytes are wholly retained.
        await using var fixture = new CatalogIoFixture();
        var path = await fixture.WriteBytesAsync([0x61, 0xE2]);

        await Assert.ThrowsAsync<DecoderFallbackException>(() =>
            new SystemSessionCatalogIo().ReadPrefixAsync(path, 2, CancellationToken.None));
    }

    [Fact]
    public async Task RunAsyncLimitsConcurrentReadsToConfiguredMaximum()
    {
        // Releasing the semaphore before an operation completes would allow more than eight reads at once.
        using var limiter = new SessionCatalogReadLimiter(8);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = 0;
        var peak = 0;
        var operations = Enumerable.Range(0, 24).Select(_ => limiter.RunAsync(async token =>
        {
            var observed = Interlocked.Increment(ref current);
            UpdateMaximum(ref peak, observed);
            if (observed == 8)
                entered.TrySetResult();

            await release.Task.WaitAsync(token);
            Interlocked.Decrement(ref current);
            return 0;
        }, cancellation.Token)).ToArray();

        await entered.Task.WaitAsync(cancellation.Token);
        release.TrySetResult();
        await Task.WhenAll(operations);

        Assert.Equal(8, peak);
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        while (true)
        {
            var observed = Volatile.Read(ref maximum);
            if (candidate <= observed || Interlocked.CompareExchange(ref maximum, candidate, observed) == observed)
                return;
        }
    }

    private sealed class CatalogIoFixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public async Task<string> WriteAsync(string content)
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "history.jsonl");
            await File.WriteAllTextAsync(path, content);
            return path;
        }

        public async Task<string> WriteBytesAsync(byte[] content)
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "history.jsonl");
            await File.WriteAllBytesAsync(path, content);
            return path;
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingStream(Stream inner, Action<int> recordRead) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            recordRead(read);
            return read;
        }

        public override int ReadByte()
        {
            var value = inner.ReadByte();
            if (value >= 0)
                recordRead(1);

            return value;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            recordRead(read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}

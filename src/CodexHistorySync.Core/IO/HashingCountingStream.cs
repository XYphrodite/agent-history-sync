using System.Security.Cryptography;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.IO;

/// <summary>
/// Write-only stream sink that computes a SHA-256 content hash and total byte length without buffering payloads in memory.
/// </summary>
internal sealed class HashingCountingStream : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _disposed;

    public long BytesWritten { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => BytesWritten;

    public override long Position
    {
        get => BytesWritten;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty) return;
        _hash.AppendData(buffer);
        BytesWritten += buffer.Length;
    }

    public (ContentHash Hash, long Length) Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        _hash.GetHashAndReset(digest);
        return (new ContentHash(Convert.ToHexStringLower(digest)), BytesWritten);
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing) _hash.Dispose();
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}

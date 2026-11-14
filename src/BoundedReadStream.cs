using System.IO;

namespace CodexTraffic;

// The caller owns the underlying response stream. The extra byte distinguishes
// an exactly-at-limit document from an oversized one, including chunked bodies.
internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _limit;
    private long _read;

    internal BoundedReadStream(Stream inner, long limit)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        _inner = inner;
        _limit = limit;
    }

    private int Allowed(int count)
    {
        if (_read > _limit) throw new InvalidDataException("Proxy response exceeds the byte limit.");
        long remaining = _limit - _read;
        return remaining >= count ? count : (int)remaining + 1;
    }

    private int Count(int count)
    {
        _read += count;
        if (_read > _limit) throw new InvalidDataException("Proxy response exceeds the byte limit.");
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer) => Count(_inner.Read(buffer[..Allowed(buffer.Length)]));
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => Count(await _inner.ReadAsync(buffer[..Allowed(buffer.Length)], cancellationToken).ConfigureAwait(false));

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

namespace SaveEditor.Ui.Workflow;

/// <summary>
/// The stream a codec serializes into: bounded, in memory, and nowhere near the destination.
/// </summary>
/// <remarks>
/// <para>
/// Handing a codec a stream over the destination file would make a mid-serialize failure
/// indistinguishable from a successful write, and would make the destination's contents a
/// function of how far a buggy codec got. Serialization therefore completes in full, into
/// memory, and is size-checked before any exclusively-created temporary file is opened —
/// so a codec that throws halfway has not caused a single byte to be created anywhere
/// (<c>PLAN.md</c> §7 step 12, finding B5).
/// </para>
/// <para>
/// Seeking and reading back are permitted, because a codec that patches a length or a
/// checksum into its own header is ordinary and not suspicious. Growing past the bound is
/// not: it fails the write rather than consuming the machine's memory on behalf of a
/// runaway serializer.
/// </para>
/// </remarks>
internal sealed class BoundedWriteStream : Stream
{
    private readonly MemoryStream _buffer;
    private readonly long _limit;

    internal BoundedWriteStream(long limit, int initialCapacity = 0)
    {
        _limit = limit;
        _buffer = initialCapacity > 0 ? new MemoryStream(initialCapacity) : new MemoryStream();
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => true;

    public override long Length => _buffer.Length;

    public override long Position
    {
        get => _buffer.Position;
        set => _buffer.Position = value;
    }

    internal byte[] ToArray() => _buffer.ToArray();

    public override void Flush() => _buffer.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _buffer.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => _buffer.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _buffer.Read(buffer);

    public override long Seek(long offset, SeekOrigin origin) => _buffer.Seek(offset, origin);

    public override void SetLength(long value)
    {
        Guard(value);
        _buffer.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Guard(_buffer.Position + count);
        _buffer.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Guard(_buffer.Position + buffer.Length);
        _buffer.Write(buffer);
    }

    public override void WriteByte(byte value)
    {
        Guard(_buffer.Position + 1);
        _buffer.WriteByte(value);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Guard(_buffer.Position + count);
        return _buffer.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Guard(_buffer.Position + buffer.Length);
        return _buffer.WriteAsync(buffer, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _buffer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Guard(long wouldBe)
    {
        if (wouldBe > _limit)
        {
            throw new InvalidOperationException(
                $"The codec tried to serialize more than the configured maximum of {_limit} bytes.");
        }
    }
}

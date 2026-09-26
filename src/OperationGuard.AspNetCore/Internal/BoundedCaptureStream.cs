using System.Security.Cryptography;

namespace OperationGuard.AspNetCore.Internal;

internal sealed class BoundedCaptureStream(Stream destination, int captureLimit) : Stream
{
    private readonly MemoryStream _capture = new(Math.Min(captureLimit, 64 * 1024));
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _totalBytes;
    private bool _digestFinalized;
    private string? _digest;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _totalBytes;
    public override long Position { get => _totalBytes; set => throw new NotSupportedException(); }

    public bool BodyAvailable => _totalBytes <= captureLimit;

    public byte[]? CapturedBody => BodyAvailable ? _capture.ToArray() : null;

    public string Digest
    {
        get
        {
            if (!_digestFinalized)
            {
                _digest = Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
                _digestFinalized = true;
            }

            return _digest!;
        }
    }

    public override void Flush() => destination.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureNotFinalized();
        destination.Write(buffer);
        Capture(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureNotFinalized();
        await destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Capture(buffer.Span);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _capture.Dispose();
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private void Capture(ReadOnlySpan<byte> buffer)
    {
        _hash.AppendData(buffer);
        var remaining = captureLimit - _capture.Length;
        if (remaining > 0)
        {
            _capture.Write(buffer[..(int)Math.Min(remaining, buffer.Length)]);
        }

        _totalBytes += buffer.Length;
    }

    private void EnsureNotFinalized()
    {
        if (_digestFinalized)
        {
            throw new InvalidOperationException("The response cannot be written after its digest is finalized.");
        }
    }
}

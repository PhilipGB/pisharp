using PiSharp.Cli.Sessions;

namespace PiSharp.Tests;

public sealed class BoundedSessionFileReaderTests
{
    [Fact]
    public void SyncReadRejectsGrowthAfterLengthCheckWithoutReadingTheGrowingTail()
    {
        using var stream = new GrowingReadStream([1, 2, 3, 4], [5, 6, 7, 8]);

        var error = Assert.Throws<InvalidDataException>(() => BoundedSessionFileReader.ReadAllBytes(stream, 4,
            "too large", "changed while read"));

        Assert.Equal("changed while read", error.Message);
        Assert.Equal(5, stream.BytesRead);
    }

    [Fact]
    public async Task AsyncReadRejectsGrowthAfterLengthCheckWithoutReadingTheGrowingTail()
    {
        using var stream = new GrowingReadStream([1, 2, 3, 4], [5, 6, 7, 8]);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => BoundedSessionFileReader.ReadAllBytesAsync(
            stream, 4, "too large", "changed while read", CancellationToken.None));

        Assert.Equal("changed while read", error.Message);
        Assert.Equal(5, stream.BytesRead);
    }

    private sealed class GrowingReadStream : Stream
    {
        private readonly byte[] _initialContents;
        private readonly byte[] _appendedContents;
        private byte[] _contents;
        private int _position;
        private bool _lengthWasRead;
        private bool _grew;

        public GrowingReadStream(byte[] initialContents, byte[] appendedContents)
        {
            _initialContents = initialContents;
            _appendedContents = appendedContents;
            _contents = initialContents;
        }

        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length
        {
            get
            {
                _lengthWasRead = true;
                return _initialContents.Length;
            }
        }

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            GrowAfterLengthCheck();
            var read = Math.Min(count, _contents.Length - _position);
            Array.Copy(_contents, _position, buffer, offset, read);
            _position += read;
            BytesRead += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GrowAfterLengthCheck();
            var read = Math.Min(buffer.Length, _contents.Length - _position);
            _contents.AsMemory(_position, read).CopyTo(buffer);
            _position += read;
            BytesRead += read;
            return ValueTask.FromResult(read);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void GrowAfterLengthCheck()
        {
            if (!_lengthWasRead || _grew) return;
            _contents = [.. _contents, .. _appendedContents];
            _grew = true;
        }
    }
}

using SocketIO.Net.Abstractions;
using System;
using System.Collections.Generic;
using System.Text;

namespace SocketIO.Net.Protocol
{
    public sealed class FixedLengthCodec : IFrameCodec
    {
        private readonly int _size;

        public FixedLengthCodec(int frameSize)
        {
            if (frameSize <= 0) throw new ArgumentOutOfRangeException(nameof(frameSize));
            _size = frameSize;
        }

        public ReadOnlyMemory<byte> Encode(ReadOnlySpan<byte> payload)
            => payload.ToArray();

        public bool TryDecode(ref ReadOnlySpan<byte> buffer, out ReadOnlyMemory<byte> frame)
        {
            frame = default;

            if (buffer.Length < _size) return false;

            frame = buffer.Slice(0, _size).ToArray();
            buffer = buffer.Slice(_size);
            return true;
        }
    }
}

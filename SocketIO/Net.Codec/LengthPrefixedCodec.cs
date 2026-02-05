using SocketIO.Net.Abstractions;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace SocketIO.Net.Protocol
{
    public sealed class LengthPrefixedCodec : IFrameCodec
    {
        public ReadOnlyMemory<byte> Encode(ReadOnlySpan<byte> payload)
        {
            var buffer = new byte[4 + payload.Length];
            BinaryPrimitives.WriteInt32BigEndian(buffer, payload.Length);
            payload.CopyTo(buffer.AsSpan(4));
            return buffer;
        }

        public bool TryDecode(ref ReadOnlySpan<byte> buffer, out ReadOnlyMemory<byte> frame)
        {
            frame = default;

            if (buffer.Length < 4)
                return false;

            int len = BinaryPrimitives.ReadInt32BigEndian(buffer);
            if (buffer.Length < 4 + len)
                return false;

            frame = buffer.Slice(4, len).ToArray();
            buffer = buffer.Slice(4 + len);
            return true;
        }
    }

}

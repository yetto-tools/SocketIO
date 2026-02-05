using SocketIO.Net.Abstractions;
using System;
using System.Collections.Generic;
using System.Text;

namespace SocketIO.Net.Protocol
{
    public sealed class StxEtxCodec : IFrameCodec
    {
        private readonly byte _stx;
        private readonly byte _etx;

        public StxEtxCodec(byte stx = 0x02, byte etx = 0x03)
        {
            _stx = stx;
            _etx = etx;
        }

        public ReadOnlyMemory<byte> Encode(ReadOnlySpan<byte> payload)
        {
            var arr = new byte[payload.Length + 2];
            arr[0] = _stx;
            payload.CopyTo(arr.AsSpan(1));
            arr[^1] = _etx;
            return arr;
        }

        public bool TryDecode(ref ReadOnlySpan<byte> buffer, out ReadOnlyMemory<byte> frame)
        {
            frame = default;

            int start = buffer.IndexOf(_stx);
            if (start < 0) return false;

            int end = buffer.Slice(start + 1).IndexOf(_etx);
            if (end < 0) return false;

            var slice = buffer.Slice(start + 1, end);
            frame = slice.ToArray();

            buffer = buffer.Slice(start + 1 + end + 1);
            return true;
        }
    }
}

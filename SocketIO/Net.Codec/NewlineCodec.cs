using SocketIO.Net.Abstractions;
using System;
using System.Collections.Generic;
using System.Text;

namespace SocketIO.Net.Protocol
{
    public sealed class NewlineCodec : IFrameCodec
    {
        public ReadOnlyMemory<byte> Encode(ReadOnlySpan<byte> payload)
        {
            // agrega '\n' al final
            var arr = new byte[payload.Length + 1];
            payload.CopyTo(arr);
            arr[^1] = (byte)'\n';
            return arr;
        }

        public bool TryDecode(ref ReadOnlySpan<byte> buffer, out ReadOnlyMemory<byte> frame)
        {
            frame = default;

            int idxN = buffer.IndexOf((byte)'\n');
            int idxR = buffer.IndexOf((byte)'\r');

            int idx;
            if (idxN >= 0 && idxR >= 0) idx = Math.Min(idxN, idxR);
            else idx = Math.Max(idxN, idxR);

            if (idx < 0) return false;

            var slice = buffer.Slice(0, idx);
            frame = slice.ToArray();

            // consumir \r\n o \n o \r
            int consume = 1;
            if (buffer.Length > idx + 1 && buffer[idx] == (byte)'\r' && buffer[idx + 1] == (byte)'\n')
                consume = 2;

            buffer = buffer.Slice(idx + consume);
            return true;
        }

    }
}

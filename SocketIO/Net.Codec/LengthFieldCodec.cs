using SocketIO.Net.Abstractions;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace SocketIO.Net.Protocol
{
    public sealed class LengthFieldCodec : IFrameCodec
    {
        private readonly int _lenBytes;          // 1,2,4
        private readonly bool _bigEndian;
        private readonly int _lengthOffset;      // offset dentro del frame donde está el length
        private readonly int _headerSize;        // tamaño mínimo para leer length
        private readonly int _maxFrame;

        public LengthFieldCodec(int lengthBytes, bool bigEndian = true, int lengthOffset = 0, int headerSize = -1, int maxFrameBytes = 65535)
        {
            if (lengthBytes is not (1 or 2 or 4)) throw new ArgumentOutOfRangeException(nameof(lengthBytes));
            _lenBytes = lengthBytes;
            _bigEndian = bigEndian;
            _lengthOffset = lengthOffset;
            _headerSize = headerSize < 0 ? lengthOffset + lengthBytes : headerSize;
            _maxFrame = maxFrameBytes;
        }

        public ReadOnlyMemory<byte> Encode(ReadOnlySpan<byte> payload)
            => payload.ToArray();

        public bool TryDecode(ref ReadOnlySpan<byte> buffer, out ReadOnlyMemory<byte> frame)
        {
            frame = default;

            if (buffer.Length < _headerSize) return false;

            var header = buffer.Slice(0, _headerSize);

            int len = _lenBytes switch
            {
                1 => header[_lengthOffset],
                2 => _bigEndian
                    ? BinaryPrimitives.ReadUInt16BigEndian(header.Slice(_lengthOffset, 2))
                    : BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(_lengthOffset, 2)),
                4 => (int)(_bigEndian
                    ? BinaryPrimitives.ReadUInt32BigEndian(header.Slice(_lengthOffset, 4))
                    : BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(_lengthOffset, 4))),
                _ => 0
            };

            if (len <= 0 || len > _maxFrame) // sanity
            {
                // si el length es basura, descartá 1 byte para re-sincronizar
                buffer = buffer.Slice(1);
                return false;
            }

            if (buffer.Length < len) return false;

            frame = buffer.Slice(0, len).ToArray();
            buffer = buffer.Slice(len);
            return true;
        }
    }
}

using SocketIO.Net.Abstractions;
using System.Buffers.Binary;

namespace SocketIO.Net.Protocol
{
    public static class PacketCodec
    {
        public const int HeaderSize = 12;
        public const byte CurrentVersion = 1;

        public static byte[] Encode(in Packet p)
        {
            var payloadLen = p.Payload.Length;
            var buffer = new byte[HeaderSize + payloadLen];

            buffer[0] = p.Version;
            buffer[1] = (byte)p.Type;

            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), p.Flags);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4, 4), p.Sequence);
            BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(8, 4), payloadLen);

            p.Payload.Span.CopyTo(buffer.AsSpan(HeaderSize));
            return buffer;
        }

        public static bool TryDecode(ReadOnlySpan<byte> data, out Packet packet)
        {
            packet = default;

            if (data.Length < HeaderSize) return false;

            byte version = data[0];
            var type = (MessageType)data[1];
            ushort flags = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));
            uint seq = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
            int payloadLen = BinaryPrimitives.ReadInt32BigEndian(data.Slice(8, 4));

            if (payloadLen < 0) return false;
            if (data.Length != HeaderSize + payloadLen) return false;

            // Copiamos payload para que sea heap-safe (no dependa del buffer temporal).
            var payload = data.Slice(HeaderSize, payloadLen).ToArray();

            packet = new Packet(
                Version: version,
                Type: type,
                Flags: flags,
                Sequence: seq,
                Payload: payload
            );

            return true;
        }
    }
}

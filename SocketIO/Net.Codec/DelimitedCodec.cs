using SocketIO.Net.Abstractions;

namespace SocketIO.Net.Protocol
{
    public sealed class DelimitedCodec : IFrameCodec
    {
        private readonly byte? _start;
        private readonly byte _end;
        private readonly int _maxFrame;

        public DelimitedCodec(byte? startByte, byte endByte, int maxFrameBytes = 8192)
        {
            _start = startByte;
            _end = endByte;
            _maxFrame = maxFrameBytes;
        }

        public ReadOnlyMemory<byte> Encode(ReadOnlySpan<byte> payload)
        {
            // Para serial normalmente NO “encodás” aquí (depende del protocolo).
            // Pero te lo dejo simple: devuelve el payload tal cual.
            return payload.ToArray();
        }

        public bool TryDecode(ref ReadOnlySpan<byte> buffer, out ReadOnlyMemory<byte> frame)
        {
            frame = default;

            if (buffer.Length == 0) return false;

            int startIdx = 0;

            // buscar start si aplica
            if (_start.HasValue)
            {
                startIdx = buffer.IndexOf(_start.Value);
                if (startIdx < 0)
                {
                    buffer = ReadOnlySpan<byte>.Empty;
                    return false;
                }
            }

            var search = buffer.Slice(startIdx);
            int endIdx = search.IndexOf(_end);
            if (endIdx < 0)
            {
                // no hay fin aún
                // evitar crecimiento infinito
                if (search.Length > _maxFrame)
                {
                    // descartar hasta el final del search (o podrías descartar 1 byte)
                    buffer = ReadOnlySpan<byte>.Empty;
                }
                return false;
            }

            int frameLen = endIdx + 1; // incluye end
            var slice = search.Slice(0, frameLen);

            frame = slice.ToArray(); // heap-safe
            buffer = search.Slice(frameLen); // consume

            return true;
        }
    }
}

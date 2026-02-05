using SocketIO.Net.Abstractions;
using SocketIO.Net.Diagnostics;

namespace SocketIO.Net.Runtime;

public sealed class AutoFrameSniffer
{
    private readonly IConnection _conn;
    private readonly FrameAwareDumper _dumper;
    private readonly List<IFrameCodec> _codecs;

    public AutoFrameSniffer(IConnection conn, FrameAwareDumper dumper, IEnumerable<IFrameCodec>? codecs = null)
    {
        _conn = conn;
        _dumper = dumper;

        _codecs = codecs?.ToList() ?? new List<IFrameCodec>
        {
            new SocketIO.Net.Protocol.Codec.DelimitedCodec(null, (byte)'\n'),
            new SocketIO.Net.Protocol.Codec.DelimitedCodec(0x02, 0x03), // STX/ETX
            new SocketIO.Net.Protocol.Codec.DelimitedCodec(0x7E, 0x7E), // HDLC-like
            new SocketIO.Net.Protocol.Codec.LengthFieldCodec(2, bigEndian:true, lengthOffset:0, headerSize:2, maxFrameBytes:4096),
            new SocketIO.Net.Protocol.Codec.FixedLengthCodec(8),
            new SocketIO.Net.Protocol.Codec.FixedLengthCodec(16),
        };
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var buffer = new byte[8192];
        int buffered = 0;
        int frameIndex = 0;
        string remote = _conn.RemoteEndPoint?.ToString() ?? "remote?";

        while (!ct.IsCancellationRequested)
        {
            int read = await _conn.ReceiveAsync(buffer.AsMemory(buffered), ct);
            if (read == 0) break;

            buffered += read;

            // spanAll SOLO se usa en sección sync (sin await)
            ReadOnlySpan<byte> spanAll = buffer.AsSpan(0, buffered);

            int bestCount = 0;
            List<ReadOnlyMemory<byte>> bestFrames = new();
            int bestRemainderLen = spanAll.Length; // si nadie decodifica, remainder = todo

            foreach (var codec in _codecs)
            {
                var span = spanAll; // local
                var frames = new List<ReadOnlyMemory<byte>>();

                while (codec.TryDecode(ref span, out var f))
                    frames.Add(f);

                if (frames.Count > bestCount)
                {
                    bestCount = frames.Count;
                    bestFrames = frames;
                    bestRemainderLen = span.Length; // ✅ guardamos SOLO LEN, no Span
                }
            }

            // ✅ ahora ya NO necesitamos spans; podemos await sin problemas
            if (bestCount == 0)
            {
                frameIndex++;
                await _dumper.DumpFrameAsync("RX", remote, frameIndex, spanAll);
                buffered = 0;
                continue;
            }

            foreach (var f in bestFrames)
            {
                frameIndex++;
                await _dumper.DumpFrameAsync("RX", remote, frameIndex, f.Span);
            }

            // compactar leftovers SIN guardar spans entre awaits
            // remainder = últimos bestRemainderLen bytes del buffer actual
            if (bestRemainderLen > 0)
            {
                Buffer.BlockCopy(
                    src: buffer,
                    srcOffset: buffered - bestRemainderLen,
                    dst: buffer,
                    dstOffset: 0,
                    count: bestRemainderLen
                );
            }

            buffered = bestRemainderLen;
        }
    }
}

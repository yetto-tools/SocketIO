using SocketIO.Net.Abstractions;
using System.Buffers.Binary;

namespace SocketIO.Net.Protocol;

public sealed class ModbusRtuCodec : IFrameCodec
{
    public sealed class Options
    {
        public int MaxFrameBytes { get; init; } = 260;        // típico RTU <= 256 data + overhead
        public int ScanLimitBytes { get; init; } = 64;        // cuánto escaneo para re-sincronizar
        public bool ValidateCrc { get; init; } = true;
        public bool AllowBroadcastAddress0 { get; init; } = true;
    }

    private readonly Options _opt;

    public ModbusRtuCodec(Options? opt = null) => _opt = opt ?? new Options();

    /// <summary>
    /// Encode espera payload = [addr][func][data...], y agrega CRC16 (Lo,Hi).
    /// </summary>
    public ReadOnlyMemory<byte> Encode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2) throw new ArgumentException("Payload Modbus RTU debe incluir addr+func.");

        var buf = new byte[payload.Length + 2];
        payload.CopyTo(buf);

        ushort crc = Crc16Modbus(payload);
        buf[^2] = (byte)(crc & 0xFF);         // CRC Lo
        buf[^1] = (byte)(crc >> 8);           // CRC Hi

        return buf;
    }

    public bool TryDecode(ref ReadOnlySpan<byte> buffer, out ReadOnlyMemory<byte> frame)
    {
        frame = default;

        // mínimo: addr(1) + func(1) + crc(2)
        if (buffer.Length < 4) return false;

        int scan = Math.Min(_opt.ScanLimitBytes, buffer.Length - 3);

        for (int start = 0; start <= scan; start++)
        {
            var span = buffer.Slice(start);

            if (!IsPlausibleAddress(span[0])) continue;

            // Necesitamos al menos addr+func
            if (span.Length < 4) break;

            // Probar longitudes posibles según function code (request/response/exception)
            foreach (int len in GetCandidateLengths(span))
            {
                if (len <= 0) continue;
                if (len > _opt.MaxFrameBytes) continue;

                if (span.Length < len)
                {
                    // puede ser una trama válida pero incompleta -> esperar más bytes
                    // OJO: si start>0 y había basura al inicio, no la descartamos todavía.
                    continue;
                }

                var candidate = span.Slice(0, len);

                if (_opt.ValidateCrc && !HasValidCrc(candidate))
                    continue;

                // ✅ encontrado
                frame = candidate.ToArray();

                // Consumir desde el buffer original (incluye bytes basura antes del start)
                buffer = buffer.Slice(start + len);
                return true;
            }

            // Si llegamos aquí: no hay frame válido desde este start.
            // seguimos escaneando (resync).
        }

        // No se pudo decodificar todavía
        return false;
    }

    private bool IsPlausibleAddress(byte addr)
    {
        if (addr == 0) return _opt.AllowBroadcastAddress0;
        return addr is >= 1 and <= 247;
    }

    /// <summary>
    /// Devuelve longitudes candidatas para request/response.
    /// Usa heurística por Function Code + campos byteCount.
    /// </summary>
    private static IEnumerable<int> GetCandidateLengths(ReadOnlySpan<byte> span)
    {
        byte func = span[1];

        // Exception response: addr, func|0x80, exCode, crcLo, crcHi => 5
        if ((func & 0x80) != 0)
        {
            yield return 5;
            yield break;
        }

        // Respuestas de lectura (1,2,3,4): addr func byteCount data... crc2
        if (func is 1 or 2 or 3 or 4)
        {
            // Request típico read: 8 bytes (addr func start2 qty2 crc2)
            yield return 8;

            // Response: necesita byteCount (3er byte)
            if (span.Length >= 3)
            {
                int bc = span[2];
                yield return 3 + bc + 2; // = bc + 5
            }

            yield break;
        }

        // Write single coil/register (5,6): request y response = 8 bytes
        if (func is 5 or 6)
        {
            yield return 8;
            yield break;
        }

        // Write multiple coils/registers (15,16)
        if (func is 15 or 16)
        {
            // Response echo: addr func start2 qty2 crc2 = 8
            yield return 8;

            // Request: addr func start2 qty2 byteCount data(byteCount) crc2 = 7 + bc + 2 = bc+9
            if (span.Length >= 7)
            {
                int bc = span[6];
                yield return bc + 9;
            }

            yield break;
        }

        // Mask Write Register (22 / 0x16): request/response = 10
        if (func == 22)
        {
            yield return 10;
            yield break;
        }

        // Read/Write Multiple Registers (23 / 0x17)
        if (func == 23)
        {
            // Response: addr func byteCount data... crc2
            if (span.Length >= 3)
            {
                int bc = span[2];
                yield return bc + 5;
            }

            // Request: addr func readStart2 readQty2 writeStart2 writeQty2 byteCount data... crc2
            // Header (addr+func + 2+2+2+2 + byteCount) = 1+1+2+2+2+2+1 = 11
            if (span.Length >= 11)
            {
                int bc = span[10];
                yield return 11 + bc + 2; // = bc + 13
            }

            yield break;
        }

        // Fallback: no sabemos el largo -> no adivinamos (para no tragarnos el stream)
        // (Si tu protocolo usa function codes custom, aquí agregás reglas.)
        yield break;
    }

    private static bool HasValidCrc(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4) return false;

        ushort got = (ushort)(frame[^2] | (frame[^1] << 8));  // Lo,Hi
        ushort calc = Crc16Modbus(frame.Slice(0, frame.Length - 2));

        return got == calc;
    }

    /// <summary>CRC-16/MODBUS (poly 0xA001, init 0xFFFF)</summary>
    public static ushort Crc16Modbus(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;

        for (int i = 0; i < data.Length; i++)
        {
            crc ^= data[i];
            for (int b = 0; b < 8; b++)
            {
                bool lsb = (crc & 0x0001) != 0;
                crc >>= 1;
                if (lsb) crc ^= 0xA001;
            }
        }

        return crc;
    }
}

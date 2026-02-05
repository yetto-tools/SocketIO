using SocketIO.Net.Abstractions;

namespace SocketIO.Net.Protocol.Codec;

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
        if (payload.Length < 2)
            throw new ArgumentException("Payload Modbus RTU debe incluir addr+func.");

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
        if (buffer.Length < 4)
            return false;

        int scan = Math.Min(_opt.ScanLimitBytes, buffer.Length - 3);

        for (int start = 0; start <= scan; start++)
        {
            var span = buffer.Slice(start);

            if (!IsPlausibleAddress(span[0]))
                continue;

            if (span.Length < 4)
                break;

            bool incompletePossible = false;

            // candidatos por function code (sin yield)
            Span<int> lens = stackalloc int[8];
            int count = FillCandidateLengths(span, lens);

            for (int i = 0; i < count; i++)
            {
                int len = lens[i];

                if (len <= 0) continue;
                if (len > _opt.MaxFrameBytes) continue;

                if (span.Length < len)
                {
                    incompletePossible = true;
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

            // Si desde el inicio parecía frame válido pero incompleto => esperar más bytes
            if (incompletePossible && start == 0)
                return false;

            // si start>0, seguimos escaneando (resync)
        }

        return false;
    }

    private bool IsPlausibleAddress(byte addr)
    {
        if (addr == 0) return _opt.AllowBroadcastAddress0;
        return addr is >= 1 and <= 247;
    }

    /// <summary>
    /// Llena longitudes candidatas (request/response) según Function Code.
    /// SIN yield, SIN closures.
    /// Devuelve cantidad escrita en dst.
    /// </summary>
    private static int FillCandidateLengths(ReadOnlySpan<byte> span, Span<int> dst)
    {
        int n = 0;
        byte func = span[1];

        // helper: add len if room (sin local functions que capturen)
        if ((func & 0x80) != 0)
        {
            if (n < dst.Length) dst[n++] = 5;
            return n;
        }

        // Read (1,2,3,4)
        if (func is 1 or 2 or 3 or 4)
        {
            // Request típico: 8 bytes (addr func start2 qty2 crc2)
            if (n < dst.Length) dst[n++] = 8;

            // Response: addr func byteCount data... crc2
            if (span.Length >= 3)
            {
                int bc = span[2];
                int len = bc + 5;
                if (n < dst.Length) dst[n++] = len;
            }

            return n;
        }

        // Write single (5,6): request y response = 8
        if (func is 5 or 6)
        {
            if (n < dst.Length) dst[n++] = 8;
            return n;
        }

        // Write multiple (15,16)
        if (func is 15 or 16)
        {
            // Response echo: 8
            if (n < dst.Length) dst[n++] = 8;

            // Request: addr func start2 qty2 byteCount data... crc2
            // byteCount en offset 6
            if (span.Length >= 7)
            {
                int bc = span[6];
                int len = bc + 9;
                if (n < dst.Length) dst[n++] = len;
            }

            return n;
        }

        // Mask Write Register (22): request/response = 10
        if (func == 22)
        {
            if (n < dst.Length) dst[n++] = 10;
            return n;
        }

        // Read/Write Multiple Registers (23)
        if (func == 23)
        {
            // Response: addr func byteCount data... crc2
            if (span.Length >= 3)
            {
                int bc = span[2];
                int len = bc + 5;
                if (n < dst.Length) dst[n++] = len;
            }

            // Request: byteCount en offset 10 (header 11 bytes + data + crc2)
            if (span.Length >= 11)
            {
                int bc = span[10];
                int len = bc + 13;
                if (n < dst.Length) dst[n++] = len;
            }

            return n;
        }

        // Unknown => 0 candidatos
        return n;
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

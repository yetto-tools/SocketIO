using SocketIO.Net.Abstractions;
using System.Runtime.CompilerServices;


namespace SocketIO.Net.Protocol
{
    /// <summary>
    /// Heurístico: prueba varios codecs contra el buffer y elige el "mejor".
    /// Cuando acierta varias veces seguidas, se bloquea a ese codec.
    /// </summary>
    public sealed class AutoFrameCodec : IFrameCodec
    {
        private readonly List<IFrameCodec> _candidates;
        private readonly Queue<ReadOnlyMemory<byte>> _pending = new();

        private readonly Dictionary<IFrameCodec, int> _streak = new(ReferenceEqualityComparer<IFrameCodec>.Instance);

        private readonly AutoFrameOptions _opt;

        private IFrameCodec? _locked;

        public AutoFrameCodec(IEnumerable<IFrameCodec> candidates, AutoFrameOptions? options = null)
        {
            _candidates = candidates?.ToList() ?? throw new ArgumentNullException(nameof(candidates));
            if (_candidates.Count == 0) throw new ArgumentException("Debe haber al menos 1 codec candidato.");
            _opt = options ?? new AutoFrameOptions();
        }

        public string Mode => _locked is null ? "AUTO" : $"LOCKED:{_locked.GetType().Name}";

        public ReadOnlyMemory<byte> Encode(ReadOnlySpan<byte> payload)
        {
            // Para TX: si ya detectamos, usamos el mismo codec.
            // Si no, usamos el DefaultEncoder (o el primero candidato).
            var encoder = _locked ?? _opt.DefaultEncoder ?? _candidates[0];
            return encoder.Encode(payload);
        }

        public bool TryDecode(ref ReadOnlySpan<byte> buffer, out ReadOnlyMemory<byte> frame)
        {
            frame = default;

            // 0) Si ya tenemos frames pre-decodificados, entregamos primero eso.
            if (_pending.Count > 0)
            {
                frame = _pending.Dequeue();
                return true;
            }

            // 1) Si ya está bloqueado, delegamos
            if (_locked is not null)
                return _locked.TryDecode(ref buffer, out frame);

            // 2) AUTO: probar todos los candidatos sobre copias del span (sync)
            if (buffer.Length < _opt.MinBufferToConsider)
                return false;

            CandidateResult best = default;
            bool hasBest = false;

            foreach (var codec in _candidates)
            {
                var span = buffer; // copia local
                var frames = new List<ReadOnlyMemory<byte>>(capacity: 8);

                int before = span.Length;
                int decoded = 0;
                bool invalid = false;

                while (decoded < _opt.MaxFramesPerPass && codec.TryDecode(ref span, out var f))
                {
                    // sanity checks
                    if (f.Length == 0 || f.Length > _opt.MaxFrameBytes)
                    {
                        invalid = true;
                        break;
                    }

                    decoded++;
                    if (frames.Count < _opt.MaxQueueFrames)
                        frames.Add(f);
                }

                if (invalid || decoded == 0)
                    continue;

                int remainderLen = span.Length;
                int consumed = before - remainderLen;

                // Score: más frames + más consumo + penalización por remainder grande
                int score =
                    decoded * 1000 +
                    consumed -
                    remainderLen * _opt.RemainderPenalty;

                if (!hasBest || score > best.Score)
                {
                    hasBest = true;
                    best = new CandidateResult(codec, score, decoded, consumed, remainderLen, frames);
                }
            }

            if (!hasBest)
                return false;

            // 3) Aplicar “best” al buffer REAL:
            // consumimos lo que el best consumió, y encolamos sus frames
            // Nota: el Peer volverá a llamar TryDecode, así que devolvemos 1 frame ahora.
            buffer = buffer.Slice(best.ConsumedBytes);

            foreach (var f in best.Frames)
                _pending.Enqueue(f);

            // 4) Lock heurístico
            int s = 1;
            if (_streak.TryGetValue(best.Codec, out var prev)) s = prev + 1;
            _streak[best.Codec] = s;

            // reset streaks de los demás (reduce falsos locks)
            foreach (var k in _streak.Keys.ToArray())
                if (!ReferenceEquals(k, best.Codec))
                    _streak[k] = Math.Max(0, _streak[k] - 1);

            if (s >= _opt.LockAfterHits && best.DecodedFrames >= _opt.MinFramesToLock)
                _locked = best.Codec;

            // 5) Entregar primer frame de la cola
            if (_pending.Count > 0)
            {
                frame = _pending.Dequeue();
                return true;
            }

            return false;
        }

        private readonly record struct CandidateResult(
            IFrameCodec Codec,
            int Score,
            int DecodedFrames,
            int ConsumedBytes,
            int RemainderLen,
            List<ReadOnlyMemory<byte>> Frames
        );

        public sealed class AutoFrameOptions
        {
            public int MaxFrameBytes { get; init; } = 4096;
            public int MinBufferToConsider { get; init; } = 4;

            public int MaxFramesPerPass { get; init; } = 64;    // evita loops infinitos
            public int MaxQueueFrames { get; init; } = 16;      // cola máxima por pasada

            public int RemainderPenalty { get; init; } = 2;     // penaliza leftovers grandes

            public int LockAfterHits { get; init; } = 3;        // cuántas veces seguidas debe ganar
            public int MinFramesToLock { get; init; } = 2;      // mínimo de frames decodificados para lock

            // Para TX mientras estamos en AUTO
            public IFrameCodec? DefaultEncoder { get; init; } = null;
        }

        /// <summary>Comparer por referencia para usar IFrameCodec como key.</summary>
        private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
        {
            public static readonly ReferenceEqualityComparer<T> Instance = new();
            public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
            public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}

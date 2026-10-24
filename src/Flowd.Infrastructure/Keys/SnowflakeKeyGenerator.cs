using Flowd.Engine.Keys;

namespace Flowd.Infrastructure.Keys;

/// <summary>
/// 63-bit time-ordered keys: 41 bits milliseconds since 2024-01-01 UTC, 10 bits node id, 12 bits sequence (FR-DOM-001).
/// Thread-safe; never goes backwards even if the clock does.
/// </summary>
public sealed class SnowflakeKeyGenerator : IKeyGenerator
{
    private const int NodeBits = 10;
    private const int SequenceBits = 12;
    private const long MaxSequence = (1L << SequenceBits) - 1;
    private static readonly DateTimeOffset Epoch = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly TimeProvider _time;
    private readonly long _node;
    private readonly Lock _gate = new();
    private long _lastMillis = -1;
    private long _sequence;

    public SnowflakeKeyGenerator(TimeProvider time, int nodeId)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfNegative(nodeId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nodeId, (1 << NodeBits) - 1);
        _time = time;
        _node = nodeId;
    }

    public long NextKey()
    {
        lock (_gate)
        {
            var millis = Math.Max((_time.GetUtcNow() - Epoch).Ticks / TimeSpan.TicksPerMillisecond, _lastMillis);
            if (millis == _lastMillis)
            {
                _sequence = (_sequence + 1) & MaxSequence;
                if (_sequence == 0)
                {
                    millis++; // sequence exhausted within this millisecond: borrow the next one
                }
            }
            else
            {
                _sequence = 0;
            }

            _lastMillis = millis;
            return (millis << (NodeBits + SequenceBits)) | (_node << SequenceBits) | _sequence;
        }
    }
}

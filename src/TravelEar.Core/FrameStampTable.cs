namespace TravelEar.Core;

/// <summary>
/// Carries capture timestamps across a ring buffer hand-off (docs/DESIGN.md "Offset",
/// <c>REQ-OFFSET-MEASURE</c>). The producer marks each block it writes with the ring position
/// of the block's first sample, the block length, and the timestamp travelling with it; the
/// consumer, on the other thread, resolves the position it is about to read to the timestamp of
/// the newest mark whose span contains it. Positions may be monotonic (modulus 0) or wrapped
/// ring indices (modulus = ring length): matching is done modulo the ring, so a read head that
/// was jumped by a resync, or a ring that wrapped, still resolves to the right mark as long as
/// every write marks (the newest mark always wins over a stale one from a ring ago).
/// <para>
/// Timestamp <see cref="NoStamp"/> (0) marks a block that carries no capture time (silence the
/// mod generated): it resolves to "none" and, being newest, shadows older marks.
/// </para>
/// <para>
/// Lock-free for the consumer: each slot is a seqlock, so a read torn by a concurrent write is
/// detected and retried. Marks must come from one producer at a time (the callers serialize
/// them); resolves may run on any single other thread. No allocation after construction.
/// </para>
/// </summary>
public sealed class FrameStampTable
{
    public const long NoStamp = 0;
    public const int DefaultSlots = 16;

    private readonly long[] _version;
    private readonly long[] _position;
    private readonly int[] _length;
    private readonly long[] _timestamp;
    private int _next;      // producer only
    private long _marks;    // producer only; the version stamp

    public FrameStampTable(int slots = DefaultSlots)
    {
        if (slots < 1) throw new ArgumentOutOfRangeException(nameof(slots), slots, "Need at least one slot.");
        _version = new long[slots];
        _position = new long[slots];
        _length = new int[slots];
        _timestamp = new long[slots];
    }

    public int Slots => _version.Length;

    /// <summary>Marks made since construction.</summary>
    public long Marks => Volatile.Read(ref _marks);

    // [impl->REQ-OFFSET-MEASURE]
    /// <summary>Producer: records that the block starting at <paramref name="position"/>, <paramref name="length"/> samples long, carries <paramref name="timestamp"/>.</summary>
    public void Mark(long position, int length, long timestamp)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length), length, "A mark needs a positive length.");
        var i = _next;
        _next = (i + 1) % _version.Length;
        var stamp = (_marks + 1) * 2; // even = stable; odd = being written

        Interlocked.Exchange(ref _version[i], stamp - 1); // full fence: readers see "in progress" before any field
        _position[i] = position;
        _length[i] = length;
        _timestamp[i] = timestamp;
        Volatile.Write(ref _version[i], stamp);
        Volatile.Write(ref _marks, _marks + 1);
    }

    // [impl->REQ-OFFSET-MEASURE]
    /// <summary>
    /// Consumer: finds the newest mark whose span contains <paramref name="position"/> (modulo
    /// <paramref name="modulus"/> when it is positive). True with its timestamp when that mark
    /// carries one; false when no mark contains the position or the containing mark is
    /// <see cref="NoStamp"/>.
    /// </summary>
    public bool TryResolve(long position, long modulus, out long timestamp)
    {
        timestamp = NoStamp;
        long bestVersion = 0;
        var bestTimestamp = NoStamp;

        for (var i = 0; i < _version.Length; i++)
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var v1 = Volatile.Read(ref _version[i]);
                if (v1 == 0) break;            // never written
                if ((v1 & 1) != 0) continue;   // being written: retry
                var pos = _position[i];
                var len = _length[i];
                var ts = _timestamp[i];
                Interlocked.MemoryBarrier();
                var v2 = Volatile.Read(ref _version[i]);
                if (v1 != v2) continue;        // torn: retry

                if (v1 > bestVersion && Contains(pos, len, position, modulus))
                {
                    bestVersion = v1;
                    bestTimestamp = ts;
                }
                break;
            }
        }

        if (bestVersion == 0 || bestTimestamp == NoStamp) return false;
        timestamp = bestTimestamp;
        return true;
    }

    private static bool Contains(long start, int length, long position, long modulus)
    {
        var d = position - start;
        if (modulus > 0) d = (d % modulus + modulus) % modulus;
        return d >= 0 && d < length;
    }
}

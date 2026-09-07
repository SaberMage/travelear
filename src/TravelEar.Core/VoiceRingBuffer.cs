namespace TravelEar.Core;

/// <summary>
/// Lock-free single-writer / single-reader float ring. The Tap (Unity audio thread) writes,
/// the Sink pump thread reads. A read that finds fewer samples than requested pads the
/// remainder with silence so the Sink stream never gaps; a write that finds too little free
/// space drops the samples that do not fit. Both events are counted for diagnostics.
/// </summary>
public sealed class VoiceRingBuffer
{
    private readonly float[] _buffer;
    private long _head; // next write position (monotonic), owned by the writer
    private long _tail; // next read position (monotonic), owned by the reader
    private long _dropped;
    private long _underruns;

    public VoiceRingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new float[capacity];
    }

    public int Capacity => _buffer.Length;

    /// <summary>Samples currently buffered and not yet read.</summary>
    public int Count => (int)(Volatile.Read(ref _head) - Volatile.Read(ref _tail));

    /// <summary>Samples dropped by <see cref="Write"/> because the ring was full.</summary>
    public long DroppedSamples => Volatile.Read(ref _dropped);

    /// <summary>Number of <see cref="Read"/> calls that had to pad with silence.</summary>
    public long Underruns => Volatile.Read(ref _underruns);

    /// <summary>Writer side. Returns how many samples were stored; the rest were dropped.</summary>
    public int Write(ReadOnlySpan<float> samples)
    {
        var head = _head; // writer-owned
        var tail = Volatile.Read(ref _tail);
        var free = Capacity - (int)(head - tail);
        var toWrite = Math.Min(free, samples.Length);
        if (toWrite < samples.Length)
            Interlocked.Add(ref _dropped, samples.Length - toWrite);
        if (toWrite == 0) return 0;

        var start = (int)(head % Capacity);
        var firstRun = Math.Min(toWrite, Capacity - start);
        samples.Slice(0, firstRun).CopyTo(_buffer.AsSpan(start, firstRun));
        if (toWrite > firstRun)
            samples.Slice(firstRun, toWrite - firstRun).CopyTo(_buffer.AsSpan(0, toWrite - firstRun));

        Volatile.Write(ref _head, head + toWrite);
        return toWrite;
    }

    /// <summary>
    /// Reader side. Fills <paramref name="destination"/> completely: real samples first, then
    /// zeros if the ring runs dry. Returns the number of real samples copied.
    /// </summary>
    public int Read(Span<float> destination)
    {
        var tail = _tail; // reader-owned
        var head = Volatile.Read(ref _head);
        var available = (int)(head - tail);
        var toRead = Math.Min(available, destination.Length);

        if (toRead > 0)
        {
            var start = (int)(tail % Capacity);
            var firstRun = Math.Min(toRead, Capacity - start);
            _buffer.AsSpan(start, firstRun).CopyTo(destination.Slice(0, firstRun));
            if (toRead > firstRun)
                _buffer.AsSpan(0, toRead - firstRun).CopyTo(destination.Slice(firstRun, toRead - firstRun));
            Volatile.Write(ref _tail, tail + toRead);
        }

        if (toRead < destination.Length)
        {
            destination.Slice(toRead).Clear();
            Interlocked.Increment(ref _underruns);
        }
        return toRead;
    }

    /// <summary>
    /// Discards up to <paramref name="count"/> of the oldest buffered samples. Reader side only.
    /// Used by the Helper to clamp its backlog: a consumer that falls behind the producer would
    /// otherwise turn the whole ring into latency. Returns how many were discarded.
    /// </summary>
    public int Discard(int count)
    {
        if (count <= 0) return 0;
        var tail = _tail;
        var head = Volatile.Read(ref _head);
        var toDrop = Math.Min(count, (int)(head - tail));
        if (toDrop > 0) Volatile.Write(ref _tail, tail + toDrop);
        return toDrop;
    }

    /// <summary>Discards everything buffered. Reader side only.</summary>
    public void Clear()
    {
        Volatile.Write(ref _tail, Volatile.Read(ref _head));
    }
}

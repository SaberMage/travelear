namespace TravelEar.Core;

/// <summary>
/// Rolling window over Offset samples (<c>REQ-OFFSET-MEASURE</c>): the mod adds one sample per
/// Sink frame the Helper reports and reads the average of the last <see cref="WindowMs"/> for the
/// log line (and, later, the settings row). Single-threaded: the back-pipe reader thread owns it.
/// </summary>
public sealed class OffsetAverager
{
    public const double DefaultWindowMs = 10_000;

    private readonly Queue<(double At, double Value)> _samples = new();
    private double _sum;

    public OffsetAverager(double windowMs = DefaultWindowMs)
    {
        if (windowMs <= 0 || double.IsNaN(windowMs))
            throw new ArgumentOutOfRangeException(nameof(windowMs), windowMs, "The window must be positive.");
        WindowMs = windowMs;
    }

    public double WindowMs { get; }

    /// <summary>Samples currently inside the window (after the last <see cref="Add"/> or <see cref="TryAverage"/>).</summary>
    public int Count => _samples.Count;

    // [impl->REQ-OFFSET-MEASURE]
    /// <summary>Adds one Offset sample taken at <paramref name="nowMs"/> and drops samples older than the window.</summary>
    public void Add(double offsetMs, double nowMs)
    {
        if (double.IsNaN(offsetMs) || double.IsInfinity(offsetMs)) return;
        _samples.Enqueue((nowMs, offsetMs));
        _sum += offsetMs;
        Trim(nowMs);
    }

    // [impl->REQ-OFFSET-MEASURE]
    /// <summary>
    /// The average, minimum and maximum of the samples inside the window ending at
    /// <paramref name="nowMs"/>. False when the window is empty.
    /// </summary>
    public bool TryAverage(double nowMs, out double averageMs, out double minMs, out double maxMs, out int count)
    {
        Trim(nowMs);
        count = _samples.Count;
        if (count == 0)
        {
            averageMs = minMs = maxMs = 0;
            return false;
        }
        averageMs = _sum / count;
        minMs = double.PositiveInfinity;
        maxMs = double.NegativeInfinity;
        foreach (var (_, value) in _samples)
        {
            if (value < minMs) minMs = value;
            if (value > maxMs) maxMs = value;
        }
        return true;
    }

    private void Trim(double nowMs)
    {
        while (_samples.Count > 0 && nowMs - _samples.Peek().At > WindowMs)
            _sum -= _samples.Dequeue().Value;
        if (_samples.Count == 0) _sum = 0; // no drift accumulates across empty windows
    }
}

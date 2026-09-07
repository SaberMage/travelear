namespace TravelEar.Core;

/// <summary>
/// Sink format helper (<c>REQ-SINK-FORMAT</c>): the Sink carries the Tap's channel count unless
/// config <c>Downmix</c> is set, in which case every frame is folded to mono before framing. The
/// fold is an equal-weight average of the interleaved channels, so a signal identical in every
/// channel comes out unchanged and full-scale input cannot clip.
/// </summary>
public static class Downmixer
{
    // [impl->REQ-SINK-FORMAT]
    /// <summary>
    /// Folds <paramref name="interleaved"/> (<paramref name="channels"/> samples per frame) into
    /// <paramref name="mono"/>, one sample per frame. Returns the number of frames written. The
    /// output may alias the front of the input: frame <c>i</c> is read before mono sample
    /// <c>i</c> is written and <c>i &lt;= i * channels</c>, so an in-place fold is safe.
    /// </summary>
    public static int ToMono(ReadOnlySpan<float> interleaved, int channels, Span<float> mono)
    {
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channel count must be at least 1.");
        if (interleaved.Length % channels != 0)
            throw new ArgumentException("Interleaved length must be a whole number of frames.", nameof(interleaved));
        var frames = interleaved.Length / channels;
        if (mono.Length < frames) throw new ArgumentException("Mono destination too small.", nameof(mono));

        if (channels == 1)
        {
            if (interleaved != (ReadOnlySpan<float>)mono.Slice(0, frames)) interleaved.CopyTo(mono);
            return frames;
        }

        var scale = 1f / channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            var offset = frame * channels;
            for (var c = 0; c < channels; c++) sum += interleaved[offset + c];
            mono[frame] = sum * scale;
        }
        return frames;
    }
}

namespace TravelEar.Core;

/// <summary>
/// The channel fade a peer hears when a voice-activation trigger opens and closes its channel
/// (Dissonance <c>VoiceBroadcastTrigger._activationFader</c> driven by the trigger's
/// <c>VolumeFaderSettings</c> fade-in and fade-out), applied to Local Voice per sample so the
/// transmit gate opens and closes as a ramp, not a cut. M2 run 3 heard the hard gate as chop: the
/// game's VAD flag flips several times per phrase and every flip past the hold spliced a zero
/// frame into speech. In a solo session the game's own fader never runs (the channel never opens),
/// so the mod applies the same fade itself. Linear, like the game's fader; a zero fade jumps.
/// <para>Pure; encoder thread only, apart from <see cref="Set"/> which the main thread may call.</para>
/// </summary>
public sealed class TransmitFader
{
    private double _fadeInMs;
    private double _fadeOutMs;
    private double _gain; // 0..1, accumulated in double so 4800 steps of 1/4800 land exactly on 1

    public TransmitFader(double fadeInMs, double fadeOutMs)
    {
        Set(fadeInMs, fadeOutMs);
    }

    public double FadeInMs => _fadeInMs;
    public double FadeOutMs => _fadeOutMs;

    /// <summary>The gain the last sample was scaled by (0 = silent, 1 = through).</summary>
    public float Gain => (float)_gain;

    /// <summary>Changes the fade times. The current gain is kept, so a change mid-fade never jumps.</summary>
    public void Set(double fadeInMs, double fadeOutMs)
    {
        if (fadeInMs < 0 || double.IsNaN(fadeInMs))
            throw new ArgumentOutOfRangeException(nameof(fadeInMs), fadeInMs, "The fade-in must be zero or positive.");
        if (fadeOutMs < 0 || double.IsNaN(fadeOutMs))
            throw new ArgumentOutOfRangeException(nameof(fadeOutMs), fadeOutMs, "The fade-out must be zero or positive.");
        _fadeInMs = fadeInMs;
        _fadeOutMs = fadeOutMs;
    }

    // [impl->REQ-RENDER-CLEAN]
    /// <summary>
    /// Scales one mono frame in place, ramping the gain linearly toward 1 while <paramref name="open"/>
    /// and toward 0 otherwise, at the fade rate for that direction. Returns true when the whole frame
    /// came out silent (gain 0 throughout), so the caller may push zeros instead.
    /// </summary>
    public bool Apply(Span<float> frame, bool open, int sampleRate)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "The sample rate must be positive.");

        var target = open ? 1.0 : 0.0;
        var gain = _gain;
        if (gain == target)
        {
            if (target == 0.0) frame.Clear();
            return target == 0.0;
        }

        var fadeMs = open ? _fadeInMs : _fadeOutMs;
        var step = fadeMs <= 0 ? 1.0 : 1000.0 / (fadeMs * sampleRate);
        var silent = true;
        for (var i = 0; i < frame.Length; i++)
        {
            if (gain < target)
            {
                gain += step;
                if (gain > target) gain = target;
            }
            else if (gain > target)
            {
                gain -= step;
                if (gain < target) gain = target;
            }

            if (gain == 0.0)
            {
                frame[i] = 0f;
                continue;
            }
            silent = false;
            frame[i] = (float)(frame[i] * gain);
        }
        _gain = gain;
        return silent;
    }
}

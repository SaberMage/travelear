using BepInEx.Logging;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TravelEar;

/// <summary>
/// The Local Voice renderer (docs/DESIGN.md): a mod-owned GameObject carrying the round-trip
/// provider and a game <c>VoicePlayer</c> (<c>PlayerType = Clean</c>) fed by it. The VoicePlayer
/// does the rest itself, exactly as it does for the game's own voices: on enable it plays its
/// <c>Cue</c> through <c>AudioPlayHelper.Play</c> with a streaming clip of constant 1.0, puts
/// itself at index 0 of the source's filter list, and switches the source's
/// <c>AudioFilterMixer</c> into synthesizer mode; if its controller is ever reclaimed it re-runs
/// Awake/OnEnable from Update. The renderer only (1) builds it once the audio system exists,
/// (2) keeps it at the listener (Self-Ear: distance 0) and (3) keeps the Tap pointed at the
/// controller's current mixer.
/// <para>
/// Cue: the last entry of <c>GlobalAudioEffects.Instance.VoiceCues</c>, the same cue kind the
/// game hands remote players (spatial settings, attenuation and RTPCs identical). Its mixer
/// group is irrelevant because the Tap removes the signal before the mixer.
/// </para>
/// </summary>
internal sealed class LocalVoiceRenderer
{
    private const float StatsIntervalSeconds = 10f;

    private readonly ManualLogSource _log;
    private readonly SinkPump _pump;
    private LocalVoiceDecoder _decoder;
    private GameObject _voiceObject;
    private VoicePlayer _player;
    private IntPtr _tapTarget;
    private bool _built;
    private bool _reportedNoCues;
    private float _nextStats;
    private long _decodeErrors;
    private long _framesDecoded;
    private int _lastDecodedSamples;

    public static LocalVoiceRenderer Instance { get; private set; }

    /// <summary>Unity's DSP output rate, read when the renderer is built (0 before).</summary>
    public static volatile int SampleRate;

    public LocalVoiceRenderer(ManualLogSource log, SinkPump pump)
    {
        _log = log;
        _pump = pump;
        Instance = this;
    }

    /// <summary>Called every frame by <see cref="TravelEarBehaviour"/> on the main thread.</summary>
    public void Tick()
    {
        if (!_built)
        {
            if (GlobalAudioEffects.Instance is null || AudioManager.Instance is null) return;
            Build();
            if (!_built) return;
        }

        if (_voiceObject is not null)
            _voiceObject.transform.position = AudioManager.ListenerPosition;

        RefreshTapTarget();

        if (Time.unscaledTime >= _nextStats)
        {
            _nextStats = Time.unscaledTime + StatsIntervalSeconds;
            LogStats();
        }
    }

    // [impl->REQ-VOICE-ROUNDTRIP]
    private void Build()
    {
        var cues = GlobalAudioEffects.Instance.VoiceCues;
        if (cues is null || cues.Length == 0)
        {
            if (!_reportedNoCues)
            {
                _reportedNoCues = true;
                _log.LogWarning("Local Voice: GlobalAudioEffects.VoiceCues is empty; waiting.");
            }
            return;
        }

        var cue = cues[cues.Length - 1];
        AudioSettings.GetDSPBufferSize(out var bufferLength, out var numBuffers);
        SampleRate = AudioSettings.outputSampleRate;
        _log.LogInfo($"Local Voice: DSP {SampleRate} Hz, buffer {bufferLength} x {numBuffers}, speaker mode {AudioSettings.speakerMode}; cue '{cue.name}' ({cues.Length} voice cues).");
        if (SampleRate != LocalVoiceDecoder.SampleRate)
            _log.LogWarning($"Local Voice: DSP rate {SampleRate} != decoder rate {LocalVoiceDecoder.SampleRate}; the provider does not resample, expect a pitch shift.");

        _voiceObject = new GameObject("TravelEar.LocalVoice");
        _voiceObject.SetActive(false);
        Object.DontDestroyOnLoad(_voiceObject);
        _voiceObject.transform.position = AudioManager.ListenerPosition;

        var provider = RoundTripProvider.Create(_voiceObject);

        _player = _voiceObject.AddComponent<VoicePlayer>();
        _player.Cue = cue;
        _player.PlayerType = VoicePlayer.VoicePlayerType.Clean;
        _player.Volume = new AudioVolume(1f, "TravelEar");
        _player.LocalVoiceProvider = provider;

        _decoder = new LocalVoiceDecoder();
        OutboundVoiceTap.FrameEncoded += OnFrameEncoded;

        _voiceObject.SetActive(true); // provider Awake (ring), VoicePlayer Awake (clip) + OnEnable (play, filter 0)

        var ring = provider.CachedVoiceData;
        _log.LogInfo($"Local Voice: renderer built; provider ring {ring?.Length ?? 0} samples, controller {(_player.Controller is null ? "pending" : "live")}.");
        _built = true;
    }

    private void RefreshTapTarget()
    {
        var controller = _player?.Controller;
        var mixer = controller?.FilterMixer;
        var target = mixer is null ? IntPtr.Zero : mixer.Pointer;
        if (target == _tapTarget) return;

        _tapTarget = target;
        TapFilter.SetTarget(target);
        if (target == IntPtr.Zero)
            _log.LogInfo("Local Voice: controller gone; Tap idle until the VoicePlayer re-plays.");
        else
            _log.LogInfo($"Local Voice: Tap armed on mixer of '{controller.name}' (synth mode {mixer.SynthesizerMode}, {mixer.Filters?.Count ?? -1} filters).");
    }

    /// <summary>Encoder thread: decode with the game's decoder and push into the provider ring.</summary>
    private void OnFrameEncoded(int sequence, byte[] opus)
    {
        var first = Interlocked.Read(ref _framesDecoded) == 0;
        try
        {
            if (first) _log.LogInfo($"Local Voice: frame 1 step A, decoding {opus.Length} bytes on thread {Environment.CurrentManagedThreadId}.");
            var count = _decoder.Decode(opus, out var pcm);
            if (first) _log.LogInfo($"Local Voice: frame 1 step B, decoded {count} samples.");
            if (count <= 0) return;
            _lastDecodedSamples = count;
            RoundTripProvider.Push(pcm);
            if (first) _log.LogInfo($"Local Voice: frame 1 step C, pushed; provider write head {RoundTripProvider.Provider?.CachedVoiceWriteHead}.");
            Interlocked.Increment(ref _framesDecoded);
        }
        catch (Exception e)
        {
            if (Interlocked.Increment(ref _decodeErrors) <= 3)
                _log.LogError($"Local Voice: decode/push failed: {e}");
        }
    }

    private void LogStats()
    {
        var provider = RoundTripProvider.Provider;
        _log.LogInfo(
            $"Local Voice stats: encoded {OutboundVoiceTap.Frames}, decoded {Interlocked.Read(ref _framesDecoded)} ({_lastDecodedSamples} smp), " +
            $"pushed {Interlocked.Read(ref RoundTripProvider.FramesPushed)}, errors {Interlocked.Read(ref _decodeErrors)}; " +
            $"provider write head {provider?.CachedVoiceWriteHead ?? -1}; " +
            $"tap blocks {Interlocked.Read(ref TapFilter.Blocks)} ({TapFilter.Channels} ch x {TapFilter.BlockLength}, peak {TapFilter.LastPeak:F3}), " +
            $"ring {TapFilter.Ring.Count} smp, dropped {TapFilter.Ring.DroppedSamples}, underruns {TapFilter.Ring.Underruns}; " +
            $"sink {(_pump.Connected ? "connected" : "waiting")}, frames sent {Interlocked.Read(ref _pump.FramesSent)}.");
    }
}

/// <summary>Injected MonoBehaviour that drives the renderer from Unity's main thread.</summary>
internal sealed class TravelEarBehaviour : MonoBehaviour
{
    public TravelEarBehaviour(IntPtr pointer) : base(pointer) { }

    private void Update()
    {
        try
        {
            LocalVoiceRenderer.Instance?.Tick();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Local Voice: renderer tick failed: {e}");
        }
    }
}

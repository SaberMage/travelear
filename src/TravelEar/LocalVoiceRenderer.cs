using System.Diagnostics;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
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
/// Awake/OnEnable from Update. The renderer (1) builds it once the audio system exists, (2) pins
/// the emitter to the listener (Self-Ear), (3) keeps the Tap pointed at the controller's current
/// mixer and (4) keeps the provider's read head a fixed, small distance behind the write head.
/// <para>
/// Cue: the last entry of <c>GlobalAudioEffects.Instance.VoiceCues</c>, the same cue kind the
/// game hands remote players (spatial settings, attenuation and RTPCs identical). Its mixer
/// group is irrelevant because the Tap removes the signal before the mixer.
/// </para>
/// <para>
/// Emitter placement (M1 T3 run 2): letting the pooled source follow our transform through the
/// game's own follow logic lags one frame behind the camera, which flips left/right while
/// strafing, and a source at the exact listener position produces stereo artifacts. The source
/// transform is therefore parented rigidly to the <c>AudioListener</c> with a forward offset
/// (config <c>SelfEarForwardMeters</c>, default 3 in, the operator's Self-Ear note in
/// docs/DESIGN.md) and the controller's follow target is cleared.
/// </para>
/// </summary>
internal sealed class LocalVoiceRenderer
{
    private const float StatsIntervalSeconds = 10f;

    /// <summary>A gap this long between pushed frames starts a new talk burst.</summary>
    private const int BurstGapMs = 250;

    /// <summary>Read head distance behind the write head at a burst start, in Opus frames.</summary>
    private const float ReadHeadMarginFrames = 1.5f;

    /// <summary>Lag beyond this many frames while talking triggers a resync (drift / missed burst).</summary>
    private const float MaxLagFrames = 4f;

    private readonly ManualLogSource _log;
    private readonly SinkPump _pump;
    private readonly float _forwardMeters;
    private LocalVoiceDecoder _decoder;
    private GameObject _voiceObject;
    private VoicePlayer _player;
    private IntPtr _tapTarget;
    private Transform _anchor;
    private Transform _pinnedSource;
    private bool _reportedNoAnchor;
    private Il2CppSystem.ArraySegment<float> _silence;
    private int _silenceSamples;
    private long _silenceFrames;
    private Transform _pinnedSourceOriginalParent;
    private bool _built;
    private bool _reportedNoCues;
    private float _nextStats;
    private long _decodeErrors;
    private long _framesDecoded;
    private long _lastPushTimestamp;
    private long _resyncs;
    private int _lastDecodedSamples;
    private volatile int _ringChannels;

    public static LocalVoiceRenderer Instance { get; private set; }

    /// <summary>Unity's DSP output rate, read when the renderer is built (0 before).</summary>
    public static volatile int SampleRate;

    public LocalVoiceRenderer(ManualLogSource log, SinkPump pump, float forwardMeters)
    {
        _log = log;
        _pump = pump;
        _forwardMeters = forwardMeters;
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

        RefreshTapTarget();
        PinEmitter();
        GuardLag();
        KeepRingFresh();

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
        _log.LogInfo($"Local Voice: DSP {SampleRate} Hz, buffer {bufferLength} x {numBuffers}, speaker mode {AudioSettings.speakerMode}; cue '{cue.name}' ({cues.Length} voice cues); emitter {_forwardMeters * 100f:F1} cm ahead of the listener.");
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
        _ringChannels = Math.Max(1, provider._channelCount);
        _log.LogInfo($"Local Voice: renderer built; provider ring {ring?.Length ?? 0} samples x {_ringChannels} ch, controller {(_player.Controller is null ? "pending" : "live")}.");
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

    // [impl->REQ-EAR-SELF]
    /// <summary>
    /// Parents the live source transform to the AudioListener at the forward offset and clears the
    /// controller's follow target; restores the previous parent when the controller goes away so a
    /// pooled source never stays stuck on the listener.
    /// </summary>
    private void PinEmitter()
    {
        var controller = _player?.Controller;
        var source = controller?.AudioSource;
        var sourceTransform = source is null ? null : source.transform;

        if (_pinnedSource is not null && (sourceTransform is null || sourceTransform.Pointer != _pinnedSource.Pointer))
        {
            try
            {
                _pinnedSource.SetParent(_pinnedSourceOriginalParent, true);
            }
            catch (Exception e)
            {
                _log.LogWarning($"Local Voice: could not restore the pooled source's parent: {e.Message}");
            }
            _pinnedSource = null;
            _pinnedSourceOriginalParent = null;
        }

        if (sourceTransform is null) return;

        if (_anchor is null || _anchor.Pointer == IntPtr.Zero || _anchor.gameObject is null)
        {
            _anchor = FindAnchor();
            if (_anchor is null)
            {
                // Unpinned: at least keep the source on the listener through the game's follow logic.
                _voiceObject.transform.position = AudioManager.ListenerPosition;
                return;
            }
        }

        var pinned = _pinnedSource is not null && _pinnedSource.parent is not null && _pinnedSource.parent.Pointer == _anchor.Pointer;
        if (pinned) return;

        _pinnedSource = sourceTransform;
        _pinnedSourceOriginalParent = sourceTransform.parent;
        controller.FollowTransform = null;
        sourceTransform.SetParent(_anchor, false);
        sourceTransform.localPosition = new Vector3(0f, 0f, _forwardMeters);
        sourceTransform.localRotation = Quaternion.identity;
        _voiceObject.transform.SetParent(_anchor, false);
        _voiceObject.transform.localPosition = sourceTransform.localPosition;
        _log.LogInfo($"Local Voice: emitter pinned to '{_anchor.name}', {_forwardMeters * 100f:F1} cm forward along its view axis (follow cleared).");
    }

    /// <summary>
    /// The transform whose forward axis is the player's view: the main camera (the game's
    /// <c>AudioListenerController</c> follows it), else the listener itself. Never
    /// <c>Object.FindObjectOfType</c>: that overload is stripped from this IL2CPP build.
    /// </summary>
    private Transform FindAnchor()
    {
        try
        {
            var camera = Camera.main;
            if (camera is not null) return camera.transform;
            var listener = AudioManager.Instance?.ListenerController?._listener;
            if (listener is not null) return listener.transform;
        }
        catch (Exception e)
        {
            if (!_reportedNoAnchor) _log.LogWarning($"Local Voice: anchor lookup failed: {e.Message}");
        }
        if (!_reportedNoAnchor)
        {
            _reportedNoAnchor = true;
            _log.LogWarning("Local Voice: no main camera or listener yet; emitter follows the listener position until one appears.");
        }
        return null;
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

            var now = Stopwatch.GetTimestamp();
            var gapMs = (now - Interlocked.Read(ref _lastPushTimestamp)) * 1000.0 / Stopwatch.Frequency;
            Interlocked.Exchange(ref _lastPushTimestamp, now);

            RoundTripProvider.Push(pcm);
            if (gapMs > BurstGapMs) ResyncReadHead(count, "burst start");

            if (first) _log.LogInfo($"Local Voice: frame 1 step C, pushed; provider write head {RoundTripProvider.Provider?.CachedVoiceWriteHead}.");
            Interlocked.Increment(ref _framesDecoded);
        }
        catch (Exception e)
        {
            if (Interlocked.Increment(ref _decodeErrors) <= 3)
                _log.LogError($"Local Voice: decode/push failed: {e}");
        }
    }

    /// <summary>
    /// Puts the VoicePlayer's read head a fixed margin behind the provider's write head. The game
    /// starts the read head two DSP buffer sets behind the write head at enable time and only
    /// resyncs on a mic reset, so with push-to-talk bursts the phase between the two was random:
    /// up to a full ring (341 ms at 32768 stereo samples) of pure latency.
    /// </summary>
    private void ResyncReadHead(int frameSamples, string reason)
    {
        var provider = RoundTripProvider.Provider;
        var ring = provider?.CachedVoiceData;
        if (provider is null || ring is null || _player is null) return;
        var length = ring.Length;
        var margin = (int)(frameSamples * ReadHeadMarginFrames) * _ringChannels;
        var readHead = ((provider.CachedVoiceWriteHead - margin) % length + length) % length;
        _player.UpdateReadHead(readHead);
        if (Interlocked.Increment(ref _resyncs) <= 5)
            _log.LogInfo($"Local Voice: read head resync ({reason}): write {provider.CachedVoiceWriteHead}, read {readHead}, margin {margin} samples.");
    }

    // [impl->REQ-VOICE-CONTINUOUS]
    /// <summary>
    /// Main thread, between talk bursts: pushes zero frames so the provider's write head keeps
    /// moving ahead of the read head. The game's own provider is fed by the mic continuously, but
    /// Outbound Voice only exists while transmitting; without this the VoicePlayer loops the last
    /// ring's worth of a burst (mostly the mic noise floor) forever, which the operator heard as a
    /// raised noise floor in-world (M1 T3 runs 2 and 3).
    /// </summary>
    private void KeepRingFresh()
    {
        var provider = RoundTripProvider.Provider;
        var ring = provider?.CachedVoiceData;
        if (provider is null || ring is null || _player is null) return;

        var frameSamples = _lastDecodedSamples > 0 ? _lastDecodedSamples : 2880;
        var sinceMs = (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastPushTimestamp)) * 1000.0 / Stopwatch.Frequency;
        if (sinceMs < frameSamples * 1000.0 / LocalVoiceDecoder.SampleRate) return; // a burst is feeding the ring

        if (_silenceSamples != frameSamples)
        {
            _silence = new Il2CppSystem.ArraySegment<float>(new Il2CppStructArray<float>(frameSamples));
            _silenceSamples = frameSamples;
        }

        var length = ring.Length;
        var frame = frameSamples * _ringChannels;
        for (var pushes = 0; pushes < 3; pushes++)
        {
            var lag = ProviderLagSamples();
            if (lag > length / 2) // reader overran the writer: put it back behind fresh silence
            {
                ResyncReadHead(frameSamples, "reader overran");
                lag = ProviderLagSamples();
            }
            if (lag >= frame * ReadHeadMarginFrames) break;
            RoundTripProvider.Push(_silence);
            _silenceFrames++;
        }
    }

    /// <summary>Main thread: resync if the lag drifted far while a burst is in progress.</summary>
    private void GuardLag()
    {
        if (_lastDecodedSamples <= 0) return;
        var sinceMs = (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastPushTimestamp)) * 1000.0 / Stopwatch.Frequency;
        if (sinceMs > BurstGapMs) return;
        var lag = ProviderLagSamples();
        var frame = _lastDecodedSamples * _ringChannels;
        if (lag > frame * MaxLagFrames || lag < frame * 0.25f)
            ResyncReadHead(_lastDecodedSamples, $"lag {lag} samples");
    }

    /// <summary>Samples between the provider's write head and the VoicePlayer's read head (0 = reader caught up).</summary>
    private int ProviderLagSamples()
    {
        var provider = RoundTripProvider.Provider;
        var ring = provider?.CachedVoiceData;
        if (provider is null || ring is null || _player is null) return 0;
        var length = ring.Length;
        return ((provider.CachedVoiceWriteHead - _player._readHead) % length + length) % length;
    }

    private void LogStats()
    {
        var provider = RoundTripProvider.Provider;
        var lag = ProviderLagSamples();
        var lagMs = SampleRate > 0 && _ringChannels > 0 ? lag * 1000.0 / (SampleRate * _ringChannels) : 0;
        _log.LogInfo(
            $"Local Voice stats: encoded {OutboundVoiceTap.Frames}, decoded {Interlocked.Read(ref _framesDecoded)} ({_lastDecodedSamples} smp), " +
            $"pushed {Interlocked.Read(ref RoundTripProvider.FramesPushed)}, errors {Interlocked.Read(ref _decodeErrors)}; " +
            $"provider write head {provider?.CachedVoiceWriteHead ?? -1}, lag {lag} smp ({lagMs:F0} ms), resyncs {Interlocked.Read(ref _resyncs)}, silence frames {_silenceFrames}; " +
            $"tap blocks {Interlocked.Read(ref TapFilter.Blocks)} ({TapFilter.Channels} ch x {TapFilter.BlockLength}, peak {TapFilter.LastPeak:F3}), " +
            $"ring {TapFilter.Ring.Count} smp, dropped {TapFilter.Ring.DroppedSamples}, underruns {TapFilter.Ring.Underruns}; " +
            $"sink {(_pump.Connected ? "connected" : "waiting")}, frames sent {Interlocked.Read(ref _pump.FramesSent)}.");
        LogGameAudioState();
    }

    /// <summary>Diagnostics for "no game audio" reports: global listener/mixer state and our own source.</summary>
    private void LogGameAudioState()
    {
        try
        {
            var manager = AudioManager.Instance;
            var source = _player?.Controller?.AudioSource;
            var sourceText = source is null
                ? "source none"
                : $"source playing={source.isPlaying} vol={source.volume:F2} mute={source.mute} spatial={source.spatialBlend:F2} " +
                  $"group='{source.outputAudioMixerGroup?.name}' clip='{source.clip?.name}' local={source.transform.localPosition} parent='{source.transform.parent?.name}'";
            _log.LogInfo(
                $"Game audio: listener vol={AudioListener.volume:F2} pause={AudioListener.pause}; " +
                $"master={(manager?.MasterVolume is null ? -1f : (float)manager.MasterVolume):F2} " +
                $"globalMute={(manager?.GlobalMuteVolume is null ? -1f : (float)manager.GlobalMuteVolume):F2} " +
                $"vo={(manager?.VOVolume is null ? -1f : (float)manager.VOVolume):F2}; listener at {AudioManager.ListenerPosition}; {sourceText}.");
        }
        catch (Exception e)
        {
            _log.LogWarning($"Game audio: state read failed: {e.Message}");
        }
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

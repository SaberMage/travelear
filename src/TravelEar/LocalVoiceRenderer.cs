using System.Diagnostics;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TravelEar.Core;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TravelEar;

/// <summary>
/// The Local Voice renderer (docs/DESIGN.md). With the default feed point (ADR-0005, config
/// <c>Fidelity.SinkFeed = Encoder</c>) it is the encoder-thread pipeline alone: decode the tapped
/// frame with the game's decoder, gate and fade it on the "peers receive" signal, run the remote
/// path's dynamics and the (optional) voice EQ port, and write the frame into <see cref="SinkFeed"/>;
/// no game object, no provider ring, no Tap, so neither the ring lag nor the game's audio-thread
/// stalls reach the Sink. The main thread only samples the transmit signal and drives the
/// makeup-gain loop.
/// <para>
/// With <c>SinkFeed = VoicePlayer</c> (the M1-M2 path, kept for A/B) it is instead a mod-owned
/// GameObject carrying the round-trip provider and a game <c>VoicePlayer</c> (<c>PlayerType = Clean</c>) fed by it. The VoicePlayer
/// does the rest itself, exactly as it does for the game's own voices: on enable it plays its
/// <c>Cue</c> through <c>AudioPlayHelper.Play</c> with a streaming clip of constant 1.0, puts
/// itself at index 0 of the source's filter list, and switches the source's
/// <c>AudioFilterMixer</c> into synthesizer mode; if its controller is ever reclaimed it re-runs
/// Awake/OnEnable from Update. The renderer (1) builds it once the audio system exists, (2) pins
/// the emitter to the listener (Self-Ear), (3) keeps the Tap pointed at the controller's current
/// mixer, (4) keeps the provider's read head a fixed, small distance behind the write head and
/// (5) gates what the ring receives on the "peers receive" signal (<see cref="TransmitSignal"/>),
/// pushing silence instead of the decoded frame while nothing is sent to peers, and (6) runs the
/// decoded frame through the processing every remote voice gets before it reaches the ring
/// (<see cref="VoiceDynamics"/> on the encoder thread, <see cref="Core.VoiceMakeupGain"/> once per
/// frame; docs/reference/big-walk-voice-dsp.md), with the game's 400 Hz voice EQ attached to the
/// pooled source when config <c>SelfEarEqDryWet</c> asks for any of its wet path.
/// </para>
/// <para>
/// Cue: the last entry of <c>GlobalAudioEffects.Instance.VoiceCues</c>, the same cue kind the
/// game hands remote players (spatial settings, attenuation and RTPCs identical). Its mixer
/// group is irrelevant because the Tap removes the signal before the mixer.
/// </para>
/// <para>
/// Emitter placement (M1 T3 run 2): letting the pooled source follow our transform through the
/// game's own follow logic lags one frame behind the camera, which flips left/right while
/// strafing, and a source at the exact listener position produces stereo artifacts. Our object
/// and the pooled source are therefore both parented to the listener's anchor at a forward offset
/// (config <c>SelfEarForwardMeters</c>, default 3 in, the operator's Self-Ear note in
/// docs/DESIGN.md) and the controller keeps following our object, so its per-frame write resolves
/// to that exact local offset (run 5). Clearing the follow target instead drops the source at the
/// world position it was played at (run 4).
/// </para>
/// </summary>
internal sealed class LocalVoiceRenderer
{
    private const float StatsIntervalSeconds = 10f;

    /// <summary>A gap this long between pushed frames starts a new talk burst.</summary>
    private const int BurstGapMs = 250;

    /// <summary>Read head distance behind the write head at a burst start, in Opus frames (config <c>Fidelity.ReadHeadMarginFrames</c>).</summary>
    private readonly float ReadHeadMarginFrames;

    /// <summary>Lag beyond this many frames while talking triggers a resync (drift / missed burst).</summary>
    private const float MaxLagFrames = 4f;

    private readonly ManualLogSource _log;
    private readonly SinkPump _pump;
    private readonly SinkFeedPoint _feed;
    private PeakingEq _eqCore; // encoder thread only (Encoder feed)

    // Mixer Stage (REQ-MIXER-RESYNTH). Main thread: _mixerModel steps the game's formulas from the
    // local player's state and publishes the four floats; encoder thread: _mixerStage applies them.
    private readonly MixerStageToggles _mixerToggles;
    private readonly float _reverbDecaySeconds;
    private readonly MixerStageModel _mixerModel = new();
    private MixerStage _mixerStage;
    private volatile float _mixerDryDb;
    private volatile float _mixerHighDb;
    private volatile float _mixerFallDb = MixerStageModel.FloorDb;
    private volatile float _mixerBoostDb = MixerStageModel.FloorDb;
    private volatile bool _mixerInputsReadable;
    private bool _reportedMixerError;
    private bool _lastInDanger;
    private long _fallEvents;

    // Megaphone voice (REQ-RENDER-MEGAPHONE). Main thread: _megaphoneActive from the held prop's
    // radio assigner; encoder thread: _megaphone renders into _megaphoneScratch and mixes.
    private readonly MegaphoneToggles _megaphoneToggles;
    private readonly MegaphoneMix _megaphoneMix;
    private MegaphoneVoice _megaphone;
    private readonly float[] _megaphoneScratch = new float[8192];
    private volatile bool _megaphoneActive;
    private volatile bool _megaphoneHeld;
    private bool _lastMegaphoneActive;
    private bool _lastMegaphoneHeld;
    private bool _reportedMegaphoneError;
    private long _megaphoneBroadcasts;
    private long _megaphoneFrames;

    // Environment reverb (REQ-MIXER-RESYNTH, M3 T2b). Main thread: _envSnapshot is the listener's
    // live SFX Reverb parameter set as the game wrote it this frame; encoder thread: _env applies
    // it last, on the sum of the Clean and Megaphone voices.
    private readonly EnvironmentReverbToggles _envToggles;
    private EnvironmentReverb _env;
    private volatile EnvironmentReverbSnapshot _envSnapshot;
    private volatile bool _envReadable;
    private bool _reportedEnvError;
    private bool _reportedBasicMode;
    private bool? _masterWetWritten;
    private float _nextEnvLog;
    private sealed class EnvironmentReverbSnapshot
    {
        public readonly EnvironmentReverbParams Params;
        public readonly bool Dynamic;
        public readonly float RoomSize, Outdoorness, ReverbTime, Diffusion;
        public EnvironmentReverbSnapshot(in EnvironmentReverbParams p, bool dynamic, float roomSize, float outdoorness, float reverbTime, float diffusion)
        {
            Params = p; Dynamic = dynamic; RoomSize = roomSize; Outdoorness = outdoorness; ReverbTime = reverbTime; Diffusion = diffusion;
        }
    }
    private readonly float _forwardMeters;
    private readonly bool _gateEnabled;
    private readonly TransmitGate _gate = new();
    private readonly TransmitFader _fader = new(0, 0); // a hard gate until the game's fade is read
    private readonly float _fadeOutOverrideMs;
    private readonly float _holdOverrideMs;
    private readonly float _outputTrim;
    private readonly float _outputTrimDb;
    /// <summary>Samples of reverb tail still to render through the gate's silence after the last passed frame.</summary>
    private int _tailSamplesLeft;
    private long _tailFrames;
    private volatile bool _megaphoneRoomOpen;
    private string _megaphonePath = "none";
    private float _nextMegaphoneProbe;
    private int _megaphoneProbes;
    /// <summary>How long the listener-side reverbs keep ringing after the gate closes (the game's longest DecayTime is 16 s, the fall return 4 s; a talk burst ends well before that in practice).</summary>
    private const float TailSeconds = 4f;
    private bool _fadeConfigured;
    private bool _reportedFadeError;
    private volatile bool _transmitting = true; // fail open until the signal is read
    private string _lastSignalState;
    private long _signalChanges;
    private bool _reportedSignalError;
    private Il2CppSystem.ArraySegment<float> _gateSilence; // encoder thread only
    private int _gateSilenceSamples;
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

    // Remote-path processing (REQ-RENDER-CLEAN). Encoder thread: _dynamics, _scratch. Main thread:
    // _makeup, the EQ. The volatile floats cross between them once per block / frame.
    private readonly VoiceDynamics _dynamics = new();
    private readonly Core.VoiceMakeupGain _makeup = new();
    private readonly float[] _scratch = new float[8192];
    private readonly float[] _captureScratch = new float[8192];
    private volatile float _makeupGain = 1f;
    private volatile float _threshold = 0.528f; // ThresholdFor(ReferenceArv) until the game's statics are read
    private volatile float _lastArv;
    private volatile bool _lastPushWasVoice;
    private bool _reportedMakeupError;
    private readonly float _eqDryWet;
    private BiquadFilters _eq;
    private AudioSourceController _eqController;

    public static LocalVoiceRenderer Instance { get; private set; }

    /// <summary>Unity's DSP output rate, read when the renderer is built (0 before).</summary>
    public static volatile int SampleRate;

    public LocalVoiceRenderer(ManualLogSource log, SinkPump pump, SinkFeedPoint feed, float forwardMeters, bool transmitGate, float transmitFadeOutMs, float transmitHoldMs, float outputTrimDb, float readHeadMarginFrames, float eqDryWet,
        MixerStageToggles mixerToggles, float reverbDecaySeconds, MegaphoneToggles megaphoneToggles, MegaphoneMix megaphoneMix, EnvironmentReverbToggles environmentToggles)
    {
        _envToggles = environmentToggles;
        _log = log;
        _pump = pump;
        _feed = feed;
        _megaphoneToggles = megaphoneToggles;
        _megaphoneMix = megaphoneMix;
        _mixerToggles = mixerToggles;
        _reverbDecaySeconds = reverbDecaySeconds > 0 && !float.IsNaN(reverbDecaySeconds) ? reverbDecaySeconds : 1.5f;
        _forwardMeters = forwardMeters;
        _gateEnabled = transmitGate;
        _fadeOutOverrideMs = transmitFadeOutMs > 0 && !float.IsNaN(transmitFadeOutMs) ? transmitFadeOutMs : 0f;
        _holdOverrideMs = transmitHoldMs > 0 && !float.IsNaN(transmitHoldMs) ? transmitHoldMs : 0f;
        _outputTrimDb = float.IsNaN(outputTrimDb) ? 0f : Mathf.Clamp(outputTrimDb, -40f, 20f);
        _outputTrim = MixerStageModel.Gain(_outputTrimDb);
        _eqDryWet = float.IsNaN(eqDryWet) ? 0f : Mathf.Clamp01(eqDryWet);
        ReadHeadMarginFrames = readHeadMarginFrames > 0 && !float.IsNaN(readHeadMarginFrames) ? readHeadMarginFrames : 1.5f;
        Instance = this;
    }

    /// <summary>Called every frame by <see cref="TravelEarBehaviour"/> on the main thread.</summary>
    public void Tick()
    {
        if (!_built)
        {
            if (_feed == SinkFeedPoint.Encoder) BuildEncoderFeed();
            else
            {
                if (GlobalAudioEffects.Instance is null || AudioManager.Instance is null) return;
                Build();
            }
            if (!_built) return;
        }

        if (_feed == SinkFeedPoint.VoicePlayer)
        {
            RefreshTapTarget();
            PinEmitter();
        }
        SampleTransmitSignal();
        UpdateMakeupGain();
        if (_feed == SinkFeedPoint.Encoder)
        {
            UpdateMixerStage();
            UpdateMegaphone();
            UpdateEnvironmentReverb();
        }
        if (_feed == SinkFeedPoint.VoicePlayer)
        {
            GuardLag();
            KeepRingFresh();
        }

        if (Time.unscaledTime >= _nextStats)
        {
            _nextStats = Time.unscaledTime + StatsIntervalSeconds;
            LogStats();
        }
    }

    // [impl->REQ-VOICE-ROUNDTRIP]
    // [impl->REQ-HAZARD-NO-GAME-AUDIO-LEAK]
    /// <summary>
    /// Encoder feed (ADR-0005): the decoder and the Sink ring are all there is. Nothing is added to
    /// the scene, so Local Voice cannot enter the game's audio. The voice EQ, when its wet mix is
    /// above 0, is the Core port of the game's filter instead of an attached component.
    /// </summary>
    private void BuildEncoderFeed()
    {
        SampleRate = LocalVoiceDecoder.SampleRate;
        _decoder = new LocalVoiceDecoder();
        _eqCore = _eqDryWet > 0f ? PeakingEq.GameVoiceEq(_eqDryWet, LocalVoiceDecoder.SampleRate) : null;
        _mixerStage = new MixerStage(LocalVoiceDecoder.SampleRate, _reverbDecaySeconds);
        _megaphone = new MegaphoneVoice(LocalVoiceDecoder.SampleRate);
        _env = new EnvironmentReverb(LocalVoiceDecoder.SampleRate);
        OutboundVoiceTap.FrameEncoded += OnFrameEncoded;
        _log.LogInfo($"Local Voice: encoder feed built; {LocalVoiceDecoder.SampleRate} Hz mono straight to the Sink ring ({SinkFeed.Ring.Capacity} samples); voice EQ {(_eqCore is null ? "off" : $"wet {_eqDryWet:F2} (Core port)")}; " +
                     $"mixer stage {(_mixerToggles.Master ? $"on (dry {_mixerToggles.Dry}, high {_mixerToggles.High}, fall {_mixerToggles.ReverbFall}, boost {_mixerToggles.ReverbBoost}, reverb {_reverbDecaySeconds:F1} s)" : "off")}; " +
                     $"environment reverb {(_envToggles.Master ? $"on (dry copy {_envToggles.DryCopy}, bus gains {_envToggles.BusGains}, voice slider {_envToggles.VoiceSlider})" : "off")}.");
        _built = true;
    }

    // [impl->REQ-MIXER-RESYNTH]
    // [impl->REQ-HAZARD-NO-PARTIAL-FIDELITY]
    /// <summary>
    /// Main thread, once per frame: the game's voice-channel model evaluated at the Self-Ear from the
    /// local player's state (falling, synced outdoorness) and the listener's dynamic reverb, exactly
    /// as <c>PlayerVoicePlaybackControl.Update</c> would for a remote copy of this player. The game
    /// never writes the channel floats for the local player, so the mod computes them. Inputs that
    /// cannot be read bypass the stage (logged once) rather than driving it with guesses.
    /// </summary>
    private void UpdateMixerStage()
    {
        if (!_mixerToggles.Master) return;
        try
        {
            var me = WorldManager.localPlayerCharacter;
            if (me is null)
            {
                _mixerInputsReadable = false;
                return;
            }
            var inDanger = me.faller?.isInDanger ?? false;
            var speakerOutdoor = me.playerNetworking?.outdoorness ?? 0f;
            var reverb = AudioManager.Instance?.AudioDynamicReverb;
            var listenerOutdoor = reverb is null ? 1f : reverb.Outdoorness;
            _mixerModel.Step(MixerStageInputs.SelfEar(inDanger, speakerOutdoor, listenerOutdoor, 1f), Time.deltaTime);
            _mixerDryDb = _mixerModel.DryDb;
            _mixerHighDb = _mixerModel.HighDb;
            _mixerFallDb = _mixerModel.ReverbFallWetDb;
            _mixerBoostDb = _mixerModel.ReverbBoostWetDb;
            _mixerInputsReadable = true;
            if (inDanger != _lastInDanger)
            {
                _lastInDanger = inDanger;
                if (inDanger && ++_fallEvents <= 10)
                    _log.LogInfo($"Mixer stage: falling (outdoorness {speakerOutdoor:F2}), fall send {_mixerModel.ReverbFallWetDb:F1} dB.");
            }
        }
        catch (Exception e)
        {
            _mixerInputsReadable = false;
            if (_reportedMixerError) return;
            _reportedMixerError = true;
            _log.LogWarning($"Mixer stage: inputs unreadable, stage bypassed: {e.Message}");
        }
    }

    // [impl->REQ-MIXER-RESYNTH]
    // [impl->REQ-HAZARD-NO-PARTIAL-FIDELITY]
    /// <summary>
    /// Main thread, once per frame: the listener's environment reverb exactly as the game wrote it
    /// this frame, the fourteen SFX Reverb floats from <c>AudioDynamicReverb</c> (Dynamic mode) or
    /// <c>AudioBasicReverb</c> (Basic mode), <c>Bypass</c> (which the game answers with dry 0 dB and
    /// no room), the Master Wet return level and the voice slider. No smoothing of the mod's own:
    /// the game smooths its inputs. Unreadable inputs bypass the stage, logged once.
    /// </summary>
    private void UpdateEnvironmentReverb()
    {
        if (!_envToggles.Master) return;
        try
        {
            var am = AudioManager.Instance;
            var gae = GlobalAudioEffects.Instance;
            if (am is null || gae is null)
            {
                _envReadable = false;
                return;
            }
            // AudioMixer.GetFloat throws in this game (see MixerFloats); the game's own SetFloat
            // writes are the source. Before the first write the mixer asset's nominal 0 dB is
            // assumed and said once; the first write is reported once too.
            var masterWetWritten = MixerFloats.TryGetMasterWet(out var masterWetDb);
            if (masterWetWritten != _masterWetWritten)
            {
                _masterWetWritten = masterWetWritten;
                _log.LogInfo(masterWetWritten
                    ? $"Environment reverb: MasterWet written by the game: {masterWetDb:F1} dB ({MixerFloats.MasterWetWrites} writes so far)."
                    : "Environment reverb: the game has not written MasterWet yet; assuming 0 dB until it does.");
            }
            var voiceBusDb = 20f * Mathf.Log10(Mathf.Max(gae.VoiceNormalVol * gae.VoiceAudioSettingsVol, 1e-4f));
            EnvironmentReverbSnapshot snapshot;
            var adr = am.AudioDynamicReverb;
            if (adr is not null)
            {
                var p = adr.Bypass
                    ? EnvironmentReverbParams.Bypassed with { MasterWetDb = masterWetDb, VoiceBusDb = voiceBusDb }
                    : new EnvironmentReverbParams(adr.DSP_DryLevel, adr.DSP_Room, adr.DSP_RoomHF, adr.DSP_RoomLF, adr.DSP_DecayTime, adr.DSP_DecayHFRatio,
                        adr.DSP_Reflections, adr.DSP_ReflectDelay, adr.DSP_Reverb, adr.DSP_ReverbDelay, adr.DSP_HFReference, adr.DSP_LFReference,
                        adr.DSP_Diffusion, adr.DSP_Density, masterWetDb, voiceBusDb);
                snapshot = new EnvironmentReverbSnapshot(p, true, adr.RoomSize, adr.Outdoorness, adr.ReverbTime, adr.Diffusion);
            }
            else
            {
                var abr = am.AudioBasicReverb;
                if (abr is null)
                {
                    _envReadable = false;
                    return;
                }
                if (!_reportedBasicMode)
                {
                    _reportedBasicMode = true;
                    _log.LogInfo("Environment reverb: the game is in Basic reverb mode; reading AudioBasicReverb.");
                }
                var p = abr.Bypass
                    ? EnvironmentReverbParams.Bypassed with { MasterWetDb = masterWetDb, VoiceBusDb = voiceBusDb }
                    : new EnvironmentReverbParams(abr.DryLevel, abr.Room, abr.RoomHF, abr.RoomLF, abr.DecayTime, abr.DecayHFRatio,
                        abr.Reflections, abr.ReflectDelay, abr.Reverb, abr.ReverbDelay, abr.HFReference, abr.LFReference,
                        abr.Diffusion, abr.Density, masterWetDb, voiceBusDb);
                snapshot = new EnvironmentReverbSnapshot(p, false, 0f, 0f, 0f, 0f);
            }
            _envSnapshot = snapshot;
            _envReadable = true;
            if (Time.unscaledTime >= _nextEnvLog)
            {
                _nextEnvLog = Time.unscaledTime + StatsIntervalSeconds;
                var q = snapshot.Params;
                _log.LogInfo($"Environment reverb: {(snapshot.Dynamic ? $"RS {snapshot.RoomSize:F2} O {snapshot.Outdoorness:F2} RT {snapshot.ReverbTime:F2} D {snapshot.Diffusion:F2}" : "basic mode")}; " +
                             $"DryLevel {q.DryLevelMb:F0} Room {q.RoomMb:F0} RoomHF {q.RoomHfMb:F0} RoomLF {q.RoomLfMb:F0} mB, Decay {q.DecayTimeS:F2} s x{q.DecayHfRatio:F2}, " +
                             $"Reflections {q.ReflectionsMb:F0} mB @{q.ReflectDelayS * 1000f:F0} ms, Reverb {q.ReverbMb:F0} mB @{q.ReverbDelayS * 1000f:F0} ms, " +
                             $"HF {q.HfReferenceHz:F0} LF {q.LfReferenceHz:F0} Hz, Diffusion {q.DiffusionPct:F0} Density {q.DensityPct:F0}; MasterWet {q.MasterWetDb:F1} dB ({(masterWetWritten ? $"{MixerFloats.MasterWetWrites} writes" : "assumed")}), voice {q.VoiceBusDb:F1} dB.");
            }
        }
        catch (Exception e)
        {
            _envReadable = false;
            if (_reportedEnvError) return;
            _reportedEnvError = true;
            _log.LogWarning($"Environment reverb: inputs unreadable, stage bypassed: {e.Message}");
        }
    }

    // [impl->REQ-RENDER-MEGAPHONE]
    /// <summary>
    /// Main thread, once per frame: is the local player broadcasting through a held megaphone? The
    /// game's own state (docs/reference/big-walk-local-voice-wiring.md section 4): the held prop's
    /// <c>RadioVoiceAssigner</c> whose prefab <c>VoicePlayer</c> is of the Megaphone type, with
    /// <c>isBroadcasting</c> set by the networked peck and cleared once Dissonance stops reporting
    /// speech into its room, and <c>latestBroadcastPlayer</c> being us. The game hard-switches its
    /// megaphone player on the same edges, so the Sink follows within a frame. Unreadable state
    /// means no megaphone voice, logged once.
    /// </summary>
    private void UpdateMegaphone()
    {
        if (!_megaphoneToggles.Master) return;
        var held = false;
        var active = false;
        var path = "none";
        try
        {
            var me = WorldManager.localPlayerCharacter;
            var rva = me?.hands?.heldProp?.radioVoiceAssigner;
            if (rva is not null && rva._cachedVoiceType == VoicePlayer.VoicePlayerType.Megaphone)
            {
                held = true;
                var broadcaster = rva.latestBroadcastPlayer;
                active = rva.isBroadcasting && broadcaster is not null && broadcaster.Pointer == me.Pointer;
                path = "prop";
            }
            // Fallback (run 4: the held-prop chain read nothing while the operator used one): the
            // wiring doc says broadcasting manifests locally as the Megaphone token room on
            // DissonanceComms, which the transmit signal already lists as transmitting.
            if (!active && _megaphoneRoomOpen)
            {
                held = true;
                active = true;
                path = "room";
                if (Time.unscaledTime >= _nextMegaphoneProbe && _megaphoneProbes < 10)
                {
                    _nextMegaphoneProbe = Time.unscaledTime + 5f;
                    _megaphoneProbes++;
                    var hands = me?.hands;
                    var prop = hands?.heldProp;
                    _log.LogInfo($"Megaphone: probe #{_megaphoneProbes}: room open but the held-prop chain says no: me {(me is null ? "null" : "ok")}, hands {(hands is null ? "null" : "ok")}, heldProp {(prop is null ? "null" : $"'{prop.name}'")}, radioVoiceAssigner {(rva is null ? "null" : $"type {rva._cachedVoiceType}, broadcasting {rva.isBroadcasting}, latest {(rva.latestBroadcastPlayer is null ? "null" : rva.latestBroadcastPlayer.Pointer == me.Pointer ? "me" : "other")}")}.");
                }
            }
        }
        catch (Exception e)
        {
            if (!_reportedMegaphoneError)
            {
                _reportedMegaphoneError = true;
                _log.LogWarning($"Megaphone: state unreadable, megaphone voice off: {e.Message}");
            }
            held = false;
            active = false;
        }
        _megaphoneHeld = held;
        _megaphoneActive = active;
        _megaphonePath = path;
        if (held != _lastMegaphoneHeld)
        {
            _lastMegaphoneHeld = held;
            if (_megaphoneBroadcasts < 20) _log.LogInfo($"Megaphone: {(held ? "picked up" : "put down")}.");
        }
        if (active != _lastMegaphoneActive)
        {
            _lastMegaphoneActive = active;
            if (active) _megaphoneBroadcasts++;
            if (_megaphoneBroadcasts <= 20) _log.LogInfo($"Megaphone: broadcast {(active ? "started" : "ended")} (#{_megaphoneBroadcasts}); Sink {(active ? $"switches to {_megaphoneMix}" : "back to the direct voice")}.");
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
        TapFilter.SetReader(_player, ring?.Length ?? 0);
        _log.LogInfo($"Local Voice: renderer built; provider ring {ring?.Length ?? 0} samples x {_ringChannels} ch, controller {(_player.Controller is null ? "pending" : "live")}; read head margin {ReadHeadMarginFrames} frames.");
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
        RefreshEq(target == IntPtr.Zero ? null : controller);
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

        // Both our object and the pooled source become children of the anchor at the same local
        // offset, and the controller keeps following our object. Its per-frame write
        // (source.position = follow.position) then resolves to exactly that local offset whatever
        // frame's anchor pose it read, so the audio thread always sees anchor(now) * offset:
        // no one-frame lag. Clearing the follow target instead (run 4) made the controller fall
        // back to the fixed world position it was played at, the world origin.
        _pinnedSource = sourceTransform;
        _pinnedSourceOriginalParent = sourceTransform.parent;
        _voiceObject.transform.SetParent(_anchor, false);
        _voiceObject.transform.localPosition = new Vector3(0f, 0f, _forwardMeters);
        _voiceObject.transform.localRotation = Quaternion.identity;
        sourceTransform.SetParent(_anchor, false);
        sourceTransform.localPosition = _voiceObject.transform.localPosition;
        sourceTransform.localRotation = Quaternion.identity;
        controller.FollowTransform = _voiceObject.transform;
        _log.LogInfo($"Local Voice: emitter pinned to '{_anchor.name}', {_forwardMeters * 100f:F1} cm forward along its view axis; anchor forward {_anchor.forward}.");
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
            if (camera is not null)
            {
                _log.LogInfo($"Local Voice: anchor = main camera '{camera.name}'.");
                return camera.transform;
            }
            var listener = AudioManager.Instance?.ListenerController?._listener;
            if (listener is not null)
            {
                // No camera tagged MainCamera in this game (run 4). If the listener hangs under a
                // camera, anchor to that camera so the offset follows the view rotation.
                var parentCamera = listener.GetComponentInParent<Camera>();
                if (parentCamera is not null)
                {
                    _log.LogInfo($"Local Voice: anchor = camera '{parentCamera.name}' above listener '{listener.name}'.");
                    return parentCamera.transform;
                }
                _log.LogInfo($"Local Voice: anchor = listener '{listener.name}' (parent '{listener.transform.parent?.name}').");
                return listener.transform;
            }
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
    private void OnFrameEncoded(int sequence, byte[] opus, long capturedAt)
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

            // [impl->REQ-VOICE-CONTINUOUS]
            var decision = _gateEnabled ? _gate.Decide(_transmitting, now * 1000.0 / Stopwatch.Frequency) : GateDecision.Pass;
            var capture = CalibrationCapture.Instance;
            if (capture is not null && _feed == SinkFeedPoint.Encoder)
            {
                var n = Math.Min(count, _captureScratch.Length);
                _decoder.CopyTo(_captureScratch, n);
                capture.Input(_captureScratch.AsSpan(0, n), capturedAt, decision == GateDecision.Pass ? "pass" : _tailSamplesLeft > 0 ? "tail" : "silence");
            }
            // [impl->REQ-OFFSET-MEASURE]
            if (decision == GateDecision.Pass)
            {
                // [impl->REQ-RENDER-CLEAN]
                if (gapMs > BurstGapMs)
                {
                    _dynamics.Reset(_makeupGain); // the game's session-change reset
                    _eqCore?.Reset();
                    if (gapMs > 2000) { _mixerStage?.Reset(); _env?.Reset(); } // a long gap: drop the reverb tails too
                }
                ProcessRemotePath(count);
                if (_feed == SinkFeedPoint.Encoder)
                {
                    // ADR-0005: the processed frame is the Sink's input; no provider, no Tap.
                    var frame = _scratch.AsSpan(0, Math.Min(count, _scratch.Length));
                    _eqCore?.Process(frame);
                    // [impl->REQ-MIXER-RESYNTH]
                    if (_mixerStage is not null && _mixerInputsReadable)
                        _mixerStage.Process(frame, _mixerDryDb, _mixerHighDb, _mixerFallDb, _mixerBoostDb, _mixerToggles);
                    // [impl->REQ-RENDER-MEGAPHONE]
                    // The megaphone's output is what its own player renders from the same processed
                    // voice; a listener beside the holder hears it on top of the direct voice.
                    if (_megaphone is not null && _megaphoneActive)
                    {
                        if (gapMs > BurstGapMs || _megaphoneFrames == 0) _megaphone.Reset();
                        var mega = _megaphoneScratch.AsSpan(0, frame.Length);
                        _megaphone.Process(frame, mega, _megaphoneToggles);
                        if (_megaphoneMix == MegaphoneMix.Replace) mega.CopyTo(frame);
                        else for (var i = 0; i < frame.Length; i++) frame[i] = Math.Clamp(frame[i] + mega[i], -1f, 1f);
                        _megaphoneFrames++;
                    }
                    else _megaphoneFrames = 0;
                    // [impl->REQ-MIXER-RESYNTH]
                    // The listener's environment reverb, last: the Clean and Megaphone voices enter it together.
                    var env = _envSnapshot;
                    if (_env is not null && _envReadable && env is not null)
                        _env.Process(frame, env.Params, _envToggles);
                    if (_outputTrim != 1f) for (var i = 0; i < frame.Length; i++) frame[i] = Math.Clamp(frame[i] * _outputTrim, -1f, 1f);
                    SinkFeed.Write(frame, capturedAt);
                    capture?.Output(frame);
                    _tailSamplesLeft = (int)(TailSeconds * LocalVoiceDecoder.SampleRate);
                }
                else RoundTripProvider.Push(pcm, count, _ringChannels, capturedAt);
                _lastPushWasVoice = true;
            }
            else
            {
                if (_feed == SinkFeedPoint.Encoder)
                {
                    // [impl->REQ-MIXER-RESYNTH]
                    // The gate silences the voice, not the room: on a listener's machine the mixer
                    // reverbs keep ringing after the channel closes, so the tails are rendered
                    // through the silence (run 4: a 1 s hallway tail cut at the gate sounded like
                    // the 0.24 s big room, and the cut itself was audible).
                    if (_tailSamplesLeft > 0 && RenderTail(count)) _tailSamplesLeft -= count;
                    else { SinkFeed.WriteSilence(count); capture?.OutputSilence(count); }
                }
                else RoundTripProvider.Push(GateSilence(count), count, _ringChannels, FrameStampTable.NoStamp);
                _lastPushWasVoice = false;
            }
            if (_feed == SinkFeedPoint.VoicePlayer && gapMs > BurstGapMs) ResyncReadHead(count, "burst start");

            if (first) _log.LogInfo($"Local Voice: frame 1 step C, pushed; provider write head {RoundTripProvider.Provider?.CachedVoiceWriteHead}.");
            Interlocked.Increment(ref _framesDecoded);
        }
        catch (Exception e)
        {
            if (Interlocked.Increment(ref _decodeErrors) <= 3)
                _log.LogError($"Local Voice: decode/push failed: {e}");
        }
    }

    /// <summary>Encoder thread: a zero frame of the decoded frame's length, reused across calls.</summary>
    private Il2CppSystem.ArraySegment<float> GateSilence(int samples)
    {
        if (_gateSilenceSamples != samples)
        {
            _gateSilence = new Il2CppSystem.ArraySegment<float>(new Il2CppStructArray<float>(samples));
            _gateSilenceSamples = samples;
        }
        return _gateSilence;
    }

    // [impl->REQ-RENDER-CLEAN]
    /// <summary>
    /// Encoder thread: the remote path's per-sample processing (gain ramp, compressor, soft clip)
    /// over the decoded frame, in place, and the block's input level for the makeup-gain loop.
    /// </summary>
    private void ProcessRemotePath(int count)
    {
        if (count > _scratch.Length) count = _scratch.Length;
        _decoder.CopyTo(_scratch, count);
        // The channel fade a peer hears on the game's voice-activation channel, ahead of their
        // playback processing (the channel volume is a property of the stream they receive).
        if (_gateEnabled) _fader.Apply(_scratch.AsSpan(0, count), _transmitting, LocalVoiceDecoder.SampleRate);
        _dynamics.Process(_scratch.AsSpan(0, count), _makeupGain, _threshold, LocalVoiceDecoder.SampleRate);
        _decoder.CopyFrom(_scratch, count);
        _lastArv = _dynamics.Arv;
    }

    /// <summary>
    /// Runs one silent frame through the listener-side reverbs and writes the tail to the Sink.
    /// False (nothing written) when no reverb stage is live, so the caller writes plain silence.
    /// </summary>
    private bool RenderTail(int count)
    {
        var mixer = _mixerStage is not null && _mixerInputsReadable && _mixerToggles.Master;
        var env = _envSnapshot;
        var environment = _env is not null && _envReadable && env is not null && _envToggles.Master;
        if (!mixer && !environment) return false;
        var frame = _scratch.AsSpan(0, Math.Min(count, _scratch.Length));
        frame.Clear();
        if (mixer) _mixerStage.Process(frame, _mixerDryDb, _mixerHighDb, _mixerFallDb, _mixerBoostDb, _mixerToggles);
        if (environment) _env.Process(frame, env.Params, _envToggles);
        if (_outputTrim != 1f) for (var i = 0; i < frame.Length; i++) frame[i] = Math.Clamp(frame[i] * _outputTrim, -1f, 1f);
        SinkFeed.Write(frame, FrameStampTable.NoStamp);
        CalibrationCapture.Instance?.Output(frame);
        _tailFrames++;
        return true;
    }

    // [impl->REQ-RENDER-CLEAN]
    /// <summary>
    /// Main thread, once per frame, as <c>PlayerVoicePlaybackControl.Update</c> does for a remote
    /// voice: feeds the last block's level to the makeup-gain loop and publishes the gain and the
    /// compressor threshold for the next block. "Speaking" is a talk burst in progress that the
    /// gate let through. The game's <c>TargetARV</c> and compressor threshold are read (never
    /// written) so the in-game voice volume slider still couples; if they cannot be read the loop
    /// runs at the reference level.
    /// </summary>
    private void UpdateMakeupGain()
    {
        var sinceMs = (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastPushTimestamp)) * 1000.0 / Stopwatch.Frequency;
        var speaking = _lastPushWasVoice && sinceMs <= BurstGapMs;
        float targetArv, threshold;
        try
        {
            targetArv = VoiceMakeupGain.TargetARV;
            threshold = VoiceCompressor.Threshold;
        }
        catch (Exception e)
        {
            if (!_reportedMakeupError)
            {
                _reportedMakeupError = true;
                _log.LogWarning($"Local Voice: game makeup-gain statics unreadable, using the reference level: {e.Message}");
            }
            targetArv = Core.VoiceMakeupGain.ReferenceArv;
            threshold = Core.VoiceMakeupGain.ThresholdFor(targetArv);
        }
        _makeupGain = _makeup.Evaluate(_lastArv, speaking, Time.deltaTime, targetArv);
        _threshold = threshold;
    }

    // [impl->REQ-EAR-SELF]
    /// <summary>
    /// The game's per-voice EQ (<c>PlayVoice</c>: PeakingEQ 400 Hz, Q 0.3, +30 dB, Vol 0.03) on
    /// the pooled source our VoicePlayer plays through, with its wet mix pinned to config
    /// <c>SelfEarEqDryWet</c> instead of the distance/angle curves, which the Self-Ear evaluates at
    /// their left edge. At 0 the filter is an exact passthrough, so none is attached: the pooled
    /// source belongs to the game, and a component left on it would follow it to its next owner.
    /// Whatever was attached is removed when the controller changes.
    /// </summary>
    private void RefreshEq(AudioSourceController controller)
    {
        if (_eq is not null)
        {
            try
            {
                _eqController?.RemoveFilter(_eq.Cast<IAudioFilter>());
                Object.Destroy(_eq);
            }
            catch (Exception e)
            {
                _log.LogWarning($"Local Voice: EQ removal failed: {e.Message}");
            }
            _eq = null;
            _eqController = null;
        }
        if (_eqDryWet <= 0f || controller is null) return;

        try
        {
            var eq = controller.gameObject.AddComponent<BiquadFilters>();
            eq.Type = BiquadFilters.FilterType.PeakingEQ;
            eq.Frequency = 400f;
            eq.Q = 0.3f;
            eq.Gain = 30f;
            eq.Vol = 0.03f;
            eq.DryWet = _eqDryWet;
            eq._dirty = true;
            var index = controller.FilterMixer?.Filters?.Count ?? 1;
            controller.AddFilter(eq.Cast<IAudioFilter>(), index);
            _eq = eq;
            _eqController = controller;
            _log.LogInfo($"Local Voice: voice EQ attached at filter index {index} (PeakingEQ 400 Hz, dry/wet {_eqDryWet:F2}).");
        }
        catch (Exception e)
        {
            _log.LogWarning($"Local Voice: voice EQ attach failed, Self-Ear stays dry: {e.Message}");
        }
    }

    // [impl->REQ-VOICE-CONTINUOUS]
    /// <summary>
    /// Main thread: reads the "peers receive" signal into the flag the encoder thread gates on,
    /// and logs the signal's parts once per state change (the first 40 changes, then every 50th:
    /// the VAD flips per phrase). A signal that cannot be read fails open.
    /// </summary>
    private void SampleTransmitSignal()
    {
        var sample = TransmitSignal.Read(out var error);
        _transmitting = !sample.Available || sample.PeersReceive;
        _megaphoneRoomOpen = sample.Available && sample.TriggersText != null && sample.TriggersText.Contains("Megaphone");
        if (_gateEnabled && !_fadeConfigured && sample.Available) ConfigureFade();
        if (error is not null && !_reportedSignalError)
        {
            _reportedSignalError = true;
            _log.LogWarning($"Transmit signal: read failed, gate stays open: {error}");
        }

        var state = sample.Describe();
        if (state == _lastSignalState) return;
        _lastSignalState = state;
        var n = ++_signalChanges;
        if (n <= 40 || n % 50 == 0)
            _log.LogInfo($"Transmit signal #{n}: {state}");
    }

    /// <summary>
    /// Main thread, once the game's voice-activation triggers exist: takes their channel fade so
    /// the gate opens and closes the way a peer hears it (config <c>Fidelity.TransmitFadeOutMs</c>
    /// overrides the fade-out), and grows the gate's hold to cover the fade-out so no frame is
    /// silenced mid-fade. An unreadable fade keeps the hard gate and is logged once.
    /// </summary>
    private void ConfigureFade()
    {
        try
        {
            if (!TransmitSignal.TryReadFade(out var fadeIn, out var fadeOut, out var source)) return;
            var gameFadeOut = fadeOut;
            if (_fadeOutOverrideMs > 0) fadeOut = _fadeOutOverrideMs;
            _fader.Set(fadeIn, fadeOut);
            _gate.SetReleaseHold(_holdOverrideMs > 0 ? _holdOverrideMs : Math.Max(TransmitGate.DefaultReleaseHoldMs, fadeOut + 60));
            _fadeConfigured = true;
            _log.LogInfo($"Transmit fade: in {fadeIn:F0} ms, out {fadeOut:F0} ms (the game's '{source}' trigger fades out over {gameFadeOut:F0} ms); gate hold {_gate.ReleaseHoldMs:F0} ms{(_holdOverrideMs > 0 ? " (Fidelity.TransmitHoldMs)" : "")}; output trim {_outputTrimDb:F1} dB.");
        }
        catch (Exception e)
        {
            _fadeConfigured = true;
            if (_reportedFadeError) return;
            _reportedFadeError = true;
            _log.LogWarning($"Transmit fade: unreadable, the gate stays hard: {e.Message}");
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
            RoundTripProvider.Push(_silence, frameSamples, _ringChannels, FrameStampTable.NoStamp);
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
        string path;
        if (_feed == SinkFeedPoint.Encoder)
        {
            path = $"encoder feed: blocks {Interlocked.Read(ref SinkFeed.Blocks)} voice + {Interlocked.Read(ref SinkFeed.SilenceBlocks)} silence (peak {SinkFeed.LastPeak:F3}), " +
                   $"ring {SinkFeed.Ring.Count} smp, dropped {SinkFeed.Ring.DroppedSamples}, underruns {SinkFeed.Ring.Underruns}";
        }
        else
        {
            var provider = RoundTripProvider.Provider;
            var lag = ProviderLagSamples();
            var lagMs = SampleRate > 0 && _ringChannels > 0 ? lag * 1000.0 / (SampleRate * _ringChannels) : 0;
            path = $"pushed {Interlocked.Read(ref RoundTripProvider.FramesPushed)}; " +
                   $"provider write head {provider?.CachedVoiceWriteHead ?? -1}, lag {lag} smp ({lagMs:F0} ms), resyncs {Interlocked.Read(ref _resyncs)}, silence frames {_silenceFrames}; " +
                   $"tap blocks {Interlocked.Read(ref TapFilter.Blocks)} ({TapFilter.Channels} ch x {TapFilter.BlockLength}, peak {TapFilter.LastPeak:F3}), " +
                   $"ring {TapFilter.Ring.Count} smp, dropped {TapFilter.Ring.DroppedSamples}, underruns {TapFilter.Ring.Underruns}";
        }
        var eq = _feed == SinkFeedPoint.Encoder
            ? (_eqCore is null ? "off" : $"wet {_eqDryWet:F2} (Core)")
            : (_eq is null ? "off" : $"wet {_eqDryWet:F2}");
        if (_feed == SinkFeedPoint.Encoder && _mixerStage is not null)
        {
            path += _mixerToggles.Master
                ? $"; mixer stage: {(_mixerInputsReadable ? "live" : "bypassed (inputs unreadable)")}, dry {_mixerDryDb:F1} dB, high {_mixerHighDb:F1} dB, fall {_mixerFallDb:F1} dB, boost {_mixerBoostDb:F1} dB, falls {_fallEvents}, frames {_mixerStage.Frames}"
                : "; mixer stage: off";
            path += _megaphoneToggles.Master
                ? $"; megaphone: {(_megaphoneActive ? "broadcasting" : _megaphoneHeld ? "held" : "none")}, broadcasts {_megaphoneBroadcasts}, frames {_megaphone?.Frames ?? 0}, via {_megaphonePath}, mix {_megaphoneMix}, reduction {_megaphone?.CompressorReductionDb ?? 0:F1}/{_megaphone?.PostCompressorReductionDb ?? 0:F1} dB"
                : "; megaphone: off";
            path += _envToggles.Master
                ? $"; environment reverb: {(_envReadable ? "live" : "bypassed (inputs unreadable)")}, room {_env?.Params.RoomMb ?? 0:F0} mB, decay {_env?.Params.DecayTimeS ?? 0:F2} s, dry {_env?.DryGain ?? 1:F2} (copy {_env?.DryCopyGain ?? 0:F2}), return {_env?.ReturnGain ?? 1:F2}, MasterWet {(MixerFloats.TryGetMasterWet(out var mw) ? $"{mw:F1} dB" : "assumed 0 dB")}, frames {_env?.Frames ?? 0}, tail frames {_tailFrames}, mixer floats seen {MixerFloats.DistinctNames}"
                : "; environment reverb: off";
            if (CalibrationCapture.Instance is { } capture) path += "; " + capture.Status;
        }
        _log.LogInfo(
            $"Local Voice stats: encoded {OutboundVoiceTap.Frames}, decoded {Interlocked.Read(ref _framesDecoded)} ({_lastDecodedSamples} smp), errors {Interlocked.Read(ref _decodeErrors)}; " +
            $"{path}; " +
            $"sink {(_pump.Connected ? "connected" : "waiting")}, frames sent {Interlocked.Read(ref _pump.FramesSent)}; " +
            $"gate {(_gateEnabled ? "on" : "off")}: transmitting {_transmitting}, passed {_gate.FramesPassed}, silenced {_gate.FramesSilenced}, signal changes {_signalChanges}, fade {_fader.FadeInMs:F0}/{_fader.FadeOutMs:F0} ms, hold {_gate.ReleaseHoldMs:F0} ms; " +
            $"remote path: makeup {_makeupGain:F2} ({_makeup.GainDb:F1} dB, level {_makeup.Level:F3}), arv {_lastArv:F3}, reduction {_dynamics.Reduction:F2}, pre-clip peak {_dynamics.PreClipPeak:F2}, threshold {_threshold:F3}, eq {eq}.");
        if (_feed == SinkFeedPoint.VoicePlayer) LogGameAudioState();
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
                  $"group='{source.outputAudioMixerGroup?.name}' clip='{source.clip?.name}' local={source.transform.localPosition} parent='{source.transform.parent?.name}' " +
                  $"anchorFwd={(_anchor is null ? Vector3.zero : _anchor.forward)} follow='{_player?.Controller?.FollowTransform?.name}'";
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
            OffsetRow.Instance?.Tick();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Local Voice: renderer tick failed: {e}");
        }
    }
}
